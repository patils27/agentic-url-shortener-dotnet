// Cross-stage context store and decision lineage for the agentic orchestrator.
//
// The RunContext is the single source of truth shared by every agent in a run.
// It carries:
//   - run identity (run_id, scenario, timestamps)
//   - a key/value store for cross-stage data (specs, file paths, flags)
//   - produced artifacts with content hashes (used by re-planning)
//   - a decision lineage log: every consequential decision recorded with
//     who made it, what was decided, why, and what it was based on
//   - an assumption log (critical for ambiguous requirements)
//   - approval records
//
// All mutations are timestamped; the audit logger mirrors key mutations so the
// JSONL audit trail and the in-memory context stay consistent.

using System.Collections;
using System.Security.Cryptography;
using System.Text;

namespace AgenticUrlShortener.Orchestrator;

/// <summary>One entry in the decision lineage log.</summary>
public sealed class Decision
{
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    /// <summary>Agent name or "human" / "policy-engine" / "orchestrator".</summary>
    public required string Actor { get; init; }
    /// <summary>What was decided.</summary>
    public required string DecisionText { get; init; }
    /// <summary>Why it was decided.</summary>
    public required string Rationale { get; init; }
    /// <summary>Inputs / evidence keys.</summary>
    public List<string> BasedOn { get; init; } = new();
    public List<string> Alternatives { get; init; } = new();
    /// <summary>Blast radius / consequence note.</summary>
    public string Impact { get; init; } = string.Empty;
}

/// <summary>
/// Detaches context data without serializing its types. Lists, dictionaries,
/// arrays and context records are copied recursively; service objects (Engine,
/// PolicyEngine, AuditLogger) and other custom objects retain their identity.
/// Custom mutable objects must provide their own synchronization. Callers must
/// not mutate input collections during copying.
/// </summary>
internal static class ContextSnapshot
{
    public static T Copy<T>(T value) => (T)CopyValue(value)!;

    private static object? CopyValue(object? value)
    {
        switch (value)
        {
            case null: return null;
            case Decision d:
                return new Decision
                {
                    Id = d.Id,
                    Timestamp = d.Timestamp,
                    Actor = d.Actor,
                    DecisionText = d.DecisionText,
                    Rationale = d.Rationale,
                    BasedOn = d.BasedOn.ToList(),
                    Alternatives = d.Alternatives.ToList(),
                    Impact = d.Impact,
                };
            case Assumption a:
                return new Assumption
                {
                    Id = a.Id,
                    Timestamp = a.Timestamp,
                    Actor = a.Actor,
                    Statement = a.Statement,
                    Status = a.Status,
                    ConfirmedBy = a.ConfirmedBy,
                };
            case Artifact a:
                return new Artifact
                {
                    Name = a.Name,
                    Kind = a.Kind,
                    Path = a.Path,
                    ContentHash = a.ContentHash,
                    ProducedBy = a.ProducedBy,
                    Timestamp = a.Timestamp,
                    Metadata = Copy(a.Metadata),
                };
            case Array array:
                var arrayCopy = (Array)array.Clone();
                if (array.Rank != 1)
                    throw new ArgumentException("Context data supports one-dimensional arrays only.");
                for (var i = array.GetLowerBound(0); i <= array.GetUpperBound(0); i++)
                    arrayCopy.SetValue(CopyValue(array.GetValue(i)), i);
                return arrayCopy;
            case IDictionary dictionary:
                var dictionaryType = value.GetType();
                var comparer = dictionaryType.GetProperty("Comparer")?.GetValue(dictionary);
                var dictionaryCopy = (IDictionary)(comparer is null
                    ? Activator.CreateInstance(dictionaryType)!
                    : Activator.CreateInstance(dictionaryType, comparer)!);
                foreach (DictionaryEntry item in dictionary)
                    dictionaryCopy.Add(item.Key, CopyValue(item.Value));
                return dictionaryCopy;
            case IList list:
                var listCopy = (IList)Activator.CreateInstance(value.GetType())!;
                foreach (var item in list) listCopy.Add(CopyValue(item));
                return listCopy;
            default:
                return value;
        }
    }
}

