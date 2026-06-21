using System.IO;
using SrcWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// A draft as shown in the Drafts list, with a resolved thumbnail: the draft's own local preview
/// image if it has one, otherwise the Steam preview of the published item it edits (if any).
/// </summary>
public sealed class DraftListItemViewModel
{
    public DraftListItemViewModel(Draft draft, string? fallbackPreviewUrl)
    {
        Draft = draft;

        var local = draft.Edit.PreviewImagePath;
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
            ThumbnailSource = local;
        else if (!string.IsNullOrWhiteSpace(fallbackPreviewUrl))
            ThumbnailSource = fallbackPreviewUrl;
    }

    public Draft Draft { get; }

    public string Name => Draft.Name;
    public DateTimeOffset Modified => Draft.Modified;

    /// <summary>Local path or remote URL for the thumbnail; null shows a placeholder.</summary>
    public string? ThumbnailSource { get; }

    public bool HasThumbnail => ThumbnailSource is not null;
}
