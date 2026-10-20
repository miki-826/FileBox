using FileBox.Core.Files;
using FileBox.Core.Security;
using FileBox.Data;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Services;

/// <summary>
/// FileBox 経由の書き込み（名前の変更・コピー・移動・ごみ箱へ・フォルダの作成）の窓口。
/// どれも Guard で権限・容量を確かめてから行い、監査ログに残し、OperationJournal に記録して Ctrl+Z で戻せるようにする。
/// 元に戻す・やり直すも同じ確認と記録を通す。
/// </summary>
public sealed class FileOperations
{
    public static FileOperations Instance { get; } = new();

    UserStore? _store;

    public OperationJournal Journal { get; } = new();

    public static void Initialize(UserStore store) => Instance._store = store;

    // ================================================================ 操作

    /// <summary>名前を変える。変えた後のパス（失敗・権限なしなら null）。</summary>
    public string? Rename(string path, string newName)
    {
        if (!Guard.Can(Permission.Modify, path, "rename")) return null;
        try
        {
            string target = ShellService.Rename(path, newName);
            Guard.Done("rename", path, target);
            Followed(path, target);
            Journal.Record(new FileOperation(OperationKind.Rename, new[] { (path.TrimEnd('\\'), target) }));
            return target;
        }
        catch (Exception ex)
        {
            App.ShowError("名前を変更できませんでした", ex, path);
            return null;
        }
    }

    /// <summary>
    /// 一括の名前変更。入れ替え（a→b, b→a）でも重ならないよう、いったん一時名にしてから付け直す。1 回の操作として記録する。
    /// </summary>
    public async Task<(int Done, List<string> Errors)> RenameManyAsync(IReadOnlyList<RenamePlan> plans)
    {
        var targets = plans.Where(p => p.Changes).ToList();
        if (targets.Any(p => !Guard.Can(Permission.Modify, p.Path, "rename"))) return (0, new());
        var errors = new List<string>();
        var done = await Task.Run(() =>
        {
            var staged = new List<(RenamePlan Plan, string Temp)>();
            foreach (var p in targets)
            {
                try
                {
                    string temp = Path.Combine(Path.GetDirectoryName(p.Path)!, $".filebox-rename-{Guid.NewGuid():N}");
                    FileMover.Move(p.Path, temp);
                    staged.Add((p, temp));
                }
                catch (Exception ex) { errors.Add($"{p.OldName}: {FriendlyError.Explain(ex)}"); }
            }
            var result = new List<(string, string)>();
            foreach (var (p, temp) in staged)
            {
                try
                {
                    FileMover.Move(temp, p.NewPath);
                    result.Add((p.Path, p.NewPath));
                }
                catch (Exception ex)
                {
                    errors.Add($"{p.OldName}: {FriendlyError.Explain(ex)}");
                    try { FileMover.Move(temp, p.Path); } catch (Exception back) { AppLog.Error($"一時名から戻せませんでした: {temp}", back); }
                }
            }
            return result;
        });
        foreach (var (from, to) in done)
        {
            Guard.Done("rename", from, to, "一括の名前変更");
            Followed(from, to);
        }
        Journal.Record(new FileOperation(OperationKind.Rename, done));
        return (done.Count, errors);
    }

    /// <summary>新しいフォルダーを作る。作ったパス（権限なし・失敗なら null）。</summary>
    public string? CreateFolder(string parent, string name = "新しいフォルダー")
    {
        if (!Guard.Can(Permission.Create, parent, "save")) return null;
        try
        {
            string path = ShellService.CreateFolder(parent, name);
            Guard.Created("save", path);
            Journal.Record(new FileOperation(OperationKind.CreateFolder, new[] { (path, path) }));
            return path;
        }
        catch (Exception ex)
        {
            App.ShowError("フォルダーを作成できませんでした", ex, parent);
            return null;
        }
    }

