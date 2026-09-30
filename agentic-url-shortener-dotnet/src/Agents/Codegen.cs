// Code generation: single source of truth for the workspace service.
//
// The greenfield implementer materializes the ``v1`` file set into its run
// workspace; the brownfield implementer evolves ``v1`` into ``v2`` (custom
// aliases, 410-for-expired bug fix, validators refactor). The templates below
// are real, compiling C# — the scenario workspaces build them with
// ``dotnet test`` and the results gate the release.
//
// v1 is derived from v2 via _to_v1() with exact-match anchors (mirroring the
// Python codegen), so the two variants can never drift apart silently.

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
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Shortener</RootNamespace>
    <AssemblyName>Shortener</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.Data.Sqlite"" Version=""8.0.11"" />
  </ItemGroup>

</Project>
";

    // =======================================================================
    // src/Shortener/ShortenerOptions.cs  (identical in v1 and v2)
    // =======================================================================
    private const string ShortenerOptionsCs = @"// Service configuration. Values come from the environment:
//   SHORTENER_DB, SHORTENER_BASE_URL,
//   SHORTENER_RATE_PER_MINUTE / SHORTENER_RATE_BURST

namespace Shortener;

public sealed class ShortenerOptions
{
    public string DbPath { get; set; } = ""shortener.db"";
    public string BaseUrl { get; set; } = ""http://localhost:8000"";
    public double RatePerMinute { get; set; } = 60.0;
    public int RateBurst { get; set; } = 10;

    public static ShortenerOptions FromEnvironment() => new()
    {
        DbPath = Environment.GetEnvironmentVariable(""SHORTENER_DB"") ?? ""shortener.db"",
        BaseUrl = (Environment.GetEnvironmentVariable(""SHORTENER_BASE_URL"") ?? ""http://localhost:8000"").TrimEnd('/'),
        RatePerMinute = double.TryParse(Environment.GetEnvironmentVariable(""SHORTENER_RATE_PER_MINUTE""),
                                        out var rpm) ? rpm : 60.0,
        RateBurst = int.TryParse(Environment.GetEnvironmentVariable(""SHORTENER_RATE_BURST""),
                                 out var burst) ? burst : 10,
    };
}
";

    // =======================================================================
    // src/Shortener/Models.cs — v2. v1 is derived by removing CustomAlias.
    // =======================================================================
    private const string ModelsCs = @"// Data models for the URL shortener API.
// JSON is serialized snake_case (short_url, created_at, ...) to preserve the
// API contract; see Program.cs HTTP JSON configuration.

namespace Shortener;

public sealed class CreateUrlRequest
{
    public string Url { get; set; } = string.Empty;
    public string? CustomAlias { get; set; }
    public int? ExpiresInDays { get; set; }
}

public sealed class ShortUrlResponse
{
    public string Code { get; set; } = string.Empty;
    public string ShortUrl { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? ExpiresAt { get; set; }
}

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

public sealed class UrlStats
{
    public string Code { get; set; } = string.Empty;
    public int TotalClicks { get; set; }
    public List<DayCount> ClicksByDay { get; set; } = new();
    public Dictionary<string, int> Referrers { get; set; } = new();
    public Dictionary<string, int> UserAgents { get; set; } = new();
    public string? LastClickedAt { get; set; }
}

public sealed record UrlRow(string Code, string Url, string CreatedAt, string? ExpiresAt);
public sealed record ClickRow(string Code, string Ts, string? Referrer, string? UserAgent, string? Ip);
";

    // =======================================================================
    // src/Shortener/Validators.cs  (v2 only — extracted from Program.cs in
    // the brownfield refactor so validation is shared and unit-testable)
    // =======================================================================
    private const string ValidatorsCs = @"// Shared input validation helpers.
//
// Extracted from Program.cs during the brownfield refactor so that URL/alias
// validation lives in one place and can be unit-tested independently of HTTP.

using System.Text.RegularExpressions;

namespace Shortener;

public static class Validators
{
    private static readonly Regex AliasRegex =
        new(@""^[A-Za-z0-9_-]{3,32}$"", RegexOptions.Compiled);

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

