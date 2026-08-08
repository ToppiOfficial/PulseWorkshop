using System.Buffers.Binary;
using System.Numerics;

namespace PulseWorkshop.Core.Mdl;

/// <summary>
/// A static render mesh lifted out of a compiled Source model - just LOD 0 positions, normals and
/// triangle indices, which is all the Unpack tab's preview draws, plus the bind-pose skeleton for the
/// preview's bone overlay. No skinning, no animation, no flexes: the vertices are taken in their bind
/// pose exactly as the .vvd stores them, and the bones are only ever drawn, never applied.
/// </summary>
public sealed class StudioMesh
{
    /// <summary>Bind-pose vertex positions, in Source units (Z up).</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Per-vertex normals, parallel to <see cref="Positions"/>.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Per-vertex UVs, parallel to <see cref="Positions"/>. Source stores V already
    /// flipped for OpenGL-style sampling, so these are used as-is.</summary>
    public required Vector2[] TexCoords { get; init; }

    /// <summary>Triangle list - three indices into <see cref="Positions"/> per triangle.</summary>
    public required int[] Indices { get; init; }

    /// <summary>Contiguous slices of <see cref="Indices"/>, one per mesh, each naming the material
    /// it draws with. Covers the whole index array in order.</summary>
    public required MeshPart[] Parts { get; init; }

    /// <summary>Material names as the .mdl stores them (mstudiotexture_t), indexed by
    /// <see cref="MeshPart.MaterialIndex"/>. No directory, no extension - usually.</summary>
    public required string[] MaterialNames { get; init; }

    /// <summary>The model's $cdmaterials search directories, relative to <c>materials/</c>.</summary>
    public required string[] MaterialDirs { get; init; }

    /// <summary>The model's bodygroups, indexed by <see cref="MeshPart.BodyPart"/>. A model with no
    /// $bodygroup still has one entry per $body.</summary>
    public required StudioBodyPart[] BodyParts { get; init; }

    /// <summary>$texturegroup: the skin families, each mapping a <see cref="MeshPart.SkinRef"/> to an
    /// index into <see cref="MaterialNames"/>. Never empty - family 0 is the default skin.</summary>
    public required int[][] SkinFamilies { get; init; }

    /// <summary>The bind-pose skeleton, in the .mdl's own bone order (parents always precede their
    /// children). Empty for a model with no bones at all.</summary>
    public required StudioBone[] Bones { get; init; }

    /// <summary>
    /// Which material a part draws with under a given skin, or -1 when it has none. This indirection
    /// is the whole point of $texturegroup: swapping skins re-points the same geometry at a different
    /// row of the table without touching a vertex.
    /// </summary>
    public int MaterialFor(in MeshPart part, int skin)
    {
        if (part.SkinRef < 0)
            return -1;
        var family = SkinFamilies[Math.Clamp(skin, 0, SkinFamilies.Length - 1)];
        int index = part.SkinRef < family.Length ? family[part.SkinRef] : part.SkinRef;
        return index >= 0 && index < MaterialNames.Length ? index : -1;
    }

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    /// <summary>studiohdr_t.version of the source .mdl (44-49).</summary>
    public required int Version { get; init; }

    public int TriangleCount => Indices.Length / 3;
}

/// <summary>One draw range: <paramref name="IndexCount"/> indices starting at
/// <paramref name="FirstIndex"/>. <paramref name="SkinRef"/> is resolved to a material through the
/// active skin (see <see cref="StudioMesh.MaterialFor"/>), not used directly. Only visible when its
/// bodygroup has <paramref name="Model"/> selected.</summary>
public readonly record struct MeshPart(
    int FirstIndex, int IndexCount, int SkinRef, int BodyPart, int Model);

/// <summary>
/// One bone of the bind-pose skeleton (mstudiobone_t). <paramref name="BindPose"/> is the bone -> model
/// space transform - the inverse of the <c>poseToBone</c> the compiler wrote - so
/// <see cref="Position"/> is where the joint sits with no animation applied.
/// <para>
/// TODO (animation): <paramref name="LocalPosition"/>/<paramref name="LocalRotation"/> are the bone's
/// rest transform relative to its parent, which is the frame animation data is stored against. A
/// future animated pose composes those down the parent chain with the $sequence deltas and multiplies
/// each result by the bone's <c>poseToBone</c> to skin the mesh; nothing here does that yet, and the
/// vertices are drawn exactly as the .vvd stores them.
/// </para>
/// </summary>
public sealed record StudioBone(
    string Name, int Parent, Matrix4x4 BindPose, Vector3 LocalPosition, Quaternion LocalRotation)
{
    /// <summary>The joint's position in model space at bind pose, in Source units.</summary>
    public Vector3 Position => BindPose.Translation;
}

/// <summary>
/// One bodygroup: a set of interchangeable sub-models of which the engine draws exactly one at a
/// time (heads, weapon attachments, a "blank" that draws nothing). A model with a single sub-model
/// is always drawn - it is a $body, not a real choice.
/// </summary>
public sealed record StudioBodyPart(string Name, string[] Models)
{
    public bool IsSelectable => Models.Length > 1;
}

