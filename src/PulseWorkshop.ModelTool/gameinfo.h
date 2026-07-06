#pragma once
#include <filesystem>
#include <vector>

// Parse a gameinfo.txt and return ordered material search directories.
// Resolves |gameinfo_path| to the gameinfo.txt parent directory.
// Skips |all_source_engine_paths|, VPK references, and wildcard entries.
// Mirrors intern/game/gameinfo.py: get_game_search_paths().
std::vector<std::filesystem::path> get_search_paths(const std::filesystem::path& gameinfo_txt);

// Parse a gameinfo.txt and return the game's VPK archives from SearchPaths (the entries
// get_search_paths skips), resolved the same way. Entries written without the "_dir"
// suffix are mapped to their "<name>_dir.vpk" directory file. Only archives that exist
// on disk are returned, deduplicated, in SearchPaths order.
std::vector<std::filesystem::path> get_vpk_paths(const std::filesystem::path& gameinfo_txt);
