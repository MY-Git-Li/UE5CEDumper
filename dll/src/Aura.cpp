// ============================================================
// Aura — 斷頭台的奧拉 (服從之秤 — Obedience Scale)
// ObjectArray: FUObjectArray slot enumeration and validation
// ============================================================

#include "Aura.h"
#include "Macht.h"
#define LOG_CAT "OARR"
#include "Sein.h"
#include "Grimoire.h"
#include "Serie.h"

#include "Ubel.h"
#include "Genau.h"

// Defined in Frieren.cpp — cached UE version for layout branching
extern uint32_t g_cachedUEVersion;

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <climits>
#include <cstring>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace Aura {

// FUObjectArray layout offsets (auto-detected)
struct ArrayLayout {
    int32_t objectsOffset;    // FUObjectItem** Objects
    int32_t maxElementsOffset;
    int32_t numElementsOffset;
    int32_t maxChunksOffset;
    int32_t numChunksOffset;
};

static uintptr_t  s_arrayAddr = 0;
static ArrayLayout s_layout = { 0x00, 0x10, 0x14, 0x18, 0x1C }; // Default layout
static int         s_itemSize = 16;  // FUObjectItem stride (auto-detected: 16 or 24)
static bool        s_isFlat   = false; // true = non-chunked flat array (some UE4 builds)

// GAP #1: Decryption hook for encrypted GObjects pointers.
// Default nullptr = identity (zero overhead — no indirect call on hot path).
// Set by SetDecryptFunc() from CE Lua export before Init().
static Aura::DecryptFunc s_decryptFunc = nullptr;

void Aura::SetDecryptFunc(DecryptFunc func) {
    s_decryptFunc = func;
    LOG_INFO("ObjectArray: Custom decryption function %s",
             func ? "SET" : "CLEARED (identity)");
}

// ============================================================
// Parallel GObjects-walk infrastructure
// ============================================================
//
// The value/reference/container scans below all walk the entire GObjects
// array (object-by-object, reading structured properties). On large games
// (1M+ objects, multi-GB heap) this single-threaded walk is the wall-clock
// floor. Because the walk is READ-ONLY against game memory + init-time
// constants (FNamePool offsets, g_cachedUEVersion, the FUObjectArray layout),
// it parallelizes cleanly: partition the index range into contiguous chunks,
// give each thread its own caches + result buffer, then merge in chunk order
// so the global result stays ascending-index ordered (matching the serial
// semantics exactly). Mirrors the discrete dumper's RunParallelScan design
// (docs reference: Memory-Scanning-Internals.md §16).
namespace {

// Worker thread count for a GObjects walk of `workItems` objects. Leaves
// headroom (-2) for the game's own threads + our pipe/UI thread so we don't
// saturate every core and stall the host. Clamped to [1, 16]; tiny arrays
// stay single-threaded (thread spawn cost would dominate).
int ScanThreadCount(int32_t workItems) {
    if (workItems < 8192) return 1;
    unsigned hc = std::thread::hardware_concurrency();
    int n = (hc >= 3) ? static_cast<int>(hc) - 2 : 1;
    if (n < 1)  n = 1;
    if (n > 16) n = 16;
    return n;
}

// Partition [0, count) into `nthreads` contiguous ascending ranges and run
// body(tid, beginIdx, endIdx) on each. Chunk 0 runs inline on the calling
// thread; the rest run on spawned std::threads which are joined before
// return. Ascending, non-overlapping ranges mean a per-thread result merge
// in tid order reproduces the serial ascending-index ordering.
template <typename BodyFn>
void ParallelIndexRanges(int32_t count, int nthreads, BodyFn&& body) {
    if (count <= 0) return;

    // A worker body must never let an exception escape: an exception leaving a
    // std::thread callable — or an un-joined joinable thread during stack
    // unwinding — calls std::terminate, which would crash the host game. The
    // scans are best-effort, so a throwing chunk just stops early; its partial
    // per-thread results still merge, exactly like a deadline hit.
    auto runChunk = [&body](int tid, int32_t b, int32_t e) {
        try {
            body(tid, b, e);
        } catch (...) {
            LOG_WARN("ParallelIndexRanges: worker tid=%d [%d,%d) threw — dropping chunk",
                     tid, b, e);
        }
    };

    if (nthreads <= 1) { runChunk(0, 0, count); return; }

    // 64-bit chunk math so a corrupted/huge object count can't overflow the
    // int32 multiply when computing a high thread's start offset.
    const int64_t chunk = (static_cast<int64_t>(count) + nthreads - 1) / nthreads;
    std::vector<std::thread> pool;
    pool.reserve(static_cast<size_t>(nthreads) - 1);
    for (int t = 1; t < nthreads; ++t) {
        const int64_t b = static_cast<int64_t>(t) * chunk;
        if (b >= count) break;
        const int32_t bi = static_cast<int32_t>(b);
        const int32_t ei = static_cast<int32_t>(std::min<int64_t>(b + chunk, count));
        pool.emplace_back([&runChunk, t, bi, ei]() { runChunk(t, bi, ei); });
    }
    runChunk(0, 0, static_cast<int32_t>(std::min<int64_t>(chunk, count)));  // chunk 0 inline
    for (auto& th : pool) th.join();
}

// Result of a ParallelGObjectsScan run. `perThread` is exposed so callers can
// fold their own per-thread stat fields (counters, sets) after the run;
// `deadlineHit` is the post-join load of the shared flag; `nthreads` is what
// ScanThreadCount picked (for logging).
template <typename PerThreadT>
struct ParallelScanResult {
    std::vector<PerThreadT> perThread;
    int                     nthreads    = 1;
    bool                    deadlineHit = false;
};

// Run a parallel GObjects walk: spawn ScanThreadCount(count) workers over
// contiguous ascending index ranges, each writing into its own PerThreadT.
// `body(tr, beginIdx, endIdx, deadlineHit)` is the per-thread loop — it owns the
// per-object work, the per-thread local maxResults cap, and the deadline check
// (call deadlineHit.store(true) to signal siblings to stop). This factors out
// the nthreads / perThread-vector / atomic-deadline / ParallelIndexRanges
// boilerplate the three scans shared. The per-thread result-vector merge is left
// to the caller (via ConcatTruncate) because the element type differs per scan.
// After join, returns {perThread (moved), nthreads, deadlineHit.load()}.
template <typename PerThreadT, typename BodyFn>
ParallelScanResult<PerThreadT> ParallelGObjectsScan(int32_t count, BodyFn&& body) {
    const int nthreads = ScanThreadCount(count);
    std::vector<PerThreadT> perThread(static_cast<size_t>(std::max(1, nthreads)));
    std::atomic<bool> deadlineHit{false};

    ParallelIndexRanges(count, nthreads, [&](int tid, int32_t beginIdx, int32_t endIdx) {
        body(perThread[tid], beginIdx, endIdx, deadlineHit);
    });

    return { std::move(perThread), nthreads, deadlineHit.load() };
}

// Concatenate each thread's result vector (selected by pointer-to-member) in
// ascending tid order, stopping at maxResults. Each worker scanned a contiguous
// ascending index range and locally capped at maxResults, so this reproduces the
// serial "first N in ascending index order" set exactly (keeps the lowest-index
// subset when truncating). Elements are moved out of `perThread`.
template <typename PerThreadT, typename ElemT>
std::vector<ElemT> ConcatTruncate(std::vector<PerThreadT>& perThread,
                                  std::vector<ElemT> PerThreadT::* member,
                                  int32_t maxResults) {
    std::vector<ElemT> out;
    for (auto& tr : perThread) {
        for (auto& item : tr.*member) {
            if (static_cast<int32_t>(out.size()) >= maxResults) return out;
            out.push_back(std::move(item));
        }
    }
    return out;
}

} // namespace

uintptr_t Aura::DecryptObjectPtr(uintptr_t rawPtr) {
    if (!rawPtr || !s_decryptFunc) return rawPtr;
    return s_decryptFunc(rawPtr);
}

// GAP #2: Named preset layouts for FChunkedFixedUObjectArray (from Dumper-7 reference).
// Games can reorder struct members; these presets cover known variants.
struct LayoutPreset {
    const char* name;
    ArrayLayout layout;
};

// All known chunked layouts. Order: default first, then game-specific.
static const LayoutPreset s_chunkedPresets[] = {
    { "Default",     { 0x00, 0x10, 0x14, 0x18, 0x1C } },  // UE4.21+ and UE5 standard
    { "Back4Blood",  { 0x10, 0x00, 0x04, 0x08, 0x0C } },  // Objects at end
    { "Multiversus", { 0x18, 0x10, 0x00, 0x14, 0x20 } },  // NumElements first
    { "MindsEye",    { 0x18, 0x00, 0x14, 0x10, 0x04 } },  // MaxElements first
    { "UE5.8",       { 0x00, 0x0C, 0x08, 0x14, 0x10 } },  // 5.8 dev: FUObjectArray fields reordered for cache locality, PreAllocatedObjects moved to end
};
static constexpr int NUM_CHUNKED_PRESETS = sizeof(s_chunkedPresets) / sizeof(s_chunkedPresets[0]);

// Extended: FUObjectArray with GC index fields before ObjObjects.
// UE4-Extended: no PreAllocatedObjects ptr in FChunkedFixedUObjectArray.
// UE5-Extended: has PreAllocatedObjects ptr (+8 bytes shift). TQ2 (UE 5.7) confirmed.
static const LayoutPreset s_ue4ExtendedPresets[] = {
    { "UE4-Extended", { 0x10, 0x18, 0x1C, 0x20, 0x24 } },
    { "UE5-Extended", { 0x10, 0x20, 0x24, 0x28, 0x2C } },
};
static constexpr int NUM_UE4_EXTENDED_PRESETS = sizeof(s_ue4ExtendedPresets) / sizeof(s_ue4ExtendedPresets[0]);

// Flat (non-chunked) FFixedUObjectArray layout.
// Objects* points directly to FUObjectItem[] (no chunk pointer indirection).
// Used by early UE4 (4.11-4.22) including Octopath Traveller.
// Layout: { Objects*(8), MaxElements(4), NumElements(4) } — total 16 bytes before FCriticalSection.
static const LayoutPreset s_flatPresets[] = {
    { "Flat", { 0x00, 0x08, 0x0C, -1, -1 } },
};
static constexpr int NUM_FLAT_PRESETS = sizeof(s_flatPresets) / sizeof(s_flatPresets[0]);

// Helper: check if a pointer value looks like a valid heap pointer (not code/null/low)
static bool LooksLikeHeapPtr(uintptr_t ptr) {
    if (!ptr || ptr < 0x10000) return false;
    // Must be in user-mode address range (below kernel boundary)
    if (ptr > 0x00007FFFFFFFFFFF) return false;
    // Reject pointers in the game module's code range (likely .text section)
    uintptr_t modBase = Macht::GetModuleBase(nullptr);
    uintptr_t modSize = Macht::GetModuleSize(nullptr);
    if (modBase && modSize && ptr >= modBase && ptr < modBase + modSize) return false;
    return true;
}

// Log all 5 layout field values at an address for diagnosis.
static void LogLayoutFields(uintptr_t addr, const ArrayLayout& layout, const char* presetName) {
    int32_t numElements = 0, maxElements = 0, numChunks = 0, maxChunks = 0;
    uintptr_t objPtr = 0;
    Macht::ReadSafe(addr + layout.numElementsOffset, numElements);
    Macht::ReadSafe(addr + layout.maxElementsOffset, maxElements);
    Macht::ReadSafe(addr + layout.objectsOffset, objPtr);
    if (layout.maxChunksOffset >= 0) Macht::ReadSafe(addr + layout.maxChunksOffset, maxChunks);
    if (layout.numChunksOffset >= 0) Macht::ReadSafe(addr + layout.numChunksOffset, numChunks);

    uintptr_t decObjPtr = DecryptObjectPtr(objPtr);
    LOG_INFO("ObjectArray: Layout '%s': Num=%d, Max=%d, NumChunks=%d, MaxChunks=%d, Objects=0x%llX%s",
             presetName, numElements, maxElements, numChunks, maxChunks,
             (unsigned long long)decObjPtr,
             (objPtr != decObjPtr) ? " (decrypted)" : "");
}

// Full validation of a chunked FUObjectArray layout (Dumper-7 rigor).
// Reads all 5 fields and checks range, alignment, and consistency.
static bool ValidateChunkedLayout(uintptr_t addr, const ArrayLayout& layout) {
    int32_t numElements = 0, maxElements = 0, numChunks = 0, maxChunks = 0;
    uintptr_t objPtr = 0;

    if (!Macht::ReadSafe(addr + layout.numElementsOffset, numElements)) return false;
    if (!Macht::ReadSafe(addr + layout.maxElementsOffset, maxElements)) return false;
    if (!Macht::ReadSafe(addr + layout.objectsOffset, objPtr)) return false;
    objPtr = DecryptObjectPtr(objPtr);

    bool hasChunkFields = (layout.maxChunksOffset >= 0 && layout.numChunksOffset >= 0);
    if (hasChunkFields) {
        if (!Macht::ReadSafe(addr + layout.maxChunksOffset, maxChunks)) return false;
        if (!Macht::ReadSafe(addr + layout.numChunksOffset, numChunks)) return false;
    }

    // --- Range checks ---
    if (numElements < 0x1000 || numElements > 0x400000) return false;
    if (maxElements < numElements || maxElements > 0x800000) return false;

    // Objects pointer must be a valid heap pointer
    if (!LooksLikeHeapPtr(objPtr)) return false;

    // --- Chunk consistency (Dumper-7 rigor, only if chunk fields present) ---
    if (hasChunkFields) {
        if (numChunks < 1 || numChunks > 0x14) return false;
        if (maxChunks < 6 || maxChunks > 0x5FF) return false;
        if (numChunks > maxChunks) return false;

        // MaxElements alignment
        if ((maxElements % 0x10) != 0) return false;

        // Elements per chunk consistency
        int32_t elemPerChunk = maxElements / maxChunks;
        if ((elemPerChunk % 0x10) != 0) return false;
        if (elemPerChunk < 0x8000 || elemPerChunk > 0x80000) return false;

        // Cross-field consistency
        if (((numElements / elemPerChunk) + 1) != numChunks) return false;
        if ((maxElements / elemPerChunk) != maxChunks) return false;
    }

    // --- Pointer dereference validation ---
    uintptr_t chunk0 = 0;
    if (!Macht::ReadSafe(objPtr, chunk0)) return false;

    if (chunk0 == 0) {
        // chunk[0] null — unlikely for valid array, but accept if objPtr is heap
        return true;
    }

    if (!LooksLikeHeapPtr(chunk0)) return false;

    // Validate additional chunk pointers are readable (cap at 5 to avoid excess reads)
    if (hasChunkFields && numChunks > 1) {
        for (int i = 1; i < numChunks && i < 5; ++i) {
            uintptr_t chunkI = 0;
            if (!Macht::ReadSafe(objPtr + i * sizeof(uintptr_t), chunkI)) return false;
            if (chunkI && !LooksLikeHeapPtr(chunkI)) return false;
        }
    }

    return true;
}

static bool DetectLayout(uintptr_t addr) {
    // Diagnostic: dump first 48 bytes at the GObjects address
    {
        uint64_t dump[6] = {};
        Macht::ReadBytesSafe(addr, dump, sizeof(dump));
        LOG_DEBUG("ObjectArray: GObjects@0x%llX: +00:%016llX +08:%016llX +10:%016llX +18:%016llX +20:%016llX +28:%016llX",
                  (unsigned long long)addr,
                  dump[0], dump[1], dump[2], dump[3], dump[4], dump[5]);
    }

    // --- Tier 1: Try all chunked presets with FULL validation (Dumper-7 rigor) ---
    for (int i = 0; i < NUM_CHUNKED_PRESETS; ++i) {
        const auto& preset = s_chunkedPresets[i];
        if (ValidateChunkedLayout(addr, preset.layout)) {
            s_layout = preset.layout;
            LOG_INFO("ObjectArray: Layout '%s' detected (strict, preset %d/%d)",
                     preset.name, i + 1, NUM_CHUNKED_PRESETS);
            LogLayoutFields(addr, s_layout, preset.name);
            return true;
        }
    }

    // --- Tier 2: Try UE4 extended presets with full validation ---
    for (int i = 0; i < NUM_UE4_EXTENDED_PRESETS; ++i) {
        const auto& preset = s_ue4ExtendedPresets[i];
        if (ValidateChunkedLayout(addr, preset.layout)) {
            s_layout = preset.layout;
            LOG_INFO("ObjectArray: Layout '%s' detected (strict)", preset.name);
            LogLayoutFields(addr, s_layout, preset.name);
            return true;
        }
    }

    // --- Tier 3: Flat (non-chunked) presets ---
    // FFixedUObjectArray: Objects* is a direct FUObjectItem[], no chunk pointer table.
    // ValidateChunkedLayout handles this (hasChunkFields=false skips chunk checks).
    for (int i = 0; i < NUM_FLAT_PRESETS; ++i) {
        const auto& preset = s_flatPresets[i];
        if (ValidateChunkedLayout(addr, preset.layout)) {
            s_layout = preset.layout;
            s_isFlat = true;
            LOG_INFO("ObjectArray: Layout '%s' detected (flat, non-chunked)", preset.name);
            LogLayoutFields(addr, s_layout, preset.name);
            return true;
        }
    }

    // --- Tier 4: RELAXED fallback (preserves current behavior, prevents regression) ---
    // Some games pass weak checks but fail strict Dumper-7 chunk consistency.
    LOG_INFO("ObjectArray: Strict validation failed for all presets, trying relaxed fallback...");

    // Layout A/C (relaxed): Objects@+0x00, Num@+0x14
    {
        int32_t num = 0;
        Macht::ReadSafe(addr + 0x14, num);
        if (num > 0 && num <= 0x800000) {
            uintptr_t objPtr = 0;
            Macht::ReadSafe(addr + 0x00, objPtr);
            objPtr = DecryptObjectPtr(objPtr);
            if (LooksLikeHeapPtr(objPtr)) {
                s_layout = { 0x00, 0x10, 0x14, 0x18, 0x1C };
                LOG_INFO("ObjectArray: Layout A/C (relaxed) detected (Num=%d, Objects=0x%llX)",
                         num, (unsigned long long)objPtr);
                return true;
            }
        }
    }

    // Layout B (flat/alt): Objects@+0x10, Num@+0x04
    {
        int32_t num = 0;
        Macht::ReadSafe(addr + 0x04, num);
        if (num > 0 && num <= 0x800000) {
            uintptr_t objPtr = 0;
            Macht::ReadSafe(addr + 0x10, objPtr);
            objPtr = DecryptObjectPtr(objPtr);
            if (LooksLikeHeapPtr(objPtr)) {
                s_layout = { 0x10, 0x08, 0x04, 0x0C, -1 };
                LOG_INFO("ObjectArray: Layout B (relaxed alt) detected (Num=%d, Objects=0x%llX)",
                         num, (unsigned long long)objPtr);
                return true;
            }
        }
    }

    // Layout D (UE4 extended relaxed): Objects@+0x10, Num@+0x1C, Max@+0x18
    {
        int32_t num = 0, max = 0;
        Macht::ReadSafe(addr + 0x1C, num);
        Macht::ReadSafe(addr + 0x18, max);
        if (num > 0 && num <= max && max <= 0x800000) {
            uintptr_t objPtr = 0;
            Macht::ReadSafe(addr + 0x10, objPtr);
            objPtr = DecryptObjectPtr(objPtr);
            if (LooksLikeHeapPtr(objPtr)) {
                s_layout = { 0x10, 0x18, 0x1C, 0x20, 0x24 };
                LOG_INFO("ObjectArray: Layout D (relaxed UE4 ext) detected (Num=%d, Max=%d, Objects=0x%llX)",
                         num, max, (unsigned long long)objPtr);
                return true;
            }
        }
    }

    // Layout E (UE5 extended relaxed): Objects@+0x10, Num@+0x24, Max@+0x20
    // FUObjectArray with GC prefix + PreAllocatedObjects ptr before array fields.
    {
        int32_t num = 0, max = 0;
        Macht::ReadSafe(addr + 0x24, num);
        Macht::ReadSafe(addr + 0x20, max);
        if (num > 0 && num <= max && max <= 0x800000) {
            uintptr_t objPtr = 0;
            Macht::ReadSafe(addr + 0x10, objPtr);
            objPtr = DecryptObjectPtr(objPtr);
            if (LooksLikeHeapPtr(objPtr)) {
                s_layout = { 0x10, 0x20, 0x24, 0x28, 0x2C };
                LOG_INFO("ObjectArray: Layout E (relaxed UE5 ext) detected (Num=%d, Max=%d, Objects=0x%llX)",
                         num, max, (unsigned long long)objPtr);
                return true;
            }
        }
    }

    LOG_WARN("ObjectArray: Could not detect layout, using default");
    s_layout = s_chunkedPresets[0].layout;
    return true;
}

// Helper: check if a pointer looks like a valid UObject (has valid ClassPrivate chain)
static bool LooksLikeUObject(uintptr_t obj) {
    if (!obj || obj < 0x10000 || obj > 0x00007FFFFFFFFFFF) return false;
    uintptr_t cls = 0;
    if (!Macht::ReadSafe(obj + 0x10, cls)) return false;
    if (cls < 0x10000 || cls > 0x00007FFFFFFFFFFF) return false;
    uintptr_t clsCls = 0;
    if (!Macht::ReadSafe(cls + 0x10, clsCls)) return false;
    if (clsCls < 0x10000 || clsCls > 0x00007FFFFFFFFFFF) return false;
    return true;
}

// Test a candidate stride against a chunk, counting valid UObject items.
// Returns the number of items that resolved names (strong) and total valid items (weak).
// NOTE: No early exit — scans all maxItems for fair comparison across strides.
static void ProbeStride(uintptr_t chunkBase, int stride, int maxItems,
                        int& outGood, int& outNamed, int& outNull, int& outBad) {
    outGood = outNamed = outNull = outBad = 0;

    for (int idx = 0; idx < maxItems; ++idx) {
        int64_t byteOff = static_cast<int64_t>(idx) * stride;

        uintptr_t obj = 0;
        if (!Macht::ReadSafe(chunkBase + byteOff, obj)) {
            ++outBad;
            if (outBad > 30 && outGood == 0) break;  // Too many read failures, give up
            continue;
        }

        if (!obj) {
            ++outNull;
            continue;
        }

        if (!LooksLikeUObject(obj)) {
            ++outBad;
            if (outBad > 30 && outGood == 0) break;
            continue;
        }

        ++outGood;

        // If FNamePool is available, use strong validation
        if (Serie::IsInitialized()) {
            uint32_t nameIdx = 0;
            if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx)) {
                std::string name = Serie::GetString(nameIdx);
                if (!name.empty() && name != "None") {
                    bool validAscii = true;
                    for (char c : name) {
                        if (c < 0x20 || c > 0x7E) { validAscii = false; break; }
                    }
                    if (validAscii) ++outNamed;
                }
            }
        }
    }
}

// Compute a quality score for a stride probe result.
// Positive signal: named items (strong) or good items (weak).
// Negative signal: bad items (wrong stride produces many misaligned reads).
// The correct stride should have high named/good and very low bad.
static int ComputeStrideScore(int named, int good, int bad) {
    // If we have named items, the score is primarily based on named count,
    // heavily penalized by bad count. Wrong strides that get "lucky" hits
    // via LCM alignment will have both named AND many bad items.
    if (named > 0) {
        // Score = (named * 10) - (bad * 3)
        // This means a stride with 2 named, 0 bad (score=20) beats
        // a stride with 5 named, 29 bad (score=50-87=-37).
        return named * 10 - bad * 3;
    }
    // No named items — use good count with lesser bad penalty
    if (good > 0) {
        return good * 5 - bad * 2;
    }
    // Nothing found
    return -bad;
}

