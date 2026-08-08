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
/// The Package - Simple tab: a friendlier take on Crowbar's "Pack" tab. Picks a Game Setup entry
/// (for its packer tool - vpk/gmad), packs one content folder into a single <c>.vpk</c>/<c>.gma</c>
/// with live output, and optionally renames the produced package. Unlike the Advanced tab there is
/// no project, no entry list and no pre-asset pipeline - just one folder in, one package out.
///
/// Live packer output streams into the shared app-wide console (the detached ConsoleWindow).
/// </summary>
public sealed class PackageViewModel : ObservableObject
{
    private readonly GameSetupViewModel _gameSetup;
    private readonly ConsoleViewModel _console;
    private readonly PackageConfig _config;

    private string _folderPath;
    private string _outputName;
    private string _extraOptions;
    private bool _multiVpk;
    private bool _ignoreWhitelistWarnings;
    private bool _writeLogToFile;
    private string _gModTitle;
    private GModTypeChoice _selectedGModType;
    private bool _isPackaging;
    private string _statusMessage = "Ready.";
    private string? _lastPackagePath;
    private CancellationTokenSource? _cancelSource;
    private System.Text.StringBuilder? _logBuffer;
    private IReadOnlyList<FileTreeNodeViewModel> _contentTree = Array.Empty<FileTreeNodeViewModel>();
    private readonly object _logLock = new();

    public PackageViewModel(GameSetupViewModel gameSetup, ConsoleViewModel console)
    {
        _gameSetup = gameSetup;
        _console = console;
        _config = PackageConfig.Load();

        _folderPath = _config.FolderPath;
        _outputName = _config.OutputName;
        _extraOptions = _config.ExtraOptions;
        _multiVpk = _config.MultiVpk;
        _ignoreWhitelistWarnings = _config.IgnoreWhitelistWarnings;
        _writeLogToFile = _config.WriteLogToFile;

        // Garry's Mod addon.json fields (gmad only).
        GModTypes = new(GModAddon.Types.Select(t => new GModTypeChoice(t.Value, t.Label)));
        _selectedGModType = GModTypes.FirstOrDefault(t => t.Value == _config.GModType) ?? GModTypes[0];
        _gModTitle = _config.GModTitle;
        GModTags = new(GModAddon.Tags.Select(t =>
            new GModTagChoice(this, t.Value, t.Label, _config.GModTags.Contains(t.Value))));
        UpdateTagAvailability();

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);
        PackageCommand = new AsyncRelayCommand(PackageAsync, () => CanPackage);
        CancelCommand = new RelayCommand(Cancel, () => IsPackaging);
        GoToPackageCommand = new RelayCommand(GoToPackage, () => !string.IsNullOrEmpty(LastPackagePath));
        ViewPackageCommand = new RelayCommand(ViewPackage, () => !string.IsNullOrEmpty(LastPackagePath));
        RefreshTreeCommand = new RelayCommand(RefreshTree);
        RefreshTree();

