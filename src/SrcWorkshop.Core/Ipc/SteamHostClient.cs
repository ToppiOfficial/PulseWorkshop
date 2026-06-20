using System.IO.Pipes;
using System.Text;

namespace SrcWorkshop.Core.Ipc;

/// <summary>
/// Client end of the named pipe to a running <c>SrcWorkshop.SteamHost</c>. Sends one request and
/// awaits its correlated response. A single connection is held open for the host's lifetime.
/// </summary>
public sealed class SteamHostClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SteamHostClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static async Task<SteamHostClient> ConnectAsync(
        uint appId, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(
            ".", PipeProtocol.PipeNameFor(appId), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        return new SteamHostClient(pipe);
    }

    /// <summary>Sends <paramref name="request"/> and returns the matching response.</summary>
    public async Task<PipeResponse> SendAsync(PipeRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(PipeJson.Serialize(request).AsMemory(), ct)
                .ConfigureAwait(false);

            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("SteamHost closed the pipe.");

            var response = PipeJson.Deserialize<PipeResponse>(line)
                ?? throw new IOException("Malformed response from SteamHost.");
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe.IsConnected)
            {
                var shutdown = new PipeRequest { Kind = RequestKind.Shutdown, RequestId = "bye" };
                await SendAsync(shutdown).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort shutdown; ignore failures while tearing down.
        }
        finally
        {
            Quietly(() => _writer.Dispose());
            Quietly(() => _reader.Dispose());
            try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch { }
            Quietly(() => _gate.Dispose());
        }

        static void Quietly(Action dispose)
        {
            try { dispose(); } catch { }
        }
    }
}