// Helper: run ProbeStride for all candidate strides on a given base address, updating best.
static void ProbeAllStrides(uintptr_t base, int maxItems, const char* phase,
                            int candidates[], int numCandidates,
                            int& bestStride, int& bestCount, int& bestNamed,
                            int& bestBad, bool& bestHasNames) {
    int bestScore = INT_MIN;

    // Store results for all candidates (for fallback logic)
    struct ProbeResult { int stride, good, named, null_, bad, score; };
    ProbeResult results[5] = {};  // max 5 candidates

    for (int i = 0; i < numCandidates && i < 5; ++i) {
        int stride = candidates[i];
        int good, named, null_, bad;
        ProbeStride(base, stride, maxItems, good, named, null_, bad);

        LOG_INFO("ObjectArray: %s stride %d: good=%d, named=%d, null=%d, bad=%d",
                 phase, stride, good, named, null_, bad);

        int score = ComputeStrideScore(named, good, bad);
        results[i] = { stride, good, named, null_, bad, score };

        if (score > bestScore) {
            bestScore = score;
            bestStride = stride;
            bestCount = good;
            bestNamed = named;
            bestBad = bad;
            bestHasNames = (named > 0);
        }
    }

    // Fallback: when best score is negative (all strides have bad > named),
    // the primary scoring may be unreliable due to LCM alignment false positives.
    // Among strides that have named > 0, prefer fewest bad — BUT only override
    // if the fallback candidate has at least as many named items as the primary winner.
    // Named count is the strongest signal (requires both valid ClassPrivate chain AND
    // FNamePool resolution), so a stride with more named items is more trustworthy
    // even if it has slightly more bad items.
    if (bestScore < 0) {
        int fallbackBad = INT_MAX;
        int fallbackStride = -1;
        int fallbackIdx = -1;
        for (int i = 0; i < numCandidates && i < 5; ++i) {
            if (results[i].named > 0 && results[i].bad < fallbackBad) {
                fallbackBad = results[i].bad;
                fallbackStride = results[i].stride;
                fallbackIdx = i;
            }
        }
        if (fallbackIdx >= 0 && fallbackStride != bestStride) {
            // Only override if fallback has equal or more named items.
            // If primary winner has more named, keep it — named count is the
            // strongest quality signal and outweighs a small bad-count difference.
            if (results[fallbackIdx].named >= bestNamed) {
                LOG_INFO("ObjectArray: %s fallback: all scores negative, selecting stride %d (fewest bad=%d, named=%d) over stride %d (bad=%d, named=%d)",
                         phase, fallbackStride, fallbackBad, results[fallbackIdx].named, bestStride, bestBad, bestNamed);
                bestStride = results[fallbackIdx].stride;
                bestCount = results[fallbackIdx].good;
                bestNamed = results[fallbackIdx].named;
                bestBad = results[fallbackIdx].bad;
                bestHasNames = (results[fallbackIdx].named > 0);
            } else {
                LOG_INFO("ObjectArray: %s fallback: stride %d has fewer bad (%d vs %d) but primary stride %d has more named (%d vs %d), keeping primary",
                         phase, fallbackStride, fallbackBad, bestBad, bestStride, bestNamed, results[fallbackIdx].named);
            }
        }
    }
}

// Auto-detect FUObjectItem size by probing consecutive items in chunks.
// UE5 (most): 16 bytes, UE4 / some UE5 with clustering: 24 bytes.
//
// Strategy: For each candidate stride, walk chunk at stride-aligned offsets
// counting valid items. Use FNamePool-based name resolution (strong) if available,
// falling back to ClassPrivate chain (weak) if not. Try all strides and pick best.
// Uses tiebreaker: when named counts are equal, prefer stride with fewer bad items.
static void DetectItemSize() {
    uintptr_t chunkTable = 0;
    if (!Macht::ReadSafe(s_arrayAddr + s_layout.objectsOffset, chunkTable) || !chunkTable) {
        LOG_WARN("ObjectArray: Cannot read chunk table for item size detection");
        return;
    }
    chunkTable = DecryptObjectPtr(chunkTable);

    // Diagnostic: dump first 64 bytes at chunkTable address
    {
        uint64_t dump[8] = {};
        Macht::ReadBytesSafe(chunkTable, dump, sizeof(dump));
        LOG_DEBUG("ObjectArray: chunkTable@0x%llX: +00:%016llX +08:%016llX +10:%016llX +18:%016llX +20:%016llX +28:%016llX +30:%016llX +38:%016llX",
                  (unsigned long long)chunkTable,
                  dump[0], dump[1], dump[2], dump[3], dump[4], dump[5], dump[6], dump[7]);
    }

    uintptr_t chunk0 = 0;
    if (!Macht::ReadSafe(chunkTable, chunk0) || !chunk0) {
        LOG_WARN("ObjectArray: Cannot read chunk[0] for item size detection");
        return;
    }

    int candidates[] = { 16, 24, 20 };
    constexpr int NUM_CANDIDATES = 3;
    int bestStride = 0;
    int bestCount = 0;
    int bestNamed = 0;
    int bestBad = INT_MAX;
    bool bestHasNames = false;

    constexpr int MAX_ITEMS_PHASE1 = 200;

    // --- Pre-check: detect flat (non-chunked) FFixedUObjectArray (UE4.11-4.20) ---
    // In a chunked array, each entry in the chunk table is an 8-byte pointer.
    // If we need 2+ chunks but chunk[1] (at chunkTable+8) is NOT a valid heap pointer,
    // then chunkTable is likely the flat item array itself (FUObjectItem*), not a
    // chunk pointer table (FUObjectItem**).
    //
    // UE4.18 (e.g. FF7R) uses FFixedUObjectArray = { FUObjectItem* Objects, int32 Max, int32 Num }
    // where Objects points directly to items. Our Layout B reads Objects at GObjects+0x10,
    // so chunkTable = Objects = flat item array. Reading *(chunkTable) gives Item[0].Object
    // which is a UObject*, not a chunk pointer. chunk[1] = *(chunkTable+8) reads Item[0].Flags
    // (e.g. 0x40000000 = EObjectFlags), which fails LooksLikeHeapPtr.
    {
        uintptr_t chunk1 = 0;
        Macht::ReadSafe(chunkTable + sizeof(uintptr_t), chunk1);
        int32_t numElements = GetCount();
        bool mightBeFlat = false;

        if (chunk0 && numElements > 0) {
            int chunksNeeded = (numElements + Grimoire::OBJECTS_PER_CHUNK - 1) / Grimoire::OBJECTS_PER_CHUNK;
            if (chunksNeeded >= 2) {
                // Validate chunk[1]: in a real chunk table, chunk[1] must be a valid heap pointer.
                // LooksLikeHeapPtr alone is insufficient — 32-bit values like EObjectFlags
                // (e.g. 0x40000000) pass its checks. Add two extra validations:
                //   1. Magnitude: real heap pointers on x64 with ASLR are > 4GB
                //   2. Dereference: real chunk pointers are readable memory
                bool chunk1Valid = LooksLikeHeapPtr(chunk1);
                if (chunk1Valid && chunk1 < 0x100000000ULL) {
                    // Value fits in 32 bits — suspicious. Verify by dereference.
                    uintptr_t testDeref = 0;
                    if (!Macht::ReadSafe(chunk1, testDeref)) {
                        chunk1Valid = false;
                        LOG_DEBUG("ObjectArray: chunk[1]=0x%llX fits in 32 bits and is unreadable — not a chunk pointer",
                                  (unsigned long long)chunk1);
                    }
                }

                if (!chunk1Valid) {
                    mightBeFlat = true;
                    LOG_INFO("ObjectArray: chunk[1]=0x%llX is not a valid chunk pointer (need %d chunks for %d objects) — testing flat layout first",
                             (unsigned long long)chunk1, chunksNeeded, numElements);
                }
            }
        }

        if (mightBeFlat) {
            // Try flat layout first: probe chunkTable itself as item base (no deref)
            s_isFlat = true;
            ProbeAllStrides(chunkTable, MAX_ITEMS_PHASE1, "P0-flat",
                            candidates, NUM_CANDIDATES,
                            bestStride, bestCount, bestNamed, bestBad, bestHasNames);

            if (bestHasNames && bestNamed >= 2) {
                LOG_INFO("ObjectArray: Flat (non-chunked) array confirmed (P0-flat: %d named, %d bad)",
                         bestNamed, bestBad);
                goto accept_size;
            }
            // Flat didn't work convincingly — reset and try chunked
            LOG_INFO("ObjectArray: Flat probe inconclusive (named=%d), falling back to chunked detection",
                     bestNamed);
            s_isFlat = false;
            bestStride = 0; bestCount = 0; bestNamed = 0; bestBad = INT_MAX; bestHasNames = false;
        }
    }

    // Phase 1: scan first 200 items of chunk[0] (standard chunked layout)
    // Use 200 items (not 100) to give sparse UE4 arrays enough items for correct stride detection.
    ProbeAllStrides(chunk0, MAX_ITEMS_PHASE1, "P1",
                    candidates, NUM_CANDIDATES,
                    bestStride, bestCount, bestNamed, bestBad, bestHasNames);

    // Phase 2: if Phase 1 yielded nothing, try deeper in chunk (items 1000+).
    // Some UE4 games have thousands of null slots at the start.
    if (bestCount == 0) {
        LOG_INFO("ObjectArray: Phase 1 found no items, trying deep scan from item 1000...");
        ProbeAllStrides(chunk0 + static_cast<int64_t>(1000) * 24, 100, "P2-deep",
                        candidates, NUM_CANDIDATES,
                        bestStride, bestCount, bestNamed, bestBad, bestHasNames);
    }

    // Phase 3: if still nothing, maybe the array is NOT chunked (some UE4 builds).
    // In non-chunked layout, chunkTable IS the item array directly (no extra deref).
    // Try probing chunkTable itself as the item base.
    if (bestCount == 0) {
        LOG_INFO("ObjectArray: Phase 2 found nothing. Trying flat (non-chunked) array at chunkTable=0x%llX...",
                 (unsigned long long)chunkTable);

        s_isFlat = true;  // Temporarily set for probing

        ProbeAllStrides(chunkTable, MAX_ITEMS_PHASE1, "P3-flat",
                        candidates, NUM_CANDIDATES,
                        bestStride, bestCount, bestNamed, bestBad, bestHasNames);

        if (bestCount == 0) {
            // Try deep scan on flat array too
            ProbeAllStrides(chunkTable + static_cast<int64_t>(1000) * 24, 100, "P3-flat-deep",
                            candidates, NUM_CANDIDATES,
                            bestStride, bestCount, bestNamed, bestBad, bestHasNames);
        }

        if (bestCount == 0) {
            s_isFlat = false;  // Revert — flat didn't work either
        } else {
            LOG_INFO("ObjectArray: Flat (non-chunked) array layout detected");
        }
    }

accept_size:
    // Determine minimum threshold for acceptance
    int threshold = bestHasNames ? 2 : 3;
    int bestTotal = bestHasNames ? bestNamed : bestCount;

    if (bestTotal >= threshold) {
        s_itemSize = bestStride;
        if (bestHasNames) {
            LOG_INFO("ObjectArray: FUObjectItem size detected as %d bytes (%d items with valid names, %d total valid, %d bad)",
                     bestStride, bestNamed, bestCount, bestBad);
        } else {
            LOG_INFO("ObjectArray: FUObjectItem size detected as %d bytes (%d items validated, no FName check)",
                     bestStride, bestCount);
        }
    } else if (bestStride > 0 && bestTotal > 0) {
        s_itemSize = bestStride;
        LOG_WARN("ObjectArray: FUObjectItem size tentatively set to %d bytes (only %d items validated)",
                 bestStride, bestTotal);
    } else {
        LOG_WARN("ObjectArray: Could not auto-detect item size, keeping default %d", s_itemSize);
    }
}

void Init(uintptr_t gobjectsAddr) {
    s_arrayAddr = gobjectsAddr;
    DetectLayout(gobjectsAddr);
    DetectItemSize();
    LOG_INFO("ObjectArray: Initialized at 0x%llX, Count=%d, ItemSize=%d",
             static_cast<unsigned long long>(gobjectsAddr), GetCount(), s_itemSize);
}

int32_t GetCount() {
    if (!s_arrayAddr) return 0;
    int32_t count = 0;
    Macht::ReadSafe(s_arrayAddr + s_layout.numElementsOffset, count);
    return count;
}

int32_t GetMax() {
    if (!s_arrayAddr) return 0;
    int32_t max = 0;
    Macht::ReadSafe(s_arrayAddr + s_layout.maxElementsOffset, max);
    return max;
}

int GetItemSize() {
    return s_itemSize;
}

bool IsFlat() {
    return s_isFlat;
}

uintptr_t GetByIndex(int32_t index) {
    if (!s_arrayAddr || index < 0 || index >= GetCount()) return 0;

    // Read array base pointer
    uintptr_t arrayBase = 0;
    if (!Macht::ReadSafe(s_arrayAddr + s_layout.objectsOffset, arrayBase) || !arrayBase) return 0;
    arrayBase = DecryptObjectPtr(arrayBase);

    uintptr_t itemAddr = 0;

    if (s_isFlat) {
        // Flat (non-chunked): items are at arrayBase + index * itemSize
        itemAddr = arrayBase + static_cast<uintptr_t>(index) * s_itemSize;
    } else {
        // Chunked: arrayBase is a chunk table, each chunk holds OBJECTS_PER_CHUNK items
        int32_t chunkIndex = index / Grimoire::OBJECTS_PER_CHUNK;
        int32_t withinChunk = index % Grimoire::OBJECTS_PER_CHUNK;

        uintptr_t chunk = 0;
        if (!Macht::ReadSafe(arrayBase + chunkIndex * sizeof(uintptr_t), chunk) || !chunk) return 0;

        itemAddr = chunk + static_cast<uintptr_t>(withinChunk) * s_itemSize;
    }

    uintptr_t object = 0;
    Macht::ReadSafe(itemAddr, object);
    return object;
}

FUObjectItem* GetItem(int32_t index) {
    if (!s_arrayAddr || index < 0 || index >= GetCount()) return nullptr;

    uintptr_t arrayBase = 0;
    if (!Macht::ReadSafe(s_arrayAddr + s_layout.objectsOffset, arrayBase) || !arrayBase) return nullptr;
    arrayBase = DecryptObjectPtr(arrayBase);

    uintptr_t itemAddr = 0;

    if (s_isFlat) {
        itemAddr = arrayBase + static_cast<uintptr_t>(index) * s_itemSize;
    } else {
        int32_t chunkIndex = index / Grimoire::OBJECTS_PER_CHUNK;
        int32_t withinChunk = index % Grimoire::OBJECTS_PER_CHUNK;

        uintptr_t chunk = 0;
        if (!Macht::ReadSafe(arrayBase + chunkIndex * sizeof(uintptr_t), chunk) || !chunk) return nullptr;

        itemAddr = chunk + static_cast<uintptr_t>(withinChunk) * s_itemSize;
    }

    return Macht::Ptr<FUObjectItem>(itemAddr);
}

int32_t GetSerialNumber(int32_t index) {
    if (!s_arrayAddr || index < 0 || index >= GetCount()) return 0;

    uintptr_t arrayBase = 0;
    if (!Macht::ReadSafe(s_arrayAddr + s_layout.objectsOffset, arrayBase) || !arrayBase)
        return 0;
    arrayBase = DecryptObjectPtr(arrayBase);

    uintptr_t itemAddr = 0;
    if (s_isFlat) {
        itemAddr = arrayBase + static_cast<uintptr_t>(index) * s_itemSize;
    } else {
        int32_t chunkIndex  = index / Grimoire::OBJECTS_PER_CHUNK;
        int32_t withinChunk = index % Grimoire::OBJECTS_PER_CHUNK;
        uintptr_t chunk = 0;
        if (!Macht::ReadSafe(arrayBase + chunkIndex * sizeof(uintptr_t), chunk) || !chunk)
            return 0;
        itemAddr = chunk + static_cast<uintptr_t>(withinChunk) * s_itemSize;
    }

    // SerialNumber offset depends on item stride:
    //   16B: Object(8) + Flags(4) + Serial(4)                        → +0x0C
    //   24B: Object(8) + Flags(4) + ClusterRootIndex(4) + Serial(4)  → +0x10
    int serialOff = (s_itemSize >= 24) ? 0x10 : 0x0C;
    int32_t serial = 0;
    Macht::ReadSafe(itemAddr + serialOff, serial);
    return serial;
}

void ForEach(std::function<bool(int32_t idx, uintptr_t obj)> cb) {
    int32_t count = GetCount();
    for (int32_t i = 0; i < count; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (obj != 0) {
            if (!cb(i, obj)) break;
        }
    }
}

uintptr_t FindByName(const std::string& name) {
    uintptr_t result = 0;
    ForEach([&](int32_t /*idx*/, uintptr_t obj) -> bool {
        // Read FName from UObject
        uint32_t nameIndex = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIndex)) return true;

        std::string objName = Serie::GetString(nameIndex);
        if (objName == name) {
            result = obj;
            return false; // Stop iteration
        }
        return true;
    });
    return result;
}

uintptr_t FindByFullName(const std::string& fullName) {
    // Forward declared — uses Ubel::GetFullName
    // This is implemented after UStructWalker is available
    (void)fullName;
    return 0;
}

SearchResultSet SearchByName(const std::string& query, int maxResults) {
    SearchResultSet rset;

    // Convert query to lowercase for case-insensitive comparison
    std::string lowerQuery = query;
    for (auto& c : lowerQuery) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

    int32_t count = GetCount();
    rset.scanned = count;
    for (int32_t i = 0; i < count && static_cast<int>(rset.results.size()) < maxResults; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;
        rset.nonNull++;

        // Read FName from UObject
        uint32_t nameIndex = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIndex)) continue;

        std::string objName = Serie::GetString(nameIndex);
        if (objName.empty()) continue;
        rset.named++;

        // Case-insensitive partial match
        std::string lowerName = objName;
        for (auto& c : lowerName) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

        if (lowerName.find(lowerQuery) == std::string::npos) continue;

        SearchResult sr;
        sr.addr = obj;
        sr.name = objName;

        // Get class name
        uintptr_t cls = 0;
        if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) && cls) {
            uint32_t clsNameIdx = 0;
            if (Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) {
                sr.className = Serie::GetString(clsNameIdx);
            }
        }

        // Get outer
        Macht::ReadSafe(obj + DynOff::UOBJECT_OUTER, sr.outer);

        rset.results.push_back(std::move(sr));
    }

    return rset;
}

SearchResultSet FindInstancesByClass(const std::string& className, bool exactMatch, int maxResults) {
    SearchResultSet rset;

    // Convert query to lowercase for case-insensitive comparison
    std::string lowerQuery = className;
    for (auto& c : lowerQuery) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

    int32_t count = GetCount();
    rset.scanned = count;
    for (int32_t i = 0; i < count && static_cast<int>(rset.results.size()) < maxResults; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;
        rset.nonNull++;

        // Read ClassPrivate
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        // Read class FName
        uint32_t clsNameIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;

        std::string clsName = Serie::GetString(clsNameIdx);
        if (clsName.empty()) continue;
        rset.named++;

        // Case-insensitive match: exact (equality) or partial (substring)
        std::string lowerClsName = clsName;
        for (auto& c : lowerClsName) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

        if (exactMatch) {
            if (lowerClsName != lowerQuery) continue;
        } else {
            if (lowerClsName.find(lowerQuery) == std::string::npos) continue;
        }

        SearchResult sr;
        sr.addr = obj;
        sr.index = i;

        // Read object name
        uint32_t nameIdx = 0;
        if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx)) {
            sr.name = Serie::GetString(nameIdx);
        }
        sr.className = clsName;

        // Read outer
        Macht::ReadSafe(obj + DynOff::UOBJECT_OUTER, sr.outer);

        rset.results.push_back(std::move(sr));
    }

    Sein::Info("PIPE:find", "FindInstancesByClass '%s': %d found, scanned=%d, nonNull=%d, named=%d",
                 className.c_str(), (int)rset.results.size(), rset.scanned, rset.nonNull, rset.named);
    return rset;
}

// Helper: populate an AddressLookupResult from a UObject pointer.
// `kind` distinguishes confidence levels — see AddressLookupResult comment.
static void FillLookupResult(AddressLookupResult& out, uintptr_t obj, int32_t index,
                             int32_t offsetFromBase, bool exact,
                             const char* kind = nullptr) {
    out.found = true;
    out.exactMatch = exact;
    out.matchKind = kind ? kind : (exact ? "exact" : "contains");
    out.objectAddr = obj;
    out.index = index;
    out.offsetFromBase = offsetFromBase;

    uint32_t nameIdx = 0;
    if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx)) {
        out.name = Serie::GetString(nameIdx);
    }
    uintptr_t cls = 0;
    if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) && cls) {
        uint32_t clsNameIdx = 0;
        if (Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) {
            out.className = Serie::GetString(clsNameIdx);
        }
    }
    Macht::ReadSafe(obj + DynOff::UOBJECT_OUTER, out.outer);
}

