using FileBox.Core.Search;

namespace FileBox.App.Models;

/// <summary>検索候補の表示。ファイル名とパスは表示言語による置き換えの対象にしない。</summary>
public sealed class SearchSuggestionItem(SearchSuggestion suggestion, bool showGroup)
{
    public SearchSuggestion Suggestion { get; } = suggestion;
    public string Title => Suggestion.Title;
    public string Detail => Suggestion.Kind == SearchSuggestionKind.Filter ? Services.Text.T(Suggestion.Detail) : Suggestion.Detail;
    public bool IsLocation => Suggestion.Kind is SearchSuggestionKind.File or SearchSuggestionKind.Folder;
    public bool IsDirectory => Suggestion.Kind == SearchSuggestionKind.Folder;
    public string ItemPath => Suggestion.Path ?? "";
    public string Extension => IsDirectory ? "" : Path.GetExtension(ItemPath).ToLowerInvariant();
    public string Glyph => Suggestion.Kind == SearchSuggestionKind.History ? "\uE81C" : "\uE71C";
    public string Group => !showGroup ? "" : Suggestion.Kind switch
    {
        SearchSuggestionKind.File or SearchSuggestionKind.Folder => "もしかしてこれ？",
        SearchSuggestionKind.History => "検索履歴",
        _ => "検索条件",
    };
    public override string ToString() => Title;
}
