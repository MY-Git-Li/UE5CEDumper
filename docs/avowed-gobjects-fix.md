# Avowed — GObjects Decoy Misdetection Fix (tracking)

> **Status:** ✅ Phase 1 **LIVE-VERIFIED in-game** (Steam build, 2026-06-16). Recovery fires and lands on the real 7.6M-object array; DynOff offset detection then succeeds. Phase 2 optional (perf/cleanup).
> **Owner:** bbfox0703 (now owns the Steam copy — local iteration possible).
> **Game:** Avowed (Obsidian Entertainment), `Avowed-Win64-Shipping.exe`, **UE 5.3** (UE503).
> **First diagnosed build:** 1162 (`609ecfb-dirty`). **Phase 1 shipped:** committed `a467082` on `dev` (build 1225); verified on build 1228 (`0b3a4e5`).

This document tracks a long-running, test-gated fix. Because the maintainer does not own Avowed and depends on a remote tester across a wide timezone gap, each verify cycle is slow. Other development continues on the repo in parallel — keep this fix's changes small and isolated.

---

## TL;DR

It is **not** "GObjects AOB not found". GObjects is found, but **intermittently resolves to the wrong address** — a `.data` decoy structure that passes the relaxed validator yet yields **0 usable objects**. The *correct* GObjects (≈7.6M objects) is reachable: on the launches where the AOB decoy fails to validate, the existing `data_scan` fallback already finds it. The fix makes that recovery happen deterministically whenever the chosen GObjects yields `Count==0`.

**VERIFIED:** on the Steam build (1228), the AOB again picked a decoy (`0x7FF6E8546868`, Count=0) → recovery evaluated 8 candidates → switched to the real heap array (`0x1B066692800`, Count=7,667,809) → `Objects=7667809` and full DynOff offset detection (Guid struct, FField/UStruct offsets) succeeded. The game is now fully usable in the dumper.

---

## Evidence (from tester logs, 3 launches, build 1162)

Log folder: `Avowed-Win64-Shipping (1)/` (init-*, offsets-*, scan-*).

| Launch | GObjects | Method | Count | ItemSize | Result |
|--------|----------|--------|-------|----------|--------|
| init-1 (15:44) | `0x2077FF02800` (**heap**) | **data_scan** fallback | **7,667,809** | 24 | ✅ works |
| init-2 (15:38) | `0x7FF6844D6868` (**.data**) | aob `GOBJ_V3` | **0** | 16 | ❌ broken |
| init-0 (16:15) | `0x7FF6844D6868` (**.data**) | aob `GOBJ_V3` | **0** | 16 | ❌ broken |

- GNames (`0x7FF684282F40`) and GWorld (`0x7FF684064B50`) resolve consistently and correctly in all three runs. Only GObjects flaps.
- Module-base addresses (`0x7FF6...`) are stable across launches; the **real** GObjects lives on the **heap** (`0x207...`) and moves per launch (ASLR), so it is only reachable via `data_scan` (which follows code RIP-relative refs), not a fixed AOB displacement.

### Key log lines

Broken launch — `scan-0.log`:
```
ValidateGObjects: Valid at 0x7FF6844D6868 (relaxed Flat, Num=40321, Objects=0x197F76E7630 [flat])
GOBJ_V3: 938 matches, validated -> 0x7FF6844D6868
```
`offsets-0.log`:
```
GObjects@0x7FF6844D6868: +08:00009D81_07050005 (hi dword=0x9D81=40321) +10:0 +14:0
ObjectArray: Flat (non-chunked) array layout detected
ObjectArray: Initialized at 0x7FF6844D6868, Count=0, ItemSize=16
```

