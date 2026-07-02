using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PulseWorkshop.App.ViewModels;

namespace PulseWorkshop.App;

/// <summary>
/// Detached, Source-engine-style console shared by every tab. Renders the app-wide
/// <see cref="ConsoleViewModel"/> as a colour-coded, selectable log with a command input line.
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
        LogBox.ScrollToEnd();
        vm.BatchAppended += OnBatchAppended;
        vm.Cleared += OnCleared;
    }

    /// <summary>Allows the next <see cref="Window.Close"/> to actually close (used at app shutdown).</summary>
    public void AllowClose() => _allowClose = true;

    private void OnBatchAppended(IReadOnlyList<ConsoleLine> batch)
    {
        // Follow the newest line only when already at the bottom - otherwise a user who scrolled up to
        // read or select older output would keep getting yanked down on every new line.
        var atBottom = LogBox.VerticalOffset >= LogBox.ExtentHeight - LogBox.ViewportHeight - 2;
        Append(batch);
        if (atBottom)
            LogBox.ScrollToEnd();
    }

    private void OnCleared() => LogBox.Document.Blocks.Clear();

    private void Append(IReadOnlyList<ConsoleLine> lines)
    {
        var blocks = LogBox.Document.Blocks;
        foreach (var line in lines)
        {
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
        switch (cmd.ToLowerInvariant())
        {
            case "clear":
            case "cls":
            case "clr":
                _vm.Clear();
                break;
            default:
                _vm.Append($"Unknown command: {cmd}", ConsoleSeverity.Warning);
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
