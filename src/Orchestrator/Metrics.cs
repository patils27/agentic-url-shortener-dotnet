// Reliability metrics for orchestrated runs.
//
// Tracks per-task attempts/retries/duration and aggregates run-level reliability
// signals: success rate, retry count/frequency, rollback frequency, MTTR, and
// end-to-end latency. Serialized to metrics.json at the end of each run.

using System.Text.Json;

namespace AgenticUrlShortener.Orchestrator;

public sealed class TaskMetrics
{
    public required string TaskId { get; init; }
    public required string Agent { get; init; }
    public int Attempts { get; set; }
    public int Retries { get; set; }
    public bool FallbackUsed { get; set; }
    public bool RollbackRun { get; set; }
    public double DurationS { get; set; }
    /// <summary>succeeded | failed | skipped | rolled_back</summary>
    public string Outcome { get; set; } = "pending";
    public double? MttrS { get; set; }
}

public sealed class MetricsCollector
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaskMetrics> _tasks = new();
    private int _replans;
    private int _policyDenials;
    private int _approvalsRequested;
    private readonly double _runStartedAt = NowMonotonic();
    private readonly string _runStartedWall = RunContext.NowIso();

    public Dictionary<string, TaskMetrics> Tasks
    {
        get { lock (_sync) return _tasks.ToDictionary(kv => kv.Key, kv => Copy(kv.Value)); }
    }
    public int Replans { get { lock (_sync) return _replans; } }
    public int PolicyDenials { get { lock (_sync) return _policyDenials; } }
    public int ApprovalsRequested { get { lock (_sync) return _approvalsRequested; } }

    public void IncrementReplans() { lock (_sync) _replans++; }
    public void IncrementPolicyDenials() { lock (_sync) _policyDenials++; }
    public void IncrementApprovalsRequested() { lock (_sync) _approvalsRequested++; }

    private static double NowMonotonic() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    // ---- per-task recording ------------------------------------------------
    public void RegisterTask(string taskId, string agent)
    {
        lock (_sync) _tasks.TryAdd(taskId, new TaskMetrics { TaskId = taskId, Agent = agent });
    }

    public void RecordTask(string taskId, int attempts, int retries, double durationS,
                           string outcome, bool fallbackUsed = false,
                           bool rollbackRun = false, double? mttrS = null)
    {
        lock (_sync)
        {
            if (!_tasks.TryGetValue(taskId, out var m))
            {
                m = new TaskMetrics { TaskId = taskId, Agent = "?" };
                _tasks[taskId] = m;
            }
            m.Attempts = attempts;
            m.Retries = retries;
            m.DurationS = Math.Round(durationS, 3);
            m.Outcome = outcome;
            m.FallbackUsed = fallbackUsed;
            m.RollbackRun = rollbackRun;
            m.MttrS = mttrS.HasValue ? Math.Round(mttrS.Value, 3) : null;
        }
    }

    // ---- run-level aggregates ----------------------------------------------
    public Dictionary<string, object?> Summary()
    {
        List<TaskMetrics> tasks;
        int replans, policyDenials, approvalsRequested;
        lock (_sync)
        {
            tasks = _tasks.Values.Select(Copy).ToList();
            replans = _replans;
            policyDenials = _policyDenials;
            approvalsRequested = _approvalsRequested;
        }
        var executed = tasks.Where(t => t.Outcome is "succeeded" or "failed").ToList();
        var succeeded = executed.Where(t => t.Outcome == "succeeded").ToList();
        var totalRetries = tasks.Sum(t => t.Retries);
        var totalAttempts = tasks.Sum(t => t.Attempts);
        var rollbacks = tasks.Count(t => t.RollbackRun);
        var mttrs = tasks.Where(t => t.MttrS.HasValue).Select(t => t.MttrS!.Value).ToList();
        var e2e = NowMonotonic() - _runStartedAt;

        return new Dictionary<string, object?>
        {
            ["run_started"] = _runStartedWall,
            ["tasks_total"] = tasks.Count,
            ["tasks_executed"] = executed.Count,
            ["tasks_succeeded"] = succeeded.Count,
            ["tasks_failed"] = executed.Count - succeeded.Count,
            ["success_rate"] = executed.Count > 0 ? Math.Round((double)succeeded.Count / executed.Count, 3) : 0.0,
            ["total_attempts"] = totalAttempts,
            ["total_retries"] = totalRetries,
            ["retry_frequency"] = totalAttempts > 0 ? Math.Round((double)totalRetries / totalAttempts, 3) : 0.0,
            ["rollbacks"] = rollbacks,
            ["rollback_frequency"] = executed.Count > 0 ? Math.Round((double)rollbacks / executed.Count, 3) : 0.0,
            ["mttr_s"] = mttrs.Count > 0 ? Math.Round(mttrs.Average(), 3) : null,
            ["mttr_samples"] = mttrs.Count,
            ["replans"] = replans,
            ["policy_denials"] = policyDenials,
            ["approvals_requested"] = approvalsRequested,
            ["end_to_end_latency_s"] = Math.Round(e2e, 3),
            ["per_task"] = tasks.ToDictionary(t => t.TaskId, t => (object?)new Dictionary<string, object?>
            {
                ["task_id"] = t.TaskId,
                ["agent"] = t.Agent,
                ["attempts"] = t.Attempts,
                ["retries"] = t.Retries,
                ["fallback_used"] = t.FallbackUsed,
                ["rollback_run"] = t.RollbackRun,
                ["duration_s"] = t.DurationS,
                ["outcome"] = t.Outcome,
                ["mttr_s"] = t.MttrS,
            }),
        };
    }

    private static TaskMetrics Copy(TaskMetrics m) => new()
    {
        TaskId = m.TaskId,
        Agent = m.Agent,
        Attempts = m.Attempts,
        Retries = m.Retries,
        DurationS = m.DurationS,
        Outcome = m.Outcome,
        FallbackUsed = m.FallbackUsed,
        RollbackRun = m.RollbackRun,
        MttrS = m.MttrS,
    };

    public string Write(string runDir)
    {
        var path = System.IO.Path.Combine(runDir, "metrics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Summary(), new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        return path;
    }
}
