# Ground truth for the AOB regression sweep

Every address below was resolved from a real PDB symbol (or, where noted, from a
2-instruction accessor or from disassembly), so a signature that resolves to one of these is
*provably* correct rather than plausibly correct.

**The sweep is scripted — do not hand-run `analyzeHeadless` per project:**

```bash
bash tools/ghidra/sweep.sh                      # everything (~40 min at SWEEP_JOBS=3)
bash tools/ghidra/sweep.sh UE4.27 UE5.7         # only tags matching these substrings
py tools/ghidra/aggregate_sweep.py out/sweep    # -> out/sweep/REPORT.md
```

`sweep.sh` holds the truth table below in executable form — it is the source of truth; this file
is the explanation. Env knobs: `GHIDRA_HOME`, `GHIDRA_PROJS`, `SWEEP_OUT`, `SWEEP_XMX`,
`SWEEP_JOBS`.

## Read this first if you are going to CHANGE a pattern

This file is the **operations** manual — how to re-run the sweep and how to add a game. The
**authoring and pruning rules** live in the header + band-discipline block of
[`dll/src/Himmel.h`](../../dll/src/Himmel.h), because they belong next to the patterns they
govern. Read that block too before adding, moving or deleting anything. The short version:

1. **Band = specificity.** Pick the priority band from the pattern's LITERAL (non-wildcard) byte
   count, not from how new it is or who contributed it. Under ~8 literal bytes ⇒ band 800+.
   Counter-example kept deliberately: `GOBJ_ES53_1` has 16 literal bytes and still takes 21–475
   matches per monolithic title, because its *shape* is the generic MSVC static-init thunk. Judge
   by specificity **and** semantics.
2. **Wildcard FRAME displacements, keep STRUCT displacements.** `lea rdx,[rsp+????????]` is fine;
   `lea rdx,[rsp+00000318]` is not — a frame offset encodes one compilation's stack layout.
   But `cmp [rcx+0x2C0],rax` (UWorld member) or `cmp eax,[rdi+0x34]` (TSet Max) pin UE's real
   layout and are the evidence that makes a pattern trustworthy. Two measured qualifiers: only
   wildcard if the pattern has enough other literal context (doing it to the 7-literal-byte
   `GWLD_G42_4` produced 38 hits / 37 decoys on UE 4.27), and small shadow-space constants
   (`sub rsp,0x28`, `[rsp+0x20]`, ≤ 0x40) are idiomatic prologue, not frame layout.
3. **Exact registers beat nibbled ones** on the sparse-delegate target — confirmed three times
   independently. Over-wildcarding does not generalise a pattern; it either adds decoys or stops
   it matching entirely.
4. **Convergent hits = a real global; divergent hits = a generic idiom.** This single test has
   rejected more candidates than any other: a rejected GEngine shape gave 76–93 *different*
   targets per binary, and the TSet hash-bucket probe gave 39–43. A good pattern's extra hits all
   land on ONE address.
   **But convergence only holds WITHIN one pattern.** `GENG_X4` is convergent and *wrong* on
   DQ7R: 50 of its 55 hits agree on `145D76D78`, which is a game-side manager singleton, while
   `145FF4B28` is the real `&GEngine`. Across patterns the discriminator is whether the
   semantically-specific shapes agree — on DQ7R `GENG_X1/X2/X3/DI427_1` all said `145FF4B28`
   and only `X4` disagreed. **Rank candidates by DISTINCT PATTERNS AGREEING, never by raw hit
   count.** `consensus_*.txt` already does this; a by-hand tally of a runtime log does not, and
   that is exactly how the wrong DQ7R value was believed at "41 hits" confidence.
5. **Never prune on "no proof" — only on counter-proof.** `GWLD_V7` sat at 0-correct across the
   whole corpus, looked like dead weight, and went UNIQUE-OK the moment Meltopia gained symbols.
   `GOBJ_RE1` had zero hits across 31 programs and was correct the moment FF7 Rebirth was added.
   Contrast `GWLD_V2/V4/V5/V6`, removed only after being re-tested against three *new* oracles
   and still scoring 0 across 12 oracle groups.
6. **Invariants the build enforces.** Every table is sorted by priority with unique priorities
   (`static_assert` in Himmel.h — verified to actually fire), and `extract_patterns.py` reports
   any `AOB_*` constant declared but referenced by no `PATTERNS[]` array. Two dead constants have
   already been found that way.
