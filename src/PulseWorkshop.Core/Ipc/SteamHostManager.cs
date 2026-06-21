using System.Diagnostics;
using System.Text;

namespace PulseWorkshop.Core.Ipc;

/// <summary>
/// Owns the lifecycle of a single <c>PulseWorkshop.SteamHost</c> process for one App ID. Because
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

    /// <summary>
    /// Raised for every line the host process writes to stdout/stderr (host log + C++ bridge upload
    /// progress). Used by the App to drive a live console. Fires on a thread-pool thread.
    /// </summary>
    public event Action<string>? HostOutput;

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
            // Capture the host's log/progress output (it uses the named pipe for IPC, so redirecting
            // stdio doesn't interfere) and forward it to subscribers for the App's live console.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(appId.ToString());

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start SteamHost process.");

        _process.OutputDataReceived += OnHostLine;
        _process.ErrorDataReceived += OnHostLine;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _client = await SteamHostClient.ConnectAsync(appId, ConnectTimeout, ct).ConfigureAwait(false);
        ActiveAppId = appId;
    }

    private void OnHostLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
            HostOutput?.Invoke(e.Data);
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
