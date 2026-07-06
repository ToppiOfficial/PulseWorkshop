#include "mdl_reader.h"
#include <algorithm>
#include <cctype>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace {

// studiohdr_t field offsets - stable across MDL versions 44-49
// (from valve source sdk / KitsuneResource mdl.py)
static constexpr size_t OFF_VERSION       = 4;
static constexpr size_t OFF_NUMBONES      = 156;
static constexpr size_t OFF_BONEINDEX     = 160;
static constexpr size_t OFF_NUMBONECTRLS  = 164;
static constexpr size_t OFF_NUMHITBOXSETS = 172;
static constexpr size_t OFF_HITBOXSETIDX  = 176;
static constexpr size_t OFF_NUMLOCALANIM  = 180; // numlocalanim (animation descriptions)
static constexpr size_t OFF_NUMLOCALSEQ   = 188; // numlocalseq (sequences)
static constexpr size_t OFF_NUMTEXTURES   = 204;
static constexpr size_t OFF_TEXINDEX      = 208;
static constexpr size_t OFF_NUMCDTEX      = 212;
static constexpr size_t OFF_CDTEXINDEX    = 216;
static constexpr size_t OFF_NUMBODYPARTS  = 232;
static constexpr size_t OFF_BODYPARTIDX   = 236; // bodypartindex
// Field order after numbodyparts (each is an int32):
// bodypartindex(236) numlocalattachments(240) localattachmentindex(244) numlocalnodes(248)
// localnodeindex(252) localnodenameindex(256) numflexdesc(260) ...
static constexpr size_t OFF_NUMATTACH     = 240; // numlocalattachments
static constexpr size_t OFF_ATTACHIDX     = 244; // localattachmentindex
static constexpr size_t OFF_NUMFLEXDESC   = 260; // flex descriptors == morph targets
static constexpr size_t OFF_NUMFLEXCTRLS  = 268; // flex controllers
static constexpr size_t OFF_NUMFLEXRULES  = 276; // flex rules
static constexpr size_t OFF_NUMIKCHAINS   = 284; // numikchains
static constexpr size_t OFF_IKCHAINIDX    = 288; // ikchainindex
static constexpr size_t OFF_NUMINCLUDEMDL = 336; // numincludemodels ($includemodel count)
static constexpr size_t OFF_INCLUDEMDLIDX = 340; // includemodelindex

// mstudioattachment_t: sznameindex(0) flags(4) localbone(8) matrix3x4(12,48B) unused[8](60,32B) = 92
static constexpr size_t ATTACH_STRUCT_SIZE = 92;
// mstudioikchain_t: sznameindex(0) linktype(4) numlinks(8) linkindex(12) = 16
static constexpr size_t IKCHAIN_STRUCT_SIZE = 16;
// mstudiobodyparts_t: sznameindex(0) nummodels(4) base(8) modelindex(12) = 16 (modelindex rel. to struct)
static constexpr size_t BODYPART_STRUCT_SIZE = 16;
static constexpr size_t BODYPART_OFF_NUMMODELS = 4;
static constexpr size_t BODYPART_OFF_MODELIDX  = 12;
// mstudiomodel_t on disk: name[64] ... numeyeballs(100) eyeballindex(104) ... = 148 bytes
static constexpr size_t MODEL_STRUCT_SIZE    = 148;
static constexpr size_t MODEL_OFF_NUMEYEBALLS = 100;
static constexpr size_t MODELGROUP_SIZE   = 8;   // sizeof(mstudiomodelgroup_t): szlabelindex + sznameindex
static constexpr size_t MODELGROUP_OFF_NAME = 4; // sznameindex, relative to the struct start
static constexpr size_t TEX_STRUCT_SIZE   = 64;  // sizeof(mstudiotexture_t) as stored on disk

static constexpr size_t BONE_STRUCT_SIZE     = 216; // sizeof(mstudiobone_t) on disk
static constexpr size_t BONE_OFF_PROCTYPE    = 164; // mstudiobone_t.proctype
static constexpr size_t HITBOXSET_STRUCT_SIZE = 12; // sizeof(mstudiohitboxset_t) on disk
static constexpr size_t HITBOXSET_OFF_NUMHB   = 4;  // mstudiohitboxset_t.numhitboxes
static constexpr int    STUDIO_PROC_JIGGLE    = 5;  // mstudiobone_t.proctype for jiggle bones

