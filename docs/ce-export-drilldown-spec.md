# CE/CSX Export Drilldown Redesign — Design Spec

> Status: **PROPOSAL (awaiting approval)** — 2026-06-13.
> Author: design draft for review before implementation. Nothing here is shipped yet.

## 1. Design contract (the rule we are restoring)

**If the Live Walker can drill into it, the export must be able to represent it — fully expanded, in one Copy, up to the Drill Depth slider.**

The user must never have to hand-expand node-by-node in Cheat Engine to reach data
that the UI already shows by clicking. This applies to **Copy CE XML**, **Copy CE
Field**, and **Export CSX** uniformly.

## 2. What the UI can drill (the reference capability)

A field/element is drillable in the Live Walker when (`LiveFieldValue`):

| Capability | Predicate | Examples |
|---|---|---|
| Pointer nav | `IsPointerNavigation` (`PtrAddress != 0`) | ObjectProperty / Class / Weak / Soft / Lazy / Interface |
| Struct nav | `IsStructNavigation` (`StructDataAddr != 0`) | StructProperty (inline) |
| Container nav | `IsContainerNavigable` | Array / Map / Set / DataTable with count > 0 |

Crucially, when the user enters a **container view**, the per-element rows are
themselves made drillable: `PopulateMapContainerFields` / `…Set…` / `…Array…`
stamp each element's **value** with `StructDataAddr`+`StructClassAddr` (struct
values) or `PtrAddress` (object values). So the UI can drill:

- Map value that is a **Struct** (e.g. `MissionInfoList {Map: NameProperty → StructProperty}`)
- Map value that is an **Object**
- Set element that is a Struct / Object
- Array element that is a Struct / Object
- …and recursively, a value-struct that itself contains Maps/Structs (e.g. `MsTuneData → MsTunes {Map → Struct}`)

## 3. Current export reality (where it stops)

### 3.1 What DOES expand
| Path | Resolver | Emitter | Recurses? |
|---|---|---|---|
| Top-level `StructProperty` | `ResolveStructFieldsAsync` (cap `MaxStructDepth=5`) | `EmitResolvedStruct` | nested structs flattened |
| Top-level `ObjectProperty…` | `ResolvePointerInstancesAsync(depth)` | `EmitDrilledPointer` | pointers, `depth` levels |
| Map/Set/Array of **scalars** | — | `EmitMapProperty`/`EmitSetProperty`/`EmitArrayProperty` | element leaves |
| Array of struct (selected element, build 1076 fix) | re-walk via `ResolveStructFieldsAsync` | `EmitResolvedStruct` | one element, full |
| DataTable rows | walk_datatable_rows | `EmitDataTableRowsProperty` | row fields (incl. nested arrays/maps — but those hit the gaps below) |

### 3.2 What STOPS (the gap) — CE XML
- **`EmitMapProperty`** ([CeXmlExportService.cs:1451](../ui/UE5DumpUI/Services/CeXmlExportService.cs)): `if (ceKey == null || ceVal == null …) → EmitGroupPlaceholder; return;`. Any Map whose **key or value is non-scalar** (Struct / Object) collapses to an empty placeholder. → `MissionInfoList`, `MsTunes`, `ForceInfoList`, etc. show nothing.
- **`EmitSetProperty`** ([:1534](../ui/UE5DumpUI/Services/CeXmlExportService.cs)): same — non-scalar element ⇒ placeholder.
- **`EmitStructArrayProperty`** ([:1401](../ui/UE5DumpUI/Services/CeXmlExportService.cs)): per element, only **scalar** sub-fields become leaves; nested struct/map sub-fields become placeholder folders (no inner data — the array preview is shallow). The build-1076 fix only re-walks a *selected* element via Copy CE Field; the all-element path (Copy CE XML) stays shallow.
- **`EmitResolvedStruct`** ([:1025](../ui/UE5DumpUI/Services/CeXmlExportService.cs)): a struct's Map/Set children dispatch to `EmitMapProperty`/`EmitSetProperty` → hit the same wall.

### 3.3 What STOPS (the gap) — CSX
CSX has a recursive container resolver (`ResolveContainerPointerInstancesAsync` + `ConvertContainerElementsToFields`) but **`ConvertMapElementsToFields` ([CsxExportService.cs:482](../ui/UE5DumpUI/Services/CsxExportService.cs)) only sets `PtrAddress` (object values) — it never sets `StructDataAddr`/`StructClassAddr` for struct values.** So CSX expands Map→Object but not Map→Struct. Same root asymmetry as CE XML.

