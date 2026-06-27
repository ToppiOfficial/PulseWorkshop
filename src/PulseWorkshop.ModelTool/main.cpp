#include <cstring>
#include <filesystem>
#include <iostream>
#include "gameinfo.h"
#include "material_copy.h"
#include "mdl_reader.h"

static void print_usage() {
    std::cout <<
        "PulseWorkshop.ModelTool - Source model tool\n"
        "Usage:\n"
        "  materials <mdl_path> <gameinfo_txt> <dest_dir> [--localize] [--flat-patch]\n"
        "\n"
        "Options:\n"
        "  --localize     Place VTFs beside their VMT instead of preserving the game hierarchy.\n"
        "  --flat-patch   Flatten Patch VMTs into their base shader.\n";
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
    for (int i = 3; i < argc; ++i) {
        if      (std::strcmp(argv[i], "--localize")   == 0) opts.localize   = true;
        else if (std::strcmp(argv[i], "--flat-patch") == 0) opts.flat_patch = true;
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

        int n = copy_materials(mat_paths, search_paths, dest_dir, opts);
        std::cout << "[ModelTool] Done. " << n << " file(s) copied.\n";
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

    std::cerr << "[ModelTool] Unknown subcommand: " << argv[1] << "\n";
    print_usage();
    return 1;
}
