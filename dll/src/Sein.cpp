// ============================================================
// Sein — 贊恩 (僧侶・記錄者 — Priest, Chronicler)
// Logger: 5-category per-process file logging with rotation
// ============================================================

#include "Sein.h"
#include "Grimoire.h"
#include "BuildStamp.h"

#include <Windows.h>
#include <ShlObj.h>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <mutex>
#include <filesystem>
#include <chrono>
#include <iomanip>
#include <sstream>
#include <vector>
#include <utility>

namespace fs = std::filesystem;

namespace Sein {

// ================================================================
// Log file categories
// ================================================================

enum LogFile : uint8_t {
    LF_Init = 0,   // init.log    — INIT, CEP, SUMMARY
    LF_Scan,        // scan.log    — SCAN:*, MEM
    LF_Offsets,     // offsets.log — DYNO:*, OARR, FNAM
    LF_Pipe,        // pipe.log    — PIPE:*
    LF_Walk,        // walk.log    — WALK:*
    LF_COUNT
};

static const wchar_t* s_fileNames[LF_COUNT] = {
    L"init", L"scan", L"offsets", L"pipe", L"walk"
};

// ================================================================
// Category → file routing (prefix-match, longest first)
// ================================================================

struct CatMapping {
    const char* prefix;
    uint8_t     prefixLen;
    LogFile     file;
};

// Sorted longest-prefix-first for correct matching
static const CatMapping s_catMap[] = {
    { "WALK:StructP",  12, LF_Walk    },
    { "WALK:ArrayP",   11, LF_Walk    },
    { "PIPE:world",    10, LF_Pipe    },
    { "PIPE:watch",    10, LF_Pipe    },
    { "DYNO:Enum",      9, LF_Offsets },
    { "SCAN:GObj",      8, LF_Scan    },
    { "SCAN:GNam",      8, LF_Scan    },
    { "SCAN:GWld",      8, LF_Scan    },
    { "SCAN:Ver",       8, LF_Scan    },
    { "PIPE:svr",       8, LF_Pipe    },
    { "PIPE:cmd",       8, LF_Pipe    },
    { "SUMMARY",        7, LF_Init    },
    { "INIT",           4, LF_Init    },
    { "SCAN",           4, LF_Scan    },
    { "DYNO",           4, LF_Offsets },
    { "OARR",           4, LF_Offsets },
    { "FNAM",           4, LF_Offsets },
    { "WALK",           4, LF_Walk    },
    { "FLY",            3, LF_Walk    },   // Dunste fly worker (movement-related)
    { "PIPE",           4, LF_Pipe    },
    { "CEP",            3, LF_Init    },
    { "MEM",            3, LF_Scan    },
};

static LogFile ResolveFile(const char* cat) {
    if (!cat || cat[0] == '\0') return LF_Init;
    for (const auto& m : s_catMap) {
        if (strncmp(cat, m.prefix, m.prefixLen) == 0) return m.file;
    }
    return LF_Init;  // fallback: unknown categories go to init.log
}

// ================================================================
// Per-file state
// ================================================================

struct LogFileState {
    FILE*          file    = nullptr;
    size_t         written = 0;
    fs::path       currentPath;
    std::wstring   baseName;
};

static std::mutex     s_mutex;
static LogFileState   s_files[LF_COUNT];
static bool           s_filesOpen   = false;
static fs::path       s_logDir;                 // base: %LOCALAPPDATA%\UE5CEDumper\Logs
static fs::path       s_processDir;             // per-process subfolder

// Early buffering: lines logged before InitProcessMirror opens files
static std::vector<std::pair<LogFile, std::string>> s_earlyBuffer;
static bool           s_buffering   = true;
static constexpr size_t EARLY_BUFFER_MAX = 100;

// ================================================================
// Helpers
// ================================================================

static fs::path GetLogDirectory() {
    wchar_t* appdata = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &appdata))) {
        fs::path dir = fs::path(appdata) / Grimoire::LOG_FOLDER_NAME / Grimoire::LOG_SUBFOLDER;
        CoTaskMemFree(appdata);
        return dir;
    }
    return fs::path(L".");
}

