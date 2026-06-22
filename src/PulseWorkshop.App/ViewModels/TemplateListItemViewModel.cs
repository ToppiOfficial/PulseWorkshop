using System.IO;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// A template as shown in the Templates list, with a resolved thumbnail from its local preview
/// image (if one is set and still exists). Mirrors <see cref="DraftListItemViewModel"/>.
/// </summary>
public sealed class TemplateListItemViewModel : ObservableObject
{
    private string? _thumbnailSource;

    public TemplateListItemViewModel(Template template)
    {
        Template = template;
        _thumbnailSource = ResolveThumbnail();
    }

    public Template Template { get; }

    public string Name => Template.Name;
    public string Description => Template.Description;
    public DateTimeOffset Modified => Template.Modified;

    /// <summary>Description capped to the first 3 lines (with a trailing "..." when there's more) so a
    /// long multi-line description doesn't blow up the row height. Each line still ellipsizes if wide.</summary>
    public string DescriptionPreview
    {
        get
        {
            var desc = Template.Description;
            if (string.IsNullOrWhiteSpace(desc))
                return string.Empty;

            var lines = desc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return lines.Length <= 3 ? desc : string.Join("\n", lines.Take(3)) + " ...";
        }
    }

    /// <summary>Local path for the thumbnail; null shows a placeholder.</summary>
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
        var local = Template.PreviewImagePath;
        return !string.IsNullOrWhiteSpace(local) && File.Exists(local) ? local : null;
    }

    /// <summary>Re-reads the display fields after the underlying template was auto-saved in place, so
    /// the list row tracks edits live without rebuilding the list (which would drop selection).</summary>
    public void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DescriptionPreview));
        OnPropertyChanged(nameof(Modified));
        ThumbnailSource = ResolveThumbnail();
    }
}
