using System.Diagnostics;
using System.Text;

namespace PulseWorkshop.Core.Services;

/// <summary>Parameters for a single model-info read.</summary>
public sealed record ModelInfoRequest(string ToolPath, string MdlPath);

/// <summary>Result of <see cref="ModelInfoService.GetInfoAsync"/>. <paramref name="Text"/> is the
/// human-readable summary block printed by ModelTool (bones, hitboxes, poly count, dependencies).</summary>
public sealed record ModelInfoResult(bool Success, string? Text, string? Error);

/// <summary>
/// Invokes PulseWorkshop.ModelTool.exe's <c>info</c> subcommand to read header + mesh stats from a
/// compiled .mdl (and its sibling .vtx/.vvd/.phy files) and returns the printed summary as text.
/// </summary>
public sealed class ModelInfoService
{
    /// <summary>Run ModelTool's <c>info</c> command and return its stdout summary. One-shot: unlike
    /// the material copy, output is collected and returned rather than streamed.</summary>
    public async Task<ModelInfoResult> GetInfoAsync(ModelInfoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ToolPath) || !File.Exists(req.ToolPath))
            return new ModelInfoResult(false, null, $"ModelTool not found: {req.ToolPath}");
        if (string.IsNullOrWhiteSpace(req.MdlPath) || !File.Exists(req.MdlPath))
            return new ModelInfoResult(false, null, $"MDL not found: {req.MdlPath}");

        var psi = new ProcessStartInfo(req.ToolPath, $"info \"{req.MdlPath}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

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
            // Drains the remaining Output/Error callbacks so the captured text is complete.
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                return new ModelInfoResult(false, null,
                    err.Length > 0 ? err : $"ModelTool exited with code {proc.ExitCode}");
            }

            return new ModelInfoResult(true, stdout.ToString().TrimEnd(), null);
        }
        catch (OperationCanceledException)
        {
            return new ModelInfoResult(false, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new ModelInfoResult(false, null, ex.Message);
        }
    }
}
