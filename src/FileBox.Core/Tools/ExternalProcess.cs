using System.Diagnostics;
using System.Text;

namespace FileBox.Core.Tools;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>シェルを経由せず引数を個別に渡す。出力は制限して読み捨て、停止要求では子プロセスも終了する。</summary>
public static class ExternalProcess
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        CancellationToken cancellationToken, Action<string>? onOutput = null, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null, int maxOutput = 128 * 1024)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("設定で外部エンジンの実行ファイルを指定してください");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromHours(12));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        limit.Token.ThrowIfCancellationRequested();
        process.Start();
        using var registration = limit.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        async Task<string> Drain(StreamReader reader, bool report)
        {
            var text = new StringBuilder(); var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (text.Length < maxOutput) text.Append(buffer, 0, Math.Min(n, maxOutput - text.Length));
                if (report && onOutput != null) onOutput(new string(buffer, 0, n));
            }
            return text.ToString();
        }
        var output = Drain(process.StandardOutput, true);
        var error = Drain(process.StandardError, false);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAll(output, error);
            limit.Token.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, output.Result, error.Result);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("外部エンジンの処理が制限時間を超えました");
            throw;
        }
    }
}
