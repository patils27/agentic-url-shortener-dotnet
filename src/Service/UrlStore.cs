// SQLite persistence layer (Microsoft.Data.Sqlite, no ORM).
//
// Thread-safe: a single connection guarded by a lock. The schema is created
// lazily on first use so constructing a store never creates stray files.

using Microsoft.Data.Sqlite;

namespace AgenticUrlShortener.Service;

public sealed class UrlStore : IUrlRepository, IDisposable
{
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS urls (
  code        TEXT PRIMARY KEY,
  url         TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  expires_at  TEXT,
  owner_id    TEXT
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
CREATE TABLE IF NOT EXISTS idempotency_requests (
  owner_id    TEXT NOT NULL,
  key         TEXT NOT NULL,
  request_hash TEXT NOT NULL,
  body        TEXT NOT NULL,
  expires_at  INTEGER NOT NULL,
  PRIMARY KEY(owner_id, key)
);
CREATE INDEX IF NOT EXISTS idx_idempotency_expiry ON idempotency_requests (expires_at);";

    private readonly string _dbPath;
    private readonly object _lock = new();
    private SqliteConnection? _conn;
    private bool _initialized;
    private bool _disposed;
    private readonly TimeProvider _timeProvider;
    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    public UrlStore(string dbPath, TimeProvider? timeProvider = null)
    {
        _dbPath = dbPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // -- schema ----------------------------------------------------------
    private void Ensure()
    {
        // Lazy connection: constructing the store never creates DB files.
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            try
            {
                connection.Open();
                // Serialize schema migration across instances using the same database.
                using var transaction = connection.BeginTransaction(deferred: false);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = Schema;
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA table_info(urls)";
                bool hasOwner = false;
                using (var columns = cmd.ExecuteReader())
                    while (columns.Read()) hasOwner |= columns.GetString(1) == "owner_id";
                if (!hasOwner)
                {
                    cmd.CommandText = "ALTER TABLE urls ADD COLUMN owner_id TEXT";
                    cmd.ExecuteNonQuery();
                }
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_urls_owner ON urls (owner_id, created_at)";
                cmd.ExecuteNonQuery();
                transaction.Commit();
                // Legacy links remain unowned; legacy unscoped idempotency rows are never replayed.
                _conn = connection;
                _initialized = true;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    private SqliteConnection Conn
    {
        get { Ensure(); return _conn!; }
    }

    /// <summary>Check database access without loading stored URL records.</summary>
    public void CheckReady()
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM urls LIMIT 1";
            cmd.ExecuteScalar();
        }
    }

    // -- urls ------------------------------------------------------------
    /// <summary>Insert a short URL. Returns false on code collision.</summary>
    public bool Create(string code, string url, string createdAt, string? expiresAt = null, string? ownerId = null)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = Conn.CreateCommand();
                cmd.CommandText = "INSERT INTO urls(code, url, created_at, expires_at, owner_id) VALUES ($code, $url, $created, $expires, $owner)";
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$created", createdAt);
                cmd.Parameters.AddWithValue("$expires", (object?)expiresAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
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

    public UrlRow? GetOwned(string code, string ownerId)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT code, url, created_at, expires_at FROM urls WHERE code = $code AND owner_id = $owner";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$owner", ownerId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new UrlRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
        }
    }

    public List<UrlRecordDto> ListOwned(string ownerId)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = @"SELECT u.code, u.url, u.created_at, u.expires_at, COUNT(c.id)
FROM urls u LEFT JOIN clicks c ON c.code = u.code
WHERE u.owner_id = $owner GROUP BY u.code ORDER BY u.created_at DESC";
            cmd.Parameters.AddWithValue("$owner", ownerId);
            using var reader = cmd.ExecuteReader();
            var rows = new List<UrlRecordDto>();
            while (reader.Read())
                rows.Add(new UrlRecordDto
                {
                    Code = reader.GetString(0), Url = reader.GetString(1), CreatedAt = reader.GetString(2),
                    ExpiresAt = reader.IsDBNull(3) ? null : reader.GetString(3), Clicks = reader.GetInt32(4),
                });
            return rows;
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

    public bool DeleteOwned(string code, string ownerId)
    {
        lock (_lock)
        {
            using var transaction = Conn.BeginTransaction(deferred: false);
            using var cmd = Conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM urls WHERE code = $code AND owner_id = $owner";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$owner", ownerId);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                using var cmd2 = Conn.CreateCommand();
                cmd2.Transaction = transaction;
                cmd2.CommandText = "DELETE FROM clicks WHERE code = $code";
                cmd2.Parameters.AddWithValue("$code", code);
                cmd2.ExecuteNonQuery();
            }
            transaction.Commit();
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

    public List<ClickRow> ClicksFor(string code, string? ownerId = null)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = @"SELECT c.code, c.ts, c.referrer, c.user_agent, c.ip FROM clicks c
WHERE c.code = $code AND ($owner IS NULL OR EXISTS
    (SELECT 1 FROM urls u WHERE u.code = c.code AND u.owner_id = $owner)) ORDER BY c.ts";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
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

    public int ClickCount(string code, string? ownerId = null)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*) FROM clicks c WHERE c.code = $code AND
($owner IS NULL OR EXISTS (SELECT 1 FROM urls u WHERE u.code = c.code AND u.owner_id = $owner))";
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // -- idempotency -----------------------------------------------------
    /// <summary>Atomically create the URL and save its replay response across connections.</summary>
    public CreateUrlResult CreateWithIdempotency(
        string code, string url, string createdAt, string? expiresAt, string ownerId,
        string? key, string requestHash, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        lock (_lock)
        {
            if (key is null)
                return new(Create(code, url, createdAt, expiresAt, ownerId)
                    ? CreateUrlOutcome.Created : CreateUrlOutcome.CodeConflict);

            // Acquire the write reservation before reading the key. A second connection
            // waits here and then observes the committed replay instead of creating a URL.
            using var transaction = Conn.BeginTransaction(deferred: false);
            var now = _timeProvider.GetUtcNow();
            using var cleanup = Conn.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM idempotency_requests WHERE expires_at <= $now";
            cleanup.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            cleanup.ExecuteNonQuery();
            using var lookup = Conn.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT request_hash, body FROM idempotency_requests WHERE owner_id = $owner AND key = $key";
            lookup.Parameters.AddWithValue("$owner", ownerId);
            lookup.Parameters.AddWithValue("$key", key);
            IdempotencyRecord? saved = null;
            using (var reader = lookup.ExecuteReader())
                if (reader.Read()) saved = new(reader.GetString(0), reader.GetString(1));
            if (saved is not null)
            {
                transaction.Commit();
                return saved.RequestHash == requestHash
                    ? new(CreateUrlOutcome.Replay, saved.Body) : new(CreateUrlOutcome.IdempotencyConflict);
            }

            try
            {
                using var insert = Conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO urls(code, url, created_at, expires_at, owner_id) VALUES ($code, $url, $created, $expires, $owner)";
                insert.Parameters.AddWithValue("$code", code);
                insert.Parameters.AddWithValue("$url", url);
                insert.Parameters.AddWithValue("$created", createdAt);
                insert.Parameters.AddWithValue("$expires", (object?)expiresAt ?? DBNull.Value);
                insert.Parameters.AddWithValue("$owner", ownerId);
                insert.ExecuteNonQuery();

                using var save = Conn.CreateCommand();
                save.Transaction = transaction;
                save.CommandText = @"INSERT INTO idempotency_requests(owner_id, key, request_hash, body, expires_at)
VALUES ($owner, $key, $hash, $body, $expires)";
                save.Parameters.AddWithValue("$owner", ownerId);
                save.Parameters.AddWithValue("$key", key);
                save.Parameters.AddWithValue("$hash", requestHash);
                save.Parameters.AddWithValue("$body", body);
                save.Parameters.AddWithValue("$expires", now.Add(IdempotencyRetention).ToUnixTimeSeconds());
                save.ExecuteNonQuery();
                transaction.Commit();
                return new(CreateUrlOutcome.Created);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Disposing the transaction rolls back both inserts on a collision.
                return new(CreateUrlOutcome.CodeConflict);
            }
        }
    }

    public IdempotencyRecord? GetIdempotency(string ownerId, string key)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = @"SELECT request_hash, body FROM idempotency_requests
WHERE owner_id = $owner AND key = $key AND expires_at > $now";
            cmd.Parameters.AddWithValue("$owner", ownerId);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _conn?.Dispose();
            _conn = null;
        }
    }
}
