namespace FileBox.App.Services;

/// <summary>移動1回分（元に戻す用）。</summary>
public sealed record MoveOperation(IReadOnlyList<(string From, string To)> Moves, string? CreatedFolder);

/// <summary>
/// FileBox 経由の移動。同名があれば「名前 (2).ext」にずらし、直前の操作を元に戻せるようにする。
/// </summary>
public static class FileMover
{
    static MoveOperation? _last;

    public static MoveOperation? LastOperation => _last;

    /// <summary>files を targetFolder へ移動する（フォルダが無ければ作成）。失敗した項目は errors に入る。</summary>
    public static Task<(MoveOperation Operation, List<string> Errors)> MoveAsync(IReadOnlyList<string> files, string targetFolder) =>
        Task.Run(() =>
        {
            var moves = new List<(string, string)>();
            var errors = new List<string>();
            string? created = null;
            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
                created = targetFolder;
            }
            foreach (var src in files)
            {
                try
                {
                    string dest = UniquePath(Path.Combine(targetFolder, Path.GetFileName(src.TrimEnd('\\'))));
                    if (Directory.Exists(src))
                    {
                        if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
                            Directory.Move(src, dest);
                        else
                            Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(src, dest);
                    }
                    else
                    {
                        File.Move(src, dest);
                    }
                    moves.Add((src, dest));
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
                }
            }
            var op = new MoveOperation(moves, created);
            if (moves.Count > 0) _last = op;
            return (op, errors);
        });

    /// <summary>直前の移動を元に戻す。作成したフォルダは空なら削除する。</summary>
    public static Task<int> UndoAsync(MoveOperation op) =>
        Task.Run(() =>
        {
            int restored = 0;
            foreach (var (from, to) in op.Moves.Reverse())
            {
                try
                {
                    if (Directory.Exists(to)) Directory.Move(to, UniquePath(from));
                    else if (File.Exists(to)) File.Move(to, UniquePath(from));
                    else continue;
                    restored++;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
            if (op.CreatedFolder is { } dir)
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception) { }
            }
            if (ReferenceEquals(_last, op)) _last = null;
            return restored;
        });

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
