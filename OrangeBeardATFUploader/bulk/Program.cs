using System.Data;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

// Fall back to parent directory config when run from bulk/
var configPath = File.Exists("appsettings.json") ? "appsettings.json" : "../appsettings.json";
var appConfig = AppConfiguration.Load(configPath);
appConfig.ApplyEnvironmentOverrides();
ValidateConfiguration(appConfig);

var maxResults = GetIntArgument(args, "--max-results") ?? int.MaxValue;
var batchSize = GetIntArgument(args, "--batch-size") ?? 1000;

var stateStore = new SyncStateStore(appConfig.StateDatabasePath);
await stateStore.InitializeAsync();

var sentIds = await stateStore.GetSentSourceIdsAsync();
var sourceRows = await ReadSourceRowsAsync(appConfig.Source.ConnectionString);
var pendingRows = sourceRows.Where(row => !sentIds.Contains(row.Id)).Take(maxResults).ToList();

Console.WriteLine($"Read {sourceRows.Count} source rows from [dbo].[TestResults].");
Console.WriteLine($"{pendingRows.Count} rows still need to be sent to Orangebeard.");

if (pendingRows.Count == 0)
    return 0;

var batches = pendingRows
    .Select((row, i) => (row, i))
    .GroupBy(x => x.i / batchSize)
    .Select(g => g.Select(x => x.row).ToList())
    .ToList();

Console.WriteLine($"Uploading in {batches.Count} batch(es) of up to {batchSize} rows each...");
using var client = new OrangebeardHttpClient(appConfig.Orangebeard);

var totalSent = 0;
for (var b = 0; b < batches.Count; b++)
{
    var batch = batches[b];
    Console.WriteLine($"Batch {b + 1}/{batches.Count}: generating XML for {batch.Count} rows...");
    var xml = BuildJUnitXml(batch);

    Console.WriteLine($"Batch {b + 1}/{batches.Count}: uploading to {appConfig.Orangebeard.Endpoint}...");
    await client.ImportJUnitXmlAsync(xml, appConfig.Orangebeard.TestSet, appConfig.Orangebeard.Project);

    await stateStore.MarkSentBatchAsync(batch.Select(r => r.Id));
    totalSent += batch.Count;
    Console.WriteLine($"Batch {b + 1}/{batches.Count}: done. ({totalSent}/{pendingRows.Count} total sent)");
}

Console.WriteLine($"Completed. Sent {totalSent} results to Orangebeard in {batches.Count} batch(es).");
return 0;

static string SanitizeXml(string? input)
{
    if (string.IsNullOrEmpty(input)) return string.Empty;
    return new string(input.Where(c =>
        c == '\t' || c == '\n' || c == '\r' ||
        (c >= '\x20' && c <= '\xD7FF') ||
        (c >= '\xE000' && c <= '\xFFFD')).ToArray());
}

static string BuildJUnitXml(List<TestResultRow> rows)
{
    var failureCount = rows.Count(r => MapTestStatus(r.Status) == "FAILED");
    var skippedCount = rows.Count(r => MapTestStatus(r.Status) == "SKIPPED");
    var totalTime = rows.Sum(r => r.Duration ?? 0);

    var testsuite = new XElement("testsuite",
        new XAttribute("name", "ATF TestResults"),
        new XAttribute("tests", rows.Count),
        new XAttribute("failures", failureCount),
        new XAttribute("skipped", skippedCount),
        new XAttribute("time", totalTime.ToString("F3", CultureInfo.InvariantCulture)),
        rows.Select(row =>
        {
            var testcase = new XElement("testcase",
                new XAttribute("name", row.DisplayName),
                new XAttribute("classname", row.Category ?? row.JobName ?? "Unknown"),
                new XAttribute("time", (row.Duration ?? 0).ToString("F3", CultureInfo.InvariantCulture)));

            var status = MapTestStatus(row.Status);
            if (status == "FAILED")
            {
                var msg = SanitizeXml(row.ErrorMessage) is { Length: > 0 } s ? s : "Test failed";
                testcase.Add(new XElement("failure",
                    new XAttribute("message", msg),
                    new XAttribute("type", "FAILED"),
                    msg));
            }
            else if (status == "SKIPPED")
                testcase.Add(new XElement("skipped"));

            return testcase;
        }));

    var doc = new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("testsuites", testsuite));

    return doc.ToString();
}

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
        problems.Add("Source connection string is missing.");
    if (string.IsNullOrWhiteSpace(config.Orangebeard.Endpoint))
        problems.Add("Orangebeard endpoint is missing.");
    if (string.IsNullOrWhiteSpace(config.Orangebeard.Token))
        problems.Add("Orangebeard token is missing.");
    if (string.IsNullOrWhiteSpace(config.Orangebeard.Project))
        problems.Add("Orangebeard project is missing.");
    if (string.IsNullOrWhiteSpace(config.Orangebeard.TestSet))
        problems.Add("Orangebeard test set name is missing.");

    if (problems.Count > 0)
        throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
}

