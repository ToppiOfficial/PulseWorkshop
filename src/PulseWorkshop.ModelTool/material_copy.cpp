#include "material_copy.h"
#include "kv_parser.h"
#include <algorithm>
#include <cctype>
#include <fstream>
#include <iostream>
#include <optional>
#include <set>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace {

// VMT texture parameter names (lowercase). Mirrors KitsuneResource's TEXTURE_KEYS so the same
// set of textures is gathered. Keys are compared against to_lower(node.key), so all must be lower.
static const std::unordered_set<std::string>& tex_params() {
    static const std::unordered_set<std::string> s = {
        "$basetexture",                "$basetexture2",
        "$bumpmap",                    "$bumpmap2",
        "$normaltexture",              "$normalmap",
        "$lightwarptexture",           "$phongexponenttexture",
        "$phongwarptexture",
        "$emissiveblendbasetexture",   "$emissiveblendtexture",
        "$emissiveblendflowtexture",
        "$ssbump",
        "$envmapmask",                 "$envmap",
        "$detail",                     "$detail1",                "$detail2",
        "$blendmodulatetexture",
        "$ambientoccltexture",
        "$corneatexture",
        "$selfillummask",              "$selfillumtexture",
        "$iris",
        "$mraotexture",
        "$paintsplatnormalmap",        "$paintsplatbubblelayout",
        "$paintsplatbubble",           "$paintenvmap",
        "$emissiontexture",            "$emissiontexture2",
    };
    return s;
}

static std::string to_lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

static std::string norm(std::string p) {
    std::replace(p.begin(), p.end(), '\\', '/');
    while (!p.empty() && p.front() == '/') p.erase(p.begin());
    while (!p.empty() && p.back()  == '/') p.pop_back();
    return p;
}

static std::string read_text(const fs::path& p) {
    std::ifstream f(p);
    return {std::istreambuf_iterator<char>(f), {}};
}

static bool copy_file_safe(const fs::path& src, const fs::path& dst) {
    std::error_code ec;
    fs::create_directories(dst.parent_path(), ec);
    if (ec) {
        std::cerr << "[ModelTool] Warning: create_directories failed: " << ec.message() << "\n";
        return false;
    }
    fs::copy_file(src, dst, fs::copy_options::overwrite_existing, ec);
    if (ec) {
        std::cerr << "[ModelTool] Warning: copy_file failed: " << ec.message() << "\n";
        return false;
    }
    return true;
}

static std::optional<fs::path> find_file(
    const std::vector<fs::path>& search_paths,
    const std::string& rel) {
    for (const auto& sp : search_paths) {
        auto c = sp / rel;
        if (fs::exists(c)) return c;
    }
    return std::nullopt;
}

// Recursively collect texture parameter values from the KV tree.
static void collect_tex_refs(const std::vector<KvNode>& nodes, std::vector<std::string>& out) {
    const auto& tp = tex_params();
    for (const auto& n : nodes) {
        if (!n.children.empty()) {
            collect_tex_refs(n.children, out);
        } else if (!n.value.empty() && tp.count(to_lower(n.key))) {
            out.push_back(n.value);
        }
    }
}

// Recursively update texture parameter values in-place using old->new remap.
static void update_tex_values(std::vector<KvNode>& nodes,
                               const std::unordered_map<std::string, std::string>& remap) {
    const auto& tp = tex_params();
    for (auto& n : nodes) {
        if (!n.children.empty()) {
            update_tex_values(n.children, remap);
        } else if (!n.value.empty() && tp.count(to_lower(n.key))) {
            auto it = remap.find(n.value);
            if (it != remap.end()) n.value = it->second;
        }
    }
}

// Merge Patch replace/insert blocks onto base children.
// replace: update existing keys (or append if not found).
// insert:  add keys that don't already exist.
static std::vector<KvNode> merge_patch(
    std::vector<KvNode> base,
    const std::vector<KvNode>& replace_kv,
    const std::vector<KvNode>& insert_kv) {

    for (const auto& r : replace_kv) {
        bool found = false;
        for (auto& b : base) {
            if (to_lower(b.key) == to_lower(r.key)) {
                b.value    = r.value;
                b.children = r.children;
                found = true;
                break;
            }
        }
        if (!found) base.push_back(r);
    }

    for (const auto& ins : insert_kv) {
        bool found = false;
        for (const auto& b : base)
            if (to_lower(b.key) == to_lower(ins.key)) { found = true; break; }
        if (!found) base.push_back(ins);
    }

    return base;
}

