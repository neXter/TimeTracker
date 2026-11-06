using Microsoft.Data.Sqlite;

namespace TimeTracker.Core;

/// <summary>One step of the database layout history. Versions are consecutive: 1, 2, 3, ...</summary>
public sealed record Migration(int Version, string Sql);

/// <summary>The database was written by a newer TimeTracker than this one. Opening it could damage it, so we refuse.</summary>
public sealed class NewerDatabaseException : Exception
{
    public int FoundVersion { get; }
    public int SupportedVersion { get; }
    public NewerDatabaseException(int found, int supported)
        : base($"Database schema {found} is newer than this version supports ({supported}).") { FoundVersion = found; SupportedVersion = supported; }
}

/// <summary>
/// Thin, thread-safe wrapper around one SQLite connection. SQL uses named parameters $0, $1, ... bound
/// positionally from the <c>args</c> array.
/// </summary>
public sealed class Db : IDisposable
{
    /// <summary>
    /// The layout history. NEVER edit an existing entry once released; add a new one at the end instead, e.g.
    /// <c>new(2, "ALTER TABLE app ADD COLUMN notes TEXT;")</c>. Existing installations are upgraded in place (after a backup).
    /// </summary>
    public static readonly IReadOnlyList<Migration> DefaultMigrations = new List<Migration>
    {
        new(1, BaseSchemaSql),
    };

    public static int SchemaVersion => DefaultMigrations[^1].Version;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    static Db() { SQLitePCL.Batteries_V2.Init(); }   // use the SQLite that ships with Windows (winsqlite3.dll)

    public string Path { get; }

    public Db(string path) : this(path, DefaultMigrations) { }

    public Db(string path, IReadOnlyList<Migration> migrations)
    {
        Path = path;
        _migrations = migrations;
        if (path != ":memory:")
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public static Db InMemory() => new(":memory:");

    private SqliteCommand Cmd(string sql, object?[] args)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            c.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return c;
    }

    public int Exec(string sql, params object?[] args)
    {
        lock (_lock) { using var c = Cmd(sql, args); return c.ExecuteNonQuery(); }
    }

    public long Insert(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql + "; SELECT last_insert_rowid();", args);
            return Convert.ToInt64(c.ExecuteScalar());
        }
    }

    public T? Scalar<T>(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql, args);
            var v = c.ExecuteScalar();
            if (v == null || v is DBNull) return default;
            var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(v, t);
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql, args);
            using var r = c.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>Runs <paramref name="work"/> inside a transaction while holding the connection lock.</summary>
    public void Transaction(Action work)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            try { work(); tx.Commit(); }
            catch { tx.Rollback(); throw; }
        }
    }

    public string? GetMeta(string key) => Scalar<string>("SELECT value FROM meta WHERE key=$0", key);

    public void SetMeta(string key, string? value)
    {
        if (value == null) Exec("DELETE FROM meta WHERE key=$0", key);
        else Exec("INSERT INTO meta(key,value) VALUES($0,$1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }

    /// <summary>Brings the database to the newest layout. Fresh files get everything; old files are backed up first.</summary>
    private void Migrate()
    {
        int target = _migrations[^1].Version;
        int current = int.TryParse(GetMetaSafe("schema_version"), out var v) ? v : 0;
        if (current > target) throw new NewerDatabaseException(current, target);
        if (current < target)
        {
            if (current > 0 && Path != ":memory:") Backup($"pre-v{target}");
            foreach (var m in _migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
            {
                Transaction(() =>
                {
                    Exec(m.Sql);
                    Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)");
                    SetMeta("schema_version", m.Version.ToString());
                });
            }
        }

        // Built-in default: Java Minecraft runs as javaw.exe, which has no useful product name.
        if (GetMeta("seed_v1") == null && TableExists("app_alias"))
        {
            Exec("INSERT INTO app_alias(match_kind,match_value,title_contains,display_name,hidden) VALUES('exe_name','javaw.exe','Minecraft','Minecraft',0)");
            SetMeta("seed_v1", "1");
        }
    }

    private bool TableExists(string name) => Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$0", name) > 0;

    private string? GetMetaSafe(string key) => TableExists("meta") ? GetMeta(key) : null;

    /// <summary>Consistent copy of the database next to it (tracker.db.pre-v2.bak). Keeps only the newest few.</summary>
    public string? Backup(string label)
    {
        if (Path == ":memory:") return null;
        try
        {
            string dest = $"{Path}.{label}.bak";
            if (File.Exists(dest)) File.Delete(dest);
            Exec($"VACUUM INTO '{dest.Replace("'", "''")}'");
            var old = new DirectoryInfo(System.IO.Path.GetDirectoryName(Path)!).GetFiles(System.IO.Path.GetFileName(Path) + ".*.bak")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(3);
            foreach (var f in old) { try { f.Delete(); } catch { } }
            return dest;
        }
        catch { return null; }      // a failed backup must not prevent the app from starting
    }

    private const string BaseSchemaSql = @"
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);

CREATE TABLE IF NOT EXISTS app (
  id             INTEGER PRIMARY KEY,
  exe_path       TEXT NOT NULL,
  exe_name       TEXT NOT NULL,
  detected_name  TEXT NOT NULL,
  source         TEXT NOT NULL,
  title_rule_id  INTEGER NOT NULL DEFAULT 0,
  first_seen_utc INTEGER NOT NULL,
  UNIQUE (exe_path, title_rule_id)
);

CREATE TABLE IF NOT EXISTS app_alias (
  id             INTEGER PRIMARY KEY,
  match_kind     TEXT NOT NULL CHECK (match_kind IN ('exe_path','exe_name')),
  match_value    TEXT NOT NULL,
  title_contains TEXT,
  display_name   TEXT NOT NULL,
  hidden         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS device_interval (
  id        INTEGER PRIMARY KEY,
  state     TEXT NOT NULL CHECK (state IN ('active','idle','locked','untracked')),
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS foreground_interval (
  id        INTEGER PRIMARY KEY,
  app_id    INTEGER NOT NULL REFERENCES app(id),
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL,
  title     TEXT
);

CREATE TABLE IF NOT EXISTS open_interval (
  id        INTEGER PRIMARY KEY,
  app_id    INTEGER NOT NULL REFERENCES app(id),
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_fg_start   ON foreground_interval(start_utc);
CREATE INDEX IF NOT EXISTS ix_fg_end     ON foreground_interval(end_utc);
CREATE INDEX IF NOT EXISTS ix_open_start ON open_interval(start_utc);
CREATE INDEX IF NOT EXISTS ix_open_end   ON open_interval(end_utc);
CREATE INDEX IF NOT EXISTS ix_dev_start  ON device_interval(start_utc);
CREATE INDEX IF NOT EXISTS ix_dev_end    ON device_interval(end_utc);
";

    /// <summary>Deletes every recorded interval and app, but keeps settings and custom names.</summary>
    public void DeleteTrackingData()
    {
        Transaction(() =>
        {
            Exec("DELETE FROM foreground_interval");
            Exec("DELETE FROM open_interval");
            Exec("DELETE FROM device_interval");
            Exec("DELETE FROM app");
            Exec("DELETE FROM meta WHERE key IN ('color_slots','dismissed_unnamed','auto_week_done','auto_month_done')");
        });
        Exec("VACUUM");
    }

    public void Dispose()
    {
        lock (_lock) { try { _conn.Close(); } catch { } _conn.Dispose(); }
        SqliteConnection.ClearAllPools();
    }
}

