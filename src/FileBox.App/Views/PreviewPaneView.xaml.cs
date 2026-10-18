using System.ComponentModel;
using System.Text;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;

namespace FileBox.App.Views;

/// <summary>種類に応じたプレビュー（画像/テキスト/PDF・HTML/動画・音声/情報表示）。</summary>
public sealed partial class PreviewPaneView : UserControl
{
    const int MaxTextBytes = 512 * 1024;

    public PreviewPaneViewModel ViewModel { get; }
    readonly IWorkspaceActions _actions;
    WebView2? _web;
    CancellationTokenSource? _cts;

    static PreviewPaneView() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public PreviewPaneView(PreviewPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        ViewModel.PropertyChanged += OnChanged;
        Loaded += (_, _) => { ViewModel.PropertyChanged -= OnChanged; ViewModel.PropertyChanged += OnChanged; };
        PointerPressed += (_, _) => _actions.ActivatePane(ViewModel);
        if (ViewModel.Item != null) _ = ShowAsync(ViewModel.Item);
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewPaneViewModel.Item) && ViewModel.Item != null) _ = ShowAsync(ViewModel.Item);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Item is { } item) ShellService.Open(item.FullPath);
    }

    async Task ShowAsync(FileItem item)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        HeaderIcon.Glyph = item.Glyph;
        HeaderName.Text = item.Name;
        HeaderDetail.Text = item.IsDirectory ? item.FullPath : $"{item.HumanSize} ・ {item.ModifiedText} ・ {item.Location}";
        OpenButton.IsEnabled = true;
        StopMedia();

        string ext = item.Extension;
        try
        {
            if (item.IsDirectory) SetContent(await FolderSummaryAsync(item, cts.Token));
            else if (FileKinds.IsImage(ext)) SetContent(Image(item.FullPath));
            else if (FileKinds.IsWeb(ext)) await ShowWebAsync(item.FullPath);
            else if (FileKinds.IsVideo(ext) || FileKinds.IsAudio(ext)) SetContent(Media(item.FullPath));
            else if (FileKinds.IsText(ext) || await LooksLikeTextAsync(item.FullPath)) SetContent(await TextAsync(item.FullPath, cts.Token));
            else SetContent(Info(item));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetContent(Message($"プレビューできません: {ex.Message}"));
        }
    }

    void SetContent(UIElement element)
    {
        // WebView2 は作り直すと重いので隠して再利用する
        foreach (var child in ContentHost.Children.ToList())
            if (!ReferenceEquals(child, _web)) ContentHost.Children.Remove(child);
        if (_web != null) _web.Visibility = Visibility.Collapsed;
        ContentHost.Children.Add(element);
    }

    void StopMedia()
    {
        foreach (var m in ContentHost.Children.OfType<MediaPlayerElement>())
            m.MediaPlayer?.Pause();
    }

    static UIElement Image(string path)
    {
        var bmp = new BitmapImage { UriSource = new Uri(path) };
        var image = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        return new ScrollViewer
        {
            Content = image,
            ZoomMode = ZoomMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    async Task ShowWebAsync(string path)
    {
        foreach (var child in ContentHost.Children.ToList())
            if (!ReferenceEquals(child, _web)) ContentHost.Children.Remove(child);
        if (_web == null)
        {
            _web = new WebView2();
            ContentHost.Children.Add(_web);
            await _web.EnsureCoreWebView2Async();
        }
        _web.Visibility = Visibility.Visible;
        _web.Source = new Uri(path);
    }

    static UIElement Media(string path)
    {
        var player = new MediaPlayerElement
        {
            Source = MediaSource.CreateFromUri(new Uri(path)),
            AreTransportControlsEnabled = true,
            AutoPlay = false,
            Stretch = Stretch.Uniform,
        };
        return player;
    }

    static async Task<bool> LooksLikeTextAsync(string path)
    {
        try
        {
            var buffer = new byte[4096];
            int n;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                n = await fs.ReadAsync(buffer);
            if (n == 0) return true;
            // NUL を含まなければテキストとみなす（UTF-16 BOM は例外）
            if (n >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return true;
            return Array.IndexOf(buffer, (byte)0, 0, n) < 0;
        }
        catch (Exception) { return false; }
    }

    static async Task<UIElement> TextAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        bool truncated;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int len = (int)Math.Min(fs.Length, MaxTextBytes);
            truncated = fs.Length > MaxTextBytes;
            bytes = new byte[len];
            await fs.ReadExactlyAsync(bytes, ct);
        }
        string text = Decode(bytes);
        if (truncated) text += $"\n\n…（先頭 {MaxTextBytes / 1024} KB のみ表示）";
        return new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, BIZ UDGothic, MS Gothic"),
                FontSize = 13,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(12),
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>BOM → UTF-8 → Shift_JIS の順で判定する。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException ex) when (ex.Index >= bytes.Length - 4)
        {
            // 末尾で多バイト文字が途切れただけなら UTF-8
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    static async Task<UIElement> FolderSummaryAsync(FileItem item, CancellationToken ct)
    {
        var (files, dirs, bytes) = await Task.Run(() =>
        {
            int f = 0, d = 0;
            long b = 0;
            try
            {
                foreach (var info in new DirectoryInfo(item.FullPath).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (info is FileInfo fi) { f++; b += fi.Length; }
                    else d++;
                }
            }
            catch (UnauthorizedAccessException) { }
            return (f, d, b);
        }, ct);
        return Message($"フォルダー {dirs:N0} 個 ・ ファイル {files:N0} 個（直下 {Format.HumanSize(bytes)}）");
    }

    static UIElement Info(FileItem item)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 64 });
        panel.Children.Add(new TextBlock { Text = item.Name, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{item.TypeText} ・ {item.HumanSize}", HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        panel.Children.Add(new TextBlock { Text = "この種類はプレビューに未対応です（Office 等は今後のフェーズで対応）", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        return panel;
    }

    static UIElement Message(string text) => new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
