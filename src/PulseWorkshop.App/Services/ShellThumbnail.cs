using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Asks the Windows shell for the same large thumbnail Explorer shows, as a frozen WPF
/// <see cref="ImageSource"/>. This is how formats WPF itself can't decode (.tga/.psd/.dds - anything
/// with a registered thumbnail provider) get a real preview instead of a 32px file icon; see
/// <see cref="ShellIcon"/> for that last-resort fallback.
/// </summary>
public static class ShellThumbnail
{
    /// <summary>The file's shell thumbnail scaled to fit <paramref name="size"/> pixels, or null when
    /// the shell has no thumbnail provider for it (the caller then falls back to the file icon).</summary>
    public static ImageSource? GetThumbnail(string path, int size)
    {
        // Shell thumbnail providers are COM objects that generally expect an STA caller; thumbnails are
        // loaded off the UI thread (an MTA pool thread), so hop onto a short-lived STA thread for the call.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return GetThumbnailCore(path, size);

        ImageSource? result = null;
        var thread = new Thread(() => result = GetThumbnailCore(path, size)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static ImageSource? GetThumbnailCore(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);

            // THUMBNAILONLY: never let the shell hand back an icon dressed up as a thumbnail - we want
            // to know there was no real preview so the caller can fall back deliberately.
            factory.GetImage(new SIZE(size, size), SIIGBF.RESIZETOFIT | SIIGBF.THUMBNAILONLY, out hbitmap);
            if (hbitmap == IntPtr.Zero)
                return null;

            // Alpha is dropped (the HBITMAP's is premultiplied) - transparent pixels read as black,
            // which is how the engine samples them anyway.
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            // No provider, an unreadable file, or a shell error - the caller falls back to the icon.
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
