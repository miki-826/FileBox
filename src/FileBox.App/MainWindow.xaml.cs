using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.App.Views;
using FileBox.Contracts;
using FileBox.Core.Links;
using FileBox.Core.Search;
using FileBox.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FileBox.App;

public sealed partial class MainWindow : Window, IWorkspaceActions
{
    sealed record TabEntry(TabViewModel Model, TabViewItem Item, PaneHost Host);

    readonly UserStore _store = new();
    readonly List<TabEntry> _tabs = new();
    readonly DispatcherQueueTimer _searchTimer;
    readonly DispatcherQueueTimer _statusTimer;
    bool _syncingChips;
    FolderPaneViewModel? _statusSource;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(150);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => RunSearchInCurrentTab(SearchBox.Text);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += async (_, _) => await RefreshIndexStatusAsync();
        _statusTimer.Start();

        FolderPaneViewModel.ShowHidden = _store.GetSetting("showHidden") == "1";
        SuggestionService.Initialize(_store);
        InboxService.Initialize(_store, DispatcherQueue);
        InboxService.Instance.CountChanged += UpdateInboxEntry;
        InitializeLinks();
        SettingsDialog.Store = _store;
        InitializePolicy();
        SaveDialogWatcher.Initialize(_store, DispatcherQueue);
        RegisterAccelerators();
        LoadSidebar();
        RestoreSession();
        Closed += OnClosed;
        _ = RefreshIndexStatusAsync();
    }

    TabEntry? CurrentTab => Tabs.SelectedItem is TabViewItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null;
    FolderPaneViewModel? ActiveFolder => CurrentTab?.Model.ActiveFolder;

    // ================================================================ タブ

    TabEntry AddTab(TabViewModel model, bool select = true, int? index = null)
    {
        var host = new PaneHost(model, this);
        var item = new TabViewItem
        {
            Content = host,
            IconSource = new FontIconSource { Glyph = "\uE8B7" },
        };
        item.SetBinding(TabViewItem.HeaderProperty, new Microsoft.UI.Xaml.Data.Binding { Source = model, Path = new PropertyPath(nameof(TabViewModel.Title)) });
        var entry = new TabEntry(model, item, host);
        _tabs.Add(entry);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.ActivePane) && ReferenceEquals(CurrentTab, entry)) OnActivePaneChanged();
        };
        if (index is int i && i <= Tabs.TabItems.Count) Tabs.TabItems.Insert(i, item);
        else Tabs.TabItems.Add(item);
        if (select) Tabs.SelectedItem = item;
        return entry;
    }

    public void OpenInNewTab(string path) => AddTab(new TabViewModel(new FolderPaneViewModel(path)));

    void CloseTab(TabEntry entry)
    {
        entry.Host.Detach();
        entry.Model.Dispose();
        _tabs.Remove(entry);
        Tabs.TabItems.Remove(entry.Item);
        if (_tabs.Count == 0) OpenInNewTab("");
    }

    void Tabs_AddTabButtonClick(TabView sender, object args) =>
        OpenInNewTab(ActiveFolder?.CurrentPath ?? "");

    void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var entry = _tabs.FirstOrDefault(t => t.Item == args.Tab);
        if (entry != null) CloseTab(entry);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnActivePaneChanged();

    void OnActivePaneChanged()
    {
        if (_statusSource != null) _statusSource.PropertyChanged -= OnStatusSourceChanged;
        _statusSource = CurrentTab?.Model.ActivePane as FolderPaneViewModel ?? ActiveFolder;
        if (_statusSource != null) _statusSource.PropertyChanged += OnStatusSourceChanged;
        UpdateStatusText();
        SyncInFolderChip();
    }

    void OnStatusSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderPaneViewModel.StatusText) or nameof(FolderPaneViewModel.CurrentPath)) UpdateStatusText();
        if (e.PropertyName == nameof(FolderPaneViewModel.CurrentPath)) SyncInFolderChip();
    }

    void UpdateStatusText()
    {
        var active = CurrentTab?.Model.ActivePane;
        StatusText.Text = active switch
        {
            SearchPaneViewModel s => s.Summary,
            _ when _statusSource != null => $"{_statusSource.StatusText}    {(_statusSource.IsPc ? "PC" : _statusSource.CurrentPath)}",
            _ => "",
        };
    }

    // ================================================================ 区画操作（IWorkspaceActions）

    public void ActivatePane(PaneViewModel pane)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(pane));
        if (tab != null && !ReferenceEquals(tab.Model.ActivePane, pane)) tab.Model.ActivePane = pane;
    }

    public void SplitPane(PaneViewModel pane, Orientation orientation)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(pane));
        if (tab == null) return;
        string path = (pane as FolderPaneViewModel)?.CurrentPath ?? tab.Model.ActiveFolder?.CurrentPath ?? "";
        if (!tab.Model.Split(pane, new FolderPaneViewModel(path), orientation))
            ShowInfo("1つのタブで分割できるのは 4 区画までです");
    }

    public void ClosePane(PaneViewModel pane)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(pane));
        if (tab == null) return;
        if (!tab.Model.Close(pane)) CloseTab(tab);
    }

    public void OpenInAdjacentPane(PaneViewModel from, string path)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(from));
        if (tab == null) return;
        var other = tab.Model.Panes.OfType<FolderPaneViewModel>().FirstOrDefault(p => !ReferenceEquals(p, from));
        if (other != null)
        {
            _ = other.NavigateAsync(path);
            tab.Model.ActivePane = other;
        }
        else if (!tab.Model.Split(from, new FolderPaneViewModel(path), Orientation.Horizontal))
        {
            OpenInNewTab(path);
        }
    }

    public void TogglePreview(PaneViewModel from)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(from));
        if (tab == null) return;
        var existing = tab.Model.Panes.OfType<PreviewPaneViewModel>().FirstOrDefault(p => p.LinkedTo == from.Id || p.LinkedTo == null);
        if (existing != null)
        {
            tab.Model.Close(existing);
            (tab.Host.ViewFor(from) as FolderPaneView)?.FocusList();
            return;
        }
        var preview = new PreviewPaneViewModel(from.Id);
        if (!tab.Model.Split(from, preview, Orientation.Horizontal, 0.6))
        {
            ShowInfo("区画が 4 つあるためプレビューを追加できません。区画を閉じてください");
            return;
        }
        // 現在の選択をすぐ表示
        var view = tab.Host.ViewFor(from);
        var list = (view as FrameworkElement)?.FindName("List") as ListView;
        if (list?.SelectedItems.Count == 1 && list.SelectedItem is FileItem item) preview.Item = item;
        tab.Model.ActivePane = from;
        (view as FolderPaneView)?.FocusList();
    }

    public void RevealInFolderPane(PaneViewModel from, string path)
    {
        var tab = _tabs.FirstOrDefault(t => t.Model.Panes.Contains(from)) ?? CurrentTab;
        if (tab == null) return;
        bool isDir = Directory.Exists(path);
        string folder = isDir ? path : Path.GetDirectoryName(path) ?? "";
        var target = tab.Model.Panes.OfType<FolderPaneViewModel>().FirstOrDefault();
        if (target == null)
        {
            target = new FolderPaneViewModel(folder);
            if (!tab.Model.Split(from, target, Orientation.Horizontal))
            {
                OpenInNewTab(folder);
                return;
            }
        }
        else _ = target.NavigateAsync(folder);
        if (!isDir) target.PendingSelection = path;
        tab.Model.ActivePane = target;
        _store.TouchRecent(path);
    }

    public void AddFavorite(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _store.AddFavorite(path);
        LoadSidebar();
    }

    /// <summary>コマンドライン引数・2つ目の起動から渡されたパスを開く。</summary>
    public void OpenFromArguments(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg == "--inbox") { OpenInbox(); continue; }
            if (arg == "--capacity") { OpenCapacity(); continue; }
            if (arg == "--admin") { OpenAdmin(); continue; }
            if (FileBoxUri.IsFileBoxUri(arg)) { HandleLink(arg); continue; }
            string? path = ShellService.ResolveAddress(arg);
            if (path == null || (path.Length > 0 && !Directory.Exists(path) && !File.Exists(path)))
            {
                // 仮想フォルダ（::{CLSID} など）や開けない場所はエクスプローラーに任せる（置き換え時の安全策 §7.3）
                if (arg.StartsWith("::") || arg.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) ShellService.OpenInExplorer(arg);
                continue;
            }
            var vm = new FolderPaneViewModel(path);
            AddTab(new TabViewModel(vm));
        }
    }

    // ================================================================ 検索

    void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) SearchBox.ItemsSource = _store.GetSearchHistory(10);
    }

    void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        SyncChipsFromQuery();
        if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen) return;
        sender.ItemsSource = sender.Text.Length == 0 ? _store.GetSearchHistory(10) : null;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        sender.Text = args.SelectedItem as string ?? "";
    }

    void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = args.ChosenSuggestion as string ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(query)) return;
        _store.AddSearchHistory(query);
        // Enter: 検索結果を新しいタブで開く（02_機能設計 §3 / 04_画面設計 S02）
        var auto = CurrentTab?.Model.Panes.OfType<SearchPaneViewModel>().FirstOrDefault(p => p.AutoCreated);
        if (auto != null && CurrentTab!.Model.PaneCount > 1) CurrentTab.Model.Close(auto);
        AddTab(new TabViewModel(new SearchPaneViewModel(query)));
        _searchTimer.Stop();
    }

    /// <summary>入力中は現在のタブに検索結果区画を出す（なければ右側に分割して追加）。</summary>
    void RunSearchInCurrentTab(string query)
    {
        var tab = CurrentTab;
        if (tab == null) return;
        var pane = tab.Model.Panes.OfType<SearchPaneViewModel>().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(query))
        {
            if (pane is { AutoCreated: true } && tab.Model.PaneCount > 1) tab.Model.Close(pane);
            else _ = pane?.RunAsync("");
            return;
        }
        if (pane == null)
        {
            pane = new SearchPaneViewModel("") { AutoCreated = true };
            var anchor = tab.Model.ActivePane is PreviewPaneViewModel ? tab.Model.Panes.First() : tab.Model.ActivePane;
            if (!tab.Model.Split(anchor, pane, Orientation.Horizontal, 0.45))
            {
                AddTab(new TabViewModel(pane));
            }
        }
        _ = pane.RunAsync(query);
    }

    // ---- チップ（クエリ文字列と双方向同期）

    void SetQuery(string query)
    {
        SearchBox.Text = query;
        _searchTimer.Stop();
        RunSearchInCurrentTab(query);
    }

    void SyncChipsFromQuery()
    {
        _syncingChips = true;
        var q = QueryParser.Parse(SearchBox.Text);
        FolderChip.IsChecked = q.Kind == KindFilter.Folders;
        FileChip.IsChecked = q.Kind == KindFilter.Files;
        ModeChip.Content = "一致: " + q.DefaultMode switch
        {
            MatchMode.Prefix => "前方一致",
            MatchMode.Exact => "完全一致",
            MatchMode.Wildcard => "ワイルドカード",
            MatchMode.Regex => "正規表現",
            MatchMode.Fuzzy => "あいまい",
            _ => "部分一致",
        };
        ExtChip.Content = q.Extensions.Count == 0 ? "拡張子" : "拡張子: " + string.Join(",", q.Extensions.Take(3)) + (q.Extensions.Count > 3 ? "…" : "");
        DateChip.Content = q.ModifiedFrom == null && q.ModifiedTo == null ? "期間" : $"期間: {q.ModifiedFrom:yyyy/MM/dd}〜{q.ModifiedTo?.AddDays(-1):yyyy/MM/dd}";
        SyncInFolderChip(q);
        _syncingChips = false;
    }

    void SyncInFolderChip(SearchQuery? q = null)
    {
        q ??= QueryParser.Parse(SearchBox.Text);
        InFolderChip.IsChecked = q.InPath != null;
        string? folder = ActiveFolder is { IsPc: false } f ? f.CurrentPath : null;
        string? scopeName = q.InPath == null ? null : Path.GetFileName(q.InPath.TrimEnd('\\')) is { Length: > 0 } n ? n : q.InPath;
        InFolderChip.Content = scopeName != null ? $"範囲: {scopeName}" : "このフォルダ内";
        InFolderChip.IsEnabled = q.InPath != null || folder != null;
    }

    void FolderChip_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingChips) return;
        SetQuery(QueryEditor.SetKind(SearchBox.Text, FolderChip.IsChecked == true ? KindFilter.Folders : KindFilter.All));
    }

    void FileChip_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingChips) return;
        SetQuery(QueryEditor.SetKind(SearchBox.Text, FileChip.IsChecked == true ? KindFilter.Files : KindFilter.All));
    }

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (QueryParser.TryParseMode((string)((FrameworkElement)sender).Tag, out var mode))
            SetQuery(QueryEditor.SetMode(SearchBox.Text, mode));
    }

    void Ext_Click(object sender, RoutedEventArgs e) =>
        SetQuery(QueryEditor.SetValue(SearchBox.Text, "ext", (string)((FrameworkElement)sender).Tag));

    void Date_Click(object sender, RoutedEventArgs e) =>
        SetQuery(QueryEditor.SetValue(SearchBox.Text, "modified", (string)((FrameworkElement)sender).Tag));

    void InFolderChip_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingChips) return;
        string? folder = InFolderChip.IsChecked == true && ActiveFolder is { IsPc: false } f ? f.CurrentPath : null;
        SetQuery(QueryEditor.SetInPath(SearchBox.Text, folder));
    }

    async void SaveSearch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) { ShowInfo("保存する検索条件を入力してください"); return; }
        string? name = await Dialogs.PromptAsync(Content.XamlRoot, "検索を保存", SearchBox.Text, "保存");
        if (name == null) return;
        _store.SaveSearch(name, SearchBox.Text);
        LoadSidebar();
    }

    async void SearchHelp_Click(object sender, RoutedEventArgs e) => await Dialogs.MessageAsync(Content.XamlRoot, "検索の書き方", """
        語を並べる …… AND（すべて含む）　例: 請求書 2026
        語1 | 語2 …… OR（どちらか）
        -語 …………… 除外
        ^abc ………… 前方一致（先頭が abc）
        "abc.txt" …… 完全一致
        *.pdf / a?c … ワイルドカード
        regex:^\d+$ … 正規表現
        ~seikyuu …… あいまい（ローマ字→かな、ひらがな⇔カタカナ）
        folder: / file: … フォルダのみ / ファイルのみ（folder:請求書 も可）
        ext:pdf,xlsx … 拡張子
        size:>1MB　size:10MB..1GB　size:<100KB
        modified:2026-10　modified:today / thisweek / thismonth / >2026-01-01 / 2025..2026-03
        in:"D:\書類" … そのフォルダ配下だけ
        path:書類\2026 … フルパスに含む
        mode:prefix …… 全体の既定の一致方法（prefix / exact / wildcard / regex / fuzzy）

        Enter で検索結果を新しいタブに開きます。
        """);

    // ================================================================ サイドバー

    void UpdateInboxEntry()
    {
        int n = InboxService.Instance.Items.Count;
        InboxList.ItemsSource = new List<SidebarItem>
        {
            new() { Name = n > 0 ? $"受け取りボックス（{n}）" : "受け取りボックス", Glyph = "", Kind = SidebarKind.Inbox },
        };
    }

    /// <summary>受け取りボックスを開く。表示中のタブにあればそこを、なければ新しいタブで。</summary>
    public void OpenInbox()
    {
        foreach (var tab in _tabs)
        {
            if (tab.Model.Panes.OfType<InboxPaneViewModel>().FirstOrDefault() is { } pane)
            {
                Tabs.SelectedItem = tab.Item;
                tab.Model.ActivePane = pane;
                return;
            }
        }
        AddTab(new TabViewModel(new InboxPaneViewModel()));
    }

    void LoadSidebar()
    {
        UpdateInboxEntry();
        QuickList.ItemsSource = ShellService.QuickAccess
            .Select(q => (q.Name, q.Glyph, Path: ShellService.KnownFolder(q.Id)))
            .Where(q => q.Path != null)
            .Select(q => new SidebarItem { Name = q.Name, Glyph = q.Glyph, Path = q.Path!, Kind = SidebarKind.Folder })
            // 仮想フォルダはエクスプローラーで開く（02_機能設計 §7.2）
            .Append(new SidebarItem { Name = "ネットワーク", Glyph = "", Path = "shell:NetworkPlacesFolder", Kind = SidebarKind.Folder })
            .Append(new SidebarItem { Name = "ごみ箱", Glyph = "", Path = "shell:RecycleBinFolder", Kind = SidebarKind.Folder })
            .ToList();
        FavoriteList.ItemsSource = _store.GetFavorites()
            .Select(f => new SidebarItem { Name = f.Label ?? (Path.GetFileName(f.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : f.Path), Glyph = "\uE734", Path = f.Path, Id = f.Id, Kind = SidebarKind.Favorite })
            .ToList();
        DriveList.ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d =>
        {
            string label = string.IsNullOrEmpty(d.VolumeLabel) ? "ローカル ディスク" : d.VolumeLabel;
            return new SidebarItem
            {
                Name = $"{label} ({d.Name.TrimEnd('\\')})",
                Glyph = "\uEDA2",
                Path = d.RootDirectory.FullName,
                Kind = SidebarKind.Drive,
                Usage = d.TotalSize > 0 ? 1.0 - (double)d.TotalFreeSpace / d.TotalSize : 0,
                Detail = $"空き {Format.HumanSize(d.TotalFreeSpace)} / {Format.HumanSize(d.TotalSize)}",
            };
        }).ToList();
        WorkspaceList.ItemsSource = _store.GetWorkspaces()
            .Select(w => new SidebarItem { Name = w.Name, Glyph = "\uE8A9", Id = w.Id, Kind = SidebarKind.Workspace })
            .ToList();
        SavedSearchList.ItemsSource = _store.GetSavedSearches()
            .Select(s => new SidebarItem { Name = s.Name, Glyph = "\uE721", Id = s.Id, Query = s.Query, Kind = SidebarKind.SavedSearch })
            .ToList();
    }

    void Sidebar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarItem item) return;
        switch (item.Kind)
        {
            case SidebarKind.Workspace:
                if (_store.GetWorkspace(item.Id) is { } ws) LoadLayout(ws.LayoutJson, replace: true);
                break;
            case SidebarKind.Inbox:
                OpenInbox();
                break;
            case SidebarKind.Zone:
                OpenCapacity(item.Path);
                break;
            case SidebarKind.SavedSearch:
                SearchBox.Text = item.Query ?? "";
                AddTab(new TabViewModel(new SearchPaneViewModel(item.Query ?? "")));
                break;
            default:
                if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) ShellService.OpenInExplorer(item.Path);
                else NavigateActive(item.Path);
                break;
        }
    }

    void NavigateActive(string path)
    {
        var folder = CurrentTab?.Model.ActivePane as FolderPaneViewModel ?? ActiveFolder;
        if (folder == null) { OpenInNewTab(path); return; }
        _ = folder.NavigateAsync(path);
        CurrentTab!.Model.ActivePane = folder;
    }

    void Sidebar_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarItem item) return;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        switch (item.Kind)
        {
            case SidebarKind.Workspace:
                Add("タブを追加して開く", "\uE8A7", () => { if (_store.GetWorkspace(item.Id) is { } ws) LoadLayout(ws.LayoutJson, replace: false); });
                Add("現在の構成で上書き保存", "\uE74E", () => { _store.SaveWorkspace(item.Name, SerializeLayout()); LoadSidebar(); });
                Add("削除", "\uE74D", () => { _store.DeleteWorkspace(item.Id); LoadSidebar(); });
                break;
            case SidebarKind.SavedSearch:
                Add("削除", "\uE74D", () => { _store.DeleteSavedSearch(item.Id); LoadSidebar(); });
                break;
            default:
                Add("新しいタブで開く", "\uE8A7", () => OpenInNewTab(item.Path));
                Add("隣の区画で開く", "\uE89F", () => { if (CurrentTab is { } t) OpenInAdjacentPane(t.Model.ActivePane, item.Path); });
                Add("エクスプローラーで開く", "\uEC50", () => ShellService.OpenInExplorer(item.Path));
                menu.Items.Add(LinkMenu.Build(item.Path));
                if (item.Kind == SidebarKind.Favorite) Add("お気に入りから削除", "\uE74D", () => { _store.RemoveFavorite(item.Id); LoadSidebar(); });
                else if (item.Kind == SidebarKind.Folder) Add("お気に入りに追加", "\uE734", () => AddFavorite(item.Path));
                Add("プロパティ", "\uE946", () => ShellService.ShowProperties(item.Path));
                break;
        }
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveFolder is { IsPc: false } f) AddFavorite(f.CurrentPath);
    }

    async void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        string? name = await Dialogs.PromptAsync(Content.XamlRoot, "ワークスペースとして保存", CurrentTab?.Model.Title ?? "ワークスペース", "保存");
        if (name == null) return;
        _store.SaveWorkspace(name, SerializeLayout());
        LoadSidebar();
    }

    // ================================================================ セッション・ワークスペース

    string SerializeLayout()
    {
        var tabs = new JsonArray();
        foreach (var item in Tabs.TabItems.OfType<TabViewItem>())
        {
            var entry = _tabs.FirstOrDefault(t => t.Item == item);
            if (entry != null) tabs.Add(entry.Model.Serialize());
        }
        var json = new JsonObject { ["tabs"] = tabs, ["activeTab"] = Math.Max(0, Tabs.SelectedIndex) };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    void LoadLayout(string layoutJson, bool replace)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(layoutJson) as JsonObject; }
        catch (JsonException) { json = null; }
        if (json?["tabs"] is not JsonArray tabs || tabs.Count == 0) return;
        if (replace)
        {
            foreach (var entry in _tabs.ToList())
            {
                entry.Host.Detach();
                entry.Model.Dispose();
                Tabs.TabItems.Remove(entry.Item);
            }
            _tabs.Clear();
        }
        var added = new List<TabEntry>();
        foreach (var t in tabs.OfType<JsonObject>())
        {
            var model = TabViewModel.Deserialize(t);
            if (model != null) added.Add(AddTab(model, select: false));
        }
        if (added.Count == 0) { if (_tabs.Count == 0) OpenInNewTab(""); return; }
        int active = Math.Clamp((int?)json["activeTab"] ?? 0, 0, added.Count - 1);
        Tabs.SelectedItem = added[active].Item;
    }

    void RestoreSession()
    {
        bool restore = _store.GetSetting("restoreSession") != "0";
        var session = restore ? _store.GetWorkspace(UserStore.SessionWorkspace) : null;
        if (session != null) LoadLayout(session.LayoutJson, replace: true);
        if (_tabs.Count == 0) OpenInNewTab("");
    }

    void OnClosed(object sender, WindowEventArgs args)
    {
        _linkServer?.Dispose();
        SaveDialogWatcher.Instance.Dispose();
        ExplorerIntegration.Instance.Dispose();
        try { _store.SaveWorkspace(UserStore.SessionWorkspace, SerializeLayout()); }
        catch (Exception) { }
        _statusTimer.Stop();
        foreach (var t in _tabs) t.Model.Dispose();
        IndexClient.Instance.Dispose();
    }

    // ================================================================ インデックス状態

    async Task RefreshIndexStatusAsync()
    {
        var status = await IndexClient.Instance.GetStatusAsync();
        if (status == null)
        {
            IndexStatusIcon.Glyph = "\uE7BA";
            IndexStatusText.Text = "インデックス: 未接続（起動中…）";
            IndexDetailText.Text = "Indexer に接続できません。";
            return;
        }
        string method = status.Volumes.Any(v => v.Method is "MFT" or "Snapshot") ? "MFT" : "走査";
        IndexStatusIcon.Glyph = status.State == IndexState.Ready ? "\uE73E" : "\uE895";
        IndexStatusText.Text = status.State == IndexState.Ready
            ? $"インデックス: {status.EntryCount:N0} 件 ✓ 最新（{method}方式）"
            : $"インデックス: 構築中… {status.EntryCount:N0} 件";
        IndexDetailText.Text = string.Join(Environment.NewLine, status.Volumes.Select(v =>
            $"{v.Root}  {MethodName(v.Method)}  {(v.Ready ? $"{v.Entries:N0} 件（{v.BuildSeconds:F1} 秒で構築）" : "構築中…")}"))
            + Environment.NewLine + (status.Elevated ? "管理者権限で動作中" : "通常権限で動作中");
        ElevateButton.Visibility = status.Elevated ? Visibility.Collapsed : Visibility.Visible;
    }

    static string MethodName(string method) => method switch
    {
        "MFT" => "MFT 直接読み取り",
        "Snapshot" => "スナップショット＋USN",
        _ => "ディレクトリ走査",
    };

    async void Elevate_Click(object sender, RoutedEventArgs e)
    {
        IndexStatusText.Text = "インデックス: 再起動中…";
        bool ok = await IndexClient.Instance.RestartElevatedAsync();
        if (!ok) ShowInfo("管理者権限での起動がキャンセルされました。通常権限で継続します");
    }

    // ================================================================ キーボード

    void RegisterAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, e) => { action(); e.Handled = true; };
            Root.KeyboardAccelerators.Add(acc);
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        // 「\」（英字配列 VK_OEM_5 / 日本語配列「ろ」VK_OEM_102）は KeyboardAccelerator が発火しないため直接拾う
        Root.PreviewKeyDown += (_, e) =>
        {
            if ((int)e.Key is not (220 or 226) || !IsDown(VirtualKey.Control) || CurrentTab is not { } t) return;
            SplitPane(t.Model.ActivePane, IsDown(VirtualKey.Shift) ? Orientation.Vertical : Orientation.Horizontal);
            e.Handled = true;
        };
        Add(VirtualKey.T, VirtualKeyModifiers.Control, () => OpenInNewTab(ActiveFolder?.CurrentPath ?? ""));
        Add(VirtualKey.W, VirtualKeyModifiers.Control, () => { if (CurrentTab is { } t) ClosePane(t.Model.ActivePane); });
        Add(VirtualKey.F, VirtualKeyModifiers.Control, FocusSearch);
        Add(VirtualKey.E, VirtualKeyModifiers.Control, FocusSearch);
        Add(VirtualKey.L, VirtualKeyModifiers.Control, EditAddress);
        Add(VirtualKey.D, VirtualKeyModifiers.Menu, EditAddress);
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _ = ActiveFolder?.GoBackAsync());
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _ = ActiveFolder?.GoForwardAsync());
        Add(VirtualKey.Up, VirtualKeyModifiers.Menu, () => _ = ActiveFolder?.GoUpAsync());
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = ActiveFolder?.RefreshAsync());
        Add(VirtualKey.H, VirtualKeyModifiers.Control, ToggleHidden);
    }

    static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    void EditAddress()
    {
        if (CurrentTab is not { } tab || tab.Model.ActiveFolder is not { } folder) return;
        (tab.Host.ViewFor(folder) as FolderPaneView)?.BeginEditAddress();
    }

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await SettingsDialog.ShowAsync(Content.XamlRoot);
        foreach (var f in _tabs.SelectMany(t => t.Model.Panes).OfType<FolderPaneViewModel>()) _ = f.RefreshAsync();
    }

    void ToggleHidden()
    {
        FolderPaneViewModel.ShowHidden = !FolderPaneViewModel.ShowHidden;
        _store.SetSetting("showHidden", FolderPaneViewModel.ShowHidden ? "1" : "0");
        foreach (var f in _tabs.SelectMany(t => t.Model.Panes).OfType<FolderPaneViewModel>()) _ = f.RefreshAsync();
        ShowInfo(FolderPaneViewModel.ShowHidden ? "隠しファイルを表示します (Ctrl+H)" : "隠しファイルを非表示にします (Ctrl+H)");
    }

    // ================================================================ 通知

    InfoBar? _toast;

    /// <summary>右下に通知を出す。actionText を渡すと「元に戻す」などのボタンを付ける（そのときは長めに表示）。</summary>
    public void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? actionText = null, Action? action = null)
    {
        if (_toast == null)
        {
            _toast = new InfoBar
            {
                IsClosable = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 40),
                MaxWidth = 520,
            };
            Grid.SetRowSpan(_toast, 3);
            Root.Children.Add(_toast);
        }
        _toast.Message = message;
        _toast.Severity = severity;
        if (actionText != null && action != null)
        {
            var button = new Button { Content = actionText };
            button.Click += (_, _) => { _toast.IsOpen = false; action(); };
            _toast.ActionButton = button;
        }
        else _toast.ActionButton = null;
        _toast.IsOpen = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(actionText != null ? 10 : 4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (_toast.Message == message) _toast.IsOpen = false; };
        timer.Start();
    }
}
