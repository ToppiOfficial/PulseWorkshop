using System.Collections.ObjectModel;
using System.Diagnostics;
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
/// The Compile tab: a friendlier take on Crowbar's "Compile MDL" Simple mode. Picks a Game Setup
/// entry (for its studiomdl + gameinfo.txt paths), runs studiomdl on one .qc with live output, and
/// gathers the compiled model files to the chosen destination.
///
/// Live studiomdl output streams into this VM's own terminal (<see cref="ConsoleText"/>), embedded
/// in the Compile - Simple tab. It is deliberately separate from the Workshop terminal so the two
/// tools' output never mixes.
/// </summary>
public sealed class CompileViewModel : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly CompileConfig _config;
    private readonly string _modelToolPath;

    private GameSetupEntryViewModel? _selectedGame;
    private string _qcPath;
    private string _extraOptions;
    private OutputModeChoice _selectedOutputMode;
    private string _subfolderName;
    private string _workFolderPath;
    private bool _getMaterialOnCompile;
    private bool _localizeMaterials;
    private bool _flatPatchShader;
    private bool _isCompiling;
    private string _statusMessage = "Ready.";
    private string? _lastMdlPath;

    public CompileViewModel(GameSetupViewModel gameSetup)
    {
        _gameSetup = gameSetup;
        _config = CompileConfig.Load();
        _modelToolPath = ToolLocator.ResolveModelToolPath();

        _qcPath = _config.QcPath;
        _extraOptions = _config.ExtraOptions;
        _workFolderPath = _config.WorkFolderPath;
        _subfolderName = string.IsNullOrWhiteSpace(_config.SubfolderName)
            ? DefaultSubfolderName
            : _config.SubfolderName;
        _selectedOutputMode = OutputModes.FirstOrDefault(o => o.Mode == _config.OutputMode)
            ?? OutputModes[0];
        _selectedGame = _config.LastGameId is { } id
            ? _gameSetup.Games.FirstOrDefault(g => g.Model.Id == id)
            : _gameSetup.Games.FirstOrDefault();
        _getMaterialOnCompile = _config.GetMaterialOnCompile;
        _localizeMaterials = _config.LocalizeMaterials;
        _flatPatchShader = _config.FlatPatchShader;

        BrowseQcCommand = new RelayCommand(BrowseQc);
        BrowseWorkFolderCommand = new RelayCommand(BrowseWorkFolder);
        CompileCommand = new AsyncRelayCommand(CompileAsync, () => CanCompile);
        ClearConsoleCommand = new RelayCommand(ClearConsole);
        GoToMdlCommand = new RelayCommand(GoToMdl, () => !string.IsNullOrEmpty(LastMdlPath));
    }

    // --- Embedded terminal (studiomdl output only) -----------------------------------------

    private const int MaxConsoleLines = 1000;

    // Backing line buffer (oldest first), kept so the line cap can be enforced; the UI binds to
    // ConsoleText, a plain multi-line string, so the terminal is fully selectable for copy/paste.
    private readonly List<string> _consoleLines = new();
    private string _consoleText = string.Empty;

    /// <summary>The Compile terminal's full text, one studiomdl line per row, oldest first.</summary>
    public string ConsoleText
    {
        get => _consoleText;
        private set => SetField(ref _consoleText, value);
    }

    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand GoToMdlCommand { get; }

    /// <summary>The .mdl produced by the last successful compile - used to show the "Go to file" button.</summary>
    public string? LastMdlPath
    {
        get => _lastMdlPath;
        private set
        {
            if (SetField(ref _lastMdlPath, value))
                GoToMdlCommand.RaiseCanExecuteChanged();
        }
    }

    private void GoToMdl()
    {
        if (string.IsNullOrEmpty(LastMdlPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastMdlPath}\"")
            { UseShellExecute = true });
    }

    private void ClearConsole()
    {
        _consoleLines.Clear();
        ConsoleText = string.Empty;
    }

    // studiomdl / ModelTool output arrives on background threads (Process.OutputDataReceived);
    // marshal to the UI thread before touching the line buffer bound to the terminal.
    private void Log(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            AppendConsoleLine(line);
        else
            dispatcher.BeginInvoke(() => AppendConsoleLine(line));
    }

    // Yields a Background-priority item to the dispatcher. Because BeginInvoke uses Normal
    // priority, awaiting this guarantees that all background-queued log lines have been
    // appended before we write our own marker lines on the UI thread.
    private static System.Threading.Tasks.Task FlushLogAsync() =>
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

    /// <summary>The shared Game Setup roster (game name + resolved tool paths) used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    /// <summary>The output-destination choices shown in the dropdown.</summary>
    public IReadOnlyList<OutputModeChoice> OutputModes { get; } = new[]
    {
        new OutputModeChoice(CompileOutputMode.Subfolder, "Subfolder (beside the .qc)"),
        new OutputModeChoice(CompileOutputMode.WorkFolder, "Work folder (custom location)"),
        new OutputModeChoice(CompileOutputMode.LeaveInGame, "Leave in game (don't copy)"),
    };

    public RelayCommand BrowseQcCommand { get; }
    public RelayCommand BrowseWorkFolderCommand { get; }
    public AsyncRelayCommand CompileCommand { get; }

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                _config.LastGameId = value?.Model.Id;
                Save();
                OnInputsChanged();
            }
        }
    }

    public string QcPath
    {
        get => _qcPath;
        set
        {
            if (SetField(ref _qcPath, value ?? string.Empty))
            {
                _config.QcPath = _qcPath;
                Save();
                OnInputsChanged();
            }
        }
    }

    public string ExtraOptions
    {
        get => _extraOptions;
        set
        {
            if (SetField(ref _extraOptions, value ?? string.Empty))
            {
                _config.ExtraOptions = _extraOptions;
                Save();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    public OutputModeChoice SelectedOutputMode
    {
        get => _selectedOutputMode;
        set
        {
            if (SetField(ref _selectedOutputMode, value ?? OutputModes[0]))
            {
                _config.OutputMode = _selectedOutputMode.Mode;
                Save();
                OnPropertyChanged(nameof(ShowSubfolderName));
                OnPropertyChanged(nameof(ShowWorkFolder));
                OnPropertyChanged(nameof(CanGetMaterials));
                OnPropertyChanged(nameof(CanConfigureMaterials));
                CompileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SubfolderName
    {
        get => _subfolderName;
        set
        {
            if (SetField(ref _subfolderName, value ?? string.Empty))
            {
                _config.SubfolderName = _subfolderName;
                Save();
                CompileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string WorkFolderPath
    {
        get => _workFolderPath;
        set
        {
            if (SetField(ref _workFolderPath, value ?? string.Empty))
            {
                _config.WorkFolderPath = _workFolderPath;
                Save();
                CompileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowSubfolderName => _selectedOutputMode.Mode == CompileOutputMode.Subfolder;
    public bool ShowWorkFolder => _selectedOutputMode.Mode == CompileOutputMode.WorkFolder;

    /// <summary>True when the output mode produces a copy destination (Subfolder or WorkFolder).
    /// "Leave in game" leaves the .mdl in the game folder where the engine already finds its materials.</summary>
    public bool CanGetMaterials => _selectedOutputMode.Mode != CompileOutputMode.LeaveInGame;

    /// <summary>True when material gathering is both available and enabled - controls Localize and Flat patch.</summary>
    public bool CanConfigureMaterials => CanGetMaterials && _getMaterialOnCompile;

    public bool GetMaterialOnCompile
    {
        get => _getMaterialOnCompile;
        set
        {
            if (SetField(ref _getMaterialOnCompile, value))
            {
                _config.GetMaterialOnCompile = value;
                Save();
                OnPropertyChanged(nameof(CanConfigureMaterials));
            }
        }
    }

    public bool LocalizeMaterials
    {
        get => _localizeMaterials;
        set
        {
            if (SetField(ref _localizeMaterials, value))
            {
                _config.LocalizeMaterials = value;
                Save();
            }
        }
    }

    public bool FlatPatchShader
    {
        get => _flatPatchShader;
        set
        {
            if (SetField(ref _flatPatchShader, value))
            {
                _config.FlatPatchShader = value;
                Save();
            }
        }
    }

    public bool IsCompiling
    {
        get => _isCompiling;
        private set
        {
            if (SetField(ref _isCompiling, value))
                CompileCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>A read-only Crowbar-style preview of the exact studiomdl command line that will run.</summary>
    public string CommandPreview
    {
        get
        {
            var studio = SelectedGame?.ModelCompiler.ResolvedPath;
            var gameInfoDir = GameInfoDir;
            if (string.IsNullOrWhiteSpace(studio) || string.IsNullOrWhiteSpace(gameInfoDir)
                || string.IsNullOrWhiteSpace(QcPath))
                return "Select a game, gameinfo.txt, and a .qc to preview the command.";

            return $"\"{studio}\" {ModelCompileService.BuildArguments(gameInfoDir, QcPath, ExtraOptions)}";
        }
    }

    /// <summary>The resolved gameinfo.txt directory (the studiomdl <c>-game</c> argument), or null.</summary>
    private string? GameInfoDir
    {
        get
        {
            var gameInfo = SelectedGame?.GameInfo.ResolvedPath;
            return string.IsNullOrWhiteSpace(gameInfo) ? null : Path.GetDirectoryName(gameInfo);
        }
    }

    public bool CanCompile
    {
        get
        {
            if (IsCompiling || SelectedGame is null)
                return false;

            var studio = SelectedGame.ModelCompiler.ResolvedPath;
            if (string.IsNullOrWhiteSpace(studio) || !File.Exists(studio))
                return false;

            var gameInfoDir = GameInfoDir;
            if (string.IsNullOrWhiteSpace(gameInfoDir) || !Directory.Exists(gameInfoDir))
                return false;

            if (string.IsNullOrWhiteSpace(QcPath) || !File.Exists(QcPath))
                return false;

            // The chosen destination must be specified for the modes that need one.
            return _selectedOutputMode.Mode switch
            {
                CompileOutputMode.Subfolder => !string.IsNullOrWhiteSpace(SubfolderName),
                CompileOutputMode.WorkFolder => !string.IsNullOrWhiteSpace(WorkFolderPath),
                _ => true,
            };
        }
    }

    private void OnInputsChanged()
    {
        OnPropertyChanged(nameof(CommandPreview));
        CompileCommand.RaiseCanExecuteChanged();
    }

    private void Save() => _config.Save();

    private void BrowseQc()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose QC file",
            Filter = "QC file (*.qc)|*.qc|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(QcPath) && Path.GetDirectoryName(QcPath) is { Length: > 0 } dir
                && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            QcPath = dlg.FileName;
    }

    private void BrowseWorkFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose work folder" };
        try
        {
            if (!string.IsNullOrWhiteSpace(WorkFolderPath) && Directory.Exists(WorkFolderPath))
                dlg.InitialDirectory = WorkFolderPath;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            WorkFolderPath = dlg.FolderName;
    }

    private async Task CompileAsync()
    {
        if (SelectedGame is null || GameInfoDir is not { } gameInfoDir)
            return;

        var destination = ResolveDestinationBase();
        var request = new CompileRequest(
            StudioMdlPath: SelectedGame.ModelCompiler.ResolvedPath ?? string.Empty,
            GameInfoDir: gameInfoDir,
            QcPath: QcPath,
            ExtraOptions: ExtraOptions,
            DestinationBase: destination);

        var service = new ModelCompileService();
        service.Output += Log;

        IsCompiling = true;
        LastMdlPath = null;
        StatusMessage = $"Compiling {Path.GetFileName(QcPath)}...";
        Log($"=== Compiling {QcPath} ===");

        try
        {
            var result = await service.CompileAsync(request);
            await FlushLogAsync();

            if (!result.Success)
            {
                StatusMessage = $"Compile failed: {result.Error}";
                Log($"FAILED: {result.Error}");
                return;
            }

            var copiedNote = destination is null
                ? "left in game folder"
                : $"{result.CopiedFiles.Count} file(s) copied to {destination}";
            StatusMessage = $"Compiled {result.CompiledMdls.Count} model(s) - {copiedNote}.";
            Log($"=== Done. {StatusMessage} ===");

            // Prefer the copied .mdl so "Go to file" opens the destination folder; fall back to the
            // in-game path when the output mode is "leave in game".
            LastMdlPath = result.CopiedFiles.FirstOrDefault(f =>
                              f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                          ?? result.CompiledMdls.FirstOrDefault();

            if (GetMaterialOnCompile && result.CompiledMdls.Count > 0)
                await RunMaterialCopyAsync(result.CompiledMdls, destination);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compile failed: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            service.Output -= Log;
            IsCompiling = false;
        }
    }

    /// <summary>
    /// Invokes ModelTool for each compiled MDL to copy its VMT + VTF files to the appropriate
    /// destination. Output is streamed into the same compile terminal. When the output mode is
    /// "leave in game" (no model copy), materials go to the game's own materials/ directory.
    /// </summary>
    private async Task RunMaterialCopyAsync(IReadOnlyList<string> mdlPaths, string? compileDest)
    {
        var gameInfoPath = SelectedGame?.GameInfo.ResolvedPath;
        if (string.IsNullOrEmpty(gameInfoPath) || !File.Exists(gameInfoPath))
        {
            Log("[Materials] Skipped: gameinfo.txt not configured.");
            return;
        }

        // Materials are only copied when the compile itself copies files somewhere (Subfolder or
        // WorkFolder). "Leave in game" leaves the .mdl in the game folder where the engine already
        // finds its materials, so there is nothing to copy.
        if (compileDest is null)
        {
            Log("[Materials] Skipped: output mode is 'leave in game'.");
            return;
        }
        var matDest = compileDest;

        Log("--- Material copy ---");
        var svc = new MaterialCopyService();
        svc.Output += Log;
        try
        {
            foreach (var mdl in mdlPaths)
            {
                var req = new MaterialCopyRequest(
                    ToolPath:     _modelToolPath,
                    MdlPath:      mdl,
                    GameInfoPath: gameInfoPath,
                    DestDir:      matDest,
                    Localize:     LocalizeMaterials,
                    FlatPatch:    FlatPatchShader);

                var r = await svc.CopyAsync(req);
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

    /// <summary>The folder compiled files are copied to, or null for "leave in game".</summary>
    private string? ResolveDestinationBase() => _selectedOutputMode.Mode switch
    {
        CompileOutputMode.Subfolder when Path.GetDirectoryName(QcPath) is { Length: > 0 } qcDir =>
            Path.Combine(qcDir, string.IsNullOrWhiteSpace(SubfolderName) ? DefaultSubfolderName : SubfolderName),
        CompileOutputMode.WorkFolder when !string.IsNullOrWhiteSpace(WorkFolderPath) =>
            WorkFolderPath,
        _ => null,
    };

    /// <summary>Default subfolder name from the app version, e.g. 0.2.5 -> "compiled025".</summary>
    private static string DefaultSubfolderName
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "compiled" : $"compiled{v.Major}{v.Minor}{v.Build}";
        }
    }
}

/// <summary>An output-destination dropdown entry (the themed ComboBox renders via ToString).</summary>
public sealed record OutputModeChoice(CompileOutputMode Mode, string Label)
{
    public override string ToString() => Label;
}
