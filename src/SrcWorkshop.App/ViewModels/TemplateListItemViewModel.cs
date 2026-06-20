using System.IO;
using SrcWorkshop.Core.Models;

namespace SrcWorkshop.App.ViewModels;

/// <summary>
/// A template as shown in the Templates list, with a resolved thumbnail from its local preview
/// image (if one is set and still exists). Mirrors <see cref="DraftListItemViewModel"/>.
/// </summary>
public sealed class TemplateListItemViewModel
{
    public TemplateListItemViewModel(Template template)
    {
        Template = template;

        var local = template.PreviewImagePath;
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
            ThumbnailSource = local;
    }

    public Template Template { get; }

    public string Name => Template.Name;
    public string Description => Template.Description;

    /// <summary>Local path for the thumbnail; null shows a placeholder.</summary>
    public string? ThumbnailSource { get; }

    public bool HasThumbnail => ThumbnailSource is not null;
}
