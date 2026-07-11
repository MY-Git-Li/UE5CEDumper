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

struct Stat { uint64_t count = 0; uint64_t firstSeq = 0; };
static std::mutex g_mu;
static std::unordered_map<uintptr_t, Stat> g_stats;
// Monotonic call-stream position (1-based), reset per recording. A function's
// firstSeq is g_seq at the moment it was first seen — the causal ordering.
static uint64_t g_seq = 0;

void RecordCall(uintptr_t ufunc) {
    std::lock_guard<std::mutex> lk(g_mu);
    uint64_t seq = ++g_seq;
    auto& s = g_stats[ufunc];       // default-constructs {0,0} on first sight
    if (s.count == 0) s.firstSeq = seq;
    ++s.count;
}

void StartRecording() {
    std::lock_guard<std::mutex> lk(g_mu);
    g_stats.clear();
    g_stats.reserve(4096);  // bound rehash churn over the recording window
    g_seq = 0;
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
    g_stats.clear();
    g_seq = 0;
}

void Snapshot(std::vector<FuncStat>& out) {
    std::lock_guard<std::mutex> lk(g_mu);
    out.clear();
    out.reserve(g_stats.size());
    for (const auto& kv : g_stats)
        out.push_back(FuncStat{ kv.first, kv.second.count, kv.second.firstSeq });
}

} // namespace Linie
