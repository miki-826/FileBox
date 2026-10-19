using FileBox.App.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FileBox.App.Views;

/// <summary>一覧共通のベクターアイコン。フォルダの形と、文書の種類を示す印で見分ける。</summary>
public sealed class FileItemIcon : Grid
{
    public static readonly DependencyProperty ItemPathProperty = Register(nameof(ItemPath), "");
    public static readonly DependencyProperty ExtensionProperty = Register(nameof(Extension), "");
    public static readonly DependencyProperty IsDirectoryProperty = Register(nameof(IsDirectory), false);
    public static readonly DependencyProperty IsDriveProperty = Register(nameof(IsDrive), false);

    static DependencyProperty Register(string name, object value) => DependencyProperty.Register(
        name, value.GetType(), typeof(FileItemIcon), new PropertyMetadata(value, (d, _) => ((FileItemIcon)d).Draw()));

    public string ItemPath { get => (string)GetValue(ItemPathProperty); set => SetValue(ItemPathProperty, value); }
    public string Extension { get => (string)GetValue(ExtensionProperty); set => SetValue(ExtensionProperty, value); }
    public bool IsDirectory { get => (bool)GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }
    public bool IsDrive { get => (bool)GetValue(IsDriveProperty); set => SetValue(IsDriveProperty, value); }

    public FileItemIcon()
    {
        Width = 32;
        Height = 32;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        // 装飾だけの要素は、ファイル名の読み上げと重複させない。
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => Draw();
    }

    void Draw()
    {
        if (!IsLoaded) return;
        Children.Clear();
        var color = Converters.ItemBrush(ItemPath, Extension, IsDirectory, IsDrive);
        if (IsDrive)
        {
            Children.Add(new Border { Margin = new Thickness(2, 8, 2, 5), CornerRadius = new CornerRadius(4),
                Background = color, Opacity = 0.85 });
            Children.Add(new Border { Margin = new Thickness(5, 11, 5, 14), CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Colors.White) });
            Children.Add(new Ellipse { Width = 3, Height = 3, Margin = new Thickness(0, 0, 6, 8),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(Colors.White) });
        }
        else if (IsDirectory)
        {
            Children.Add(new Border { Width = 13, Height = 7, Margin = new Thickness(2, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(3, 3, 0, 0), Background = color });
            Children.Add(new Border { Margin = new Thickness(2, 9, 2, 4), CornerRadius = new CornerRadius(3),
                Background = color, Opacity = 0.75 });
            Children.Add(new Border { Margin = new Thickness(2, 12, 2, 4), CornerRadius = new CornerRadius(3),
                Background = color, BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(0, 1, 0, 0) });
        }
        else
        {
            Children.Add(new Border { Margin = new Thickness(6, 2, 4, 2), CornerRadius = new CornerRadius(2),
                Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                BorderBrush = color, BorderThickness = new Thickness(1) });
            Children.Add(new Polygon { Points = new PointCollection { new(21, 2), new(28, 9), new(21, 9) },
                Fill = color, Opacity = 0.5 });
            var badge = new Border { Margin = new Thickness(1, 13, 3, 4), CornerRadius = new CornerRadius(3), Background = color };
            string? label = Extension.ToLowerInvariant() switch
            {
                ".pdf" => "PDF",
                ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" => "W",
                ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".tsv" or ".ods" => "X",
                ".ppt" or ".pptx" or ".pptm" or ".odp" => "P",
                _ when FileKinds.IsArchive(Extension) => "ZIP",
                _ => null,
            };
            badge.Child = label != null
                ? new TextBlock { Text = label, FontSize = label.Length > 1 ? 8 : 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new FontIcon { Glyph = FileKinds.Glyph(Extension), FontSize = 10, Foreground = new SolidColorBrush(Colors.White) };
            Children.Add(badge);
        }
    }
}
