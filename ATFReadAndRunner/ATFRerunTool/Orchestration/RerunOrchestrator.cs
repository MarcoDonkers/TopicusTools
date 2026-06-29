using ATFRerunTool.Configuration;
using ATFRerunTool.Models;
using ATFRerunTool.Running;
using System.Collections.Concurrent;

namespace ATFRerunTool.Orchestration;

/// <summary>
/// Orchestrates the full rerun loop:
///   Round 1..N: for each still-failing job, run only the components
///   (S and/or R) that have not yet passed.  S must pass before R is attempted.
///   Multiple jobs run concurrently up to Settings.MaxParallelism.
///   After each round, remove jobs where both S and R have passed.
///   Stop early when no jobs remain or max rounds is reached.
/// </summary>
public sealed class RerunOrchestrator
{
    private readonly Settings _settings;
    private readonly LocalTestRunner _localRunner;
    private readonly JenkinsTestRunner _jenkinsRunner;

    public RerunOrchestrator(Settings settings)
    {
        _settings = settings;
        _localRunner = new LocalTestRunner(settings);
        _jenkinsRunner = new JenkinsTestRunner(settings.Jenkins);
    }

    public async Task<RerunSession> RunAsync(
        string runName,
        List<TestJob> jobs,
        CancellationToken cancellationToken = default)
    {
        var session = new RerunSession
        {
            RunName = runName,
            StartedAt = DateTime.Now,
            MaxRounds = _settings.MaxRerunCount,
            Jobs = jobs,
        };

        if (jobs.Count == 0)
        {
            Console.WriteLine("No failed jobs found in the Jenkins log. Nothing to rerun.");
            session.FinishedAt = DateTime.Now;
            return session;
        }

        int parallelism = _settings.MaxParallelism > 0 ? _settings.MaxParallelism : Environment.ProcessorCount;

        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"Starting rerun session: {session.SessionId}");
        Console.WriteLine($"Jobs to rerun:  {jobs.Count}");
        Console.WriteLine($"Max rounds:     {_settings.MaxRerunCount}");
        Console.WriteLine($"Parallelism:    {parallelism}");
        Console.WriteLine($"Runner:         {(_settings.Jenkins.Enabled ? "Jenkins" : "Local NUnit")}");
        Console.WriteLine(new string('=', 80) + "\n");

        // Only track R permanently — once R goes green the job is done.
        // S always runs fresh each round because it rebuilds state that R depends on.
        var rPassed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Thread-safe collection for accumulating attempts across parallel workers
        var allAttempts = new ConcurrentBag<TestRunAttempt>();

        var queue = new List<TestJob>(jobs);

        for (int round = 1; round <= _settings.MaxRerunCount; round++)
        {
            if (queue.Count == 0) break;

            Console.WriteLine($"\n--- Round {round} / {_settings.MaxRerunCount} ({queue.Count} job(s), parallelism={parallelism}) ---\n");

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForEachAsync(queue, parallelOptions, async (job, ct) =>
            {
                bool rAlreadyGreen = !job.HasRTest || rPassed.ContainsKey(job.TestId);
                if (rAlreadyGreen)
                {
                    // Should not be in queue, but guard anyway
                    WriteColored($"  [{job.TestId}] Skipping — already fully green.", ConsoleColor.DarkGray);
                    return;
                }

                // ── State test — always runs each round ────────────────────
                var sAttempt = await ExecuteAsync(job, TestVariant.State, round, ct);
                allAttempts.Add(sAttempt);

                // ── Regression test — only if S passed this round ──────────
                if (sAttempt.Passed && job.HasRTest)
                {
                    var rAttempt = await ExecuteAsync(job, TestVariant.Regression, round, ct);
                    allAttempts.Add(rAttempt);
                    if (rAttempt.Passed) rPassed.TryAdd(job.TestId, true);
                }
                else if (!sAttempt.Passed && job.HasRTest)
                {
                    WriteColored($"  [{job.TestId}] Skipping {job.CategoryR} — S did not pass this round.", ConsoleColor.DarkGray);
                }
                else if (sAttempt.Passed && !job.HasRTest)
                {
                    rPassed.TryAdd(job.TestId, true); // S-only job, counts as done
                }
            });

            // Flush bag to session list
            while (allAttempts.TryTake(out var a)) session.AllAttempts.Add(a);

            // Remove jobs where R (or S-only) has gone green
            queue.RemoveAll(j => rPassed.ContainsKey(j.TestId));

            int nowGreen = jobs.Count - queue.Count;
            Console.WriteLine($"\nRound {round} complete.  Green: {nowGreen}/{jobs.Count}.  Still failing: {queue.Count}.");
        }

        session.FinishedAt = DateTime.Now;
        Console.WriteLine($"\nAll rounds complete. Total duration: {session.FinishedAt - session.StartedAt:hh\\:mm\\:ss}");
        return session;
    }

    private async Task<TestRunAttempt> ExecuteAsync(
        TestJob job,
        TestVariant variant,
        int round,
        CancellationToken ct)
    {
        if (_settings.Jenkins.Enabled)
            return await _jenkinsRunner.RunAsync(job, variant, round, ct);

        return await _localRunner.RunAsync(job, variant, round, ct);
    }

    private static void WriteColored(string message, ConsoleColor color)
    {
        lock (Console.Out)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}