static void PrintHelp()
{
    Console.WriteLine("OrangebeardBulkApp uploads [dbo].[TestResults] to Orangebeard via JUnit XML import (single request).");
    Console.WriteLine();
    Console.WriteLine("Configure via appsettings.json (or ../appsettings.json) or environment variables:");
    Console.WriteLine("  SOURCE_CONNECTION_STRING");
    Console.WriteLine("  ORANGEBEARD_ENDPOINT");
    Console.WriteLine("  ORANGEBEARD_TOKEN");
    Console.WriteLine("  ORANGEBEARD_PROJECT");
    Console.WriteLine("  ORANGEBEARD_TESTSET");
    Console.WriteLine("  ORANGEBEARD_STATE_DB   (set to an absolute path to share state with the sequential tool)");
    Console.WriteLine();
    Console.WriteLine("Optional command-line flags:");
    Console.WriteLine("  --batch-size <n>    Rows per JUnit XML upload (default: 5000)");
    Console.WriteLine("  --max-results <n>   Limit rows processed in this run");
}

static int? GetIntArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            continue;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {name}.");
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
    private readonly string _importUrl;

    public OrangebeardHttpClient(OrangebeardConfiguration config)
    {
        _importUrl = $"{config.Endpoint.TrimEnd('/')}/listener/v3/test-tool/junit/import";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    public async Task ImportJUnitXmlAsync(string xml, string testSetName, string projectName, int maxRetries = 3)
    {
        var xmlBytes = Encoding.UTF8.GetBytes(xml);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(testSetName), "testSetName");
                form.Add(new StringContent(projectName), "projectName");
                var fileContent = new ByteArrayContent(xmlBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                form.Add(fileContent, "file", "results.xml");

                var response = await _http.PostAsync(_importUrl, form);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException(
                        $"Orangebeard HTTP {(int)response.StatusCode} {response.StatusCode} [{_importUrl}]\n{body}");
                }

                return;
            }
            catch (Exception ex) when (attempt < maxRetries && ex is TaskCanceledException or HttpRequestException)
            {
                var delay = TimeSpan.FromSeconds(5 * attempt);
                Console.WriteLine($"  Attempt {attempt} failed ({ex.GetType().Name}), retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay);
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record AppConfiguration
{
    public SourceConfiguration Source { get; init; } = new();
    public OrangebeardConfiguration Orangebeard { get; init; } = new();
    public string StateDatabasePath { get; set; } = "orangebeard-sync-state.db";

    public static AppConfiguration Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new AppConfiguration();
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<AppConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfiguration();
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
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task MarkSentBatchAsync(IEnumerable<int> sourceIds)
    {
        var sentAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText = """
            INSERT OR REPLACE INTO SentResults (SourceId, OrangebeardTestId, SentAtUtc)
            VALUES ($sourceId, 'junit-import', $sentAtUtc);
            """;
        var pSourceId = command.Parameters.Add("$sourceId", SqliteType.Integer);
        command.Parameters.AddWithValue("$sentAtUtc", sentAt);

        foreach (var id in sourceIds)
        {
            pSourceId.Value = id;
            await command.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private string GetConnectionString()
    {
        var fullPath = Path.GetFullPath(_databasePath, AppContext.BaseDirectory);
        return new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }
}
