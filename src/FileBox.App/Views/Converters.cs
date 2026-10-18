using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>x:Bind 関数バインディング用のヘルパー。</summary>
public static class Converters
{
    static readonly SolidColorBrush FolderBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xA3, 0x3D));
    static readonly SolidColorBrush DriveBrush = new(Windows.UI.Color.FromArgb(255, 0x5B, 0x8D, 0xEF));

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIf(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush IconBrush(bool isDirectory, bool isDrive) =>
        isDrive ? DriveBrush : isDirectory ? FolderBrush : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static double Percent(double ratio) => ratio * 100;

    public static Brush UsageBrush(double ratio) =>
        new SolidColorBrush(ratio > 0.9 ? Colors.IndianRed : Windows.UI.Color.FromArgb(255, 0x26, 0xA0, 0xDA));
}
