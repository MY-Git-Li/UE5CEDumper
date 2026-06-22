# Snapshot Group Match — Multiple Values over captured snapshots (Design)

> **Status: PLAN ONLY (no code yet).** Design of record for adding **Multiple
> Values** (N-value group matching) to the **Snapshot** experimental feature.
> `docs/todo.md` carries the phased work block once this is scheduled.

Brings the object-aware **Group Scan** capability (today live-memory only, in the
Value Search tab) to the **captured-snapshot** corpus — the Orden-reuse target the
group-value-scan spec already reserved (`group-value-scan-spec.md` §3.1, row
"Snapshot": *"N values co-occur inside one captured object"*).

The underlying snapshot data already supports this — the `fields` table carries
everything a group match needs (`class_fqn / norm_path / prop_name /
gobjects_index / offset / hex / declared_type / numeric_value`). **No DB schema
change.** The gap is purely the matcher + query + UI.

-----

## 1. Why — the gap

The three Snapshot surfaces are all **single-value / single-field** today:

| Surface | Query model today | Multi-value? |
|---|---|---|
| **Snapshot Diff** | one snapshot pair `(idA, idB)` → changed rows, one `(class, object, field, old, new)` per row; client-side filters | ❌ |
| **SPC Query** | **one field's** value *sequence* across N snapshots vs a directional predicate chain (Any/Unchanged/Changed/Increased/Decreased) + per-snapshot absolute window (Exact/Between/≥/≤); AND-only | ❌ |
| **Class Pivot** | group one class's instances by a **single** key field; project value fields | ❌ |
| *(reference)* **Value Search → Group** | full N-value group scan (`Orden` SDR matcher) — but **live memory only** | ✅ |

No surface answers **"in this captured snapshot, which objects hold ALL of N
values at distinct numeric offsets?"** — the group-scan semantics over captured
data.

### Why do it over a snapshot at all (vs the existing live Group Scan)?

Honest value proposition — this complements, does **not** replace, the live scan:

- **Offline + repeatable** — capture once, run many group queries with no 15–25 s
  live walk each time; deterministic (the snapshot is frozen).
- **Composes with the corpus** — once a snapshot group match locates the
  class + offsets, hand off to SPC / Class Pivot on the same corpus.
- **Pre-filtered** — the snapshot is already numeric-UPROPERTY-only (+ optional
  Native-C / noise-skip), so the match runs over clean data.
- **The temporal axis (Mode B, §3)** — the real differentiator: track a *group*
  of related stats *across* snapshots, where some change and some don't. Live
  Group Scan's refine does per-slot prev-value too, but the snapshot version
  persists across game restarts and reuses the whole SPC corpus.

**Caveat (state up front):** captured `obj_addr` goes stale across game
restarts; the structural finding (which class / which offsets) stays valuable and
is re-resolved via Locate / Live Walker — the **same same-session-address handoff
contract the existing Diff / SPC rows already use**. For "find a live value
right now", the live Group Scan is still the better tool.

-----

## 2. Two modes

### Mode A — single-snapshot group find (absolute)
*"Which objects in snapshot S hold these N values, each at a distinct numeric
offset, in any order?"*

