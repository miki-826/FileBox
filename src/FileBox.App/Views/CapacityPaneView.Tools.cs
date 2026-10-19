using System.Runtime.InteropServices;
using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.Contracts;
using FileBox.Core.Files;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>容量の画面の道具: 右クリックの操作・ツリーマップ・重複ファイル・片付けの候補。消すときはどれもごみ箱へ（Ctrl+Z で戻せる）。</summary>
public sealed partial class CapacityPaneView
{
    /// <summary>内訳・大きいファイル・図の右クリック: 開く・場所を開く・ごみ箱へ（確認と監査つき）。</summary>
    MenuFlyout ItemMenu(string path, bool isDirectory)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (isDirectory) Add("この中の容量を見る", "", () => _ = ViewModel.OpenAsync(path));
        Add("開く", "", () => { if (isDirectory) _actions.OpenInNewTab(path); else ShellService.Open(path); });
        Add("場所を開く", "", () => _actions.RevealInFolderPane(ViewModel, path));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("ごみ箱へ移動", "", () => _ = RecycleAsync(new[] { path }));
        return menu;
    }

    async Task RecycleAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string what = paths.Count == 1 ? $"「{Path.GetFileName(paths[0].TrimEnd('\\'))}」" : $"{paths.Count} 件";
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"{what}をごみ箱へ移動しますか？", "ごみ箱から元に戻せます（Ctrl+Z でも戻せます）。", "ごみ箱へ移動")) return;
        if (FileOperations.Instance.Recycle(paths) > 0) await ViewModel.LoadAsync();
    }

    static StackPanel TextContent(string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text });
        return content;
    }

    // ================================================================ ツリーマップ

    static readonly string[] MapColors = { "#3E63DD", "#30A46C", "#F76B15", "#8E4EC6", "#05A2C2", "#D6409F", "#E2A300", "#A07553", "#E5484D", "#0F9D8C" };

    UIElement TreemapView(UsageReport report)
    {
        var items = report.Children.Where(c => c.Size > 0).Take(80).ToList();
        var canvas = new Canvas();
        var host = new Border { Child = canvas, Height = 360, CornerRadius = new CornerRadius(8), Background = Brush("CardBackgroundFillColorDefaultBrush") };
        void Draw()
        {
            canvas.Children.Clear();
            double w = host.ActualWidth, h = host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var cells = Treemap.Layout(items.Select(i => (double)i.Size).ToList(), w, h);
            foreach (var cell in cells)
            {
                var item = items[cell.Index];
                string color = item.IsDirectory ? ViewPreferences.FolderColor(item.Path) ?? MapColors[cell.Index % MapColors.Length] : FileKinds.Color(Path.GetExtension(item.Path)) ?? "#8B8D98";
                var rect = new Border
                {
                    Width = Math.Max(0, cell.Width - 2), Height = Math.Max(0, cell.Height - 2),
                    Background = Converters.HexBrush(color), Opacity = item.IsDirectory ? 0.85 : 0.6,
                    CornerRadius = new CornerRadius(3),
                };
                string name = Path.GetFileName(item.Path.TrimEnd('\\'));
                if (cell.Width > 60 && cell.Height > 28)
                    rect.Child = new TextBlock
                    {
                        Text = $"{name}\n{Format.HumanSize(item.Size)}", Tag = "data", FontSize = 11, Margin = new Thickness(5, 3, 5, 3),
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap,
                    };
                ToolTipService.SetToolTip(rect, $"{item.Path}\n{Format.HumanSize(item.Size)}（{(report.Total > 0 ? 100.0 * item.Size / report.Total : 0):0.#}%）");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rect, $"{name} {Format.HumanSize(item.Size)}");
                if (item.IsDirectory) rect.Tapped += (_, _) => _ = ViewModel.OpenAsync(item.Path);
                rect.RightTapped += (_, e) => { ItemMenu(item.Path, item.IsDirectory).ShowAt(rect, e.GetPosition(rect)); e.Handled = true; };
                Canvas.SetLeft(rect, cell.X + 1);
                Canvas.SetTop(rect, cell.Y + 1);
                canvas.Children.Add(rect);
            }
        }
        host.SizeChanged += (_, _) => Draw();
        return host;
    }

    // ================================================================ 重複ファイル

    readonly Expander _duplicates = new() { Header = "重複ファイルを探す（この場所の中）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    CancellationTokenSource? _dupCts;

    void InitDuplicates()
    {
        var body = new StackPanel { Spacing = 8 };
        var status = Note("中身がまったく同じファイルを探します（名前が違っても見つかります）。1 MB 以上のファイルが対象です。");
        var results = new StackPanel { Spacing = 10 };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var start = new Button { Content = TextContent("", "探す") }.Named("探す");
        var cancel = new Button { Content = TextContent("", "中止"), Visibility = Visibility.Collapsed }.Named("中止");
        cancel.Click += (_, _) => _dupCts?.Cancel();
        start.Click += async (_, _) =>
        {
            _dupCts?.Cancel();
            var cts = _dupCts = new CancellationTokenSource();
            results.Children.Clear();
            start.IsEnabled = false;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            var report = new Progress<DuplicateProgress>(p => status.Text = p.Total > 0 ? $"{p.Stage}… {p.Done:N0} / {p.Total:N0}" : $"{p.Stage}… {p.Done:N0} 件");
            try
            {
                var groups = await DuplicateFinder.FindAsync(new[] { ViewModel.Path }, minSize: 1 << 20, report, cts.Token);
                long wasted = groups.Sum(g => g.Wasted);
                status.Text = groups.Count == 0 ? "重複したファイルは見つかりませんでした。" : $"{groups.Count:N0} 組見つかりました。整理すると {Format.HumanSize(wasted)} 空きます。";
                foreach (var g in groups.Take(100)) results.Children.Add(DuplicateGroupView(g, results));
            }
            catch (OperationCanceledException) { status.Text = "中止しました。"; }
            catch (Exception ex) { status.Text = FriendlyError.Explain(ex).ToString(); }
            finally
            {
                start.IsEnabled = true;
                cancel.Visibility = progress.Visibility = Visibility.Collapsed;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        body.Children.Add(status);
        body.Children.Add(buttons);
        body.Children.Add(progress);
        body.Children.Add(results);
        _duplicates.Content = body;
    }

    /// <summary>重複の 1 組。残すものを 1 つ選び（既定は一番上）、ほかをごみ箱へ移す。</summary>
    UIElement DuplicateGroupView(DuplicateGroup group, Panel container)
    {
        var card = new StackPanel { Spacing = 4, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Background = Brush("CardBackgroundFillColorDefaultBrush") };
        card.Children.Add(new TextBlock { Text = $"{Format.HumanSize(group.Size)} × {group.Paths.Count} 件（{Format.HumanSize(group.Wasted)} 空けられます）", FontWeight = FontWeights.SemiBold });
        string groupName = Guid.NewGuid().ToString("N");
        var radios = new List<(RadioButton Radio, string Path)>();
        foreach (var path in group.Paths)
        {
            var radio = new RadioButton { GroupName = groupName, IsChecked = radios.Count == 0, Content = new TextBlock { Text = path, Tag = "data", TextTrimming = TextTrimming.CharacterEllipsis } };
            ToolTipService.SetToolTip(radio, "これを残す");
            radios.Add((radio, path));
            card.Children.Add(radio);
        }
        var keep = new Button { Content = TextContent("", "選んだもの以外をごみ箱へ") }.Named("選んだもの以外をごみ箱へ");
        keep.Click += async (_, _) =>
        {
            var others = radios.Where(r => r.Radio.IsChecked != true).Select(r => r.Path).ToList();
            if (others.Count == 0) return;
            if (!await Dialogs.ConfirmAsync(XamlRoot, $"{others.Count} 件をごみ箱へ移動しますか？", "選んだ 1 件だけを残します。ごみ箱から元に戻せます（Ctrl+Z でも戻せます）。", "ごみ箱へ移動")) return;
            if (FileOperations.Instance.Recycle(others) > 0) container.Children.Remove(card);
        };
        card.Children.Add(keep);
        return card;
    }

    // ================================================================ 片付けの候補

    readonly Expander _cleanup = new() { Header = "片付けの候補（消しても困らないもの）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    void InitCleanup()
    {
        var body = new StackPanel { Spacing = 8 };
        var status = Note("一時ファイル（%TEMP%）の古いもの、ごみ箱の中身、ダウンロードの古いインストーラーや展開済みの ZIP を探します。消すかどうかは選んで決められます。");
        var list = new StackPanel { Spacing = 4 };
        var find = new Button { Content = TextContent("", "候補を探す") }.Named("候補を探す");
        find.Click += async (_, _) =>
        {
            find.IsEnabled = false;
            list.Children.Clear();
            status.Text = "探しています…";
            try
            {
                string temp = Path.GetTempPath();
                string? downloads = ShellService.KnownFolder(ShellService.QuickAccess[2].Id);
                var now = DateTime.Now;
                var candidates = await Task.Run(() =>
                {
                    var all = CleanupCandidates.ScanTemp(temp, now).ToList();
                    if (downloads != null) all.AddRange(CleanupCandidates.ScanDownloads(downloads, now).Where(c => c.Kind != CleanupKind.Unused));
                    return all;
                });
                long binSize = RecycleBinSize();
                status.Text = $"候補 {candidates.Count:N0} 件・合計 {Format.HumanSize(candidates.Sum(c => c.Size))}" + (binSize > 0 ? $"／ごみ箱 {Format.HumanSize(binSize)}" : "");
                if (binSize > 0)
                {
                    var empty = new Button { Content = TextContent("", $"ごみ箱を空にする（{Format.HumanSize(binSize)}）") }.Named($"ごみ箱を空にする（{Format.HumanSize(binSize)}）");
                    empty.Click += (_, _) =>
                    {
                        RecycleBin.Empty(App.Window != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.Window) : IntPtr.Zero);
                        Guard.Done("delete", "ごみ箱", detail: "ごみ箱を空にする（容量の画面）");
                    };
                    list.Children.Add(empty);
                }
                var checks = new List<(CheckBox Box, string Path)>();
                foreach (var c in candidates.Take(300))
                {
                    var box = new CheckBox { IsChecked = c.Kind is CleanupKind.TempFile or CleanupKind.ExtractedArchive };
                    var text = new StackPanel();
                    text.Children.Add(new TextBlock { Text = $"{Path.GetFileName(c.Path)}（{Format.HumanSize(c.Size)}）", Tag = "data", TextTrimming = TextTrimming.CharacterEllipsis });
                    text.Children.Add(new TextBlock { Text = c.Reason, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") });
                    box.Content = text;
                    ToolTipService.SetToolTip(box, c.Path);
                    checks.Add((box, c.Path));
                    list.Children.Add(box);
                }
                if (checks.Count > 0)
                {
                    var recycle = new Button { Content = TextContent("", "選んだものをごみ箱へ") }.Named("選んだものをごみ箱へ");
                    recycle.Click += (_, _) => _ = RecycleAsync(checks.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToList());
                    list.Children.Add(recycle);
                }
            }
            catch (Exception ex) { status.Text = FriendlyError.Explain(ex).ToString(); }
            finally { find.IsEnabled = true; }
        };
        body.Children.Add(status);
        body.Children.Add(find);
        body.Children.Add(list);
        _cleanup.Content = body;
    }

    static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);
}
