using System.Text.RegularExpressions;

namespace FileBox.Core.Search;

/// <summary>検索結果の名前で、検索語に一致した部分（強調表示用）。重なる範囲はまとめ、先頭から順に返す。</summary>
public static class MatchHighlighter
{
    public static IReadOnlyList<(int Start, int Length)> Ranges(SearchQuery query, string name)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (var term in query.Groups.SelectMany(g => g).Where(t => !t.Negate && !t.OnPath && t.Text.Length > 0))
        {
            var mode = term.Mode ?? query.DefaultMode;
            switch (mode)
            {
                case MatchMode.Regex:
                case MatchMode.Wildcard:
                    try
                    {
                        string pattern = mode == MatchMode.Regex ? term.Text : TermMatcher.WildcardToRegex(term.Text);
                        foreach (Match m in Regex.Matches(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
                            if (m.Length > 0 && m.Length < name.Length) ranges.Add((m.Index, m.Length));
                    }
                    catch (ArgumentException) { }
                    catch (RegexMatchTimeoutException) { }
                    break;
                case MatchMode.Prefix:
                    if (name.StartsWith(term.Text, StringComparison.OrdinalIgnoreCase)) ranges.Add((0, term.Text.Length));
                    break;
                default:
                    var variants = mode == MatchMode.Fuzzy ? KanaConverter.FuzzyVariants(term.Text) : KanaConverter.SearchVariants(term.Text);
                    foreach (var v in variants)
                        for (int i = name.IndexOf(v, StringComparison.OrdinalIgnoreCase); i >= 0; i = name.IndexOf(v, i + v.Length, StringComparison.OrdinalIgnoreCase))
                            ranges.Add((i, v.Length));
                    break;
            }
        }
        return Merge(ranges);
    }

    static List<(int Start, int Length)> Merge(List<(int Start, int Length)> ranges)
    {
        var result = new List<(int Start, int Length)>();
        foreach (var r in ranges.OrderBy(r => r.Start))
        {
            if (result.Count > 0 && r.Start <= result[^1].Start + result[^1].Length)
            {
                var last = result[^1];
                result[^1] = (last.Start, Math.Max(last.Start + last.Length, r.Start + r.Length) - last.Start);
            }
            else result.Add(r);
        }
        return result;
    }
}
