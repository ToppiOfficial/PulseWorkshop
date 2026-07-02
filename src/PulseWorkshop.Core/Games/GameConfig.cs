namespace PulseWorkshop.Core.Games;

/// <summary>How a <see cref="TagCategory"/>'s tags may be selected.</summary>
public enum TagSelectionMode
{
    /// <summary>Any number selectable (L4D2 categories).</summary>
    Multi,

    /// <summary>Exactly one selectable, shown as a dropdown (GMod "Type").</summary>
    Single,

    /// <summary>Up to <see cref="TagCategory.MaxSelectable"/> selectable (GMod "choose up to two").</summary>
    Limited,

    /// <summary>Always on, not user-editable (GMod "Always set: Addon").</summary>
    AlwaysSet,
}

/// <summary>
/// A named group of selectable Workshop tags (e.g. "Survivors", "Type"). <see cref="Mode"/> and
/// <see cref="MaxSelectable"/> capture per-game rules (GMod's single-select Type, max-2 tags,
/// always-on Addon); L4D2 categories are plain <see cref="TagSelectionMode.Multi"/>.
/// </summary>
public sealed record TagCategory(
    string Name,
    IReadOnlyList<string> Tags,
    TagSelectionMode Mode = TagSelectionMode.Multi,
    int MaxSelectable = 0);

/// <summary>
/// Describes a Steam game whose Workshop this tool can manage. The list is config-driven so
/// more games can be added without code changes elsewhere; <see cref="KnownGames"/> ships with
/// Left 4 Dead 2 and Garry's Mod configured.
/// </summary>
public sealed record GameConfig
{
    public required uint AppId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Developer-defined tags grouped into categories (rendered as labeled columns in the editor,
    /// like Crowbar). Free multi-select across categories, matching the real Steam Workshop.
    /// </summary>
    public required IReadOnlyList<TagCategory> TagCategories { get; init; }

    /// <summary>The Workshop content file type for this game (a single packed file).</summary>
    public required string ContentFileExtension { get; init; } // e.g. ".vpk", ".gma"

    /// <summary>
    /// True when the game's Workshop uses the modern ISteamUGC upload path (CreateItem +
    /// SubmitItemUpdate); false for the legacy Steam Cloud (ISteamRemoteStorage) Workshop.
    /// GMod is UGC (mirrors Crowbar's UsesSteamUGC and gmpublish); L4D2 has no upload depot and
    /// must stay legacy. Routing GMod through the legacy path produces items subscribers cannot
    /// download.
    /// </summary>
    public bool UsesUgcUpload { get; init; }

    /// <summary>Flat list of every known tag across all categories.</summary>
    public IReadOnlyList<string> KnownTags =>
        TagCategories.SelectMany(c => c.Tags).ToList();

    public override string ToString() => $"{DisplayName} ({AppId})";
}

/// <summary>
/// Built-in catalog of supported games. Add an entry here to support another Workshop.
/// </summary>
public static class KnownGames
{
    public const uint LeftForDead2AppId = 550;
    public const uint GarrysModAppId = 4000;

    /// <summary>Left 4 Dead 2 Workshop tags, grouped to mirror the L4D2 Workshop upload categories.</summary>
    public static readonly GameConfig LeftForDead2 = new()
    {
        AppId = LeftForDead2AppId,
        DisplayName = "Left 4 Dead 2",
        ContentFileExtension = ".vpk",
        TagCategories = new[]
        {
            new TagCategory("Survivors", new[]
            {
                "Survivors", "Bill", "Francis", "Louis", "Zoey", "Coach", "Ellis", "Nick", "Rochelle",
            }),
            new TagCategory("Infected", new[]
            {
                "Common Infected", "Special Infected", "Boomer", "Charger", "Hunter", "Jockey",
                "Smoker", "Spitter", "Tank", "Witch",
            }),
            new TagCategory("Game Content", new[]
            {
                "Campaigns", "Miscellaneous", "Models", "Scripts", "Sounds", "Textures", "UI",
            }),
            new TagCategory("Game Modes", new[]
            {
                "Co-op", "Mutations", "Realism", "Realism Versus", "Scavenge", "Single Player",
                "Survival", "Versus",
            }),
            new TagCategory("Weapons", new[]
            {
                "Weapons", "Grenade Launcher", "M60", "Melee", "Pistol", "Rifle", "Shotgun",
                "SMG", "Sniper", "Throwable",
            }),
            new TagCategory("Items", new[]
            {
                "Items", "Adrenaline", "Defibrillator", "Medkit", "Other", "Pills",
            }),
        },
    };

    /// <summary>
    /// Garry's Mod Workshop tagging: a single-select "Type", a "choose up to two" tag group, and an
    /// always-on "Addon" - mirroring the GMod publisher.
    /// </summary>
    public static readonly GameConfig GarrysMod = new()
    {
        AppId = GarrysModAppId,
        DisplayName = "Garry's Mod",
        ContentFileExtension = ".gma",
        UsesUgcUpload = true,
        TagCategories = new[]
        {
            new TagCategory("Type", new[]
            {
                "Effects", "Game Mode", "Map", "Model", "NPC", "Server Content", "Tool",
                "Vehicle", "Weapon",
            }, TagSelectionMode.Single),
            new TagCategory("Tags", new[]
            {
                "Build", "Cartoon", "Comic", "Fun", "Movie", "Realism", "Roleplay", "Scenic", "Water",
            }, TagSelectionMode.Limited, MaxSelectable: 2),
            new TagCategory("Always set", new[] { "Addon" }, TagSelectionMode.AlwaysSet),
        },
    };

    public static readonly IReadOnlyList<GameConfig> All = new[] { LeftForDead2, GarrysMod };

    public static GameConfig? FindByAppId(uint appId) =>
        All.FirstOrDefault(g => g.AppId == appId);
}
