using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;
using PulseWorkshop.Core.Storage;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Compile - Advanced tab: a project-directory workflow on top of the same studiomdl pipeline as
/// the Simple tab. Project state (path, game, save) lives in the shared <see cref="AdvancedProjectSession"/>
/// so the Package tab can edit the same <c>.pw_mdlproject</c> without clobbering it; this view model
/// owns only the compile-specific concerns: the model-entry list, the project's compile options, and
/// the compile run. Output streams into the shared app-wide console. Reuses
/// <see cref="ModelCompileService"/> and <see cref="MaterialCopyService"/>.
/// </summary>
public sealed class CompileAdvancedViewModel : ObservableObject
{
    private readonly AdvancedProjectSession _session;
    private readonly ConsoleViewModel _console;
    private readonly UiSettings _settings;
    private readonly string _modelToolPath;

    private ModelEntryViewModel? _selectedEntry;
    private bool _isCompiling;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;
    private string? _selectedMaterialGameRoot;

    public CompileAdvancedViewModel(AdvancedProjectSession session, ConsoleViewModel console, UiSettings settings)
    {
        _session = session;
        _console = console;
        _settings = settings;
        _modelToolPath = ToolLocator.ResolveModelToolPath();

        AddEntryCommand = new RelayCommand(AddEntry, () => IsProjectOpen);
        CompileAllCommand = new AsyncRelayCommand(CompileAllAsync, () => CanCompileAll);
        CompileSelectedCommand = new AsyncRelayCommand(CompileSelectedAsync, () => CanCompileSelected);
        CancelCommand = new RelayCommand(Cancel, () => IsCompiling);

        // This tab persists the compile entry list into the shared project on every save.
        _session.RegisterSync(() => _session.Project.Entries = Entries.Select(e => e.Model).ToList());
        _session.ProjectChanged += OnProjectChanged;
        _session.GameChanged += OnGameChanged;

        // Pick up a project the session reopened before this view model existed.
        OnProjectChanged();
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand NewProjectCommand => _session.NewProjectCommand;
    public RelayCommand OpenProjectCommand => _session.OpenProjectCommand;
    public RelayCommand CloseProjectCommand => _session.CloseProjectCommand;
    public RelayCommand AddEntryCommand { get; }
    public AsyncRelayCommand CompileAllCommand { get; }

    /// <summary>Compiles the entries highlighted in the list (multi-select with Ctrl/Shift). Replaces
    /// the old per-entry "Compile" button: a single-row selection behaves exactly like compiling one.</summary>
    public AsyncRelayCommand CompileSelectedCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The shared Game Setup roster (game name + resolved tool paths) used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _session.Games;

    /// <summary>The model entries, in compile order. The UI lets the user drag to reorder.</summary>
    public ObservableCollection<ModelEntryViewModel> Entries { get; } = new();

    /// <summary>The entry shown in the editor panel (master-detail, like Game Setup / Package).</summary>
    public ModelEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
                OnPropertyChanged(nameof(HasSelectedEntry));
        }
    }

    public bool HasSelectedEntry => _selectedEntry is not null;

    /// <summary>Output-destination choices, shared with every entry so ComboBox selection matches.
    /// A Subfolder name may itself be a nested path (e.g. <c>test/bill</c>), so a separate work-folder
    /// mode is unnecessary.</summary>
    public IReadOnlyList<OutputModeChoice> OutputModes { get; } = new[]
    {
        new OutputModeChoice(CompileOutputMode.Subfolder, "Subfolder (under project)"),
        new OutputModeChoice(CompileOutputMode.LeaveInGame, "Compile in game (don't move)"),
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
        foreach (var entry in _session.Project.Entries)
            Entries.Add(new ModelEntryViewModel(this, entry));

        SelectedEntry = Entries.FirstOrDefault();
        RefreshMaterialGameRoots();
        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        StatusMessage = IsProjectOpen ? $"Opened {ProjectName}." : "No project open.";
    }

    private void OnGameChanged()
    {
        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(IsGameReady));
        RefreshMaterialGameRoots();
        RefreshEntryCommandPreviews();
        RefreshCommands();
    }

    /// <summary>Persists the project (best-effort), rebuilding entry order from the UI via the session.</summary>
    public void Save() => _session.Save();

    /// <summary>Re-evaluates the can-execute state of every compile command.</summary>
    public void RefreshCommands()
    {
        CompileAllCommand.RaiseCanExecuteChanged();
        CompileSelectedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddEntryCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by an entry when its list selection toggles, so "Compile selected" re-evaluates.</summary>
    public void OnEntrySelectionChanged() => CompileSelectedCommand.RaiseCanExecuteChanged();

    /// <summary>Raised when an entry's "View Model" is clicked: the compiled .mdl path to hand to the
    /// Model View tab. Wired by <c>MainViewModel</c> to assign the path and switch tabs.</summary>
    public event Action<string>? ViewModelRequested;

    /// <summary>Sends an entry's last compiled .mdl to the Model View tab (without launching the viewer).</summary>
    public void RequestViewModel(string mdlPath)
    {
        if (!string.IsNullOrEmpty(mdlPath))
            ViewModelRequested?.Invoke(mdlPath);
    }

    // --- Project-level bound state -------------------------------------------------------------

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _session.SelectedGame;
        set => _session.SelectedGame = value;
    }

    public string GlobalCommand
    {
        get => _session.Project.GlobalCommand;
        set
        {
            if (_session.Project.GlobalCommand != (value ?? string.Empty))
            {
                _session.Project.GlobalCommand = value ?? string.Empty;
                OnPropertyChanged();
                RefreshEntryCommandPreviews();
                Save();
            }
        }
    }

    /// <summary>Builds the read-only studiomdl command-line preview for one entry (the global command
    /// plus that entry's command), mirroring the Simple tab. Shown above the entry's model info.</summary>
    public string BuildEntryCommandPreview(ModelEntryViewModel entry)
    {
        var studio = SelectedGame?.ModelCompiler.ResolvedPath;
        var gameInfoDir = GameInfoDir;
        if (string.IsNullOrWhiteSpace(studio) || string.IsNullOrWhiteSpace(gameInfoDir)
            || string.IsNullOrWhiteSpace(entry.QcPath))
            return "Select a game, gameinfo.txt, and a .qc to preview the command.";

        var extra = ModelCompileService.CombineOptions(SelectedGame?.ModelCompilerCommand, GlobalCommand, entry.Command);
        return $"\"{studio}\" {ModelCompileService.BuildArguments(gameInfoDir, entry.ResolvedQcPath, extra)}";
    }

    /// <summary>Re-raises every entry's command preview - used when a project-level input the preview
    /// depends on (global command, selected game) changes.</summary>
    private void RefreshEntryCommandPreviews()
    {
        foreach (var entry in Entries)
            entry.RaiseCommandPreviewChanged();
    }

    public bool GetMaterialOnCompile
    {
        get => _session.Project.GetMaterialOnCompile;
        set
        {
            if (_session.Project.GetMaterialOnCompile != value)
            {
                _session.Project.GetMaterialOnCompile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfigureMaterials));
                OnPropertyChanged(nameof(CanEditMaterialsDir));
                Save();
            }
        }
    }

    public bool LocalizeMaterials
    {
        get => _session.Project.LocalizeMaterials;
        set { if (_session.Project.LocalizeMaterials != value) { _session.Project.LocalizeMaterials = value; OnPropertyChanged(); Save(); } }
    }

    public bool FlatPatchShader
    {
        get => _session.Project.FlatPatchShader;
        set { if (_session.Project.FlatPatchShader != value) { _session.Project.FlatPatchShader = value; OnPropertyChanged(); Save(); } }
    }

    public bool CleanBeforeTransfer
    {
        get => _session.Project.CleanBeforeTransfer;
        set { if (_session.Project.CleanBeforeTransfer != value) { _session.Project.CleanBeforeTransfer = value; OnPropertyChanged(); Save(); } }
    }

    /// <summary>App-wide toggle (shared with Compile - Simple): copy each entry's compiled files to its
    /// output folder instead of moving them, leaving the just-built model in the game folder too. No
    /// effect on the "compile in game" output mode (nothing is transferred there). Stored in the shared
    /// UI settings, not the project, so it applies to every project.</summary>
    public bool CopyToDestination
    {
        get => _settings.CompileCopyToDestination;
        set
        {
            if (_settings.CompileCopyToDestination != value)
            {
                _settings.CompileCopyToDestination = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>When true, an in-game compile also creates the model's material folders (from its
    /// <c>$cdmaterials</c> paths) under the game's <c>materials/</c>. Non-in-game compiles do nothing.</summary>
    public bool MakeMaterialDir
    {
        get => _session.Project.MakeMaterialDir;
        set { if (_session.Project.MakeMaterialDir != value) { _session.Project.MakeMaterialDir = value; OnPropertyChanged(); Save(); } }
    }

    /// <summary>The game content roots gameinfo.txt mounts (full paths, engine priority order). The
    /// "Make material's directory" dropdown picks which one the model's folders are created under.</summary>
    public ObservableCollection<string> MaterialGameRoots { get; } = new();

    /// <summary>The chosen game root for <see cref="MakeMaterialDir"/> (bound to the dropdown, saved to
    /// the project). Falls back to the first root when the saved choice is no longer available.</summary>
    public string? SelectedMaterialGameRoot
    {
        get => _selectedMaterialGameRoot;
        set
        {
            if (SetField(ref _selectedMaterialGameRoot, value))
            {
                _session.Project.MaterialDirGameRoot = value ?? string.Empty;
                Save();
            }
        }
    }

    /// <summary>Rebuilds <see cref="MaterialGameRoots"/> from the selected game's gameinfo.txt and
    /// restores the saved selection (or the first root if the saved one is gone). Called when the game
    /// or project changes.</summary>
    private void RefreshMaterialGameRoots()
    {
        MaterialGameRoots.Clear();
        var gameInfoPath = SelectedGame?.GameInfo.ResolvedPath;
        if (!string.IsNullOrWhiteSpace(gameInfoPath) && File.Exists(gameInfoPath))
            foreach (var root in GameInfoMount.GetGameRoots(gameInfoPath))
                MaterialGameRoots.Add(root);

        var saved = _session.Project.MaterialDirGameRoot;
        var match = MaterialGameRoots.FirstOrDefault(
            r => string.Equals(r, saved, StringComparison.OrdinalIgnoreCase));
        // Setting the property persists the fallback when the saved root is gone.
        SelectedMaterialGameRoot = match ?? MaterialGameRoots.FirstOrDefault();
    }

    /// <summary>When true, the materials/ folder is written to <see cref="MaterialsOutputDir"/> (under the
    /// project root) instead of beside the compiled models.</summary>
    public bool UseCustomMaterialsDir
    {
        get => _session.Project.UseCustomMaterialsDir;
        set
        {
            if (_session.Project.UseCustomMaterialsDir != value)
            {
                _session.Project.UseCustomMaterialsDir = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditMaterialsDir));
                Save();
            }
        }
    }

    /// <summary>The materials destination folder, always relative to the project root (absolute or
    /// outside-project paths are rejected when copying). Empty means the project root itself.</summary>
    public string MaterialsOutputDir
    {
        get => _session.Project.MaterialsOutputDir;
        set
        {
            var v = value ?? string.Empty;
            if (_session.Project.MaterialsOutputDir != v)
            {
                _session.Project.MaterialsOutputDir = v;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>True when material gathering is enabled - controls the Localize and Flat patch options.</summary>
    public bool CanConfigureMaterials => GetMaterialOnCompile;

    /// <summary>True when the materials folder text input is editable (materials on + custom folder on).</summary>
    public bool CanEditMaterialsDir => GetMaterialOnCompile && UseCustomMaterialsDir;

    // --- Validation ---------------------------------------------------------------------------

    /// <summary>The resolved gameinfo.txt directory (the studiomdl <c>-game</c> argument), or null.</summary>
    private string? GameInfoDir
    {
        get
        {
            var gameInfo = SelectedGame?.GameInfo.ResolvedPath;
            return string.IsNullOrWhiteSpace(gameInfo) ? null : Path.GetDirectoryName(gameInfo);
        }
    }

    /// <summary>True when the selected game has a usable compiler + gameinfo.txt directory.</summary>
    public bool IsGameReady
    {
        get
        {
            var studio = SelectedGame?.ModelCompiler.ResolvedPath;
            var gameInfoDir = GameInfoDir;
            return !string.IsNullOrWhiteSpace(studio) && File.Exists(studio)
                && !string.IsNullOrWhiteSpace(gameInfoDir) && Directory.Exists(gameInfoDir);
        }
    }

    public bool IsCompiling
    {
        get => _isCompiling;
        private set
        {
            if (SetField(ref _isCompiling, value))
                RefreshCommands();
        }
    }

    public bool CanCompileAll =>
        IsProjectOpen && !IsCompiling && IsGameReady && Entries.Any(e => e.CompileInAll);

    public bool CanCompileSelected =>
        IsProjectOpen && !IsCompiling && IsGameReady && Entries.Any(e => e.IsSelected);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    // --- Path helpers (used by entries; delegated to the session) -----------------------------

    /// <summary>Resolves a stored (relative or absolute) path against the project folder.</summary>
    public string ResolveAgainstProject(string path) => _session.ResolveAgainstProject(path);

    /// <summary>Stores a picked path relative to the project when it sits under it; else absolute.</summary>
    public string MakeProjectRelative(string fullPath) => _session.MakeProjectRelative(fullPath);

    // --- Entries ------------------------------------------------------------------------------

    private void AddEntry()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose QC file",
            Filter = "QC file (*.qc)|*.qc|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (ProjectDir is { Length: > 0 } pdir && Directory.Exists(pdir))
            dlg.InitialDirectory = pdir;
        if (dlg.ShowDialog() != true)
            return;

        var model = new ModelEntry
        {
            Name = Path.GetFileNameWithoutExtension(dlg.FileName),
            QcPath = MakeProjectRelative(dlg.FileName),
            SubfolderName = DefaultSubfolderName,
        };
        var vm = new ModelEntryViewModel(this, model);
        Entries.Add(vm);
        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    /// <summary>Inserts a copy of an entry right after it (fresh id, name suffixed " (copy)").</summary>
    public void CloneEntry(ModelEntryViewModel entry)
    {
        var clone = entry.Model.Clone();
        clone.Name = string.IsNullOrWhiteSpace(entry.Model.Name)
            ? "Copy"
            : entry.Model.Name + " (copy)";

        var index = Entries.IndexOf(entry);
        var vm = new ModelEntryViewModel(this, clone);
        if (index >= 0)
            Entries.Insert(index + 1, vm);
        else
            Entries.Add(vm);

        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    public void RemoveEntry(ModelEntryViewModel entry)
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

        var clones = new List<ModelEntryViewModel>();
        foreach (var entry in selected)
        {
            var clone = entry.Model.Clone();
            clone.Name = string.IsNullOrWhiteSpace(entry.Model.Name)
                ? "Copy"
                : entry.Model.Name + " (copy)";

            var index = Entries.IndexOf(entry);
            var vm = new ModelEntryViewModel(this, clone);
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

    // --- Compile ------------------------------------------------------------------------------

    /// <summary>Requests cancellation of the in-progress compile (single or batch).</summary>
    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private async Task CompileAllAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Entries.Where(e => e.CompileInAll).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No entries flagged for 'Compile all'.";
            return;
        }

        if (!ConfirmBatch("Compile all", targets.Count))
            return;

        await CompileBatchAsync(targets, "Compile all");
    }

    private async Task CompileSelectedAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Entries.Where(e => e.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No entries selected.";
            return;
        }

        // A single-row selection compiles straight away (like the old per-entry button); only a
        // multi-entry batch asks first.
        if (targets.Count > 1 && !ConfirmBatch("Compile selected", targets.Count))
            return;

        await CompileBatchAsync(targets, "Compile selected");
    }

    /// <summary>Compiles a set of entries in order, streaming progress and a final tally to the console.
    /// Used by both "Compile all" and "Compile selected".</summary>
    private async Task CompileBatchAsync(IReadOnlyList<ModelEntryViewModel> targets, string label)
    {
        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsCompiling = true;
        Log($"=== {label} ({targets.Count} entr{(targets.Count == 1 ? "y" : "ies")}) ===");
        try
        {
            var ok = 0;
            var cancelled = false;
            foreach (var entry in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                entry.IsCompiling = true;
                try
                {
                    if (await CompileOneAsync(entry, ct))
                        ok++;
                }
                finally
                {
                    entry.IsCompiling = false;
                }

                cancelled = ct.IsCancellationRequested;
                if (cancelled)
                    break;
            }
            StatusMessage = cancelled
                ? $"{label} cancelled: {ok}/{targets.Count} done."
                : $"{label} done: {ok}/{targets.Count} succeeded.";
            Log($"=== {StatusMessage} ===");
        }
        finally
        {
            IsCompiling = false;
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

    /// <summary>Compiles a single entry end-to-end (studiomdl + optional material copy). Returns success.</summary>
    private async Task<bool> CompileOneAsync(ModelEntryViewModel entry, CancellationToken ct)
    {
        if (GameInfoDir is not { } gameInfoDir)
            return false;

        var qc = entry.ResolvedQcPath;
        var destination = ResolveDestination(entry);
        var request = new CompileRequest(
            StudioMdlPath: SelectedGame!.ModelCompiler.ResolvedPath ?? string.Empty,
            GameInfoDir: gameInfoDir,
            QcPath: qc,
            ExtraOptions: ModelCompileService.CombineOptions(SelectedGame?.ModelCompilerCommand, GlobalCommand, entry.Command),
            DestinationBase: destination,
            CleanBeforeTransfer: CleanBeforeTransfer,
            CopyToDestination: CopyToDestination);

        var service = new ModelCompileService();
        service.Output += Log;
        StatusMessage = $"Compiling {entry.Name}...";
        Log($"=== Compiling {entry.Name} ({qc}) ===");
        try
        {
            var result = await service.CompileAsync(request, ct);
            await FlushLogAsync();

            if (!result.Success)
            {
                // A cancel isn't a compile error - leave the entry's outline as it was.
                if (ct.IsCancellationRequested)
                {
                    Log("=== Cancelled ===");
                    return false;
                }
                entry.HasError = true;
                entry.MdlInfo = null;
                StatusMessage = $"{entry.Name}: {result.Error}";
                Log($"FAILED: {result.Error}");
                return false;
            }

            entry.HasError = false;
            var copiedNote = destination is null
                ? "left in game folder"
                : $"{result.CopiedFiles.Count} file(s) -> {destination}";
            StatusMessage = $"Compiled {entry.Name} - {copiedNote}.";
            Log($"=== Done. {StatusMessage} ===");

            entry.LastMdlPath = result.CopiedFiles.FirstOrDefault(f =>
                                    f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                                ?? result.CompiledMdls.FirstOrDefault();

            // Same trigger as the entry's "Go to file": read the compiled .mdl's stats for the panel.
            await ScanMdlInfoAsync(entry, ct);

            if (GetMaterialOnCompile && result.CompiledMdls.Count > 0)
            {
                // After the move the in-game .mdl is gone, so read the moved copy at the destination.
                var matMdls = destination is null
                    ? result.CompiledMdls
                    : result.CopiedFiles
                        .Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList();
                await RunMaterialCopyAsync(matMdls, ResolveMaterialsDestination(destination), ct);
            }

            // "Make material's directory": in-game compiles only (destination is null). Creates the
            // model's $cdmaterials folders under the game's materials/ so a fresh model's textures
            // have a home, then lights up the entry's "Go to Materials" button.
            if (MakeMaterialDir && destination is null && entry.LastMdlPath is { Length: > 0 })
                await MakeMaterialDirsAsync(entry, ct);
            return true;
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
        finally
        {
            service.Output -= Log;
        }
    }

    /// <summary>The folder an entry's compiled files are moved to, or null for "compile in game".</summary>
    private string? ResolveDestination(ModelEntryViewModel entry)
    {
        var projectDir = ProjectDir;
        var m = entry.Model;
        return m.OutputMode switch
        {
            CompileOutputMode.Subfolder when !string.IsNullOrWhiteSpace(m.SubfolderName) && projectDir is not null =>
                Path.Combine(projectDir, m.SubfolderName),
            CompileOutputMode.WorkFolder when !string.IsNullOrWhiteSpace(m.OutputDir) =>
                Path.IsPathRooted(m.OutputDir)
                    ? m.OutputDir
                    : projectDir is null ? null : Path.Combine(projectDir, m.OutputDir),
            _ => null,
        };
    }

    /// <summary>
    /// Where the materials/ folder is written for an entry. With "Custom materials folder" off this is the
    /// entry's own compile destination (materials sit beside the models). With it on, materials go to a
    /// folder under the project root (<see cref="MaterialsOutputDir"/>) - the path is always resolved
    /// relative to the project root, and absolute or escaping paths are rejected (falling back to the
    /// compile destination). Empty means the project root itself. An in-game compile (null
    /// <paramref name="compileDest"/>) always overrules the custom folder: materials stay in game.
    /// </summary>
    private string? ResolveMaterialsDestination(string? compileDest)
    {
        // "Compile in game" leaves the output in place, so there's nothing to gather out - this
        // overrules the custom materials folder.
        if (compileDest is null || !UseCustomMaterialsDir)
            return compileDest;

        var projectDir = ProjectDir;
        if (string.IsNullOrEmpty(projectDir))
            return compileDest;

        var rel = (MaterialsOutputDir ?? string.Empty).Trim();
        if (Path.IsPathRooted(rel))
        {
            Log($"[Materials] Custom folder '{rel}' must be relative to the project root - using the compile output folder.");
            return compileDest;
        }

        var root = Path.GetFullPath(projectDir);
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch
        {
            Log($"[Materials] Custom folder '{rel}' is not a valid path - using the compile output folder.");
            return compileDest;
        }

        if (combined.Equals(root, StringComparison.OrdinalIgnoreCase)
            || combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return combined;

        Log($"[Materials] Custom folder '{rel}' escapes the project root - using the compile output folder.");
        return compileDest;
    }

    private async Task RunMaterialCopyAsync(IReadOnlyList<string> mdlPaths, string? materialsDest, CancellationToken ct)
    {
        var gameInfoPath = SelectedGame?.GameInfo.ResolvedPath;
        if (string.IsNullOrEmpty(gameInfoPath) || !File.Exists(gameInfoPath))
        {
            Log("[Materials] Skipped: gameinfo.txt not configured.");
            return;
        }
        if (materialsDest is null)
        {
            Log("[Materials] Skipped: output mode is 'compile in game'.");
            return;
        }

        Log("--- Material copy ---");

        // The game's vpk.exe lets ModelTool tell "missing" apart from "shipped in a game VPK".
        var vpkExe = VpkLocator.FindVpkExe(SelectedGame?.PackerTool.ResolvedPath, gameInfoPath);
        if (vpkExe is null)
            Log("[Materials] No vpk.exe found for this game - files inside game VPKs will be reported as missing.");

        var svc = new MaterialCopyService();
        svc.Output += Log;
        try
        {
            foreach (var mdl in mdlPaths)
            {
                if (ct.IsCancellationRequested)
                    break;

                var req = new MaterialCopyRequest(
                    ToolPath:     _modelToolPath,
                    MdlPath:      mdl,
                    GameInfoPath: gameInfoPath,
                    DestDir:      materialsDest,
                    Localize:     LocalizeMaterials,
                    FlatPatch:    FlatPatchShader,
                    VpkExePath:   vpkExe);

                var r = await svc.CopyAsync(req, ct);
                await FlushLogAsync();
                if (!r.Success)
                    Log($"[Materials] Failed: {r.Error}");
            }
        }
        finally
        {
            svc.Output -= Log;
        }
        Log("--- Material copy done ---");
    }

    /// <summary>
    /// Creates the model's material folders (from its <c>$cdmaterials</c> paths, read via ModelTool)
    /// under the game's <c>materials/</c> folder, then hands the created folders to the entry for its
    /// "Go to Materials" button + picker. In-game compiles only (the caller gates on a null
    /// destination). Existing folders are left as-is; every failure is a best-effort log line.
    /// </summary>
    private async Task MakeMaterialDirsAsync(ModelEntryViewModel entry, CancellationToken ct)
    {
        if (GameInfoDir is not { } gameInfoDir)
        {
            Log("[Material dirs] Skipped: gameinfo.txt not configured.");
            return;
        }

        // The game root the folders are created under (gameinfo can mount several); falls back to the
        // gameinfo directory when the saved choice is gone or none is available.
        var root = SelectedMaterialGameRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = gameInfoDir;

        var svc = new ModelMaterialDirsService();
        var result = await svc.GetDirsAsync(new ModelMaterialDirsRequest(_modelToolPath, entry.LastMdlPath!), ct);
        if (!result.Success)
        {
            Log($"[Material dirs] Failed: {result.Error}");
            return;
        }
        if (result.Directories.Count == 0)
        {
            Log("[Material dirs] No material directories found in the model.");
            entry.SetMaterialDirs(Array.Empty<MaterialDirEntry>());
            return;
        }

        Log($"--- Make material directories (under {root}) ---");
        var materialsRoot = Path.Combine(root, "materials");
        var created = new List<MaterialDirEntry>();
        foreach (var rel in result.Directories)
        {
            var full = Path.Combine(materialsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var existed = Directory.Exists(full);
                Directory.CreateDirectory(full);
                created.Add(new MaterialDirEntry(rel, full));
                Log(existed ? $"[Material dirs] Exists: materials/{rel}" : $"[Material dirs] Created: materials/{rel}");
            }
            catch (Exception ex)
            {
                Log($"[Material dirs] WARNING: could not create materials/{rel}: {ex.Message}");
            }
        }
        entry.SetMaterialDirs(created);
        Log("--- Make material directories done ---");
    }

    /// <summary>Reads the just-compiled .mdl's stats (bones, hitboxes, poly count, dependencies) into
    /// the entry's info panel. Best-effort: a read failure leaves a short note rather than erroring the
    /// compile.</summary>
    private async Task ScanMdlInfoAsync(ModelEntryViewModel entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.LastMdlPath))
        {
            entry.MdlInfo = null;
            return;
        }
        var svc = new ModelInfoService();
        var result = await svc.GetInfoAsync(new ModelInfoRequest(_modelToolPath, entry.LastMdlPath), ct);
        entry.MdlInfo = result.Success ? result.Text : $"Model info unavailable: {result.Error}";
    }

    /// <summary>Default subfolder name from the app version, e.g. 0.2.5 -> "compiled025".</summary>
    private static string DefaultSubfolderName
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "compiled" : $"compiled{v.Major}{v.Minor}{v.Build}";
        }
    }

    // --- Shared console (studiomdl / ModelTool output) ----------------------------------------

    // Output arrives one line at a time on background threads, thousands per verbose compile; the
    // shared console buffers and coalesces them, so we just forward each line.
    private void Log(string line) => _console.Append(line);

    // Ensures queued background output is on screen before we write our own marker lines.
    private Task FlushLogAsync() => _console.FlushAsync();
}