/// <summary>
/// Reader for the render mesh of a Source-engine studiomodel: the .mdl gives the bodypart -> model
/// -> mesh tree, the .vvd the vertices, the .dx90.vtx the triangle indices. All three are needed;
/// the .mdl alone carries no geometry at all.
/// <para>
/// Ported from PulseModel's decompiler (MIT, same author) - see its <c>libs/format/{mdl,vvd,vtx}.h</c>
/// for the on-disk structs the offsets below come from. Versions 44-49 (HL2 through L4D2 / Portal 2)
/// share this layout; GoldSrc "IDST" models and Source 2 are different formats entirely and are
/// rejected.
/// </para>
/// </summary>
public static class StudioMeshReader
{
    public const int MinVersion = 44;
    public const int MaxVersion = 49;

    // studiohdr_t field offsets (the struct is 408 bytes; only these few are needed here).
    private const int HdrId = 0, HdrVersion = 4, HdrChecksum = 8, HdrNumBodyParts = 232, HdrBodyPartIndex = 236;

    // ... and the material tables: the texture list, the $cdmaterials list, and the skin table that
    // maps a mesh's material field to a texture index.
    private const int HdrNumTextures = 204, HdrTextureIndex = 208, HdrNumCdTextures = 212,
        HdrCdTextureIndex = 216, HdrNumSkinRef = 220, HdrNumSkinFamilies = 224, HdrSkinIndex = 228;

    // mstudiotexture_t (64): sznameindex(0, relative to the struct) flags(4) used(8) ...
    private const int TextureSize = 64;

    // mstudiobodyparts_t (16): sznameindex(0) nummodels(4) base(8) modelindex(12)
    private const int BodyPartSize = 16;

    // mstudiomodel_t (148): name[64](0) type(64) boundingradius(68) nummeshes(72) meshindex(76) ...
    private const int ModelSize = 148;

    // mstudiomesh_t (116): material(0) modelindex(4) numvertices(8) vertexoffset(12) numflexes(16)
    //   flexindex(20) materialtype(24) materialparam(28) meshid(32) center(36) then the 44-byte
    //   mstudio_meshvertexdata_t at 48, whose numLODVertexes[8] starts at 52.
    // materialtype 1 marks an eyeball mesh, and materialparam is then its index into the model's
    // eyeball table.
    private const int MeshSize = 116, MeshMaterialType = 24, MeshMaterialParam = 28, MeshNumLodVertexes = 52;

    // mstudiomodel_t's eyeball table: numeyeballs(100) eyeballindex(104), the offset relative to the
    // mstudiomodel_t itself.
    private const int ModelNumEyeballs = 100, ModelEyeballIndex = 104;

    // mstudioeyeball_t (172): sznameindex(0) bone(4) org(8) zoffset(20) radius(24) up(28)
    //   forward(40) texture(52) unused1(56) iris_scale(60) ...
    private const int EyeballSize = 172, EyeballBone = 4, EyeballOrg = 8, EyeballRadius = 24,
        EyeballUp = 28, EyeballForward = 40, EyeballIrisScale = 60;

    // mstudiobone_t (216): sznameindex(0) parent(4) bonecontroller[6](8) pos(32) quat(44) rot(60)
    //   posscale(72) rotscale(84) poseToBone(96, a 3x4 model -> bone matrix) qAlignment(144) flags(160) ...
    private const int BoneSize = 216, BoneParent = 4, BonePos = 32, BoneQuat = 44, BonePoseToBone = 96;

    // studiohdr_t.numbones / .boneindex.
    private const int HdrNumBones = 156, HdrBoneIndex = 160;

    /// <summary>
    /// Shrinks the projected iris by a few percent. Flat-projecting the eyeball mesh only
    /// approximates EyeRefract, which raytraces a sphere and refracts through the cornea, so the
    /// straight geometric mapping lands a little large against what the engine draws.
    /// <para>
    /// Calibrated by eye against HLMV - raise it to shrink the iris, lower it to grow it. This is the
    /// knob to turn if eyes ever look off; the projection maths above is not.
    /// </para>
    /// </summary>
    private const float IrisProjectionCorrection = 1.07f;

    // vertexFileHeader_t (64): id(0) version(4) checksum(8) numLODs(12) numLODVertexes[8](16)
    //   numFixups(48) fixupTableStart(52) vertexDataStart(56) tangentDataStart(60)
    private const int VvdNumLodVertexes = 16, VvdNumFixups = 48, VvdFixupStart = 52, VvdVertexStart = 56;

    // mstudiovertex_t (48): boneweights(0,16) position(16) normal(28) texcoord(40)
    private const int VertexSize = 48, VertexPos = 16, VertexNormal = 28, VertexUv = 40;
    private const int FixupSize = 12; // lod(0) sourceVertexID(4) numVertexes(8)

