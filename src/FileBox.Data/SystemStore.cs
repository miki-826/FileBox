using FileBox.Core.Security;
using Microsoft.Data.Sqlite;

namespace FileBox.Data;

public sealed record SystemUser(long Id, string Sid, string AccountName, long RoleId, string RoleName, long? QuotaBytes, bool IsDisabled, DateTime CreatedAt, DateTime? LastSeenAt, bool OsEnforced = false);
/// <summary>OS 強制モードで Windows 側に付けた設定（解除時に同じものを外すための記録）。Kind は acl / quota。</summary>
public sealed record OsApplied(long Id, long UserId, string Kind, string Path, long Value);
public sealed record Role(long Id, string Name, Permission DefaultFlags, long? QuotaBytes);
public sealed record AuditEntry(long Id, DateTime At, string? Account, string Action, string? Src, string? Dst, long? Bytes, string Result, string? Detail);
public sealed record OwnedFile(long VolumeSerial, byte[] FileId, string LastPath, long Size);

/// <summary>
/// 全ユーザー共通のデータ（%ProgramData%\FileBox\system.db、03_データ設計 §2）。
/// アカウント・ロール・フォルダ権限・クォータゾーン・作成者記録・監査ログ。書き込みは Indexer だけが行う。
/// </summary>
public sealed class SystemStore : IDisposable
{
    public const long AdminRole = 1, StandardRole = 2, GuestRole = 3;

    static readonly string[] Migrations =
    {
        """
        CREATE TABLE roles (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, default_flags INTEGER NOT NULL, quota_bytes INTEGER);
        INSERT INTO roles(id, name, default_flags, quota_bytes) VALUES
          (1, '管理者', 63, NULL),
          (2, '一般', 31, 53687091200),
          (3, 'ゲスト', 1, 0);
        CREATE TABLE users (
          id INTEGER PRIMARY KEY, windows_sid TEXT NOT NULL UNIQUE, account_name TEXT NOT NULL, display_name TEXT,
          role_id INTEGER NOT NULL REFERENCES roles(id), quota_bytes INTEGER, is_disabled INTEGER NOT NULL DEFAULT 0,
          is_orphaned INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, last_seen_at TEXT);
        CREATE TABLE permissions (
          id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id) ON DELETE CASCADE, role_id INTEGER REFERENCES roles(id) ON DELETE CASCADE,
          path TEXT NOT NULL, allow_flags INTEGER NOT NULL DEFAULT 0, deny_flags INTEGER NOT NULL DEFAULT 0,
          CHECK ((user_id IS NULL) <> (role_id IS NULL)));
        CREATE INDEX ix_permissions_path ON permissions(path);
        CREATE TABLE quota_zones (
          id INTEGER PRIMARY KEY, path TEXT NOT NULL, limit_bytes INTEGER NOT NULL,
          scope TEXT NOT NULL CHECK (scope IN ('all','user','role')),
          user_id INTEGER REFERENCES users(id) ON DELETE CASCADE, role_id INTEGER REFERENCES roles(id) ON DELETE CASCADE,
          warn_percent INTEGER NOT NULL DEFAULT 80, os_enforced INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE file_owners (
          volume_serial INTEGER NOT NULL, file_id BLOB NOT NULL, user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          last_path TEXT NOT NULL, size INTEGER NOT NULL, PRIMARY KEY (volume_serial, file_id));
        CREATE INDEX ix_file_owners_user ON file_owners(user_id);
        CREATE TABLE audit_log (
          id INTEGER PRIMARY KEY, at TEXT NOT NULL, user_id INTEGER, action TEXT NOT NULL, src_path TEXT, dst_path TEXT,
          bytes INTEGER, result TEXT NOT NULL, detail TEXT);
        CREATE INDEX ix_audit_at ON audit_log(at);
        """,
        "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        // OS 強制モード（02_機能設計 §6.3）
        """
        ALTER TABLE users ADD COLUMN os_enforced INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE os_applied (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          kind TEXT NOT NULL, path TEXT NOT NULL, value INTEGER NOT NULL);
        """,
    };

