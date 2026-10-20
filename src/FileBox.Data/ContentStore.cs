using Microsoft.Data.Sqlite;

namespace FileBox.Data;

public sealed record ContentHit(string Path, string Snippet);

/// <summary>
/// ファイルの本文の索引（%LOCALAPPDATA%\FileBox\content.db）。SQLite の FTS5（trigram）で日本語も語の区切りなしに探せる。
/// 利用者の権限で動く App が作るので、読めないファイルは索引に入らない（OS のアクセス権がそのまま効く）。
/// </summary>
public sealed class ContentStore : IDisposable
{
    static readonly string[] Migrations =
    {
        """
        CREATE TABLE docs (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL, indexed_at TEXT NOT NULL);
        CREATE VIRTUAL TABLE body USING fts5(text, tokenize='trigram');
        """,
    };

    readonly SqliteConnection _db;
    readonly object _gate = new();

    public ContentStore(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        long version = Scalar("SELECT MAX(version) FROM schema_version") ?? 0;
        for (int v = (int)version; v < Migrations.Length; v++)
        {
            Exec(Migrations[v]);
            Exec("INSERT INTO schema_version(version) VALUES ($v)", ("$v", v + 1));
        }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileBox", "content.db");

    public long Count => Scalar("SELECT COUNT(*) FROM docs") ?? 0;

    /// <summary>このファイルを（読み直して）索引に入れる必要があるか。サイズと更新日時が同じなら不要。</summary>
    public bool IsCurrent(string path, long size, DateTime modifiedUtc) =>
        Scalar("SELECT 1 FROM docs WHERE path=$p AND size=$s AND mtime=$m", ("$p", path), ("$s", size), ("$m", modifiedUtc.Ticks)) != null;

    public void Upsert(string path, long size, DateTime modifiedUtc, string text)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            long? id = ScalarTx(tx, "SELECT id FROM docs WHERE path=$p", ("$p", path));
            if (id is long existing)
            {
                ExecTx(tx, "UPDATE docs SET size=$s, mtime=$m, indexed_at=$t WHERE id=$id", ("$s", size), ("$m", modifiedUtc.Ticks), ("$t", DateTime.Now.ToString("o")), ("$id", existing));
                ExecTx(tx, "DELETE FROM body WHERE rowid=$id", ("$id", existing));
            }
            else
            {
                ExecTx(tx, "INSERT INTO docs(path, size, mtime, indexed_at) VALUES ($p, $s, $m, $t)", ("$p", path), ("$s", size), ("$m", modifiedUtc.Ticks), ("$t", DateTime.Now.ToString("o")));
                existing = ScalarTx(tx, "SELECT last_insert_rowid()")!.Value;
            }
            ExecTx(tx, "INSERT INTO body(rowid, text) VALUES ($id, $x)", ("$id", existing), ("$x", text));
            tx.Commit();
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            if (ScalarTx(tx, "SELECT id FROM docs WHERE path=$p", ("$p", path)) is long id)
            {
                ExecTx(tx, "DELETE FROM body WHERE rowid=$id", ("$id", id));
                ExecTx(tx, "DELETE FROM docs WHERE id=$id", ("$id", id));
            }
            tx.Commit();
        }
    }

    /// <summary>root の配下を索引から外す（対象フォルダーを外したとき）。</summary>
    public void RemoveUnder(string root)
    {
        string prefix = root.TrimEnd('\\') + "\\";
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            ExecTx(tx, "DELETE FROM body WHERE rowid IN (SELECT id FROM docs WHERE substr(path, 1, length($r)) = $r COLLATE NOCASE)", ("$r", prefix));
            ExecTx(tx, "DELETE FROM docs WHERE substr(path, 1, length($r)) = $r COLLATE NOCASE", ("$r", prefix));
            tx.Commit();
        }
    }

    /// <summary>索引にあるが、もう存在しないファイルを外す。</summary>
    public int Prune(Func<string, bool> exists)
    {
        var gone = Query("SELECT path FROM docs", r => r.GetString(0)).Where(p => !exists(p)).ToList();
        foreach (var p in gone) Remove(p);
        return gone.Count;
    }

    /// <summary>
    /// 本文に語をすべて含むファイル。3 文字以上の語は全文索引で、短い語は LIKE で絞る。
    /// Snippet は一致した前後の抜き出し（一致部分は「」で囲む）。
    /// </summary>
    public List<ContentHit> Search(string text, int limit = 200)
    {
        var terms = text.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries).Where(t => !t.Contains(':')).ToList();
        if (terms.Count == 0) return new();
        var args = new List<(string, object?)>();
        var where = new List<string>();
        var match = new List<string>();
        for (int i = 0; i < terms.Count; i++)
        {
            if (terms[i].Length >= 3) match.Add("\"" + terms[i].Replace("\"", "\"\"") + "\"");
            else
            {
                where.Add($"body.text LIKE $l{i} ESCAPE '^'");
                args.Add(($"$l{i}", "%" + terms[i].Replace("^", "^^").Replace("%", "^%").Replace("_", "^_") + "%"));
            }
        }
        if (match.Count > 0)
        {
            where.Insert(0, "body MATCH $m");
            args.Add(("$m", string.Join(" AND ", match)));
        }
        args.Add(("$n", limit));
        string sql = $"""
            SELECT docs.path, snippet(body, 0, '「', '」', '…', 16) FROM body JOIN docs ON docs.id = body.rowid
            WHERE {string.Join(" AND ", where)} LIMIT $n
            """;
        try { return Query(sql, r => new ContentHit(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)), args.ToArray()); }
        catch (SqliteException) { return new(); }
    }

    // ---------------------------------------------------------------- 低レベル

    void Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, null, args);
            cmd.ExecuteNonQuery();
        }
    }

    void ExecTx(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    long? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, null, args);
            return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v) : null;
        }
    }

    long? ScalarTx(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v) : null;
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, null, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    SqliteCommand Command(string sql, SqliteTransaction? tx, (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => _db.Dispose();
}
