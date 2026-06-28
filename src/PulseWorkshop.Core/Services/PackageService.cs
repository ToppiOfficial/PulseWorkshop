using System.Diagnostics;
using System.Text;

namespace PulseWorkshop.Core.Services;

/// <summary>
/// One package request: the resolved packer path, the folder to pack, and any extra command-line
/// options for that entry.
/// </summary>
public sealed record PackageRequest(
    string PackerToolPath,
    string FolderPath,
    string ExtraOptions);

/// <summary>The outcome of a package run: process result plus the produced <c>.vpk</c>/<c>.gma</c>.</summary>
public sealed record PackageResult(
    bool Success,
    int ExitCode,
    string? OutputPackagePath,
    string? Error);

/// <summary>
/// Packs a content folder into a single <c>.vpk</c> (Source) or <c>.gma</c> (GMod) by shelling out to
/// the game's packer (<c>vpk.exe</c> / <c>gmad.exe</c>, configured as the Game Setup "Packer tool").
/// The packer writes the package beside the folder, so the output path is derived, not chosen. Streams
/// the tool's output live (mirrors <see cref="ModelCompileService"/>).
/// </summary>
public sealed class PackageService
{
    /// <summary>Raised once per packer stdout/stderr line, live.</summary>
    public event Action<string>? Output;

    public async Task<PackageResult> PackageAsync(PackageRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.PackerToolPath) || !File.Exists(req.PackerToolPath))
            return Fail($"Packer tool not found: {req.PackerToolPath}");
        if (string.IsNullOrWhiteSpace(req.FolderPath) || !Directory.Exists(req.FolderPath))
            return Fail($"Folder to package not found: {req.FolderPath}");

        var folder = req.FolderPath.TrimEnd('\\', '/');
        var isGmad = Path.GetFileNameWithoutExtension(req.PackerToolPath)
            .Contains("gmad", StringComparison.OrdinalIgnoreCase);

        // gmad needs an explicit output; vpk writes "<folder>.vpk" beside the folder on its own.
        var outputPath = folder + (isGmad ? ".gma" : ".vpk");
        var args = isGmad ? BuildGmadArgs(folder, outputPath, req.ExtraOptions)
                          : BuildVpkArgs(folder, req.ExtraOptions);

        Output?.Invoke($"> \"{req.PackerToolPath}\" {args}");

        var psi = new ProcessStartInfo
        {
            FileName = req.PackerToolPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(folder) ?? Environment.CurrentDirectory,
        };

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            process.WaitForExit();

            var exitCode = process.ExitCode;
            if (exitCode != 0)
                return new PackageResult(false, exitCode, null, $"Packer exited with code {exitCode}.");

            if (!File.Exists(outputPath))
                return new PackageResult(false, exitCode, null,
                    $"Packer finished but no package was found at {outputPath} (check the output above).");

            Output?.Invoke($"packaged -> {outputPath}");
            return new PackageResult(true, exitCode, outputPath, null);
        }
        catch (OperationCanceledException)
        {
            return Fail("Package was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to run packer: {ex.Message}");
        }
    }

    // vpk.exe: options precede the single input folder; it writes "<folder>.vpk" beside it.
    private static string BuildVpkArgs(string folder, string extra)
    {
        var sb = new StringBuilder();
        AppendExtra(sb, extra);
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append('"').Append(folder).Append('"');
        return sb.ToString();
    }

    // gmad.exe create -folder <in> -out <out> [extra]
    private static string BuildGmadArgs(string folder, string output, string extra)
    {
        var sb = new StringBuilder();
        sb.Append("create -folder \"").Append(folder).Append("\" -out \"").Append(output).Append('"');
        AppendExtra(sb.Append(' '), extra);
        return sb.ToString().TrimEnd();
    }

    private static void AppendExtra(StringBuilder sb, string extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return;
        // The entry command may be typed across lines for readability; flatten to a single arg string.
        var flattened = string.Join(' ', extra.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        sb.Append(flattened);
    }

    private static PackageResult Fail(string error) => new(false, -1, null, error);
}
