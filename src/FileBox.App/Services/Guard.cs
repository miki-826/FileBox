using FileBox.Core.Security;

namespace FileBox.App.Services;

/// <summary>
/// FileBox 経由の書き込み操作の共通チェック（権限 → 容量）と、成功後の記録（監査・作成者）。
/// 強制は FileBox 経由の操作に限られる（02_機能設計 §6.3）。
/// </summary>
public static class Guard
{
    static PolicyService Policy => PolicyService.Instance;

    /// <summary>コピー/移動/保存の前に: 送り先への作成権限、移動なら元の削除権限、容量。</summary>
    public static async Task<bool> TransferAsync(IReadOnlyList<string> sources, string destination, bool move, string action)
    {
        if (!Policy.Demand(Permission.Create, destination, action)) return false;
        if (move && sources.Any(s => !Policy.Demand(Permission.Delete, s, action))) return false;
        return await Policy.EnsureCapacityAsync(sources, destination, move, action);
    }

    /// <summary>コピー/移動の後に: 監査ログと作成者の記録。</summary>
    public static void Transferred(IReadOnlyList<string> sources, string destination, bool move, string action)
    {
        foreach (var s in sources)
            Policy.Audit(action, s, Path.Combine(destination, Path.GetFileName(s.TrimEnd('\\'))), null);
        if (!move) Policy.RecordOwners(sources.Select(s => Path.Combine(destination, Path.GetFileName(s.TrimEnd('\\')))).ToList());
    }

    public static bool Can(Permission permission, string path, string action) => Policy.Demand(permission, path, action);

    public static void Done(string action, string? src, string? dst = null, string? detail = null) => Policy.Audit(action, src, dst, null, "ok", detail);

    /// <summary>新しく作ったファイル・フォルダ（作成者を記録）。</summary>
    public static void Created(string action, string path)
    {
        Policy.Audit(action, null, path, null);
        Policy.RecordOwners(new[] { path });
    }
}
