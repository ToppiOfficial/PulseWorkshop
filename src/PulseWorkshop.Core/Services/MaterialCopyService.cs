using System.Diagnostics;
using System.Text;

namespace PulseWorkshop.Core.Services;

/// <summary>Parameters for a single material copy invocation.</summary>
public sealed record MaterialCopyRequest(
    string ToolPath,
    string MdlPath,
    string GameInfoPath,
    string DestDir,
    bool Localize,
    bool FlatPatch);

/// <summary>Result returned by <see cref="MaterialCopyService.CopyAsync"/>.</summary>
public sealed record MaterialCopyResult(bool Success, string? Error);

/// <summary>
/// Invokes the native PulseWorkshop.ModelTool.exe to copy VMT + VTF files referenced by a
/// compiled .mdl into the chosen destination directory.
/// </summary>
public sealed class MaterialCopyService
{
    /// <summary>Fired for each stdout/stderr line produced by ModelTool. Raised on a thread-pool thread.</summary>
    public event Action<string>? Output;

    /// <summary>
    /// Run ModelTool asynchronously and wait for it to finish.
    /// Streams all output via <see cref="Output"/> as it arrives.
    /// </summary>
    public async Task<MaterialCopyResult> CopyAsync(MaterialCopyRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ToolPath) || !File.Exists(req.ToolPath))
            return new MaterialCopyResult(false, $"ModelTool not found: {req.ToolPath}");

        var args = new StringBuilder("materials");
        args.Append($" \"{req.MdlPath}\"");
        args.Append($" \"{req.GameInfoPath}\"");
        args.Append($" \"{req.DestDir}\"");
        if (req.Localize)  args.Append(" --localize");
        if (req.FlatPatch) args.Append(" --flat-patch");

        var psi = new ProcessStartInfo(req.ToolPath, args.ToString())
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            // WaitForExitAsync only waits for the process handle. Calling the no-arg overload
            // afterwards additionally drains all OutputDataReceived / ErrorDataReceived callbacks
            // so every output line has been delivered before we return.
            proc.WaitForExit();

            return proc.ExitCode == 0
                ? new MaterialCopyResult(true, null)
                : new MaterialCopyResult(false, $"ModelTool exited with code {proc.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return new MaterialCopyResult(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new MaterialCopyResult(false, ex.Message);
        }
    }
}
