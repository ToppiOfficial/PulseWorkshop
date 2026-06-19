namespace SrcWorkshop.Core.Models;

/// <summary>
/// A reusable starting point (preset tags / description / visibility) for new items. Kept on a
/// separate list from <see cref="Draft"/>s. Applying a template seeds a fresh <see cref="ItemEdit"/>.
/// </summary>
public sealed class Template
{
    public required Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>Optional: a template can target a specific game, or be game-agnostic when 0.</summary>
    public uint AppId { get; set; }

    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public WorkshopVisibility DefaultVisibility { get; set; } = WorkshopVisibility.Private;

    /// <summary>Produce a new editable item seeded from this template.</summary>
    public ItemEdit ToNewEdit(uint appId) => new()
    {
        AppId = appId,
        Description = Description,
        Tags = new List<string>(Tags),
        Visibility = DefaultVisibility,
    };
}
