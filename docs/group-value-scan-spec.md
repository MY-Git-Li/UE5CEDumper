# Multiple Values Group Scan — Spec & Extension Points

Object-aware analogue of Cheat Engine's **Group Scan**, living as a **mode** inside the
Value Search tab. This doc is the durable reference for the architecture and — more
importantly — the **connection points where future features plug in** (the `Orden`
reuse seam + the phase roadmap).

Shipped: P1 builds 1276-1278 (group scan), Deep builds 1283-1285 (opt-in deep
containers, single + group). In-game verified on SEED (UE4.27). See
[dev-log.md](dev-log.md) for the milestone history.

-----

## 1. What it does

Enter **2–4 values**; the scan finds objects (blocks) that hold **ALL** of them at
**distinct** numeric-property offsets, in **any order** (e.g. `Str + Def + Dex + Int`
in one stats object). Matching N values at once is multiplicatively more selective than
one value — thousands of single-value hits collapse to a handful.

- **Block = a UObject instance** by default (its direct + one-level-struct numeric leaves).
- **Deep mode (opt-in)** additionally makes each **numeric container** (a `TArray<int>`)
  and each **struct-array / map element** its own block, so a group hidden inside a
  deeply-nested container is matched *within* one array. The user's rule: a numeric array
  is matched "as a whole array"; a struct-array element's inner numerics form a block.
- Refine narrows each slot's candidate offsets until a single offset remains (🔒 locked).

-----

## 2. Architecture

```
Fern (pipe)  begin_group_scan / refine_group_scan / query_group_candidates / end_group_scan
   │
   ▼
Radar::GroupSessionManager   sibling of SessionManager (300s expiry, V3-C view cache)
   │  GroupSession { slots[], candidates[], descriptors[], instances[] }
   │  GroupCandidate { instanceIdx, slotMatches[nSlots][] }
   │  GroupSlotMatch { descriptorIdx, elementIndex, offset, leafAddr(absolute), prevValue }
   ▼
Aura::ScanForValueGroup(slots, gameOnly, maxResults, deep)
   per object →  ① object block  : CollectGroupLeaves (direct + struct descent)
                 ② deep blocks    : WalkContainerLeaves → bucket leaves by container/element
                 each block →  Orden::MatchGroup
   │
   ▼
Orden::MatchGroup(leaves, slots) → SlotMatches      ← THE REUSE SEAM (source-agnostic)
```

### The matcher: `Orden` (歐爾登, `dll/src/Orden.h`, header-only)

`Orden` is the **pure, source-agnostic** core. It never reads game memory and never
touches GObjects. It takes:

- `Leaf { position, Radar::DataType width, uint8_t bytes[8], descriptorIdx, elementIndex }`
- `SlotTarget { const Radar::NumericTargetSet* targets }`  (multi-width, same as single scan)

and decides whether all N slot targets can be satisfied by **distinct** leaves — a
**System of Distinct Representatives**, solved with Kuhn's augmenting-path matching
(`HasDistinctAssignment`). `MatchGroup` returns each slot's converging match list.

The **caller** produces the `Leaf` set. That is the entire seam: anything that can hand
`Orden` a list of `{position, width, bytes}` can run a group match — live memory today,
captured snapshot rows / pivot sequences tomorrow.

### Block model (`Aura::ScanForValueGroup`)

- **Object block** — `CollectGroupLeaves`: the object's direct numeric fields + a
  depth-capped StructProperty descent. `leafAddr = obj + offset`.
- **Deep blocks** — `WalkContainerLeaves` (the *same* recursive walker snapshot capture
  uses) emits container leaves; a visitor buckets them by block:
  - scalar-container element (`leafName == ""`) → block key = the array path; leaves =
    its elements (`Tunes` = one block of 7 ints).
  - struct element (`leafName != ""`) → block key = `arrayPath[index]`; leaves = the
    element's inner numerics.
  Each block runs `Orden::MatchGroup` independently → a match means the group is **within
  one array/element**. `leafAddr` = the absolute element address.

