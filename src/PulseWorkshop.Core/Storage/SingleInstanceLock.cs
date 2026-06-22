namespace PulseWorkshop.Core.Storage;

/// <summary>
/// Cross-platform single-instance guard. Holds an exclusive OS lock on a file under the app-data
/// directory for the lifetime of the process; a second instance fails to acquire it and should exit.
///
/// Implemented with an exclusive file lock (<see cref="FileShare.None"/>) rather than a named
/// <c>Mutex</c>: named mutexes are not shared across processes on Unix in .NET, whereas an exclusive
/// file lock behaves the same on Windows and Linux (Linux backs it with flock). This keeps the guard
/// working unchanged when Linux support lands.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private FileStream? _stream;

    /// <summary>The lock file. Lives under <see cref="AppPaths.Root"/>; its contents are irrelevant.</summary>
    public static string LockFilePath { get; } = Path.Combine(AppPaths.Root, "instance.lock");

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Attempts to become the sole running instance. On success returns the lock - keep it alive for
    /// the whole process and dispose it on exit. Returns null if another instance already holds it.
    /// </summary>
    public static SingleInstanceLock? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            // FileShare.None gives us an exclusive lock for the lifetime of the stream. A second
            // process opening the same path throws IOException until this stream is closed.
            var stream = new FileStream(
                LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            // Held by another instance.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Some platforms surface a contended/locked file as access-denied.
            return null;
        }
    }

    /// <summary>
    /// Releases the lock. The lock file itself is intentionally left on disk: deleting it would race
    /// with a second instance that may already be opening the same path, which could let two
    /// instances run at once. An empty leftover file is harmless.
    /// </summary>
    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
