# Architecture

This document describes the system's components, the orchestration model
that drives them, and the key design decisions behind both. It is a
feature-for-feature port of the Python implementation — see
[README.md](../README.md) for setup and usage, and
[TESTING.md](TESTING.md) for verification.

## 1. Components

### 1.1 The service (`src/Service/`)

An ASP.NET Core (minimal API) URL shortener with SQLite persistence via
`Microsoft.Data.Sqlite` (no EF Core):

| File | Responsibility |
|---|---|
| `ShortenerApp.cs` | Route definitions and app factory `ShortenerApp.CreateApp(ServiceOptions?)`; catch-all `/{code}` registered **last** so it never shadows `/health`, `/ready`, or `/api/*` |
| `Models.cs` | Request/response records (`CreateUrlRequest`, `ShortUrlResponse`, `UrlStats`, `ClickRow`, `UrlRow`) |
| `UrlStore.cs` | SQLite layer (single connection guarded by a lock; tables created lazily; tables `urls`, `clicks`, `idempotency`) |
| `ClickAnalytics.cs` | Pure-function click aggregation (totals, per-day, referrer/user-agent breakdown, `last_clicked_at`) |
| `RateLimiter.cs` | Per-IP token-bucket limiter (in-memory, thread-safe; default 60 req/min, burst 10); `429` carries `Retry-After`; `/health` and `/ready` bypass it |
| `Validators.cs` | Shared URL/alias validation (mirrors the v2 refactor that extracted validators from the app layer) |
| `ServiceOptions.cs` | Configuration from environment (`SHORTENER_DB`, `SHORTENER_BASE_URL`, `SHORTENER_RATE_PER_MINUTE`, `SHORTENER_RATE_BURST`) |
| `Program.cs` | Entry point; `partial class Program` exposed for `WebApplicationFactory` tests |

Endpoints: `POST /api/urls` (201; `Idempotency-Key` replay → 200 same
body), `GET /{code}` (307 redirect, records click),
`GET /api/urls`, `GET /api/urls/{code}`, `DELETE /api/urls/{code}` (204),
`GET /api/urls/{code}/stats`, `GET /health`, `GET /ready` (performs a DB
check, returns `db: ok|error`). All JSON is snake_case
(`JsonNamingPolicy.SnakeCaseLower`); referrer/user-agent breakdown keys
are returned verbatim per the API contract.

### 1.2 The orchestrator (`src/Orchestrator/`)

| File | Responsibility |
|---|---|
| `RunContext.cs` | The shared store for the whole run: key/value state, artifacts with content hashes, decision lineage, assumption log, approvals |
| `Dag.cs` | `AgentTask`/DAG model, topological **wave** computation, parallel fan-out, join barriers, `Invalidate()` for re-planning |
| `Engine.cs` | Wave execution (see §2.2); safe-stop on policy violation or denied approval; re-plan draining |
| `Gates.cs` | Registry of **named** entry (precondition) and exit (postcondition) predicates evaluated against the `RunContext` |
| `Approvals.cs` | Human checkpoints (`RequiresApproval`); `--auto` auto-approves **but logs the decision** |
| `Policies.cs` | 5 guardrail rules (see §2.4), evaluated **before** actions execute — fail-closed |
| `Retry.cs` | Bounded retries with exponential backoff + jitter, fallback chain, rollback hooks, `fatalExceptions` that skip retrying |
| `Audit.cs` | Append-only JSONL audit log (tamper-evident) + `manifest.json` per run |
| `Metrics.cs` | Success rate, retry count/frequency, rollback frequency, MTTR, end-to-end latency → `metrics.json` |
| `Replan.cs` | Content-hash drift detection, DAG mutation, invalidation of transitive dependents; governance is re-applied on re-run |
| `IAgent.cs` | Agent contract (`ExecuteAsync(RunContext, AgentTask, CancellationToken)`) |

### 1.3 The agents (`src/Agents/`)

