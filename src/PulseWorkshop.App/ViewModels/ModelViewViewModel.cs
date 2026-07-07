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
    // (F5); a different model (or a dead instance) relaunches. Kept alongside the model it is showing
    // and the temp stage it loaded (so a refresh can re-stage a recompiled model over the same path).
    private Process? _viewerProcess;
    private string? _viewerMdlPath;
    private string? _viewerStagedMdl;

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

    /// <summary>The resolved gameinfo.txt directory (the mod folder the model may live under), or null.</summary>
    private string? GameInfoDir
    {
        get
        {
            var gameInfo = SelectedGame?.GameInfo.ResolvedPath;
            if (string.IsNullOrWhiteSpace(gameInfo))
                return null;
            var dir = Path.GetDirectoryName(gameInfo);
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
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

    // The files that make up a compiled model, keyed off the .mdl's stem (e.g. "foo.mdl", "foo.vvd",
    // "foo.dx90.vtx"). Only these travel to the temp stage - materials never do (HLMV resolves those
    // through its own game mount).
    private static readonly string[] ModelFileSuffixes =
    {
        ".mdl", ".vvd", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx", ".phy", ".ani",
    };

    /// <summary>
    /// Launches the configured model viewer (e.g. HLMV) on the current .mdl. HLMV loads a model passed
    /// on the command line through the game filesystem (its "(Steam) Load Model" path), so it needs
    /// <c>-game</c> for context - but it crashes when that model physically lives inside the mounted
    /// tree. So when the .mdl is inside the game tree we first stage just its model files
    /// (.mdl/.vvd/.vtx/.phy/.ani - not materials) to a temp folder outside the tree and open that copy;
    /// <c>-game</c> still supplies the filesystem context and resolves the model's textures.
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

        // Already showing this same model in our instance -> refresh it in place (F5) instead of
        // relaunching, so there is no window flash. Re-stage first so a recompiled model is what the
        // viewer re-reads on refresh.
        if (IsViewerShowing(MdlPath))
        {
            if (_viewerStagedMdl is not null)
                TryRestage(_viewerStagedMdl);
            if (TryRefreshViewer())
            {
                StatusMessage = $"Refreshed {Path.GetFileName(MdlPath)} in {Path.GetFileName(viewer)}.";
                return;
            }
            // Refresh could not be delivered - fall through to a clean relaunch.
        }

        // Only one viewer instance from this tab: if one is still open (a different model, or refresh
        // failed), close it first so this Open relaunches rather than stacking a second window.
        // Waiting for exit also releases its lock on the previous temp stage, so the stale-stage
        // cleanup below can remove it.
        CloseViewer();

        // Inside the game tree -> stage the model files out to temp to dodge the mounted-load crash.
        var mdlToOpen = MdlPath;
        var staged = false;
        var gameDir = GameInfoDir;
        var hasGame = gameDir is not null && Directory.Exists(gameDir);
        if (hasGame && IsInsideGameTree(MdlPath, gameDir!))
        {
            var stagedMdl = StageModelFiles(MdlPath);
            if (stagedMdl is not null)
            {
                mdlToOpen = stagedMdl;
                staged = true;
            }
        }

        // -game gives HLMV the filesystem context it loads the model (and its textures) through.
        var args = hasGame
            ? $"-game \"{gameDir}\" \"{mdlToOpen}\""
            : $"\"{mdlToOpen}\"";

        try
        {
            _viewerProcess = Process.Start(new ProcessStartInfo(viewer, args)
            {
                UseShellExecute = false,
                // Start in the viewer's own folder so relative game content it expects resolves.
                WorkingDirectory = Path.GetDirectoryName(viewer) ?? string.Empty,
            });
            _viewerMdlPath = MdlPath;
            _viewerStagedMdl = staged ? mdlToOpen : null;
            StatusMessage = staged
                ? $"Staged {Path.GetFileName(MdlPath)} to temp and opened it in {Path.GetFileName(viewer)}."
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

    /// <summary>Re-copies the model files over the existing temp stage so a recompiled model is what
    /// the viewer re-reads on refresh. Tolerant of the viewer holding a file open (refresh then just
    /// reloads the existing copy).</summary>
    private void TryRestage(string stagedMdl)
    {
        try
        {
            var dir = Path.GetDirectoryName(stagedMdl);
            var stem = Path.GetFileNameWithoutExtension(MdlPath);
            if (dir is not null && !string.IsNullOrEmpty(stem))
                CopyModelFiles(MdlPath, dir, stem);
        }
        catch
        {
            // Viewer holds the staged file(s) open - the refresh below reloads the existing copy.
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
    /// waits briefly for it to exit, so a relaunch is a clean single-instance reload and the previous
    /// temp stage is unlocked for cleanup. No-op when nothing is open.</summary>
    private void CloseViewer()
    {
        var proc = _viewerProcess;
        _viewerProcess = null;
        _viewerMdlPath = null;
        _viewerStagedMdl = null;
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

    /// <summary>True when <paramref name="mdlPath"/> sits inside the game install tree (the folder that
    /// contains the mod dir the gameinfo lives in), i.e. it would be loaded through the game mount.</summary>
    private static bool IsInsideGameTree(string mdlPath, string gameDir)
    {
        // The game "tree" is the install root - the parent of the gameinfo's mod folder - so models
        // under any of the game's mounted sub-folders count, not just the mod folder itself.
        var trimmed = gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetDirectoryName(trimmed);
        return IsUnder(mdlPath, trimmed) || (root is not null && IsUnder(mdlPath, root));
    }

    private static bool IsUnder(string path, string dir)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var baseDir = Path.GetFullPath(dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Copies the model's files (see <see cref="ModelFileSuffixes"/>) into a fresh temp folder
    /// and returns the staged .mdl path, or null if nothing could be staged. Materials are not copied.</summary>
    private string? StageModelFiles(string mdlPath)
    {
        try
        {
            var srcDir = Path.GetDirectoryName(mdlPath);
            var stem = Path.GetFileNameWithoutExtension(mdlPath);
            if (string.IsNullOrEmpty(srcDir) || string.IsNullOrEmpty(stem))
                return null;

            var tempRoot = Path.Combine(Path.GetTempPath(), "PulseWorkshop", "ModelView");
            CleanStaleStages(tempRoot); // best-effort: drop earlier stages whose viewer has closed
            var dest = Path.Combine(tempRoot, stem + "_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dest);
            return CopyModelFiles(mdlPath, dest, stem);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not stage model files: {ex.Message}";
            return null;
        }
    }

    /// <summary>Copies the model file set (see <see cref="ModelFileSuffixes"/>) from the .mdl's folder
    /// into <paramref name="destDir"/>, overwriting. Returns the destination .mdl path if it landed.</summary>
    private static string? CopyModelFiles(string mdlPath, string destDir, string stem)
    {
        var srcDir = Path.GetDirectoryName(mdlPath);
        if (string.IsNullOrEmpty(srcDir))
            return null;
        foreach (var suffix in ModelFileSuffixes)
        {
            var src = Path.Combine(srcDir, stem + suffix);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(destDir, stem + suffix), overwrite: true);
        }
        var stagedMdl = Path.Combine(destDir, stem + ".mdl");
        return File.Exists(stagedMdl) ? stagedMdl : null;
    }

    /// <summary>Best-effort delete of previous temp stages. The one a still-open viewer holds is
    /// locked and simply skipped; it gets cleaned on a later launch once that viewer closes.</summary>
    private static void CleanStaleStages(string tempRoot)
    {
        try
        {
            if (!Directory.Exists(tempRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* locked by an open viewer - leave it */ }
            }
        }
        catch
        {
            // Never let cleanup failures block a launch.
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
