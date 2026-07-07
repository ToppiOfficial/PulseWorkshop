using System.Diagnostics;
using System.Text;

namespace PulseWorkshop.Core.Services;

/// <summary>Parameters for a single model material-directory read.</summary>
public sealed record ModelMaterialDirsRequest(string ToolPath, string MdlPath);

/// <summary>Result of <see cref="ModelMaterialDirsService.GetDirsAsync"/>. <paramref name="Directories"/>
/// are the model's material folders, relative to the game's <c>materials/</c> folder (forward slashes).</summary>
public sealed record ModelMaterialDirsResult(bool Success, IReadOnlyList<string> Directories, string? Error);

/// <summary>
/// Invokes PulseWorkshop.ModelTool.exe's <c>matdirs</c> subcommand to read the distinct material
/// directories a compiled .mdl's textures live in (derived from its <c>$cdmaterials</c> + texture
/// names). Used to pre-create those folders under <c>materials/</c> for a fresh, untextured model.
/// </summary>
public sealed class ModelMaterialDirsService
{
    /// <summary>Runs ModelTool's <c>matdirs</c> command and returns one relative directory per line.</summary>
    public async Task<ModelMaterialDirsResult> GetDirsAsync(ModelMaterialDirsRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ToolPath) || !File.Exists(req.ToolPath))
            return new ModelMaterialDirsResult(false, Array.Empty<string>(), $"ModelTool not found: {req.ToolPath}");
        if (string.IsNullOrWhiteSpace(req.MdlPath) || !File.Exists(req.MdlPath))
            return new ModelMaterialDirsResult(false, Array.Empty<string>(), $"MDL not found: {req.MdlPath}");

        var psi = new ProcessStartInfo(req.ToolPath, $"matdirs \"{req.MdlPath}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        var dirs   = new List<string>();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) dirs.Add(e.Data.Trim()); };
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
            // Drains the remaining Output/Error callbacks so the captured list is complete.
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                return new ModelMaterialDirsResult(false, Array.Empty<string>(),
                    err.Length > 0 ? err : $"ModelTool exited with code {proc.ExitCode}");
            }

            return new ModelMaterialDirsResult(true, dirs, null);
        }
        catch (OperationCanceledException)
        {
            return new ModelMaterialDirsResult(false, Array.Empty<string>(), "Cancelled.");
        }
        catch (Exception ex)
        {
            return new ModelMaterialDirsResult(false, Array.Empty<string>(), ex.Message);
        }
    }
}
