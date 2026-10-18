namespace FileBox.Core.Links;

public enum LinkKind { Open, Bookmark, Workspace, Search }

/// <summary>filebox:// リンクを解析した結果（02_機能設計 §4.1）。</summary>
public sealed record FileBoxLink(LinkKind Kind, string? Path = null, string? Select = null, string? Token = null, long WorkspaceId = 0, string? Query = null);

/// <summary>
/// filebox:// リンクの生成と、外部から渡されたリンクの厳密な検証（§4.5）。
/// 検証に通らないリンクは null（理由は error）。UNC・デバイスパス・相対パス・親参照は受け付けない。
/// </summary>
public static class FileBoxUri
{
    public const string Scheme = "filebox";

    /// <summary>開いても実行はせず、場所の表示（選択）だけにする種類（§4.5）。</summary>
    static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr",
        ".lnk", ".url", ".pif", ".hta", ".cpl", ".jar", ".reg", ".dll", ".appref-ms", ".application", ".settingcontent-ms",
    };

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(System.IO.Path.GetExtension(path));

    // ---------------------------------------------------------------- 生成

    public static string ForPath(string path, string? select = null) =>
        $"{Scheme}://open?path={Uri.EscapeDataString(path)}" + (select != null ? $"&select={Uri.EscapeDataString(select)}" : "");

    public static string ForBookmark(string token) => $"{Scheme}://bookmark/{Uri.EscapeDataString(token)}";

    public static string ForWorkspace(long id) => $"{Scheme}://workspace/{id}";

    public static string ForSearch(string query) => $"{Scheme}://search?q={Uri.EscapeDataString(query)}";

    /// <summary>Markdown のリンク表記。</summary>
    public static string Markdown(string label, string link) =>
        $"[{label.Replace("[", "\\[").Replace("]", "\\]")}]({link})";

    // ---------------------------------------------------------------- 解析

    public static bool IsFileBoxUri(string s) => s.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static FileBoxLink? Parse(string input, out string? error, bool allowUnc = false)
    {
        error = null;
        if (input.Length > 4096) { error = "リンクが長すぎます"; return null; }
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = "filebox:// のリンクではありません";
            return null;
        }
        string host = uri.Host.ToLowerInvariant();
        var query = ParseQuery(uri.Query);
        string tail = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));

        switch (host)
        {
            case "open":
                if (!query.TryGetValue("path", out var raw) || string.IsNullOrWhiteSpace(raw)) { error = "path がありません"; return null; }
                string? path = ValidatePath(raw, allowUnc, out error);
                if (path == null) return null;
                string? select = null;
                if (query.TryGetValue("select", out var sel) && sel.Length > 0)
                {
                    // select はファイル名だけ（区切り文字や親参照を含めない）
                    if (sel.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || sel is "." or ".." || sel.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    {
                        error = "select が不正です";
                        return null;
                    }
                    select = sel;
                }
                return new FileBoxLink(LinkKind.Open, Path: path, Select: select);

            case "bookmark":
                if (tail.Length is < 8 or > 64 || !tail.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { error = "ブックマークの指定が不正です"; return null; }
                return new FileBoxLink(LinkKind.Bookmark, Token: tail);

            case "workspace":
                if (!long.TryParse(tail, out long id) || id <= 0) { error = "ワークスペースの指定が不正です"; return null; }
                return new FileBoxLink(LinkKind.Workspace, WorkspaceId: id);

            case "search":
                if (!query.TryGetValue("q", out var q) || q.Length is 0 or > 1000) { error = "検索語がありません"; return null; }
                return new FileBoxLink(LinkKind.Search, Query: q);

            default:
                error = $"未対応のリンクです（{host}）";
                return null;
        }
    }

    /// <summary>ローカルの絶対パス（ドライブ文字から始まる）だけを正規化して返す。</summary>
    public static string? ValidatePath(string raw, bool allowUnc, out string? error)
    {
        error = null;
        string p = raw.Replace('/', '\\');
        if (p.IndexOf('\0') >= 0 || p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) { error = "パスに使えない文字があります"; return null; }
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\")) { error = "デバイスパスは開けません"; return null; }
        bool unc = p.StartsWith(@"\\");
        if (unc && !allowUnc) { error = "ネットワークパス（UNC）のリンクは設定で許可されていません"; return null; }
        if (!unc && !(p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')) { error = "ドライブから始まる絶対パスではありません"; return null; }
        if (p.Split('\\').Any(seg => seg == "..")) { error = "親フォルダ参照（..）を含むパスは開けません"; return null; }
        // 代替データストリーム（file.txt:stream）は不可。ドライブ指定の ':' だけ許す
        if (p.IndexOf(':', unc ? 0 : 2) >= 0) { error = "パスに ':' を含められません"; return null; }
        try
        {
            string full = System.IO.Path.GetFullPath(p);
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        catch (Exception) { error = "パスの形式が不正です"; return null; }
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            string value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            result.TryAdd(key, value);
        }
        return result;
    }
}
