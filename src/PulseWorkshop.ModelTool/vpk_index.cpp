#include "vpk_index.h"
#include <algorithm>
#include <cctype>
#include <iostream>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace fs = std::filesystem;

namespace {

// Normalize a VPK entry (or a query path) for case-insensitive comparison:
// trim whitespace/CR, backslashes -> forward slashes, no leading slash, lowercase.
static std::string norm_entry(std::string s) {
    while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' ' || s.back() == '\t'))
        s.pop_back();
    size_t start = 0;
    while (start < s.size() && (s[start] == ' ' || s[start] == '\t')) ++start;
    s = s.substr(start);
    std::replace(s.begin(), s.end(), '\\', '/');
    while (!s.empty() && s.front() == '/') s.erase(s.begin());
    std::transform(s.begin(), s.end(), s.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

// Run "<exe> l <vpk>" and capture its stdout. The working directory is set to the exe's own
// folder so vpk.exe finds its side-by-side DLLs (tier0.dll, vstdlib.dll, ...). stderr is
// merged into the same pipe - any diagnostics become lines that simply never match a path.
static std::string run_vpk_list(const fs::path& exe, const fs::path& vpk) {
    std::string out;

    SECURITY_ATTRIBUTES sa{};
    sa.nLength        = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE read_h = nullptr, write_h = nullptr;
    if (!CreatePipe(&read_h, &write_h, &sa, 0))
        return out;
    SetHandleInformation(read_h, HANDLE_FLAG_INHERIT, 0);

    std::wstring cmd = L"\"" + exe.wstring() + L"\" l \"" + vpk.wstring() + L"\"";
    std::vector<wchar_t> cmd_buf(cmd.begin(), cmd.end());
    cmd_buf.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb         = sizeof(si);
    si.dwFlags    = STARTF_USESTDHANDLES;
    si.hStdOutput = write_h;
    si.hStdError  = write_h;
    si.hStdInput  = nullptr;

    PROCESS_INFORMATION pi{};
    const std::wstring workdir = exe.parent_path().wstring();

    BOOL ok = CreateProcessW(nullptr, cmd_buf.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW, nullptr,
                             workdir.empty() ? nullptr : workdir.c_str(), &si, &pi);
    CloseHandle(write_h);
    if (!ok) {
        CloseHandle(read_h);
        std::cerr << "[ModelTool] Warning: failed to launch vpk.exe (" << GetLastError() << ")\n";
        return out;
    }

    char  chunk[8192];
    DWORD got = 0;
    while (ReadFile(read_h, chunk, sizeof(chunk), &got, nullptr) && got > 0)
        out.append(chunk, got);
    CloseHandle(read_h);

    WaitForSingleObject(pi.hProcess, INFINITE);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return out;
}

} // namespace

bool VpkIndex::contains(const std::string& rel_path) {
    if (!enabled())
        return false;
    if (!built_)
        build();
    return entries_.count(norm_entry(rel_path)) > 0;
}

void VpkIndex::build() {
    built_ = true;
    for (const auto& vpk : vpk_files_) {
        const std::string listing = run_vpk_list(vpk_exe_, vpk);
        size_t pos = 0;
        while (pos < listing.size()) {
            size_t nl   = listing.find('\n', pos);
            size_t len  = (nl == std::string::npos ? listing.size() : nl) - pos;
            std::string entry = norm_entry(listing.substr(pos, len));
            if (!entry.empty())
                entries_.insert(std::move(entry));
            if (nl == std::string::npos) break;
            pos = nl + 1;
        }
    }
    std::cout << "[ModelTool] Indexed " << entries_.size() << " entries from "
              << vpk_files_.size() << " game VPK(s) for the native-file check.\n";
}
