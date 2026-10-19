using System.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Security;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FileBox.App.Views;

/// <summary>ごみ箱・ネットワークの区画。どちらもエクスプローラーを開かずに FileBox の中で扱う。</summary>
public sealed class ShellPaneView : UserControl
{
    const int MaxRows = 2000;

    public ShellPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;
    readonly FontIcon _icon = new() { FontSize = 20 };
    readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _summary = new() { FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressRing _ring = new() { Width = 20, Height = 20 };
    readonly ListView _list = new() { Margin = new Thickness(4, 6, 4, 0), SelectionMode = ListViewSelectionMode.Extended };
    readonly TextBlock _empty = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, MaxWidth = 420, Margin = new Thickness(32), Foreground = Brush("TextFillColorSecondaryBrush"),
    };
    int _loadVersion;

    public ShellPaneView(ShellPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12, BorderBrush = Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_icon);
        var titles = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_title);
        titles.Children.Add(_summary);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_buttons, 2);
        header.Children.Add(_buttons);
        root.Children.Add(header);

        _list.ItemContainerStyle = ItemStyle();
        _list.DoubleTapped += List_DoubleTapped;
        _list.RightTapped += List_RightTapped;
        _list.KeyDown += List_KeyDown;
        _list.SelectionChanged += (_, _) => { _actions.ActivatePane(ViewModel); UpdateSummary(); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_list, "項目の一覧");
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_empty);
        Content = root;

        ViewModel.PropertyChanged += OnChanged;
        _ = LoadAsync();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellPaneViewModel.Location)) _ = LoadAsync();
    }

    static Style ItemStyle()
    {
        var style = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"] };
        style.Setters.Add(new Setter(MinHeightProperty, 56));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(6)));
        return style;
    }

    // ================================================================ 読み込み

    List<RecycleItem> _recycled = new();

    async Task LoadAsync()
    {
        int version = ++_loadVersion;
        _list.Items.Clear();
        _empty.Visibility = Visibility.Collapsed;
        _ring.IsActive = true;
        BuildHeader();
        try
        {
            if (ViewModel.IsRecycleBin)
            {
                var items = await Task.Run(RecycleBin.List);
                if (version != _loadVersion) return;
                _recycled = items;
                foreach (var item in items.Take(MaxRows)) _list.Items.Add(RecycleRow(item));
                ShowEmpty(items.Count == 0 ? "ごみ箱は空です。" : null);
            }
            else if (ViewModel.Location == ShellPaneViewModel.Network)
            {
                var places = NetworkBrowser.MappedDrives();
                foreach (var p in places) _list.Items.Add(PlaceRow(p));
                var computers = await NetworkBrowser.ComputersAsync();
                if (version != _loadVersion) return;
                foreach (var c in computers) _list.Items.Add(PlaceRow(c));
                ShowEmpty(_list.Items.Count == 0
                    ? "ネットワーク上のコンピューターが見つかりません。\nネットワーク探索がオフの場合は、上の欄に「\\\\サーバー名」または「\\\\サーバー名\\共有名」を入力して開けます。"
                    : null);
            }
            else
            {
                var shares = await NetworkBrowser.SharesAsync(ViewModel.Location);
                if (version != _loadVersion) return;
                foreach (var s in shares) _list.Items.Add(PlaceRow(s));
                ShowEmpty(shares.Count == 0 ? "共有フォルダはありません。" : null);
            }
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) ShowEmpty($"読み込めませんでした: {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion) _ring.IsActive = false;
            UpdateSummary();
        }
    }

    void ShowEmpty(string? text)
    {
        _empty.Text = text ?? "";
        _empty.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void BuildHeader()
    {
        _buttons.Children.Clear();
        _buttons.Children.Add(_ring);
        if (ViewModel.IsRecycleBin)
        {
            _icon.Glyph = "\uE74D";
            _icon.Foreground = Converters.HexBrush("#8B8D98");
            _title.Text = "ごみ箱";
            _list.SelectionMode = ListViewSelectionMode.Extended;
            _buttons.Children.Add(TextButton("\uE7A7", "元に戻す", () => _ = RestoreAsync(SelectedRecycled()), accent: true));
            _buttons.Children.Add(TextButton("\uE74D", "完全に削除", () => _ = DeleteAsync(SelectedRecycled())));
            _buttons.Children.Add(TextButton("\uE894", "ごみ箱を空にする", EmptyBin));
        }
        else
        {
            _icon.Glyph = "\uE968";
            _icon.Foreground = Converters.HexBrush("#05A2C2");
            _title.Text = ViewModel.Location == ShellPaneViewModel.Network ? "ネットワーク" : ViewModel.Location;
            _list.SelectionMode = ListViewSelectionMode.Single;
            if (ViewModel.Location != ShellPaneViewModel.Network)
                _buttons.Children.Add(IconButton("\uE74A", "ネットワークに戻る", () => ViewModel.Location = ShellPaneViewModel.Network));
            var address = new TextBox { PlaceholderText = @"\\サーバー名\共有名", Width = 240, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(address, "ネットワークの場所");
            address.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { OpenAddress(address.Text); e.Handled = true; } };
            _buttons.Children.Add(address);
            _buttons.Children.Add(TextButton("\uE8AD", "開く", () => OpenAddress(address.Text)));
        }
        _buttons.Children.Add(IconButton("\uE72C", "最新の情報に更新", () => _ = LoadAsync()));
    }

    void UpdateSummary()
    {
        if (ViewModel.IsRecycleBin)
        {
            long total = _recycled.Sum(r => r.Size);
            int selected = _list.SelectedItems.Count;
            _summary.Text = _recycled.Count == 0 ? "" : $"{_recycled.Count:N0} 個の項目 · {Format.HumanSize(total)}"
                + (selected > 0 ? $" · {selected:N0} 個選択" : "")
                + (_recycled.Count > MaxRows ? $"（新しい {MaxRows:N0} 件を表示）" : "");
        }
        else _summary.Text = _ring.IsActive ? "探しています…" : $"{_list.Items.Count:N0} 個の場所";
    }

    // ================================================================ 行

    UIElement RecycleRow(RecycleItem item)
    {
        string ext = item.IsDirectory ? "" : Path.GetExtension(item.Name).ToLowerInvariant();
        var icon = new FontIcon
        {
            Glyph = item.IsDirectory ? "\uE8B7" : FileKinds.Glyph(ext),
            FontSize = 20,
            Foreground = Converters.ItemBrush(item.OriginalPath, ext, item.IsDirectory, false),
        };
        string deleted = item.DeletedAt == DateTime.MinValue ? "" : $" · 削除 {item.DeletedAt:yyyy/MM/dd HH:mm}";
        string size = item.IsDirectory ? "" : $" · {Format.HumanSize(item.Size)}";
        return Row(icon, item.Name, $"元の場所: {item.OriginalFolder}{deleted}{size}", item);
    }

    UIElement PlaceRow(NetworkPlace place)
    {
        var (glyph, color) = place.Kind switch
        {
            NetworkPlaceKind.Computer => ("\uE7F8", "#3E63DD"),
            NetworkPlaceKind.Drive => ("\uE8CE", "#05A2C2"),
            _ => ("\uE8B7", null),
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = color != null ? Converters.HexBrush(color) : Converters.IconBrush(true, false) };
        string kind = place.Kind switch { NetworkPlaceKind.Computer => "コンピューター", NetworkPlaceKind.Drive => "ネットワーク ドライブ", _ => "共有フォルダ" };
        return Row(icon, place.Name, $"{kind} · {place.Path}", place);
    }

    static UIElement Row(FontIcon icon, string name, string detail, object tag)
    {
        var grid = new Grid { ColumnSpacing = 10, Tag = tag };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(text, detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    static object? TagOf(object? row) => (row as FrameworkElement)?.Tag;

    List<RecycleItem> SelectedRecycled() => _list.SelectedItems.Select(TagOf).OfType<RecycleItem>().ToList();

    static object? RowTag(RoutedEventArgs e)
    {
        for (var fe = e.OriginalSource as FrameworkElement; fe != null; fe = VisualTreeHelper.GetParent(fe) as FrameworkElement)
            if (fe.Tag is RecycleItem or NetworkPlace) return fe.Tag;
        return null;
    }

    // ================================================================ 操作

    void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        switch (RowTag(e))
        {
            case NetworkPlace p: OpenPlace(p); break;
            case RecycleItem r: _ = RestoreAsync(new List<RecycleItem> { r }); break;
        }
    }

    void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && TagOf(_list.SelectedItem) is NetworkPlace p) { OpenPlace(p); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && ViewModel.IsRecycleBin) { _ = DeleteAsync(SelectedRecycled()); e.Handled = true; }
    }

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var tag = RowTag(e);
        if (tag == null) return;
        var row = _list.Items.FirstOrDefault(i => ReferenceEquals(TagOf(i), tag));
        if (row != null && !_list.SelectedItems.Contains(row))
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = row;
        }
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (tag is RecycleItem item)
        {
            var selected = SelectedRecycled();
            Add("元に戻す", "\uE7A7", () => _ = RestoreAsync(selected));
            Add("完全に削除", "\uE74D", () => _ = DeleteAsync(selected));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("元の場所を開く", "\uE838", () => _actions.RevealInFolderPane(ViewModel, item.OriginalFolder));
        }
        else if (tag is NetworkPlace place)
        {
            Add("開く", "\uE8AD", () => OpenPlace(place));
            if (place.Kind != NetworkPlaceKind.Computer)
            {
                Add("新しいタブで開く", "\uE8A7", () => _actions.OpenInNewTab(place.Path));
                Add("お気に入りに追加", "\uE734", () => _actions.AddFavorite(place.Path));
            }
            Add("パスをコピー", "\uE8C8", () => ShellService.CopyText(place.Path));
        }
        menu.ShowAt(_list, e.GetPosition(_list));
        e.Handled = true;
    }

    void OpenPlace(NetworkPlace place)
    {
        if (place.Kind == NetworkPlaceKind.Computer) ViewModel.Location = place.Path;
        else _actions.RevealInFolderPane(ViewModel, place.Path);
    }

    void OpenAddress(string text)
    {
        string path = text.Trim().Replace('/', '\\');
        if (path.Length == 0) return;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) path = @"\\" + path.TrimStart('\\');
        // \\サーバー だけなら共有の一覧、\\サーバー\共有… ならフォルダとして開く
        if (path.TrimEnd('\\').Count(c => c == '\\') <= 2) ViewModel.Location = path.TrimEnd('\\');
        else _actions.RevealInFolderPane(ViewModel, path);
    }

    async Task RestoreAsync(List<RecycleItem> items)
    {
        if (items.Count == 0) return;
        int done = 0;
        string? last = null;
        foreach (var item in items)
        {
            if (!Guard.Can(Permission.Create, item.OriginalFolder, "move")) continue;
            try
            {
                last = await Task.Run(() => RecycleBin.Restore(item));
                Guard.Done("move", item.DataPath, last, "ごみ箱から元に戻す");
                done++;
            }
            catch (Exception ex) { App.ShowError($"「{item.Name}」を元に戻せませんでした", ex); }
        }
        await LoadAsync();
        if (done > 0)
            App.Window?.ShowInfo($"{done} 個の項目を元の場所に戻しました", InfoBarSeverity.Success,
                last != null ? "場所を開く" : null, () => { if (last != null) _actions.RevealInFolderPane(ViewModel, last); });
    }

    async Task DeleteAsync(List<RecycleItem> items)
    {
        if (items.Count == 0) return;
        string what = items.Count == 1 ? $"「{items[0].Name}」" : $"{items.Count} 個の項目";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "完全に削除", $"{what}を完全に削除します。この操作は元に戻せません。", "削除")) return;
        foreach (var item in items)
        {
            if (!Guard.Can(Permission.Delete, item.OriginalPath, "delete")) continue;
            try
            {
                await Task.Run(() => RecycleBin.Delete(item));
                Guard.Done("delete", item.OriginalPath, detail: "ごみ箱から完全に削除");
            }
            catch (Exception ex) { App.ShowError($"「{item.Name}」を削除できませんでした", ex); }
        }
        await LoadAsync();
    }

    void EmptyBin()
    {
        if (_recycled.Count == 0) return;
        if (_recycled.Any(r => !Guard.Can(Permission.Delete, r.OriginalPath, "delete"))) return;
        var hwnd = App.Window != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.Window) : IntPtr.Zero;
        RecycleBin.Empty(hwnd);
        if (RecycleBin.List().Count == 0) Guard.Done("delete", null, detail: $"ごみ箱を空にする（{_recycled.Count} 個）");
        _ = LoadAsync();
    }

    // ================================================================ 部品

    static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, MinWidth = 40, MinHeight = 36, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    static Button TextButton(string glyph, string text, Action action, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content, MinHeight = 36 };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += (_, _) => action();
        return b;
    }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
