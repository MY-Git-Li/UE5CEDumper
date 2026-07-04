// ============================================================
// Stark — 修塔爾克 (勇者戰士 — Brave Warrior)
// GameThreadDispatch: MinHook ProcessEvent hook + game-thread queue
//
// Architecture:
//   Pipe thread calls EnqueueInvoke() → pushes request to queue
//   Game thread's ProcessEvent hook drains queue → executes requests
//   Result returned via std::future (blocks pipe thread until done)
//
// This ensures ProcessEvent is always called from the game thread,
// which UE expects for state-changing operations (UI, rendering,
// spawning actors, etc.).
// ============================================================
#pragma once

#include <cstdint>

namespace Stark {

/// Install the ProcessEvent hook at the given address.
/// @param processEventAddr  Absolute address of UObject::ProcessEvent
/// @return true if hook installed successfully
bool InstallHook(uintptr_t processEventAddr);

/// Remove the hook and clean up. Safe to call even if not installed.
/// Does NOT call MH_Uninitialize — use Shutdown() at DLL unload instead.
void RemoveHook();

/// Full teardown: remove hook AND uninitialize MinHook globally.
/// Call ONLY from DLL_PROCESS_DETACH (via UE5_Shutdown). After this,
/// InstallHook() can no longer be used unless MinHook is re-initialized.
void Shutdown();

/// @return true if the ProcessEvent hook is active
bool IsHookActive();

/// Enqueue a ProcessEvent invocation for game-thread execution.
/// Blocks until the game thread executes it (or timeout).
///
/// @param instance    UObject instance pointer
/// @param ufunc       UFunction pointer
/// @param params      Parameter buffer pointer (already allocated/written by caller)
/// @param paramsSize  Bytes to COPY into the request so it owns its buffer. Pass
///                    the UFunction ParmsSize when the caller's buffer is
///                    transient (the common case — prevents a use-after-free if
///                    the invoke times out but is later drained by the game
///                    thread). Pass 0 only when `params` is a persistent buffer
///                    that outlives the request (e.g. Mimic's mailbox global).
/// @return 0 on success, -4 if SEH exception, -5 if timeout, -7 if hook not active
int32_t EnqueueInvoke(uintptr_t instance, uintptr_t ufunc, uintptr_t params, size_t paramsSize);

/// Default invoke timeout (compile-time baseline, used when no override is set).
constexpr int32_t kDefaultInvokeTimeoutMs = 5000;
/// Clamp band for a user-supplied invoke timeout (100ms .. 10min). Enforced by
/// SetInvokeTimeoutMs and mirrored by Fern's set_invoke_timeout pipe validation.
constexpr int32_t kMinInvokeTimeoutMs = 100;
constexpr int32_t kMaxInvokeTimeoutMs = 600000;

/// Override the EnqueueInvoke timeout in milliseconds. Set to 0 to revert to
/// kDefaultInvokeTimeoutMs. Clamped to [100, 600000] (100ms .. 10min). Thread-safe.
void SetInvokeTimeoutMs(int32_t timeoutMs);

/// Read the current invoke timeout in milliseconds (post-clamping).
int32_t GetInvokeTimeoutMs();

/// Total number of times HookedProcessEvent has fired since this process
/// started. Used by post-install validation (build 648+) to confirm the
/// hook was actually placed on UObject::ProcessEvent and not on an
/// adjacent UObject virtual: a real PE hook fires many times per second
/// during normal gameplay, a wrong hook fires 0 times. Thread-safe.
uint64_t GetHookFireCount();

/// Gap (ms) beyond which — once the PE hook has fired at least once — the game
/// thread is considered "stalled": not ticking ProcessEvent (paused / suspended
/// / alt-tab-throttled). A live game fires PE hundreds of times per frame, so
/// even a few-FPS game stays far under this; only a genuine stop crosses it.
constexpr int32_t kStallThresholdMs = 500;

/// Milliseconds since HookedProcessEvent last fired, or UINT64_MAX when it has
/// never fired (hook not installed yet, or installed but the game thread has
/// not reached it — liveness unknown). Thread-safe.
uint64_t MsSinceLastHookFire();

/// @return true if the game thread appears to be ticking (draining our PE
/// hook): either no fire has been observed yet (unknown → give a fresh hook a
/// chance) or the last fire was within thresholdMs. False ONLY once the hook
/// has fired and then gone quiet past the threshold — i.e. the game is
/// paused/suspended. Callers use this to skip game-thread invokes that would
/// otherwise block for the full invoke timeout on a thread that will not run,
/// and to surface a "game paused" hint. thresholdMs<=0 uses kStallThresholdMs.
/// Thread-safe.
bool IsGameThreadResponsive(int32_t thresholdMs = kStallThresholdMs);

} // namespace Stark
