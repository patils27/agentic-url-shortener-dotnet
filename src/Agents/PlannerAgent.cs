// Planner agent: requirement understanding and task decomposition.
//
// Modes:
//   - decompose: analyze a requirement, detect ambiguity, normalize it into
//     a structured engineering problem, and decompose it into an ordered
//     task DAG (specs with dependencies, gates, approvals, retry policy).
//   - clarify: (ambiguous scenario) normalize a vague requirement into an
//     explicit hypothesis, log every assumption, and mark clarification
//     needed.
//   - inject_clarification: simulate the stakeholder's clarification
//     response; updates the normalized requirement and queues a re-plan
//     request so the orchestrator invalidates and re-runs the affected
//     subgraph.

using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class PlannerAgent : Agent
{
    public override string Name => "planner";

    private static readonly string[] VagueTerms =
    {
        "smarter", "better", "improve", "improved", "improvement", "optimize",
        "robust", "seamless", "intuitive", "user-friendly", "leverage",
        "synergy", "etc.", "various", "somehow", "nice",
    };

    private static readonly string[] MeasurableHints =
    {
        "must", "should", "api", "endpoint", "/api", "test",
        "xunit", "ms", "%", "http", "sqlite", "asp.net",
    };

    public static Dictionary<string, object?> DetectAmbiguity(string requirement)
    {
        var lowered = requirement.ToLowerInvariant();
        var vague = VagueTerms.Where(t => lowered.Contains(t)).ToList();
        var words = requirement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var measurable = MeasurableHints.Any(h => lowered.Contains(h));
        var score = vague.Count * 2 + (measurable ? 0 : 3) + (words < 12 ? 2 : 0);
        return new Dictionary<string, object?>
        {
            ["ambiguous"] = score >= 3,
            ["score"] = score,
            ["vague_terms"] = vague,
            ["word_count"] = words,
            ["has_measurable_criteria"] = measurable,
        };
    }

    private static Dictionary<string, object?> Spec(
        string id, string agent, string name, Dictionary<string, object?> @params,
        List<string>? deps = null, List<string>? entryGates = null,
        List<string>? exitGates = null, bool requiresApproval = false,
        string approvalSummary = "", string approvalImpact = "",
        int maxRetries = 3, double backoffBase = 0.05, double timeoutS = 300.0,
        string? fallbackAgent = null, Dictionary<string, object?>? paramsExtra = null)
    {
        var merged = new Dictionary<string, object?>(@params);
        if (paramsExtra is not null)
            foreach (var (k, v) in paramsExtra) merged[k] = v;
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["agent"] = agent, ["name"] = name, ["params"] = merged,
            ["deps"] = deps ?? new List<string>(),
            ["entry_gates"] = entryGates ?? new List<string>(),
            ["exit_gates"] = exitGates ?? new List<string>(),
            ["requires_approval"] = requiresApproval,
            ["approval_summary"] = approvalSummary,
            ["approval_impact"] = approvalImpact,
            ["max_retries"] = maxRetries,
            ["backoff_base"] = backoffBase,
            ["timeout_s"] = timeoutS,
            ["fallback_agent"] = fallbackAgent,
        };
    }

    public override AgentResult Run(RunContext ctx, TaskNode task)
    {
        var mode = StrParam(task, "mode", "decompose");
        return mode switch
        {
            "clarify" => Clarify(ctx, task),
            "inject_clarification" => InjectClarification(ctx, task),
            _ => Decompose(ctx, task),
        };
    }

    // ------------------------------------------------------------------
    private AgentResult Decompose(RunContext ctx, TaskNode task)
    {
        var requirement = StrParam(task, "requirement");
        var kind = StrParam(task, "scenario_kind", "greenfield");
        var analysis = DetectAmbiguity(requirement);
        ctx.Put("ambiguity_analysis", analysis);
        var vagueTerms = (List<string>)analysis["vague_terms"]!;
        var vagueDesc = vagueTerms.Count == 0 ? "none" : string.Join(", ", vagueTerms);
        Decide(ctx,
            $"requirement classified as {(analysis["ambiguous"] is true ? "AMBIGUOUS" : "WELL-DEFINED")} (score {analysis["score"]})",
            $"vague terms: {vagueDesc}; measurable criteria: {analysis["has_measurable_criteria"]}",
            basedOn: new List<string> { "requirement text" },
            impact: "drives decomposition strategy and clarification checkpoints");

        var normalized = Normalize(requirement, kind);
        ctx.Put("normalized_requirement", normalized);
        ctx.AddArtifact("normalized_requirement", "data",
                        producedBy: $"planner:{task.Id}",
                        content: JsonSerializer.Serialize(normalized,
                            new JsonSerializerOptions { WriteIndented = false }));
        var specs = kind switch
        {
            "greenfield" => GreenfieldPlan(task),
            "brownfield" => BrownfieldPlan(task),
            "ambiguous" => AmbiguousPlan(task),
            _ => throw new ArgumentException($"unknown scenario kind '{kind}'"),
        };
        var plan = new Dictionary<string, object?>
        {
            ["requirement"] = requirement, ["normalized"] = normalized,
            ["tasks"] = specs,
        };
        ctx.Put("plan", plan);
        ctx.AddArtifact("plan", "data", producedBy: $"planner:{task.Id}",
                        content: JsonSerializer.Serialize(plan));
        Decide(ctx,
            $"decomposed '{kind}' scope into {specs.Count} tasks with explicit dependencies",
            "independent modules/docs fan out in parallel; integration points (tests, release) are join barriers",
            basedOn: new List<string> { "normalized requirement" },
            impact: $"{specs.Count} DAG nodes; release gate is the final barrier");
        return Ok(new Dictionary<string, object?> { ["plan"] = plan },
                  artifacts: new List<string> { "plan" });
    }

    private static Dictionary<string, object?> Normalize(string requirement, string kind)
    {
        var sentences = Regex.Split(requirement, @"[.;]\s*")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var tech = new[] { "c#", "asp.net", "aspnet", "sqlite", "xunit",
                           "kestrel", "dotnet", ".net" };
        var goals = sentences.Where(s => !tech.Any(k => s.ToLowerInvariant().Contains(k))).ToList();
        var constraints = sentences.Where(s => !goals.Contains(s)).ToList();
        var acceptance = new Dictionary<string, List<string>>
        {
            ["greenfield"] = new() { "all API endpoints respond per spec",
                "xunit suite passes", "docs generated", "release checklist green" },
            ["brownfield"] = new() { "custom aliases work incl. 409 conflict",
                "expired links return 410", "validators extracted with unit tests",
                "no existing test regresses" },
            ["ambiguous"] = new() { "assumptions logged and confirmed",
                "clarification checkpoint passed", "re-plan executed after clarification" },
        }[kind];
        return new Dictionary<string, object?>
        {
            ["goals"] = goals, ["constraints"] = constraints,
            ["acceptance_criteria"] = acceptance,
        };
    }

    // ------------------------- plan builders ---------------------------
    private static List<Dictionary<string, object?>> GreenfieldPlan(TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var impl = new Dictionary<string, object?>
            { ["mode"] = "materialize_subset", ["variant"] = "v1", ["workspace_dir"] = ws };
        return new List<Dictionary<string, object?>>
        {
            Spec("architect", "architect", "Architecture & design",
                new Dictionary<string, object?> { ["mode"] = "design", ["workspace_dir"] = ws },
                entryGates: new() { "plan_exists", "requirement_normalized" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "architecture_decisions" }),
            Spec("impl_models", "implementer", "Implement models + store",
                new Dictionary<string, object?>(impl)
                    { ["files"] = new List<string> { "Shortener/Shortener.csproj",
                        "Shortener/Models.cs", "Shortener/UrlStore.cs" } },
                deps: new() { "architect" }, entryGates: new() { "architecture_decided" }),
            Spec("impl_api", "implementer", "Implement API layer",
                new Dictionary<string, object?>(impl)
                    { ["files"] = new List<string> { "Shortener/Program.cs",
                        "Shortener/ShortenerOptions.cs" } },
                deps: new() { "architect" }, entryGates: new() { "architecture_decided" }),
            Spec("impl_analytics", "implementer", "Implement analytics + rate limiting",
                new Dictionary<string, object?>(impl)
                    { ["files"] = new List<string> { "Shortener/ClickAnalytics.cs",
                        "Shortener/RateLimiter.cs" } },
                deps: new() { "architect" }, entryGates: new() { "architecture_decided" }),
            Spec("write_tests", "implementer", "Write service test suite",
                new Dictionary<string, object?> { ["mode"] = "write_tests",
                    ["variant"] = "v1", ["workspace_dir"] = ws },
                deps: new() { "architect" }, entryGates: new() { "architecture_decided" }),
            Spec("write_docs", "documenter", "Write API documentation",
                new Dictionary<string, object?> { ["mode"] = "api_docs", ["workspace_dir"] = ws },
                deps: new() { "architect" }, entryGates: new() { "architecture_decided" }),
            Spec("run_tests", "tester", "Run xunit suite",
                new Dictionary<string, object?> { ["mode"] = "dotnet_test",
                    ["workspace_dir"] = ws, ["test_project"] = "Shortener.Tests/Shortener.Tests.csproj" },
                deps: new() { "impl_models", "impl_api", "impl_analytics", "write_tests" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "test_report" }),
            Spec("release", "release", "Release readiness review",
                new Dictionary<string, object?> { ["mode"] = "checklist", ["workspace_dir"] = ws },
                deps: new() { "run_tests", "write_docs" },
                entryGates: new() { "tests_passed", "docs_present",
                    "no_policy_violations", "rollback_plan_exists" },
                requiresApproval: true,
                approvalSummary: "Release v0.1.0 of the URL shortener",
                approvalImpact: "marks the greenfield build as shippable; no production traffic in this demo"),
        };
    }

    private static List<Dictionary<string, object?>> BrownfieldPlan(TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        return new List<Dictionary<string, object?>>
        {
            Spec("analyze_impact", "architect", "Impacted-module analysis",
                new Dictionary<string, object?> { ["mode"] = "brownfield_impact",
                    ["workspace_dir"] = ws, ["baseline"] = "v1" },
                entryGates: new() { "plan_exists", "requirement_normalized" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "impact_analysis" }),
            Spec("implement_changes", "implementer", "Apply enhancement + bug fix + refactor",
                new Dictionary<string, object?> { ["mode"] = "apply_v2", ["workspace_dir"] = ws },
                deps: new() { "analyze_impact" }, entryGates: new() { "architecture_decided" }),
            Spec("add_regression_tests", "implementer", "Add/refresh regression tests",
                new Dictionary<string, object?> { ["mode"] = "write_tests",
                    ["variant"] = "v2", ["workspace_dir"] = ws },
                deps: new() { "analyze_impact" }, entryGates: new() { "architecture_decided" }),
            Spec("update_changelog", "documenter", "Update changelog",
                new Dictionary<string, object?> { ["mode"] = "changelog", ["workspace_dir"] = ws },
                deps: new() { "analyze_impact" }, entryGates: new() { "architecture_decided" }),
            Spec("run_tests", "tester", "Run full xunit suite",
                new Dictionary<string, object?> { ["mode"] = "dotnet_test",
                    ["workspace_dir"] = ws, ["test_project"] = "Shortener.Tests/Shortener.Tests.csproj" },
                deps: new() { "implement_changes", "add_regression_tests" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "test_report" }),
            Spec("release", "release", "Release readiness review",
                new Dictionary<string, object?> { ["mode"] = "checklist", ["workspace_dir"] = ws },
                deps: new() { "run_tests", "update_changelog" },
                entryGates: new() { "tests_passed", "docs_present",
                    "no_policy_violations", "rollback_plan_exists" },
                requiresApproval: true,
                approvalSummary: "Release v1.0.0 (aliases + 410 fix + validators refactor)",
                approvalImpact: "changes existing API behavior (404->410); requires sign-off"),
        };
    }

    private static List<Dictionary<string, object?>> AmbiguousPlan(TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var req = StrParam(task, "requirement");
        return new List<Dictionary<string, object?>>
        {
            Spec("clarify", "planner", "Normalize ambiguous requirement",
                new Dictionary<string, object?> { ["mode"] = "clarify",
                    ["workspace_dir"] = ws, ["requirement"] = req },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "normalized_requirement" }),
            Spec("design_smart", "architect", "Design smart-link feature",
                new Dictionary<string, object?> { ["mode"] = "smart_design", ["workspace_dir"] = ws },
                deps: new() { "clarify" }, entryGates: new() { "requirement_normalized" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "smart_design" }),
            Spec("implement_smart", "implementer", "Implement smart-link feature",
                new Dictionary<string, object?> { ["mode"] = "smart_feature", ["workspace_dir"] = ws },
                deps: new() { "design_smart" }, entryGates: new() { "architecture_decided" }),
            Spec("test_smart", "tester", "Test smart-link feature",
                new Dictionary<string, object?> { ["mode"] = "dotnet_test",
                    ["workspace_dir"] = ws, ["test_project"] = "Shortener.Tests/Shortener.Tests.csproj" },
                deps: new() { "implement_smart" },
                exitGates: new() { "artifact_present" },
                paramsExtra: new() { ["produces_artifact"] = "test_report" }),
            Spec("await_clarification", "planner", "Stakeholder clarification checkpoint",
                new Dictionary<string, object?> { ["mode"] = "inject_clarification",
                    ["workspace_dir"] = ws },
                deps: new() { "test_smart" },
                requiresApproval: true,
                approvalSummary: "Confirm the interpretation of 'make short links smarter'",
                approvalImpact: "a changed interpretation triggers a re-plan and re-execution of design->test"),
            Spec("document", "documenter", "Document assumptions + smart-link API",
                new Dictionary<string, object?> { ["mode"] = "assumptions", ["workspace_dir"] = ws },
                deps: new() { "await_clarification" }),
            Spec("release", "release", "Release readiness review",
                new Dictionary<string, object?> { ["mode"] = "checklist", ["workspace_dir"] = ws },
                deps: new() { "document" },
                entryGates: new() { "tests_passed", "docs_present",
                    "no_policy_violations", "rollback_plan_exists" },
                requiresApproval: true,
                approvalSummary: "Release smart-links iteration",
                approvalImpact: "new endpoints; backward compatible"),
        };
    }

    // ------------------------------------------------------------------
    private AgentResult Clarify(RunContext ctx, TaskNode task)
    {
        // Re-run after a re-plan must not regress a confirmed clarification.
        if (ctx.Get("clarified_requirement") is not null)
        {
            Decide(ctx, "keep confirmed clarification on re-run",
                "clarified requirement already exists; re-affirming instead of re-guessing",
                basedOn: new List<string> { "clarified_requirement" });
            return Ok(new Dictionary<string, object?> { ["clarified"] = true },
                      notes: "clarification already confirmed");
        }

        var requirement = StrParam(task, "requirement");
        var analysis = DetectAmbiguity(requirement);
        ctx.Put("ambiguity_analysis", analysis);

        var assumptions = new[]
        {
            "'smarter' means device-aware redirect routing (mobile/desktop/tablet get different targets)",
            "'smarter' means automatic target-URL health monitoring",
            "no database schema changes in this iteration",
            "all new behavior must be backward compatible with v1 clients",
            "scope is limited to link intelligence, not analytics UI",
        };
        foreach (var stmt in assumptions) ctx.LogAssumption(Name, stmt);

        var hypothesis = new Dictionary<string, object?>
        {
            ["interpretation"] = "planner hypothesis (unconfirmed)",
            ["features"] = new List<string>
            {
                "target-URL health monitoring via GET /api/urls/{code}/health",
                "user-agent device classification utility",
            },
            ["non_goals"] = new List<string>
            {
                "device-specific redirect targets (needs product confirmation)",
            },
        };
        ctx.Put("normalized_requirement", hypothesis);
        ctx.AddArtifact("normalized_requirement", "data",
                        producedBy: $"planner:{task.Id}",
                        content: JsonSerializer.Serialize(hypothesis));
        Decide(ctx,
            "normalized 'make short links smarter' into an explicit hypothesis with 5 logged assumptions",
            $"ambiguity score {analysis["score"]} (vague terms: {string.Join(", ", (List<string>)analysis["vague_terms"]!)}); " +
            "proceeding with a documented hypothesis and a clarification checkpoint instead of stalling",
            basedOn: new List<string> { "requirement text", "ambiguity analysis" },
            impact: "downstream design/implementation proceed against the hypothesis until clarification arrives",
            alternatives: new List<string>
            {
                "block for clarification before any work",
                "pick device-routing as the hypothesis",
            });
        return Ok(new Dictionary<string, object?> { ["hypothesis"] = hypothesis },
                  notes: "hypothesis recorded; clarification pending",
                  artifacts: new List<string> { "normalized_requirement" });
    }

    private AgentResult InjectClarification(RunContext ctx, TaskNode task)
    {
        if (ctx.GetFlagBool("clarification_injected"))
            return Ok(new Dictionary<string, object?> { ["already_injected"] = true },
                      notes: "clarification already applied");

        var clarified = new Dictionary<string, object?>
        {
            ["interpretation"] = "stakeholder-confirmed",
            ["features"] = new List<string>
            {
                "device-aware smart redirects: per-link rules map device class (mobile/desktop/tablet/bot) to targets",
                "target-URL health monitoring via GET /api/urls/{code}/health",
                "device classification API POST /api/smart/classify",
            },
            ["non_goals"] = new List<string> { "analytics UI changes" },
        };
        ctx.Put("clarified_requirement", clarified);
        ctx.Put("normalized_requirement", clarified);
        ctx.AddArtifact("normalized_requirement", "data",
                        producedBy: $"planner:{task.Id}",
                        content: JsonSerializer.Serialize(clarified));
        foreach (var aid in new[] { "A01", "A02", "A03", "A04", "A05" })
            ctx.ResolveAssumption(aid, "confirmed", "stakeholder");
        ctx.SetFlag("clarification_injected");
        var requests = ctx.Get<List<Dictionary<string, object?>>>("replan_requests") ?? new();
        requests.Add(new Dictionary<string, object?>
        {
            ["reason"] = "stakeholder clarification received: 'smarter' = device-aware smart redirects + link health monitoring (supersedes planner hypothesis)",
            ["changed_task_ids"] = new List<string> { "clarify" },
        });
        ctx.Put("replan_requests", requests);
        Decide(ctx,
            "applied stakeholder clarification; queued re-plan",
            "confirmed scope (device-aware redirects + health) differs from the planner hypothesis (health only); downstream artifacts are stale and must be rebuilt",
            basedOn: new List<string> { "stakeholder clarification", "assumption log" },
            impact: "re-plan invalidates clarify + all downstream tasks; they re-execute under the same gates and approvals");
        return Ok(new Dictionary<string, object?> { ["clarified"] = clarified },
                  notes: "clarification applied; re-plan queued");
    }
}
