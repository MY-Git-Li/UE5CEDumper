// ============================================================
// Linie — 莉涅 (讀取魔力者 — the mage who reads an opponent's mana)
// Live ProcessEvent call profiler — implementation. See Linie.h.
//
// Data model: unordered_map<UFunction* addr, fire count> guarded by a
// DEDICATED mutex (never Stark's queue mutex — that would drag profiler
// contention into the invoke-drain critical section). Recording gated by
// a single atomic<bool> so Stark's not-recording hot path never touches
// the mutex or the map.
// ============================================================

#include "Linie.h"

#include <mutex>
#include <unordered_map>

namespace Linie {

std::atomic<bool> g_recording{false};

static std::mutex g_mu;
static std::unordered_map<uintptr_t, uint64_t> g_counts;

void RecordCall(uintptr_t ufunc) {
    std::lock_guard<std::mutex> lk(g_mu);
    ++g_counts[ufunc];
}

void StartRecording() {
    std::lock_guard<std::mutex> lk(g_mu);
    g_counts.clear();
    g_counts.reserve(4096);  // bound rehash churn over the recording window
    // Flip on UNDER the lock so a concurrent RecordCall can never observe
    // recording==true against a half-cleared table.
    g_recording.store(true, std::memory_order_relaxed);
}

void StopRecording() {
    g_recording.store(false, std::memory_order_relaxed);
}

bool IsActive() {
    return g_recording.load(std::memory_order_relaxed);
}

void Reset() {
    std::lock_guard<std::mutex> lk(g_mu);
    g_recording.store(false, std::memory_order_relaxed);
    g_counts.clear();
}

void Snapshot(std::vector<std::pair<uintptr_t, uint64_t>>& out) {
    std::lock_guard<std::mutex> lk(g_mu);
    out.assign(g_counts.begin(), g_counts.end());
}

} // namespace Linie