/// <summary>One entry in the assumption log.</summary>
public sealed class Assumption
{
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Actor { get; init; }
    public required string Statement { get; init; }
    /// <summary>open | confirmed | rejected | superseded</summary>
    public string Status { get; set; } = "open";
    public string ConfirmedBy { get; set; } = string.Empty;
}

/// <summary>A produced artifact with a content hash.</summary>
public sealed class Artifact
{
    public required string Name { get; init; }
    /// <summary>"file" | "data" | "doc"</summary>
    public required string Kind { get; init; }
    public string? Path { get; init; }
    public required string ContentHash { get; init; }
    public required string ProducedBy { get; init; }
    public required string Timestamp { get; init; }
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

/// <summary>Mutable, shared context for one orchestrated run.</summary>
public sealed class RunContext
{
    public string RunId { get; }
    public string Scenario { get; }
    public string? RunDir { get; }
    public string CreatedAt { get; }
    private readonly object _sync = new();
    private readonly Dictionary<string, object?> _metadata = new();
    private readonly Dictionary<string, object?> _store = new();
    private readonly Dictionary<string, Artifact> _artifacts = new();
    private readonly List<Decision> _decisions = new();
    private readonly List<Assumption> _assumptions = new();
    private readonly List<Dictionary<string, object?>> _approvals = new();
    private readonly Dictionary<string, object?> _flags = new();

    // Collection properties and reads are detached snapshots. Publish changes through
    // the mutation methods; read-modify-write sequences must use Update/AppendToList.
    public Dictionary<string, object?> Metadata { get { lock (_sync) return ContextSnapshot.Copy(_metadata); } }
    public Dictionary<string, object?> Store { get { lock (_sync) return ContextSnapshot.Copy(_store); } }
    public Dictionary<string, Artifact> Artifacts { get { lock (_sync) return ContextSnapshot.Copy(_artifacts); } }
    public List<Decision> Decisions { get { lock (_sync) return ContextSnapshot.Copy(_decisions); } }
    public List<Assumption> Assumptions { get { lock (_sync) return ContextSnapshot.Copy(_assumptions); } }
    public List<Dictionary<string, object?>> Approvals { get { lock (_sync) return ContextSnapshot.Copy(_approvals); } }
    public Dictionary<string, object?> Flags { get { lock (_sync) return ContextSnapshot.Copy(_flags); } }

    private int _seq;

    public RunContext(string scenario, string? runId = null, string? runDir = null,
                      Dictionary<string, object?>? metadata = null)
    {
        Scenario = scenario;
        RunId = runId ?? $"run-{Guid.NewGuid():N}"[..12];
        RunDir = runDir;
        CreatedAt = NowIso();
        if (metadata is not null)
            foreach (var kv in ContextSnapshot.Copy(metadata)) _metadata[kv.Key] = kv.Value;
    }

    public static string NowIso() =>
        DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

    // ---- key/value store -------------------------------------------------
    public void Put(string key, object? value)
    {
        lock (_sync) _store[key] = ContextSnapshot.Copy(value);
    }

    public object? Get(string key, object? defaultValue = null)
    {
        lock (_sync) return ContextSnapshot.Copy(_store.GetValueOrDefault(key, defaultValue));
    }

    public T? Get<T>(string key)
    {
        lock (_sync) return _store.TryGetValue(key, out var v) && v is T t ? ContextSnapshot.Copy(t) : default;
    }

    /// <summary>Atomically transform one stored value. The callback must be short and must not wait on other work.</summary>
    public T Update<T>(string key, Func<T?, T> update)
    {
        lock (_sync)
        {
            var current = _store.TryGetValue(key, out var value) && value is T typed
                ? ContextSnapshot.Copy(typed) : default;
            var updated = update(current);
            _store[key] = ContextSnapshot.Copy(updated);
            return ContextSnapshot.Copy(updated);
        }
    }

