# Testing

How this system is tested, what the tests cover, where the gaps honestly
are, and how to run everything. See [ARCHITECTURE.md](ARCHITECTURE.md) for
what the components do.

## 1. Testing approach

The test strategy mirrors the project's dual nature (a service *and* an
orchestration framework):

1. **Generated service suites (from `Codegen.cs`).**
   `tests/Service.Tests/ServiceTests.cs` is produced by the same single
   source of truth as the service itself — the tester agent materializes
   it into each scenario workspace and runs it as a **real `dotnet test`
   subprocess**. The canonical copy at `tests/` verifies the shipped
   `src/Service/`.
2. **Orchestrator unit tests with stub agents.**
   `tests/Orchestrator.Tests/OrchestratorTests.cs` drives the real
   `Engine`/`Dag`/gates/policies/retry/replan code with tiny stub agents,
   so scheduling, governance, and recovery logic are tested without
   running scenarios.
3. **Scenario verification.** Each scenario run is itself an integration
   test: greenfield/brownfield/ambiguous must reach `succeeded` and leave
   a complete `audit.jsonl` + `metrics.json` trail. The build log records
   the verified outcomes (8/8 tasks green in greenfield; 6/6 in brownfield;
   `replans=1` with 5 assumptions resolved in ambiguous).
4. **Live curl demo.** The demo sequence in the README was verified
   against a running service: create (201), redirect (307 + `location`),
   stats (click recorded), idempotent replay (201 → 200 same body),
   invalid URL (422), unknown code (404), expired link (410), alias
   conflict (409), delete (204), rate limit (429 + `Retry-After`, health
   bypasses), `/health` + `/ready` (with DB check).

## 2. What is covered

**`ServiceTests` (37 cases):** health/readiness, create + redirect,
click recording, analytics aggregation (totals, per-day,
referrer/user-agent, `last_clicked_at`), idempotency (`Idempotency-Key`
replay returns the same body), rate limiting (429 + `Retry-After`,
health bypass), URL/alias validators, custom alias creation and
409-on-conflict, expired-link → 410, list/delete lifecycle.

Additional regression cases verify concurrent idempotency over HTTP and across
SQLite connections, collision rollback, reserved aliases, and trusted-proxy
handling for rate limiting and click IPs.
Expiry cases cover 1..3650-day bounds and future/past instants with explicit
offsets. Readiness cases cover empty storage, unavailable storage returning 503,
and probe bypass of rate limits. Run these cases in a non-UTC environment as well
as UTC: the original local-time comparison bug is only observable outside UTC.

**`AccessTests` (24 cases):** missing/invalid/multiple credentials, fail-closed
configuration, public redirects and probes, cross-owner isolation, owner-scoped
idempotency, changed requests and concurrent conflicts, normalized replays,
invalid key headers, reserved API aliases, legacy migration, key rotation, and
24-hour replay expiry using a controlled clock. Generated suites also check
authentication, ownership, and request matching; smart suites check health access.

**`OrchestratorTests` (15 tests):** DAG execution order, parallel wave
overlap (real timing overlap, not just completion), gate blocking, retry
recovery + metrics accounting, retry-exhaustion → rollback, fallback
chain, policy-denial safe-stop, approval grant/deny paths, replan
invalidation + subgraph re-run, content-hash drift detection, metrics
JSON shape. Regression cases verify unsuccessful result retries/rollback and
safe-stop on fallback policy violations.

**`AgentTests` (2 tests):** simultaneous draining of large stdout/stderr streams
and timeout handling for hung child processes.

**`AgentCancellationTests` (11 cases):** pre-cancelled agents, cancellation
during a policy-checked write, and termination of a subprocess tree.

**`EngineCancellationTests` (8 cases):** retain concurrency slots until agents
exit, reject late success, interrupt retry backoff, compensate once after
shutdown, preserve fatal policy exceptions, and log only actual fallback calls.

