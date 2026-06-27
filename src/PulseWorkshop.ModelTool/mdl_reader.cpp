#include "mdl_reader.h"
#include <algorithm>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace {

// studiohdr_t field offsets - stable across MDL versions 44-49
// (from valve source sdk / KitsuneResource mdl.py)
static constexpr size_t OFF_VERSION     = 4;
static constexpr size_t OFF_NUMTEXTURES = 204;
static constexpr size_t OFF_TEXINDEX    = 208;
static constexpr size_t OFF_NUMCDTEX   = 212;
static constexpr size_t OFF_CDTEXINDEX  = 216;
static constexpr size_t TEX_STRUCT_SIZE = 64; // sizeof(mstudiotexture_t) as stored on disk

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

std::vector<std::string> build_material_paths(const MdlMaterials& mats) {
    std::vector<std::string> paths;
    std::vector<std::string> seen;

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

        for (const auto& tex : mats.texture_names) {
            std::string tex_norm = tex;
            std::replace(tex_norm.begin(), tex_norm.end(), '\\', '/');
            while (!tex_norm.empty() && tex_norm.front() == '/') tex_norm.erase(tex_norm.begin());
            add(cd_norm + "/" + tex_norm);
        }
    }

    return paths;
}
