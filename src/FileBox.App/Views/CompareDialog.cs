using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.Core.Files;
using FileBox.Core.Security;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 2 つのファイル・フォルダーの比較。テキストは行ごとの違い、それ以外のファイルは中身が同じか、
/// フォルダーは片方にだけあるもの・中身が違うものを一覧にする。読むだけで、何も変えない。
/// </summary>
public static class CompareDialog
{
    const int MaxLines = 3000;
    const long MaxTextBytes = 4L << 20;

    public static async Task ShowAsync(XamlRoot root, string left, string right)
    {
        if (!Guard.Can(Permission.Read, left, "open") || !Guard.Can(Permission.Read, right, "open")) return;
        var body = new StackPanel { Spacing = 8, MinWidth = 480 };
        var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
        body.Children.Add(Header(left, right));
        body.Children.Add(ring);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "比較",
            Content = new ScrollViewer { Content = body, MaxHeight = 640 },
            CloseButtonText = "閉じる",
        };
        var show = dialog.ShowAsync();
        try
        {
            UIElement result = Directory.Exists(left) && Directory.Exists(right) ? await FoldersAsync(left, right)
                : File.Exists(left) && File.Exists(right) ? await FilesAsync(left, right)
                : Note("フォルダー同士、またはファイル同士を選んでください。");
            body.Children.Remove(ring);
            body.Children.Add(result);
        }
        catch (Exception ex)
        {
            body.Children.Remove(ring);
            body.Children.Add(Note($"比較できませんでした: {FriendlyError.Explain(ex)}"));
        }
        await show;
    }

    static UIElement Header(string left, string right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = Side("左", left, "#D93025");
        var b = Side("右", right, "#30A46C");
        Grid.SetColumn(b, 1);
        grid.Children.Add(a);
        grid.Children.Add(b);
        return grid;
    }

    static FrameworkElement Side(string label, string path, string color)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Converters.HexBrush(color), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), Tag = "data", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = Path.GetDirectoryName(path) ?? "", Tag = "data", FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) panel.Children.Add(new TextBlock { Text = $"{Format.HumanSize(info.Length)} ・ {info.LastWriteTime:yyyy/MM/dd HH:mm}", FontSize = 12, Foreground = Secondary });
        }
        catch (Exception) { }
        return panel;
    }

    static async Task<UIElement> FilesAsync(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        bool same = a.Length == b.Length && await Task.Run(() => FolderCompare.SameContent(left, right));
        var panel = new StackPanel { Spacing = 8 };
        string newer = a.LastWriteTime == b.LastWriteTime ? "更新日時は同じです" : a.LastWriteTime > b.LastWriteTime ? "左の方が新しい" : "右の方が新しい";
        panel.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false,
            Severity = same ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            Title = same ? "中身は同じです" : "中身が違います",
            Message = newer,
        });
        if (same || !IsText(left) || !IsText(right) || a.Length > MaxTextBytes || b.Length > MaxTextBytes) return panel;

        var diff = await Task.Run(() => LineDiff.Compare(TextExtractor.ReadText(left), TextExtractor.ReadText(right)));
        int added = diff.Count(d => d.Kind == DiffKind.Added), removed = diff.Count(d => d.Kind == DiffKind.Removed);
        panel.Children.Add(new TextBlock { Text = $"左だけ（削除）{removed} 行・右だけ（追加）{added} 行", FontSize = 12, Foreground = Secondary });
        var lines = new StackPanel();
        int shown = 0;
        // 同じ行が続くところは前後 3 行だけ残して畳む
        for (int i = 0; i < diff.Count && shown < MaxLines; i++)
        {
            var d = diff[i];
            if (d.Kind == DiffKind.Same && !Near(diff, i, 3))
            {
                int skip = i;
                while (skip < diff.Count && diff[skip].Kind == DiffKind.Same && !Near(diff, skip, 3)) skip++;
                lines.Children.Add(new TextBlock { Text = $"… 同じ行 {skip - i} 行 …", FontSize = 11, Foreground = Secondary, Margin = new Thickness(0, 2, 0, 2) });
                i = skip - 1;
                continue;
            }
            lines.Children.Add(Line(d));
            shown++;
        }
        if (shown >= MaxLines) lines.Children.Add(Note($"先頭 {MaxLines} 行の違いだけを表示しています。"));
        panel.Children.Add(new Border { Child = lines, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] });
        return panel;
    }

    static bool Near(IReadOnlyList<DiffLine> diff, int index, int range)
    {
        for (int k = Math.Max(0, index - range); k <= Math.Min(diff.Count - 1, index + range); k++)
            if (diff[k].Kind != DiffKind.Same) return true;
        return false;
    }

    static UIElement Line(DiffLine d)
    {
        var (mark, color) = d.Kind switch
        {
            DiffKind.Added => ("+", "#30A46C"),
            DiffKind.Removed => ("-", "#D93025"),
            _ => (" ", ""),
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (color.Length > 0) grid.Background = new SolidColorBrush(((SolidColorBrush)Converters.HexBrush(color)).Color) { Opacity = 0.15 };
        var font = new FontFamily("Cascadia Mono, Consolas, BIZ UDGothic, MS Gothic");
        grid.Children.Add(new TextBlock { Text = (d.LeftLine ?? d.RightLine)?.ToString() ?? "", FontSize = 11, Foreground = Secondary, FontFamily = font, HorizontalAlignment = HorizontalAlignment.Right });
        var m = new TextBlock { Text = mark, FontFamily = font, FontSize = 12, Foreground = color.Length > 0 ? Converters.HexBrush(color) : Secondary };
        Grid.SetColumn(m, 1);
        grid.Children.Add(m);
        var text = new TextBlock { Text = d.Text, Tag = "data", FontFamily = font, FontSize = 12, TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    static async Task<UIElement> FoldersAsync(string left, string right)
    {
        var entries = await FolderCompare.CompareAsync(left, right);
        var panel = new StackPanel { Spacing = 6 };
        int diff = entries.Count(e => e.Status == CompareStatus.Different), onlyL = entries.Count(e => e.Status == CompareStatus.OnlyLeft), onlyR = entries.Count(e => e.Status == CompareStatus.OnlyRight);
        panel.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false,
            Severity = diff + onlyL + onlyR == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            Title = diff + onlyL + onlyR == 0 ? "中身はすべて同じです" : $"違い {diff} 件・左だけ {onlyL} 件・右だけ {onlyR} 件",
            Message = $"同じもの {entries.Count(e => e.Status == CompareStatus.Same)} 件",
        });
        var onlyDiff = new CheckBox { Content = "違いだけ表示", IsChecked = true };
        var list = new StackPanel { Spacing = 2 };
        void Render()
        {
            list.Children.Clear();
            foreach (var e in entries.Where(e => onlyDiff.IsChecked != true || e.Status != CompareStatus.Same).Take(1000))
            {
                var (glyph, color, text) = e.Status switch
                {
                    CompareStatus.Different => ("", "#F76B15", "中身が違う"),
                    CompareStatus.OnlyLeft => ("", "#D93025", "左だけ"),
                    CompareStatus.OnlyRight => ("", "#30A46C", "右だけ"),
                    _ => ("", "#8B8D98", "同じ"),
                };
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = Converters.HexBrush(color) });
                var name = new TextBlock { Text = e.RelativePath + (e.IsDirectory ? "\\" : ""), Tag = "data", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var status = new TextBlock { Text = text, FontSize = 12, Foreground = Secondary };
                Grid.SetColumn(status, 2);
                row.Children.Add(status);
                list.Children.Add(row);
            }
        }
        onlyDiff.Checked += (_, _) => Render();
        onlyDiff.Unchecked += (_, _) => Render();
        Render();
        panel.Children.Add(onlyDiff);
        panel.Children.Add(list);
        return panel;
    }

    static bool IsText(string path) => FileKinds.IsText(Path.GetExtension(path)) || FileKinds.IsWeb(Path.GetExtension(path)) && !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Secondary };

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}