    public static string ValidateAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !AliasRegex.IsMatch(alias))
            throw new ArgumentException(""custom_alias must be 3-32 chars of [A-Za-z0-9_-]"");
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

public sealed class UrlStore
{
    private const string Schema = @""
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
);"";

    private readonly string _dbPath;
    private readonly object _lock = new();
    private SqliteConnection? _conn;
    private bool _initialized;

    public UrlStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    private void Ensure()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            _conn = new SqliteConnection($""Data Source={_dbPath}"");
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

    public bool Create(string code, string url, string createdAt, string? expiresAt = null)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = Conn.CreateCommand();
                cmd.CommandText = ""INSERT INTO urls(code, url, created_at, expires_at) VALUES ($code, $url, $created, $expires)"";
                cmd.Parameters.AddWithValue(""$code"", code);
                cmd.Parameters.AddWithValue(""$url"", url);
                cmd.Parameters.AddWithValue(""$created"", createdAt);
                cmd.Parameters.AddWithValue(""$expires"", (object?)expiresAt ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
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

    public bool Delete(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = ""DELETE FROM urls WHERE code = $code"";
            cmd.Parameters.AddWithValue(""$code"", code);
            var deleted = cmd.ExecuteNonQuery();
            using var cmd2 = Conn.CreateCommand();
            cmd2.CommandText = ""DELETE FROM clicks WHERE code = $code"";
            cmd2.Parameters.AddWithValue(""$code"", code);
            cmd2.ExecuteNonQuery();
            return deleted > 0;
        }
    }

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

    public List<ClickRow> ClicksFor(string code)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = ""SELECT code, ts, referrer, user_agent, ip FROM clicks WHERE code = $code ORDER BY ts"";
            cmd.Parameters.AddWithValue(""$code"", code);
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
            cmd.CommandText = ""SELECT COUNT(*) FROM clicks WHERE code = $code"";
            cmd.Parameters.AddWithValue(""$code"", code);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void SaveIdempotency(string key, string body, string createdAt)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = ""INSERT OR IGNORE INTO idempotency(key, body, created_at) VALUES ($key, $body, $created)"";
            cmd.Parameters.AddWithValue(""$key"", key);
            cmd.Parameters.AddWithValue(""$body"", body);
            cmd.Parameters.AddWithValue(""$created"", createdAt);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetIdempotency(string key)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = ""SELECT body FROM idempotency WHERE key = $key"";
            cmd.Parameters.AddWithValue(""$key"", key);
            return cmd.ExecuteScalar() as string;
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
    // src/Shortener/Program.cs — v2. v1 is derived via _to_v1().
    //
    // NOTE: the catch-all ``/{code}`` redirect route is registered LAST so it
    // can never shadow /health, /ready, or /api/* routes.
    // =======================================================================
    private const string ProgramCs = @"// URL shortener service — workspace build (variant v2).
// Generated by the implementer agent from the reviewed codegen spec.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shortener;

var app = CreateApp(ShortenerOptions.FromEnvironment());
app.Run();

WebApplication CreateApp(ShortenerOptions? options = null)
{
    options ??= ShortenerOptions.FromEnvironment();

    var builder = WebApplication.CreateBuilder();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<UrlStore>(sp =>
        new UrlStore(sp.GetRequiredService<ShortenerOptions>().DbPath));
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

    string ClientIp(HttpContext c) =>
        c.Request.Headers.TryGetValue(""X-Forwarded-For"", out var fwd)
            ? fwd.ToString().Split(',')[0].Trim()
            : c.Connection.RemoteIpAddress?.ToString() ?? ""unknown"";

    string? Referrer(HttpContext c) =>
        c.Request.Headers.TryGetValue(""Referer"", out var r) ? r.ToString() : null;

    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path != ""/health"" && path != ""/ready"")
        {
            var limiter = context.RequestServices.GetRequiredService<RateLimiter>();
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

    app.MapGet(""/health"", () => Results.Ok(new { status = ""ok"" }));
    app.MapGet(""/ready"", (HttpContext context) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        string db;
        try { store.ListAll(); db = ""ok""; }
        catch (Exception) { db = ""error""; }
        return Results.Ok(new { status = db == ""ok"" ? ""ready"" : ""degraded"", db });
    });

