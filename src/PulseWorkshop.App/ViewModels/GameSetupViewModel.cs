using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Game Setup panel: a Crowbar-style manager for user-defined games (name, engine, exe/tool
/// paths) plus the shared Steam library folders. Standalone config - it does not touch the Workshop
/// connection. Every change is persisted to <c>gamesetup.json</c> immediately (the file is tiny).
/// </summary>
public sealed class GameSetupViewModel : ObservableObject
{
    private readonly GameSetupConfig _config;
    private GameSetupEntryViewModel? _selectedGame;

    public GameSetupViewModel()
    {
        _config = GameSetupConfig.Load();

        // The base dropdown shown by every path field: an "(Absolute path)" sentinel first, then one
        // choice per library. Shared across all path fields so adding/removing a library updates them
        // all at once.
        LibraryChoices.Add(Absolute);
        foreach (var lib in _config.Libraries)
            AddLibraryVm(lib);

        foreach (var g in _config.Games)
            Games.Add(CreateEntryVm(g));

        _selectedGame = Games.FirstOrDefault();

        AddGameCommand = new RelayCommand(AddGame);
        AddLibraryCommand = new RelayCommand(AddLibrary);
    }

    public ObservableCollection<GameSetupEntryViewModel> Games { get; } = new();
    public ObservableCollection<SteamLibraryViewModel> Libraries { get; } = new();

    /// <summary>The shared option list for every path field's base dropdown (sentinel + libraries).</summary>
    public ObservableCollection<LibraryChoice> LibraryChoices { get; } = new();

    /// <summary>The "(Absolute path)" sentinel (no library base); always the first choice.</summary>
    public LibraryChoice Absolute { get; } = new(null);

    public IReadOnlyList<string> EngineOptions { get; } = new[] { "Source", "GoldSource" };

    /// <summary>True when at least one Steam library folder is defined (drives the empty-state hint).</summary>
    public bool HasLibraries => Libraries.Count > 0;

    public RelayCommand AddGameCommand { get; }
    public RelayCommand AddLibraryCommand { get; }

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
                OnPropertyChanged(nameof(HasSelectedGame));
        }
    }

    public bool HasSelectedGame => _selectedGame is not null;

    private GameSetupEntryViewModel CreateEntryVm(GameSetupEntry entry) =>
        new(entry, LibraryChoices, Absolute, Save);

    private void AddLibraryVm(SteamLibrary lib)
    {
        var vm = new SteamLibraryViewModel(lib, Save, RemoveLibrary);
        Libraries.Add(vm);
        LibraryChoices.Add(new LibraryChoice(vm));
    }

    private void Save() => _config.Save();

    private void AddGame()
    {
        var entry = new GameSetupEntry { Name = "New Game" };
        _config.Games.Add(entry);
        var vm = CreateEntryVm(entry);
        Games.Add(vm);
        SelectedGame = vm;
        Save();
    }

    /// <summary>Duplicates a specific game as "Clone of ..." and selects it.</summary>
    public void CloneGame(GameSetupEntryViewModel source)
    {
        var clone = source.Model.Clone();
        clone.Name = $"Clone of {source.Name}";
        _config.Games.Add(clone);
        var vm = CreateEntryVm(clone);
        Games.Add(vm);
        SelectedGame = vm;
        Save();
    }

    /// <summary>Removes a specific game. The caller (code-behind) confirms first.</summary>
    public void DeleteGame(GameSetupEntryViewModel game)
    {
        var idx = Games.IndexOf(game);
        if (idx < 0)
            return;

        var wasSelected = ReferenceEquals(SelectedGame, game);
        _config.Games.Remove(game.Model);
        Games.Remove(game);
        if (wasSelected)
            SelectedGame = Games.Count == 0 ? null : Games[Math.Min(idx, Games.Count - 1)];
        Save();
    }

    private void AddLibrary()
    {
        var lib = new SteamLibrary();
        _config.Libraries.Add(lib);
        AddLibraryVm(lib);
        OnPropertyChanged(nameof(HasLibraries));
        Save();
    }

    private void RemoveLibrary(SteamLibraryViewModel vm)
    {
        _config.Libraries.Remove(vm.Model);
        Libraries.Remove(vm);

        if (LibraryChoices.FirstOrDefault(c => ReferenceEquals(c.Library, vm)) is { } choice)
            LibraryChoices.Remove(choice);

        // Any path field that was based on this library falls back to an absolute path.
        foreach (var game in Games)
            game.OnLibraryRemoved(vm.Model.Id);

        OnPropertyChanged(nameof(HasLibraries));
        Save();
    }
}

