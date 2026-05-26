#pragma once

// ============================================================
// ValueScan — CE-style First Scan / Next Scan workflow
//
// Walks GObjects + UProperty metadata to find every UPROPERTY-declared
// field whose typed value matches the user's target. Each candidate is
// already enriched with owning UObject + class + defining-class + field
// metadata, so the user sees `ACharacter.Health = 100 @ PlayerPawn_0`
// instead of CE's raw "address = 100".
//
// Session lifecycle:
//   begin_value_scan   → SessionManager::Begin returns sessionId
//   refine_value_scan  → SessionManager::RefineWith runs predicate
//                        against cached candidates, pruning in place
//   end_value_scan     → SessionManager::End drops the session
//
// Sessions auto-expire after 300s of inactivity so a misbehaving client
// can't leak memory. SessionManager holds a global mutex; ownership is
// via unique_ptr so move semantics are cheap when refining (caller's
// lambda runs inside the lock holding a reference to the candidates
// vector — typical refine reads ~50K addresses via ReadSafe which is
// fast enough that a coarse lock is acceptable for v1).
//
// MVP scope (locked-in 2026-05-26 — see memory project_value_search):
//   - Types:     Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool
//                (BoolProperty supports both whole-byte and bitfield)
//   - First scan: Exact / Bigger / Smaller / Between
//   - Next scan:  Exact / Bigger / Smaller / Between (vs target value)
//                 Changed / Unchanged / Increased / Decreased (vs prev value)
//   - Excluded:  Native C++ fields (non-UPROPERTY) — UI must show banner
//   - Deferred:  FString / FName / FText / TArray<T> / FVector
//
// The candidate enrichment matches Aura::FindByAddress conventions so
// "Open in Live Walker" works without further address-→-instance lookup.
// ============================================================

#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace ValueScan {

// Primitive data types supported by the MVP. String/array/vector forms
// are deferred — see memory project_value_search_caveats for the
// TArray<T> crash-risk plan that gates the v2 expansion.
enum class DataType : uint8_t {
    Int8 = 0,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float,
    Double,
    Bool,
};

enum class ScanType : uint8_t {
    Exact = 0,
    Bigger,
    Smaller,
    Between,
    Changed,
    Unchanged,
    Increased,
    Decreased,
};

// Byte count for a given DataType. 1..8 for primitives.
size_t SizeOf(DataType dt);

// Human-readable name (matches the JSON wire shape: "Int32" / "Float" / ...)
const char* NameOf(DataType dt);

// Parse from wire string; returns true on match, false on unknown.
bool TryParseDataType(const std::string& s, DataType& out);
bool TryParseScanType(const std::string& s, ScanType& out);

// True when the scan type compares against the stored prevValue.
// (Changed / Unchanged / Increased / Decreased)
bool IsPrevValueScanType(ScanType st);

// True when the scan type is valid for the FIRST scan (no prevValue yet).
// (Exact / Bigger / Smaller / Between)
bool IsFirstScanType(ScanType st);

// Map a DataType to the set of UE property type-name strings that
// represent it in ClassInfo.Fields. UInt8 maps to ByteProperty
// (UE's name for one-byte unsigned int).
const std::vector<std::string>& PropertyTypeNames(DataType dt);

// One candidate as remembered between RPCs. Cache the FindByAddress
// metadata on the FIRST scan so refine rounds don't re-resolve owner
// info. `prevValue` is updated to the latest-observed bytes on every
// successful refine, so Changed/Unchanged/etc. always compare against
// "what we saw last time we looked at this slot".
struct Candidate {
    uintptr_t   addr           = 0;     // instance + fieldOffset (the value's address)
    uintptr_t   instanceAddr   = 0;     // owning UObject
    int32_t     instanceIndex  = -1;    // GObjects index (-1 if not enumerated)
    int32_t     fieldOffset    = 0;     // bytes from instanceAddr
    uint8_t     prevValue[8]   = {};    // last-observed bytes; size = SizeOf(dt)
    std::string instanceName;
    std::string className;              // The instance's UClass name
    std::string definingClassName;      // Class where the field is declared
    std::string fieldName;
    std::string fieldType;              // "FloatProperty" / "IntProperty" / ...
    // BoolProperty bitfield support: when boolFieldMask != 0xFF the
    // field shares a byte with siblings. Read as `(byte & mask) != 0`.
    uint8_t     boolFieldMask  = 0xFF;
};

struct Session {
    uint64_t                              id        = 0;
    DataType                              dt        = DataType::Int32;
    std::vector<Candidate>                candidates;
    std::chrono::steady_clock::time_point lastUse;
};

class SessionManager {
public:
    static SessionManager& Instance();

    // Allocate a new session, take ownership of the candidate vector,
    // return its sessionId. Triggers a lazy expiry pass on existing
    // sessions so abandoned sessions don't accumulate.
    uint64_t Begin(DataType dt, std::vector<Candidate> candidates);

    // Run `fn(dataType, candidates&)` under the manager's lock. Returns
    // false if the session doesn't exist (caller should map to wire
    // error "session_not_found"). `fn` may mutate the candidates
    // vector (typical Refine behavior is to prune entries that no
    // longer match).
    template <typename Fn>
    bool RefineWith(uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(it->second->dt, it->second->candidates);
        return true;
    }

    // Read-only access — same lock as RefineWith.
    template <typename Fn>
    bool ViewWith(uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        fn(it->second->dt, it->second->candidates);
        return true;
    }

    // Drop a specific session. Returns false if not found.
    bool End(uint64_t sessionId);

    // Drop sessions whose lastUse is older than kExpirySeconds.
    // Called lazily from Begin().
    void ExpireOldSessions();

    // Drop every session — called from pipe shutdown / DLL unload.
    void DropAll();

    struct Stats {
        size_t   sessionCount    = 0;
        size_t   totalCandidates = 0;
        uint64_t nextId          = 0;
    };
    Stats GetStats();

private:
    SessionManager() = default;
    SessionManager(const SessionManager&) = delete;
    SessionManager& operator=(const SessionManager&) = delete;

    // 5-minute idle expiry — matches discrete's Phase 27b precedent.
    // Long enough that a user can step away mid-refine without losing
    // candidates; short enough that abandoned sessions clear on the
    // next Begin().
    static constexpr std::chrono::seconds kExpirySeconds{300};

    std::mutex                                                  mu_;
    std::unordered_map<uint64_t, std::unique_ptr<Session>>      sessions_;
    uint64_t                                                    nextId_ = 1;
};

// Typed compare predicate. Returns true if rawBytes (size = SizeOf(dt))
// satisfies (scanType, targetBytes, target2Bytes) where target2Bytes is
// only consulted for ScanType::Between.
//
// For prev-value scan types (Changed / Unchanged / Increased / Decreased)
// targetBytes is the candidate's prevValue snapshot.
//
// Exposed for the dll_helpers_test unit suite; the scan + refine
// engines call this internally.
bool ComparePredicate(DataType dt, ScanType st,
                      const uint8_t* rawBytes,
                      const uint8_t* targetBytes,
                      const uint8_t* target2Bytes = nullptr);

}  // namespace ValueScan
