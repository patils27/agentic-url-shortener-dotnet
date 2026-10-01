using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class AccessTests
{
    private const string AliceKey = "alice-test-only-0123456789-abcdefghijklmnopqrstuvwxyz";
    private const string BobKey = "bob-test-only-0123456789-abcdefghijklmnopqrstuvwxyz";

    private static ShortenerTestFactory Factory(bool configured = true) => new(new ServiceOptions
    {
        DbPath = ":memory:", RatePerMinute = 6000, RateBurst = 1000,
        ApiKeys = configured ? new() { ["alice"] = AliceKey, ["bob"] = BobKey } : new(),
    }, authenticate: false);

    private static HttpClient Client(ShortenerTestFactory factory, string? key = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, key);
        return client;
    }

    private static async Task<string> Create(HttpClient client, object? request = null)
    {
        using var response = await client.PostAsJsonAsync("/api/urls", request ?? new { url = "https://example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task ManagementRejectsUnauthenticatedRequests(string? key)
    {
        using var factory = Factory();
        using var client = Client(factory, key);
        foreach (var path in new[] { "/api/urls", "/API/urls", "/api/urls/abc", "/api/urls/abc/stats" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("ApiKey", response.Headers.WwwAuthenticate.Single().Scheme);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/urls/abc")).StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Fact]
    public async Task MultipleCredentialsAreRejected()
    {
        using var factory = Factory();
        using var client = Client(factory, AliceKey);
        client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, BobKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/urls")).StatusCode);
    }

    [Fact]
    public async Task MissingConfigurationFailsClosedWhilePublicRoutesRemainAvailable()
    {
        using var factory = Factory(configured: false);
        using var client = Client(factory);
        factory.Services.GetRequiredService<UrlStore>().Create("legacy", "https://example.com", DateTime.UtcNow.ToString("o"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/urls")).StatusCode);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await client.GetAsync("/legacy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task OwnersCannotReadAnalyticsOrDeleteEachOthersLinks()
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        using var bob = Client(factory, BobKey);
        using var anonymous = Client(factory);
        var code = await Create(alice, new { url = "https://example.com", owner_id = "bob" });
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await anonymous.GetAsync("/" + code)).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/urls")).EnumerateArray());
        var owned = Assert.Single((await alice.GetFromJsonAsync<JsonElement>("/api/urls")).EnumerateArray());
        Assert.Equal(1, owned.GetProperty("clicks").GetInt32());
        foreach (var suffix in new[] { "", "/stats" })
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/urls/{code}{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/urls/{code}")).StatusCode);
        Assert.Equal(1, (await alice.GetFromJsonAsync<JsonElement>($"/api/urls/{code}/stats")).GetProperty("total_clicks").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/urls/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/urls/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/" + code)).StatusCode);
    }

    [Fact]
    public async Task SameIdempotencyKeyIsIndependentAcrossOwners()
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        using var bob = Client(factory, BobKey);
        alice.DefaultRequestHeaders.Add("Idempotency-Key", "shared");
        bob.DefaultRequestHeaders.Add("Idempotency-Key", "shared");
        var codes = await Task.WhenAll(Create(alice), Create(bob));
        Assert.NotEqual(codes[0], codes[1]);
        Assert.Equal(2, factory.Services.GetRequiredService<UrlStore>().ListAll().Count);
    }

    [Theory]
    [InlineData("{\"url\":\"https://other.example\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"custom_alias\":\"different\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"expires_in_days\":2}")]
    public async Task ChangedRequestCannotReplayAnExistingKey(string changed)
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        alice.DefaultRequestHeaders.Add("Idempotency-Key", "matched");
        await Create(alice);
        using var response = await alice.PostAsync("/api/urls", new StringContent(changed, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Fact]
    public async Task EquivalentValidatedRequestsReplayExactBody()
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        alice.DefaultRequestHeaders.Add("Idempotency-Key", "equivalent");
        using var first = await alice.PostAsJsonAsync("/api/urls", new { url = "https://example.com" });
        using var second = await alice.PostAsJsonAsync("/api/urls", new { custom_alias = "", url = "  https://example.com  " });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentDifferentRequestsWithSameKeyCreateOnlyOneLink()
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        alice.DefaultRequestHeaders.Add("Idempotency-Key", "race");
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            alice.PostAsJsonAsync("/api/urls", new { url = $"https://example.com/{i}" })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Single(factory.Services.GetRequiredService<UrlStore>().ListAll());
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("too-long")]
    [InlineData("multiple")]
    public async Task InvalidIdempotencyHeadersDoNotCreateLinks(string key)
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        if (key.Length == 0)
        {
            // HttpClient drops empty default headers; inject the wire-level empty value directly.
            var context = await factory.Server.SendAsync(context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/api/urls";
                context.Request.Headers[ApiKeyAuthentication.HeaderName] = AliceKey;
                context.Request.Headers["Idempotency-Key"] = "";
                context.Request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes("{\"url\":\"https://example.com\"}");
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
            });
            Assert.Equal(400, context.Response.StatusCode);
            Assert.Empty(factory.Services.GetRequiredService<UrlStore>().ListAll());
            return;
        }
        alice.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", key == "too-long" ? new string('x', 129) : key);
        if (key == "multiple") alice.DefaultRequestHeaders.Add("Idempotency-Key", "second");
        using var response = await alice.PostAsJsonAsync("/api/urls", new { url = "https://example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    public async Task ApiAliasIsReserved(string alias)
    {
        using var factory = Factory();
        using var alice = Client(factory, AliceKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.PostAsJsonAsync("/api/urls", new { url = "https://example.com", custom_alias = alias })).StatusCode);
    }

    [Fact]
    public void MigrationPreservesLegacyLinksWithoutClaimingThemOrReplayingLegacyKeys()
    {
        var database = $"migration-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var original = new SqliteConnection($"Data Source={database};Pooling=False");
        original.Open();
        using var command = original.CreateCommand();
        command.CommandText = """
            CREATE TABLE urls(code TEXT PRIMARY KEY, url TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT);
            CREATE TABLE idempotency(key TEXT PRIMARY KEY, body TEXT NOT NULL);
            INSERT INTO urls VALUES('legacy','https://example.com','2026-01-01',NULL);
            INSERT INTO idempotency VALUES('legacy-key','private legacy response');
            """;
        command.ExecuteNonQuery();
        using var store = new UrlStore(database);
        Assert.NotNull(store.Get("legacy"));
        Assert.Empty(store.ListOwned("alice"));
        Assert.False(store.DeleteOwned("legacy", "alice"));
        Assert.Null(store.GetIdempotency("alice", "legacy-key"));
        Assert.Equal(CreateUrlOutcome.Created, store.CreateWithIdempotency("new-code", "https://new.example", "2026-01-01", null,
            "alice", "legacy-key", "new-hash", "new-body").Outcome);
        using var reopened = new UrlStore(database);
        Assert.Equal("new-body", reopened.GetIdempotency("alice", "legacy-key")?.Body);
        Assert.NotNull(reopened.Get("legacy"));
    }

    [Fact]
    public void IdempotencyExpiresAfter24HoursAndIsPurgedOnKeyedCreate()
    {
        var clock = new TestClock();
        using var store = new UrlStore(":memory:", clock);
        Assert.Equal(CreateUrlOutcome.Created, store.CreateWithIdempotency("first", "https://example.com", "now", null, "alice", "key", "hash", "first-body").Outcome);
        clock.Now += UrlStore.IdempotencyRetention - TimeSpan.FromSeconds(1);
        Assert.Equal("first-body", store.GetIdempotency("alice", "key")?.Body);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(store.GetIdempotency("alice", "key"));
        Assert.Equal(CreateUrlOutcome.Created, store.CreateWithIdempotency("second", "https://other.example", "now", null, "alice", "key", "new-hash", "second-body").Outcome);
        Assert.Equal("second-body", store.GetIdempotency("alice", "key")?.Body);
        Assert.Equal(2, store.ListOwned("alice").Count);
    }

    [Fact]
    public void KeyRotationKeepsStableOwnerIdentity()
    {
        var before = new ApiKeyAuthentication(new ServiceOptions { ApiKeys = new() { ["alice"] = AliceKey } });
        var after = new ApiKeyAuthentication(new ServiceOptions { ApiKeys = new() { ["alice"] = BobKey } });
        Assert.Null(after.Authenticate(AliceKey));
        using var store = new UrlStore(":memory:");
        store.Create("owned", "https://example.com", "now", ownerId: before.Authenticate(AliceKey));
        Assert.NotNull(store.GetOwned("owned", after.Authenticate(BobKey)!));
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("{\"alice\":12}")]
    [InlineData("{\"alice\":\"first\",\"alice\":\"second\"}")]
    public void MalformedConfigurationFailsWithoutEchoingSecret(string configuration)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ServiceOptions.ReadApiKeys(configuration));
        Assert.DoesNotContain(configuration, error.Message);
    }

    [Fact]
    public void AmbiguousOrWeakCredentialsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new ApiKeyAuthentication(new ServiceOptions { ApiKeys = new() { ["alice"] = "short" } }));
        Assert.Throws<InvalidOperationException>(() => new ApiKeyAuthentication(new ServiceOptions { ApiKeys = new() { ["alice"] = AliceKey, ["bob"] = AliceKey } }));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
