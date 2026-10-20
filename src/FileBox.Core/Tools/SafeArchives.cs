using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace FileBox.Core.Tools;

public static class SafeArchives
{
    public const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100000;

    public static async Task CreateAsync(IReadOnlyList<string> sources, string output, string format, CancellationToken token, Func<string, bool>? canRead = null, string? excludePath = null)
    {
        var files = new List<(string Path, string Entry, bool Folder)>();
        void Add(string path, string entry, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (excludePath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(excludePath), StringComparison.OrdinalIgnoreCase)) return;
            if (depth > 100 || files.Count >= MaxEntries) throw new IOException("圧縮する項目が上限を超えています");
            if (!(canRead?.Invoke(path) ?? true)) throw new UnauthorizedAccessException("圧縮対象の読み取りが許可されていません");
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを含む場所は圧縮できません");
            bool folder = (attr & FileAttributes.Directory) != 0;
            files.Add((path, entry, folder));
            if (folder)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Add(child, entry + "/" + Path.GetFileName(child), depth + 1);
        }
        foreach (string source in sources) Add(source, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), 0);
        if (files.Select(x => x.Entry).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new IOException("同じ名前の入力があるため圧縮できません");
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        if (format == "zip")
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file.Entry + (file.Folder ? "/" : ""), CompressionLevel.Optimal);
                if (!file.Folder)
                {
                    await using var input = File.OpenRead(file.Path); await using var target = entry.Open();
                    await input.CopyToAsync(target, token);
                }
            }
        }
        else if (format is "tar" or "tar.gz")
        {
            await using Stream compressed = format == "tar.gz" ? new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true) : stream;
            await using var writer = new TarWriter(compressed, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = new PaxTarEntry(file.Folder ? TarEntryType.Directory : TarEntryType.RegularFile, file.Entry);
                if (file.Folder) await writer.WriteEntryAsync(entry, token);
                else
                {
                    await using var input = File.OpenRead(file.Path); entry.DataStream = input;
                    await writer.WriteEntryAsync(entry, token);
                }
            }
        }
        else throw new NotSupportedException();
    }

    public static async Task ExtractZipAsync(string input, string destination, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var zip = OpenZip(input);
        if (zip.Entries.Count > MaxEntries) throw new IOException("展開する項目が上限を超えています");
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = SafeOutput.EntryPath(destination, entry.FullName);
            if (!names.Add(target)) throw new IOException("同名の展開先が重複しています");
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクまたは特殊な項目を含む圧縮ファイルは展開できません");
            total = checked(total + entry.Length);
            if (total > MaxExpandedBytes) throw new IOException("展開後の合計容量が 20 GiB を超えています");
        }
        Directory.CreateDirectory(destination);
        long written = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = SafeOutput.EntryPath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            written += await CopyLimitedAsync(source, output, MaxExpandedBytes - written, token);
        }
    }

    static ZipArchive OpenZip(string input)
    {
        var zip = ZipFile.Open(input, ZipArchiveMode.Read, new UTF8Encoding(false, true));
        try { _ = zip.Entries.Count; return zip; }
        catch (DecoderFallbackException)
        {
            zip.Dispose();
            return ZipFile.Open(input, ZipArchiveMode.Read, Encoding.GetEncoding(932));
        }
    }

    public static async Task ExtractTarAsync(string input, string destination, bool gzip, CancellationToken token)
    {
        await using var file = File.OpenRead(input);
        await using Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        await using var reader = new TarReader(stream);
        Directory.CreateDirectory(destination);
        long total = 0; int count = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, token)) != null)
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaxEntries) throw new IOException("展開する項目が上限を超えています");
            string target = SafeOutput.EntryPath(destination, entry.Name);
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new IOException("リンクまたは特殊な項目を含む圧縮ファイルは展開できません");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            if (entry.DataStream != null) total += await CopyLimitedAsync(entry.DataStream, output, MaxExpandedBytes - total, token);
        }
    }

    static async Task<long> CopyLimitedAsync(Stream source, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int n;
        while ((n = await source.ReadAsync(buffer, token)) > 0)
        {
            total += n;
            if (total > limit) throw new IOException("展開後の合計容量が 20 GiB を超えています");
            await output.WriteAsync(buffer.AsMemory(0, n), token);
        }
        return total;
    }

    /// <summary>7-Zip の -slt の一覧を実行前に検査する。リンク、ADS、範囲外パス、異常な容量は拒否する。</summary>
    public static void ValidateSevenZipListing(string listing, string destination)
    {
        int start = listing.IndexOf("----------", StringComparison.Ordinal);
        if (start < 0) throw new IOException("圧縮ファイルの一覧を確認できませんでした");
        long total = 0; int count = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string block in listing[(start + 10)..].Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in block.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                if (equals <= 0 || !values.TryAdd(line[..equals], line[(equals + 3)..])) throw new IOException("圧縮ファイルの一覧が正しくありません");
            }
            if (!values.TryGetValue("Path", out var name)) continue;
            string target = SafeOutput.EntryPath(destination, name);
            if (!names.Add(target) || ++count > MaxEntries) throw new IOException("項目が重複しているか上限を超えています");
            if (values.Keys.Any(k => k.Contains("Link", StringComparison.OrdinalIgnoreCase) || k.Contains("Stream", StringComparison.OrdinalIgnoreCase)) ||
                values.GetValueOrDefault("Attributes", "").Contains('l') || values.GetValueOrDefault("Symbolic Link", "").Length > 0)
                throw new IOException("リンクまたは代替データストリームを含む圧縮ファイルは展開できません");
            if (values.TryGetValue("Size", out var sizeText))
            {
                if (!long.TryParse(sizeText, out long size) || size < 0) throw new IOException("展開後の容量を確認できませんでした");
                total = checked(total + size);
                if (total > MaxExpandedBytes) throw new IOException("展開後の合計容量が 20 GiB を超えています");
            }
            else if (values.GetValueOrDefault("Folder") != "+") throw new IOException("展開後の容量を確認できませんでした");
        }
        if (count == 0) throw new IOException("展開できる項目がありません");
    }
}
