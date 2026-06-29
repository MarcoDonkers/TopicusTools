using ATFRerunTool.Configuration;
using ATFRerunTool.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ATFRerunTool.Running;

/// <summary>
/// Runs a single NUnit test category locally via nunit3-console.exe and parses the XML result.
/// </summary>
public sealed class LocalTestRunner
{
    private readonly Settings _settings;
    private static readonly object _configPatchLock = new();

    public LocalTestRunner(Settings settings) => _settings = settings;

    public async Task<TestRunAttempt> RunAsync(
        TestJob job,
        TestVariant variant,
        int round,
        CancellationToken cancellationToken = default)
    {
        var category = variant == TestVariant.State ? job.CategoryS : job.CategoryR;
        var attempt = new TestRunAttempt
        {
            TestId = job.TestId,
            Category = category,
            Variant = variant,
            Round = round,
            StartedAt = DateTime.Now,
        };

        Directory.CreateDirectory(_settings.ResultsOutputDirectory);
        var resultFile = Path.Combine(
            _settings.ResultsOutputDirectory,
            $"result_{job.TestId}_{(variant == TestVariant.State ? "S" : "R")}_round{round}_{DateTime.Now:HHmmss}.xml");

        attempt.ResultXmlPath = resultFile;

        Console.WriteLine($"  [{job.TestId}] Running {category} (round {round})...");

        // Patch the test DLL config to speed up status-change waits, then restore after
        string? configBackup = null;
        string? configPath = null;
        if (_settings.OverrideMaxWaitTimeStatusChangeMs > 0)
            (configPath, configBackup) = PatchDllConfig(_settings.OverrideMaxWaitTimeStatusChangeMs);

        try
        {
            var exitCode = await RunNUnitAsync(job.TestId, category, resultFile, cancellationToken);
            attempt.FinishedAt = DateTime.Now;

            if (File.Exists(resultFile))
            {
                attempt.Failures = ParseFailures(resultFile);
                attempt.Status = attempt.Failures.Count == 0 && exitCode == 0
                    ? RunStatus.Passed
                    : RunStatus.Failed;
            }
            else
            {
                attempt.Status = exitCode == 0 ? RunStatus.Passed : RunStatus.Error;
                Console.WriteLine($"  [{job.TestId}] WARNING: Result XML not found at {resultFile}");
            }
        }
        catch (OperationCanceledException)
        {
            attempt.FinishedAt = DateTime.Now;
            attempt.Status = RunStatus.Error;
            attempt.Failures.Add(new TestCaseFailure
            {
                TestName = category,
                Message = "Run was cancelled.",
            });
        }
        catch (Exception ex)
        {
            attempt.FinishedAt = DateTime.Now;
            attempt.Status = RunStatus.Error;
            attempt.Failures.Add(new TestCaseFailure
            {
                TestName = category,
                Message = $"Runner exception: {ex.Message}",
                StackTrace = ex.StackTrace ?? "",
            });
        }
        finally
        {
            RestoreDllConfig(configPath, configBackup);
        }

        var icon = attempt.Passed ? "✓" : "✗";
        lock (Console.Out)
            Console.WriteLine($"  [{job.TestId}] {icon} {category} → {attempt.Status} ({attempt.Duration:mm\\:ss})");
        return attempt;
    }

    /// <summary>
    /// Patches MaxWaitTimeStatusChangeMiliseconds in the test DLL's .config file.
    /// Uses a lock so parallel jobs don't race on the same file.
    /// Returns the config path and the original XML so it can be restored.
    /// </summary>
    private (string? path, string? original) PatchDllConfig(int newValueMs)
    {
        try
        {
            var configPath = _settings.TestDllPath + ".config";
            if (!File.Exists(configPath)) return (null, null);

            lock (_configPatchLock)
            {
                var original = File.ReadAllText(configPath);
                // Replace the MaxWaitTimeStatusChangeMiliseconds value
                var patched = Regex.Replace(
                    original,
                    @"(<add\s+key=""MaxWaitTimeStatusChangeMiliseconds""\s+value="")[^""]*(""\s*/>)",
                    $"${{1}}{newValueMs}${{2}}",
                    RegexOptions.IgnoreCase);

                if (patched != original)
                {
                    File.WriteAllText(configPath, patched);
                    lock (Console.Out)
                        Console.WriteLine($"  [config] MaxWaitTimeStatusChangeMiliseconds → {newValueMs}ms");
                }
                return (configPath, original);
            }
        }
        catch (Exception ex)
        {
            lock (Console.Out)
                Console.WriteLine($"  [config] Warning: could not patch DLL config: {ex.Message}");
            return (null, null);
        }
    }

    private void RestoreDllConfig(string? configPath, string? original)
    {
        if (configPath is null || original is null) return;
        try
        {
            lock (_configPatchLock)
                File.WriteAllText(configPath, original);
        }
        catch (Exception ex)
        {
            lock (Console.Out)
                Console.WriteLine($"  [config] Warning: could not restore DLL config: {ex.Message}");
        }
    }

    private async Task<int> RunNUnitAsync(
        string testId,
        string category,
        string resultFile,
        CancellationToken cancellationToken)
    {
        var args = string.Join(" ",
            $"\"{_settings.TestDllPath}\"",
            $"--where=\"cat == {category}\"",
            $"--result=\"{resultFile}\"",
            "--work=\"" + _settings.ResultsOutputDirectory + "\""
        );

        var psi = new ProcessStartInfo
        {
            FileName = _settings.NUnitConsolePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var prefix = $"  [{testId}] ";

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (Console.Out) Console.WriteLine(prefix + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (Console.Out) Console.WriteLine(prefix + "ERR: " + e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static readonly Regex ScreenshotLine =
        new(@"Screenshot:\s*(.+\.(?:png|jpg|jpeg|bmp|gif))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<TestCaseFailure> ParseFailures(string xmlPath)
    {
        var failures = new List<TestCaseFailure>();
        try
        {
            var doc = XDocument.Load(xmlPath);
            // Find all test-case elements with result="Failed" (case-insensitive)
            foreach (var testCase in doc.Descendants("test-case"))
            {
                var result = (string?)testCase.Attribute("result");
                if (!string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase)) continue;

                var failure = testCase.Element("failure");
                var message = failure?.Element("message")?.Value ?? "";
                var stackTrace = failure?.Element("stack-trace")?.Value ?? "";

                // Extract screenshot paths embedded in the message text
                var screenshots = ScreenshotLine.Matches(message)
                    .Select(m => m.Groups[1].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                failures.Add(new TestCaseFailure
                {
                    TestName = (string?)testCase.Attribute("fullname") ?? (string?)testCase.Attribute("name") ?? "unknown",
                    Message = message,
                    StackTrace = stackTrace,
                    ScreenshotPaths = screenshots,
                });
            }
        }
        catch (Exception ex)
        {
            failures.Add(new TestCaseFailure
            {
                TestName = "XML parse error",
                Message = ex.Message,
            });
        }
        return failures;
    }
}