/// <summary>
/// One game entry in the panel: wraps a <see cref="GameSetupEntry"/> and exposes a
/// <see cref="PathFieldViewModel"/> for each tool path. Mutates the underlying model in place and
/// calls back to persist on every change.
/// </summary>
public sealed class GameSetupEntryViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public GameSetupEntryViewModel(GameSetupEntry model, ObservableCollection<LibraryChoice> choices,
        LibraryChoice absolute, Action onChanged)
    {
        Model = model;
        _onChanged = onChanged;

        const string exeFilter = "Executable (*.exe)|*.exe|All files (*.*)|*.*";
        GameInfo = new PathFieldViewModel("GameInfo.txt",
            "GameInfo (gameinfo.txt)|gameinfo.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            model.GameInfoTxt, choices, absolute, onChanged);
        ModelCompiler = new PathFieldViewModel("Model compiler", exeFilter, model.ModelCompiler, choices, absolute, onChanged);
        ModelViewer = new PathFieldViewModel("Model viewer", exeFilter, model.ModelViewer, choices, absolute, onChanged);
        PackerTool = new PathFieldViewModel("Packer tool", exeFilter, model.PackerTool, choices, absolute, onChanged);
        VtfTool = new PathFieldViewModel("VTF tool", exeFilter, model.VtfTool, choices, absolute, onChanged);
    }

    public GameSetupEntry Model { get; }

    /// <summary>Label shown in the games dropdown.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Model.Name) ? "(unnamed)" : Model.Name;

    // The themed ComboBox's selection box renders via ToString (it ignores DisplayMemberPath), so
    // surface the name here too - otherwise the box shows the type name.
    public override string ToString() => DisplayName;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name != (value ?? string.Empty))
            {
                Model.Name = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                _onChanged();
            }
        }
    }

    public string Engine
    {
        get => Model.Engine;
        set
        {
            if (Model.Engine != value && value is not null)
            {
                Model.Engine = value;
                OnPropertyChanged();
                _onChanged();
            }
        }
    }

    public PathFieldViewModel GameInfo { get; }
    public PathFieldViewModel ModelCompiler { get; }
    public PathFieldViewModel ModelViewer { get; }
    public PathFieldViewModel PackerTool { get; }
    public PathFieldViewModel VtfTool { get; }

    /// <summary>The command-line template the Package tool uses to launch the VTF tool for one asset.
    /// Placeholders: {input} {output} {outputdir} {outputname}.</summary>
    public string VtfToolCommand
    {
        get => Model.VtfToolCommand;
        set
        {
            if (Model.VtfToolCommand != (value ?? string.Empty))
            {
                Model.VtfToolCommand = value ?? string.Empty;
                OnPropertyChanged();
                _onChanged();
            }
        }
    }

    public void OnLibraryRemoved(Guid id)
    {
        GameInfo.OnLibraryRemoved(id);
        ModelCompiler.OnLibraryRemoved(id);
        ModelViewer.OnLibraryRemoved(id);
        PackerTool.OnLibraryRemoved(id);
        VtfTool.OnLibraryRemoved(id);
    }
}

/// <summary>
/// A single tool-path field: a base dropdown (absolute, or a Steam library), the remaining path as
/// text, a Browse button, and a "missing" flag when the resolved path doesn't exist.
/// </summary>
public sealed class PathFieldViewModel : ObservableObject
{
    private readonly PathRef _model;
    private readonly LibraryChoice _absolute;
    private readonly Action _onChanged;
    private LibraryChoice _selectedChoice;

    public PathFieldViewModel(string label, string fileFilter, PathRef model,
        ObservableCollection<LibraryChoice> choices, LibraryChoice absolute, Action onChanged)
    {
        Label = label;
        FileFilter = fileFilter;
        _model = model;
        Choices = choices;
        _absolute = absolute;
        _onChanged = onChanged;

        _selectedChoice = model.LibraryId is { } id
            ? choices.FirstOrDefault(c => c.Library?.Model.Id == id) ?? absolute
            : absolute;

        BrowseCommand = new RelayCommand(Browse);
    }

    public string Label { get; }
    public string FileFilter { get; }
    public ObservableCollection<LibraryChoice> Choices { get; }
    public RelayCommand BrowseCommand { get; }

