using System.IO.Pipes;
using System.Text;
using SrcWorkshop.Core.Ipc;
using SrcWorkshop.Core.Models;
using SrcWorkshop.SteamBridge;

namespace SrcWorkshop.SteamHost;

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
            });
        }

        return new QueryPublishedResult
        {
            Items = items,
            TotalResults = bridgeResult.TotalResults,
            Page = req.Page,
        };
    }

    private PublishResult HandlePublish(string? payloadJson)
    {
        var edit = PipeJson.Deserialize<ItemEdit>(payloadJson ?? "null")
            ?? throw new InvalidOperationException("Missing publish payload.");

        // ISteamUGC::SetItemContent wants a FOLDER, but the user picks a single packed file
        // (.vpk / .gma). Stage that file into a temp folder and point Steam at the folder.
        string? stagedFolder = StageContentFile(edit.ContentFile);
        try
        {
            var bridgeEdit = new BridgeEdit
            {
                PublishedFileId = edit.PublishedFileId ?? 0,
                Title = edit.Title,
                Description = edit.Description,
                Tags = edit.Tags.ToList(),
                Visibility = (BridgeVisibility)(int)edit.Visibility,
                ContentFolder = stagedFolder,
                PreviewImagePath = edit.PreviewImagePath,
                ChangeNote = edit.ChangeNote,
            };

            var result = _workshop.Publish(bridgeEdit);
            return new PublishResult
            {
                PublishedFileId = result.PublishedFileId,
                NeedsLegalAgreement = result.NeedsLegalAgreement,
            };
        }
        finally
        {
            if (stagedFolder is not null)
            {
                try { Directory.Delete(stagedFolder, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Copies a single content file into a fresh temp folder and returns that folder,
    /// or null when no content file was supplied (e.g. metadata-only edits).</summary>
    private static string? StageContentFile(string? contentFile)
    {
        if (string.IsNullOrWhiteSpace(contentFile) || !File.Exists(contentFile))
            return null;

        var dir = Path.Combine(Path.GetTempPath(), "SrcWorkshop_content_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(contentFile, Path.Combine(dir, Path.GetFileName(contentFile)), overwrite: true);
        return dir;
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
