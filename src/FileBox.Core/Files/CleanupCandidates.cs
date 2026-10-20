namespace FileBox.Core.Files;

public enum CleanupKind { OldInstaller, ExtractedArchive, Unused, TempFile }

/// <summary>片付けの候補 1 件。消すかどうかは利用者が決める（ここでは提案だけ）。</summary>
public sealed record CleanupCandidate(string Path, bool IsDirectory, long Size, DateTime LastUsed, CleanupKind Kind, string Reason);

/// <summary>
/// 片付けの候補（02_機能設計 §2 整理）。ダウンロードの古いインストーラー・展開済みの圧縮ファイル・長く使っていないもの、
/// 一時フォルダーの古いもの。どれも「ごみ箱へ」で戻せる前提の提案で、自動では消さない。
/// </summary>
public static class CleanupCandidates
{
    static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };
    static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz" };

    public const int InstallerDays = 30;
    public const int UnusedDays = 180;
    public const int TempDays = 7;

    /// <summary>ダウンロードフォルダー（直下のみ）の候補。</summary>
    public static IReadOnlyList<CleanupCandidate> ScanDownloads(string folder, DateTime now)
    {
        var list = new List<CleanupCandidate>();
        if (!Directory.Exists(folder)) return list;
        var dir = new DirectoryInfo(folder);
        var entries = Safe(() => dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList()) ?? new();
        var folders = new HashSet<string>(entries.OfType<DirectoryInfo>().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var f in entries.OfType<FileInfo>())
        {
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            DateTime used = LastUsed(f);
            double days = (now - used).TotalDays;
            string ext = f.Extension;
            string stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            if (Archives.Contains(ext) && folders.Contains(stem))
                list.Add(new(f.FullName, false, f.Length, used, CleanupKind.ExtractedArchive, $"展開済みです（「{stem}」フォルダーがあります）"));
            else if (Installers.Contains(ext) && days >= InstallerDays)
                list.Add(new(f.FullName, false, f.Length, used, CleanupKind.OldInstaller, $"{(int)days} 日前のインストーラーです"));
            else if (days >= UnusedDays)
                list.Add(new(f.FullName, false, f.Length, used, CleanupKind.Unused, $"{(int)days} 日間使っていません"));
        }
        return list.OrderByDescending(c => c.Size).ToList();
    }

    /// <summary>一時フォルダー（%TEMP%）の、TempDays 日以上触られていない直下の項目。</summary>
    public static IReadOnlyList<CleanupCandidate> ScanTemp(string tempFolder, DateTime now, CancellationToken ct = default)
    {
        var list = new List<CleanupCandidate>();
        if (!Directory.Exists(tempFolder)) return list;
        var entries = Safe(() => new DirectoryInfo(tempFolder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList()) ?? new();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (e is FileInfo f)
            {
                DateTime used = LastUsed(f);
                if ((now - used).TotalDays >= TempDays)
                    list.Add(new(f.FullName, false, f.Length, used, CleanupKind.TempFile, "古い一時ファイルです"));
            }
            else if (e is DirectoryInfo d)
            {
                // フォルダーは中身のどれかが最近使われていれば候補にしない（実行中のアプリが使っている可能性）
                var (size, newest) = Measure(d, ct);
                if ((now - newest).TotalDays >= TempDays)
                    list.Add(new(d.FullName, true, size, newest, CleanupKind.TempFile, "古い一時フォルダーです"));
            }
        }
        return list.OrderByDescending(c => c.Size).ToList();
    }

    static DateTime LastUsed(FileSystemInfo f) => f.LastWriteTime > f.CreationTime ? f.LastWriteTime : f.CreationTime;

    static (long Size, DateTime Newest) Measure(DirectoryInfo dir, CancellationToken ct)
    {
        long size = 0;
        DateTime newest = LastUsed(dir);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Safe(() => dir.EnumerateFiles("*", options)) ?? Enumerable.Empty<FileInfo>())
        {
            ct.ThrowIfCancellationRequested();
            size += f.Length;
            var used = LastUsed(f);
            if (used > newest) newest = used;
        }
        return (size, newest);
    }

    static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
