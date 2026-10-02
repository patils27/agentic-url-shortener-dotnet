# API access and existing databases

All `/api/*` endpoints require `X-Api-Key`. Each key maps to a stable owner ID;
the server assigns ownership when creating a link. Callers cannot supply or
change ownership in the request body. Lists, details, deletion, analytics, and
generated smart-link health checks are restricted to that owner. Other owners'
codes return `404`. Short-link redirects, `/health`, and `/ready` remain public.

## Run locally from PowerShell

Run from the repository root. Generate a fresh key in memory and configure the
service process; do not commit keys to launch settings or source control.

```powershell
$randomBytes = New-Object byte[] 32
$generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$generator.GetBytes($randomBytes)
$generator.Dispose()
$shortenerKey = [Convert]::ToBase64String($randomBytes)
$env:SHORTENER_API_KEYS = @{ local = $shortenerKey } | ConvertTo-Json -Compress
$env:SHORTENER_BASE_URL = 'http://localhost:8000'
$env:ASPNETCORE_URLS = 'http://localhost:8000'
dotnet run --project src/Service --configuration Release --no-launch-profile
```

Use the generated key in the HTTP client's `X-Api-Key` header. In PowerShell,
set `$shortenerKey` in the client terminal to the same value, then:

```powershell
$headers = @{ 'X-Api-Key' = $shortenerKey; 'Idempotency-Key' = 'first-link' }
$link = Invoke-RestMethod http://localhost:8000/api/urls -Method Post `
    -Headers $headers -ContentType application/json `
    -Body '{"url":"https://example.com","custom_alias":"my-first-link"}'
Invoke-RestMethod http://localhost:8000/api/urls -Headers $headers
```

For Visual Studio debugging, the service process must receive the same
`SHORTENER_API_KEYS` environment variable. Launch Visual Studio from the
configured terminal, or use a local, untracked debug environment configuration.
An already-running Visual Studio instance will not inherit newly set variables.
Set `src/Service` as the startup project and use F5. The API client must still
send `X-Api-Key` while debugging. Existing launch settings were not changed.

## Use Swagger UI

With the HTTPS Development profile, browse to `https://localhost:7084/swagger`.
For the HTTP profile, use `http://localhost:5038/swagger`. The Development root
page redirects there automatically. Configure `SHORTENER_API_KEYS` before
starting the service, then use **Authorize** in Swagger UI to enter the matching
key without a prefix. Management operations send it in `X-Api-Key`; public
health and redirect operations do not require it. Keys are not persisted across
page reloads or embedded in the OpenAPI document.

Use **Try it out** and **Execute** to call an endpoint. Swagger UI remains
accessible in Development even when no server keys are configured, but it does
not bypass authentication or create credentials. Swagger endpoints are disabled
outside Development.

## Configuration and errors

`SHORTENER_API_KEYS` is a JSON object, for example
`{"alice":"<secret-for-alice>","bob":"<different-secret-for-bob>"}`.
The placeholders must be replaced by securely generated secrets. Keys must be
unique, 32–512 printable ASCII characters without spaces. Owner IDs must be
nonempty, trimmed, and at most 128 characters. Invalid configuration fails
startup without echoing secrets. Configuration is loaded at startup; restart
after changes. Rotate a secret while keeping its owner ID to retain access.

Missing configuration returns `503` on management endpoints. Missing, invalid,
or multiple key headers return `401` when authentication is configured.
Use HTTPS when accessing the service beyond localhost; keys are bearer secrets.
There is no API for user registration, key provisioning, or ownership transfer.

## Existing database migration

Back up the database before an upgrade. The first database access transactionally
adds nullable `urls.owner_id`, its index, and `idempotency_requests`. Existing
URLs and clicks remain intact. Old links have no owner and remain redirectable,
but no API caller can list, inspect, or delete them. A database operator may
explicitly assign verified legacy rows to known owner IDs after reviewing them;
the service never assigns all legacy links to the first caller.

The old unscoped `idempotency` table is retained for operator review. Its
responses are never replayed because their owners cannot be established.

## Idempotency contract

Send one `Idempotency-Key`, containing 1–128 printable ASCII characters without
spaces. Invalid headers return `400`. Keys are scoped to owner ID and the
validated URL, alias, and expiry. URL surrounding whitespace and absent/empty
aliases are normalized before comparison; JSON property order is irrelevant.

The first request returns `201`. A matching replay within 24 hours returns
`200` with the exact saved body. Reusing a key with different request values
returns `422`, including concurrent requests. Different owners can independently
reuse the same key. Alias uniqueness remains global because redirects are public.

After 24 hours, the key can be used again. Expired records are purged during
subsequent keyed creates. Deleting or expiring a link does not erase its saved
response during that window; use a new key to request a new link. The retention
window does not change the link's own expiry.
