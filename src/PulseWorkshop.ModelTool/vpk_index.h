#pragma once
#include <filesystem>
#include <string>
#include <unordered_set>
#include <vector>

// Lazy index of the files packed inside a game's VPK archives.
//
// A material that is missing from the loose search paths may still ship inside one of the
// game's VPKs (pak01_dir.vpk etc.) - then it is not truly missing, the game provides it
// natively and nothing needs to be copied. The index is built by running the game's own
// vpk.exe ("vpk l <archive>") once per archive, on the first contains() call only, so runs
// with no missing files never pay for it.
class VpkIndex {
public:
    VpkIndex(std::filesystem::path vpk_exe, std::vector<std::filesystem::path> vpk_files)
        : vpk_exe_(std::move(vpk_exe)), vpk_files_(std::move(vpk_files)) {}

    // True when a vpk.exe and at least one VPK archive are available to check against.
    bool enabled() const { return !vpk_exe_.empty() && !vpk_files_.empty(); }

    // Case-insensitive membership test for a game-relative path (e.g. "materials/x/y.vtf").
    // Always false when the index is disabled.
    bool contains(const std::string& rel_path);

private:
    void build();

    std::filesystem::path              vpk_exe_;
    std::vector<std::filesystem::path> vpk_files_;
    std::unordered_set<std::string>    entries_;
    bool                               built_ = false;
};
