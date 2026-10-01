namespace AgenticUrlShortener.Service;

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
