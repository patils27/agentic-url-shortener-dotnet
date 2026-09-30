// Service configuration. Values come from the environment so tests and
// operators can override them without code changes:
//   SHORTENER_DB          SQLite path (default "shortener.db"; ":memory:" in tests)
//   SHORTENER_BASE_URL    public base URL used to build short_url (default http://localhost:8000)
//   SHORTENER_RATE_PER_MINUTE / SHORTENER_RATE_BURST  rate limiter tuning

namespace AgenticUrlShortener.Service;

public sealed class ServiceOptions
{
    public string DbPath { get; set; } = "shortener.db";
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public double RatePerMinute { get; set; } = 60.0;
    public int RateBurst { get; set; } = 10;

    public static ServiceOptions FromEnvironment() => new()
    {
        DbPath = Environment.GetEnvironmentVariable("SHORTENER_DB") ?? "shortener.db",
        BaseUrl = (Environment.GetEnvironmentVariable("SHORTENER_BASE_URL") ?? "http://localhost:8000").TrimEnd('/'),
        RatePerMinute = double.TryParse(Environment.GetEnvironmentVariable("SHORTENER_RATE_PER_MINUTE"),
                                        out var rpm) ? rpm : 60.0,
        RateBurst = int.TryParse(Environment.GetEnvironmentVariable("SHORTENER_RATE_BURST"),
                                 out var burst) ? burst : 10,
    };
}
