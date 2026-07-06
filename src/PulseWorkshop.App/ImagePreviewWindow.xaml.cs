using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PulseWorkshop.App;

/// <summary>
/// Modeless full-size preview of an image file, opened by clicking an asset thumbnail. Sizes itself
/// to the image (capped to the work area), closes on Esc, and decodes the file up front with
/// <see cref="BitmapCacheOption.OnLoad"/> so the file isn't held open while the window is up.
/// </summary>
public partial class ImagePreviewWindow : Window
{
    // Only one preview is ever open at a time; opening another (or re-clicking a thumbnail)
    // closes the previous one instead of stacking windows.
    private static ImagePreviewWindow? _current;

    private ImagePreviewWindow(string path, BitmapImage bmp)
    {
        InitializeComponent();
        PreviewImage.Source = bmp;
        Title = $"{Path.GetFileName(path)} - {bmp.PixelWidth}x{bmp.PixelHeight}";
        MaxWidth = SystemParameters.WorkArea.Width * 0.9;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (format.Length == 0)
            format = "?";
        InfoText.Text = $"{bmp.PixelWidth} x {bmp.PixelHeight}  -  {format}  -  {FormatFileSize(path)}";
    }

    /// <summary>Human-readable size of the file at <paramref name="path"/>, or "?" if it can't be read.</summary>
    private static string FormatFileSize(string path)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            return "?";
        }

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

    /// <summary>Opens a preview for the image at <paramref name="path"/>; no-ops when the file
    /// can't be decoded (it may have changed on disk since the thumbnail loaded).</summary>
    public static void ShowPreview(Window owner, string path)
    {
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Bypass WPF's URI-keyed bitmap cache so the preview shows the file's current contents.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
        }
        catch
        {
            return;
        }

        // Close any preview that's already up so clicking a different thumbnail replaces it.
        _current?.Close();

        var window = new ImagePreviewWindow(path, bmp) { Owner = owner };
        _current = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
                _current = null;
        };
        window.Show();
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