static const uint8_t MDL_MAGIC[4] = {'I', 'D', 'S', 'T'};

static int32_t read_i32(const std::vector<uint8_t>& data, size_t offset) {
    if (offset + 4 > data.size()) return 0;
    int32_t v;
    std::memcpy(&v, data.data() + offset, 4);
    return v;
}

static std::string read_cstr(const std::vector<uint8_t>& data, size_t offset) {
    if (offset >= data.size()) return {};
    size_t end = offset;
    while (end < data.size() && data[end] != 0) ++end;
    return std::string(reinterpret_cast<const char*>(data.data() + offset), end - offset);
}

static std::string norm(std::string s) {
    std::replace(s.begin(), s.end(), '\\', '/');
    return s;
}

} // namespace

MdlMaterials read_mdl_materials(const std::filesystem::path& mdl_path) {
    std::ifstream f(mdl_path, std::ios::binary);
    if (!f) throw std::runtime_error("Cannot open MDL: " + mdl_path.string());

    std::vector<uint8_t> data(
        (std::istreambuf_iterator<char>(f)),
        std::istreambuf_iterator<char>());

    if (data.size() < 220)
        throw std::runtime_error("MDL too small to be valid: " + mdl_path.string());
    if (std::memcmp(data.data(), MDL_MAGIC, 4) != 0)
        throw std::runtime_error("Not an MDL file (bad magic): " + mdl_path.string());

    int32_t version = read_i32(data, OFF_VERSION);
    if (version < 44)
        throw std::runtime_error("MDL version " + std::to_string(version) + " not supported (need 44+)");

    int32_t numtex   = read_i32(data, OFF_NUMTEXTURES);
    int32_t texindex = read_i32(data, OFF_TEXINDEX);
    int32_t numcd    = read_i32(data, OFF_NUMCDTEX);
    int32_t cdindex  = read_i32(data, OFF_CDTEXINDEX);

    MdlMaterials result;

    for (int32_t i = 0; i < numtex; ++i) {
        size_t struct_start = static_cast<size_t>(texindex) + static_cast<size_t>(i) * TEX_STRUCT_SIZE;
        int32_t nameoff = read_i32(data, struct_start); // sznameindex: relative to struct_start
        auto name = read_cstr(data, struct_start + static_cast<size_t>(nameoff));
        result.texture_names.push_back(norm(std::move(name)));
    }

    for (int32_t i = 0; i < numcd; ++i) {
        size_t off_pos = static_cast<size_t>(cdindex) + static_cast<size_t>(i) * 4;
        int32_t stroff = read_i32(data, off_pos); // absolute offset from file start
        auto dir = read_cstr(data, static_cast<size_t>(stroff));
        result.cdmaterials.push_back(norm(std::move(dir)));
    }

    return result;
}