static std::string GetTimestamp() {
    // Use the Win32 GetLocalTime() API, NOT the CRT localtime_s/std::put_time
    // path. The CRT path triggers __tzset() + locale-facet init on first use,
    // which dereferences not-yet-ready CRT/process global state and AVs when
    // this DLL is loaded EARLY in a host EXE's static-import graph (its DllMain
    // runs before the process is "warm"). Concrete failure: hijacking dxgi.dll
    // on Octopath Traveler — dxgi is imported before d3d11, so our DllMain's
    // very first log line faulted inside __tzset (write to null+0x24). The
    // version.dll/dinput8.dll proxies never hit it only because they load late.
    // GetLocalTime reads the system clock + timezone via ntdll with zero CRT
    // global dependency, so it is safe at any load time.
    SYSTEMTIME st;
    GetLocalTime(&st);

    char buf[32];
    snprintf(buf, sizeof(buf), "%04u-%02u-%02u %02u:%02u:%02u.%03u",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute,
             st.wSecond, st.wMilliseconds);
    return std::string(buf);
}

// ================================================================
// Retention — archive by timestamp, then prune by age
// ================================================================
//
// The scheme here is:
//
//   <base>-0.log                 ALWAYS the current run. Readers, docs and the UI
//                                depend on this, so it is deliberately unchanged.
//   <base>-YYYYMMDD-HHMMSS.log   an archived earlier run
//   anything older than Grimoire::LOG_RETENTION_DAYS is deleted at startup
//
// The archive name is stamped from the file's OWN last-write time, never from
// "now". That is what makes the age prune honest — a log written a month ago but
// archived today still prunes on its real date. Stamping with the archive time
// would silently resurrect stale data for another full retention window.
//
// Why not the old generation shuffle: it ran on EVERY process start, not only on
// size, so N launches of one game in an afternoon evicted everything before them
// no matter how recent. An age policy cannot be expressed as a file count.

static bool FileWriteTime(const fs::path& p, FILETIME& out) {
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &fad)) return false;
    out = fad.ftLastWriteTime;
    return true;
}

static ULONGLONG AsU64(const FILETIME& ft) {
    ULARGE_INTEGER u;
    u.LowPart  = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    return u.QuadPart;
}

// FILETIME ticks are 100 ns, so one day is 24*60*60*10^7.
static constexpr ULONGLONG kTicksPerDay = 864000000000ULL;

static ULONGLONG RetentionTicks() {
    return static_cast<ULONGLONG>(Grimoire::LOG_RETENTION_DAYS) * kTicksPerDay;
}

