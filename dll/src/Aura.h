#pragma once

// ============================================================
// Aura — 斷頭台的奧拉 (服從之秤 — Obedience Scale)
// ObjectArray: FUObjectArray slot enumeration and validation
// ============================================================

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "Ubel.h"   // For ::ClassInfo (defined at global scope in Ubel.h, despite the filename) used by WalkClassesBatch
#include "ValueScan.h"  // For ValueScan::Candidate / DataType / ScanType used by ScanForValue / RefineCandidates

// FUObjectItem structure (in FChunkedFixedUObjectArray)
// Size varies by UE version — auto-detected at Init() time:
//   UE5 (most):  16 bytes { Object*(8), Flags(4), SerialNumber(4) }
//   UE4 / some UE5: 24 bytes { Object*(8), Flags(4), ClusterRootIndex(4), SerialNumber(4), _pad(4) }
// Only the Object* field at +0x00 is used; the rest is stride padding.
struct FUObjectItem {
    uintptr_t Object;           // UObject* (always at +0x00)
    int32_t   Flags;
    int32_t   SerialNumber;
};

namespace Aura {

// === Encrypted GObjects Support (GAP #1) ===
// Some anti-cheat games encrypt the Objects pointer in FUObjectArray.
// Set a custom decryption function BEFORE calling Init().
// Default: nullptr (identity, zero overhead for non-encrypted games).
using DecryptFunc = uintptr_t(*)(uintptr_t rawPtr);
void SetDecryptFunc(DecryptFunc func);
uintptr_t DecryptObjectPtr(uintptr_t rawPtr);

// Initialize with the FUObjectArray address found by OffsetFinder
void Init(uintptr_t gobjectsAddr);

// Get total number of allocated objects
int32_t GetCount();

// Get max number of objects
int32_t GetMax();

// Get UObject* by index (returns 0 if invalid/null)
uintptr_t GetByIndex(int32_t index);

// Get FUObjectItem by index (returns nullptr if invalid)
FUObjectItem* GetItem(int32_t index);

// Read the SerialNumber of the FUObjectItem at the given index.
// Handles both 16-byte (serial@+0x0C) and 24-byte (serial@+0x10) items.
int32_t GetSerialNumber(int32_t index);

// Iterate all valid objects
// Callback: return false to stop iteration
void ForEach(std::function<bool(int32_t idx, uintptr_t obj)> cb);

// Find first object matching name (linear scan)
uintptr_t FindByName(const std::string& name);

// Find first object matching full path (linear scan)
uintptr_t FindByFullName(const std::string& fullName);

// Get the detected FUObjectItem stride in bytes (16 or 24)
int GetItemSize();

// Whether the GObjects array is a flat (non-chunked) FFixedUObjectArray.
// Flat arrays were used in UE4.11-4.20; chunked arrays in UE4.21+ and all UE5.
bool IsFlat();

// Search objects by partial name (case-insensitive), returns up to maxResults
struct SearchResult {
    uintptr_t addr;
    int32_t   index;       // InternalIndex in GObjects
    std::string name;
    std::string className;
    uintptr_t outer;
};

// Search results with diagnostic counters for debugging
struct SearchResultSet {
    std::vector<SearchResult> results;
    int32_t scanned = 0;    // Total indices iterated (= GetCount() at call time)
    int32_t nonNull = 0;    // Objects that were non-null
    int32_t named   = 0;    // Objects whose class name resolved successfully
};

SearchResultSet SearchByName(const std::string& query, int maxResults = 200);

// Find all instances whose class name matches (case-insensitive partial match)
// Returns addr, index, name, className, outer for each instance
SearchResultSet FindInstancesByClass(const std::string& className, bool exactMatch = false, int maxResults = 500);

// Address-to-Instance reverse lookup result.
//
// Confidence levels (worst to best):
//   exact      — addr IS a UObject pointer (highest confidence)
//   contains   — addr is within UObject + PropertiesSize (high)
//   backward   — backward memory scan found a UObject header (medium —
//                addr is past a NewObject<>'d sub-object not in GObjects)
//   nearest    — closest GObjects entry below addr; addr is BEYOND its
//                PropertiesSize so this is just a "best guess" hint, not a
//                real containment (low — frequently misleading)
struct AddressLookupResult {
    bool        found         = false;
    bool        exactMatch    = false;  // true = addr is a UObject, false = addr is inside a UObject
    std::string matchKind;              // "exact" / "contains" / "backward" / "nearest"
    uintptr_t   objectAddr    = 0;      // The owning UObject address
    int32_t     index         = -1;     // InternalIndex in GObjects
    std::string name;
    std::string className;
    uintptr_t   outer         = 0;
    int32_t     offsetFromBase = 0;     // addr - objectAddr (0 for exact match)
};

// Given an arbitrary address, find which UObject it belongs to.
// First tries exact match (is this address a UObject?), then containment
// (is this address inside a UObject's property data?).
AddressLookupResult FindByAddress(uintptr_t addr);

// === Container-Aware Address Lookup ===

// One match for an address that falls inside a UObject field's
// heap-allocated container buffer (TArray::Data / TSparseArray::Data).
struct ContainerMatch {
    uintptr_t   ownerObj      = 0;      // UObject that owns the container field
    int32_t     ownerIndex    = -1;     // GObjects index of owner
    std::string ownerName;
    std::string ownerClassName;
    int32_t     fieldOffset   = 0;      // Field offset within owner UObject
    std::string fieldName;
    std::string fieldType;              // "ArrayProperty" / "SetProperty" / "MapProperty"
    std::string innerType;              // Inner type label (Set: elem; Map: "K → V")
    int32_t     elementIndex  = 0;      // (addr - dataAddr) / stride (sparse index for Map/Set)
    int32_t     elementSize   = 0;      // Per-element / per-pair stride
    int32_t     intraOffset   = 0;      // (addr - elementStart) within element
    uintptr_t   dataAddr      = 0;      // TArray::Data / TSparseArray::Data base
    int32_t     count         = 0;      // Logical element count (allocated only for Map/Set)
    // Diagnostic note about match confidence:
    //   ""       — solid hit (within Count, allocated slot)
    //   "slack"  — Array index is in [Count, Max) — uninitialised / freed slack
    //   "freed"  — Map/Set slot is on the free list (stale data, may still match)
    std::string note;
};

// Diagnostic stats from a container scan — surfaced through the pipe so
// the UI can tell the user whether a "not found" was a complete scan
// or got cut off by the deadline.
struct ContainerScanStats {
    int32_t objectsScanned   = 0;   // UObjects iterated
    int32_t objectsTotal     = 0;   // Total in GObjects
    int32_t classesPrimed    = 0;   // Unique classes touched (cache built)
    int64_t durationMs       = 0;
    bool    deadlineHit      = false;
};

// Scan all UObjects' container fields for `addr`. Returns matches where
// addr falls in [Data, Data + bound). Covers ArrayProperty (TArray.Data,
// including slack slots), SetProperty (TSparseArray.Data, including freed
// slots), and MapProperty (TSparseArray.Data of TPair). Has an internal
// time deadline and per-class field cache; cache persists for the DLL
// lifetime so subsequent calls are much faster.
//
// `stats` (optional out param) receives diagnostic counters; if non-null
// the caller can detect a truncated scan via `deadlineHit`.
std::vector<ContainerMatch> FindInContainers(uintptr_t addr, int32_t maxResults = 16,
                                              ContainerScanStats* stats = nullptr);

// === Reverse Reference Search (logical-parent navigation) ===
//
// One match for a UObject that holds a pointer to the target UObject.
// Used to answer "what owns this Item?" — UE's `OuterPrivate` is a
// naming-hierarchy parent (often `/Engine/Transient` for runtime objects)
// rather than the logical gameplay parent. Reverse-scanning all UObjects'
// pointer fields and Object array elements gives the actual owner.
struct ReferenceMatch {
    uintptr_t   ownerObj      = 0;
    int32_t     ownerIndex    = -1;
    std::string ownerName;
    std::string ownerClassName;
    int32_t     fieldOffset   = 0;        // Absolute field offset within owner
    std::string fieldName;                // Dotted path (e.g. "Stats.Equipment");
                                          // Map matches append ".Key" / ".Value"
    std::string fieldType;                // "ObjectProperty" / "ClassProperty" /
                                          // "InterfaceProperty" / "WeakObjectProperty" /
                                          // "SoftObjectProperty" / "SoftClassProperty" /
                                          // "LazyObjectProperty" / "OptionalProperty" /
                                          // "DelegateProperty" /
                                          // "MulticastInlineDelegateProperty" /
                                          // "MulticastDelegateProperty" /
                                          // "MulticastSparseDelegateProperty" /
                                          // "ArrayProperty" / "MapProperty" / "SetProperty"
    std::string innerType;                // For Array: inner element type;
                                          // For Set: element type;
                                          // For Map: "<keyType> → <valueType>"
    int32_t     elementIndex  = -1;       // -1 for direct field, >=0 for array/
                                          // map/set element (sparse index for
                                          // Map/Set)
};

// Find UObjects that hold a pointer to `target`. Walks every UObject's:
//   - ObjectProperty / ClassProperty / InterfaceProperty (8B raw pointer)
//   - WeakObjectProperty / SoftObject{Class}Property / LazyObjectProperty
//     (resolves embedded FWeakObjectPtr; only matches when bound to live
//     UObject)
//   - OptionalProperty<T> for pointer-shaped T (Object/Class/Interface/
//     Weak/Soft/Lazy) — same comparison as the bare T at field+0
//   - DelegateProperty (single FScriptDelegate — FWeakObjectPtr target at
//     field+0 — surfaces "X is bound to a delegate on Y" relationships)
//   - MulticastInlineDelegateProperty / MulticastDelegateProperty
//     (FMulticastScriptDelegate := TArray<FScriptDelegate>; walks each
//     binding's FWeakObjectPtr target).
//   - MulticastSparseDelegateProperty (UE 5.0+ only) — global pass after
//     the per-object loop walks FSparseDelegateStorage::SparseDelegates
//     once and checks every binding's FWeakObjectPtr against `target`.
//     Skipped silently when AOB scan failed or UE < 5.0.
//   - TArray of any single-pointer type above (incl. TArray<FScriptDelegate>)
//   - TMap with Object/Class key and/or value (allocated slots only)
//   - TSet with Object/Class element (allocated slots only)
// Walks include fields nested inside StructProperty (depth 3).
//
// Has its own per-class metadata cache (separate from container cache);
// first call primes, subsequent calls are fast.
//
// `stats` mirrors ContainerScanStats — duration and deadline indication.
std::vector<ReferenceMatch> FindReferencesToUObject(uintptr_t target,
                                                     int32_t maxResults = 32,
                                                     ContainerScanStats* stats = nullptr);

// === Property Keyword Search ===

struct PropertyMatch {
    std::string className;        // The class this match was emitted from
                                  // (= definingClassName after dedup, since
                                  //  dedup keeps only the defining-class row)
    uintptr_t   classAddr = 0;
    std::string classPath;
    std::string superName;
    std::string propName;
    std::string propType;
    int32_t     propOffset = 0;
    int32_t     propSize   = 0;
    std::string structType;   // StructProperty -> inner struct name
    std::string innerType;    // ArrayProperty -> inner element type

