# Multiple Values Group Scan — Spec & Extension Points

Object-aware analogue of Cheat Engine's **Group Scan**, living as a **mode** inside the
Value Search tab. This doc is the durable reference for the architecture and — more
importantly — the **connection points where future features plug in** (the `Orden`
reuse seam + the phase roadmap).

Shipped: P1 builds 1276-1278 (group scan), Deep builds 1283-1285 (opt-in deep
containers, single + group), P2 builds 1295-1302 (per-slot prev-value / ordered /
Between predicates + locked-offset table; the "Copy CE / export" sub-piece was
deliberately dropped — export from Live Walker), **P4 increment 1 builds 1303-1313
(opt-in cross-object actor block — owned sub-objects' numerics, approach C; MERGED
main PR #313 `d977a34`)**. P1/Deep + the P2 prev-value refine in-game verified on
SEED (UE4.27); **P4 cross-object in-game VERIFIED on TQ2 (UE5.07, GAS)**; Between
first-scan live-verify still pending. **Next: P4 increment 2** (per-slot
`owner_class` for the Pivot handoff — see §3.2 P4). See [dev-log.md](dev-log.md)
for the milestone history.

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

- **P2 — prev-value per slot + locked-offset table. DONE (builds 1295-1302).** Per-slot
  predicate lives on `Orden::SlotTarget` (`st` + `tolerance` + `targets2`, routed through
  `Radar::ComparePredicate`; `LeafSatisfiesSlot` rejects prev-value types on the first
  scan — no baseline). First scan takes `Exact / Bigger / Smaller / Between` (the last
  carries an upper bound in `value2`/`targets2` — the bounded-unknown entry point); the
  group refine (`Aura::RefineGroupCandidates`) also takes `Changed / Unchanged / Increased
  / Decreased`, comparing each leaf against its stored `GroupSlotMatch::prevValue`. The
  locked-offset table (`🔒 Class — Str@0x20, Def@0x24`) surfaces once every slot locks
  (`AllLocked`). The third sub-piece — a **"Copy CE Script" / export of the matched object —
  is a deliberate WON'T-DO** (owner decision, not pending work): the resolved pointer chain is
  exported from **Live Walker**, which already does it. Do not re-add a group-side CE/export
  button. Prev-value refine in-game VERIFIED on SEED; *remaining:* Between first-scan live-verify.
- **P3 — numeric containers as blocks.** **Largely done** via the opt-in Deep mode.
  *Remaining:* scalar-**valued** maps (`TMap<Name,int>` values) aren't emitted by
  `WalkContainerLeaves` (struct-valued maps *are*) — needs `ContainerCacheEntry` to carry
  key/value leaf types (the TODO is in the walker comment).
- **P4 — cross-object actor block (owned sub-objects), opt-in. APPROACH C — ownership +
  value-driven (NOT class-name matching). Increment 1 SHIPPED + MERGED main PR #313
  (builds 1303-1313); in-game VERIFIED on TQ2 (UE5.07, GAS) — `bp_tq2_character_stats_component`
  → `AttributesComponent` (ASC) → `m_pAttributeSetHealth.CurrentHealth`. Remaining: increment 2
  = per-slot `owner_class` so the Pivot handoff pivots on the sub-object's class (today it uses
  the actor's class — `GroupSlotMatch.ClassName` is denormalized from the candidate actor; for a
  cross-object slot the Pivot should use the OWNED sub-object's class instead). Add an
  `owner_class` to the wire `slots[]` (DLL `GroupCandidateToJson`, from the leaf's owning object),
  parse it in `ParseGroupCandidate`, and have `PivotGroupSlot` prefer it.** The goal is **not**
  "find an Attribute Component"
  — it is **"merge an actor + the numeric leaves of the sub-objects it OWNS into ONE block"**,
  so a group whose N values are *distributed across* {actor, its components, its GAS
  AttributeSets} is matched. (Values that *co-locate* in a single object — including one
  `UAttributeSet` or one bespoke stats component — are ALREADY found by P1: every UObject,
  AttributeSet included, is scanned as its own block. P4 adds **only** the
  cross-object-distributed case.)

  **Why not class-name matching (the original P4 sketch).** Keying on the target class name
  matching `AttributeSet` / `Component` is both too narrow and too broad: `AttributeSet` only
  catches GAS — a minority of shipped titles (SEED, our main UE4.27 test game, uses a bespoke
  `LifeMSUnit : LifeUnitBase` framework, **not** GAS) — while `Component` catches every
  `UActorComponent` (Mesh / Movement / Capsule / Audio / Widget / Collision — almost none of
  them stats), which would explode the leaf budget and destroy the group AND's selectivity.
  `ClassLocationScorer` already encodes this lesson (scores by property-name keywords +
  penalises Audio/Niagara/Widget/Particle; never trusts class names alone). Terminology: the
  **component** is `UAbilitySystemComponent`; the value-holder is `UAttributeSet` (a `UObject`,
  not a component) whose values are `FGameplayAttributeData{ BaseValue, CurrentValue }` floats.

  **Mechanism (`Aura::AppendOwnedSubObjectLeaves`, a bounded 2-level BFS over owned objects).**
  For each candidate actor, after its own-object block:
  1. **Discover OWNED sub-objects (not by class name).** Reuse `EnumerateOutgoingObjectPtrs`
     (the outgoing-pointer adapter Locate-in-GWorld uses) to follow the actor's non-null
     `ObjectProperty` fields **plus object-pointer CONTAINERS** — `OwnedComponents`
     (`TSet<UActorComponent*>`) and a GAS ASC's `SpawnedAttributes` (`TArray<UAttributeSet*>`),
     neither of which P1/Deep follows (Deep walks *numeric* containers, not object-pointer ones).
     Gate each target by `IsOwnedBy(child, actor, 2)` — the child's Outer chain reaches the actor
     within ≤ 2 hops — so shared / global objects (other actors, GameInstance, the world) are
     never followed. **Depth 1** = the actor's components; **depth 2** = the GAS AttributeSets
     reached actor → ASC → AttributeSet (the BFS expands each owned component one more level).
  2. **Merge** each owned sub-object's numeric leaves (`CollectGroupLeaves`, absolute
     `leafAddr = sub + offset`) into the actor's block, then run `Orden::MatchGroup` over the
     union → a match means the N values co-occur at distinct addresses across the owned tree.
     Selectivity comes from the **value AND** — a Mesh's `RelativeLocation` won't equal "Str=24"
     so it self-filters; the scorer's noise-type list only **skips** obvious non-stat components
     up front (a cost optimisation, not the gate).
  3. **Refine** re-reads each leaf's absolute `leafAddr` (already the `GroupSlotMatch` contract)
     — a sub-object freed between scans → SEH-safe read faults → that leaf drops, same as the
     deep-container refine. The candidate carries the owning-actor instance for handoffs; a
     cross-object slot's `field_name` is `Sub<ClassName>.Field` (or `[i]` for a container-reached
     sub-object).

  Reuses the read-ptr + Outer-validate pattern from `FindReferencesToUObject`. Default OFF (a
  third opt-in alongside Deep). Bounded (≤ N owned sub-objects, shared leaf budget, same 15 s
  deadline). Highest risk — kept fully isolated behind the flag so P1 / Deep / single-value stay
  byte-identical.

