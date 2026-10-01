# HTTP error handling

`ShortenerApp` registers `ApiExceptionHandler` through ASP.NET Core's
`IExceptionHandler` and uses the same exception pipeline in Development and
Production. Endpoint handlers continue to return expected business outcomes
(such as validation 422, conflict 409, and missing links 404) explicitly.

Every response that reaches the application pipeline receives a server-generated
`X-Correlation-ID` header. Caller-supplied values are ignored. The ID is included
in the request logging scope, alongside the distributed trace ID when available,
and in each centralized exception log's `CorrelationId` field. Give this response
header to an operator to locate the corresponding failure.

| Failure | HTTP status |
| --- | --- |
| Invalid JSON or request binding | 400 |
| Request body too large | 413 |
| Unsupported request content type | 415 |
| SQLite busy, locked, or unable to open; operation timeout | 503 |
| Readiness check failure | 503 |
| Unexpected exception | 500 |

Programming errors such as `ArgumentException` or `UnauthorizedAccessException`
are not globally converted to client errors: they may indicate a server bug or
filesystem problem. Request cancellation caused by a disconnected client is
handled by ASP.NET Core's exception middleware before the custom handler.

Exceptions return `application/problem+json`, with generic messages and a
`correlation_id` extension. For example:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "correlation_id": "08fc780407a14dc1b028a5eb0f0ec4de"
}
```

The response never copies an exception message, stack trace, request body, or
database path. JSON is returned even if a caller requests HTML. Empty error
responses from routing and binding also receive a safe Problem Details body.
Readiness retains its existing `{ "status": "degraded", "db": "error" }`
contract, with the correlation ID added on failure. Existing business-error
bodies retain their contracts and include the correlation ID in the header.
Generated smart-link probes return a fixed failure message for expected network
failures; unexpected probe exceptions reach the centralized handler.

Handled exceptions are logged once by `ApiExceptionHandler` (event 1001), at
Warning for client errors and Error for server failures. Exception details are
available only in server logs; restrict access to those logs. The handler does
not add request headers, credentials, or request bodies to log fields. ASP.NET
Core's duplicate diagnostics for handled exceptions are suppressed explicitly.
See [Microsoft's exception handling documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).

The middleware cannot replace a response after headers have been sent, or handle
startup failures or requests rejected before entering the application pipeline.
The same handler, correlation middleware, and regression tests are included in
the generated v1/v2 services.
