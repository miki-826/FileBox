using System.Globalization;
using System.Text;

namespace FileBox.Core.Search;

/// <summary>
/// 検索クエリ構文の解析。
/// 例: <c>請求書 ext:pdf,xlsx size:&gt;1MB modified:2026-10 in:"D:\書類" folder: -tmp</c>
/// </summary>
public static class QueryParser
{
    static readonly string[] Keys = { "folder", "file", "ext", "size", "modified", "dm", "in", "path", "mode", "regex" };

    public readonly record struct Token(int Start, int Length, string? Key, string Value, bool Quoted);

    public static SearchQuery Parse(string? text, DateTime? now = null)
    {
        var q = new SearchQuery();
        if (string.IsNullOrWhiteSpace(text)) return q;
        DateTime today = (now ?? DateTime.Now).Date;

        foreach (var tok in Tokenize(text))
        {
            if (tok.Key == null && !tok.Quoted && (tok.Value == "|" || tok.Value == "OR"))
            {
                if (q.Groups[^1].Count > 0) q.Groups.Add(new());
                continue;
            }

            switch (tok.Key)
            {
                case "folder":
                case "file":
                    q.Kind = tok.Key == "folder" ? KindFilter.Folders : KindFilter.Files;
                    if (tok.Value.Length > 0) AddTerm(q, tok.Value, tok.Quoted);
                    break;
                case "ext":
                    foreach (var e in tok.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        q.Extensions.Add(e.TrimStart('*').TrimStart('.'));
                    break;
                case "size":
                    if (!TryParseSize(tok.Value, out var min, out var max)) q.Errors.Add($"size の指定が不正です: {tok.Value}");
                    else { q.MinSize = min; q.MaxSize = max; }
                    break;
                case "modified":
                case "dm":
                    if (!TryParseDateRange(tok.Value, today, out var from, out var to)) q.Errors.Add($"modified の指定が不正です: {tok.Value}");
                    else { q.ModifiedFrom = from; q.ModifiedTo = to; }
                    break;
                case "in":
                    if (tok.Value.Length > 0) q.InPath = tok.Value.TrimEnd('\\', '/');
                    break;
                case "path":
                    if (tok.Value.Length > 0) q.Groups[^1].Add(new SearchTerm(tok.Value, MatchMode.Substring, false, true));
                    break;
                case "mode":
                    if (TryParseMode(tok.Value, out var mode)) { q.DefaultMode = mode; q.DefaultModeExplicit = true; }
                    else q.Errors.Add($"mode の指定が不正です: {tok.Value}");
                    break;
                case "regex":
                    if (tok.Value.Length > 0) q.Groups[^1].Add(new SearchTerm(tok.Value, MatchMode.Regex, false, false));
                    break;
                default:
                    AddTerm(q, tok.Value, tok.Quoted);
                    break;
            }
        }
        if (q.Groups.Count > 1 && q.Groups[^1].Count == 0) q.Groups.RemoveAt(q.Groups.Count - 1);
        return q;
    }

    static void AddTerm(SearchQuery q, string raw, bool quoted)
    {
        if (quoted)
        {
            q.Groups[^1].Add(new SearchTerm(raw, MatchMode.Exact, false, false));
            return;
        }
        bool negate = false;
        if (raw.Length > 1 && raw[0] == '-')
        {
            negate = true;
            raw = raw[1..];
        }
        MatchMode? mode = null;
        if (raw.Length > 1 && raw[0] == '^') { mode = MatchMode.Prefix; raw = raw[1..]; }
        else if (raw.Length > 1 && raw[0] == '~') { mode = MatchMode.Fuzzy; raw = raw[1..]; }
        else if (raw.IndexOfAny(new[] { '*', '?' }) >= 0) mode = MatchMode.Wildcard;
        if (raw.Length == 0) return;
        q.Groups[^1].Add(new SearchTerm(raw, mode, negate, false));
    }

    /// <summary>クエリ文字列をトークン（元の位置付き）に分割する。チップとの双方向同期にも使う。</summary>
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            var sb = new StringBuilder();
            bool quoted = false;
            bool inQuote = false;
            while (i < text.Length && (inQuote || !char.IsWhiteSpace(text[i])))
            {
                char c = text[i];
                if (c == '"') { inQuote = !inQuote; quoted = true; i++; continue; }
                sb.Append(c);
                i++;
            }
            string word = sb.ToString();
            string? key = null;
            string value = word;
            int colon = word.IndexOf(':');
            if (colon > 0)
            {
                string candidate = word[..colon].ToLowerInvariant();
                if (Array.IndexOf(Keys, candidate) >= 0)
                {
                    key = candidate;
                    value = word[(colon + 1)..];
                }
            }
            tokens.Add(new Token(start, i - start, key, value, quoted));
        }
        return tokens;
    }

    public static bool TryParseMode(string value, out MatchMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "substring": case "partial": case "contains": mode = MatchMode.Substring; return true;
            case "prefix": case "start": mode = MatchMode.Prefix; return true;
            case "exact": case "whole": mode = MatchMode.Exact; return true;
            case "wildcard": mode = MatchMode.Wildcard; return true;
            case "regex": mode = MatchMode.Regex; return true;
            case "fuzzy": mode = MatchMode.Fuzzy; return true;
            default: mode = MatchMode.Substring; return false;
        }
    }

    public static string ModeKeyword(MatchMode mode) => mode switch
    {
        MatchMode.Prefix => "prefix",
        MatchMode.Exact => "exact",
        MatchMode.Wildcard => "wildcard",
        MatchMode.Regex => "regex",
        MatchMode.Fuzzy => "fuzzy",
        _ => "substring",
    };

    public static bool TryParseSize(string value, out long? min, out long? max)
    {
        min = max = null;
        value = value.Trim();
        int range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!TryParseBytes(value[..range], out long a) || !TryParseBytes(value[(range + 2)..], out long b)) return false;
            min = a; max = b;
            return true;
        }
        if (value.StartsWith(">=")) { if (!TryParseBytes(value[2..], out long v)) return false; min = v; return true; }
        if (value.StartsWith("<=")) { if (!TryParseBytes(value[2..], out long v)) return false; max = v; return true; }
        if (value.StartsWith('>')) { if (!TryParseBytes(value[1..], out long v)) return false; min = v + 1; return true; }
        if (value.StartsWith('<')) { if (!TryParseBytes(value[1..], out long v)) return false; max = v - 1; return true; }
        if (value.StartsWith('=')) value = value[1..];
        if (!TryParseBytes(value, out long x)) return false;
        min = x;
        return true;
    }

    public static bool TryParseBytes(string value, out long bytes)
    {
        bytes = 0;
        value = value.Trim().ToUpperInvariant();
        long mul = 1;
        string[] units = { "TB", "GB", "MB", "KB", "T", "G", "M", "K", "B" };
        long[] muls = { 1L << 40, 1L << 30, 1L << 20, 1L << 10, 1L << 40, 1L << 30, 1L << 20, 1L << 10, 1 };
        for (int i = 0; i < units.Length; i++)
        {
            if (value.EndsWith(units[i], StringComparison.Ordinal))
            {
                mul = muls[i];
                value = value[..^units[i].Length];
                break;
            }
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n < 0) return false;
        bytes = (long)(n * mul);
        return true;
    }

    public static bool TryParseDateRange(string value, DateTime today, out DateTime? from, out DateTime? to)
    {
        from = to = null;
        value = value.Trim();
        int range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!TryParseDateLiteral(value[..range], today, out var a, out _) ||
                !TryParseDateLiteral(value[(range + 2)..], today, out _, out var b)) return false;
            from = a; to = b;
            return true;
        }
        if (value.StartsWith(">=")) { if (!TryParseDateLiteral(value[2..], today, out var s, out _)) return false; from = s; return true; }
        if (value.StartsWith("<=")) { if (!TryParseDateLiteral(value[2..], today, out _, out var e)) return false; to = e; return true; }
        if (value.StartsWith('>')) { if (!TryParseDateLiteral(value[1..], today, out _, out var e)) return false; from = e; return true; }
        if (value.StartsWith('<')) { if (!TryParseDateLiteral(value[1..], today, out var s, out _)) return false; to = s; return true; }
        if (!TryParseDateLiteral(value, today, out var start, out var end)) return false;
        from = start; to = end;
        return true;
    }

    /// <summary>日付リテラルを [start, end) の期間に変換する。</summary>
    static bool TryParseDateLiteral(string value, DateTime today, out DateTime start, out DateTime end)
    {
        start = end = default;
        value = value.Trim().ToLowerInvariant().Replace('/', '-');
        switch (value)
        {
            case "today": start = today; end = today.AddDays(1); return true;
            case "yesterday": start = today.AddDays(-1); end = today; return true;
            case "thisweek":
                int offset = ((int)today.DayOfWeek + 6) % 7; // 月曜始まり
                start = today.AddDays(-offset); end = start.AddDays(7); return true;
            case "thismonth": start = new DateTime(today.Year, today.Month, 1); end = start.AddMonths(1); return true;
            case "lastmonth": end = new DateTime(today.Year, today.Month, 1); start = end.AddMonths(-1); return true;
            case "thisyear": start = new DateTime(today.Year, 1, 1); end = start.AddYears(1); return true;
        }
        var ci = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(value, new[] { "yyyy-M-d", "yyyyMMdd" }, ci, DateTimeStyles.None, out var d))
        {
            start = d; end = d.AddDays(1); return true;
        }
        if (DateTime.TryParseExact(value, new[] { "yyyy-M", "yyyyMM" }, ci, DateTimeStyles.None, out var m))
        {
            start = m; end = m.AddMonths(1); return true;
        }
        if (value.Length == 4 && int.TryParse(value, out int y) && y is > 1900 and < 3000)
        {
            start = new DateTime(y, 1, 1); end = start.AddYears(1); return true;
        }
        return false;
    }
}
