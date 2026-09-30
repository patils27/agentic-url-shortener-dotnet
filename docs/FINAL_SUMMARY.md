# Final Summary

Project completion report for the **Agentic Software Engineering System —
URL Shortener (.NET 10)**. See [ARCHITECTURE.md](ARCHITECTURE.md) for
design detail and [TESTING.md](TESTING.md) for verification.

## 1. Plan and rationale

**Plan:** port the Python assessment solution feature-for-feature to C# on
.NET 10 — (a) a working URL shortener service, and (b) the agentic SDLC
framework that builds it — then prove the framework by running it through
three SDLC scenarios: greenfield build, brownfield evolution, and
ambiguous-requirement handling. Build order: scaffold → Orchestrator →
Agents → Service → Scenarios → tests → docs.

**Rationale:** a toy orchestrator that "simulates" agents proves nothing.
Every agent here does real work on the real filesystem: the implementer
writes actual files (policy-checked first), the tester runs the actual
`dotnet test` suite as a subprocess, the architect analyzes the
materialized workspace. The strongest correctness mechanism is
structural: `src/Agents/Codegen.cs` is the **single source of truth** for
the service, so the shipped `src/Service/` and the scenario-generated
code are *generated* artifacts — the product and the agent-produced code
cannot drift. Governance (gates, approvals, policies) is enforced in the
engine, not requested of the agents, so an agent cannot "forget" to be
safe.

## 2. Artifacts produced

