// HTTP layer: builds the WebApplication with all routes.
//
// A static factory (instead of top-level-only setup) so tests can build the
// app with custom ServiceOptions while production reads the environment.

using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;

namespace AgenticUrlShortener.Service;

public static class ShortenerApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static WebApplication CreateApp(ServiceOptions? options = null)
    {
        options ??= ServiceOptions.FromEnvironment();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ApiKeyAuthentication>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IUrlRepository>(sp => sp.GetRequiredService<UrlStore>());
        builder.Services.AddSingleton<UrlService>();
        builder.Services.AddSingleton<UrlStore>(sp =>
            new UrlStore(sp.GetRequiredService<ServiceOptions>().DbPath, sp.GetRequiredService<TimeProvider>()));
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

        var limiter = app.Services.GetRequiredService<RateLimiter>();

        // ---- rate limiting (health/ready bypass) -----------------------------
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path != "/health" && path != "/ready")
            {
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

        ApiKeyAuthentication.ProtectManagementApi(app);

        // HTTP handlers bind requests and map business outcomes to the API contract.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/ready", (UrlService service) =>
        {
            var ready = service.IsReady();
            return Results.Json(new { status = ready ? "ready" : "degraded", db = ready ? "ok" : "error" },
                statusCode: ready ? 200 : 503);
        });

        app.MapPost("/api/urls", (HttpContext context, CreateUrlRequest body, UrlService service) =>
        {
            string? key = null;
            if (context.Request.Headers.TryGetValue("Idempotency-Key", out var values))
            {
                if (values.Count != 1 || values[0] is null)
                    return Results.Json(new { detail = "Idempotency-Key must be 1–128 printable ASCII characters without spaces" }, statusCode: 400);
                key = values[0];
            }
            var result = service.Create(ApiKeyAuthentication.OwnerOf(context), body, key);
            return result.Status switch
            {
                UrlCreationStatus.Created => Results.Json(result.Value, JsonOptions, statusCode: 201),
                UrlCreationStatus.Replay => Results.Json(JsonSerializer.Deserialize<JsonElement>(result.ReplayJson!), statusCode: 200),
                UrlCreationStatus.InvalidIdempotencyKey => Results.Json(new { detail = result.Error }, statusCode: 400),
                UrlCreationStatus.InvalidRequest or UrlCreationStatus.IdempotencyConflict =>
                    Results.Json(new { detail = result.Error }, statusCode: 422),
                UrlCreationStatus.CodeConflict => Results.Json(new { detail = result.Error }, statusCode: 409),
                UrlCreationStatus.AllocationFailed => Results.Json(new { detail = result.Error }, statusCode: 500),
                _ => throw new InvalidOperationException("Unknown URL creation outcome."),
            };
        });

        app.MapGet("/api/urls", (HttpContext context, UrlService service) =>
            Results.Ok(service.List(ApiKeyAuthentication.OwnerOf(context))));

        app.MapGet("/api/urls/{code}/stats", (HttpContext context, string code, UrlService service) =>
        {
            var stats = service.Stats(code, ApiKeyAuthentication.OwnerOf(context));
            // Preserve referrer and user-agent dictionary keys verbatim.
            return stats is null
                ? Results.Json(new { detail = "unknown code" }, statusCode: 404)
                : Results.Json(stats, JsonOptions);
        });

        app.MapGet("/api/urls/{code}", (HttpContext context, string code, UrlService service) =>
        {
            var row = service.GetOwned(code, ApiKeyAuthentication.OwnerOf(context));
            return row is null ? Results.NotFound(new { detail = "unknown code" }) : Results.Ok(row);
        });

        app.MapDelete("/api/urls/{code}", (HttpContext context, string code, UrlService service) =>
            service.Delete(code, ApiKeyAuthentication.OwnerOf(context))
                ? Results.NoContent() : Results.NotFound(new { detail = "unknown code" }));

        // ---- redirect (catch-all, registered last) -----------------------------------
        app.MapGet("/{code}", (HttpContext context, string code, UrlService service) =>
        {
            var result = service.Redirect(code, new ClickMetadata(Referrer(context),
                context.Request.Headers.UserAgent.ToString(), ClientIp(context)));
            return result.Status switch
            {
                RedirectStatus.Unknown => Results.Json(new { detail = "unknown code" }, statusCode: 404),
                RedirectStatus.Expired => Results.Json(new { detail = "link expired" }, statusCode: 410),
                RedirectStatus.Found => Results.Redirect(result.Destination!, preserveMethod: true),
                _ => throw new InvalidOperationException("Unknown redirect outcome."),
            };
        });

        return app;
    }
}
