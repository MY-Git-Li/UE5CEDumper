// ============================================================
// Mimic — 寶箱怪 (經典梗 — The Classic Gag)
// Mailbox: CE Lua shared-memory command interface
//
// Polling thread checks the mailbox every ~1ms (see kPollIntervalMs).
// Uses existing public APIs (UE5_Init, UE5_CallProcessEvent, etc.)
// so no internal changes to GameThreadDispatch are needed.
//
// Thread safety model:
//   CE Lua writes all fields, then writes cmd (trigger) LAST.
//   Polling thread reads cmd, processes, writes status=1 then cmd=0.
//   WriteProcessMemory (CE side) is kernel-serializing.
// ============================================================

#define LOG_CAT "PIPE"
#include "Sein.h"
#include "Mimic.h"
#include "Frieren.h"
#include "Aura.h"
#include "Ubel.h"

#include <Windows.h>
#include <timeapi.h>   // timeBeginPeriod / timeEndPeriod (winmm)

#include <atomic>
#include <cstring>
#include <thread>
#include <algorithm>

// Forward declarations for ExportAPI symbols (must be outside namespace)
extern "C" bool     UE5_Init();
extern "C" int32_t  UE5_CallProcessEvent(uintptr_t, uintptr_t, uintptr_t);
extern "C" int32_t  UE5_CallProcessEventDirect(uintptr_t, uintptr_t, uintptr_t);
extern uintptr_t    g_cachedGObjects;
extern uintptr_t    g_cachedGNames;

// UE FunctionFlags subset we care about for the static-native fast path.
// Pulled from Engine/Source/Runtime/CoreUObject/Public/UObject/Script.h.
// Only the two flags below are read; defining locally avoids dragging the
// full enum into Mimic just for two bit checks.
static constexpr uint32_t kFuncFlag_Native = 0x00000400u;
static constexpr uint32_t kFuncFlag_Static = 0x00002000u;

// The exported mailbox — zero-initialized by default
extern "C" __declspec(dllexport) Mimic::MailboxData g_invokeMailbox = {};

