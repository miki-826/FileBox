using System.IO.Compression;

namespace FileBox.Core.Files;

/// <summary>ZIP の圧縮・展開（02_機能設計 §7.2）。ファイル名は UTF-8 で書き、展開時はフォルダ外への書き出しを防ぐ。</summary>
public static class ArchiveService
{
    public static Task CompressAsync(IReadOnlyList<string> paths, string zipPath) => Task.Run(() =>
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, System.Text.Encoding.UTF8);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                string root = Path.GetFileName(path.TrimEnd('\\'));
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                bool any = false;
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    zip.CreateEntryFromFile(file, root + "/" + Path.GetRelativePath(path, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    any = true;
                }
                if (!any) zip.CreateEntry(root + "/");
            }
            else
            {
                zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            }
        }
    });

    public static Task ExtractAsync(string zipPath, string destination) => ExtractEntriesAsync(zipPath, null, destination);

    /// <summary>
    /// names（エントリの FullName。フォルダーを指定すると配下すべて）だけを destination に展開する。null なら全部。
    /// stripPrefix を指定すると、その先頭のフォルダーを外して展開する（中身が 1 つのフォルダーの ZIP をそのまま置く用）。
    /// </summary>
    public static Task ExtractEntriesAsync(string zipPath, IReadOnlyCollection<string>? names, string destination, string? stripPrefix = null) => Task.Run(() =>
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        using var zip = OpenWithNameEncoding(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (names != null && !names.Any(n => entry.FullName == n || (n.EndsWith('/') && entry.FullName.StartsWith(n, StringComparison.Ordinal))))
                continue;
            string relative = entry.FullName;
            if (stripPrefix != null)
            {
                if (!relative.StartsWith(stripPrefix, StringComparison.Ordinal)) continue;
                relative = relative[stripPrefix.Length..];
                if (relative.Length == 0) continue;
            }
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // ../ で外へ出るエントリ（Zip Slip）は無視
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    });

    /// <summary>ZIP の中身の一覧（フォルダーのエントリが省略された ZIP でも、途中のフォルダーを補って返す）。</summary>
    public static IReadOnlyList<ArchiveEntry> List(string zipPath)
    {
        using var zip = OpenWithNameEncoding(zipPath);
        var map = new SortedDictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            string name = e.FullName.Replace('\\', '/');
            bool isDir = name.EndsWith('/');
            if (!isDir) map[name] = new ArchiveEntry(name, false, e.Length, e.CompressedLength, e.LastWriteTime.LocalDateTime);
            // 途中のフォルダー
            int slash = -1;
            while ((slash = name.IndexOf('/', slash + 1)) >= 0)
            {
                string dir = name[..(slash + 1)];
                map.TryAdd(dir, new ArchiveEntry(dir, true, 0, 0, null));
            }
        }
        return map.Values.ToList();
    }

    /// <summary>
    /// 展開先の決め方（エクスプローラーの「すべて展開」より賢く）: 中身が 1 つのフォルダー（または 1 つのファイル）だけなら
    /// ZIP と同じ場所にそのまま置き、ばらばらなら ZIP の名前のフォルダーを作ってその中に置く。
    /// 戻り値の Destination は展開する（展開後に開く）場所、StripPrefix は ExtractEntriesAsync に渡す値。
    /// </summary>
    public static (string Destination, string? StripPrefix) PlanExtract(string zipPath, Func<string, bool>? exists = null)
    {
        string parent = Path.GetDirectoryName(zipPath)!;
        var entries = List(zipPath);
        var tops = entries.Select(e => e.FullName.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tops.Count == 1 && entries.Any(e => e.FullName == tops[0] + "/"))
        {
            // 1 つのフォルダー: 同名が既にあれば「名前 (2)」にして中身を入れる
            string folder = UniqueName.Path(Path.Combine(parent, tops[0]), exists);
            return (folder, tops[0] + "/");
        }
        if (tops.Count == 1)
        {
            string file = Path.Combine(parent, tops[0]);
            if (!(exists ?? (p => File.Exists(p) || Directory.Exists(p)))(file)) return (parent, null);
        }
        string dest = UniqueName.Path(Path.Combine(parent, Path.GetFileNameWithoutExtension(zipPath)), exists);
        return (dest, null);
    }

    /// <summary>
    /// ファイル名を UTF-8 として厳密に読めればそのまま、読めなければ Shift_JIS（Windows 標準の「圧縮フォルダー」で作った日本語名 ZIP）で開く。
    /// </summary>
    static ZipArchive OpenWithNameEncoding(string zipPath)
    {
        var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, strictUtf8);
        try
        {
            _ = zip.Entries.Count;
            return zip;
        }
        catch (System.Text.DecoderFallbackException)
        {
            zip.Dispose();
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return ZipFile.Open(zipPath, ZipArchiveMode.Read, System.Text.Encoding.GetEncoding(932));
        }
    }
}

/// <summary>ZIP の中の 1 項目。FullName は「/」区切りで、フォルダーは末尾が「/」。</summary>
public sealed record ArchiveEntry(string FullName, bool IsDirectory, long Size, long CompressedSize, DateTime? Modified)
{
    public string Name => FullName.TrimEnd('/').Split('/')[^1];
    public int Depth => FullName.TrimEnd('/').Count(c => c == '/');
}