    app.MapPost(""/api/urls"", (HttpContext context, CreateUrlRequest body) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var opts = context.RequestServices.GetRequiredService<ShortenerOptions>();

        if (context.Request.Headers.TryGetValue(""Idempotency-Key"", out var keyValues))
        {
            var key = keyValues.ToString();
            var saved = store.GetIdempotency(key);
            if (saved is not null)
                return Results.Json(JsonSerializer.Deserialize<JsonElement>(saved), statusCode: 200);
        }

        string url;
        try { url = Validators.ValidateUrl(body.Url); }
        catch (ArgumentException exc)
        {
            return Results.Json(new { detail = exc.Message }, statusCode: 422);
        }
        if (body.ExpiresInDays is not null && body.ExpiresInDays <= 0)
            return Results.Json(new { detail = ""expires_in_days must be positive"" }, statusCode: 422);

        string code;
        if (!string.IsNullOrEmpty(body.CustomAlias))
        {
            try { code = Validators.ValidateAlias(body.CustomAlias); }
            catch (ArgumentException exc)
            {
                return Results.Json(new { detail = exc.Message }, statusCode: 422);
            }
            if (store.Get(code) is not null)
                return Results.Json(new { detail = ""custom alias already in use"" }, statusCode: 409);
        }
        else
        {
            code = string.Empty;
            for (var i = 0; i < 10; i++)
            {
                var candidate = GenerateCode();
                if (store.Get(candidate) is null) { code = candidate; break; }
            }
            if (string.IsNullOrEmpty(code))
                return Results.Json(new { detail = ""could not allocate a short code"" }, statusCode: 500);
        }

        var now = DateTime.UtcNow;
        var createdAt = now.ToString(""o"");
        string? expiresAt = null;
        if (body.ExpiresInDays is not null)
            expiresAt = now.AddDays(body.ExpiresInDays.Value).ToString(""o"");
        if (!store.Create(code, url, createdAt, expiresAt))
            return Results.Json(new { detail = ""custom alias already in use"" }, statusCode: 409);

        var payload = new ShortUrlResponse
        {
            Code = code,
            ShortUrl = $""{opts.BaseUrl.TrimEnd('/')}/{code}"",
            Url = url,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
        var bodyJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        if (context.Request.Headers.TryGetValue(""Idempotency-Key"", out var keyValues2))
            store.SaveIdempotency(keyValues2.ToString(), bodyJson, createdAt);

        return Results.Json(JsonSerializer.Deserialize<JsonElement>(bodyJson), statusCode: 201);
    });

    app.MapGet(""/api/urls"", (HttpContext context) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var rows = store.ListAll();
        return Results.Ok(rows.Select(r => new UrlRecordDto
        {
            Code = r.Code, Url = r.Url, CreatedAt = r.CreatedAt,
            ExpiresAt = r.ExpiresAt, Clicks = store.ClickCount(r.Code),
        }).ToList());
    });

    app.MapGet(""/api/urls/{code}/stats"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        if (store.Get(code) is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        return Results.Json(ClickAnalytics.BuildStats(code, store.ClicksFor(code)),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        // NOTE: no DictionaryKeyPolicy — referrer / user-agent breakdown keys
        // are returned verbatim (matches the API contract).
    });

    app.MapGet(""/api/urls/{code}"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var row = store.Get(code);
        if (row is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        return Results.Ok(new UrlRecordDto
        {
            Code = row.Code, Url = row.Url, CreatedAt = row.CreatedAt,
            ExpiresAt = row.ExpiresAt, Clicks = store.ClickCount(row.Code),
        });
    });

    app.MapDelete(""/api/urls/{code}"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        if (!store.Delete(code))
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        return Results.NoContent();
    });

    // -- EXTENSION POINT: smart-link endpoints (ambiguous scenario) --

    app.MapGet(""/{code}"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var row = store.Get(code);
        if (row is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        if (row.ExpiresAt is not null &&
            DateTime.TryParse(row.ExpiresAt, out var exp) && exp <= DateTime.UtcNow)
            return Results.Json(new { detail = ""link expired"" }, statusCode: 410);
        store.RecordClick(row.Code, DateTime.UtcNow.ToString(""o""),
                          Referrer(context),
                          context.Request.Headers.UserAgent.ToString(),
                          ClientIp(context));
        return Results.Redirect(row.Url, preserveMethod: true);
    });

    return app;
}

