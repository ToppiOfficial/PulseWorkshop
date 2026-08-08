using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseWorkshop.Core.Imaging;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.App.Services;

/// <summary>
/// The one place a file path becomes a preview bitmap. The Source-pipeline formats WPF has no codec
/// for (<c>.vtf</c>, <c>.dds</c>, <c>.tga</c>, <c>.psd</c>) are decoded in managed code so previews
/// match what the VTF tool will actually import, with no dependency on Windows' thumbnail providers
/// (which need the right shell extension installed, and whose out-of-process hosts are what made the
/// grid stutter on window activation).
/// <para>
/// Results are cached per (path, last write, size, requested size), so a rescan that rebuilds the
/// tile view models repaints the identical bitmap instead of blanking every tile and re-decoding.
/// </para>
/// </summary>
public static class TexturePreview
{
    /// <summary>Above this many entries the cache is dropped wholesale on the next insert.</summary>
    // ponytail: flat cap + clear-all, not an LRU. A grid is capped at 300 tiles, so this only trips
    // when the user sweeps through several large folders; swap in an LRU if that churn ever shows.
    private const int CacheLimit = 512;

    private static readonly Dictionary<CacheKey, ImageSource> Cache = [];

    private readonly record struct CacheKey(
        string Path, long WriteTicks, long Length, int MaxPixels, bool Alpha);

    /// <summary>
    /// The already-decoded preview for this file, or null if it hasn't been loaded yet. Cheap and
    /// synchronous - call it from a view model constructor so a rebuilt tile paints its bitmap on the
    /// first frame rather than flashing empty until the async load lands.
    /// </summary>
    public static ImageSource? TryGetCached(string path, int maxPixels, bool showAlpha)
    {
        if (KeyFor(path, maxPixels, showAlpha) is not { } key)
            return null;
        lock (Cache)
            return Cache.TryGetValue(key, out var cached) ? cached : null;
    }

