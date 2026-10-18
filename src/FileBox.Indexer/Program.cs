using System.Diagnostics;
using FileBox.Indexer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// FileBox.Indexer
//   (引数なし)            コンソールで起動
//   --parent <pid>        App から起動。親プロセス終了で停止
//   --service             Windows サービスとして起動（sc.exe から）
//   --install-service     サービス登録（管理者）
//   --uninstall-service   サービス削除（管理者）

const string ServiceName = "FileBoxIndexer";

if (args.Contains("--install-service"))
{
    string exe = Environment.ProcessPath!;
    return RunSc($"create {ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"FileBox Indexer\"")
        | RunSc($"description {ServiceName} \"FileBox のファイル名インデックス（MFT/USN）\"")
        | RunSc($"start {ServiceName}");
}
if (args.Contains("--uninstall-service"))
{
    RunSc($"stop {ServiceName}");
    return RunSc($"delete {ServiceName}");
}

if (args.Contains("--service"))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
    builder.Services.AddHostedService<IndexerService>();
    await builder.Build().RunAsync();
    return 0;
}

await using var host = new IndexerHost();
host.Start();

int parentIndex = Array.IndexOf(args, "--parent");
if (parentIndex >= 0 && parentIndex + 1 < args.Length && int.TryParse(args[parentIndex + 1], out int parentPid))
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(host.Stopping);
            Log.Info("親プロセスが終了したため停止します");
            host.RequestStop();
        }
        catch (ArgumentException) { host.RequestStop(); }
        catch (OperationCanceledException) { }
    });
}

Console.CancelKeyPress += (_, e) => { e.Cancel = true; host.RequestStop(); };
try { await Task.Delay(Timeout.Infinite, host.Stopping); } catch (OperationCanceledException) { }
return 0;

static int RunSc(string arguments)
{
    using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false })!;
    p.WaitForExit();
    return p.ExitCode;
}

sealed class IndexerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var host = new IndexerHost();
        host.Start();
        try { await Task.Delay(Timeout.Infinite, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, host.Stopping).Token); }
        catch (OperationCanceledException) { }
    }
}
