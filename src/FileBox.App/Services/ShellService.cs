using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FileBox.App.Services;

/// <summary>OS のシェル機能（開く・ごみ箱・コピー/移動・プロパティ・既知フォルダ）。</summary>
public static class ShellService
{
    public static void Open(string path)
    {
        // フォルダは明示的にエクスプローラーで開く（既定の動作を FileBox に置き換えていると自分自身に戻ってくるため）
        if (Directory.Exists(path)) { OpenInExplorer(path); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.ShowError($"開けませんでした: {path}", ex); }
    }

    /// <summary>エクスプローラーでフォルダ（または仮想フォルダ "::{CLSID}" / shell:）を開く。既定の動詞ではなく "open" を指定する。</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { App.ShowError($"エクスプローラーで開けませんでした: {path}", ex); }
    }

    public static void ShowInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    public static void ShowProperties(string path) => SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, path, null);

    /// <summary>ごみ箱へ移動（エクスプローラーと同じ確認・進捗ダイアログ）。実際にごみ箱へ移ったパスを返す。</summary>
    public static List<string> Recycle(IEnumerable<string> paths)
    {
        var done = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                if (!Directory.Exists(path) && !File.Exists(path)) done.Add(path);
            }
            catch (Exception ex) { App.ShowError($"削除できませんでした: {Path.GetFileName(path.TrimEnd('\\'))}", ex, path); }
        }
        return done;
    }

    /// <summary>名前を変える。変えた後のパスを返す。大文字小文字だけの変更もできる。</summary>
    public static string Rename(string path, string newName)
    {
        string target = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, newName);
        if (Directory.Exists(path))
        {
            // 大文字小文字だけの変更は Directory.Move が失敗するため一時名を経由する
            if (string.Equals(path.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase) && !string.Equals(path.TrimEnd('\\'), target, StringComparison.Ordinal))
            {
                string temp = target + ".filebox-" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(path, temp);
                Directory.Move(temp, target);
            }
            else Directory.Move(path, target);
        }
        else File.Move(path, target);
        return target;
    }

    public static string CreateFolder(string parent, string name = "新しいフォルダー")
    {
        string path = FileBox.Core.Files.UniqueName.Path(Path.Combine(parent, name));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>ターミナル（Windows Terminal があればそれ、無ければ PowerShell）をこのフォルダで開く。</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            string? wt = FindOnPath("wt.exe");
            // 末尾の \ が引用符を打ち消さないよう「\.」で終える
            if (wt != null) Process.Start(new ProcessStartInfo(wt, $"-d \"{folder.TrimEnd('\\')}\\.\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { UseShellExecute = true, WorkingDirectory = folder });
        }
        catch (Exception ex) { App.ShowError("ターミナルを開けませんでした", ex); }
    }

    static string? FindOnPath(string exe)
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; }
            catch (ArgumentException) { }
        }
        return null;
    }

    // ---------------------------------------------------------------- クリップボード（エクスプローラーと相互にコピー/貼り付け）

    public static async Task CopyToClipboardAsync(IReadOnlyList<string> paths, bool cut)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                items.Add(Directory.Exists(p)
                    ? await StorageFolder.GetFolderFromPathAsync(p)
                    : await StorageFile.GetFileFromPathAsync(p));
            }
            catch (Exception) { }
        }
        if (items.Count == 0) return;
        var package = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        package.SetStorageItems(items);
        package.SetText(string.Join(Environment.NewLine, paths));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>リンクをコピー。テキストに加えて HTML のリンクも入れ、Outlook・Notion 等へ貼るとそのままリンクになるようにする。</summary>
    public static void CopyLink(string text, string url, string label)
    {
        var package = new DataPackage();
        package.SetText(text);
        string html = $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">{System.Net.WebUtility.HtmlEncode(label)}</a>";
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>クリップボードのファイルを destination に貼り付ける（Windows 標準の進捗・競合ダイアログ付き）。</summary>
    public static async Task PasteAsync(string destination)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return;
        var items = await content.GetStorageItemsAsync();
        bool move = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        await Task.Run(() => Transfer(paths, destination, move));
    }

    /// <summary>クリップボードのファイルと、切り取り（移動）かどうか。</summary>
    public static async Task<(List<string> Paths, bool Move)> GetClipboardFilesAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return (new(), false);
        var items = await content.GetStorageItemsAsync();
        return (items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList(), content.RequestedOperation.HasFlag(DataPackageOperation.Move));
    }

    /// <summary>
    /// コピー/移動（Windows 標準の進捗・一時停止・中止・上書き確認ダイアログ付き）。同じフォルダへのコピーは「 - コピー」を付ける。
    /// 実際に行えた (元, 先) を返す（中止・スキップしたものは含まない）。
    /// </summary>
    public static List<(string From, string To)> Transfer(IReadOnlyList<string> paths, string destination, bool move)
    {
        var done = new List<(string, string)>();
        foreach (var path in paths)
        {
            string name = Path.GetFileName(path.TrimEnd('\\'));
            string target = Path.Combine(destination, name);
            bool same = string.Equals(Path.GetDirectoryName(path.TrimEnd('\\')), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (same && !move) target = FileBox.Core.Files.UniqueName.Copy(target);
            else if (same) continue;
            try
            {
                if (Directory.Exists(path))
                {
                    if (move) FileSystem.MoveDirectory(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                    else FileSystem.CopyDirectory(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
                else
                {
                    if (move) FileSystem.MoveFile(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                    else FileSystem.CopyFile(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
                bool arrived = File.Exists(target) || Directory.Exists(target);
                bool left = !move || (!File.Exists(path) && !Directory.Exists(path));
                if (arrived && left) done.Add((path, target));
            }
            catch (Exception ex) { App.ShowError($"{(move ? "移動" : "コピー")}できませんでした: {name}", ex, path); }
        }
        return done;
    }

    // ---------------------------------------------------------------- アドレス解決

    public static readonly (string Name, string Glyph, Guid Id)[] QuickAccess =
    {
        ("ホーム", "", new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173")),     // Profile
        ("デスクトップ", "", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        ("ダウンロード", "", new Guid("374DE290-123F-4565-9164-39C4925E467B")),
        ("ドキュメント", "", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        ("ピクチャ", "", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        ("ミュージック", "", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43")),
        ("ビデオ", "", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC")),
    };

    static readonly Dictionary<string, Guid> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Profile"] = QuickAccess[0].Id,
        ["Desktop"] = QuickAccess[1].Id,
        ["Downloads"] = QuickAccess[2].Id,
        ["Personal"] = QuickAccess[3].Id,
        ["Documents"] = QuickAccess[3].Id,
        ["My Pictures"] = QuickAccess[4].Id,
        ["Pictures"] = QuickAccess[4].Id,
        ["My Music"] = QuickAccess[5].Id,
        ["Music"] = QuickAccess[5].Id,
        ["My Video"] = QuickAccess[6].Id,
        ["Videos"] = QuickAccess[6].Id,
        ["AppData"] = new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
        ["Local AppData"] = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
        ["Startup"] = new Guid("B97D20BB-F46A-4C97-BA10-5E3608430854"),
        ["Programs"] = new Guid("A77F5D77-2E2B-44C3-A6A2-ABA601054A51"),
    };

    public static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>アドレスバー入力を実パスへ展開する（%USERPROFILE% / shell:Downloads / ~ など）。空文字は PC。</summary>
    public static string? ResolveAddress(string input)
    {
        string s = input.Trim().Trim('"');
        if (s.Length == 0 || s.Equals("PC", StringComparison.OrdinalIgnoreCase) || s.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase))
            return "";
        if (s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = s[6..];
            string? tail = null;
            int slash = rest.IndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) { tail = rest[(slash + 1)..]; rest = rest[..slash]; }
            if (!ShellNames.TryGetValue(rest, out var id)) return null;
            string? root = KnownFolder(id);
            return root == null ? null : tail == null ? root : Path.Combine(root, tail);
        }
        if (s == "~" || s.StartsWith("~\\") || s.StartsWith("~/"))
            s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..];
        s = Environment.ExpandEnvironmentVariables(s);
        if (s.Length == 2 && s[1] == ':') s += "\\";
        try { s = Path.GetFullPath(s); } catch (Exception) { return null; }
        return s;
    }

    const uint SHOP_FILEPATH = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
