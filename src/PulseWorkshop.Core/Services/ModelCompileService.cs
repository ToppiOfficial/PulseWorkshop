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
    string? DestinationBase);

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
    /// Copies every gathered model file to the chosen destination, preserving the path relative to
    /// the gameinfo dir (so the result lands under <c>&lt;dest&gt;\models\...</c> and is pack-ready).
    /// No-op when the mode is "leave in game" (null destination).
    /// </summary>
    private IReadOnlyList<string> CopyOutputs(CompileRequest req, IReadOnlyList<string> mdlPaths)
    {
        if (string.IsNullOrWhiteSpace(req.DestinationBase))
            return Array.Empty<string>();

        var sources = new List<string>();
        foreach (var mdl in mdlPaths)
        {
            sources.AddRange(GatherModelFiles(mdl));
            // Best-effort: follow include models referenced inside the .mdl, resolved against the
            // game's models folder. Any failure here is swallowed - the sibling group is the guarantee.
            foreach (var include in TryReadIncludeModels(mdl, req.GameInfoDir))
                sources.AddRange(GatherModelFiles(include));
        }

        var copied = new List<string>();
        foreach (var src in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var rel = Path.GetRelativePath(req.GameInfoDir, src);
                // If the file isn't under the game dir, fall back to copying it by name only.
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                    rel = Path.GetFileName(src);

                var dest = Path.Combine(req.DestinationBase, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
                copied.Add(dest);
                Output?.Invoke($"copied {rel}");
            }
            catch (Exception ex)
            {
                Output?.Invoke($"WARNING: could not copy {src}: {ex.Message}");
            }
        }

        return copied;
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

    /// <summary>
    /// Reads a compiled .mdl's studiohdr and returns the disk paths of its include models, resolved
    /// against <c>&lt;gameInfoDir&gt;\models</c>. Fully defensive: any parsing problem yields nothing.
    /// </summary>
    private static IEnumerable<string> TryReadIncludeModels(string mdlPath, string gameInfoDir)
    {
        try
        {
            var info = new FileInfo(mdlPath);
            if (!info.Exists || info.Length is < 348 or > 128 * 1024 * 1024)
                return Array.Empty<string>();

            var buf = File.ReadAllBytes(mdlPath);

            // studiohdr_t: numincludemodels @ 336, includemodelindex @ 340 (mdl v44+).
            var count = BitConverter.ToInt32(buf, 336);
            var index = BitConverter.ToInt32(buf, 340);
            if (count is <= 0 or > 256 || index <= 0 || index >= buf.Length)
                return Array.Empty<string>();

            var modelsRoot = Path.Combine(gameInfoDir, "models");
            var results = new List<string>();
            for (var i = 0; i < count; i++)
            {
                // mstudiomodelgroup_t { int szlabelindex; int sznameindex; } - name offset is
                // relative to the group struct's own base.
                var groupBase = index + i * 8;
                if (groupBase + 8 > buf.Length)
                    break;

                var nameOffset = BitConverter.ToInt32(buf, groupBase + 4);
                var nameAbs = groupBase + nameOffset;
                var name = ReadCString(buf, nameAbs);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var resolved = Path.Combine(modelsRoot, NormalizeSlashes(name));
                if (File.Exists(resolved))
                    results.Add(resolved);
            }

            return results;
        }
        catch
        {
            // Best-effort only.
            return Array.Empty<string>();
        }
    }

    private static string ReadCString(byte[] buf, int offset)
    {
        if (offset < 0 || offset >= buf.Length)
            return string.Empty;

        var end = offset;
        while (end < buf.Length && buf[end] != 0)
            end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset);
    }

    private static string NormalizeSlashes(string path) =>
        path.Replace('/', '\\');

    private static CompileResult Fail(string error) =>
        new(false, -1, Array.Empty<string>(), Array.Empty<string>(), error);
}
