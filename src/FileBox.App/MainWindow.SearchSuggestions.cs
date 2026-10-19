using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.Core.Search;
using FileBox.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileBox.App;

public sealed partial class MainWindow
{
    CancellationTokenSource? _suggestionsCts;
    bool _searchFocused;
    bool _suggestionsDismissed;

    void ShowSearchSuggestions(string text, IEnumerable<SearchCandidate>? matches = null)
    {
        if (!_searchFocused || _suggestionsDismissed) return;
        var suggestions = SearchSuggestions.Build(text, matches ?? Array.Empty<SearchCandidate>(), _store.GetSearchHistory(30));
        bool locationsShown = false;
        var groups = new HashSet<SearchSuggestionKind>();
        SearchBox.ItemsSource = suggestions.Select(s =>
        {
            bool isLocation = s.Kind is SearchSuggestionKind.File or SearchSuggestionKind.Folder;
            bool first = isLocation ? !locationsShown : groups.Add(s.Kind);
            locationsShown |= isLocation;
            return new SearchSuggestionItem(s, first);
        }).ToList();
        SearchBox.IsSuggestionListOpen = suggestions.Count > 0;
    }

    async Task RefreshSearchSuggestionsAsync()
    {
        _suggestionsCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _suggestionsCts = cts;
        string query = SearchBox.Text;
        try
        {
            var parsed = QueryParser.Parse(query);
            // タグ・本文などのローカル検索は、検索確定後の結果ペインに任せる。
            if (!_searchFocused || _suggestionsDismissed || parsed.IsEmpty || parsed.UsesLocalSources || parsed.Errors.Count > 0) return;
            var result = await IndexClient.Instance.SearchAsync(query, 16, cts.Token);
            if (cts.IsCancellationRequested || !_searchFocused || _suggestionsDismissed || SearchBox.Text != query) return;
            var candidates = result?.Items.Where(i => PolicyService.Instance.Can(Permission.Read, i.Path))
                .Select(i => new SearchCandidate(i.Name, i.Path, i.IsDirectory));
            ShowSearchSuggestions(query, candidates);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("検索候補を取得できませんでした", ex); }
        finally { if (ReferenceEquals(_suggestionsCts, cts)) _suggestionsCts = null; }
    }

    void CancelSearchSuggestions(bool close = true)
    {
        _searchTimer.Stop();
        _suggestionsCts?.Cancel();
        _suggestionsDismissed = true;
        if (close) SearchBox.IsSuggestionListOpen = false;
    }

    void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _searchFocused = false;
        // クリックした候補は QuerySubmitted が受け取るため、ItemsSource はここで消さない。
        CancelSearchSuggestions(close: false);
    }

    void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) CancelSearchSuggestions();
    }

    bool ChooseSearchLocation(SearchSuggestionItem item)
    {
        string? path = item.Suggestion.Path;
        if (path == null) return false;
        CancelSearchSuggestions();
        if (!PolicyService.Instance.Can(Permission.Read, path)) return true;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ShowInfo("候補のファイルが見つかりません。もう一度検索してください。", InfoBarSeverity.Informational);
            return true;
        }
        _store.AddSearchHistory(SearchBox.Text);
        // 実行ファイルを含め、候補からは起動せず場所を開いて選択する。
        NavigateActive(path);
        return true;
    }
}