Working launch — `scan-1.log`:
```
=== GObjects: 51 patterns tried, 10 with hits, NONE validated ===
FindGObjects: All patterns failed, trying data-section scan fallback...
FindGObjectsByDataScan: Found 1149519 static pointers ...
ValidateGObjects: Valid at 0x2077FF02800 (relaxed Flat, Num=32758, Objects=0x20703978348 [flat])
FindAll: Complete — GObjects=0x2077FF02800 (data_scan) ...   → Count=7,667,809
```

---

## Root cause

1. `Sig::GOBJECTS_PATTERNS` entry **`GOBJ_V3` is very loose** (938 matches on this build). `ScanForTarget` accepts the first match that passes `ValidateGObjects`.
2. `ValidateGObjects` **Tier-2 relaxed-Flat** path (`Genau.cpp`) accepts the `.data` decoy: `NumElements` read at `+0x0C` is a coincidental `40321` (in the plausible `0x1000..0x400000` range), and the **single** `ValidateCyclicClassChain` check passes because the decoy's *first* item happens to look like a valid UObject. It does **not** verify that *many* consecutive items are valid, so a sparse (~23% valid) decoy slips through.
3. The decoy's true element count (the offset `Aura::Init` later reads) is **0** → `Aura` initializes with `Count=0` → the dumper sees an empty object array → every downstream feature is dead.
4. Because AOB "succeeded", `FindGObjects` never falls through to the `data_scan` fallback that *does* find the real heap array. Whether the decoy validates is **heap-state / ASLR dependent**, hence the intermittency.

This matches RE-UE4SS's note for the sibling Obsidian/UE5 title **The Outer Worlds 2**: *"Changes were made to UObject"* — Obsidian ships a non-standard object/array layout.

---

## Fix plan

### Phase 1 — DLL decoy recovery (zero-regression) — ✅ LIVE-VERIFIED (Steam build 1228)

Strictly additive: a recovery step in `UE5_Init` that runs **only when the chosen GObjects yields `Aura::GetCount() == 0`**. Working games (Count > 0) never enter it, so there is **no regression risk** to the 20+ already-supported titles.

Mechanism:
- New `Genau::CollectGObjectsCandidates(out, avoid, maxCandidates)` — reuses the proven data-section RIP-relative scan (`DataScanGObjectsCandidates`, extracted from `FindGObjectsByDataScan`) to gather up to N **unique** addresses that pass `ValidateGObjects`, skipping the known-bad `avoid` address.
- `UE5_Init` (Frieren.cpp): on `Count==0`, iterate candidates; for each, `Aura::Init(cand)` and accept the first that yields `Count>0` **and** at least one resolvable (non-"None") FName. The name gate is the real discriminator — it rejects count-only decoys that `ValidateGObjects` cannot. On success, update `ptrs.GObjects` / `g_cachedGObjects` / method = `data_scan_recovery`, then run `ValidateAndFixOffsets` on the good array. On total failure, restore the original GObjects (state stays consistent).

Files: `dll/src/Genau.h`, `dll/src/Genau.cpp`, `dll/src/Frieren.cpp`. (None overlap the parallel Aura/Fern work in flight at diagnosis time.)

Cost: adds ~0.7s to init **only on the broken-launch path** (one data-section sweep). Working launches are unaffected.

Expected effect: converts the broken launches (init-0/2) into working ones — recovery runs the same data-scan that already succeeded in init-1 → lands on `0x207...` → 7.6M objects, names resolve.

### Phase 2 — optional perf/cleanup (NOT required for functionality) — ⬜ backlog

Phase 1 makes the game fully usable; Phase 2 only improves init time and robustness. Now iterable locally (maintainer owns the Steam copy).

