# Pipe Protocol — JSON IPC Specification

Named pipe: `\\.\pipe\UE5DumpBfx`
Format: JSON, newline-delimited (one message per `\n`)
Direction: bidirectional — Request/Response + async push Events
Total commands: 31 (command name constants live in `dll/src/Renge.h`)

-----

## General Rules

- Every request carries an `"id"` (integer, caller-assigned).
- Every response echoes the same `"id"` and includes `"ok": true|false`.
- On failure: `"ok": false, "error": "message"`.
- On partial success: `"ok": true, "error": "message"` — check for `"error"` even when `ok` is true.
- All addresses are hex strings with no prefix (e.g. `"7FF600A12340"`) unless noted.
- Pagination advances by `"scanned"` (indices iterated), **not** by `objects.length` — null slots are skipped but still counted.

-----

## Commands (UI → DLL)

### Initialization & Info

```jsonc
// Initialize — returns UE version; DLL runs AOB scans internally on startup
{ "id": 1, "cmd": "init" }

// Get global pointer addresses
{ "id": 2, "cmd": "get_pointers" }

// Get total object count
{ "id": 3, "cmd": "get_object_count" }

// Get dynamically-detected DynOff values (diagnostics)
{ "id": 4, "cmd": "get_offsets" }
```

### Object Enumeration

```jsonc
// Paginated object list — advance by "scanned", not objects.length
{ "id": 5, "cmd": "get_object_list", "offset": 0, "limit": 200 }

// Single object detail
{ "id": 6, "cmd": "get_object", "addr": "7FF123456789" }

// Find object by full path
{ "id": 7, "cmd": "find_object", "path": "/Game/BP_Player.BP_Player_C" }

// Reverse address lookup: given any address, find the containing UObject
{ "id": 8, "cmd": "find_by_address", "addr": "7FF123456789" }

// Reverse reference scan (Find Refs v3): who holds a UObject* pointing at addr?
// Covers direct Object/Class/Interface, Weak/Soft/Lazy, OptionalProperty<pointer>,
// Delegate / MulticastInline / MulticastDelegate, TArray of all of those,
// TMap<UObject*, V> / TMap<K, UObject*>, TSet<UObject*>. Excludes
// MulticastSparseDelegate (storage is external to the field).
{ "id": 30, "cmd": "find_refs_to_uobject", "addr": "7FF123456789", "max_results": 32 }

// Forward path search ("Locate in GWorld"): shortest pointer chain GWorld → target.
{ "id": 31, "cmd": "find_path_from_gworld", "target": "7FF123456789", "object_addr": "7FF123456789", "max_depth": 5 }
```

### Class & Instance Walking

```jsonc
// Walk all FFields of a UClass (static schema, no instance required)
{ "id": 9, "cmd": "walk_class", "addr": "7FF123456789" }

// Walk all UFunctions of a UClass (returns signatures + struct sub-field layouts)
{ "id": 20, "cmd": "walk_functions", "addr": "7FF123456789" }

// Walk live field values of a UObject instance
// class_addr is optional (auto-resolved from UObject::ClassPrivate)
// array_limit: max inline array elements (default 64)
// preview_limit: max struct sub-fields in preview (0=none, default 2, max 6)
{ "id": 10, "cmd": "walk_instance", "addr": "7FF123456789" }
{ "id": 10, "cmd": "walk_instance", "addr": "7FF123456789", "class_addr": "7FF...", "preview_limit": 2 }

// Walk GWorld → PersistentLevel → Actors
{ "id": 11, "cmd": "walk_world" }

// Find all instances of a class by name
{ "id": 12, "cmd": "find_instances", "class_name": "BP_Player_C", "limit": 100 }
```

### Array Reading

```jsonc
// Read array elements (paginated) — Phase B+ for scalar/pointer/struct arrays
{
  "id": 13, "cmd": "read_array_elements",
  "addr": "7FF6BB123000",         // UObject instance address
  "field_offset": 256,             // byte offset of the TArray field within the instance
  "inner_addr": "7FF601234560",   // FProperty* of the inner element type
  "inner_type": "FloatProperty",
  "elem_size": 4,
  "offset": 0,                    // pagination start
  "limit": 64                     // max elements to return
}
```

### Memory Access

```jsonc
// Read raw memory (returns hex string)
{ "id": 14, "cmd": "read_mem", "addr": "7FF123456789", "size": 256 }

// Write raw memory
{ "id": 15, "cmd": "write_mem", "addr": "7FF123456789", "bytes": "3F800000" }

// Subscribe to address for periodic push (Live Watch)
{ "id": 16, "cmd": "watch", "addr": "7FF123456789", "size": 4, "interval_ms": 500 }

// Unsubscribe
{ "id": 17, "cmd": "unwatch", "addr": "7FF123456789" }
```

### Search & Enumeration

