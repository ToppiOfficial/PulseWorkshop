using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Textures tab: a project workflow (<c>.pw_textureproject</c>) that converts loose image files into
/// Source <c>.vtf</c> textures in bulk, driven by regex/literal patterns matched against a source folder -
/// the successor to KitsuneResource's texture pipeline. Unlike the Compile/Package tabs it owns its own
/// standalone project (not the shared <c>.pw_mdlproject</c> session): New/Open/Close + auto-save, a game
/// selection (for the VTF tool + command), and an ordered list of <see cref="TextureGroupViewModel"/>.
/// Output streams into the shared app-wide console.
/// </summary>
public sealed class TexturesViewModel : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly ConsoleViewModel _console;
    private readonly AdvancedTextureConfig _config;

    private TextureProject _project = new();
    private string? _projectPath;
    private GameSetupEntryViewModel? _selectedGame;
    private TextureGroupViewModel? _selectedGroup;
    private bool _isConverting;
    private bool _force;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;

    public TexturesViewModel(GameSetupViewModel gameSetup, ConsoleViewModel console)
    {
        _gameSetup = gameSetup;
        _console = console;
        _config = AdvancedTextureConfig.Load();

        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProject);
        CloseProjectCommand = new RelayCommand(CloseProject, () => IsProjectOpen);
        AddGroupCommand = new RelayCommand(AddGroup, () => IsProjectOpen);
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => IsProjectOpen);
        ConvertAllCommand = new AsyncRelayCommand(ConvertAllAsync, () => CanConvertAll);
        ConvertSelectedCommand = new AsyncRelayCommand(ConvertSelectedAsync, () => CanConvertSelected);
        CancelCommand = new RelayCommand(Cancel, () => IsConverting);

        // Reopen the last project if it still exists.
        if (!string.IsNullOrEmpty(_config.LastProjectPath) && File.Exists(_config.LastProjectPath)
            && TextureProject.Load(_config.LastProjectPath) is { } reopened)
            LoadProject(_config.LastProjectPath, reopened);
        else
            OnProjectChanged();

        RebuildRecentProjects();
    }

    /// <summary>The most-recently-opened texture projects that still exist on disk (newest first,
    /// capped at <see cref="MaxRecentShown"/>), shown in the empty state's "Open recent" list.</summary>
    public ObservableCollection<RecentItemViewModel> RecentProjects { get; } = new();

    private const int MaxRecentShown = 8;

    /// <summary>Rebuilds <see cref="RecentProjects"/> from the persisted list, dropping entries whose
    /// file is gone. Called at startup and whenever the open project changes.</summary>
    private void RebuildRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in _config.RecentProjects)
        {
            if (!File.Exists(path))
                continue;
            RecentProjects.Add(new RecentItemViewModel(path, p => OpenProjectFromPath(p)));
            if (RecentProjects.Count >= MaxRecentShown)
                break;
        }
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand CloseProjectCommand { get; }
    public RelayCommand AddGroupCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public AsyncRelayCommand ConvertAllCommand { get; }

    /// <summary>Converts the groups highlighted in the list (multi-select with Ctrl/Shift). A single-row
    /// selection behaves exactly like converting one group.</summary>
    public AsyncRelayCommand ConvertSelectedCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The shared Game Setup roster used for the game dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    /// <summary>The texture groups, in run order (the UI lets the user drag to reorder).</summary>
    public ObservableCollection<TextureGroupViewModel> Groups { get; } = new();

    /// <summary>The group shown in the editor panel (master-detail, like the Package tab).</summary>
    public TextureGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetField(ref _selectedGroup, value))
                OnPropertyChanged(nameof(HasSelectedGroup));
        }
    }

    public bool HasSelectedGroup => _selectedGroup is not null;

    // --- Project lifecycle --------------------------------------------------------------------

    public bool IsProjectOpen => !string.IsNullOrEmpty(_projectPath);
    public string ProjectPath => _projectPath ?? string.Empty;
    public string ProjectName => IsProjectOpen ? Path.GetFileNameWithoutExtension(_projectPath!) : string.Empty;

    /// <summary>The folder the <c>.pw_textureproject</c> lives in (the base for relative paths), or null.</summary>
    public string? ProjectDir => string.IsNullOrEmpty(_projectPath) ? null : Path.GetDirectoryName(_projectPath);

    private void NewProject()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Create textures project",
            Filter = "Textures project (*.pw_textureproject)|*.pw_textureproject",
            DefaultExt = ".pw_textureproject",
            FileName = "textures.pw_textureproject",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        var project = new TextureProject();
        project.Save(dlg.FileName);
        LoadProject(dlg.FileName, project);
    }

    private void OpenProject()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open textures project",
            Filter = "Textures project (*.pw_textureproject)|*.pw_textureproject|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        var loaded = TextureProject.Load(dlg.FileName);
        if (loaded is null)
            return;
        LoadProject(dlg.FileName, loaded);
    }

    /// <summary>
    /// Opens a project directly from a path (e.g. a shell file-association launch: double-clicking a
    /// <c>.pw_textureproject</c>). Returns whether it loaded; a missing or corrupt file is a silent no-op.
    /// </summary>
    public bool OpenProjectFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var loaded = TextureProject.Load(path);
        if (loaded is null)
            return false;

        LoadProject(Path.GetFullPath(path), loaded);
        return true;
    }

    private void CloseProject()
    {
        _projectPath = null;
        _project = new TextureProject();
        _selectedGame = null;
        _config.Clear();
        RebuildRecentProjects();
        OnProjectChanged();
    }

    private void LoadProject(string path, TextureProject project)
    {
        _projectPath = path;
        _project = project;
        _selectedGame = project.GameId is { } id
            ? _gameSetup.Games.FirstOrDefault(g => g.Model.Id == id)
            : null;
        _config.Remember(path);
        RebuildRecentProjects();
        OnProjectChanged();
    }

    private void OnProjectChanged()
    {
        Groups.Clear();
        foreach (var group in _project.Groups)
            Groups.Add(new TextureGroupViewModel(this, group));

        SelectedGroup = Groups.FirstOrDefault();
        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        StatusMessage = IsProjectOpen ? $"Opened {ProjectName}." : "No project open.";
    }

    /// <summary>Rebuilds the project's group list from the live UI collection, then writes it to disk
    /// (best-effort). Auto-save on every edit - there is no manual save step.</summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_projectPath))
            return;
        _project.Groups = Groups.Select(g => g.Model).ToList();
        _project.Save(_projectPath);
    }

    /// <summary>
    /// "Save As" (Ctrl+Shift+S while on the Textures tab): picks a new <c>.pw_textureproject</c> path,
    /// writes the current in-memory project there, and makes that the open project. When nothing is
    /// open yet it behaves like New-from-current-state. Best-effort: a cancelled dialog is a no-op.
    /// </summary>
    public void SaveProjectAs()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save textures project as",
            Filter = "Textures project (*.pw_textureproject)|*.pw_textureproject",
            DefaultExt = ".pw_textureproject",
            FileName = IsProjectOpen ? Path.GetFileName(_projectPath!) : "textures.pw_textureproject",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        // Fold the live group list into the project before writing the copy, then switch to it.
        _project.Groups = Groups.Select(g => g.Model).ToList();
        _project.Save(dlg.FileName);
        LoadProject(dlg.FileName, _project);
    }

    public void RefreshCommands()
    {
        CloseProjectCommand.RaiseCanExecuteChanged();
        AddGroupCommand.RaiseCanExecuteChanged();
        BrowseSourceCommand.RaiseCanExecuteChanged();
        ConvertAllCommand.RaiseCanExecuteChanged();
        ConvertSelectedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Re-checks the source folder on disk (used to refresh readiness when the window regains
    /// focus - the folder can be created or deleted in another program while a path is typed).</summary>
    public void RefreshFileState()
    {
        OnPropertyChanged(nameof(SourceFolderMissing));
        RefreshCommands();
    }

    /// <summary>Called by a group when its list selection toggles, so "Convert selected" re-evaluates.</summary>
    public void OnGroupSelectionChanged() => ConvertSelectedCommand.RaiseCanExecuteChanged();

    // --- Project-level bound state ------------------------------------------------------------

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

    /// <summary>Source folder scanned for input images (relative to the project, or absolute). Blank
    /// means the project folder itself.</summary>
    public string SourceFolder
    {
        get => _project.SourceFolder;
        set
        {
            if (_project.SourceFolder != (value ?? string.Empty))
            {
                _project.SourceFolder = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedSourceFolder));
                OnPropertyChanged(nameof(SourceFolderMissing));
                Save();
                RefreshCommands();
            }
        }
    }

    /// <summary>The source folder resolved to an absolute path (the project folder when blank).</summary>
    public string ResolvedSourceFolder
    {
        get
        {
            var dir = ProjectDir;
            if (string.IsNullOrWhiteSpace(_project.SourceFolder))
                return dir ?? string.Empty;
            return ResolveAgainstProject(_project.SourceFolder);
        }
    }

    /// <summary>True when the resolved source folder doesn't exist on disk (drives a "missing" warning).</summary>
    public bool SourceFolderMissing
    {
        get
        {
            var resolved = ResolvedSourceFolder;
            return !string.IsNullOrEmpty(resolved) && !Directory.Exists(resolved);
        }
    }

    public bool SkipUpToDate
    {
        get => _project.SkipUpToDate;
        set
        {
            if (_project.SkipUpToDate != value)
            {
                _project.SkipUpToDate = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>Extra VTF arguments applied to every group (appended after the game's base command).</summary>
    public string GlobalVtfCommand
    {
        get => _project.GlobalVtfCommand;
        set
        {
            if (_project.GlobalVtfCommand != (value ?? string.Empty))
            {
                _project.GlobalVtfCommand = value ?? string.Empty;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>Reconvert even files whose <c>.vtf</c> is already up-to-date (this run only; not saved).</summary>
    public bool Force
    {
        get => _force;
        set => SetField(ref _force, value);
    }

    /// <summary>The resolved VTF tool path (from the selected game), or null.</summary>
    private string? VtfToolPath
    {
        get
        {
            var p = SelectedGame?.VtfTool.ResolvedPath;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
    }

    /// <summary>True when the selected game has a usable VTF tool on disk.</summary>
    public bool IsGameReady
    {
        get
        {
            var tool = VtfToolPath;
            return !string.IsNullOrWhiteSpace(tool) && File.Exists(tool);
        }
    }

    public bool IsConverting
    {
        get => _isConverting;
        private set { if (SetField(ref _isConverting, value)) RefreshCommands(); }
    }

    public bool CanConvertAll =>
        IsProjectOpen && !IsConverting && IsGameReady && Groups.Any(g => g.IncludeInAll);

    public bool CanConvertSelected =>
        IsProjectOpen && !IsConverting && IsGameReady && Groups.Any(g => g.IsSelected);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    // --- Path helpers -------------------------------------------------------------------------

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

    private void BrowseSource()
    {
        var dlg = new OpenFolderDialog { Title = "Choose source folder to scan" };
        try
        {
            if (ResolvedSourceFolder is { Length: > 0 } cur && Directory.Exists(cur))
                dlg.InitialDirectory = cur;
            else if (ProjectDir is { Length: > 0 } pdir && Directory.Exists(pdir))
                dlg.InitialDirectory = pdir;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            SourceFolder = MakeProjectRelative(dlg.FolderName);
    }

    // --- Groups -------------------------------------------------------------------------------

    private void AddGroup()
    {
        var model = new TextureGroup { Name = "New group" };
        var vm = new TextureGroupViewModel(this, model);
        Groups.Add(vm);
        SelectedGroup = vm;
        Save();
        RefreshCommands();
    }

    public void CloneGroup(TextureGroupViewModel group)
    {
        var clone = group.Model.Clone();
        clone.Name = string.IsNullOrWhiteSpace(group.Model.Name)
            ? "Copy"
            : group.Model.Name + " (copy)";

        var index = Groups.IndexOf(group);
        var vm = new TextureGroupViewModel(this, clone);
        if (index >= 0)
            Groups.Insert(index + 1, vm);
        else
            Groups.Add(vm);

        SelectedGroup = vm;
        Save();
        RefreshCommands();
    }

    public void RemoveGroup(TextureGroupViewModel group)
    {
        var name = string.IsNullOrWhiteSpace(group.Name) ? "this group" : $"\"{group.Name}\"";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, $"Remove {name} from the project?", "Remove group",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show($"Remove {name} from the project?", "Remove group",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            return;

        var index = Groups.IndexOf(group);
        Groups.Remove(group);
        if (ReferenceEquals(SelectedGroup, group))
            SelectedGroup = Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)];
        Save();
        RefreshCommands();
    }

    // --- Convert ------------------------------------------------------------------------------

    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private async Task ConvertAllAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Groups.Where(g => g.IncludeInAll).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No groups flagged for 'Convert all'.";
            return;
        }
        if (!ConfirmBatch("Convert all", targets.Count))
            return;

        await ConvertBatchAsync(targets, "Convert all");
    }

    private async Task ConvertSelectedAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Groups.Where(g => g.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No groups selected.";
            return;
        }

        // A single-row selection converts straight away; only a multi-group batch asks first.
        if (targets.Count > 1 && !ConfirmBatch("Convert selected", targets.Count))
            return;

        await ConvertBatchAsync(targets, "Convert selected");
    }

    /// <summary>Converts a set of groups in order, streaming progress and a final tally to the console.</summary>
    private async Task ConvertBatchAsync(IReadOnlyList<TextureGroupViewModel> targets, string label)
    {
        var source = ResolvedSourceFolder;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            StatusMessage = "Source folder not found - set it above.";
            Log($"FAILED: source folder not found ({source}).");
            return;
        }

        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsConverting = true;
        Log($"=== {label} ({targets.Count} group{(targets.Count == 1 ? "" : "s")}) - source: {source} ===");
        try
        {
            var service = new TextureConversionService();
            service.Output += Log;
            var vtf = new TextureToolConfig(VtfToolPath, CombineToolCommand(SelectedGame?.VtfToolCommand));
            int totalConverted = 0, totalSkipped = 0, totalFailed = 0;
            var cancelled = false;
            try
            {
                foreach (var group in targets)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }

                    group.IsConverting = true;
                    try
                    {
                        StatusMessage = $"Converting {group.Name}...";
                        var result = await service.ConvertGroupAsync(
                            source, group.Model, vtf, SkipUpToDate, Force, ct);
                        await FlushLogAsync();
                        totalConverted += result.Converted;
                        totalSkipped += result.Skipped;
                        totalFailed += result.Failed;
                        group.HasError = !result.Success;
                    }
                    finally
                    {
                        group.IsConverting = false;
                    }

                    cancelled = ct.IsCancellationRequested;
                    if (cancelled) break;
                }
            }
            finally
            {
                service.Output -= Log;
            }

            StatusMessage = cancelled
                ? $"{label} cancelled: {totalConverted} converted, {totalSkipped} skipped, {totalFailed} failed."
                : $"{label} done: {totalConverted} converted, {totalSkipped} skipped, {totalFailed} failed.";
            Log($"=== {StatusMessage} ===");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{label} cancelled.";
            Log("=== Cancelled ===");
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsConverting = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
    }

    /// <summary>Base game command + the project-wide global command, space-joined (either may be blank).</summary>
    private string CombineToolCommand(string? gameCommand)
    {
        var a = gameCommand?.Trim() ?? string.Empty;
        var b = GlobalVtfCommand?.Trim() ?? string.Empty;
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    private bool ConfirmBatch(string title, int count)
    {
        var message = $"{title}: run {count} group{(count == 1 ? "" : "s")} in order?";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    // --- Shared console -----------------------------------------------------------------------

    private void Log(string line) => _console.Append(line);

    private Task FlushLogAsync() => _console.FlushAsync();
}
