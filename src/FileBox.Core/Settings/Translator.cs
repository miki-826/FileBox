using System.Text.RegularExpressions;

namespace FileBox.Core.Settings;

/// <summary>
/// 日本語の文を、対応表（日本語 → 英語）で置き換える。「{0} 件」のように値の入る文は、対応表の「{0}」の位置の値を
/// 英語の文に入れ直す（値も訳せれば訳す）。つなげて作った文は、日本語の語ごとに訳せるものだけ訳す。
/// </summary>
public sealed partial class Translator
{
    readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    readonly List<(Regex Pattern, string Template)> _patterns = new();

    public Translator(IReadOnlyDictionary<string, string> map)
    {
        foreach (var (ja, en) in map)
        {
            if (!PlaceholderRegex().IsMatch(ja)) { _exact[ja] = en; continue; }
            var parts = PlaceholderRegex().Split(ja);
            string regex = "^" + string.Join("(.*?)", parts.Where((_, i) => i % 2 == 0).Select(Regex.Escape)) + "$";
            _patterns.Add((new Regex(regex, RegexOptions.Singleline | RegexOptions.CultureInvariant), en));
        }
        // 固定部分の長い（より具体的な）文を先に試す
        _patterns.Sort((a, b) => b.Pattern.ToString().Length.CompareTo(a.Pattern.ToString().Length));
    }

    public int Count => _exact.Count + _patterns.Count;

    /// <summary>訳した文。訳せなければ null。</summary>
    public string? Translate(string ja) => Translate(ja, depth: 2);

    string? Translate(string ja, int depth)
    {
        if (_exact.TryGetValue(ja, out var en)) return en;
        if (_exact.TryGetValue(ja.Trim(), out var trimmed)) return trimmed;
        foreach (var (pattern, template) in _patterns)
        {
            var m = pattern.Match(ja);
            if (!m.Success) continue;
            var values = m.Groups.Values.Skip(1)
                .Select(g => depth > 0 && HasJapanese(g.Value) ? Translate(g.Value, depth - 1) ?? g.Value : g.Value)
                .ToArray();
            return PlaceholderRegex().Replace(template, x => int.TryParse(x.Groups[1].Value, out int i) && i < values.Length ? values[i] : x.Value);
        }
        // 複数行の文は行ごとに訳す
        if (ja.Contains('\n'))
        {
            var lines = ja.Split('\n');
            var translated = lines.Select(l => l.Trim().Length == 0 ? l : Translate(l.TrimEnd('\r'), depth) ?? l).ToArray();
            if (!translated.SequenceEqual(lines)) return string.Join('\n', translated);
        }
        // つなげて作った文（「今日（3）」など）は、日本語の語ごとに訳せるものだけ訳す
        string pieces = JapaneseRunRegex().Replace(ja, r => _exact.TryGetValue(r.Value, out var w) ? w : r.Value);
        return pieces != ja ? pieces : null;
    }

    public static bool HasJapanese(string s)
    {
        foreach (char c in s)
            if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '！' and <= '～') return true;
        return false;
    }

    /// <summary>文の中の「{0}」「{1}」…の番号（対応表の点検用）。</summary>
    public static IReadOnlyList<int> Placeholders(string s) =>
        PlaceholderRegex().Matches(s).Select(m => int.Parse(m.Groups[1].Value)).OrderBy(i => i).ToList();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"[぀-ヿ一-鿿々]+")]
    private static partial Regex JapaneseRunRegex();
}
