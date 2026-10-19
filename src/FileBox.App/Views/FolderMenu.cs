using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>パンくずの区切りから、その中のフォルダーへ直接移るメニュー（エクスプローラーの「›」と同じ）。</summary>
public static class FolderMenu
{
    const int MaxItems = 60;

    /// <summary>folder（空文字は PC）の中のフォルダー一覧。</summary>
    public static MenuFlyout Build(string folder, Action<string> navigate)
    {
        var menu = new MenuFlyout();
        IEnumerable<(string Name, string Path)> entries;
        try
        {
            entries = folder.Length == 0
                ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => ($"{(string.IsNullOrEmpty(d.VolumeLabel) ? "ローカル ディスク" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName))
                : new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = ViewModels.FolderPaneViewModel.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System })
                    .Select(d => (d.Name, d.FullName))
                    .OrderBy(d => d.Name, ViewModels.NaturalComparer.Instance);
            var list = entries.Take(MaxItems + 1).ToList();
            foreach (var (name, path) in list.Take(MaxItems))
            {
                var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "", Foreground = Converters.ItemBrush(path, "", true, folder.Length == 0) } };
                item.Click += (_, _) => navigate(path);
                menu.Items.Add(item);
            }
            if (list.Count > MaxItems) menu.Items.Add(new MenuFlyoutItem { Text = $"ほか多数（最初の {MaxItems} 件を表示）", IsEnabled = false });
            if (list.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "フォルダーはありません", IsEnabled = false });
        }
        catch (Exception)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "このフォルダーの中を表示できません", IsEnabled = false });
        }
        return menu;
    }

    /// <summary>
    /// BreadcrumbBar の項目を右クリックしたとき、その項目の中のフォルダーを出す。paths はパンくずの並びに対応する実際の場所。
    /// </summary>
    public static void AttachTo(BreadcrumbBar bar, Func<IReadOnlyList<string>> paths, Action<string> navigate)
    {
        ToolTipService.SetToolTip(bar, "右クリックで中のフォルダーを表示");
        bar.RightTapped += (sender, e) =>
        {
            int index = IndexOf(e.OriginalSource as DependencyObject);
            var list = paths();
            if (index < 0 || index >= list.Count) return;
            Build(list[index], navigate).ShowAt(bar, e.GetPosition(bar));
            e.Handled = true;
        };
    }

    /// <summary>押された要素を含むパンくずの項目の位置（ItemsRepeater の中の順番）。</summary>
    static int IndexOf(DependencyObject? element)
    {
        while (element != null && element is not BreadcrumbBarItem) element = VisualTreeHelper.GetParent(element);
        if (element is not BreadcrumbBarItem item) return -1;
        return VisualTreeHelper.GetParent(item) is ItemsRepeater repeater ? repeater.GetElementIndex(item) : -1;
    }
}
