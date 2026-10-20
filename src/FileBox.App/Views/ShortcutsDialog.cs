using FileBox.App.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FileBox.App.Views;

/// <summary>ショートカット一覧（F1）。「変更」を押してから新しいキーを押すと割り当てが変わる（次の起動から有効）。</summary>
public static class ShortcutsDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 480 };
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Secondary,
            Text = "「変更」を押してから、割り当てたいキーを押してください（Esc でやめる）。変更は次の起動から有効になります。" };
        panel.Children.Add(note);

        void Render()
        {
            while (panel.Children.Count > 1) panel.Children.RemoveAt(1);
            foreach (var group in KeyBindings.Current.GroupBy(b => b.Group))
            {
                panel.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
                foreach (var binding in group)
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = binding.Label, VerticalAlignment = VerticalAlignment.Center });
                    var keys = new Border
                    {
                        Child = new TextBlock { Text = binding.KeyText, FontFamily = new FontFamily("Consolas"), FontSize = 13 },
                        Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
                        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                    };
                    Grid.SetColumn(keys, 1);
                    row.Children.Add(keys);
                    var change = new Button { Content = "変更", Padding = new Thickness(10, 3, 10, 3) };
                    change.Click += (_, _) =>
                    {
                        change.Content = "キーを押してください…";
                        change.KeyDown += Capture;
                        change.Focus(FocusState.Programmatic);
                    };
                    void Capture(object sender, KeyRoutedEventArgs e)
                    {
                        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                        change.KeyDown -= Capture;
                        e.Handled = true;
                        if (e.Key == VirtualKey.Escape) { Render(); return; }
                        var mods = VirtualKeyModifiers.None;
                        if (IsDown(VirtualKey.Control)) mods |= VirtualKeyModifiers.Control;
                        if (IsDown(VirtualKey.Shift)) mods |= VirtualKeyModifiers.Shift;
                        if (IsDown(VirtualKey.Menu)) mods |= VirtualKeyModifiers.Menu;
                        if (KeyBindings.Set(binding.Id, e.Key, mods) is { } clash)
                            note.Text = $"{KeyBinding.Format(e.Key, mods)} は「{clash}」で使っています。別のキーを選んでください。";
                        Render();
                    }
                    Grid.SetColumn(change, 2);
                    row.Children.Add(change);
                    var reset = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4) };
                    ToolTipService.SetToolTip(reset, "既定に戻す");
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reset, "既定に戻す");
                    reset.Click += (_, _) => { KeyBindings.Reset(binding.Id); Render(); };
                    Grid.SetColumn(reset, 3);
                    row.Children.Add(reset);
                    panel.Children.Add(row);
                }
            }
            panel.Children.Add(new TextBlock { Text = "一覧の中のキー（変えられません）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            foreach (var (keys, label) in KeyBindings.Fixed)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                row.Children.Add(new TextBlock { Text = label });
                var k = new TextBlock { Text = keys, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Secondary };
                Grid.SetColumn(k, 1);
                row.Children.Add(k);
                panel.Children.Add(row);
            }
        }
        Render();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "ショートカット一覧",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            CloseButtonText = "閉じる",
        };
        await dialog.ShowAsync();
    }

    static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}