AddressLookupResult FindByAddress(uintptr_t addr) {
    AddressLookupResult result;
    if (!addr || !s_arrayAddr) return result;

    int32_t count = GetCount();
    if (count <= 0) return result;

    LOG_INFO("FindByAddress: Looking up 0x%llX in %d objects",
             static_cast<unsigned long long>(addr), count);

    // --- Single pass: Exact match + track top-N closest objects below addr ---
    // Tracking multiple candidates allows better containment matching
    // even when small UObjects are packed near the query address.
    struct Candidate {
        uintptr_t obj;
        int32_t   idx;
        uintptr_t dist;
    };
    constexpr int MAX_CANDIDATES = 16;
    constexpr uintptr_t MAX_CONTAINMENT_RANGE = 0x40000;  // 256KB

    Candidate candidates[MAX_CANDIDATES] = {};
    int numCandidates = 0;

    for (int32_t i = 0; i < count; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        // Exact match check
        if (obj == addr) {
            FillLookupResult(result, obj, i, 0, true);
            LOG_INFO("FindByAddress: Exact match at index %d (%s : %s)",
                     i, result.name.c_str(), result.className.c_str());
            return result;
        }

        // Track candidates below addr within range
        if (obj < addr) {
            uintptr_t dist = addr - obj;
            if (dist >= MAX_CONTAINMENT_RANGE) continue;

            // Insert into sorted candidates (smallest distance first)
            if (numCandidates < MAX_CANDIDATES) {
                candidates[numCandidates++] = { obj, i, dist };
                // Bubble up
                for (int j = numCandidates - 1; j > 0 && candidates[j].dist < candidates[j-1].dist; --j) {
                    auto tmp = candidates[j];
                    candidates[j] = candidates[j-1];
                    candidates[j-1] = tmp;
                }
            } else if (dist < candidates[MAX_CANDIDATES - 1].dist) {
                candidates[MAX_CANDIDATES - 1] = { obj, i, dist };
                // Bubble up
                for (int j = MAX_CANDIDATES - 1; j > 0 && candidates[j].dist < candidates[j-1].dist; --j) {
                    auto tmp = candidates[j];
                    candidates[j] = candidates[j-1];
                    candidates[j-1] = tmp;
                }
            }
        }
    }

    if (numCandidates == 0) {
        LOG_INFO("FindByAddress: No objects within 256KB below 0x%llX — will try backward scan",
                 static_cast<unsigned long long>(addr));
    } else {
        LOG_INFO("FindByAddress: No exact match. %d candidates within range. Closest at dist=0x%llX",
                 numCandidates, static_cast<unsigned long long>(candidates[0].dist));
    }

    // --- Containment check on candidates ---
    // Try each candidate (closest first), check if addr is within its PropertiesSize.
    // Pick the smallest PropertiesSize that still contains addr (most specific match).
    AddressLookupResult bestMatch;
    int32_t smallestSize = INT32_MAX;

    for (int c = 0; c < numCandidates; ++c) {
        uintptr_t obj = candidates[c].obj;
        uintptr_t dist = candidates[c].dist;

        // Read ClassPrivate to get PropertiesSize
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        int32_t propsSize = 0;
        if (!Macht::ReadSafe(cls + DynOff::USTRUCT_PROPSSIZE, propsSize)) continue;
        if (propsSize <= 0 || propsSize > 0x100000) continue;

        // Log top candidates for diagnosis
        if (c < 5) {
            uint32_t nameIdx = 0;
            std::string name = "(read fail)";
            if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx))
                name = Serie::GetString(nameIdx);
            LOG_INFO("FindByAddress: Candidate #%d: 0x%llX (%s), dist=0x%llX, propsSize=%d, %s",
                     c, static_cast<unsigned long long>(obj), name.c_str(),
                     static_cast<unsigned long long>(dist), propsSize,
                     (dist < static_cast<uintptr_t>(propsSize)) ? "CONTAINS" : "no");
        }

        // Check containment: addr >= obj && addr < obj + propsSize
        if (dist < static_cast<uintptr_t>(propsSize)) {
            if (propsSize < smallestSize) {
                smallestSize = propsSize;
                FillLookupResult(bestMatch, obj, candidates[c].idx,
                                 static_cast<int32_t>(dist), false);
            }
        }
    }

    if (bestMatch.found) {
        LOG_INFO("FindByAddress: Containment match: %s at 0x%llX, offset +0x%X",
                 bestMatch.name.c_str(),
                 static_cast<unsigned long long>(bestMatch.objectAddr),
                 bestMatch.offsetFromBase);
        return bestMatch;
    }

    // --- Backward memory scan: find UObject header before query address ---
    // When the address is inside a subobject that's NOT in GObjects (e.g.,
    // GrimAttributeSetHealth created by NewObject<>), scan backward from the
    // query address looking for a valid UObject header pattern.
    //
    // UObject header layout:
    //   +0x00: VTable* (pointer to module code/data range)
    //   +0x08: ObjectFlags (EObjectFlags, typically small value)
    //   +0x0C: InternalIndex (int32, 0..maxObjects)
    //   +0x10: ClassPrivate* (UClass*, must be non-null and point to valid memory)
    //   +0x18: NamePrivate (FName ComparisonIndex, must resolve in FNamePool)
    //   +0x20/0x28: OuterPrivate* (UObject*, nullable)
    //
    // We scan backward in 8-byte steps (UObjects are at least 8-byte aligned),
    // up to a reasonable range (64KB), checking each candidate address.

    constexpr uintptr_t MAX_BACKWARD_SCAN = 0x10000;  // 64KB backward scan

    uintptr_t moduleBase = Macht::GetModuleBase(nullptr);
    uintptr_t moduleEnd = moduleBase + Macht::GetModuleSize(nullptr);

    uintptr_t scanStart = (addr > MAX_BACKWARD_SCAN) ? (addr - MAX_BACKWARD_SCAN) : 0;
    // Align to 8 bytes
    scanStart = (scanStart + 7) & ~7ULL;

    LOG_INFO("FindByAddress: Backward scan from 0x%llX to 0x%llX (module 0x%llX-0x%llX)...",
             static_cast<unsigned long long>(addr),
             static_cast<unsigned long long>(scanStart),
             static_cast<unsigned long long>(moduleBase),
             static_cast<unsigned long long>(moduleEnd));

    uintptr_t bestScanObj = 0;
    uintptr_t bestScanDist = UINTPTR_MAX;

    // Scan from just below addr backward, in 8-byte steps
    for (uintptr_t probe = (addr & ~7ULL); probe >= scanStart && probe <= addr; probe -= 8) {
        // Quick reject: read VTable pointer
        uintptr_t vtable = 0;
        if (!Macht::ReadSafe(probe + Grimoire::OFF_UOBJECT_VTABLE, vtable) || !vtable) continue;

        // VTable should point into the module's address range
        if (vtable < moduleBase || vtable >= moduleEnd) continue;

        // Read ClassPrivate — must be non-null
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(probe + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        // ClassPrivate's VTable should also be in module range (it's a UClass)
        uintptr_t clsVtable = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_VTABLE, clsVtable)) continue;
        if (clsVtable < moduleBase || clsVtable >= moduleEnd) continue;

        // Read InternalIndex — should be reasonable
        int32_t idx = 0;
        if (!Macht::ReadSafe(probe + Grimoire::OFF_UOBJECT_INDEX, idx)) continue;
        if (idx < 0 || idx > 0x800000) continue;

        // Read FName ComparisonIndex — must resolve to a non-empty string
        uint32_t nameIdx = 0;
        if (!Macht::ReadSafe(probe + Grimoire::OFF_UOBJECT_NAME, nameIdx)) continue;
        if (nameIdx == 0) continue;  // Index 0 = "None", skip
        std::string name = Serie::GetString(nameIdx);
        if (name.empty() || name == "None") continue;

        // Additional validation: name should contain only printable ASCII
        bool validName = true;
        for (char c : name) {
            if (c < 0x20 || c > 0x7E) { validName = false; break; }
        }
        if (!validName) continue;

        // This looks like a valid UObject!
        uintptr_t dist = addr - probe;

        LOG_INFO("FindByAddress: Backward scan hit at 0x%llX (%s), dist=0x%llX, idx=%d",
                 static_cast<unsigned long long>(probe), name.c_str(),
                 static_cast<unsigned long long>(dist), idx);

        if (dist < bestScanDist) {
            bestScanDist = dist;
            bestScanObj = probe;
        }
        // Found the closest valid UObject — stop scanning
        // (scanning downward, first hit from addr is closest)
        break;
    }

    if (bestScanObj) {
        // Backward scan found a UObject — use it if no GObjects candidates,
        // or if it's closer than the best GObjects candidate.
        // Match kind = "backward" (medium confidence — the UObject was found
        // by memory pattern, not by GObjects, so addr is past its bounds).
        bool useBackward = (numCandidates == 0) ||
                           (bestScanDist < candidates[0].dist);
        if (useBackward) {
            FillLookupResult(result, bestScanObj, -1,
                             static_cast<int32_t>(bestScanDist), false, "backward");
            LOG_INFO("FindByAddress: Backward scan match: %s at 0x%llX, offset +0x%X",
                     result.name.c_str(),
                     static_cast<unsigned long long>(bestScanObj),
                     result.offsetFromBase);
            return result;
        }
    }

    if (numCandidates > 0) {
        // --- Fallback: Return closest GObjects object as "nearest" ---
        // Low confidence: addr is past this UObject's PropertiesSize, so we
        // are NOT actually inside it. Surfaced as a hint only — frequently
        // misleading when the address is heap-allocated container data.
        FillLookupResult(result, candidates[0].obj, candidates[0].idx,
                         static_cast<int32_t>(candidates[0].dist), false, "nearest");
        result.exactMatch = false;
        LOG_INFO("FindByAddress: Nearest GObjects fallback: %s at 0x%llX, offset +0x%X (likely outside bounds)",
                 result.name.c_str(),
                 static_cast<unsigned long long>(candidates[0].obj),
                 result.offsetFromBase);
        return result;
    }

    // Nothing found at all
    LOG_INFO("FindByAddress: No match found for 0x%llX (no candidates, no backward scan hit)",
             static_cast<unsigned long long>(addr));
    return result;
}

// === Container-Aware Address Lookup ===
//
// Persistent per-class cache of container fields (ArrayProperty / MapProperty
// / SetProperty) and their resolved per-element strides. Built lazily on
// first encounter via WalkClassEx. Empty entries (classes with no usable
// container fields) are stored so we don't re-walk them on subsequent queries.
//
// Nested struct support: many UE games store gameplay arrays inside a
// USTRUCT() rather than as direct UPROPERTY() arrays of the UObject —
// e.g. UPlayerInfo { FCharacterStats Stats; } where Stats has a TArray<int>
// Levels member. The cache builder recurses into StructProperty fields
// (depth-capped) and registers nested arrays/maps/sets with their absolute
// offset (parent struct offset + child field offset) and a dotted name
// like "Stats.Levels".

enum class ContainerKind {
    Array,   // TArray.Data buffer, stride = inner element size
    Set,     // TSparseArray.Data buffer, stride = ComputeSetElementStride
    Map,     // TSparseArray.Data buffer, stride = ComputeSetElementStride(pair)
};

struct ContainerCacheEntry {
    int32_t       offset;       // Absolute byte offset within owner UObject
    std::string   name;         // Dotted name (e.g. "Stats.Levels")
    std::string   innerType;    // ArrayProperty: inner; Set: elem; Map: "K → V"
    int32_t       stride;       // Bytes per element/pair within Data buffer
    ContainerKind kind;
};

static std::unordered_map<uintptr_t, std::vector<ContainerCacheEntry>> s_classContainerCache;
static std::mutex s_classContainerMutex;

// Recursive collector — walks `structAddr` (a UClass* or UScriptStruct*)
// and emits one ContainerCacheEntry for each ArrayProperty/MapProperty/
// SetProperty found, INCLUDING those nested inside StructProperty fields.
// Depth-capped to avoid pathological cyclic struct definitions.
static void CollectContainersRecursive(
    uintptr_t structAddr,
    int32_t baseOffset,
    const std::string& namePrefix,
    std::vector<ContainerCacheEntry>& out,
    int depth)
{
    // Reasonable cap: most UE games nest at most 1–2 levels (UObject →
    // FStruct → TArray). Depth 3 covers struct-of-struct-of-struct.
    constexpr int kMaxDepth = 3;
    if (depth > kMaxDepth) return;

    auto ci = Ubel::WalkClassEx(structAddr);
    for (const auto& f : ci.Fields) {
        if (!f.Address) continue;

        std::string fullName = namePrefix.empty()
            ? f.Name
            : (namePrefix + "." + f.Name);
        int32_t absOffset = baseOffset + f.Offset;

        if (f.TypeName == "ArrayProperty") {
            int32_t es = Ubel::GetArrayInnerElemSize(f.Address);
            if (es <= 0) continue;
            out.push_back({ absOffset, fullName, f.innerType, es, ContainerKind::Array });
        }
        else if (f.TypeName == "SetProperty") {
            int32_t st = Ubel::GetSetElementStride(f.Address);
            if (st <= 0) continue;
            out.push_back({ absOffset, fullName, f.elemType, st, ContainerKind::Set });
        }
        else if (f.TypeName == "MapProperty") {
            int32_t st = Ubel::GetMapPairStride(f.Address);
            if (st <= 0) continue;
            std::string innerLabel = f.keyType + " → " + f.valueType;
            out.push_back({ absOffset, fullName, innerLabel, st, ContainerKind::Map });
        }
        else if (f.TypeName == "StructProperty") {
            // Descend into the nested UScriptStruct, accumulating offset
            // and dotted name. Inner UScriptStruct address lives at the
            // FProperty's subclass-extension offset.
            uintptr_t innerStruct = 0;
            if (Macht::ReadSafe(f.Address + DynOff::FSTRUCTPROP_STRUCT, innerStruct)
                && innerStruct) {
                CollectContainersRecursive(innerStruct, absOffset, fullName,
                                           out, depth + 1);
            }
        }
        else if (f.TypeName == "OptionalProperty"
                 && f.innerType == "StructProperty") {
            // TOptional<FStruct> non-intrusive layout: { T value; uint8 bIsSet; }.
            // Value lives at field+0, so offset accumulation is identical to
            // a bare StructProperty. We can't tell at cache-build time which
            // instances are set vs unset, but a container scan that hits an
            // unset slot just sees zeros and naturally fails its address
            // comparison.
            uintptr_t innerProp = 0;
            uintptr_t innerStruct = 0;
            // Probe inner FProperty* (same offset as ArrayProperty::Inner).
            if (Macht::ReadSafe(f.Address + DynOff::FARRAYPROP_INNER, innerProp)
                && innerProp
                && Macht::ReadSafe(innerProp + DynOff::FSTRUCTPROP_STRUCT, innerStruct)
                && innerStruct) {
                CollectContainersRecursive(innerStruct, absOffset, fullName,
                                           out, depth + 1);
            }
        }
    }
}

static const std::vector<ContainerCacheEntry>& GetClassContainers(uintptr_t cls) {
    {
        std::lock_guard<std::mutex> lk(s_classContainerMutex);
        auto it = s_classContainerCache.find(cls);
        if (it != s_classContainerCache.end()) return it->second;
    }

    // Build outside the lock — WalkClassEx is non-trivial and may itself
    // touch caches. Insert under lock at the end.
    std::vector<ContainerCacheEntry> entries;
    CollectContainersRecursive(cls, /*baseOffset*/ 0, /*namePrefix*/ "",
                               entries, /*depth*/ 0);

    std::lock_guard<std::mutex> lk(s_classContainerMutex);
    auto [ins, _] = s_classContainerCache.emplace(cls, std::move(entries));
    return ins->second;
}

// Helper: emit one ContainerMatch given a resolved hit. Reads owner name +
// class name lazily so we only pay that cost when a match is actually found.
static ContainerMatch BuildMatch(uintptr_t obj, int32_t ownerIndex, uintptr_t cls,
                                  const ContainerCacheEntry& cfe,
                                  uintptr_t dataAddr, int32_t count,
                                  int32_t elementIndex, int32_t intraOffset,
                                  const char* note = "") {
    ContainerMatch m;
    m.ownerObj     = obj;
    m.ownerIndex   = ownerIndex;

    uint32_t nameIdx = 0;
    if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx))
        m.ownerName = Serie::GetString(nameIdx);

    uint32_t clsNameIdx = 0;
    if (Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx))
        m.ownerClassName = Serie::GetString(clsNameIdx);

    m.fieldOffset  = cfe.offset;
    m.fieldName    = cfe.name;
    m.fieldType    = (cfe.kind == ContainerKind::Array) ? "ArrayProperty"
                   : (cfe.kind == ContainerKind::Set)   ? "SetProperty"
                                                        : "MapProperty";
    m.innerType    = cfe.innerType;
    m.elementSize  = cfe.stride;
    m.elementIndex = elementIndex;
    m.intraOffset  = intraOffset;
    m.dataAddr     = dataAddr;
    m.count        = count;
    m.note         = note ? note : "";
    return m;
}

std::vector<ContainerMatch> FindInContainers(uintptr_t addr, int32_t maxResults,
                                              ContainerScanStats* stats) {
    std::vector<ContainerMatch> matches;
    if (stats) *stats = {};
    if (!addr || !s_arrayAddr) return matches;
    if (maxResults <= 0) maxResults = 16;

    int32_t count = GetCount();
    if (count <= 0) return matches;
    if (stats) stats->objectsTotal = count;

    LOG_INFO("FindInContainers: scanning %d objects for addr 0x%llX",
             count, static_cast<unsigned long long>(addr));

    // Per-call deadline so a slow / huge-class game doesn't hang the UI.
    // 15s is comfortable on first scan even for 400K-object games (FF7
    // Rebirth) — first scan primes the per-class cache, subsequent scans
    // finish in ~1s. 5s was too tight for first-scan on big games.
    constexpr int kDeadlineMs = 15000;
    auto t0 = std::chrono::steady_clock::now();

    // Parallel GObjects walk. GetClassContainers + Ubel caches are mutex-guarded,
    // so per-thread state is just the match buffer + diagnostic counters; the
    // ascending-tid merge below reproduces the serial ascending-index ordering.
    struct ThreadResult {
        std::vector<ContainerMatch> matches;
        int32_t                     scanned       = 0;
        int32_t                     classesWalked = 0;
    };

    auto scan = ParallelGObjectsScan<ThreadResult>(count,
        [&](ThreadResult& tr, int32_t beginIdx, int32_t endIdx,
            std::atomic<bool>& deadlineHit) {

        // maxResults is a per-thread local cap; the ascending-tid merge truncates
        // to maxResults, reproducing the serial "first N in ascending index order".
        for (int32_t i = beginIdx; i < endIdx && static_cast<int>(tr.matches.size()) < maxResults; ++i) {
        // Chunk-relative stride (see ScanForValue) so the deadline + sibling
        // deadlineHit check fires from this chunk's first iteration.
        if (((i - beginIdx) & 0x3FF) == 0) {
            if (deadlineHit.load(std::memory_order_relaxed)) return;
            auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::steady_clock::now() - t0).count();
            if (dt > kDeadlineMs) {
                deadlineHit.store(true, std::memory_order_relaxed);
                return;
            }
        }
        tr.scanned++;

        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        const auto& containers = GetClassContainers(cls);
        if (containers.empty()) continue;
        ++tr.classesWalked;

        for (const auto& cfe : containers) {
            uintptr_t fieldAddr = obj + cfe.offset;

            if (cfe.kind == ContainerKind::Array) {
                Macht::TArrayView arr;
                if (!Macht::ReadTArray(fieldAddr, arr)) continue;
                if (arr.Max <= 0 || !arr.Data) continue;
                // ReadTArray sanity-caps Count at 1M but not Max. A corrupted
                // Max would project a huge buffer span and dilute results.
                // Apply the same cap defensively.
                if (arr.Max > 0x100000) continue;

                // Use Max (allocated capacity) rather than Count so we also
                // catch addresses landing in the array's slack region — when
                // a value comes from a previously-shrunk array element the
                // memory often still holds the last-written game value.
                uintptr_t bufEnd = arr.Data + static_cast<int64_t>(arr.Max) * cfe.stride;
                if (addr < arr.Data || addr >= bufEnd) continue;

                int32_t intraTotal = static_cast<int32_t>(addr - arr.Data);
                int32_t elemIdx    = intraTotal / cfe.stride;
                const char* note   = (elemIdx >= arr.Count) ? "slack" : "";

                auto m = BuildMatch(obj, i, cls, cfe, arr.Data, arr.Count,
                                    elemIdx, intraTotal % cfe.stride, note);
                LOG_INFO("FindInContainers: hit %s.%s[%d]+0x%X (Array%s, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(),
                         m.elementIndex, m.intraOffset,
                         note[0] ? "/slack" : "",
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                tr.matches.push_back(std::move(m));
            }
            else { // Set or Map — both use TSparseArray
                Macht::TSparseArrayView sa;
                if (!Macht::ReadTSparseArray(fieldAddr, sa)) continue;
                if (sa.MaxCapacity <= 0 || !sa.Data) continue;
                // Defensive cap — same rationale as Array.Max above.
                if (sa.MaxCapacity > 0x100000) continue;

                // TSparseArray frees slots without overwriting them, so an
                // address landing on a free-list slot may still hold the
                // last-written value. Don't filter — surface them with a
                // "freed" note so the user can judge.
                uintptr_t bufEnd = sa.Data + static_cast<int64_t>(sa.MaxCapacity) * cfe.stride;
                if (addr < sa.Data || addr >= bufEnd) continue;

                int32_t intraTotal = static_cast<int32_t>(addr - sa.Data);
                int32_t sparseIdx  = intraTotal / cfe.stride;
                bool allocated = Macht::IsSparseIndexAllocated(sa, sparseIdx);
                const char* note = allocated ? "" : "freed";

                int32_t logicalCount = sa.MaxIndex - sa.NumFreeIndices;
                auto m = BuildMatch(obj, i, cls, cfe, sa.Data, logicalCount,
                                    sparseIdx, intraTotal % cfe.stride, note);
                LOG_INFO("FindInContainers: hit %s.%s[%d]+0x%X (%s%s, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(),
                         m.elementIndex, m.intraOffset,
                         m.fieldType.c_str(),
                         note[0] ? "/freed" : "",
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                tr.matches.push_back(std::move(m));
            }

            if (static_cast<int>(tr.matches.size()) >= maxResults) break;
        }
    }
    });  // ParallelGObjectsScan

    // Fold per-thread stats; match vectors concat in ascending tid order.
    int32_t scanned = 0, classesWalked = 0;
    for (auto& tr : scan.perThread) {
        scanned       += tr.scanned;
        classesWalked += tr.classesWalked;
    }
    matches = ConcatTruncate(scan.perThread, &ThreadResult::matches, maxResults);

    auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                  std::chrono::steady_clock::now() - t0).count();
    if (stats) {
        stats->objectsScanned = scanned;
        stats->classesPrimed  = classesWalked;
        stats->durationMs     = static_cast<int64_t>(dt);
        stats->deadlineHit    = scan.deadlineHit;
    }
    LOG_INFO("FindInContainers: found %d matches in %lld ms (scanned %d/%d, %d non-empty classes, %d thread(s)%s)",
             static_cast<int>(matches.size()), static_cast<long long>(dt),
             scanned, count, classesWalked, scan.nthreads, scan.deadlineHit ? ", DEADLINE HIT" : "");
    return matches;
}

// === Reverse Reference Search ===
//
// Per-class cache of pointer-shaped fields and Object array fields.
// Built lazily, mirrors the container cache pattern.
//
// Coverage (v2 + v3):
//   - Direct ObjectProperty / ClassProperty / InterfaceProperty
//     (8-byte UObject* read directly at field+0)
//   - Direct WeakObjectProperty / SoftObjectProperty / SoftClassProperty /
//     LazyObjectProperty (FWeakObjectPtr at field+0 → resolved via
//     ResolveWeakObjectPtr; only matches when the ref is currently bound
//     to a live UObject)
//   - DelegateProperty (single FScriptDelegate — FWeakObjectPtr target at
//     field+0; same resolution path)
//   - MulticastInlineDelegateProperty / MulticastDelegateProperty
//     (FMulticastScriptDelegate is just TArray<FScriptDelegate> at
//     field+0; each element's FWeakObjectPtr is the binding target).
//     MulticastSparseDelegateProperty deliberately NOT covered — bindings
//     live in FSparseDelegateStorage rather than at the field.
//   - OptionalProperty<T> for pointer-shaped T — bucketed alongside its
//     bare T because the intrusive layout is identical at field+0.
//   - TArray<UObject*> / TArray<UClass*> (8-byte stride)
//   - TArray<FScriptInterface> (16-byte stride, ptr at elem+0)
//   - TArray<FWeakObjectPtr> / TArray<FSoftObjectPtr> /
//     TArray<FLazyObjectPtr> / TArray<FScriptDelegate> (variable stride,
//     FWeakObjectPtr at elem+0)
//   - TMap<UObject*,V> / TMap<K,UObject*> (TSparseArray walk, allocated
//     slots only — frees aren't real references)
//   - TSet<UObject*> (TSparseArray walk, allocated slots only)
//
// Each entry's `offset` is absolute within the owner UObject (parent
// struct offsets pre-summed for nested fields, depth-capped at 3).

struct DirectPointerEntry {
    int32_t     offset;
    std::string name;
    std::string typeName;     // "ObjectProperty" / "ClassProperty" / "InterfaceProperty"
};

