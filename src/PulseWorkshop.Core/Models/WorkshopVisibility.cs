namespace PulseWorkshop.Core.Models;

/// <summary>
/// Mirrors Steam's <c>ERemoteStoragePublishedFileVisibility</c>. New items default to
/// <see cref="Private"/> so nothing is published publicly by accident.
/// </summary>
public enum WorkshopVisibility
{
    Public = 0,
    FriendsOnly = 1,
    Private = 2,
    Unlisted = 3,
}