```jsonc
// Multi-pattern object search (substring match on name/class/path)
{ "id": 19, "cmd": "search_objects", "query": "Player", "limit": 200 }

// Search properties by name across all classes
{ "id": 20, "cmd": "search_properties", "query": "Health", "limit": 100 }

// List all classes (UClass objects)
{ "id": 21, "cmd": "list_classes", "limit": 500 }

// List all enum definitions
{ "id": 22, "cmd": "list_enums" }
```

### DataTable

```jsonc
// Walk DataTable rows (RowMap probe, returns row keys + addresses)
{ "id": 23, "cmd": "walk_datatable_rows", "addr": "7FF123456789" }
```

### Rescan & Scan Control

```jsonc
// Trigger background rescan of global pointers (non-blocking)
{ "id": 24, "cmd": "rescan" }

// Query rescan progress
{ "id": 25, "cmd": "rescan_status" }

// Apply rescanned pointers (replaces current GObjects/GNames/GWorld)
{ "id": 26, "cmd": "apply_rescan" }

// Trigger full re-initialization (proxy DLL deferred scan)
{ "id": 27, "cmd": "trigger_scan" }

// Query scan progress feedback
{ "id": 28, "cmd": "scan_status" }
```

### CE Export

```jsonc
// Get CE-compatible XML pointer chain for an instance
{ "id": 18, "cmd": "get_ce_pointer_info", "addr": "7FF123456789", "class_addr": "7FF..." }
```

### UFunction Invocation

```jsonc
// Invoke ProcessEvent via pipe (bypasses CE executeCodeEx)
{ "id": 42, "cmd": "invoke_function", "func_name": "Attack", "instance_addr": "0x7FF...", "parms_size": 16, "params_hex": "3F800000" }
```

### Debug Camera (robust force on/off)

Shared by the Console panel (here) and CE Lua (`setDebugCamera` export). All logic — two-hop state read, ToggleDebugCamera invoke, and controller-swap fallback for Shipping builds that strip `DisableDebugCamera` — lives in the DLL (`UE5_GetDebugCameraState` / `UE5_SetDebugCamera`). `state`: `1` = ON, `0` = OFF, `-1` = unknown/error.

```jsonc
// Read live state
{ "id": 50, "cmd": "get_debug_camera_state" }
// → { "ok": true, "state": 1 }

// Force ON (enable:true) / OFF (enable:false); idempotent
{ "id": 51, "cmd": "set_debug_camera", "enable": false }
// → { "ok": true, "state": 0 }
```

### Value Search (build 738 + Phase 2 build 757)

CE-style First Scan / Next Scan workflow over UPROPERTY fields. Three commands form a session: `begin_value_scan` opens, `refine_value_scan` narrows, `end_value_scan` closes. Sessions auto-expire after 5 min idle.

```jsonc
// First Scan — open a new session, return enriched candidates.
// data_type: Int8/Int16/Int32/Int64/UInt8/UInt16/UInt32/UInt64/Float/Double/Bool
//          | FString/FName/FText           (Phase 2A — case-insensitive default)
//          | FVector/FRotator/FTransform   (Phase 2B — FTransform reserved, 0 hits pending Translation offset)
// scan_type for numeric/vector: Exact/Bigger/Smaller/Between
// scan_type for string:         Exact/Contains/StartsWith/EndsWith
// value:    string-encoded target (e.g. "100", "3.14", "true", "Engine", "100,200,300" CSV for vectors)
// value2:   second target for Between (numeric/vector only)
// tolerance: float-only — applies to Float/Double + vector types (per-axis); omitted for integer/string
// case_sensitive: string types only — omitted unless true (CE-style default is insensitive)
// parallel: omitted unless false. false forces a single-threaded GObjects walk
//           (slower, but avoids the burst of concurrent cross-thread reads some
//           games' anti-tamper flags). First Scan only — refine is always serial.
// batch_read: omitted unless false. false forces one SEH read per field instead
//           of one per-object body read (the DLL default, which is faster — fewer
//           reads + better locality, with automatic per-field fallback).
{
  "id": 50, "cmd": "begin_value_scan",
  "data_type": "FString",
  "scan_type": "Contains",
  "value":     "Engine",
  "game_only": true,
  "max_results": 50000,
  "case_sensitive": false,      // optional, string types only
  "parallel": false,            // optional, default true (omitted when parallel)
  "batch_read": false           // optional, default true (omitted when batching)
}

// Next Scan — refine candidates in an open session.
// scan_type may switch between targeted (Exact/Contains/...) and prev-value
// (Changed/Unchanged/Increased/Decreased). value/value2 omitted for prev-value types.
{
  "id": 51, "cmd": "refine_value_scan",
  "session_id": 1234,
  "scan_type":  "Decreased"
}

// End — drop the session. Idempotent (returns ok=true even when already expired).
{ "id": 52, "cmd": "end_value_scan", "session_id": 1234 }
```

