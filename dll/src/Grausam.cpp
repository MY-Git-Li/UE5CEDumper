// Grausam — グラオザーム (幻影魔法大師 — Seven Sages, Master of Illusion)
// ForegroundLock: hook user32!GetForegroundWindow to always report the game's own
// window as foreground. See Grausam.h.

#include "Grausam.h"
#include "Sein.h"

#include <windows.h>
#include <MinHook.h>
#include <atomic>
#include <mutex>
#include <utility>

namespace Grausam {

namespace {

using GetForegroundWindow_t = HWND(WINAPI*)();

std::mutex             g_mutex;
GetForegroundWindow_t  g_origGetForegroundWindow = nullptr;
std::atomic<bool>      g_enabled{false};
std::atomic<HWND>      g_gameWindow{nullptr};
bool                   g_hookInstalled = false;
bool                   g_mhInitialized = false;

// Pick the largest top-level, visible, un-owned window belonging to THIS process
// — the game's render window (not a splash / tooltip / owned dialog).
BOOL CALLBACK EnumProc(HWND hwnd, LPARAM lp) {
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ::GetCurrentProcessId()) return TRUE;
    if (!::IsWindowVisible(hwnd)) return TRUE;
    if (::GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;  // skip owned dialogs/tooltips

    RECT rc{};
    if (!::GetWindowRect(hwnd, &rc)) return TRUE;
    const long area = (rc.right - rc.left) * (rc.bottom - rc.top);
    auto* best = reinterpret_cast<std::pair<HWND, long>*>(lp);
    if (area > best->second) { best->first = hwnd; best->second = area; }
    return TRUE;
}

HWND FindGameWindow() {
    std::pair<HWND, long> best{nullptr, 0};
    ::EnumWindows(&EnumProc, reinterpret_cast<LPARAM>(&best));
    return best.first;
}

// Replacement for GetForegroundWindow. When the illusion is off, or the real
// foreground already belongs to us, we return the truth. Otherwise we return our
// own top-level window so IsThisApplicationForeground() (which compares the
// foreground window's PID to GetCurrentProcessId()) reports "foreground".
HWND WINAPI HookedGetForegroundWindow() {
    HWND real = g_origGetForegroundWindow ? g_origGetForegroundWindow() : ::GetForegroundWindow();
    if (!g_enabled.load(std::memory_order_relaxed))
        return real;

    DWORD pid = 0;
    if (real) ::GetWindowThreadProcessId(real, &pid);
    if (pid == ::GetCurrentProcessId())
        return real;  // genuinely foreground — keep the truth

    HWND gw = g_gameWindow.load(std::memory_order_relaxed);
    if (!gw || !::IsWindow(gw)) {
        gw = FindGameWindow();
        g_gameWindow.store(gw, std::memory_order_relaxed);
    }
    return (gw && ::IsWindow(gw)) ? gw : real;
}

}  // namespace

int SetForegroundLock(bool enable) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (enable) {
        if (!g_hookInstalled) {
            if (!g_mhInitialized) {
                MH_STATUS st = MH_Initialize();
                if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
                    Sein::Error("Grausam", "MH_Initialize failed: %s", MH_StatusToString(st));
                    return -1;
                }
                g_mhInitialized = true;
            }

            FARPROC target = ::GetProcAddress(::GetModuleHandleW(L"user32.dll"), "GetForegroundWindow");
            if (!target) {
                Sein::Error("Grausam", "GetProcAddress(user32!GetForegroundWindow) failed");
                return -2;
            }

            MH_STATUS st = MH_CreateHook(reinterpret_cast<LPVOID>(target),
                                         reinterpret_cast<LPVOID>(&HookedGetForegroundWindow),
                                         reinterpret_cast<LPVOID*>(&g_origGetForegroundWindow));
            if (st != MH_OK) {
                Sein::Error("Grausam", "MH_CreateHook failed: %s", MH_StatusToString(st));
                return -3;
            }
            st = MH_EnableHook(reinterpret_cast<LPVOID>(target));
            if (st != MH_OK) {
                Sein::Error("Grausam", "MH_EnableHook failed: %s", MH_StatusToString(st));
                MH_RemoveHook(reinterpret_cast<LPVOID>(target));
                return -4;
            }
            g_hookInstalled = true;
        }

        g_gameWindow.store(FindGameWindow(), std::memory_order_relaxed);
        g_enabled.store(true, std::memory_order_relaxed);
        Sein::Info("Grausam", "Foreground lock ENABLED (game window=0x%p)",
                   reinterpret_cast<void*>(g_gameWindow.load()));
        return 1;
    }

    // Soft-disable: leave the hook physically installed (avoids an unhook race with
    // an in-flight call) and just pass through to the real API.
    g_enabled.store(false, std::memory_order_relaxed);
    Sein::Info("Grausam", "Foreground lock DISABLED");
    return 0;
}

int IsForegroundLockEnabled() {
    return g_enabled.load(std::memory_order_relaxed) ? 1 : 0;
}

}  // namespace Grausam
