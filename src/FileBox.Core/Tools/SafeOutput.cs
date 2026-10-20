namespace FileBox.Core.Tools;

/// <summary>出力先内に一時作業を作り、完成したファイルだけを衝突しない名前で公開する。</summary>
public sealed class SafeOutput : IDisposable
{
    public string Destination { get; }
    public string WorkPath { get; }

    public SafeOutput(string destination)
    {
        Destination = Path.GetFullPath(destination);
        if (!Directory.Exists(Destination)) throw new DirectoryNotFoundException("出力先のフォルダがありません");
        RejectReparseParents(Destination);
        WorkPath = Path.Combine(Destination, ".FileBox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkPath);
    }

    public static void RejectReparseParents(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("出力先にリンクまたはジャンクションがあります。実際のフォルダを指定してください");
    }

    public static string EntryPath(string destination, string entry)
    {
        string name = entry.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0') ||
            name.Split('/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new IOException("圧縮ファイルに安全でないパスが含まれています");
        foreach (string component in name.TrimEnd('/').Split('/'))
        {
            if (component.Length == 0 || Files.BatchRename.Validate(component) != null)
                throw new IOException("圧縮ファイルに安全でないファイル名が含まれています");
        }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("圧縮ファイルのパスが展開先の範囲外です");
        return target;
    }

    public string PublishFile(string staged, string fileName)
    {
        if (Files.BatchRename.Validate(fileName) != null) throw new IOException("出力ファイル名が正しくありません");
        AssertOwned(staged);
        RejectReparseParents(Destination);
        for (int n = 1; n < 100000; n++)
        {
            string name = n == 1 ? fileName : Path.GetFileNameWithoutExtension(fileName) + $" ({n})" + Path.GetExtension(fileName);
            string target = Path.Combine(Destination, name);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try { File.Move(staged, target, overwrite: false); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new IOException("同名のファイルが多すぎるため保存できません");
    }

    public string PublishFolder(string staged, string folderName)
    {
        if (Files.BatchRename.Validate(folderName) != null) throw new IOException("出力フォルダ名が正しくありません");
        AssertOwned(staged);
        ValidateTree(staged);
        RejectReparseParents(Destination);
        for (int n = 1; n < 100000; n++)
        {
            string target = Path.Combine(Destination, n == 1 ? folderName : folderName + $" ({n})");
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try { Directory.Move(staged, target); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new IOException("同名のフォルダが多すぎるため保存できません");
    }

    void AssertOwned(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(WorkPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("一時作業の範囲外です");
        RejectReparseParents(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("出力にリンクが含まれています");
    }

    public static void ValidateTree(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("出力にリンクが含まれています");
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("出力にリンクが含まれています");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    public void Dispose()
    {
        // 作成した一時ディレクトリ以外は消さない。リンクを辿る再帰削除を避ける。
        try { if (Directory.Exists(WorkPath)) RemoveOwned(WorkPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void RemoveOwned(string path)
    {
        var attr = File.GetAttributes(path);
        if ((attr & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        if ((attr & FileAttributes.ReparsePoint) == 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) RemoveOwned(child);
        Directory.Delete(path, recursive: false);
    }
}