    readonly SqliteConnection _db;
    readonly object _gate = new();

    /// <summary>書き込めない（管理者が保護した system.db を一般権限で開いた）。読み取りと判定はできる。</summary>
    public bool IsReadOnly { get; }

    public SystemStore(string? path = null, bool readOnly = false)
    {
        path ??= DefaultPath();
        IsReadOnly = readOnly;
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _db.Open();
        // WAL は読むだけでも -shm への書き込みが要るので、読み取り専用で開ける通常のジャーナルにする
        if (readOnly) { Exec("PRAGMA foreign_keys=ON;"); return; }
        Exec("PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;");
        Exec("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        long version = Scalar<long?>("SELECT MAX(version) FROM schema_version") ?? 0;
        for (int v = (int)version; v < Migrations.Length; v++)
        {
            using var tx = _db.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec("INSERT INTO schema_version(version) VALUES ($v)", tx, ("$v", v + 1));
            tx.Commit();
        }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileBox", "system", "system.db");

    /// <summary>書き込みで開き、権限が無ければ読み取り専用で開く。どちらも無理なら null。</summary>
    public static SystemStore? OpenBest(string? path, out string? error)
    {
        error = null;
        path ??= DefaultPath();
        try { return new SystemStore(path); }
        catch (Exception ex) when (ex is SqliteException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
        }
        try { return File.Exists(path) ? new SystemStore(path, readOnly: true) : null; }
        catch (Exception ex) when (ex is SqliteException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return null;
        }
    }

    // ================================================================ アカウント（02_機能設計 §6.1）

    /// <summary>
    /// SID で利用者を探し、無ければ自動登録する（初回起動 = 登録。ログイン画面なし）。
    /// 最初の利用者と Windows の Administrators は管理者ロール、それ以外は既定ロール。
    /// </summary>
    public (SystemUser User, bool Created) EnsureUser(string sid, string accountName, bool isWindowsAdmin)
    {
        lock (_gate)
        {
            var existing = GetUserBySid(sid);
            string now = DateTime.Now.ToString("o");
            if (existing != null)
            {
                Exec("UPDATE users SET account_name=$n, last_seen_at=$t, is_orphaned=0 WHERE id=$id", null, ("$n", accountName), ("$t", now), ("$id", existing.Id));
                return (GetUserBySid(sid)!, false);
            }
            bool first = (Scalar<long?>("SELECT COUNT(*) FROM users") ?? 0) == 0;
            long defaultRole = long.TryParse(GetSetting("defaultRole"), out long r) ? r : StandardRole;
            long role = first || isWindowsAdmin ? AdminRole : defaultRole;
            Exec("INSERT INTO users(windows_sid, account_name, role_id, created_at, last_seen_at) VALUES ($s, $n, $r, $t, $t)", null,
                ("$s", sid), ("$n", accountName), ("$r", role), ("$t", now));
            return (GetUserBySid(sid)!, true);
        }
    }

    const string UserSelect = """
        SELECT u.id, u.windows_sid, u.account_name, u.role_id, r.name, u.quota_bytes, u.is_disabled, u.created_at, u.last_seen_at, u.os_enforced
        FROM users u JOIN roles r ON r.id = u.role_id
        """;

    static SystemUser MapUser(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt64(5), r.GetInt64(6) != 0, DateTime.Parse(r.GetString(7)), r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)), r.GetInt64(9) != 0);

    public void SetUserOsEnforced(long id, bool on) => Exec("UPDATE users SET os_enforced=$o WHERE id=$id", null, ("$o", on ? 1 : 0), ("$id", id));