7. **`instrOffset` is the silent killer.** If you add leading context to a pattern, move
   `instrOffset` by the same amount. Getting it wrong keeps the hit count healthy and makes the
   resolved address garbage — it drops to 0 correct while still looking like it works.

### Still open (as of build 2440)

- ~~**UE 4.23** is the only unverified sparse-delegate version~~ — **DELIBERATELY NOT CHASED**
  (decided 2026-07-27). 4.23 shipped 2019-09 and 4.24 landed that December, so its window was
  three months and essentially every surviving title has been bumped to 4.27; building a sample
  would need an old Visual Studio the maintainer has no intention of installing. It is also the
  version where the feature matters least — sparse delegates were barely adopted that early, so
  an unverified 4.23 is close to unobservable in practice. The real mitigation is structural, not
  a sample: **`Aura` probes the live key shape instead of gating on a version number**, which is
  what makes 4.23 *and any licensee fork* safe without a binary to test against. Do not reopen
  this unless a 4.23 binary falls into your lap.
- **FF7 Rebirth's SparseDelegates** exists (proved from `.rdata`: `SparseDelegateFunction`,
  `MulticastSparseDelegateProperty`, the `SparseDelegateReport` console command) but no pattern
  finds it. Lead for whoever picks it up: find the `SparseDelegateReport` string xref and follow
  the `FAutoConsoleCommand` handler.
- ~~**Avowed (5.3)** sparse: zero hits.~~ **CLOSED 2026-07-27** — `SparseDelegates = 0x14B5BD9A8`,
  found structurally (the `SparseDelegateReport` string does not exist in this binary), structure
  is stock UE 5.3, `SPARSE_AV53_1` added at priority 170.
- ~~**Sparse `n=1`** on five binaries~~ **MOSTLY CLOSED 2026-07-27** by `SPARSE_X1`/`X2`, mined on
  Grimhook: Everspace 2 5.5/5.5b 1→2, Satisfactory 5.2/5.6 CoreUObject 1→3, CrashReportClient
  1→3, Grimhook 1→3. **Avowed 5.3 is now the only `n=1` left** — `SPARSE_AV53_1` is its sole
  reach, so a patch that moves that site takes sparse support on Avowed with it.
- **`SPARSE_PAL51_1` is Palworld-specific, not "UE 5.1".** It takes **0 hits** on Grimhook, a real
  symbolised 5.1. Per rule 5 a MISS is not counter-proof, so it stays — but do not read its
  `PAL51` provenance as version coverage. The 5.1 sparse coverage that actually works is
  `SPARSE_ES2_1` (+ now `X1`/`X2`).
- ~~**`GWLD_TQ_1` sits at priority 210**~~ **DONE 2026-07-27 — promoted 210 → 101.** Measured
  first: it wins on **6 of 16** oracles (no other GWorld pattern wins more than 2) and has **zero
  decoys anywhere** (10 UNIQUE-OK, 6 NO-TRUTH, 23 MISS). The saving is a whole `.text` pass, not
  a few validations — patterns scan in **batches of 8**, so order *within* a batch is free but
  crossing a batch boundary costs a full extra AVX2 sweep, and at 210 it sat in batch 2. What it
  displaces out of batch 1 (`GWLD_ES2_3`) wins on nothing, so the swap is free.

### Settled facts — do not re-chase these

Each cost at least one headless run to establish. Recorded so nobody spends another.

- **`out/sweep/patterns.tsv` goes stale.** Always re-run `extract_patterns.py` before scanning;
  a cached tsv from a previous sweep put three of five analysts a commit behind on a priority.
- **DropIn's 32-byte `FUObjectItem` is a CONFIG artifact, not a 4.27 trait.** Proven by two
  independent symbolised 4.27 binaries (Breeders, Maelstrom) carrying the stock 24-byte item.
- **A small `.msvcjmc` section does NOT mean a Development build.** Breeders and Maelstrom have
  one at 8 bytes VirtualSize, LightMaze 512 — one stray `/JMC` translation unit, not engine-wide
  instrumentation. All three still ship the stock 24-byte chunked item, and it cost zero patterns.
- **`tdb` @ `0xFF00000000` is a Ghidra loader artifact, not a PE section.** Four analysts have now
  investigated it independently; one dumped the on-disk section table to disprove it.
