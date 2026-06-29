using System.Diagnostics;
using System.Text;
using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Fetches ApplicationHost.config via PowerShell remoting (WinRM).
/// Uses a local powershell.exe process with Invoke-Command so no heavy SDK is required.
/// Requires WinRM to be enabled on the target: `Enable-PSRemoting -Force`
/// </summary>
public class WinRmConfigFetcher : IConfigFetcher
{
    public bool Supports(ConnectionMethod method) => method == ConnectionMethod.WinRm;

    public async Task<string> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default)
    {
        return await Task.Run(() => FetchInternal(environment), ct);
    }

    private static string FetchInternal(EnvironmentConfig environment)
    {
        var script = BuildScript(environment);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{EscapeForArgument(script)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell process.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(
                $"WinRM fetch from '{environment.Host}' failed (exit {process.ExitCode}): {error}");

        return output;
    }

    private static string BuildScript(EnvironmentConfig environment)
    {
        var remoteConfigPath = $@"C:\{environment.ConfigPath}";
        var scriptBody = $"Get-Content -Path '{remoteConfigPath}' -Raw";

        if (!string.IsNullOrWhiteSpace(environment.Username))
        {
            var userWithDomain = string.IsNullOrWhiteSpace(environment.Domain)
                ? environment.Username
                : $@"{environment.Domain}\\{environment.Username}";

            return $@"
$pw = ConvertTo-SecureString '{environment.Password}' -AsPlainText -Force;
$cred = New-Object System.Management.Automation.PSCredential('{userWithDomain}', $pw);
Invoke-Command -ComputerName '{environment.Host}' -Credential $cred -ScriptBlock {{ {scriptBody} }}";
        }

        return $"Invoke-Command -ComputerName '{environment.Host}' -ScriptBlock {{ {scriptBody} }}";
    }

    private static string EscapeForArgument(string script) =>
        script.Replace("\"", "\\\"");
}
