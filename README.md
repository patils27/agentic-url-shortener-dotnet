# Agentic Software Engineering System — URL Shortener (.NET 10)

A production-style **URL shortener service** built by a **real agentic SDLC
orchestration framework**, ported feature-for-feature from the Python
implementation in `~/workspace/agentic-url-shortener/` to **C# on .NET 10**.
The repo is two things in one:

1. **`src/Service/`** — an ASP.NET Core URL shortener (create, redirect,
   analytics, idempotency, rate limiting, custom aliases, expiry).
2. **`src/Orchestrator/` + `src/Agents/` + `src/Scenarios/`** — an agent
   orchestration engine that planned, designed, implemented, tested,
   documented, and released the service across **three verified scenario
   runs** (greenfield, brownfield, ambiguous), with DAG scheduling, gates,
   human approvals, policy guardrails, retries, audit logging, metrics,
   and dynamic re-planning.

The scenario-generated service code comes from a **single source of truth**
(`src/Agents/Codegen.cs`), so the product and the agent-generated code can
never drift apart. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Prerequisites

- .NET SDK **8.0.425**, installed at `~/workspace/.dotnet`
  (verify with `~/workspace/.dotnet/dotnet --version`).
- A Unix-like shell. No external services — SQLite via
  `Microsoft.Data.Sqlite`, no Docker, no network needed at runtime.

```bash
export PATH=$HOME/workspace/.dotnet:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
```

Pinned versions: `Microsoft.Data.Sqlite` **10.0.7** (+ `SQLitePCLRaw.lib.e_sqlite3`
**2.1.13** pin for the NU1903 advisory), xUnit **2.9.2** /
test SDK **17.11.1**, `Microsoft.AspNetCore.Mvc.Testing` **10.0.0**.
All projects target `net10.0`.

## Running the tests

```bash
# from the repo root
dotnet test AgenticUrlShortener.sln --nologo -v q
```

40/40 passing: 23 service cases + 17 orchestrator/agent cases. Generated
scenario workspaces under `runs/*/workspace/` are ordinary directories
(not in the solution), so their own suites are never collected by the
repo test run. Details in [docs/TESTING.md](docs/TESTING.md).

## Running the service

```bash
SHORTENER_DB=/tmp/demo.db \
ASPNETCORE_URLS=http://127.0.0.1:8000 \
dotnet src/Service/bin/Debug/net10.0/AgenticUrlShortener.Service.dll
# (build first: dotnet build AgenticUrlShortener.sln --nologo -v q)
```

### Configuration

| Variable | Default | Purpose |
|---|---|---|
| `SHORTENER_DB` | `shortener.db` | SQLite file path (`:memory:` in tests) |
| `SHORTENER_BASE_URL` | `http://localhost:8000` | Base URL used in generated `short_url` fields |
| `SHORTENER_RATE_PER_MINUTE` | `60` | Token-bucket refill rate per IP |
| `SHORTENER_RATE_BURST` | `10` | Token-bucket burst per IP |
| `SHORTENER_TRUSTED_PROXIES` | (empty) | Comma-separated proxy IP addresses allowed to supply `X-Forwarded-For` |
| `ASPNETCORE_URLS` | (SDK default) | Bind address, e.g. `http://127.0.0.1:8000` |

### API endpoints

| Method | Path | Notes |
|---|---|---|
| `POST` | `/api/urls` | Create short URL → `201`; `Idempotency-Key` header replays → `200` same body |
| `GET` | `/api/urls` | List all short URLs |
| `GET` | `/api/urls/{code}` | Fetch one |
| `DELETE` | `/api/urls/{code}` | Delete → `204` |
| `GET` | `/api/urls/{code}/stats` | Click analytics: totals, per-day, referrer/UA breakdown, `last_clicked_at` |
| `GET` | `/{code}` | `307` redirect, records timestamp/referrer/user-agent/IP |
| `GET` | `/health` | Liveness (bypasses rate limiter) |
| `GET` | `/ready` | Readiness — performs a DB check, returns `db: ok\|error` |