- **The default PDB loader is fine.** MSDIA remains a *Meltopia-only* workaround, not a habit.
- **Pre-4.23 has no `NamePoolData` symbol at any version** — do not look for one. Go straight to
  `FName::GetNames`. It also has no sparse delegates: 10/10 `SPARSE_*` MISS on a 4.20/4.21 binary
  is the CORRECT answer, and raw ASCII+UTF-16 data-section scans for `SparseDelegate` have already
  been run on both and returned zero.
- **`GNAM_V7` (CallFollow) and the three `GNAM_EXP_*` (SymbolCallFollow) are UNMEASURABLE by this
  harness**, not worryingly unknown: `scan_patterns.java` scans byte patterns only, and the `EXP`
  ones cannot fire on a monolithic EXE because nothing is exported.

### Recipe — pre-4.23 GNames on a binary with NO symbols

`TNameEntryArray` is `128*8+8 = 0x408` bytes, so scan `.text` for `mov ecx,0x408` and take the
`mov [rip+disp],rax` within ~64 bytes. On Freud Gate's 31 MB `.text` that gave 12 candidates and
exactly one with a rip store — `FName::GetNames`. **Live lead for the three 4.18 rows that leave
GNames unset on purpose** (DQ XI S, Octopath, FF7 Remake), with the caveat that 4.18's chunk-table
size may not be `0x408` — confirm it from the memset / field offsets rather than hardcoding.
Note also that a pre-4.23 binary carries TWO different chunk sizes: Freud Gate's `FUObjectArray`
is 65536 elems/chunk (`sar 0x10`) while its `TNameEntryArray` is 16384 (`sar 0xE`). Do not conflate.

### Thinnest coverage in the table

**Pre-4.23 GNames rests on exactly two patterns — `GNAM_CT3` (700) and `GNAM_G42_1` (710) — and
they are the SAME `FName::GetNames` lazy-init prologue shape.** Both are OK-BEHIND, both in batch
3, confirmed identical on HeliumRain *and* Freud Gate. A compiler change to that prologue takes
GNames on every pre-4.23 title at once. This is the sparse-`n=1` situation again, on a different
target; if a third pre-4.23 sample ever arrives, mining a structurally different anchor there is
the highest-value thing to do with it.

**Why this file exists:** re-deriving these costs a headless run per binary, and getting one
wrong silently corrupts every verdict downstream. It has already happened once — a placeholder
truth value made two good GEngine patterns look like they produced five decoys, and they were
demoted on that basis. `scan_patterns.java` now prints `NO-TRUTH` instead of `DECOY-ONLY` when
it has no plausible truth, but the real fix is to keep the values written down.

All addresses are **image-based VAs** as Ghidra shows them (preferred base, not runtime).

## The sweep