-----

## 4. Pipe / UI surface

- **Pipe** — `begin_group_scan` (`values[]`, optional `deep`, optional `cross_object`),
  `refine_group_scan`, `query_group_candidates`, `end_group_scan`. Each input slot carries
  `value`, `data_type`, (P2) `scan_type` (default `Exact`; begin = Exact/Bigger/Smaller/Between,
  refine also Changed/Unchanged/Increased/Decreased), and `value2` (Between upper bound only).
  Object-level candidate with nested `slots[]` (each: `value`, `scan_type`, `value2`,
  `field_name`, `field_offset`, `field_type`, `leaf_value`, `addr`, `owner_addr`,
  `matched_offsets[]`, `locked`). `owner_addr` (P4) is the object directly holding the leaf —
  the actor, or an owned sub-object for a cross-object leaf. See [pipe-protocol.md](pipe-protocol.md).
- **UI** — Value Search tab Single/Group `ToggleSwitch`; group mode = 2–4 row editable
  input grid (per-row width scope + **scan-type ComboBox** + value box, which hides for
  prev-value types, plus a second `..to` box for Between) + master-detail results DataGrid
  whose detail header shows the **locked-offset table** once all slots lock. A shared
  **"Deep (nested containers)"** checkbox (default off) on both modes, plus a group-only
  **"Cross-object (owned components)"** checkbox (P4). Handoffs (Live Walker / Locate-in-GWorld
  / Copy / Pivot) reuse the single-value events; a cross-object slot's handoff targets the
  owning sub-object (via `owner_addr`).

-----

## 5. Caps (deep walk)

Same deterministic budget as snapshot capture: depth ≤ 4, ≤ 256 elements per container,
≤ 50 000 elements per object, 8192 blocks / 1024 leaves-per-block per object, 15 s
wall-clock deadline + cooperative cancel. Group scan runs single-threaded (result sets
are small by construction; the AND across slots is the selectivity).