    // vtx FileHeader_t (36): version(0) ... checkSum(20) numLODs(24) matReplListOffset(28)
    //   numBodyParts(32) bodyPartOffset(36)? - no: version(0) vertCacheSize(4) maxBonesPerStrip(8,u16)
    //   maxBonesPerFace(10,u16) maxBonesPerVert(12) checkSum(16) numLODs(20)
    //   materialReplacementListOffset(24) numBodyParts(28) bodyPartOffset(32)
    private const int VtxVersion = 0, VtxChecksum = 16, VtxNumLods = 20, VtxNumBodyParts = 28, VtxBodyPartOffset = 32;
    private const int VtxBodyPartSize = 8;  // numModels(0) modelOffset(4)
    private const int VtxModelSize = 8;     // numLODs(0) lodOffset(4)
    private const int VtxLodSize = 12;      // numMeshes(0) meshOffset(4) switchPoint(8)
    private const int VtxMeshSize = 9;      // numStripGroups(0) stripGroupHeaderOffset(4) flags(8)
    private const int VtxStripVertexSize = 9; // boneWeightIndex[3] numBones origMeshVertID(u16 @4) boneID[3]

    /// <summary>The two strip-group strides that exist under .vtx version 7: the legacy 25-byte
    /// layout (TF2 / L4D2 era) and the 33-byte one that grew topology fields in the Alien Swarm era
    /// without a version bump. Nothing in the file says which, so both are tried.</summary>
    private static readonly int[] StripGroupStrides = [25, 33];

    /// <summary>The .vtx variants to look for beside a .mdl, best first. They hold the same geometry
    /// for different (mostly dead) renderers.</summary>
    public static readonly string[] VtxSuffixes = [".dx90.vtx", ".dx80.vtx", ".vtx", ".sw.vtx"];

    /// <summary>
    /// Builds the LOD-0 render mesh from the three files' raw bytes. Throws
    /// <see cref="InvalidDataException"/> with a reason the caller can log and show; callers treat any
    /// failure as "no preview" rather than an error worth interrupting the user for.
    /// </summary>
    /// <param name="log">Optional verbose sink - one line per parse step, for the shared console.</param>
    public static StudioMesh Read(byte[] mdl, byte[] vvd, byte[] vtx, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(vvd);
        ArgumentNullException.ThrowIfNull(vtx);

        // --- .mdl header ---------------------------------------------------------------------
        if (mdl.Length < 408)
            throw new InvalidDataException("the .mdl is too small to hold a studiohdr_t");
        if (I32(mdl, HdrId) != 0x54534449) // "IDST"
            throw new InvalidDataException("not a studiomodel (no IDST magic)");

        int version = I32(mdl, HdrVersion);
        if (version is < MinVersion or > MaxVersion)
            throw new InvalidDataException(
                $"studiomodel version {version} is not supported (only {MinVersion}-{MaxVersion}; "
                + (version < MinVersion ? "GoldSrc / early Source" : "newer than Portal 2") + ")");

        int checksum = I32(mdl, HdrChecksum);
        int numBodyParts = I32(mdl, HdrNumBodyParts);
        int bodyPartIndex = I32(mdl, HdrBodyPartIndex);
        log?.Invoke($"mdl: version {version}, checksum 0x{checksum:X8}, {numBodyParts} bodypart(s)");
        if (numBodyParts <= 0)
            throw new InvalidDataException("the model has no body parts (nothing to draw)");

        // --- .vvd vertices (LOD 0, fixups applied) --------------------------------------------
        var (positions, normals, texCoords) = ReadVvdLod0(vvd, checksum, log);

        // --- materials, so each mesh knows which VMT to draw with -------------------------------
        var (materialNames, materialDirs) = ReadMaterials(mdl, log);
        var skinFamilies = ReadSkinFamilies(mdl, materialNames.Length, log);

        // --- the bind-pose skeleton, which the eyeball projection below also needs ---------------
        var bones = ReadBones(mdl, log);

        // --- the .mdl's mesh tree, which says where each mesh's vertices sit in that array -----
        var layout = BuildLod0Layout(mdl, numBodyParts, bodyPartIndex, out var bodyParts, out var eyeballs);
        ApplyEyeballUvs(mdl, bones, positions, texCoords, eyeballs, log);
        log?.Invoke($"mdl: {layout.Count} mesh(es), {layout.Sum(m => m.Count)} LOD-0 vertices expected");
        if (bodyParts.Any(b => b.IsSelectable))
            log?.Invoke("mdl: bodygroups - "
                        + string.Join(", ", bodyParts.Where(b => b.IsSelectable)
                            .Select(b => $"{b.Name} ({b.Models.Length})")));

        // --- .vtx indices ----------------------------------------------------------------------
        // Two passes. The stride only matters for a mesh's second and later strip groups - group 0
        // sits at the same offset either way - so a wrong stride corrupts just those meshes, which
        // reads as a few scrambled patches rather than an obviously broken model. The first pass
        // therefore demands every strip group look like a triangle list; the second takes what fits.
        int[]? indices = null;
        MeshPart[]? parts = null;
        int chosenStride = 0;
        for (int pass = 0; pass < 2 && indices is null; pass++)
        {
            bool strict = pass == 0;
            int hits = 0;
            foreach (int stride in StripGroupStrides)
            {
                bool ok = TryReadVtxLod0(vtx, checksum, numBodyParts, mdl, bodyPartIndex, layout, stride,
                    positions.Length, strict, log, out var got, out var gotParts);
                if (strict)
                    log?.Invoke($"vtx: {stride}-byte strip groups {(ok ? "look" : "do not look")} like triangle lists");
                if (!ok)
                    continue;
                if (++hits == 1)
                {
                    indices = got;
                    parts = gotParts;
                    chosenStride = stride;
                }
            }
            // Both strides agreeing means the test did not discriminate - retry on bounds alone.
            if (strict && hits != 1)
            {
                indices = null;
                parts = null;
            }
        }
        if (indices is null || parts is null)
            throw new InvalidDataException("the .vtx does not parse as either strip-group layout");
        log?.Invoke($"vtx: parsed with the {(chosenStride == 25 ? "legacy" : "Alien Swarm")} "
                    + $"{chosenStride}-byte strip-group layout - {indices.Length / 3} triangle(s)");
        if (indices.Length < 3)
            throw new InvalidDataException("the .vtx yielded no triangles for LOD 0");

        // Bounds over the vertices the triangles actually reference - a .vvd routinely carries
        // vertices no LOD-0 triangle uses, and framing the camera on those would zoom out for nothing.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int i in indices)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }

        return new StudioMesh
        {
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = indices,
            Parts = parts,
            MaterialNames = materialNames,
            MaterialDirs = materialDirs,
            BodyParts = bodyParts,
            SkinFamilies = skinFamilies,
            Bones = bones,
            BoundsMin = min,
            BoundsMax = max,
            Version = version,
        };
    }