| Tag | Project (`D:\Tools\GHIDRA_Projs\*.rep`) | UE | Symbols | Notes |
|---|---|---|---|---|
| UE4.18-FF7R | `FF7R` | 4.18+ | ❌ none | GObjects+GEngine truth **derived by disassembly** — see below |
| UE4.18-DQXIS | `DQ_XI_S` | 4.18 | ❌ none | truth by disassembly; ASLR base recovered; **GNames absent on purpose** |
| UE4.27-DQ7R | `DQ7R` | 4.27 | ❌ none | truth by disassembly; **GEngine ≠ the live consensus** — see below |
| UE5.4-Elliot | `Elliot` | 5.4 | ❌ none | truth by disassembly; the corpus's **only UE 5.4** sample |
| UE4.20-Everspace | `ES1-420` | 4.20 | ✅ full PDB | oldest sample; supersedes the symbol-less `ES1.rep` |
| UE4.20-HeliumRain | `HeliumRain` | 4.20.3 | ✅ full PDB | **second symbolised 4.20** — pre-4.23 GNames no longer rests on Everspace alone |
| UE4.21-FreudGate | `Freud_Gate_UE421` | 4.21 | ❌ none | truth by disassembly; **closes the 4.21 hole** |
| UE4.27-Breeders | `Breeders_of_the_Nephelym` | 4.27 | ✅ full PDB | |
| UE4.27-Maelstrom | `Maelstrom` | 4.27.2 | ✅ full PDB | |
| UE5.0-LightMaze | `Light_Maze` | 5.0.3 | ❌ none | truth by disassembly; **closes the 5.0 hole** (4.27 used to jump to 5.1) |
| UE4.22-Satisfactory | `Satisfactory_UE422` | 4.22 | ✅ full PDB | **monolithic EXE with symbols** — the only pre-4.25 one |
| UE4.24-DropIn | `DropIn_UE424` | 4.24.3 | ✅ full PDB | **closed the last checkable sparse-delegate gap** — see below |
| UE4.25-Everspace2 | `ES2-UE425` | 4.25.2 | ✅ full PDB | the FField/FProperty transition band |
| UE4.26-Satisfactory | `Satisfactory_UE426` | 4.26.2 | ✅ full PDB | modular, 4 DLLs — supersedes the unusable `Satfi426` |
| UE4.27-DropIn | `DropIn` | 4.27.2 | ✅ full PDB | Development build (32-byte `FUObjectItem`) |
| UE4.27-Artisan | `The_Artisan_of_Glimmith` | 4.27 | ❌ none | monolithic noise probe |
| UE4.18-Octopath | `Octopath` | 4.18 | ❌ none | monolithic noise probe; version recovered from the `++UE4+Release-4.18` build tag + a fingerprint identical to DQ XI S |
| UE4.x-FF7Rebirth | `FF7Re` | 4.26 fork | ❌ none | the only binary that exercises `GOBJ_RE1` / `GNAM_V7` |
| UE5.1-Grimhook | `Grimhook` | 5.1.1 | ✅ full PDB | **the first symbolised 5.1** — see below |
| UE5.1-Palworld | `Palworld` | 5.1 | ❌ none | monolithic noise probe; its consensus is now corroborated by Grimhook |
| UE5.2-Satisfactory | `SF521_pdb` | 5.2.1 | ✅ full PDB | **created by us** — see the UE5.2 note below |
| UE5.2-SatGameDLL | `Satisfactory_UE521` | 5.2.1 | ⚠ game DLL only | noise probe; project mis-imported, see below |
| UE5.3-Avowed | `Avowed` | 5.3 | ❌ none | negative control (packed 20-byte `FUObjectItem`) |
| UE5.5-Everspace2 | `ES2-0517` | 5.5 | ✅ full PDB | `0517` is a DATE, not a version |
| UE5.5-Everspace2b | `ES2_UE55` | 5.5 | ✅ full PDB | 2025-06-17 build — the same-game cross-build pair with `ES2-0517` |
| UE5.5-Meltopia | `Meltopia_V2` | 5.5 | ✅ full PDB¹ | second symbolised MONOLITHIC 5.5; supersedes `Meltopia.rep` |
| UE5.5-ManorLords | `Manor Lords` | 5.5 | ❌ none | monolithic noise probe |
| UE5.6-Satisfactory | `Satisfactory_v1.2.3.1` | 5.6.1 | ✅ full PDB | modular; **also holds a symbolised CrashReportClient.exe** |
| UE5.6-TQ2 | `TQ2` | 5.6 | ❌ none | monolithic noise probe |
| UE5.7-Solarpunk | `Solarpunk` | 5.7 | ✅ full PDB | |
| UEx-DQ12HD2D | `DQ_I_II_HD2D` | ? | ❌ none | monolithic noise probe |

¹ Meltopia's first import silently failed to apply its 347 MB PDB. The retry succeeded by
selecting the **MSDIA** PDB loader — **PDB-Universal fails on this file**. If a game ships a PDB
and the probe still reports zero UE globals, try MSDIA before concluding the PDB is unusable.
The symbol-less `Meltopia.rep` is superseded and can be deleted.

**Meltopia is also the cleanest validation of the consensus method.** While it had no symbols,
the sweep's consensus table predicted GEngine `149F002F8`, GWorld `149F03D10`, GObjects
`149D87430` and GNames `149CA3C80`. Once the PDB applied, the symbols gave `149f002f8`,
`149f03d10`, `149d87420` (+0x10 = `149d87430`) and `149ca3c80` — **all four exact**.

### UE 4.24 closed the sparse-delegate question

`DropIn_UE424` carries a `FSparseDelegateStorage::SparseDelegates` symbol, and its mangled name
demangles to

    TMap<UObjectBase const*, TMap<FName, TSharedPtr<TMulticastScriptDelegate<FWeakObjectPtr>>>>

