using Microsoft.Data.Sqlite;

namespace FileBox.Data;

public sealed record Favorite(long Id, string Path, string? Label);
public sealed record Workspace(long Id, string Name, string LayoutJson, DateTime UpdatedAt);
public sealed record SavedSearch(long Id, string Name, string Query);
public sealed record SaveHistoryRow(string Tokens, string? Ext, string FolderPath, string? RejectedPath, DateTime ChosenAt);
public sealed record Bookmark(long Id, string Token, long VolumeSerial, byte[] FileId, string LastPath, string? Label);
public sealed record InboxRule(long Id, string? NamePattern, string? Ext, string TargetPath, bool AutoMove, int Priority);

/// <summary>
/// ユーザーごとのデータ（%LOCALAPPDATA%\FileBox\filebox.db）。03_データ設計 §3 の user.db。
/// Phase 1 ではお気に入り・ワークスペース・検索履歴・最近使った項目・設定を扱う。
/// </summary>
public sealed class UserStore : IDisposable
{
    /// <summary>スキーマの世代。後続フェーズのテーブル（save_history 等）はここに追記する。</summary>
    static readonly string[] Migrations =
    {
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE favorites (id INTEGER PRIMARY KEY, path TEXT NOT NULL, label TEXT, sort INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE workspaces (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, layout_json TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE saved_searches (id INTEGER PRIMARY KEY, name TEXT NOT NULL, query TEXT NOT NULL);
        CREATE TABLE search_history (id INTEGER PRIMARY KEY, query TEXT NOT NULL, at TEXT NOT NULL);
        CREATE TABLE recent_items (path TEXT PRIMARY KEY, last_access TEXT NOT NULL, count INTEGER NOT NULL);
        """,
        // P2 スマート保存・受け取りボックス（03_データ設計 §3）。rejected_path はネガティブ学習用に追加
        """
        CREATE TABLE save_history (
          id INTEGER PRIMARY KEY, tokens TEXT NOT NULL, ext TEXT, source_app TEXT, folder_path TEXT NOT NULL,
          suggested_rank INTEGER, rejected_path TEXT, chosen_at TEXT NOT NULL);
        CREATE INDEX ix_save_history_at ON save_history(chosen_at);
        CREATE TABLE inbox_sources (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, enabled INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE inbox_rules (
          id INTEGER PRIMARY KEY, name_pattern TEXT, ext TEXT, source_app TEXT, target_path TEXT NOT NULL,
          auto_move INTEGER NOT NULL DEFAULT 0, priority INTEGER NOT NULL DEFAULT 100);
        CREATE TABLE inbox_dismissed (path TEXT PRIMARY KEY COLLATE NOCASE, at TEXT NOT NULL);
        """,
        // P3 リンク（03_データ設計 bookmarks）と、エクスプローラー置き換え前のレジストリ値（shell_backup）
        """
        CREATE TABLE bookmarks (
          id INTEGER PRIMARY KEY, token TEXT NOT NULL UNIQUE, volume_serial INTEGER NOT NULL, file_id BLOB NOT NULL,
          last_path TEXT NOT NULL, label TEXT, created_at TEXT NOT NULL);
        CREATE TABLE shell_backup (key_path TEXT NOT NULL, value_name TEXT NOT NULL, value TEXT, existed INTEGER NOT NULL,
          PRIMARY KEY (key_path, value_name));
        """,
    };

    /// <summary>前回終了時のタブ構成を保存するワークスペース名（一覧には出さない）。</summary>
    public const string SessionWorkspace = "__session__";

    readonly SqliteConnection _db;
    readonly object _gate = new();

    public UserStore(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileBox", "filebox.db");

    void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        long version = Scalar<long?>("SELECT MAX(version) FROM schema_version") ?? 0;
        for (int v = (int)version; v < Migrations.Length; v++)
        {
            using var tx = _db.BeginTransaction();
            Exec(Migrations[v], tx: tx);
            Exec("INSERT INTO schema_version(version) VALUES ($v)", tx, ("$v", v + 1));
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- 設定

    public string? GetSetting(string key) => Scalar<string?>("SELECT value FROM settings WHERE key=$k", ("$k", key));

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", null, ("$k", key), ("$v", value));

    // ---------------------------------------------------------------- お気に入り

    public List<Favorite> GetFavorites() =>
        Query("SELECT id, path, label FROM favorites ORDER BY sort, id", r => new Favorite(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));

    public void AddFavorite(string path, string? label = null)
    {
        if (Scalar<long?>("SELECT id FROM favorites WHERE path=$p COLLATE NOCASE", ("$p", path)) != null) return;
        Exec("INSERT INTO favorites(path, label, sort) VALUES ($p, $l, (SELECT IFNULL(MAX(sort), 0) + 1 FROM favorites))", null, ("$p", path), ("$l", label));
    }

    public void RemoveFavorite(long id) => Exec("DELETE FROM favorites WHERE id=$id", null, ("$id", id));

    // ---------------------------------------------------------------- ワークスペース

    public List<Workspace> GetWorkspaces() =>
        Query("SELECT id, name, layout_json, updated_at FROM workspaces WHERE name <> $s ORDER BY updated_at DESC",
            r => new Workspace(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3))), ("$s", SessionWorkspace));

    public Workspace? GetWorkspace(string name) =>
        Query("SELECT id, name, layout_json, updated_at FROM workspaces WHERE name=$n",
            r => new Workspace(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3))), ("$n", name)).FirstOrDefault();

    public Workspace? GetWorkspace(long id) =>
        Query("SELECT id, name, layout_json, updated_at FROM workspaces WHERE id=$id",
            r => new Workspace(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3))), ("$id", id)).FirstOrDefault();

    public void SaveWorkspace(string name, string layoutJson) =>
        Exec("""
             INSERT INTO workspaces(name, layout_json, updated_at) VALUES ($n, $j, $t)
             ON CONFLICT(name) DO UPDATE SET layout_json=excluded.layout_json, updated_at=excluded.updated_at
             """, null, ("$n", name), ("$j", layoutJson), ("$t", DateTime.Now.ToString("o")));

    public void DeleteWorkspace(long id) => Exec("DELETE FROM workspaces WHERE id=$id", null, ("$id", id));

    // ---------------------------------------------------------------- 検索

    public void AddSearchHistory(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        Exec("DELETE FROM search_history WHERE query=$q", null, ("$q", query));
        Exec("INSERT INTO search_history(query, at) VALUES ($q, $t)", null, ("$q", query), ("$t", DateTime.Now.ToString("o")));
        Exec("DELETE FROM search_history WHERE id NOT IN (SELECT id FROM search_history ORDER BY id DESC LIMIT 200)");
    }

    public List<string> GetSearchHistory(int limit = 20) =>
        Query("SELECT query FROM search_history ORDER BY id DESC LIMIT $n", r => r.GetString(0), ("$n", limit));

    public List<SavedSearch> GetSavedSearches() =>
        Query("SELECT id, name, query FROM saved_searches ORDER BY name", r => new SavedSearch(r.GetInt64(0), r.GetString(1), r.GetString(2)));

    public void SaveSearch(string name, string query) =>
        Exec("INSERT INTO saved_searches(name, query) VALUES ($n, $q)", null, ("$n", name), ("$q", query));

    public void DeleteSavedSearch(long id) => Exec("DELETE FROM saved_searches WHERE id=$id", null, ("$id", id));

    // ---------------------------------------------------------------- 最近使った項目

    public void TouchRecent(string path) =>
        Exec("""
             INSERT INTO recent_items(path, last_access, count) VALUES ($p, $t, 1)
             ON CONFLICT(path) DO UPDATE SET last_access=excluded.last_access, count=count+1
             """, null, ("$p", path), ("$t", DateTime.Now.ToString("o")));

    public List<string> GetRecent(int limit = 20) =>
        Query("SELECT path FROM recent_items ORDER BY last_access DESC LIMIT $n", r => r.GetString(0), ("$n", limit));

    // ---------------------------------------------------------------- 保存先の学習（02_機能設計 §1.5）

    public void AddSaveHistory(string tokens, string? ext, string folderPath, int? suggestedRank, string? rejectedPath, string? sourceApp = null)
    {
        Exec("""
             INSERT INTO save_history(tokens, ext, source_app, folder_path, suggested_rank, rejected_path, chosen_at)
             VALUES ($t, $e, $a, $f, $r, $x, $at)
             """, null, ("$t", tokens), ("$e", ext), ("$a", sourceApp), ("$f", folderPath), ("$r", suggestedRank), ("$x", rejectedPath),
             ("$at", DateTime.Now.ToString("o")));
    }

    public List<SaveHistoryRow> GetSaveHistory(int limit = 3000) =>
        Query("SELECT tokens, ext, folder_path, rejected_path, chosen_at FROM save_history ORDER BY id DESC LIMIT $n",
            r => new SaveHistoryRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                DateTime.Parse(r.GetString(4))), ("$n", limit));

    // ---------------------------------------------------------------- 受け取りボックス（§1.6）

    public List<string> GetInboxSources() =>
        Query("SELECT path FROM inbox_sources WHERE enabled=1 ORDER BY id", r => r.GetString(0));

    public void AddInboxSource(string path) =>
        Exec("INSERT INTO inbox_sources(path) VALUES ($p) ON CONFLICT(path) DO UPDATE SET enabled=1", null, ("$p", path));

    public void RemoveInboxSource(string path) => Exec("DELETE FROM inbox_sources WHERE path=$p", null, ("$p", path));

    public List<InboxRule> GetInboxRules() =>
        Query("SELECT id, name_pattern, ext, target_path, auto_move, priority FROM inbox_rules ORDER BY priority, id",
            r => new InboxRule(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), r.GetInt64(4) != 0, (int)r.GetInt64(5)));

    public void AddInboxRule(string? namePattern, string? ext, string targetPath, bool autoMove, int priority = 100) =>
        Exec("INSERT INTO inbox_rules(name_pattern, ext, target_path, auto_move, priority) VALUES ($n, $e, $t, $a, $p)", null,
            ("$n", namePattern), ("$e", ext), ("$t", targetPath), ("$a", autoMove ? 1 : 0), ("$p", priority));

    public void SetInboxRuleAutoMove(long id, bool autoMove) =>
        Exec("UPDATE inbox_rules SET auto_move=$a WHERE id=$id", null, ("$a", autoMove ? 1 : 0), ("$id", id));

    public void DeleteInboxRule(long id) => Exec("DELETE FROM inbox_rules WHERE id=$id", null, ("$id", id));

    public void DismissInboxItem(string path) =>
        Exec("INSERT INTO inbox_dismissed(path, at) VALUES ($p, $t) ON CONFLICT(path) DO UPDATE SET at=excluded.at", null,
            ("$p", path), ("$t", DateTime.Now.ToString("o")));

    public HashSet<string> GetDismissedInboxItems()
    {
        // 古い記録は消す（ファイル自体も移動・削除されていることが多い）
        Exec("DELETE FROM inbox_dismissed WHERE at < $t", null, ("$t", DateTime.Now.AddDays(-60).ToString("o")));
        return Query("SELECT path FROM inbox_dismissed", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- ブックマーク（02_機能設計 §4.3）

    public Bookmark? FindBookmarkByFileId(long volumeSerial, byte[] fileId) =>
        Query("SELECT id, token, volume_serial, file_id, last_path, label FROM bookmarks WHERE volume_serial=$v AND file_id=$f",
            MapBookmark, ("$v", volumeSerial), ("$f", fileId)).FirstOrDefault();

    public Bookmark? GetBookmark(string token) =>
        Query("SELECT id, token, volume_serial, file_id, last_path, label FROM bookmarks WHERE token=$t", MapBookmark, ("$t", token)).FirstOrDefault();

    public void AddBookmark(string token, long volumeSerial, byte[] fileId, string path, string? label) =>
        Exec("INSERT INTO bookmarks(token, volume_serial, file_id, last_path, label, created_at) VALUES ($t, $v, $f, $p, $l, $at)", null,
            ("$t", token), ("$v", volumeSerial), ("$f", fileId), ("$p", path), ("$l", label), ("$at", DateTime.Now.ToString("o")));

    public void UpdateBookmarkPath(long id, string path) =>
        Exec("UPDATE bookmarks SET last_path=$p WHERE id=$id", null, ("$p", path), ("$id", id));

    static Bookmark MapBookmark(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), (byte[])r[3], r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));

    // ---------------------------------------------------------------- レジストリの退避（02_機能設計 §7.3）

    public void BackupRegistryValue(string keyPath, string valueName, string? value, bool existed) =>
        Exec("""
             INSERT INTO shell_backup(key_path, value_name, value, existed) VALUES ($k, $n, $v, $e)
             ON CONFLICT(key_path, value_name) DO NOTHING
             """, null, ("$k", keyPath), ("$n", valueName), ("$v", value), ("$e", existed ? 1 : 0));

    public List<(string KeyPath, string ValueName, string? Value, bool Existed)> GetRegistryBackups() =>
        Query("SELECT key_path, value_name, value, existed FROM shell_backup",
            r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0));

    public void ClearRegistryBackups() => Exec("DELETE FROM shell_backup");

    // ---------------------------------------------------------------- 低レベル

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
