using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PulseWorkshop.App;

/// <summary>
/// Modeless raw-text preview of a text asset, opened by clicking its thumbnail. Reads the file up
/// front (capped so a huge file can't stall the UI), shows it read-only in a monospace box, and
/// closes on Esc. The counterpart to <see cref="ImagePreviewWindow"/> for non-image assets.
/// </summary>
public partial class TextPreviewWindow : Window
{
    // Text past this point is dropped with a note in the caption - a preview, not a full editor.
    private const int MaxPreviewBytes = 2 * 1024 * 1024;

    // Only one preview (image or text) is meaningful at a time; opening another closes the previous.
    private static TextPreviewWindow? _current;

    private TextPreviewWindow(string path, string text, bool truncated, long byteLength)
    {
        InitializeComponent();
        PreviewText.Text = text;
        Title = Path.GetFileName(path);

        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (format.Length == 0)
            format = "?";
        int lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        var caption = $"{format}  -  {lines} lines  -  {FormatFileSize(byteLength)}";
        if (truncated)
            caption += "  -  preview truncated";
        InfoText.Text = caption;
    }

    /// <summary>Human-readable size for a byte count.</summary>
    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    /// <summary>Opens a raw-text preview for the file at <paramref name="path"/>; no-ops when the
    /// file can't be read (it may have changed on disk since the thumbnail loaded).</summary>
    public static void ShowPreview(Window owner, string path)
    {
        string text;
        bool truncated;
        long byteLength;
        try
        {
            byteLength = new FileInfo(path).Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            truncated = byteLength > MaxPreviewBytes;
            var buffer = new byte[truncated ? MaxPreviewBytes : (int)byteLength];
            int read = ReadExactly(stream, buffer);
            text = DecodeText(buffer, read);
        }
        catch
        {
            return;
        }

        // Close any preview that's already up so clicking a different thumbnail replaces it.
        _current?.Close();

        var window = new TextPreviewWindow(path, text, truncated, byteLength) { Owner = owner };
        _current = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
                _current = null;
        };
        window.Show();
    }

    /// <summary>Fills <paramref name="buffer"/> from the stream and returns how many bytes were read
    /// (a short final chunk is fine - it means the file shrank since we measured it).</summary>
    private static int ReadExactly(FileStream stream, byte[] buffer)
    {
        int total = 0;
        int n;
        while (total < buffer.Length && (n = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += n;
        return total;
    }

    /// <summary>Decodes the leading <paramref name="count"/> bytes as text, honoring a UTF-8/UTF-16
    /// BOM and otherwise assuming UTF-8. Kept lenient so binary-ish files still render something.</summary>
    private static string DecodeText(byte[] buffer, int count)
    {
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(buffer, 3, count - 3);
        if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(buffer, 2, count - 2);
        if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(buffer, 2, count - 2);
        return new System.Text.UTF8Encoding(false, false).GetString(buffer, 0, count);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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
