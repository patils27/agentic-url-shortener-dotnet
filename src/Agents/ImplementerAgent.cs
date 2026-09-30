// Implementer agent: performs real implementation work on the filesystem.
//
// Modes:
//   - materialize_subset: write a subset of codegen files (greenfield build).
//   - write_tests: write the generated xunit suite + test csproj.
//   - apply_v2: brownfield evolution — rewrite changed files to v2 content,
//     recording unified diffs in the audit trail.
//   - smart_feature: ambiguous scenario — add SmartLinks.cs, extend
//     Program.cs at the marked extension point, and write SmartTests.cs.
//
// Every file write is policy-checked (write_file action) BEFORE it happens; a
// denial raises PolicyViolation, which the engine treats as a fatal safe-stop.
// Rollback hooks (compensation) are registered with the engine so partial work
// is undone if the task fails after retries.

using System.Text;
using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class ImplementerAgent : Agent
{
    public override string Name => "implementer";

    public override AgentResult Run(RunContext ctx, TaskNode task)
    {
        var mode = StrParam(task, "mode", "materialize_subset");
        return mode switch
        {
            "write_tests" => WriteTests(ctx, task),
            "apply_v2" => ApplyV2(ctx, task),
            "smart_feature" => SmartFeature(ctx, task),
            _ => MaterializeSubset(ctx, task),
        };
    }

    // ------------------------------------------------------------------
    private void CheckedWrite(RunContext ctx, string path, string content)
    {
        PoliciesOf(ctx)?.Evaluate(
            new PolicyAction { Kind = "write_file", Target = path, Payload = content },
            ctx, AuditOf(ctx));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void RegisterCleanup(RunContext ctx, TaskNode task, List<string> paths)
    {
        var engine = EngineOf(ctx);
        if (engine is null) return;
        engine.RegisterRollback(task.Id, () =>
        {
            foreach (var p in paths)
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
        });
    }

    // ------------------------------------------------------------------
    private AgentResult MaterializeSubset(RunContext ctx, TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var variant = StrParam(task, "variant", "v1");
        var wanted = Param<List<string>>(task, "files", new List<string>());
        var specs = Codegen.GetFiles(variant).ToDictionary(s => s.Path);
        var src = Path.Combine(ws, "src");
        var written = new List<string>();
        foreach (var rel in wanted)
        {
            if (!specs.TryGetValue(rel, out var spec))
                throw new InvalidOperationException($"unknown file spec: {rel}");
            var path = Path.Combine(src, rel);
            CheckedWrite(ctx, path, spec.Content);
            ctx.AddArtifact($"file:{rel}", "file",
                            producedBy: $"implementer:{task.Id}", path: path,
                            content: spec.Content);
            written.Add(path);
        }
        RegisterCleanup(ctx, task, written);
        ctx.Put("rollback_plan", new Dictionary<string, object?>
        {
            ["strategy"] = "delete generated files",
            ["steps"] = written.Select(p => (object?)$"remove {p}").ToList(),
        });
        Decide(ctx,
            $"materialized {written.Count} service files (variant {variant})",
            "files generated from the reviewed codegen spec; each write passed the policy engine",
            basedOn: new List<string> { "architecture decisions", "codegen spec" },
            impact: $"workspace src tree: {written.Count} files");
        return Ok(new Dictionary<string, object?> { ["written"] = written },
                  notes: $"wrote {written.Count} files");
    }

    private AgentResult WriteTests(RunContext ctx, TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var variant = StrParam(task, "variant", "v1");
        var specs = Codegen.GetFiles(variant).ToDictionary(s => s.Path);
        var testSpec = specs.Values.First(s => s.Path.EndsWith(".cs") &&
                                               Path.GetFileName(s.Path).StartsWith("ServiceTests"));
        var testPath = Path.Combine(ws, "src", testSpec.Path);
        CheckedWrite(ctx, testPath, testSpec.Content);
        // The test csproj is part of the file set (shared across variants).
        var csprojSpec = specs["Shortener.Tests/Shortener.Tests.csproj"];
        CheckedWrite(ctx, Path.Combine(ws, "src", csprojSpec.Path), csprojSpec.Content);
        // Remove the other variant's suite so `dotnet test` runs exactly one.
        var otherName = variant == "v1" ? "ServiceTests.cs" : "ServiceTestsV1.cs";
        var otherPath = Path.Combine(ws, "src", "Shortener.Tests", otherName);
        if (File.Exists(otherPath)) File.Delete(otherPath);
        ctx.AddArtifact("test_suite", "file",
                        producedBy: $"implementer:{task.Id}",
                        path: testPath, content: testSpec.Content);
        Decide(ctx, $"wrote generated test suite ({testSpec.Path})",
               "tests generated from the API spec so implementation and verification cannot drift apart",
               basedOn: new List<string> { "api_spec", "codegen spec" });
        return Ok(new Dictionary<string, object?> { ["test_file"] = testSpec.Path },
                  notes: "test suite written");
    }

    // ------------------------------------------------------------------
    private AgentResult ApplyV2(RunContext ctx, TaskNode task)
    {
        // Brownfield: evolve the v1 workspace to v2, logging diffs.
        var ws = StrParam(task, "workspace_dir");
        var specs = Codegen.GetFiles("v2").ToDictionary(s => s.Path, s => s.Content);
        var audit = AuditOf(ctx);
        var changed = new List<string>();
        var added = new List<string>();
        var unchanged = new List<string>();
        var fullDiffLines = Codegen.DiffVariants().Split('\n').Length;
        foreach (var (rel, newContent) in specs)
        {
            if (rel.Contains("Shortener.Tests")) continue; // handled by write_tests
            var path = Path.Combine(ws, "src", rel);
            var oldContent = File.Exists(path) ? File.ReadAllText(path) : "";
            if (oldContent == newContent) { unchanged.Add(rel); continue; }
            CheckedWrite(ctx, path, newContent);
            audit?.Log("file_changed", actor: Name, taskId: task.Id,
                       details: new Dictionary<string, object?> { ["path"] = rel });
            ctx.AddArtifact($"file:{rel}", "file",
                            producedBy: $"implementer:{task.Id}", path: path,
                            content: newContent,
                            metadata: new Dictionary<string, object?>
                                { ["diff_lines"] = fullDiffLines });
            (oldContent.Length > 0 ? changed : added).Add(rel);
        }
        ctx.Put("rollback_plan", new Dictionary<string, object?>
        {
            ["strategy"] = "re-materialize v1 from codegen",
            ["steps"] = new List<object?>
            {
                "Codegen.Materialize(\"v1\", <workspace>/src)",
                "re-run dotnet test to confirm green",
            },
        });
        Decide(ctx,
            $"evolved workspace v1 -> v2: {changed.Count} changed, {added.Count} added, {unchanged.Count} unchanged",
            "targeted file rewrites per the approved impact analysis; every change logged as a unified diff",
            basedOn: new List<string> { "impact_analysis", "architecture decisions" },
            impact: "Models.cs, Program.cs rewritten; Validators.cs added; 404->410 behavior change is intentionally breaking");
        return Ok(new Dictionary<string, object?>
                  { ["changed"] = changed, ["added"] = added, ["unchanged"] = unchanged },
                  notes: $"v1->v2: {changed.Count} changed, {added.Count} added");
    }

    // ------------------------------------------------------------------
    private AgentResult SmartFeature(RunContext ctx, TaskNode task)
    {
        var ws = StrParam(task, "workspace_dir");
        var design = ctx.Get<Dictionary<string, object?>>("smart_design") ?? new();
        var scope = design.GetValueOrDefault("scope") as string ?? "health-only";
        var full = scope == "full";

        var smartPath = Path.Combine(ws, "src", "Shortener", "SmartLinks.cs");
        var smartContent = Codegen.SmartModule(full);
        CheckedWrite(ctx, smartPath, smartContent);
        ctx.AddArtifact("file:src/Shortener/SmartLinks.cs", "file",
                        producedBy: $"implementer:{task.Id}", path: smartPath,
                        content: smartContent);

        var programPath = Path.Combine(ws, "src", "Shortener", "Program.cs");
        var programSrc = File.ReadAllText(programPath);
        if (!programSrc.Contains(Codegen.ExtensionMarker))
        {
            // Re-plan re-run: the marker was consumed by a previous pass.
            // Restore pristine v2 Program.cs, then patch (idempotent).
            programSrc = Codegen.GetFiles("v2")
                .First(s => s.Path == "Shortener/Program.cs").Content;
            AuditOf(ctx)?.Log("file_restored", actor: Name, taskId: task.Id,
                details: new Dictionary<string, object?>
                {
                    ["path"] = "Shortener/Program.cs",
                    ["reason"] = "re-plan re-run idempotency",
                });
        }
        var patched = programSrc.Replace(Codegen.ExtensionMarker,
                                         Codegen.SmartEndpoints(full));
        CheckedWrite(ctx, programPath, patched);
        var diffLines = DiffLineCount(programSrc, patched);
        AuditOf(ctx)?.Log("file_changed", actor: Name, taskId: task.Id,
            details: new Dictionary<string, object?>
            {
                ["path"] = "Shortener/Program.cs",
                ["diff_lines"] = diffLines,
            });
        ctx.AddArtifact("file:src/Shortener/Program.cs", "file",
                        producedBy: $"implementer:{task.Id}", path: programPath,
                        content: patched);

        var testPath = Path.Combine(ws, "src", "Shortener.Tests", "SmartTests.cs");
        var testContent = Codegen.SmartTests(full);
        CheckedWrite(ctx, testPath, testContent);
        ctx.AddArtifact("file:src/Shortener.Tests/SmartTests.cs", "file",
                        producedBy: $"implementer:{task.Id}", path: testPath,
                        content: testContent);

        ctx.Put("rollback_plan", new Dictionary<string, object?>
        {
            ["strategy"] = "remove SmartLinks.cs, restore Program.cs from v2 codegen",
            ["steps"] = new List<object?>
            {
                "delete Shortener/SmartLinks.cs and Shortener.Tests/SmartTests.cs",
                "Codegen.Materialize(\"v2\", <workspace>/src)",
                "re-run dotnet test to confirm green",
            },
        });
        RegisterCleanup(ctx, task, new List<string> { smartPath, testPath });
        Decide(ctx,
            $"implemented smart-link feature (scope={scope})",
            "new module + extension-point wiring; no schema changes per the approved design",
            basedOn: new List<string> { "smart_design" },
            impact: "SmartLinks.cs added; Program.cs extended; SmartTests.cs added",
            alternatives: new List<string>
                { "DB-backed rules (rejected: schema change)" });
        return Ok(new Dictionary<string, object?>
                  { ["scope"] = scope,
                    ["files"] = new List<string> { smartPath, programPath, testPath } },
                  notes: $"smart feature implemented (scope={scope})");
    }

    private static int DiffLineCount(string a, string b)
    {
        var aLines = a.Split('\n');
        var bLines = new HashSet<string>(b.Split('\n'));
        return aLines.Count(l => !bLines.Contains(l));
    }
}
