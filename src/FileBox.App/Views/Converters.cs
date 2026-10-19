using FileBox.App.Models;
using FileBox.App.Services;
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

    /// <summary>一覧のアイコン色: フォルダは利用者が付けた色（なければ黄）、ファイルは種類ごとの色。</summary>
    public static Brush ItemBrush(string fullPath, string extension, bool isDirectory, bool isDrive)
    {
        if (isDrive) return DriveBrush;
        if (isDirectory) return ViewPreferences.FolderColor(fullPath) is { } hex ? HexBrush(hex) : FolderBrush;
        return HexBrush(FileKinds.Color(extension));
    }

    static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>#RRGGBB の色。null・不正な値は既定の文字色。</summary>
    public static Brush HexBrush(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        if (!Brushes.TryGetValue(hex, out var brush))
            Brushes[hex] = brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return brush;
    }

    public static double Percent(double ratio) => ratio * 100;

    public static Brush UsageBrush(double ratio) =>
        new SolidColorBrush(ratio > 0.9 ? Colors.IndianRed : Windows.UI.Color.FromArgb(255, 0x26, 0xA0, 0xDA));
}
