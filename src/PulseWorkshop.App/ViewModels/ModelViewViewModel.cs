using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Model View tab: a Crowbar-style "View in HLMV". Picks a Game Setup entry (for its model
/// viewer path), points at a compiled .mdl, launches the external model viewer on it, and shows the
/// ModelTool <c>info</c> summary (version, tris/verts, bones, hitboxes, materials, dependency sizes)
/// in a read-only box. There is no embedded viewer - the model viewer is launched as configured.
/// </summary>
public sealed class ModelViewViewModel : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly ModelViewConfig _config;
    private readonly string _modelToolPath;

    private string _mdlPath;
    private string? _modelInfo;
    private bool _isLoadingInfo;
    private string _statusMessage = "Ready.";

    public ModelViewViewModel(GameSetupViewModel gameSetup)
    {
        _gameSetup = gameSetup;
        _config = ModelViewConfig.Load();
        _modelToolPath = ToolLocator.ResolveModelToolPath();

        _mdlPath = _config.MdlPath;

        BrowseMdlCommand = new RelayCommand(BrowseMdl);
        OpenInViewerCommand = new RelayCommand(OpenInViewer, () => CanOpenInViewer);
        GoToMdlCommand = new RelayCommand(GoToMdl, () => HasMdl);
        RefreshInfoCommand = new AsyncRelayCommand(ScanInfoAsync, () => HasMdl && !IsLoadingInfo);

        // The game dropdown is shared with Compile - Simple and Package - Simple: react when either of
        // them (or Game Setup) changes the active game so this tab's selection and viewer follow.
        _gameSetup.PropertyChanged += OnGameSetupChanged;

        // Load the info for the reopened model (best-effort) on first launch.
        if (HasMdl)
            _ = ScanInfoAsync();
    }

    private void OnGameSetupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameSetupViewModel.ActiveGame))
        {
            OnPropertyChanged(nameof(SelectedGame));
            OnPropertyChanged(nameof(HasModelViewer));
            OpenInViewerCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand BrowseMdlCommand { get; }
    public RelayCommand OpenInViewerCommand { get; }
    public RelayCommand GoToMdlCommand { get; }
    public AsyncRelayCommand RefreshInfoCommand { get; }

    /// <summary>The shared Game Setup roster (game name + resolved tool paths) used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    /// <summary>The shared active game (see <see cref="GameSetupViewModel.ActiveGame"/>). Setting it
    /// here also updates Compile - Simple and Package - Simple, which bind to the same shared selection.</summary>
    public GameSetupEntryViewModel? SelectedGame
    {
        get => _gameSetup.ActiveGame;
        set => _gameSetup.ActiveGame = value;
    }

    /// <summary>The compiled .mdl to view. Setting it (to a valid file) reloads the info panel.</summary>
    public string MdlPath
    {
        get => _mdlPath;
        set
        {
            if (SetField(ref _mdlPath, value ?? string.Empty))
            {
                _config.MdlPath = _mdlPath;
                _config.Save();
                OnPropertyChanged(nameof(HasMdl));
                OpenInViewerCommand.RaiseCanExecuteChanged();
                GoToMdlCommand.RaiseCanExecuteChanged();
                RefreshInfoCommand.RaiseCanExecuteChanged();

                if (HasMdl)
                    _ = ScanInfoAsync();
                else
                    ModelInfo = null;
            }
        }
    }

    /// <summary>The ModelTool <c>info</c> summary for the current .mdl, shown read-only. Not persisted.</summary>
    public string? ModelInfo
    {
        get => _modelInfo;
        private set => SetField(ref _modelInfo, value);
    }

    public bool IsLoadingInfo
    {
        get => _isLoadingInfo;
        private set
        {
            if (SetField(ref _isLoadingInfo, value))
                RefreshInfoCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>True when a .mdl path is set and the file exists on disk.</summary>
    public bool HasMdl => !string.IsNullOrWhiteSpace(_mdlPath) && File.Exists(_mdlPath);

    /// <summary>The resolved model viewer path (from the selected game), or null.</summary>
    private string? ModelViewerPath
    {
        get
        {
            var p = SelectedGame?.ModelViewer.ResolvedPath;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
    }

    /// <summary>True when the selected game has a usable model viewer on disk (drives the warning/hint).</summary>
    public bool HasModelViewer
    {
        get
        {
            var viewer = ModelViewerPath;
            return !string.IsNullOrWhiteSpace(viewer) && File.Exists(viewer);
        }
    }

    /// <summary>Open needs both a viewer exe and a .mdl on disk.</summary>
    public bool CanOpenInViewer => HasModelViewer && HasMdl;

    /// <summary>The resolved gameinfo.txt directory (passed to the viewer as <c>-game</c>), or null.</summary>
    private string? GameInfoDir
    {
        get
        {
            var gameInfo = SelectedGame?.GameInfo.ResolvedPath;
            return string.IsNullOrWhiteSpace(gameInfo) ? null : Path.GetDirectoryName(gameInfo);
        }
    }

    /// <summary>
    /// Assigns a .mdl to the tab and (re)reads its info, without launching the viewer. Used by the
    /// Compile tabs' "View Model" button to hand a freshly-compiled model over. Unlike the
    /// <see cref="MdlPath"/> setter this always rescans, so re-viewing the same recompiled path works.
    /// </summary>
    public void LoadModel(string mdlPath)
    {
        var value = mdlPath ?? string.Empty;
        if (_mdlPath == value)
        {
            // Same path (e.g. recompiled in place): refresh the info panel explicitly.
            OnPropertyChanged(nameof(HasMdl));
            OpenInViewerCommand.RaiseCanExecuteChanged();
            GoToMdlCommand.RaiseCanExecuteChanged();
            RefreshInfoCommand.RaiseCanExecuteChanged();
            if (HasMdl)
                _ = ScanInfoAsync();
            else
                ModelInfo = null;
        }
        else
        {
            MdlPath = value; // setter persists + auto-scans
        }
    }

    /// <summary>Re-checks the .mdl / viewer on disk when the window regains focus (files can be
    /// created or deleted in another program while a path is typed).</summary>
    public void RefreshFileState()
    {
        OnPropertyChanged(nameof(HasMdl));
        OnPropertyChanged(nameof(HasModelViewer));
        OpenInViewerCommand.RaiseCanExecuteChanged();
        GoToMdlCommand.RaiseCanExecuteChanged();
        RefreshInfoCommand.RaiseCanExecuteChanged();
    }

    private void BrowseMdl()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose model (.mdl)",
            Filter = "Source model (*.mdl)|*.mdl|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(MdlPath) && Path.GetDirectoryName(MdlPath) is { Length: > 0 } dir
                && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            MdlPath = dlg.FileName;
    }

    /// <summary>
    /// Launches the configured model viewer (e.g. HLMV) on the current .mdl. When a gameinfo.txt is
    /// configured its folder is passed as <c>-game</c> so the viewer can resolve the model's
    /// materials; otherwise the .mdl is opened on its own.
    /// </summary>
    private void OpenInViewer()
    {
        var viewer = ModelViewerPath;
        if (string.IsNullOrWhiteSpace(viewer) || !File.Exists(viewer))
        {
            StatusMessage = "No model viewer configured - set it in Game Setup.";
            return;
        }
        if (!HasMdl)
        {
            StatusMessage = "Choose a .mdl to view.";
            return;
        }

        var gameDir = GameInfoDir;
        var args = !string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir)
            ? $"-game \"{gameDir}\" \"{MdlPath}\""
            : $"\"{MdlPath}\"";

        try
        {
            Process.Start(new ProcessStartInfo(viewer, args)
            {
                UseShellExecute = false,
                // Start in the viewer's own folder so relative game content it expects resolves.
                WorkingDirectory = Path.GetDirectoryName(viewer) ?? string.Empty,
            });
            StatusMessage = $"Opened {Path.GetFileName(MdlPath)} in {Path.GetFileName(viewer)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not launch the model viewer: {ex.Message}";
        }
    }

    private void GoToMdl()
    {
        if (!HasMdl)
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{MdlPath}\"")
            { UseShellExecute = true });
    }

    /// <summary>Reads the current .mdl's stats into <see cref="ModelInfo"/> via ModelTool. Best-effort.</summary>
    private async Task ScanInfoAsync()
    {
        if (!HasMdl)
        {
            ModelInfo = null;
            return;
        }

        IsLoadingInfo = true;
        StatusMessage = $"Reading {Path.GetFileName(MdlPath)}...";
        try
        {
            var result = await new ModelInfoService()
                .GetInfoAsync(new ModelInfoRequest(_modelToolPath, MdlPath));
            ModelInfo = result.Success ? result.Text : $"Model info unavailable: {result.Error}";
            StatusMessage = result.Success
                ? $"Read {Path.GetFileName(MdlPath)}."
                : "Model info unavailable.";
        }
        finally
        {
            IsLoadingInfo = false;
        }
    }
}
