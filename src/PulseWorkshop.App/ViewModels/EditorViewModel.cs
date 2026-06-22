using System.Collections.ObjectModel;
using System.IO;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Games;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// Editable view of a single item (new, draft, or existing published item): title, description,
/// categorized tags, content file, preview, visibility. Raises <see cref="Changed"/> whenever an
/// editable field changes so the host can persist a linked draft and toggle Save-edit.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private ItemEdit _original;
    private readonly List<string> _unknownTags;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string? _contentFile;
    private string? _previewImagePath;
    private string _changeNote = string.Empty;
    private WorkshopVisibility _visibility = WorkshopVisibility.Private;

    /// <param name="source">Values to populate the editor fields with (e.g. a resumed draft).</param>
    /// <param name="baseline">The state that "dirty" is measured against - the live published item
    /// when editing, so resuming a draft with real changes still enables Save edit. Defaults to
    /// <paramref name="source"/> for new items / templates.</param>
    /// <param name="templateId">When set, the editor is in template mode (edits a saved template).
    /// In template mode the Title field doubles as the template's name.</param>
    /// <param name="fallbackPreviewUrl">Steam-hosted preview of the published item being edited,
    /// shown in the preview box when no local preview image is chosen.</param>
    /// <param name="publishedContentInfo">Name/size of the already-published content file, shown in
    /// the content box (as info, not a pending upload) when no new local file is chosen.</param>
    public EditorViewModel(GameConfig game, ItemEdit? source = null, ItemEdit? baseline = null,
        Guid? templateId = null, string? templateName = null, string? fallbackPreviewUrl = null,
        string? publishedContentInfo = null)
    {
        Game = game;
        PublishedFileId = source?.PublishedFileId;
        TemplateId = templateId;
        FallbackPreviewUrl = fallbackPreviewUrl;
        PublishedContentInfo = publishedContentInfo;
        _original = (baseline ?? source ?? new ItemEdit { AppId = game.AppId }).Clone();

        var selectedTags = source?.Tags ?? new List<string>();

        TagGroups = new ObservableCollection<TagGroupViewModel>(
            game.TagCategories.Select(cat => new TagGroupViewModel(cat, selectedTags)));

        // Preserve any tags on the item that aren't in this game's configured categories (so they
        // aren't silently stripped on save and don't make the item look permanently dirty).
        var knownTags = new HashSet<string>(game.KnownTags, StringComparer.OrdinalIgnoreCase);
        _unknownTags = selectedTags.Where(t => !knownTags.Contains(t)).ToList();

        // Bubble any tag change up as an edit.
        foreach (var group in TagGroups)
            group.Changed += RaiseChanged;

        if (source is not null)
        {
            _title = source.Title;
            _description = source.Description;
            _contentFile = source.ContentFile;
            _previewImagePath = source.PreviewImagePath;
            _changeNote = source.ChangeNote;
            _visibility = source.Visibility;
        }

        // In template mode the Title field IS the template name.
        if (templateId is not null && !string.IsNullOrEmpty(templateName))
            _title = templateName;
    }

    /// <summary>Raised whenever any editable field changes (for draft persistence / button state).</summary>
    public event Action? Changed;

    public GameConfig Game { get; }

    public ulong? PublishedFileId { get; }

    public bool IsNew => PublishedFileId is null;

    /// <summary>True when editing an existing published item (button reads "Save edit").</summary>
    public bool IsEditingPublished => PublishedFileId is not null;

    /// <summary>Id of the template being edited, or null for normal item edits.</summary>
    public Guid? TemplateId { get; }

    /// <summary>Id of the draft this editor was opened from (for an unpublished draft), if any.</summary>
    public Guid? SourceDraftId { get; set; }

    /// <summary>True when editing a reusable template (no publish; Title is the template name).</summary>
    public bool IsTemplateMode => TemplateId is not null;

    public ObservableCollection<TagGroupViewModel> TagGroups { get; }

    public IReadOnlyList<WorkshopVisibility> VisibilityOptions { get; } =
        Enum.GetValues<WorkshopVisibility>();

    public string ContentFileExtension => Game.ContentFileExtension;

    public string Title
    {
        get => _title;
        set { if (SetField(ref _title, value)) RaiseChanged(); }
    }

    public string Description
    {
        get => _description;
        set { if (SetField(ref _description, value)) RaiseChanged(); }
    }

    public string? ContentFile
    {
        get => _contentFile;
        set
        {
            if (SetField(ref _contentFile, value))
            {
                OnPropertyChanged(nameof(HasContentFile));
                OnPropertyChanged(nameof(ContentFileSizeDisplay));
                OnPropertyChanged(nameof(ShowPublishedContentInfo));
                OnPropertyChanged(nameof(ShowContentPlaceholder));
                RaiseChanged();
            }
        }
    }

    public bool HasContentFile => !string.IsNullOrWhiteSpace(_contentFile);

    /// <summary>Size of the chosen content file, e.g. "307.9 MB" (empty if none/missing).</summary>
    public string ContentFileSizeDisplay => FileSizeOf(_contentFile);

    /// <summary>Name + size of the already-published content file, e.g. "mymod.vpk - 307.9 MB".
    /// Shown as info (this stays the content unless a new file is chosen), null if unknown.</summary>
    public string? PublishedContentInfo { get; }

    /// <summary>Show the published-file info line: there's a published file and no new upload chosen.</summary>
    public bool ShowPublishedContentInfo =>
        !HasContentFile && !string.IsNullOrWhiteSpace(PublishedContentInfo);

    /// <summary>Show the empty "drop a file" placeholder: no new upload and nothing published to describe.</summary>
    public bool ShowContentPlaceholder => !HasContentFile && !ShowPublishedContentInfo;

    public string? PreviewImagePath
    {
        get => _previewImagePath;
        set
        {
            if (SetField(ref _previewImagePath, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
                OnPropertyChanged(nameof(PreviewDisplaySource));
                OnPropertyChanged(nameof(HasPreviewDisplay));
                OnPropertyChanged(nameof(PreviewImageSizeDisplay));
                RaiseChanged();
            }
        }
    }

    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(_previewImagePath);

    /// <summary>Steam preview URL of the published item being edited, used when no local image is set.</summary>
    public string? FallbackPreviewUrl { get; }

    /// <summary>What the preview box actually shows: the chosen local image, else the item's Steam
    /// preview (so editing a published item still shows its existing preview).</summary>
    public string? PreviewDisplaySource =>
        HasPreviewImage ? _previewImagePath : FallbackPreviewUrl;

    /// <summary>True when there is something to show in the preview box (local image or Steam fallback).</summary>
    public bool HasPreviewDisplay => !string.IsNullOrWhiteSpace(PreviewDisplaySource);

    /// <summary>Size of the chosen preview image (empty if none/missing).</summary>
    public string PreviewImageSizeDisplay => FileSizeOf(_previewImagePath);

    private static string FileSizeOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        try
        {
            return WorkshopItem.FormatBytes((ulong)new FileInfo(path).Length);
        }
        catch
        {
            return string.Empty;
        }
    }

    public string ChangeNote
    {
        get => _changeNote;
        set { if (SetField(ref _changeNote, value)) RaiseChanged(); }
    }

    /// <summary>Label for the change-note box: a template stores a default note to pre-fill new items.</summary>
    public string ChangeNoteLabel => IsTemplateMode ? "Default change note" : "Change note";

    public WorkshopVisibility Visibility
    {
        get => _visibility;
        set { if (SetField(ref _visibility, value)) RaiseChanged(); }
    }

    /// <summary>True when the current edit differs from the original (drives "Save edit" enablement).</summary>
    public bool IsDirty => !EditEquals(ToItemEdit(), _original);

    /// <summary>
    /// True when a draft/template/new item has edits not yet saved locally - drives the editor's
    /// "unsaved changes" inner border. Excludes published-item edits (those use the "Save edit"
    /// button / Revert flow instead).
    /// </summary>
    public bool HasUnsavedChanges => !IsEditingPublished && IsDirty;

    /// <summary>
    /// Called after a successful publish/save-edit. Clears the chosen content file and preview image
    /// (so a follow-up minor metadata edit won't re-upload the same large content), and rebaselines
    /// "dirty" to the just-published state.
    /// </summary>
    public void MarkPublished()
    {
        // Clear via the backing fields (NOT the setters) so this does not raise Changed - that event
        // would re-create the linked draft we just deleted after a successful publish.
        _contentFile = null;
        _previewImagePath = null;
        OnPropertyChanged(nameof(ContentFile));
        OnPropertyChanged(nameof(HasContentFile));
        OnPropertyChanged(nameof(ContentFileSizeDisplay));
        OnPropertyChanged(nameof(ShowPublishedContentInfo));
        OnPropertyChanged(nameof(ShowContentPlaceholder));
        OnPropertyChanged(nameof(PreviewImagePath));
        OnPropertyChanged(nameof(HasPreviewImage));
        OnPropertyChanged(nameof(PreviewDisplaySource));
        OnPropertyChanged(nameof(HasPreviewDisplay));
        OnPropertyChanged(nameof(PreviewImageSizeDisplay));

        _original = ToItemEdit(); // current state is now the clean baseline
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>
    /// Called after the open draft/template is saved locally. Re-baselines "dirty" to the current
    /// state so the unsaved-changes indicator clears. Unlike <see cref="MarkPublished"/> this keeps
    /// the chosen content file and preview image (saving a draft doesn't upload them).
    /// </summary>
    public void MarkSaved()
    {
        _original = ToItemEdit();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>
    /// Requirements that must be met before a NEW item can be published: title, description,
    /// content file, and preview image. Returns the human-readable names of any that are missing
    /// (empty when satisfied, or always empty for published edits / templates).
    /// </summary>
    public IReadOnlyList<string> MissingRequirements
    {
        get
        {
            if (IsTemplateMode || IsEditingPublished)
                return Array.Empty<string>();

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(Title)) missing.Add("title");
            if (string.IsNullOrWhiteSpace(Description)) missing.Add("description");
            if (!HasContentFile || !File.Exists(_contentFile)) missing.Add("content file");
            if (!HasPreviewImage || !File.Exists(_previewImagePath)) missing.Add("preview image");
            return missing;
        }
    }

    public bool MeetsPublishRequirements => MissingRequirements.Count == 0;

    /// <summary>UI hint listing what's still needed, e.g. "Needs: description, preview image".</summary>
    public string MissingRequirementsHint =>
        MissingRequirements.Count == 0 ? string.Empty : "Needs: " + string.Join(", ", MissingRequirements);

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(MeetsPublishRequirements));
        OnPropertyChanged(nameof(MissingRequirementsHint));
        Changed?.Invoke();
    }

    private IEnumerable<string> SelectedTags =>
        TagGroups.SelectMany(g => g.SelectedTags).Concat(_unknownTags);

    /// <summary>Build the <see cref="ItemEdit"/> to send to the Steam host or save as a draft.</summary>
    public ItemEdit ToItemEdit() => new()
    {
        PublishedFileId = PublishedFileId,
        AppId = Game.AppId,
        Title = Title,
        Description = Description,
        Tags = SelectedTags.ToList(),
        Visibility = Visibility,
        ContentFile = ContentFile,
        PreviewImagePath = PreviewImagePath,
        ChangeNote = ChangeNote,
    };

    private static bool EditEquals(ItemEdit a, ItemEdit b) =>
        a.Title == b.Title &&
        a.Description == b.Description &&
        a.Visibility == b.Visibility &&
        (a.ContentFile ?? "") == (b.ContentFile ?? "") &&
        (a.PreviewImagePath ?? "") == (b.PreviewImagePath ?? "") &&
        TagsEqual(a.Tags, b.Tags);

    /// <summary>
    /// Compares tag sets case-insensitively. Steam returns tags with its own casing (e.g. "Addon"),
    /// while our editor emits the configured chip casing (e.g. "addon"); without this, a freshly
    /// opened item would always look dirty.
    /// </summary>
    private static bool TagsEqual(IEnumerable<string> a, IEnumerable<string> b)
    {
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }
}

