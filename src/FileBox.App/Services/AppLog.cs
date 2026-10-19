namespace FileBox.App.Services;

/// <summary>
/// アプリのログ（%LOCALAPPDATA%\FileBox\logs\app.log）。エラーと主な出来事だけを書き、1MB を超えたら 1 世代だけ残して切り替える。
/// 「不具合を報告」で ZIP にまとめる。ファイルの中身は書かない。
/// </summary>
public static class AppLog
{
    const long MaxBytes = 1 << 20;
    static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileBox", "logs");
    public static string FilePath => Path.Combine(Folder, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}\n{ex.StackTrace}");

    static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
