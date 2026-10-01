using System.Text.Json;
using AgenticUrlShortener.Orchestrator;
using Xunit;

namespace AgenticUrlShortener.Orchestrator.Tests;

public sealed class AuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "shortener-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TypedEventsPreserveTheFlatJsonContractAndValueTypes()
    {
        var logger = new AuditLogger("run-1", _directory);
        logger.Log("task_retried", taskId: "task-1", details: new()
        {
            ["attempt"] = 2, ["backoff_s"] = 0.5, ["allowed"] = false,
            ["error"] = "first line\nsecond line", ["optional"] = null,
            ["tasks"] = new[] { "a", "b" },
        });
        var line = Assert.Single(File.ReadAllLines(logger.Path!));
        using var document = JsonDocument.Parse(line);
        var json = document.RootElement;
        Assert.Equal(1, json.GetProperty("schema_version").GetInt32());
        Assert.Equal("run-1", json.GetProperty("run_id").GetString());
        Assert.Equal("task_retried", json.GetProperty("event").GetString());
        Assert.Equal("orchestrator", json.GetProperty("actor").GetString());
        Assert.Equal("task-1", json.GetProperty("task_id").GetString());
        Assert.Equal(TimeSpan.Zero, json.GetProperty("timestamp").GetDateTimeOffset().Offset);
        Assert.False(json.TryGetProperty("details", out _));
        Assert.Equal(2, json.GetProperty("attempt").GetInt32());
        Assert.Equal(0.5, json.GetProperty("backoff_s").GetDouble());
        Assert.False(json.GetProperty("allowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("optional").ValueKind);
        Assert.Equal("first line\nsecond line", json.GetProperty("error").GetString());
        Assert.Equal(2, json.GetProperty("tasks").GetArrayLength());
        var restored = JsonSerializer.Deserialize<AuditEvent>(line)!;
        Assert.Equal("task_retried", restored.Event);
        Assert.Equal(2, restored.Details["attempt"].GetInt32());
        Assert.Equal(line, JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void LegacyJsonWithoutSchemaVersionCanStillBeRead()
    {
        const string line = """
            {"timestamp":"2026-10-01T10:00:00-05:00","run_id":"old-run","event":"task_started","actor":"orchestrator","task_id":"t","attempt":1}
            """;
        var entry = JsonSerializer.Deserialize<AuditEvent>(line)!;
        Assert.Equal(1, entry.SchemaVersion);
        Assert.Equal("old-run", entry.RunId);
        Assert.Equal("task_started", entry.Event);
        Assert.Equal(1, entry.Details["attempt"].GetInt32());
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("timestamp")]
    [InlineData("run_id")]
    [InlineData("event")]
    [InlineData("actor")]
    [InlineData("task_id")]
    [InlineData("RUN_ID")]
    public void DetailsCannotOverwriteEnvelopeFields(string field)
    {
        var logger = new AuditLogger("real-run", _directory);
        Assert.Throws<ArgumentException>(() => logger.Log("test", details: new() { [field] = "forged" }));
        Assert.Empty(logger.Events);
        Assert.False(File.Exists(logger.Path));
    }

    [Fact]
    public void JsonSnapshotsDetachMutableObjectsAndDisposedJsonDocuments()
    {
        var logger = new AuditLogger("snapshots");
        var payload = new MutablePayload { Value = "before" };
        using (var document = JsonDocument.Parse("{\"nested\":[1,2]}"))
            logger.Log("test", details: new() { ["payload"] = payload, ["json"] = document.RootElement });
        payload.Value = "after";
        var entry = Assert.Single(logger.Events);
        Assert.Equal("before", entry.Details["payload"].GetProperty("value").GetString());
        Assert.Equal(2, entry.Details["json"].GetProperty("nested").GetArrayLength());
        entry.Details.Clear();
        Assert.Equal(2, Assert.Single(logger.Events).Details.Count);
    }

    [Fact]
    public void ParallelWritersKeepFileAndMemoryInTheSameOrderWithoutLostEvents()
    {
        var logger = new AuditLogger("parallel", _directory);
        Parallel.For(0, 200, i =>
        {
            logger.TaskStarted($"task-{i}", "test");
            if (i % 20 == 0) _ = logger.Events;
        });
        var events = logger.Events;
        var lines = File.ReadAllLines(logger.Path!);
        Assert.Equal(200, events.Count);
        Assert.Equal(200, events.Select(e => e.TaskId).Distinct().Count());
        Assert.Equal(200, logger.EventCounts()["task_started"]);
        Assert.Equal(events.Select(e => e.TaskId), lines.Select(line => JsonSerializer.Deserialize<AuditEvent>(line)!.TaskId));
    }

    [Fact]
    public void FailedAppendDoesNotPublishAnInMemoryEvent()
    {
        var logger = new AuditLogger("failed-write", _directory);
        Directory.CreateDirectory(logger.Path!); // A directory at the file path forces append to fail.
        var error = Record.Exception(() => logger.TaskStarted("task", "test"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(logger.Events);
        Assert.Empty(logger.EventCounts());
    }

    [Fact]
    public void EnvelopeIdentityMustBeNonempty()
    {
        Assert.Throws<ArgumentException>(() => new AuditLogger(" "));
        var logger = new AuditLogger("valid-run");
        Assert.Throws<ArgumentException>(() => logger.Log(" "));
        Assert.Throws<ArgumentException>(() => logger.Log("test", actor: ""));
        Assert.Empty(logger.Events);
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_directory);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("shortener-audit-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean up outside the audit test directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private sealed class MutablePayload
    {
        public string Value { get; set; } = "";
    }
}
