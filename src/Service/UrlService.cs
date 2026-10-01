using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgenticUrlShortener.Service;

public enum UrlCreationStatus { Created, Replay, InvalidRequest, InvalidIdempotencyKey, IdempotencyConflict, CodeConflict, AllocationFailed }
public sealed record UrlCreationResult(UrlCreationStatus Status, ShortUrlResponse? Value = null,
    string? ReplayJson = null, string? Error = null);
public enum RedirectStatus { Found, Unknown, Expired }
public sealed record RedirectResult(RedirectStatus Status, string? Destination = null);
public sealed record ClickMetadata(string? Referrer, string? UserAgent, string? Ip);

/// <summary>URL business operations. No dependency on HTTP, the DI container, or SQLite.</summary>
public sealed class UrlService(IUrlRepository repository, ServiceOptions options, TimeProvider clock)
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
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
            return new(UrlCreationStatus.InvalidRequest, Error: "expires_in_days must be between 1 and 3650");
        if (idempotencyKey is not null && (idempotencyKey.Length is < 1 or > 128 ||
            idempotencyKey.Any(c => c < '!' || c > '~')))
            return new(UrlCreationStatus.InvalidIdempotencyKey, Error: "Idempotency-Key must be 1–128 printable ASCII characters without spaces");

        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            url, custom_alias = alias, expires_in_days = request.ExpiresInDays,
        })));
        if (idempotencyKey is not null && repository.GetIdempotency(owner, idempotencyKey) is { } saved)
            return saved.RequestHash == requestHash
                ? new(UrlCreationStatus.Replay, ReplayJson: saved.Body)
                : new(UrlCreationStatus.IdempotencyConflict, Error: "Idempotency-Key was already used with a different request");

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
                return new(UrlCreationStatus.AllocationFailed, Error: "could not allocate a short code");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var response = new ShortUrlResponse
        {
            Code = code, ShortUrl = $"{_baseUrl}/{code}", Url = url,
            CreatedAt = now.ToString("o"),
            ExpiresAt = request.ExpiresInDays is { } days ? now.AddDays(days).ToString("o") : null,
        };
        var result = repository.CreateWithIdempotency(code, url, response.CreatedAt, response.ExpiresAt,
            owner, idempotencyKey, requestHash, JsonSerializer.Serialize(response, JsonOptions));
        return result.Outcome switch
        {
            CreateUrlOutcome.Created => new(UrlCreationStatus.Created, Value: response),
            CreateUrlOutcome.Replay => new(UrlCreationStatus.Replay, ReplayJson: result.Replay),
            CreateUrlOutcome.IdempotencyConflict => new(UrlCreationStatus.IdempotencyConflict,
                Error: "Idempotency-Key was already used with a different request"),
            CreateUrlOutcome.CodeConflict => new(UrlCreationStatus.CodeConflict,
                Error: "short code already in use; retry with a new code"),
            _ => throw new InvalidOperationException("Unknown persistence outcome."),
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
        repository.RecordClick(code, now.UtcDateTime.ToString("o"), click.Referrer, click.UserAgent, click.Ip);
        return new(RedirectStatus.Found, row.Url);
    }

    public void CheckReady() => repository.CheckReady();
}
