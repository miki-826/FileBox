using FileBox.App.Services;
using FileBox.Core.Suggest;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 「もしかしてこのフォルダ？」ダイアログ（02_機能設計 §1.1 入口①、04_画面設計 保存先提案パネル）。
/// 候補上位3件＋新規フォルダ提案＋別の場所を選び、移動・学習・元に戻すまで行う。
/// </summary>
public static class SuggestMoveDialog
{
    /// <summary>files を提案先へ移動する。移動したら移動先フォルダを返す。</summary>
    public static async Task<string?> RunAsync(XamlRoot root, IReadOnlyList<string> files, SuggestionResult? precomputed = null)
    {
        if (files.Count == 0) return null;
        var choice = await ShowAsync(root, files, precomputed);
        if (choice == null) return null;
        return await MoveAndLearnAsync(files, choice.Value.Target, choice.Value.Shown);
    }

    /// <summary>移動して学習し、元に戻せる通知を出す。</summary>
    public static async Task<string?> MoveAndLearnAsync(IReadOnlyList<string> files, string target, SuggestionResult? shown)
    {
        var (op, errors) = await FileOperations.Instance.MoveQuietAsync(files, target);
        foreach (var (from, _) in op?.Items ?? Array.Empty<(string, string)>())
            SuggestionService.Instance.Record(Path.GetFileName(from), target, files.Count == 1 ? shown : null);

        var window = App.Window;
        if (window == null) return op != null ? target : null;
        if (errors.Count > 0)
            window.ShowInfo($"{errors.Count} 件を移動できませんでした: {errors[0]}", InfoBarSeverity.Error);
        else if (op != null)
            window.ShowInfo($"{op.Items.Count} 件を「{Leaf(target)}」へ移動しました", InfoBarSeverity.Success, "元に戻す (Ctrl+Z)",
                () => _ = FileOperations.Instance.UndoAsync());
        return op != null ? target : null;
    }

    /// <summary>提案ダイアログだけを表示する。キャンセル時は null。</summary>
    public static async Task<(string Target, SuggestionResult? Shown)?> ShowAsync(XamlRoot root, IReadOnlyList<string> files, SuggestionResult? precomputed = null)
    {
        string first = files[0];
        var options = new StackPanel { Spacing = 4, MinWidth = 520 };
        var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 8) };
        loading.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        loading.Children.Add(new TextBlock { Text = "似たファイルのある場所を探しています…", Foreground = Secondary });
        options.Children.Add(loading);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = files.Count == 1 ? Path.GetFileName(first) : $"{Path.GetFileName(first)} ほか {files.Count - 1} 件",
            Foreground = Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(options);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "もしかしてこのフォルダ？",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = "移動",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        string group = Guid.NewGuid().ToString("N");
        SuggestionResult? result = precomputed;
        TextBox? newName = null;
        string? newParent = null;
        var radios = new List<(RadioButton Radio, Func<string> Target)>();

        RadioButton AddRadio(UIElement body, Func<string> target, int index = -1)
        {
            var radio = new RadioButton { GroupName = group, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
            radio.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            if (index < 0) options.Children.Add(radio); else options.Children.Insert(index, radio);
            radios.Add((radio, target));
            return radio;
        }

        void Render(SuggestionResult r)
        {
            options.Children.Clear();
            foreach (var c in r.Candidates)
                AddRadio(CandidateBody(c), () => c.Path);

            if (r.NewFolder is { } nf)
            {
                newParent = Path.GetDirectoryName(nf.Path)!;
                bool nothingFits = r.Candidates.Count == 0 || r.Candidates[0].Confidence == Confidence.Low;
                options.Children.Add(new TextBlock
                {
                    Text = nothingFits ? "該当するフォルダはなさそうです。新しく作りますか？" : "新しいフォルダを作る場合",
                    Margin = new Thickness(0, 10, 0, 0),
                    FontWeight = FontWeights.SemiBold,
                });
                var body = new StackPanel { Spacing = 2 };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                line.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
                line.Children.Add(new TextBlock { Text = Leaf(newParent) + " \\", VerticalAlignment = VerticalAlignment.Center });
                newName = new TextBox { Text = Path.GetFileName(nf.Path), MinWidth = 180, Padding = new Thickness(6, 2, 6, 2) };
                line.Children.Add(newName);
                body.Children.Add(line);
                body.Children.Add(new TextBlock { Text = nf.Reason + "　" + newParent, FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
                var radio = AddRadio(body, () => Path.Combine(newParent, newName.Text.Trim()));
                newName.GotFocus += (_, _) => radio.IsChecked = true;
                if (nothingFits) radio.IsChecked = true;
            }

            var browse = new HyperlinkButton { Content = "別の場所を選ぶ…", Margin = new Thickness(0, 6, 0, 0) };
            browse.Click += async (_, _) =>
            {
                string? picked = await PickFolderAsync();
                if (picked == null) return;
                var radio = AddRadio(CandidateBody(new FolderSuggestion(picked, 0, Confidence.Medium, "手動で選択")), () => picked, options.Children.IndexOf(browse));
                radio.IsChecked = true;
            };
            options.Children.Add(browse);

            if (radios.Count > 0 && radios.All(x => x.Radio.IsChecked != true)) radios[0].Radio.IsChecked = true;
            if (r.Candidates.Count == 0 && r.NewFolder == null)
                options.Children.Insert(0, new TextBlock { Text = "候補が見つかりませんでした。", Foreground = Secondary });
        }

        dialog.Opened += async (_, _) =>
        {
            try
            {
                result ??= await SuggestionService.Instance.SuggestAsync(first);
                Render(result);
            }
            catch (Exception ex)
            {
                options.Children.Clear();
                options.Children.Add(new TextBlock { Text = "提案を取得できませんでした: " + ex.Message, TextWrapping = TextWrapping.Wrap });
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var chosen = radios.FirstOrDefault(x => x.Radio.IsChecked == true);
        if (chosen.Radio == null) return null;
        string target = chosen.Target();
        if (string.IsNullOrWhiteSpace(Path.GetFileName(target)) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        return (target, result);
    }

    static UIElement CandidateBody(FolderSuggestion c)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(ConfidenceBadge(c.Confidence));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Leaf(c.Path), FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = c.Path, FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = c.Reason, FontSize = 12, Foreground = Secondary });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        ToolTipService.SetToolTip(grid, c.Path);
        return grid;
    }

    public static Border ConfidenceBadge(Confidence confidence)
    {
        string key = confidence switch
        {
            Confidence.High => "SystemFillColorSuccessBrush",
            Confidence.Medium => "SystemFillColorCautionBrush",
            _ => "SystemFillColorNeutralBrush",
        };
        return new Border
        {
            Background = (Brush)Application.Current.Resources[key],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new TextBlock
            {
                Text = confidence switch { Confidence.High => "高", Confidence.Medium => "中", _ => "低" },
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (App.Window != null) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
}