    // === Inheritance-aware fields (build 610+) ===
    //
    // PropertySearch dedupes by (definingClass, propName, offset) so a
    // field declared on AActor and inherited by 4823 children only emits
    // one row. The defining class is the highest-up class in the
    // inheritance chain that actually declares the property; everything
    // below it inherits the same FProperty at the same offset, so writing
    // to that offset on any instance has identical effect.
    std::string definingClassName;     // Class where the FProperty is first declared
    uintptr_t   definingClassAddr = 0; // Address of that class
    std::string definingClassPath;     // Full path of the defining class (for game-vs-engine UI hint)
    int32_t     inheritedByCount = 0;  // Number of OTHER classes (excludes defining)
                                       // that inherit this field. 0 means
                                       // the property is unique to this class.

    // === Internal preview-resolution helper (not serialised) ===
    //
    // After dedup, classAddr / definingClassAddr point to the canonical
    // defining class (often abstract -- AActor / APawn / etc -- with no
    // direct instances). Phase 2 needs an actual non-abstract subclass
    // to find a representative instance. Track the most-derived
    // subclass we observed during the search loop so Phase 2 can find
    // instances even when the defining class is abstract.
    //
    // "Most derived" is approximated by largest PropertiesSize -- a
    // subclass with more bytes in its struct is presumed to be deeper
    // in the inheritance chain and more likely to have live instances
    // (concrete BP classes typically have more fields than the abstract
    // engine bases).
    uintptr_t   previewClassAddr      = 0;
    int32_t     previewPropertiesSize = 0;

