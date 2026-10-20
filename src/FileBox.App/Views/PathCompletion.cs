using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileBox.App.Views;

/// <summary>
/// アドレス欄の入力補完。入力中のパスの最後の「\」より前のフォルダーから、続きが一致するフォルダーを候補に出す。
/// ↑↓で選び、Tab か Enter で入れる（Enter で決めた候補はそのまま開かず、続けて入力できる）。Esc で候補を閉じる。
/// </summary>
public sealed class PathCompletion
{
    const int MaxItems = 12;
    readonly TextBox _box;
    readonly Popup _popup = new() { IsLightDismissEnabled = false, ShouldConstrainToRootBounds = true };
    // 候補を押してもアドレス欄からフォーカスを移さない（移すと入力モードが終わってしまう）
    readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MaxHeight = 320, AllowFocusOnInteraction = false };
    CancellationTokenSource? _cts;
    bool _applying;

    public static void Attach(TextBox box) => _ = new PathCompletion(box);

    PathCompletion(TextBox box)
    {
        _box = box;
        _popup.Child = new Border
        {
            Child = _list,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            Padding = new Thickness(2),
        };
        _list.ItemClick += (_, e) => Accept((string)e.ClickedItem);
        box.TextChanged += (_, _) => { if (!_applying) _ = UpdateAsync(); };
        box.LostFocus += (_, _) => Close();
        box.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKey), true);
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_popup.IsOpen || _list.Items.Count == 0) return;
        switch (e.Key)
        {
            case VirtualKey.Down:
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                _list.SelectedIndex = Math.Max(-1, _list.SelectedIndex - 1);
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                Accept((string)(_list.SelectedItem ?? _list.Items[0]));
                e.Handled = true;
                break;
            case VirtualKey.Enter when _list.SelectedItem is string chosen:
                Accept(chosen);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    void Accept(string path)
    {
        _applying = true;
        _box.Text = path.TrimEnd('\\') + "\\";
        _box.SelectionStart = _box.Text.Length;
        _applying = false;
        _ = UpdateAsync();
        _box.Focus(FocusState.Programmatic);
    }

    async Task UpdateAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        string text = Environment.ExpandEnvironmentVariables(_box.Text.Trim().Trim('"'));
        int slash = text.LastIndexOf('\\');
        if (slash < 0 || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) { Close(); return; }
        string folder = text[..(slash + 1)];
        string prefix = text[(slash + 1)..];
        List<string> items;
        try
        {
            items = await Task.Run(() =>
            {
                if (folder.Length < 3 || !Directory.Exists(folder)) return new List<string>();
                return new DirectoryInfo(folder)
                    .EnumerateDirectories(prefix + "*", new EnumerationOptions { IgnoreInaccessible = true })
                    .Select(d => d.FullName)
                    .OrderBy(p => p, ViewModels.NaturalComparer.Instance)
                    .Take(MaxItems)
                    .ToList();
            }, cts.Token);
        }
        catch (Exception) { items = new(); }
        if (cts.IsCancellationRequested) return;
        // 入力済みのものと同じ 1 件だけなら出さない
        if (items.Count == 0 || (items.Count == 1 && string.Equals(items[0].TrimEnd('\\'), text.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            Close();
            return;
        }
        _list.ItemsSource = items;
        _list.SelectedIndex = -1;
        Open();
    }

    void Open()
    {
        if (_box.XamlRoot == null) return;
        _popup.XamlRoot = _box.XamlRoot;
        var point = _box.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, _box.ActualHeight + 2));
        _popup.HorizontalOffset = point.X;
        _popup.VerticalOffset = point.Y;
        _list.Width = Math.Max(240, _box.ActualWidth);
        _popup.IsOpen = true;
    }

    void Close()
    {
        _cts?.Cancel();
        _popup.IsOpen = false;
    }
}
