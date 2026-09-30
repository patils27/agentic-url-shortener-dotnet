// Entry/exit gates: explicit checks that bound every task execution.
//
// A gate is a named predicate evaluated against the RunContext. Entry gates run
// before a task starts (preconditions); exit gates run after it completes
// (postconditions). A failing gate blocks progress and is recorded in the audit
// log — the run either stops safely or the failure is routed to retry/fallback
// policy by the engine.

namespace AgenticUrlShortener.Orchestrator;

public sealed class GateResult
{
    public required string Gate { get; init; }
    /// <summary>"entry" | "exit"</summary>
    public required string Phase { get; init; }
    public required bool Passed { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public delegate GateResult GateCheck(RunContext ctx, Dictionary<string, object?> taskParams);

public sealed class GateRegistry
{
    private readonly Dictionary<string, GateCheck> _checks = new();

    public void Register(string name, GateCheck check) => _checks[name] = check;

    public GateCheck? Get(string name) =>
        _checks.TryGetValue(name, out var c) ? c : null;

    public List<GateResult> Evaluate(IEnumerable<string> names, string phase,
                                     RunContext ctx, Dictionary<string, object?> taskParams)
    {
        var results = new List<GateResult>();
        foreach (var name in names)
        {
            if (!_checks.TryGetValue(name, out var check))
            {
                results.Add(new GateResult { Gate = name, Phase = phase, Passed = false,
                                             Reason = $"unknown gate '{name}'" });
                continue;
            }
            try
            {
                results.Add(check(ctx, taskParams));
            }
            catch (Exception ex) // a crashing gate is a failed gate
            {
                results.Add(new GateResult { Gate = name, Phase = phase, Passed = false,
                                             Reason = $"gate raised: {ex.Message}" });
            }
        }
        return results;
    }
}

// ---------------------------------------------------------------------------
// Built-in gate checks
// ---------------------------------------------------------------------------
public static class BuiltinGates
{
    private static GateResult Ok(string gate, string phase) =>
        new() { Gate = gate, Phase = phase, Passed = true };

    private static GateResult Fail(string gate, string phase, string reason) =>
        new() { Gate = gate, Phase = phase, Passed = false, Reason = reason };

    public static GateResult CheckPlanExists(RunContext ctx, Dictionary<string, object?> p)
    {
        if (ctx.Get("plan") is Dictionary<string, object?> plan &&
            plan.GetValueOrDefault("tasks") is List<Dictionary<string, object?>> tasks &&
            tasks.Count > 0)
            return Ok("plan_exists", "entry");
        return Fail("plan_exists", "entry", "no decomposed plan in context");
    }

    public static GateResult CheckRequirementNormalized(RunContext ctx, Dictionary<string, object?> p) =>
        ctx.Get("normalized_requirement") is not null
            ? Ok("requirement_normalized", "entry")
            : Fail("requirement_normalized", "entry", "requirement was not normalized by the planner");

    public static GateResult CheckArchitectureDecided(RunContext ctx, Dictionary<string, object?> p) =>
        ctx.Get("architecture") is not null
            ? Ok("architecture_decided", "entry")
            : Fail("architecture_decided", "entry", "no architecture decisions recorded");

    public static GateResult CheckNoOpenBlockers(RunContext ctx, Dictionary<string, object?> p)
    {
        var blockers = ctx.Get<List<string>>("blockers") ?? new List<string>();
        return blockers.Count == 0
            ? Ok("no_open_blockers", "entry")
            : Fail("no_open_blockers", "entry", $"open blockers: {string.Join(", ", blockers)}");
    }

    public static GateResult CheckTestsPassed(RunContext ctx, Dictionary<string, object?> p)
    {
        // Mirrors the Python gate: a truthy top-level "passed" with no
        // top-level "failed" count. (The per-count breakdown lives in
        // report["counts"].)
        var report = ctx.Get("test_report") as Dictionary<string, object?>;
        var passed = report is not null && report.GetValueOrDefault("passed") is true;
        var failedTop = report?.GetValueOrDefault("failed");
        var hasFailed = failedTop is int i && i != 0 || failedTop is long l && l != 0;
        if (passed && !hasFailed)
            return Ok("tests_passed", "entry");
        var detail = report is null ? "no test report" : $"{failedTop ?? "?"} failing tests";
        return Fail("tests_passed", "entry", detail);
    }

    public static GateResult CheckDocsPresent(RunContext ctx, Dictionary<string, object?> p)
    {
        var docs = ctx.Get<List<string>>("documents") ?? new List<string>();
        return docs.Count > 0
            ? Ok("docs_present", "entry")
            : Fail("docs_present", "entry", "documenter produced no documents");
    }

    public static GateResult CheckNoPolicyViolations(RunContext ctx, Dictionary<string, object?> p)
    {
        var violations = ctx.Get<List<string>>("policy_violations") ?? new List<string>();
        return violations.Count == 0
            ? Ok("no_policy_violations", "entry")
            : Fail("no_policy_violations", "entry", $"{violations.Count} policy violation(s) recorded");
    }

    public static GateResult CheckRollbackPlanExists(RunContext ctx, Dictionary<string, object?> p) =>
        ctx.Get("rollback_plan") is not null
            ? Ok("rollback_plan_exists", "entry")
            : Fail("rollback_plan_exists", "entry", "no rollback plan recorded");

    /// <summary>Exit gate: the task must have produced the artifact named in params.</summary>
    public static GateResult CheckArtifactPresent(RunContext ctx, Dictionary<string, object?> p)
    {
        var name = p.GetValueOrDefault("produces_artifact") as string;
        if (string.IsNullOrEmpty(name))
            return Ok("artifact_present", "exit");
        return ctx.Artifacts.ContainsKey(name)
            ? Ok("artifact_present", "exit")
            : Fail("artifact_present", "exit", $"expected artifact '{name}' was not produced");
    }

    public static GateRegistry DefaultRegistry()
    {
        var reg = new GateRegistry();
        reg.Register("plan_exists", CheckPlanExists);
        reg.Register("requirement_normalized", CheckRequirementNormalized);
        reg.Register("architecture_decided", CheckArchitectureDecided);
        reg.Register("no_open_blockers", CheckNoOpenBlockers);
        reg.Register("tests_passed", CheckTestsPassed);
        reg.Register("docs_present", CheckDocsPresent);
        reg.Register("no_policy_violations", CheckNoPolicyViolations);
        reg.Register("rollback_plan_exists", CheckRollbackPlanExists);
        reg.Register("artifact_present", CheckArtifactPresent);
        return reg;
    }
}
