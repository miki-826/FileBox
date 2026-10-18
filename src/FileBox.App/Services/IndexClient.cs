using System.Diagnostics;
using System.IO.Pipes;
using FileBox.Contracts;

namespace FileBox.App.Services;

/// <summary>
/// Indexer との IPC クライアント。接続できなければ同梱の Indexer を起動する。
/// 応答は Id で突き合わせ、古い検索の応答は捨てる（入力ごとの再検索）。
/// </summary>
public sealed class IndexClient : IDisposable
{
    NamedPipeClientStream? _pipe;
    readonly SemaphoreSlim _connectLock = new(1, 1);
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Dictionary<long, TaskCompletionSource<IndexResponse>> _pending = new();
    long _nextId;
    Process? _launched;

    public static IndexClient Instance { get; } = new();

    string IndexerPath => Path.Combine(AppContext.BaseDirectory, "indexer", "FileBox.Indexer.exe");

    public async Task<SearchResult?> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var response = await SendAsync(new IndexRequest { Type = IndexRequestType.Search, Query = query, Limit = limit }, ct);
        if (response?.Error != null && response.Search == null) throw new InvalidOperationException(response.Error);
        if (response?.Search != null && response.Error != null) LastQueryError = response.Error;
        else LastQueryError = null;
        return response?.Search;
    }

    /// <summary>権限・容量・監査・管理の操作を呼ぶ。拒否や失敗は InvalidOperationException（メッセージは利用者向け）。</summary>
    public async Task<T?> CallAsync<T>(string method, object? args = null, CancellationToken ct = default)
    {
        var response = await SendAsync(new IndexRequest { Type = IndexRequestType.Call, Method = method, Args = PolicyMethods.ToNode(args) }, ct);
        if (response == null) throw new InvalidOperationException("FileBox のバックグラウンド処理（Indexer）に接続できません");
        if (response.Error != null) throw new InvalidOperationException(response.Error);
        return PolicyMethods.FromNode<T>(response.Result);
    }

    /// <summary>保存先提案の候補フォルダ。Indexer に繋がらなければ null。</summary>
    public async Task<List<SuggestFolder>?> SuggestAsync(IReadOnlyList<string> terms, CancellationToken ct)
    {
        var response = await SendAsync(new IndexRequest { Type = IndexRequestType.Suggest, Terms = terms.ToList() }, ct);
        return response?.Suggest;
    }

    /// <summary>直近の検索でクエリ構文エラーがあった場合のメッセージ。</summary>
    public string? LastQueryError { get; private set; }

    public async Task<IndexStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await SendAsync(new IndexRequest { Type = IndexRequestType.Status }, ct);
            return response?.Status;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Indexer を停止し、管理者権限（MFT 方式）で起動し直す。UAC の確認が出る。</summary>
    public async Task<bool> RestartElevatedAsync()
    {
        try
        {
            if (await EnsureConnectedAsync(CancellationToken.None, launch: false))
            {
                await WriteAsync(new IndexRequest { Type = IndexRequestType.Shutdown }, CancellationToken.None);
                await Task.Delay(1500);
            }
        }
        catch (Exception) { }
        Disconnect();
        try
        {
            _launched = Process.Start(new ProcessStartInfo(IndexerPath, $"--parent {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC で拒否された場合は通常権限で起動し直す
            await EnsureConnectedAsync(CancellationToken.None, launch: true);
            return false;
        }
    }

    async Task<IndexResponse?> SendAsync(IndexRequest request, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct, launch: true)) return null;
        request.Id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IndexResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[request.Id] = tcs;
        try
        {
            await WriteAsync(request, ct);
            using (ct.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (IOException)
        {
            Disconnect();
            return null;
        }
        finally
        {
            lock (_pending) _pending.Remove(request.Id);
        }
    }

    async Task WriteAsync(IndexRequest request, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await IndexPipe.WriteAsync(_pipe!, request, ct); }
        finally { _writeLock.Release(); }
    }

    async Task<bool> EnsureConnectedAsync(CancellationToken ct, bool launch)
    {
        if (_pipe is { IsConnected: true }) return true;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_pipe is { IsConnected: true }) return true;
            if (await TryConnectAsync(300, ct)) return true;
            if (!launch) return false;
            if (_launched == null || _launched.HasExited)
            {
                if (!File.Exists(IndexerPath)) return false;
                _launched = Process.Start(new ProcessStartInfo(IndexerPath, $"--parent {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            for (int i = 0; i < 20; i++)
            {
                if (await TryConnectAsync(250, ct)) return true;
            }
            return false;
        }
        finally { _connectLock.Release(); }
    }

    async Task<bool> TryConnectAsync(int timeoutMs, CancellationToken ct)
    {
        // 識別レベルの偽装を許可: Indexer がこちらの Windows アカウントを確認できる（権限判定用）。なりすましはできないレベル
        var pipe = new NamedPipeClientStream(".", IndexPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync();
            return false;
        }
        _pipe = pipe;
        _ = Task.Run(() => ReadLoopAsync(pipe));
        return true;
    }

    async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            while (pipe.IsConnected)
            {
                var response = await IndexPipe.ReadAsync<IndexResponse>(pipe);
                if (response == null) break;
                TaskCompletionSource<IndexResponse>? tcs;
                lock (_pending) _pending.TryGetValue(response.Id, out tcs);
                tcs?.TrySetResult(response);
            }
        }
        catch (Exception) { }
        if (ReferenceEquals(_pipe, pipe)) Disconnect();
    }

    void Disconnect()
    {
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose();
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }

    public void Dispose() => Disconnect();
}
