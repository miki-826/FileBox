using FileBox.App.Services;
using FileBox.Core.Files;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 名前の一括変更（置換・正規表現・連番・日付の付加・大文字小文字）。変更前と変更後を並べて確かめてから実行する。
/// 問題のある項目（同じ名前になる・使えない文字）は赤で示し、実行しない。Ctrl+Z でまとめて戻せる。
/// </summary>
public static class BatchRenameDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        var find = new TextBox { Header = "検索する文字", MinWidth = 220 };
        var replace = new TextBox { Header = "置き換える文字", MinWidth = 220 };
        var regex = new CheckBox { Content = "正規表現（$1 などで一致した部分を使える）" };
        var ignoreCase = new CheckBox { Content = "大文字と小文字を区別しない", IsChecked = true };
        var nameCase = Combo("大文字・小文字", "変えない", "すべて大文字", "すべて小文字", "単語の先頭だけ大文字");
        var numbering = Combo("連番", "付けない", "先頭に付ける", "末尾に付ける", "名前を連番にする");
        var numberBase = new TextBox { Header = "連番の前の名前", PlaceholderText = "例: 旅行", MinWidth = 160, Visibility = Visibility.Collapsed };
        var start = new NumberBox { Header = "開始", Value = 1, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 100 };
        var digits = new NumberBox { Header = "桁数", Value = 2, Minimum = 1, Maximum = 9, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 100 };
        var separator = new TextBox { Header = "区切り", Text = "_", MinWidth = 70 };
        var date = Combo("更新日を先頭に付ける", "付けない", "20261020", "2026-10-20", "261020");
        var withExt = new CheckBox { Content = "拡張子も対象にする" };

        var preview = new StackPanel { Spacing = 2 };
        var summary = new TextBlock { FontSize = 12, Foreground = Secondary };
        IReadOnlyList<RenamePlan> plans = Array.Empty<RenamePlan>();

        RenameRule Rule() => new()
        {
            Find = find.Text,
            Replace = replace.Text,
            UseRegex = regex.IsChecked == true,
            IgnoreCase = ignoreCase.IsChecked == true,
            Case = (NameCase)nameCase.SelectedIndex,
            Numbering = (NumberPosition)numbering.SelectedIndex,
            NumberBase = numberBase.Text,
            NumberStart = double.IsNaN(start.Value) ? 1 : (int)start.Value,
            NumberDigits = double.IsNaN(digits.Value) ? 2 : (int)digits.Value,
            NumberSeparator = separator.Text,
            DateFormat = date.SelectedIndex switch { 1 => "yyyyMMdd", 2 => "yyyy-MM-dd", 3 => "yyMMdd", _ => "" },
            IncludeExtension = withExt.IsChecked == true,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"{paths.Count} 件の名前を一括で変更",
            PrimaryButtonText = "変更",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Update()
        {
            numberBase.Visibility = numbering.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            plans = BatchRename.Preview(paths, Rule());
            preview.Children.Clear();
            foreach (var p in plans.Take(200))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = p.OldName, Tag = "data", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Secondary, FontSize = 13 });
                var arrow = new FontIcon { Glyph = "", FontSize = 11, Foreground = Secondary };
                Grid.SetColumn(arrow, 1);
                row.Children.Add(arrow);
                var after = new TextBlock
                {
                    Text = p.Error ?? p.NewName,
                    Tag = p.Error == null ? "data" : null,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 13,
                    FontWeight = p.Changes ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = p.Error != null ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed) : p.Changes ? Primary : Secondary,
                };
                ToolTipService.SetToolTip(after, p.Error ?? p.NewName);
                Grid.SetColumn(after, 2);
                row.Children.Add(after);
                preview.Children.Add(row);
            }
            int changes = plans.Count(p => p.Changes), errors = plans.Count(p => p.Error != null);
            summary.Text = $"変更 {changes} 件" + (errors > 0 ? $"・問題 {errors} 件（実行しません）" : "") + (plans.Count > 200 ? "・先頭 200 件を表示" : "");
            dialog.IsPrimaryButtonEnabled = changes > 0;
        }

        foreach (var box in new[] { find, replace, numberBase, separator }) box.TextChanged += (_, _) => Update();
        foreach (var check in new[] { regex, ignoreCase, withExt }) { check.Checked += (_, _) => Update(); check.Unchecked += (_, _) => Update(); }
        foreach (var combo in new[] { nameCase, numbering, date }) combo.SelectionChanged += (_, _) => Update();
        foreach (var number in new[] { start, digits }) number.ValueChanged += (_, _) => Update();

        var form = new StackPanel { Spacing = 10, MinWidth = 480 };
        form.Children.Add(Row(find, replace));
        form.Children.Add(Row(regex, ignoreCase));
        form.Children.Add(Row(nameCase, date));
        form.Children.Add(Row(numbering, numberBase));
        form.Children.Add(Row(start, digits, separator));
        form.Children.Add(withExt);
        form.Children.Add(new TextBlock { Text = "変更後の名前", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        form.Children.Add(summary);
        form.Children.Add(new ScrollViewer { Content = preview, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = new ScrollViewer { Content = form, MaxHeight = 640 };
        Update();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        var (done, errorsList) = await FileOperations.Instance.RenameManyAsync(plans.Where(p => p.Error == null).ToList());
        if (errorsList.Count > 0) App.Window?.ShowInfo($"{done} 件を変更しました。{errorsList.Count} 件は変更できませんでした: {errorsList[0]}", InfoBarSeverity.Warning);
        else if (done > 0) App.Window?.ShowInfo($"{done} 件の名前を変更しました", InfoBarSeverity.Success, "元に戻す (Ctrl+Z)", () => _ = FileOperations.Instance.UndoAsync());
        return done > 0;
    }

    static ComboBox Combo(string header, params string[] items)
    {
        var combo = new ComboBox { Header = header, MinWidth = 180 };
        foreach (var i in items) combo.Items.Add(i);
        combo.SelectedIndex = 0;
        return combo;
    }

    static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    static Brush Primary => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
}