    /// <summary>コピー・移動（Windows の進捗・中止・上書き確認の画面付き）。終わったら件数と「元に戻す」を通知する。</summary>
    public async Task<FileOperation?> TransferAsync(IReadOnlyList<string> paths, string destination, bool move, bool notify = true)
    {
        string action = move ? "move" : "copy";
        if (paths.Count == 0 || !await Guard.TransferAsync(paths, destination, move, action)) return null;
        var done = await Task.Run(() => ShellService.Transfer(paths, destination, move));
        if (done.Count == 0) return null;
        foreach (var (from, to) in done) Guard.Done(action, from, to);
        if (!move) PolicyService.Instance.RecordOwners(done.Select(d => d.To).ToList());
        else foreach (var (from, to) in done) Followed(from, to);
        var op = new FileOperation(move ? OperationKind.Move : OperationKind.Copy, done);
        Journal.Record(op);
        if (notify) NotifyDone(op, $"「{Leaf(destination)}」へ");
        return op;
    }

    /// <summary>確認画面を出さずに移動する（保存先の提案・受け取りボックス）。</summary>
    public async Task<(FileOperation? Operation, List<string> Errors)> MoveQuietAsync(IReadOnlyList<string> files, string targetFolder)
    {
        if (files.Count == 0 || !await Guard.TransferAsync(files, targetFolder, move: true, "move")) return (null, new());
        var (op, created, errors) = await FileMover.MoveAsync(files, targetFolder);
        if (created != null) Guard.Created("save", created);
        foreach (var (from, to) in op.Items)
        {
            Guard.Done("move", from, to);
            Followed(from, to);
        }
        if (op.Items.Count == 0) return (null, errors);
        Journal.Record(op);
        return (op, errors);
    }

    /// <summary>ごみ箱へ移動する。ごみ箱へ移った件数。</summary>
    public int Recycle(IReadOnlyList<string> paths, bool notify = true)
    {
        if (paths.Count == 0 || paths.Any(p => !Guard.Can(Permission.Delete, p, "delete"))) return 0;
        var done = ShellService.Recycle(paths);
        foreach (var p in done) Guard.Done("delete", p, detail: "ごみ箱へ");
        if (done.Count == 0) return 0;
        var op = new FileOperation(OperationKind.Recycle, done.Select(p => (p, p)).ToList());
        Journal.Record(op);
        if (notify) NotifyDone(op, "");
        return done.Count;
    }

    void NotifyDone(FileOperation op, string where)
    {
        string what = op.Kind switch
        {
            OperationKind.Move => $"{op.Items.Count} 件を{where}移動しました",
            OperationKind.Copy => $"{op.Items.Count} 件を{where}コピーしました",
            OperationKind.Recycle => $"{op.Items.Count} 件をごみ箱へ移動しました",
            _ => op.Describe(),
        };
        App.Window?.ShowInfo(what, InfoBarSeverity.Success, "元に戻す (Ctrl+Z)", () => _ = UndoAsync());
    }

    // ================================================================ 元に戻す・やり直す

    public async Task UndoAsync()
    {
        var next = Journal.NextUndo;
        if (next == null) { App.Window?.ShowInfo("元に戻せる操作はありません"); return; }
        var done = await Journal.UndoAsync(Execute);
        Report(done, next, "元に戻しました", "元に戻せませんでした");
    }

    public async Task RedoAsync()
    {
        var next = Journal.NextRedo;
        if (next == null) { App.Window?.ShowInfo("やり直せる操作はありません"); return; }
        var done = await Journal.RedoAsync(Execute);
        Report(done, next, "やり直しました", "やり直せませんでした");
    }

    void Report(FileOperation? done, FileOperation requested, string ok, string failed)
    {
        var window = App.Window;
        if (window == null) return;
        if (done == null) window.ShowInfo($"{failed}（{requested.Describe()}）", InfoBarSeverity.Warning);
        else if (done.Items.Count < requested.Items.Count)
            window.ShowInfo($"{requested.Items.Count} 件のうち {done.Items.Count} 件を{ok}。残りは見つからないか、変更できませんでした", InfoBarSeverity.Warning);
        else window.ShowInfo($"{done.Describe()}を{ok}");
    }

