#pragma once

// ============================================================
// Tot — 托托, 終極聖女托托 ("Saint of the End")
// Cancellation: cooperative cancel flag for long-running DLL operations.
//
// The pipe server (Fern) processes one command at a time, synchronously,
// on a single connection. A long scan therefore BLOCKS the pipe thread
// until it returns. Two failure modes motivated this module:
//
//   1. "Game won't close": disabling the CE script calls UE5_Shutdown ->
//      Fern::Stop(), which JOINS the accept thread. If that thread is mid
//      scan, the join blocks until the scan finishes (potentially long /
//      unbounded). RequestShutdown() lets every long loop bail promptly so
//      the join completes fast.
//
//   2. "DLL keeps scanning after the UI closes": the client disconnects but
//      the blocked handler can't notice until it returns. Fern's monitor
//      thread peeks the pipe while a command is in-flight and calls
//      RequestPerCommand() on a broken pipe, so the orphaned scan bails and
//      the pipe frees for the next (reconnecting) client.
//
// Long loops poll Requested() every N iterations (cheap relaxed atomic load)
// and bail with an empty / partial result. Per-command cancellation is reset
// when a fresh session connects into an empty connection registry (Fern
// AcceptLoop firstConn), NOT per-command — a light command on one lane must not
// clear a running scan's cancel on another lane; shutdown is sticky. Background
// re-assert workers opt out of the per-command cancel via MarkBackgroundWorker()
// (they live for the game process, not a single pipe command). (M4)
// ============================================================

#include <atomic>

namespace Tot {

// Set when the connected client disconnects mid-command (Fern monitor).
// Cleared by ResetPerCommand() when a fresh session connects into an empty
// registry (Fern AcceptLoop firstConn) — kept latched until then so an orphaned
// in-flight scan on the dropped connection keeps aborting (do NOT clear it at
// disconnect: the orphaned scan must still see the cancel until it unwinds).
inline std::atomic<bool> g_perCommand{false};

// Sticky: set at the TOP of UE5_Shutdown() (and by Fern::Stop()) so in-flight
// ops abort and the accept-thread join completes quickly, AND so every module's
// StartWorker* refuses to (re)spawn a re-assert worker during the shutdown window
// (join → Stark::Shutdown → pipe stop) — otherwise a pipe/mailbox command landing
// in that window could revive a just-joined worker that nothing joins again (M5).
// Cleared only by Fern::Start() (ResetShutdown) so re-enabling the CE script in
// the same game process brings the server back to a non-aborting state — otherwise
// every long op would bail on its first Requested() poll.
inline std::atomic<bool> g_shutdown{false};

// Marks the current thread as a background re-assert worker (Solide/Hemmung/
// Laufen/Solitar/Dunste/Schlacht). Such a thread lives for the whole game
// process, not a single pipe command, so it ignores the per-command cancel (set
// when a pipe client drops mid-command) and aborts only on real shutdown —
// otherwise a client disconnect silently freezes every hold. Notably Solide's
// only instance source (Aura::FindInstancesByClass) polls Requested() at n=0 and
// would bail to an EMPTY set every re-assert tick while a client is gone. (M4)
inline thread_local bool t_backgroundWorker = false;
inline void MarkBackgroundWorker() { t_backgroundWorker = true; }

// True when any in-flight long-running operation should abort. On a background
// worker thread only real shutdown aborts (see MarkBackgroundWorker); on a pipe
// command thread a mid-command client disconnect (per-command) aborts too — so
// the SAME resolve helper honours the cancel when called from a pipe command but
// keeps running when called from a re-assert worker.
inline bool Requested() {
    if (t_backgroundWorker)
        return g_shutdown.load(std::memory_order_relaxed);
    return g_perCommand.load(std::memory_order_relaxed)
        || g_shutdown.load(std::memory_order_relaxed);
}

// True once teardown has begun (g_shutdown). Used by the worker (re)start gate to
// refuse spawning a re-assert worker during the shutdown window. (M5)
inline bool ShutdownRequested() { return g_shutdown.load(std::memory_order_relaxed); }

inline void RequestPerCommand() { g_perCommand.store(true,  std::memory_order_relaxed); }
inline void ResetPerCommand()   { g_perCommand.store(false, std::memory_order_relaxed); }
inline void RequestShutdown()   { g_shutdown.store(true,    std::memory_order_relaxed); }
inline void ResetShutdown()     { g_shutdown.store(false,   std::memory_order_relaxed); }

} // namespace Tot
