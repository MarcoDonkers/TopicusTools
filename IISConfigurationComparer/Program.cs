using System.Xml.Linq;
using IISConfigurationComparer.Models;
using IISConfigurationComparer.Services;
using Microsoft.Extensions.Configuration;

// ─── Build configuration ────────────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var environments = configuration
    .GetSection("environments")
    .Get<List<EnvironmentConfig>>() ?? [];

if (environments.Count == 0)
{
    Console.Error.WriteLine("No environments found in appsettings.json.");
    return 1;
}

// ─── Parse CLI arguments ─────────────────────────────────────────────────────
// Usage:
//   IISConfigurationComparer                              → compare all pairs (console)
//   IISConfigurationComparer <env1> <env2>                → compare specific pair
//   IISConfigurationComparer --html [env1] [env2]         → generate HTML report
//   IISConfigurationComparer --webapp [--html] [env1] [env2] → include app config comparison
//   IISConfigurationComparer --list                       → list configured environments
//   IISConfigurationComparer --save <env> <file.xml>      → save config to file
//   IISConfigurationComparer --local <file.xml> <env>     → compare local file vs env
//   IISConfigurationComparer --explore-webapp <env>       → list app configs found on env

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

bool htmlOutput  = cliArgs.Contains("--html");
bool webAppMode  = cliArgs.Contains("--webapp");
cliArgs = cliArgs.Where(a => a != "--html" && a != "--webapp").ToArray();

if (cliArgs.Length == 1 && cliArgs[0] == "--list")
{
    PrintEnvironmentList(environments);
    return 0;
}

if (cliArgs.Length == 3 && cliArgs[0] == "--save")
{
    return await SaveConfigToFile(cliArgs[1], cliArgs[2], environments);
}

if (cliArgs.Length == 3 && cliArgs[0] == "--local")
{
    return await CompareLocalVsRemote(cliArgs[1], cliArgs[2], environments);
}

if (cliArgs.Length == 3 && cliArgs[0] == "--compare-files")
{
    return CompareLocalFiles(cliArgs[1], cliArgs[2]);
}

if (cliArgs.Length == 2 && cliArgs[0] == "--explore-webapp")
{
    return await ExploreWebApp(cliArgs[1], environments);
}


List<EnvironmentConfig> targets;

if (cliArgs.Length == 2)
{
    var left = environments.FirstOrDefault(e =>
        e.Name.Equals(cliArgs[0], StringComparison.OrdinalIgnoreCase));
    var right = environments.FirstOrDefault(e =>
        e.Name.Equals(cliArgs[1], StringComparison.OrdinalIgnoreCase));

    if (left is null) { Console.Error.WriteLine($"Unknown environment: {cliArgs[0]}"); return 1; }
    if (right is null) { Console.Error.WriteLine($"Unknown environment: {cliArgs[1]}"); return 1; }

    targets = [left, right];
}
else
{
    targets = environments;
}

var fetchers = new List<IConfigFetcher>
{
    new UncConfigFetcher(),
    new WinRmConfigFetcher(),
    new SshConfigFetcher()
};
var factory = new ConfigFetcherFactory(fetchers);
var comparer = new IISConfigComparer();
var sslFetcher = new SslCertFetcher();

Console.WriteLine("IIS Configuration Comparer");
Console.WriteLine(new string('═', 60));
Console.WriteLine();

var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var sslBindings = new Dictionary<string, List<SslCertBinding>>(StringComparer.OrdinalIgnoreCase);

foreach (var env in targets)
{
    Console.Write($"  Fetching '{env.Name}' ({env.Host}, {env.Method})... ");
    try
    {
        var xml = await factory.FetchAsync(env);
        configs[env.Name] = xml;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("OK");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"FAILED: {ex.Message}");
        Console.ResetColor();
        configs[env.Name] = string.Empty;
    }

    // Fetch SSL cert bindings (best-effort, don't fail the whole run)
    if (!string.IsNullOrEmpty(configs[env.Name]) && env.Method == ConnectionMethod.Ssh)
    {
        try
        {
            Console.Write("  (fetching SSL certs... ");
            var certs = await sslFetcher.FetchAsync(env);
            sslBindings[env.Name] = certs;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"{certs.Count} bindings)");
            Console.ResetColor();
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("SSL cert fetch skipped)");
            Console.ResetColor();
        }
    }

    Console.WriteLine();
}

Console.WriteLine();

// ─── Compare each pair ───────────────────────────────────────────────────────
var successful = configs.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToList();
var failed = configs.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => $"{kv.Key}: fetch failed").ToList();
var results = new List<ComparisonResult>();
int exitCode = 0;

