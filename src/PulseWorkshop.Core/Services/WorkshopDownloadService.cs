using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PulseWorkshop.Core.Services;

/// <summary>What to fetch and where to put it (see <see cref="WorkshopDownloadService.DownloadAsync"/>).</summary>
public sealed record WorkshopDownloadRequest(string UrlOrId, string DestinationFolder);

/// <summary>The public details Steam returns for a Workshop item (the subset we use).</summary>
public sealed record WorkshopItemDetails(
    ulong PublishedFileId,
    string Title,
    string? FileUrl,
    string? FileName,
    ulong FileSize,
    uint ConsumerAppId,
    string? PreviewUrl);

/// <summary>A Workshop collection: its id, display name, and the ids of the items it contains.</summary>
public sealed record WorkshopCollection(ulong Id, string Name, IReadOnlyList<ulong> ChildIds);

/// <summary>Outcome of a download. <paramref name="OutputPath"/> is the file (or folder) written -
/// or the already-present one - on success. <paramref name="AlreadyExisted"/> is true when the item
/// was found in the destination and no download was needed. <paramref name="NeedsSteamClient"/> is set
/// on failure when the item has no direct URL (UGC/SteamPipe only) and can only be fetched by the
/// owning Steam client; <paramref name="ConsumerAppId"/> then names the game to connect to.</summary>
public sealed record WorkshopDownloadResult(
    bool Success, string? OutputPath, string? Error, ulong PublishedFileId, string? Title,
    bool AlreadyExisted = false, bool NeedsSteamClient = false, uint ConsumerAppId = 0);

/// <summary>
/// Downloads a Steam Workshop item from a pasted link or id, Crowbar-style: it asks Steam's public
/// <c>ISteamRemoteStorage/GetPublishedFileDetails</c> web endpoint (no API key, no login, no game
/// ownership) for the item's direct <c>file_url</c> on the Steam CDN, then streams that file to a
/// destination folder. Because it never touches the Steam client, it works for any Source game's
/// Workshop item regardless of whether the account owns the game.
///
/// The catch (inherent to this path): items delivered only through the modern UGC/SteamPipe system
/// (chiefly Garry's Mod) expose an empty <c>file_url</c> and cannot be fetched this way - those need
/// the owning Steam client. Such items are reported with a clear message rather than silently failing.
///
/// Pure <c>net10.0</c> - HttpClient / System.IO / System.Text.Json only, no Windows-specific APIs, so
/// it moves to a cross-platform (Avalonia) front-end unchanged.
/// </summary>
public sealed class WorkshopDownloadService
{
    private const string DetailsEndpoint =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private const string CollectionEndpoint =
        "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

