using FileBox.Contracts;
using FileBox.Core.Files;
using FileBox.Core.Security;

namespace FileBox.App.Services;

/// <summary>索引を優先し、対象外の場所はバックグラウンドで集計する。</summary>
public static class FolderUsageService
{
    public static async Task<UsageReport> ReadAsync(string path, CancellationToken cancellation)
    {
        if (!PolicyService.Instance.Can(Permission.Read, path)) throw new UnauthorizedAccessException("この場所の容量を表示する権限がありません。");
        UsageReport? report = null;
        try
        {
            report = await IndexClient.Instance.CallAsync<UsageReport>(PolicyMethods.Usage, path, cancellation);
            var status = await IndexClient.Instance.GetStatusAsync(cancellation);
            if (report != null)
            {
                report.IsPartial |= status?.State != IndexState.Ready;
                if (report.IsPartial) foreach (var child in report.Children) child.IsPartial = true;
            }
            else if (status?.Volumes.Any(v => !v.Ready && path.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidOperationException("このドライブは索引を作成中です。少し待って更新してください。");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message == "FileBox のバックグラウンド処理（Indexer）に接続できません") { }
        if (report != null) return report;
        var result = await Task.Run(() => DirectoryUsage.Scan(path, cancellation), cancellation);
        static UsageItem Item(DirectoryUsageEntry entry) => new() { Path = entry.Path, IsDirectory = entry.IsDirectory, Size = entry.Bytes, IsPartial = entry.IsPartial };
        return new UsageReport { Path = path, Total = result.Bytes, FileCount = result.FileCount, FolderCount = result.FolderCount,
            IsPartial = result.IsPartial, Source = "scan", Children = result.Children.Select(Item).ToList(), Largest = result.Largest.Select(Item).ToList() };
    }
}