// Normalize a Patch "include" value to a material-relative path: forward slashes, no leading
// "materials/", no trailing ".vmt". Includes are written as e.g. "materials/.../base.vmt".
static std::string include_to_matpath(const std::string& include_val) {
    std::string p = norm(include_val);
    std::string pl = to_lower(p);
    if (pl.size() > 10 && pl.compare(0, 10, "materials/") == 0)
        p = p.substr(10);
    pl = to_lower(p);
    if (pl.size() > 4 && pl.compare(pl.size() - 4, 4, ".vmt") == 0)
        p = p.substr(0, p.size() - 4);
    return p;
}

// Copy a single VTF (referenced by a VMT) to an arbitrary materials-relative destination.
// Returns the source path's stem-less behaviour is handled by the caller; keyed off the original
// path in done_vtfs so the same source is only copied once per run.
static void copy_one_vtf(
    const std::string&           tex,        // material-relative texture path (no extension)
    const std::string&           dest_mp,    // material-relative destination (no extension)
    const std::vector<fs::path>& search_paths,
    const fs::path&              dest_dir,
    std::set<std::string>&       done_vtfs,
    int&                         copied) {

    if (done_vtfs.count(tex)) return;
    auto vtf_src = find_file(search_paths, "materials/" + tex + ".vtf");
    if (!vtf_src) vtf_src = find_file(search_paths, "materials/" + tex);
    if (!vtf_src) {
        std::cout << "[ModelTool] VTF not found: " << tex << "\n";
        return;
    }
    if (copy_file_safe(*vtf_src, dest_dir / "materials" / (dest_mp + ".vtf"))) {
        done_vtfs.insert(tex);
        ++copied;
        std::cout << "[ModelTool] Copied VTF: " << dest_mp << "\n";
    }
}

// A Patch VMT copied as-is (not flattened) still references its base via "include", so the base
// VMT - and the base's own textures - must travel with it.
//
// Non-localize: the base VMT and its textures are copied preserving the materials/ hierarchy, and
// the patch's include line is left untouched (it still resolves). Returns "" (no include rewrite).
//
// Localize: the base VMT and its textures are relocated beside the patch VMT (same flat layout as
// the patch's own textures); the base VMT's texture refs are rewritten to the new locations and it
// is written serialized. Returns the new include value (game-relative, with "materials/" + ".vmt")
// so the caller can rewrite the patch's include line.
static std::string bring_patch_base(
    const KvNode&                shader_node,
    const std::vector<fs::path>& search_paths,
    const fs::path&              dest_dir,
    bool                         localize,
    const std::string&           mp_dir,     // patch VMT's dir relative to materials/
    std::set<std::string>&       done_vmts,
    std::set<std::string>&       done_vtfs,
    int&                         copied) {

    const KvNode* inc = kv_find(shader_node.children, "include");
    if (!inc || inc->value.empty()) return {};

    const std::string base_mp = include_to_matpath(inc->value);
    if (base_mp.empty()) return {};

    auto base_src = find_file(search_paths, "materials/" + base_mp + ".vmt");
    if (!base_src) {
        std::cout << "[ModelTool] Patch base VMT not found: " << base_mp << ".vmt\n";
        return {};
    }

    if (!localize) {
        // Copy the base VMT and its textures preserving the materials/ hierarchy.
        if (done_vmts.count(base_mp)) return {};
        done_vmts.insert(base_mp);

        if (copy_file_safe(*base_src, dest_dir / "materials" / (base_mp + ".vmt"))) {
            ++copied;
            std::cout << "[ModelTool] Copied base VMT: " << base_mp << ".vmt\n";
        }
        auto base_kv = kv_parse(read_text(*base_src));
        if (!base_kv.children.empty()) {
            std::vector<std::string> base_refs;
            collect_tex_refs(base_kv.children[0].children, base_refs);
            for (const auto& tv : base_refs) {
                const std::string tex = norm(tv);
                copy_one_vtf(tex, tex, search_paths, dest_dir, done_vtfs, copied);
            }
        }
        return {};
    }

    // --- Localize: relocate the base VMT and its textures beside the patch VMT ---
    const std::string base_stem    = fs::path(base_mp).stem().generic_string();
    const std::string base_dest_mp = mp_dir.empty() ? base_stem : mp_dir + "/" + base_stem;
    const std::string new_include  = "materials/" + base_dest_mp + ".vmt";

    if (done_vmts.count(base_dest_mp)) return new_include;  // already written for this folder
    done_vmts.insert(base_dest_mp);

    auto base_kv = kv_parse(read_text(*base_src));
    if (base_kv.children.empty()) return new_include;

    auto& base_shader = base_kv.children[0];
    std::vector<KvNode> base_children = base_shader.children;

    // Relocate the base's textures beside the patch VMT and rewrite the base's refs to match.
    std::vector<std::string> base_refs;
    collect_tex_refs(base_children, base_refs);
    std::unordered_map<std::string, std::string> remap;
    for (const auto& tv : base_refs) {
        const std::string tex = norm(tv);
        auto vtf_src = find_file(search_paths, "materials/" + tex + ".vtf");
        if (!vtf_src) vtf_src = find_file(search_paths, "materials/" + tex);
        if (!vtf_src) { std::cout << "[ModelTool] VTF not found: " << tex << "\n"; continue; }

        const std::string stem    = vtf_src->stem().generic_string();
        const std::string new_tex = mp_dir.empty() ? stem : mp_dir + "/" + stem;
        if (new_tex != tex) remap[tv] = new_tex;
        copy_one_vtf(tex, new_tex, search_paths, dest_dir, done_vtfs, copied);
    }
    if (!remap.empty()) update_tex_values(base_children, remap);

    // Write the relocated base VMT.
    const fs::path base_dest = dest_dir / "materials" / (base_dest_mp + ".vmt");
    std::error_code ec;
    fs::create_directories(base_dest.parent_path(), ec);
    std::ofstream ofs(base_dest);
    if (ofs) {
        ofs << kv_serialize(base_shader.key, base_children);
        ++copied;
        std::cout << "[ModelTool] Localized base VMT: " << base_dest_mp << ".vmt\n";
    } else {
        std::cerr << "[ModelTool] Warning: cannot write base VMT: " << base_dest.string() << "\n";
    }

    return new_include;
}

} // namespace

