using System.Diagnostics;

namespace SrcWorkshop.Core.Ipc;

/// <summary>
/// Owns the lifecycle of a single <c>SrcWorkshop.SteamHost</c> process for one App ID. Because
/// the Steamworks App ID is process-global, switching games means stopping the current host and
/// starting a fresh one — exactly what <see cref="SwitchToAsync"/> does.
/// </summary>
public sealed class SteamHostManager : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _hostExePath;

    private Process? _process;
    private SteamHostClient? _client;

    public uint? ActiveAppId { get; private set; }

    public SteamHostManager(string hostExePath) => _hostExePath = hostExePath;

    /// <summary>The connected client for the active host, or throws if none is running.</summary>
    public SteamHostClient Client =>
        _client ?? throw new InvalidOperationException("No Steam host is running. Call SwitchToAsync first.");

    /// <summary>
    /// Ensures a host for <paramref name="appId"/> is running and connected, relaunching if a
    /// different game was active.
    /// </summary>
    public async Task SwitchToAsync(uint appId, CancellationToken ct = default)
    {
        if (ActiveAppId == appId && _client is not null && _process is { HasExited: false })
            return;

        await StopAsync().ConfigureAwait(false);

        if (!File.Exists(_hostExePath))
            throw new FileNotFoundException("SteamHost executable not found.", _hostExePath);

        var psi = new ProcessStartInfo
        {
            FileName = _hostExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(appId.ToString());

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start SteamHost process.");

        _client = await SteamHostClient.ConnectAsync(appId, ConnectTimeout, ct).ConfigureAwait(false);
        ActiveAppId = appId;
    }

    public async Task StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have already exited after the Shutdown request.
            }
            _process.Dispose();
            _process = null;
        }

        ActiveAppId = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