    // Preview support — populated in Phase 2 of SearchProperties
    std::string preview;           // Inline value preview from a representative instance
    uintptr_t   fieldAddr   = 0;   // FField/FProperty address (for enum resolve)
    uint8_t     boolFieldMask  = 0; // BoolProperty: FieldMask byte
    uint8_t     boolByteOffset = 0; // BoolProperty: ByteOffset within property
    uintptr_t   enumAddr    = 0;   // EnumProperty: UEnum* for name resolution
    std::string keyType;           // MapProperty: key type name
    std::string valueType;         // MapProperty: value type name
};

struct PropertySearchResult {
    int scannedClasses = 0;
    int scannedObjects = 0;
    std::vector<PropertyMatch> results;
};

// Search for properties matching a keyword across all UClass objects.
// query: case-insensitive substring match on property name.
// typeFilter: optional list of property types (e.g. "FloatProperty"); empty = all types.
// gameOnly: skip engine packages (/Script/Engine, /Script/CoreUObject, etc.)
//
// Results are deduped by (definingClass, propName, offset) -- a field
// declared on AActor and inherited by 4823 children only emits one row,
// keyed by the defining class. The PropertyMatch.inheritedByCount
// records how many other classes share that inherited field.
PropertySearchResult SearchProperties(
    const std::string& query,
    const std::vector<std::string>& typeFilter,
    bool gameOnly,
    int maxResults = 200);

// Batched property search: walk GObjects + class fields ONCE and check
// every property against ALL queries. Returns one PropertySearchResult
// per query (in the same order as the input). Each query gets its own
// dedup index, per-query maxResults limit, and (optionally) per-query
// preview values.
//
// The big win: a 36-query sweep on a 4400-class game drops from
// ~42 sequential seconds (each call re-walks GObjects) to ~1.5 seconds
// (one shared walk; per-property keyword check is cheap).
//
// withPreviews=false skips the Phase-2 instance scan that resolves
// preview values for the wire output. The Interesting Properties tab
// (the primary consumer) doesn't show previews, so the default is off
// and we save another GObjects pass.
std::vector<PropertySearchResult> SearchPropertiesBatch(
    const std::vector<std::string>& queries,
    const std::vector<std::string>& typeFilter,
    bool gameOnly,
    int maxResultsPerQuery = 200,
    bool withPreviews = false);

// Batched class schema walk: invokes Ubel::WalkClassEx once per input
// address and returns results in the same order. The DLL implementation
// is deliberately a trivial loop — every element comes from the exact
// same WalkClassEx call the single-walk `walk_class` pipe command uses,
// so each ClassInfo is byte-identical to a single-call response. The
// optimisation is purely pipe round-trip + JSON serialisation
// amortisation: a 4000-class Full SDK export saves ~4000 × ~0.3ms of
// per-message overhead plus the per-call JSON envelope cost.
//
// Caller is responsible for chunking — a single batch carrying
// thousands of fully-walked classes would produce a multi-megabyte
// JSON payload, so the UI side fans out in ~200-class chunks.
// Note: ClassInfo is defined at global scope in Ubel.h (not inside the
// Ubel namespace), so the unqualified name is correct here.
std::vector<ClassInfo> WalkClassesBatch(const std::vector<uintptr_t>& addrs);

// Walk the SuperStruct chain upward from `classAddr` and return the
// highest-up class that still declares a property at `fieldOffset`.
// Algorithm: a class C declares the property iff
//   fieldOffset >= C.SuperStruct.PropertiesSize  (super doesn't have it)
//   fieldOffset <  C.PropertiesSize              (C does have it)
// If no super exists (UObject is the root), classAddr itself is the
// defining class.
//
// Used by SearchProperties dedup. Cap on chain depth (32) matches
// Ubel's WalkClass inherited-walk to avoid pathological cycles.
uintptr_t FindDefiningClass(uintptr_t classAddr, int32_t fieldOffset);

// === Game Class List ===

struct ClassListEntry {
    std::string className;
    uintptr_t   classAddr;
    std::string classPath;
    std::string superName;
    int32_t     propertyCount;
    int32_t     propertiesSize;
    int32_t     heuristicScore;   // Auto-ranked suspicion score (higher = more interesting for RE)
};

struct ClassListResult {
    int scannedObjects = 0;
    int totalClasses = 0;
    std::vector<ClassListEntry> results;
};

// List all UClass objects, optionally filtering out engine packages.
ClassListResult ListClasses(bool gameOnly, int maxResults = 5000);

// === All-Functions Enumeration (Interesting Functions Finder) ===

// Lightweight per-function metadata returned by EnumerateAllFunctions.
// Deliberately omits parameter details — the UI only needs enough to
// score + render a row; full param walk happens on-demand when the
// user picks a function (existing CMD_WALK_FUNCTIONS path).
struct AllFunctionEntry {
    std::string className;
    uintptr_t   classAddr   = 0;
    std::string superName;
    std::string classPath;        // full path for game_only / package filter
    std::string funcName;
    uintptr_t   funcAddr    = 0;
    uint32_t    functionFlags = 0;
    uint8_t     numParms    = 0;
    uint16_t    parmsSize   = 0;
};

struct AllFunctionsResult {
    int scannedObjects   = 0;     // GObjects count walked
    int scannedClasses   = 0;     // UClasses considered (post game-only filter)
    int totalFunctions   = 0;     // sum of WalkFunctions over all classes
    std::vector<AllFunctionEntry> entries;
};

// Walk every UClass in GObjects, enumerate its UFunctions, and return a
// flat list of {class, function, addr, flags, paramsSize} tuples.
//
// gameOnly: when true, skips classes whose path matches IsEnginePackage
//   (/Script/Engine, /Script/CoreUObject, etc.) -- typically reduces
//   the result set ~5x for shipping games.
// maxEntries: hard cap to keep the pipe payload bounded. Defaults to
//   100k (well above the ~50k-function ceiling of typical UE games).
//
// Cost is O(GObjects + sum(WalkFunctions)) with a single pass over
// GObjects to identify UClasses, plus one Ubel::WalkFunctions per
// class. Typical 1M-object game completes in 2-10s. UI should run
// this on a worker task with a progress indicator.
AllFunctionsResult EnumerateAllFunctions(bool gameOnly, int maxEntries = 100000);

// === Property Bytecode Cross-Reference (Path 1: Kismet bytecode static xref) ===
//
// Finds which UFunctions reference a target FProperty by scanning each
// function's UStruct::Script (Kismet bytecode) for the FProperty* pointer.
// Variable-access opcodes (EX_InstanceVariable 0x01 / EX_LocalVariable 0x00 /
// EX_Context inner) embed the live, fixed-up FProperty* directly, so a raw
// byte-scan for the 8-byte pointer value is complete AND version-agnostic
// (no opcode table; the 0x00/0x01 preceding byte is only a confidence/kind
// hint). The target address is the field's FProperty* (UProperty* on UE4
// <4.25) — exactly what WalkClassEx / SearchProperties report as fieldAddr.
//
// COVERAGE: Blueprint / script functions only. Native (FUNC_Native) functions
// have empty Script — their property access is in compiled machine code and is
// invisible here. The UI MUST surface this (mirror the value-search caveat
// contract); complementary to the CE access-breakpoint approach which covers
// native but drowns on shared/inlined code.
struct PropertyXref {
    uintptr_t   funcAddr       = 0;
    std::string funcName;
    std::string funcFullName;
    uintptr_t   ownerClassAddr = 0;
    std::string ownerClassName;
    int32_t     occurrences    = 0;    // hit count within this function's bytecode
    int32_t     writeCount     = 0;    // of those, how many are assignment destinations
                                       // (EX_Let* LHS). reads = occurrences - writeCount.
                                       // Best-effort: wrapped LHS (Other.Field/Struct.Member/
                                       // Arr[i] = x) is not detected and counts as a read.
    std::string kind;                  // "instance" (0x01) / "local" (0x00) / "ref"

