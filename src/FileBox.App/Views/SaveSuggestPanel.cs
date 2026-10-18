using System.Runtime.InteropServices;
using FileBox.Core.Suggest;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 保存ダイアログの横に出す小さな提案パネル（02_機能設計 §1.2）。常に手前・フォーカスを奪わない。
/// </summary>
public sealed class SaveSuggestPanel
{
    const int WidthDip = 340, HeightDip = 320;
    readonly Window _window;
    readonly StackPanel _list = new() { Spacing = 4 };
    readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Action<string, string, bool, SuggestionResult?> _choose;
    bool _visible;
    IntPtr _dialog;

    public SaveSuggestPanel(Action<string, string, bool, SuggestionResult?> choose, Action close)
    {
        _choose = choose;
        _window = new Window { Title = "FileBox の提案", SystemBackdrop = new MicaBackdrop() };
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        var closeButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(closeButton, "このダイアログでは表示しない");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeButton, "提案を閉じる");
        closeButton.Click += (_, _) => close();
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);

        var root = new StackPanel { Padding = new Thickness(12, 10, 12, 12), Spacing = 8 };
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = HeightDip - 70 });
        _window.Content = root;

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
    }

    public void Show(IntPtr dialog, string fileName, SuggestionResult result)
    {
        _dialog = dialog;
        _title.Text = "もしかしてこのフォルダ？";
        _list.Children.Clear();
        foreach (var c in result.Candidates)
            _list.Children.Add(Item(c.Path, c.Reason, SuggestMoveDialog.ConfidenceBadge(c.Confidence), () => _choose(fileName, c.Path, false, result)));
        if (result.NewFolder is { } nf)
        {
            var badge = new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            _list.Children.Add(Item(nf.Path, (result.Candidates.Count == 0 ? "該当フォルダはなさそうです。" : "") + nf.Reason, badge, () => _choose(fileName, nf.Path, true, result)));
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock { Text = "候補が見つかりません", Foreground = Brush("TextFillColorSecondaryBrush") });
        FollowDialog(dialog);
        if (!_visible)
        {
            _window.AppWindow.Show(activateWindow: false);   // 入力中のダイアログからフォーカスを奪わない
            _visible = true;
        }
    }

    UIElement Item(string path, string reason, UIElement badge, Action click)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(badge);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = path, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = reason, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, "クリックするとファイル名欄にこの場所を入れます");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{path} に保存");   // 読み上げ・自動操作用
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>ダイアログの右に並べる（画面からはみ出すなら左）。</summary>
    public void FollowDialog(IntPtr dialog)
    {
        if (!GetWindowRect(dialog, out var r)) return;
        double scale = GetDpiForWindow(dialog) / 96.0;
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale);
        var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(r.Left, r.Top), DisplayAreaFallback.Nearest).WorkArea;
        int x = r.Right + 4;
        if (x + w > area.X + area.Width) x = Math.Max(area.X, r.Left - w - 4);
        int y = Math.Clamp(r.Top, area.Y, Math.Max(area.Y, area.Y + area.Height - h));
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    public void HidePanel()
    {
        if (!_visible) return;
        _window.AppWindow.Hide();
        _visible = false;
    }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
