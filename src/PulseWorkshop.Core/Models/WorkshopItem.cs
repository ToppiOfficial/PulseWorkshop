namespace PulseWorkshop.Core.Models;

/// <summary>
/// A published Workshop item owned by the logged-in user, as returned by an ISteamUGC query.
/// </summary>
public sealed record WorkshopItem
{
    /// <summary>The PublishedFileId — the Workshop item's unique Steam id.</summary>
    public required ulong PublishedFileId { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public WorkshopVisibility Visibility { get; init; } = WorkshopVisibility.Private;

    /// <summary>Steam-hosted preview image URL (not downloaded eagerly; bound lazily in the UI).</summary>
    public string? PreviewUrl { get; init; }

    public DateTimeOffset? Updated { get; init; }

    /// <summary>When the item was first published.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>Total size of the item's files in bytes (0 if unknown).</summary>
    public ulong FileSizeBytes { get; init; }

    /// <summary>Human-readable file size, e.g. "307.9 MB".</summary>
    public string FileSizeDisplay => FormatBytes(FileSizeBytes);

    /// <summary>Cloud filename of the published primary content file (e.g. "mymod.vpk"), if known.</summary>
    public string? ContentFileName { get; init; }

    /// <summary>Public Workshop page, handy for "open in browser" / "open in Steam".</summary>
    public string WorkshopUrl =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={PublishedFileId}";

    /// <summary>Formats a byte count as a compact size string (KB/MB/GB).</summary>
    public static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
            return "-";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
