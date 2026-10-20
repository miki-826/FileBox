using System.Text.Json.Nodes;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.App.Views;
using FileBox.Core.Search;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileBox.App;

/// <summary>
/// 利用者目線の改善（2026-10-20）: 閉じたタブの再表示・状態の自動保存・タブへのドロップ・タグ欄・よく使う検索・
/// 容量の通知・更新の確認・テーマ・ホットキー・初回の案内・ショートカット一覧。
/// </summary>
public sealed partial class MainWindow
{
    const int ClosedTabLimit = 10;
    readonly Stack<JsonObject> _closedTabs = new();
    string? _lastSavedLayout;
    readonly HashSet<string> _warnedDrives = new(StringComparer.OrdinalIgnoreCase);

    void InitializeExtras()
    {
        ApplyTheme(ThemeService.Theme(_store));
        PathCompletion.Attach(TopAddressBox);
        FolderMenu.AttachTo(TopBreadcrumb, () => AddressSource?.Breadcrumbs.Select(b => b.Path).ToList() ?? new List<string>(), NavigateActive);
        RegisterSection("tags", "星・タグ", TagToggle, TagList);
        UserMarks.Changed += () => DispatcherQueue.TryEnqueue(LoadTags);
        LoadTags();

        // 落ちても前回の状態に戻れるよう、タブの構成が変わったら少し待って保存する
        var autosave = DispatcherQueue.CreateTimer();
        autosave.Interval = TimeSpan.FromSeconds(5);
        autosave.Tick += (_, _) => SaveSessionIfChanged();
        autosave.Start();

        // 容量が少なくなったら知らせる（10 分ごと。同じドライブは 1 回だけ）
        var space = DispatcherQueue.CreateTimer();
        space.Interval = TimeSpan.FromMinutes(10);
        space.Tick += (_, _) => CheckFreeSpace();
        space.Start();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CheckFreeSpace);