— a **raw pointer key**, identical to 4.25 / 4.26 / 4.27 / 5.x. Sparse delegates were introduced
in 4.23, so **only 4.23 itself is now unverified**, and no 4.23 binary exists in the corpus.
`Aura`'s walker still probes the live key shape rather than gating on a version number; keep it
that way — that is what makes 4.23 and any licensee fork safe without a binary to test against.

### Oracles vs noise probes — why both

A row with truth answers *"does it resolve to the right address?"*. A row without answers only
*"did anything hit that should not have?"* — but that second question needs **monolithic** game
EXEs. A Satisfactory engine DLL is 4–30 MB of `.text`; a shipped game EXE is 100–200 MB of
engine + game + middleware. Per-pattern collision counts measured only on modular DLLs
understate real-world noise several-fold, which is why the report normalises to hits/MB and
calls out the monolithic-only figure separately.

### Broken imports you will see

Several supplied projects contain duplicate program entries with image base `0000:0000`, 0
functions and 1 symbol — failed imports sitting beside a good one **under the same name**.
`scan_patterns.java` skips any program with zero executable bytes and keys its output files on
`tag + program + image base`; without that the broken duplicate silently overwrote the real
results (this cost us the 5.6 Engine DLL on the first pass).

### `Satisfactory_UE521` — mis-imported, and how it was fixed

Only ONE program in it is genuinely UE 5.2: `FactoryGame-FactoryGame-Win64-Shipping.dll` (the
*game* module, which holds no engine globals). Its `Core` / `CoreUObject` / `Engine` entries are
**duplicates of the UE 4.26.2 DLLs** — their recorded `executablePath` points at
`…\Satisfactory\UE4.26.2\Engine\Binaries\Win64\` and their function/symbol counts match the 4.26
project exactly. Four further entries are broken empty imports.

Fixed by importing the real files into a **separate** project so the original stays untouched:

```bash
for M in CoreUObject Core Engine; do
  analyzeHeadless D:/Tools/GHIDRA_Projs SF521_pdb \
    -import "D:/tmp/Game archive/Satisfactory/UE5.2.1/Engine/Binaries/Win64/FactoryGame-$M-Win64-Shipping.dll"
done
```

Run these **one at a time**: a Ghidra project takes an exclusive lock, so concurrent imports into
the same project fail with `LockException` and only the first gets in.

### `ES2-0517` needs a one-time language upgrade

It was created by an older Ghidra. Opening it triggers "Updating language version", which
`-readOnly` cannot save — the run stalls and the script never executes. Run it **once without
`-readOnly`** (`-noanalysis` is fine) to persist the upgrade, then the normal read-only sweep
works. If a previous read-only attempt was killed mid-upgrade it leaves `<project>.lock` behind;
delete the stale `*.lock` / `*.lock~` after confirming no `java.exe` holds that project.

### `Satfi426` — superseded, do not re-chase

`Satfi426.rep` contains only three *game* DLLs; the modules that **define** the globals were
never imported, so it has no ground truth and no engine code to mine. Its game module does carry
real IAT slots (`__imp_?GUObjectArray@@3VFUObjectArray@@A` @ `0x180722950`,
`__imp_?GWorld@@3VUWorldProxy@@A` @ `0x180727CB8`, `__imp_?GEngine@@3PEAVUEngine@@EA` @
`0x180727CB0`) but all 490 referencing sites are Coffee Stain's own code (`UFG*`/`AFG*`/`FFG*`),
so any pattern mined there would match Satisfactory and nothing else.
**`Satisfactory_UE426` replaces it entirely** — same engine version, all four modules, full PDBs.
Note `find_syms3.java` deliberately does **not** filter `__imp_*`; without that the IAT is
invisible and a modular build looks empty.

## Deriving truth for a new game

1. **`probe.java` first** — confirms the project opened and whether symbols exist at all.
2. **If symbols:** read the globals straight off it. Three catches:
   - `NamePoolData` often has no symbol. Disassemble `FNameDebugVisualizer::GetBlocks` — it is
     always `lea rax,[&Pool.Entries.Blocks]; ret`, so subtract `0x10`.
   - The name pool moved from **CoreUObject to Core** at 5.6. Check both.
   - Pre-4.23 there is no `FNamePool` at all: GNames is a `TNameEntryArray*` lazily allocated
     inside `FName::GetNames`. Dump that function
     (`-postScript dump_func.java "FName::GetNames"`) and take the global it tests-and-stores.
3. **If no symbols:** run the sweep with no `GS_TRUE` and read `consensus_*.txt`. Addresses that
   **≥3 independent patterns** agree on are reliable — validated on Everspace, where the pre-PDB
   consensus matched the symbols exactly once the PDB arrived. The `-- priority walk --` block in
   the same file shows what the runtime would actually land on.
4. **If consensus is ambiguous, disassemble.** This is how FF7 Remake was cracked (below), and it
   is often faster than it sounds — one `dump_func.java` on a candidate site settles it.
5. Add the row to **`sweep.sh`**, then re-run the **whole** sweep, not just the new game.

### The FF7 Remake derivation (worked example)

FF7R has no PDB and its GObjects consensus was empty — the generic patterns each resolved
somewhere different. A candidate GEngine pattern produced exactly one hit; dumping that site
(`FUN_140FD1490`) showed:

```
  MOV    RCX,[0x145879EE8]     ; <- loaded as `this`
  CALL   FUN_1416C7CE0         ; ...returns a UWorld   => this is GEngine
  MOVSXD RAX,[RBX + 0xC]       ; UObject::InternalIndex of that UWorld
  CMP    EAX,[0x1453BD48C]     ; ObjObjects.NumElements  (Objects + 0xC)
  LEA    RDX,[RAX + RAX*2]
  MOV    RAX,[0x1453BD480]     ; ObjObjects.Objects      => GUObjectArray + 0x10
  LEA    RCX,[RAX + RDX*8]     ; 24-byte FUObjectItem stride
  MOV    EAX,[RCX + 8]         ; FUObjectItem.Flags
