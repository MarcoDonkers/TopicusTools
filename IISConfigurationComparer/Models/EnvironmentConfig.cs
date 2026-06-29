namespace IISConfigurationComparer.Models;

public enum ConnectionMethod
{
    UncShare,
    WinRm,
    Ssh
}

public class EnvironmentConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public ConnectionMethod Method { get; set; } = ConnectionMethod.UncShare;

    // Credentials (optional — uses current Windows identity when omitted for UNC/WinRM)
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }

    // SSH-specific
    public int SshPort { get; set; } = 22;
    public string? SshKeyFile { get; set; }

    // Override the default config path if needed
    public string ConfigPath { get; set; } =
        @"Windows\System32\inetsrv\config\ApplicationHost.config";
}
