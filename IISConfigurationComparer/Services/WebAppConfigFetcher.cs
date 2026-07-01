using IISConfigurationComparer.Models;
using Renci.SshNet;

namespace IISConfigurationComparer.Services;

public class WebAppConfigFile
{
    public string AppName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Fetches application config files (web.config, appsettings*.json) from a list of
/// physical paths discovered from ApplicationHost.config, via SFTP.
/// </summary>
public class WebAppConfigFetcher
{
    private static readonly string[] ConfigFileNames =
    [
        "web.config",
        "appsettings.json",
        "appsettings.Production.json",
        "appsettings.Release.json",
        "appsettings.Staging.json"
    ];

    /// <param name="appPaths">
    /// List of (appName, windowsPath) — e.g. ("QSP.API.Contract", @"D:\API\QSP.API.Contract")
    /// </param>
    public async Task<List<WebAppConfigFile>> FetchAsync(
        EnvironmentConfig environment,
        IEnumerable<(string AppName, string WindowsPath)> appPaths,
        CancellationToken ct = default)
    {
        return await Task.Run(() => FetchInternal(environment, appPaths.ToList()), ct);
    }

    private static List<WebAppConfigFile> FetchInternal(
        EnvironmentConfig environment,
        List<(string AppName, string WindowsPath)> appPaths)
    {
        using var client = BuildSftpClient(environment);
        client.Connect();

        var results = new List<WebAppConfigFile>();

        foreach (var (appName, windowsPath) in appPaths)
        {
            var sftpBase = ToSftpPath(windowsPath);

            foreach (var configFileName in ConfigFileNames)
            {
                var sftpPath = $"{sftpBase}/{configFileName}";
                if (!client.Exists(sftpPath)) continue;

                try
                {
                    using var stream = client.OpenRead(sftpPath);
                    using var reader = new StreamReader(stream);
                    results.Add(new WebAppConfigFile
                    {
                        AppName = appName,
                        FileName = configFileName,
                        Content = reader.ReadToEnd()
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: could not read {sftpPath}: {ex.Message}");
                }
            }
        }

        return results;
    }

    /// <summary>Converts a Windows path like D:\API\Foo to the SFTP path /D:/API/Foo</summary>
    public static string ToSftpPath(string windowsPath)
        => "/" + windowsPath.Replace('\\', '/').TrimStart('/');

    private static SftpClient BuildSftpClient(EnvironmentConfig environment)
    {
        AuthenticationMethod auth = string.IsNullOrWhiteSpace(environment.SshKeyFile)
            ? new PasswordAuthenticationMethod(environment.Username!, environment.Password ?? "")
            : new PrivateKeyAuthenticationMethod(environment.Username!,
                new PrivateKeyFile(environment.SshKeyFile,
                    string.IsNullOrWhiteSpace(environment.Password) ? null : environment.Password));

        return new SftpClient(new ConnectionInfo(
            environment.Host, environment.SshPort, environment.Username!, auth));
    }
}
