using System.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Contracts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>容量分析・クォータ管理（04_画面設計 S06）。ゾーンの使用量、フォルダ別の内訳、大きいファイル。</summary>
public sealed partial class CapacityPaneView : UserControl
{
    public CapacityPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;
    readonly StackPanel _body = new() { Spacing = 12, Padding = new Thickness(16, 12, 16, 20) };
    readonly ProgressRing _ring = new() { Width = 18, Height = 18 };
    readonly TextBox _location = new() { MinWidth = 120, PlaceholderText = "調べたいフォルダのパス", VerticalContentAlignment = VerticalAlignment.Center };
    bool _showAll;
    bool _treemap;
    Button _cancel = null!;

    public CapacityPaneView(CapacityPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Padding = new Thickness(16, 14, 16, 14), ColumnSpacing = 12, BorderBrush = Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 16 });
        var title = new TextBlock { Text = "ストレージの使用状況", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(_ring);
        _cancel = TextButton("", "中止", () => ViewModel.Cancel());
        buttons.Children.Add(_cancel);
        buttons.Children.Add(TextButton("\uE8B7", "場所を選ぶ", () => _ = PickLocationAsync()));
        buttons.Children.Add(IconButton("", "更新", () => _ = ViewModel.LoadAsync()));
        Grid.SetColumn(buttons, 2);
        header.Children.Add(buttons);
        root.Children.Add(header);

        var scroll = new ScrollViewer { Content = _body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        ViewModel.PropertyChanged += OnChanged;
        _location.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            string? path = ShellService.ResolveAddress(_location.Text);
            if (path != null && Directory.Exists(path)) _ = ViewModel.OpenAsync(path);
            e.Handled = true;
        };
        InitDuplicates();
        InitCleanup();
        Render();
    }

    async Task PickLocationAsync()
    {
        if (await SuggestMoveDialog.PickFolderAsync() is { } path) await ViewModel.OpenAsync(path);
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CapacityPaneViewModel.Path)) _showAll = false;
        if (e.PropertyName is nameof(CapacityPaneViewModel.Path) or nameof(CapacityPaneViewModel.Report) or nameof(CapacityPaneViewModel.Zones) or nameof(CapacityPaneViewModel.ErrorText)
            or nameof(CapacityPaneViewModel.IsLoading))
            Render();
    }


    void Render()
    {
        // 描けなかった部分があっても画面全体を止めない（原因はログに残す）
        try { RenderCore(); }
        catch (Exception ex)
        {
            AppLog.Error("容量の画面を表示できませんでした", ex);
            _body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = Core.Files.FriendlyError.Explain(ex).ToString() });
        }
    }

    void RenderCore()
    {
        _ring.IsActive = ViewModel.IsLoading;
        _cancel.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        _body.Children.Clear();

        // 実際のディスク使用量と、ファイルの論理サイズ合計を区別して表示する。
        _body.Children.Add(Header("ドライブの使用状況"));
        var otherDrives = new StackPanel { Spacing = 8 };
        foreach (var drive in ViewModel.Drives)
        {
            var card = new Grid { Padding = new Thickness(14), ColumnSpacing = 14, CornerRadius = new CornerRadius(8),
                Background = Brush("CardBackgroundFillColorDefaultBrush") };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock { Text = drive.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"使用中 {Format.HumanSize(drive.Used)}　／　空き {Format.HumanSize(drive.Free)}　／　全体 {Format.HumanSize(drive.Total)}",
                TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = drive.Total > 0 ? 100.0 * drive.Used / drive.Total : 0, Height = 5 });
            card.Children.Add(content);
            var open = TextButton("\uE9D9", "内訳", () => _ = ViewModel.OpenAsync(drive.Path));
            open.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(open, 1);
            card.Children.Add(open);
            if (ViewModel.Path.StartsWith(drive.Path, StringComparison.OrdinalIgnoreCase)) _body.Children.Add(card);
            else otherDrives.Children.Add(card);
        }
        if (otherDrives.Children.Count > 0) _body.Children.Add(new Expander { Header = $"ほかのドライブ ({otherDrives.Children.Count})",
            Content = otherDrives, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });

        _body.Children.Add(Header("この場所の容量"));
        // 入力欄は入力中の文字を保つため使い回す。入れ物ごと使い回し、別の枠へ入れ直さない
        var navigation = _navigation;
        navigation.Children.Clear();
        _location.Text = ViewModel.Path;
        navigation.Children.Add(_location);
        var parent = Directory.GetParent(ViewModel.Path)?.FullName;
        var up = IconButton("\uE74A", "一つ上の容量を見る", () => { if (parent != null) _ = ViewModel.OpenAsync(parent); });
        up.IsEnabled = parent != null;
        Grid.SetColumn(up, 1);
        navigation.Children.Add(up);
        var folderOpen = IconButton("\uE838", "このフォルダを開く", () => _actions.OpenInNewTab(ViewModel.Path));
        Grid.SetColumn(folderOpen, 2);
        navigation.Children.Add(folderOpen);
        _body.Children.Add(navigation);
        if (ViewModel.ErrorText != null) _body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = ViewModel.ErrorText });
        var report = ViewModel.Report;
        if (report != null)
        {
            var summary = new StackPanel { Spacing = 4, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Background = Brush("CardBackgroundFillColorDefaultBrush") };
            summary.Children.Add(Note("この場所に含まれるファイルの合計"));
            summary.Children.Add(new TextBlock { Text = report.IsPartial && report.Total == 0 ? "集計待ち" : (report.IsPartial ? "約 " : "") + Format.HumanSize(report.Total), FontSize = 32, FontWeight = FontWeights.SemiBold });
            summary.Children.Add(new TextBlock { Text = $"ファイル {report.FileCount:N0} 件 ・ フォルダ {report.FolderCount:N0} 件", FontSize = 13 });
            summary.Children.Add(Note(report.Source == "index" ? "検索用の索引をもとに集計。更新後の値は「更新」で再取得できます。" : "読み取れたファイルを集計しています。"));
            if (report.IsPartial) summary.Children.Add(Note("集計途中、または容量を取得できない項目があります。数値は取得できた分の合計です。"));
            _body.Children.Add(summary);
            _body.Children.Add(Note("ドライブの使用中容量にはシステム領域なども含まれるため、上のファイル合計とは一致しないことがあります。"));
            var breakdownHeader = new Grid();
            breakdownHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            breakdownHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            breakdownHeader.Children.Add(Header("フォルダ・ファイル別の内訳（容量の大きい順）"));
            var asMap = new ToggleButton { IsChecked = _treemap, VerticalAlignment = VerticalAlignment.Bottom, Content = TextContent("", "図で表示") }.Named("図で表示");
            asMap.Click += (_, _) => { _treemap = asMap.IsChecked == true; Render(); };
            Grid.SetColumn(asMap, 1);
            breakdownHeader.Children.Add(asMap);
            _body.Children.Add(breakdownHeader);
            _body.Children.Add(Note(_treemap
                ? "面積が容量の大きさです。フォルダーを押すとその中を、右クリックで開く・ごみ箱へ移動などができます。"
                : "右側は容量とこの場所に占める割合です。フォルダ名を押すと、その中の内訳を表示します。右クリックで開く・ごみ箱へ移動などができます。"));
            if (report.Children.Count == 0) _body.Children.Add(Note(report.IsPartial ? "まだ集計できた項目がありません。時間をおいて更新してください。" : "この場所には集計対象のファイルがありません。"));
            else if (_treemap) _body.Children.Add(TreemapView(report));
            else foreach (var item in report.Children.Take(_showAll ? int.MaxValue : 40)) _body.Children.Add(UsageRow(item, report.Total));
            if (!_treemap && !_showAll && report.Children.Count > 40)
                _body.Children.Add(TextButton("\uE70D", $"残り {report.Children.Count - 40:N0} 件も表示", () => { _showAll = true; Render(); }));

            var largest = new Expander { Header = $"内訳の中で大きいファイル（上位 {Math.Min(20, report.Largest.Count)} 件）", IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var largeFiles = new StackPanel { Spacing = 8 };
            largeFiles.Children.Add(Note("上の内訳に含まれるファイルです。容量を重複して加算しません。"));
            foreach (var file in report.Largest.Take(20)) largeFiles.Children.Add(UsageRow(file, report.Total, showLocation: true));
            largest.Content = largeFiles;
            _body.Children.Add(largest);
        }
        else if (ViewModel.IsLoading) _body.Children.Add(Note("容量を集計しています… 一覧の表示を待たずに別の場所へ移動できます。"));

        _body.Children.Add(_duplicates);
        _body.Children.Add(_cleanup);

        var limits = new Expander { Header = "容量の上限と通知（設定）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var limitBody = new StackPanel { Spacing = 8 };
        if (!PolicyService.Instance.Managed) limitBody.Children.Add(Note("容量の上限は現在管理されていません。使用量の確認はそのまま利用できます。"));
        else if (ViewModel.Zones.Count == 0) limitBody.Children.Add(Note("設定されている容量の上限はありません。"));
        foreach (var zone in ViewModel.Zones) limitBody.Children.Add(ZoneRow(zone));
        if (PolicyService.Instance.IsAdmin) limitBody.Children.Add(TextButton("", "容量の上限を追加", () => _ = EditZoneAsync(null)));
        limits.Content = limitBody;
        _body.Children.Add(limits);
    }

    readonly Grid _navigation = NavigationGrid();

    static Grid NavigationGrid()
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    UIElement ZoneRow(ZoneDto zone)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Background = Brush("CardBackgroundFillColorDefaultBrush") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = zone.Path, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"対象: {ScopeText(zone)}　警告: {zone.WarnPercent}%", FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush") });
        grid.Children.Add(text);

        double ratio = zone.UsedBytes < 0 || zone.LimitBytes <= 0 ? 0 : (double)zone.UsedBytes / zone.LimitBytes;
        var usage = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        usage.Children.Add(new ProgressBar
        {
            Value = Math.Min(100, ratio * 100),
            Foreground = new SolidColorBrush(ratio >= 1 ? Microsoft.UI.Colors.IndianRed : ratio * 100 >= zone.WarnPercent ? Microsoft.UI.Colors.Orange : Windows.UI.Color.FromArgb(255, 0x26, 0xA0, 0xDA)),
        });
        usage.Children.Add(new TextBlock
        {
            Text = zone.UsedBytes < 0 ? "使用量: 索引にありません" : $"{Format.HumanSize(zone.UsedBytes)} / {Format.HumanSize(zone.LimitBytes)}（{ratio:P0}）",
            FontSize = 12,
        });
        Grid.SetColumn(usage, 1);
        grid.Children.Add(usage);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(IconButton("", "内訳を見る", () => _ = ViewModel.OpenAsync(zone.Path)));
        if (PolicyService.Instance.IsAdmin)
        {
            buttons.Children.Add(IconButton("", "編集", () => _ = EditZoneAsync(zone)));
            buttons.Children.Add(IconButton("", "削除", () => _ = DeleteZoneAsync(zone)));
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    UIElement UsageRow(UsageItem item, long total, bool showLocation = false)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 5, Padding = new Thickness(4, 6, 4, 6) };
        foreach (double width in new[] { 34.0, -1, 104, 52, 34 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new FileItemIcon { ItemPath = item.Path, IsDirectory = item.IsDirectory, Extension = Path.GetExtension(item.Path) });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string leaf = Path.GetFileName(item.Path.TrimEnd('\\'));
        if (item.IsDirectory)
        {
            var button = new HyperlinkButton { Content = leaf, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 480 };
            button.Click += (_, _) => _ = ViewModel.OpenAsync(item.Path);
            ToolTipService.SetToolTip(button, item.Path + " の内訳を見る");
            name.Children.Add(button);
        }
        else name.Children.Add(new TextBlock { Text = leaf, TextTrimming = TextTrimming.CharacterEllipsis });
        if (showLocation) name.Children.Add(new TextBlock { Text = Path.GetDirectoryName(item.Path), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") });
        ToolTipService.SetToolTip(name, item.Path);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var size = new TextBlock { Text = item.IsPartial && item.Size == 0 ? "未集計" : (item.IsPartial ? "約 " : "") + Format.HumanSize(item.Size), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);
        double percent = total > 0 ? 100.0 * item.Size / total : 0;
        var share = new TextBlock { Text = $"{percent:0.#}%", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush("TextFillColorSecondaryBrush") };
        Grid.SetColumn(share, 3);
        grid.Children.Add(share);
        var reveal = IconButton("\uE838", "場所を開く", () => _actions.RevealInFolderPane(ViewModel, item.Path));
        Grid.SetColumn(reveal, 4);
        grid.Children.Add(reveal);
        var bar = new ProgressBar { Value = percent, Maximum = 100, Height = 4 };
        Grid.SetRow(bar, 1);
        Grid.SetColumn(bar, 1);
        Grid.SetColumnSpan(bar, 3);
        grid.Children.Add(bar);
        grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        grid.RightTapped += (_, e) => { ItemMenu(item.Path, item.IsDirectory).ShowAt(grid, e.GetPosition(grid)); e.Handled = true; };
        return grid;
    }

    string ScopeText(ZoneDto zone) => zone.Scope switch
    {
        "user" => "ユーザー " + (ViewModel.Users.FirstOrDefault(u => u.Id == zone.UserId)?.Account ?? zone.UserId?.ToString()),
        "role" => "ロール " + (ViewModel.Roles.FirstOrDefault(r => r.Id == zone.RoleId)?.Name ?? zone.RoleId?.ToString()),
        _ => "全員",
    };

    // ================================================================ ゾーンの追加・編集（管理者）

    async Task EditZoneAsync(ZoneDto? zone)
    {
        var path = new TextBox { Header = "フォルダ（またはドライブ）", Text = zone?.Path ?? ViewModel.Path, MinWidth = 380 };
        var browse = new Button { Content = "参照…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) => { if (await SuggestMoveDialog.PickFolderAsync() is { } p) path.Text = p; };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathRow.Children.Add(path);
        pathRow.Children.Add(browse);

        var limit = new NumberBox { Header = "上限（GB）", Value = zone != null ? Math.Round(zone.LimitBytes / 1073741824.0, 2) : 100, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1, LargeChange = 10 };
        var warn = new NumberBox { Header = "警告する使用率（%）", Value = zone?.WarnPercent ?? 80, Minimum = 1, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var scope = new ComboBox { Header = "対象", MinWidth = 160 };
        scope.Items.Add(new ComboBoxItem { Content = "全員", Tag = "all" });
        scope.Items.Add(new ComboBoxItem { Content = "特定のユーザー", Tag = "user" });
        scope.Items.Add(new ComboBoxItem { Content = "特定のロール", Tag = "role" });
        var target = new ComboBox { Header = "ユーザー / ロール", MinWidth = 200 };
        void FillTargets()
        {
            target.Items.Clear();
            string s = (string)((ComboBoxItem)scope.SelectedItem).Tag;
            target.Visibility = s == "all" ? Visibility.Collapsed : Visibility.Visible;
            if (s == "user") foreach (var u in ViewModel.Users) target.Items.Add(new ComboBoxItem { Content = u.Account, Tag = u.Id });
            if (s == "role") foreach (var r in ViewModel.Roles) target.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r.Id });
            long? current = s == "user" ? zone?.UserId : zone?.RoleId;
            target.SelectedItem = target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (long)i.Tag == current) ?? target.Items.FirstOrDefault();
        }
        scope.SelectionChanged += (_, _) => FillTargets();
        scope.SelectedIndex = zone?.Scope switch { "user" => 1, "role" => 2, _ => 0 };
        FillTargets();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(pathRow);
        var nums = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        nums.Children.Add(limit);
        nums.Children.Add(warn);
        panel.Children.Add(nums);
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        who.Children.Add(scope);
        who.Children.Add(target);
        panel.Children.Add(who);
        panel.Children.Add(Note("FileBox からの保存・コピー・移動は上限を超えるとブロックされ、他のアプリによる書き込みは警告で知らせます。"));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = zone == null ? "容量の上限を追加" : "容量の上限を編集", Content = panel,
            PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string s2 = (string)((ComboBoxItem)scope.SelectedItem).Tag;
        long? targetId = (target.SelectedItem as ComboBoxItem)?.Tag as long?;
        if (!Directory.Exists(path.Text.Trim())) { App.Window?.ShowInfo("フォルダが見つかりません", InfoBarSeverity.Warning); return; }
        if (!await AdminVerifier.VerifyAsync("容量の上限を変更")) return;
        await Admin(PolicyMethods.AdminSaveZone, new ZoneDto
        {
            Id = zone?.Id ?? 0, Path = Path.GetFullPath(path.Text.Trim()), LimitBytes = (long)(double.IsNaN(limit.Value) ? 0 : limit.Value * 1073741824.0),
            WarnPercent = (int)(double.IsNaN(warn.Value) ? 80 : warn.Value), Scope = s2,
            UserId = s2 == "user" ? targetId : null, RoleId = s2 == "role" ? targetId : null,
        });
    }

    async Task DeleteZoneAsync(ZoneDto zone)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "容量の上限を削除しますか？", Content = zone.Path,
            PrimaryButtonText = "削除", CloseButtonText = "キャンセル",
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        if (!await AdminVerifier.VerifyAsync("容量の上限を削除")) return;
        await Admin(PolicyMethods.AdminDeleteZone, zone.Id);
    }

    async Task Admin(string method, object args)
    {
        try
        {
            await IndexClient.Instance.CallAsync<object>(method, args);
            await PolicyService.Instance.RefreshAsync();
            await ViewModel.LoadAsync();
        }
        catch (Exception ex) { App.Window?.ShowInfo(ex.Message, InfoBarSeverity.Error); }
    }

    // ================================================================ 部品

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 2) };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush") };

    static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 13 }, Padding = new Thickness(7, 5, 7, 5), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    static Button TextButton(string glyph, string text, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content }.Named(text);
        b.Click += (_, _) => action();
        return b;
    }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
