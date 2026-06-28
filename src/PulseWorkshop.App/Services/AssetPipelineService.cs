using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Bakes a package entry's pre-assets into its folder before packing: each <see cref="PackageAsset"/>
/// is transformed and written to a path <b>inside</b> the entry folder (sandboxed - it can never
/// escape). Sources are never mutated, so the same shared input can be reused by other entries.
///
/// - Text: read the source, apply the regex passes in order, write the result.
/// - Image (non-VTF): decode with WPF and re-encode to the chosen raster format.
/// - Image (VTF): launch the Game Setup VTF tool with the entry's command template.
/// Lives in the App project because the raster re-encode needs WPF imaging.
/// </summary>
public sealed class AssetPipelineService
{
    /// <summary>The resolved VTF tool path + command template (from the project's selected game).</summary>
    public sealed record VtfToolConfig(string? ToolPath, string? Command);

    /// <summary>Raised once per progress line, live (streamed into the Package terminal).</summary>
    public event Action<string>? Output;

    /// <summary>
    /// Applies every asset for one entry. <paramref name="resolveInput"/> turns a stored input path
    /// (relative to the project, or absolute) into an absolute path. Returns false if any asset failed.
    /// </summary>
    public async Task<bool> ApplyAsync(
        string entryFolder,
        IReadOnlyList<PackageAsset> assets,
        Func<string, string> resolveInput,
        VtfToolConfig vtf,
        CancellationToken ct = default)
    {
        if (assets.Count == 0)
            return true;

        var root = Path.GetFullPath(entryFolder);
        var allOk = true;

        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();

            var input = resolveInput(asset.InputPath);
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                Output?.Invoke($"[asset] Skipped: input not found ({asset.InputPath}).");
                allOk = false;
                continue;
            }

            var destDir = SandboxedDir(root, asset.OutputDir);
            if (destDir is null)
            {
                Output?.Invoke($"[asset] Skipped: output dir '{asset.OutputDir}' escapes the package folder.");
                allOk = false;
                continue;
            }

            var fileName = string.IsNullOrWhiteSpace(asset.OutputFileName)
                ? Path.GetFileName(input)
                : asset.OutputFileName;
            var dest = Path.Combine(destDir, fileName);

            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            {
                Output?.Invoke($"[asset] Skipped: output would overwrite the source ({fileName}).");
                allOk = false;
                continue;
            }