// "YYYYMMDD-HHMMSS" in LOCAL time. Uses the Win32 conversion rather than the CRT
// for the same reason GetTimestamp() does — see the comment there.
static std::wstring StampFor(const FILETIME& ft) {
    FILETIME local{};
    SYSTEMTIME st{};
    if (!FileTimeToLocalFileTime(&ft, &local)) return std::wstring();
    if (!FileTimeToSystemTime(&local, &st))    return std::wstring();
    wchar_t buf[32];
    swprintf(buf, 32, L"%04u%02u%02u-%02u%02u%02u",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return std::wstring(buf);
}

// Rename `src` to <base>-<its own mtime>.log. On any failure the file is removed
// rather than left behind: an un-archivable log would otherwise be invisible to
// the age prune (which keys on the dated name's mtime) and accumulate forever.
static void ArchiveByWriteTime(const fs::path& src, const fs::path& dir,
                               const wchar_t* baseName) {
    std::error_code ec;
    if (!fs::exists(src, ec)) return;

    FILETIME ft{};
    std::wstring stamp;
    if (FileWriteTime(src, ft)) stamp = StampFor(ft);
    if (stamp.empty()) { fs::remove(src, ec); return; }

    // Two rotations inside the same second are possible on a busy category
    // (8 MB fills fast during a scan), so disambiguate rather than overwrite.
    for (int dup = 0; dup < 100; ++dup) {
        std::wstring name = std::wstring(baseName) + L"-" + stamp;
        if (dup > 0) name += L"-" + std::to_wstring(dup);
        name += L".log";
        auto dst = dir / name;
        if (!fs::exists(dst, ec)) {
            fs::rename(src, dst, ec);
            if (!ec) return;
            break;                 // rename failed (locked?) — fall through to remove
        }
    }
    fs::remove(src, ec);
}

// Delete archived logs past the retention window. Only touches *.log, and never
// the live -0.log (callers archive that first, so it does not exist here).
// One error_code for ITERATION, a fresh one per filesystem call. Sharing a single ec
// let a failed fs::remove poison the loop's own `if (ec) break`: the first undeletable
// entry ended the sweep — and because enumeration order is stable, it ended it at the
// SAME entry on every launch, so the advertised 21-day retention silently stopped
// applying past that point forever. One locked file is enough to do that. (B19)
static void PruneAgedLogs(const fs::path& dir) {
    std::error_code iterEc;
    FILETIME nowFt{};
    GetSystemTimeAsFileTime(&nowFt);
    const ULONGLONG now    = AsU64(nowFt);
    const ULONGLONG maxAge = RetentionTicks();

    for (auto& entry : fs::directory_iterator(dir, iterEc)) {
        if (iterEc) break;
        std::error_code ec;
        if (!entry.is_regular_file(ec)) continue;
        if (entry.path().extension() != L".log") continue;

        FILETIME ft{};
        if (!FileWriteTime(entry.path(), ft)) continue;
        const ULONGLONG t = AsU64(ft);
        // A failure here is per-file and must not stop the sweep.
        if (now > t && (now - t) > maxAge) fs::remove(entry.path(), ec);
    }
}

// One-time migration of the pre-retention numbered generations (-1 .. -9).
// Without this they orphan: nothing rotates them any more, and the age prune
// would only reach them once they aged out on their own.
static void MigrateLegacyGenerations(const fs::path& dir, const wchar_t* baseName) {
    std::error_code ec;
    for (int i = 1; i <= 9; ++i) {
        auto legacy = dir / (std::wstring(baseName) + L"-" + std::to_wstring(i) + L".log");
        if (fs::exists(legacy, ec)) ArchiveByWriteTime(legacy, dir, baseName);
    }
}

static void RotateIfNeeded(LogFileState& fs_state) {
    if (fs_state.written < Grimoire::LOG_MAX_SIZE) return;

    fflush(fs_state.file);
    fclose(fs_state.file);
    fs_state.file = nullptr;

    ArchiveByWriteTime(fs_state.currentPath, fs_state.currentPath.parent_path(),
                       fs_state.baseName.c_str());

    fs_state.file = _wfopen(fs_state.currentPath.c_str(), L"w");
    fs_state.written = 0;

    if (fs_state.file) {
        auto ts = GetTimestamp();
        int n = fprintf(fs_state.file, "[%s] [INFO] [INIT] Log rotated | build: %s\n",
                        ts.c_str(), BuildStamp::VersionString());
        if (n > 0) fs_state.written += static_cast<size_t>(n);
        fflush(fs_state.file);
    }
}

// ================================================================
// Init / open / close files
// ================================================================

static bool OpenFileInDir(LogFileState& fs_state, const fs::path& dir,
                          const wchar_t* baseName) {
    fs_state.baseName    = baseName;
    fs_state.currentPath = dir / (std::wstring(baseName) + L"-0.log");

    MigrateLegacyGenerations(dir, baseName);
    ArchiveByWriteTime(fs_state.currentPath, dir, baseName);   // last run -> dated archive

    fs_state.file = _wfopen(fs_state.currentPath.c_str(), L"w");
    if (!fs_state.file) return false;
    fs_state.written = 0;

    auto ts = GetTimestamp();
    int n = fprintf(fs_state.file, "[%s] [INFO] [INIT] Logger started | build: %s\n",
                    ts.c_str(), BuildStamp::VersionString());
    if (n > 0) fs_state.written += static_cast<size_t>(n);
    fflush(fs_state.file);
    return true;
}

static void CloseFile(LogFileState& fs_state) {
    if (fs_state.file) {
        fflush(fs_state.file);
        fclose(fs_state.file);
        fs_state.file = nullptr;
    }
}

// Write a pre-formatted line to a file
static void WriteToFile(LogFileState& fs_state, const char* line) {
    if (!fs_state.file) return;
    RotateIfNeeded(fs_state);
    // RotateIfNeeded RETURNS VOID and can leave `file` null: it closes the old handle
    // unconditionally, and the truncating reopen can fail (disk full — the 21-day
    // retention has no size cap — or a viewer holding the file open). fprintf on a NULL
    // FILE* hits the UCRT invalid-parameter handler, which terminates the INJECTED GAME
    // with no message. Logging is best-effort; the game is not. (B11)
    if (!fs_state.file) return;
    int written = fprintf(fs_state.file, "%s\n", line);
    if (written > 0) fs_state.written += static_cast<size_t>(written);
    fflush(fs_state.file);
}

// ================================================================
// Process folder cleanup
// ================================================================

// Age-based, matching the file policy: a per-process folder goes when NOTHING in
// it has been written for LOG_RETENTION_DAYS. Judging by the newest file inside
// (not the folder's own mtime, which Windows does not reliably bump on writes to
// existing children) is also what makes this safe while another game is running:
// a live folder's files are seconds old, so it can never be selected.
//
// `keep` is the folder this process owns and is never removed, even in the
// pathological case of a system clock jump.
static void PruneStaleProcessFolders(const fs::path& parentDir, const fs::path& keep) {
    std::error_code iterEc;
    FILETIME nowFt{};
    GetSystemTimeAsFileTime(&nowFt);
    const ULONGLONG now    = AsU64(nowFt);
    const ULONGLONG maxAge = RetentionTicks();

    // Same per-entry error_code discipline as PruneAgedLogs: an undeletable folder
    // (a game still running, a viewer holding a file) must cost that folder, not the
    // rest of the sweep. The old `ec.clear()` sat BEFORE both remove_all calls, so it
    // could not undo the failure that actually broke the loop. (B19)
    for (auto& entry : fs::directory_iterator(parentDir, iterEc)) {
        if (iterEc) break;
        std::error_code ec;
        if (!entry.is_directory(ec)) continue;
        if (fs::equivalent(entry.path(), keep, ec)) continue;

        ULONGLONG newest = 0;
        bool sawFile = false;
        std::error_code subEc;
        for (auto& sub : fs::directory_iterator(entry.path(), subEc)) {
            if (subEc) break;
            FILETIME ft{};
            if (!FileWriteTime(sub.path(), ft)) continue;
            sawFile = true;
            const ULONGLONG t = AsU64(ft);
            if (t > newest) newest = t;
        }
        // Could not even enumerate it — leave it alone rather than guess it is empty.
        if (subEc) continue;

        std::error_code rmEc;
        // An empty folder is removable immediately; there is nothing to retain.
        if (!sawFile) { fs::remove_all(entry.path(), rmEc); continue; }
        if (now > newest && (now - newest) > maxAge) fs::remove_all(entry.path(), rmEc);
    }
}

// ================================================================
// Core write functions
// ================================================================

static void WriteLog(const char* level, const char* cat, const char* fmt, va_list args) {
    std::lock_guard<std::mutex> lock(s_mutex);

    auto ts = GetTimestamp();
    char msgBuf[4096];
    vsnprintf(msgBuf, sizeof(msgBuf), fmt, args);

    char lineBuf[4352];
    if (cat && cat[0] != '\0') {
        snprintf(lineBuf, sizeof(lineBuf), "[%s] [%s] [%s] %s", ts.c_str(), level, cat, msgBuf);
    } else {
        snprintf(lineBuf, sizeof(lineBuf), "[%s] [%s] %s", ts.c_str(), level, msgBuf);
    }

    LogFile target = ResolveFile(cat);

    if (s_filesOpen) {
        WriteToFile(s_files[target], lineBuf);
    } else if (s_buffering && s_earlyBuffer.size() < EARLY_BUFFER_MAX) {
        s_earlyBuffer.emplace_back(target, std::string(lineBuf));
    }
}

static void WriteSummary(const char* fmt, va_list args) {
    std::lock_guard<std::mutex> lock(s_mutex);

    auto ts = GetTimestamp();
    char msgBuf[4096];
    vsnprintf(msgBuf, sizeof(msgBuf), fmt, args);

    char lineBuf[4352];
    snprintf(lineBuf, sizeof(lineBuf), "[%s] [SUMMARY] %s", ts.c_str(), msgBuf);

    if (s_filesOpen) {
        WriteToFile(s_files[LF_Init], lineBuf);
    } else if (s_buffering && s_earlyBuffer.size() < EARLY_BUFFER_MAX) {
        s_earlyBuffer.emplace_back(LF_Init, std::string(lineBuf));
    }
}

// ================================================================
// Public API
// ================================================================

bool Init() {
    std::lock_guard<std::mutex> lock(s_mutex);

    s_logDir = GetLogDirectory();
    std::error_code ec;
    fs::create_directories(s_logDir, ec);

    // Enable early buffering — files will be opened in InitProcessMirror
    s_buffering = true;
    s_earlyBuffer.clear();
    s_earlyBuffer.reserve(EARLY_BUFFER_MAX);
    s_filesOpen = false;

    return true;
}

void InitProcessMirror(const std::wstring& processName) {
    std::lock_guard<std::mutex> lock(s_mutex);

    if (processName.empty()) return;

    // Sanitize process name
    std::wstring safeName = processName;
    auto dotPos = safeName.rfind(L'.');
    if (dotPos != std::wstring::npos) safeName = safeName.substr(0, dotPos);
    for (wchar_t& c : safeName) {
        if (c == L'/' || c == L'\\' || c == L':' || c == L'*' ||
            c == L'?' || c == L'"' || c == L'<' || c == L'>' || c == L'|') {
            c = L'_';
        }
    }

    s_processDir = s_logDir / safeName;
    std::error_code ec;
    fs::create_directories(s_processDir, ec);
    if (ec) return;

    // Open all 5 category files. Each archives its own previous -0.log first.
    for (int i = 0; i < LF_COUNT; ++i) {
        OpenFileInDir(s_files[i], s_processDir, s_fileNames[i]);
    }
    s_filesOpen = true;

    // Flush early buffer to the correct files
    s_buffering = false;
    for (auto& [target, line] : s_earlyBuffer) {
        WriteToFile(s_files[target], line.c_str());
    }
    s_earlyBuffer.clear();
    s_earlyBuffer.shrink_to_fit();

    // Retention sweep. Runs AFTER the files are open, so the live -0.log of every
    // category already exists and the archives are the only *.log left to age out.
    PruneAgedLogs(s_processDir);
    PruneStaleProcessFolders(s_logDir, s_processDir);
}

void Shutdown() {
    std::lock_guard<std::mutex> lock(s_mutex);
    for (int i = 0; i < LF_COUNT; ++i) {
        CloseFile(s_files[i]);
    }
    s_filesOpen = false;
    s_buffering = false;
    s_earlyBuffer.clear();
}

void SetChannel(LogChannel /*ch*/) {
    // No-op: category routing replaces channel switching
}

LogChannel GetChannel() {
    return LogChannel::Scan;  // No-op
}

void Info(const char* cat, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    WriteLog("INFO", cat, fmt, args);
    va_end(args);
}

void Error(const char* cat, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    WriteLog("ERROR", cat, fmt, args);
    va_end(args);
}

void Warn(const char* cat, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    WriteLog("WARN", cat, fmt, args);
    va_end(args);
}

void Debug(const char* cat, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    WriteLog("DEBUG", cat, fmt, args);
    va_end(args);
}

void Summary(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    WriteSummary(fmt, args);
    va_end(args);
}

} // namespace Sein