    public LibraryChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            var choice = value ?? _absolute;
            if (SetField(ref _selectedChoice, choice))
            {
                _model.LibraryId = choice.Library?.Model.Id;
                OnPropertyChanged(nameof(PathMissing));
                _onChanged();
            }
        }
    }

    public string PathText
    {
        get => _model.Path;
        set
        {
            if (_model.Path != (value ?? string.Empty))
            {
                _model.Path = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PathMissing));
                _onChanged();
            }
        }
    }

    /// <summary>The full path: the library base joined with the remainder, or the absolute path.</summary>
    public string? ResolvedPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_model.Path))
                return null;
            var basePath = _selectedChoice.Library?.PathText;
            return string.IsNullOrWhiteSpace(basePath)
                ? _model.Path
                : Path.Combine(basePath, _model.Path);
        }
    }

    /// <summary>True when a path is set but the resolved file doesn't exist (drives the warning).</summary>
    public bool PathMissing
    {
        get
        {
            var resolved = ResolvedPath;
            return !string.IsNullOrWhiteSpace(resolved) && !File.Exists(resolved);
        }
    }

    /// <summary>Resets to an absolute path if this field was based on the removed library.</summary>
    public void OnLibraryRemoved(Guid id)
    {
        if (_selectedChoice.Library?.Model.Id == id)
            SelectedChoice = _absolute;
    }

    private void Browse()
    {
        var basePath = _selectedChoice.Library?.PathText;

        var dlg = new OpenFileDialog
        {
            Title = $"Choose {Label}",
            Filter = FileFilter,
            CheckFileExists = false,
        };

        // Start the dialog in the current value's folder, falling back to the selected library base.
        if (StartDirectory(basePath) is { } start)
            dlg.InitialDirectory = start;

        if (dlg.ShowDialog() != true)
            return;

        var picked = dlg.FileName;

        // If a library base is selected and the chosen file lives under it (same drive, no parent
        // traversal), keep the base and store just the relative remainder; otherwise store an
        // absolute path and switch the base to "(Absolute path)".
        if (!string.IsNullOrWhiteSpace(basePath) && TryMakeRelative(basePath, picked, out var relative))
        {
            PathText = relative;
        }
        else
        {
            SelectedChoice = _absolute;
            PathText = picked;
        }
    }

    /// <summary>The folder to open the Browse dialog in: the current path's folder, else the base.</summary>
    private string? StartDirectory(string? basePath)
    {
        try
        {
            if (ResolvedPath is { } resolved && Path.GetDirectoryName(resolved) is { Length: > 0 } dir
                && Directory.Exists(dir))
                return dir;
            if (!string.IsNullOrWhiteSpace(basePath) && Directory.Exists(basePath))
                return basePath;
        }
        catch
        {
            // Ignore malformed paths - just let the dialog open at its default location.
        }
        return null;
    }

    /// <summary>True (with the remainder) when <paramref name="fullPath"/> sits under
    /// <paramref name="basePath"/>; false for a different drive or any parent (..) traversal.</summary>
    private static bool TryMakeRelative(string basePath, string fullPath, out string relative)
    {
        relative = string.Empty;
        try
        {
            var rel = Path.GetRelativePath(basePath, fullPath);
            if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
                return false; // different drive, or not actually under the base
            relative = rel;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>One Steam library folder row: an editable path + Browse + remove.</summary>
public sealed class SteamLibraryViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public SteamLibraryViewModel(SteamLibrary model, Action onChanged, Action<SteamLibraryViewModel> remove)
    {
        Model = model;
        _onChanged = onChanged;
        BrowseCommand = new RelayCommand(Browse);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public SteamLibrary Model { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public string PathText
    {
        get => Model.Path;
        set
        {
            if (Model.Path != (value ?? string.Empty))
            {
                Model.Path = value ?? string.Empty;
                OnPropertyChanged();
                _onChanged();
            }
        }
    }

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Choose Steam library folder" };
        try
        {
            if (!string.IsNullOrWhiteSpace(Model.Path) && Directory.Exists(Model.Path))
                dlg.InitialDirectory = Model.Path;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            PathText = dlg.FolderName;
    }
}

/// <summary>
/// A base option in a path field's dropdown: either the "(Absolute path)" sentinel
/// (<see cref="Library"/> is null) or a Steam library. <see cref="Display"/> tracks the library's
/// path so the dropdown text stays current.
/// </summary>
public sealed class LibraryChoice : ObservableObject
{
    public LibraryChoice(SteamLibraryViewModel? library)
    {
        Library = library;
        if (library is not null)
            library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SteamLibraryViewModel.PathText))
                    OnPropertyChanged(nameof(Display));
            };
    }

    public SteamLibraryViewModel? Library { get; }

    public string Display =>
        Library is null ? "(Absolute path)"
        : string.IsNullOrWhiteSpace(Library.PathText) ? "(unnamed library)"
        : Library.PathText;

    // The themed ComboBox selection box renders via ToString (DisplayMemberPath is ignored there).
    public override string ToString() => Display;
}
