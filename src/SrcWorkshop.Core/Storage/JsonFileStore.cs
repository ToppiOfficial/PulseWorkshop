using System.Text.Json;

namespace SrcWorkshop.Core.Storage;

/// <summary>
/// A minimal "directory of JSON files" store: one file per entity, named <c>{id}.json</c>.
/// Used for both drafts and templates so each gets an independent, easily inspectable folder.
/// </summary>
public sealed class JsonFileStore<T>
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly Func<T, Guid> _idOf;

    public JsonFileStore(string dir, Func<T, Guid> idOf)
    {
        _dir = dir;
        _idOf = idOf;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, $"{id}.json");

    public IReadOnlyList<T> LoadAll()
    {
        var result = new List<T>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options);
                if (item is not null)
                    result.Add(item);
            }
            catch (JsonException)
            {
                // Skip corrupt files rather than failing the whole list load.
            }
        }
        return result;
    }

    public void Save(T item)
    {
        var path = PathFor(_idOf(item));
        File.WriteAllText(path, JsonSerializer.Serialize(item, Options));
    }

    public void Delete(Guid id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
            File.Delete(path);
    }
}
