namespace FileBox.Core.Files;

/// <summary>記録する操作の種類。Recycle と Restore、CreateFolder と RemoveFolder は互いに逆の操作。</summary>
public enum OperationKind { Move, Rename, Copy, Recycle, Restore, CreateFolder, RemoveFolder }

/// <summary>
/// 1 回分のファイル操作。Items は (操作前のパス, 操作後のパス)。
/// Copy は (コピー元, 作ったコピー)、Recycle / Restore は (対象のパス, 同じパス)、フォルダの作成・削除は (フォルダ, 同じパス)。
/// </summary>
public sealed record FileOperation(OperationKind Kind, IReadOnlyList<(string From, string To)> Items)
{
    /// <summary>この操作を打ち消す操作。</summary>
    public FileOperation Inverse() => Kind switch
    {
        OperationKind.Move or OperationKind.Rename => this with { Items = Items.Select(i => (i.To, i.From)).Reverse().ToList() },
        OperationKind.Copy => new(OperationKind.Recycle, Items.Select(i => (i.To, i.To)).ToList()),
        OperationKind.Recycle => new(OperationKind.Restore, Items),
        OperationKind.Restore => new(OperationKind.Recycle, Items),
        OperationKind.CreateFolder => new(OperationKind.RemoveFolder, Items),
        OperationKind.RemoveFolder => new(OperationKind.CreateFolder, Items),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    /// <summary>通知用の短い説明（例:「3 件の移動」「“報告書.docx” の名前の変更」）。</summary>
    public string Describe()
    {
        string what = Kind switch
        {
            OperationKind.Move => "移動",
            OperationKind.Rename => "名前の変更",
            OperationKind.Copy => "コピー",
            OperationKind.Recycle => "ごみ箱への移動",
            OperationKind.Restore => "ごみ箱からの復元",
            OperationKind.CreateFolder => "フォルダーの作成",
            _ => "フォルダーの削除",
        };
        return Items.Count == 1 ? $"「{System.IO.Path.GetFileName(Items[0].From.TrimEnd('\\'))}」の{what}" : $"{Items.Count} 件の{what}";
    }
}

/// <summary>
/// 元に戻す／やり直すための操作の記録（新しいものから最大 Capacity 件）。
/// 実際のファイル操作は呼び出し側の execute が行い、実際に行えた操作（名前がずれた・一部失敗した場合はそれを反映したもの）を返す。
/// </summary>
public sealed class OperationJournal(int capacity = 50)
{
    readonly LinkedList<FileOperation> _undo = new();
    readonly Stack<FileOperation> _redo = new();
    readonly object _lock = new();

    public int Capacity { get; } = capacity;

    /// <summary>記録が変わった（ボタンの有効・無効の更新用）。</summary>
    public event Action? Changed;

    public bool CanUndo { get { lock (_lock) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_lock) return _redo.Count > 0; } }
    public FileOperation? NextUndo { get { lock (_lock) return _undo.First?.Value; } }
    public FileOperation? NextRedo { get { lock (_lock) return _redo.Count > 0 ? _redo.Peek() : null; } }

    /// <summary>新しい操作を記録する（やり直しの記録は消える）。</summary>
    public void Record(FileOperation op)
    {
        if (op.Items.Count == 0) return;
        lock (_lock)
        {
            Push(op);
            _redo.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>直前の操作を打ち消す。execute が null を返した（何もできなかった）ときは記録を戻さず捨てる。</summary>
    public async Task<FileOperation?> UndoAsync(Func<FileOperation, Task<FileOperation?>> execute)
    {
        FileOperation? op;
        lock (_lock)
        {
            op = _undo.First?.Value;
            if (op == null) return null;
            _undo.RemoveFirst();
        }
        var done = await execute(op.Inverse()).ConfigureAwait(false);
        lock (_lock) if (done is { Items.Count: > 0 }) _redo.Push(done);
        Changed?.Invoke();
        return done;
    }

    /// <summary>元に戻した操作をもう一度行う。</summary>
    public async Task<FileOperation?> RedoAsync(Func<FileOperation, Task<FileOperation?>> execute)
    {
        FileOperation? op;
        lock (_lock)
        {
            if (_redo.Count == 0) return null;
            op = _redo.Pop();
        }
        var done = await execute(op.Inverse()).ConfigureAwait(false);
        lock (_lock) if (done is { Items.Count: > 0 }) Push(done);
        Changed?.Invoke();
        return done;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _undo.Clear();
            _redo.Clear();
        }
        Changed?.Invoke();
    }

    void Push(FileOperation op)
    {
        _undo.AddFirst(op);
        while (_undo.Count > Capacity) _undo.RemoveLast();
    }
}
