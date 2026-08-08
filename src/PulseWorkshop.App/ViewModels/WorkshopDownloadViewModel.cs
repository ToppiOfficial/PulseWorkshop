using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
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
        ClearListCommand = new RelayCommand(ClearList, () => Downloads.Count > 0);

        // Enable/disable "Clear list" as entries come and go.
        Downloads.CollectionChanged += (_, _) => ClearListCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Raised (on the UI thread) with a downloaded file's path when the user asks to open it
    /// in the Unpack tab. MainViewModel handles it the same way as "View Package".</summary>
    public event Action<string>? OpenInUnpackRequested;

    // --- Commands -------------------------------------------------------------------------------

    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearListCommand { get; }

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
                await DownloadSingleAsync(id.Value, _downloadInput, ct);
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
            _cancel.Dispose();
            _cancel = null;
        }
    }

    /// <summary>Downloads one item: its entry is added to the list up front so its own progress bar,
    /// metadata and Cancel button are live for the whole transfer.</summary>
    private async Task DownloadSingleAsync(ulong id, string urlOrId, CancellationToken ct)
    {
        var entry = NewEntry(id, ct);
        Downloads.Insert(0, entry);

        await ProcessEntryAsync(entry, urlOrId, _downloadFolder);

        StatusMessage = entry.State switch
        {
            DownloadEntryState.Done => $"Downloaded \"{entry.Title}\".",
            DownloadEntryState.Cancelled => "Cancelled.",
            _ => entry.StatusText,
        };
    }

    /// <summary>Downloads every item in a collection into a subfolder named after the collection. Every
    /// child gets its own list entry up front (in collection order), each with its own progress bar and
    /// Cancel button, so the user can drop individual items without stopping the whole run.</summary>
    private async Task DownloadCollectionAsync(WorkshopCollection collection, CancellationToken ct)
    {
        var subfolder = Path.Combine(_downloadFolder, WorkshopDownloadService.SanitizeName(collection.Name));
        var total = collection.ChildIds.Count;
        _console.Append($"=== Download: collection \"{collection.Name}\" - {total} item{(total == 1 ? "" : "s")} -> {subfolder} ===");

        // Materialize an entry per child up front so the whole queue is visible and individually
        // cancellable; keep them grouped in collection order at the top of the list.
        var entries = new List<DownloadedItemViewModel>(total);
        for (var i = 0; i < total; i++)
        {
            var entry = NewEntry(collection.ChildIds[i], ct);
            entries.Add(entry);
            Downloads.Insert(i, entry);
        }

        // Bracket the whole collection as one batch: if any child needs the Steam client, the session
        // is connected once on the first such item and only released (disconnect / restore) when the
        // batch ends here - not once per item. Web-only collections never open a session.
        _ugc?.BeginBatch();
        try
        {
            for (var i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested)
                    break; // top-level "Cancel" stops the run; the rest are marked cancelled below
                var entry = entries[i];
                StatusMessage = $"Collection \"{collection.Name}\": item {i + 1} of {total}...";
                await ProcessEntryAsync(entry, entry.PublishedFileId.ToString(), subfolder);
            }
        }
        finally
        {
            // Finished or cancelled: release the borrowed session exactly once.
            if (_ugc is not null)
                await _ugc.EndBatchAsync();
        }

        // Anything the run never reached (top-level cancel) is reported as cancelled, not left "Queued".
        foreach (var e in entries)
            if (e.State == DownloadEntryState.Queued)
                e.SetCancelled();

        var ok = entries.Count(e => e.State == DownloadEntryState.Done);
        var cancelled = entries.Count(e => e.State == DownloadEntryState.Cancelled);
        var failed = entries.Count(e => e.State == DownloadEntryState.Failed);
        StatusMessage = $"Collection \"{collection.Name}\": {ok} of {total} downloaded"
            + (failed > 0 ? $", {failed} failed" : "")
            + (cancelled > 0 ? $", {cancelled} cancelled" : "")
            + $" to {Path.GetFileName(subfolder)}.";
        _console.Append($"=== {StatusMessage} ===");
    }

    /// <summary>Builds a fresh entry for <paramref name="id"/> with its own cancellation source linked to
    /// the run's token (so the top-level Cancel stops it too, but its own Cancel stops only it).</summary>
    private DownloadedItemViewModel NewEntry(ulong id, CancellationToken parent) =>
        new(id, path => OpenInUnpackRequested?.Invoke(path), RemoveEntry)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent),
        };

    /// <summary>Removes a single finished entry from the list. The downloaded files on disk are left
    /// in place - only the list row is dropped.</summary>
    private void RemoveEntry(DownloadedItemViewModel entry) => Downloads.Remove(entry);

    /// <summary>Clears the list of every entry that is no longer active (done / failed / cancelled),
    /// leaving any in-progress downloads running. The files on disk are left untouched.</summary>
    private void ClearList()
    {
        foreach (var entry in Downloads.Where(e => e.CanRemove).ToList())
            Downloads.Remove(entry);
    }

    /// <summary>Runs one entry end to end: resolve its metadata (id/author/dates), then the download
    /// cascade, driving the entry's own progress bar and final state throughout.</summary>
    private async Task ProcessEntryAsync(DownloadedItemViewModel entry, string urlOrId, string destFolder)
    {
        var cts = entry.Cancellation!;
        var token = cts.Token;
        try
        {
            if (token.IsCancellationRequested) // cancelled while still queued
            {
                entry.SetCancelled();
                return;
            }

            entry.SetDownloading();

            // Resolve details first so the entry shows its id/author/dates/size while it downloads - and
            // even if the download itself later fails. Metadata is best-effort; the download surfaces
            // the real errors.
            WorkshopItemDetails? details = null;
            try { details = await _service.GetDetailsAsync(entry.PublishedFileId, token); }
            catch (OperationCanceledException) { }
            catch { /* best-effort metadata */ }

            if (details is not null)
            {
                entry.ApplyDetails(details);
                _ = ResolveAuthorNameAsync(entry, details.Creator); // fire-and-forget, best-effort
            }

            if (token.IsCancellationRequested)
            {
                entry.SetCancelled();
                return;
            }

            var progress = new Progress<double>(entry.ReportProgress);
            var result = await DownloadOneAsync(urlOrId, destFolder, details, progress, token);

            if (result.Success && result.OutputPath is not null)
                entry.SetDone(result.OutputPath, result.AlreadyExisted);
            else if (token.IsCancellationRequested)
                entry.SetCancelled();
            else
                entry.SetFailed(result.Error);
        }
        finally
        {
            cts.Dispose();
            entry.Cancellation = null;
        }
    }

    /// <summary>Best-effort resolve of the author's display name (keyless Community-profile XML); updates
    /// the entry's owner text in place when it lands, otherwise leaves the SteamID64 fallback showing.</summary>
    private static async Task ResolveAuthorNameAsync(DownloadedItemViewModel entry, ulong creator)
    {
        if (creator == 0)
            return;
        var name = await SteamProfile.GetPersonaNameAsync(creator);
        if (!string.IsNullOrWhiteSpace(name))
            entry.OwnerText = name!;
    }

    /// <summary>The full per-item download cascade: Steam's web API first, then the Steam-client UGC
    /// fallback (if wired) for items with no direct URL. Used for both single items and collection children.</summary>
    private async Task<WorkshopDownloadResult> DownloadOneAsync(
        string urlOrId, string destinationFolder, WorkshopItemDetails? knownDetails,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = await _service.DownloadAsync(
            new WorkshopDownloadRequest(urlOrId, destinationFolder), progress, overwrite: false, ct: ct,
            knownDetails: knownDetails);

        if (!result.Success && result.NeedsSteamClient && _ugc is not null)
        {
            _console.Append("=== Download: no direct URL; fetching via the Steam client (the game must be owned)... ===");
            result = await _ugc.DownloadAsync(result.PublishedFileId, result.ConsumerAppId, result.Title,
                destinationFolder, progress, ct);
        }
        return result;
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
            ShellOpen.Open(DownloadFolder);
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

/// <summary>Where one download entry is in its lifecycle (drives the per-entry UI: progress bar,
/// Cancel button, and the final Reveal / Open-in-Unpack actions).</summary>
public enum DownloadEntryState
{
    /// <summary>Queued in a collection, not started yet (cancellable).</summary>
    Queued,
    /// <summary>Actively transferring (progress bar + Cancel live).</summary>
    Downloading,
    /// <summary>Finished on disk (Reveal / Open-in-Unpack available).</summary>
    Done,
    /// <summary>Failed - the reason is in <see cref="DownloadedItemViewModel.StatusText"/>.</summary>
    Failed,
    /// <summary>Cancelled by the user (this item or the whole run).</summary>
    Cancelled,
}

/// <summary>
/// One item in the Download tab's list. It is created the moment its download starts (not on
/// completion), so the entry shows its own live progress bar, per-item Cancel button, the item's
/// Workshop id / author / upload + update dates, and links back to its Workshop page and the author's
/// profile - then, once finished, Reveal / Open-in-Unpack.
/// </summary>
public sealed class DownloadedItemViewModel : ObservableObject
{
    private readonly Action<string>? _openInUnpack;
    private readonly Action<DownloadedItemViewModel>? _remove;

    public DownloadedItemViewModel(ulong publishedFileId, Action<string>? openInUnpack,
        Action<DownloadedItemViewModel>? remove = null)
    {
        PublishedFileId = publishedFileId;
        _title = $"Item {publishedFileId}";
        _openInUnpack = openInUnpack;
        _remove = remove;

        RevealCommand = new RelayCommand(Reveal, () => IsDone && PathExists(FilePath));
        OpenInUnpackCommand = new RelayCommand(OpenInUnpack, () => IsDone && CanOpenInUnpack);
        OpenWorkshopPageCommand = new RelayCommand(OpenWorkshopPage);
        OpenAuthorProfileCommand = new RelayCommand(OpenAuthorProfile, () => OwnerId != 0);
        CancelCommand = new RelayCommand(CancelEntry, () => CanCancel);
        RemoveCommand = new RelayCommand(() => _remove?.Invoke(this), () => CanRemove);
    }

    /// <summary>The item's Workshop published-file id (stable from creation - the entry starts knowing
    /// only this, then fills in the rest from the resolved details).</summary>
    public ulong PublishedFileId { get; }

    /// <summary>This entry's own cancellation source, linked to the run's token so the top-level Cancel
    /// stops it too. The orchestrator sets it before the entry runs and clears it when done.</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    // --- Display fields -------------------------------------------------------------------------

    private string _title;
    public string Title { get => _title; set => SetField(ref _title, value); }

    /// <summary>"ID 1234567890" - always shown.</summary>
    public string IdText => $"ID {PublishedFileId}";

    /// <summary>The item's Steam-hosted preview image URL (shown as the entry's thumbnail), or null.</summary>
    private string? _previewUrl;
    public string? PreviewUrl { get => _previewUrl; private set => SetField(ref _previewUrl, value); }

    private ulong _ownerId;
    public ulong OwnerId
    {
        get => _ownerId;
        private set
        {
            if (SetField(ref _ownerId, value))
                OpenAuthorProfileCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The author: the resolved persona name when available, otherwise the SteamID64 (settable
    /// so the name can be filled in asynchronously once resolved).</summary>
    private string _ownerText = string.Empty;
    public string OwnerText { get => _ownerText; set => SetField(ref _ownerText, value); }

    /// <summary>First-upload date ("yyyy-MM-dd"), or null when unknown.</summary>
    private string? _uploadedText;
    public string? UploadedText { get => _uploadedText; private set => SetField(ref _uploadedText, value); }

    /// <summary>Last-update date ("yyyy-MM-dd"), or null when unknown.</summary>
    private string? _updatedText;
    public string? UpdatedText { get => _updatedText; private set => SetField(ref _updatedText, value); }

    /// <summary>Human-readable content-file size, or null when unknown.</summary>
    private string? _sizeText;
    public string? SizeText { get => _sizeText; private set => SetField(ref _sizeText, value); }

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        private set
        {
            if (!SetField(ref _filePath, value))
                return;
            // A download can land as a single file or - for a multi-file item (CS2 splits an item into a
            // _dir.vpk plus numbered chunks) - a folder; resolve the openable archive either way.
            _archivePath = ResolveArchivePath(value);
            OnPropertyChanged(nameof(CanOpenInUnpack));
        }
    }

    /// <summary>The packed archive the Unpack tab should open for this download (the file itself, or the
    /// _dir.vpk / .vpk / .gma found inside a folder download), or null when nothing is unpackable.</summary>
    private string? _archivePath;

    // --- Lifecycle / progress -------------------------------------------------------------------

    private DownloadEntryState _state = DownloadEntryState.Queued;
    public DownloadEntryState State
    {
        get => _state;
        private set
        {
            if (!SetField(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(ShowProgress));
            CancelCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
            OpenInUnpackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDone => State == DownloadEntryState.Done;
    public bool IsFailed => State is DownloadEntryState.Failed or DownloadEntryState.Cancelled;

    /// <summary>Cancel button shows while the item is still queued or transferring.</summary>
    public bool CanCancel => State is DownloadEntryState.Queued or DownloadEntryState.Downloading;

    /// <summary>Remove button shows once the item is finished (done / failed / cancelled): it drops the
    /// row from the list only - the downloaded files on disk are left in place.</summary>
    public bool CanRemove => State is DownloadEntryState.Done or DownloadEntryState.Failed
        or DownloadEntryState.Cancelled;

    /// <summary>Progress bar shows while queued (indeterminate) or downloading.</summary>
    public bool ShowProgress => State is DownloadEntryState.Queued or DownloadEntryState.Downloading;

    private double _progress;
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetField(ref _isIndeterminate, value); }

    private string _statusText = "Queued";
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    // --- Commands -------------------------------------------------------------------------------

    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenInUnpackCommand { get; }
    public RelayCommand OpenWorkshopPageCommand { get; }
    public RelayCommand OpenAuthorProfileCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }

    /// <summary>True when this download contains a packed archive the Unpack tab can open (.vpk / .gma) -
    /// the "Open in Unpack" button only shows for those (a preview image, say, has nothing to unpack).</summary>
    public bool CanOpenInUnpack => _archivePath is not null;

    /// <summary>Resolves the packed archive to hand to the Unpack tab: the download itself when it's a
    /// .vpk/.gma, or - for a multi-file folder download - the openable archive inside it, preferring a
    /// VPK dir index (which pulls in its numbered chunks), then a standalone .vpk, then a .gma. Returns
    /// null when nothing openable is present.</summary>
    private static string? ResolveArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (File.Exists(path))
            return PackedArchiveLoader.CanOpen(path) ? path : null;

        if (!Directory.Exists(path))
            return null;

        var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToList();

        var dirVpk = files.FirstOrDefault(f => f.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase));
        if (dirVpk is not null)
            return dirVpk;

        // A standalone single-file .vpk (not one of the numbered x_000.vpk chunks, which aren't openable alone).
        var vpk = files.FirstOrDefault(f =>
            f.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) && !IsNumberedVpkChunk(f));
        if (vpk is not null)
            return vpk;

        return files.FirstOrDefault(f => f.EndsWith(".gma", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for a numbered VPK chunk file (e.g. "foo_000.vpk") - content, not the openable index.</summary>
    private static bool IsNumberedVpkChunk(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var underscore = name.LastIndexOf('_');
        return underscore >= 0 && underscore < name.Length - 1
            && name.AsSpan(underscore + 1).ToString().All(char.IsAsciiDigit);
    }

    private static bool PathExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    // --- Orchestrator hooks (called by WorkshopDownloadViewModel) --------------------------------

    /// <summary>Fills in the metadata row (title, author, dates, size) from the resolved details.</summary>
    public void ApplyDetails(WorkshopItemDetails d)
    {
        if (!string.IsNullOrWhiteSpace(d.Title))
            Title = d.Title;
        PreviewUrl = d.PreviewUrl;
        OwnerId = d.Creator;
        OwnerText = d.Creator != 0 ? d.Creator.ToString() : "Unknown author";
        UploadedText = FormatDate(d.TimeCreated);
        UpdatedText = FormatDate(d.TimeUpdated);
        SizeText = d.FileSize > 0 ? WorkshopDownloadService.FormatSize(d.FileSize) : null;
    }

    public void SetDownloading()
    {
        State = DownloadEntryState.Downloading;
        IsIndeterminate = true; // until the first byte arrives
        StatusText = "Downloading...";
    }

    /// <summary>Drives this entry's own progress bar (0..100).</summary>
    public void ReportProgress(double percent)
    {
        IsIndeterminate = false;
        Progress = percent;
        StatusText = percent >= 100 ? "Finishing..." : $"Downloading  {percent:0}%";
    }

    public void SetDone(string filePath, bool alreadyExisted)
    {
        FilePath = filePath;
        Progress = 100;
        IsIndeterminate = false;
        State = DownloadEntryState.Done;
        StatusText = alreadyExisted ? "Already downloaded" : "Downloaded";
    }

    public void SetFailed(string? error)
    {
        State = DownloadEntryState.Failed;
        StatusText = string.IsNullOrWhiteSpace(error) ? "Failed" : error!;
    }

    public void SetCancelled()
    {
        State = DownloadEntryState.Cancelled;
        StatusText = "Cancelled";
    }

    // --- Actions --------------------------------------------------------------------------------

    private void CancelEntry()
    {
        StatusText = "Cancelling...";
        Cancellation?.Cancel();
    }

    /// <summary>Opens the item's Workshop page, preferring the Steam client and falling back to a browser.</summary>
    private void OpenWorkshopPage() => OpenUrl(
        $"steam://url/CommunityFilePage/{PublishedFileId}",
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={PublishedFileId}");

    /// <summary>Opens the author's Steam Workshop profile - their published-items page, not their general
    /// Community profile (Steam client first, browser fallback).</summary>
    private void OpenAuthorProfile()
    {
        if (OwnerId == 0)
            return;
        var workshopProfile = $"https://steamcommunity.com/profiles/{OwnerId}/myworkshopfiles/";
        OpenUrl($"steam://openurl/{workshopProfile}", workshopProfile);
    }

    private static void OpenUrl(string preferred, string fallback)
    {
        if (!TryStart(preferred))
            TryStart(fallback);
    }

    private static bool TryStart(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FormatDate(long unixSeconds) =>
        unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd")
            : null;

    private void OpenInUnpack()
    {
        if (_archivePath is not null)
            _openInUnpack?.Invoke(_archivePath);
    }

    private void Reveal()
    {
        try
        {
            // A folder download opens in place; a single file is selected within its parent folder.
            if (Directory.Exists(FilePath) || File.Exists(FilePath))
                ShellOpen.Reveal(FilePath);
        }
        catch
        {
            // Best effort - the console still shows where the file landed.
        }
    }
}