Per-slot **absolute** predicate (the live group's First-Scan set):
`Exact / Bigger / Smaller / Between`. One snapshot, no temporal axis. This is the
most direct answer to the user's question and the v1 deliverable.

### Mode B — cross-snapshot group comparison (temporal) — the motivating case
*"Which objects' group of stats moved in THIS pattern across snapshots — some
changed, some stayed put?"*

Per-slot **relative** predicate over the value sequence:
`Changed / Unchanged / Increased / Decreased` (optionally combined with an
absolute window). N snapshots, intersected like SPC. This is where the feature
earns its keep.

-----

## 3. Design principle: **groups need `Unchanged`**

> In a multi-value group, it is *normal* for some members to stay constant while
> others move. The canonical example: a `{ Current HP, Max HP }` pair — taking
> damage gives **Current HP `Decreased` AND Max HP `Unchanged`**. Forcing every
> slot to a *moving* predicate (Increased/Decreased only) would make that group
> unexpressible and silently miss the right object.

Therefore, in **Mode B**, every slot's relative-predicate set **must include
`Unchanged`** (full set: `Changed / Unchanged / Increased / Decreased`), and a
match is the **AND of independent per-slot predicates over distinct leaves**. The
selectivity comes from the *combination*: `Decreased + Unchanged` is both more
selective than either alone **and** `Unchanged` is a real constraint (it excludes
objects whose Max HP also moved — e.g. a level-up, where both change).

### Contrast with the single-value surfaces (why this is consistent, not special-casing)

- **Single-value Snapshot Diff** shows *only changed* values — a diff is, by
  definition, the set of changes; there is no `Unchanged` row. That is correct
  *for a diff*.
- **Single-value `Unchanged`** is deliberately routed to **SPC Query** (which has
  the `Unchanged` predicate per single field across N snapshots).
- **Multi-value group comparison** is the case neither covers: a *set* of fields
  on *one* object where the pattern mixes changed and unchanged. `Unchanged` is
  not noise here — it is part of the group's fingerprint.

This mirrors the **live** Group Scan, whose P2 refine already offers per-slot
`Changed / Unchanged / Increased / Decreased` (`Orden::SlotTarget`,
`Radar::ComparePredicate`) — Mode B is the snapshot analogue of that refine.

-----

## 4. Architecture — pure-C# `Orden` port (the reuse seam)

`Orden` (`dll/src/Orden.h`, ~56 lines: `MatchGroup` + `HasDistinctAssignment` =
Kuhn's augmenting-path SDR) was **deliberately built source-agnostic, std-only,
header-only** so DB-driven features could reuse it (`group-value-scan-spec.md`
§3.1; the constraint "`Orden.h` must depend only on `Radar.h`"). The snapshot
corpus lives in C# / SQLite, so the snapshot match runs **in C#**, not via a DLL
round-trip.

**Decision: port the SDR matcher to C#.** Rationale:
- Big snapshots are ~1M+ rows; shipping them back to the DLL to run Orden is
  absurd. The existing Snapshot engines (Diff / SPC / Pivot) are **all pure C#
  over loaded rows** — a `GroupMatchEngine` fits the identical pattern.
- Kuhn's augmenting path is trivial and AOT-safe (no reflection, no dynamic
  dispatch) — ~60–100 lines + unit tests mirroring `dll_helpers_test`'s Orden
  cases.

```
SnapshotStore.GroupMatchAsync(snapshotIds, slots, joinMode, gameOnly,
                              excludeClasses, maxResults, ct)
   │  (Mode A: snapshotIds.length == 1; Mode B: >= 2, intersected)
   ▼
load rows grouped by object   (reuse DiffSnapshotsAsync hash-join /
                               LoadIntersectedCandidatesAsync intersection)
   │  per object → numeric leaves { offset, declaredType, hex[]→bytes/num[] }
   ▼
GroupMatch.MatchGroup(leaves, slots)   ← C# port of Orden (SDR, Kuhn)
   │  Mode A: absolute predicate per slot, value(s) from the one snapshot
   │  Mode B: relative predicate per slot, evaluated over the value SEQUENCE
   ▼
GroupCandidate { instance, slots[] }   ← REUSE the existing model + UI template
```

### Leaf construction (no schema change)
- One leaf per numeric field row of an object: `position = offset`,
  `width = SizeOf(declaredType)`, `bytes = hex→bytes`, plus `num[]` (the
  per-snapshot decoded value sequence for Mode B) via `SnapshotNumeric.TryFromHex`.
- **Distinctness key = `(object, offset)`** (the snapshot analogue of the live
  matcher's absolute `leafAddr`) — so two slots can't claim the same field.
- Numeric scope only (the snapshot is already numeric-only; bool/string excluded
  from group slots, same as the live group).

### Predicate model (mirror `Orden::SlotTarget` / live group P2)
- **Mode A (absolute):** `Exact / Bigger / Smaller / Between` against the slot's
  target value(s), evaluated on the single snapshot's leaf value.
- **Mode B (relative):** `Changed / Unchanged / Increased / Decreased` comparing
  the leaf's value across the chosen snapshot pair/sequence (reuse the SPC
  directional semantics: `Unchanged`/`Changed` on raw bytes, `Increased`/
  `Decreased` on decoded numeric). Optionally AND an absolute window per slot.
- A slot "fits" a leaf iff the leaf's width can hold the target **and** the
  predicate holds; `HasDistinctAssignment` then requires an SDR across all slots.

### Cross-snapshot join (Mode B)
Reuse SPC's `LoadIntersectedCandidatesAsync` + `SpcKey` join modes
(`Strict / Loose / InSession`) so each candidate object carries the value
sequence per leaf. Mode A uses the simpler single-snapshot load (one value per
leaf), like one side of a Diff.

-----

## 5. UI — host inside the Snapshot panel (Diff / Group toggle)

Recommended: a **Diff / Group mode toggle in the Snapshot panel** (mirrors the
Value Search Single/Group `ToggleSwitch`), experimental-gated, snapshot-centric.

- **Reuse the models verbatim:** `GroupSlotInput` (2–4 row input: DataType /
  ScanType / Value / Value2), `GroupCandidate`, `GroupSlotMatch`,
  `GroupScan*Result` (`ValueScanModels.cs`).
- **Extract + reuse the templates:** lift the group **input grid**
  (`ValueSearchPanel.axaml:418-443`) and the **master-detail results DataGrid +
  RowDetailsTemplate** (incl. the 🔒 locked-offset table,
  `ValueSearchPanel.axaml:581-679`) into a shared `UserControl` both panels bind.
- **Snapshot picker:** Mode A = one snapshot; Mode B = an ordered N-snapshot
  selection (reuse the SPC snapshot picker, `SpcPanel.axaml:44-86`) with a
  per-slot predicate column that exposes the **relative set incl. `Unchanged`**.
- **Handoffs:** per-slot Open in Live Walker / Copy Address / Locate in GWorld /
  Locate in GameEngine — reuse the existing **Diff / SPC same-session-address
  handoff** contract (the stored `obj_addr`), not the live group's session
  windowing.
- **No "Next Scan" refine** in the snapshot context: Mode A is a single query;
  Mode B *is* the across-snapshot comparison (the temporal axis replaces refine).

Alternatives considered (rejected for v1):
- *Add "Source: Live / Snapshot" to the Value Search Group mode* — maximal reuse
  but couples the always-on Value Search to the experimental Snapshot feature,
  and forces two divergent behaviours (live = DLL refine + DLL windowing;
  snapshot = no refine + C# paging) into one panel.
- *Fold into SPC as an "N-field intersection"* — conflates the temporal axis with
  the spatial-AND axis; less clean as the answer to "the **Snapshot** UI".

-----

## 6. Phasing

| Phase | Scope | Effort · Risk |
|---|---|---|
| **S1 — C# Orden port** | `Services/GroupMatch.cs`: `Leaf` / `SlotTarget` / `MatchGroup` / `HasDistinctAssignment` (Kuhn), pure + AOT-safe; xUnit tests mirroring the DLL Orden cases. | **S** · low |
| **S2 — `GroupMatchEngine` + `SnapshotStore.GroupMatchAsync` (Mode A)** | Single-snapshot load (reuse Diff hash-join), per-object leaves via `SnapshotNumeric`, run `MatchGroup`, emit `GroupCandidate`. Absolute predicates only. | **M** · low-med |
| **S3 — UI (Snapshot panel Group mode)** | Diff/Group toggle; extract the shared group input + master-detail control; wire handoffs (same-session addr). | **M** · med (XAML extract / AOT binding) |
| **S4 — Mode B (cross-snapshot temporal)** | N-snapshot intersection (reuse `LoadIntersectedCandidatesAsync` + `SpcKey`), per-slot relative predicates **incl. `Unchanged`**, optional absolute AND. This is the §3 motivating feature. | **M-L** · med |
| **S5 — Deep blocks (optional)** | Snapshot already stores struct-array elements (`SnapshotCapturedArray/Element`, inner-key) → array-as-block group match, mirroring the live deep group's per-block `MatchGroup`. | **M** · med |

v1 = S1–S3 (Mode A). The user's priority (the `Current HP / Max HP` case) lands in
**S4 (Mode B)** — schedule S4 close behind v1.

-----

## 7. Caps / constraints

- **N ∈ [2,4]** slots (UI), per-slot convergence list capped at 8 (matches the
  live `Orden` `perSlotCap`); SDR over 2–4 slots is trivial (`O(slots×edges)`).
- **Numeric scope only** (`NumericNoByte` / `NumericAll`); bool/string excluded
  from slots (same as live group).
- **AOT / trim safe** — pure loops, no reflection, raw ADO.NET (the snapshot
  engines' existing rules). Capture denylist / excluded-classes once at query
  start (the SPC `LoadIntersectedCandidatesAsync` thread-safety pattern).
- **Memory** — group match is linear over loaded rows (~1M for a big snapshot);
  do **not** materialise a cross-product of `(field, value)` pairs — gate at
  per-object leaf evaluation, like `SpcEngine.Matches`.
- **Result cap** — one `GroupCandidate` per matched object regardless of how many
  slots gated it; truncation flag at the cap (the SPC `MaxRows` semantics).

-----

## 8. Open decisions (confirm before building)

1. **Host** — Snapshot panel Diff/Group toggle *(recommended)* vs Value Search
   "Source" selector vs SPC fold-in.
2. **v1 scope** — Mode A single-snapshot first *(recommended)* then S4 Mode B;
   or go straight to Mode B (the user's motivating case) and treat Mode A as a
   degenerate (1-snapshot) case of the same engine.
3. **Matcher** — C# Orden port *(recommended)* vs DLL round-trip.
4. **Mode B predicate UI** — per-slot relative predicate column (Changed/
   Unchanged/Increased/Decreased) **+** optional absolute window per slot; do we
   also allow an absolute-only slot mixed with relative-only slots in the same
   query? (Yes — independent per slot, like the live group.)

-----

## 9. Reuse seam note (keep the contract intact)

The C# port must keep the **same matcher contract** as `Orden.h` (a leaf fits a
slot by width + predicate; a match is an SDR). That keeps Snapshot, SPC-group, and
a future Class-Pivot group input (`group-value-scan-spec.md` §3.1 rows 2–3) on one
matcher. **Do not** fork per-feature SDR logic — the whole point of the seam is one
matcher, many leaf sources.

-----

### Anchors (current code, for the implementer)

- Snapshot data + store: `SnapshotStore.cs` (fields schema ~`146-165`; Diff
  hash-join `501-641`; SPC intersection `752-839`; `SpcKey` `714-729`);
  `SnapshotNumeric.TryFromHex` `16-53`; `ISnapshotStore.cs`.
- Predicates to mirror: `SpcEngine.Matches` `25-61`; `SpcModels.cs` `11-69`
  (`SpcPredicateKind` / `SpcAbsoluteKind`), `94-125` (`SpcQuery`).
- Matcher to port: `dll/src/Orden.h` `99-169`.
- Models + UI to reuse: `ValueScanModels.cs` `219-388`
  (`GroupSlotInput` / `GroupSlotMatch` / `GroupCandidate`);
  `ValueSearchPanel.axaml` `408-680` (input grid + master-detail + RowDetails).
- Host panel: `SnapshotPanel.axaml`, `SnapshotViewModel.cs`.

See also: [group-value-scan-spec.md](group-value-scan-spec.md) (the live group +
the Orden seam), [experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md)
(Snapshot / SPC / Pivot design of record).
