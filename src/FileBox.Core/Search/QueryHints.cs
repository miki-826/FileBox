namespace FileBox.Core.Search;

/// <summary>検索欄の候補 1 つ。Query は選んだときの検索欄の文字列、Label は表示、Description は説明。</summary>
public sealed record QueryHint(string Query, string Label, string Description);

/// <summary>
/// 検索の書き方の候補（02_機能設計 §3.4）。最後の語が条件の書きかけ（「ex」「size:」など）なら、その続きと例を出す。
/// </summary>
public static class QueryHints
{
    static readonly (string Key, string Description, string[] Examples)[] Keys =
    {
        ("ext:", "拡張子で絞る（カンマで複数）", new[] { "pdf", "docx,xlsx", "jpg,png" }),
        ("size:", "サイズで絞る", new[] { ">100MB", "<1MB", "10MB..1GB" }),
        ("modified:", "更新日時で絞る", new[] { "today", "yesterday", "thisweek", "thismonth", "lastmonth", "2026-10", ">=2026-01-01" }),
        ("folder:", "フォルダーだけ", Array.Empty<string>()),
        ("file:", "ファイルだけ", Array.Empty<string>()),
        ("in:", "このフォルダーの中だけ", Array.Empty<string>()),
        ("path:", "パスに含む語", Array.Empty<string>()),
        ("mode:", "一致のしかた", new[] { "prefix", "exact", "wildcard", "regex", "fuzzy" }),
        ("tag:", "FileBox のタグを付けたもの", Array.Empty<string>()),
        ("is:", "星を付けたもの", new[] { "starred" }),
        ("content:", "ファイルの中身（本文検索をオンにしたとき）", Array.Empty<string>()),
        ("kind:", "種類で探す（ゲームのフォルダ、メモらしいファイルなど）", new[] { "ゲーム", "メモ", "写真", "請求書", "インストーラー" }),
    };

    static readonly Dictionary<string, string> ExampleText = new()
    {
        ["today"] = "今日", ["yesterday"] = "昨日", ["thisweek"] = "今週", ["thismonth"] = "今月", ["lastmonth"] = "先月",
        ["2026-10"] = "その月", [">=2026-01-01"] = "その日以降",
        [">100MB"] = "100MB より大きい", ["<1MB"] = "1MB 未満", ["10MB..1GB"] = "10MB〜1GB",
        ["starred"] = "星付き",
        ["prefix"] = "前方一致", ["exact"] = "完全一致", ["wildcard"] = "* と ?", ["regex"] = "正規表現", ["fuzzy"] = "あいまい（ローマ字・かな）",
    };

    public static IReadOnlyList<QueryHint> For(string text)
    {
        var hints = new List<QueryHint>();
        int space = text.LastIndexOf(' ');
        string head = space >= 0 ? text[..(space + 1)] : "";
        string last = text[(space + 1)..];
        if (last.Length == 0) return hints;
        if (last.StartsWith('-')) { head += "-"; last = last[1..]; }

        int colon = last.IndexOf(':');
        if (colon < 0)
        {
            // 条件名の書きかけ: 「ex」→ ext:
            foreach (var (key, description, _) in Keys)
                if (key.StartsWith(last, StringComparison.OrdinalIgnoreCase) && key.Length > last.Length)
                    hints.Add(new(head + key, key, description));
            return hints;
        }

        string name = last[..(colon + 1)].ToLowerInvariant();
        string value = last[(colon + 1)..];
        var entry = Keys.FirstOrDefault(k => k.Key == name || (name == "dm:" && k.Key == "modified:"));
        if (entry.Key == null) return hints;
        foreach (var example in entry.Examples)
        {
            if (!example.StartsWith(value, StringComparison.OrdinalIgnoreCase) || example.Length == value.Length) continue;
            hints.Add(new(head + name + example + " ", name + example, ExampleText.TryGetValue(example, out var t) ? t : entry.Description));
        }
        return hints;
    }
}
