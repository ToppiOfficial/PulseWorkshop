using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Package - Advanced tab: a project workflow that packs content folders into <c>.vpk</c>/<c>.gma</c>
/// via the game's packer. It shares the open <c>.pw_mdlproject</c> with the Compile tab through
/// <see cref="AdvancedProjectSession"/> but edits its own <see cref="ModelProject.PackageEntries"/>
/// list. Each entry first bakes its pre-assets into the folder (<see cref="AssetPipelineService"/>),
/// then runs the packer (<see cref="PackageService"/>). Owns its own embedded terminal.
/// </summary>
public sealed class PackageAdvancedViewModel : ObservableObject
{
    private readonly AdvancedProjectSession _session;
    private PackageEntryViewModel? _selectedEntry;
    private bool _isPackaging;
    private bool _isTerminalVisible;
    private string _statusMessage = "No project open.";
    private CancellationTokenSource? _cancelSource;

    public PackageAdvancedViewModel(AdvancedProjectSession session)
    {
        _session = session;

        AddEntryCommand = new RelayCommand(AddEntry, () => IsProjectOpen);
        PackageAllCommand = new AsyncRelayCommand(PackageAllAsync, () => CanPackageAll);
        CancelCommand = new RelayCommand(Cancel, () => IsPackaging);
        ClearConsoleCommand = new RelayCommand(ClearConsole);

        // This tab persists the package entry list (with each entry's assets) into the shared project.
        _session.RegisterSync(() => _session.Project.PackageEntries =
            Entries.Select(e => { e.SyncAssets(); return e.Model; }).ToList());
        _session.ProjectChanged += OnProjectChanged;
        _session.GameChanged += OnGameChanged;

        OnProjectChanged();
    }

    // --- Commands -----------------------------------------------------------------------------

