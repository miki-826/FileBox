using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.Contracts;
using FileBox.Core.Search;

namespace FileBox.App.ViewModels;

public enum SearchSort { Relevance, Name, Location, Size, Modified }

/// <summary>
/// 検索結果区画（04_画面設計 S05）。名前は Indexer の索引から、tag: / is:starred は FileBox の印から、
/// content: と本文検索（オンのとき）は本文の索引から探す。結果の並べ替えと絞り込みのチップはこの区画の中で行う。
/// </summary>
public sealed partial class SearchPaneViewModel : PaneViewModel
{
    CancellationTokenSource? _cts;
    List<FileItem> _raw = new();

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

    [ObservableProperty]
    IReadOnlyList<Facet> _facets = Array.Empty<Facet>();

    [ObservableProperty]
    SearchSort _sort = SearchSort.Relevance;

    [ObservableProperty]
    bool _sortDescending;

    /// <summary>強調表示に使う解析済みのクエリ。</summary>
    public SearchQuery Parsed { get; private set; } = new();

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
        Parsed = QueryParser.Parse(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            SetResults(new());
            Summary = "";
            ErrorText = null;
            CanLoadMore = false;
            return;
        }
        IsSearching = true;
        try
        {
            if (Parsed.UsesLocalSources)
            {
                var local = await Task.Run(() => SearchLocal(query, Parsed, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;
                SetResults(local);
                Summary = $"{local.Count:N0} 件" + (Parsed.ContentTerms.Count > 0 && !ContentIndexer.Instance.Enabled ? "（本文検索はオフです。設定でオンにしてください）" : "");
                ErrorText = Parsed.Errors.FirstOrDefault();
                CanLoadMore = false;
                return;
            }
            // 「ゲーム」「メモ」など種類の語だけなら、名前の一致に加えて種類として探す（kind:メモ なら種類としてだけ）
            var concept = ConceptSearch.Find(query);
            bool conceptOnly = concept != null && query.Contains(':');
            var result = conceptOnly ? new SearchResult() : await IndexClient.Instance.SearchAsync(query, _limit, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (result == null)
            {
                ErrorText = "インデックスに接続できません。Indexer を起動しています…";
                return;
            }
            string? queryError = IndexClient.Instance.LastQueryError;
            var items = result.Items.Select(i => new FileItem
            {
                Name = i.Name,
                FullPath = i.Path,
                IsDirectory = i.IsDirectory,
                Size = i.Size,
                Modified = i.Modified,
            }).ToList();
            int conceptHits = 0;
            if (concept != null)
            {
                var related = await SearchConceptAsync(concept, cts.Token);
                if (cts.IsCancellationRequested) return;
                var known = related.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                items = related.Concat(items.Where(i => !known.Contains(i.FullPath))).ToList();
                conceptHits = related.Count;
            }
            // 本文検索がオンなら、名前に一致しなかったが本文に一致したものを後ろに足す
            int contentHits = 0;
            if (ContentIndexer.Instance.Enabled && PlainTerms(query) is { Length: > 0 } words)
            {
                var known = items.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hits = await Task.Run(() => ContentIndexer.Instance.Search(words, 200), cts.Token);
                foreach (var hit in hits.Where(h => !known.Contains(h.Path) && File.Exists(h.Path)))
                {
                    items.Add(FromPath(hit.Path, hit.Snippet));
                    contentHits++;
                }
            }
            if (cts.IsCancellationRequested) return;
            SetResults(items);
            Summary = concept == null ? $"{result.Total:N0} 件 ({result.ElapsedMs:F0} ms)"
                : conceptOnly ? $"種類「{concept.Name}」として {conceptHits:N0} 件"
                : $"種類「{concept.Name}」として {conceptHits:N0} 件 ・ 名前に一致 {result.Total:N0} 件 ({result.ElapsedMs:F0} ms)";
            if (!conceptOnly && result.Truncated) Summary += $" ・ 上位 {result.Items.Count:N0} 件を表示";
            if (contentHits > 0) Summary += $" ・ 本文に一致 {contentHits:N0} 件";
            ErrorText = queryError;
            CanLoadMore = result.Truncated && _limit < 10000;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorText = Core.Files.FriendlyError.Explain(ex).ToString(); AppLog.Error("検索に失敗しました", ex); }
        finally
        {
            if (_cts == cts) IsSearching = false;
        }
    }

    /// <summary>
    /// 種類として探す。名前に関係する語を含む項目（Windows や AppData の奥は下げる）、ゲームのライブラリ（Steam・Epic など）の直下のフォルダ、
    /// 印になる項目（.obsidian・.git）を含むフォルダ。関係の強い順に最大 300 件。
    /// </summary>
    static async Task<List<FileItem>> SearchConceptAsync(Concept concept, CancellationToken ct)
    {
        var hits = new Dictionary<string, (FileItem Item, double Score)>(StringComparer.OrdinalIgnoreCase);
        void Add(FileItem item, double score)
        {
            if (!hits.TryGetValue(item.FullPath, out var old) || old.Score < score) hits[item.FullPath] = (item, score);
        }
        var libraries = new List<(string Path, string Label)>();
        var owners = new List<(string Path, string Label)>();
        foreach (var q in concept.IndexQueries())
        {
            SearchResult? result;
            try { result = await IndexClient.Instance.SearchAsync(q, 2000, ct); }
            catch (InvalidOperationException ex) { AppLog.Error($"種類の検索に失敗しました: {q}", ex); continue; }
            if (result == null) break;
            foreach (var i in result.Items)
            {
                if (i.IsDirectory && concept.LibraryLabel(i.Path) is { } library) libraries.Add((i.Path, library));
                else if (concept.MarkerLabel(i.Name) is { } marker && Path.GetDirectoryName(i.Path) is { } owner) owners.Add((owner, marker));
                else if (!concept.IsInsideLibrary(i.Path) && concept.Score(i.Name, i.Path, i.IsDirectory) is var score and > 0)
                    Add(new FileItem { Name = i.Name, FullPath = i.Path, IsDirectory = i.IsDirectory, Size = i.Size, Modified = i.Modified }, score);
            }
        }
        // ライブラリの直下（1 フォルダ = 1 本のゲーム）と、印を含むフォルダはディスクから情報を取る
        await Task.Run(() =>
        {
            foreach (var (path, label) in libraries)
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                        if ((File.GetAttributes(dir) & FileAttributes.Hidden) == 0) Add(FromPath(dir, $"{concept.Name}（{label}）"), 2);
                }
                catch (Exception) { }
            }
            foreach (var (path, label) in owners)
                if (Directory.Exists(path)) Add(FromPath(path, label), 1.5);
        }, ct);
        return hits.Values.OrderByDescending(h => h.Score).ThenBy(h => h.Item.Name, NaturalComparer.Instance).Take(300).Select(h => h.Item).ToList();
    }