    // --- .mdl material tables -------------------------------------------------------------------

    /// <summary>
    /// The model's texture names and its $cdmaterials search directories. Both are string tables the
    /// header points at; a texture name is usually bare ("hunter_body") and only becomes a path once
    /// a cdmaterials entry is prefixed, but DMX-authored models sometimes store the full path.
    /// </summary>
    private static (string[] Names, string[] Dirs) ReadMaterials(byte[] mdl, Action<string>? log)
    {
        var names = new List<string>();
        int numTextures = I32(mdl, HdrNumTextures), textureIndex = I32(mdl, HdrTextureIndex);
        for (int i = 0; i < numTextures; i++)
        {
            int at = textureIndex + i * TextureSize;
            if (!Fits(mdl, at, TextureSize))
                break;
            // sznameindex is relative to the mstudiotexture_t, not the file.
            names.Add(Str(mdl, at + I32(mdl, at)));
        }

        var dirs = new List<string>();
        int numCd = I32(mdl, HdrNumCdTextures), cdIndex = I32(mdl, HdrCdTextureIndex);
        for (int i = 0; i < numCd; i++)
        {
            int at = cdIndex + i * 4;
            if (!Fits(mdl, at, 4))
                break;
            var dir = Str(mdl, I32(mdl, at)); // this one IS a file-absolute offset
            if (dir.Length > 0)
                dirs.Add(dir);
        }

        log?.Invoke($"mdl: {names.Count} material(s) over {dirs.Count} $cdmaterials path(s)");
        return (names.ToArray(), dirs.ToArray());
    }

    /// <summary>
    /// The $texturegroup table: numskinfamilies rows of numskinref entries, each an index into the
    /// texture list. Falls back to a single identity row when the model has no table, so callers
    /// never have to special-case its absence.
    /// </summary>
    private static int[][] ReadSkinFamilies(byte[] mdl, int materialCount, Action<string>? log)
    {
        int numRef = I32(mdl, HdrNumSkinRef), families = I32(mdl, HdrNumSkinFamilies),
            at = I32(mdl, HdrSkinIndex);
        // Guarded as a long: a corrupt header could overflow the byte count on its own.
        long bytes = (long)numRef * families * 2;
        if (numRef <= 0 || families <= 0 || bytes > int.MaxValue || !Fits(mdl, at, (int)bytes))
            return [Identity(materialCount)];

        var table = new int[families][];
        for (int f = 0; f < families; f++)
        {
            table[f] = new int[numRef];
            for (int i = 0; i < numRef; i++)
                table[f][i] = U16(mdl, at + (f * numRef + i) * 2);
        }
        if (families > 1)
            log?.Invoke($"mdl: {families} skin(s) over {numRef} skin reference(s)");
        return table;

        static int[] Identity(int count)
        {
            var row = new int[Math.Max(count, 1)];
            for (int i = 0; i < row.Length; i++)
                row[i] = i;
            return row;
        }
    }

    // --- .mdl skeleton --------------------------------------------------------------------------

