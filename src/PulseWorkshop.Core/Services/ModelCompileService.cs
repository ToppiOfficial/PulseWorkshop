using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.Core.Services;

/// <summary>What the compiled model files are copied to after a successful compile.</summary>
public enum CompileOutputMode
{
    /// <summary>Leave studiomdl's output where it wrote it (under the game's models folder).</summary>
    LeaveInGame,

    /// <summary>Copy beside the .qc into a named subfolder (default "compiled{version}").</summary>
    Subfolder,

    /// <summary>Copy to a custom absolute location (Crowbar-style "work folder").</summary>
    WorkFolder,
}

/// <summary>
/// One "Simple" compile request: the resolved tool paths plus the output destination. A null
/// <see cref="DestinationBase"/> means "leave the files in the game" (no copy step).
/// </summary>
public sealed record CompileRequest(
    string StudioMdlPath,
    string GameInfoDir,
    string QcPath,
    string ExtraOptions,
    string? DestinationBase,
    bool CleanBeforeTransfer = false);

/// <summary>The outcome of a compile: process result plus the model files found and copied.</summary>
public sealed record CompileResult(
    bool Success,
    int ExitCode,
    IReadOnlyList<string> CompiledMdls,
    IReadOnlyList<string> CopiedFiles,
    string? Error);

