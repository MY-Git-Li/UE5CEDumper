#pragma once

// ============================================================
// Routine — ルティーネ, 影の戦士の司書 (Shadow Warrior librarian)
// Periodic-worker scaffolding shared by every re-assert / hold module.
//
// Six modules (Solitar / Laufen / Hemmung / Solide / Dunste / Schlacht) run the same
// worker: mark the thread cancel-immune, sleep a period in short slices so a stop is
// honoured promptly, do one tick, repeat. That shape had been hand-copied six times,
// and audit #4 measured what hand-copying costs: the exception guard that build 2389
// added for a LIVE-reproduced 0xC0000409 reached 2 of the 7 thread procs, and the
// 25 ms sleep slice existed as 8 bare literals (B14 + R5).
//
// An exception escaping a thread entry is std::terminate, not a caught error. A worker
// tick reads live game state and allocates from it, so during the game's own teardown a
// read or a std::vector sizing can throw — which is precisely the crash that was
// observed. Every tick therefore runs inside RunTickGuarded, once, here.
//
// LOG_CAT: the macros below expand at the INCLUDE site, so include this header after
// the module's `#define LOG_CAT "..."` (every module already defines it first). Each
// module's worker then logs under its own category, exactly as the hand-copied
// versions did.
// ============================================================

#include "Sein.h"
#include "Grimoire.h"
#include "Tot.h"

#include <atomic>
#include <chrono>
#include <exception>
#include <thread>

namespace Routine {

// Run one worker tick with the guard a thread entry must have. `warnedOnce` is the
// caller's loop-local latch so a game tearing down for several seconds logs one line,
// not one per tick.
template <typename TickFn>
inline void RunTickGuarded(const char* tag, bool& warnedOnce, TickFn&& tick) {
    try {
        tick();
    } catch (const std::exception& e) {
        if (!warnedOnce) {
            warnedOnce = true;
            LOG_WARN("%s: tick threw (%s) — skipping (game tearing down?)", tag, e.what());
        }
    } catch (...) {
        if (!warnedOnce) {
            warnedOnce = true;
            LOG_WARN("%s: tick threw (unknown) — skipping (game tearing down?)", tag);
        }
    }
}

// Sleep `totalMs` in WORKER_SLEEP_SLICE_MS slices so StopWorker()'s join waits at most
// one slice instead of a whole period. Returns false if the stop flag was raised (or a
// shutdown began) while sleeping — i.e. "do not run the tick".
inline bool SleepSliced(int totalMs, const std::atomic<bool>& stop) {
    for (int slept = 0; slept < totalMs; slept += Grimoire::WORKER_SLEEP_SLICE_MS) {
        if (stop.load() || Tot::ShutdownRequested()) return false;
        std::this_thread::sleep_for(
            std::chrono::milliseconds(Grimoire::WORKER_SLEEP_SLICE_MS));
    }
    return !stop.load() && !Tot::ShutdownRequested();
}

// The whole re-assert loop: cancel-immunity, start/stop logging, sliced sleep, guarded
// tick. `tick` owns everything module-specific — including its own drift counting and
// wording, which is deliberate: those WARN strings are individually worded and are
// grepped by the log-verification checklist, so they are not templated away.
//
// The loop also breaks on Tot::ShutdownRequested() (via SleepSliced), which the
// hand-copied versions did not: they only tested their own stop flag, so a worker whose
// StopWorker never ran could keep walking reflection against a tearing-down process.
template <typename TickFn>
inline void ReassertLoop(const char* tag, int periodMs,
                         const std::atomic<bool>& stop, TickFn&& tick) {
    Tot::MarkBackgroundWorker();   // ignore per-command cancel; abort only on shutdown (M4)
    LOG_INFO("%s: re-assert worker started (%d ms)", tag, periodMs);
    bool warnedThrow = false;
    while (SleepSliced(periodMs, stop))
        RunTickGuarded(tag, warnedThrow, tick);
    LOG_INFO("%s: re-assert worker stopped", tag);
}

} // namespace Routine
