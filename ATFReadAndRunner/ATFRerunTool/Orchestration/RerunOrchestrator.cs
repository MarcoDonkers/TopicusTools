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

        ConsoleProgress.Start(jobs.Count);

        ConsoleProgress.WriteLine(new string('=', 80));
        ConsoleProgress.WriteLine($"Starting rerun session: {session.SessionId}");
        ConsoleProgress.WriteLine($"Jobs to rerun:  {jobs.Count}");
        ConsoleProgress.WriteLine($"Max rounds:     {_settings.MaxRerunCount}");
        ConsoleProgress.WriteLine($"Parallelism:    {parallelism}");
        ConsoleProgress.WriteLine($"Runner:         {(_settings.Jenkins.Enabled ? "Jenkins" : "Local NUnit")}");
        if (_settings.SkipStateOnFirstRound)
            ConsoleProgress.WriteLine("Round 1:        S skipped for jobs with an R counterpart (assumes state is valid)", ConsoleColor.Yellow);
        ConsoleProgress.WriteLine(new string('=', 80) + "\n");

        // Only track R permanently — once R goes green the job is done.
        // S always runs fresh each round because it rebuilds state that R depends on
        // (unless SkipStateOnFirstRound short-circuits round 1 for jobs that have an R test).
        var rPassed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Thread-safe collection for accumulating attempts across parallel workers
        var allAttempts = new ConcurrentBag<TestRunAttempt>();

        var queue = new List<TestJob>(jobs);

        for (int round = 1; round <= _settings.MaxRerunCount; round++)
        {
            if (queue.Count == 0) break;

            // Jobs still in the queue are being re-attempted this round — their previous
            // "failed" count no longer applies until this round's outcome is known.
            ConsoleProgress.ResetRoundFailures();

            ConsoleProgress.WriteLine($"\n--- Round {round} / {_settings.MaxRerunCount} ({queue.Count} job(s), parallelism={parallelism}) ---\n");

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            };

            // The DB setup job (e.g. S000_BasisOpzetten) must run alone first —
            // every other test depends on the state it builds.
            var setupJob = string.IsNullOrEmpty(_settings.DatabaseReset.SetupCategory) ? null
                : queue.FirstOrDefault(j =>
                    string.Equals(j.CategoryS, _settings.DatabaseReset.SetupCategory, StringComparison.OrdinalIgnoreCase));

            var parallelQueue = queue;
            if (setupJob is not null)
            {
                if (queue.Count > 1)
                    ConsoleProgress.WriteLine($"  {setupJob.CategoryS} runs alone first — {queue.Count - 1} other job(s) wait for it.\n", ConsoleColor.Yellow);
                await RunJobAsync(setupJob, round, rPassed, allAttempts, cancellationToken);
                parallelQueue = queue.Where(j => !ReferenceEquals(j, setupJob)).ToList();
            }

            await Parallel.ForEachAsync(parallelQueue, parallelOptions,
                async (job, ct) => await RunJobAsync(job, round, rPassed, allAttempts, ct));

            // Flush bag to session list
            while (allAttempts.TryTake(out var a)) session.AllAttempts.Add(a);

            // Remove jobs where R (or S-only) has gone green
            queue.RemoveAll(j => rPassed.ContainsKey(j.TestId));

            int nowGreen = jobs.Count - queue.Count;
            ConsoleProgress.WriteLine($"\nRound {round} complete.  Green: {nowGreen}/{jobs.Count}.  Still failing: {queue.Count}.");
        }

        session.FinishedAt = DateTime.Now;
        ConsoleProgress.WriteLine($"\nAll rounds complete. Total duration: {session.FinishedAt - session.StartedAt:hh\\:mm\\:ss}");
        ConsoleProgress.Stop();
        return session;
    }

    private async Task RunJobAsync(
        TestJob job,
        int round,
        ConcurrentDictionary<string, bool> rPassed,
        ConcurrentBag<TestRunAttempt> allAttempts,
        CancellationToken ct)
    {
        bool alreadyGreen = rPassed.ContainsKey(job.TestId);
        if (alreadyGreen)
        {
            // Should not be in queue, but guard anyway
            ConsoleProgress.WriteLine($"  [{job.TestId}] Skipping — already fully green.", ConsoleColor.DarkGray);
            return;
        }

        // ── State test — always runs each round, unless SkipStateOnFirstRound
        // short-circuits round 1 for a job that has an R counterpart to fall back on ──
        bool skipState = _settings.SkipStateOnFirstRound && round == 1 && job.HasRTest;

        TestRunAttempt? sAttempt = null;
        if (skipState)
        {
            ConsoleProgress.WriteLine($"  [{job.TestId}] Skipping {job.CategoryS} (round 1) — assuming state is already valid.", ConsoleColor.DarkGray);
        }
        else
        {
            sAttempt = await ExecuteAsync(job, TestVariant.State, round, ct);
            allAttempts.Add(sAttempt);
        }

        bool sPassed = skipState || (sAttempt?.Passed ?? false);

        // ── Regression test — only if S passed (or was skipped) this round ──
        if (sPassed && job.HasRTest)
        {
            var rAttempt = await ExecuteAsync(job, TestVariant.Regression, round, ct);
            allAttempts.Add(rAttempt);
            if (rAttempt.Passed)
            {
                rPassed.TryAdd(job.TestId, true);
                ConsoleProgress.MarkPassed();
            }
            else
            {
                ConsoleProgress.MarkFailed();
            }
        }
        else if (!sPassed && job.HasRTest)
        {
            ConsoleProgress.WriteLine($"  [{job.TestId}] Skipping {job.CategoryR} — S did not pass this round.", ConsoleColor.DarkGray);
            ConsoleProgress.MarkFailed();
        }
        else if (sPassed && !job.HasRTest)
        {
            rPassed.TryAdd(job.TestId, true); // S-only job, counts as done
            ConsoleProgress.MarkPassed();
        }
        else // !sPassed && !job.HasRTest — S-only job whose S attempt failed
        {
            ConsoleProgress.MarkFailed();
        }
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
}
