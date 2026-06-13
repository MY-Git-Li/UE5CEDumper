// ============================================================
// Stark — 修塔爾克 (勇者戰士 — Brave Warrior)
// GameThreadDispatch: MinHook ProcessEvent hook + game-thread queue
//
// Hooks UObject::ProcessEvent using MinHook. Every game-thread PE
// call first drains a lock-protected queue of pending invocations
// submitted from the pipe handler thread.
//
// Empty-queue fast path: one mutex lock/unlock per ProcessEvent
// call (negligible vs ProcessEvent's own cost).
// ============================================================

#define LOG_CAT "PIPE"
#include "Sein.h"
#include "Stark.h"

#include <MinHook.h>
#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <queue>
#include <vector>

namespace Stark {

// ---- Types ----

/// A single queued ProcessEvent invocation request.
/// Shared ownership: pipe thread holds shared_ptr while waiting on future,
/// game thread holds shared_ptr while executing.
///
/// CRITICAL: the shared_ptr keeps the REQUEST struct alive, but NOT the caller's
/// parameter buffer. If the caller passes a transient buffer (e.g. the pipe
/// handler's stack-local vector) and the invoke TIMES OUT, the request stays
/// queued while the caller's buffer is freed — the game thread then dereferences
/// freed memory (use-after-free). To make a timed-out-but-still-queued request
/// self-contained, EnqueueInvoke COPIES the param bytes into `ownedParams` (when
/// a size is given) and points `params` at that owned copy. Callers that pass a
/// persistent buffer (Mimic's mailbox global) may pass size 0 to skip the copy.
struct InvokeRequest {
    uintptr_t instance;
    uintptr_t ufunc;
    uintptr_t params;
    std::vector<uint8_t> ownedParams;   // owns the param bytes when copied (size>0)
    std::promise<int32_t> promise;
};

// ---- State ----

// Original ProcessEvent function pointer (set by MinHook)
typedef void(__fastcall* FnProcessEvent)(void* thisObj, void* ufunc, void* params);
static FnProcessEvent s_originalPE = nullptr;

// Pending invoke queue
static std::mutex s_queueMutex;
static std::queue<std::shared_ptr<InvokeRequest>> s_invokeQueue;
// Relaxed mirror of s_invokeQueue.size(), maintained under s_queueMutex. Lets the
// hot ProcessEvent hook skip taking the mutex on the overwhelmingly common path
// where no invoke is pending (ProcessEvent fires thousands of times per second).
static std::atomic<size_t> s_queueDepth{0};

// Hook state
static std::atomic<bool> s_hookActive{false};
static std::atomic<bool> s_mhInitialized{false};
static uintptr_t s_hookedAddr = 0;

// Timeout for waiting on game-thread execution. Atomic so the pipe thread
// (handling set_invoke_timeout) can update it while another pipe call is
// already blocked in EnqueueInvoke without locking. Re-read on each invoke;
// already-pending requests keep their original timeout (consistent with how
// future.wait_for is captured at call time).
static std::atomic<int32_t> s_invokeTimeoutMs{kDefaultInvokeTimeoutMs};

// Hook fire counter — incremented every time HookedProcessEvent runs.
// Used by post-install validation (Frieren::TryInstallGameThreadHook): a
// correctly-placed PE hook fires many times per second under normal
// gameplay, so a 0 count ~1.5s after install means we hooked the wrong
// vtable slot. relaxed memory order — readers just want a non-zero check.
static std::atomic<uint64_t> s_hookFireCount{0};

// ---- SEH-isolated helper ----

/// Call ProcessEvent with SEH protection. Isolated into a separate function
/// because MSVC does not allow __try in functions with C++ objects that
/// require unwinding (shared_ptr, vector, promise, etc.).
/// Returns 0 on success, -3 if no original PE, -4 on SEH exception.
static int32_t CallProcessEventSEH(uintptr_t instance, uintptr_t ufunc, uintptr_t params) {
    if (!s_originalPE) return -3;
    __try {
        s_originalPE(
            reinterpret_cast<void*>(instance),
            reinterpret_cast<void*>(ufunc),
            reinterpret_cast<void*>(params));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -4;
    }
    return 0;
}

// ---- Hook function ----

/// Hooked ProcessEvent — called on the game thread for every UObject event.
/// Drains the invoke queue first, then calls the original PE for the game's own call.
static void __fastcall HookedProcessEvent(void* thisObj, void* ufunc, void* params) {
    // Tick the fire counter first thing. Even if the queue is empty and we
    // pass straight through to s_originalPE, this gives the post-install
    // validator (Frieren) ground truth that "we are sitting on the right
    // vtable slot." relaxed: a single non-zero observation by the validator
    // is enough; we never read this back inside the hot path.
    s_hookFireCount.fetch_add(1, std::memory_order_relaxed);

    // Drain pending invocations from pipe thread. Fast path: skip the mutex
    // entirely unless the pipe thread has actually enqueued something. A stale
    // zero read just defers a freshly enqueued request to the next PE call
    // (microseconds away), which is harmless — the real happens-before for the
    // request data is the mutex taken below.
    if (s_queueDepth.load(std::memory_order_acquire) != 0) {
        std::vector<std::shared_ptr<InvokeRequest>> pending;

        {
            std::lock_guard<std::mutex> lock(s_queueMutex);
            while (!s_invokeQueue.empty()) {
                pending.push_back(std::move(s_invokeQueue.front()));
                s_invokeQueue.pop();
            }
            s_queueDepth.store(0, std::memory_order_release);
        }

        // Execute all pending requests outside the lock
        for (auto& req : pending) {
            int32_t result = CallProcessEventSEH(req->instance, req->ufunc, req->params);

            if (result == -4) {
                LOG_ERROR("GameThreadDispatch: SEH exception during queued PE call "
                          "inst=0x%llX func=0x%llX",
                          (unsigned long long)req->instance,
                          (unsigned long long)req->ufunc);
            }

            // Fulfill the promise — unblocks the waiting pipe thread
            try {
                req->promise.set_value(result);
            } catch (...) {
                // Promise already satisfied (shouldn't happen, but be safe)
            }
        }
    }

    // Now handle the game's own ProcessEvent call
    if (s_originalPE) {
        s_originalPE(thisObj, ufunc, params);
    }
}

// ---- Public API ----

bool InstallHook(uintptr_t processEventAddr) {
    if (s_hookActive.load()) {
        LOG_WARN("GameThreadDispatch: hook already active");
        return true;
    }

    if (!processEventAddr) {
        LOG_ERROR("GameThreadDispatch: null processEventAddr");
        return false;
    }

    // Audit fix #13: re-enable after soft disable. RemoveHook only flips
    // s_hookActive to false (the physical hook stays installed to avoid
    // an in-flight unhook race), so a second InstallHook on the same
    // address just flips the flag back on. No MinHook calls needed.
    if (s_hookedAddr == processEventAddr && s_originalPE != nullptr) {
        s_hookActive.store(true);
        LOG_INFO("GameThreadDispatch: re-enabled existing hook at 0x%llX",
                 (unsigned long long)processEventAddr);
        return true;
    }

    // Initialize MinHook (once)
    if (!s_mhInitialized.load()) {
        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
            LOG_ERROR("GameThreadDispatch: MH_Initialize failed: %s",
                      MH_StatusToString(status));
            return false;
        }
        s_mhInitialized.store(true);
    }

