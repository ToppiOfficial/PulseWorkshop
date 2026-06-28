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

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Compile - Advanced tab: a project-directory workflow on top of the same studiomdl pipeline as
/// the Simple tab. State lives in a user-chosen <c>.pw_mdlproject</c> file (not <c>%AppData%</c>) and
/// holds its own game selection plus an ordered list of model entries that compile one at a time.
/// Reuses <see cref="ModelCompileService"/> and <see cref="MaterialCopyService"/>; owns its own
/// embedded terminal, separate from the Simple tab's.
/// </summary>
public sealed class CompileAdvancedViewModel : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly AdvancedCompileConfig _config;
    private readonly string _modelToolPath;

    private ModelProject _project = new();
    private string? _projectPath;
    private GameSetupEntryViewModel? _selectedGame;
    private bool _isCompiling;
    private bool _isTerminalVisible;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;

    public CompileAdvancedViewModel(GameSetupViewModel gameSetup)
    {
        _gameSetup = gameSetup;
        _config = AdvancedCompileConfig.Load();
        _modelToolPath = ToolLocator.ResolveModelToolPath();

        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProject);
        CloseProjectCommand = new RelayCommand(CloseProject, () => IsProjectOpen);
        AddEntryCommand = new RelayCommand(AddEntry, () => IsProjectOpen);
        CompileAllCommand = new AsyncRelayCommand(CompileAllAsync, () => CanCompileAll);
        CancelCommand = new RelayCommand(Cancel, () => IsCompiling);
        ClearConsoleCommand = new RelayCommand(ClearConsole);

        // Reopen the last project if it still exists.
        if (!string.IsNullOrEmpty(_config.LastProjectPath) && File.Exists(_config.LastProjectPath)
            && ModelProject.Load(_config.LastProjectPath) is { } reopened)
            LoadProject(_config.LastProjectPath, reopened);
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand CloseProjectCommand { get; }
    public RelayCommand AddEntryCommand { get; }
    public AsyncRelayCommand CompileAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }

    /// <summary>The shared Game Setup roster (game name + resolved tool paths) used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    /// <summary>The model entries, in compile order. The UI lets the user drag to reorder.</summary>
    public ObservableCollection<ModelEntryViewModel> Entries { get; } = new();

    /// <summary>Output-destination choices, shared with every entry so ComboBox selection matches.
    /// A Subfolder name may itself be a nested path (e.g. <c>test/bill</c>), so a separate work-folder
    /// mode is unnecessary.</summary>
    public IReadOnlyList<OutputModeChoice> OutputModes { get; } = new[]
    {
        new OutputModeChoice(CompileOutputMode.Subfolder, "Subfolder (under project)"),
        new OutputModeChoice(CompileOutputMode.LeaveInGame, "Compile in game (don't move)"),
    };

    // --- Project lifecycle --------------------------------------------------------------------

    public bool IsProjectOpen => !string.IsNullOrEmpty(_projectPath);
    public string ProjectPath => _projectPath ?? string.Empty;
    public string ProjectName => IsProjectOpen ? Path.GetFileNameWithoutExtension(_projectPath!) : string.Empty;

    /// <summary>The folder the <c>.pw_mdlproject</c> lives in (the base for relative paths), or null.</summary>
    public string? ProjectDir => string.IsNullOrEmpty(_projectPath) ? null : Path.GetDirectoryName(_projectPath);

    private void NewProject()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Create model project",
            Filter = "Model project (*.pw_mdlproject)|*.pw_mdlproject",
            DefaultExt = ".pw_mdlproject",
            FileName = "models.pw_mdlproject",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        var project = new ModelProject();
        project.Save(dlg.FileName);
        LoadProject(dlg.FileName, project);
    }

    private void OpenProject()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open model project",
            Filter = "Model project (*.pw_mdlproject)|*.pw_mdlproject|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        var loaded = ModelProject.Load(dlg.FileName);
        if (loaded is null)
        {
            StatusMessage = "Couldn't open project (missing or corrupt).";
            return;
        }
        LoadProject(dlg.FileName, loaded);
    }

    private void CloseProject()
    {
        _projectPath = null;
        _project = new ModelProject();
        _selectedGame = null;
        Entries.Clear();
        _config.LastProjectPath = null;
        _config.Save();

        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        StatusMessage = "No project open.";
    }

    private void LoadProject(string path, ModelProject project)
    {
        _projectPath = path;
        _project = project;
        _selectedGame = project.GameId is { } id
            ? _gameSetup.Games.FirstOrDefault(g => g.Model.Id == id)
            : null;

        Entries.Clear();
        foreach (var entry in project.Entries)
            Entries.Add(new ModelEntryViewModel(this, entry));

        _config.Remember(path);

        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        StatusMessage = $"Opened {Path.GetFileName(path)}.";
    }

    /// <summary>Persists the project to its file (best-effort), rebuilding entry order from the UI.</summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_projectPath))
            return;
        _project.Entries = Entries.Select(e => e.Model).ToList();
        _project.Save(_projectPath);
    }

    /// <summary>Re-evaluates the can-execute state of every compile command.</summary>
    public void RefreshCommands()
    {
        CompileAllCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddEntryCommand.RaiseCanExecuteChanged();
        CloseProjectCommand.RaiseCanExecuteChanged();
        foreach (var entry in Entries)
            entry.RaiseCanCompileChanged();
    }

    // --- Project-level bound state -------------------------------------------------------------

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                _project.GameId = value?.Model.Id;
                Save();
                OnPropertyChanged(nameof(IsGameReady));
                RefreshCommands();
            }
        }
    }

    public string GlobalCommand
    {
        get => _project.GlobalCommand;
        set
        {
            if (_project.GlobalCommand != (value ?? string.Empty))
            {
                _project.GlobalCommand = value ?? string.Empty;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool GetMaterialOnCompile
    {
        get => _project.GetMaterialOnCompile;
        set
        {
            if (_project.GetMaterialOnCompile != value)
            {
                _project.GetMaterialOnCompile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfigureMaterials));
                Save();
            }
        }
    }

    public bool LocalizeMaterials
    {
        get => _project.LocalizeMaterials;
        set { if (_project.LocalizeMaterials != value) { _project.LocalizeMaterials = value; OnPropertyChanged(); Save(); } }
    }

    public bool FlatPatchShader
    {
        get => _project.FlatPatchShader;
        set { if (_project.FlatPatchShader != value) { _project.FlatPatchShader = value; OnPropertyChanged(); Save(); } }
    }

    public bool CleanBeforeTransfer
    {
        get => _project.CleanBeforeTransfer;
        set { if (_project.CleanBeforeTransfer != value) { _project.CleanBeforeTransfer = value; OnPropertyChanged(); Save(); } }
    }

    /// <summary>True when material gathering is enabled - controls the Localize and Flat patch options.</summary>
    public bool CanConfigureMaterials => GetMaterialOnCompile;

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

    /// <summary>Whether the embedded terminal drawer is shown (toggleable, like the Workshop terminal).</summary>
    public bool IsTerminalVisible
    {
        get => _isTerminalVisible;
        set => SetField(ref _isTerminalVisible, value);
    }

    // --- Path helpers (used by entries) -------------------------------------------------------

    /// <summary>Resolves a stored (relative or absolute) path against the project folder.</summary>
    public string ResolveAgainstProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        if (Path.IsPathRooted(path))
            return path;
        var dir = ProjectDir;
        return string.IsNullOrEmpty(dir) ? path : Path.GetFullPath(Path.Combine(dir, path));
    }

    /// <summary>Stores a picked path relative to the project when it sits under it; else absolute.</summary>
    public string MakeProjectRelative(string fullPath)
    {
        var dir = ProjectDir;
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                var rel = Path.GetRelativePath(dir, fullPath);
                if (!Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal))
                    return rel;
            }
            catch
            {
                // Fall through to the absolute path.
            }
        }
        return fullPath;
    }

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
        Entries.Add(new ModelEntryViewModel(this, model));
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

        Entries.Remove(entry);
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

        ClearConsole(); // A single-entry compile starts with a clean terminal.
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

        // Clear once at the start of the batch; the per-entry compiles below append so the whole
        // run stays in one transcript.
        ClearConsole();
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
                await RunMaterialCopyAsync(matMdls, destination, ct);
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

    /// <summary>The project's global command with one entry's command appended after it. The global
    /// command may be typed across multiple lines for readability; newlines are flattened to spaces
    /// here so studiomdl receives a single argument string.</summary>
    private static string CombineCommands(string global, string entry)
    {
        static string Flatten(string? s) =>
            string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(" ", new[] { Flatten(global), Flatten(entry) }.Where(s => s.Length > 0)).Trim();
    }

    private async Task RunMaterialCopyAsync(IReadOnlyList<string> mdlPaths, string? compileDest, CancellationToken ct)
    {
        var gameInfoPath = SelectedGame?.GameInfo.ResolvedPath;
        if (string.IsNullOrEmpty(gameInfoPath) || !File.Exists(gameInfoPath))
        {
            Log("[Materials] Skipped: gameinfo.txt not configured.");
            return;
        }
        if (compileDest is null)
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
                    DestDir:      compileDest,
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

    // --- Embedded terminal (studiomdl / ModelTool output) -------------------------------------

    private const int MaxConsoleLines = 1000;
    private readonly List<string> _consoleLines = new();
    private string _consoleText = string.Empty;

    /// <summary>The Advanced terminal's full text, one output line per row, oldest first.</summary>
    public string ConsoleText
    {
        get => _consoleText;
        private set => SetField(ref _consoleText, value);
    }

    private void ClearConsole()
    {
        _consoleLines.Clear();
        ConsoleText = string.Empty;
    }

    // studiomdl / ModelTool output arrives on background threads; marshal to the UI thread before
    // touching the line buffer bound to the terminal.
    private void Log(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            AppendConsoleLine(line);
        else
            dispatcher.BeginInvoke(() => AppendConsoleLine(line));
    }

    // Awaiting a Background-priority no-op guarantees all background-queued log lines have been
    // appended before we write our own marker lines on the UI thread.
    private static Task FlushLogAsync() =>
        Application.Current.Dispatcher
            .InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Background)
            .Task;

    private void AppendConsoleLine(string line)
    {
        _consoleLines.Add(line);
        while (_consoleLines.Count > MaxConsoleLines)
            _consoleLines.RemoveAt(0);
        ConsoleText = string.Join(Environment.NewLine, _consoleLines);
    }
}