for (int i = 0; i < successful.Count; i++)
{
    for (int j = i + 1; j < successful.Count; j++)
    {
        var (leftName, leftXml) = (successful[i].Key, successful[i].Value);
        var (rightName, rightXml) = (successful[j].Key, successful[j].Value);

        // Pass hostnames as extra tokens so "ws-o20.host" vs "ws-o21.host" is not reported
        var leftEnv  = targets.FirstOrDefault(e => e.Name == leftName);
        var rightEnv = targets.FirstOrDefault(e => e.Name == rightName);
        var leftTokens  = leftEnv  is not null ? new[] { leftEnv.Host }  : null;
        var rightTokens = rightEnv is not null ? new[] { rightEnv.Host } : null;

        var result = comparer.Compare(leftXml, leftName, rightXml, rightName, leftTokens, rightTokens);
        results.Add(result);
        PrintComparisonResult(result);

        // SSL cert comparison
        if (sslBindings.TryGetValue(leftName, out var leftCerts) &&
            sslBindings.TryGetValue(rightName, out var rightCerts))
        {
            var sslDiffs = new SslCertComparer().Compare(leftCerts, leftName, rightCerts, rightName);
            PrintSslDifferences(leftName, rightName, sslDiffs);
        }

        if (!result.IsIdentical) exitCode = 2;
    }
}

// ─── App config comparison (--webapp) ───────────────────────────────────────
var webAppPairResults = new List<(string PairKey, List<ComparisonResult> AppResults)>();

if (webAppMode && successful.Count >= 2)
{
    Console.WriteLine("App Configuration Comparison");
    Console.WriteLine(new string('─', 60));
    Console.WriteLine();

    // Extract IIS app physical paths from each successfully-fetched config
    var appPathsByEnv = new Dictionary<string, List<(string AppName, string WindowsPath)>>(
        StringComparer.OrdinalIgnoreCase);

    foreach (var (envName, xml) in successful)
    {
        var paths = ExtractAppPhysicalPaths(xml);
        appPathsByEnv[envName] = paths;
        Console.WriteLine($"  '{envName}': {paths.Count} IIS applications found.");
    }

    Console.WriteLine();

    // Fetch webapp configs per env
    var webAppFiles = new Dictionary<string, List<WebAppConfigFile>>(StringComparer.OrdinalIgnoreCase);
    var webFetcher  = new WebAppConfigFetcher();

    foreach (var env in targets.Where(e => appPathsByEnv.ContainsKey(e.Name)))
    {
        Console.Write($"  Fetching app configs from '{env.Name}'... ");
        try
        {
            var files = await webFetcher.FetchAsync(env, appPathsByEnv[env.Name]);
            webAppFiles[env.Name] = files;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"OK ({files.Count} config file(s))");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }
    }

    Console.WriteLine();

    // Compare each pair
    var webComparer = new WebAppConfigComparer();

    for (int i = 0; i < successful.Count; i++)
    {
        for (int j = i + 1; j < successful.Count; j++)
        {
            var leftName  = successful[i].Key;
            var rightName = successful[j].Key;

            if (!webAppFiles.TryGetValue(leftName,  out var leftFiles))  continue;
            if (!webAppFiles.TryGetValue(rightName, out var rightFiles)) continue;

            var leftEnv  = targets.FirstOrDefault(e => e.Name == leftName);
            var rightEnv = targets.FirstOrDefault(e => e.Name == rightName);

            var appDiffs = webComparer.Compare(
                leftFiles, leftName, rightFiles, rightName,
                leftEnv  is not null ? new[] { leftEnv.Host }  : null,
                rightEnv is not null ? new[] { rightEnv.Host } : null);

            var pairKey = $"{leftName} ↔ {rightName}";
            webAppPairResults.Add((pairKey, appDiffs));

            PrintWebAppResults(leftName, rightName, appDiffs);
            if (appDiffs.Count > 0) exitCode = 2;
        }
    }
}

if (htmlOutput && results.Count > 0)
{
    var generator  = new HtmlReportGenerator();
    var html = generator.Generate(results, failed,
        webAppPairResults.Count > 0 ? webAppPairResults : null);
    var reportPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"iis-compare-{DateTime.Now:yyyyMMdd-HHmmss}.html");
    await File.WriteAllTextAsync(reportPath, html);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  HTML report saved to: {reportPath}");
    Console.ResetColor();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(reportPath) { UseShellExecute = true });
}

return exitCode;

