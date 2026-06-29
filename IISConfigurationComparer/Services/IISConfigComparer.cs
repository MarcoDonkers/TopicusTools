using System.Xml.Linq;
using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Compares two ApplicationHost.config XML documents and reports structured differences
/// across key IIS sections: sites, application pools, global modules, and handlers.
/// </summary>
public class IISConfigComparer
{
    private static readonly string[] TrackedSections =
    [
        "system.applicationHost/sites",
        "system.applicationHost/applicationPools",
        "system.webServer/globalModules",
        "system.webServer/modules",
        "system.webServer/handlers",
        "system.webServer/security",
        "system.webServer/security/authentication/anonymousAuthentication",
        "system.webServer/security/authentication/basicAuthentication",
        "system.webServer/security/authentication/windowsAuthentication",
        "system.webServer/security/access",
        "system.webServer/httpErrors",
        "system.webServer/defaultDocument",
        "system.applicationHost/log",
        "system.applicationHost/webLimits"
    ];

    public ComparisonResult Compare(
        string leftXml, string leftName,
        string rightXml, string rightName,
        IEnumerable<string>? leftTokens = null,
        IEnumerable<string>? rightTokens = null)
    {
        var result = new ComparisonResult
        {
            LeftEnvironment = leftName,
            RightEnvironment = rightName
        };

        // Build ordered token lists (longest first to avoid partial replacements)
        var leftNorm  = BuildTokens(leftName,  leftTokens);
        var rightNorm = BuildTokens(rightName, rightTokens);

        var leftDoc  = XDocument.Parse(leftXml);
        var rightDoc = XDocument.Parse(rightXml);

        foreach (var section in TrackedSections)
        {
            var parts = section.Split('/');
            var leftSection  = Navigate(leftDoc.Root, parts);
            var rightSection = Navigate(rightDoc.Root, parts);

            if (leftSection is null && rightSection is null) continue;

            if (leftSection is null)
            {
                result.Differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInRight,
                    Section = section,
                    ElementPath = section
                });
                continue;
            }

