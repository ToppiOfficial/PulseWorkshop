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
    private bool _isSelected;

    public DraftListItemViewModel(Draft draft, string? fallbackPreviewUrl)
    {
        Draft = draft;
        _fallbackPreviewUrl = fallbackPreviewUrl;
        _thumbnailSource = ResolveThumbnail();
    }

    public Draft Draft { get; }

    public string Name => Draft.Name;
    public DateTimeOffset Modified => Draft.Modified;

    /// <summary>Set when the draft edits an existing published Workshop item; null for a fresh draft.</summary>
    public bool IsPublished => Draft.Edit.PublishedFileId is > 0;

    /// <summary>Workshop item id label, e.g. "Workshop ID: 123456789". Empty when not published.</summary>
    public string WorkshopIdLabel => IsPublished ? $"Workshop ID: {Draft.Edit.PublishedFileId}" : string.Empty;

    /// <summary>Full path of the draft's content file (.vpk/.gma), or null when none is set.</summary>
    public string? ContentFilePath => string.IsNullOrWhiteSpace(Draft.Edit.ContentFile) ? null : Draft.Edit.ContentFile;

    public bool HasContentFile => ContentFilePath is not null;

    /// <summary>Just the file name + extension - shown pinned to the right so it stays legible even
    /// when the directory part is ellipsized (paths have no spaces to trim on).</summary>
    public string ContentFileName => HasContentFile ? Path.GetFileName(ContentFilePath!) : string.Empty;

    /// <summary>Everything up to and including the last separator (keeps the original slashes); this
    /// part gets character-ellipsized when the row is too narrow.</summary>
    public string ContentFileDirectory
    {
        get
        {
            if (!HasContentFile) return string.Empty;
            var full = ContentFilePath!;
            return full.Length >= ContentFileName.Length ? full[..^ContentFileName.Length] : string.Empty;
        }
    }

    /// <summary>Row-level tick used for bulk actions (publish / save / delete selected). Independent
    /// of which single draft is open in the editor, so several can be marked at once.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

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
        OnPropertyChanged(nameof(IsPublished));
        OnPropertyChanged(nameof(WorkshopIdLabel));
        OnPropertyChanged(nameof(ContentFilePath));
        OnPropertyChanged(nameof(HasContentFile));
        OnPropertyChanged(nameof(ContentFileName));
        OnPropertyChanged(nameof(ContentFileDirectory));
        ThumbnailSource = ResolveThumbnail();
    }
}