| Artifact | Path |
|---|---|
| Service implementation (ASP.NET Core, `net10.0`) | `src/Service/` (`ShortenerApp.cs`, `Models.cs`, `UrlStore.cs`, `ClickAnalytics.cs`, `RateLimiter.cs`, `Validators.cs`, `ServiceOptions.cs`, `Program.cs`) |
| Service tests (12 tests, `WebApplicationFactory`) | `tests/Service.Tests/` |
| Orchestrator framework | `src/Orchestrator/` (11 files: `Dag`, `RunContext`, `Gates`, `Approvals`, `Policies`, `Retry`, `Audit`, `Metrics`, `Replan`, `IAgent`, `Engine`) |
| SDLC agents | `src/Agents/` (Planner, Architect, Implementer, Tester, Documenter, Release, base `Agent`, `Codegen`) |
| Single source of truth | `src/Agents/Codegen.cs` (v1 + v2 service variants as data; `Materialize()`) |
| Orchestrator tests (12 tests) | `tests/Orchestrator.Tests/` |
| Scenario runners | `src/Scenarios/` (harness + greenfield, brownfield, ambiguous + CLI) |
| Build log | `runs/BUILD_LOG.md` |
| Scenario run bundles | `runs/<scenario>/<run_id>/` — `audit.jsonl`, `metrics.json`, `decision_lineage.md`, `manifest.json`, `workspace/` (generated code + docs) |
| Documentation | `README.md`, `docs/ARCHITECTURE.md`, `docs/TESTING.md`, `docs/FINAL_SUMMARY.md` |
| Pinned dependencies | `.csproj` files (`Microsoft.Data.Sqlite` 10.0.7 + `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 pin, xUnit 2.9.2 / test SDK 17.11.1, `Microsoft.AspNetCore.Mvc.Testing` 10.0.0; SDK 10.0.401; `net10.0` everywhere) |

Test result: **24/24 passing** (`dotnet test AgenticUrlShortener.sln`),
warning-free build. Live curl demo verified: create 201 → redirect 307 →
stats (click recorded) → idempotent replay (201→200 same body) →
invalid URL 422 → unknown 404 → expired 410 → alias conflict 409 →
delete 204 → rate limit 429 + `Retry-After` (health bypasses) →
`/health`, `/ready` (with DB check).

## 3. Scenario outcomes

| Scenario | Command | Outcome |
|---|---|---|
| **greenfield** | `dotnet run --project src/Scenarios -- greenfield --auto` | Succeeded (run `20260922-052254-9173`). 8/8 tasks, success_rate 1, 1 approval. Architecture → five parallel implementation tasks fanned out → converged on the test join barrier (real `dotnet test`: 9/9 generated tests) → release with human approval. |
| **brownfield** | `dotnet run --project src/Scenarios -- brownfield --auto` | Succeeded (run `20260922-052305-9398`). 6/6 tasks. v1 baseline materialized; import-graph impact analysis; implementer evolved v1→v2 (custom aliases, 410-for-expired fix, validators extraction); regression tests (12/12 generated) + changelog in parallel; release approved (the 404→410 change is intentionally behavior-breaking). |
| **ambiguous** | `dotnet run --project src/Scenarios -- ambiguous --auto` | Succeeded (run `20260922-052316-9646`). `replans=1`; implement/test executed twice (hypothesis pass, then confirmed-scope pass). Planner detected the vague "make short links smarter", normalized a hypothesis with **5 logged assumptions**, passed the clarification checkpoint, and after clarification was injected the re-plan invalidated the downstream subgraph and re-ran it under full governance — `replan_triggered` in the audit log, all 5 assumptions confirmed. Final scope: device-aware smart redirects + health monitoring. |

## 4. Key decisions (condensed)

- `Codegen.cs` single source of truth — shipped code is generated, cannot drift.
- Policies evaluated **before** writes (fail-closed); `PolicyViolationException`
  safe-stops without retrying (fatal).
- Named gate registry; approvals as human judgments separate from gates.
- Re-plan via content-hash drift detection with transitive-dependent
  invalidation; governance re-applied on re-run.
- v1→v2 with **no DB migration** (custom alias reuses the `code` PK).
- Real `dotnet test` subprocess in the tester agent — the gate measures reality.
- Deterministic rule-based planner (no LLM): explainable and reproducible,
  but not genuinely understanding — see §6.
- Wave fan-out on dedicated `LongRunning` threads + `SemaphoreSlim`, not
  the thread pool (starvation serialized parallel waves — caught by test).
- `Microsoft.Data.Sqlite` directly, no EF Core (mirrors the original's
  stdlib-sqlite minimalism).

## 5. Assumptions made

- The ambiguous scenario's 5 planner assumptions were all confirmed at the
  clarification checkpoint (recorded in that run's `decision_lineage.md`).
- SQLite via `Microsoft.Data.Sqlite` is sufficient (single-node, no external DB).
- 60 req/min per-IP rate limit with burst 10 is a reasonable default.
- `--auto` mode (logged auto-approval) is acceptable for demos and CI;
  interactive runs require real human sign-off.
- Expiry tested via direct DB inserts stands in for wall-clock TTL behavior.
- The v1 404-for-expired behavior was a planted bug, fixed deliberately
  as a behavior-breaking change in brownfield with explicit release approval.
- SDK installed at `~/workspace/.dotnet` (8.0.425); tester resolves
  `dotnet` from `DOTNET_BIN` → `~/workspace/.dotnet/dotnet` → `PATH`.

## 6. Limitations

- No LLM calls anywhere: rule-based planner is explainable but does not
  genuinely understand novel requirements.
- SQLite single-node; in-memory per-process rate limiter with an
  unboundedly growing bucket map (needs Redis + eviction for production).
- Rollback is per-task compensation, no distributed saga.
- Pattern-based secret scanning (can miss or false-positive).
- Expiry simulated via DB inserts, not real TTL waiting.
- `--auto` simulates rather than provides human oversight.
- No load, chaos, or fault-injection testing.
- Generated scenario workspaces under `runs/*/workspace/` are not in the
  solution — the tester agent's subprocess is their only compiler.
- VSTest test discovery requires IPv6 disabled in this sandbox
  (`RuntimeHostConfigurationOption` + `DOTNET_SYSTEM_NET_DISABLEIPV6=1`);
  without it, `dotnet test` stalls.

## 7. Risks and validation

| Risk | Mitigation / validation |
|---|---|
| Agent-generated code drifts from shipped code | Structural: `Codegen.cs` is the single source of truth; service code is generated |
| Unsafe writes or secret leaks | Policies evaluated before writes (fail-closed); `allowed_write_paths` confines writes to the workspace |
| Broken release shipped | `tests_must_pass_before_release` + real `dotnet test` subprocess + `requires_approval` on release; 404→410 change explicitly approved |
| Mid-run scope change corrupts governance | Re-plan re-runs the invalidated subgraph through the same gates/approvals/policies; verified in the ambiguous scenario |
| Silent failures | Append-only JSONL audit (replayable), `metrics.json` (success rate, retries, rollbacks, MTTR, latency), `decision_lineage.md` per run |
| Parallel waves silently serializing | Dedicated-thread fan-out + an orchestrator test asserting real timing overlap |

Net: the framework demonstrably plans, builds, tests, documents,
releases, evolves, and re-plans real software under enforced governance —
with the honest caveat that its "intelligence" is deterministic
heuristics, not an LLM.
