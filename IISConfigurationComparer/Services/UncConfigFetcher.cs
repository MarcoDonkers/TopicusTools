using System.Net;
using System.Diagnostics;
using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Fetches ApplicationHost.config via UNC admin shares (\\server\C$\...).
/// Requires the caller to have administrative rights on the target host.
/// </summary>
public class UncConfigFetcher : IConfigFetcher
{
    public bool Supports(ConnectionMethod method) => method == ConnectionMethod.UncShare;

    public async Task<string> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default)
    {
        var uncPath = $@"\\{environment.Host}\C$\{environment.ConfigPath}";
        NetworkCredential? credential = BuildCredential(environment);

        if (credential is not null)
            await MapNetworkDriveAsync(environment.Host, credential, ct);

        try
        {
            if (!File.Exists(uncPath))
                throw new FileNotFoundException(
                    $"ApplicationHost.config not found at '{uncPath}'. " +
                    "Ensure the C$ share is accessible and you have admin rights.", uncPath);

            return await File.ReadAllTextAsync(uncPath, ct);
        }
        finally
        {
            if (credential is not null)
                await UnmapNetworkDriveAsync(environment.Host, ct);
        }
    }

    private static async Task MapNetworkDriveAsync(string host, NetworkCredential credential, CancellationToken ct)
    {
        var userArg = string.IsNullOrWhiteSpace(credential.Domain)
            ? credential.UserName
            : $@"{credential.Domain}\{credential.UserName}";

        var result = await RunProcessAsync(
            "net", $@"use \\{host}\IPC$ ""{credential.Password}"" /user:""{userArg}""", ct);

        // Exit code 2 means already connected — that's fine
        if (result.ExitCode != 0 && result.ExitCode != 2 && !result.Output.Contains("already"))
            throw new InvalidOperationException(
                $"Failed to authenticate to \\\\{host}: {result.Output.Trim()}");
    }

    private static async Task UnmapNetworkDriveAsync(string host, CancellationToken ct)
    {
        await RunProcessAsync("net", $@"use \\{host}\IPC$ /delete /yes", ct);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string exe, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        output += await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output);
    }

    private static NetworkCredential? BuildCredential(EnvironmentConfig env)
    {
        if (string.IsNullOrWhiteSpace(env.Username))
            return null;

        return string.IsNullOrWhiteSpace(env.Domain)
            ? new NetworkCredential(env.Username, env.Password)
            : new NetworkCredential(env.Username, env.Password, env.Domain);
    }
}

