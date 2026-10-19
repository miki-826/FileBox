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
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FileBox.ico")); }
        catch (Exception) { }

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += async (_, _) => await RefreshSearchSuggestionsAsync();
        SearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SearchBox_KeyDown), true);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += async (_, _) => await RefreshIndexStatusAsync();
        _statusTimer.Start();

        FolderPaneViewModel.ShowHidden = _store.GetSetting("showHidden") == "1";
        ViewPreferences.Initialize(_store);
        UserMarks.Initialize(_store);
        FileOperations.Initialize(_store);
        KeyBindings.Initialize(_store);
        ViewPreferences.ColorsChanged += OnFolderColorsChanged;
        FolderPaneViewModel.Visited += OnFolderVisited;
        SuggestionService.Initialize(_store);
        InboxService.Initialize(_store, DispatcherQueue);
        InboxService.Instance.CountChanged += UpdateInboxEntry;
        InitializeLinks();
        SettingsDialog.Store = _store;
        InitializePolicy();
        SaveDialogWatcher.Initialize(_store, DispatcherQueue);
        RegisterAccelerators();
        InitializeSections();
        LoadSidebar();
        RestoreSession();
        InitializeExtras();
        Text.Start(this);
        Closed += OnClosed;
        _ = RefreshIndexStatusAsync();
    }

    TabEntry? CurrentTab => Tabs.SelectedItem is TabViewItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null;
    FolderPaneViewModel? ActiveFolder => CurrentTab?.Model.ActiveFolder;

    // ウィンドウが狭いときも、場所と検索欄の幅を確保する。
    void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 1150;
        BrandPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TopBar.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 228);
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 212 : 248);
    }

    // ================================================================ タブ

    TabEntry AddTab(TabViewModel model, bool select = true, int? index = null)
    {
        var host = new PaneHost(model, this);
        var item = new TabViewItem
        {
            Content = host,
            IconSource = TabIcon(model.ActivePane),
        };
        item.SetBinding(TabViewItem.HeaderProperty, new Microsoft.UI.Xaml.Data.Binding { Source = model, Path = new PropertyPath(nameof(TabViewModel.Title)) });
        var entry = new TabEntry(model, item, host);
        _tabs.Add(entry);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TabViewModel.ActivePane) or nameof(TabViewModel.Title)) item.IconSource = TabIcon(model.ActivePane);
            if (e.PropertyName == nameof(TabViewModel.ActivePane) && ReferenceEquals(CurrentTab, entry)) OnActivePaneChanged();
        };
        EnableTabDrop(entry);
        if (index is int i && i <= Tabs.TabItems.Count) Tabs.TabItems.Insert(i, item);
        else Tabs.TabItems.Add(item);
        if (select) Tabs.SelectedItem = item;
        return entry;
    }

    static FontIconSource TabIcon(PaneViewModel pane) => new()
    {
        Glyph = pane switch
        {
            FolderPaneViewModel { IsPc: true } => "\uE7F8",
            SearchPaneViewModel => "\uE721",
            InboxPaneViewModel => "\uE896",
            CapacityPaneViewModel => "\uEDA2",
            AdminPaneViewModel => "\uE7EF",
            PreviewPaneViewModel => "\uE8A1",
            ShellPaneViewModel { IsRecycleBin: true } => "\uE74D",
            ShellPaneViewModel => "\uE968",
            _ => "\uE8B7",
        },
    };

    public void OpenInNewTab(string path) => AddTab(new TabViewModel(new FolderPaneViewModel(path)));

    void CloseTab(TabEntry entry)
    {
        RememberClosed(entry);
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
        UpdateAddressBar();
    }

    void OnStatusSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderPaneViewModel.StatusText) or nameof(FolderPaneViewModel.CurrentPath)) UpdateStatusText();
        if (e.PropertyName == nameof(FolderPaneViewModel.CurrentPath))
        {
            SyncInFolderChip();
            UpdateAddressBar();
        }
    }

    // ================================================================ 上のバー（戻る・進む・上へ・アドレス）

    /// <summary>上のバーが操作する区画: 選択中のフォルダ区画（検索結果などを選んでいるときはタブ内のフォルダ区画）。</summary>
    FolderPaneViewModel? AddressSource => _statusSource;

    void UpdateAddressBar()
    {
        var folder = AddressSource;
        TopBackButton.IsEnabled = folder?.CanGoBack == true;
        TopForwardButton.IsEnabled = folder?.CanGoForward == true;
        TopUpButton.IsEnabled = folder?.CanGoUp == true;
        if (folder == null)
        {
            // フォルダ区画の無いタブ（ごみ箱・受け取りボックスなど）は区画の名前を出す
            var pane = CurrentTab?.Model.ActivePane;
            TopBreadcrumb.ItemsSource = pane == null ? new List<string>() : new List<string> { pane.Title };
            var (glyph, color) = pane switch
            {
                ShellPaneViewModel { IsRecycleBin: true } => ("\uE74D", "#8B8D98"),
                ShellPaneViewModel => ("\uE968", "#05A2C2"),
                InboxPaneViewModel => ("\uE896", "#30A46C"),
                SearchPaneViewModel => ("\uE721", "#0090FF"),
                CapacityPaneViewModel => ("\uEDA2", "#30A46C"),
                AdminPaneViewModel => ("\uE7EF", "#8E4EC6"),
                _ => ("\uE8B7", "#E8A33D"),
            };
            TopAddressIcon.Glyph = glyph;
            TopAddressIcon.Foreground = Converters.HexBrush(color);
        }
        else
        {
            TopBreadcrumb.ItemsSource = folder.Breadcrumbs.Select(b => b.Name).ToList();
            TopAddressIcon.Glyph = folder.IsPc ? "\uE7F8" : "\uE8B7";
            TopAddressIcon.Foreground = Converters.HexBrush(folder.IsPc ? "#3E63DD" : ViewPreferences.FolderColor(folder.CurrentPath) ?? "#E8A33D");
        }
        // 検索欄に「どこを検索するか」を出す
        var q = QueryParser.Parse(SearchBox.Text);
        string? scope = q.InPath == null ? null : Path.GetFileName(q.InPath.TrimEnd('\\')) is { Length: > 0 } n ? n : q.InPath;
        SearchBox.PlaceholderText = scope != null ? $"「{scope}」の中を検索（Ctrl+F）" : "PC 全体を検索（Ctrl+F）";
        UpdateSidebarSelection();
    }

    void TopBack_Click(object sender, RoutedEventArgs e) => _ = AddressSource?.GoBackAsync();
    void TopForward_Click(object sender, RoutedEventArgs e) => _ = AddressSource?.GoForwardAsync();
    void TopUp_Click(object sender, RoutedEventArgs e) => _ = AddressSource?.GoUpAsync();
    void TopRefresh_Click(object sender, RoutedEventArgs e) => _ = AddressSource?.RefreshAsync();

    void TopBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (AddressSource is not { } folder) return;
        var crumbs = folder.Breadcrumbs;
        if (args.Index < crumbs.Count) _ = folder.NavigateAsync(crumbs[args.Index].Path);
    }

    void TopBreadcrumbHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // パンくずの項目以外（余白）をクリックしたら直接入力モードへ
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is string) return;
        BeginTopAddressEdit();
    }

    void BeginTopAddressEdit()
    {
        TopBreadcrumbHost.Visibility = Visibility.Collapsed;
        TopAddressBox.Visibility = Visibility.Visible;
        TopAddressBox.Text = AddressSource is { IsPc: false } f ? f.CurrentPath : "PC";
        TopAddressBox.Focus(FocusState.Programmatic);
        TopAddressBox.SelectAll();
    }

    void EndTopAddressEdit()
    {
        TopAddressBox.Visibility = Visibility.Collapsed;
        TopBreadcrumbHost.Visibility = Visibility.Visible;
    }

    void TopAddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            string text = TopAddressBox.Text;
            EndTopAddressEdit();
            if (ShellLocation(text) is { } shell) OpenShellPane(shell);
            else if (ShellService.ResolveAddress(text) is not { } path) ShowInfo($"場所が見つかりません: {text}", InfoBarSeverity.Warning);
            else if (File.Exists(path)) ShellService.Open(path);
            else NavigateActive(path);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            EndTopAddressEdit();
            e.Handled = true;
        }
    }

    void TopAddressBox_LostFocus(object sender, RoutedEventArgs e) => EndTopAddressEdit();

    void UpdateStatusText()
    {
        var active = CurrentTab?.Model.ActivePane;
        StatusText.Text = active switch
        {
            SearchPaneViewModel s => s.Summary,
            // 件数の文とパスは別々に訳す（パスまで含めると対応表の文と一致しないため）
            _ when _statusSource != null => $"{Text.T(_statusSource.StatusText)}    {(_statusSource.IsPc ? "PC" : _statusSource.CurrentPath)}",
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
        var list = (view as FrameworkElement)?.FindName("List") as ListViewBase;
        if (view is FolderPaneView folderView) { if (folderView.SingleSelection is { } selected) preview.Item = selected; }
        else if (list?.SelectedItems.Count == 1 && list.SelectedItem is FileItem item) preview.Item = item;
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
            if (ShellLocation(arg) is { } shell) { OpenShellPane(shell); continue; }
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
        _searchFocused = true;
        _suggestionsDismissed = false;
        ShowSearchSuggestions(SearchBox.Text);
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) { _searchTimer.Stop(); _searchTimer.Start(); }
    }

    void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        SyncChipsFromQuery();
        UpdateAddressBar();
        if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen) return;
        _searchTimer.Stop();
        _suggestionsCts?.Cancel();
        _suggestionsDismissed = false;
        ShowSearchSuggestions(sender.Text);
        if (_searchFocused && !string.IsNullOrWhiteSpace(sender.Text)) _searchTimer.Start();
    }

    void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestionItem candidate)
        {
            if (ChooseSearchLocation(candidate)) return;
            if (candidate.Suggestion.Kind == SearchSuggestionKind.Filter)
            {
                sender.Text = candidate.Suggestion.Query;
                sender.Focus(FocusState.Programmatic);
                return;
            }
        }
        string query = args.ChosenSuggestion is SearchSuggestionItem selected ? selected.Suggestion.Query : args.QueryText;
        sender.Text = query;
        CancelSearchSuggestions();
        if (string.IsNullOrWhiteSpace(query)) return;
        _store.AddSearchHistory(query);
        // Enter: 検索結果を新しいタブで開く（02_機能設計 §3 / 04_画面設計 S02）
        var auto = CurrentTab?.Model.Panes.OfType<SearchPaneViewModel>().FirstOrDefault(p => p.AutoCreated);
        if (auto != null && CurrentTab!.Model.PaneCount > 1) CurrentTab.Model.Close(auto);
        AddTab(new TabViewModel(new SearchPaneViewModel(query)));
        _searchTimer.Stop();
    }

    /// <summary>条件チップなどで検索を変更したとき、現在タブの結果区画を更新する。</summary>
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
        CancelSearchSuggestions();
        RunSearchInCurrentTab(query);
    }

    void SyncChipsFromQuery()
    {
        _syncingChips = true;
        var q = QueryParser.Parse(SearchBox.Text);
        FolderChip.IsChecked = q.Kind == KindFilter.Folders;
        FileChip.IsChecked = q.Kind == KindFilter.Files;
        ModeChip.Content = "一致: " + (q.DefaultMode switch
        {
            MatchMode.Prefix => "前方一致",
            MatchMode.Exact => "完全一致",
            MatchMode.Wildcard => "ワイルドカード",
            MatchMode.Regex => "正規表現",
            MatchMode.Fuzzy => "あいまい",
            _ => "部分一致",
        });
        ExtChip.Content = q.Extensions.Count == 0 ? "拡張子" : "拡張子: " + string.Join(",", q.Extensions.Take(3)) + (q.Extensions.Count > 3 ? "…" : "");
        DateChip.Content = q.ModifiedFrom == null && q.ModifiedTo == null ? "期間" : $"期間: {q.ModifiedFrom:yyyy/MM/dd}〜{q.ModifiedTo?.AddDays(-1):yyyy/MM/dd}";
        SyncInFolderChip(q);
        _syncingChips = false;
    }

    void ResetSearchFilters_Click(object sender, RoutedEventArgs e)
    {
        string query = QueryEditor.SetKind(SearchBox.Text, KindFilter.All);
        query = QueryEditor.SetMode(query, MatchMode.Substring);
        query = QueryEditor.SetValue(query, "ext", "");
        query = QueryEditor.SetValue(query, "modified", "");
        SetQuery(QueryEditor.SetInPath(query, null));
    }

    void SyncInFolderChip(SearchQuery? q = null)
    {
        q ??= QueryParser.Parse(SearchBox.Text);
        InFolderChip.IsChecked = q.InPath != null;
        string? folder = ActiveFolder is { IsPc: false } f ? f.CurrentPath : null;
        string? scopeName = q.InPath == null ? null : Path.GetFileName(q.InPath.TrimEnd('\\')) is { Length: > 0 } n ? n : q.InPath;
        InFolderChip.Content = scopeName != null ? $"範囲: {scopeName}" : "このフォルダ内";
        InFolderChip.IsEnabled = q.InPath != null || folder != null;
        int count = (q.Kind != KindFilter.All ? 1 : 0) + (q.DefaultMode != MatchMode.Substring ? 1 : 0)
            + (q.Extensions.Count > 0 ? 1 : 0) + (q.ModifiedFrom != null || q.ModifiedTo != null ? 1 : 0) + (q.InPath != null ? 1 : 0);
        FilterSummary.Text = count == 0 ? "検索条件" : $"検索条件 ({count})";
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
        InboxList.ItemsSource = new List<SidebarItem>
        {
            new() { Name = "PC", Glyph = "\uE7F8", Kind = SidebarKind.Folder, Color = "#3E63DD" },
        };
        UpdateSidebarSelection();
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
            .Select((q, i) => (q.Name, q.Glyph, Path: ShellService.KnownFolder(q.Id), Color: QuickColors[i % QuickColors.Length]))
            .Where(q => q.Path != null)
            .Select(q => new SidebarItem { Name = q.Name, Glyph = q.Glyph, Path = q.Path!, Kind = SidebarKind.Folder, Color = q.Color })
            .ToList();
        FavoriteList.ItemsSource = _store.GetFavorites()
            .Select(f => new SidebarItem
            {
                Name = f.Label ?? (Path.GetFileName(f.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : f.Path),
                Glyph = "\uE734",
                Path = f.Path,
                Id = f.Id,
                Kind = SidebarKind.Favorite,
                Color = ViewPreferences.FolderColor(f.Path) ?? "#E2A300",
            })
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
                Color = "#5B8DEF",
            };
        }).Concat(ShellPlaces()).ToList();
        WorkspaceList.ItemsSource = _store.GetWorkspaces()
            .Select(w => new SidebarItem { Name = w.Name, Glyph = "\uE8A9", Id = w.Id, Kind = SidebarKind.Workspace, Color = "#8E4EC6" })
            .ToList();
        SavedSearchList.ItemsSource = _store.GetSavedSearches()
            .Select(s => new SidebarItem { Name = s.Name, Glyph = "\uE721", Id = s.Id, Query = s.Query, Kind = SidebarKind.SavedSearch, Color = "#0090FF" })
            .Concat(FrequentSearchItems())
            .ToList();
        _ = LoadRecentAsync();
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
            case SidebarKind.SavedSearch or SidebarKind.FrequentSearch or SidebarKind.Tag:
                OpenSearch(item.Query ?? "");
                break;
            case SidebarKind.Shell:
                OpenShellPane(item.Path);
                break;
            default:
                NavigateActive(item.Path);
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
            case SidebarKind.FrequentSearch:
                Add("検索を保存…", "\uE74E", async () =>
                {
                    if (await Dialogs.PromptAsync(Content.XamlRoot, "検索を保存", item.Query ?? "", "保存") is { } name) { _store.SaveSearch(name, item.Query ?? ""); LoadSidebar(); }
                });
                break;
            case SidebarKind.Tag when item.Query != "is:starred":
                Add("このタグを削除（付けた印をすべて外す）", "\uE74D", () => UserMarks.DeleteTag(item.Name));
                break;
            case SidebarKind.Tag:
                Add("すべての星を外す", "\uE74D", () => UserMarks.SetStar(UserMarks.StarredPaths(), false));
                break;
            case SidebarKind.Shell:
                Add("開く", "\uE8AD", () => OpenShellPane(item.Path));
                Add("エクスプローラーで開く", "\uEC50", () => ShellService.OpenInExplorer(item.Path == ShellPaneViewModel.Recycle ? "shell:RecycleBinFolder" : "shell:NetworkPlacesFolder"));
                break;
            default:
                Add("新しいタブで開く", "\uE8A7", () => OpenInNewTab(item.Path));
                Add("隣の区画で開く", "\uE89F", () => { if (CurrentTab is { } t) OpenInAdjacentPane(t.Model.ActivePane, item.Path); });
                Add("エクスプローラーで開く", "\uEC50", () => ShellService.OpenInExplorer(item.Path));
                menu.Items.Add(LinkMenu.Build(item.Path));
                if (item.Kind == SidebarKind.Favorite) Add("お気に入りから削除", "\uE74D", () => { _store.RemoveFavorite(item.Id); LoadSidebar(); });
                else if (item.Kind is SidebarKind.Folder or SidebarKind.Recent) Add("お気に入りに追加", "\uE734", () => AddFavorite(item.Path));
                if (item.Kind == SidebarKind.Recent) Add("一覧から外す", "\uE711", () => { _store.RemoveRecent(item.Path); _ = LoadRecentAsync(); });
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
        CancelSearchSuggestions();
        _linkServer?.Dispose();
        GlobalHotkey.Instance.Dispose();
        ContentIndexer.Instance.Dispose();
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
        var actions = new Dictionary<string, Action>
        {
            ["newTab"] = () => OpenInNewTab(ActiveFolder?.CurrentPath ?? ""),
            ["closePane"] = () => { if (CurrentTab is { } t) ClosePane(t.Model.ActivePane); },
            ["reopenTab"] = ReopenClosedTab,
            ["nextTab"] = () => CycleTab(1),
            ["prevTab"] = () => CycleTab(-1),
            ["search"] = FocusSearch,
            ["search2"] = FocusSearch,
            ["address"] = EditAddress,
            ["address2"] = EditAddress,
            ["back"] = () => _ = ActiveFolder?.GoBackAsync(),
            ["forward"] = () => _ = ActiveFolder?.GoForwardAsync(),
            ["up"] = () => _ = ActiveFolder?.GoUpAsync(),
            ["refresh"] = () => _ = ActiveFolder?.RefreshAsync(),
            ["undo"] = () => _ = FileOperations.Instance.UndoAsync(),
            ["redo"] = () => _ = FileOperations.Instance.RedoAsync(),
            ["hidden"] = ToggleHidden,
            ["zoomIn"] = () => ViewPreferences.SetZoom(ViewPreferences.Zoom + 1),
            ["zoomOut"] = () => ViewPreferences.SetZoom(ViewPreferences.Zoom - 1),
            ["help"] = () => _ = ShortcutsDialog.ShowAsync(Content.XamlRoot),
        };
        // 入力欄の中では、文字の元に戻す・やり直す（Ctrl+Z / Ctrl+Y）を優先する
        var textKeys = new HashSet<string> { "undo", "redo" };
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action, bool notInText = false)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, e) =>
            {
                if (notInText && IsTyping()) return;
                action();
                e.Handled = true;
            };
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
        foreach (var binding in KeyBindings.Current)
            if (actions.TryGetValue(binding.Id, out var action)) Add(binding.Key, binding.Modifiers, action, textKeys.Contains(binding.Id));
        for (int n = 0; n < 9; n++)
        {
            int index = n;
            Add(VirtualKey.Number1 + n, VirtualKeyModifiers.Control, () => SelectTab(index));
        }
        // テンキーでない「+」「-」（VK_OEM_PLUS / VK_OEM_MINUS）でも大きさを変える
        Add((VirtualKey)187, VirtualKeyModifiers.Control, actions["zoomIn"]);
        Add((VirtualKey)189, VirtualKeyModifiers.Control, actions["zoomOut"]);
    }

    /// <summary>文字を入力する場所にフォーカスがあるか。</summary>
    bool IsTyping() => FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox or RichEditBox;

    static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    void EditAddress()
    {
        if (CurrentTab is not { } tab) return;
        if (tab.Model.PaneCount == 1 || tab.Model.ActiveFolder is not { } folder) { BeginTopAddressEdit(); return; }
        (tab.Host.ViewFor(folder) as FolderPaneView)?.BeginEditAddress();
    }

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await SettingsDialog.ShowAsync(Content.XamlRoot);
        foreach (var f in _tabs.SelectMany(t => t.Model.Panes).OfType<FolderPaneViewModel>()) _ = f.RefreshAsync();
    }

    public void ToggleHidden()
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
