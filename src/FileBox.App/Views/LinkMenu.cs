using FileBox.App.Services;
using FileBox.Core.Links;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Views;

/// <summary>右クリックの「リンクをコピー」（02_機能設計 §4.4）。予定表の本文などに貼って、そこから FileBox で開く。</summary>
public static class LinkMenu
{
    public static MenuFlyoutSubItem Build(string path)
    {
        var sub = new MenuFlyoutSubItem { Text = "リンクをコピー", Icon = new FontIcon { Glyph = "" } };
        string label = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        var links = LinkService.Instance;

        void Add(string text, Func<string> make, Func<string, string>? format = null)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) =>
            {
                if (!Guard.Can(Core.Security.Permission.Share, path, "link_create")) return;
                try
                {
                    string url = make();
                    ShellService.CopyLink(format?.Invoke(url) ?? url, url, label);
                    App.Window?.ShowInfo("リンクをコピーしました。予定やメモに貼り付けると、そこから FileBox で開けます");
                }
                catch (Exception ex) { App.ShowError("リンクを作れませんでした", ex); }
            };
            sub.Items.Add(mi);
        }

        Add("リンク（移動・名前変更しても切れない）", () => links.CreateBookmarkLink(path));
        Add("Markdown 形式", () => links.CreateBookmarkLink(path), url => FileBoxUri.Markdown(label, url));
        Add("パスを含むリンク", () => FileBoxUri.ForPath(path));
        sub.Items.Add(new MenuFlyoutSeparator());
        if (links.HttpLinksEnabled)
        {
            Add("カレンダー用（http）", () => links.ToHttp(links.CreateBookmarkLink(path)));
        }
        else
        {
            var enable = new MenuFlyoutItem { Text = "カレンダー用（http）リンクを使う…" };
            enable.Click += async (_, _) => { if (App.Window is { } w) await SettingsDialog.ShowAsync(w.Content.XamlRoot); };
            sub.Items.Add(enable);
        }
        return sub;
    }
}
