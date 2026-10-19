using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Files;
using FileBox.Core.Suggest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

public sealed partial class InboxPaneView : UserControl
{
    public InboxPaneViewModel ViewModel { get; }
    public string SourcesText => "整理対象: " + string.Join("、", InboxService.Instance.Sources);
    readonly IWorkspaceActions _actions;

    public InboxPaneView(InboxPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        ViewModel.ItemsChanged += ApplyItems;
        ApplyItems();
    }

    /// <summary>届いた日（今日・昨日・今週・先週・今月…）ごとに見出しを付けて表示する。選択中の項目は残す。</summary>
    void ApplyItems()
    {
        var selected = List.SelectedItem;
        var now = DateTime.Now;
        var firstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var groups = ViewModel.Items
            .OrderByDescending(i => i.Arrived)
            .GroupBy(i => DateGroup.Classify(i.Arrived, now, firstDay))
            .OrderBy(g => g.Key)
            .Select(g => new ItemGroup($"{DateGroup.Label(g.Key)}（{g.Count()}）", g))
            .ToList();
        List.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        if (selected != null && ViewModel.Items.Contains(selected)) List.SelectedItem = selected;
    }

    public static Brush BadgeBrush(Confidence confidence, bool hasTarget) =>
        (Brush)Application.Current.Resources[!hasTarget ? "SystemFillColorNeutralBrush"
            : confidence == Confidence.High ? "SystemFillColorSuccessBrush"
            : confidence == Confidence.Medium ? "SystemFillColorCautionBrush" : "SystemFillColorNeutralBrush"];

    public static Visibility EmptyVisibility(string summary) =>
        summary.StartsWith("未整理のファイルはありません") ? Visibility.Visible : Visibility.Collapsed;

    static InboxItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as InboxItem;

    async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { TargetPath: { } target } item) return;
        await InboxService.Instance.MoveAsync(item, target);
        ViewModel.Refresh();
    }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) InboxService.Instance.Dismiss(item);
    }

    void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, MenuFlyoutSubItem? parent = null)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            if (parent != null) parent.Items.Add(mi); else menu.Items.Add(mi);
        }
        async void MoveTo(string target) { await InboxService.Instance.MoveAsync(item, target); ViewModel.Refresh(); }

        if (item.Suggestion is { } s)
        {
            foreach (var c in s.Candidates)
                Add($"{Leaf(c.Path)}（{c.ConfidenceText}）— {c.Reason}", "", () => MoveTo(c.Path));
            if (s.NewFolder is { } nf)
                Add($"新しいフォルダ「{Leaf(nf.Path)}」を作って移動", "", () => MoveTo(nf.Path));
        }
        Add("詳しく選ぶ…", "", async () =>
        {
            var moved = await SuggestMoveDialog.RunAsync(XamlRoot, new[] { item.FullPath }, item.Suggestion);
            if (moved != null) { InboxService.Instance.Remove(item.FullPath); ViewModel.Refresh(); }
        });
        Add("別の場所を選ぶ…", "", async () => { if (await SuggestMoveDialog.PickFolderAsync() is { } p) MoveTo(p); });

        if (item.TargetPath is { } target && !item.TargetIsNew)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var rule = new MenuFlyoutSubItem { Text = $"「{Leaf(target)}」への振り分けをルール化", Icon = new FontIcon { Glyph = "" } };
            Add("候補として毎回表示する", "", () => CreateRule(item, target, auto: false), rule);
            Add("届いたら自動で移動する", "", () => CreateRule(item, target, auto: true), rule);
            menu.Items.Add(rule);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("開く", "", () => ShellService.Open(item.FullPath));
        Add("ファイルの場所を開く", "", () => _actions.RevealInFolderPane(ViewModel, item.FullPath));
        menu.ShowAt((FrameworkElement)sender);
    }

    void CreateRule(InboxItem item, string target, bool auto)
    {
        var rule = InboxService.Instance.AddRuleFrom(item, target, auto);
        item.Rule = rule;
        string cond = string.Join("・", new[] { rule.NamePattern, rule.Ext is { } x ? "." + x : null }.Where(v => v != null));
        App.Window?.ShowInfo($"ルールを追加しました: {cond} → {Leaf(target)}" + (auto ? "（自動移動）" : ""), InfoBarSeverity.Success);
    }

    void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is InboxItem item) ShellService.Open(item.FullPath);
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _actions.ActivatePane(ViewModel);
        FileItem? file = null;
        if (List.SelectedItem is InboxItem item)
            try { file = FileItem.FromInfo(new FileInfo(item.FullPath)); } catch (Exception) { }
        ViewModel.OnSelectionChanged(file);
    }

    async void MoveAll_Click(object sender, RoutedEventArgs e)
    {
        int n = await InboxService.Instance.MoveAllConfidentAsync();
        ViewModel.Refresh();
        App.Window?.ShowInfo(n > 0 ? $"{n} 件を推奨先へ移動しました（移動先ごとに Ctrl+Z で戻せます）" : "確信度「高」の項目はありません",
            n > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            n > 0 ? "元に戻す (Ctrl+Z)" : null, () => _ = FileOperations.Instance.UndoAsync());
    }

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await InboxSettingsDialog.ShowAsync(XamlRoot);
        SourcesLabel.Text = SourcesText;
    }

    void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (ShellService.KnownFolder(ShellService.QuickAccess[2].Id) is { } path) _actions.OpenInNewTab(path);
    }

    static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;

    // ---------------------------------------------------------------- 片付けの候補

    async void Cleanup_Expanding(Expander sender, ExpanderExpandingEventArgs args) => await LoadCleanupAsync();

    async Task LoadCleanupAsync()
    {
        CleanupList.Children.Clear();
        CleanupList.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
        var sources = InboxService.Instance.Sources.ToList();
        var now = DateTime.Now;
        var candidates = await Task.Run(() => sources.SelectMany(s => Core.Files.CleanupCandidates.ScanDownloads(s, now)).ToList());
        CleanupList.Children.Clear();
        if (candidates.Count == 0)
        {
            CleanupList.Children.Add(new TextBlock { Text = "片付けの候補はありません。", FontSize = 12 });
            return;
        }
        CleanupList.Children.Add(new TextBlock { Text = $"{candidates.Count:N0} 件・合計 {Format.HumanSize(candidates.Sum(c => c.Size))}。選んでごみ箱へ移せます（Ctrl+Z で戻せます）。", FontSize = 12 });
        var checks = new List<(CheckBox Box, string Path)>();
        foreach (var c in candidates.Take(200))
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{Path.GetFileName(c.Path)}（{Format.HumanSize(c.Size)}）", Tag = "data", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = c.Reason, FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            var box = new CheckBox { Content = text, IsChecked = c.Kind != Core.Files.CleanupKind.Unused };
            ToolTipService.SetToolTip(box, c.Path);
            checks.Add((box, c.Path));
            CleanupList.Children.Add(box);
        }
        var recycle = new Button { Content = "選んだものをごみ箱へ" };
        recycle.Click += async (_, _) =>
        {
            var paths = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToList();
            if (paths.Count == 0) return;
            if (FileOperations.Instance.Recycle(paths) > 0) { ViewModel.Refresh(); await LoadCleanupAsync(); }
        };
        CleanupList.Children.Add(recycle);
    }
}
