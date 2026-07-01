using System.Text.Json;
using System.Xml.Linq;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Parses web.config (XML) and appsettings.json into flat key→value dictionaries
/// suitable for environment comparison.
/// </summary>
public static class AppConfigParser
{
    /// <summary>
    /// Extracts appSettings and connectionStrings from a web.config XML string.
    /// Keys are prefixed: "appSettings/MyKey" or "connectionStrings/MyName".
    /// </summary>
    public static Dictionary<string, string> ParseWebConfig(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return result; }

        foreach (var section in doc.Descendants("appSettings"))
        {
            foreach (var add in section.Elements("add"))
            {
                var key = add.Attribute("key")?.Value;
                if (key is not null)
                    result[$"appSettings/{key}"] = add.Attribute("value")?.Value ?? "";
            }
        }

        foreach (var section in doc.Descendants("connectionStrings"))
        {
            foreach (var add in section.Elements("add"))
            {
                var name = add.Attribute("name")?.Value;
                if (name is not null)
                    result[$"connectionStrings/{name}"] = add.Attribute("connectionString")?.Value ?? "";
            }
        }

        return result;
    }

    /// <summary>
    /// Flattens an appsettings.json file into key-path → value pairs.
    /// Nested objects use ":" as separator (e.g. "Logging:LogLevel:Default").
    /// </summary>
    public static Dictionary<string, string> ParseAppSettings(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            FlattenElement(doc.RootElement, "", result);
        }
        catch { /* skip unparseable files */ }
        return result;
    }

    private static void FlattenElement(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    FlattenElement(prop.Value, key, result);
                }
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                    FlattenElement(item, $"{prefix}[{i++}]", result);
                break;

            case JsonValueKind.String:
                result[prefix] = element.GetString() ?? "";
                break;

            default:
                result[prefix] = element.GetRawText();
                break;
        }
    }
}
