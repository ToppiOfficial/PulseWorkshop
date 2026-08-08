using PulseWorkshop.Core.Mdl;
using PulseWorkshop.Core.Unpack;

namespace PulseWorkshop.Core.Materials;

/// <summary>
/// The families of Source shader the preview knows how to draw. Every VMT names a shader; the ones
/// not in this list render plain white rather than guessing at what they mean.
/// <para>
/// Source's shader set is per-branch, not universal - L4D2's VertexLitGeneric grew parameters HL2's
/// never had, and forks add whole shaders (PBR ones among them). The mapping below is a table on
/// purpose so a branch-specific entry is one line, not a rewrite.
/// </para>
/// </summary>
public enum VmtShader
{
    Unknown,
    VertexLit,
    Unlit,
    Eye,
}

/// <summary>
/// One parsed .vmt: its shader and its parameters, with any <c>Patch</c> already flattened onto the
/// material it includes. Only the parameters the preview actually draws with are interpreted - the
/// rest are kept verbatim in <see cref="Parameters"/> for whatever gets implemented next.
/// </summary>
public sealed class Vmt
{
    /// Shader name exactly as the VMT spells it (after a Patch is resolved).
    public required string ShaderName { get; init; }

    public required VmtShader Shader { get; init; }

    /// Every top-level key/value in the shader block, keys lowercased ("$basetexture" -> "foo/bar").
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public string? this[string parameter] =>
        Parameters.TryGetValue(parameter, out var value) && value.Length > 0 ? value : null;

    // Shader name -> family. Per-branch variants (vertexlitgeneric_l4d2, a fork's PBR shader) get
    // their own entries here once they need behaviour the base family does not have.
    private static readonly Dictionary<string, VmtShader> ShaderFamilies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vertexlitgeneric"] = VmtShader.VertexLit,
            ["unlitgeneric"] = VmtShader.Unlit,
            ["eyes"] = VmtShader.Eye,
            ["eyeball"] = VmtShader.Eye,
            ["eyerefract"] = VmtShader.Eye,
        };

    /// <summary>
    /// The texture the preview samples as diffuse colour, material-relative and without extension,
    /// or null when this shader has none to give.
    /// <para>
    /// Eye shaders carry no $basetexture: EyeRefract builds the eye from $iris behind $corneatexture,
    /// so the iris is the layer that actually reads as the eye. The cornea's refraction and the
    /// per-eye lighting are not implemented - this is the diffuse pass only.
    /// </para>
    /// </summary>
    public string? DiffuseTexture => Shader switch
    {
        VmtShader.Eye => this["$iris"] ?? this["$basetexture"] ?? this["$corneatexture"],
        VmtShader.VertexLit or VmtShader.Unlit => this["$basetexture"],
        _ => null,
    };

    /// Source's convention for a boolean parameter: present and not "0".
    public bool Flag(string parameter) => this[parameter]?.Trim() is { Length: > 0 } v && v != "0";

    /// Alpha below this is discarded; 0 when the material does not alpha-test.
    public float AlphaTestReference =>
        !Flag("$alphatest") ? 0f
        : Number("$alphatestreference") is { } reference and > 0 ? reference
        : 0.5f;

    /// $translucent - blend against what is already drawn, rather than writing over it.
    public bool Translucent => Flag("$translucent");

    /// $additive - add to what is already drawn, so black contributes nothing (glows, eye highlights).
    public bool Additive => Flag("$additive");

    /// $nocull - draw both facings. Without it the engine culls back faces.
    public bool NoCull => Flag("$nocull");

    /// $alpha, a constant opacity multiplier on top of the texture's own alpha; 1 when unset.
    public float Opacity => Number("$alpha") is { } alpha and >= 0f and <= 1f ? alpha : 1f;

    private float? Number(string parameter) =>
        float.TryParse(this[parameter], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Parses a .vmt. <paramref name="readInclude"/> is handed a game-relative path
    /// ("materials/foo/bar.vmt") to resolve a <c>Patch</c>'s include, and may return null.
    /// Returns null when the text holds no shader block at all.
    /// </summary>
    public static Vmt? Parse(string text, Func<string, string?>? readInclude = null, int depth = 0)
    {
        var root = KeyValues.Parse(text);
        var shaderNode = root.Children.FirstOrDefault(n => n.Key.Length > 0);
        if (shaderNode is null)
            return null;

        var children = shaderNode.Children;
        var name = shaderNode.Key;

        // A Patch is not a shader - it is an edit list applied to the material it includes. Depth is
        // capped because nothing stops a pair of VMTs from including each other.
        if (name.Equals("patch", StringComparison.OrdinalIgnoreCase))
        {
            if (depth >= 8 || readInclude is null)
                return null;
            var include = KeyValues.Find(children, "include")?.Value;
            if (string.IsNullOrEmpty(include))
                return null;
            var baseText = readInclude(NormalizeIncludePath(include));
            if (baseText is null || Parse(baseText, readInclude, depth + 1) is not { } patched)
                return null;

            var merged = new Dictionary<string, string>(patched.Parameters, StringComparer.OrdinalIgnoreCase);
            // replace overwrites, insert only fills gaps - the same split gmad/vmt tooling uses.
            foreach (var node in KeyValues.Find(children, "replace")?.Children ?? [])
                merged[node.Key] = node.Value;
            foreach (var node in KeyValues.Find(children, "insert")?.Children ?? [])
                merged.TryAdd(node.Key, node.Value);

            return new Vmt
            {
                ShaderName = patched.ShaderName,
                Shader = patched.Shader,
                Parameters = merged,
            };
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in children)
            if (node.Children.Count == 0)
                parameters[node.Key] = node.Value;

        return new Vmt
        {
            ShaderName = name,
            Shader = ShaderFamilies.GetValueOrDefault(name, VmtShader.Unknown),
            Parameters = parameters,
        };
    }

    // An include is written game-relative and with its extension ("materials/foo/bar.vmt").
    private static string NormalizeIncludePath(string include)
    {
        var path = include.Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
            path = "materials/" + path;
        return path.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) ? path : path + ".vmt";
    }
}