// FWeakObjectPtr-shaped single field: { int32 ObjectIndex, int32 Serial }
// at field+0. Soft/Lazy place this same struct at the head of a richer
// envelope (FSoftObjectPath / FGuid follows) but the resolution path is
// identical — the embedded weak ptr is what reveals the live UObject.
struct WeakLikePointerEntry {
    int32_t     offset;
    std::string name;
    std::string typeName;     // "WeakObjectProperty" / "SoftObjectProperty"
                              // / "SoftClassProperty" / "LazyObjectProperty"
};

struct ObjectArrayEntry {
    int32_t     offset;
    std::string name;
    std::string innerType;    // "ObjectProperty" / "ClassProperty"
};

// TArray<FScriptInterface>: 16-byte elements, UObject* at elem+0.
struct InterfaceArrayEntry {
    int32_t     offset;
    std::string name;
};

// TArray<FWeakObjectPtr>/<FSoftObjectPtr>/<FLazyObjectPtr>: variable
// per-element stride, FWeakObjectPtr at elem+0 in every case.
struct WeakLikeArrayEntry {
    int32_t     offset;
    std::string name;
    std::string innerType;    // Same vocabulary as WeakLikePointerEntry::typeName
    int32_t     elemStride;   // From Ubel::GetArrayInnerElemSize
};

// TMap with at least one Object/Class side. Both flags can be true for a
// TMap<UObject*, UObject*> — scan emits one match per matching side.
struct ObjectMapEntry {
    int32_t     offset;
    std::string name;
    int32_t     pairStride;
    int32_t     valueOffset;  // Within each pair
    bool        keyIsObject;
    bool        valueIsObject;
    std::string keyTypeName;    // "ObjectProperty" / "ClassProperty" (for matched side)
    std::string valueTypeName;
    std::string innerLabel;     // "<keyType> → <valueType>" for UI
};

// TSet with Object/Class element type.
struct ObjectSetEntry {
    int32_t     offset;
    std::string name;
    int32_t     elemStride;
    std::string elemTypeName;   // "ObjectProperty" / "ClassProperty"
};

struct ClassReferenceMeta {
    std::vector<DirectPointerEntry>     directPointers;
    std::vector<WeakLikePointerEntry>   weakLikePointers;
    std::vector<ObjectArrayEntry>       objectArrays;
    std::vector<InterfaceArrayEntry>    interfaceArrays;
    std::vector<WeakLikeArrayEntry>     weakLikeArrays;
    std::vector<ObjectMapEntry>         objectMaps;
    std::vector<ObjectSetEntry>         objectSets;

    bool empty() const {
        return directPointers.empty() && weakLikePointers.empty()
            && objectArrays.empty() && interfaceArrays.empty()
            && weakLikeArrays.empty() && objectMaps.empty()
            && objectSets.empty();
    }
};

static bool IsWeakLikeProp(const std::string& tn) {
    return tn == "WeakObjectProperty" || tn == "SoftObjectProperty"
        || tn == "SoftClassProperty"  || tn == "LazyObjectProperty";
}
static bool IsDirectObjectProp(const std::string& tn) {
    return tn == "ObjectProperty" || tn == "ClassProperty";
}

static std::unordered_map<uintptr_t, ClassReferenceMeta> s_classRefCache;
static std::mutex s_classRefMutex;

// Recursive walker — descends through StructProperty (depth-capped) and
// emits one cache entry for each pointer-shaped field, pointer-array
// field, ObjectMap, or ObjectSet found. Mirrors CollectContainersRecursive.
static void CollectRefMetaRecursive(uintptr_t structAddr,
                                     int32_t baseOffset,
                                     const std::string& namePrefix,
                                     ClassReferenceMeta& out,
                                     int depth)
{
    constexpr int kMaxDepth = 3;
    if (depth > kMaxDepth) return;

    auto ci = Ubel::WalkClassEx(structAddr);
    for (const auto& f : ci.Fields) {
        if (!f.Address) continue;

        std::string fullName = namePrefix.empty()
            ? f.Name
            : (namePrefix + "." + f.Name);
        int32_t absOffset = baseOffset + f.Offset;

        // --- Single pointer fields ---
        if (IsDirectObjectProp(f.TypeName) || f.TypeName == "InterfaceProperty") {
            // All three layouts hold a UObject* at field+0 (FScriptInterface
            // also has ifacePtr at +8, but we ignore that — only objPtr is
            // the resolvable reference).
            out.directPointers.push_back({ absOffset, fullName, f.TypeName });
        }
        else if (IsWeakLikeProp(f.TypeName)) {
            out.weakLikePointers.push_back({ absOffset, fullName, f.TypeName });
        }
        // --- TOptional<T> wrapping a pointer-shaped T ---
        // For pointer-shaped T, FOptionalProperty stores T directly at
        // field+0; "unset" is encoded as null/zero. So the comparison logic
        // is identical to the bare pointer/weak-like field — only the
        // type-name label changes (so the user can see it was reached via
        // an Optional). innerType comes from WalkClassEx.
        else if (f.TypeName == "OptionalProperty"
              && (IsDirectObjectProp(f.innerType)
                  || f.innerType == "InterfaceProperty")) {
            out.directPointers.push_back({ absOffset, fullName, f.TypeName });
        }
        else if (f.TypeName == "OptionalProperty"
              && IsWeakLikeProp(f.innerType)) {
            out.weakLikePointers.push_back({ absOffset, fullName, f.TypeName });
        }
        // --- DelegateProperty (single FScriptDelegate) ---
        // Layout: { FWeakObjectPtr Target(8B), FName FunctionName(8/16B) }.
        // The FWeakObjectPtr at field+0 is the binding's target — same
        // resolution path as WeakObjectProperty, so reuse weakLikePointers.
        // typeName is preserved so the user sees this was reached via a
        // delegate (a "register on click" bind, not a property reference).
        else if (f.TypeName == "DelegateProperty") {
            out.weakLikePointers.push_back({ absOffset, fullName, f.TypeName });
        }
        // --- MulticastInline / MulticastDelegate (single field) ---
        // FMulticastScriptDelegate := TArray<FScriptDelegate> at field+0.
        // Each binding has FWeakObjectPtr at elem+0 — same scan logic as
        // weakLikeArrays, just with a delegate-specific stride. (Sparse
        // multicast deliberately excluded: bindings live in
        // FSparseDelegateStorage, not at the field.)
        else if (f.TypeName == "MulticastInlineDelegateProperty"
              || f.TypeName == "MulticastDelegateProperty") {
            int32_t fnameSize = DynOff::bCasePreservingName ? 0x10 : 0x08;
            int32_t stride    = 8 + fnameSize;
            out.weakLikeArrays.push_back({ absOffset, fullName,
                                            f.TypeName, stride });
        }
        // --- Array of pointer-shaped types ---
        else if (f.TypeName == "ArrayProperty") {
            if (IsDirectObjectProp(f.innerType)) {
                out.objectArrays.push_back({ absOffset, fullName, f.innerType });
            }
            else if (f.innerType == "InterfaceProperty") {
                out.interfaceArrays.push_back({ absOffset, fullName });
            }
            else if (IsWeakLikeProp(f.innerType)) {
                int32_t es = Ubel::GetArrayInnerElemSize(f.Address);
                if (es > 0) {
                    out.weakLikeArrays.push_back({ absOffset, fullName,
                                                    f.innerType, es });
                }
            }
            else if (f.innerType == "DelegateProperty") {
                // TArray<FScriptDelegate> — element layout matches the
                // multicast bindings list. Stride is FName-size dependent.
                int32_t fnameSize = DynOff::bCasePreservingName ? 0x10 : 0x08;
                int32_t stride    = 8 + fnameSize;
                out.weakLikeArrays.push_back({ absOffset, fullName,
                                                f.innerType, stride });
            }
        }
        // --- Map with pointer-shaped key and/or value ---
        else if (f.TypeName == "MapProperty") {
            bool keyIsObj = IsDirectObjectProp(f.keyType);
            bool valIsObj = IsDirectObjectProp(f.valueType);
            if (keyIsObj || valIsObj) {
                Ubel::MapPairLayout layout;
                if (Ubel::GetMapPairLayout(f.Address, layout)
                    && layout.pairStride > 0) {
                    ObjectMapEntry e;
                    e.offset        = absOffset;
                    e.name          = fullName;
                    e.pairStride    = layout.pairStride;
                    e.valueOffset   = layout.valueOffset;
                    e.keyIsObject   = keyIsObj;
                    e.valueIsObject = valIsObj;
                    e.keyTypeName   = f.keyType;
                    e.valueTypeName = f.valueType;
                    e.innerLabel    = f.keyType + " → " + f.valueType;
                    out.objectMaps.push_back(std::move(e));
                }
            }
        }
        // --- Set with pointer-shaped element ---
        else if (f.TypeName == "SetProperty") {
            if (IsDirectObjectProp(f.elemType)) {
                int32_t st = Ubel::GetSetElementStride(f.Address);
                if (st > 0) {
                    out.objectSets.push_back({ absOffset, fullName,
                                                st, f.elemType });
                }
            }
        }
        // --- Recurse into nested structs ---
        else if (f.TypeName == "StructProperty") {
            uintptr_t innerStruct = 0;
            if (Macht::ReadSafe(f.Address + DynOff::FSTRUCTPROP_STRUCT, innerStruct)
                && innerStruct) {
                CollectRefMetaRecursive(innerStruct, absOffset, fullName,
                                         out, depth + 1);
            }
        }
        else if (f.TypeName == "OptionalProperty"
                 && f.innerType == "StructProperty") {
            // TOptional<FStruct>: { T value; uint8 bIsSet; } — value at field+0,
            // so absOffset is unchanged for sub-fields. The bIsSet trailing
            // byte doesn't matter for reverse scan: an unset slot is zero
            // and naturally fails pointer comparisons.
            uintptr_t innerProp = 0;
            uintptr_t innerStruct = 0;
            if (Macht::ReadSafe(f.Address + DynOff::FARRAYPROP_INNER, innerProp)
                && innerProp
                && Macht::ReadSafe(innerProp + DynOff::FSTRUCTPROP_STRUCT, innerStruct)
                && innerStruct) {
                CollectRefMetaRecursive(innerStruct, absOffset, fullName,
                                         out, depth + 1);
            }
        }
    }
}

static const ClassReferenceMeta& GetClassRefMeta(uintptr_t cls) {
    {
        std::lock_guard<std::mutex> lk(s_classRefMutex);
        auto it = s_classRefCache.find(cls);
        if (it != s_classRefCache.end()) return it->second;
    }

    ClassReferenceMeta meta;
    CollectRefMetaRecursive(cls, 0, "", meta, 0);

    std::lock_guard<std::mutex> lk(s_classRefMutex);
    auto [ins, _] = s_classRefCache.emplace(cls, std::move(meta));
    return ins->second;
}

static void FillRefMatchOwner(ReferenceMatch& m, uintptr_t obj, int32_t idx, uintptr_t cls) {
    m.ownerObj   = obj;
    m.ownerIndex = idx;
    uint32_t nameIdx = 0;
    if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx))
        m.ownerName = Serie::GetString(nameIdx);
    uint32_t clsNameIdx = 0;
    if (Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx))
        m.ownerClassName = Serie::GetString(clsNameIdx);
}

// Resolve FWeakObjectPtr at `addr` (8 bytes: int32 idx + int32 serial) to
// a live UObject* — same logic Ubel uses, inlined here so the scan loop
// stays self-contained.
static uintptr_t ResolveWeakAt(uintptr_t addr) {
    int32_t objIdx = 0, serial = 0;
    if (!Macht::ReadSafe(addr,     objIdx))  return 0;
    if (!Macht::ReadSafe(addr + 4, serial))  return 0;
    return Ubel::ResolveWeakObjectPtr(objIdx, serial);
}

// ============================================================
// TMap header reader — shared by FindReferencesToUObject (sparse pass)
// and WalkSparseDelegateBindings.
//
// Layout reference (UE 5.0+, verified against Everspace 2 UE 5.4 PDB,
// FSparseDelegateStorage::SparseDelegates):
//
//   TMap (0x50 bytes):
//     +0x00  Elements.Data.AllocatorInstance.Data    (TPair<...>* heap base)
//     +0x08  Elements.Data.ArrayNum                  (int32, total slots incl. freed)
//     +0x0C  Elements.Data.ArrayMax                  (int32)
//     +0x10  Elements.AllocationFlags inline data    (16B = 128 bits inline)
//     +0x20  Elements.AllocationFlags secondary ptr  (heap if NumBits > 128)
//     +0x28  Elements.AllocationFlags.NumBits        (int32)
//     +0x2C  Elements.AllocationFlags.MaxBits        (int32)
//     +0x30  Elements.FirstFreeIndex                 (int32)
//     +0x34  Elements.NumFreeIndices                 (int32)
//     +0x40  Hash secondary ptr
//     +0x48  HashSize                                (int32)
//
//   FSparseDelegateStorage outer TSetElement stride: 0x60
//     +0x00  Key   (UObjectBase*, 8B)
//     +0x08  Value (inner TMap, 0x50B)
//     +0x58  HashNextId / HashIndex (8B)
//
//   FSparseDelegateStorage inner TSetElement stride:
//     bCasePreservingName=false (FName=8): TPair=24, +HashId 8 = 0x20
//     bCasePreservingName=true  (FName=16): TPair=32, +HashId 8 = 0x28
//
//   TSharedPtr<TMulticastScriptDelegate, ThreadSafe> (16B):
//     +0x00  Object* (FMulticastScriptDelegate*)
//     +0x08  SharedReferenceCount*
//
//   FMulticastScriptDelegate (16B):
//     +0x00  TArray<FScriptDelegate> InvocationList { Data, Num, Max }
//
//   FScriptDelegate (16B or 24B for case-preserving FName):
//     +0x00  FWeakObjectPtr Object { int32 Idx, int32 Serial }
//     +0x08  FName FunctionName
// ============================================================

// ResolveTMapBitArrayBase — figure out where the AllocationFlags bits live.
// Inline if MaxBits <= 128; heap (secondaryPtr) otherwise.
static uintptr_t ResolveTMapBitArrayBase(uintptr_t mapAddr) {
    uintptr_t secondaryPtr = 0;
    Macht::ReadSafe(mapAddr + 0x20, secondaryPtr);
    if (secondaryPtr) return secondaryPtr;
    return mapAddr + 0x10;  // inline buffer
}

static bool TMapBitSet(uintptr_t bitArrayBase, int32_t idx) {
    if (idx < 0) return false;
    uint32_t word = 0;
    if (!Macht::ReadSafe(bitArrayBase + (idx >> 5) * 4u, word)) return false;
    return (word >> (idx & 31)) & 1u;
}

// Read a TMap header. Returns false on read failure.
struct TMapHeader {
    uintptr_t arrayData      = 0;
    int32_t   arrayNum       = 0;   // total slots (includes freed)
    int32_t   numFreeIndices = 0;
    uintptr_t bitArrayBase   = 0;
};

static bool ReadTMapHeader(uintptr_t mapAddr, TMapHeader& out) {
    if (!Macht::ReadSafe(mapAddr + 0x00, out.arrayData))      return false;
    if (!Macht::ReadSafe(mapAddr + 0x08, out.arrayNum))       return false;
    if (!Macht::ReadSafe(mapAddr + 0x34, out.numFreeIndices)) return false;
    out.bitArrayBase = ResolveTMapBitArrayBase(mapAddr);
    // Sanity: ArrayNum bounded; some games hit 6-7 figures of total entries
    // when many UObjects use sparse delegates, but never beyond 1M.
    if (out.arrayNum < 0 || out.arrayNum > 0x100000) return false;
    return true;
}

