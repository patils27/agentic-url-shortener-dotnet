using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticUrlShortener.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgenticUrlShortener.Service.Tests;

public sealed class SwaggerTests
{
    [Fact]
    public async Task DevelopmentServesBundledUiAndRedirectsHomeWithoutAuthentication()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("swagger/index.html", home.Headers.Location?.OriginalString);
        foreach (var path in new[] { "/swagger/index.html", "/swagger/swagger-ui-bundle.js", "/swagger/swagger-ui.css" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task ProductionDoesNotExposeDocumentation(string path)
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DocumentDescribesActualPayloadsResponsesAndManagementOnlySecurity()
    {
        using var factory = new ShortenerTestFactory();
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ShortenerTestFactory.TestKey, text);
        using var json = JsonDocument.Parse(text);
        var document = json.RootElement;
        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal(ApiKeyAuthentication.HeaderName, scheme.GetProperty("name").GetString());
        var paths = document.GetProperty("paths");
        Assert.False(paths.TryGetProperty("/", out _));
        foreach (var path in paths.EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
        {
            if (path.Name.StartsWith("/api/", StringComparison.Ordinal))
            {
                Assert.True(operation.Value.GetProperty("security")[0].TryGetProperty("ApiKey", out _));
                Assert.True(operation.Value.GetProperty("responses").TryGetProperty("401", out _));
                Assert.True(operation.Value.GetProperty("responses").TryGetProperty("503", out _));
            }
            else
                Assert.True(!operation.Value.TryGetProperty("security", out var security) || security.GetArrayLength() == 0);
        }
        var create = paths.GetProperty("/api/urls").GetProperty("post");
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("in").GetString() == "header");
        foreach (var status in new[] { "200", "201", "400", "409", "422" })
            Assert.True(create.GetProperty("responses").TryGetProperty(status, out _));
        Assert.True(paths.GetProperty("/api/urls/{code}").GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
        Assert.True(paths.GetProperty("/{code}").GetProperty("get").GetProperty("responses").TryGetProperty("307", out _));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var request = schemas.GetProperty("CreateUrlRequest").GetProperty("properties");
        Assert.True(request.TryGetProperty("url", out _));
        Assert.True(request.TryGetProperty("expires_in_days", out _));
        Assert.False(request.TryGetProperty("expiresInDays", out _));
        Assert.True(schemas.GetProperty("ShortUrlResponse").GetProperty("properties").TryGetProperty("short_url", out _));
    }

    [Fact]
    public async Task DocumentationDoesNotBypassApiAuthentication()
    {
        using var factory = new ShortenerTestFactory();
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        using var unauthorized = await client.GetAsync("/api/urls");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, ShortenerTestFactory.TestKey);
        using var created = await client.PostAsJsonAsync("/api/urls", new { url = "https://example.com" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var listed = await client.GetAsync("/api/urls");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    [Fact]
    public async Task DocumentationIsAvailableWhenApiKeysHaveNotBeenConfigured()
    {
        using var factory = new ShortenerTestFactory(authenticate: false);
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var documentation = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, documentation.StatusCode);
        using var management = await client.GetAsync("/api/urls");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, management.StatusCode);
    }
}
