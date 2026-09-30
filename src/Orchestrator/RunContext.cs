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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public Dictionary<string, object?> Metadata { get; } = new();
    public Dictionary<string, object?> Store { get; } = new();
    public Dictionary<string, Artifact> Artifacts { get; } = new();
    public List<Decision> Decisions { get; } = new();
    public List<Assumption> Assumptions { get; } = new();
    public List<Dictionary<string, object?>> Approvals { get; } = new();
    public Dictionary<string, object?> Flags { get; } = new();

    private int _seq;

    public RunContext(string scenario, string? runId = null, string? runDir = null,
                      Dictionary<string, object?>? metadata = null)
    {
        Scenario = scenario;
        RunId = runId ?? $"run-{Guid.NewGuid():N}"[..12];
        RunDir = runDir;
        CreatedAt = NowIso();
        if (metadata is not null)
            foreach (var kv in metadata) Metadata[kv.Key] = kv.Value;
    }

    public static string NowIso() =>
        DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

    // ---- key/value store -------------------------------------------------
    public void Put(string key, object? value) => Store[key] = value;

    public object? Get(string key, object? defaultValue = null) =>
        Store.TryGetValue(key, out var v) ? v : defaultValue;

    public T? Get<T>(string key) => Store.TryGetValue(key, out var v) && v is T t ? t : default;

    public void SetFlag(string name, object? value = null) => Flags[name] = value ?? true;

    public object? GetFlag(string name, object? defaultValue = null) =>
        Flags.TryGetValue(name, out var v) ? v : defaultValue;

    public bool GetFlagBool(string name, bool defaultValue = false) =>
        Flags.TryGetValue(name, out var v) && v is bool b ? b : defaultValue;

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
            Name = name, Kind = kind, Path = path, ContentHash = contentHash,
            ProducedBy = producedBy, Timestamp = NowIso(),
            Metadata = metadata ?? new(),
        };
        Artifacts[name] = artifact;
        return artifact;
    }

    public string? ArtifactHash(string name) =>
        Artifacts.TryGetValue(name, out var a) ? a.ContentHash : null;

    // ---- decision lineage --------------------------------------------------
    public Decision RecordDecision(string actor, string decision, string rationale,
                                   List<string>? basedOn = null,
                                   List<string>? alternatives = null,
                                   string impact = "")
    {
        _seq++;
        var d = new Decision
        {
            Id = $"D{_seq:000}", Timestamp = NowIso(), Actor = actor,
            DecisionText = decision, Rationale = rationale,
            BasedOn = basedOn ?? new(), Alternatives = alternatives ?? new(),
            Impact = impact,
        };
        Decisions.Add(d);
        return d;
    }

    // ---- assumptions -------------------------------------------------------
    public Assumption LogAssumption(string actor, string statement)
    {
        var a = new Assumption
        {
            Id = $"A{Assumptions.Count + 1:00}", Timestamp = NowIso(),
            Actor = actor, Statement = statement,
        };
        Assumptions.Add(a);
        return a;
    }

    public void ResolveAssumption(string assumptionId, string status, string confirmedBy = "")
    {
        foreach (var a in Assumptions.Where(a => a.Id == assumptionId))
        {
            a.Status = status;
            a.ConfirmedBy = confirmedBy;
        }
    }

    // ---- approvals ---------------------------------------------------------
    public void RecordApproval(Dictionary<string, object?> record) => Approvals.Add(record);

    // ---- serialization -------------------------------------------------------
    public Dictionary<string, object?> ToDict() => new()
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

    public string DecisionLineageMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Decision Lineage — {Scenario} ({RunId})");
        sb.AppendLine();
        sb.AppendLine($"Run started: {CreatedAt}");
        sb.AppendLine();
        sb.AppendLine("## Decisions");
        foreach (var d in Decisions)
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
        if (Assumptions.Count > 0)
        {
            sb.AppendLine("## Assumptions");
            foreach (var a in Assumptions)
            {
                sb.Append($"- **{a.Id}** [{a.Status}] ({a.Actor}): {a.Statement}");
                if (!string.IsNullOrEmpty(a.ConfirmedBy))
                    sb.Append($" — confirmed by {a.ConfirmedBy}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        if (Approvals.Count > 0)
        {
            sb.AppendLine("## Approvals");
            foreach (var ap in Approvals)
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
        ["name"] = a.Name, ["kind"] = a.Kind, ["path"] = a.Path,
        ["content_hash"] = a.ContentHash, ["produced_by"] = a.ProducedBy,
        ["timestamp"] = a.Timestamp, ["metadata"] = a.Metadata,
    };

    private static Dictionary<string, object?> DecisionToDict(Decision d) => new()
    {
        ["id"] = d.Id, ["timestamp"] = d.Timestamp, ["actor"] = d.Actor,
        ["decision"] = d.DecisionText, ["rationale"] = d.Rationale,
        ["based_on"] = d.BasedOn, ["alternatives"] = d.Alternatives,
        ["impact"] = d.Impact,
    };

    private static Dictionary<string, object?> AssumptionToDict(Assumption a) => new()
    {
        ["id"] = a.Id, ["timestamp"] = a.Timestamp, ["actor"] = a.Actor,
        ["statement"] = a.Statement, ["status"] = a.Status,
        ["confirmed_by"] = a.ConfirmedBy,
    };
}
