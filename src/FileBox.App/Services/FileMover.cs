using FileBox.Core.Files;

namespace FileBox.App.Services;

/// <summary>
/// 確認画面を出さない移動（保存先の提案・受け取りボックス用）。同名があれば「名前 (2).ext」にずらす。
/// 権限の確認・監査・元に戻すための記録は FileOperations が行う。
/// </summary>
public static class FileMover
{
    /// <summary>files を targetFolder へ移動する（フォルダが無ければ作成）。失敗した項目は errors に入る。</summary>
    public static Task<(FileOperation Operation, string? CreatedFolder, List<string> Errors)> MoveAsync(IReadOnlyList<string> files, string targetFolder) =>
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
                    string dest = UniqueName.Path(Path.Combine(targetFolder, Path.GetFileName(src.TrimEnd('\\'))));
                    Move(src, dest);
                    moves.Add((src, dest));
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(src)}: {FriendlyError.Explain(ex)}");
                    AppLog.Error($"移動できませんでした: {src}", ex);
                }
            }
            return (new FileOperation(OperationKind.Move, moves), created, errors);
        });

    /// <summary>1 件を移動する（別のドライブのフォルダはコピー＋削除になる）。</summary>
    public static void Move(string src, string dest)
    {
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
    }
}