        ContentIndexer.Instance.Initialize(_store, DispatcherQueue);
        GlobalHotkey.Instance.Initialize(_store, WinRT.Interop.WindowNative.GetWindowHandle(this), OnHotkey);
        _ = CheckUpdateAsync();
        if (_store.GetSetting("onboarding.done") != "1")
            Root.Loaded += async (_, _) => { await Task.Delay(800); ShowOnboarding(); };
    }

    // ================================================================ テーマ

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        bool dark = theme == ElementTheme.Dark || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    // ================================================================ タブ

    /// <summary>閉じたタブを覚えておく（Ctrl+Shift+T で開き直す）。</summary>
    void RememberClosed(TabEntry entry)
    {
        try
        {
            _closedTabs.Push(entry.Model.Serialize());
            while (_closedTabs.Count > ClosedTabLimit)
            {
                var keep = _closedTabs.Take(ClosedTabLimit).Reverse().ToList();
                _closedTabs.Clear();
                foreach (var k in keep) _closedTabs.Push(k);
            }
        }
        catch (Exception ex) { AppLog.Error("閉じたタブを記録できませんでした", ex); }
    }

    void ReopenClosedTab()
    {
        while (_closedTabs.Count > 0)
        {
            var json = _closedTabs.Pop();
            if (TabViewModel.Deserialize(json) is { } model) { AddTab(model); return; }
        }
        ShowInfo("開き直せるタブはありません");
    }

    void SelectTab(int index)
    {
        var items = Tabs.TabItems.OfType<TabViewItem>().ToList();
        if (items.Count == 0) return;
        // Ctrl+9 は最後のタブ（ブラウザと同じ）
        Tabs.SelectedItem = index >= 8 ? items[^1] : items[Math.Min(index, items.Count - 1)];
    }

    void CycleTab(int delta)
    {
        int count = Tabs.TabItems.Count;
        if (count < 2) return;
        Tabs.SelectedIndex = ((Tabs.SelectedIndex + delta) % count + count) % count;
    }

    /// <summary>タブの見出しにファイルをドラッグすると、そのタブに切り替わり、そこへ落とすとそのタブのフォルダーへ入る。</summary>
    void EnableTabDrop(TabEntry entry)
    {
        var item = entry.Item;
        item.AllowDrop = true;
        DateTime hoverSince = DateTime.MinValue;
        item.DragEnter += (_, _) => hoverSince = DateTime.Now;
        item.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems) || entry.Model.ActiveFolder is not { IsPc: false } folder)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                return;
            }
            if (!ReferenceEquals(Tabs.SelectedItem, item) && DateTime.Now - hoverSince > TimeSpan.FromMilliseconds(600)) Tabs.SelectedItem = item;
            bool copy = IsDown(VirtualKey.Control);
            e.AcceptedOperation = copy ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"{(copy ? "コピー" : "移動")}: {folder.Title}";
        };
        item.Drop += async (_, e) =>
        {
            if (entry.Model.ActiveFolder is not { IsPc: false } folder) return;
            var deferral = e.GetDeferral();
            try
            {
                var paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                bool move = !IsDown(VirtualKey.Control);
                await FileOperations.Instance.TransferAsync(paths, folder.CurrentPath, move);
                await folder.RefreshAsync();
            }
            catch (Exception ex) { App.ShowError("ドロップした項目を処理できませんでした", ex); }
            finally { deferral.Complete(); }
        };
    }

    void SaveSessionIfChanged()
    {
        if (_store.GetSetting("restoreSession") == "0") return;
        try
        {
            string layout = SerializeLayout();
            if (layout == _lastSavedLayout) return;
            _store.SaveWorkspace(Data.UserStore.SessionWorkspace, layout);
            _lastSavedLayout = layout;
        }
        catch (Exception ex) { AppLog.Error("タブの状態を保存できませんでした", ex); }
    }

    // ================================================================ 星・タグ・よく使う検索（サイドバー）

    void LoadTags()
    {
        var items = new List<SidebarItem>();
        if (UserMarks.StarredPaths().Count > 0)
            items.Add(new SidebarItem { Name = "星付き", Glyph = "", Kind = SidebarKind.Tag, Query = "is:starred", Color = "#E2A300" });
        foreach (var tag in UserMarks.AllTagNames())
            items.Add(new SidebarItem { Name = tag, Glyph = "", Kind = SidebarKind.Tag, Query = $"tag:\"{tag}\"", Color = "#3E8EDE" });
        TagList.ItemsSource = items;
        TagHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_collapsed.Contains("tags") || items.Count == 0) TagList.Visibility = Visibility.Collapsed;
        else TagList.Visibility = Visibility.Visible;
    }

    IEnumerable<SidebarItem> FrequentSearchItems() => _store.GetFrequentSearches().Select(f => new SidebarItem
    {
        Name = f.Query, Glyph = "", Kind = SidebarKind.FrequentSearch, Query = f.Query, Color = "#8B8D98",
    });

    /// <summary>検索欄の文字を変えて、入力中と同じように検索し直す（検索結果の絞り込みのチップから）。</summary>
    public void SetSearchText(string query) => SetQuery(query);

    /// <summary>検索結果のタブを開く（タグ・保存した検索・よく使う検索から）。</summary>
    public void OpenSearch(string query)
    {
        SearchBox.Text = query;
        CancelSearchSuggestions();
        AddTab(new TabViewModel(new SearchPaneViewModel(query)));
    }

    // ================================================================ 容量・更新・ホットキー

    void CheckFreeSpace()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0))
            {
                double free = (double)d.TotalFreeSpace / d.TotalSize;
                if (free >= 0.1) { _warnedDrives.Remove(d.Name); continue; }
                if (!_warnedDrives.Add(d.Name)) continue;
                string path = d.RootDirectory.FullName;
                ShowInfo($"ドライブ {d.Name.TrimEnd('\\')} の空きが少なくなっています（残り {Format.HumanSize(d.TotalFreeSpace)}・{free:P0}）",
                    InfoBarSeverity.Warning, "容量を確認", () => OpenCapacity(path));
            }
        }
        catch (Exception ex) { AppLog.Error("空き容量を確認できませんでした", ex); }
    }

    async Task CheckUpdateAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (await UpdateChecker.CheckAsync(_store) is DateTime published)
            ShowInfo($"新しい preview 版があります（{published:yyyy/MM/dd HH:mm} 公開）", InfoBarSeverity.Informational, "配布ページを開く",
                () => _ = Launcher.LaunchUriAsync(new Uri(UpdateChecker.ReleasePage)));
    }

    void OnHotkey()
    {
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        FocusSearch();
    }

    // ================================================================ ヘルプ・初回の案内

    void Help_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var keys = new MenuFlyoutItem { Text = "ショートカット一覧", Icon = new FontIcon { Glyph = "" }, KeyboardAcceleratorTextOverride = "F1" };
        keys.Click += async (_, _) => await ShortcutsDialog.ShowAsync(Content.XamlRoot);
        var tour = new MenuFlyoutItem { Text = "使い方の案内をもう一度見る", Icon = new FontIcon { Glyph = "" } };
        tour.Click += (_, _) => ShowOnboarding();
        var search = new MenuFlyoutItem { Text = "検索の書き方", Icon = new FontIcon { Glyph = "" } };
        search.Click += (_, _) => SearchHelp_Click(sender, e);
        var report = new MenuFlyoutItem { Text = "不具合を報告…", Icon = new FontIcon { Glyph = "" } };
        report.Click += async (_, _) => await ReportBugAsync();
        menu.Items.Add(keys);
        menu.Items.Add(tour);
        menu.Items.Add(search);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(report);
        menu.ShowAt((FrameworkElement)sender);
    }

    public async Task ReportBugAsync()
    {
        if (await BugReport.CreateAsync() is { } zip)
            ShowInfo($"報告用のファイルをデスクトップに保存しました（{Path.GetFileName(zip)}）。開いたページに添付してください", InfoBarSeverity.Success,
                "保存した場所を開く", () => ShellService.ShowInExplorer(zip));
    }

    public void ShowOnboardingAgain() => ShowOnboarding();

    /// <summary>初回起動の案内（4 枚）。最後まで見るか閉じると、次からは出さない。</summary>
    void ShowOnboarding()
    {
        var steps = new (FrameworkElement Target, TeachingTipPlacementMode Placement, string Title, string Body)[]
        {
            (SearchBox, TeachingTipPlacementMode.Bottom, "PC 全体をすぐに検索", "名前の一部を打つだけで、PC のどこにあるファイルも探せます。ext:pdf や modified:thisweek などの条件も使えます（書きかけると候補が出ます）。"),
            (SidebarPanel, TeachingTipPlacementMode.Right, "よく使う場所はサイドバーに", "クイック アクセス・お気に入り・最近使ったフォルダ・星やタグを付けたものが並びます。見出しの矢印で折りたためます。"),
            (Tabs, TeachingTipPlacementMode.Center, "タブと分割で並べて作業", "Ctrl+T で新しいタブ、Ctrl+\\ で左右に分割。ファイルはタブの見出しへドラッグしても移せます。間違えたら Ctrl+Z で元に戻せます。"),
            (SettingsButton, TeachingTipPlacementMode.RightBottom, "保存先の提案とダウンロードの整理", "ダウンロードに届いたファイルや「名前を付けて保存」で、置き場所を提案します。表示・言語・データの書き出しは設定から。F1 でショートカット一覧を開けます。"),
        };
        // 1 枚ごとに作り直す（同じものの向き先を変えると、前の位置に出たままになるため）
        void Show(int index)
        {
            var (target, placement, title, body) = steps[index];
            var tip = new TeachingTip
            {
                Target = target, PreferredPlacement = placement, IsLightDismissEnabled = false,
                Title = title, Subtitle = body, CloseButtonContent = "閉じる",
                ActionButtonContent = index < steps.Length - 1 ? $"次へ（{index + 1}/{steps.Length}）" : "はじめる",
            };
            void Close()
            {
                tip.IsOpen = false;
                Root.Children.Remove(tip);
            }
            tip.ActionButtonClick += (_, _) =>
            {
                Close();
                if (index + 1 < steps.Length) Show(index + 1);
                else _store.SetSetting("onboarding.done", "1");
            };
            tip.CloseButtonClick += (_, _) => { Close(); _store.SetSetting("onboarding.done", "1"); };
            Root.Children.Add(tip);
            tip.IsOpen = true;
        }
        Show(0);
    }
}
