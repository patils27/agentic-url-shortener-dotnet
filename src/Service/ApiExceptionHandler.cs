using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AgenticUrlShortener.Service;

/// <summary>Maps request failures to safe responses; exception details stay in server logs.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        var readiness = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint?
            .Metadata.GetMetadata<ReadinessEndpoint>() is not null;
        var status = readiness ? StatusCodes.Status503ServiceUnavailable : exception switch
        {
            BadHttpRequestException { StatusCode: 400 or 413 or 415 } badRequest => badRequest.StatusCode,
            // Busy, locked, or unable to open the database: the service cannot currently serve requests.
            SqliteException { SqliteErrorCode: 5 or 6 or 14 } => StatusCodes.Status503ServiceUnavailable,
            TimeoutException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, new EventId(1001, "RequestFailed"),
            exception, "Request failed with status {StatusCode}. Correlation ID: {CorrelationId}",
            status, context.TraceIdentifier);

        context.Response.StatusCode = status;
        if (readiness)
        {
            // Preserve the readiness probe contract used by deployment checks.
            await context.Response.WriteAsJsonAsync(new
            {
                status = "degraded", db = "error", correlation_id = context.TraceIdentifier,
            }, cancellationToken);
            return true;
        }

        var (title, detail) = status switch
        {
            400 => ("Bad Request", "The request body or parameters are invalid."),
            413 => ("Content Too Large", "The request body exceeds the allowed size."),
            415 => ("Unsupported Media Type", "The request content type is not supported."),
            503 => ("Service Unavailable", "The service is temporarily unavailable. Try again later."),
            _ => ("Internal Server Error", "An unexpected error occurred."),
        };
        await Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["correlation_id"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}

internal sealed class ReadinessEndpoint;
