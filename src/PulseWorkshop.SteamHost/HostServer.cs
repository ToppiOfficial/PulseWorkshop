using System.IO.Pipes;
using System.Text;
using PulseWorkshop.Core.Ipc;
using PulseWorkshop.Core.Models;
using PulseWorkshop.SteamBridge;

namespace PulseWorkshop.SteamHost;

/// <summary>
/// Named-pipe JSON server. Handles one client (the App) at a time over a persistent connection,
/// translating <see cref="PipeRequest"/>s into <see cref="SteamWorkshop"/> bridge calls.
/// </summary>
internal sealed class HostServer
{
    private readonly SteamWorkshop _workshop;
    private readonly uint _appId;

    public HostServer(SteamWorkshop workshop, uint appId)
    {
        _workshop = workshop;
        _appId = appId;
    }

    public void Run()
    {
        using var pipe = new NamedPipeServerStream(
            PipeProtocol.PipeNameFor(_appId),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        pipe.WaitForConnection();

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var request = PipeJson.Deserialize<PipeRequest>(line);
            if (request is null)
                continue;

            if (request.Kind == RequestKind.Shutdown)
                break;

            var response = Handle(request);
            writer.WriteLine(PipeJson.Serialize(response));
        }
    }

    private PipeResponse Handle(PipeRequest request)
    {
        try
        {
            string? payload = request.Kind switch
            {
                RequestKind.Ping => PipeJson.Serialize(HandlePing()),
                RequestKind.QueryPublished => PipeJson.Serialize(HandleQuery(request.PayloadJson)),
                RequestKind.Publish => PipeJson.Serialize(HandlePublish(request.PayloadJson)),
                RequestKind.GetProgress => PipeJson.Serialize(HandleProgress()),
                _ => null,
            };

            return new PipeResponse { RequestId = request.RequestId, Ok = true, PayloadJson = payload };
        }
        catch (Exception ex)
        {
            return new PipeResponse { RequestId = request.RequestId, Ok = false, Error = ex.Message };
        }
    }

    private PingResult HandlePing()
    {
        var p = _workshop.Ping();
        return new PingResult
        {
            SteamRunning = p.SteamRunning,
            SteamId = p.SteamId,
            PersonaName = p.PersonaName,
            AppId = p.AppId,
        };
    }

    private QueryPublishedResult HandleQuery(string? payloadJson)
    {
        var req = payloadJson is null
            ? new QueryPublishedRequest()
            : PipeJson.Deserialize<QueryPublishedRequest>(payloadJson)!;

        var bridgeResult = _workshop.QueryUserPublished(req.Page);

        var items = new List<WorkshopItem>(bridgeResult.Items.Count);
        foreach (var b in bridgeResult.Items)
        {
            items.Add(new WorkshopItem
            {
                PublishedFileId = b.PublishedFileId,
                Title = b.Title ?? string.Empty,
                Description = b.Description ?? string.Empty,
                Tags = b.Tags is null ? Array.Empty<string>() : b.Tags.ToArray(),
                Visibility = (WorkshopVisibility)(int)b.Visibility,
                PreviewUrl = string.IsNullOrEmpty(b.PreviewUrl) ? null : b.PreviewUrl,
                Updated = b.UpdatedUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(b.UpdatedUnix)
                    : null,
                Created = b.CreatedUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(b.CreatedUnix)
                    : null,
                FileSizeBytes = b.FileSize,
                ContentFileName = string.IsNullOrEmpty(b.ContentFileName) ? null : b.ContentFileName,
            });
        }

        return new QueryPublishedResult
        {
            Items = items,
            TotalResults = bridgeResult.TotalResults,
            Page = req.Page,
        };
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseWorkshop", "host.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* logging is best-effort */ }
    }

    private PublishResult HandlePublish(string? payloadJson)
    {
        var edit = PipeJson.Deserialize<ItemEdit>(payloadJson ?? "null")
            ?? throw new InvalidOperationException("Missing publish payload.");

        Log($"Publish: id={edit.PublishedFileId?.ToString() ?? "new"} title='{edit.Title}' " +
            $"contentFile='{edit.ContentFile}' contentExists={(!string.IsNullOrWhiteSpace(edit.ContentFile) && File.Exists(edit.ContentFile))} " +
            $"preview='{edit.PreviewImagePath}'");

        // The bridge uploads the content/preview to Steam Cloud (RemoteStorage) directly from these
        // file paths - no folder staging needed. A content file is required for a brand-new item.
        if (!string.IsNullOrWhiteSpace(edit.ContentFile) && !File.Exists(edit.ContentFile))
            return new PublishResult { Success = false, Error = $"Content file not found: {edit.ContentFile}" };

        if (string.IsNullOrWhiteSpace(edit.ContentFile) && edit.PublishedFileId is null)
            return new PublishResult { Success = false, Error = "A content file is required to publish a new item." };

        var bridgeEdit = new BridgeEdit
        {
            PublishedFileId = edit.PublishedFileId ?? 0,
            Title = edit.Title,
            Description = edit.Description,
            Tags = edit.Tags.ToList(),
            Visibility = (BridgeVisibility)(int)edit.Visibility,
            ContentFile = edit.ContentFile,
            PreviewImagePath = edit.PreviewImagePath,
            ChangeNote = edit.ChangeNote,
        };

        var result = _workshop.Publish(bridgeEdit);
        Log($"Publish result: id={result.PublishedFileId} success={result.Success} error='{result.Error}'");
        return new PublishResult
        {
            PublishedFileId = result.PublishedFileId,
            NeedsLegalAgreement = result.NeedsLegalAgreement,
            Success = result.Success,
            Error = result.Error,
        };
    }

    private ProgressResult HandleProgress()
    {
        var p = _workshop.GetProgress();
        return new ProgressResult
        {
            BytesProcessed = p.BytesProcessed,
            BytesTotal = p.BytesTotal,
            Status = p.Status ?? string.Empty,
            Done = p.Done,
        };
    }
}