// ─── Helper methods ──────────────────────────────────────────────────────────
static void PrintEnvironmentList(List<EnvironmentConfig> environments)
{
    Console.WriteLine("Configured environments:");
    foreach (var env in environments)
        Console.WriteLine($"  {env.Name,-20} {env.Host,-30} [{env.Method}]");
}

static void PrintSslDifferences(string leftName, string rightName, List<string> diffs)
{
    var header = $"  {leftName}  ←→  {rightName}  [SSL Certificate Bindings]";
    Console.WriteLine(header);
    Console.WriteLine(new string('─', Math.Max(header.Length, 40)));

    if (diffs.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ SSL certificate bindings are identical.");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  {diffs.Count} SSL difference(s):");
        Console.ResetColor();
        foreach (var d in diffs)
        {
            Console.ForegroundColor = d.StartsWith("[ONLY IN LEFT") ? ConsoleColor.Red
                                    : d.StartsWith("[ONLY IN RIGHT") ? ConsoleColor.Blue
                                    : ConsoleColor.Yellow;
            Console.WriteLine($"    {d}");
            Console.ResetColor();
        }
    }
    Console.WriteLine();
}

static int CompareLocalFiles(string leftPath, string rightPath)
{
    if (!File.Exists(leftPath)) { Console.Error.WriteLine($"File not found: {leftPath}"); return 1; }
    if (!File.Exists(rightPath)) { Console.Error.WriteLine($"File not found: {rightPath}"); return 1; }

    var leftXml = File.ReadAllText(leftPath);
    var rightXml = File.ReadAllText(rightPath);
    var comparer = new IISConfigComparer();
    var result = comparer.Compare(leftXml, Path.GetFileName(leftPath), rightXml, Path.GetFileName(rightPath));

    Console.WriteLine("IIS Configuration Comparer — Local File Comparison");
    Console.WriteLine(new string('═', 60));
    Console.WriteLine();
    PrintComparisonResult(result);
    return result.IsIdentical ? 0 : 2;
}


static void PrintComparisonResult(ComparisonResult result)
{
    var header = $"  {result.LeftEnvironment}  ←→  {result.RightEnvironment}";
    Console.WriteLine(header);
    Console.WriteLine(new string('─', Math.Max(header.Length, 40)));

    if (result.IsIdentical)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ Configurations are identical.");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  {result.Differences.Count} difference(s) found:");
        Console.ResetColor();
        Console.WriteLine();

        var bySection = result.Differences.GroupBy(d => d.Section);
        foreach (var group in bySection)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [{group.Key}]");
            Console.ResetColor();

            foreach (var diff in group)
            {
                Console.ForegroundColor = diff.Kind switch
                {
                    DifferenceKind.OnlyInLeft => ConsoleColor.Red,
                    DifferenceKind.OnlyInRight => ConsoleColor.Blue,
                    _ => ConsoleColor.Yellow
                };
                Console.WriteLine($"    {diff}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }
    }

    Console.WriteLine();
}

static async Task<int> SaveConfigToFile(
    string envName, string outputPath, List<EnvironmentConfig> environments)
{
    var env = environments.FirstOrDefault(e =>
        e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));
    if (env is null) { Console.Error.WriteLine($"Unknown environment: {envName}"); return 1; }

    var fetchers = new List<IConfigFetcher>
    {
        new UncConfigFetcher(), new WinRmConfigFetcher(), new SshConfigFetcher()
    };
    var factory = new ConfigFetcherFactory(fetchers);

    Console.Write($"Fetching config from '{env.Name}'... ");
    try
    {
        var xml = await factory.FetchAsync(env);
        await File.WriteAllTextAsync(outputPath, xml);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Saved to '{outputPath}'");
        Console.ResetColor();
        return 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
}

/// <summary>
/// Lists all IIS app config files found on an environment using already-known app paths.
/// </summary>
static async Task<int> ExploreWebApp(string envName, List<EnvironmentConfig> environments)
{
    var env = environments.FirstOrDefault(e =>
        e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));
    if (env is null) { Console.Error.WriteLine($"Unknown environment: {envName}"); return 1; }

    Console.WriteLine($"Fetching IIS config from '{env.Name}' to discover app paths...");
    var fetchers = new List<IConfigFetcher> { new UncConfigFetcher(), new WinRmConfigFetcher(), new SshConfigFetcher() };
    var iisXml = await new ConfigFetcherFactory(fetchers).FetchAsync(env);
    var appPaths = ExtractAppPhysicalPaths(iisXml);
    Console.WriteLine($"  Found {appPaths.Count} IIS applications.");
    Console.WriteLine();

    Console.WriteLine("Scanning for config files...");
    var files = await new WebAppConfigFetcher().FetchAsync(env, appPaths);

    if (files.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  No config files found.");
        Console.ResetColor();
        return 0;
    }

    foreach (var app in files.GroupBy(f => f.AppName).OrderBy(g => g.Key))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  [{app.Key}]");
        Console.ResetColor();
        foreach (var file in app)
            Console.WriteLine($"    {file.FileName}  ({file.Content.Split('\n').Length} lines)");
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Total: {files.Count} config file(s) across {files.Select(f => f.AppName).Distinct().Count()} app(s).");
    Console.ResetColor();
    return 0;
}

