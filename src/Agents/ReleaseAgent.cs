// Release agent: release-readiness checklist with policy enforcement.
//
// Verifies every release precondition (tests green, docs present, no policy
// violations, rollback plan recorded, approvals granted), evaluates the
// `release` policy action (which independently re-checks the guardrails), and
// publishes docs/RELEASE_CHECKLIST.md. The task itself also carries
// requires_approval=True, so a human signs off before the run is marked
// shippable.

using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class ReleaseAgent : Agent
{
    public override string Name => "release";

    public override AgentResult Run(RunContext ctx, TaskNode task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = ctx.Get<Dictionary<string, object?>>("test_report")
                     ?? new Dictionary<string, object?>();
        var counts = report.GetValueOrDefault("counts") as Dictionary<string, object?>
                     ?? new Dictionary<string, object?>();
        var documents = ctx.Get<List<string>>("documents") ?? new List<string>();
        var violations = ctx.Get<List<string>>("policy_violations") ?? new List<string>();
        var rollbackPlan = ctx.Get<Dictionary<string, object?>>("rollback_plan");
        var humanApproval = ctx.Approvals.Any(a =>
            a.GetValueOrDefault("task_id") as string == task.Id &&
            a.GetValueOrDefault("verdict") as string == "approved");

        var checks = new List<(string Name, bool Ok, string Note)>
        {
            ("tests passing",
             report.GetValueOrDefault("passed") is true,
             $"{counts.GetValueOrDefault("passed") ?? 0} passed, " +
             $"{counts.GetValueOrDefault("failed") ?? 0} failed"),
            ("documentation present",
             documents.Count > 0,
             $"{documents.Count} document(s)"),
            ("no policy violations",
             violations.Count == 0,
             $"{violations.Count} violation(s)"),
            ("rollback plan recorded",
             rollbackPlan is not null,
             rollbackPlan?.GetValueOrDefault("strategy") as string ?? "missing"),
            ("human approval granted",
             humanApproval,
             "approval checkpoint"),
        };
        // Independent policy enforcement: the release action itself is gated.
        bool policyOk;
        string policyNote;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PoliciesOf(ctx)?.Evaluate(
                new PolicyAction { Kind = "release", Target = task.Id },
                ctx, AuditOf(ctx));
            policyOk = true;
            policyNote = "release action allowed";
        }
        catch (PolicyViolationException exc)
        {
            policyOk = false;
            policyNote = exc.Message;
        }
        checks.Add(("release policy evaluation", policyOk, policyNote));

        var allGreen = checks.All(c => c.Ok);
        var lines = new List<string>
            { $"# Release Checklist — {ctx.Scenario} ({ctx.RunId})", "" };
        lines.AddRange(checks.Select(c =>
            $"- [{(c.Ok ? 'x' : ' ')}] **{c.Name}** — {c.Note}"));
        lines.AddRange(new[] { "", $"**Verdict: {(allGreen ? "GO" : "NO-GO")}**", "" });

        var ws = StrParam(task, "workspace_dir");
        var path = Path.Combine(ws, "docs", "RELEASE_CHECKLIST.md");
        var content = string.Join("\n", lines);
        WriteFile(ctx, path, content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.AppendToList("documents", "RELEASE_CHECKLIST.md");
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("release_decision", allGreen ? "GO" : "NO-GO");

        Decide(ctx, cancellationToken,
            $"release verdict: {(allGreen ? "GO" : "NO-GO")}",
            "checklist evaluated against context state + independent policy evaluation of the release action",
            basedOn: new List<string>
                { "test_report", "documents", "policy_violations", "rollback_plan", "approvals" },
            impact: allGreen ? "marks the run shippable" : "blocks the run; remediation required");
        if (!allGreen)
            throw new InvalidOperationException("release checklist not green: " +
                string.Join("; ", checks.Where(c => !c.Ok).Select(c => c.Name)));
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(new Dictionary<string, object?>
                  { ["verdict"] = "GO", ["checklist"] = path },
                  notes: "release checklist GO");
    }
}
