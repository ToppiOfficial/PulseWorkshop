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

            // Source engine hardcodes vtf/vmt lookups to materials/, so a VTF asset written anywhere
            // else would never be found at runtime.
            if (asset.Kind == AssetKind.Image && asset.ImageFormat == ImageTargetFormat.Vtf
                && !IsMaterialsRooted(asset.OutputDir))
            {
                Output?.Invoke($"[asset] Skipped: VTF output dir must start with 'materials' (got '{asset.OutputDir}').");
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
                    : await ApplyImageAsync(asset, input, dest, destDir, root, resolveInput, vtf, ct);
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
        string root, Func<string, string> resolveInput, VtfToolConfig vtf, CancellationToken ct)
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
            if (!await ConvertToVtfAsync(input, dest, destDir, effectiveVtf, ct))
                return false;
            if (asset.CreateVmt)
                WriteVmt(asset, dest, root, resolveInput);
            return true;
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

    // --- VMT ------------------------------------------------------------------------------------

    /// <summary>Writes a <c>.vmt</c> next to the produced <paramref name="vtfDest"/> (same dir, same
    /// name), pointing <c>$basetexture</c> at the VTF - a path relative to <c>materials/</c> with no
    /// extension and forward slashes. Reads <see cref="PackageAsset.VmtTemplatePath"/> as the base (its
    /// existing <c>$basetexture</c> is rewritten, or one is inserted); a minimal material is generated
    /// when the template is blank or missing.</summary>
    private void WriteVmt(PackageAsset asset, string vtfDest, string root, Func<string, string> resolveInput)
    {
        var vmtPath = Path.ChangeExtension(vtfDest, ".vmt");
        var baseTexture = MaterialBaseTexturePath(root, vtfDest);

        string? template = null;
        if (!string.IsNullOrWhiteSpace(asset.VmtTemplatePath))
        {
            var resolved = resolveInput(asset.VmtTemplatePath);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                template = File.ReadAllText(resolved);
            else
                Output?.Invoke($"[asset] VMT template not found ({asset.VmtTemplatePath}) - generating a minimal material.");
        }

        var vmt = BuildVmt(template, baseTexture);
        File.WriteAllText(vmtPath, vmt);
        Output?.Invoke($"[asset] vmt -> {vmtPath} ($basetexture \"{baseTexture}\")");
    }

    /// <summary>The <c>$basetexture</c> value for a VTF at <paramref name="vtfDest"/>: its path relative
    /// to the entry's <c>materials/</c> folder, forward-slashed and without the <c>.vtf</c> extension.
    /// A VTF asset's output dir is always rooted at <c>materials</c> (enforced upstream), so the first
    /// path segment is dropped.</summary>
    private static string MaterialBaseTexturePath(string root, string vtfDest)
    {
        var rel = Path.GetRelativePath(root, vtfDest).Replace('\\', '/').TrimStart('/');
        var slash = rel.IndexOf('/');
        if (slash >= 0)
            rel = rel[(slash + 1)..]; // drop the leading "materials/" segment
        if (rel.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^4];
        return rel;
    }

    private static readonly System.Text.RegularExpressions.Regex BaseTextureLine = new(
        @"^(?<indent>[ \t]*)(?<q>""?)\$basetexture\k<q>[ \t]+(?:""[^""\r\n]*""|\S+).*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Multiline
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Produces VMT text with <c>$basetexture</c> set to <paramref name="baseTexture"/>. If the
    /// template already declares a <c>$basetexture</c> its value is replaced (indentation preserved);
    /// otherwise one is inserted after the material's opening brace. A blank template yields a minimal
    /// <c>VertexLitGeneric</c> material.</summary>
    internal static string BuildVmt(string? template, string baseTexture)
    {
        if (string.IsNullOrWhiteSpace(template))
            return $"\"VertexLitGeneric\"\r\n{{\r\n\t\"$basetexture\" \"{baseTexture}\"\r\n}}\r\n";

        if (BaseTextureLine.IsMatch(template))
            return BaseTextureLine.Replace(template,
                m => $"{m.Groups["indent"].Value}\"$basetexture\" \"{baseTexture}\"", 1);

        // No $basetexture in the template - insert one just after the first opening brace.
        var brace = template.IndexOf('{');
        if (brace >= 0)
        {
            var insert = $"\r\n\t\"$basetexture\" \"{baseTexture}\"";
            return template.Insert(brace + 1, insert);
        }

        // Malformed template (no brace) - fall back to a generated material.
        return $"\"VertexLitGeneric\"\r\n{{\r\n\t\"$basetexture\" \"{baseTexture}\"\r\n}}\r\n";
    }

    /// <summary>The absolute output dir for <paramref name="outputDir"/> under <paramref name="root"/>,
    /// or null if it would escape the folder (the asset output is always sandboxed inside the entry).
    /// Internal (not private) so the UI can run the same check live, before packaging.</summary>
    internal static string? SandboxedDir(string root, string outputDir)
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

    /// <summary>True when <paramref name="outputDir"/>'s first path segment is "materials" (case-insensitive).
    /// Source engine hardcodes vtf/vmt lookups relative to materials/, so a VTF asset outside it would
    /// never be found by the game at runtime. Internal (not private) so the UI can run the same check
    /// live, before packaging.</summary>
    internal static bool IsMaterialsRooted(string outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            return false;

        var normalized = outputDir.Replace('\\', '/').Trim('/');
        var slashIndex = normalized.IndexOf('/');
        var first = slashIndex < 0 ? normalized : normalized[..slashIndex];
        return string.Equals(first, "materials", StringComparison.OrdinalIgnoreCase);
    }
}