/// What to draw when a material yields no texture of its own.
public enum MaterialFallback
{
    /// The material resolved and <see cref="ModelMaterial.Diffuse"/> is set.
    None,

    /// The VMT or its texture is genuinely absent - draw the engine's missing-texture checkerboard.
    Missing,

    /// The VMT is there but its shader (or its lack of a diffuse map) is not something we draw yet.
    Plain,
}

/// One of a model's materials, resolved as far as the preview can take it.
public sealed class ModelMaterial
{
    /// Material name as the .mdl stores it.
    public required string Name { get; init; }

    /// The game-relative .vmt path this resolved to, or null if none was found.
    public string? Path { get; init; }

    public Vmt? Vmt { get; init; }

    /// Decoded diffuse texture; null unless <see cref="Fallback"/> is <see cref="MaterialFallback.None"/>.
    public VtfImage? Diffuse { get; init; }

    public MaterialFallback Fallback { get; init; }

    /// UnlitGeneric ignores lighting entirely - the texture is the final colour.
    public bool Unlit => Vmt?.Shader == VmtShader.Unlit;

    public float AlphaTestReference => Vmt?.AlphaTestReference ?? 0f;

    public bool Translucent => Vmt?.Translucent ?? false;

    public bool Additive => Vmt?.Additive ?? false;

    /// True for anything that blends, and so has to draw after every opaque part.
    public bool Blended => Translucent || Additive;

    /// Two-sided only when the material says so; anything else is back-face culled like the engine.
    public bool NoCull => Vmt?.NoCull ?? false;

    public float Opacity => Vmt?.Opacity ?? 1f;
}

