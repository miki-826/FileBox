namespace FileBox.Core.Security;

/// <summary>権限ビット（03_データ設計 permissions）。</summary>
[Flags]
public enum Permission
{
    None = 0,
    Read = 1,
    Create = 2,
    Modify = 4,
    Delete = 8,
    Share = 16,   // リンク発行
    Admin = 32,
    All = Read | Create | Modify | Delete | Share | Admin,
}

/// <summary>フォルダ単位の権限設定1行。UserId か RoleId のどちらか一方。</summary>
public sealed record PermissionRule(string Path, Permission Allow, Permission Deny, long? UserId = null, long? RoleId = null);

/// <summary>
/// 実効権限の判定（02_機能設計 §6.2）。
/// 判定順は ユーザー個別設定 → ロール設定 → ロールの既定。各段では対象に最も近い（深い）フォルダの設定を使い、
/// 同じ深さで許可と拒否が両方あれば拒否を優先する。設定は配下のフォルダに継承される。
/// </summary>
public static class PolicyEvaluator
{
    public static Permission Effective(string path, long userId, long roleId, Permission roleDefault, IReadOnlyList<PermissionRule> rules)
    {
        string target = Normalize(path);
        var userRules = rules.Where(r => r.UserId == userId && Applies(r.Path, target)).ToList();
        var roleRules = rules.Where(r => r.RoleId == roleId && r.UserId == null && Applies(r.Path, target)).ToList();

        Permission result = Permission.None;
        foreach (Permission bit in new[] { Permission.Read, Permission.Create, Permission.Modify, Permission.Delete, Permission.Share, Permission.Admin })
        {
            bool? decided = Decide(userRules, bit) ?? Decide(roleRules, bit);
            if (decided ?? roleDefault.HasFlag(bit)) result |= bit;
        }
        return result;
    }

    /// <summary>最も深いフォルダの設定で決める。その深さに設定が無ければ一つ浅い設定へ。どこにも無ければ null。</summary>
    static bool? Decide(List<PermissionRule> rules, Permission bit)
    {
        foreach (var level in rules.GroupBy(r => Normalize(r.Path).Length).OrderByDescending(g => g.Key))
        {
            if (level.Any(r => r.Deny.HasFlag(bit))) return false;
            if (level.Any(r => r.Allow.HasFlag(bit))) return true;
        }
        return null;
    }

    /// <summary>rulePath が target 自身か、その祖先フォルダか。</summary>
    public static bool Applies(string rulePath, string target)
    {
        string r = Normalize(rulePath);
        return target.Equals(r, StringComparison.Ordinal) || target.StartsWith(r.EndsWith('\\') ? r : r + "\\", StringComparison.Ordinal);
    }

    /// <summary>比較用の正規化（小文字・区切りは \・末尾の \ なし。ドライブ直下は "c:\"）。</summary>
    public static string Normalize(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    public static string Describe(Permission p)
    {
        var names = new List<string>();
        if (p.HasFlag(Permission.Read)) names.Add("閲覧");
        if (p.HasFlag(Permission.Create)) names.Add("作成");
        if (p.HasFlag(Permission.Modify)) names.Add("編集");
        if (p.HasFlag(Permission.Delete)) names.Add("削除");
        if (p.HasFlag(Permission.Share)) names.Add("リンク");
        if (p.HasFlag(Permission.Admin)) names.Add("管理");
        return names.Count == 0 ? "なし" : string.Join("・", names);
    }
}

/// <summary>クォータゾーン1件と現在の使用量。</summary>
public sealed record QuotaZone(long Id, string Path, long LimitBytes, string Scope, long? UserId, long? RoleId, int WarnPercent, long UsedBytes = 0)
{
    public double Ratio => LimitBytes <= 0 ? 1 : (double)UsedBytes / LimitBytes;
    public long Remaining => Math.Max(0, LimitBytes - UsedBytes);

    /// <summary>このゾーンが userId / roleId の利用者に効くか。</summary>
    public bool AppliesTo(long userId, long roleId) => Scope switch
    {
        "user" => UserId == userId,
        "role" => RoleId == roleId,
        _ => true,
    };
}

/// <summary>書き込み前の容量判定（02_機能設計 §5.2）。</summary>
public static class QuotaCheck
{
    /// <summary>
    /// destination へ bytes 書き込むと超過するゾーンを返す（無ければ null）。
    /// 移動の場合、元がすでに同じゾーン内なら使用量は増えないので数えない。
    /// </summary>
    public static QuotaZone? FindViolation(IEnumerable<QuotaZone> zones, string destination, IReadOnlyList<(string Path, long Bytes)> sources, bool move)
    {
        string dest = PolicyEvaluator.Normalize(destination);
        foreach (var zone in zones)
        {
            if (!PolicyEvaluator.Applies(zone.Path, dest)) continue;
            long incoming = sources.Where(s => !(move && PolicyEvaluator.Applies(zone.Path, PolicyEvaluator.Normalize(s.Path)))).Sum(s => s.Bytes);
            if (incoming > 0 && zone.UsedBytes + incoming > zone.LimitBytes) return zone;
        }
        return null;
    }
}