POST accepts `{"url": "...", "custom_alias": "...", "expires_in_days": N}`.
Generated codes are 7 alphanumeric characters; aliases must be 3–32 chars
of `[A-Za-z0-9_-]`. Conflicting aliases → `409`; invalid destination →
`422`; expired links → `410` (v1 incorrectly returned `404` — the planted
bug fixed in the brownfield run). The catch-all `/{code}` route is
registered **last** so it never shadows `/health`, `/ready`, or `/api/*`.
Per-IP token-bucket rate limiting returns `429` + `Retry-After`. All JSON
is snake_case.

Forwarded IP headers are ignored by default. When using a reverse proxy, set
`SHORTENER_TRUSTED_PROXIES` to its immediate peer IP address. The service consumes
one forwarded hop, from the right of the header; the proxy must append or replace
the client IP. The validated IP is used for both rate limiting and click analytics.
Aliases `health` and `ready` are reserved, regardless of case. Concurrent creates
with the same idempotency key return one saved response and create one URL.

### Demo sequence (verified live)

```bash
B=http://127.0.0.1:8000
# create — custom alias -> 201 Created
curl -X POST $B/api/urls -H 'Content-Type: application/json' \
  -d '{"url":"https://example.com/docs","custom_alias":"demo-link"}'

# redirect -> 307 Temporary Redirect + location: https://example.com/docs
curl -D - $B/demo-link

# analytics -> {"total_clicks": 1, "clicks_by_day": [...], ...}
curl $B/api/urls/demo-link/stats

# idempotent replay -> 201 then 200 with the identical body
curl -X POST $B/api/urls -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: abc123' -d '{"url":"https://example.com/docs"}'
curl -X POST $B/api/urls -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: abc123' -d '{"url":"https://example.com/docs"}'

# expired link -> 410 ; unknown code -> 404 ; alias conflict -> 409
curl -o /dev/null -s -w '%{http_code}\n' $B/<expired-code>

curl $B/health $B/ready
```

## Running the scenarios

Each scenario runs the full agentic SDLC and emits a run bundle
(`audit.jsonl`, `metrics.json`, `decision_lineage.md`, `manifest.json`).
From the repo root:

```bash
dotnet run --project src/Scenarios -- greenfield --auto   # build v1 from scratch
dotnet run --project src/Scenarios -- brownfield --auto    # evolve v1 -> v2 (aliases, 410 fix)
dotnet run --project src/Scenarios -- ambiguous --auto     # vague request -> clarify -> re-plan
```

`--auto` auto-approves human checkpoints (approval decisions are still
recorded in the audit log). Run bundles land in
`runs/<scenario>/<run-id>/`. Details in
[docs/FINAL_SUMMARY.md](docs/FINAL_SUMMARY.md).

## Repository layout

```
agentic-url-shortener-dotnet/
├── AgenticUrlShortener.sln
├── src/
│   ├── Service/        # ASP.NET Core URL shortener (shipped product)
│   ├── Orchestrator/   # DAG engine: waves, gates, approvals, policies,
│   │                   # retries/rollback, audit, metrics, re-planning
│   ├── Agents/         # Planner, Architect, Implementer, Tester,
│   │                   # Documenter, Release + Codegen (single source of truth)
│   └── Scenarios/      # greenfield / brownfield / ambiguous CLI runners
├── tests/
│   ├── Service.Tests/      # 23 service cases (HTTP and storage)
│   └── Orchestrator.Tests/ # 17 orchestrator/agent cases (xUnit)
├── docs/               # ARCHITECTURE.md, TESTING.md, FINAL_SUMMARY.md
└── runs/               # scenario run bundles + BUILD_LOG.md
```

## Limitations

- The Tester agent resolves `dotnet` from `DOTNET_BIN`, then
  `~/workspace/.dotnet/dotnet`, then `dotnet` on `PATH`; scenario runs
  require one of those to exist.
- Scenario workspaces materialize generated projects under
  `runs/<scenario>/<run-id>/workspace/src/`; they are intentionally not
  part of the solution, so `dotnet test` at the repo root never collects
  them.
- The in-memory per-IP rate limiter does not survive restarts and is not
  shared across instances — same as the Python original.
- Human-approval checkpoints are simulated by `--auto` in unattended
  runs; every auto-approval is recorded in `audit.jsonl` like a real one.
