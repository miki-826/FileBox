using FileBox.Core.Files;
using FileBox.Indexer.Index;
using Xunit;

namespace FileBox.Tests;

public sealed class DirectoryUsageTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "FileBox-Usage-" + Guid.NewGuid().ToString("N"));
    public DirectoryUsageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CountsNestedFilesAndEmptyFoldersWithoutDoubleCounting()
    {
        Directory.CreateDirectory(Path.Combine(_root, "資料", "空"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(_root, "資料", "b.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(_root, "資料", "空", "zero.bin"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(_root, "未使用"));
        var result = DirectoryUsage.Scan(_root);
        Assert.Equal(10, result.Bytes);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(3, result.FolderCount);
        Assert.False(result.IsPartial);
        Assert.Equal(7, result.Children.First().Bytes);
        Assert.Equal(0, result.Children.Single(c => c.Path.EndsWith("未使用")).Bytes);
        Assert.Equal(10, result.Largest.Sum(f => f.Bytes));
    }

    [Fact]
    public void CancelsBeforeStartingAndDoesNotReturnAnEmptySuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DirectoryUsage.Scan(_root, cancellation.Token));
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryUsage.Scan(Path.Combine(_root, "不存在")));
    }

    [Fact]
    public void IndexCountsFilesAndMarksUnresolvedMetadataAsPartial()
    {
        var index = new FileIndex();
        int missing;
        using (var write = index.Write())
        {
            int root = write.AddRoot(0, "D:", 0);
            int docs = write.Add(root, "資料", EntryFlags.Directory, 0, 0);
            write.Add(docs, "empty", EntryFlags.Directory, 0, 0);
            write.Add(docs, "known.bin", EntryFlags.None, 12, 0);
            missing = write.Add(docs, "pending.bin", EntryFlags.MetaMissing, 0, 0);
            int deleted = write.Add(docs, "deleted.bin", EntryFlags.None, 99, 0);
            write.Remove(deleted);
        }
        var report = index.Usage(@"D:\")!;
        Assert.Equal(12, report.Total);
        Assert.Equal(2, report.FileCount);
        Assert.Equal(2, report.FolderCount);
        Assert.True(report.IsPartial);
        Assert.True(Assert.Single(report.Children).IsPartial);
        using (var write = index.Write()) write.SetMeta(missing, 8, 0);
        report = index.Usage(@"D:\")!;
        Assert.Equal(20, report.Total);
        Assert.False(report.IsPartial);
        Assert.Equal(20, index.SubtreeSize(@"D:\"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
