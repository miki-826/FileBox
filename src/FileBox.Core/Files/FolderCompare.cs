namespace FileBox.Core.Files;

public enum CompareStatus { Same, Different, OnlyLeft, OnlyRight }

/// <summary>フォルダー比較の 1 項目。RelativePath は比べた 2 つのフォルダーからの相対パス。</summary>
public sealed record CompareEntry(string RelativePath, bool IsDirectory, CompareStatus Status, long? LeftSize, long? RightSize, DateTime? LeftModified, DateTime? RightModified);

/// <summary>2 つのフォルダー・ファイルの比較（02_機能設計 §7.2 比較）。</summary>
public static class FolderCompare
{
    /// <summary>
    /// 配下を比べる。サイズが違えば「違う」、同じなら中身を読み比べる（日時が同じでも中身が違うことがあるため）。
    /// </summary>
    public static Task<IReadOnlyList<CompareEntry>> CompareAsync(string left, string right, CancellationToken ct = default) => Task.Run(() =>
    {
        var l = Snapshot(left, ct);
        var r = Snapshot(right, ct);
        var result = new List<CompareEntry>();
        foreach (var key in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            l.TryGetValue(key, out var a);
            r.TryGetValue(key, out var b);
            if (a == null) { result.Add(new(key, b!.IsDir, CompareStatus.OnlyRight, null, b.Size, null, b.Modified)); continue; }
            if (b == null) { result.Add(new(key, a.IsDir, CompareStatus.OnlyLeft, a.Size, null, a.Modified, null)); continue; }
            CompareStatus status;
            if (a.IsDir || b.IsDir) status = a.IsDir == b.IsDir ? CompareStatus.Same : CompareStatus.Different;
            else if (a.Size != b.Size) status = CompareStatus.Different;
            else status = SameContent(Path.Combine(left, key), Path.Combine(right, key), ct) ? CompareStatus.Same : CompareStatus.Different;
            result.Add(new(key, a.IsDir, status, a.IsDir ? null : a.Size, b.IsDir ? null : b.Size, a.Modified, b.Modified));
        }
        return (IReadOnlyList<CompareEntry>)result;
    }, ct);

    sealed record Info(bool IsDir, long Size, DateTime Modified);

    static Dictionary<string, Info> Snapshot(string root, CancellationToken ct)
    {
        var map = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            map[Path.GetRelativePath(root, e.FullName)] = e is FileInfo f ? new(false, f.Length, f.LastWriteTimeUtc) : new(true, 0, e.LastWriteTimeUtc);
        }
        return map;
    }

    /// <summary>2 つのファイルの中身が同じか（先頭から読み比べ、違いが見つかった時点で止める）。</summary>
    public static bool SameContent(string a, string b, CancellationToken ct = default)
    {
        try
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            if (fa.Length != fb.Length) return false;
            byte[] ba = new byte[81920], bb = new byte[81920];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
                if (na == 0) return true;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
