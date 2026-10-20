using FileBox.Core.Tools;

namespace FileBox.Core.Search;

/// <summary>結果の絞り込み候補 1 つ。Key / Value は QueryEditor.SetValue に渡す（例: ext / pdf）。</summary>
public sealed record Facet(string Group, string Label, string Key, string Value, int Count);

/// <summary>検索結果の 1 件（集計に必要な情報だけ）。</summary>
public readonly record struct FacetInput(string Name, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>
/// 検索結果から「種類・更新日時・サイズ」の集計を作る（02_機能設計 §3 検索結果の絞り込み）。
/// チップを押すとクエリに ext: / modified: / size: を足して検索し直す。
/// </summary>
public static class SearchFacets
{
    // 画像・動画・音声は変換機能と同じ分類を使う。検索条件には分類全体の拡張子を渡す。
    static readonly (string Label, string[] Extensions)[] FileKinds =
    {
        ("画像", ToolCatalog.Get(ToolKind.ImageConvert).Extensions),
        ("動画", ToolCatalog.Get(ToolKind.VideoConvert).Extensions),
        ("音声", ToolCatalog.Get(ToolKind.AudioConvert).Extensions),
        ("Office", new[] { "doc", "docx", "docm", "xls", "xlsx", "xlsm", "ppt", "pptx", "pptm" }),
        ("PDF", new[] { "pdf" }),
    };

    static (string Label, string Value) FileKind(string extension)
    {
        foreach (var (label, extensions) in FileKinds)
            if (extensions.Contains(extension)) return (label, string.Join(',', extensions));
        return ("." + extension, extension);
    }

    static readonly (string Label, string Value, Func<DateTime, DateTime, bool> Match)[] Dates =
    {
        ("今日", "today", (d, today) => d >= today),
        ("今週", "thisweek", (d, today) => d >= today.AddDays(-(((int)today.DayOfWeek + 6) % 7))),
        ("今月", "thismonth", (d, today) => d >= new DateTime(today.Year, today.Month, 1)),
        ("今年", "thisyear", (d, today) => d >= new DateTime(today.Year, 1, 1)),
    };

    static readonly (string Label, string Value, long Min, long Max)[] Sizes =
    {
        ("100KB 未満", "<100KB", 0, 100L << 10),
        ("100KB〜10MB", "100KB..10MB", 100L << 10, 10L << 20),
        ("10MB〜1GB", "10MB..1GB", 10L << 20, 1L << 30),
        ("1GB 以上", ">=1GB", 1L << 30, long.MaxValue),
    };

    /// <summary>items の集計。種類は多い順に maxKinds 個まで（既定は全種類）。件数 0 や、全件が当てはまる候補は出さない。</summary>
    public static IReadOnlyList<Facet> Build(IReadOnlyList<FacetInput> items, DateTime today, int maxKinds = int.MaxValue)
    {
        var list = new List<Facet>();
        if (items.Count == 0) return list;
        today = today.Date;

        int folders = items.Count(i => i.IsDirectory);
        var kinds = items.Where(i => !i.IsDirectory)
            .Select(i => System.IO.Path.GetExtension(i.Name).TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(FileKind)
            .Where(g => g.Count() < items.Count)
            .Select(g => new Facet("種類", g.Key.Label, "ext", g.Key.Value, g.Count())).ToList();
        if (folders > 0 && folders < items.Count) kinds.Add(new("種類", "フォルダー", "folder", "", folders));
        list.AddRange(kinds.OrderByDescending(f => f.Count).ThenBy(f => f.Label, StringComparer.Ordinal).Take(maxKinds));

        foreach (var (label, value, match) in Dates)
        {
            int n = items.Count(i => i.Modified is DateTime d && match(d, today));
            if (n > 0 && n < items.Count) list.Add(new("更新日時", label, "modified", value, n));
        }

        foreach (var (label, value, min, max) in Sizes)
        {
            int n = items.Count(i => !i.IsDirectory && i.Size >= min && i.Size < max);
            if (n > 0 && n < items.Count) list.Add(new("サイズ", label, "size", value, n));
        }
        return list;
    }

    /// <summary>クエリにチップの条件を足す（同じ種類の条件は置き換える）。</summary>
    public static string Apply(string query, Facet facet) => facet.Key switch
    {
        "folder" => QueryEditor.SetKind(query, KindFilter.Folders),
        _ => QueryEditor.SetValue(query, facet.Key, facet.Value),
    };
}
