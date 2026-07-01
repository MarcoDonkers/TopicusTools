using System.Text;
using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

public class HtmlReportGenerator
{
    public string Generate(List<ComparisonResult> results, List<string> fetchErrors,
        List<(string PairKey, List<ComparisonResult> AppResults)>? webAppResults = null)
    {
        var sb = new StringBuilder();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var envNames = results.SelectMany(r => new[] { r.LeftEnvironment, r.RightEnvironment })
                              .Concat(fetchErrors.Select(e => e.Split(':')[0]))
                              .Distinct().OrderBy(x => x).ToList();

        sb.AppendLine($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>IIS Config Comparison — {{timestamp}}</title>
              <style>
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                body { font-family: 'Segoe UI', system-ui, sans-serif; background: #f0f2f5; color: #1a1a2e; }
                header { background: #1a1a2e; color: #fff; padding: 24px 32px; }
                header h1 { font-size: 1.5rem; font-weight: 600; }
                header p  { font-size: 0.85rem; color: #aab; margin-top: 4px; }
                .container { max-width: 1200px; margin: 32px auto; padding: 0 24px; }
                .summary-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px,1fr)); gap: 16px; margin-bottom: 32px; }
                .summary-card { background: #fff; border-radius: 10px; padding: 20px 24px;
                                box-shadow: 0 1px 4px rgba(0,0,0,.08); border-left: 4px solid #ccc; }
                .summary-card.identical { border-left-color: #22c55e; }
                .summary-card.differs   { border-left-color: #f59e0b; }
                .summary-card.error     { border-left-color: #ef4444; }
                .summary-card h3 { font-size: 1rem; margin-bottom: 6px; }
                .summary-card .badge { display: inline-block; border-radius: 999px; padding: 2px 10px;
                                       font-size: 0.75rem; font-weight: 600; color: #fff; }
                .badge.green  { background: #22c55e; }
                .badge.amber  { background: #f59e0b; }
                .badge.red    { background: #ef4444; }
                .comparison { background: #fff; border-radius: 10px; margin-bottom: 28px;
                              box-shadow: 0 1px 4px rgba(0,0,0,.08); overflow: hidden; }
                .comparison-header { background: #1a1a2e; color: #fff; padding: 16px 24px;
                                     display: flex; align-items: center; gap: 12px; }
                .comparison-header h2 { font-size: 1rem; font-weight: 600; }
                .comparison-header .env-badge { background: rgba(255,255,255,.15); border-radius: 6px;
                                                padding: 3px 10px; font-size: 0.8rem; }
                .comparison-header .arrow { opacity: .6; }
                .identical-msg { padding: 20px 24px; color: #16a34a; font-weight: 500;
                                 display: flex; align-items: center; gap: 8px; }
                .section-block { border-top: 1px solid #f0f0f0; }
                .section-title { padding: 12px 24px; background: #f8f9fb; font-size: 0.78rem;
                                 font-weight: 700; color: #555; text-transform: uppercase;
                                 letter-spacing: .05em; cursor: pointer; user-select: none;
                                 display: flex; justify-content: space-between; align-items: center; }
                .section-title:hover { background: #f0f2f5; }
                .section-count { background: #e2e8f0; border-radius: 999px; padding: 1px 8px;
                                 font-size: 0.72rem; color: #334155; }
                .diff-table { width: 100%; border-collapse: collapse; font-size: 0.82rem; }
                .diff-table td { padding: 9px 24px; border-bottom: 1px solid #f5f5f5;
                                 vertical-align: top; word-break: break-all; }
                .diff-table tr:last-child td { border-bottom: none; }
                .diff-table .kind { width: 130px; font-weight: 600; white-space: nowrap; }
                .kind-left  { color: #dc2626; }
                .kind-right { color: #2563eb; }
                .kind-attr  { color: #d97706; }
                .val-old { color: #dc2626; text-decoration: line-through; opacity: .8; }
                .val-new { color: #16a34a; }
                .path { color: #334155; font-family: 'Consolas', monospace; font-size: 0.8rem; }
                .attr-name { color: #7c3aed; font-family: 'Consolas', monospace; font-weight: 600; }
                .legend { display: flex; gap: 20px; padding: 16px 24px; font-size: 0.78rem;
                          background: #f8f9fb; border-top: 1px solid #eee; flex-wrap: wrap; }
                .legend-item { display: flex; align-items: center; gap: 6px; }
                .legend-dot { width: 10px; height: 10px; border-radius: 50%; }
                footer { text-align: center; padding: 32px; color: #999; font-size: 0.78rem; }
                .section-heading { font-size: 1.15rem; font-weight: 700; color: #1a1a2e;
                                    margin: 40px 0 16px; padding-bottom: 8px;
                                    border-bottom: 2px solid #1a1a2e; }
              </style>
            </head>
            <body>
            <header>
              <h1>IIS Configuration Comparison Report</h1>
              <p>Generated {{timestamp}} &nbsp;·&nbsp; Environments: {{string.Join(", ", envNames)}}</p>
            </header>
            <div class="container">
            """);

        // Summary cards
        sb.AppendLine("<div class=\"summary-grid\">");
        foreach (var r in results)
        {
            var cls = r.IsIdentical ? "identical" : "differs";
            var badgeCls = r.IsIdentical ? "green" : "amber";
            var label = r.IsIdentical ? "Identical" : $"{r.Differences.Count} differences";
            sb.AppendLine($"""
                <div class="summary-card {cls}">
                  <h3>{r.LeftEnvironment} ↔ {r.RightEnvironment}</h3>
                  <span class="badge {badgeCls}">{label}</span>
                </div>
                """);
        }
        foreach (var err in fetchErrors)
        {
            sb.AppendLine($"""
                <div class="summary-card error">
                  <h3>{err}</h3>
                  <span class="badge red">Fetch failed</span>
                </div>
                """);
        }
        sb.AppendLine("</div>");

        // Detail blocks
        foreach (var result in results)
        {
            sb.AppendLine($"""
                <div class="comparison">
                  <div class="comparison-header">
                    <span class="env-badge">{result.LeftEnvironment}</span>
                    <span class="arrow">↔</span>
                    <span class="env-badge">{result.RightEnvironment}</span>
                    <h2 style="margin-left:auto; opacity:.7; font-size:.85rem;">
                      {(result.IsIdentical ? "✓ Identical" : $"{result.Differences.Count} differences")}
                    </h2>
                  </div>
                """);

            if (result.IsIdentical)
            {
                sb.AppendLine("<div class=\"identical-msg\">✓ Configurations are identical across all tracked sections.</div>");
            }
            else
            {
                var bySection = result.Differences.GroupBy(d => d.Section);
                foreach (var section in bySection)
                {
                    var sectionId = $"s-{Guid.NewGuid():N}";
                    sb.AppendLine($"""
                        <div class="section-block">
                          <div class="section-title" onclick="toggle('{sectionId}')">
                            <span>{section.Key}</span>
                            <span class="section-count">{section.Count()}</span>
                          </div>
                          <div id="{sectionId}">
                            <table class="diff-table">
                        """);

                    foreach (var diff in section)
                    {
                        var (kindClass, kindLabel) = diff.Kind switch
                        {
                            DifferenceKind.OnlyInLeft  => ("kind-left",  "Only in left"),
                            DifferenceKind.OnlyInRight => ("kind-right", "Only in right"),
                            DifferenceKind.AttributeDifference => ("kind-attr", "Attr differs"),
                            _ => ("kind-attr", "Differs")
                        };

                        string valCell;
                        if (diff.Kind == DifferenceKind.AttributeDifference)
                        {
                            valCell = $"<span class=\"attr-name\">@{HtmlEncode(diff.AttributeName)}</span>&nbsp;"
                                    + $"<span class=\"val-old\">{HtmlEncode(diff.LeftValue)}</span>"
                                    + $"&nbsp;→&nbsp;<span class=\"val-new\">{HtmlEncode(diff.RightValue)}</span>";
                        }
                        else if (diff.Kind == DifferenceKind.OnlyInLeft)
                        {
                            valCell = $"Present in <strong>{HtmlEncode(result.LeftEnvironment)}</strong> only";
                        }
                        else
                        {
                            valCell = $"Present in <strong>{HtmlEncode(result.RightEnvironment)}</strong> only";
                        }

                        // Strip the section prefix from the path for readability
                        var shortPath = diff.ElementPath.Length > diff.Section.Length + 1
                            ? diff.ElementPath[(diff.Section.Length + 1)..]
                            : diff.ElementPath;

                        sb.AppendLine($"""
                            <tr>
                              <td class="kind {kindClass}">{kindLabel}</td>
                              <td class="path">{HtmlEncode(shortPath)}</td>
                              <td>{valCell}</td>
                            </tr>
                            """);
                    }

                    sb.AppendLine("</table></div></div>");
                }

                sb.AppendLine("""
                    <div class="legend">
                      <div class="legend-item"><div class="legend-dot" style="background:#dc2626"></div> Only in left environment</div>
                      <div class="legend-item"><div class="legend-dot" style="background:#2563eb"></div> Only in right environment</div>
                      <div class="legend-item"><div class="legend-dot" style="background:#d97706"></div> Attribute value differs</div>
                    </div>
                    """);
            }

            sb.AppendLine("</div>"); // .comparison
        }

        // ── Web application config section ──────────────────────────────────────
        if (webAppResults is { Count: > 0 })
        {
            sb.AppendLine("""<h2 class="section-heading">App Configuration Differences</h2>""");

            foreach (var (pairKey, appResults) in webAppResults)
            {
                if (appResults.Count == 0)
                {
                    sb.AppendLine($"""
                        <div class="comparison">
                          <div class="comparison-header">
                            <span class="env-badge">{HtmlEncode(pairKey.Split('↔')[0].Trim())}</span>
                            <span class="arrow">↔</span>
                            <span class="env-badge">{HtmlEncode(pairKey.Split('↔').LastOrDefault()?.Trim())}</span>
                            <h2 style="margin-left:auto; opacity:.7; font-size:.85rem;">✓ All app configs identical</h2>
                          </div>
                          <div class="identical-msg">✓ No non-environment differences found across all applications.</div>
                        </div>
                        """);
                    continue;
                }

                // Group all app differences by app name (= Section)
                var byApp = appResults
                    .SelectMany(r => r.Differences.Select(d => (d, r)))
                    .GroupBy(x => x.d.Section)
                    .OrderBy(g => g.Key);

                var leftEnv  = appResults.First().LeftEnvironment;
                var rightEnv = appResults.First().RightEnvironment;
                var totalDiffs = appResults.Sum(r => r.Differences.Count);

                sb.AppendLine($"""
                    <div class="comparison">
                      <div class="comparison-header">
                        <span class="env-badge">{HtmlEncode(leftEnv)}</span>
                        <span class="arrow">↔</span>
                        <span class="env-badge">{HtmlEncode(rightEnv)}</span>
                        <h2 style="margin-left:auto; opacity:.7; font-size:.85rem;">{totalDiffs} app config difference(s)</h2>
                      </div>
                    """);

                foreach (var appGroup in byApp)
                {
                    var appName = appGroup.Key;
                    var diffs = appGroup.Select(x => x.d).ToList();
                    var sectionId = $"wa-{Guid.NewGuid():N}";

                    sb.AppendLine($"""
                        <div class="section-block">
                          <div class="section-title" onclick="toggle('{sectionId}')">
                            <span>{HtmlEncode(appName)}</span>
                            <span class="section-count">{diffs.Count}</span>
                          </div>
                          <div id="{sectionId}">
                            <table class="diff-table">
                        """);

                    foreach (var diff in diffs)
                    {
                        var (kindClass, kindLabel) = diff.Kind switch
                        {
                            DifferenceKind.OnlyInLeft  => ("kind-left",  "Only in left"),
                            DifferenceKind.OnlyInRight => ("kind-right", "Only in right"),
                            DifferenceKind.AttributeDifference => ("kind-attr", "Value differs"),
                            _ => ("kind-attr", "Differs")
                        };

                        // Strip the app name prefix from ElementPath for display
                        var shortPath = diff.ElementPath.StartsWith(appName + "/", StringComparison.OrdinalIgnoreCase)
                            ? diff.ElementPath[(appName.Length + 1)..]
                            : diff.ElementPath;

                        string valCell = diff.Kind == DifferenceKind.AttributeDifference
                            ? $"<span class=\"val-old\">{HtmlEncode(diff.LeftValue)}</span>"
                              + $"&nbsp;→&nbsp;<span class=\"val-new\">{HtmlEncode(diff.RightValue)}</span>"
                            : diff.Kind == DifferenceKind.OnlyInLeft
                                ? $"<span class=\"val-old\">{HtmlEncode(diff.LeftValue)}</span> (present in <strong>{HtmlEncode(leftEnv)}</strong> only)"
                                : $"<span class=\"val-new\">{HtmlEncode(diff.RightValue)}</span> (present in <strong>{HtmlEncode(rightEnv)}</strong> only)";

                        sb.AppendLine($"""
                            <tr>
                              <td class="kind {kindClass}">{kindLabel}</td>
                              <td class="path">{HtmlEncode(shortPath)}</td>
                              <td>{valCell}</td>
                            </tr>
                            """);
                    }

                    sb.AppendLine("</table></div></div>");
                }

                sb.AppendLine("""
                    <div class="legend">
                      <div class="legend-item"><div class="legend-dot" style="background:#dc2626"></div> Only in left environment</div>
                      <div class="legend-item"><div class="legend-dot" style="background:#2563eb"></div> Only in right environment</div>
                      <div class="legend-item"><div class="legend-dot" style="background:#d97706"></div> Value differs between environments</div>
                    </div>
                    """);

                sb.AppendLine("</div>"); // .comparison
            }
        }

        sb.AppendLine("""
            </div>
            <footer>IIS Configuration Comparer &nbsp;·&nbsp; report generated automatically</footer>
            <script>
              function toggle(id) {
                const el = document.getElementById(id);
                el.style.display = el.style.display === 'none' ? '' : 'none';
              }
            </script>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    private static string HtmlEncode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
