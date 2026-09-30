// Agent contract: the interface every SDLC agent implements.
//
// Lives in the Orchestrator assembly so the engine can invoke agents without
// depending on the Agents assembly (which in turn references this one).

namespace AgenticUrlShortener.Orchestrator;

/// <summary>The outcome of one agent execution.</summary>
public sealed class AgentResult
{
    public required bool Success { get; init; }
    public Dictionary<string, object?> Outputs { get; init; } = new();
    public string Notes { get; init; } = string.Empty;
    /// <summary>Artifact names produced by this execution.</summary>
    public List<string> Artifacts { get; init; } = new();
}

public interface IAgent
{
    string Name { get; }
    AgentResult Run(RunContext ctx, TaskNode task);
}
