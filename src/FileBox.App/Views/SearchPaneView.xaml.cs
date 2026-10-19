using System.ComponentModel;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Search;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileBox.App.Views;

public sealed partial class SearchPaneView : UserControl
{
    public SearchPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;

    public SearchPaneView(SearchPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        RenderFacets();
        UpdateHeaders();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchPaneViewModel.Facets)) RenderFacets();
        if (e.PropertyName is nameof(SearchPaneViewModel.Sort) or nameof(SearchPaneViewModel.SortDescending)) UpdateHeaders();
    }

    IReadOnlyList<FileItem> Selected => List.SelectedItems.Cast<FileItem>().ToList();

    void LoadMore_Click(object sender, RoutedEventArgs e) => _ = ViewModel.LoadMoreAsync();

    // ---------------------------------------------------------------- 絞り込み・並べ替え

    void RenderFacets()
    {
        FacetPanel.Children.Clear();
        string? lastGroup = null;
        foreach (var facet in ViewModel.Facets)
        {
            if (facet.Group != lastGroup)
            {
                FacetPanel.Children.Add(new TextBlock { Text = facet.Group, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(lastGroup == null ? 0 : 8, 0, 2, 0), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                lastGroup = facet.Group;
            }
            var chip = new Button { Content = $"{facet.Label} {facet.Count:N0}", FontSize = 12, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(14), MinHeight = 28 };
            ToolTipService.SetToolTip(chip, "この条件で絞り込む");
            chip.Click += (_, _) => ApplyQuery(SearchFacets.Apply(ViewModel.Query, facet));
            FacetPanel.Children.Add(chip);
        }
        FacetPanel.Visibility = ViewModel.Facets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>入力中の検索から出た区画なら検索欄を、Enter で開いたタブならこの区画だけを書き換える。</summary>
    void ApplyQuery(string query)
    {
        if (ViewModel.AutoCreated) App.Window?.SetSearchText(query);
        else _ = ViewModel.RunAsync(query);
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (Enum.TryParse<SearchSort>((string)((FrameworkElement)sender).Tag, out var sort)) ViewModel.SetSort(sort);
    }

    void UpdateHeaders()
    {
        string arrow = ViewModel.SortDescending ? " ▼" : " ▲";
        NameHeader.Content = "名前・場所" + (ViewModel.Sort == SearchSort.Name ? arrow : "");
        SizeHeader.Content = "サイズ" + (ViewModel.Sort == SearchSort.Size ? arrow : "");
        ModifiedHeader.Content = "更新日時" + (ViewModel.Sort == SearchSort.Modified ? arrow : "");
    }

    // ---------------------------------------------------------------- 一致部分の強調・場所を開くボタン

    // ---------------------------------------------------------------- 狭い区画ではサイズ・日時の列を隠して名前を広く見せる

    bool _narrow;

    void List_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 520;
        if (narrow == _narrow) return;
        _narrow = narrow;
        ApplyColumns(HeaderGrid);
        for (int i = 0; i < List.Items.Count; i++)
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid g }) ApplyColumns(g);
    }

    void ApplyColumns(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 5) return;
        grid.ColumnDefinitions[2].Width = new GridLength(_narrow ? 0 : 90);
        grid.ColumnDefinitions[3].Width = new GridLength(_narrow ? 0 : 130);
        foreach (var child in grid.Children.OfType<FrameworkElement>())
            if (Grid.GetColumn(child) is 2 or 3) child.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItem item || args.ItemContainer.ContentTemplateRoot is not Grid root) return;
        item.RefreshMarks();
        ApplyColumns(root);
        if (root.FindName("NameText") is TextBlock name) Highlight(name, item.Name);
        if (args.ItemContainer.Tag is not "hooked")
        {
            args.ItemContainer.Tag = "hooked";
            args.ItemContainer.PointerEntered += (s, _) => SetRevealVisible((ListViewItem)s, true);
            args.ItemContainer.PointerExited += (s, _) => SetRevealVisible((ListViewItem)s, false);
            args.ItemContainer.GotFocus += (s, _) => SetRevealVisible((ListViewItem)s, true);
            args.ItemContainer.LostFocus += (s, _) => SetRevealVisible((ListViewItem)s, false);
        }
    }

    static void SetRevealVisible(ListViewItem container, bool visible)
    {
        if (container.ContentTemplateRoot is Grid g && g.Children.OfType<Button>().FirstOrDefault() is { } b) b.Opacity = visible ? 1 : 0;
    }

    void Highlight(TextBlock block, string name)
    {
        var ranges = MatchHighlighter.Ranges(ViewModel.Parsed, name);
        block.Inlines.Clear();
        int pos = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > pos) block.Inlines.Add(new Run { Text = name[pos..start] });
            block.Inlines.Add(new Run { Text = name.Substring(start, length), FontWeight = FontWeights.Bold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            pos = start + length;
        }
        if (pos < name.Length) block.Inlines.Add(new Run { Text = name[pos..] });
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileItem item) _actions.RevealInFolderPane(ViewModel, item.FullPath);
    }

    // ---------------------------------------------------------------- 一覧操作

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _actions.ActivatePane(ViewModel);
        var s = Selected;
        ViewModel.OnSelectionChanged(s.Count == 1 ? s[0] : null);
    }

    void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) Open(item);
    }

    void Open(FileItem item)
    {
        if (item.IsDirectory) _actions.RevealInFolderPane(ViewModel, item.FullPath);
        else ShellService.Open(item.FullPath);
    }

    void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var first = Selected.FirstOrDefault();
        if (first == null) return;
        if (e.Key == VirtualKey.Enter) { Open(first); e.Handled = true; }
        else if (e.Key == VirtualKey.Space) { _actions.TogglePreview(ViewModel); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete)
        {
            if (FileOperations.Instance.Recycle(Selected.Select(s => s.FullPath).ToList()) > 0) _ = ViewModel.RunAsync(ViewModel.Query);
            e.Handled = true;
        }
    }

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item) return;
        if (!List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        BuildMenu(item).ShowAt(List, e.GetPosition(List));
        e.Handled = true;
    }

    /// <summary>キーボード（Shift+F10・アプリケーション キー）でも同じメニューを出す。マウスの右クリックは List_RightTapped が出す。</summary>
    void List_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(List, out _) || List.SelectedItem is not FileItem item) return;
        var target = List.ContainerFromItem(item) as FrameworkElement ?? List;
        BuildMenu(item).ShowAt(target, new Windows.Foundation.Point(32, Math.Min(24, target.ActualHeight / 2)));
        e.Handled = true;
    }

    MenuFlyout BuildMenu(FileItem item)
    {
        var selected = Selected;
        var paths = selected.Select(s => s.FullPath).ToList();
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("開く", "", () => Open(item));
        Add("ファイルの場所を開く", "", () => _actions.RevealInFolderPane(ViewModel, item.FullPath));
        menu.Items.Add(LinkMenu.Build(item.FullPath));
        Add("新しいタブで場所を開く", "", () => _actions.OpenInNewTab(item.IsDirectory ? item.FullPath : item.Location));
        Add("プレビュー", "", () => _actions.TogglePreview(ViewModel));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("コピー", "", () => _ = ShellService.CopyToClipboardAsync(paths, cut: false));
        Add("パスをコピー", "", () => ShellService.CopyText(string.Join(Environment.NewLine, paths)));
        Add("パスを引用符付きでコピー", "", () => ShellService.CopyText(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\""))));
        bool starred = paths.All(UserMarks.IsStarred);
        Add(starred ? "星を外す" : "星を付ける", starred ? "" : "", () => UserMarks.SetStar(paths, !starred));
        if (selected.Count == 2) Add("2 つを比較", "", () => _ = CompareDialog.ShowAsync(XamlRoot, selected[0].FullPath, selected[1].FullPath));
        Add("削除（ごみ箱へ）", "", () => { if (FileOperations.Instance.Recycle(paths) > 0) _ = ViewModel.RunAsync(ViewModel.Query); });
        Add("プロパティ", "", () => ShellService.ShowProperties(item.FullPath));
        return menu;
    }
}
