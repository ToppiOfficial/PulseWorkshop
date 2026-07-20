using System.Diagnostics;
using System.Text.RegularExpressions;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.Core.Services;

/// <summary>The resolved VTF tool path + base command template (from the project's selected game).</summary>
public sealed record TextureToolConfig(string? ToolPath, string? Command);

/// <summary>The tally of one group's run: how many source files matched and what happened to each.</summary>
public sealed record TextureGroupResult(int Matched, int Converted, int Skipped, int Failed)
{
    public bool Success => Failed == 0;
}

/// <summary>
/// Converts loose image files into Source <c>.vtf</c> textures in bulk - the successor to
/// KitsuneResource's <c>ValveTexturePipeline</c>. For one <see cref="TextureGroup"/> it finds the source
/// files matching the group's pattern under the project source folder, works out each output path, and
/// shells out to the game's VTF tool (the same tool + command template the Package tab uses). Sources are
/// never mutated. Streams the tool's output live (mirrors <see cref="PackageService"/>).
/// </summary>
public sealed class TextureConversionService
{
    /// <summary>Raised once per progress / tool-output line, live (streamed into the shared console).</summary>
    public event Action<string>? Output;

    /// <summary>
    /// Converts every file matched by <paramref name="group"/> under <paramref name="sourceFolder"/>.
    /// <paramref name="skipUpToDate"/> skips a file whose <c>.vtf</c> is already newer than the source
    /// (unless <paramref name="force"/>). Returns the run tally; a missing tool/command fails fast with a
    /// zero-match result.
    /// </summary>
    public async Task<TextureGroupResult> ConvertGroupAsync(
        string sourceFolder,
        TextureGroup group,
        TextureToolConfig vtf,
        bool skipUpToDate,
        bool force,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vtf.ToolPath) || !File.Exists(vtf.ToolPath))
        {
            Output?.Invoke("[textures] Skipped: the Game Setup VTF tool path is not set.");
            return new TextureGroupResult(0, 0, 0, 1);
        }

        var command = CombineCommand(vtf.Command, group.VtfCommand);
        if (string.IsNullOrWhiteSpace(command))
        {
            Output?.Invoke("[textures] Skipped: the VTF command is empty (set it in Game Setup or on the group).");
            return new TextureGroupResult(0, 0, 0, 1);
        }

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            Output?.Invoke($"[textures] Skipped: source folder not found ({sourceFolder}).");
            return new TextureGroupResult(0, 0, 0, 1);
        }

        Regex? regex = null;
        if (group.IsRegex)
        {
            try
            {
                regex = new Regex(group.InputPattern, RegexOptions.IgnoreCase);
            }
            catch (Exception ex)
            {
                Output?.Invoke($"[textures] Skipped: invalid regex '{group.InputPattern}' ({ex.Message}).");
                return new TextureGroupResult(0, 0, 0, 1);
            }
        }

        var root = Path.GetFullPath(sourceFolder);
        var matches = FindMatches(root, group, regex);
        Output?.Invoke($"[textures] {DisplayName(group)}: {matches.Count} file(s) matched.");

        int converted = 0, skipped = 0, failed = 0;
        foreach (var src in matches)
        {
            ct.ThrowIfCancellationRequested();

            var dest = ResolveOutput(root, src, group);

            // A custom output dir can never escape the source folder (it's a relative sub-folder).
            if (!string.IsNullOrWhiteSpace(group.OutputDir) && !IsUnder(root, dest))
            {
                Output?.Invoke($"[textures] Skipped {Path.GetFileName(src)} - output dir '{group.OutputDir}' escapes the source folder.");
                skipped++;
                continue;
            }

            // Never write the .vtf onto its own source (a .vtf matched with a beside-source output).
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
            {
                Output?.Invoke($"[textures] Skipped {Path.GetFileName(src)} - output would overwrite the source.");
                skipped++;
                continue;
            }

            if (!force && skipUpToDate && IsUpToDate(src, dest))
            {
                Output?.Invoke($"[textures] Up-to-date: {Path.GetFileName(src)}");
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (await ConvertOneAsync(src, dest, vtf.ToolPath!, command, ct))
                {
                    SyncTimestamp(src, dest);
                    converted++;
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Output?.Invoke($"[textures] Failed ({Path.GetFileName(src)}): {ex.Message}");
                failed++;
            }
        }

        return new TextureGroupResult(matches.Count, converted, skipped, failed);
    }

    /// <summary>Base game command + optional per-group command, space-joined (either may be blank).</summary>
    private static string CombineCommand(string? baseCommand, string? groupCommand)
    {
        var a = baseCommand?.Trim() ?? string.Empty;
        var b = groupCommand?.Trim() ?? string.Empty;
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    private static string DisplayName(TextureGroup group) =>
        string.IsNullOrWhiteSpace(group.Name) ? group.InputPattern : group.Name;

    // --- File matching --------------------------------------------------------------------------

    /// <summary>
    /// The files a group would convert, without converting anything - the exact same matching rule the
    /// run uses, exposed for the UI's match preview. A missing folder or an invalid regex yields an
    /// empty list rather than throwing (the user is mid-edit).
    /// </summary>
    public static IReadOnlyList<string> FindGroupMatches(string sourceFolder, TextureGroup group)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            return [];

        Regex? regex = null;
        if (group.IsRegex)
        {
            try
            {
                regex = new Regex(group.InputPattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return [];
            }
        }

        try
        {
            return FindMatches(Path.GetFullPath(sourceFolder), group, regex);
        }
        catch
        {
            // A folder that vanished or an unreadable sub-tree mid-scan: report nothing.
            return [];
        }
    }

    /// <summary>
    /// Drops the files an <b>earlier</b> group in the project already claims. Groups run top-to-bottom
    /// and the first one to convert a file stamps its <c>.vtf</c> as up-to-date, so a later group's
    /// pattern re-matching that file is a no-op - the preview shouldn't pretend otherwise.
    /// </summary>
    public static List<string> RemoveClaimed(string sourceFolder, IEnumerable<string> paths,
        IReadOnlyList<TextureGroup> earlierGroups)
    {
        var claimers = earlierGroups
            .Select(g => (Group: g, Match: BuildMatcher(g)))
            .Where(c => c.Match is not null)
            .ToList();
        if (claimers.Count == 0)
            return paths.ToList();

        var root = Path.GetFullPath(sourceFolder);
        var kept = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            var topLevel = string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase);
            // A non-recursive claimer only reaches files sitting directly in the source root.
            var claimed = claimers.Any(c => (c.Group.Recursive || topLevel) && c.Match!(name));
            if (!claimed)
                kept.Add(path);
        }
        return kept;
    }

    /// <summary>The group's name test as a predicate, or null when the group can never match anything
    /// (an invalid regex). Keeps "bad regex" from silently degrading into a substring search.</summary>
    private static Func<string, bool>? BuildMatcher(TextureGroup group)
    {
        if (!group.IsRegex)
            return name => name.Contains(group.InputPattern, StringComparison.OrdinalIgnoreCase);
        try
        {
            var regex = new Regex(group.InputPattern, RegexOptions.IgnoreCase);
            return regex.IsMatch;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every file under <paramref name="root"/> whose <b>name</b> matches the group's pattern
    /// (regex or case-insensitive substring), respecting the group's recursive flag.</summary>
    private static List<string> FindMatches(string root, TextureGroup group, Regex? regex)
    {
        var option = group.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", option))
        {
            var name = Path.GetFileName(file);
            var hit = regex is not null
                ? regex.IsMatch(name)
                : name.Contains(group.InputPattern, StringComparison.OrdinalIgnoreCase);
            if (hit)
                result.Add(Path.GetFullPath(file));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Whether a run would actually convert <paramref name="src"/> - i.e. its <c>.vtf</c> is missing or
    /// older than the source. Mirrors the run's own skip test, for the UI's "needs converting" flag.
    /// (A forced run converts regardless; this answers the normal, skip-up-to-date case.)
    /// </summary>
    public static bool IsOutOfDate(string sourceFolder, string src, TextureGroup group)
    {
        try
        {
            return !IsUpToDate(src, ResolveOutput(Path.GetFullPath(sourceFolder), src, group));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The output <c>.vtf</c> path for <paramref name="src"/>: beside the source when the group's
    /// output dir is blank, otherwise the source tree mirrored under that sub-folder of the source root.</summary>
    private static string ResolveOutput(string root, string src, TextureGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.OutputDir))
            return Path.GetFullPath(Path.ChangeExtension(src, ".vtf"));

        var rel = Path.GetRelativePath(root, src);
        var mirrored = Path.Combine(root, group.OutputDir, Path.ChangeExtension(rel, ".vtf"));
        return Path.GetFullPath(mirrored);
    }

    /// <summary>True when <paramref name="path"/> sits at or under <paramref name="root"/> (used to keep a
    /// custom output dir from escaping the source folder).</summary>
    private static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpToDate(string src, string dest)
    {
        if (!File.Exists(dest))
            return false;
        return File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(src);
    }

    /// <summary>Stamps the output with the source's last-write time so incremental runs can tell it's
    /// current (matches KitsuneResource, which synced mtime after each convert).</summary>
    private static void SyncTimestamp(string src, string dest)
    {
        try
        {
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
        }
        catch
        {
            // Best-effort: a failed timestamp copy only means the file re-converts next run.
        }
    }

    // --- VTF invocation (mirrors AssetPipelineService.ConvertToVtfAsync) ------------------------

    private async Task<bool> ConvertOneAsync(string input, string dest, string toolPath, string command,
        CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(dest) ?? Environment.CurrentDirectory;
        var args = command
            .Replace("{input}", input)
            .Replace("{output}", dest)
            .Replace("{outputdir}", destDir.TrimEnd('\\', '/'))
            .Replace("{outputname}", Path.GetFileNameWithoutExtension(dest));

        Output?.Invoke($"> \"{toolPath}\" {args}");

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
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
            Output?.Invoke($"[textures] VTF tool exited with code {process.ExitCode}.");
            return false;
        }

        // vtfcmd names its output after the input file, ignoring any desired output name; if the
        // expected dest differs, rename the produced file to match.
        var produced = Path.Combine(destDir, Path.GetFileNameWithoutExtension(input) + ".vtf");
        if (!string.Equals(Path.GetFullPath(produced), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(produced))
            {
                Output?.Invoke($"[textures] VTF tool succeeded but output not found ({produced}).");
                return false;
            }
            File.Move(produced, dest, overwrite: true);
        }

        Output?.Invoke($"[textures] vtf -> {dest}");
        return true;
    }
}
