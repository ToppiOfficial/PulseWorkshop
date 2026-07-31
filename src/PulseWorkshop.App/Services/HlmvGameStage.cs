using System.IO;
using System.Security.Cryptography;
using System.Text;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Makes a .mdl loadable by an external model viewer (HLMV), the way Crowbar does.
///
/// HLMV loads a model through the game filesystem, so it can only open one that actually lives
/// under a mounted search path at its own internal model name (the path baked into the .mdl
/// header - that is also how the engine finds the .vvd/.vtx siblings and the model's materials).
/// A model sitting anywhere else - a working folder, a compile output outside the game - simply
/// fails to load.
///
/// So when the model is not reachable through the game's gameinfo.txt we build a "fake game": a
/// folder next to the real mod folder in the game root (deliberately NOT one gameinfo.txt lists,
/// so the real game never mounts it), holding a patched copy of gameinfo.txt that mounts both the
/// fake folder and the real mod folder, plus a copy of the model at its internal name. The viewer
/// is then launched with <c>-game &lt;fake folder&gt;</c>: the model loads from the fake folder,
/// everything else (materials, $includemodel targets) still resolves out of the real game.
///
/// The copy is checksum-compared, so re-opening the same unchanged model reuses what is already
/// staged. Staged folders are deleted on app exit (<see cref="CleanupAll"/>); a hard crash leaves
/// one behind, which the next session adopts - it is reused, then cleaned up on that session's exit.
/// </summary>
public static class HlmvGameStage
{
    /// <summary>The fake game folder's name, created in the game root beside the real mod folder.</summary>
    private const string StageFolderName = "pulseworkshop_view";

    // The files that make up a compiled model, keyed off the .mdl's stem. Materials are never copied -
    // the viewer resolves those through the real game folder the staged gameinfo.txt mounts.
    private static readonly string[] ModelFileSuffixes =
    {
        ".mdl", ".vvd", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx", ".phy", ".ani",
    };

    // Fake game folders created (or adopted) this session, deleted on exit.
    private static readonly List<string> Staged = new();

    /// <summary>How the viewer should be launched: the <c>-game</c> folder (null when there is no
    /// gameinfo to point at), the model path to pass, whether that path is a staged copy, and the
    /// reason staging was skipped when it was wanted but failed.</summary>
    public sealed record Launch(string? GameDir, string ModelPath, bool IsStaged, string? Problem);

    /// <summary>
    /// Works out how to open <paramref name="mdlPath"/> in the viewer, staging it into a fake game
    /// folder when the real game cannot see it. Never throws - a staging failure falls back to a
    /// plain launch against the real mod folder and is reported in <see cref="Launch.Problem"/>.
    /// </summary>
    public static Launch Prepare(string mdlPath, string? gameInfoPath)
    {
        mdlPath = Path.GetFullPath(mdlPath);

        if (string.IsNullOrWhiteSpace(gameInfoPath) || !File.Exists(gameInfoPath))
            return new Launch(null, mdlPath, false, null);

        var modDir = Path.GetDirectoryName(Path.GetFullPath(gameInfoPath));
        if (string.IsNullOrEmpty(modDir))
            return new Launch(null, mdlPath, false, null);

        var relative = InternalModelPath(mdlPath);

        // Already where the game filesystem expects it (some search path holds it at its internal
        // name) - the viewer can load it straight out of the game.
        foreach (var root in GameInfoMount.GetGameRoots(gameInfoPath))
        {
            if (SamePath(Path.Combine(root, relative), mdlPath))
                return new Launch(modDir, mdlPath, false, null);
        }

        try
        {
            var stageDir = BuildStage(mdlPath, relative, gameInfoPath, modDir);
            return new Launch(stageDir, Path.Combine(stageDir, relative), true, null);
        }
        catch (Exception ex)
        {
            return new Launch(modDir, mdlPath, false, ex.Message);
        }
    }