int copy_materials(
    const std::vector<std::string>&           material_paths,
    const std::vector<fs::path>&              search_paths,
    const fs::path&                           dest_dir,
    const MaterialCopyOptions&                opts) {

    int copied = 0;
    std::set<std::string> done_vmts;
    std::set<std::string> done_vtfs;

    for (const auto& mat_path : material_paths) {
        std::string mp = norm(mat_path);

        // Strip a leading "materials/" prefix if the MDL's cdmaterials already included it.
        // The search below always prepends "materials/" itself.
        {
            std::string mp_l = to_lower(mp);
            if (mp_l.size() > 10 && mp_l.substr(0, 10) == "materials/")
                mp = mp.substr(10);
        }

        if (done_vmts.count(mp)) continue;
        done_vmts.insert(mp);

        // Locate the VMT
        auto vmt_src = find_file(search_paths, "materials/" + mp + ".vmt");
        if (!vmt_src) {
            std::cout << "[ModelTool] VMT not found: " << mp << "\n";
            continue;
        }

        auto kv = kv_parse(read_text(*vmt_src));
        if (kv.children.empty()) {
            std::cout << "[ModelTool] Empty/unparseable VMT: " << mp << "\n";
            continue;
        }

        auto& shader_node = kv.children[0];
        std::string shader = shader_node.key;
        const fs::path dest_vmt = dest_dir / "materials" / (mp + ".vmt");
        std::vector<KvNode> active_children = shader_node.children;

        // Track how the VMT will be written: serialize from KvNode or copy the source file.
        bool needs_serialize = false;
        const bool is_patch  = (to_lower(shader) == "patch");
        bool flattened       = false;
        std::string vmt_log;  // message printed after a successful write

        // --- Flat-patch flattening ---
        if (opts.flat_patch && is_patch) {
            const KvNode* inc = kv_find(shader_node.children, "include");
            if (inc && !inc->value.empty()) {
                std::string inc_path = norm(inc->value);
                auto base_src = find_file(search_paths, inc_path);
                if (!base_src) {
                    if (to_lower(inc_path.size() >= 10 ? inc_path.substr(0, 10) : "") != "materials/")
                        base_src = find_file(search_paths, "materials/" + inc_path);
                }
                if (base_src) {
                    auto base_kv = kv_parse(read_text(*base_src));
                    if (!base_kv.children.empty()) {
                        shader = base_kv.children[0].key;
                        std::vector<KvNode> replace_kv, insert_kv;
                        for (const auto& c : shader_node.children) {
                            const auto kl = to_lower(c.key);
                            if (kl == "replace")     replace_kv = c.children;
                            else if (kl == "insert") insert_kv  = c.children;
                        }
                        active_children = merge_patch(base_kv.children[0].children, replace_kv, insert_kv);
                        needs_serialize = true;
                        flattened       = true;
                        vmt_log = "[ModelTool] Flattened Patch VMT: " + mp + ".vmt";
                    }
                } else {
                    std::cout << "[ModelTool] Patch base VMT not found (" << inc_path << "), copying as-is.\n";
                }
            }
            if (!flattened)
                vmt_log = "[ModelTool] Copied VMT (Patch): " + mp + ".vmt";
        } else if (is_patch) {
            vmt_log = "[ModelTool] Copied VMT (Patch): " + mp + ".vmt";
        } else {
            vmt_log = "[ModelTool] Copied VMT: " + mp + ".vmt";
        }

        // --- Collect VTF refs ---
        std::vector<std::string> tex_refs;
        collect_tex_refs(active_children, tex_refs);

        // This VMT's directory relative to materials/ - where localize places its files.
        const std::string mp_dir = fs::path(mp).parent_path().generic_string();

        // --- Localize: rewrite texture paths to point to the VMT's own directory ---
        // VTFs land next to the VMT, so we update the values in the KV tree before writing.
        if (opts.localize) {
            std::unordered_map<std::string, std::string> remap;
            for (const auto& tv : tex_refs) {
                const std::string tex = norm(tv);
                auto vsrc = find_file(search_paths, "materials/" + tex + ".vtf");
                if (!vsrc) vsrc = find_file(search_paths, "materials/" + tex);
                if (!vsrc) continue;
                // New value: "<vmts_dir>/<vtf_stem>", relative to materials/
                std::string stem = vsrc->stem().generic_string();
                std::string new_val = mp_dir.empty() ? stem : mp_dir + "/" + stem;
                if (new_val != tex) remap[tv] = new_val;
            }
            if (!remap.empty()) {
                update_tex_values(active_children, remap);
                needs_serialize = true;
            }
        }

        // --- Unflattened Patch: bring its included base VMT (and the base's textures) along.
        // When localizing, the base + its textures are relocated beside the patch VMT and the
        // include line is rewritten; otherwise everything is copied preserving the hierarchy. ---
        if (is_patch && !flattened) {
            std::string new_inc = bring_patch_base(
                shader_node, search_paths, dest_dir, opts.localize, mp_dir,
                done_vmts, done_vtfs, copied);
            if (!new_inc.empty()) {
                if (KvNode* inc_node = kv_find(active_children, "include")) {
                    inc_node->value = new_inc;
                    needs_serialize = true;
                }
            }
        }

        // --- Write the VMT ---
        bool vmt_ok = false;
        if (needs_serialize) {
            const std::string out_kv = kv_serialize(shader, active_children);
            std::error_code ec;
            fs::create_directories(dest_vmt.parent_path(), ec);
            std::ofstream ofs(dest_vmt);
            if (ofs) { ofs << out_kv; vmt_ok = true; }
            else std::cerr << "[ModelTool] Warning: cannot write VMT: " << dest_vmt.string() << "\n";
        } else {
            vmt_ok = copy_file_safe(*vmt_src, dest_vmt);
        }
        if (vmt_ok) {
            ++copied;
            std::cout << vmt_log << "\n";
        }

        // --- Copy VTFs ---
        for (const auto& tex_val : tex_refs) {
            const std::string tex = norm(tex_val);
            if (done_vtfs.count(tex)) continue;

            auto vtf_src = find_file(search_paths, "materials/" + tex + ".vtf");
            if (!vtf_src) vtf_src = find_file(search_paths, "materials/" + tex);
            if (!vtf_src) {
                std::cout << "[ModelTool] VTF not found: " << tex << "\n";
                continue;
            }

            fs::path dest_vtf;
            if (opts.localize) {
                // Place the VTF beside its VMT
                dest_vtf = dest_vmt.parent_path() / vtf_src->filename();
            } else {
                // Preserve the game's materials/ hierarchy
                dest_vtf = dest_dir / "materials" / (tex + ".vtf");
            }

            if (copy_file_safe(*vtf_src, dest_vtf)) {
                done_vtfs.insert(tex);
                ++copied;
                std::cout << "[ModelTool] Copied VTF: " << tex << "\n";
            }
        }
    }

    return copied;
}
