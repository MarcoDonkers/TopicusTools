namespace ATFRerunTool.Models;

/// <summary>
/// Tracks everything for a full rerun session (all rounds, all jobs, all results).
/// </summary>
public sealed class RerunSession
{
    public string SessionId { get; init; } = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    public string RunName { get; init; } = "";   // e.g. "S3903-R1857"
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int MaxRounds { get; init; } = 3;

    public List<TestJob> Jobs { get; set; } = [];

    /// <summary>All attempts across all rounds, in execution order.</summary>
    public List<TestRunAttempt> AllAttempts { get; set; } = [];

    public IEnumerable<TestRunAttempt> AttemptsForJob(string testId) =>
        AllAttempts.Where(a => a.TestId == testId);

    public IEnumerable<TestRunAttempt> AttemptsForRound(int round) =>
        AllAttempts.Where(a => a.Round == round);

    /// <summary>
    /// Final verdict: Pass if both S and R eventually passed, Fail otherwise.
    /// "Went green locally" means it was Jenkins instability — still a Pass.
    /// </summary>
    public JobVerdict GetVerdict(TestJob job)
    {
        // A job is green when R has passed at least once (or S-only job where S passed).
        // S reruns every round so its pass/fail per round is informational only.
        var rAttempts = AllAttempts.Where(a => a.TestId == job.TestId && a.Variant == TestVariant.Regression).ToList();

        if (!job.HasRTest)
        {
            var sAttempts = AllAttempts.Where(a => a.TestId == job.TestId && a.Variant == TestVariant.State).ToList();
            return sAttempts.Any(a => a.Passed) ? JobVerdict.Pass : JobVerdict.Fail;
        }

        return rAttempts.Any(a => a.Passed) ? JobVerdict.Pass : JobVerdict.Fail;
    }
}

public enum JobVerdict { Pass, Fail }
