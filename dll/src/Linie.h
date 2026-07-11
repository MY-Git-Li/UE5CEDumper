#pragma once

// ============================================================
// Linie — 莉涅 (讀取魔力者 — the mage who reads an opponent's mana)
// Live ProcessEvent call profiler: per-UFunction* fire-count table,
// recorded from Stark's ProcessEvent hook during an opt-in Start/Stop
// window. Behaviour-based UFunction discovery — the user performs an
// in-game action (open shop / dash) and sees which UFunctions fired,
// ranked by count. Counting is gated by an atomic so the not-recording
// hot path pays only one relaxed load.
// ============================================================

#include <cstdint>
#include <atomic>
#include <vector>
#include <utility>

namespace Linie {

// Hot-path gate. Defined in Linie.cpp; declared extern so the check inlines at
// Stark's call site (one relaxed atomic load + predicted-not-taken branch when off).
extern std::atomic<bool> g_recording;
inline bool IsRecording() { return g_recording.load(std::memory_order_relaxed); }

// Record one PE fire for `ufunc`. ONLY call when IsRecording() is already true
// (Stark inlines that check). Takes the profile mutex; safe from any thread.
void RecordCall(uintptr_t ufunc);

// Clear the table (reserve to bound rehash churn), then flip recording on
// under the lock so there is never a half-cleared window.
void StartRecording();

// Flip recording off; the accumulated counts are retained for a later Snapshot.
void StopRecording();

// == IsRecording(). Named for readers at the pipe layer.
bool IsActive();

// Clear the table + force recording off. Called on client disconnect / shutdown
// so a stale recording never leaks across connections and the map is freed.
void Reset();

// Copy out {UFunction* addr -> count} pairs. Safe to call while recording.
void Snapshot(std::vector<std::pair<uintptr_t, uint64_t>>& out);

} // namespace Linie