    /// <summary>キー（ext: など）を除いた、ふつうの語だけ（本文検索に使う）。</summary>
    static string PlainTerms(string query) =>
        string.Join(' ', QueryParser.Tokenize(query).Where(t => t.Key == null && !t.Value.StartsWith('-') && t.Value is not ("|" or "OR")).Select(t => t.Value));

    /// <summary>タグ・星・本文から探し、残りの条件（名前・種類・サイズ・日時）で絞る。</summary>
    static List<FileItem> SearchLocal(string query, SearchQuery q, CancellationToken ct)
    {
        IEnumerable<(string Path, string? Snippet)> candidates;
        if (q.ContentTerms.Count > 0)
            candidates = ContentIndexer.Instance.Search(string.Join(' ', q.ContentTerms), 1000).Select(h => (h.Path, (string?)h.Snippet));
        else
        {
            var set = q.Starred ? UserMarks.StarredPaths().ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var tag in q.Tags)
            {
                var tagged = UserMarks.PathsWithTag(tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
                set = set == null ? tagged : set.Intersect(tagged, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            candidates = (set ?? new()).Select(p => (p, (string?)null));
        }
        var compiled = CompiledQuery.Compile(q);
        var list = new List<FileItem>();
        foreach (var (path, snippet) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (q.InPath != null && !(path + "\\").StartsWith(q.InPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var item = FromPath(path, snippet);
            if (!compiled.Match(item.Name, item.IsDirectory)) continue;
            if (compiled.NeedsMetadata && !compiled.MatchMetadata(item.Size, item.Modified)) continue;
            list.Add(item);
        }
        return list;
    }

    static FileItem FromPath(string path, string? snippet)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return new FileItem
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = info is DirectoryInfo,
            Size = info is FileInfo f && f.Exists ? f.Length : 0,
            Modified = info.Exists ? info.LastWriteTime : null,
            Attributes = info.Exists ? info.Attributes : 0,
            Snippet = snippet,
        };
    }

    void SetResults(List<FileItem> items)
    {
        _raw = items;
        Facets = SearchFacets.Build(items.Select(i => new FacetInput(i.Name, i.IsDirectory, i.Size, i.Modified)).ToList(), DateTime.Now);
        ApplySort();
    }

    /// <summary>並べ替え（見出しを押すたびに昇順・降順が切り替わる。「関連度」は索引の順）。</summary>
    public void SetSort(SearchSort sort)
    {
        if (Sort == sort) SortDescending = !SortDescending;
        else
        {
            Sort = sort;
            SortDescending = sort is SearchSort.Size or SearchSort.Modified;
        }
        ApplySort();
    }

    void ApplySort()
    {
        IEnumerable<FileItem> items = _raw;
        var natural = NaturalComparer.Instance;
        items = Sort switch
        {
            SearchSort.Name => SortDescending ? items.OrderByDescending(i => i.Name, natural) : items.OrderBy(i => i.Name, natural),
            SearchSort.Location => SortDescending ? items.OrderByDescending(i => i.Location, natural) : items.OrderBy(i => i.Location, natural),
            SearchSort.Size => SortDescending ? items.OrderByDescending(i => i.Size) : items.OrderBy(i => i.Size),
            SearchSort.Modified => SortDescending ? items.OrderByDescending(i => i.Modified) : items.OrderBy(i => i.Modified),
            _ => items,
        };
        Results = new ObservableCollection<FileItem>(items);
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
