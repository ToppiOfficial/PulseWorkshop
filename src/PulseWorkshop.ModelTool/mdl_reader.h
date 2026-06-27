#pragma once
#include <filesystem>
#include <string>
#include <vector>

struct MdlMaterials {
    std::vector<std::string> texture_names; // raw texture names as stored in the MDL
    std::vector<std::string> cdmaterials;   // cdmaterials search dirs
};

// Parse a compiled MDL. Supports versions 44-49 (HL2 through L4D2 / Portal 2).
// Throws std::runtime_error on failure.
MdlMaterials read_mdl_materials(const std::filesystem::path& mdl_path);

// Combine texture names and cdmaterials into VMT search paths (deduped, forward slashes, no leading slash).
// Mirrors intern/formats/mdl.py: build_material_paths().
std::vector<std::string> build_material_paths(const MdlMaterials& mats);
