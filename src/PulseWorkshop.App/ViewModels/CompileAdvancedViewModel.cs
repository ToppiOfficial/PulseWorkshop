using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

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
    private readonly string _modelToolPath;

    private ModelEntryViewModel? _selectedEntry;
    private bool _isCompiling;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;

    public CompileAdvancedViewModel(AdvancedProjectSession session, ConsoleViewModel console)
    {
        _session = session;
        _console = console;
        _modelToolPath = ToolLocator.ResolveModelToolPath();

        AddEntryCommand = new RelayCommand(AddEntry, () => IsProjectOpen);
        CompileAllCommand = new AsyncRelayCommand(CompileAllAsync, () => CanCompileAll);
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

    private void OnProjectChanged()
    {
        Entries.Clear();
        foreach (var entry in _session.Project.Entries)
            Entries.Add(new ModelEntryViewModel(this, entry));

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

    /// <summary>Persists the project (best-effort), rebuilding entry order from the UI via the session.</summary>
    public void Save() => _session.Save();

    /// <summary>Re-evaluates the can-execute state of every compile command.</summary>
    public void RefreshCommands()
    {
        CompileAllCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddEntryCommand.RaiseCanExecuteChanged();
        foreach (var entry in Entries)
            entry.RaiseCanCompileChanged();
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
                Save();
            }
        }
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

    // --- Compile ------------------------------------------------------------------------------

    /// <summary>Requests cancellation of the in-progress compile (single or batch).</summary>
    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    public async Task CompileEntryAsync(ModelEntryViewModel entry)
    {
        if (!IsGameReady)
            return;

        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsCompiling = true;
        entry.IsCompiling = true;
        try
        {
            await CompileOneAsync(entry, ct);
        }
        finally
        {
            entry.IsCompiling = false;
            IsCompiling = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
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

        if (!ConfirmCompileAll(targets.Count))
            return;

        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsCompiling = true;
        Log($"=== Compile all ({targets.Count} entr{(targets.Count == 1 ? "y" : "ies")}) ===");
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
                ? $"Compile all cancelled: {ok}/{targets.Count} done."
                : $"Compile all done: {ok}/{targets.Count} succeeded.";
            Log($"=== {StatusMessage} ===");
        }
        finally
        {
            IsCompiling = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
    }

    private bool ConfirmCompileAll(int count)
    {
        var message = $"Compile all {count} flagged entr{(count == 1 ? "y" : "ies")} in order?";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, "Compile all",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(message, "Compile all",
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
            ExtraOptions: CombineCommands(GlobalCommand, entry.Command),
            DestinationBase: destination,
            CleanBeforeTransfer: CleanBeforeTransfer);

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

            if (GetMaterialOnCompile && result.CompiledMdls.Count > 0)
            {
                // After the move the in-game .mdl is gone, so read the moved copy at the destination.
                var matMdls = destination is null
                    ? result.CompiledMdls
                    : result.CopiedFiles
                        .Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList();
                await RunMaterialCopyAsync(matMdls, ResolveMaterialsDestination(destination), ct);
            }
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

    /// <summary>The project's global command with one entry's command appended after it. The global
    /// command may be typed across multiple lines for readability; newlines are flattened to spaces
    /// here so studiomdl receives a single argument string.</summary>
    private static string CombineCommands(string global, string entry)
    {
        static string Flatten(string? s) =>
            string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(" ", new[] { Flatten(global), Flatten(entry) }.Where(s => s.Length > 0)).Trim();
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
                    FlatPatch:    FlatPatchShader);

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
