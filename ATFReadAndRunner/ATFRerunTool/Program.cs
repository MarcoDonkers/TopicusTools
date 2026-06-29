using ATFRerunTool.Configuration;
using ATFRerunTool.Orchestration;
using ATFRerunTool.Parsing;
using ATFRerunTool.Reporting;
using System.Diagnostics;
using System.Text.Json;

namespace ATFRerunTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintBanner();

        try
        {
            // Load settings
            var settings = LoadSettings();

            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                if (args.Length == 0)
                {
                    // No args at all — try the file picker first
                    return await RunRerunCommand(args, settings);
                }
                PrintHelp(settings);
                return 0;
            }

            // Determine mode
            var command = args[0].ToLowerInvariant();

            return command switch
            {
                "rerun" => await RunRerunCommand(args, settings),
                "report" => RunReportCommand(args, settings),
                _ => await RunRerunCommand(args, settings), // default: treat first arg as log path
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\nUnhandled error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey(intercept: true);
            return 1;
        }
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    private static async Task<int> RunRerunCommand(string[] args, Settings settings)
    {
        string? sLogPath = null;
        string? rLogPath = null;

        // CLI path: accept up to 2 positional log files
        var positional = args
            .Where(a => !a.Equals("rerun", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.StartsWith('-'))
            .ToList();

        if (positional.Count >= 1) sLogPath = positional[0];
        if (positional.Count >= 2) rLogPath = positional[1];

        // Interactive picker when paths weren't supplied on the command line
        if (sLogPath is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Step 1/2 — Select the STATE (S) Jenkins log:");
            Console.ResetColor();
            sLogPath = PickLogFile(settings, allowSkip: false, prefix: "S");
            if (sLogPath is null) return 0;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nStep 2/2 — Select the REGRESSION (R) Jenkins log (or 0 to skip):");
            Console.ResetColor();
            rLogPath = PickLogFile(settings, allowSkip: true, prefix: "R");
        }

        // Validate
        if (!File.Exists(sLogPath))
        {
            Console.Error.WriteLine($"Error: S log file not found: {sLogPath}");
            return 1;
        }
        if (rLogPath is not null && !File.Exists(rLogPath))
        {
            Console.Error.WriteLine($"Error: R log file not found: {rLogPath}");
            return 1;
        }

        // Build run name and results folder early so we can check for previous state
        var sName = Path.GetFileNameWithoutExtension(sLogPath);
        var rName = rLogPath is not null ? Path.GetFileNameWithoutExtension(rLogPath) : null;
        var runName = rName is not null ? $"{sName}-{rName}" : sName;
        settings.ResultsOutputDirectory = Path.Combine(settings.ResultsOutputDirectory, runName);

        // Load the KnownToBeBroken list from the ATFRun folder
        var atfRunFolder = Path.GetDirectoryName(sLogPath) ?? Directory.GetCurrentDirectory();
        var knownBroken = KnownBrokenList.Load(atfRunFolder);
        if (knownBroken.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\nKnown-to-be-broken (will be skipped):");
            foreach (var id in knownBroken.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  - {id}");
            Console.ResetColor();
        }

        // Check for a previous session for this run
        var previousState = LoadPreviousState(settings.ResultsOutputDirectory);
        List<ATFRerunTool.Models.TestJob> jobs;
        List<string> knownBrokenThatPassed;

        if (previousState is not null && previousState.StillFailingTestIds.Count > 0)
        {
            // Filter out known-broken from the displayed/used previous failures
            var relevantFailing = previousState.StillFailingTestIds
                .Where(id => !knownBroken.Contains(id))
                .ToList();

            if (relevantFailing.Count == 0)
            {
                // All previously-failing jobs are now known-broken — nothing actionable
                Console.WriteLine("\nAll previously-failing jobs are in the known-to-be-broken list. Nothing to rerun.");
                var (_, kbP) = ParseAndMerge(sLogPath, rLogPath, knownBroken);
                knownBrokenThatPassed = kbP;
                jobs = [];
            }
            else
            {
                var skippedCount = previousState.StillFailingTestIds.Count - relevantFailing.Count;
                Console.WriteLine($"\nPrevious run found ({previousState.CompletedAt:dd-MM-yyyy HH:mm}).");
                Console.WriteLine($"{relevantFailing.Count} job(s) were still failing{(skippedCount > 0 ? $" ({skippedCount} known-broken excluded)" : "")}:");
                foreach (var id in relevantFailing)
                    Console.WriteLine($"  - {id}");

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("\nContinue from previous results? [Y/n]: ");
                Console.ResetColor();
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                bool usePrevious = answer is "" or "y" or "yes";

                if (usePrevious)
                {
                    // Re-parse logs to get full job metadata but filter to only the relevant (non-known-broken) failing ids
                    var (allJobs, kbPassed) = ParseAndMerge(sLogPath, rLogPath, knownBroken);
                    knownBrokenThatPassed = kbPassed;
                    var failingIds = new HashSet<string>(relevantFailing, StringComparer.OrdinalIgnoreCase);
                    jobs = allJobs.Where(j => failingIds.Contains(j.TestId)).ToList();

                    // If a previously-failing id isn't in the logs (edge case), add a minimal job entry
                    foreach (var id in relevantFailing)
                    {
                        if (!jobs.Any(j => string.Equals(j.TestId, id, StringComparison.OrdinalIgnoreCase)))
                        {
                            jobs.Add(new ATFRerunTool.Models.TestJob
                            {
                                TestId = id,
                                CategoryS = "S" + id,
                                CategoryR = "R" + id,
                                Source = "previous session",
                            });
                        }
                    }
                    jobs.Sort((a, b) => string.Compare(a.TestId, b.TestId, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var (allJobs, kbPassed) = ParseAndMerge(sLogPath, rLogPath, knownBroken);
                    jobs = allJobs;
                    knownBrokenThatPassed = kbPassed;
                }
            }
        }
        else
        {
            var (allJobs, kbPassed) = ParseAndMerge(sLogPath, rLogPath, knownBroken);
            jobs = allJobs;
            knownBrokenThatPassed = kbPassed;
        }

        // Notify about known-broken tests that passed in Jenkins and offer removal
        if (knownBrokenThatPassed.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"🎉 {knownBrokenThatPassed.Count} known-to-be-broken test(s) PASSED in Jenkins this run:");
            Console.ResetColor();
            for (int i = 0; i < knownBrokenThatPassed.Count; i++)
                Console.WriteLine($"  [{i + 1}] {knownBrokenThatPassed[i]}");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nEnter numbers to remove from the broken list (comma-separated), or press Enter to skip:");
            Console.ResetColor();
            var removal = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(removal))
            {
                var toRemove = removal.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => int.TryParse(s, out _))
                    .Select(s => int.Parse(s) - 1)
                    .Where(idx => idx >= 0 && idx < knownBrokenThatPassed.Count)
                    .Select(idx => knownBrokenThatPassed[idx])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (toRemove.Count > 0)
                {
                    var updated = knownBroken.Where(id => !toRemove.Contains(id)).ToList();
                    KnownBrokenList.Save(atfRunFolder, updated);
                    Console.ForegroundColor = ConsoleColor.Green;
                    foreach (var id in toRemove)
                        Console.WriteLine($"  ✓ Removed '{id}' from KnownToBeBroken.txt");
                    Console.ResetColor();
                }
            }
        }


        if (jobs.Count == 0)
        {
            Console.WriteLine("No failing jobs to rerun. All clear!");
            return 0;
        }

        Console.WriteLine($"\nJobs to rerun ({jobs.Count}):");
        foreach (var job in jobs)
            Console.WriteLine($"  S: {job.CategoryS,-40}  R: {job.CategoryR}");

        Console.WriteLine($"\nResults folder: {settings.ResultsOutputDirectory}");

        // Ask about retries (default: No — run once without extra rounds)
        settings.MaxRerunCount = AskRetries(settings.MaxRerunCount);

        // Ask for parallelism
        settings.MaxParallelism = AskParallelism(settings.MaxParallelism);

        // Validate prerequisites
        if (!settings.Jenkins.Enabled)
        {
            if (!File.Exists(settings.NUnitConsolePath))
            {
                Console.Error.WriteLine($"Error: nunit3-console.exe not found at: {settings.NUnitConsolePath}");
                Console.Error.WriteLine("Update NUnitConsolePath in appsettings.json.");
                return 1;
            }
            if (!File.Exists(settings.TestDllPath))
            {
                Console.Error.WriteLine($"Error: Test DLL not found at: {settings.TestDllPath}");
                Console.Error.WriteLine("Build the ATF solution first, or update TestDllPath in appsettings.json.");
                return 1;
            }
        }

        // Run
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancellation requested – finishing current test and saving report...");
            cts.Cancel();
        };

        var orchestrator = new RerunOrchestrator(settings);
        var session = await orchestrator.RunAsync(runName, jobs, cts.Token);

        // Persist state for next run
        SavePreviousState(settings.ResultsOutputDirectory, session);

        // Save HTML report
        var reportPath = HtmlReportGenerator.Save(session, settings.ResultsOutputDirectory);
        Console.WriteLine($"\nReport saved to: {reportPath}");

        try { Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); }
        catch { /* non-critical */ }

        bool hasFailures = session.Jobs.Any(j => session.GetVerdict(j) == ATFRerunTool.Models.JobVerdict.Fail);
        return hasFailures ? 2 : 0;
    }

    /// <summary>
    /// Parses failures from the S log and optionally the R log, then merges them
    /// into a deduplicated list keyed on TestId, excluding any known-broken tests.
    /// Returns the filtered job list and the set of known-broken IDs that passed in Jenkins.
    /// </summary>
    private static (List<ATFRerunTool.Models.TestJob> Jobs, List<string> KnownBrokenThatPassed)
        ParseAndMerge(string sLogPath, string? rLogPath, HashSet<string> knownBroken)
    {
        var dict = new Dictionary<string, ATFRerunTool.Models.TestJob>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> sSuccessIds = [];
        HashSet<string> rSuccessIds = [];

        void AddFrom(string path, ref HashSet<string> successOut)
        {
            Console.WriteLine($"Parsing: {path}");
            List<ATFRerunTool.Models.TestJob> parsed;
            try { parsed = JenkinsLogParser.Parse(path); }
            catch (Exception ex) { Console.Error.WriteLine($"  Warning: could not parse {path}: {ex.Message}"); return; }

            try { successOut = JenkinsLogParser.ParseSuccessIds(path); }
            catch { successOut = []; }

            foreach (var job in parsed)
            {
                if (knownBroken.Contains(job.TestId))
                {
                    Console.WriteLine($"  Skipping {job.TestId} (known to be broken).");
                    continue;
                }
                if (!dict.ContainsKey(job.TestId))
                    dict[job.TestId] = job;
            }
        }

        AddFrom(sLogPath, ref sSuccessIds);
        if (rLogPath is not null) AddFrom(rLogPath, ref rSuccessIds);

        var result = dict.Values.ToList();
        result.Sort((a, b) => string.Compare(a.TestId, b.TestId, StringComparison.OrdinalIgnoreCase));

        // A known-broken test is only suggested for removal when it passed in BOTH S and R logs
        // (or just S when there is no R log — no R runner means S success is sufficient).
        var knownBrokenThatPassed = knownBroken
            .Where(id =>
            {
                bool sGreen = sSuccessIds.Contains(id);
                bool rGreen = rLogPath is null || rSuccessIds.Contains(id);
                return sGreen && rGreen;
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (result, knownBrokenThatPassed);
    }

    private static int RunReportCommand(string[] args, Settings settings)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ATFRerunTool report <session-output-directory>");
            return 1;
        }
        Console.WriteLine("Re-generating report from existing XML results is not yet implemented.");
        Console.WriteLine("Run 'ATFRerunTool rerun <log-file>' to generate a new report.");
        return 0;
    }

    // ─── File Picker ──────────────────────────────────────────────────────────

    private static int AskRetries(int configuredMax)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"Enable retries? Up to {configuredMax} extra rounds if a test fails [y/N]: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input is "y" or "yes")
        {
            Console.WriteLine($"  → Retries enabled (max {configuredMax} rounds).");
            return configuredMax;
        }

        Console.WriteLine("  → No retries. Each test runs once.");
        return 1;
    }

    private static ATFRerunTool.Models.PreviousRunState? LoadPreviousState(string resultsDir)
    {
        var path = Path.Combine(resultsDir, "previous_state.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<ATFRerunTool.Models.PreviousRunState>(json);
        }
        catch { return null; }
    }

    private static void SavePreviousState(string resultsDir, ATFRerunTool.Models.RerunSession session)
    {
        try
        {
            Directory.CreateDirectory(resultsDir);
            var failingIds = session.Jobs
                .Where(j => session.GetVerdict(j) == ATFRerunTool.Models.JobVerdict.Fail)
                .Select(j => j.TestId)
                .ToList();

            var state = new ATFRerunTool.Models.PreviousRunState
            {
                RunName = session.RunName,
                CompletedAt = session.FinishedAt,
                StillFailingTestIds = failingIds,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(state,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(resultsDir, "previous_state.json"), json);

            if (failingIds.Count == 0)
                Console.WriteLine("All jobs green — previous state cleared.");
            else
                Console.WriteLine($"State saved: {failingIds.Count} still-failing job(s) recorded for next run.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not save previous state: {ex.Message}");
        }
    }

    private static int AskParallelism(int defaultValue)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"Parallel jobs per round [default {defaultValue}]: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return defaultValue;

        if (int.TryParse(input, out int value) && value >= 1)
            return value;

        Console.WriteLine($"Invalid input — using default ({defaultValue}).");
        return defaultValue;
    }

    /// <summary>
    /// Searches for .txt files in the ATFRun folder(s) and lets the user pick one.
    /// Returns null if the user cancels or no files are found.
    /// </summary>
    private static string? PickLogFile(Settings settings, bool allowSkip = false, string? prefix = null)
    {
        var candidates = FindAtfRunFolders(settings);
        var pattern = prefix is not null ? $"{prefix}*.txt" : "*.txt";
        var files = candidates
            .SelectMany(dir => Directory.GetFiles(dir, pattern))
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No .txt log files found in ATFRun folder(s).");
            Console.WriteLine("Pass the log file path as an argument: ATFRerunTool <path-to-log>");
            return null;
        }

        Console.WriteLine();
        for (int i = 0; i < files.Count; i++)
        {
            var fi = new FileInfo(files[i]);
            Console.WriteLine($"  [{i + 1,3}]  {fi.Name,-30}  {fi.LastWriteTime:dd-MM-yyyy HH:mm}  ({fi.Length / 1024} KB)");
        }

        var skipLabel = allowSkip ? "None / Skip" : "Exit";
        Console.WriteLine($"\n  [  0]  {skipLabel}");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Enter number: ");
            var input = Console.ReadLine()?.Trim();
            if (input == "0" || string.IsNullOrEmpty(input)) return null;

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= files.Count)
            {
                Console.WriteLine();
                return files[choice - 1];
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Invalid choice. Enter a number between 0 and {files.Count}.");
            Console.ResetColor();
        }
    }

    private static List<string> FindAtfRunFolders(Settings settings)
    {
        var execDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        return [Path.Combine(execDir, "ATFRun")];
    }

    // ─── Settings ─────────────────────────────────────────────────────────────

    private static Settings LoadSettings()
    {
        var execDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        var configPath = Path.Combine(execDir, "appsettings.json");

        if (!File.Exists(configPath))
        {
            // Fall back to current directory
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"No appsettings.json found – using defaults. Create one next to the executable to customise.");
            return ResolveRelativePaths(new Settings(), execDir);
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var settings = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            }) ?? new Settings();

            return ResolveRelativePaths(settings, execDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not parse appsettings.json ({ex.Message}). Using defaults.");
            return ResolveRelativePaths(new Settings(), execDir);
        }
    }

    private static Settings ResolveRelativePaths(Settings settings, string execDir)
    {
        if (!Path.IsPathRooted(settings.ResultsOutputDirectory))
        {
            settings.ResultsOutputDirectory = Path.GetFullPath(
                Path.Combine(execDir, settings.ResultsOutputDirectory));
        }
        return settings;
    }

    // ─── Help / Banner ────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ╔═══════════════════════════════════════════╗
  ║         ATF Rerun Tool  v1.0              ║
  ║   Reads Jenkins failures → reruns locally ║
  ╚═══════════════════════════════════════════╝");
        Console.ResetColor();
    }

    private static void PrintHelp(Settings settings)
    {
        Console.WriteLine(@"
Usage:
  ATFRerunTool <jenkins-log-file>          Rerun failed tests from Jenkins log
  ATFRerunTool rerun <jenkins-log-file>    Same as above
  ATFRerunTool report <results-dir>        Re-open report (not yet implemented)
  ATFRerunTool --help                      Show this help

Examples:
  ATFRerunTool C:\path\to\ATFRun\3903.txt
  ATFRerunTool rerun ""C:\path\to\jenkins-build-3903.txt""

Configuration (appsettings.json next to executable):
");
        Console.WriteLine($"  TestDllPath        : {settings.TestDllPath}");
        Console.WriteLine($"  NUnitConsolePath   : {settings.NUnitConsolePath}");
        Console.WriteLine($"  ResultsOutputDir   : {settings.ResultsOutputDirectory}");
        Console.WriteLine($"  MaxRerunCount      : {settings.MaxRerunCount}");
        Console.WriteLine($"  Jenkins.Enabled    : {settings.Jenkins.Enabled}");
        Console.WriteLine(@"
Rerun logic:
  1. Parse Jenkins log → collect all FAILURE job names
  2. Map each job name to S (State) and R (Regression) NUnit categories
  3. Round 1..MaxRerunCount:
       For each still-failing job: run S test, then R test
       Remove jobs that fully pass
  4. Generate HTML report with per-round results, error messages and stack traces
");
    }
}
