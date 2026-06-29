namespace ATFRerunTool.Parsing;

/// <summary>
/// Loads and saves the KnownToBeBroken.txt file from the ATFRun folder.
/// Each non-comment line is a test ID (without the S/R prefix).
/// </summary>
public static class KnownBrokenList
{
    private const string FileName = "KnownToBeBroken.txt";

    public static string GetFilePath(string atfRunFolder) =>
        Path.Combine(atfRunFolder, FileName);

    /// <summary>Loads the list; returns empty set if the file doesn't exist.</summary>
    public static HashSet<string> Load(string atfRunFolder)
    {
        var path = GetFilePath(atfRunFolder);
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Saves the set back, preserving the comment header from the existing file.</summary>
    public static void Save(string atfRunFolder, IEnumerable<string> ids)
    {
        var path = GetFilePath(atfRunFolder);

        // Preserve any comment lines at the top of the existing file
        var comments = new List<string>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.StartsWith('#') || t.Length == 0)
                    comments.Add(line);
                else
                    break;
            }
        }

        if (comments.Count == 0)
            comments.Add("# Tests known to be broken — excluded from reruns.");

        var sorted = ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var lines = comments.Concat(sorted.Select(id => id));
        File.WriteAllLines(path, lines);
    }
}
