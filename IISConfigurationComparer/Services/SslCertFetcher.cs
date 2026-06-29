using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

public record SslCertBinding(string IpPort, string Thumbprint, string? AppId, string? StoreName, string? SniHost);

/// <summary>
/// Fetches HTTP.sys SSL certificate bindings via SSH by running `netsh http show sslcert`
/// on the remote server. These bindings are NOT stored in ApplicationHost.config.
/// </summary>
public class SslCertFetcher
{
    public async Task<List<SslCertBinding>> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default)
    {
        return await Task.Run(() => FetchInternal(environment), ct);
    }

    private static List<SslCertBinding> FetchInternal(EnvironmentConfig environment)
    {
        if (environment.Method != ConnectionMethod.Ssh)
            throw new NotSupportedException("SSL cert fetching is currently only supported via SSH.");

        var output = RunSshCommand(environment, "netsh http show sslcert");
        return ParseNetshOutput(output);
    }

    private static string RunSshCommand(EnvironmentConfig environment, string command)
    {
        var authArg = $"-o StrictHostKeyChecking=no -o BatchMode=yes";
        var userHost = $"{Uri.EscapeDataString(environment.Username!)}@{environment.Host}";

        // Use sshpass if password auth; key-based auth otherwise
        // Fall back to SSH.NET for password auth (no sshpass dependency)
        return RunViaSshNet(environment, command);
    }

    private static string RunViaSshNet(EnvironmentConfig environment, string command)
    {
        using var client = BuildSshClient(environment);
        client.Connect();
        using var cmd = client.CreateCommand(command);
        return cmd.Execute();
    }

    private static Renci.SshNet.SshClient BuildSshClient(EnvironmentConfig environment)
    {
        Renci.SshNet.AuthenticationMethod auth;

        if (!string.IsNullOrWhiteSpace(environment.SshKeyFile))
        {
            var keyFile = new Renci.SshNet.PrivateKeyFile(environment.SshKeyFile,
                string.IsNullOrWhiteSpace(environment.Password) ? null : environment.Password);
            auth = new Renci.SshNet.PrivateKeyAuthenticationMethod(environment.Username!, keyFile);
        }
        else
        {
            auth = new Renci.SshNet.PasswordAuthenticationMethod(
                environment.Username!, environment.Password ?? string.Empty);
        }

        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            environment.Host, environment.SshPort, environment.Username!, auth);

        return new Renci.SshNet.SshClient(connectionInfo);
    }

    /// <summary>
    /// Parses the output of `netsh http show sslcert` into structured bindings.
    /// </summary>
    private static List<SslCertBinding> ParseNetshOutput(string output)
    {
        var bindings = new List<SslCertBinding>();
        // Split on double blank lines (each binding block is separated by blank lines)
        var blocks = Regex.Split(output, @"\r?\n\r?\n+");

        foreach (var block in blocks)
        {
            string? ipPort     = Extract(block, @"IP:port\s+:\s+(.+)");
            string? sniHost    = Extract(block, @"Hostname:port\s+:\s+(.+)");
            string? thumbprint = Extract(block, @"Certificate Hash\s+:\s+([0-9a-fA-F]+)");
            string? appId      = Extract(block, @"Application ID\s+:\s+(\{[^}]+\})");
            string? storeName  = Extract(block, @"Certificate Store Name\s+:\s+(.+)");

            var endpoint = ipPort ?? sniHost;
            if (endpoint is null || thumbprint is null) continue;

            bindings.Add(new SslCertBinding(
                endpoint.Trim(),
                thumbprint.Trim().ToUpperInvariant(),
                appId?.Trim(),
                storeName?.Trim(),
                sniHost?.Trim()));
        }

        return bindings;
    }

    private static string? Extract(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}

/// <summary>
/// Compares SSL cert bindings between two environments.
/// </summary>
public class SslCertComparer
{
    public List<string> Compare(
        List<SslCertBinding> left, string leftName,
        List<SslCertBinding> right, string rightName)
    {
        var differences = new List<string>();

        var leftByEndpoint  = left.ToDictionary(b => NormalizeEndpoint(b.IpPort), b => b);
        var rightByEndpoint = right.ToDictionary(b => NormalizeEndpoint(b.IpPort), b => b);

        foreach (var (ep, lb) in leftByEndpoint)
        {
            if (!rightByEndpoint.TryGetValue(ep, out var rb))
            {
                differences.Add($"[ONLY IN {leftName.ToUpper()}]  {lb.IpPort}  cert={lb.Thumbprint}");
            }
            else if (!lb.Thumbprint.Equals(rb.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"[CERT DIFFERS]  {lb.IpPort}  {leftName}={lb.Thumbprint}  {rightName}={rb.Thumbprint}");
            }
        }

        foreach (var (ep, rb) in rightByEndpoint)
        {
            if (!leftByEndpoint.ContainsKey(ep))
                differences.Add($"[ONLY IN {rightName.ToUpper()}]  {rb.IpPort}  cert={rb.Thumbprint}");
        }

        return differences;
    }

    // Strip server-specific IP prefixes — compare only the port part for wildcard bindings
    private static string NormalizeEndpoint(string ep)
    {
        // 0.0.0.0:443 and *:443 should both normalize to :443 for comparison
        return Regex.Replace(ep, @"^(\*|0\.0\.0\.0)", "any");
    }
}
