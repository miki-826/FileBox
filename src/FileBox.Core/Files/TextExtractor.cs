using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FileBox.Core.Files;

/// <summary>
/// 本文検索用に、ファイルから文字を抜き出す。テキスト・コード（UTF-8 / UTF-16 / Shift_JIS を判定）と、
/// Office の新しい形式（docx / xlsx / pptx。中の XML の文字だけ）に対応する。PDF などは App 側（IFilter）で扱う。
/// </summary>
public static class TextExtractor
{
    /// <summary>1 ファイルから取り出す最大の文字数（巨大なログなどで索引が膨らまないように）。</summary>
    public const int MaxChars = 1_000_000;
    public const long MaxFileBytes = 50L << 20;

    static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".cs", ".java", ".py", ".rb", ".go", ".rs", ".c", ".h", ".cpp", ".hpp",
        ".kt", ".swift", ".php", ".sql", ".ps1", ".sh", ".bat", ".cmd", ".xaml", ".csproj", ".tex", ".rtf",
    };

    static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp" };

    public static bool Supports(string ext) => Plain.Contains(ext) || Office.Contains(ext);

    /// <summary>抜き出した文字（対応していない・読めないときは null）。</summary>
    public static string? Extract(string path)
    {
        string ext = Path.GetExtension(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            if (Plain.Contains(ext)) return Limit(ReadText(path));
            if (Office.Contains(ext)) return Limit(ReadOfficeXml(path));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
        catch (XmlException) { }
        return null;
    }

    static string Limit(string s) => s.Length > MaxChars ? s[..MaxChars] : s;

    /// <summary>BOM、UTF-8 として厳密に読めるか、の順で判定し、どちらでもなければ Shift_JIS として読む。</summary>
    public static string ReadText(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[Math.Min(fs.Length, MaxChars * 3L)];
            fs.ReadExactly(bytes);
        }
        return Decode(bytes);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            // 途中で切った最後の文字が壊れていても UTF-8 と判定できるよう、末尾の 3 バイトは判定から外す
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            strict.GetCharCount(bytes, 0, Math.Max(0, bytes.Length - 3));
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    /// <summary>Office（Open XML / OpenDocument）の本文。段落・セル・スライドの区切りは改行にする。</summary>
    static string ReadOfficeXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var parts = zip.Entries.Where(e => IsTextPart(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal);
        foreach (var entry in parts)
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) sb.Append(reader.Value);
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "p" or "br" or "tab" or "si" or "row" or "c" or "tr" or "h")
                    sb.Append(reader.LocalName is "tab" or "c" ? '\t' : '\n');
                if (sb.Length > MaxChars) return sb.ToString();
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static bool IsTextPart(string name) =>
        name is "word/document.xml" or "xl/sharedStrings.xml" or "content.xml"
        || name.StartsWith("word/header", StringComparison.Ordinal) || name.StartsWith("word/footer", StringComparison.Ordinal)
        || name.StartsWith("word/footnotes", StringComparison.Ordinal)
        || (name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal))
        || (name.StartsWith("ppt/notesSlides/", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal))
        || (name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal));
}
