# Todo

Open work only. **Read this when deciding what to do next.**

> **2026-06-06 cleanup.** This file was slimmed to open items only. The full
> pre-cleanup history (every shipped build's effort/risk retrospective, files
> touched, test counts, decision rationale) is frozen in
> [archive/todo-completed-build-937.md](archive/todo-completed-build-937.md).
> The running milestone log is [dev-log.md](dev-log.md).
>
> **Conventions:** each item is **flat and self-describing** — the title decodes
> its session shorthand (e.g. "V3-C", "#5 v2"), and a trailing *parent* line gives
> the one-line context (which already-shipped work it follows + the dev-log build).
> **Effort** S/M/L/XL (S=hours · M=1 session · L=multi-session · XL=weeks).
> **Risk** low/med/high (chance of breaking existing behaviour / perf regression).
> When an item ships: write it up in [dev-log.md](dev-log.md), update
> [roadmap.md](roadmap.md) if capability changed, then **delete it here** (don't
> strike-through — the archive holds the history).

-----

## ▶ Ghidra is out of the sweep — SHIPPED 2026-08-01, acceptance passed (build 2545)

`py tools/ghidra/pe_sweep.py` replays the whole signature database from the **game binaries**, with
**byte-identical output**, no JVM and no Ghidra install. Measured on the laptop (32 logical cores,
8 Python workers): **138 s** against **773 s** for `sweep.sh` at `SWEEP_JOBS=3` — and the 138 s
figure is the same whether or not a Ghidra JVM is running alongside it.

* **`compare_sweeps.py out/sweep-ref-ghidra2 out/pe-sweep` → 210/210 files byte-identical**,
  0 differing, 0 only-in-B. `aggregate_sweep.py`'s `REPORT.md` matches too, modulo one line naming
  the broken imports the PE route cannot produce. Matrix unchanged: **162 ✅ / 59 ⚠️ / 2 ❌** over
  55 oracle rows / 70 programs.
* **`check_pe_memory.py` → 70/70 EXACT** — image base, complete block map and a **per-block MD5**,
  all reconstructed from the PE. The 74 Ghidra-side maps are committed
  ([`tools/ghidra/memory-maps/`](../tools/ghidra/memory-maps/), 62 KB) so this runs on a machine
  with the archive and no Ghidra, and so `pe_memory.py` has a regression oracle.

Ghidra is still required to **author** a new AOB (decompiler, xrefs, symbols). Only the replay
moved.

### The re-import rule is now SATISFIED — and still does not license deleting anything

`py tools/ghidra/reimport_verify.py` rebuilds a project from the archived binary and grades it on
the executable hashes, a **SHA-256 over the whole symbol table**, the block map with per-block MD5,
and byte-identical sweep output. Measured 2026-08-01: `UE4.10-Game` (`-noanalysis`)
**REBUILT-IDENTICAL** in 95 s; `FactoryGame-CoreUObject` (4.2 MB, 684,805 instructions, full PDB +
analysis) **REBUILT-IDENTICAL** in 422 s. Two `--analyze` rebuilds of one input came out
field-for-field identical, so Ghidra's analysis is deterministic here. Full write-up in
[corpus-preservation.md](corpus-preservation.md) §0b/§0c and [dev-log.md](dev-log.md).

**Backup status: `X:\Ghidra_Projs_Backup` lives on the OTHER machine, not this one.** An earlier
draft of this section called the corpus "single-copy" on the strength of `X:` being absent *here* —
that is a one-machine observation stated as a global fact, and it is withdrawn. What remains true
is that a rebuild costs minutes per binary for the 42 PDB-loaded, disassembled programs, so
rebuilding is a recovery path, not a substitute for the copy.

Four things to keep in view when the deletion decision is actually made:

* ~~`ES2-0517`'s per-open language upgrade~~ — **FIXED 2026-08-01.** Upgraded in place (one run
  without `-readOnly`): **12 m 43 s once**, and the same scan went from **>10 min, not finishing**
  to **30 s**. Behaviour-preserving — scan/consensus/blocks byte-identical, symbol digest and all
  507,555 functions / 28,635,821 instructions unchanged. It was the only project needing it.
  Upgrading beats re-importing: the migration keeps the analysis, a re-import would re-run it.
  See [corpus-preservation.md](corpus-preservation.md) §0d.
* **18 patterns have exactly one program where they resolve correctly** — Satisfactory 7 across
  four DLLs, UE4.22-Satisfactory 4, Solarpunk 4, Everspace 2. That reads as a reason to keep those
  `.rep`s and is not: `pe_sweep.py` reads binaries, so the risk attaches to the **binary**, and all
  **11 sole-source programs have an archived binary** (0 missing).
* The 74 **identity fingerprints are committed** ([`tools/ghidra/identity/`](../tools/ghidra/identity/),
  163 KB). They are what outlives a `.rep` — delete a project without one and there is nothing left
  to verify a future rebuild against.
* Four projects hold a **stub re-import** beside the real program (image base `0000:0000`, ~1 KB of
  DOS header mapped as code). They are pure Ghidra artifacts, and their scan output is noise.

-----

## ▶ Corpus state as of 2026-07-29 (build 2505) — the sweep is CURRENT

`sweep.sh` is at **57 rows**. A full sweep ran 2026-07-29 and `out/sweep/REPORT.md`, `Himmel.h`'s
header counts (**70 programs / 55 oracles**, UE **4.10–5.8**) and this file all agree.
`preflight.py` returns **`GO (exit 0)`** — the manifest was regenerated and covers exactly the 57
sweep tags. Nothing is stale or blocked. Matrix: **162 ✅ / 59 ⚠️ / 2 ❌**.

⏱ **The full sweep costs minutes, not the ~30–50 min the docs claimed for months — but WHICH minutes
depends on the machine.** Measured at `SWEEP_JOBS=3`: **4m38s on the desktop** (9950X3D, 57 rows) and
**12-15 min on the laptop** (9955HX3D, 57 rows / 74 programs, internal NVMe; the spread is cache state) — a **2.6-3.1×**
spread, so never quote one without the other; `GROUND-TRUTH.md` carries the table. Those *old*
~30–50 min figures were never taken at all — they date from the pre-script era of hand-running each
project *with* Auto Analyze, and one was even "updated" by scaling the wrong number with the row
count. **So never reach for a tag filter to save time**; use one only to
isolate a row while debugging. The correctness argument for always running the full sweep (a
filtered run leaves `REPORT.md` describing a corpus that no longer exists) now costs nothing.

**One ❌ in the regression matrix, and it is deliberate: UE 4.10 GObjects on both rows. Leave it.**
It measures the pre-4.11 support floor rather than asserting it — full reasoning in `Himmel.h`'s
corpus block and GROUND-TRUTH.md §"Settled facts". Do not mine a `GetUObjectArray` pattern to
"fix" it; 4.10 has no `FUObjectItem` at all, so finding the address would not make it readable.

Tooling, all no-Ghidra: `tools/pe/pdb_globals.py` (truth from a PDB — now also points at the
pre-4.11 magic-static route when GObjects has no symbol), `tools/ghidra/replay_patterns.py`
(corroborate by byte replay), `tools/pe/func_bytes.py` (is a function hollow?),
`tools/ghidra/capture_provenance.py` (build-identity snapshot).

⚡ **Adding a new engine VERSION is now nearly free — check for prebuilt targets first.** A
launcher-installed engine ships monolithic `UE4Game*.exe` / `UnrealGame*.exe` **with full PDBs** in
`Engine/Binaries/Win64`. Surveyed on this machine: 4.23 / 4.27 / **5.4** / 5.7 / 5.8 have all three
configs; 4.10 and 4.15 have Shipping + Development. That is what made 4.10 possible at all (it
needs VS2015, which is not installed). **UE 5.4 is installed and ready to harvest with no
packaging and no compiler** — copy, `pdb_globals.py`, import `-noanalysis`, add rows.

-----

## Build-identity signals: what `duplicate_copies` and PE link timestamps can and cannot tell you

*Parent: the 2026-07-29 manifest regenerate; [dev-log.md](dev-log.md) build 2505.*
**Effort S · Risk low. The `duplicate_copies` half is FIXED; the timestamp half is a rule to follow.**

### FIXED — `duplicate_copies` was silently empty on exactly the rows that need it

The regenerate wrote, for Palworld: `duplicate_copies: []` and *"the `.rep` is the last copy"* —
while **two** byte-identical copies of the corpus build sat in `Game Binary backup`.

⚠ **The first diagnosis of this was wrong and the correction matters.** It was NOT "compares
against today's bytes instead of `binary_md5`" — line 302 has always compared against
`rec['exe_md5']`, Ghidra's import-time hash, i.e. the corpus build. The real cause was a **cheap
size prefilter** sizing candidates against whatever file sits at `binary_last_seen` *today*. On a
DRIFTED row that is the build which REPLACED the corpus one, so the surviving copy — which has the
old size — was skipped **before its md5 was ever computed**. An optimisation valid only under the
assumption "the file on disk is the corpus build", which is precisely false where it matters.

Fixed by passing `size_prefilter=(state == 'MATCH')`. Verified: Palworld `0 → 2` copies.
**Generalise the shape, it is the reusable part:** when a fast path guards a correct check, ask
what the guard assumes — a wrong guard makes the correct check unreachable and looks like a
confident negative. Note also that a null reads as *unknown* while `[]` plus that note is a
**positive false claim**, which is why this was worse than the `steam_buildid` nulling.

### RULE — a PE `TimeDateStamp` is a date ONLY if `IMAGE_DEBUG_TYPE_REPRO` is absent

Worth knowing because file mtime dates the *copy*, not the build, and the COFF `TimeDateStamp`
looks like the fix. Sometimes it is. **There is an authoritative test, so never guess:** with
`/Brepro` (reproducible builds) the linker overwrites that field with a **content hash** and emits
a debug-directory entry of **type 16 (`IMAGE_DEBUG_TYPE_REPRO`)**. That entry IS the answer.
`tools/pe/pdb_match.py` now reports it on every check.

⚠ **Plausibility is worthless, and an earlier pass of this entry got it wrong by relying on it.**
It classified by "does the value fall in a 2000–2030 window", which is true of roughly a fifth of
32-bit hashes. Two corpus rows are hashes reading as perfectly ordinary dates:

| binary | reads as | actually |
|---|---|---|
| **Hogwarts Legacy** (4.27) | `2025-11-12` | **`/Brepro` hash** — and the earlier pass called it REAL |
| **The Adventures of Elliot** (5.4) | `2026-07-15` | **`/Brepro` hash** |
| UE 5.7 StackOBot | `2022-09-28` | hash (before 5.7 existed) |
| UE 5.4 Shipping | `2039-01-13` | hash (the value that prompted the recheck) |

**It is per-CONFIG, not per-version** — the other thing the earlier pass got wrong. Measured across
the self-built oracles, where the builder and toolchain are controlled:

| | Shipping | Development | DebugGame |
|---|---|---|---|
| 4.15 / 4.23 / 4.27 | real | real | real |
| 5.3 / 5.4 / 5.7 | **`/Brepro`** | real | real |
| 5.8 | **`/Brepro`** | `TimeDateStamp = 0` | `TimeDateStamp = 0` |

So Epic's UBT enables `/Brepro` on **Shipping only**, from ~5.3, and 5.8 non-Shipping zeroes the
field outright — a third state that is neither a time nor a hash. This also kills the "cross-config
spread" discriminator a previous pass proposed: the 5.4 spread was huge because it compared a hash
against a *real* time, not because both were hashes. Right conclusion, wrong reasoning, and it
would fail whenever both sides are hashed.

**Third-party studios choose for themselves.** Hogwarts is `/Brepro` at **4.27** while DQ7R, the
same engine version, is not. So for a shipped game the UE version predicts nothing — test the flag.
And since every shipped game is a Shipping build, the useful-signal case is the *rare* one.

Bottom line: usable as a corroborating signal only when type 16 is absent, and never as the primary
answer. When the question is "is this the same build?", skip timestamps entirely — `binary_md5`
answers it exactly, and for a PDB the CodeView **GUID+Age** is a true per-link identity. A
`/Brepro` hash is still deterministic per link, so it works as a weak identity — just never as a
clock.

-----

## UE5 non-Shipping: GNames reaches nothing — decide whether to mine a pattern

*Parent: the 2026-07-29 PDB+replay pass. Full evidence in
[GROUND-TRUTH.md](../tools/ghidra/GROUND-TRUTH.md) §Still open.* **Effort S–M · Risk med.**

**On a non-Shipping UE5 build, GNames survives on ONE pattern and costs ~2,300 wasted validations
to get there.** Sweep-verified 2026-07-29: it lands on **`GNAM_V1`** (priority 870, 4 literal
bytes) after **2,199 / 2,369 / 2,372 / 2,424** rejected candidates on 5.7.4-DbgG / 5.8.0-DbgG /
5.8.1-Dev / Titan — **the four most expensive fall-throughs in the corpus**, next worst 475. It is
**config, not a version regression**; every Shipping build resolves normally, so **no shipped game
is affected**:

| | 4.10.4 | 4.15.3 | 4.23.1 | 4.27.2 | **5.3** | **5.4.4** | 5.7.4 | 5.8.0 | 5.8.1 |
|---|---|---|---|---|---|---|---|---|---|
| Shipping | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ n=11 | ✅ n=11 |
| Development / DebugGame | ✅ | ✅ (1w) | ✅ n=16 | ✅ n=16 | ✅ **15/15, 0w** | ⚠️ **1/6, 2240w** | ⚠️ 1/8 | ⚠️ | ⚠️ |

⚠ **The boundary is NOT at 5.8** — the first pass called it a 5.8 thing and that was wrong; 5.7.4
DebugGame behaves identically.

### ✅ BISECTION CLOSED 2026-07-29 — the edge is **5.3 → 5.4**

Stock UE 5.4.4 ThirdPerson (all three configs) settled it in **one** install, not the two this item
budgeted for. 5.3 Dev/DebugGame land `GNAM_ES53_1` with **15/15 patterns correct and zero** wasted
validations; 5.4 Dev/DebugGame drop to **1/6 correct, landing `GNAM_V1` after 2,240** — already the
full collapse, indistinguishable from 5.7.4/5.8.x. Both 5.4 configs report the identical 2,240,
consistent with UE building DebugGame's engine modules optimized like Development.

So **5.5 and 5.6 are no longer needed for this question.** Whatever they add is coverage, not
bisection. And if a fix pattern is ever mined, **5.3-vs-5.4 is the pair to mine it against** — the
smallest interval that contains the change, with a clean control on one side.

4.10 and 4.15 extend the healthy band downward, so this is a sharp UE5-era edge, not a slow drift.

**Not project-specific.** A second, unrelated 5.8.0 DebugGame project (Titan) matches StackOBot's
coverage *down to the individual pattern IDs* — same GObjects quartet, same GWorld n=13, same
GEngine quartet, GNames 0 with the same `{CT3, CT4, G42_1}` decoy. Build configuration is the only
remaining variable.

Root cause is a hardcoded destination register — the `GOBJ_V1`-on-DropIn failure mode one target
over. The twin-LEA lazy init is there (46 xrefs to `NamePoolData`), but the first LEA targets
**rbx (`48 8d 1d`) / r15 (`4c 8d 3d`)** and every GNames pattern pins rax/r8/rdx/rsi/rbp.

Decision needed, because it is genuinely marginal:
- **Do nothing** (default). Nobody attaches to a Development build of a template project, and the
  candidate fix `4? 8d ?? <d32> eb ?? 48 8d 0d <d32> e8` has only ~2 literal bytes at its head —
  in the band where `GWLD_G42_4` proves wildcarding backfires.
- **Or mine it**, and put it through the full 65-program gauntlet before it goes anywhere near the
  table. If it survives decoy-free it is also insurance for a shipped game whose register pressure
  happens to land the same way.

All four affected rows are in `sweep.sh` and swept, so the cost is visible as a ⚠️ with its wasted
count in the regression matrix instead of being invisible. **Leave them showing that** until fixed
— note they are ⚠️ (lands correct, expensively), not ❌; the only ❌ in the corpus is 4.10 GObjects.

**Second result from the same pass, and arguably the more important one — rule 5 just paid out.**
The sparse-delegate patterns that were kept purely as redundancy are the *only* thing holding that
target up on non-Shipping builds: on 5.8 `SPARSE_ES2_1` misses and **`X1`/`X2` alone** reach it,
and on **5.7.4 DebugGame those miss too and `SPARSE_MEL55_1` is the sole survivor (n=1)** — the
thinnest coverage anywhere in the corpus. Every one of the three was added against a Shipping
binary that already resolved, i.e. they looked like dead weight at the time. Had any been pruned,
a whole build configuration would have silently lost sparse-delegate support.

All three of the non-Shipping oracles this item was written against (5.7.4 DebugGame, 5.8.0
DebugGame, 5.8.0 Titan DebugGame) are imported `-noanalysis` and swept — the sweep reads raw bytes
and never needs auto-analyze, which is what had made a 300 MB Development project look
un-importable.

-----

## Next self-built oracles: **5.4 and 5.3 are DONE — 5.6 / 5.5 are now OPTIONAL**

*Parent: the 5.3 + 5.4 builds, 2026-07-29. Shipped in [dev-log.md](dev-log.md).*
**Effort M each · Risk low.**

**Both bisection steps are spent, and they answered the question early.** Stock 5.3 and stock
5.4.4 ThirdPerson, each built in all three configs, imported `-noanalysis`, rows in `sweep.sh`:

| | 4.10 | 4.15 | 4.23 | 4.27 | **5.3** | **5.4.4** | 5.5 | 5.6 | 5.7.4 | 5.8.x |
|---|---|---|---|---|---|---|---|---|---|---|
| non-Shipping GNames | ✅ | ✅ | ✅ | ✅ | ✅ 15/15 | ⚠️ **1/6** | – | – | ⚠️ 1/8 | ⚠️ |

**The edge is 5.3 → 5.4.** That was budgeted at two installs (5.4 *and* 5.6) and cost one, because
5.4 collapsed outright rather than landing mid-interval. **5.5 and 5.6 no longer carry a bisection
argument** — judge them purely on coverage now:

- **5.6** — still the more interesting of the two: the `UEnum::Names` → `FNameData` change
  (struct-of-arrays + tagged pointers, the `Neu` module) has no non-Shipping row, and 5.6's only
  PDB oracle is `CrashReportClient`, which is not a game.
- **5.5** — the weakest remaining case, and it was already last: three symbolised Shipping oracles
  exist (Everspace 2 ×2, Meltopia). Worth doing only as part of a gameplay-matrix pass.

Neither is on the critical path for anything. **Do them when a reason appears, not on schedule.**

### 5.4 was packaged, not taken from the prebuilt target — deliberately

**UE_5.4 ships prebuilt `UnrealGame{,-Win64-Shipping,-Win64-DebugGame}.exe` with full PDBs** in
`Engine/Binaries/Win64` (so do 4.23 / 4.27 / 5.7 / 5.8; 4.10 / 4.15 ship two of three), and for AOB
truth alone that is free — copy → `pdb_globals.py` → import `-noanalysis`. It is what made 4.10
possible when VS2015 was unavailable.

⚠ **But it does NOT cover the gameplay-feature matrix.** `UnrealGame.exe` is the bare engine
default with no content and no `ACharacter` to possess, so it answers "where are the engine
globals" and nothing else — GodMode/Teleport/Laufen/Hemmung all need a real pawn. That is why 5.4
was packaged as ThirdPerson anyway: **those binaries serve both jobs.** Use the prebuilt shortcut
when you only want AOB rows; package when the version is also a gameplay target.

### THE ENGINE INSTALL IS TRANSIENT — that is what makes the packaged half affordable

5.3 established the pattern: **install → package 3 configs → `-noanalysis` import → DELETE the
engine.** What stays is ~3.2 GB of packages + PDBs (mirror it to `X:` like the rest); the ~114 GB
engine is temporary. Verified on 5.3 before deleting it: the `.rep`s are self-contained, the
packages are runnable standalone (pak + launcher exe), and both D: and X: hold byte-identical
copies. So this is not "which one can I afford" — it is a sequence, each step costing ~3 GB
permanently.

