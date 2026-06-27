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

// Replace all occurrences of |gameinfo_path| (case-insensitive) with the replacement string.
static std::string replace_macro(const std::string& val, const std::string& replacement) {
    static const std::string macro = "|gameinfo_path|";
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

} // namespace

std::vector<std::filesystem::path> get_search_paths(const std::filesystem::path& gameinfo_txt) {
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

        // Skip VPK references (pak01_dir.vpk etc.)
        if (val.size() >= 4 && to_lower(val.substr(val.size() - 4)) == ".vpk") continue;

        // Skip |all_source_engine_paths| - would require enumerating Steam libraries
        if (vl.find("|all_source_engine_paths|") != std::string::npos) continue;

        // Skip wildcard glob entries
        if (val.find('*') != std::string::npos) continue;

        bool has_macro = vl.find("|gameinfo_path|") != std::string::npos;

        std::string resolved = replace_macro(val, gi_dir_str);
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
