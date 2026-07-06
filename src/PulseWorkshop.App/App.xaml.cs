using System.IO;
using System.Windows;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App;

public partial class App : Application
{
    // Held for the whole process lifetime; releasing it lets another instance start.
    private SingleInstanceLock? _instanceLock;

    // Listens for later launches (file-association double-clicks) so they open in this window instead
    // of spawning a second one. Only created by the instance that owns the lock.
    private SingleInstanceSignal? _signal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A shell file-association / "Open with" launch passes the file path as an argument.
        var openPath = TryGetOpenPath(e.Args);

        _instanceLock = SingleInstanceLock.TryAcquire();
        if (_instanceLock is null)
        {
            // Another instance owns the app. Hand it the file to open (or a bare activate), then exit
            // quietly. Only fall back to the old "already running" notice if we couldn't reach it.
            if (!SingleInstanceSignal.TrySend(openPath ?? SingleInstanceSignal.ActivateOnly))
            {
                MessageBox.Show(
                    "PulseWorkshop is already running.",
                    "PulseWorkshop",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Register the .pw_mdlproject association so Explorer double-clicks launch us (best-effort).
        FileAssociation.EnsureRegistered();

        var window = new MainWindow();

        // Later launches forward their file here (marshalled to the UI thread) instead of opening a
        // second window.
        _signal = SingleInstanceSignal.StartListener(message =>
            window.Dispatcher.BeginInvoke(() => window.HandleShellOpen(message)));

        window.Show();

        // If we were launched by opening a file, jump straight to it.
        if (openPath is not null)
            window.HandleShellOpen(openPath);
    }

    /// <summary>Returns the first argument that is a file PulseWorkshop can open on launch, or null:
    /// a project file (<c>.pw_mdlproject</c> / <c>.pw_textureproject</c>) or an Unpack-able archive
    /// (<c>.vpk</c> / <c>.gma</c> / <c>gameinfo.txt</c>). The window routes it to the right tab.</summary>
    private static string? TryGetOpenPath(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || !File.Exists(arg))
                continue;
            if (arg.EndsWith(".pw_mdlproject", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(".pw_textureproject", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(".gma", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(arg).Equals("gameinfo.txt", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(arg);
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signal?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
