// ============================================================
// Heiter — 海塔 (僧侶・旅途起點 — Priest Who Started the Journey)
// dllmain: DLL entry point and auto-start logic
// ============================================================

#include <Windows.h>
#include <atomic>
#define LOG_CAT "INIT"
#include "Sein.h"
#include "BuildStamp.h"
#include "Grimoire.h"
#include "Utf8Helpers.h"

// Global DLL module handle — used by CEPlugin.cpp to resolve the DLL's
// own file path when injecting into the game process.
HMODULE g_hDllModule = nullptr;

// Set to true by CEPlugin_InitializePlugin when this DLL is loaded as a
// CE plugin (in CE's own process). Auto-start thread checks this flag to
// decide whether to self-initialize.
std::atomic<bool> g_isCEPlugin{false};

// Forward declaration — defined in ExportAPI.cpp
extern "C" bool UE5_AutoStart();
extern "C" bool UE5_StartPipeServer();
extern "C" void UE5_Shutdown();

// Note: the dxgi proxy resolves the real dxgi exports LAZILY, from its asm
// forwarding thunks (Lugner_Dxgi.asm -> DxgiProxy_EnsureResolved) on the
// first forwarded dxgi call. It is intentionally NOT resolved here in
// DllMain: loading dxgi.dll under the loader lock during the EXE's static-
// import init crashes games that import dxgi.dll before d3d11 (e.g.
// Octopath Traveler). See Lugner_Dxgi.cpp for the full rationale.

// Mailbox — shared memory interface for CE Lua
#include "Mimic.h"

// Handle for the auto-start thread — stored so we can wait for it during
// active shutdown (UE5_Shutdown, not DLL_PROCESS_DETACH, see below).
static HANDLE g_hAutoStartThread = nullptr;

#ifdef UE5_PROXY_BUILD
// ── Proxy mutual exclusion ─────────────────────────────────────────────────
// When the user has both proxy DLLs (version.dll + dinput8.dll) sitting in
// the game folder, both will be loaded by the OS. Only the first to attach
// runs full init (pipe server, mailbox, AOB scan); the second becomes a
// passive forwarder — its OS-API stubs still work, but it skips all
// UE5CEDumper-side init so we don't get duplicate pipe servers, log files,
// or background threads.
//
// The mutex handle is intentionally never closed — its lifetime spans the
// whole process. The OS reclaims it on process exit.
static HANDLE g_primaryProxyMutex = nullptr;
// False when CreateMutexW itself failed — the guard is then not armed at all and a
// second proxy in this process would run alongside us. Logged once Sein exists. (B47)
static bool   g_primaryProxyMutexArmed = false;
#endif

