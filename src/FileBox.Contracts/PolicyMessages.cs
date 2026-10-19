using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileBox.Contracts;

/// <summary>Call 要求の操作名（02_機能設計 §5・§6）。Admin… は管理者ロールのみ。</summary>
public static class PolicyMethods
{
    public const string Session = "session";              // 自分のアカウント・権限・容量
    public const string Audit = "audit";                  // 監査ログへ記録
    public const string RecordOwners = "recordOwners";    // FileBox 経由で作成したファイルの作成者を記録
    public const string Usage = "usage";                  // フォルダ内訳と大きいファイル（容量分析）

    public const string AdminUsers = "admin.users";
    public const string AdminUpdateUser = "admin.updateUser";
    public const string AdminUpdateRole = "admin.updateRole";
    public const string AdminSetDefaultRole = "admin.setDefaultRole";
    public const string AdminSetPermission = "admin.setPermission";
    public const string AdminDeletePermission = "admin.deletePermission";
    public const string AdminSaveZone = "admin.saveZone";
    public const string AdminDeleteZone = "admin.deleteZone";
    public const string AdminAudit = "admin.audit";
    public const string AdminOsEnforce = "admin.osEnforce";   // OS 強制モードの切り替え（管理者として起動した Indexer のみ）

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
    public static T? FromNode<T>(JsonNode? node) => node == null ? default : node.Deserialize<T>(Options);
}

public sealed class SessionInfo
{
    /// <summary>system.db が使えず権限・容量の管理が無効（全操作を許可）。</summary>
    public bool Unmanaged { get; set; }
    public string? UnmanagedReason { get; set; }
    public long UserId { get; set; }
    public string Account { get; set; } = "";
    public long RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public int RoleDefault { get; set; }
    public bool IsAdmin { get; set; }
    public bool FirstUse { get; set; }
    /// <summary>自分か自分のロールに関係する権限設定。</summary>
    public List<PermissionDto> Permissions { get; set; } = new();
    /// <summary>自分に効くクォータゾーン（使用量付き）。</summary>
    public List<ZoneDto> Zones { get; set; } = new();
    /// <summary>ユーザー別容量（FileBox 経由で作成したファイルの合計）。上限 null は無制限。</summary>
    public long? UserQuotaBytes { get; set; }
    public long UserUsedBytes { get; set; }
}

public sealed class PermissionDto
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public int Allow { get; set; }
    public int Deny { get; set; }
    public long? UserId { get; set; }
    public long? RoleId { get; set; }
}

public sealed class ZoneDto
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public long LimitBytes { get; set; }
    public string Scope { get; set; } = "all";
    public long? UserId { get; set; }
    public long? RoleId { get; set; }
    public int WarnPercent { get; set; } = 80;
    /// <summary>使用量。索引に無い場所は -1。</summary>
    public long UsedBytes { get; set; }
}

public sealed class UserDto
{
    public long Id { get; set; }
    public string Account { get; set; } = "";
    public long RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public long? QuotaBytes { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public long UsedBytes { get; set; }
    public bool OsEnforced { get; set; }
}

public sealed class OsEnforceRequest
{
    public long UserId { get; set; }
    public bool Enabled { get; set; }
}

public sealed class RoleDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int DefaultFlags { get; set; }
    public long? QuotaBytes { get; set; }
}

public sealed class AdminData
{
    public List<UserDto> Users { get; set; } = new();
    public List<RoleDto> Roles { get; set; } = new();
    public List<PermissionDto> Permissions { get; set; } = new();
    public List<ZoneDto> Zones { get; set; } = new();
    public long DefaultRoleId { get; set; }
    /// <summary>Indexer が管理者として動いている（OS 強制モードが使える）。</summary>
    public bool Elevated { get; set; }
}

public sealed class AuditDto
{
    public DateTime At { get; set; }
    public string? Account { get; set; }
    public string Action { get; set; } = "";
    public string? Src { get; set; }
    public string? Dst { get; set; }
    public long? Bytes { get; set; }
    public string Result { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class AuditQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public long? UserId { get; set; }
    public string? Action { get; set; }
    public int Limit { get; set; } = 1000;
}

public sealed class UsageItem
{
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
    public bool IsPartial { get; set; }
}

public sealed class UsageReport
{
    public string Path { get; set; } = "";
    public long Total { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public bool IsPartial { get; set; }
    public string Source { get; set; } = "index";
    public List<UsageItem> Children { get; set; } = new();
    public List<UsageItem> Largest { get; set; } = new();
}
