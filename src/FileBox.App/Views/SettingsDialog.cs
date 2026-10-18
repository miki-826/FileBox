using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>設定（04_画面設計 設定）。エクスプローラー置き換え・リンク・表示。</summary>
public static class SettingsDialog
{
    public static UserStore? Store { get; set; }

    public static async Task ShowAsync(XamlRoot root)
    {
        var store = Store ?? throw new InvalidOperationException("SettingsDialog.Store が未設定");
        var explorer = ExplorerIntegration.Instance;
        var links = LinkService.Instance;
        var panel = new StackPanel { Spacing = 6, MinWidth = 520 };

        // ---- エクスプローラー置き換え（02_機能設計 §7）
        panel.Children.Add(Header("エクスプローラーの代わりに使う"));
        var replace = Toggle("フォルダやドライブを開くときに FileBox を使う", explorer.IsEnabled && explorer.IsActiveInRegistry(),
            "デスクトップやスタートメニューからフォルダを開くと FileBox が開きます。オフにすると元のエクスプローラーに戻ります（変更は現在のユーザーだけ）。");
        replace.Toggled += (_, _) =>
        {
            try
            {
                if (replace.IsOn) explorer.Enable(); else explorer.Disable();
            }
            catch (Exception ex) { App.ShowError("設定を変更できませんでした", ex); }
        };
        panel.Children.Add(replace);
        var winE = Toggle("Win + E で FileBox を開く", explorer.WinEEnabled, "FileBox が起動している間だけ有効です。");
        winE.Toggled += (_, _) => explorer.SetWinE(winE.IsOn);
        panel.Children.Add(winE);

        // ---- 保存先の提案（§1）
        panel.Children.Add(Header("保存先の提案"));
        var saveDialog = Toggle("他のアプリの「名前を付けて保存」で保存先を提案する", SaveDialogWatcher.Instance.Enabled,
            "メモ帳・Office・ブラウザなどの標準の保存画面の横に候補を表示します。選ぶとファイル名欄に場所が入ります。");
        saveDialog.Toggled += (_, _) => SaveDialogWatcher.Instance.SetEnabled(saveDialog.IsOn);
        panel.Children.Add(saveDialog);

        // ---- リンク（§4）
        panel.Children.Add(Header("リンク"));
        var http = Toggle("カレンダー用の http リンクを使う", links.HttpLinksEnabled,
            $"Google カレンダーなど filebox:// をリンクにしないアプリ向けです。このパソコンの FileBox が起動中のときだけ開けます（127.0.0.1:{links.HttpPort}）。");
        http.Toggled += (_, _) =>
        {
            store.SetSetting("links.http", http.IsOn ? "1" : "0");
            App.Window?.RestartLinkServer();
        };
        panel.Children.Add(http);
        var unc = Toggle("ネットワーク上のフォルダ（\\\\サーバー\\共有）へのリンクを開く", links.AllowUnc, "オフのときは、ネットワークパスを含むリンクを開きません。");
        unc.Toggled += (_, _) => store.SetSetting("links.allowUnc", unc.IsOn ? "1" : "0");
        panel.Children.Add(unc);

        // ---- 表示
        panel.Children.Add(Header("表示"));
        var restore = Toggle("起動時に前回のタブを復元する", store.GetSetting("restoreSession") != "0");
        restore.Toggled += (_, _) => store.SetSetting("restoreSession", restore.IsOn ? "1" : "0");
        panel.Children.Add(restore);
        var hidden = Toggle("隠しファイルを表示する（Ctrl+H）", FolderPaneViewModel.ShowHidden);
        hidden.Toggled += (_, _) =>
        {
            FolderPaneViewModel.ShowHidden = hidden.IsOn;
            store.SetSetting("showHidden", hidden.IsOn ? "1" : "0");
        };
        panel.Children.Add(hidden);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "設定",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            CloseButtonText = "閉じる",
        };
        await dialog.ShowAsync();
    }

    static ToggleSwitch Toggle(string header, bool on, string? description = null)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "オン", OffContent = "オフ" };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            head.Children.Add(new TextBlock
            {
                Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 500,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        toggle.Header = head;
        return toggle;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) };
}