    /// <summary>
    /// The bind-pose skeleton. The bone -> model transform is taken as the inverse of the compiler's
    /// <c>poseToBone</c> rather than by composing each bone's local rest transform down the parent
    /// chain: poseToBone already <em>is</em> that composition, inverted, so reading it needs no
    /// assumption about bone ordering and cannot drift from what the mesh was skinned against.
    /// </summary>
    private static StudioBone[] ReadBones(byte[] mdl, Action<string>? log)
    {
        int count = I32(mdl, HdrNumBones), first = I32(mdl, HdrBoneIndex);
        if (count <= 0)
            return [];

        var bones = new List<StudioBone>(count);
        for (int i = 0; i < count; i++)
        {
            int at = first + i * BoneSize;
            if (!Fits(mdl, at, BoneSize))
                break;

            // poseToBone maps model space -> bone space as (R * v + t), with R stored row by row.
            // Its inverse applies R^T to (v - t), and a row-vector matrix whose rows are R's rows
            // *is* R^T, so the three rows go straight in and the translation is -R^T t.
            int poseAt = at + BonePoseToBone;
            var r0 = Vec3(mdl, poseAt);
            var r1 = Vec3(mdl, poseAt + 16);
            var r2 = Vec3(mdl, poseAt + 32);
            var t = new Vector3(F32(mdl, poseAt + 12), F32(mdl, poseAt + 28), F32(mdl, poseAt + 44));
            var bindPose = new Matrix4x4(
                r0.X, r0.Y, r0.Z, 0f,
                r1.X, r1.Y, r1.Z, 0f,
                r2.X, r2.Y, r2.Z, 0f,
                0f, 0f, 0f, 1f)
            {
                Translation = -(t.X * r0 + t.Y * r1 + t.Z * r2),
            };

            // sznameindex is relative to the mstudiobone_t, as everywhere else in the format.
            bones.Add(new StudioBone(Str(mdl, at + I32(mdl, at)), I32(mdl, at + BoneParent), bindPose,
                Vec3(mdl, at + BonePos), Quat(mdl, at + BoneQuat)));
        }

        log?.Invoke($"mdl: {bones.Count} bone(s)");
        return bones.ToArray();
    }

    /// <summary>Null-terminated ASCII at an absolute file offset, or "" if it runs off the end.</summary>
    private static string Str(byte[] b, int at)
    {
        if (at <= 0 || at >= b.Length)
            return string.Empty;
        int end = at;
        while (end < b.Length && b[end] != 0)
            end++;
        return System.Text.Encoding.ASCII.GetString(b, at, end - at);
    }

    // --- .vvd ---------------------------------------------------------------------------------

    /// <summary>
    /// The LOD-0 vertex array. When the file has a fixup table the vertices on disk are in compile
    /// order and every run whose lod reaches 0 is concatenated to rebuild the order the .mdl's mesh
    /// offsets assume; with no fixups the block is already in that order.
    /// </summary>
    private static (Vector3[] Positions, Vector3[] Normals, Vector2[] TexCoords) ReadVvdLod0(
        byte[] vvd, int mdlChecksum, Action<string>? log)
    {
        if (vvd.Length < 64)
            throw new InvalidDataException("the .vvd is too small to hold its header");
        if (I32(vvd, 0) != 0x56534449) // "IDSV"
            throw new InvalidDataException("the .vvd has no IDSV magic");
        if (I32(vvd, 4) != 4)
            throw new InvalidDataException($"unsupported .vvd version {I32(vvd, 4)} (expected 4)");
        if (I32(vvd, 8) != mdlChecksum)
            throw new InvalidDataException("the .vvd checksum does not match the .mdl (mismatched files)");

        int vertexStart = I32(vvd, VvdVertexStart);
        int numFixups = I32(vvd, VvdNumFixups);
        int fixupStart = I32(vvd, VvdFixupStart);
        int lod0Count = I32(vvd, VvdNumLodVertexes);
        if (vertexStart < 0 || vertexStart > vvd.Length)
            throw new InvalidDataException("the .vvd vertex block starts outside the file");
        int available = (vvd.Length - vertexStart) / VertexSize;
        log?.Invoke($"vvd: {available} vertex slot(s) on disk, {lod0Count} at LOD 0, {numFixups} fixup(s)");

        var positions = new List<Vector3>(lod0Count > 0 ? lod0Count : available);
        var normals = new List<Vector3>(positions.Capacity);
        var texCoords = new List<Vector2>(positions.Capacity);

        void Take(int first, int count)
        {
            for (int i = first; i < first + count; i++)
            {
                int at = vertexStart + i * VertexSize;
                positions.Add(Vec3(vvd, at + VertexPos));
                normals.Add(Vec3(vvd, at + VertexNormal));
                texCoords.Add(new Vector2(
                    BitConverter.ToSingle(vvd, at + VertexUv), BitConverter.ToSingle(vvd, at + VertexUv + 4)));
            }
        }

        if (numFixups <= 0)
        {
            Take(0, lod0Count > 0 && lod0Count < available ? lod0Count : available);
        }
        else
        {
            if (fixupStart < 0 || (long)fixupStart + (long)numFixups * FixupSize > vvd.Length)
                throw new InvalidDataException("the .vvd fixup table runs past the end of the file");
            for (int i = 0; i < numFixups; i++)
            {
                int at = fixupStart + i * FixupSize;
                int lod = I32(vvd, at), source = I32(vvd, at + 4), count = I32(vvd, at + 8);
                if (lod < 0 || source < 0 || count < 0 || source + count > available)
                    throw new InvalidDataException("a .vvd fixup run points outside the vertex block");
                // A run is kept when its lod reaches the one being built; for LOD 0 that is all of them.
                Take(source, count);
            }
        }

        if (positions.Count == 0)
            throw new InvalidDataException("the .vvd holds no LOD-0 vertices");
        return (positions.ToArray(), normals.ToArray(), texCoords.ToArray());
    }