| Agent | Modes |
|---|---|
| `PlannerAgent.cs` | `decompose` (ambiguity scoring, normalization, task decomposition), `clarify`, `inject_clarification` |
| `ArchitectAgent.cs` | `design` (ADRs + API spec), `brownfield_impact` (import-graph analysis over the materialized workspace), `smart_design` |
| `ImplementerAgent.cs` | `materialize_subset`, `write_tests`, `apply_v2` (unified diffs), `smart_feature`; every write is policy-checked **before** it happens; registers rollback hooks |
| `TesterAgent.cs` | Runs the real `dotnet test` suite as a **subprocess** in the scenario workspace; publishes a test-report artifact; its pass/fail verdict is a hard gate. Resolves dotnet from `DOTNET_BIN`, then `~/workspace/.dotnet/dotnet`, then `dotnet` on `PATH` |
| `DocumenterAgent.cs` | Renders `API.md`, `CHANGELOG.md`, `ASSUMPTIONS.md`, `SMART_LINKS.md` from context state |
| `ReleaseAgent.cs` | Release-readiness checklist (tests green, docs present, no policy violations, rollback plan recorded, approvals granted) + **independent** policy evaluation of the release action itself |
| `Codegen.cs` | **Single source of truth**: the full service implementation (v1 greenfield and v2 brownfield variants) held as data (`FileSpec` list); `Materialize()` writes `<workspace>/src/` + tests |
| `Agent.cs` | Shared base: policy-checked writes, artifact snapshots, decision recording |

### 1.4 Scenarios (`src/Scenarios/`)

- `Harness.cs` — shared setup/planning/DAG-build/run/finalize pipeline;
  `Finalize()` writes `decision_lineage.md` and prints the console summary.
- `Greenfield.cs`, `Brownfield.cs`, `Ambiguous.cs` — the three verified
  SDLC runs (see README). `Program.cs` is the CLI
  (`greenfield|brownfield|ambiguous [--auto]`).

## 2. The orchestration model

### 2.1 DAG waves, fan-out, join barriers

Work is an explicit DAG of `AgentTask`s. The engine computes topological
**waves**: all tasks whose dependencies have succeeded form a wave and run
**concurrently** (parallel fan-out). A wave is a synchronization barrier —
the next wave starts only when every task in the current wave has reached
a terminal state (join). In greenfield, for example, five parallel
implementation tasks fan out after architecture and converge on the test
join barrier before release.

Parallelism is implemented with dedicated `Task`s created with
`TaskCreationOptions.LongRunning` (each wave task gets its own thread)
bounded by a `SemaphoreSlim` — **not** the default thread pool. This is
deliberate: agent delegates block on subprocesses (`dotnet test`) and
locks, and running them on pooled threads caused thread-pool starvation
that serialized supposedly-parallel waves (caught by the parallel-overlap
test). Dedicated threads preserve deterministic fan-out and join
semantics.

### 2.2 Control-flow walkthrough of one wave

For each ready task, in order:

1. **Entry gates** — named preconditions (e.g. `tests_passed` before
   release) are evaluated against the `RunContext`. A failing gate blocks
   the task and is recorded in the audit log.
2. **Approval** — if `RequiresApproval`, execution pauses for a human
   (interactive prompt, or logged auto-approval under `--auto`). A denial
   safe-stops the run.
3. **Policy check** — the 5 guardrails are evaluated against the *action
   about to happen*. A denial raises `PolicyViolationException` and
   safe-stops the run; nothing half-applied is left behind.
4. **Agent execution** — wrapped in bounded retries: up to `maxAttempts`
   with exponential backoff + jitter between attempts. On exhaustion, a
   registered **fallback** agent is tried; rollback hooks then run as
   per-task compensation.
5. **Exit gates** — postconditions (e.g. `artifact_present(test_report)`).
6. **Output snapshot** — content hashes of produced artifacts are recorded
   on the task; these feed drift detection during re-planning.

`PolicyViolationException`s are fatal: they bypass retries entirely and
go straight to safe-stop, because a guardrail denial is a deliberate
verdict, not a transient failure.

### 2.3 Gates and approvals

Gates are **named predicates**, not inline conditionals — they are
registered once (`Gates.cs`) and referenced by name from task specs, so the
same precondition (`tests_passed`, `docs_present`, …) is spelled the same
way everywhere and every evaluation is auditable. Approvals are separate
from gates: a gate is a deterministic fact about the context; an approval
is a human judgment. The release task carries both: it must pass the
release checklist *and* carry `RequiresApproval`.

### 2.4 Policies (the 5 guardrails)

| Rule | Intent |
|---|---|
| `no_secrets_in_code` | Pattern-scan written code for secret-like literals |
| `allowed_write_paths` | Writes constrained to the run workspace |
| `tests_must_pass_before_release` | Release blocked unless the tester verdict is green |
| `no_destructive_migration_without_approval` | Schema-destroying changes need explicit approval |
| `no_unreviewed_release` | Release requires its approval checkpoint |

Policies are evaluated **before** the action they guard. Denial =
`PolicyViolationException` = safe-stop. The release agent additionally
evaluates the `release` policy independently of its checklist, so a policy
regression would block the release even if the checklist logic had a bug.

