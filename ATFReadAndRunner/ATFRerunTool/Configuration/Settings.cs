namespace ATFRerunTool.Configuration;

public sealed class Settings
{
    /// <summary>Path to the QSP.Core git repository. All repo-relative paths are derived from this.</summary>
    public string GitRepoPath { get; set; } = "";

    public string TestDllRelativePath { get; set; } =
        @"FinGen.WebApplication.SysteemTest\bin\FinGen.WebApplication.SysteemTest.dll";

    public string NUnitConsoleRelativePath { get; set; } =
        @"packages\NUnit.ConsoleRunner\tools\nunit3-console.exe";

    public string ConfigScriptRelativePath { get; set; } =
        @"Tools\Powershell\TransformConfig.ps1";

    public string TestDllPath => Path.Combine(GitRepoPath, TestDllRelativePath);
    public string NUnitConsolePath => Path.Combine(GitRepoPath, NUnitConsoleRelativePath);

    /// <summary>Absolute path, or relative to the executable directory.</summary>
    public string ResultsOutputDirectory { get; set; } = "Results";

    public int MaxRerunCount { get; set; } = 3;

    /// <summary>How many test jobs may run concurrently within a round (0 = no limit).</summary>
    public int MaxParallelism { get; set; } = 5;

    /// <summary>
    /// If > 0, overrides MaxWaitTimeStatusChangeMiliseconds in the test DLL config before each run.
    /// Use to make status-change waits fail faster locally. 0 = don't override.
    /// </summary>
    public int OverrideMaxWaitTimeStatusChangeMs { get; set; } = 60000;

    public List<EnvironmentEntry> Environments { get; set; } = [];

    public DatabaseResetSettings DatabaseReset { get; set; } = new();

    public JenkinsSettings Jenkins { get; set; } = new();
}

public sealed class EnvironmentEntry
{
    public string Number { get; set; } = "";
    public string Host { get; set; } = "";
}

public sealed class DatabaseResetSettings
{
    /// <summary>S-only category that initialises the database; runs alone before all other tests.</summary>
    public string SetupCategory { get; set; } = "S000_BasisOpzetten";
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
