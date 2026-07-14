using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Storage;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.App.ViewModels;

/// <summary>Which column the Unpack file list is sorted by (the clickable header ribbon).</summary>
public enum UnpackSortColumn { Name, Type, Source, Size }

/// <summary>Kind of hover preview a file row supports (see <see cref="UnpackFileViewModel.PreviewKind"/>).</summary>
public enum UnpackPreviewKind { None, Image, Vtf, Vtex }

/// <summary>
/// The Unpack tab: opens a packed Source archive (.vpk or .gma) - or a whole game via its
/// gameinfo.txt, which mounts every VPK the SearchPaths reference in engine priority order
/// (topmost search path wins for conflicting files, like the engine's filesystem) - then lets
/// the user browse a folder tree, preview files, and export any selection to disk. Crowbar's
/// "Unpack" feature, PulseWorkshop-style. Export output streams into the shared console.
/// </summary>
public sealed class UnpackViewModel : ObservableObject
{
    /// <summary>Filter results are capped so typing one letter over a full-game mount stays snappy.</summary>
    private const int MaxFilterResults = 5000;

    /// <summary>Exporting at least this many files prompts a confirmation first, so a stray click on a
    /// large folder (or the whole-package root) doesn't spill thousands of files without warning.</summary>
    private const int ExportConfirmThreshold = 100;

    private readonly ConsoleViewModel _console;
    private readonly UiSettings _settings;

    private IPackedArchive? _archive;
    private UnpackFolderViewModel? _treeRoot;
    private UnpackFolderViewModel? _selectedFolder;
    private string _filterText = string.Empty;
    // Matches the display name; plain case-insensitive substring by default, regex when FilterRegex is on.
    private readonly ItemFilter _filter = new();
    private string _selectedSearchScope = ScopeRecursive;
    private bool _isLoading;
    private bool _isExporting;
    private double _exportProgress;
    private string _statusMessage = "No package open.";
    private string _fileListCaption = string.Empty;
    private CancellationTokenSource? _exportCancel;
    private UnpackSortColumn _sortColumn = UnpackSortColumn.Name;
    private bool _sortAscending = true;
    private string _selectedExportMode = ExportModeChoose;
    // Set while we programmatically clear one pane's selection to make the other the active one, so
    // the cross-clearing does not bounce back and forth.
    private bool _syncingSelection;
    // Bumped by every Close(). An open that awaited across a newer close/open discards its result
    // instead of committing, otherwise two packages would end up open at once (e.g. a second
    // "View Package" click or a dropped file while the first load is still running).
    private int _openGeneration;
    // Opens currently awaiting their background load; IsLoading clears when the last one finishes.
    private int _pendingOpens;

    // The .mdl files the user has exported this session, surfaced by the "View model" bar so an
    // unpacked model can be sent straight to the Model View tab.
    private UnpackedModelViewModel? _selectedModel;

    // Typing restarts this timer; the search itself runs when it fires (or on Enter). Matching a
    // full-game mount is fast, but repopulating a 5000-row list on every keystroke is not.
    private readonly DispatcherTimer _searchDebounce;

