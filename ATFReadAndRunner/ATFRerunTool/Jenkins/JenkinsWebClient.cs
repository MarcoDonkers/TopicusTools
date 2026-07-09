using Microsoft.Playwright;
using System.Text.Json;

namespace ATFRerunTool.Jenkins;

/// <summary>A single build of a Jenkins job, as shown in the run picker.</summary>
public sealed class JenkinsBuildInfo
{
    public int Number { get; init; }
    public DateTime StartedAt { get; init; }
    public bool Building { get; init; }
    /// <summary>SUCCESS / FAILURE / UNSTABLE / ABORTED, or null while building.</summary>
    public string? Result { get; init; }
    /// <summary>Who or what started the run, e.g. "Marco Donkers" or "Started by timer".</summary>
    public string StartedBy { get; init; } = "unknown";
}

/// <summary>
/// Browser-based Jenkins access using Playwright — same session strategy as the
/// Jira tooling in Automat: a persistent Chromium profile is stored in
/// jenkins-session/ next to the executable. On first run (or when the session
/// has expired) a visible browser window opens so the user can complete the
/// login/SSO/MFA flow; on later runs the saved profile is reused headlessly.
/// REST calls are made by navigating a tab directly to the API URL — the only
/// reliable way to carry HttpOnly SSO session cookies.
/// </summary>
public sealed class JenkinsWebClient : IAsyncDisposable
{
    private const int LoginPollIntervalMs = 5_000;
    private const int LoginTimeoutSeconds = 300;

    private readonly string _baseUrl;
    private readonly string _sessionDir;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    public JenkinsWebClient(string anyJenkinsUrl)
    {
        _baseUrl = new Uri(anyJenkinsUrl).GetLeftPart(UriPartial.Authority);
        var execDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        _sessionDir = Path.Combine(execDir, "jenkins-session");
    }

    // ─── Launch / shutdown ────────────────────────────────────────────────────

    private async Task LaunchAsync(bool headless)
    {
        Directory.CreateDirectory(_sessionDir);
        _playwright ??= await Playwright.CreateAsync();

        try
        {
            _context = await LaunchContextAsync(headless);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            Console.WriteLine("  Playwright Chromium not found — downloading it (one-time setup)...");
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
                throw new InvalidOperationException("Automatic Playwright browser install failed. Run: playwright install chromium");
            _context = await LaunchContextAsync(headless);
        }

        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
    }

    private Task<IBrowserContext> LaunchContextAsync(bool headless) =>
        _playwright!.Chromium.LaunchPersistentContextAsync(_sessionDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            Args = ["--disable-blink-features=AutomationControlled"],
        });

    private async Task ShutdownAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
            _context = null;
        }
        _page = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _playwright?.Dispose();
        _playwright = null;
    }

    // ─── Auth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirms the saved session can read Jenkins data. If not, re-launches with
    /// a visible window and waits for the user to complete the login flow.
    /// Authentication is detected by polling the job API — no URL pattern
    /// matching, so any SSO provider is supported.
    /// </summary>
    public async Task EnsureLoggedInAsync(string probeJobUrl, CancellationToken ct = default)
    {
        Console.WriteLine("Checking Jenkins session...");
        await LaunchAsync(headless: true);

        if (await IsAuthenticatedAsync(probeJobUrl))
        {
            Console.WriteLine("  Session valid.");
            return;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Not logged in to Jenkins.");
        Console.WriteLine("  A browser window will open — please log in (including any SSO/MFA steps).");
        Console.WriteLine($"  Waiting up to {LoginTimeoutSeconds / 60} minutes...");
        Console.ResetColor();

        await ShutdownAsync();
        await LaunchAsync(headless: false);
        await _page!.GotoAsync($"{_baseUrl}/login", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000,
        });

        var elapsedMs = 0;
        while (elapsedMs < LoginTimeoutSeconds * 1000)
        {
            await Task.Delay(LoginPollIntervalMs, ct);
            elapsedMs += LoginPollIntervalMs;
            if (await IsAuthenticatedAsync(probeJobUrl))
            {
                Console.WriteLine("  Login confirmed — session saved for future runs.");
                return;
            }
        }

        throw new TimeoutException($"Timed out waiting for Jenkins login after {LoginTimeoutSeconds} seconds.");
    }

    private async Task<bool> IsAuthenticatedAsync(string probeJobUrl)
    {
        try
        {
            var json = await PageFetchTextAsync(NormalizeJobUrl(probeJobUrl) + "api/json?tree=name");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out _);
        }
        catch
        {
            return false;
        }
    }

    // ─── Fetching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a new tab, navigates to the URL, returns the raw response body and
    /// closes the tab. Navigation (rather than fetch) carries the SSO cookies.
    /// </summary>
    private async Task<string> PageFetchTextAsync(string url)
    {
        var page = await _context!.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });

            if (response is null)
                throw new InvalidOperationException($"No response for {url}");

            if (response.Status >= 400)
            {
                var body = await page.InnerTextAsync("body");
                throw new InvalidOperationException(
                    $"Jenkins returned {response.Status} for {url}\n{body[..Math.Min(body.Length, 500)]}");
            }

            return await response.TextAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>Fetches the most recent builds of a job, newest first.</summary>
    public async Task<List<JenkinsBuildInfo>> GetRecentBuildsAsync(string jobUrl, int count)
    {
        var url = NormalizeJobUrl(jobUrl) +
                  $"api/json?tree=builds[number,timestamp,result,building,actions[causes[shortDescription,userName]]]{{0,{count}}}";

        var json = await PageFetchTextAsync(url);
        using var doc = JsonDocument.Parse(json);

        var builds = new List<JenkinsBuildInfo>();
        if (!doc.RootElement.TryGetProperty("builds", out var buildsEl))
            return builds;

        foreach (var b in buildsEl.EnumerateArray())
        {
            var number = b.GetProperty("number").GetInt32();
            var timestamp = b.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).LocalDateTime
                : DateTime.MinValue;
            var building = b.TryGetProperty("building", out var bg) && bg.ValueKind == JsonValueKind.True;
            var result = b.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            builds.Add(new JenkinsBuildInfo
            {
                Number = number,
                StartedAt = timestamp,
                Building = building,
                Result = result,
                StartedBy = ExtractStartedBy(b),
            });
        }

        builds.Sort((a, b2) => b2.Number.CompareTo(a.Number));
        return builds;
    }

    private static string ExtractStartedBy(JsonElement build)
    {
        if (!build.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return "unknown";

        foreach (var action in actions.EnumerateArray())
        {
            if (!action.TryGetProperty("causes", out var causes) || causes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var cause in causes.EnumerateArray())
            {
                if (cause.TryGetProperty("userName", out var userName) &&
                    userName.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(userName.GetString()))
                    return userName.GetString()!;

                if (cause.TryGetProperty("shortDescription", out var desc) &&
                    desc.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(desc.GetString()))
                    return desc.GetString()!;
            }
        }
        return "unknown";
    }

    /// <summary>
    /// Downloads the full console log of a build with HH:mm:ss timestamps
    /// prepended — the same format as the manually saved ATFRun logs.
    /// </summary>
    public Task<string> DownloadTimestampedLogAsync(string jobUrl, int buildNumber) =>
        PageFetchTextAsync(NormalizeJobUrl(jobUrl) +
                           $"{buildNumber}/timestamps/?time=HH:mm:ss&timeZone=GMT%2B2&appendLog&locale=en_US");

    private static string NormalizeJobUrl(string jobUrl) =>
        jobUrl.EndsWith('/') ? jobUrl : jobUrl + "/";
}
