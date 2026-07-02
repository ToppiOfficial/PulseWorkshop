using System.IO.Pipes;
using System.Text;

namespace PulseWorkshop.Core.Storage;

/// <summary>
/// A tiny cross-process signal that pairs with <see cref="SingleInstanceLock"/>. The primary instance
/// listens for messages from later launches (e.g. a shell file-association double-click that lands
/// while the app is already running), so the running window can open the requested file instead of a
/// second window appearing - or just be brought to the front.
///
/// Backed by a named pipe, which behaves the same on Windows and Linux in .NET (so it stays working
/// when Linux support lands, matching the file-lock guard's cross-platform intent). Each message is a
/// single newline-terminated UTF-8 line: a file path to open, or <see cref="ActivateOnly"/> (an empty
/// line) meaning "just activate the running window".
/// </summary>
public sealed class SingleInstanceSignal : IDisposable
{
    /// <summary>Message that asks the running instance only to bring its window to the front.</summary>
    public const string ActivateOnly = "";

    // Fixed, app-specific pipe name. Single-instance is already scoped per user by the lock file, and a
    // desktop session is single-user, so a constant name is sufficient here.
    private const string PipeName = "PulseWorkshop.SingleInstance";

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _onMessage;

    private SingleInstanceSignal(Action<string> onMessage) => _onMessage = onMessage;

    /// <summary>
    /// Starts listening (on a background task) for messages from later launches. Keep the returned
    /// instance alive for the process lifetime and dispose it on exit to stop the listener.
    /// </summary>
    public static SingleInstanceSignal StartListener(Action<string> onMessage)
    {
        var signal = new SingleInstanceSignal(onMessage);
        _ = Task.Run(signal.ListenAsync);
        return signal;
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadLineAsync(_cts.Token);
                if (message is not null)
                    _onMessage(message);
            }
            catch (OperationCanceledException)
            {
                return; // Disposed - stop listening.
            }
            catch
            {
                // A malformed or aborted connection must never kill the listener; loop and re-arm.
            }
        }
    }

    /// <summary>
    /// Sends a message to the already-running instance's listener. Returns true if a listener accepted
    /// it (so the caller knows the running app got the request and this launch can exit quietly).
    /// </summary>
    public static bool TrySend(string message, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(message ?? ActivateOnly);
            return true;
        }
        catch
        {
            // No listener, or it didn't accept in time.
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
