// Code templates for scenario workspaces; src/Service is maintained separately.
//
// The greenfield implementer materializes the ``v1`` file set into its run
// workspace; the brownfield implementer evolves ``v1`` into ``v2`` (custom
// aliases, 410-for-expired bug fix, validators refactor). The templates below
// are real, compiling C# — the scenario workspaces build them with
// ``dotnet test`` and the results gate the release.
//
// v1 is derived from v2 via ToV1() using exact-match anchors.
// Both variants share C# templates, and unmatched patches fail explicitly.

using System.Text;

namespace AgenticUrlShortener.Agents;

public sealed record FileSpec(string Path, string Content);

public static class Codegen
{
    // =======================================================================
    // src/Shortener/Shortener.csproj
    // =======================================================================
    private const string ShortenerCsproj = @"<Project Sdk=""Microsoft.NET.Sdk.Web"">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Shortener</RootNamespace>
    <AssemblyName>Shortener</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.Data.Sqlite"" Version=""10.0.7"" />
    <PackageReference Include=""Swashbuckle.AspNetCore"" Version=""10.2.3"" />
    <PackageReference Include=""SQLitePCLRaw.lib.e_sqlite3"" Version=""2.1.13"" />
  </ItemGroup>

</Project>
";

    // =======================================================================
    // src/Shortener/ShortenerOptions.cs  (identical in v1 and v2)
    // =======================================================================
    private const string ShortenerOptionsCs = @"// Service configuration. Values come from the environment so tests and
// operators can override them without code changes:
//   SHORTENER_DB          SQLite path (default ""shortener.db""; "":memory:"" in tests)
//   SHORTENER_BASE_URL    public base URL used to build short_url (default http://localhost:8000)
//   SHORTENER_RATE_PER_MINUTE / SHORTENER_RATE_BURST  rate limiter tuning
//   SHORTENER_TRUSTED_PROXIES  proxy IPs allowed to supply forwarded client IPs
//   SHORTENER_API_KEYS    JSON mapping stable owner IDs to secret API keys

namespace Shortener;

public sealed class ShortenerOptions
{
    public string DbPath { get; set; } = ""shortener.db"";
    public string BaseUrl { get; set; } = ""http://localhost:8000"";
    public double RatePerMinute { get; set; } = 60.0;
    public int RateBurst { get; set; } = 10;
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();
    // Stable owner ID -> secret API key. Empty configuration disables management access.
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.Ordinal);