        // The game dropdown is shared with Compile - Simple and Model View: react when either of them
        // (or Game Setup) changes the active game so this tab's selection and command preview follow.
        _gameSetup.PropertyChanged += OnGameSetupChanged;
    }

    private void OnGameSetupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameSetupViewModel.ActiveGame))
        {
            OnPropertyChanged(nameof(SelectedGame));
            OnInputsChanged();
        }
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand PackageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand GoToPackageCommand { get; }

    /// <summary>Opens the last-packaged file in the Unpack tab to browse its contents.</summary>
    public RelayCommand ViewPackageCommand { get; }

    /// <summary>Raised when the user clicks "View Package": the packed file to hand to the Unpack tab.
    /// Wired by <c>MainViewModel</c> to open it there and switch tabs.</summary>
    public event Action<string>? ViewPackageRequested;

    /// <summary>The Unpack tab, set by <c>MainViewModel</c>: before packing, the pack asks it to
    /// temporarily release the output package if it happens to be open there (its read handles
    /// would block the packer), then reopens it once the pack finishes.</summary>
    public UnpackViewModel? UnpackTab { get; set; }

    private void ViewPackage()
    {
        if (!string.IsNullOrEmpty(LastPackagePath))
            ViewPackageRequested?.Invoke(LastPackagePath);
    }

    /// <summary>The shared Game Setup roster (game name + resolved tool paths) used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _gameSetup.Games;

    // --- Bound state --------------------------------------------------------------------------

    /// <summary>The shared active game (see <see cref="GameSetupViewModel.ActiveGame"/>). Setting it
    /// here also updates Compile - Simple and Model View, which bind to the same shared selection.</summary>
    public GameSetupEntryViewModel? SelectedGame
    {
        get => _gameSetup.ActiveGame;
        set => _gameSetup.ActiveGame = value;
    }

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (SetField(ref _folderPath, value ?? string.Empty))
            {
                _config.FolderPath = _folderPath;
                Save();
                OnInputsChanged();
                OpenFolderCommand.RaiseCanExecuteChanged();
                RefreshTree();
            }
        }
    }

    public string OutputName
    {
        get => _outputName;
        set
        {
            if (SetField(ref _outputName, value ?? string.Empty))
            {
                _config.OutputName = _outputName;
                Save();
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

    /// <summary>vpk only: pack into multiple chunk files (<c>-M</c>) instead of one file.</summary>
    public bool MultiVpk
    {
        get => _multiVpk;
        set
        {
            if (SetField(ref _multiVpk, value))
            {
                _config.MultiVpk = value;
                Save();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    /// <summary>gmad only: warn about non-whitelisted files and continue instead of failing (<c>-warninvalid</c>).</summary>
    public bool IgnoreWhitelistWarnings
    {
        get => _ignoreWhitelistWarnings;
        set
        {
            if (SetField(ref _ignoreWhitelistWarnings, value))
            {
                _config.IgnoreWhitelistWarnings = value;
                Save();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    /// <summary>Also write the packer's console output to a <c>.log</c> file beside the folder.</summary>
    public bool WriteLogToFile
    {
        get => _writeLogToFile;
        set
        {
            if (SetField(ref _writeLogToFile, value))
            {
                _config.WriteLogToFile = value;
                Save();
            }
        }
    }

    // --- Garry's Mod addon.json (gmad only) ---------------------------------------------------

    /// <summary>True when the selected game's packer is gmad (Garry's Mod) - gates the addon.json fields.</summary>
    public bool IsGmodPacker
    {
        get
        {
            var p = PackerToolPath;
            return !string.IsNullOrWhiteSpace(p)
                && Path.GetFileNameWithoutExtension(p).Contains("gmad", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when the selected game's packer is vpk (any non-gmad packer) - gates the Multi-VPK option.</summary>
    public bool IsVpkPacker => !string.IsNullOrWhiteSpace(PackerToolPath) && !IsGmodPacker;

    /// <summary>The GMod addon "type" choices for the dropdown.</summary>
    public ObservableCollection<GModTypeChoice> GModTypes { get; }

    /// <summary>The GMod addon "tag" choices (checkboxes, choose up to two).</summary>
    public ObservableCollection<GModTagChoice> GModTags { get; }

    public GModTypeChoice SelectedGModType
    {
        get => _selectedGModType;
        set
        {
            if (SetField(ref _selectedGModType, value ?? GModTypes[0]))
            {
                _config.GModType = _selectedGModType.Value;
                Save();
            }
        }
    }

    public string GModTitle
    {
        get => _gModTitle;
        set
        {
            if (SetField(ref _gModTitle, value ?? string.Empty))
            {
                _config.GModTitle = _gModTitle;
                Save();
            }
        }
    }

    /// <summary>Called by a tag checkbox when it toggles: enforces the two-tag cap and persists.</summary>
    public void OnTagSelectionChanged()
    {
        UpdateTagAvailability();
        _config.GModTags = GModTags.Where(t => t.IsSelected).Select(t => t.Value).ToList();
        Save();
    }

    // Disables unchecked tags once the cap is reached (checked ones stay enabled so they can be cleared).
    private void UpdateTagAvailability()
    {
        var atMax = GModTags.Count(t => t.IsSelected) >= GModAddon.MaxTags;
        foreach (var t in GModTags)
            t.IsEnabled = t.IsSelected || !atMax;
    }

    public bool IsPackaging
    {
        get => _isPackaging;
        private set
        {
            if (SetField(ref _isPackaging, value))
            {
                PackageCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>The package produced by the last successful run - drives the "Go to file" button.</summary>
    public string? LastPackagePath
    {
        get => _lastPackagePath;
        private set
        {
            if (SetField(ref _lastPackagePath, value))
            {
                GoToPackageCommand.RaiseCanExecuteChanged();
                ViewPackageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // --- Content preview tree -------------------------------------------------------------------

    /// <summary>Rebuilds the read-only folder tree (also the Refresh button - the tree is a snapshot,
    /// nothing watches the disk).</summary>
    public RelayCommand RefreshTreeCommand { get; }

    /// <summary>The folder to pack as a single-root tree, or empty when the path isn't a folder.
    /// A one-element list because TreeView wants a collection, not a node.</summary>
    public IReadOnlyList<FileTreeNodeViewModel> ContentTree
    {
        get => _contentTree;
        private set
        {
            if (SetField(ref _contentTree, value))
                OnPropertyChanged(nameof(TreeIsEmpty));
        }
    }

    public bool TreeIsEmpty => ContentTree.Count == 0;

    private void RefreshTree() =>
        ContentTree = FileTreeNodeViewModel.ForFolder(FolderPath) is { } root
            ? new[] { root }
            : Array.Empty<FileTreeNodeViewModel>();

    /// <summary>A read-only Crowbar-style preview of the exact packer command line that will run.</summary>
    public string CommandPreview
    {
        get
        {
            var packer = PackerToolPath;
            if (string.IsNullOrWhiteSpace(packer) || string.IsNullOrWhiteSpace(FolderPath))
                return "Select a game with a packer tool and a folder to preview the command.";

            return PackageService.BuildCommandPreview(packer, FolderPath.TrimEnd('\\', '/'), ExtraOptions,
                MultiVpk, IgnoreWhitelistWarnings);
        }
    }

    /// <summary>The resolved packer (vpk/gmad) path, or null.</summary>
    private string? PackerToolPath
    {
        get
        {
            var p = SelectedGame?.PackerTool.ResolvedPath;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
    }

    public bool CanPackage
    {
        get
        {
            if (IsPackaging || SelectedGame is null)
                return false;

            var packer = PackerToolPath;
            if (string.IsNullOrWhiteSpace(packer) || !File.Exists(packer))
                return false;

            return !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath.TrimEnd('\\', '/'));
        }
    }

    private bool CanOpenFolder =>
        !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath.TrimEnd('\\', '/'));

    private void OnInputsChanged()
    {
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(IsGmodPacker));
        OnPropertyChanged(nameof(IsVpkPacker));
        PackageCommand.RaiseCanExecuteChanged();
    }

    private void Save() => _config.Save();

    /// <summary>Re-checks the folder on disk (enables/disables the actions) when the window is
    /// re-activated, so a folder created or deleted in another program is picked up.</summary>
    public void RefreshFileState()
    {
        PackageCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    // --- Actions ------------------------------------------------------------------------------

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder to package" };
        try
        {
            if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath.TrimEnd('\\', '/')))
                dlg.InitialDirectory = FolderPath.TrimEnd('\\', '/');
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            FolderPath = dlg.FolderName;
    }

    private void OpenFolder()
    {
        if (!CanOpenFolder) return;
        ShellOpen.Open(FolderPath);
    }

    private void GoToPackage()
    {
        if (string.IsNullOrEmpty(LastPackagePath)) return;
        ShellOpen.Reveal(LastPackagePath);
    }

    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private async Task PackageAsync()
    {
        var packer = PackerToolPath;
        if (SelectedGame is null || string.IsNullOrWhiteSpace(packer))
            return;

        var folder = FolderPath.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusMessage = "Folder to package not found.";
            return;
        }

        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;

        // If the Unpack tab is browsing the very package this pack will overwrite, its open read
        // handles would block the packer (and the rename) - close it there for the duration.
        string? reopenInUnpack = UnpackTab?.ReleaseForRepack(
            PackOutputCandidates(folder, IsGmodPacker ? ".gma" : ".vpk", OutputName));

        var service = new PackageService();
        service.Output += Log;

        IsPackaging = true;
        LastPackagePath = null;
        _logBuffer = WriteLogToFile ? new System.Text.StringBuilder() : null;
        StatusMessage = $"Packaging {Path.GetFileName(folder)}...";
        Log($"=== Packaging {folder} ===");

        try
        {
            // gmad reads an addon.json from the folder for the title/type/tags; write it first so the
            // pack has one (mirrors Crowbar / gmpublish). vpk has no such file.
            if (IsGmodPacker)
            {
                var addonPath = GModAddon.Write(folder, GModTitle, SelectedGModType.Value,
                    GModTags.Where(t => t.IsSelected).Select(t => t.Value));
                Log($"Wrote {Path.GetFileName(addonPath)} (title: \"{GModTitle}\", type: {SelectedGModType.Value}).");
            }

            var request = new PackageRequest(
                PackerToolPath: packer,
                FolderPath: folder,
                ExtraOptions: ExtraOptions,
                MultiVpk: MultiVpk,
                IgnoreWhitelistWarnings: IgnoreWhitelistWarnings);
            var result = await service.PackageAsync(request, ct);
            await FlushLogAsync();

            if (!result.Success)
            {
                if (ct.IsCancellationRequested)
                {
                    StatusMessage = "Package cancelled.";
                    Log("=== Cancelled ===");
                    return;
                }
                StatusMessage = $"Package failed: {result.Error}";
                Log($"FAILED: {result.Error}");
                return;
            }

            var finalPath = RenamePackage(result.OutputPackagePath, OutputName);
            LastPackagePath = finalPath;
            StatusMessage = $"Packaged -> {Path.GetFileName(finalPath)}.";
            Log($"=== Done. {StatusMessage} ===");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Package cancelled.";
            Log("=== Cancelled ===");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Package failed: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            service.Output -= Log;
            FlushLogFile(folder);
            IsPackaging = false;
            _cancelSource.Dispose();
            _cancelSource = null;

            // Reopen the package in the Unpack tab if we closed it there: the fresh result when
            // the pack succeeded, the old file otherwise (if it survived).
            if (reopenInUnpack is not null)
            {
                var reopen = LastPackagePath ?? reopenInUnpack;
                if (File.Exists(reopen))
                    await UnpackTab!.OpenFromPathAsync(reopen);
            }
        }
    }

    /// <summary>The package files a pack of <paramref name="folder"/> may write: the packer's own
    /// output beside the folder, plus the optional rename target (mirrors <see cref="RenamePackage"/>).</summary>
    internal static IEnumerable<string> PackOutputCandidates(string folder, string ext, string outputName)
    {
        folder = folder.TrimEnd('\\', '/'); // the packer derives its output from the trimmed folder
        yield return folder + ext;

        if (string.IsNullOrWhiteSpace(outputName))
            yield break;
        var name = Path.GetFileName(outputName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            yield break;
        if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += ext;
        yield return Path.Combine(Path.GetDirectoryName(folder) ?? string.Empty, name);
    }

    /// <summary>Writes the buffered packer output to a <c>.log</c> file beside the folder when
    /// "Write log to a file" is enabled. Best-effort - a failure here only logs a warning.</summary>
    private void FlushLogFile(string folder)
    {
        string? contents;
        lock (_logLock)
        {
            contents = _logBuffer?.ToString();
            _logBuffer = null;
        }
        if (contents is null)
            return;
        var logPath = folder + ".log";
        try
        {
            File.WriteAllText(logPath, contents);
            _console.Append($"Wrote log -> {logPath}");
        }
        catch (Exception ex)
        {
            _console.Append($"WARNING: could not write log to '{logPath}': {ex.Message}");
        }
    }

    /// <summary>Renames a freshly-produced package to <paramref name="outputName"/> (in the same folder),
    /// keeping the packer's extension when none is given and overwriting any existing file of that name.
    /// Returns the resulting path (unchanged when the name is blank or the produced path is missing). Any
    /// directory parts are stripped so it can't escape the output folder. Mirrors the Advanced tab.</summary>
    private string? RenamePackage(string? producedPath, string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName)
            || string.IsNullOrEmpty(producedPath) || !File.Exists(producedPath))
            return producedPath;

        var name = Path.GetFileName(outputName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return producedPath;

        if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += Path.GetExtension(producedPath); // keep the packer's .vpk/.gma

        var dir = Path.GetDirectoryName(producedPath) ?? string.Empty;
        var target = Path.Combine(dir, name);

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(producedPath),
                StringComparison.OrdinalIgnoreCase))
            return producedPath; // already the desired name

        try
        {
            File.Move(producedPath, target, overwrite: true);
            Log($"Renamed package -> {name}");
            return target;
        }
        catch (Exception ex)
        {
            Log($"WARNING: could not rename package to '{name}': {ex.Message}");
            return producedPath;
        }
    }

    // --- Shared console -----------------------------------------------------------------------

    // Packer output arrives one line at a time on background threads; the shared console buffers and
    // coalesces them, so we just forward each line (also capturing it for the optional .log file).
    private void Log(string line)
    {
        lock (_logLock)
            _logBuffer?.AppendLine(line);
        _console.Append(line);
    }

    // Ensures queued background output is on screen before we write our own marker lines.
    private Task FlushLogAsync() => _console.FlushAsync();
}

/// <summary>A GMod addon "type" dropdown entry (the themed ComboBox renders via ToString). <see cref="Value"/>
/// is the canonical lowercase token written to addon.json.</summary>
public sealed record GModTypeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A GMod addon "tag" checkbox (choose up to two). <see cref="IsEnabled"/> is toggled off by the
/// parent once the two-tag cap is reached so a third can't be checked.</summary>
public sealed class GModTagChoice : ObservableObject
{
    private readonly PackageViewModel _parent;
    private bool _isSelected;
    private bool _isEnabled = true;

    public GModTagChoice(PackageViewModel parent, string value, string label, bool isSelected)
    {
        _parent = parent;
        Value = value;
        Label = label;
        _isSelected = isSelected; // set directly so construction doesn't fire the parent callback
    }

    public string Value { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetField(ref _isSelected, value)) _parent.OnTagSelectionChanged(); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
}