namespace Mimic {

// Polling thread state
static std::atomic<bool> s_running{false};
static HANDLE s_hThread = nullptr;

// Mailbox poll interval. CE Lua's invokeUFunction blocks waiting for the
// status flag to flip — every ms shaved off this interval is shaved off the
// per-invoke wall-clock latency. Was 10ms historically; lowered to 1ms so a
// tight Lua loop of N invokes doesn't accumulate (N × ~5ms) of pure idle
// time inside the polling loop. CPU cost at idle: one thread ticks 1000x/sec
// doing a handful of volatile reads — negligible. The polling thread bumps
// the Windows timer resolution to 1ms via timeBeginPeriod so Sleep(1) really
// delivers ~1ms on the few systems that still default to the legacy 15.6ms
// system tick (Win10 2004+ scopes this to our process; earlier systems pay
// a small global cost but the DLL is only loaded into game processes that
// almost always already request 1ms resolution for their own frame timing).
static constexpr UINT kPollIntervalMs = 1;

// Audit fix #10: depth counter for compound multi-step operations
// (HandleInvokeByName chains three sub-handlers). When > 0, sub-handlers'
// SetDone/SetError write `result` and `errorMsg` only — they do NOT touch
// `status` or `cmd`, which would prematurely signal completion to CE
// between sub-steps. The outer compound op publishes the final state via
// CompoundOpGuard's destructor.
static int s_compoundDepth = 0;

// Forward declarations
static void HandleFindInstance();
static void HandleFindFunction();
static void HandleInvoke();
static void HandleInvokeByName();
static void HandleListFunctions();
static void HandleListInstances();
static void SetError(int32_t code, const char* msg);
static void SetDone(int32_t resultCode);
static bool EnsureInitialized();

// RAII guard for compound operations — increments s_compoundDepth on entry,
// decrements on exit. When the outermost guard destructs (depth back to 0)
// it publishes status=DONE / cmd=IDLE based on whatever `result` was last
// written. Catches all return paths (success, early error, exception).
struct CompoundOpGuard {
    CompoundOpGuard() { ++s_compoundDepth; }
    ~CompoundOpGuard() {
        --s_compoundDepth;
        if (s_compoundDepth == 0) {
            g_invokeMailbox.status = STATUS_DONE;
            g_invokeMailbox.cmd    = CMD_IDLE;
        }
    }
};

// ---- Polling thread ----

static DWORD WINAPI PollingThreadProc(LPVOID /*param*/) {
    LOG_INFO("Mailbox: polling thread started (poll=%ums)", kPollIntervalMs);

    // Bump Windows timer resolution to 1ms for the lifetime of this thread.
    // Without this, Sleep(1) on a host with the default 15.6ms tick (rare on
    // modern Win10/11 game processes, common on idle/server SKUs) would actually
    // sleep ~15.6ms — completely defeating the latency win. Paired with
    // timeEndPeriod below; balanced so we don't leak the request if the thread
    // exits cleanly. MMSYSERR_NOERROR == 0; non-zero just logs (caller decision
    // is to proceed: even on failure the worst case is Sleep granularity falls
    // back to the system default, not a correctness break).
    MMRESULT tbpRc = timeBeginPeriod(kPollIntervalMs);
    if (tbpRc != TIMERR_NOERROR) {
        LOG_WARN("Mailbox: timeBeginPeriod(%u) failed rc=%u — Sleep granularity "
                 "may fall back to system default", kPollIntervalMs, tbpRc);
    }

    // Audit doc #8: g_invokeMailbox is a plain struct (no atomics). Reads
    // are correct here under the assumption that the writer (CE Lua) uses
    // WriteProcessMemory, which kernel-serializes on x86/x64 with the
    // platform's strong memory model — so cross-process writes become
    // visible without explicit fences. `volatile`-style access prevents
    // compiler reordering. If we ever switch the writer to in-process,
    // the cmd/status fields must become std::atomic<int32_t>.
    while (s_running.load(std::memory_order_acquire)) {
        int32_t cmd = g_invokeMailbox.cmd;

        if (cmd != CMD_IDLE) {
            // Mark as processing
            g_invokeMailbox.status = STATUS_PROCESSING;

            LOG_INFO("Mailbox: received cmd=%d", cmd);

            // Auto-init if needed (proxy DLL mode: UE5_Init not called yet)
            if (!EnsureInitialized() && cmd != CMD_IDLE) {
                // Init failed — most commands won't work
                if (cmd == CMD_INVOKE || cmd == CMD_INVOKE_BY_NAME) {
                    SetError(-10, "DLL not initialized (GObjects/GNames not found)");
                    continue;
                }
                // FIND_INSTANCE/FIND_FUNCTION also need init
                SetError(-10, "DLL not initialized");
                continue;
            }

            switch (cmd) {
            case CMD_FIND_INSTANCE:
                HandleFindInstance();
                break;
            case CMD_FIND_FUNCTION:
                HandleFindFunction();
                break;
            case CMD_INVOKE:
                HandleInvoke();
                break;
            case CMD_INVOKE_BY_NAME:
                HandleInvokeByName();
                break;
            case CMD_LIST_FUNCTIONS:
                HandleListFunctions();
                break;
            case CMD_LIST_INSTANCES:
                HandleListInstances();
                break;
            default:
                SetError(-1, "Unknown command");
                break;
            }
        }

        Sleep(kPollIntervalMs);
    }

    if (tbpRc == TIMERR_NOERROR) {
        timeEndPeriod(kPollIntervalMs);
    }

    LOG_INFO("Mailbox: polling thread stopped");
    return 0;
}

// ---- Public API ----

void StartThread() {
    if (s_running.load()) return;

    s_running.store(true, std::memory_order_release);
    s_hThread = CreateThread(nullptr, 0, PollingThreadProc, nullptr, 0, nullptr);
    if (!s_hThread) {
        s_running.store(false);
        LOG_ERROR("Mailbox: failed to create polling thread (err=%lu)", GetLastError());
    }
}

void StopThread() {
    if (!s_running.load()) return;

    s_running.store(false, std::memory_order_release);

    if (s_hThread) {
        WaitForSingleObject(s_hThread, 3000);
        CloseHandle(s_hThread);
        s_hThread = nullptr;
    }

    // Clear mailbox
    memset(&g_invokeMailbox, 0, sizeof(g_invokeMailbox));
}

uintptr_t GetAddress() {
    return reinterpret_cast<uintptr_t>(&g_invokeMailbox);
}

// ---- Auto-initialization ----

static bool EnsureInitialized() {
    // UE5_Init is idempotent (checks internal s_initialized flag)
    // Note: extern declarations are at file scope (above namespace)
    if (g_cachedGObjects && g_cachedGNames) {
        return true;  // Already initialized
    }

    LOG_INFO("Mailbox: auto-initializing (UE5_Init)...");
    UE5_Init();

    return (g_cachedGObjects != 0 && g_cachedGNames != 0);
}

// ---- Command handlers ----

static void HandleFindInstance() {
    // Read class name from mailbox
    char className[256];
    memcpy(className, g_invokeMailbox.className, sizeof(className));
    className[255] = '\0';

    if (className[0] == '\0') {
        SetError(-1, "Empty class name");
        return;
    }

    LOG_INFO("Mailbox: FIND_INSTANCE class='%s'", className);

    // Reuse existing logic from UE5_FindInstanceOfClass
    auto rset = Aura::FindInstancesByClass(className, false, 100);

    // Prefer non-CDO instance
    uintptr_t found = 0;
    for (const auto& r : rset.results) {
        if (r.addr && r.name.find("Default__") == std::string::npos) {
            found = r.addr;
            break;
        }
    }

    // Fallback: first result even if CDO
    if (!found && !rset.results.empty() && rset.results[0].addr) {
        found = rset.results[0].addr;
        LOG_WARN("Mailbox: FIND_INSTANCE only CDO found for '%s'", className);
    }

    if (found) {
        g_invokeMailbox.instanceAddr = found;
        LOG_INFO("Mailbox: FIND_INSTANCE '%s' -> 0x%llX",
                 className, (unsigned long long)found);
        SetDone(0);
    } else {
        g_invokeMailbox.instanceAddr = 0;
        char msg[256];
        snprintf(msg, sizeof(msg), "No instance of '%s' found (scanned=%d)",
                 className, rset.scanned);
        SetError(-2, msg);
    }
}

static void HandleFindFunction() {
    uintptr_t instanceAddr = g_invokeMailbox.instanceAddr;

    char funcName[256];
    memcpy(funcName, g_invokeMailbox.funcName, sizeof(funcName));
    funcName[255] = '\0';

    if (!instanceAddr) {
        SetError(-1, "Instance address is null");
        return;
    }
    if (funcName[0] == '\0') {
        SetError(-1, "Empty function name");
        return;
    }

    LOG_INFO("Mailbox: FIND_FUNCTION inst=0x%llX func='%s'",
             (unsigned long long)instanceAddr, funcName);

    // Get UClass from instance
    uintptr_t classAddr = Ubel::GetClass(instanceAddr);
    if (!classAddr) {
        SetError(-2, "Cannot read UClass from instance");
        return;
    }

    // Walk functions
    auto funcs = Ubel::WalkFunctions(classAddr);

    // Exact match
    uintptr_t ufuncAddr = 0;
    uint16_t parmsSize = 0;
    uint16_t numParms = 0;
    uint32_t funcFlags = 0;

    for (const auto& f : funcs) {
        if (f.name == funcName) {
            ufuncAddr = f.address;
            parmsSize = f.parmsSize;
            numParms = f.numParms;
            funcFlags = f.functionFlags;
            break;
        }
    }

    // Case-insensitive fallback
    if (!ufuncAddr) {
        std::string lower(funcName);
        std::transform(lower.begin(), lower.end(), lower.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        for (const auto& f : funcs) {
            std::string fl = f.name;
            std::transform(fl.begin(), fl.end(), fl.begin(),
                           [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            if (fl == lower) {
                ufuncAddr = f.address;
                parmsSize = f.parmsSize;
                numParms = f.numParms;
                funcFlags = f.functionFlags;
                break;
            }
        }
    }

    if (ufuncAddr) {
        g_invokeMailbox.ufuncAddr = ufuncAddr;
        g_invokeMailbox.parmsSize = parmsSize;
        g_invokeMailbox.numParms = numParms;
        g_invokeMailbox.functionFlags = funcFlags;
        LOG_INFO("Mailbox: FIND_FUNCTION '%s' -> 0x%llX (parmsSize=%u numParms=%u flags=0x%X)",
                 funcName, (unsigned long long)ufuncAddr,
                 parmsSize, numParms, funcFlags);
        SetDone(0);
    } else {
        g_invokeMailbox.ufuncAddr = 0;
        char msg[256];
        snprintf(msg, sizeof(msg), "Function '%s' not found (%d functions walked)",
                 funcName, (int)funcs.size());
        SetError(-3, msg);
    }
}

static void HandleInvoke() {
    uintptr_t instanceAddr = g_invokeMailbox.instanceAddr;
    uintptr_t ufuncAddr = g_invokeMailbox.ufuncAddr;

    if (!instanceAddr || !ufuncAddr) {
        SetError(-1, "Instance or UFunction address is null");
        return;
    }

    LOG_INFO("Mailbox: INVOKE inst=0x%llX func=0x%llX",
             (unsigned long long)instanceAddr, (unsigned long long)ufuncAddr);

    // Pure-helper fast path: a UFunction tagged Native+Static is a C++
    // implementation that takes no implicit `this` and has no hidden
    // dependency on game state. KismetMathLibrary, KismetStringLibrary,
    // KismetArrayLibrary etc. all live here. These are safe to call
    // directly from the pipe thread and -- importantly -- DO NOT need
    // the game thread to ever fire ProcessEvent again.
    //
    // Without this short-circuit, an idle game (main menu / loading
    // screen) leaves the GameThreadDispatch queue undrained and every
    // pure-helper invoke times out at the configured deadline (default
    // 5s, observed in ES2 logs even at 7s). The user has no clue why a
    // simple `exp(8)` math helper would time out -- the call doesn't
    // need a game thread at all.
    //
    // Stateful instance methods (FUNC_Net, FUNC_Event, BlueprintEvent,
    // anything touching actor mutable state from off-thread) still
    // route through GameThreadDispatch via UE5_CallProcessEvent.
    const bool isStaticNative =
        (g_invokeMailbox.functionFlags & (kFuncFlag_Native | kFuncFlag_Static))
            == (kFuncFlag_Native | kFuncFlag_Static);

    int32_t result;
    if (isStaticNative) {
        LOG_INFO("Mailbox: INVOKE -> static-native fast path "
                 "(flags=0x%08X, bypassing GameThreadDispatch)",
                 g_invokeMailbox.functionFlags);
        result = UE5_CallProcessEventDirect(
            instanceAddr, ufuncAddr,
            reinterpret_cast<uintptr_t>(g_invokeMailbox.paramsData));
    } else {
        // Call ProcessEvent using the existing public API.
        // UE5_CallProcessEvent handles:
        //   - Lazy ProcessEvent vtable detection
        //   - Lazy MinHook installation (GameThreadDispatch)
        //   - EnqueueInvoke (blocks until game thread executes)
        //   - Fallback direct call if hook not active
        result = UE5_CallProcessEvent(
            instanceAddr, ufuncAddr,
            reinterpret_cast<uintptr_t>(g_invokeMailbox.paramsData));
    }

    if (result != 0) {
        char msg[256];
        snprintf(msg, sizeof(msg), "ProcessEvent returned %d", result);
        // Still mark as done (not error) — the result code tells the story
        strncpy(g_invokeMailbox.errorMsg, msg, sizeof(g_invokeMailbox.errorMsg) - 1);
        g_invokeMailbox.errorMsg[sizeof(g_invokeMailbox.errorMsg) - 1] = '\0';
    }

    LOG_INFO("Mailbox: INVOKE result=%d", result);
    SetDone(result);
}

static void HandleInvokeByName() {
    LOG_INFO("Mailbox: INVOKE_BY_NAME starting...");

    // Audit fix #10: chain three sub-handlers without leaking intermediate
    // status=DONE / cmd=IDLE writes to CE. The guard suppresses those
    // publishes inside the inner SetDone/SetError calls; on destruction
    // (any return path) it publishes the final state once.
    CompoundOpGuard guard;

    HandleFindInstance();
    if (g_invokeMailbox.result != 0) return;

    HandleFindFunction();
    if (g_invokeMailbox.result != 0) return;

    HandleInvoke();

    LOG_INFO("Mailbox: INVOKE_BY_NAME complete, result=%d", g_invokeMailbox.result);
}

static void HandleListFunctions() {
    // Resolve instance: use instanceAddr if set, otherwise find by className
    uintptr_t instanceAddr = g_invokeMailbox.instanceAddr;

    if (!instanceAddr) {
        char className[256];
        memcpy(className, g_invokeMailbox.className, sizeof(className));
        className[255] = '\0';

        if (className[0] == '\0') {
            SetError(-1, "No instance address or class name provided");
            return;
        }

        LOG_INFO("Mailbox: LIST_FUNCTIONS finding instance of '%s'...", className);

        auto rset = Aura::FindInstancesByClass(className, false, 100);
        for (const auto& r : rset.results) {
            if (r.addr && r.name.find("Default__") == std::string::npos) {
                instanceAddr = r.addr;
                break;
            }
        }
        if (!instanceAddr && !rset.results.empty() && rset.results[0].addr) {
            instanceAddr = rset.results[0].addr;
        }
        if (!instanceAddr) {
            char msg[256];
            snprintf(msg, sizeof(msg), "No instance of '%s' found", className);
            SetError(-2, msg);
            return;
        }
        g_invokeMailbox.instanceAddr = instanceAddr;
    }

    // Get page index from params_data[0..3]
    uint32_t pageIndex = 0;
    memcpy(&pageIndex, g_invokeMailbox.paramsData, sizeof(uint32_t));

    // Get UClass
    uintptr_t classAddr = Ubel::GetClass(instanceAddr);
    if (!classAddr) {
        SetError(-2, "Cannot read UClass from instance");
        return;
    }

    // Walk all functions
    auto funcs = Ubel::WalkFunctions(classAddr);

    LOG_INFO("Mailbox: LIST_FUNCTIONS inst=0x%llX class=0x%llX total=%d page=%u",
             (unsigned long long)instanceAddr, (unsigned long long)classAddr,
             (int)funcs.size(), pageIndex);

    // Pagination: 15 entries per page (64 bytes each, 15*64=960 < 1024)
    // First 4 bytes of paramsData used for page header
    constexpr uint32_t ENTRY_SIZE = 64;
    constexpr uint32_t NAME_SIZE = 48;
    constexpr uint32_t ENTRIES_PER_PAGE = 15;

    uint32_t totalCount = static_cast<uint32_t>(funcs.size());
    uint32_t totalPages = (totalCount + ENTRIES_PER_PAGE - 1) / ENTRIES_PER_PAGE;
    if (totalPages == 0) totalPages = 1;

    uint32_t startIdx = pageIndex * ENTRIES_PER_PAGE;
    uint32_t endIdx = (std::min)(startIdx + ENTRIES_PER_PAGE, totalCount);
    uint32_t returnedCount = (startIdx < totalCount) ? (endIdx - startIdx) : 0;

    // Write metadata to header fields (repurposed)
    g_invokeMailbox.parmsSize = static_cast<uint16_t>(totalCount);
    g_invokeMailbox.numParms = static_cast<uint16_t>(returnedCount);
    g_invokeMailbox.functionFlags = totalPages;

    // Zero out params_data
    memset(g_invokeMailbox.paramsData, 0, sizeof(g_invokeMailbox.paramsData));

    // Write entries: each 64 bytes
    for (uint32_t i = 0; i < returnedCount; ++i) {
        const auto& f = funcs[startIdx + i];
        uint8_t* entry = g_invokeMailbox.paramsData + (i * ENTRY_SIZE);

        // [0..7] addr
        uint64_t addr64 = f.address;
        memcpy(entry + 0, &addr64, 8);

        // [8..9] parmsSize
        uint16_t ps = f.parmsSize;
        memcpy(entry + 8, &ps, 2);

        // [10..11] numParms
        uint16_t np = f.numParms;
        memcpy(entry + 10, &np, 2);

        // [12..15] flags
        uint32_t fl = f.functionFlags;
        memcpy(entry + 12, &fl, 4);

        // [16..63] name (null-terminated, max 47 chars + null)
        size_t nameLen = (std::min)(f.name.size(), static_cast<size_t>(NAME_SIZE - 1));
        memcpy(entry + 16, f.name.c_str(), nameLen);
        entry[16 + nameLen] = '\0';
    }

    LOG_INFO("Mailbox: LIST_FUNCTIONS returned %u/%u functions (page %u/%u)",
             returnedCount, totalCount, pageIndex + 1, totalPages);
    SetDone(0);
}

// Enumerate all live (non-CDO) instances of a class for the property-freeze
// helper. Paginated identically to LIST_FUNCTIONS so the CE Lua side can pull
// multi-page result sets through the single-slot mailbox.
//
// Match policy: exactMatch=true. Freeze callers want precise class identity
// — partial matching would have "Pawn" pull every pawn subclass in the world,
// and the property offset only makes sense for the exact class chain the
// PropertySearch row identified. Users who deliberately want a broader scope
// can edit the className in the generated AA Script's CFG block.
static void HandleListInstances() {
    char className[256];
    memcpy(className, g_invokeMailbox.className, sizeof(className));
    className[255] = '\0';

    if (className[0] == '\0') {
        SetError(-1, "Empty class name");
        return;
    }

    // Read page index from paramsData[0..3] BEFORE we overwrite the buffer.
    uint32_t pageIndex = 0;
    memcpy(&pageIndex, g_invokeMailbox.paramsData, sizeof(uint32_t));

    LOG_INFO("Mailbox: LIST_INSTANCES class='%s' page=%u", className, pageIndex);

    // Hard cap at 2000 instances — freeze use cases ("all teammates", "all
    // ammo pickups") rarely exceed double digits; 2000 is generous and the
    // total walk stays bounded.
    auto rset = Aura::FindInstancesByClass(className, /*exactMatch=*/true, /*maxResults=*/2000);

    // CDO filter: the class default object (Default__BP_Foo_C) is the
    // template, not a live instance. Freezing its property would touch the
    // template state — never what the user wants.
    std::vector<uintptr_t> live;
    live.reserve(rset.results.size());
    for (const auto& r : rset.results) {
        if (r.addr && r.name.find("Default__") == std::string::npos) {
            live.push_back(r.addr);
        }
    }

    constexpr uint32_t ENTRY_SIZE = 8;            // uint64 pointer
    constexpr uint32_t ENTRIES_PER_PAGE = 128;    // 128 * 8 = 1024 bytes (fills paramsData)

    uint32_t totalCount = static_cast<uint32_t>(live.size());
    uint32_t totalPages = (totalCount + ENTRIES_PER_PAGE - 1) / ENTRIES_PER_PAGE;
    if (totalPages == 0) totalPages = 1;

    uint32_t startIdx = pageIndex * ENTRIES_PER_PAGE;
    uint32_t endIdx = (std::min)(startIdx + ENTRIES_PER_PAGE, totalCount);
    uint32_t returnedCount = (startIdx < totalCount) ? (endIdx - startIdx) : 0;

    // parmsSize is uint16 — saturate if a class somehow has >65535 instances.
    g_invokeMailbox.parmsSize = static_cast<uint16_t>(totalCount > 0xFFFFu ? 0xFFFFu : totalCount);
    g_invokeMailbox.numParms = static_cast<uint16_t>(returnedCount);
    g_invokeMailbox.functionFlags = totalPages;

    memset(g_invokeMailbox.paramsData, 0, sizeof(g_invokeMailbox.paramsData));

    for (uint32_t i = 0; i < returnedCount; ++i) {
        uint64_t addr = live[startIdx + i];
        memcpy(g_invokeMailbox.paramsData + (i * ENTRY_SIZE), &addr, ENTRY_SIZE);
    }

    LOG_INFO("Mailbox: LIST_INSTANCES returned %u/%u (page %u/%u)",
             returnedCount, totalCount, pageIndex + 1, totalPages);
    SetDone(0);
}

// ---- Helpers ----

static void SetError(int32_t code, const char* msg) {
    g_invokeMailbox.result = code;
    if (msg) {
        strncpy(g_invokeMailbox.errorMsg, msg, sizeof(g_invokeMailbox.errorMsg) - 1);
        g_invokeMailbox.errorMsg[sizeof(g_invokeMailbox.errorMsg) - 1] = '\0';
    }
    LOG_WARN("Mailbox: error=%d msg='%s'", code, msg ? msg : "");

    // Audit fix #10: inside a compound op (HandleInvokeByName), the outer
    // CompoundOpGuard publishes status/cmd; suppress the intermediate
    // signal here so CE doesn't see a transient DONE between sub-steps.
    if (s_compoundDepth > 0) return;

    // Signal completion — MUST write status BEFORE clearing cmd
    g_invokeMailbox.status = STATUS_DONE;
    g_invokeMailbox.cmd = CMD_IDLE;
}

static void SetDone(int32_t resultCode) {
    g_invokeMailbox.result = resultCode;

    // Audit fix #10: see SetError comment.
    if (s_compoundDepth > 0) return;

    // Signal completion — MUST write status BEFORE clearing cmd
    g_invokeMailbox.status = STATUS_DONE;
    g_invokeMailbox.cmd = CMD_IDLE;
}

} // namespace Mimic