            if (rightSection is null)
            {
                result.Differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInLeft,
                    Section = section,
                    ElementPath = section
                });
                continue;
            }

            CompareElements(leftSection, rightSection, section, section, result.Differences, leftNorm, rightNorm);
        }

        return result;
    }

    /// <summary>
    /// Builds a list of strings to replace in values for a given environment,
    /// ordered longest-first so longer tokens are replaced before shorter ones.
    /// </summary>
    private static List<string> BuildTokens(string envName, IEnumerable<string>? extra)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { envName };
        if (extra is not null)
            foreach (var t in extra) tokens.Add(t);
        return tokens.OrderByDescending(t => t.Length).ToList();
    }

    /// <summary>
    /// Normalizes a value by replacing all environment-specific tokens with a
    /// shared placeholder, so that "ws-o20.qsp.finance.lab" and "ws-o21.qsp.finance.lab"
    /// both become "ws-{ENV}.qsp.finance.lab" and are treated as equal.
    /// </summary>
    private static string Normalize(string value, IList<string> tokens)
    {
        foreach (var token in tokens)
            value = value.Replace(token, "{ENV}", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    /// <summary>
    /// Returns true if the two values differ only because of environment-specific
    /// tokens (e.g. the hostname or environment name). Such differences are noise.
    /// </summary>
    private static bool IsEnvOnlyDifference(
        string leftVal, string rightVal,
        IList<string> leftNorm, IList<string> rightNorm)
    {
        if (leftVal == rightVal) return false;
        return string.Equals(
            Normalize(leftVal, leftNorm),
            Normalize(rightVal, rightNorm),
            StringComparison.OrdinalIgnoreCase);
    }

    private static XElement? Navigate(XElement? root, string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current is null) return null;
            // Match <configuration><system.applicationHost><sites> etc.
            current = current.Descendants(segment).FirstOrDefault()
                   ?? current.Element(segment);
        }
        return current;
    }

    private static void CompareElements(
        XElement left, XElement right,
        string section, string path,
        List<ConfigDifference> differences,
        IList<string> leftNorm, IList<string> rightNorm)
    {
        // Compare attributes
        var leftAttrs = left.Attributes()
            .ToDictionary(a => a.Name.LocalName, a => a.Value);
        var rightAttrs = right.Attributes()
            .ToDictionary(a => a.Name.LocalName, a => a.Value);

        foreach (var (name, leftVal) in leftAttrs)
        {
            if (!rightAttrs.TryGetValue(name, out var rightVal))
            {
                differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInLeft,
                    Section = section,
                    ElementPath = path,
                    AttributeName = name,
                    LeftValue = leftVal
                });
            }
            else if (leftVal != rightVal && !IsEnvOnlyDifference(leftVal, rightVal, leftNorm, rightNorm))
            {
                differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.AttributeDifference,
                    Section = section,
                    ElementPath = path,
                    AttributeName = name,
                    LeftValue = leftVal,
                    RightValue = rightVal
                });
            }
        }

        foreach (var (name, rightVal) in rightAttrs)
        {
            if (!leftAttrs.ContainsKey(name))
            {
                differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInRight,
                    Section = section,
                    ElementPath = path,
                    AttributeName = name,
                    RightValue = rightVal
                });
            }
        }

        // Compare named child elements (matched by key attributes)
        var leftChildren = left.Elements().ToList();
        var rightChildren = right.Elements().ToList();

        // Group children by element name
        var leftByTag = leftChildren.GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.ToList());
        var rightByTag = rightChildren.GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allTags = leftByTag.Keys.Union(rightByTag.Keys).ToHashSet();

        foreach (var tag in allTags)
        {
            var hasLeft = leftByTag.TryGetValue(tag, out var leftGroup);
            var hasRight = rightByTag.TryGetValue(tag, out var rightGroup);

            if (!hasLeft)
            {
                differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInRight,
                    Section = section,
                    ElementPath = $"{path}/{tag}"
                });
                continue;
            }

            if (!hasRight)
            {
                differences.Add(new ConfigDifference
                {
                    Kind = DifferenceKind.OnlyInLeft,
                    Section = section,
                    ElementPath = $"{path}/{tag}"
                });
                continue;
            }

            // Try to match elements by key attribute (name, path, or first attribute)
            var leftKeyed = IndexByKey(leftGroup!, leftNorm);
            var rightKeyed = IndexByKey(rightGroup!, rightNorm);

            foreach (var (key, leftEl) in leftKeyed)
            {
                var childPath = $"{path}/{tag}[@{key}]";
                if (!rightKeyed.TryGetValue(key, out var rightEl))
                {
                    differences.Add(new ConfigDifference
                    {
                        Kind = DifferenceKind.OnlyInLeft,
                        Section = section,
                        ElementPath = childPath
                    });
                }
                else
                {
                    CompareElements(leftEl, rightEl, section, childPath, differences, leftNorm, rightNorm);
                }
            }

            foreach (var (key, _) in rightKeyed)
            {
                if (!leftKeyed.ContainsKey(key))
                {
                    differences.Add(new ConfigDifference
                    {
                        Kind = DifferenceKind.OnlyInRight,
                        Section = section,
                        ElementPath = $"{path}/{tag}[@{key}]"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Indexes elements by key attribute, normalizing key values so that
    /// environment-specific tokens don't prevent matching across environments.
    /// </summary>
    private static Dictionary<string, XElement> IndexByKey(List<XElement> elements, IList<string>? normTokens = null)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var keyAttributes = new[] { "name", "path", "type", "value", "verb" };

        foreach (var el in elements)
        {
            string? key = null;
            foreach (var attrName in keyAttributes)
            {
                var attr = el.Attribute(attrName);
                if (attr is not null)
                {
                    var attrVal = normTokens is not null ? Normalize(attr.Value, normTokens) : attr.Value;
                    key = $"{attrName}={attrVal}";
                    break;
                }
            }

            // Fall back to first attribute or index
            key ??= el.Attributes().FirstOrDefault() is { } firstAttr
                ? $"{firstAttr.Name.LocalName}={firstAttr.Value}"
                : $"index={elements.IndexOf(el)}";

            result.TryAdd(key, el);
        }

        return result;
    }
}