    // --- .mdl mesh tree -------------------------------------------------------------------------

    /// <summary>Where one mesh's LOD-0 vertices sit in the rebuilt .vvd array, its skin reference, and
    /// the bodygroup slot it belongs to. The .vtx stores mesh-relative vertex ids, so
    /// each mesh needs its base offset to index the flat array.</summary>
    private readonly record struct MeshSlot(int Base, int Count, int SkinRef, int BodyPart, int Model);

    /// <summary>
    /// Walks bodypart -> model -> mesh in file order, accumulating each mesh's LOD-0 vertex count
    /// (mstudiomesh_t.vertexdata.numLODVertexes[0]) into a running base offset. That walk order is
    /// the order the .vvd fixup table was written in, which is why the offsets are re-derived here
    /// rather than read off any single field.
    /// </summary>
    /// <summary>An eyeball mesh: which vertices it owns, and where its mstudioeyeball_t sits.</summary>
    private readonly record struct EyeballMesh(int VertexBase, int VertexCount, int EyeballAt);

    private static List<MeshSlot> BuildLod0Layout(byte[] mdl, int numBodyParts, int bodyPartIndex,
        out StudioBodyPart[] bodyParts, out List<EyeballMesh> eyeballs)
    {
        var slots = new List<MeshSlot>();
        var groups = new List<StudioBodyPart>(numBodyParts);
        eyeballs = [];
        int running = 0;
        for (int bp = 0; bp < numBodyParts; bp++)
        {
            int bpAt = bodyPartIndex + bp * BodyPartSize;
            Require(mdl, bpAt, BodyPartSize, "body part");
            int numModels = I32(mdl, bpAt + 4);
            int modelIndex = I32(mdl, bpAt + 12);
            var modelNames = new string[Math.Max(numModels, 0)];
            groups.Add(new StudioBodyPart(
                Str(mdl, bpAt + I32(mdl, bpAt)) is { Length: > 0 } n ? n : $"bodygroup {bp}", modelNames));

            for (int m = 0; m < numModels; m++)
            {
                int modelAt = bpAt + modelIndex + m * ModelSize;
                Require(mdl, modelAt, ModelSize, "model");
                int numMeshes = I32(mdl, modelAt + 72);
                int meshIndex = I32(mdl, modelAt + 76);

                // mstudiomodel_t.name is an inline char[64], not a string-table offset. A "blank"
                // sub-model - the one that draws nothing - is usually unnamed.
                modelNames[m] = Fixed(mdl, modelAt, 64) is { Length: > 0 } name ? name : "blank";

                for (int k = 0; k < numMeshes; k++)
                {
                    int meshAt = modelAt + meshIndex + k * MeshSize;
                    Require(mdl, meshAt, MeshSize, "mesh");
                    int count = I32(mdl, meshAt + MeshNumLodVertexes); // numLODVertexes[0]
                    if (count < 0)
                        count = 0;

                    // mstudiomesh_t.material is a skin reference, resolved against the active skin
                    // family later - not an index into the texture list.
                    int skinRef = I32(mdl, meshAt);

                    // An eyeball mesh's stored UVs are meaningless - the engine projects the iris onto
                    // it at runtime - so note it here and rebuild them once the vertices are read.
                    if (I32(mdl, meshAt + MeshMaterialType) == 1)
                    {
                        int eyeball = I32(mdl, meshAt + MeshMaterialParam);
                        int eyeballAt = modelAt + I32(mdl, modelAt + ModelEyeballIndex) + eyeball * EyeballSize;
                        if (eyeball >= 0 && eyeball < I32(mdl, modelAt + ModelNumEyeballs)
                            && Fits(mdl, eyeballAt, EyeballSize))
                            eyeballs.Add(new EyeballMesh(running, count, eyeballAt));
                    }

                    slots.Add(new MeshSlot(running, count, skinRef, bp, m));
                    running += count;
                }
            }
        }
        bodyParts = groups.ToArray();
        return slots;
    }

