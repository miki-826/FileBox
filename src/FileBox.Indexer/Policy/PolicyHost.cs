using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json.Nodes;
using FileBox.Contracts;
using FileBox.Core.Links;
using FileBox.Core.Security;
using FileBox.Data;
using FileBox.Indexer.Index;

namespace FileBox.Indexer.Policy;

/// <summary>パイプの相手（Windows アカウント）。名前付きパイプの偽装で OS から得るので、App 側では偽れない。</summary>
public sealed record Caller(string Sid, string Account, bool IsWindowsAdmin)
{
    const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// パイプの相手のトークンを取り出す。偽装は「トークンを開く」間だけにし、同じスレッドで必ず元に戻す
    /// （NamedPipeServerStream.RunAsClient では偽装が後続の処理に残り、DB やログに書けなくなったため）。
    /// </summary>
    public static Caller? FromPipe(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        IntPtr token = IntPtr.Zero;
        if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle)) return null;
        try
        {
            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, openAsSelf: true, out token)) return null;
        }
        finally
        {
            if (!RevertToSelf()) Environment.FailFast("RevertToSelf に失敗しました");   // 偽装したまま続行するのは危険
        }
        try
        {
            using var identity = new WindowsIdentity(token);
            return FromIdentity(identity);
        }
        finally { CloseHandle(token); }
    }

    const uint TOKEN_QUERY = 0x0008;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ImpersonateNamedPipeClient(Microsoft.Win32.SafeHandles.SafePipeHandle pipe);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool RevertToSelf();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static Caller FromIdentity(WindowsIdentity identity)
    {
        // UAC で昇格していない管理者は Administrators が「拒否専用」で入っている。その場合もメンバーとして扱う
        bool admin = identity.Groups?.Any(g => g.Value == AdministratorsSid) == true
                  || identity.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == AdministratorsSid);
        return new Caller(identity.User?.Value ?? "", identity.Name, admin);
    }
}

/// <summary>
/// アカウント・権限・容量・監査（02_機能設計 §5・§6）の判定役。データは system.db、容量は名前索引から集計する。
/// </summary>
public sealed class PolicyHost : IDisposable
{
    readonly FileIndex _index;
    readonly SystemStore? _store;
    readonly string? _error;
    readonly bool _elevated;
    readonly Dictionary<long, (long Used, DateTime At)> _zoneUsage = new();
    readonly Dictionary<long, (long Used, DateTime At)> _userUsage = new();
    static readonly TimeSpan UsageCacheFor = TimeSpan.FromSeconds(5);

    public PolicyHost(FileIndex index, bool elevated, string? databasePath = null)
    {
        _index = index;
        _elevated = elevated;
        string path = databasePath ?? SystemStore.DefaultPath();
        if (elevated) ProtectFolder(Path.GetDirectoryName(path)!);
        _store = SystemStore.OpenBest(path, out _error);
        if (_store == null) Log.Error($"system.db を開けません（権限・容量の管理は無効）: {_error}");
        else if (_store.IsReadOnly) Log.Info("system.db を読み取り専用で開きました（管理操作は管理者として起動した Indexer で行えます）");
    }

