// Service test suite (12 tests): full HTTP coverage of the URL shortener API
// via WebApplicationFactory. Mirrors the Python test suite test-for-test.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class ShortenerTestFactory : WebApplicationFactory<Program>
{
    private readonly ServiceOptions _options;

    public ShortenerTestFactory(ServiceOptions? options = null)
    {
        _options = options ?? new ServiceOptions
        {
            DbPath = ":memory:",
            BaseUrl = "http://test",
            RatePerMinute = 6000,
            RateBurst = 1000,
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddSingleton(_options));
    }
}

public sealed class ServiceTests : IDisposable
{
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
