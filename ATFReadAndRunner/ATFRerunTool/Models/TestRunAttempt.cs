namespace ATFRerunTool.Models;

public enum TestVariant { State, Regression }
public enum RunStatus { NotRun, Passed, Failed, Error }

/// <summary>
/// The result of running a single NUnit test category in one attempt.
/// </summary>
public sealed class TestRunAttempt
{
    public string TestId { get; init; } = "";
    public string Category { get; init; } = "";
    public TestVariant Variant { get; init; }
    public int Round { get; init; }
    public RunStatus Status { get; set; } = RunStatus.NotRun;
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public TimeSpan Duration => FinishedAt - StartedAt;

    /// <summary>Raw NUnit XML result file path.</summary>
    public string ResultXmlPath { get; set; } = "";

    /// <summary>Parsed failure details (may be empty when passed).</summary>
    public List<TestCaseFailure> Failures { get; set; } = [];

    public bool Passed => Status == RunStatus.Passed;
}

public sealed class TestCaseFailure
{
    public string TestName { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public List<string> ScreenshotPaths { get; set; } = [];
}
