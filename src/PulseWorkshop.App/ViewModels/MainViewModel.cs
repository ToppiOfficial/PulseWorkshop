using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Games;
using PulseWorkshop.Core.Ipc;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// Top-level view model: game picker, connection status, and the three separate lists
/// (Published / Drafts / Templates), plus the active editor.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WorkshopService _service;
    private readonly DraftStore _drafts = new();
    private readonly TemplateStore _templates = new();

    // Per-list text filters (plain substring by default, regex optional).
    private readonly ItemFilter _publishedFilter = new();
    private readonly ItemFilter _draftsFilter = new();
    private readonly ItemFilter _templatesFilter = new();

    // Debounce timers so the lists re-filter only after typing pauses (or on Enter, see
    // ApplySearchNow), instead of on every keystroke.
    private readonly DispatcherTimer _publishedSearchTimer;
    private readonly DispatcherTimer _draftsSearchTimer;
    private readonly DispatcherTimer _templatesSearchTimer;

    private GameConfig _selectedGame = KnownGames.LeftForDead2;
    private string _statusMessage = "Not connected.";
    private bool _isBusy;
    private EditorViewModel? _editor;

    // Logged-in Steam user, shown on the far right of the top bar once connected.
    private string? _personaName;
    private string? _steamIdDisplay;
    private string? _avatarUrl;

    public MainViewModel()
    {
        AppPaths.EnsureCreated();
        _service = new WorkshopService(HostLocator.ResolveHostExePath());

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsBusy);
        RefreshPublishedCommand = new AsyncRelayCommand(LoadAllPublishedAsync, () => !IsBusy);
        NewItemCommand = new RelayCommand(NewItem, () => !IsBusy && IsConnected);
        SaveDraftCommand = new RelayCommand(SaveDraft, () => Editor is not null);
        RevertCommand = new RelayCommand(Revert, () => CanRevert);
        PublishCommand = new AsyncRelayCommand(PublishAsync, CanPublish);
        ToggleConsoleCommand = new RelayCommand(() => IsConsoleVisible = !IsConsoleVisible);
        ClearConsoleCommand = new RelayCommand(ClearConsole);

        // Stream the Steam host's log + upload progress into the live console panel.
        _service.HostOutput += OnHostOutput;

        // Live-filtered views over each list.
        PublishedView = CollectionViewSource.GetDefaultView(PublishedItems);
        PublishedView.Filter = o => PublishedMatches((WorkshopItem)o);
        DraftsView = CollectionViewSource.GetDefaultView(Drafts);
        DraftsView.Filter = o => DraftMatches(((DraftListItemViewModel)o).Draft);
        TemplatesView = CollectionViewSource.GetDefaultView(Templates);
        TemplatesView.Filter = o => TemplateMatches(((TemplateListItemViewModel)o).Template);

        _publishedSearchTimer = CreateSearchTimer(PublishedView);
        _draftsSearchTimer = CreateSearchTimer(DraftsView);
        _templatesSearchTimer = CreateSearchTimer(TemplatesView);

        // Default the Published list to "published date, newest first".
        _selectedPublishedSort = PublishedSortOptions[0];
        ApplyPublishedSort();

        // Drafts/Templates are populated only after a successful Connect (see LoadLocalLists), so the
        // lists start empty until the user connects.
    }

    public IReadOnlyList<GameConfig> Games { get; } = KnownGames.All;

    public ObservableCollection<WorkshopItem> PublishedItems { get; } = new();
    public ObservableCollection<DraftListItemViewModel> Drafts { get; } = new();
    public ObservableCollection<TemplateListItemViewModel> Templates { get; } = new();

    public ICollectionView PublishedView { get; }
    public ICollectionView DraftsView { get; }
    public ICollectionView TemplatesView { get; }

    // --- Published list sorting (Published tab only) ---------------------------------------

    /// <summary>The sort options offered for the Published list (label + field + natural direction).</summary>
    public IReadOnlyList<PublishedSortOption> PublishedSortOptions { get; } = new[]
    {
        new PublishedSortOption("Published date", nameof(WorkshopItem.Created), ListSortDirection.Descending),
        new PublishedSortOption("Last updated", nameof(WorkshopItem.Updated), ListSortDirection.Descending),
        new PublishedSortOption("Title (A-Z)", nameof(WorkshopItem.Title), ListSortDirection.Ascending),
        new PublishedSortOption("Steam ID", nameof(WorkshopItem.PublishedFileId), ListSortDirection.Ascending),
    };

    private PublishedSortOption _selectedPublishedSort = null!; // set in the constructor
    private bool _publishedSortReversed;

    /// <summary>Selected Published sort field. Default is published date (newest first).</summary>
    public PublishedSortOption SelectedPublishedSort
    {
        get => _selectedPublishedSort;
        set { if (SetField(ref _selectedPublishedSort, value)) ApplyPublishedSort(); }
    }

    /// <summary>Reverses the chosen sort's natural direction.</summary>
    public bool PublishedSortReversed
    {
        get => _publishedSortReversed;
        set { if (SetField(ref _publishedSortReversed, value)) ApplyPublishedSort(); }
    }

    private void ApplyPublishedSort()
    {
        // Each option has a natural direction (e.g. dates newest-first, titles A-Z); the reverse
        // toggle flips it.
        var dir = _selectedPublishedSort.Direction;
        if (_publishedSortReversed)
            dir = dir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        using (PublishedView.DeferRefresh())
        {
            PublishedView.SortDescriptions.Clear();
            PublishedView.SortDescriptions.Add(new SortDescription(_selectedPublishedSort.Property, dir));
        }
    }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand RefreshPublishedCommand { get; }
    public RelayCommand NewItemCommand { get; }
    public RelayCommand SaveDraftCommand { get; }
    public RelayCommand RevertCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public RelayCommand ToggleConsoleCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }

    public GameConfig SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                // Switching games invalidates the loaded list and the open editor.
                Editor = null;
                PublishedItems.Clear();
                OnPropertyChanged(nameof(IsConnected)); // host is for the previous game now
                NewItemCommand.RaiseCanExecuteChanged();
                LoadLocalLists(); // clears Drafts/Templates (not connected to the new game yet)
                StatusMessage = $"Selected {value.DisplayName}. Click Connect to load items.";
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    // --- Live console (streams the Steam host's log + upload progress) ----------------------

    private const int MaxConsoleLines = 1000;
    private bool _isConsoleVisible;

    /// <summary>Lines shown in the bottom console drawer, oldest first.</summary>
    public ObservableCollection<string> ConsoleLines { get; } = new();

    /// <summary>Whether the console drawer is expanded.</summary>
    public bool IsConsoleVisible
    {
        get => _isConsoleVisible;
        set => SetField(ref _isConsoleVisible, value);
    }

    /// <summary>Appends a line authored by the App itself (host lines arrive via <see cref="OnHostOutput"/>).</summary>
    private void ConsoleLog(string message) => AppendConsoleLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private void ClearConsole() => ConsoleLines.Clear();

    // Host output fires on a background (thread-pool) thread; marshal to the UI thread before
    // touching the ObservableCollection bound to the console list.
    private void OnHostOutput(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            AppendConsoleLine(line);
        else
            dispatcher.BeginInvoke(() => AppendConsoleLine(line));
    }

    private void AppendConsoleLine(string line)
    {
        ConsoleLines.Add(line);
        while (ConsoleLines.Count > MaxConsoleLines)
            ConsoleLines.RemoveAt(0);
    }

    // --- Logged-in Steam profile (top-right) -----------------------------------------------

    /// <summary>Steam persona (display) name, set after a successful connect.</summary>
    public string? PersonaName
    {
        get => _personaName;
        private set { if (SetField(ref _personaName, value)) OnPropertyChanged(nameof(HasProfile)); }
    }

    /// <summary>The user's SteamID64 as text, shown under the persona name.</summary>
    public string? SteamIdDisplay
    {
        get => _steamIdDisplay;
        private set => SetField(ref _steamIdDisplay, value);
    }

    /// <summary>Avatar image URL resolved from the Steam Community profile (null if unavailable).</summary>
    public string? AvatarUrl
    {
        get => _avatarUrl;
        private set => SetField(ref _avatarUrl, value);
    }

    /// <summary>True once we have a connected user to show in the top-right.</summary>
    public bool HasProfile => !string.IsNullOrEmpty(_personaName);

    /// <summary>True when a Steam host for the selected game is live. Opening any editor (new item,
    /// draft, template, published item) requires this so the published item, its preview, and the
    /// "Save edit" baseline can all resolve. Drafts/templates are visible offline but not editable.</summary>
    public bool IsConnected => _service.ActiveAppId == SelectedGame.AppId;

    /// <summary>
    /// Populate the top-right profile from a successful ping, then resolve the avatar in the
    /// background from the public Steam Community profile (best-effort; name/id show regardless).
    /// </summary>
    private void UpdateProfile(PingResult ping)
    {
        PersonaName = ping.PersonaName;
        SteamIdDisplay = ping.SteamId != 0 ? ping.SteamId.ToString() : null;
        AvatarUrl = null;

        if (ping.SteamId != 0)
            _ = ResolveAvatarAsync(ping.SteamId);
    }

    /// <summary>
    /// Fetches the avatar image URL from the public Steam Community profile XML (no API key needed).
    /// Quietly leaves the avatar empty on any failure (offline, private profile, etc.).
    /// </summary>
    private async Task ResolveAvatarAsync(ulong steamId)
    {
        try
        {
            var url = await SteamProfile.GetAvatarUrlAsync(steamId);
            if (!string.IsNullOrEmpty(url))
                Application.Current.Dispatcher.Invoke(() => AvatarUrl = url);
        }
        catch
        {
            // Best-effort: a missing avatar is not an error worth surfacing.
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                RefreshPublishedCommand.RaiseCanExecuteChanged();
                NewItemCommand.RaiseCanExecuteChanged();
                RevertCommand.RaiseCanExecuteChanged();
                PublishCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanRevert));
            }
        }
    }

    public EditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (_editor is not null)
                _editor.Changed -= OnEditorChanged;

            if (SetField(ref _editor, value))
            {
                if (_editor is not null)
                    _editor.Changed += OnEditorChanged;

                SaveDraftCommand.RaiseCanExecuteChanged();
                PublishCommand.RaiseCanExecuteChanged();
                RevertCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(SaveActionText));
                OnPropertyChanged(nameof(ShowItemActions));
                OnPropertyChanged(nameof(CanSaveAsTemplate));
                OnPropertyChanged(nameof(CanRevert));
                OnPropertyChanged(nameof(PublishRequirementsHint));
            }
        }
    }

    /// <summary>Label for the main action button: "Save edit" when editing a published item,
    /// otherwise "Publish".</summary>
    public string PrimaryActionText =>
        Editor?.IsEditingPublished == true ? "Save edit" : "Publish";

    /// <summary>Label for the secondary save button: "Save template" in template mode else "Save draft".</summary>
    public string SaveActionText =>
        Editor?.IsTemplateMode == true ? "Save template" : "Save draft";

    /// <summary>Publish/Revert only apply to real items, not templates.</summary>
    public bool ShowItemActions => Editor is not null && !Editor.IsTemplateMode;

    /// <summary>"Save as template" only makes sense for a real item edit (not while editing a template).</summary>
    public bool CanSaveAsTemplate => Editor is not null && !Editor.IsTemplateMode;

    private void OnEditorChanged()
    {
        // Persist edits to a published item into its linked draft so progress survives app exit.
        if (Editor is { IsEditingPublished: true, PublishedFileId: { } pubId })
            UpsertLinkedDraft(Editor, pubId);

        PublishCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(PublishRequirementsHint));
    }

    /// <summary>Create or update the one draft that tracks edits to a published item.</summary>
    private void UpsertLinkedDraft(EditorViewModel editor, ulong publishedFileId)
    {
        var existing = _drafts.FindByPublishedFileId(publishedFileId);
        var name = $"Editing: {(string.IsNullOrWhiteSpace(editor.Title) ? publishedFileId.ToString() : editor.Title)}";

        if (existing is null)
        {
            _drafts.Create(name, editor.ToItemEdit());
        }
        else
        {
            existing.Name = name;
            existing.Edit = editor.ToItemEdit();
            _drafts.Save(existing);
        }
        LoadLocalLists();
    }

    // --- Search / filter -------------------------------------------------------------------

    // The text setters only restart the debounce timer; the actual re-filter happens when the
    // timer ticks (typing pause) or when the view explicitly flushes on Enter (Apply*SearchNow).
    // The regex toggle re-filters immediately since it's a single deliberate click.

    public string PublishedSearch
    {
        get => _publishedFilter.Query;
        set { _publishedFilter.Query = value; OnPropertyChanged(); DebounceSearch(_publishedSearchTimer); }
    }

    public bool PublishedRegex
    {
        get => _publishedFilter.UseRegex;
        set { _publishedFilter.UseRegex = value; OnPropertyChanged(); ApplyPublishedSearchNow(); }
    }

    public string DraftsSearch
    {
        get => _draftsFilter.Query;
        set { _draftsFilter.Query = value; OnPropertyChanged(); DebounceSearch(_draftsSearchTimer); }
    }

    public bool DraftsRegex
    {
        get => _draftsFilter.UseRegex;
        set { _draftsFilter.UseRegex = value; OnPropertyChanged(); ApplyDraftsSearchNow(); }
    }

    public string TemplatesSearch
    {
        get => _templatesFilter.Query;
        set { _templatesFilter.Query = value; OnPropertyChanged(); DebounceSearch(_templatesSearchTimer); }
    }

    public bool TemplatesRegex
    {
        get => _templatesFilter.UseRegex;
        set { _templatesFilter.UseRegex = value; OnPropertyChanged(); ApplyTemplatesSearchNow(); }
    }

    // Flush a pending search immediately (called when the user presses Enter in a search box).
    public void ApplyPublishedSearchNow() => FlushSearch(_publishedSearchTimer, PublishedView);
    public void ApplyDraftsSearchNow() => FlushSearch(_draftsSearchTimer, DraftsView);
    public void ApplyTemplatesSearchNow() => FlushSearch(_templatesSearchTimer, TemplatesView);

    private static DispatcherTimer CreateSearchTimer(ICollectionView view)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); view.Refresh(); };
        return timer;
    }

    private static void DebounceSearch(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private static void FlushSearch(DispatcherTimer timer, ICollectionView view)
    {
        timer.Stop();
        view.Refresh();
    }

    private bool PublishedMatches(WorkshopItem i) =>
        _publishedFilter.Matches(
            i.Title,
            i.Description,
            string.Join(' ', i.Tags),
            i.PublishedFileId.ToString());

    private bool DraftMatches(Draft d) =>
        _draftsFilter.Matches(
            d.Name,
            d.Edit.Title,
            d.Edit.Description,
            string.Join(' ', d.Edit.Tags),
            d.Edit.PublishedFileId?.ToString());

    private bool TemplateMatches(Template t) =>
        // Strict per-game: only show templates for the currently selected game.
        t.AppId == SelectedGame.AppId &&
        _templatesFilter.Matches(
            t.Name,
            t.Description,
            string.Join(' ', t.Tags));

    private void LoadLocalLists()
    {
        Drafts.Clear();
        Templates.Clear();

        // Drafts/templates are only browsable once connected: a published-linked draft needs the
        // loaded Published items to resolve its live values and Steam preview, and editing anything
        // requires a live host. Show nothing until Connect succeeds.
        if (!IsConnected)
            return;

        // Drafts and templates are strictly per-game: only surface those belonging to the selected
        // game so another game's drafts/templates never leak into this game's lists.
        foreach (var d in _drafts.GetAll()
                     .Where(d => d.Edit.AppId == SelectedGame.AppId)
                     .OrderByDescending(d => d.Modified))
        {
            // For a published-linked draft with no local image, fall back to the item's Steam preview.
            string? fallback = d.Edit.PublishedFileId is { } id
                ? PublishedItems.FirstOrDefault(i => i.PublishedFileId == id)?.PreviewUrl
                : null;
            Drafts.Add(new DraftListItemViewModel(d, fallback));
        }

        foreach (var t in _templates.GetAll()
                     .Where(t => t.AppId == SelectedGame.AppId)
                     .OrderBy(t => t.Name))
            Templates.Add(new TemplateListItemViewModel(t));
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = $"Connecting to Steam for {SelectedGame.DisplayName}...";
            await _service.SelectGameAsync(SelectedGame.AppId);

            var ping = await _service.PingAsync();
            if (!ping.SteamRunning)
            {
                StatusMessage = "Steam is not running, or you do not own this game on the logged-in account.";
                return;
            }

            UpdateProfile(ping);
            OnPropertyChanged(nameof(IsConnected));
            NewItemCommand.RaiseCanExecuteChanged();
            StatusMessage = $"Connected as {ping.PersonaName} - {SelectedGame.DisplayName}. Loading items...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
            IsBusy = false;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Load the full catalog by default so search covers everything. Edit/publish stay disabled
        // (via IsBusy) until this completes.
        await LoadAllPublishedAsync();

        // Now that Published items are loaded, populate Drafts/Templates (their preview fallbacks
        // resolve against the published list).
        LoadLocalLists();
    }

    private async Task RefreshPublishedAsync()
    {
        if (_service.ActiveAppId is null)
        {
            StatusMessage = "Connect to a game first.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _service.GetPublishedAsync(page: 1);
            PublishedItems.Clear();
            foreach (var item in result.Items)
                PublishedItems.Add(item);
            StatusMessage = result.TotalResults > result.Items.Count
                ? $"Loaded {result.Items.Count} of {result.TotalResults}. Click Refresh to load the full catalog."
                : $"Loaded {result.Items.Count} published item(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load items: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Pages through Steam to load every published item (so search covers the full catalog, not
    /// just the first 50). Sequential and on-demand to stay gentle on Steam.
    /// </summary>
    private async Task LoadAllPublishedAsync()
    {
        if (_service.ActiveAppId is null)
        {
            StatusMessage = "Connect to a game first.";
            return;
        }

        IsBusy = true;
        try
        {
            var all = await _service.GetAllPublishedAsync(
                onProgress: (loaded, total) =>
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusMessage = $"Loading all published items... {loaded} of {total}"));

            PublishedItems.Clear();
            foreach (var item in all)
                PublishedItems.Add(item);
            StatusMessage = $"Loaded all {PublishedItems.Count} published item(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load all items: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// New items: enabled only when all publish requirements (title/description/content/preview)
    /// are met. Published edits: enabled only when something changed.
    /// </summary>
    private bool CanPublish()
    {
        if (Editor is null || IsBusy)
            return false;
        return Editor.IsEditingPublished ? Editor.IsDirty : Editor.MeetsPublishRequirements;
    }

    /// <summary>"Needs: ..." hint shown by the Publish button when a new item is incomplete.</summary>
    public string PublishRequirementsHint => Editor?.MissingRequirementsHint ?? string.Empty;

    /// <summary>Revert is only meaningful while editing a published item that has a linked draft.</summary>
    public bool CanRevert
    {
        get
        {
            if (Editor is null || IsBusy || !Editor.IsEditingPublished || Editor.PublishedFileId is not { } id)
                return false;
            return _drafts.FindByPublishedFileId(id) is not null;
        }
    }

    private void Revert()
    {
        if (Editor?.PublishedFileId is not { } id)
            return;

        // Drop the in-progress draft and reset the editor to the item's live published values.
        var linked = _drafts.FindByPublishedFileId(id);
        if (linked is not null)
            _drafts.Delete(linked.Id);

        var live = PublishedItems.FirstOrDefault(i => i.PublishedFileId == id);
        if (live is not null)
            Editor = BuildPublishedEditor(live, ignoreLinkedDraft: true);

        LoadLocalLists();
        OnPropertyChanged(nameof(CanRevert));
        RevertCommand.RaiseCanExecuteChanged();
        StatusMessage = "Reverted edits and removed the draft.";
    }

    /// <summary>Status shown when the user tries to open an editor before connecting.</summary>
    private const string ConnectFirstMessage = "Connect to a game first to create or edit items.";

    /// <summary>Returns false (and sets a status message) when no live host is connected.</summary>
    private bool RequireConnected()
    {
        if (IsConnected)
            return true;
        StatusMessage = ConnectFirstMessage;
        return false;
    }

    private void NewItem()
    {
        if (!RequireConnected())
            return;

        // A new item immediately becomes a draft so the work is tracked from the start; the editor
        // is bound to that draft (subsequent Save draft updates it) and the UI jumps to Drafts.
        Editor = new EditorViewModel(SelectedGame);
        var draft = _drafts.Create("Untitled draft", Editor.ToItemEdit());
        Editor.SourceDraftId = draft.Id;
        LoadLocalLists();
        NavigateToDrafts?.Invoke();
    }

    public void EditPublished(WorkshopItem item)
    {
        if (!RequireConnected())
            return;
        Editor = BuildPublishedEditor(item, ignoreLinkedDraft: false);
    }

    /// <summary>
    /// Builds an editor for a published item. Unless <paramref name="ignoreLinkedDraft"/>, resumes
    /// an in-progress linked draft so the user picks up where they left off.
    /// </summary>
    private EditorViewModel BuildPublishedEditor(WorkshopItem item, bool ignoreLinkedDraft)
    {
        // The live published values - this is the baseline "Save edit"/Revert compare against.
        var published = new ItemEdit
        {
            PublishedFileId = item.PublishedFileId,
            AppId = SelectedGame.AppId,
            Title = item.Title,
            Description = item.Description,
            Tags = item.Tags.ToList(),
            Visibility = item.Visibility,
        };

        // Populate fields from the in-progress draft if one exists, but keep dirtiness measured
        // against the live published state so a resumed draft with real changes can be saved.
        var linked = ignoreLinkedDraft ? null : _drafts.FindByPublishedFileId(item.PublishedFileId);
        var source = linked?.Edit.Clone() ?? published;

        return new EditorViewModel(SelectedGame, source, baseline: published,
            fallbackPreviewUrl: item.PreviewUrl,
            publishedContentInfo: DescribePublishedContent(item));
    }

    /// <summary>Describes a published item's existing content file as "name - size" for the editor's
    /// content box (e.g. "mymod.vpk - 307.9 MB"); null when neither name nor size is known.</summary>
    private static string? DescribePublishedContent(WorkshopItem item)
    {
        var name = item.ContentFileName;
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasSize = item.FileSizeBytes > 0;
        if (!hasName && !hasSize)
            return null;
        if (hasName && hasSize)
            return $"{name} - {item.FileSizeDisplay}";
        return hasName ? name : item.FileSizeDisplay;
    }

    public void EditDraft(Draft draft)
    {
        if (!RequireConnected())
            return;

        // A published-linked draft edits the live item (keep dirtiness vs. the published values);
        // an unpublished draft is just a work-in-progress new item.
        if (draft.Edit.PublishedFileId is { } pubId &&
            PublishedItems.FirstOrDefault(i => i.PublishedFileId == pubId) is { } live)
        {
            Editor = BuildPublishedEditor(live, ignoreLinkedDraft: false);
        }
        else
        {
            // A draft may still reference a published item not in the loaded list; use its Steam
            // preview as the fallback so the preview box isn't empty when no local image is set.
            var fallback = draft.Edit.PublishedFileId is { } pid
                ? PublishedItems.FirstOrDefault(i => i.PublishedFileId == pid)?.PreviewUrl
                : null;
            Editor = new EditorViewModel(SelectedGame, draft.Edit.Clone(),
                fallbackPreviewUrl: fallback) { SourceDraftId = draft.Id };
        }
    }

    /// <summary>
    /// Seed a brand-new item from a template. Any stored content/preview path that no longer exists
    /// is stripped from the template (persisted) and reported via <paramref name="removed"/> so the
    /// caller can warn the user.
    /// </summary>
    public void ApplyTemplate(Template template, out IReadOnlyList<string> removed)
    {
        removed = Array.Empty<string>();
        if (!RequireConnected())
            return;

        var removedList = new List<string>();

        if (!string.IsNullOrWhiteSpace(template.ContentFile) && !File.Exists(template.ContentFile))
        {
            removedList.Add($"content file ({template.ContentFile})");
            template.ContentFile = null;
        }
        if (!string.IsNullOrWhiteSpace(template.PreviewImagePath) && !File.Exists(template.PreviewImagePath))
        {
            removedList.Add($"preview image ({template.PreviewImagePath})");
            template.PreviewImagePath = null;
        }

        if (removedList.Count > 0)
        {
            _templates.Save(template); // persist the cleanup
            LoadLocalLists();
        }

        removed = removedList;

        // Using a template creates a new draft from it, then opens that draft (and the UI switches
        // to the Drafts tab via NavigateToDrafts).
        var edit = template.ToNewEdit(SelectedGame.AppId);
        var name = string.IsNullOrWhiteSpace(edit.Title) ? "Untitled draft" : edit.Title;
        var draft = _drafts.Create(name, edit);
        LoadLocalLists();

        Editor = new EditorViewModel(SelectedGame, edit) { SourceDraftId = draft.Id };
        NavigateToDrafts?.Invoke();
    }

    /// <summary>Raised when the UI should switch to the Drafts tab (e.g. after using a template,
    /// creating a new item, or saving a draft).</summary>
    public event Action? NavigateToDrafts;

    /// <summary>Raised when the UI should switch to the Templates tab (e.g. after saving a template).</summary>
    public event Action? NavigateToTemplates;

    /// <summary>Open a template for editing (template mode: name + presets, no publish).</summary>
    public void EditTemplate(Template template)
    {
        if (!RequireConnected())
            return;

        var seed = template.ToNewEdit(SelectedGame.AppId); // includes content file + preview image
        Editor = new EditorViewModel(SelectedGame, seed, templateId: template.Id, templateName: template.Name);
    }

    public void DeleteDraft(Draft draft)
    {
        _drafts.Delete(draft.Id);

        if (draft.Edit.PublishedFileId is { } pubId)
        {
            // Deleting a published-linked draft == Revert: discard the in-progress edits, leave the
            // live Steam item untouched. If that item is open, reset the editor to live values.
            if (Editor is { IsTemplateMode: false } e && e.PublishedFileId == pubId)
            {
                var live = PublishedItems.FirstOrDefault(i => i.PublishedFileId == pubId);
                Editor = live is not null ? BuildPublishedEditor(live, ignoreLinkedDraft: true) : null;
            }
            StatusMessage = $"Deleted draft and reverted edits for \"{draft.Name}\".";
        }
        else
        {
            // Unpublished draft: if it's the one open in the editor, clear it.
            if (Editor?.SourceDraftId == draft.Id)
                Editor = null;
            StatusMessage = $"Deleted draft \"{draft.Name}\".";
        }

        LoadLocalLists();
        OnPropertyChanged(nameof(CanRevert));
        RevertCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Duplicates a draft. If the draft is the one currently open in the editor, its in-editor
    /// (possibly unsaved) state is persisted first so the clone captures those edits too. The clone
    /// is always a fresh standalone draft - any published-item link is dropped so we never end up
    /// with two drafts tracking the same published item.
    /// </summary>
    public void CloneDraft(Draft draft)
    {
        // Save the editor into its source draft first so unsaved changes are part of the clone.
        // (Published-linked drafts are already auto-persisted on every edit, so this only matters
        // for an open unpublished draft.)
        if (Editor is { IsTemplateMode: false } e && e.SourceDraftId == draft.Id)
        {
            draft.Name = string.IsNullOrWhiteSpace(e.Title) ? draft.Name : e.Title;
            draft.Edit = e.ToItemEdit();
            _drafts.Save(draft);
            e.MarkSaved(); // editor is now in sync with the saved source draft
        }

        var clone = draft.Edit.Clone();
        clone.PublishedFileId = null; // a clone is a new item, not a second edit of the same one
        _drafts.Create(draft.Name, clone);

        LoadLocalLists();
        StatusMessage = $"Cloned draft \"{draft.Name}\".";
        NavigateToDrafts?.Invoke();
    }

    public void DeleteTemplate(Template template)
    {
        _templates.Delete(template.Id);
        if (Editor?.TemplateId == template.Id)
            Editor = null;
        LoadLocalLists();
        StatusMessage = $"Deleted template \"{template.Name}\".";
    }

    /// <summary>Create a reusable template from the current editor's presets, named after the
    /// item's title. Requires a non-empty title.</summary>
    public void SaveAsTemplate()
    {
        if (Editor is null)
            return;

        var name = Editor.Title?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Enter a title before saving as a template - the title is used as the template name.";
            return;
        }

        var edit = Editor.ToItemEdit();
        var template = _templates.Create(name, SelectedGame.AppId);
        template.Description = edit.Description;
        template.ChangeNote = edit.ChangeNote;
        template.Tags = edit.Tags.ToList();
        template.DefaultVisibility = edit.Visibility;
        template.ContentFile = edit.ContentFile;
        template.PreviewImagePath = edit.PreviewImagePath;
        _templates.Save(template);

        // Converting a non-published draft into a template consumes the draft (it now lives on as the
        // template). Published-item edits keep their linked draft - they're not being converted.
        if (!Editor.IsEditingPublished && Editor.SourceDraftId is { } draftId)
        {
            _drafts.Delete(draftId);
            Editor = null;
        }

        LoadLocalLists();
        StatusMessage = $"Saved template \"{name}\".";
        NavigateToTemplates?.Invoke();
    }

    private void SaveDraft()
    {
        if (Editor is null)
            return;

        // In template mode, "Save" persists the template instead of creating a draft.
        if (Editor is { IsTemplateMode: true, TemplateId: { } tid })
        {
            SaveEditedTemplate(tid);
            return;
        }

        var name = string.IsNullOrWhiteSpace(Editor.Title) ? "Untitled draft" : Editor.Title;

        // Update the draft this editor is already bound to; only create a new one the first time.
        var existing = Editor.SourceDraftId is { } id ? _drafts.Get(id) : null;
        if (existing is not null)
        {
            existing.Name = name;
            existing.Edit = Editor.ToItemEdit();
            _drafts.Save(existing);
        }
        else
        {
            var created = _drafts.Create(name, Editor.ToItemEdit());
            Editor.SourceDraftId = created.Id; // subsequent saves update this same draft
        }

        Editor.MarkSaved(); // clears the editor's unsaved-changes indicator
        LoadLocalLists();
        StatusMessage = $"Saved draft \"{name}\".";
        NavigateToDrafts?.Invoke();
    }

    private void SaveEditedTemplate(Guid templateId)
    {
        var existing = _templates.GetAll().FirstOrDefault(t => t.Id == templateId);
        if (existing is null || Editor is null)
            return;

        var edit = Editor.ToItemEdit();
        existing.Name = string.IsNullOrWhiteSpace(Editor.Title) ? existing.Name : Editor.Title;
        existing.Description = edit.Description;
        existing.ChangeNote = edit.ChangeNote;
        existing.Tags = edit.Tags.ToList();
        existing.DefaultVisibility = edit.Visibility;
        existing.ContentFile = edit.ContentFile;
        existing.PreviewImagePath = edit.PreviewImagePath;
        _templates.Save(existing);
        Editor.MarkSaved(); // clears the editor's unsaved-changes indicator
        LoadLocalLists();
        StatusMessage = $"Saved template \"{existing.Name}\".";
        NavigateToTemplates?.Invoke();
    }

    private async Task PublishAsync()
    {
        if (Editor is null)
            return;

        var edit = Editor.ToItemEdit();
        if (edit.PublishedFileId is null &&
            (string.IsNullOrWhiteSpace(edit.ContentFile) || !File.Exists(edit.ContentFile)))
        {
            StatusMessage = $"Pick a valid {SelectedGame.ContentFileExtension} content file before publishing a new item.";
            return;
        }

        var wasEditingPublished = Editor.IsEditingPublished;
        IsBusy = true;
        IsConsoleVisible = true; // surface the live upload log so the user sees real progress
        ConsoleLog(wasEditingPublished
            ? $"Saving edit to \"{edit.Title}\"..."
            : $"Publishing new item \"{edit.Title}\"...");
        try
        {
            // Publishing needs a live Steam host for this game. Auto-connect if the user hasn't yet.
            if (_service.ActiveAppId != SelectedGame.AppId)
            {
                StatusMessage = $"Connecting to Steam for {SelectedGame.DisplayName}...";
                await _service.SelectGameAsync(SelectedGame.AppId);
            }

            var ping = await _service.PingAsync();
            if (!ping.SteamRunning)
            {
                StatusMessage = "Steam is not running, or you do not own this game. Start Steam and click Connect.";
                return;
            }

            if (!HasProfile)
                UpdateProfile(ping);

            StatusMessage = wasEditingPublished ? "Saving edit to Steam Workshop..." : "Uploading to Steam Workshop...";
            var result = await _service.PublishAsync(edit);

            if (!result.Success)
            {
                StatusMessage = $"Publish failed: {result.Error ?? "the content upload did not complete."}";
                ConsoleLog($"FAILED: {result.Error ?? "the content upload did not complete."}");
                return;
            }

            if (result.NeedsLegalAgreement)
            {
                MessageBox.Show(
                    "You must accept the Steam Workshop legal agreement for this item before it is visible. " +
                    "The item was created/updated; open it on Steam to accept.",
                    "Workshop legal agreement", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // The publish succeeded, so remove any draft that was tracking this work:
            // - a published-linked draft (edits to an existing item), and/or
            // - the new-item draft this editor was bound to (first publish of a brand-new item).
            if (result.PublishedFileId != 0)
            {
                var linked = _drafts.FindByPublishedFileId(result.PublishedFileId);
                if (linked is not null)
                    _drafts.Delete(linked.Id);

                if (Editor?.SourceDraftId is { } draftId)
                {
                    _drafts.Delete(draftId);
                    Editor.SourceDraftId = null;
                }

                LoadLocalLists();
            }

            // Clear the uploaded content/preview so a follow-up metadata-only edit doesn't
            // accidentally re-upload the same large content. Re-drop to upload again.
            Editor?.MarkPublished();

            StatusMessage = wasEditingPublished
                ? $"Saved edit to item {result.PublishedFileId}."
                : $"Published item {result.PublishedFileId}.";
            ConsoleLog($"Done. Item {result.PublishedFileId}.");
            await RefreshPublishedAsync();

            // Rebuild the editor from the refreshed item so the content box shows the just-uploaded
            // file's name/size (and the preview/baseline) rather than stale construction-time values.
            if (result.PublishedFileId != 0 &&
                PublishedItems.FirstOrDefault(i => i.PublishedFileId == result.PublishedFileId) is { } refreshed)
            {
                Editor = BuildPublishedEditor(refreshed, ignoreLinkedDraft: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Publish failed: {ex.Message}";
            ConsoleLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _service.HostOutput -= OnHostOutput;
        return _service.DisposeAsync();
    }
}

/// <summary>A Published-list sort choice: its menu label, the item property to sort on, and the
/// natural direction (which the reverse toggle flips).</summary>
public sealed record PublishedSortOption(string Label, string Property, ListSortDirection Direction)
{
    // Shown directly in the ComboBox (the themed combo's selection box ignores DisplayMemberPath).
    public override string ToString() => Label;
}
