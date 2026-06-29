namespace ATFRerunTool.Models;

/// <summary>
/// Saved to disk after each session so the next run can pick up only the jobs
/// that are still failing, without re-parsing the Jenkins log files.
/// Written to: Results\{RunName}\previous_state.json
/// </summary>
public sealed class PreviousRunState
{
    public string RunName { get; set; } = "";
    public DateTime CompletedAt { get; set; }
    public List<string> StillFailingTestIds { get; set; } = [];
}
