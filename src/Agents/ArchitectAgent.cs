// Architect agent: system design and brownfield impact analysis.
//
// Modes:
//   - design: record architecture decisions (ADRs) for the greenfield build
//     and publish the API spec the implementers and documenter build against.
//   - brownfield_impact: scan the baseline codebase, build a module
//     dependency graph (via using-directive analysis), and determine
//     impacted modules for each planned change.
//   - smart_design: design the smart-link feature from the (possibly
//     clarified) normalized requirement.

using System.Text.RegularExpressions;
using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class ArchitectAgent : Agent
{
    public override string Name => "architect";

    public override AgentResult Run(RunContext ctx, TaskNode task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = StrParam(task, "mode", "design");
        return mode switch
        {
            "brownfield_impact" => BrownfieldImpact(ctx, task, cancellationToken),
            "smart_design" => SmartDesign(ctx, task, cancellationToken),
            _ => Design(ctx, task, cancellationToken),
        };
    }

    // ------------------------------------------------------------------
    private AgentResult Design(RunContext ctx, TaskNode task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ws = StrParam(task, "workspace_dir");
        var decisions = new (string Title, string Rationale, string[] Alts, string Impact)[]
        {
            ("Use ASP.NET Core minimal APIs",
             "low ceremony, built-in JSON, first-class testability via WebApplicationFactory; team standard for .NET HTTP services",
             new[] { "Controllers", "Nancy" }, "all HTTP handling"),
            ("SQLite via Microsoft.Data.Sqlite (no ORM)",
             "single-file DB, zero extra deps, sufficient for prototype throughput; schema is tiny and stable",
             new[] { "PostgreSQL", "EF Core" }, "persistence layer"),
            ("Token-bucket rate limiting per client IP (in-memory)",
             "simple, no external store; 60 req/min default protects the redirect path which is the hot path",
             new[] { "Redis-backed limiter" }, "abuse protection"),
            ("Idempotency-Key header on POST /api/urls",
             "safe retries for clients; stored response replayed with 200",
             new[] { "no idempotency" }, "write API contract"),
            ("Click analytics as separate table + pure aggregation functions",
             "keeps write path fast; analytics module has no I/O so it is trivially unit-testable",
             new[] { "analytics in SQL views" }, "analytics module"),
            ("Catch-all /{code} route registered last",
             "prevents shadowing /health, /ready and /api/* routes",
             new[] { "path prefix like /r/{code}" }, "routing correctness"),
        };
        foreach (var (title, rationale, alts, impact) in decisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Decide(ctx, cancellationToken, title, rationale, basedOn: new List<string> { "normalized requirement" },
                   impact: impact, alternatives: alts.ToList());
        }

        var apiSpec = new List<Dictionary<string, string>>
        {
            new() { ["method"] = "POST", ["path"] = "/api/urls",
                    ["description"] = "Create a short URL (7-char code, optional expiry). Supports Idempotency-Key." },
            new() { ["method"] = "GET", ["path"] = "/api/urls",
                    ["description"] = "List all short URLs with click counts." },
            new() { ["method"] = "GET", ["path"] = "/api/urls/{code}",
                    ["description"] = "Get one short URL." },
            new() { ["method"] = "DELETE", ["path"] = "/api/urls/{code}",
                    ["description"] = "Delete a short URL and its clicks." },
            new() { ["method"] = "GET", ["path"] = "/api/urls/{code}/stats",
                    ["description"] = "Click analytics: totals, per-day, referrer and user-agent breakdowns." },
            new() { ["method"] = "GET", ["path"] = "/{code}",
                    ["description"] = "307 redirect to the destination; records a click." },
            new() { ["method"] = "GET", ["path"] = "/health",
                    ["description"] = "Liveness probe." },
            new() { ["method"] = "GET", ["path"] = "/ready",
                    ["description"] = "Readiness probe incl. DB check." },
        };
        var architecture = new Dictionary<string, object?>
        {
            ["modules"] = new List<string>
            {
                "src/Shortener/Program.cs (HTTP + routing)",
                "src/Shortener/UrlStore.cs", "src/Shortener/Models.cs",
                "src/Shortener/ClickAnalytics.cs", "src/Shortener/RateLimiter.cs",
            },
            ["api_spec"] = apiSpec,
            ["data_model"] = new List<string>
            {
                "urls(code PK, url, created_at, expires_at)",
                "clicks(id, code, ts, referrer, user_agent, ip)",
                "idempotency(key PK, body, created_at)",
            },
        };
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("architecture", architecture);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("api_spec", apiSpec);
        var md = new List<string> { "# Architecture Decisions (greenfield)", "" };
        var i = 1;
        foreach (var (title, rationale, alts, impact) in decisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            md.Add($"## ADR-{i:00}: {title}");
            md.Add($"- Rationale: {rationale}");
            md.Add($"- Alternatives: {string.Join(", ", alts)}");
            md.Add($"- Impact: {impact}\n");
            i++;
        }
        var docPath = Path.Combine(ws, "docs", "ARCHITECTURE_NOTES.md");
        WriteFile(ctx, docPath, string.Join("\n", md), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.AddArtifact("architecture_decisions", "doc",
                        producedBy: $"architect:{task.Id}", path: docPath,
                        content: string.Join("\n", md));
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(new Dictionary<string, object?> { ["architecture"] = architecture },
                  notes: $"recorded {decisions.Length} ADRs",
                  artifacts: new List<string> { "architecture_decisions" });
    }

    // ------------------------------------------------------------------
    private static Dictionary<string, HashSet<string>> ModuleGraph(string srcDir,
                                                                  CancellationToken cancellationToken)
    {
        // Parse C# `using X;` / `using static X;` directives into a module
        // dependency graph keyed by file name (without extension).
        var graph = new Dictionary<string, HashSet<string>>();
        if (!Directory.Exists(srcDir)) return graph;
        foreach (var path in Directory.GetFiles(srcDir, "*.cs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = Path.GetFileNameWithoutExtension(path);
            var deps = new HashSet<string>();
            string text;
            try { text = File.ReadAllTextAsync(path, cancellationToken).GetAwaiter().GetResult(); }
            catch (IOException) { text = ""; }
            foreach (Match m in Regex.Matches(text, @"^\s*using\s+(?:static\s+)?([\w\.]+)\s*;",
                                              RegexOptions.Multiline))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ns = m.Groups[1].Value;
                var leaf = ns.Split('.').Last();
                if (leaf != mod) deps.Add(leaf);
            }
            graph[mod] = deps;
        }
        return graph;
    }

    private AgentResult BrownfieldImpact(RunContext ctx, TaskNode task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ws = StrParam(task, "workspace_dir");
        var src = Path.Combine(ws, "src", "Shortener");
        var graph = ModuleGraph(src, cancellationToken);

        var changes = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["change"] = "custom aliases on creation",
                ["touches"] = new List<string> { "Models", "Program" },
                ["reason"] = "new request field + alias reservation logic in the create handler",
            },
            new()
            {
                ["change"] = "bug fix: expired links 404 -> 410",
                ["touches"] = new List<string> { "Program" },
                ["reason"] = "one-line status change in the redirect handler",
            },
            new()
            {
                ["change"] = "refactor: extract Validators.cs",
                ["touches"] = new List<string> { "Program", "Validators" },
                ["reason"] = "move inline URL validation out of Program.cs into a shared, unit-testable module",
            },
        };
        var importers = graph.Keys.ToDictionary(m => m, _ => new HashSet<string>());
        foreach (var (mod, deps) in graph)
            foreach (var d in deps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!importers.TryGetValue(d, out var set))
                {
                    set = new HashSet<string>();
                    importers[d] = set;
                }
                set.Add(mod);
            }
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var impacted = new HashSet<string>((List<string>)change["touches"]!);
            foreach (var mod in (List<string>)change["touches"]!)
                if (importers.TryGetValue(mod, out var set))
                    foreach (var x in set) impacted.Add(x);
            change["impacted_modules"] = impacted.OrderBy(x => x).ToList();
        }
        var risks = new List<string>
        {
            "404->410 changes observable API behavior: clients branching on 404 for expired links will see 410 (intended, but breaking).",
            "Validators.cs extraction must keep error messages compatible so existing 422 assertions still pass.",
            "No schema migration: custom alias reuses the code PK, so the change is backward compatible at the storage layer.",
        };
        var analysis = new Dictionary<string, object?>
        {
            ["module_graph"] = graph.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.OrderBy(x => x).ToList()),
            ["changes"] = changes,
            ["risks"] = risks,
        };
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("architecture", new Dictionary<string, object?> { ["brownfield_changes"] = changes });
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("impact_analysis", analysis);
        Decide(ctx, cancellationToken,
            "approved brownfield change set: aliases + 410 fix + validators extraction",
            "impact analysis shows blast radius limited to Models/Program (+ new Validators module); no storage migration; risks documented",
            basedOn: new List<string> { "module dependency graph", "planned changes" },
            impact: "files rewritten: Models.cs, Program.cs; new: Validators.cs, tests",
            alternatives: new List<string>
                { "separate alias table (rejected: unnecessary migration)" });

        var md = new List<string> { "# Brownfield Impact Analysis", "", "## Module dependency graph" };
        foreach (var (mod, deps) in graph.OrderBy(kv => kv.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            md.Add($"- `{mod}` uses: {(deps.Count == 0 ? "—" : string.Join(", ", deps.OrderBy(x => x)))}");
        }
        md.Add("\n## Changes and impacted modules");
        foreach (var c in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            md.Add($"### {c["change"]}");
            md.Add($"- Directly touches: {string.Join(", ", (List<string>)c["touches"]!)}");
            md.Add($"- Impacted (incl. importers): {string.Join(", ", (List<string>)c["impacted_modules"]!)}");
            md.Add($"- Reason: {c["reason"]}");
        }
        md.Add("\n## Risks");
        md.AddRange(risks.Select(r => $"- {r}"));
        var docPath = Path.Combine(ws, "docs", "IMPACT_ANALYSIS.md");
        var content = string.Join("\n", md) + "\n";
        WriteFile(ctx, docPath, content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.AddArtifact("impact_analysis", "doc", producedBy: $"architect:{task.Id}",
                        path: docPath, content: content);
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(new Dictionary<string, object?> { ["impact_analysis"] = analysis },
                  notes: "impact analysis complete",
                  artifacts: new List<string> { "impact_analysis" });
    }

    // ------------------------------------------------------------------
    private AgentResult SmartDesign(RunContext ctx, TaskNode task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ws = StrParam(task, "workspace_dir");
        var clarified = ctx.Get("clarified_requirement") is not null;
        var scope = clarified ? "full" : "health-only";
        var endpoints = clarified
            ? new List<string> { "POST /api/smart/classify", "GET /api/urls/{code}/health" }
            : new List<string> { "GET /api/urls/{code}/health" };
        var design = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["modules"] = new List<string>
            {
                "src/Shortener/SmartLinks.cs (new): device classification, smart-target resolution, link health probing",
            },
            ["endpoints"] = endpoints,
            ["notes"] = "no schema changes; pure functions unit-tested; health probe never raises (returns reachable=false)",
        };
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("smart_design", design);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.Put("architecture", new Dictionary<string, object?> { ["smart_links"] = design });
        Decide(ctx, cancellationToken,
            $"designed smart-link feature with scope '{scope}'",
            (clarified ? "full scope" : "hypothesis scope") + " derived from the " +
            (clarified ? "stakeholder-confirmed requirement" : "planner hypothesis"),
            basedOn: new List<string> { clarified ? "clarified_requirement" : "normalized_requirement (hypothesis)" },
            impact: "new module SmartLinks.cs; Program.cs extended at the marked extension point",
            alternatives: new List<string>
            {
                "device rules in DB (rejected: schema change)",
                "JS-based client routing (rejected: server-side keeps API contract)",
            });
        var docPath = Path.Combine(ws, "docs", "SMART_DESIGN.md");
        var content = "# Smart-link Design\n\n" +
                      $"Scope: **{scope}** ({(clarified ? "stakeholder-confirmed" : "planner hypothesis")})\n\n" +
                      "## New endpoints\n" +
                      string.Concat(endpoints.Select(e => $"- `{e}`\n")) +
                      "\n## Notes\n" + design["notes"] + "\n";
        WriteFile(ctx, docPath, content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ctx.AddArtifact("smart_design", "doc", producedBy: $"architect:{task.Id}",
                        path: docPath, content: content);
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(new Dictionary<string, object?> { ["smart_design"] = design },
                  notes: $"smart design recorded (scope={scope})",
                  artifacts: new List<string> { "smart_design" });
    }
}