**Wire-shape contract** (locked by tests):
- `tolerance` is attached only when non-zero AND the data type is Float/Double/FVector/FRotator/FTransform. Integer + string sessions never carry it; the DLL ignores it for those anyway.
- `case_sensitive` is attached only when true AND the data type is FString/FName/FText.
- `parallel` is attached only when **false** (the DLL default is true / full parallel). `false` caps the GObjects walk to one worker thread; the UI exposes it as the default-ON "Parallel scan" toggle for anti-tamper-sensitive games.
- `batch_read` is attached only when **false** (DLL default true). `false` forces one SEH read per field; default batches each object's fixed-width leaf fields into a single body read (per-thread reused buffer, span-capped, with per-field fallback on fault). Strings + container data are always read directly. UI = default-ON "Batch read" toggle.
- `(data_type, scan_type)` combinations are validated server-side by `IsScanTypeValidFor` — `FString + Bigger` or `Int32 + Contains` return an explicit error rather than running with garbage semantics.

### Snapshot Capture (experimental — Phase A)

Type-agnostic streamed capture of every numeric UPROPERTY of every (scoped) UObject, for the experimental Snapshot / SPC / Pivot tabs. **Stateless cursor pagination** (mirrors `get_object_list`): no server-side session. `begin_snapshot` returns the total object count for a progress bar; `snapshot_chunk` streams `[offset, offset+limit)` objects. Advance `offset` by the returned `scanned` (indices iterated), NOT by `objects.length` (objects with zero numeric fields are skipped). Phase A1a captures scalar numeric fields only; array elements arrive in A1b.

```jsonc
// Begin — validate scope, return total object count.
// data_type: NumericNoByte (default) | NumericAll. Must be a multi-numeric
//            meta type; the structured walk compares each field by its own
//            declared width (no byte-reinterpret). NumericNoByte excludes
//            1-byte families to avoid flooding.
{ "id": 60, "cmd": "begin_snapshot", "data_type": "NumericNoByte" }

// Chunk — stream the next window of objects.
// array_cap bounds struct-array elements captured per array (default 256).
{ "id": 61, "cmd": "snapshot_chunk",
  "data_type": "NumericNoByte",
  "game_only": true,
  "offset":    0,
  "limit":     100,
  "array_cap": 256 }
```

Each chunk object may also carry an `arrays` field (Phase A1b) — struct-array
inner-key capture for cargo/inventory cases. Each element has an inner key
(`key_name`/`key_value`, e.g. `ItemID`=`Fuel`) so the same logical slot joins
across snapshots regardless of reordering, plus its numeric inner fields:

```jsonc
"arrays": [
  { "field": "Cargo",
    "elements": [
      { "i": 0, "key_name": "ItemID", "key_value": "Fuel",
        "fields": [ { "name": "Quantity", "off": 8, "type": "IntProperty", "hex": "64000000" } ] }
    ] }
]
```

-----

## Responses (DLL → UI)

### init

```jsonc
{ "id": 1, "ok": true, "ue_version": 507 }
// ue_version: 507=UE5.7, 505=UE5.5, 427=UE4.27, 422=UE4.22, etc.
```

### get_pointers

```jsonc
{
  "id": 2, "ok": true,
  "gobjects":     "7FF600A12340",
  "gnames":       "7FF600B56780",
  "gworld":       "7FF600C89ABC",   // may be "0" if not found
  "object_count": 58432,
  "module_name":  "MyGame-Win64-Shipping.exe",
  "module_base":  "7FF600000000",
  "ue_version":   504,
  "gobjects_method": "aob",         // "aob", "data_scan", "string_ref", "pointer_scan", "not_found"
  "gnames_method":   "string_ref",
  "gworld_method":   "not_found",
  // AOB Usage Tracking (added v1.1)
  "pe_hash":              "5F3A1B2CCDD40000",  // TimeDateStamp(8hex) + SizeOfImage(8hex)
  "gobjects_pattern_id":  "GOBJ_V1",           // winning pattern ID, "" if not AOB
  "gnames_pattern_id":    "",
  "gworld_pattern_id":    "",
  "scan_stats": {
    "gobjects_tried": 40,    // patterns evaluated
    "gobjects_hit":   3,     // patterns with >=1 match
    "gnames_tried":   27,
    "gnames_hit":     0,
    "gworld_tried":   37,
    "gworld_hit":     0
  }
}
```

### get_object_list

```jsonc
{
  "id": 5, "ok": true,
  "total":   58432,
  "scanned": 200,      // ← indices iterated; advance offset by this, NOT by objects.length
  "objects": [
    {
      "addr":  "7FF123456000",
      "name":  "BP_Player_C_0",
      "class": "BlueprintGeneratedClass",
      "outer": "7FF123400000"
    }
  ]
}
```

### begin_snapshot / snapshot_chunk

