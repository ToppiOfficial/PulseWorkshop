#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <sstream>
#include "gameinfo.h"
#include "material_copy.h"
#include "mdl_reader.h"
#include "model_stats.h"
#include "vpk_index.h"

static void print_usage() {
    std::cout <<
        "PulseWorkshop.ModelTool - Source model tool\n"
        "Usage:\n"
        "  materials <mdl_path> <gameinfo_txt> <dest_dir> [--localize] [--flat-patch] [--vpk-exe <path>]\n"
        "  info <mdl_path>\n"
        "  matdirs <mdl_path>\n"
        "\n"
        "Options:\n"
        "  --localize        Place VTFs beside their VMT instead of preserving the game hierarchy.\n"
        "  --flat-patch      Flatten Patch VMTs into their base shader.\n"
        "  --vpk-exe <path>  The game's vpk.exe. Files missing from the search paths are then\n"
        "                    checked against the game's VPK archives (from gameinfo.txt) and\n"
        "                    reported as natively provided instead of missing when found there.\n";
}

// Thousands-separated integer (e.g. 131824 -> "131,824") so poly/vertex counts read easily.
static std::string with_commas(long long n) {
    std::string digits = std::to_string(n < 0 ? -n : n);
    std::string out;
    int count = 0;
    for (auto it = digits.rbegin(); it != digits.rend(); ++it) {
        if (count && count % 3 == 0) out.push_back(',');
        out.push_back(*it);
        ++count;
    }
    if (n < 0) out.push_back('-');
    std::reverse(out.begin(), out.end());
    return out;
}

// A compact human-readable byte size (e.g. 45056 -> "44 KB"), for the dependency list.
static std::string human_size(std::uintmax_t bytes) {
    const char* units[] = {"B", "KB", "MB", "GB"};
    double v = static_cast<double>(bytes);
    int u = 0;
    while (v >= 1024.0 && u < 3) { v /= 1024.0; ++u; }
    std::ostringstream os;
    if (u == 0) os << bytes << " B";
    else {
        os.setf(std::ios::fixed);
        os.precision(v < 10.0 ? 1 : 0);
        os << v << " " << units[u];
    }
    return os.str();
}

// Prints a clean, human-readable model summary to stdout (one "Label: value" per line, no
// [ModelTool] prefix) so the C# side can show it verbatim in the per-entry info panel.
static int cmd_info(int argc, char* argv[]) {
    if (argc < 1) {
        std::cerr << "[ModelTool] Error: 'info' requires <mdl_path>\n";
        print_usage();
        return 1;
    }

    std::filesystem::path mdl_path = argv[0];
    try {
        MdlInfo   info  = read_mdl_info(mdl_path);
        MeshStats stats = read_mesh_stats(mdl_path);

        // Geometry
        std::cout << "MDL version:  " << info.version << "\n";
        std::cout << "Triangles:    " << (stats.have_vtx ? with_commas(stats.triangles) : "n/a") << "\n";
        std::cout << "Vertices:     " << (stats.have_vvd ? with_commas(stats.vertices)  : "n/a") << "\n";
        std::cout << "Body parts:   " << info.num_bodyparts << "\n";
        std::cout << "Eyeballs:     " << info.num_eyeballs << "\n";
        std::cout << "LODs:         " << (stats.have_vtx ? std::to_string(stats.lods) : "n/a") << "\n";
        // Per-LOD triangle counts (LOD 0 is the full-detail mesh).
        for (size_t i = 0; i < stats.lod_triangles.size(); ++i)
            std::cout << "  LOD " << i << ":       " << with_commas(stats.lod_triangles[i]) << " tris\n";

        // Bones
        std::cout << "\n";
        std::cout << "Bones:            " << info.num_bones << "\n";
        std::cout << "  Jiggle:         " << info.num_jiggle << "\n";
        std::cout << "  Procedural:     " << info.num_procedural << " (driver/helper bones)\n";
        std::cout << "  Controllers:    " << info.num_bone_ctrls << "\n";

        // IK chains
        std::cout << "\n";
        std::cout << "IK chains: " << info.ik_chains.size() << "\n";
        for (const auto& ik : info.ik_chains)
            std::cout << "  - " << (ik.empty() ? "unnamed" : ik) << "\n";

        // Attachments
        std::cout << "\n";
        std::cout << "Attachments: " << info.attachments.size() << "\n";
        for (const auto& at : info.attachments)
            std::cout << "  - " << (at.empty() ? "unnamed" : at) << "\n";

        // Animation - sequences, animation blocks, and $includemodel references
        std::cout << "\n";
        std::cout << "Sequences:    " << info.num_sequences << "\n";
        std::cout << "Animations:   " << info.num_anims << "\n";
        std::cout << "Include models ($includemodel): " << info.include_models.size() << "\n";
        for (const auto& im : info.include_models)
            std::cout << "  - " << im << "\n";

        // Hitbox sets
        std::cout << "\n";
        std::cout << "Hitbox sets: " << info.hitbox_sets.size() << "\n";
        for (const auto& hs : info.hitbox_sets) {
            std::cout << "  - \"" << (hs.name.empty() ? "unnamed" : hs.name) << "\": "
                      << hs.num_hitboxes << " hitbox" << (hs.num_hitboxes == 1 ? "" : "es") << "\n";
        }

        // Collision (.phy) - solids and ragdoll constraints (joints)
        std::cout << "\n";
        if (stats.have_phy) {
            std::cout << "Collision (.phy):  yes\n";
            std::cout << "  Solids:          " << stats.phy_solids << "\n";
            std::cout << "  Constraints:     " << stats.phy_constraints << "\n";
        } else {
            std::cout << "Collision (.phy):  none\n";
        }

        // Flexes (facial/morph data, from the MDL header)
        std::cout << "\n";
        std::cout << "Flexes (morphs):  " << info.num_flex_desc << "\n";
        std::cout << "  Controllers:    " << info.num_flex_ctrls << "\n";
        std::cout << "  Rules:          " << info.num_flex_rules << "\n";

        // Materials
        std::cout << "\n";
        std::cout << "Materials: " << info.materials.size() << "\n";
        for (const auto& mat : info.materials)
            std::cout << "  - " << mat << "\n";

        // Dependencies (only what was actually written - absent siblings are expected, not errors)
        std::cout << "\n";
        std::cout << "Dependencies:\n";
        bool any_dep = false;
        for (const auto& dep : stats.deps) {
            if (dep.exists) {
                std::cout << "  - " << dep.ext << " (" << human_size(dep.size) << ")\n";
                any_dep = true;
            }
        }
        if (!any_dep)
            std::cout << "  (none)\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "[ModelTool] Error: " << e.what() << "\n";
        return 1;
    }
}