MdlInfo read_mdl_info(const std::filesystem::path& mdl_path) {
    std::ifstream f(mdl_path, std::ios::binary);
    if (!f) throw std::runtime_error("Cannot open MDL: " + mdl_path.string());

    std::vector<uint8_t> data(
        (std::istreambuf_iterator<char>(f)),
        std::istreambuf_iterator<char>());

    if (data.size() < 344)
        throw std::runtime_error("MDL too small to be valid: " + mdl_path.string());
    if (std::memcmp(data.data(), MDL_MAGIC, 4) != 0)
        throw std::runtime_error("Not an MDL file (bad magic): " + mdl_path.string());

    int32_t version = read_i32(data, OFF_VERSION);
    if (version < 44)
        throw std::runtime_error("MDL version " + std::to_string(version) + " not supported (need 44+)");

    MdlInfo info;
    info.version        = version;
    info.num_bones      = read_i32(data, OFF_NUMBONES);
    info.num_bone_ctrls = read_i32(data, OFF_NUMBONECTRLS);
    info.num_bodyparts  = read_i32(data, OFF_NUMBODYPARTS);
    info.num_anims      = read_i32(data, OFF_NUMLOCALANIM);
    info.num_sequences  = read_i32(data, OFF_NUMLOCALSEQ);
    info.num_flex_desc  = read_i32(data, OFF_NUMFLEXDESC);
    info.num_flex_ctrls = read_i32(data, OFF_NUMFLEXCTRLS);
    info.num_flex_rules = read_i32(data, OFF_NUMFLEXRULES);

    // $includemodel references (external animation/model includes). Each mstudiomodelgroup_t stores
    // a label and a name; the name is the referenced .mdl path (relative to the struct start).
    int32_t num_inc = read_i32(data, OFF_NUMINCLUDEMDL);
    int32_t inc_idx = read_i32(data, OFF_INCLUDEMDLIDX);
    for (int32_t i = 0; i < num_inc; ++i) {
        size_t struct_start = static_cast<size_t>(inc_idx) + static_cast<size_t>(i) * MODELGROUP_SIZE;
        int32_t nameoff = read_i32(data, struct_start + MODELGROUP_OFF_NAME);
        std::string name = norm(read_cstr(data, struct_start + static_cast<size_t>(nameoff)));
        if (!name.empty()) info.include_models.push_back(std::move(name));
    }

    // Bones: split by proctype into jiggle (physics, proctype 5) and other procedural "driver"
    // bones (twist/QuatInterp/aim helpers, proctype 1-4). The two counts are disjoint.
    int32_t boneindex = read_i32(data, OFF_BONEINDEX);
    for (int32_t i = 0; i < info.num_bones; ++i) {
        size_t bone_start = static_cast<size_t>(boneindex) + static_cast<size_t>(i) * BONE_STRUCT_SIZE;
        int32_t proctype = read_i32(data, bone_start + BONE_OFF_PROCTYPE);
        if (proctype == STUDIO_PROC_JIGGLE) ++info.num_jiggle;
        else if (proctype != 0)             ++info.num_procedural;
    }

    // Hitbox sets and how many hitboxes each holds.
    int32_t numhbsets  = read_i32(data, OFF_NUMHITBOXSETS);
    int32_t hbsetindex = read_i32(data, OFF_HITBOXSETIDX);
    for (int32_t i = 0; i < numhbsets; ++i) {
        size_t set_start = static_cast<size_t>(hbsetindex) + static_cast<size_t>(i) * HITBOXSET_STRUCT_SIZE;
        int32_t nameoff = read_i32(data, set_start); // sznameindex: relative to set_start
        MdlHitboxSet set;
        set.name = read_cstr(data, set_start + static_cast<size_t>(nameoff));
        set.num_hitboxes = read_i32(data, set_start + HITBOXSET_OFF_NUMHB);
        info.hitbox_sets.push_back(std::move(set));
    }

    // Attachments (mstudioattachment_t): each carries a name relative to its struct start.
    int32_t num_attach  = read_i32(data, OFF_NUMATTACH);
    int32_t attach_idx  = read_i32(data, OFF_ATTACHIDX);
    for (int32_t i = 0; i < num_attach; ++i) {
        size_t struct_start = static_cast<size_t>(attach_idx) + static_cast<size_t>(i) * ATTACH_STRUCT_SIZE;
        int32_t nameoff = read_i32(data, struct_start); // sznameindex: relative to struct_start
        info.attachments.push_back(read_cstr(data, struct_start + static_cast<size_t>(nameoff)));
    }

    // IK chains (mstudioikchain_t): name relative to its struct start.
    int32_t num_ik  = read_i32(data, OFF_NUMIKCHAINS);
    int32_t ik_idx  = read_i32(data, OFF_IKCHAINIDX);
    for (int32_t i = 0; i < num_ik; ++i) {
        size_t struct_start = static_cast<size_t>(ik_idx) + static_cast<size_t>(i) * IKCHAIN_STRUCT_SIZE;
        int32_t nameoff = read_i32(data, struct_start); // sznameindex: relative to struct_start
        info.ik_chains.push_back(read_cstr(data, struct_start + static_cast<size_t>(nameoff)));
    }

    // Eyeballs live per-model: walk bodypart -> model and sum each model's numeyeballs.
    int32_t num_bp  = read_i32(data, OFF_NUMBODYPARTS);
    int32_t bp_idx  = read_i32(data, OFF_BODYPARTIDX);
    for (int32_t bp = 0; bp < num_bp; ++bp) {
        size_t bp_start   = static_cast<size_t>(bp_idx) + static_cast<size_t>(bp) * BODYPART_STRUCT_SIZE;
        int32_t nummodels = read_i32(data, bp_start + BODYPART_OFF_NUMMODELS);
        int32_t modeloff  = read_i32(data, bp_start + BODYPART_OFF_MODELIDX); // relative to bp_start
        for (int32_t m = 0; m < nummodels; ++m) {
            size_t model_start = bp_start + static_cast<size_t>(modeloff) + static_cast<size_t>(m) * MODEL_STRUCT_SIZE;
            info.num_eyeballs += read_i32(data, model_start + MODEL_OFF_NUMEYEBALLS);
        }
    }

    // Materials (texture names), read the same way as read_mdl_materials.
    int32_t numtex   = read_i32(data, OFF_NUMTEXTURES);
    int32_t texindex = read_i32(data, OFF_TEXINDEX);
    for (int32_t i = 0; i < numtex; ++i) {
        size_t struct_start = static_cast<size_t>(texindex) + static_cast<size_t>(i) * TEX_STRUCT_SIZE;
        int32_t nameoff = read_i32(data, struct_start); // sznameindex: relative to struct_start
        info.materials.push_back(norm(read_cstr(data, struct_start + static_cast<size_t>(nameoff))));
    }

    return info;
}

