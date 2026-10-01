using AgenticUrlShortener.Orchestrator;
using Xunit;
using TaskStatus = AgenticUrlShortener.Orchestrator.TaskStatus;

namespace AgenticUrlShortener.Orchestrator.Tests;

public sealed class EngineCancellationTests
{
    private sealed class CallbackAgent(Func<RunContext, TaskNode, CancellationToken, AgentResult> callback) : IAgent
    {
        public string Name => "callback";
        public AgentResult Run(RunContext ctx, TaskNode task, CancellationToken cancellationToken = default) =>
            callback(ctx, task, cancellationToken);
    }

    private static Engine CreateEngine(Dag dag, Dictionary<string, IAgent> agents, int maxParallel = 1)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cancel-engine-{Guid.NewGuid():N}");
        return new Engine(dag, new RunContext("cancellation", runDir: directory), agents, maxParallel: maxParallel);
    }

    [Fact]
    public async Task TimeoutKeepsSlotUntilAgentExitsAndRejectsLateSuccess()
    {
        using var timedOut = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var active = 0;
        var starts = 0;
        var overlapped = 0;
        var agent = new CallbackAgent((_, _, token) =>
        {
            Interlocked.Increment(ref starts);
            if (Interlocked.Increment(ref active) > 1) Interlocked.Exchange(ref overlapped, 1);
            try
            {
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                timedOut.Set();
                // Deliberately ignore cancellation while simulating cleanup/non-cooperative work.
                Assert.True(finish.Wait(TimeSpan.FromSeconds(10)));
                return new AgentResult { Success = true };
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var dag = new Dag();
        foreach (var id in new[] { "a", "b" })
            dag.AddTask(new TaskNode { Id = id, Name = id, Agent = "callback", TimeoutS = 0.1 });
        var engine = CreateEngine(dag, new() { ["callback"] = agent });
        var running = Task.Run(() => engine.Run());
        try
        {
            Assert.True(timedOut.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(running.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(1, Volatile.Read(ref active));
        }
        finally { finish.Set(); }
        var manifest = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", manifest["status"]);
        Assert.Equal(0, overlapped);
        Assert.Equal(0, active);
        Assert.Equal(2, starts);
        Assert.All(dag.Tasks.Values, t =>
        {
            Assert.Equal(TaskStatus.Failed, t.Status);
            Assert.Contains("timed out", t.Error);
        });
        Assert.DoesNotContain(engine.Audit.Events, e => e.Event == "task_succeeded");
        Assert.Equal(2, engine.Metrics.Summary()["tasks_failed"]);
    }

    [Fact]
    public async Task TimeoutCancelsBackoffWithoutRetryOrFallback()
    {
        var attempts = 0;
        var fallbacks = 0;
        var dag = new Dag();
        dag.AddTask(new TaskNode
        {
            Id = "a", Name = "a", Agent = "primary", FallbackAgent = "fallback",
            TimeoutS = 0.5, MaxRetries = 3, BackoffBase = 30,
        });
        var engine = CreateEngine(dag, new()
        {
            ["primary"] = new CallbackAgent((_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("retryable failure");
            }),
            ["fallback"] = new CallbackAgent((_, _, _) =>
            {
                Interlocked.Increment(ref fallbacks);
                return new AgentResult { Success = true };
            }),
        });
        var manifest = await Task.Run(() => engine.Run()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", manifest["status"]);
        Assert.Equal(1, attempts);
        Assert.Equal(0, fallbacks);
        Assert.Contains("timed out", dag.Tasks["a"].Error);
        Assert.DoesNotContain(engine.Audit.Events, e => e.Event == "fallback_invoked");
    }

    [Fact]
    public async Task TimeoutRunsCompensationAfterAgentStopsAndSkipsDependent()
    {
        var active = false;
        var cleaned = false;
        var childRan = false;
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "a", Name = "a", Agent = "primary", TimeoutS = 0.1 });
        dag.AddTask(new TaskNode { Id = "b", Name = "b", Agent = "child", Deps = new() { "a" } });
        Engine? engine = null;
        engine = CreateEngine(dag, new()
        {
            ["primary"] = new CallbackAgent((_, task, token) =>
            {
                active = true;
                engine!.RegisterRollback(task.Id, () => cleaned = !active);
                try
                {
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                    return new AgentResult { Success = true };
                }
                finally { active = false; }
            }),
            ["child"] = new CallbackAgent((_, _, _) =>
            {
                childRan = true;
                return new AgentResult { Success = true };
            }),
        });
        Assert.Equal("failed", (await Task.Run(() => engine.Run()).WaitAsync(TimeSpan.FromSeconds(10)))["status"]);
        Assert.True(cleaned);
        Assert.False(childRan);
        Assert.Equal(TaskStatus.RolledBack, dag.Tasks["a"].Status);
        Assert.Equal(TaskStatus.Skipped, dag.Tasks["b"].Status);
        Assert.Equal(1, engine.Metrics.Summary()["rollbacks"]);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void FallbackAuditIsRecordedOnlyWhenFallbackExecutes(bool primarySucceeds, int expectedEvents)
    {
        var dag = new Dag();
        dag.AddTask(new TaskNode { Id = "a", Name = "a", Agent = "primary", FallbackAgent = "fallback", MaxRetries = 1 });
        var fallbackRuns = 0;
        var engine = CreateEngine(dag, new()
        {
            ["primary"] = new CallbackAgent((_, _, _) => new AgentResult { Success = primarySucceeds }),
            ["fallback"] = new CallbackAgent((_, _, _) =>
            {
                fallbackRuns++;
                return new AgentResult { Success = true };
            }),
        });
        Assert.Equal("succeeded", engine.Run()["status"]);
        Assert.Equal(expectedEvents, fallbackRuns);
        Assert.Equal(expectedEvents, engine.Audit.Events.Count(e => e.Event == "fallback_invoked"));
    }

    [Fact]
    public async Task CancellationDuringRollbackDoesNotRepeatCompensation()
    {
        var cleanups = 0;
        var dag = new Dag();
        dag.AddTask(new TaskNode
        {
            Id = "a", Name = "a", Agent = "primary", FallbackAgent = "fallback", MaxRetries = 1, TimeoutS = 0.2,
        });
        Engine? engine = null;
        engine = CreateEngine(dag, new()
        {
            ["primary"] = new CallbackAgent((_, task, token) =>
            {
                engine!.RegisterRollback(task.Id, () =>
                {
                    Interlocked.Increment(ref cleanups);
                    Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                });
                return new AgentResult { Success = false };
            }),
            ["fallback"] = new CallbackAgent((_, _, _) => new AgentResult { Success = true }),
        });
        Assert.Equal("failed", (await Task.Run(() => engine.Run()).WaitAsync(TimeSpan.FromSeconds(10)))["status"]);
        Assert.Equal(1, cleanups);
        Assert.Equal(TaskStatus.RolledBack, dag.Tasks["a"].Status);
        Assert.True(engine.Metrics.Tasks["a"].FallbackUsed);
        Assert.Single(engine.Audit.Events, e => e.Event == "rollback_completed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDoesNotMaskFatalPolicyExceptions(bool inFallback)
    {
        using var stop = new CancellationTokenSource();
        object? Denied()
        {
            stop.Cancel();
            throw new PolicyViolationException("denied concurrently with timeout");
        }
        Assert.Throws<PolicyViolationException>(() => Retry.ExecuteWithRetry(
            inFallback ? () => throw new InvalidOperationException("primary failed") : Denied,
            maxAttempts: 1,
            fallback: inFallback ? Denied : null,
            cancellationToken: stop.Token,
            fatalExceptions: new[] { typeof(PolicyViolationException) }));
    }
}
