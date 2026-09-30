// AMBIGUOUS scenario: "make short links smarter".
//
// Demonstrates: ambiguity detection -> normalization into an explicit
// hypothesis with a logged assumption set -> clarification checkpoint (human
// approval) -> stakeholder clarification arrives -> RE-PLAN event mutates the
// DAG, invalidates the clarify+downstream subgraph, and re-runs it under the
// same governance (gates, approvals, policies) with the confirmed scope.
//
// Pass 1 executes against the planner hypothesis (health monitoring only);
// pass 2 executes against the confirmed scope (device-aware smart redirects +
// health monitoring). Both passes appear in the audit trail.
//
// Usage:
//   dotnet run --project src/Scenarios -- ambiguous --auto

using AgenticUrlShortener.Agents;

namespace AgenticUrlShortener.Scenarios;

public static class Ambiguous
{
    public const string Requirement = "Make short links smarter.";

    public static void Run(bool auto)
    {
        var setup = Harness.SetupRun("ambiguous", auto);
        Console.WriteLine($"[ambiguous] run {setup.RunId} (auto={auto})");

        // Baseline: the current (v2) product the feature builds on.
        var src = Path.Combine(setup.Workspace, "src");
        var written = Codegen.Materialize("v2", src);
        setup.Audit.Log("baseline_materialized",
            details: new System.Collections.Generic.Dictionary<string, object?>
            {
                ["variant"] = "v2",
                ["files"] = written.Count,
            });
        Console.WriteLine($"[ambiguous] v2 baseline materialized ({written.Count} files) at {src}");

        var plan = Harness.RunPlanningPhase(
            setup.Ctx, setup.Audit, Requirement, "ambiguous", setup.Workspace);
        var dag = Harness.BuildDag(plan);
        var manifest = Harness.RunEngine(
            dag, setup.Ctx, setup.Audit, setup.Policies, setup.Approvals);
        Harness.Finalize(setup.Ctx, setup.RunDir, manifest);
    }
}