### Refine & distinctness

`RefineGroupCandidates` re-reads each slot match's **absolute `leafAddr`** (direct or
deep), keeps those still equal to the new target, drops a candidate when any slot empties
or no distinct assignment survives. SDR distinctness keys on `leafAddr` (unique per
value). Container reallocation between scans → the SEH-safe read faults → that match is
dropped (same contract as the single-value container refine).

-----

## 3. Extension points (where future work connects)

### 3.1 `Orden` reuse — Snapshot / SPC Query / Class Pivot  *(deferred, seam ready)*

`Orden::MatchGroup` is source-agnostic by design. To add group matching to another
feature, produce a `std::vector<Orden::Leaf>` from that feature's data and call it — no
scanner dependency:

| Feature | Leaf source | Result meaning |
|---|---|---|
| **Snapshot** | `SnapshotCapturedObject.Fields` (each has `Offset` / `Type` / `Hex`) → decode hex to `bytes`, resolve `width` from `Type`. No DB schema change. | "N values co-occur inside one captured object." |
| **SPC Query** | Per-object field sequences already loaded by `RunSpcQueryAsync` → run `Orden` per object after the load. | "N-field intersection query" over the snapshot corpus. |
| **Class Pivot** | Extend `DiscoveryInput` to a multi-field group input; feed each object's fields. | Pivot on **co-varying value tuples**, not just single (class, prop). |

**Design constraint to preserve:** `Orden.h` must keep depending only on `Radar.h`
(std-only) — never include a live-memory / GObjects header. That is what keeps the seam
usable from the DB-driven features.

### 3.2 Phase roadmap (open work)  → tracked in [todo.md](todo.md)

- **P2 — prev-value per slot + locked-offset table + Copy CE.** Add per-slot
  `Changed / Increased / Decreased / Unchanged` to `Orden::SlotTarget` + the group refine
  (the real power when you *don't* know exact values). Surface the resolved offset table
  (class + N offsets) and a "Copy CE Script" / export of the matched object.
- **P3 — numeric containers as blocks.** **Largely done** via the opt-in Deep mode.
  *Remaining:* scalar-**valued** maps (`TMap<Name,int>` values) aren't emitted by
  `WalkContainerLeaves` (struct-valued maps *are*) — needs `ContainerCacheEntry` to carry
  key/value leaf types (the TODO is in the walker comment).
- **P4 — Attribute Component cross-object (opt-in).** The one cross-class exception:
  follow `ObjectProperty` pointers whose target class name matches `AttributeSet` /
  `Component`, 1–2 hops, include the sub-object's numeric leaves in the actor's block
  (a 2-hop path candidate + pointer-aware refine). Reuse the read-ptr+validate pattern in
  `FindReferencesToUObject`. Default OFF (no forward object-pointer schema descent exists
  today). Highest risk — isolate.

-----

## 4. Pipe / UI surface

- **Pipe** — `begin_group_scan` (`values[]`, optional `deep`), `refine_group_scan`,
  `query_group_candidates`, `end_group_scan`. Object-level candidate with nested `slots[]`
  (each: `value`, `field_name`, `field_offset`, `field_type`, `leaf_value`, `addr`,
  `matched_offsets[]`, `locked`). See [pipe-protocol.md](pipe-protocol.md).
- **UI** — Value Search tab Single/Group `ToggleSwitch`; group mode = 2–4 row editable
  input grid + master-detail results DataGrid. A shared **"Deep (nested containers)"**
  checkbox (default off) on both modes. Handoffs (Live Walker / Locate-in-GWorld / Copy /
  Pivot) reuse the single-value events.

-----

## 5. Caps (deep walk)

Same deterministic budget as snapshot capture: depth ≤ 4, ≤ 256 elements per container,
≤ 50 000 elements per object, 8192 blocks / 1024 leaves-per-block per object, 15 s
wall-clock deadline + cooperative cancel. Group scan runs single-threaded (result sets
are small by construction; the AND across slots is the selectivity).