/// <summary>
/// Resolves a <see cref="StudioMesh"/>'s material names into VMTs and decoded textures, reading
/// through whatever file source the caller supplies (an open VPK/GMA mount, or a loose folder).
/// </summary>
public static class ModelMaterialLoader
{
    /// <summary>
    /// One entry per name in <see cref="StudioMesh.MaterialNames"/>, in the same order. Never throws
    /// and never returns null entries: a material that cannot be found comes back with a
    /// <see cref="MaterialFallback"/> saying what the renderer should draw instead.
    /// </summary>
    /// <param name="read">Game-relative path ("materials/foo/bar.vmt") -> bytes, or null if absent.</param>
    /// <param name="maxTextureSize">Largest mip the decoder is asked for; bigger textures decode at a
    /// lower mip, which is plenty for a preview pane and keeps the upload cheap.</param>
    public static ModelMaterial[] Resolve(StudioMesh mesh, Func<string, byte[]?> read,
        Action<string>? log = null, int maxTextureSize = 1024)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(read);

        var materials = new ModelMaterial[mesh.MaterialNames.Length];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = ResolveOne(mesh.MaterialNames[i], mesh.MaterialDirs, read, log, maxTextureSize);
        return materials;
    }

    private static ModelMaterial ResolveOne(string name, string[] searchDirs, Func<string, byte[]?> read,
        Action<string>? log, int maxTextureSize)
    {
        string? path = null;
        byte[]? vmtBytes = null;
        foreach (var candidate in CandidatePaths(name, searchDirs))
        {
            vmtBytes = read(candidate);
            if (vmtBytes is not null)
            {
                path = candidate;
                break;
            }
        }

        if (vmtBytes is null)
        {
            log?.Invoke($"material: {name} - no .vmt found in {searchDirs.Length} $cdmaterials path(s)");
            return new ModelMaterial { Name = name, Fallback = MaterialFallback.Missing };
        }

        var vmt = Vmt.Parse(Text(vmtBytes), include => read(include) is { } bytes ? Text(bytes) : null);
        if (vmt is null)
        {
            log?.Invoke($"material: {path} did not parse as a .vmt");
            return new ModelMaterial { Name = name, Path = path, Fallback = MaterialFallback.Missing };
        }

        if (vmt.DiffuseTexture is not { } texture)
        {
            log?.Invoke(vmt.Shader == VmtShader.Unknown
                ? $"material: {name} uses {vmt.ShaderName}, which the preview does not draw yet"
                : $"material: {name} ({vmt.ShaderName}) names no diffuse texture");
            return new ModelMaterial { Name = name, Path = path, Vmt = vmt, Fallback = MaterialFallback.Plain };
        }

        var texturePath = "materials/" + texture.Replace('\\', '/').TrimStart('/');
        if (!texturePath.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
            texturePath += ".vtf";

        if (read(texturePath) is not { } vtfBytes)
        {
            log?.Invoke($"material: {name} - {texturePath} is missing");
            return new ModelMaterial { Name = name, Path = path, Vmt = vmt, Fallback = MaterialFallback.Missing };
        }

        if (VtfImage.Decode(vtfBytes, maxTextureSize) is not { } image)
        {
            log?.Invoke($"material: {name} - {texturePath} is in a .vtf format the decoder does not handle");
            return new ModelMaterial { Name = name, Path = path, Vmt = vmt, Fallback = MaterialFallback.Missing };
        }

        log?.Invoke($"material: {name} -> {vmt.ShaderName}, {texture} ({image.Width}x{image.Height})");
        return new ModelMaterial { Name = name, Path = path, Vmt = vmt, Diffuse = image };
    }

    /// <summary>
    /// Where a material name might live, best guess first: as a full path when it already carries
    /// one, then under each $cdmaterials directory in the order the model lists them (which is the
    /// order the engine searches), then bare at the materials root.
    /// </summary>
    private static IEnumerable<string> CandidatePaths(string name, string[] searchDirs)
    {
        var clean = name.Replace('\\', '/').TrimStart('/');
        if (clean.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^4];
        if (clean.Length == 0)
            yield break;

        if (clean.Contains('/'))
            yield return "materials/" + clean + ".vmt";

        foreach (var dir in searchDirs)
        {
            var prefix = dir.Replace('\\', '/').Trim('/');
            if (prefix.Length > 0)
                yield return "materials/" + prefix + "/" + clean + ".vmt";
        }

        yield return "materials/" + clean + ".vmt";
    }

    // VMTs are ASCII in practice but ship with the odd UTF-8 BOM, which would become part of the
    // first key and stop the shader name matching.
    private static string Text(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
}
