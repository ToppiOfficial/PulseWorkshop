using System.Numerics;
using PulseWorkshop.Core.Mdl;

namespace PulseWorkshop.Core.Materials;

/// <summary>Makes eyeball meshes draw as the engine's eye shaders do, by rebuilding studiorender's
/// $irisu/$irisv plane equations (<see cref="StudioEyeball.Uv"/>) - the engine never samples the iris
/// with the .vvd's UVs. Runs after the VMTs resolve: the mapping is the shader's, not the .mdl's.</summary>
/// <remarks>EyeRefract is one layer, so the projection replaces the mesh's UVs at half scale; Eyes is
/// two, so the $iris layer is added here as duplicated triangles blended over the sclera. The cornea,
/// $dilation, the cubemap, the glint and $raytracesphere are view-dependent and not implemented.</remarks>
public static class EyeballProjection
{
    /// <summary>Returns the mesh and material list to draw with, both unchanged when the model has no
    /// eyes. <paramref name="skin"/> picks which skin family resolves an eye's material - a model whose
    /// skins swapped one eye shader for the other would need this redone, and none do.</summary>
    public static (StudioMesh Mesh, ModelMaterial[] Materials) Apply(StudioMesh mesh,
        ModelMaterial[] materials, Action<string>? log = null, int skin = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(materials);
        if (mesh.Eyeballs.Length == 0)
            return (mesh, materials);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var indices = new List<int>();
        var parts = new List<MeshPart>();
        var extraMaterials = new List<ModelMaterial>();

        foreach (var eye in mesh.Eyeballs)
        {
            int last = Math.Min(eye.VertexBase + eye.VertexCount, mesh.Positions.Length);
            if (eye.VertexBase < 0 || last <= eye.VertexBase)
                continue;

            var material = MaterialFor(mesh, materials, eye, skin);
            // With no material resolved there is nothing to tell the two layouts apart; EyeRefract is
            // the one nearly everything compiled since Orange Box uses.
            float scale = material?.Vmt?.IrisUvScale ?? 0.5f;

            if (material?.Iris is not { } iris)
            {
                // One layer: the projection is the eye's only mapping, so it replaces what is there.
                for (int i = eye.VertexBase; i < last; i++)
                    mesh.TexCoords[i] = eye.Uv(mesh.Positions[i], scale);
                continue;
            }

            // Two layers. The eyeball's triangles are copied onto vertices that differ only in their
            // UVs, and drawn again over the sclera.
            if (FindPart(mesh, eye) is not { } source)
                continue;

            // Remap the eye's triangles onto the copies first: an index reaching outside the eye's
            // own vertices means the part is not really this eye's, and the layer is dropped rather
            // than drawn over the wrong geometry.
            int firstVertex = mesh.Positions.Length + positions.Count;
            var remapped = new int[source.IndexCount];
            bool ok = true;
            for (int i = 0; i < source.IndexCount && ok; i++)
            {
                int v = mesh.Indices[source.FirstIndex + i] - eye.VertexBase;
                ok = v >= 0 && v < last - eye.VertexBase;
                remapped[i] = firstVertex + v;
            }
            if (!ok)
                continue;

            for (int i = eye.VertexBase; i < last; i++)
            {
                positions.Add(mesh.Positions[i]);
                normals.Add(mesh.Normals[i]);
                texCoords.Add(eye.Uv(mesh.Positions[i], scale));
            }

            parts.Add(new MeshPart(mesh.Indices.Length + indices.Count, remapped.Length, SkinRef: -1,
                source.BodyPart, source.Model, MaterialOverride: materials.Length + extraMaterials.Count));
            indices.AddRange(remapped);
            extraMaterials.Add(new ModelMaterial
            {
                Name = material.Name + " ($iris)",
                Path = material.Path,
                Vmt = material.Vmt,
                Diffuse = iris,
                ForceTranslucent = true,
            });
            log?.Invoke($"eyeball: {eye.Name} - {material.Vmt?.ShaderName} sclera + projected $iris");
        }

        if (parts.Count == 0)
            return (mesh, materials);

        return (new StudioMesh
        {
            Positions = [.. mesh.Positions, .. positions],
            Normals = [.. mesh.Normals, .. normals],
            TexCoords = [.. mesh.TexCoords, .. texCoords],
            Indices = [.. mesh.Indices, .. indices],
            Parts = [.. mesh.Parts, .. parts],
            MaterialNames = mesh.MaterialNames,
            MaterialDirs = mesh.MaterialDirs,
            BodyParts = mesh.BodyParts,
            SkinFamilies = mesh.SkinFamilies,
            Bones = mesh.Bones,
            Eyeballs = mesh.Eyeballs,
            BoundsMin = mesh.BoundsMin,
            BoundsMax = mesh.BoundsMax,
            Version = mesh.Version,
            IsStaticProp = mesh.IsStaticProp,
        }, [.. materials, .. extraMaterials]);
    }

    private static ModelMaterial? MaterialFor(StudioMesh mesh, ModelMaterial[] materials,
        StudioEyeball eye, int skin)
    {
        int index = mesh.MaterialFor(new MeshPart(0, 0, eye.SkinRef, 0, 0), skin);
        return index >= 0 && index < materials.Length ? materials[index] : null;
    }

    /// <summary>The draw range holding this eye's triangles - the one whose first index lands inside
    /// the vertices the eye owns. One mstudiomesh_t is one part, and an eyeball is one mesh.</summary>
    private static MeshPart? FindPart(StudioMesh mesh, StudioEyeball eye)
    {
        foreach (var part in mesh.Parts)
        {
            if (part.IndexCount <= 0)
                continue;
            int v = mesh.Indices[part.FirstIndex];
            if (v >= eye.VertexBase && v < eye.VertexBase + eye.VertexCount)
                return part;
        }
        return null;
    }
}
