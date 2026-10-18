using System.Security.Cryptography;
using FileBox.Core.Links;
using FileBox.Data;
using Microsoft.Win32;

namespace FileBox.App.Services;

/// <summary>
/// filebox:// リンク（02_機能設計 §4）。URI スキームの登録（HKCU のみ）、ブックマークの発行と追跡、
/// カレンダー用の http リンク生成を受け持つ。リンクを開く処理は MainWindow.HandleLink。
/// </summary>
public sealed class LinkService
{
    static LinkService? _instance;
    public static LinkService Instance => _instance ?? throw new InvalidOperationException("LinkService.Initialize が未実行");

    readonly UserStore _store;

    LinkService(UserStore store) => _store = store;

    public static void Initialize(UserStore store)
    {
        if (_instance != null) return;
        _instance = new LinkService(store);
        try { RegisterProtocol(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public bool AllowUnc => _store.GetSetting("links.allowUnc") == "1";

    // ================================================================ 登録（§4.2）

    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileBox.exe");

    /// <summary>HKCU\Software\Classes\filebox を登録（実行ファイルの場所が変わっていれば更新）。</summary>
    public static void RegisterProtocol()
    {
        string command = $"\"{ExePath}\" \"%1\"";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\filebox");
        key.SetValue("", "URL:FileBox リンク");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
        using var cmd = key.CreateSubKey(@"shell\open\command");
        if (cmd.GetValue("") as string != command) cmd.SetValue("", command);
    }

    // ================================================================ ブックマーク（§4.3）

    /// <summary>移動・名前変更しても切れないリンク。NTFS のファイルIDが取れなければパス指定のリンク。</summary>
    public string CreateBookmarkLink(string path)
    {
        if (FileIdentity.TryGet(path) is not { } id) return FileBoxUri.ForPath(path);
        var existing = _store.FindBookmarkByFileId(id.VolumeSerial, id.Id);
        if (existing != null)
        {
            if (!string.Equals(existing.LastPath, path, StringComparison.OrdinalIgnoreCase)) _store.UpdateBookmarkPath(existing.Id, path);
            return FileBoxUri.ForBookmark(existing.Token);
        }
        string token = NewToken();
        _store.AddBookmark(token, id.VolumeSerial, id.Id, path, Path.GetFileName(path.TrimEnd('\\')));
        return FileBoxUri.ForBookmark(token);
    }

    /// <summary>ブックマークの現在の場所。見つからなければ (最終パス, false)。未知のトークンは null。</summary>
    public (string Path, bool Found)? ResolveBookmark(string token)
    {
        var b = _store.GetBookmark(token);
        if (b == null) return null;
        if (FileIdentity.TryResolve(new FileId(b.VolumeSerial, b.FileId)) is { } current)
        {
            if (!string.Equals(current, b.LastPath, StringComparison.OrdinalIgnoreCase)) _store.UpdateBookmarkPath(b.Id, current);
            return (current, true);
        }
        return (b.LastPath, Directory.Exists(b.LastPath) || File.Exists(b.LastPath));
    }

    static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ================================================================ カレンダー用 http リンク（§4.4）

    public bool HttpLinksEnabled => _store.GetSetting("links.http") == "1";
    public int HttpPort => int.TryParse(_store.GetSetting("links.httpPort"), out int p) && p is > 1024 and < 65536 ? p : 47821;

    /// <summary>http://127.0.0.1 宛てのリンク。推測されにくい秘密の文字列を含め、他のサイトから勝手に叩かれないようにする。</summary>
    public string HttpSecret
    {
        get
        {
            string? s = _store.GetSetting("links.httpSecret");
            if (s is { Length: >= 16 }) return s;
            s = NewToken() + NewToken();
            _store.SetSetting("links.httpSecret", s);
            return s;
        }
    }

    /// <summary>filebox://kind/...?query を http://127.0.0.1:port/secret/kind/...?query に変える。</summary>
    public string ToHttp(string fileboxLink)
    {
        string rest = fileboxLink[(FileBoxUri.Scheme.Length + 3)..];
        return $"http://127.0.0.1:{HttpPort}/{HttpSecret}/{rest}";
    }
}
