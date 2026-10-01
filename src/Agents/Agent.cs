// Agent base contract shared by all six SDLC agents.
//
// Each agent is a small, auditable unit of work: it reads its task params,
// does real work (files, subprocesses, analysis), records decisions and
// artifacts in the RunContext, and returns an AgentResult. Exceptions and
// unsuccessful results enter the engine's retry, fallback, and rollback flow.

using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public abstract class Agent : IAgent
{
    public abstract string Name { get; }
    public abstract AgentResult Run(RunContext ctx, TaskNode task,
                                    CancellationToken cancellationToken = default);

    protected AgentResult Ok(Dictionary<string, object?>? outputs = null,
                            string notes = "",
                            List<string>? artifacts = null) =>
        new() { Success = true, Outputs = outputs ?? new(), Notes = notes,
                Artifacts = artifacts ?? new() };

    protected AgentResult Fail(string notes) =>
        new() { Success = false, Notes = notes };

    protected void Decide(RunContext ctx, CancellationToken cancellationToken,
                          string decision, string rationale,
                          List<string>? basedOn = null, string impact = "",
                          List<string>? alternatives = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ctx.RecordDecision(Name, decision, rationale, basedOn, alternatives, impact);
    }

    protected static void WriteFile(RunContext ctx, string path, string content,
                                    CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PoliciesOf(ctx)?.Evaluate(
            new PolicyAction { Kind = "write_file", Target = path, Payload = content },
            ctx, AuditOf(ctx));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Stage the write so cancellation does not leave a truncated destination.
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllTextAsync(temporaryPath, content, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    protected static string StrParam(TaskNode task, string key, string fallback = "") =>
        task.Params.GetValueOrDefault(key) as string ?? fallback;

    protected static T Param<T>(TaskNode task, string key, T fallback = default!)
        => task.Params.GetValueOrDefault(key) is T v ? v : fallback;

    protected static PolicyEngine? PoliciesOf(RunContext ctx) =>
        ctx.Get<PolicyEngine>("_policies");

    protected static AuditLogger? AuditOf(RunContext ctx) =>
        ctx.Get<AuditLogger>("_audit");

    protected static Engine? EngineOf(RunContext ctx) =>
        ctx.Get<Engine>("_engine");
}
