# Build Log

Chronological record of the C#/.NET 8 implementation of the agentic URL
shortener with ASP.NET Core.

SDK: 8.0.425 at `~/workspace/.dotnet` (do not delete). All projects target
`net8.0`. Pinned packages: `Microsoft.Data.Sqlite` 8.0.11, xUnit 2.9.2 /
test SDK 17.11.1, `Microsoft.AspNetCore.Mvc.Testing` 8.0.11.

## 2026-09-22 — Scaffold + service

- Created solution `AgenticUrlShortener.sln` with 6 projects:
  `src/Service`, `src/Orchestrator`, `src/Agents`, `src/Scenarios`,
  `tests/Service.Tests`, `tests/Orchestrator.Tests`.
- `src/Service/`: ASP.NET Core minimal API (`ShortenerApp.cs`,
  `Models.cs`, `Validators.cs`, `UrlStore.cs`, `ClickAnalytics.cs`,
  `RateLimiter.cs`, `ServiceOptions.cs`, `Program.cs`). SQLite tables
  `urls`, `clicks`, `idempotency` via `Microsoft.Data.Sqlite` (no EF
  Core). Snake_case JSON; catch-all `/{code}` registered last;
  per-IP token bucket with `429` + `Retry-After`; `/health` + `/ready`
  bypass the limiter.
- Fixes during port: redirect uses `Results.Redirect(..., preserveMethod:
  true)` for a true 307; stats serializer preserves referrer/user-agent
  dictionary keys verbatim (no `DictionaryKeyPolicy`).
- 12 service tests (`WebApplicationFactory`): health, create/redirect,
  invalid URL 422, aliases + 409 conflict, idempotent replay (200,
  identical body), expired 410, unknown 404, analytics, deletion, rate
  limiting, alias validation, list/click counts.
- Environment quirk found: VSTest's dual-mode local socket is routed
  through the egress proxy, so test discovery stalls unless IPv6 is
  disabled — both test projects set
  `RuntimeHostConfigurationOption Include="System.Net.DisableIPv6"`,
  and runs export `DOTNET_SYSTEM_NET_DISABLEIPV6=1`.

## 2026-09-22 — Orchestrator + agents

- Ported the framework: `Dag.cs`, `RunContext.cs`, `Gates.cs`,
  `Approvals.cs`, `Policies.cs` (5 guardrails), `Retry.cs` (bounded
  exponential backoff + jitter, fallback, rollback hooks, fatal
  exceptions), `Audit.cs` (append-only JSONL + manifest), `Metrics.cs`,
  `Replan.cs` (content-hash drift, DAG mutation, transitive
  invalidation), `IAgent.cs`, `Engine.cs`.
- 12 orchestrator tests ported: DAG order/join, real parallel overlap,
  entry-gate block propagation, retry recovery + metrics, rollback,
  fallback, policy-denial safe-stop, approval grant/deny, replan
  invalidation/rerun, content-hash drift, metrics shape.
- **Key fix found by test:** `Engine.RunWave` originally used
  `Task.Run` on the thread pool; blocking agent delegates (subprocess
  + locks) starved the pool and serialized parallel waves. Rewrote to
  dedicated `LongRunning` tasks bounded by `SemaphoreSlim` — the
  parallel-overlap test now asserts real timing overlap.
- Agents: Planner (ambiguity scoring, decompose/clarify/inject modes),
  Architect (ADRs, workspace impact analysis, smart design), Implementer
  (policy-checked writes, v1→v2 diffs, smart feature), Tester (real
  `dotnet test` subprocess; resolves dotnet from `DOTNET_BIN` →
  `~/workspace/.dotnet/dotnet` → `PATH`), Documenter (API/CHANGELOG/
  ASSUMPTIONS/SMART_LINKS docs), Release (checklist + independent policy
  evaluation, GO/NO-GO).
- `Codegen.cs`: single source of truth — full service as `FileSpec` data
  (v1 greenfield, v2 brownfield, smart-link variants); `Materialize()`
  writes `<workspace>/src/`. Verified generated suites standalone:
  v1 9/9, v2 12/12, smart health-only 15/15, smart full 18/18.

## 2026-09-22 — Scenarios + first runs

- `src/Scenarios/`: `Harness.cs` + `Greenfield`/`Brownfield`/`Ambiguous`
  runners + CLI (`greenfield|brownfield|ambiguous [--auto]`). Run
  bundles land in `runs/<scenario>/<run-id>/`.
- Fix: `Harness.RepoRoot` traversal corrected (5 parents) so runs land
  under repo-level `runs/`, not `src/runs/`.
- First greenfield failures (kept under `runs/_failed/` for the record):
  planner referenced a nonexistent generated file; then generated
  `FileSpec` paths carried a `src/` prefix while paths are relative to
  `<workspace>/src`. Stripped the prefix everywhere (`Codegen.cs`,
  planner file lists, tester project path, implementer write paths);
  removed the stale `ShortenerApp.cs` reference from the greenfield plan.
- After fixes: **greenfield succeeded** (run `20260922-052254-9173`):
  8/8 tasks, 1 approval, generated 9/9 tests green via real `dotnet
  test`.
- **brownfield succeeded** (run `20260922-052305-9398`): 6/6 tasks,
  v1 baseline → impact analysis → aliases + 410 fix + validator
  refactor → 12/12 regression tests → changelog → approved release.
- **ambiguous succeeded** (run `20260922-052316-9646`): 7/7 tasks,
  `replans=1`, 5 assumptions logged and all confirmed, 3 approvals,
  `replan_triggered` in audit; hypothesis pass (health monitoring) then
  confirmed-scope pass (device-aware smart redirects + health).

## 2026-09-22 — Parity fix + live verification

- `/ready` now performs a real DB check (`store.ListAll()`) and returns
  `{"status": "ready"|"degraded", "db": "ok"|"error"}`. The same fix was
  applied to the C# `Codegen.cs` template, and the
  generated health/ready test strengthened to assert `db == "ok"`.
- Re-verified generated v2 suite standalone: 12/12 pass.
- Live curl verification against the built service (port 18080):
  create 201 (custom alias honored), redirect 307 + `Location`, stats
  (`total_clicks=1`, `last_clicked_at`, per-day, referrer + user-agent
  breakdowns), idempotent replay 200 with identical body, invalid URL
  422, unknown code 404, expired link 410, alias conflict 409, list,
  delete 204 → 404, rate limit 429 + `Retry-After: 30` (health bypassed),
  `/health` → `{"status":"ok"}`, `/ready` → `{"status":"ready","db":"ok"}`.

## 2026-09-22 — Docs + final acceptance

- Wrote README.md, docs/ARCHITECTURE.md, docs/TESTING.md,
  docs/FINAL_SUMMARY.md, .gitignore.
- Final acceptance: `dotnet build AgenticUrlShortener.sln` → 0 warnings,
  0 errors; `dotnet test AgenticUrlShortener.sln` → 24/24 passed;
  all three scenarios `succeeded` with `audit.jsonl`, `metrics.json`,
  `decision_lineage.md`, `manifest.json` in every run dir.
