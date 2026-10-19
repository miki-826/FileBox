using System.Runtime.InteropServices;
using System.Security.Principal;
using FileBox.Core.Files;

namespace FileBox.App.Services;

/// <summary>ごみ箱の1項目。DataPath が実体（$R…）、InfoPath が元の場所などの情報（$I…）。</summary>
public sealed record RecycleItem(string InfoPath, string DataPath, string OriginalPath, long Size, DateTime DeletedAt, bool IsDirectory)
{
    public string Name => Path.GetFileName(OriginalPath.TrimEnd('\\'));
    public string OriginalFolder => Path.GetDirectoryName(OriginalPath) ?? "";
}

/// <summary>
/// ごみ箱を FileBox の中で見る・元に戻す・完全に削除する（エクスプローラーを開かない）。
/// 各ドライブの $Recycle.Bin\（自分の SID）にある $I / $R ファイルを直接扱う。Windows のごみ箱と同じ実体なので、
/// ここで戻した・消したものはエクスプローラーのごみ箱にも反映される。
/// </summary>
public static class RecycleBin
{
    public static List<RecycleItem> List()
    {
        var result = new List<RecycleItem>();
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return result;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var dir = new DirectoryInfo(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid));
            try
            {
                if (!dir.Exists) continue;
                foreach (var info in dir.EnumerateFiles("$I*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    RecycledEntry? entry;
                    try { entry = RecycleBinInfo.Parse(File.ReadAllBytes(info.FullName)); }
                    catch (IOException) { continue; }
                    if (entry == null) continue;
                    string data = Path.Combine(dir.FullName, RecycleBinInfo.DataName(info.Name));
                    bool isDir = Directory.Exists(data);
                    if (!isDir && !File.Exists(data)) continue;
                    result.Add(new RecycleItem(info.FullName, data, entry.OriginalPath, entry.Size, entry.DeletedAt, isDir));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result.OrderByDescending(r => r.DeletedAt).ToList();
    }

    /// <summary>元の場所に戻す。同じ名前があれば「(2)」を付ける。戻した先のパスを返す。</summary>
    public static string Restore(RecycleItem item)
    {
        string folder = item.OriginalFolder;
        if (folder.Length > 0) Directory.CreateDirectory(folder);
        string target = File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath) ? FileBox.Core.Files.UniqueName.Path(item.OriginalPath) : item.OriginalPath;
        if (item.IsDirectory) Directory.Move(item.DataPath, target);
        else File.Move(item.DataPath, target);
        TryDelete(item.InfoPath);
        Notify();
        return target;
    }

    /// <summary>完全に削除する（元に戻せない）。</summary>
    public static void Delete(RecycleItem item)
    {
        if (item.IsDirectory) Directory.Delete(item.DataPath, recursive: true);
        else
        {
            File.SetAttributes(item.DataPath, FileAttributes.Normal);
            File.Delete(item.DataPath);
        }
        TryDelete(item.InfoPath);
        Notify();
    }

    /// <summary>ごみ箱を空にする（Windows の確認画面が出る）。</summary>
    public static void Empty(IntPtr hwnd) => SHEmptyRecycleBin(hwnd, null, 0);

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // エクスプローラー・デスクトップのごみ箱アイコンに変化を知らせる
    static void Notify() => SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);

    const int SHCNE_UPDATEDIR = 0x00001000;
    const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
