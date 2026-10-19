using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FileBox.App;

/// <summary>サイドバーの折りたたみ・最近使ったフォルダ・ごみ箱とネットワーク（FileBox の中で開く）。</summary>
public sealed partial class MainWindow
{
    const string CollapsedKey = "sidebar.collapsed";
    const int RecentCount = 5;

    /// <summary>クイックアクセスの色（エクスプローラーのアイコンの色に寄せる）。</summary>
    static readonly string[] QuickColors = { "#3E63DD", "#0090FF", "#30A46C", "#5B6B7F", "#0F9D8C", "#F76B15", "#8E4EC6" };

    readonly Dictionary<string, (Button Toggle, FrameworkElement Body)> _sections = new();
    HashSet<string> _collapsed = new();

    /// <summary>表示中の場所を一つだけ強調する。お気に入りと履歴の重複では上側を優先する。</summary>
    void UpdateSidebarSelection()
    {
        var active = CurrentTab?.Model.ActivePane;
        bool found = false;
        foreach (var list in new[] { InboxList, QuickList, FavoriteList, RecentList, DriveList, SavedSearchList })
        {
            SidebarItem? selected = null;
            if (!found)
            {
                selected = list.Items.OfType<SidebarItem>().FirstOrDefault(item => active switch
                {
                    InboxPaneViewModel => item.Kind == SidebarKind.Folder && string.Equals(item.Path.TrimEnd('\\'),
                        ShellService.KnownFolder(ShellService.QuickAccess[2].Id)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    ShellPaneViewModel shell => item.Kind == SidebarKind.Shell && item.Path == shell.Location,
                    SearchPaneViewModel search => item.Kind == SidebarKind.SavedSearch && item.Query == search.Query,
                    FolderPaneViewModel folder => item.Kind is SidebarKind.Folder or SidebarKind.Favorite or SidebarKind.Recent or SidebarKind.Drive
                        && string.Equals(item.Path.TrimEnd('\\'), folder.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    _ => false,
                });
            }
            list.SelectedItem = selected;
            found |= selected != null;
        }
    }

    // ================================================================ 折りたたみ

    void InitializeSections()
    {
        _collapsed = (_store.GetSetting(CollapsedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        RegisterSection("quick", "クイック アクセス", QuickToggle, QuickList);
        RegisterSection("recent", "最近使ったフォルダ", RecentToggle, RecentList);
        RegisterSection("favorites", "お気に入り", FavoriteToggle, FavoriteList);
        RegisterSection("drives", "ドライブと場所", DriveToggle, DriveList);
        RegisterSection("zones", "容量の上限", ZoneToggle, ZoneList);
        RegisterSection("workspaces", "ワークスペース", WorkspaceToggle, WorkspaceList);
        RegisterSection("searches", "保存した検索", SavedSearchToggle, SavedSearchList);
    }

    void RegisterSection(string key, string name, Button toggle, FrameworkElement body)
    {
        _sections[key] = (toggle, body);
        toggle.Content = new FontIcon
        {
            Glyph = "",   // 下向きの矢印。閉じると上向きに回る
            FontSize = 14,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        toggle.Tag = name;
        toggle.Click += (_, _) => SetSectionExpanded(key, _collapsed.Contains(key), animate: true);
        SetSectionExpanded(key, !_collapsed.Contains(key), animate: false);
    }

    void SetSectionExpanded(string key, bool expanded, bool animate)
    {
        var (toggle, body) = _sections[key];
        var rotate = (RotateTransform)((FontIcon)toggle.Content).RenderTransform;
        string tip = $"{toggle.Tag}を{(expanded ? "折りたたむ" : "表示する")}";
        ToolTipService.SetToolTip(toggle, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, tip);
        if (expanded ? _collapsed.Remove(key) : _collapsed.Add(key))
            _store.SetSetting(CollapsedKey, string.Join(',', _collapsed));

        void Finish()
        {
            rotate.Angle = expanded ? 0 : 180;
            body.MaxHeight = double.PositiveInfinity;
            body.Opacity = 1;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!animate) { Finish(); return; }

        // 高さを縮めながら薄くする（開くときはその逆）。矢印は同時に回す
        body.Visibility = Visibility.Visible;
        body.MaxHeight = double.PositiveInfinity;
        body.Measure(new Windows.Foundation.Size(Math.Max(1, SidebarPanel.ActualWidth), double.PositiveInfinity));
        double full = body.DesiredSize.Height;
        double from = expanded ? 0 : body.ActualHeight;
        double to = expanded ? full : 0;
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        void Animate(DependencyObject target, string property, double? start, double end, bool dependent = false)
        {
            var a = new DoubleAnimation { From = start, To = end, Duration = duration, EasingFunction = ease, EnableDependentAnimation = dependent };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, property);
            storyboard.Children.Add(a);
        }
        Animate(body, "MaxHeight", from, to, dependent: true);
        Animate(body, "Opacity", expanded ? 0 : 1, expanded ? 1 : 0);
        Animate(rotate, "Angle", null, expanded ? 0 : 180);
        storyboard.Completed += (_, _) =>
        {
            Finish();
            storyboard.Stop();
        };
        storyboard.Begin();
    }

    // ================================================================ 最近使ったフォルダ

    void OnFolderVisited(string path)
    {
        try { _store.TouchRecent(path); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        _ = LoadRecentAsync();
    }

    int _recentVersion;

    /// <summary>直近に開いたフォルダ（クイックアクセス・ドライブの直下は除く）。存在確認は別スレッドで行う。</summary>
    async Task LoadRecentAsync()
    {
        int version = ++_recentVersion;
        var fixedPlaces = ShellService.QuickAccess.Select(q => ShellService.KnownFolder(q.Id)).Where(p => p != null)
            .Select(p => p!.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = _store.GetRecent(60);
        var folders = await Task.Run(() => recent
            .Where(p => p.Length > 3 && !fixedPlaces.Contains(p.TrimEnd('\\')) && !p.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentCount)
            .ToList());
        if (version != _recentVersion) return;
        RecentList.ItemsSource = folders.Select(p => new SidebarItem
        {
            Name = Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } n ? n : p,
            Glyph = "",
            Path = p,
            Kind = SidebarKind.Recent,
            Color = ViewPreferences.FolderColor(p) ?? "#E8A33D",
        }).ToList();
        UpdateSidebarSelection();
    }

    void OnFolderColorsChanged()
    {
        foreach (var f in _tabs.SelectMany(t => t.Model.Panes).OfType<FolderPaneViewModel>()) _ = f.RefreshAsync();
        LoadSidebar();
        UpdateAddressBar();
    }

    // ================================================================ ごみ箱・ネットワーク

    IEnumerable<SidebarItem> ShellPlaces() => new[]
    {
        new SidebarItem { Name = "ネットワーク", Glyph = "", Path = ShellPaneViewModel.Network, Kind = SidebarKind.Shell, Color = "#05A2C2" },
        new SidebarItem { Name = "ごみ箱", Glyph = "", Path = ShellPaneViewModel.Recycle, Kind = SidebarKind.Shell, Color = "#8B8D98" },
    };

    /// <summary>ごみ箱・ネットワークを開く。表示中のタブにあればそこを、なければ新しいタブで。</summary>
    public void OpenShellPane(string location)
    {
        foreach (var tab in _tabs)
        {
            var pane = tab.Model.Panes.OfType<ShellPaneViewModel>()
                .FirstOrDefault(p => p.IsRecycleBin == (location == ShellPaneViewModel.Recycle));
            if (pane == null) continue;
            Tabs.SelectedItem = tab.Item;
            tab.Model.ActivePane = pane;
            if (!pane.IsRecycleBin) pane.Location = location;
            return;
        }
        AddTab(new TabViewModel(new ShellPaneViewModel(location)));
    }

    /// <summary>エクスプローラーの特殊フォルダ（ごみ箱・ネットワーク）の指定なら、FileBox の区画の場所を返す。</summary>
    static string? ShellLocation(string arg) => arg.Trim().ToLowerInvariant() switch
    {
        "shell:recyclebinfolder" or "::{645ff040-5081-101b-9f08-00aa002f954e}" => ShellPaneViewModel.Recycle,
        "shell:networkplacesfolder" or "::{f02c1a0d-be21-4350-88b0-7367fc96ef3c}" => ShellPaneViewModel.Network,
        _ => null,
    };
}
