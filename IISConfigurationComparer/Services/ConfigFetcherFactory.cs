using IISConfigurationComparer.Models;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Resolves the appropriate IConfigFetcher for a given connection method
/// and orchestrates fetching from remote environments.
/// </summary>
public class ConfigFetcherFactory
{
    private readonly IEnumerable<IConfigFetcher> _fetchers;

    public ConfigFetcherFactory(IEnumerable<IConfigFetcher> fetchers)
    {
        _fetchers = fetchers;
    }

    public IConfigFetcher Resolve(ConnectionMethod method)
    {
        var fetcher = _fetchers.FirstOrDefault(f => f.Supports(method))
            ?? throw new NotSupportedException(
                $"No fetcher registered for connection method '{method}'.");
        return fetcher;
    }

    public async Task<string> FetchAsync(EnvironmentConfig environment, CancellationToken ct = default)
    {
        var fetcher = Resolve(environment.Method);
        return await fetcher.FetchAsync(environment, ct);
    }
}
