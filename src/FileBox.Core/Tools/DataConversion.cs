using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace FileBox.Core.Tools;

/// <summary>テキストと構造化データの変換。推測で文字化けや欠落を隠さず、無効な入力は失敗にする。</summary>
public static class DataConversion
{
    public const int MaxInputBytes = 64 * 1024 * 1024;

    public static Encoding EncodingFor(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name switch
        {
            "UTF-8" => new UTF8Encoding(false, true),
            "UTF-8 BOM" => new UTF8Encoding(true, true),
            "UTF-16 LE" => new UnicodeEncoding(false, true, true),
            "UTF-16 BE" => new UnicodeEncoding(true, true, true),
            "Shift_JIS" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new ArgumentException("対応していない文字コードです"),
        };
    }

    public static string Decode(byte[] data, string encoding)
    {
        var codec = EncodingFor(encoding);
        var preamble = codec.GetPreamble();
        int offset = preamble.Length > 0 && data.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        // UTF-8 は BOM なしの指定でも入力の BOM を取り除く。
        if (encoding == "UTF-8" && data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        return codec.GetString(data, offset, data.Length - offset);
    }

    public static byte[] Encode(string text, string encoding)
    {
        var codec = EncodingFor(encoding);
        return [.. codec.GetPreamble(), .. codec.GetBytes(text)];
    }

    public static string Newlines(string text, string newline) => text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Replace("\n", newline switch { "LF" => "\n", "CRLF" => "\r\n", "CR" => "\r", _ => throw new ArgumentException("改行コードを選択してください") });

    public static string Convert(string input, ToolKind kind, ToolOptions options)
    {
        switch (kind)
        {
            case ToolKind.TextEncoding: return input;
            case ToolKind.TextNewlines: return Newlines(input, options.Newline);
            case ToolKind.JsonFormat: return ParseJson(input)?.ToJsonString(JsonOptions) ?? "null";
            case ToolKind.JsonSort: return Sort(ParseJson(input))?.ToJsonString(JsonOptions) ?? "null";
            case ToolKind.JsonConvert:
                var json = ParseJson(input);
                return options.Format == "xml" ? ToXml(json).ToString() : JsonToCsv(json, options.Delimiter[0]);
            case ToolKind.CsvConvert:
                var rows = ParseCsv(input, options.Delimiter[0]);
                var objects = CsvObjects(rows, options.HasHeader);
                return options.Format switch
                {
                    "json" => objects.ToJsonString(JsonOptions),
                    "xml" => ToXml(objects).ToString(),
                    "html" => Table(rows, options.HasHeader, html: true),
                    "md" => Table(rows, options.HasHeader, html: false),
                    _ => throw new ArgumentException("出力形式を選択してください"),
                };
            case ToolKind.ConfigConvert:
                var config = ParseIni(input);
                return options.Format == "xml" ? ToXml(config).ToString() : config.ToJsonString(JsonOptions);
            default: throw new NotSupportedException();
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? ParseJson(string input)
    {
        // 同じキーのデータを無言で失わない。
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
        void Check(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!keys.Add(p.Name)) throw new FormatException("JSON に重複するキーがあります");
                    Check(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Check(item);
        }
        Check(document.RootElement);
        return JsonNode.Parse(input);
    }

    static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };

    public static List<string[]> ParseCsv(string input, char delimiter)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, closed = false, started = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            started = true;
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < input.Length && input[i + 1] == '"') { cell.Append('"'); i++; }
                else { quoted = false; closed = true; }
                continue;
            }
            if (c == '"' && cell.Length == 0 && !closed) { quoted = true; continue; }
            if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); closed = false; continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < input.Length && input[i + 1] == '\n') i++;
                cells.Add(cell.ToString()); rows.Add(cells.ToArray()); cells.Clear(); cell.Clear(); closed = false; started = false;
                continue;
            }
            if (closed || c == '"') throw new FormatException("CSV の引用符の位置が正しくありません");
            cell.Append(c);
        }
        if (quoted) throw new FormatException("CSV の引用符が閉じられていません");
        if (started || cells.Count > 0) { cells.Add(cell.ToString()); rows.Add(cells.ToArray()); }
        if (rows.Count > 0 && rows.Any(r => r.Length != rows[0].Length)) throw new FormatException("CSV の列数がそろっていません");
        return rows;
    }

    static JsonArray CsvObjects(List<string[]> rows, bool header)
    {
        if (rows.Count == 0) return new();
        var keys = header ? rows[0] : Enumerable.Range(1, rows[0].Length).Select(n => "column" + n).ToArray();
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new FormatException("CSV の見出しが空欄または重複しています");
        return new JsonArray(rows.Skip(header ? 1 : 0).Select(r => (JsonNode)new JsonObject(keys.Select((k, i) => KeyValuePair.Create(k, (JsonNode?)JsonValue.Create(r[i]))))).ToArray());
    }

    static string JsonToCsv(JsonNode? node, char delimiter)
    {
        if (node is not JsonArray rows || rows.Any(x => x is not JsonObject)) throw new FormatException("CSV への変換にはオブジェクトの配列が必要です");
        var keys = rows.OfType<JsonObject>().SelectMany(o => o.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToArray();
        string Escape(string value) => value.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        string Value(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
        var lines = new[] { string.Join(delimiter, keys.Select(Escape)) }.Concat(rows.OfType<JsonObject>()
            .Select(o => string.Join(delimiter, keys.Select(k => Escape(Value(o[k]))))));
        return string.Join("\r\n", lines) + "\r\n";
    }

    static XElement ToXml(JsonNode? node)
    {
        XElement Element(string name, JsonNode? value)
        {
            var result = new XElement(name);
            switch (value)
            {
                case JsonObject obj:
                    foreach (var p in obj) { var e = Element("property", p.Value); e.SetAttributeValue("name", p.Key); result.Add(e); }
                    break;
                case JsonArray array:
                    foreach (var item in array) result.Add(Element("item", item));
                    break;
                case JsonValue v:
                    result.Value = v.TryGetValue<string>(out var text) ? text : v.ToJsonString();
                    break;
                default: result.SetAttributeValue("null", "true"); break;
            }
            return result;
        }
        return Element("data", node);
    }

    static string Table(List<string[]> rows, bool hasHeader, bool html)
    {
        if (rows.Count == 0) return html ? "<table></table>" : "";
        var keys = hasHeader ? rows[0] : Enumerable.Range(1, rows[0].Length).Select(n => "column" + n).ToArray();
        if (html)
        {
            string Row(string[] row, string tag) => "<tr>" + string.Concat(row.Select(c => $"<{tag}>{WebUtility.HtmlEncode(c)}</{tag}>")) + "</tr>";
            return "<!doctype html><meta charset=\"utf-8\"><table><thead>" + Row(keys, "th") + "</thead><tbody>" + string.Concat(rows.Skip(hasHeader ? 1 : 0).Select(r => Row(r, "td"))) + "</tbody></table>";
        }
        string Md(string[] row) => "| " + string.Join(" | ", row.Select(c => WebUtility.HtmlEncode(c).Replace("|", "&#124;").Replace("\r\n", "<br>").Replace("\r", "<br>").Replace("\n", "<br>"))) + " |";
        return Md(keys) + "\n| " + string.Join(" | ", keys.Select(_ => "---")) + " |\n" + string.Join("\n", rows.Skip(hasHeader ? 1 : 0).Select(Md));
    }

    public static JsonObject ParseIni(string input)
    {
        var root = new JsonObject();
        var section = root;
        foreach (string line in input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0 || text[0] is ';' or '#') continue;
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                string name = text[1..^1].Trim();
                if (name.Length == 0 || root.ContainsKey(name)) throw new FormatException("設定ファイルのセクション名が空欄または重複しています");
                section = new JsonObject(); root.Add(name, section); continue;
            }
            int equal = text.IndexOf('=');
            if (equal <= 0) throw new FormatException("設定ファイルは [セクション] と キー=値 の構文に対応しています");
            string key = text[..equal].Trim();
            if (section.ContainsKey(key)) throw new FormatException("設定ファイルに重複するキーがあります");
            section.Add(key, text[(equal + 1)..].Trim());
        }
        return root;
    }

    public static int[] PageRange(string input, int count)
    {
        if (count < 1) return [];
        if (string.IsNullOrWhiteSpace(input)) return Enumerable.Range(1, count).ToArray();
        var result = new SortedSet<int>();
        foreach (var part in input.Split(','))
        {
            var ends = part.Trim().Split('-');
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out int start) || start < 1 || start > count)
                throw new ArgumentException("ページ範囲を 1,3-5 のように指定してください");
            int end = start;
            if (ends.Length == 2 && (!int.TryParse(ends[1], out end) || end < start || end > count)) throw new ArgumentException("ページ範囲が文書の範囲を超えています");
            for (int i = start; i <= end; i++) result.Add(i);
        }
        return result.ToArray();
    }
}
