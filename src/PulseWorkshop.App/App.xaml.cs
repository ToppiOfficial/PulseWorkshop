using System.Windows;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App;

public partial class App : Application
{
    // Held for the whole process lifetime; releasing it lets another instance start.
    private SingleInstanceLock? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceLock = SingleInstanceLock.TryAcquire();
        if (_instanceLock is null)
        {
            MessageBox.Show(
                "PulseWorkshop is already running.",
                "PulseWorkshop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
