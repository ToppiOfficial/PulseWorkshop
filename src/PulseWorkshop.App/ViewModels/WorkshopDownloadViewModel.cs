using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Services;
using PulseWorkshop.Core.Storage;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// The Workshop -> Download tab: paste a Steam Workshop link (or a bare id) and pull the item's
/// content file into a chosen folder, Crowbar-style. Downloads go through
/// <see cref="WorkshopDownloadService"/> (Steam's public web API + CDN), so no Steam session, login,
/// or game ownership is needed - any Source game's Workshop item can be fetched. Progress streams into
/// the shared console.
/// </summary>
public sealed class WorkshopDownloadViewModel : ObservableObject
{
    private readonly ConsoleViewModel _console;
    private readonly UiSettings _settings;
    private readonly WorkshopDownloadService _service = new();
    private readonly IUgcClientDownloader? _ugc;

    private string _downloadInput = string.Empty;
    private string _downloadFolder;
    private bool _isDownloading;
    private double _progress;
    private string _statusMessage = "Paste a Workshop link or id to download.";
    private CancellationTokenSource? _cancel;

    /// <param name="ugc">Optional Steam-client downloader for UGC-only items (no direct URL); when null
    /// those items just report that they need the owning Steam client.</param>
    public WorkshopDownloadViewModel(ConsoleViewModel console, UiSettings settings,
        IUgcClientDownloader? ugc = null)
    {
        _console = console;
        _settings = settings;
        _ugc = ugc;
        _downloadFolder = string.IsNullOrWhiteSpace(settings.WorkshopDownloadFolder)
            ? AppPaths.DownloadsDir
            : settings.WorkshopDownloadFolder;

        // Stream the service's progress/status lines into the shared console. Append is thread-safe
        // (it marshals to the UI thread itself), so this is safe from the background download task.
        _service.Output += line => _console.Append(line);

        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanDownload);
        CancelCommand = new RelayCommand(Cancel, () => IsDownloading);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => !IsDownloading);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(DownloadFolder));
    }

    /// <summary>Raised (on the UI thread) with a downloaded file's path when the user asks to open it
    /// in the Unpack tab. MainViewModel handles it the same way as "View Package".</summary>
    public event Action<string>? OpenInUnpackRequested;

    // --- Commands -------------------------------------------------------------------------------

    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    // --- Bound state ----------------------------------------------------------------------------

    /// <summary>The pasted Workshop link or numeric id.</summary>
    public string DownloadInput
    {
        get => _downloadInput;
        set
        {
            if (SetField(ref _downloadInput, value ?? string.Empty))
                DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Where downloaded items are written. Persisted so it is remembered across sessions.</summary>
    public string DownloadFolder
    {
        get => _downloadFolder;
        set
        {
            if (!SetField(ref _downloadFolder, value ?? string.Empty))
                return;
            _settings.WorkshopDownloadFolder = _downloadFolder;
            _settings.Save();
            DownloadCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetField(ref _isDownloading, value))
                return;
            DownloadCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            BrowseFolderCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>0..100 for the determinate progress bar (indeterminate until the first byte).</summary>
    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>The items downloaded this session (newest first), each revealable in the file browser.</summary>
    public ObservableCollection<DownloadedItemViewModel> Downloads { get; } = new();

    private bool CanDownload =>
        !IsDownloading
        && !string.IsNullOrWhiteSpace(_downloadInput)
        && !string.IsNullOrWhiteSpace(_downloadFolder);

    // --- Actions --------------------------------------------------------------------------------

    private async Task DownloadAsync()
    {
        var id = WorkshopDownloadService.ParseId(_downloadInput);
        if (id is null)
        {
            StatusMessage = "That does not look like a Workshop link or id.";
            return;
        }

        _cancel = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        StatusMessage = "Resolving...";

        try
        {
            var ct = _cancel.Token;

            // A collection downloads all of its items into a subfolder named after the collection; a
            // plain item downloads on its own into the destination folder.
            var collection = await _service.GetCollectionAsync(id.Value, ct);
            if (collection is not null)
                await DownloadCollectionAsync(collection, ct);
            else
                await DownloadSingleAsync(_downloadInput, ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            // The Steam-client fallback talks over the host pipe and can throw; keep it off the
            // unhandled async-void path and surface it as a status instead.
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            Progress = 0;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    /// <summary>Downloads one item into the destination folder and reports it as the primary status.</summary>
    private async Task DownloadSingleAsync(string urlOrId, CancellationToken ct)
    {
        var progress = new Progress<double>(p =>
        {
            Progress = p;
            StatusMessage = p >= 100 ? "Finishing..." : $"Downloading... {p:0}%";
        });

        var result = await DownloadOneAsync(urlOrId, _downloadFolder, progress, ct);
        if (result.Success && result.OutputPath is not null)
        {
            var title = TitleOf(result);
            StatusMessage = result.AlreadyExisted
                ? $"Already downloaded: {Path.GetFileName(result.OutputPath)}."
                : $"Downloaded \"{title}\" to {Path.GetFileName(result.OutputPath)}.";
            AddToHistory(title, result.OutputPath);
        }
        else
        {
            StatusMessage = result.Error ?? "Download failed.";
        }
    }

    /// <summary>Downloads every item in a collection into a subfolder named after the collection. The
    /// overall progress bar tracks item count; per-item byte progress goes to the console.</summary>
    private async Task DownloadCollectionAsync(WorkshopCollection collection, CancellationToken ct)
    {
        var subfolder = Path.Combine(_downloadFolder, WorkshopDownloadService.SanitizeName(collection.Name));
        var total = collection.ChildIds.Count;
        _console.Append($"=== Download: collection \"{collection.Name}\" - {total} item{(total == 1 ? "" : "s")} -> {subfolder} ===");

        // Bracket the whole collection as one batch: if any child needs the Steam client, the session
        // is connected once on the first such item and only released (disconnect / restore) when the
        // batch ends here - not once per item. Web-only collections never open a session.
        _ugc?.BeginBatch();
        int ok = 0, failed = 0;
        try
        {
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var childId = collection.ChildIds[i];
                Progress = 100.0 * i / total;
                StatusMessage = $"Collection \"{collection.Name}\": item {i + 1} of {total}...";

                var result = await DownloadOneAsync(childId.ToString(), subfolder, null, ct);
                if (result.Success && result.OutputPath is not null)
                {
                    ok++;
                    AddToHistory(TitleOf(result), result.OutputPath);
                }
                else
                {
                    failed++;
                    _console.Append($"  item {childId} failed: {result.Error}");
                }
            }
        }
        finally
        {
            // Finished or cancelled: release the borrowed session exactly once.
            if (_ugc is not null)
                await _ugc.EndBatchAsync();
        }

        Progress = 100;
        StatusMessage = $"Collection \"{collection.Name}\": {ok} of {total} downloaded"
            + (failed > 0 ? $", {failed} failed" : "") + $" to {Path.GetFileName(subfolder)}.";
        _console.Append($"=== {StatusMessage} ===");
    }

    /// <summary>The full per-item download cascade: Steam's web API first, then the Steam-client UGC
    /// fallback (if wired) for items with no direct URL. Used for both single items and collection children.</summary>
    private async Task<WorkshopDownloadResult> DownloadOneAsync(
        string urlOrId, string destinationFolder, IProgress<double>? progress, CancellationToken ct)
    {
        var result = await _service.DownloadAsync(
            new WorkshopDownloadRequest(urlOrId, destinationFolder), progress, overwrite: false, ct: ct);

        if (!result.Success && result.NeedsSteamClient && _ugc is not null)
        {
            _console.Append("=== Download: no direct URL; fetching via the Steam client (the game must be owned)... ===");
            result = await _ugc.DownloadAsync(result.PublishedFileId, result.ConsumerAppId, result.Title,
                destinationFolder, progress, ct);
        }
        return result;
    }

    private static string TitleOf(WorkshopDownloadResult result) =>
        string.IsNullOrWhiteSpace(result.Title) ? Path.GetFileName(result.OutputPath!) : result.Title;

    /// <summary>Adds (or moves to the top) a session-history entry for a downloaded file, wiring its
    /// "Open in Unpack" action back through <see cref="OpenInUnpackRequested"/>.</summary>
    private void AddToHistory(string title, string filePath)
    {
        var existing = Downloads.FirstOrDefault(
            d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Downloads.Move(Downloads.IndexOf(existing), 0);
            return;
        }
        Downloads.Insert(0, new DownloadedItemViewModel(title, filePath,
            path => OpenInUnpackRequested?.Invoke(path)));
    }

    private void Cancel()
    {
        _cancel?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where downloaded items are saved" };
        try
        {
            if (Directory.Exists(DownloadFolder))
                dlg.InitialDirectory = DownloadFolder;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
            DownloadFolder = Path.GetFullPath(dlg.FolderName);
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            Process.Start(new ProcessStartInfo(DownloadFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the folder: {ex.Message}";
        }
    }
}

/// <summary>
/// The Steam-client UGC fallback the App supplies to <see cref="WorkshopDownloadViewModel"/> for items
/// with no direct web URL. It fetches through the owning game's Steam session (auto-connecting as
/// needed) and copies the content into the destination. <see cref="BeginBatch"/>/<see cref="EndBatchAsync"/>
/// let a collection download hold one borrowed session across all of its items instead of connecting
/// and disconnecting per item - the session is released only when the batch ends.
/// </summary>
public interface IUgcClientDownloader
{
    /// <summary>Downloads one UGC item into <paramref name="destinationFolder"/> via the Steam client.</summary>
    Task<WorkshopDownloadResult> DownloadAsync(ulong publishedFileId, uint consumerAppId, string? title,
        string destinationFolder, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Marks the start of a multi-item batch (a collection): the session, once borrowed for a
    /// UGC child, stays connected until <see cref="EndBatchAsync"/> instead of restoring per item.</summary>
    void BeginBatch();

    /// <summary>Ends the batch, releasing any session borrowed during it (reconnect to the previous
    /// game, or disconnect if we started disconnected). A no-op when no session was borrowed.</summary>
    Task EndBatchAsync();
}

/// <summary>One downloaded item in the session history: its title and the file written on disk.</summary>
public sealed class DownloadedItemViewModel : ObservableObject
{
    private readonly Action<string>? _openInUnpack;

    public DownloadedItemViewModel(string title, string filePath, Action<string>? openInUnpack)
    {
        Title = title;
        FilePath = filePath;
        _openInUnpack = openInUnpack;
        RevealCommand = new RelayCommand(Reveal, () => File.Exists(FilePath));
        OpenInUnpackCommand = new RelayCommand(OpenInUnpack, () => CanOpenInUnpack && File.Exists(FilePath));
    }

    public string Title { get; }
    public string FilePath { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenInUnpackCommand { get; }

    /// <summary>True when the file is a packed archive the Unpack tab can open (.vpk / .gma) - the
    /// "Open in Unpack" button only shows for those (a preview image, say, has nothing to unpack).</summary>
    public bool CanOpenInUnpack => PackedArchiveLoader.CanOpen(FilePath);

    private void OpenInUnpack()
    {
        if (File.Exists(FilePath))
            _openInUnpack?.Invoke(FilePath);
    }

    private void Reveal()
    {
        if (!File.Exists(FilePath))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{FilePath}\"")
                { UseShellExecute = true });
        }
        catch
        {
            // Best effort - the console still shows where the file landed.
        }
    }
}
