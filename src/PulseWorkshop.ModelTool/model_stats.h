#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

// A sibling file of a compiled model (.vvd/.vtx/.phy/.ani) and whether it is present on disk.
struct DepFile {
    std::string   ext;            // e.g. ".vvd", ".dx90.vtx"
    bool          exists = false;
    std::uintmax_t size  = 0;     // bytes, when exists
};

// Mesh-data stats gathered from the model's sibling .vtx (triangles + LODs) and .vvd (vertices),
// plus the presence/size of every dependency file. Best-effort: a missing/unreadable sibling
// leaves its counts at zero rather than throwing.
struct MeshStats {
    long long            triangles = 0;   // LOD 0 triangle (poly) count, from the .vtx
    int                  vertices  = 0;   // LOD 0 vertex count, from the .vvd
    int                  lods      = 0;   // LOD count, from the .vtx header
    bool                 have_vtx  = false;
    bool                 have_vvd  = false;

    // Per-LOD triangle counts (index 0 == LOD 0), summed across every bodypart/model. Size matches
    // the .vtx LOD count; triangles above equals lod_triangles[0].
    std::vector<long long> lod_triangles;

    // Collision, from the sibling .phy. A prop's $collisionmodel is a single solid; a ragdoll's
    // $collisionjoints has one solid per collision bone plus a ragdollconstraint per joint.
    bool                 have_phy        = false;
    int                  phy_solids      = 0; // collision solids (header solidCount)
    int                  phy_constraints = 0; // "ragdollconstraint" blocks in the text section

    std::vector<DepFile> deps;            // .vvd, .vtx variants, .phy, .ani
};

// Inspect the siblings of a compiled .mdl and return their combined stats. Never throws.
MeshStats read_mesh_stats(const std::filesystem::path& mdl_path);
