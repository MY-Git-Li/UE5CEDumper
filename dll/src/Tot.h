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
// at the start of each command; shutdown is sticky.
// ============================================================

#include <atomic>

namespace Tot {

// Set when the connected client disconnects mid-command (Fern monitor).
// Cleared by ResetPerCommand() at the start of each new command.
inline std::atomic<bool> g_perCommand{false};

// Sticky: set by Fern::Stop() / UE5_Shutdown() so in-flight ops abort and
// the accept-thread join completes quickly. Cleared only by Fern::Start()
// (ResetShutdown) so re-enabling the CE script in the same game process
// brings the server back to a non-aborting state — otherwise every long op
// would bail on its first Requested() poll.
inline std::atomic<bool> g_shutdown{false};

// True when any in-flight long-running operation should abort.
inline bool Requested() {
    return g_perCommand.load(std::memory_order_relaxed)
        || g_shutdown.load(std::memory_order_relaxed);
}

inline void RequestPerCommand() { g_perCommand.store(true,  std::memory_order_relaxed); }
inline void ResetPerCommand()   { g_perCommand.store(false, std::memory_order_relaxed); }
inline void RequestShutdown()   { g_shutdown.store(true,    std::memory_order_relaxed); }
inline void ResetShutdown()     { g_shutdown.store(false,   std::memory_order_relaxed); }

} // namespace Tot
