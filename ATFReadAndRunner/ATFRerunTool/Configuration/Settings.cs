namespace ATFRerunTool.Configuration;

public sealed class Settings
{
    public string TestDllPath { get; set; } = "";

    public string NUnitConsolePath { get; set; } = "";

    /// <summary>
    /// Absolute path, or relative to the executable directory.
    /// </summary>
    public string ResultsOutputDirectory { get; set; } = "Results";

    public int MaxRerunCount { get; set; } = 3;

    /// <summary>How many test jobs may run concurrently within a round (0 = no limit).</summary>
    public int MaxParallelism { get; set; } = 5;

    /// <summary>
    /// If > 0, overrides MaxWaitTimeStatusChangeMiliseconds in the test DLL config before each run.
    /// Use to make status-change waits fail faster locally. 0 = don't override.
    /// </summary>
    public int OverrideMaxWaitTimeStatusChangeMs { get; set; } = 60000;

    public JenkinsSettings Jenkins { get; set; } = new();
}

public sealed class JenkinsSettings
{
    /// <summary>Set to true to trigger/poll reruns via Jenkins instead of locally.</summary>
    public bool Enabled { get; set; } = false;

    public string BaseUrl { get; set; } = "https://jenkins.example.com";

    /// <summary>Format string; {0} = test category name, e.g. S067_WBH_Omzetting.</summary>
    public string StateJobPattern { get; set; } = "QSP-ATF-State-Opbouw-Runner-{0}";
    public string RegressionJobPattern { get; set; } = "QSP-ATF-Regressietest-Runner-{0}";

    public string ApiUser { get; set; } = "";
    public string ApiToken { get; set; } = "";
}