    public void AppendToList<T>(string key, T item)
    {
        lock (_sync)
        {
            if (!_store.TryGetValue(key, out var value))
                _store[key] = value = new List<T>();
            if (value is not List<T> list)
                throw new InvalidOperationException($"Context value '{key}' is not a List<{typeof(T).Name}>.");
            list.Add(ContextSnapshot.Copy(item));
        }
    }

    /// <summary>Atomically read and remove a value, for example a queue of replan requests.</summary>
    public T? Take<T>(string key)
    {
        lock (_sync)
        {
            if (!_store.TryGetValue(key, out var value) || value is not T typed) return default;
            _store.Remove(key);
            return ContextSnapshot.Copy(typed);
        }
    }

    public void SetFlag(string name, object? value = null)
    {
        lock (_sync) _flags[name] = ContextSnapshot.Copy(value ?? true);
    }

    public object? GetFlag(string name, object? defaultValue = null)
    {
        lock (_sync) return ContextSnapshot.Copy(_flags.GetValueOrDefault(name, defaultValue));
    }

    public bool GetFlagBool(string name, bool defaultValue = false)
    {
        lock (_sync) return _flags.TryGetValue(name, out var v) && v is bool b ? b : defaultValue;
    }

    // ---- artifacts -------------------------------------------------------
    public Artifact AddArtifact(string name, string kind, string producedBy,
                                string? path = null, string? content = null,
                                byte[]? contentBytes = null,
                                Dictionary<string, object?>? metadata = null)
    {
        string contentHash;
        if (contentBytes is not null)
            contentHash = HashBytes(contentBytes);
        else if (content is not null)
            contentHash = HashBytes(Encoding.UTF8.GetBytes(content));
        else
            contentHash = HashBytes(Encoding.UTF8.GetBytes($"{name}:{DateTimeOffset.UtcNow.Ticks}"));

        var artifact = new Artifact
        {
            Name = name,
            Kind = kind,
            Path = path,
            ContentHash = contentHash,
            ProducedBy = producedBy,
            Timestamp = NowIso(),
            Metadata = metadata is null ? new() : ContextSnapshot.Copy(metadata),
        };
        lock (_sync) _artifacts[name] = artifact;
        return ContextSnapshot.Copy(artifact);
    }

    public string? ArtifactHash(string name)
    {
        lock (_sync) return _artifacts.TryGetValue(name, out var a) ? a.ContentHash : null;
    }

    // ---- decision lineage --------------------------------------------------
    public Decision RecordDecision(string actor, string decision, string rationale,
                                   List<string>? basedOn = null,
                                   List<string>? alternatives = null,
                                   string impact = "")
    {
        lock (_sync)
        {
            _seq++;
            var d = new Decision
            {
                Id = $"D{_seq:000}",
                Timestamp = NowIso(),
                Actor = actor,
                DecisionText = decision,
                Rationale = rationale,
                BasedOn = basedOn?.ToList() ?? new(),
                Alternatives = alternatives?.ToList() ?? new(),
                Impact = impact,
            };
            _decisions.Add(d);
            return ContextSnapshot.Copy(d);
        }
    }

    // ---- assumptions -------------------------------------------------------
    public Assumption LogAssumption(string actor, string statement)
    {
        lock (_sync)
        {
            var a = new Assumption
            {
                Id = $"A{_assumptions.Count + 1:00}",
                Timestamp = NowIso(),
                Actor = actor,
                Statement = statement,
            };
            _assumptions.Add(a);
            return ContextSnapshot.Copy(a);
        }
    }

    public void ResolveAssumption(string assumptionId, string status, string confirmedBy = "")
    {
        lock (_sync)
            foreach (var a in _assumptions.Where(a => a.Id == assumptionId))
            {
                a.Status = status;
                a.ConfirmedBy = confirmedBy;
            }
    }

    // ---- approvals ---------------------------------------------------------
    public void RecordApproval(Dictionary<string, object?> record)
    {
        lock (_sync) _approvals.Add(ContextSnapshot.Copy(record));
    }

