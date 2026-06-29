using ATFRerunTool.Configuration;
using ATFRerunTool.Models;

namespace ATFRerunTool.Running;

/// <summary>
/// Triggers and polls Jenkins jobs for ATF reruns.
/// Currently DISABLED — set Jenkins.Enabled = true in appsettings.json to activate.
/// </summary>
public sealed class JenkinsTestRunner
{
    private readonly JenkinsSettings _settings;
    private readonly HttpClient _http;

    public JenkinsTestRunner(JenkinsSettings settings)
    {
        _settings = settings;
        _http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(settings.ApiUser) && !string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{settings.ApiUser}:{settings.ApiToken}"));
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>
    /// Triggers a Jenkins build for the given category and waits for it to complete.
    /// Returns a TestRunAttempt with the result.
    /// </summary>
    public async Task<TestRunAttempt> RunAsync(
        TestJob job,
        TestVariant variant,
        int round,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("JenkinsTestRunner is disabled. Set Jenkins.Enabled=true in appsettings.json.");

        var category = variant == TestVariant.State ? job.CategoryS : job.CategoryR;
        var jobPattern = variant == TestVariant.State ? _settings.StateJobPattern : _settings.RegressionJobPattern;
        var jenkinsJobName = string.Format(jobPattern, category);
        var triggerUrl = $"{_settings.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jenkinsJobName)}/build";

        var attempt = new TestRunAttempt
        {
            TestId = job.TestId,
            Category = category,
            Variant = variant,
            Round = round,
            StartedAt = DateTime.Now,
        };

        try
        {
            Console.WriteLine($"  Triggering Jenkins: {jenkinsJobName}");

            // Trigger build
            var triggerResponse = await _http.PostAsync(triggerUrl, null, cancellationToken);
            triggerResponse.EnsureSuccessStatusCode();

            // Wait for queued build to start and finish
            var buildNumber = await WaitForBuildStartAsync(jenkinsJobName, cancellationToken);
            var passed = await WaitForBuildCompleteAsync(jenkinsJobName, buildNumber, cancellationToken);

            attempt.FinishedAt = DateTime.Now;
            attempt.Status = passed ? RunStatus.Passed : RunStatus.Failed;

            if (!passed)
            {
                attempt.Failures.Add(new TestCaseFailure
                {
                    TestName = jenkinsJobName,
                    Message = $"Jenkins build #{buildNumber} failed. Check: {_settings.BaseUrl}/job/{jenkinsJobName}/{buildNumber}/console",
                });
            }
        }
        catch (Exception ex)
        {
            attempt.FinishedAt = DateTime.Now;
            attempt.Status = RunStatus.Error;
            attempt.Failures.Add(new TestCaseFailure
            {
                TestName = jenkinsJobName,
                Message = ex.Message,
                StackTrace = ex.StackTrace ?? "",
            });
        }

        return attempt;
    }

    private async Task<int> WaitForBuildStartAsync(string jobName, CancellationToken ct)
    {
        var queueUrl = $"{_settings.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/api/json?tree=nextBuildNumber";
        var beforeBuild = await GetBuildNumberAsync(queueUrl, ct);

        // Poll until a new build appears
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(5000, ct);
            var current = await GetBuildNumberAsync(queueUrl, ct);
            if (current > beforeBuild) return current - 1;
        }
        throw new TimeoutException($"Jenkins build for {jobName} did not start within 5 minutes.");
    }

    private async Task<int> GetBuildNumberAsync(string url, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(url, ct);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("nextBuildNumber").GetInt32();
    }

    private async Task<bool> WaitForBuildCompleteAsync(string jobName, int buildNumber, CancellationToken ct)
    {
        var statusUrl = $"{_settings.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{buildNumber}/api/json?tree=building,result";

        for (int i = 0; i < 360; i++) // max ~30 minutes
        {
            await Task.Delay(5000, ct);
            var json = await _http.GetStringAsync(statusUrl, ct);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var building = doc.RootElement.GetProperty("building").GetBoolean();
            if (!building)
            {
                var result = doc.RootElement.GetProperty("result").GetString();
                return string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase);
            }
        }
        throw new TimeoutException($"Jenkins build {buildNumber} for {jobName} did not finish within 30 minutes.");
    }
}
