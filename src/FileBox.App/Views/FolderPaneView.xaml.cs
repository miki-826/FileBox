using System.ComponentModel;
using System.Globalization;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Files;
using FileBox.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace FileBox.App.Views;

public sealed partial class FolderPaneView : UserControl
{
    public FolderPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;
    readonly TypeAhead _typeAhead = new();
    ViewMode _mode = ViewMode.Details;

    // 大きさの段階ごとの寸法（1 が既定）
    static readonly double[] RowHeights = { 32, 40, 48, 60 };
    static readonly double[] RowIcons = { 24, 32, 40, 48 };
    static readonly double[] RowFonts = { 12, 14, 16, 18 };
    static readonly double[] IconTiles = { 76, 108, 148, 200 };
    static readonly double[] IconSizes = { 40, 64, 96, 144 };
    static readonly double[] TileWidths = { 200, 240, 280, 330 };
    static readonly double[] TileHeights = { 52, 64, 76, 92 };
    static readonly double[] TileIcons = { 32, 48, 56, 72 };

    public FolderPaneView(FolderPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        // ビューと ViewModel は同じ寿命。分割で付け替えると Loaded→Unloaded の順で発火するため Unloaded で購読解除しない
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.ItemsReloaded += OnItemsReloaded;
        ViewPreferences.LayoutChanged += OnLayoutChanged;
        UserMarks.Changed += OnMarksChanged;
        FileOperations.Instance.Journal.Changed += OnJournalChanged;
        ViewModel.Disposed += () =>
        {
            ViewPreferences.LayoutChanged -= OnLayoutChanged;
            UserMarks.Changed -= OnMarksChanged;
            FileOperations.Instance.Journal.Changed -= OnJournalChanged;
        };
        Loaded += (_, _) =>
        {
            UpdateChrome();
            // 新しいタブではビューが表示される前に選択が決まるので、表示後に選択項目までスクロールする
            if (View.SelectedItem != null) View.ScrollIntoView(View.SelectedItem, ScrollIntoViewAlignment.Leading);
        };
        PathCompletion.Attach(AddressBox);
        FolderMenu.AttachTo(Breadcrumb, () => ViewModel.Breadcrumbs.Select(b => b.Path).ToList(), p => _ = ViewModel.NavigateAsync(p));
        foreach (var view in new ListViewBase[] { List, Tiles })
        {
            view.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
            // キーボード（Shift+F10・アプリケーション キー）でも右クリックと同じメニューを出す。マウスの右クリックは List_RightTapped が出す
            view.ContextRequested += (sender, e) =>
            {
                if (e.TryGetPosition(View, out _)) return;
                var item = View.SelectedItem as FileItem;
                var target = (item != null ? View.ContainerFromItem(item) as FrameworkElement : null) ?? View;
                var point = new Windows.Foundation.Point(32, Math.Min(24, target.ActualHeight / 2));
                _menuPoint = ShellContextMenu.ToScreen(target, point, Hwnd);
                BuildContextMenu(item).ShowAt(target, point);
                e.Handled = true;
            };
            // Space は一覧の項目が選択の切り替えに使ってしまうので、先に受け取ってプレビューにする
            view.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Space || _typeAhead.IsTyping(DateTime.Now) || ViewPreferences.CheckBoxes || IsDown(VirtualKey.Control)) return;
                _actions.TogglePreview(ViewModel);
                e.Handled = true;
            };
        }
        UpdateChrome();
        ApplyItems();
    }

    /// <summary>今の表示のしかたで使っている一覧（詳細は ListView、大アイコン・並べて表示は GridView）。</summary>
    ListViewBase View => _mode == ViewMode.Details ? List : Tiles;

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderPaneViewModel.CurrentPath) or nameof(FolderPaneViewModel.Sort) or nameof(FolderPaneViewModel.SortDescending))
            UpdateChrome();
        if (e.PropertyName == nameof(FolderPaneViewModel.Items)) ApplyItems();
    }

    void OnLayoutChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        ApplySelectionMode();
        ApplyColumns(HeaderGrid);
        UpdateExtraHeaders();
        ForEachContainer(ApplyContainer);
    });

    void OnMarksChanged() => DispatcherQueue.TryEnqueue(() => { foreach (var item in ViewModel.Items) item.RefreshMarks(); });

    void OnJournalChanged() => DispatcherQueue.TryEnqueue(() => UndoButton.IsEnabled = FileOperations.Instance.Journal.CanUndo);

    /// <summary>
    /// 一覧に項目を渡す。「日付でまとめる」場所（ダウンロードは既定）は、今日・昨日・今週・先週・今月…の見出しを付ける。
    /// まとまりの中の順番は今の並べ替えのまま。
    /// </summary>
    void ApplyItems()
    {
        var selectedPaths = View.SelectedItems.OfType<FileItem>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mode = ViewModel.IsPc ? ViewMode.Details : ViewPreferences.Mode(ViewModel.CurrentPath);
        if (mode != _mode) SetMode(mode, remember: false);
        bool grouped = !ViewModel.IsPc && ViewPreferences.GroupByDate(ViewModel.CurrentPath);
        GroupButton.IsChecked = grouped;
        GroupButton.IsEnabled = !ViewModel.IsPc;
        object source = ViewModel.Items;
        if (grouped)
        {
            var now = DateTime.Now;
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var groups = ViewModel.Items
                .GroupBy(i => DateGroup.Classify(i.Modified, now, firstDay))
                .OrderBy(g => g.Key)
                .Select(g => new ItemGroup($"{DateGroup.Label(g.Key)}（{g.Count()}）", g))
                .ToList();
            source = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        }
        // 表示していない方の一覧は空にして、二重に描かない
        List.ItemsSource = _mode == ViewMode.Details ? source : null;
        Tiles.ItemsSource = _mode == ViewMode.Details ? null : source;
        foreach (var item in ViewModel.Items.Where(i => selectedPaths.Contains(i.FullPath)))
            if (!View.SelectedItems.Contains(item)) View.SelectedItems.Add(item);
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPc) return;
        ViewPreferences.SetGroupByDate(ViewModel.CurrentPath, GroupButton.IsChecked == true);
        ApplyItems();
    }

    // ---------------------------------------------------------------- 表示のしかた・大きさ

    void SetMode(ViewMode mode, bool remember)
    {
        var selected = View.SelectedItems.OfType<FileItem>().ToList();
        _mode = mode;
        if (remember) ViewPreferences.SetMode(ViewModel.CurrentPath, mode);
        List.Visibility = mode == ViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
        HeaderGrid.Visibility = List.Visibility;
        Tiles.Visibility = mode == ViewMode.Details ? Visibility.Collapsed : Visibility.Visible;
        Tiles.ItemTemplate = (DataTemplate)Resources[mode == ViewMode.Tiles ? "TileTemplate" : "IconTemplate"];
        ApplySelectionMode();
        if (remember)
        {
            ApplyItems();
            foreach (var item in selected) View.SelectedItems.Add(item);
        }
    }

    void ApplySelectionMode()
    {
        foreach (var view in new ListViewBase[] { List, Tiles })
        {
            view.SelectionMode = ViewPreferences.CheckBoxes ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
            view.IsMultiSelectCheckBoxEnabled = ViewPreferences.CheckBoxes;
        }
    }

    void ViewMenu_Opening(object sender, object e)
    {
        DetailsItem.IsChecked = _mode == ViewMode.Details;
        IconsItem.IsChecked = _mode == ViewMode.Icons;
        TilesItem.IsChecked = _mode == ViewMode.Tiles;
        CheckBoxItem.IsChecked = ViewPreferences.CheckBoxes;
        HiddenItem.IsChecked = FolderPaneViewModel.ShowHidden;
        DetailsItem.IsEnabled = IconsItem.IsEnabled = TilesItem.IsEnabled = !ViewModel.IsPc;
    }

    void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (Enum.TryParse<ViewMode>((string)((FrameworkElement)sender).Tag, out var mode) && mode != _mode) SetMode(mode, remember: true);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ViewPreferences.SetZoom(ViewPreferences.Zoom + 1);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ViewPreferences.SetZoom(ViewPreferences.Zoom - 1);
    void CheckBoxes_Click(object sender, RoutedEventArgs e) => ViewPreferences.SetCheckBoxes(CheckBoxItem.IsChecked);
    void Hidden_Click(object sender, RoutedEventArgs e) => App.Window?.ToggleHidden();
    void Undo_Click(object sender, RoutedEventArgs e) => _ = FileOperations.Instance.UndoAsync();

    /// <summary>Ctrl+ホイールでアイコンと文字の大きさを変える（エクスプローラーと同じ）。</summary>
    void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDown(VirtualKey.Control)) return;
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        ViewPreferences.SetZoom(ViewPreferences.Zoom + (delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    void ForEachContainer(Action<SelectorItem> action)
    {
        for (int i = 0; i < View.Items.Count; i++)
            if (View.ContainerFromIndex(i) is SelectorItem container) action(container);
    }

    /// <summary>1 行（1 マス）に大きさ・列・印を当てる。</summary>
    void ApplyContainer(SelectorItem container)
    {
        int z = ViewPreferences.Zoom;
        if (container.Content is FileItem item) item.RefreshMarks();
        var root = container.ContentTemplateRoot as Grid;
        var icon = root == null ? null : FindIcon(root);
        if (_mode == ViewMode.Details)
        {
            container.MinHeight = RowHeights[z];
            if (icon != null) icon.IconSize = RowIcons[z];
            if (root != null)
            {
                root.ColumnDefinitions[0].Width = new GridLength(RowIcons[z] + 10);
                ApplyColumns(root);
                if (root.Children[1] is StackPanel { Children.Count: > 0 } name && name.Children[0] is Grid nameRow && nameRow.Children[0] is TextBlock text)
                    text.FontSize = RowFonts[z];
            }
            if (container.Content is FileItem { IsDrive: false } file) _ = LoadExtrasAsync(file);
        }
        else if (_mode == ViewMode.Icons)
        {
            container.Width = IconTiles[z];
            container.Height = IconTiles[z] + 36;
            if (icon != null) icon.IconSize = IconSizes[z];
        }
        else
        {
            container.Width = TileWidths[z];
            container.Height = TileHeights[z];
            if (icon != null) icon.IconSize = TileIcons[z];
        }
    }

    static FileItemIcon? FindIcon(DependencyObject root)
    {
        if (root is FileItemIcon icon) return icon;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindIcon(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ---------------------------------------------------------------- 追加の列（撮影日時・長さ など）

    string ColumnsKey => string.Join(",", ViewPreferences.Columns);

    async Task LoadExtrasAsync(FileItem item)
    {
        if (ViewPreferences.Columns.Count == 0) return;
        string key = ColumnsKey;
        if (item.ExtrasKey == key) return;
        item.ExtrasKey = key;
        var columns = ViewPreferences.Columns.ToList();
        var values = await FileProperties.ReadAsync(item.FullPath, columns);
        if (ColumnsKey != key) return;
        item.SetExtras(key, columns.Select(c => values.TryGetValue(c, out var v) ? v : "").ToList());
    }

    void UpdateExtraHeaders()
    {
        var headers = new[] { Extra0Header, Extra1Header, Extra2Header };
        for (int i = 0; i < headers.Length; i++)
        {
            string? key = i < ViewPreferences.Columns.Count ? ViewPreferences.Columns[i] : null;
            headers[i].Visibility = key != null && _columns[0] > 0 ? Visibility.Visible : Visibility.Collapsed;
            headers[i].Content = key == null ? "" : FileProperties.Columns.FirstOrDefault(c => c.Key == key).Label + (_extraSort == i ? (_extraDesc ? " ▼" : " ▲") : "");
        }
    }

    void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var (key, label) in FileProperties.Columns)
        {
            var mi = new ToggleMenuFlyoutItem { Text = label, IsChecked = ViewPreferences.Columns.Contains(key) };
            mi.Click += (_, _) => ViewPreferences.ToggleColumn(key);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "追加できる列は 3 つまでです", IsEnabled = false });
        menu.ShowAt(HeaderGrid, e.GetPosition(HeaderGrid));
        e.Handled = true;
    }

    int _extraSort = -1;
    bool _extraDesc;

    /// <summary>追加の列で並べ替える（まだ読んでいない項目の値を先に読む）。</summary>
    async void SortExtra_Click(object sender, RoutedEventArgs e)
    {
        int index = int.Parse((string)((FrameworkElement)sender).Tag);
        _extraDesc = _extraSort == index && !_extraDesc;
        _extraSort = index;
        var items = ViewModel.Items.ToList();
        foreach (var item in items.Where(i => !i.IsDrive).Take(3000)) await LoadExtrasAsync(item);
        string Value(FileItem i) => index switch { 0 => i.Extra0, 1 => i.Extra1, _ => i.Extra2 };
        var sorted = items.OrderByDescending(i => i.IsDirectory);
        sorted = _extraDesc ? sorted.ThenByDescending(Value, NaturalComparer.Instance) : sorted.ThenBy(Value, NaturalComparer.Instance);
        ViewModel.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(sorted);
        UpdateExtraHeaders();
    }

    /// <summary>タブに区画が1つのときは、上の共通バー（戻る・アドレス）を使うので区画内のナビゲーションを隠す。</summary>
    public void SetNavigationVisible(bool visible)
    {
        NavigationRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateChrome()
    {
        Breadcrumb.ItemsSource = ViewModel.Breadcrumbs.Select(b => b.Name).ToList();
        BackButton.IsEnabled = ViewModel.CanGoBack;
        ForwardButton.IsEnabled = ViewModel.CanGoForward;
        UpButton.IsEnabled = ViewModel.CanGoUp;
        string arrow = ViewModel.SortDescending ? " ▼" : " ▲";
        NameHeader.Content = "名前" + (ViewModel.Sort == SortColumn.Name ? arrow : "");
        ModifiedHeader.Content = "更新日時" + (ViewModel.Sort == SortColumn.Modified ? arrow : "");
        TypeHeader.Content = "種類" + (ViewModel.Sort == SortColumn.Type ? arrow : "");
        SizeHeader.Content = "サイズ" + (ViewModel.Sort == SortColumn.Size ? arrow : "");
        _extraSort = -1;
        UpdateExtraHeaders();
        UpdateCommandButtons();
        UndoButton.IsEnabled = FileOperations.Instance.Journal.CanUndo;
        string? downloads = ShellService.KnownFolder(ShellService.QuickAccess[2].Id);
        OrganizeButton.Visibility = downloads != null && string.Equals(downloads.TrimEnd('\\'), ViewModel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateCommandButtons()
    {
        var selected = Selected;
        bool editable = selected.Count > 0 && selected.All(i => !i.IsDrive);
        CutButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = RenameButton.IsEnabled = editable;
        NewFolderButton.IsEnabled = PasteButton.IsEnabled = !ViewModel.IsPc;
    }

    async void Cut_Click(object sender, RoutedEventArgs e) => await CopySelectionAsync(cut: true);
    async void Copy_Click(object sender, RoutedEventArgs e) => await CopySelectionAsync(cut: false);
    async Task CopySelectionAsync(bool cut)
    {
        var paths = Selected.Where(i => !i.IsDrive).Select(i => i.FullPath).ToList();
        if (paths.Count > 0) await ShellService.CopyToClipboardAsync(paths, cut);
    }
    async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { await PasteAsync(); }
        catch (Exception ex) { App.ShowError("貼り付けできませんでした", ex); }
    }
    async void Rename_Click(object sender, RoutedEventArgs e) => await RenameSelectionAsync();
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    void Organize_Click(object sender, RoutedEventArgs e) => _actions.OpenInbox();

    void OnItemsReloaded()
    {
        // 「上へ」や「場所を開く」で元の項目を選択状態にする
        string? target = ViewModel.PendingSelection;
        ViewModel.PendingSelection = null;
        if (target == null) return;
        var item = ViewModel.Items.FirstOrDefault(i => string.Equals(i.FullPath, target, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        View.SelectedItem = item;
        View.ScrollIntoView(item);
    }

    // ---------------------------------------------------------------- 列幅（区画が狭いときは日時・種類・追加の列を隠す）

    double[] _columns = { 150, 140, 110 };

    void List_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        double[] next = w >= 700 ? new double[] { 150, 140, 110 }
                      : w >= 520 ? new double[] { 140, 0, 100 }
                      : new double[] { 0, 0, 90 };
        if (next.SequenceEqual(_columns)) return;
        _columns = next;
        ApplyColumns(HeaderGrid);
        UpdateExtraHeaders();
        if (_mode == ViewMode.Details) ForEachContainer(c => { if (c.ContentTemplateRoot is Grid g) ApplyColumns(g); });
    }

    void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        ApplyContainer(args.ItemContainer);
    }

    void ApplyColumns(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 8) return;
        for (int c = 0; c < 3; c++)
        {
            grid.ColumnDefinitions[c + 2].Width = new GridLength(_columns[c]);
            SetColumnVisible(grid, c + 2, _columns[c] > 0);
        }
        // 追加の列は、狭いときは隠す
        for (int c = 0; c < 3; c++)
        {
            bool on = c < ViewPreferences.Columns.Count && _columns[0] > 0;
            grid.ColumnDefinitions[c + 5].Width = new GridLength(on ? 130 : 0);
            SetColumnVisible(grid, c + 5, on);
        }
    }

    static void SetColumnVisible(Grid grid, int column, bool visible)
    {
        foreach (var child in grid.Children)
            if (child is FrameworkElement fe && Grid.GetColumn(fe) == column && fe is not Button)
                fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusList()
    {
        View.Focus(FocusState.Programmatic);
    }

    IReadOnlyList<FileItem> Selected => View.SelectedItems.Cast<FileItem>().ToList();

    /// <summary>プレビューに出す項目（1 つだけ選んでいるとき）。</summary>
    public FileItem? SingleSelection => View.SelectedItems.Count == 1 ? View.SelectedItem as FileItem : null;

    // ---------------------------------------------------------------- ナビゲーション

    void Back_Click(object sender, RoutedEventArgs e) => _ = ViewModel.GoBackAsync();
    void Forward_Click(object sender, RoutedEventArgs e) => _ = ViewModel.GoForwardAsync();
    void Up_Click(object sender, RoutedEventArgs e) => _ = ViewModel.GoUpAsync();
    void Refresh_Click(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

    void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        var crumbs = ViewModel.Breadcrumbs;
        if (args.Index < crumbs.Count) _ = ViewModel.NavigateAsync(crumbs[args.Index].Path);
    }

    void BreadcrumbHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // パンくずの項目以外（余白）をクリックしたら直接入力モードへ
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is string) return;
        BeginEditAddress();
    }

    public void BeginEditAddress()
    {
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = ViewModel.IsPc ? "PC" : ViewModel.CurrentPath;
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    void EndEditAddress()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            string? path = ShellService.ResolveAddress(AddressBox.Text);
            EndEditAddress();
            if (path == null) ViewModel.ErrorText = $"場所が見つかりません: {AddressBox.Text}";
            else if (File.Exists(path)) ShellService.Open(path);
            else _ = ViewModel.NavigateAsync(path);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            EndEditAddress();
            FocusList();
            e.Handled = true;
        }
    }

    void AddressBox_LostFocus(object sender, RoutedEventArgs e) => EndEditAddress();

    void SortName_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort(SortColumn.Name);
    void SortModified_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort(SortColumn.Modified);
    void SortType_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort(SortColumn.Type);
    void SortSize_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort(SortColumn.Size);

    void Preview_Click(object sender, RoutedEventArgs e) => _actions.TogglePreview(ViewModel);

    async void NewFolder_Click(object sender, RoutedEventArgs e) => await NewFolderAsync();

    /// <summary>名前を先に聞いてから作る（キャンセルしたら何も作らない）。</summary>
    async Task NewFolderAsync()
    {
        if (ViewModel.IsPc || !Guard.Can(Permission.Create, ViewModel.CurrentPath, "save")) return;
        string suggested = Path.GetFileName(UniqueName.Path(Path.Combine(ViewModel.CurrentPath, "新しいフォルダー")));
        string? name = await Dialogs.PromptAsync(XamlRoot, "新しいフォルダーの名前", suggested, "作成");
        if (name == null) return;
        if (BatchRename.Validate(name) is { } problem) { App.Window?.ShowInfo(problem, InfoBarSeverity.Warning); return; }
        if (FileOperations.Instance.CreateFolder(ViewModel.CurrentPath, name) is { } path)
        {
            ViewModel.PendingSelection = path;
            await ViewModel.RefreshAsync();
        }
    }

    // ---------------------------------------------------------------- 一覧操作

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, View)) return;
        _actions.ActivatePane(ViewModel);
        ViewModel.OnSelectionChanged(Selected);
        UpdateCommandButtons();
    }

    void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) OpenItem(item);
    }

    void OpenItem(FileItem item)
    {
        if (item.IsDirectory) _ = ViewModel.NavigateAsync(item.FullPath);
        else if (Guard.Can(Permission.Read, item.FullPath, "open")) ShellService.Open(item.FullPath);
    }

    /// <summary>キーを打つと、その文字で始まる項目へ移る（続けて打つと絞り込み、同じ文字の繰り返しで次へ）。</summary>
    void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return;
        if (e.Character == ' ' && !_typeAhead.IsTyping(DateTime.Now)) return;   // Space はプレビュー
        var items = View.Items.OfType<FileItem>().ToList();
        int index = _typeAhead.Next(e.Character, items.Select(i => i.Name).ToList(), View.SelectedIndex, DateTime.Now);
        if (index < 0) return;
        View.SelectedItems.Clear();
        View.SelectedItem = items[index];
        View.ScrollIntoView(items[index]);
        (View.ContainerFromItem(items[index]) as Control)?.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
        if (item != null && !View.SelectedItems.Contains(item))
        {
            View.SelectedItems.Clear();
            View.SelectedItem = item;
        }
        if (item == null) View.SelectedItems.Clear();
        _menuPoint = ShellContextMenu.ToScreen(View, e.GetPosition(View), Hwnd);
        // Shift+右クリックはエクスプローラーと同じく Windows のメニュー（拡張項目付き）を直接出す
        if (IsDown(VirtualKey.Shift) && !ViewModel.IsPc) ShowWindowsMenu(item);
        else BuildContextMenu(item).ShowAt(View, e.GetPosition(View));
        e.Handled = true;
    }

    (int X, int Y) _menuPoint;

    static IntPtr Hwnd => App.Window != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.Window) : IntPtr.Zero;

    /// <summary>Windows 標準の右クリックメニュー（他アプリの追加項目を含む）。</summary>
    void ShowWindowsMenu(FileItem? item)
    {
        if (item != null) ShellContextMenu.Show(Hwnd, Selected.Select(s => s.FullPath).ToList(), _menuPoint.X, _menuPoint.Y);
        else ShellContextMenu.ShowBackground(Hwnd, ViewModel.CurrentPath, _menuPoint.X, _menuPoint.Y);
        _ = ViewModel.RefreshAsync();
    }

    MenuFlyout BuildContextMenu(FileItem? item)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, string? key = null)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (key != null) mi.KeyboardAcceleratorTextOverride = key;
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        var selected = Selected;
        var paths = selected.Where(s => !s.IsDrive).Select(s => s.FullPath).ToList();
        if (item != null)
        {
            Add("開く", "", () => OpenItem(item), "Enter");
            if (item.IsDirectory)
            {
                Add("新しいタブで開く", "", () => _actions.OpenInNewTab(item.FullPath));
                Add("隣の区画で開く", "", () => _actions.OpenInAdjacentPane(ViewModel, item.FullPath));
                Add("お気に入りに追加", "", () => _actions.AddFavorite(item.FullPath));
                Add("ターミナルで開く", "", () => ShellService.OpenTerminal(item.FullPath));
            }
            Add(FileKinds.IsArchive(item.Extension) ? "中身を見る（プレビュー）" : "プレビュー・詳細", "", () => _actions.TogglePreview(ViewModel), "Space");
            if (!item.IsDrive) Add("変換・整理…", "\uE8A5", () => _ = OpenToolsAsync(paths));
            if (item.IsDirectory && !item.IsDrive) menu.Items.Add(FolderColorMenu(selected.Where(s => s.IsDirectory).Select(s => s.FullPath).ToList()));
            if (!item.IsDrive)
            {
                menu.Items.Add(MarksMenu(paths));
                if (paths.Any(CloudFiles.IsInCloud)) menu.Items.Add(CloudMenu(paths));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.IsDrive)
            {
                Add("切り取り", "", () => _ = ShellService.CopyToClipboardAsync(paths, cut: true), "Ctrl+X");
                Add("コピー", "", () => _ = ShellService.CopyToClipboardAsync(paths, cut: false), "Ctrl+C");
            }
            var copyPath = new MenuFlyoutSubItem { Text = "パスをコピー", Icon = new FontIcon { Glyph = "" } };
            void AddCopy(string text, string value, string? key = null)
            {
                var mi = new MenuFlyoutItem { Text = text };
                if (key != null) mi.KeyboardAcceleratorTextOverride = key;
                mi.Click += (_, _) => ShellService.CopyText(value);
                copyPath.Items.Add(mi);
            }
            AddCopy("そのまま", string.Join(Environment.NewLine, selected.Select(s => s.FullPath)), "Ctrl+Shift+C");
            AddCopy("引用符付き（\"…\"）", string.Join(Environment.NewLine, selected.Select(s => $"\"{s.FullPath}\"")));
            AddCopy("名前だけ", string.Join(Environment.NewLine, selected.Select(s => s.Name)));
            menu.Items.Add(copyPath);
            if (!item.IsDrive)
            {
                Add("最適な場所へ移動…", "", () => _ = SmartMoveAsync(selected));
                Add(selected.Count > 1 ? $"{selected.Count} 件の名前を一括で変更…" : "名前の変更", "", () => _ = RenameSelectionAsync(), "F2");
                if (selected.Count == 1) Add("名前の一括変更…", "", () => _ = BatchRenameAsync(paths));
                Add("削除（ごみ箱へ）", "", () => DeleteSelected(), "Delete");
                if (selected.Count == 2) Add("2 つを比較", "", () => _ = CompareDialog.ShowAsync(XamlRoot, selected[0].FullPath, selected[1].FullPath));
                // 圧縮ファイルだけを選んでいるときは出さない（ZIP をさらに ZIP にしても意味がない）
                if (selected.Any(s => s.IsDirectory || !FileKinds.IsArchive(s.Extension)))
                    Add("ZIP に圧縮", "", () => _ = CompressAsync(selected));
                if (!item.IsDirectory && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    Add("展開（中身に合わせて場所を決める）", "", () => _ = ExtractAsync(item));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.IsDrive) Add("その他のオプション（Windows のメニュー）", "", () => ShowWindowsMenu(item), "Shift+右クリック");
            Add("エクスプローラーで表示", "", () => ShellService.ShowInExplorer(item.FullPath));
            menu.Items.Add(LinkMenu.Build(item.FullPath));
            Add("プロパティ", "", () => ShellService.ShowProperties(item.FullPath), "Alt+Enter");
        }
        else if (!ViewModel.IsPc)
        {
            Add("貼り付け", "", () => _ = PasteAsync(), "Ctrl+V");
            Add("新しいフォルダー", "", () => _ = NewFolderAsync(), "Ctrl+Shift+N");
            Add("最新の情報に更新", "", () => _ = ViewModel.RefreshAsync(), "F5");
            if (FileOperations.Instance.Journal.NextUndo is { } undo) Add($"元に戻す: {undo.Describe()}", "", () => _ = FileOperations.Instance.UndoAsync(), "Ctrl+Z");
            Add("その他のオプション（Windows のメニュー）", "", () => ShowWindowsMenu(null), "Shift+右クリック");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("このフォルダをお気に入りに追加", "", () => _actions.AddFavorite(ViewModel.CurrentPath));
            Add("ターミナルで開く", "", () => ShellService.OpenTerminal(ViewModel.CurrentPath));
            Add("パスをコピー", "", () => ShellService.CopyText(ViewModel.CurrentPath));
            Add("エクスプローラーで開く", "", () => ShellService.OpenInExplorer(ViewModel.CurrentPath));
            menu.Items.Add(LinkMenu.Build(ViewModel.CurrentPath));
            Add("プロパティ", "", () => ShellService.ShowProperties(ViewModel.CurrentPath));
        }
        return menu;
    }

    void Tools_Click(object sender, RoutedEventArgs e) => _ = OpenToolsAsync(Selected.Where(s => !s.IsDrive).Select(s => s.FullPath).ToList());

    async Task OpenToolsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await ConversionDialog.ShowAsync(XamlRoot, paths.Count > 0 ? paths : Directory.Exists(ViewModel.CurrentPath) ? new[] { ViewModel.CurrentPath } : Array.Empty<string>());
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.Window?.ShowInfo(ex.Message, InfoBarSeverity.Error); }
    }

    /// <summary>フォルダの色（Teams のように自分で選べる。FileBox の中だけの表示で、Windows 側は変えない）。</summary>
    static MenuFlyoutSubItem FolderColorMenu(IReadOnlyList<string> folders)
    {
        var sub = new MenuFlyoutSubItem { Text = "フォルダの色", Icon = new FontIcon { Glyph = "" } };
        string? current = folders.Count > 0 ? ViewPreferences.FolderColor(folders[0]) : null;
        void Add(string text, string? hex)
        {
            var mi = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase),
                Icon = new FontIcon { Glyph = "", Foreground = hex == null ? Converters.IconBrush(true, false) : Converters.HexBrush(hex) },
            };
            mi.Click += (_, _) => { foreach (var f in folders) ViewPreferences.SetFolderColor(f, hex); };
            sub.Items.Add(mi);
        }
        Add("既定（黄）", null);
        sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var (name, hex) in ViewPreferences.Palette) Add(name, hex);
        return sub;
    }

    /// <summary>星とタグ（FileBox の中だけの印）。</summary>
    MenuFlyoutSubItem MarksMenu(IReadOnlyList<string> paths)
    {
        var sub = new MenuFlyoutSubItem { Text = "星・タグ", Icon = new FontIcon { Glyph = "" } };
        bool starred = paths.All(UserMarks.IsStarred);
        var star = new MenuFlyoutItem { Text = starred ? "星を外す" : "星を付ける", Icon = new FontIcon { Glyph = starred ? "" : "", Foreground = Converters.HexBrush("#E2A300") } };
        star.Click += (_, _) => UserMarks.SetStar(paths, !starred);
        sub.Items.Add(star);
        sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var tag in UserMarks.AllTagNames())
        {
            bool on = paths.All(p => UserMarks.TagsOf(p).Contains(tag));
            var mi = new ToggleMenuFlyoutItem { Text = tag, IsChecked = on };
            mi.Click += (_, _) => UserMarks.SetTag(paths, tag, !on);
            sub.Items.Add(mi);
        }
        var add = new MenuFlyoutItem { Text = "新しいタグ…", Icon = new FontIcon { Glyph = "" } };
        add.Click += async (_, _) =>
        {
            string? name = await Dialogs.PromptAsync(XamlRoot, "タグの名前", "", "付ける");
            if (name != null) UserMarks.SetTag(paths, name, true);
        };
        sub.Items.Add(add);
        return sub;
    }

    /// <summary>OneDrive などのクラウドの保持設定。</summary>
    MenuFlyoutSubItem CloudMenu(IReadOnlyList<string> paths)
    {
        var sub = new MenuFlyoutSubItem { Text = "クラウド（OneDrive）", Icon = new FontIcon { Glyph = "" } };
        var keep = new MenuFlyoutItem { Text = "常にこのデバイスに保持", Icon = new FontIcon { Glyph = "" } };
        keep.Click += async (_, _) =>
        {
            int n = await CloudFiles.SetPinnedAsync(paths, pin: true);
            App.Window?.ShowInfo($"{n} 件を常にこのデバイスに保持します（ダウンロードは同期アプリが行います）");
            _ = ViewModel.RefreshAsync();
        };
        var free = new MenuFlyoutItem { Text = "空き領域を増やす（オンラインのみにする）", Icon = new FontIcon { Glyph = "" } };
        free.Click += async (_, _) =>
        {
            int n = await CloudFiles.SetPinnedAsync(paths, pin: false);
            App.Window?.ShowInfo($"{n} 件をオンラインのみにします（同期アプリが空き領域を戻します）");
            _ = ViewModel.RefreshAsync();
        };
        sub.Items.Add(keep);
        sub.Items.Add(free);
        return sub;
    }

    async Task CompressAsync(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return;
        string name = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : Path.GetFileName(ViewModel.CurrentPath.TrimEnd('\\'));
        string zip = UniqueName.Path(Path.Combine(ViewModel.CurrentPath, name + ".zip"));
        var sources = items.Select(i => i.FullPath).ToList();
        if (!await Guard.TransferAsync(sources, ViewModel.CurrentPath, move: false, "save")) return;
        try
        {
            await ArchiveService.CompressAsync(sources, zip);
            Guard.Created("save", zip);
            ViewModel.PendingSelection = zip;
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("圧縮できませんでした", ex, zip); }
    }

    /// <summary>中身が 1 つのフォルダーならそのまま置き、ばらばらなら ZIP の名前のフォルダーを作って展開する。</summary>
    async Task ExtractAsync(FileItem zip)
    {
        if (!await Guard.TransferAsync(new[] { zip.FullPath }, ViewModel.CurrentPath, move: false, "save")) return;
        try
        {
            var (dest, strip) = await Task.Run(() => ArchiveService.PlanExtract(zip.FullPath));
            var before = strip == null && string.Equals(dest, ViewModel.CurrentPath, StringComparison.OrdinalIgnoreCase)
                ? ArchiveService.List(zip.FullPath).Select(e => e.FullName.Split('/')[0]).Distinct().ToList() : null;
            await ArchiveService.ExtractEntriesAsync(zip.FullPath, null, dest, strip);
            Guard.Created("save", dest);
            ViewModel.PendingSelection = before is { Count: 1 } ? Path.Combine(dest, before[0]) : dest;
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("展開できませんでした", ex, zip.FullPath); }
    }

    /// <summary>「もしかしてこのフォルダ？」で選んだ場所へ移動（02_機能設計 §1.1 入口①）。</summary>
    async Task SmartMoveAsync(IReadOnlyList<FileItem> items)
    {
        var moved = await SuggestMoveDialog.RunAsync(XamlRoot, items.Select(i => i.FullPath).ToList());
        if (moved != null) await ViewModel.RefreshAsync();
    }

    /// <summary>1 つなら名前の変更、複数なら一括の名前変更。</summary>
    async Task RenameSelectionAsync()
    {
        var selected = Selected.Where(s => !s.IsDrive).ToList();
        if (selected.Count == 1) await RenameAsync(selected[0]);
        else if (selected.Count > 1) await BatchRenameAsync(selected.Select(s => s.FullPath).ToList());
    }

    async Task RenameAsync(FileItem item)
    {
        if (!Guard.Can(Permission.Modify, item.FullPath, "rename")) return;
        string? name = await Dialogs.PromptAsync(XamlRoot, "名前の変更", item.Name, "変更", selectStem: !item.IsDirectory);
        if (name == null || name == item.Name) return;
        if (BatchRename.Validate(name) is { } problem) { App.Window?.ShowInfo(problem, InfoBarSeverity.Warning); return; }
        if (FileOperations.Instance.Rename(item.FullPath, name) is { } renamed)
        {
            ViewModel.PendingSelection = renamed;
            await ViewModel.RefreshAsync();
        }
    }

    async Task BatchRenameAsync(IReadOnlyList<string> paths)
    {
        // 一覧の並びの順に連番を振る
        var ordered = View.Items.OfType<FileItem>().Select(i => i.FullPath).Where(p => paths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (await BatchRenameDialog.ShowAsync(XamlRoot, ordered.Count == paths.Count ? ordered : paths.ToList())) await ViewModel.RefreshAsync();
    }

    void DeleteSelected()
    {
        var paths = Selected.Where(s => !s.IsDrive).Select(s => s.FullPath).ToList();
        if (paths.Count == 0) return;
        if (FileOperations.Instance.Recycle(paths) > 0) _ = ViewModel.RefreshAsync();
    }

    // ---------------------------------------------------------------- ドラッグ＆ドロップ（他アプリ・エクスプローラー・別の区画とも相互）

    void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileItem>().Where(i => !i.IsDrive).Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy | Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        // 実体（StorageFile）は相手が要求したときに作る
        e.Data.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<Windows.Storage.IStorageItem>();
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(p));
                    else if (File.Exists(p)) items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(p));
                }
                request.SetData(items);
            }
            catch (Exception ex) { AppLog.Error("ドラッグするファイルを用意できませんでした", ex); }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>ドロップ先: フォルダの上ならそのフォルダ、それ以外は表示中のフォルダ。</summary>
    string? DropTarget(DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem { IsDirectory: true } folder) return folder.FullPath;
        return ViewModel.IsPc ? null : ViewModel.CurrentPath;
    }

    /// <summary>エクスプローラーと同じ規則: 同じドライブは移動、別ドライブはコピー。Ctrl でコピー、Shift で移動。</summary>
    static bool ShouldMove(string? source, string target)
    {
        if (IsDown(VirtualKey.Shift)) return true;
        if (IsDown(VirtualKey.Control)) return false;
        return source != null && string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
    }

    string? _dragPeekSource;

    void List_DragLeave(object sender, DragEventArgs e) => _dragPeekSource = null;

    async void List_DragOver(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        if (target == null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }
        // 移動かコピーかの判定に元の場所が要るので、最初の1件だけ覗く（DragOver は何度も来るのでキャッシュ）
        var deferral = e.GetDeferral();
        try
        {
            if (_dragPeekSource == null)
            {
                var items = await e.DataView.GetStorageItemsAsync();
                _dragPeekSource = items.FirstOrDefault()?.Path ?? "";
            }
            bool move = ShouldMove(_dragPeekSource, target);
            e.AcceptedOperation = move ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            string targetName = Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } n ? n : target;
            e.DragUIOverride.Caption = $"{(move ? "移動" : "コピー")}: {targetName}（Ctrl でコピー・Shift で移動）";
        }
        catch (Exception) { e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; }
        finally { deferral.Complete(); }
    }

    async void List_Drop(object sender, DragEventArgs e)
    {
        _dragPeekSource = null;
        string? target = DropTarget(e);
        if (target == null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            // 自分自身や、すでにそのフォルダにあるものへのドロップは何もしない
            paths = paths.Where(p => !string.Equals(Path.GetDirectoryName(p), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                  && !target.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(p, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (paths.Count == 0) return;
            bool move = ShouldMove(paths[0], target);
            var op = await FileOperations.Instance.TransferAsync(paths, target, move);
            if (move && op != null) foreach (var (from, _) in op.Items) SuggestionService.Instance.Record(Path.GetFileName(from), target, null);
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("ドロップした項目を処理できませんでした", ex); }
        finally { deferral.Complete(); }
    }

    async Task PasteAsync()
    {
        if (ViewModel.IsPc) return;
        var (paths, move) = await ShellService.GetClipboardFilesAsync();
        if (paths.Count == 0) return;
        await FileOperations.Instance.TransferAsync(paths, ViewModel.CurrentPath, move);
        await ViewModel.RefreshAsync();
    }

    static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        var selected = Selected;
        var first = selected.FirstOrDefault();
        switch (e.Key)
        {
            case VirtualKey.Enter when alt && first != null:
                ShellService.ShowProperties(first.FullPath);
                break;
            case VirtualKey.Enter when first != null:
                if (selected.Count == 1) OpenItem(first);
                else foreach (var s in selected.Where(s => !s.IsDirectory)) ShellService.Open(s.FullPath);
                break;
            case VirtualKey.Back:
                _ = ViewModel.GoBackAsync();
                break;
            // 名前を打っている途中の Space は頭出しの続き（名前に空白を含むことがある）
            case VirtualKey.Space when !_typeAhead.IsTyping(DateTime.Now):
                _actions.TogglePreview(ViewModel);
                break;
            case VirtualKey.F2 when first != null && !first.IsDrive:
                _ = RenameSelectionAsync();
                break;
            case VirtualKey.Delete:
                DeleteSelected();
                break;
            case VirtualKey.C when ctrl && shift && selected.Count > 0:
                ShellService.CopyText(string.Join(Environment.NewLine, selected.Select(s => s.FullPath)));
                break;
            case VirtualKey.C when ctrl && selected.Count > 0:
                _ = ShellService.CopyToClipboardAsync(selected.Select(s => s.FullPath).ToList(), cut: false);
                break;
            case VirtualKey.X when ctrl && selected.Count > 0:
                _ = ShellService.CopyToClipboardAsync(selected.Select(s => s.FullPath).ToList(), cut: true);
                break;
            case VirtualKey.V when ctrl:
                _ = PasteAsync();
                break;
            case VirtualKey.N when ctrl && shift:
                _ = NewFolderAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

}