```jsonc
// begin_snapshot
{ "id": 60, "ok": true, "total": 58432 }

// snapshot_chunk — one entry per object with >=1 numeric field.
// "index" is the GObjects index (stable in-session join key). "path" is the
// full object path (cross-session identity; UI normalises the FName suffix).
// "off" is the field byte offset; "hex" is the little-endian raw bytes.
{
  "id": 61, "ok": true,
  "total":   58432,
  "scanned": 100,          // ← advance offset by this, NOT objects.length
  "objects": [
    {
      "index":       12345,
      "addr":        "0x7FF123456000",
      "name":        "BP_Player_C_0",
      "class":       "BP_Player_C",
      "outer_class": "World",
      "path":        "/Game/Maps/Map.Map:PersistentLevel.BP_Player_C_0",
      "fields": [
        { "name": "Health", "off": 720, "type": "FloatProperty", "hex": "0000C842" },
        { "name": "Ammo",   "off": 728, "type": "IntProperty",   "hex": "1E000000" }
      ]
    }
  ]
}
```

### walk_class

```jsonc
{
  "id": 9, "ok": true,
  "class": {
    "name":       "BP_Player_C",
    "full_path":  "/Game/BP_Player.BP_Player_C",
    "addr":       "7FF123456000",
    "super_addr": "7FF123450000",
    "super_name": "Character",
    "props_size": 1024,
    "fields": [
      {
        "addr":   "7FF601234000",
        "name":   "Health",
        "type":   "FloatProperty",
        "offset": 720,
        "size":   4
      },
      {
        "addr":   "7FF601234020",
        "name":   "Inventory",
        "type":   "ArrayProperty",
        "offset": 728,
        "size":   16
      }
    ]
  }
}
```

### walk_instance

Field objects include all `walk_class` fields **plus** live typed values and array element data.

```jsonc
{
  "id": 10, "ok": true,
  "addr":        "7FF6AA000000",
  "name":        "BP_Player_C_0",
  "class":       "BP_Player_C",
  "class_addr":  "7FF123456000",
  "outer":       "7FF6BB000000",
  "outer_name":  "ThirdPersonMap",
  "outer_class": "World",
  "fields": [
    // --- Scalar field ---
    {
      "name":   "Health",
      "type":   "FloatProperty",
      "offset": 720,
      "size":   4,
      "hex":    "0000C842",
      "value":  "100.0000000000"
    },
    // --- BoolProperty (bit field) ---
    {
      "name":          "bIsDead",
      "type":          "BoolProperty",
      "offset":        724,
      "size":          1,
      "hex":           "00",
      "value":         "false",
      "bool_mask":     4,
      "bool_bit_idx":  2
    },
    // --- ObjectProperty (pointer) ---
    {
      "name":      "WeaponComponent",
      "type":      "ObjectProperty",
      "offset":    728,
      "size":      8,
      "hex":       "0050AA6F0C020000",
      "value":     "7FF20C6FAA5000",
      "ptr_name":  "BP_Weapon_C_3",
      "ptr_class": "BP_Weapon_C"
    },
    // --- EnumProperty ---
    {
      "name":       "MovementMode",
      "type":       "EnumProperty",
      "offset":     736,
      "size":       4,
      "hex":        "02000000",
      "value":      "2",
      "enum_name":  "EMovementMode::Walking"
    },
    // --- StrProperty (FString) ---
    {
      "name":       "PlayerTag",
      "type":       "StrProperty",
      "offset":     740,
      "size":       16,
      "hex":        "...",
      "str_value":  "Hero_01"
    },
    // --- ArrayProperty: scalar inner type (Phase B inline elements) ---
    {
      "name":             "DamageMultipliers",
      "type":             "ArrayProperty",
      "offset":           756,
      "size":             16,
      "hex":              "000001A0B4C00000 00000005 00000005",
      "count":            5,
      "array_inner_type": "FloatProperty",
      "array_elem_size":  4,
      "array_inner_addr": "7FF601234560",
      "elements": [
        { "i": 0, "v": "1.5000000000", "h": "0000C03F" },
        { "i": 1, "v": "2",            "h": "00000040" },
        { "i": 2, "v": "0.5000000000", "h": "0000003F" }
      ]
      // "elements" only present for scalar arrays with count <= 64
      // For enum inner type, each element also has "en": "EnumName::Value"
    },
    // --- ArrayProperty: NameProperty inner (Phase B) ---
    {
      "name":             "MissionIDs",
      "type":             "ArrayProperty",
      "offset":           772,
      "size":             16,
      "count":            30,
      "array_inner_type": "NameProperty",
      "array_elem_size":  8,
      "array_inner_addr": "7FF601234580",
      "elements": [
        { "i": 0, "v": "S001", "h": "..." },
        { "i": 1, "v": "S002", "h": "..." }
      ]
    },
    // --- ArrayProperty: struct inner type (no inline elements) ---
    {
      "name":                  "LevelCollections",
      "type":                  "ArrayProperty",
      "offset":                788,
      "size":                  16,
      "count":                 3,
      "array_inner_type":      "StructProperty",
      "array_inner_struct_type": "LevelCollection",
      "array_elem_size":       120,
      "array_inner_addr":      "7FF6012345A0",
      "array_inner_struct_addr": "7FF601234600"
      // no "elements" — Phase F scope
    }
  ]
}
```

### walk_world

