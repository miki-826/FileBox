using System.Collections.ObjectModel;
using System.Text.Json;
using FileBox.App.Services;
using FileBox.Core.Tools;
using FileBox.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileBox.App.Views;

/// <summary>対象・設定・保存先を確認して一括処理する。実行中の閉じる操作は中止要求にする。</summary>
public static class ConversionDialog
{
    static bool _showing;

    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<string> selected, ToolKind? initial = null)
    {
        if (_showing) { App.Window?.ShowInfo("変換・整理の画面が開いています"); return; }
        _showing = true;
        try { await ShowCoreAsync(root, selected, initial); }
        finally { _showing = false; }
    }

    static async Task ShowCoreAsync(XamlRoot root, IReadOnlyList<string> selected, ToolKind? initial)
    {
        var store = SettingsDialog.Store!;
        var paths = new ObservableCollection<string>(selected.Distinct(StringComparer.OrdinalIgnoreCase));
        var inputList = new ListView { ItemsSource = paths, ItemTemplate = WrappedTemplate(), MaxHeight = 145, SelectionMode = ListViewSelectionMode.Single };
        var addFiles = new Button { Content = "ファイルを追加…" };
        addFiles.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));
            foreach (var f in await picker.PickMultipleFilesAsync()) if (!paths.Contains(f.Path)) paths.Add(f.Path);
        };
        var addFolder = new Button { Content = "フォルダを追加…" };
        addFolder.Click += async (_, _) => { if (await SuggestMoveDialog.PickFolderAsync() is { } p && !paths.Contains(p)) paths.Add(p); };
        var remove = new Button { Content = "選択を外す" };
        remove.Click += (_, _) => { if (inputList.SelectedItem is string path) paths.Remove(path); };
        var up = new Button { Content = "上へ" }; var down = new Button { Content = "下へ" };
        up.Click += (_, _) => { int i = inputList.SelectedIndex; if (i > 0) { paths.Move(i, i - 1); inputList.SelectedIndex = i - 1; } };
        down.Click += (_, _) => { int i = inputList.SelectedIndex; if (i >= 0 && i + 1 < paths.Count) { paths.Move(i, i + 1); inputList.SelectedIndex = i + 1; } };
        var kindChoice = new ComboBox { Header = "処理", MinWidth = 420 };
        var available = ToolCatalog.All.Where(t => store.GetSetting("tools.hidden." + t.Kind) != "1" && (selected.Count == 0 || selected.Any(t.Accepts))).ToList();
        foreach (var t in available) kindChoice.Items.Add(new ComboBoxItem { Content = t.Group + " / " + t.Label, Tag = t });
        if (available.Count == 0) { App.Window?.ShowInfo("使える処理がありません。設定で変換メニューの表示を確認してください", InfoBarSeverity.Warning); return; }
        kindChoice.SelectedIndex = Math.Max(0, available.FindIndex(t => initial != null && t.Kind == initial));
        var form = new StackPanel { Spacing = 10 };
        var output = new TextBox { Header = "出力先", Text = store.GetSetting("tools.output") is { Length: > 0 } preferred ? preferred : selected.FirstOrDefault() is { } first ? Directory.Exists(first) ? first : Path.GetDirectoryName(first)! : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), MinWidth = 420 };
        var chooseOutput = new Button { Content = "出力先を選択…" };
        chooseOutput.Click += async (_, _) => { if (await SuggestMoveDialog.PickFolderAsync() is { } folder) output.Text = folder; };
        var note = Note("");
        var validation = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning, IsOpen = false };
        var runDialog = new ContentDialog { XamlRoot = root, Title = "ファイルの変換・整理", PrimaryButtonText = "実行", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.None };
        Func<ToolOptions> readOptions = () => new();
        CheckBox? jarConsent = null;
        void BuildForm()
        {
            if (kindChoice.SelectedItem is not ComboBoxItem { Tag: ToolDefinition tool }) return;
            form.Children.Clear(); validation.IsOpen = false; jarConsent = null;
            ToolOptions saved;
            try { saved = JsonSerializer.Deserialize<ToolOptions>(store.GetSetting("tools.preset." + tool.Kind) ?? "null") ?? new(); }
            catch (JsonException) { saved = new(); }
            var format = Combo("出力形式", tool.Formats, tool.Formats.Contains(saved.Format) ? saved.Format : tool.Formats[0]);
            var width = Number("幅（px）", saved.Width, 1, 16384); var height = Number("高さ（px）", saved.Height, 1, 16384);
            var aspect = new CheckBox { Content = "縦横比を維持", IsChecked = saved.KeepAspect };
            var quality = Number("画質（1～100）", saved.Quality, 1, 100);
            var target = Number("目標容量（MiB、0 なら画質を使用）", saved.TargetMiB, 0, 102400);
            var angle = Number("回転角度", saved.Angle, -360, 360);
            var crop = new CheckBox { Content = "回転後に元の大きさで切り抜く", IsChecked = saved.Crop };
            var background = new TextBox { Header = "背景色（#RRGGBB）", Text = saved.Background };
            var tolerance = Number("色の許容差（%）", saved.Tolerance, 0, 100);
            var start = Number("開始秒（画像抽出はこの時刻）", saved.StartSeconds, 0, 604800);
            var end = Number("終了秒（0 なら末尾まで）", saved.EndSeconds, 0, 604800);
            var fast = new CheckBox { Content = "高速モード（再エンコードなし）", IsChecked = saved.FastTrim };
            var codec = Combo("動画コーデック", ["h264", "h265", "vp9", "av1"], saved.Codec);
            var hardware = Combo("エンコード", ["cpu", "nvenc", "qsv", "amf"], saved.Hardware);
            var fps = Number("フレームレート", saved.FrameRate, 1, 60);
            var dpi = Number("解像度（DPI）", saved.Dpi, 36, 600);
            var pages = new TextBox { Header = "ページ範囲（空欄なら全ページ）", PlaceholderText = "1,3-5", Text = saved.Pages };
            var delimiter = Combo("区切り文字", [",", ";", "TAB", "|"], saved.Delimiter == "\t" ? "TAB" : saved.Delimiter);
            var header = new CheckBox { Content = "先頭行を見出しとして使用", IsChecked = saved.HasHeader };
            string[] encodings = ["UTF-8", "UTF-8 BOM", "UTF-16 LE", "UTF-16 BE", "Shift_JIS"];
            var inputEncoding = Combo("入力の文字コード", encodings, saved.InputEncoding);
            var outputEncoding = Combo("出力の文字コード", encodings, saved.OutputEncoding);
            var newline = Combo("出力の改行コード", ["LF", "CRLF", "CR"], saved.Newline);
            var depth = Number("ツリーの深さ", saved.Depth, 1, 30);
            var language = new ComboBox { Header = "OCR の言語", MinWidth = 240 };
            language.Items.Add(new ComboBoxItem { Content = "Windows の優先言語", Tag = "" });
            foreach (var l in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages) language.Items.Add(new ComboBoxItem { Content = l.DisplayName, Tag = l.LanguageTag });
            language.SelectedIndex = language.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == saved.OcrLanguage);
            if (language.SelectedIndex < 0) language.SelectedIndex = 0;
            form.Children.Add(format);
            if (tool.Kind is ToolKind.ImageResize or ToolKind.VideoResize or ToolKind.VideoGif)
            {
                var presets = Combo("サイズのプリセット", ["カスタム", "1920 × 1080", "1280 × 720", "800 × 600", "512 × 512"], "カスタム");
                presets.SelectionChanged += (_, _) => { var sizes = new[] { (saved.Width, saved.Height), (1920, 1080), (1280, 720), (800, 600), (512, 512) }; var s = sizes[Math.Max(0, presets.SelectedIndex)]; width.Value = s.Item1; height.Value = s.Item2; };
                form.Children.Add(presets); form.Children.Add(Row(width, height)); form.Children.Add(aspect);
            }
            if (tool.Engine == "magick" || tool.Kind is ToolKind.VideoConvert or ToolKind.VideoCompress or ToolKind.VideoTrim or ToolKind.VideoResize or ToolKind.VideoRotate) form.Children.Add(quality);
            if (tool.Kind is ToolKind.ImageCompress or ToolKind.VideoCompress) form.Children.Add(target);
            if (tool.Kind is ToolKind.ImageRotate or ToolKind.VideoRotate) form.Children.Add(angle);
            if (tool.Kind == ToolKind.ImageRotate) form.Children.Add(crop);
            if (tool.Engine == "magick") form.Children.Add(background);
            if (tool.Kind == ToolKind.ImageRemoveColor) form.Children.Add(tolerance);
            if (tool.Kind == ToolKind.ImageOcr) form.Children.Add(language);
            if (tool.Group == "動画") { form.Children.Add(Row(start, end)); }
            if (tool.Kind == ToolKind.VideoTrim) form.Children.Add(fast);
            if (tool.Kind is ToolKind.VideoConvert or ToolKind.VideoTrim or ToolKind.VideoCompress or ToolKind.VideoResize or ToolKind.VideoRotate)
            {
                form.Children.Add(Row(codec, hardware));
                format.SelectionChanged += (_, _) => { if ((string)format.SelectedItem == "webm" && (string)codec.SelectedItem is "h264" or "h265") codec.SelectedItem = "vp9"; };
                if ((string)format.SelectedItem == "webm" && (string)codec.SelectedItem is "h264" or "h265") codec.SelectedItem = "vp9";
            }
            if (tool.Kind == ToolKind.VideoGif) form.Children.Add(fps);
            if (tool.Kind == ToolKind.PdfImages) { form.Children.Add(pages); form.Children.Add(dpi); }
            if (tool.Kind is ToolKind.CsvConvert or ToolKind.JsonConvert) form.Children.Add(delimiter);
            if (tool.Kind == ToolKind.CsvConvert) form.Children.Add(header);
            if (tool.Group == "データ" && tool.Kind != ToolKind.JarSources) form.Children.Add(inputEncoding);
            if (tool.Kind is ToolKind.TextEncoding or ToolKind.TextNewlines) form.Children.Add(outputEncoding);
            if (tool.Kind == ToolKind.TextNewlines) form.Children.Add(newline);
            if (tool.Kind == ToolKind.FolderTree) form.Children.Add(depth);
            if (tool.Kind == ToolKind.JarSources)
            {
                jarConsent = new CheckBox { Content = "利用権限と対象コードのライセンスを確認しました", IsChecked = false };
                form.Children.Add(jarConsent);
            }
            note.Text = Notice(tool);
            readOptions = () => new ToolOptions
            {
                Format = (string)format.SelectedItem, Width = Integer(width), Height = Integer(height), KeepAspect = aspect.IsChecked == true,
                Quality = Integer(quality), TargetMiB = target.Value, Angle = Integer(angle), Crop = crop.IsChecked == true, Background = background.Text.Trim(), Tolerance = Integer(tolerance),
                StartSeconds = start.Value, EndSeconds = end.Value, FastTrim = fast.IsChecked == true, Codec = (string)codec.SelectedItem, Hardware = (string)hardware.SelectedItem,
                FrameRate = Integer(fps), Dpi = Integer(dpi), Pages = pages.Text, Delimiter = (string)delimiter.SelectedItem == "TAB" ? "\t" : (string)delimiter.SelectedItem,
                HasHeader = header.IsChecked == true, InputEncoding = (string)inputEncoding.SelectedItem, OutputEncoding = (string)outputEncoding.SelectedItem,
                Newline = (string)newline.SelectedItem, Depth = Integer(depth), OcrLanguage = (string)((ComboBoxItem)language.SelectedItem).Tag,
            };
        }
        kindChoice.SelectionChanged += (_, _) => BuildForm();
        paths.CollectionChanged += (_, _) =>
        {
            var previous = (kindChoice.SelectedItem as ComboBoxItem)?.Tag as ToolDefinition;
            var updated = ToolCatalog.All.Where(t => store.GetSetting("tools.hidden." + t.Kind) != "1" && (paths.Count == 0 || paths.Any(t.Accepts))).ToList();
            if (updated.Select(t => t.Kind).SequenceEqual(kindChoice.Items.Cast<ComboBoxItem>().Select(i => ((ToolDefinition)i.Tag).Kind))) return;
            kindChoice.Items.Clear();
            foreach (var t in updated) kindChoice.Items.Add(new ComboBoxItem { Content = t.Group + " / " + t.Label, Tag = t });
            kindChoice.SelectedIndex = Math.Max(0, updated.FindIndex(t => t.Kind == previous?.Kind));
        };
        var folderTools = new Button { Content = "重複・空フォルダ・容量・名前の変更…" };
        bool openOrganize = false;
        folderTools.Click += (_, _) => { openOrganize = true; runDialog.Hide(); };
        var body = new StackPanel { Spacing = 12, MinWidth = 500, MaxWidth = 620 };
        body.Children.Add(Note("原本を残して別名で保存します。同名ファイルは上書きしません。PDF の結合順は下の一覧で変更できます。"));
        body.Children.Add(inputList); body.Children.Add(Row(addFiles, addFolder)); body.Children.Add(Row(up, down, remove));
        body.Children.Add(kindChoice); body.Children.Add(form); body.Children.Add(note); body.Children.Add(output); body.Children.Add(chooseOutput); body.Children.Add(folderTools); body.Children.Add(validation);
        runDialog.Content = new ScrollViewer { Content = body, MaxHeight = 620 };
        BuildForm();
        ToolOptions? finalOptions = null;
        ToolDefinition? finalTool = null;
        runDialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                if (paths.Count == 0) throw new ArgumentException("入力を追加してください");
                if (!Directory.Exists(output.Text.Trim())) throw new ArgumentException("出力先のフォルダを選択してください");
                finalTool = (ToolDefinition)((ComboBoxItem)kindChoice.SelectedItem).Tag;
                finalOptions = readOptions(); finalOptions.Validate(finalTool.Kind);
                if (finalTool.Kind == ToolKind.JarSources && jarConsent?.IsChecked != true) throw new ArgumentException("利用権限とライセンスを確認してください");
                if (finalTool.Kind == ToolKind.PdfMerge && paths.Count(p => finalTool.Accepts(p)) < 2) throw new ArgumentException("結合する PDF を 2 件以上選択してください");
                string? engine = finalTool.Engine;
                if (finalTool.Kind == ToolKind.ArchiveCreate && finalOptions.Format == "7z" || finalTool.Kind == ToolKind.ArchiveExtract && paths.Any(p => Path.GetExtension(p).ToLowerInvariant() is ".rar" or ".7z" || p.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))) engine = "7z";
                if (engine != null && !File.Exists(store.GetSetting("tools.engine." + engine))) throw new ArgumentException("設定で必要な外部エンジンを指定してください");
                if (finalTool.Kind == ToolKind.JarSources && !File.Exists(store.GetSetting("tools.engine.cfr"))) throw new ArgumentException("設定で CFR の JAR を指定してください");
                SafeOutput.RejectReparseParents(output.Text.Trim());
            }
            catch (Exception ex) { e.Cancel = true; validation.Message = ex.Message; validation.IsOpen = true; }
        };
        var choice = await runDialog.ShowAsync();
        if (openOrganize) { await OrganizeToolsDialog.ShowAsync(root, paths.ToList()); return; }
        if (choice != ContentDialogResult.Primary || finalOptions == null || finalTool == null) return;
        store.SetSetting("tools.preset." + finalTool.Kind, JsonSerializer.Serialize(finalOptions));
        await RunAsync(root, store, paths.ToList(), finalTool, finalOptions, output.Text.Trim());
    }

    static string Notice(ToolDefinition tool) => tool.Kind switch
    {
        ToolKind.ImageRemoveColor => "単色の背景を透明にします。結果一覧の画像を選ぶと処理前後を確認できます。",
        ToolKind.ImageStripMetadata => "EXIF・GPS・コメント・色プロファイルなど、エンジンが扱える付加情報を削除して保存します。",
        ToolKind.ImageRotate => "PNG・WebP の回転余白は透明です。JPEG の余白には指定した背景色を使います。",
        ToolKind.ImageCompress => "目標容量は保証値ではありません。PNG などでは画質を下げても小さくならない場合があります。",
        ToolKind.VideoTrim => "高速モードはキーフレームの制約で開始位置がずれる場合があります。精密な切り出しは高速モードをオフにしてください。",
        ToolKind.VideoCompress => "容量を指定する場合は動画の終了秒も指定します。容量は目標値です。GPU が利用できないときは CPU を選択してください。",
        ToolKind.PdfText => "PDF 用 IFilter が必要です。画像だけの PDF はページを画像化し、OCR で読み取れます。",
        ToolKind.JsonSort => "すべての階層でキーを文字コード順に並べ替えます。配列の順序は維持します。",
        ToolKind.ConfigConvert => "[セクション] と キー=値 の構文に対応します。コメントは出力せず、値は文字列として保存します。",
        ToolKind.JarSources => "Java と CFR が必要です。対象 JAR は実行しません。利用権限とライセンスを確認してください。復元結果は元のソースと一致するとは限りません。",
        ToolKind.ArchiveExtract => "展開は新規フォルダへ行います。不正なパス・リンク・20 GiB を超える展開・10 万項目超は拒否します。",
        ToolKind.ImageConvert => "対応形式は導入した ImageMagick に依存します。HEIC は対応ビルドが必要です。SVG は読み取りのみ。アニメーションは先頭フレームを出力します。",
        _ => "対応していない入力は理由を表示してスキップします。処理中は安全に中止できます。",
    };

    static async Task RunAsync(XamlRoot root, UserStore store, IReadOnlyList<string> inputs, ToolDefinition tool, ToolOptions options, string destination)
    {
        using var cts = new CancellationTokenSource();
        bool running = true;
        var status = Note("処理を開始しています…");
        var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, inputs.Count), IsIndeterminate = true };
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap, Tag = "data" };
        var list = new ListView { ItemTemplate = WrappedTemplate(), MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 190, Visibility = Visibility.Collapsed };
        var before = new Image { MaxHeight = 160, MaxWidth = 240, Stretch = Stretch.Uniform };
        var after = new Image { MaxHeight = 160, MaxWidth = 240, Stretch = Stretch.Uniform };
        var images = Row(new StackPanel { Children = { Note("処理前"), before } }, new StackPanel { Children = { Note("処理後"), after } }); images.Visibility = Visibility.Collapsed;
        var open = new Button { Content = "選択結果の保存先を開く", IsEnabled = false };
        var copy = new Button { Content = "テキストをコピー", IsEnabled = false };
        var dialog = new ContentDialog { XamlRoot = root, Title = tool.Label, CloseButtonText = "中止" };
        dialog.CloseButtonClick += (_, e) => { if (running) { e.Cancel = true; cts.Cancel(); dialog.CloseButtonText = "停止しています…"; } };
        dialog.Closing += (_, e) => { if (running) { e.Cancel = true; cts.Cancel(); } };
        list.SelectionChanged += async (_, _) =>
        {
            before.Source = after.Source = null; images.Visibility = Visibility.Collapsed; details.Visibility = Visibility.Collapsed;
            if (list.SelectedItem is not ConversionResult result) return;
            open.IsEnabled = result.Output != null;
            copy.IsEnabled = false;
            string text = result.Message ?? "";
            if (result.Detail != null) text += "\n\n" + result.Detail;
            try
            {
                if (result.Output is { } file && File.Exists(file))
                {
                    if (Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length < 1024 * 1024)
                    {
                        text = await File.ReadAllTextAsync(file);
                        if (!ReferenceEquals(list.SelectedItem, result)) return;
                        copy.IsEnabled = true;
                    }
                    if (tool.Group == "画像" && tool.Kind != ToolKind.ImageOcr)
                    {
                        before.Source = new BitmapImage(new Uri(result.Input)); after.Source = new BitmapImage(new Uri(file)); images.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex) { text = ex.Message; }
            details.Text = text; details.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        open.Click += (_, _) => { if (list.SelectedItem is ConversionResult { Output: { } path }) ShellService.ShowInExplorer(path); };
        copy.Click += (_, _) => ShellService.CopyText(details.Text);
        var content = new StackPanel { Spacing = 12, MinWidth = 500 };
        content.Children.Add(status); content.Children.Add(current); content.Children.Add(progress); content.Children.Add(list); content.Children.Add(images); content.Children.Add(details); content.Children.Add(Row(open, copy));
        dialog.Content = new ScrollViewer { Content = content, MaxHeight = 620 };
        var shown = dialog.ShowAsync();
        IReadOnlyList<ConversionResult> results = Array.Empty<ConversionResult>();
        try
        {
            results = await ConversionService.RunAsync(store, inputs, tool.Kind, options, destination,
                new DirectProgress<ConversionProgress>(p => { progress.IsIndeterminate = p.Done < p.Total; progress.Maximum = Math.Max(1, p.Total); progress.Value = p.Done; current.Text = p.Current; status.Text = $"{p.Done} / {p.Total} 件 完了"; }),
                new DirectProgress<ConversionResult>(r => list.Items.Add(r)), cts.Token);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { running = false; progress.IsIndeterminate = false; dialog.CloseButtonText = "閉じる"; }
        if (results.Count > 0)
        {
            status.Text = $"成功 {results.Count(r => r.State == "成功")}・失敗 {results.Count(r => r.State == "失敗")}・スキップ {results.Count(r => r.State == "スキップ")}・キャンセル {results.Count(r => r.State == "キャンセル")} 件";
            list.SelectedIndex = 0;
        }
        await shown;
    }

    static int Integer(NumberBox number) => double.IsFinite(number.Value) ? checked((int)number.Value) : throw new ArgumentException("数値を入力してください");
    sealed class DirectProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    static DataTemplate WrappedTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding}" TextWrapping="Wrap" MaxWidth="530" Tag="data" /></DataTemplate>
        """);
    static NumberBox Number(string header, double value, double min, double max) => new() { Header = header, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 180 };
    static ComboBox Combo(string header, string[] choices, string selected)
    {
        var combo = new ComboBox { Header = header, MinWidth = 180 };
        foreach (string item in choices) combo.Items.Add(item);
        combo.SelectedItem = choices.Contains(selected) ? selected : choices[0]; return combo;
    }
    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) row.Children.Add(child); return row;
    }
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
}