```

so `GEngine = 0x145879EE8` and `GUObjectArray = 0x1453BD470`. Both independently corroborated:
`GOBJ_RE2` + `GOBJ_V12` agree on `0x1453BD470`, and a second candidate shape on `0x145879EE8`.
GNames/GWorld are deliberately **left unset** — the consensus is suggestive but unproven, and a
guessed truth is worse than none (it mislabels every hit as a decoy).

## Reading the report

`aggregate_sweep.py` writes `out/sweep/REPORT.md`. Section 1 decides whether a change is safe.

**Model note — read this before trusting a verdict.** `scan_patterns.java`'s `>>> SELECTED` line
names the first pattern that *hits*. That is NOT what the runtime settles on:
`Genau::ScanForTarget` validates every match and moves on when they all fail, returning only on
the first that validates. So a `DECOY-ONLY` pattern at the top of the list is a **fall-through
(cost)**, not a wrong answer **(correctness)**. The report's regression matrix replays the real
walk and shows the pattern actually landed on plus the validations wasted getting there. Reading
the raw SELECTED line as "what we resolve to" overstates risk; ignoring the fall-through
understates cost.

| Verdict (per pattern) | Meaning |
|---|---|
| `UNIQUE-OK` | every hit resolves to the true VA |
| `OK-FIRST` | reaches truth, and the correct site is scanned before its decoys |
| `OK-BEHIND` | reaches truth, but decoys scan first — the validator must reject them |
| `DECOY-ONLY` | hits, never correct → the runtime falls through to the next pattern |
| `MISS` | no hits |
| `NO-TRUTH` | no usable truth supplied — verdict withheld on purpose, **not** evidence of anything |

One more thing the cost numbers do NOT say: patterns are scanned in **batches of 8**, and
ScanForTarget returns on the first validated match, so a pattern that wins from batch 1 avoids
every later `.text` pass. Rejecting a few hundred candidates by validation is much cheaper than
an extra AVX2 sweep of a 130 MB `.text`. Do not demote a noisy pattern that is also a *winner*.

## `GS_TRUE` reference

`GObjects` accepts two values because `ValidateGObjects` matches both the `FUObjectArray` base
and its `ObjObjects` sub-struct (base + `0x10`).

For **modular** builds every entry must carry a `programNameSubstring:` prefix. Their DLLs all
share image base `0x180000000`, so their address ranges OVERLAP — an unscoped union would score
a hit inside `Core` as a correct `GObjects`, which lives in `CoreUObject`. Use substrings that
cannot alias: `-Core-Win64` does not match `-CoreUObject-Win64`.

```sh
# UE 4.18 — FF7 Remake. DERIVED BY DISASSEMBLY, not a PDB. GNames/GWorld intentionally absent.
GS_TRUE="GObjects=1453bd470|1453bd480,GEngine=145879ee8"

