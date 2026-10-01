using System.Net;
using System.Net.Http.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class UrlServiceTests
{
    [Fact]
    public void InvalidInputNeverReachesPersistence()
    {
        var service = Service(new StubRepository());
        Assert.Equal(UrlCreationStatus.InvalidRequest, service.Create("alice", new() { Url = "invalid" }).Status);
        Assert.Equal(UrlCreationStatus.InvalidRequest, service.Create("alice", new() { Url = "https://example.com", ExpiresInDays = 0 }).Status);
        Assert.Equal(UrlCreationStatus.InvalidIdempotencyKey, service.Create("alice", new() { Url = "https://example.com" }, "two words").Status);
    }

    [Fact]
    public void ReplayUsesValidatedRequestAndDoesNotAllocateOrWriteAgain()
    {
        IdempotencyRecord? saved = null;
        var writes = 0;
        var repository = new StubRepository
        {
            FindReplay = (owner, key) => { Assert.Equal("alice", owner); Assert.Equal("key", key); return saved; },
            Insert = (owner, hash, body) =>
            {
                Assert.Equal("alice", owner);
                saved = new(hash, body);
                writes++;
                return new(CreateUrlOutcome.Created);
            },
        };
        var service = Service(repository);
        var first = service.Create("alice", new() { Url = "https://example.com", CustomAlias = "my-link" }, "key");
        var replay = service.Create("alice", new() { Url = " https://example.com ", CustomAlias = "my-link" }, "key");
        var changed = service.Create("alice", new() { Url = "https://other.example", CustomAlias = "my-link" }, "key");
        Assert.Equal(UrlCreationStatus.Created, first.Status);
        Assert.Equal(UrlCreationStatus.Replay, replay.Status);
        Assert.Equal(saved!.Body, replay.ReplayJson);
        Assert.Equal(UrlCreationStatus.IdempotencyConflict, changed.Status);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void CreationAndRedirectUseInjectedClockAtExpiryBoundary()
    {
        var clock = new TestClock();
        var clicks = new List<string>();
        var repository = new StubRepository
        {
            Insert = (_, _, _) => new(CreateUrlOutcome.Created),
            Record = (_, timestamp) => clicks.Add(timestamp),
        };
        var service = Service(repository, clock);
        var created = service.Create("alice", new() { Url = "https://example.com", CustomAlias = "clock-link", ExpiresInDays = 1 });
        Assert.Equal(UrlCreationStatus.Created, created.Status);
        Assert.Equal(clock.Now, DateTimeOffset.Parse(created.Value!.CreatedAt));
        Assert.Equal(clock.Now.AddDays(1), DateTimeOffset.Parse(created.Value.ExpiresAt!));
        repository.Find = code => new(code, created.Value.Url, created.Value.CreatedAt, created.Value.ExpiresAt);
        clock.Now = clock.Now.AddDays(1).AddTicks(-1);
        Assert.Equal(RedirectStatus.Found, service.Redirect("clock-link", new(null, null, null)).Status);
        Assert.Equal(clock.Now, DateTimeOffset.Parse(Assert.Single(clicks)));
        clock.Now = clock.Now.AddTicks(1);
        Assert.Equal(RedirectStatus.Expired, service.Redirect("clock-link", new(null, null, null)).Status);
        Assert.Single(clicks);
    }

    [Fact]
    public void UnownedStatsNeverReadClickData()
    {
        var repository = new StubRepository
        {
            FindOwned = (code, owner) =>
            {
                Assert.Equal("private-link", code);
                Assert.Equal("bob", owner);
                return null;
            },
        };
        Assert.Null(Service(repository).Stats("private-link", "bob"));
    }

    [Fact]
    public void AtomicPersistenceConflictIsNotReportedAsSuccess()
    {
        var repository = new StubRepository
        {
            FindReplay = (_, _) => null,
            Insert = (_, _, _) => new(CreateUrlOutcome.IdempotencyConflict),
        };
        var result = Service(repository).Create("alice", new() { Url = "https://example.com", CustomAlias = "race-link" }, "key");
        Assert.Equal(UrlCreationStatus.IdempotencyConflict, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task HttpEndpointsUseTheRegisteredRepositoryAbstraction()
    {
        var repository = new StubRepository
        {
            List = owner =>
            {
                Assert.Equal("test-owner", owner);
                return new() { new() { Code = "injected", Url = "https://example.com", CreatedAt = "now" } };
            },
        };
        using var factory = new ShortenerTestFactory();
        using var substituted = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IUrlRepository>(repository)));
        using var client = substituted.CreateClient();
        using var response = await client.GetAsync("/api/urls");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("injected", Assert.Single((await response.Content.ReadFromJsonAsync<List<UrlRecordDto>>())!).Code);
        // A concrete SQLite dependency in the endpoint would return an empty list instead.
    }

    [Fact]
    public void DefaultRepositoryRegistrationSharesOneStore()
    {
        using var factory = new ShortenerTestFactory();
        Assert.Same(factory.Services.GetRequiredService<UrlStore>(), factory.Services.GetRequiredService<IUrlRepository>());
        Assert.Same(factory.Services.GetRequiredService<UrlService>(), factory.Services.GetRequiredService<UrlService>());
    }

    private static UrlService Service(IUrlRepository repository, TimeProvider? clock = null) =>
        new(repository, new ServiceOptions { BaseUrl = "https://short.example/" }, clock ?? new TestClock());

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Unexpected calls throw, making tests catch accidental storage access.
    private sealed class StubRepository : IUrlRepository
    {
        public Func<string, UrlRow?> Find { get; set; } = _ => throw new NotSupportedException();
        public Func<string, string, UrlRow?> FindOwned { get; init; } = (_, _) => throw new NotSupportedException();
        public Func<string, List<UrlRecordDto>> List { get; init; } = _ => throw new NotSupportedException();
        public Func<string, string, IdempotencyRecord?> FindReplay { get; init; } = (_, _) => throw new NotSupportedException();
        public Func<string, string, string, CreateUrlResult> Insert { get; init; } = (_, _, _) => throw new NotSupportedException();
        public Action<string, string> Record { get; init; } = (_, _) => throw new NotSupportedException();
        public UrlRow? Get(string code) => Find(code);
        public UrlRow? GetOwned(string code, string ownerId) => FindOwned(code, ownerId);
        public List<UrlRecordDto> ListOwned(string ownerId) => List(ownerId);
        public IdempotencyRecord? GetIdempotency(string ownerId, string key) => FindReplay(ownerId, key);
        public CreateUrlResult CreateWithIdempotency(string code, string url, string createdAt, string? expiresAt,
            string ownerId, string? key, string requestHash, string body) => Insert(ownerId, requestHash, body);
        public void RecordClick(string code, string ts, string? referrer, string? userAgent, string? ip) => Record(code, ts);
        public void CheckReady() => throw new NotSupportedException();
        public bool DeleteOwned(string code, string ownerId) => throw new NotSupportedException();
        public List<ClickRow> ClicksFor(string code, string ownerId) => throw new NotSupportedException();
        public int ClickCount(string code, string ownerId) => throw new NotSupportedException();
    }
}
