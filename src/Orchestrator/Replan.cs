// Dynamic re-planning: detect upstream output changes, mutate the DAG,
// invalidate affected nodes, and re-run the affected subgraph.
//
// Mechanism:
//   - Every task records the content hashes of the artifacts it produced
//     (snapshot stored on the task).
//   - When a task is re-run (or an external "change event" arrives) and its
//     output hash differs from the snapshot, all transitive dependents are
//     invalidated — their previous outputs are stale.
//   - A re-plan may also *mutate* the DAG (add/remove tasks or edges), e.g.
//     after a clarification in the ambiguous scenario.
//   - Governance is preserved: invalidated tasks re-pass entry gates and
//     approval checkpoints on re-execution.
//
// The engine calls ReplanManager after each wave; scenario code can also
// inject explicit change events (e.g. "clarification received").

namespace AgenticUrlShortener.Orchestrator;

public sealed class ChangeEvent
{
    public required string SourceTaskId { get; init; }
    public required string Reason { get; init; }
    public string Artifact { get; init; } = string.Empty;
    public string OldHash { get; init; } = string.Empty;
    public string NewHash { get; init; } = string.Empty;
}

public sealed class ReplanManager
{
    private readonly Dag _dag;
    private readonly RunContext _ctx;
    private readonly AuditLogger? _audit;
    private readonly MetricsCollector? _metrics;
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, string>> _snapshots = new();
    private readonly List<Dictionary<string, object?>> _history = new();

    public List<Dictionary<string, object?>> History
    {
        get { lock (_sync) return ContextSnapshot.Copy(_history); }
    }

    public ReplanManager(Dag dag, RunContext ctx, AuditLogger? audit = null,
                         MetricsCollector? metrics = null)
    {
        _dag = dag;
        _ctx = ctx;
        _audit = audit;
        _metrics = metrics;
    }

    private Dictionary<string, string> ProducedBy(string taskId) =>
        _ctx.Artifacts
            .Where(kv => kv.Value.ProducedBy == taskId ||
                         kv.Value.ProducedBy.EndsWith($":{taskId}"))
            .ToDictionary(kv => kv.Key, kv => kv.Value.ContentHash);

    // ---- output tracking ----------------------------------------------------
    /// <summary>Record current artifact hashes as the task's output snapshot.</summary>
    public void SnapshotOutputs(string taskId)
    {
        lock (_sync) _snapshots[taskId] = ProducedBy(taskId);
    }

    /// <summary>Compare current artifact hashes with the snapshot; report drift.</summary>
    public ChangeEvent? DetectOutputChange(string taskId)
    {
        lock (_sync)
        {
            var before = _snapshots.GetValueOrDefault(taskId) ?? new Dictionary<string, string>();
            var current = ProducedBy(taskId);
            foreach (var (name, newHash) in current)
            {
                if (before.TryGetValue(name, out var oldHash) && oldHash != newHash)
                    return new ChangeEvent
                    {
                        SourceTaskId = taskId,
                        Reason = "task output changed on re-run",
                        Artifact = name,
                        OldHash = oldHash,
                        NewHash = newHash,
                    };
            }
            foreach (var name in before.Keys)
            {
                if (!current.ContainsKey(name))
                    return new ChangeEvent
                    {
                        SourceTaskId = taskId,
                        Reason = "task output removed on re-run",
                        Artifact = name,
                        OldHash = before[name],
                    };
            }
            return null;
        }
    }

    // ---- re-plan --------------------------------------------------------------
    /// <summary>
    /// Apply a DAG mutation and invalidate affected nodes.
    /// Returns a report describing what was invalidated/added.
    /// </summary>
    public Dictionary<string, object?> RequestReplan(
        string reason,
        List<string>? changedTaskIds = null,
        Func<Dag, RunContext, List<string>>? mutate = null,
        List<ChangeEvent>? events = null)
    {
        lock (_sync)
        {
            var changed = changedTaskIds?.ToList() ?? new List<string>();
            var added = mutate?.Invoke(_dag, _ctx) ?? new List<string>();
            var invalidated = new HashSet<string>();
            foreach (var tid in changed)
                if (_dag.Tasks.ContainsKey(tid))
                    foreach (var x in _dag.Invalidate(new[] { tid })) invalidated.Add(x);
            // newly added tasks start pending by construction; their dependents
            // (if any were added with deps on existing succeeded tasks) are fine.
            foreach (var tid in added)
                if (_dag.Tasks.ContainsKey(tid))
                    foreach (var x in _dag.Invalidate(new[] { tid })) invalidated.Add(x);

            var report = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["changed"] = changed,
                ["added"] = added,
                ["invalidated"] = invalidated.OrderBy(x => x).ToList(),
                ["events"] = (events ?? new List<ChangeEvent>()).Select(e => (object?)new Dictionary<string, object?>
                {
                    ["source_task_id"] = e.SourceTaskId,
                    ["reason"] = e.Reason,
                    ["artifact"] = e.Artifact,
                    ["old_hash"] = e.OldHash,
                    ["new_hash"] = e.NewHash,
                }).ToList(),
            };
            _history.Add(ContextSnapshot.Copy(report));
            _metrics?.IncrementReplans();
            _audit?.Replan(reason, invalidated.OrderBy(x => x).ToList(), added);
            _ctx.RecordDecision(
                actor: "orchestrator",
                decision: $"re-plan triggered: {reason}",
                rationale: "Upstream outputs changed; downstream work is stale and " +
                           "must be re-executed under the same governance gates.",
                basedOn: changed.Concat((events ?? new List<ChangeEvent>())
                    .Where(e => !string.IsNullOrEmpty(e.Artifact)).Select(e => e.Artifact)).ToList(),
                impact: $"{invalidated.Count} task(s) invalidated, {added.Count} added");
            return report;
        }
    }

    /// <summary>After a wave, detect tasks whose outputs drifted vs. snapshot.</summary>
    public List<ChangeEvent> CheckWaveForDrift(IEnumerable<string> completedTaskIds)
    {
        lock (_sync)
        {
            var detected = new List<ChangeEvent>();
            foreach (var tid in completedTaskIds)
            {
                if (!_snapshots.ContainsKey(tid)) continue;
                var ev = DetectOutputChange(tid);
                if (ev is not null) detected.Add(ev);
            }
            return detected;
        }
    }
}