/// <summary>
/// Runs Source's <c>studiomdl</c> on a single .qc (Crowbar's "Simple" compile), streaming its live
/// output and then gathering the compiled model files.
///
/// Unlike Crowbar it does NOT scan the .qc for <c>$modelname</c>. studiomdl prints a
/// "writing ...\X.mdl" line (only with <c>-verbose</c>, which is why we force it on); we use that
/// solely to locate the compiled <c>.mdl</c>, then derive the rest of the model's files from the
/// <c>.mdl</c> itself (its sibling parts + any include models) rather than trusting every per-file
/// print line.
/// </summary>
public sealed class ModelCompileService
{
    /// <summary>The file group studiomdl emits for one model, keyed off the .mdl's base name.</summary>
    private static readonly string[] ModelPartExtensions =
        { ".mdl", ".vvd", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx", ".phy", ".ani" };

    // studiomdl emits e.g. "writing d:\...\models/toppi/x.mdl:" (note mixed slashes, trailing ':').
    private static readonly Regex WritingMdlLine =
        new(@"writing\s+(.+?\.mdl)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A .qc's "$modelname <path>" line (quoted or bare). Used only by "clean before transfer" to
    // locate the previous in-game build before a fresh compile.
    private static readonly Regex ModelNameLine =
        new(@"^\s*\$modelname\s+""?([^""\r\n]+?)""?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Raised once per studiomdl stdout/stderr line (and per copied file), live.</summary>
    public event Action<string>? Output;

    /// <summary>
    /// Builds the studiomdl argument string for the Simple compile: forces <c>-verbose</c> on and
    /// strips any user-typed <c>-quiet</c>/<c>-verbose</c> (we add our own). Shared by the real
    /// compile and the UI's command preview so they never drift.
    /// </summary>
    public static string BuildArguments(string gameInfoDir, string qcPath, string extraOptions)
    {
        var sb = new StringBuilder();
        sb.Append("-game \"").Append(gameInfoDir.TrimEnd('\\', '/')).Append("\" -nop4 -verbose");

        foreach (var token in SanitizeOptions(extraOptions))
            sb.Append(' ').Append(token);

        sb.Append(" \"").Append(qcPath).Append('"');
        return sb.ToString();
    }

    /// <summary>Drops <c>-quiet</c> (incompatible with the verbose output we rely on) and any
    /// duplicate <c>-verbose</c> from the user's options; everything else is kept as-is.</summary>
    private static IEnumerable<string> SanitizeOptions(string extraOptions)
    {
        if (string.IsNullOrWhiteSpace(extraOptions))
            return Array.Empty<string>();

        return extraOptions
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("-quiet", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("-verbose", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CompileResult> CompileAsync(CompileRequest req, CancellationToken ct = default)
    {
        // Validate up front so the user gets one clear reason instead of an opaque process failure.
        if (string.IsNullOrWhiteSpace(req.StudioMdlPath) || !File.Exists(req.StudioMdlPath))
            return Fail($"Model compiler not found: {req.StudioMdlPath}");
        if (string.IsNullOrWhiteSpace(req.GameInfoDir) || !Directory.Exists(req.GameInfoDir))
            return Fail($"Game folder (gameinfo.txt directory) not found: {req.GameInfoDir}");
        if (string.IsNullOrWhiteSpace(req.QcPath) || !File.Exists(req.QcPath))
            return Fail($"QC file not found: {req.QcPath}");

        // "Clean before transfer": only meaningful when we're moving the output out of the game
        // (a destination is set). Deleting the previous in-game build first stops stale sibling
        // files from a former compile (e.g. an .ani that's no longer produced) leaking into the move.
        if (req.CleanBeforeTransfer && !string.IsNullOrWhiteSpace(req.DestinationBase))
            CleanInGameModel(req.QcPath, req.GameInfoDir);

        var args = BuildArguments(req.GameInfoDir, req.QcPath, req.ExtraOptions);
        Output?.Invoke($"> \"{req.StudioMdlPath}\" {args}");

        var psi = new ProcessStartInfo
        {
            FileName = req.StudioMdlPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // studiomdl resolves its own DLLs relative to its folder.
            WorkingDirectory = Path.GetDirectoryName(req.StudioMdlPath) ?? Environment.CurrentDirectory,
        };

        var mdlPaths = new List<string>();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) => OnLine(e.Data, mdlPaths);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data, mdlPaths);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Cancellation kills studiomdl (and its children) so a cancelled compile stops for real.
            await using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // WaitForExitAsync only waits for the process to exit. Calling the no-arg overload
            // afterwards additionally drains any remaining OutputDataReceived / ErrorDataReceived
            // events so they are all delivered before we continue.
            process.WaitForExit();

            var exitCode = process.ExitCode;
            var distinctMdls = mdlPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (exitCode != 0)
                return new CompileResult(false, exitCode, distinctMdls, Array.Empty<string>(),
                    $"studiomdl exited with code {exitCode}.");

            if (distinctMdls.Count == 0)
                return new CompileResult(false, exitCode, distinctMdls, Array.Empty<string>(),
                    "Compile finished but no '.mdl' was written (check the output above).");

            var copied = CopyOutputs(req, distinctMdls);
            return new CompileResult(true, exitCode, distinctMdls, copied, null);
        }
        catch (OperationCanceledException)
        {
            return Fail("Compile was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to run studiomdl: {ex.Message}");
        }
    }

    private void OnLine(string? line, List<string> mdlPaths)
    {
        if (line is null)
            return;

        Output?.Invoke(line);

        var match = WritingMdlLine.Match(line);
        if (match.Success)
        {
            var path = NormalizeSlashes(match.Groups[1].Value.Trim());
            if (path.Length > 0)
                mdlPaths.Add(path);
        }
    }

    /// <summary>
    /// Moves every gathered model file to the chosen destination, preserving the path relative to the
    /// gameinfo dir (so the result lands under <c>&lt;dest&gt;\models\...</c> and is pack-ready). The
    /// files are <b>moved</b>, not copied, so the game folder is left clean (Crowbar-style).
    /// No-op when the mode is "leave in game" (null destination).
    /// </summary>
    private IReadOnlyList<string> CopyOutputs(CompileRequest req, IReadOnlyList<string> mdlPaths)
    {
        if (string.IsNullOrWhiteSpace(req.DestinationBase))
            return Array.Empty<string>();

        var sources = new List<string>();
        foreach (var mdl in mdlPaths)
            sources.AddRange(GatherModelFiles(mdl));

        var moved = new List<string>();
        foreach (var src in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var rel = Path.GetRelativePath(req.GameInfoDir, src);
                // If the file isn't under the game dir, fall back to moving it by name only.
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                    rel = Path.GetFileName(src);

                var dest = Path.Combine(req.DestinationBase, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src, dest, overwrite: true);
                moved.Add(dest);
                Output?.Invoke($"moved {rel}");
            }
            catch (Exception ex)
            {
                Output?.Invoke($"WARNING: could not move {src}: {ex.Message}");
            }
        }

        return moved;
    }

    /// <summary>
    /// Deletes the previous in-game build of the model the .qc compiles to (its <c>$modelname</c>
    /// sibling group), so a fresh compile + move can't carry stale files forward. Fully best-effort:
    /// a missing $modelname or any IO error simply leaves things as-is.
    /// </summary>
    private void CleanInGameModel(string qcPath, string gameInfoDir)
    {
        var modelName = TryReadModelName(qcPath);
        if (string.IsNullOrWhiteSpace(modelName))
            return;

        var mdlPath = Path.Combine(gameInfoDir, "models", NormalizeSlashes(modelName));
        if (!mdlPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            mdlPath += ".mdl";

        foreach (var file in GatherModelFiles(mdlPath))
        {
            try
            {
                File.Delete(file);
                Output?.Invoke($"cleaned {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                Output?.Invoke($"WARNING: could not clean {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Reads the <c>$modelname</c> path from a .qc (best-effort; empty on any problem).</summary>
    private static string TryReadModelName(string qcPath)
    {
        try
        {
            if (!File.Exists(qcPath))
                return string.Empty;
            var match = ModelNameLine.Match(File.ReadAllText(qcPath));
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The on-disk file group for one compiled model: the sibling files sharing the .mdl's base name
    /// across the known studiomdl output extensions (only those that actually exist).
    /// </summary>
    private static IEnumerable<string> GatherModelFiles(string mdlPath)
    {
        var dir = Path.GetDirectoryName(mdlPath);
        if (string.IsNullOrEmpty(dir))
            yield break;

        var baseName = Path.GetFileNameWithoutExtension(mdlPath);
        foreach (var ext in ModelPartExtensions)
        {
            var candidate = Path.Combine(dir, baseName + ext);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static string NormalizeSlashes(string path) =>
        path.Replace('/', '\\');

    private static CompileResult Fail(string error) =>
        new(false, -1, Array.Empty<string>(), Array.Empty<string>(), error);
}
