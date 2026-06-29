using ATFRerunTool.Models;
using System.Text;

namespace ATFRerunTool.Reporting;

/// <summary>
/// Generates a self-contained HTML report from a completed RerunSession.
/// The report includes a summary table, per-job round history, and full failure details
/// (error messages + stack traces) suitable for feeding into an AI for analysis.
/// </summary>
public static class HtmlReportGenerator
{
    public static string Generate(RerunSession session)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'><head><meta charset='utf-8'>");
        sb.AppendLine("<title>ATF Rerun Report – " + session.SessionId + "</title>");
        AppendStyles(sb);
        AppendScripts(sb);
        sb.AppendLine("</head><body>");

        AppendHeader(sb, session);
        AppendSummaryCards(sb, session);
        AppendSummaryTable(sb, session);
        AppendJobDetails(sb, session);

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string Save(RerunSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"ATF_Report_{session.SessionId}.html");
        File.WriteAllText(path, Generate(session), Encoding.UTF8);
        return path;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private static void AppendHeader(StringBuilder sb, RerunSession session)
    {
        sb.AppendLine("<div class='header'>");
        sb.AppendLine("  <h1>ATF Rerun Report</h1>");
        sb.AppendLine($"  <p>Session: <strong>{session.SessionId}</strong></p>");
        sb.AppendLine($"  <p>Run: <strong>{HtmlEncode(session.RunName)}</strong></p>");
        sb.AppendLine($"  <p>Started: {session.StartedAt:dd-MM-yyyy HH:mm:ss} &nbsp;|&nbsp; " +
                      $"Finished: {session.FinishedAt:dd-MM-yyyy HH:mm:ss} &nbsp;|&nbsp; " +
                      $"Duration: {(session.FinishedAt - session.StartedAt):hh\\:mm\\:ss}</p>");
        sb.AppendLine("</div>");
    }

    private static void AppendSummaryCards(StringBuilder sb, RerunSession session)
    {
        int total = session.Jobs.Count;
        int passed = session.Jobs.Count(j => session.GetVerdict(j) == JobVerdict.Pass);
        int failed = session.Jobs.Count(j => session.GetVerdict(j) == JobVerdict.Fail);

        sb.AppendLine("<div class='cards'>");
        sb.AppendLine($"  <div class='card card-total'><div class='card-num'>{total}</div><div>Jobs Rerun</div></div>");
        sb.AppendLine($"  <div class='card card-pass'><div class='card-num'>{passed}</div><div>✓ Green (was instability)</div></div>");
        sb.AppendLine($"  <div class='card card-fail'><div class='card-num'>{failed}</div><div>✗ Still Failing (real issue)</div></div>");
        sb.AppendLine("</div>");
    }

