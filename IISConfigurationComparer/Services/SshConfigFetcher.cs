using IISConfigurationComparer.Models;
using Renci.SshNet;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Fetches ApplicationHost.config via SSH/SFTP.
/// Requires OpenSSH to be installed and running on the target Windows server.
/// </summary>
public class SshConfigFetcher : IConfigFetcher
{
    public bool Supports(ConnectionMethod method) => method == ConnectionMethod.Ssh;

    public async Task<string> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default)
    {
        return await Task.Run(() => FetchInternal(environment), ct);
    }

    private static string FetchInternal(EnvironmentConfig environment)
    {
        using var client = BuildSftpClient(environment);
        client.Connect();

        // SFTP uses forward-slash paths; Windows SFTP root is typically C:/
        var remotePath = $"/C:/{environment.ConfigPath.Replace('\\', '/')}";

        if (!client.Exists(remotePath))
            throw new FileNotFoundException(
                $"ApplicationHost.config not found at remote path '{remotePath}' on {environment.Host}.");

        using var stream = client.OpenRead(remotePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SftpClient BuildSftpClient(EnvironmentConfig environment)
    {
        AuthenticationMethod auth;

        if (!string.IsNullOrWhiteSpace(environment.SshKeyFile))
        {
            var keyFile = new PrivateKeyFile(environment.SshKeyFile,
                string.IsNullOrWhiteSpace(environment.Password) ? null : environment.Password);
            auth = new PrivateKeyAuthenticationMethod(environment.Username!, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(
                environment.Username!, environment.Password ?? string.Empty);
        }

        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            environment.Host,
            environment.SshPort,
            environment.Username!,
            auth);

        return new SftpClient(connectionInfo);
    }
}
