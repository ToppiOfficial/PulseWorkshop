#include "model_stats.h"
#include <cstring>
#include <fstream>
#include <iterator>
#include <string_view>
#include <system_error>

namespace fs = std::filesystem;

namespace {

// The dependency siblings we probe, in display order. The .vtx variants share a base name with the
// .mdl (foo.mdl -> foo.dx90.vtx); .dx90 is the modern hardware mesh preferred for the triangle count.
static const char* const DEP_EXTS[] = {
    ".vvd", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx", ".phy", ".ani",
};

static std::vector<uint8_t> read_all(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return {};
    return std::vector<uint8_t>(
        (std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

static int32_t read_i32(const std::vector<uint8_t>& d, size_t off) {
    if (off + 4 > d.size()) return 0;
    int32_t v;
    std::memcpy(&v, d.data() + off, 4);
    return v;
}

// The path with its final extension swapped for `ext` (which begins with a dot). For .mdl this
// yields foo.vvd / foo.phy; the .vtx variants carry a compound suffix so we append to the stem.
static fs::path sibling(const fs::path& mdl, const std::string& ext) {
    fs::path p = mdl;
    p.replace_extension(); // drop ".mdl"
    return p.string() + ext;
}

// --- .vvd: LOD-0 vertex count -------------------------------------------------------------------
// vertexFileHeader_t: id(0) version(4) checksum(8) numLODs(12) numLODVertexes[8]@16 ...
static int read_vvd_vertices(const fs::path& vvd) {
    auto d = read_all(vvd);
    if (d.size() < 20) return 0;
    if (std::memcmp(d.data(), "IDSV", 4) != 0) return 0;
    return read_i32(d, 16); // numLODVertexes[0]
}

// --- .vtx: per-LOD triangle count + LOD count ---------------------------------------------------
// All VTX structs are byte-packed (#pragma pack(1)) and their *Offset fields are relative to the
// struct they live in. We walk bodypart -> model -> every LOD -> mesh -> stripgroup and sum
// numIndices/3 (VTX stores triangle lists) into a per-LOD total. Struct sizes below are the packed
// on-disk sizes.
static constexpr size_t VTX_OFF_NUMLODS   = 20;
static constexpr size_t VTX_OFF_NUMBODYP  = 28;
static constexpr size_t VTX_OFF_BODYPOFF  = 32;
static constexpr size_t BODYPART_SIZE     = 8;   // numModels(0) modelOffset(4)
static constexpr size_t MODEL_SIZE        = 8;   // numLODs(0) lodOffset(4)
static constexpr size_t LOD_SIZE          = 12;  // numMeshes(0) meshOffset(4) switchPoint(8)
static constexpr size_t MESH_SIZE         = 9;   // numStripGroups(0) stripGroupOffset(4) flags(8)
static constexpr size_t STRIPGROUP_SIZE   = 25;  // ... numIndices(8) ... flags(24)

static void read_vtx(const fs::path& vtx, MeshStats& out) {
    auto d = read_all(vtx);
    if (d.size() < 36) return;

    out.have_vtx = true;
    out.lods = read_i32(d, VTX_OFF_NUMLODS);
    if (out.lods > 0)
        out.lod_triangles.assign(static_cast<size_t>(out.lods), 0);

    int32_t num_bodyparts = read_i32(d, VTX_OFF_NUMBODYP);
    int32_t bodypart_off  = read_i32(d, VTX_OFF_BODYPOFF);

    for (int32_t bp = 0; bp < num_bodyparts; ++bp) {
        size_t bp_base   = static_cast<size_t>(bodypart_off) + static_cast<size_t>(bp) * BODYPART_SIZE;
        int32_t nummodels = read_i32(d, bp_base);
        int32_t modeloff  = read_i32(d, bp_base + 4);

        for (int32_t m = 0; m < nummodels; ++m) {
            size_t model_base = bp_base + static_cast<size_t>(modeloff) + static_cast<size_t>(m) * MODEL_SIZE;
            int32_t numlods = read_i32(d, model_base);
            int32_t lodoff  = read_i32(d, model_base + 4);

            for (int32_t lod = 0; lod < numlods; ++lod) {
                size_t lod_base   = model_base + static_cast<size_t>(lodoff) + static_cast<size_t>(lod) * LOD_SIZE;
                int32_t nummeshes = read_i32(d, lod_base);
                int32_t meshoff   = read_i32(d, lod_base + 4);

                long long lod_tris = 0;
                for (int32_t msh = 0; msh < nummeshes; ++msh) {
                    size_t mesh_base = lod_base + static_cast<size_t>(meshoff) + static_cast<size_t>(msh) * MESH_SIZE;
                    int32_t numsg  = read_i32(d, mesh_base);
                    int32_t sgoff  = read_i32(d, mesh_base + 4);

                    for (int32_t sg = 0; sg < numsg; ++sg) {
                        size_t sg_base = mesh_base + static_cast<size_t>(sgoff) + static_cast<size_t>(sg) * STRIPGROUP_SIZE;
                        int32_t numindices = read_i32(d, sg_base + 8);
                        if (numindices > 0)
                            lod_tris += numindices / 3;
                    }
                }

                if (lod >= 0 && static_cast<size_t>(lod) < out.lod_triangles.size())
                    out.lod_triangles[static_cast<size_t>(lod)] += lod_tris;
            }
        }
    }

    // LOD 0 is the model's headline poly budget; keep the flat `triangles` field in sync.
    if (!out.lod_triangles.empty())
        out.triangles = out.lod_triangles.front();
}

// --- .phy: collision solids + ragdoll constraints -----------------------------------------------
// phyheader_t: size(0) id(4) solidCount(8) checksum(12). The header is `size` bytes (usually 16).
// Each solid is preceded by an int32 giving the byte length of that solid's collision data; walking
// those lengths lands us on the trailing text (KeyValues) block, which holds one "solid" per
// collision piece and one "ragdollconstraint" per joint. We report solidCount from the header (the
// authoritative solid count) and count the "ragdollconstraint" keywords in the text.
static void read_phy(const fs::path& phy, MeshStats& out) {
    auto d = read_all(phy);
    if (d.size() < 16) return;

    int32_t header_size = read_i32(d, 0);
    int32_t solid_count = read_i32(d, 8);
    if (header_size < 16 || solid_count < 0 || solid_count > 4096) return; // sanity guard

    out.have_phy   = true;
    out.phy_solids = solid_count;

    // Walk past each solid (int32 length prefix + that many bytes) to reach the text block.
    size_t pos = static_cast<size_t>(header_size);
    for (int32_t i = 0; i < solid_count; ++i) {
        if (pos + 4 > d.size()) return;
        int32_t solid_size = read_i32(d, pos);
        if (solid_size < 0) return;
        pos += 4 + static_cast<size_t>(solid_size);
    }
    if (pos >= d.size()) return;

    // Count "ragdollconstraint" occurrences in the trailing KeyValues text.
    static const std::string kw = "ragdollconstraint";
    const char* text_begin = reinterpret_cast<const char*>(d.data() + pos);
    size_t      text_len   = d.size() - pos;
    std::string_view text(text_begin, text_len);
    for (size_t at = text.find(kw); at != std::string_view::npos; at = text.find(kw, at + kw.size()))
        ++out.phy_constraints;
}

} // namespace

MeshStats read_mesh_stats(const fs::path& mdl_path) {
    MeshStats stats;

    // Presence + size of every dependency sibling.
    for (const char* ext : DEP_EXTS) {
        DepFile dep;
        dep.ext = ext;
        std::error_code ec;
        fs::path p = sibling(mdl_path, dep.ext);
        if (fs::exists(p, ec) && !ec) {
            dep.exists = true;
            dep.size = fs::file_size(p, ec);
            if (ec) dep.size = 0;
        }
        stats.deps.push_back(std::move(dep));
    }

    // Vertices from the .vvd.
    fs::path vvd = sibling(mdl_path, ".vvd");
    std::error_code ec;
    if (fs::exists(vvd, ec) && !ec) {
        stats.vertices = read_vvd_vertices(vvd);
        stats.have_vvd = true;
    }

    // Triangles from the best available .vtx (prefer .dx90, then .dx80, .sw, plain .vtx).
    for (const char* ext : {".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx"}) {
        fs::path vtx = sibling(mdl_path, ext);
        if (fs::exists(vtx, ec) && !ec) {
            read_vtx(vtx, stats);
            break;
        }
    }

    // Collision solids + constraints from the .phy.
    fs::path phy = sibling(mdl_path, ".phy");
    if (fs::exists(phy, ec) && !ec)
        read_phy(phy, stats);

    return stats;
}
