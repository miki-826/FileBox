using System.ComponentModel;
using System.Globalization;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Files;
using FileBox.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace FileBox.App.Views;

public sealed partial class FolderPaneView : UserControl
{
    public FolderPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;

    public FolderPaneView(FolderPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        // ビューと ViewModel は同じ寿命。分割で付け替えると Loaded→Unloaded の順で発火するため Unloaded で購読解除しない
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.ItemsReloaded += OnItemsReloaded;
        Loaded += (_, _) =>
        {
            UpdateChrome();
            // 新しいタブではビューが表示される前に選択が決まるので、表示後に選択項目までスクロールする
            if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem, ScrollIntoViewAlignment.Leading);
        };
        UpdateChrome();
        ApplyItems();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderPaneViewModel.CurrentPath) or nameof(FolderPaneViewModel.Sort) or nameof(FolderPaneViewModel.SortDescending))
            UpdateChrome();
        if (e.PropertyName == nameof(FolderPaneViewModel.Items)) ApplyItems();
    }

    /// <summary>
    /// 一覧に項目を渡す。「日付でまとめる」場所（ダウンロードは既定）は、今日・昨日・今週・先週・今月…の見出しを付ける。
    /// まとまりの中の順番は今の並べ替えのまま。
    /// </summary>
    void ApplyItems()
    {
        var selectedPaths = List.SelectedItems.OfType<FileItem>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool grouped = !ViewModel.IsPc && ViewPreferences.GroupByDate(ViewModel.CurrentPath);
        GroupButton.IsChecked = grouped;
        GroupButton.IsEnabled = !ViewModel.IsPc;
        if (!grouped)
        {
            List.ItemsSource = ViewModel.Items;
            RestoreSelection();
            return;
        }
        var now = DateTime.Now;
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var groups = ViewModel.Items
            .GroupBy(i => DateGroup.Classify(i.Modified, now, firstDay))
            .OrderBy(g => g.Key)
            .Select(g => new ItemGroup($"{DateGroup.Label(g.Key)}（{g.Count()}）", g))
            .ToList();
        List.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        RestoreSelection();
        void RestoreSelection()
        {
            foreach (var item in ViewModel.Items.Where(i => selectedPaths.Contains(i.FullPath)))
                if (!List.SelectedItems.Contains(item)) List.SelectedItems.Add(item);
        }
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPc) return;
        ViewPreferences.SetGroupByDate(ViewModel.CurrentPath, GroupButton.IsChecked == true);
        ApplyItems();
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
        UpdateCommandButtons();
        string? downloads = ShellService.KnownFolder(ShellService.QuickAccess[2].Id);
        OrganizeButton.Visibility = downloads != null && string.Equals(downloads.TrimEnd('\\'), ViewModel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateCommandButtons()
    {
        var selected = Selected;
        bool editable = selected.Count > 0 && selected.All(i => !i.IsDrive);
        CutButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = editable;
        RenameButton.IsEnabled = editable && selected.Count == 1;
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
    async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Count: 1 } items && !items[0].IsDrive) await RenameAsync(items[0]);
    }
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
        List.SelectedItem = item;
        List.ScrollIntoView(item);
    }

    // ---------------------------------------------------------------- 列幅（区画が狭いときは日時・種類を隠す）

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
        for (int i = 0; i < ViewModel.Items.Count; i++)
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid g }) ApplyColumns(g);
    }

    void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is Grid g) ApplyColumns(g);
    }

    void ApplyColumns(Grid grid)
    {
        for (int c = 0; c < 3; c++)
        {
            grid.ColumnDefinitions[c + 2].Width = new GridLength(_columns[c]);
            foreach (var child in grid.Children)
                if (child is FrameworkElement fe && Grid.GetColumn(fe) == c + 2)
                    fe.Visibility = _columns[c] > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void FocusList()
    {
        List.Focus(FocusState.Programmatic);
    }

    IReadOnlyList<FileItem> Selected => List.SelectedItems.Cast<FileItem>().ToList();

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

    async Task NewFolderAsync()
    {
        if (ViewModel.IsPc || !Guard.Can(Permission.Create, ViewModel.CurrentPath, "save")) return;
        try
        {
            string path = ShellService.CreateFolder(ViewModel.CurrentPath);
            Guard.Created("save", path);
            ViewModel.PendingSelection = path;
            await ViewModel.RefreshAsync();
            string? name = await Dialogs.PromptAsync(XamlRoot, "新しいフォルダーの名前", Path.GetFileName(path), "作成");
            if (name != null && name != Path.GetFileName(path))
            {
                ShellService.Rename(path, name);
                ViewModel.PendingSelection = Path.Combine(ViewModel.CurrentPath, name);
                await ViewModel.RefreshAsync();
            }
        }
        catch (Exception ex) { App.ShowError("フォルダーを作成できませんでした", ex); }
    }

    // ---------------------------------------------------------------- 一覧操作

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
        if (item != null && !List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        if (item == null) List.SelectedItems.Clear();
        _menuPoint = ShellContextMenu.ToScreen(List, e.GetPosition(List), Hwnd);
        // Shift+右クリックはエクスプローラーと同じく Windows のメニュー（拡張項目付き）を直接出す
        if (IsDown(VirtualKey.Shift) && !ViewModel.IsPc) ShowWindowsMenu(item);
        else BuildContextMenu(item).ShowAt(List, e.GetPosition(List));
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
        if (item != null)
        {
            Add("開く", "", () => OpenItem(item), "Enter");
            if (item.IsDirectory)
            {
                Add("新しいタブで開く", "", () => _actions.OpenInNewTab(item.FullPath));
                Add("隣の区画で開く", "", () => _actions.OpenInAdjacentPane(ViewModel, item.FullPath));
                Add("お気に入りに追加", "", () => _actions.AddFavorite(item.FullPath));
            }
            Add("プレビュー・詳細", "", () => _actions.TogglePreview(ViewModel), "Space");
            if (item.IsDirectory && !item.IsDrive) menu.Items.Add(FolderColorMenu(selected.Where(s => s.IsDirectory).Select(s => s.FullPath).ToList()));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.IsDrive)
            {
                Add("切り取り", "", () => _ = ShellService.CopyToClipboardAsync(selected.Select(s => s.FullPath).ToList(), cut: true), "Ctrl+X");
                Add("コピー", "", () => _ = ShellService.CopyToClipboardAsync(selected.Select(s => s.FullPath).ToList(), cut: false), "Ctrl+C");
            }
            Add("パスをコピー", "", () => ShellService.CopyText(string.Join(Environment.NewLine, selected.Select(s => s.FullPath))), "Ctrl+Shift+C");
            if (!item.IsDrive)
            {
                Add("最適な場所へ移動…", "", () => _ = SmartMoveAsync(selected));
                Add("名前の変更", "", () => _ = RenameAsync(item), "F2");
                Add("削除（ごみ箱へ）", "", () => DeleteSelected(), "Delete");
                // 圧縮ファイルだけを選んでいるときは出さない（ZIP をさらに ZIP にしても意味がない）
                if (selected.Any(s => s.IsDirectory || !FileKinds.IsArchive(s.Extension)))
                    Add("ZIP に圧縮", "\uF012", () => _ = CompressAsync(selected));
                if (!item.IsDirectory && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    Add("すべて展開", "\uE8B7", () => _ = ExtractAsync(item));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.IsDrive) Add("その他のオプション（Windows のメニュー）", "\uE712", () => ShowWindowsMenu(item), "Shift+右クリック");
            Add("エクスプローラーで表示", "", () => ShellService.ShowInExplorer(item.FullPath));
            menu.Items.Add(LinkMenu.Build(item.FullPath));
            Add("プロパティ", "", () => ShellService.ShowProperties(item.FullPath), "Alt+Enter");
        }
        else if (!ViewModel.IsPc)
        {
            Add("貼り付け", "", () => _ = PasteAsync(), "Ctrl+V");
            Add("新しいフォルダー", "", () => _ = NewFolderAsync(), "Ctrl+Shift+N");
            Add("最新の情報に更新", "", () => _ = ViewModel.RefreshAsync(), "F5");
            Add("その他のオプション（Windows のメニュー）", "\uE712", () => ShowWindowsMenu(null), "Shift+右クリック");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("このフォルダをお気に入りに追加", "", () => _actions.AddFavorite(ViewModel.CurrentPath));
            Add("パスをコピー", "", () => ShellService.CopyText(ViewModel.CurrentPath));
            Add("エクスプローラーで開く", "", () => ShellService.OpenInExplorer(ViewModel.CurrentPath));
            menu.Items.Add(LinkMenu.Build(ViewModel.CurrentPath));
            Add("プロパティ", "", () => ShellService.ShowProperties(ViewModel.CurrentPath));
        }
        return menu;
    }

    /// <summary>フォルダの色（Teams のように自分で選べる。FileBox の中だけの表示で、Windows 側は変えない）。</summary>
    static MenuFlyoutSubItem FolderColorMenu(IReadOnlyList<string> folders)
    {
        var sub = new MenuFlyoutSubItem { Text = "フォルダの色", Icon = new FontIcon { Glyph = "\uE790" } };
        string? current = folders.Count > 0 ? ViewPreferences.FolderColor(folders[0]) : null;
        void Add(string text, string? hex)
        {
            var mi = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase),
                Icon = new FontIcon { Glyph = "\uE8B7", Foreground = hex == null ? Converters.IconBrush(true, false) : Converters.HexBrush(hex) },
            };
            mi.Click += (_, _) => { foreach (var f in folders) ViewPreferences.SetFolderColor(f, hex); };
            sub.Items.Add(mi);
        }
        Add("既定（黄）", null);
        sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var (name, hex) in ViewPreferences.Palette) Add(name, hex);
        return sub;
    }

    async Task CompressAsync(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return;
        string name = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : Path.GetFileName(ViewModel.CurrentPath.TrimEnd('\\'));
        string zip = FileMover.UniquePath(Path.Combine(ViewModel.CurrentPath, name + ".zip"));
        var sources = items.Select(i => i.FullPath).ToList();
        if (!await Guard.TransferAsync(sources, ViewModel.CurrentPath, move: false, "save")) return;
        try
        {
            await ArchiveService.CompressAsync(sources, zip);
            Guard.Created("save", zip);
            ViewModel.PendingSelection = zip;
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("圧縮できませんでした", ex); }
    }

    async Task ExtractAsync(FileItem zip)
    {
        string dest = FileMover.UniquePath(Path.Combine(ViewModel.CurrentPath, Path.GetFileNameWithoutExtension(zip.Name)));
        if (!await Guard.TransferAsync(new[] { zip.FullPath }, ViewModel.CurrentPath, move: false, "save")) return;
        try
        {
            await ArchiveService.ExtractAsync(zip.FullPath, dest);
            Guard.Created("save", dest);
            ViewModel.PendingSelection = dest;
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("展開できませんでした", ex); }
    }

    /// <summary>「もしかしてこのフォルダ？」で選んだ場所へ移動（02_機能設計 §1.1 入口①）。</summary>
    async Task SmartMoveAsync(IReadOnlyList<FileItem> items)
    {
        var moved = await SuggestMoveDialog.RunAsync(XamlRoot, items.Select(i => i.FullPath).ToList());
        if (moved != null) await ViewModel.RefreshAsync();
    }

    async Task RenameAsync(FileItem item)
    {
        if (!Guard.Can(Permission.Modify, item.FullPath, "rename")) return;
        string? name = await Dialogs.PromptAsync(XamlRoot, "名前の変更", item.Name, "変更", selectStem: !item.IsDirectory);
        if (name == null || name == item.Name) return;
        try
        {
            ShellService.Rename(item.FullPath, name);
            Guard.Done("rename", item.FullPath, Path.Combine(ViewModel.CurrentPath, name));
            ViewModel.PendingSelection = Path.Combine(ViewModel.CurrentPath, name);
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex) { App.ShowError("名前を変更できませんでした", ex); }
    }

    void DeleteSelected()
    {
        var paths = Selected.Where(s => !s.IsDrive).Select(s => s.FullPath).ToList();
        if (paths.Count == 0 || paths.Any(p => !Guard.Can(Permission.Delete, p, "delete"))) return;
        ShellService.Recycle(paths);
        foreach (var p in paths.Where(p => !File.Exists(p) && !Directory.Exists(p))) Guard.Done("delete", p, detail: "ごみ箱へ");
        _ = ViewModel.RefreshAsync();
    }

    // ---------------------------------------------------------------- ドラッグ＆ドロップ（他アプリ・エクスプローラーとも相互）

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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
            e.DragUIOverride.Caption = $"{(move ? "移動" : "コピー")}: {targetName}";
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
            if (!await Guard.TransferAsync(paths, target, move, move ? "move" : "copy")) return;
            await Task.Run(() => ShellService.Transfer(paths, target, move));
            Guard.Transferred(paths, target, move, move ? "move" : "copy");
            if (move) foreach (var p in paths) SuggestionService.Instance.Record(Path.GetFileName(p), target, null);
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
        string action = move ? "move" : "copy";
        if (!await Guard.TransferAsync(paths, ViewModel.CurrentPath, move, action)) return;
        await Task.Run(() => ShellService.Transfer(paths, ViewModel.CurrentPath, move));
        Guard.Transferred(paths, ViewModel.CurrentPath, move, action);
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
            case VirtualKey.Space:
                _actions.TogglePreview(ViewModel);
                break;
            case VirtualKey.F2 when first != null && !first.IsDrive:
                _ = RenameAsync(first);
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
