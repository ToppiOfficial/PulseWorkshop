using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Catches otherwise-unhandled exceptions from every corner of the process - the WPF dispatcher
/// (UI thread), any background thread (<see cref="AppDomain.UnhandledException"/>) and faulted tasks
/// nobody awaited (<see cref="TaskScheduler.UnobservedTaskException"/>) - and, for each, writes a
/// timestamped crash report to <see cref="AppPaths.CrashesDir"/> and shows the user what happened and
/// where the log is. A UI-thread crash then shuts the app down cleanly rather than dying via the
/// default Windows error dialog.
///
/// The console's <c>crash</c> command deliberately routes here so the whole path can be exercised.
/// </summary>
public static class CrashReporter
{
    // A crash while we're already reporting one must not recurse into another report (and a torn-down
    // process can raise several handlers for the same failure). Report the first, note the rest quietly.
    private static int _reporting;

    /// <summary>Registers the global exception handlers. Call once, as early in startup as possible.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (Application.Current is { } app)
            app.DispatcherUnhandledException += OnDispatcherException;
    }

    // UI-thread crash. We handle it so WPF doesn't tear the process down with its own error dialog:
    // write the report, tell the user, then shut down cleanly.
    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, "UI thread (Dispatcher)", terminating: true);
        Application.Current?.Shutdown(1);
    }

    // Background-thread crash. The runtime is already tearing the process down (IsTerminating is
    // normally true) and we can't veto it - just get the report out and notify before it dies.
    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
                 ?? new Exception("Non-Exception object thrown: " + e.ExceptionObject);
        Report(ex, "Background thread (AppDomain)", terminating: e.IsTerminating);
    }

    // A task faulted and nothing observed it. Modern .NET no longer crashes the process for this, so
    // we mark it observed and log it rather than let it escalate.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "Unobserved task", terminating: false);
        e.SetObserved();
    }

    /// <summary>
    /// Writes a crash report for <paramref name="ex"/> and shows the user a summary with the log path.
    /// Never throws - a failure in here must not mask the original crash. Returns the report path, or
    /// null if writing failed (or a report is already in flight).
    /// </summary>
    public static string? Report(Exception ex, string source, bool terminating)
    {
        // First crash wins; ignore re-entrant / duplicate reports for the same teardown.
        if (Interlocked.Exchange(ref _reporting, 1) == 1)
            return null;

        try
        {
            var path = TryWriteReport(ex, source, terminating);
            ShowUser(ex, path, terminating);
            return path;
        }
        catch
        {
            // A crash handler that throws is worse than useless - swallow everything.
            return null;
        }
        finally
        {
            // Leave the guard raised while terminating so late duplicate handlers stay quiet; only a
            // survivable crash (unobserved task, or a UI crash we recovered from) re-arms the reporter.
            if (!terminating)
                Interlocked.Exchange(ref _reporting, 0);
        }
    }

    private static string? TryWriteReport(Exception ex, string source, bool terminating)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CrashesDir);
            var file = Path.Combine(
                AppPaths.CrashesDir,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(file, BuildReport(ex, source, terminating), Encoding.UTF8);
            return file;
        }
        catch
        {
            return null; // Disk full / permissions / whatever - we still try to tell the user.
        }
    }

    private static string BuildReport(Exception ex, string source, bool terminating)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PulseWorkshop crash report");
        sb.AppendLine("==========================");
        sb.AppendLine($"Time (local): {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"Time (UTC):   {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
        sb.AppendLine($"App version:  {AppVersion}");
        sb.AppendLine($"Source:       {source}");
        sb.AppendLine($"Terminating:  {terminating}");
        sb.AppendLine($"OS:           {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Runtime:      {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process arch: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Working set:  {Environment.WorkingSet / (1024 * 1024)} MB");
        sb.AppendLine();
        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        // Exception.ToString() already unrolls the type, message, stack trace and every inner exception.
        sb.AppendLine(ex.ToString());
        return sb.ToString();
    }

    private static void ShowUser(Exception ex, string? reportPath, bool terminating)
    {
        var summary = $"{ex.GetType().Name}: {ex.Message}";
        var body = new StringBuilder();
        body.AppendLine(terminating
            ? "PulseWorkshop hit an unexpected error and has to close."
            : "PulseWorkshop hit an unexpected error.");
        body.AppendLine();
        body.AppendLine(summary);
        body.AppendLine();
        body.AppendLine(reportPath is not null
            ? $"A crash report was saved to:\n{reportPath}"
            : $"A crash report could not be written. Crash reports live in:\n{AppPaths.CrashesDir}");

        // A background-thread crash can fire off the UI thread; marshal the dialog so it actually shows.
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ShowDialog(body.ToString()));
            return;
        }
        ShowDialog(body.ToString());
    }

    private static void ShowDialog(string message) => MessageBox.Show(
        message, "PulseWorkshop - Crash", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
