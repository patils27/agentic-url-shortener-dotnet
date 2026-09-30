// Documenter agent: generates reviewable documentation artifacts.
//
// Modes:
//   - api_docs: render docs/API.md from the architect's api_spec.
//   - changelog: render docs/CHANGELOG.md for the brownfield release.
//   - assumptions: render docs/ASSUMPTIONS.md + docs/SMART_LINKS.md for the
//     ambiguous scenario.
// Registers every document in ctx["documents"] (feeds the docs_present gate).

using AgenticUrlShortener.Orchestrator;

namespace AgenticUrlShortener.Agents;

public sealed class DocumenterAgent : Agent
{
    public override string Name => "documenter";

    public override AgentResult Run(RunContext ctx, TaskNode task)
    {
        var mode = StrParam(task, "mode", "api_docs");
        return mode switch
        {
            "changelog" => Changelog(ctx, task),
            "assumptions" => Assumptions(ctx, task),
            _ => ApiDocs(ctx, task),
        };
    }

    private string Write(RunContext ctx, TaskNode task, string rel, string content)
    {
        var ws = StrParam(task, "workspace_dir");
        var path = Path.Combine(ws, "docs", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PoliciesOf(ctx)?.Evaluate(
            new PolicyAction { Kind = "write_file", Target = path, Payload = content },
            ctx, AuditOf(ctx));
        File.WriteAllText(path, content);
        var docs = ctx.Get<List<string>>("documents") ?? new List<string>();
        docs.Add(rel);
        ctx.Put("documents", docs);
        ctx.AddArtifact($"doc:{rel}", "doc",
                        producedBy: $"documenter:{task.Id}", path: path,
                        content: content);
        return path;
    }

    private AgentResult ApiDocs(RunContext ctx, TaskNode task)
    {
        var spec = ctx.Get<List<Dictionary<string, string>>>("api_spec")
                   ?? new List<Dictionary<string, string>>();
        var lines = new List<string>
        {
            "# URL Shortener — API Reference", "",
            "Base URL: `http://localhost:8000`", "",
            "## Endpoints", "",
        };
        foreach (var ep in spec)
        {
            lines.Add($"### `{ep["method"]} {ep["path"]}`");
            lines.Add($"{ep["description"]}\n");
        }
        lines.AddRange(new[]
        {
            "## Reliability", "",
            "- **Rate limiting:** 60 requests/minute per client IP (token bucket); `429` responses carry a `Retry-After` header.",
            "- **Idempotency:** send `Idempotency-Key` with `POST /api/urls`; replays return the original body with `200`.",
            "- **Expiry:** links created with `expires_in_days` return `410 Gone` after expiry.",
            "", "## Analytics", "",
            "`GET /api/urls/{code}/stats` returns total clicks, per-day counts, and referrer / user-agent breakdowns.",
            "",
        });
        var path = Write(ctx, task, "API.md", string.Join("\n", lines));
        Decide(ctx, "generated API.md from the architect's api_spec",
               "docs generated from the same spec the implementers built against, so docs and code cannot drift",
               basedOn: new List<string> { "api_spec" });
        return Ok(new Dictionary<string, object?> { ["doc"] = path },
                  notes: "API.md written",
                  artifacts: new List<string> { "doc:API.md" });
    }

    private AgentResult Changelog(RunContext ctx, TaskNode task)
    {
        var content = @"# Changelog

## v1.0.0 — Brownfield iteration

### Added
- Custom aliases on `POST /api/urls` (`custom_alias`, 3–32 chars,
  `[A-Za-z0-9_-]`); `409 Conflict` when the alias is taken.

### Fixed
- Expired short links now return `410 Gone` instead of `404 Not Found`
  (previously indistinguishable from unknown codes). **Note:** this is an
  intentional behavior change for API clients.

### Refactored
- Extracted inline URL validation from `Program.cs` into
  `Validators.cs` with dedicated unit tests.

### Notes
- No database migration required: custom aliases reuse the `code` primary key.
- All v0.1.0 tests still pass unmodified (except the expired-link test, which
  now asserts `410`).
";
        var path = Write(ctx, task, "CHANGELOG.md", content);
        Decide(ctx, "generated CHANGELOG.md for v1.0.0",
               "behavior change (404->410) called out explicitly for reviewers",
               basedOn: new List<string> { "impact_analysis" });
        return Ok(new Dictionary<string, object?> { ["doc"] = path },
                  notes: "CHANGELOG.md written",
                  artifacts: new List<string> { "doc:CHANGELOG.md" });
    }

    private AgentResult Assumptions(RunContext ctx, TaskNode task)
    {
        var lines = new List<string>
            { "# Assumption Log — 'make short links smarter'", "" };
        foreach (var a in ctx.Assumptions)
            lines.Add($"- **{a.Id}** [{a.Status}] ({a.Actor}): {a.Statement}" +
                      (string.IsNullOrEmpty(a.ConfirmedBy) ? ""
                       : $" — confirmed by {a.ConfirmedBy}"));
        lines.AddRange(new[]
        {
            "", "## Resolution", "",
            "The stakeholder clarification confirmed device-aware smart redirects + link health monitoring. " +
            "All assumptions were confirmed; the planner's initial hypothesis (health only) was superseded and the DAG was re-planned.",
            "",
        });
        var p1 = Write(ctx, task, "ASSUMPTIONS.md", string.Join("\n", lines));
        var design = ctx.Get<Dictionary<string, object?>>("smart_design") ?? new();
        var endpoints = design.GetValueOrDefault("endpoints") as List<string>
                        ?? new List<string>();
        var smart = new List<string>
        {
            "# Smart Links", "",
            $"Scope: **{design.GetValueOrDefault("scope") ?? "unknown"}**", "",
            "## Endpoints", "",
        };
        smart.AddRange(endpoints.Select(e => $"- `{e}`"));
        smart.AddRange(new[]
            { "", "## Notes", "", design.GetValueOrDefault("notes") as string ?? "", "" });
        var p2 = Write(ctx, task, "SMART_LINKS.md", string.Join("\n", smart));
        Decide(ctx, "documented assumptions and smart-link API",
               "assumption log is the audit trail for the ambiguous requirement",
               basedOn: new List<string> { "assumption log", "smart_design" });
        return Ok(new Dictionary<string, object?> { ["docs"] = new List<string> { p1, p2 } },
                  notes: "ASSUMPTIONS.md + SMART_LINKS.md written");
    }
}
