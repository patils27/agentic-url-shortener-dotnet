// Policy guardrails: security, compliance, and change-control rules.
//
// Policies are evaluated *before* an action executes. A denial raises
// PolicyViolationException, which the engine treats as a safe-stop: the run
// halts, the violation is recorded in the context and audit log, and nothing
// half-applied is left behind.
//
// Policies implemented:
//   - no_secrets_in_code: file writes are scanned for secret-like patterns.
//   - allowed_write_paths: agents may only write inside sanctioned roots.
//   - tests_must_pass_before_release: the release agent cannot run green unless
//     the tester recorded a passing report.
//   - no_destructive_migration_without_approval: schema-destructive actions
//     require an explicit human approval record.
//   - no_unreviewed_release: a release requires every approval checkpoint to
//     have been granted (or auto-granted and logged).

using System.Text.RegularExpressions;

namespace AgenticUrlShortener.Orchestrator;

public sealed class PolicyAction
{
    /// <summary>"write_file" | "execute_task" | "release" | "migrate" | "network"</summary>
    public required string Kind { get; init; }
    public string Target { get; init; } = string.Empty;
    public object? Payload { get; init; }
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

public sealed class PolicyVerdict
{
    public required bool Allowed { get; init; }
    public required string Rule { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Raised when a policy denies an action. Triggers engine safe-stop.</summary>
public sealed class PolicyViolationException : Exception
{
    public PolicyViolationException(string message) : base(message) { }
}

public delegate PolicyVerdict? PolicyRule(PolicyAction action, RunContext ctx);

public static class BuiltinPolicies
{
    private static readonly (Regex Pattern, string Label)[] SecretPatterns =
    {
        (new(@"(?i)\b(api[_-]?key|secret[_-]?key)\s*=\s*['""][^'""]{8,}['""]", RegexOptions.Compiled), "api/secret key assignment"),
        (new(@"-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled), "private key material"),
        (new(@"(?i)\bpassword\s*=\s*['""][^'""]{4,}['""]", RegexOptions.Compiled), "hardcoded password"),
        (new(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]{16,}", RegexOptions.Compiled), "bearer token"),
        (new(@"sk-(live|test)-[A-Za-z0-9]{8,}", RegexOptions.Compiled), "sk-... API token"),
        (new(@"(?i)aws_secret_access_key\s*=\s*['""][^'""]+['""]", RegexOptions.Compiled), "AWS secret access key"),
    };

    public static PolicyVerdict? RuleNoSecretsInCode(PolicyAction action, RunContext ctx)
    {
        if (action.Kind != "write_file" || action.Payload is not string payload)
            return null;
        foreach (var (pattern, label) in SecretPatterns)
        {
            if (pattern.IsMatch(payload))
                return new PolicyVerdict
                {
                    Allowed = false, Rule = "no_secrets_in_code",
                    Reason = $"secret-like content detected ({label}) in {action.Target}",
                };
        }
        return null;
    }

    public static PolicyVerdict? RuleAllowedWritePaths(PolicyAction action, RunContext ctx)
    {
        if (action.Kind != "write_file")
            return null;
        var allowedRoots = ctx.Get<List<string>>("allowed_write_roots") ?? new List<string>();
        var target = Path.GetFullPath(action.Target);
        foreach (var root in allowedRoots)
        {
            var absRoot = Path.GetFullPath(root);
            if (target == absRoot || target.StartsWith(absRoot + Path.DirectorySeparatorChar))
                return null;
        }
        return new PolicyVerdict
        {
            Allowed = false, Rule = "allowed_write_paths",
            Reason = $"write target {action.Target} is outside allowed roots [{string.Join(", ", allowedRoots)}]",
        };
    }

    public static PolicyVerdict? RuleTestsMustPassBeforeRelease(PolicyAction action, RunContext ctx)
    {
        if (action.Kind != "release")
            return null;
        var report = ctx.Get("test_report") as Dictionary<string, object?>;
        if (report is not null && report.GetValueOrDefault("passed") is true)
            return null;
        return new PolicyVerdict
        {
            Allowed = false, Rule = "tests_must_pass_before_release",
            Reason = "release blocked: no passing test report in context",
        };
    }

    public static PolicyVerdict? RuleNoDestructiveMigrationWithoutApproval(PolicyAction action, RunContext ctx)
    {
        if (action.Kind != "migrate")
            return null;
        if (action.Metadata.GetValueOrDefault("destructive") is true)
        {
            var approved = ctx.Approvals.Any(a =>
                (a.GetValueOrDefault("task_id") as string) == (action.Metadata.GetValueOrDefault("task_id") as string) &&
                (a.GetValueOrDefault("verdict") as string) == "approved");
            if (!approved)
                return new PolicyVerdict
                {
                    Allowed = false, Rule = "no_destructive_migration_without_approval",
                    Reason = "destructive migration requires an explicit approval record",
                };
        }
        return null;
    }

    public static PolicyVerdict? RuleNoUnreviewedRelease(PolicyAction action, RunContext ctx)
    {
        if (action.Kind != "release")
            return null;
        var required = ctx.Get<List<string>>("required_approvals") ?? new List<string>();
        var granted = ctx.Approvals
            .Where(a => (a.GetValueOrDefault("verdict") as string) == "approved")
            .Select(a => a.GetValueOrDefault("task_id") as string)
            .ToHashSet();
        var missing = required.Where(t => !granted.Contains(t)).ToList();
        if (missing.Count > 0)
            return new PolicyVerdict
            {
                Allowed = false, Rule = "no_unreviewed_release",
                Reason = $"missing approvals for: {string.Join(", ", missing)}",
            };
        return null;
    }
}

public sealed class PolicyEngine
{
    private readonly List<PolicyRule> _rules;

    public PolicyEngine(IEnumerable<PolicyRule>? rules = null)
    {
        _rules = rules?.ToList() ?? new List<PolicyRule>
        {
            BuiltinPolicies.RuleNoSecretsInCode,
            BuiltinPolicies.RuleAllowedWritePaths,
            BuiltinPolicies.RuleTestsMustPassBeforeRelease,
            BuiltinPolicies.RuleNoDestructiveMigrationWithoutApproval,
            BuiltinPolicies.RuleNoUnreviewedRelease,
        };
    }

    /// <summary>
    /// Return the first denying verdict, else an allow verdict.
    /// Raises PolicyViolationException on denial (safe-stop signal for the engine).
    /// </summary>
    public PolicyVerdict Evaluate(PolicyAction action, RunContext ctx, AuditLogger? audit = null)
    {
        foreach (var rule in _rules)
        {
            var verdict = rule(action, ctx);
            if (verdict is not null && !verdict.Allowed)
            {
                var violations = ctx.Get<List<string>>("policy_violations") ?? new List<string>();
                violations.Add(verdict.Reason);
                ctx.Put("policy_violations", violations);
                audit?.Policy(action.Kind, false, verdict.Rule, verdict.Reason);
                throw new PolicyViolationException($"[{verdict.Rule}] {verdict.Reason}");
            }
        }
        var allow = new PolicyVerdict { Allowed = true, Rule = "all", Reason = "all policies passed" };
        audit?.Policy(action.Kind, true, "all", "all policies passed");
        return allow;
    }
}