# UE 4.18 — DRAGON QUEST XI S. DERIVED BY DISASSEMBLY. The process is ASLR-relocated; the image
# base was recovered from FFixedUObjectArray's shape at 145d83c08 (Objects / +8 Max / +0xC Num,
# 24-byte FUObjectItem, non-chunked), so the array base is that minus 0x10. GEngine confirmed via
# UWorld::GetGameViewport + GetRealTimeSeconds; GWorld and GEngine also appear in one function.
# GNames INTENTIONALLY ABSENT — 4.18 predates FNamePool and every GNames pattern here is
# FNamePool-shaped, so the consensus is noise. No sparse delegates before 4.23.
GS_TRUE="GObjects=145d83bf8|145d83c08,GWorld=145e70c98,GEngine=145e6eeb0"

# UE 4.27 — DRAGON QUEST VII Reimagined. DERIVED BY DISASSEMBLY (all globals live in `.shared`).
# GEngine is 145ff4b28. The live runtime log's most-hit candidate, 145d76d78, is a GAME-SIDE
# singleton: GENG_X4 alone accounts for 50 of its 55 hits, and no UWorld/FWorldContext semantics
# appear at any of them. Do not "correct" this back to the runtime value.
GS_TRUE="GObjects=145ea7660|145ea7670,GNames=145e6b300,GWorld=145ff8470,SparseDelegates=145b26520,GEngine=145ff4b28"

# UE 5.4 — The Adventures of Elliot. DERIVED BY DISASSEMBLY; the corpus's only 5.4. All five
# corroborated (GetGameViewport, FNamePool::Resolve, the NotifyUObjectDeleted twin-ref that pins
# Sparse and GObjects in one function). Note this game reports 4.27 in its PE as a publisher
# fallback — it is 5.4.
GS_TRUE="GObjects=149bfc140|149bfc150,GNames=149b18600,GWorld=149d8bda0,SparseDelegates=14990e1c0,GEngine=149d8e290"

# UE 4.20 — Everspace. No FNamePool (TNameEntryArray era) and no sparse delegates (4.23+).
GS_TRUE="GObjects=142e797f0|142e79800,GNames=1431dead8,GWorld=1432e1ac0,GEngine=1432df470"

# UE 4.22 — Satisfactory (monolithic). GNames = `Names`, the TNameEntryArray* lazily new'd in
# FName::GetNames @0x140BCEBF0 (the load is at +4). Pre-4.23: no sparse delegates.
GS_TRUE="GObjects=144006f80|144006f90,GNames=144002a78,GWorld=1441073b8,GEngine=144104e58"

# UE 4.24 — DropIn (a second, older build of the same unplayable VR title). NamePoolData from
# FNameDebugVisualizer::GetBlocks @0x141323510 = `lea rax,[0x1471BCA10]; ret`, minus 0x10.
GS_TRUE="GObjects=1471db720|1471db730,GNames=1471bca00,GWorld=1472ea620,SparseDelegates=146da38d0,GEngine=1472e74a0"

# UE 4.25 — Everspace 2 (Steam depot). NamePoolData from FNameDebugVisualizer::GetBlocks
# @0x140EF8410 = `lea rax,[0x144497D10]; ret`, minus 0x10.
GS_TRUE="GObjects=1444b0520|1444b0510,GNames=144497d00,GWorld=1445f1160,SparseDelegates=1440070c0,GEngine=1445edad8"

# UE 4.26 — Satisfactory, MODULAR (4 DLLs in one project).
GS_TRUE="-CoreUObject-:GObjects=1803f9210|1803f9220,-CoreUObject-:SparseDelegates=1803f37d0,-Core-Win64:GNames=180659380,-Engine-:GWorld=18182a0b8,-Engine-:GEngine=181826658"

# UE 4.27 — DropIn. NamePoolData from FNameDebugVisualizer::GetBlocks
# @0x1426F59C0 = `lea rax,[0x14A363950]; ret`, minus 0x10.
GS_TRUE="GObjects=14a3aa670|14a3aa660,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910,GEngine=14a528890"

