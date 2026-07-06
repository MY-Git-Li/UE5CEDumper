// Grausam — グラオザーム (幻影魔法大師 — Seven Sages, Master of Illusion)
// ForegroundLock: two illusions so the game never learns it lost focus —
//   1. MinHook user32!GetForegroundWindow → report the game's own window as
//      foreground (defeats t.IdleWhenNotForeground idle + FApp::HasFocus polling).
//   2. Subclass every top-level game window's WndProc and rewrite the
//      deactivation messages (WM_ACTIVATEAPP / WM_ACTIVATE / WM_NCACTIVATE →
//      "active", swallow WM_KILLFOCUS) → defeats WM_ACTIVATEAPP-driven pauses
//      (UE's OnApplicationActivationChanged + any game-side focus-loss pause).
// See Grausam.h.

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

// Per-window property key holding the original WNDPROC of a window we subclassed.
constexpr const wchar_t* kOrigProcProp = L"UE5CEDumper_Grausam_OrigProc";

// ── Window enumeration ───────────────────────────────────────────────

BOOL CALLBACK PickLargestProc(HWND hwnd, LPARAM lp) {
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
    ::EnumWindows(&PickLargestProc, reinterpret_cast<LPARAM>(&best));
    return best.first;
}

// ── (1) GetForegroundWindow hook ─────────────────────────────────────

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

// ── (2) WndProc subclass — neutralize deactivation messages ──────────

LRESULT CALLBACK SubclassProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    auto orig = reinterpret_cast<WNDPROC>(::GetPropW(h, kOrigProcProp));

    if (g_enabled.load(std::memory_order_relaxed)) {
        switch (msg) {
            case WM_ACTIVATEAPP:  w = TRUE;                              break;  // always "activated"
            case WM_NCACTIVATE:   w = TRUE;                              break;  // keep frame active
            case WM_ACTIVATE:     w = MAKEWPARAM(WA_ACTIVE, HIWORD(w));  break;  // low word = WA_ACTIVE
            case WM_KILLFOCUS:    return 0;                                      // swallow focus loss
            default: break;
        }
    }

    if (orig) return ::CallWindowProcW(orig, h, msg, w, l);
    return ::DefWindowProcW(h, msg, w, l);
}

BOOL CALLBACK SubclassEnumProc(HWND hwnd, LPARAM /*lp*/) {
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ::GetCurrentProcessId()) return TRUE;
    if (!::IsWindowVisible(hwnd)) return TRUE;
    if (::GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;
    if (::GetPropW(hwnd, kOrigProcProp)) return TRUE;  // already subclassed by us

    WNDPROC orig = reinterpret_cast<WNDPROC>(
        static_cast<LONG_PTR>(::GetWindowLongPtrW(hwnd, GWLP_WNDPROC)));
    if (!::SetPropW(hwnd, kOrigProcProp, reinterpret_cast<HANDLE>(orig)))
        return TRUE;
    ::SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&SubclassProc));

    wchar_t title[128] = {0};
    ::GetWindowTextW(hwnd, title, 127);
    RECT rc{}; ::GetWindowRect(hwnd, &rc);
    Sein::Info("Grausam", "Subclassed window 0x%p (%dx%d, unicode=%d)",
               reinterpret_cast<void*>(hwnd),
               (int)(rc.right - rc.left), (int)(rc.bottom - rc.top),
               (int)::IsWindowUnicode(hwnd));
    return TRUE;
}

void SubclassAllGameWindows() {
    ::EnumWindows(&SubclassEnumProc, 0);
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
        // Subclass the game window(s) so WM_ACTIVATEAPP-driven pauses are defeated
        // too (the GetForegroundWindow hook alone only covers the polling path).
        SubclassAllGameWindows();
        Sein::Info("Grausam", "Foreground lock ENABLED (fg-window=0x%p)",
                   reinterpret_cast<void*>(g_gameWindow.load()));
        return 1;
    }

    // Soft-disable: leave the hook + subclass installed (they gate on g_enabled and
    // pass through when off) to avoid an unhook/unsubclass race with an in-flight call.
    g_enabled.store(false, std::memory_order_relaxed);
    Sein::Info("Grausam", "Foreground lock DISABLED");
    return 0;
}

int IsForegroundLockEnabled() {
    return g_enabled.load(std::memory_order_relaxed) ? 1 : 0;
}

}  // namespace Grausam
