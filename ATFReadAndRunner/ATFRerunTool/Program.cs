using ATFRerunTool.Configuration;
using ATFRerunTool.Jenkins;
using ATFRerunTool.Orchestration;
using ATFRerunTool.Parsing;
using ATFRerunTool.Reporting;
using ATFRerunTool.Running;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
            var startMode = PickStartMode();
            if (startMode == StartMode.Exit) return 0;
            if (startMode == StartMode.RunAll)
                return await RunAllTestsAsync(settings);

            var logSource = PickLogSource();
            if (logSource == LogSource.Exit) return 0;

            if (logSource == LogSource.Jenkins)
            {
                (sLogPath, rLogPath) = await PickJenkinsRunsAsync(settings);
                if (sLogPath is null) return 0;
            }
            else
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


        // Ask if the database was reset — unless the setup job is already in the
        // list, in which case the orchestrator runs it alone first anyway.
        bool setupInJobs = ContainsSetupJob(settings, jobs);
        if (setupInJobs)
            Console.WriteLine($"\n{settings.DatabaseReset.SetupCategory} is in the job list — it will run alone first; the rest waits for it.");
        bool dbWasReset = !setupInJobs && AskDatabaseReset(settings);

        if (jobs.Count == 0)
        {
            Console.WriteLine("No failing jobs to rerun. All clear!");
            return 0;
        }

        Console.WriteLine($"\nJobs to rerun ({jobs.Count}):");
        foreach (var job in jobs)
            Console.WriteLine($"  S: {job.CategoryS,-40}  R: {job.CategoryR}");

        Console.WriteLine($"\nResults folder: {settings.ResultsOutputDirectory}");

        // Ask for branch to test on
        AskBranch(settings);

        // Ask for environment and apply config
        ApplyEnvironmentConfig(settings);

        // Ask whether the browser should run headless and patch the test config
        AskHeadless(settings);

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
                Console.Error.WriteLine("Check GitRepoPath in appsettings.json.");
                return 1;
            }
            if (!File.Exists(settings.TestDllPath))
            {
                Console.Error.WriteLine($"Error: Test DLL not found at: {settings.TestDllPath}");
                Console.Error.WriteLine("Build the ATF solution first, or check GitRepoPath in appsettings.json.");
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

        // If DB was reset, run S000_BasisOpzetten alone before handing off to the orchestrator
        if (dbWasReset && !settings.Jenkins.Enabled)
            await RunDatabaseSetupAsync(settings, cts.Token);

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

    // ─── Branch Picker ───────────────────────────────────────────────────────

    private record GitBranch(string Name, bool IsCurrent, DateTime? LastCommit);

    private static List<GitBranch>? GetGitBranches(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("branch");
            psi.ArgumentList.Add("--sort=-committerdate");
            psi.ArgumentList.Add("--format=%(HEAD)|%(refname:short)|%(committerdate:iso8601)");

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) return null;

            var branches = new List<GitBranch>();
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Split('|');
                if (parts.Length < 2) continue;
                bool isCurrent = parts[0].Trim() == "*";
                var name = parts[1].Trim();
                DateTime? date = null;
                if (parts.Length >= 3 && DateTimeOffset.TryParse(parts[2].Trim(), out var dto))
                    date = dto.LocalDateTime;
                if (!string.IsNullOrEmpty(name))
                    branches.Add(new GitBranch(name, isCurrent, date));
            }
            return branches;
        }
        catch { return null; }
    }

    private static void CheckoutBranch(string repoPath, string branchName)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  → Checking out: {branchName}...");
        Console.ResetColor();

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("checkout");
        psi.ArgumentList.Add(branchName);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Git checkout failed: {stderr.Trim()}");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ Switched to: {branchName}");
            Console.ResetColor();
        }
    }

    private static void AskBranch(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.GitRepoPath) || !Directory.Exists(settings.GitRepoPath))
            return;

        var branches = GetGitBranches(settings.GitRepoPath);
        if (branches is null || branches.Count == 0) return;

        var currentName = branches.FirstOrDefault(b => b.IsCurrent)?.Name ?? "(none)";
        int selectedIdx = Math.Max(0, branches.FindIndex(b => b.IsCurrent));

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Select branch (current: {currentName}):");
        Console.ResetColor();

        const int maxVisible = 10;
        // fixed area height: top-scroll + list + bottom-scroll + search + hint
        const int totalLines = maxVisible + 4;

        int listStartRow = Console.CursorTop;
        for (int i = 0; i < totalLines; i++) Console.WriteLine(); // reserve space

        string filter = "";

        while (true)
        {
            var filtered = branches
                .Where(b => string.IsNullOrEmpty(filter) ||
                            b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (selectedIdx >= filtered.Count)
                selectedIdx = Math.Max(0, filtered.Count - 1);

            int windowStart = Math.Max(0, selectedIdx - maxVisible / 2);
            if (windowStart + maxVisible > filtered.Count)
                windowStart = Math.Max(0, filtered.Count - maxVisible);
            int windowEnd = Math.Min(filtered.Count, windowStart + maxVisible);

            Console.SetCursorPosition(0, listStartRow);

            // Top scroll indicator
            BranchPrintLine(windowStart > 0 ? $"  ↑ {windowStart} more" : "");

            // Branch list (always maxVisible rows)
            for (int row = 0; row < maxVisible; row++)
            {
                int idx = windowStart + row;
                if (idx < windowEnd)
                {
                    var b = filtered[idx];
                    bool sel = idx == selectedIdx;

                    if (sel) { Console.BackgroundColor = ConsoleColor.DarkCyan; Console.ForegroundColor = ConsoleColor.White; }
                    else if (b.IsCurrent) { Console.ForegroundColor = ConsoleColor.Green; }

                    var marker = b.IsCurrent ? "*" : " ";
                    var dateStr = b.LastCommit?.ToString("yyyy-MM-dd") ?? "          ";
                    BranchPrintLine($"  {marker} {b.Name,-50} {dateStr}");
                    Console.ResetColor();
                }
                else
                {
                    BranchPrintLine("");
                }
            }

            // Bottom scroll indicator
            BranchPrintLine(windowEnd < filtered.Count ? $"  ↓ {filtered.Count - windowEnd} more" : "");

            // Search input
            Console.ForegroundColor = ConsoleColor.Cyan;
            BranchPrintLine($"  Search: {filter}█");
            Console.ResetColor();

            // Hint (no trailing newline — last line of reserved area)
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var hint = "  Enter=checkout  Esc=keep current  ↑↓=navigate  Backspace=clear";
            Console.Write(hint.PadRight(Math.Max(hint.Length, Console.WindowWidth - 1)));
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    BranchClearArea(listStartRow, totalLines);
                    Console.SetCursorPosition(0, listStartRow);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  Branch unchanged: {currentName}");
                    Console.ResetColor();
                    Console.WriteLine();
                    return;

                case ConsoleKey.Enter:
                    if (filtered.Count == 0) break;
                    var chosen = filtered[selectedIdx];
                    BranchClearArea(listStartRow, totalLines);
                    Console.SetCursorPosition(0, listStartRow);

                    if (chosen.IsCurrent)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"  Branch unchanged: {chosen.Name}");
                        Console.ResetColor();
                        Console.WriteLine();
                        return;
                    }

                    // Check for uncommitted changes before switching
                    var dirtyFiles = GetDirtyFiles(settings.GitRepoPath);
                    if (dirtyFiles.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  Uncommitted changes on '{currentName}':");
                        Console.ResetColor();
                        foreach (var df in dirtyFiles)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"    {df}");
                            Console.ResetColor();
                        }
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"  Discard all changes and switch to '{chosen.Name}'? [y/N]: ");
                        Console.ResetColor();
                        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                        if (confirm is not ("y" or "yes"))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine("  Branch unchanged.");
                            Console.ResetColor();
                            Console.WriteLine();
                            return;
                        }
                        DiscardChanges(settings.GitRepoPath);
                    }

                    CheckoutBranch(settings.GitRepoPath, chosen.Name);
                    Console.WriteLine();
                    return;

                case ConsoleKey.UpArrow:
                    if (selectedIdx > 0) selectedIdx--;
                    break;

                case ConsoleKey.DownArrow:
                    if (filtered.Count > 0 && selectedIdx < filtered.Count - 1) selectedIdx++;
                    break;

                case ConsoleKey.Backspace:
                    if (filter.Length > 0) { filter = filter[..^1]; selectedIdx = 0; }
                    break;

                default:
                    if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                    { filter += key.KeyChar; selectedIdx = 0; }
                    break;
            }
        }
    }

    private static List<string> GetDirtyFiles(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Length >= 2 && line[..2] != "??") // ignore untracked
                .ToList();
        }
        catch { return []; }
    }

    private static void DiscardChanges(string repoPath)
    {
        // Unstage then restore working tree; ignore errors (e.g. nothing staged)
        RunSilentGit(repoPath, "restore", "--staged", ".");
        RunSilentGit(repoPath, "restore", ".");
    }

    private static void RunSilentGit(string repoPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.WaitForExit();
    }

    private static void BranchPrintLine(string content)
    {
        int w = Console.WindowWidth - 1;
        Console.WriteLine(w > content.Length ? content.PadRight(w) : content);
    }

    private static void BranchClearArea(int startRow, int lines)
    {
        Console.SetCursorPosition(0, startRow);
        int w = Math.Max(1, Console.WindowWidth - 1);
        for (int i = 0; i < lines; i++)
            Console.WriteLine(new string(' ', w));
    }

    // ─── Environment config ───────────────────────────────────────────────────

    private static void ApplyEnvironmentConfig(Settings settings)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Select environment:");
        Console.ResetColor();

        foreach (var env in settings.Environments)
            Console.WriteLine($"  {env.Number,3}) {env.Host}");

        string? envHost = null;
        while (envHost is null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\nMake a choice: ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();
            envHost = settings.Environments.FirstOrDefault(e => e.Number == input)?.Host;

            if (envHost is null)
                Console.WriteLine($"  \"{input}\" is not a valid option.");
        }

        Console.WriteLine($"  → Configuring for {envHost}...");

        if (string.IsNullOrEmpty(settings.GitRepoPath) || !Directory.Exists(settings.GitRepoPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: GitRepoPath '{settings.GitRepoPath}' does not exist.");
            Console.Error.WriteLine("Set GitRepoPath in appsettings.json to your local QSP.Core repository path.");
            Console.ResetColor();
            return;
        }

        var psScript = Path.Combine(settings.GitRepoPath, settings.ConfigScriptRelativePath);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{psScript}\" -environment \"{envHost}\"",
            WorkingDirectory = settings.GitRepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  ERR: " + e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Warning: config script exited with code {process.ExitCode}");
            Console.ResetColor();
        }
        else
        {
            SyncAppConfigToDllConfig(settings);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Config applied.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// The transform script updates the project's App.config, but NUnit reads the
    /// DLL's .config next to the built DLL — copy it over so the selected
    /// environment actually takes effect without a rebuild.
    /// </summary>
    private static void SyncAppConfigToDllConfig(Settings settings)
    {
        var appConfig = GetAppConfigPath(settings);
        var dllConfig = settings.TestDllPath + ".config";

        if (appConfig is null || !File.Exists(appConfig) || !File.Exists(dllConfig))
            return;

        try
        {
            File.Copy(appConfig, dllConfig, overwrite: true);
            Console.WriteLine($"  → Synced App.config to {Path.GetFileName(dllConfig)}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Warning: could not sync App.config to DLL config: {ex.Message}");
            Console.WriteLine($"  Tests may run against the wrong environment!");
            Console.ResetColor();
        }
    }

    private static string? GetAppConfigPath(Settings settings)
    {
        var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(settings.TestDllPath));
        return projectDir is null ? null : Path.Combine(projectDir, "App.config");
    }

    // ─── Headless ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks whether the browser should run headless and patches the "Headless"
    /// appSetting in the test configs (the App.config that TransformConfig.ps1
    /// maintains, plus the DLL .config that NUnit actually reads at run time).
    /// </summary>
    private static void AskHeadless(Settings settings)
    {
        var configPaths = GetHeadlessConfigPaths(settings).Where(File.Exists).ToList();
        if (configPaths.Count == 0) return;

        bool? current = ReadHeadlessSetting(configPaths);
        var hint = current == false ? "[y/N]" : "[Y/n]";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"\nRun ATF headless (no visible browser)? {hint}: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        bool headless = input switch
        {
            "y" or "yes" => true,
            "n" or "no" => false,
            _ => current ?? true, // Enter keeps the current setting
        };

        foreach (var path in configPaths)
            PatchHeadlessSetting(path, headless);

        Console.WriteLine($"  → Headless: {(headless ? "yes" : "no (browser visible)")}");
    }

    private static readonly Regex HeadlessSettingRegex = new(
        @"(<add\s+key=""Headless""\s+value="")[^""]*(""\s*/>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> GetHeadlessConfigPaths(Settings settings)
    {
        var paths = new List<string> { settings.TestDllPath + ".config" };
        if (GetAppConfigPath(settings) is string appConfig)
            paths.Add(appConfig);
        return paths;
    }

    private static bool? ReadHeadlessSetting(List<string> configPaths)
    {
        foreach (var path in configPaths)
        {
            try
            {
                var match = HeadlessSettingRegex.Match(File.ReadAllText(path));
                if (match.Success)
                {
                    var raw = match.Value;
                    var value = raw[(raw.IndexOf("value=\"", StringComparison.OrdinalIgnoreCase) + 7)..];
                    return value.StartsWith("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static void PatchHeadlessSetting(string configPath, bool headless)
    {
        try
        {
            var original = File.ReadAllText(configPath);
            var patched = HeadlessSettingRegex.Replace(original, $"${{1}}{(headless ? "True" : "False")}${{2}}");
            if (patched != original)
                File.WriteAllText(configPath, patched);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: could not set Headless in {configPath}: {ex.Message}");
        }
    }

    // ─── Database Reset ───────────────────────────────────────────────────────

    /// <summary>True when the DB setup job (e.g. S000_BasisOpzetten) is part of the job list.</summary>
    private static bool ContainsSetupJob(Settings settings, List<ATFRerunTool.Models.TestJob> jobs) =>
        !string.IsNullOrEmpty(settings.DatabaseReset.SetupCategory) &&
        jobs.Any(j => string.Equals(j.CategoryS, settings.DatabaseReset.SetupCategory, StringComparison.OrdinalIgnoreCase));

    private static bool AskDatabaseReset(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.DatabaseReset.SetupCategory))
            return false;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Was the database reset? [y/N]: ");
        Console.ResetColor();

        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes";
    }

    private static async Task RunDatabaseSetupAsync(Settings settings, CancellationToken cancellationToken)
    {
        var setupCategory = settings.DatabaseReset.SetupCategory;
        if (string.IsNullOrEmpty(setupCategory)) return;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Running database setup: {setupCategory}...");
        Console.ResetColor();

        var setupJob = new ATFRerunTool.Models.TestJob
        {
            TestId = setupCategory.StartsWith("S", StringComparison.OrdinalIgnoreCase)
                ? setupCategory[1..] : setupCategory,
            CategoryS = setupCategory,
            HasRTest = false,
            Source = "DB reset setup",
        };

        var runner = new LocalTestRunner(settings);
        var attempt = await runner.RunAsync(setupJob, ATFRerunTool.Models.TestVariant.State, 1, cancellationToken);

        if (attempt.Passed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ {setupCategory} passed — proceeding with tests.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: {setupCategory} did not pass. Proceeding anyway.");
        }
        Console.ResetColor();
        Console.WriteLine();
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

    // ─── Start-mode Picker ────────────────────────────────────────────────────

    private enum StartMode { Exit, FromLogs, RunAll }

    private static StartMode PickStartMode()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Select mode:");
        Console.ResetColor();
        Console.WriteLine("  [1]  Rerun failed tests from Jenkins log files (S and/or R)");
        Console.WriteLine("  [2]  Run all tests (no log files needed — discovers from DLL)");
        Console.WriteLine("  [0]  Exit");
        Console.WriteLine();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("Enter choice: ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();
            switch (input)
            {
                case "0": return StartMode.Exit;
                case "1": return StartMode.FromLogs;
                case "2": return StartMode.RunAll;
                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  Invalid choice. Enter 0, 1 or 2.");
                    Console.ResetColor();
                    break;
            }
        }
    }

    // ─── Log-source Picker ────────────────────────────────────────────────────

    private enum LogSource { Exit, LocalFiles, Jenkins }

    private static LogSource PickLogSource()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Where do the S and R runs come from?");
        Console.ResetColor();
        Console.WriteLine("  [1]  Local log files (ATFRun folder)");
        Console.WriteLine("  [2]  Jenkins (qsp-ci.topicusfinance.nl — pick a run, log is downloaded)");
        Console.WriteLine("  [0]  Exit");
        Console.WriteLine();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("Enter choice: ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();
            switch (input)
            {
                case "0": return LogSource.Exit;
                case "1": return LogSource.LocalFiles;
                case "2": return LogSource.Jenkins;
                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  Invalid choice. Enter 0, 1 or 2.");
                    Console.ResetColor();
                    break;
            }
        }
    }

    // ─── Jenkins Run Picker ───────────────────────────────────────────────────

    /// <summary>
    /// Lets the user pick the S and (optionally) R runs directly from Jenkins,
    /// downloads their timestamped console logs into the ATFRun folder, and
    /// returns the local paths. Returns (null, null) when the user exits.
    /// </summary>
    private static async Task<(string? SLogPath, string? RLogPath)> PickJenkinsRunsAsync(Settings settings)
    {
        var source = settings.JenkinsLogSource;
        await using var client = new JenkinsWebClient(source.StateJobUrl);

        Console.WriteLine();
        await client.EnsureLoggedInAsync(source.StateJobUrl);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Step 1/2 — Select the STATE (S) run from Jenkins:");
        Console.ResetColor();
        var sBuild = await PickJenkinsBuildAsync(client, source.StateJobUrl, source.BuildsToShow, allowSkip: false);
        if (sBuild is null) return (null, null);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Step 2/2 — Select the REGRESSION (R) run from Jenkins (or 0 to skip):");
        Console.ResetColor();
        var rBuild = await PickJenkinsBuildAsync(client, source.RegressionJobUrl, source.BuildsToShow, allowSkip: true);

        var atfRunFolder = FindAtfRunFolders(settings).First();
        Directory.CreateDirectory(atfRunFolder);

        var sLogPath = await DownloadJenkinsLogAsync(client, source.StateJobUrl, sBuild, "S", atfRunFolder);

        string? rLogPath = null;
        if (rBuild is not null)
            rLogPath = await DownloadJenkinsLogAsync(client, source.RegressionJobUrl, rBuild, "R", atfRunFolder);

        return (sLogPath, rLogPath);
    }

    private static async Task<string> DownloadJenkinsLogAsync(
        JenkinsWebClient client,
        string jobUrl,
        JenkinsBuildInfo build,
        string prefix,
        string atfRunFolder)
    {
        var path = Path.Combine(atfRunFolder, $"{prefix}{build.Number}.txt");
        Console.WriteLine($"  Downloading {prefix} run #{build.Number} console log...");
        var log = await client.DownloadTimestampedLogAsync(jobUrl, build.Number);
        File.WriteAllText(path, log);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ Saved to {path} ({log.Length / 1024} KB)");
        Console.ResetColor();
        return path;
    }

    private static async Task<JenkinsBuildInfo?> PickJenkinsBuildAsync(
        JenkinsWebClient client,
        string jobUrl,
        int buildsToShow,
        bool allowSkip)
    {
        Console.WriteLine("  Fetching runs from Jenkins...");
        var builds = await client.GetRecentBuildsAsync(jobUrl, buildsToShow);

        if (builds.Count == 0)
        {
            Console.WriteLine("  No builds found for this job.");
            return null;
        }

        Console.WriteLine();
        for (int i = 0; i < builds.Count; i++)
        {
            var b = builds[i];
            Console.Write($"  [{i + 1,3}]  #{b.Number,-6} {b.StartedAt:dd-MM-yyyy HH:mm}  ");

            var (label, color) = b switch
            {
                { Building: true } => ("RUNNING ", ConsoleColor.Cyan),
                { Result: "SUCCESS" } => ("SUCCESS ", ConsoleColor.Green),
                { Result: "UNSTABLE" } => ("UNSTABLE", ConsoleColor.Yellow),
                { Result: "ABORTED" } => ("ABORTED ", ConsoleColor.DarkGray),
                { Result: not null } => (b.Result!.PadRight(8), ConsoleColor.Red),
                _ => ("?       ", ConsoleColor.DarkGray),
            };
            Console.ForegroundColor = color;
            Console.Write(label);
            Console.ResetColor();

            Console.WriteLine($"  {b.StartedBy}");
        }

        var skipLabel = allowSkip ? "None / Skip" : "Exit";
        Console.WriteLine($"\n  [  0]  {skipLabel}");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Enter number: ");
            var input = Console.ReadLine()?.Trim();
            if (input == "0" || string.IsNullOrEmpty(input)) return null;

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= builds.Count)
            {
                var chosen = builds[choice - 1];
                if (chosen.Building)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"  Run #{chosen.Number} is still running — its log will be incomplete. Use it anyway? [y/N]: ");
                    Console.ResetColor();
                    var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (confirm is not ("y" or "yes")) continue;
                }
                Console.WriteLine();
                return chosen;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Invalid choice. Enter a number between 0 and {builds.Count}.");
            Console.ResetColor();
        }
    }

    // ─── Run All ──────────────────────────────────────────────────────────────

    private static async Task<int> RunAllTestsAsync(Settings settings)
    {
        const string runName = "RunAll";
        settings.ResultsOutputDirectory = Path.Combine(settings.ResultsOutputDirectory, runName);

        var atfRunFolders = FindAtfRunFolders(settings);
        var atfRunFolder = atfRunFolders.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        var knownBroken = KnownBrokenList.Load(atfRunFolder);

        if (knownBroken.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\nKnown-to-be-broken (will be skipped):");
            foreach (var id in knownBroken.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  - {id}");
            Console.ResetColor();
        }

        // Prerequisites needed for test discovery
        if (!File.Exists(settings.NUnitConsolePath))
        {
            Console.Error.WriteLine($"Error: nunit3-console.exe not found at: {settings.NUnitConsolePath}");
            Console.Error.WriteLine("Check GitRepoPath / NUnitConsoleRelativePath in appsettings.json.");
            return 1;
        }
        if (!File.Exists(settings.TestDllPath))
        {
            Console.Error.WriteLine($"Error: Test DLL not found at: {settings.TestDllPath}");
            Console.Error.WriteLine("Build the ATF solution first, or check GitRepoPath in appsettings.json.");
            return 1;
        }

        var previousState = LoadPreviousState(settings.ResultsOutputDirectory);
        List<ATFRerunTool.Models.TestJob> jobs;

        if (previousState is not null && previousState.StillFailingTestIds.Count > 0)
        {
            var relevantFailing = previousState.StillFailingTestIds
                .Where(id => !knownBroken.Contains(id))
                .ToList();

            if (relevantFailing.Count == 0)
            {
                Console.WriteLine("\nAll previously-failing jobs are in the known-to-be-broken list. Nothing to rerun.");
                jobs = [];
            }
            else
            {
                var skippedCount = previousState.StillFailingTestIds.Count - relevantFailing.Count;
                Console.WriteLine($"\nPrevious run-all found ({previousState.CompletedAt:dd-MM-yyyy HH:mm}).");
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
                    var allJobs = await DiscoverAllJobsAsync(settings, knownBroken);
                    var failingIds = new HashSet<string>(relevantFailing, StringComparer.OrdinalIgnoreCase);
                    jobs = allJobs.Where(j => failingIds.Contains(j.TestId)).ToList();

                    // If a previously-failing id is no longer in the DLL (edge case), add a minimal entry
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
                    jobs = await DiscoverAllJobsAsync(settings, knownBroken);
                }
            }
        }
        else
        {
            jobs = await DiscoverAllJobsAsync(settings, knownBroken);
        }

        // The setup job is normally part of the discovered list and runs alone
        // first in the orchestrator — only ask about a DB reset when it is not.
        bool setupInJobs = ContainsSetupJob(settings, jobs);
        if (setupInJobs)
            Console.WriteLine($"\n{settings.DatabaseReset.SetupCategory} is in the job list — it will run alone first; the rest waits for it.");
        bool dbWasReset = !setupInJobs && AskDatabaseReset(settings);

        if (jobs.Count == 0)
        {
            Console.WriteLine("No jobs to run. All clear!");
            return 0;
        }

        Console.WriteLine($"\nJobs to run ({jobs.Count}):");
        foreach (var job in jobs)
            Console.WriteLine($"  S: {job.CategoryS,-40}  R: {(job.HasRTest ? job.CategoryR : "(none)")}");

        Console.WriteLine($"\nResults folder: {settings.ResultsOutputDirectory}");

        AskBranch(settings);
        ApplyEnvironmentConfig(settings);
        AskHeadless(settings);
        settings.MaxRerunCount = AskRetries(settings.MaxRerunCount);
        settings.MaxParallelism = AskParallelism(settings.MaxParallelism);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancellation requested – finishing current test and saving report...");
            cts.Cancel();
        };

        if (dbWasReset && !settings.Jenkins.Enabled)
            await RunDatabaseSetupAsync(settings, cts.Token);

        var orchestrator = new RerunOrchestrator(settings);
        var session = await orchestrator.RunAsync(runName, jobs, cts.Token);

        SavePreviousState(settings.ResultsOutputDirectory, session);

        var reportPath = HtmlReportGenerator.Save(session, settings.ResultsOutputDirectory);
        Console.WriteLine($"\nReport saved to: {reportPath}");

        try { Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); }
        catch { /* non-critical */ }

        bool hasFailures = session.Jobs.Any(j => session.GetVerdict(j) == ATFRerunTool.Models.JobVerdict.Fail);
        return hasFailures ? 2 : 0;
    }

    private static async Task<List<ATFRerunTool.Models.TestJob>> DiscoverAllJobsAsync(
        Settings settings,
        HashSet<string> knownBroken)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nDiscovering all tests from DLL...");
        Console.ResetColor();

        var tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".xml");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = settings.NUnitConsolePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(settings.TestDllPath);
            psi.ArgumentList.Add($"--explore={tempFile}");

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync();

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                Console.Error.WriteLine("Warning: test discovery produced no output.");
                return [];
            }

            var jobs = ParseExploreXml(tempFile, knownBroken);
            Console.WriteLine($"  Found {jobs.Count} test(s) ({jobs.Count(j => j.HasRTest)} with R counterpart).");
            return jobs;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* non-critical */ }
        }
    }

    private static List<ATFRerunTool.Models.TestJob> ParseExploreXml(
        string xmlPath,
        HashSet<string> knownBroken)
    {
        var allCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var doc = XDocument.Load(xmlPath);

        foreach (var prop in doc.Descendants("property"))
        {
            if (!string.Equals((string?)prop.Attribute("name"), "Category",
                    StringComparison.OrdinalIgnoreCase)) continue;
            var value = (string?)prop.Attribute("value");
            if (!string.IsNullOrEmpty(value))
                allCategories.Add(value);
        }

        var sCategories = allCategories.Where(IsStateCategory).ToList();
        var rCategories = allCategories.Where(IsRegressionCategory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var jobs = new List<ATFRerunTool.Models.TestJob>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sCat in sCategories)
        {
            var testId = LocalStripVariantPrefix(sCat);
            if (!seenIds.Add(testId)) continue;
            if (knownBroken.Contains(testId))
            {
                Console.WriteLine($"  Skipping {testId} (known to be broken).");
                continue;
            }

            bool useUnderscore = sCat.StartsWith("S_", StringComparison.OrdinalIgnoreCase);
            var rCat = useUnderscore ? "R_" + testId : "R" + testId;

            jobs.Add(new ATFRerunTool.Models.TestJob
            {
                TestId = testId,
                CategoryS = sCat,
                CategoryR = rCat,
                HasRTest = rCategories.Contains(rCat),
                Source = "run all (discovered)",
            });
        }

        jobs.Sort((a, b) => string.Compare(a.TestId, b.TestId, StringComparison.OrdinalIgnoreCase));
        return jobs;
    }

    private static bool IsStateCategory(string category) =>
        (category.Length > 1 && char.ToUpperInvariant(category[0]) == 'S' && category[1] != '_')
        || category.StartsWith("S_", StringComparison.OrdinalIgnoreCase);

    private static bool IsRegressionCategory(string category) =>
        (category.Length > 1 && char.ToUpperInvariant(category[0]) == 'R' && category[1] != '_')
        || category.StartsWith("R_", StringComparison.OrdinalIgnoreCase);

    private static string LocalStripVariantPrefix(string category)
    {
        if (category.Length > 1 &&
            (char.ToUpperInvariant(category[0]) == 'S' || char.ToUpperInvariant(category[0]) == 'R') &&
            category[1] != '_')
            return category[1..];
        if (category.StartsWith("S_", StringComparison.OrdinalIgnoreCase) ||
            category.StartsWith("R_", StringComparison.OrdinalIgnoreCase))
            return category[2..];
        return category;
    }
}
