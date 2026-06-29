using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IISConfigurationComparer.Services;

/// <summary>
/// Establishes a temporary authenticated UNC connection using WNetAddConnection2
/// so that subsequent File I/O against the UNC path uses the given credentials.
/// Disposed automatically when done.
/// </summary>
internal sealed class TemporaryNetworkConnection : IDisposable
{
    private readonly string _host;
    private bool _connected;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string name, int flags, bool force);

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    public TemporaryNetworkConnection(string host, System.Net.NetworkCredential credential)
    {
        _host = $@"\\{host}\IPC$";
        var resource = new NETRESOURCE
        {
            dwType = 0, // RESOURCETYPE_ANY
            lpRemoteName = _host
        };

        var userWithDomain = string.IsNullOrWhiteSpace(credential.Domain)
            ? credential.UserName
            : $@"{credential.Domain}\{credential.UserName}";

        int result = WNetAddConnection2(ref resource, credential.Password, userWithDomain, 0);
        if (result != 0)
            throw new Win32Exception(result, $"Failed to connect to {_host}: Win32 error {result}");

        _connected = true;
    }

    public void Dispose()
    {
        if (_connected)
        {
            WNetCancelConnection2(_host, 0, true);
            _connected = false;
        }
    }
}