    /// <summary>
    /// Rebuilds the UVs of every eyeball mesh by projecting its vertices onto the eye's own right/up
    /// axes, which is what the engine does per frame instead of trusting the .vvd. Without this an
    /// eyeball samples one arbitrary speck of its iris texture and renders as a flat blank disc.
    /// <para>
    /// The eyeball's origin and axes are stored in its bone's space, so they are lifted into model
    /// space through the inverse of that bone's <c>poseToBone</c>. The eye is drawn in bind pose
    /// looking straight ahead - it does not track a target the way the engine's does.
    /// </para>
    /// </summary>
    private static void ApplyEyeballUvs(byte[] mdl, StudioBone[] bones, Vector3[] positions,
        Vector2[] texCoords, List<EyeballMesh> eyeballs, Action<string>? log)
    {
        foreach (var (vertexBase, vertexCount, at) in eyeballs)
        {
            int bone = I32(mdl, at + EyeballBone);
            float radius = F32(mdl, at + EyeballRadius);
            float irisScale = F32(mdl, at + EyeballIrisScale);
            if (bone < 0 || bone >= bones.Length || radius <= 0f || irisScale <= 0f)
                continue;

            // The eyeball's origin and axes are stored in its bone's space; the bone's bind pose is
            // exactly the transform back into model space (directions skip the translation).
            var toModel = bones[bone].BindPose;
            var origin = Vector3.Transform(Vec3(mdl, at + EyeballOrg), toModel);
            var up = Vector3.TransformNormal(Vec3(mdl, at + EyeballUp), toModel);
            var forward = Vector3.TransformNormal(Vec3(mdl, at + EyeballForward), toModel);
            if (up.LengthSquared() < 1e-8f || forward.LengthSquared() < 1e-8f)
                continue;
            up = Vector3.Normalize(up);
            forward = Vector3.Normalize(forward);
            var right = Vector3.Cross(forward, up);
            if (right.LengthSquared() < 1e-8f)
                continue;
            right = Vector3.Normalize(right);

            // The iris disc's half-size is radius/(2 * iris_scale), and the eyeball mesh is that
            // disc, so 0.5/that maps it across the whole 0..1 texture. Checked against a GFL2 port:
            // predicted half-size 0.774 against a measured mesh half-extent of 0.80.
            // Halving this scale is the classic mistake - it samples the middle of the iris only and
            // the eye renders at double size. V runs down the image, hence the negation below.
            float scale = irisScale / radius * IrisProjectionCorrection;
            int last = Math.Min(vertexBase + vertexCount, positions.Length);
            for (int i = vertexBase; i < last; i++)
            {
                var d = positions[i] - origin;
                texCoords[i] = new Vector2(
                    Vector3.Dot(d, right) * scale + 0.5f,
                    0.5f - Vector3.Dot(d, up) * scale);
            }
        }

        if (eyeballs.Count > 0)
            log?.Invoke($"mdl: projected iris UVs for {eyeballs.Count} eyeball mesh(es)");
    }

    private static float F32(byte[] b, int at) => BitConverter.ToSingle(b, at);

    /// <summary>A fixed-width, null-padded ASCII field (mstudiomodel_t.name and friends).</summary>
    private static string Fixed(byte[] b, int at, int length)
    {
        if (!Fits(b, at, length))
            return string.Empty;
        int end = at;
        while (end < at + length && b[end] != 0)
            end++;
        return System.Text.Encoding.ASCII.GetString(b, at, end - at);
    }

