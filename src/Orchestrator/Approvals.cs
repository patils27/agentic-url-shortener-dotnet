// Human approval checkpoints for high-impact actions.
//
// Tasks flagged RequiresApproval=true pause the DAG until a human (or the
// auto-approver in --auto mode) grants or denies the action. Every request and
// verdict is recorded in the context and the audit log, so even auto-approved
// demo runs leave a complete approval trail.

namespace AgenticUrlShortener.Orchestrator;

public sealed class ApprovalRequest
{
    public required string TaskId { get; init; }
    public required string Summary { get; init; }
    public required string Impact { get; init; }
    public required string RequestedBy { get; init; }
    public required string Timestamp { get; init; }
}

public sealed class ApprovalVerdict
{
    public required bool Approved { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
    public required string Timestamp { get; init; }
}

/// <summary>Raised when a human (or policy) denies a checkpoint.</summary>
public sealed class ApprovalDeniedException : Exception
{
    public ApprovalDeniedException(string message) : base(message) { }
}

public class ApprovalManager
{
    private readonly bool _auto;
    private readonly string _autoActor;
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly AsyncLocal<CancellationToken> _promptCancellation = new();

    public ApprovalManager(bool auto = false, string autoActor = "auto-approver")
    {
        _auto = auto;
        _autoActor = autoActor;
    }

    public ApprovalVerdict Request(RunContext ctx, string taskId, string summary,
                                   string impact, string requestedBy,
                                   AuditLogger? audit = null,
                                   CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ts = RunContext.NowIso();
        var req = new ApprovalRequest
        {
            TaskId = taskId,
            Summary = summary,
            Impact = impact,
            RequestedBy = requestedBy,
            Timestamp = ts,
        };

        ApprovalVerdict verdict;
        if (_auto)
        {
            verdict = new ApprovalVerdict
            {
                Approved = true,
                Actor = _autoActor,
                Timestamp = ts,
                Reason = "auto-approved: --auto mode; approval checkpoint still " +
                         "enforced and logged, human retains final review of artifacts",
            };
        }
        else
        {
            _promptLock.Wait(cancellationToken);
            var previousToken = _promptCancellation.Value;
            try
            {
                _promptCancellation.Value = cancellationToken;
                cancellationToken.ThrowIfCancellationRequested();
                verdict = PromptHuman(req);
            }
            finally
            {
                _promptCancellation.Value = previousToken;
                _promptLock.Release();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        var record = new Dictionary<string, object?>
        {
            ["timestamp"] = ts,
            ["task_id"] = taskId,
            ["summary"] = summary,
            ["impact"] = impact,
            ["requested_by"] = requestedBy,
            ["verdict"] = verdict.Approved ? "approved" : "denied",
            ["actor"] = verdict.Actor,
            ["reason"] = verdict.Reason,
        };
        ctx.RecordApproval(record);
        audit?.Approval(taskId, (string)record["verdict"]!, verdict.Actor, verdict.Reason);

        if (!verdict.Approved)
            throw new ApprovalDeniedException(
                $"approval denied for task {taskId} by {verdict.Actor}: {verdict.Reason}");
        return verdict;
    }

    protected virtual ApprovalVerdict PromptHuman(ApprovalRequest req)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("HUMAN APPROVAL CHECKPOINT");
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"Task   : {req.TaskId}");
        Console.WriteLine($"Action : {req.Summary}");
        Console.WriteLine($"Impact : {req.Impact}");
        Console.WriteLine($"Asked by agent: {req.RequestedBy}");
        Console.Write("Approve? [y/N] ");
        var answer = ReadAnswer(_promptCancellation.Value).Trim().ToLowerInvariant();
        var ts = RunContext.NowIso();
        return answer is "y" or "yes"
            ? new ApprovalVerdict { Approved = true, Actor = "human", Reason = "approved interactively", Timestamp = ts }
            : new ApprovalVerdict { Approved = false, Actor = "human", Reason = "denied interactively — run will safe-stop", Timestamp = ts };
    }

    private static string ReadAnswer(CancellationToken cancellationToken)
    {
        // Redirected input and custom PromptHuman overrides may block until their
        // reader returns. The engine keeps their worker alive until it exits.
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var answer = new System.Text.StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                if (cancellationToken.WaitHandle.WaitOne(50))
                    cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return answer.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && answer.Length > 0)
            {
                answer.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                answer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