std::vector<ReferenceMatch> FindReferencesToUObject(uintptr_t target,
                                                     int32_t maxResults,
                                                     ContainerScanStats* stats)
{
    std::vector<ReferenceMatch> matches;
    if (stats) *stats = {};
    if (!target || !s_arrayAddr) return matches;
    if (maxResults <= 0) maxResults = 32;

    int32_t count = GetCount();
    if (count <= 0) return matches;
    if (stats) stats->objectsTotal = count;

    LOG_INFO("FindReferencesToUObject: scanning %d objects for ref to 0x%llX",
             count, static_cast<unsigned long long>(target));

    // Reference search is more expensive than container scan (each UObject
    // checked has up to N pointer fields + array elements). Bump deadline
    // to 30s so first-pass cache prime can complete on huge games.
    constexpr int kDeadlineMs = 30000;
    auto t0 = std::chrono::steady_clock::now();

    // Parallel GObjects walk. GetClassRefMeta + Ubel caches are mutex-guarded,
    // so per-thread state is just the match buffer + diagnostic counters; the
    // ascending-tid merge reproduces serial ascending-index ordering. (The
    // sparse-delegate pass below the loop is a single global-TMap walk and stays
    // serial — it runs once, after the merge.)
    struct ThreadResult {
        std::vector<ReferenceMatch> matches;
        int32_t                     scanned       = 0;
        int32_t                     classesPrimed = 0;
    };

    auto scan = ParallelGObjectsScan<ThreadResult>(count,
        [&](ThreadResult& tr, int32_t beginIdx, int32_t endIdx,
            std::atomic<bool>& deadlineHit) {

        // maxResults is a per-thread local cap; the ascending-tid merge truncates
        // to maxResults (== serial "first N in ascending index order").
        auto pushMatch = [&](ReferenceMatch&& m) -> bool {
            tr.matches.push_back(std::move(m));
            return static_cast<int>(tr.matches.size()) >= maxResults;
        };

        for (int32_t i = beginIdx; i < endIdx && static_cast<int>(tr.matches.size()) < maxResults; ++i) {
        // Chunk-relative stride (see ScanForValue) so the deadline + sibling
        // deadlineHit check fires from this chunk's first iteration.
        if (((i - beginIdx) & 0x3FF) == 0) {
            if (deadlineHit.load(std::memory_order_relaxed)) return;
            auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::steady_clock::now() - t0).count();
            if (dt > kDeadlineMs) {
                deadlineHit.store(true, std::memory_order_relaxed);
                return;
            }
        }
        tr.scanned++;

        uintptr_t obj = GetByIndex(i);
        if (!obj || obj == target) continue;  // Don't report self-reference

        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        const auto& meta = GetClassRefMeta(cls);
        if (meta.empty()) continue;
        ++tr.classesPrimed;

        bool hitMaxThisObj = false;

        // --- Direct ObjectProperty / ClassProperty / InterfaceProperty ---
        for (const auto& pfe : meta.directPointers) {
            uintptr_t ptr = 0;
            if (!Macht::ReadSafe(obj + pfe.offset, ptr)) continue;
            if (ptr != target) continue;

            ReferenceMatch m;
            FillRefMatchOwner(m, obj, i, cls);
            m.fieldOffset  = pfe.offset;
            m.fieldName    = pfe.name;
            m.fieldType    = pfe.typeName;
            m.elementIndex = -1;

            LOG_INFO("FindReferencesToUObject: hit %s.%s (%s, owner=0x%llX, %s)",
                     m.ownerName.c_str(), m.fieldName.c_str(),
                     pfe.typeName.c_str(),
                     static_cast<unsigned long long>(obj),
                     m.ownerClassName.c_str());
            if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
        }
        if (hitMaxThisObj) break;

        // --- Weak/Soft/Lazy single fields (FWeakObjectPtr at field+0) ---
        for (const auto& wpe : meta.weakLikePointers) {
            uintptr_t resolved = ResolveWeakAt(obj + wpe.offset);
            if (resolved != target) continue;

            ReferenceMatch m;
            FillRefMatchOwner(m, obj, i, cls);
            m.fieldOffset  = wpe.offset;
            m.fieldName    = wpe.name;
            m.fieldType    = wpe.typeName;
            m.elementIndex = -1;

            LOG_INFO("FindReferencesToUObject: hit %s.%s (%s, owner=0x%llX, %s)",
                     m.ownerName.c_str(), m.fieldName.c_str(),
                     wpe.typeName.c_str(),
                     static_cast<unsigned long long>(obj),
                     m.ownerClassName.c_str());
            if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
        }
        if (hitMaxThisObj) break;

        // --- TArray<UObject*> / TArray<UClass*> (8-byte stride) ---
        for (const auto& oae : meta.objectArrays) {
            Macht::TArrayView arr;
            if (!Macht::ReadTArray(obj + oae.offset, arr)) continue;
            if (arr.Count <= 0 || !arr.Data) continue;

            // Bulk-read the TArray's data buffer once and scan in-memory.
            constexpr int32_t kElemBytes = 8;
            std::vector<uintptr_t> buf(arr.Count, 0);
            if (!Macht::ReadBytesSafe(arr.Data, buf.data(),
                                       arr.Count * kElemBytes))
                continue;

            for (int32_t e = 0; e < arr.Count; ++e) {
                if (buf[e] != target) continue;

                ReferenceMatch m;
                FillRefMatchOwner(m, obj, i, cls);
                m.fieldOffset  = oae.offset;
                m.fieldName    = oae.name;
                m.fieldType    = "ArrayProperty";
                m.innerType    = oae.innerType;
                m.elementIndex = e;

                LOG_INFO("FindReferencesToUObject: hit %s.%s[%d] (Array<%s>, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(), e,
                         oae.innerType.c_str(),
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
            }
            if (hitMaxThisObj) break;
        }
        if (hitMaxThisObj) break;

        // --- TArray<FScriptInterface> (16-byte stride, ptr at elem+0) ---
        for (const auto& iae : meta.interfaceArrays) {
            Macht::TArrayView arr;
            if (!Macht::ReadTArray(obj + iae.offset, arr)) continue;
            if (arr.Count <= 0 || !arr.Data) continue;

            constexpr int32_t kElemBytes = 16;
            for (int32_t e = 0; e < arr.Count; ++e) {
                uintptr_t ptr = 0;
                if (!Macht::ReadSafe(arr.Data + static_cast<int64_t>(e) * kElemBytes, ptr))
                    continue;
                if (ptr != target) continue;

                ReferenceMatch m;
                FillRefMatchOwner(m, obj, i, cls);
                m.fieldOffset  = iae.offset;
                m.fieldName    = iae.name;
                m.fieldType    = "ArrayProperty";
                m.innerType    = "InterfaceProperty";
                m.elementIndex = e;

                LOG_INFO("FindReferencesToUObject: hit %s.%s[%d] (Array<InterfaceProperty>, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(), e,
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
            }
            if (hitMaxThisObj) break;
        }
        if (hitMaxThisObj) break;

        // --- TArray<FWeak/Soft/Lazy ObjectPtr> (FWeakObjectPtr at elem+0) ---
        for (const auto& wae : meta.weakLikeArrays) {
            Macht::TArrayView arr;
            if (!Macht::ReadTArray(obj + wae.offset, arr)) continue;
            if (arr.Count <= 0 || !arr.Data || wae.elemStride <= 0) continue;

            for (int32_t e = 0; e < arr.Count; ++e) {
                uintptr_t resolved = ResolveWeakAt(
                    arr.Data + static_cast<int64_t>(e) * wae.elemStride);
                if (resolved != target) continue;

                ReferenceMatch m;
                FillRefMatchOwner(m, obj, i, cls);
                m.fieldOffset  = wae.offset;
                m.fieldName    = wae.name;
                m.fieldType    = "ArrayProperty";
                m.innerType    = wae.innerType;
                m.elementIndex = e;

                LOG_INFO("FindReferencesToUObject: hit %s.%s[%d] (Array<%s>, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(), e,
                         wae.innerType.c_str(),
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
            }
            if (hitMaxThisObj) break;
        }
        if (hitMaxThisObj) break;

        // --- TMap<UObject*, V> / TMap<K, UObject*> (allocated slots only) ---
        for (const auto& ome : meta.objectMaps) {
            Macht::TSparseArrayView sa;
            if (!Macht::ReadTSparseArray(obj + ome.offset, sa)) continue;
            if (sa.MaxIndex <= 0 || !sa.Data || ome.pairStride <= 0) continue;

            for (int32_t e = 0; e < sa.MaxIndex; ++e) {
                if (!Macht::IsSparseIndexAllocated(sa, e)) continue;
                uintptr_t pair = sa.Data + static_cast<int64_t>(e) * ome.pairStride;

                if (ome.keyIsObject) {
                    uintptr_t kp = 0;
                    if (Macht::ReadSafe(pair, kp) && kp == target) {
                        ReferenceMatch m;
                        FillRefMatchOwner(m, obj, i, cls);
                        m.fieldOffset  = ome.offset;
                        m.fieldName    = ome.name + ".Key";
                        m.fieldType    = "MapProperty";
                        m.innerType    = ome.innerLabel;
                        m.elementIndex = e;
                        LOG_INFO("FindReferencesToUObject: hit %s.%s.Key[%d] (Map<%s>, owner=0x%llX, %s)",
                                 m.ownerName.c_str(), ome.name.c_str(), e,
                                 ome.innerLabel.c_str(),
                                 static_cast<unsigned long long>(obj),
                                 m.ownerClassName.c_str());
                        if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
                    }
                }
                if (ome.valueIsObject) {
                    uintptr_t vp = 0;
                    if (Macht::ReadSafe(pair + ome.valueOffset, vp) && vp == target) {
                        ReferenceMatch m;
                        FillRefMatchOwner(m, obj, i, cls);
                        m.fieldOffset  = ome.offset;
                        m.fieldName    = ome.name + ".Value";
                        m.fieldType    = "MapProperty";
                        m.innerType    = ome.innerLabel;
                        m.elementIndex = e;
                        LOG_INFO("FindReferencesToUObject: hit %s.%s.Value[%d] (Map<%s>, owner=0x%llX, %s)",
                                 m.ownerName.c_str(), ome.name.c_str(), e,
                                 ome.innerLabel.c_str(),
                                 static_cast<unsigned long long>(obj),
                                 m.ownerClassName.c_str());
                        if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
                    }
                }
            }
            if (hitMaxThisObj) break;
        }
        if (hitMaxThisObj) break;

        // --- TSet<UObject*> (allocated slots only) ---
        for (const auto& ose : meta.objectSets) {
            Macht::TSparseArrayView sa;
            if (!Macht::ReadTSparseArray(obj + ose.offset, sa)) continue;
            if (sa.MaxIndex <= 0 || !sa.Data || ose.elemStride <= 0) continue;

            for (int32_t e = 0; e < sa.MaxIndex; ++e) {
                if (!Macht::IsSparseIndexAllocated(sa, e)) continue;
                uintptr_t elem = sa.Data + static_cast<int64_t>(e) * ose.elemStride;
                uintptr_t ptr = 0;
                if (!Macht::ReadSafe(elem, ptr)) continue;
                if (ptr != target) continue;

                ReferenceMatch m;
                FillRefMatchOwner(m, obj, i, cls);
                m.fieldOffset  = ose.offset;
                m.fieldName    = ose.name;
                m.fieldType    = "SetProperty";
                m.innerType    = ose.elemTypeName;
                m.elementIndex = e;

                LOG_INFO("FindReferencesToUObject: hit %s.%s[%d] (Set<%s>, owner=0x%llX, %s)",
                         m.ownerName.c_str(), m.fieldName.c_str(), e,
                         ose.elemTypeName.c_str(),
                         static_cast<unsigned long long>(obj),
                         m.ownerClassName.c_str());
                if (pushMatch(std::move(m))) { hitMaxThisObj = true; break; }
            }
            if (hitMaxThisObj) break;
        }
        if (hitMaxThisObj) break;
    }
    });  // ParallelGObjectsScan

    // Fold per-thread stats; match vectors concat in ascending tid order.
    int32_t scanned = 0, classesPrimed = 0;
    for (auto& tr : scan.perThread) {
        scanned       += tr.scanned;
        classesPrimed += tr.classesPrimed;
    }
    matches = ConcatTruncate(scan.perThread, &ThreadResult::matches, maxResults);

    // Carry the parallel phase's deadline state into the serial sparse pass as a
    // plain bool (the atomic lived inside ParallelGObjectsScan). The sparse pass
    // may set it if IT runs long; the epilogue reports the final value.
    bool deadlineHit = scan.deadlineHit;

    // Serial pushMatch for the single-pass sparse-delegate walk below (appends
    // to the already-merged `matches`).
    auto pushMatch = [&](ReferenceMatch&& m) -> bool {
        matches.push_back(std::move(m));
        return static_cast<int>(matches.size()) >= maxResults;
    };

    // ── MulticastSparseDelegateProperty pass (UE 5.0+) ────────────────
    // Field-level scan above can't see sparse delegates because their
    // bindings live in a CoreUObject-global TMap, not in the owning
    // UObject's memory. Walk that TMap once and check every binding's
    // FWeakObjectPtr against `target`. Skipped silently when AOB scan
    // failed or UE version is unsupported.
    if (static_cast<int>(matches.size()) < maxResults && !deadlineHit &&
        ::g_cachedUEVersion >= 500)
    {
        uintptr_t storage = Genau::FindSparseDelegateStorage();
        if (storage) {
            TMapHeader outerHdr{};
            if (ReadTMapHeader(storage, outerHdr) && outerHdr.arrayData &&
                outerHdr.arrayNum > 0)
            {
                constexpr int32_t kOuterStride = 0x60;
                constexpr int32_t kOuterValueOffset = 0x08;
                int fnameSize = DynOff::bCasePreservingName ? 0x10 : 0x08;
                int32_t innerStride = (fnameSize == 0x10 ? 0x28 : 0x20);
                int32_t scriptDelegateSize = 8 + fnameSize;

                int32_t outerVisited = 0;
                bool sparseAbort = false;
                for (int32_t oi = 0; oi < outerHdr.arrayNum && !sparseAbort; ++oi) {
                    if (!TMapBitSet(outerHdr.bitArrayBase, oi)) continue;
                    if ((++outerVisited & 0xFF) == 0) {
                        auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                                      std::chrono::steady_clock::now() - t0).count();
                        if (dt > kDeadlineMs) { deadlineHit = true; break; }
                    }

                    uintptr_t outerSlot = outerHdr.arrayData +
                        static_cast<uintptr_t>(oi) * kOuterStride;
                    uintptr_t ownerObj = 0;
                    if (!Macht::ReadSafe(outerSlot, ownerObj) || !ownerObj) continue;
                    if (ownerObj == target) continue;  // self-reference suppressed

                    uintptr_t innerMapAddr = outerSlot + kOuterValueOffset;
                    TMapHeader innerHdr{};
                    if (!ReadTMapHeader(innerMapAddr, innerHdr)) continue;
                    if (!innerHdr.arrayData || innerHdr.arrayNum == 0) continue;

                    for (int32_t ii = 0; ii < innerHdr.arrayNum; ++ii) {
                        if (!TMapBitSet(innerHdr.bitArrayBase, ii)) continue;
                        uintptr_t innerSlot = innerHdr.arrayData +
                            static_cast<uintptr_t>(ii) * innerStride;

                        int32_t funcComp = 0;
                        if (!Macht::ReadSafe(innerSlot, funcComp)) continue;
                        std::string fieldFName = Serie::GetString(funcComp);

                        // TPair: FName at +0, TSharedPtr at +fnameSize
                        uintptr_t mcdAddr = 0;
                        if (!Macht::ReadSafe(innerSlot + fnameSize, mcdAddr) || !mcdAddr)
                            continue;

                        uintptr_t invData = 0;
                        int32_t   invNum  = 0;
                        Macht::ReadSafe(mcdAddr + 0x00, invData);
                        Macht::ReadSafe(mcdAddr + 0x08, invNum);
                        if (invNum < 0 || invNum > 4096 || !invData) continue;

                        for (int32_t bi = 0; bi < invNum; ++bi) {
                            uintptr_t bindAddr = invData +
                                static_cast<uintptr_t>(bi) * scriptDelegateSize;
                            uintptr_t resolved = ResolveWeakAt(bindAddr);
                            if (resolved != target) continue;

                            // Match. Resolve owner metadata.
                            int32_t ownerIdx = -1;
                            Macht::ReadSafe(ownerObj + Grimoire::OFF_UOBJECT_INDEX,
                                            ownerIdx);
                            uintptr_t ownerCls = 0;
                            Macht::ReadSafe(ownerObj + Grimoire::OFF_UOBJECT_CLASS,
                                            ownerCls);

                            ReferenceMatch m;
                            FillRefMatchOwner(m, ownerObj, ownerIdx, ownerCls);
                            m.fieldOffset  = 0;  // unknown — bindings live outside owner
                            m.fieldName    = fieldFName;
                            m.fieldType    = "MulticastSparseDelegateProperty";
                            m.elementIndex = bi;

                            LOG_INFO("FindReferencesToUObject: hit %s.%s[%d] "
                                     "(MulticastSparseDelegateProperty, owner=0x%llX, %s)",
                                     m.ownerName.c_str(), m.fieldName.c_str(), bi,
                                     static_cast<unsigned long long>(ownerObj),
                                     m.ownerClassName.c_str());
                            if (pushMatch(std::move(m))) { sparseAbort = true; break; }
                        }
                        if (sparseAbort) break;
                    }
                }
            }
        }
    }

    auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                  std::chrono::steady_clock::now() - t0).count();
    if (stats) {
        stats->objectsScanned = scanned;
        stats->classesPrimed  = classesPrimed;
        stats->durationMs     = static_cast<int64_t>(dt);
        stats->deadlineHit    = deadlineHit;
    }
    LOG_INFO("FindReferencesToUObject: found %d matches in %lld ms (scanned %d/%d, %d classes with refs, %d thread(s)%s)",
             static_cast<int>(matches.size()), static_cast<long long>(dt),
             scanned, count, classesPrimed, scan.nthreads, deadlineHit ? ", DEADLINE HIT" : "");
    return matches;
}

// === Property Keyword Search ===

// Identify "class-like" metas. UClass instances have meta-class name "Class",
// but UE has several UClass subclasses whose own meta is a different string:
//   * Class                           — regular C++ UClass
//   * BlueprintGeneratedClass         — every BP-derived class (most games)
//   * AnimBlueprintGeneratedClass     — Anim BP-derived classes
//   * WidgetBlueprintGeneratedClass   — UMG widget BP-derived classes
//   * DynamicClass                    — Shipping cooked dynamic classes
// Before this whitelist, SearchProperties / ListClasses / EnumerateAllFunctions
// matched only "Class" and silently dropped every game-specific BPGC — which
// is where 90%+ of game-specific Health / Damage / Gold properties live. The
// user's TowerOfMask repro: `SearchProperties 'Health': 0 matches` despite
// `Health @ AnimMan_Player_C` clearly existing in the Class Struct view.
static bool IsClassLikeMeta(const std::string& metaClassName) {
    return metaClassName == "Class"
        || metaClassName == "BlueprintGeneratedClass"
        || metaClassName == "AnimBlueprintGeneratedClass"
        || metaClassName == "WidgetBlueprintGeneratedClass"
        || metaClassName == "DynamicClass";
}

// Engine packages to skip when gameOnly is true
static bool IsEnginePackage(const std::string& path) {
    static const char* kEnginePrefixes[] = {
        "/Script/Engine",
        "/Script/CoreUObject",
        "/Script/CoreOnline",
        "/Script/UMG",
        "/Script/Slate",
        "/Script/SlateCore",
        "/Script/InputCore",
        "/Script/PhysicsCore",
        "/Script/NavigationSystem",
        "/Script/AIModule",
        "/Script/Niagara",
        "/Script/MovieScene",
        "/Script/LevelSequence",
        "/Script/Landscape",
        "/Script/Foliage",
        "/Script/AnimGraphRuntime",
        "/Script/AudioMixer",
        "/Script/ChaosCloth",
        "/Script/ChaosSolverEngine",
        "/Script/ClothingSystemRuntimeNv",
        "/Script/GeometryCollectionEngine",
        "/Script/FieldSystemEngine",
        "/Script/GameplayTags",
        "/Script/GameplayTasks",
        "/Script/GameplayAbilities",
        "/Script/PacketHandler",
        "/Script/PropertyAccess",
        "/Script/DeveloperSettings",
        "/Script/AssetRegistry",
        "/Script/MediaAssets",
        "/Script/HeadMountedDisplay",
    };

    for (const auto* prefix : kEnginePrefixes) {
        size_t prefixLen = std::strlen(prefix);
        // Match exact prefix followed by end-of-string, '/', or '.'
        if (path.compare(0, prefixLen, prefix) == 0) {
            if (path.size() == prefixLen || path[prefixLen] == '/' || path[prefixLen] == '.') {
                return true;
            }
        }
    }
    return false;
}

