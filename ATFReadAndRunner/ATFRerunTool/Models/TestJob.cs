namespace ATFRerunTool.Models;

/// <summary>
/// Represents a pair of S (State) and R (Regression) tests that belong to the same test number.
/// </summary>
public sealed class TestJob
{
    /// <summary>The test identifier, e.g. "067_WBH_Omzetting" or "Beslisboom_5_1_3".</summary>
    public string TestId { get; init; } = "";

    /// <summary>The NUnit category for the State test, e.g. "S067_WBH_Omzetting".</summary>
    public string CategoryS { get; init; } = "";

    /// <summary>The NUnit category for the Regression test, e.g. "R067_WBH_Omzetting".</summary>
    public string CategoryR { get; init; } = "";

    /// <summary>Source that triggered this job being added (Jenkins FAILURE, or manual).</summary>
    public string Source { get; init; } = "";

    /// <summary>Whether the R test exists in the test suite (some S tests have no R counterpart).</summary>
    public bool HasRTest { get; set; } = true;

    public override string ToString() => TestId;
}