    public static ShortenerOptions FromEnvironment() => new()
    {
        DbPath = Environment.GetEnvironmentVariable(""SHORTENER_DB"") ?? ""shortener.db"",
        BaseUrl = (Environment.GetEnvironmentVariable(""SHORTENER_BASE_URL"") ?? ""http://localhost:8000"").TrimEnd('/'),
        RatePerMinute = double.TryParse(Environment.GetEnvironmentVariable(""SHORTENER_RATE_PER_MINUTE""),
                                        out var rpm) ? rpm : 60.0,
        RateBurst = int.TryParse(Environment.GetEnvironmentVariable(""SHORTENER_RATE_BURST""),
                                 out var burst) ? burst : 10,
        TrustedProxies = (Environment.GetEnvironmentVariable(""SHORTENER_TRUSTED_PROXIES"") ?? """")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ApiKeys = ReadApiKeys(Environment.GetEnvironmentVariable(""SHORTENER_API_KEYS"")),
    };

    public static Dictionary<string, string> ReadApiKeys(string? configuration)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(configuration)) return keys;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new FormatException();
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String ||
                    !keys.TryAdd(property.Name, property.Value.GetString()!))
                    throw new FormatException();
        }
        catch (Exception exc) when (exc is System.Text.Json.JsonException or FormatException)
        {
            // Never include secret configuration in startup errors.
            throw new InvalidOperationException(""SHORTENER_API_KEYS must be a JSON object mapping unique owner IDs to API keys."");
        }
        return keys;
    }
}
";

    // =======================================================================
    // src/Shortener/Models.cs — v2. v1 is derived by removing CustomAlias.
    // =======================================================================
    private const string ModelsCs = @"// Data models for the URL shortener API.
// JSON is serialized snake_case (short_url, created_at, ...) to preserve the
// API contract; naming is configured at the HTTP boundary.

namespace Shortener;

/// <summary>POST /api/urls request body.</summary>
public sealed class CreateUrlRequest
{
    public string Url { get; set; } = string.Empty;
    public string? CustomAlias { get; set; }
    public int? ExpiresInDays { get; set; }
}

/// <summary>POST /api/urls response body.</summary>
public sealed class ShortUrlResponse
{
    public string Code { get; set; } = string.Empty;
    public string ShortUrl { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? ExpiresAt { get; set; }
}

/// <summary>A stored short URL plus its click count.</summary>
public sealed class UrlRecordDto
{
    public string Code { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? ExpiresAt { get; set; }
    public int Clicks { get; set; }
}

public sealed class DayCount
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>GET /api/urls/{code}/stats response body.</summary>
public sealed class UrlStats
{
    public string Code { get; set; } = string.Empty;
    public int TotalClicks { get; set; }
    public List<DayCount> ClicksByDay { get; set; } = new();
    public Dictionary<string, int> Referrers { get; set; } = new();
    public Dictionary<string, int> UserAgents { get; set; } = new();
    public string? LastClickedAt { get; set; }
}

// ---- storage rows ----
public sealed record UrlRow(string Code, string Url, string CreatedAt, string? ExpiresAt);
public sealed record ClickRow(string Code, string Ts, string? Referrer, string? UserAgent, string? Ip);

public enum CreateUrlOutcome { Created, Replay, CodeConflict, IdempotencyConflict }
public sealed record CreateUrlResult(CreateUrlOutcome Outcome, string? Replay = null);
public sealed record IdempotencyRecord(string RequestHash, string Body);
";

    // =======================================================================
    // src/Shortener/Validators.cs  (v2 only — extracted from Program.cs in
    // the brownfield refactor so validation is shared and unit-testable)
    // =======================================================================
    private const string ValidatorsCs = @"// Shared input validation helpers.
//
// Extracted into a dedicated module (brownfield refactor) so that URL/alias
// validation lives in one place and can be unit-tested independently of HTTP.

using System.Text.RegularExpressions;

namespace Shortener;

public static class Validators
{
    private static readonly Regex AliasRegex =
        new(@""^[A-Za-z0-9_-]{3,32}$"", RegexOptions.Compiled);

    /// <summary>Validate and normalize a destination URL. Throws <see cref=""ArgumentException""/>.</summary>
    public static string ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException(""url must be a non-empty string"");
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException(""url must be an absolute http(s) URL"");
        return url;
    }

    /// <summary>Validate a custom alias. Throws <see cref=""ArgumentException""/>.</summary>
    public static string ValidateAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !AliasRegex.IsMatch(alias))
            throw new ArgumentException(""custom_alias must be 3-32 chars of [A-Za-z0-9_-]"");
        if (alias.Equals(""health"", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals(""ready"", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals(""api"", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals(""swagger"", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(""custom_alias is reserved for a service endpoint"");
        return alias;
    }
}
";

    // =======================================================================
    // src/Shortener/UrlStore.cs  (identical in v1 and v2 — custom aliases
    // reuse the ``code`` primary key, so no schema migration was needed)
    // =======================================================================
    private const string UrlStoreCs = @"// SQLite persistence layer (Microsoft.Data.Sqlite, no ORM).
//
// Thread-safe: a single connection guarded by a lock. The schema is created
// lazily on first use so constructing a store never creates stray files.

using Microsoft.Data.Sqlite;

namespace Shortener;

public sealed class UrlStore : IUrlRepository, IDisposable
{
    private const string Schema = @""
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
CREATE INDEX IF NOT EXISTS idx_idempotency_expiry ON idempotency_requests (expires_at);"";

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
            var connection = new SqliteConnection($""Data Source={_dbPath};Pooling=False"");
            try
            {
                connection.Open();
                // Serialize schema migration across instances using the same database.
                using var transaction = connection.BeginTransaction(deferred: false);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = Schema;
                cmd.ExecuteNonQuery();
                cmd.CommandText = ""PRAGMA table_info(urls)"";
                bool hasOwner = false;
                using (var columns = cmd.ExecuteReader())
                    while (columns.Read()) hasOwner |= columns.GetString(1) == ""owner_id"";
                if (!hasOwner)
                {
                    cmd.CommandText = ""ALTER TABLE urls ADD COLUMN owner_id TEXT"";
                    cmd.ExecuteNonQuery();
                }
                cmd.CommandText = ""CREATE INDEX IF NOT EXISTS idx_urls_owner ON urls (owner_id, created_at)"";
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
            cmd.CommandText = ""SELECT 1 FROM urls LIMIT 1"";
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
                cmd.CommandText = ""INSERT INTO urls(code, url, created_at, expires_at, owner_id) VALUES ($code, $url, $created, $expires, $owner)"";
                cmd.Parameters.AddWithValue(""$code"", code);
                cmd.Parameters.AddWithValue(""$url"", url);
                cmd.Parameters.AddWithValue(""$created"", createdAt);
                cmd.Parameters.AddWithValue(""$expires"", (object?)expiresAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue(""$owner"", (object?)ownerId ?? DBNull.Value);
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
            cmd.CommandText = ""SELECT code, url, created_at, expires_at FROM urls WHERE code = $code"";
            cmd.Parameters.AddWithValue(""$code"", code);
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
            cmd.CommandText = ""SELECT code, url, created_at, expires_at FROM urls WHERE code = $code AND owner_id = $owner"";
            cmd.Parameters.AddWithValue(""$code"", code);
            cmd.Parameters.AddWithValue(""$owner"", ownerId);
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
            cmd.CommandText = @""SELECT u.code, u.url, u.created_at, u.expires_at, COUNT(c.id)
FROM urls u LEFT JOIN clicks c ON c.code = u.code
WHERE u.owner_id = $owner GROUP BY u.code ORDER BY u.created_at DESC"";
            cmd.Parameters.AddWithValue(""$owner"", ownerId);
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
            cmd.CommandText = ""SELECT code, url, created_at, expires_at FROM urls ORDER BY created_at DESC"";
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
            cmd.CommandText = ""DELETE FROM urls WHERE code = $code AND owner_id = $owner"";
            cmd.Parameters.AddWithValue(""$code"", code);
            cmd.Parameters.AddWithValue(""$owner"", ownerId);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                using var cmd2 = Conn.CreateCommand();
                cmd2.Transaction = transaction;
                cmd2.CommandText = ""DELETE FROM clicks WHERE code = $code"";
                cmd2.Parameters.AddWithValue(""$code"", code);
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
            cmd.CommandText = ""INSERT INTO clicks(code, ts, referrer, user_agent, ip) VALUES ($code, $ts, $ref, $ua, $ip)"";
            cmd.Parameters.AddWithValue(""$code"", code);
            cmd.Parameters.AddWithValue(""$ts"", ts);
            cmd.Parameters.AddWithValue(""$ref"", (object?)referrer ?? DBNull.Value);
            cmd.Parameters.AddWithValue(""$ua"", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue(""$ip"", (object?)ip ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ClickRow> ClicksFor(string code, string? ownerId = null)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = @""SELECT c.code, c.ts, c.referrer, c.user_agent, c.ip FROM clicks c
WHERE c.code = $code AND ($owner IS NULL OR EXISTS
    (SELECT 1 FROM urls u WHERE u.code = c.code AND u.owner_id = $owner)) ORDER BY c.ts"";
            cmd.Parameters.AddWithValue(""$code"", code);
            cmd.Parameters.AddWithValue(""$owner"", (object?)ownerId ?? DBNull.Value);
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
            cmd.CommandText = @""SELECT COUNT(*) FROM clicks c WHERE c.code = $code AND
($owner IS NULL OR EXISTS (SELECT 1 FROM urls u WHERE u.code = c.code AND u.owner_id = $owner))"";
            cmd.Parameters.AddWithValue(""$code"", code);
            cmd.Parameters.AddWithValue(""$owner"", (object?)ownerId ?? DBNull.Value);
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
            cleanup.CommandText = ""DELETE FROM idempotency_requests WHERE expires_at <= $now"";
            cleanup.Parameters.AddWithValue(""$now"", now.ToUnixTimeSeconds());
            cleanup.ExecuteNonQuery();
            using var lookup = Conn.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = ""SELECT request_hash, body FROM idempotency_requests WHERE owner_id = $owner AND key = $key"";
            lookup.Parameters.AddWithValue(""$owner"", ownerId);
            lookup.Parameters.AddWithValue(""$key"", key);
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
                insert.CommandText = ""INSERT INTO urls(code, url, created_at, expires_at, owner_id) VALUES ($code, $url, $created, $expires, $owner)"";
                insert.Parameters.AddWithValue(""$code"", code);
                insert.Parameters.AddWithValue(""$url"", url);
                insert.Parameters.AddWithValue(""$created"", createdAt);
                insert.Parameters.AddWithValue(""$expires"", (object?)expiresAt ?? DBNull.Value);
                insert.Parameters.AddWithValue(""$owner"", ownerId);
                insert.ExecuteNonQuery();

                using var save = Conn.CreateCommand();
                save.Transaction = transaction;
                save.CommandText = @""INSERT INTO idempotency_requests(owner_id, key, request_hash, body, expires_at)
VALUES ($owner, $key, $hash, $body, $expires)"";
                save.Parameters.AddWithValue(""$owner"", ownerId);
                save.Parameters.AddWithValue(""$key"", key);
                save.Parameters.AddWithValue(""$hash"", requestHash);
                save.Parameters.AddWithValue(""$body"", body);
                save.Parameters.AddWithValue(""$expires"", now.Add(IdempotencyRetention).ToUnixTimeSeconds());
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
            cmd.CommandText = @""SELECT request_hash, body FROM idempotency_requests
WHERE owner_id = $owner AND key = $key AND expires_at > $now"";
            cmd.Parameters.AddWithValue(""$owner"", ownerId);
            cmd.Parameters.AddWithValue(""$key"", key);
            cmd.Parameters.AddWithValue(""$now"", _timeProvider.GetUtcNow().ToUnixTimeSeconds());
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
";

    // =======================================================================
    // src/Shortener/ClickAnalytics.cs  (identical in v1 and v2)
    // =======================================================================
    private const string ClickAnalyticsCs = @"// Click analytics aggregation (pure functions, no I/O).

namespace Shortener;

public static class ClickAnalytics
{
    public static UrlStats BuildStats(string code, List<ClickRow> clicks)
    {
        var byDay = new Dictionary<string, int>();
        var referrers = new Dictionary<string, int>();
        var userAgents = new Dictionary<string, int>();
        string? lastClicked = null;

        foreach (var click in clicks)
        {
            var ts = click.Ts ?? string.Empty;
            var day = ts.Length >= 10 ? ts[..10] : ts;
            byDay[day] = byDay.GetValueOrDefault(day) + 1;
            if (!string.IsNullOrEmpty(click.Referrer))
                referrers[click.Referrer] = referrers.GetValueOrDefault(click.Referrer) + 1;
            if (!string.IsNullOrEmpty(click.UserAgent))
                userAgents[click.UserAgent] = userAgents.GetValueOrDefault(click.UserAgent) + 1;
            if (lastClicked is null || string.CompareOrdinal(ts, lastClicked) > 0)
                lastClicked = ts;
        }

        return new UrlStats
        {
            Code = code,
            TotalClicks = clicks.Count,
            ClicksByDay = byDay.OrderBy(kv => kv.Key)
                               .Select(kv => new DayCount { Date = kv.Key, Count = kv.Value })
                               .ToList(),
            Referrers = referrers,
            UserAgents = userAgents,
            LastClickedAt = lastClicked,
        };
    }
}
";

    // =======================================================================
    // src/Shortener/RateLimiter.cs  (identical in v1 and v2)
    // =======================================================================
    private const string RateLimiterCs = @"// Per-IP token-bucket rate limiter (in-memory, thread-safe).

namespace Shortener;

public sealed class TokenBucket
{
    private readonly double _capacity;
    private double _tokens;
    private readonly double _refillPerSec;
    private double _updated = NowMonotonic();
    private readonly object _lock = new();

    public TokenBucket(double ratePerMinute, int burst)
    {
        _capacity = burst;
        _tokens = burst;
        _refillPerSec = ratePerMinute / 60.0;
    }

    private static double NowMonotonic() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    public (bool Allowed, double RetryAfter) Allow()
    {
        lock (_lock)
        {
            var now = NowMonotonic();
            _tokens = Math.Min(_capacity, _tokens + (now - _updated) * _refillPerSec);
            _updated = now;
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return (true, 0.0);
            }
            return (false, (1.0 - _tokens) / _refillPerSec);
        }
    }
}

public sealed class RateLimiter
{
    private readonly double _rate;
    private readonly int _burst;
    private readonly Dictionary<string, TokenBucket> _buckets = new();
    private readonly object _lock = new();

    public RateLimiter(double ratePerMinute = 60.0, int burst = 10)
    {
        _rate = ratePerMinute;
        _burst = burst;
    }

    public (bool Allowed, double RetryAfter) Allow(string key)
    {
        TokenBucket bucket;
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out bucket!))
            {
                bucket = new TokenBucket(_rate, _burst);
                _buckets[key] = bucket;
            }
        }
        return bucket.Allow();
    }
}
";

    // =======================================================================
    // Service support classes and HTTP entry point for the generated variants.
    //
    // NOTE: the catch-all ``/{code}`` redirect route is registered LAST so it
    // alongside the catch-all /{code} redirect.
    // =======================================================================
    private const string ApiKeyAuthenticationCs = @"using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Shortener;

/// <summary>Authenticate management API callers without storing their secrets in the database.</summary>
public sealed class ApiKeyAuthentication
{
    public const string HeaderName = ""X-Api-Key"";
    private readonly List<(string Owner, byte[] Hash)> _credentials = new();
    public bool IsConfigured => _credentials.Count > 0;

    public ApiKeyAuthentication(ShortenerOptions options)
    {
        var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (owner, secret) in options.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || owner.Trim() != owner ||
                string.IsNullOrEmpty(secret) || secret.Length is < 32 or > 512 ||
                secret.Any(c => c < '!' || c > '~') || !uniqueKeys.Add(secret))
                throw new InvalidOperationException(
                    ""API keys must be unique, 32–512 printable ASCII characters, with nonempty owner IDs of at most 128 characters."");
            _credentials.Add((owner, SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
        }
    }

    public string? Authenticate(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 512) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        string? owner = null;
        foreach (var credential in _credentials)
            if (CryptographicOperations.FixedTimeEquals(hash, credential.Hash))
                owner = credential.Owner;
        return owner;
    }

    public static string OwnerOf(HttpContext context) =>
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException(""Authenticated owner is required."");

    public static void ProtectManagementApi(WebApplication app)
    {
        // Validate configured credentials at startup; no anonymous management fallback.
        var authentication = app.Services.GetRequiredService<ApiKeyAuthentication>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(""/api""))
            {
                if (!authentication.IsConfigured)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { detail = ""API authentication is not configured"" });
                    return;
                }
                var values = context.Request.Headers[HeaderName];
                var owner = values.Count == 1 ? authentication.Authenticate(values[0]) : null;
                if (owner is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = ""ApiKey"";
                    await context.Response.WriteAsJsonAsync(new { detail = ""a valid API key is required"" });
                    return;
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, owner) }, ""ApiKey""));
            }
            await next();
        });
    }
}
";

    private const string IUrlRepositoryCs = @"namespace Shortener;

/// <summary>Persistence operations needed by URL use cases, independent of SQLite.</summary>
public interface IUrlRepository
{
    void CheckReady();
    UrlRow? Get(string code);
    UrlRow? GetOwned(string code, string ownerId);
    List<UrlRecordDto> ListOwned(string ownerId);
    bool DeleteOwned(string code, string ownerId);
    List<ClickRow> ClicksFor(string code, string ownerId);
    int ClickCount(string code, string ownerId);
    void RecordClick(string code, string ts, string? referrer, string? userAgent, string? ip);
    IdempotencyRecord? GetIdempotency(string ownerId, string key);

    // Keep the URL and replay response in one atomic persistence operation.
    CreateUrlResult CreateWithIdempotency(string code, string url, string createdAt,
        string? expiresAt, string ownerId, string? key, string requestHash, string body);
}
";

    private const string UrlServiceCs = @"using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shortener;

public enum UrlCreationStatus { Created, Replay, InvalidRequest, InvalidIdempotencyKey, IdempotencyConflict, CodeConflict, AllocationFailed }
public sealed record UrlCreationResult(UrlCreationStatus Status, ShortUrlResponse? Value = null,
    string? ReplayJson = null, string? Error = null);
public enum RedirectStatus { Found, Unknown, Expired }
public sealed record RedirectResult(RedirectStatus Status, string? Destination = null);
public sealed record ClickMetadata(string? Referrer, string? UserAgent, string? Ip);

/// <summary>URL business operations. No dependency on HTTP, the DI container, or SQLite.</summary>
public sealed class UrlService(IUrlRepository repository, ShortenerOptions options, TimeProvider clock)
{
    private const string Alphabet = ""abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"";
    private readonly string _baseUrl = options.BaseUrl.TrimEnd('/');
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public UrlCreationResult Create(string owner, CreateUrlRequest request, string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        string url;
        string? alias;
        try
        {
            url = Validators.ValidateUrl(request.Url);
            alias = string.IsNullOrEmpty(request.CustomAlias) ? null : Validators.ValidateAlias(request.CustomAlias);
        }
        catch (ArgumentException error)
        {
            return new(UrlCreationStatus.InvalidRequest, Error: error.Message);
        }
        if (request.ExpiresInDays is < 1 or > 3650)
            return new(UrlCreationStatus.InvalidRequest, Error: ""expires_in_days must be between 1 and 3650"");
        if (idempotencyKey is not null && (idempotencyKey.Length is < 1 or > 128 ||
            idempotencyKey.Any(c => c < '!' || c > '~')))
            return new(UrlCreationStatus.InvalidIdempotencyKey, Error: ""Idempotency-Key must be 1–128 printable ASCII characters without spaces"");

        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            url, custom_alias = alias, expires_in_days = request.ExpiresInDays,
        })));
        if (idempotencyKey is not null && repository.GetIdempotency(owner, idempotencyKey) is { } saved)
            return saved.RequestHash == requestHash
                ? new(UrlCreationStatus.Replay, ReplayJson: saved.Body)
                : new(UrlCreationStatus.IdempotencyConflict, Error: ""Idempotency-Key was already used with a different request"");

        string code;
        if (alias is not null) code = alias;
        else
        {
            code = string.Empty;
            for (var i = 0; i < 10; i++)
            {
                var candidate = RandomNumberGenerator.GetString(Alphabet, 7);
                if (repository.Get(candidate) is null) { code = candidate; break; }
            }
            if (code.Length == 0)
                return new(UrlCreationStatus.AllocationFailed, Error: ""could not allocate a short code"");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var response = new ShortUrlResponse
        {
            Code = code, ShortUrl = $""{_baseUrl}/{code}"", Url = url,
            CreatedAt = now.ToString(""o""),
            ExpiresAt = request.ExpiresInDays is { } days ? now.AddDays(days).ToString(""o"") : null,
        };
        var result = repository.CreateWithIdempotency(code, url, response.CreatedAt, response.ExpiresAt,
            owner, idempotencyKey, requestHash, JsonSerializer.Serialize(response, JsonOptions));
        return result.Outcome switch
        {
            CreateUrlOutcome.Created => new(UrlCreationStatus.Created, Value: response),
            CreateUrlOutcome.Replay => new(UrlCreationStatus.Replay, ReplayJson: result.Replay),
            CreateUrlOutcome.IdempotencyConflict => new(UrlCreationStatus.IdempotencyConflict,
                Error: ""Idempotency-Key was already used with a different request""),
            CreateUrlOutcome.CodeConflict => new(UrlCreationStatus.CodeConflict,
                Error: ""short code already in use; retry with a new code""),
            _ => throw new InvalidOperationException(""Unknown persistence outcome.""),
        };
    }

    public List<UrlRecordDto> List(string owner) => repository.ListOwned(owner);

    public UrlRecordDto? GetOwned(string code, string owner)
    {
        var row = repository.GetOwned(code, owner);
        return row is null ? null : new UrlRecordDto
        {
            Code = row.Code, Url = row.Url, CreatedAt = row.CreatedAt,
            ExpiresAt = row.ExpiresAt, Clicks = repository.ClickCount(code, owner),
        };
    }

    public UrlStats? Stats(string code, string owner) => repository.GetOwned(code, owner) is null
        ? null : ClickAnalytics.BuildStats(code, repository.ClicksFor(code, owner));

    public bool Delete(string code, string owner) => repository.DeleteOwned(code, owner);

    public RedirectResult Redirect(string code, ClickMetadata click)
    {
        var row = repository.Get(code);
        if (row is null) return new(RedirectStatus.Unknown);
        var now = clock.GetUtcNow();
        if (row.ExpiresAt is not null &&
            DateTimeOffset.TryParse(row.ExpiresAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expires) && expires <= now)
            return new(RedirectStatus.Expired);
        repository.RecordClick(code, now.UtcDateTime.ToString(""o""), click.Referrer, click.UserAgent, click.Ip);
        return new(RedirectStatus.Found, row.Url);
    }

    public void CheckReady() => repository.CheckReady();
}
";

    private const string ApiExceptionHandlerCs = @"using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Shortener;

/// <summary>Maps request failures to safe responses; exception details stay in server logs.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        var readiness = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint?
            .Metadata.GetMetadata<ReadinessEndpoint>() is not null;
        var status = readiness ? StatusCodes.Status503ServiceUnavailable : exception switch
        {
            BadHttpRequestException { StatusCode: 400 or 413 or 415 } badRequest => badRequest.StatusCode,
            // Busy, locked, or unable to open the database: the service cannot currently serve requests.
            SqliteException { SqliteErrorCode: 5 or 6 or 14 } => StatusCodes.Status503ServiceUnavailable,
            TimeoutException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, new EventId(1001, ""RequestFailed""),
            exception, ""Request failed with status {StatusCode}. Correlation ID: {CorrelationId}"",
            status, context.TraceIdentifier);

        context.Response.StatusCode = status;
        if (readiness)
        {
            // Preserve the readiness probe contract used by deployment checks.
            await context.Response.WriteAsJsonAsync(new
            {
                status = ""degraded"", db = ""error"", correlation_id = context.TraceIdentifier,
            }, cancellationToken);
            return true;
        }

        var (title, detail) = status switch
        {
            400 => (""Bad Request"", ""The request body or parameters are invalid.""),
            413 => (""Content Too Large"", ""The request body exceeds the allowed size.""),
            415 => (""Unsupported Media Type"", ""The request content type is not supported.""),
            503 => (""Service Unavailable"", ""The service is temporarily unavailable. Try again later.""),
            _ => (""Internal Server Error"", ""An unexpected error occurred.""),
        };
        await Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { [""correlation_id""] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}

internal sealed class ReadinessEndpoint;
";

    private const string RequestCorrelationMiddlewareCs = @"using System.Diagnostics;

namespace Shortener;

/// <summary>Assigns a server-generated ID to each request and its logging scope.</summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    public const string HeaderName = ""X-Correlation-ID"";

    public async Task InvokeAsync(HttpContext context)
    {
        // Do not trust caller-supplied IDs as log fields or response headers.
        var correlationId = Guid.NewGuid().ToString(""N"");
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [""CorrelationId""] = correlationId,
            [""TraceId""] = Activity.Current?.TraceId.ToString(),
        });
        await next(context);
    }
}
";

    private const string SwaggerDocumentationCs = @"using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Shortener;

public static class SwaggerDocumentation
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(""v1"", new OpenApiInfo
            {
                Title = ""URL Shortener API"",
                Version = ""v1"",
                Description = ""Configure SHORTENER_API_KEYS on the server, then use Authorize to enter your key. "" +
                    ""Management endpoints require X-Api-Key. Health checks and redirects are public."",
            });
            options.AddSecurityDefinition(""ApiKey"", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyAuthentication.HeaderName,
                Description = ""Enter the API key only, without a Bearer prefix."",
            });
            options.DocumentFilter<ManagementApiDocumentFilter>();
        });
        // Use the minimal API's serializer settings so schemas match its snake_case payloads.
        services.AddSingleton<ISerializerDataContractResolver>(sp => new JsonSerializerDataContractResolver(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions));
        return services;
    }

    public static void UseApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint(""v1/swagger.json"", ""URL Shortener API v1"");
            options.DocumentTitle = ""URL Shortener API"";
            // Use bundled assets only and do not send the document to an external validator.
            options.ConfigObject.ValidatorUrl = null;
            options.ConfigObject.PersistAuthorization = false;
        });
        app.MapGet(""/"", () => Results.Redirect(""swagger/index.html"")).ExcludeFromDescription();
    }
}

/// <summary>Describe the same /api boundary enforced by API-key middleware.</summary>
public sealed class ManagementApiDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var (path, item) in document.Paths)
        {
            if (!path.StartsWith(""/api/"", StringComparison.OrdinalIgnoreCase) || item.Operations is null)
                continue;
            foreach (var operation in item.Operations.Values)
            {
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(""ApiKey"", document)] = [],
                }];
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd(""401"", new OpenApiResponse { Description = ""Missing or invalid API key."" });
                operation.Responses.TryAdd(""503"", new OpenApiResponse { Description = ""Authentication is not configured or storage is unavailable."" });
                operation.Responses.TryAdd(""429"", new OpenApiResponse { Description = ""Rate limit exceeded. Retry after the Retry-After header interval."" });
            }
        }

        if (document.Paths.TryGetValue(""/api/urls"", out var urls) &&
            urls.Operations?.TryGetValue(HttpMethod.Post, out var create) == true)
        {
            create.Parameters ??= [];
            create.Parameters.Add(new OpenApiParameter
            {
                Name = ""Idempotency-Key"",
                In = ParameterLocation.Header,
                Required = false,
                Description = ""Optional replay key scoped to your owner. Reusing it with changed input returns 422."",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 },
            });
        }
    }
}
";

    private const string SwaggerTestsCs = @"using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shortener;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class SwaggerTests
{
    [Fact]
    public async Task DevelopmentServesBundledUiAndRedirectsHomeWithoutAuthentication()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment(""Development""));
        using var client = development.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var home = await client.GetAsync(""/"");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal(""swagger/index.html"", home.Headers.Location?.OriginalString);
        foreach (var path in new[] { ""/swagger/index.html"", ""/swagger/swagger-ui-bundle.js"", ""/swagger/swagger-ui.css"" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(""/"")]
    [InlineData(""/swagger/index.html"")]
    [InlineData(""/swagger/v1/swagger.json"")]
    public async Task ProductionDoesNotExposeDocumentation(string path)
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment(""Production""));
        using var client = production.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DocumentDescribesActualPayloadsResponsesAndManagementOnlySecurity()
    {
        using var factory = new ShortenerTestFactory();
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment(""Development""));
        using var client = development.CreateClient();
        using var response = await client.GetAsync(""/swagger/v1/swagger.json"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ShortenerTestFactory.TestKey, text);
        using var json = JsonDocument.Parse(text);
        var document = json.RootElement;
        var scheme = document.GetProperty(""components"").GetProperty(""securitySchemes"").GetProperty(""ApiKey"");
        Assert.Equal(""apiKey"", scheme.GetProperty(""type"").GetString());
        Assert.Equal(""header"", scheme.GetProperty(""in"").GetString());
        Assert.Equal(ApiKeyAuthentication.HeaderName, scheme.GetProperty(""name"").GetString());
        var paths = document.GetProperty(""paths"");
        Assert.False(paths.TryGetProperty(""/"", out _));
        foreach (var path in paths.EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
        {
            if (path.Name.StartsWith(""/api/"", StringComparison.Ordinal))
            {
                Assert.True(operation.Value.GetProperty(""security"")[0].TryGetProperty(""ApiKey"", out _));
                Assert.True(operation.Value.GetProperty(""responses"").TryGetProperty(""401"", out _));
                Assert.True(operation.Value.GetProperty(""responses"").TryGetProperty(""503"", out _));
            }
            else
                Assert.True(!operation.Value.TryGetProperty(""security"", out var security) || security.GetArrayLength() == 0);
        }
        var create = paths.GetProperty(""/api/urls"").GetProperty(""post"");
        Assert.Contains(create.GetProperty(""parameters"").EnumerateArray(),
            parameter => parameter.GetProperty(""name"").GetString() == ""Idempotency-Key"" &&
                parameter.GetProperty(""in"").GetString() == ""header"");
        foreach (var status in new[] { ""200"", ""201"", ""400"", ""409"", ""422"" })
            Assert.True(create.GetProperty(""responses"").TryGetProperty(status, out _));
        Assert.True(paths.GetProperty(""/api/urls/{code}"").GetProperty(""delete"").GetProperty(""responses"").TryGetProperty(""204"", out _));
        Assert.True(paths.GetProperty(""/{code}"").GetProperty(""get"").GetProperty(""responses"").TryGetProperty(""307"", out _));
        var schemas = document.GetProperty(""components"").GetProperty(""schemas"");
        var request = schemas.GetProperty(""CreateUrlRequest"").GetProperty(""properties"");
        Assert.True(request.TryGetProperty(""url"", out _));
        Assert.True(request.TryGetProperty(""expires_in_days"", out _));
        Assert.False(request.TryGetProperty(""expiresInDays"", out _));
        Assert.True(schemas.GetProperty(""ShortUrlResponse"").GetProperty(""properties"").TryGetProperty(""short_url"", out _));
    }

    [Fact]
    public async Task DocumentationDoesNotBypassApiAuthentication()
    {
        using var factory = new ShortenerTestFactory();
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment(""Development""));
        using var client = development.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        using var unauthorized = await client.GetAsync(""/api/urls"");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, ShortenerTestFactory.TestKey);
        using var created = await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com"" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var listed = await client.GetAsync(""/api/urls"");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    [Fact]
    public async Task DocumentationIsAvailableWhenApiKeysHaveNotBeenConfigured()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment(""Development""));
        using var client = development.CreateClient();
        using var documentation = await client.GetAsync(""/swagger/v1/swagger.json"");
        Assert.Equal(HttpStatusCode.OK, documentation.StatusCode);
        using var management = await client.GetAsync(""/api/urls"");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, management.StatusCode);
    }
}
";

    private const string ProgramCs = @"using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Shortener;

var app = CreateApp(ShortenerOptions.FromEnvironment(), args);
app.Run();

WebApplication CreateApp(ShortenerOptions? options = null, string[]? args = null)
{
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    options ??= ShortenerOptions.FromEnvironment();

    var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
    builder.Services.AddApiDocumentation();
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<ApiKeyAuthentication>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IUrlRepository>(sp => sp.GetRequiredService<UrlStore>());
    builder.Services.AddSingleton<UrlService>();
    builder.Services.AddSingleton<UrlStore>(sp =>
        new UrlStore(sp.GetRequiredService<ShortenerOptions>().DbPath, sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<RateLimiter>(sp =>
    {
        var o = sp.GetRequiredService<ShortenerOptions>();
        return new RateLimiter(o.RatePerMinute, o.RateBurst);
    });
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

    var app = builder.Build();
    app.UseMiddleware<RequestCorrelationMiddleware>();
    // The handler logs failures once, including correlation IDs, in every environment.
    app.UseExceptionHandler(new Microsoft.AspNetCore.Builder.ExceptionHandlerOptions
    {
        SuppressDiagnosticsCallback = _ => true,
    });
    // Binding/routing can return an empty error response without throwing.
    app.UseStatusCodePages(statusContext => Results.Problem(
        statusCode: statusContext.HttpContext.Response.StatusCode,
        extensions: new Dictionary<string, object?>
        {
            [""correlation_id""] = statusContext.HttpContext.TraceIdentifier,
        }).ExecuteAsync(statusContext.HttpContext));

    app.UseApiDocumentation();

    var trustedProxies = app.Services.GetRequiredService<ShortenerOptions>().TrustedProxies;
    if (trustedProxies.Length > 0)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = 1,
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in trustedProxies)
            forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        app.UseForwardedHeaders(forwarded);
    }

    string ClientIp(HttpContext c) =>
        c.Connection.RemoteIpAddress?.ToString() ?? ""unknown"";

    string? Referrer(HttpContext c) =>
        c.Request.Headers.TryGetValue(""Referer"", out var r) ? r.ToString() : null;

    var limiter = app.Services.GetRequiredService<RateLimiter>();

    // ---- rate limiting (health/ready bypass) -----------------------------
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path != ""/health"" && path != ""/ready"")
        {
            var (allowed, retryAfter) = limiter.Allow(ClientIp(context));
            if (!allowed)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers[""Retry-After""] = ((int)retryAfter + 1).ToString();
                await context.Response.WriteAsJsonAsync(new { detail = ""rate limit exceeded"" });
                return;
            }
        }
        await next();
    });

    ApiKeyAuthentication.ProtectManagementApi(app);

    // HTTP handlers bind requests and map business outcomes to the API contract.
    app.MapGet(""/health"", () => Results.Ok(new { status = ""ok"" }))
        .WithSummary(""Check application liveness"").WithTags(""Health"");
    app.MapGet(""/ready"", (UrlService service) =>
    {
        service.CheckReady();
        return Results.Ok(new { status = ""ready"", db = ""ok"" });
    }).WithMetadata(new ReadinessEndpoint())
        .WithSummary(""Check database readiness"").WithTags(""Health"").Produces(200).Produces(503);

    app.MapPost(""/api/urls"", (HttpContext context, CreateUrlRequest body, UrlService service) =>
    {
        string? key = null;
        if (context.Request.Headers.TryGetValue(""Idempotency-Key"", out var values))
        {
            if (values.Count != 1 || values[0] is null)
                return Results.Json(new { detail = ""Idempotency-Key must be 1–128 printable ASCII characters without spaces"" }, statusCode: 400);
            key = values[0];
        }
        var result = service.Create(ApiKeyAuthentication.OwnerOf(context), body, key);
        return result.Status switch
        {
            UrlCreationStatus.Created => Results.Json(result.Value, jsonOptions, statusCode: 201),
            UrlCreationStatus.Replay => Results.Json(JsonSerializer.Deserialize<JsonElement>(result.ReplayJson!), statusCode: 200),
            UrlCreationStatus.InvalidIdempotencyKey => Results.Json(new { detail = result.Error }, statusCode: 400),
            UrlCreationStatus.InvalidRequest or UrlCreationStatus.IdempotencyConflict =>
                Results.Json(new { detail = result.Error }, statusCode: 422),
            UrlCreationStatus.CodeConflict => Results.Json(new { detail = result.Error }, statusCode: 409),
            UrlCreationStatus.AllocationFailed => Results.Json(new { detail = result.Error }, statusCode: 500),
            _ => throw new InvalidOperationException(""Unknown URL creation outcome.""),
        };
    }).WithSummary(""Create a short URL"").WithTags(""URLs"")
        .Produces<ShortUrlResponse>(201).Produces<ShortUrlResponse>(200)
        .Produces(400).Produces(409).Produces(422).Produces(500);

    app.MapGet(""/api/urls"", (HttpContext context, UrlService service) =>
        Results.Ok(service.List(ApiKeyAuthentication.OwnerOf(context))))
        .WithSummary(""List your short URLs"").WithTags(""URLs"").Produces<List<UrlRecordDto>>();

    app.MapGet(""/api/urls/{code}/stats"", (HttpContext context, string code, UrlService service) =>
    {
        var stats = service.Stats(code, ApiKeyAuthentication.OwnerOf(context));
        // Preserve referrer and user-agent dictionary keys verbatim.
        return stats is null
            ? Results.Json(new { detail = ""unknown code"" }, statusCode: 404)
            : Results.Json(stats, jsonOptions);
    }).WithSummary(""Get click statistics for your short URL"").WithTags(""URLs"")
        .Produces<UrlStats>().Produces(404);

    app.MapGet(""/api/urls/{code}"", (HttpContext context, string code, UrlService service) =>
    {
        var row = service.GetOwned(code, ApiKeyAuthentication.OwnerOf(context));
        return row is null ? Results.NotFound(new { detail = ""unknown code"" }) : Results.Ok(row);
    }).WithSummary(""Get one of your short URLs"").WithTags(""URLs"")
        .Produces<UrlRecordDto>().Produces(404);

    app.MapDelete(""/api/urls/{code}"", (HttpContext context, string code, UrlService service) =>
        service.Delete(code, ApiKeyAuthentication.OwnerOf(context))
            ? Results.NoContent() : Results.NotFound(new { detail = ""unknown code"" }))
        .WithSummary(""Delete one of your short URLs"").WithTags(""URLs"").Produces(204).Produces(404);

    // -- EXTENSION POINT: smart-link endpoints (ambiguous scenario) --

    // ---- redirect (catch-all, registered last) -----------------------------------
    app.MapGet(""/{code}"", (HttpContext context, string code, UrlService service) =>
    {
        var result = service.Redirect(code, new ClickMetadata(Referrer(context),
            context.Request.Headers.UserAgent.ToString(), ClientIp(context)));
        return result.Status switch
        {
            RedirectStatus.Unknown => Results.Json(new { detail = ""unknown code"" }, statusCode: 404),
            RedirectStatus.Expired => Results.Json(new { detail = ""link expired"" }, statusCode: 410),
            RedirectStatus.Found => Results.Redirect(result.Destination!, preserveMethod: true),
            _ => throw new InvalidOperationException(""Unknown redirect outcome.""),
        };
    }).WithSummary(""Follow a short URL and record a click"").WithTags(""Redirects"")
        .Produces(307).Produces(404).Produces(StatusCodes.Status410Gone);

    return app;
}

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
";

    // =======================================================================
    // src/Shortener.Tests/Shortener.Tests.csproj
    // =======================================================================
    private const string TestsCsproj = @"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <!--
      System.Net.DisableIPv6 works around a sandbox egress quirk: the test
      host's dual-mode IPv6 sockets are transparently redirected to the
      egress proxy, which breaks the vstest <-> testhost channel.
    -->
    <RuntimeHostConfigurationOption Include=""System.Net.DisableIPv6"" Value=""true"" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.AspNetCore.Mvc.Testing"" Version=""10.0.7"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.11.1"" />
    <PackageReference Include=""xunit"" Version=""2.9.2"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.8.2"" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include=""..\Shortener\Shortener.csproj"" />
  </ItemGroup>

</Project>
";

    private const string ExceptionHandlingTestsCs = @"using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Shortener;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed class ExceptionHandlingTests
{
    private const string PrivateDetail = ""secret-token internal/database.db stack detail"";

    [Theory]
    [InlineData(""unexpected"", 500)]
    [InlineData(""argument"", 500)]
    [InlineData(""access"", 500)]
    [InlineData(""cancellation"", 500)]
    [InlineData(""timeout"", 503)]
    [InlineData(""busy"", 503)]
    [InlineData(""locked"", 503)]
    [InlineData(""open"", 503)]
    [InlineData(""corrupt"", 500)]
    [InlineData(""large"", 413)]
    public async Task FailuresAreMappedSanitizedAndLoggedWithMatchingCorrelationId(string failure, int status)
    {
        Exception exception = failure switch
        {
            ""argument"" => new ArgumentException(PrivateDetail),
            ""access"" => new UnauthorizedAccessException(PrivateDetail),
            ""cancellation"" => new OperationCanceledException(PrivateDetail),
            ""timeout"" => new TimeoutException(PrivateDetail),
            ""busy"" => new SqliteException(PrivateDetail, 5),
            ""locked"" => new SqliteException(PrivateDetail, 6),
            ""open"" => new SqliteException(PrivateDetail, 14),
            ""corrupt"" => new SqliteException(PrivateDetail, 11),
            ""large"" => new BadHttpRequestException(PrivateDetail, 413),
            _ => new InvalidOperationException(PrivateDetail),
        };
        var logger = new CapturingLogger();
        using var factory = new ShortenerTestFactory();
        using var failing = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(""Development"");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IUrlRepository>(new FailingRepository(exception));
                services.AddSingleton<ILogger<ApiExceptionHandler>>(logger);
            });
        });
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(""text/html"");
        using var response = await client.GetAsync(""/api/urls"");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(""application/problem+json"", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateDetail, content);
        Assert.DoesNotContain(exception.GetType().Name, content);
        Assert.DoesNotContain(""stack"", content, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(content);
        Assert.Equal(status, body.RootElement.GetProperty(""status"").GetInt32());
        var correlationId = Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
        Assert.Equal(correlationId, body.RootElement.GetProperty(""correlation_id"").GetString());
        var log = Assert.Single(logger.Entries);
        Assert.Same(exception, log.Exception);
        Assert.Equal(correlationId, log.Fields[""CorrelationId""]);
        Assert.Equal(status, log.Fields[""StatusCode""]);
        Assert.Equal(status >= 500 ? LogLevel.Error : LogLevel.Warning, log.Level);
    }

    [Theory]
    [InlineData(""Development"", ""application/json"")]
    [InlineData(""Production"", ""application/json"")]
    [InlineData(""Development"", ""text/html"")]
    [InlineData(""Production"", ""text/html"")]
    public async Task MalformedJsonHasSafe400InEveryEnvironment(string environment, string accept)
    {
        using var factory = new ShortenerTestFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var content = new StringContent(""{\""url\"":\""secret-token\"",\""expires_in_days\"":\""private\""}"", Encoding.UTF8, ""application/json"");
        using var response = await client.PostAsync(""/api/urls"", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(""application/problem+json"", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(""secret-token"", json);
        Assert.DoesNotContain(""private"", json);
        Assert.DoesNotContain(""Exception"", json);
        using var body = JsonDocument.Parse(json);
        Assert.Equal(400, body.RootElement.GetProperty(""status"").GetInt32());
        Assert.Equal(Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName)),
            body.RootElement.GetProperty(""correlation_id"").GetString());
    }

    [Fact]
    public async Task UnsupportedContentTypeReturnsSafe415()
    {
        using var factory = new ShortenerTestFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(PrivateDetail);
        using var response = await client.PostAsync(""/api/urls"", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(415, body.RootElement.GetProperty(""status"").GetInt32());
    }

    [Theory]
    [InlineData(""/ready"")]
    [InlineData(""/READY"")]
    [InlineData(""/ready/"")]
    public async Task ReadinessFailuresKeepProbeContractAndAreLogged(string path)
    {
        var logger = new CapturingLogger();
        using var factory = new ShortenerTestFactory();
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IUrlRepository>(new FailingRepository(new InvalidOperationException(PrivateDetail)));
            services.AddSingleton<ILogger<ApiExceptionHandler>>(logger);
        }));
        using var client = failing.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateDetail, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(""degraded"", body.RootElement.GetProperty(""status"").GetString());
        Assert.Equal(""error"", body.RootElement.GetProperty(""db"").GetString());
        Assert.Equal(Assert.Single(logger.Entries).Fields[""CorrelationId""],
            body.RootElement.GetProperty(""correlation_id"").GetString());
        using var healthy = await client.GetAsync(""/health"");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task EveryResponseGetsAnIndependentServerGeneratedCorrelationId()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestCorrelationMiddleware.HeaderName, ""untrusted-client-value"");
        var ids = new HashSet<string>();
        foreach (var path in new[] { ""/health"", ""/ready"", ""/api/urls"", ""/missing"", ""/unknown/path"" })
        {
            using var response = await client.GetAsync(path);
            var id = Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
            Assert.True(Guid.TryParseExact(id, ""N"", out _));
            Assert.True(ids.Add(id));
        }
    }

    private sealed class CapturingLogger : ILogger<ApiExceptionHandler>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((level, exception, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }

    private sealed class FailingRepository(Exception exception) : IUrlRepository
    {
        public void CheckReady() => throw exception;
        public List<UrlRecordDto> ListOwned(string ownerId) => throw exception;
        public UrlRow? Get(string code) => throw exception;
        public UrlRow? GetOwned(string code, string ownerId) => throw exception;
        public bool DeleteOwned(string code, string ownerId) => throw exception;
        public List<ClickRow> ClicksFor(string code, string ownerId) => throw exception;
        public int ClickCount(string code, string ownerId) => throw exception;
        public void RecordClick(string code, string ts, string? referrer, string? userAgent, string? ip) => throw exception;
        public IdempotencyRecord? GetIdempotency(string ownerId, string key) => throw exception;
        public CreateUrlResult CreateWithIdempotency(string code, string url, string createdAt, string? expiresAt,
            string ownerId, string? key, string requestHash, string body) => throw exception;
    }
}
";

    private const string TestsHeader = @"// Generated service test suite (__VARIANT_LABEL__).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shortener;
using Xunit;

public sealed class ShortenerTestFactory : WebApplicationFactory<Program>
{
    public const string TestKey = ""test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz"";
    private readonly bool _authenticate;
    private readonly ShortenerOptions _options;
    private readonly IPAddress? _remoteIp;

    public ShortenerTestFactory(ShortenerOptions? options = null, IPAddress? remoteIp = null, bool authenticate = true)
    {
        _authenticate = authenticate;
        _remoteIp = remoteIp;
        _options = options ?? new ShortenerOptions
        {
            DbPath = "":memory:"",
            BaseUrl = ""http://test"",
            RatePerMinute = 6000,
            RateBurst = 1000,
        };
        if (authenticate) _options.ApiKeys[""test-owner""] = TestKey;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (_authenticate) client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, TestKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_options);
            if (_remoteIp is not null)
                services.AddSingleton<IStartupFilter>(new RemoteIpFilter(_remoteIp));
        });
    }
}

file sealed class RemoteIpFilter(IPAddress address) : IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware();
        });
        next(app);
    };
}

public sealed class ServiceTests : IDisposable
{
    [Fact]
    public async Task ManagementRequiresApiKey()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(""/api/urls"")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(""/health"")).StatusCode);
    }

    [Fact]
    public async Task OtherOwnerCannotReadOrDeleteLink()
    {
        using var factory = new ShortenerTestFactory(new ShortenerOptions
        {
            DbPath = "":memory:"", RateBurst = 1000,
            ApiKeys = new() { [""other-owner""] = ""other-test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz"" },
        });
        using var first = factory.CreateClient();
        var code = CodeOf(await (await first.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com"" })).Content.ReadAsStringAsync());
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        other.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, ""other-test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz"");
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>(""/api/urls"")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($""/api/urls/{code}"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($""/api/urls/{code}/stats"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($""/api/urls/{code}"")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync($""/api/urls/{code}"")).StatusCode);
    }

    [Fact]
    public async Task IdempotencyRejectsChangedRequest()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add(""Idempotency-Key"", ""request-match"");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com"" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://other.example"" })).StatusCode);
        Assert.Single(_factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    private readonly ShortenerTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client(bool followRedirects = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = followRedirects,
        });

    private static JsonElement Body(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    private static string CodeOf(string json) =>
        Body(json).GetProperty(""code"").GetString()!;

    [Fact]
    public async Task ReadinessUnavailableDatabaseReturns503()
    {
        using var factory = new ShortenerTestFactory(new ShortenerOptions
        {
            DbPath = Path.Combine(Path.GetTempPath(), $""missing-{Guid.NewGuid():N}"", ""ready.db""),
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(""/ready"");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = Body(await response.Content.ReadAsStringAsync());
        Assert.Equal(""degraded"", body.GetProperty(""status"").GetString());
        Assert.Equal(""error"", body.GetProperty(""db"").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(""/health"")).StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3651)]
    [InlineData(int.MaxValue)]
    public async Task CreateInvalidExpiryDaysReturns422(int days)
    {
        using var response = await Client().PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com"", expires_in_days = days });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3650)]
    public async Task CreateExpiryDayBoundsAreAccepted(int days)
    {
        using var response = await Client().PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com"", expires_in_days = days });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = Body(await response.Content.ReadAsStringAsync());
        var created = DateTimeOffset.Parse(body.GetProperty(""created_at"").GetString()!);
        var expires = DateTimeOffset.Parse(body.GetProperty(""expires_at"").GetString()!);
        Assert.Equal(TimeSpan.FromDays(days), expires - created);
    }

    [Theory]
    [InlineData(-14)]
    [InlineData(0)]
    [InlineData(14)]
    public async Task FutureExpiryRedirectsRegardlessOfOffset(int offsetHours)
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(1).ToOffset(TimeSpan.FromHours(offsetHours));
        var store = _factory.Services.GetRequiredService<UrlStore>();
        Assert.True(store.Create(""offset-expiry"", ""https://example.com"",
            now.ToString(""o""), expiry.ToString(""o"")));
        using var response = await Client(followRedirects: false).GetAsync(""/offset-expiry"");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(1, store.ClickCount(""offset-expiry""));
    }
";

    // =======================================================================
    // src/Shortener.Tests/ServiceTests.cs  (v2)
    // =======================================================================
    private static string ServiceTestsCs =>
        TestsHeader.Replace("__VARIANT_LABEL__",
            "v2: custom aliases, expiry 410, shared validators") + @"
    [Fact]
    public async Task CreateShortUrl()
    {
        var r = await Client().PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/long/path"" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = Body(await r.Content.ReadAsStringAsync());
        var code = body.GetProperty(""code"").GetString()!;
        Assert.Equal(7, code.Length);
        Assert.EndsWith(""/"" + code, body.GetProperty(""short_url"").GetString());
        Assert.Equal(""https://example.com/long/path"", body.GetProperty(""url"").GetString());
        Assert.NotNull(body.GetProperty(""created_at"").GetString());
    }

    [Fact]
    public async Task CreateRejectsBadUrl()
    {
        foreach (var bad in new[] { ""not-a-url"", ""ftp://example.com/x"", """", ""   "" })
        {
            var r = await Client().PostAsJsonAsync(""/api/urls"", new { url = bad });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        }
    }

    [Fact]
    public async Task RedirectAndClickRecorded()
    {
        var client = Client(followRedirects: false);
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        var r = await client.GetAsync(""/"" + code);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, r.StatusCode);
        Assert.Equal(""https://example.com/"", r.Headers.Location?.ToString());
        var stats = Body(await (await client.GetAsync($""/api/urls/{code}/stats"")).Content.ReadAsStringAsync());
        Assert.Equal(1, stats.GetProperty(""total_clicks"").GetInt32());
        Assert.Equal(1, stats.GetProperty(""clicks_by_day"").EnumerateArray().First().GetProperty(""count"").GetInt32());
    }

    [Fact]
    public async Task RedirectUnknownCode404()
    {
        var r = await Client(followRedirects: false).GetAsync(""/nope-not-real"");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task ExpiredLinkReturns410()
    {
        var store = _factory.Services.GetRequiredService<UrlStore>();
        Assert.True(store.Create(""old1"", ""https://example.com/"",
            ""2020-01-01T00:00:00+00:00"", ""2020-01-02T00:00:00+00:00""));
        var r = await Client(followRedirects: false).GetAsync(""/old1"");
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
    }

    [Fact]
    public async Task CustomAlias()
    {
        var client = Client();
        var r = await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"", custom_alias = ""my-link"" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(""my-link"", CodeOf(await r.Content.ReadAsStringAsync()));
        var dup = await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.org/"", custom_alias = ""my-link"" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var bad = await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.org/"", custom_alias = ""no spaces!"" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task ListGetDelete()
    {
        var client = Client();
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        Assert.Contains(code, await (await client.GetAsync(""/api/urls"")).Content.ReadAsStringAsync());
        var get = Body(await (await client.GetAsync($""/api/urls/{code}"")).Content.ReadAsStringAsync());
        Assert.Equal(""https://example.com/"", get.GetProperty(""url"").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(""/api/urls/missing"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($""/api/urls/{code}"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($""/api/urls/{code}"")).StatusCode);
    }

    [Fact]
    public async Task AnalyticsBreakdown()
    {
        var client = Client(followRedirects: false);
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        using var c1 = Client(followRedirects: false);
        c1.DefaultRequestHeaders.Referrer = new Uri(""https://google.com"");
        c1.DefaultRequestHeaders.UserAgent.ParseAdd(""UA-1"");
        using var c2 = Client(followRedirects: false);
        c2.DefaultRequestHeaders.Referrer = new Uri(""https://google.com"");
        c2.DefaultRequestHeaders.UserAgent.ParseAdd(""UA-2"");
        using var c3 = Client(followRedirects: false);
        c3.DefaultRequestHeaders.UserAgent.ParseAdd(""UA-1"");
        await c1.GetAsync(""/"" + code);
        await c2.GetAsync(""/"" + code);
        await c3.GetAsync(""/"" + code);
        var stats = Body(await (await client.GetAsync($""/api/urls/{code}/stats"")).Content.ReadAsStringAsync());
        Assert.Equal(3, stats.GetProperty(""total_clicks"").GetInt32());
        Assert.Equal(2, stats.GetProperty(""referrers"").GetProperty(""https://google.com/"").GetInt32());
        Assert.Equal(2, stats.GetProperty(""user_agents"").GetProperty(""UA-1"").GetInt32());
        Assert.Equal(1, stats.GetProperty(""user_agents"").GetProperty(""UA-2"").GetInt32());
        Assert.NotNull(stats.GetProperty(""last_clicked_at"").GetString());
    }

    [Fact]
    public async Task IdempotencyKey()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(""Idempotency-Key"", ""key-123"");
        var r1 = await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com/"" });
        var r2 = await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com/"" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(CodeOf(await r1.Content.ReadAsStringAsync()),
                     CodeOf(await r2.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task RateLimiting()
    {
        using var factory = new ShortenerTestFactory(new ShortenerOptions
        {
            DbPath = "":memory:"", BaseUrl = ""http://test"",
            RatePerMinute = 2, RateBurst = 2,
        });
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsJsonAsync(""/api/urls"",
                new { url = ""https://example.com/"" })).StatusCode);
        Assert.Contains((HttpStatusCode)429, statuses);
    }

    [Fact]
    public async Task HealthAndReady()
    {
        var client = Client();
        Assert.Equal(""ok"", Body(await (await client.GetAsync(""/health"")).Content.ReadAsStringAsync()).GetProperty(""status"").GetString());
        var ready = Body(await (await client.GetAsync(""/ready"")).Content.ReadAsStringAsync());
        Assert.Equal(""ready"", ready.GetProperty(""status"").GetString());
        Assert.Equal(""ok"", ready.GetProperty(""db"").GetString());
    }

    [Fact]
    public void ValidatorsUnit()
    {
        Assert.Equal(""https://example.com/x"", Validators.ValidateUrl(""https://example.com/x""));
        foreach (var bad in new[] { ""nope"", ""ftp://x.com"", """" })
            Assert.Throws<ArgumentException>(() => Validators.ValidateUrl(bad));
        Assert.Equal(""abc-123_X"", Validators.ValidateAlias(""abc-123_X""));
        foreach (var bad in new[] { ""ab"", ""has space"", new string('x', 33) })
            Assert.Throws<ArgumentException>(() => Validators.ValidateAlias(bad));
    }
}
";

    // =======================================================================
    // Shortener.Tests/ServiceTestsV1.cs  (v1: core only, pre-brownfield)
    // =======================================================================
    private static string ServiceTestsV1Cs =>
        TestsHeader.Replace("__VARIANT_LABEL__",
            "v1: core only (pre-brownfield)") + @"
    [Fact]
    public async Task CreateShortUrl()
    {
        var r = await Client().PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/long/path"" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = Body(await r.Content.ReadAsStringAsync());
        var code = body.GetProperty(""code"").GetString()!;
        Assert.Equal(7, code.Length);
        Assert.EndsWith(""/"" + code, body.GetProperty(""short_url"").GetString());
    }

    [Fact]
    public async Task CreateRejectsBadUrl()
    {
        var r = await Client().PostAsJsonAsync(""/api/urls"", new { url = ""not-a-url"" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task RedirectAndClickRecorded()
    {
        var client = Client(followRedirects: false);
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        var r = await client.GetAsync(""/"" + code);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, r.StatusCode);
        Assert.Equal(""https://example.com/"", r.Headers.Location?.ToString());
        var stats = Body(await (await client.GetAsync($""/api/urls/{code}/stats"")).Content.ReadAsStringAsync());
        Assert.Equal(1, stats.GetProperty(""total_clicks"").GetInt32());
    }

    [Fact]
    public async Task ExpiredLinkReturns404KnownBug()
    {
        // v1 known bug (fixed in the brownfield scenario): expired links
        // return 404 instead of 410, making them indistinguishable from
        // unknown codes.
        var store = _factory.Services.GetRequiredService<UrlStore>();
        Assert.True(store.Create(""old1"", ""https://example.com/"",
            ""2020-01-01T00:00:00+00:00"", ""2020-01-02T00:00:00+00:00""));
        var r = await Client(followRedirects: false).GetAsync(""/old1"");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task ListGetDelete()
    {
        var client = Client();
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        Assert.Contains(code, await (await client.GetAsync(""/api/urls"")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($""/api/urls/{code}"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($""/api/urls/{code}"")).StatusCode);
    }

    [Fact]
    public async Task AnalyticsBreakdown()
    {
        var client = Client(followRedirects: false);
        var code = CodeOf(await (await client.PostAsJsonAsync(""/api/urls"",
            new { url = ""https://example.com/"" })).Content.ReadAsStringAsync());
        using var c1 = Client(followRedirects: false);
        c1.DefaultRequestHeaders.Referrer = new Uri(""https://google.com"");
        await c1.GetAsync(""/"" + code);
        await client.GetAsync(""/"" + code);
        var stats = Body(await (await client.GetAsync($""/api/urls/{code}/stats"")).Content.ReadAsStringAsync());
        Assert.Equal(2, stats.GetProperty(""total_clicks"").GetInt32());
        Assert.Equal(1, stats.GetProperty(""referrers"").GetProperty(""https://google.com/"").GetInt32());
    }

    [Fact]
    public async Task IdempotencyKey()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(""Idempotency-Key"", ""key-123"");
        var r1 = await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com/"" });
        var r2 = await client.PostAsJsonAsync(""/api/urls"", new { url = ""https://example.com/"" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(CodeOf(await r1.Content.ReadAsStringAsync()),
                     CodeOf(await r2.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task RateLimiting()
    {
        using var factory = new ShortenerTestFactory(new ShortenerOptions
        {
            DbPath = "":memory:"", BaseUrl = ""http://test"",
            RatePerMinute = 2, RateBurst = 2,
        });
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsJsonAsync(""/api/urls"",
                new { url = ""https://example.com/"" })).StatusCode);
        Assert.Contains((HttpStatusCode)429, statuses);
    }

    [Fact]
    public async Task HealthAndReady()
    {
        var client = Client();
        Assert.Equal(""ok"", Body(await (await client.GetAsync(""/health"")).Content.ReadAsStringAsync()).GetProperty(""status"").GetString());
        Assert.Equal(""ready"", Body(await (await client.GetAsync(""/ready"")).Content.ReadAsStringAsync()).GetProperty(""status"").GetString());
    }
}
";

    // =======================================================================
    // v1 derivation
    // =======================================================================

    /// <summary>
    /// v1 omits aliases and the shared validator module. The HTTP adapter retains
    /// the planted expired-link 404 bug, fixed by the brownfield scenario.
    /// </summary>
    private static List<FileSpec> ToV1(List<FileSpec> files)
    {
        var result = new List<FileSpec>();
        foreach (var spec in files)
        {
            if (spec.Path == "Shortener/Validators.cs") continue;
            var content = spec.Content;
            if (spec.Path == "Shortener/Models.cs")
                content = ReplaceOnce(content, "    public string? CustomAlias { get; set; }\n", "");
            else if (spec.Path == "Shortener/UrlService.cs")
                content = ReplaceOnce(content,
                    @"            url = Validators.ValidateUrl(request.Url);
            alias = string.IsNullOrEmpty(request.CustomAlias) ? null : Validators.ValidateAlias(request.CustomAlias);",
                    @"            // v1: inline validation; extracted in the brownfield refactor.
            url = (request.Url ?? """").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(parsed.Host))
                throw new ArgumentException(""url must be an absolute http(s) URL"");
            alias = null;");
            else if (spec.Path == "Shortener/Program.cs")
                content = ReplaceOnce(content,
                    @"RedirectStatus.Expired => Results.Json(new { detail = ""link expired"" }, statusCode: 410)",
                    @"RedirectStatus.Expired => Results.Json(new { detail = ""link expired"" }, statusCode: 404)")
                    .Replace(".Produces(StatusCodes.Status410Gone)", "");
            result.Add(new(spec.Path, content));
        }
        if (result.Any(spec => spec.Path is "Shortener/Models.cs" or "Shortener/UrlService.cs" &&
            (spec.Content.Contains("CustomAlias") || spec.Content.Contains("Validators."))))
            throw new InvalidOperationException("v1 still references aliases or shared validators");
        return result;
    }

    private static string ReplaceOnce(string content, string oldText, string newText)
    {
        if (CountOccurrences(content, oldText) != 1)
            throw new InvalidOperationException($"v1 patch anchor not found exactly once: {oldText}");
        return content.Replace(oldText, newText);
    }

    private static int CountOccurrences(string text, string sub)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
    }

    // =======================================================================
    // Smart-link feature (ambiguous scenario)
    // =======================================================================

    public const string ExtensionMarker =
        "    // -- EXTENSION POINT: smart-link endpoints (ambiguous scenario) --";

    private const string SmartLinksHealthCs = @"// Smart-link features (scope: health monitoring).
//
// Smart-link helpers and HTTP probes, wired through Program.cs.
// Probe failures are represented as unreachable results.

namespace Shortener;

public static class SmartLinks
{
    /// <summary>Probe the target URL and return reachability or failure details.</summary>
    public static Dictionary<string, object?> CheckLinkHealth(string url, double timeoutS = 5.0)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutS) };
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = client.Send(request);
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = (int)resp.StatusCode < 400,
                [""status_code""] = (int)resp.StatusCode,
            };
        }
        catch (Exception exc) when (exc is HttpRequestException or OperationCanceledException)
        {
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = false,
                [""status_code""] = null,
                [""error""] = ""link health probe failed"",
            };
        }
    }
}
";

    private const string SmartLinksFullCs = @"// Smart-link features (scope: full — device-aware routing + health).
//
// Smart-link helpers and HTTP probes, wired through Program.cs.
// Probe failures are represented as unreachable results.

using System.Text.RegularExpressions;

namespace Shortener;

public sealed class SmartClassifyRequest
{
    public string? UserAgent { get; set; }
}

public static class SmartLinks
{
    private static readonly Regex MobileRe =
        new(@""(mobile|android|iphone|phone)"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TabletRe =
        new(@""(tablet|ipad)"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BotRe =
        new(@""(bot|crawler|spider|curl|wget|httpx|httpclient)"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Classify a User-Agent string as mobile/tablet/desktop/bot.</summary>
    public static string ClassifyDevice(string? userAgent)
    {
        var ua = userAgent ?? """";
        if (BotRe.IsMatch(ua)) return ""bot"";
        if (TabletRe.IsMatch(ua)) return ""tablet"";
        if (MobileRe.IsMatch(ua)) return ""mobile"";
        return ""desktop"";
    }

    /// <summary>Pick the redirect target for a device class from per-link rules.</summary>
    public static string ResolveSmartTarget(Dictionary<string, string>? rules,
                                            string device, string defaultUrl)
    {
        if (rules is null) return defaultUrl;
        if (rules.TryGetValue(device, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (rules.TryGetValue(""default"", out var d) && !string.IsNullOrEmpty(d)) return d;
        return defaultUrl;
    }

    /// <summary>Probe the target URL and return reachability or failure details.</summary>
    public static Dictionary<string, object?> CheckLinkHealth(string url, double timeoutS = 5.0)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutS) };
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = client.Send(request);
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = (int)resp.StatusCode < 400,
                [""status_code""] = (int)resp.StatusCode,
            };
        }
        catch (Exception exc) when (exc is HttpRequestException or OperationCanceledException)
        {
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = false,
                [""status_code""] = null,
                [""error""] = ""link health probe failed"",
            };
        }
    }
}
";

    public const string SmartEndpointsHealth = @"    // -- smart-link endpoints (added by the smart-link feature) --
    app.MapGet(""/api/urls/{code}/health"", (HttpContext context, string code, UrlService service) =>
    {
        var row = service.GetOwned(code, ApiKeyAuthentication.OwnerOf(context));
        if (row is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        var result = new Dictionary<string, object?> { [""code""] = code };
        foreach (var (k, v) in SmartLinks.CheckLinkHealth(row.Url))
            result[k] = v;
        return Results.Json(result);
    });
";

    public const string SmartEndpointsFull = @"    // -- smart-link endpoints (added by the smart-link feature) --
    app.MapGet(""/api/urls/{code}/health"", (HttpContext context, string code, UrlService service) =>
    {
        var row = service.GetOwned(code, ApiKeyAuthentication.OwnerOf(context));
        if (row is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        var result = new Dictionary<string, object?> { [""code""] = code };
        foreach (var (k, v) in SmartLinks.CheckLinkHealth(row.Url))
            result[k] = v;
        return Results.Json(result);
    });

    app.MapPost(""/api/smart/classify"", (SmartClassifyRequest body) =>
        Results.Json(new { device = SmartLinks.ClassifyDevice(body.UserAgent) }));
";

    private const string SmartTestsHeader = @"// Tests for the smart-link feature (scope: __SCOPE__).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shortener;
using Xunit;

public sealed class SmartTests : IDisposable
{
    [Fact]
    public async Task HealthEndpointRequiresLinkOwnership()
    {
        using var client = Client();
        var store = _factory.Services.GetRequiredService<UrlStore>();
        store.Create(""other-owned"", ""http://127.0.0.1:9/x"", DateTime.UtcNow.ToString(""o""), ownerId: ""someone-else"");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(""/api/urls/other-owned/health"")).StatusCode);
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(""/api/urls/other-owned/health"")).StatusCode);
    }

    private readonly ShortenerTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient();

    [Fact]
    public async Task CheckLinkHealthUnreachable()
    {
        var result = SmartLinks.CheckLinkHealth(""http://127.0.0.1:9/nope"", timeoutS: 1.0);
        Assert.False((bool)result[""reachable""]!);
        Assert.Null(result[""status_code""]);
        Assert.Equal(""link health probe failed"", result[""error""]);
    }

    [Fact]
    public async Task UrlHealthEndpoint()
    {
        var client = Client();
        var code = JsonSerializer.Deserialize<JsonElement>(
            await (await client.PostAsJsonAsync(""/api/urls"",
                new { url = ""http://127.0.0.1:9/x"" })).Content.ReadAsStringAsync())
            .GetProperty(""code"").GetString()!;
        var r = await client.GetAsync($""/api/urls/{code}/health"");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(
            await r.Content.ReadAsStringAsync());
        Assert.Equal(code, body.GetProperty(""code"").GetString());
        Assert.False(body.GetProperty(""reachable"").GetBoolean());
    }

    [Fact]
    public async Task UrlHealthUnknownCode404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client().GetAsync(""/api/urls/nope/health"")).StatusCode);
    }
";

    private const string SmartTestsFullExtra = @"
    [Fact]
    public void ClassifyDevice()
    {
        Assert.Equal(""mobile"", SmartLinks.ClassifyDevice(
            ""Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)""));
        Assert.Equal(""desktop"", SmartLinks.ClassifyDevice(
            ""Mozilla/5.0 (Windows NT 10.0; Win64; x64)""));
        Assert.Equal(""tablet"", SmartLinks.ClassifyDevice(
            ""Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)""));
        Assert.Equal(""bot"", SmartLinks.ClassifyDevice(
            ""Googlebot/2.1 (+http://google.com/bot)""));
        Assert.Equal(""desktop"", SmartLinks.ClassifyDevice(null));
    }

    [Fact]
    public void ResolveSmartTarget()
    {
        var rules = new Dictionary<string, string>
        {
            [""mobile""] = ""https://m.example.com"",
            [""default""] = ""https://example.com"",
        };
        Assert.Equal(""https://m.example.com"",
            SmartLinks.ResolveSmartTarget(rules, ""mobile"", ""https://example.com""));
        Assert.Equal(""https://example.com"",
            SmartLinks.ResolveSmartTarget(rules, ""desktop"", ""https://example.com""));
        Assert.Equal(""https://fallback.example"",
            SmartLinks.ResolveSmartTarget(new Dictionary<string, string>(),
                ""mobile"", ""https://fallback.example""));
    }

    [Fact]
    public async Task SmartClassifyEndpoint()
    {
        var r = await Client().PostAsJsonAsync(""/api/smart/classify"",
            new { user_agent = ""Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)"" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(""mobile"", JsonSerializer.Deserialize<JsonElement>(
            await r.Content.ReadAsStringAsync()).GetProperty(""device"").GetString());
    }
}
";

    public static string SmartModule(bool full) => full ? SmartLinksFullCs : SmartLinksHealthCs;
    public static string SmartEndpoints(bool full) => full ? SmartEndpointsFull : SmartEndpointsHealth;

    public static string SmartTests(bool full) =>
        SmartTestsHeader.Replace("__SCOPE__", full ? "full" : "health monitoring")
        + (full ? SmartTestsFullExtra : "}\n");

    // =======================================================================
    // File set assembly
    // =======================================================================

    /// <summary>Return the full file set (service + tests) for a variant.</summary>
    public static List<FileSpec> GetFiles(string variant)
    {
        if (variant != "v1" && variant != "v2")
            throw new ArgumentException($"unknown variant: {variant}");
        var serviceFiles = new List<FileSpec>
        {
            new("Shortener/Shortener.csproj", ShortenerCsproj),
            new("Shortener/ShortenerOptions.cs", ShortenerOptionsCs),
            new("Shortener/ApiKeyAuthentication.cs", ApiKeyAuthenticationCs),
            new("Shortener/Models.cs", ModelsCs),
            new("Shortener/Validators.cs", ValidatorsCs),
            new("Shortener/UrlStore.cs", UrlStoreCs),
            new("Shortener/IUrlRepository.cs", IUrlRepositoryCs),
            new("Shortener/UrlService.cs", UrlServiceCs),
            new("Shortener/ClickAnalytics.cs", ClickAnalyticsCs),
            new("Shortener/RateLimiter.cs", RateLimiterCs),
            new("Shortener/ApiExceptionHandler.cs", ApiExceptionHandlerCs),
            new("Shortener/RequestCorrelationMiddleware.cs", RequestCorrelationMiddlewareCs),
            new("Shortener/SwaggerDocumentation.cs", SwaggerDocumentationCs),
            new("Shortener.Tests/SwaggerTests.cs", SwaggerTestsCs),
            new("Shortener/Program.cs", ProgramCs),
            new("Shortener.Tests/Shortener.Tests.csproj", TestsCsproj),
            new("Shortener.Tests/ExceptionHandlingTests.cs", ExceptionHandlingTestsCs),
        };
        if (variant == "v1")
        {
            serviceFiles = ToV1(serviceFiles);
            serviceFiles.Add(new FileSpec(
                "Shortener.Tests/ServiceTestsV1.cs", ServiceTestsV1Cs));
        }
        else
        {
            serviceFiles.Add(new FileSpec(
                "Shortener.Tests/ServiceTests.cs", ServiceTestsCs));
        }
        return serviceFiles;
    }

    /// <summary>Write the variant's files under destDir. Returns written paths.</summary>
    public static List<string> Materialize(string variant, string destDir)
    {
        var written = new List<string>();
        foreach (var spec in GetFiles(variant))
        {
            var path = Path.Combine(destDir, spec.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, spec.Content);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Unified diff of v1 -&gt; v2 (used by docs and the brownfield scenario).</summary>
    public static string DiffVariants()
    {
        var v1 = GetFiles("v1").ToDictionary(s => s.Path, s => s.Content);
        var v2 = GetFiles("v2").ToDictionary(s => s.Path, s => s.Content);
        var sb = new StringBuilder();
        foreach (var path in v1.Keys.Union(v2.Keys).OrderBy(x => x))
        {
            v1.TryGetValue(path, out var a);
            v2.TryGetValue(path, out var b);
            if (a != b)
                sb.Append(UnifiedDiff(a ?? "", b ?? "", $"v1/{path}", $"v2/{path}"));
        }
        return sb.ToString();
    }

    // ---- minimal unified diff (LCS-based, for small generated files) ----
    private static string UnifiedDiff(string a, string b, string fromFile, string toFile)
    {
        var aLines = a.Split('\n');
        var bLines = b.Split('\n');
        var ops = DiffOps(aLines, bLines);
        var sb = new StringBuilder();
        sb.Append($"--- {fromFile}\n+++ {toFile}\n");
        int ia = 0, ib = 0;
        foreach (var (op, x, y) in ops)
        {
            switch (op)
            {
                case '=': sb.Append($" {aLines[x]}\n"); ia++; ib++; break;
                case '-': sb.Append($"-{aLines[x]}\n"); ia++; break;
                case '+': sb.Append($"+{bLines[y]}\n"); ib++; break;
            }
        }
        return sb.ToString();
    }

    private static List<(char Op, int X, int Y)> DiffOps(string[] a, string[] b)
    {
        // LCS table (files are small; O(n*m) is fine).
        var n = a.Length; var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1
                                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var ops = new List<(char, int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(('=', x, y)); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', x, y)); x++; }
            else { ops.Add(('+', x, y)); y++; }
        }
        while (x < n) { ops.Add(('-', x, y)); x++; }
        while (y < m) { ops.Add(('+', x, y)); y++; }
        return ops;
    }
}