    // ---- serialization -------------------------------------------------------
    public Dictionary<string, object?> ToDict()
    {
        lock (_sync) return new()
        {
            ["run_id"] = RunId,
            ["scenario"] = Scenario,
            ["created_at"] = CreatedAt,
            ["run_dir"] = RunDir,
            ["metadata"] = Metadata,
            ["store_keys"] = Store.Keys.OrderBy(k => k).ToList(),
            ["artifacts"] = Artifacts.ToDictionary(kv => kv.Key, kv => ArtifactToDict(kv.Value)),
            ["decisions"] = Decisions.Select(DecisionToDict).ToList(),
            ["assumptions"] = Assumptions.Select(AssumptionToDict).ToList(),
            ["approvals"] = Approvals,
            ["flags"] = Flags,
        };
    }

    public string DecisionLineageMarkdown()
    {
        List<Decision> decisions;
        List<Assumption> assumptions;
        List<Dictionary<string, object?>> approvals;
        lock (_sync)
        {
            decisions = Decisions;
            assumptions = Assumptions;
            approvals = Approvals;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# Decision Lineage — {Scenario} ({RunId})");
        sb.AppendLine();
        sb.AppendLine($"Run started: {CreatedAt}");
        sb.AppendLine();
        sb.AppendLine("## Decisions");
        foreach (var d in decisions)
        {
            sb.AppendLine($"### {d.Id} · {d.Actor} · {d.Timestamp}");
            sb.AppendLine($"- **Decision:** {d.DecisionText}");
            sb.AppendLine($"- **Rationale:** {d.Rationale}");
            if (d.BasedOn.Count > 0)
                sb.AppendLine($"- **Based on:** {string.Join(", ", d.BasedOn)}");
            if (d.Alternatives.Count > 0)
                sb.AppendLine($"- **Alternatives considered:** {string.Join("; ", d.Alternatives)}");
            if (!string.IsNullOrEmpty(d.Impact))
                sb.AppendLine($"- **Impact:** {d.Impact}");
            sb.AppendLine();
        }
        if (assumptions.Count > 0)
        {
            sb.AppendLine("## Assumptions");
            foreach (var a in assumptions)
            {
                sb.Append($"- **{a.Id}** [{a.Status}] ({a.Actor}): {a.Statement}");
                if (!string.IsNullOrEmpty(a.ConfirmedBy))
                    sb.Append($" — confirmed by {a.ConfirmedBy}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        if (approvals.Count > 0)
        {
            sb.AppendLine("## Approvals");
            foreach (var ap in approvals)
            {
                sb.AppendLine($"- {ap.GetValueOrDefault("timestamp")}: **{ap.GetValueOrDefault("task_id")}** — " +
                              $"{ap.GetValueOrDefault("verdict")} by {ap.GetValueOrDefault("actor")} ({ap.GetValueOrDefault("reason")})");
            }
        }
        return sb.ToString();
    }

    private static string HashBytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();

    private static Dictionary<string, object?> ArtifactToDict(Artifact a) => new()
    {
        ["name"] = a.Name,
        ["kind"] = a.Kind,
        ["path"] = a.Path,
        ["content_hash"] = a.ContentHash,
        ["produced_by"] = a.ProducedBy,
        ["timestamp"] = a.Timestamp,
        ["metadata"] = a.Metadata,
    };

    private static Dictionary<string, object?> DecisionToDict(Decision d) => new()
    {
        ["id"] = d.Id,
        ["timestamp"] = d.Timestamp,
        ["actor"] = d.Actor,
        ["decision"] = d.DecisionText,
        ["rationale"] = d.Rationale,
        ["based_on"] = d.BasedOn,
        ["alternatives"] = d.Alternatives,
        ["impact"] = d.Impact,
    };

    private static Dictionary<string, object?> AssumptionToDict(Assumption a) => new()
    {
        ["id"] = a.Id,
        ["timestamp"] = a.Timestamp,
        ["actor"] = a.Actor,
        ["statement"] = a.Statement,
        ["status"] = a.Status,
        ["confirmed_by"] = a.ConfirmedBy,
    };
}
