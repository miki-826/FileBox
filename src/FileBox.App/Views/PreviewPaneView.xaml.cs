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
        HeaderIcon.Foreground = Converters.ItemBrush(item.FullPath, item.Extension, item.IsDirectory, item.IsDrive);
        _ = ShowDetailsAsync(item, cts.Token);
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

    // ---------------------------------------------------------------- 詳細

    /// <summary>Windows のプロパティから表示するもの（ある項目だけ出す）。</summary>
    static readonly (string Key, string Label)[] ExtendedProperties =
    {
        ("System.Title", "タイトル"),
        ("System.Author", "作成者"),
        ("System.Document.LastAuthor", "最終更新者"),
        ("System.Company", "会社"),
        ("System.Image.Dimensions", "大きさ"),
        ("System.Photo.DateTaken", "撮影日時"),
        ("System.Photo.CameraModel", "カメラ"),
        ("System.Media.Duration", "長さ"),
        ("System.Video.FrameWidth", "フレーム幅"),
        ("System.Video.FrameHeight", "フレーム高"),
        ("System.Music.Artist", "アーティスト"),
        ("System.Music.AlbumTitle", "アルバム"),
        ("System.Keywords", "タグ"),
        ("System.Comment", "コメント"),
    };

    async Task ShowDetailsAsync(FileItem item, CancellationToken ct)
    {
        var rows = new List<(string Label, string Value)>();
        try
        {
            FileSystemInfo info = item.IsDirectory ? new DirectoryInfo(item.FullPath) : new FileInfo(item.FullPath);
            info.Refresh();
            rows.Add(("種類", item.TypeText));
            rows.Add(("場所", item.IsDrive ? item.FullPath : item.Location));
            if (info is FileInfo fi && fi.Exists) rows.Add(("サイズ", $"{Format.HumanSize(fi.Length)}（{fi.Length:N0} バイト）"));
            if (info.Exists)
            {
                rows.Add(("作成日時", info.CreationTime.ToString("yyyy/MM/dd HH:mm")));
                rows.Add(("更新日時", info.LastWriteTime.ToString("yyyy/MM/dd HH:mm")));
                rows.Add(("アクセス日時", info.LastAccessTime.ToString("yyyy/MM/dd HH:mm")));
                if (Attributes(info.Attributes) is { Length: > 0 } attrs) rows.Add(("属性", attrs));
            }
        }
        catch (Exception ex) { rows.Add(("エラー", ex.Message)); }
        RenderDetails(rows);

        if (item.IsDirectory) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FullPath);
            var props = await file.Properties.RetrievePropertiesAsync(ExtendedProperties.Select(p => p.Key));
            if (ct.IsCancellationRequested) return;
            foreach (var (key, label) in ExtendedProperties)
                if (props.TryGetValue(key, out var value) && FormatProperty(key, value) is { Length: > 0 } text)
                    rows.Add((label, text));
            RenderDetails(rows);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }   // 対応していない種類・アクセスできないファイルは基本の詳細だけ
    }

    void RenderDetails(List<(string Label, string Value)> rows)
    {
        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var value = new TextBlock { Text = rows[i].Value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            DetailsGrid.Children.Add(label);
            DetailsGrid.Children.Add(value);
        }
        DetailsHost.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Attributes(FileAttributes a)
    {
        var names = new List<string>();
        if (a.HasFlag(FileAttributes.ReadOnly)) names.Add("読み取り専用");
        if (a.HasFlag(FileAttributes.Hidden)) names.Add("隠し");
        if (a.HasFlag(FileAttributes.System)) names.Add("システム");
        if (a.HasFlag(FileAttributes.Compressed)) names.Add("圧縮");
        if (a.HasFlag(FileAttributes.Encrypted)) names.Add("暗号化");
        if (a.HasFlag(FileAttributes.Offline) || a.HasFlag((FileAttributes)0x400000)) names.Add("オンラインのみ（クラウド）");
        return string.Join("・", names);
    }

    static string? FormatProperty(string key, object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        string[] arr => string.Join("; ", arr.Where(v => !string.IsNullOrWhiteSpace(v))),
        DateTimeOffset d => d.LocalDateTime.ToString("yyyy/MM/dd HH:mm"),
        ulong ticks when key == "System.Media.Duration" => TimeSpan.FromTicks((long)ticks).ToString(ticks >= (ulong)TimeSpan.TicksPerHour ? @"h\:mm\:ss" : @"m\:ss"),
        uint n when key.StartsWith("System.Video.Frame") => $"{n} px",
        _ => value.ToString(),
    };

    static UIElement Info(FileItem item)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 64, Foreground = Converters.ItemBrush(item.FullPath, item.Extension, item.IsDirectory, item.IsDrive) });
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