**`SharedStateTests` (8 cases):** parallel context and metric writers, detached
nested snapshots, atomic queue operations, synchronized replan snapshots and
policy denials, and serialized/cancel-aware approvals.

**Generated variant suites** (run inside scenario workspaces by the
tester agent): v1 = 22 cases, v2 = 25 cases, smart health-only = 29,
smart full = 32.

## 3. How to run

```bash
export PATH=$HOME/workspace/.dotnet:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# From the repo root
dotnet test AgenticUrlShortener.sln --nologo -v q
# -> 105 passed (61 + 44), 0 failed
```

**Environment quirk (important):** VSTest opens a dual-mode local socket
that this sandbox routes through the egress proxy, so test discovery
hangs unless IPv6 is disabled. Both test projects set

```xml
<RuntimeHostConfigurationOption Include="System.Net.DisableIPv6" Value="true" />
```

and runs also export `DOTNET_SYSTEM_NET_DISABLEIPV6=1`. Without these,
`dotnet test` stalls instead of failing.

**Note:** each scenario run leaves a workspace copy with its own
generated suite under `runs/*/workspace/src/`. Those directories are
*not* part of `AgenticUrlShortener.sln`, so a repo-root `dotnet test`
never collects them.

Regenerating the canonical tests from the source of truth is what the
scenario agents do: `Codegen.Materialize("v2", destDir)` writes the
service + tests to `<destDir>/src/`.

## 4. Limitations and trade-offs (honest)

These are real and worth knowing before trusting the system:

- **The planner is rule-based and deterministic — there are no LLM calls
  anywhere.** Ambiguity scoring, normalization, and decomposition are
  hand-written heuristics. This makes every run explainable and
  reproducible, but the system does not "understand" requirements the way
  an LLM agent would; novel or genuinely ambiguous inputs outside the
  heuristics' coverage get shallow treatment.
- **SQLite is single-node.** No replication, no failover, no concurrent
  writers beyond a lock. Fine for the demo and tests; not a distributed
  store.
- **The rate limiter is in-memory and per-process**, and the per-IP
  bucket map **grows unboundedly** — a production deployment would need
  Redis (or equivalent) and bucket eviction.
- **Rollback is per-task compensation, not a distributed saga.** A task's
  rollback hooks undo that task's own effects; there is no cross-task
  transaction or saga coordinator.
- **Secret scanning is pattern-based.** `no_secrets_in_code` matches
  secret-like patterns in written code; it is not a real secret-detection
  engine and can both miss and false-positive.
- **Expiry is tested via direct DB inserts.** The tests insert
  already-expired rows rather than waiting out a TTL, so the wall-clock
  expiry path is simulated, not exercised end-to-end.
- **`--auto` auto-approves.** In `--auto` mode, human checkpoints are
  granted automatically — oversight is *simulated and logged*, not real.
  Interactive runs prompt for actual approval.
- **No load or chaos testing.** There are no benchmarks, no concurrency
  torture tests of the service, and no fault-injection against the
  orchestrator (killed workers, disk-full, etc.).
- **The brownfield 404→410 change is behavior-breaking by design** and is
  exercised only because the release approval gate explicitly signs off on
  it; without that gate the change would be unsafe to ship.
- **Generated scenario projects deliberately stay out of the solution.**
  This keeps repo test discovery clean, but it also means `dotnet build`
  at the root never compiles scenario workspaces — the tester agent's
  subprocess is the only compiler for them.

## 5. What the scenario runs validate that unit tests don't

- The full wave pipeline end-to-end: gates → approval → policy →
  bounded retry → exit gates → snapshot, under both success and
  re-planning.
- Governance preserved across a DAG mutation (ambiguous: re-run tasks
  re-pass the same gates/policies).
- Artifact emission: every run leaves `audit.jsonl`, `metrics.json`,
  `decision_lineage.md`, `manifest.json`, and a `workspace/` with
  generated code and docs — checkable after the fact.
