using PulseWorkshop.Core.Models;

namespace PulseWorkshop.Core.Storage;

/// <summary>Persists <see cref="Template"/>s under <see cref="AppPaths.TemplatesDir"/>.</summary>
public sealed class TemplateStore
{
    private readonly JsonFileStore<Template> _store = new(AppPaths.TemplatesDir, t => t.Id);

    public IReadOnlyList<Template> GetAll() => _store.LoadAll();

    public Template Create(string name, uint appId)
    {
        var template = new Template { Id = Guid.NewGuid(), Name = name, AppId = appId };
        _store.Save(template);
        return template;
    }

    public void Save(Template template)
    {
        template.Modified = DateTimeOffset.Now;
        _store.Save(template);
    }

    public void Delete(Guid id) => _store.Delete(id);
}
