// GREENFIELD scenario: build the URL shortener from scratch.
//
// Demonstrates: requirement understanding -> decomposition into a task DAG ->
// parallel implementation paths (models/store, API, analytics+rate limiting,
// tests, docs fan out after architecture) -> join barriers at test and release
// -> human approval checkpoint -> policy-guarded file writes.
//
// Usage:
//   dotnet run --project src/Scenarios -- greenfield --auto   # auto-approve
//   dotnet run --project src/Scenarios -- greenfield          # prompt

namespace AgenticUrlShortener.Scenarios;

public static class Greenfield
{
    public const string Requirement =
        "Build a URL shortener service from scratch. " +
        "POST /api/urls creates a 7-char short code for a destination URL with an " +
        "optional expiry in days; the response includes the short URL. " +
        "GET /{code} 307-redirects to the destination and records a click with " +
        "timestamp, referrer, user-agent and IP. " +
        "GET /api/urls lists links, GET /api/urls/{code} fetches one, " +
        "DELETE /api/urls/{code} removes one. " +
        "GET /api/urls/{code}/stats returns click analytics: total clicks, " +
        "per-day counts, referrer and user-agent breakdowns. " +
        "Reliability: per-IP rate limiting at 60 requests/minute with 429 and a " +
        "Retry-After header; Idempotency-Key header support on creation; " +
        "/health and /ready probes. " +
        ".NET 8, ASP.NET Core, SQLite via Microsoft.Data.Sqlite, xunit.";

    public static void Run(bool auto)
    {
        var setup = Harness.SetupRun("greenfield", auto);
        Console.WriteLine($"[greenfield] run {setup.RunId} (auto={auto})");

        var plan = Harness.RunPlanningPhase(
            setup.Ctx, setup.Audit, Requirement, "greenfield", setup.Workspace);
        var dag = Harness.BuildDag(plan);
        var manifest = Harness.RunEngine(
            dag, setup.Ctx, setup.Audit, setup.Policies, setup.Approvals);
        Harness.Finalize(setup.Ctx, setup.RunDir, manifest);
    }
}