# UE 5.1 — Grimhook. The FIRST symbolised 5.1; every 5.1 claim before it rested on Palworld's
# unproven consensus. NamePoolData from FNameDebugVisualizer::GetBlocks @0x141BEEAD0 =
# `lea rax,[0x14639E150]; ret`, minus 0x10 — and independently corroborated by its 58 xrefs
# being FName::ToString / GetEntry / AppendString / FLazyName::Resolve loading it as the pool.
# Stock layout: 24-byte FUObjectItem, UObject* at +0x00, chunked (FChunkedFixedUObjectArray).
# Version confirmed structurally, not from the label: the PDB's EUnrealEngineObjectUE5Version
# terminates at ADD_SOFTOBJECTPATH_LIST = 1008, which is exactly 5.1 (5.0 stops at 1004,
# 5.2 adds 1009).
GS_TRUE="GObjects=14643d800|14643d810,GNames=14639e140,GWorld=1465aa438,SparseDelegates=1461417d0,GEngine=1465a6630"

# UE 5.2 — Satisfactory, MODULAR (project SF521_pdb, built by us — see above). Cross-checked
# against the DLL export table: GEngine RVA 0x1CD1140, GWorld 0x1CD4828, GUObjectArray 0x4194D0.
GS_TRUE="-CoreUObject-:GObjects=1804194d0|1804194e0,-CoreUObject-:SparseDelegates=1803edcb0,-Core-Win64:GNames=18073d0c0,-Engine-:GWorld=181cd4828,-Engine-:GEngine=181cd1140"

# UE 5.5 — Everspace 2. Needs the one-time language upgrade described above.
GS_TRUE="GObjects=149aa7ef0|149aa7ee0,GNames=149c009c0,GWorld=149b37d18,SparseDelegates=149aa7e90,GEngine=149da5810"

# UE 5.5 — Everspace 2, 2025-06-17 build (two manifests newer than ES2-0517). Every global moved
# ~-0x2000 versus that snapshot, so this is a real "does a pattern survive a game update?" test
# and not a trivially identical binary. It passes: both builds land on the SAME patterns with
# the SAME cost for all five targets.
GS_TRUE="GObjects=149aa5f60|149aa5f70,GNames=149bfe940,GWorld=149b35dd8,SparseDelegates=149aa5f10,GEngine=149da37b0"

# UE 5.5 — Meltopia (monolithic, PDB applied via MSDIA). NamePoolData from
# FNameDebugVisualizer::GetBlocks @0x141270620 = `lea rax,[0x149CA3C90]; ret`, minus 0x10.
GS_TRUE="GObjects=149d87420|149d87430,GNames=149ca3c80,GWorld=149f03d10,SparseDelegates=149a9a070,GEngine=149f002f8"

# UE 5.6 — Satisfactory, MODULAR. The name pool moved CoreUObject -> Core at 5.6.
# CrashReportClient.exe in the same project is a bonus MONOLITHIC 5.6 oracle (it links no Engine
# module, so it legitimately has no GWorld/GEngine).
GS_TRUE="-CoreUObject-:GObjects=1805a3620|1805a3630,-CoreUObject-:SparseDelegates=1805661a0,-Core-Win64:GNames=18082e8c0,-Engine-:GWorld=18216db68,-Engine-:GEngine=182170748,CrashReportClient:GObjects=141a9d4b0|141a9d4c0,CrashReportClient:GNames=1419c7e80,CrashReportClient:SparseDelegates=1419307f0"

# UE 5.7 — Solarpunk.
GS_TRUE="GObjects=1476ca920|1476ca910,GNames=1478e50c0,GWorld=1478cce58,SparseDelegates=1476ca4b0,GEngine=147a2fc20"

# Noise probes — pass NO GS_TRUE at all.
```

## Exported symbols (the O(1) path)

Modular builds export the globals, and `Genau::TrySymbolExport` enumerates every loaded module,
so these resolve via `GetProcAddress` before any scan runs. Verified with
`py tools/pe/pe_imports_exports.py exports <dll>`:

| Symbol | Exported by | Confirmed on |
|---|---|---|
| `?GUObjectArray@@3VFUObjectArray@@A` | CoreUObject | 4.26, 5.2, 5.6 |
| `?GWorld@@3VUWorldProxy@@A` | Engine | 4.26, 5.2, 5.6 |
| `?GEngine@@3PEAVUEngine@@EA` | Engine | 4.26, 5.2 |
| `?GMalloc@@3PEAVFMalloc@@EA` | Core | 4.26, 5.2 |

`NamePoolData` / `GNames` are **not** exported anywhere — which is why GNames has only
`SymbolCallFollow` entries (resolve `FName::ToString`, then scan its body for the pool
reference).