static string GenerateCode(int length = 7)
{
    const string alphabet = ""abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"";
    var sb = new StringBuilder(length);
    var bytes = new byte[length];
    RandomNumberGenerator.Fill(bytes);
    foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
    return sb.ToString();
}

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
";

    // =======================================================================
    // src/Shortener.Tests/Shortener.Tests.csproj
    // =======================================================================
    private const string TestsCsproj = @"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
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
    <PackageReference Include=""Microsoft.AspNetCore.Mvc.Testing"" Version=""8.0.11"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.11.1"" />
    <PackageReference Include=""xunit"" Version=""2.9.2"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.8.2"" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include=""..\Shortener\Shortener.csproj"" />
  </ItemGroup>

</Project>
";

    private const string TestsHeader = @"// Generated service test suite (__VARIANT_LABEL__).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shortener;
using Xunit;

public sealed class ShortenerTestFactory : WebApplicationFactory<Program>
{
    private readonly ShortenerOptions _options;

    public ShortenerTestFactory(ShortenerOptions? options = null)
    {
        _options = options ?? new ShortenerOptions
        {
            DbPath = "":memory:"",
            BaseUrl = ""http://test"",
            RatePerMinute = 6000,
            RateBurst = 1000,
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddSingleton(_options));
    }
}