static std::string ToLower(const std::string& s) {
    std::string out = s;
    for (auto& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

// FindDefiningClass: walk SuperStruct chain upward and return the
// highest-up class that still declares the property at `fieldOffset`.
//
// Algorithm:
//   For class C with SuperStruct S:
//     - If S exists and S.PropertiesSize > fieldOffset, then S has the
//       property too -- keep walking up (cur = S).
//     - Otherwise S doesn't have it (or no S), so C is the defining
//       class.
//
// 32-step depth cap matches Ubel's WalkClass inherited-walk so a
// pathological cycle in the SuperStruct chain can't hang us.
uintptr_t FindDefiningClass(uintptr_t classAddr, int32_t fieldOffset) {
    if (!classAddr) return 0;
    uintptr_t cur = classAddr;
    for (int depth = 0; depth < 32; ++depth) {
        uintptr_t super = 0;
        if (!Macht::ReadSafe(cur + DynOff::USTRUCT_SUPER, super) || !super) {
            // No super left -- cur is at the root (UObject) and must
            // be where the property lives.
            return cur;
        }
        int32_t superPropsSize = 0;
        if (!Macht::ReadSafe(super + DynOff::USTRUCT_PROPSSIZE, superPropsSize)
            || superPropsSize <= 0) {
            // Can't read super's PropertiesSize -- conservatively
            // attribute to current class.
            return cur;
        }
        // Super has the property too iff its size covers this offset.
        // Note: PropertiesSize is the END of the struct, so a property
        // at offset O is inside super iff O < superPropsSize.
        if (fieldOffset < superPropsSize) {
            cur = super;  // super has it; keep going up
            continue;
        }
        return cur;  // super doesn't have it; cur is the defining class
    }
    return cur;
}

// Cache for FindDefiningClass results -- per (classAddr, fieldOffset).
// Reset implicitly per SearchProperties call (keyed by a thread-local
// epoch would be over-engineering; the cost is one map per call).

PropertySearchResult SearchProperties(
    const std::string& query,
    const std::vector<std::string>& typeFilter,
    bool gameOnly,
    int maxResults)
{
    PropertySearchResult result;

    std::string lowerQuery = ToLower(query);

    // Build lowercase type filter set for fast lookup
    std::unordered_set<std::string> typeSet;
    for (const auto& t : typeFilter) typeSet.insert(ToLower(t));

    // Track already-visited UClass addresses to avoid duplicates
    std::unordered_set<uintptr_t> visitedClasses;

    // Per-call cache of FindDefiningClass results -- a single property
    // walked across many subclasses would otherwise re-walk the
    // SuperStruct chain redundantly. Keyed by (classAddr, fieldOffset)
    // because different fields on the same class have different
    // defining classes. Wrapped in a struct because std::pair isn't
    // hashable out of the box.
    struct FieldKey {
        uintptr_t classAddr;
        int32_t   offset;
        bool operator==(const FieldKey& o) const {
            return classAddr == o.classAddr && offset == o.offset;
        }
    };
    struct FieldKeyHash {
        size_t operator()(const FieldKey& k) const {
            return std::hash<uintptr_t>{}(k.classAddr)
                 ^ (std::hash<int32_t>{}(k.offset) << 1);
        }
    };
    std::unordered_map<FieldKey, uintptr_t, FieldKeyHash> definingCache;

    // Dedup map: groups inheriting classes by (definingClass, propName, offset).
    // Value is the index into `result.results` for that group's
    // representative match. inheritedByCount accumulates as we visit
    // more classes that inherit the same field.
    struct DedupKey {
        uintptr_t   definingClassAddr;
        std::string propName;
        int32_t     offset;
        bool operator==(const DedupKey& o) const {
            return definingClassAddr == o.definingClassAddr
                && offset == o.offset
                && propName == o.propName;
        }
    };
    struct DedupKeyHash {
        size_t operator()(const DedupKey& k) const {
            return std::hash<uintptr_t>{}(k.definingClassAddr)
                 ^ (std::hash<std::string>{}(k.propName) << 1)
                 ^ (std::hash<int32_t>{}(k.offset) << 2);
        }
    };
    std::unordered_map<DedupKey, size_t, DedupKeyHash> dedupIndex;

    int32_t count = GetCount();
    result.scannedObjects = count;

    for (int32_t i = 0; i < count && static_cast<int>(result.results.size()) < maxResults; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        // Identify class-like objects (UClass + BlueprintGeneratedClass +
        // variants). See IsClassLikeMeta for why "== Class" alone is too
        // strict — it drops every BPGC and breaks property search on
        // game-specific BP fields.
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        uint32_t clsNameIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;

        std::string metaClassName = Serie::GetString(clsNameIdx);
        if (!IsClassLikeMeta(metaClassName)) continue;

        // This object is a class. Skip if already visited.
        if (!visitedClasses.insert(obj).second) continue;

        // Get class path for game_only filter
        std::string classPath = Ubel::GetFullName(obj);
        if (gameOnly && IsEnginePackage(classPath)) continue;

        result.scannedClasses++;

        // Walk class properties (including inherited)
        ClassInfo ci = Ubel::WalkClassEx(obj);
        if (ci.Fields.empty()) continue;

        // Search properties
        for (const auto& field : ci.Fields) {
            if (static_cast<int>(result.results.size()) >= maxResults) break;

            // Case-insensitive substring match on property name
            std::string lowerPropName = ToLower(field.Name);
            if (lowerPropName.find(lowerQuery) == std::string::npos) continue;

            // Optional type filter
            if (!typeSet.empty()) {
                std::string lowerType = ToLower(field.TypeName);
                if (typeSet.find(lowerType) == typeSet.end()) continue;
            }

            // Resolve defining class (cached per field key).
            FieldKey fk{ obj, field.Offset };
            uintptr_t definingAddr = 0;
            auto cacheIt = definingCache.find(fk);
            if (cacheIt != definingCache.end()) {
                definingAddr = cacheIt->second;
            } else {
                definingAddr = FindDefiningClass(obj, field.Offset);
                definingCache[fk] = definingAddr;
            }
            if (!definingAddr) definingAddr = obj;  // safety net

            // Dedup: have we seen this defining-class+name+offset combo?
            DedupKey dk{ definingAddr, field.Name, field.Offset };
            auto dedupIt = dedupIndex.find(dk);
            if (dedupIt != dedupIndex.end()) {
                // This class inherits a field we've already emitted.
                // Bump the inheritedByCount on the existing match.
                auto& existing = result.results[dedupIt->second];
                existing.inheritedByCount++;
                // Update preview-source if THIS subclass is more derived
                // (bigger PropertiesSize) than the previous best -- bias
                // toward leaf classes that actually have live instances.
                if (ci.PropertiesSize > existing.previewPropertiesSize) {
                    existing.previewClassAddr      = obj;
                    existing.previewPropertiesSize = ci.PropertiesSize;
                }
                continue;
            }

            // First time seeing this (definingClass, propName, offset)
            // triple -- emit a representative row keyed by the defining
            // class, NOT the iterated class. That way the user sees
            // "bCanBeDamaged @ AActor (inherited by 4822)" instead of
            // "bCanBeDamaged @ BP_RandomChild_C" depending on iteration
            // order.
            std::string definingName;
            std::string definingPath;
            if (definingAddr == obj) {
                // Defining class is the one we're iterating -- already have its name/path.
                definingName = ci.Name;
                definingPath = classPath;
            } else {
                // Defining class is somewhere up the chain -- read its name + path.
                definingName = Ubel::GetName(definingAddr);
                definingPath = Ubel::GetFullName(definingAddr);
            }

            PropertyMatch match;
            // The headline className/classAddr/classPath/superName all
            // reflect the DEFINING class -- the user wants to see the
            // canonical home of the field, not whichever subclass we
            // happened to iterate first.
            match.className   = definingName;
            match.classAddr   = definingAddr;
            match.classPath   = definingPath;
            // SuperName is the defining class's super (if we can read it
            // cheaply). For non-iterated defining-classes we'd need to
            // read it via DynOff::USTRUCT_SUPER -> name; skip for now
            // since it's not load-bearing for the dedup story.
            match.superName   = (definingAddr == obj) ? ci.SuperName : "";
            match.propName    = field.Name;
            match.propType    = field.TypeName;
            match.propOffset  = field.Offset;
            match.propSize    = field.Size;
            match.structType  = field.structType;
            match.innerType   = field.innerType;
            // Inheritance fields
            match.definingClassName = definingName;
            match.definingClassAddr = definingAddr;
            match.definingClassPath = definingPath;
            match.inheritedByCount  = 0;  // bumps as we encounter inheritors below
            // Preview metadata (read from any class -- the defining
            // class CDO would be most "canonical" but the iterated
            // class's preview is still valid since the field is
            // identical).
            match.fieldAddr      = field.Address;
            match.boolFieldMask  = field.boolFieldMask;
            match.keyType        = field.keyType;
            match.valueType      = field.valueType;
            // Seed preview source with the iterated class -- guaranteed
            // to have the field (we're walking its property chain). Will
            // be replaced by a more-derived subclass on later count bumps.
            match.previewClassAddr      = obj;
            match.previewPropertiesSize = ci.PropertiesSize;

            dedupIndex[dk] = result.results.size();
            result.results.push_back(std::move(match));
        }
    }

    // --- Phase 2: Resolve value previews from representative instances ---
    //
    // After dedup, match.classAddr is the DEFINING class (often abstract
    // -- AActor / APawn / etc -- with no direct instances). We use the
    // separate previewClassAddr (the most-derived subclass observed
    // during the search loop) to find a live instance whose data we can
    // sample. Since the property is at the same offset on every subclass,
    // the preview value is identical regardless of which subclass we
    // sampled.
    if (!result.results.empty()) {
        // 2a. Collect unique preview-source class set (subclasses
        // chosen for instance lookup, NOT the defining classes).
        std::unordered_set<uintptr_t> needPreviewClasses;
        for (const auto& m : result.results)
            needPreviewClasses.insert(m.previewClassAddr);

        // 2b. Scan GObjects to find one instance per preview-source class.
        std::unordered_map<uintptr_t, uintptr_t> instanceMap;
        int32_t cnt = GetCount();
        for (int32_t i = 0; i < cnt && instanceMap.size() < needPreviewClasses.size(); ++i) {
            uintptr_t obj = GetByIndex(i);
            if (!obj) continue;

            uintptr_t cls = 0;
            if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

            // Skip if this IS a UClass (we want instances, not the class itself)
            if (needPreviewClasses.count(cls) && !instanceMap.count(cls) && obj != cls) {
                instanceMap[cls] = obj;
            }
        }

        // 2c. Read property values and fill previews. ResolvePropertyPreviews
        // expects the match's classAddr to key into instanceMap, but we
        // want it to use previewClassAddr instead. Temporarily swap, run,
        // then swap back so the wire output keeps the defining-class
        // address as the canonical classAddr.
        if (!instanceMap.empty()) {
            // Resolve EnumProperty: read UEnum* from FField for matches that need it
            for (auto& m : result.results) {
                if (m.propType == "EnumProperty" && m.enumAddr == 0 && m.fieldAddr) {
                    Macht::ReadSafe(m.fieldAddr + DynOff::FENUMPROP_ENUM, m.enumAddr);
                }
            }
            // Swap classAddr <-> previewClassAddr around the call.
            for (auto& m : result.results) {
                std::swap(m.classAddr, m.previewClassAddr);
            }
            Ubel::ResolvePropertyPreviews(result.results, instanceMap);
            for (auto& m : result.results) {
                std::swap(m.classAddr, m.previewClassAddr);
            }
        }
    }

    Sein::Info("PIPE:search", "SearchProperties '%s': %d matches from %d classes (scanned %d objects)",
                 query.c_str(), static_cast<int>(result.results.size()),
                 result.scannedClasses, result.scannedObjects);
    return result;
}

// === Property Keyword Search — Batched ===
//
// Walks GObjects + WalkClassEx ONCE and checks every property against
// every query in `queries` in the same iteration. The big-O is now
// O(classes × fields × queries) but the queries-loop is a cheap
// std::string::find on lowercased names — the cost is dwarfed by the
// classes × fields walk that dominates the single-query version. For
// a 36-query / 4400-class game this drops wall time from ~42s
// (sequential pipe calls each re-walking GObjects) to ~1.5s.
//
// Per-query state (dedup index, results vector, fill count) is
// independent; per-field state (defining class, WalkClassEx output)
// is shared across queries. PropertyMatch.inheritedByCount counts
// inheritance hits PER QUERY since dedup keys are local to each
// query's result set.

std::vector<PropertySearchResult> SearchPropertiesBatch(
    const std::vector<std::string>& queries,
    const std::vector<std::string>& typeFilter,
    bool gameOnly,
    int maxResultsPerQuery,
    bool /*withPreviews*/)
{
    // Per-query state — independent dedup + results, lowercased query
    // pre-computed once.
    struct DedupKey {
        uintptr_t   definingClassAddr;
        std::string propName;
        int32_t     offset;
        bool operator==(const DedupKey& o) const {
            return definingClassAddr == o.definingClassAddr
                && offset == o.offset
                && propName == o.propName;
        }
    };
    struct DedupKeyHash {
        size_t operator()(const DedupKey& k) const {
            return std::hash<uintptr_t>{}(k.definingClassAddr)
                 ^ (std::hash<std::string>{}(k.propName) << 1)
                 ^ (std::hash<int32_t>{}(k.offset) << 2);
        }
    };
    struct QueryState {
        std::string lowerQuery;
        PropertySearchResult result;
        std::unordered_map<DedupKey, size_t, DedupKeyHash> dedup;
    };
    std::vector<QueryState> qs;
    qs.reserve(queries.size());
    for (const auto& q : queries) {
        qs.push_back(QueryState{ ToLower(q), {}, {} });
    }

    // Shared state across queries.
    std::unordered_set<std::string> typeSet;
    for (const auto& t : typeFilter) typeSet.insert(ToLower(t));

    struct FieldKey {
        uintptr_t classAddr;
        int32_t   offset;
        bool operator==(const FieldKey& o) const {
            return classAddr == o.classAddr && offset == o.offset;
        }
    };
    struct FieldKeyHash {
        size_t operator()(const FieldKey& k) const {
            return std::hash<uintptr_t>{}(k.classAddr)
                 ^ (std::hash<int32_t>{}(k.offset) << 1);
        }
    };

    std::unordered_set<uintptr_t> visitedClasses;
    std::unordered_map<FieldKey, uintptr_t, FieldKeyHash> definingCache;

    int32_t count = GetCount();
    int32_t scannedClasses = 0;

    for (int32_t i = 0; i < count; ++i) {
        // Early-exit: if every query is already at limit, stop walking.
        bool allFull = true;
        for (const auto& s : qs) {
            if (static_cast<int>(s.result.results.size()) < maxResultsPerQuery) {
                allFull = false;
                break;
            }
        }
        if (allFull) break;

        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        // Class-like meta filter (matches single-query SearchProperties).
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;
        uint32_t clsNameIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;
        std::string metaClassName = Serie::GetString(clsNameIdx);
        if (!IsClassLikeMeta(metaClassName)) continue;

        if (!visitedClasses.insert(obj).second) continue;

        std::string classPath = Ubel::GetFullName(obj);
        if (gameOnly && IsEnginePackage(classPath)) continue;

        scannedClasses++;

        ClassInfo ci = Ubel::WalkClassEx(obj);
        if (ci.Fields.empty()) continue;

        // Per-field: check every query in one pass over the field list.
        for (const auto& field : ci.Fields) {
            // Lowercase property name once per field (not per-query).
            std::string lowerPropName = ToLower(field.Name);

            // Type filter — apply once per field, before the keyword loop,
            // because it's keyword-independent.
            if (!typeSet.empty()) {
                std::string lowerType = ToLower(field.TypeName);
                if (typeSet.find(lowerType) == typeSet.end()) continue;
            }

            // Defining-class lookup: cached across queries since the
            // (class, offset) pair determines the definition site
            // independently of which keyword caused the match.
            uintptr_t definingAddr = 0;
            bool definingResolved = false;

            for (auto& s : qs) {
                if (static_cast<int>(s.result.results.size()) >= maxResultsPerQuery) continue;
                if (lowerPropName.find(s.lowerQuery) == std::string::npos) continue;

                // Resolve defining class lazily — only the first matching
                // query in this field triggers the lookup.
                if (!definingResolved) {
                    FieldKey fk{ obj, field.Offset };
                    auto cacheIt = definingCache.find(fk);
                    if (cacheIt != definingCache.end()) {
                        definingAddr = cacheIt->second;
                    } else {
                        definingAddr = FindDefiningClass(obj, field.Offset);
                        definingCache[fk] = definingAddr;
                    }
                    if (!definingAddr) definingAddr = obj;
                    definingResolved = true;
                }

                // Per-query dedup: if this query already emitted a match
                // for the same (defining-class, name, offset), bump the
                // inheritor count instead of duplicating.
                DedupKey dk{ definingAddr, field.Name, field.Offset };
                auto dedupIt = s.dedup.find(dk);
                if (dedupIt != s.dedup.end()) {
                    auto& existing = s.result.results[dedupIt->second];
                    existing.inheritedByCount++;
                    if (ci.PropertiesSize > existing.previewPropertiesSize) {
                        existing.previewClassAddr      = obj;
                        existing.previewPropertiesSize = ci.PropertiesSize;
                    }
                    continue;
                }

                // First-time match for this query — emit a new row.
                std::string definingName;
                std::string definingPath;
                if (definingAddr == obj) {
                    definingName = ci.Name;
                    definingPath = classPath;
                } else {
                    definingName = Ubel::GetName(definingAddr);
                    definingPath = Ubel::GetFullName(definingAddr);
                }

                PropertyMatch match;
                match.className   = definingName;
                match.classAddr   = definingAddr;
                match.classPath   = definingPath;
                match.superName   = (definingAddr == obj) ? ci.SuperName : "";
                match.propName    = field.Name;
                match.propType    = field.TypeName;
                match.propOffset  = field.Offset;
                match.propSize    = field.Size;
                match.structType  = field.structType;
                match.innerType   = field.innerType;
                match.definingClassName = definingName;
                match.definingClassAddr = definingAddr;
                match.definingClassPath = definingPath;
                match.inheritedByCount  = 0;
                match.fieldAddr      = field.Address;
                match.boolFieldMask  = field.boolFieldMask;
                match.keyType        = field.keyType;
                match.valueType      = field.valueType;
                match.previewClassAddr      = obj;
                match.previewPropertiesSize = ci.PropertiesSize;

                s.dedup[dk] = s.result.results.size();
                s.result.results.push_back(std::move(match));
            }
        }
    }

    // Phase 2 (preview resolution) is INTENTIONALLY SKIPPED in the
    // batch path. Interesting Properties — the primary consumer —
    // doesn't display previews; values are read on-demand when the user
    // opens a row in Live Walker. Skipping the second GObjects pass +
    // ResolvePropertyPreviews call buys us another big chunk of the
    // batch-vs-sequential speedup.
    //
    // If a future caller needs previews, add a branch on withPreviews
    // that mirrors the single-query Phase 2 — collect unique preview
    // classes across all queries, find one instance per class, resolve
    // previews per match.

    std::vector<PropertySearchResult> out;
    out.reserve(qs.size());
    for (auto& s : qs) {
        s.result.scannedObjects = count;
        s.result.scannedClasses = scannedClasses;
        out.push_back(std::move(s.result));
    }

    int totalMatches = 0;
    for (const auto& r : out) totalMatches += static_cast<int>(r.results.size());
    Sein::Info("PIPE:search", "SearchPropertiesBatch: %d queries -> %d total matches from %d classes (scanned %d objects)",
                 static_cast<int>(queries.size()), totalMatches,
                 scannedClasses, count);
    return out;
}

// === Batched class schema walk ===
//
// Pure pipe-amortisation helper: invokes Ubel::WalkClassEx once per
// input address and returns the results in the same order. Built as a
// trivial loop on top of the single-class function so each batch
// element is byte-identical to a single-call walk_class response —
// the safety guarantee that lets SdkExportService / DumpAllService
// switch from N round-trips to N/200 without risking dropped fields.
//
// Caller chunks the request (~200 addrs per call) to keep response
// payloads bounded and progress feedback live.
std::vector<ClassInfo> WalkClassesBatch(const std::vector<uintptr_t>& addrs)
{
    auto t0 = std::chrono::high_resolution_clock::now();
    std::vector<ClassInfo> out;
    out.reserve(addrs.size());

    int emptyCount = 0;
    for (uintptr_t addr : addrs) {
        // ClassInfo lives at global scope (see Ubel.h), but the
        // WalkClassEx function lives inside namespace Ubel.
        ClassInfo ci = Ubel::WalkClassEx(addr);
        if (ci.Fields.empty()) ++emptyCount;
        out.push_back(std::move(ci));
    }

    auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                       std::chrono::high_resolution_clock::now() - t0).count();
    Sein::Info("PIPE:walk", "WalkClassesBatch: %d addrs -> %d results (%d empty) in %lld ms",
               static_cast<int>(addrs.size()), static_cast<int>(out.size()),
               emptyCount, static_cast<long long>(elapsed));
    return out;
}

// --- Heuristic Scorer: auto-rank classes by RE interest ---

static int GetFieldTypeWeight(const std::string& typeName) {
    // High-value: game stats, collections
    if (typeName == "FloatProperty" || typeName == "DoubleProperty") return 3;
    if (typeName == "ArrayProperty") return 3;

    // Medium-value: integers, structs, object refs, maps/sets
    if (typeName == "IntProperty"   || typeName == "Int8Property"  ||
        typeName == "Int16Property" || typeName == "Int32Property" ||
        typeName == "Int64Property" || typeName == "UInt16Property"||
        typeName == "UInt32Property"|| typeName == "UInt64Property") return 2;
    if (typeName == "StructProperty") return 2;
    if (typeName == "ObjectProperty"     || typeName == "ClassProperty"      ||
        typeName == "WeakObjectProperty" || typeName == "LazyObjectProperty" ||
        typeName == "SoftObjectProperty" || typeName == "SoftClassProperty"  ||
        typeName == "InterfaceProperty") return 2;
    if (typeName == "MapProperty" || typeName == "SetProperty") return 2;

    // Low-value: enums, bools, strings, bytes
    if (typeName == "EnumProperty") return 1;
    if (typeName == "BoolProperty") return 1;
    if (typeName == "StrProperty"  || typeName == "TextProperty" ||
        typeName == "NameProperty" || typeName == "ByteProperty") return 1;

    return 1; // Unknown types get minimum weight
}

static int GetSuperClassBonus(const std::string& superName) {
    if (superName.empty()) return 0;

    // Convert to lowercase for case-insensitive matching
    std::string lower = superName;
    for (auto& c : lower) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

    // Checked in priority order (most specific first)
    if (lower.find("character") != std::string::npos || lower.find("pawn") != std::string::npos) return 20;
    if (lower.find("playercontroller") != std::string::npos ||
        lower.find("aicontroller")     != std::string::npos ||
        lower.find("controller")       != std::string::npos) return 15;
    if (lower.find("playerstate") != std::string::npos ||
        lower.find("gamestate")   != std::string::npos ||
        lower.find("gamemode")    != std::string::npos) return 15;
    if (lower.find("gameinstance") != std::string::npos) return 10;
    if (lower.find("actor") != std::string::npos) return 10;
    if (lower.find("actorcomponent") != std::string::npos ||
        lower.find("scenecomponent") != std::string::npos) return 8;
    if (lower.find("widget") != std::string::npos || lower.find("userwidget") != std::string::npos) return 5;
    if (lower.find("animinstance") != std::string::npos) return 5;
    if (lower.find("dataasset") != std::string::npos) return 5;

    return 0;
}

static int ComputeHeuristicScore(const ClassInfo& ci) {
    int score = 0;

    // Sum per-field type weights
    for (const auto& f : ci.Fields) {
        score += GetFieldTypeWeight(f.TypeName);
    }

    // Super class bonus
    score += GetSuperClassBonus(ci.SuperName);

    // Size bonus
    if (ci.PropertiesSize > 0x400)       score += 5;
    else if (ci.PropertiesSize > 0x100)  score += 3;
    else if (ci.PropertiesSize > 0)      score += 1;

    // Penalty for empty/abstract classes
    if (ci.Fields.empty()) score -= 5;

    return (score < 0) ? 0 : score;
}

// --- ListClasses ---

ClassListResult ListClasses(bool gameOnly, int maxResults) {
    ClassListResult result;

    std::unordered_set<uintptr_t> visitedClasses;

    int32_t count = GetCount();
    result.scannedObjects = count;

    for (int32_t i = 0; i < count && static_cast<int>(result.results.size()) < maxResults; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        // Identify class-like objects (UClass + BPGC variants); see
        // IsClassLikeMeta for the rationale on accepting more than just
        // "Class".
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        uint32_t clsNameIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;

        std::string metaClassName = Serie::GetString(clsNameIdx);
        if (!IsClassLikeMeta(metaClassName)) continue;

        // Skip if already visited
        if (!visitedClasses.insert(obj).second) continue;

        // Get class path for game_only filter
        std::string classPath = Ubel::GetFullName(obj);
        if (gameOnly && IsEnginePackage(classPath)) continue;

        result.totalClasses++;

        // Walk class to get property count and size
        ClassInfo ci = Ubel::WalkClassEx(obj);

        ClassListEntry entry;
        entry.className      = ci.Name;
        entry.classAddr      = obj;
        entry.classPath      = classPath;
        entry.superName      = ci.SuperName;
        entry.propertyCount  = static_cast<int32_t>(ci.Fields.size());
        entry.propertiesSize = ci.PropertiesSize;
        entry.heuristicScore = ComputeHeuristicScore(ci);
        result.results.push_back(std::move(entry));
    }

    // Sort by heuristic score descending, then alphabetically for ties
    std::sort(result.results.begin(), result.results.end(),
        [](const ClassListEntry& a, const ClassListEntry& b) {
            if (a.heuristicScore != b.heuristicScore)
                return a.heuristicScore > b.heuristicScore;
            return a.className < b.className;
        });

    Sein::Info("PIPE:list", "ListClasses: %d classes (gameOnly=%d, scanned %d objects)",
                 static_cast<int>(result.results.size()), gameOnly ? 1 : 0, result.scannedObjects);
    return result;
}

// --- EnumerateAllFunctions ---
//
// Mirrors the SearchProperties / ListClasses GObjects-walk pattern: scan
// every object, identify UClasses by metaclass-name, dedupe via a visited
// set, and flatten the per-class function list into a single result vector.
//
// Per-class cost is dominated by Ubel::WalkFunctions which walks the
// UField::Children chain (4096-iteration safety cap, 256-iteration param
// cap per function). On 1M-object games this typically takes 2-10s
// because the UFunction count per class is small (usually <50) and the
// per-class walk caches nothing — we pay the full O(F) per class.

AllFunctionsResult EnumerateAllFunctions(bool gameOnly, int maxEntries) {
    AllFunctionsResult result;

    std::unordered_set<uintptr_t> visitedClasses;

    int32_t count = GetCount();
    result.scannedObjects = count;

    for (int32_t i = 0; i < count; ++i) {
        if (static_cast<int>(result.entries.size()) >= maxEntries) break;

        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        // Identify class-like object (UClass + BPGC variants) via
        // IsClassLikeMeta — same helper SearchProperties + ListClasses use.
        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        uint32_t clsNameIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;

        std::string metaClassName = Serie::GetString(clsNameIdx);
        if (!IsClassLikeMeta(metaClassName)) continue;

        // Skip duplicates (same UClass can be referenced from multiple GObjects slots
        // when CDOs or hot-reload artefacts keep stale handles around).
        if (!visitedClasses.insert(obj).second) continue;

        std::string classPath = Ubel::GetFullName(obj);
        if (gameOnly && IsEnginePackage(classPath)) continue;

        result.scannedClasses++;

        // Walk class metadata + functions. WalkClassEx is needed for SuperName
        // (used by the UI's class-keyword scoring). It also walks fields, which
        // is wasted work here, but the alternative (a Functions-only walker)
        // would mean a parallel reader path -- not worth the maintenance burden
        // for the typical perf budget.
        ClassInfo ci = Ubel::WalkClassEx(obj);
        std::vector<FunctionInfo> funcs = Ubel::WalkFunctions(obj);

        for (const auto& f : funcs) {
            if (static_cast<int>(result.entries.size()) >= maxEntries) break;

            AllFunctionEntry entry;
            entry.className     = ci.Name;
            entry.classAddr     = obj;
            entry.superName     = ci.SuperName;
            entry.classPath     = classPath;
            entry.funcName      = f.name;
            entry.funcAddr      = f.address;
            entry.functionFlags = f.functionFlags;
            entry.numParms      = f.numParms;
            entry.parmsSize     = f.parmsSize;
            result.entries.push_back(std::move(entry));
            result.totalFunctions++;
        }
    }

    Sein::Info("PIPE:list",
        "EnumerateAllFunctions: %d entries from %d classes "
        "(gameOnly=%d, scanned %d objects, total funcs %d)",
        static_cast<int>(result.entries.size()), result.scannedClasses,
        gameOnly ? 1 : 0, result.scannedObjects, result.totalFunctions);
    return result;
}

// --- FindPropertyXrefs: Kismet bytecode static cross-reference (Path 1) ---
//
// Walk GObjects; for every UFunction, read UStruct::Script and byte-scan the
// bytecode for the 8-byte little-endian `propAddr`. The variable-access opcodes
// embed the live FProperty* directly, so any function that references the field
// contains its pointer in the script buffer. Parallelised over GObjects index
// ranges via ParallelGObjectsScan (Ubel name/outer caches are mutex-guarded).
PropertyXrefResult FindPropertyXrefs(uintptr_t propAddr, bool gameOnly,
                                     int32_t maxResults) {
    PropertyXrefResult out;
    if (!propAddr || !s_arrayAddr) return out;
    if (maxResults <= 0) maxResults = 200;

    int32_t count = GetCount();
    if (count <= 0) return out;
    out.stats.objectsTotal = count;

    LOG_INFO("FindPropertyXrefs: scanning %d objects for xrefs to FProperty 0x%llX (gameOnly=%d)",
             count, static_cast<unsigned long long>(propAddr), gameOnly ? 1 : 0);

    // Target pointer as 8 little-endian bytes for the memcmp window.
    uint8_t needle[8];
    memcpy(needle, &propAddr, sizeof(needle));

    constexpr int kDeadlineMs = 30000;
    auto t0 = std::chrono::steady_clock::now();

    struct ThreadResult {
        std::vector<PropertyXref> xrefs;
        int32_t funcsScanned    = 0;
        int32_t funcsWithScript = 0;
    };

    auto scan = ParallelGObjectsScan<ThreadResult>(count,
        [&](ThreadResult& tr, int32_t beginIdx, int32_t endIdx,
            std::atomic<bool>& deadlineHit) {

        std::vector<uint8_t> buf;   // reused across functions (keeps capacity)

        for (int32_t i = beginIdx;
             i < endIdx && static_cast<int>(tr.xrefs.size()) < maxResults; ++i) {
            // Chunk-relative stride so the deadline / sibling check fires from
            // this chunk's first iteration (mirrors FindReferencesToUObject).
            if (((i - beginIdx) & 0x3FF) == 0) {
                if (deadlineHit.load(std::memory_order_relaxed)) return;
                auto dt = std::chrono::duration_cast<std::chrono::milliseconds>(
                              std::chrono::steady_clock::now() - t0).count();
                if (dt > kDeadlineMs) {
                    deadlineHit.store(true, std::memory_order_relaxed);
                    return;
                }
            }

            uintptr_t obj = GetByIndex(i);
            if (!obj) continue;

            // UFunction? Its UClass name is "Function".
            uintptr_t cls = 0;
            if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;
            uint32_t clsNameIdx = 0;
            if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, clsNameIdx)) continue;
            if (Serie::GetString(clsNameIdx) != "Function") continue;
            tr.funcsScanned++;

            // Read UStruct::Script { Data*, Num, Max }.
            uintptr_t scriptData = 0; int32_t scriptNum = 0;
            Macht::ReadSafe(obj + DynOff::USTRUCT_SCRIPT,        scriptData);
            Macht::ReadSafe(obj + DynOff::USTRUCT_SCRIPT + 0x08, scriptNum);
            if (!scriptData || scriptNum <= 0 || scriptNum > (1 << 22)) continue;  // sanity guard
            tr.funcsWithScript++;

            // Bulk-read bytecode, byte-scan for the (UNALIGNED) pointer value.
            buf.resize(static_cast<size_t>(scriptNum));
            if (!Macht::ReadBytesSafe(scriptData, buf.data(), static_cast<size_t>(scriptNum)))
                continue;

            int32_t occ = 0;
            uint8_t precByte = 0xFF;
            for (int32_t p = 0; p + 8 <= scriptNum; ++p) {
                if (memcmp(buf.data() + p, needle, 8) == 0) {
                    if (occ == 0 && p > 0) precByte = buf[p - 1];  // classify first hit
                    occ++;
                }
            }
            if (occ == 0) continue;

            // Owning class = UFunction's Outer. Apply gameOnly on its path.
            uintptr_t owner = Ubel::GetOuter(obj);
            if (gameOnly && owner && IsEnginePackage(Ubel::GetFullName(owner))) continue;

            PropertyXref x;
            x.funcAddr       = obj;
            x.funcName       = Ubel::GetName(obj);
            x.funcFullName   = Ubel::GetFullName(obj);
            x.ownerClassAddr = owner;
            x.ownerClassName = owner ? Ubel::GetName(owner) : "";
            x.occurrences    = occ;
            x.kind = (precByte == 0x01) ? "instance"
                   : (precByte == 0x00) ? "local" : "ref";
            tr.xrefs.push_back(std::move(x));
        }
    });

    out.xrefs = ConcatTruncate(scan.perThread, &ThreadResult::xrefs, maxResults);
    for (auto& tr : scan.perThread) {
        out.stats.functionsScanned    += tr.funcsScanned;
        out.stats.functionsWithScript += tr.funcsWithScript;
    }
    out.stats.deadlineHit = scan.deadlineHit;
    out.stats.durationMs  = std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - t0).count();

    LOG_INFO("FindPropertyXrefs: %zu xrefs (scanned %d functions, %d with script, %lldms%s)",
             out.xrefs.size(), out.stats.functionsScanned, out.stats.functionsWithScript,
             static_cast<long long>(out.stats.durationMs),
             out.stats.deadlineHit ? ", DEADLINE" : "");
    return out;
}