```jsonc
{
  "id": 11, "ok": true,
  "world_addr": "7FF6CC000000",
  "world_name": "ThirdPersonMap",
  "level_addr": "7FF6DD000000",
  "actors": [
    { "addr": "7FF6AA000000", "name": "BP_Player_C_0",  "class": "BP_Player_C"  },
    { "addr": "7FF6AB000000", "name": "BP_Enemy_C_0",   "class": "BP_Enemy_C"   }
  ]
}

// Partial success (GWorld null, UWorld found via GObjects fallback):
{ "id": 11, "ok": true, "world_addr": "...", "actors": [...], "error": "GWorld=0, found via GObjects fallback" }

// GWorld failure (CDO or no UWorld instance):
{ "id": 11, "ok": true, "actors": [], "error": "PersistentLevel is null (CDO or uninitialized)" }
```

### find_instances

```jsonc
{
  "id": 12, "ok": true,
  "class_name":    "BP_Player_C",
  "total_scanned": 58432,
  "instances": [
    {
      "addr":  "7FF6AA000000",
      "name":  "BP_Player_C_0",
      "class": "BP_Player_C",
      "outer": "7FF6BB000000"
    }
  ]
}
```

### find_by_address

```jsonc
// Exact match (query addr == UObject base)
{
  "id": 8, "ok": true, "found": true, "match_type": "exact",
  "addr":            "7FF123456000",
  "index":           12345,
  "name":            "BP_Player_C_0",
  "class":           "BP_Player_C",
  "outer":           "7FF6BB000000",
  "offset_from_base": 0,
  "query_addr":      "7FF123456000"
}

// Contains match (query addr is inside a UObject)
{
  "id": 8, "ok": true, "found": true, "match_type": "contains",
  "addr":            "7FF123456000",
  "index":           12345,
  "name":            "BP_Player_C_0",
  "class":           "BP_Player_C",
  "outer":           "7FF6BB000000",
  "offset_from_base": 1929,
  "query_addr":      "7FF123456789"
}

// Not found
{ "id": 8, "ok": true, "found": false }
```

**Container-aware lookup.** Request may set `"scan_containers": true` to also
attribute addresses that fall inside a UObject's heap-allocated container buffer
(TArray/TSet/TMap data — these don't fall within any UObject's PropertiesSize).
`"container_depth": N` (default 1 = shallow only) opts into a **recursive deep
descent**: when the fast shallow scan finds nothing, the DLL descends struct-array
/ map-value / set elements up to depth N to locate values in *separately-allocated*
nested containers (e.g. a `TArray<int>` whose header is inline in a struct element
but whose data lives elsewhere). `"container_elem_cap": M` (default 256) caps how
many elements are probed per container during that descent (UI-configurable via the
Options flyout). The deep scan runs only on a shallow miss (common case stays
fast), is bounded by the element cap + the 15s deadline, and early-outs on the
first match.

```jsonc
// Container match(es). Shallow 1-level hit has no "nested_chain"; a deeply-nested
// value carries the full chain (outermost stays in the match fields, each deeper
// hop in nested_chain; the last hop's intra_offset locates the value).
{
  "id": 8, "ok": true, "found": false,
  "query_addr": "228F1251BE8",
  "container_scan": {
    "objects_scanned": 28116, "objects_total": 28116,
    "classes_primed": 4382, "duration_ms": 51,
    "deadline_hit": false, "deep_scan": true
  },
  "container_matches": [
    {
      "owner_addr": "2294EDBE830", "owner_index": 17231,
      "owner_name": "BP_LifeSaveData_C", "owner_class": "BP_LifeSaveData_C",
      "field_offset": 1240, "field_name": "SaveSlotList", "field_type": "ArrayProperty",
      "inner_type": "StructProperty", "element_index": 1, "element_size": 1280,
      "intra_offset": 0, "data_addr": "226CD6A5000", "count": 4,
      "nested_chain": [
        { "field_name": "MsTuneData.MsTunes", "field_type": "MapProperty",
          "element_index": 0, "element_size": 96, "intra_offset": 0,
          "data_addr": "...", "map_value_side": true },
        { "field_name": "WeaponTuneList", "field_type": "ArrayProperty",
          "element_index": 0, "element_size": 64, "intra_offset": 0, "data_addr": "..." },
        { "field_name": "Tunes", "field_type": "ArrayProperty", "inner_type": "IntProperty",
          "element_index": 42, "element_size": 4, "intra_offset": 0, "data_addr": "228F1251B40" }
      ]
    }
  ]
}
```

### find_refs_to_uobject

Reverse reference scan. `references[]` lists each UObject that holds a pointer
to the target via a reflected field (or a container slot). Map matches set
`field_name` to `<owningField>.Key` or `.Value`; array/set element matches
populate `element_index` (otherwise `-1`).