    /// <summary>Deletes every fake game folder this session created or adopted. Best-effort: a folder
    /// the viewer still holds open is left, and the next session reuses then removes it.</summary>
    public static void CleanupAll()
    {
        foreach (var dir in Staged)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* still locked by an open viewer - the next session cleans it */ }
        }
        Staged.Clear();
    }

    /// <summary>Creates (or refreshes) the fake game folder and returns its path.</summary>
    private static string BuildStage(string mdlPath, string relative, string gameInfoPath, string modDir)
    {
        // Beside the real mod folder, i.e. in the game root - relative search paths in the copied
        // gameinfo.txt ("hl2", "left4dead2_dlc3", ...) resolve against that root, so they keep working.
        var gameRoot = Path.GetDirectoryName(
            modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? modDir;
        var stageDir = Path.Combine(gameRoot, StageFolderName);
        Directory.CreateDirectory(stageDir);

        WriteGameInfo(stageDir, gameInfoPath, Path.GetFileName(modDir));

        var destMdl = Path.Combine(stageDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destMdl)!);
        CopyModelFiles(mdlPath, destMdl);
        if (!File.Exists(destMdl))
            throw new IOException($"the model could not be copied to {destMdl}");

        if (!Staged.Contains(stageDir, StringComparer.OrdinalIgnoreCase))
            Staged.Add(stageDir);
        return stageDir;
    }

    /// <summary>Writes the fake game's gameinfo.txt: the real one, with the fake folder itself and the
    /// real mod folder pushed to the top of SearchPaths. Everything else (SteamAppId, the DLC and
    /// hl2 search paths) is inherited unchanged.</summary>
    private static void WriteGameInfo(string stageDir, string gameInfoPath, string modDirName)
    {
        var text = File.ReadAllText(gameInfoPath);

        var block = text.IndexOf("SearchPaths", StringComparison.OrdinalIgnoreCase);
        var brace = block < 0 ? -1 : text.IndexOf('{', block);
        if (brace >= 0)
        {
            // "|gameinfo_path|." is the fake folder (it holds the staged model, so it must win);
            // the bare mod folder name resolves against the game root and brings the real content in.
            text = text.Insert(brace + 1,
                $"\r\n\t\t\tGame\t|gameinfo_path|.\r\n\t\t\tGame\t{modDirName}");
        }

        // Only rewrite when it differs, so a reused stage keeps its timestamps.
        var dest = Path.Combine(stageDir, "gameinfo.txt");
        if (!File.Exists(dest) || !string.Equals(File.ReadAllText(dest), text, StringComparison.Ordinal))
            File.WriteAllText(dest, text);
    }

    /// <summary>Copies the model's file set next to <paramref name="destMdl"/>, renaming it to the
    /// destination's stem. Files that are already identical (checksum) are left alone, files no longer
    /// present at the source are removed, and a file the viewer holds open is skipped (a refresh then
    /// reloads the copy that is there).</summary>
    private static void CopyModelFiles(string mdlPath, string destMdl)
    {
        var srcDir = Path.GetDirectoryName(mdlPath)!;
        var srcStem = Path.GetFileNameWithoutExtension(mdlPath);
        var destDir = Path.GetDirectoryName(destMdl)!;
        var destStem = Path.GetFileNameWithoutExtension(destMdl);

        foreach (var suffix in ModelFileSuffixes)
        {
            var src = Path.Combine(srcDir, srcStem + suffix);
            var dest = Path.Combine(destDir, destStem + suffix);
            try
            {
                if (!File.Exists(src))
                {
                    // Stale piece from an earlier compile (e.g. a .phy the model no longer has).
                    if (File.Exists(dest))
                        File.Delete(dest);
                }
                else if (!SameContent(src, dest))
                {
                    File.Copy(src, dest, overwrite: true);
                }
            }
            catch (IOException)
            {
                // Held open by the running viewer - leave the existing copy in place.
            }
        }
    }

    /// <summary>True when both files exist and have identical content (size first, then SHA-256).</summary>
    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
            return false;
        return Hash(a).AsSpan().SequenceEqual(Hash(b));
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>
    /// The model's own path inside the game tree - the name baked into the .mdl header
    /// (e.g. <c>models\props\foo.mdl</c>), which is what the engine's filesystem looks it up by.
    /// Falls back to <c>models\&lt;file stem&gt;.mdl</c> for a header that is missing, unreadable or
    /// not a plausible relative path.
    /// </summary>
    private static string InternalModelPath(string mdlPath)
    {
        var fallback = Path.Combine("models", Path.GetFileName(mdlPath));
        try
        {
            // studiohdr_t: id[4] "IDST", version, checksum, then char name[64].
            using var stream = File.OpenRead(mdlPath);
            Span<byte> head = stackalloc byte[76];
            stream.ReadExactly(head);
            if (!head[..4].SequenceEqual("IDST"u8))
                return fallback;

            var name = Encoding.ASCII.GetString(head[12..76]);
            var end = name.IndexOf('\0');
            name = (end < 0 ? name : name[..end]).Trim().Replace('/', '\\');

            if (name.Length == 0 || Path.IsPathRooted(name) || name.Contains("..")
                || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return fallback;

            if (!name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                name += ".mdl";
            // Some compilers store the name without the leading "models\" the filesystem expects.
            if (!name.StartsWith("models\\", StringComparison.OrdinalIgnoreCase))
                name = Path.Combine("models", name);
            return name;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