    // --- .vtx ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads LOD 0's triangle list under one candidate strip-group stride. Returns false (rather than
    /// throwing) on any out-of-bounds read, which is how the caller tells the two layouts apart: the
    /// wrong stride reliably walks off the rails within the first mesh.
    /// </summary>
    private static bool TryReadVtxLod0(byte[] vtx, int mdlChecksum, int numBodyParts, byte[] mdl,
        int bodyPartIndex, List<MeshSlot> layout, int stripGroupStride, int vertexCount,
        bool strictTriangleLists, Action<string>? log, out int[] indices, out MeshPart[] parts)
    {
        indices = [];
        parts = [];
        if (vtx.Length < 36)
            return false;
        if (I32(vtx, VtxVersion) != 7 || I32(vtx, VtxNumBodyParts) != numBodyParts)
            return false;
        if (I32(vtx, VtxChecksum) != mdlChecksum)
        {
            // Worth saying out loud once - a mismatched .vtx is a packaging bug, not a parse failure.
            if (stripGroupStride == StripGroupStrides[0])
                log?.Invoke("vtx: checksum does not match the .mdl (mismatched files)");
            return false;
        }

        int vtxBodyPartOffset = I32(vtx, VtxBodyPartOffset);
        var tris = new List<int>(1024);
        var ranges = new List<MeshPart>();
        int slot = 0;

        for (int bp = 0; bp < numBodyParts; bp++)
        {
            int mdlBpAt = bodyPartIndex + bp * BodyPartSize;
            if (!Fits(mdl, mdlBpAt, BodyPartSize)) return false;
            int mdlNumModels = I32(mdl, mdlBpAt + 4);
            int mdlModelIndex = I32(mdl, mdlBpAt + 12);

            int vtxBpAt = vtxBodyPartOffset + bp * VtxBodyPartSize;
            if (!Fits(vtx, vtxBpAt, VtxBodyPartSize)) return false;
            if (I32(vtx, vtxBpAt) != mdlNumModels) return false;
            int vtxModelOffset = I32(vtx, vtxBpAt + 4);

            for (int m = 0; m < mdlNumModels; m++)
            {
                int mdlModelAt = mdlBpAt + mdlModelIndex + m * ModelSize;
                if (!Fits(mdl, mdlModelAt, ModelSize)) return false;
                int mdlNumMeshes = I32(mdl, mdlModelAt + 72);

                int vtxModelAt = vtxBpAt + vtxModelOffset + m * VtxModelSize;
                if (!Fits(vtx, vtxModelAt, VtxModelSize)) return false;
                int numLods = I32(vtx, vtxModelAt);
                int lodOffset = I32(vtx, vtxModelAt + 4);

                // A "blank" bodygroup set has no meshes in either file - skip it, don't fail.
                if (numLods <= 0 || mdlNumMeshes <= 0)
                {
                    slot += Math.Max(mdlNumMeshes, 0);
                    continue;
                }

                int lodAt = vtxModelAt + lodOffset; // LOD 0 is the first entry
                if (!Fits(vtx, lodAt, VtxLodSize)) return false;
                int numMeshes = I32(vtx, lodAt);
                int meshOffset = I32(vtx, lodAt + 4);
                if (numMeshes != mdlNumMeshes) return false;

                for (int k = 0; k < numMeshes; k++, slot++)
                {
                    if (slot >= layout.Count) return false;
                    var (meshBase, meshVerts, meshSkinRef, meshBodyPart, meshModel) = layout[slot];
                    int meshFirstIndex = tris.Count;

                    int meshAt = lodAt + meshOffset + k * VtxMeshSize;
                    if (!Fits(vtx, meshAt, VtxMeshSize)) return false;
                    int numStripGroups = I32(vtx, meshAt);
                    int stripGroupOffset = I32(vtx, meshAt + 4);
                    if (numStripGroups <= 0 || meshVerts <= 0)
                        continue;
                    if (numStripGroups > 0xFFFF) return false;

                    for (int g = 0; g < numStripGroups; g++)
                    {
                        // Only the leading four fields are read and those are identical in both
                        // strip-group layouts; the stride is the whole difference.
                        int sgAt = meshAt + stripGroupOffset + g * stripGroupStride;
                        if (!Fits(vtx, sgAt, stripGroupStride)) return false;
                        int numVerts = I32(vtx, sgAt);
                        int vertOffset = I32(vtx, sgAt + 4);
                        int numIndices = I32(vtx, sgAt + 8);
                        int indexOffset = I32(vtx, sgAt + 12);
                        if (numVerts < 0 || numIndices < 0) return false;
                        // The discriminator. A strip group's indices are triangle lists, so the count
                        // is always a multiple of three; read at the wrong stride this field is the
                        // middle of some other field and almost never is.
                        if (strictTriangleLists && (numIndices % 3 != 0 || (numIndices > 0 && numVerts == 0)))
                            return false;
                        if (numVerts == 0 || numIndices < 3) continue;

                        int vertsAt = sgAt + vertOffset, indicesAt = sgAt + indexOffset;
                        if (!Fits(vtx, vertsAt, numVerts * VtxStripVertexSize)) return false;
                        if (!Fits(vtx, indicesAt, numIndices * 2)) return false;

                        // Strips inside a group are consecutive runs of the same index array and are
                        // all triangle lists, so the group's whole index array reads as triangles
                        // without walking the strip headers (which is the part that differs).
                        for (int n = 0; n + 2 < numIndices; n += 3)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                int vi = U16(vtx, indicesAt + (n + c) * 2);
                                if (vi >= numVerts) return false;
                                int meshVertId = U16(vtx, vertsAt + vi * VtxStripVertexSize + 4);
                                if (meshVertId >= meshVerts) return false;
                                int global = meshBase + meshVertId;
                                if (global >= vertexCount) return false;
                                tris.Add(global);
                            }
                        }
                    }

                    // One draw range per mesh - every strip group inside a mesh shares its material.
                    if (tris.Count > meshFirstIndex)
                        ranges.Add(new MeshPart(meshFirstIndex, tris.Count - meshFirstIndex, meshSkinRef,
                            meshBodyPart, meshModel));
                }
            }
        }

        if (tris.Count == 0)
            return false;
        indices = tris.ToArray();
        parts = ranges.ToArray();
        return true;
    }

    // --- primitives ----------------------------------------------------------------------------

    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));

    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));

    private static Vector3 Vec3(byte[] b, int at) => new(
        BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));

    /// <summary>Source stores a Quaternion as x, y, z, w - the same order System.Numerics takes.</summary>
    private static Quaternion Quat(byte[] b, int at) => new(
        BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4),
        BitConverter.ToSingle(b, at + 8), BitConverter.ToSingle(b, at + 12));

    private static bool Fits(byte[] b, int at, int bytes) =>
        at >= 0 && bytes >= 0 && (long)at + bytes <= b.Length;

    private static void Require(byte[] b, int at, int bytes, string what)
    {
        if (!Fits(b, at, bytes))
            throw new InvalidDataException($"the .mdl's {what} table runs past the end of the file");
    }
}
