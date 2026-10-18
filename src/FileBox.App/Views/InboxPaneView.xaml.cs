using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Suggest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

public sealed partial class InboxPaneView : UserControl
{
    public InboxPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;

    public InboxPaneView(InboxPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
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
        var (n, ops) = await InboxService.Instance.MoveAllConfidentAsync();
        ViewModel.Refresh();
        App.Window?.ShowInfo(n > 0 ? $"{n} 件を推奨先へ移動しました" : "確信度「高」の項目はありません", n > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            n > 0 ? "元に戻す" : null,
            async () => { foreach (var op in ops) await FileMover.UndoAsync(op); });
    }

    async void Settings_Click(object sender, RoutedEventArgs e) => await InboxSettingsDialog.ShowAsync(XamlRoot);

    static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
}
