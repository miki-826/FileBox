namespace FileBox.Core.Files;

public sealed record DirectoryUsageEntry(string Path, bool IsDirectory, long Bytes, bool IsPartial);
public sealed record DirectoryUsageResult(long Bytes, long FileCount, long FolderCount, bool IsPartial,
    IReadOnlyList<DirectoryUsageEntry> Children, IReadOnlyList<DirectoryUsageEntry> Largest);

/// <summary>索引のない場所の容量集計。再解析ポイントは辿らず、読めなかった場所を明示する。</summary>
public static class DirectoryUsage
{
    public static DirectoryUsageResult Scan(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var children = new List<DirectoryUsageEntry>();
        var largest = new PriorityQueue<DirectoryUsageEntry, long>();
        long total = 0, files = 0, folders = 0;
        bool partial = false;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            long bytes = 0;
            bool incomplete = false;
            bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
            if (directory) folders++;
            var pending = new Stack<FileSystemInfo>();
            pending.Push(entry);
            while (pending.TryPop(out var current))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                    if (current is DirectoryInfo dir)
                    {
                        foreach (var child in dir.EnumerateFileSystemInfos())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if ((child.Attributes & FileAttributes.Directory) != 0) folders++;
                            pending.Push(child);
                        }
                    }
                    else if (current is FileInfo file)
                    {
                        long size = file.Length;
                        bytes += size;
                        files++;
                        var item = new DirectoryUsageEntry(file.FullName, false, size, false);
                        if (largest.Count < 100) largest.Enqueue(item, size);
                        else if (largest.TryPeek(out _, out long minimum) && size > minimum) largest.EnqueueDequeue(item, size);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { incomplete = true; }
            }
            children.Add(new(entry.FullName, directory, bytes, incomplete));
            total += bytes;
            partial |= incomplete;
        }
        return new(total, files, folders, partial, children.OrderByDescending(c => c.Bytes).ToList(),
            largest.UnorderedItems.Select(i => i.Element).OrderByDescending(i => i.Bytes).ToList());
    }
}
