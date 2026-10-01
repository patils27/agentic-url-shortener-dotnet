using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace AgenticUrlShortener.Service;

/// <summary>Authenticate management API callers without storing their secrets in the database.</summary>
public sealed class ApiKeyAuthentication
{
    public const string HeaderName = "X-Api-Key";
    private readonly List<(string Owner, byte[] Hash)> _credentials = new();
    public bool IsConfigured => _credentials.Count > 0;

    public ApiKeyAuthentication(ServiceOptions options)
    {
        var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (owner, secret) in options.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || owner.Trim() != owner ||
                string.IsNullOrEmpty(secret) || secret.Length is < 32 or > 512 ||
                secret.Any(c => c < '!' || c > '~') || !uniqueKeys.Add(secret))
                throw new InvalidOperationException(
                    "API keys must be unique, 32–512 printable ASCII characters, with nonempty owner IDs of at most 128 characters.");
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
        ?? throw new InvalidOperationException("Authenticated owner is required.");

    public static void ProtectManagementApi(WebApplication app)
    {
        // Validate configured credentials at startup; no anonymous management fallback.
        var authentication = app.Services.GetRequiredService<ApiKeyAuthentication>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                if (!authentication.IsConfigured)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { detail = "API authentication is not configured" });
                    return;
                }
                var values = context.Request.Headers[HeaderName];
                var owner = values.Count == 1 ? authentication.Authenticate(values[0]) : null;
                if (owner is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "ApiKey";
                    await context.Response.WriteAsJsonAsync(new { detail = "a valid API key is required" });
                    return;
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, owner) }, "ApiKey"));
            }
            await next();
        });
    }
}