SparseDelegateResult WalkSparseDelegateBindings(uintptr_t ownerObj,
                                                 const std::string& fieldName,
                                                 int32_t maxBindings)
{
    SparseDelegateResult result{};
    if (!ownerObj || fieldName.empty()) return result;

    // Version gate: walker is correct only for UE 5.0+ (raw-pointer outer key).
    // UE 4.23-4.27 used FObjectKey { FWeakObjectPtr, int32 } which has
    // different stride and key-comparison semantics.
    if (::g_cachedUEVersion != 0 && ::g_cachedUEVersion < 500) {
        result.supported = false;
        return result;
    }

    uintptr_t storage = Genau::FindSparseDelegateStorage();
    if (!storage) return result;  // resolved=false

    TMapHeader outerHdr{};
    if (!ReadTMapHeader(storage, outerHdr)) return result;
    if (!outerHdr.arrayData || outerHdr.arrayNum == 0) {
        result.resolved = true;  // empty storage is a valid state
        return result;
    }

    constexpr int32_t kOuterStride = 0x60;
    constexpr int32_t kOuterValueOffset = 0x08;  // TPair value (inner TMap) starts after key

    // Phase 1: linear scan outer slots for matching owner key.
    uintptr_t innerMapAddr = 0;
    for (int32_t i = 0; i < outerHdr.arrayNum; ++i) {
        if (!TMapBitSet(outerHdr.bitArrayBase, i)) continue;  // freed slot
        uintptr_t slot = outerHdr.arrayData + static_cast<uintptr_t>(i) * kOuterStride;
        uintptr_t key = 0;
        if (!Macht::ReadSafe(slot, key)) continue;
        if (key == ownerObj) {
            innerMapAddr = slot + kOuterValueOffset;
            break;
        }
    }

    result.resolved = true;
    if (!innerMapAddr) return result;  // owner not in storage
    result.ownerFound = true;

    // Phase 2: linear scan inner TMap for matching FName key.
    TMapHeader innerHdr{};
    if (!ReadTMapHeader(innerMapAddr, innerHdr)) return result;
    if (!innerHdr.arrayData || innerHdr.arrayNum == 0) return result;

    int32_t fnameSize = DynOff::bCasePreservingName ? 0x10 : 0x08;
    int32_t innerStride = (fnameSize == 0x10 ? 0x28 : 0x20);
    int32_t sharedPtrOffset = fnameSize;  // TPair: FName at +0, TSharedPtr at +fnameSize

    uintptr_t sharedPtrAddr = 0;
    for (int32_t i = 0; i < innerHdr.arrayNum; ++i) {
        if (!TMapBitSet(innerHdr.bitArrayBase, i)) continue;
        uintptr_t slot = innerHdr.arrayData + static_cast<uintptr_t>(i) * innerStride;
        int32_t comp = 0;
        if (!Macht::ReadSafe(slot, comp)) continue;
        std::string keyStr = Serie::GetString(comp);
        if (keyStr == fieldName) {
            sharedPtrAddr = slot + sharedPtrOffset;
            break;
        }
    }
    if (!sharedPtrAddr) return result;  // FName not in inner map
    result.nameFound = true;

    // Phase 3: deref TSharedPtr, walk InvocationList: TArray<FScriptDelegate>.
    uintptr_t mcdAddr = 0;
    if (!Macht::ReadSafe(sharedPtrAddr, mcdAddr) || !mcdAddr) return result;

    // FMulticastScriptDelegate { TArray<FScriptDelegate> InvocationList; }
    uintptr_t invData = 0;
    int32_t   invNum  = 0;
    Macht::ReadSafe(mcdAddr + 0x00, invData);
    Macht::ReadSafe(mcdAddr + 0x08, invNum);
    if (invNum < 0 || invNum > 4096) invNum = 0;

    int32_t scriptDelegateSize = 8 + fnameSize;  // FWeakObjectPtr + FName
    int32_t readMax = std::min(invNum, maxBindings);
    result.bindings.reserve(readMax);
    for (int32_t i = 0; invData && i < readMax; ++i) {
        uintptr_t elemAddr = invData + static_cast<uintptr_t>(i) * scriptDelegateSize;
        SparseDelegateBinding b{};
        Macht::ReadSafe(elemAddr,     b.objectIndex);
        Macht::ReadSafe(elemAddr + 4, b.serialNumber);

        b.targetObj = Ubel::ResolveWeakObjectPtr(b.objectIndex, b.serialNumber);
        if (b.targetObj) {
            b.targetName = Ubel::GetName(b.targetObj);
            uintptr_t cls = Ubel::GetClass(b.targetObj);
            if (cls) b.targetClassName = Ubel::GetName(cls);
        }

        // FName at +8 (FWeakObjectPtr is always 8 bytes regardless of FName size)
        int32_t funcComp = 0;
        Macht::ReadSafe(elemAddr + 8, funcComp);
        b.functionName = Serie::GetString(funcComp);

        result.bindings.push_back(std::move(b));
    }

    return result;
}

// === Value Search (CE-style First Scan / Next Scan workflow) ===

ValueScanResult ScanForValue(
    ValueScan::DataType dt,
    ValueScan::ScanType st,
    const uint8_t*      targetBytes,
    const uint8_t*      target2Bytes,
    bool                gameOnly,
    int32_t             maxResults,
    double              tolerance,
    const std::string&  targetString,
    bool                caseSensitive,
    const ValueScan::NumericTargetSet* multiTargets,
    const ValueScan::NumericTargetSet* multiTargets2)
{
    ValueScanResult result;
    auto t0 = std::chrono::steady_clock::now();
    constexpr auto kDeadline = std::chrono::seconds(15);

    const bool isString = ValueScan::IsStringDataType(dt);
    const bool isVector = ValueScan::IsVectorDataType(dt);
    const bool isMulti  = ValueScan::IsMultiNumericDataType(dt);
    const size_t dtSize = ValueScan::SizeOf(dt);

    // Validate inputs per type family.
    if (isString) {
        // String scans take the user's needle on targetString; the byte
        // buffers are unused. Caller must pass a non-empty target for
        // targeted predicates (substring matchers + Exact); Changed /
        // Unchanged use the candidate's prevStr.
        if (!ValueScan::IsPrevValueScanType(st) && targetString.empty()) return result;
    } else if (isMulti) {
        // Multi-numeric meta scan: the pre-parsed per-width target set
        // replaces targetBytes. First scan requires it (prev-value scan
        // types never reach here — rejected below).
        if (!multiTargets || multiTargets->entries.empty()) return result;
        if (st == ValueScan::ScanType::Between
            && (!multiTargets2 || multiTargets2->entries.empty())) return result;
    } else {
        if (dtSize == 0 || !targetBytes) return result;
        if (st == ValueScan::ScanType::Between && !target2Bytes) return result;
    }
    // Prev-value scan types have no meaning on a first scan -- caller (pipe
    // handler) is responsible for rejecting these, but be defensive.
    if (ValueScan::IsPrevValueScanType(st)) return result;

    const auto& acceptedTypes = ValueScan::PropertyTypeNames(dt);
    // Vector types match by StructProperty + inner struct name (e.g.
    // "Vector", "Vector3f"). Empty for non-vector dt so the inner-name
    // check is skipped.
    const auto& acceptedStructNames = ValueScan::VectorStructNames(dt);

    // Per-class field index. classAddr -> filtered subset of FieldInfo
    // that match the requested DataType. Built lazily on first
    // encounter; reused across all instances of that class.
    //
    // Phase 2C extends this with an "isArray" flag — when set, the
    // ScanField represents an ArrayProperty whose Inner matches the
    // requested DataType. The per-instance loop reads the TArray
    // header (Data ptr, Num, Max) and emits ONE candidate per matching
    // element. elemStride captures the per-element size in bytes;
    // size still refers to the field-level size (16B TArray header).
    struct ScanField {
        int32_t     offset;
        int32_t     size;
        std::string name;
        std::string typeName;
        uint8_t     boolFieldMask;
        bool        isArray      = false;
        int32_t     elemStride   = 0;
        std::string elemTypeName;   // Inner type name when isArray (e.g. "IntProperty")
    };
    struct ScanClassInfo {
        std::string             className;
        std::string             classPath;
        bool                    gameClass = false;   // !IsEnginePackage(classPath)
        std::vector<ScanField>  fields;
    };
    // DefKey caches FindDefiningClass results per (classAddr, fieldOffset)
    // so a hot scan over many instances of the same class doesn't re-walk
    // the SuperStruct chain on every candidate emission. (Defined here at
    // function scope so the per-thread worker below can hold one locally.)
    struct DefKey {
        uintptr_t classAddr;
        int32_t   offset;
        bool operator==(const DefKey& o) const {
            return classAddr == o.classAddr && offset == o.offset;
        }
    };
    struct DefKeyHash {
        size_t operator()(const DefKey& k) const {
            return std::hash<uintptr_t>{}(k.classAddr)
                 ^ (std::hash<int32_t>{}(k.offset) << 1);
        }
    };

    const int32_t count = GetCount();

    LOG_INFO("ValueScan: First Scan dt=%s st=%d (target %zuB, gameOnly=%d, max=%d) over %d objects",
             ValueScan::NameOf(dt), static_cast<int>(st), dtSize,
             gameOnly ? 1 : 0, maxResults, count);

    // Per-thread output of the parallel GObjects walk. Each thread owns its
    // own caches + candidate buffer (lock-free hot path); results merge in
    // ascending tid order so the global candidate list stays ascending by
    // object index — identical ordering to the old serial walk.
    struct ThreadResult {
        std::vector<ValueScan::Candidate> candidates;
        int32_t                           scannedObjects = 0;
        std::unordered_set<uintptr_t>     classesWithFields;
    };

    auto scan = ParallelGObjectsScan<ThreadResult>(count,
        [&](ThreadResult& tr, int32_t beginIdx, int32_t endIdx,
            std::atomic<bool>& deadlineHit) {

        // Thread-local per-class field index. classAddr -> filtered subset of
        // FieldInfo matching the requested DataType. Built lazily on first
        // encounter; reused across all instances of that class within this
        // thread's chunk. (Threads may redundantly build the same class index;
        // that cost is negligible vs. the GObjects walk and avoids any locking.)
        std::unordered_map<uintptr_t, ScanClassInfo> classCache;

    // Recursive struct expansion: walks a UStruct's FProperty chain and
    // emits ScanField entries for every leaf property matching the target
    // DataType, including fields nested inside StructProperty members.
    //
    // Critical for GAS / Gameplay Ability System games: the most common
    // pattern is `UAttributeSet -> FGameplayAttributeData MaximumHealth ->
    // float BaseValue / float CurrentValue`. Without recursion the scan
    // sees only the outer StructProperty (which isn't a leaf type) and
    // returns 0 candidates -- the original 2026-05-26 TQ2 repro that
    // motivated this fix. Same applies to FVector / FRotator / FTransform
    // members of any UObject.
    //
    // Cycle / pathological-depth guards:
    //   - kMaxDepth = 4. Real UE structs rarely nest beyond 2-3 levels;
    //     beyond 4 we're either in a recursive type loop or pathological
    //     data. The cap bounds worst-case CPU per class.
    //   - visited set per call protects against accidental cycles
    //     (FStructProperty's Struct pointer pointing to a struct that
    //     transitively re-references itself, e.g. linked-list nodes
    //     declared as USTRUCT with a self-typed StructProperty).
    auto expandFields = [&](auto& self,
                            uintptr_t structAddr,
                            int32_t   baseOffset,
                            const std::string& namePrefix,
                            std::vector<ScanField>& out,
                            std::unordered_set<uintptr_t>& visited,
                            int depth) -> void {
        constexpr int kMaxDepth = 4;
        if (depth > kMaxDepth) return;
        if (!visited.insert(structAddr).second) return;  // cycle

        // Use WalkClassEx at every depth so BoolProperty FieldMask is
        // populated for nested bitfield bools (WalkClass alone covers it
        // on the UE4 UProperty path only; the UE5 FProperty path needs
        // the WalkClassEx pass). The extra metadata reads we don't use
        // are cheap relative to the GObjects walk itself.
        ClassInfo ci = Ubel::WalkClassEx(structAddr);
        for (const auto& f : ci.Fields) {
            // Leaf-type match: emit a ScanField at the cumulative offset.
            bool accepted = false;
            for (const auto& t : acceptedTypes) {
                if (f.TypeName == t) { accepted = true; break; }
            }

            // Vector data types additionally require the inner struct
            // name to match (e.g. "Vector" / "Vector3f"). This both
            // filters out unrelated StructProperty fields (which are
            // numerous) and avoids the cost of reading 12 bytes from
            // every non-vector struct on every scan.
            if (accepted && !acceptedStructNames.empty() && f.TypeName == "StructProperty") {
                bool nameMatch = false;
                for (const auto& name : acceptedStructNames) {
                    if (f.structType == name) { nameMatch = true; break; }
                }
                accepted = nameMatch;
            }

            if (accepted) {
                ScanField sf;
                sf.offset        = baseOffset + f.Offset;
                sf.size          = f.Size;
                sf.name          = namePrefix.empty() ? f.Name : (namePrefix + "." + f.Name);
                sf.typeName      = f.TypeName;
                sf.boolFieldMask = f.boolFieldMask;
                out.push_back(std::move(sf));
                continue;
            }

            // Phase 2C: TArray<T> container scan. When the inner
            // FProperty's type matches the requested DataType, emit a
            // ScanField marked isArray=true with the per-element
            // stride. Per-instance loop branches on isArray to walk
            // the TArray buffer.
            //
            // Vector inner types additionally require innerStructType
            // to match the accepted struct names (mirrors the leaf
            // StructProperty filter).
            if (f.TypeName == "ArrayProperty" && !f.innerType.empty()) {
                bool innerAccepted = false;
                for (const auto& t : acceptedTypes) {
                    // Inner matches a leaf-property type the scan
                    // wants, but vectors also need StructProperty inner
                    // (and the struct name check below).
                    if (f.innerType == t) {
                        innerAccepted = true;
                        break;
                    }
                }
                if (innerAccepted && !acceptedStructNames.empty()
                    && f.innerType == "StructProperty") {
                    bool nameMatch = false;
                    for (const auto& name : acceptedStructNames) {
                        if (f.innerStructType == name) { nameMatch = true; break; }
                    }
                    innerAccepted = nameMatch;
                }
                if (innerAccepted) {
                    int32_t stride = Ubel::GetArrayInnerElemSize(f.Address);
                    // Skip arrays whose inner-element size couldn't be
                    // resolved (rare; defensive). Without stride we
                    // can't iterate safely.
                    if (stride > 0) {
                        ScanField sf;
                        sf.offset        = baseOffset + f.Offset;
                        sf.size          = f.Size;
                        sf.name          = namePrefix.empty()
                            ? f.Name : (namePrefix + "." + f.Name);
                        sf.typeName      = "ArrayProperty";
                        sf.boolFieldMask = 0xFF;
                        sf.isArray       = true;
                        sf.elemStride    = stride;
                        sf.elemTypeName  = f.innerType;
                        out.push_back(std::move(sf));
                        continue;
                    }
                }
            }

            // StructProperty: resolve the inner UScriptStruct via
            // FStructProperty::Struct (FField + FSTRUCTPROP_STRUCT) and
            // recurse with the cumulative offset + dotted name prefix.
            // Map / Set / Optional containers are intentionally NOT
            // recursed -- they're separate milestones.
            //
            // For vector data types, skip recursion -- we only want
            // leaves whose own type IS the vector struct, not nested
            // structs that happen to contain a vector. This matches the
            // CE-style cheat workflow (find an FVector at a known
            // location, not all FVectors-anywhere-inside).
            if (acceptedStructNames.empty()
                && f.TypeName == "StructProperty" && f.Address) {
                uintptr_t nested = 0;
                if (Macht::ReadSafe(f.Address + DynOff::FSTRUCTPROP_STRUCT, nested) && nested) {
                    std::string childPrefix = namePrefix.empty()
                        ? f.Name : (namePrefix + "." + f.Name);
                    self(self, nested, baseOffset + f.Offset, childPrefix, out, visited, depth + 1);
                }
            }
        }
    };

    auto buildClassIndex = [&](uintptr_t classAddr) -> ScanClassInfo* {
        auto it = classCache.find(classAddr);
        if (it != classCache.end()) return &it->second;

        ScanClassInfo sci;
        // Two passes: first WalkClassEx for the class metadata (Name +
        // FullPath are populated by Ubel::WalkClass already, but
        // WalkClassEx also populates structType / inner / enum metadata
        // we want available). Then expandFields walks the property chain
        // recursively for ScanField emission.
        ClassInfo ci = Ubel::WalkClassEx(classAddr);
        sci.className = ci.Name;
        sci.classPath = ci.FullPath;
        sci.gameClass = !IsEnginePackage(ci.FullPath);

        std::unordered_set<uintptr_t> visited;
        expandFields(expandFields, classAddr, /*baseOffset=*/0,
                     /*namePrefix=*/"", sci.fields, visited, /*depth=*/0);

        auto inserted = classCache.emplace(classAddr, std::move(sci));
        return &inserted.first->second;
    };

        // Thread-local FindDefiningClass result cache (see DefKey above).
        std::unordered_map<DefKey, std::string, DefKeyHash> definingNameCache;

        // Multi-numeric meta resolver. For NumericNoByte scans, resolve a
        // field/element's own concrete DataType from its property type
        // name and point `tgt`/`tgt2` at the matching pre-parsed target.
        // Returns false (skip the field) when the type isn't a numeric
        // member or the value can't fit that width. Only reached on the
        // targeted first-scan path (prev-value scan types never get here).
        auto multiResolve = [&](const std::string& propTypeName,
                                ValueScan::DataType& memberDt,
                                const uint8_t*&      tgt,
                                const uint8_t*&      tgt2) -> bool {
            if (!ValueScan::TryDataTypeFromPropertyTypeName(propTypeName, memberDt)) return false;
            const uint8_t* e = multiTargets ? multiTargets->Find(memberDt) : nullptr;
            if (!e) return false;
            tgt  = e;
            tgt2 = nullptr;
            if (st == ValueScan::ScanType::Between) {
                const uint8_t* e2 = multiTargets2 ? multiTargets2->Find(memberDt) : nullptr;
                if (!e2) return false;
                tgt2 = e2;
            }
            return true;
        };

        for (int32_t i = beginIdx; i < endIdx; ++i) {
        // Periodic deadline + max-results check (every 4K objects keeps
        // the chrono cost negligible while still bounding worst-case
        // wall time to ~16ms past the deadline). maxResults is a per-thread
        // local cap: each thread keeps at most maxResults of its own
        // (ascending) candidates, and the ascending-order merge truncates to
        // maxResults — yielding exactly the lowest-index matches the serial
        // walk would have stopped at.
        // Chunk-relative stride so the check fires on this chunk's first
        // iteration (i == beginIdx) and every 4096 after, regardless of where
        // beginIdx lands — otherwise a non-4096-aligned chunk start would delay
        // the deadline + cross-thread deadlineHit check by up to 4095 objects.
        if (((i - beginIdx) & 0xFFF) == 0) {
            if (deadlineHit.load(std::memory_order_relaxed)) return;
            if (std::chrono::steady_clock::now() - t0 > kDeadline) {
                deadlineHit.store(true, std::memory_order_relaxed);
                return;
            }
            if (static_cast<int32_t>(tr.candidates.size()) >= maxResults) return;
        }

        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;
        tr.scannedObjects++;

        uintptr_t cls = 0;
        if (!Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_CLASS, cls) || !cls) continue;

        // Skip class-meta objects -- we want instances + CDOs, not the
        // UClass entries themselves. (A UClass's own class is "Class" /
        // "BlueprintGeneratedClass" / etc.; an instance's class is the
        // game class.)
        uint32_t metaIdx = 0;
        if (!Macht::ReadSafe(cls + Grimoire::OFF_UOBJECT_NAME, metaIdx)) continue;
        std::string metaName = Serie::GetString(metaIdx);
        if (IsClassLikeMeta(metaName)) continue;

        ScanClassInfo* sci = buildClassIndex(cls);
        if (!sci || sci->fields.empty()) continue;
        if (gameOnly && !sci->gameClass) continue;

        // Defer instance-name resolution until we know we have a match;
        // FName lookup is cheap but billions of unused calls add up.
        bool   gotInstanceName = false;
        std::string instanceName;

        // Helper: emit one candidate from per-hit data. Used by both
        // direct-field and per-array-element paths. Captures the
        // shared metadata + defining-class cache lookup. Inlined as a
        // lambda to avoid an extra parameter cascade.
        auto emitCandidate = [&](uintptr_t valueAddr,
                                 int32_t   fieldOffsetForCand,
                                 const std::string& displayName,
                                 const std::string& fieldType,
                                 uint8_t   boolMask,
                                 const uint8_t* rawBytes,
                                 size_t   rawByteCount,
                                 const std::string* strValue) {
            if (!gotInstanceName) {
                instanceName    = Ubel::GetName(obj);
                gotInstanceName = true;
            }
            ValueScan::Candidate cand;
            cand.addr          = valueAddr;
            cand.instanceAddr  = obj;
            cand.instanceIndex = i;
            cand.fieldOffset   = fieldOffsetForCand;
            if (strValue) {
                cand.prevStr = *strValue;
            } else if (rawBytes && rawByteCount > 0) {
                std::memcpy(cand.prevValue, rawBytes, rawByteCount);
            }
            cand.instanceName  = instanceName;
            cand.className     = sci->className;
            cand.fieldName     = displayName;
            cand.fieldType     = fieldType;
            cand.boolFieldMask = boolMask;

            DefKey dk{ cls, fieldOffsetForCand };
            auto dit = definingNameCache.find(dk);
            if (dit != definingNameCache.end()) {
                cand.definingClassName = dit->second;
            } else {
                uintptr_t defAddr = FindDefiningClass(cls, fieldOffsetForCand);
                std::string defName = (defAddr && defAddr != cls)
                    ? Ubel::GetName(defAddr) : sci->className;
                definingNameCache.emplace(dk, defName);
                cand.definingClassName = std::move(defName);
            }
            tr.candidates.push_back(std::move(cand));
        };

        for (const auto& sf : sci->fields) {
            if (static_cast<int32_t>(tr.candidates.size()) >= maxResults) break;

            // Phase 2C: TArray<T> path. Read the TArray header (Data,
            // Num, Max), defensively validate, then iterate elements
            // and emit one candidate per match.
            if (sf.isArray) {
                uintptr_t arrayDataPtr = 0;
                int32_t   arrayNum     = 0;
                int32_t   arrayMax     = 0;
                if (!Macht::ReadSafe(obj + sf.offset, arrayDataPtr)) continue;
                if (!Macht::ReadSafe(obj + sf.offset + 8, arrayNum)) continue;
                if (!Macht::ReadSafe(obj + sf.offset + 12, arrayMax)) continue;

                // Safety circuit-breakers (memory project_value_search_caveats):
                //   - Negative or absurdly-large Num is a corrupted /
                //     freed-memory marker; skip with a single LOG_WARN
                //     so we surface pathological data without spamming.
                //   - Max must be >= Num for a valid TArray; mismatch
                //     signals corruption (or OptionalProperty being
                //     misread as TArray).
                //   - Empty array (Num == 0) is fine, just skip the
                //     iteration; Data ptr may be null in that case.
                constexpr int32_t kMaxElementsPerArray = 10'000'000;
                if (arrayNum < 0 || arrayNum > kMaxElementsPerArray) {
                    LOG_WARN("ValueScan: skipping TArray with Num=%d on field '%s' at 0x%llX (instance 0x%llX)",
                             arrayNum, sf.name.c_str(),
                             (unsigned long long)(obj + sf.offset),
                             (unsigned long long)obj);
                    continue;
                }
                if (arrayNum == 0) continue;
                if (arrayMax < arrayNum) continue;
                if (!arrayDataPtr) continue;

                for (int32_t idx = 0; idx < arrayNum; ++idx) {
                    if (static_cast<int32_t>(tr.candidates.size()) >= maxResults) break;

                    uintptr_t   elemAddr = arrayDataPtr + static_cast<uintptr_t>(idx) * sf.elemStride;
                    uint8_t     readBuf[16] = {};
                    std::string readStr;

                    ValueScan::DataType elemDt = dt;
                    size_t              elemReadSize = dtSize;
                    if (isString) {
                        if (dt == ValueScan::DataType::FString) {
                            readStr = Ubel::ReadFStringAt(elemAddr, 0);
                        } else if (dt == ValueScan::DataType::FName) {
                            readStr = Ubel::ReadFNameAt(elemAddr, 0);
                        } else {
                            readStr = Ubel::ReadFTextStringAt(elemAddr, 0);
                        }
                        if (!ValueScan::CompareStringPredicate(st, readStr, targetString, caseSensitive)) continue;
                    } else if (isVector) {
                        if (!Macht::ReadBytesSafe(elemAddr, readBuf, 12)) continue;
                        if (!ValueScan::CompareVectorPredicate(st, readBuf, targetBytes, target2Bytes, tolerance)) continue;
                    } else if (isMulti) {
                        // Resolve the array's inner element width + target.
                        const uint8_t* mtgt = nullptr;
                        const uint8_t* mtgt2 = nullptr;
                        if (!multiResolve(sf.elemTypeName, elemDt, mtgt, mtgt2)) continue;
                        elemReadSize = ValueScan::SizeOf(elemDt);
                        if (!Macht::ReadBytesSafe(elemAddr, readBuf, elemReadSize)) continue;
                        if (!ValueScan::ComparePredicate(elemDt, st, readBuf, mtgt, mtgt2, tolerance)) continue;
                    } else {
                        if (!Macht::ReadBytesSafe(elemAddr, readBuf, dtSize)) continue;
                        // Array elements never share a bitfield byte
                        // (TArray<bool> is stored unpacked), so the
                        // boolFieldMask = 0xFF path applies.
                        if (!ValueScan::ComparePredicate(dt, st, readBuf, targetBytes, target2Bytes, tolerance)) continue;
                    }

                    // Display name "Field[N]" so the user sees which
                    // element matched. fieldOffset stays the
                    // class-level offset (helps with defining-class
                    // lookup); the candidate's addr is the element
                    // address.
                    char idxStr[24];
                    std::snprintf(idxStr, sizeof(idxStr), "[%d]", idx);
                    std::string elemName = sf.name + idxStr;

                    if (isString) {
                        emitCandidate(elemAddr, sf.offset, elemName,
                                      sf.elemTypeName, 0xFF,
                                      nullptr, 0, &readStr);
                    } else if (isVector) {
                        emitCandidate(elemAddr, sf.offset, elemName,
                                      sf.elemTypeName, 0xFF,
                                      readBuf, 12, nullptr);
                    } else {
                        // elemReadSize == dtSize for fixed-width numeric
                        // scans; for multi-numeric it's the resolved
                        // per-element width (dtSize is 0 in that mode).
                        emitCandidate(elemAddr, sf.offset, elemName,
                                      sf.elemTypeName, 0xFF,
                                      readBuf, elemReadSize, nullptr);
                    }
                }
                continue;
            }

            uintptr_t valueAddr = obj + sf.offset;
            uint8_t  readBuf[16] = {};
            std::string readStr;

            if (isString) {
                // FString / FName / FText -- resolve to UTF-8 via Ubel
                // helpers. Empty resolution returns "" and we still test
                // it (target may be "" for Exact-empty searches).
                if (dt == ValueScan::DataType::FString) {
                    readStr = Ubel::ReadFStringAt(obj, sf.offset);
                } else if (dt == ValueScan::DataType::FName) {
                    readStr = Ubel::ReadFNameAt(obj, sf.offset);
                } else {
                    readStr = Ubel::ReadFTextStringAt(obj, sf.offset);
                }
                if (!ValueScan::CompareStringPredicate(st, readStr, targetString, caseSensitive)) continue;
                emitCandidate(valueAddr, sf.offset, sf.name, sf.typeName,
                              sf.boolFieldMask, nullptr, 0, &readStr);
                continue;
            }
            if (isVector) {
                // Vector / Rotator: read 12 bytes (3 floats) from the
                // struct start. Caller's targetBytes already encodes
                // the 12-byte (X,Y,Z) layout.
                if (!Macht::ReadBytesSafe(valueAddr, readBuf, 12)) continue;
                if (!ValueScan::CompareVectorPredicate(st, readBuf, targetBytes, target2Bytes, tolerance)) continue;
                emitCandidate(valueAddr, sf.offset, sf.name, sf.typeName,
                              sf.boolFieldMask, readBuf, 12, nullptr);
                continue;
            }

            if (isMulti) {
                // Resolve this field's own width + matching target; skip
                // if the value can't fit it. Compare with the per-field
                // DataType so an int field compares as int, a float field
                // as float — no byte-reinterpret.
                ValueScan::DataType memberDt;
                const uint8_t* mtgt = nullptr;
                const uint8_t* mtgt2 = nullptr;
                if (!multiResolve(sf.typeName, memberDt, mtgt, mtgt2)) continue;
                size_t msz = ValueScan::SizeOf(memberDt);
                if (!Macht::ReadBytesSafe(valueAddr, readBuf, msz)) continue;
                if (!ValueScan::ComparePredicate(memberDt, st, readBuf, mtgt, mtgt2, tolerance)) continue;
                emitCandidate(valueAddr, sf.offset, sf.name, sf.typeName,
                              sf.boolFieldMask, readBuf, msz, nullptr);
                continue;
            }

            if (!Macht::ReadBytesSafe(valueAddr, readBuf, dtSize)) continue;

            // BoolProperty bitfield normalisation. The bytes we
            // store as prevValue must reflect the LOGICAL bool
            // (0/1), not the raw shared byte, so Changed /
            // Unchanged refines compare on a stable value even
            // when sibling bits flip.
            if (dt == ValueScan::DataType::Bool
                && sf.boolFieldMask != 0 && sf.boolFieldMask != 0xFF) {
                readBuf[0] = ((readBuf[0] & sf.boolFieldMask) != 0) ? 1 : 0;
            }

            if (!ValueScan::ComparePredicate(dt, st, readBuf, targetBytes, target2Bytes, tolerance)) continue;
            emitCandidate(valueAddr, sf.offset, sf.name, sf.typeName,
                          sf.boolFieldMask, readBuf, dtSize, nullptr);
        }
    }

        // Tally classes that contributed at least one matching field so the
        // merge can report a deduplicated global class count.
        for (const auto& kv : classCache) {
            if (!kv.second.fields.empty()) tr.classesWithFields.insert(kv.first);
        }
    });  // ParallelGObjectsScan

    // Fold per-thread stats; candidate vectors concat in ascending tid order
    // (ConcatTruncate) → serial "scan ascending, stop at maxResults" set.
    std::unordered_set<uintptr_t> classesWithFields;
    for (auto& tr : scan.perThread) {
        result.stats.scannedObjects += tr.scannedObjects;
        classesWithFields.insert(tr.classesWithFields.begin(),
                                 tr.classesWithFields.end());
    }
    result.candidates = ConcatTruncate(scan.perThread, &ThreadResult::candidates, maxResults);
    result.stats.scannedClasses = static_cast<int32_t>(classesWithFields.size());
    result.stats.deadlineHit    = scan.deadlineHit;

    auto dtms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - t0).count();
    result.stats.durationMs = static_cast<int64_t>(dtms);

    LOG_INFO("ValueScan: First Scan complete -- %d candidates in %lld ms (%d objects, %d classes with matching fields, %d thread(s)%s)",
             static_cast<int>(result.candidates.size()),
             static_cast<long long>(dtms),
             result.stats.scannedObjects,
             static_cast<int>(classesWithFields.size()), scan.nthreads,
             result.stats.deadlineHit ? ", DEADLINE HIT" : "");
    return result;
}