    public UnpackViewModel(ConsoleViewModel console, UiSettings settings)
    {
        _console = console;
        _settings = settings;
        _selectedExportMode = settings.UnpackExportBesidePackage ? ExportModeBeside : ExportModeChoose;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) => ApplyFilterNow();

        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsLoading && !IsExporting);
        CloseCommand = new RelayCommand(Close, () => IsArchiveOpen && !IsLoading && !IsExporting);
        RebuildRecentArchives();
        ExportSelectedCommand = new AsyncRelayCommand(ExportSelectedAsync, () => CanExportSelected);
        CancelExportCommand = new RelayCommand(CancelExport, () => IsExporting);
        ViewModelCommand = new RelayCommand(ViewSelectedModel, () => CanViewModel);
        BrowseModelCommand = new RelayCommand(BrowseSelectedModel, () => CanViewModel);
        ToggleDetailsPaneCommand = new RelayCommand(() => IsDetailsPaneOpen = !IsDetailsPaneOpen);
    }

    /// <summary>Raised (on the UI thread) when a row should be scrolled into view - after
    /// "Go to file" navigates to the folder and selects the target file.</summary>
    public event Action<UnpackFileViewModel>? ScrollFileIntoView;

    /// <summary>Raised when the user clicks "View model": the on-disk path of an exported .mdl to hand
    /// to the Model View tab.</summary>
    public event Action<string>? ViewModelRequested;

    /// <summary>Raised (on the UI thread) when the Details pane's subject changes to a different
    /// item, so the view reloads its thumbnail (decoding a .vtf/.vtex_c preview needs WPF and lives
    /// in the code-behind).</summary>
    public event Action? DetailChanged;

    // --- Commands -------------------------------------------------------------------------

    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand CloseCommand { get; }
    public AsyncRelayCommand ExportSelectedCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public RelayCommand ViewModelCommand { get; }
    public RelayCommand BrowseModelCommand { get; }
    public RelayCommand ToggleDetailsPaneCommand { get; }

    // --- Bound state ------------------------------------------------------------------------

    public bool IsArchiveOpen => _archive is not null;

    public string ArchiveName => _archive?.DisplayName ?? string.Empty;
    public string ArchivePath => _archive?.SourcePath ?? string.Empty;

    /// <summary>Summary line under the archive name: entry count, total size, mount info.</summary>
    public string ArchiveSummary
    {
        get
        {
            if (_archive is null)
                return string.Empty;
            long total = 0;
            foreach (var e in _archive.Entries)
                total += e.Size;
            var summary = $"{_archive.Entries.Count:N0} files - {FormatSize(total)}";
            if (_archive is GameInfoMount mount)
                summary += $" - {mount.MountedVpks.Count} VPKs mounted"
                    + (mount.ShadowedCount > 0 ? $" ({mount.ShadowedCount:N0} shadowed by priority)" : "");
            else if (_archive is GmaArchive { AddonName.Length: > 0 } gma)
                summary += $" - \"{gma.AddonName}\"";
            return summary;
        }
    }

    /// <summary>The folder tree (a single root node named after the archive).</summary>
    public ObservableCollection<UnpackFolderViewModel> TreeRoots { get; } = new();

    /// <summary>Files shown on the right: the selected folder's files, or flat filter matches.</summary>
    public ObservableCollection<UnpackFileViewModel> Files { get; } = new();

    /// <summary>The archives most recently opened here that still exist on disk (newest first, capped
    /// at <see cref="MaxRecentShown"/>), shown in the empty state's "Open recent" list.</summary>
    public ObservableCollection<RecentItemViewModel> RecentArchives { get; } = new();

    private const int MaxRecentShown = 8;

    /// <summary>Rebuilds <see cref="RecentArchives"/> from the persisted list, dropping entries whose
    /// file is gone. Called at startup and after each successful open.</summary>
    private void RebuildRecentArchives()
    {
        RecentArchives.Clear();
        foreach (var path in _settings.UnpackRecentArchives)
        {
            if (!File.Exists(path))
                continue;
            RecentArchives.Add(new RecentItemViewModel(path, p => _ = OpenFromPathAsync(p)));
            if (RecentArchives.Count >= MaxRecentShown)
                break;
        }
    }

    // --- Model View bridge ----------------------------------------------------------------------

    /// <summary>The .mdl files exported this session (from the open archive), by archive-relative path -
    /// the "View model" picker. Grows as models are exported; cleared on close.</summary>
    public ObservableCollection<UnpackedModelViewModel> ModelFiles { get; } = new();

    /// <summary>True once at least one .mdl has been unpacked this session (shows the "View model" bar).</summary>
    public bool HasModelFiles => ModelFiles.Count > 0;

    /// <summary>The exported .mdl chosen in the picker to hand to the Model View tab.</summary>
    public UnpackedModelViewModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetField(ref _selectedModel, value))
                return;
            ViewModelCommand.RaiseCanExecuteChanged();
            BrowseModelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>"View model" is available once an exported model is picked.</summary>
    private bool CanViewModel => _selectedModel is not null;

    /// <summary>Caption above the file list ("materials/models - 120 files" or match info).</summary>
    public string FileListCaption
    {
        get => _fileListCaption;
        private set => SetField(ref _fileListCaption, value);
    }

    public UnpackFolderViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetField(ref _selectedFolder, value))
            {
                // Re-clicking the current folder still makes the tree the active pane.
                if (value is not null)
                    ClearFileHighlights();
                return;
            }
            // Picking a folder makes the tree the active selection: drop any file highlights. A
            // folder-scoped search follows the selection; a Global one keeps its flat results, so
            // clear the highlights there explicitly (the other branches rebuild the list anyway).
            if (FilterText.Length == 0)
                ShowFolderFiles();
            else if (SelectedSearchScope != ScopeGlobal)
                ShowFilterResults();
            else if (value is not null)
                ClearFileHighlights();
        }
    }

    /// <summary>Substring search over the scope picked next to the box; non-empty switches the
    /// list to flat results. Debounced - the list refreshes shortly after typing pauses, or
    /// immediately on Enter (see <see cref="ApplyFilterNow"/>).</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetField(ref _filterText, value ?? string.Empty))
                return;
            _filter.Query = _filterText;
            _searchDebounce.Stop();
            if (_filterText.Length == 0)
                ShowFolderFiles(); // clearing snaps back to browsing instantly
            else
                _searchDebounce.Start();
        }
    }

    public bool IsFiltering => _filterText.Length > 0;

    /// <summary>When on, the filter box is a regex (case-insensitive) instead of a plain substring.</summary>
    public bool FilterRegex
    {
        get => _filter.UseRegex;
        set
        {
            if (_filter.UseRegex == value)
                return;
            _filter.UseRegex = value;
            OnPropertyChanged();
            if (_filterText.Length > 0)
                ApplyFilterNow();
        }
    }

    // --- Search scope ---------------------------------------------------------------------------

    public const string ScopeCurrent = "Current";
    public const string ScopeRecursive = "Recursive";
    public const string ScopeGlobal = "Global";

    /// <summary>Where the search looks: the selected folder only, the selected folder and all its
    /// subfolders, or the whole package.</summary>
    public IReadOnlyList<string> SearchScopes { get; } = [ScopeCurrent, ScopeRecursive, ScopeGlobal];

    public string SelectedSearchScope
    {
        get => _selectedSearchScope;
        set
        {
            if (SetField(ref _selectedSearchScope, value) && FilterText.Length > 0)
                ApplyFilterNow();
        }
    }

    /// <summary>Runs the pending search immediately (Enter in the filter box, scope change).</summary>
    public void ApplyFilterNow()
    {
        _searchDebounce.Stop();
        if (_filterText.Length > 0)
            ShowFilterResults();
    }

    // --- Export destination mode ----------------------------------------------------------------

    public const string ExportModeChoose = "Choose folder...";
    public const string ExportModeBeside = "Beside package";

    /// <summary>The two export destinations: prompt for a folder each time, or a fixed location
    /// beside the opened package (see <see cref="ResolveBesideExportRoot"/>).</summary>
    public IReadOnlyList<string> ExportModes { get; } = [ExportModeChoose, ExportModeBeside];

    public string SelectedExportMode
    {
        get => _selectedExportMode;
        set
        {
            if (!SetField(ref _selectedExportMode, value))
                return;
            _settings.UnpackExportBesidePackage = value == ExportModeBeside;
            OnPropertyChanged(nameof(BesideExportHint));
        }
    }

    /// <summary>When on, the hover preview honors a texture's alpha (transparent areas show through);
    /// when off it forces the thumbnail opaque, so masks that store data with alpha 0 (metalness,
    /// roughness, ...) are still visible. Applies to both .vtf and Source 2 .vtex_c previews and is
    /// read at hover time - see the preview handler in the window. Persisted in UI settings.</summary>
    public bool PreviewAlpha
    {
        get => _settings.UnpackPreviewAlpha;
        set
        {
            if (_settings.UnpackPreviewAlpha == value)
                return;
            _settings.UnpackPreviewAlpha = value;
            OnPropertyChanged();
        }
    }

    // --- Details pane ---------------------------------------------------------------------------
    //
    // An Explorer-style pane on the right of the file list: a thumbnail (a decoded .vtf/.vtex_c or
    // raster preview, or a generic file/folder glyph) over a few info rows for whatever is currently
    // selected. The pane is width-adjustable (its splitter, persisted in UI settings) and can be
    // collapsed. The textual fields live here; the view owns the thumbnail image (see DetailChanged).

    private bool _detailHasContent;
    private bool _detailIsFolder;
    private string _detailName = string.Empty;
    private string _detailTypeText = string.Empty;
    private string _detailSizeText = string.Empty;
    private string _detailLocationText = string.Empty;
    private string _detailSourceText = string.Empty;
    private string _detailExtensionChip = string.Empty;
    private UnpackPreviewKind _detailPreviewKind = UnpackPreviewKind.None;
    private PackedEntry? _detailEntry;
    // The item currently shown; used to skip redundant thumbnail reloads (and its DetailChanged).
    private string _detailSignature = string.Empty;

    /// <summary>Whether the Details pane is shown (persisted). Toggled by the "Details" button.</summary>
    public bool IsDetailsPaneOpen
    {
        get => _settings.UnpackDetailsPaneOpen;
        set
        {
            if (_settings.UnpackDetailsPaneOpen == value)
                return;
            _settings.UnpackDetailsPaneOpen = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True when there is something to describe (an item is selected); false shows the
    /// pane's placeholder hint instead.</summary>
    public bool DetailHasContent => _detailHasContent;

    /// <summary>True when the described item is a folder (the view shows a folder glyph, not a file
    /// thumbnail).</summary>
    public bool DetailIsFolder => _detailIsFolder;

    public string DetailName => _detailName;
    public string DetailTypeText => _detailTypeText;
    public string DetailSizeText => _detailSizeText;
    public string DetailLocationText => _detailLocationText;
    public string DetailSourceText => _detailSourceText;
    public bool DetailHasSource => _detailSourceText.Length > 0;

    /// <summary>Uppercased extension chip shown over a generic file glyph when there is no thumbnail
    /// (e.g. "VMT"). Empty for folders and extension-less files.</summary>
    public string DetailExtensionChip => _detailExtensionChip;

    /// <summary>The kind of thumbnail the described file supports (the view decodes it).</summary>
    public UnpackPreviewKind DetailPreviewKind => _detailPreviewKind;

    /// <summary>The described file's entry (for the view to read bytes and decode a thumbnail); null
    /// for folders / multi-selection / nothing.</summary>
    public PackedEntry? DetailEntry => _detailEntry;

    /// <summary>Recomputes the Details pane from the current selection: a single highlighted file
    /// row wins, then a multi-selection summary, then the folder selected in the tree, else nothing.</summary>
    private void UpdateDetail()
    {
        if (_archive is null)
        {
            SetDetailNone();
            return;
        }

        UnpackFileViewModel? single = null;
        int highlighted = 0;
        foreach (var row in Files)
        {
            if (!row.IsSelected)
                continue;
            single = row;
            if (++highlighted > 1)
                break;
        }

        if (highlighted == 1)
            SetDetailRow(single!);
        else if (highlighted > 1)
            SetDetailMulti();
        else if (_selectedFolder is { } folder)
            SetDetailFolder(folder);
        else
            SetDetailNone();
    }

    private void SetDetailRow(UnpackFileViewModel row)
    {
        if (row.Folder is { } folder)
        {
            SetDetailFolder(folder);
            return;
        }
        var e = row.Entry!;
        ApplyDetail(
            isFolder: false,
            name: e.FileName,
            type: FriendlyType(e.Extension),
            size: FormatSize(e.Size),
            location: e.Directory.Length == 0 ? "(root)" : e.Directory,
            source: e.Source,
            chip: e.Extension.Length > 0 ? e.Extension.ToUpperInvariant() : string.Empty,
            previewKind: row.PreviewKind,
            entry: e,
            signature: $"F:{e.Source}|{e.Path}");
    }

    private void SetDetailFolder(UnpackFolderViewModel folder)
    {
        bool isRoot = folder.FullPath.Length == 0;
        ApplyDetail(
            isFolder: true,
            name: isRoot ? ArchiveName : folder.Name,
            type: "File folder",
            size: $"{folder.TotalFileCount:N0} file{(folder.TotalFileCount == 1 ? "" : "s")}",
            location: isRoot ? "(package)"
                : folder.Parent is { FullPath.Length: > 0 } p ? p.FullPath : "(root)",
            source: string.Empty,
            chip: string.Empty,
            previewKind: UnpackPreviewKind.None,
            entry: null,
            signature: $"D:{folder.FullPath}");
    }

    private void SetDetailMulti()
    {
        long totalBytes = 0;
        int files = 0, folders = 0;
        foreach (var row in Files)
        {
            if (!row.IsSelected)
                continue;
            if (row.Folder is not null)
                folders++;
            else { files++; totalBytes += row.Entry!.Size; }
        }
        var parts = new List<string>();
        if (files > 0)
            parts.Add($"{files:N0} file{(files == 1 ? "" : "s")}");
        if (folders > 0)
            parts.Add($"{folders:N0} folder{(folders == 1 ? "" : "s")}");
        ApplyDetail(
            isFolder: folders > 0 && files == 0,
            name: $"{files + folders:N0} items selected",
            type: string.Join(", ", parts),
            size: files > 0 ? FormatSize(totalBytes) : string.Empty,
            location: string.Empty,
            source: string.Empty,
            chip: string.Empty,
            previewKind: UnpackPreviewKind.None,
            entry: null,
            signature: $"M:{files}/{folders}/{totalBytes}");
    }

    private void SetDetailNone()
    {
        ApplyDetail(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, UnpackPreviewKind.None, null, "N", hasContent: false);
    }

    private void ApplyDetail(bool isFolder, string name, string type, string size, string location,
        string source, string chip, UnpackPreviewKind previewKind, PackedEntry? entry, string signature,
        bool hasContent = true)
    {
        _detailHasContent = hasContent;
        _detailIsFolder = isFolder;
        _detailName = name;
        _detailTypeText = type;
        _detailSizeText = size;
        _detailLocationText = location;
        _detailSourceText = source;
        _detailExtensionChip = chip;
        _detailPreviewKind = previewKind;
        _detailEntry = entry;
        OnPropertyChanged(nameof(DetailHasContent));
        OnPropertyChanged(nameof(DetailIsFolder));
        OnPropertyChanged(nameof(DetailName));
        OnPropertyChanged(nameof(DetailTypeText));
        OnPropertyChanged(nameof(DetailSizeText));
        OnPropertyChanged(nameof(DetailLocationText));
        OnPropertyChanged(nameof(DetailSourceText));
        OnPropertyChanged(nameof(DetailHasSource));
        OnPropertyChanged(nameof(DetailExtensionChip));

        // Only reload the thumbnail when the subject actually changed (selection churn during a
        // drag-select would otherwise re-decode the same file repeatedly).
        if (_detailSignature != signature)
        {
            _detailSignature = signature;
            DetailChanged?.Invoke();
        }
    }

    /// <summary>A short, friendly type label for the Details pane, Explorer-style ("Valve Texture",
    /// "Source Model", ...), falling back to "&lt;EXT&gt; File" for anything unmapped.</summary>
    private static string FriendlyType(string ext) => ext.ToLowerInvariant() switch
    {
        "" => "File",
        "vtf" => "Valve Texture",
        "vmt" => "Valve Material",
        "vtex_c" => "Source 2 Texture",
        "vmat_c" => "Source 2 Material",
        "mdl" => "Source Model",
        "vmdl_c" => "Source 2 Model",
        "phy" => "Model Physics",
        "vvd" => "Model Vertex Data",
        "vtx" => "Model Mesh Data",
        "qc" => "Model Compile Script",
        "smd" => "Studiomdl Model",
        "dmx" => "Datamodel",
        "vpk" => "Valve Pack",
        "gma" => "GMod Addon",
        "bsp" => "Source Map",
        "vmf" => "Hammer Map Source",
        "wav" => "WAV Sound",
        "mp3" => "MP3 Sound",
        "txt" => "Text Document",
        "cfg" or "vdf" or "kv" or "res" => "KeyValues Text",
        "png" => "PNG Image",
        "jpg" or "jpeg" => "JPEG Image",
        "tga" => "Targa Image",
        "bmp" => "Bitmap Image",
        "gif" => "GIF Image",
        var e => $"{e.ToUpperInvariant()} File",
    };

    /// <summary>One-line preview of where "Beside package" would write, shown next to the picker so
    /// the fixed location is visible before exporting. Empty in "Choose folder" mode or with nothing
    /// open.</summary>
    public string BesideExportHint =>
        _selectedExportMode == ExportModeBeside && ResolveBesideExportRoot() is { } root
            ? $"-> {root}"
            : string.Empty;

    /// <summary>The fixed export root for "Beside package" mode: a gameinfo mount writes into an
    /// <c>unpack_files</c> subfolder next to gameinfo.txt; a bare .vpk/.gma writes into a
    /// <c>&lt;package name&gt;_unpack</c> subfolder next to the package. Null with nothing open.</summary>
    private string? ResolveBesideExportRoot()
    {
        if (_archive is null)
            return null;

        string source;
        try { source = Path.GetFullPath(_archive.SourcePath); }
        catch { return null; }
        var dir = Path.GetDirectoryName(source) ?? string.Empty;

        if (_archive is GameInfoMount)
            return Path.Combine(dir, "unpack_files");

        // Bare pack: a <package name>_unpack subfolder beside it. The opened VPK is the "_dir" file;
        // strip that suffix so the folder is named after the package, not the directory index.
        var name = Path.GetFileNameWithoutExtension(source);
        if (name.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return Path.Combine(dir, name + "_unpack");
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) RefreshCommands(); }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set { if (SetField(ref _isExporting, value)) RefreshCommands(); }
    }

    /// <summary>0..100 for the determinate export progress bar.</summary>
    public double ExportProgress
    {
        get => _exportProgress;
        private set => SetField(ref _exportProgress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    private bool CanExportSelected =>
        IsArchiveOpen && !IsExporting && !IsLoading
        && (Files.Any(f => f.IsSelected) || SelectedFolder is not null);

    /// <summary>Label for the Export button, tracking the active selection so the two panes read as
    /// one: highlighted rows win (many -> "Export selected", one file -> "Export item", one folder ->
    /// "Export folder"); with nothing highlighted it acts on the tree's selected folder.</summary>
    public string ExportButtonLabel
    {
        get
        {
            int highlighted = 0;
            UnpackFileViewModel? only = null;
            foreach (var row in Files)
            {
                if (!row.IsSelected)
                    continue;
                if (++highlighted > 1)
                    return "Export selected";
                only = row;
            }
            if (highlighted == 1)
                return only!.IsFolder ? "Export folder" : "Export item";
            // Nothing highlighted: the button falls back to the selected tree folder.
            return "Export folder";
        }
    }

    /// <summary>Called by the view when the file-list selection toggles.</summary>
    public void OnFileSelectionChanged()
    {
        if (_syncingSelection)
            return;
        SyncActivePaneToFileList();
    }

    /// <summary>Highlighting a file makes the list the active pane: drop the tree's visual selection
    /// while keeping the folder as the navigation context (so the list stays populated), then refresh
    /// the export command/label. Call once after a batch selection change.</summary>
    private void SyncActivePaneToFileList()
    {
        if (_selectedFolder is { IsSelected: true } folder && Files.Any(f => f.IsSelected))
            folder.IsSelected = false;
        ExportSelectedCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ExportButtonLabel));
        UpdateDetail();
    }

    /// <summary>Clears every file-row highlight without letting that bounce back and re-clear the
    /// tree - used when the tree becomes the active pane.</summary>
    private void ClearFileHighlights()
    {
        _syncingSelection = true;
        foreach (var row in Files)
            row.IsSelected = false;
        _syncingSelection = false;
        ExportSelectedCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ExportButtonLabel));
        UpdateDetail();
    }

    private void RefreshCommands()
    {
        OpenCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        ExportSelectedCommand.RaiseCanExecuteChanged();
        CancelExportCommand.RaiseCanExecuteChanged();
        ViewModelCommand.RaiseCanExecuteChanged();
        BrowseModelCommand.RaiseCanExecuteChanged();
    }

    // --- Open / close ---------------------------------------------------------------------------

    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a packed archive or a game's gameinfo.txt",
            Filter = "Source packages (*.vpk;*.gma;gameinfo.txt)|*.vpk;*.gma;gameinfo.txt"
                   + "|VPK packages (*.vpk)|*.vpk|GMod addons (*.gma)|*.gma"
                   + "|Game info (gameinfo.txt)|gameinfo.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        await OpenFromPathAsync(dlg.FileName);
    }

    /// <summary>Opens an archive directly from a path (dialog pick or a dropped file).</summary>
    public async Task OpenFromPathAsync(string path)
    {
        if (!PackedArchiveLoader.CanOpen(path))
        {
            StatusMessage = $"Can't open {Path.GetFileName(path)} - pick a .vpk, .gma, or gameinfo.txt.";
            return;
        }

        Close();
        int gen = _openGeneration;
        _pendingOpens++;
        IsLoading = true;
        StatusMessage = $"Opening {Path.GetFileName(path)}...";
        try
        {
            // Opening a gameinfo mount reads many VPK directory trees; a workshop gma may need a
            // full LZMA decompress first. All off the UI thread.
            var (archive, root) = await Task.Run(() =>
            {
                var opened = PackedArchiveLoader.Open(path);
                try
                {
                    return (opened, BuildTree(opened));
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
            });

            if (gen != _openGeneration)
            {
                // A newer open (or close) superseded this load while it ran - drop the result.
                archive.Dispose();
                return;
            }

            _archive = archive;
            _treeRoot = root;
            TreeRoots.Add(root);
            root.IsExpanded = true;
            root.IsSelected = true;
            SelectedFolder = root;

            // Remember it for the empty state's "Open recent" list (also updates the last-open pointer).
            _settings.RememberUnpackArchive(archive.SourcePath);
            RebuildRecentArchives();

            StatusMessage = $"Opened {archive.DisplayName}.";
            Log($"=== Unpack: opened {archive.SourcePath} ({archive.Entries.Count:N0} files) ===");
            if (archive is GameInfoMount mount)
            {
                Log($"Mounted {mount.MountedVpks.Count} VPKs in search-path priority order:");
                for (int i = 0; i < mount.MountedVpks.Count; i++)
                    Log($"  {i + 1,3}. {mount.MountedVpks[i]}");
                if (mount.ShadowedCount > 0)
                    Log($"{mount.ShadowedCount:N0} conflicting entries resolved to the topmost search path.");
            }
            else if (archive is GmaArchive { WasCompressed: true })
            {
                Log("The .gma was LZMA-compressed (legacy workshop download) - decompressed for browsing.");
            }
        }
        catch (Exception ex)
        {
            if (gen == _openGeneration)
                StatusMessage = $"Failed to open: {ex.Message}";
            Log($"Unpack: failed to open {path}: {ex.Message}");
        }
        finally
        {
            if (--_pendingOpens == 0)
                IsLoading = false;
            OnPropertyChanged(nameof(IsArchiveOpen));
            OnPropertyChanged(nameof(ArchiveName));
            OnPropertyChanged(nameof(ArchivePath));
            OnPropertyChanged(nameof(ArchiveSummary));
            OnPropertyChanged(nameof(BesideExportHint));
        }
    }

    private void Close()
    {
        _openGeneration++; // supersedes any open still loading in the background
        _exportCancel?.Cancel();
        _searchDebounce.Stop();
        _archive?.Dispose();
        _archive = null;
        _treeRoot = null;
        _selectedFolder = null;
        _filterText = string.Empty;
        _filter.Query = string.Empty;
        _selectedModel = null;
        TreeRoots.Clear();
        Files.Clear();
        ModelFiles.Clear();
        FileListCaption = string.Empty;
        StatusMessage = "No package open.";
        CleanPreviewDir();
        UpdateDetail(); // resets the Details pane and clears its thumbnail
        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
    }

    /// <summary>Pack integration: when the archive open here is (part of) the package a pack is
    /// about to overwrite, its read handles would block the packer (and the rename that follows) -
    /// close it and return the archive's path so the caller can reopen the fresh package after the
    /// pack. Returns null when the pack touches nothing that is open here.</summary>
    public string? ReleaseForRepack(IEnumerable<string> packOutputPaths)
    {
        if (_archive is null)
            return null;

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packOutputPaths)
            if (!string.IsNullOrWhiteSpace(p))
                targets.Add(PackageFamilyKey(p));
        if (targets.Count == 0)
            return null;

        // The files this archive holds (or may lazily open) read handles on: the archive itself,
        // or - for a gameinfo mount - every mounted VPK.
        IEnumerable<string> held = _archive is GameInfoMount mount
            ? mount.MountedVpks
            : new[] { _archive.SourcePath };
        if (!held.Any(h => targets.Contains(PackageFamilyKey(h))))
            return null;

        var reopen = _archive.SourcePath;
        var name = _archive.DisplayName;
        Close();
        StatusMessage = $"Closed {name} while it is being repacked.";
        Log($"Unpack: closed {name} - it is about to be repacked.");
        return reopen;
    }

    /// <summary>Collapses a package path to its "family" so related files compare equal: a
    /// <c>x_dir.vpk</c>, its numbered chunks (<c>x_000.vpk</c>, ...) and a single-file <c>x.vpk</c>
    /// all map to <c>x.vpk</c>. Non-vpk paths just normalize.</summary>
    private static string PackageFamilyKey(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }
        if (!full.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            return full;

        var name = Path.GetFileNameWithoutExtension(full);
        if (name.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        else if (name.Length > 4 && name[^4] == '_'
                 && char.IsAsciiDigit(name[^3]) && char.IsAsciiDigit(name[^2]) && char.IsAsciiDigit(name[^1]))
            name = name[..^4];
        return Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, name + ".vpk");
    }

    // --- Tree building ----------------------------------------------------------------------

    private static UnpackFolderViewModel BuildTree(IPackedArchive archive)
    {
        var root = new UnpackFolderViewModel(Path.GetFileName(archive.SourcePath), string.Empty, parent: null);
        var folders = new Dictionary<string, UnpackFolderViewModel>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        UnpackFolderViewModel GetFolder(string dir)
        {
            if (folders.TryGetValue(dir, out var existing))
                return existing;
            int slash = dir.LastIndexOf('/');
            var parent = GetFolder(slash < 0 ? string.Empty : dir[..slash]);
            var node = new UnpackFolderViewModel(slash < 0 ? dir : dir[(slash + 1)..], dir, parent);
            folders[dir] = node;
            parent.ChildList.Add(node);
            return node;
        }

        foreach (var entry in archive.Entries)
            GetFolder(entry.Directory).FileList.Add(entry);

        root.SortAndCount();
        return root;
    }

    // --- File list ------------------------------------------------------------------------------

    private void ShowFolderFiles()
    {
        OnPropertyChanged(nameof(IsFiltering));
        if (_selectedFolder is not { } folder)
        {
            Files.Clear();
            FileListCaption = string.Empty;
            return;
        }

        // Explorer-style: subfolders and this folder's files, sorted by the active column header.
        var rows = new List<UnpackFileViewModel>(folder.Children.Count + folder.FileList.Count);
        foreach (var child in folder.Children)
            rows.Add(UnpackFileViewModel.ForFolder(this, child));
        foreach (var entry in folder.FileList)
            rows.Add(UnpackFileViewModel.ForEntry(this, entry, entry.FileName));
        SetFiles(rows);

        var where = folder.FullPath.Length == 0 ? "root" : folder.FullPath;
        var parts = new List<string>();
        if (folder.Children.Count > 0)
            parts.Add($"{folder.Children.Count:N0} folder{(folder.Children.Count == 1 ? "" : "s")}");
        parts.Add($"{folder.FileList.Count:N0} file{(folder.FileList.Count == 1 ? "" : "s")}");
        FileListCaption = $"{where} - {string.Join(", ", parts)}"
            + (folder.TotalFileCount != folder.FileList.Count
                ? $" ({folder.TotalFileCount:N0} incl. subfolders)" : "");
        ExportSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Double-click on a folder row: select it in the tree (which repopulates the list).</summary>
    public void NavigateToFolder(UnpackFolderViewModel folder)
    {
        for (var parent = folder.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        folder.IsSelected = true;
        SelectedFolder = folder; // direct set too - the tree may not have realized the item yet
    }

    private void ShowFilterResults()
    {
        OnPropertyChanged(nameof(IsFiltering));
        if (_archive is null)
        {
            Files.Clear();
            return;
        }

        // The searched set and the displayed name depend on the scope: Current = this folder's
        // files by name, Recursive = this folder's subtree by folder-relative path, Global = the
        // whole package by full path. The match runs against the displayed name.
        var folder = _selectedFolder ?? _treeRoot;
        IEnumerable<(PackedEntry Entry, string Display)> candidates = SelectedSearchScope switch
        {
            ScopeCurrent when folder is not null =>
                folder.FileList.Select(e => (e, e.FileName)),
            ScopeRecursive when folder is not null =>
                folder.CollectEntries().Select(e => (e, RelativeTo(e, folder))),
            _ => _archive.Entries.Select(e => (e, e.Path)),
        };

        int matched = 0;
        var rows = new List<UnpackFileViewModel>();
        foreach (var (entry, display) in candidates)
        {
            if (!_filter.Matches(display))
                continue;
            matched++;
            if (rows.Count < MaxFilterResults)
                rows.Add(UnpackFileViewModel.ForEntry(this, entry, display));
        }
        SetFiles(rows);

        var scopeLabel = SelectedSearchScope switch
        {
            ScopeCurrent => $"in {FolderLabel(folder)}",
            ScopeRecursive => $"under {FolderLabel(folder)}",
            _ => "in the whole package",
        };
        FileListCaption = !_filter.RegexValid
            ? "Invalid regex pattern"
            : matched > Files.Count
                ? $"{matched:N0} matches {scopeLabel} - showing the first {Files.Count:N0}"
                : $"{matched:N0} match{(matched == 1 ? "" : "es")} {scopeLabel}";
        ExportSelectedCommand.RaiseCanExecuteChanged();

        static string FolderLabel(UnpackFolderViewModel? f) =>
            f is null || f.FullPath.Length == 0 ? "root" : f.FullPath;

        static string RelativeTo(PackedEntry entry, UnpackFolderViewModel folder) =>
            folder.FullPath.Length == 0 ? entry.Path : entry.Path[(folder.FullPath.Length + 1)..];
    }

    // --- Sorting (clickable column-header ribbon) -----------------------------------------------

    public UnpackSortColumn SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;

    /// <summary>Sort glyph shown in each header - up/down on the active column, blank otherwise.</summary>
    public string SortGlyphName => GlyphFor(UnpackSortColumn.Name);
    public string SortGlyphType => GlyphFor(UnpackSortColumn.Type);
    public string SortGlyphSource => GlyphFor(UnpackSortColumn.Source);
    public string SortGlyphSize => GlyphFor(UnpackSortColumn.Size);

    private string GlyphFor(UnpackSortColumn c) =>
        _sortColumn != c ? string.Empty : _sortAscending ? "▲" : "▼";

    /// <summary>Header click: sort by this column, toggling direction if it is already active.</summary>
    public void SortBy(UnpackSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            // Text columns feel natural ascending; size defaults to largest-first.
            _sortAscending = column != UnpackSortColumn.Size;
        }
        OnPropertyChanged(nameof(SortGlyphName));
        OnPropertyChanged(nameof(SortGlyphType));
        OnPropertyChanged(nameof(SortGlyphSource));
        OnPropertyChanged(nameof(SortGlyphSize));

        // Re-sort the rows already on screen in place (no need to rescan the archive).
        var rows = Files.ToList();
        SetFiles(rows);
    }

    /// <summary>Sorts <paramref name="rows"/> by the active column (folders always first) and
    /// replaces the visible list with them.</summary>
    private void SetFiles(List<UnpackFileViewModel> rows)
    {
        rows.Sort(CompareRows);
        Files.Clear();
        foreach (var row in rows)
            Files.Add(row);
        // The rebuilt rows start unhighlighted, so the button snaps back to acting on the folder.
        OnPropertyChanged(nameof(ExportButtonLabel));
        UpdateDetail();
    }

    private int CompareRows(UnpackFileViewModel a, UnpackFileViewModel b)
    {
        // Folders always group before files, regardless of column or direction.
        if (a.IsFolder != b.IsFolder)
            return a.IsFolder ? -1 : 1;

        int cmp = _sortColumn switch
        {
            UnpackSortColumn.Type => string.Compare(a.TypeKey, b.TypeKey, StringComparison.OrdinalIgnoreCase),
            UnpackSortColumn.Source => string.Compare(a.Source, b.Source, StringComparison.OrdinalIgnoreCase),
            UnpackSortColumn.Size => a.SizeKey.CompareTo(b.SizeKey),
            _ => 0,
        };
        // Name is the primary key for the Name column and the tie-break for every other column.
        if (cmp == 0)
            cmp = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        return _sortAscending ? cmp : -cmp;
    }

    // --- Go to file -----------------------------------------------------------------------------

    /// <summary>Context-menu "Go to file": for a folder row, navigate into it; for a file row
    /// (typically a search result), clear the search, open the folder that contains it, and select
    /// and scroll to the file there.</summary>
    public void GoToFile(UnpackFileViewModel row)
    {
        if (row.Folder is { } folder)
        {
            NavigateToFolder(folder);
            return;
        }
        if (row.Entry is not { } entry)
            return;

        var dir = FindFolder(entry.Directory);
        if (dir is null)
            return;

        FilterText = string.Empty; // back to folder browsing (stops any pending search)
        NavigateToFolder(dir);     // selecting the folder repopulates the list

        var target = Files.FirstOrDefault(f =>
            f.Entry is { } e && string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return;
        foreach (var f in Files)
            f.IsSelected = false;
        target.IsSelected = true;
        ScrollFileIntoView?.Invoke(target);
    }

    /// <summary>Walks the tree to the folder with the given archive-relative path, or null.</summary>
    private UnpackFolderViewModel? FindFolder(string dir)
    {
        var node = _treeRoot;
        if (node is null || dir.Length == 0)
            return node;
        foreach (var segment in dir.Split('/'))
        {
            node = node.Children.FirstOrDefault(c =>
                string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                return null;
        }
        return node;
    }

    // --- Export ---------------------------------------------------------------------------------

    private async Task ExportSelectedAsync()
    {
        // Highlighted rows: files directly, folder rows recursively (dedup in case a highlighted
        // folder also contains a highlighted file).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<PackedEntry>();
        int selectedRows = 0;
        foreach (var row in Files.Where(f => f.IsSelected))
        {
            selectedRows++;
            if (row.Folder is { } sub)
            {
                foreach (var entry in sub.CollectEntries())
                    if (seen.Add(entry.Path))
                        selected.Add(entry);
            }
            else if (row.Entry is { } entry && seen.Add(entry.Path))
            {
                selected.Add(entry);
            }
        }

        string what;
        if (selectedRows > 0)
        {
            what = $"{selected.Count:N0} file{(selected.Count == 1 ? "" : "s")} from {selectedRows:N0} selected row{(selectedRows == 1 ? "" : "s")}";
        }
        else if (SelectedFolder is { } folder)
        {
            // Nothing highlighted in the list: export the selected folder recursively.
            selected = folder.CollectEntries();
            what = folder.FullPath.Length == 0
                ? "the whole package"
                : $"folder \"{folder.FullPath}\" ({selected.Count:N0} files)";
        }
        else
        {
            return;
        }

        if (selected.Count == 0)
        {
            StatusMessage = "Nothing to export.";
            return;
        }
        await ExportEntriesAsync(selected, what);
    }

    /// <summary>Exports one folder from the tree (recursively). Driven by the tree's context menu;
    /// on the root node this exports the whole package.</summary>
    public async Task ExportFolderAsync(UnpackFolderViewModel folder)
    {
        if (!IsArchiveOpen || IsExporting || IsLoading)
            return;

        var entries = folder.CollectEntries();
        if (entries.Count == 0)
        {
            StatusMessage = "Nothing to export.";
            return;
        }

        string what = folder.FullPath.Length == 0
            ? "the whole package"
            : $"folder \"{folder.FullPath}\" ({entries.Count:N0} files)";
        await ExportEntriesAsync(entries, what);
    }

    private async Task ExportEntriesAsync(IReadOnlyList<PackedEntry> entries, string what)
    {
        if (_archive is null)
            return;

        // A large export (a big folder, or the whole-package root) can spill thousands of files -
        // confirm intent before prompting for a destination.
        if (entries.Count >= ExportConfirmThreshold && !ConfirmLargeExport(entries.Count, what))
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        string destRoot;
        if (SelectedExportMode == ExportModeBeside)
        {
            // Fixed location beside the opened package - no prompt.
            if (ResolveBesideExportRoot() is not { } besideRoot)
                return;
            destRoot = besideRoot;
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = $"Export {what} to..." };
            if (dlg.ShowDialog() != true)
                return;
            destRoot = Path.GetFullPath(dlg.FolderName);
        }

        _exportCancel = new CancellationTokenSource();
        var ct = _exportCancel.Token;
        IsExporting = true;
        ExportProgress = 0;
        Log($"=== Unpack: exporting {what} from {_archive.DisplayName} to {destRoot} ===");

        int done = 0, failed = 0, skipped = 0;
        try
        {
            var archive = _archive;
            var progress = new Progress<int>(count =>
            {
                ExportProgress = 100.0 * count / entries.Count;
                StatusMessage = $"Exporting... {count:N0} / {entries.Count:N0}";
            });

            await Task.Run(() =>
            {
                IProgress<int> report = progress;
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Zip-slip guard: the entry path must land inside the chosen folder.
                    string target;
                    try
                    {
                        target = Path.GetFullPath(Path.Combine(destRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                    }
                    catch (Exception)
                    {
                        Log($"  SKIPPED (bad path): {entry.Path}");
                        skipped++;
                        continue;
                    }
                    if (!target.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"  SKIPPED (escapes destination): {entry.Path}");
                        skipped++;
                        continue;
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var output = new FileStream(target, FileMode.Create, FileAccess.Write,
                                                          FileShare.None, 1 << 16);
                        archive.Extract(entry, output, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"  FAILED: {entry.Path} - {ex.Message}");
                        failed++;
                        continue;
                    }

                    done++;
                    if ((done & 31) == 0 || done == entries.Count)
                        report.Report(done);
                }
            }, ct);

            StatusMessage = $"Exported {done:N0} file{(done == 1 ? "" : "s")}"
                + (failed > 0 ? $", {failed} failed" : "")
                + (skipped > 0 ? $", {skipped} skipped" : "")
                + $" to {destRoot}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Export cancelled after {done:N0} file{(done == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            Log($"Unpack: export failed: {ex.Message}");
        }
        finally
        {
            // Whatever landed on disk (full export, partial, or cancelled) - surface any .mdl among it
            // in the "View model" picker.
            RegisterExportedModels(entries, destRoot);
            Log($"=== {StatusMessage} ===");
            IsExporting = false;
            ExportProgress = 0;
            _exportCancel.Dispose();
            _exportCancel = null;
        }
    }

    private void CancelExport()
    {
        _exportCancel?.Cancel();
        StatusMessage = "Cancelling export...";
    }

    /// <summary>Confirms a large export (see <see cref="ExportConfirmThreshold"/>) before it runs.</summary>
    private static bool ConfirmLargeExport(int count, string what)
    {
        var message = $"You're about to export {what}.\n\n"
            + $"This will write {count:N0} files to disk. Continue?";
        const string title = "Export many files";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    // --- Preview --------------------------------------------------------------------------------

    private static readonly string PreviewDir = Path.Combine(Path.GetTempPath(), "PulseWorkshop", "unpack-preview");

    /// <summary>Extracts the entry to the preview temp folder and returns the on-disk path
    /// (double-click hands this to the shell so the user's default app opens it), or null on
    /// failure.</summary>
    public async Task<string?> ExtractForPreviewAsync(PackedEntry entry)
    {
        if (_archive is null)
            return null;

        try
        {
            var archive = _archive;
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(PreviewDir);
                // Keep the real file name (the preview windows show it as the title) in a
                // per-extract folder so same-named files from different paths don't collide.
                var dir = Path.Combine(PreviewDir, Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, SanitizeFileName(entry.FileName));
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                    archive.Extract(entry, output);
                return target;
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>Reads an entry's bytes into memory (for the hover preview, which decodes images and
    /// .vtf textures directly rather than writing a temp file per hover). Capped at
    /// <paramref name="maxBytes"/> - entries larger than that return null so a hover never pulls a
    /// huge file into memory. Returns null on any read failure or cancellation.</summary>
    public async Task<byte[]?> ReadEntryBytesAsync(PackedEntry entry, int maxBytes, CancellationToken ct)
    {
        if (_archive is null || entry.Size > maxBytes)
            return null;
        var archive = _archive;
        try
        {
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream(entry.Size > 0 ? (int)entry.Size : 0);
                archive.Extract(entry, ms, ct);
                return ms.ToArray();
            }, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reports that the shell could not open a previewed file (no handler, launch error).</summary>
    public void ReportPreviewOpenFailed(PackedEntry entry, Exception ex)
    {
        StatusMessage = $"Couldn't open {entry.FileName}: {ex.Message}";
        Log($"Unpack: failed to open {entry.Path} with the default app: {ex.Message}");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "file" : name;
    }

    private static void CleanPreviewDir()
    {
        try
        {
            if (Directory.Exists(PreviewDir))
                Directory.Delete(PreviewDir, recursive: true);
        }
        catch
        {
            // A preview window may still hold a file open; %TEMP% cleanup gets the rest.
        }
    }

    // --- Model View bridge ----------------------------------------------------------------------

    /// <summary>Adds any .mdl among the just-exported entries that actually landed on disk to the
    /// "View model" picker (new paths only). Called after every export - full, partial, or cancelled.</summary>
    private void RegisterExportedModels(IReadOnlyList<PackedEntry> entries, string destRoot)
    {
        var added = false;
        foreach (var entry in entries)
        {
            if (!entry.Extension.Equals("mdl", StringComparison.OrdinalIgnoreCase))
                continue;

            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(destRoot,
                    entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                continue;
            }
            if (!File.Exists(target))
                continue; // skipped (zip-slip guard) or failed to write
            if (ModelFiles.Any(m => string.Equals(m.FilePath, target, StringComparison.OrdinalIgnoreCase)))
                continue; // already listed from an earlier export

            ModelFiles.Add(new UnpackedModelViewModel(entry.Path, target));
            added = true;
        }

        if (!added)
            return;
        SelectedModel ??= ModelFiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasModelFiles));
    }

    /// <summary>"View model": hands the picked exported .mdl's on-disk path to the Model View tab (its
    /// .vvd/.vtx/.phy siblings were written alongside it by the export), via
    /// <see cref="ViewModelRequested"/>.</summary>
    private void ViewSelectedModel()
    {
        if (_selectedModel is not { } model)
            return;
        if (!File.Exists(model.FilePath))
        {
            StatusMessage = $"{Path.GetFileName(model.FilePath)} is no longer on disk - export it again.";
            ModelFiles.Remove(model);
            SelectedModel = ModelFiles.FirstOrDefault();
            OnPropertyChanged(nameof(HasModelFiles));
            return;
        }
        StatusMessage = $"Opened {Path.GetFileName(model.FilePath)} in Model View.";
        Log($"Unpack: viewing exported model {model.FilePath} in Model View.");
        ViewModelRequested?.Invoke(model.FilePath);
    }

    /// <summary>"Browse file": opens the folder the picked exported .mdl lives in (Explorer, with the
    /// file selected).</summary>
    private void BrowseSelectedModel()
    {
        if (_selectedModel is not { } model)
            return;
        if (!File.Exists(model.FilePath))
        {
            StatusMessage = $"{Path.GetFileName(model.FilePath)} is no longer on disk - export it again.";
            ModelFiles.Remove(model);
            SelectedModel = ModelFiles.FirstOrDefault();
            OnPropertyChanged(nameof(HasModelFiles));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{model.FilePath}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the folder: {ex.Message}";
        }
    }

    // --- Helpers --------------------------------------------------------------------------------

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    private void Log(string line) => _console.Append(line);
}

/// <summary>A folder node in the Unpack tree. Immutable after the initial build+sort.</summary>
public sealed class UnpackFolderViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public UnpackFolderViewModel(string name, string fullPath, UnpackFolderViewModel? parent)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public string Name { get; }

    /// <summary>Archive-relative path with forward slashes; "" for the root node.</summary>
    public string FullPath { get; }

    /// <summary>The containing folder; null on the root node. Used to expand the tree down to a
    /// folder when a folder row is double-clicked.</summary>
    public UnpackFolderViewModel? Parent { get; }

    internal List<UnpackFolderViewModel> ChildList { get; } = new();
    internal List<Core.Unpack.PackedEntry> FileList { get; } = new();

    public IReadOnlyList<UnpackFolderViewModel> Children => ChildList;

    /// <summary>Files across this folder and all subfolders (shown next to the name).</summary>
    public int TotalFileCount { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Recursively sorts children/files by name and computes the rolled-up file counts.</summary>
    internal void SortAndCount()
    {
        ChildList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        FileList.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        int total = FileList.Count;
        foreach (var child in ChildList)
        {
            child.SortAndCount();
            total += child.TotalFileCount;
        }
        TotalFileCount = total;
    }

    /// <summary>All entries under this folder, recursively (for a folder export).</summary>
    internal List<Core.Unpack.PackedEntry> CollectEntries()
    {
        var result = new List<Core.Unpack.PackedEntry>();
        Collect(this, result);
        return result;

        static void Collect(UnpackFolderViewModel node, List<Core.Unpack.PackedEntry> into)
        {
            into.AddRange(node.FileList);
            foreach (var child in node.ChildList)
                Collect(child, into);
        }
    }
}

/// <summary>One row in the Unpack file list: a packed file, or (in folder view, Explorer-style)
/// one of the selected folder's subfolders.</summary>
public sealed class UnpackFileViewModel : ObservableObject
{
    private readonly UnpackViewModel _owner;
    private bool _isSelected;

    private UnpackFileViewModel(UnpackViewModel owner, Core.Unpack.PackedEntry? entry,
                                UnpackFolderViewModel? folder, string displayName)
    {
        _owner = owner;
        Entry = entry;
        Folder = folder;
        DisplayName = displayName;
    }

    public static UnpackFileViewModel ForEntry(UnpackViewModel owner, Core.Unpack.PackedEntry entry,
                                               string displayName) =>
        new(owner, entry, null, displayName);

    public static UnpackFileViewModel ForFolder(UnpackViewModel owner, UnpackFolderViewModel folder) =>
        new(owner, null, folder, folder.Name);

    /// <summary>The packed file; null for a folder row.</summary>
    public Core.Unpack.PackedEntry? Entry { get; }

    /// <summary>The subfolder; null for a file row.</summary>
    public UnpackFolderViewModel? Folder { get; }

    public bool IsFolder => Folder is not null;

    /// <summary>What kind of thumbnail the Details pane can show for this row, from its extension: a
    /// raster image WPF can decode, a .vtf texture (decoded by our lite reader), a Source 2 .vtex_c,
    /// or none.</summary>
    public UnpackPreviewKind PreviewKind => Folder is not null || Entry is null
        ? UnpackPreviewKind.None
        : Entry.Extension.ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "gif" or "tif" or "tiff" or "ico" => UnpackPreviewKind.Image,
            "vtf" => UnpackPreviewKind.Vtf,
            "vtex_c" => UnpackPreviewKind.Vtex,
            _ => UnpackPreviewKind.None,
        };

    /// <summary>File name in folder view; the scope-relative path in search results.</summary>
    public string DisplayName { get; }

    /// <summary>Extension chip ("VMT", "MDL", ...; "-" when there is none). Unused on folder rows
    /// (the template shows a folder glyph instead).</summary>
    public string TypeDisplay =>
        Entry?.Extension is { Length: > 0 } ext ? ext.ToUpperInvariant() : "-";

    /// <summary>Sort key for the Type column (folders collapse to "" and tie-break by name).</summary>
    public string TypeKey => Folder is not null ? string.Empty : Entry!.Extension;

    /// <summary>Sort key for the Size column: byte size for files, rolled-up count for folders
    /// (folders and files never compare - folders always sort first).</summary>
    public long SizeKey => Folder is { } f ? f.TotalFileCount : Entry!.Size;

    /// <summary>File size, or the folder's rolled-up file count.</summary>
    public string SizeDisplay => Folder is { } f
        ? $"{f.TotalFileCount:N0} file{(f.TotalFileCount == 1 ? "" : "s")}"
        : UnpackViewModel.FormatSize(Entry!.Size);

    /// <summary>The providing archive - meaningful for gameinfo mounts. Blank on folder rows.</summary>
    public string Source => Entry?.Source ?? string.Empty;

    public string ToolTipText => Folder is { } f
        ? $"{(f.FullPath.Length == 0 ? f.Name : f.FullPath)}\n{SizeDisplay} - double-click to open"
        : $"{Entry!.Path}\n{SizeDisplay} - from {Entry.Source}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                _owner.OnFileSelectionChanged();
        }
    }
}

/// <summary>One entry in the Unpack tab's "View model" picker: an exported .mdl, shown by its
/// archive-relative path and resolved to its on-disk location for the Model View tab.</summary>
public sealed class UnpackedModelViewModel
{
    public UnpackedModelViewModel(string displayPath, string filePath)
    {
        DisplayPath = displayPath;
        FilePath = filePath;
    }

    /// <summary>Archive-relative path shown in the dropdown (e.g. <c>models/foo.mdl</c>).</summary>
    public string DisplayPath { get; }

    /// <summary>Full on-disk path of the exported .mdl handed to the Model View tab.</summary>
    public string FilePath { get; }

    // The dark-theme ComboBox's selection box renders the item via ToString(), so return the path here
    // (DisplayMemberPath alone only reaches the dropdown items, not the closed selection box).
    public override string ToString() => DisplayPath;
}
