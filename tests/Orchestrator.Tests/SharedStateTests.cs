using AgenticUrlShortener.Orchestrator;
using Xunit;

namespace AgenticUrlShortener.Orchestrator.Tests;

public sealed class SharedStateTests
{
    [Fact]
    public void ParallelContextWritersPreserveAllRecordsAndUniqueIds()
    {
        var ctx = new RunContext("parallel");
        Parallel.For(0, 400, i =>
        {
            ctx.Update<int>("count", count => count + 1);
            ctx.AppendToList("documents", $"doc-{i}");
            ctx.RecordDecision("test", $"decision-{i}", "parallel write");
            var assumption = ctx.LogAssumption("test", $"assumption-{i}");
            ctx.ResolveAssumption(assumption.Id, "confirmed", "test");
            ctx.RecordApproval(new() { ["task_id"] = $"task-{i}" });
            ctx.AddArtifact($"artifact-{i}", "data", "test", content: $"content-{i}");
            if (i % 40 == 0)
            {
                _ = ctx.ToDict();
                _ = ctx.DecisionLineageMarkdown();
            }
        });

        Assert.Equal(400, ctx.Get<int>("count"));
        Assert.Equal(400, ctx.Get<List<string>>("documents")!.Distinct().Count());
        Assert.Equal(400, ctx.Decisions.Select(d => d.Id).Distinct().Count());
        Assert.Equal(400, ctx.Assumptions.Select(a => a.Id).Distinct().Count());
        Assert.All(ctx.Assumptions, a => Assert.Equal("confirmed", a.Status));
        Assert.Equal(400, ctx.Approvals.Count);
        Assert.Equal(400, ctx.Artifacts.Count);
    }

