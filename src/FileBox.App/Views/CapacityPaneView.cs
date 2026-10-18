using System.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Contracts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>容量分析・クォータ管理（04_画面設計 S06）。ゾーンの使用量、フォルダ別の内訳、大きいファイル。</summary>
public sealed class CapacityPaneView : UserControl
{
    public CapacityPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;
    readonly StackPanel _body = new() { Spacing = 6, Padding = new Thickness(16, 8, 16, 16) };
    readonly ProgressRing _ring = new() { Width = 18, Height = 18 };

    public CapacityPaneView(CapacityPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 8, BorderBrush = Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 16 });
        var title = new TextBlock { Text = "容量", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(_ring);
        buttons.Children.Add(IconButton("", "更新", () => _ = ViewModel.LoadAsync()));
        _addZone = TextButton("", "ゾーンを追加", () => _ = EditZoneAsync(null));
        buttons.Children.Add(_addZone);
        Grid.SetColumn(buttons, 2);
        header.Children.Add(buttons);
        root.Children.Add(header);

        var scroll = new ScrollViewer { Content = _body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        ViewModel.PropertyChanged += OnChanged;
        Render();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CapacityPaneViewModel.Report) or nameof(CapacityPaneViewModel.Zones) or nameof(CapacityPaneViewModel.ErrorText)
            or nameof(CapacityPaneViewModel.IsLoading))
            Render();
    }

    readonly Button _addZone;

    void Render()
    {
        _ring.IsActive = ViewModel.IsLoading;
        _addZone.Visibility = PolicyService.Instance.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        _body.Children.Clear();

        // ---- クォータゾーン
        _body.Children.Add(Header("クォータゾーン"));
        if (!PolicyService.Instance.Managed)
            _body.Children.Add(Note(PolicyService.Instance.Session?.UnmanagedReason is { } r ? $"容量の管理は無効です: {r}" : "容量の管理は無効です（Indexer に接続中…）"));
        else if (ViewModel.Zones.Count == 0)
            _body.Children.Add(Note(PolicyService.Instance.IsAdmin ? "ゾーンはまだありません。「ゾーンを追加」でフォルダごとの上限を決められます。" : "あなたに適用される容量の上限はありません。"));
        foreach (var zone in ViewModel.Zones) _body.Children.Add(ZoneRow(zone));
        if (PolicyService.Instance.Session is { UserQuotaBytes: long limit } s)
            _body.Children.Add(Note($"あなた（{s.Account}）が FileBox で作成したファイル: {Format.HumanSize(s.UserUsedBytes)} / {Format.HumanSize(limit)}"));

        // ---- 内訳
        var report = ViewModel.Report;
        var crumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        crumb.Children.Add(new TextBlock { Text = "内訳", FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        crumb.Children.Add(new TextBlock { Text = ViewModel.Path + (report != null ? $"（合計 {Format.HumanSize(report.Total)}）" : ""), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") });
        if (Path.GetDirectoryName(ViewModel.Path.TrimEnd('\\')) is { } parent && ViewModel.Path.Length > 3)
            crumb.Children.Add(IconButton("", "一つ上", () => _ = ViewModel.OpenAsync(parent)));
        crumb.Children.Add(IconButton("", "フォルダを開く", () => _actions.OpenInNewTab(ViewModel.Path)));
        _body.Children.Add(crumb);
        if (ViewModel.ErrorText != null) _body.Children.Add(Note(ViewModel.ErrorText));
        if (report == null) return;

        long max = Math.Max(1, report.Children.FirstOrDefault()?.Size ?? 1);
        foreach (var item in report.Children.Take(40))
            _body.Children.Add(UsageRow(item, max, report.Total));
        if (report.Children.Count > 40) _body.Children.Add(Note($"ほか {report.Children.Count - 40} 項目"));

        // ---- 大きいファイル
        _body.Children.Add(Header("大きいファイル"));
        int rank = 1;
        foreach (var file in report.Largest.Take(50))
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{rank++}.", Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = Path.GetFileName(file.Path), TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = Path.GetDirectoryName(file.Path), FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var size = new TextBlock { Text = Format.HumanSize(file.Size), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            var open = IconButton("", "場所を開く", () => _actions.OpenInNewTab(file.Path));
            Grid.SetColumn(open, 3);
            row.Children.Add(open);
            _body.Children.Add(row);
        }
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

    UIElement UsageRow(UsageItem item, long max, long total)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.Children.Add(new FontIcon
        {
            Glyph = item.IsDirectory ? "" : FileKinds.Glyph(Path.GetExtension(item.Path)), FontSize = 14,
            Foreground = item.IsDirectory ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE8, 0xA3, 0x3D)) : Brush("TextFillColorSecondaryBrush"),
        });
        var name = new HyperlinkButton { Content = Path.GetFileName(item.Path), Padding = new Thickness(2, 0, 2, 0), IsEnabled = item.IsDirectory };
        if (!item.IsDirectory) name.Foreground = Brush("TextFillColorPrimaryBrush");
        name.Click += (_, _) => _ = ViewModel.OpenAsync(item.Path);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var bar = new Border
        {
            Height = 10, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = Brush("AccentFillColorDefaultBrush"), Opacity = 0.8,
        };
        var barHost = new Grid();
        barHost.SizeChanged += (_, e) => bar.Width = Math.Max(2, e.NewSize.Width * item.Size / max);
        barHost.Children.Add(bar);
        Grid.SetColumn(barHost, 2);
        grid.Children.Add(barHost);
        var size = new TextBlock
        {
            Text = $"{Format.HumanSize(item.Size)}（{(total > 0 ? (double)item.Size / total : 0):P0}）",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        Grid.SetColumn(size, 3);
        grid.Children.Add(size);
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
            XamlRoot = XamlRoot, Title = zone == null ? "クォータゾーンを追加" : "クォータゾーンを編集", Content = panel,
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
            XamlRoot = XamlRoot, Title = "クォータゾーンを削除しますか？", Content = zone.Path,
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
        b.Click += (_, _) => action();
        return b;
    }

    static Button TextButton(string glyph, string text, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content };
        b.Click += (_, _) => action();
        return b;
    }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
