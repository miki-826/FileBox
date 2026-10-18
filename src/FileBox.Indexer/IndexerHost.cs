using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FileBox.Contracts;
using FileBox.Core.Search;
using FileBox.Indexer.Index;
using FileBox.Indexer.Policy;
using FileBox.Indexer.Volumes;

namespace FileBox.Indexer;

/// <summary>インデックス全体（全ボリューム）と IPC サーバをまとめる。</summary>
internal sealed class IndexerHost : IAsyncDisposable
{
    readonly FileIndex _index = new();
    readonly List<IVolumeSource> _volumes = new();
    readonly CancellationTokenSource _cts = new();
    readonly bool _elevated;
    readonly List<Task> _tasks = new();
    readonly PolicyHost _policy;

    public IndexerHost()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
        _policy = new PolicyHost(_index, _elevated);
    }

    public CancellationToken Stopping => _cts.Token;

    public void Start()
    {
        Log.Info($"Indexer 起動 (elevated={_elevated}, pid={Environment.ProcessId})");
        byte vol = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
            IVolumeSource? source = null;
            if (drive.DriveFormat == "NTFS")
                source = NtfsVolumeSource.TryCreate(_index, vol, drive.RootDirectory.FullName);
            source ??= new ScanVolumeSource(_index, vol, drive.RootDirectory.FullName);
            _volumes.Add(source);
            var s = source;
            _tasks.Add(Task.Run(async () =>
            {
                try { await s.RunAsync(_cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error($"{s.Status.Root}: インデックス処理が停止しました", ex); }
            }));
            vol++;
        }
        _tasks.Add(Task.Run(() => ServePipeAsync(_cts.Token)));
        _tasks.Add(Task.Run(() => SnapshotLoopAsync(_cts.Token)));
    }

    public void RequestStop() => _cts.Cancel();

    async Task SnapshotLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                foreach (var v in _volumes) v.SaveSnapshot();
            }
        }
        catch (OperationCanceledException) { }
    }

    public IndexStatus GetStatus()
    {
        var status = new IndexStatus { EntryCount = _index.LiveCount, Elevated = _elevated };
        foreach (var v in _volumes)
        {
            if (v.Status.Ready) v.Status.Entries = _index.CountVolume((byte)_volumes.IndexOf(v));
            status.Volumes.Add(v.Status);
        }
        status.State = status.Volumes.All(v => v.Ready) ? IndexState.Ready
            : status.EntryCount > 0 ? IndexState.Scanning : IndexState.Starting;
        return status;
    }

    public SearchResult Search(string? query, int limit, CancellationToken ct, out IReadOnlyList<string> errors)
    {
        var compiled = CompiledQuery.Compile(query);
        errors = compiled.Errors;
        if (compiled.Source.IsEmpty) return new SearchResult();
        return _index.Search(compiled, Math.Clamp(limit, 1, 10000), ct);
    }

    // ---------------------------------------------------------------- IPC

    static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        // 対話ユーザー（認証済みユーザー）と SYSTEM のみ接続可（05_非機能要件 §2）
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    async Task ServePipeAsync(CancellationToken ct)
    {
        var security = CreatePipeSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(IndexPipe.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            }
            catch (Exception ex)
            {
                Log.Error("パイプを作成できません（他の Indexer が起動中の可能性）", ex);
                _cts.Cancel();
                return;
            }
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            _ = Task.Run(() => HandleClientAsync(server, ct));
        }
    }

    async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        CancellationTokenSource? currentSearch = null;
        Caller? caller = null;
        await using var connection = pipe;

        // 相手の Windows アカウントは、最初の要求を受け取った後にパイプの偽装（識別レベル）で OS から得る
        Caller GetCaller()
        {
            if (caller != null) return caller;
            try { caller = Caller.FromPipe(pipe); }
            catch (Exception ex) { Log.Error("接続元のアカウントを確認できません", ex); }
            // 確認できなければ匿名扱い（管理操作は不可）。自分（Indexer）の資格で代用しない
            caller ??= new Caller("", "(不明)", false);
            Log.Info($"接続元: {caller.Account} sid={caller.Sid} windowsAdmin={caller.IsWindowsAdmin}");
            return caller;
        }
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var req = await IndexPipe.ReadAsync<IndexRequest>(pipe, ct);
                if (req == null) break;
                switch (req.Type)
                {
                    case IndexRequestType.Status:
                        await SendAsync(new IndexResponse { Id = req.Id, Status = GetStatus() });
                        break;
                    case IndexRequestType.Shutdown:
                        Log.Info("クライアントから停止要求を受信");
                        foreach (var v in _volumes) v.SaveSnapshot();
                        _cts.Cancel();
                        return;
                    case IndexRequestType.Search:
                        // 新しい検索が来たら前の検索はキャンセル（入力ごとの再検索に対応）
                        currentSearch?.Cancel();
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        currentSearch = cts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var result = Search(req.Query, req.Limit, cts.Token, out var compiledErrors);
                                await SendAsync(new IndexResponse
                                {
                                    Id = req.Id,
                                    Search = result,
                                    Error = compiledErrors.Count > 0 ? string.Join(" / ", compiledErrors) : null,
                                });
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                Log.Error("検索失敗", ex);
                                await SendAsync(new IndexResponse { Id = req.Id, Error = ex.Message });
                            }
                        });
                        break;
                    case IndexRequestType.Call:
                    {
                        var who = GetCaller();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var result = _policy.Handle(who, req.Method ?? "", req.Args);
                                await SendAsync(new IndexResponse { Id = req.Id, Result = result });
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                            {
                                await SendAsync(new IndexResponse { Id = req.Id, Error = ex.Message });
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"{req.Method} の処理に失敗", ex);
                                await SendAsync(new IndexResponse { Id = req.Id, Error = ex.Message });
                            }
                        });
                        break;
                    }
                    case IndexRequestType.Suggest:
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var folders = _index.FindSuggestCandidates(req.Terms ?? new(), ct: ct);
                                await SendAsync(new IndexResponse { Id = req.Id, Suggest = folders });
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                Log.Error("提案候補の取得失敗", ex);
                                await SendAsync(new IndexResponse { Id = req.Id, Error = ex.Message });
                            }
                        });
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Log.Error("クライアント処理エラー", ex); }

        async Task SendAsync(IndexResponse response)
        {
            await writeLock.WaitAsync(ct);
            try { if (pipe.IsConnected) await IndexPipe.WriteAsync(pipe, response, ct); }
            catch (IOException) { }
            finally { writeLock.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        foreach (var v in _volumes)
        {
            v.SaveSnapshot();
            v.Dispose();
        }
        _policy.Dispose();
        Log.Info("Indexer 停止");
    }
}
