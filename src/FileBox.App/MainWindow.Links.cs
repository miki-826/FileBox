using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Core.Links;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App;

/// <summary>filebox:// リンクを開く（02_機能設計 §4）と、リンク転送口・Win+E の起動。</summary>
public sealed partial class MainWindow
{
    LocalLinkServer? _linkServer;

    void InitializeLinks()
    {
        LinkService.Initialize(_store);
        ExplorerIntegration.Initialize(_store, onWinE: () =>
        {
            OpenInNewTab("");
            Activate();
            BringToFront();
        });
        RestartLinkServer();
    }

    /// <summary>設定に合わせてカレンダー用 http リンクの転送口を起動・停止する。</summary>
    public void RestartLinkServer()
    {
        _linkServer?.Dispose();
        _linkServer = null;
        var links = LinkService.Instance;
        if (!links.HttpLinksEnabled) return;
        _linkServer = new LocalLinkServer(links.HttpPort, links.HttpSecret, link => DispatcherQueue.TryEnqueue(() =>
        {
            HandleLink(link);
            Activate();
            BringToFront();
        }));
        if (!_linkServer.Start())
            ShowInfo($"カレンダー用リンクのポート {links.HttpPort} を使えませんでした（他のアプリが使用中）", InfoBarSeverity.Warning);
    }

    /// <summary>リンクを検証して開く。実行ファイルは開かず、場所を表示して選択するだけ（§4.5）。</summary>
    public void HandleLink(string uri)
    {
        var link = FileBoxUri.Parse(uri, out var error, LinkService.Instance.AllowUnc);
        if (link == null)
        {
            ShowInfo("リンクを開けません: " + error, InfoBarSeverity.Warning);
            return;
        }
        switch (link.Kind)
        {
            case LinkKind.Open:
                OpenLinkedPath(link.Select != null ? Path.Combine(link.Path!, link.Select) : link.Path!, found: true);
                break;
            case LinkKind.Bookmark:
                if (LinkService.Instance.ResolveBookmark(link.Token!) is not { } resolved)
                {
                    ShowInfo("このパソコンでは登録されていないリンクです（別のパソコンで作られた可能性）", InfoBarSeverity.Warning);
                    return;
                }
                OpenLinkedPath(resolved.Path, resolved.Found);
                break;
            case LinkKind.Workspace:
                if (_store.GetWorkspace(link.WorkspaceId) is { } ws) LoadLayout(ws.LayoutJson, replace: false);
                else ShowInfo("ワークスペースが見つかりません", InfoBarSeverity.Warning);
                break;
            case LinkKind.Search:
                SearchBox.Text = link.Query!;
                AddTab(new TabViewModel(new SearchPaneViewModel(link.Query!)));
                break;
        }
    }

    void OpenLinkedPath(string path, bool found)
    {
        if (!Guard.Can(Core.Security.Permission.Read, path, "link_open")) return;
        Guard.Done("link_open", path);
        if (found && (Directory.Exists(path) || File.Exists(path)))
        {
            // フォルダはそのまま、ファイルは親フォルダを開いて選択（ファイル自体は起動しない）
            OpenInNewTab(path);
            return;
        }
        // 見つからなければ、残っている一番近い親フォルダを開く
        string? parent = Path.GetDirectoryName(path);
        while (parent != null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
        ShowInfo($"「{Path.GetFileName(path.TrimEnd('\\'))}」は移動または削除された可能性があります", InfoBarSeverity.Warning);
        if (parent != null) OpenInNewTab(parent);
    }

    void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
