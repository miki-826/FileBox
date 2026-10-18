using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;

namespace FileBox.App.ViewModels;

/// <summary>検索結果区画（04_画面設計 S05）。</summary>
public sealed partial class SearchPaneViewModel : PaneViewModel
{
    CancellationTokenSource? _cts;

    public override PaneKind Kind => PaneKind.Search;

    [ObservableProperty]
    string _query = "";

    [ObservableProperty]
    ObservableCollection<FileItem> _results = new();

    [ObservableProperty]
    string _summary = "";

    [ObservableProperty]
    string? _errorText;

    [ObservableProperty]
    bool _isSearching;

    [ObservableProperty]
    bool _canLoadMore;

    int _limit = 500;

    /// <summary>検索バーへの入力に合わせて自動で作られた区画か（クエリが空になったら閉じる）。</summary>
    public bool AutoCreated { get; set; }

    public SearchPaneViewModel(string query, string? id = null)
    {
        if (id != null) Id = id;
        Query = query;
        Title = "検索";
        if (query.Length > 0) _ = RunAsync(query);
    }

    public async Task RunAsync(string query, bool loadMore = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (!loadMore) _limit = 500;
        Query = query;
        Title = string.IsNullOrWhiteSpace(query) ? "検索" : $"検索: {query}";
        if (string.IsNullOrWhiteSpace(query))
        {
            Results = new();
            Summary = "";
            ErrorText = null;
            CanLoadMore = false;
            return;
        }
        IsSearching = true;
        try
        {
            var result = await IndexClient.Instance.SearchAsync(query, _limit, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (result == null)
            {
                ErrorText = "インデックスに接続できません。Indexer を起動しています…";
                return;
            }
            Results = new ObservableCollection<FileItem>(result.Items.Select(i => new FileItem
            {
                Name = i.Name,
                FullPath = i.Path,
                IsDirectory = i.IsDirectory,
                Size = i.Size,
                Modified = i.Modified,
            }));
            Summary = $"{result.Total:N0} 件 ({result.ElapsedMs:F0} ms)" + (result.Truncated ? $" ・ 上位 {result.Items.Count:N0} 件を表示" : "");
            ErrorText = IndexClient.Instance.LastQueryError;
            CanLoadMore = result.Truncated && _limit < 10000;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally
        {
            if (_cts == cts) IsSearching = false;
        }
    }

    public Task LoadMoreAsync()
    {
        _limit = Math.Min(10000, _limit * 4);
        return RunAsync(Query, loadMore: true);
    }

    public void OnSelectionChanged(FileItem? item) => RaiseSelectionChanged(item);

    public override JsonObject Serialize() => new()
    {
        ["pane"] = "search",
        ["id"] = Id,
        ["query"] = Query,
    };

    public override void Dispose() => _cts?.Cancel();
}
