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
// Scope (build 738 MVP + Phase 2 expansion 2026-05-26):
//   Numeric (MVP, build 738):
//     - Types:      Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool
//                   (BoolProperty supports both whole-byte and bitfield)
//     - First scan: Exact / Bigger / Smaller / Between
//     - Next scan:  + Changed / Unchanged / Increased / Decreased
//   String (Phase 2A, build 750):
//     - Types:      FString (StrProperty), FName (NameProperty), FText
//                   (TextProperty — best-effort: cooked FText often
//                   has the display string at a probed offset; the
//                   scan falls back to the raw header bytes if no
//                   string is resolvable)
//     - First scan: Exact / Contains / StartsWith / EndsWith
//     - Next scan:  + Changed / Unchanged
//                   (Bigger/Smaller/Between/Increased/Decreased rejected
//                    DLL-side for string types — no natural ordering)
//   Vector (Phase 2B, build 750):
//     - Types:      FVector / FRotator / FTransform (translation only).
//                   FVector / FRotator are 3 floats (X,Y,Z) or
//                   (Pitch,Yaw,Roll). FTransform scans the Translation
//                   FVector — the most useful cheat axis; Rotation +
//                   Scale are out of scope to keep the UI surface tight.
//     - First scan: Exact / Bigger / Smaller (component-wise; Between
//                   takes a single second vector for upper bound)
//     - Next scan:  + Changed / Unchanged / Increased / Decreased
//   Excluded:       Native C++ fields (non-UPROPERTY) — UI must show banner
//   Deferred:       TArray<T> scan — see memory project_value_search_caveats
//                   for the crash-risk plan that gates the v2 expansion
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

// Data types supported by the scan engine. Numeric primitives are the
// MVP; string types (FString/FName/FText) and vector types (FVector/
// FRotator/FTransform) are Phase 2 extensions (build 750). TArray<T>
// scan remains deferred — see memory project_value_search_caveats.
enum class DataType : uint8_t {
    // Numeric primitives (MVP, build 738)
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

    // String types (Phase 2A, build 750). prevValue array unused;
    // candidate's prevStr holds the resolved UTF-8 string.
    FString,
    FName,
    FText,

    // Vector types (Phase 2B, build 750). prevValue array holds the
    // first 8 bytes (X), prevValue2 holds Y, prevValue3 holds Z so the
    // candidate row stays POD-sized. FTransform scans the Translation
    // FVector only (Rotation + Scale out of scope to keep UI tight).
    FVector,
    FRotator,
    FTransform,
};

enum class ScanType : uint8_t {
    // Targeted (compare against user-supplied value)
    Exact = 0,
    Bigger,
    Smaller,
    Between,

    // Prev-value (compare against last-observed candidate bytes)
    Changed,
    Unchanged,
    Increased,
    Decreased,

    // String-only targeted predicates (Phase 2A). Reject DLL-side for
    // numeric / vector types since there's no natural substring concept.
    Contains,
    StartsWith,
    EndsWith,
};

// Byte count for a given DataType. 1..8 for numeric primitives;
// 12 for FVector/FRotator (3 floats); 0 for string types (variable
// length — the scan engine reads on demand via Ubel::ReadFStringAt /
// ReadFNameAt / ReadFTextStringAt).
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
// Numeric: Exact / Bigger / Smaller / Between.
// String:  Exact / Contains / StartsWith / EndsWith.
// Vector:  Exact / Bigger / Smaller / Between (component-wise).
bool IsFirstScanType(ScanType st);

// True when the data type is one of the Phase 2 string family
// (FString / FName / FText) — those use the candidate's prevStr and
// the CompareStringPredicate path instead of byte comparisons.
bool IsStringDataType(DataType dt);

// True when the data type is one of the Phase 2 vector family
// (FVector / FRotator / FTransform). Reads SizeOf(dt) bytes per
// candidate and compares X / Y / Z component-wise with shared tolerance.
bool IsVectorDataType(DataType dt);

// True when the scan type is one of the string-only substring
// predicates (Contains / StartsWith / EndsWith). Caller must reject
// these for non-string DataTypes.
bool IsSubstringScanType(ScanType st);

// True when the (DataType, ScanType) pair is a legal combination.
// Used by the pipe handler to reject Bigger/Smaller on strings and
// Contains/StartsWith on numerics before the scan engine sees them.
bool IsScanTypeValidFor(DataType dt, ScanType st);

// Map a DataType to the set of UE property type-name strings that
// represent it in ClassInfo.Fields. UInt8 maps to ByteProperty
// (UE's name for one-byte unsigned int). Vector types map to
// StructProperty — caller must additionally check the inner struct's
// name matches "Vector" / "Vector3f" / "Rotator" / "Transform" /
// "Transform3f" before emitting a candidate.
const std::vector<std::string>& PropertyTypeNames(DataType dt);

