// Unit tests for the agentic orchestration framework.
//
// Covers: DAG ordering, parallel fan-out with join barriers, gate blocking,
// bounded retries, fallback, rollback on exhaustion, policy-denial safe-stop,
// approval checkpoints, re-plan invalidation, and metrics aggregation.

using AgenticUrlShortener.Orchestrator;
using Xunit;

namespace AgenticUrlShortener.Orchestrator.Tests;

// ---------------------------------------------------------------------------
// Stub agents
// ---------------------------------------------------------------------------
file sealed class OkAgent(List<string> log) : IAgent
{
    public string Name => "ok";
    public AgentResult Run(RunContext ctx, TaskNode task)
    {
        lock (log) log.Add(task.Id);
        return new AgentResult { Success = true, Notes = "ok" };
    }
}

file sealed class FlakyAgent(int failTimes, List<(string, int)> log) : IAgent
{
    public string Name => "flaky";
    private int _calls;
    public int Calls => _calls;
    public AgentResult Run(RunContext ctx, TaskNode task)
    {
        _calls++;
        log.Add((task.Id, _calls));
        if (_calls <= failTimes)
            throw new InvalidOperationException($"boom {_calls}");
        return new AgentResult { Success = true, Notes = "recovered" };
    }
}

file sealed class SleepAgent(double seconds, List<(string, string, double)> events) : IAgent
{
    public string Name => "sleep";
    public AgentResult Run(RunContext ctx, TaskNode task)
    {
        lock (events) events.Add((task.Id, "start", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        lock (events) events.Add((task.Id, "end", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
        return new AgentResult { Success = true, Notes = "slept" };
    }
}

file sealed class Denier() : ApprovalManager(auto: false)
{
    protected override ApprovalVerdict PromptHuman(ApprovalRequest req) =>
        new() { Approved = false, Actor = "human", Timestamp = RunContext.NowIso(),
                Reason = "nope" };
}

file sealed class ResultAgent(Func<AgentResult> run) : IAgent
{
    public string Name => "result";
    public AgentResult Run(RunContext ctx, TaskNode task) => run();
}

public sealed class OrchestratorTests
{
    [Fact]
    public void UnsuccessfulResultRetriesAndRecovers()
    {
        var attempts = 0;
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "t", Name = "t", Agent = "result", MaxRetries = 2, BackoffBase = 0 });
        var (engine, _) = MakeEngine(dag, new()
        {
            ["result"] = new ResultAgent(() => new AgentResult { Success = ++attempts == 2, Notes = "try again" }),
        });
        Assert.Equal("succeeded", engine.Run()["status"]);
        Assert.Equal(2, attempts);
        Assert.Equal(1, engine.Metrics.Summary()["total_retries"]);
    }

    [Fact]
    public void UnsuccessfulResultExhaustionRollsBackAndSkipsDependents()
    {
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "t", Name = "t", Agent = "result", MaxRetries = 2, BackoffBase = 0 });
        dag.AddTask(new TaskNode { Id = "after", Name = "after", Agent = "result", Deps = new() { "t" } });
        var (engine, _) = MakeEngine(dag, new()
        {
            ["result"] = new ResultAgent(() => new AgentResult { Success = false, Notes = "failed result" }),
        });
        var rolledBack = false;
        engine.RegisterRollback("t", () => rolledBack = true);
        Assert.Equal("failed", engine.Run()["status"]);
        Assert.True(rolledBack);
        Assert.Equal(2, dag.Tasks["t"].Attempts);
        Assert.Equal(TaskStatus.RolledBack, dag.Tasks["t"].Status);
        Assert.Equal(TaskStatus.Skipped, dag.Tasks["after"].Status);
    }

    [Fact]
    public void FallbackPolicyViolationSafeStopsRun()
    {
        var dag = new Dag();
        dag.AddTask(new TaskNode
        {
            Id = "t", Name = "t", Agent = "result", FallbackAgent = "denied", MaxRetries = 1,
        });
        var (engine, _) = MakeEngine(dag, new()
        {
            ["result"] = new ResultAgent(() => new AgentResult { Success = false }),
            ["denied"] = new ResultAgent(() => throw new PolicyViolationException("fallback denied")),
        });
        Assert.Equal("stopped", engine.Run()["status"]);
        Assert.Equal(1, engine.Metrics.PolicyDenials);
        Assert.Contains(engine.Audit.Events, e => (string)e["event"]! == "safe_stop");
    }

    private static (Engine engine, RunContext ctx) MakeEngine(
        Dag dag, Dictionary<string, IAgent> agents,
        int maxParallel = 4, ApprovalManager? approvals = null)
    {
        // Hermetic run dir: audit/metrics must never land in the repo root.
        var runDir = Path.Combine(Path.GetTempPath(),
            $"engine-test-{Guid.NewGuid():N}");
        var ctx = new RunContext("test", Guid.NewGuid().ToString("N"), runDir);
        ctx.Put("allowed_write_roots", new List<string> { Path.GetTempPath() });
        var audit = new AuditLogger(ctx.RunId, runDir);
        var engine = new Engine(dag, ctx, agents, audit: audit,
            approvals: approvals ?? new ApprovalManager(auto: true),
            policies: new PolicyEngine(), maxParallel: maxParallel);
        return (engine, ctx);
    }

    private static Dag DiamondDag()
    {
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "a", Name = "a", Agent = "ok" });
        dag.AddTask(new TaskNode { Id = "b", Name = "b", Agent = "ok", Deps = new List<string> { "a" } });
        dag.AddTask(new TaskNode { Id = "c", Name = "c", Agent = "ok", Deps = new List<string> { "a" } });
        dag.AddTask(new TaskNode { Id = "d", Name = "d", Agent = "ok", Deps = new List<string> { "b", "c" } });
        return dag;
    }

    private static string? Detail(IReadOnlyList<Dictionary<string, object?>> events,
                                  string eventName, string detailKey, bool? passed = null)
    {
        foreach (var e in events)
        {
            if ((string)e["event"]! != eventName) continue;
            // Audit entries flatten details into the top-level dict.
            if (passed is not null &&
                !Equals(e.GetValueOrDefault("passed"), passed)) continue;
            return e.GetValueOrDefault(detailKey)?.ToString();
        }
        return null;
    }

    // ---------------------------------------------------------------------
    [Fact]
    public void DagTopologicalOrderAndJoinBarrier()
    {
        var log = new List<string>();
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(log) };
        var (engine, _) = MakeEngine(DiamondDag(), agents);
        var manifest = engine.Run();
        Assert.Equal("succeeded", manifest["status"]);
        Assert.Equal("a", log[0]);
        Assert.Equal("d", log[^1]);
        Assert.Equal(new HashSet<string> { "b", "c" }, new HashSet<string>(log[1..3]));
    }

    [Fact]
    public void ParallelWaveExecutionOverlaps()
    {
        var events = new List<(string, string, double)>();
        var agents = new Dictionary<string, IAgent>
            { ["sleep"] = new SleepAgent(0.3, events) };
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "a", Name = "a", Agent = "sleep" });
        dag.AddTask(new TaskNode { Id = "b", Name = "b", Agent = "sleep", Deps = new List<string> { "a" } });
        dag.AddTask(new TaskNode { Id = "c", Name = "c", Agent = "sleep", Deps = new List<string> { "a" } });
        var (engine, _) = MakeEngine(dag, agents, maxParallel: 2);
        Assert.Equal("succeeded", engine.Run()["status"]);
        var starts = events.Where(e => e.Item2 == "start").ToDictionary(e => e.Item1, e => e.Item3);
        var ends = events.Where(e => e.Item2 == "end").ToDictionary(e => e.Item1, e => e.Item3);
        // b and c overlapped: each started before the other finished
        Assert.True(starts["b"] < ends["c"] && starts["c"] < ends["b"]);
        // Verify the barrier directly, without a machine-dependent elapsed-time limit.
        Assert.True(ends["a"] <= starts["b"] && ends["a"] <= starts["c"]);
    }

    [Fact]
    public void EntryGateFailureBlocksTaskAndSkipsDependents()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t1", Name = "t1", Agent = "ok",
              EntryGates = new List<string> { "plan_exists" } });  // no plan in ctx
        dag.AddTask(new TaskNode
            { Id = "t2", Name = "t2", Agent = "ok", Deps = new List<string> { "t1" } });
        var (engine, _) = MakeEngine(dag, agents);
        var manifest = engine.Run();
        Assert.Equal(TaskStatus.Failed, dag.Tasks["t1"].Status);
        Assert.Contains("entry gate", dag.Tasks["t1"].Error ?? "");
        Assert.Equal(TaskStatus.Skipped, dag.Tasks["t2"].Status);
        Assert.Equal("failed", manifest["status"]);
        Assert.Contains(engine.Audit.Events, e =>
            (string)e["event"]! == "gate_entry" &&
            Equals(e.GetValueOrDefault("passed"), false));
    }

    [Fact]
    public void BoundedRetryRecoversAndRecordsMetrics()
    {
        var log = new List<(string, int)>();
        var agents = new Dictionary<string, IAgent>
            { ["flaky"] = new FlakyAgent(failTimes: 2, log) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t", Name = "t", Agent = "flaky", MaxRetries = 3, BackoffBase = 0.0 });
        var (engine, _) = MakeEngine(dag, agents);
        var manifest = engine.Run();
        Assert.Equal("succeeded", manifest["status"]);
        Assert.Equal(3, log.Count);  // 1 initial + 2 retries
        var summary = engine.Metrics.Summary();
        Assert.Equal(2, summary["total_retries"]);
        Assert.NotNull(summary["mttr_s"]);
        Assert.Equal(2, engine.Audit.Events.Count(
            e => (string)e["event"]! == "task_retried"));
    }

    [Fact]
    public void RetryExhaustionRunsRollbackHooks()
    {
        var cleaned = new List<string>();
        var agents = new Dictionary<string, IAgent>
            { ["flaky"] = new FlakyAgent(failTimes: 99, new List<(string, int)>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t", Name = "t", Agent = "flaky", MaxRetries = 2, BackoffBase = 0.0 });
        var (engine, _) = MakeEngine(dag, agents);
        engine.RegisterRollback("t", () => cleaned.Add("t"));
        var manifest = engine.Run();
        Assert.Equal(TaskStatus.RolledBack, dag.Tasks["t"].Status);
        Assert.Equal(new List<string> { "t" }, cleaned);
        var metrics = (Dictionary<string, object?>)manifest["metrics"]!;
        Assert.Equal(1, metrics["rollbacks"]);
        Assert.Contains(engine.Audit.Events,
            e => (string)e["event"]! == "rollback_completed");
    }

    [Fact]
    public void FallbackAgentUsedOnExhaustion()
    {
        var log = new List<string>();
        var agents = new Dictionary<string, IAgent>
        {
            ["flaky"] = new FlakyAgent(failTimes: 99, new List<(string, int)>()),
            ["ok"] = new OkAgent(log),
        };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t", Name = "t", Agent = "flaky", FallbackAgent = "ok",
              MaxRetries = 2, BackoffBase = 0.0 });
        var (engine, _) = MakeEngine(dag, agents);
        var manifest = engine.Run();
        Assert.Equal("succeeded", manifest["status"]);
        Assert.Equal(new List<string> { "t" }, log);  // fallback executed the task
        var perTask = (Dictionary<string, object?>)engine.Metrics.Summary()["per_task"]!;
        var t = (Dictionary<string, object?>)perTask["t"]!;
        Assert.True((bool)t["fallback_used"]!);
    }

    [Fact]
    public void PolicyDenialSafeStopsRun()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
        {
            Id = "t1", Name = "t1", Agent = "ok",
            Params = new Dictionary<string, object?>
            {
                ["policy_action"] = new PolicyAction
                {
                    Kind = "write_file", Target = "/tmp/leak.cs",
                    Payload = "api_key = \"sk-live-1234567890abcdef\"",
                },
            },
        });
        dag.AddTask(new TaskNode
            { Id = "t2", Name = "t2", Agent = "ok", Deps = new List<string> { "t1" } });
        var (engine, _) = MakeEngine(dag, agents);
        var manifest = engine.Run();
        Assert.Equal("stopped", manifest["status"]);
        Assert.Equal(TaskStatus.Skipped, dag.Tasks["t2"].Status);
        Assert.True(engine.Metrics.PolicyDenials >= 1);
        var denial = engine.Audit.Events.FirstOrDefault(e =>
            (string)e["event"]! == "policy_evaluated" &&
            Equals(e.GetValueOrDefault("allowed"), false));
        Assert.NotNull(denial);
        Assert.Equal("no_secrets_in_code", denial.GetValueOrDefault("rule")?.ToString());
        Assert.Contains(engine.Audit.Events, e => (string)e["event"]! == "safe_stop");
    }

    [Fact]
    public void ApprovalCheckpointRecorded()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t", Name = "t", Agent = "ok", RequiresApproval = true,
              ApprovalSummary = "do the thing", ApprovalImpact = "test impact" });
        var (engine, ctx) = MakeEngine(dag, agents);
        var manifest = engine.Run();
        Assert.Equal("succeeded", manifest["status"]);
        Assert.Single(ctx.Approvals);
        Assert.Equal("approved", ctx.Approvals[0]["verdict"]);
        Assert.Equal("auto-approver", ctx.Approvals[0]["actor"]);
    }

    [Fact]
    public void ApprovalDeniedSafeStops()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode
            { Id = "t", Name = "t", Agent = "ok", RequiresApproval = true });
        var (engine, ctx) = MakeEngine(dag, agents, approvals: new Denier());
        var manifest = engine.Run();
        Assert.Equal("stopped", manifest["status"]);
        Assert.Throws<ApprovalDeniedException>(() =>
            new Denier().Request(ctx, "x", "s", "i", requestedBy: "tester", audit: null));
    }

    [Fact]
    public void ReplanInvalidatesDownstreamAndReruns()
    {
        var log = new List<string>();
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(log) };
        var (engine, _) = MakeEngine(DiamondDag(), agents);
        Assert.Equal("succeeded", engine.Run()["status"]);
        Assert.Equal("a", log[0]);
        Assert.Equal("d", log[^1]);
        Assert.Equal(new HashSet<string> { "b", "c" }, new HashSet<string>(log[1..3]));

        // Upstream output of "a" changed -> re-plan invalidates a + dependents.
        var report = engine.Replan.RequestReplan(
            reason: "upstream output changed", changedTaskIds: new List<string> { "a" });
        var invalidated = (List<string>)report["invalidated"]!;
        Assert.Equal(new HashSet<string> { "a", "b", "c", "d" },
                     new HashSet<string>(invalidated));
        Assert.Equal("succeeded", engine.Run()["status"]);
        Assert.Equal(2, log.Count(x => x == "a"));
        Assert.Equal(2, log.Count(x => x == "d"));
        Assert.Equal(1, engine.Metrics.Replans);
        var reason = Detail(engine.Audit.Events, "replan_triggered", "reason");
        Assert.Equal("upstream output changed", reason);
    }

    [Fact]
    public void ReplanDetectsOutputDriftViaHash()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "a", Name = "a", Agent = "ok" });
        var (engine, ctx) = MakeEngine(dag, agents);
        engine.Run();
        // Simulate a re-run of "a" producing different output.
        ctx.AddArtifact("out_a", kind: "data", producedBy: "ok:a", content: "v1");
        engine.Replan.SnapshotOutputs("a");
        ctx.AddArtifact("out_a", kind: "data", producedBy: "ok:a", content: "v2");
        var ev = engine.Replan.DetectOutputChange("a");
        Assert.NotNull(ev);
        Assert.Equal("out_a", ev.Artifact);
        Assert.NotEqual(ev.OldHash, ev.NewHash);
    }

    [Fact]
    public void MetricsSummaryShape()
    {
        var agents = new Dictionary<string, IAgent> { ["ok"] = new OkAgent(new List<string>()) };
        var (engine, _) = MakeEngine(DiamondDag(), agents);
        engine.Run();
        var s = engine.Metrics.Summary();
        foreach (var key in new[] { "success_rate", "total_retries", "retry_frequency",
                                    "rollbacks", "rollback_frequency", "mttr_s",
                                    "end_to_end_latency_s", "replans", "per_task" })
            Assert.True(s.ContainsKey(key), $"missing metrics key: {key}");
        Assert.Equal(1.0, s["success_rate"]);
        Assert.Equal(4, s["tasks_succeeded"]);
    }
}
