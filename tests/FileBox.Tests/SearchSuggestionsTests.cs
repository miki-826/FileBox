using FileBox.Core.Search;
using Xunit;

namespace FileBox.Tests;

public sealed class SearchSuggestionsTests
{
    [Fact]
    public void KeepsRelatedFilesInOrderWithDistinctPathsAndSmallList()
    {
        var matches = new[]
        {
            new SearchCandidate("請求書.pdf", @"C:\仕事\請求書.pdf", false),
            new SearchCandidate("同じファイル", @"c:\仕事\請求書.pdf", false),
            new SearchCandidate("請求書", @"C:\仕事\請求書", true),
            new SearchCandidate("請求書2.pdf", @"C:\仕事\請求書2.pdf", false),
            new SearchCandidate("請求書3.pdf", @"C:\仕事\請求書3.pdf", false),
            new SearchCandidate("請求書4.pdf", @"C:\仕事\請求書4.pdf", false),
        };
        var result = SearchSuggestions.Build("請求", matches, new[] { "請求", "請求 ext:pdf", "請求 modified:today", "別の検索" });
        Assert.Equal(SearchSuggestions.Limit, result.Count);
        Assert.Equal(new[] { "請求書.pdf", "請求書", "請求書2.pdf", "請求書3.pdf" }, result.Take(4).Select(s => s.Title));
        Assert.Equal(SearchSuggestionKind.Folder, result[1].Kind);
        Assert.Equal(@"C:\仕事", result[0].Detail);
        Assert.Equal(SearchSuggestionKind.History, result[4].Kind);
        Assert.All(result.Take(4), s => Assert.Equal("請求", s.Query));
    }

    [Fact]
    public void EmptyInputShowsOnlyRecentQueriesAndNeverListsFiles()
    {
        var matches = new[] { new SearchCandidate("a", @"C:\a", false) };
        var result = SearchSuggestions.Build(" ", matches, new[] { "", "a", "A", "b", "c", "d", "e", "f" });
        Assert.Equal(5, result.Count);
        Assert.All(result, s => Assert.Equal(SearchSuggestionKind.History, s.Kind));
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, result.Select(s => s.Query));
    }

    [Fact]
    public void RetainsFilterCompletionAndTheOriginalSearchScope()
    {
        const string query = "報告 in:\"C:\\仕事\" ex";
        var result = SearchSuggestions.Build(query, new[] { new SearchCandidate("報告.xlsx", @"C:\仕事\報告.xlsx", false) }, Array.Empty<string>());
        Assert.Equal(SearchSuggestionKind.Filter, result[0].Kind);
        Assert.Equal("報告 in:\"C:\\仕事\" ext:", result[0].Query);
        Assert.Equal(query, result[1].Query);
        Assert.Equal(@"C:\仕事\報告.xlsx", result[1].Path);
    }

    [Fact]
    public void NormalizesHistoryButDoesNotTreatTypedTextAsACompletedChoice()
    {
        var result = SearchSuggestions.Build("れぽ", Array.Empty<SearchCandidate>(), new[] { "レポ", "レポート ext:pdf", "ほか" });
        Assert.Equal("レポート ext:pdf", Assert.Single(result).Query);
        Assert.Empty(SearchSuggestions.Build("一致なし", Array.Empty<SearchCandidate>(), new[] { "別の検索" }));
    }

    [Fact]
    public void KeepsExecutableNamesAndSyntaxCharactersAsPaths()
    {
        var result = SearchSuggestions.Build("tool", new[] { new SearchCandidate("tool [1].exe", @"C:\tool [1].exe", false) }, Array.Empty<string>());
        var suggestion = Assert.Single(result);
        Assert.Equal(@"C:\", suggestion.Detail);
        Assert.Equal(@"C:\tool [1].exe", suggestion.Path);
        Assert.Equal("tool", suggestion.Query);
    }
}
