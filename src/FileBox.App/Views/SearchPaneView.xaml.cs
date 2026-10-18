using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    }

    IReadOnlyList<FileItem> Selected => List.SelectedItems.Cast<FileItem>().ToList();

    void LoadMore_Click(object sender, RoutedEventArgs e) => _ = ViewModel.LoadMoreAsync();

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
    }

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item) return;
        if (!List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("開く", "", () => Open(item));
        Add("ファイルの場所を開く", "", () => _actions.RevealInFolderPane(ViewModel, item.FullPath));
        menu.Items.Add(LinkMenu.Build(item.FullPath));
        Add("新しいタブで場所を開く", "", () => _actions.OpenInNewTab(item.IsDirectory ? item.FullPath : item.Location));
        Add("プレビュー", "", () => _actions.TogglePreview(ViewModel));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("コピー", "", () => _ = ShellService.CopyToClipboardAsync(Selected.Select(s => s.FullPath).ToList(), cut: false));
        Add("パスをコピー", "", () => ShellService.CopyText(string.Join(Environment.NewLine, Selected.Select(s => s.FullPath))));
        Add("プロパティ", "", () => ShellService.ShowProperties(item.FullPath));
        menu.ShowAt(List, e.GetPosition(List));
        e.Handled = true;
    }
}