### 2.5 Retries, fallback, rollback

`Retry.ExecuteWithRetryAsync` (`Retry.cs`): bounded attempts (never
infinite), backoff `backoffBase * 2^(attempt-1)` plus jitter, then an
optional fallback delegate, then registered rollback hooks. Rollback here
is **per-task compensation** (e.g. undo the files a task wrote), not a
distributed saga — see [TESTING.md](TESTING.md) limitations.

### 2.6 Audit, metrics, decision lineage

- **Audit**: every state transition appends one JSON line to `audit.jsonl`
  (append-only → tamper-evident, replayable). `manifest.json` summarizes
  run identity, status, task outcomes, metrics, audit event counts, and
  replan history.
- **Metrics** (`metrics.json`): task counts, success rate, attempts/retries
  and retry frequency, rollbacks and rollback frequency, MTTR, end-to-end
  latency, and per-task breakdowns (duration, fallback/rollback used,
  outcome).
- **Decision lineage**: `decision_lineage.md` is a human-readable record of
  every decision the agents made and its rationale; the `RunContext` also
  keeps an assumption log (used heavily in the ambiguous scenario).

### 2.7 Re-planning

When upstream outputs change (or new information arrives, e.g. stakeholder
clarification), `Replan.cs` detects **content-hash drift** in task output
snapshots, mutates the DAG, invalidates the changed task and its
**transitive dependents**, and re-runs the affected subgraph. Crucially,
re-run tasks go through the **same governance** (gates → approvals →
policies → retries) as the first pass — governance is preserved, not
bypassed, by re-planning. The ambiguous scenario exercises this: pass 1
implements the planner's hypothesis (health monitoring only); after
clarification arrives, `replan_triggered` appears in the audit log and
implement/test re-run under the confirmed scope (device-aware smart
redirects + health monitoring).

## 3. Key design decisions

| # | Decision | Rationale | Alternatives considered |
|---|---|---|---|
| 1 | `Codegen.cs` as the **single source of truth** — the whole service is data; `Materialize()` generates `<workspace>/src/` + tests | The shipped product and the agent-generated code can never drift; scenario diffs (v1→v2) are computed against the same canonical content | Hand-maintaining `src/Service/` and duplicating it in agent templates (rejected: drift risk) |
| 2 | **Policies evaluated before writes** (fail-closed) | A denied write never touches the filesystem; the implementer's `PolicyViolationException` safe-stops before half-applied state | Check-after-write with rollback (rejected: leaves a window of bad state) |
| 3 | **Named gates** as a registry rather than inline checks | One spelling for each precondition everywhere; every gate evaluation is logged and auditable | Inline `if` conditions in agents (rejected: inconsistent, invisible) |
| 4 | **Re-plan via content hashing**, invalidating transitive dependents | Precise detection of what actually changed; dependents re-run even when the changed task itself is upstream | Timestamp-based invalidation (rejected: false positives); manual re-run lists (rejected: human error) |
| 5 | **v1→v2 with no DB migration** — custom aliases reuse the existing `code` primary key | The feature is additive; a migration would add risk for zero benefit | New table for aliases (rejected: unnecessary join complexity) |
| 6 | **Real `dotnet test` subprocess** in the tester agent | The quality gate measures the actual generated suite, not a simulation | In-process or mocked test results (rejected: dishonest gate) |
| 7 | **Fatal exceptions** bypass retries | Guardrail denials are verdicts, not transient failures — retrying them is wrong | Retrying everything (rejected: could loop on policy violations) |
| 8 | Rule-based deterministic **planner** (no LLM calls) | Fully explainable, reproducible runs; every decomposition is auditable | LLM-based planning (rejected for this build: non-determinism, cost, unverifiable reasoning) — see limitations in [TESTING.md](TESTING.md) |
| 9 | **Dedicated-thread wave fan-out** (`LongRunning` tasks + `SemaphoreSlim`) with `RunContext` as the shared store | Deterministic parallel fan-out/join for blocking agent delegates; the default thread pool starved under subprocess + lock workloads | `Task.Run` on the pool (rejected: starvation serialized parallel waves — caught by test) |
| 10 | Append-only **JSONL audit** + separate `manifest.json` | Tamper-evident history plus a cheap summary for tooling | Single mutable log file (rejected: rewrites destroy history) |
| 11 | `Microsoft.Data.Sqlite` directly, **no EF Core** | Matches the original's stdlib-sqlite minimalism; full control over schema and snake_case-free storage | EF Core (rejected: heavier, unnecessary for this schema) |