    // One shared client; per-download deadlines come from the CancellationToken, not this timeout.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWorkshop/1.0");
        return http;
    }

    /// <summary>Raised for each progress/status line so the App can stream it into the shared console.
    /// Fires on the calling (background) thread - marshal to the UI thread before touching UI.</summary>
    public event Action<string>? Output;

    private void Log(string line) => Output?.Invoke(line);

    /// <summary>
    /// Extracts the numeric published-file id from a raw id or any Workshop URL form
    /// (<c>.../sharedfiles/filedetails/?id=123</c>, <c>.../workshop/filedetails/?id=123</c>, or a bare
    /// <c>123</c>). Returns null when no id can be found.
    /// </summary>
    public static ulong? ParseId(string? urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
            return null;

        var text = urlOrId.Trim();

        // A bare id (the whole string is digits).
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
            return bare;

        // An "id=" query parameter anywhere in a URL.
        var m = Regex.Match(text, @"[?&]id=(\d+)");
        if (m.Success && ulong.TryParse(m.Groups[1].Value, out var fromQuery))
            return fromQuery;

        // Fallback: the last run of digits in the string (covers odd/shortened link forms).
        var digits = Regex.Match(text, @"(\d{6,})");
        if (digits.Success && ulong.TryParse(digits.Groups[1].Value, out var loose))
            return loose;

        return null;
    }

    /// <summary>
    /// Fetches the item's public details from Steam's web API. Returns null when the id is unknown or
    /// the endpoint reports no result.
    /// </summary>
    public async Task<WorkshopItemDetails?> GetDetailsAsync(ulong publishedFileId, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = publishedFileId.ToString(CultureInfo.InvariantCulture),
        });

        using var response = await Http.PostAsync(DetailsEndpoint, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("response", out var resp) ||
            !resp.TryGetProperty("publishedfiledetails", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;

        var d = list[0];

        // result == 1 (k_EResultOK) means the item exists; anything else (9 = not found, etc.) is a miss.
        if (GetInt(d, "result") != 1)
            return null;

        return new WorkshopItemDetails(
            PublishedFileId: GetUInt64(d, "publishedfileid", publishedFileId),
            Title: GetString(d, "title") ?? string.Empty,
            FileUrl: GetString(d, "file_url"),
            FileName: GetString(d, "filename"),
            FileSize: GetUInt64(d, "file_size", 0),
            ConsumerAppId: (uint)GetUInt64(d, "consumer_app_id", 0),
            PreviewUrl: GetString(d, "preview_url"));
    }

    /// <summary>
    /// If <paramref name="id"/> is a Workshop collection, returns its name and the ids of the items it
    /// contains; otherwise returns null. Uses the public <c>GetCollectionDetails</c> endpoint (no key,
    /// no ownership), then the standard details endpoint for the collection's display name.
    /// </summary>
    public async Task<WorkshopCollection?> GetCollectionAsync(ulong id, CancellationToken ct = default)
    {
        var children = await GetCollectionChildrenAsync(id, ct).ConfigureAwait(false);
        if (children.Count == 0)
            return null; // not a collection (or an empty one)

        // GetCollectionDetails doesn't carry the collection's title, so fetch it from the item details.
        string name;
        try { name = (await GetDetailsAsync(id, ct).ConfigureAwait(false))?.Title ?? string.Empty; }
        catch { name = string.Empty; }
        if (string.IsNullOrWhiteSpace(name))
            name = $"collection_{id}";

        return new WorkshopCollection(id, name, children);
    }

    /// <summary>Returns the child item ids of a collection (empty when the id is not a collection).</summary>
    private async Task<IReadOnlyList<ulong>> GetCollectionChildrenAsync(ulong id, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["collectioncount"] = "1",
            ["publishedfileids[0]"] = id.ToString(CultureInfo.InvariantCulture),
        });

        using var response = await Http.PostAsync(CollectionEndpoint, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var ids = new List<ulong>();
        if (!doc.RootElement.TryGetProperty("response", out var resp) ||
            !resp.TryGetProperty("collectiondetails", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return ids;

        var c = list[0];
        if (GetInt(c, "result") != 1 ||
            !c.TryGetProperty("children", out var kids) || kids.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var kid in kids.EnumerateArray())
        {
            var childId = GetUInt64(kid, "publishedfileid", 0);
            if (childId != 0)
                ids.Add(childId);
        }
        return ids;
    }

    /// <summary>
    /// Resolves the item and streams its content file into <see cref="WorkshopDownloadRequest.DestinationFolder"/>.
    /// Progress (0..100) is reported through <paramref name="progress"/> and mirrored to <see cref="Output"/>.
    /// </summary>
    public async Task<WorkshopDownloadResult> DownloadAsync(
        WorkshopDownloadRequest req, IProgress<double>? progress = null,
        bool overwrite = false, CancellationToken ct = default)
    {
        var id = ParseId(req.UrlOrId);
        if (id is null)
            return Fail(0, null, "Could not find a Workshop item id in that link. Paste a Workshop URL or the numeric id.");

        if (string.IsNullOrWhiteSpace(req.DestinationFolder))
            return Fail(id.Value, null, "Choose a destination folder first.");

        string destFolder;
        try
        {
            destFolder = Path.GetFullPath(req.DestinationFolder);
        }
        catch (Exception ex)
        {
            return Fail(id.Value, null, $"Destination folder is not usable: {ex.Message}");
        }

        // Skip the network round-trip entirely when this item is already in the folder, unless the
        // caller asked to overwrite. Matches on the "(id)" name tag our downloads carry.
        if (!overwrite && FindExistingDownload(destFolder, id.Value) is { } already)
        {
            Log($"=== Already downloaded: {already} ===");
            return new WorkshopDownloadResult(true, already, null, id.Value,
                Path.GetFileNameWithoutExtension(already), AlreadyExisted: true);
        }

        Log($"=== Download: resolving item {id.Value}... ===");

        WorkshopItemDetails? details;
        try
        {
            details = await GetDetailsAsync(id.Value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(id.Value, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(id.Value, null, $"Could not reach Steam to resolve the item: {ex.Message}");
        }

        if (details is null)
            return Fail(id.Value, null, $"Item {id.Value} was not found (it may be private, removed, or the id is wrong).");

        Log($"Found \"{details.Title}\" (app {details.ConsumerAppId}, {FormatSize(details.FileSize)}).");

        if (string.IsNullOrWhiteSpace(details.FileUrl))
        {
            // No direct URL: this is a UGC/SteamPipe-only item (typical for Garry's Mod). Signal the
            // caller so it can fall back to the owning Steam client (see WorkshopService.DownloadViaClientAsync).
            Log("No direct download URL - this item is delivered through Steam's UGC/SteamPipe system.");
            return new WorkshopDownloadResult(false, null,
                "This item has no direct download URL - it needs the Steam client of an account that owns the game.",
                id.Value, details.Title, NeedsSteamClient: true, ConsumerAppId: details.ConsumerAppId);
        }

        string outputPath;
        try
        {
            Directory.CreateDirectory(destFolder);
            outputPath = Path.Combine(destFolder, BuildFileName(details));
        }
        catch (Exception ex)
        {
            return Fail(id.Value, details.Title, $"Destination folder is not usable: {ex.Message}");
        }

        // Stream to a .part file, then move into place - a cancelled/failed download never leaves a
        // half file masquerading as complete.
        var partPath = outputPath + ".part";
        try
        {
            Log($"Downloading to {outputPath} ...");
            using (var response = await Http.GetAsync(details.FileUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ??
                            (details.FileSize > 0 ? (long)details.FileSize : -1);

                await using var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, 1 << 16, useAsync: true);

                var buffer = new byte[1 << 16];
                long received = 0;
                int lastPercent = -1;
                int read;
                while ((read = await http.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        var percent = (int)(received * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(percent);
                            if (percent % 10 == 0)
                                Log($"  {FormatSize((ulong)received)} / {FormatSize((ulong)total)} ({percent}%)");
                        }
                    }
                }
            }

            // Replace any existing file with the freshly downloaded one.
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(partPath, outputPath);

            progress?.Report(100);
            Log($"=== Download complete: {outputPath} ===");
            return new WorkshopDownloadResult(true, outputPath, null, id.Value, details.Title);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            return Fail(id.Value, details.Title, "Cancelled.");
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            return Fail(id.Value, details.Title, $"Download failed: {ex.Message}");
        }
    }

    private WorkshopDownloadResult Fail(ulong id, string? title, string error)
    {
        Log($"=== Download failed: {error} ===");
        return new WorkshopDownloadResult(false, null, error, id, title);
    }

    /// <summary>
    /// Returns the path of an already-downloaded copy of <paramref name="id"/> in
    /// <paramref name="destinationFolder"/>, or null. Recognises the <c>(id)</c> name tag every
    /// download carries (and a bare <c>id</c> name for titleless items); ignores partial
    /// (<c>.part</c>) files. Offline - no network needed.
    /// </summary>
    public static string? FindExistingDownload(string destinationFolder, ulong id)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
            return null;

        var idTag = $"({id})";
        var idStr = id.ToString(CultureInfo.InvariantCulture);
        // Both files (web-API downloads, single-file client copies) and folders (multi-file client
        // copies) carry the "(id)" name tag; check both kinds.
        foreach (var path in Directory.EnumerateFileSystemEntries(destinationFolder))
        {
            if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Directory.Exists(path)
                ? Path.GetFileName(path)
                : Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(idTag, StringComparison.Ordinal) || name == idStr)
                return path;
        }
        return null;
    }

    /// <summary>
    /// Copies a Steam-client-downloaded item out of its local cache folder (as returned by
    /// <c>GetItemInstallInfo</c>) into <paramref name="destinationFolder"/>, using the same
    /// <c>&lt;title&gt; (&lt;id&gt;)</c> naming the web-API path uses. A single-file item (e.g. a
    /// <c>.gma</c>) lands as one named file; a folder of loose files lands in a named subfolder that
    /// mirrors the cache tree. Used by the UGC fallback (see <c>WorkshopService.DownloadViaClientAsync</c>).
    /// </summary>
    public static WorkshopDownloadResult CopyFromInstallFolder(
        string? installFolder, string destinationFolder, ulong id, string? title)
    {
        if (string.IsNullOrWhiteSpace(installFolder) || !Directory.Exists(installFolder))
            return new WorkshopDownloadResult(false, null,
                $"Steam's cache folder for the item is missing: {installFolder}", id, title);

        try
        {
            Directory.CreateDirectory(destinationFolder);
            var files = Directory.GetFiles(installFolder, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
                return new WorkshopDownloadResult(false, null, "Steam's cache folder for the item is empty.", id, title);

            if (files.Length == 1)
            {
                var src = files[0];
                var outputPath = Path.Combine(destinationFolder,
                    BuildDownloadFileName(title, id, Path.GetExtension(src)));
                File.Copy(src, outputPath, overwrite: true);
                return new WorkshopDownloadResult(true, outputPath, null, id, title);
            }

            // Multiple loose files -> a "<title> (<id>)" subfolder mirroring the cache tree.
            var outDir = Path.Combine(destinationFolder, BuildDownloadFolderName(title, id));
            foreach (var src in files)
            {
                var rel = Path.GetRelativePath(installFolder, src);
                var target = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(src, target, overwrite: true);
            }
            return new WorkshopDownloadResult(true, outDir, null, id, title);
        }
        catch (Exception ex)
        {
            return new WorkshopDownloadResult(false, null, $"Copying from Steam's cache failed: {ex.Message}", id, title);
        }
    }

    /// <summary>Builds a safe output file name for a web-API download: the extension comes from the
    /// item's filename (or URL), the stem from <see cref="BuildDownloadFolderName"/>.</summary>
    private static string BuildFileName(WorkshopItemDetails d)
    {
        var ext = ExtensionOf(d.FileName) ?? ExtensionOf(d.FileUrl) ?? ".bin";
        return BuildDownloadFileName(d.Title, d.PublishedFileId, ext);
    }

    /// <summary>Builds a safe, descriptive output file name: <c>&lt;title&gt; (&lt;id&gt;)&lt;ext&gt;</c>,
    /// falling back to just the id when the title is empty.</summary>
    public static string BuildDownloadFileName(string? title, ulong id, string? extension) =>
        BuildDownloadFolderName(title, id) + (NormalizeExtension(extension) ?? ".bin");

    /// <summary>Builds the stem used for downloads: <c>&lt;title&gt; (&lt;id&gt;)</c>, or just the id
    /// when the title is empty. Also the folder name for multi-file items.</summary>
    public static string BuildDownloadFolderName(string? title, ulong id)
    {
        var t = Sanitize(title ?? string.Empty);
        return t.Length > 0 ? $"{t} ({id})" : id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Normalizes an extension/path to a leading-dot extension, or null when there is none.</summary>
    private static string? NormalizeExtension(string? extensionOrPath)
    {
        if (string.IsNullOrWhiteSpace(extensionOrPath))
            return null;
        var ext = extensionOrPath.StartsWith('.') && !extensionOrPath.Contains('/') && !extensionOrPath.Contains('\\')
            ? extensionOrPath
            : ExtensionOf(extensionOrPath);
        return string.IsNullOrEmpty(ext) ? null : ext;
    }

    /// <summary>Returns the extension (with dot) of a path/URL, or null when there is none.</summary>
    private static string? ExtensionOf(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return null;
        // Drop any query string before looking at the extension.
        var q = pathOrUrl.IndexOf('?');
        var clean = q >= 0 ? pathOrUrl[..q] : pathOrUrl;
        var ext = Path.GetExtension(clean);
        return string.IsNullOrEmpty(ext) || ext.Length > 8 ? null : ext;
    }

    /// <summary>Makes a string safe to use as a file or folder name (e.g. a collection subfolder).</summary>
    public static string SanitizeName(string name) => Sanitize(name);

    private static string Sanitize(string name)
    {
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        // Windows also treats trailing dots/spaces specially; trim them for portability.
        return name.TrimEnd('.', ' ');
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    internal static string FormatSize(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    // --- JSON helpers: the endpoint returns numbers sometimes as JSON numbers, sometimes as strings --

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt32(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => 0,
        };
    }

    private static ulong GetUInt64(JsonElement e, string name, ulong fallback)
    {
        if (!e.TryGetProperty(name, out var v))
            return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetUInt64(out var n) => n,
            JsonValueKind.String when ulong.TryParse(v.GetString(), out var n) => n,
            _ => fallback,
        };
    }
}
