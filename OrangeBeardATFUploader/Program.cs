using System.Data;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var appConfig = AppConfiguration.Load("appsettings.json");
appConfig.ApplyEnvironmentOverrides();
ValidateConfiguration(appConfig);

var maxResults = GetIntArgument(args, "--max-results") ?? int.MaxValue;

var stateStore = new SyncStateStore(appConfig.StateDatabasePath);
await stateStore.InitializeAsync();

var sentIds = await stateStore.GetSentSourceIdsAsync();
var sourceRows = await ReadSourceRowsAsync(appConfig.Source.ConnectionString);
var pendingRows = sourceRows.Where(row => !sentIds.Contains(row.Id)).Take(maxResults).ToList();

Console.WriteLine($"Read {sourceRows.Count} source rows from [dbo].[TestResults].");
Console.WriteLine($"{pendingRows.Count} rows still need to be sent to Orangebeard.");

if (pendingRows.Count == 0)
{
    return 0;
}

Console.WriteLine($"Connecting to {appConfig.Orangebeard.Endpoint} / project '{appConfig.Orangebeard.Project}'...");

using var client = new OrangebeardHttpClient(appConfig.Orangebeard);

var testRunId = await client.StartTestRunAsync(appConfig.Orangebeard.TestSet, appConfig.Orangebeard.Description);
Console.WriteLine($"Test run started: {testRunId}");

var processedCount = 0;
try
{
    var suiteId = await client.StartSuiteAsync(testRunId, appConfig.Orangebeard.TestSet);
    Console.WriteLine($"Suite started: {suiteId}");

    foreach (var row in pendingRows)
    {
        Console.WriteLine($"Sending source result {row.Id} ({row.DisplayName})...");

        var testId = await client.StartTestAsync(testRunId, suiteId, row.DisplayName, row.BuildDescription(), row.ExecutedAtUtc ?? DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(row.ErrorMessage))
        {
            await client.LogAsync(testRunId, testId, row.ErrorMessage!, "ERROR", DateTime.UtcNow);
        }

        await client.FinishTestAsync(testRunId, testId, MapTestStatus(row.Status), DateTime.UtcNow);

        await stateStore.MarkSentAsync(row.Id, testId);
        processedCount++;
    }

    Console.WriteLine($"Completed. Sent {processedCount} new results to Orangebeard.");
}
finally
{
    await client.FinishTestRunAsync(testRunId);
}

return 0;

static string MapTestStatus(string? status) =>
    status?.Trim().ToLowerInvariant() switch
    {
        "success" or "passed" or "pass" => "PASSED",
        "failed" or "failure" or "error" => "FAILED",
        "skipped" or "ignored" => "SKIPPED",
        "stopped" => "STOPPED",
        "timedout" or "timed_out" or "timeout" => "TIMED_OUT",
        _ => "PASSED"
    };

static void ValidateConfiguration(AppConfiguration config)
{
    var problems = new List<string>();

    if (string.IsNullOrWhiteSpace(config.Source.ConnectionString))
    {
        problems.Add("Source connection string is missing.");
    }

    if (string.IsNullOrWhiteSpace(config.Orangebeard.Endpoint))
    {
        problems.Add("Orangebeard endpoint is missing.");
    }

    if (string.IsNullOrWhiteSpace(config.Orangebeard.Token) || !Guid.TryParse(config.Orangebeard.Token, out _))
    {
        problems.Add("Orangebeard token is missing or not a valid UUID.");
    }

    if (string.IsNullOrWhiteSpace(config.Orangebeard.Project))
    {
        problems.Add("Orangebeard project is missing.");
    }

    if (string.IsNullOrWhiteSpace(config.Orangebeard.TestSet))
    {
        problems.Add("Orangebeard test set name is missing.");
    }

    if (problems.Count > 0)
    {
        throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }
}

