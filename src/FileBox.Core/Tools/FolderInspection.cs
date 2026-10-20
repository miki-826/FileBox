using System.Text;

namespace FileBox.Core.Tools;

public sealed record EmptyFolderReport(IReadOnlyList<string> Paths, IReadOnlyList<string> Unreadable);

public static class FolderInspection
{
    public static EmptyFolderReport EmptyFolders(string root, CancellationToken token, Func<string, bool>? canRead = null)
    {
        var empty = new List<string>(); var unreadable = new List<string>();
        void Visit(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 100) { unreadable.Add(path); return; }
            try
            {
                if (!(canRead?.Invoke(path) ?? true)) { unreadable.Add(path); return; }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                var children = Directory.GetFileSystemEntries(path);
                // 検出時に実際に空である場所だけ。親まで空になると推定して削除候補へ入れない。
                if (children.Length == 0 && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) empty.Add(path);
                foreach (string child in children)
                    if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) Visit(child, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add(path); }
        }
        Visit(Path.GetFullPath(root), 0);
        return new(empty, unreadable);
    }

    public static string Tree(string root, int maxDepth, CancellationToken token, Func<string, bool>? canRead = null, string? excludePath = null)
    {
        if (maxDepth is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var result = new StringBuilder(); int count = 0;
        result.AppendLine(Path.GetFullPath(root));
        void Visit(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > maxDepth || count >= 10000) return;
            try
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (excludePath != null && string.Equals(Path.GetFullPath(child), Path.GetFullPath(excludePath), StringComparison.OrdinalIgnoreCase)) continue;
                    if (++count > 10000) { result.AppendLine("[最大 10000 項目で打ち切りました]"); return; }
                    var attributes = File.GetAttributes(child);
                    result.Append(' ', depth * 2).AppendLine(Path.GetFileName(child) + ((attributes & FileAttributes.Directory) != 0 ? "/" : ""));
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory && (canRead?.Invoke(child) ?? true)) Visit(child, depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Append(' ', depth * 2).AppendLine("[読み取り不可]"); }
        }
        Visit(root, 1);
        return result.ToString();
    }
}
