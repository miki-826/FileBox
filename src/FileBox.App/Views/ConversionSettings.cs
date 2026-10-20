using FileBox.App.Services;
using FileBox.Core.Tools;
using FileBox.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Views;

public static class ConversionSettings
{
    public static Expander Build(UserStore store)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Note("変換処理はローカルで行います。外部エンジンは同梱・自動ダウンロードしません。信頼できる配布元から導入したファイルを指定してください。"));
        var toggle = new ToggleSwitch { Header = "エクスプローラーに「FileBox で変換・整理」を追加", IsOn = ConversionContextMenu.Enabled(store) };
        bool changing = false;
        toggle.Toggled += (_, _) =>
        {
            if (changing) return;
            try { ConversionContextMenu.Apply(store, toggle.IsOn); }
            catch (Exception ex) { changing = true; toggle.IsOn = ConversionContextMenu.Enabled(store); changing = false; App.Window?.ShowInfo(ex.Message, InfoBarSeverity.Error); }
        };
        body.Children.Add(toggle);
        body.Children.Add(Note("既定はオフです。Windows 11 では「その他のオプションを確認」に表示します。エクスプローラーの一度の選択は 15 件まで。FileBox 内では追加選択できます。アンインストール時には登録を解除します。"));
        foreach (var (id, label, extension) in new[] { ("ffmpeg", "FFmpeg", ".exe"), ("magick", "ImageMagick 7（magick.exe）", ".exe"), ("7z", "7-Zip（7z.exe）", ".exe"), ("java", "Java（java.exe）", ".exe"), ("cfr", "CFR（逆コンパイラの JAR）", ".jar") })
        {
            var path = new TextBox { Header = label, Text = store.GetSetting("tools.engine." + id) ?? "", IsReadOnly = true, MinWidth = 340 };
            var browse = new Button { Content = "選択…" };
            browse.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(extension);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));
                if (await picker.PickSingleFileAsync() is not { } file) return;
                path.Text = file.Path; store.SetSetting("tools.engine." + id, file.Path);
            };
            var clear = new Button { Content = "解除" };
            clear.Click += (_, _) => { path.Text = ""; store.SetSetting("tools.engine." + id, ""); };
            body.Children.Add(path); body.Children.Add(ConversionDialog.Row(browse, clear));
        }
        var output = new TextBox { Header = "既定の出力先（空欄なら入力と同じ場所）", Text = store.GetSetting("tools.output") ?? "" };
        output.TextChanged += (_, _) => store.SetSetting("tools.output", output.Text.Trim());
        body.Children.Add(output);
        body.Children.Add(Note("画像・動画・音声の対応形式は導入したエンジンとコーデックに依存します。HEIC は対応ビルドが必要です。SVG は読み取りのみで、ベクターへの変換は行いません。OCR は Windows の言語パック、PDF テキスト抽出は PDF 用 IFilter を使用します。"));
        var items = new StackPanel { Spacing = 4 };
        foreach (var tool in ToolCatalog.All)
        {
            var check = new CheckBox { Content = tool.Label, IsChecked = store.GetSetting("tools.hidden." + tool.Kind) != "1" };
            check.Checked += (_, _) => store.SetSetting("tools.hidden." + tool.Kind, "0");
            check.Unchecked += (_, _) => store.SetSetting("tools.hidden." + tool.Kind, "1");
            items.Children.Add(check);
        }
        body.Children.Add(new Expander { Header = "変換メニューに表示する機能", Content = items, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        return new Expander { Header = "ファイルの変換・整理と外部エンジン", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
}