    // Create hook
    MH_STATUS status = MH_CreateHook(
        reinterpret_cast<LPVOID>(processEventAddr),
        reinterpret_cast<LPVOID>(&HookedProcessEvent),
        reinterpret_cast<LPVOID*>(&s_originalPE));

    if (status != MH_OK) {
        LOG_ERROR("GameThreadDispatch: MH_CreateHook failed: %s",
                  MH_StatusToString(status));
        return false;
    }

    // Enable hook
    status = MH_EnableHook(reinterpret_cast<LPVOID>(processEventAddr));
    if (status != MH_OK) {
        LOG_ERROR("GameThreadDispatch: MH_EnableHook failed: %s",
                  MH_StatusToString(status));
        MH_RemoveHook(reinterpret_cast<LPVOID>(processEventAddr));
        return false;
    }

    s_hookedAddr = processEventAddr;
    s_hookActive.store(true);
    LOG_INFO("GameThreadDispatch: ProcessEvent hook installed at 0x%llX",
             (unsigned long long)processEventAddr);
    return true;
}

void RemoveHook() {
    if (!s_hookActive.load()) return;

    // Audit fix #13: do NOT call MH_DisableHook + MH_RemoveHook. They patch
    // the original code page back and free the trampoline — but a game
    // thread may be executing INSIDE the trampoline at this exact moment,
    // and MinHook does not synchronize with in-flight calls. Unhooking
    // under it is a guaranteed crash.
    //
    // Soft disable: just flip the active flag. EnqueueInvoke now returns
    // -7 immediately, so no new requests reach the queue. HookedProcessEvent
    // remains the entry point — with an empty queue (drained by Shutdown
    // for a clean stop), it just forwards to s_originalPE. The few KB of
    // trampoline memory persists until process exit, where the OS reclaims
    // it.
    //
    // s_originalPE / s_hookedAddr are intentionally NOT cleared so that
    // (a) HookedProcessEvent can still forward to the original PE for any
    //     game thread still inside our trampoline at this moment, and
    // (b) a subsequent InstallHook on the same address can fast-path
    //     re-enable via the s_hookedAddr / s_originalPE check above.
    s_hookActive.store(false);

    LOG_INFO("GameThreadDispatch: hook flag cleared (physical hook retained "
             "to avoid in-flight unhook race)");
}

