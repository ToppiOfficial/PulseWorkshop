using System.Text.Json;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// A path field that is either absolute, or relative to a defined <see cref="SteamLibrary"/>
/// (Crowbar-style "macro" base). When <see cref="LibraryId"/> is set, <see cref="Path"/> is the
/// remainder appended to that library's folder; otherwise <see cref="Path"/> is an absolute path.
/// </summary>
public sealed class PathRef
{
    public Guid? LibraryId { get; set; }

    public string Path { get; set; } = string.Empty;

    public PathRef Clone() => new() { LibraryId = LibraryId, Path = Path };
}

/// <summary>A Steam library folder, referenced by path fields as a selectable base.</summary>
public sealed class SteamLibrary
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// A user-defined game in the Game Setup panel (a friendlier take on Crowbar's Game Setup): a name,
/// engine, and the executable / tool paths. Purely a stored configuration - it does NOT drive the
/// Workshop connection (which is keyed by App ID via <c>KnownGames</c>).
/// </summary>
public sealed class GameSetupEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Engine { get; set; } = "Source";

    public PathRef GameInfoTxt { get; set; } = new();

    public PathRef ModelCompiler { get; set; } = new();

    public PathRef ModelViewer { get; set; } = new();

    public PathRef PackerTool { get; set; } = new();

    public GameSetupEntry Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        Engine = Engine,
        GameInfoTxt = GameInfoTxt.Clone(),
        ModelCompiler = ModelCompiler.Clone(),
        ModelViewer = ModelViewer.Clone(),
        PackerTool = PackerTool.Clone(),
    };
}

/// <summary>
/// The whole Game Setup document: the shared Steam library folders, the Steam executable, and the
/// list of game entries. Persisted as a single <c>gamesetup.json</c>; load and save are best-effort
/// and never throw (mirrors <see cref="UiSettings"/>). A missing or corrupt file seeds defaults.
/// </summary>
public sealed class GameSetupConfig
{
    public List<SteamLibrary> Libraries { get; set; } = new();

    public List<GameSetupEntry> Games { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static GameSetupConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.GameSetupFile))
            {
                var loaded = JsonSerializer.Deserialize<GameSetupConfig>(
                    File.ReadAllText(AppPaths.GameSetupFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Missing or corrupt - start empty.
        }

        // Start with no games: Game Setup is intentionally independent of the Workshop game list,
        // so the user builds their own roster (Add / Clone) rather than inheriting it.
        return new GameSetupConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.GameSetupFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist Game Setup must never disrupt the app.
        }
    }
}