/// <summary>
/// Extracts IIS application physical paths from ApplicationHost.config XML,
/// returning (appName, windowsPath) where appName is the last segment of the path.
/// </summary>
static List<(string AppName, string WindowsPath)> ExtractAppPhysicalPaths(string applicationHostXml)
{
    var result = new List<(string, string)>();
    var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        var doc   = System.Xml.Linq.XDocument.Parse(applicationHostXml);
        var sites = doc.Descendants("site");

        foreach (var site in sites)
        {
            foreach (var app in site.Descendants("application"))
            {
                var vdir  = app.Descendants("virtualDirectory").FirstOrDefault();
                var physPath = vdir?.Attribute("physicalPath")?.Value;
                if (string.IsNullOrWhiteSpace(physPath)) continue;

                // Normalise environment variables like %SystemDrive% if present
                physPath = physPath.Trim();

                var appName = Path.GetFileName(physPath.TrimEnd('\\'));
                if (string.IsNullOrEmpty(appName)) continue;

                // Deduplicate by physical path
                if (!seen.Add(physPath)) continue;
                result.Add((appName, physPath));
            }
        }
    }
    catch { /* ignore parse errors */ }

    return result;
}

/// <summary>Prints webapp comparison results to the console.</summary>
static void PrintWebAppResults(string leftName, string rightName, List<ComparisonResult> appDiffs)
{
    var header = $"  {leftName}  ←→  {rightName}  [App Configuration]";
    Console.WriteLine(header);
    Console.WriteLine(new string('─', Math.Max(header.Length, 40)));

    if (appDiffs.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ All app configurations are identical (ignoring env-specific values).");
        Console.ResetColor();
    }
    else
    {
        var totalDiffs = appDiffs.Sum(r => r.Differences.Count);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  {appDiffs.Count} app(s) with {totalDiffs} difference(s):");
        Console.ResetColor();
        Console.WriteLine();

        foreach (var appResult in appDiffs)
        {
            var byFile = appResult.Differences.GroupBy(d =>
            {
                // ElementPath is "web.config/appSettings/key" — extract the file part
                var slash = d.ElementPath.IndexOf('/');
                return slash >= 0 ? d.ElementPath[..slash] : d.ElementPath;
            });

            foreach (var fileGroup in byFile)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  [{appResult.Differences.First().Section} / {fileGroup.Key}]");
                Console.ResetColor();

                foreach (var diff in fileGroup)
                {
                    Console.ForegroundColor = diff.Kind switch
                    {
                        DifferenceKind.OnlyInLeft  => ConsoleColor.Red,
                        DifferenceKind.OnlyInRight => ConsoleColor.Blue,
                        _                          => ConsoleColor.Yellow
                    };
                    Console.WriteLine($"    {diff}");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }
    }

    Console.WriteLine();
}

static async Task<int> CompareLocalVsRemote(
    string localPath, string envName, List<EnvironmentConfig> environments)
{
    if (!File.Exists(localPath))
    {
        Console.Error.WriteLine($"Local file not found: {localPath}");
        return 1;
    }

    var env = environments.FirstOrDefault(e =>
        e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));
    if (env is null) { Console.Error.WriteLine($"Unknown environment: {envName}"); return 1; }

    var localXml = await File.ReadAllTextAsync(localPath);
    var fetchers = new List<IConfigFetcher>
    {
        new UncConfigFetcher(), new WinRmConfigFetcher(), new SshConfigFetcher()
    };
    var factory = new ConfigFetcherFactory(fetchers);
    var comparer = new IISConfigComparer();

    Console.Write($"Fetching config from '{env.Name}'... ");
    try
    {
        var remoteXml = await factory.FetchAsync(env);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("OK");
        Console.ResetColor();
        Console.WriteLine();

        var result = comparer.Compare(localXml, Path.GetFileName(localPath), remoteXml, env.Name);
        PrintComparisonResult(result);
        return result.IsIdentical ? 0 : 2;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
}

