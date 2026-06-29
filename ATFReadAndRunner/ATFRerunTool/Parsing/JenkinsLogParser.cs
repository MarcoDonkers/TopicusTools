using ATFRerunTool.Models;
using System.Text.RegularExpressions;

namespace ATFRerunTool.Parsing;

/// <summary>
/// Parses a Jenkins console log and extracts all jobs that finished with FAILURE.
/// </summary>
public static class JenkinsLogParser
{
    // Matches lines like:
    //   14:11:23  Finished Build : #3213 of Job : QSP-ATF-State-Opbouw-Runner-S_Beslisboom_5_1_3 with status : FAILURE
    private static readonly Regex FailureLine = new(
        @"Finished Build\s*:\s*#\d+\s+of Job\s*:\s*(?<jobname>\S+)\s+with status\s*:\s*FAILURE",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SuccessLine = new(
        @"Finished Build\s*:\s*#\d+\s+of Job\s*:\s*(?<jobname>\S+)\s+with status\s*:\s*SUCCESS",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Known prefixes used in Jenkins job names for ATF jobs
    private static readonly string[] KnownPrefixes =
    [
        "QSP-ATF-State-Opbouw-Runner-",
        "QSP-ATF-Regressietest-Runner-",
        "QSP-ATF-State-Runner-",
        "QSP-ATF-Regression-Runner-",
    ];

    public static List<TestJob> Parse(string logFilePath)
    {
        if (!File.Exists(logFilePath))
            throw new FileNotFoundException($"Jenkins log not found: {logFilePath}");

        var lines = File.ReadAllLines(logFilePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<TestJob>();

        foreach (var line in lines)
        {
            var m = FailureLine.Match(line);
            if (!m.Success) continue;

            var jobName = m.Groups["jobname"].Value;
            var category = ExtractCategory(jobName);
            if (string.IsNullOrEmpty(category)) continue;

            // Normalise to ensure we always have a key regardless of S/R prefix
            var testId = StripVariantPrefix(category);
            if (!seen.Add(testId)) continue;

            var (categoryS, categoryR) = BuildCategoryPair(category, testId);
            jobs.Add(new TestJob
            {
                TestId = testId,
                CategoryS = categoryS,
                CategoryR = categoryR,
                Source = jobName,
            });
        }

        // Sort by test id so they run in numeric/alphabetic order
        jobs.Sort((a, b) => string.Compare(a.TestId, b.TestId, StringComparison.OrdinalIgnoreCase));
        return jobs;
    }

    /// <summary>
    /// Returns the set of TestIds that appeared as SUCCESS in the log.
    /// Only ATF runner jobs matching the known prefixes are included.
    /// </summary>
    public static HashSet<string> ParseSuccessIds(string logFilePath)
    {
        if (!File.Exists(logFilePath))
            throw new FileNotFoundException($"Jenkins log not found: {logFilePath}");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(logFilePath))
        {
            var m = SuccessLine.Match(line);
            if (!m.Success) continue;

            var category = ExtractCategory(m.Groups["jobname"].Value);
            if (string.IsNullOrEmpty(category)) continue;

            ids.Add(StripVariantPrefix(category));
        }
        return ids;
    }

    private static string ExtractCategory(string jobName)
    {
        foreach (var prefix in KnownPrefixes)
        {
            if (jobName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return jobName[prefix.Length..];
        }
        // Fallback: if no known prefix matched, try stripping up to the last dash-word
        // that starts with S or R followed by a digit or underscore
        var match = Regex.Match(jobName, @"(?:^|-)([SR][\w]+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>Remove leading S or R prefix to get the bare test identifier.</summary>
    private static string StripVariantPrefix(string category)
    {
        if (category.Length > 1 && (category[0] == 'S' || category[0] == 'R') && category[1] != '_')
        {
            // e.g. S067_WBH_Omzetting → 067_WBH_Omzetting
            // But S_Beslisboom_3 starts with S_ which means it is ALREADY using underscore naming
            return category[1..];
        }

        if (category.StartsWith("S_", StringComparison.OrdinalIgnoreCase) ||
            category.StartsWith("R_", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. S_Beslisboom_5_1_3 → Beslisboom_5_1_3 (strip "S_" or "R_")
            return category[2..];
        }

        return category;
    }

    private static (string S, string R) BuildCategoryPair(string originalCategory, string testId)
    {
        // Determine if original was S or R type
        bool isR = originalCategory.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                   && !originalCategory.StartsWith("R_", StringComparison.OrdinalIgnoreCase)
                   || originalCategory.StartsWith("R_", StringComparison.OrdinalIgnoreCase);

        // Detect separator style: "S067_..." uses no underscore, "S_Beslisboom_..." uses underscore
        bool useUnderscore = originalCategory.StartsWith("S_", StringComparison.OrdinalIgnoreCase)
                             || originalCategory.StartsWith("R_", StringComparison.OrdinalIgnoreCase);

        string categoryS = useUnderscore ? "S_" + testId : "S" + testId;
        string categoryR = useUnderscore ? "R_" + testId : "R" + testId;

        return (categoryS, categoryR);
    }
}