    /// <summary>
    /// Decodes the image at <paramref name="path"/>, scaled so its longest edge is at most
    /// <paramref name="maxPixels"/> (pass 0 or less for full resolution). Returns a frozen bitmap - safe
    /// to build on a worker thread and hand straight to a binding - or null when nothing can decode the
    /// file, in which case the caller falls back to a file icon.
    /// <para>
    /// <paramref name="showAlpha"/> chooses whether the image's alpha channel is composited or the
    /// colour channels are shown opaque. Callers generally want false: a Source texture's alpha is
    /// usually a mask (specular, envmap, translucency) rather than transparency, and compositing one
    /// of those leaves a perfectly good image as a near-invisible ghost. Nothing in the file
    /// distinguishes the two cases, hence a caller's choice rather than a guess. Both states cache
    /// separately, so flipping a toggle costs one decode each way.
    /// </para>
    /// </summary>
    public static ImageSource? Load(string path, int maxPixels, bool showAlpha)
    {
        var key = KeyFor(path, maxPixels, showAlpha);
        if (key is { } hit)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(hit, out var cached))
                    return cached;
            }
        }

        var image = Decode(path, maxPixels, showAlpha);
        if (image is not null && key is { } store)
        {
            lock (Cache)
            {
                if (Cache.Count >= CacheLimit)
                    Cache.Clear();
                Cache[store] = image;
            }
        }
        return image;
    }

    /// <summary>The cache key for a file, or null when it can't be stat'd (missing/locked) - such a
    /// file is decoded uncached rather than pinned under a stale key.</summary>
    private static CacheKey? KeyFor(string path, int maxPixels, bool showAlpha)
    {
        // Full-resolution loads (the preview window) are one-shot and can be tens of megabytes -
        // caching them would pin far more than the thumbnail grid ever does.
        if (maxPixels <= 0)
            return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return new CacheKey(path, info.LastWriteTimeUtc.Ticks, info.Length, maxPixels, showAlpha);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? Decode(string path, int maxPixels, bool showAlpha)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".vtf":
                    // VtfImage picks a mip instead of decoding 4K pixels for a tile; int.MaxValue asks
                    // for mip 0 when the caller wants full resolution.
                    var vtf = VtfImage.Decode(File.ReadAllBytes(path), maxPixels > 0 ? maxPixels : int.MaxValue);
                    return vtf is null
                        ? null
                        : Finish(BitmapSource.Create(vtf.Width, vtf.Height, 96, 96,
                            PixelFormats.Bgra32, null, vtf.Bgra, vtf.Width * 4), maxPixels, showAlpha);

                case ".dds" or ".tga":
                    return FromPfim(path, maxPixels, showAlpha);

                case ".psd" or ".psb":
                    return FromPsd(path, maxPixels, showAlpha);

                default:
                    return FromEncoded(File.ReadAllBytes(path), maxPixels, showAlpha);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// PSD, preferring whichever source suits the size being asked for. A tile takes the embedded
    /// JPEG when there is one - a few KB to decode against a full composite that can be 4K and
    /// RLE-packed - while the full-size window decodes the composite so it shows real pixels instead
    /// of a 160px preview blown up. Either way the other one is the fallback, because a given file
    /// may carry only one: the embedded preview depends on Photoshop's "Image Previews" preference,
    /// the composite on "Maximize Compatibility".
    /// </summary>
    private static ImageSource? FromPsd(string path, int maxPixels, bool showAlpha)
    {
        // Tiles ask the cheap source first, the full-size window the faithful one; each falls back to
        // the other, and a source that throws (a corrupt embedded JPEG, say) doesn't sink the one
        // that would have worked.
        //
        // The embedded preview is a JPEG, so it has no alpha channel at all - Photoshop already
        // flattened it against white. Showing alpha therefore means paying for the composite even on
        // a tile, or the toggle would appear to do nothing.
        bool preferThumbnail = maxPixels > 0 && !showAlpha;
        return preferThumbnail
            ? Try(Thumbnail) ?? Try(Composite)
            : Try(Composite) ?? Try(Thumbnail);

        ImageSource? Thumbnail() =>
            PsdImage.TryReadThumbnailJpeg(path) is { } jpeg ? FromEncoded(jpeg, maxPixels, showAlpha) : null;

        ImageSource? Composite() =>
            PsdImage.TryDecodeComposite(path) is { } image
                ? Finish(BitmapSource.Create(image.Width, image.Height, 96, 96,
                    PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), maxPixels, showAlpha)
                : null;

        static ImageSource? Try(Func<ImageSource?> source)
        {
            try
            {
                return source();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>DDS and TGA via Pfim. Its buffers are already in BGR(A) byte order, so they map onto a
    /// WPF pixel format directly with no channel shuffling.</summary>
    private static ImageSource? FromPfim(string path, int maxPixels, bool showAlpha)
    {
        using var image = Pfim.Pfimage.FromFile(path);
        PixelFormat? format = image.Format switch
        {
            Pfim.ImageFormat.Rgba32 => PixelFormats.Bgra32,
            Pfim.ImageFormat.Rgb24 => PixelFormats.Bgr24,
            Pfim.ImageFormat.Rgb8 => PixelFormats.Gray8,
            Pfim.ImageFormat.R5g5b5 or Pfim.ImageFormat.R5g5b5a1 => PixelFormats.Bgr555,
            Pfim.ImageFormat.R5g6b5 => PixelFormats.Bgr565,
            _ => null, // Rgba16 (4444) has no WPF equivalent - vanishingly rare, no preview.
        };
        if (format is not { } pixelFormat)
            return null;

        // image.Data holds mip 0 followed by any smaller mips; Width/Height/Stride describe mip 0 and
        // BitmapSource.Create only reads that far in.
        return Finish(
            BitmapSource.Create(image.Width, image.Height, 96, 96, pixelFormat, null, image.Data, image.Stride),
            maxPixels, showAlpha);
    }

    /// <summary>Anything with a WPF codec (png/jpg/bmp/gif/tif), plus the JPEG lifted out of a PSD.</summary>
    private static ImageSource FromEncoded(byte[] bytes, int maxPixels, bool showAlpha)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        // Decoding from bytes rather than a URI: WPF's bitmap cache is keyed by UriSource, so this
        // never hits it - and setting IgnoreImageCache here throws on the null key rather than
        // being a harmless no-op. Reading the file ourselves is what keeps previews current.
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        // Let the codec downscale while decoding - far cheaper than decoding a 4K source in full and
        // shrinking afterwards. Only one axis may be set, or the aspect ratio is forced; a metadata-only
        // pass says which axis is the long one. Setting it on the short axis would upscale instead.
        if (maxPixels > 0)
        {
            var (width, height) = Probe(bytes);
            if (Math.Max(width, height) > maxPixels)
            {
                if (width >= height)
                    bitmap.DecodePixelWidth = maxPixels;
                else
                    bitmap.DecodePixelHeight = maxPixels;
            }
        }

        bitmap.EndInit();
        bitmap.Freeze();

        // The codec already sized it, so only the alpha switch is left to apply.
        var flattened = Flatten(bitmap, showAlpha);
        flattened.Freeze();
        return flattened;
    }

    /// <summary>Source dimensions without decoding pixels; (0, 0) when the codec can't report them.</summary>
    private static (int Width, int Height) Probe(byte[] bytes)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes),
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Freezes a decoded bitmap and, when it overshoots <paramref name="maxPixels"/>, resamples
    /// it down so the cache holds a tile-sized copy rather than the full-resolution one.</summary>
    private static ImageSource Finish(BitmapSource source, int maxPixels, bool showAlpha)
    {
        source = Flatten(source, showAlpha);
        source.Freeze(); // TransformedBitmap requires a frozen source when built off the UI thread

        if (maxPixels > 0)
        {
            double scale = (double)maxPixels / Math.Max(source.PixelWidth, source.PixelHeight);
            if (scale < 1.0)
            {
                // WriteableBitmap materialises the scaled pixels, so the full-size original becomes
                // garbage instead of staying alive behind a TransformedBitmap.
                var scaled = new WriteableBitmap(
                    new TransformedBitmap(source, new ScaleTransform(scale, scale)));
                scaled.Freeze();
                return scaled;
            }
        }
        return source;
    }

    /// <summary>Applies the caller's alpha choice at the one point every decoder funnels through, so
    /// it means the same thing for a .psd as it does for a .vtf. Sources with no alpha to begin with
    /// are passed straight back.</summary>
    private static BitmapSource Flatten(BitmapSource source, bool showAlpha) =>
        showAlpha || source.Format.BitsPerPixel != 32 || source.Format == PixelFormats.Bgr32
            ? source
            : ForceOpaque(source);

    /// <summary>Rewrites every pixel's alpha to fully opaque, keeping the colour channels as the file
    /// stored them.</summary>
    public static BitmapSource ForceOpaque(BitmapSource source)
    {
        // Converting first means the copy below always sees straight (non-premultiplied) BGRA.
        var straight = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = straight.PixelWidth, height = straight.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var opaque = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        opaque.Freeze();
        return opaque;
    }
}