    [Fact]
    public void ContextReadsAndWritesDetachNestedMutableData()
    {
        var ctx = new RunContext("snapshots");
        var original = new Dictionary<string, object?>
        {
            ["items"] = new List<Dictionary<string, string>> { new() { ["value"] = "original" } },
        };
        ctx.Put("nested", original);
        ((List<Dictionary<string, string>>)original["items"]!)[0]["value"] = "input mutation";
        var snapshot = ctx.Get<Dictionary<string, object?>>("nested")!;
        var items = (List<Dictionary<string, string>>)snapshot["items"]!;
        Assert.Equal("original", items[0]["value"]);
        items[0]["value"] = "read mutation";
        ctx.Store.Clear();
        Assert.Equal("original", ((List<Dictionary<string, string>>)
            ctx.Get<Dictionary<string, object?>>("nested")!["items"]!)[0]["value"]);

        var artifact = ctx.AddArtifact("out", "data", "test", metadata: original);
        artifact.Metadata.Clear();
        ctx.Artifacts["out"].Metadata.Clear();
        Assert.NotEmpty(ctx.Artifacts["out"].Metadata);
        var decision = ctx.RecordDecision("test", "decision", "reason", basedOn: new() { "source" });
        decision.BasedOn.Clear();
        Assert.Single(ctx.Decisions[0].BasedOn);
        var assumption = ctx.LogAssumption("test", "assumption");
        assumption.Status = "rejected";
        ctx.Assumptions[0].Status = "rejected";
        Assert.Equal("open", ctx.Assumptions[0].Status);

        ctx.Put("case-insensitive", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Key"] = "value",
        });
        Assert.Equal("value", ctx.Get<Dictionary<string, string>>("case-insensitive")!["KEY"]);
    }

    [Fact]
    public async Task ConcurrentAppendAndTakeDoNotLoseOrDuplicateQueuedRequests()
    {
        var ctx = new RunContext("queue");
        var drained = new List<int>();
        var producer = Task.Run(() => Parallel.For(0, 2000, i => ctx.AppendToList("requests", i)));
        while (!producer.IsCompleted)
        {
            drained.AddRange(ctx.Take<List<int>>("requests") ?? new());
            await Task.Yield();
        }
        await producer;
        drained.AddRange(ctx.Take<List<int>>("requests") ?? new());
        Assert.Equal(Enumerable.Range(0, 2000), drained.Order());
        Assert.Null(ctx.Take<List<int>>("requests"));
    }

    [Fact]
    public void ParallelMetricUpdatesAndSummariesPreserveCountersAndTaskRecords()
    {
        var metrics = new MetricsCollector();
        Parallel.For(0, 400, i =>
        {
            var id = $"task-{i}";
            metrics.RegisterTask(id, "test");
            metrics.RecordTask(id, 2, 1, 1.5, "succeeded");
            metrics.IncrementApprovalsRequested();
            metrics.IncrementPolicyDenials();
            metrics.IncrementReplans();
            if (i % 40 == 0) _ = metrics.Summary();
        });
        var summary = metrics.Summary();
        Assert.Equal(400, summary["tasks_succeeded"]);
        Assert.Equal(800, summary["total_attempts"]);
        Assert.Equal(400, summary["total_retries"]);
        Assert.Equal(400, metrics.ApprovalsRequested);
        Assert.Equal(400, metrics.PolicyDenials);
        Assert.Equal(400, metrics.Replans);
        metrics.Tasks["task-0"].Outcome = "failed";
        Assert.Equal("succeeded", metrics.Tasks["task-0"].Outcome);
    }

    [Fact]
    public void ParallelReplanSnapshotsObserveOwnOutputChanges()
    {
        var ctx = new RunContext("replan");
        var replan = new ReplanManager(new Dag(), ctx);
        Parallel.For(0, 300, i =>
        {
            var taskId = $"task-{i}";
            var artifact = $"out-{i}";
            ctx.AddArtifact(artifact, "data", taskId, content: "before");
            replan.SnapshotOutputs(taskId);
            ctx.AddArtifact(artifact, "data", taskId, content: "after");
            var change = replan.DetectOutputChange(taskId);
            Assert.NotNull(change);
            Assert.Equal(artifact, change.Artifact);
        });
        Assert.Equal(300, replan.CheckWaveForDrift(
            Enumerable.Range(0, 300).Select(i => $"task-{i}")).Count);
    }

    [Fact]
    public void ParallelPolicyDenialsPreserveEveryViolation()
    {
        var ctx = new RunContext("policy");
        var policies = new PolicyEngine(new PolicyRule[]
        {
            (action, _) => new() { Allowed = false, Rule = "test", Reason = action.Target },
        });
        Parallel.For(0, 400, i => Assert.Throws<PolicyViolationException>(() =>
            policies.Evaluate(new() { Kind = "test", Target = $"reason-{i}" }, ctx)));
        Assert.Equal(400, ctx.Get<List<string>>("policy_violations")!.Distinct().Count());
    }

    [Fact]
    public void AuditEntriesAndReplanHistoryAreDetachedSnapshots()
    {
        var audit = new AuditLogger("snapshots");
        var input = new List<string> { "original" };
        var returned = audit.Log("test", details: new() { ["items"] = input });
        input.Clear();
        ((List<string>)returned["items"]!).Clear();
        ((List<string>)audit.Events[0]["items"]!).Clear();
        Assert.Equal("original", Assert.Single((List<string>)audit.Events[0]["items"]!));

        var replan = new ReplanManager(new Dag(), new RunContext("snapshots"));
        var report = replan.RequestReplan("test", changedTaskIds: new() { "source" });
        ((List<string>)report["changed"]!).Clear();
        ((List<string>)replan.History[0]["changed"]!).Clear();
        Assert.Equal("source", Assert.Single((List<string>)replan.History[0]["changed"]!));
    }

    [Fact]
    public void CanceledApprovalDoesNotRecordAnApproval()
    {
        var ctx = new RunContext("approval");
        using var cancellation = new CancellationTokenSource();
        var approvals = new CancelingApprover(cancellation);
        Assert.Throws<OperationCanceledException>(() => approvals.Request(
            ctx, "task", "summary", "impact", "test", cancellationToken: cancellation.Token));
        Assert.Empty(ctx.Approvals);
    }

    private sealed class CancelingApprover(CancellationTokenSource cancellation) : ApprovalManager
    {
        protected override ApprovalVerdict PromptHuman(ApprovalRequest request)
        {
            cancellation.Cancel();
            return new() { Approved = true, Actor = "test", Reason = "test", Timestamp = RunContext.NowIso() };
        }
    }
}
