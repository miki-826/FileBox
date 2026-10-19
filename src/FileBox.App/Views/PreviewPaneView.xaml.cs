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
    PreviewHandlerHost? _handlerHost;

    static PreviewPaneView() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public PreviewPaneView(PreviewPaneViewModel viewModel, IWorkspaceActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        ViewModel.PropertyChanged += OnChanged;
        Loaded += (_, _) => { ViewModel.PropertyChanged -= OnChanged; ViewModel.PropertyChanged += OnChanged; };
        PointerPressed += (_, _) => _actions.ActivatePane(ViewModel);
        Unloaded += (_, _) => _handlerHost?.Unload();
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
            else if (ext is ".md" or ".markdown") await ShowMarkdownAsync(item.FullPath, cts.Token);
            else if (Core.Preview.SyntaxHighlighter.Supports(ext) && item.Size <= MaxCodeBytes) SetContent(await CodeAsync(item.FullPath, ext, cts.Token));
            else if (FileKinds.IsWeb(ext)) await ShowWebAsync(item.FullPath);
            else if (FileKinds.IsVideo(ext) || FileKinds.IsAudio(ext)) SetContent(Media(item.FullPath));
            else if (ext == ".zip") SetContent(await ZipAsync(item, cts.Token));
            else if (PreviewHandlerHost.HandlerFor(ext) != null) ShowWithHandler(item);
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
        _handlerHost?.Unload();
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

    async Task ShowWebAsync(string path, string? html = null)
    {
        _handlerHost?.Unload();
        foreach (var child in ContentHost.Children.ToList())
            if (!ReferenceEquals(child, _web)) ContentHost.Children.Remove(child);
        if (_web == null)
        {
            _web = new WebView2();
            ContentHost.Children.Add(_web);
            await _web.EnsureCoreWebView2Async();
        }
        _web.Visibility = Visibility.Visible;
        if (html != null) _web.NavigateToString(html);
        else _web.Source = new Uri(path);
    }

    // ---------------------------------------------------------------- Markdown・コード・Office・ZIP

    const long MaxCodeBytes = 256 * 1024;

    async Task ShowMarkdownAsync(string path, CancellationToken ct)
    {
        string text = await Task.Run(() => Core.Files.TextExtractor.ReadText(path), ct);
        bool dark = ActualTheme == ElementTheme.Dark;
        await ShowWebAsync(path, Core.Preview.Markdown.ToHtml(text, dark));
    }

    /// <summary>キーワード・文字列・コメント・数値を色分けして表示する。</summary>
    async Task<UIElement> CodeAsync(string path, string ext, CancellationToken ct)
    {
        var tokens = await Task.Run(() => Core.Preview.SyntaxHighlighter.Tokenize(Core.Files.TextExtractor.ReadText(path), ext), ct);
        bool dark = ActualTheme == ElementTheme.Dark;
        var colors = new Dictionary<Core.Preview.TokenKind, string>
        {
            [Core.Preview.TokenKind.Keyword] = dark ? "#569CD6" : "#0000FF",
            [Core.Preview.TokenKind.String] = dark ? "#CE9178" : "#A31515",
            [Core.Preview.TokenKind.Comment] = dark ? "#6A9955" : "#008000",
            [Core.Preview.TokenKind.Number] = dark ? "#B5CEA8" : "#098658",
        };
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
        foreach (var token in tokens)
        {
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = token.Text };
            if (colors.TryGetValue(token.Kind, out var hex)) run.Foreground = Converters.HexBrush(hex);
            paragraph.Inlines.Add(run);
        }
        var block = new RichTextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, BIZ UDGothic, MS Gothic"),
            FontSize = 13,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(12),
        };
        block.Blocks.Add(paragraph);
        return new ScrollViewer
        {
            Content = block,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>Office など、Windows のプレビュー ハンドラーで表示する。</summary>
    void ShowWithHandler(FileItem item)
    {
        var area = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        SetContent(area);
        _handlerHost ??= new PreviewHandlerHost(ContentHost);
        var window = App.Window != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.Window) : IntPtr.Zero;
        // 配置が決まってから（レイアウトの後で）表示する
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(ViewModel.Item, item) || !ContentHost.Children.Contains(area)) return;
            if (!_handlerHost.Show(item.FullPath, window)) SetContent(Info(item));
        });
    }

    /// <summary>ZIP の中身の一覧。選んだものだけ、またはすべてを展開できる。</summary>
    async Task<UIElement> ZipAsync(FileItem zip, CancellationToken ct)
    {
        var entries = await Task.Run(() => Core.Files.ArchiveService.List(zip.FullPath), ct);
        var root = new Grid { Margin = new Thickness(12, 8, 12, 8), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        long total = entries.Sum(e => e.Size);
        root.Children.Add(new TextBlock { Text = $"{entries.Count(e => !e.IsDirectory):N0} ファイル・展開後 {Format.HumanSize(total)}", FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple };
        foreach (var e in entries.Take(3000))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(e.Depth * 16, 0, 0, 0), Tag = e.FullName };
            row.Children.Add(new FileItemIcon { Extension = e.IsDirectory ? "" : Path.GetExtension(e.Name), IsDirectory = e.IsDirectory, IconSize = 20 });
            row.Children.Add(new TextBlock { Text = e.Name, Tag = "data", VerticalAlignment = VerticalAlignment.Center });
            if (!e.IsDirectory) row.Children.Add(new TextBlock { Text = Format.HumanSize(e.Size), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            list.Items.Add(row);
        }
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var selected = new Button { Content = "選んだものを展開", IsEnabled = false };
        list.SelectionChanged += (_, _) => selected.IsEnabled = list.SelectedItems.Count > 0;
        selected.Click += async (_, _) =>
        {
            var names = list.SelectedItems.OfType<StackPanel>().Select(p => (string)p.Tag).ToList();
            await ExtractAsync(zip, names);
        };
        var all = new Button { Content = "すべて展開" };
        all.Click += async (_, _) => await ExtractAsync(zip, null);
        buttons.Children.Add(selected);
        buttons.Children.Add(all);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return root;
    }

    /// <summary>展開する。すべてなら中身に合わせて場所を決め、一部なら ZIP の名前のフォルダーに出す。</summary>
    static async Task ExtractAsync(FileItem zip, IReadOnlyList<string>? names)
    {
        string parent = zip.Location;
        if (!await Guard.TransferAsync(new[] { zip.FullPath }, parent, move: false, "save")) return;
        try
        {
            string dest;
            if (names == null)
            {
                var (d, strip) = await Task.Run(() => Core.Files.ArchiveService.PlanExtract(zip.FullPath));
                await Core.Files.ArchiveService.ExtractEntriesAsync(zip.FullPath, null, d, strip);
                dest = d;
            }
            else
            {
                dest = Core.Files.UniqueName.Path(Path.Combine(parent, Path.GetFileNameWithoutExtension(zip.Name)));
                await Core.Files.ArchiveService.ExtractEntriesAsync(zip.FullPath, names, dest);
            }
            Guard.Created("save", dest);
            App.Window?.ShowInfo($"「{Path.GetFileName(dest.TrimEnd('\\'))}」に展開しました", InfoBarSeverity.Success, "開く", () => App.Window?.OpenInNewTab(dest));
        }
        catch (Exception ex) { App.ShowError("展開できませんでした", ex, zip.FullPath); }
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

    async Task ShowDetailsAsync(FileItem item, CancellationToken ct)
    {
        var rows = new List<(string Label, string Value, string? Key)>();
        void AddRow(string label, string value) => rows.Add((label, value, null));
        try
        {
            FileSystemInfo info = item.IsDirectory ? new DirectoryInfo(item.FullPath) : new FileInfo(item.FullPath);
            info.Refresh();
            AddRow("種類", item.TypeText);
            AddRow("場所", item.IsDrive ? item.FullPath : item.Location);
            if (info is FileInfo fi && fi.Exists) AddRow("サイズ", $"{Format.HumanSize(fi.Length)}（{fi.Length:N0} バイト）");
            if (info.Exists)
            {
                AddRow("作成日時", info.CreationTime.ToString("yyyy/MM/dd HH:mm"));
                AddRow("更新日時", info.LastWriteTime.ToString("yyyy/MM/dd HH:mm"));
                AddRow("アクセス日時", info.LastAccessTime.ToString("yyyy/MM/dd HH:mm"));
                if (Attributes(info.Attributes) is { Length: > 0 } attrs) AddRow("属性", attrs);
            }
            if (UserMarks.TagsOf(item.FullPath) is { Count: > 0 } tags) AddRow("FileBox のタグ", string.Join("・", tags));
        }
        catch (Exception ex) { AddRow("エラー", Core.Files.FriendlyError.Explain(ex).ToString()); }
        RenderDetails(item, rows);

        if (item.IsDirectory) return;
        var values = await FileProperties.ReadAsync(item.FullPath, FileProperties.Details.Select(p => p.Key));
        if (ct.IsCancellationRequested) return;
        foreach (var (key, label) in FileProperties.Details)
        {
            if (values.TryGetValue(key, out var text)) rows.Add((label, text, key));
            // 書き換えられる項目は、空でも「追加」できるように行を出す（写真・音楽・Office など）
            else if (FileProperties.Editable.ContainsKey(key) && values.Count > 0) rows.Add((label, "", key));
        }
        RenderDetails(item, rows);
    }

    void RenderDetails(FileItem item, List<(string Label, string Value, string? Key)> rows)
    {
        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        for (int i = 0; i < rows.Count; i++)
        {
            var (labelText, valueText, key) = rows[i];
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = labelText, FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            var cell = new Grid { ColumnSpacing = 4 };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var value = new TextBlock { Text = valueText, Tag = key != null || labelText == "場所" ? "data" : null, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(value);
            if (key != null && FileProperties.Editable.ContainsKey(key))
            {
                var edit = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(5, 3, 5, 3),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                ToolTipService.SetToolTip(edit, $"{labelText}を編集");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, $"{labelText}を編集");
                Grid.SetColumn(edit, 1);
                edit.Click += (_, _) => BeginEdit(item, key, labelText, valueText, cell);
                cell.Children.Add(edit);
            }
            Grid.SetRow(label, i);
            Grid.SetRow(cell, i);
            Grid.SetColumn(cell, 1);
            DetailsGrid.Children.Add(label);
            DetailsGrid.Children.Add(cell);
        }
        DetailsHost.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>詳細の値をその場で書き換える（Enter で保存、Esc でやめる）。</summary>
    void BeginEdit(FileItem item, string key, string label, string current, Grid cell)
    {
        cell.Children.Clear();
        var box = new TextBox { Text = current, FontSize = 12, MinWidth = 120,
            PlaceholderText = FileProperties.Editable[key] == typeof(string[]) ? "複数は ; で区切る" : FileProperties.Editable[key] == typeof(DateTimeOffset) ? "2026/10/20 14:30" : "" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
        cell.Children.Add(box);
        async Task Save()
        {
            if (await FileProperties.WriteAsync(item.FullPath, key, box.Text.Trim())) App.Window?.ShowInfo($"{label}を変更しました", InfoBarSeverity.Success);
            _ = ShowDetailsAsync(item, CancellationToken.None);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = Save(); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; _ = ShowDetailsAsync(item, CancellationToken.None); }
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
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

    static UIElement Info(FileItem item)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 64, Foreground = Converters.ItemBrush(item.FullPath, item.Extension, item.IsDirectory, item.IsDrive) });
        panel.Children.Add(new TextBlock { Text = item.Name, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{item.TypeText} ・ {item.HumanSize}", HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        panel.Children.Add(new TextBlock { Text = "この種類はプレビューできません（表示に使う Windows のプレビュー機能が入っていません）", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
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