            try
            {
                Directory.CreateDirectory(destDir);
                var ok = asset.Kind == AssetKind.Text
                    ? ApplyText(asset, input, dest)
                    : await ApplyImageAsync(asset, input, dest, destDir, vtf, ct);
                allOk &= ok;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Output?.Invoke($"[asset] Failed ({fileName}): {ex.Message}");
                allOk = false;
            }
        }

        return allOk;
    }

    // --- Text -----------------------------------------------------------------------------------

    private bool ApplyText(PackageAsset asset, string input, string dest)
    {
        // Read the whole source first, so writing the result never depends on the source file again.
        var text = File.ReadAllText(input);
        foreach (var r in asset.RegexReplaces)
        {
            if (string.IsNullOrEmpty(r.Pattern))
                continue;
            var options = System.Text.RegularExpressions.RegexOptions.None;
            if (r.IgnoreCase) options |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            if (r.Multiline) options |= System.Text.RegularExpressions.RegexOptions.Multiline;
            var pattern = r.IsLiteral
                ? System.Text.RegularExpressions.Regex.Escape(r.Pattern)
                : r.Pattern;
            var replacement = r.IsLiteral
                ? r.Replacement?.Replace("$", "$$") ?? string.Empty
                : r.Replacement ?? string.Empty;
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement, options);
        }
        File.WriteAllText(dest, text);
        Output?.Invoke($"[asset] text -> {dest}");
        return true;
    }

    // --- Image ----------------------------------------------------------------------------------

    private async Task<bool> ApplyImageAsync(PackageAsset asset, string input, string dest, string destDir,
        VtfToolConfig vtf, CancellationToken ct)
    {
        // If the user left out the extension on the output name, append the one implied by the format.
        if (string.IsNullOrEmpty(Path.GetExtension(dest)))
        {
            var ext = asset.ImageFormat switch
            {
                ImageTargetFormat.Copy => Path.GetExtension(input),
                ImageTargetFormat.Vtf  => ".vtf",
                ImageTargetFormat.Png  => ".png",
                ImageTargetFormat.Jpg  => ".jpg",
                ImageTargetFormat.Gif  => ".gif",
                ImageTargetFormat.Bmp  => ".bmp",
                ImageTargetFormat.Tiff => ".tiff",
                _                      => ".png",
            };
            dest = dest + ext;
        }

        if (asset.ImageFormat == ImageTargetFormat.Vtf)
        {
            VtfToolConfig effectiveVtf;
            if (!string.IsNullOrWhiteSpace(asset.VtfCommand))
            {
                var combined = string.IsNullOrWhiteSpace(vtf.Command)
                    ? asset.VtfCommand
                    : vtf.Command + " " + asset.VtfCommand;
                effectiveVtf = new VtfToolConfig(vtf.ToolPath, combined);
            }
            else
            {
                effectiveVtf = vtf;
            }
            return await ConvertToVtfAsync(input, dest, destDir, effectiveVtf, ct);
        }

        if (asset.ImageFormat == ImageTargetFormat.Copy)
        {
            File.Copy(input, dest, overwrite: true);
            Output?.Invoke($"[asset] image copy -> {dest}");
            return true;
        }

        // Decode with OnLoad so the source handle is released immediately (the source is never locked
        // or mutated), then re-encode to the chosen format.
        BitmapFrame frame;
        using (var inStream = File.OpenRead(input))
        {
            var decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        BitmapEncoder encoder = asset.ImageFormat switch
        {
            ImageTargetFormat.Png => new PngBitmapEncoder(),
            ImageTargetFormat.Jpg => new JpegBitmapEncoder(),
            ImageTargetFormat.Gif => new GifBitmapEncoder(),
            ImageTargetFormat.Bmp => new BmpBitmapEncoder(),
            ImageTargetFormat.Tiff => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using (var outStream = File.Create(dest))
            encoder.Save(outStream);

        Output?.Invoke($"[asset] image {asset.ImageFormat} -> {dest}");
        return true;
    }

    private async Task<bool> ConvertToVtfAsync(string input, string dest, string destDir,
        VtfToolConfig vtf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vtf.ToolPath) || !File.Exists(vtf.ToolPath))
        {
            Output?.Invoke("[asset] VTF skipped: the Game Setup VTF tool path is not set.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(vtf.Command))
        {
            Output?.Invoke("[asset] VTF skipped: the Game Setup VTF command is empty.");
            return false;
        }

        var args = vtf.Command
            .Replace("{input}", input)
            .Replace("{output}", dest)
            .Replace("{outputdir}", destDir.TrimEnd('\\', '/'))
            .Replace("{outputname}", Path.GetFileNameWithoutExtension(dest));

        Output?.Invoke($"> \"{vtf.ToolPath}\" {args}");

        var psi = new ProcessStartInfo
        {
            FileName = vtf.ToolPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(vtf.ToolPath) ?? Environment.CurrentDirectory,
        };

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

        if (process.ExitCode != 0)
        {
            Output?.Invoke($"[asset] VTF tool exited with code {process.ExitCode}.");
            return false;
        }

        // vtfcmd names its output after the input file, ignoring any desired output name.
        // If the expected dest differs, rename the produced file to match.
        var produced = Path.Combine(destDir, Path.GetFileNameWithoutExtension(input) + ".vtf");
        if (!string.Equals(Path.GetFullPath(produced), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(produced))
            {
                Output?.Invoke($"[asset] VTF tool succeeded but output not found ({produced}).");
                return false;
            }
            File.Move(produced, dest, overwrite: true);
        }

        Output?.Invoke($"[asset] vtf -> {dest}");
        return true;
    }

    /// <summary>The absolute output dir for <paramref name="outputDir"/> under <paramref name="root"/>,
    /// or null if it would escape the folder (the asset output is always sandboxed inside the entry).</summary>
    private static string? SandboxedDir(string root, string outputDir)
    {
        var fullRoot = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(outputDir))
            return fullRoot;

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(fullRoot, outputDir));
        }
        catch
        {
            return null;
        }

        if (combined.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return combined;
        return null;
    }
}
