using System.Text;
using System.Text.RegularExpressions;

namespace FileBox.Core.Search;

/// <summary>検索対象エントリのパスを必要な時だけ組み立てるための口。</summary>
public interface IPathSource
{
    string GetPath(int id);
}

/// <summary>解析済みクエリを照合用にコンパイルしたもの。インデックスの全件走査から並列に呼ばれる（スレッドセーフ）。</summary>
public sealed class CompiledQuery
{
    readonly TermMatcher[][] _groups;
    readonly string[] _extSuffixes;
    readonly string? _rankText;

    public SearchQuery Source { get; }
    public KindFilter Kind => Source.Kind;
    public bool NeedsMetadata => Source.NeedsMetadata;
    public bool HasPathTerms { get; }
    public IReadOnlyList<string> Errors => Source.Errors;

    CompiledQuery(SearchQuery source, TermMatcher[][] groups, string[] extSuffixes, string? rankText)
    {
        Source = source;
        _groups = groups;
        _extSuffixes = extSuffixes;
        _rankText = rankText;
        HasPathTerms = groups.Any(g => g.Any(m => m.OnPath));
    }

    public static CompiledQuery Compile(string? text, DateTime? now = null) => Compile(QueryParser.Parse(text, now));

    public static CompiledQuery Compile(SearchQuery q)
    {
        var groups = new List<TermMatcher[]>();
        foreach (var g in q.Groups)
        {
            var matchers = new List<TermMatcher>();
            foreach (var term in g)
            {
                try
                {
                    matchers.Add(TermMatcher.Create(term, term.Mode ?? q.DefaultMode));
                }
                catch (ArgumentException ex)
                {
                    q.Errors.Add($"正規表現が不正です: {term.Text} ({ex.Message})");
                }
            }
            groups.Add(matchers.ToArray());
        }
        var exts = q.Extensions.Select(e => "." + e).ToArray();
        string? rank = q.Groups.SelectMany(g => g).FirstOrDefault(t => !t.Negate && !t.OnPath && (t.Mode ?? q.DefaultMode) is MatchMode.Substring or MatchMode.Prefix or MatchMode.Exact)?.Text;
        return new CompiledQuery(q, groups.ToArray(), exts, rank);
    }

    /// <summary>名前・種別・拡張子・検索語で照合する。path: 語がある場合のみ paths からパスを取得する。</summary>
    public bool Match(string name, bool isDirectory, IPathSource? paths = null, int id = -1)
    {
        if (Kind == KindFilter.Folders && !isDirectory) return false;
        if (Kind == KindFilter.Files && isDirectory) return false;
        if (_extSuffixes.Length > 0)
        {
            if (isDirectory) return false;
            bool any = false;
            foreach (var suffix in _extSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            }
            if (!any) return false;
        }
        if (_groups.Length == 0) return true;

        string? path = null;
        foreach (var group in _groups)
        {
            bool ok = true;
            foreach (var m in group)
            {
                string target;
                if (m.OnPath)
                {
                    if (paths == null) { ok = false; break; }
                    target = path ??= paths.GetPath(id);
                }
                else target = name;
                if (m.IsMatch(target) == m.Negate) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    public bool MatchMetadata(long size, DateTime? modifiedLocal)
    {
        var q = Source;
        if (q.MinSize is long min && size < min) return false;
        if (q.MaxSize is long max && size > max) return false;
        if (q.ModifiedFrom != null || q.ModifiedTo != null)
        {
            if (modifiedLocal is not DateTime m) return false;
            if (q.ModifiedFrom is DateTime f && m < f) return false;
            if (q.ModifiedTo is DateTime t && m >= t) return false;
        }
        return true;
    }

    /// <summary>並び順用の順位: 0=完全一致, 1=前方一致, 2=その他。</summary>
    public int Rank(string name)
    {
        if (_rankText == null) return 2;
        if (name.Equals(_rankText, StringComparison.OrdinalIgnoreCase)) return 0;
        // 拡張子を除いた名前の完全一致も 0 扱い
        int dot = name.LastIndexOf('.');
        if (dot > 0 && name.AsSpan(0, dot).Equals(_rankText, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(_rankText, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }
}

abstract class TermMatcher
{
    public bool Negate { get; private set; }
    public bool OnPath { get; private set; }
    public abstract bool IsMatch(string s);

    public static TermMatcher Create(SearchTerm term, MatchMode mode)
    {
        string text = term.Text;
        TermMatcher m = mode switch
        {
            MatchMode.Prefix => new PrefixMatcher(text),
            MatchMode.Exact => new ExactMatcher(text),
            MatchMode.Wildcard => new RegexMatcher(WildcardToRegex(text), LongestLiteral(text)),
            MatchMode.Regex => new RegexMatcher(text),
            MatchMode.Fuzzy => new AnySubstringMatcher(KanaConverter.FuzzyVariants(text)),
            _ => NeedsWidthVariant(text)
                ? new AnySubstringMatcher(new[] { text, text.Normalize(NormalizationForm.FormKC) })
                : new SubstringMatcher(text),
        };
        m.Negate = term.Negate;
        m.OnPath = term.OnPath;
        return m;
    }

    static bool NeedsWidthVariant(string s) => !s.Equals(s.Normalize(NormalizationForm.FormKC), StringComparison.Ordinal);

    /// <summary>ワイルドカードを含まない最長の固定部分（正規表現の前に Contains で高速に絞り込むため）。</summary>
    static string? LongestLiteral(string pattern)
    {
        string best = pattern.Split('*', '?').MaxBy(s => s.Length) ?? "";
        return best.Length >= 2 ? best : null;
    }

    public static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (char c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.Append('$').ToString();
    }
}

sealed class SubstringMatcher(string text) : TermMatcher
{
    public override bool IsMatch(string s) => s.Contains(text, StringComparison.OrdinalIgnoreCase);
}

sealed class PrefixMatcher(string text) : TermMatcher
{
    public override bool IsMatch(string s) => s.StartsWith(text, StringComparison.OrdinalIgnoreCase);
}

sealed class ExactMatcher(string text) : TermMatcher
{
    public override bool IsMatch(string s) => s.Equals(text, StringComparison.OrdinalIgnoreCase);
}

sealed class AnySubstringMatcher(IReadOnlyList<string> variants) : TermMatcher
{
    readonly string[] _variants = variants.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public override bool IsMatch(string s)
    {
        foreach (var v in _variants)
            if (s.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

sealed class RegexMatcher(string pattern, string? requiredLiteral = null) : TermMatcher
{
    readonly Regex _regex = new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    public override bool IsMatch(string s)
    {
        if (requiredLiteral != null && !s.Contains(requiredLiteral, StringComparison.OrdinalIgnoreCase)) return false;
        try { return _regex.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
