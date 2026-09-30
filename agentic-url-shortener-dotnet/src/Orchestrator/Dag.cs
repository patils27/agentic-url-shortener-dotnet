// Explicit task dependency graph (DAG) with entry/exit gates.
//
// A TaskNode is the unit of agentic work. Edges express dependencies; the
// engine executes the DAG in topological waves:
//   - tasks whose dependencies all succeeded form a wave and run concurrently
//     (Task.WhenAll fan-out),
//   - the engine waits for the whole wave before starting the next one
//     (synchronization barrier / join),
//   - a failed task blocks its transitive dependents (they are skipped) unless
//     a fallback or re-plan recovers the run.
//
// The DAG supports dynamic re-planning: tasks can be invalidated (reset to
// pending) and new tasks can be added mid-run; the engine then re-computes
// waves. Governance (gates, approvals, policies) is re-applied on re-runs.

namespace AgenticUrlShortener.Orchestrator;

/// <summary>Well-known task lifecycle states.</summary>
public static class TaskStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string RolledBack = "rolled_back";
}

/// <summary>A single unit of agentic work in the DAG.</summary>
public sealed class TaskNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Registered agent name that executes this task.</summary>
    public required string Agent { get; init; }
    public Dictionary<string, object?> Params { get; init; } = new();
    public List<string> Deps { get; init; } = new();
    public List<string> EntryGates { get; init; } = new();
    public List<string> ExitGates { get; init; } = new();
    public bool RequiresApproval { get; init; }
    public string ApprovalSummary { get; init; } = string.Empty;
    public string ApprovalImpact { get; init; } = string.Empty;
    public int MaxRetries { get; init; } = 3;
    public double BackoffBase { get; init; } = 0.2;
    /// <summary>Agent to try if the primary exhausts its retries.</summary>
    public string? FallbackAgent { get; init; }
    /// <summary>Human-readable compensation plan.</summary>
    public string RollbackNote { get; init; } = string.Empty;
    public double TimeoutS { get; init; } = 600.0;

    // mutable execution state
    public string Status { get; set; } = TaskStatus.Pending;
    public int Attempts { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>Explicit task dependency graph with wave computation.</summary>
public sealed class Dag
{
    public Dictionary<string, TaskNode> Tasks { get; } = new();

    // ---- construction --------------------------------------------------
    public TaskNode AddTask(TaskNode task)
    {
        if (Tasks.ContainsKey(task.Id))
            throw new InvalidOperationException($"duplicate task id: {task.Id}");
        Tasks[task.Id] = task;
        return task;
    }

    public void AddEdge(string taskId, string dependsOn)
    {
        if (!Tasks.ContainsKey(taskId) || !Tasks.ContainsKey(dependsOn))
            throw new KeyNotFoundException("both tasks must exist before adding an edge");
        if (!Tasks[taskId].Deps.Contains(dependsOn))
            Tasks[taskId].Deps.Add(dependsOn);
    }

    /// <summary>Check for unknown deps and cycles (Kahn's algorithm).</summary>
    public void Validate()
    {
        foreach (var t in Tasks.Values)
            foreach (var d in t.Deps)
                if (!Tasks.ContainsKey(d))
                    throw new InvalidOperationException($"task {t.Id} depends on unknown {d}");

        var indegree = Tasks.ToDictionary(kv => kv.Key, kv => kv.Value.Deps.Count);
        var dependents = BuildDependents();
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var seen = 0;
        while (queue.Count > 0)
        {
            var tid = queue.Dequeue();
            seen++;
            foreach (var nxt in dependents[tid])
            {
                indegree[nxt]--;
                if (indegree[nxt] == 0) queue.Enqueue(nxt);
            }
        }
        if (seen != Tasks.Count)
            throw new InvalidOperationException("cycle detected in task DAG");
    }

    // ---- graph queries ---------------------------------------------------
    public List<string> Dependents(string taskId) =>
        Tasks.Values.Where(t => t.Deps.Contains(taskId)).Select(t => t.Id).ToList();

    public List<string> TransitiveDependents(string taskId)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(Dependents(taskId));
        while (stack.Count > 0)
        {
            var tid = stack.Pop();
            if (!seen.Add(tid)) continue;
            foreach (var d in Dependents(tid)) stack.Push(d);
        }
        return seen.OrderBy(x => x).ToList();
    }

    /// <summary>Pending tasks whose deps all succeeded — the next parallel wave.</summary>
    public List<TaskNode> ReadyWave() =>
        Tasks.Values
            .Where(t => t.Status == TaskStatus.Pending &&
                        t.Deps.All(d => Tasks[d].Status == TaskStatus.Succeeded))
            .OrderBy(t => t.Id)
            .ToList();

    /// <summary>Pending tasks with a failed/skipped/rolled-back dependency.</summary>
    public List<TaskNode> BlockedTasks() =>
        Tasks.Values
            .Where(t => t.Status == TaskStatus.Pending &&
                        t.Deps.Any(d => Tasks[d].Status is TaskStatus.Failed
                                                              or TaskStatus.Skipped
                                                              or TaskStatus.RolledBack))
            .ToList();

    public bool IsComplete() =>
        Tasks.Values.All(t => t.Status != TaskStatus.Pending && t.Status != TaskStatus.Running);

    public List<string> TopologicalOrder()
    {
        Validate();
        var indegree = Tasks.ToDictionary(kv => kv.Key, kv => kv.Value.Deps.Count);
        var dependents = BuildDependents();
        var queue = indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x).ToList();
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var tid = queue[0];
            queue.RemoveAt(0);
            order.Add(tid);
            foreach (var nxt in dependents[tid].OrderBy(x => x))
            {
                indegree[nxt]--;
                if (indegree[nxt] == 0) queue.Add(nxt);
            }
        }
        return order;
    }

    // ---- dynamic re-planning ----------------------------------------------
    /// <summary>
    /// Reset tasks to pending so the engine re-runs them.
    /// Returns the full set of invalidated task ids (including transitive
    /// dependents of the given ids).
    /// </summary>
    public List<string> Invalidate(IEnumerable<string> taskIds)
    {
        var affected = new HashSet<string>();
        foreach (var tid in taskIds)
        {
            affected.Add(tid);
            foreach (var dep in TransitiveDependents(tid)) affected.Add(dep);
        }
        foreach (var tid in affected)
        {
            var t = Tasks[tid];
            t.Status = TaskStatus.Pending;
            t.Error = string.Empty;
        }
        return affected.OrderBy(x => x).ToList();
    }

    private Dictionary<string, List<string>> BuildDependents()
    {
        var dependents = Tasks.Keys.ToDictionary(k => k, _ => new List<string>());
        foreach (var (tid, t) in Tasks)
            foreach (var d in t.Deps)
                dependents[d].Add(tid);
        return dependents;
    }
}