    /// <summary>
    /// 管理者（またはサービス）として初めて作るときに、system.db のフォルダを「管理者と SYSTEM は変更可・利用者は読み取りのみ」にする。
    /// 一般ユーザーがファイルを直接書き換えて権限を外すことを防ぐ。
    /// </summary>
    static void ProtectFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex) { Log.Error("system.db フォルダの保護に失敗", ex); }
    }

    // ================================================================ 要求の処理

    public JsonNode? Handle(Caller caller, string method, JsonNode? args)
    {
        if (method == PolicyMethods.Session) return PolicyMethods.ToNode(Session(caller));
        if (_store == null) return null;   // 管理なし: 記録系は何もしない

        var (user, _) = Register(caller);
        switch (method)
        {
            case PolicyMethods.Audit:
                var e = PolicyMethods.FromNode<AuditDto>(args)!;
                TryWrite(() => _store.Audit(user?.Id, e.Action, e.Src, e.Dst, e.Bytes, e.Result, e.Detail));
                return null;
            case PolicyMethods.RecordOwners:
                if (user != null) RecordOwners(user.Id, PolicyMethods.FromNode<List<string>>(args) ?? new());
                return null;
            case PolicyMethods.Usage:
                return PolicyMethods.ToNode(_index.Usage(PolicyMethods.FromNode<string>(args) ?? "", 100));
        }

        // ---- ここから管理者のみ（system.db の役割で判定。Windows 管理者かどうかではない）
        if (user == null || !IsAdmin(user)) throw new UnauthorizedAccessException("管理者ロールのアカウントだけが行えます");
        if (_store.IsReadOnly) throw new UnauthorizedAccessException("設定を変更するには Indexer を管理者として起動してください（状態表示の「管理者として再起動」）");

        switch (method)
        {
            case PolicyMethods.AdminUsers:
                return PolicyMethods.ToNode(AdminData());
            case PolicyMethods.AdminUpdateUser:
            {
                var u = PolicyMethods.FromNode<UserDto>(args)!;
                if (u.Id == user.Id && u.RoleId != SystemStore.AdminRole && _store.GetUsers().Count(x => x.RoleId == SystemStore.AdminRole) <= 1)
                    throw new InvalidOperationException("管理者が1人もいなくなるため、自分のロールは変更できません");
                _store.UpdateUser(u.Id, u.RoleId, u.QuotaBytes, u.IsDisabled);
                ResyncOs();
                _store.Audit(user.Id, "perm_change", null, null, null, "ok", $"ユーザー {u.Account}: ロール={u.RoleId} 容量={u.QuotaBytes?.ToString() ?? "既定"} 無効={u.IsDisabled}");
                return null;
            }
            case PolicyMethods.AdminUpdateRole:
            {
                var r = PolicyMethods.FromNode<RoleDto>(args)!;
                var flags = (Permission)r.DefaultFlags;
                if (r.Id == SystemStore.AdminRole) flags = Permission.All;   // 管理者ロールから管理権限は外せない
                _store.UpdateRole(r.Id, flags, r.QuotaBytes);
                _store.Audit(user.Id, "perm_change", null, null, null, "ok", $"ロール {r.Name}: 既定={PolicyEvaluator.Describe(flags)} 容量={r.QuotaBytes?.ToString() ?? "無制限"}");
                return null;
            }
            case PolicyMethods.AdminSetDefaultRole:
            {
                long id = PolicyMethods.FromNode<long>(args);
                _store.SetSetting("defaultRole", id.ToString());
                _store.Audit(user.Id, "perm_change", null, null, null, "ok", $"新しいアカウントの既定ロール={id}");
                return null;
            }
            case PolicyMethods.AdminSetPermission:
            {
                var p = PolicyMethods.FromNode<PermissionDto>(args)!;
                _store.SetPermission(p.Path, p.UserId, p.RoleId, (Permission)p.Allow, (Permission)p.Deny);
                ResyncOs();
                _store.Audit(user.Id, "perm_change", p.Path, null, null, "ok",
                    $"{(p.UserId != null ? "ユーザー" + p.UserId : "ロール" + p.RoleId)}: 許可={PolicyEvaluator.Describe((Permission)p.Allow)} 拒否={PolicyEvaluator.Describe((Permission)p.Deny)}");
                return null;
            }
            case PolicyMethods.AdminDeletePermission:
                _store.DeletePermission(PolicyMethods.FromNode<long>(args));
                ResyncOs();
                _store.Audit(user.Id, "perm_change", null, null, null, "ok", "権限設定を削除");
                return null;
            case PolicyMethods.AdminSaveZone:
            {
                var z = PolicyMethods.FromNode<ZoneDto>(args)!;
                if (z.LimitBytes < 0) throw new ArgumentException("上限が不正です");
                long id = _store.SaveZone(new QuotaZone(z.Id, z.Path.TrimEnd('\\').Length <= 2 ? z.Path : z.Path.TrimEnd('\\'), z.LimitBytes, z.Scope, z.UserId, z.RoleId, Math.Clamp(z.WarnPercent, 1, 100)));
                lock (_zoneUsage) _zoneUsage.Remove(id);
                ResyncOs();
                _store.Audit(user.Id, "quota_change", z.Path, null, z.LimitBytes, "ok", $"対象={z.Scope}");
                return PolicyMethods.ToNode(id);
            }
            case PolicyMethods.AdminDeleteZone:
                _store.DeleteZone(PolicyMethods.FromNode<long>(args));
                ResyncOs();
                _store.Audit(user.Id, "quota_change", null, null, null, "ok", "クォータゾーンを削除");
                return null;
            case PolicyMethods.AdminOsEnforce:
            {
                var req = PolicyMethods.FromNode<OsEnforceRequest>(args)!;
                if (!_elevated) throw new UnauthorizedAccessException("OS 強制モードを変えるには Indexer を管理者として起動してください（状態表示の「管理者として再起動」）");
                var target = _store.GetUser(req.UserId) ?? throw new ArgumentException("ユーザーが見つかりません");
                RemoveOs(target.Id);
                _store.SetUserOsEnforced(target.Id, req.Enabled);
                string detail = req.Enabled ? ApplyOs(_store.GetUser(target.Id)!) : "解除";
                _store.Audit(user.Id, "perm_change", null, null, null, "ok", $"OS 強制モード {target.AccountName}: {detail}");
                return null;
            }
            case PolicyMethods.AdminAudit:
            {
                var q = PolicyMethods.FromNode<AuditQuery>(args) ?? new AuditQuery();
                var list = _store.GetAudit(q.From, q.To, q.UserId, q.Action, Math.Clamp(q.Limit, 1, 100000))
                    .Select(a => new AuditDto { At = a.At, Account = a.Account, Action = a.Action, Src = a.Src, Dst = a.Dst, Bytes = a.Bytes, Result = a.Result, Detail = a.Detail })
                    .ToList();
                return PolicyMethods.ToNode(list);
            }
        }
        throw new InvalidOperationException($"未対応の操作: {method}");
    }

    // ================================================================ セッション

    (SystemUser? User, bool Created) Register(Caller caller)
    {
        if (_store == null || caller.Sid.Length == 0) return (null, false);
        if (_store.IsReadOnly) return (_store.GetUserBySid(caller.Sid), false);
        try
        {
            var result = _store.EnsureUser(caller.Sid, caller.Account, caller.IsWindowsAdmin);
            if (result.Created) _store.Audit(result.User.Id, "user_register", null, null, null, "ok", $"初回利用 {caller.Account}（ロール: {result.User.RoleName}）");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("利用者の登録に失敗", ex);
            return (_store.GetUserBySid(caller.Sid), false);
        }
    }

    bool IsAdmin(SystemUser user) =>
        !user.IsDisabled && (_store!.GetRole(user.RoleId)?.DefaultFlags.HasFlag(Permission.Admin) ?? false);

    SessionInfo Session(Caller caller)
    {
        if (_store == null) return new SessionInfo { Unmanaged = true, UnmanagedReason = _error ?? "system.db を開けません", Account = caller.Account, IsAdmin = caller.IsWindowsAdmin };
        var (user, created) = Register(caller);
        long roleId = user?.RoleId ?? (long.TryParse(_store.GetSetting("defaultRole"), out long r) ? r : SystemStore.StandardRole);
        var role = _store.GetRole(roleId);
        var info = new SessionInfo
        {
            UserId = user?.Id ?? 0,
            Account = user?.AccountName ?? caller.Account,
            RoleId = roleId,
            RoleName = role?.Name ?? "",
            // 無効化されたアカウントは閲覧のみ
            RoleDefault = user?.IsDisabled == true ? (int)Permission.Read : (int)(role?.DefaultFlags ?? Permission.Read),
            IsAdmin = user != null && IsAdmin(user),
            FirstUse = created,
            UserQuotaBytes = user?.QuotaBytes ?? role?.QuotaBytes,
        };
        foreach (var (id, rule) in _store.GetPermissions())
        {
            if ((rule.UserId != null && rule.UserId == info.UserId) || (rule.RoleId != null && rule.RoleId == roleId))
                info.Permissions.Add(new PermissionDto { Id = id, Path = rule.Path, Allow = (int)rule.Allow, Deny = (int)rule.Deny, UserId = rule.UserId, RoleId = rule.RoleId });
        }
        foreach (var zone in _store.GetZones().Where(z => z.AppliesTo(info.UserId, roleId)))
            info.Zones.Add(ToDto(zone));
        if (user != null && info.UserQuotaBytes != null) info.UserUsedBytes = UserUsage(user.Id);
        return info;
    }

    ZoneDto ToDto(QuotaZone z) => new()
    {
        Id = z.Id, Path = z.Path, LimitBytes = z.LimitBytes, Scope = z.Scope, UserId = z.UserId, RoleId = z.RoleId, WarnPercent = z.WarnPercent,
        UsedBytes = ZoneUsage(z),
    };

    long ZoneUsage(QuotaZone zone)
    {
        lock (_zoneUsage)
        {
            if (_zoneUsage.TryGetValue(zone.Id, out var c) && DateTime.UtcNow - c.At < UsageCacheFor) return c.Used;
        }
        long used = _index.SubtreeSize(zone.Path);
        lock (_zoneUsage) _zoneUsage[zone.Id] = (used, DateTime.UtcNow);
        return used;
    }

    AdminData AdminData()
    {
        var data = new AdminData
        {
            DefaultRoleId = long.TryParse(_store!.GetSetting("defaultRole"), out long r) ? r : SystemStore.StandardRole,
            Elevated = _elevated,
            Roles = _store.GetRoles().Select(x => new RoleDto { Id = x.Id, Name = x.Name, DefaultFlags = (int)x.DefaultFlags, QuotaBytes = x.QuotaBytes }).ToList(),
            Zones = _store.GetZones().Select(ToDto).ToList(),
            Permissions = _store.GetPermissions().Select(p => new PermissionDto
            {
                Id = p.Id, Path = p.Rule.Path, Allow = (int)p.Rule.Allow, Deny = (int)p.Rule.Deny, UserId = p.Rule.UserId, RoleId = p.Rule.RoleId,
            }).ToList(),
        };
        foreach (var u in _store.GetUsers())
            data.Users.Add(new UserDto
            {
                Id = u.Id, Account = u.AccountName, RoleId = u.RoleId, RoleName = u.RoleName, QuotaBytes = u.QuotaBytes,
                IsDisabled = u.IsDisabled, LastSeenAt = u.LastSeenAt, UsedBytes = UserUsage(u.Id), OsEnforced = u.OsEnforced,
            });
        return data;
    }

    // ================================================================ OS 強制モード

    /// <summary>
    /// 利用者に効く「拒否」を NTFS の拒否エントリに、ドライブ全体のユーザー別ゾーンをディスククォータにする。
    /// ロールの拒否でも、本人の設定が同じ場所で許可しているもの（FileBox では本人設定が優先）は付けない。
    /// </summary>
    string ApplyOs(SystemUser user)
    {
        var rules = _store!.GetPermissions().Select(p => p.Rule).ToList();
        var mine = rules.Where(r => r.UserId == user.Id).ToList();
        var fromRole = rules.Where(r => r.RoleId == user.RoleId && r.UserId == null).ToList();
        int acl = 0, quota = 0;
        var errors = new List<string>();
        foreach (var rule in mine.Concat(fromRole))
        {
            Permission deny = rule.Deny;
            if (rule.RoleId != null)
                deny &= ~mine.Where(m => string.Equals(m.Path, rule.Path, StringComparison.OrdinalIgnoreCase)).Aggregate(Permission.None, (a, m) => a | m.Allow);
            var rights = OsEnforcer.RightsFor(deny);
            if (rights == 0) continue;
            try
            {
                OsEnforcer.ApplyDeny(rule.Path, user.Sid, rights);
                _store.AddOsApplied(user.Id, "acl", rule.Path, (long)rights);
                acl++;
            }
            catch (Exception ex) { errors.Add($"{rule.Path}: {ex.Message}"); }
        }
        foreach (var zone in _store.GetZones().Where(z => z.Scope == "user" && z.UserId == user.Id && z.Path.TrimEnd('\\').Length == 2))
        {
            try
            {
                OsEnforcer.SetVolumeQuota(zone.Path, user.AccountName, zone.LimitBytes, zone.WarnPercent);
                _store.AddOsApplied(user.Id, "quota", zone.Path, zone.LimitBytes);
                quota++;
            }
            catch (Exception ex) { errors.Add($"{zone.Path}: {ex.Message}"); }
        }
        if (errors.Count > 0) Log.Error("OS 強制モードの一部に失敗: " + string.Join(" / ", errors));
        return $"アクセス拒否 {acl} 件・ディスククォータ {quota} 件" + (errors.Count > 0 ? $"（失敗 {errors.Count} 件: {errors[0]}）" : "");
    }

    void RemoveOs(long userId)
    {
        var user = _store!.GetUser(userId);
        foreach (var applied in _store.GetOsApplied(userId))
        {
            try
            {
                if (applied.Kind == "acl" && user != null) OsEnforcer.RemoveDeny(applied.Path, user.Sid, (System.Security.AccessControl.FileSystemRights)applied.Value);
                else if (applied.Kind == "quota" && user != null) OsEnforcer.SetVolumeQuota(applied.Path, user.AccountName, null, 100);
            }
            catch (Exception ex) { Log.Error($"OS 強制モードの解除に失敗: {applied.Path}", ex); }
            _store.DeleteOsApplied(applied.Id);
        }
    }

    /// <summary>権限・ゾーン・ロールが変わったら、OS 強制モードの利用者の設定を付け直す（管理者として動いているときだけ）。</summary>
    void ResyncOs()
    {
        if (!_elevated || _store == null) return;
        foreach (var u in _store.GetUsers().Where(u => u.OsEnforced))
        {
            RemoveOs(u.Id);
            ApplyOs(u);
        }
    }

    // ================================================================ ユーザー別容量（作成者の記録）

    void RecordOwners(long userId, List<string> paths)
    {
        int count = 0;
        foreach (var path in paths)
        {
            IEnumerable<string> files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                : new[] { path };
            foreach (var file in files)
            {
                if (++count > 20000) return;   // 巨大なフォルダのコピーでも止まらないよう上限
                if (FileIdentity.TryGet(file) is not { } id) continue;
                long size;
                try { size = new FileInfo(file).Length; } catch (Exception) { continue; }
                TryWrite(() => _store!.RecordOwner(id.VolumeSerial, id.Id, userId, file, size));
            }
        }
        lock (_userUsage) _userUsage.Remove(userId);
    }

    long UserUsage(long userId)
    {
        lock (_userUsage)
        {
            if (_userUsage.TryGetValue(userId, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Used;
        }
        long total = 0;
        foreach (var f in _store!.GetOwnedFiles(userId))
        {
            // 最後に知っている場所にあればそのサイズ。無ければファイルIDで追跡し、消えていたら記録を消す
            long? size = _index.SizeOf(f.LastPath) ?? (File.Exists(f.LastPath) ? new FileInfo(f.LastPath).Length : null);
            if (size == null)
            {
                string? moved = FileIdentity.TryResolve(new FileId(f.VolumeSerial, f.FileId));
                if (moved != null && File.Exists(moved))
                {
                    size = new FileInfo(moved).Length;
                    TryWrite(() => _store.UpdateOwnedFile(f.VolumeSerial, f.FileId, moved, size.Value));
                }
                else
                {
                    TryWrite(() => _store.DeleteOwnedFile(f.VolumeSerial, f.FileId));
                    continue;
                }
            }
            total += size.Value;
        }
        lock (_userUsage) _userUsage[userId] = (total, DateTime.UtcNow);
        return total;
    }

    void TryWrite(Action write)
    {
        if (_store == null || _store.IsReadOnly) return;
        try { write(); } catch (Exception ex) { Log.Error("system.db への書き込み失敗", ex); }
    }

    public void Dispose() => _store?.Dispose();
}
