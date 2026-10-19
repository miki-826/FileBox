using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileBox.App.Services;

/// <summary>
/// 「不具合を報告」: ログと環境の情報を ZIP にしてデスクトップに保存し、GitHub の報告ページを開く（自動では送らない）。
/// ログの中のユーザー名（C:\Users\名前）は伏せる。ファイルの中身・データベースは含めない。
/// </summary>
public static partial class BugReport
{
    const string IssueUrl = "https://github.com/miki-826/FileBox/issues/new";

    public static async Task<string?> CreateAsync()
    {
        try
        {
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            string zipPath = Core.Files.UniqueName.Path(Path.Combine(desktop, $"FileBox-報告-{DateTime.Now:yyyyMMdd-HHmm}.zip"));
            string environment = Describe();   // 画面の倍率を読むので UI スレッドで
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
                Add(zip, "環境.txt", environment);
                foreach (var log in new[] { AppLog.FilePath, AppLog.FilePath + ".1", IndexerLog(), IndexerLog() + ".1" })
                    if (File.Exists(log)) Add(zip, "logs/" + Path.GetFileName(Path.GetDirectoryName(log)) + "-" + Path.GetFileName(log), Mask(ReadShared(log)));
            });
            Process.Start(new ProcessStartInfo(IssueUrl) { UseShellExecute = true });
            return zipPath;
        }
        catch (Exception ex)
        {
            App.ShowError("報告用のファイルを作れませんでした", ex);
            return null;
        }
    }

    static string IndexerLog() => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "FileBox", "logs", "indexer.log");

    static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FileBox: {typeof(BugReport).Assembly.GetName().Version}（{File.GetLastWriteTime(System.Environment.ProcessPath ?? "")} にビルド）");
        sb.AppendLine($"Windows: {System.Environment.OSVersion.VersionString}（{(System.Environment.Is64BitOperatingSystem ? "64" : "32")} ビット）");
        sb.AppendLine($".NET: {System.Environment.Version}");
        sb.AppendLine($"表示言語: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"画面の倍率: {App.Window?.Content?.XamlRoot?.RasterizationScale:0.##}");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            sb.AppendLine($"ドライブ {d.Name} {d.DriveFormat} {d.DriveType} 空き {Models.Format.HumanSize(d.TotalFreeSpace)} / {Models.Format.HumanSize(d.TotalSize)}");
        return sb.ToString();
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>ユーザー名を伏せる（C:\Users\yamada\… → C:\Users\***\…）。</summary>
    public static string Mask(string text) => UserFolderRegex().Replace(text, m => m.Groups[1].Value + "***");

    static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(true));
        w.Write(text);
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\\r\n""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolderRegex();
}