    public List<OsApplied> GetOsApplied(long userId) =>
        Query("SELECT id, user_id, kind, path, value FROM os_applied WHERE user_id=$u", r => new OsApplied(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4)), ("$u", userId));

    public void AddOsApplied(long userId, string kind, string path, long value) =>
        Exec("INSERT INTO os_applied(user_id, kind, path, value) VALUES ($u, $k, $p, $v)", null, ("$u", userId), ("$k", kind), ("$p", path), ("$v", value));

    public void DeleteOsApplied(long id) => Exec("DELETE FROM os_applied WHERE id=$id", null, ("$id", id));

    public SystemUser? GetUserBySid(string sid) => Query(UserSelect + " WHERE u.windows_sid=$s", MapUser, ("$s", sid)).FirstOrDefault();
    public SystemUser? GetUser(long id) => Query(UserSelect + " WHERE u.id=$id", MapUser, ("$id", id)).FirstOrDefault();
    public List<SystemUser> GetUsers() => Query(UserSelect + " ORDER BY u.id", MapUser);

    public void UpdateUser(long id, long roleId, long? quotaBytes, bool disabled) =>
        Exec("UPDATE users SET role_id=$r, quota_bytes=$q, is_disabled=$d WHERE id=$id", null, ("$r", roleId), ("$q", quotaBytes), ("$d", disabled ? 1 : 0), ("$id", id));

    public List<Role> GetRoles() =>
        Query("SELECT id, name, default_flags, quota_bytes FROM roles ORDER BY id", r => new Role(r.GetInt64(0), r.GetString(1), (Permission)r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3)));

    public Role? GetRole(long id) => GetRoles().FirstOrDefault(r => r.Id == id);

    public void UpdateRole(long id, Permission defaults, long? quotaBytes) =>
        Exec("UPDATE roles SET default_flags=$f, quota_bytes=$q WHERE id=$id", null, ("$f", (long)defaults), ("$q", quotaBytes), ("$id", id));

    // ================================================================ 権限（§6.2）

    public List<(long Id, PermissionRule Rule)> GetPermissions() =>
        Query("SELECT id, path, allow_flags, deny_flags, user_id, role_id FROM permissions ORDER BY path",
            r => (r.GetInt64(0), new PermissionRule(r.GetString(1), (Permission)r.GetInt64(2), (Permission)r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5))));

    public void SetPermission(string path, long? userId, long? roleId, Permission allow, Permission deny)
    {
        string p = PolicyEvaluator.Normalize(path);
        lock (_gate)
        {
            Exec("DELETE FROM permissions WHERE path=$p AND user_id IS $u AND role_id IS $r", null, ("$p", p), ("$u", userId), ("$r", roleId));
            if (allow != Permission.None || deny != Permission.None)
                Exec("INSERT INTO permissions(path, user_id, role_id, allow_flags, deny_flags) VALUES ($p, $u, $r, $a, $d)", null,
                    ("$p", p), ("$u", userId), ("$r", roleId), ("$a", (long)allow), ("$d", (long)deny));
        }
    }

    public void DeletePermission(long id) => Exec("DELETE FROM permissions WHERE id=$id", null, ("$id", id));

    // ================================================================ クォータ（§5）

    public List<QuotaZone> GetZones() =>
        Query("SELECT id, path, limit_bytes, scope, user_id, role_id, warn_percent FROM quota_zones ORDER BY path",
            r => new QuotaZone(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5), (int)r.GetInt64(6)));

    public long SaveZone(QuotaZone zone)
    {
        lock (_gate)
        {
            if (zone.Id > 0)
            {
                Exec("UPDATE quota_zones SET path=$p, limit_bytes=$l, scope=$s, user_id=$u, role_id=$r, warn_percent=$w WHERE id=$id", null,
                    ("$p", zone.Path), ("$l", zone.LimitBytes), ("$s", zone.Scope), ("$u", zone.UserId), ("$r", zone.RoleId), ("$w", zone.WarnPercent), ("$id", zone.Id));
                return zone.Id;
            }
            Exec("INSERT INTO quota_zones(path, limit_bytes, scope, user_id, role_id, warn_percent) VALUES ($p, $l, $s, $u, $r, $w)", null,
                ("$p", zone.Path), ("$l", zone.LimitBytes), ("$s", zone.Scope), ("$u", zone.UserId), ("$r", zone.RoleId), ("$w", zone.WarnPercent));
            return Scalar<long>("SELECT last_insert_rowid()");
        }
    }

    public void DeleteZone(long id) => Exec("DELETE FROM quota_zones WHERE id=$id", null, ("$id", id));

    // ================================================================ 作成者（ユーザー別容量）

    public void RecordOwner(long volumeSerial, byte[] fileId, long userId, string path, long size) =>
        Exec("""
             INSERT INTO file_owners(volume_serial, file_id, user_id, last_path, size) VALUES ($v, $f, $u, $p, $s)
             ON CONFLICT(volume_serial, file_id) DO UPDATE SET user_id=excluded.user_id, last_path=excluded.last_path, size=excluded.size
             """, null, ("$v", volumeSerial), ("$f", fileId), ("$u", userId), ("$p", path), ("$s", size));

    public List<OwnedFile> GetOwnedFiles(long userId) =>
        Query("SELECT volume_serial, file_id, last_path, size FROM file_owners WHERE user_id=$u", r => new OwnedFile(r.GetInt64(0), (byte[])r[1], r.GetString(2), r.GetInt64(3)), ("$u", userId));

    public void UpdateOwnedFile(long volumeSerial, byte[] fileId, string path, long size) =>
        Exec("UPDATE file_owners SET last_path=$p, size=$s WHERE volume_serial=$v AND file_id=$f", null, ("$p", path), ("$s", size), ("$v", volumeSerial), ("$f", fileId));

    public void DeleteOwnedFile(long volumeSerial, byte[] fileId) =>
        Exec("DELETE FROM file_owners WHERE volume_serial=$v AND file_id=$f", null, ("$v", volumeSerial), ("$f", fileId));

    // ================================================================ 監査ログ（§6.4）

    public void Audit(long? userId, string action, string? src, string? dst, long? bytes, string result, string? detail = null) =>
        Exec("INSERT INTO audit_log(at, user_id, action, src_path, dst_path, bytes, result, detail) VALUES ($t, $u, $a, $s, $d, $b, $r, $x)", null,
            ("$t", DateTime.Now.ToString("o")), ("$u", userId), ("$a", action), ("$s", src), ("$d", dst), ("$b", bytes), ("$r", result), ("$x", detail));

    public List<AuditEntry> GetAudit(DateTime? from = null, DateTime? to = null, long? userId = null, string? action = null, int limit = 1000) =>
        Query("""
              SELECT a.id, a.at, u.account_name, a.action, a.src_path, a.dst_path, a.bytes, a.result, a.detail
              FROM audit_log a LEFT JOIN users u ON u.id = a.user_id
              WHERE ($f IS NULL OR a.at >= $f) AND ($t IS NULL OR a.at < $t) AND ($u IS NULL OR a.user_id = $u) AND ($a IS NULL OR a.action = $a)
              ORDER BY a.id DESC LIMIT $n
              """,
            r => new AuditEntry(r.GetInt64(0), DateTime.Parse(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)),
            ("$f", from?.ToString("o")), ("$t", to?.ToString("o")), ("$u", userId), ("$a", action), ("$n", limit));

    // ================================================================ 設定（既定ロールなど）

    public string? GetSetting(string key) => Scalar<string?>("SELECT value FROM settings WHERE key=$k", ("$k", key));

    public void SetSetting(string key, string value)
    {
        Exec("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", null, ("$k", key), ("$v", value));
    }

    // ================================================================ 低レベル

    void Exec(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
    }

    T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            object? v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return default;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(v, target);
        }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => _db.Dispose();
}
