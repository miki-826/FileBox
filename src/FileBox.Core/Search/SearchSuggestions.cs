namespace FileBox.Core.Search;

public enum SearchSuggestionKind { File, Folder, History, Filter }
public sealed record SearchCandidate(string Name, string Path, bool IsDirectory);
public sealed record SearchSuggestion(SearchSuggestionKind Kind, string Title, string Detail, string Query, string? Path = null);

/// <summary>検索欄の小さな候補一覧。索引の関連順を保ち、履歴と条件のヒントも少数だけ添える。</summary>
public static class SearchSuggestions
{
    public const int Limit = 6;

    public static IReadOnlyList<SearchSuggestion> Build(string text, IEnumerable<SearchCandidate> matches, IEnumerable<string> history)
    {
        var result = new List<SearchSuggestion>();
        string query = text.Trim();
        if (query.Length == 0)
            return history.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5)
                .Select(h => new SearchSuggestion(SearchSuggestionKind.History, h, "", h)).ToList();

        // 条件を入力しているときは、その続きを優先して残す。
        var hint = QueryHints.For(text).FirstOrDefault();
        if (hint != null) result.Add(new(SearchSuggestionKind.Filter, hint.Label, hint.Description, hint.Query));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (string.IsNullOrWhiteSpace(match.Path) || !paths.Add(match.Path.TrimEnd('\\'))) continue;
            result.Add(new(match.IsDirectory ? SearchSuggestionKind.Folder : SearchSuggestionKind.File,
                match.Name, Parent(match.Path), text, match.Path));
            if (paths.Count == 4) break;
        }
        string normalized = Normalize(query);
        foreach (string previous in history.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= Limit) break;
            if (Normalize(previous) == normalized || !Normalize(previous).StartsWith(normalized, StringComparison.Ordinal)) continue;
            result.Add(new(SearchSuggestionKind.History, previous, "", previous));
        }
        return result;
    }

    static string Normalize(string text) => KanaConverter.KatakanaToHiragana(text.Normalize(System.Text.NormalizationForm.FormKC)).ToUpperInvariant();

    static string Parent(string path)
    {
        int separator = path.TrimEnd('\\').LastIndexOf('\\');
        return separator < 0 ? path : path[..(separator + (separator == 2 ? 1 : 0))];
    }
}
