using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PulseWorkshop.App.ViewModels;

namespace PulseWorkshop.App;

/// <summary>
/// Detached, Source-engine-style console shared by every tab. Renders the app-wide
/// <see cref="ConsoleViewModel"/> as a colour-coded, selectable log with a command input line and
/// a regex display filter (hides non-matching lines without touching the retained history).
/// Toggled with the ~ key; the window's X just hides it (the instance lives for the app's lifetime,
/// so history and scroll position survive a hide/show).
/// </summary>
public partial class ConsoleWindow : Window
{
    private readonly ConsoleViewModel _vm;
    private readonly Brush _normalBrush;
    private readonly Brush _infoBrush;
    private readonly Brush _warnBrush;
    private readonly Brush _errorBrush;

    // Set by the owner at app shutdown so the "X hides instead of closes" behaviour is bypassed.
    private bool _allowClose;

    // Active display filter (null = show everything). Only affects what's rendered - the view
    // model's retained lines are untouched, so clearing the filter restores the full log.
    private Regex? _filter;

    // Active severity filter (null = every severity). Combined with the regex: a line must pass both.
    private ConsoleSeverity? _severityFilter;

    // False until the constructor finishes wiring the view model, so the dropdown's initial
    // SelectionChanged (raised during InitializeComponent) doesn't re-render against a null VM.
    private readonly bool _ready;

    public ConsoleWindow(ConsoleViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _normalBrush = (Brush)Resources["ConsoleNormalBrush"];
        _infoBrush = (Brush)Resources["ConsoleInfoBrush"];
        _warnBrush = (Brush)Resources["ConsoleWarnBrush"];
        _errorBrush = (Brush)Resources["ConsoleErrorBrush"];

        LogBox.Document = new FlowDocument { PagePadding = new Thickness(0) };

        Append(vm.Snapshot());
        // ScrollToEnd here is a no-op: the RichTextBox has no scrollable extent until it's laid out,
        // so a console first opened after a busy run would otherwise render scrolled to the top. Defer
        // the scroll until after the initial layout pass so it lands on the newest line.
        Dispatcher.BeginInvoke(new Action(() => LogBox.ScrollToEnd()), DispatcherPriority.Loaded);
        vm.BatchAppended += OnBatchAppended;
        vm.Cleared += OnCleared;
        _ready = true;
    }

    /// <summary>Allows the next <see cref="Window.Close"/> to actually close (used at app shutdown).</summary>
    public void AllowClose() => _allowClose = true;

    private void OnBatchAppended(IReadOnlyList<ConsoleLine> batch)
    {
        // Always follow the newest line so live output (compiles, uploads) never scrolls out of view.
        Append(batch);
        LogBox.ScrollToEnd();
    }

    private void OnCleared() => LogBox.Document.Blocks.Clear();

    private void Append(IReadOnlyList<ConsoleLine> lines)
    {
        var blocks = LogBox.Document.Blocks;
        foreach (var line in lines)
        {
            if (_severityFilter is { } sev && line.Severity != sev)
                continue;
            if (_filter is not null && !_filter.IsMatch(line.Text))
                continue;
            blocks.Add(new Paragraph(new Run(line.Text))
            {
                Margin = new Thickness(0),
                Foreground = BrushFor(line.Severity),
            });
        }

        // Trim to the model's cap so the visual document can't grow without bound.
        while (blocks.Count > ConsoleViewModel.MaxLines && blocks.FirstBlock is { } first)
            blocks.Remove(first);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var pattern = FilterBox.Text;
        Regex? next = null;
        if (pattern.Length > 0)
        {
            try
            {
                next = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                // Mid-typing / invalid pattern: flag it in red and keep the last valid filter applied.
                FilterBox.Foreground = _errorBrush;
                return;
            }
        }

        FilterBox.Foreground = _normalBrush;
        _filter = next;
        ReRender();
    }

