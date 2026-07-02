using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The shared open <c>.pw_mdlproject</c> for both Advanced tabs (Compile and Package). It owns the
/// single in-memory <see cref="ModelProject"/>, its path, the selected game, and the New/Open/Close +
/// Save lifecycle. The two tabs edit different lists of the same project (compile
/// <see cref="ModelProject.Entries"/> vs <see cref="ModelProject.PackageEntries"/>); because they
/// share one session and one save, neither can clobber the other.
///
/// Each tab's view model registers a sync callback (<see cref="RegisterSync"/>) that rebuilds its own
/// list from its live UI collection; every <see cref="Save"/> runs all callbacks first, so both lists
/// are always current on disk regardless of which tab triggered the save.
/// </summary>
public sealed class AdvancedProjectSession : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly AdvancedCompileConfig _config;
    private readonly List<Action> _syncHandlers = new();

    private ModelProject _project = new();
    private string? _projectPath;
    private GameSetupEntryViewModel? _selectedGame;

    public AdvancedProjectSession(GameSetupViewModel gameSetup)
    {
        _gameSetup = gameSetup;
        _config = AdvancedCompileConfig.Load();

        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProject);
        CloseProjectCommand = new RelayCommand(CloseProject, () => IsProjectOpen);

        // Reopen the last project if it still exists. View models built after this session call
        // RaiseProjectChanged once in their own constructors to pick up this initial state.
        if (!string.IsNullOrEmpty(_config.LastProjectPath) && File.Exists(_config.LastProjectPath)
            && ModelProject.Load(_config.LastProjectPath) is { } reopened)
            LoadProject(_config.LastProjectPath, reopened, raise: false);
    }

    /// <summary>The single shared project instance both tabs read from and write into.</summary>
    public ModelProject Project => _project;

    /// <summary>The shared Game Setup roster used for both tabs' game dropdowns.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand CloseProjectCommand { get; }

    /// <summary>Raised after the open project changes (new / open / close): view models rebuild their
    /// entry lists from <see cref="Project"/> and refresh their project-level bindings.</summary>
    public event Action? ProjectChanged;

    /// <summary>Raised when the selected game changes: view models refresh their readiness state.</summary>
    public event Action? GameChanged;

    public bool IsProjectOpen => !string.IsNullOrEmpty(_projectPath);
    public string ProjectPath => _projectPath ?? string.Empty;
    public string ProjectName => IsProjectOpen ? Path.GetFileNameWithoutExtension(_projectPath!) : string.Empty;

    /// <summary>The folder the <c>.pw_mdlproject</c> lives in (the base for relative paths), or null.</summary>
    public string? ProjectDir => string.IsNullOrEmpty(_projectPath) ? null : Path.GetDirectoryName(_projectPath);

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                _project.GameId = value?.Model.Id;
                Save();
                GameChanged?.Invoke();
            }
        }
    }

    /// <summary>Registers a callback that rebuilds one of the project's lists from its tab's live UI
    /// collection. Run by every <see cref="Save"/> before writing to disk.</summary>
    public void RegisterSync(Action sync) => _syncHandlers.Add(sync);

    private void NewProject()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Create project",
            Filter = "Project (*.pw_mdlproject)|*.pw_mdlproject",
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
            Title = "Open project",
            Filter = "Project (*.pw_mdlproject)|*.pw_mdlproject|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        var loaded = ModelProject.Load(dlg.FileName);
        if (loaded is null)
            return;
        LoadProject(dlg.FileName, loaded);
    }

    /// <summary>
    /// Opens a project directly from a path (e.g. a shell file-association launch: double-clicking a
    /// <c>.pw_mdlproject</c>) rather than through the file picker. Returns whether it loaded; a missing
    /// or corrupt file is a silent no-op so the caller can decide how to report it.
    /// </summary>
    public bool OpenProjectFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var loaded = ModelProject.Load(path);
        if (loaded is null)
            return false;

        LoadProject(Path.GetFullPath(path), loaded);
        return true;
    }

    private void CloseProject()
    {
        _projectPath = null;
        _project = new ModelProject();
        _selectedGame = null;
        _config.LastProjectPath = null;
        _config.Save();
        CloseProjectCommand.RaiseCanExecuteChanged();
        RaiseProjectChanged();
    }

    private void LoadProject(string path, ModelProject project, bool raise = true)
    {
        _projectPath = path;
        _project = project;
        _selectedGame = project.GameId is { } id
            ? _gameSetup.Games.FirstOrDefault(g => g.Model.Id == id)
            : null;
        _config.Remember(path);
        CloseProjectCommand.RaiseCanExecuteChanged();
        if (raise)
            RaiseProjectChanged();
    }

    /// <summary>Re-syncs both tabs' lists into the project, then writes it to disk (best-effort).</summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_projectPath))
            return;
        foreach (var sync in _syncHandlers)
            sync();
        _project.Save(_projectPath);
    }

    /// <summary>Notifies subscribers the open project changed (used at startup to push the reopened
    /// project once the view models exist).</summary>
    public void RaiseProjectChanged()
    {
        OnPropertyChanged(string.Empty); // refresh every project-level binding on the session
        ProjectChanged?.Invoke();
    }

    // --- Path helpers (shared by both tabs) ----------------------------------------------------

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
}
