namespace PulseWorkshop.Core.Ipc;

/// <summary>
/// Shared wire contract between the App (via Core's <c>SteamHostClient</c>) and the
/// <c>PulseWorkshop.SteamHost</c> process. Messages are newline-delimited JSON; one request gets
/// exactly one response.
/// </summary>
public static class PipeProtocol
{
    /// <summary>Base name for the named pipe; the active App ID is appended for per-game hosts.</summary>
    public const string PipeBaseName = "PulseWorkshop.SteamHost";

    public static string PipeNameFor(uint appId) => $"{PipeBaseName}.{appId}";
}

public enum RequestKind
{
    /// <summary>Verify Steam is running and the session is hooked; returns the current user.</summary>
    Ping,
    QueryPublished,
    Publish,
    GetProgress,
    Shutdown,
}

public sealed class PipeRequest
{
    public required RequestKind Kind { get; init; }

    /// <summary>Correlates a response with its request.</summary>
    public required string RequestId { get; init; }

    /// <summary>Kind-specific JSON payload, deserialized by the handler.</summary>
    public string? PayloadJson { get; init; }
}

public sealed class PipeResponse
{
    public required string RequestId { get; init; }

    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>Kind-specific JSON result payload.</summary>
    public string? PayloadJson { get; init; }
}

// --- Payloads -------------------------------------------------------------------------------

public sealed class PingResult
{
    public bool SteamRunning { get; init; }
    public ulong SteamId { get; init; }
    public string? PersonaName { get; init; }
    public uint AppId { get; init; }
}

public sealed class QueryPublishedRequest
{
    /// <summary>1-based page; ISteamUGC returns up to 50 items per page.</summary>
    public int Page { get; init; } = 1;
}

public sealed class QueryPublishedResult
{
    public required List<Models.WorkshopItem> Items { get; init; }
    public int TotalResults { get; init; }
    public int Page { get; init; }
}

public sealed class PublishResult
{
    public ulong PublishedFileId { get; init; }

    /// <summary>True when Steam requires the user to accept the Workshop legal agreement.</summary>
    public bool NeedsLegalAgreement { get; init; }

    /// <summary>False when the content upload failed (see <see cref="Error"/>).</summary>
    public bool Success { get; init; } = true;

    public string? Error { get; init; }
}

/// <summary>Progress of an in-flight upload, polled by the App while publishing.</summary>
public sealed class ProgressResult
{
    public ulong BytesProcessed { get; init; }
    public ulong BytesTotal { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool Done { get; init; }
}
