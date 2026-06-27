using PulseWorkshop.Core.Ipc;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.Core.Services;

/// <summary>
/// High-level API the UI talks to. Orchestrates the per-game Steam host and exposes typed
/// operations (ping, list published items, publish, poll progress) over the JSON pipe.
/// </summary>
public sealed class WorkshopService : IAsyncDisposable
{
    private readonly SteamHostManager _hosts;
    private int _requestCounter;

    public WorkshopService(string hostExePath) => _hosts = new SteamHostManager(hostExePath);

    public uint? ActiveAppId => _hosts.ActiveAppId;

    /// <summary>
    /// Raised for each line the Steam host emits (host log + bridge upload progress), so the App can
    /// show a live console. Fires on a background thread - marshal to the UI thread before binding.
    /// </summary>
    public event Action<string>? HostOutput
    {
        add => _hosts.HostOutput += value;
        remove => _hosts.HostOutput -= value;
    }

    /// <summary>Selects the active game, (re)launching the Steam host as needed.</summary>
    public Task SelectGameAsync(uint appId, CancellationToken ct = default) =>
        _hosts.SwitchToAsync(appId, ct);

    /// <summary>Stops the active Steam host (if any), ending the session for the current game.</summary>
    public Task DisconnectAsync() => _hosts.StopAsync();

    /// <summary>Confirms Steam is running and the session is hooked for the active game.</summary>
    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(RequestKind.Ping, (string?)null, ct).ConfigureAwait(false);
        return PipeJson.Deserialize<PingResult>(response)!;
    }

    /// <summary>Lists the logged-in user's published items for the active game (one page).</summary>
    public async Task<QueryPublishedResult> GetPublishedAsync(int page = 1, CancellationToken ct = default)
    {
        var payload = PipeJson.Serialize(new QueryPublishedRequest { Page = page });
        var response = await SendAsync(RequestKind.QueryPublished, payload, ct).ConfigureAwait(false);
        return PipeJson.Deserialize<QueryPublishedResult>(response)!;
    }

    /// <summary>
    /// Fetches every published item by paging through Steam sequentially (50 per page) until the
    /// full set is gathered. Sequential and one-shot to stay gentle on Steam. <paramref name="onProgress"/>
    /// reports (loaded, total) after each page so the UI can show progress.
    /// </summary>
    public async Task<IReadOnlyList<WorkshopItem>> GetAllPublishedAsync(
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var all = new List<WorkshopItem>();
        var page = 1;
        int total;

        do
        {
            ct.ThrowIfCancellationRequested();
            var result = await GetPublishedAsync(page, ct).ConfigureAwait(false);
            total = result.TotalResults;

            if (result.Items.Count == 0)
                break;

            all.AddRange(result.Items);
            onProgress?.Invoke(all.Count, total);
            page++;
        }
        while (all.Count < total);

        return all;
    }

    /// <summary>Creates or updates an item and starts the upload. Poll <see cref="GetProgressAsync"/>.</summary>
    public async Task<PublishResult> PublishAsync(ItemEdit edit, CancellationToken ct = default)
    {
        var payload = PipeJson.Serialize(edit);
        var response = await SendAsync(RequestKind.Publish, payload, ct).ConfigureAwait(false);
        return PipeJson.Deserialize<PublishResult>(response)!;
    }

    /// <summary>Permanently deletes the user's published item from the Workshop. Irreversible.</summary>
    public async Task<DeleteResult> DeleteAsync(ulong publishedFileId, CancellationToken ct = default)
    {
        var payload = PipeJson.Serialize(new DeleteRequest { PublishedFileId = publishedFileId });
        var response = await SendAsync(RequestKind.Delete, payload, ct).ConfigureAwait(false);
        return PipeJson.Deserialize<DeleteResult>(response)!;
    }

    public async Task<ProgressResult> GetProgressAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(RequestKind.GetProgress, (string?)null, ct).ConfigureAwait(false);
        return PipeJson.Deserialize<ProgressResult>(response)!;
    }

    private async Task<string> SendAsync(RequestKind kind, string? payload, CancellationToken ct)
    {
        var request = new PipeRequest
        {
            Kind = kind,
            RequestId = $"r{Interlocked.Increment(ref _requestCounter)}",
            PayloadJson = payload,
        };

        var response = await _hosts.Client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.Ok)
            throw new WorkshopHostException(response.Error ?? "Unknown SteamHost error.");

        return response.PayloadJson ?? "null";
    }

    public ValueTask DisposeAsync() => _hosts.DisposeAsync();
}

/// <summary>Raised when the Steam host reports an error for a request.</summary>
public sealed class WorkshopHostException : Exception
{
    public WorkshopHostException(string message) : base(message) { }
}