std::vector<std::string> build_material_paths(const MdlMaterials& mats) {
    std::vector<std::string> paths;
    std::vector<std::string> seen;

    auto to_lower = [](std::string s) {
        std::transform(s.begin(), s.end(), s.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return s;
    };

    auto add = [&](std::string p) {
        std::replace(p.begin(), p.end(), '\\', '/');
        while (!p.empty() && p.front() == '/') p.erase(p.begin());
        while (!p.empty() && p.back()  == '/') p.pop_back();
        if (p.empty()) return;
        if (std::find(seen.begin(), seen.end(), p) == seen.end()) {
            seen.push_back(p);
            paths.push_back(p);
        }
    };

    // Always add bare texture names first (handles full-path textures and empty cdmaterials)
    for (const auto& tex : mats.texture_names)
        add(tex);

    // Then add cdmaterials-prefixed combinations
    for (const auto& cd : mats.cdmaterials) {
        std::string cd_norm = cd;
        std::replace(cd_norm.begin(), cd_norm.end(), '\\', '/');
        while (!cd_norm.empty() && cd_norm.front() == '/') cd_norm.erase(cd_norm.begin());
        while (!cd_norm.empty() && cd_norm.back()  == '/') cd_norm.pop_back();
        if (cd_norm.empty()) continue;

        const std::string cd_lower = to_lower(cd_norm);

        for (const auto& tex : mats.texture_names) {
            std::string tex_norm = tex;
            std::replace(tex_norm.begin(), tex_norm.end(), '\\', '/');
            while (!tex_norm.empty() && tex_norm.front() == '/') tex_norm.erase(tex_norm.begin());

            // When the texture name already carries its full path (DMX stores the full
            // filepath), prefixing it with a cdmaterials dir that points at the same
            // directory produces a doubled path (".../faelynn/.../faelynn/c_...") that
            // resolves to nothing. The bare texture name already covers this case, so skip
            // the redundant combination instead of emitting a spurious "not found".
            const std::string tex_lower = to_lower(tex_norm);
            if (tex_lower == cd_lower ||
                (tex_lower.size() > cd_lower.size() &&
                 tex_lower.compare(0, cd_lower.size(), cd_lower) == 0 &&
                 tex_lower[cd_lower.size()] == '/'))
                continue;

            add(cd_norm + "/" + tex_norm);
        }
    }

    return paths;
}
