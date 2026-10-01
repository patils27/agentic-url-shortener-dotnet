using System.Diagnostics;
using AgenticUrlShortener.Agents;
using Xunit;

namespace AgenticUrlShortener.Orchestrator.Tests;

public sealed class AgentCancellationTests
{
    private static Agent CreateAgent(string name) => name switch
    {
        "planner" => new PlannerAgent(),
        "architect" => new ArchitectAgent(),
        "implementer" => new ImplementerAgent(),
        "tester" => new TesterAgent(),
        "documenter" => new DocumenterAgent(),
        "release" => new ReleaseAgent(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static TaskNode TaskFor(string agent, string workspace) => new()
    {
        Id = "cancelled", Name = "Cancellation regression", Agent = agent,
        Params = new()
        {
            ["workspace_dir"] = workspace,
            ["files"] = new List<string> { "Shortener/Models.cs" },
        },
    };

    [Theory]
    [InlineData("planner")]
    [InlineData("architect")]
    [InlineData("implementer")]
    [InlineData("tester")]
    [InlineData("documenter")]
    [InlineData("release")]
    public void CancelledAgentsDoNotPublishStateOrWriteFiles(string name)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "agent-cancel-" + Guid.NewGuid().ToString("N"));
        var context = new RunContext("cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateAgent(name).Run(context, TaskFor(name, workspace), cancellation.Token));

        Assert.Empty(context.Store);
        Assert.Empty(context.Decisions);
        Assert.Empty(context.Artifacts);
        Assert.Empty(context.Assumptions);
        Assert.False(Directory.Exists(workspace));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("implementer")]
    [InlineData("documenter")]
    [InlineData("release")]
    public void CancellationDuringPolicyCheckPreventsTheFileWrite(string name)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "agent-cancel-" + Guid.NewGuid().ToString("N"));
        var context = new RunContext("cancelled");
        using var cancellation = new CancellationTokenSource();
        context.Put("_policies", new PolicyEngine(new PolicyRule[]
        {
            (action, _) =>
            {
                if (action.Kind == "write_file") cancellation.Cancel();
                return null;
            },
        }));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateAgent(name).Run(context, TaskFor(name, workspace), cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(Directory.Exists(workspace));
        Assert.Empty(context.Artifacts);
        Assert.Null(context.Get<List<string>>("documents"));
    }

    [Fact]
    public async Task TesterCancellationStopsTheProcessTreeBeforeReturning()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "agent-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var pidFile = Path.Combine(workspace, "pids.txt");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["AGENT_CANCELLATION_PID_FILE"] = pidFile;
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                "$child = Start-Process -FilePath powershell.exe " +
                "-ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' " +
                "-WindowStyle Hidden -PassThru; " +
                "[IO.File]::WriteAllText($env:AGENT_CANCELLATION_PID_FILE, \"$PID,$($child.Id)\"); " +
                "[Console]::Out.Write('ready'); [Console]::Error.Write('ready'); Start-Sleep -Seconds 60");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                "sleep 60 & child=$!; printf '%s,%s' \"$$\" \"$child\" > \"$AGENT_CANCELLATION_PID_FILE\"; " +
                "printf ready; printf ready >&2; wait");
        }

        using var cancellation = new CancellationTokenSource();
        var running = Task.Run(() => TesterAgent.RunProcess(start, TimeSpan.FromSeconds(60), cancellation.Token));
        Process? parent = null;
        Process? child = null;
        try
        {
            var started = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && started.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(25);
            Assert.True(File.Exists(pidFile), "The subprocess did not publish its process IDs.");
            var pids = (await File.ReadAllTextAsync(pidFile)).Split(',').Select(int.Parse).ToArray();
            parent = Process.GetProcessById(pids[0]);
            child = Process.GetProcessById(pids[1]);
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);

            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(parent.HasExited);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var process in new[] { parent, child })
                {
                    if (process is null) continue;
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.Dispose();
                }
                Directory.Delete(workspace, recursive: true);
            }
        }
    }
}