#ifdef UE5_PROXY_BUILD
// ── Proxy DLL auto-start ───────────────────────────────────────────────────
// In proxy mode, we start the pipe server immediately (no scan).
// The user triggers scanning later from the UI when the game has loaded.
// No CE delay needed — proxy DLL is always in the game process.
static DWORD WINAPI AutoStartThreadProc(LPVOID)
{
    // Guard: if this DLL was accidentally loaded by UE5DumpUI.exe (e.g. both
    // files in the same directory), skip all initialization to avoid the UI
    // connecting to its own in-process pipe server.
    {
        wchar_t procName[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, procName, MAX_PATH);
        std::wstring path(procName);
        auto slash = path.find_last_of(L"\\/");
        std::wstring exe = (slash != std::wstring::npos) ? path.substr(slash + 1) : path;
        // Case-insensitive compare
        for (auto& c : exe) c = towlower(c);
        if (exe == L"ue5dumpui.exe") {
            LOG_WARN("DllMain ProxyStart: loaded by UE5DumpUI.exe — skipping proxy init");
            g_invokeMailbox.initState = Mimic::INIT_SKIPPED;
            return 0;
        }
    }

    LOG_INFO("DllMain ProxyStart: proxy DLL mode — starting pipe server only (no scan)");
    g_invokeMailbox.initState = Mimic::INIT_RUNNING;

    // Brief delay for game to finish early init (avoid pipe creation during process startup)
    Sleep(500);

    bool ok = UE5_StartPipeServer();
    g_invokeMailbox.initState = ok ? Mimic::INIT_READY : Mimic::INIT_FAILED;
    LOG_INFO("DllMain ProxyStart: pipe server %s", ok ? "started" : "FAILED to start");
    return 0;
}
#else
// ── CE / Manual inject auto-start ──────────────────────────────────────────
// Spawned on DLL_PROCESS_ATTACH. Waits 1 second to allow CEPlugin_InitializePlugin
// to run (which sets g_isCEPlugin = true) if CE is loading this as a plugin.
// If g_isCEPlugin is still false after the delay, we're in a game process and
// call UE5_AutoStart() to scan for GObjects/GNames and start the pipe server.
//
// This eliminates the need for CE Lua to call UE5_Init via executeCodeEx,
// which was timing out due to the AOB scan taking longer than CE's remote-
// thread timeout.
static DWORD WINAPI AutoStartThreadProc(LPVOID)
{
    // Log immediately (before any sleep) to confirm thread is running.
    LOG_INFO("DllMain AutoStart: thread started (g_isCEPlugin=%d)", (int)g_isCEPlugin.load());

    // Give CE time to call CEPlugin_InitializePlugin (~200 ms typical).
    Sleep(1000);

    LOG_INFO("DllMain AutoStart: after 1s delay (g_isCEPlugin=%d)", (int)g_isCEPlugin.load());

    if (g_isCEPlugin.load()) {
        LOG_INFO("DllMain AutoStart: CE plugin host — skipping auto-start");
        g_invokeMailbox.initState = Mimic::INIT_SKIPPED;
        return 0;
    }

    // Belt and braces for the same race (B34). CEPlugin_GetVersion now claims plugin
    // identity, which closes the ordinary window — but "did a human tick a checkbox in
    // under a second" is not a condition worth betting a full AOB scan of the WRONG
    // PROCESS on. Cheat Engine is never a scan target, so name it explicitly.
    {
        wchar_t hostPath[MAX_PATH] = {};
        if (GetModuleFileNameW(nullptr, hostPath, MAX_PATH)) {
            const wchar_t* leaf = wcsrchr(hostPath, L'\\');
            leaf = leaf ? leaf + 1 : hostPath;
            if (_wcsicmp(leaf, L"cheatengine-x86_64.exe") == 0 ||
                _wcsicmp(leaf, L"cheatengine-i386.exe")   == 0 ||
                _wcsicmp(leaf, L"Cheat Engine.exe")       == 0) {
                LOG_WARN("DllMain AutoStart: host process is '%ls' — Cheat Engine is never "
                         "a scan target; skipping auto-start (CE plugin, not yet enabled?)",
                         leaf);
                g_invokeMailbox.initState = Mimic::INIT_SKIPPED;
                return 0;
            }
        }
    }

    // Check if another UE5Dumper instance is already running (e.g., proxy DLL)
    // by trying to open the named pipe. If it exists, skip auto-start.
    HANDLE testPipe = CreateFileW(
        Grimoire::PIPE_NAME,
        GENERIC_READ, 0, nullptr,
        OPEN_EXISTING, 0, nullptr);
    if (testPipe != INVALID_HANDLE_VALUE) {
        CloseHandle(testPipe);
        LOG_WARN("DllMain AutoStart: pipe already exists (another UE5Dumper instance running) — skipping auto-start");
        // SKIPPED, not FAILED: a pipe server IS up (owned by the other instance),
        // so a CE Lua poller should proceed to connect rather than report an error.
        g_invokeMailbox.initState = Mimic::INIT_SKIPPED;
        return 0;
    }

    LOG_INFO("DllMain AutoStart: game process — calling UE5_AutoStart");
    UE5_AutoStart();
    LOG_INFO("DllMain AutoStart: UE5_AutoStart returned");
    return 0;
}
#endif

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
    case DLL_PROCESS_ATTACH: {
        g_hDllModule = hModule;
        DisableThreadLibraryCalls(hModule);

#ifdef UE5_PROXY_BUILD
        // First-loaded-wins: if another UE5CEDumper proxy DLL is already loaded into
        // THIS process, become a passive forwarder. We MUST do this before Sein::Init()
        // so the second instance doesn't open log files in the same per-process
        // subfolder as the first.
        //
        // Returning TRUE here skips Sein::Init / Mimic::StartThread / the
        // auto-start thread; the only thing this DLL still does is service
        // its OS-API export forwarders (Lugner.cpp / Lugner_Dinput8.cpp).
        //
        // The name is per-PROCESS, and that is the whole point — the comment above has
        // always said so, but the name used to be `Global\…`, which is a different
        // thing in two ways (B47). Creating a `Global\` object needs
        // SeCreateGlobalPrivilege, which a non-elevated game does NOT have: CreateMutexW
        // returned NULL, the guard was skipped entirely, and the same-process dedup it
        // exists for silently never worked. When it DID work (elevated game) it was
        // worse — machine-wide, so a second instrumented game anywhere on the system
        // turned this proxy passive. Local\ + PID is the scope the design intends.
        {
            wchar_t mutexName[64];
            swprintf_s(mutexName, L"Local\\UE5CEDumper_PrimaryProxy_%lu",
                       GetCurrentProcessId());
            g_primaryProxyMutex = CreateMutexW(nullptr, FALSE, mutexName);
            if (g_primaryProxyMutex != nullptr &&
                GetLastError() == ERROR_ALREADY_EXISTS) {
                // Cannot log — Sein is deliberately not initialized in passive mode, and
                // initializing it is exactly what this branch must avoid. The first
                // instance records the pairing from its own side instead (below).
                return TRUE;
            }
            // A NULL handle means the guard is not armed at all. Record it once we have
            // a logger, because a silently-unarmed dedup is what made this defect
            // invisible: two proxies in one process would BOTH run, and the only symptom
            // is interleaved logs in one folder.
            g_primaryProxyMutexArmed = (g_primaryProxyMutex != nullptr);
        }
#endif

        Sein::Init();
        {
            // Log which process loaded this DLL — distinguishes CE plugin
            // host (ce.exe) from game process injection in the log file.
            wchar_t procPathW[MAX_PATH] = {};
            GetModuleFileNameW(nullptr, procPathW, MAX_PATH);

            // Extract filename from path for log + mirror
            std::wstring fullPath(procPathW);
            auto lastSlash = fullPath.find_last_of(L"\\/");
            std::wstring fileName = (lastSlash != std::wstring::npos)
                ? fullPath.substr(lastSlash + 1) : fullPath;

            // UTF-8 encode the WIDE path already in hand rather than asking Windows for a
            // narrow one. GetModuleFileNameA converts through the ANSI code page, which
            // DESTROYS any character the code page cannot represent: a real install at
            // "...\EVERSPACE™ 2\..." logged as "EVERSPACE? 2", and '?' is not a legal path
            // character — so the logged path could not be opened, existence-checked, or matched
            // back to a folder. The log files are UTF-8, so the wide path round-trips exactly.
            // This line is a data source for the Proxy Deploy orphan scan, which recovers
            // deployment locations from it; a mangled path is an unusable row there.
            std::string procPathU8 = Utf8Helpers::EncodeUtf16(fullPath.c_str(), fullPath.size());
            LOG_INFO("UE5Dumper DLL loaded | build: %s | process: %s [PID=%lu]",
                     BuildStamp::VersionString(), procPathU8.c_str(), GetCurrentProcessId());

            // Initialize per-process mirror log subfolder
            Sein::InitProcessMirror(fileName);

#ifdef UE5_PROXY_BUILD
            // The first moment this can be said out loud. A dedup that is not armed
            // must not look like a dedup that found nothing. (B47)
            if (!g_primaryProxyMutexArmed) {
                LOG_WARN("Proxy: first-loaded-wins guard is NOT armed (CreateMutexW failed, "
                         "err=%lu) — a second UE5CEDumper proxy in this process would run "
                         "alongside this one", GetLastError());
            }
#endif
        }
        // Start mailbox polling thread (CE Lua shared memory interface).
        // Runs in both proxy and inject modes — handles auto-init on first command.
        Mimic::StartThread();

        // Spawn auto-start thread. It will self-terminate if g_isCEPlugin
        // is set true by CEPlugin_InitializePlugin within 1 second.
        // Store the handle so DLL_PROCESS_DETACH can wait for it to finish.
        g_hAutoStartThread = CreateThread(nullptr, 0, AutoStartThreadProc, nullptr, 0, nullptr);
        if (g_hAutoStartThread) {
            LOG_INFO("DllMain: auto-start thread created OK");
        } else {
            LOG_ERROR("DllMain: CreateThread failed (error=%lu)", GetLastError());
        }
        break;
    }

    case DLL_PROCESS_DETACH:
        // Audit findings #1, #2, #14: heavy work in DLL_PROCESS_DETACH
        // (thread joins, MinHook teardown, FreeLibrary, log file flushes)
        // risks loader-lock deadlocks because joined threads or unhooked
        // code may take the loader lock that DllMain already holds.
        //
        // The OS terminates all threads and reclaims handles, file
        // descriptors, loaded modules, and memory automatically when the
        // process exits — that's the right place for this teardown.
        //
        // Active shutdowns still go through the full UE5_Shutdown path:
        //   - CE Lua "Disable" → ue5_callDLL("UE5_Shutdown")
        //   - Future hooks for proxy-mode graceful exit
        // Only the implicit process-exit DETACH is a no-op.
        break;

    default:
        break;
    }
    return TRUE;
}