void Shutdown() {
    // Soft-disable the hook (audit fix #13).
    RemoveHook();

    // Drop any pending queued invokes so waiting pipe threads get a result
    // instead of blocking on a promise no one will ever fulfill.
    {
        std::lock_guard<std::mutex> lock(s_queueMutex);
        while (!s_invokeQueue.empty()) {
            auto req = std::move(s_invokeQueue.front());
            s_invokeQueue.pop();
            try {
                req->promise.set_value(-7); // hook not active
            } catch (...) {
                // promise already satisfied — ignore
            }
        }
        s_queueDepth.store(0, std::memory_order_release);
    }

    // Audit fix #14: do NOT call MH_Uninitialize. It patches every hooked
    // module's code page back and frees all trampolines — same in-flight
    // crash risk as MH_RemoveHook. MinHook's tables stay in memory; the
    // OS reclaims them on process exit.
    //
    // s_mhInitialized intentionally remains true so that a future re-init
    // path (currently not exercised) would skip MH_Initialize and reuse
    // existing state.
}

bool IsHookActive() {
    return s_hookActive.load();
}

int32_t EnqueueInvoke(uintptr_t instance, uintptr_t ufunc, uintptr_t params, size_t paramsSize) {
    if (!s_hookActive.load()) {
        return -7; // Hook not active
    }

    auto req = std::make_shared<InvokeRequest>();
    req->instance = instance;
    req->ufunc = ufunc;
    // Own a copy of the param bytes so a timed-out-but-still-queued request can
    // never dereference a freed caller buffer (use-after-free). When the caller
    // passes size 0 (a persistent buffer like Mimic's global), use the pointer
    // as-is — that buffer outlives the request.
    if (paramsSize > 0 && params != 0) {
        const auto* src = reinterpret_cast<const uint8_t*>(params);
        req->ownedParams.assign(src, src + paramsSize);
        req->params = reinterpret_cast<uintptr_t>(req->ownedParams.data());
    } else {
        req->params = params;
    }

    auto future = req->promise.get_future();

    {
        std::lock_guard<std::mutex> lock(s_queueMutex);
        // Push a COPY of the shared_ptr (refcount stays >=1 locally) so `req`
        // remains valid below for the out-param copy-back even after the game
        // thread drains and pops the queue entry.
        s_invokeQueue.push(req);
        s_queueDepth.fetch_add(1, std::memory_order_release);  // wake the hook's fast-path gate
    }

    LOG_INFO("GameThreadDispatch: enqueued invoke inst=0x%llX func=0x%llX, waiting...",
             (unsigned long long)instance, (unsigned long long)ufunc);

    // Wait for game thread to execute the request
    int32_t timeoutMs = s_invokeTimeoutMs.load();
    auto status = future.wait_for(std::chrono::milliseconds(timeoutMs));
    if (status == std::future_status::timeout) {
        LOG_ERROR("GameThreadDispatch: invoke timeout (%dms) inst=0x%llX func=0x%llX",
                  timeoutMs,
                  (unsigned long long)instance, (unsigned long long)ufunc);
        // The request stays queued, but it owns its param buffer, so the eventual
        // game-thread execution is safe. We just abandon the (now stale) result.
        return -5;
    }

    int32_t result = future.get();
    // Propagate out-params written by the game thread back to the caller's buffer
    // (only when we owned a copy; the size-0 path wrote the caller's buffer directly).
    if (!req->ownedParams.empty() && params != 0) {
        memcpy(reinterpret_cast<void*>(params), req->ownedParams.data(), req->ownedParams.size());
    }
    LOG_INFO("GameThreadDispatch: invoke completed result=%d", result);
    return result;
}

// Public setter/getter for the invoke timeout. Clamped to a sane band so
// a misbehaving UI can't accidentally hang every UFunction call forever
// (or, conversely, set such a tight value that everything always times out).
void SetInvokeTimeoutMs(int32_t timeoutMs) {
    if (timeoutMs <= 0) {
        s_invokeTimeoutMs.store(kDefaultInvokeTimeoutMs);
        return;
    }
    if (timeoutMs < 100)    timeoutMs = 100;
    if (timeoutMs > 600000) timeoutMs = 600000;
    s_invokeTimeoutMs.store(timeoutMs);
}

int32_t GetInvokeTimeoutMs() {
    return s_invokeTimeoutMs.load();
}

uint64_t GetHookFireCount() {
    return s_hookFireCount.load(std::memory_order_relaxed);
}

} // namespace Stark
