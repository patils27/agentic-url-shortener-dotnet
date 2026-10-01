using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticUrlShortener.Orchestrator;

/// <summary>
/// Versioned audit envelope. Extension fields stay at the JSON root for compatibility
/// with existing JSONL consumers. Logger snapshots detach the details dictionary.
/// </summary>
public sealed record AuditEvent
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    // JsonElement limits detail values to JSON data and detaches mutable CLR objects.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Details { get; init; } = new(StringComparer.Ordinal);

    internal AuditEvent Snapshot() => this with
    {
        Details = Details.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
    };
}
