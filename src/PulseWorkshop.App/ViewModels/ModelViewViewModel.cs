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

    // The viewer instance this tab launched. A second Open on the same model refreshes it in place
    // (F5); a different model (or a dead instance) relaunches. Kept alongside the model it is showing.
    private Process? _viewerProcess;
    private string? _viewerMdlPath;

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

    /// <summary>The resolved gameinfo.txt of the selected game (the mod the viewer mounts), or null.</summary>
    private string? GameInfoPath
    {
        get
        {
            var gameInfo = SelectedGame?.GameInfo.ResolvedPath;
            return string.IsNullOrWhiteSpace(gameInfo) ? null : gameInfo;
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
    /// Launches the configured model viewer (e.g. HLMV) on the current .mdl. HLMV loads models through
    /// the game filesystem, so a model the game cannot see does not load at all - see
    /// <see cref="HlmvGameStage"/>, which copies such a model into a fake game folder beside the real
    /// mod folder and hands back the <c>-game</c> folder to launch with.
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

        // Where the viewer has to load the model from (staging it into the fake game folder when the
        // real game cannot reach it). Re-run on a refresh too: it re-copies only what changed, so a
        // recompiled model is what the viewer re-reads.
        var launch = HlmvGameStage.Prepare(MdlPath, GameInfoPath);

        // Already showing this same model in our instance -> refresh it in place (F5) instead of
        // relaunching, so there is no window flash.
        if (IsViewerShowing(MdlPath) && TryRefreshViewer())
        {
            StatusMessage = $"Refreshed {Path.GetFileName(MdlPath)} in {Path.GetFileName(viewer)}.";
            return;
        }

        // Only one viewer instance from this tab: if one is still open (a different model, or refresh
        // failed), close it first so this Open relaunches rather than stacking a second window.
        CloseViewer();

        // -game gives HLMV the filesystem context it loads the model (and its materials) through.
        var args = launch.GameDir is null
            ? $"\"{launch.ModelPath}\""
            : $"-game \"{launch.GameDir}\" \"{launch.ModelPath}\"";

        try
        {
            _viewerProcess = Process.Start(new ProcessStartInfo(viewer, args)
            {
                UseShellExecute = false,
                // Start in the viewer's own folder so relative game content it expects resolves.
                WorkingDirectory = Path.GetDirectoryName(viewer) ?? string.Empty,
            });
            _viewerMdlPath = MdlPath;
            StatusMessage = launch.Problem is not null
                ? $"Opened {Path.GetFileName(MdlPath)} in {Path.GetFileName(viewer)}, but it could not be staged into the game ({launch.Problem}) - it may fail to load."
                : launch.IsStaged
                    ? $"Staged {Path.GetFileName(MdlPath)} into the game and opened it in {Path.GetFileName(viewer)}."
                    : $"Opened {Path.GetFileName(MdlPath)} in {Path.GetFileName(viewer)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not launch the model viewer: {ex.Message}";
        }
    }

    /// <summary>True when the viewer instance this tab launched is still running and is showing
    /// <paramref name="mdlPath"/> - the case where Open should refresh in place rather than relaunch.</summary>
    private bool IsViewerShowing(string mdlPath)
    {
        var proc = _viewerProcess;
        if (proc is null || !string.Equals(_viewerMdlPath, mdlPath, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Posts an F5 (Refresh) keystroke to the launched viewer's main window so it reloads the
    /// current model in place. Returns false when the window is not available (caller then relaunches).</summary>
    private bool TryRefreshViewer()
    {
        var proc = _viewerProcess;
        if (proc is null)
            return false;
        try
        {
            proc.Refresh();
            if (proc.HasExited)
                return false;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return false;

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            // WM_KEYDOWN/UP for VK_F5 (scan code 0x3F). The viewer's message loop translates it to
            // its Refresh accelerator; posting avoids depending on our window having input focus.
            PostMessage(hwnd, WM_KEYDOWN, VK_F5, 0x003F0001);
            PostMessage(hwnd, WM_KEYUP, VK_F5, unchecked((nint)0xC03F0001));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes the viewer instance this tab previously launched (if it is still running) and
    /// waits briefly for it to exit, so a relaunch is a clean single-instance reload and the staged
    /// copy is unlocked for cleanup. No-op when nothing is open.</summary>
    private void CloseViewer()
    {
        var proc = _viewerProcess;
        _viewerProcess = null;
        _viewerMdlPath = null;
        if (proc is null)
            return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        {
            // Already gone, access denied, or never had a window - nothing to reload.
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>Ends the session's viewing: closes the viewer this tab launched (so it releases the
    /// staged files) and deletes the fake game folder(s) it staged into. Called on app shutdown.</summary>
    public void Shutdown()
    {
        CloseViewer();
        HlmvGameStage.CleanupAll();
    }

    private void GoToMdl()
    {
        if (!HasMdl)
            return;
        ShellOpen.Reveal(MdlPath);
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

    // --- Win32: post an F5 (Refresh) to the viewer's window for an in-place reload ----------------

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const nint VK_F5 = 0x74;
    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