**One template is enough — do NOT package two.** An earlier version of this item said to build
Flying *and* ThirdPerson. Measurement supersedes that: at 4.27, Flying vs 3rdPerson gave
**identical voter sets down to the individual pattern IDs**, so the template does not affect
engine-global resolution at all. Use **ThirdPerson**, because it is also the Character-based target
the gameplay-feature matrix needs (Flying's pawn has no `CharacterMovement`).

### What 5.4 delivered besides the bisection — and it was ordered first for these, not for that

The ordering argument said the exact boundary version was worth *less* than durable corpus value,
so 5.4 went first on coverage grounds and the bisection was treated as a 1-in-4 bonus. Both paid:

1. **Every UE5 version now has a symbolised oracle.** 5.4 was the last one without. Elliot is 5.4
   but PDB-less and disassembly-derived — and the new stock Shipping row **corroborates it**
   (GObjects 8/15 vs 9/15, GNames 13/16 vs 13/17, GWorld 15/16 vs 13/14), the first independent
   check that row has ever had.
2. **MindsEye finally has a stock-5.4 control.** The engine is **5.4.4 — MindsEye's exact patch
   version.** `mindseye-fork-notes.md` is a whole re-derivation playbook whose "the fork changed
   X" claims all rested on inference about stock 5.4; each is now a measurable delta. Same
   evidentiary shape as the Avowed/DropIn gaps closed the same week.
3. The bonus landed too — it pinned the boundary outright.

Remaining, judged on coverage alone:

2. **5.6** — the `UEnum::Names` → `FNameData` change (struct-of-arrays + tagged pointers, the `Neu`
   module). 5.6 has a monolithic PDB oracle already (CrashReportClient) but no non-Shipping row.
3. **5.5** — last, precisely because it is already the **best-covered** version: three symbolised
   Shipping oracles (Everspace 2 ×2, Meltopia). Its non-Shipping row pairs against real data, which
   is nice but is the smallest marginal gain of the three.

### No C++ project needed — the 5.3 lesson, and it generalises

A C++ project on a launcher engine can fail in UBT (*"must be compiled with Visual Studio 2022 17.4
(MSVC 14.34.x) or later … detected 14.29.30159"*). The message blames the VS version and a forced
`VisualStudio2019` setting; **both are wrong**. It is toolset *ranking*: UE ranks families it does
not know as `FamilyRank=4`, so a recognised-but-too-old **14.29 (from VS2026's v142 component)**
ranks 3, outranks a perfectly usable 14.44, and then fails the `>= 14.34` gate. **Nothing needs
fixing** — the launcher ships `UnrealGame{,-Win64-DebugGame,-Win64-Shipping}.exe` **with PDBs**, so
a Blueprint-only project packages all three configs with nothing compiled.

Also extended BACKWARDS: **4.15.3 Development + DebugGame** rows added (the oldest config group in
the corpus). `pdb_globals.py` gained a pre-4.23 GNames route for them — `FName::GetNames`'s load at
+4, **no** `-0x10` — validated by reproducing the 4.15 Shipping row's recorded `GNames=142c92508`.

**5.3's engine can be deleted** (~114 GB) — checked before saying so: its three `.rep`s are
imported and self-contained, its packages run standalone, and D: and X: hold byte-identical copies
(3168 MB / 100 files / 3 PDBs each). The only thing lost is the ability to rebuild a *different*
5.3 sample, and per the note above a second template would add nothing anyway.

-----

## Gameplay-feature regression matrix on the self-built samples (Teleport tab et al.)

*Parent: "can the PDB corpus improve Teleport/GodMode accuracy?", 2026-07-29.* **Effort M · Risk low.**

**Not via offsets — that question resolves to "no", and by design.** `Solitar`, `Laufen`, `Hemmung`
and `Wirbel` contain **zero hardcoded struct offsets** (verified by grep); everything binds through
UE reflection by NAME (`CanBeDamaged`, `CustomTimeDilation`, `CharacterMovement`, `MaxWalkSpeed`,
`GetHitResultUnderCursorByChannel`, …), per the CLAUDE.md rule. Runtime reflection is *more*
authoritative than a PDB — it is the data the game itself uses, so it tracks licensee forks a PDB
cannot. Using a PDB to "correct" an offset would be a step down, not up.

> **▶ THE SAMPLE IS WRITTEN — [`tools/ue-sample/`](../tools/ue-sample/README.md) (2026-08-05).**
> A stock **UE 5.4 Third Person** project plus one `ADumperTestActor` carrying a deliberate property
> zoo, spawned by a `UWorldSubsystem` so **no binary level asset has to be edited**. It exists
> because a large share of the ⬜ register is blocked not on effort but on *finding a game that
> happens to contain the right UPROPERTY*: `TSet`/`TMap` (⬜ since build **927**), `TOptional`
> (**942**), the NumericAll byte family (**796**), **B28** CJK FText, and **B8**, whose blocker was
> *"needs a game that actually goes quiet when backgrounded"* — solved by setting
> `t.IdleWhenNotForeground` **from code** behind a `-DumperTestIdle` switch, which also turns
> Grausam's foreground lock into a **positive** test rather than "it seemed to keep working".
> (Not from an ini: that cvar is `ECVF_Cheat`, and an ini that sets one makes the project
> **impossible to cook** — 22 errors, `ExitCode=25`. Measured, not guessed.) Expected values are written down in that README, so a
> disagreement is a defect rather than a discussion. **Still to do: package it** (Shipping +
> Development, ~20 min in the editor) — the source is versioned, the binaries are not.

**The real win is a reproducible live test target.** Today every one of these features is verified
ad hoc on a commercial title — *"LIVE-VERIFIED P3R"*, *"VERIFIED Tower of Mask + DQ7R"*, *"NO-OP on
FF7R"* — one-shot, unrepeatable, and gated on owning and launching that game. The self-built samples
are runnable, free, symbol-carrying, and exist at six engine versions **with source**. That converts
"someone tested GodMode once" into a matrix, and it is the direct answer to the pile of
*"needs in-game verify"* / *"⏳ in-game verify"* / *"UNVERIFIED"* items in this file.

**Highest-value first cell: reflected-UFunction survival, Shipping vs Development, same project.**
The repo already knows the failure mode ([lessons-learned.md](lessons-learned.md) §UCheatManager):
`UCheatManager::Fly/Ghost/God/Slomo` **invoke successfully and do nothing** in cooked Shipping —
the bodies are `#if !UE_BUILD_SHIPPING`, but the `UFUNCTION(exec)` metadata is generated pre-cook and
survives. A same-project Shipping/Development pair *measures* which reflected functions get hollowed
out instead of discovering it per-game. **This is the DI427 `check()`-gating story one layer up** —
same source, same engine, config the only variable.

**What a PDB genuinely could add, with a caveat that narrows it.** `Schlacht`'s `FHitResult` is the
one non-reflected struct (UE4 `.Actor` weak-ptr vs UE5 `HitObjectHandle`); it currently locates the
field by sub-field NAME and dumps the layout when it fails, which is already fairly robust. PDB type
info could pin the layout per version — except GROUND-TRUTH records that these StackOBot PDBs carry
**only partial merged type info** (`FFieldClass`/`UObjectBase`/`FUObjectItem` have no TPI record at
all). For layout and API-name questions the **installed engine source** is the better oracle.

-----

## AOB specificity index (§6) — MEASURED and feasible, build it. Block library (§4) second.

*Parent: [aob-block-library-eval.md](aob-block-library-eval.md), extended 2026-07-29 with §6.*
**Effort M · Risk low.**

**The problem it solves:** answering *"is this candidate AOB too generic?"* currently needs the full
~156 GB Ghidra corpus + ~53 GB `UE_Analyze_Data`. That makes pattern authoring single-machine,
non-contributable, and dependent on 200 GB surviving.

**§6 is now measured and the licensing question dissolves** — it ships a byte n-gram *frequency
table*, never code. Validated on UE 5.4.4 Shipping against all 151 patterns: **0 upper-bound
violations**, and monotone (bound <10 → max 4 measured hits; bound ≥1000 → max 852). Size ~9.3 MB
per binary at threshold 16, less as a max-count union.

Build in this order — §6 before §4, because §6 needs no legal call, is already validated, and
answers the question that actually blocks authoring:

1. `tools/pe/build_ngram_index.py` — union index over the self-built inventory
   ([reference-builds.md](reference-builds.md)). Offline, corpus machine.
2. Commit the index.
3. `tools/pe/aob_specificity.py` — AOB in; bound + limiting window + the run-<4 verdict out. Stdlib
   only, so it **could** run on the bare second machine and in CI — ⚠️ **but it does not, and nothing
   in the repo calls it** (audited 2026-08-01; zero references from `dll/`, `ui/`, `scripts/`,
   `build.ps1`, both workflows). Advisory tool, human-invoked only.
4. ~~Does a one-version index generalise?~~ **ANSWERED — see §6 of the eval.** Cross-*version* is
   fine (4.27-only index vs the 5.4 binary: 0 violations / 113). Cross-*codebase* is not: on the 58
   binaries the index never saw, `CLEAR` violates at **0.20%** with a real tail (`GNAM_UD2` bounds
   ≤15, takes 932 on FF7R). Cause is code coverage — stock templates contain no game code, and
   licensing means third-party code can never be indexed, so the limit is **structural**. Wording
   in the tool now says "quiet in stock engine code", never "certified".
5. ~~§4's shape blocks + `blocktest.py`~~ **DONE.** 340 blocks / 195 KB from 22 self-built
   oracles; `blocktest.py` is stdlib-only, runs in seconds, and is **wired into CI** — the first
   automated check `Himmel.h`'s patterns have ever had. Asserts *resolution*, not just matching:
   perturbing one pattern's displacement `adj` by 8 still matches but fails 15 blocks.

**4 of 5 steps are complete — step 5 was substituted, not done.** The eval's own build order
([aob-block-library-eval.md](aob-block-library-eval.md) "Build order") ends with
*"5. Gate authoring on it: a candidate clears the pre-filter before it earns a sweep."* The list
above quietly replaced that with the §4 block work (which is genuinely done and genuinely in CI) and
then declared the set complete. **Gating was never built**, which is exactly why nothing reads the
n-gram index. Two honest options, and picking one is the open decision:

* **Wire it up** — add `aob_specificity.py --tsv` to the CI step beside `blocktest.py` and fail on a
  regression in the CLEAR set. Cheap; makes the artifact load-bearing and its accuracy worth tuning.
* **Or retire the claim** — keep the tool as a human-run triage aid and stop describing it as part of
  a completed pipeline. Also legitimate; it is a good tool that simply is not a gate.

Do NOT tune the index's threshold before choosing: a knob nobody reads cannot be evaluated.
Measured 2026-08-01: **every `CLEAR` verdict comes from the threshold FLOOR (limiting window absent),
never from a stored bucket — CLEAR-absent 47 / CLEAR-present 0.** So `CLEAR` means "we have never
seen this window", and a higher CLEAR count indicates a *worse-covered* index, not a quieter pattern
set. (Reductio: AOBMaker's x86 index, which contains zero x64 code, certifies 109 of our x64
patterns CLEAR.)

What remains is optional and should wait for a real need:
* Re-extract blocks whenever a pattern is added/renamed — `blocktest.py` reports `skipped N` when a
  recorded `found_by` no longer exists in `Himmel.h`, so drift is visible rather than silent.
* Rebuild the n-gram index when new self-built Shipping oracles land (it only ever needs to grow).
* Neither tool is an acceptance gate, and that must not erode: **rule 5 still means the sweep.**

⚠ **Neither half is an acceptance gate.** Neither can say a pattern hits the RIGHT address —
`GNAM_XX_1` scores a clean bound of 57 and is `DECOY-ONLY`. `Himmel.h` rule 5 keeps meaning the
sweep; this is a pre-filter so the expensive run only sees plausible candidates.

⚠ **The trap that already bit once, recorded so it does not repeat:** the first scorer indexed only
n=6 and bucketed *"no literal run reaches 6 bytes"* as **rare** — so `GWLD_V3` (852 measured hits)
scored "<8". That is the absence-of-evidence trap `replay_patterns.py` warns about, in a new place.
Index n=4/5/6 and score at the largest n the longest run supports; **never let "unscoreable" fall
into the "rare" bucket.**

-----

## Palworld: re-point the corpus manifest at the D: archive (the live install has patched)

*Parent: same pass.* **Effort S · Risk low.**

Palworld updated 2026-07-29 (md5 `fb10d568…` → `a2dadf69…`, +11,776 bytes), so
`py tools/ghidra/preflight.py Palworld --verify-hash` now correctly reports `id=MISMATCH`
against `H:\SteamLibrary\...`. Nothing is broken — the `.rep` is the artifact of record and
`D:\UE_Analyze_Data\Game Binary backup\Palworld` holds the exact corpus build (verified: its
`SparseDelegates` consensus is `148fb66b0`, the address hardcoded in `Himmel.h`'s `SPARSE_PAL51_1`
note). The backup was taken the day before the patch.

⚠ **DO NOT just re-run `build_corpus_manifest.py` — an earlier version of this item said to, and
that was wrong.** The generator NULLS `steam_buildid`/`size`/`sha256` on a drifted row (correctly:
it must never assert the wrong build), so regenerating would ERASE Palworld's `24181527`, which is
the only pointer to the SteamDB build the `.rep` was made from. **That value is now preserved in
[`tools/ghidra/corpus-provenance.tsv`](../tools/ghidra/corpus-provenance.tsv)** — a hand-made
snapshot that the generator must not overwrite. Regenerate the manifest only after confirming the
provenance snapshot is committed. `corpus-manifest.tsv/json` themselves stay generated — do not
hand-edit those.

The patch itself is now a **settled fact, recorded in GROUND-TRUTH.md**: every global moved
(+0x3300, Sparse +0x3180) and **not one pattern broke** — all six voter sets came back
character-identical. Do not re-measure this per patch.

`GOBJ_DI427_1/2/3` are the only patterns for which `UE4.27-DropIn` is the sole oracle, and what
they encode is the **32-byte `FUObjectItem`** (`shl r,5`). Those 8 bytes are `TStatId`, gated at
4.27 by `#if STATS || ENABLE_STATNAMEDEVENTS_UOBJECT` (`UObjectArray.h` @ `4.27.2-release`).
`STATS` is 0 in Shipping, so a Shipping sample adds nothing — Breeders and Maelstrom already
cover the stock 24-byte item.

Steps: import (one project, `-noanalysis` is fine for the gate) → derive truth from the PDB in
Python (`?GUObjectArray@@3VFUObjectArray@@A`, `?GWorld@@3VUWorldProxy@@A`,
`?GEngine@@3PEAVUEngine@@EA`, the sparse mangled name; GNames via
`FNameDebugVisualizer::GetBlocks` minus 0x10 — verify the 0x10 at 4.27) → add the `sweep.sh` row
and the `GROUND-TRUTH.md` block → full sweep → confirm `GOBJ_DI427_*` now land on it too.

Payoff: converts a sole-oracle dependency on an external store — where a patch can silently
replace the build, as happened to `ES2-0517` — into a locally rebuildable asset, and gives a
second three-config control group after 4.23.

-----

## In-game text: S2T conversion + local-LLM translation — EVALUATED (2026-07-24), mostly NOT BUILT

Full 41-agent evaluation in [text-translation-eval.md](text-translation-eval.md) (all 12 load-bearing
claims refuted or qualified). **In-memory text rewrite = rejected** (three UE-source-level walls: a
ProcessEvent hook can't see `SetText` §3-3; an in-place same-length overwrite doesn't repaint §3-4; and
`FString::Data` can't be repointed without corrupting GMalloc §3-1 — on top of the first-order **font
glyph-coverage** risk). The **offline `.locres` route wins outright** for the S2T half; LLM translation
belongs in an **offline pre-pass** (extract → translate on any GPU incl. a remote box → re-import), never
a live path. Open follow-ons, in priority order:

- **Phase 3 (SHIPPED, build 2368)** — `ReadFTextString` now decodes UTF-8 *and* UTF-16 display strings
  (`Utf8Helpers::DecodeFStringBuffer`) + UE5.4+ pointer-indirection probe; `ReadFString`/`ReadFUtf8String`
  torn-read fixed. ⚠️ **In-game-unverified**: whether STVoyager's UE5.6 ITextData header lands on a probed
  offset — if a re-test returns empty, CE-pointer-scan `2E1097B7000` for the offset chain (risk #3). **M · low**
- **Phase 1 — Locale switcher** (`Lektüre` module: `SetCurrentCulture` invoke + `locale_get`/`locale_set`
  + one UI card). Zero-write, solves "game has zh-Hant but the menu won't let me pick it". Smallest useful
  slice; validate on stock UE5.6 (Satisfactory / STVoyager). **S · low**
- **Phase 2 — Font coverage probe** (`UFont::FontCacheType` + composite-font cmap parse → Offline/covered/
  missing-N). Useful diagnostic even if translation is never built. **M · low**
- **Phase 0 (SHIPPED here)** — the one-page offline S2T/LLM workflow lives in
  [text-translation-eval.md](text-translation-eval.md) §附錄. **done**

-----

## 🔎 Audit #4 fixes (build 2554 — 2026-08-04; full detail in [audit-2026-08-04-findings.md](audit-2026-08-04-findings.md))

Fourth audit, and the first to cover **refactor** alongside bugs/leaks. Scope = the 96 shipped source
files / +15,372 lines changed since the audit #3 baseline (`af2ce50`). Run as two passes: **4a** swept 8
bug areas + 2 refactor areas; a **completeness critic** then mapped what 4a never read, and **4b** closed
those 6 gaps. 48 agents, test baseline 3110 green. **51 items kept** (2 HIGH · 14 MED · 32 LOW · 3 INFO),
**7 refuted** (listed in the findings doc — do not re-raise).

**Do not track individual status here** — that is the mistake audit #3's block made (one sentence said
"awaits in-game verification" for 13 fixes at once, so nothing could be ticked off). The findings doc is
the tracker; delete a row there when it ships and write it up in [dev-log.md](dev-log.md). This block only
records what the audit *is* and the two things that must not be got wrong:

- **✅ DONE (build 2560) — B6 + B27, one commit.** B27 = the Coordinate Library store was constructed and
  never passed (11 positional args into a 12-param ctor), so the whole feature never persisted; B6 =
  Clear-all had no pre-clear backup, harmless *only because* nothing persisted. Wiring now goes through
  `AppComposition.BuildMainWindowViewModel` with **required** parameters (verified: dropping the argument
  is now `CS7036`, not a silent no-op), and a `.preclear.bak` + confirmation guards the wipe. See
  [dev-log.md](dev-log.md) build 2560.
- **✅ DONE (build 2561) — B1 + B30 + B40, one commit.** The live test ran first and inverted the plan:
  `executeCodeEx` returned `nil` without raising, so (a) was real and (b) was *latent* — making "fix the
  arity alone" a certain session brick rather than a suspected one. Both halves shipped together, plus the
  serving-vs-parked split that lets a re-tick revive the DLL instead of tearing down someone else's proxy.
  A deliberate invariant ("no `executeCodeEx` in `[ENABLE]`") was **narrowed with its reasoning stated**,
  not dropped. See [dev-log.md](dev-log.md) build 2561.

**Two root causes, both worth fixing as patterns rather than site-by-site:** 4a's is *the report and the
reality are computed by different code paths* (a success message written by code that never observed
whether the operation ran); 4b's is *a cheap proxy signal substituted for a predicate this codebase
already computes* — filename instead of an export probe, a 1-second sleep instead of an actual signal,
directory mtime instead of the newest file inside — **and in 10 of 22 cases a sibling in this same repo
implements the real check correctly.** A secondary 4b thread, *silent defaults at composition points*
(B27, B31, B38, B45), is why B27's composition-root test is worth more than its one-line production fix.

**`ce-artifacts` is the area to give standing attention.** Three of 4b's five MEDIUMs live there, each can
leave a working setup broken with a confidently wrong message, and — unlike the AOB tooling, which now has
five CI gates — the `.CT`, the emitted Lua and the CE-plugin entry path have **no automated coverage at
all**. `axaml-strings`, the AOT/dependency surface and the generated-proxy family came back effectively
clean, which is a real result worth recording.

-----

## 🔎 Audit #3 fixes (build 2168 — 2026-07-14; full detail in [audit-2026-07-14-findings.md](audit-2026-07-14-findings.md))

Third bug/leak audit of the post-b1872 code (Solide/Hemmung/Linie/Schlacht/Grausam + Auto-Snapshot/
Dump-Explorer/Live-Funcs/Teleport-Stealth). 32 findings raised, verified against current code, then
put through an **adversarial double-confirm** (skeptics mandated to refute; HIGH got 3 diverse lenses).
**Net after double-confirm: 23 scheduled** (1 HIGH + 9 MED + 13 LOW) — **M6 and L6 refuted/dropped**,
7 LOWs downgraded to optional cleanup. 0 regressions from audit #2. **Common root cause of 8 of the 10
HIGH/MED: disconnect/shutdown lifecycle** — a *bare* `OperationCanceledException` from `PipeClient`
(DisconnectAsync/Dispose `TrySetCanceled()` with **no token**, so only ambient `ct.IsCancellationRequested`
distinguishes it from a real cancel), or a DLL worker whose state isn't reset/restored when the last client
leaves. **Prefer fixing the shared root** (an `IsUserCancel(ct)` helper + a single `OnLastClientGone()`
reset registry) over each site individually. Each ID below maps to a section in the findings doc; delete
the row here when it ships.

- **✅ DONE — H1 — Snapshot silently truncated but saved as usable/Success on user Disconnect** —
  SHIPPED commit `452d3ff` (build 2182). Producer catch now filters on `lct.IsCancellationRequested`, so a
  bare disconnect-OCE faults the producer → `Task.WhenAll` rethrows → `CompleteSnapshotAsync` is skipped →
  the existing outer OCE catch deletes the partial. **Verified `is_usable` defaults to 1** (so an
  un-finalised row is *usable* — the fix relies on deletion, not on it being auto-cleaned); the outer catch
  was deliberately **not** filtered (that would reroute to the non-deleting generic handler and re-leave a
  usable partial — M7's separate concern). Regression test
  `Capture_DisconnectMidStream_DoesNotSaveUsablePartial`; 2526 green.
  > **✅ LIVE-VERIFIED 2026-07-23 (Elliot, the real kill).** Snapshot #1 created 22:43:48, 12 chunks
  > in, chunk `offset 90112 / total 356407` (25%) sent and never answered because the GAME WAS CLOSED
  > → `Pipe: ReadLine returned null (disconnected)` → **`SnapshotStore: deleted snapshot #1 (reclaimed
  > disk)`** at 22:44:03. No usable partial survived, the UI did not wedge, and it shut down cleanly
  > afterwards. This is exactly the H1 scenario, executed for real rather than simulated.
  *Delete this row after the audit batch is merged to main.* *Parent: audit-2026-07-14-findings §H1.*

- **[✅ ALL MEDIUMs DONE — 1 HIGH + 10 MED shipped on `dev`]** — the entire
  audit-#3 HIGH+MEDIUM set is fixed. Remaining audit work = the **13 LOW** batch (below) + optional/cosmetic
  items. **In-game verification status is NOT tracked here** — it lives per-item under
  [§ Pending live-game verification](#pending-live-game-verification-verify-only--no-code); this block
  is about what shipped, that one is about what has been proven. Done-notes for the DLL cluster:
  > **✅ DONE — M5 + M2 enable-recovery leak** (SHIPPED commit `61e1f7f`, build 2189, **needs in-game verify**).
  > `Tot::RequestShutdown()` at the TOP of `UE5_Shutdown` + every module's `StartWorker*` gated on
  > `Tot::ShutdownRequested()` (single spawn chokepoint) → no worker revives in the shutdown window; cleared
  > by `Fern::Start`. **Adversarially verified** (5 lenses: no deadlock / lock-order / M3↔M5 / M4↔M5
  > regression; `EnqueueInvoke` gates on Stark's hook flag not `g_shutdown` so the M3 un-hide survives). The
  > same pass caught + fixed a leak in the M1/M2 enable-recovery (un-responsive re-enable orphaned the
  > leftover). **⚠️ EXERCISED at last (Solarpunk, 2026-07-25) — and it revealed a DIFFERENT crash, now
  > FIXED (build 2389).** Leaving See-through ON and closing the game fail-fasted the DLL (`0xc0000409`).
  > A WER minidump showed the fault was on OUR worker thread (pure `version.dll` stack): the per-tick
  > invoke sizes `std::vector(fi.parmsSize)` with no upper bound, so a garbage UFunction ParmsSize read
  > during the game's shutdown throws `bad_alloc`, which escapes the unguarded `WorkerLoop` →
  > `std::terminate`. Fixed by capping ParmsSize in `FindFuncByName` + a `try/catch` around the worker
  > tick, in **Schlacht AND Dunste** (the Fly twin). **Note also confirmed:** `UE5_Shutdown` is NOT
  > called on a game-close (`DllMain(DETACH)` is a no-op, no proxy-graceful-exit hook), so `PipeServer:
  > Stopped` never logs and the shutdown-window worker-revive gate itself is still unproven — but the
  > actual risk it was meant to cover (a worker misbehaving at close) is now handled by the crash fix.
  > **✅ crash fix LIVE-VERIFIED (build 2389, DEBUG build, 2026-07-25):** re-ran the repro (See-through +
  > a Time re-assert worker both live → close game) → no crash / no dump / no event-log error; the 2384
  > run produced all three. *Delete the shutdown-gate half after a real game-close is shown to leave no
  > worker running; the crash half is done + verified.*
  > **✅ DONE + LIVE-VERIFIED — M1 / M2 / M3** (SHIPPED commit `0f6f6e0`, build 2188). All Schlacht:
  > disable joins the worker *before* snapshot/restore (M1); an unresponsive game thread keeps the hidden
  > record + recovers it on the next enable instead of discarding it (M2); `SetEnabled(false)` is called from
  > Fern last-client cleanup + `UE5_Shutdown` with a cheap no-op early-out (M3).
  > **M1 verified (Elliot 2026-07-23):** `SeeThrough: worker stopped` is logged BEFORE
  > `SeeThrough: disabled (1 restored)` — join, then restore, and the hidden actor really came back.
  > **M3 verified (Elliot 2026-07-24):** the session sent **one** `seethrough_set` (the enable) and never a
  > disable, yet `SeeThrough: worker stopped` fired at 09:51:30.235 — the same instant as the second
  > `Client disconnected`. That disable came from the last-client cleanup, which is exactly M3.
  > **M2 half-verified (same run):** the unresponsive branch fired for real —
  > `disabled but 1 actor(s) remain hidden (game thread unresponsive)` — so the record is KEPT rather than
  > discarded. The other half is no longer a user-visible risk: build 2364's deferred restore (see the
  > leftover row below) un-hides automatically once the game thread resumes, so it no longer waits on a
  > later enable. *Delete after the batch merges to main.*
  > **✅ DONE — M4** (SHIPPED commit `7edea28`, build 2187, **needs in-game verify**). `Tot::MarkBackgroundWorker()`
  > thread-local marks each re-assert worker (Solide/Hemmung/Laufen/Solitar/Dunste/Schlacht) so `Tot::Requested()`
  > returns `g_shutdown`-only on those threads → workers no longer freeze on the per-command cancel latch while
  > a pipe command still honours it. Did NOT reset `g_perCommand` on disconnect (would regress the
  > orphaned-scan abort). **Exercised but not conclusively verified (Elliot 2026-07-24):** the UI
  > disconnected with a Hemmung dual-lane hold (world 0.5 + pawn 2.0) and a Laufen jump multiplier live, so
  > the per-command cancel was tripped with re-assert workers running — and nothing errored. But the DLL log
  > ends at the disconnect, so "the workers kept re-asserting afterwards" is not directly shown. To close it:
  > disconnect with a hold on, then look at the GAME (does the hold still apply?) or reconnect and read
  > `get_time_state`. *Delete after batch merged + in-game verified.*
  > **~~M6~~ dropped by double-confirm** — "Solide hold unstoppable after UI crash" is working-as-designed:
  > hold persistence across disconnect is deliberate and family-uniform (Solitar/Laufen/Hemmung/Wirbel also
  > persist), and an off-switch exists (reconnect → `reset_all_fields`, or game restart). The real disconnect
  > defect here is **M4**, which stays scheduled.

- **[✅ ALL UI MEDIUMs DONE — M7/M8/M9/M10]** — the four UI-side audit-#3 MEDIUMs are shipped on `dev`
  (H1 too). Remaining MEDIUMs are the **M1–M5 DLL disconnect/shutdown cluster** (above). Done-notes:
  > **✅ DONE — M10** (SHIPPED commit `8108ff2`, build 2186). PropertySearch ResultFilter now uses
  > `ObjectTreeFilter.SplitTerms` + `MatchesAllTerms(terms, Class, Prop, Type, Super, Preview)` (space=AND,
  > field-OR) + `KeywordSearchMemory` (field + `ResultFilterHistory` + probe + `Schedule` + `Dispose`); axaml
  > `TextBox`→`AutoCompleteBox`. Tests `PropertySearchFilterTests`; made `StubDumpService.SearchPropertiesAsync`
  > virtual. *Delete after batch merged to main.*
  > **✅ DONE — M9** (SHIPPED commit `1f46994`, build 2185). Gate-off teardown releases an active Solide
  > stealth hold (`if (StealthState == StealthHoldingState) ResetStealthCommand`), matching the
  > Foreground/Fly/SeeThrough force-off pattern; `"Holding @0"` factored into a shared const. Tests
  > `ExperimentalGateOff_releases_active_stealth_hold` (+ no-op case). *Delete after batch merged to main.*
  > **✅ DONE — M8** (SHIPPED commit `ad9a7e7`, build 2184). `_sessionEpoch` bumped on disconnect; the dwell
  > records only via the pure gate `ShouldConfirmProxy(IsConnected, scheduledEpoch, currentEpoch)`; timer
  > disposed before early returns, on disconnect, and in `Dispose()`. Tests `MainWindowProxyConfirmTests`.
  > *Delete after the audit batch is merged to main.*
  > **✅ DONE — M7** (SHIPPED commit `1b108a9`, build 2183). Disconnect now reports `Failed` (not
  > `Cancelled`), so the auto-loop stops via `case Failed` instead of wedging; `case Cancelled` also stops
  > defensively; the partial delete+reclaim (`RemovePartialAsync`) now also runs on the generic
  > `catch (Exception)`, closing the non-OCE (IOException/InvalidOperationException) H1 sibling hole.
  > Regression test `AutoSnapshot_DisconnectMidCapture_StopsLoopWithoutWedge`; 2527 green. *Delete after the
  > audit batch is merged to main.*

- **Audit #3 DLL batch — what the Elliot 2026-07-23 22:19-22:28 session DID exercise** —
  The user drove the checklist. Commands seen: `seethrough_set` on→off, `set_time_dilation` x4
  (global 0.5 + pawn 2.0, twice), `set_foreground_lock` on, `pe_profile_start/stop/get` + a second
  start, `get_current_target` x2, `get_related_objects` x3, `find_path_from_gworld`. Results:
  > **✅ M1 verified with evidence.** `SeeThrough: worker stopped` is logged BEFORE
  > `SeeThrough: disabled (1 restored)` — the disable joins the worker first, then restores, and the
  > one hidden actor was genuinely un-hidden. Enable→disable window 29.3 s at the 100 ms tick.
  > **✅ Disconnect with holds active.** Both sessions ended with a clean two-lane
  > `Client disconnected`, with a Hemmung dual-lane hold, the Grausam foreground lock, and a running
  > Linie recording all still active — the M4 worker-cancel-latch and the deliberate
  > "holds persist across disconnect" behaviour, with **zero errors** in any category.
  > **⛔ Still NOT exercised: M5** — the game was never closed (no `PipeServer: Stopped` /
  > `UE5_Shutdown` in this session), so the shutdown-window worker-revive gate is still unproven.
  > Also untouched: Solide force-field (L2/L3/L4), Laufen, Solitar, Dunste.
  > **The session surfaced two NEW defects** (both fixed below, both need an in-game re-check).
  *Parent: audit-2026-07-14-findings; log review 2026-07-23.*

- **NEW (Elliot 2026-07-24) — "See-through OFF" could leave an actor invisible with no hint why; UI now
  says so. The DEEPER fix is still open.** Effort: **M** · Risk: med.
  Switching See-through off while the game thread is paused cannot un-hide anything: the DLL keeps the
  record (M2) and warns, and the actors stay invisible until a later enable restores them. **This is not an
  edge case — it is the default path.** `Stark::IsGameThreadResponsive()` is driven by ProcessEvent
  fire times, and UE throttles a backgrounded window, so *clicking in the UI to switch the feature off (or
  to disconnect) is itself what pauses the game thread.* Live: the 2026-07-24 disconnect left exactly one
  actor hidden.
  > **Half-fixed (build 2362):** the leftover count was already on the wire (`hidden_count`), the UI just
  > ignored it and said "See-through OFF." It now reports *"OFF — but N actor(s) are still hidden because
  > the game thread is paused… focus the game, then toggle See-through on and off once"* in both the status
  > line and the card, with a test. The user is no longer left guessing.

  > **✅ FULLY FIXED (build 2364) — (c) deferred restore + (d) cross-link.**
  > **(c)** A disable that cannot un-hide now hands off to a short-lived `PendingRestoreLoop` that polls
  > `Stark::IsGameThreadResponsive()` every 250 ms and restores the moment the thread comes back — i.e.
  > the instant the user clicks into the game, which is exactly when they would have noticed. It is a
  > proper member of the worker family: `Tot::MarkBackgroundWorker()` (M4), spawn gated on
  > `Tot::ShutdownRequested()` (M5), joined by `StopWorker()`, and superseded by either direction of
  > `SetEnabled` so only one path ever owns `hiddenActors`. Bounded at 5 minutes (a thread that outlives
  > everything is what audit #3 was about; past that the game is realistically closed, which makes the
  > leftover moot) with an explicit give-up WARN.
  > **NOTE — the connect-time drain first proposed as (c) would NOT have worked:** reconnecting means
  > clicking in the UI, which backgrounds the game again, so the drain would fail for the same reason
  > the original disable did. Waiting on the game thread is the only trigger that actually fires.
  > **(d)** The card hint and both messages now say recovery is automatic and point at **Keep Foreground**,
  > which prevents the pause entirely.
  > **This also closes M2's recovery half** — the leftover no longer depends on a user remembering to
  > re-enable.
  *Parent: audit-2026-07-14-findings §M2; log review 2026-07-24.*

- **✅ FIXED (build 2354) — See-Through re-scanned the whole GObjects pool ~5x/second** —
  Effort: **S** · Risk: low. **Found by reading the Elliot log, not by a test.**
  `Schlacht::CollectOccluders` called `UE5_FindInstanceOfClass("KismetSystemLibrary")` **per trace**,
  and that is a full `Aura::FindInstancesByClass` scan which only exits early on a NON-CDO hit — a
  function library has none, so every call walked all **326,363** objects, plus a `FindFuncByName`
  class walk. Measured from the log: **145 of those scans in the 29 s** the feature was on (~5/s).
  Fix: resolve the CDO + `LineTraceSingle` signature ONCE per enable, keyed off a generation counter
  bumped in `SetEnabled(true)` (so a re-enable re-resolves); the failure case is cached too, so a game
  that cooked the function out no longer rescans 10x/s. **Re-check in-game:** enable See-Through on a
  big game and confirm the `only CDO` WARN now appears once per enable instead of continuously — and
  that occluders still hide/restore.
  > **✅ VERIFIED TWICE.** Build 2356 (19 s window): 145 full-pool scans → 1. Build 2361 (2026-07-24, a
  > **105 s** window — 5x longer, so the old code would have logged ~500): still exactly **1**, and
  > See-Through still behaves (`enabled (pierce=1)` → worker → `worker stopped` → `disabled
  > (1 restored)`). The single scan is visible as one
  > `FindInstancesByClass class='KismetSystemLibrary': 1 found, scanned=352851`.
  *Parent: Schlacht build 1991; log review 2026-07-23.*

- **✅ FIXED (build 2354) — the "concurrent first-invoke" WARN fired on EVERY invoke** —
  Effort: **S** · Risk: low. `Frieren::EnsureProcessEventReady` tested `arrivals > 1`, where
  `arrivals` is the all-time invoke ordinal — so every invoke after the very first logged
  `ProcessEvent: concurrent first-invoke #N serialized behind the one-time init (audit #3 race window
  observed but guarded)`. The Elliot session logged **437** of them in four minutes (one per
  See-Through tick) on a run whose own INFO line said **"1 caller(s) arrived before init began"**,
  i.e. there was no contention at all. Beyond the log spam, the diagnostic was worthless because it
  could never be false. Fix: publish the arrival high-water mark inside the `call_once` lambda and
  warn only for `arrivals <= thatMark` — genuine in-flight contention only.
  > **✅ VERIFIED TWICE: 436 WARNs → 0** (build 2356, 552 invokes), and **0 again** on the build-2361
  > run. *Parent: audit #3 ProcessEvent double-install guard; log review 2026-07-23.*

- **[✅ ALL 13 LOWs DONE] Audit #3 low-severity batch** — SHIPPED on `dev` in four commits:
  UI L13–L17 (`8bd33f8`, +2 tests), Solide L2/L3/L4 (`408fd2d`), DLL L1/L5/L8/L10/L12 (`7f3898f`), and the
  adversarial-verify followups (`3362636`: L4 prune-guard for >256-instance churn + L10 GFW-hook re-subclass
  race). The DLL LOWs (L1–L12) **await in-game verify**. With the HIGH + all 10 MED, the entire scheduled
  audit-#3 set is now fixed — only the optional/cosmetic downgrades below remain. *Delete after batch merged +
  DLL in-game verified. Parent: audit-2026-07-14-findings §L1–L17.*

- **[optional / cosmetic] Audit #3 downgraded items (do only if touching the file)** —
  Effort: **S** each · Risk: low. Double-confirm judged these real-but-not-worth-a-dedicated-fix (no
  incorrect/harmful outcome): **~~L6~~** DROPPED (Welford `m2` provably ≥ 0 → NaN unreachable + tolerant
  downstream); **L7** Linie post-Reset phantom row (wiped by next `StartRecording`); **L9** Grausam per-frame
  global `ClipCursor(nullptr)` (deliberate release tradeoff; niche third-party case); **L11** Schlacht raw
  `AActor*` across GC (SEH-guarded, self-corrects next tick, no crash); **L18** DetectStats missing detach has
  zero functional effect (no `OnSelectedResultChanged`) — only the no-`ct` usability point stands; **L19**
  LiveFuncs `Clear()` inverted detach order (cosmetic); **L20** Dump All export no `CancellationToken`
  (usability gap, output correct); **L21** (INFO) DumpExplorer reimplements space=AND (semantically
  identical — style only). *Parent: audit-2026-07-14-findings §L6–L21 (see per-item ⬇/⛔ banners).*

-----

## ▶ Next up (genuinely actionable now)

- **Multi-pipe Phase 1 — residual verification: only the WATCH item is left** —
  Effort: **S** · Risk: low. The two-connection lane split shipped + in-game verified for §9.6 items
  1–5 (dev-log 2026-06-28).
  > **✅ The lane-drop edge is now verified (Elliot 2026-07-23).** Closing the game mid-snapshot
  > dropped the bulk lane and the router did exactly what §9.7 specifies:
  > `Pipe lane dropped — tearing down both lanes for a clean reconnect` → `Pipe disconnected`, with the
  > in-flight snapshot faulting into H1's delete path rather than half-finishing. No wedge, no orphan.
  Still open: (6) **watch-event delivery** to the interactive lane (System-tab / address watch still
  pushes correctly while the bulk lane is busy). Verify opportunistically.
  *Parent: multipipe-eval §9 (PR #396).*
  The build-1836 single-handle worker-pool was REVERTED (deadlocked on the synchronous pipe, §8.1).
  The sister repo `D:\Github\discrete` runs a proven alternative: the UI opens **two** client
  connections (interactive + bulk) to a `maxInstances≥2` server that serves **each connection on
  its own thread + own handle**. Each connection stays serial read→write on one handle (one thread)
  → **no same-handle deadlock, and NO overlapped-I/O rewrite needed** (each handle is touched by
  exactly one thread). Safe because the interactive lane never builds the Aura class caches and the
  bulk lane runs scans one-at-a-time (§9.1). DLL work = per-connection refactor of Fern (thread-per-
  connection accept, per-connection write-mutex / in-flight / watch-event routing, monitor + session
  cleanup keyed per-connection — §9.3); UI work = a 2nd `PipeClient` + a `BulkCommands` router like
  discrete's `BackendAdapter` (§9.4). Lane table = §6. **MUST pass the §9.6 in-game checklist before
  shipping.** Open decisions in §9.7 (maxInstances count; session-drop policy; cancellation scope).
  Snapshot SPEED is a SEPARATE issue (§9.5): UI-side single-threaded multi-MB chunk parse (~2.4s/chunk)
  → streaming `Utf8JsonReader`/smaller chunks. *Parent: reverted Phase 1 build 1836 (dev-log 2026-06-28).*

- **Magic-number centralization — Tier 2 remainder + Tier 3 (deferred; low priority)** —
  Effort: **M** · Risk: med. Tier 1 (dup/tunable literals) + the Tier 2 `IsUserspacePointer` paired-
  bounds helper SHIPPED (dev-log 2026-07-03). LEFT because each carries genuine per-site multi-meaning
  nuance: object-count/size ceilings — `0x800000` (8M UObject count), `0x100000` (1M, but needs
  SPLITTING into container-element-COUNT vs PropertiesSize-BYTES — one const would conflate them),
  `[0x1000..0x400000]` NumElements window; and `MAX_CLASS_HIERARCHY_DEPTH=64` (`64` is high-collision —
  buffers / bit-counts — needs per-site verification). Tier 3 = single-use knobs (Heiter startup
  delays, Fern watch/monitor poll, Mimic caps, Solitar caps, Wirbel eps/channel, movement/debug-cam
  protocol-id enums). Same careful methodology: pair/meaning-gated, never blind-sweep a literal.
  *Parent: magic-number centralization (dev-log 2026-07-03).*

- **CE responsiveness under heavy scans — pick a NON-priority approach if it ever bites** —
  Effort: **S-M** · Risk: med. Phase 0 (drop scan threads to `BELOW_NORMAL`) was **REVERTED** —
  with the game saturating cores it starved scans ~20× (Snapshot 1–2 min → ~1 h). The
  pre-existing `cores − 2` count cap (`ScanThreadCount`, Aura.cpp:105) is the right throttle and
  is restored. CE `.CT` invoke is already off-pipe (Mimic mailbox); if a real starvation case
  appears, prefer: yield points inside the scan loop, a smaller worker cap while a CE invoke is
  pending, or `Stark`-queue priority for mailbox invokes — **not** a blanket thread-priority drop.
  *Parent: reverted Phase 0 build 1834 (dev-log 2026-06-28).*

- **Class Pivot discovery (build 1742) — deeper capture-memory fix** —
  Effort: **M** (memory) · Risk: low. The bounded N-snapshot discovery + shape ranking +
  Locate-in-GWorld/GameEngine + resizable/filterable results shipped build 1742; the class
  picker freeze was fixed + in-game verified build 1764 (filter TextBox + ListBox — see
  dev-log). The **change-driven discovery ("Suggest Targets") path itself is still NOT in-game
  verified** end-to-end. Separately: the post-capture compacting `GC.Collect` (build 1742) is a
  **mitigation** for the multi-snapshot working-set bloat — the *deeper* fix is to stop the
  transient allocation at the source by replacing the capture's JSON-DOM parse with a streaming
  `Utf8JsonReader` (`SnapshotChunkAsync` / the chunk parse). Only pursue the streaming rewrite if
  the GC reclaim proves insufficient on huge (Avowed-scale, 266K-object) games. *Parent: dev-log build 1742.*

- **Class Pivot — rounding-mode + "can't-find-data"/GAS-capture follow-ups (deferred from build 1672)** —
  Effort: **S-M** · Risk: low. The per-panel **RoundingMode {Round/Trunc/Ceil}** (build 1672) was rolled out to Value Search, Snapshot, and SPC but **NOT Class Pivot**. Two distinct gaps:
  (1) **Rounding mode** — Pivot does **no numeric value MATCHING** today: it groups by the *rendered* key string (`PivotEngine` uses `SnapshotNumeric.Render`) and `PivotDiscoveryEngine.Direction()` compares raw `double`s with no reduce. So a rounding-mode switch is largely **N/A** — but if Pivot ever grows a value-target filter, it should reuse `SnapshotNumeric.ExactMatch/OrderedMatch/BetweenMatch(...,FloatRoundMode)` like the other panels. Lower priority: optionally apply the reduce to the grouping KEY so float GAS values bucket by displayed integer (e.g. 513.36/513.4 group as "513").
  (2) **"Can't find data" / GAS-capture** — the recent snapshot fixes (nested-`StructProperty` GAS capture `Aura::CaptureDirectStructFields`, build 1648; rounded-float matching) flowed into Snapshot/SPC/Group. Pivot reads the **same captured corpus**, so the GAS `Health.BaseValue`-style fields *should* now appear in Pivot automatically — **but this is UNVERIFIED**. Verify in-game that a GAS attribute captured post-1648 actually shows up as a pivotable field/key in Class Pivot; if Pivot has its own field-selection or numeric-only filter that drops nested-struct leaves, fix it. *Parent: rounding-mode switch build 1672; snapshot GAS-capture build 1648 (project-snapshot-nested-struct-gas).*

- **Flatten GAS attributes — optional extensions (deferred by user, build 1698)** —
  Effort: **S** · Risk: low. The "Flatten GAS attributes" Options toggle (build 1698) collapses a
  `GameplayAttributeData` StructProperty one level in **Copy CE XML / Copy CE Field** only. Two
  follow-ups the user explicitly scoped out of that change:
  (1) **Export CSX** — apply the same flatten to the CE Structure Dissect (`.csx`) export
  (`CsxExportService.EmitElement`). The `IsGasAttributeStruct` detection + combined-offset math port
  directly, but CSX is a separate emitter so it was intentionally left out.
  (2) **Other single-field / wrapper structs** — keep flatten GAS-only "for now"; a general
  "flatten any single-/two-scalar-field struct one level" option would need a careful detection rule
  to avoid surprising collapses ("various cases"). *Parent: Flatten GAS attributes build 1698
  (project-gas-attr-flatten-ce-export).*

- **dxgi proxy early-load fragility — harden (thin-shim + renamed real-dxgi copy), or leave dxgi as "late-load games only"** —
  Effort: **M-L** · Risk: med (loader-time code + deploy flow). **Deferred by owner (2026-06-19); Octopath uses version.dll for now, and the UI default is back to version.dll.** The dxgi proxy instant-exits on games that call dxgi **extremely early — under the loader lock, before our CRT is initialised** (Octopath Traveler: debugger-confirmed across 3 distinct crash dumps — execute-0 / `__tzset` uninit CRT lock / `RtlAllocateHeap` null heap; see dev-log 2026-06-19). Two genuine early-load fixes shipped + kept (`Sein::GetTimestamp`→Win32 `GetLocalTime`; dxgi lazy self-resolving thunks), but they do NOT make Octopath's dxgi work — the **root blocker** is that `LoadLibraryW(real same-named System32\dxgi.dll)` returns NULL under the early loader lock. **version.dll dodges it all by being called at normal runtime, not under early loader lock.** Robust fix = **thin-shim split (like RE-UE4SS):** `dxgi.dll` becomes a tiny CRT-free forwarder that (a) loads the real dxgi via a **renamed copy** (`dxgi_orig.dll`) to dodge the same-base-name-under-lock failure, and (b) `LoadLibrary("UE5Dumper.dll")` to run the heavy dumper as a **separate, normally-named, late-loaded** DLL. Deploy becomes **2 files** (`dxgi.dll` + `UE5Dumper.dll`) → the Proxy Deploy panel's deploy/undeploy/redundancy/Update-All must copy/remove both. NOTE: `/MD` (dynamic VCRuntime/UCRT) alone is only a **partial** fix — it removes the CRT-init crashes (Octopath already loads the shared UCRT early) but NOT the loader-lock same-name `LoadLibrary` blocker (that resurfaces as execute-0). version.dll/dinput8.dll don't need any of this (they load late). *Parent: dxgi proxy build 1172; early-load diagnosis + 2 fixes build 1351 (dev-log 2026-06-19).*

- **UE5.7+ packed FUObjectItem — live-verify + calibrate when a packed game appears** —
  Effort: **S** (mostly verify) · Risk: low (gated, last-resort only). Packed parsing shipped
  build 1108 but is **UNVERIFIED** (no `UE_ENABLE_FUOBJECT_ITEM_PACKING` game exists yet). When
  one does: attach, watch for the `*** UNVERIFIED ... PACKED ... ACTIVATED ***` WARN (or force via
  `set_packed_consts {force:true}`), then tune `align_bits`/`ptr_mask_bits`/`serial_off` against the
  echoed `GObjects[0..7]` samples until names resolve; confirm the object walk + a CE XML/CSX export
  are correct; then promote out of UNVERIFIED (drop the badge gate, pin the constants).
  Open sub-question: the packed **SerialNumber** offset (currently best-effort `0x0C`) is unpinned.
  *Parent: PackedItem.h + Aura packed mode + set_packed_consts shipped build 1108 (dev-log 2026-06-14).*

- **✅ DONE — DLL cancellation live-verified (Elliot 2026-07-23).** (a) **Closing the game while a
  snapshot streamed** ended with a clean DLL-side `PipeServer: Stopped` 5 s after the last answered
  chunk, and the UI tore both lanes down and deleted the partial instead of hanging. (b) closing the
  UI mid-scan + prompt reconnect was already covered by the two back-to-back sessions earlier that
  evening (disconnect at 22:25:11 → reconnect at 22:25:13, 2 s). *Delete after the batch merges to
  main. Parent: cooperative cancel + shutdown-abort + disconnect monitor shipped build 936-937,
  PR #238 (dev-log 2026-06-06).*

- **Guess? "missing" mid-object data — RESOLVED (working as designed; diagnostic kept).** The
  `WALK:guess` diagnostic (build 1364+, `Ubel.cpp` `WalkInstance` fillGaps block, one line per
  Guess? walk, opt-in-gated) confirmed it **live on Elliot `LSGameWork`**: `0x170=16(ArrayProperty)`
  covers `0x170–0x180` and `0x180=80(MapProperty)` covers `0x180–0x1D0` exactly — the region the user
  saw "missing" is the inline allocator bytes of a TArray + TMap, fully owned by reflected container
  properties. The `GAPS:` list has nothing in `0x170–0x1D0` (only small padding/bitfield holes like
  `[0x3,0x8)`, `[0xA04,0xA3C)`). So `Guess?` correctly emits no raw rows there — CE dissect just
  flattens the container internals; our walker shows them as expandable Array/Map rows. **No code
  change.** The diagnostic line is kept as the standing answer to future "why doesn't Guess? show
  region X" questions (gated on `Guess?` being on). Optional future nicety: a `docs/tips.md` note that
  container internals aren't decomposed into guessed rows. *Parent: Guess-What leading-gap fix (builds
  1330-1333) + diagnostic (build 1364, this session); confirmed live 2026-06-19.*

- **Native-C Value Scan — P0–P3 ALL SHIPPED on dev; only in-game verify of P3 remains** —
  Effort: **0** (verify only) · Risk: low. Full design + status in
  [native-c-value-scan-spec.md](native-c-value-scan-spec.md). Opt-in raw/unmanaged
  (non-`UPROPERTY`) scan for native HP/MP via "Guess What" (`Ubel::GuessGapTypes`), across
  Value Search + Group Scan + Snapshot→SPC→Pivot. **DONE + in-game VERIFIED:** P1 single
  (Octopath), P2 group (FF7 Rebirth). **P3 DONE (build+tests+AOT green):**
  `CaptureSnapshotChunk(captureNativeC)` → `AppendRawHoleFields` (GuessGapTypes →
  `NormalizeGuessedTypeToProperty` → drop Pointer/Padding → `<raw@0xNN>` fields,
  numericScope-filtered, ≤256/obj); pipe `native_c` on `snapshot_chunk`; C#
  `SnapshotViewModel.IncludeNativeFields` toggle + intro string. SPC Query + Class Pivot
  consume raw rows with ZERO code changes (key on prop_name=offset + canonical declared_type;
  existing `fields` schema, no migration). **REMAINING: in-game verify P3** — BLOCKED on the
  snapshot-perf item below (FF7 Rebirth capture with Native-C didn't finish — 16+ min, >50%
  uncaptured). Verify on a smaller / faster game, or after the perf work: capture a native
  snapshot pair around a stat change, confirm SPC diff tracks a `<raw@0x..>` value + Class
  Pivot decodes it (not hex).
  *Parent: P0–P3 shipped on dev (this session); builds on value_search_caveats, the `Orden`
  seam (group-value-scan-spec §3.1), and the "Guess What" build (commit 75ea723).*

- **[✅ IMPROVED — parked unless it bites again] Snapshot capture too slow on huge games** —
  User re-tested 2026-07-23 **on a smaller title rather than FF7 Rebirth and confirms the four
  changes below improved it**, so the item is parked. What that does NOT settle is the original
  433K-object case — keep the notes below for when it recurs; the untried levers are class-scoped
  capture (only a chosen class's instances) and a clearer "X% captured" progress.
  **Native-C P3 verification is no longer blocked by this.**
  Effort: **M-L** · Risk: med (touches the hot capture path). FF7 Rebirth snapshot with
  Native-C ON ran **16+ min and left >50% of objects uncaptured**, so P3 couldn't be verified
  there. Likely causes, in order: (1) **Native-C `AppendRawHoleFields` calls `Ubel::GuessGapTypes`,
  which reads memory BYTE-BY-BYTE** (the zero-run probe does one `Macht::ReadSafe<uint8_t>` per
  byte) — over every hole of every object that's very slow. **FIX SHIPPED (this session):** `Ubel::GuessGapTypes` now reads the whole gap
  ONCE into a reused `thread_local` buffer and guesses in-buffer — the per-position AND
  per-byte (zero-run probe) SEH reads are eliminated; output is byte-identical; an SEH
  fallback is kept for a faulting / over-large gap. Also speeds up LiveWalker "Guess What".
  (still 10+ min after this fix, so the round-trip overhead below dominates too.) (2)
  ~~one pipe round-trip per 200 objects~~ **CHUNK RAISED 200 → 1000 (this session, 待測 /
  in-game re-test pending):** `Constants.SnapshotChunkSize` — ~2166 chunks → ~433 for 433K
  objects, also cutting the per-chunk SQLite write-transaction count. Safe (byte-mode pipe +
  `StreamReader.ReadLineAsync` accumulate any size; DLL 15s per-chunk deadline re-chunks slow
  chunks). **NEEDS in-game re-test on FF7 Rebirth — if still too slow, the bottleneck is the
  single-threaded DLL walk → do (3).** Could raise further / batch SQLite inserts if needed.
  (3) ~~DLL `CaptureSnapshotChunk` is single-threaded~~ **PARALLELIZED (this session, 待測 /
  in-game re-test pending):** the per-object capture loop now runs across worker threads via
  `ParallelGObjectsScan` (each worker fills its own `SnapshotObject` vector, merged after; whole
  chunk processed → `scanned` stays contiguous for the pager; Tot-cancel only, no wall-clock
  early return). Chunk size raised to **8192** so each full chunk clears `ScanThreadCount`'s
  >=8192 worker-thread threshold + amortizes the per-chunk cancel watcher. Verified by an
  adversarial 3-lens race/correctness audit (capture-helper statics, GuessGapTypes/WalkClassEx
  copy-out, pager contiguity) — no crash/corruption/mis-paging; the worker stride-check was
  fixed to range-relative for prompt cancel. **NEEDS in-game re-test on FF7 Rebirth** (combined
  with the GuessGapTypes + chunk wins, this is the big one). (4) **Source-level noise skip
  SHIPPED (builds 1484-1486, dev, in-game verify pending):** the "Auto detect Engine/System
  noise" option (default ON) `continue`s past pure engine/system classes (UI widgets, textures,
  sounds, Niagara, anim instances, `/Script` engine packages) in the capture loop BEFORE the
  per-field walk — cutting the dominant per-object cost for the many noise objects a big game
  carries + shrinking the DB, with a gameplay guardrail that force-keeps Actor/Pawn/Character/
  component-derived classes. Complements (1)-(3); especially helps games heavy in engine UI/FX
  objects. Still open if needed: class-scoped capture (only a chosen class's instances) + a
  clearer "X% captured" progress.
  *Parent: Native-C P3 in-game test (FF7 Rebirth), this session.*

- **DynOff calibrated offsets are non-atomic — tighten the second writer (low-risk hardening)** —
  Effort: **S** · Risk: low. The race audit of the parallel snapshot flagged a PRE-EXISTING
  technical data race the parallel readers widen: `DynOff::FSTRUCTPROP_STRUCT` (and the sibling
  calibrated `DynOff::` ints) are non-atomic. `Ubel::CorrectSubclassOffsets` serializes its writes
  (`s_calibrationMutex` + acquire/release `s_checked`), but there's a SECOND unguarded writer at
  `Ubel.cpp:~4288` (`DynOff::FSTRUCTPROP_STRUCT = tryOffset;` inside `WalkInstance`), and the
  snapshot/WalkClassEx-enrichment readers aren't gated by `s_checked`. Benign in practice
  (idempotent convergent writes + aligned-int load/store atomicity on x64), so NOT a crash risk,
  but technically UB. Lowest-risk fix: drop the redundant `~4288` write (verify `CorrectSubclassOffsets`
  already covers that calibration first — don't regress StructProperty struct-name resolution), or
  make the calibrated offsets `std::atomic<int>` with relaxed loads. *Parent: parallel-snapshot race audit, this session.*

- **NEW (Elliot 2026-07-23) — a transient `MH_CreateHook` failure permanently poisons the session
  into the "unsafe direct call" path** — Effort: **S-M** · Risk: **med** (touches the hook path, the
  most crash-prone code in the DLL). **Observed once, on the run right after a session where the same
  hook installed fine at the same address:**
  > `[ERROR] GameThreadDispatch: MH_CreateHook failed: MH_ERROR_MEMORY_ALLOC`
  > `[WARN]  GameThreadDispatch: hook install failed, invoke will use direct call (unsafe)`
  > `ProcessEvent: first-time init complete — offset=608, hook_active=0`

  `MH_ERROR_MEMORY_ALLOC` means MinHook could not place a trampoline within reach of the target, which
  depends on the process's VM layout at that instant — i.e. it is **intermittent by nature**
  (22:24 install at `0x1415968E0` succeeded; 22:43 the same address failed). Two problems follow:

  1. **It is latched forever.** `TryInstallGameThreadHook` sets `static bool s_hookAttempted = true`
     **before** attempting and never clears it on failure, and `EnsureProcessEventReady` wraps the
     whole thing in `std::call_once`. So one unlucky allocation means *every* invoke for the rest of
     the process life takes the fallback — even when the user re-enables the feature minutes later,
     when the VM layout may well have room again.
  2. **The fallback is the historically crash-prone path**, and a WORKER-driven feature hammers it:
     See-Through logged **552** × `UE5_CallProcessEvent: hook not active, using direct call (unsafe)`
     in 19 seconds (~10 ProcessEvent calls/second from a non-game thread). It happened to survive
     here, but a one-shot user invoke and a 10 Hz re-assert worker are very different exposures.

  **✅ FIXED (build 2358) — (a)+(b)+(c) all built, with the UI reflecting failure AND recovery:**
  - **(a) Retry instead of latch.** One install path (`TryInstallGameThreadHook`), no permanent
    `s_hookAttempted`: it returns early when the hook is already up, otherwise retries up to 8 times
    with a 5 s cooldown. Cheap enough to sit on the lazy invoke path (a 10 Hz worker adds at most one
    attempt per cooldown) and bounded so a genuinely unhookable game stops trying. A user-initiated
    enable calls it with `force`, skipping cooldown and cap. Recovery logs
    `hook RECOVERED on attempt N`.
  - **(b) One line per transition.** `ReportHookState` logs the fallback once when the hook goes
    down and once when it comes back, carrying the count of invokes that took the fallback meanwhile.
    The per-invoke `direct call inst=…` / `direct call success` INFO pair is gone (the exception path
    still logs unconditionally — that IS per-call news).
  - **(c) Worker invokes refuse the unsafe path.** `Tot::IsBackgroundWorker()` (the thread-local the
    M4 fix already set on every re-assert worker) gates it: with the hook down, a worker invoke
    returns **-8** instead of calling ProcessEvent off the game thread. `Schlacht::SetEnabled(true)`
    forces one hook attempt and, if it still isn't up, declines with **`STR_ERR_NO_HOOK` (-5)**
    without starting a worker that could only tick uselessly.
  - **UI.** `seethrough_set`/`get_state` now carry `hook_active`; the See-through card shows
    "Unavailable" + *"Game-thread hook unavailable … press Apply again to retry"* on a refusal, and
    a later Refresh CLEARS it once the hook recovers. Gated on the refusal CODE, not on `hook_active`
    alone — the hook installs lazily, so "not installed yet" is the normal state of a fresh session
    and must not read as a failure. Three tests pin exactly that (refusal / recovery / lazy).

  **Re-check in-game:** the failure is intermittent, so it may not reproduce. What to look for if it
  does: the log should show at most 8 install attempts (not one), a single fallback WARN instead of
  hundreds, and See-through should refuse with a visible message rather than silently doing nothing.
  > **Not reproduced on the build-2361 run (2026-07-24)** — the hook installed first try at the same
  > address (`0x1415968E0`) that failed on 07-23, which is consistent with "VM-layout accident, not a
  > property of the game". Zero fallback invokes, zero retries needed. Worth banking from the same run:
  > the post-install validator reported **`hook fired 10238 times in 1500ms`**, i.e. the pattern-based
  > vtable detection (`vtable+0x260`) landed on the RIGHT slot on Elliot — the failure mode recorded in
  > `feedback-pe-vtable-wrong` for ES2 / Geri.
  *Parent: Stark::InstallHook / Frieren::TryInstallGameThreadHook; log review 2026-07-23.*

-----

## Bookmarks + Options persistence + CE-export filter — follow-ups (shipped PR #359, builds 1652-1663)

The three persistence features (CE-export system-component filter, global panel-options persistence, per-game bookmark persistence) shipped + in-game verified (dev-log 2026-06-24). Deferred refinements, none blocking:

- **#3 — snapshot capture options: GLOBAL → per-game (peHash)** — Effort: **M** · Risk: med.
  The snapshot capture block (`GameOnly` / `AutoSkipNoise` / `IncludeNativeFields` / `SelectedScope` / `SelectedFamily` / `SelectedMaxDataset`) is persisted as a single GLOBAL default in `ui-options.json` to avoid a connect-time load race. Making it per-game (a `snapshots.{peHash}.options.json` sibling of the denylist, or a section in the bookmark/per-game store) lets `SelectedMaxDataset` track each game's size (the Avowed-6.7 GB driver). **Load it inside `SnapshotViewModel.SetEngineState` AFTER `_store.SetActiveGame(peHash)` and BEFORE `RefreshAsync`, under its own suppression flag** — NOT in `ApplyEngineState` (the original adversarial-review C2 finding: wrong-game bleed + save-storm if loaded at the wrong point). *Parent: #3 Options persistence, PR #359 (dev-log 2026-06-24).*

- **#3 — opt-in "resume where I left off" (view-state persistence)** — Effort: **S** · Risk: low.
  `SelectedTabIndex` + panel-collapse toggles (`CaptureSectionOpen` / `CompareSectionOpen` / `NoisePanelOpen` / `IsFunctionsExpanded` / Object Tree `IsCollapsed`) were deliberately EXCLUDED as transient view state. Some users want them restored. Add as an OPT-IN (a "remember tab + panels" preference) so the default stays clean. Lives in the existing `UiOptionsStore` (a `View` sub-object). *Parent: #3 Options persistence, PR #359.*

- **#3 — "Reset options to defaults" button** — Effort: **S** · Risk: low.
  Delete `ui-options.json` + re-apply model defaults to every VM (reuse `ApplyOptions(new UiOptionsSettings())`). One en.axaml string + a menu item (System tab or a small ⚙ on the toolbar). *Parent: #3 Options persistence, PR #359.*

- **#2 — CE-export filter: also skip system-component CONTAINER elements** — Effort: **S** · Risk: low.
  The "Skip system components" filter currently only covers pointer/struct fields (`PtrClassName`); array/map/set ELEMENTS whose element class is an engine asset (`KeyPtrClassName` / `ValuePtrClassName`, `LiveFieldValue.cs:60/66`) slip through because the container emitters don't route through the `EmitFields` depth gate. Add the per-element check in `EmitMapProperty` / `EmitSetProperty` / `EmitArrayProperty` (depth>1 only). The tooltip already states elements are not filtered, so this is additive, not a bug. *Parent: #2 CE-export noise filter, PR #359.*

- **~~#1 — bookmarks across a game RESTART (deeper-than-GWorld-root paths)~~ — DONE (build 1690, MERGED main PR #365 `2e54d86`, in-game VERIFIED).**
  Bookmarks now SPINE-re-walk on every load: `LiveWalkerViewModel.TryReresolveBookmarkSpineAsync` re-resolves the saved breadcrumb chain (stable field name+offset+deref/container kind) from a live anchor — GWorld (`WalkWorldAsync`) or GameEngine (`ResolveGameEngineAsync`) — rebuilding each crumb with a fresh address (`BreadcrumbItem` is init-only → `CloneCrumbWithAddress`). Container element hops (`[N]`) re-resolve via the same element-address math as the `Populate*ContainerFields` helpers. Any unmatched hop / null ptr / DataTable view / non-anchor root → return null → fall back to saved addresses (same-process fast path) + the existing `SavedClassName` staleness guard. **v1 safety property kept: never silently shows the wrong object** (name+offset match + class guard). `BuildBreadcrumbSpineFromPath` now threads `rootKind` so Locate-in-GameEngine bookmarks persist the right `"GameEngine"` anchor marker (adversarial-review finding). Remaining game-PATCH case (offsets shifted across builds): the **"import bookmarks from a previous build"** affordance (pick an older `bookmarks.{oldHash}.json`, re-resolve against the new build) is still open if users ask. *Parent: #1 bookmark persistence, PR #359.*

- **#1 — orphaned per-game bookmark files accumulate** — Effort: **S** · Risk: low.
  Every game patch = new PE hash = a fresh `bookmarks.{hash}.json`; old ones are never swept (same latent issue the snapshot per-game files have, but those have quota eviction). Add a startup sweep (delete `bookmarks.*.json` older than N days, or cap file count) — or a "clear bookmarks for all games" action. Low disk impact; do only if it bothers someone. *Parent: #1 bookmark persistence, PR #359.*

-----

## Related Objects panel — Phase 2 + follow-ups (Phase 1 shipped builds 1323-1327)

Phase 1 (the "Related" tab: given an actor, list Self/Class/Outer + Controller↔Pawn + owned components/ASC/AttributeSet via a depth-3 owned walk; 🌍 GWorld / Live Walker / finder / copy per row; 🔗 Related handoff from Instance Finder / Value Search / Live Walker) + the Instance Finder **"Newest first"** opt-in shipped builds 1323-1326. **In-game VERIFIED on TQ2:** `bp_ai_default_character_C` → Related lists 58 objects incl. `TQ2AIController`, `GrimAbilitySystemComponent` (ASC), `bp_tq2_character_stats_component_C` (AttributesComponent) → `AttributeSetHealth.CurrentHealth` = live HP (73.57). **Phase 2 (`Edel` current-target auto-detect) SHIPPED build 1400** (dev-log 2026-06-20) — `🎯 Detect target` button resolves GWorld→PC→Pawn, scores the player's outgoing object-ptr fields (structural is-Actor gate + keyword boost), auto-loads the top candidate; the `Edel` roster name is now 🟢. Remaining follow-ups, in order:

- **Phase 2 Edel — HALF VERIFIED (graceful fallback, Elliot 2026-07-23); the positive case is what's left** —
  Effort: **0** (verify only) · Risk: low. **The fallback works exactly as designed.** Two 🎯 Detect
  target runs on The Adventures of Elliot both returned `resolved=False candidates=8`: nothing was
  auto-loaded, and the ranked list was surfaced instead. The ranking is also sane — top candidate
  `BP_SupportFairy_C` (score 45, reason `is-Pawn`, reached via `BP_PlayerCharacter_C.SupportCharacter`),
  then `DefaultPhysicsVolume` (30, `is-Actor`), then a gameplay-cue actor. That top hit is the player's
  COMPANION, not a target — i.e. precisely the plausible-but-wrong pick that auto-loading would have
  gotten wrong, and the confidence bar correctly refused it. **Still open: the positive case** — a
  lock-on / soft-target action title where the target really does live in a `UPROPERTY`, to confirm the
  top candidate IS the focused enemy and its AttributeSet/HP loads. Tune the score constants only if
  such a game motivates it. *Original note:* Built + unit-tested + AOT-green but unproven live. Verify on a **lock-on / soft-target action title** (the target lives in a `UPROPERTY` object field): click 🎯 Detect target → confirm the top candidate IS the focused enemy and its AttributeSet/HP shows in the grid; confirm the 🌍 Locate-in-GWorld now resolves for that target (it should — the player references it). On the named JP/CN test games (TQ2/SEED/DQ7R, mostly no target `UPROPERTY`) confirm the **graceful fallback** fires (note = "no clear target / weak guesses", nothing auto-loaded) rather than feeding a wrong actor. Tune the score constants / keyword tables only if a real game motivates it. *Parent: Edel shipped build 1400, dev-log 2026-06-20.*

- **Locate in GWorld — streaming / World-Partition actors — the `ok_via_level` RECOVERY is still the
  unverified half (Elliot 2026-07-23 exercised the normal path instead)** — Effort: **0** (verify only) ·
  Risk: low. A 🌍 on Elliot **succeeded through the ordinary forward BFS**: `status: "ok"`, `found: true`,
  depth 5, 28 ms, 3,065 nodes visited, root `MainField_A2` — path `GWorld > GameState > PlayerArray[0]
  > PawnPrivate > SupportCharacter > DamageHit`. Worth banking: that request carried `deep: true` +
  `container_depth: 4` and the path hops **through a container ELEMENT** (`PlayerArray` ArrayProperty,
  `element_index: 0`), so the deep / container-element descent is verified live. What it does NOT
  exercise is `RecoverViaWorldLevel`: the target was reachable normally, so `ok_via_level` never fired.
  **To exercise it:** 🌍 on an actor the forward BFS cannot reach — a just-spawned or streamed-in enemy
  (the original Elliot "weapons map, enemies don't" case). Look for the status note "via the world's
  level list" and confirm the breadcrumb spine still drills to its HP. *Original note:* `Aura::RecoverViaWorldLevel` now recovers a `not_reachable` actor through its owning `ULevel` (reached by the `ULevel::OwningWorld` back-reference, since an actor's Outer IS its level), emitting `world →(WorldLevel)→ ULevel → Actors[k] → actor [→ target]` with status `ok_via_level`. This makes ANY actor that belongs to the current world locatable + navigable in Live Walker (and a bounded tail BFS reaches an owned AttributeSet/HP), regardless of how its level was streamed in — closing the Elliot "weapons map, enemies don't" case. **Verify in-game:** on a streaming/WP title, 🌍 on a just-spawned enemy now lands (status note: "via the world's level list"); confirm the breadcrumb spine reaches the enemy and you can drill to its HP. Two honest residual limits (acceptable, not bugs): (1) the chain is NOT a clean CE static-pointer chain (the world→level hop is a back-reference) — it's for in-tool navigation; (2) a truly unreferenced actor not in ANY world level still returns `not_reachable` (correct). Edel (build 1400) remains the complementary path when the player references the target. *Parent: Related Objects Phase 1 in-game test (dev-log 2026-06-19); recovery dev-log 2026-06-20.*

- **Locate from GEngine — alternate root for UI-widget / GameInstance-owned objects** — ✅ **DONE (builds 1542-1544, MERGED main PR #345 `f488592`; in-game VERIFIED)** — shipped as **Locate in GameEngine** (⚙ icon on all 10 🌍 surfaces). The existing `find_path_from_gworld` handler gained a `root_kind` field (`engine` → `rootObj = Genau::FindGameEngine().engineAddr`, `no_engine` when absent); `FindObjectGraphPath` was already root-agnostic so it was untouched. Reaches engine-layer objects (GameInstance / LocalPlayer / GameViewport / UMG widgets — the Octopath `PartyCharacterPanel_C` case) that no GWorld chain reaches; a deliberate complement to 🌍 (weaker for world actors, since `RecoverViaWorldLevel`/`ok_via_level` is World-root-gated). See dev-log 2026-06-22. *Residual: `deadline_ms` still hardcoded 20000ms in `FindObjectGraphPath` — optional follow-up.*

-----

## Multiple Values Group Scan — remaining phases (P1 shipped build 1276)

P1 (object-aware group scan, direct numeric leaves + one-level struct descent, exact-per-slot, mode toggle + master-detail UI) shipped builds 1276-1278 — new `Orden` SDR matcher. Follow-ups, in order:

- ~~**P1 in-game verification**~~ **DONE** — verified on SEED (UE4.27): single Value Search + Group Search both pass; Deep mode surfaces the buried `Tunes` block. *(dev-log 2026-06-18.)*

- ~~**P2 — prev-value per slot + offset-table**~~ **DONE (builds 1295-1302)** — per-slot scan type now on `Orden::SlotTarget` (`st`+`tolerance`+`targets2`, routed through `ComparePredicate`) + `RefineGroupCandidates`: First Scan takes Exact/Bigger/Smaller/**Between** per slot (Between = bounded-unknown entry, e.g. HP in [1,100]), Next Scan also takes the prev-value four (Changed/Unchanged/Increased/Decreased — compare each leaf vs its own previous round). Locked-offset table (`🔒 Class — Str@0x20, Def@0x24`) shows once all slots lock. **"Copy CE Script" / export is a deliberate WON'T-DO (not pending work)** — the owner exports the resolved chain from Live Walker (which already does it); do not re-add a group-side CE/export button. **prev-value group refine in-game VERIFIED on SEED** (Unchanged/Unchanged/Increased ran clean); Between first-scan live-verify still nice-to-have. *(dev-log 2026-06-18.)*

- **P3 — numeric containers as blocks — DONE (opt-in Deep, builds 1283-1285; scalar maps builds 1561-1562)**. The "Deep" toggle treats each numeric `TArray/TSet` + each struct-array/map element as its own block via the recursive `WalkContainerLeaves`, matching the group WITHIN one array (finds the SEED `Tunes[N]` case); single-value Deep forces the existing deep pass on all classes. The scalar-map follow-up added **scalar-valued + scalar-keyed maps** (`TMap<Name,int>` → value block `<map>.Value`, key block `<map>.Key`) by extending `ContainerCacheEntry` with `keyLeafType`/`valueLeafType` — closes the walker TODO **and** the Value Search "Proper scalar-map value/key capture" item (one shared fix); struct sides byte-identical, adversarial 4-lens review 0-confirmed. *Remaining (verify only):* in-game verify of the Deep path + a scalar-map (`Map<Name,int>`) game on SEED. *Parent: dev-log 2026-06-18 deep entry + 2026-06-22 scalar-map entry.*

- **Snapshot Group Match follow-ups — feature S1-S5 SHIPPED + MERGED main PR #348, in-game VERIFIED on SEED** (spec: [snapshot-group-match-spec.md](snapshot-group-match-spec.md); dev-log 2026-06-23). Remaining optional work, same `Orden` matcher: ~~**(1)** SPC Query "N-field intersection"~~ **DONE — SPC Group** (Single/Group toggle in the SPC tab; the N-snapshot, per-slot predicate-CHAIN generalisation of Snapshot Group Mode B; builds 1575-1584, in-game VERIFIED on SEED, dev-log 2026-06-23). Still open: **(2)** Class Pivot "co-varying tuples" (spec §3.1 row 3); **(3)** array-AS-BLOCK deep (each nested array its own block, like the live deep — both snapshot reuses shipped object-flat: array elements as the owner's leaves); **(4)** the snapshot-wide >2^53 double-precision limitation (carry exact target bytes per width like the DLL's `NumericTargetSet` — affects SPC/Diff/Pivot too, not just group match). All low priority — pick up if a real case motivates it.

-----

## Locate-in-GWorld — `IsGWorldAvailable` gate decouple (Value Search done, others pending)

- **Decouple the other panels' 🌍 from `IsGWorldAvailable`** — Effort: **S** · Risk: low. Value Search's per-row/per-slot "Locate in GWorld" was gated `IsEnabled="{Binding IsGWorldAvailable}"` (and the command short-circuited on it); on TQ2 (proxy mode) the flag read false even though GWorld was resolved, so the button was silently disabled (no `find_path` sent, no feedback). Fixed for Value Search (build 1311) by decoupling — the button is always clickable and the DLL's `find_path_from_gworld` (which returns `invalid`/`no path` with no live UWorld) is the source of truth. **The same gate still exists on Instance Finder / Snapshot / SPC 🌍 buttons** — apply the same decouple if a user hits it there. Open question worth a quick check: *why* did `IsGWorldAvailable` (an `[ObservableProperty]` fed from `state.HasGWorld`, which was true) evaluate false in the button binding on TQ2 — a binding-resolution quirk in the group RowDetails template vs a real state-propagation timing bug. *(dev-log 2026-06-18.)*

-----

## CE export drilldown — remaining gaps (Phase A/B/C shipped)

Phase A (CE XML/Field container-value expansion, build 1085), Phase B (CSX parity,
build 1098), Phase C (depth-from-current-view tests + CSX truncation note, build
1098) all shipped. Spec: [ce-export-drilldown-spec.md](ce-export-drilldown-spec.md).
Open follow-ups (low priority):

- **CSX struct-array element full re-walk** — Effort: **S** · Risk: low. CSX struct
  arrays still flatten the shallow Phase-F `StructFields` preview, so nested
  structs/maps *inside* an array element stay shallow. CE XML's
  `EmitStructArrayProperty` already re-walks each element via `resolvedStructs`
  (build 1076); mirror that in `ConvertArrayStructElementsToFields` (stamp
  `StructDataAddr` per element + route to `EmitStructPropertyFlattened`). The unified
  resolver already populates `resolvedStructs` for array struct elements — only the
  CSX emit path ignores it.
- **Nested-container truncation note** — Effort: **S** · Risk: low. The
  `⚠ Container element limit` note (CE XML + CSX) only scans top-level fields; a
  container clipped by `ArrayLimit` *inside* a drilled struct/pointer is unreported.
  Cheap: scan `resolvedStructs`/`resolvedInstances` values too. (Marked optional in
  the spec.)
- **FName → live readable string in CE via a "UE FName to String" custom type** —
  Effort: **M-L** · Risk: med (CE-Lua + per-game GNames config). Highest-value of the
  remaining sample.CSX gaps. Today FName is shown statically: **CSX** emits a raw
  8-byte qword (`MapCsxType` `NameProperty`→`8 Bytes` — no name at all); **Copy CE XML /
  Field** emit the 4-byte `ComparisonIndex` + a static `DropDownList` snapshot (index→string
  captured at export time, arrays only; single scalar FNames mostly show the raw index).
  sample.CSX instead uses `Vartype="Custom" Customtype="UE FName to String"` — a CE custom
  type (Lua, registered via `registerCustomTypeLua`) that resolves the FName index against
  GNames **live, inside CE, at runtime**, so any FName value updates to its current string.
  The exporter change is the easy 10% (emit `Custom`/`Customtype` for `NameProperty`, opt-in,
  keep DropDownList as fallback); the real work is **shipping + auto-configuring a GNames-aware
  FName custom-type Lua**: parse the pool block layout (UE4 `TNameEntry` vs UE5 `FNamePool`,
  stride/casing — knowledge already in the DLL's `Serie` module) and feed it the live GNames
  address. GNames must be **ASLR/restart-stable** → reuse the AOB / GWorld-anchor recovery from
  the Copy CE AA Script work (dev-log 2026-06-21). Benefits all three exporters (CSX gains names
  at all; CE XML/Field gain live resolution vs the frozen snapshot + single-scalar coverage).
  Decide: keep DropDownList as a no-setup fallback when the custom type isn't installed.
  *Parent: CSX 7.7+ Binary format + sample.CSX audit (dev-log 2026-06-21, PR #335); FNamePool =
  `Serie` module; GNames anchor = `project-aa-script-gworld-walk`.*

-----

## Teleport Coordinate Library — P1-P5 SHIPPED (builds 2257-2267), needs in-game verification

Design contract: **[teleport-coord-library-spec.md](teleport-coord-library-spec.md)**.
Write-up: [dev-log.md](dev-log.md) 2026-07-23. All five phases are on `dev`, 2777 tests green,
**zero DLL/pipe change**. What remains is verification that unit tests structurally cannot do.

> **User verification pass 2026-07-23:** the **DLL-flavour** emitted Lua **WORKS in CE** (picker
> opens, list + filter + teleport), **CSV export/import was exercised**, and the group/label round
> trip was driven from the Lua picker UI. Two results came out of it — the DLL flavour is verified,
> and the **no-DLL (standalone) flavour does NOT work on the tested title**. The remaining VERIFY
> rows are the ones that pass was not aimed at. *(Which title the standalone failed on still needs
> filling in here.)*

- **✅ VERIFIED — the emitted picker, DLL flavour (2026-07-23).** Form opens, ListView fills, filter
  narrows, Teleport works from the picker. That confirms the CE control/property set lifted from
  `CrimsonDesert.CT` (`lv.ItemIndex`, `readString(mb + params + 48, 127, false)`, `rgGroup.Items.add`)
  against a real CE — the highest-risk unknown in P3/P5. *Delete after the batch merges to main.*

- **✅ VERIFIED — CSV export/import (2026-07-23).** Round trip exercised. NOT separately confirmed:
  the two deliberate hostile cases (a group named `1-2` that Excel mangles into a date; a label
  starting `=` surviving the formula armouring). Retry those only if a real library corrupts.

- **BUG / LIMIT — the no-DLL (standalone) flavour does not teleport on the tested title** —
  Effort: **M** · Risk: med. Confirmed by the user 2026-07-23. The spec already carries the caveat
  ("needs *UE5 Trainer: Setup* enabled first; may not visibly move"), so this is that caveat firing
  rather than a surprise: the standalone flavour writes the pawn's location RAW, and a game that
  re-asserts its own transform every tick simply overwrites it. **Decide between** (a) documenting it
  as a hard limitation of the no-DLL flavour (cheap, honest), or (b) having the standalone picker
  DETECT the snap-back — read the location back N ms after the write and, if it drifted back, say so
  in the status line instead of silently doing nothing. (b) is what stops the next user concluding
  the feature is broken. *Parent: P5; teleport-coord-library-spec.md §10.*

- **VERIFY IN-GAME — the teleport itself, from the APP (not the CE picker)** — Effort: **S** · Risk: low.
  Still open: save current pos → move → Teleport selected → land back, then the **map guard** (save on
  map A, load map B; plain Teleport must refuse and Force must be the only way through). Watch for
  `Tier == 2` (raw-write fallback) in the status line. *Parent: P1.*

- **✅ VERIFIED — the quick-jump menu label (2026-07-23).** The Teleport tab's right-click menu shows
  "Coordinate Library" and the user has been navigating with it, so the `SemiBold`-TextBlock walk still
  resolves a card whose label lives inside an `Expander.Header` (spec §7's worry). *Delete after the
  batch merges to main.*

- **VERIFY — DataGrid behaviour at scale** — Effort: **S** · Risk: med.
  The grid carries `MaxHeight="260"` precisely because `ContentRoot` is a vertically unbounded
  ScrollViewer and an unconstrained DataGrid would not virtualize. Load ~4 000 entries (import a
  generated CSV) and confirm scrolling and filtering stay responsive. Also measure where CE's
  ListView actually stutters — the picker's 2 000-row display cap is inherited from the reference
  table as an unverified guess. *Parent: P1 + P3.*

- **VERIFY — experimental gating** (DECIDED + implemented, build 2269) — Effort: **S** · Risk: low.
  The card is now gated on `ExperimentalEnabled` like the other five. Confirm the whole card
  appears/disappears with the System-tab checkbox, that it is absent from the tab's right-click
  quick-jump menu while hidden (the code-behind skips a card that is not `IsEffectivelyVisible`),
  and that toggling the gate off mid-preview clears a pending CSV/Lua import. *Parent: user call
  2026-07-23; spec §10.4.*

- **Unrelated finding, worth doing anyway** — Effort: **S** · Risk: low.
  `AobMakerBridgeService.WriteMessageAsync` (`:495-506`) has **no send-side size check**, and the
  plugin's oversize path (`pipe_server.cpp:61`) returns *without writing a response*, so an oversized
  push surfaces as a confusing "no response"/timeout instead of a size error. Add a client-side
  pre-flight check against the 10 MiB cap. A 4 000-entry library is ~480 KB so this is not urgent for
  the coordinate library, but it is the failure mode a user would hit first. *Parent: spec §10.6.*

-----

## Teleport — follow-ups (deferred / future research)

Teleport shipped (Wirbel, build 1027-1043). Works where the possessed pawn is
the visible character (SEED) and, via the deep-force, even on hard-cooked HD-2D
titles (Octopath — character moves). Open items, all per-game / research-grade:

- **Camera/POV doesn't follow on hard-cooked games (Octopath / SE HD-2D)** —
  Effort: **M-L** · Risk: med. **Read-only camera-POV display DONE + LIVE-VERIFIED
  builds 1110-1112** (Teleport tab → "Camera POV" → Get POV; `Wirbel::GetPov` +
  `teleport_get_pov` + a "Get camera POV" mailbox AA record). POV now **reads on
  all four tested titles** — getters on SEED / DQ III, and a fully-reflected
  `CameraCachePrivate.POV` raw fallback on TQ2 / Octopath (getters present but
  `ProcessEvent` returns nothing) — so you can *measure* the camera↔pawn delta.
  **That's the READ; the actual camera-FOLLOW-after-teleport fix below is still
  open** (POV read confirms the divergence but doesn't move the camera). Phase 2 ideas: FOV set/reset (`SetFOV`/`LockedFOV` is the only
  persistently-settable POV component); a "re-anchor camera" nudge after teleport
  (`SetViewTargetWithBlend(pawn,0)` + `SetGameCameraCutThisFrame()`). There is no
  universal Set POV — `UpdateCamera` overwrites it every tick. See
  [teleport-spec.md](teleport-spec.md) §15.
  The deep-force moves the pawn's root
  `ComponentToWorld`, but the camera tracks a separate child component
  (SpringArm / CameraComponent) or follow-camera actor whose world transform we
  never refresh — so the view stays put and can get **stuck unrecoverably**
  (no in-game event re-syncs the view-target chain; a save reload / area
  transition fixes it). Options, in order of cheapness: (1) invoke
  `APlayerController::SetViewTargetWithBlend(pawn, 0)` to re-anchor (likely also
  cooked out on these titles); (2) `APlayerCameraManager::SetGameCameraCutThisFrame()`
  for an instant cut; (3) deep-force the follow-camera component's
  `ComponentToWorld` too (need to *find* it — game-specific); (4) recompute
  child world transforms (manual `UpdateChildTransforms` — native, no
  reflection, hard). **Deferred**: no universal solution; a failed camera nudge
  risks making the stuck-camera worse. In-app disclaimer covers it.
- **TQ2 teleport — FIXED (build 1113); two minor caveats remain.** The old
  "separate visible actor" theory was **disproven** (build 1113 ViewTarget
  diagnostic): TQ2's pawn IS the camera view-target and owns the mesh + CMC. The
  failure was `K2_SetActorLocation` reporting success but not moving + a stale
  cached transform; fixed by always running `K2_SetWorldLocation` + deep-force in
  the CMC-freeze path. Marker teleport now works. Remaining: **(a) cursor teleport
  blocked** — TQ2 strips `GetMousePosition` (returns 0,0 / virtual cursor),
  `GetViewportSize`, and `KismetSystemLibrary`, so there's no generic way to read
  the cursor target (per-game RE only — low value, deferred); **(b) minor visual
  lag** — the mesh snaps over on the next move after a marker teleport (CMC network
  smoothing; a CMC smoothing-offset reset would fix it, Effort S, deferred). See
  [lessons-learned.md](lessons-learned.md) "TQ2 verdict".
- **Gamepad / mouse-extra-button hotkeys** — Effort: **M** · Risk: low.
  Marker hotkey capture is keyboard-only (`RegisterHotKey`). Mouse extra buttons
  + gamepad need low-level hooks / XInput polling ("record on all-released" per
  the user's spec). Deferred until requested.

-----

## Value Search — coverage + memory (build 923 plan)

Dependency order was **V3-A → V3-B → V1a → V3-C → V2**; **all shipped** (V3-C build 949:
DLL owns the set, UI is a server-side-filtered/sorted window; V2 build 954: ceiling
raised to 1M, sort/filter verified sub-second). Remaining open: **V1b** (container
prev-value refine) and **V1c live-verify**.

- **Deep Value-Search candidate → multi-level 🌍 drill** — ✅ **DONE build 1208.**
  Generalised `TryParseStructArrayInner`/`DrillToStructArrayInnerAsync` into
  `TryParseContainerPath` + `DrillDisplayPathAsync` (parse the full multi-`[N]`
  display path into ordered `(name,index)` segments; drill each as a container
  hop or direct-struct field; land on the final leaf). Wired into BOTH the VS/SPC
  `LocateInGWorldAsync` reach branch AND `NavigateToInstanceFieldAsync`
  (Open-in-Live-Walker — also fixes the offset-0 mis-select for deep candidates).
  Verified by a 4-agent audit (drill-sites + scan/capture correctness); the value
  was always FOUND — this was the land-ON-it polish. ⚠ in-game live-verify pending
  (multi-`[N]` 🌍 should land exactly on the SEED `...Tunes[N]` value).

- **Top-level `TSet<FStruct>` / `TMap<K,FStruct>` depth-1 inner leaves (Value Search)** —
  Effort: **S/M** · Risk: low. The static depth-1 collector (`collectStructArrayInner`)
  only covers `TArray<FStruct>`; the recursive `deepEmit` skips `depth<2` to avoid
  double-counting it. So the DIRECT fields of a struct element in a *top-level*
  Set/Map are scanned by neither path. (Nested ones — the SEED `MsTunes` case — are
  depth≥2 and ARE caught.) Fix: add a Set/Map analogue to `collectStructArrayInner`,
  or relax `deepEmit` to `depth>=1` for the Set/Map element side only. Audit #2.

- **SPC Strict-join `prop_offset` migration edge** — Effort: **S** · Risk: low.
  1-level struct-array element rows now store `prop_offset=0` (build 1205, was
  `nf.Offset`); a Strict-mode SPC query that mixes a pre-1205 and a post-1205
  snapshot keys the same logical field differently. Either zero `prop_offset` for
  array-element rows in the Strict key, or bump the schema to force recapture.
  Audit #4. *Cosmetic unless mixing snapshots across the 1205 boundary.*

- **Interesting Props: optional "Locate in GWorld" 🌍** — ✅ **DONE (builds 1531+, MERGED main PR #344 `86fb765`; in-game VERIFIED)** — added a leftmost per-row 🌍 icon column that resolves a live non-CDO instance of the row's class then calls `LocateInGWorldAsync(addr, 0, null, stopAtParent:true)` (the Interesting Functions handoff, gated on `IsGWorldAvailable`). Same PR added the prominent Live Walker failure banner. The panel also gained the ⚙ Locate in GameEngine button via PR #345. See dev-log 2026-06-22.

- **V1b — container prev-value refine (stable key)** — Effort: **M** · Risk: **high**.
  `Candidate.addr` stores a raw element address; TArray realloc already makes it stale,
  and TSparseArray is worse — freed slots get reused, so `c.addr` on refine may point at
  a different logical entry → Changed/Unchanged semantics silently lie. Store
  `container addr + slot index` as a stable key and re-walk the sparse array on refine
  (same idea as snapshot's `SelectArrayInnerKey`). **Do only if refine-on-container is
  actually requested.**
  *Parent: V1a TSet/TMap key|value scan shipped First-Scan-only, build 927 (dev-log
  2026-06-06).*

-----

## Experimental: Snapshot / SPC / Class Pivot

Gated behind the System-tab opt-in (`IExperimentalGate`). Design of record:
[experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md). Phases
0/A/B/C (C1+C3-lite+C4+C5+C6) + N1 noise picker all shipped; the engine rework
(in-memory hash-joins), heavy-query cancellation, persisted pivot index, and
Windows-only AOT backend all shipped (dev-log builds 805–923).

- **C2 — find-by-value locator + pivot handoff** — Effort: **M** · Risk: **med**.
  Closes the loop: locate which class/field holds a known value, then hand off into
  Class Pivot.
  *Parent: Pivot Phase C (C1/C3-lite/C4/C5/C6) shipped builds 830-877 (dev-log).*

- **A3c — CE .CT freeze-export from a diff/SPC/pivot hit** — Effort: **M** · Risk: low.
  Copy Address already covers the manual path; this is the full automated freeze export.
  *Parent: A3 diff engine + Copy Address shipped build 817.*

- **Heavier C3 scorer** — Effort: **M** · Risk: med. Jaccard stability + greedy compound
  key + class shortlist / volatility ranking (the "29i-3" scorer).
  *Parent: C3-lite key scorer shipped build 830.*

- **N1 v2 — per-`(class, prop)` deny granularity** — Effort: **M** · Risk: low. v1 is
  by-class; some classes (`ACharacter`, `APawn`) carry both gameplay fields (`Health`)
  and noise (`Velocity`, `LastRenderTime`). v2 would chevron-expand each Top-N row to its
  Top-K noisiest props. **Defer until v1 proves the bulk case is solved.**
  *Parent: N1 per-tab class denylist shipped builds 908-910 (dev-log 2026-06-05).*

- *(optional)* **`discrete`-style gzip blob storage** — Effort: M · Risk: low. Only if
  snapshot DB size becomes a real concern.

-----

## Bytecode cross-reference: property ↔ function — deferred follow-ups

Path 1 (BP Kismet bytecode) core + v2a, and Path 2 (native via Zydis/Denken) forward
direction, both shipped (dev-log builds 838-872).

- **Path 1 v2b — CFG-precise attribution** — Effort: **L** · Risk: **med-high**. v2a's
  "nearest entry offset" mis-attributes a sub-graph reached from multiple events via
  jumps; the `EX_Let*` write detector misses wrapped LHS (`Other.Field` / `Struct.Member`
  / `Arr[i] = x`). A real variable-length decoder (follow `EX_Jump` / `EX_JumpIfNot` /
  `EX_ComputedJump`, parse the LHS expression tree) fixes both. Reference:
  `vendor/RE-UE4SS/.../KismetDebugger.cpp` (`render_expr`) + `EExprToken`. **Only when a
  real mis-attribution motivates the cost.**
  *Parent: Path 1 + v2a shipped builds 838-861.*

- **Path 2 follow-ups** — (a) **reverse direction** (property → native funcs; needs
  disassembling every native function per query — expensive); (b) **SIB-indexed**
  `[reg+idx*scale+disp]` accesses (currently skipped); (c) **CFG-aware branch following**
  (only fall-through + direct call/tail-jmp followed today); (d) live tuning of the
  `this`-tracking + Func-offset detector across more games.
  *Parent: Path 2 native UFunction analysis shipped builds 862-872.*

-----

## Call-UE-function / invoke

- **#2 Live ProcessEvent Call Profiler** — ✅ **SHIPPED build 2109** (new `Linie` module +
  "Live Funcs" tab). Ranks UFunctions by **observed behaviour** (Start → perform action →
  Stop → see what fired), the root-cause answer for game-specific functions (OpenShop/Dash)
  name heuristics can't find. Hot-path gate is one relaxed `atomic<bool>` load when off (the
  map + mutex are only touched while recording — the recording-window mutex was accepted over
  a lockless counter per the plan; escalate to a sharded table only if an in-game benchmark
  shows contention). `pe_profile_start` forces the PE hook via `UE5_EnsureGameThreadHook`.
  **Remaining:** in-game acceptance test (shop/dash on a live UE title) + confirm nil overhead
  with recording off. See [dev-log.md](dev-log.md) build 2109.

- **#7 View Snap Hotkey (Property → snap-to-step)** — Effort: **S-M** · Risk: **low**.
  Bind a CE hotkey that snaps a Float/Double property to the next N° step (rotation
  snap) — generalises to MoveSpeed multipliers, zoom levels, time-dilation cycling.
  New row action + `SnapHotkeyDialog` + `scripts/ue5_snap_helper.lua` +
  `SnapHotkeyScriptGenerator.cs`. **95% mirrors the build-719 freeze Route B.**

- **Add-on: AA(Baked) "Auto-tick every N ms"** — Effort: **S** · Risk: low. Wrap the
  generated `invokeUFunction` call in a `createTimer(N, callback)` block ([DISABLE] tears
  it down); same per-script keyed handle table as FreezeScript. Lands as a 1-day add-on
  after #7 (both touch the script generator + dialog).

- **#5 v2 — ObjectProperty return resolution + recursive struct expansion** —
  Effort: **S + S**. (a) Resolve ObjectProperty/ClassProperty returns to "Name (Class)"
  via a DLL pipe round-trip (`resolve_object_name(addr)` or extend the invoke response).
  (b) Recursively expand nested structs (`FHitResult.Location` → its own FVector rows).
  *Parent: #5 structured-return DataGrid shipped build 775, PR #211.*

- **#0c FTransform Translation offset** — Effort: **S** · Risk: low. `VectorStructNames
  (FTransform)` returns empty → zero hits. Needs per-version Translation offset detection
  (UE4 / UE5-non-LWC at +16, UE5 LWC at +32).
  *Parent: Value Search Phase 2 vectors shipped build 757, PR #208.*

- **FString / FText / TArray input in baked AA Script** — Effort: **M** · Risk: **med**.
  Functions like `KismetSystemLibrary::PrintString` are observable side-effect verify
  targets but unreachable — the helper's `writeBakedParams` only handles scalar inputs.
  Needs CE-side buffer alloc + FString header write (ptr/count/max) + keep-alive + free
  in the cleanup timer. Same pattern for FText + TArray-of-scalars. (Open since the build
  643-644 ES2 live test.)

- **LiveWalker batch generator (v2 of the CT batch)** — Effort: **S-M**. Heterogeneous
  rows (functions + fields + struct sub-fields + array elements) + drilldown state — needs
  its own UX pass.
  *Parent: #3 multi-row → one .CT batch (Interesting Funcs/Props) shipped build 760.*

- **Dual-connection pipe (eliminate head-of-line blocking)** — Effort: **M** · Risk:
  **high** (in-process DLL concurrency). **POSTPONED 2026-06-01.** Full design:
  [multi-connection-pipe-proposal.md](archive/multi-connection-pipe-proposal.md) *(archived — superseded by [multipipe-eval.md](multipipe-eval.md))*. Engine-side
  concurrency is already safe (builds 792/793 + SessionManager); residual risk is Fern's
  accept/shutdown rewrite. Benefit is moderate (parallel scans already shrank the blocking
  window) — revisit only if "UI freezes during a big scan" becomes a real pain.

- **KismetMathLibrary stub-pattern UX hint** — Effort: **S** · Risk: low. On UE 5.5+
  cooked Shipping, `KismetMathLibrary::Add_IntInt` etc. consistently return 0 (cooker
  strips the `execXxx` thunk). Add a "Recommended verification targets" footer hint when
  the selected class is `KismetMathLibrary` / `KismetSystemLibrary`, and update
  lessons-learned / test-games. (Not a feature to enable calling them — a UX redirect.)

- **Mimic: zero the ReturnValue slot before invoke** — Effort: **S** · Risk: low. ES2
  showed Before/After dumps identical (stale `0x49`) so we can't tell "wrote 73" from
  "didn't touch ReturnValue". Overwrite the slot with a sentinel / zero before calling PE
  so the After dump is unambiguous. ~2-line patch in `Mimic.cpp` (both fast path + game-
  thread dispatch).

- **CE Lua AA Script activation hang — UX hardening** — Effort: **M** (mitigation) ·
  Risk: low. AA Script sometimes never reaches the mailbox (CE Lua froze or hid an error).
  Mitigations: re-arm helper-injected check on UI Connect; mailbox heartbeat `print()`
  before the write; early-exit `if not g_invokeMailbox then showMessage(...) end` in the
  helper. (UX hardening, not a correctness bug — we can't distinguish AA-error from CE
  freeze from the DLL side.)

-----

## Time / Timer control (Hemmung) — feasibility evaluated (2026-07-13), not yet built

Eval memo: memory `project-timer-feature-eval`. Multi-agent + adversarially verified.
User ask = auto/manual-assisted discovery of game **time/timer components**, list the
**methods** handling them, real-time **lock/reset/adjust + multi-select**, and a
**cross-session persistence** path (Copy-CE-Field-like). Confirmed the DLL has ZERO
`TimeDilation`/`WorldSettings`/`TimerManager`/`CustomTimeDilation` refs today — new
capability, but every building block already ships. **Verdict: build in layers; the L2/L3
native-RE parts are exactly the reflection-invisible ones — cut them from v1.** Order below.

- **L0 — timer discovery docs recipe (ships today, ~0 code)** — Effort: **S** · Risk: low.
  BP-authored `Cooldown`/`RemainingTime`/`RespawnTime`/`Duration` floats are reflected
  UPROPERTYs → already found by Property Search (by name) + Value Search (by value; a
  ticking countdown survives repeated **Decreased** refines, count-up via Increased, paused
  via Unchanged). Lock via class-wide **Freeze** (`FreezeScriptGenerator`+`ue5_freeze_helper.lua`,
  restart+respawn-safe by class+offset re-enum); multi-select via **CheatTableBuilder** (a
  `List<CtPropertyRow>`, no builder change). Deliverable = a `docs/tips.md` recipe + a Group
  Scan example ({Elapsed↑, Remaining↓} in one object). *Parent: this eval.*

- **L1 — global game-speed control + Timing discovery category — DISCOVERY + DLL SHIPPED (build 2148); UI Time card (Part C) REMAINING** —
  Effort remaining: **M** · Risk: low. **DONE (build 2148, dev):** the `Hemmung` DLL module +
  `PropertyCategory.Timing` discovery category shipped and green (all 2453 C# tests + C++ self-tests).
  `Hemmung.cpp/.h` (roster 🟢) = absolute-value `Laufen` sibling: DIL_GLOBAL `AWorldSettings::TimeDilation`
  (GWorld→PersistentLevel→WorldSettings reflected chain + `Aura::FindInstancesByClass("WorldSettings")`
  fallback) + DIL_PAWN pawn `AActor::CustomTimeDilation`, write-on-drift re-assert worker, clamp
  [0.0,100.0]; exports `UE5_Set/ResetTimeDilation`, Mimic `CMD_TIME=15` (`TimeOp` SET/RESET, ufuncAddr =
  target), pipe `set/reset_time_dilation` + `get_time_state`. Discovery: `PropertyScoringTable.Timing`
  (append LAST → BuffDuration stays Combat) + `TimeStructTypes` (Timespan/DateTime/QualifiedFrameTime/
  FrameTime/Timecode) + `SeedQueries` timer terms + `ClassLocationScorer` GameplayEffect/GameplayAbility/
  WorldSettings +2 + function-side `UtilityKeywords` widen (Cooldown/Dilation/Delay/Interval/Elapsed/
  Recharge). Dev-log 2026-07-13; memory `project-timer-feature-eval`.
  **Part C UI Time card — DONE (build 2149; UI SUPERSEDED at build 2207 by the dual-row World+Player
  card — see dev-log 2026-07-15).** A "Time Dilation" card in the Teleport panel beside
  Move-Speed/Gravity: *as first shipped,* a "Player only" toggle (global `TimeDilation` vs pawn
  `CustomTimeDilation`), 0–3×
  slider + % + presets (Freeze/¼×/½×/1×/2×) + Apply/Reset/↻ + badge/readout; new `IDumpService`
  `Get/Set/ResetTimeDilation` (+ `TimeDilationKnob`/`TimeDilationSetResult`/`TimeState` models) + VM
  commands + en.axaml strings + 6 VM tests (2459 C# green).
  **CE Lua/.CT generation — DONE (build 2150).** `TimeDilationScriptGenerator` (mirrors
  `MovementScriptGenerator`): stateful `[ENABLE]`/`[DISABLE]` records poking `CMD_TIME=15` (op SET on tick, op
  RESET on untick); `CeLuaHygiene`-compliant; wired into the Teleport panel's "Add to CE" (2 records: World +
  Player) + "Save .CT" batch; 6 generator tests. So the dilation lock now works from a standalone CE table
  without the UI.
  **Persistence — DONE (build 2151).** (1) live read-back: `SetConnected` reflects the DLL's held dilation on
  connect + on target-switch (syncs the slider to the engaged value; `RefreshHeldTimeStateAsync`), disconnect
  resets the badge — the "state lives in the DLL, survives a UI reconnect" markers model. (2) disk preference:
  `TeleportUiOptions.TimeDilation`/`TimeTargetIsPawn` in `ui-options.json` pre-fill the last value+target
  across UI restarts (NOT auto-applied; live read-back wins). +2 VM tests + options round-trip.
  *(Those two keys were renamed `WorldTimeDilation`/`PawnTimeDilation` at build 2207 with no migration.)*
  **L1 COMPLETE + LIVE-VERIFIED on Elliot (UE4.27, build 2151)** — log confirms `set_time_dilation target=pawn
  value=0.5` → `hold 0.5000 (rc=0)`, held 0.5/1.0/2.0/1.4688×, reset clean, `get_time_state` polled on connect.
  Per-pawn `CustomTimeDilation` exercised; global `WorldSettings::TimeDilation` wired+unit-covered but not yet
  live-exercised (verify opportunistically). Also NOT built (deferred):
  `SetGlobalTimeDilation`/`GetTimeSeconds` invoke wrappers, and a dedicated opt-in function-side
  `FunctionCategory.Timing` bucket (timer methods currently land in Utility at weight 3 — below threshold
  without a class bonus / Show-All).
  **DON'T over-promise (adversarial corrections):** (a) locating the ACTIVE WorldSettings is
  NOT one line — `Aura::FindInstancesByClass` matches immediate class FName only (no `IsA`,
  `Aura.cpp:1365`), misses BP subclasses + can't disambiguate streaming/PIE sub-worlds →
  prefer INVOKING `SetGlobalTimeDilation` (calls `GetWorldSettings()` internally, price = its
  `[Min,Max]` clamp; a direct write bypasses the clamp but needs the right instance);
  (b) `Ubel` only READS today — a generic "write reflected float by name on object" surface is
  new; (c) paused worlds can't be stepped via dilation + active Sequencer flickers within the
  250ms drift window; (d) cross-game parity is CONDITIONAL — L1 inherits the tool's GObjects
  baseline burden on hard-cooked/SE-fork/encrypted titles, and bespoke non-UE time multipliers
  won't respond at all. Persistence (Copy-CE-Field-like), best→worst: class-wide **Freeze** >
  **GWorld-anchored AA Script** (`CeXmlExportService.GenerateGWorldWalkedSymbolXml`, `useAob`;
  registers a SYMBOL only → add a CE freeze or route TimeDilation through FreezeScriptGenerator
  with className=WorldSettings) > **StandaloneTrainer**; NONE survive a game PATCH; multi-select
  → CheatTableBuilder verbatim. *Parent: this eval; reuses movement-tuning-laufen + godmode-spec
  + autodetect-stats + standalone-trainer + aa-script-gworld-walk.*

- **L2 — GAS effect cooldowns (DEFER; feasible-with-caveats)** — Effort: **L** · Risk: high.
  Deep `UAbilitySystemComponent::ActiveGameplayEffects` walk: partly non-UPROPERTY
  (FastArraySerializer), remaining time is COMPUTED (`Duration-(Now-StartWorldTime)`, needs
  world time), version-fragile. Current ASC folding (P4 cross-object) reaches
  SpawnedAttributes/AttributeSets VALUES, NOT ActiveGameplayEffects. Only if a GAS title
  motivates it. *Parent: this eval.*

- **L3 — live `FTimerManager` timer enumeration (DEFER; research-grade native RE)** —
  Effort: **XL** · Risk: high. `FTimerManager` is NOT a UObject (FNoncopyable via native
  `UWorld::GetTimerManager()`); its `FTimerData` timers live in a TSparseArray+heap with
  handle indirection, none reflected. Per-version native layout (ExpireTime widened float→double
  in UE5; internals refactored across 4.x) + often-unresolvable C++/lambda callbacks =
  Avowed-packed-FUObjectItem-tier. A reflected BP `FTimerHandle` var is opaque (just an index)
  → does NOT yield remaining time. Recommend NOT in v1. *Parent: this eval.*

- **E — Linie cadence flag — DONE + LIVE-VERIFIED on Elliot (UE4.27, build 2158).** `Linie::Stat` Welford
  inter-arrival mean/variance (fed the timestamp `Stark.cpp:143` already reads, zero hot-path cost);
  `pe_profile_get` emits `mean_period_ms`/`cv`/`gap_samples` + logs the periodic candidates; UI
  `PeProfileEntry.IsPeriodic` (≥3 gaps, CV≤0.25, period out of the ~40 ms frame band, ≤30 s) → "Timer" badge +
  Period column + "Periodic only" filter (idle-window workflow). +12 tests. **Verified:** an idle recording
  flagged 3 periodic funcs out of ~90 (`BP_SupportFairy_C::TryAttackEnable`+`ExecuteUbergraph` @ ~325 ms
  cv 0.02 = a real ~3 Hz BP timer; `ProvideSingleActor` ~108 ms), Tick correctly excluded, stable across two
  windows. Native lambda/member-ptr timers bypass ProcessEvent (documented). *Parent: this eval; extended
  Linie/LivePEProfiler build 2109.*

-----

## MindsEye licensee fork — follow-ups (GObjects + GNames SHIPPED builds 2220/2238)

Both halves are live-verified end-to-end, but **on game version 7.3.1 only** (PE hash
`0863E3B90C993000`). Everything below was identified while shipping and deliberately not blocked on;
full context + the re-derivation playbook is in [mindseye-fork-notes.md](mindseye-fork-notes.md).
**Before touching any of these, check the PE hash** — if it moved, the constants come first.

- **Wide `FNameEntry` payloads are not de-obfuscated** — Effort: **S-M** · Risk: low.
  `Serie::GetString` applies the XOR only in the **ANSI** branch; the wide branch reads and
  `EncodeUtf16`s the raw ciphertext, which `IsImplausibleWideName` then rejects → empty name.
  `Genau::ObfChainOk` likewise bails on the first wide entry ("stop, do not judge"). The fork ships a
  wide twin de-obfuscator (RVA `0x0178B540` on the solved build, `add r8,r8` — i.e. the same key,
  applied over 2-byte units), so wide names **are** obfuscated and the key lookup already works for
  them. Low practical impact (most FNames are ANSI) but it is a real hole: any wide name silently
  resolves empty rather than wrong. Fix = mirror the ANSI XOR into the wide branch (per-byte over the
  UTF-16 payload) + let `ObfChainOk` corroborate on wide entries instead of aborting.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

- **Process-lifetime caches that no rescan clears** — Effort: **S** · Risk: low.
  Two function-local statics survive a full re-scan: (1) `Genau::TryObfuscatedPool`'s
  `s_ctxTried`/`s_ctxCache` — `FindGNames`' reset block clears `g_nameChunksOffset` /
  `g_namePayloadGap` / `g_nameKeyTableCtx` but **not** these, so **one failed AOB scan is sticky for
  the whole process** and a rescan never retries the key-table resolve; (2)
  `Flamme::IsExperimentalEnabled()` caches its answer, so toggling the UI experimental switch after
  the DLL is loaded has **no effect until re-inject** (arguably correct — but it is undocumented in
  the UI and reads as a broken toggle). Fix (1) by clearing the statics from the same reset block;
  for (2) either re-read on each query or surface "re-inject required" next to the toggle.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

- **No test coverage for any of the fork-specific paths** — Effort: **M** · Risk: low.
  `dll/tests/` holds only `dll_helpers_test.cpp` + `utf8_helpers_test.cpp`; nothing exercises the
  preset-bound `LayoutPreset::itemHint` gate, `TryObfuscatedPool`'s acceptance rule, or
  `Serie::LookupTagKey`'s open-hash probe. All three are **pure enough to unit-test without a game**
  (fabricate a synthetic chunk/pool/key-table in a buffer), and two of them encode load-bearing
  invariants a refactor could silently break: the item hint must stay **evidence-gated**
  (`hGood>=8 && hBad*4<=hGood`) so it can never win on a 50%-aliased stride-16 read, and the pool
  must be **REFUSED when the key table is unresolvable** even though block 0 decodes. Also worth a
  regression test: the tag→key cache publishes value+flag in **one** `std::atomic<uint16_t>` — the
  two-plain-stores version produced wrong keys across threads.
  *Parent: MindsEye, dev-log 2026-07-19 (builds 2220 + 2238).*

- **`find_anchors.py` from the re-derivation playbook is not committed** — Effort: **S** · Risk: low.
  [mindseye-fork-notes.md](mindseye-fork-notes.md) step 0 tells the reader to run
  `python find_anchors.py <exe>`, but `tools/pe/` only has `disasm_function.py` (which takes explicit
  VAs — it neither parses `.pdata` nor searches for `__FILE__` anchors), `minidump_triage.py` and
  `pe_imports_exports.py`. So the playbook's first two steps have to be re-scripted ad hoc, which is
  exactly the friction the playbook exists to remove. Either commit the script under `tools/pe/` (with
  a line in [tools/README.md](../tools/README.md)) or rewrite steps 0–2 against what is actually
  committed. *Parent: MindsEye docs, commit `8ef4a9f`.*

- **`GAP = 2` is hardcoded, so a second fork needs a code change** — Effort: **S** · Risk: low.
  The obfuscated-payload gap is a `constexpr int GAP = 2` inside `TryObfuscatedPool` ("the only forked
  geometry seen so far"). That is the honest call today — inventing a search over gaps would weaken
  acceptance for zero known benefit — but note it as the first thing to generalise if a second
  licensee fork with a different `FNameEntry` shape appears. Do **not** pre-emptively loosen it.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

-----

## Property scoring / discovery

- **Class Family Browser (Proposal C)** — Effort: **L** · Risk: **med**. New "Class
  Family" tab bucketing game classes by inferred role (Character / Pawn / Inventory /
  Stats / Save / Components / DataAssets / DataTables / GameMode) — the "I have no idea
  where to start in a new game" entry point. **NOT a jump-in-and-code task** — the
  classification heuristic + UI design needs its own planning round (cluster the dump
  corpus's BPGCs by property-name similarity first).

- **Proposal B — per-row "similar BP-added properties"** — Effort: **M** · Risk: low.
  **DEFERRED indefinitely.** When the user lands on `bCanBeDamaged @ AActor`, surface a
  side-panel of fuzzy-matched game-specific bools (`bIsImmortal @ BP_PlayerCharacter_C`).
  B' (the broad-sweep Interesting Properties) already covers the workflow — revisit only
  if a real user reports the specific gap B fills.

- **Runtime `keywords.json` override** — Effort: **M** · Risk: **med**. Let users tune
  the scoring tables without recompiling (source-gen JSON for AOT; hardcoded fallback;
  additive vs replace mode; "Export current tables to JSON" seed button). **Only if a
  user actually asks.**

- **More-genre dump coverage (calibration)** — Effort: **S** (mostly user-side). The
  corpus is heavy on JRPG/sim/ARPG/FPS/racing/sandbox; missing MMO/fighting/horror/RTS/
  sports-sim. Dump 3-5 games per genre → re-run `scripts/analysis/analyze_dumps.py` → PR
  keyword adds with evidence attached.

-----

## Carryover capability gaps

Pick up when the active plan finishes or when blocked.

- ~~**MulticastSparseDelegateProperty UE 4.23-4.27**~~ — **DONE for 4.27 (build 2399)**, and
  the plan that used to sit here was based on a false premise. It said UE4 needed a separate
  AOB plus a walker branch for an `FObjectKey {FWeakObjectPtr; int32}` (16B) outer key with
  stride `0x60 → 0x68`. The DropIn 4.27.2 PDB shows the outer key is a **raw `UObjectBase*`**
  exactly as on UE5, and `FObjectKey` is **8** bytes, not 16. No new stride, no key
  reconstruction: deleting the `UEVersion < 500` gate was the entire fix, and `SPARSE_ES2_1`
  already resolved correctly on 4.27 (2 extra 4.27-verified patterns added anyway).
  **Remaining — narrowed 2026-07-29.** 4.23 is no longer unsampled: the self-built
  `UE4.23-Flying` oracle PDB-confirms the outer key is a raw `UObjectBase const*` at the very
  version sparse delegates were INTRODUCED, character-identical to 4.24, and `SPARSE_DI427_1`
  resolves it live. Combined with 4.24/4.25/4.27 that leaves **only 4.26** without a symbolised
  monolithic sample of its own (the 4.26 Satisfactory rows are modular DLLs and do carry the
  symbol). The key shape is now measured at every version the feature has ever had, so this is
  redundancy rather than a gap; the walker's runtime key-shape probe remains the real mitigation
  and is what covers licensee forks no sample can.

- **Find Refs v4 — TMap / TSet weak-like inner sides** — Effort: **M** · Risk: **low**.
  Currently Object/Class only; weak/soft pointer collections (`TMap<UObject*,
  FWeakObjectPtr>` etc.) silently miss target hits. Reuse the v3 weak-resolve helper
  inside the existing TMap/TSet walkers in `Aura::FindReferencesToUObject`.

- **FieldPathProperty drill-down + Find Refs** — Effort: **M** · Risk: **low**. Last
  remaining no-handler property type. Rare in shipping games (only Editor-derived
  classes) — genuinely low priority.

- **GWorld coverage** — Effort: **S each** · Risk: low. Two remaining titles:
  - **Star Wars Jedi: Survivor** (UE 4.27?) — untested; needs an AOB sweep + result triage.
  - **Satisfactory** (UE 5.3, modular DLL build) — (1) proxy DLL injection fails (loader
    bypasses normal proxy hooking; workaround = CE manual injection); (2) GWorld pattern
    likely lives in `CoreUObject-Win64-Shipping.dll` — adapt `Genau::FindAll` to scan
    multiple modules when the primary scan fails.

- **`kPublishers[]` table additions** — Effort: **S each** · Risk: **high** (if added
  casually — wrong publisher bias overrides correct detection). Only add a publisher with
  ≥3 misdetected titles AND a clear pattern. Wait for real misdetection reports.

- **UE 6.0 readiness — version-string map entry + remote-object watch (do only with a real UE6 binary)** —
  Effort: **S** · Risk: low. UE 6.0 is **layout-identical to UE 5.8** across every structure the dumper
  reads (verified `origin/5.8..origin/ue6-main`, 2026-06-30 — see [technical-notes.md](technical-notes.md));
  the core walk + AOBs are already UE6-ready, nothing to implement now. Two small, deferred items:
  (1) **Version-string map** — `Genau.cpp:2159` tops out at `{"5.8.",508}`; no `6.0.`→600 entry, so UE6
  games fall to the bias fallback (dynamic detection still works, so this is detection-clarity only).
  Adding `{"6.0.",600}` needs a `kVersionDetectLogicRev` bump (forces a one-time re-detect of all cached
  games) **and** care vs game-version strings like "6.0" (mirror the "15.6.0" guard at `Genau.cpp:2221`).
  (2) **UE6 AOBs** — add UE6-specific AOBs only against a real binary; our AOBs wildcard displacements and
  resolve the pointer, so the 5.8/6.0 reordered fields are handled post-resolve by the existing "UE5.8"
  preset. `StaticAllocateObject` gained a `UObject*` param (body changed) but the GObjects AOBs target the
  `mov reg,[rip+GUObjectArray]` sites, not that prologue. **Watch-item (far future, not shipping-default):**
  `UE_WITH_REMOTE_OBJECT_HANDLE` (experimental multi-server / UEFN remote objects, OFF in normal shipping)
  inserts `FRemoteObjectId` into `UObjectBase` (between `InternalIndex` and `ClassPrivate`) and `FUObjectItem`;
  if a UE6 game ships it ON, the hardcoded `OFF_UOBJECT_*` offsets shift by `sizeof(FRemoteObjectId)` and
  FUObjectItem packing is forced off — a real handler branch would then be needed.
  *Parent: UE6-vs-5.8 parity audit (2026-06-30); per-structure detail in technical-notes.md.*

-----

## Pending live-game verification (verify only — no code)

### 🔴 NEW 2026-08-05 — two defects the DumperTest sample found on its first real use

Both came out of the config-only A/B (**same source, Shipping vs Development**) that this file has
called the highest-value first cell since 2026-07-29. It produced them on day one.

**D1 — GNames resolves into `EOSSDK-Win64-Shipping.dll` on a Development package.** Effort **M** ·
Risk med. On the Shipping build of the *same source* everything resolves cleanly
(`validated=yes`, GWorld fine). On Development:

```
[GNames] GNAM_SF_2: 1 match(es), none validated
AOBScanAllModules: 2 matches in '...\Engine\Binaries\Win64\EOSSDK-Win64-Shipping.dll'
[GNames] GNAM_SAT425_3: 2 matches (multi-module), validated -> 0x7FFCEF5F8FC0
```

GObjects is at `0x7FF67517D5A0` (inside the game exe); GNames lands at `0x7FFCEF5F8FC0`, **a
different module entirely**. On a monolithic build that cannot be right. Every in-exe GNames
pattern missed — the tables are Shipping-tuned — and the multi-module fallback then matched a
data pattern inside a **third-party SDK DLL** whose pointer happens to reach a plausible name pool,
so `ValidateGNames` accepted it.

**The whole failure chain is downstream of this one address:**
`Cannot find Guid or Vector struct` → `validated=NO (DEFAULTS)` → the FField/FProperty offsets stay
at defaults that are wrong for this build (`Next=+0x18/Name=+0x20` vs Shipping's `+0x20/+0x28`) →
`GWorld does not deref to a UWorld — recovery failed` → **Start-from-GWorld and Value Search both
fail.** One misresolution, four visible symptoms.

*Multi-module is deliberate and must stay* — modular builds put GNames in `CoreUObject`, which is
why the winning pattern is named `GNAM_SAT425` (Satisfactory 4.25). The fix is not to remove it but
to **rank same-module-as-GObjects first, and refuse an unrelated third-party DLL** (`EOSSDK`,
redistributables) when GObjects resolved inside the main executable.

**D2 — Group Scan cannot see the object's own scalar UPROPERTYs.** Effort **M** · Risk med.
On the Shipping package (where the pointers ARE correct), a Group First Scan over
`DumperTestActor_0` matched only **container elements and base-class fields**:

```
PrimaryActorTick.TickInterval=0, CustomTimeDilation=1     <- AActor's own
Set_Int[0][0]=1337   Map_NameToInt.Value[0][0]=111   Arr_Int[0][0]=10
```

Not one of `I32`(1234567), `FrozenInt`(424242), `TickCount`, `Health.*`, `Opt_*` — all plain
scalars declared on the derived class, all of which the **single-value** scan finds without trouble
(`Opt_Int_Set` @0x468, `Set_Int` @0x358). Because the only leaves recorded are ones that never
change, a follow-up `Changed`/`Decreased` refine returns **0**, which is what made this look like a
Mode-B problem for three rounds.

**Not a leaf cap:** `Aura.cpp` `kLeafCap = 4096`; the actor has 121 fields.
**The sample is not at fault** — its on-screen heartbeat shows `frames=5971 TickCount=101` climbing
and `Health.CurrentValue` falling, so the values genuinely change.
**Sharpest repro, no timing involved:** Group First Scan, both slots `Exact` — `1234567` and
`424242`. Both are static UPROPERTYs on the same object.


> 🇹🇼 **繁體中文版：[pending-verification_zh-TW.md](pending-verification_zh-TW.md)** — a standalone
> translation of THIS section, reorganised by how much effort each check costs (seven of the ①
> items are free from any ordinary session). **This English section is canonical**: if the two
> disagree, this one is right, and edits land here first.
>
> **Procedure lives in [log-verification-checklist.md](log-verification-checklist.md)** — where to
> grep, which file each marker lands in, and which items need a deliberate in-game action versus
> which are free evidence from any ordinary session. THIS section is the status (⬜ / ✅); that one
> is the how. Two things worth knowing before you open a log: **there is no log level, nothing is
> filtered** (so `[DEBUG]` lines count), and **See-Through / Foreground-Lock evidence lands in
> `init-0.log`**, not `walk`/`pipe`, because their categories fall through `ResolveFile`.

### 🔎 Audit #4 items — split by HOW they can be verified

> **The rule, set 2026-08-04:** every audit-#4 fix is filed here classified into one of the two
> groups below **at the time it ships**. An item with no group is an item nobody can act on.
>
> **① Log-derivable** — provable by reading `%LOCALAPPDATA%\UE5CEDumper\Logs` after an ordinary
> session, or after one where a log line was *added for the purpose*. Prefer this: it needs no
> special skill and it leaves evidence. If an added line is heavy (per-object, per-tick), the commit
> that adds it must say so and mark it for removal once the item is ticked.
> Grep by **format string, never line number** — see
> [log-verification-checklist.md](log-verification-checklist.md).
>
> **② Manual-only** — needs a human at the keyboard doing something no log can cause (a click
> sequence, a specific game, a specific third-party install). Each of these carries its exact steps
> and the PASS/FAIL observation.
>
> **STATUS after five rounds of live testing (2026-08-04 → 08-05, builds 2622 → 2650):**
> **11 ✅ verified · 2 🟡 half (B8, Dump Explorer) · 14 ⬜ not yet exercised.**
> *(Dump Explorer's ⬜→🟡 came out of the "shipped but unproven" list below, not out of the 14 — an
> earlier revision of this line said 13 and was wrong. The 14 is the count of `- ⬜` bullets.)*
> Verified: B49, B31, B5(passive), B47, B35, B42, B36, **B34**, **B14+R5**, **B38**,
> the clean-scan report, and B8's main path.
>
> **The 2026-08-05 DQ7R pass moved three things and none of them were the three it aimed at:**
> the `Stop conn drain TIMEOUT` root cause fell out of a capture *already on disk* (see below —
> it needed no recurrence); **B47's earlier ✅ was found to be credited to a hand-injected session
> where the guard was not even compiled in**, and re-earned properly on that day's real proxy run;
> and **B28 was NOT tested** — the rows inspected were `StrProperty`, not FText. R8 was refuted
> outright by the maintainer (see [audit-2026-08-04-findings.md](audit-2026-08-04-findings.md)).
>
> **B14+R5 took three attempts, and the two failures are the most useful thing this audit
> produced.** Round 1: the guard was applied to an enumeration ("2 of 7 thread procs") that had
> counted wrong — a WER dump proved `std::terminate` on a thread no guard covered. Round 2: with
> guards on all ~15 entry points it crashed *again*, identically. That was the answer, not a
> setback — **there was never an exception.** `~std::thread()` on a joinable thread calls
> `std::terminate()` directly, and `UE5_Shutdown` is never called when a user closes a game, so
> every worker was still joinable at process exit. Fixed by making it a property of the TYPE
> (`Routine::SafeThread`) rather than a third list.
>
> **The lesson all of it shares, worth carrying into the remaining 14:** a fix verified against the
> *list* it was written from is not verified. B34 listed three CE filenames; B14 listed seven
> thread procs. Each was correct about every item on its list and wrong about the world. And when
> a fix does not take, re-read the EVIDENCE before adding more of the same fix — round 2 was
> effort spent on a mechanism that was never involved.
>
> ### ⚠ The three worth doing FIRST next session
>
> | # | Item | Why it leads |
> |---|---|---|
> | **1** | **B28** — CJK FText mojibake | The only open item that shows the user **wrong data**. Needs a CJK-language game; trigger is an even-length string containing a `U+xx00` char (一, 第…一, 統一). Counter-check STVoyager (UTF-8 FText) still reads correctly — that is the regression direction. |
> | **2** | **B4** — CE mailbox survives a dead UI client | Fails **silently**: lookups answer 0 while reporting `scanned=<full pool>`, which reads as "the object isn't there". A CE-only session stays broken for its whole life. |
> | **3** | ~~`Stop conn drain TIMEOUT`~~ | **DIAGNOSED + FIXED (build 2650). Verification is one grep — see below.** |
>
> The rest (B18, B19, B2, B25, B26, B13/B41 …) cannot produce wrong data or a crash, so they can wait.
>
> ### 🔍 `Stop conn drain TIMEOUT` — the invoke hypothesis is DEAD; do not "fix" it
>
> > **This entry briefly claimed the root cause was found. It was not, and the retraction is worth
> > more than the claim.** The reasoning was: `teleport_get_pose`/`teleport_get_pov` arrive at
> > 22:19:39.590/591, *"never answered"*, therefore the connections were inside a command. **The pipe
> > log has no response marker for ANY command** — 193 `Received`, zero `Sent` — so "no response
> > line" is not evidence of anything. 78 `teleport_get_pov` in that same file are equally
> > "unanswered" throughout a perfectly healthy session.
>
> **What the log DOES establish** (`pipe-20260804-221945.log`, build 2638):
> `Stop entry (conns=2)` → `cancels+wake done (0 ms)` → `conn drain TIMEOUT, 2 left (5000 ms)`.
> Two connection threads survived both `Tot::RequestShutdown()` and a `CancelIoEx` on every live
> connection handle (`Fern.cpp:481`, `:507-510`), then burned the full 5 s budget.
>
> **What reading the code eliminates — the invoke hypothesis, completely.**
> `UE5_Shutdown` (`Frieren.cpp:587`) calls **`Stark::Shutdown()` BEFORE `s_pipeServer.Stop()`**, and
> `Stark::Shutdown` drains the invoke queue setting every pending promise to `-7` (`Stark.cpp:328-340`).
> A pipe thread blocked in `EnqueueInvoke`'s `future.wait_for` is therefore **already released before
> `Stop()` is even entered** — the ordering exists for exactly this reason and the comment says so.
> So "make the Stark invoke wait observe `Tot::Requested()`" would be **a poll loop for a case that
> cannot occur on this path**. Considered and rejected 2026-08-05.
>
> > Rejected on its own merits too, for the record: honouring the full `Tot::Requested()` would let a
> > **latched `g_perCommand`** (set when one lane drops, cleared only on a fresh connect into an empty
> > registry) abort invokes on the *other* lane — manufacturing a new silent-failure bug of exactly
> > the B4 family. If it is ever wanted, it must key on `ShutdownRequested()` alone.
>
> **ANSWERED 2026-08-05 10:57 — the straggler line fired on the first proper repro, and it is the
> OTHER half.** Repro was exactly as filed (UI connected, untick the CE record):
>
> ```
> 10:57:00.157  Stop entry (conns=2)
> 10:57:00.157  Stop cancels+wake done (0 ms)
> 10:57:05.160  straggler: idle in ReadFile (the I/O cancel should have freed it), last cmd 'teleport_get_markers'
> 10:57:05.160  straggler: idle in ReadFile (the I/O cancel should have freed it), last cmd 'trigger_scan'
> 10:57:05.160  Stop conn drain TIMEOUT, 2 left (5002 ms)
> ```
>
> Both connections were **idle** (`inFlight == false`) — so nothing was stuck in a command, and the
> guess that started this whole thread was wrong in both directions. The cancel simply did not reach
> them.
>
> **Why a one-shot `CancelIoEx` misses.** `Fern::ReadLine` (`Fern.cpp:758-783`) reads **one byte per
> `ReadFile` call**, so a 40-byte command is 40 separate reads with 40 gaps between them. `Stop`
> fired `CancelIoEx` **once**, before the drain wait began. A thread sitting in a gap at that instant
> has no pending I/O to cancel (`ERROR_NOT_FOUND`) and then issues a **fresh** `ReadFile` that
> nothing will ever cancel — parked until the 5 s budget expires. With the Teleport panel polling
> twice a second on both lanes, landing in a gap is not a rare race: `Stop entry` came **146 ms**
> after the last command arrived.
>
> ### ✅ FIXED build 2650 — re-assert the cancel instead of firing it once
>
> `Fern::Stop` now slices its 5 s drain wait into `Grimoire::PIPE_STOP_CANCEL_REASSERT_MS` (100 ms)
> and re-issues `CancelIoEx` on every surviving connection each slice — the same *assert the state
> you want repeatedly* shape as the six re-assert workers, applied to teardown. Zero cost in the
> common case: with nothing left to drain the loop exits on its first wait with zero re-asserts.
> Safe under `m_connMutex` because a connection thread erases itself from `m_conns` **before**
> `CloseConnOnce` (`Fern.cpp:900-907`), so anything still in the registry has an open handle.
>
> A second line was added because the old log could say the threads were *"idle in ReadFile (the I/O
> cancel should have freed it)"* but **not whether the cancel had anything to free** — those are
> different bugs: `Stop cancel issued: N accepted, M had nothing pending`.
>
> ### ❌ That fix FAILED, and its own instrumentation said why — build 2651 has the real one
>
> Re-run 2026-08-05 12:55 (DumperTest, DLL build 2650), and the answer was in the line added for
> exactly this:
>
> ```
> Stop entry (conns=2)
> Stop cancel issued: 0 accepted, 2 had nothing pending
> straggler: idle in ReadFile ×2  (last cmd 'teleport_get_markers' / 'walk_world')
> Stop conn drain TIMEOUT, 2 left (5027 ms, 49 cancel re-asserts)
> ```
>
> **49 re-asserts, every one reporting nothing pending.** So it is not a missed window — my
> hypothesis is refuted by my own diagnostic. `CancelIoEx` cancels **asynchronous** requests; these
> pipe instances are created without `FILE_FLAG_OVERLAPPED`, so a thread parked in a blocking
> `ReadFile` has no pending IRP for it to find and it returns `ERROR_NOT_FOUND` every time, forever.
>
> **`CancelSynchronousIo` is the API for a synchronous operation blocking a known thread** — and it
> takes the **thread** handle, which only the serving thread can produce. Build 2651: each
> connection publishes a `DuplicateHandle` of its own thread, `Stop` calls `CancelSynchronousIo` on
> it alongside the (kept, harmless) `CancelIoEx`, and the handle is closed by the owner after it
> unregisters.
>
> **Same grep, same repro:** UI connected, untick the CE record → `grep "Stop conn drain"`.
> **PASS** = `satisfied, 0 left (… ms, N cancel re-asserts)`. **FAIL** = `TIMEOUT` again, which
> would mean the thread is not in `ReadFile` at all and the straggler line is wrong about it.
> ### ❌ 2651 FAILED TOO — stop guessing; build 2657 instruments instead
>
> Re-run 2026-08-05 13:25 on DLL build **2652** (which contains the CancelSynchronousIo fix, and
> no `could not duplicate serving-thread handle` warning, so the handles were published and the
> call was made):
>
> ```
> Stop cancel issued: 0 accepted, 2 had nothing pending
> straggler: idle in ReadFile x2   (last cmd 'teleport_get_markers' / 'refine_group_scan')
> Stop conn drain TIMEOUT, 2 left (5030 ms, 49 cancel re-asserts)
> ```
>
> **Three hypotheses, three refutations:** "stuck inside a command" (they are idle), "CancelIoEx
> missed the window" (49 re-asserts, all nothing-pending), "CancelSynchronousIo is the right API"
> (called, still timed out). Every one of them aimed at the same phrase — and that phrase is an
> **inference**. `inFlight` is set only around `DispatchCommand`, so a thread blocked in
> `WriteFile`, waiting on `writeMutex`, or **joining its watch threads in
> `StopWatchesForConnection`** is equally reported as "idle in ReadFile". A cancel does nothing for
> any of the latter.
>
> This is `feedback-fix-not-taking-reread-evidence` playing out verbatim: *when a fix does not take,
> re-read the evidence before adding more of the same fix.* Two were added.
>
> **Build 2657 replaces the label with an observation** — a per-connection `Phase`
> (Reading / Dispatching / Writing / StoppingWatches / Unregistering) stamped at every transition,
> reported with how long it has been there. `CancelIoEx` + `CancelSynchronousIo` are both kept:
> harmless, and correct for the case the phase may yet confirm.
>
> **Next run, same repro, one grep:** `grep "straggler" pipe-0.log`. It now names the real phase.
> `StoppingWatches` would mean the fix belongs in the watch-thread join, not in I/O cancellation at
> all — a different subsystem from the three already tried. ⬜
>
> *The re-assert loop is kept. It cost nothing (49 iterations of a failing syscall over 5 s) and it
> is what proved the diagnosis wrong quickly; a single shot would have looked like bad luck.*
>
> ⬜ does **not** mean "probably fine". It means nobody has looked. Most of the fourteen were
> simply not exercised (no wrapper installed, no UI killed mid-command, no Extra Scan).

#### ① Log-derivable

- ✅ **`Fern::Stop` no longer waits for a client that may never come** (build 2569, B49) —
  **VERIFIED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622.** The CE session hit the exact wedge condition, `Stop entry (conns=0)`,
  which is the case the old `CloseHandle` on a synchronous listen handle blocked on forever:
  `cancels+wake done (0 ms)` → `conn drain satisfied, 0 left (3 ms)` → `accept join done (3 ms)`
  → `monitor join done (58 ms)` → `Stopped`. 59 ms end to end against a PASS bar of ~100 ms.
  *Original instructions kept below for the next build.*
  **Already instrumented** — the fix shipped with per-phase logging precisely so this needs no
  special run. Play normally with the UI connected, then disconnect the UI and untick the CE record.
  Grep `pipe-0.log` for `PipeServer: Stop entry` and the phase lines that follow it.
  **PASS** = `PipeServer: Stopped` appears within ~100 ms of `Stop entry`, and `Stop conn drain`
  says `satisfied`. **FAIL** = no `Stopped` line at all (the old unbounded hang), or a phase line
  showing seconds. The old behaviour logged *only* `Stopped`, so the presence of `Stop entry` also
  confirms you are on the new build.

- ⬜ **CE-plugin double-inject guard rejects a foreign wrapper** (build 2577, B29) — *log half.*
  Any session where CE's plugin menu is used: grep `init-0.log` for
  `is loaded but is not ours`. That line only exists in the new code, and it fires for the exact
  case that used to be misread. **PASS** = the line names the foreign module and injection proceeds.
  (The manual half — actually installing a wrapper — is in ② below.)

- ✅ **UI log rolls at 8 MB instead of stopping** (build 2585, B31) — **VERIFIED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622.**
  `Logs\UE5DumpUI\` holds `pipe-0.log` at **8,388,756 bytes** (the 8 MiB cap) *and*
  `pipe-0_001.log` at 4,055,182 bytes with a **newer** mtime (21:05 vs 20:53). The roll happened
  and writing continued into the new file — the silent-stop signature would have been the 8 MB
  file alone with a stale last line. *Original instructions below.*
  Free from any long session:
  `ls %LOCALAPPDATA%\UE5CEDumper\Logs\UE5DumpUI\`. **PASS** = files named `pipe-0_001.log` (or
  similar) exist alongside `pipe-0.log` once a category passes 8 MB, and the newest file's last line
  is recent. **FAIL** = a single `pipe-0.log` sitting at exactly ~8 MB with a stale last line — that
  is the silent-stop signature. Fastest way to reach it: Teleport → Auto refresh, left running.

- ✅ **Leftover-proxy reports land inside the app folder** (build 2585, B38) — **VERIFIED
  2026-08-04 22:49, build 2643.** `leftover-proxies-20260804-224903.txt` was written to
  `%LOCALAPPDATA%\UE5CEDumper\Reports\`, and the old `%LOCALAPPDATA%\Reports\` still holds only
  the pre-fix file from 2026-07-30. Log line: `Leftover report written: …\UE5CEDumper\Reports\…`
  *Original instructions below.*
  **Previously not exercised**
  — no Report has been run since the fix. Checked 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622: `%LOCALAPPDATA%\Reports\` does
  hold `leftover-proxies-20260730-210903.txt`, but that is dated **2026-07-30**, i.e. before
  build 2585, so it is the documented pre-fix leftover and **not** evidence of failure.
  Run a proxy-cleanup Report. **PASS** = the file appears under `%LOCALAPPDATA%\UE5CEDumper\Reports\`. **FAIL** = it
  appears in `%LOCALAPPDATA%\Reports\`. (Files written before 2585 stay in the old place by design.)

- ✅ **A CLEAN scan still produces a report** (build 2637) — **VERIFIED 2026-08-04 22:49.**
  Raised by the maintainer: a scan that finds nothing must still leave an artifact, because
  "scanned everything and found nothing" and "never ran / looked in the wrong place / failed
  silently" are otherwise indistinguishable a week later. `BuildReport` had always handled the
  empty case; `CanWriteOrphanReport => Orphans.Count > 0` made that text unreachable and greyed
  the button out. Now gated on `OrphanScanRan`, and the empty report states the coverage:
  *"No leftover proxy DLLs were found. 67 folder(s) were examined."*

  > **~~Open UX question~~ — CLOSED 2026-08-05 by the maintainer: keep the current behaviour.**
  > *Find leftovers* shows its findings on screen; *Report…* writes the file. Writing a file stays
  > an explicit act. The discoverability half was already handled in build 2645 — the scan result
  > now names the button verbatim (*"press "Report…" to save this result as a file"*) and the clean
  > case states its coverage. **No auto-write. Do not re-open.**

- ✅ **The `UE5_Init` guard did not break ordinary init** (build 2592, B5) — *passive half* —
  **VERIFIED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622.** `Starting initialization...` and `Complete (UE…)` are one-for-one in
  all three games (DQ7R 5/5, Elliot 14/14, CE 1/1), and neither new line
  (`init already in progress`, `shutdown was requested during the scan`) appears anywhere.
  As stated below, that proves the guard is harmless, **not** that the race is fixed — the
  deliberate provocation is still open in ②. *Original instructions below.*
  *free from any session.* Grep `init-0.log` for `UE5_Init:`. **PASS** = `Starting initialization...` and
  `Complete (UE…)` alternate strictly one-for-one, and neither of the two new lines
  (`init already in progress`, `shutdown was requested during the scan`) appears. **FAIL** =
  a `Starting` with no matching `Complete` (the guard deadlocked — nothing should be able to cause
  this, which is why it is worth one grep per session), or two `Starting` lines in a row (still
  racing). Absence of the new lines proves only that the race did not *occur*; the deliberate
  provocation is in ② below.

- ✅ **Cheat Engine is never scanned as if it were the game** (build 2603, B34) — **VERIFIED
  build 2633**: `host process is 'cheatengine-x86_64-SSE4-AVX2.exe' — Cheat Engine is never a
  scan target`, and `scan-0.log` stayed at 121 bytes (header only) where the failing run left
  1.3 MB. *Earlier:* **FAILED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622, REFIXED build 2628, needs a re-test.**
  The capture shows `process: …\cheatengine-x86_64-SSE4-AVX2.exe` followed by
  `DllMain AutoStart: game process — calling UE5_AutoStart` — a 5.8 s AOB scan and the pipe
  opened **inside CE** (1.3 MB `scan-0.log` in that folder). Cause: the guard was an exact-name
  list and CE's real executable is the `-SSE4-AVX2` CPU-feature variant, which matched none of
  the three names. `g_isCEPlugin=0` too — the DLL was hand-injected, so the
  `CEPlugin_GetVersion` half could not help either. Now
  `Grimoire::IsCheatEngineExeName`, a case-insensitive **prefix** on the `cheatengine` stem
  (anchored at the start, so `MyCheatEngineClone.exe` is still allowed).
  **Re-test:** inject the DLL into CE by hand again. **PASS** = `host process is '…' — Cheat
  Engine is never a scan target` and **no** `scan-0.log` growth in that folder.
  Free from any
  session where the CE plugin is registered: grep `init-0.log` for `DllMain AutoStart:`.
  **PASS** = when the host is CE, either `CE plugin host — skipping auto-start` (the normal path,
  now reached because `CEPlugin_GetVersion` claims identity) or the new
  `host process is '…' — Cheat Engine is never a scan target`. **FAIL** = `game process — calling
  UE5_AutoStart` with `cheatengine-x86_64.exe` on the `UE5Dumper DLL loaded | … | process:` line
  two lines above. To provoke the original race: register the plugin but leave it **unticked**,
  then start CE.

- ⬜ **Extra Scan can be cancelled** (build 2603, B18). Needs a game where GObjects does NOT
  resolve by AOB, so Extra Scan actually runs long. Start it, then untick the CE record (or close
  the UI) while it is still going. **PASS** = `pipe-0.log` shows `PipeServer: Stop watches+scan
  joins done` within a second or so of `Stop entry`. **FAIL** = seconds of gap, or CE's window
  frozen until the sweep finishes — that is the unbounded join, and `UE5_Shutdown` runs on CE's own
  thread, which is why it freezes CE rather than just the game.

- ⬜ **Log retention no longer dies at the first undeletable file** (build 2603, B19). Provoke it:
  open any archived `%LOCALAPPDATA%\UE5CEDumper\Logs\<proc>\*.log` in a program that holds it open,
  and make sure at least one OTHER archive in the same folder is older than 21 days (backdate it).
  Start a game with the DLL. **PASS** = the backdated file is gone and the held one remains.
  **FAIL** = both remain — the sweep aborted at the held file, which it did on every launch because
  enumeration order is stable.

- ✅ **The proxy dedup guard says when it is not armed** (build 2603, B47) — **VERIFIED 2026-08-05,
  build 2645 — and the 2026-08-04 ✅ was credited to the WRONG SESSION.**
  > **The correction, because it is the same trap as B34 and B14.** The 08-04 note said *"DQ7R ran
  > through `version.dll` (a real proxy session, so the guard is compiled in)"*. It did not. That
  > line is inside `#ifdef UE5_PROXY_BUILD` (`Heiter.cpp:262-270`), and **not one 08-04 DQ7R session
  > logged `DllMain ProxyStart` or `Loaded real version.dll`** — every one was hand-injected, so the
  > guard was not in the loaded binary at all. Its absence proved nothing. *An absence is only
  > evidence once you have shown the producing code was present and running.*
  >
  > **The real evidence is the 2026-08-05 10:29:30 run**, which IS a proxy session —
  > `DllMain ProxyStart: proxy DLL mode — starting pipe server only (no scan)` →
  > `Loaded real version.dll: C:\WINDOWS\system32\version.dll` — and
  > `first-loaded-wins guard is NOT armed` is absent there. `Local\…_<PID>` succeeded where `Global\`
  > needed a privilege the game does not have. PASS, for the right reason this time.
  *Original instructions below.* Any proxy session:
  grep `init-0.log` for `first-loaded-wins guard is NOT armed`. **PASS** = the line is ABSENT
  (`Local\` + PID succeeds where `Global\` needed a privilege the game does not have). Its presence
  is not a failure of this fix — it is the fix reporting a condition that used to be silent — but
  it is worth investigating if it appears.

- ✅ **The PERF split no longer measures its own probe** (build 2610, B35) — **VERIFIED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622.**
  *This item had no verification entry when it shipped — a gap in the filing, found while
  sweeping these logs.* `grep 'PERF Snapshot capture'` gives
  `wall 5,256.2 ms … split dll 2,733.5 / ipc 692.4 / ui 1,830.3 ms`. The three parts sum to the
  wall time exactly, transport (dll+ipc = 3,425.9) is **less** than wall, and `ui` is a large
  non-zero. The pre-fix signature was the opposite: transport **exceeded** wall, so `ui` clamped
  to 0 and `ipc` absorbed the probe's own 93–125 ms round-trip. These are the numbers
  [multipipe-eval.md](multipipe-eval.md) reasons from.

- ✅ **CJK FText no longer renders as ASCII mojibake** (build 2599, B28) — **VERIFIED 2026-08-05 on
  the DumperTest sample, Shipping package, DLL build 2650.** All **eight** FText fields render as
  CJK in Live Walker, and every control holds:

  | field | rendered | role |
  |---|---|---|
  | `Text_Even2_OneNull` 統一 · `Text_Even2_TwoNull` 一言 · `Text_Even4_TwoNull` 統一言語 | correct | the trigger cases (even length, U+xx00) |
  | `Text_Odd3_OneNull` · `Text_Even6_NoNull` 日本語テスト | correct | length/parity controls |
  | `Text_Ascii` `DumperTest FText ASCII` | correct | **the other-direction control** — a fix that swung to always-UTF-16 would have broken this |
  | `Text_Localized` 統一言語 | correct | different `FTextHistory`, agrees with `Text_Even4_TwoNull` ⇒ the fault was never history traversal |
  | `Str_*` ×4, `Name_Cjk` | correct | FString + FNamePool paths, unaffected as expected |

  **This closes the one open item that could show the user WRONG DATA.** The counter-check on
  STVoyager's UTF-8 FText is a separate, licensee-specific case and stays open.

  > **Two observations from the same screen, neither of them B28:**
  > 1. `Text_Empty` renders as **`No`**. An `FText::GetEmpty()` should read as empty; `No` looks
  >    like a truncated `None` or a mis-typed render. Cheap to chase, cosmetic, but it is the empty
  >    display-string path and nothing else covers it. **NEW, unfiled.**
  > 2. The package under test was built from a **stale** `DumperTestActor.cpp` (退一步 where the
  >    repo had 走一步), so the odd-length control was not the documented one. It renders correctly
  >    either way, so B28's result stands — but see the identity-record note below; this is exactly
  >    what `capture_package_identity.py --project` now detects.

  *Original instructions below.*
  > **❌ NOT tested by the 2026-08-05 DQ7R pass, and the near-miss is worth recording so the next
  > attempt does not repeat it.** The rows inspected (`Name` / `DisplayName` / `ListName` = 忘名)
  > are **`StrProperty`** — FString, which goes through the UTF-16-only reader and **never had this
  > bug**. B28 lives in `ReadFTextString` alone. The hex confirms the FString path is fine and says
  > nothing about B28: `D8 5F | 0D 54 | 00 00 | 6F 00 | 78 00 | 00 00` = 忘(U+5FD8) 名(U+540D) NUL
  > 'o' 'x' NUL, `ArrayNum=6`, i.e. the game stores a fixed 6-TCHAR field with an **embedded NUL at
  > index 2**; the reader stops at the NUL and renders 忘名 — correct. Second miss: neither 忘
  > (U+5FD8) nor 名 (U+540D) has a **low byte of 0x00**, so this string could not have tripped the
  > trigger even as an FText.
  >
  > **What to do instead:** find a row whose Type column literally reads **`TextProperty`**. DQ7R's
  > 2026-08-05 walk logs contain **zero FText field reads** (the only `TextProperty` hits are the
  > class names `TextPropertyTestObject` / the `TextProperty` meta-class), so one has to be hunted:
  > Property Search for a TextProperty on a UI/dialogue/item-description class. Trigger characters
  > whose low byte IS 0x00, all common in JP/CN: **一** U+4E00 · **最** U+6700 · **言** U+8A00 ·
  > **退** U+9000 · **紀** U+7D00 — and the string must be an **even** number of characters.
  Affects **FText-typed values only** (`ReadFTextString`); FString goes
  through the UTF-16-only reader and never had the bug. **To test:** any game with Chinese/Japanese
  UI text — set the game to a CJK language, find an FText property in Live Walker or Property
  Search. **PASS** = the value reads as CJK. **FAIL** = short ASCII punctuation soup (`,{1`, `-N?e`)
  where CJK belongs. Worth checking specifically on a string with an **even** character count
  containing a `U+xx00` character (一, 第…一, 統一) — that is the exact trigger. Counter-check that
  the fix did not swing the other way: **Star Trek Voyager (UE5.6)** stores its FText as UTF-8, and
  its Chinese must still read correctly.

- 🟡 **Fly/Noclip no longer leaves the pawn ghosted** (build 2596, B8) — **MAIN PATH VERIFIED**
  > **⚠ READ THIS BEFORE RE-TESTING — the deferred half is NOT reachable by closing the game.**
  > Closing a game never calls Fly's disable at all: `UE5_Shutdown` does not run on game close
  > (proven — zero `UE5_Shutdown: Cleaning up` lines in any session), so `Dunste::SetEnabled(false)`
  > never executes and `DISABLED but the pawn's collision is still OFF` can never be printed.
  > Confirmed in the 22:33 Elliot run: Fly was ON, the game was closed, and there is **no
  > `Fly: DISABLED` line at all**. That run is a B14 test, not a B8 test.
  >
  > The deferred half needs the **Disable button clicked while the game thread is quiet**. The
  > 22:01 Elliot run did click Disable — and `SetActorEnableCollision(1) invoked` proves the game
  > thread was still ticking, so Elliot does not appear to idle when unfocused. Alt-tab duration is
  > not the variable; whether the title honours `t.IdleWhenNotForeground` is.
  >
  > **So this needs a game that actually goes quiet when backgrounded.** If none is to hand it is
  > reasonable to close it as accepted-unverified: the code path is the same one Schlacht has been
  > running in production since build 2364, and the main path is verified.
  (Elliot, 2026-08-04, noclip ON). The log shows the fixed ordering exactly:
  `Fly: worker stopped` → `Fly: SetActorEnableCollision(1) invoked` → `Fly: DISABLED`. Join
  before restore, and the restore is committed from the invoke *actually running*. **The
  DEFERRED path is still ⬜** — the game thread stayed responsive, so
  `DISABLED but the pawn's collision is still OFF` was never reached. To finish it, alt-tab
  away for >500 ms before clicking Disable on a title that idles when unfocused.
  *Original instructions:* The whole answer is in the
  log, and the trigger is the *ordinary* way to turn Fly off on an idle-when-unfocused title.
  **To test:** Teleport tab → Fly ON + Noclip → fly through a wall → **alt-tab to the UI** (wait
  >500 ms so ProcessEvent goes quiet) → click Disable. Grep `init-0.log` for `Fly:`.
  **PASS** = `Fly: DISABLED but the pawn's collision is still OFF (game thread unresponsive)`,
  then — after you click back into the game — `Fly: game thread resumed after N ms — pawn collision
  restored`. **FAIL** = the old shape: a plain `Fly: DISABLED` and nothing else, after which the
  pawn falls through the world. Corroborate in-game: walk into a wall, it should stop you.
  Second, cheaper check on any Fly session: `Fly: collision disable deferred` may appear, but it
  must not repeat — it is rate-limited to once per stall.

- ⬜ **`WalkClassEx` memo — the win is already instrumented** (build 2596, B10). **Blocked on a
  BASELINE**, not on instrumentation: the retained logs hold exactly one
  `PERF Snapshot capture` line (`wall 5,256.2 ms`, 2026-08-04, post-fix), so there is nothing
  pre-2596 to compare it against. Either keep this number as the new baseline and compare the
  next capture of the SAME snapshot on the same game, or settle the correctness half alone
  (struct types / enum names / bool masks still populate). Snapshot capture is
  wrapped in a `DiagnosticsProbe`, so no new logging is needed: grep `pipe-0.log` for
  `PERF Snapshot capture`. **PASS** = `wall … ms` is materially lower than the same capture on a
  pre-2596 build (the memo removes a 100–300 × `FieldInfo` deep copy per struct-array *element*),
  and correctness is unchanged — property grids still show struct types, enum names and bool masks,
  which are exactly the fields `WalkClassEx` adds on top of `WalkClass`. **FAIL** = those columns go
  blank (the memo would be serving a pre-enrichment entry), or a crash under a parallel scan (a
  handed-out reference being invalidated — the reason `try_emplace` landed first).

- ⬜ **CE mailbox survives a dead UI client** (build 2592, B4). The evidence line is **cold** — once
  per latch, so it costs nothing to leave in. Needs a deliberate sequence but the whole answer is in
  the log, so it lives here: connect the UI, start something long (Property Search deep, or a full
  Instance Finder scan), **kill the UI process while it runs**, then use any CE-side lookup — the
  `.CT`'s Find Instance, or a teleport/GodMode hotkey on a game that resolves through the class-scan
  fallback. Grep `pipe-0.log` for `per-command cancel is latched`.
  **PASS** = that WARN appears **and** the command that follows it reports a non-zero result count.
  **FAIL** = the old signature: no WARN, and a lookup answering `0` with `scanned=<full pool>` —
  the message that made this bug read like "the object isn't there".

#### ② Manual-only

- ⬜ **Symbol-export GWorld no longer claims to have an AOB** (build 2581, audit #4 B2). The gate is
  unit-tested against the shipped pattern tables, but needs a game whose GWorld actually resolves
  through a symbol export — **Satisfactory** (`?GWorld@@3VUWorldProxy@@A`, see
  [test-games.md](test-games.md)). **To test:** scan, then look at the CE-export / Standalone-Trainer
  AOB toggle. PASS = the toggle is greyed out (no AOB offered) and the exported table's addresses
  resolve normally through the non-AOB path. FAIL = the toggle is enabled and every address in the
  exported table shows `??`. Nothing to check on a normal RIP-pattern game — the toggle behaves as
  before there, which is the point.

- ⬜ **CE-plugin double-inject guard — the third-party-wrapper case** (build 2577, audit #4 B29).
  Ownership is now decided by PE ProductName, not file name. **Verified on real files here** (our 5
  binaries say `UE5CEDumper`; the 4 System32 counterparts say `Microsoft® …`), but the case that
  motivated the fix has no test material on this machine. **To test:** install ReShade (or drop any
  third-party `dxgi.dll`/`dinput8.dll` wrapper) into a UE game folder, attach CE, click
  *UE5CEDumper: Inject && Connect*. PASS = it injects normally, and the DLL log carries
  `'dxgi.dll' is loaded but is not ours`. FAIL = the old *"already loaded … no injection needed"*
  message, after which the UI cannot connect. Also worth eyeballing there: a game path with
  non-ASCII characters must now appear intact in that message (it used to render as `EVERSPACE? 2`).

- ⬜ **Recycle-Bin refusal on a volume with no bin** (build 2621, B13/B41). Needs a volume whose
  Recycle Bin is off: pick a spare fixed volume, `Recycle Bin Properties → Don't move files to the
  Recycle Bin`, and put a leftover proxy on it. Run the orphan scan. **PASS** = the row is refused
  with *"This volume has no working Recycle Bin … a delete here would be PERMANENT"*, and the
  confirm dialog never offers to recycle it. **FAIL** = the row is offered and the file vanishes
  permanently while the status says "moved to the Recycle Bin". Re-enable the bin afterwards and
  confirm the same row becomes actionable again — that half proves the probe isn't just refusing
  everything.

- ⬜ **The pre-4.11 refusal no longer fires on one PE field** (build 2621, B25). Provoke it with the
  UE-version override, or with any game whose PE ProductVersion reports a 4.0–4.10 major/minor.
  Grep `scan-0.log` for `below the … floor — NOT accepting that on its own`. **PASS** = that line
  appears and the scan **runs anyway** (tier 3 → low confidence → the gate does not arm). **FAIL** =
  `SKIPPING the scan` on a game that works. Also confirm the *other* direction still works: a
  genuinely pre-UE4 (UE3) binary must still be refused, via the marker path — grep
  `PRE-UE4 engine POSITIVELY identified`.

- ⬜ **Duplicate GameEngine records no longer break each other** (build 2621, B26). Teleport →
  Global Pointers → *Get GameEngine*, then click it again. **PASS** = the second click says it was
  *already pushed this session* and copies XML instead of adding a record. Then paste that XML to
  deliberately create a second record, tick BOTH, and untick the OLDER one. **PASS** = the newer
  record's `UE_GameEngine` still resolves and its chain still reads (set `UE5_DEBUG=1` to see
  *"another record owns UE_GameEngine now — leaving it alone"*). **FAIL** = the newer record's
  addresses go to `??`.

- ⬜ **The five dead coord-grid sort headers** (build 2610, B16). Teleport → Coordinate Library with
  ≥3 rows. Click the **X**, **Y**, **Z**, **Yaw** and **Dist** headers. **PASS** = rows reorder on
  every one. **FAIL** = the header glyph animates and nothing moves. Must be checked on a
  **published (AOT/trimmed)** build — the whole defect is trimmed-away reflection metadata, so a
  plain `dotnet run` will not reproduce it. Label / Group / Map worked before and must still work.

- ✅ **Second launch raises the first window** (build 2610, B42) — **VERIFIED 2026-08-04 (maintainer).** Run `dist\UE5DumpUI.exe`, then run
  it again (double-click the exe, or the shortcut). **PASS** = the existing window comes to the
  front — including when it was minimized — and no second window appears. **FAIL** = nothing
  visibly happens, which is the old behaviour. Worth testing with the first instance **connected to
  a game**, since the window title carries the module name and a title-based search would miss
  exactly then.

- ✅ **Force submenu with nothing selected** (build 2610, B36) — **VERIFIED 2026-08-04 (maintainer).** Property Search → run a search →
  **right-click empty space below the rows**, or a row you have not left-clicked. **PASS** = no
  Force submenu. Left-click a BoolProperty row, right-click it: only Force ON / OFF. FAIL = all
  four actions at once. (Needs the Experimental toggle on for the submenu to exist at all.)

- ✅ **Close the game with a hold worker live** (build 2596, B14 + R5) — **VERIFIED build 2638**
  (DQ7R, bullet-time + See-through ON, closed from the game's own window: no event-log entry, no
  dump). Took THREE attempts and the first two failures are the whole lesson — see below.
  *Earlier:* **FAILED 2026-08-04 session logs (DQ7R / Elliot / CE), build 2622, SCOPE CORRECTED build 2628, needs a re-test.**
  DQ7R crashed at 21:05:06 on build 2622 (every fix present). The WER dump
  (`%LOCALAPPDATA%\CrashDumps\DQ7R-Win64-Shipping.exe.55564.dmp`) gives
  `0xC0000409` with **param[0] = 7 = FAST_FAIL_FATAL_APP_EXIT** — `abort()`/`std::terminate` —
  and the whole faulting stack inside `version.dll` + the CRT. **No `tick threw` line anywhere**,
  so no guard was even reached. Context: `pipe-0.log`'s last line is a `FindInstancesByClass`
  reporting `nonNull=35109` where the call 0.3 s earlier said `154964` — the game was freeing its
  object pool while we walked it.
  **The fix was right; its SCOPE was wrong.** The finding said "2 of 7 thread procs"; the DLL has
  ~15 places where a throw is fatal. Build 2628 adds `Routine::RunThreadGuarded` to all of them,
  the important one being `Stark::HookedProcessEvent` — it runs on the **game's own thread**,
  entered from game code with no handler for us, and allocates twice.
  **Re-test:** same steps below. **PASS** = no event-log entry. If it fires again, `init-0.log`
  now carries `UNCAUGHT exception … contained` naming the thread — that is what routing every
  entry point through one helper buys.
  *Note: the Elliot crash in the same event log is build **2567**, before B14 shipped — that one
  is the original bug, not a regression.*
  This is the exact repro that
  produced the live `0xC0000409` in build 2389, re-run against the loops that were still unguarded.
  **To test:** enable **two** holds whose workers were previously bare — Time Dilation (Hemmung) and
  Move Speed (Laufen) — plus See-through, then **disable See-through while the game is backgrounded**
  so its `PendingRestoreLoop` is actually waiting, and close the game from its own window.
  **PASS** = no crash, no WER minidump, nothing in the Windows Application event log. **FAIL** =
  exit code `0xc0000409` with a fault on a `version.dll` stack — that is an exception escaping a
  thread entry. If `init-0.log` carries `tick threw (…) — skipping (game tearing down?)`, the guard
  fired and did its job; its absence proves only that nothing threw this time.
  *Why it can't be tested here: the throw comes from reading a UFunction in a process that is
  actively freeing it — there is no way to stage that outside a real game shutdown.*

- ⬜ **Provoke the concurrent `UE5_Init`** (build 2592, B5) — the active half of the passive check in
  ① above. Needs the **proxy** launch path, because that is what makes the second caller reachable:
  the proxy starts the pipe *without* scanning, so both cached pointers are 0 while the pipe is
  already live. **To test:** launch the game with a deployed proxy DLL, connect the UI, click Scan,
  and **while the scan is still running** trigger any CE-side mailbox command (tick the `.CT`, or a
  teleport hotkey) — that path calls `Mimic::EnsureInitialized`, which is the second `UE5_Init`.
  **PASS** = `init-0.log` shows `init already in progress on another thread — tid=… is waiting`
  followed by `resumed after waiting (first caller succeeded — returning its result, no second
  scan)`, exactly **one** `Starting initialization...`, and the CE command then works normally.
  **FAIL** = two `Starting` lines, or a `validated=yes` summary on a session where drill-down shows
  every property type unknown — that is the silent-corruption shape this fix exists to prevent.
  *Why it can't be tested here: it needs two real threads racing a multi-second scan inside a live
  game; the unit tests can only pin the flag semantics, not the timing.*

- ⬜ **`.CT` DLL discovery — the `reg.exe` recent-files fallback** (build 2576). The breadcrumb half
  is **✅ verified** (run `UE5DumpUI.exe` once, open the `.CT` from CE's recent-files menu, tick
  `init` → the DLL resolves). The registry half has NOT been exercised: it only runs when every
  cheap slot misses. **To test:** delete `%LOCALAPPDATA%\UE5CEDumper\dll-path.txt`, open the `.CT`
  from recent files, tick `init`. PASS = a brief console flash, the DLL resolves, the slot report
  (set `UE5_DEBUG=1`) credits *"folder of the most recent UE5CEDumper.CT in CE's recent-files
  list"*, **and `dll-path.txt` is recreated** so a second tick does not flash again. FAIL = still
  not found, or it flashes every time (the self-heal write did not happen).
  *Why it can't be tested here: it is CE Lua, and `CtDllDiscoveryTests` can only pin structure.*

- **Flaky: `SnapshotViewModelTests.GroupMatch_MissingValue_ShowsErrorNoCandidates`** — failed ONCE
  in a full parallel run on 2026-07-23 (build 2318), then passed 25/25 three times in isolation and
  green on an immediate full re-run. Unrelated to the winmm/proxy work that was in flight. This test
  class has prior form for snapshot-DB concurrency flakes (see `feedback-ci-only-test-flakes`, and
  PR #451's concurrent-first-open fix), so the likeliest cause is another store-level race under
  parallel load rather than the assertion itself. **Not chased** — one observation is not a
  reproduction. If it recurs, capture whether `GroupCandidates` was non-empty or `GroupStatusText`
  empty, since those point at different halves. Effort **S** once reproducible.


Shipped + unit-tests-pass but unproven on real games:

- **Dump Explorer cross-game identity gate** (build 2538+; UI/C#-only, no DLL or pipe change).
  The live match joins on bare class NAMES, and every UE title has `Object` / `Actor` / `Pawn` /
  `PlayerController`, so loading game A's `.jsonl` against game B did not fail — it "succeeded",
  marked those rows **in current game**, and Jump opened B's object under A's label. Now two-tier:
  different `module` → refuse and name both sides; same module + different `pe_hash` → still match
  (a pre-patch dump of this game is the normal use) but say "Different build — offsets may have
  moved"; missing `pe_hash` → match but never claim identity was checked. Identity is read at match
  time via `GetPointersAsync`, deliberately NOT fanned into the VM — `SetConnected(true)` can fire
  before an `EngineState` exists, and that window is the wrong-game bleed in C2 above.
  **What offline already settled — do not spend live time on it:** all four arms plus the
  probe-throws path (`DumpExplorerTests` ×5, both directions), and the refusal was verified to FAIL
  when the module comparison is neutered.
  **What ONLY a real game can prove:** that `EngineState.ModuleName` and the dump's `meta.module`
  actually agree on the SAME game — they come from different producers (live DLL vs
  `DumpAllService` at export time), and if one carries a path or different casing the gate would
  refuse a legitimate same-game match. Acceptance: (1) export a Dump All from game X, keep X
  connected, Re-check → matches with NO caveat; (2) load that file with game Y connected → refused,
  status names X and Y, every row unmatched, Jump offers nothing; (3) load an OLD dump of X after
  an X patch → matches WITH the "Different build" caveat. Case (1) is the regression risk — a false
  refusal there breaks the feature for its main use. **No log marker** for the pass; the refusal
  logs `DumpExplorer live match refused: dump module '…' != live module '…'`.
  🟡 **Case (1) has evidence (2026-08-05, DQ7R).** The maintainer loaded a **different session's dump
  of the same game** and it matched; `DumpExplorer live match refused` appears **zero** times across
  every DQ7R log. That is the regression risk retired — `EngineState.ModuleName` and the dump's
  `meta.module` do agree on the same game despite coming from different producers. **Cases (2) and
  (3) are still ⬜**: (2) load that dump with a *different* game connected → must refuse and name both
  sides; (3) load a pre-patch dump of the same game → must match **with** the "Different build" caveat.
  Note (3) needs an actual DQ7R patch to come along, so it is opportunistic, not schedulable.

- **Solide pool-truncation badge — `⚠ capped` / "cap reached, more exist unheld"** (build 2531+;
  DLL `Solide`/`Fern` + Property Search + Teleport Stealth card). `Aura` already computed
  `rset.truncated` and `Solide` was dropping it, so "0 live instances matched" and "matched more
  than `SOLIDE_MAX_INSTANCES`=256 and discarded the rest" were indistinguishable. Now plumbed to
  both `force_field` and `get_forced_fields`, and the Stealth card **withdraws** its
  "you are minimal to detection" claim when the pool was capped (that claim is false for every
  instance past the cap).
  **What offline already settled — do not spend live time on it:** the wire parse both ways incl.
  the older-DLL missing-key default (`SolideTruncationWireTests`, 4 tests), both VM messages in
  both directions (`PropertySearchForceTests` ×3, `TeleportViewModelTests` ×1), and the prune-guard
  swap being an exact no-op (`!rset.truncated` ≡ the old size test on this path, since
  `FindInstancesByClass` is called with the default `buildHistogram=false`). All 8 were verified to
  FAIL when the implementation is reverted — three separate negative controls.
  **What ONLY a real game can prove:** that the flag ever fires. It needs a class with **>256 live
  instances** where a Force hold is meaningful — projectiles, crowd NPCs, destructible props are the
  likely candidates; most gameplay classes never reach the cap, which is exactly why this went
  unnoticed. Acceptance: hold a field on such a class → the strip row shows `⚠ capped` next to
  `(256 held)` and the status line ends "cap reached, more exist unheld"; hold on a small class →
  neither appears. **No grep-able log marker** — the DLL logs nothing on truncation; the evidence is
  the badge and the status text. Secondary check: with the pool capped, `RemoveForce` must still
  restore cleanly (the base-prune guard is skipped while truncated — L4), so verify no field is left
  stuck at the forced value after Reset.
  ⬜ unverified.

- **Copy CE Field drills object-pointer arrays — leaf + GWorld-path spine + dup-crumb dedup — DONE +
  MERGED (PR #323, builds 1364-1379).** LEAF (`SpawnedAttributes[2]` → `CharacterAttributeSet` →
  `HealthPoint`), SPINE 2b (`PathStepToBreadcrumbs` splits a Locate-in-GWorld `PlayerArray[0]` hop into
  container + element), and DEDUP 2c (`DedupeConsecutiveBreadcrumbs` collapses a redundant consecutive
  container crumb in `ExportCeFieldXmlAsync` + `CleanBreadcrumbs`) all **LIVE-VERIFIED on Elliot AND the
  deeply-nested Gundam SEED chain** (nested + Collapse-chain). Unit-tested
  (`...ObjectArray_WithResolvedElement_DrillsElementGroup`, `...PathThroughObjectArrayElement_EmitsElementDerefNode`,
  `DedupeConsecutiveBreadcrumbs_*`, `..._DeepDistinctChain_Unchanged`). **(b) DONE + LIVE-VERIFIED
  (builds 1380-1388) — Back-nav onto a path-synthetic container crumb now re-hydrates the array element view.** The
  crumb's `ContainerField` is null (the `GWorldPathStep` carries no `ArrayDataAddr`/`ArrayCount`/element
  list), so Back-nav fell through to a parent re-walk and rendered the PARENT object grid (a silent
  mis-render — NOT a literal duplicate; the 2c dedup already covers the export-time crumb). "Give it a
  `ContainerField`" is infeasible (path step lacks the data) → `TryRepopulateSyntheticContainerAsync`
  LAZILY re-walks the parent + matches the field by name+offset + `RepopulateContainerView`, wired into
  all 4 re-display sites (NavigateToBreadcrumb, GoBack normal + pre-bookmark restore, LoadBookmark) +
  `RefreshAsync`'s container gate broadened. 7 new tests; C# 1648/0, AOT 46.5 MB. **(a) DONE +
  LIVE-VERIFIED (builds 1389-1390) — Map/Set (and interface-array) element hops in a GWorld-path spine
  now split into container + element crumbs.** The DLL `emit()` lambda was widened 6→8 args to thread `elemStride`
  (Map `pairStride` / Set `elemStride` / interface-array 16) + `elemValueOffset` (Map value's within-pair
  offset; 0 for set/key/interface) through `GraphEdge`/`GraphPathStep` → Fern `elem_stride`/`elem_value_offset`
  → C# `GWorldPathStep` → `PathStepToBreadcrumbs` (element crumb offset = `ElementIndex*stride + valueOffset`;
  container crumb strips the `.Key`/`.Value` suffix so Back-nav re-hydration matches). All emit callers
  updated (`GetRelatedObjects`/`AppendOwnedSubObjectLeaves`/test mock); object/class arrays keep the
  hardcoded-8 path. 6 new tests (5 C# + 1 dll round-trip); C++ 697/0, C# 1653/0, AOT 46.5 MB. Adversarial
  review confirmed Map/Set/Set offsets correct + reachable; accepted nits: struct-nested dotted base name
  doesn't re-hydrate (pre-existing, affects arrays too, CE math still correct) + int32 element-offset
  arithmetic (theoretical, `FieldOffset` is int by design).
- **Genau RIP decode: `Macht::IsRipRelativeModRM` (mod=00 half restored at 3 of 5 sites)**
  (build 2544+; DLL only). Three hand-rolled decode loops tested `(b & 0x07) == 0x05` and
  omitted the `mod == 00` half, so `mov rcx,[rbp-8]` / `lea rax,[rbp+0x20]` / `mov rax,rbp`
  were decoded as RIP-relative and the int32 read at `instr+3` was a disp8 plus the next
  instruction's bytes. All five sites now share one named predicate.
  **What offline already settled — do not spend live time on it:** the predicate itself
  (13 assertions incl. an exhaustive "exactly 8 of 256 ModR/M bytes qualify", verified to
  FAIL — 6 reds — when reverted to the r/m-only form). Also settled: this is **NOT** a
  wrong-answer bug at `ScanFunctionBodyForRipRef`, whose every caller is a GNames path gated
  by `ValidateGNamesAny` (it must decode the literal string `"None"` through a two-level
  pointer chain). Treat it as a correctness + scan-cost cleanup, not a fix.
  **What ONLY a real game can prove, and `sweep.sh` CANNOT:** `scan_patterns.java:137` skips
  every `Symbol*`/`CallFollow` signature (`GROUND-TRUTH.md` says so), and the two data scans
  are runtime-only and absent from the pattern harness — **a clean sweep diff here would mean
  "not measured", not "no regression".** The only evidence is the DLL's own scan log, same
  game, before vs after: the candidate/probe counts should go DOWN while **every resolved
  GObjects / GNames / GWorld address stays byte-identical**. The second half is the real
  acceptance criterion; a changed address is a regression, a lower count is the win.
  Passive — needs no special in-game action, just one injection each side. ⬜ unverified.

- **Audit #3 DLL fixes — M1–M5 + the DLL/Solide LOWs** ([audit-2026-07-14-findings.md](audit-2026-07-14-findings.md)).
  Shipped on `dev` (`408fd2d`, `7f3898f`, `3362636`); this section is their SINGLE owner — the audit
  doc and the Audit-#3 block above point here rather than each asserting a status of their own.
  Every one is a **race or a lifecycle-ordering fix**, which is precisely the class a unit test
  cannot reach: the bug needs a real game thread, a real disconnect, and real timing.
  - **M1 / M2 / M3 — Schlacht restore-set** (disable↔Tick race repopulating `hiddenActors`; disable
    while the game thread is stalled discarding the restore set; no un-hide on disconnect/shutdown).
    Acceptance: enable See-Through, then (a) toggle off during motion, (b) toggle off while the game
    is paused/stalled, (c) yank the UI connection and (d) close the game — in **all four** every
    hidden actor must become visible again. A single actor left invisible is the failure, and it is
    only visible on screen. ⬜
  - **M4 — Tot latch zombifying a Solide hold** during the disconnect window. Acceptance: start a
    force-field hold, disconnect the UI mid-hold, reconnect → `get_forced_fields` must still list the
    hold AND the value must still be held (a zombie job lists but stops re-asserting, so checking the
    list alone is not enough — read the value in CE). ⬜
  - **M5 — `UE5_Shutdown` worker-join ordering** (joined hold workers before stopping the pipe, so a
    mutator arriving in the window respawned an unjoined worker). Acceptance: with a hold active,
    close the game while the UI is still connected → no hang, no crash on exit. Evidence is the
    absence of a hang; there is no positive log line. ⬜
  - **DLL LOWs L1 / L5 / L8 / L10 / L12** (Solitar worker start/stop under `s_workerMutex`;
    Welford gap underflow on out-of-order PE timestamps; Grausam `GetWindowTextW` under `g_mutex`
    hanging the pipe thread; Grausam post-enable windows + shutdown teardown; Fern `str_params`
    malloc leak on a mid-loop JSON `type_error`). L8 and L12 are the ones with a user-visible
    symptom (pipe stall / leak under repeated failed invokes). ⬜
  - **Solide LOWs L2 / L3 / L4** (weak-ptr refusal no longer silent; substring class + fuzzy field
    match tightened; per-instance restore bases instead of one representative). L4's prune guard was
    touched again in build 2531 — see the Solide pool-truncation entry below, verify them together. ⬜

- ✅ **Value Search `TSet<T>` / `TMap<K,V>` scan (key: V1a)** — **VERIFIED 2026-08-05 (DumperTest,
  build 2650), ⬜ since build 927.** Scanning `4242` returned `DumperTestActor.Set_Int[1]`
  (IntProperty, Reflected, offset `0x358`) on both the live actor and the CDO; scanning `222`
  returned `DumperTestActor.Map_NameToInt.Value[1]` at `0x3A8`. Both render with the element index,
  which is what the row format promised. The sparse-walk geometry hands back the slots we expect.
  *Not yet exercised: container reallocation between scans (the degrade-don't-lie case).*
- ✅ **Value Search `TOptional<T>` scan (key: V1c)** — **VERIFIED 2026-08-05, ⬜ since build 942.**
  `24680` returned `DumperTestActor.Opt_Int_Set` (IntProperty, `0x468`), and — the criterion that
  actually matters because it is negative — **a scan for `0` did NOT surface `Opt_Int_Unset`**, so
  the `bIsSet` gate holds and an unset optional is not being read as a zero.
- ✅ **Value Search `NumericAll` (byte families included)** — **VERIFIED 2026-08-05, ⬜ since build
  796.** `-5` (Int8Property) and `255` (ByteProperty) both returned results with NumericAll
  selected. *The remaining half is a UX judgement, not a defect: whether the result volume for a
  1-byte value is usable. The panel's own orange warning says it will flood, and this sample cannot
  settle "usable" — that needs a real game's object count.*
- **Value Search `TSet<T>` / `TMap<K,V>` scan — original instructions** (build 927). Scan a known value held
  in a `TSet<int>` / `TMap<K,int>` UPROPERTY → rows must render as `Set[idx]` / `Map.Key[idx]` /
  `Map.Value[idx]`, and a Next Scan must prune. The sparse-walk geometry
  (`Ubel::GetSetElementStride` / `GetMapPairLayout`) is shared with the container-aware Address
  Finder and unit-tested; what is NOT provable offline is that live sets/maps hand back the slots
  we expect. Specifically watch a **container reallocation between scans** — element addresses are
  raw, so refine degrades exactly like `TArray` (the SEH-safe read drops the candidate); confirm it
  degrades rather than reporting a wrong hit. ⬜ unverified.
- **Value Search `NumericAll` (byte families included) (key: NumericAll)** (build 796-797). Select
  NumericAll and scan a value that genuinely lives in an `Int8Property` / `ByteProperty` → confirm
  the byte field is found, and that the orange result-volume warning
  (`ValueSearchViewModel.DataTypeWarning`) appears. `BuildNumericTargets`' range gating is
  unit-tested (`300` → no Int8/UInt8; `-5` → Int8 yes / UInt8 no); the live question is whether the
  result volume for a small value (0/1/255) is *usable* or drowns the panel — that is a UX
  judgement no test can make. ⬜ unverified.
- **Value Search `TOptional<T>` scan (key: V1c)** (build 942). Scan a known value held in a
  `TOptional<int/float/FString>` UPROPERTY → confirm the row appears under the optional's
  field name and a Next Scan prunes; confirm an **unset** optional doesn't surface on a
  scan for `0` (the `bIsSet` gate). Layout helper is unit-tested; the field walk needs a
  live game with optional UPROPERTYs.
- **Property freeze (Route B)** on a respawning-NPC game (build 719). Watch: tick FPS
  impact (50ms × N instances), rescan cadence at respawn, vtable-liveness guard on level
  transition, AOBMaker gating UX, multi-script coexistence. First candidate: Geri (UE
  4.27).
- **Build-648 ProcessEvent fix** re-verify on ES2 (UE 5.5) + Geri (UE 4.27): look for
  `GameThreadDispatch: validation OK — hook fired N times`; previously-`-5`-timing-out
  instance invokes should now succeed. Lower-priority extras: a UE 4.18-4.24 game (smaller
  vtable / lower slot) + a heavily-modified publisher fork.
- **Static-native PE fast path** (build 636) latency vs game-thread dispatch on an active
  session; confirm stateful UFunctions still route through dispatch (don't fall into the
  fast path by accident).
- **FPROPERTY_FLAGS offset fix** (build 642): sweep the 12+ tested games' Class Structure
  Return columns + confirm baked PARAMS no longer include ReturnValue as an input.
- **Verify Return Value diagnostic** (build 637/644): pointer-return shows `0x` prefix;
  FString-return shows the "see After: dump above" hint.
- **`walk_functions_batch` follow-up** — Effort: **S**. Sister to `walk_class_batch`;
  DumpAll still does `WalkFunctions` single-call per class. Same byte-equivalence safety
  net. **Skip unless profiling shows it as the new bottleneck.**

-----

## See-through (Schlacht) — "pass light/shadow through too?" — EVALUATED (mostly WON'T-DO)

**Question:** can See-through also let the occluder's **light/shadow** effects pass through, not just
its mesh? **Verdict: split by lighting type — dynamic is already handled; baked is infeasible from an
injected DLL.** (36-agent adversarial verify against UE engine source, 2026-07-09.)

- **Dynamic / real-time light (movable lights, Lumen GI+reflections, DF shadows/DFAO, HW ray tracing)
  — ALREADY passes through, no code change.** `AActor::SetActorHiddenInGame(true)` sets `bHidden` →
  `UPrimitiveComponent::ShouldRender()` false → `ShouldComponentAddToScene()` false (default flags) →
  the primitive is dropped from the render scene entirely, so it is absent from the **shadow-depth
  pass** too — the dynamic shadow vanishes with the mesh (community's `bCastHiddenShadow=true` recipe
  exists only because default hiding drops the shadow). UE5 does the same for the mesh distance-field /
  Lumen scene: `PrimitiveNeedsDistanceFieldSceneData()` has `IsDrawnInGame()` as a required OR-term,
  and `FScene::UpdatePrimitivesIsDrawn_RenderThread()` calls `DistanceFieldSceneData.RemovePrimitive()`
  + `LumenRemovePrimitive()` on the hide branch by default; the HW-RT gather also skips `!bDrawInGame`
  primitives. So on a Lumen/movable-light game, hiding the wall already removes its shadow, GI
  occlusion, and RT contribution.

- **Exception (fixable): a game that sets `bCastHiddenShadow=true` (or `bAffectIndirectLightingWhileHidden`)
  on world meshes** keeps the shadow/GI after hide (that flag's whole purpose is cast-while-hidden).
  **Only actionable enhancement:** alongside `SetActorHiddenInGame`, also invoke
  `UPrimitiveComponent::SetCastShadow(false)` / `SetCastHiddenShadow(false)` (and
  `SetAffectDistanceFieldLighting(false)` for Lumen/DF) on each of the hit actor's primitive components,
  restoring on un-hide. All are `BlueprintCallable` UFUNCTIONs reachable via the existing
  `UE5_CallProcessEventEx` ProcessEvent path; component enumeration already exists (`GetRelatedObjects`).
  Effort: **S** · Risk: low. **Do only if a real game shows a lingering shadow after See-through hides
  the mesh** (LIVE-VERIFY first, per the module's ethos). Won't help baked lighting.

- **Baked / static light (Static or Stationary mobility — the common case for UE4 & perf-sensitive UE5
  world geometry: locked-60fps, mobile, VR) — INFEASIBLE, WON'T-DO.** The wall's shadow is baked by
  Lightmass into the **receiving** surface's (floor / neighbouring wall) lightmap texture (and per-object
  distance-field shadow maps for Stationary), stored per-mesh in the `MapBuildDataRegistry` — it lives
  on the receiver, not the caster. `SetActorHiddenInGame` only toggles the caster's own primitive
  visibility; it cannot touch another mesh's lightmap, so a **"ghost shadow"** stays exactly where the
  wall was. Removing a baked shadow needs an editor-time **Build Lighting** (Lightmass is editor-only,
  stripped from shipping/cooked builds); no runtime API recomputes lightmaps. The only external "fix"
  is forcing the whole level to unlit/dynamic (`r.AllowStaticLighting 0` + restart — global, breaks all
  level lighting), which isn't worth it. This is why many games show a residual shadow after See-through
  hides the mesh — nothing we can do about it from a DLL.

*Parent: Schlacht Stage 1 (dev-log 2026-07-08 build ~1989; project-seethrough-occluders-schlacht).*

-----

## Output-monitor pin — "the game has no monitor-select UI" — EVALUATED (2026-07-23), NOT BUILT

**Question:** on a dual-monitor setup, when a game exposes no output-display setting, can we fix it
with **UE functionality**? **Verdict: the UE reflection layer has no concept of an output monitor —
the monitor-selecting step is Win32/DXGI. UE reflection only contributes the windowed↔fullscreen
toggle and the persistence.** And the hard part is not the initial move, it is that the game
**drifts back** — so the deliverable is a *pin*, not a one-shot move.

**What UE reflection does and does not give us**

- Stock UE has **no** monitor-index `UPROPERTY`, no BlueprintCallable monitor selector, and no cvar.
  (The `-monitor=N` recipe circulating since Froyok's 2018 post is an *engine source modification*,
  not stock behaviour.) `r.setres WxH[w|f|wf]` changes mode/resolution, never the screen.
- **Invokable today** (BlueprintCallable ⇒ in the reflection function table ⇒ reachable via
  `invoke_function`): `UGameUserSettings::SetFullscreenMode(int32)` (`EWindowMode` 0=Fullscreen /
  1=WindowedFullscreen / 2=Windowed), `SetScreenResolution`, `ApplyResolutionSettings(bool)`,
  `ApplySettings(bool)`, `SaveSettings()`.
- **NOT invokable:** `SetWindowPosition()` / `GetWindowPosition()` are **not** BlueprintCallable, so
  they are absent from the reflection function table. The backing `WindowPosX` / `WindowPosY` *are*
  config properties (default `-1` = centre) ⇒ writable via Property Search / Live Walker / Solide
  Force. That yields a no-code path (**write WindowPosX/Y → invoke `SaveSettings()` → restart**) but
  it needs a restart and collides with the documented UE 4.16+ "re-centres itself after the startup
  map loads" override.
- Why the move-then-fullscreen sequence works at all: UE `WindowedFullscreen` resolves via
  `MonitorFromWindow`, and DXGI exclusive fullscreen picks "the output containing most of the client
  area" when `pTarget` is NULL — **both follow the window**. So `SetFullscreenMode(2) → move the
  HWND → SetFullscreenMode(1)` lands on the target screen.

**Drift is event-driven, not continuous** — regain focus / alt-tab / `WM_DISPLAYCHANGE` /
swapchain reset. Unity's issue tracker documents exactly this symptom ("exclusive fullscreen always
opens on monitor 1 after regaining focus even when monitor 2 is set as primary"). So a pin does
**not** need a high-frequency poll.

**Three pin mechanisms, lightest first**

- **(a) Rewrite `WM_WINDOWPOSCHANGING` — the good one.** `Grausam.cpp` `SubclassProc` (~line 144)
  already subclasses the game WndProc and `Grausam.cpp` `FindGameWindow()` (~line 61) already resolves the HWND
  (`EnumWindows` + same PID + largest visible). Patching `WINDOWPOS.x/y` **before the move happens**
  is flicker-free and the game never notices. Any "detect it moved, move it back" scheme flickers and
  fights the game's own repositioning — which is the user-visible "it just snaps back" symptom.
- **(b) Low-frequency watchdog — the backstop.** ~4-5 Hz worker; if
  `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target`, `SetWindowPos`. Structurally
  **identical to the Solide / Hemmung / Laufen write-on-drift re-assert workers** — copy the shape.
  Covers paths (a) can't see (game switches mode via the swapchain, not via `SetWindowPos`).
- **(c) Hook `IDXGISwapChain::SetFullscreenState` — the real fix for exclusive fullscreen.** MSDN is
  explicit: `pTarget` **is** the output selector; NULL means "DXGI guesses from window placement",
  and on alt-enter **NULL is the only option DXGI has**. So for a true-exclusive-fullscreen game
  (a)+(b) are palliative — the game's next `SetFullscreenState(TRUE, NULL)` re-guesses. Substituting
  the user's chosen `IDXGIOutput*` is the cure. MinHook is already vendored (Stark/Grausam), but
  `Lugner_Dxgi.cpp` is a **pure export forwarder (asm thunks), not a
  swapchain vtable hook** — this is entirely new work, and per-API (D3D11 / D3D12 / Vulkan separately).

**This feature is not UE-bound (scope decision needed).** `Heiter.cpp` (`ProxyStart`, ~lines 57-86)
shows **proxy mode starts the pipe server immediately with no AOB scan**, and all three mechanisms
above are pure Win32/DXGI with zero UE reflection ⇒ injected via the `dxgi.dll` proxy this would work
in a **Unity** game too. Blocker: every UI panel currently assumes UE init succeeded, so a non-UE
process shows a wall of errors. Either accept "only this one card works, everything else is red" or
build a minimal non-UE mode — decide before advertising it as a capability.

**Try the no-code per-engine fixes first** (this class of game is rare; don't pre-build):

- **Unity:** `HKCU\Software\<Company>\<Product>` → **`UnitySelectMonitor`** (0-based), and the
  documented **`-adapter N`** launch arg. ⚠ *Engine-specific*: in **UE**, `-adapter` selects the **GPU
  adapter** and does nothing for monitor choice — the two engines are not interchangeable here, and
  `-adapter` is widely mis-recommended for UE.
- **UE:** `-windowed -WinX= -WinY= -ResX= -ResY=` (Steam launch options) or the same keys in
  `%LOCALAPPDATA%\<Game>\Saved\Config\Windows\GameUserSettings.ini` — subject to the 4.16+ recentre bug.
- **Engine-agnostic:** disable the unwanted display before launch (MultiMonitorTool / `DisplaySwitch`),
  re-enable after. 100% effective against "always picks output 0" games.
- **Why "set it as primary" fails:** enumeration order comes from the adapter's output connectors
  (`EnumDisplayMonitors` / DXGI output order); Windows exposes **no** way to reorder it, and the
  primary flag doesn't change it. That is why physically re-ordering the DP cables is the only clean
  non-tool fix.

**Prior art — check before building.** Special K already does this (Window Management X/Y offset,
retained across launches). For one or two games it is the faster answer. Our differentiators: the
zero-flicker (a) that Special K lacks, integration with the existing UI, and the (c) DXGI path —
Special K's own multi-display borderless-fullscreen limitation is still open (SpecialKO/SpecialK#87).

| Phase | Scope | Effort | Risk |
|---|---|---|---|
| **P1** | (a) `WM_WINDOWPOSCHANGING` + (b) watchdog + `EnumDisplayMonitors` listing + 2 pipe cmds (`list_monitors` / `set_game_monitor`) + one Teleport card. Borderless/windowed only | **M** | low |
| **P2** | (c) `SetFullscreenState` hook — covers exclusive fullscreen | **M-L** | med — swapchain vtable hooks read as overlay behaviour to some anti-cheat; per-graphics-API work |
| **P3** | Minimal non-UE-mode UI boundary (unlocks Unity/other engines) | **M** | med |

**Naming:** take **Böse** (barrier/guard) from the [naming-convention.md](naming-convention.md) roster —
the module's job is *holding the window in place*, a barrier, not a transfer (so not `Zart`, and
teleport semantics stay with Wirbel).

**Recommendation:** spend ten minutes on `UnitySelectMonitor` / `-adapter N` against the actual
offending game first. If that sticks, park this entirely — P1+P2 is M-L of work for a handful of
games. If it doesn't stick *and* more than one such game is on hand, P1 alone is cheap: it reuses
Grausam's subclass and Solide's re-assert shape, leaving only monitor enumeration and the
`WM_WINDOWPOSCHANGING` branch as genuinely new code.

*Parent: Grausam foreground-lock infrastructure (dev-log builds ~1950-1984;
project-foreground-lock-grausam). Sibling evaluation of the Schlacht see-through and Hemmung
time-control evals above.*

-----

## 4th proxy DLL — winmm.dll — ✅ SHIPPED build 2317 (as a SLOT, not for coverage)

**Built on the slot-contention trigger, not the coverage one.** The n=24 census below stands: winmm
and dxgi both cover 100% and winmm reaches exactly zero games dxgi misses, so there was never a
coverage case. What justifies it is the other half of that finding — **a proxy only works if its
filename is free.** `dxgi.dll` is the name ReShade and many mod loaders take; `version.dll` is
likewise a common ASI/mod-loader name (e.g. Ultimate ASI Loader). With both taken the only remaining
choice was dinput8 at 2/24. winmm is the spare universally-viable slot.

**Generated, not hand-written** (`scripts/gen_proxy_forwarders.py winmm`): 180 exports across
`Lugner_Winmm.cpp` / `.asm` / `ProxyWinmm.def`. Re-run with `--check` to verify they are current.
**Verified against the real DLL:** 180/180 forwarding exports present, **every ordinal matching
System32 winmm exactly**, zero missing, plus our 60-symbol UE5 ABI — and the proxy does **not**
import winmm itself, which the build-2301 prerequisite is what makes possible.

*Kept below: the census that says don't build it for coverage, and the trap that had to be fixed
first. Both still govern any FIFTH flavour.*

**⚠ The earlier n=7 recommendation was wrong, and it is worth knowing why.** That sample silently
included **non-UE games** — Nioh3 (Team Ninja), Crimson Desert (BlackSpace), Atelier Yumia (KT) —
because it globbed a Steam library rather than gating on UE markers. Nioh3 was the single row that
made winmm look uniquely valuable ("imports neither dxgi nor version"), and it is not a UE game at
all, so it never bore on this decision.

**Measured — every installed UE game, n=24 (`scripts/analysis/scan_proxy_imports.py`):**

| Module | Coverage | Note |
|---|---|---|
| **dxgi.dll** | **24/24 (100%)** | every UE game, static or delay import |
| **winmm.dll** | **24/24 (100%)** | identical set — **adds nothing over dxgi** |
| d3d11 / d3d12 | 24/24 | (not hijack candidates; sanity check that the scan saw real games) |
| dsound.dll | 23/24 | |
| xinput1_3 | 17/24 | |
| version.dll | 7/24 | **but see below — this number does NOT measure version's viability** |
| dinput8.dll | 2/24 | genuinely weak |

**Games importing none of {version, dinput8, dxgi}: 0.** The current three already reach everything.

- **The version.dll 7/24 is not a coverage figure.** Per `ProxyImportAnalyzer`'s own class remarks,
  version.dll is loaded *dynamically* by almost every Windows process, so its absence from an import
  table says nothing about whether the proxy works. It remains the safe universal default; the 29%
  is only how often it happens to be a *static* import.
- **KnownDLLs verified on this host** (Win11 26200): `winmm` / `dsound` / `dxgi` / `version` /
  `dinput8` are **all absent** ⇒ all app-dir-hijackable. `IMM32` / `MSVCRT` / `gdiplus` / `SHCORE` /
  `PSAPI` / `SHLWAPI` **are** listed ⇒ permanently non-viable, exclude from any future selection.
- **Export counts (measured):** version 17 · dinput8 6 · dxgi 20 · **winmm 181 (180 named)** ·
  dsound 12. If winmm is ever built: do **not** hand-write it — **generate** the `.def` + trampoline
  `.asm` from the real DLL's export table (and re-generate dxgi's from the same script to kill
  hand-maintenance drift).

**The one surviving trigger: slot contention, not coverage.** A proxy only helps if its filename is
*free*. Two real cases: **ReShade** commonly installs itself as `dxgi.dll` (or `d3d11.dll`), and
**ASI / mod loaders** (e.g. Ultimate ASI Loader) commonly install as `version.dll`. A user with
ReShade on dxgi *and* an ASI loader on version has only dinput8 left, which is 2/24. **Build winmm if, and only if, that
combination shows up in practice** — it would then be a genuinely free 100%-coverage slot. Until
then it is M-effort for a case nobody has reported.

**✅ DONE (build 2308) — `ProxyImportAnalyzer` misread modular UE builds.** `ReadProxyImports`
is handed `game.ExePath` only. In a **modular** build (Satisfactory) that exe is a ~264 KB bootstrap
stub and the engine lives in `*-Win64-Shipping.dll` modules, so the analyzer sees no dxgi/dinput8 and
the Suggested-proxy column claims `version · default · no dxgi/dinput8` — when a dxgi proxy would in
fact load fine (`D3D12RHI` imports it). **Severity LOW**: the analyzer's design deliberately treats
imports as advisory context that never overrides the version default, so the harm is a misleading
hint string, not a wrong deployment. Fix shape: when the main exe imports none of
`{dxgi, d3d11, d3d12}`, union in the imports of the sibling `*-Win64-Shipping.dll` modules — the same
fallback `scan_proxy_imports.py` uses. **Shipped:** `ImportsNone` + a pure `Merge` OR on
`ProxyImportInfo`, with the file-walking half in `ProxyDeployService` so the analyzer stays OS-free.
Measured at **30 ms** for Satisfactory's 182 modules; monolithic games unaffected (0 ms, fallback
never triggers). +5 tests.

**✅ CLEARED (build 2301) — the BLOCKER: we called winmm ourselves, from the shared object library.**
Kept here because it is the single most important thing to understand before touching this idea, and
because the same trap applies to any future proxy whose API we also *consume*.

`Mimic.cpp` raises the timer resolution for the CE-mailbox poll thread
(`timeBeginPeriod(kPollIntervalMs)` / `timeEndPeriod`), and `Mimic.cpp` is in
**`UE5_COMMON_SOURCES`** — the object library linked into the main DLL *and every proxy*. `Winmm` was
in both `UE5Dumper`'s link list and `PROXY_LINK_LIBS`. Had our proxy *been* `winmm.dll`:

- our static import of `winmm.dll!timeBeginPeriod` would resolve against the module named
  `winmm.dll` in the process — **ourselves** → `Proxy_timeBeginPeriod` → the forwarding pointer.
  Before the real System32 winmm is resolved that pointer is the fallback stub, which **returns 0 —
  and `0` is `TIMERR_NOERROR`**. So it would not crash: it would **silently succeed while doing
  nothing**, `Sleep(1)` degrading to the 15.6 ms tick and CE-mailbox latency getting ~15× worse with
  no error anywhere.
- **Delay-load would not have saved us** (unlike the version proxy, which delay-loads `version.dll`
  purely to break the link-time circularity and never calls into it): a delay-load
  `LoadLibrary("winmm.dll")` from the game directory finds **us** again.
- **No test would have caught it** — `dll_helpers_test` linked `Winmm` directly into the test exe, so
  its `timeBeginPeriod(1)` + `Sleep(1)` latency assert passed regardless of proxy behaviour.

**How it was fixed (build 2301, dev-log 2026-07-23):** `Mimic.cpp` now resolves
`timeBeginPeriod` / `timeEndPeriod` from the **System32** copy by explicit path
(`GetSystemDirectoryW` → `LoadLibraryW(<sys>\winmm.dll)` → `GetProcAddress`), and `Winmm` is gone
from `UE5Dumper`'s link list, `PROXY_LINK_LIBS`, **and** `dll_helpers_test` — whose latency check now
resolves the same way, so it covers the real mechanism. Windows keys loaded modules by full path, so
this always yields the genuine OS winmm even with a same-named proxy of ours mapped. The helper is
proxy-agnostic (no `UE5_PROXY_*` test), satisfying the `UE5_COMMON_SOURCES` invariant — an `#ifdef`
would have violated it. **Verified objectively:** `winmm.dll` no longer appears in the import table
of `UE5Dumper.dll`, any of the three proxies, or the test exe; and the reworked latency check
measures 1.95 ms/sleep through the resolved pointers (vs the ~15.6 ms a silent no-op would give).
**UI side: verified clean** — no `winmm` / `timeBeginPeriod` usage anywhere in `ui/`.

**Prior art — `D:\Github\ZoltDump` already ships a winmm proxy.** Shape to copy: `Eisen.cpp/.h` +
a **generated** `EisenWinmmPtrs.h` (one `extern "C" FARPROC g_pfn_<name>` per export, every one
initialised to `Proxy_Fallback` = `xor rax,rax; ret` so exports missing on older Windows return 0
instead of jumping through null) + `ProxyWinmmTrampoline.asm` (MASM stubs jumping via the pointers) +
`ProxyWinmm.def` (`name = Proxy_name`), with the real DLL loaded by explicit `GetSystemDirectoryW`
path. Its `build.ps1` parameterises the flavour as `-ProxyTarget winmm` rather than our hardcoded
target triple. **Two caveats when copying:** (1) ZoltDump has **no** `timeBeginPeriod` caller of its
own, so its design does not address the self-call trap above — don't assume copying it is enough
(ours is now handled on our side, in Mimic); (2) that same
returns-0 fallback is precisely what makes our self-call fail *silently*. Keep our forwarder named
`Lugner_Winmm.cpp` for internal consistency (`Lugner` owns proxy forwarding here); ZoltDump used
`Eisen` only because it has no `Lügner` equivalent.

**Build-script deltas (measured, so the "compile once" sharing is preserved):**
`dll/CMakeLists.txt` — add `src/Lugner_Winmm.cpp` to `PROXY_SPECIFIC_SOURCES` (gated by
`UE5_PROXY_WINMM_BUILD` like the other two) + one `option(BUILD_PROXY_WINMM)` / `add_library` block
copied from the dxgi one. **`UE5DumperCommon` must not change** — that is what keeps the shared
sources compiling once instead of 5×. `build.ps1` — `-Target` `ValidateSet`, the `$cppTargets` map
(~line 355), the `-DBUILD_PROXY_*=ON` configure string (appears **twice**: ~363 and ~588 — both must
be updated or the test configure silently drops the target), and the dist-copy table (~413-415).
`build.cmd` needs nothing unless we want a `build proxywinmm` alias (it only forwards mode/target).
UI — `Models/ProxyType.cs` (enum + `GetDllName` + `GetDisplayName` + `FromDllName`), `Constants.cs`,
`Services/ProxyImportAnalyzer.cs`, `Services/DumperModuleDetector.cs`, `Services/ProxyDeployService.cs`,
`ViewModels/ProxyDeployViewModel.cs`, `Resources/Strings/en.axaml`, tests.

**Two invariants to hold:** (1) `ProxyImportAnalyzer`'s class remarks deliberately refuse to
auto-escalate away from the version default — **winmm must be an advisory alt, not the new default**,
until a wider sample proves otherwise. (2) A static import only proves the DLL *gets loaded*, not
that the proxy *survives* (anti-tamper, signature checks, an occupied slot); and its absence doesn't
prove the reverse either, since a dynamic `LoadLibrary("dxgi.dll")` also searches the app dir first.

**Rejected alternatives:** `dsound` — cheap (12 exports) but 4/7 and lands in audio init.
`xinput1_4` — 109 functions but only **8 named, the rest ordinal-only**, so the `.def` needs
`NONAME` + ordinal mapping, for only 3/7 coverage.

**Note for whoever adds winmm:** the double-inject guards are now driven off a shared named list
(`Methode.cpp` `kProxyDllNames` / `UE5CEDumper.CT` `UE5_PROXY_DLL_NAMES`) rather than the old
hardcoded `version` + `winmm` pair — add the 4th flavour to **both** or the guard silently stops
covering it (shipped build 2291; the `.CT` half is in-game verified, the `Methode.cpp` CE-plugin half
is only reachable via CE's *Inject && Connect* menu item and has not been exercised).

Effort **M** · Risk low (the prerequisite is done) — **but do not spend it without the slot-contention
trigger above.** The census that would have justified it has now been run (n=24) and says no; re-run
`scripts/analysis/scan_proxy_imports.py` if the installed set changes substantially before
revisiting.

*Parent: the 3-proxy set (version/dinput8/dxgi; project-dll-loading-and-proxies).*

-----

## UE performance counters in the UI — EVALUATED (2026-07-23), tiered

**Verdict: the literal ask — surfacing UE's own `stat` counters — is impossible from an injected
DLL. But the two cheapest tiers are worth more than the literal ask, because they measure the thing
[multipipe-eval.md](multipipe-eval.md) already blames for UI lag and currently has zero telemetry for.**

- **Tier 0 — WON'T DO: UE's `stat` system.** Shipping builds compile with `STATS=0` (even the *Test*
  configuration defines `STATS 0` by default), and the console is **removed from the binary** in
  Shipping, not hidden. Re-enabling needs `FORCE_USE_STATS` and an engine recompile. Unreachable from
  an injected DLL — record as WON'T-DO so it isn't re-litigated.

- **✅ Tier 1 — DONE (build 2308).** New `Sense` module + `get_diagnostics` pipe command + a
  System-tab card. Records per-command dispatch cost (count / total / max / last) at Fern's existing
  `inFlight` chokepoint — which is exactly the head-of-line window — and reports `busy_percent`, the
  fraction of wall-clock a dispatcher was occupied. **That is the number Phase 1 was missing.**
  Also carries game-thread health from Stark and the GObjects count. *Original note kept below for
  the rationale.*

- **Tier 1 — our own health. Zero new machinery, highest value.**
  [multipipe-eval.md](multipipe-eval.md) already names DLL-side **serial-dispatch head-of-line
  blocking** as the root cause of UI lag and game-thread CPU starvation as the CE-mailbox risk — yet
  neither is measured, so Phase 1 would be decided blind. Free to collect: per-command Fern handling
  time + queue depth; Stark invoke queue depth / timeout count (`invoke_timeout_ms` is already
  reported over the pipe); per-worker tick count + write-on-drift hit rate for Solide / Hemmung /
  Laufen / Solitar / Schlacht; Aura `NumElements` over time (GC/leak indicator). **Linie already
  computes frame-cadence statistics** (per-UFunction fire counts + Welford mean/cv) — it just isn't
  presented as performance. Effort **S-M** · Risk none.

- **✅ Tier 2 — DONE (build 2308).** Working set / private bytes / CPU% / thread + handle counts,
  in the same `get_diagnostics` payload. On demand only (thread count walks a system-wide snapshot).
  CPU% is `-1` until a second sample exists to difference against, and the UI renders that as an em
  dash — "0%" would read as *idle*, which is a different and wrong claim.

- **Tier 3 — real FPS / frame time: hook `IDXGISwapChain::Present`.** The only engine-version-
  independent, accurate source (true frametime, 1% low, pacing, present mode). **Shares its entire
  hook infrastructure with P2 of the output-monitor-pin evaluation above** — these two must be
  decided together and funded once, not twice. Effort **M-L** (joint) · Risk med (overlay-shaped
  behaviour; per-graphics-API work).

- **Tier 3.5 — `GAverageFPS` / `GAverageMS` via AOB.** These are plain engine globals
  (`GAverageFPS = 1000/GAverageMS`), **not** gated by `STATS`, so they survive Shipping, and Himmel's
  128-pattern infrastructure could carry a signature. But it is a per-version/per-compiler signature
  to maintain and yields the engine's *smoothed average*, strictly worse than the Present hook. Keep
  only as the fallback if we decide never to hook DXGI.

- **Tier 4 — reflected time values.** `AWorldSettings::TimeDilation` (Hemmung already reads it) and
  `UWorld::TimeSeconds` / `RealTimeSeconds` / `DeltaTimeSeconds` (not `UPROPERTY` — needs DynOff
  probing). Caveat: `DeltaTimeSeconds` is the **game-thread** delta only (no render/GPU) and is
  polluted by time dilation — usable as context, **not** as an FPS readout.

**Status: Tier 1 + Tier 2 SHIPPED (build 2308). Tier 3 still deferred to the monitor-pin P2
DXGI-hook decision; Tier 0 remains WON'T-DO.**

**Follow-on deliberately NOT built: per-worker tick counters** for Solide / Hemmung / Laufen /
Solitar / Schlacht (tick count + write-on-drift hit rate). That is five modules touched for a number
that does not bear on the dispatch question — and the dispatch question is the one that blocked a
decision. Worth doing if a re-assert worker is ever suspected of burning game-thread time. Effort
**S-M** · Risk low.

**✅ Automatic PERF records — DONE (build 2320).** `Services/DiagnosticsProbe.cs` brackets **Copy CE
XML / Copy CE Field / Value Scan (First & Next) / Snapshot capture** with two `get_diagnostics`
snapshots and logs the delta as a `PERF` line in the `view` log. Better than the manual measurement
session it replaces: a deliberate test only covers the scenario somebody thought of, and only if they
remembered to reset first — this accumulates evidence from real use.

**✅ ANSWERED (2026-07-23, build 2324) — and the answer is "don't build Phase 1".** Measured on
Elliot (UE 5.4) + SEED (UE 4.27), 24,178 dispatches across 5 real Copy CE XML / Copy CE Field runs.
Full table and reasoning in [multipipe-eval.md](multipipe-eval.md) §10.

- **Dispatcher busy 29.8%** — idle ~70% of wall-clock, and the ratio holds (22-31%) across
  operations from 2.6 ms to 5.4 s. Non-blocking dispatch can only recover a slice of the busy 30%,
  and only if something were queued behind it — in a single-user export nothing is.
- **Worst SINGLE dispatch: 14.3 ms** out of 24,178. Phase 1's premise is a long-blocking command
  holding the read loop; no such command exists here.
- Phase 1 was already **shipped and reverted once** (build 1840) and a correct version needs
  overlapped/async pipe I/O. Not a trade worth making for this.

**The real lever is CALL COUNT.** `walk_instance` is 100% of dispatcher cost in every row, and one
Copy CE XML issued **20,357** of them: **0.088 ms in the DLL vs 0.208 ms of round-trip overhead —
2.4x the work is overhead.** Batching it at the established ~200/call chunk (as
`search_properties_batch` / `walk_class_batch` already do) would collapse 24,178 round-trips to
~121. **✅ SHIPPED build 2329 — `walk_instance_batch`.** The measurement said dll 27-30% / **ipc 59-73%** /
ui 0-10%, i.e. per-call round-trip overhead roughly 2x the actual walk, so the calls were collapsed
(chunk ~200). Built to the `walk_class_batch` precedent with all three safety layers: a DLL handler
that is a trivial loop over the single-call path, a shared serialiser/deserialiser pair, and an
equivalence test comparing both paths field-for-field. The CE export now walks breadth-first per
level. A failed batch — or a short/long reply, which would otherwise mis-pair results with addresses
— replays the chunk as single calls.

**✅ DONE + MEASURED (build 2335): 1.71x faster.** Copy CE XML on SEED went **5,893 -> 3,437 ms**,
dispatches **22,522 -> 1,355**, IPC **3,532 -> 1,278 ms**. `top:` names `walk_instance_batch`.
(Build 2329 had batched the wrong loop - the calls come from the STRUCT tree, not the
object-pointer drilldown; fixed with a breadth-first `PrefetchStructTreeAsync` feeding the
unchanged depth-first emit, since that emit's order IS the exported field order.)

**The 2.4-3.5x projection was wrong, and usefully so - IPC is not purely per-round-trip.** At the
old 0.157 ms/call, 1,355 calls should have cost ~212 ms of IPC; they cost **1,278 ms**. So of the
original 3,532 ms, ~2,253 ms was fixed per-round-trip cost (removed) and **~1,066 ms is
payload-proportional** (untouchable by batching - the same bytes still cross). `ui` rose 610 -> 653
ms for the same reason. Full table in [multipipe-eval.md](multipipe-eval.md) section 10.5.

**Next lever, if anyone wants more: BYTES, not messages.** Remaining 3,437 ms = dll 1,506 (real
work) + ipc 1,278 (mostly payload) + ui 653 (parse). Trimming fields the CE export never reads would
hit the payload-proportional IPC *and* the parse cost together. Note also that raising the batch
chunk would achieve nothing: average batch size is ~16.6 (fan-out-limited), not near the 200 cap.

**✅ MEASURED (build 2339) — `scripts/analysis/walk_payload_audit.py`.** Byte-accounted a real
Copy CE XML on SEED against a key-by-key map of what the exporters read (full table in
[multipipe-eval.md](multipipe-eval.md) section 10.6):

- Per-field keys (52.7% of the sample): **60.9% used / 18.6% CSX-only / 16.7% unused.**
- Inline array elements (20.3%): **43.9% used / 44.6% unused** — `elem.h` (element raw hex) alone
  is 9.0% of the whole payload and no exporter reads it.
- The per-instance header (`name` / `class` / `outer_*` / `props_size` / even `addr`) is **99%
  dead** — the export touches `result.Fields` and nothing else.
- Verdict: **~24% of the payload-scaling bytes are droppable outright, ~38% if CSX opts out of
  `hex` too.** Biggest single items: `elem.h`, `field.hex` (CSX-only), `field.value`,
  `field.array_inner_addr`.

**✅ SHIPPED (build 2351) — `lean: true`.** `walk_instance` / `walk_instance_batch` take a `lean`
flag that omits exactly those keys (drop list in [pipe-protocol.md](pipe-protocol.md); design notes
in [multipipe-eval.md](multipipe-eval.md) section 10.7). Subtractive only, so an older DLL that
ignores it stays correct. Wired to the CE XML export path ONLY — CSX shares the same
`ResolveDrilldownAsync` and genuinely reads `hex` / `bool_mask` / `bool_byte_offset`, so the default
stays full-fat. `WalkInstanceLeanTests` proves lean and full payloads produce **byte-identical XML**
(mutation-checked: blanking a key the exporter does read fails it).

**✅ IN-GAME VERIFIED (build 2353, SEED).** Same object exported before (DLL 2338) and after
(DLL 2353): **payload 1,982,875 -> 1,168,944 bytes over the same 134 batch responses = -41.0%**,
matching section 10.6's prediction. The XML is unchanged — 149,621 lines / 14,326 leaves both
sides, 15 differing lines and every one a per-session value (root address + FName ComparisonIndex,
name half identical). DLL serialise time -20% (146.7 -> 116-119 ms), consistent across both runs.

**Still open — the wall-clock.** On that small export `ipc` did NOT move (207 -> 213-216 ms) even
though the bytes nearly halved: at ~15 KB/response over 134 calls, IPC is dominated by fixed
per-call cost.

A **bigger lean run exists** (2026-07-23 22:09, SEED `BP_LifeGameInstance_C`, depth 4, 13,845 structs
/ 54 pointers): wall **2,086.6 ms**, 302 dispatches, split **dll 832.4 (39.9%) / ipc 704.3 / ui
549.9 ms**, and **10.16 MB of lean payload** across 241 batch + 65 single responses (~39 KB per batch
response — 4x the small run). It has **no before-side**, so it measures where the time sits now
(DLL-bound) rather than what lean saved. Two cheap ways to close it:
(a) re-run the same export against the pre-lean DLL (build 2338) for a true A/B; or
(b) export the **same object as CSX**, which goes through the same `ResolveDrilldownAsync` with
`lean:false` — caveat: CSX additionally drills object-arrays / DataTable rows, so its walk set is a
SUPERSET and the comparison is an upper bound, not an equality.
While at it, re-run the payload audit with `UE5DUMP_PIPE_LOG_FULL=1` for an untruncated sample — the
1024-char body-log cap makes the whole-payload split read a flattering 39%.

*Parent: multipipe-eval.md Phase 1 (non-blocking dispatch) needs Tier 1 to be decidable; Linie
(dev-log build 2156) already holds the cadence half.*

-----

## Speculative — pick if the active plan finishes ahead of schedule

Not yet committed to:

- **Invoke history / favorites panel** — auto-record (target, args, result) per
  invocation; one-click re-fire.
- **Dry-run-first invoke** — for never-called functions, invoke with zero/sentinel params
  first to detect a crash before committing real args.
- **CE table builder** — bundle selected pointer entries + AA scripts into a single `.ct`,
  auto-grouped by category (broader than the build-760 Interesting Funcs/Props batch).
- **Global hotkey binding** for shortlisted functions ("give 1000 gold" on Ctrl+G).
- **Property freeze — Route A (docs only)** — reuse CE XML/CSX export to land a pointer
  chain, user manually ticks Freeze in CE. Works today, no code; tradeoff is the chain
  binds to one resolved instance (breaks on respawn). Keep for one-shot static-singleton
  freezes so users don't have to wait for Route B.
