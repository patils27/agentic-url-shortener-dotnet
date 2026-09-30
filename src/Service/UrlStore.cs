// SQLite persistence layer (Microsoft.Data.Sqlite, no ORM).
//
// Thread-safe: a single connection guarded by a lock. The schema is created
// lazily on first use so constructing a store never creates stray files.

using Microsoft.Data.Sqlite;

namespace AgenticUrlShortener.Service;

public sealed class UrlStore
{
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS urls (
  code        TEXT PRIMARY KEY,
  url         TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  expires_at  TEXT
);
CREATE TABLE IF NOT EXISTS clicks (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  code        TEXT NOT NULL,
  ts          TEXT NOT NULL,
  referrer    TEXT,
  user_agent  TEXT,
  ip          TEXT
);
CREATE INDEX IF NOT EXISTS idx_clicks_code_ts ON clicks (code, ts);
CREATE TABLE IF NOT EXISTS idempotency (
  key         TEXT PRIMARY KEY,
  body        TEXT NOT NULL,
  created_at  TEXT NOT NULL
);";

    private readonly string _dbPath;
    private readonly object _lock = new();
    private SqliteConnection? _conn;
    private bool _initialized;

    public UrlStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    // -- schema ----------------------------------------------------------
    private void Ensure()
    {
        // Lazy connection: constructing the store never creates DB files.
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private SqliteConnection Conn
    {
        get { Ensure(); return _conn!; }
    }

    // -- urls ------------------------------------------------------------
    /// <summary>Insert a short URL. Returns false on code collision.</summary>
    public bool Create(string code, string url, string createdAt, string? expiresAt = null)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = Conn.CreateCommand();
                cmd.CommandText = "INSERT INTO urls(code, url, created_at, expires_at) VALUES ($code, $url, $created, $expires)";
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$created", createdAt);
                cmd.Parameters.AddWithValue("$expires", (object?)expiresAt ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint violation
            {
                return false;
            }
        }
    }

    public UrlRow? Get(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT code, url, created_at, expires_at FROM urls WHERE code = $code";
            cmd.Parameters.AddWithValue("$code", code);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new UrlRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    public List<UrlRow> ListAll()
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT code, url, created_at, expires_at FROM urls ORDER BY created_at DESC";
            using var reader = cmd.ExecuteReader();
            var rows = new List<UrlRow>();
            while (reader.Read())
                rows.Add(new UrlRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            return rows;
        }
    }

    public bool Delete(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM urls WHERE code = $code";
            cmd.Parameters.AddWithValue("$code", code);
            var deleted = cmd.ExecuteNonQuery();
            using var cmd2 = Conn.CreateCommand();
            cmd2.CommandText = "DELETE FROM clicks WHERE code = $code";
            cmd2.Parameters.AddWithValue("$code", code);
            cmd2.ExecuteNonQuery();
            return deleted > 0;
        }
    }

    // -- clicks ----------------------------------------------------------
    public void RecordClick(string code, string ts, string? referrer, string? userAgent, string? ip)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO clicks(code, ts, referrer, user_agent, ip) VALUES ($code, $ts, $ref, $ua, $ip)";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$ref", (object?)referrer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ua", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ClickRow> ClicksFor(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT code, ts, referrer, user_agent, ip FROM clicks WHERE code = $code ORDER BY ts";
            cmd.Parameters.AddWithValue("$code", code);
            using var reader = cmd.ExecuteReader();
            var rows = new List<ClickRow>();
            while (reader.Read())
                rows.Add(new ClickRow(reader.GetString(0), reader.GetString(1),
                                      reader.IsDBNull(2) ? null : reader.GetString(2),
                                      reader.IsDBNull(3) ? null : reader.GetString(3),
                                      reader.IsDBNull(4) ? null : reader.GetString(4)));
            return rows;
        }
    }

    public int ClickCount(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clicks WHERE code = $code";
            cmd.Parameters.AddWithValue("$code", code);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // -- idempotency -----------------------------------------------------
    public void SaveIdempotency(string key, string body, string createdAt)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO idempotency(key, body, created_at) VALUES ($key, $body, $created)";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$body", body);
            cmd.Parameters.AddWithValue("$created", createdAt);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetIdempotency(string key)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT body FROM idempotency WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }
}
