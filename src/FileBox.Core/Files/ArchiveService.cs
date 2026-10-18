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

    public static Task ExtractAsync(string zipPath, string destination) => Task.Run(() =>
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        using var zip = OpenWithNameEncoding(zipPath);
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
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