    private void SeverityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex maps to the dropdown order: 0 = All (no restriction), 1 = Warning, 2 = Error.
        _severityFilter = SeverityBox.SelectedIndex switch
        {
            1 => ConsoleSeverity.Warning,
            2 => ConsoleSeverity.Error,
            _ => null,
        };
        ReRender();
    }

    // Re-render the whole retained log through the current filters (hidden lines come back when the
    // filters no longer exclude them).
    private void ReRender()
    {
        // The severity dropdown fires its initial SelectionChanged during InitializeComponent, before
        // the constructor has wired up the view model; there's nothing to re-render yet in that case.
        if (!_ready)
            return;
        LogBox.Document.Blocks.Clear();
        Append(_vm.Snapshot());
        LogBox.ScrollToEnd();
    }

    private void FilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FilterBox.Clear(); // TextChanged then restores the unfiltered log.
            e.Handled = true;
        }
    }

    private Brush BrushFor(ConsoleSeverity severity) => severity switch
    {
        ConsoleSeverity.Error => _errorBrush,
        ConsoleSeverity.Warning => _warnBrush,
        ConsoleSeverity.Info => _infoBrush,
        _ => _normalBrush,
    };

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var cmd = InputBox.Text.Trim();
        InputBox.Clear();
        if (cmd.Length == 0)
            return;

        _vm.Append("] " + cmd, ConsoleSeverity.Info); // echo the command, Source-style

        // First token is the command; the rest are arguments (only "crash" takes one so far).
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "clear":
            case "cls":
            case "clr":
                _vm.Clear();
                break;
            case "help":
            case "?":
                _vm.Append("Commands:", ConsoleSeverity.Info);
                _vm.Append("  clear            - clear the console");
                _vm.Append("  crash [ui|bg|task] - raise a test crash to exercise the crash reporter");
                _vm.Append("  help             - show this list");
                break;
            case "crash":
                RaiseTestCrash(arg);
                break;
            default:
                _vm.Append($"Unknown command: {cmd}", ConsoleSeverity.Warning);
                break;
        }
    }

    // Deliberately throws so the global crash reporter can be verified end to end: a report is written
    // and the crash dialog shown. "ui" (default) crashes the UI thread and closes the app; "bg" crashes
    // a background thread (also fatal); "task" faults an unawaited task (logged, non-fatal).
    private void RaiseTestCrash(string kind)
    {
        var ex = new InvalidOperationException($"Test crash triggered from the console ({(kind.Length == 0 ? "ui" : kind)}).");
        switch (kind)
        {
            case "bg":
                _vm.Append("Raising a test crash on a background thread...", ConsoleSeverity.Warning);
                new Thread(() => throw ex) { IsBackground = true }.Start();
                break;
            case "task":
                _vm.Append("Faulting an unobserved task...", ConsoleSeverity.Warning);
                // Fault a task and drop it so no one observes it; the finalizer raises the event later,
                // so force a GC to make the report appear promptly rather than at some indefinite time.
                _ = Task.Run(() => throw ex);
                Dispatcher.BeginInvoke(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }, DispatcherPriority.Background);
                break;
            default:
                _vm.Append("Raising a test crash on the UI thread...", ConsoleSeverity.Warning);
                // Post it so this key handler returns first: the throw then surfaces as a genuine
                // unhandled dispatcher exception rather than an error inside the input handler.
                Dispatcher.BeginInvoke(new Action(() => throw ex), DispatcherPriority.Background);
                break;
        }
    }

    // ~ closes the console from within it too (mirrors the Source console); the owner's ~ handler
    // reopens it. The backtick is never needed as input here, so it's safe to swallow.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.OemTilde)
        {
            _vm.IsVisible = false;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose)
            return;

        // Keep the instance alive; just hide it and let the toggle reflect the closed state.
        e.Cancel = true;
        _vm.IsVisible = false;
    }

    // --- Dark title bar (matches the main window; WPF doesn't theme the non-client area) -----------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int useDark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }
}