static void PrintHelp()
{
    Console.WriteLine("OrangebeardApp synchronizes [dbo].[TestResults] from SQL Server to Orangebeard.");
    Console.WriteLine();
    Console.WriteLine("Configure the app in appsettings.json or via environment variables:");
    Console.WriteLine("  Source__ConnectionString");
    Console.WriteLine("  Orangebeard__Endpoint");
    Console.WriteLine("  Orangebeard__Token");
    Console.WriteLine("  Orangebeard__Project");
    Console.WriteLine("  Orangebeard__TestSet");
    Console.WriteLine("  Orangebeard__Description");
    Console.WriteLine("  StateDatabasePath");
    Console.WriteLine();
    Console.WriteLine("Optional command-line flags:");
    Console.WriteLine("  --max-results <n>");
}

static int? GetIntArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}.");
        }

        return int.Parse(args[index + 1], CultureInfo.InvariantCulture);
    }

    return null;
}

static async Task<List<TestResultRow>> ReadSourceRowsAsync(string connectionString)
{
    var rows = new List<TestResultRow>();

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    const string query = """
        SELECT Id, Status, Duration, ErrorMessage, JobName, JobRun, Category, ExecutedAt, Branch, Label, Environment
        FROM [dbo].[TestResults]
        ORDER BY Id;
        """;

    await using var command = new SqlCommand(query, connection);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

    while (await reader.ReadAsync())
    {
        rows.Add(new TestResultRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)));
    }

    return rows;
}

internal sealed class OrangebeardHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public OrangebeardHttpClient(OrangebeardConfiguration config)
    {
        _base = $"{config.Endpoint.TrimEnd('/')}/listener/v3/{config.Project}";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<Guid> StartTestRunAsync(string testSetName, string? description)
    {
        var body = new { testSetName, description, startTime = DateTime.UtcNow.ToString("o") };
        return await PostAsync<Guid>($"{_base}/test-run/start", body);
    }

    public async Task<Guid> StartSuiteAsync(Guid testRunId, string suiteName)
    {
        var body = new { testRunUUID = testRunId, suiteNames = new[] { suiteName } };
        var suites = await PostAsync<List<SuiteResponse>>($"{_base}/suite/start", body);
        return suites.FirstOrDefault()?.suiteUUID
            ?? throw new InvalidOperationException("Orangebeard did not return a suite id.");
    }

    public async Task<Guid> StartTestAsync(Guid testRunId, Guid suiteId, string testName, string? description, DateTime startTime)
    {
        var body = new { testRunUUID = testRunId, suiteUUID = suiteId, testName, testType = "TEST", description, startTime = startTime.ToUniversalTime().ToString("o") };
        return await PostAsync<Guid>($"{_base}/test/start", body);
    }

    public async Task LogAsync(Guid testRunId, Guid testId, string message, string logLevel, DateTime logTime)
    {
        var body = new { testRunUUID = testRunId, testUUID = testId, message, logLevel, logTime = logTime.ToUniversalTime().ToString("o"), logFormat = "PLAIN_TEXT" };
        await PostAsync<Guid>($"{_base}/log", body);
    }

    public async Task FinishTestAsync(Guid testRunId, Guid testId, string status, DateTime endTime)
    {
        var body = new { testRunUUID = testRunId, status, endTime = endTime.ToUniversalTime().ToString("o") };
        await PutAsync($"{_base}/test/finish/{testId}", body);
    }

    public async Task FinishTestRunAsync(Guid testRunId)
    {
        await PutAsync($"{_base}/test-run/finish/{testRunId}", new { endTime = DateTime.UtcNow.ToString("o") });
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var response = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        await EnsureSuccessAsync(response, url);
        var responseBody = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private async Task PutAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var response = await _http.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        await EnsureSuccessAsync(response, url);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Orangebeard HTTP {(int)response.StatusCode} {response.StatusCode} [{url}]\n{body}");
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record SuiteResponse(Guid suiteUUID);
}

internal sealed record AppConfiguration
{
    public SourceConfiguration Source { get; init; } = new();
    public OrangebeardConfiguration Orangebeard { get; init; } = new();
    public string StateDatabasePath { get; set; } = "orangebeard-sync-state.db";

    public static AppConfiguration Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new AppConfiguration();
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions()) ?? new AppConfiguration();
    }

    public void ApplyEnvironmentOverrides()
    {
        Source.ConnectionString = GetEnvironmentValue("SOURCE_CONNECTION_STRING", Source.ConnectionString);
        Orangebeard.Endpoint = GetEnvironmentValue("ORANGEBEARD_ENDPOINT", Orangebeard.Endpoint);
        Orangebeard.Token = GetEnvironmentValue("ORANGEBEARD_TOKEN", Orangebeard.Token);
        Orangebeard.Project = GetEnvironmentValue("ORANGEBEARD_PROJECT", Orangebeard.Project);
        Orangebeard.TestSet = GetEnvironmentValue("ORANGEBEARD_TESTSET", Orangebeard.TestSet);
        Orangebeard.Description = GetEnvironmentValue("ORANGEBEARD_DESCRIPTION", Orangebeard.Description);
        StateDatabasePath = GetEnvironmentValue("ORANGEBEARD_STATE_DB", StateDatabasePath);
    }

    private static string GetEnvironmentValue(string name, string? currentValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? currentValue ?? string.Empty : value;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record SourceConfiguration
{
    public string ConnectionString { get; set; } = string.Empty;
}

internal sealed record OrangebeardConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string TestSet { get; set; } = string.Empty;
    public string? Description { get; set; }
}

