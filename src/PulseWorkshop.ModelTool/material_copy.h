#pragma once
#include <filesystem>
#include <string>
#include <vector>

struct MaterialCopyOptions {
    bool localize   = false; // place VTFs beside their VMT instead of preserving game hierarchy
    bool flat_patch = false; // flatten "Patch" VMTs into their base shader
};

// Find VMTs referenced by material_paths, copy VMTs + VTFs to dest_dir/materials/...
// Prints progress lines to stdout. Returns total files copied.
int copy_materials(
    const std::vector<std::string>&           material_paths,
    const std::vector<std::filesystem::path>& search_paths,
    const std::filesystem::path&              dest_dir,
    const MaterialCopyOptions&                opts);
