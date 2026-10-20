using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FileBox.Core.Tools;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace FileBox.Tools;

public sealed record EnginePaths(string Ffmpeg = "", string Magick = "", string SevenZip = "", string Java = "", string Cfr = "");
public sealed record StagedOutput(string Path, string Name, bool IsFolder = false, string? Notice = null);

/// <summary>入力は読み取りだけ。一時領域へ結果を作り、公開と権限確認は呼出側が行う。</summary>
public sealed class ToolEngine(EnginePaths engines)
{
    public async Task<StagedOutput> ExecuteAsync(IReadOnlyList<string> inputs, ToolKind kind, ToolOptions o, string work,
        CancellationToken token, Func<string, bool>? canRead = null)
    {
        o.Validate(kind);
        token.ThrowIfCancellationRequested();
        string input = inputs[0];
        string stem = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(input));
        if (stem.Length == 0) stem = "result";
        string output = Path.Combine(work, "result." + o.Format);
        string name = stem + "_converted." + o.Format;
        if (kind == ToolKind.PdfMerge)
        {
            if (inputs.Count < 2) throw new ArgumentException("結合する PDF を 2 件以上選択してください");
            using var combined = new PdfDocument();
            foreach (string path in inputs)
            {
                token.ThrowIfCancellationRequested();
                using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                foreach (var page in document.Pages) { token.ThrowIfCancellationRequested(); combined.AddPage(page); }
            }
            combined.Save(output);
            return new(output, "merged.pdf");
        }
        if (kind is >= ToolKind.CsvConvert and <= ToolKind.TextNewlines)
        {
            if (new FileInfo(input).Length > DataConversion.MaxInputBytes) throw new IOException("テキスト処理の入力は 64 MiB 以下にしてください");
            byte[] bytes = await File.ReadAllBytesAsync(input, token);
            if (bytes.Length > DataConversion.MaxInputBytes) throw new IOException("テキスト処理の入力は 64 MiB 以下にしてください");
            string text = DataConversion.Decode(bytes, o.InputEncoding);
            if (text.Contains('\0')) throw new FormatException("バイナリの可能性があります。入力の文字コードを確認してください");
            string converted = DataConversion.Convert(text, kind, o);
            string encoding = kind is ToolKind.TextEncoding or ToolKind.TextNewlines ? o.OutputEncoding : "UTF-8";
            await File.WriteAllBytesAsync(output, DataConversion.Encode(converted, encoding), token);
            if (kind is ToolKind.TextEncoding or ToolKind.TextNewlines) name = stem + "_converted" + Path.GetExtension(input);
            return new(output, name, Notice: kind == ToolKind.ConfigConvert ? "コメントは出力しません。値は文字列として保存します。" : null);
        }
        if (kind == ToolKind.FolderTree)
        {
            await File.WriteAllTextAsync(output, FolderInspection.Tree(input, o.Depth, token, canRead, work), new UTF8Encoding(false), token);
            return new(output, stem + "_tree.txt");
        }
        if (kind == ToolKind.ArchiveCreate)
        {
            if (o.Format != "7z") await SafeArchives.CreateAsync(inputs, output, o.Format, token, canRead, work);
            else
            {
                // コピー時に配下の権限・リンクも検査。7-Zip に元フォルダを直接再帰走査させない。
                string sources = Path.Combine(work, "sources"); Directory.CreateDirectory(sources);
                foreach (string path in inputs) await CopySourceAsync(path, Path.Combine(sources, Path.GetFileName(Path.TrimEndingDirectorySeparator(path))), token, canRead, 0, work);
                var args = new List<string> { "a", "-t7z", "-y", "-bd", output, "--" };
                args.AddRange(Directory.EnumerateFileSystemEntries(sources));
                await CheckedAsync(engines.SevenZip, args, sources, token);
            }
            return new(output, stem + "_archive." + o.Format);
        }
        if (kind == ToolKind.ArchiveExtract)
        {
            string folder = Path.Combine(work, "extracted");
            string ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext == ".zip") await SafeArchives.ExtractZipAsync(input, folder, token);
            else if (ext == ".tar" || input.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || ext == ".tgz")
                await SafeArchives.ExtractTarAsync(input, folder, ext != ".tar", token);
            else
            {
                var listed = await CheckedAsync(engines.SevenZip, ["l", "-slt", "-sccUTF-8", "--", input], work, token, maxOutput: 32 * 1024 * 1024);
                if (listed.Output.Length >= 32 * 1024 * 1024) throw new IOException("圧縮ファイルの一覧が大きすぎます");
                SafeArchives.ValidateSevenZipListing(listed.Output, folder);
                Directory.CreateDirectory(folder);
                await CheckedAsync(engines.SevenZip, ["x", "-y", "-aos", "-bd", "-o" + folder, "--", input], work, token);
            }
            SafeOutput.ValidateTree(folder);
            if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) > SafeArchives.MaxExpandedBytes)
                throw new IOException("展開後の合計容量が 20 GiB を超えています");
            return new(folder, stem + "_extracted", true);
        }
        if (kind == ToolKind.JarSources)
        {
            if (!File.Exists(engines.Cfr) || !engines.Cfr.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("設定で CFR の JAR を指定してください");
            string folder = Path.Combine(work, "sources");
            // 入力 JAR の危険なエントリを先に拒否。対象コードの実行はしない。
            using (var jar = ZipFile.OpenRead(input))
            {
                if (jar.Entries.Count > SafeArchives.MaxEntries) throw new IOException("JAR の項目が上限を超えています");
                long size = 0;
                foreach (var entry in jar.Entries)
                {
                    _ = SafeOutput.EntryPath(folder, entry.FullName);
                    size = checked(size + entry.Length);
                    if (size > 1024L * 1024 * 1024) throw new IOException("JAR の展開容量が 1 GiB を超えています");
                }
            }
            await CheckedAsync(engines.Java, ["-Xmx512m", "-jar", engines.Cfr, input, "--outputdir", folder, "--silent", "true"], work, token);
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*.java", SearchOption.AllDirectories).Any()) throw new IOException("Java ソースを抽出できませんでした。CFR と JDK の対応を確認してください");
            SafeOutput.ValidateTree(folder);
            return new(folder, stem + "_sources", true, "復元されたコードは元のソースと一致するとは限りません。");
        }
        var definition = ToolCatalog.Get(kind);
        if (definition.Engine == "magick")
        {
            string copied = Path.Combine(work, "input" + Path.GetExtension(input).ToLowerInvariant());
            await CopySourceAsync(input, copied, token, canRead, 0);
            if (Path.GetExtension(input).Equals(".svg", StringComparison.OrdinalIgnoreCase)) ValidateSvg(copied);
            var formats = await CheckedAsync(engines.Magick, ["-list", "format"], work, token);
            string outputCoder = MediaCommands.ImageCoder(o.Format);
            var capabilities = System.Text.RegularExpressions.Regex.Match(formats.Output, @"(?m)^\s*" + outputCoder + @"\*?\s+(\S+)");
            if (!capabilities.Success || !capabilities.Groups[1].Value.Contains('w'))
                throw new NotSupportedException("この ImageMagick は指定形式への書き出しに対応していません。対応ビルドを導入するか出力形式を変更してください");
            string policy = Path.Combine(work, "policy.xml");
            await File.WriteAllTextAsync(policy, ImagePolicy, token);
            var env = new Dictionary<string, string> { ["MAGICK_CONFIGURE_PATH"] = work, ["MAGICK_TEMPORARY_PATH"] = work };
            string imageInput = MediaCommands.ImageCoder(Path.GetExtension(input)) + ":" + copied + "[0]";
            await CheckedAsync(engines.Magick, MediaCommands.Image(imageInput, output, kind, o), work, token, env);
            string? notice = Path.GetExtension(input).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "アニメーション画像は先頭フレームを出力します。" : null;
            if (kind == ToolKind.ImageCompress && o.TargetMiB > 0)
            {
                long target = (long)(o.TargetMiB * 1024 * 1024);
                // 画質を下げながら再試行し、達成できなければ明示する。
                for (int q = o.Quality - 10; new FileInfo(output).Length > target && q >= 5; q -= 10)
                {
                    token.ThrowIfCancellationRequested(); File.Delete(output);
                    await CheckedAsync(engines.Magick, MediaCommands.Image(imageInput, output, kind, o, q), work, token, env);
                }
                if (new FileInfo(output).Length > target) notice = "目標容量に達しませんでした。解像度を下げるか出力形式を変更してください。";
            }
            if (!File.Exists(output)) throw new IOException("出力が作成されませんでした。複数ページ画像は先頭フレームに変換してから操作してください");
            return new(output, name, Notice: notice);
        }
        if (definition.Engine == "ffmpeg")
        {
            if (kind is not (ToolKind.AudioConvert or ToolKind.VideoAudio or ToolKind.VideoGif or ToolKind.VideoThumbnail) && !(kind == ToolKind.VideoTrim && o.FastTrim))
            {
                string encoder = MediaCommands.VideoEncoder(o);
                var available = await CheckedAsync(engines.Ffmpeg, ["-hide_banner", "-encoders"], work, token);
                if (!available.Output.Contains(" " + encoder + " ", StringComparison.Ordinal)) throw new NotSupportedException("選択したエンコーダーがこの FFmpeg にありません。別のコーデックまたは CPU を選択してください");
            }
            await CheckedAsync(engines.Ffmpeg, MediaCommands.Media(input, output, kind, o), work, token);
            return new(output, name, Notice: kind == ToolKind.VideoTrim && o.FastTrim ? "高速モードではキーフレームにより切り出し位置がずれる場合があります。" : kind == ToolKind.VideoCompress && o.TargetMiB > 0 ? "指定容量は目標値です。結果の容量を確認してください。" : null);
        }
        throw new NotSupportedException("この処理は Windows の変換機能を使って実行します");
    }

    public static async Task<ProcessResult> CheckedAsync(string engine, IEnumerable<string> args, string work, CancellationToken token,
        IReadOnlyDictionary<string, string>? environment = null, int maxOutput = 128 * 1024)
    {
        var result = await ExternalProcess.RunAsync(engine, args, work, token, environment: environment, maxOutput: maxOutput);
        if (result.ExitCode != 0) throw new ToolEngineException(result.ExitCode, result.Error);
        return result;
    }

    static async Task CopySourceAsync(string path, string target, CancellationToken token, Func<string, bool>? canRead, int depth, string? excludePath = null)
    {
        token.ThrowIfCancellationRequested();
        if (excludePath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(excludePath), StringComparison.OrdinalIgnoreCase)) return;
        if (depth > 100) throw new IOException("フォルダが深すぎます");
        if (!(canRead?.Invoke(path) ?? true)) throw new UnauthorizedAccessException("入力の読み取りが許可されていません");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクは処理できません。実際のファイルを指定してください");
        if (Directory.Exists(path))
        {
            Directory.CreateDirectory(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) await CopySourceAsync(child, Path.Combine(target, Path.GetFileName(child)), token, canRead, depth + 1, excludePath);
        }
        else
        {
            await using var source = File.OpenRead(path); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            await source.CopyToAsync(output, token);
        }
    }

    static void ValidateSvg(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("SVG は 16 MiB 以下にしてください");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var svg = XDocument.Load(reader);
        foreach (var e in svg.Descendants())
        {
            if (e.Name.LocalName is "script" or "foreignObject" || e.Attributes().Any(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("スクリプトを含む SVG は変換できません");
            foreach (var a in e.Attributes())
                if (a.Name.LocalName == "href" && !a.Value.StartsWith('#') || a.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) && !a.Value.Contains("url(#", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("外部参照を含む SVG は変換できません");
            if (e.Name.LocalName == "style" && (e.Value.Contains("@import", StringComparison.OrdinalIgnoreCase) || e.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("外部参照を含む SVG は変換できません");
        }
    }

    const string ImagePolicy = """
        <policymap>
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="filter" rights="none" pattern="*" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read|write" pattern="{PNG,JPEG,JPG,WEBP,HEIC,HEIF,ICO,GIF,BMP,TIFF}" />
          <policy domain="coder" rights="read" pattern="{SVG,MSVG,MVG}" />
        </policymap>
        """;
}

/// <summary>外部ツールの詳細は画面でのみ参照。ファイル内容を含む可能性があるためログには残さない。</summary>
public sealed class ToolEngineException(int code, string detail) : Exception("外部エンジンが処理できませんでした。形式・コーデック・GPU・ファイルの状態を確認してください")
{
    public int ExitCode { get; } = code;
    public string EngineDetail { get; } = detail;
}
