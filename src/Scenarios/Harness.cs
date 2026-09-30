// Shared harness for the three scenario runs.
//
// Each scenario: SetupRun() -> planning pre-phase (planner agent decomposes
// the requirement) -> BuildDag() -> RunEngine() -> Finalize() which writes
// decision_lineage.md and prints a human-readable summary.
//
// Run from the repo root, e.g.:
//   dotnet run --project src/Scenarios -- greenfield --auto

using AgenticUrlShortener.Agents;
using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Scenarios;

public sealed record RunSetup(
    RunContext Ctx,
    AuditLogger Audit,
    PolicyEngine Policies,
    ApprovalManager Approvals,
    string RunDir,
    string Workspace,
    string RunId);

public static class Harness
{
    public static string RepoRoot { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static Dictionary<string, IAgent> MakeAgents() => new()
    {
        ["planner"] = new PlannerAgent(),
        ["architect"] = new ArchitectAgent(),
        ["implementer"] = new ImplementerAgent(),
        ["tester"] = new TesterAgent(),
        ["documenter"] = new DocumenterAgent(),
        ["release"] = new ReleaseAgent(),
    };

    public static RunSetup SetupRun(string scenario, bool auto = true)
    {
        var runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        var runDir = Path.Combine(RepoRoot, "runs", scenario, runId);
        var workspace = Path.Combine(runDir, "workspace");
        Directory.CreateDirectory(workspace);
        var ctx = new RunContext(scenario, runId, runDir);
        var audit = new AuditLogger(runId, runDir);
        var policies = new PolicyEngine();
        var approvals = new ApprovalManager(auto: auto);
        ctx.Put("allowed_write_roots", new List<string> { workspace, runDir });
        ctx.Put("_audit", audit);
        ctx.Put("_policies", policies);
        return new RunSetup(ctx, audit, policies, approvals, runDir, workspace, runId);
    }

    public static Dictionary<string, object?> RunPlanningPhase(
        RunContext ctx, AuditLogger audit, string requirement,
        string scenarioKind, string workspace)
    {
        var planner = new PlannerAgent();
        var task = new TaskNode
        {
            Id = "plan", Name = "planning-phase", Agent = "planner",
            Params = new Dictionary<string, object?>
            {
                ["mode"] = "decompose",
                ["requirement"] = requirement,
                ["scenario_kind"] = scenarioKind,
                ["workspace_dir"] = workspace,
            },
        };
        audit.Log("planning_phase_started",
                  details: new Dictionary<string, object?> { ["scenario_kind"] = scenarioKind });
        var result = planner.Run(ctx, task);
        var plan = (Dictionary<string, object?>)result.Outputs["plan"]!;
        var tasks = (List<Dictionary<string, object?>>)plan["tasks"]!;
        audit.Log("planning_phase_finished",
                  details: new Dictionary<string, object?>
                  {
                      ["tasks"] = tasks.Count,
                      ["task_ids"] = tasks.Select(s => s["id"]).ToList(),
                  });
        return plan;
    }

    private static List<string> ToStringList(object? value) =>
        value switch
        {
            List<string> l => l,
            System.Collections.IEnumerable e =>
                e.Cast<object>().Select(x => x?.ToString() ?? "").ToList(),
            _ => new List<string>(),
        };

    public static Dag BuildDag(Dictionary<string, object?> plan)
    {
        var dag = new Dag();
        var specs = (List<Dictionary<string, object?>>)plan["tasks"]!;
        foreach (var spec in specs)
        {
            dag.AddTask(new TaskNode
            {
                Id = (string)spec["id"]!,
                Name = (string)spec["name"]!,
                Agent = (string)spec["agent"]!,
                Params = (Dictionary<string, object?>)spec["params"]!,
                Deps = ToStringList(spec.GetValueOrDefault("deps")),
                EntryGates = ToStringList(spec.GetValueOrDefault("entry_gates")),
                ExitGates = ToStringList(spec.GetValueOrDefault("exit_gates")),
                RequiresApproval = spec.GetValueOrDefault("requires_approval") is true,
                ApprovalSummary = spec.GetValueOrDefault("approval_summary") as string ?? "",
                ApprovalImpact = spec.GetValueOrDefault("approval_impact") as string ?? "",
                MaxRetries = Convert.ToInt32(spec.GetValueOrDefault("max_retries") ?? 3),
                BackoffBase = Convert.ToDouble(spec.GetValueOrDefault("backoff_base") ?? 0.1),
                FallbackAgent = spec.GetValueOrDefault("fallback_agent") as string,
                TimeoutS = Convert.ToDouble(spec.GetValueOrDefault("timeout_s") ?? 300.0),
            });
        }
        dag.Validate();
        return dag;
    }

    public static Dictionary<string, object?> RunEngine(
        Dag dag, RunContext ctx, AuditLogger audit, PolicyEngine policies,
        ApprovalManager approvals, int maxParallel = 4)
    {
        var engine = new Engine(dag, ctx, MakeAgents(), approvals: approvals,
                                policies: policies, audit: audit,
                                maxParallel: maxParallel);
        ctx.Put("_engine", engine);
        return engine.Run();
    }

    public static void Finalize(RunContext ctx, string runDir,
                                Dictionary<string, object?> manifest)
    {
        File.WriteAllText(Path.Combine(runDir, "decision_lineage.md"),
                          ctx.DecisionLineageMarkdown());
        PrintSummary(ctx, manifest, runDir);
    }

    public static void PrintSummary(RunContext ctx,
                                    Dictionary<string, object?> manifest,
                                    string runDir)
    {
        var m = (Dictionary<string, object?>)manifest["metrics"]!;
        var bar = new string('=', 70);
        Console.WriteLine("\n" + bar);
        Console.WriteLine($"SCENARIO : {ctx.Scenario}");
        Console.WriteLine($"RUN      : {ctx.RunId}");
        Console.WriteLine($"STATUS   : {manifest["status"]}");
        if (manifest.GetValueOrDefault("stop_reason") as string is string stop &&
            stop.Length > 0)
            Console.WriteLine($"STOP     : {stop[..Math.Min(200, stop.Length)]}");
        Console.WriteLine(bar);
        Console.WriteLine("Tasks:");
        var perTask = (Dictionary<string, object?>)m["per_task"]!;
        foreach (var tid in perTask.Keys.OrderBy(x => x))
        {
            var t = (Dictionary<string, object?>)perTask[tid]!;
            var mttr = t.GetValueOrDefault("mttr_s") is double mt && mt > 0
                ? $" mttr={mt}s" : "";
            Console.WriteLine(
                $"  [{t["outcome"],10}] {tid,-22} agent={t["agent"],-12} " +
                $"{Convert.ToDouble(t["duration_s"]),7:0.00}s " +
                $"retries={t["retries"]}{mttr}");
        }
        Console.WriteLine(new string('-', 70));
        Console.WriteLine(
            $"Metrics  : success_rate={m["success_rate"]} " +
            $"retries={m["total_retries"]} (freq={m["retry_frequency"]}) " +
            $"rollbacks={m["rollbacks"]} mttr={m["mttr_s"]}s " +
            $"e2e={m["end_to_end_latency_s"]}s replans={m["replans"]}");
        var approvals = ctx.Approvals;
        Console.WriteLine(
            $"Approvals: {approvals.Count} recorded " +
            $"({approvals.Count(a => a.GetValueOrDefault("verdict") as string == "approved")} approved)");
        var openAss = ctx.Assumptions.Count(a => a.Status == "open");
        Console.WriteLine(
            $"Decisions: {ctx.Decisions.Count} recorded | " +
            $"Assumptions: {ctx.Assumptions.Count} ({openAss} open)");
        Console.WriteLine("Artifacts:");
        foreach (var name in new[] { "audit.jsonl", "metrics.json",
                                     "decision_lineage.md", "manifest.json" })
            Console.WriteLine($"  - {Path.Combine(runDir, name)}");
        Console.WriteLine(bar + "\n");
    }
}
