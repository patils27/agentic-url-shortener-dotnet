using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class ExceptionHandlingTests
{
    private const string PrivateDetail = "secret-token internal/database.db stack detail";

    [Theory]
    [InlineData("unexpected", 500)]
    [InlineData("argument", 500)]
    [InlineData("access", 500)]
    [InlineData("cancellation", 500)]
    [InlineData("timeout", 503)]
    [InlineData("busy", 503)]
    [InlineData("locked", 503)]
    [InlineData("open", 503)]
    [InlineData("corrupt", 500)]
    [InlineData("large", 413)]
    public async Task FailuresAreMappedSanitizedAndLoggedWithMatchingCorrelationId(string failure, int status)
    {
        Exception exception = failure switch
        {
            "argument" => new ArgumentException(PrivateDetail),
            "access" => new UnauthorizedAccessException(PrivateDetail),
            "cancellation" => new OperationCanceledException(PrivateDetail),
            "timeout" => new TimeoutException(PrivateDetail),
            "busy" => new SqliteException(PrivateDetail, 5),
            "locked" => new SqliteException(PrivateDetail, 6),
            "open" => new SqliteException(PrivateDetail, 14),
            "corrupt" => new SqliteException(PrivateDetail, 11),
            "large" => new BadHttpRequestException(PrivateDetail, 413),
            _ => new InvalidOperationException(PrivateDetail),
        };
        var logger = new CapturingLogger();
        using var factory = new ShortenerTestFactory();
        using var failing = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IUrlRepository>(new FailingRepository(exception));
                services.AddSingleton<ILogger<ApiExceptionHandler>>(logger);
            });
        });
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.GetAsync("/api/urls");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateDetail, content);
        Assert.DoesNotContain(exception.GetType().Name, content);
        Assert.DoesNotContain("stack", content, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(content);
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        var correlationId = Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
        Assert.Equal(correlationId, body.RootElement.GetProperty("correlation_id").GetString());
        var log = Assert.Single(logger.Entries);
        Assert.Same(exception, log.Exception);
        Assert.Equal(correlationId, log.Fields["CorrelationId"]);
        Assert.Equal(status, log.Fields["StatusCode"]);
        Assert.Equal(status >= 500 ? LogLevel.Error : LogLevel.Warning, log.Level);
    }

    [Theory]
    [InlineData("Development", "application/json")]
    [InlineData("Production", "application/json")]
    [InlineData("Development", "text/html")]
    [InlineData("Production", "text/html")]
    public async Task MalformedJsonHasSafe400InEveryEnvironment(string environment, string accept)
    {
        using var factory = new ShortenerTestFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var content = new StringContent("{\"url\":\"secret-token\",\"expires_in_days\":\"private\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/urls", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-token", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("Exception", json);
        using var body = JsonDocument.Parse(json);
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName)),
            body.RootElement.GetProperty("correlation_id").GetString());
    }

    [Fact]
    public async Task UnsupportedContentTypeReturnsSafe415()
    {
        using var factory = new ShortenerTestFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(PrivateDetail);
        using var response = await client.PostAsync("/api/urls", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(415, body.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("/ready")]
    [InlineData("/READY")]
    [InlineData("/ready/")]
    public async Task ReadinessFailuresKeepProbeContractAndAreLogged(string path)
    {
        var logger = new CapturingLogger();
        using var factory = new ShortenerTestFactory();
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IUrlRepository>(new FailingRepository(new InvalidOperationException(PrivateDetail)));
            services.AddSingleton<ILogger<ApiExceptionHandler>>(logger);
        }));
        using var client = failing.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateDetail, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("degraded", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("error", body.RootElement.GetProperty("db").GetString());
        Assert.Equal(Assert.Single(logger.Entries).Fields["CorrelationId"],
            body.RootElement.GetProperty("correlation_id").GetString());
        using var healthy = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task EveryResponseGetsAnIndependentServerGeneratedCorrelationId()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestCorrelationMiddleware.HeaderName, "untrusted-client-value");
        var ids = new HashSet<string>();
        foreach (var path in new[] { "/health", "/ready", "/api/urls", "/missing", "/unknown/path" })
        {
            using var response = await client.GetAsync(path);
            var id = Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
            Assert.True(Guid.TryParseExact(id, "N", out _));
            Assert.True(ids.Add(id));
        }
    }

    private sealed class CapturingLogger : ILogger<ApiExceptionHandler>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((level, exception, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }

    private sealed class FailingRepository(Exception exception) : IUrlRepository
    {
        public void CheckReady() => throw exception;
        public List<UrlRecordDto> ListOwned(string ownerId) => throw exception;
        public UrlRow? Get(string code) => throw exception;
        public UrlRow? GetOwned(string code, string ownerId) => throw exception;
        public bool DeleteOwned(string code, string ownerId) => throw exception;
        public List<ClickRow> ClicksFor(string code, string ownerId) => throw exception;
        public int ClickCount(string code, string ownerId) => throw exception;
        public void RecordClick(string code, string ts, string? referrer, string? userAgent, string? ip) => throw exception;
        public IdempotencyRecord? GetIdempotency(string ownerId, string key) => throw exception;
        public CreateUrlResult CreateWithIdempotency(string code, string url, string createdAt, string? expiresAt,
            string ownerId, string? key, string requestHash, string body) => throw exception;
    }
}