    public RelayCommand NewProjectCommand => _session.NewProjectCommand;
    public RelayCommand OpenProjectCommand => _session.OpenProjectCommand;
    public RelayCommand CloseProjectCommand => _session.CloseProjectCommand;
    public RelayCommand AddEntryCommand { get; }
    public AsyncRelayCommand PackageAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }

    /// <summary>The shared Game Setup roster used for the dropdown.</summary>
    public ObservableCollection<GameSetupEntryViewModel> Games => _session.Games;

    /// <summary>The package entries, in package order. The UI lets the user drag to reorder.</summary>
    public ObservableCollection<PackageEntryViewModel> Entries { get; } = new();

    /// <summary>The entry shown in the editor panel (master-detail, like Game Setup).</summary>
    public PackageEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
                OnPropertyChanged(nameof(HasSelectedEntry));
        }
    }

    public bool HasSelectedEntry => _selectedEntry is not null;

    /// <summary>The asset-kind choices shared with every asset row.</summary>
    public IReadOnlyList<AssetKindChoice> AssetKinds { get; } = new[]
    {
        new AssetKindChoice(AssetKind.Text, "Text"),
        new AssetKindChoice(AssetKind.Image, "Image"),
    };

    /// <summary>The image-format choices shared with every image asset row.</summary>
    public IReadOnlyList<ImageFormatChoice> ImageFormats { get; } = new[]
    {
        new ImageFormatChoice(ImageTargetFormat.Copy, "Copy (keep format)"),
        new ImageFormatChoice(ImageTargetFormat.Png, "PNG"),
        new ImageFormatChoice(ImageTargetFormat.Jpg, "JPG"),
        new ImageFormatChoice(ImageTargetFormat.Gif, "GIF"),
        new ImageFormatChoice(ImageTargetFormat.Bmp, "BMP"),
        new ImageFormatChoice(ImageTargetFormat.Tiff, "TIFF"),
        new ImageFormatChoice(ImageTargetFormat.Vtf, "VTF (VTF tool)"),
    };

    // --- Project lifecycle (delegated to the shared session) ----------------------------------

    public bool IsProjectOpen => _session.IsProjectOpen;
    public string ProjectPath => _session.ProjectPath;
    public string ProjectName => _session.ProjectName;
    public string? ProjectDir => _session.ProjectDir;

    private void OnProjectChanged()
    {
        Entries.Clear();
        foreach (var entry in _session.Project.PackageEntries)
            Entries.Add(new PackageEntryViewModel(this, entry));

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

    /// <summary>Persists the project (best-effort) via the shared session.</summary>
    public void Save() => _session.Save();

    public void RefreshCommands()
    {
        PackageAllCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddEntryCommand.RaiseCanExecuteChanged();
        foreach (var entry in Entries)
            entry.RaiseCanPackageChanged();
    }

    // --- Project-level bound state -------------------------------------------------------------

    public GameSetupEntryViewModel? SelectedGame
    {
        get => _session.SelectedGame;
        set => _session.SelectedGame = value;
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

    /// <summary>True when the selected game has a usable packer tool.</summary>
    public bool IsGameReady
    {
        get
        {
            var packer = PackerToolPath;
            return !string.IsNullOrWhiteSpace(packer) && File.Exists(packer);
        }
    }

    public bool IsPackaging
    {
        get => _isPackaging;
        private set { if (SetField(ref _isPackaging, value)) RefreshCommands(); }
    }

    public bool CanPackageAll =>
        IsProjectOpen && !IsPackaging && IsGameReady && Entries.Any(e => e.IncludeInAll);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsTerminalVisible
    {
        get => _isTerminalVisible;
        set => SetField(ref _isTerminalVisible, value);
    }

    // --- Path helpers (used by entries / assets) ----------------------------------------------

    public string ResolveAgainstProject(string path) => _session.ResolveAgainstProject(path);
    public string MakeProjectRelative(string fullPath) => _session.MakeProjectRelative(fullPath);

    // --- Entries ------------------------------------------------------------------------------

    private void AddEntry()
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder to package" };
        if (ProjectDir is { Length: > 0 } pdir && Directory.Exists(pdir))
            dlg.InitialDirectory = pdir;
        if (dlg.ShowDialog() != true)
            return;

        var model = new PackageEntry
        {
            Name = Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/')),
            FolderPath = MakeProjectRelative(dlg.FolderName),
        };
        var vm = new PackageEntryViewModel(this, model);
        Entries.Add(vm);
        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    public void CloneEntry(PackageEntryViewModel entry)
    {
        entry.SyncAssets();
        var clone = entry.Model.Clone();
        clone.Name = string.IsNullOrWhiteSpace(entry.Model.Name)
            ? "Copy"
            : entry.Model.Name + " (copy)";

        var index = Entries.IndexOf(entry);
        var vm = new PackageEntryViewModel(this, clone);
        if (index >= 0)
            Entries.Insert(index + 1, vm);
        else
            Entries.Add(vm);

        SelectedEntry = vm;
        Save();
        RefreshCommands();
    }

    public void RemoveEntry(PackageEntryViewModel entry)
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

    // --- Package ------------------------------------------------------------------------------

    private void Cancel()
    {
        _cancelSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    public async Task PackageEntryAsync(PackageEntryViewModel entry)
    {
        if (!IsGameReady)
            return;

        ClearConsole();
        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsPackaging = true;
        entry.IsPackaging = true;
        try
        {
            await PackageOneAsync(entry, ct);
        }
        finally
        {
            entry.IsPackaging = false;
            IsPackaging = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
    }

    private async Task PackageAllAsync()
    {
        if (!IsGameReady)
            return;

        var targets = Entries.Where(e => e.IncludeInAll).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No entries flagged for 'Package all'.";
            return;
        }
        if (!ConfirmPackageAll(targets.Count))
            return;

        ClearConsole();
        _cancelSource = new CancellationTokenSource();
        var ct = _cancelSource.Token;
        IsPackaging = true;
        Log($"=== Package all ({targets.Count} entr{(targets.Count == 1 ? "y" : "ies")}) ===");
        try
        {
            var ok = 0;
            var cancelled = false;
            foreach (var entry in targets)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                entry.IsPackaging = true;
                try
                {
                    if (await PackageOneAsync(entry, ct))
                        ok++;
                }
                finally
                {
                    entry.IsPackaging = false;
                }

                cancelled = ct.IsCancellationRequested;
                if (cancelled) break;
            }
            StatusMessage = cancelled
                ? $"Package all cancelled: {ok}/{targets.Count} done."
                : $"Package all done: {ok}/{targets.Count} succeeded.";
            Log($"=== {StatusMessage} ===");
        }
        finally
        {
            IsPackaging = false;
            _cancelSource.Dispose();
            _cancelSource = null;
        }
    }

    private bool ConfirmPackageAll(int count)
    {
        var message = $"Package all {count} flagged entr{(count == 1 ? "y" : "ies")} in order?";
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, "Package all",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(message, "Package all",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Bakes an entry's assets into its folder, then packs the folder. Returns success.</summary>
    private async Task<bool> PackageOneAsync(PackageEntryViewModel entry, CancellationToken ct)
    {
        var folder = entry.ResolvedFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            entry.HasError = true;
            StatusMessage = $"{entry.Name}: folder not found.";
            Log($"FAILED: folder not found ({folder}).");
            return false;
        }

        StatusMessage = $"Packaging {entry.Name}...";
        Log($"=== Packaging {entry.Name} ({folder}) ===");
        try
        {
            entry.SyncAssets();

            // 1. Pre-assets: transform + copy into the folder (sources are never mutated).
            if (entry.Model.Assets.Count > 0)
            {
                Log("--- Pre-assets ---");
                var pipeline = new AssetPipelineService();
                pipeline.Output += Log;
                var vtf = new AssetPipelineService.VtfToolConfig(
                    SelectedGame?.VtfTool.ResolvedPath, SelectedGame?.VtfToolCommand);
                bool assetsOk;
                try
                {
                    assetsOk = await pipeline.ApplyAsync(folder, entry.Model.Assets,
                        ResolveAgainstProject, vtf, ct);
                }
                finally
                {
                    pipeline.Output -= Log;
                }
                await FlushLogAsync();
                if (!assetsOk)
                {
                    entry.HasError = true;
                    StatusMessage = $"{entry.Name}: one or more assets failed.";
                    Log("=== Stopped: asset processing failed. ===");
                    return false;
                }
            }

            // 2. Pack the folder.
            var service = new PackageService();
            service.Output += Log;
            try
            {
                var request = new PackageRequest(
                    PackerToolPath: PackerToolPath ?? string.Empty,
                    FolderPath: folder,
                    ExtraOptions: entry.Command);
                var result = await service.PackageAsync(request, ct);
                await FlushLogAsync();

                if (!result.Success)
                {
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
                entry.LastPackagePath = result.OutputPackagePath;
                StatusMessage = $"Packaged {entry.Name} -> {Path.GetFileName(result.OutputPackagePath)}.";
                Log($"=== Done. {StatusMessage} ===");
                return true;
            }
            finally
            {
                service.Output -= Log;
            }
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
    }

    // --- Embedded terminal --------------------------------------------------------------------

    private const int MaxConsoleLines = 1000;
    private readonly List<string> _consoleLines = new();
    private string _consoleText = string.Empty;

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

    private void Log(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            AppendConsoleLine(line);
        else
            dispatcher.BeginInvoke(() => AppendConsoleLine(line));
    }

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

/// <summary>An asset-kind dropdown entry (the themed ComboBox renders via ToString).</summary>
public sealed record AssetKindChoice(AssetKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An image-format dropdown entry (the themed ComboBox renders via ToString).</summary>
public sealed record ImageFormatChoice(ImageTargetFormat Format, string Label)
{
    public override string ToString() => Label;
}