internal sealed record TestResultRow(
    int Id,
    string? Status,
    double? Duration,
    string? ErrorMessage,
    string? JobName,
    string? JobRun,
    string? Category,
    DateTime? ExecutedAtUtc,
    string? Branch,
    string? Label,
    string? Environment)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(JobName)
        ? $"{JobName} #{Id}"
        : $"TestResult #{Id}";

    public string BuildDescription()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Status))
        {
            parts.Add($"Status: {Status}");
        }

        if (!string.IsNullOrWhiteSpace(JobRun))
        {
            parts.Add($"JobRun: {JobRun}");
        }

        if (!string.IsNullOrWhiteSpace(Category))
        {
            parts.Add($"Category: {Category}");
        }

        if (!string.IsNullOrWhiteSpace(Branch))
        {
            parts.Add($"Branch: {Branch}");
        }

        if (!string.IsNullOrWhiteSpace(Label))
        {
            parts.Add($"Label: {Label}");
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            parts.Add($"Environment: {Environment}");
        }

        if (Duration.HasValue)
        {
            parts.Add($"Duration: {Duration.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (ExecutedAtUtc.HasValue)
        {
            parts.Add($"ExecutedAt: {ExecutedAtUtc.Value:O}");
        }

        return string.Join(" | ", parts);
    }
}

internal sealed class SyncStateStore
{
    private readonly string _databasePath;

    public SyncStateStore(string databasePath)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? "orangebeard-sync-state.db"
            : databasePath;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SentResults (
                SourceId INTEGER PRIMARY KEY,
                OrangebeardTestId TEXT NOT NULL,
                SentAtUtc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<int>> GetSentSourceIdsAsync()
    {
        var ids = new HashSet<int>();

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId FROM SentResults;";
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    public async Task MarkSentAsync(int sourceId, Guid orangebeardTestId)
    {
        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO SentResults (SourceId, OrangebeardTestId, SentAtUtc)
            VALUES ($sourceId, $orangebeardTestId, $sentAtUtc);
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$orangebeardTestId", orangebeardTestId.ToString());
        command.Parameters.AddWithValue("$sentAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private string GetConnectionString()
    {
        var fullPath = Path.GetFullPath(_databasePath, AppContext.BaseDirectory);
        return new SqliteConnectionStringBuilder
        {
            DataSource = fullPath
        }.ToString();
    }
}