public sealed class ServiceTests : IDisposable
{
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
        Assert.Contains(code, client.GetAsync(""/api/urls"").Result.Content.ReadAsStringAsync().Result);
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
        Assert.Contains(code, client.GetAsync(""/api/urls"").Result.Content.ReadAsStringAsync().Result);
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
    /// Derive the v1 (greenfield) file set from the v2 file set.
    /// v1 differences (documented, intentional):
    ///   * no custom_alias support in models / Program
    ///   * URL validation inline in Program.cs (Validators.cs does not exist yet)
    ///   * KNOWN BUG: expired links return 404 instead of 410 (fixed in brownfield)
    /// </summary>
    private static List<FileSpec> ToV1(List<FileSpec> files)
    {
        var result = new List<FileSpec>();
        foreach (var spec in files)
        {
            if (spec.Path == "Shortener/Validators.cs")
                continue; // does not exist in v1
            if (spec.Path == "Shortener/Models.cs")
            {
                var content = spec.Content.Replace(
                    "    public string? CustomAlias { get; set; }\n", "");
                result.Add(new FileSpec(spec.Path, content));
            }
            else if (spec.Path == "Shortener/Program.cs")
            {
                var content = spec.Content;
                var patches = new (string Old, string New)[]
                {
                    (@"        string url;
        try { url = Validators.ValidateUrl(body.Url); }
        catch (ArgumentException exc)
        {
            return Results.Json(new { detail = exc.Message }, statusCode: 422);
        }
        if (body.ExpiresInDays is not null && body.ExpiresInDays <= 0)
            return Results.Json(new { detail = ""expires_in_days must be positive"" }, statusCode: 422);

        string code;
        if (!string.IsNullOrEmpty(body.CustomAlias))
        {
            try { code = Validators.ValidateAlias(body.CustomAlias); }
            catch (ArgumentException exc)
            {
                return Results.Json(new { detail = exc.Message }, statusCode: 422);
            }
            if (store.Get(code) is not null)
                return Results.Json(new { detail = ""custom alias already in use"" }, statusCode: 409);
        }
        else
        {
            code = string.Empty;
            for (var i = 0; i < 10; i++)
            {
                var candidate = GenerateCode();
                if (store.Get(candidate) is null) { code = candidate; break; }
            }
            if (string.IsNullOrEmpty(code))
                return Results.Json(new { detail = ""could not allocate a short code"" }, statusCode: 500);
        }",
                     @"        // v1: inline validation (extracted to the shared validators
        // module in the brownfield refactor)
        var rawUrl = (body.Url ?? """").Trim();
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(parsedUrl.Host))
            return Results.Json(new { detail = ""url must be an absolute http(s) URL"" }, statusCode: 422);
        var url = rawUrl;
        if (body.ExpiresInDays is not null && body.ExpiresInDays <= 0)
            return Results.Json(new { detail = ""expires_in_days must be positive"" }, statusCode: 422);

        string code = GenerateCode();
        var attempts = 0;
        while (store.Get(code) is not null && attempts < 10)
        {
            code = GenerateCode();
            attempts++;
        }"),
                    (@"            return Results.Json(new { detail = ""link expired"" }, statusCode: 410);",
                     @"            // KNOWN BUG (fixed in brownfield): expired links are
            // indistinguishable from unknown ones (404 instead of 410).
            return Results.Json(new { detail = ""link expired"" }, statusCode: 404);"),
                };
                foreach (var (oldText, newText) in patches)
                {
                    if (CountOccurrences(content, oldText) != 1)
                        throw new InvalidOperationException(
                            $"v1 patch anchor not found exactly once in Program.cs: {oldText[..60]}");
                    content = content.Replace(oldText, newText);
                }
                result.Add(new FileSpec(spec.Path, content));
            }
            else
            {
                result.Add(spec);
            }
        }
        foreach (var spec in result)
            if (spec.Path == "Shortener/Models.cs" && spec.Content.Contains("CustomAlias"))
                throw new InvalidOperationException("v1 Models.cs still mentions CustomAlias");
        foreach (var spec in result)
            if (spec.Path == "Shortener/Program.cs" &&
                (spec.Content.Contains("Validators.ValidateUrl") ||
                 spec.Content.Contains("Validators.ValidateAlias")))
                throw new InvalidOperationException("v1 Program.cs still references Validators");
        return result;
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
// Pure helpers + thin HTTP wiring in Program.cs. All probes are defensive: a
// failing probe reports reachable=false instead of throwing.

namespace Shortener;

public static class SmartLinks
{
    /// <summary>Probe the target URL. Never throws.</summary>
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
        catch (Exception exc)
        {
            var msg = exc.Message;
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = false,
                [""status_code""] = null,
                [""error""] = msg.Length > 200 ? msg[..200] : msg,
            };
        }
    }
}
";

    private const string SmartLinksFullCs = @"// Smart-link features (scope: full — device-aware routing + health).
//
// Pure helpers + thin HTTP wiring in Program.cs. All probes are defensive: a
// failing probe reports reachable=false instead of throwing.

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

    /// <summary>Probe the target URL. Never throws.</summary>
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
        catch (Exception exc)
        {
            var msg = exc.Message;
            return new Dictionary<string, object?>
            {
                [""url""] = url,
                [""reachable""] = false,
                [""status_code""] = null,
                [""error""] = msg.Length > 200 ? msg[..200] : msg,
            };
        }
    }
}
";

    public const string SmartEndpointsHealth = @"    // -- smart-link endpoints (added by the smart-link feature) --
    app.MapGet(""/api/urls/{code}/health"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var row = store.Get(code);
        if (row is null)
            return Results.Json(new { detail = ""unknown code"" }, statusCode: 404);
        var result = new Dictionary<string, object?> { [""code""] = code };
        foreach (var (k, v) in SmartLinks.CheckLinkHealth(row.Url))
            result[k] = v;
        return Results.Json(result);
    });
";

    public const string SmartEndpointsFull = @"    // -- smart-link endpoints (added by the smart-link feature) --
    app.MapGet(""/api/urls/{code}/health"", (HttpContext context, string code) =>
    {
        var store = context.RequestServices.GetRequiredService<UrlStore>();
        var row = store.Get(code);
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
    private readonly ShortenerTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient();

    [Fact]
    public async Task CheckLinkHealthUnreachable()
    {
        var result = SmartLinks.CheckLinkHealth(""http://127.0.0.1:9/nope"", timeoutS: 1.0);
        Assert.False((bool)result[""reachable""]!);
        Assert.Null(result[""status_code""]);
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
            new("Shortener/Models.cs", ModelsCs),
            new("Shortener/Validators.cs", ValidatorsCs),
            new("Shortener/UrlStore.cs", UrlStoreCs),
            new("Shortener/ClickAnalytics.cs", ClickAnalyticsCs),
            new("Shortener/RateLimiter.cs", RateLimiterCs),
            new("Shortener/Program.cs", ProgramCs),
            new("Shortener.Tests/Shortener.Tests.csproj", TestsCsproj),
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