/// <summary>
/// A labeled tag category. Renders and enforces selection per its <see cref="TagSelectionMode"/>:
/// multi (checkboxes), single (a "Type" dropdown), limited (checkboxes capped at a max), or
/// always-set (a fixed, non-editable tag like GMod's "Addon").
/// </summary>
public sealed class TagGroupViewModel : ObservableObject
{
    private readonly TagCategory _category;
    private string? _selectedType;

    public TagGroupViewModel(TagCategory category, IReadOnlyCollection<string> selectedTags)
    {
        _category = category;
        Name = category.Name;

        Tags = new ObservableCollection<TagChipViewModel>(
            category.Tags.Select(t => new TagChipViewModel(t)
            {
                IsSelected = selectedTags.Contains(t, StringComparer.OrdinalIgnoreCase),
            }));

        foreach (var chip in Tags)
            chip.PropertyChanged += OnChipChanged;

        if (Mode == TagSelectionMode.Single)
        {
            _selectedType = category.Tags
                .FirstOrDefault(t => selectedTags.Contains(t, StringComparer.OrdinalIgnoreCase));
        }

        UpdateLimitEnforcement();
    }

    public event Action? Changed;

    public string Name { get; }
    public TagSelectionMode Mode => _category.Mode;
    public ObservableCollection<TagChipViewModel> Tags { get; }

