using System.IO;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// A draft as shown in the Drafts list, with a resolved thumbnail: the draft's own local preview
/// image if it has one, otherwise the Steam preview of the published item it edits (if any).
/// </summary>
public sealed class DraftListItemViewModel : ObservableObject
{
    private readonly string? _fallbackPreviewUrl;
    private string? _thumbnailSource;

    public DraftListItemViewModel(Draft draft, string? fallbackPreviewUrl)
    {
        Draft = draft;
        _fallbackPreviewUrl = fallbackPreviewUrl;
        _thumbnailSource = ResolveThumbnail();
    }

    public Draft Draft { get; }

    public string Name => Draft.Name;
    public DateTimeOffset Modified => Draft.Modified;

    /// <summary>Local path or remote URL for the thumbnail; null shows a placeholder.</summary>
    public string? ThumbnailSource
    {
        get => _thumbnailSource;
        private set
        {
            if (SetField(ref _thumbnailSource, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => ThumbnailSource is not null;

    private string? ResolveThumbnail()
    {
        var local = Draft.Edit.PreviewImagePath;
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
            return local;
        if (!string.IsNullOrWhiteSpace(_fallbackPreviewUrl))
            return _fallbackPreviewUrl;
        return null;
    }

    /// <summary>Re-reads the row's display fields after the underlying draft was auto-saved in place,
    /// so the label and thumbnail track edits live without rebuilding the list (which would drop selection).</summary>
    public void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(Name));
        ThumbnailSource = ResolveThumbnail();
    }
}
