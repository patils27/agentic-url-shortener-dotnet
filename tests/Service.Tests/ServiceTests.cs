// HTTP coverage of the URL shortener API via WebApplicationFactory,
// plus cross-connection storage regression tests.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class ShortenerTestFactory : WebApplicationFactory<Program>
{
    public const string TestKey = "test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz";
    private readonly bool _authenticate;
    private readonly ServiceOptions _options;
    private readonly IPAddress? _remoteIp;

    public ShortenerTestFactory(ServiceOptions? options = null, IPAddress? remoteIp = null, bool authenticate = true)
    {
        _authenticate = authenticate;
        _remoteIp = remoteIp;
        _options = options ?? new ServiceOptions
        {
            DbPath = ":memory:",
            BaseUrl = "http://test",
            RatePerMinute = 6000,
            RateBurst = 1000,
        };
        if (authenticate) _options.ApiKeys["test-owner"] = TestKey;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (_authenticate) client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, TestKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_options);
            if (_remoteIp is not null)
                services.AddSingleton<IStartupFilter>(new RemoteIpFilter(_remoteIp));
        });
    }
}

file sealed class RemoteIpFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware();
        });
        next(app);
    };
}

public sealed class ServiceTests : IDisposable
{
    [Theory]
    [InlineData(null)]
    [InlineData("concurrent-alias")]
    public async Task ConcurrentIdempotentRequestsCreateOneUrl(string? alias)
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "concurrent-key");
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            client.PostAsJsonAsync("/api/urls", new { url = "https://example.com", custom_alias = alias })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Single(_factory.Services.GetRequiredService<UrlStore>().ListAll());
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task IdempotencyIsAtomicAcrossStoreConnections()
    {
        // Shared in-memory SQLite database: separate connections, no filesystem state.
        var database = $"idempotency-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var stores = new[] { new UrlStore(database), new UrlStore(database) };
        foreach (var store in stores) Assert.Empty(store.ListAll());
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(stores.Select((store, index) => Task.Factory.StartNew(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return store.CreateWithIdempotency($"code{index}", "https://example.com", DateTime.UtcNow.ToString("o"),
                null, "test-owner", "shared-key", "same-request", $"response{index}");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
        Assert.Single(results, r => r.Outcome == CreateUrlOutcome.Created);
        var replay = Assert.Single(results, r => r.Replay is not null).Replay;
        var row = Assert.Single(stores[0].ListAll());
        Assert.Equal("response" + row.Code[^1], replay);
        Assert.Equal(replay, stores[1].GetIdempotency("test-owner", "shared-key")?.Body);
        foreach (var store in stores) store.Dispose();
    }

    [Fact]
    public void AliasCollisionDoesNotConsumeIdempotencyKey()
    {
        var store = _factory.Services.GetRequiredService<UrlStore>();
        var now = DateTime.UtcNow.ToString("o");
        Assert.True(store.Create("existing", "https://example.com", now));
        Assert.Equal(CreateUrlOutcome.CodeConflict, store.CreateWithIdempotency(
            "existing", "https://example.com/new", now, null, "test-owner", "new-key", "hash", "new-response").Outcome);
        Assert.Null(store.GetIdempotency("test-owner", "new-key"));
        Assert.Equal(CreateUrlOutcome.Created, store.CreateWithIdempotency(
            "available", "https://example.com/new", now, null, "test-owner", "new-key", "hash", "new-response").Outcome);
        Assert.Equal("new-response", store.GetIdempotency("test-owner", "new-key")?.Body);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("HEALTH")]
    [InlineData("ready")]
    [InlineData("ReAdY")]
    [InlineData("swagger")]
    [InlineData("SwAgGeR")]
    public async Task ReservedAliasesAreRejected(string alias)
    {
        var response = await Client().PostAsJsonAsync("/api/urls",
            new { url = "https://example.com", custom_alias = alias });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForwardedIpIsUsedOnlyForExplicitlyTrustedPeers(bool configured, bool trustedPeer)
    {
        var peer = trustedPeer ? "192.0.2.1" : "192.0.2.2";
        using var factory = new ShortenerTestFactory(new ServiceOptions
        {
            DbPath = ":memory:", BaseUrl = "http://test", RateBurst = 1, RatePerMinute = 0.001,
            TrustedProxies = configured ? new[] { "192.0.2.1" } : Array.Empty<string>(),
        }, IPAddress.Parse(peer));
        var store = factory.Services.GetRequiredService<UrlStore>();
        store.Create("ip-test", "https://example.com", DateTime.UtcNow.ToString("o"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var first = new HttpRequestMessage(HttpMethod.Get, "/ip-test");
        first.Headers.Add("X-Forwarded-For", "198.51.100.10");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await client.SendAsync(first)).StatusCode);
        Assert.Equal(configured && trustedPeer ? "198.51.100.10" : peer, Assert.Single(store.ClicksFor("ip-test")).Ip);
        using var second = new HttpRequestMessage(HttpMethod.Get, "/ip-test");
        second.Headers.Add("X-Forwarded-For", "198.51.100.11");
        Assert.Equal(configured && trustedPeer ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.TooManyRequests,
            (await client.SendAsync(second)).StatusCode);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ShortenerTestFactory _factory;

    public ServiceTests()
    {
        _factory = new ShortenerTestFactory();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client(bool followRedirects = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = followRedirects,
        });

    private static JsonElement Body(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    // 1 ------------------------------------------------------------------
    [Fact]
    public async Task Health_ReturnsOk()
    {
        var resp = await Client().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", Body(await resp.Content.ReadAsStringAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_EmptyDatabase_ReturnsReady200()
    {
        using var response = await Client().GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Body(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("db").GetString());
    }

    [Fact]
    public async Task Readiness_UnavailableDatabase_ReturnsDegraded503()
    {
        // The parent directory intentionally does not exist; no database is created.
        using var factory = new ShortenerTestFactory(new ServiceOptions
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "ready.db"),
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = Body(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("error", body.GetProperty("db").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3651)]
    [InlineData(int.MaxValue)]
    public async Task Create_InvalidExpiryDays_Returns422WithoutSaving(int days)
    {
        using var response = await Client().PostAsJsonAsync("/api/urls",
            new { url = "https://example.com", expires_in_days = days });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("between 1 and 3650",
            Body(await response.Content.ReadAsStringAsync()).GetProperty("detail").GetString());
        Assert.Empty(_factory.Services.GetRequiredService<UrlStore>().ListAll());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3650)]
    public async Task Create_ExpiryDayBounds_AreAccepted(int days)
    {
        using var response = await Client().PostAsJsonAsync("/api/urls",
            new { url = "https://example.com", expires_in_days = days });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = Body(await response.Content.ReadAsStringAsync());
        var created = DateTimeOffset.Parse(body.GetProperty("created_at").GetString()!);
        var expires = DateTimeOffset.Parse(body.GetProperty("expires_at").GetString()!);
        Assert.Equal(TimeSpan.FromDays(days), expires - created);
    }

    [Theory]
    [InlineData(1, -14)]
    [InlineData(1, 0)]
    [InlineData(1, 14)]
    [InlineData(-1, -14)]
    [InlineData(-1, 0)]
    [InlineData(-1, 14)]
    public async Task Redirect_ExpiryUsesAbsoluteTime(int hoursFromNow, int offsetHours)
    {
        // Offsets change the timestamp's wall-clock representation, not its expiry instant.
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(hoursFromNow).ToOffset(TimeSpan.FromHours(offsetHours));
        var store = _factory.Services.GetRequiredService<UrlStore>();
        Assert.True(store.Create("offset-expiry", "https://example.com",
            now.ToString("o"), expiry.ToString("o")));

        using var response = await Client(followRedirects: false).GetAsync("/offset-expiry");
        Assert.Equal(hoursFromNow > 0 ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.Gone,
            response.StatusCode);
        Assert.Equal(hoursFromNow > 0 ? 1 : 0, store.ClickCount("offset-expiry"));
    }

    // 2 ------------------------------------------------------------------
    [Fact]
    public async Task Create_Then_Redirect_307()
    {
        var client = Client(followRedirects: false);
        var resp = await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com/page" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = Body(await resp.Content.ReadAsStringAsync());
        var code = body.GetProperty("code").GetString()!;
        Assert.Equal(7, code.Length);
        Assert.All(code, c => Assert.True(char.IsLetterOrDigit(c)));
        Assert.EndsWith("/" + code, body.GetProperty("short_url").GetString());

        var redir = await client.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redir.StatusCode);
        Assert.Equal("https://example.com/page", redir.Headers.Location?.ToString());
    }

    // 3 ------------------------------------------------------------------
    [Fact]
    public async Task Create_InvalidUrl_Rejected422()
    {
        foreach (var bad in new[] { "not-a-url", "ftp://example.com/x", "" })
        {
            var resp = await Client().PostAsJsonAsync("/api/urls", new { url = bad });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
    }

    // 4 ------------------------------------------------------------------
    [Fact]
    public async Task CustomAlias_Then_Conflict409()
    {
        var client = Client();
        var first = await client.PostAsJsonAsync("/api/urls",
            new { url = "https://example.com/a", custom_alias = "myalias" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("myalias",
            Body(await first.Content.ReadAsStringAsync()).GetProperty("code").GetString());

        var dup = await client.PostAsJsonAsync("/api/urls",
            new { url = "https://example.com/b", custom_alias = "myalias" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    // 5 ------------------------------------------------------------------
    [Fact]
    public async Task IdempotentReplay_ReturnsIdenticalBody()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-123");
        var payload = new { url = "https://example.com/idem" };
        var first = await client.PostAsJsonAsync("/api/urls", payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();

        var replay = await client.PostAsJsonAsync("/api/urls", payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
    }

    // 6 ------------------------------------------------------------------
    [Fact]
    public async Task ExpiredRedirect_Returns410()
    {
        var store = _factory.Services.GetRequiredService<UrlStore>();
        store.Create("old1", "https://example.com/old",
                     DateTime.UtcNow.AddDays(-2).ToString("o"),
                     DateTime.UtcNow.AddDays(-1).ToString("o"));

        var resp = await Client(followRedirects: false).GetAsync("/old1");
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    // 7 ------------------------------------------------------------------
    [Fact]
    public async Task UnknownCode_Returns404()
    {
        var resp = await Client(followRedirects: false).GetAsync("/nope-not-here");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // 8 ------------------------------------------------------------------
    [Fact]
    public async Task Stats_RecordsClicks()
    {
        var client = Client(followRedirects: false);
        client.DefaultRequestHeaders.Referrer = new Uri("https://ref.example/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test-agent/1.0");

        var created = await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com/stats" });
        var code = Body(await created.Content.ReadAsStringAsync()).GetProperty("code").GetString()!;

        await client.GetAsync("/" + code);
        await client.GetAsync("/" + code);

        var statsResp = await client.GetAsync($"/api/urls/{code}/stats");
        Assert.Equal(HttpStatusCode.OK, statsResp.StatusCode);
        var stats = Body(await statsResp.Content.ReadAsStringAsync());
        Assert.Equal(2, stats.GetProperty("total_clicks").GetInt32());
        Assert.Equal(2, stats.GetProperty("referrers").GetProperty("https://ref.example/").GetInt32());
        Assert.Equal(2, stats.GetProperty("user_agents").GetProperty("test-agent/1.0").GetInt32());
        Assert.NotNull(stats.GetProperty("last_clicked_at").GetString());
        Assert.Single(stats.GetProperty("clicks_by_day").EnumerateArray());
    }

    // 9 ------------------------------------------------------------------
    [Fact]
    public async Task Delete_RemovesUrl()
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com/del" });
        var code = Body(await created.Content.ReadAsStringAsync()).GetProperty("code").GetString()!;

        var del = await client.DeleteAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.GetAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // 10 ------------------------------------------------------------------
    [Fact]
    public async Task RateLimit_Returns429WithRetryAfter()
    {
        using var factory = new ShortenerTestFactory(new ServiceOptions
        {
            DbPath = ":memory:", BaseUrl = "http://test",
            RatePerMinute = 6, RateBurst = 2,
        });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/urls")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/urls")).StatusCode);
        var limited = await client.GetAsync("/api/urls");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // health bypasses the limiter
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    // 11 ------------------------------------------------------------------
    [Fact]
    public async Task AliasValidation_RejectsBadAliases()
    {
        var client = Client();
        foreach (var bad in new[] { "ab", "bad alias!", new string('x', 33) })
        {
            var resp = await client.PostAsJsonAsync("/api/urls",
                new { url = "https://example.com/x", custom_alias = bad });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
    }

    // 12 ------------------------------------------------------------------
    [Fact]
    public async Task ListUrls_ReturnsAllWithClickCounts()
    {
        var client = Client();
        await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com/1" });
        await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com/2" });

        var resp = await client.GetAsync("/api/urls");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = Body(await resp.Content.ReadAsStringAsync()).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(0, item.GetProperty("clicks").GetInt32()));
    }
}