```jsonc
{
  "id": 30, "ok": true,
  "query_addr": "7FF6AA000000",
  "scan": {
    "objects_scanned": 1180536,
    "objects_total":   1180536,
    "classes_primed":  6234,
    "duration_ms":     224,
    "deadline_hit":    false
  },
  "references": [
    {
      "owner_addr":   "7FF6BB100000",
      "owner_index":  98231,
      "owner_name":   "BP_PlayerState_C_0",
      "owner_class":  "BP_PlayerState_C",
      "field_offset": 0x2A8,
      "field_name":   "ActiveAbilities",
      "field_type":   "ArrayProperty",
      "inner_type":   "ObjectProperty",
      "element_index": 3
    }
  ]
}
```

Cache is per-class and persists for DLL lifetime — a cold scan on a 1.18M-object
game is typically ~200-300ms; warm scans are ~70ms. Hard deadline is 30s
(`deadline_hit: true` indicates the scan was truncated and the UI should offer
a re-run after warm-up).

### find_path_from_gworld

Forward object-graph path search ("Locate in GWorld") — the inverse of
`find_refs_to_uobject`. Computes the SHORTEST (fewest-hop) pointer chain from the
live `UWorld` (GWorld) down to a target, by breadth-first walking the same
outgoing object-pointer edges the reverse search uses (direct Object/Class/
Interface, Weak/Soft/Lazy, TArray/TMap/TSet of objects, and fields nested in
StructProperty to depth 3). Reuses the per-class reference-metadata cache.

Request:

```jsonc
{
  "id": 31, "cmd": "find_path_from_gworld",
  "target": "0x7FF6BB100000",     // address to locate (a UObject, or a value inside one)
  "object_addr": "0x7FF6BB100000",// OPTIONAL — the owning UObject if the caller already
                                  //   knows it (Value Search / Instance Finder); skips the
                                  //   FindByAddress resolution scan
  "max_depth": 5                  // max pointer hops from GWorld (default 5; hard-capped 32)
}
```

Response (`steps` is the path `root → steps[0].to → … → target_obj`; empty when
the target IS the root). When no path exists within the depth budget, `found` is
false and `status` explains why:

```jsonc
{
  "id": 31, "ok": true,
  "found": true,
  "status": "ok",               // ok / not_reachable / deadline / cancelled / no_gworld / invalid_target / visited_cap
  "root_addr":  "0x7FF6AA000000",
  "root_name":  "World_0",
  "target_obj": "0x7FF6BB100000",
  "target_name":  "BP_PlayerState_C_0",
  "target_class": "BP_PlayerState_C",
  "target_intra_offset": 0,      // (value addr - target_obj); >0 when target was a value inside the object
  "max_depth": 5,
  "depth":     4,                // hop count (== steps.length)
  "visited":   18342,            // distinct objects discovered
  "duration_ms": 120,
  "steps": [
    { "from": "0x7FF6AA000000", "to": "0x7FF6AA001000",
      "field_offset": 0x30, "field_name": "PersistentLevel",
      "field_type": "ObjectProperty", "element_index": -1,
      "to_name": "PersistentLevel", "to_class": "Level" },
    { "from": "0x7FF6AA001000", "to": "0x7FF6AA002000",
      "field_offset": 0x98, "field_name": "Actors",
      "field_type": "ArrayProperty", "inner_type": "ObjectProperty", "element_index": 12,
      "to_name": "BP_PlayerController_C_0", "to_class": "BP_PlayerController_C" }
    // … → target_obj
  ]
}
```

The UI replaces the Live Walker breadcrumb spine with this path. For a property
VALUE it lands on `target_obj` and scrolls to the value field; for an OBJECT /
class instance it stops at the parent (drops the final node) and highlights the
pointer field, without drilling into the target. BFS first-hit == shortest hops.
20s deadline; also bails on `Cancel::Requested()` (pipe disconnect / shutdown).
MulticastSparseDelegateProperty edges are intentionally NOT followed (their
bindings live in a CoreUObject-global TMap — a per-node global walk would be
prohibitively expensive).

### get_offsets

```jsonc
{
  "id": 4, "ok": true,
  "offsets": {
    "ustruct_super":          64,
    "ustruct_children":       72,
    "ustruct_childprops":     80,
    "ustruct_propssize":      88,
    "ffield_class":           8,
    "ffield_next":            32,
    "ffield_name":            40,
    "fproperty_elemsize":     56,
    "fproperty_flags":        64,
    "fproperty_offset":       76,
    "uobject_outer":          32,
    "case_preserving_name":   false,
    "use_fproperty":          true,
    "offsets_validated":      true
  }
}
```

### read_array_elements

```jsonc
{
  "id": 13, "ok": true,
  "total":      128,
  "read":       64,
  "inner_type": "FloatProperty",
  "elem_size":  4,
  "elements": [
    { "i": 0, "v": "100.5000000000", "h": "0000C842" },
    { "i": 1, "v": "200",            "h": "00004843" }
  ]
}
```

### get_ce_pointer_info

