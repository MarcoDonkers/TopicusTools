namespace IISConfigurationComparer.Models;

public enum DifferenceKind
{
    OnlyInLeft,
    OnlyInRight,
    AttributeDifference,
    ChildCountDifference
}

public class ConfigDifference
{
    public DifferenceKind Kind { get; set; }
    public string Section { get; set; } = string.Empty;
    public string ElementPath { get; set; } = string.Empty;
    public string? AttributeName { get; set; }
    public string? LeftValue { get; set; }
    public string? RightValue { get; set; }

    public override string ToString()
    {
        return Kind switch
        {
            DifferenceKind.OnlyInLeft =>
                $"[ONLY IN LEFT]  {Section} > {ElementPath}",
            DifferenceKind.OnlyInRight =>
                $"[ONLY IN RIGHT] {Section} > {ElementPath}",
            DifferenceKind.AttributeDifference =>
                $"[ATTR DIFFERS]  {Section} > {ElementPath} @{AttributeName}: \"{LeftValue}\" vs \"{RightValue}\"",
            DifferenceKind.ChildCountDifference =>
                $"[COUNT DIFFERS] {Section} > {ElementPath}: {LeftValue} vs {RightValue} children",
            _ => $"[UNKNOWN] {ElementPath}"
        };
    }
}

public class ComparisonResult
{
    public string LeftEnvironment { get; set; } = string.Empty;
    public string RightEnvironment { get; set; } = string.Empty;
    public List<ConfigDifference> Differences { get; set; } = new();
    public bool IsIdentical => Differences.Count == 0;
}
