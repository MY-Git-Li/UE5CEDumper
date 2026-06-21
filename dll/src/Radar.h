#pragma once

// ============================================================
// Radar — 拉達爾 (影子戰士 — Shadow Warrior)
// CE-style value scan: First Scan / Next Scan workflow
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
//   Containers:     TArray<T> (Phase 2C, build 757) + TSet<T> / TMap<K,V>
//                   key|value (V1a, build 927) are all walked element-wise
//                   for any supported leaf DataType. Sparse containers
//                   iterate allocated slots only. Element addresses are raw,
//                   so refine degrades on a container reallocation (SEH-safe
//                   read drops the candidate). TOptional is still deferred (V1c).
//   Excluded:       Native C++ fields (non-UPROPERTY) — UI must show banner
//

// The candidate enrichment matches Aura::FindByAddress conventions so
// "Open in Live Walker" works without further address-→-instance lookup.
// ============================================================

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace Radar {

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

    // Multi-numeric "meta" type (build 794). Unlike CE's raw "All" scan
    // (which reinterprets the same untyped bytes as multiple widths),
    // our structured property walk knows each field's DECLARED type, so
    // NumericNoByte means: accept every word/dword/qword/float/double
    // UPROPERTY and compare the user's value against each field using
    // that field's own declared width — no byte-reinterpret false hits.
    // "NoByte" deliberately excludes Int8/UInt8/Bool: 1-byte fields are
    // extremely numerous and a small value (0/1/5/100) would explode the
    // candidate set. Members: Int16/UInt16, Int32/UInt32, Int64/UInt64,
    // Float, Double. SizeOf() returns 0 (variable, like strings); the
    // scan/refine engines resolve the concrete per-field DataType via
    // TryDataTypeFromPropertyTypeName + a pre-built NumericTargetSet.
    NumericNoByte,

    // Multi-numeric "meta" type WITH 1-byte fields (build 796). Same
    // structured per-width compare as NumericNoByte, but additionally
    // includes Int8 (Int8Property) + UInt8 (ByteProperty). Bool is still
    // excluded (it has its own single-type scan). Members: Int8/UInt8,
    // Int16/UInt16, Int32/UInt32, Int64/UInt64, Float, Double. WARNING:
    // small values (0/1/255) match a very large number of 1-byte fields —
    // the UI surfaces a result-volume warning for this type.
    NumericAll,
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

// Wire string for a ScanType ("Exact" / "Bigger" / "Increased" / ...). Inverse
// of TryParseScanType; used to echo a session's stored per-slot predicate back
// to the UI (the group scan persists ScanType as an enum, not the raw string).
const char* NameOf(ScanType st);

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

// True when the data type is a multi-numeric "meta" type
// (NumericNoByte / NumericAll) that fans out over a fixed set of
// concrete numeric member types instead of a single fixed-width compare.
bool IsMultiNumericDataType(DataType dt);

// Concrete member DataTypes a multi-numeric meta type expands to.
// NumericNoByte -> { Int16, UInt16, Int32, UInt32, Int64, UInt64,
//                    Float, Double }.
// NumericAll    -> the above plus { Int8, UInt8 }.
// Empty for non-meta data types.
const std::vector<DataType>& MultiNumericMembers(DataType dt);

// Map a UE property type-name string (as it appears in
// ClassInfo.Fields[].TypeName) to its concrete scalar DataType. The
// full numeric member set is recognised — "IntProperty"->Int32,
// "FloatProperty"->Float, "Int16Property"->Int16, "Int8Property"->Int8,
// "ByteProperty"->UInt8, etc. Returns false for BoolProperty +
// non-numeric names. (NumericNoByte simply never feeds byte-width names
// here because its PropertyTypeNames union excludes them.) Used by the
// scan + refine engines to resolve each candidate's own width.
bool TryDataTypeFromPropertyTypeName(const std::string& propTypeName, DataType& out);

// Inverse of TryDataTypeFromPropertyTypeName: a concrete numeric DataType ->
// its canonical UE property-type string ("IntProperty" / "FloatProperty" /
// "ByteProperty" / ...). Returns "" for non-numeric / meta / Bool types.
// Used by the Native-C value scan to stamp synthetic raw-hole descriptors with
// a fieldType the refine path (TryDataTypeFromPropertyTypeName) round-trips.
const char* PropertyTypeNameOf(DataType dt);

// Snapshot capture (Phase A1a): given the property type-name of each field
// of a class (in ClassInfo.Fields order), select those whose declared type
// is a member of the given numeric meta scope (NumericNoByte / NumericAll),
// pairing each selected field's index with its resolved concrete DataType.
// Pure / std-only so it reuses the exact same MultiNumericMembers +
// TryDataTypeFromPropertyTypeName invariant the value scan relies on — a
// field captured here is guaranteed to resolve to a fixed width via
// SizeOf(dt). Non-numeric, Bool, and out-of-scope (e.g. 1-byte under
// NumericNoByte) fields are omitted.
struct SnapshotFieldPick {
    int32_t  fieldIndex;
    DataType dt;
};
std::vector<SnapshotFieldPick> SelectSnapshotNumericFields(
    const std::vector<std::string>& propTypeNames, DataType numericScope);

// Snapshot array capture (Phase A1b): pick the index of the best "inner key"
// field for a struct-array element, given the element struct's field type names
// + field names (parallel vectors). Prefers a NameProperty (FName) whose name
// looks like an id/name/tag/key/row; then any NameProperty; then the first
// integer field. Returns -1 if none (caller falls back to the element index).
// Pure / std-only. Used to give cargo/inventory rows a reorder-immune key
// (e.g. FCargoSlot.ItemID) so the same logical slot joins across snapshots.
int SelectArrayInnerKey(const std::vector<std::string>& typeNames,
                        const std::vector<std::string>& fieldNames);

// Pre-parsed multi-numeric target. Holds one little-endian byte buffer
// per member DataType whose width can represent the user's value (e.g.
// "70000" yields Int32/UInt32/Int64/UInt64/Float/Double entries but no
// Int16/UInt16; "100.5" yields only Float/Double; "-5" yields the
// signed + float members but no unsigned ones). BuildNumericTargets
// populates it; the scan/refine engines Find() the entry matching each
// field's resolved DataType — a missing entry means "value can't fit
// this width" and the field is skipped (no candidate / pruned).
struct NumericTargetSet {
    struct Entry {
        DataType dt;
        uint8_t  bytes[8];
    };
    std::vector<Entry> entries;

    // Return the buffer for `dt`, or nullptr if the value didn't fit
    // that width.
    const uint8_t* Find(DataType dt) const {
        for (const auto& e : entries) {
            if (e.dt == dt) return e.bytes;
        }
        return nullptr;
    }
};

// Parse the user's numeric value string into one NumericTargetSet entry
// per member width of `metaDt` (currently NumericNoByte) that can
// represent it. Returns false (and leaves out.entries empty) when the
// string is empty / unparseable / fits no member width. Signed,
// unsigned, and floating interpretations are each attempted and gated:
//   - negative values produce no unsigned entries
//   - non-integral values (e.g. "100.5") produce no integer entries
//   - hex (0x..) values produce integer entries only
bool BuildNumericTargets(DataType metaDt, const std::string& raw, NumericTargetSet& out);

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

// ---- Interned candidate metadata (build 924, V3-A) -------------------
//
// A First Scan can return tens of thousands of candidates. Each used to
// carry six std::strings (class / defining-class / field name / field
// type / instance name) copied BY VALUE, which is almost entirely
// redundant: className / definingClassName / fieldName / fieldType /
// boolFieldMask / fieldOffset are functions of the (class, field) pair —
// identical across every candidate of that class+field — and instanceName
// is shared by every field that matches on the same object. Interning
// them into two session-level pools shrinks the per-candidate record from
// ~240 B (+ up to 5 heap strings) to ~72 B (+ 0 heap strings for numeric
// scans). That is what lets a session hold a large candidate set inside
// the injected DLL without bloating the target process (the precondition
// for any later maxResults-cap increase — see todo.md V2/V3).

// Per-(class, field) display metadata. One entry per distinct field the
// scan emits a candidate for; shared via Candidate::descriptorIdx.
struct FieldDescriptor {
    std::string className;          // The instance's UClass name
    std::string definingClassName;  // Class where the field is declared
    std::string fieldName;          // BASE name (no array "[i]" suffix)
    std::string fieldType;          // "FloatProperty" / "IntProperty" / ...
    int32_t     fieldOffset   = 0;  // bytes from instanceAddr
    // BoolProperty bitfield support: when boolFieldMask != 0xFF the field
    // shares a byte with siblings. Read as `(byte & mask) != 0`.
    uint8_t     boolFieldMask = 0xFF;
    // Native-C (P1): true when this descriptor is a synthetic raw-hole leaf
    // (an unmanaged, non-UPROPERTY offset) rather than a reflected field. The
    // refine path is identical (re-read addr, re-resolve width from fieldType);
    // this flag + guessedType only drive UI badging (className stays the owning
    // class, fieldName encodes the offset as "<raw@0xNN>", definingClassName "").
    bool        isNativeC = false;
    std::string guessedType;        // human label of the interpreted width (e.g. "Int32")
};

// Per-owning-object metadata. One entry per distinct UObject that owns at
// least one candidate; shared via Candidate::instanceIdx. (Each object is
// scanned by exactly one worker thread, so instances never duplicate
// across the per-thread merge.)
struct InstanceRecord {
    uintptr_t   instanceAddr  = 0;  // owning UObject
    int32_t     instanceIndex = -1; // GObjects index (-1 if not enumerated)
    std::string instanceName;
};

// One candidate as remembered between RPCs. Holds only per-candidate
// state: the value's address, the last-observed value, and indices into
// the session's FieldDescriptor / InstanceRecord pools. `prevValue` is
// updated to the latest-observed bytes on every successful refine so
// Changed/Unchanged/etc. always compare against "what we saw last time".
//
// For Phase 2 string data types (FString / FName / FText) `prevStr` holds
// the resolved UTF-8 value and `prevValue` is unused. For vector data
// types (FVector / FRotator) `prevValue` is 12 bytes (X / Y / Z packed);
// for FTransform the same 12 bytes hold the Translation FVector.
// `elementIndex` is >= 0 for a TArray/container element (display name is
// descriptor.fieldName + "[elementIndex]") and -1 for a direct field.
struct Candidate {
    uintptr_t   addr          = 0;   // instance + fieldOffset (the value's address)
    uint8_t     prevValue[16] = {};  // last-observed bytes; size = SizeOf(dt)
    std::string prevStr;             // last-observed string (string DataTypes only)
    uint32_t    descriptorIdx = 0;   // -> Session::descriptors
    uint32_t    instanceIdx   = 0;   // -> Session::instances
    int32_t     elementIndex  = -1;  // array/container element index, -1 = direct field
};

struct Session {
    uint64_t                              id        = 0;
    DataType                              dt        = DataType::Int32;
    std::vector<Candidate>                candidates;
    std::vector<FieldDescriptor>          descriptors;  // shared via Candidate::descriptorIdx
    std::vector<InstanceRecord>           instances;    // shared via Candidate::instanceIdx
    std::chrono::steady_clock::time_point lastUse;

    // V3-C: cached ordered view (filtered + sorted candidate indices) so pure
    // paging doesn't re-sort. Recomputed by SessionManager::QueryWith only when
    // (viewFilter, viewSortKey, viewSortDesc) change, and invalidated
    // (viewValid=false) after a refine mutates `candidates`. viewSortKey is the
    // raw SortKey value (stored as uint8_t because the enum is declared after
    // this struct).
    bool                                  viewValid    = false;
    std::string                           viewFilter;
    uint8_t                               viewSortKey  = 0;
    bool                                  viewSortDesc = false;
    std::string                           viewExclude;  // canonical key of excluded classes
    std::vector<uint32_t>                 viewOrder;
};

// Reconstruct a candidate's display field name from its descriptor +
// element index: `fieldName` for a direct field, `fieldName[elementIndex]`
// for a TArray/container element. Pure / std-only (unit-tested).
std::string FieldDisplayName(const FieldDescriptor& desc, int32_t elementIndex);

// V1c: byte offset of the bIsSet flag inside a non-intrusive TOptional<T>.
// A non-intrusive optional is laid out `{ T value; bool bIsSet; }` (padded to
// alignof(T)), so the flag sits at offset == sizeof(T) and the wrapped value
// is at offset 0. Returns innerSize when the optional is larger than its value
// (i.e. there's room for the trailing bool), else -1 — meaning "no separate
// flag to gate on" (intrusive/pointer optionals encode unset in the value
// itself, and an unknown/zero innerSize can't be gated). Pure (unit-tested).
int32_t OptionalFlagOffset(int32_t optionalSize, int32_t innerSize);

// --- V3-C: server-side value rendering / filter / sort over a candidate pool ---
//
// The session owns the full candidate set inside the injected DLL; the UI is a
// windowed view. Filter / sort therefore run here (over the DLL's own pools —
// no game-memory reads, so the game thread is never touched) and only a window
// is serialized to the UI. These helpers are pure / std-only (unit-tested).

// Render a candidate's value exactly as the wire `value` field does: numeric
// formatted per `dt`; multi-numeric resolved per the descriptor's fieldType;
// vector "X, Y, Z"; string the resolved prevStr. The single source of truth
// for value rendering (the wire encoder calls this too).
std::string FormatCandidateValue(const Candidate& c, DataType dt,
                                 const FieldDescriptor& desc);

// Decode a numeric candidate's prevValue to a double sort key (lossy for very
// large 64-bit integers but monotonic enough for ordering). Non-numeric dt
// returns 0.
double DecodeNumericToDouble(DataType dt, const uint8_t* bytes);

// Columns the result grid can sort by. ScanOrder = the original scan/refine
// order (the candidate vector order). Wire strings parsed by TryParseSortKey.
enum class SortKey : uint8_t {
    ScanOrder, Address, Value, ClassName, FieldName,
    InstanceName, FieldType, Offset, InstanceIndex
};
bool TryParseSortKey(const std::string& s, SortKey& out);

// Canonicalize an excluded-class list into a stable cache key (sorted, '\n'-
// joined) so the view cache is order-insensitive to the wire list. Pure.
inline std::string CanonicalExcludeKey(const std::vector<std::string>& classes) {
    if (classes.empty()) return std::string();
    std::vector<std::string> v = classes;
    std::sort(v.begin(), v.end());
    std::string key;
    for (const auto& s : v) { key += s; key.push_back('\n'); }
    return key;
}

// Build the display order over a candidate pool: keep only candidates whose
// displayed columns contain `filter` (case-insensitive substring; "" keeps
// all) AND whose class is NOT in `excludeClasses` (the client-side class-noise
// picker, applied server-side over the full set), then stable-sort by
// (sortKey, sortDesc). Returns candidate indices in display order — the caller
// slices the requested window out of it.
std::vector<uint32_t> BuildOrderedView(
    const std::vector<Candidate>&       candidates,
    const std::vector<FieldDescriptor>& descriptors,
    const std::vector<InstanceRecord>&  instances,
    DataType dt, const std::string& filter,
    SortKey sortKey, bool sortDesc,
    const std::unordered_set<std::string>& excludeClasses = {});

// Tally a value-scan candidate pool by owning class name, sorted by count desc
// (ties: class name asc). Computed over the WHOLE pool (pre-filter, pre-exclude)
// so the noise picker stays stable as the user filters/excludes. Pure / std-only.
std::vector<std::pair<std::string, int>> BuildClassHistogram(
    const std::vector<Candidate>&       candidates,
    const std::vector<FieldDescriptor>& descriptors);

class SessionManager {
public:
    static SessionManager& Instance();

    // Allocate a new session, take ownership of the candidate vector plus
    // its shared descriptor / instance pools, return its sessionId.
    // Triggers a lazy expiry pass on existing sessions so abandoned
    // sessions don't accumulate.
    uint64_t Begin(DataType dt,
                   std::vector<Candidate>       candidates,
                   std::vector<FieldDescriptor> descriptors,
                   std::vector<InstanceRecord>  instances);

    // Run `fn(Session&)` under the manager's lock. Returns false if the
    // session doesn't exist (caller should map to wire error
    // "session_not_found"). `fn` may mutate the session's candidates
    // vector (typical Refine behavior is to prune entries that no longer
    // match); the descriptor / instance pools are append-only and should
    // be treated as read-only by the callback.
    template <typename Fn>
    bool RefineWith(uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(*it->second);
        // The candidate vector may have been pruned — drop the cached view
        // so the next QueryWith rebuilds the order against the surviving set.
        it->second->viewValid = false;
        return true;
    }

    // Read-only access — same lock as RefineWith. `fn(const Session&)`.
    template <typename Fn>
    bool ViewWith(uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        fn(static_cast<const Session&>(*it->second));
        return true;
    }

    // V3-C server-side window query. Ensures the session's cached ordered view
    // matches (filter, sortKey, sortDesc) — recomputing via BuildOrderedView
    // only when those params changed or the view was invalidated by a refine —
    // then calls `fn(const Session&, const std::vector<uint32_t>& order)` under
    // the lock; the caller slices the requested window out of `order`. Returns
    // false if the session doesn't exist. Reads only the DLL's own pools (no
    // game memory), so it never touches the game thread.
    template <typename Fn>
    bool QueryWith(uint64_t sessionId, const std::string& filter,
                   SortKey sortKey, bool sortDesc,
                   const std::vector<std::string>& excludeClasses, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        Session& s = *it->second;
        s.lastUse = std::chrono::steady_clock::now();
        const uint8_t keyRaw = static_cast<uint8_t>(sortKey);
        const std::string exclKey = CanonicalExcludeKey(excludeClasses);
        if (!s.viewValid || s.viewFilter != filter
            || s.viewSortKey != keyRaw || s.viewSortDesc != sortDesc
            || s.viewExclude != exclKey) {
            std::unordered_set<std::string> exclSet(excludeClasses.begin(), excludeClasses.end());
            s.viewOrder = BuildOrderedView(s.candidates, s.descriptors,
                                           s.instances, s.dt, filter,
                                           sortKey, sortDesc, exclSet);
            s.viewFilter   = filter;
            s.viewSortKey  = keyRaw;
            s.viewSortDesc = sortDesc;
            s.viewExclude  = exclKey;
            s.viewValid    = true;
        }
        fn(static_cast<const Session&>(s), s.viewOrder);
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

// ============================================================
// Multiple values group scan (build 1276) — object-aware "group scan".
//
// A group scan looks for objects (blocks) that SIMULTANEOUSLY hold ALL of N
// user values (2..4) at DISTINCT numeric-property offsets, in any order. The
// pure combinatorial match lives in Orden.h (source-agnostic); the structures
// below persist a group session so refine can re-read the located offsets.
//
// One logical hit = one owning UObject + a per-slot CONVERGENCE LIST of the
// offsets whose current value still satisfies that slot. On the first scan a
// slot may match several offsets; refine re-reads them and shrinks each list. A
// slot list of size 1 is "locked" (the field is identified). The owning object
// stays a candidate only while a System of Distinct Representatives exists
// across the slots (checked via Orden::HasDistinctAssignment in Aura's
// scan/refine — kept out of this header to avoid an Orden<->Radar include cycle,
// since Orden.h includes Radar.h).
//
// Descriptor / instance pools are reused exactly as the single-value Session,
// so a slot match is a lean index pair. GroupSessionManager mirrors
// SessionManager (same 300s expiry, coarse lock, V3-C view cache). Kept a
// SIBLING of SessionManager — not folded in — so the battle-tested single-value
// path is untouched.
// ============================================================

// One input value slot of a group scan. `targets` is the pre-parsed multi-width
// target set (BuildNumericTargets); `value` is the raw user string echoed back
// on the wire. P2: `st` is the per-slot predicate — a first-scan targeted type
// (Exact / Bigger / Smaller) on begin, plus the prev-value types (Changed /
// Unchanged / Increased / Decreased) on refine, where the compare is against
// each matched leaf's stored prevValue and `targets` is unused. `tolerance` is
// the float +- band (0 = exact / strict; only meaningful for Float/Double leaves).
struct SlotSpec {
    DataType         dt = DataType::NumericNoByte;  // per-slot meta or concrete width
    ScanType         st = ScanType::Exact;
    std::string      value;                          // original user value (display/echo)
    NumericTargetSet targets;                        // pre-parsed per fitting width
    double           tolerance = 0.0;                // float compare band (0 = exact)
    std::string      value2;                         // Between upper bound (display/echo)
    NumericTargetSet targets2;                       // pre-parsed upper bound (Between only)
};

// One converging match of a slot inside a candidate object. `offset` is the byte
// offset from the owning object to the leaf value (== descriptor.fieldOffset for
// a direct field; a separate field so containers can carry an element-address
// offset later). `prevValue` is the last-observed bytes at that leaf so the wire
// can render it and a future prev-value refine can compare.
struct GroupSlotMatch {
    uint32_t  descriptorIdx = 0;   // -> GroupSession::descriptors
    int32_t   elementIndex  = -1;  // -1 = direct field; >=0 = container element index
    int32_t   offset        = 0;   // bytes from the OWNING object to the leaf (direct); 0 for deep
    uintptr_t leafAddr      = 0;   // ABSOLUTE address of the value — direct: owner+offset;
                                   // deep (container element): the element's own address.
                                   // Refine re-reads this (SEH-safe; stale on container realloc = drop).
    uintptr_t ownerAddr     = 0;   // object directly holding the leaf: the candidate actor for an
                                   // own-block leaf, or an OWNED sub-object for a cross-object leaf
                                   // (P4). Drives the per-slot handoffs to the right object.
    std::string ownerClass;        // class name of ownerAddr's object: the candidate class for an
                                   // own-block leaf, the OWNED sub-object's class for a cross-object
                                   // leaf (P4 inc 2). Drives the per-slot Pivot handoff to the right class.
    uint8_t   prevValue[16] = {};  // last-observed leaf bytes
};

// One group candidate: an owning object plus, per slot, the convergence list of
// matches. slotMatches.size() == session.slots.size(); every inner list is
// non-empty while the candidate lives (an emptied slot drops the candidate).
struct GroupCandidate {
    uint32_t                                 instanceIdx = 0;  // -> GroupSession::instances
    std::vector<std::vector<GroupSlotMatch>> slotMatches;
};

struct GroupSession {
    uint64_t                              id = 0;
    std::vector<SlotSpec>                 slots;
    std::vector<GroupCandidate>           candidates;
    std::vector<FieldDescriptor>          descriptors;  // shared via GroupSlotMatch::descriptorIdx
    std::vector<InstanceRecord>           instances;    // shared via GroupCandidate::instanceIdx
    std::chrono::steady_clock::time_point lastUse;

    // V3-C cached ordered view (same contract as Session::view*).
    bool                                  viewValid    = false;
    std::string                           viewFilter;
    uint8_t                               viewSortKey  = 0;
    bool                                  viewSortDesc = false;
    std::string                           viewExclude;  // canonical key of excluded classes
    std::vector<uint32_t>                 viewOrder;
};

// Build the display order over a group-candidate pool: keep candidates whose
// owning class / instance / any slot field-name or value contains `filter`
// (case-insensitive; "" keeps all), then stable-sort by (sortKey, sortDesc).
// Object-level keys (ClassName / InstanceName / InstanceIndex) plus slot-0's
// Offset / Value are supported; unsupported keys fall back to scan order. Pure /
// std-only. Returns candidate indices in display order.
std::vector<uint32_t> BuildGroupOrderedView(
    const std::vector<GroupCandidate>&  candidates,
    const std::vector<SlotSpec>&        slots,
    const std::vector<FieldDescriptor>& descriptors,
    const std::vector<InstanceRecord>&  instances,
    const std::string& filter, SortKey sortKey, bool sortDesc,
    const std::unordered_set<std::string>& excludeClasses = {});

// Tally a group-candidate pool by the candidate's OBJECT-level class (first
// non-empty slot's first match descriptor className — same key BuildGroupOrdered-
// View sorts/filters on, NOT per-slot owner_class), sorted count desc / name asc.
// Computed over the whole pool. Pure / std-only.
std::vector<std::pair<std::string, int>> BuildGroupClassHistogram(
    const std::vector<GroupCandidate>&  candidates,
    const std::vector<FieldDescriptor>& descriptors);

// Sibling of SessionManager for group sessions. Same lifecycle/expiry/lock/view
// contract; the single-value SessionManager is left untouched.
class GroupSessionManager {
public:
    static GroupSessionManager& Instance();

    uint64_t Begin(std::vector<SlotSpec>        slots,
                   std::vector<GroupCandidate>  candidates,
                   std::vector<FieldDescriptor> descriptors,
                   std::vector<InstanceRecord>  instances);

    // Run `fn(GroupSession&)` under the lock; may prune candidates. Invalidates
    // the cached view afterwards.
    template <typename Fn>
    bool RefineWith(uint64_t sessionId, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        it->second->lastUse = std::chrono::steady_clock::now();
        fn(*it->second);
        it->second->viewValid = false;
        return true;
    }

    // V3-C window query — ensures the cached ordered view matches (filter,
    // sortKey, sortDesc), then `fn(const GroupSession&, const std::vector<uint32_t>&)`.
    template <typename Fn>
    bool QueryWith(uint64_t sessionId, const std::string& filter,
                   SortKey sortKey, bool sortDesc,
                   const std::vector<std::string>& excludeClasses, Fn&& fn) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = sessions_.find(sessionId);
        if (it == sessions_.end()) return false;
        GroupSession& s = *it->second;
        s.lastUse = std::chrono::steady_clock::now();
        const uint8_t keyRaw = static_cast<uint8_t>(sortKey);
        const std::string exclKey = CanonicalExcludeKey(excludeClasses);
        if (!s.viewValid || s.viewFilter != filter
            || s.viewSortKey != keyRaw || s.viewSortDesc != sortDesc
            || s.viewExclude != exclKey) {
            std::unordered_set<std::string> exclSet(excludeClasses.begin(), excludeClasses.end());
            s.viewOrder = BuildGroupOrderedView(s.candidates, s.slots, s.descriptors,
                                                s.instances, filter, sortKey, sortDesc, exclSet);
            s.viewFilter   = filter;
            s.viewSortKey  = keyRaw;
            s.viewSortDesc = sortDesc;
            s.viewExclude  = exclKey;
            s.viewValid    = true;
        }
        fn(static_cast<const GroupSession&>(s), s.viewOrder);
        return true;
    }

    bool End(uint64_t sessionId);
    void ExpireOldSessions();
    void DropAll();

private:
    GroupSessionManager() = default;
    GroupSessionManager(const GroupSessionManager&) = delete;
    GroupSessionManager& operator=(const GroupSessionManager&) = delete;

    static constexpr std::chrono::seconds kExpirySeconds{300};

    std::mutex                                                      mu_;
    std::unordered_map<uint64_t, std::unique_ptr<GroupSession>>     sessions_;
    uint64_t                                                        nextId_ = 1;
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

}  // namespace Radar