    // View-selecting flags for the XAML DataTemplate.
    public bool IsSingle => Mode == TagSelectionMode.Single;
    public bool IsCheckList => Mode is TagSelectionMode.Multi or TagSelectionMode.Limited;
    public bool IsAlwaysSet => Mode == TagSelectionMode.AlwaysSet;

    /// <summary>Hint shown under a limited group (e.g. "Choose up to two").</summary>
    public string? LimitHint =>
        Mode == TagSelectionMode.Limited ? $"Choose up to {_category.MaxSelectable}" : null;

    /// <summary>Options for a single-select (dropdown) category; includes a blank "none".</summary>
    public IReadOnlyList<string?> TypeOptions =>
        IsSingle ? new string?[] { null }.Concat(_category.Tags).ToList() : Array.Empty<string?>();

    public string? SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetField(ref _selectedType, value))
                Changed?.Invoke();
        }
    }

    /// <summary>The effective selected tag names for this category.</summary>
    public IEnumerable<string> SelectedTags => Mode switch
    {
        TagSelectionMode.Single => _selectedType is null ? Array.Empty<string>() : new[] { _selectedType },
        TagSelectionMode.AlwaysSet => _category.Tags,           // always on
        _ => Tags.Where(t => t.IsSelected).Select(t => t.Name), // multi / limited
    };

    private void OnChipChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateLimitEnforcement();
        Changed?.Invoke();
    }

    /// <summary>For limited groups, disable unchecked chips once the max is reached.</summary>
    private void UpdateLimitEnforcement()
    {
        if (Mode != TagSelectionMode.Limited)
            return;

        var atMax = Tags.Count(t => t.IsSelected) >= _category.MaxSelectable;
        foreach (var chip in Tags)
            chip.IsEnabled = chip.IsSelected || !atMax;
    }
}

public sealed class TagChipViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEnabled = true;

    public TagChipViewModel(string name) => Name = name;

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
}