static int cmd_materials(int argc, char* argv[]) {
    if (argc < 3) {
        std::cerr << "[ModelTool] Error: 'materials' requires <mdl_path> <gameinfo_txt> <dest_dir>\n";
        print_usage();
        return 1;
    }

    std::filesystem::path mdl_path  = argv[0];
    std::filesystem::path gi_path   = argv[1];
    std::filesystem::path dest_dir  = argv[2];

    MaterialCopyOptions opts;
    std::filesystem::path vpk_exe;
    for (int i = 3; i < argc; ++i) {
        if      (std::strcmp(argv[i], "--localize")   == 0) opts.localize   = true;
        else if (std::strcmp(argv[i], "--flat-patch") == 0) opts.flat_patch = true;
        else if (std::strcmp(argv[i], "--vpk-exe")    == 0 && i + 1 < argc) vpk_exe = argv[++i];
    }

    try {
        std::cout << "[ModelTool] MDL: " << mdl_path.string() << "\n";
        auto mats = read_mdl_materials(mdl_path);
        std::cout << "[ModelTool] " << mats.texture_names.size() << " texture(s), "
                  << mats.cdmaterials.size() << " cdmaterials dir(s).\n";

        auto mat_paths = build_material_paths(mats);
        std::cout << "[ModelTool] " << mat_paths.size() << " material path(s) to look up.\n";

        std::cout << "[ModelTool] Gameinfo: " << gi_path.string() << "\n";
        auto search_paths = get_search_paths(gi_path);
        std::cout << "[ModelTool] " << search_paths.size() << " search path(s).\n";

        // The game's VPKs are only consulted to tell "missing" apart from "shipped with the
        // game" - a file found in a VPK is never copied.
        std::vector<std::filesystem::path> vpk_files;
        if (!vpk_exe.empty()) {
            if (!std::filesystem::exists(vpk_exe)) {
                std::cout << "[ModelTool] vpk.exe not found (" << vpk_exe.string()
                          << ") - VPK check disabled.\n";
                vpk_exe.clear();
            } else {
                vpk_files = get_vpk_paths(gi_path);
                std::cout << "[ModelTool] " << vpk_files.size()
                          << " game VPK archive(s) available for the native-file check.\n";
            }
        }
        VpkIndex vpks(vpk_exe, vpk_files);

        int n = copy_materials(mat_paths, search_paths, dest_dir, opts, vpks);
        std::cout << "[ModelTool] Done. " << n << " file(s) copied.\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "[ModelTool] Error: " << e.what() << "\n";
        return 1;
    }
}

// Prints the distinct material directories a model's textures live in - one per line, relative to
// the game's materials/ folder, forward slashes. Derived from the model's cdmaterials + texture
// names (the same lookup paths the 'materials' command copies from), taking each path's parent
// folder. Used by the app to create the folders under materials/ for a fresh, untextured model.
static int cmd_matdirs(int argc, char* argv[]) {
    if (argc < 1) {
        std::cerr << "[ModelTool] Error: 'matdirs' requires <mdl_path>\n";
        print_usage();
        return 1;
    }

    std::filesystem::path mdl_path = argv[0];
    try {
        auto mats      = read_mdl_materials(mdl_path);
        auto mat_paths = build_material_paths(mats);

        std::vector<std::string> dirs;
        for (const auto& mp : mat_paths) {
            std::string dir = std::filesystem::path(mp).parent_path().generic_string();
            // A cdmaterials entry may itself begin with "materials/"; strip it so the printed
            // path is relative to the game's materials/ folder (the app prepends it back).
            if (dir.size() >= 10) {
                std::string head = dir.substr(0, 10);
                std::transform(head.begin(), head.end(), head.begin(),
                               [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
                if (head == "materials/") dir = dir.substr(10);
            }
            if (dir.empty()) continue; // texture sits directly under materials/ - nothing to create
            if (std::find(dirs.begin(), dirs.end(), dir) == dirs.end())
                dirs.push_back(dir);
        }

        for (const auto& d : dirs)
            std::cout << d << "\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "[ModelTool] Error: " << e.what() << "\n";
        return 1;
    }
}

int main(int argc, char* argv[]) {
    if (argc < 2) { print_usage(); return 1; }

    if (std::strcmp(argv[1], "materials") == 0)
        return cmd_materials(argc - 2, argv + 2);

    if (std::strcmp(argv[1], "info") == 0)
        return cmd_info(argc - 2, argv + 2);

    if (std::strcmp(argv[1], "matdirs") == 0)
        return cmd_matdirs(argc - 2, argv + 2);

    std::cerr << "[ModelTool] Unknown subcommand: " << argv[1] << "\n";
    print_usage();
    return 1;
}