- **Reduce init cost.** The recovery adds ~10s on Avowed (a full data-section sweep + 8-candidate eval) because the loose AOB winner pre-empts the fallback. Tighten `ValidateGObjects` at the source so the decoy is rejected up front: in the relaxed-Flat path, validate the **first K consecutive items** at a probed stride and require a high valid ratio (decoy ≈23% vs real ≈100%). ⚠ Touches the shared validator used by all games → regression-test across the test-game matrix before merge. Alternatively/additionally, persist a per-exe *method preference* ("prefer data_scan") so subsequent launches skip the doomed AOB pass (the heap address itself moves per launch — do **not** persist the address).
- **NumElements/ItemSize note (low priority).** `Aura` reads `Count=7,667,809 / ItemSize=24` consistently and DynOff detection succeeds, so the array is interpreted correctly. The stale `ValidateGObjects` relaxed-Flat count (`Num=32758`) is read at a different offset but is unused once recovery hands Aura the right base — cosmetic only.
- **Let the UI "Extra Scan" rescue this case too**: `apply_rescan` is gated on `g_cachedGObjects == 0`, but a decoy is non-zero, so the button cannot overwrite it. Relax the gate to also allow overwrite when `Aura::GetCount() == 0`. (Less urgent now that auto-recovery handles it at init.)

---

## Test protocol (for the remote tester)

1. Use the build that contains Phase 1 (build # noted at the top once tagged).
2. Inject into Avowed, let it auto-start, reach the point where the UI connects.
3. **Capture the whole DLL log folder** (`%LOCALAPPDATA%\UE5CEDumper\Logs\<Avowed pid subfolder>\`) — specifically `init-*.log`, `offsets-*.log`, `scan-*.log`. Zip and send back.
4. **Launch the game ≥3 times** (separate runs) so we sample both the decoy-hit and decoy-miss cases.
5. Success criteria, per launch:
   - `init-*.log` → `UE5_Init: Complete (... Objects=<large number, not 0>)`.
   - If the line `UE5_Init: Recovery SUCCESS — GObjects 0x... -> 0x... (Count=...)` appears, Phase 1 fired and worked.
   - UI shows a populated object tree / non-zero object count; Instance Finder / Property Search return results.
6. If any launch still shows `Objects=0` with **no** recovery-success line, capture that launch's `scan-*.log` + `offsets-*.log` — we need to see why no candidate passed the name gate.

---

## Progress log

- **2026-06-16** — Diagnosed from a 3-launch log set (build 1162). Confirmed intermittent decoy (`0x7FF6844D6868`) vs real heap GObjects (`0x207...`, 7.6M objects). Root cause = loose `GOBJ_V3` + permissive relaxed-Flat `ValidateGObjects` blocking the `data_scan` fallback. Implemented Phase 1 (zero-regression `Count==0` recovery): `Genau::CollectGObjectsCandidates` + `DataScanGObjectsCandidates` refactor (`Genau.h`/`Genau.cpp`) + recovery loop in `UE5_Init` (`Frieren.cpp`). DLL build 1225 compiles clean. Committed `a467082` + pushed to `origin/dev`.
- **2026-06-16 (same day)** — Maintainer bought the Steam copy. **Phase 1 LIVE-VERIFIED on build 1228 (`0b3a4e5`):** AOB picked decoy `0x7FF6E8546868` (Count=0) → recovery evaluated 8 candidates → `Recovery SUCCESS — 0x7FF6E8546868 -> 0x1B066692800 (Count=7667809, names ok)` → `Objects=7667809`; DynOff detection (Guid struct @0x1B06110AC90, fields A–D, ElementSize +0x34 etc.) all succeeded. Game fully usable. Phase 2 demoted to optional perf/cleanup backlog.

---

## Open questions / risks

- ~~Does `CollectGObjectsCandidates` surface the real array within the cap (16)?~~ **Resolved:** on build 1228 it evaluated 8 candidates and recovered successfully.
- ~~Is `7,667,809` the true object count?~~ **Resolved (good enough):** `Aura` reads it consistently and DynOff detection succeeds on it; any residual count-offset nit is cosmetic (Phase 2 note).
- Recovery re-runs every decoy launch (~10s, no persistence yet) — acceptable; addressable via Phase 2 method-preference persistence if init time becomes annoying.