    // v2a: for hits inside a Blueprint ubergraph (ExecuteUbergraph_*), the BP
    // event(s) whose entry offset precedes the reference (comma-joined, distinct).
    // Empty for non-ubergraph functions. Best-effort: shared sub-graphs reached
    // from multiple events can mis-attribute (nearest-preceding heuristic).
    std::string eventName;
    // Transient (not serialised): byte offsets of the FProperty* within the
    // ubergraph Script, used by the post-scan attribution pass then cleared.
    std::vector<int32_t> ubergraphOffsets;
};

struct PropertyXrefStats {
    int32_t functionsScanned    = 0;   // objects whose class name == "Function"
    int32_t functionsWithScript = 0;   // of those, Script.Num > 0
    int32_t objectsTotal        = 0;   // GObjects count
    int64_t durationMs          = 0;
    bool    deadlineHit         = false;
};

struct PropertyXrefResult {
    std::vector<PropertyXref> xrefs;
    PropertyXrefStats         stats;
};

// Scan every UFunction's bytecode for references to `propAddr` (an FProperty*).
// gameOnly skips functions whose owning class is an engine package. Parallel
// GObjects walk (relies on Ubel's mutex-guarded caches, build 792).
PropertyXrefResult FindPropertyXrefs(uintptr_t propAddr, bool gameOnly,
                                     int32_t maxResults = 200);

// === Reverse edge: function -> properties it reads/writes ===
//
// Given ONE UFunction, parse its Kismet bytecode and list every FProperty it
// references, with read/write classification. Opcode-anchored scan: for each
// variable/struct-member/persistent-frame opcode, the following 8-byte pointer
// is validated via Ubel::ResolvePropertyNameType (type must contain "Property")
// — so non-property pointers (UFunction*, literals) are rejected. Writes are
// detected with the same EX_Let* heuristic as FindPropertyXrefs.
//
// Blueprint/script functions only (native functions have empty bytecode).
// Best-effort: a write whose LHS is wrapped (Other.Field / Struct.Member /
// Arr[i] = x) may be reported as a read.
struct FunctionPropRef {
    uintptr_t   propAddr    = 0;
    std::string name;
    std::string type;          // "FloatProperty" / "StructProperty" / ...
    int32_t     occurrences = 0;
    int32_t     writeCount  = 0;   // reads = occurrences - writeCount
    std::string scope;             // "instance" (class member) / "local" (function
                                   // local/param) / "default" / "sparse" / "struct" /
                                   // "frame". UI defaults to instance-only so BP
                                   // compiler temporaries (CallFunc_*) don't drown it.
    // === Path 2 (native disasm) fields ===
    int32_t     offset     = -1;   // class-member offset the access mapped to (-1 = n/a)
    std::string confidence;        // "high" (base proven `this`) / "low" — disasm only;
                                   // empty for the exact bytecode path.
};

struct FunctionPropRefResult {
    int32_t scriptBytes = 0;       // UStruct::Script.Num (0 = native / empty)
    std::vector<FunctionPropRef> refs;
    // "bytecode" = Path 1 Kismet scan (exact). "disasm" = Path 2 native x64
    // disassembly (heuristic — see FunctionPropRef::confidence). "none" =
    // native but analysis unavailable (Func offset unresolved / unreadable).
    std::string method;
    int32_t unmappedAccesses = 0;  // disasm: [reg+off] hits with no matching property
};

FunctionPropRefResult WalkFunctionPropertyRefs(uintptr_t funcAddr);

// === Sparse Delegate Storage Walker ===
//
// Resolves bindings for a MulticastSparseDelegateProperty. The field on a
// UObject only stores `FSparseDelegate { uint8 bIsBound; }` — actual binding
// list lives in CoreUObject's static
//   FSparseDelegateStorage::SparseDelegates :
//     TMap<UObjectBase*, TMap<FName, TSharedPtr<TMulticastScriptDelegate>>>
//
// This walker locates the static via Genau::FindSparseDelegateStorage(),
// linearly scans the outer TSparseArray for the matching owner key, then
// scans the inner TSparseArray for the matching FName key, derefs the
// TSharedPtr, and walks the InvocationList.
//
// Version support: UE 5.0+ only. UE 4.23-4.27 used FObjectKey as outer
// key (different layout); walker returns supported=false on those.
struct SparseDelegateBinding {
    int32_t     objectIndex    = 0;   // raw FWeakObjectPtr.ObjectIndex
    int32_t     serialNumber   = 0;   // raw FWeakObjectPtr.SerialNumber
    uintptr_t   targetObj      = 0;   // resolved live UObject* (0 if stale)
    std::string targetName;           // resolved target name (empty if stale)
    std::string targetClassName;      // resolved target class name (empty if stale)
    std::string functionName;         // FName of bound function
};

struct SparseDelegateResult {
    bool resolved   = false;     // AOB worked + walker ran (may have 0 bindings)
    bool supported  = true;      // false = current UE version not supported
    bool ownerFound = false;     // outer key matched
    bool nameFound  = false;     // inner key matched
    std::vector<SparseDelegateBinding> bindings;
};

// Walk FSparseDelegateStorage to enumerate bindings for `fieldName` on
// `ownerObj`. Returns immediately if the AOB resolver hasn't found the
// static (resolved=false) or if UE version isn't supported.
SparseDelegateResult WalkSparseDelegateBindings(uintptr_t ownerObj,
                                                 const std::string& fieldName,
                                                 int32_t maxBindings = 64);

// === Value Search (CE-style First Scan / Next Scan workflow) ===
//
// Walks GObjects + UProperty metadata to find every UPROPERTY-declared
// field matching `dt` whose typed value satisfies the predicate. Each
// candidate is enriched with owning UObject + class + defining-class +
// field metadata via the same machinery FindByAddress / SearchProperties
// already use.
//
// Native C++ fields (non-UPROPERTY) are NOT visible to this scan — the
// UI is contractually required to surface this limitation. See
// `project_value_search_caveats` memory for the rationale and the
// TArray<T> crash-risk plan that gates the v2 expansion past primitives.

struct ValueScanStats {
    int32_t scannedClasses = 0;   // Unique classes with matching-type fields
    int32_t scannedObjects = 0;   // UObject instances iterated
    int64_t durationMs     = 0;
    bool    deadlineHit    = false;
};

struct ValueScanResult {
    std::vector<ValueScan::Candidate>       candidates;
    // Shared metadata pools the candidates index into (V3-A). Moved into
    // the ValueScan::Session alongside the candidates by SessionManager::Begin.
    std::vector<ValueScan::FieldDescriptor> descriptors;
    std::vector<ValueScan::InstanceRecord>  instances;
    ValueScanStats                          stats;
};

// First Scan: walk every UPROPERTY field matching `dt` across all
// UObject instances, applying the (st, targetBytes, target2Bytes,
// tolerance) predicate. Skips UClass meta-objects -- only live
// instances + CDOs are scanned.
//
// Numeric path: targetBytes / target2Bytes carry the predicate target.
// Vector path (FVector/FRotator/FTransform): same buffers, 12 bytes each.
// String path (FString/FName/FText): targetBytes/target2Bytes ignored;
// targetString carries the user's search string and caseSensitive
// controls the comparison.
//
// Valid scan types for first scan:
//   Numeric / Vector: Exact / Bigger / Smaller / Between.
//   String:           Exact / Contains / StartsWith / EndsWith.
// Pipe handler rejects invalid (dt, st) combinations upstream so the
// scan engine doesn't have to second-guess.
//
// `tolerance` only affects Float/Double and vector comparisons
// (CE-style rounded scan -- displays show "338" for a real float of
// 337.5, so users want to scan with +-0.5 slack). Integer + string
// types ignore it.
//
// Returns at most maxResults candidates; the scan also bails on a 15s
// deadline (stats.deadlineHit fires when this happens). Used by the
// Value Search tab.
//
// Multi-numeric meta path (dt == NumericNoByte): targetBytes /
// target2Bytes are ignored. Every word/dword/qword/float/double field
// is accepted; each is compared using its OWN resolved DataType against
// the matching entry in `multiTargets` (and `multiTargets2` for
// Between). A field whose width can't represent the value (no matching
// entry) is skipped. `multiTargets` must be non-null for this path.
ValueScanResult ScanForValue(
    ValueScan::DataType dt,
    ValueScan::ScanType st,
    const uint8_t*      targetBytes,
    const uint8_t*      target2Bytes,
    bool                gameOnly,
    int32_t             maxResults    = 100000,
    double              tolerance     = 0.0,
    const std::string&  targetString  = "",
    bool                caseSensitive = false,
    const ValueScan::NumericTargetSet* multiTargets  = nullptr,
    const ValueScan::NumericTargetSet* multiTargets2 = nullptr,
    // When false, the GObjects walk runs single-threaded (no worker threads
    // spawned) so concurrent cross-thread reads can't trip a game's anti-tamper.
    // Default true = full parallel scan (fast). Exposed via the pipe `parallel`
    // field on begin_value_scan and the Value Search "Parallel scan" toggle.
    bool                parallel      = true);

// Refine an existing candidate vector in place: re-read each
// candidate's bytes (or string, for FString/FName/FText DataTypes),
// apply the predicate, prune entries that no longer match. For
// prev-value scan types (Changed / Unchanged / Increased / Decreased)
// the candidate's prevValue/prevStr snapshot is used in place of the
// targeted inputs. Snapshots are updated to the latest-observed
// state on survivors so the NEXT prev-value refine compares against
// what was seen during THIS refine -- standard CE Next Scan semantics.
//
// Multi-numeric meta path (dt == NumericNoByte): each candidate's
// concrete DataType is re-resolved from its stored fieldType; targeted
// predicates compare against `multiTargets`/`multiTargets2` (matched by
// that width), prev-value predicates against the candidate's prevValue.
ValueScanStats RefineCandidates(
    ValueScan::DataType                          dt,
    ValueScan::ScanType                          st,
    const uint8_t*                               targetBytes,
    const uint8_t*                               target2Bytes,
    std::vector<ValueScan::Candidate>&           candidates,
    const std::vector<ValueScan::FieldDescriptor>& descriptors,
    double                                       tolerance     = 0.0,
    const std::string&                           targetString  = "",
    bool                                         caseSensitive = false,
    const ValueScan::NumericTargetSet*           multiTargets  = nullptr,
    const ValueScan::NumericTargetSet*           multiTargets2 = nullptr);

// ------------------------------------------------------------------
// Snapshot capture (experimental — Phase A1a). A type-agnostic, streamed
// capture of every numeric UPROPERTY of every (scoped) UObject, used by the
// UI to persist snapshots for diff / SPC / pivot. Stateless cursor
// pagination (mirrors GetCount/GetByIndex + get_object_list): each chunk
// walks [offset, offset+limit) GObjects indices. Reuses Ubel::WalkClassEx
// (cached) + ValueScan::SelectSnapshotNumericFields. Array elements are
// captured in Phase A1b.
// ------------------------------------------------------------------
struct SnapshotField {
    std::string name;
    int32_t     offset = 0;
    std::string type;       // "FloatProperty" / "IntProperty" / ... (declared type)
    std::string hex;        // little-endian raw bytes, hex (no 0x prefix)
};

// One element of a struct-array (Phase A1b). Carries an inner-key (e.g.
// FCargoSlot.ItemID = "Fuel") so the same logical slot joins across snapshots
// regardless of array reordering, plus the element's numeric inner fields.
struct SnapshotArrayElement {
    int32_t     index = 0;         // element position (fallback key when keyName empty)
    std::string keyName;           // inner-key field name (e.g. "ItemID"); "" if none
    std::string keyValue;          // rendered inner-key value (e.g. "Fuel" / "42")
    std::vector<SnapshotField> fields;  // numeric inner fields (e.g. Quantity)
};

struct SnapshotArray {
    std::string field;             // owning ArrayProperty name (e.g. "Cargo")
    std::vector<SnapshotArrayElement> elements;
};

struct SnapshotObject {
    int32_t     index = -1;        // GObjects index (stable in-session join key)
    uintptr_t   addr  = 0;         // session-local; for CE export handoff
    std::string name;              // FName (numeric suffix normalised UI-side)
    std::string className;         // owning UClass short name
    std::string outerClassName;    // immediate outer's UClass name (loose join)
    std::string path;              // Ubel::GetFullName (cross-session identity)
    std::vector<SnapshotField> fields;
    std::vector<SnapshotArray>  arrays;   // struct-array inner-key capture (A1b)
};

struct SnapshotChunkResult {
    int32_t total   = 0;   // GObjects count
    int32_t scanned = 0;   // indices iterated this chunk (advance offset by this)
    std::vector<SnapshotObject> objects;  // only objects with >=1 numeric field
};

// Capture a chunk of objects [offset, offset+limit) with their numeric
// scalar UPROPERTY values + struct-array element inner fields (inner-key
// capture). gameOnly skips engine-package classes. numericScope must be a
// multi-numeric meta type (NumericNoByte default / NumericAll); a non-meta
// type captures nothing. arrayCap bounds elements captured per struct array.
SnapshotChunkResult CaptureSnapshotChunk(int32_t offset, int32_t limit,
                                         bool gameOnly,
                                         ValueScan::DataType numericScope,
                                         int32_t arrayCap = 256);

} // namespace Aura
