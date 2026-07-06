using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// Helpers for Garry's Mod addon metadata (<c>addon.json</c>) and the fixed catalog of addon types
/// and tags the Workshop whitelist allows. gmad's <c>create</c> reads an addon.json from the folder
/// being packed for the title, type and tags; without one it refuses to pack. Mirrors gmad / gmpublish
/// / Crowbar. Only applies to the gmad packer (Garry's Mod).
/// </summary>
public static class GModAddon
{
    /// <summary>The addon "type" choices (single-select). <c>Value</c> is the canonical lowercase
    /// token written to addon.json; <c>Label</c> is the display name.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Types = new[]
    {
        ("gamemode", "Gamemode"),
        ("map", "Map"),
        ("weapon", "Weapon"),
        ("vehicle", "Vehicle"),
        ("npc", "NPC"),
        ("entity", "Entity"),
        ("tool", "Tool"),
        ("effects", "Effects"),
        ("model", "Model"),
        ("servercontent", "ServerContent"),
    };

    /// <summary>The addon "tag" choices (choose up to <see cref="MaxTags"/>). <c>Value</c> is the
    /// canonical lowercase token; <c>Label</c> is the display name.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Tags = new[]
    {
        ("build", "Build"),
        ("cartoon", "Cartoon"),
        ("comic", "Comic"),
        ("fun", "Fun"),
        ("movie", "Movie"),
        ("realism", "Realism"),
        ("roleplay", "Roleplay"),
        ("scenic", "Scenic"),
        ("water", "Water"),
    };

    /// <summary>The Workshop whitelist allows at most this many tags per addon.</summary>
    public const int MaxTags = 2;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Writes (or updates) an <c>addon.json</c> in <paramref name="folder"/> for gmad, setting
    /// title/type/tags while preserving any other keys an existing file has (e.g. <c>ignore</c>). Title
    /// falls back to the folder name; type falls back to the first type. Returns the file path.</summary>
    public static string Write(string folder, string title, string type, IEnumerable<string> tags)
    {
        var path = Path.Combine(folder, "addon.json");

        JsonObject obj;
        try
        {
            obj = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            // Malformed existing file - replace it rather than fail the pack.
            obj = new JsonObject();
        }

        obj["title"] = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileName(folder.TrimEnd('\\', '/'))
            : title.Trim();
        obj["type"] = string.IsNullOrWhiteSpace(type) ? Types[0].Value : type;
        obj["tags"] = new JsonArray(tags.Take(MaxTags).Select(t => (JsonNode)t!).ToArray());

        File.WriteAllText(path, obj.ToJsonString(WriteOptions));
        return path;
    }
}
