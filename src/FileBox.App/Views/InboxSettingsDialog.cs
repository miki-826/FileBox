using FileBox.App.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>ダウンロードの整理の監視フォルダと振り分けルールの管理（02_機能設計 §1.6）。</summary>
public static class InboxSettingsDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var service = InboxService.Instance;
        var sources = new StackPanel { Spacing = 2 };
        var rules = new StackPanel { Spacing = 2 };
        bool sourcesChanged = false;

        void RenderSources()
        {
            sources.Children.Clear();
            foreach (var path in service.Sources)
            {
                var row = Row(path, () => { service.RemoveSource(path); sourcesChanged = true; RenderSources(); });
                sources.Children.Add(row);
            }
            var add = new HyperlinkButton { Content = "＋ 監視フォルダを追加" };
            add.Click += async (_, _) =>
            {
                if (await SuggestMoveDialog.PickFolderAsync() is not { } p) return;
                service.AddSource(p);
                sourcesChanged = true;
                RenderSources();
            };
            sources.Children.Add(add);
        }

        void RenderRules()
        {
            rules.Children.Clear();
            var list = service.GetRules();
            if (list.Count == 0)
                rules.Children.Add(new TextBlock
                {
                    Text = "ルールはまだありません。ダウンロードの整理の項目の「…」メニューから作れます。",
                    Foreground = Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                });
            foreach (var r in list)
            {
                string cond = string.Join(" かつ ", new[] { r.NamePattern is { } n ? $"名前「{n}」" : null, r.Ext is { } x ? $"拡張子 .{x}" : null }.Where(v => v != null));
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = cond, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = "→ " + r.TargetPath, FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
                grid.Children.Add(text);
                var auto = new ToggleSwitch { IsOn = r.AutoMove, OnContent = "自動移動", OffContent = "提案のみ", MinWidth = 0 };
                auto.Toggled += (_, _) => service.SetRuleAutoMove(r.Id, auto.IsOn);
                Grid.SetColumn(auto, 1);
                grid.Children.Add(auto);
                var del = DeleteButton(() => { service.DeleteRule(r.Id); RenderRules(); });
                Grid.SetColumn(del, 2);
                grid.Children.Add(del);
                rules.Children.Add(grid);
            }
        }

        RenderSources();
        RenderRules();
        var content = new StackPanel { Spacing = 8, MinWidth = 520 };
        content.Children.Add(Header("監視フォルダ"));
        content.Children.Add(new TextBlock { Text = "ここに新しく届いたファイルに、置き場所を提案します。", FontSize = 12, Foreground = Secondary });
        content.Children.Add(sources);
        content.Children.Add(Header("振り分けルール"));
        content.Children.Add(rules);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "ダウンロードの整理の設定",
            Content = new ScrollViewer { Content = content, MaxHeight = 560 },
            CloseButtonText = "閉じる",
        };
        await dialog.ShowAsync();
        if (!sourcesChanged) service.Restart();   // ルール変更を既存項目にも反映
    }

    static Grid Row(string text, Action remove)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var del = DeleteButton(remove);
        Grid.SetColumn(del, 1);
        grid.Children.Add(del);
        return grid;
    }

    static Button DeleteButton(Action action)
    {
        var b = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, "削除");
        b.Click += (_, _) => action();
        return b;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}
