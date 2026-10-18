namespace FileBox.Core.Search;

public enum MatchMode { Substring, Prefix, Exact, Wildcard, Regex, Fuzzy }

public enum KindFilter { All, Files, Folders }

/// <summary>検索語1つ。Mode が null のときはクエリ全体の既定モード（mode: / チップ）に従う。</summary>
public sealed record SearchTerm(string Text, MatchMode? Mode, bool Negate, bool OnPath);

/// <summary>解析済みクエリ（02_機能設計 §3.4）。Groups は「AND のグループ」を OR で結んだもの。</summary>
public sealed class SearchQuery
{
    public List<List<SearchTerm>> Groups { get; } = new() { new() };
    public KindFilter Kind { get; set; }
    public MatchMode DefaultMode { get; set; } = MatchMode.Substring;
    public bool DefaultModeExplicit { get; set; }
    public List<string> Extensions { get; } = new();
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    /// <summary>更新日時の範囲 [From, To)（ローカル時刻）。</summary>
    public DateTime? ModifiedFrom { get; set; }
    public DateTime? ModifiedTo { get; set; }
    public string? InPath { get; set; }
    public List<string> Errors { get; } = new();

    public bool HasTerms => Groups.Any(g => g.Count > 0);

    public bool HasFilters =>
        Kind != KindFilter.All || Extensions.Count > 0 || MinSize != null || MaxSize != null ||
        ModifiedFrom != null || ModifiedTo != null || InPath != null;

    public bool IsEmpty => !HasTerms && !HasFilters;

    public bool NeedsMetadata => MinSize != null || MaxSize != null || ModifiedFrom != null || ModifiedTo != null;
}