Builds a CE pointer chain (`ce_base` + `ce_offsets`) for a GObjects instance. Under the
UE5.7+ packed FUObjectItem layout a native CE chain cannot reconstruct the bit-packed
object pointer, so the response degrades to the absolute object address and sets
`packed_layout:true` + a `warning` (the chain won't survive a restart / ASLR rebase). The
direct-layout item hop includes `Aura::GetItemObjOffset()` so it dereferences the Object
pointer at its real within-item offset (+0x00 classic, +0x08 UE5.7+ unpacked).

```jsonc
// Direct (classic / unpacked57): full GObjects → chunk → item → field chain
{ "id": 18, "ok": true, "packed_layout": false,
  "ce_base": "\"Game.exe\"+1BA1820",
  "ce_offsets": [64, 264, 24, 0] }            // [field, withinChunk*itemSize+objOff, chunkIndex*8, 0]

// Packed57 (UNVERIFIED): degraded to the absolute object address
{ "id": 18, "ok": true, "packed_layout": true,
  "warning": "UE5.7+ packed FUObjectItem layout (UNVERIFIED): ... absolute address only ...",
  "ce_base": "0x1F809E08FB0", "ce_offsets": [64] }
```

### set_packed_consts

Runtime calibration / force-enable for the UE5.7+ **UNVERIFIED** packed FUObjectItem
reconstruction (no DLL rebuild). Leave a field unchanged with `align_bits<=0` /
`ptr_mask_bits=="0x0"` / `serial_off<0`. `force:true` switches the live layout to packed
unconditionally. Echoes the resulting mode + reconstructed `GObjects[0..7]` samples for
eyeball calibration (tweak constants until names look like real UObjects).

```jsonc
// Request
{ "id": 60, "cmd": "set_packed_consts",
  "align_bits": 3, "ptr_mask_bits": "0x3FFF", "force": true, "serial_off": 12 }

// Response
{ "id": 60, "ok": true,
  "item_packed": true, "item_layout_mode": "packed57", "item_obj_offset": 0, "item_size": 24,
  "samples": [ { "index": 0, "addr": "0x1F800000000", "name": "CoreUObject" }, ... ] }
```

> `get_pointers` (and `get_offsets`) additionally carry `item_layout_mode` /
> `item_packed` / `item_obj_offset` / `item_size` so the UI can flag the unverified
> packed mode (badge + export notes).

### read_mem / write_mem

```jsonc
// read_mem response
{ "id": 14, "ok": true, "bytes": "48 8B 05 AB CD EF 12 ..." }

// write_mem response
{ "id": 15, "ok": true }
```

### walk_functions

Walk all UFunctions of a UClass. Returns function signatures with parameters,
including StructProperty sub-field layouts discovered by walking the UScriptStruct.

```jsonc
// Request
{ "id": 20, "cmd": "walk_functions", "addr": "7FF123456789" }

// Response
{
  "id": 20, "ok": true,
  "count": 1,
  "functions": [
    {
      "name": "SetAttribute",
      "full": "Function /Script/Game.Character.SetAttribute",
      "addr": "0x7FF601234500",
      "flags": 67109120,
      "num_parms": 1,
      "parms_size": 8,
      "ret_offset": 65535,
      "ret": "",
      "params": [
        {
          "name": "NewValue",
          "type": "StructProperty",
          "size": 8,
          "offset": 0,
          "out": false,
          "ret": false,
          "struct_type": "GameplayAttributeData",
          "struct_fields": [
            { "name": "BaseValue", "type": "FloatProperty", "offset": 0, "size": 4 },
            { "name": "CurrentValue", "type": "FloatProperty", "offset": 4, "size": 4 }
          ]
        }
      ]
    }
  ]
}
```

**`struct_fields`** (optional): Present only for `StructProperty` params where the DLL
successfully walked the UScriptStruct's FField chain. Used by the UI as fallback when
`KnownStructLayouts` has no hardcoded definition for the struct type. Each sub-field
includes name, type, byte offset within the struct, and size. Nested StructProperty
sub-fields are not recursively expanded (Phase B scope).

### invoke_function

Invoke a UFunction via ProcessEvent. The DLL executes in-process, bypassing CE's
`executeCodeEx` (which uses `CreateRemoteThread` and is blocked by some games).

**Game-thread dispatch:** When available, the DLL hooks ProcessEvent with MinHook
and dispatches invocations to the game thread via a queue. This ensures correct
thread context for state-changing functions (UI, rendering, spawning). If the hook
is not available, falls back to direct call from the pipe handler thread (risky for
state-changing operations but works for simple getters).

```jsonc
// Request
{
  "id": 42,
  "cmd": "invoke_function",
  "func_name": "Attack",           // required
  "instance_addr": "0x7FF6AA000",  // optional (one of instance_addr / class_name required)
  "class_name": "BP_Player_C",     // optional
  "parms_size": 16,                // optional (default 0)
  "params_hex": "3F800000"         // optional (hex param bytes)
}

// Response (success)
{
  "id": 42, "ok": true,
  "result": 0,
  "instance_addr": "0x7FF6AA000",
  "func_addr": "0x7FF123ABC",
  "parms_size": 16,
  "result_hex": "3F80000000000000...",  // post-call buffer (out-params)
  "message": "ProcessEvent OK"
}

// Response (ProcessEvent error)
{
  "id": 42, "ok": true,
  "result": -2,
  "instance_addr": "0x7FF6AA000",
  "error": "ProcessEvent error code -2 (vtable read failed)"
}
```

Error codes:
- `0` = success
- `-1` = invalid args
- `-2` = vtable read failed
- `-3` = ProcessEvent offset not found
- `-4` = SEH exception during call
- `-5` = game-thread dispatch timeout (5s) — game may be paused or unresponsive
- `-7` = hook not active, fell back to direct call (may have succeeded but on wrong thread)

### Error response (any command)

```jsonc
{ "id": 5, "ok": false, "error": "Object not found at address 7FF123456789" }
```

-----

## Push Events (DLL → UI, no id)

```jsonc
// Live watch periodic push (triggered by "watch" command)
{
  "event":     "watch",
  "addr":      "7FF123456789",
  "bytes":     "0000803F",
  "timestamp": 1234567890
}
```

-----

## Teleport (Wirbel) — marker save/recall + cursor teleport

6 request/response commands (build 1027; full contract in
[teleport-spec.md](teleport-spec.md) §7). Non-zero `code` is still an
`ok:true` response — the UI maps codes to user hints; `MakeError` is reserved
for malformed requests. `tier`: 1 = engine invoke (clean), 2 = raw-write
fallback (game may snap back). Codes (§8): 0 OK, -1 not-init, -2 no controller,
-3 no pawn, -4 reflection, -5 invoke-timeout, -6 empty marker, -7 map mismatch,
-8 no hit, -9 no cursor, -10 write failed.

```jsonc
{ "cmd": "teleport_get_pose" }
→ { "x":…,"y":…,"z":…,"pitch":…,"yaw":…,"roll":…,"map":"Map","source":"raw|invoke","code":0 }

{ "cmd": "teleport_save_marker", "slot": 0 }
→ { "slot":0,"x":…,…,"map":"…","code":0 }

{ "cmd": "teleport_recall_marker", "slot": 0, "force": false }
→ { "code":0, "tier":1 }   |   { "code":-7,"map":"Map_B","markerMap":"Map_A" }
// Explicit-pose variant (BugItGo): pass x/y/z (+optional pitch/yaw/roll)
// instead of slot — bypasses the marker store and the map check.
{ "cmd": "teleport_recall_marker", "x":…, "y":…, "z":…, "pitch":…, "yaw":…, "roll":… }

{ "cmd": "teleport_to_cursor", "zOffset":100.0, "channel":0, "fallbackCenter":true }
→ { "code":0,"tier":1,"usedCenter":false,"hitX":…,"hitY":…,"hitZ":… }

{ "cmd": "teleport_get_markers" }
→ { "markers":[ { "slot":0,"valid":true,"x":…,…,"map":"…" }, { "slot":1,"valid":false }, … ] }

{ "cmd": "teleport_clear_marker", "slot": 0 } → { "slot":0,"code":0 }

// Camera POV (read-only) — distinct from the pawn pose. There is no Set POV.
{ "cmd": "teleport_get_pov" }
→ { "code":0, "camX":…,"camY":…,"camZ":…, "pitch":…,"yaw":…,"roll":…,
    "fov":…, "hasPawn":true, "pawnX":…,"pawnY":…,"pawnZ":…, "source":"invoke" }

// Teleport along the pawn's facing by `distance` uu (negative = backward).
// horizontal=true keeps Z (ground-plane); false = full 3D forward (incl. pitch).
// Returns the resulting pose. Undoable via teleport_recall_last.
{ "cmd": "teleport_relative", "distance":100.0, "horizontal":true }
→ { "code":0, "tier":1, "x":…,"y":…,"z":…, "pitch":…,"yaw":…,"roll":… }

// Force the mouse cursor on/off (writes APlayerController.bShowMouseCursor).
{ "cmd": "set_mouse_cursor", "show": true } → { "code":0, "state":true }
{ "cmd": "get_mouse_cursor" }              → { "code":0, "state":true }
// (Explicit-coordinate teleport reuses teleport_recall_marker with x/y/z above.)
```

The CE Lua path uses the Mimic mailbox `CMD_TELEPORT=8` instead (see
[teleport-spec.md](teleport-spec.md) §8) — `executeCodeEx` can't read export
return values.

-----

## Pagination Pattern

```
UI loop:
  offset = 0
  while allNodes.Count < target:
      send: { "cmd": "get_object_list", "offset": offset, "limit": 200 }
      recv: { "scanned": N, "objects": [...] }
      append objects to tree
      offset += scanned          ← MUST use "scanned", not objects.length
      if scanned == 0: break     ← end of array
```

**Why:** The DLL silently skips null/unnamed slots. `scanned` reports how many indices were actually iterated, ensuring the next request starts from the correct position even when many consecutive slots are empty (common in UE4).
