using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One tile in the Textures tab's match preview: a source file the selected group's pattern matches,
/// shown with its name, size and a low-res thumbnail. Read-only - it never converts or writes anything.
/// Formats WPF can't decode (.tga/.psd/.dds/...) fall back to the file's shell icon.
/// </summary>
public sealed class TextureMatchViewModel : ObservableObject
{
    /// <summary>The widest edge a preview is decoded at - big enough to stay sharp in the tile, small
    /// enough that a folder of hundreds of 4K source images doesn't blow up memory.</summary>
    private const int ThumbnailPixels = 256;

    private ImageSource? _thumbnail;
    private bool _thumbnailIsFileIcon;
    private bool _loadStarted;

    public TextureMatchViewModel(string fullPath, string root, bool isOutOfDate)
    {
        IsOutOfDate = isOutOfDate;
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, fullPath);
        }
        catch
        {
            relative = FileName;
        }
        RelativePath = relative;

        try
        {
            Bytes = new FileInfo(fullPath).Length;
        }
        catch
        {
            Bytes = -1;
        }

        Extension = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
        if (Extension.Length == 0)
            Extension = "?";
    }

    public string FullPath { get; }
    public string FileName { get; }

    /// <summary>True when this file's <c>.vtf</c> is missing or older than the source - the next run
    /// converts it. Drives the amber tile border; up-to-date files stay unmarked.</summary>
    public bool IsOutOfDate { get; }

    /// <summary>The tile's tooltip: where the file sits, plus whether the next run will convert it.</summary>
    public string Details => RelativePath + "\n" + (IsOutOfDate
        ? "Needs converting - the .vtf is missing or older than this source file."
        : "Up to date - the .vtf is newer than this source file.");

    /// <summary>The path relative to the source folder (the tile's tooltip - tells apart same-named
    /// files from different sub-folders in a recursive group).</summary>
    public string RelativePath { get; }

    /// <summary>File size in bytes, or -1 when it couldn't be read.</summary>
    public long Bytes { get; }

    public string SizeText => FormatSize(Bytes);

    /// <summary>Upper-case extension without the dot, shown on the tile when there is no thumbnail.</summary>
    public string Extension { get; }

    /// <summary>The decoded thumbnail, the file's shell icon, or null while it hasn't loaded yet.</summary>
    public ImageSource? Thumbnail => _thumbnail;

    /// <summary>True when <see cref="Thumbnail"/> is a shell icon rather than the image's own pixels -
    /// the view renders those small and centered instead of filling the tile.</summary>
    public bool ThumbnailIsFileIcon => _thumbnailIsFileIcon;

    /// <summary>True when the tile shows the image's own pixels - clicking it opens the full-size preview.</summary>
    public bool HasImagePreview => _thumbnail is not null && !_thumbnailIsFileIcon;

    /// <summary>Decodes the thumbnail off the UI thread (once per tile). The bitmap is frozen, so it can
    /// be built on the worker and handed straight to the binding.</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_loadStarted)
            return;
        _loadStarted = true;

        var (image, isIcon) = await Task.Run(() => Load(FullPath)).ConfigureAwait(true);
        _thumbnail = image;
        _thumbnailIsFileIcon = isIcon;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(ThumbnailIsFileIcon));
        OnPropertyChanged(nameof(HasImagePreview));
    }

    private static (ImageSource? Image, bool IsIcon) Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = ThumbnailPixels;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, false);
        }
        catch
        {
            // Not decodable by WPF (e.g. .tga/.psd/.dds) - ask the shell for Explorer's thumbnail.
        }

        try
        {
            if (ShellThumbnail.GetThumbnail(path, ThumbnailPixels) is { } shell)
                return (shell, false);
            return (ShellIcon.GetFileIcon(path), true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>Human-readable byte count ("?" for an unreadable file).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
            return "?";
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
}
