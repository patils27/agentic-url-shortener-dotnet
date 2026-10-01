// BROWNFIELD scenario: enhance the shipped URL shortener.
//
// Baseline: the v1 greenfield output is materialized into the run workspace.
// The planner decomposes the change request; the architect performs real
// approximate impact analysis (using-directive import graph); the implementer
// evolves v1 -> v2 (custom aliases + 410 bug fix + validators refactor) with
// changed paths and diff-line counts in the audit trail; tests are refreshed in
// parallel; the release gate requires all tests green plus human approval
// (the 404->410 change is intentionally behavior-breaking).
//
// Usage:
//   dotnet run --project src/Scenarios -- brownfield --auto

using AgenticUrlShortener.Agents;

namespace AgenticUrlShortener.Scenarios;

public static class Brownfield
{
    public const string Requirement =
        "Enhance the shipped URL shortener (v1 baseline in the workspace). " +
        "1) Enhancement: support custom aliases on POST /api/urls — 3-32 chars, " +
        "[A-Za-z0-9_-], unique; return 409 Conflict when the alias is taken. " +
        "2) Bug fix: expired links currently return 404; they must return " +
        "410 Gone so clients can distinguish expired from unknown codes. " +
        "3) Refactor: extract the inline URL validation in Program.cs into a shared " +
        "Validators.cs module with dedicated unit tests. " +
        "No database migration: the custom alias reuses the code primary key. " +
        "All existing tests must keep passing.";

    public static void Run(bool auto)
    {
        var setup = Harness.SetupRun("brownfield", auto);
        Console.WriteLine($"[brownfield] run {setup.RunId} (auto={auto})");

        // Baseline: the v1 product as shipped by the greenfield run.
        var src = Path.Combine(setup.Workspace, "src");
        var written = Codegen.Materialize("v1", src);
        setup.Audit.Log("baseline_materialized",
            details: new System.Collections.Generic.Dictionary<string, object?>
            {
                ["variant"] = "v1",
                ["files"] = written.Count,
            });
        Console.WriteLine($"[brownfield] v1 baseline materialized ({written.Count} files) at {src}");

        var plan = Harness.RunPlanningPhase(
            setup.Ctx, setup.Audit, Requirement, "brownfield", setup.Workspace);
        var dag = Harness.BuildDag(plan);
        var manifest = Harness.RunEngine(
            dag, setup.Ctx, setup.Audit, setup.Policies, setup.Approvals);
        Harness.Finalize(setup.Ctx, setup.RunDir, manifest);
    }
}
