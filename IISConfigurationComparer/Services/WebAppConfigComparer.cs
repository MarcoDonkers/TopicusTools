using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Compares application config files between two environments, suppressing differences
/// that are only due to environment-specific tokens (e.g. "o21" vs "o23").
/// </summary>
public class WebAppConfigComparer
{
    /// <summary>
    /// Compares two sets of app config files and returns one ComparisonResult per app
    /// that has non-trivial differences. Apps that are identical or differ only in
    /// environment tokens are omitted.
    /// </summary>
    public List<ComparisonResult> Compare(
        List<WebAppConfigFile> leftFiles, string leftName,
        List<WebAppConfigFile> rightFiles, string rightName,
        IEnumerable<string>? leftExtraTokens = null,
        IEnumerable<string>? rightExtraTokens = null)
    {
        var leftNorm  = BuildTokens(leftName,  leftExtraTokens);
        var rightNorm = BuildTokens(rightName, rightExtraTokens);

        var leftByApp  = GroupByApp(leftFiles);
        var rightByApp = GroupByApp(rightFiles);

        var allApps = leftByApp.Keys.Union(rightByApp.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k);

        var results = new List<ComparisonResult>();

        foreach (var appName in allApps)
        {
            var leftAppFiles  = leftByApp.GetValueOrDefault(appName)  ?? [];
            var rightAppFiles = rightByApp.GetValueOrDefault(appName) ?? [];

            var result = new ComparisonResult
            {
                LeftEnvironment  = leftName,
                RightEnvironment = rightName
            };

            CompareApp(appName, leftAppFiles, rightAppFiles, result.Differences, leftNorm, rightNorm);

            if (!result.IsIdentical)
                results.Add(result);
        }

        return results;
    }

    private static void CompareApp(
        string appName,
        List<WebAppConfigFile> leftFiles, List<WebAppConfigFile> rightFiles,
        List<ConfigDifference> differences,
        List<string> leftNorm, List<string> rightNorm)
    {
        var leftByFile  = leftFiles.ToDictionary(f => f.FileName,  StringComparer.OrdinalIgnoreCase);
        var rightByFile = rightFiles.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);
        var allFiles    = leftByFile.Keys.Union(rightByFile.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in allFiles)
        {
            var hasLeft  = leftByFile.TryGetValue(fileName,  out var leftFile);
            var hasRight = rightByFile.TryGetValue(fileName, out var rightFile);

            if (!hasLeft)
            {
                differences.Add(new ConfigDifference
                {
                    Kind        = DifferenceKind.OnlyInRight,
                    Section     = appName,
                    ElementPath = $"{appName}/{fileName}"
                });
                continue;
            }

            if (!hasRight)
            {
                differences.Add(new ConfigDifference
                {
                    Kind        = DifferenceKind.OnlyInLeft,
                    Section     = appName,
                    ElementPath = $"{appName}/{fileName}"
                });
                continue;
            }

            var leftKvs  = ParseConfig(leftFile!);
            var rightKvs = ParseConfig(rightFile!);

            CompareKeyValues(appName, fileName, leftKvs, rightKvs, differences, leftNorm, rightNorm);
        }
    }

    private static void CompareKeyValues(
        string appName, string fileName,
        Dictionary<string, string> leftKvs, Dictionary<string, string> rightKvs,
        List<ConfigDifference> differences,
        List<string> leftNorm, List<string> rightNorm)
    {
        foreach (var (key, leftVal) in leftKvs)
        {
            if (!rightKvs.TryGetValue(key, out var rightVal))
            {
                differences.Add(new ConfigDifference
                {
                    Kind        = DifferenceKind.OnlyInLeft,
                    Section     = appName,
                    ElementPath = $"{fileName}/{key}",
                    LeftValue   = leftVal
                });
            }
            else if (leftVal != rightVal && !IsEnvOnlyDifference(leftVal, rightVal, leftNorm, rightNorm))
            {
                differences.Add(new ConfigDifference
                {
                    Kind          = DifferenceKind.AttributeDifference,
                    Section       = appName,
                    ElementPath   = $"{fileName}/{key}",
                    AttributeName = "value",
                    LeftValue     = leftVal,
                    RightValue    = rightVal
                });
            }
        }

        foreach (var (key, rightVal) in rightKvs)
        {
            if (!leftKvs.ContainsKey(key))
            {
                differences.Add(new ConfigDifference
                {
                    Kind        = DifferenceKind.OnlyInRight,
                    Section     = appName,
                    ElementPath = $"{fileName}/{key}",
                    RightValue  = rightVal
                });
            }
        }
    }

    private static Dictionary<string, string> ParseConfig(WebAppConfigFile file)
        => file.FileName.Equals("web.config", StringComparison.OrdinalIgnoreCase)
            ? AppConfigParser.ParseWebConfig(file.Content)
            : AppConfigParser.ParseAppSettings(file.Content);

    private static Dictionary<string, List<WebAppConfigFile>> GroupByApp(List<WebAppConfigFile> files)
        => files.GroupBy(f => f.AppName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    private static List<string> BuildTokens(string envName, IEnumerable<string>? extra)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { envName };
        if (extra is not null)
            foreach (var t in extra) tokens.Add(t);
        return [.. tokens.OrderByDescending(t => t.Length)];
    }

    private static string Normalize(string value, IList<string> tokens)
    {
        foreach (var token in tokens)
            value = value.Replace(token, "{ENV}", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static bool IsEnvOnlyDifference(
        string leftVal, string rightVal,
        IList<string> leftNorm, IList<string> rightNorm)
    {
        if (leftVal == rightVal) return false;
        return string.Equals(
            Normalize(leftVal,  leftNorm),
            Normalize(rightVal, rightNorm),
            StringComparison.OrdinalIgnoreCase);
    }
}
