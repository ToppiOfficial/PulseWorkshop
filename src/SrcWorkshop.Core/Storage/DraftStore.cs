using SrcWorkshop.Core.Models;

namespace SrcWorkshop.Core.Storage;

/// <summary>Persists <see cref="Draft"/>s under <see cref="AppPaths.DraftsDir"/>.</summary>
public sealed class DraftStore
{
    private readonly JsonFileStore<Draft> _store = new(AppPaths.DraftsDir, d => d.Id);

    public IReadOnlyList<Draft> GetAll() => _store.LoadAll();

    public Draft? Get(Guid id) => _store.LoadAll().FirstOrDefault(d => d.Id == id);

    /// <summary>Finds the single draft tracking edits to a given published item, if any.</summary>
    public Draft? FindByPublishedFileId(ulong publishedFileId) =>
        _store.LoadAll().FirstOrDefault(d => d.Edit.PublishedFileId == publishedFileId);

    public Draft Create(string name, ItemEdit edit)
    {
        var draft = new Draft { Id = Guid.NewGuid(), Name = name, Edit = edit };
        _store.Save(draft);
        return draft;
    }

    public void Save(Draft draft)
    {
        draft.Modified = DateTimeOffset.Now;
        _store.Save(draft);
    }

    public void Delete(Guid id) => _store.Delete(id);
}