ValueScanStats RefineCandidates(
    ValueScan::DataType                dt,
    ValueScan::ScanType                st,
    const uint8_t*                     targetBytes,
    const uint8_t*                     target2Bytes,
    std::vector<ValueScan::Candidate>& candidates,
    double                             tolerance,
    const std::string&                 targetString,
    bool                               caseSensitive,
    const ValueScan::NumericTargetSet* multiTargets,
    const ValueScan::NumericTargetSet* multiTargets2)
{
    ValueScanStats stats;
    auto t0 = std::chrono::steady_clock::now();

    const bool isString = ValueScan::IsStringDataType(dt);
    const bool isVector = ValueScan::IsVectorDataType(dt);
    const bool isMulti  = ValueScan::IsMultiNumericDataType(dt);
    const size_t dtSize = ValueScan::SizeOf(dt);
    if (!isString && !isMulti && dtSize == 0) return stats;

    const bool usePrev = ValueScan::IsPrevValueScanType(st);
    if (isMulti) {
        // Targeted multi-numeric refine needs the pre-parsed target set;
        // prev-value predicates compare against each candidate's snapshot.
        if (!usePrev && (!multiTargets || multiTargets->entries.empty())) return stats;
        if (!usePrev && st == ValueScan::ScanType::Between
            && (!multiTargets2 || multiTargets2->entries.empty())) return stats;
    } else if (!isString) {
        if (!usePrev && !targetBytes) return stats;
        if (st == ValueScan::ScanType::Between && !target2Bytes) return stats;
    }

    const int32_t initialSize = static_cast<int32_t>(candidates.size());

    std::vector<ValueScan::Candidate> kept;
    kept.reserve(candidates.size());

    for (auto& c : candidates) {
        if (isMulti) {
            // Re-resolve this candidate's own width from its stored
            // fieldType (concrete property type, e.g. "FloatProperty").
            // Targeted predicates compare against the matching target
            // entry; prev-value predicates against the snapshot.
            ValueScan::DataType memberDt;
            if (!ValueScan::TryDataTypeFromPropertyTypeName(c.fieldType, memberDt)) continue;
            size_t msz = ValueScan::SizeOf(memberDt);
            uint8_t readBuf[16] = {};
            if (!Macht::ReadBytesSafe(c.addr, readBuf, msz)) continue;

            const uint8_t* cmpTarget = nullptr;
            const uint8_t* cmp2      = nullptr;
            if (usePrev) {
                cmpTarget = c.prevValue;
            } else {
                cmpTarget = multiTargets ? multiTargets->Find(memberDt) : nullptr;
                if (!cmpTarget) continue;  // value can't fit this width
                if (st == ValueScan::ScanType::Between) {
                    cmp2 = multiTargets2 ? multiTargets2->Find(memberDt) : nullptr;
                    if (!cmp2) continue;
                }
            }
            if (!ValueScan::ComparePredicate(memberDt, st, readBuf, cmpTarget, cmp2, tolerance)) continue;
            std::memcpy(c.prevValue, readBuf, msz);
            kept.push_back(std::move(c));
            continue;
        }

        if (isString) {
            // Re-resolve the string from the candidate's recorded
            // address. c.addr already points at the value (FString
            // header / FName slot / FText payload) — for direct fields
            // this is instanceAddr + fieldOffset; for array elements
            // it's arrayDataPtr + index * stride. Reading from c.addr
            // with offset=0 works uniformly for both.
            //
            // Array reallocation caveat: if the underlying TArray
            // resized between scans, c.addr is stale and the read may
            // fail or return garbage. Macht's SEH-wrapped reads turn
            // bad accesses into safe failures (continue), so we drop
            // the candidate quietly; the user can First-Scan again
            // to refresh.
            std::string cur;
            if (c.fieldType == "StrProperty") {
                cur = Ubel::ReadFStringAt(c.addr, 0);
            } else if (c.fieldType == "NameProperty") {
                cur = Ubel::ReadFNameAt(c.addr, 0);
            } else if (c.fieldType == "TextProperty") {
                cur = Ubel::ReadFTextStringAt(c.addr, 0);
            } else {
                // Shouldn't happen for a string-typed session, but be
                // defensive: a candidate with the wrong fieldType
                // can't be re-read, so drop it.
                continue;
            }

            const std::string& cmpTarget = usePrev ? c.prevStr : targetString;
            if (!ValueScan::CompareStringPredicate(st, cur, cmpTarget, caseSensitive)) continue;

            c.prevStr = std::move(cur);
            kept.push_back(std::move(c));
            continue;
        }

        if (isVector) {
            uint8_t readBuf[16] = {};
            if (!Macht::ReadBytesSafe(c.addr, readBuf, 12)) continue;
            const uint8_t* cmpTarget = usePrev ? c.prevValue : targetBytes;
            if (!ValueScan::CompareVectorPredicate(st, readBuf, cmpTarget, target2Bytes, tolerance)) continue;
            std::memcpy(c.prevValue, readBuf, 12);
            kept.push_back(std::move(c));
            continue;
        }

        uint8_t readBuf[16] = {};
        if (!Macht::ReadBytesSafe(c.addr, readBuf, dtSize)) continue;

        if (dt == ValueScan::DataType::Bool
            && c.boolFieldMask != 0 && c.boolFieldMask != 0xFF) {
            readBuf[0] = ((readBuf[0] & c.boolFieldMask) != 0) ? 1 : 0;
        }

        const uint8_t* cmpTarget = usePrev ? c.prevValue : targetBytes;
        if (!ValueScan::ComparePredicate(dt, st, readBuf, cmpTarget, target2Bytes, tolerance)) continue;

        std::memcpy(c.prevValue, readBuf, dtSize);
        kept.push_back(std::move(c));
    }

    stats.scannedObjects = initialSize;
    candidates           = std::move(kept);

    auto dtms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - t0).count();
    stats.durationMs = static_cast<int64_t>(dtms);

    LOG_INFO("ValueScan: Refine st=%d (usePrev=%d): %d -> %d candidates in %lld ms",
             static_cast<int>(st), usePrev ? 1 : 0,
             initialSize, static_cast<int>(candidates.size()),
             static_cast<long long>(dtms));
    return stats;
}

// ------------------------------------------------------------------
// Snapshot capture (Phase A1a)
// ------------------------------------------------------------------

namespace {
// Uppercase, no-prefix hex — matches Renge::BytesToHex without pulling the
// json-heavy Renge.h into this TU.
std::string SnapshotBytesToHex(const uint8_t* d, size_t n) {
    static const char* kHex = "0123456789ABCDEF";
    std::string s;
    s.reserve(n * 2);
    for (size_t i = 0; i < n; ++i) {
        s.push_back(kHex[(d[i] >> 4) & 0xF]);
        s.push_back(kHex[d[i] & 0xF]);
    }
    return s;
}

// Render a struct-array element's inner-key value to a string: FName -> its
// string; integer -> decimal; otherwise "" (caller falls back to elem index).
std::string RenderInnerKey(const FieldInfo& kf, uintptr_t elemAddr) {
    if (kf.TypeName == "NameProperty")
        return Ubel::ReadFNameAt(elemAddr, kf.Offset);

    ValueScan::DataType dt;
    if (ValueScan::TryDataTypeFromPropertyTypeName(kf.TypeName, dt)) {
        size_t sz = ValueScan::SizeOf(dt);
        uint8_t buf[8] = {};
        if (sz >= 1 && sz <= 8 && Macht::ReadBytesSafe(elemAddr + kf.Offset, buf, sz)) {
            switch (dt) {
                case ValueScan::DataType::Int8:   return std::to_string(static_cast<int>(static_cast<int8_t>(buf[0])));
                case ValueScan::DataType::UInt8:  return std::to_string(static_cast<unsigned>(buf[0]));
                case ValueScan::DataType::Int16:  { int16_t v;  std::memcpy(&v, buf, 2); return std::to_string(v); }
                case ValueScan::DataType::UInt16: { uint16_t v; std::memcpy(&v, buf, 2); return std::to_string(v); }
                case ValueScan::DataType::Int32:  { int32_t v;  std::memcpy(&v, buf, 4); return std::to_string(v); }
                case ValueScan::DataType::UInt32: { uint32_t v; std::memcpy(&v, buf, 4); return std::to_string(v); }
                case ValueScan::DataType::Int64:  { int64_t v;  std::memcpy(&v, buf, 8); return std::to_string(v); }
                case ValueScan::DataType::UInt64: { uint64_t v; std::memcpy(&v, buf, 8); return std::to_string(v); }
                default: break;
            }
        }
    }
    return "";
}

// Capture struct-array elements of `obj` (Phase A1b). For each
// TArray<StructProperty> field, resolve the inner UScriptStruct, pick an
// inner-key field (reorder-immune join key) + its numeric inner fields, and
// emit up to arrayCap elements.
void CaptureStructArrays(uintptr_t obj, const ClassInfo& ci,
                         ValueScan::DataType numericScope, int32_t arrayCap,
                         std::vector<Aura::SnapshotArray>& out) {
    if (arrayCap <= 0) arrayCap = 256;
    for (const auto& fi : ci.Fields) {
        if (fi.TypeName != "ArrayProperty" || fi.innerType != "StructProperty") continue;
        if (!fi.Address) continue;

        // Inner UScriptStruct: ArrayProperty::Inner (FProperty*) -> StructProperty::Struct.
        uintptr_t innerProp = 0, innerStruct = 0;
        if (!Macht::ReadSafe(fi.Address + DynOff::FARRAYPROP_INNER, innerProp) || !innerProp) continue;
        if (!Macht::ReadSafe(innerProp + DynOff::FSTRUCTPROP_STRUCT, innerStruct) || !innerStruct) continue;

        ClassInfo si = Ubel::WalkClassEx(innerStruct);  // cached per struct
        if (si.Fields.empty()) continue;

        int32_t stride = Ubel::GetArrayInnerElemSize(fi.Address);
        if (stride <= 0) continue;

        std::vector<std::string> innerTypes, innerNames;
        innerTypes.reserve(si.Fields.size());
        innerNames.reserve(si.Fields.size());
        for (const auto& f : si.Fields) { innerTypes.push_back(f.TypeName); innerNames.push_back(f.Name); }

        auto numPicks = ValueScan::SelectSnapshotNumericFields(innerTypes, numericScope);
        if (numPicks.empty()) continue;  // nothing numeric to track inside the struct
        int keyIdx = ValueScan::SelectArrayInnerKey(innerTypes, innerNames);

        Macht::TArrayView arr;
        if (!Macht::ReadTArray(obj + fi.Offset, arr)) continue;
        if (arr.Count <= 0 || !arr.Data) continue;
        int32_t n = (std::min)(arr.Count, arrayCap);

        Aura::SnapshotArray sa;
        sa.field = fi.Name;
        for (int32_t e = 0; e < n; ++e) {
            uintptr_t elemAddr = arr.Data + static_cast<uintptr_t>(e) * static_cast<uintptr_t>(stride);
            Aura::SnapshotArrayElement el;
            el.index = e;
            if (keyIdx >= 0 && keyIdx < static_cast<int>(si.Fields.size())) {
                const auto& kf = si.Fields[keyIdx];
                el.keyName  = kf.Name;
                el.keyValue = RenderInnerKey(kf, elemAddr);
            }
            for (const auto& p : numPicks) {
                const auto& nf = si.Fields[p.fieldIndex];
                size_t sz = ValueScan::SizeOf(p.dt);
                if (sz == 0 || sz > 8) continue;
                uint8_t buf[8] = {};
                if (!Macht::ReadBytesSafe(elemAddr + nf.Offset, buf, sz)) continue;
                Aura::SnapshotField f2;
                f2.name = nf.Name; f2.offset = nf.Offset; f2.type = nf.TypeName;
                f2.hex = SnapshotBytesToHex(buf, sz);
                el.fields.push_back(std::move(f2));
            }
            if (!el.fields.empty()) sa.elements.push_back(std::move(el));
        }
        if (!sa.elements.empty()) out.push_back(std::move(sa));
    }
}
} // namespace

SnapshotChunkResult CaptureSnapshotChunk(int32_t offset, int32_t limit,
                                         bool gameOnly,
                                         ValueScan::DataType numericScope,
                                         int32_t arrayCap) {
    SnapshotChunkResult result;
    const int32_t total = GetCount();
    result.total = total;

    if (offset < 0) offset = 0;
    if (limit < 0)  limit = 0;
    const int32_t end = (std::min)(offset + limit, total);
    result.scanned = (end > offset) ? (end - offset) : 0;

    // Reused scratch so per-object capture doesn't churn the heap.
    std::vector<std::string> typeNames;

    for (int32_t i = offset; i < end; ++i) {
        uintptr_t obj = GetByIndex(i);
        if (!obj) continue;

        std::string name = Ubel::GetName(obj);
        if (name.empty()) continue;  // skip unnamed slots (matches get_object_list)

        uintptr_t cls = Ubel::GetClass(obj);
        if (!cls) continue;

        // game_only filter keys on the class path (engine packages skipped).
        std::string classPath = Ubel::GetFullName(cls);
        if (gameOnly && IsEnginePackage(classPath)) continue;

        ClassInfo ci = Ubel::WalkClassEx(cls);  // cached per class
        if (ci.Fields.empty()) continue;

        typeNames.clear();
        typeNames.reserve(ci.Fields.size());
        for (const auto& f : ci.Fields) typeNames.push_back(f.TypeName);

        auto picks = ValueScan::SelectSnapshotNumericFields(typeNames, numericScope);

        SnapshotObject so;
        so.index     = i;  // GObjects index == logical slot index
        so.addr      = obj;
        so.name      = std::move(name);
        so.className = ci.Name;
        so.path      = Ubel::GetFullName(obj);
        uintptr_t outer = Ubel::GetOuter(obj);
        so.outerClassName = outer ? Ubel::GetName(Ubel::GetClass(outer)) : "";

        // Top-level numeric scalar fields.
        for (const auto& p : picks) {
            const auto& fi = ci.Fields[p.fieldIndex];
            size_t sz = ValueScan::SizeOf(p.dt);
            if (sz == 0 || sz > 8) continue;  // defensive; meta members are 1..8B
            uint8_t buf[8] = {};
            if (!Macht::ReadBytesSafe(obj + fi.Offset, buf, sz)) continue;

            SnapshotField sf;
            sf.name   = fi.Name;
            sf.offset = fi.Offset;
            sf.type   = fi.TypeName;
            sf.hex    = SnapshotBytesToHex(buf, sz);
            so.fields.push_back(std::move(sf));
        }

        // Struct-array element inner fields (inner-key capture).
        CaptureStructArrays(obj, ci, numericScope, arrayCap, so.arrays);

        // Keep objects with any captured scalar field OR array element.
        if (so.fields.empty() && so.arrays.empty()) continue;
        result.objects.push_back(std::move(so));
    }

    return result;
}

} // namespace Aura
