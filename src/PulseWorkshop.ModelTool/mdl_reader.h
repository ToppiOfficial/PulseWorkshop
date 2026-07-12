#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

struct MdlMaterials {
    std::vector<std::string> texture_names; // raw texture names as stored in the MDL
    std::vector<std::string> cdmaterials;   // cdmaterials search dirs
};

// One hitbox set and how many hitboxes it contains.
struct MdlHitboxSet {
    std::string name;
    int         num_hitboxes = 0;
};

// One "set" (an mstudiomodel_t) inside a body part - the selectable sub-model of a bodygroup
// (e.g. body / none). Flex info is per-mesh, so it is summed across the set's meshes.
struct MdlBodyPartSet {
    std::string name;                    // the model name (may be blank for empty/"blank" sets)
    int         num_meshes        = 0;
    int         num_vertices      = 0;
    int         num_flexes        = 0;   // total mesh flexes (morph targets) in this set
    bool        has_morph         = false; // this set carries morph targets (numflexes > 0)
    bool        has_flexcontroller = false; // model defines flex controllers AND this set has morphs
};

// One body part (bodygroup) and its selectable sets (models).
struct MdlBodyPart {
    std::string                 name;
    std::vector<MdlBodyPartSet> sets;
};

// Header-level model stats parsed straight from the studiohdr_t. Triangle/vertex counts and LOD
// count live in the sibling .vtx/.vvd files, not here (see model_stats.h).
struct MdlInfo {
    int                       version           = 0;
    uint32_t                  checksum          = 0; // studiohdr_t.checksum - matches the .mdl to its .vtx/.vvd siblings
    int                       num_bones         = 0;
    int                       num_procedural    = 0; // driver/helper bones (proctype 1-4: axis/quat interp, aim) - excludes jiggle
    int                       num_jiggle        = 0; // jiggle bones (proctype == STUDIO_PROC_JIGGLE, 5)
    int                       num_bone_ctrls    = 0;
    int                       num_bodyparts     = 0;
    int                       num_anims         = 0; // local animation descriptions (numlocalanim)
    int                       num_sequences     = 0; // sequences (numlocalseq)
    std::vector<std::string>  include_models;        // $includemodel paths (mstudiomodelgroup_t names)
    int                       num_flex_desc     = 0; // flex descriptors == morph targets
    int                       num_flex_ctrls    = 0; // flex controllers
    int                       num_flex_rules    = 0; // flex rules (controller -> flex expressions)
    std::vector<std::string>  materials;             // texture names (deduped is not applied - as stored)
    std::vector<MdlHitboxSet> hitbox_sets;
    std::vector<std::string>  ik_chains;             // IK chain names (numikchains / mstudioikchain_t)
    std::vector<std::string>  attachments;           // attachment names (numlocalattachments)
    int                       num_eyeballs      = 0; // eyeballs summed across all bodyparts/models
    std::vector<MdlBodyPart>  bodyparts;             // bodygroups and their sets (with per-set flex flags)
};

// Parse a compiled MDL. Supports versions 44-49 (HL2 through L4D2 / Portal 2).
// Throws std::runtime_error on failure.
MdlMaterials read_mdl_materials(const std::filesystem::path& mdl_path);

// Parse header-level model stats (bones, hitbox sets, materials, body parts) from the MDL.
// Throws std::runtime_error on failure.
MdlInfo read_mdl_info(const std::filesystem::path& mdl_path);

// Combine texture names and cdmaterials into VMT search paths (deduped, forward slashes, no leading slash).
// Mirrors intern/formats/mdl.py: build_material_paths().
std::vector<std::string> build_material_paths(const MdlMaterials& mats);
