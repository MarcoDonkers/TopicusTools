using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

public interface IConfigFetcher
{
    /// <summary>
    /// Fetches the raw XML content of ApplicationHost.config from a remote environment.
    /// </summary>
    Task<string> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default);

    bool Supports(ConnectionMethod method);
}
