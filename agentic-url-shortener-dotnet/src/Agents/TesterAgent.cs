// Tester agent: executes the real xunit suite and decides the quality gate.
//
// Runs `dotnet test` as a subprocess inside the scenario workspace, parses
// the result, publishes a test report artifact, and records it in the
// context. The exit gate `artifact_present(test_report)` and the entry gate
// `tests_passed` for the release task both key off this report — a failing
// suite genuinely blocks the release.
//
// The dotnet binary is resolved as: DOTNET_BIN env var, then
// ~/workspace/.dotnet/dotnet, then `dotnet` on PATH.

using System.Diagnostics;
using System.Text.RegularExpressions;
using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class TesterAgent : Agent
{
    public override string Name => "tester";

    public static string ResolveDotnet()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_BIN");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "workspace", ".dotnet", "dotnet");
        if (File.Exists(home))
            return home;
        return "dotnet";
    }

    public static Dictionary<string, object?> ParseTestOutput(string output)
    {
        // e.g. "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12"
        var passed = 0; var failed = 0; var errors = 0;
        var m = Regex.Match(output, @"Passed:\s*(\d+)");
        if (m.Success) passed = int.Parse(m.Groups[1].Value);
        m = Regex.Match(output, @"Failed:\s*(\d+)");
        if (m.Success) failed = int.Parse(m.Groups[1].Value);
        // fall back to the terse "12 passed" / "2 failed" forms
        if (passed == 0)
        {
            m = Regex.Match(output, @"(\d+) passed");
            if (m.Success) passed = int.Parse(m.Groups[1].Value);
        }
        if (failed == 0)
        {
            m = Regex.Match(output, @"(\d+) failed");
            if (m.Success) failed = int.Parse(m.Groups[1].Value);
        }
        m = Regex.Match(output, @"(\d+) error");
        if (m.Success) errors = int.Parse(m.Groups[1].Value);
        var noTests = output.Contains("no tests", StringComparison.OrdinalIgnoreCase);
        return new Dictionary<string, object?>
        {
            ["passed"] = passed, ["failed"] = failed, ["errors"] = errors,
            ["total"] = passed + failed + errors, ["no_tests_ran"] = noTests,
        };
    }

    public override AgentResult Run(RunContext ctx, TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var testProject = StrParam(task, "test_project",
            "Shortener.Tests/Shortener.Tests.csproj");
        var src = Path.Combine(ws, "src");
        var dotnet = ResolveDotnet();

        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = src,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(testProject);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");
        psi.ArgumentList.Add("--nologo");
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_SYSTEM_NET_DISABLEIPV6"] = "1";
        var homeDotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "workspace", ".dotnet");
        if (Directory.Exists(homeDotnet))
            psi.Environment["PATH"] = homeDotnet +
                Path.PathSeparator + (psi.Environment["PATH"] ?? "");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start dotnet test");
        var timeoutMs = (int)(task.TimeoutS * 1000);
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException(
                $"dotnet test timed out after {task.TimeoutS}s");
        }
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        var output = (stdout + "\n" + stderr);
        var tail = output.Length > 6000 ? output[^6000..] : output;
        var counts = ParseTestOutput(stdout);
        var ok = (int)counts["passed"]! > 0 &&
                 (int)counts["failed"]! == 0 &&
                 (int)counts["errors"]! == 0 &&
                 proc.ExitCode == 0;
        var report = new Dictionary<string, object?>
        {
            ["test_project"] = testProject,
            ["returncode"] = proc.ExitCode,
            ["passed"] = ok,
            ["counts"] = counts,
            ["output_tail"] = tail,
        };
        ctx.Put("test_report", report);
        ctx.AddArtifact("test_report", "data",
                        producedBy: $"tester:{task.Id}",
                        content: System.Text.Json.JsonSerializer.Serialize(report));
        Decide(ctx,
            $"test gate {(ok ? "PASSED" : "FAILED")}: {counts["passed"]} passed, " +
            $"{counts["failed"]} failed, {counts["errors"]} errors",
            $"dotnet test executed against the workspace build; returncode={proc.ExitCode}",
            basedOn: new List<string> { testProject },
            impact: ok ? "release gate open" : "release gate blocked");
        var notes = $"{counts["passed"]} passed, {counts["failed"]} failed, {counts["errors"]} errors";
        if (!ok)
            // Fail the task so bounded retries / fallback / rollback engage.
            throw new InvalidOperationException(
                $"test suite failed: {notes}\n{tail[^Math.Min(2000, tail.Length)..]}");
        return Ok(new Dictionary<string, object?> { ["test_report"] = report },
                  notes: notes, artifacts: new List<string> { "test_report" });
    }
}
