// Service configuration. Values come from the environment so tests and
// operators can override them without code changes:
//   SHORTENER_DB          SQLite path (default "shortener.db"; ":memory:" in tests)
//   SHORTENER_BASE_URL    public base URL used to build short_url (default http://localhost:8000)
//   SHORTENER_RATE_PER_MINUTE / SHORTENER_RATE_BURST  rate limiter tuning
//   SHORTENER_TRUSTED_PROXIES  proxy IPs allowed to supply forwarded client IPs
//   SHORTENER_API_KEYS    JSON mapping stable owner IDs to secret API keys

namespace AgenticUrlShortener.Service;

public sealed class ServiceOptions
{
    public string DbPath { get; set; } = "shortener.db";
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public double RatePerMinute { get; set; } = 60.0;
    public int RateBurst { get; set; } = 10;
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();
    // Stable owner ID -> secret API key. Empty configuration disables management access.
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.Ordinal);

    public static ServiceOptions FromEnvironment() => new()
    {
        DbPath = Environment.GetEnvironmentVariable("SHORTENER_DB") ?? "shortener.db",
        BaseUrl = (Environment.GetEnvironmentVariable("SHORTENER_BASE_URL") ?? "http://localhost:8000").TrimEnd('/'),
        RatePerMinute = double.TryParse(Environment.GetEnvironmentVariable("SHORTENER_RATE_PER_MINUTE"),
                                        out var rpm) ? rpm : 60.0,
        RateBurst = int.TryParse(Environment.GetEnvironmentVariable("SHORTENER_RATE_BURST"),
                                 out var burst) ? burst : 10,
        TrustedProxies = (Environment.GetEnvironmentVariable("SHORTENER_TRUSTED_PROXIES") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ApiKeys = ReadApiKeys(Environment.GetEnvironmentVariable("SHORTENER_API_KEYS")),
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
            throw new InvalidOperationException("SHORTENER_API_KEYS must be a JSON object mapping unique owner IDs to API keys.");
        }
        return keys;
    }
}
