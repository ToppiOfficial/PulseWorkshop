namespace PulseWorkshop.Core.Models;

/// <summary>
/// A locally saved, not-yet-published (or in-progress) item edit. Drafts are stored separately
/// from <see cref="Template"/>s and shown on their own list — unlike Crowbar, which mixes them.
/// </summary>
public sealed class Draft
{
    public required Guid Id { get; init; }

    public required string Name { get; set; }

    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    public required ItemEdit Edit { get; set; }
}
