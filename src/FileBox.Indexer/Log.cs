namespace FileBox.Indexer;

/// <summary>最小限のファイルログ（%PROGRAMDATA%\FileBox\logs\indexer.log）。</summary>
internal static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath;

    static Log()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileBox", "logs");
        try { Directory.CreateDirectory(dir); } catch { }
        FilePath = Path.Combine(dir, "indexer.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Console.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 5 * 1024 * 1024) info.Delete();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* ログ失敗は無視 */ }
        }
    }
}