    /// <summary>元に戻す／やり直すの実行。実際に行えた分を返す（何もできなければ null）。</summary>
    async Task<FileOperation?> Execute(FileOperation op)
    {
        var errors = new List<string>();
        var done = new List<(string From, string To)>();
        switch (op.Kind)
        {
            case OperationKind.Move:
            case OperationKind.Rename:
                foreach (var (from, to) in op.Items)
                {
                    if (!File.Exists(from) && !Directory.Exists(from)) continue;
                    string? parent = Path.GetDirectoryName(to);
                    if (!Guard.Can(Permission.Delete, from, "move") || (parent != null && !Guard.Can(Permission.Create, parent, "move"))) continue;
                    try
                    {
                        string target = await Task.Run(() =>
                        {
                            if (parent != null) Directory.CreateDirectory(parent);
                            // 名前の変更を戻すときに大文字小文字だけ違う場合は、同じものなのでずらさない
                            string t = string.Equals(from, to, StringComparison.OrdinalIgnoreCase) ? to : UniqueName.Path(to);
                            if (op.Kind == OperationKind.Rename) ShellService.Rename(from, Path.GetFileName(t));
                            else FileMover.Move(from, t);
                            return t;
                        });
                        Guard.Done(op.Kind == OperationKind.Rename ? "rename" : "move", from, target, "元に戻す・やり直し");
                        Followed(from, target);
                        done.Add((from, target));
                    }
                    catch (Exception ex) { errors.Add(Error(from, ex)); }
                }
                break;

            case OperationKind.Recycle:
                var existing = op.Items.Select(i => i.From).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (existing.Any(p => !Guard.Can(Permission.Delete, p, "delete"))) break;
                foreach (var p in await Task.Run(() => ShellService.Recycle(existing)))
                {
                    Guard.Done("delete", p, detail: "ごみ箱へ（元に戻す・やり直し）");
                    done.Add((p, p));
                }
                break;

            case OperationKind.Restore:
                var bin = await Task.Run(RecycleBin.List);
                foreach (var (path, _) in op.Items)
                {
                    var item = bin.Where(b => string.Equals(b.OriginalPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(b => b.DeletedAt).FirstOrDefault();
                    if (item == null || !Guard.Can(Permission.Create, item.OriginalFolder, "restore")) continue;
                    try
                    {
                        string restored = await Task.Run(() => RecycleBin.Restore(item));
                        Guard.Done("restore", item.OriginalPath, restored, "ごみ箱から復元");
                        done.Add((restored, restored));
                    }
                    catch (Exception ex) { errors.Add(Error(path, ex)); }
                }
                break;

            case OperationKind.RemoveFolder:
                foreach (var (path, _) in op.Items)
                {
                    try
                    {
                        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any() || !Guard.Can(Permission.Delete, path, "delete")) continue;
                        Directory.Delete(path);
                        Guard.Done("delete", path, detail: "空のフォルダー（元に戻す）");
                        done.Add((path, path));
                    }
                    catch (Exception ex) { errors.Add(Error(path, ex)); }
                }
                break;

            case OperationKind.CreateFolder:
                foreach (var (path, _) in op.Items)
                {
                    string? parent = Path.GetDirectoryName(path);
                    if (parent == null || !Guard.Can(Permission.Create, parent, "save")) continue;
                    try
                    {
                        Directory.CreateDirectory(path);
                        Guard.Created("save", path);
                        done.Add((path, path));
                    }
                    catch (Exception ex) { errors.Add(Error(path, ex)); }
                }
                break;
        }
        if (errors.Count > 0) App.Window?.ShowInfo($"{errors.Count} 件を処理できませんでした: {errors[0]}", InfoBarSeverity.Warning);
        return done.Count == 0 ? null : new FileOperation(op.Kind, done);
    }

    static string Error(string path, Exception ex)
    {
        AppLog.Error($"元に戻す・やり直しに失敗: {path}", ex);
        return $"{Leaf(path)}: {FriendlyError.Explain(ex)}";
    }

    /// <summary>名前・場所が変わったものに付けたタグ・星・お気に入りを付け替える。</summary>
    void Followed(string from, string to)
    {
        try
        {
            _store?.RenamePath(from, to);
            UserMarks.Reload();
        }
        catch (Exception ex) { AppLog.Error("タグ・お気に入りのパスを更新できませんでした", ex); }
    }

    static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
}
