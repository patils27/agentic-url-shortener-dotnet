// Data models for the URL shortener API.
// JSON is serialized snake_case (short_url, created_at, ...) to preserve the
// API contract; see ShortenerApp HTTP JSON configuration.

namespace AgenticUrlShortener.Service;

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