    private static void AppendSummaryTable(StringBuilder sb, RerunSession session)
    {
        int maxRound = session.AllAttempts.Any() ? session.AllAttempts.Max(a => a.Round) : 0;

        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine("<table>");
        sb.Append("<thead><tr><th>Test ID</th><th>Verdict</th>");
        for (int r = 1; r <= maxRound; r++)
            sb.Append($"<th>Round {r} S</th><th>Round {r} R</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var job in session.Jobs.OrderBy(j => j.TestId))
        {
            var verdict = session.GetVerdict(job);
            var verdictClass = verdict == JobVerdict.Pass ? "pass" : "fail";
            var verdictLabel = verdict == JobVerdict.Pass ? "✓ Green" : "✗ Failing";

            sb.Append($"<tr><td><a href='#job-{HtmlId(job.TestId)}'>{HtmlEncode(job.TestId)}</a></td>");
            sb.Append($"<td class='badge {verdictClass}'>{verdictLabel}</td>");

            for (int r = 1; r <= maxRound; r++)
            {
                var sAttempt = session.AllAttempts.FirstOrDefault(a => a.TestId == job.TestId && a.Variant == TestVariant.State && a.Round == r);
                var rAttempt = session.AllAttempts.FirstOrDefault(a => a.TestId == job.TestId && a.Variant == TestVariant.Regression && a.Round == r);

                sb.Append($"<td>{StatusCell(sAttempt)}</td><td>{StatusCell(rAttempt)}</td>");
            }
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");
    }

    private static string StatusCell(TestRunAttempt? attempt)
    {
        if (attempt == null) return "<span class='na'>–</span>";
        return attempt.Status switch
        {
            RunStatus.Passed => "<span class='pass'>✓ Pass</span>",
            RunStatus.Failed => "<span class='fail'>✗ Fail</span>",
            RunStatus.Error => "<span class='error'>⚠ Error</span>",
            _ => "<span class='na'>–</span>",
        };
    }

    private static void AppendJobDetails(StringBuilder sb, RerunSession session)
    {
        var failingJobs = session.Jobs.Where(j => session.GetVerdict(j) == JobVerdict.Fail).OrderBy(j => j.TestId).ToList();

        if (failingJobs.Count == 0)
        {
            sb.AppendLine("<div class='all-green'><strong>✓ All jobs went green!</strong> No failures to report.</div>");
            return;
        }

        sb.AppendLine("<h2>Failure Details</h2>");
        sb.AppendLine("<p class='detail-note'>Only jobs that are still failing after all rounds are shown below.</p>");

        foreach (var job in failingJobs)
        {
            sb.AppendLine($"<div class='job-card' id='job-{HtmlId(job.TestId)}'>");
            sb.AppendLine($"  <div class='job-title fail'>");
            sb.AppendLine($"    <strong>{HtmlEncode(job.TestId)}</strong>");
            sb.AppendLine($"    <span class='badge fail'>✗ Still Failing</span>");
            sb.AppendLine($"    <span class='job-source'>Jenkins: {HtmlEncode(job.Source)}</span>");
            sb.AppendLine("  </div>");

            var attempts = session.AttemptsForJob(job.TestId).OrderBy(a => a.Round).ThenBy(a => a.Variant);
            foreach (var attempt in attempts)
            {
                var statusClass = attempt.Status switch
                {
                    RunStatus.Passed => "pass",
                    RunStatus.Failed => "fail",
                    RunStatus.Error  => "error",
                    _ => "na",
                };
                var variantLabel = attempt.Variant == TestVariant.State ? "State (S)" : "Regression (R)";

                sb.AppendLine($"  <div class='attempt'>");
                sb.AppendLine($"    <div class='attempt-header {statusClass}'>");
                sb.AppendLine($"      Round {attempt.Round} – {variantLabel} ({attempt.Category}) – {attempt.Status}");
                sb.AppendLine($"      <span class='duration'>{attempt.Duration:mm\\:ss\\.ff}</span>");
                sb.AppendLine("    </div>");

                if (attempt.Failures.Any())
                {
                    sb.AppendLine("    <div class='failures'>");
                    foreach (var failure in attempt.Failures)
                    {
                        sb.AppendLine("      <div class='failure'>");
                        sb.AppendLine($"        <div class='failure-name'>{HtmlEncode(failure.TestName)}</div>");
                        if (!string.IsNullOrWhiteSpace(failure.Message))
                        {
                            sb.AppendLine("        <div class='failure-section'>Error Message:</div>");
                            sb.AppendLine($"        <pre class='failure-message'>{HtmlEncode(failure.Message)}</pre>");
                        }
                        if (failure.ScreenshotPaths.Count > 0)
                        {
                            sb.AppendLine("        <div class='failure-section'>Screenshots:</div>");
                            sb.AppendLine("        <div class='screenshots'>");
                            foreach (var screenshotPath in failure.ScreenshotPaths)
                            {
                                var dataUri = TryBuildDataUri(screenshotPath);
                                if (dataUri is not null)
                                {
                                    sb.AppendLine($"          <div class='screenshot-wrap'>");
                                    sb.AppendLine($"            <div class='screenshot-path'>{HtmlEncode(screenshotPath)}</div>");
                                    sb.AppendLine($"            <img class='screenshot-img' src='{dataUri}' alt='Screenshot' loading='lazy' />");
                                    sb.AppendLine($"          </div>");
                                }
                                else
                                {
                                    sb.AppendLine($"          <div class='screenshot-missing'>Screenshot not found: {HtmlEncode(screenshotPath)}</div>");
                                }
                            }
                            sb.AppendLine("        </div>");
                        }
                        if (!string.IsNullOrWhiteSpace(failure.StackTrace))
                        {
                            sb.AppendLine("        <div class='failure-section'>Stack Trace:</div>");
                            sb.AppendLine($"        <pre class='failure-stacktrace'>{HtmlEncode(failure.StackTrace)}</pre>");
                        }
                        sb.AppendLine("      </div>");
                    }
                    sb.AppendLine("    </div>");
                }
                sb.AppendLine("  </div>");
            }

            sb.AppendLine("</div>");
        }
    }

    private static void AppendStyles(StringBuilder sb)
    {
        sb.AppendLine(@"<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: 'Segoe UI', system-ui, sans-serif; background: #f0f2f5; color: #1a1a2e; padding: 24px; }
  h1 { font-size: 1.8rem; margin-bottom: 4px; }
  h2 { font-size: 1.3rem; margin: 28px 0 12px; border-bottom: 2px solid #ddd; padding-bottom: 6px; }
  .header { background: #1a1a2e; color: #fff; border-radius: 8px; padding: 20px 24px; margin-bottom: 24px; }
  .header p { margin-top: 6px; font-size: 0.9rem; color: #ccc; }
  .header strong { color: #fff; }
  code { background: rgba(255,255,255,0.15); padding: 2px 6px; border-radius: 4px; font-size: 0.85em; }

  /* Cards */
  .cards { display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 24px; }
  .card { background: #fff; border-radius: 8px; padding: 16px 24px; min-width: 140px; text-align: center; box-shadow: 0 1px 4px rgba(0,0,0,.1); }
  .card-num { font-size: 2.4rem; font-weight: 700; }
  .card-total .card-num { color: #555; }
  .card-pass .card-num { color: #16a34a; }
  .card-fail .card-num { color: #dc2626; }

  /* Table */
  table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 4px rgba(0,0,0,.1); margin-bottom: 24px; }
  thead { background: #1a1a2e; color: #fff; }
  th, td { padding: 10px 14px; text-align: left; font-size: 0.88rem; border-bottom: 1px solid #eee; }
  tr:last-child td { border-bottom: none; }
  tr:hover td { background: #f9fafb; }
  a { color: #2563eb; text-decoration: none; }
  a:hover { text-decoration: underline; }

  /* Status badges & cells */
  .badge { display: inline-block; padding: 2px 10px; border-radius: 12px; font-size: 0.8rem; font-weight: 600; }
  .pass, span.pass { color: #16a34a; }
  .fail, span.fail { color: #dc2626; }
  .error, span.error { color: #7c3aed; }
  .na { color: #9ca3af; }
  .badge.pass { background: #dcfce7; color: #16a34a; }
  .badge.fail { background: #fee2e2; color: #dc2626; }

  .all-green { background: #dcfce7; border: 1px solid #86efac; border-radius: 8px; padding: 20px 24px; font-size: 1.1rem; color: #15803d; margin-bottom: 24px; }
  .detail-note { color: #6b7280; font-size: 0.88rem; margin-bottom: 16px; }

  /* Job details cards */
  .job-card { background: #fff; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,.1); margin-bottom: 20px; overflow: hidden; }
  .job-title { display: flex; align-items: center; gap: 12px; padding: 12px 16px; font-size: 1rem; }
  .job-title.pass { border-left: 4px solid #16a34a; }
  .job-title.flaky { border-left: 4px solid #d97706; }
  .job-title.fail { border-left: 4px solid #dc2626; }
  .job-source { margin-left: auto; font-size: 0.78rem; color: #6b7280; font-family: monospace; }

  .attempt { border-top: 1px solid #f0f0f0; }
  .attempt-header { display: flex; align-items: center; padding: 8px 16px; font-size: 0.88rem; font-weight: 600; gap: 8px; }
  .attempt-header.pass { background: #f0fdf4; color: #15803d; }
  .attempt-header.fail { background: #fff1f2; color: #b91c1c; }
  .attempt-header.error { background: #faf5ff; color: #7c3aed; }
  .duration { margin-left: auto; font-size: 0.8rem; font-weight: normal; color: #6b7280; }

  .failures { padding: 12px 16px; }
  .failure { margin-bottom: 16px; padding: 12px; background: #fff8f8; border: 1px solid #fecaca; border-radius: 6px; }
  .failure:last-child { margin-bottom: 0; }
  .failure-name { font-weight: 700; font-size: 0.88rem; color: #1e40af; margin-bottom: 8px; font-family: monospace; }
  .failure-section { font-size: 0.8rem; font-weight: 600; color: #6b7280; text-transform: uppercase; letter-spacing: .05em; margin: 8px 0 4px; }
  pre.failure-message, pre.failure-stacktrace {
    background: #1e1e2e; color: #cdd6f4; padding: 12px 14px; border-radius: 6px;
    font-size: 0.82rem; overflow-x: auto; white-space: pre-wrap; word-break: break-word;
    line-height: 1.5; max-height: 400px; overflow-y: auto;
  }
  pre.failure-stacktrace { background: #0f172a; }
  .pass-msg { padding: 8px 16px; color: #16a34a; font-size: 0.88rem; }

  /* Screenshots */
  .screenshots { display: flex; flex-direction: column; gap: 12px; margin: 8px 0; }
  .screenshot-wrap { border: 1px solid #e2e8f0; border-radius: 6px; overflow: hidden; background: #f8fafc; }
  .screenshot-path { font-size: 0.75rem; font-family: monospace; color: #6b7280; padding: 4px 8px; background: #f1f5f9; border-bottom: 1px solid #e2e8f0; word-break: break-all; }
  .screenshot-img { display: block; max-width: 100%; cursor: zoom-in; }
  .screenshot-img:hover { outline: 2px solid #2563eb; }
  .screenshot-missing { font-size: 0.8rem; color: #9ca3af; font-family: monospace; padding: 4px 0; }
</style>");
    }

    private static void AppendScripts(StringBuilder sb)
    {
        sb.AppendLine(@"<script>
document.addEventListener('DOMContentLoaded', function() {
  // Click screenshot to open full-size in new tab
  document.querySelectorAll('.screenshot-img').forEach(function(img) {
    img.addEventListener('click', function() {
      window.open(img.src, '_blank');
    });
  });
});
</script>");
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string HtmlId(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9_-]", "_");

    private static string? TryBuildDataUri(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png"  => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"  => "image/gif",
                ".bmp"  => "image/bmp",
                _       => "image/png",
            };
            var bytes = File.ReadAllBytes(filePath);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }
}
