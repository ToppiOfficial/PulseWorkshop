namespace SrcWorkshop.Core.Models;

/// <summary>
/// The editable fields of a Workshop item. Used as the payload for creating a new item or
/// updating an existing one, and as the persisted body of a <see cref="Draft"/> and
/// <see cref="Template"/>.
/// </summary>
public sealed class ItemEdit
{
    /// <summary>Null when creating a new item; set when editing an existing published item.</summary>
    public ulong? PublishedFileId { get; set; }

    public uint AppId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public WorkshopVisibility Visibility { get; set; } = WorkshopVisibility.Private;

    /// <summary>
    /// Local packed content file uploaded as the item's content (e.g. a .vpk for L4D2 or .gma for
    /// GMod). The Steam host stages it into a temp folder for ISteamUGC::SetItemContent.
    /// </summary>
    public string? ContentFile { get; set; }

    /// <summary>Local image file (JPG/PNG/GIF, &lt; 1 MB) used as the preview.</summary>
    public string? PreviewImagePath { get; set; }

    /// <summary>Change note recorded with the update on Steam.</summary>
    public string ChangeNote { get; set; } = string.Empty;

    public ItemEdit Clone() => new()
    {
        PublishedFileId = PublishedFileId,
        AppId = AppId,
        Title = Title,
        Description = Description,
        Tags = new List<string>(Tags),
        Visibility = Visibility,
        ContentFile = ContentFile,
        PreviewImagePath = PreviewImagePath,
        ChangeNote = ChangeNote,
    };
}