### 3.4 Root cause (one sentence)
**Container element *values that are structs or objects* are never fed into the existing struct/pointer resolvers + emitters** — the data to do so (`MapValueStructAddr`, computed value address, `ValuePtrAddress`) already exists and the UI already uses it; only the export path ignores it.

## 4. Drill Depth semantics (requirement #3)

Verified in the current code: depth is **already measured from the current view's
fields downward** — the breadcrumb navigation path (GWorld → … → current object)
does **not** consume any depth budget. `ExportCeXmlAsync`/`…CsxAsync`/`…FieldXmlAsync`
all pass the fresh slider value `CsxDrilldownDepth` to the resolver, which is applied
to `fieldsForXml` (current view) only; breadcrumbs are emitted flat.

**This spec preserves that and makes it explicit + tested.** Definition:

- `D` = Drill Depth slider (0–8), applied fresh at the current view.
- A **pointer dereference** costs 1 level.
- A **container expansion** (walking element values that are structs/objects) costs 1 level (matches CSX's existing model).
- **Struct flattening is free** (not charged to `D`), but bounded by `MaxStructDepth=5` to stop runaway nested-struct inlining.
- Breadcrumb depth = 0 cost (already true; lock with a test).

Example, current view = a `LifeSaveDataSlot`, `D=2`:
`MsTuneData` (struct, free) → `MsTunes` (Map expand, costs 1 → D=1) → value struct fields (struct flatten, free) → a Map inside that value (expand, costs 1 → D=0) → stops. Matches "2 clicks from here".

## 5. Proposed design

### 5.1 Principle: feed container values back through the existing struct/pointer machinery
Do **not** invent a parallel emitter. Instead:

1. **Resolver**: a unified recursive pass that, for every container field, builds **synthetic value fields** (one per element) carrying the value's `StructDataAddr`+`StructClassAddr` (struct) or `PtrAddress`+`PtrClassAddr` (object), then resolves them with the existing `ResolveStructFieldsAsync` / `ResolvePointerInstancesAsync`, **recursing into nested containers**, decrementing `D` per the §4 cost model. Populates the same `resolvedStructs` (keyed by StructDataAddr) and `resolvedInstances` (keyed by PtrAddress) dictionaries the emitter already consumes.
2. **Emitter**: `EmitMapProperty` / `EmitSetProperty` / `EmitStructArrayProperty` stop bailing to placeholders. Per element they emit the **key** as a leaf (when scalar) and the **value** via a new `EmitContainerElementValue` that dispatches:
   - value scalar → `EmitLeaf` (existing)
   - value struct **with resolved fields** → inline group at `+valueOffset`, children = struct fields at `+subOffset` (reuses `EmitResolvedStruct` body)
   - value object **with resolved target** → group at `+valueOffset`, `Offsets=[0]`, children = target fields (reuses `EmitDrilledPointer` body)
   - value nested container → recurse into `EmitMapProperty`/etc.
   - value struct/object **without** resolved fields (depth exhausted / walk failed) → today's placeholder (graceful fallback)

### 5.2 Addressing (all verified against the UI's own math)
Relative to the container group's deref'd `TSparseArray::Data` / `TArray::Data` base:

| Case | Element group offset | Value sub-offset | Deref? |
|---|---|---|---|
| Map element | `index*stride` | value at `+valOffset`; struct fields at `+valOffset+subOffset` | value struct inline (no deref); value object `Offsets=[0]` |
| Set element | `index*stride` | struct fields at `+subOffset` | inline / object `Offsets=[0]` |
| Array element | `index*elemSize` | struct fields at `+subOffset` | inline / object `Offsets=[0]` |

`stride = ComputeSetElementStride(valOffset + valueSize)`, `valOffset = MapValueOffset` (already aligned by DLL). These are exactly the formulas `ConvertMapElementsToFields` / `PopulateMapContainerFields` use, so the resolver computes each value's absolute address as `containerDataBase + (above)` to walk it.

### 5.3 Shared resolver (de-dupe CSX ↔ CE XML)
Both exporters need the same "expand container element values recursively" logic.
Plan: lift the container-element-value resolution into **one** shared async resolver
(extend `CeXmlExportService.ResolvePointerInstancesAsync` to also descend container
*struct* values, and fix `CsxExportService.ConvertMapElementsToFields` to stamp
struct addresses). CSX then reuses it; CE XML gains it. One code path, one set of tests.

## 6. Safety / bounds (must-haves)
Recursive container-of-struct-of-container expansion can explode (e.g. `MissionInfoList`
223 entries × struct × nested map…). Guards:

- **Depth cap**: the `D` budget (≤ 8) is the primary bound; container expansion decrements it.
- **Array/element limit**: reuse `ArrayLimit` (default 64) per container level — already applied; surface a "(N of M shown)" note when truncated (the UI already shows "Container element limit (64)").
- **Cycle detection**: reuse the existing emit-path `_emitPath` set + `MaxEmitPointerDepth=16` hard ceiling.
- **Walk budget**: a global cap on total `WalkInstance` calls per export (e.g. a few thousand) with a status warning if hit, so a deep+wide table can't hang the UI. Resolution already runs off the UI thread.
- **Perf note in UI**: keep the existing amber Drill-Depth ≥ 5 colour cue; add a tooltip that deep + wide containers can be slow/large.

## 7. Scope & phasing
| Phase | Deliverable | Risk |
|---|---|---|
| **A** | CE XML: Map/Set/Array **value-struct** + **value-object** expansion via the unified resolver/emitter (covers `MissionInfoList`, `MsTunes`, `ForceInfoList`, struct arrays in Copy CE XML). | Med — touches the emit core; well-tested. |
| **B** | CSX: fix `ConvertMapElementsToFields` to stamp struct addrs + share the resolver; verify `.csx` struct defs for Map→Struct. | Med |
| **C** | Lock Drill-Depth-from-current-view semantics with explicit tests; add walk-budget guard + truncation notes. | Low |

Copy CE Field already routes selected struct-array elements through `ResolveStructFieldsAsync` (build 1076); Phase A makes the same richness available for Map/Set values and for the all-element Copy CE XML.

## 8. Testing
Service-level (`CeXmlExportServiceTests`, no live DLL):
- Map<Name, Struct> → per-element group with value-struct fields at correct offsets; enum widths correct; collapse honoured.
- Map<Name, Object> → value group with `Offsets=[0]` + target fields.
- Set<Struct>, Array<Struct> (all elements) → expanded.
- Nested: struct → Map<…,Struct> → expanded to `D` levels; stops at `D=0` with placeholder.
- Depth accounting: `D=1` expands one container level, not two; breadcrumb length does not change results.
- Cycle/большой: a self-referential pointer inside a container value elides without blowup.

## 9. Decisions (locked 2026-06-13)
1. **Phasing**: ship **A (CE XML) first**, then B (CSX) + C (depth tests/guards). ✅
2. **Depth budget**: container expansion **shares the single Drill Depth slider** with pointers (each container level and each pointer deref decrements the same `D`). ✅
3. **Walk-budget cap**: **NOT added** — bounded by `D` (≤8) + `ArrayLimit` (default 64) + existing cycle detection only. ✅

## 10. Phase A implementation contract (CE XML)
- **Resolver** `CeXmlExportService.ResolveDrilldownAsync(dump, fields, resolvedStructs, resolvedInstances, depth, arrayLimit)` — single recursive pass replacing the two separate calls in the CE-XML export methods. Resolves: (1) structs at each level (flatten, depth-free, `MaxStructDepth` bound); (2) pointers (cost 1, recurse depth-1); (3) **container element values** — struct values → `resolvedStructs[valueAddr]`, object values → `resolvedInstances[valuePtr]`, recurse depth-1. Cycle-guarded by a visited set on struct-data / ptr addresses.
- **`BuildContainerValueFields(container)`** — synthetic value `LiveFieldValue` per element: struct values stamped `StructDataAddr = containerDataBase + index*stride(+valOffset)` + `StructClassAddr`; object values stamped `PtrAddress` (mirrors `PopulateMapContainerFields`). Offset = value's offset *within the element group*.
- **Emit**: thread `resolvedStructs`/`resolvedInstances` into `EmitMapProperty`/`EmitSetProperty`/`EmitArrayProperty`/`EmitStructArrayProperty`/`EmitDataTableRowsProperty`/`EmitResolvedStruct`. Per container element, emit the value by delegating to `EmitFields([valueField], …)` so struct/object/nested-container values reuse `EmitResolvedStruct`/`EmitDrilledPointer`/`Emit*Property` uniformly. Maps/Sets no longer bail to a placeholder when key/value is non-scalar — the key stays a leaf (scalar) and the value expands.
- **Depth=0** keeps today's flat behaviour (no container-value resolution → struct/object values fall back to placeholder); **depth≥1** expands.

---
*Approved 2026-06-13 — implementation contract (like docs/teleport-spec.md).*
**Phase A SHIPPED + owner live-verified (build 1085): CE XML/Field now expand
Map/Set/struct-array struct/object values recursively to Drill Depth. Phase B
(CSX alignment) + C (extra depth guards) still open.**
