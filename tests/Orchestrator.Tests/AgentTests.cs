using System.Diagnostics;
using AgenticUrlShortener.Agents;
using Xunit;

namespace AgenticUrlShortener.Orchestrator.Tests;

public sealed class AgentTests
{
    private static ProcessStartInfo Shell(string windowsCommand, string unixCommand)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(windowsCommand);
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(unixCommand);
        }
        return start;
    }

    [Fact]
    public void TesterDrainsLargeOutputFromBothPipes()
    {
        var start = Shell(
            "[Console]::Out.Write(('x' * 1048576)); [Console]::Error.Write(('y' * 1048576)); exit 7",
            "head -c 1048576 /dev/zero; head -c 1048576 /dev/zero >&2; exit 7");
        var result = TesterAgent.RunProcess(start, TimeSpan.FromSeconds(20));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1048576, result.Stdout.Length);
        Assert.Equal(1048576, result.Stderr.Length);
    }

    [Fact]
    public void TesterStillTimesOutHungProcesses()
    {
        var start = Shell("Start-Sleep -Seconds 30", "sleep 30");
        Assert.Throws<TimeoutException>(() => TesterAgent.RunProcess(start, TimeSpan.FromMilliseconds(500)));
    }
}
