// Orchestration engine: executes the task DAG with governance.
//
// Execution model per wave (synchronization barrier):
//   1. select ready tasks whose dependencies all succeeded
//   2. entry gates -> human approval (if required) -> policy check
//   3. agent execution wrapped in bounded retries + backoff; fallback agent on
//      exhaustion; rollback hooks for compensation
//   4. exit gates -> output snapshots for drift detection
//   5. join: the wave completes before the next wave starts
//   6. re-plan check: output drift or explicit replan requests mutate the DAG
//      and invalidate stale nodes (governance re-applied on re-run)
//
// Fatal failures (policy violation, denied approval) trigger a safe-stop: the
// run halts, remaining work is skipped, and the audit trail shows exactly why.

using System.Diagnostics;

namespace AgenticUrlShortener.Orchestrator;

/// <summary>Wraps errors that must safe-stop the whole run.</summary>
public sealed class FatalTaskException : Exception
{
    public FatalTaskException(string message) : base(message) { }
}

public sealed class Engine
{
    private readonly Dag _dag;
    private readonly RunContext _ctx;
    private readonly Dictionary<string, IAgent> _agents;
    private readonly GateRegistry _gates;
    private readonly ApprovalManager _approvals;
    private readonly PolicyEngine _policies;
    private readonly AuditLogger _audit;
    private readonly MetricsCollector _metrics;
    private readonly ReplanManager _replan;
    private readonly int _maxParallel;
    private readonly bool _stopOnPolicyViolation;
    private sealed class RollbackRegistration(Action hook)
    {
        private int _invoked;
        public void Reset() => Volatile.Write(ref _invoked, 0);
        public void Run()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0) hook();
        }
    }

    private readonly Dictionary<string, List<RollbackRegistration>> _rollbackHooks = new();
    private readonly object _rollbackLock = new();
    private readonly object _stopLock = new();

    private volatile bool _stopRequested;
    private string _stopReason = string.Empty;

    public Engine(Dag dag, RunContext ctx, Dictionary<string, IAgent> agents,
                  GateRegistry? gates = null, ApprovalManager? approvals = null,
                  PolicyEngine? policies = null, AuditLogger? audit = null,
                  MetricsCollector? metrics = null, int maxParallel = 4,
                  bool stopOnPolicyViolation = true)
    {
        _dag = dag;
        _ctx = ctx;
        _agents = agents;
        _gates = gates ?? BuiltinGates.DefaultRegistry();
        _approvals = approvals ?? new ApprovalManager(auto: true);
        _policies = policies ?? new PolicyEngine();
        _audit = audit ?? new AuditLogger(ctx.RunId, ctx.RunDir);
        _metrics = metrics ?? new MetricsCollector();
        _replan = new ReplanManager(dag, ctx, _audit, _metrics);
        _maxParallel = Math.Max(1, maxParallel);
        _stopOnPolicyViolation = stopOnPolicyViolation;
        foreach (var (tid, task) in dag.Tasks)
            _metrics.RegisterTask(tid, task.Agent);
    }

    public ReplanManager Replan => _replan;
    public MetricsCollector Metrics => _metrics;
    public AuditLogger Audit => _audit;

    // ---- rollback hook registry --------------------------------------------
    public void RegisterRollback(string taskId, Action hook)
    {
        lock (_rollbackLock)
        {
            if (!_rollbackHooks.TryGetValue(taskId, out var list))
            {
                list = new List<RollbackRegistration>();
                _rollbackHooks[taskId] = list;
            }
            list.Add(new RollbackRegistration(hook));
        }
    }

    private List<Action> RollbackHooks(string taskId)
    {
        lock (_rollbackLock)
            return _rollbackHooks.TryGetValue(taskId, out var hooks)
                ? hooks.Select<RollbackRegistration, Action>(hook => hook.Run).ToList() : new();
    }

    private void ResetRollbackHooks(string taskId)
    {
        lock (_rollbackLock)
            if (_rollbackHooks.TryGetValue(taskId, out var hooks))
                foreach (var hook in hooks) hook.Reset();
    }

    // ---- main loop ------------------------------------------------------------
    public Dictionary<string, object?> Run()
    {
        _dag.Validate();
        _audit.RunStarted(_ctx.Scenario, new Dictionary<string, object?>
        {
            ["tasks"] = _dag.Tasks.Keys.OrderBy(k => k).ToList(),
        });
        try
        {
            while (!_dag.IsComplete() && !_stopRequested)
            {
                var wave = _dag.ReadyWave();
                if (wave.Count == 0)
                {
                    DrainBlocked();
                    if (!_dag.IsComplete())
                    {
                        // No runnable wave and work remains: deadlock.
                        foreach (var t in _dag.Tasks.Values.Where(t => t.Status == TaskStatus.Pending))
                        {
                            t.Status = TaskStatus.Failed;
                            t.Error = "deadlock: no runnable wave";
                            _audit.TaskFailed(t.Id, t.Error, t.Attempts);
                        }
                    }
                    break;
                }
                RunWave(wave);
                ProcessReplanRequests();
                DrainBlocked();
                if (_stopRequested) break;
            }
        }
        catch (Exception exc) // never lose the audit trail
        {
            _stopReason = $"engine error: {exc}";
            _stopRequested = true;
            _audit.Log("engine_error", details: new Dictionary<string, object?> { ["error"] = exc.ToString() });
        }

        // anything still pending after a stop is skipped, not silently dropped
        foreach (var t in _dag.Tasks.Values.Where(t => t.Status == TaskStatus.Pending))
        {
            t.Status = TaskStatus.Skipped;
            t.Error = string.IsNullOrEmpty(_stopReason) ? "run stopped" : _stopReason;
            _audit.TaskSkipped(t.Id, t.Error);
        }

        var status = FinalStatus();
        var summary = _metrics.Summary();
        var manifest = new Dictionary<string, object?>
        {
            ["run_id"] = _ctx.RunId,
            ["scenario"] = _ctx.Scenario,
            ["status"] = status,
            ["stop_reason"] = _stopReason,
            ["task_statuses"] = _dag.Tasks.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Status),
            ["metrics"] = summary,
            ["audit_events"] = _audit.EventCounts(),
            ["replan_history"] = _replan.History,
        };
        _audit.WriteManifest(manifest);
        var finishedDetails = summary
            .Where(kv => kv.Key != "per_task")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _audit.RunFinished(status, finishedDetails);
        _metrics.Write(_ctx.RunDir ?? ".");
        return manifest;
    }

    // ---- wave execution (parallel fan-out + join barrier) ----------------------
    private sealed class ExecutionState
    {
        public bool FallbackUsed { get; set; }
        public bool RollbackAudited { get; set; }
    }

    private void TimeoutTask(TaskNode task, double startedAt, ExecutionState execution)
    {
        // The agent has exited before compensation runs; it cannot race cleanup.
        var hooks = RollbackHooks(task.Id);
        foreach (var hook in hooks)
        {
            try { hook(); }
            catch (Exception exc)
            {
                _audit.Rollback(task.Id, "failed", exc.Message);
            }
        }
        if (hooks.Count > 0 && !execution.RollbackAudited)
            _audit.Rollback(task.Id, "completed", "timeout compensation hooks ran");
        FinishTask(task, false, startedAt, $"task timed out after {task.TimeoutS}s", report: new RetryReport
        {
            Succeeded = false, Attempts = task.Attempts, Retries = Math.Max(0, task.Attempts - 1),
            FallbackUsed = execution.FallbackUsed, FallbackSucceeded = false, RollbackRun = hooks.Count > 0,
            TotalDurationS = NowMonotonic() - startedAt,
        });
        _audit.TaskFailed(task.Id, task.Error, task.Attempts);
    }

    private void WrapperError(TaskNode task, Exception exc, double startedAt)
    {
        FinishTask(task, false, startedAt, $"engine wrapper error: {exc.Message}");
        _audit.TaskFailed(task.Id, task.Error, task.Attempts);
    }

    private void RunWave(List<TaskNode> wave)
    {
        _audit.Log("wave_started", details: new Dictionary<string, object?>
        {
            ["task_ids"] = wave.Select(t => t.Id).ToList(),
            ["size"] = wave.Count,
        });

        var dop = Math.Max(1, Math.Min(_maxParallel, wave.Count));
        using var semaphore = new SemaphoreSlim(dop, dop);
        // Each wave item gets a dedicated thread (LongRunning): agent work is
        // synchronous and blocking, and the shared thread pool may be slow to
        // inject workers (or starved by the test runner), which would
        // serialize the wave. The semaphore bounds actual concurrency to dop.
        var running = wave.Select(task => Task.Factory.StartNew(() =>
        {
            semaphore.Wait();
            var startedAt = NowMonotonic();
            var execution = new ExecutionState();
            try
            {
                if (_stopRequested)
                {
                    task.Status = TaskStatus.Skipped;
                    task.Error = "run stopped before task started";
                    _audit.TaskSkipped(task.Id, task.Error);
                    _metrics.RecordTask(task.Id, 0, 0, 0, "skipped");
                    return;
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(task.TimeoutS));
                try
                {
                    // Keep the slot and join barrier until the agent actually exits.
                    // In-process agents must cooperate; cancellation never detaches work.
                    ExecuteTask(task, cts.Token, execution);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    TimeoutTask(task, startedAt, execution);
                }
                catch (Exception exc) // defensive
                {
                    WrapperError(task, exc, startedAt);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(running);

        // join barrier reached: detect output drift before the next wave
        var succeeded = wave.Where(t => t.Status == TaskStatus.Succeeded).Select(t => t.Id).ToList();
        var drift = _replan.CheckWaveForDrift(succeeded);
        if (drift.Count > 0)
        {
            _replan.RequestReplan(
                reason: string.Join("; ", drift.Select(e => e.Reason)),
                changedTaskIds: drift.Select(e => e.SourceTaskId).ToList(),
                events: drift);
        }
        _audit.Log("wave_finished", details: new Dictionary<string, object?>
        {
            ["task_ids"] = wave.Select(t => t.Id).ToList(),
            ["statuses"] = wave.ToDictionary(t => t.Id, t => (object?)t.Status),
        });
    }

    // ---- single task execution --------------------------------------------------
    private void ExecuteTask(TaskNode task, CancellationToken cancellationToken, ExecutionState execution)
    {
        var t0 = NowMonotonic();
        ResetRollbackHooks(task.Id);
        task.Status = TaskStatus.Running;
        _audit.TaskStarted(task.Id, task.Agent);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. entry gates (preconditions)
        foreach (var res in _gates.Evaluate(task.EntryGates, "entry", _ctx, task.Params))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _audit.Gate(task.Id, res.Gate, "entry", res.Passed, res.Reason);
            if (!res.Passed)
            {
                FinishTask(task, false, t0, $"entry gate '{res.Gate}' failed: {res.Reason}", fatal: false);
                return;
            }
        }

        // 2. human approval checkpoint for high-impact actions
        if (task.RequiresApproval)
        {
            _metrics.IncrementApprovalsRequested();
            _audit.Log("approval_requested", taskId: task.Id,
                       details: new Dictionary<string, object?> { ["summary"] = task.ApprovalSummary });
            try
            {
                _approvals.Request(_ctx, task.Id,
                    string.IsNullOrEmpty(task.ApprovalSummary) ? task.Name : task.ApprovalSummary,
                    string.IsNullOrEmpty(task.ApprovalImpact) ? "unspecified" : task.ApprovalImpact,
                    requestedBy: task.Agent, audit: _audit, cancellationToken: cancellationToken);
            }
            catch (ApprovalDeniedException exc)
            {
                FinishTask(task, false, t0, exc.Message, fatal: true);
                return;
            }
        }

        // 3. policy guardrail: authorize the execution itself
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var action = task.Params.GetValueOrDefault("policy_action") as PolicyAction
                         ?? new PolicyAction { Kind = "execute_task", Target = task.Id };
            _policies.Evaluate(action, _ctx, _audit);
        }
        catch (PolicyViolationException exc)
        {
            _metrics.IncrementPolicyDenials();
            _ctx.RecordDecision("policy-engine", $"deny execution of {task.Id}",
                                exc.Message, impact: "run safe-stop");
            FinishTask(task, false, t0, exc.Message, fatal: _stopOnPolicyViolation);
            return;
        }

        // 4. agent execution with bounded retries / fallback / rollback
        if (!_agents.TryGetValue(task.Agent, out var agent))
        {
            FinishTask(task, false, t0, $"unknown agent '{task.Agent}'", fatal: false);
            return;
        }

        object? Primary()
        {
            cancellationToken.ThrowIfCancellationRequested();
            task.Attempts++;
            var result = agent.Run(_ctx, task, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
                throw new InvalidOperationException($"agent failed: {result.Notes}");
            return result;
        }

        void OnRetry(int attempt, double delay, Exception exc) =>
            _audit.TaskRetried(task.Id, attempt + 1, delay, exc.Message);

        void OnFailure(int attempt, Exception exc) =>
            _audit.TaskFailed(task.Id, $"{exc.GetType().Name}: {exc.Message}", attempt);

        Func<object?>? fallback = null;
        var fallbackNote = string.Empty;
        if (task.FallbackAgent is not null && _agents.TryGetValue(task.FallbackAgent, out var fallbackAgent))
        {
            fallback = () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _audit.FallbackInvoked(task.Id, task.FallbackAgent);
                execution.FallbackUsed = true;
                var res = fallbackAgent.Run(_ctx, task, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!res.Success)
                    throw new InvalidOperationException($"fallback agent failed: {res.Notes}");
                fallbackNote = $" (fallback {task.FallbackAgent} used)";
                return res;
            };
        }

        RetryReport report;
        try
        {
            report = Retry.ExecuteWithRetry(
                Primary,
                maxAttempts: task.MaxRetries,
                backoffBase: task.BackoffBase,
                fallback: fallback,
                rollbackHooks: DeferredRollbackHooks(task.Id),
                onRetry: OnRetry,
                onFailure: OnFailure,
                cancellationToken: cancellationToken,
                fatalExceptions: new[] { typeof(PolicyViolationException) });
        }
        catch (PolicyViolationException exc)
        {
            // In-agent guardrail denial: fail closed, safe-stop the run.
            _metrics.IncrementPolicyDenials();
            _ctx.RecordDecision("policy-engine", $"deny file/action write inside {task.Id}",
                                exc.Message, impact: "run safe-stop");
            FinishTask(task, false, t0, exc.Message, fatal: true);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exc) // defensive
        {
            FinishTask(task, false, t0, $"retry wrapper error: {exc.Message}", fatal: false);
            return;
        }

        if (report.RollbackRun)
        {
            _audit.Rollback(task.Id, "completed",
                            string.IsNullOrEmpty(task.RollbackNote) ? "compensation hooks ran" : task.RollbackNote);
            execution.RollbackAudited = true;
        }
        if (!report.Succeeded)
        {
            FinishTask(task, false, t0,
                       $"agent failed after {report.Attempts} attempt(s){fallbackNote}: {report.LastError}",
                       fatal: false, report: report);
            return;
        }

        // 5. exit gates (postconditions)
        foreach (var res in _gates.Evaluate(task.ExitGates, "exit", _ctx, task.Params))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _audit.Gate(task.Id, res.Gate, "exit", res.Passed, res.Reason);
            if (!res.Passed)
            {
                FinishTask(task, false, t0, $"exit gate '{res.Gate}' failed: {res.Reason}",
                           fatal: false, report: report);
                return;
            }
        }

        // 6. snapshot outputs for drift detection
        cancellationToken.ThrowIfCancellationRequested();
        _replan.SnapshotOutputs(task.Id);
        var notes = (report.Result as AgentResult)?.Notes ?? string.Empty;
        FinishTask(task, true, t0, notes + fallbackNote, report: report, cancellationToken: cancellationToken);
    }

    // Resolve after execution: agents may register compensation during an attempt.
    private IEnumerable<Action> DeferredRollbackHooks(string taskId)
    {
        foreach (var hook in RollbackHooks(taskId)) yield return hook;
    }

    private void FinishTask(TaskNode task, bool ok, double t0, string note,
                            bool fatal = false, RetryReport? report = null,
                            CancellationToken cancellationToken = default)
    {
        lock (task)
        {
            if (task.Status != TaskStatus.Running && task.Status != TaskStatus.Pending)
                return;
            cancellationToken.ThrowIfCancellationRequested();
            var duration = NowMonotonic() - t0;
            if (ok)
            {
                task.Status = TaskStatus.Succeeded;
                task.Error = string.Empty;
                _audit.TaskSucceeded(task.Id, duration, note.Length > 500 ? note[..500] : note);
            }
            else
            {
                task.Status = TaskStatus.Failed;
                task.Error = note.Length > 1000 ? note[..1000] : note;
                if (fatal)
                {
                    lock (_stopLock)
                    {
                        if (!_stopRequested) _stopReason = note;
                        _stopRequested = true;
                    }
                    _audit.Log("safe_stop", taskId: task.Id,
                               details: new Dictionary<string, object?>
                               {
                                   ["reason"] = note.Length > 500 ? note[..500] : note,
                               });
                }
                if (report is not null && report.RollbackRun)
                    task.Status = TaskStatus.RolledBack;
            }
            _metrics.RecordTask(
                task.Id,
                attempts: task.Attempts > 0 ? task.Attempts : (report?.Attempts ?? 1),
                retries: report?.Retries ?? 0,
                durationS: duration,
                outcome: ok ? "succeeded" : (task.Status == TaskStatus.RolledBack ? "rolled_back" : "failed"),
                fallbackUsed: report?.FallbackUsed ?? false,
                rollbackRun: report?.RollbackRun ?? false,
                mttrS: report?.MttrS);
        }
    }

    // ---- blocked / skipped propagation -------------------------------------------
    private void DrainBlocked()
    {
        foreach (var t in _dag.BlockedTasks())
        {
            t.Status = TaskStatus.Skipped;
            t.Error = "skipped: upstream dependency failed";
            _audit.TaskSkipped(t.Id, t.Error);
            _metrics.RecordTask(t.Id, 0, 0, 0.0, "skipped");
        }
    }

    // ---- re-plan requests queued by agents ------------------------------------------
    private void ProcessReplanRequests()
    {
        var requests = _ctx.Take<List<Dictionary<string, object?>>>("replan_requests") ?? new();
        if (requests.Count == 0) return;
        foreach (var req in requests)
        {
            _replan.RequestReplan(
                reason: req.GetValueOrDefault("reason") as string ?? "agent-requested re-plan",
                changedTaskIds: req.GetValueOrDefault("changed_task_ids") as List<string>);
        }
    }

    private string FinalStatus()
    {
        if (_stopRequested) return "stopped";
        var statuses = _dag.Tasks.Values.Select(t => t.Status).ToHashSet();
        if (statuses.IsSubsetOf(new[] { TaskStatus.Succeeded, TaskStatus.Skipped }))
            return statuses.Contains(TaskStatus.Succeeded) ? "succeeded" : "stopped";
        if (statuses.Contains(TaskStatus.Failed) || statuses.Contains(TaskStatus.RolledBack))
            return "failed";
        return "succeeded";
    }

    private static double NowMonotonic() =>
        (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
