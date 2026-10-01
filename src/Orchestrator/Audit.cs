// Audit-grade observability: structured JSONL audit log + run manifest.
//
// Every state transition in the orchestrator is appended as one JSON line so the
// full history of a run is replayable and tamper-evident (append-only).

using System.Text.Json;

namespace AgenticUrlShortener.Orchestrator;

public sealed class AuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly List<Dictionary<string, object?>> _events = new();
    private readonly object _lock = new();

    public string RunId { get; }
    public string? RunDir { get; }
    public string? Path { get; }

    public AuditLogger(string runId, string? runDir = null)
    {
        RunId = runId;
        RunDir = runDir;
        if (runDir is not null)
        {
            Directory.CreateDirectory(runDir);
            Path = System.IO.Path.Combine(runDir, "audit.jsonl");
        }
    }

    public Dictionary<string, object?> Log(string eventName, string actor = "orchestrator",
                                           string? taskId = null,
                                           Dictionary<string, object?>? details = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = RunContext.NowIso(),
            ["run_id"] = RunId,
            ["event"] = eventName,
            ["actor"] = actor,
            ["task_id"] = taskId,
        };
        if (details is not null)
            foreach (var kv in ContextSnapshot.Copy(details)) entry[kv.Key] = kv.Value;

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_lock)
        {
            _events.Add(entry);
            if (Path is not null)
                File.AppendAllText(Path, line + "\n");
        }
        return ContextSnapshot.Copy(entry);
    }

    private Dictionary<string, object?> D(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // convenience wrappers for the most common transitions
    public void RunStarted(string scenario, Dictionary<string, object?>? extra = null)
    {
        var d = D(("scenario", (object?)scenario));
        if (extra is not null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        Log("run_started", details: d);
    }

    public void RunFinished(string status, Dictionary<string, object?>? extra = null) =>
        Log("run_finished", details: D(("status", (object?)status))
            .Concat(extra ?? new Dictionary<string, object?>())
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    public void TaskStarted(string taskId, string agent, int attempt = 1) =>
        Log("task_started", taskId: taskId, details: D(("agent", (object?)agent), ("attempt", attempt)));

    public void TaskSucceeded(string taskId, double durationS, string notes = "") =>
        Log("task_succeeded", taskId: taskId,
            details: D(("duration_s", (object?)Math.Round(durationS, 3)), ("notes", notes)));

    public void TaskFailed(string taskId, string error, int attempt) =>
        Log("task_failed", taskId: taskId, details: D(("error", (object?)error), ("attempt", attempt)));

    public void TaskRetried(string taskId, int attempt, double backoffS, string error) =>
        Log("task_retried", taskId: taskId,
            details: D(("attempt", (object?)attempt), ("backoff_s", Math.Round(backoffS, 3)), ("error", error)));

    public void TaskSkipped(string taskId, string reason) =>
        Log("task_skipped", taskId: taskId, details: D(("reason", (object?)reason)));

    public void FallbackInvoked(string taskId, string fallback) =>
        Log("fallback_invoked", taskId: taskId, details: D(("fallback", (object?)fallback)));

    public void Rollback(string taskId, string phase, string detail = "") =>
        Log($"rollback_{phase}", taskId: taskId, details: D(("detail", (object?)detail)));

    public void Gate(string taskId, string gate, string phase, bool passed, string reason = "") =>
        Log($"gate_{phase}", taskId: taskId,
            details: D(("gate", (object?)gate), ("passed", passed), ("reason", reason)));

    public void Approval(string taskId, string verdict, string actor, string reason) =>
        Log("approval", taskId: taskId, actor: actor,
            details: D(("verdict", (object?)verdict), ("reason", reason)));

    public void Policy(string action, bool allowed, string rule = "", string reason = "") =>
        Log("policy_evaluated",
            details: D(("action", (object?)action), ("allowed", allowed), ("rule", rule), ("reason", reason)));

    public void Replan(string reason, List<string>? invalidated = null, List<string>? added = null) =>
        Log("replan_triggered",
            details: D(("reason", (object?)reason),
                       ("invalidated", invalidated ?? new List<string>()),
                       ("added", added ?? new List<string>())));

    public void Decision(string decisionId, string actor, string summary) =>
        Log("decision_recorded", actor: actor,
            details: D(("decision_id", (object?)decisionId), ("summary", summary)));

    public IReadOnlyList<Dictionary<string, object?>> Events
    {
        get { lock (_lock) return ContextSnapshot.Copy(_events); }
    }

    public Dictionary<string, int> EventCounts()
    {
        var counts = new Dictionary<string, int>();
        lock (_lock)
        {
            foreach (var e in _events)
            {
                var key = e["event"] as string ?? "?";
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return counts;
    }

    /// <summary>Write run manifest (run-level summary) next to the audit log.</summary>
    public string WriteManifest(Dictionary<string, object?> manifest)
    {
        var path = System.IO.Path.Combine(RunDir ?? ".", "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        return path;
    }
}
