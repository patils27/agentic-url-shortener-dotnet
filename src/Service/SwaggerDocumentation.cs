using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AgenticUrlShortener.Service;

public static class SwaggerDocumentation
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "URL Shortener API",
                Version = "v1",
                Description = "Configure SHORTENER_API_KEYS on the server, then use Authorize to enter your key. " +
                    "Management endpoints require X-Api-Key. Health checks and redirects are public.",
            });
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyAuthentication.HeaderName,
                Description = "Enter the API key only, without a Bearer prefix.",
            });
            options.DocumentFilter<ManagementApiDocumentFilter>();
        });
        // Use the minimal API's serializer settings so schemas match its snake_case payloads.
        services.AddSingleton<ISerializerDataContractResolver>(sp => new JsonSerializerDataContractResolver(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions));
        return services;
    }

    public static void UseApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("v1/swagger.json", "URL Shortener API v1");
            options.DocumentTitle = "URL Shortener API";
            // Use bundled assets only and do not send the document to an external validator.
            options.ConfigObject.ValidatorUrl = null;
            options.ConfigObject.PersistAuthorization = false;
        });
        app.MapGet("/", () => Results.Redirect("swagger/index.html")).ExcludeFromDescription();
    }
}

/// <summary>Describe the same /api boundary enforced by API-key middleware.</summary>
public sealed class ManagementApiDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var (path, item) in document.Paths)
        {
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || item.Operations is null)
                continue;
            foreach (var operation in item.Operations.Values)
            {
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
                }];
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid API key." });
                operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Authentication is not configured or storage is unavailable." });
                operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Rate limit exceeded. Retry after the Retry-After header interval." });
            }
        }

        if (document.Paths.TryGetValue("/api/urls", out var urls) &&
            urls.Operations?.TryGetValue(HttpMethod.Post, out var create) == true)
        {
            create.Parameters ??= [];
            create.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Optional replay key scoped to your owner. Reusing it with changed input returns 422.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 },
            });
        }
    }
}
