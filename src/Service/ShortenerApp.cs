// HTTP layer: builds the WebApplication with all routes.
//
// A static factory (instead of top-level-only setup) so tests can build the
// app with custom ServiceOptions while production reads the environment.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;

namespace AgenticUrlShortener.Service;

public static class ShortenerApp
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    internal static string GenerateCode(RandomNumberGenerator rng, int length = 7)
    {
        var sb = new StringBuilder(length);
        var bytes = new byte[length];
        rng.GetBytes(bytes);
        foreach (var b in bytes) sb.Append(Alphabet[b % Alphabet.Length]);
        return sb.ToString();
    }

    public static WebApplication CreateApp(ServiceOptions? options = null)
    {
        options ??= ServiceOptions.FromEnvironment();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<UrlStore>(sp =>
            new UrlStore(sp.GetRequiredService<ServiceOptions>().DbPath));
        builder.Services.AddSingleton<RateLimiter>(sp =>
        {
            var o = sp.GetRequiredService<ServiceOptions>();
            return new RateLimiter(o.RatePerMinute, o.RateBurst);
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        var app = builder.Build();

        var trustedProxies = app.Services.GetRequiredService<ServiceOptions>().TrustedProxies;
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
            c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        string? Referrer(HttpContext c) =>
            c.Request.Headers.TryGetValue("Referer", out var r) ? r.ToString() : null;

        // ---- rate limiting (health/ready bypass) -----------------------------
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path != "/health" && path != "/ready")
            {
                var limiter = context.RequestServices.GetRequiredService<RateLimiter>();
                var (allowed, retryAfter) = limiter.Allow(ClientIp(context));
                if (!allowed)
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = ((int)retryAfter + 1).ToString();
                    await context.Response.WriteAsJsonAsync(new { detail = "rate limit exceeded" });
                    return;
                }
            }
            await next();
        });

        // ---- health -----------------------------------------------------------
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/ready", (HttpContext context) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            string db;
            try { store.ListAll(); db = "ok"; }
            catch (Exception) { db = "error"; }
            return Results.Ok(new { status = db == "ok" ? "ready" : "degraded", db });
        });

        // ---- create short URL ---------------------------------------------------
        app.MapPost("/api/urls", (HttpContext context, CreateUrlRequest body) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            var opts = context.RequestServices.GetRequiredService<ServiceOptions>();

            // idempotent replay
            if (context.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues))
            {
                var key = keyValues.ToString();
                var saved = store.GetIdempotency(key);
                if (saved is not null)
                    return Results.Json(JsonSerializer.Deserialize<JsonElement>(saved), statusCode: 200);
            }

            // validation
            string url;
            try { url = Validators.ValidateUrl(body.Url); }
            catch (ArgumentException exc)
            {
                return Results.Json(new { detail = exc.Message }, statusCode: 422);
            }
            if (body.ExpiresInDays is not null && body.ExpiresInDays <= 0)
                return Results.Json(new { detail = "expires_in_days must be positive" }, statusCode: 422);

            // code selection
            string code;
            if (!string.IsNullOrEmpty(body.CustomAlias))
            {
                try { code = Validators.ValidateAlias(body.CustomAlias); }
                catch (ArgumentException exc)
                {
                    return Results.Json(new { detail = exc.Message }, statusCode: 422);
                }
            }
            else
            {
                code = string.Empty;
                for (var i = 0; i < 10; i++)
                {
                    var candidate = GenerateCode(RandomNumberGenerator.Create());
                    if (store.Get(candidate) is null) { code = candidate; break; }
                }
                if (string.IsNullOrEmpty(code))
                    return Results.Json(new { detail = "could not allocate a short code" }, statusCode: 500);
            }

            // persistence
            var now = DateTime.UtcNow;
            var createdAt = now.ToString("o");
            string? expiresAt = null;
            if (body.ExpiresInDays is not null)
                expiresAt = now.AddDays(body.ExpiresInDays.Value).ToString("o");

            var payload = new ShortUrlResponse
            {
                Code = code,
                ShortUrl = $"{opts.BaseUrl.TrimEnd('/')}/{code}",
                Url = url,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
            var bodyJson = JsonSerializer.Serialize(payload,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
                });

            // The database transaction resolves concurrent requests with the same key.
            var idempotencyKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues2)
                ? keyValues2.ToString() : null;
            var result = store.CreateWithIdempotency(code, url, createdAt, expiresAt, idempotencyKey, bodyJson);
            if (result.Replay is not null)
                return Results.Json(JsonSerializer.Deserialize<JsonElement>(result.Replay), statusCode: 200);
            if (!result.Created)
                return Results.Json(new { detail = "custom alias already in use" }, statusCode: 409);

            return Results.Json(JsonSerializer.Deserialize<JsonElement>(bodyJson), statusCode: 201);
        });

        // ---- list / get / delete / stats -----------------------------------------
        app.MapGet("/api/urls", (HttpContext context) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            var rows = store.ListAll();
            return Results.Ok(rows.Select(r => new UrlRecordDto
            {
                Code = r.Code, Url = r.Url, CreatedAt = r.CreatedAt,
                ExpiresAt = r.ExpiresAt, Clicks = store.ClickCount(r.Code),
            }).ToList());
        });

        app.MapGet("/api/urls/{code}/stats", (HttpContext context, string code) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            if (store.Get(code) is null)
                return Results.Json(new { detail = "unknown code" }, statusCode: 404);
            // NOTE: no DictionaryKeyPolicy — referrer / user-agent breakdown keys
            // are returned verbatim (matches the API contract).
            return Results.Json(ClickAnalytics.BuildStats(code, store.ClicksFor(code)),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                });
        });

        app.MapGet("/api/urls/{code}", (HttpContext context, string code) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            var row = store.Get(code);
            if (row is null)
                return Results.Json(new { detail = "unknown code" }, statusCode: 404);
            return Results.Ok(new UrlRecordDto
            {
                Code = row.Code, Url = row.Url, CreatedAt = row.CreatedAt,
                ExpiresAt = row.ExpiresAt, Clicks = store.ClickCount(row.Code),
            });
        });

        app.MapDelete("/api/urls/{code}", (HttpContext context, string code) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            if (!store.Delete(code))
                return Results.Json(new { detail = "unknown code" }, statusCode: 404);
            return Results.NoContent();
        });

        // ---- redirect (catch-all, registered last) -----------------------------------
        app.MapGet("/{code}", (HttpContext context, string code) =>
        {
            var store = context.RequestServices.GetRequiredService<UrlStore>();
            var row = store.Get(code);
            if (row is null)
                return Results.Json(new { detail = "unknown code" }, statusCode: 404);
            if (row.ExpiresAt is not null &&
                DateTime.TryParse(row.ExpiresAt, out var exp) && exp <= DateTime.UtcNow)
                return Results.Json(new { detail = "link expired" }, statusCode: 410);
            store.RecordClick(row.Code, DateTime.UtcNow.ToString("o"),
                              Referrer(context),
                              context.Request.Headers.UserAgent.ToString(),
                              ClientIp(context));
            return Results.Redirect(row.Url, preserveMethod: true); // 307 Temporary Redirect
        });

        return app;
    }
}