// Vector DataType → set of UScriptStruct names that represent it
// (handles UE5 "Vector3f" + UE4 "Vector" + UE5 LWC variants). Empty
// for non-vector data types.
const std::vector<std::string>& VectorStructNames(DataType dt);

// One candidate as remembered between RPCs. Cache the FindByAddress
// metadata on the FIRST scan so refine rounds don't re-resolve owner
// info. `prevValue` is updated to the latest-observed bytes on every
// successful refine, so Changed/Unchanged/etc. always compare against
// "what we saw last time we looked at this slot".
//
// For Phase 2 string data types (FString / FName / FText) `prevStr`
// holds the resolved UTF-8 value instead of the byte buffer. For
// vector data types (FVector / FRotator) `prevValue` is 12 bytes
// (X / Y / Z packed); for FTransform the same 12 bytes hold the
// Translation FVector (Rotation + Scale not stored).
struct Candidate {
    uintptr_t   addr           = 0;     // instance + fieldOffset (the value's address)
    uintptr_t   instanceAddr   = 0;     // owning UObject
    int32_t     instanceIndex  = -1;    // GObjects index (-1 if not enumerated)
    int32_t     fieldOffset    = 0;     // bytes from instanceAddr
    uint8_t     prevValue[16]  = {};    // last-observed bytes; size = SizeOf(dt)
    std::string prevStr;                // last-observed string (string DataTypes only)
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
// `tolerance` is the CE-style rounded-scan slack applied to Float/Double
// only. Per-scan-type semantics (let `a` = target, `b` = target2, `c` = cur):
//   Exact      |c - a| <= tol           (matches displayed-rounded values)
//   Bigger     c > a + tol              (clearly above the tolerance band)
//   Smaller    c < a - tol
//   Between    a - tol <= c <= b + tol  (widen the inclusive range)
//   Changed    |c - prev| > tol         (changed by more than noise)
//   Unchanged  |c - prev| <= tol
//   Increased  c > prev + tol           (strictly above prev beyond noise)
//   Decreased  c < prev - tol
// For Int8/16/32/64 + UInt8/16/32/64 + Bool the tolerance is ignored
// (typed comparison stays exact -- tolerance semantics don't transfer
// cleanly to integral types where the user typically means literal values).
//
// Exposed for the dll_helpers_test unit suite; the scan + refine
// engines call this internally.
bool ComparePredicate(DataType dt, ScanType st,
                      const uint8_t* rawBytes,
                      const uint8_t* targetBytes,
                      const uint8_t* target2Bytes = nullptr,
                      double         tolerance    = 0.0);

// String predicate. `cur` is the latest-observed string at the
// candidate field; `target` is either the user-supplied search string
// (targeted predicates) or the candidate's prevStr (Changed /
// Unchanged). Supports Exact / Contains / StartsWith / EndsWith /
// Changed / Unchanged; all other scan types return false.
//
// CE-style default is case-insensitive — `caseSensitive=true` keeps the
// raw comparison (used when the user explicitly opts in). Matching
// uses byte-level comparison after lowercasing ASCII letters — non-
// ASCII bytes (UTF-8 multibyte sequences) compare bitwise. This is
// sufficient for the cheat use cases that hit string scans (English
// item names, savegame keys, dialogue tags); a full Unicode case fold
// would need an ICU dependency we don't ship and isn't justified yet.
bool CompareStringPredicate(ScanType           st,
                            const std::string& cur,
                            const std::string& target,
                            bool               caseSensitive);

// Vector predicate. `rawBytes` is 12 bytes (3 floats X,Y,Z) read from
// the candidate field. `targetBytes` / `target2Bytes` are 12 bytes
// each (Between takes a second corner). Component-wise compare with
// shared tolerance applied per-axis.
//
// ScanType semantics (let a = target, b = target2, c = current,
// applied per axis):
//   Exact      |c - a| <= tol             (all three axes match)
//   Bigger     c > a + tol                (all three axes strictly above)
//   Smaller    c < a - tol                (all three axes strictly below)
//   Between    a - tol <= c <= b + tol    (all three axes inside box)
//   Changed    any axis |c - prev| > tol
//   Unchanged  all axes  |c - prev| <= tol
//   Increased  any axis  c > prev + tol (game movement is rarely
//              uniformly higher on all axes — match if ANY component
//              moved in the requested direction)
//   Decreased  any axis  c < prev - tol
// Substring scan types reject for vectors (return false).
bool CompareVectorPredicate(ScanType       st,
                            const uint8_t* rawBytes,
                            const uint8_t* targetBytes,
                            const uint8_t* target2Bytes = nullptr,
                            double         tolerance    = 0.0);

}  // namespace ValueScan
