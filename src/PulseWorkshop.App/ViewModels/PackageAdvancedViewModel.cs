using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Package - Advanced tab: a project workflow that packs content folders into <c>.vpk</c>/<c>.gma</c>
/// via the game's packer. It shares the open <c>.pw_mdlproject</c> with the Compile tab through
/// <see cref="AdvancedProjectSession"/> but edits its own <see cref="ModelProject.PackageEntries"/>
/// list. Each entry first bakes its pre-assets into the folder (<see cref="AssetPipelineService"/>),
/// then runs the packer (<see cref="PackageService"/>). Output streams into the shared app-wide console.
/// </summary>
public sealed class PackageAdvancedViewModel : ObservableObject
{
    private readonly AdvancedProjectSession _session;
    private readonly ConsoleViewModel _console;
    private PackageEntryViewModel? _selectedEntry;
    private bool _isPackaging;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;

    public PackageAdvancedViewModel(AdvancedProjectSession session, ConsoleViewModel console)
    {
        _session = session;
        _console = console;

        AddEntryCommand = new RelayCommand(AddEntry, () => IsProjectOpen);
        PackageAllCommand = new AsyncRelayCommand(PackageAllAsync, () => CanPackageAll);
        PackageSelectedCommand = new AsyncRelayCommand(PackageSelectedAsync, () => CanPackageSelected);
        CancelCommand = new RelayCommand(Cancel, () => IsPackaging);

        // This tab persists the package entry list (with each entry's assets) into the shared project.
        _session.RegisterSync(() => _session.Project.PackageEntries =
            Entries.Select(e => { e.SyncAssets(); return e.Model; }).ToList());
        _session.ProjectChanged += OnProjectChanged;
        _session.GameChanged += OnGameChanged;

        OnProjectChanged();
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand NewProjectCommand => _session.NewProjectCommand;
    public RelayCommand OpenProjectCommand => _session.OpenProjectCommand;
    public RelayCommand CloseProjectCommand => _session.CloseProjectCommand;
    public RelayCommand AddEntryCommand { get; }
    public AsyncRelayCommand PackageAllCommand { get; }

    /// <summary>Packages the entries highlighted in the list (multi-select with Ctrl/Shift). Replaces
    /// the old per-entry "Package" button: a single-row selection behaves exactly like packaging one.</summary>
    public AsyncRelayCommand PackageSelectedCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The shared Game Setup roster used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _session.Games;

    /// <summary>The package entries, in package order. The UI lets the user drag to reorder.</summary>
    public ObservableCollection<PackageEntryViewModel> Entries { get; } = new();

    /// <summary>The entry shown in the editor panel (master-detail, like Game Setup).</summary>
    public PackageEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
                OnPropertyChanged(nameof(HasSelectedEntry));
        }
    }

    public bool HasSelectedEntry => _selectedEntry is not null;

    /// <summary>The asset-kind choices shared with every asset row.</summary>
    public IReadOnlyList<AssetKindChoice> AssetKinds { get; } = new[]
    {
        new AssetKindChoice(AssetKind.Text, "Text"),
        new AssetKindChoice(AssetKind.Image, "Image"),
    };

    /// <summary>The image-format choices shared with every image asset row.</summary>
    public IReadOnlyList<ImageFormatChoice> ImageFormats { get; } = new[]
    {
        new ImageFormatChoice(ImageTargetFormat.Copy, "Copy (keep format)"),
        new ImageFormatChoice(ImageTargetFormat.Png, "PNG"),
        new ImageFormatChoice(ImageTargetFormat.Jpg, "JPG"),
        new ImageFormatChoice(ImageTargetFormat.Gif, "GIF"),
        new ImageFormatChoice(ImageTargetFormat.Bmp, "BMP"),
        new ImageFormatChoice(ImageTargetFormat.Tiff, "TIFF"),
        new ImageFormatChoice(ImageTargetFormat.Vtf, "VTF (VTF tool)"),
    };

    // --- Project lifecycle (delegated to the shared session) ----------------------------------

    public bool IsProjectOpen => _session.IsProjectOpen;
    public string ProjectPath => _session.ProjectPath;
    public string ProjectName => _session.ProjectName;
    public string? ProjectDir => _session.ProjectDir;

    /// <summary>The recent-project list shown in the empty state's "Open recent" panel (shared session).</summary>
    public ObservableCollection<RecentItemViewModel> RecentProjects => _session.RecentProjects;

    private void OnProjectChanged()
    {
        Entries.Clear();
        foreach (var entry in _session.Project.PackageEntries)
            Entries.Add(new PackageEntryViewModel(this, entry));

        SelectedEntry = Entries.FirstOrDefault();
        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        StatusMessage = IsProjectOpen ? $"Opened {ProjectName}." : "No project open.";
    }

    private void OnGameChanged()
    {
        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(IsGameReady));
        RefreshCommands();
    }

    /// <summary>Persists the project (best-effort) via the shared session.</summary>
    public void Save() => _session.Save();

    /// <summary>Re-checks asset input files on disk across all entries (thumbnails + validation).
    /// Called when the main window is activated, so files created or deleted in another program are
    /// picked up without retyping the path.</summary>
    public void RefreshFileState()
    {
        foreach (var entry in Entries)
            entry.RefreshFileState();
    }

    public void RefreshCommands()
    {
        PackageAllCommand.RaiseCanExecuteChanged();
        PackageSelectedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddEntryCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by an entry when its list selection toggles, so "Package selected" re-evaluates.</summary>
    public void OnEntrySelectionChanged() => PackageSelectedCommand.RaiseCanExecuteChanged();

    /// <summary>Raised when an entry's "View Package" is clicked: the packed file to hand to the Unpack
    /// tab. Wired by <c>MainViewModel</c> to open it there and switch tabs.</summary>
    public event Action<string>? ViewPackageRequested;

    /// <summary>Forwards an entry's "View Package" request to the Unpack tab.</summary>
    public void ViewPackage(string packagePath) => ViewPackageRequested?.Invoke(packagePath);

    /// <summary>The Unpack tab, set by <c>MainViewModel</c>: before packing an entry, the pack asks
    /// it to temporarily release the output package if it happens to be open there (its read handles
    /// would block the packer), then reopens it once the pack finishes.</summary>
    public UnpackViewModel? UnpackTab { get; set; }

    // --- Project-level bound state -------------------------------------------------------------

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _session.SelectedGame;
        set => _session.SelectedGame = value;
    }

    /// <summary>The resolved packer (vpk/gmad) path, or null.</summary>
    private string? PackerToolPath
    {
        get
        {
            var p = SelectedGame?.PackerTool.ResolvedPath;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
    }

    /// <summary>True when the selected game has a usable packer tool.</summary>
    public bool IsGameReady
    {
        get
        {
            var packer = PackerToolPath;
            return !string.IsNullOrWhiteSpace(packer) && File.Exists(packer);
        }
    }

    public bool IsPackaging
    {
        get => _isPackaging;
        private set { if (SetField(ref _isPackaging, value)) RefreshCommands(); }
    }

    public bool CanPackageAll =>
        IsProjectOpen && !IsPackaging && IsGameReady && Entries.Any(e => e.IncludeInAll);

    public bool CanPackageSelected =>
        IsProjectOpen && !IsPackaging && IsGameReady && Entries.Any(e => e.IsSelected);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    // --- Path helpers (used by entries / assets) ----------------------------------------------

    public string ResolveAgainstProject(string path) => _session.ResolveAgainstProject(path);
    public string MakeProjectRelative(string fullPath) => _session.MakeProjectRelative(fullPath);

    // --- Entries ------------------------------------------------------------------------------

    private void AddEntry()
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder to package" };
        if (ProjectDir is { Length: > 0 } pdir && Directory.Exists(pdir))
            dlg.InitialDirectory = pdir;
        if (dlg.ShowDialog() != true)
            return;

        var model = new PackageEntry
        {
            Name = Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/')),
            FolderPath = MakeProjectRelative(dlg.FolderName),
        };
        var vm = new PackageEntryViewModel(this, model);
        Entries.Add(vm);
        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    public void CloneEntry(PackageEntryViewModel entry)
    {
        entry.SyncAssets();
        var clone = entry.Model.Clone();
        clone.Name = string.IsNullOrWhiteSpace(entry.Model.Name)
            ? "Copy"
            : entry.Model.Name + " (copy)";

        var index = Entries.IndexOf(entry);
        var vm = new PackageEntryViewModel(this, clone);
        if (index >= 0)
            Entries.Insert(index + 1, vm);
        else
            Entries.Add(vm);

        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    public void RemoveEntry(PackageEntryViewModel entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "this entry" : $"\"{entry.Name}\"";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, $"Remove {name} from the project?", "Remove entry",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show($"Remove {name} from the project?", "Remove entry",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            return;

        var index = Entries.IndexOf(entry);
        Entries.Remove(entry);
        if (ReferenceEquals(SelectedEntry, entry))
            SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
        Save();
        RefreshCommands();
    }

    /// <summary>Context-menu "Duplicate selected": clones every highlighted entry, inserting each copy
    /// after its source, then moves the highlight onto the new copies. One row behaves like its Clone.</summary>
    public void CloneSelectedEntries()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var clones = new List<PackageEntryViewModel>();
        foreach (var entry in selected)
        {
            entry.SyncAssets();
            var clone = entry.Model.Clone();
            clone.Name = string.IsNullOrWhiteSpace(entry.Model.Name)
                ? "Copy"
                : entry.Model.Name + " (copy)";

            var index = Entries.IndexOf(entry);
            var vm = new PackageEntryViewModel(this, clone);
            if (index >= 0)
                Entries.Insert(index + 1, vm);
            else
                Entries.Add(vm);
            clones.Add(vm);
        }

        // Move the selection onto the new copies (the SelectedItem binding follows to the first).
        foreach (var entry in selected)
            entry.IsSelected = false;
        foreach (var clone in clones)
            clone.IsSelected = true;
        SelectedEntry = clones[^1];

        Save();
        RefreshCommands();
    }

    /// <summary>Context-menu "Delete selected": removes every highlighted entry after one confirmation.</summary>
    public void RemoveSelectedEntries()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var what = selected.Count == 1
            ? (string.IsNullOrWhiteSpace(selected[0].Name) ? "this entry" : $"\"{selected[0].Name}\"")
            : $"{selected.Count} entries";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, $"Remove {what} from the project?", "Remove entries",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show($"Remove {what} from the project?", "Remove entries",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            return;

        var firstIndex = Entries.IndexOf(selected[0]);
        var selectionAffected = selected.Any(e => ReferenceEquals(SelectedEntry, e));
        foreach (var entry in selected)
            Entries.Remove(entry);
        if (selectionAffected)
            SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(firstIndex, Entries.Count - 1)];
        Save();
        RefreshCommands();
    }

    // --- Package ------------------------------------------------------------------------------

    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private async Task PackageAllAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Entries.Where(e => e.IncludeInAll).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No entries flagged for 'Package all'.";
            return;
        }
        if (!ConfirmBatch("Package all", targets.Count))
            return;

        await PackageBatchAsync(targets, "Package all");
    }

    private async Task PackageSelectedAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Entries.Where(e => e.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No entries selected.";
            return;
        }

        // A single-row selection packages straight away (like the old per-entry button); only a
        // multi-entry batch asks first.
        if (targets.Count > 1 && !ConfirmBatch("Package selected", targets.Count))
            return;

        await PackageBatchAsync(targets, "Package selected");
    }

    /// <summary>Packages a set of entries in order, streaming progress and a final tally to the console.
    /// Used by both "Package all" and "Package selected".</summary>
    private async Task PackageBatchAsync(IReadOnlyList<PackageEntryViewModel> targets, string label)
    {
        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsPackaging = true;
        Log($"=== {label} ({targets.Count} entr{(targets.Count == 1 ? "y" : "ies")}) ===");
        try
        {
            var ok = 0;
            var cancelled = false;
            foreach (var entry in targets)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                entry.IsPackaging = true;
                try
                {
                    if (await PackageOneAsync(entry, ct))
                        ok++;
                }
                finally
                {
                    entry.IsPackaging = false;
                }

                cancelled = ct.IsCancellationRequested;
                if (cancelled) break;
            }
            StatusMessage = cancelled
                ? $"{label} cancelled: {ok}/{targets.Count} done."
                : $"{label} done: {ok}/{targets.Count} succeeded.";
            Log($"=== {StatusMessage} ===");
        }
        finally
        {
            IsPackaging = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
    }

    private bool ConfirmBatch(string title, int count)
    {
        var message = $"{title}: run {count} entr{(count == 1 ? "y" : "ies")} in order?";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Bakes an entry's assets into its folder, then packs the folder. Returns success.</summary>
    private async Task<bool> PackageOneAsync(PackageEntryViewModel entry, CancellationToken ct)
    {
        var folder = entry.ResolvedFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            entry.HasError = true;
            StatusMessage = $"{entry.Name}: folder not found.";
            Log($"FAILED: folder not found ({folder}).");
            return false;
        }

        StatusMessage = $"Packaging {entry.Name}...";
        Log($"=== Packaging {entry.Name} ({folder}) ===");
        try
        {
            entry.SyncAssets();

            // 1. Pre-assets: transform + copy into the folder (sources are never mutated).
            if (entry.Model.Assets.Count > 0)
            {
                Log("--- Pre-assets ---");
                var pipeline = new AssetPipelineService();
                pipeline.Output += Log;
                var vtf = new AssetPipelineService.VtfToolConfig(
                    SelectedGame?.VtfTool.ResolvedPath, SelectedGame?.VtfToolCommand);
                bool assetsOk;
                try
                {
                    assetsOk = await pipeline.ApplyAsync(folder, entry.Model.Assets,
                        ResolveAgainstProject, vtf, ct);
                }
                finally
                {
                    pipeline.Output -= Log;
                }
                await FlushLogAsync();
                if (!assetsOk)
                {
                    entry.HasError = true;
                    StatusMessage = $"{entry.Name}: one or more assets failed.";
                    Log("=== Stopped: asset processing failed. ===");
                    return false;
                }
            }

            // 2. Pack the folder.
            // If the Unpack tab is browsing the very package this pack will overwrite, its open
            // read handles would block the packer (and the rename) - close it there for the duration.
            var packerExt = Path.GetFileNameWithoutExtension(PackerToolPath ?? string.Empty)
                .Contains("gmad", StringComparison.OrdinalIgnoreCase) ? ".gma" : ".vpk";
            string? reopenInUnpack = UnpackTab?.ReleaseForRepack(
                PackageViewModel.PackOutputCandidates(folder, packerExt, entry.Model.OutputName));
            string? packedPath = null;

            var service = new PackageService();
            service.Output += Log;
            try
            {
                var request = new PackageRequest(
                    PackerToolPath: PackerToolPath ?? string.Empty,
                    FolderPath: folder,
                    ExtraOptions: entry.Command);
                var result = await service.PackageAsync(request, ct);
                await FlushLogAsync();

                if (!result.Success)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Log("=== Cancelled ===");
                        return false;
                    }
                    entry.HasError = true;
                    StatusMessage = $"{entry.Name}: {result.Error}";
                    Log($"FAILED: {result.Error}");
                    return false;
                }

                var finalPath = RenamePackage(result.OutputPackagePath, entry.Model.OutputName);
                packedPath = finalPath;

                entry.HasError = false;
                entry.LastPackagePath = finalPath;
                StatusMessage = $"Packaged {entry.Name} -> {Path.GetFileName(finalPath)}.";
                Log($"=== Done. {StatusMessage} ===");
                return true;
            }
            finally
            {
                service.Output -= Log;

                // Reopen the package in the Unpack tab if we closed it there: the fresh result
                // when the pack succeeded, the old file otherwise (if it survived).
                if (reopenInUnpack is not null)
                {
                    var reopen = packedPath ?? reopenInUnpack;
                    if (File.Exists(reopen))
                        await UnpackTab!.OpenFromPathAsync(reopen);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("=== Cancelled ===");
            return false;
        }
        catch (Exception ex)
        {
            entry.HasError = true;
            StatusMessage = $"{entry.Name}: {ex.Message}";
            Log($"ERROR: {ex.Message}");
            return false;
        }
    }

    /// <summary>Renames a freshly-produced package to the entry's <c>OutputName</c> (in the same
    /// folder), keeping the packer's extension when none is given and overwriting any existing file of
    /// that name. Returns the resulting path (unchanged when <paramref name="outputName"/> is blank or
    /// the produced path is missing). Any directory parts in the name are stripped so it can't escape
    /// the output folder.</summary>
    private string? RenamePackage(string? producedPath, string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName)
            || string.IsNullOrEmpty(producedPath) || !File.Exists(producedPath))
            return producedPath;

        var name = Path.GetFileName(outputName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return producedPath;

        if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += Path.GetExtension(producedPath); // keep the packer's .vpk/.gma

        var dir = Path.GetDirectoryName(producedPath) ?? string.Empty;
        var target = Path.Combine(dir, name);

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(producedPath),
                StringComparison.OrdinalIgnoreCase))
            return producedPath; // already the desired name

        try
        {
            File.Move(producedPath, target, overwrite: true);
            Log($"Renamed package -> {name}");
            return target;
        }
        catch (Exception ex)
        {
            Log($"WARNING: could not rename package to '{name}': {ex.Message}");
            return producedPath;
        }
    }

    // --- Shared console (packer / asset output) -----------------------------------------------

    // Output arrives one line at a time on background threads, in bursts; the shared console buffers
    // and coalesces them, so we just forward each line.
    private void Log(string line) => _console.Append(line);

    // Ensures queued background output is on screen before we write our own marker lines.
    private Task FlushLogAsync() => _console.FlushAsync();
}

/// <summary>An asset-kind dropdown entry (the themed ComboBox renders via ToString).</summary>
public sealed record AssetKindChoice(AssetKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An image-format dropdown entry (the themed ComboBox renders via ToString).</summary>
public sealed record ImageFormatChoice(ImageTargetFormat Format, string Label)
{
    public override string ToString() => Label;
}
