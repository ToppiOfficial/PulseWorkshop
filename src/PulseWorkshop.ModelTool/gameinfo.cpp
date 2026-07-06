#include "gameinfo.h"
#include "kv_parser.h"
#include <algorithm>
#include <cctype>
#include <fstream>
#include <stdexcept>

namespace {

static std::string to_lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

static std::string read_text(const std::filesystem::path& p) {
    std::ifstream f(p);
    if (!f) throw std::runtime_error("Cannot open gameinfo.txt: " + p.string());
    return {std::istreambuf_iterator<char>(f), {}};
}

// Replace all occurrences of |macro| (case-insensitive) with the replacement string.
static std::string replace_macro_named(const std::string& val, const std::string& macro,
                                       const std::string& replacement) {
    std::string result;
    result.reserve(val.size());
    size_t i = 0;
    while (i < val.size()) {
        if (i + macro.size() <= val.size()
            && to_lower(val.substr(i, macro.size())) == macro) {
            result += replacement;
            i += macro.size();
        } else {
            result += val[i++];
        }
    }
    return result;
}

static std::string replace_macro(const std::string& val, const std::string& replacement) {
    return replace_macro_named(val, "|gameinfo_path|", replacement);
}

} // namespace

// Shared SearchPaths walk. want_vpks selects which entries are returned: the directory
// search paths (VPK references skipped), or the VPK archive references themselves.
static std::vector<std::filesystem::path> collect_search_entries(
    const std::filesystem::path& gameinfo_txt, bool want_vpks) {
    namespace fs = std::filesystem;

    auto root = kv_parse(read_text(gameinfo_txt));

    const KvNode* gi = kv_find(root.children, "GameInfo");
    if (!gi) throw std::runtime_error("gameinfo.txt: missing GameInfo block");

    const KvNode* fs_block = kv_find(gi->children, "FileSystem");
    if (!fs_block) throw std::runtime_error("gameinfo.txt: missing FileSystem block");

    const KvNode* sp_block = kv_find(fs_block->children, "SearchPaths");
    if (!sp_block) throw std::runtime_error("gameinfo.txt: missing SearchPaths block");

    fs::path gi_dir = gameinfo_txt.parent_path();
    // Use forward slashes in the replacement so concatenation is cross-consistent
    std::string gi_dir_str = gi_dir.generic_string();

    // In Source Engine, paths that use |gameinfo_path| expand to an absolute path.
    // Paths WITHOUT |gameinfo_path| (e.g. "left4dead2_workshop", "hl2") are relative to
    // the app root - one directory above the gameinfo directory (i.e. the Steam game folder).
    fs::path app_root = gi_dir.parent_path();

    std::vector<fs::path> result;

    for (const auto& entry : sp_block->children) {
        const std::string& val = entry.value;
        if (val.empty()) continue;

        const std::string vl = to_lower(val);

        // VPK references (pak01_dir.vpk etc.) and directory paths go to separate callers.
        const bool is_vpk = val.size() >= 4 && to_lower(val.substr(val.size() - 4)) == ".vpk";
        if (is_vpk != want_vpks) continue;

        // |all_source_engine_paths| means "every Source install" - too broad to enumerate for
        // directories. For VPK entries the referenced archives live under the game's own root
        // (e.g. GMod's sourceengine/hl2_textures.vpk), so stripping the macro resolves them.
        std::string entry_val = val;
        if (vl.find("|all_source_engine_paths|") != std::string::npos) {
            if (!want_vpks) continue;
            entry_val = replace_macro_named(entry_val, "|all_source_engine_paths|", "");
            if (entry_val.empty()) continue;
        }

        // Skip wildcard glob entries
        if (entry_val.find('*') != std::string::npos) continue;

        bool has_macro = vl.find("|gameinfo_path|") != std::string::npos;

        std::string resolved = replace_macro(entry_val, gi_dir_str);
        std::replace(resolved.begin(), resolved.end(), '\\', '/');

        fs::path p(resolved);
        if (p.is_relative()) {
            // Paths with |gameinfo_path| become absolute after macro substitution, so
            // only bare relative names reach here - they belong under the app root.
            p = (has_macro ? gi_dir : app_root) / p;
        }
        result.push_back(p.lexically_normal());
    }

    return result;
}

std::vector<std::filesystem::path> get_search_paths(const std::filesystem::path& gameinfo_txt) {
    return collect_search_entries(gameinfo_txt, /*want_vpks=*/false);
}

std::vector<std::filesystem::path> get_vpk_paths(const std::filesystem::path& gameinfo_txt) {
    namespace fs = std::filesystem;

    std::vector<fs::path> result;
    std::vector<std::string> seen;  // lowercase keys for dedup, order preserved in result

    auto add = [&](fs::path archive) {
        std::error_code ec;
        if (!fs::exists(archive, ec)) {
            // Some gameinfos reference "garrysmod.vpk"; the filesystem opens "garrysmod_dir.vpk".
            fs::path with_dir = archive.parent_path()
                / (archive.stem().string() + "_dir" + archive.extension().string());
            if (!fs::exists(with_dir, ec)) return;
            archive = with_dir;
        }

        std::string key = to_lower(archive.generic_string());
        if (std::find(seen.begin(), seen.end(), key) != seen.end()) return;
        seen.push_back(std::move(key));
        result.push_back(std::move(archive));
    };

    // Explicit .vpk entries in SearchPaths (GMod-style gameinfos).
    for (auto& p : collect_search_entries(gameinfo_txt, /*want_vpks=*/true))
        add(std::move(p));

    // L4D2-era gameinfos list only directories: the engine implicitly mounts the VPKs inside
    // every "Game" search path directory (pak01_dir.vpk, pak02_dir.vpk, hl2_misc_dir.vpk, ...),
    // so pick up every "*_dir.vpk" directory archive found there.
    for (auto& dir : collect_search_entries(gameinfo_txt, /*want_vpks=*/false)) {
        std::error_code ec;
        if (!fs::is_directory(dir, ec)) continue;
        for (const auto& e : fs::directory_iterator(dir, ec)) {
            if (!e.is_regular_file(ec)) continue;
            const std::string name = to_lower(e.path().filename().string());
            if (name.size() > 8 && name.compare(name.size() - 8, 8, "_dir.vpk") == 0)
                add(e.path());
        }
    }

    return result;
}
