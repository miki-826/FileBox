using System.Security.Cryptography;

namespace FileBox.Core.Files;

/// <summary>中身が同じファイルの組（Size は 1 ファイルあたり）。</summary>
public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<string> Paths)
{
    /// <summary>1 つを残して他を消したときに空く容量。</summary>
    public long Wasted => Size * (Paths.Count - 1);
}

public sealed record DuplicateProgress(string Stage, int Done, int Total);

/// <summary>
/// 重複ファイルの検出。サイズ → 先頭 64KB のハッシュ → 全体の SHA-256 の順に絞り込み、読むファイルを最小にする。
/// 再解析ポイント（ジャンクション・OneDrive のクラウドのみのファイルなど）は辿らない・読まない。
/// </summary>
public static class DuplicateFinder
{
    const int HeadBytes = 64 * 1024;
    const FileAttributes Skip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00400000; // RECALL_ON_DATA_ACCESS

    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<string> roots, long minSize = 1,
        IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default, Func<string, bool>? canRead = null) => Task.Run(() =>
    {
        var bySize = new Dictionary<long, List<string>>();
        int scanned = 0;
        foreach (var file in Enumerate(roots, ct))
        {
            if (file.Length < minSize || !(canRead?.Invoke(file.FullName) ?? true)) continue;
            if (!bySize.TryGetValue(file.Length, out var list)) bySize[file.Length] = list = new();
            list.Add(file.FullName);
            if (++scanned % 2000 == 0) progress?.Report(new("ファイルを数えています", scanned, 0));
        }

        var sameSize = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int total = sameSize.Sum(kv => kv.Value.Count), done = 0;
        var groups = new List<DuplicateGroup>();
        foreach (var (size, paths) in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            var byHead = GroupBy(paths, p => Hash(p, size <= HeadBytes ? size : HeadBytes, ct));
            foreach (var head in byHead.Where(g => g.Paths.Count > 1))
            {
                // 64KB 以下なら先頭のハッシュが全体のハッシュ
                var byFull = size <= HeadBytes ? new() { head } : GroupBy(head.Paths, p => Hash(p, long.MaxValue, ct));
                foreach (var full in byFull.Where(g => g.Paths.Count > 1))
                    groups.Add(new DuplicateGroup(size, full.Key, full.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
            }
            done += paths.Count;
            progress?.Report(new("中身を比べています", done, total));
        }
        return (IReadOnlyList<DuplicateGroup>)groups.OrderByDescending(g => g.Wasted).ToList();
    }, ct);

    static IEnumerable<FileInfo> Enumerate(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = Skip };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Add(f.FullName)) yield return f;
            }
        }
    }

    static List<(string Key, List<string> Paths)> GroupBy(List<string> paths, Func<string, string?> key) =>
        paths.Select(p => (Path: p, Key: key(p)))
             .Where(x => x.Key != null)
             .GroupBy(x => x.Key!)
             .Select(g => (g.Key, g.Select(x => x.Path).ToList()))
             .ToList();

    /// <summary>先頭 length バイトの SHA-256（読めないファイルは null）。</summary>
    static string? Hash(string path, long length, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long left = length;
            int n;
            while (left > 0 && (n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, n);
                left -= n;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
