using System.Windows;
using System.Windows.Threading;
using PulseWorkshop.App.Mvvm;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// Severity of a console line, mapped to a colour by the <see cref="ConsoleWindow"/>: Normal is white,
/// Warning yellow, Error red, and Info a Steam-blue accent (section dividers and echoed commands).
/// </summary>
public enum ConsoleSeverity
{
    Normal,
    Info,
    Warning,
    Error,
}

/// <summary>One line in the shared console: its text plus a severity that drives its colour.</summary>
public sealed record ConsoleLine(string Text, ConsoleSeverity Severity);

/// <summary>
/// The single, app-wide console. Every tab's output - Workshop uploads, studiomdl compiles, packaging -
/// streams into this one model, which the detached <see cref="ConsoleWindow"/> renders as a
/// Source-style, colour-coded log.
///
/// Output arrives one line at a time on background threads, thousands per compile, so appends are
/// buffered and coalesced into a single UI batch per dispatcher idle cycle (the same pattern the
/// per-tab terminals used before they were unified): a burst collapses into one <see cref="BatchAppended"/>.
/// </summary>
public sealed class ConsoleViewModel : ObservableObject
{
    /// <summary>Hard cap on retained lines; the oldest are dropped past this. The window trims to match.</summary>
    public const int MaxLines = 2500;

    private readonly List<ConsoleLine> _lines = new();
    private readonly List<ConsoleLine> _pending = new();
    private readonly object _gate = new();
    private bool _flushScheduled;
    private bool _isVisible;

    public ConsoleViewModel()
    {
        ClearCommand = new RelayCommand(Clear);
    }

    /// <summary>Fired on the UI thread with each freshly-flushed batch of lines (append-only).</summary>
    public event Action<IReadOnlyList<ConsoleLine>>? BatchAppended;

    /// <summary>Fired on the UI thread when the log is cleared.</summary>
    public event Action? Cleared;

    public RelayCommand ClearCommand { get; }

    /// <summary>Whether the detached console window is open. The window's X and the ~ key both toggle this.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>A point-in-time copy of the retained lines, for a newly-opened window to render.</summary>
    public IReadOnlyList<ConsoleLine> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }

    /// <summary>Appends a line, auto-classifying its severity from its text.</summary>
    public void Append(string text) => Append(text, Classify(text));

    /// <summary>Appends a line with an explicit severity.</summary>
    public void Append(string text, ConsoleSeverity severity)
    {
        lock (_gate)
        {
            _pending.Add(new ConsoleLine(text, severity));
            if (_flushScheduled)
                return; // A flush is already queued; it will pick this line up.
            _flushScheduled = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            Flush(); // No UI (headless harness): apply synchronously.
        else
            dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    /// <summary>A timestamped, app-authored line (e.g. upload progress), auto-classified.</summary>
    public void Log(string message) => Append($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>A Steam-blue section divider, e.g. at the start of a compile or package run.</summary>
    public void Divider(string title) => Append($"=== {title} ===", ConsoleSeverity.Info);

    /// <summary>
    /// Awaits a Background-priority no-op so a queued flush (also Background priority, enqueued earlier)
    /// has run - callers use this to be sure streamed output is on screen before their own marker lines.
    /// </summary>
    public Task FlushAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.CompletedTask;
        return dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task;
    }

    /// <summary>Clears the log. Safe to call from any thread; marshalled to the UI thread.</summary>
    public void Clear()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Clear);
            return;
        }

        lock (_gate)
        {
            _pending.Clear();
            _lines.Clear();
            _flushScheduled = false;
        }
        Cleared?.Invoke();
    }

    // Always runs on the UI thread (or synchronously when headless). Drains the pending buffer in one
    // pass, caps the retained history, and hands the new lines to the window.
    private void Flush()
    {
        ConsoleLine[] batch;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_pending.Count == 0)
                return; // A Clear() ran between scheduling and now.
            batch = _pending.ToArray();
            _lines.AddRange(_pending);
            _pending.Clear();
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
        BatchAppended?.Invoke(batch);
    }

    // Best-effort colour hint from a plain-text line (studiomdl / packer output carries no severity).
    // Explicit callers pass a severity directly and bypass this.
    private static ConsoleSeverity Classify(string text)
    {
        if (text.StartsWith("==="))
            return ConsoleSeverity.Info;

        var lower = text.ToLowerInvariant();
        if ((lower.Contains("error") && !lower.Contains("0 error") && !lower.Contains("no error"))
            || lower.Contains("failed") || lower.Contains("fatal"))
            return ConsoleSeverity.Error;
        if (lower.Contains("warning") || lower.Contains("warn:"))
            return ConsoleSeverity.Warning;
        return ConsoleSeverity.Normal;
    }
}
