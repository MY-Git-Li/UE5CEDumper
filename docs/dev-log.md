# Dev Log

Append-only milestone history, newest first. Each entry references a
build number from `build_number.txt` so commits can be cross-referenced.
**Reading tip:** grep `^## ` for the index, then read the top (newest-first).
Entries for **builds ≤1799** are archived: builds 1178–1799 in
[archive/dev-log-2026-06-pre-build-1800.md](archive/dev-log-2026-06-pre-build-1800.md),
builds 939–1177 in
[archive/dev-log-2026-06-pre-build-1180.md](archive/dev-log-2026-06-pre-build-1180.md),
builds 715–937 in
[archive/dev-log-2026-06-pre-build-940.md](archive/dev-log-2026-06-pre-build-940.md),
builds ≤696 in
[archive/dev-log-2026-05-pre-build-700.md](archive/dev-log-2026-05-pre-build-700.md).

> **Looking for current state?** See [roadmap.md](roadmap.md) for the
> capability matrix / per-game configuration / tested games, and
> [todo.md](todo.md) for the prioritized next-work list. This file
> records *what shipped* — the other two record *what works now* and
> *what's next*.

-----

## 2026-07-29 - UE 4.10 + stock 5.4.4 join; 58 -> 70 programs; the GNames bisection closes at 5.4 (build 2505)

Same-day follow-on to build 2503. Two things landed: the full sweep that 2503 could not run, and
the corpus's oldest binary.

### UE 4.10.4, and why its ❌ is the point

`UE410_Game_Shipping` + `UE410_Game_Development`, both full-PDB, both Epic-stock (CL 2872498,
`++depot+UE4-Releases+4.10`, `IsLicenseeVersion=0`). **Nothing was compiled** — 4.10 needs VS2015,
which is not installed. The launcher engine already ships monolithic `UE4Game-Win64-Shipping.exe`
(38.7 MB) and `UE4Game.exe` (83 MB) with PDBs in `Engine/Binaries/Win64`.

That generalises, and it is now step `00` of the derivation recipe: **check for the engine's own
prebuilt game targets before packaging or compiling anything.** Surveyed across the installed
engines — 4.23 / 4.27 / **5.4** / 5.7 / 5.8 ship all three configs, 4.10 / 4.15 ship Shipping +
Development. The caveat is that these are content-free engine defaults: fine for engine globals,
useless for the gameplay-feature matrix, which still needs a packaged Character-based project.

**GObjects is unresolvable on both rows, and it is meant to be — the only ❌ in the matrix.** It
converts "below 4.11 is UNSUPPORTED" from an assertion into a measurement with two independent
causes:

1. *It cannot be found.* At 4.10 the array is a **function-local static behind a magic-static
   guard** in `GetUObjectArray()`; consumers reach it by `call` and never materialise the address
   inline, while all 52 `GOBJ_*` patterns are `lea reg,[rip+GUObjectArray]`-shaped. 4.11 promoted
   it to a plain global — which is exactly why 4.11 Nekopara resolves one row below. Measured: 74
   GObjects candidates on Shipping / 105 on Development, with the true VA and its `+0x10` alias in
   **neither list at any rank**.
2. *It could not be read anyway.* Per `4.10.4-release` source, `TUObjectArray` is
   `TStaticIndirectArrayThreadSafeRead` and **`FUObjectItem` does not exist** — elements are bare
   `UObjectBase*`. No `ArrayLayout` preset models that.

So mining a `GetUObjectArray`-shaped pattern would buy nothing. GNames/GWorld/GEngine resolve
normally, so the rows still earn their scan as the oldest coverage for those three.

Truth for GObjects came from disassembly (no public symbol exists): the guarded init does
`lea rbx,[rip+X]`, passes rbx as `this` to `??0FUObjectArray@@QEAA@XZ` and returns rbx —
corroborated by `GetObjectArrayForDebugVisualizers`, which is literally `GetUObjectArray();
add rax,0x10` and therefore **measures** `ObjObjects@+0x10` at this version instead of inheriting
it. `pdb_globals.py` now prints that route as a hint when GObjects has no symbol, so the dead end
is not rediscovered; both of its validation rows (4.23-Flying, 5.8-StackOBot) still reproduce
byte-for-byte.

### Stock UE 5.4.4 — the last UE5 version without a symbolised oracle, and it closed the bisection

`ThirdPerson54_{Shipping,Development,DebugGame}`, Epic stock (CL 35576357, `++UE5+Release-5.4`,
`IsLicenseeVersion=0`). Packaged rather than taken from the engine's prebuilt `UnrealGame` target,
deliberately: the prebuilt one is free but content-free, so it cannot serve the gameplay-feature
matrix. These binaries serve both jobs. All five targets double-derived on all three configs
(`pdb_globals.py` + 151-pattern replay, exact agreement).

**The non-Shipping GNames bisection is CLOSED — the edge is 5.3 → 5.4:**

| version | non-Shipping GNames | lands on | wasted |
|---|---|---|---|
| 5.3 Dev + DebugGame | **15/15 patterns correct** | `GNAM_ES53_1` | **0** |
| **5.4.4 Dev + DebugGame** | **1/6** | `GNAM_V1` | **2,240** |
| 5.7.4 DebugGame | 1/8 | `GNAM_V1` | 2,199 |

Budgeted at two installs (5.4 *and* 5.6); cost one, because 5.4 collapsed outright instead of
landing mid-interval. 5.5/5.6 lose their bisection argument and are now coverage-only. If a fix
pattern is ever mined, **5.3-vs-5.4 is the pair to mine it against**.

Two more results it was actually ordered first for:

* **Every UE5 version now has a symbolised oracle.** The new Shipping row also **corroborates
  UE5.4-Elliot**, whose truth is disassembly-derived with no PDB — GObjects 8/15 vs 9/15, GNames
  13/16 vs 13/17, GWorld 15/16 vs 13/14. First independent check that row has ever had.
* **MindsEye has a stock control at last.** The engine is **5.4.4, MindsEye's exact patch
  version**, so `mindseye-fork-notes.md`'s "the fork changed X" claims become measurable deltas
  instead of inference.

### The backup measurement that overturned the manifest plan

`D:\UE_Analyze_Data\Game Binary backup` (30 games / 11 GB) hashed against the manifest's
import-time `binary_md5`: **24 rows byte-identical to the corpus build, Palworld included.** With
`Game archive` and `Varies Version builds` covering the archive and self-built rows, the manifest
splits **33 rows with 2 copies / 3 with 3 / and just 2 with none.**

That reverses the earlier recommendation to add `--merge` to `build_corpus_manifest.py` before
regenerating. `steam_buildid` is a **last-resort** route, and 36 of 38 rows never need it — losing
one only matters if the bytes are also gone. The two that qualify are the `UE5.5-Everspace2{,b}`
same-appid pair, and both are already preserved in `corpus-provenance.tsv` as hand-resolved
`STEAMDB-MANIFEST` entries. Regenerating is safe; `--merge` is a nice-to-have because the nulling
is *silent*, not because data is at risk.

### Manifest regenerated — and `duplicate_copies` turned out to lie on drifted rows

`build_corpus_manifest.py` re-run: **38 → 57 entries**, `preflight.py` **DRIFT → `GO (exit 0)`**,
`pdb gaps 19 → 4`, `wrong build 1 → 0`, `unknown 16 → 0`. Cost was exactly the predicted one row —
Palworld's `steam_buildid` / size / sha256 nulled, all three already in `corpus-provenance.tsv`.

The before/after diff caught something the generator's own output cannot show, since it prints only
`N tags -> path`: for Palworld it wrote **"the `.rep` is the last copy"** and `duplicate_copies: []`,
when two byte-identical copies of the corpus build exist.

**The first diagnosis of that was wrong, and the correction is the interesting part.** It was not
"compares today's bytes instead of `binary_md5`" — the md5 test has always used Ghidra's
import-time hash. The cause was a **size prefilter** sizing candidates against whatever sits at
`binary_last_seen` *today*; on a drifted row that is the replacement build, so the surviving copy
(old size) was skipped **before its md5 was computed**. A fast path whose guard assumed "the file
on disk is the corpus build" — false precisely where the field matters. Fixed with
`size_prefilter=(state == 'MATCH')`; Palworld goes **0 → 2** copies. Unlike a nulled field, which
reads as *unknown*, `[]` plus that note was a positive false claim.

### PE link timestamps: real below ~5.3, `/Brepro` content hashes above it

Asked whether a build's own time could stand in for file mtime (which dates the copy, not the
build). Measured: **4.10 / 4.15 / 4.23 / 4.27 / 5.1 carry REAL link times** (4.10.4 reads
`2016-02-19`), while **5.3 / 5.4 / 5.7 / 5.8 are `/Brepro`** — the field holds a content hash.

A plausible date proves nothing: ~1 in 5 hashes lands in a 2000-2030 window, and the corpus holds
two traps — 5.7 StackOBot reads `2022-09-28` (before 5.7 existed) and 5.4 Development reads the
very day it was checked. Discriminators that work: **cross-config spread** (configs of one build
link minutes apart; 5.4 Shipping vs Development are 393,153,207 s apart) and sanity against the
engine's release. Conclusion recorded in todo.md: corroborating signal on <=5.1 only, never alone,
and for "is this the same build?" use `binary_md5` or a PDB's CodeView GUID instead.

### `tools/pe/pdb_match.py` — "can I trust this PDB for this binary?"

A matching filename proves nothing: a PDB from a different build of the same game loads without
complaint and yields plausible-looking wrong addresses. The tool compares the PE's CodeView
**GUID + Age** against the PDB's info stream (fresh GUID per link, so a rebuild cannot fake it),
then checks the publics stream is not a stripped shell. `--scan` walks a backup tree.

Self-tested both ways before use — a known-good pair passes, and the 5.4 Shipping exe against the
5.4 Development PDB is correctly rejected. (The first run failed on a known-good pair and caught a
real bug: `IMAGE_DEBUG_DIRECTORY.AddressOfRawData` @20 was being used as a file offset instead of
`PointerToRawData` @24, which silently reads as "no CodeView record".)

Applied to `Game Binary backup`: **9/9 pairs valid**, and 6 of the 7 that are corpus oracles
reproduce their recorded `GS_TRUE` byte-for-byte. The 7th (Solarpunk) differs only cosmetically —
`GObjects=A|B` is a set, not an ordered pair, and its GNames legitimately has no `pdb_globals`
route. Recorded in tools/README.md: **the strongest PDB check is reproducing a row you already
have**, not the pairing test.

### The full sweep — 65 programs / 52 oracles, UE 4.10-5.8 (70 / 55 after 5.4)

54 rows, run end to end for the first time since the 5.3 / 4.15 / 4.27.2 additions. The 5.8.1
Development row that build 2503 left blocked on a Ghidra lock is included.

* **The non-Shipping GNames collapse is bisected to 5.4/5.5/5.6.** The 5.3 Development and
  DebugGame rows land on `GNAM_ES53_1` with **zero** wasted validations — cleaner than 4.23/4.27 —
  while 5.7.4 / 5.8.0 / 5.8.1 / Titan fall through to `GNAM_V1` after 2,199 / 2,369 / 2,372 /
  2,424. 4.10 and 4.15 extend the healthy band downward, so it is a sharp UE5-era edge, not drift.
* Two documentation caveats discharged: `UE423_Flying-Win64-DebugGame` is a live sweep row rather
  than an orphan `.rep`, and `ISDefenseEditor_UE410` is no longer the sole evidence for the
  pre-4.11 floor — both noted in the corpus-preservation drop list.

-----

## 2026-07-29 - Ground truth without Ghidra; corpus 51 -> 58 programs; three documented beliefs corrected (build 2503)

A corpus/tooling pass, not a feature one. The through-line: **almost everything this project uses
Ghidra for does not need it**, and once that was measured the corpus grew, three long-standing
claims turned out to be wrong, and the sweep came back green.

### `tools/pe/pdb_globals.py` — the five globals straight out of a PDB

Standalone MSF 7.00 reader (stdlib only, same house style as `pe_imports_exports.py`). Walks the
publics stream, maps `(segment, offset)` through the PDB's own section headers, and prints a
paste-ready `GS_TRUE=` line. GNames has no usable symbol at any version, so it is recovered by
disassembling `FNameDebugVisualizer::GetBlocks` (`lea rax,[&Blocks]; ret`) and subtracting `0x10` —
printing the bytes it read, so the `-0x10` stays checkable.

**Validated before being trusted**: it reproduces the `UE4.23-Flying` and `UE5.8-StackOBot` rows
byte-for-byte, including the `GetBlocks @0x14062c010` / `48 8d 05 f9 83 82 02 c3` detail already
recorded in `sweep.sh`. Re-run those two after touching it.

The 5.8 GObjects `base|base+0x10` alias — the one manual judgement call — is now **auto-detected**
from `??1FFieldClass@@UEAA@XZ` (`U` = virtual dtor = 5.8+), the one-second test GROUND-TRUTH.md
already documented. Verified across five eras. Also made the module importable (`if __name__ ==
"__main__"`), which is what enabled the offline function-body check below.

This replaces step 2 of the "Deriving truth for a new game" recipe — a ~10-minute headless run per
binary — with about two seconds.

### AUTO ANALYZE IS NOT NEEDED FOR THE SWEEP, and it is ~88% of a `.rep`

`scan_patterns.java` touches only `getMemory()` / `getBytes()` / `getImageBase()` — no
`FunctionManager`, no `Listing`, no `SymbolTable` — which is why `sweep.sh` already passes
`-noanalysis -readOnly`. Measured on one 49 MB binary:

| | `.rep` size |
|---|---|
| `-noanalysis` import | **169 MB, 46 s** |
| fully analysed | **1,369 MB** |

**8.1x — analysis is 88% of the artifact the sweep never reads**, and `scan_patterns.java` returns
the *identical five verdicts* from the raw import. Of the 15 Ghidra scripts, 5 need no analysis; the
other 10 are all *read-the-code* pattern-mining tools — analyse into a throwaway project and delete
it. A 5.8 DebugGame project had previously been deleted because auto-analyze would not finish, when
the import never needed it. Written up in [corpus-preservation.md](corpus-preservation.md).

### Corpus 51 -> 58 programs / 36 -> 43 oracles, and the sweep is green

Imported with `-noanalysis` and each verified post-import: 4.23 DebugGame, 4.27.2 x3 configs
(Development / DebugGame / Shipping), 5.7.4 DebugGame, 5.8.0 DebugGame, 5.8.0 Titan DebugGame,
plus rows for 5.8.1 Shipping + Development. Every value double-derived (`pdb_globals.py` + a
151-pattern byte replay) with zero contradicting candidates.

**`Every target present in every oracle resolves to the correct address.`** The `GOBJ_DI427_1`
demotion (105 -> 256, below) is verified neutral: no lander moved, the band audit came back empty.
Two rows did not run — `UE5.8.1-StackOBot{,Dev}` were LOCKED by an open Ghidra session, which
`preflight.py` flagged in advance as exactly those two.

This also **closes the "verify the corpus after moving it to the HDD" item** — a full sweep is the
only real integrity check for a silently corrupted `.rep`, and every pre-existing oracle still
resolves on the same landing pattern. Scope it honestly though: `aggregate_sweep.py` overwrites
`REPORT.md`, so this was a comparison against the rows read earlier in the session, not a literal
file diff against the pre-move report.

### `GOBJ_DI427_1` demoted 105 -> 256: it is a build-config fingerprint, not a UE 4.27 one

Proven by a controlled A/B the corpus could not do before — one project, three configs, one engine:

|  | `DI427_1` | `DI427_2` | `DI427_3` |
|---|---|---|---|
| 4.27.2 Development | 832 | 1415 | 246 |
| 4.27.2 DebugGame | 832 | 1415 | 246 |
| **4.27.2 Shipping** | **0** | **0** | **0** |

`_1`/`_2` anchor on the `E8 <check-fail>; nop; int3` that `check()` emits; `_1` additionally needs
the 32-byte `TStatId` `FUObjectItem` (`STATS` is 0 in Shipping). `_1` fires on **1 of 51** programs
and is never selected, so it should not hold a batch-1 slot every shipped game pays to scan. `_2`
(5 programs) and `_3` (4) do reach real Shipping builds — Satisfactory ships with checks on — and
keep their bands.

### THREE CORRECTIONS, all to things this repo asserted confidently

1. **`UCheatManager::God`/`Fly`/`Ghost` are NOT body-stripped in Shipping.** Read the bytes at
   `?God@UCheatManager@@UEAAXXZ` out of a stock 4.27.2 *Shipping* EXE: a full body, not a `ret`.
   `CheatManager.cpp` @ 4.27 has exactly one `#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)` block and
   it wraps `TickCollisionDebug`, not the cheat commands. **The real gate is that no
   `UCheatManager` INSTANCE exists** — `AddCheats` spawns one only when `AGameModeBase::AllowCheats`
   (`NM_Standalone || GIsEditor`) permits, so the invoke lands on the CDO and `GetPawn()` yields
   nothing. Same symptom, different cause; and the fix is not "give up" but "you need an instance",
   or do what `Solitar` already does and set `bCanBeDamaged` by reflection — which is literally what
   `God()` itself does.
2. **GNames on a non-Shipping UE5 build.** Called "a 5.8 thing" (5.7.4 disproved it), then "all 37
   patterns miss, n=0" (a byte-replay display showing only the top 4 candidates; the true VA had one
   voter, ranked off the bottom), then "unreachable". The sweep settled it: it **resolves**, on
   `GNAM_V1` (pri 870, 4 literal bytes) after **2,199 / 2,369 / 2,424** rejected candidates — the
   three most expensive fall-throughs in the corpus, next worst 475. Config-gated, not a version
   regression; boundary is between 4.27 and 5.7.4, and **5.0-5.6 non-Shipping is untested**.
3. **Rule 5 paid out, twice.** `SPARSE_MEL55_1` and `SPARSE_X1` are now in REPORT.md's Load-bearing
   table: `MEL55_1` is the *only* pattern reaching sparse on 5.7.4 DebugGame. All three of
   `X1`/`X2`/`MEL55_1` were added as pure redundancy against binaries that already resolved. Had any
   been pruned as dead weight, a whole build configuration would silently have lost sparse support.

### `tools/ghidra/corpus-provenance.tsv` — nothing in the corpus is unrecoverable any more

`build_corpus_manifest.py` **nulls** `steam_buildid`/`size`/`sha256` on a drifted row (correctly — it
must never assert the wrong build), which destroys the pointer to the build a `.rep` was made from
the first time a game patches. Palworld drifted that same day. This snapshot preserves it, with four
recovery routes: 22 `STEAMDB-BUILDID`, 6 `STEAMLOG-MANIFEST` (Steam's `console_log.txt` records every
depot fetch with its exact manifest; an archived file's mtime falls inside its download), 4
`STEAM-BACKUP-MANIFEST` (`sku.sis` in a Steam backup — exact, and survives delisting), 4 `REBUILD`,
2 `STEAMDB-MANIFEST`. **`NONE-HASH-ONLY`: 0.**

Two traps recorded: **use `file_modified`, never `file_created`** (a copy resets ctime but preserves
mtime, so install->copy->uninstall keeps the original Steam write time — reading ctime made the dates
look destroyed by the corpus move when they were not); and `console_log.txt` **rotates**, so its ten
surviving records were transcribed into the doc.

### Also

* **Palworld patched mid-session** and it answered "does a pattern survive a game update?" better
  than the ES2 cross-build pair could: every global moved (+0x3300, sparse +0x3180) and **not one
  pattern broke** — all six voter sets came back character-identical.
* Himmel.h header regenerated (158 entries, 151 AOB, 31 source tags — it was four short), corpus
  paragraph refreshed, and a **UE 5.8 layout note** added: 5.8 moved `ObjObjects` +0x10 -> +0x00, so
  the version-fixed `adjustment` patterns encode pre-5.8 arithmetic. `New patterns:` headers lost the
  meaningless `New`.
* The 4.23 sparse-delegate comment said "only 4.23 itself is unverified, and no 4.23 binary is in the
  corpus" — stale since 2026-07-28. Also recorded **why there is no `SPARSE_EXP`**: the symbol exists
  in modular builds but its mangled name embeds the whole template argument list and differs on all
  three engine versions measured, so an exact-name `GetProcAddress` cannot work.
* README UE badge `4.18-5.8` -> **`4.11-5.8`**; `winmm.dll` documented as the spare proxy slot for
  when `dxgi`/`version` is taken; CLAUDE.md "3 proxy DLLs" -> 4.
* [aob-block-library-eval.md](aob-block-library-eval.md) — evaluated, not built. The copyright
  question is sidesteppable because every finding this session came from the **self-built** tier.

### Same day, later: 5.3 + 4.15 config groups — sweep.sh 47 -> 52 rows

Five more rows, all `-noanalysis` imports, each corroborated by running `scan_patterns.java`
against its derived truth immediately after import.

**UE 5.3 ThirdPerson x3 configs — 5.3's FIRST symbolised oracle.** Until now the only 5.3 binary
was Avowed: no PDB, truth for 1 of 5 targets, so GObjects/GNames/GWorld/GEngine had *zero* ground
truth at 5.3. It was picked to bisect the non-Shipping GNames collapse and **it did**: the two
non-Shipping rows land GNames on `GNAM_ES53_1` **UNIQUE-OK** — no fall-through at all — and sparse
on `SPARSE_ES2_1`. So the collapse starts **after 5.3**, and the open interval shrinks from
5.0-5.6 to **5.4 / 5.5 / 5.6**. Next bisect is 5.5.

**UE 4.15.3 Development + DebugGame** — the oldest config group in the corpus, anchoring the far
end. Both resolve all four applicable targets (SparseDelegates absent by design pre-4.23);
GNames lands on `GNAM_SAT422_1`, GWorld on `GWLD_FD_1`.

**`pdb_globals.py` gained a pre-4.23 GNames route**, which it previously could not do at all —
`FNameDebugVisualizer` does not exist before 4.23, so it now falls back to `FName::GetNames` and
takes the RIP load at **+4 with NO `-0x10`** (that adjustment is an FNamePool/Blocks artifact and
applying it here lands 16 bytes low). Validated the same way as everything else: it reproduces the
UE4.15-Flying Shipping row's recorded `GNames=142c92508` exactly before being used on the new two.

**`UE5.8.1-StackOBot` (Shipping) swept** once Ghidra released its lock — all five targets, matching
the offline derivation. Only `UE5.8.1-StackOBotDev` is still outstanding.

**A C++ project was NOT needed for any of this, and the failure that prompted the question is worth
recording.** A C++ project on the 5.3 launcher engine dies in UBT with *"must be compiled with
Visual Studio 2022 17.4 (MSVC 14.34.x) or later … The current compiler version was detected as:
14.29.30159"*. The message blames the VS version and a forced `VisualStudio2019` setting; both are
wrong here. From UBT's own log it is **toolset ranking**: UE 5.3 predates 14.44/14.50/14.51 so it
ranks all of them `FamilyRank=4` ("unknown"), while the one family it recognises — 14.29, supplied
by **VS2026's v142 component**, not by any VS2019 install — ranks 3, wins, and then fails the
`>= 14.34` gate. VS2022's 14.44 was perfectly usable and lost for being too *new* to be in the
table. Nothing needed fixing: the launcher ships `UnrealGame{,-Win64-DebugGame,-Win64-Shipping}.exe`
**with PDBs**, so a Blueprint-only project packages all three configs with nothing compiled.

-----

## 2026-07-27 - GWLD_FD_1: the GWorld fall-through list is now empty (build 2478)

Landed the `UWorld::FinishDestroy` GWorld pattern mined at the end of the 4.11/4.13 support pass.
It was held back deliberately — `GROUND-TRUTH.md`'s own rule requires a full 46-program sweep
before any pattern change, and the priority placement was undecided.

```
48 8B 05 ?? ?? ?? ?? 48 3B C? 48 0F 44 C? 48 89 05 ?? ?? ?? ?? E8   pri 265, io 0
```

22 bytes, 12 fully-literal. The shape is a **read of a global followed by a conditional write-back
of the same global**, which is self-evidencing — that, not the length, is why it is clean. Source
PDB-confirmed on three independent oracles (HeliumRain 4.20, DropIn 4.24, DropIn 4.27).

### Measured over the full sweep: 46 programs / 32 oracles

**21 hits, 16 UNIQUE-OK, zero decoys anywhere**, never more than 1 hit on any binary. It appears
in neither the hotspot table nor the dead-weight table, and the band audit stays clean.
It became the lander on four binaries — three improvements, one lateral, **no regressions**:

| binary | before | after |
|---|---|---|
| UE4.11 Nekopara | `GWLD_G42_1` (880), 5 wasted | `GWLD_FD_1`, **0 wasted** |
| UE4.13 Fantasynth | `GWLD_G42_1` (880), 6 wasted | `GWLD_FD_1`, **0 wasted** |
| UE4.26 Satisfactory Engine | `GWLD_SF_2` (300), 2 wasted | `GWLD_FD_1`, **0 wasted** |
| UE5.2 Satisfactory Engine | `GWLD_SF_2` (300), 0 wasted | `GWLD_FD_1`, 0 wasted |

Those were the *only* three GWorld entries in the report's fall-through list, so **that list is now
GObjects-only**. GWorld redundancy rose by one on 13 oracles and fell nowhere. `GWLD_SF_2` is no
longer the lander anywhere but still reaches truth on both Satisfactory DLLs, so it stays as
redundancy (never prune on "no proof", only on counter-proof).

### Why the sweep understates what this fixes

The *baseline* sweep already showed 4.11 and 4.13 resolving GWorld correctly. That is the harness
model, not the runtime: `scan_patterns.java` has the truth and walks past a decoy, whereas the live
`ValidateGWorldBasic` is deliberately loose and accepts the first one it is handed. In-game both
titles were landing on a **wrong** GWorld — Nekopara via `GWLD_SAT52_1` → `1423C9940` (a
`TSharedPtr {Object, ReferenceController}` singleton whose `+0` reads like a UWorld pointer),
Fantasynth via `GWLD_SF_2` → `14288E648` — and were rescued only by instance-scan recovery.

At 265 the new pattern is scanned **ahead of both** (`SF_2` 300, `SAT52_1` 365), so the true GWorld
is validated and returned before either decoy is ever presented. This is the shape the maintainer
asked for when declining to tighten the validator: *add a pattern, do not touch something 30+
oracles depend on to fix one 2016 title.*

### Placement: 265 over a Tier-1 slot

`GWORLD_PATTERNS` went 49 → 50 byte patterns, i.e. 6 full batches + 2 either way — **no batch
boundary moves**. At 265 it lands in batch 3, leaving batches 1–2 byte-identical, so nothing that
resolves off the modern Tier-1 block could be perturbed at all. Tier 1 (~102) is defensible on the
raw numbers (16 UNIQUE-OK / 0 decoys beats `GWLD_TQ_1`'s 10) but the existing Tier-1 block has not
been re-measured corpus-wide, so what a promotion would displace is unknown. Recorded in
`Himmel.h`, not decided by taste.

Also corrected the pattern-count summary block in `Himmel.h`, which had drifted 4 short
(`SPARSE_AV53_1`/`X1`/`X2` were never added to it): now **150 AOB + 1 CallFollow + 6 symbol
exports = 157 entries**, with a note to regenerate it from `extract_patterns.py` rather than
hand-edit.

-----

## 2026-07-27 - Five new oracles close the 4.21 and 5.0 holes; GWLD_TQ_1 promoted 210 -> 101

Five games added — Helium Rain (4.20.3, PDB), Freud Gate (4.21, no PDB), Breeders of the Nephelym
(4.27, PDB), Maelstrom (4.27.2, PDB), Light Maze (5.0.3, no PDB). Derived in parallel, five agents
on five projects (Ghidra's lock is per-project, so that is safe).

**23 of 23 targets resolve correctly, zero version disagreements, and nothing justified mining a
new pattern.** No repeat of the Elliot "PE says 4.27, actually 5.4" trap — every version was
confirmed independently from the `++UE4+Release-X.Y` build tag and refined where the label was
coarse (4.20.3, 4.27.2, and Light Maze's `CL-20979098` = the 5.0.3 release changelist).

### `GWLD_TQ_1`: 210 -> 101

Measured before moving. It wins on **6 of 16** oracles — no other GWorld pattern wins more than 2 —
and has **zero decoys anywhere**: 10 UNIQUE-OK, 6 NO-TRUTH on probes, 23 MISS. It was sitting
behind 13 AOBs.

The saving is **a whole `.text` pass, not a few validations**. Patterns scan in **batches of 8**,
so order *within* a batch only changes validation order — one AVX2 sweep either way — but crossing
a batch boundary costs an entire extra sweep. At 210 it sat in batch 2, so every game it wins paid
for batch 1 first. What it displaces out of batch 1 is `GWLD_ES2_3`, which wins on nothing, so the
swap is free. Placed at 101 rather than 95 because the 40–90 band means "symbol-derived", and
first-vs-second inside a batch is worth nothing.

Then the five new games arrived and `GWLD_TQ_1` won **all five** — 4.20, 4.21, 4.27 ×2, 5.0. The
promotion is now backed by 11 wins across five engine generations.

Bundling the reorder with the corpus additions was safe, and worth stating why: **scanning is
per-program independent**, so adding rows cannot change another program's result. Any GWorld
change on an *existing* oracle is attributable to the reorder alone; the new games are new
information regardless.

### What the batch settled

- **DropIn's 32-byte `FUObjectItem` is a config artifact, not a 4.27 trait** — proven by two
  independent symbolised 4.27 binaries carrying the stock 24-byte item.
- **`SPARSE_PAL51_1` fires and is CORRECT on Maelstrom (4.27)** — its first correct fire outside
  Palworld, and on a non-5.1 binary. It stays "provenance ≠ version coverage", but it is no longer
  a pattern that has only ever worked on the game it was mined from.
- **`SPARSE_X1`/`X2` are UNIQUE-OK on Maelstrom** — second corroboration outside 5.1.
- **`GENG_X4` is clean on four of the five** and takes 1 decoy on Breeders that is never selected.
  DQ7R stays the only place it is convergent-and-wrong.
- A reusable recipe for pre-4.23 GNames without symbols (`mov ecx,0x408` → the nearby rip store),
  which is a live lead for the three 4.18 rows that leave GNames unset on purpose.

### The thinnest thing in the table now

**Pre-4.23 GNames rests on exactly two patterns, and they are the same shape** — `GNAM_CT3` and
`GNAM_G42_1`, both the `FName::GetNames` lazy-init prologue, both OK-BEHIND, both batch 3,
confirmed identical on Helium Rain *and* Freud Gate. That is the sparse-`n=1` situation again on a
different target. If a third pre-4.23 sample ever arrives, mining a structurally different anchor
is the highest-value thing to do with it.

### UE 4.23 — closed as a deliberate non-goal

It shipped 2019-09 and 4.24 landed that December, so essentially every surviving title has been
bumped to 4.27, and building a sample needs an old Visual Studio the maintainer will not install.
It is also the version where the feature matters least — sparse delegates were barely adopted that
early, so an unverified 4.23 is close to unobservable. The mitigation was never going to be a
sample anyway: **`Aura` probes the live key shape instead of gating on a version number**, which is
what makes 4.23 *and any licensee fork* safe without a binary to test against.

### Version coverage

4.18, 4.20, **4.21**, 4.22, 4.24, 4.25, 4.26, 4.27, **5.0**, 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7 —
contiguous from 4.24 up, with 4.19 the only remaining UE4 gap (sandwiched between covered
neighbours) and 4.23 deliberately skipped. 5.8 is next, and the practical route is packaging a
Blueprint template for **Shipping** from an Epic Launcher engine install — installing the engine
alone yields Editor binaries, which are the wrong shape entirely.

-----

## 2026-07-27 - Grimhook: the first symbolised UE 5.1; sparse n=1 cluster closed

Grimhook ships a **full public PDB** on a `-Win64-Shipping.exe` (2.7 M symbols, 232 K functions).
Until now the corpus's only 5.1 was Palworld, which has no symbols — so every 5.1 claim rested on
consensus. All five globals read straight off the PDB, and the version was confirmed
*structurally* rather than from the label: the PDB's `EUnrealEngineObjectUE5Version` terminates at
`ADD_SOFTOBJECTPATH_LIST = 1008`, which is exactly 5.1 (5.0 stops at 1004, 5.2 adds 1009). Stock
layout — 24-byte `FUObjectItem`, `UObject*` at `+0x00`, chunked.

### What it settled about Palworld

Different binary, so the addresses cannot match — what transfers is which *sets* of patterns
converge. They match almost exactly, which corroborates Palworld's derived values:

- GEngine: the identical 4-set `[X1,X2,X3,X4]` converges on truth here and on `149657F38` there.
- GNames: the identical **12**-pattern set converges on truth here and on `14944DB80` there.
- GObjects: the base gets one 6-set on both and `+0x10` a different 5-set on both — so Palworld's
  base/`ObjObjects` split was the right way round.
- Sparse: `SPARSE_ES2_1` is now *proven* correct on real 5.1, and it is what hit Palworld.

And three patterns that had never been checked against 5.1 symbols are now proven: `GOBJ_V13`
(136 hits, 136 ok), `GNAM_V8` (the priority-100 winner), `GWLD_V7` (its second oracle after
Meltopia).

**One falsification.** `SPARSE_PAL51_1` takes **0 hits** on Grimhook. It is not a generic UE 5.1
shape — it is Palworld-specific inlining. A MISS is not counter-proof so it stays, but its
`PAL51` tag must not be read as "covers 5.1".

### The n=1 cluster — closed except Avowed

Six binaries reached SparseDelegates through `SPARSE_ES2_1` and **nothing else**; a patch moving
that one site would have taken sparse support with it. `SPARSE_X1` / `X2`, mined here, anchor on
`Remove`/`RemoveAll`/`Clear` — different *functions* from `ES2_1`'s `NotifyUObjectDeleted`, so
this is real redundancy, not a re-anchor on the same instruction stream.

| binary | patterns reaching truth |
|---|---|
| Everspace 2 5.5 / 5.5b | 1 → **2** |
| Satisfactory 5.2 / 5.6 CoreUObject, CrashReportClient 5.6, Grimhook 5.1 | 1 → **3** |
| Avowed 5.3 | 1 → 1 — **now the only n=1 left** |

Both are decoy-free across 39 programs including 8 monolithic EXEs up to 414 MB of `.text`.
No binary that currently fails starts working; this is insurance, on the same footing as
`PAL51_1` / `MEL55_1` / `AV53_1`.

### The adversarial pass earned its keep — twice

**X1 was refuted as submitted and shipped shorter.** Its mined form ended with one more
`48 8D 0D` (the `GUObjectArray` ref). Measured, those 3 bytes are inert on 36 of 38 programs — and
they *cost* both Everspace 2 5.5 builds, because 5.5 emits `lea rdx,…; call` with no second `lea`.
Since ES2 5.5 is one of the exact `n=1` binaries the pattern exists to fix, **the longer form
failed at its own purpose.** Longer is not safer; it is only safer where the extra bytes are
load-bearing. This is the mirror image of the `GWLD_G42_4` finding, where wildcarding *more* was
the mistake.

**The `instrOffset` trap was demonstrated, not just asserted.** X2 needs `instrOffset = 11`. A
deliberate wrong-value control at 26 resolves to `SparseDelegateObjectListener` — a plausible
adjacent global 8 bytes below truth — and goes DECOY-ONLY on all 15 binaries *while the hit count
stays healthy*. That is exactly the silent failure rule 7 warns about, now with a worked example.

### Build quirks worth remembering

- `.rodata` is marked **executable** here (2 KB) — every other corpus binary has it non-exec, so
  "exec bytes" for Grimhook is `.text` + that.
- A `.msvcjmc` section is present: MSVC `/JMC` instrumentation on ~512 functions, which adds a
  `call __CheckForDebuggerJustMyCode` prologue. It disturbed nothing here, but it is the kind of
  thing that would shift a prologue-anchored AOB in a game that enabled it globally.
- `GNameBlocksDebug` **is** symbolised (`0x14632A4D0`) and is a **trap**: it is a separate pointer
  variable, not `NamePoolData+0x10`, and it is all-zero in the file. Recorded in GROUND-TRUTH.md
  so nobody takes the shortcut on a future PDB game.

### Version coverage after this

4.18, 4.20, 4.22, 4.24, 4.25, 4.26, 4.27, **5.1**, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7. The named holes
are **4.23** (still the only unverified sparse-delegate version — mitigated because `Aura` probes
the live key shape rather than gating on a version number) and **5.0** (bracketed by 4.27 and a
now-symbolised 5.1, so low risk). 4.19 / 4.21 sit between covered neighbours.

-----

## 2026-07-27 - UE_GameEngine binds the &GEngine slot instead of a frozen pointer (build 2453)

The Teleport tab's Global Pointers card exported two CE symbols with asymmetric backing:
`UE_GWorld` bound directly to the stable `&GWorld` slot (auto-follows), but `UE_GameEngine` was an
`allocateMemory(8)` buffer holding a `UEngine*` **snapshot**. That asymmetry existed for one
reason — `&GEngine` could not be resolved — and that stopped being true today.

- **DLL:** new `QUERY_OP_GENGINE_SLOT = 2` returns the SLOT (`g_cachedGEngine`) plus its deref,
  same shape as `QUERY_OP_GWORLD`. Op 1 still returns the live instance.
- **Script:** `UE_GameEngine` now asks for the slot first and registers the symbol straight to it.
  Only when no GEngine AOB validated does it fall back to op 1 + the buffer.

**The choice is made at ENABLE time, not at generation time.** A CE record gets saved into a `.CT`
and re-enabled in later sessions, where the AOB may resolve even though it did not when the record
was created. Baking the decision in when the script is generated would make the artifact silently
wrong later — and a downgrade to a frozen pointer is precisely the failure a user would not notice.

Two details worth keeping:

- **A marker symbol, not a heuristic, decides what to free.** The snapshot path also registers
  `UE_GameEngine_buf`; `[DISABLE]` frees only through that. Deciding from `UE_GameEngine` itself
  would call `deAlloc` on a game address on the slot path. The GWorld script emits no `deAlloc`
  or `allocateMemory` **at all** — a test asserts the string is absent, so a reader can see the
  record cannot free a game address without tracing logic.
- **The busy check had to become a bounded wait.** `SetDone`/`SetError` publish `status = DONE`
  **before** clearing `cmd` (deliberately). A script issuing two round-trips back to back can
  exit its status poll and still observe the previous `cmd` for an instant, so a single sample
  would report "mailbox busy" and silently abandon the fallback. The prior scripts never hit this
  because they only ever queried once. Now `MailboxIdleWaitMs = 100` ms, bounded.

-----

## 2026-07-27 - Avowed 5.3 sparse closed; 3 games added as oracles; GENG_X4 demoted in prose

### Avowed (UE 5.3) sparse delegates — found, and the fork did NOT change the structure

`FSparseDelegateStorage::SparseDelegates = 0x14B5BD9A8`. This had been an open "zero hits" line
in GROUND-TRUTH.md since the Avowed case study.

The route the docs suggested is dead here: **`SparseDelegateReport` does not exist in the binary
at all** (the `!UE_BUILD_SHIPPING` console command is compiled out), verified against every
initialized block in both ASCII and UTF-16. Found structurally instead — scan `.text` for TSet
element-stride arithmetic adjacent to a rip-relative `.data` reference, bucket by global, and
take the one with a pure 0x60-stride profile. Corroborated by `SparseDelegateMapCritical` sitting
exactly `0x28` (`sizeof(CRITICAL_SECTION)`) below it: the two statics of `SparseDelegate.cpp`,
adjacent, as expected.

**The user's question was whether Obsidian changed the sparse structure, given they changed the
object array. Answer: no, on every observable axis** — outer stride `0x60`, `TSet::HashSize` at
`+0x48`, element `HashNextId` at `+0x58`, inner `TMap` at element`+8`, inner stride `0x20` with
the value at `+8`, `PointerHash` = `ptr>>4` into the Murmur finalizer. The fork's known
deviations (packed 20-byte `FUObjectItem`, static `FUObjectArray`) stop at the object array.
Practical consequence: `ValidateSparseDelegates`' hardcoded `kOuterStride = 0x60` was already
correct for Avowed.

Three candidates were mined; **one was added**. An adversarial pass measured over 42 programs
refuted the other two:

- the twin-ref form baked in `[rsp+0x20]`, which the mining report called shadow space. It is
  not — `mov [rsp+0x20],rdi` spills the key into a frame **local**, and `DI427_1/2` encode the
  same out-param idiom with that disp8 wildcarded. One added spill in a future build takes its
  only hit to zero.
- the `mov rdx` variant is strictly dominated: a nibbled form covers its sites *and* `AV53_1`'s.
- both would push `SPARSE_PATTERNS` from 8 to 9 = **two batches** (`kBatchSize = 8`), costing a
  second AVX2 pass over ~430 MB of `.text` across the titles that find nothing in batch 1, for a
  pattern that can only ever hit Avowed.

Honest caveat recorded in the header: `AV53_1`'s head alone (14 literal bytes) measures
identically, so its tail is inert on this corpus — the selectivity is exact register allocation,
not length.

### Three games promoted from "not in the corpus" to oracles

DQ7R (4.27), The Adventures of Elliot (**5.4 — the corpus had none**) and DQ XI S (4.18, a second
pre-4.23 sample). Live-run first, then corroborated by disassembly. 14 of 15 globals confirmed.

DQ XI S's GNames is **deliberately omitted**: 4.18 predates `FNamePool`, every GNames pattern is
FNamePool-shaped, and the consensus is noise. Per the standing rule, leave it out rather than
guess a value that would mislabel every hit as a decoy.

### The one contradiction — and the rule it produced

**DQ7R's GEngine is `145FF4B28`, not the `145D76D78` the runtime log pointed at.** I had reported
that address to the user as strongly supported: 41 hits converging on one address, against a
7-hit runner-up. It was wrong. `145D76D78` is a game-side manager singleton, and `GENG_X4` alone
accounts for 50 of its 55 hits; the "runner-up" was `GENG_X1`+`X3`+`X2` — the semantically
specific patterns — agreeing on the truth. `145FF4B28` is proven three ways
(`UWorld::GetGameViewport`, `UWorld::GetRealTimeSeconds`, and a `GetWorld` fallback that loads
GEngine and GWorld in the same function) and sits `-0x3948` from GWorld, in family with DropIn
4.27's `-0x4648`.

So GROUND-TRUTH.md rule 4 gained a limit: **convergence only holds WITHIN one pattern. Across
patterns, rank by DISTINCT PATTERNS AGREEING, never by raw hit count.** `consensus_*.txt` already
does this; a hand tally of a runtime log does not.

`GENG_X4` keeps its priority (it is still what reaches FF7 Remake, and `ValidateGEngineSlot`
rejects its decoys so it costs validations, not correctness) but its note no longer claims
"correct on 7 oracles" — it now says what it is: the broadest and noisiest pattern in the table,
whose decoys are game singletons.

-----

## 2026-07-27 - Proxy Deploy CTD: bound rows were mutated from thread-pool threads (build 2445)

**Symptom:** Proxy Deploy tab → *Scan Steam* → *Update all* → the whole app disappears. No managed
exception, no error dialog, nothing after the last log line. Windows recorded
`0xc0000005` (access violation) in **`libSkiaSharp.DLL`**, i.e. inside the renderer.

The deploy itself had *succeeded* — `Updated: 26, up-to-date: 0, failed: 0` is the last line in
`view-0.log`, ~2 s before the crash. So the work was fine; the painting of the result was not.

### Root cause

`DetectedGame` is an `ObservableObject` whose `Status` / `InstalledVersion` / `ErrorMessage` /
`SuggestedProxy` are bound to the Proxy Deploy `DataGrid`. Four `ProxyDeployService` methods
(`RefreshDeployStatusAsync`, `DeployAsync`, `UndeployAsync`, `ApplyProxySuggestionsAsync`) wrote
those properties **from inside their `Task.Run` bodies**, so `PropertyChanged` fired on
thread-pool threads for 29 bound rows at once. Avalonia then mutated the visual tree while the
render thread was composing it. That is not an exception path — it is an AV in Skia, which takes
the process down with no managed stack to look at.

The codebase had already met this bug and mistaken it for a cosmetic one. From
`ProxyDeployPanel.axaml`:

> *"Marking the whole grid IsReadOnly=True caused row visuals to lag behind item PropertyChanged
> events **from the background-thread Refresh**, requiring a second click to repaint."*

The late repaint and the crash are the same race. Setting `IsReadOnly` per column instead of on
the grid fixed the visible symptom and left the thread violation in place.

### Fix — compute off-thread, apply on the caller's thread

All four methods now do their file I/O inside `Task.Run`, collect the results into a
`GameStatusUpdate` record, and apply them **after** the `await`, which resumes on the caller's
context. All 11 call sites are UI-thread `[RelayCommand]`s in `ProxyDeployViewModel` with no
`ConfigureAwait(false)`, so that context is the UI thread.

`GameStatusUpdate` carries `SetInstalledVersion` / `SetErrorMessage` flags because some paths
deliberately leave a field alone (the "already up to date" deploy sets only `Status`) and `null`
is itself a meaningful value for the other two — a blanket three-field apply would have changed
behaviour.

The contract is now written down on `IProxyDeployService` rather than left implicit, since
nothing enforced it the first time.

### Notes

- `ProxyDeployService` deliberately does **not** take a dependency on `Avalonia.Threading`. In
  this codebase `Dispatcher.UIThread` appears only in ViewModels and Views; services stay
  UI-framework-free, so the marshalling is done by *where the code runs*, not by a dispatcher call.
- Swept the rest of `Services/`: `PipeClient` and `SnapshotStore` are the only other files using
  `Task.Run`, and neither references any `ObservableObject` model. `DriveDescriptor.IsSelected` is
  read but never written off-thread. `ProxyDeployService` was the only offender.
- A UI crash whose faulting module is `libSkiaSharp` and whose trigger is a batch operation over
  a bound collection should be treated as a threading bug until proven otherwise — SOS cannot
  read the dump (Native AOT publish has no CoreCLR), so the dump is a dead end and the code is not.

-----

## 2026-07-27 - &GEngine was never resolvable: the validator ran before the offsets it needs (build 2441)

**Symptom:** the System tab reported `&GEngine — AOB not found` on *every* game. Reported against
DQ7R (4.27), The Adventures of Elliot (5.4), DQ XI S (4.18), Titan Quest II (5.6) and
Everspace 2 (5.5) — the last two being cases where the offline sweep says the patterns resolve
correctly, which is what made it obviously a code bug rather than a coverage gap.

### The patterns were right the whole time

The scan log records every candidate. On Everspace 2, **14 of 15 candidates across four
independent patterns resolved to `0x7FF68ACC37B0`** — and the PDB's `&GEngine` is image VA
`0x149DA37B0`, which at that process's load base is exactly `0x7FF68ACC37B0`. Same picture
everywhere else: DQ7R 41 hits on one address, Elliot 27, TQ2 28, DQ XI S 12. Textbook
convergence. Then every one of them was rejected.

### Root cause — an ordering contract that was documented but not honoured

`Genau.cpp`'s `FindGEngineSlot` carried a comment stating it *"MUST be called after
GObjects/GNames/offsets are up"*, because `ValidateGEngineSlot` derefs the candidate and asks the
reflected class for a `GameViewport` property. `FindPropertyOffsetByName` needs
`DynOff::USTRUCT_CHILDPROPS` / `FFIELD_NAME` / `FFIELD_NEXT` / `FPROPERTY_OFFSET` **and**
`Serie::GetString` — i.e. the dynamic offsets *and* a live FNamePool.

The call site did not satisfy it. From the Everspace 2 log:

| time | event |
|---|---|
| `12:10:01.340` | GEngine AOB scan + validation (inside `Genau::FindAll`, `Frieren.cpp:122`) |
| `12:10:02.505` | `FNamePool: Initialized` |
| `12:10:02.506` | `ValidateAndFixOffsets: Starting…` (`Frieren.cpp:319`) |
| `12:10:02.508` | `ChildProperties found at struct+0x50`, `FField::Name at +0x20` |

The validator ran **1.2 s before** the values it reads were discovered, so it walked the property
chain with compile-time default offsets and a dead name pool. Every candidate failed, on every
game, always — the feature had never worked since it was added at build 2399.

### Fix — resolve &GEngine in a second pass

- `FindGEngineSlot` now **enforces** its own precondition instead of documenting it: if
  `DynOff::bOffsetsValidated` is false it returns 0 with method `deferred` **without scanning**.
  That also stops burning the 0.2–0.7 s AVX2 pass on a scan whose result cannot be accepted.
- New `Genau::ResolveGEngineDeferred(EnginePointers&)` re-runs the scan once the offsets exist,
  republishing the pattern-id / scan-addr / AOB triple so a GameEngine-rooted CE export can still
  be AOB-wrapped (a deferred win that only set the address would have left that empty).
- `UE5_Init` calls it directly after `ValidateAndFixOffsets` and re-caches the seven
  `g_cachedGEngine*` globals the pipe serves to the System tab.
- The `apply_rescan` pipe path got the same second pass: a recovery rescan that revives
  GObjects/GNames is exactly the case where the offsets GEngine was waiting on have just arrived.

### Follow-up (same day) — the two rows that had no AOBMaker buttons

**LIVE-VERIFIED on Everspace 2**: `&GEngine (engine slot) = 0x7FF6430237B0` via `GENG_X3`.

With the address finally resolving, the Pointer panel's last two rows were still Copy-only.
`FSparseDelegateStorage` and `&GEngine` gained a **HEX** button (same contract as the three
above them), and `&GEngine` gained a **SYM** button matching GWorld's.

`SYM` registers the **slot**, not the UEngine object — which is the entire point. The slot
address is restart-stable, so a GameEngine-rooted CE record auto-follows engine recreation
instead of freezing a stale `UEngine*`. Symbol name `gengine_addr`, mirroring `gworld_addr`.
`CanRegisterGEngineSymbol` requires the AOB triple, not just an address, for the same reason
GWorld's does: without the pattern the generated AA script cannot re-scan on enable.

### Notes for next time

- The 0.2–0.7 s per game the wasted scan cost was invisible because it was folded into the
  scan-progress bar.
- Nothing was wrong in `Himmel.h`. Every GEngine pattern is fine and the target hits on 5/5 live
  games spanning UE 4.18 / 4.27 / 5.4 / 5.5 / 5.6 — **adding more GEngine AOBs would have fixed
  nothing.** A "not found" in the UI is only evidence about the *pipeline*, not the patterns,
  until the scan log's candidate list has been read.
- `RecoverGWorldViaEngine` has the same reflection dependency but is already invoked from a later
  path (`Frieren.cpp:447`, after offsets), so it was never affected.

-----

## 2026-07-26 - Stack-displacement rule codified and applied; GWLD_SF_4 coverage 4 -> 15 binaries (build 2437)

The user stated a design rule I had been applying too bluntly: **`lea rdx,[rsp+????????]` is fine
in a pattern; `lea rdx,[rsp+00000318]` is not.** The instruction form is acceptable — the literal
frame offset is not. I had read it as "avoid stack instructions" and dropped a leading `lea` from
`SPARSE_MEL55_1` for no reason.

### Why the rule is right — and what it is really about

A frame displacement encodes the **callee's frame layout**: local count, register spills, inlining
decisions, alignment. None of that is a property of Unreal Engine; it is a property of one
compilation, and it moves when a patch adds a single local.

A **struct** displacement is the exact opposite and must be kept: `cmp [rcx+0x2C0],rax` (a UWorld
member) or `cmp eax,[rdi+0x34]` (TSet Max) pin UE's real data layout, which is version-stable and
is precisely the evidence that makes a pattern trustworthy. So the rule is not "avoid stack
instructions" — it is **wildcard FRAME displacements, keep STRUCT displacements**.

Auditing the whole database for literal rsp-relative displacements found 18, splitting cleanly:
ten are small shadow-space constants (`sub rsp,0x28`, `mov [rsp+0x20],rbx`, all ≤ 0x40) which are
the idiomatic x64 prologue and stable across compilers; eight are genuinely frame-specific
(0x50–0x70), in four patterns.

**An honest note on evidence:** breadth statistics do *not* separate the two groups (frame-offset
patterns average 6.5 binaries hit / 3.5 correct vs 7.5 / 3.3 overall), and the one same-game
cross-build pair in the corpus cannot test them (all four score zero hits on both ES2 builds). The
rule stands on the mechanism, not on a correlation this corpus can show. What the corpus *can* do
is test each change directly, which is what was done.

### Measured, one pattern at a time

| pattern | literal bytes | wildcarding the frame offset | action |
|---|---|---|---|
| `GWLD_GH_3` | 22 | 5 → 7 binaries, **UNIQUE-OK and decoy-free on every one** | applied |
| `GWLD_SF_4` | 9 | 2 → 6 binaries, UNIQUE-OK on five, one late decoy on 4.27 | applied |
| `GOBJ_G42_4` | 24 | neutral — still 1/1 on Everspace 4.20 where it is the lander | applied (free build-tolerance) |
| `GWLD_G42_4` | **7** | gains 4.24 but breaks three versions to OK-BEHIND and **38 hits / 37 decoys on UE 4.27** | **rejected** |

Final coverage after the full re-sweep: `GWLD_SF_4` **4 → 15 binaries** hit and **3 → 9** correct;
`GWLD_GH_3` 9 → 12 and 4 → 6. `GWLD_G42_4` and `GOBJ_G42_4` unchanged.

That last row is the qualifier worth keeping: **wildcard the frame displacement only if the
pattern has enough other literal context.** On a seven-literal-byte pattern the frame offset *is*
the selectivity — which is itself a reason to distrust that pattern, but removing it makes things
worse, not better. Both the rule and this exception are now recorded in the band-discipline block
in `Himmel.h`.

### A bug the verification caught

Restoring the leading `lea rdx,[rsp+d32]` to `SPARSE_MEL55_1` shifted its RIP-relative
instruction from byte 0 to byte 8, but `instrOffset` was left at 0 — so it resolved off the wrong
instruction and silently dropped to **0 correct** while still reporting 3 hits. The sweep caught
it; the regression matrix did not move only because that pattern sits last at priority 160.
This is the characteristic failure mode of an `instrOffset` mistake: hits look healthy, the
resolved address is garbage. Fixed to `instrOffset = 8` and re-verified (1 correct, and the three
binaries it hits are unchanged).

Full 35-program re-sweep after every change: every target on all 20 oracles still correct.
Build + tests green.

-----

## 2026-07-26 - Sparse-delegate coverage audit: a second 5.5/5.6 anchor; FF7 Rebirth answered but not fixed (build 2432)

Prompted by "should FF7 Rebirth get insurance AOBs, and does it even have FSparseDelegateStorage?"
Auditing that produced a corpus-wide finding worth more than the original question.

### SparseDelegates is the systematically weakest target

Counting anchors per binary showed **eight** binaries resolving sparse through exactly ONE
pattern (`SPARSE_ES2_1`), spanning UE 5.2 / 5.5 / 5.6. Many other `n=0` rows are *correct* —
pre-4.23 engines have no sparse delegates (FF7 Remake 4.18, Everspace 4.20, Satisfactory 4.22,
Octopath), and the modular DLLs where it does not live. The genuine gaps were the `n=1` band plus
**Avowed 5.3 and FF7 Rebirth**.

`n=1` matters more here than for other targets: `ValidateSparseDelegates` can only range-check
two ints, so unlike the GObjects/GNames/GWorld/GEngine validators it cannot reject a wrong hit or
rescue a miss.

### `SPARSE_MEL55_1` — mined on Meltopia, covers three of the eight

Meltopia's PDB names the whole family (`Add` / `AddUnique` / `Clear` / `Remove` / `RemoveAll` /
`Get*MulticastDelegate` / `SetMulticastDelegate` / `NotifyUObjectDeleted`), which made the shapes
easy to compare. The one that generalises is a **twin reference**:

```
lea    rcx,[SparseDelegates]     <- passed as `this` to TSet::FindOrAddId
call   <FindOrAddId>
movsxd rax,[rsp+d32]             <- out-param element index (displacement WILDCARDED)
lea    rdi,[rax+rax*2]; shl rdi,5   <- element stride 0x60
add    rdi,[SparseDelegates]     <- the SAME global again
```

Two references to one static with the stride math between them — the same property that makes
`SPARSE_ES2_1` reliable. Meltopia 3/3 decoy-free; also hits **Manor Lords** and **TQ2**, both
previously `n=1`, converging on a single address on each. Zero hits on Everspace 2 5.5,
Satisfactory 5.2/5.6, Solarpunk, DropIn, Avowed and FF7 Rebirth — codegen-specific rather than
version-specific, i.e. genuinely additive. Priority **160, last**, so it cannot perturb anything.

Sparse `n=1` binaries: **8 → 5** (the remaining five are the two Everspace 2 builds and
Satisfactory's 5.2/5.6 CoreUObject).

### Two rejected candidates, both worth recording

- The **TSet hash-bucket probe** (`dec ecx; mov eax,rNd; and rcx,rax; mov eax,[rdx+rcx*4];
  cmp eax,-1; jz; mov rdx,[Sparse]`) reads like an ideal anchor and is the opposite: it is the
  *generic* TSet lookup every TSet in the engine uses. It resolved to **39–43 different globals
  per binary** and was DECOY-ONLY on Solarpunk and Satisfactory 5.2.
- The **register-nibbled** form of the accepted pattern took **0 hits**. Over-wildcarding does
  not generalise a pattern; it just stops it matching. That is now the third independent
  confirmation of the exact-register rule for this target.

The leading `lea rdx,[rsp+d32]` was also dropped on purpose — a frame-layout detail is not a
semantic anchor, and the pattern is equally unique without it.

### FF7 Rebirth: question answered, patterns deliberately NOT added

**Yes, it has `FSparseDelegateStorage`.** Proven from `.rdata`, which carries
`SparseDelegateFunction`, `MulticastSparseDelegateProperty` and even the `SparseDelegateReport`
console command with its help text. So the storage exists and every one of our sparse patterns
simply misses this fork's codegen — including both new ones.

Its other four targets are in reasonable shape (GNames `1490D3C00` n=5, GObjects `14871EB38`
n=5 with `GOBJ_RE1` independently finding the `-0x10` base, GWorld `148F30420` n=3, GEngine
`148F4B580` n=2), and the tool is recorded as working in-game on it.

No pattern was added, for two reasons worth stating rather than hiding:

1. **Cost/benefit.** Locating the global needs a dedicated RE pass on a 377K-function
   symbol-less binary — the `SparseDelegateReport` console command is the obvious lead (find the
   string xref, follow the `FAutoConsoleCommand` handler) and is recorded here for whoever picks
   it up. Sparse is lazily resolved and non-critical: only the sparse-delegate drill-down
   degrades, nothing in the boot path.
2. **It probably would not transfer.** The hope was insurance for FF7 part 3. But the history
   argues against it: FF7 Remake (4.18) and FF7 Rebirth (4.26 fork) share *no* signatures —
   `GOBJ_RE2`/`GOBJ_V12` work on Remake, `GOBJ_RE1` on Rebirth, and `GENG_X4` is DECOY-ONLY on
   Remake while merely divergent on Rebirth. A pattern mined from Rebirth would only help part 3
   if it reuses the same fork *and* toolchain, which those two titles did not manage between
   themselves. Better to re-mine when the binary exists.

-----

## 2026-07-26 - Two AOBs mined from Palworld (UE 5.1): a second sparse anchor + the broadest GEngine pattern yet (build 2426)

Palworld ships no PDB, so this is a worked example of mining from a symbol-less binary. Ground
truth first, patterns second — the reverse order silently produces patterns for a wrong address.

### Establishing Palworld's truth without symbols

The consensus table gave GNames `14944DB80` and GWorld `14965BBE0` at **12 agreeing patterns**
each — not in doubt. GObjects showed a `1494ED280` / `1494ED290` pair (6 patterns each, exactly
`base` and `base+0x10` = ObjObjects). SparseDelegates had **one** pattern and nothing to
corroborate it, so it was confirmed structurally instead: disassembling the `SPARSE_ES2_1` site
gives `FSparseDelegateStorage::NotifyUObjectDeleted` —

```
LEA  RCX,[0x148FB66B0]   ; passed as `this`
CALL <TMap::Remove>
MOV  EAX,[0x148FB66B8]   ; +0x8   \ the two int32s ValidateSparseDelegates range-checks
CMP  EAX,[0x148FB66E4]   ; +0x34  /
...
LEA  RCX,[0x1494ED280]   ; then RemoveUObjectDeleteListener  => confirms GObjects too
```

### What was actually weak, and what was not

| target | Palworld anchors | action |
|---|---|---|
| GNames / GWorld | 12 patterns each | nothing needed |
| GObjects | 12 patterns | nothing needed (the ~40 wasted validations are cost, not risk) |
| GEngine | 3 patterns, but X1/X3 overlap by construction ⇒ **2 independent shapes** | added one |
| **SparseDelegates** | **1** — and its validator is the weakest we have, so it cannot rescue a miss | added one |

### `SPARSE_PAL51_1` — a second sparse anchor

Anchors the element-address block rather than `NotifyUObjectDeleted`:
`lea r,[rax+rax*2]; shl r,5` (stride 0x60) → **`add r,[SparseDelegates]`** → `lea r,[r+8];
cmovz; test; jz near` → `mov eax,[r+8]; cmp eax,[r+0x34]` (the TSet Num-vs-Max compare).
`SPARSE_DI427_2` models the same semantics but with a *short* jz and a different instruction
order, which is why it takes 0 hits here. 29 literal bytes; fires on exactly three binaries,
decoy-free on all: Palworld 2/2, **UE 4.26 Satisfactory 2/2 UNIQUE-OK** (an unplanned bonus — it
is not 5.1-only), and DQ I&II HD-2D (2 hits converging on one address). Zero hits on the other
32 programs. Placed at priority **150, deliberately last**, so it cannot perturb any existing
selection — it is the backup for when `SPARSE_ES2_1`'s site changes, not a replacement.

The register-agnostic nibbled variant was measured and **rejected**: it produced a decoy on
Palworld itself, reproducing the trap already recorded for `SPARSE_DI427_2`. Exact-register forms
remain the safe ones for this target — that is now two independent confirmations.

### `GENG_X4` — mined on 5.1, useful nearly everywhere

`mov rax,[GEngine]; test rax,rax; jz; mov rcx,[rax+disp32]; test rcx,rcx; jz` — null-check the
engine, load one of its object members at a **32-bit** displacement, null-check that. The
`?? ?? 00 00` is load-bearing: it pins the member load to a disp32, which UEngine's layout forces
and which keeps the pattern off the far commoner 8-bit-displacement `mov rcx,[rax+0x30]` idiom.

Correct on **twelve** oracles spanning UE 4.20 → 5.7 — UNIQUE-OK on 4.22 / 4.24 / 4.26 / 4.27 /
5.2 / 5.5 Meltopia / 5.6, and correct-site-first on 4.20 / 4.25 / both 5.5 Everspace builds / 5.7.
On Avowed all 53 hits converge on one address.

**Recorded honestly, because it is not clean everywhere:** on FF7 Remake it is DECOY-ONLY (106
hits, 3 distinct targets) and FF7 Rebirth is similarly divergent (90 hits, 6 targets) — the
SquareEnix forks reuse this shape for something else. It costs nothing today because `GENG_X3`
(pri 105) wins on FF7 Remake first, and `ValidateGEngineSlot` derefs the slot and demands a
reflected `GameViewport` property. Placed at 115, behind the three cleaner X-family patterns.

A rejected candidate is worth recording too: `mov rcx,[G]; test; jz; call [vtable]` looked
plausible and produced **76–93 different targets per binary**. Divergent hits mean a generic
idiom; convergent hits mean a real global. That single test separated the two candidates.

### Verification

Full 35-program re-sweep after both additions: **the regression matrix is byte-for-byte
identical** — every target on all 20 oracles still resolves correctly and no landing pattern
moved. Palworld's SparseDelegates is now `n=2`. Build + tests green.

-----

## 2026-07-26 - Corpus to 35 programs / 20 oracles: UE 4.24 + 5.1 + a same-game cross-build pair; sparse delegates settled (build 2420)

Five more Ghidra projects, all produced with the current Ghidra: `DropIn_UE424` (UE 4.24.3, PDB),
`ES2_UE55` (UE 5.5, 2025-06-17 build, PDB), `Meltopia_V2` (UE 5.5, PDB **now applied**),
`Palworld` (UE 5.1) and `FF7Re` (FF7 Rebirth). Corpus: **35 programs, 20 with ground truth,
twelve engine versions.** No new pattern was needed — **every target on every one of the 20
oracles still resolves to the correct address**, and the four pre-existing fall-throughs are
unchanged.

### UE 4.24 settles the sparse-delegate question

`DropIn_UE424` carries a `FSparseDelegateStorage::SparseDelegates` symbol whose mangled name
demangles to

```
TMap<UObjectBase const*, TMap<FName, TSharedPtr<TMulticastScriptDelegate<FWeakObjectPtr>>>>
```

— a **raw pointer key**, identical to 4.25 / 4.26 / 4.27 / 5.x. Sparse delegates arrived in 4.23,
so **only 4.23 itself is now unverified** and no 4.23 binary exists in the corpus. `Aura` still
probes the live key shape rather than gating on a version number, which is what keeps 4.23 and
any licensee fork safe without a binary; the note in `Himmel.h` that once claimed
"4.23-4.26 remain unverified" is now down to one version. All five 4.24 targets resolve with no
new patterns (`GOBJ_ES53_1` / `GNAM_V8` / `GWLD_TQ_1` / `SPARSE_DI427_1` / `GENG_X1`).

### The same-game cross-build pair — patterns survive a game update

`ES2-0517` (2025-05-17) and `ES2_UE55` (2025-06-17) are the same game, same engine, two manifests
apart. Every global moved:

| | 0517 | UE55 | delta |
|---|---|---|---|
| GObjects | `149AA7EE0` | `149AA5F60` | -0x1f80 |
| GNames | `149C009C0` | `149BFE940` | -0x2080 |
| GWorld | `149B37D18` | `149B35DD8` | -0x1f40 |
| SparseDelegates | `149AA7E90` | `149AA5F10` | -0x1f80 |
| GEngine | `149DA5810` | `149DA37B0` | -0x2060 |

so this is a real re-find, not a trivially identical binary. Both builds land on the **same
pattern with the same cost for all five targets**. That is the first direct evidence in the
corpus that a signature survives a shipped patch rather than merely a version bump — every other
pair differs by engine version too.

### Meltopia: PDB applied via MSDIA, and it vindicates the consensus method

The first import silently failed to apply Meltopia's 347 MB PDB; the retry succeeded by selecting
the **MSDIA** loader — **PDB-Universal fails on this file**. Worth remembering as a first
resort when a game ships a PDB and the probe still reports zero UE globals.

The payoff is a clean, blind validation. While Meltopia had no symbols, the sweep's consensus
table predicted GEngine `149F002F8`, GWorld `149F03D10`, GObjects `149D87430`, GNames
`149CA3C80`. The PDB then gave `149f002f8`, `149f03d10`, `149d87420` (+0x10 = `149d87430`) and
`149ca3c80` — **all four exact**. The ≥3-independent-patterns-agree heuristic has now been
confirmed against symbols twice (Everspace, Meltopia).

### A caution about pruning, learned the same day

`GWLD_V7` ("Palworld long context") sat at **0 correct across the whole corpus** and appeared in
the dead-weight table — and then went **UNIQUE-OK the moment Meltopia gained symbols**. A pattern
with *no proof* is not the same as a pattern with *counter-proof*.

So the four GWorld patterns removed in build 2409 were re-tested against all three new oracles
rather than assumed. `GWLD_V2` / `V4` / `V5` / `V6` are still `DECOY-ONLY` on every one — now
**0 correct across 12 oracle groups** while firing 11–395 times each. That is counter-proof, and
it is precisely why those went and V7 stayed. Both facts are recorded in the corpus note in
`Himmel.h` so the next pruning pass starts from the right test.

### Palworld and FF7 Rebirth close two attribution loops

Both are symbol-less noise probes, but each is the binary its namesake patterns were contributed
for, and neither had ever been in the corpus:

- **`GOBJ_RE1`** ("FF7 Rebirth add+cmp+jge") had **zero hits anywhere** across 31 programs. On
  FF7 Rebirth it hits exactly once — it was never broken, just never tested on its own game.
- **`GWLD_V7`**, **`GOBJ_V13`** and **`GOBJ_V9`** ("Palworld …") all fire on Palworld, the UE 5.1
  title they were named after and the corpus's only 5.1 sample.

Fourteen patterns still hit nothing anywhere (`GOBJ_SAT425_1`, `GOBJ_RE3`, `GOBJ_V11`,
`GOBJ_SF_1`, `GOBJ_PS4`, `GOBJ_PS5`, `GOBJ_CT3`, `GNAM_SAT52_1`, `GNAM_V6`, `GWLD_GH_2`,
`GWLD_V1`, `GWLD_SF_3`, `GWLD_G427_3`, `GWLD_G427_4`). On the evidence above they are being left
alone: zero cost at their priorities, and the corpus keeps demonstrating that "never seen to
fire" often means "the right binary is not here yet".

-----

## 2026-07-26 - Pattern tables sorted + compile-time-enforced; 4 never-correct GWorld patterns removed (build 2414)

### The tables had drifted out of priority order, and the file was lying about it

The user asked why `GNAM_V1`/`V3`/`V4` "still have not been re-prioritised". They **had** been —
demoted to 870/880/890 in build 2405 — but the array had not been re-sorted, so all three still
sat under a `// 500–590: Tier 3 — short patterns` header. `GNAM_V5` (850) sat inside the Tier-1
block, `GNAM_V2` (860) inside Tier 2, `GOBJ_PS7` (970) under `// 600–690`, and `GWLD_G42_1`
(880) inside the 325–365 run. `ScanForTarget` sorts by priority so **behaviour was always
correct** — but anyone reading the file got a different order from the one that actually runs.
That is a worse failure than a plain bug: it silently invalidates review.

All five tables are now written in priority order, the band headers match their contents, and
the invariant is **enforced by the compiler** rather than by discipline:

```cpp
ASSERT_TABLE_ORDER(GOBJECTS_PATTERNS);   // static_assert: sorted AND no duplicate priorities
```

Verified the guard actually fires by deliberately mis-numbering an entry:
`error C2338: static assertion failed: 'GNAMES_PATTERNS must be listed in priority order'`.
Duplicate priorities are rejected too — two patterns on one number have an order that depends on
the sort's stability, which makes a regression sweep unreproducible.

### GWLD_V2 / V4 / V5 / V6 removed — never once correct in 31 programs

The user's read that `AOB_GWORLD_V5` / `V6` "look a bit short, priority should be low" was right,
and the data went further than that. Across 31 programs (9 groups with GWorld ground truth):

| pattern | literal bytes | matches | reaches truth on |
|---|---|---|---|
| `GWLD_V4` `48 8B 3D ?? ?? ?? ?? 48 85 FF` | 6 | 5,809 | **0 of 9** |
| `GWLD_V6` `48 89 1D ?? ?? ?? ?? E8` (write) | 4 | 2,403 | **0 of 9** |
| `GWLD_V2` `48 89 05 ?? ?? ?? ?? 48 85 C0 74` (write) | 7 | 1,301 | **0 of 9** |
| `GWLD_V5` `48 39 05 ?? ?? ?? ?? 74` | 4 | 929 | **0 of 9** |
| `GWLD_V3` `48 8B 1D ?? ?? ?? ?? 48 85 DB` — **kept** | 6 | 22,581 | 6 of 9 |

Every shape is already covered by a longer sibling that does work: the `mov rdi,[GWorld]` read by
`SP57_3`/`G427_2`/`SF_4`, the rax-write by `SAT426_2`/`ES53_1`/`SAT425_3`, the rbx-write by
`SF_3`. Removing them loses no mechanism, only the degenerate context-free form.

**The deciding argument is specific to GWorld: a wrong GWorld is worse than no GWorld.**
`ValidateGWorldBasic` is deliberately loose, and when it is fooled the damage is silent — exactly
what happened on Solarpunk, where `GWLD_SF_2` matched a decoy `.data` global, passed validation
and produced a wrong world. With nothing resolving, Genau instead falls back to instance-scan
recovery, which found the *right* world on that same title. A pattern that has never once been
correct is therefore pure downside here, however low its priority.

### Why the GNames short patterns were NOT removed

Same question, different answer, because the evidence differs. Over the same corpus
(10 GNames oracle groups): `GNAM_V2` 8 correct / 2 decoy-only, `V5` 8/2, `V3` 7/3, `V4` 6/4,
`V1` 6/4. They are **redundant, not wrong** — where each is correct there are 3–14 other correct
patterns, so deleting them changes no result today, but "correct yet redundant" is worth keeping
as insurance for an engine build the corpus does not cover, whereas "never correct" is not. The
second half of the argument is the validator: `ValidateGNames` reads the pool structure and is
strong, while `ValidateGWorldBasic` is loose and has been fooled in the field. At 850–890 they
are only reached when everything above failed; on all 10 oracles GNames resolves by 715 at the
latest, so they are never even scanned.

Re-ran the full 31-program sweep after both changes: the regression matrix is **byte-for-byte
identical**, and all eight symbol-less titles still pick GWorld at priority 100–390, far above
the removed slots.

### The file header was stale, and a second dead constant fell out of checking it

The top-of-file block still said *"128+ AOB pattern database"* and *"signatures for GObjects,
GNames, GWorld"* — it never mentioned **SparseDelegates or GEngine at all**, despite both being
first-class `AobTarget` values. Its source list also overclaimed: `RE1-RE5` when only RE1–RE3
exist, `UD1-UD3`, `CT1-CT5`, and `D7_1` which was deleted back in 2404.

Rewritten with a per-target breakdown (counts machine-verified against
`extract_patterns.py`, not hand-copied), the priority-order + `static_assert` rule, the
"verify against the corpus before trusting it" step with the actual command, and a description
of what the 31-program / 17-oracle corpus contains and why half of it deliberately has no
ground truth.

Auditing "is every declared constant actually in a table?" then turned up
**`AOB_GOBJECTS_CT2`** — dead in exactly the way `AOB_GNAMES_UD1` was, and worse on inspection:
`push rbx; sub rsp,0x20; mov rbx,rcx; test rdx,rdx; jz; mov` is a bare MSVC prologue matching
thousands of functions, and it contains **no RIP-relative operand at all**, so there was nothing
for `TryResolveMatch` to resolve — wiring it up could never have produced an address. Removed.

Since this class of rot has now bitten twice, `extract_patterns.py` reports it: any `AOB_*`
constant declared but referenced by no `PATTERNS[]` array is listed as `DEAD`, with a whitelist
for the one deliberate exception (`AOB_NAMEDECRYPT_ME1`, which `Genau::ResolveNameKeyTable`
consumes directly because it de-obfuscates FName payloads rather than resolving a pointer).
Verified by planting a fake constant and watching it get flagged.

-----

## 2026-07-26 - 31-program AOB sweep: GEngine symbol export + FF7R coverage, two dead patterns removed, one unmatchable pattern fixed (build 2408)

The corpus grew from 8 binaries to **31 programs across 18 Ghidra projects — 17 of them with PDB
truth**, spanning UE 4.18 / 4.20 / 4.22 / 4.25 / 4.26 / 4.27 / 5.2 / 5.3 / 5.5 / 5.6 / 5.7. The
sweep is now a script (`tools/ghidra/sweep.sh` + `aggregate_sweep.py`) rather than a hand-run
command per project, because the next round has to be repeatable.

**Headline: every target on every one of the 17 oracles resolves to the correct address.** No
pattern added in this or the previous two builds changed what any engine version lands on.

### What the bigger corpus exposed

| finding | detail |
|---|---|
| **GEngine was never given a symbol export** | `?GEngine@@3PEAVUEngine@@EA` is exported by the Engine module in every modular build we have binaries for — verified in the export table of Satisfactory's `FactoryGame-Engine-Win64-Shipping.dll` on **both** UE 4.26 (ordinal 13690) and UE 5.2 (19170), sitting directly beside `?GWorld@@3VUWorldProxy@@A`. GObjects and GWorld had `SIG_EXPORT` entries; GEngine simply never got one, so modular titles paid for a full AOB sweep to find something `GetProcAddress` returns in O(1). Added at priority 0. |
| **`AOB_GNAMES_SAT422_1` could never match anything** | It omitted the `48 85 C0` (`test rax,rax`) between the load and the jump — and MSVC cannot emit `mov`+`jnz` with no flag-setting instruction between them, so the string was unmatchable *by construction*. Zero hits across all 31 programs, including the very Satisfactory UE 4.22 build it is named after. Re-derived from that build's PDB (`FName::GetNames` @ `0x140BCEBF0`, load at +4) and moved 730 → 715, so UE 4.22 now lands on its purpose-built anchor instead of falling through to `GNAM_CT4`, a `ret; mov [rip],rbx` **write** pattern that only got there after rejecting a decoy. |
| **`AOB_GNAMES_UD1` was dead code** | Declared since the DB was written, never referenced by `GNAMES_PATTERNS[]` — it has never been scanned for in any build. The suspicion about it was well founded: `cmp dword [rbp-0x18], 0` pins an exact frame-pointer-relative stack slot, a property of one compilation of one function in one game. Deleted rather than wired up. |
| **`GNAM_CT2` is byte-for-byte redundant with `GNAM_UD2`** | CT2 is UD2 minus its final `05`. Measured over all 31 programs the two produced **identical** hit counts on every single one (0/0, 10/10, 11/11, 15/15, 36/36, 932/932 on FF7R…). The `C6` CT2 stops on is `mov byte ptr`, and the only encoding that ever follows here is the `C6 05` UD2 pins. CT2 removed; UD2 takes priority 300. |

### FF7 Remake: the one binary where GEngine found nothing

Of 31 programs, FF7R was the only one where **every** GEngine pattern missed. Its
`GetWorldFromContextObject` wrapper spills the result (`mov rbx,rax`) *before* the null check, so
`GENG_X1`'s trailing `48 85 C0` no longer follows the call — a length change no nibble can
bridge. New **`GENG_X3`** is X1's head only (`sub rsp,0x2X; mov rdx,rcx; mov rcx,[GEngine];
call`, REX nibble-masked, tail dropped). Dropping the tail was measured, not assumed: X3 is
UNIQUE-OK with **zero decoys** on both calibration oracles and finds strictly *more* correct
sites than X1 (DropIn 3 vs 2, Solarpunk 2 vs 1). It also closes UE 5.5, where X1 misses.

Disassembling X3's single FF7R hit confirmed the address and handed over two more constants for
free — the caller runs the returned UWorld's `InternalIndex` through
`cmp [0x1453BD48C]` / `mov rax,[0x1453BD480]` / `lea rcx,[rax+idx*24]`, i.e. textbook
`GUObjectArray.IndexToObject`. So FF7R is now a **partial oracle**: `GEngine = 0x145879EE8`,
`GObjects = 0x1453BD470`, both corroborated by independent patterns. GNames/GWorld are
deliberately left unset — a guessed truth is worse than none, because it mislabels every hit as
a decoy (the mistake that once got two good GEngine patterns demoted).

GEngine coverage is now complete over the corpus: `GENG_X1` lands on 8 engine versions,
`GENG_X3` on the 2 it misses.

### Band discipline extended to GObjects and GWorld

Build 2405 fixed the GNames table; the same audit had never been applied to the other two.
`GWLD_V3` alone takes **22,017 matches** — 95.7 per MB of `.text` on a monolithic game EXE, 2,658
on FF7 Remake by itself — out of six literal bytes. `GOBJ_V1` takes 10,152 (53/MB).

Be precise about what moving them buys, because the two tables differ:

- **GObjects** (V1/V2/V3/V5/V6/V7/CT3 + PS6/PS7, 390–660 → 890–970): a **real** ordering change.
  They previously outranked `GOBJ_G427_2` (700), `G427_4` (720), `CT1` (800) and the Octopath
  `OT_1`/`OT_2` pair (820/840) — all 9–13 literal bytes against these six or seven.
- **GWorld** (V2/V3/V4/V5/V6, 500–580 → 900–980): **consistency only**. They already sat behind
  every other GWorld pattern (highest was 435), so the validator never reached them on any
  oracle. The point is that the band now *means* something. The one genuine ordering change here
  is `GWLD_G42_1` (7 literal bytes) 340 → 880.

**Counter-example kept in the header comment,** because literal-byte count is necessary but not
sufficient: `GOBJ_ES53_1` has 16 literal bytes yet takes 21–475 matches on every monolithic
title — its shape is the generic MSVC function-scope-static + `atexit` registration thunk, so it
matches once per static with a destructor. It stays at priority 100 anyway: it is the landing
pattern for six module-instances, and patterns are scanned in **batches of 8** with an early
return on the first validated match, so winning from batch 1 avoids every later `.text` pass.
Rejecting a few hundred candidates by validation is far cheaper than an extra AVX2 sweep of a
130 MB `.text`. Do not demote a noisy pattern that is also a winner.

### Harness defects fixed along the way

Three of these silently corrupted results rather than failing loudly, which is the dangerous kind:

- `scan_patterns.java` wrote a fixed `scan_patterns.txt`, so a `-process` run over a **modular**
  project overwrote itself and only the last DLL survived. Outputs are now keyed by
  `tag + program + image base` — all three are needed: `FactoryGame-FactoryGame-Win64-Shipping.dll`
  exists in both the 4.26 and 5.2 projects, and Satisfactory v1.2.3.1 holds a good *and* a broken
  import of Core/CoreUObject/Engine under identical names. The broken duplicate had overwritten
  the real 5.6 Engine results.
- Programs with zero executable bytes (failed imports, image base `0000:0000`) are now skipped.
- Hit counts were reported as `hits.size()`, which is capped at 40,000 — hot patterns were
  under-counted. Now counted uncapped, with only the *detail* list capped.
- `extract_patterns.py` parsed the `#define SIG_RIP(...)` macro **definition** as a signature,
  producing a phantom 154th row with `pattern = "<UNRESOLVED:pat>"`.
- The regression model itself was wrong: `>>> SELECTED` names the first pattern that *hits*, but
  `ScanForTarget` validates every match and moves on when they all fail. A `DECOY-ONLY` top
  pattern is a **fall-through (cost)**, not a wrong answer **(correctness)**. `aggregate_sweep.py`
  now replays the real walk. Reading the old line as "what we resolve to" overstated risk.

### Corpus notes for next time

- `Satisfactory_UE521.rep` is **mis-imported**: only the *game* DLL is 5.2, and its
  Core/CoreUObject/Engine are duplicates of the 4.26 DLLs (plus four broken empty programs). The
  real 5.2 engine DLLs + PDBs were imported into a separate `SF521_pdb` project so the original
  stays untouched. UE 5.2 is now a full oracle.
- `Meltopia` ships a 347 MB PDB that its import never applied — it works as a monolithic UE 5.5
  noise probe, and re-importing with the PDB would make it a second symbolised 5.5 oracle.
- `ES2-0517` needs a one-time Ghidra language-version upgrade that `-readOnly` cannot save.
- `Satfi426` is superseded by `Satisfactory_UE426` and can be deleted.

All of this — the truth table, the per-project quirks, and the derivation procedure — is in
[tools/ghidra/GROUND-TRUTH.md](../tools/ghidra/GROUND-TRUTH.md).

-----

## 2026-07-26 - GNames band discipline: short patterns demoted, hand-derived UE4 ones promoted; UE 4.25 folded in (build 2405)

### The GNames table had drifted in *both* directions

The user's read of it was right, and the sweep data was blunt about it. A pattern's band is
supposed to track how **specific** it is — count its literal (non-wildcard) bytes — but:

| pattern | old pri | bytes | literal | measured |
|---|---|---|---|---|
| `GNAM_V5` | **110** (Tier 1) | 19 | 7 | 16,686 hits on 4.27; OK-BEHIND on every engine it touches |
| `GNAM_V2` | 400 | 14 | 6 | 16,692 hits on 4.27 |
| `GNAM_V1`/`V3`/`V4` | 500/520/540 | 8 | **4** | DECOY-ONLY on 4.20/5.5/5.7; 539-2060 hits elsewhere |
| `GNAM_CT3` | **800** | 27 | **20** | UNIQUE-OK on 4.20, MISS on every FNamePool binary |
| `GNAM_G42_1` | 840 | 18 | 9 | UNIQUE-OK on 4.20, MISS elsewhere |

The four-literal-byte patterns were running *before* the twenty-literal-byte ones. The
pre-FNamePool UE4 entries had been hand-derived later and deliberately lengthened to cut
collisions — but nobody moved them out of the last-resort band afterwards.

Re-sorted from measurement, not vibes: `V5→850`, `V2→860`, `V1→870`, `V3→880`, `V4→890`;
`CT3→700`, `G42_1→710`, `CT4→720`, `SAT422_1→730`. Promoting the UE4 set is provably free —
they target `TStaticIndirectArrayThreadSafeRead`/`TNameEntryArray`, a different structure, and
MISS on all four FNamePool binaries. A band-discipline note now sits in `Himmel.h` so the rule
survives: **fewer than ~8 literal bytes means 800+, regardless of what it anchors on.**

**Four of five engine versions improved, none regressed:**

| | before | after |
|---|---|---|
| UE 4.20 | `GNAM_V5` DECOY-ONLY (after ~710 wasted validations) | **`GNAM_CT3` CORRECT (all hits)** |
| UE 5.5 | `GNAM_V5` OK-BEHIND, 15 hits | **`GNAM_ES53_1` CORRECT (all hits)** |
| UE 5.6 | `GNAM_V5` AT RISK, 5 decoys first | **`GNAM_ES53_1` CORRECT (all hits)** |
| UE 5.7 | `GNAM_V5` OK-BEHIND, 86 hits | **`GNAM_SAT425_3` CORRECT (all hits)** |
| UE 4.27 | `GNAM_DI427_2` CORRECT | unchanged |

### UE 4.25 added — and it closes the sparse-delegate gap

`ES2-UE425.rep` (Everspace 2 from a Steam depot, **UE 4.25.2**, full PDB) is the FField/FProperty
transition band. Ground truth: `GUObjectArray` `0x1444B0510`, `NamePoolData` `0x144497D00`
(via `FNameDebugVisualizer::GetBlocks` @ `0x140EF8410`), `GWorld` `0x1445F1160`,
`SparseDelegates` `0x1440070C0`, `GEngine` `0x1445EDAD8`.

**It needs no new patterns** — GEngine `GENG_X1`, GNames `GNAM_V8`, GWorld `GWLD_TQ_1` and
Sparse `SPARSE_DI427_1` are all CORRECT-on-all-hits; GObjects reaches truth via
`GOBJ_SAT425_2`. More usefully it *extends* two families: `SPARSE_DI427_1/_2` and
`GNAM_DI427_1/_2`, both mined on 4.27, are correct here too.

And it settles a documented unknown: the 4.25 PDB gives
`TMap<UObjectBase const*, …>` for `FSparseDelegateStorage::SparseDelegates` — **a raw pointer
key, identical to 4.27 and 5.x**. The "UE 4.23-4.26 uses FObjectKey" claim is now falsified on
two independent UE4 builds; only 4.23/4.24 remain unverified.

`GENG_X1` is now correct-first on **4.20, 4.25, 4.27, 5.6 and 5.7** — five engine versions from
one signature. `GROUND-TRUTH.md` updated with the 4.25 row and its `GS_TRUE` line.

-----

## 2026-07-25 - Removed the 27k-decoy GNames pattern; sparse validator now checks content (build 2404)

Closed the two weaknesses the six-engine harness surfaced last build.

### `GNAM_D7_1` removed

It was `"48 8D 0D ?? ?? ?? ?? E8"` — `lea rcx,[rip+X]; call`, **three literal bytes**, i.e. a
match on essentially every this-call in the image. Measured hit counts: **27,001** on UE 4.20,
**104,897** on UE 4.27, **40,000** on UE 5.5. Every one of those was resolved and validated
(several SEH-guarded reads each) *before* the scan could reach the patterns that actually work
on those titles — on UE 4.20 the winners are `GNAM_CT3` (pri 800) and `GNAM_G42_1` (pri 840),
both well after D7_1 at 560.

It was never the sole correct pattern on any of the eight binaries in the sweep, and its own
comment already said "same as V2 but shorter context; already covered by V2/V5". Dumper-7 can
afford the bare string because it follows the CALL and checks the callee for
`InitializeSRWLock` + a `"ByteProperty"` reference — a second stage we do not implement, so for
us it was pure cost. Re-adding it would need `AobResolve::CallFollow` plus that callee check,
not the byte string.

**Removing it improved DropIn**: GNames now selects `GNAM_DI427_2` → CORRECT (all hits),
where it previously fell through `GNAM_V7` and D7_1's 104,897 validations first.

### Validation is now bounded

Independently of that pattern, `ScanForTarget`'s per-match validation loop gained a
`kMaxValidatePerPattern = 4096` cap with a `LOG_WARN`. If the correct site is not in a
pattern's first 4096 matches, that pattern was not selective enough to trust anyway — and the
warning makes the next over-generic signature visible instead of silently expensive.

### `ValidateSparseDelegates` now checks content, not just shape

The old validator only range-checked two int32s, so it accepted any `.data` blob that looked
vaguely like a TMap — which is why offline sweeps kept finding sparse patterns whose decoys
resolve to unrelated 0x60-stride TSets, and why an `OK-BEHIND` sparse pattern would have been
genuinely dangerous. When the map is **non-empty** it now also requires that one of the first
32 slots holds a key that is a userspace pointer **whose own first qword is a vtable inside the
module image** — which is exactly what `TMap<UObjectBase const*, …>` guarantees and what a map
keyed by FName/int/FString cannot fake. Empty maps are still accepted on shape alone, on
purpose: `FindAll` can legitimately run before anything binds a sparse delegate.

### And the "SPARSE_SP57_1 risk" was a reporting bug, not a real one

Last build flagged `SPARSE_SP57_1` on Solarpunk as "2 decoys scan first". It does not — its
correct site is the *first* match (`0x1413D5EE5`, well below the decoys at `0x143DB6E21`). The
harness printed the warning whenever any decoy existed, ignoring scan order. Fixed to compare
the two indices, so the verdict now reads `CORRECT first (2 decoy(s) scan later, never
reached)`. The strengthened validator above is still worth having — it protects the genuinely
`AT RISK` orderings that a future game may produce.

### Re-verified

Full six-engine sweep re-run after the removal: no target regressed on any binary, `GENG_X1`
still correct-first on 4.20/4.27/5.6/5.7, `SPARSE_ES2_1` still correct on 4.27/5.5/5.6.
`tools/ghidra/GROUND-TRUTH.md` added — the per-project `GS_TRUE` strings, the verdict glossary,
and the procedure for folding in the next PDB game, so the next sweep is copy-paste.

-----

## 2026-07-25 - Six-engine regression harness; a measurement error corrected (build 2402)

Two more symbolised projects arrived — **Everspace re-analysed WITH its PDB** (`ES1-420.rep`,
UE 4.20) and **Satisfactory v1.2.3.1** (UE 5.6.1, modular, CoreUObject+Core+Engine+FactoryGame
all imported). Both were folded into the sweep, which now covers **six engine versions with
real symbols on five of them**: 4.20, 4.27, 5.5, 5.6, 5.7 (+ Avowed 5.3, symbol-less).

### The correction

Last build demoted `GENG_X1` and `GENG_DI427_1` on the strength of "5 decoys on Everspace 4.20".
**That was a measurement artifact.** Everspace had no symbols then, so the sweep had been given
a *placeholder* truth value (`GEngine=5`); every hit necessarily compared unequal and got
labelled a decoy. With the real PDB both are **UNIQUE-OK on 4.20** — `GENG_X1` 1/1,
`GENG_DI427_1` 5/5. Priorities restored, and `GENG_X1` is now the lead GEngine pattern: it is
correct-first on **4.20, 4.27, 5.6 and 5.7**, the broadest single signature in the file.

Systemic fix so it cannot recur: `scan_patterns.java` now emits **`NO-TRUTH`** instead of
`DECOY-ONLY` when a target has no plausible truth value, and refuses to render decoy counts at
all in that case. It also skips `CallFollow`/`Symbol*` resolutions, whose model it cannot
reproduce (`GNAM_V7` is CallFollow and had been scoring phantom decoys the same way).

### The regression harness (answers "does adding AOBs break anything?")

`scan_patterns.java` gained a **`>>> SELECTED`** line: walking priority order, which is the
FIRST pattern that hits, and does it reach truth? That mirrors `Genau::ScanForTarget`, which
validates each match and takes the first that passes — so a newly-added lower-numbered pattern
can only do harm if it hits, survives validation, AND is wrong.

Result across all eight binaries/modules: **every time a newly-added pattern is selected it is
CORRECT on all hits** (`GENG_X1` ×4, `GENG_ES55_1`, `GWLD_DI427_1`, `SPARSE_DI427_1`). No
existing target changed hands on any binary — Solarpunk still selects `GWLD_SP57_1` /
`SPARSE_SP57_1`, ES2 still selects `GWLD_ES2_1` / `SPARSE_ES2_1`.

The harness also surfaced two **pre-existing** (not new) weaknesses worth recording:
* `GOBJ_ES53_1` (pri 100) and `GNAM_V5`/`GNAM_V7` are selected first on several binaries and
  reach truth only after the validator rejects their decoys — by design, since
  `ValidateGObjects`/`ValidateGNames*` are strong. The one to watch is `SPARSE_SP57_1` on
  Solarpunk (2 decoys scan first) because `ValidateSparseDelegates` is deliberately weak.
* UE 4.20 GNames is covered only in the last-resort band (`GNAM_CT3` pri 800, `GNAM_G42_1`
  pri 840 — both UNIQUE-OK, anchored on `FName::GetNames`) while `GNAM_D7_1` fires **27,001**
  decoys at pri 560 first. Correct, but slow.

### Satisfactory 5.6.1 — no new patterns needed

All five targets already resolve: GObjects `GOBJ_ES53_1`, GNames `GNAM_V5`, GWorld `GWLD_SF_1`,
Sparse `SPARSE_ES2_1`, GEngine `GENG_X1`. Layout note: **the name pool moved from CoreUObject
to Core by 5.6** — `NamePoolData` `0x18082E8C0`, recovered from `FNameDebugVisualizer::GetBlocks`
(`lea rax,[pool+0x10]; ret`), the same 2-instruction oracle that worked on DropIn.

`SPARSE_ES2_1` is now verified correct on **UE 4.27, 5.5, 5.6 and 5.7** — four engine versions
from one signature.

### Everspace 4.20 also validated the consensus technique

Before its PDB existed, running the full database and keeping addresses that ≥3 independent
patterns agreed on gave GWorld `0x1432E1AC0`, GObjects `0x142E797F0`, GNames `0x1431DEAD8`.
The symbols confirmed **all three exactly** (`Names` is reached via `FName::GetNames`, which
lazily `new`s a 0x408-byte `TNameEntryArray` — 4.20 predates FNamePool). Consensus is a sound
fallback for any symbol-less binary.

-----

## 2026-07-25 - Three more Ghidra projects swept; GEngine gains UE5.5 (build 2401)

Followed the DropIn work by running the same audit over three donated Ghidra projects.
Net result: **one new signature, two demotions, and a clear "this one can't help" verdict.**

**Everspace 2 `ES2-0517` — UE 5.5, and the second symbolised oracle.** The project name's
`0517` is a **date, not a version**. There is no `++UE5+Release-` string in the image, so the
version was pinned **structurally**: `FFieldVariant`=0x08 (≥5.1.1), `UEnum::Names` still
`TArray<TTuple<FName,int64>>` (<5.6), `FUObjectItem` 24B **with `RefCount`@+0x14**, classic
`FChunkedFixedUObjectArray` order (<5.8), and — decisively — the PDB's
`EUnrealEngineObjectUE5Version` enum ends at `ASSETREGISTRY_PACKAGEBUILDDEPENDENCIES`, whereas
vendored UE 5.8 adds `METADATA_SERIALIZATION_OFFSET` / `VERSE_CELLS` after it. `dump_types.java`
gained enum support for exactly this: **the last member of that enum is the most reliable UE
version marker available when the build strings are stripped and there is no PE on disk.**

Audit found GObjects/GNames/GWorld/Sparse well covered but **GEngine hitting on only 1 of 4
patterns** — `GENG_X1`/`X2` both MISS on 5.5 because 5.5 emits `FEngineLoop::Tick`'s null check
as a NEAR `0F 84` where 4.27/5.7 use a short `74`, a length change no nibble can bridge.
Added **`GENG_ES55_1`** (`UEngine::GetEngineSubsystem<T>` prologue): UNIQUE-OK on **both** 5.5
(7 sites) and 5.7 (6 sites), zero hits on 4.27/5.3/4.20.

The obvious 5.5 `FEngineLoop::Tick` pattern was **rejected**: 6 hits on Avowed resolving to six
*different* globals. Recorded as a rule — **divergent hits mean a generic shape; accepted
patterns' extra hits all converge on one address.**

**Everspace 1 `ES1` — UE 4.20, no PDB.** Usable two ways. As a **negative control** it demoted
two GEngine patterns that had looked fine on a 3-binary sweep (`GENG_X1` 1 decoy, `GENG_DI427_1`
5 decoys here), so `GENGINE_PATTERNS` was reordered to put the three all-clean patterns
(`X2`, `ES55_1`, `SP57_1`) first. And via **pattern consensus** — an address independently
agreed on by N distinct signatures — it yielded truth without symbols: GWorld `0x1432E1AC0`
(12 patterns), GObjects `0x142E797F0` (8), GNames `0x1431DEAD8` (3). No GEngine/Sparse
consensus, as expected: 4.20 predates sparse delegates (4.23+).

**Satisfactory `Satfi426` — UE 4.26 modular: cannot help as supplied.** The .rep holds only 3
game DLLs; `FactoryGame-CoreUObject`/`-Engine`, which DEFINE the globals, were never imported.
The game module does carry the IAT slots (`__imp_?GUObjectArray` `0x180722950`,
`__imp_?GWorld` `0x180727CB8`, `__imp_?GEngine` `0x180727CB0`) — the "via `_imp_`" shape
`GOBJ_SF_1` already models, with `RipDeref` doing the second hop at runtime — but **all 490
referencing sites are game code** (`UFG*`/`AFG*`/`FFG*`), so nothing mined there would
generalise, and no existing pattern scores a correct hit on it. Re-importing those two DLLs
would make the project productive. (`find_syms3.java` stopped filtering `__imp_*` so the IAT
is visible at all.)

**Re-verified: all 12 DI427 signatures are 0-hit / 0-decoy on both new binaries**, so the
gauntlet now stands at five: DropIn 4.27, ES2 5.5, Solarpunk 5.7, Avowed 5.3, Everspace 4.20.

-----

## 2026-07-25 - DropIn UE 4.27 PDB: 12 new AOBs, a new GEngine target, and three corrected premises (build 2399)

**DropIn - VR Battle Royale** (Steam, `DropIn.exe`) is **UE 4.27.2** (`++UE4+Release-4.27-CL-18319896`,
2021-11-30) and ships its full **286 MB PDB** — the project's first symbolised UE 4.27 oracle.
It is a **Development** build (`.msvcjmc` + Live++ `.lpp_*` + `.uedbg` + engine source paths in
`.rdata`), non-editor. Ghidra project `D:\Tools\GHIDRA_Projs\DropIn.rep`.

**Method — the three-binary gauntlet.** Every candidate had to be `UNIQUE-OK` on DropIn (every
hit resolves to the true VA, zero decoys) **and** 0-hit-or-correct on Solarpunk (UE 5.7) and
Avowed (UE 5.3). This is stricter than the SP57 rule and it earned its keep twice: a 14-byte
GObjects form that is decoy-free on DropIn produces 1 decoy on Solarpunk and **9** on Avowed,
and making a sparse pattern register-agnostic with nibbles made it *worse* (picked up two
unrelated 0x60-stride `TSet`s that scan **before** the real sites — fatal, because
`ValidateSparseDelegates` only range-checks two ints).

**What the audit of the existing database found.** Replaying all 140 signatures over the whole
129 MB `.text`: GWorld 12 working, GNames 12, SparseDelegates 1 — and **GObjects 0 of 52**.
Root cause, measured across all 400 xrefs to `ObjObjects.Objects`: the chunk-load destination
register is rdi(156)/rsi(92)/r14(63)/rbx(40)/… and **never rcx**, because rcx is the *index*
register at every one of those sites. `GOBJ_V1` hardcodes `48 8B 0C C8` (dest = rcx), so the
entire V-series is structurally unable to fire. Compounding it, this build's `FUObjectItem` is
**32 bytes** (`StatID` compiled in), so the within-chunk math is `shl r,5`, not the 24-byte
`lea r,[r+r*2]; shl 4` the patterns assume.

**Added — 12 signatures (source tag `DI427`)**: `GOBJ_DI427_1/2/3`, `GNAM_DI427_1/2`,
`GWLD_DI427_1/2`, `SPARSE_DI427_1/2`, and 4 for the new GEngine target.
`GWLD_DI427_2` is the first `mov qword[rip], imm32` (C7-form) **store** pattern in the file —
that opcode shape was absent from all 52 GWorld entries, so this class of site was invisible in
every game, not just this one (note `totalLen = 11`, not 7).

**New — `AobTarget::GEngine`** (`Himmel.h`, `GENGINE_PATTERNS[]`). Resolves **`&GEngine`, the
static slot**, not the object. `GENG_X1` (`UWorld::GetGameViewport`) and `GENG_X2`
(`FEngineLoop::Tick`) are **cross-version** — verified on UE 4.27 *and* UE 5.7, and X1 also
matches Avowed (5.3). Two payoffs: `FindLiveGameEngine` stops walking the entire object pool
resolving a property offset per class (one deref instead), and — the user-visible one — a
GameEngine-rooted CE record can be AOB-wrapped like a GWorld-rooted one instead of baking in an
`allocateMemory` snapshot of a `UEngine*` that goes stale on restart. Scanned after
GObjects/GNames in `FindAll` because the validator asks the reflected class for `GameViewport`.
Surfaced over the pipe (`gengine`, `gengine_method`, `gengine_aob*`) and as a System-tab row.

**Three premises corrected by ground truth:**

1. **Sparse delegates on UE4 (feature unlocked).** `Aura.cpp`'s `UEVersion < 500` gate rested on
   "UE 4.23-4.27 keys the outer map by `FObjectKey {FWeakObjectPtr, int32}` (16B)". The PDB says
   the key is a raw `UObjectBase const*` — same as UE5 — and `FObjectKey` is **8** bytes
   (`{int32 ObjectIndex; int32 ObjectSerialNumber}`); `FWeakObjectPtr` *is* those two ints, so
   the old note double-counted. All six walker constants already matched 4.27 exactly. The gate
   is now a **runtime key-shape probe** (first occupied outer key must look like a userspace
   pointer), so 4.23-4.26 — for which we still have no symbols — fail safe rather than being
   guessed at. `SPARSE_ES2_1` was verified to resolve correctly on 4.27 all along.
2. **`Grimoire::FPROPERTY_ELEMSIZE` was 0x38 = `ArrayDim`, not `ElementSize` (0x3C).** Latent:
   only used when dynamic offset validation fails. The `Genau` heuristic was likewise
   `Offset_Internal - 0x14`; the correct delta is **0x10** in *both* known layouts
   (4.25-4.27/5.0-5.1 → 0x4C-0x3C, 5.1.1+ → 0x44-0x34).
3. **`DetectVersion` Tier 1 could essentially never match.** Its needles carry a trailing dot
   (`"4.27."`) but real tags are `++UE4+Release-4.27` with nothing after the minor. Tier 1 now
   drops the dot (the `++UEx+Release-` prefix is already all the context it needs) **and** runs a
   second UTF-16LE pass — DropIn keeps the tag *only* as a wide literal (4 copies, zero ASCII).
   `DetectVersionFromPEResource` also gained a StringFileInfo `ProductVersion`/`FileVersion`
   fallback: DropIn's is literally `++UE4+Release-4.27-CL-18319896`, an O(1) lookup we were
   ignoring. `kVersionDetectLogicRev` 1 → 2 so cached versions recompute once.
   *(Correction to an earlier read: DropIn **does** have a valid `VS_FIXEDFILEINFO` (4.27.2) —
   .NET's `FileVersionInfo` returns empty strings for it because they sit under a non-default
   translation, which is why it first looked absent.)*

**ProcessEvent detection hardened** (the primary pattern path was already correct — build 648 —
and this PDB is its 4th independent 4.27 confirmation at **+0x220**, the first with a symbol
proving the slot *is* `UObject::ProcessEvent`):
* `kBodySize` `0xF00` → `0x2000`. Measured: pattern 2 sits at byte **3537** of a 5182-byte
  `UObject::ProcessEvent` — 303 bytes (7.9%) inside the old window.
* `FindAnyValidVTable` → `CollectCandidateVTables` (up to 12 distinct). `AActor::ProcessEvent`
  is a thin override containing the FUNC_Native test but **not** the high-flag test, so the scan
  returns −1 for any Actor vtable and used to fall through to the version table.
* Version fallback: `>= 427 → 0x220`. `0x218` is slot 67 = `UObject::OverridePerObjectConfigSection`,
  one slot before PE — exactly the build-647 failure. 4.25/4.26 deliberately left at `0x218`:
  unmeasured, and RE-UE4SS's vendored `VTableLayout_4_2*_Template.ini` cannot settle it (computed
  absolute slots give 4.27 = 70/`0x230`, i.e. those templates are editor-inclusive and sit 2 slots
  above every non-editor measurement).

**Tooling landed in `tools/ghidra/`** — `scan_patterns.java` (supersedes `verify_aob.java`: TSV
input, nibble wildcards, and a decoy-**ordering** verdict), `extract_patterns.py` (whole
`Himmel.h` → TSV), `gen_cands.py` (xrefs → mechanically enumerated candidates), plus
`dump_xrefs2` / `dump_types` / `dump_vtables` / `pe_probe` / `dump_dataat` / `dump_func` /
`find_syms3` / `scan_strings` / `probe`. `tools/README.md` documents the full PDB→AOB loop.

**Layout facts now backed by real 4.27 symbols** (see technical-notes): `FProperty` ordering
confirms the hard-won `feedback-fproperty-layout` note exactly (`ArrayDim@0x38`,
`ElementSize@0x3C`, `PropertyFlags@0x40` — +4, not +8); `Offset_Internal@0x4C`,
`FStructProperty::Struct@0x78`, `FField Next@0x20/Name@0x28`, `Outer@0x20` are byte-identical to
what the tool already reports for SBDR; `UEnum::Names` is still the classic
`TArray<TTuple<FName,int64>>` (the `FNameData` change really is 5.6+); legacy `UProperty` does
not exist at 4.27; the `FNamePool` model (`FRWLock@0`, `CurrentBlock@8`, `Blocks[8192]@0x10`,
`0x20000`-byte blocks, stride 2) that `ValidateGNamesStructural` assumes is exact.

**Not done (deliberate):** re-rooting a *GWorld*-rooted CE export through GEngine when GWorld's
AOB fails. `GEngine→GameViewport→World` does re-enter the GWorld subtree, but it needs path-prefix
re-derivation — bigger than this change. Also unaddressed: the stride sweep still tries only
{16, 24, 20}, so a 32-byte `FUObjectItem` like DropIn's is not in the candidate list (adding 32
naively is unsafe — it aliases on 16-byte-item games, where it would validate and halve the
object count).

-----

## 2026-07-25 - AOB priority scheme widened 0–100 → sparse 0–1000 bands (build 2393)

`Himmel.h`'s AOB priorities were cramped in 0–100 with collisions (e.g. three GObjects patterns all at
10), leaving no room to insert a new pattern between two existing ones without renumbering. Re-spread
all 140 pattern entries across **sparse 0–1000 bands** (exports 0–30 · Tier-1 long/specific 100–290 ·
Tier-2 medium 300–490 · Tier-3 short 500–590 · patternsleuth 600–690 · UE4/legacy 700–790 · last-resort
800–990), stepping by 10 within a band so there's always room to slot a new pattern in. Done by a
verified script (`scratchpad/repriority.py`): it parses each `AobSignature[]`, re-bands each entry from
its old priority, and **asserts the new ordering is identical to the old** (`sort by new priority` ==
`sort by (old priority, textual position)`) with no duplicates — so **scan behaviour is unchanged**;
the only difference is that previously-tied priorities are now deterministic (a strict improvement — a
tie was resolved by an unstable `std::sort`). Absolute values are meaningless; only per-target order
matters. SP57 GWorld patterns are now pri 100–160 (were 10–13), SP57 Sparse 100–120 (were 15–16).
Header comment documents the bands + "pick an unused value in the matching band" rule.

-----

## 2026-07-25 - PDB-mined UE5.7 GWorld + SparseDelegate AOBs; the GWorld-decoy root cause (build 2383)

Solarpunk (rokaplay, `SolarpunkSteam-Win64-Shipping.exe`) is **UE 5.7 and ships a full 1.6 GB PDB** —
our first UE5.7 symbol oracle. Its reported "GWorld AOB fails" turned out to be a precise, now
fully cross-validated failure, and the PDB let us fix it with **verified-unique** signatures instead
of guesses.

**Root cause (two independent methods agree).** UE5.7's MSVC codegen shifted, so **all** priority-10-20
UE5 GWorld patterns (ES2_1-6, SF_1, GH_1/2, TQ_1/2, V1) get **0 hits**. The scan reaches the generic
`GWLD_SF_2` (pri 21), which matched a single **decoy** `.data` global `0x1478C25A8` (0xA8B0 below the
real GWorld) that the deliberately-loose `ValidateGWorldBasic` (readable + `LooksLikeDataPtr`)
accepted. `UE5_Init`'s secondary guard then caught it — scan-0.log / init-0.log:
`GWLD_SF_2: Unique match -> 0x7FF655F125A8` → `GWorld=0x7FF655F125A8 does not deref to a UWorld —
recovering...` → `recovered via instance_scan_recovery -> 0x7FF655F1CE58`. That recovered address
equals **exactly** the PDB symbol (`GWorld` RVA `0x78CCE58` + imagebase `0x7FF64E650000`). So GWorld
*worked via the fallback net*, but the AOB path failed and cached no fast hint (`gWorld.patternId`
saved empty → a relaunch re-scans clean, **no cache clear needed**).

**Fix (`dll/src/Himmel.h`, source tag `SP57`).** Four GWorld patterns at pri 10-13 (before the decoy
SF_2) + two SparseDelegate patterns at pri 15-16 (`SPARSE_ES2_1` got **0/21** on this build). **Every
candidate was scanned against the real `.text` and its every hit resolved the DLL's way before
inclusion** — kept only `correct>=1, decoys=0` (or decoys strictly higher-addressed so the real site
validates first):

| ID | pri | anchor | hits/correct/decoy |
|---|---|---|---|
| `GWLD_SP57_1` | 10 | UGameEngine::Tick `cmp [rcx+2C0]` (tolerates inserted `mov rcx,[rbx+rax]`) | 1/1/0 |
| `GWLD_SP57_2` | 11 | FMallocLeakReporter::WriteReports (`mov rsi,rcx` variant of GH_1) | 1/1/0 |
| `GWLD_SP57_3` | 12 | UEngine::GetWorldFromContextObject fallback | 1/1/0 |
| `GWLD_SP57_4` | 13 | UActorComponent::On*PhysicsState `mov [rax+298]` (0x298 version-specific → last) | 2/2/0 |
| `SPARSE_SP57_1` | 15 | TSet::Find/FindOrAdd/EmplaceByHash element index (mov rdx) | 5/3/2 (decoys higher-addr) |
| `SPARSE_SP57_2` | 16 | TSet::Remove element index (mov r8) | 1/1/0 |

(The FinishDestroy twin-ref candidate was **dropped** — verification found 1 decoy.)

**Reusable tooling** (`tools/ghidra/`): `dump_global_xref_aob.java` resolves UE globals by PDB name and
dumps per-xref raw window + disp-masked AOB + read/write kind + function; `verify_aob.java` scans
`.text` and resolves every hit exactly like `Genau::ScanForTarget`, reporting hits/decoys/correct so a
candidate is proven before it ships. Two traps baked into the headers: a ~GB PDB OOMs headless →
`export _JAVA_OPTIONS=-Xmx16G`; and never touch **variable** symbols (`getAddress()` lazy-loads the
whole datatype list → OOM) — filter `SymbolType.LOCAL_VAR/PARAMETER`.

**Corroborations (doc-only claims now backed by real symbols):** SparseDelegates outer key is a raw
`UObjectBase*` (symbol `EmplaceByHash<TKeyInitializer<UObjectBase_const_*_&&>>` — confirms the
"UE5.0+ raw-pointer key vs UE4.23-4.27 FObjectKey" note); UWorld member `+0x2C0` compared in
UGameEngine::Tick (a 3rd version behind ES2_3/SF_1's hardcoded 2C0); GObjects tool-convention =
symbol base **+0x10** (ObjObjects); GNames only 2 xrefs, both in `GetNamePool` (function-static).
The AOB parser is confirmed **full-byte `??` only** (no nibble wildcards) and the scanner is AVX2
single-anchor (`Macht::ScanRegion`), so all SP57 patterns use full-byte wildcards.

**LIVE-VERIFIED (build 2384).** Redeployed + re-tested on Solarpunk: `GWLD_SP57_1: Unique match ->
0x7FF6D4D1CE58` — the real GWorld (imagebase `0x7FF6CD450000` + RVA `0x78CCE58`), method **`aob`**, no
"does not deref" warning, no recovery. GWorld scan **143 ms (1 batch)** vs the old **1.24 s (2
batches)**. `SPARSE_SP57_1 -> 0x7FF6D4B1A4B0` — Sparse **found** (was `not_found`). `FindAll: Complete`
all four `(aob)`.

**Then — nibble wildcards + validator hardening (build 2387).**

*Nibble match.* `Macht::ParsePattern` now accepts nibble tokens: `4?` fixes the high nibble (matches
0x40-0x4F, i.e. any REX prefix), `?5` fixes the low nibble. Representation changed from a `{0,1}` mask
to a per-byte AND-mask (`0x00` wildcard / `0x0F`,`0xF0` nibble / `0xFF` literal) with pre-masked
`bytes`; every verify site is now `(mem & mask) == bytes`. **Perf is unchanged**: the AVX2 hot loop
still broadcasts a single full-literal anchor (nibble bytes are never chosen as the anchor); nibbles
only touch the sparse per-candidate verify. `ParsePattern` moved to `Macht.h` as `inline` (pure — no
Win32) so `dll_helpers_test` exercises the real parser (`Test_Macht_ParsePattern_Nibble`, 875/0).
First use: `GWLD_SP57_1`'s `?? 8B` → `4? 8B` (the inserted `mov rcx,[rbx+rax]` REX byte is 0x4A).

*Validator hardening (the decoy's root fix).* `ValidateGWorldBasic` was too loose (readable +
`LooksLikeDataPtr`) — that is what accepted the decoy in the first place. It now adds an
offset-independent C++-object guard: a real UWorld's `[[world]]` (vtable → first virtual method) is a
code pointer in a module image; a `.data` global that merely holds a data-shaped pointer fails it, so
the scan rejects the decoy and continues to the real GWorld. Because a rejected decoy can leave GWorld
= 0, the `UE5_Init` recovery gate (Frieren.cpp:388) no longer requires `ptrs.GWorld != 0` — so a
no-valid-AOB title (Avowed) still recovers. No regression for the 30+ tested games (a real GWorld
always passes the vtable check).

**Also found + FIXED — the M5 see-through-close crash (build 2389).** Exercising the
previously-untested M5 scenario (leave See-through ON, close the GAME) crashed the injected DLL:
event log Id 1000, faulting module VERSION.dll, `0xc0000409` (__fastfail). A WER minidump
(`tools/pe/minidump_triage.py`) showed the faulting thread stack was **pure `version.dll` + the ntdll
fail-fast chain — no game-engine frames**, i.e. the fault was in OUR worker thread, in our code. Root
cause: the See-through worker's per-tick invokes (`InvokeSetHidden`/`InvokeRetVec`) size a
`std::vector<uint8_t> buf(fi.parmsSize)` directly from a UFunction's ParmsSize, with **no upper
bound**. During the game's own shutdown a freed/reused UFunction reads a garbage-huge ParmsSize → the
vector throws `std::bad_alloc` → it escapes `WorkerLoop` (no handler) → `std::terminate` →
`__fastfail(0xC0000409)`. Fix, two levels: (1) `FindFuncByName` now rejects an out-of-range ParmsSize
(`> 0x4000`) so the per-tick invoke is a clean no-op instead of allocating; (2) `WorkerLoop` wraps
`Tick()` in `try/catch` so no exception class can ever `std::terminate` the host. **Dunste (Fly) had
the identical invoke-with-parmsSize twin** (`InvokeSetCollision`) → same two fixes applied. The four
field-writer workers (Solitar/Laufen/Hemmung/Solide) write via SEH-guarded reads/writes with no
game-sized allocation, so they have no equivalent trigger. (`UE5_Shutdown` is still never called on a
game-close — `DllMain(DETACH)` is a deliberate no-op — but that's now moot: the crash was the worker
faulting during the game's shutdown window, not the missing clean teardown.)
**LIVE-VERIFIED (build 2389, 2026-07-25, a DEBUG build — stricter):** re-ran the exact M5 repro on
Solarpunk — `SeeThrough: worker started` + a `Time:` re-assert worker both live, then closed the game.
**No crash, no dump, no event-log error** (vs. the 18:38 run that produced all three); the `try/catch`
"tick threw" line never fired, so the ParmsSize cap headed off the `bad_alloc` before it could throw.
GWorld still `aob` (GWLD_SP57_1, now a cached hint).

-----

## 2026-07-23 - MEASURED then SHIPPED: the lean walk payload (builds 2339 / 2351)

Build 2335 ended with "the next lever is BYTES, not messages" and an admitted blank: nobody had
measured how much of a `walk_instance` payload the CE export actually consumes. Two steps closed it.

**Measured first (build 2339).** `scripts/analysis/walk_payload_audit.py` byte-accounts every JSON
key of the `walk_instance` / `walk_instance_batch` responses in a UI pipe log against a key-by-key
map of what `CeXmlExportService` / `CsxExportService` really read (each verdict cites its consuming
line). On a real Copy CE XML on SEED - 6,778 responses, 14,263 instances, 27,002 complete field
objects:

| scope | share | used | csx-only | **unused** |
|---|---:|---:|---:|---:|
| `field` | 52.7% | 60.9% | 18.6% | **16.7%** |
| `elem` (inline array elements) | 20.3% | 43.9% | - | **44.6%** |
| `instance` (per-instance header) | 20.4% | 0% | - | **99.0%** |

The exporter reads `result.Fields` and nothing else, so the whole per-instance header is dead
weight; and the biggest single droppable key is `elements[].h` (element raw hex, ~9% of the entire
payload). Because CE XML output is **structural** - description + offset + CE type + drill-down -
every decoded VALUE the walk carries is dead for it. Full table in
[multipipe-eval.md](multipipe-eval.md) section 10.6.

**Then shipped (build 2351).** `lean: true` on `walk_instance` / `walk_instance_batch` omits exactly
those keys (drop list in [pipe-protocol.md](pipe-protocol.md)). Three properties make it cheap to
trust: it is **subtractive only** (a lean object is the full object minus keys, so no client needs a
new parsing branch and an older DLL that ignores the flag stays correct); the **default stays
full-fat** because `CsxExportService` calls the same `ResolveDrilldownAsync` and genuinely reads
`hex` / `bool_mask` / `bool_byte_offset`, so only the three CE XML callers opt in; and
`WalkInstanceLeanTests` runs the same export over full and lean payloads demanding **byte-identical
XML** - mutation-checked, so it fails when a key the exporter really reads is dropped.

**In-game verified (build 2353, SEED).** Same object, before (DLL 2338) vs after (DLL 2353):
**payload 1,982,875 -> 1,168,944 bytes across the same 134 batch responses, -41.0%** - section
10.6's prediction held. The XML is unchanged: 149,621 lines and 14,326 leaves both sides, with 15
differing lines that are all per-session values (the root heap address, and 14 DropDownList entries
whose FName ComparisonIndex moved while the name half stayed identical). DLL serialise time fell
20% (146.7 -> 116-119 ms) consistently across both runs.

**The wall-clock is still not claimed.** `ipc` did not move (207 -> 213-216 ms) despite the bytes
nearly halving - at ~15 KB per response over 134 calls, IPC is dominated by fixed per-call cost, so
this export is simply too small to attribute. Build 2335's lesson applies to its own successor:
repeat on the ~20k-call export before quoting a speed-up. Also of note from the audit tooling:
`UE5DUMP_PIPE_LOG_FULL=1` uncaps `PipeClient`'s 1024-char body log so the audit can sample whole
payloads instead of prefixes.

-----

## 2026-07-23 - RESULT: struct-tree batching is 1.71x, and the IPC cost model was wrong (build 2335)

`top:` names `walk_instance_batch` - the acceptance criterion - and the same Copy CE XML on SEED
went **5,893.3 -> 3,437.1 ms (1.71x)**, dispatches **22,522 -> 1,355 (16.6x fewer)**.

| | before | after |
|---|---:|---:|
| wall | 5,893.3 ms | **3,437.1 ms** |
| dll | 1,751.7 | 1,505.5 |
| **ipc** | **3,531.7** | **1,278.4** |
| ui | 609.9 | 653.2 |

**The projection was 2.4-3.5x; reality is 1.71x - and the gap is informative.** Section 10.4 flagged
the projection as an upper bound because it assumed batching adds nothing. The data now says
precisely what it adds: **IPC is not purely a per-round-trip cost.** At the old 0.157 ms/call, 1,355
calls should have cost ~212 ms; they cost 1,278 ms. Of the original 3,532 ms, **~2,253 ms was fixed
per-round-trip overhead (removed)** and **~1,066 ms is payload-proportional** - the same bytes still
cross the pipe however many messages carry them. `ui` rose 610 -> 653 ms for the same reason: bigger
JSON documents cost more to parse.

Two secondary findings, recorded in [multipipe-eval.md](multipipe-eval.md) section 10.5:
- **Average batch size is ~16.6**, far below the 200 chunk cap - the limit is struct fan-out, not
  the cap, so raising the chunk would achieve nothing.
- **Worst single dispatch rose 14.5 -> 85.2 ms** (a batch does ~16 walks). Harmless at that scale,
  but it is the exact metric Phase 1 cared about, and batching moves it the wrong way - one more
  reason those two ideas do not belong together.

**Next lever is BYTES, not messages.** The residual 3,437 ms is dll 1,506 (real work) + ipc 1,278
(mostly payload) + ui 653 (parse); trimming fields the export never reads would attack the last two
together. Left unquantified on purpose - nobody has measured what share of a `walk_instance` payload
the CE export actually consumes, and this cycle already showed what happens when a projection
outruns its measurement.

-----

## 2026-07-23 — The batching was aimed at the wrong loop; fixed at the struct tree (build 2335; dev, UI-only)

Build 2329 shipped `walk_instance_batch` and batched the CE export's object-pointer
drilldown. The next live run showed **no change at all** — 22,522 dispatches, still
`walk_instance 22,521x`, and no fallback warnings, so the batch command was simply never
called:

```
PERF Copy CE XML: wall 5,893.3 ms · busy 1,751.7 ms (29.7%) · 22522 dispatches
   · split dll 1,751.7 / ipc 3,531.7 / ui 609.9 ms · top: walk_instance 1,751.7ms/22521x
```

**The calls come from the STRUCT tree, not the pointer drilldown.**
`ResolveStructFieldsIntoAsync` → `ResolveStructRecursiveAsync` issues one
`walk_instance` per `StructProperty` and recurses into nested structs — and a UE class is
full of them (FVector, FTransform, custom structs, each nesting further). The
object-pointer loop that got batched is a minor contributor by comparison.

**Why the fix isn't "batch that recursion too".** `ResolveStructRecursiveAsync` produces a
**depth-first flattened list**, and that traversal order — with its accumulated
`Parent.Child` name prefixes and summed offsets — *is* the emitted CE XML's field order.
Restructuring it breadth-first would reorder every exported struct.

So: a separate **breadth-first prefetch** (`PrefetchStructTreeAsync`) walks the tree one
batched call per level, bounded by the same `MaxStructDepth`, and the **unchanged**
depth-first emit reads from that cache. Output order is preserved by construction, because
the emit traversal is literally the same code.

Details worth keeping:
- **One shared predicate** (`IsRecursableStruct`) decides both what the prefetch fetches and
  what the emit recurses into, so the two can't drift. A mismatch is harmless either way — a
  superset wastes a walk, a subset falls back to a live call — but matching is what makes it pay.
- **The cache is a pure optimisation.** Any miss (older DLL, failed batch, an unanticipated
  shape) walks live exactly as before. `PrefetchStructTreeAsync` swallows batch failures and
  returns what it has.
- **Dedup doubles as the cycle guard**: a self-referential struct is fetched once, then the
  depth bound stops the descent.
- Cache key includes the class address — the same data address walked as a different class is
  a different walk.

**Verification:** 2911 tests green (+4), the important one comparing batched vs
batch-disabled output field-for-field (names, types, offsets, order) over a deliberately
asymmetric tree. AOT publish clean. **Not verified: the speed-up** — that needs another live
Copy CE XML, and this time the check is simple: `top:` should show `walk_instance_batch`,
not `walk_instance`.

**Process note:** build 2329's claim rested on the round-trip count alone; a single grep of
the next PERF line for `walk_instance_batch` would have caught the miss immediately. That
check is now the stated acceptance criterion rather than the projection.

-----

## 2026-07-23 — `walk_instance_batch`: act on the measurement (build 2329; dev, DLL + UI)

Implements what §10.4 concluded. A Copy CE XML issued **20,357** single `walk_instance` calls whose
cost split as dll 30% / **ipc 59-73%** / ui ~0% — per call, 0.16-0.21 ms of round-trip overhead
carrying 0.08 ms of actual work. Collapsing the calls is the lever.

**Built to the `walk_class_batch` precedent, all three safety layers:**
- **Layer 1 — structural.** The DLL handler is a trivial `for` loop over `Ubel::WalkInstance`, the
  same function the single command calls. Equivalence is true by construction, not by promise.
- **Layer 2 — shared serialiser.** New `EncodeInstanceWalkToJson` on the DLL side (the single
  command's inline emit was extracted into it) and `DumpService.DeserializeInstanceWalk` on the UI
  side. One emitter, one parser, so the two paths cannot disagree about a field — including the
  **optional** keys (`is_definition` / `stale` / `props_size`), which is where an independently
  written batch encoder diverges first.
- **Layer 3 — equivalence test.** `WalkInstanceBatchEquivalenceTests` runs the same fixture through
  both paths and compares field-for-field, and covers chunk splitting, ordering, and both
  degradation paths.

**The export walks breadth-first per level now.** `ResolvePointerInstancesRecursiveAsync` collects
every pointer target at one depth, walks them in one batched call, then recurses. That restructuring
is what makes batching possible at all — targets at one depth are independent. The `visited` /
`resolved` guards deliberately stay outside the batch so cycle protection and dedup behave exactly
as before.

**Two failure modes, both degrading rather than losing data:**
- A batch that throws — including an **older DLL that doesn't know the command** — replays that
  chunk as single calls. Each can then fail independently, exactly as before batching existed.
- **A short or long reply also falls back.** Consuming N-1 rows positionally would silently attach
  one instance's fields to a *different* address — in a CE export that is a wrong pointer chain that
  looks perfectly valid. There is a test for precisely this.

**Verification:** all 4 proxies + DLL build clean; 2907 tests green (+7). **Not verified: the actual
speed-up** — the projection is 2.4-3.5×, but it is an upper bound (it assumes batching adds nothing,
while a larger payload costs something on both sides). The next live Copy CE XML will print its own
`split dll / ipc / ui` line and settle it.

-----

## 2026-07-23 — MEASURED: IPC is 59-73% of a heavy export; batch `walk_instance` (build 2327; docs)

The decomposition built earlier today, read on real data. Three Copy CE XML runs on **SEED BATTLE
DESTINY REMASTERED (UE 4.27)**:

| run | wall | dll | ipc | ui | calls | per call (dll / ipc / ui) |
|---|---:|---:|---:|---:|---:|---|
| A | 5,548.3 ms | 1,689.5 (30.5%) | **3,290.0 (59.3%)** | 568.8 (10.3%) | 20,357 | 0.083 / 0.162 / 0.028 ms |
| B | 555.1 ms | 157.7 (28.4%) | **406.3 (73.2%)** | 0.0 (0%) | 1,901 | 0.083 / 0.214 / 0.000 ms |
| C | 614.6 ms | 165.9 (27.0%) | **411.8 (67.0%)** | 36.9 (6.0%) | 2,108 | 0.079 / 0.195 / 0.018 ms |

**IPC is the cost — 59-73% of wall-clock, roughly 2× the actual DLL work — and it is exactly the
part batching removes.** The per-call figures barely move across a 10× spread in operation size,
which is what a fixed per-round-trip overhead looks like. UI-side per-result work is negligible
(0.000-0.028 ms/call), so the export tree building is *not* where the time goes — worth knowing,
because that was the other plausible suspect.

Projected at the established ~200/call chunk: **2.4-3.5×** (A: 5,548 → ~2,275 ms, 20,357 round-trips
→ 102).

**Treat that as an upper bound.** It assumes batching removes IPC proportionally and adds nothing,
whereas real batching serialises a larger payload and parses a bigger document — some of which
reappears in `dll` and `ui`. `ui` in run B hit the zero floor (transport ≥ wall), so it is
"negligible, at or below the measurement floor", not precisely quantified. And `dll` at ~0.08 ms/call
is untouched by batching: run A cannot go below its 1,689 ms of actual walking.

**This settles multipipe Phase 1 harder than the first measurement did.** Phase 1 targets the `dll`
share (27-30%); the cost is `ipc` (59-73%). It would have been aimed at the smaller half of the
wrong problem — and it had already been built and reverted once on that premise.

Recommendation recorded in [multipipe-eval.md](multipipe-eval.md) §10.4 and [todo.md](todo.md):
**batch `walk_instance`**, following the `walk_class_batch` / `search_properties_batch` precedent
*including* their three-layer equivalence safety net, because a silently dropped field in a CE
export is invisible until someone needs it months later.

-----

## 2026-07-23 — Decompose the per-call overhead: dll / ipc / ui (build 2327; dev, UI-only)

Build 2324 proved the dispatcher is not the bottleneck and pointed at `walk_instance`'s **20,357
round-trips per export** instead — 0.088 ms of DLL work carrying 0.208 ms of overhead. But it could
not say how much of that 0.208 ms **batching would actually recover**: pipe latency vanishes when
200 calls become one, UI-side per-result work does not. Promising a speed-up on that basis would
have been guessing.

**One new measurement closes the gap.** `PipeTransportStats` accumulates time spent inside
`PipeClient.SendAsync` (write → response). Combined with the two figures already collected, every
part of an operation is now accounted for:

| part | derivation | does batching remove it? |
|---|---|---|
| `dll` | Sense dispatcher busy | no — it is the actual work |
| `ipc` | transport − dll | **yes** — the round-trip itself |
| `ui`  | wall − transport | no — deserialise + per-result caller work |

The PERF line gained both the totals and the per-call breakdown, the latter at microsecond
resolution because the whole decision turns on sub-millisecond figures that `N1` would round away:

```
PERF Copy CE XML: wall 5,362.7 ms · dispatcher busy 1,651.3 ms (30.8%) · 20357 dispatches
   · split dll 1,651.3 / ipc 2,348.7 / ui 1,362.7 ms
   · (per call: dll 0.081 / ipc 0.115 / ui 0.067 ms)
```

Details worth keeping:
- **Transport is timed in a `finally`**, so a cancelled or faulted request still counts. Dropping
  those would flatter the IPC figure exactly when the pipe is misbehaving.
- **The probe subtracts its own two round-trips** from the call count, the same reasoning that
  already excludes its `get_diagnostics` from the busy total.
- **Monotonic snapshots, differenced** — never a reset — so two overlapping probes cannot clobber
  each other's baseline.
- **Every derived figure is floored at zero.** Concurrency or clock skew can make transport look
  smaller than the DLL time it contains, and a negative "ipc" in a log is worse than a wrong one.
- **Stated caveat:** transport is summed per call, so with both lanes sending concurrently the sum
  can exceed wall-clock. The heavy exports this measures are sequential, but the number is not
  exclusive time and must not be read as such.

Cost is one `Stopwatch.GetTimestamp` pair and two interlocked adds per pipe call.

**Verification:** 2900 tests green (+4), including the split arithmetic shaped on the real Copy CE
XML numbers, the omit-when-unsampled path, and the negative-guard. **Not verified: real split
figures** — that needs another live export, and is the point of the change.

-----

## 2026-07-23 — MEASURED: the dispatcher is not the bottleneck; Phase 1 is WON'T-DO (build 2324; docs)

The question this whole diagnostics chain was built to answer, answered. `multipipe-eval.md`'s core
claim — that DLL-side serial-dispatch head-of-line blocking is what makes the UI lag — was reasoned
in 2026-06 and never measured. It is now, on two games spanning both engine generations: **Elliot
(UE 5.4)** and **SEED BATTLE DESTINY REMASTERED (UE 4.27)**, 24,178 dispatches across five real
Copy CE XML / Copy CE Field runs.

**Dispatcher busy: 29.8% aggregate**, and remarkably stable — 22-31% across operations spanning
2.6 ms to 5.4 s. **Worst single dispatch out of 24,178: 14.3 ms.**

**Verdict: do not build Phase 1.** Three independent readings agree. The dispatcher is idle ~70% of
wall-clock, so non-blocking dispatch can only recover a slice of the busy 30% — and only if
something were queued behind it, which in a single-user export there is not. There is no
head-of-line spike to remove: nothing holds the read loop for more than a frame. And Phase 1 is
expensive — it was shipped and reverted once already (build 1840) and a correct version needs
overlapped/async pipe I/O.

**What the data says the real lever is: call count.** `walk_instance` is 100% of the dispatcher cost
in every single row, and one Copy CE XML issued **20,357** of them. Per round-trip: **0.088 ms
inside the DLL, 0.208 ms everywhere else** — pipe latency, JSON envelope, UI-side deserialise. **2.4x
the actual work is overhead.** Batching at the established ~200/call chunk (the pattern
`search_properties_batch` and `walk_class_batch` already use) would collapse 24,178 round-trips to
~121.

**Stated limit on that estimate:** this data cannot decompose the 0.208 ms into pipe latency (which
batching removes) and UI-side per-result work (which it does not). The trend is suggestive —
per-call overhead falls from 0.427 ms at 386 calls to 0.182 ms at 20,357 — but the split must be
measured before promising a number. Recorded as a candidate with that caveat rather than as a plan.

Written up in [multipipe-eval.md](multipipe-eval.md) §10 with the full table; Phase 1's status in
that document changes from "phased recommendation" to **WON'T-DO**, revisit only if a workload
appears whose *single* dispatches block for hundreds of ms.

**Also confirmed this run:** the winmm proxy on a second game — SEED is **UE 4.27**, so the proxy is
now verified across both engine generations, 180/180 exports forwarded on each.

-----

## 2026-07-23 — winmm proxy LIVE-VERIFIED; the PERF records immediately found two of their own bugs (build 2324; dev)

**winmm proxy works.** First live run, The Adventures of Elliot (UE 5.4):

```
DllMain ProxyStart: proxy DLL mode — starting pipe server only (no scan)
DllMain ProxyStart: pipe server started
[PROXY] winmm proxy: lazily forwarded 180/180 exports to real System32 winmm.dll
UE5_Init: Complete (UE504, GObjects=0x149BFF150, GNames=0x149B1B600, Objects=326364)
```

**180/180 forwarded**, and at T+1.2 s — i.e. lazily, on a game thread after DllMain returned, exactly
as designed rather than under the loader lock. Name sanity 10/10, full offset detection, GWorld
found. The proxy family is now version / dinput8 / dxgi / winmm, all four working.

**And the automatic PERF records earned their keep on their first outing** by exposing two defects in
themselves — which a hand-run measurement session would very likely have shrugged past:

```
PERF Copy CE Field: wall 57.7 ms · dispatcher busy 93 ms (161.2%) · top: walk_instance 0ms/128x max 15ms
```

**161% busy, and the breakdown contradicting the total.** Two independent causes:

1. **`GetTickCount64` was the wrong clock.** Its ~15.6 ms granularity floors every sub-tick dispatch
   to zero, so 128 `walk_instance` calls summed to "0 ms" while one that happened to straddle a tick
   read 15 ms. That is an artefact of tick alignment, not a measurement — and sub-millisecond
   commands are precisely the population this exists to measure. `Sense` now times with
   **QueryPerformanceCounter and accumulates microseconds**, reporting fractional ms on the wire
   (`total_ms` / `max_ms` / `last_ms` became doubles, and the C# model with them).
2. **The probe was measuring itself.** `busy` came from the global `total_busy_ms` delta, which
   includes the probe's own opening `get_diagnostics` (~93 ms) — while the per-command ranking
   already excluded it. Hence 93 ms of "busy" against a 57.7 ms operation whose top row showed 0 ms.
   Busy is now **summed from the per-command deltas**, so the percentage and the breakdown agree by
   construction rather than by coincidence.

Both are pinned by regression tests carrying the real numbers from the log. Note that a genuine
>100% remains possible and meaningful — the two-connection lane split can have two dispatchers busy
at once — so the figure is deliberately not capped.

**Verification:** all 4 proxies + main DLL build clean; 2896 tests green (+3). **Still to do: re-read
the PERF lines with the fixed clock** — the pre-fix numbers above understate sub-ms commands and
overstate short operations, so the multipipe Phase 1 decision should wait for fresh ones.

-----

## 2026-07-23 — Automatic PERF records around every heavy operation (build 2320; dev, UI-only)

The user's idea, and better than the manual measurement session it replaces: a deliberate test run
only ever covers the scenario somebody thought to try, and only if they remembered to reset the
counters first. Recording every real **Copy CE XML / Copy CE Field / Value Scan (First & Next) /
Snapshot capture** instead means the evidence for the multipipe Phase 1 decision accumulates from
actual use — including the combinations nobody would think to test.

New `Services/DiagnosticsProbe.cs` brackets each operation with two `get_diagnostics` snapshots and
writes one `PERF` line to the `view` log:

```
PERF Value Scan (First): wall 2,340.0 ms · dispatcher busy 1,980 ms (84.6%) · 7 dispatches
   · top: value_scan_begin 1900ms/1x max 1900ms, get_object_list 80ms/6x max 32ms
```

Design decisions that make the line trustworthy:
- **Deltas, not absolutes.** Absolute totals answer "what has this session done"; the question is
  what *this* operation cost. Wall-clock is measured locally rather than from the DLL's uptime, so a
  `reset_diagnostics` landing mid-operation cannot produce a negative duration — every figure is
  floored at zero and there is a test that fires a mid-operation reset.
- **The probe excludes its own calls.** The opening snapshot is itself a dispatch that lands in the
  closing one; without the filter every measurement would list the measurement.
- **`await using`**, so the closing sample happens even when the operation throws or is cancelled.
- **Never affects the operation.** No connection, an older DLL that doesn't know the command, a
  mid-operation disconnect — all swallowed, and `BeginAsync` returns a working no-op probe rather
  than null so call sites need no null handling. A diagnostic that breaks what it measures is worse
  than no diagnostic.
- **`MaxMs` is reported, not differenced** — it is a running high-water mark, so a delta would be
  meaningless.

Cost is two pipe round-trips (~0-125 ms each) around operations that run for seconds, so it is on
unconditionally rather than behind a flag.

**Verification:** 2893 tests green (+11), covering the delta arithmetic, the self-call exclusion, the
mid-operation-reset floor, and the zero-length-operation divisor guard. **Not verified: the lines
against a real heavy operation** — that is the point of the feature, and the next thing to look at.

-----

## 2026-07-23 — winmm.dll proxy: the spare slot (build 2317; dev, DLL + UI)

The 4th proxy. **Built on the slot-contention trigger, not the coverage one** — the n=24 census
(build 2313) stands: winmm and dxgi both cover 100% of installed UE games and winmm reaches exactly
zero that dxgi misses. What justifies it is the other half of that finding: **a proxy only works if
its filename is free.** `dxgi.dll` is the name ReShade and many mod loaders take, `version.dll` is
likewise a common ASI/mod-loader name (e.g. Ultimate ASI Loader), and with both gone the only
remaining choice was dinput8 at 2/24. winmm is the spare universally-viable slot, so users now get a real dxgi/winmm
choice.

**Generated, never hand-written.** New `scripts/gen_proxy_forwarders.py` reads the export table of
the real System32 DLL and emits all three artefacts in the shapes the dxgi proxy already uses —
`Lugner_Winmm.cpp` (the `mProcs[]` table + lazy System32 resolver), `Lugner_Winmm.asm` (180 MASM
lazy jmp-thunks), `ProxyWinmm.def` (`name = fN @ordinal` + our C ABI). At 180 exports hand-editing
was never an option; `--check` verifies the checked-in files are current. The generator carries the
two hard-won constraints in its header: jmp-thunks rather than C forwarders (a bare `jmp` forwards
ANY signature, which matters because the export table holds undocumented internals), and LAZY
resolution rather than eager DllMain (eager resolution crashed Octopath Traveler through the dxgi
proxy by running LoadLibrary under the loader lock).

**Verified against the real DLL rather than by inspection:** 180/180 forwarding exports present,
**every ordinal matching System32 winmm exactly**, zero missing, plus the 60-symbol UE5 ABI
including `g_invokeMailbox`. One ordinal-only export (@2, an internal) is skipped and reported by
the generator — a game importing winmm by ordinal would miss it; none does. And the proxy does
**not import winmm itself**, which is only possible because build 2301 moved Mimic's
`timeBeginPeriod` off a static import.

**Two more hardcoded proxy lists found and removed** while wiring this up — the same desync class
that had left the double-inject guards blind to dinput8/dxgi. `DumperModuleDetector.ProxyNames` and
`WindowsPlatformService`'s module filter both carried literal `{version, dinput8, dxgi}`; both now
derive from `ProxyType` through one `IsInterestingModuleName` helper. New `ProxyTypeCoverageTests`
walks every enum value through `GetDllName` / `GetDisplayName` / `FromDllName` / the module filter,
so a fifth flavour cannot be half-added again.

**Also in this build:** the Diagnostics card's process line now reads *"Game process (not the DLL)"*.
The figures are the whole game's — we are injected into it and there is no supported way to
attribute a working set to one module — and an unlabelled "7,453 MiB" next to our own diagnostics
read as ours.

**Verification:** all 4 proxies + main DLL build clean; 2882 tests green (+7). **Not verified: the
winmm proxy loading a real game** — that needs a deploy-and-launch and is the obvious next step.

-----

## 2026-07-23 — Diagnostics card: auto-refresh toggle + resizable columns (build 2315; dev, UI-only)

Two things the first live run made obvious.

**Auto-refresh (5 s), off by default.** The interval is deliberately unhurried, and the reason is
specific to this card: **every poll is itself a dispatch**, so a fast timer would inflate the very
numbers being reported — `get_diagnostics` already appears in its own table (8.6% of busy time on
the user's first run, from a single call). 5 s stays in the noise while making CPU% meaningful, since
that needs two samples to difference.

Three guards, all for the same reason — the measurement must not perturb what it measures:
- **Pauses on tab-leave, resumes on tab-enter** (`OnLeavingTab` / `OnEnteringTab`, wired the same way
  Live Funcs auto-stops its recording). A forgotten toggle would otherwise keep adding pipe traffic
  while the user works elsewhere. The checkbox stays ticked.
- **Never stacks requests** — a tick is skipped while one is in flight. The first snapshot measured
  125 ms; queuing polls behind each other would turn a timer into a burst.
- Toggling on fires one refresh immediately rather than making the user wait a full interval.

**Resizable columns.** The numeric columns had been sized to their content and were clipping their
own headers ("Cou", "% bu") with no way to widen them — `CanUserResizeColumns` was never set. Now
explicit, with `MinWidth` so a dragged column can't collapse. **Sorting is explicitly OFF**: Avalonia's
DataGrid sort is reflection-based (an AOT hazard — see `ui-avalonia12-pinvoke-gotchas`), the rows
already arrive ranked by total time, and switching it off reclaims the header space the sort glyph
was reserving, which is part of why the headers fit now.

One self-inflicted compile break worth noting: adding `vm.Pointers?.OnLeavingTab()` made the
compiler treat `vm.Pointers` as nullable from that point on, breaking a pre-existing non-null
dereference three lines later. `Pointers` is a non-nullable property; the `?.` was wrong, not the
old code.

-----

## 2026-07-23 — Diagnostics fix: a UINT64_MAX sentinel on the wire blanked the whole card (build 2311; dev, DLL + UI)

First live run of the new Diagnostics card failed outright with *"An element of type 'Number'
cannot be converted to a 'System.Int64'"*.

**Root cause.** `Stark::MsSinceLastHookFire()` returns `UINT64_MAX` for "the PE hook never fired —
liveness unknown", and build 2308 put that straight on the pipe. 18446744073709551615 does not fit
an `Int64`, so `GetValue<long>()` threw and took the entire panel with it. **"Never fired" is the
NORMAL state on a fresh connection** — the hook installs lazily on the first invoke — so this was
the default path, not an edge case.

**Why it took longer than it should have:** `System.Text.Json` emits the *identical* message for
out-of-range and for fractional values, and names neither. That sends you hunting for a decimal
point. The raw payload settled it in one grep — the UI's own pipe log had
`"ms_since_last_fire":18446744073709551615` sitting there the whole time. Recorded in
[lessons-learned.md](lessons-learned.md): grab the payload before theorising.

**Two fixes, because either alone would be insufficient.**
- **Wire boundary (DLL).** The sentinel is now mapped to `-1` before serialising. An in-process
  `UINT64_MAX` convention is fine; the wire is a narrower type system and sentinels must land in the
  range the other side can parse.
- **Reader (UI).** New `Services/JsonNum.cs` — saturating, non-throwing `L/I/D/B` reads, now used
  throughout the diagnostics parse. **Telemetry must degrade, never throw:** one odd field is worth
  a wrong number in one cell, not a blank panel the user opened to debug something else. `JsonNum.D`
  also collapses non-finite values, since a `NaN` reaching a format string prints "NaN%" at the user.
  A pre-fix DLL still works — `UINT64_MAX` saturates to `long.MaxValue`, which `HasFired` reads as
  "unknown" rather than as a plausible age.

**UI:** the card now prints *"never fired (hook installs on first invoke)"* instead of a nonsense
age. **Tests:** +17, including the verbatim failing payload from the log and both causes of that
ambiguous STJ message pinned separately. 2875 green.

-----

## 2026-07-23 — Diagnostics (`Sense`): measure what the pipe traffic actually costs (build 2308; dev, DLL + UI)

Tier 1 + Tier 2 of the performance-counter evaluation. Exists for one reason:
[multipipe-eval.md](multipipe-eval.md) names DLL-side **serial-dispatch head-of-line blocking** as
the root cause of UI lag and game-thread CPU starvation as the CE-mailbox risk — and **nothing
measured either**, so "should Phase 1 (non-blocking dispatch) be built?" was a blind decision. Now it
isn't.

**New `Sense` module** (Frieren roster: Second-Exam proctor, "scythe" — the roster's own suggested
use for that name was *harvest-collection*). Records per-command dispatch cost — count / total / max
/ last — plus Win32 process facts and game-thread health. New pipe commands `get_diagnostics` /
`reset_diagnostics`, both pipe-only.

**Where the timing is taken matters.** Fern already brackets `DispatchCommand` with an `inFlight`
flag, documented as the CPU-bound stretch that never touches the pipe. That span *is* the window
during which the connection's dispatcher is unavailable to anything else — so it is exactly the
head-of-line blocking in question, and the measurement needed no new chokepoint.

**The headline number is `busy_percent`** — what fraction of wall-clock a dispatcher was occupied.
High, with a lagging UI, is the case *for* Phase 1; low says the lag is elsewhere and Phase 1 would
not help. The per-command table ranks by **total** rather than max, because the question is which
command *owns* the dispatcher, not which one spiked once — `max_ms` is reported alongside because
that is the spike a user actually feels.

Three deliberate choices:
- **Dedicated mutex.** Borrowing a lock a long scan also holds would make the diagnostics contend
  with the very thing they exist to measure. Cost when idle is a map lookup and a few adds per
  command — noise next to commands that are microseconds at best.
- **CPU% is `-1`, not `0`, until a second sample exists** to difference against. The UI renders that
  as an em dash: "0%" would read as *idle*, a different and wrong claim. Normalised by core count so
  100 means one whole machine, matching what a user sees in Task Manager.
- **Tier 2 is on demand only.** Thread count walks a system-wide `TH32CS_SNAPTHREAD` snapshot (no
  cheaper documented API), so it never runs unless a client asks.

**UI:** System tab → *Diagnostics — DLL dispatch cost*, placed directly above the existing Pipe
Activity card — that one shows *what* crossed the pipe, this shows what it *cost*. Refresh + Reset
counters; the reset re-reads immediately so the card shows a live empty baseline rather than looking
broken. Counters also reset when the last client disconnects, so one session's numbers never
pollute the next.

**Not built, deliberately:** per-worker tick counters for Solide / Hemmung / Laufen / Solitar /
Schlacht. That means touching five modules for a number that does not bear on the dispatch question,
and the dispatch question is what blocks a decision. Recorded in [todo.md](todo.md) as the natural
follow-on.

**Verification:** 2858 tests green (+12). DLL + all 3 proxies build clean. App launched to confirm
the new card and its `DataGrid` bind without error. **Not verified: the numbers themselves against a
live game** — that needs an attached session, and is the point of the feature rather than of the
code.

-----

## 2026-07-23 — Modular UE builds: fold the engine modules into the proxy-import hint (build 2308; dev, UI-only)

Fixes the LOW-severity defect the n=24 proxy census turned up. `ReadProxyImports` was handed
`game.ExePath` only. In a **modular** build the exe is a thin bootstrap — Satisfactory's is 264 KB,
with the engine split across ~182 sibling `*-Win64-Shipping.dll` modules — so the analyzer saw no
dxgi/dinput8 and the Suggested-proxy column claimed `version · default · no dxgi/dinput8` for a game
where a dxgi proxy loads perfectly well (`D3D12RHI` imports it).

A proxy activates if **any** module in the process imports that name — the loader searches the exe's
directory whichever one asks. So when the exe imports none of the three (`ImportsNone`, the
bootstrap-stub signature), the sibling modules are now folded in with `Merge`, a pure OR. The
file-walking half stays in `ProxyDeployService`; `ProxyImportAnalyzer` remains OS-free and
synthetic-PE-testable by design.

**Measured, not assumed:** Satisfactory goes from *nothing* to `version + dxgi` in **30 ms** for all
182 modules (header-only parsing), and a monolithic game is untouched at 0 ms because the fallback
never triggers. The 512 cap is a runaway guard, not a budget — 182 is walked in full, since the
all-three short-circuit cannot fire on a build that imports no dinput8.

Severity was LOW throughout: imports are advisory context the analyzer never lets override the
version default, so the harm was a misleading hint string, not a wrong deployment. +5 tests.

-----

## 2026-07-23 — Stop statically importing winmm: resolve the 1 ms timer from System32 (build 2301; dev, DLL)

Clears the hard prerequisite the winmm-proxy evaluation identified. Correct on its own, and shipped
separately from the proxy so the two can be judged independently.

**The trap.** `Mimic.cpp` raises the timer resolution for the CE-mailbox poll thread
(`timeBeginPeriod(1)` / `timeEndPeriod`), and it lives in `UE5_COMMON_SOURCES` — the object library
linked into the main DLL *and every proxy*, with `Winmm` in both link lists. The day a proxy target
**is** `winmm.dll`, our own static import of `winmm.dll!timeBeginPeriod` resolves against the module
of that name in the process — **ourselves** — landing in our forwarding stub. Before the stub has
resolved the real export it returns 0, and **0 is `TIMERR_NOERROR`**: no crash, no error, the call
just silently does nothing while `Sleep(1)` degrades to the 15.6 ms tick and mailbox latency gets
~15× worse. Delay-loading would not have helped (a delay-load `LoadLibrary("winmm.dll")` from the
game folder finds us again), and no test would have caught it — `dll_helpers_test` linked `Winmm`
into the test exe, so its latency assert passed regardless of proxy behaviour.

**The fix.** `Mimic.cpp` resolves both functions from the **System32** copy by explicit path
(`GetSystemDirectoryW` → `LoadLibraryW` → `GetProcAddress`); Windows keys loaded modules by full
path, so this yields the genuine OS winmm even with a same-named proxy of ours mapped. `Winmm` is now
absent from `UE5Dumper`'s link list, `PROXY_LINK_LIBS`, **and** `dll_helpers_test`. Unresolvable →
returns a non-zero rc so the existing "log and proceed" path runs and the paired `timeEndPeriod` is
skipped; the worst case was always a graceful degradation to system Sleep granularity, never a
correctness break. The helper is deliberately proxy-agnostic (no `UE5_PROXY_*` test), which is what
lets `Mimic.cpp` stay in the shared object library — an `#ifdef` would have violated that invariant
and forced the file out of the compile-once set.

**Verification — objective, not by inspection.** Parsing the built PE import tables shows
`winmm.dll` is gone from `UE5Dumper.dll`, all three proxies, **and** the test exe. The poll-latency
micro-benchmark was reworked to resolve the same way rather than through a linked import, so it now
covers the real mechanism; it measures **1.95 ms/sleep** (194.9 ms for 100 × `Sleep(1)`) — a silent
no-op would have landed near 15.6 ms/sleep. `dll_helpers_test` 845 pass / 0 fail (+4), UI 2846 / 0.

-----

## 2026-07-23 — Undeploy removes every proxy flavour of ours, not just the selected one (build 2299; dev, UI-only)

**Reported bug.** With `dxgi.dll` deployed and the radio switched to `version.dll`, *Undeploy* did
nothing — `UndeployAsync` only ever looked at `proxyType.GetDllName()`. The user was left unable to
remove the proxy at all, while the grid cheerfully reported `DeployedOtherType` at them (the
*detection* side has handled all flavours since build 2134 via `deployedProxyNames`; only the removal
was type-scoped).

**Fix: undeploy is type-agnostic.** The radio governs what to *deploy*; undeploy is a clean-up, so it
now sweeps every flavour we ship. `UndeployAsync` lost its `ProxyType` parameter entirely rather than
keeping a misleading one. It still only deletes files that are **ours** (`IsOurProxyDll` →
`FileVersionInfo.ProductName`); a foreign `version.dll`/`dxgi.dll` (mod loader, another tool) is left
alone and named in the message.

Three decisions worth keeping:
- **Per-file try/catch.** One locked DLL must not abandon the rest — removing what we can is the
  point, and the locked one is reported by name.
- **Refusing a foreign DLL is only a FAILURE when we removed nothing of ours.** Otherwise it's a note
  on an otherwise successful clean-up (`NotDeployed` + "Left another program's version.dll").
  A locked file outranks both, since it's the actionable one.
- **The policy is pure and separately testable.** `PlanUndeploy` (which files) and
  `ResolveUndeployOutcome` (status/message/success) are static and side-effect free, because
  ownership is decided by a PE version resource — fabricating one in a unit test would test the
  fixture, not the policy. `AllProxyDllNames()` is now shared with the refresh path.

**Verification:** 2846 tests green (+12 pure-policy cases covering the exact reported combination,
the all-three sweep, the foreign-DLL spare, and the locked/foreign precedence). The real
file-touching path was additionally exercised once against the **actual built proxies** in
`dist\proxy\` (real `ProductName`, so `IsOurProxyDll` ran for real): both of ours deleted, a foreign
`version.dll` kept and named. That integration check was not kept as a test — it would depend on
build outputs being present.

-----

## 2026-07-23 — CE autorun helper: every table gets `ue5_inject()`, permanently (build 2297; dev, UI-only)

The fourth and last delivery route, and the only one needing **neither** the standalone `.CT`
**nor** the AOBMaker plugin. **Tools → Install CE autorun Helper** writes `ue5_autorun.lua` into
`<CheatEngine>\autorun\`, which CE executes at start-up — so `ue5_inject()` / `ue5_shutdown()` then
exist in **every** table, plus a **UE5CEDumper: Inject DLL** entry in CE's main menu. Takes effect on
the next CE start.

**Finding Cheat Engine without new plumbing.** The install directory comes from a *running* CE
process via the existing `GameProcessInfo.Path` (`ListGameProcessesAsync(showAll: true)` — CE isn't a
UE game, so the UE-only filter would hide it), falling back to the save dialog when CE isn't running.
Deliberately not the registry: that would need a new platform-abstraction surface, and a running CE
is both the common case and the authoritative answer for *which* install of several is in play.

**The early-startup API risk is designed out, not tested away.** Autorun runs before any process is
attached, so the file **only defines things at load time** — every process-dependent call sits inside
a function the user invokes later. A unit test enforces it by parsing top-level statements and
rejecting `injectDLL` / `getOpenedProcessID` / `readInteger` / `executeCodeEx` / `showMessage` there.
The one genuinely uncertain call, `getMainForm().Menu`, is `pcall`-wrapped: if the form isn't ready
the menu is simply absent and `ue5_inject()` still works from the Lua console — a cosmetic extra must
never break someone's CE start-up. The menu API shape (`createMenuItem` / `parent.add` / `.Caption` /
`.OnClick`) is copied from the verified precedent in `vendor/UE4 Dumper.CT` rather than invented, per
the CE-API rule. A `ue5_menuAdded` global makes a manual re-run idempotent.

**Shared readiness emitter.** With two generators plus the `.CT` all needing the same
"wait until the DLL is actually up" loop, it now lives in one place —
`Services/CeReadinessLua.cs` — so the offsets, timeouts, and the two properties that matter
(pure memory read, never `executeCodeEx`; symbol resolved *inside* the loop) cannot drift.
`CeInjectScriptGenerator` was refactored onto it; the three failure messages are shared too, so both
routes give the same diagnosis for the same state.

**Route ranking updated to four** across the Proxy Deploy panel line and the Deploy / Inject /
bootstrap / autorun tooltips, and a new **"Getting `UE5Dumper.dll` into the game — which of the four
routes?"** recipe leads [tips.md](tips.md).

**Verification:** 2834 tests green (+12). The generated Lua was parsed with a real Lua parser (whole
file for the autorun helper, per-`{$lua}`-block for the record) — the shape assertions alone would
not catch a syntax error. **LIVE-VERIFIED 2026-07-23** — Cheat Engine picks the file up at start-up
and the route works end to end, which also settles the early-startup API question the evaluation
flagged: `getMainForm().Menu` is reachable from `autorun\`.

-----

## 2026-07-23 — Push the "Inject DLL" record into the CE table you already have open (build 2295; dev, UI-only)

Kills the two-stage table load. Cheat Engine holds **one table at a time**, so using the standalone
`scripts/UE5CEDumper.CT` meant: open ours → inject → open the game's own table → the injection entry
is gone. New **Tools → "Add \"Inject DLL\" Record to Current CE Table"** generates the same bootstrap
as an `[ENABLE]`/`[DISABLE]` memory record and pushes it into whatever table CE currently has open,
via the AOBMaker plugin's existing `CreateAAScript` (grouped under `UE5CEDumper (DLL)` so it doesn't
litter the user's root). **The standalone `.CT` is unchanged and still shipped** — it stays the
developer / no-AOBMaker path.

**Zero new plumbing.** `CreateAAScript` already wrote into the open address list (Teleport and
LiveWalker invoke have used it for builds); the bootstrap simply had no generator behind it. New
`Services/CeInjectScriptGenerator.cs` + one Tools command; no DLL, pipe, or CE-plugin change.

Carried over from the build-2291 `.CT` work, so both routes behave identically: **polls the DLL's
mailbox `initState` instead of sleeping a fixed budget** (pure memory read via `g_invokeMailbox`,
never `executeCodeEx` — games block `CreateRemoteThread` during start-up), resolves the symbol
**inside** the poll loop (CE's symbol handler may not see the fresh module on the first try), and
treats a timeout as a real error rather than printing "probably fine". `[DISABLE]` may use
`executeCodeEx` because by then the game is running normally.

Improvements over the `.CT` version:
- **The DLL path is baked in.** The UI already knows where `dist\UE5Dumper.dll` is (same resolution
  as `ProxyDeployViewModel.InjectIntoRunningGameAsync`), so there is no run-time directory search,
  and a missing DLL is reported by the UI *before* generating rather than failing inside CE.
- **`[DISABLE]` is a quiet no-op when nothing was ever loaded.** `[ENABLE]`'s early bail-outs set
  `memrec.Active = false`, which makes CE run `[DISABLE]` against a DLL that never loaded; it now
  probes for `UE5_StopPipeServer` first and stays silent instead of reporting a false failure.
- Falls back to CE record XML on the clipboard when AOBMaker isn't reachable, distinguishing "pipe
  broke mid-send" from "CE was never running".

**Route guidance (build 2296).** Three delivery routes now exist and they are not equally good, so
the ordering is stated where each choice is made: an always-visible line in the Proxy Deploy panel
(`str.ProxyDeploy.RouteOrder`) ranks **① deploy a proxy DLL** (loads with the game, survives
restarts, no CE at all) → **② inject into a running game, or push the bootstrap record into your open
CE table** → **③ the standalone `dist\UE5CEDumper.CT`** (developer fallback, and the only route
needing no AOBMaker plugin); the Deploy / Inject / Tools-bootstrap tooltips each name their own rank
so the ordering stays consistent. In-panel rather than tooltip-only on purpose — that panel is where
the decision happens, and a tooltip is only found by someone who already knew to look.

**Verification:** 2822 tests green (+17). **LIVE-VERIFIED 2026-07-23** — the pushed
*UE5CEDumper: Inject DLL + Start Pipe Server* record was ticked in a real CE table and injected +
came up correctly. `CeMailboxLayout` gained `OffInitState` + the five
`InitState` values so the offsets stay single-sourced. The emitted Lua was additionally checked by
running both `{$lua}` blocks through a real Lua parser — the shape assertions alone would not have
caught a syntax error. For the route-guidance strings the tests prove nothing (no string-resource
coverage test exists), so those were checked by confirming every key resolves, then launching the
app: `ProxyDeployPanel` is instantiated directly by `MainWindow.axaml`, not lazily, so a clean start
with an error-free init log means the new `StaticResource` resolved. The wrapped layout itself was
not visually inspected.

Two test-only traps worth remembering: `Assert.DoesNotContain("\0", s)` **always fails** — the string
overload is culture-sensitive and under ICU a NUL has zero collation weight, so it "matches" at
position 0 of any string (use the `char` overload, which is ordinal); and asserting
`DoesNotContain("executeCodeEx(")` misses `pcall(executeCodeEx, ...)`, so the check now strips Lua
comment lines and asserts on the bare identifier against code only.

-----

## 2026-07-23 — CE `.CT` inject: poll for readiness instead of sleeping 15 s; double-inject guard learns dinput8/dxgi (build 2291; dev)

Two small fixes to the Cheat-Engine injection path, both from the 2026-07-23 evaluation batch
in [todo.md](todo.md). **LIVE-VERIFIED 2026-07-23** — the `.CT` route was run against a real game
(CE Lua is not unit-testable, so this was the only way to confirm it). The `Methode.cpp` half of the
double-inject guard is only reachable via CE's *Inject && Connect* plugin menu item and has not been
exercised.

**1. The 15 s blind wait is now a 250 ms poll.** `scripts/UE5CEDumper.CT` `ue5_inject()` used to
`sleep(1000)` fifteen times and then print "complete (or failed — check DLL log)" **without ever
checking anything**: a normal run (its own comment budgets "1 s thread delay + ~2-8 s AOB scan")
wasted 5-10 s, and a *failed* run still reported success.

The readiness signal is a new `Mimic::InitState` published into the mailbox
(`IDLE`/`RUNNING`/`READY`/`FAILED`/`SKIPPED`), written by `UE5_AutoStart` (`Frieren.cpp`) and both
`AutoStartThreadProc` flavours (`Heiter.cpp`, proxy + CE-inject). CE Lua reads it with
`getAddress("g_invokeMailbox")` + `readInteger` — **a pure memory read**, deliberately not
`executeCodeEx`, because the script's own step-1 comment says `CreateRemoteThread` is avoided here
(games block it). Timeout raised to 25 s but only reached when genuinely wedged; a timeout is now an
**error** (`showMessage` + `return`), as is `FAILED`. `SKIPPED` (another instance owns the pipe, or
we are the CE plugin host) proceeds — a pipe server *is* up.

Three details worth keeping:
- **`initState` reuses the former `reserved` alignment slot** at `MailboxData+0x0C` (same type, same
  offset) ⇒ struct layout unchanged, so no proxy `.def` needed a new `DATA` entry and the UI's
  mailbox offsets are untouched.
- **The symbol is resolved inside the poll loop, not once up-front.** CE's symbol handler may not
  have picked up the just-injected module yet; a single failed `getAddress` would have silently
  dropped back to the blind wait and lost the entire benefit. A 5 s grace period, then the old
  fixed wait as fallback for pre-`initState` DLL builds.
- **`READY`/`FAILED` are published only after `UE5_StartPipeServer` returns**, so a poller that
  observes `READY` can connect immediately. `UE5_Shutdown` resets to `IDLE` (load-bearing for the
  path where `Mimic::StopThread` early-returns and its whole-struct `memset` never runs).

**2. The double-inject guard only knew the *old* proxy pair.** `Methode.cpp`
`IsAlreadyLoadedInTarget` and the `.CT`'s `ue5_isAlreadyLoaded` both tested `version.dll` /
`winmm.dll` — **neither checked `dinput8.dll` or `dxgi.dll`, the two proxies we actually ship**
(`winmm` was aspirational; no such proxy exists). A user running the dxgi or dinput8 proxy got no
guard at all and could double-map. Both sites now drive off a named list (`kProxyDllNames` /
`UE5_PROXY_DLL_NAMES`) carrying all three real flavours, with cross-references so a future 4th
flavour can't desync them.

**Verification:** DLL + all 3 proxies build clean; `dll_helpers_test` 841 pass / 0 fail; UI suite
2805 pass / 0 fail. The `.CT`'s embedded Lua was checked by parsing the table as XML and running
every `{$lua}` block through a real Lua parser — which also caught that the new `<` / `>=` operators
needed XML-escaping (`&lt;` / `&gt;=`), since this table stores Lua as escaped text, not CDATA
(precedent: the pre-existing `2&gt;nul` shell redirect at `UE5CEDumper.CT:110`).

-----

## 2026-07-23 — Teleport Coordinate Library: unlimited labelled positions, CSV + CE-Lua round trip (builds 2257-2267; dev, UI-only)

**P1-P5 all shipped, 2777 tests green, ZERO DLL/pipe change.** An unlimited, labelled +
grouped, filterable list of positions persisted per game, with pick→confirm→teleport, CSV
export/import, and a CE-Lua picker in both needs-DLL and no-DLL flavours. The 3 DLL marker
slots are untouched and stay what they were (DLL-side, hotkey-driven); this is a separate
curated UI-side list. Teleport reuses the existing explicit-coordinate path — `teleport_recall_marker`
with x/y/z (`DumpService.cs:3107`) and mailbox `CMD_TELEPORT` op 13 — so nothing DLL-side moved.

Design contract: [teleport-coord-library-spec.md](teleport-coord-library-spec.md).

**P1 (2257)** — `CoordEntry` + `CoordinateLibraryStore` + a collapsed-by-default Expander card
below "Teleport to Coordinates". Three deliberate deviations from the `BookmarkStore` pattern
it otherwise clones: keyed by **exe module name, not PE hash** (bookmarks hold offsets and
*should* die on a game patch; a hand-curated 4 000-entry list must not); the JSON context omits
`WhenWritingDefault` so a legitimately-saved `0.0` coordinate is written rather than reloading as
0 *by accident*; and it keeps a rolling `.bak` that `Load` falls back to on a corrupt main file.
Entries carry an opaque **`uid`, never `id`** — AOBMaker's `CtIdRenumberService` classifies a
script as an ID-check script on `RxIdField` alone and would silently renumber `id = N` literals in
any `.CT` the user later renumbers.

**Precision (D4)** resolves two reviewers' opposite recommendations: round to **3 dp at capture**,
then format shortest-round-trippable. The stored double is then the nearest double to a 3-decimal
literal, so the text is a clean `67162.398` **and** the round trip is bit-exact and idempotent —
neither the lossy `0.0###`/`0.000` helpers nor a bare `"R"` on an unrounded double achieves both.
It also denoises rotator values like `1.4210855e-14` to a clean 0.

**P2 (2260)** — CSV, scheduled *before* the Lua export because it is what users actually curate
4 000 rows in, and its import-report machinery is reused by P4. The repo had **no** CSV reader or
writer, so every rule is stated: RFC 4180 quoting (one unquoted comma in a label shifts every later
column); split positionally with **no `RemoveEmptyEntries`** (the obvious template, `BugItGoParser`,
uses it and would collapse an empty Group from 10 fields to 9, shifting Map→Group and X→Map);
**UTF-8 WITH BOM**, a documented exception to the house BOM-less rule because a BOM-less CJK export
opens as mojibake in Excel on a zh-TW box; delimiter sniffing with comma-decimals when it is `;`;
and formula-injection armouring (`=Boss Arena` displays `#NAME?` and Excel saves the *displayed*
text, destroying the label with no error).

**Import is two-stage and that is the point.** Excel silently coerces `1-2` to a date and `0012` to
`12` and writes back the displayed text, producing perfectly valid CSV no validator can reject. So
stage 1 parses with per-line diagnostics and a **cell-level diff** against the current library;
stage 2 commits after an explicit Apply, writing a `.preimport.bak` first. Merge identity is
uid-first then `(Label, Group, Map)` case-insensitively, **never** the coordinates — a spreadsheet
rewrites coordinate text, so coordinate matching would fail on every row and turn a 4 000-row merge
into 8 000. Export writes the **model, never the view**.

**P3 (2263)** — `-- @UE5CD:COORDS v1` … `-- @UE5CD:END`, named-field records, plus AOBMaker's
`---- GENERATED CODE (do not edit below) ----` separator verbatim. Shape adapted from
`@AOBMAKER:AA_TOGGLE v1` but deliberately in **our** namespace: AOBMaker's end marker is the
feature-less shared `-- @AOBMAKER:END` matched by unanchored `IndexOf`, so a block of ours pasted
into an AA Toggle script would make AOBMaker slice from its own start marker to our END, parse zero
entries, and **silently untick every record in the tree**. Escaping moved into one
`CeLuaHygiene.EscapeLuaString` rather than adding a fifth private copy. The picker is built only
from CE controls verified in the wild (`CrimsonDesert.CT` CheatEntry 357) — **`createListBox` and
`createComboBox` appear nowhere in this repo and were not used**. Confirm-before-teleport is two
buttons (no CE yes/no API is verified); interactive, so the untick lives on `OnClose`, never the
momentary auto-close.

**P4 (2265)** — brace-balanced re-import, tolerating reordered fields, missing commas, entries split
over lines, inserted comments and unknown keys from a newer build. Four AOBMaker parser defects
deliberately **not** inherited (each confirmed by running its shipped assembly): version baked into
the marker literal so a v2 block reads as "no block"; unanchored marker matching that hits inside
string literals; **100 % silent failure**; and single-quoted values silently ignored. The `.CT`
clipboard form decodes all **five** entities `CheatTableBuilder.EscapeXml` emits — `ExtractAssemblerScript`
reverses only three — but only when the paste really is XML.

**P5 (2267)** — the no-DLL flavour, added by threading a `Flavour` through the same generator. Both
flavours emit the **byte-identical** data block (asserted) so the round trip cannot drift. It is
honest about what it gives up: no map guard (the map name is only readable through the DLL), the
existing weak-raw-write caveat, staleness on a game patch, and a runtime `UE5T_ready` guard.

**Three bugs caught by the tests as they were written**, each a silent-corruption class:
CSV `Write` did not round, so an entry built by any path other than a pose capture broke
`Write → Parse → Write`; the Lua parser treated a key *present with a non-numeric value*
(`x=oops`) as absent and imported it as 0 with no diagnostic; and the first draft of the picker
hand-wrote mailbox offsets and got all but one wrong (they now come from `CeMailboxLayout`, with a
test).

**Experimental-gated (build 2269, user call).** The card carries
`IsVisible="{Binding ExperimentalEnabled}"` like the other five Teleport cards. The design draft
had argued it should *not* be gated ("a coordinate bookmark list is not combat-affecting") — too
narrow, since it writes the pawn position live and emits CE scripts that do the same. Gating also
fixes the quick-jump menu for free: the code-behind already skips a card that is not
`IsEffectivelyVisible`. Two lifecycle consequences fell out, both fixed: an un-applied import
preview is cancelled when the gate goes off (it would otherwise sit behind a hidden card where the
user can neither see nor cancel it), and — **a pre-existing bug the gating work surfaced** — it is
also dropped when the active game changes, since the diff was computed against the *previous*
game's library and applying it would have written those rows into the new game's file.

**NOT yet verified in-game.** Nothing here has executed a line of the emitted Lua, and the teleport
itself needs a live game. The CSV path has not met a real spreadsheet.

-----

## 2026-07-22 — CE-Lua escaping: closing long brackets of any level could break the AOBMaker push (build 2256; dev, UI-only)

**Three latent bugs in the script emitters, all one root cause.** AOBMaker's CE plugin wraps
the **entire** submitted script in a Lua long bracket at a **hardcoded** level —
`mr.Script = [==[ … ]==]` (`AOBMaker/plugins/CEPlugin/src/pipe_server.cpp:857`) — and does
**not** escape the script body (only `description`/`group` go through `EscapeLuaString`). Its
`InjectTableFile` sibling is safe because it calls `PickLongBracketLevel` to pick a
non-colliding level; `HandleCreateAAScript` does not. So the byte sequence `]==]` must not
appear **anywhere** in an emitted script, in **any** Lua context — including inside a quoted
string, where it is harmless to Lua itself.

1. **`BakedScriptGenerator.EscapeLuaComment`** neutralised `]]` only. `]=]`, `]==]`, `]===]`…
   passed straight through. Now scans for `]` + `=`* + `]` and pads after the leading `]`,
   breaking a closing bracket of any level.
2. **Pre-existing, found while fixing #1:** a trailing `]` in the escaped text fused with
   `MarkUnparsed`'s own `]]` into `]]]`, closing the comment **one character early** and
   leaving `] 0` as dangling syntax — `x]` produced `--[[unparsed:x]]] 0`. The old
   `abc]]def` test never caught it because that input ends in a letter. Now padded.
3. **Found during the audit — `BakedScriptGenerator.EscapeLua`** had the same hole and is
   equally user-reachable: the string-param path (`:467`) emits the invoke param dialog's
   free text as a Lua literal. Padding would corrupt the value, so the leading `]` becomes
   the decimal escape `\093` — same runtime string, different source bytes. 3 digits because
   `\ddd` greedily takes three (a following digit would fuse into `\931` > 255); by
   construction the next char is always `=` or `]`, so that is belt-and-braces.
   `FreezeScriptGenerator.EscapeLua` got the same case to keep its documented
   *"mirrors BakedScriptGenerator's escape rules"* claim true.

Reachability: `MarkUnparsed` (`InvokeParamDialog` → unparseable numeric/pointer/bool param)
and the string-param literal are the **only two** places arbitrary user-typed text enters a
generated script. Both are now covered. Deliberately **not** changed —
`InvokeScriptGenerator.EscapeLua` and `StandaloneTrainerScriptGenerator` (which interpolates
into Lua literals with *no* escaping at all): engine/const-derived inputs today, documented in
`todo.md` to revisit if they ever take free text.

**Verified 2026-07-22: nothing under `ui/` or `scripts/` emitted `]==]`**, so all three were
latent, never active. 8 new tests (adversarial long-bracket inputs at both entry points, plus
a regression guard that ordinary values escape unchanged); 2562 green.

Surfaced by the Teleport Coordinate Library evaluation — see
[teleport-coord-library-spec.md](teleport-coord-library-spec.md) §4, which turns the same
finding into that feature's character-blocking rule.

-----

## 2026-07-22 — Snapshot DB: concurrent first-open could silently DROP a just-captured snapshot (build 2252; dev, UI-only)

**Data-loss fix, found by chasing a CI-only test flake.** `SnapshotStore.OpenAsync` ran
`EnsureSchemaAsync` on **every** open with no mutual exclusion. That method reads
`PRAGMA user_version` and, when it reads low, `DROP`s `snapshots`/`objects`/`fields` before
re-`CREATE`ing them. Two connections opening the same **brand-new** DB both read `0`, so the
slower one could DROP the tables — and the committed rows — the faster one had just written.
No exception is raised on that path: the capture simply disappears.

Reachable in the shipping app, not just tests: `SnapshotViewModel.SetEngineState` ends with a
fire-and-forget `_ = RefreshAsync()` (its own open, on a thread-pool thread) while the user can
press Capture immediately, whose `CreateSnapshotAsync` + `BeginCaptureSessionAsync` open the same
file. Local machines always won the race; a loaded CI runner interleaved.

Three distinct races lived in that window:

1. `PRAGMA journal_mode=WAL` was **first** in the pragma batch — ahead of `busy_timeout=5000` —
   so the pragma that needs a brief exclusive lock ran under the default **0 ms** timeout and
   returned `SQLITE_BUSY` on the spot. `busy_timeout` now goes first.
2. The `user_version` read → DROP → CREATE sequence above (the data-loss one).
3. `AddColumnIfMissingAsync` is read-then-`ALTER`; a tie throws `duplicate column name: is_usable`.

**Fix:** schema init now runs **at most once per (DB path, process)** behind a per-path
`SemaphoreSlim`, with a double-checked `s_schemaReady` memo; only the *first* open of a file pays
the gate, so the pipelined capture's producer/consumer connections still open concurrently. The
memo is invalidated in `DeleteAllSnapshotDatabasesAsync` — a whole-**file** wipe (as opposed to a
row purge) would otherwise leave the memo describing a file that no longer exists, and the next
open would skip `CREATE TABLE` and die on `no such table: snapshots`.

Regression cover: `SnapshotStoreTests.ConcurrentFirstOpens_OnFreshDb_DoNotRace` (12 iterations,
fresh dir each — reproduced the row loss on the first run before the fix) and a re-open assertion
appended to `DeleteAllSnapshotDatabases_RemovesEveryGameFileFromDisk` (verified to fail when the
memo invalidation is removed). The CI-only symptom was
`SnapshotViewModelTests.Capture_StreamsAllChunks_PersistsWithCorrectCounts` failing at
`Assert.True(dump.LastGameOnly)` → `null`; that assert and its two neighbours now carry `Diag()`
too, since the prior `Diag()` sat 8 lines later and printed nothing. Tests 2545 → 2546.

-----

## 2026-07-19 — MindsEye: GNames solved — obfuscated FNameEntry payloads decoded from the fork's own key table (build 2238; dev, DLL needs re-inject)

**LIVE-VERIFIED on MindsEye game version 7.3.1 ONLY** (PE hash `0863E3B90C993000`; the exe carries no
game-version resource, so pin the build by that hash). **Name sanity 10/10** — GNames had never been
found for this title. Every RVA below is build-specific; re-derivation playbook in
[mindseye-fork-notes.md](mindseye-fork-notes.md). Experimental-gated end to end; a title without the
fork's fingerprint runs byte-identical code.

**The format.** MindsEye keeps the STOCK UE5 `FNameEntryHeader` but inserts a field and obfuscates
the payload:

```
+0x00  u16 header   stock: bIsWide:1 | LowercaseProbeHash:5 | Len:10   (len = header >> 6)
+0x02  u16 tag      NON-STOCK — selects this entry's XOR key
+0x04  chars[len]   single-byte XOR (stock puts chars at +0x02), 2-byte aligned
```

`FNamePool` = RVA `0x0BA306C0` (`FRWLock`=0, `CurrentBlock`, `CurrentByteCursor`, `Blocks[]` at
`+0x10`). Block 0 decodes under `0x09` to the canonical EName list — `None`, `ByteProperty`,
`IntProperty`, … — every length matching `header >> 6`.

**The key is per TAG, not per block** — an early hypothesis that cost a build to disprove. Observed:
tag `0x0001`→`0x09`, `0x0002`/`0x0082`→`0x5B`, `0x0003`→`0x1D`, `0x0016`→`0xA3`, `0x0036`→`0xE3`,
`0x0061`→`0xC9`.

**Where the key comes from.** The fork's de-obfuscator (RVA `0x0178B440` ANSI, `0x0178B540` wide)
does `len = header>>6`, `memcpy(dst, entry+4, len)`, then `KeyDerive(ctx, u16 @ entry+2)`
(RVA `0x0178CF50`) and `xor byte ptr [rax], dl` with an SSE `xorps` fast path. `KeyDerive` is an
open-hash probe under `RtlAcquireSRWLockShared`:

```
ctx +0x10 entries   +0x18 count   +0x44 sentinel (== count => empty)
ctx +0x48 inline buckets   +0x50 bucket array (0 => inline)   +0x58 capacity (pow2)
bucket = tag & (capacity-1)
entry = 24 bytes: +0x00 u16 tag | +0x08 u64 (LOW BYTE = key) | +0x10 i32 next (-1 = end)
```

**We read that table directly and NEVER call the routine.** Calling it was designed, adversarially
reviewed and dropped: `KeyDerive` takes the SRW lock *before* probing, so a fault inside it would
unwind out of game frames with the lock still held and permanently wedge every later
`FName::ToString` — no crash, no log, and `Tot` is a cooperative poll with no poll point inside game
code. Its ctx getter also reads `gs:[0x58]` with a lazy per-thread init branch, so calling it from a
thread the game never used is its own hazard. Reading the table has neither failure mode.

**Locating ctx** — all static, no control transfer: `Sig::AOB_NAMEDECRYPT_ME1` (Himmel, new `ME`
source tag) matches the de-obfuscator's semantically anchored prologue (unique in the 145 MB
`.text`; the 16-byte MSVC prologue alone hits 139x), then `match + AOB_NAMEDECRYPT_ME1_CTX_CALL_OFF`
(`0x2F`) `E8 rel32` -> the getter, and the getter's first `48 8D 05` rip-relative LEA -> ctx
(RVA `0x0BA47700`). Live: `decrypt fn=0x7FF60492B440 getter=0x7FF604931050 keyTable=0x7FF60EBE7700`
— all three exactly the RVAs confirmed in CE.

**Changes.**
- `Flamme::IsExperimentalEnabled()` — reads the SAME `%LOCALAPPDATA%\UE5CEDumper\experimental.json`
  the UI's `ExperimentalGate` writes, so the DLL honours the toggle on every entry path (UI pipe
  scan, CE Lua `UE5_Init`, proxy auto-start) with no protocol change. Missing/malformed => OFF.
- `Genau::TryObfuscatedPool` — appended LAST inside the `ValidateGNames` block-offset loop, after
  both stock layouts are rejected for that candidate. Acceptance is the SAME standard as stock:
  entry 0 must decode to exactly `"None"` — found in ONE compare, not 256, since a single-byte XOR
  onto `"None"` exists only if the three inter-byte deltas already match — then >=6 chained
  identifier entries must corroborate. The AOB scan runs only after all of that passes, so an
  ordinary title never scans it. No key table => the pool is REFUSED (decoding block 0 alone is not
  name resolution).
- `Serie::InitObfuscated` — adopts the geometry Genau proved instead of running the stock detectors
  (they all hunt a literal `"None"` in the payload, impossible before decryption), so no stock
  detection path is modified, only bypassed on this one branch. `s_payloadGap` is 0 for every stock
  title, so `strStart` resolves to the same address as before.

**Two bugs found on the way, both mine:**
1. *Heuristic key recovery was wrong.* The first design brute-forced a key per block behind an
   identifier-charset filter. It rejected 135 of 465 blocks: real pools are full of asset paths
   (`/Game/Storm/Animations/...` fails a `[A-Za-z0-9_]` gate) and wide entries aborted the walk.
   Superseded entirely by the table read; every heuristic deleted.
2. *A memory-ordering race produced wrong keys.* The tag->key cache wrote value then flag as two
   plain stores; nothing stops the compiler publishing the flag first, so another thread saw
   "resolved" with a still-zero key and XORed with 0 — `Object` rendered as `Fkclj}`. The same tag
   resolved to `0x09` on one thread and `0x00` on another **in the same millisecond**. Fixed by
   collapsing value+flag into ONE `std::atomic<uint16_t>` (bit 8 resolved, bit 9 miss, bits 0-7 key)
   — a single word cannot tear — plus a decode retry, since the table is LIVE (the game adds names
   as it runs) and a lock-free read can catch it mid-update.

**What is NOT recoverable, and why that is not a defect.** MindsEye ran a symbol-rename pass over
its own non-engine symbols at build time: property and class names are generated 16-character
all-lowercase identifiers (`wcxugjojsqaqvers`, `eurngjogndgrjhls`, ...). Proven, not inferred —
those strings appear verbatim in the exe's `.rdata`, and the binary holds **21,635** distinct 16-char
all-lowercase tokens. Length comes from the header and is key-independent, so a wrong key could
never produce them. Engine symbols (`LocalPlayers`, `NetTimeSyncComponent`, `AnalyticsComponent`, ...)
are untouched and read normally. The original names exist nowhere — not in memory, not in the
binary — so no tool can recover them.

Live Walker now walks `GWorld -> PersistentLevel -> StormWP -> EVMindsEyeGameInstance ->
LocalPlayers -> LocalPlayer -> BP_PlayerController_C` with correct values, classes and outer chains.

> The `GWorld does not deref to a UWorld — recovering...` line in an earlier session log is a
> scan-time timing artifact, not a defect: `GWorld` is a `UWorld**` static slot and `*GWorld` was
> still null while the game was loading. Later runs log no warning and `Start from GWorld` works.

-----

## 2026-07-19 — MindsEye: GObjects solved via preset-bound item layout; GNames located + name obfuscation reverse-engineered (build 2220; dev, DLL needs re-inject)

**LIVE-VERIFIED on MindsEye (Build A Rocket Boy, UE 5.4.4 licensee fork) — game version 7.3.1 ONLY**
(PE hash `0863E3B90C993000`). The first game in the matrix where `GNames=MISSING`, and the first where
the tool **reported `GObjects=OK` on garbage**. Every RVA below is build-specific; see
[mindseye-fork-notes.md](mindseye-fork-notes.md) to re-derive them after a game update.

**What was actually wrong (two independent bugs).** The AOB *did* find the real `GUObjectArray`
(RVA `0x0BB139B0`) and `ValidateGObjects` **rejected** it — the existing `"MindsEye"` preset is written
relative to `FChunkedFixedUObjectArray`, but the AOB resolves the `FUObjectArray` base, `0x10` earlier,
so `num@+0x14` read `NumChunks=9` and failed the `num < 0x1000` gate. The relaxed Tier 2 fallback then
accepted an unrelated heap blob (an ICU-like locale object containing the ASCII text `"International"`)
because its `numOff` landed on the **high half of an adjacent module pointer** — `Num=32758` is literally
`0x7FF6`. Result: `Count=509`, `named=0`, every lookup empty, init reporting `GObjects=OK`.

**Ground truth by offline disassembly** (capstone + the `.pdata` RUNTIME_FUNCTION table — no Ghidra;
`.text` is 145 MB so headless analysis was not worth the hours). `.rdata` still carries the `__FILE__`
anchors (`J:\work\e18f6e32b612e2cd\Engine\Source\Runtime\CoreUObject\Private\UObject\UObjectArray.cpp`),
so xref → containing function → rip-relative globals recovers everything:
`FUObjectArray::AllocateObjectPool` (RVA `0x019B17B0`) pins the five chunked fields, and the
index→object accessors (e.g. RVA `0x0191AA10`: `shr rcx,0x10 / movzx edx,bx / shl rdx,5 /
add rdx,[r9+rcx*8] / cmp qword [rdx+0x10],0`) pin the item layout.

| | value |
|---|---|
| `GUObjectArray` | RVA `0x0BB139B0` (`ObjObjects` at `+0x10`) |
| chunked fields | MaxElements `+0x10`, NumChunks `+0x14`, MaxChunks `+0x20`, NumElements `+0x24`, Objects `+0x28` |
| `FUObjectItem` | **32 bytes**, `UObject*` at **`+0x10`** (stock: 24 / `+0x00`) |
| elements per chunk | 65536 (stock) |

Matches the vendored Dumper-7 `MindsEye` layout exactly (`vendor/Dumper-7/.../ObjectArray.cpp:60`).

**Changes (all additive; the shared detection paths are byte-for-byte unchanged for other titles).**
- `Genau.cpp` — appended `{ 0x28, 0x10, 0x24, 0x20, 0x14, "MindsEye-Extended" }` as the **last** strict
  preset (the `Default → UE5-Extended` relationship already in that table).
- `Aura.cpp` — same row appended **last** in Tier 2 `s_ue4ExtendedPresets`. Cannot steal an existing
  title: `ValidateChunkedLayout` needs `maxChunks ∈ [6, 0x5FF]`, and on a UE5-Extended array this row
  reads `MaxElements` (~2.1M) as `maxChunks`.
- `Aura.cpp` — new **preset-bound `LayoutPreset::itemHint {stride, objOff}`**, consumed by
  `DetectItemSize` *before* the shared sweep and only when the winning preset carries one.
  **Deliberately not another entry in `candidates[]`:** MindsEye's 32/`+0x10` item aliases perfectly
  with stride 16 (every odd 16-byte slot is a real object pointer → `good=100 / bad=100`), so putting
  it in the shared sweep would let it outscore the true stride on genuine stride-16 titles
  (Titan Quest II, Octopath Traveler). Still evidence-gated (`hGood >= 8 && hBad*4 <= hGood`, which the
  50%-aliased result cannot reach); on rejection it logs and falls through to the unchanged sweep.
- `Genau.cpp` — relaxed Tier 2 rows gained a mirrored `maxOff` feeding an **upper-bound-only** check
  (`kRelaxedMaxCeiling = 0x4000000`, 8× the strict cap; skipped entirely if the read fails).
  Deliberately weaker than Tier 1 (no `max < num`, looser ceiling) so a title that raises
  `gc.MaxObjectsInGame`, or whose max field is elsewhere, still reaches the accept path as before.
  Kills the blob by 3.5× (its `max` reads `0x0DE62600` = 233M).
- `Genau.cpp` — GNames diagnostics: `ValidateGNames` now logs the **candidate pool base** (previously
  never logged, so a `chunk0` could not be attributed) plus **raw header bytes** instead of `name` —
  `name` is memset before the log and only ever filled after a length match, so the empty name in every
  historical log was a **logging artifact, not evidence about the game**. Added a 96-byte dump of the
  block itself on its own budget (the 7×2 per-offset probes used to exhaust the shared 10-line throttle).

Rejected after adversarial regression review (3 hunters × 5 rules): a pointer-high-bits reject rule
(would kill The Artisan of Glimmith at 24K objects — the qword covering `numOff` on a real array is
`Max | Num<<32`, which is itself in userspace range), an Aura quality floor, and a DEGRADED-report
trigger keyed on relaxed-tier acceptance (fires on correct resolutions, e.g. Avowed).

**Result:** `Layout 'MindsEye-Extended' detected (strict)` → `FUObjectItem size=32, object-ptr
offset=+0x10 (preset item hint) — 200 total, 0 bad` → `Count=530638, ItemSize=32`. Was
`Count=509, ItemSize=16, bad=100`.

**GNames — located, still unresolved (names ARE obfuscated).** The new block dump identified the pool
immediately: `FNamePool` = RVA `0x0BA306C0` (`FRWLock=0`, `CurrentBlock=507`,
`CurrentByteCursor=0x197D4`, then 64 KB-aligned `Blocks[]` at `+0x10`). The entry format keeps the
**stock UE5 header** but inserts a field and encrypts the payload:

```
+0x00  u16 header   stock: bIsWide:1 | LowercaseProbeHash:5 | Len:10   (len = header >> 6)
+0x02  u16 tag      NON-STOCK — the lookup key for this block's XOR key
+0x04  chars[len]   XOR-obfuscated (stock puts chars at +0x02), 2-byte aligned
```

Block 0 decodes under XOR `0x09` to the canonical hardcoded EName list — `None`, `ByteProperty`,
`IntProperty`, `BoolProperty`, `FloatProperty`, `ObjectProperty` — every length matching `header >> 6`.
The key is **per block**: block 0/1/2/3/6 = `0x09` / `0xE3` / `0x81` / `0xE7` / `0x33`, decoding
`None` / `GameplayTargetDataFilterHandle` / `RigUnit_DebugTransform` / `GetBoneTrackByName` /
`NetConnPacke…`. Encrypted bytes are identical across sessions at different addresses, so the key is
deterministic, not address-derived.

Decrypt routine found: **RVA `0x0178B440`** (ANSI; `0x0178B540` is the wide twin, `add r8,r8`). It does
`len = header>>6`, `memcpy(dst, entry+4, len)`, then `KeyDerive(ctx, u16 @ entry+2)` (RVA `0x0178CF50`)
and a byte-wise `xor byte ptr [rax], dl` with an SSE `xorps` fast path. `KeyDerive` is **not** closed
form — it takes a lock and does a hash-map probe (`bucket = tag & (capacity-1)`) into a runtime table
at ctx RVA `0x0BA47700`. So the key cannot be computed offline; it must be read from the live table or
obtained by calling the game's own routine. Follow-up (Plan A): AOB the decrypt function and route
`Serie` through it for this title.

**Also corrected:** the pak/IoStore container AES (CUE4Parse `MindsEyeAes.cs`) is real but irrelevant —
that is asset-load-time, not process memory. The binary itself is unpacked, has no Denuvo/EAC/BattlEye,
and stock `GWLD_ES2_6` / `SPARSE_ES2_1` still match uniquely.

-----

## 2026-07-15 — Time Dilation: dual-row World + Player levers, held simultaneously (builds 2207 + 2215; MERGED main PRs #442/#443, tag `v2215`)

**UI-ONLY — zero C++/pipe/mailbox change** (both commits touch only `ui/` + `docs/tips.md` + the build
number). The DLL already supported this: `Hemmung` keeps a per-target slot (`s_dils[DIL_COUNT]`, one
shared re-assert worker started while *any* lever is active), `set/reset_time_dilation` take a target,
and `get_time_state` has always returned **both** the Global and Pawn knobs in one reply. The single
slider + "Player only" checkbox shipped at build 2149 was the *only* thing making the two levers
mutually exclusive — so this is the UI catching up to the DLL, not new capability.

**Why it matters:** UE multiplies the world's effective dilation into `AActor::CustomTimeDilation`, so
the pawn's real rate is always **world × pawn**. Holding both at once is what produces classic
bullet-time — **World 0.5× + Player 2× = the player at normal speed inside a half-speed world** — and
it was unreachable from the card before.

- **Dual-row card** ([TeleportPanel.axaml](../ui/UE5DumpUI/Views/TeleportPanel.axaml), `3a91ba0`,
  build 2207) — "Whole world (`AWorldSettings.TimeDilation`)" and "Player pawn
  (`AActor.CustomTimeDilation`)", each with its own slider, preset row, **Apply**, **Reset**, badge and
  live readout, plus one shared **↻ Refresh both**. Reset is strictly per-lane: it resets its own
  target and snaps only its own slider to 1×; the DLL restores that lever's captured base and stops the
  worker only when no lever remains active.
- **Lane-parameterised VM core, not duplicated code**
  ([TeleportViewModel.cs](../ui/UE5DumpUI/ViewModels/TeleportViewModel.cs)) — `private enum TimeLane
  { World, Pawn }` + `LaneKey()` (`"global"`/`"pawn"` pipe target) + `LaneName()`, with
  `ApplyTimeAsync`/`ResetTimeAsync`/`ApplyTimePresetAsync(lane, …)` cores behind six thin
  `[RelayCommand]` wrappers. `ApplyTimeDilationReadout` fills BOTH lanes from one `TimeState`;
  `RefreshHeldTimeStateAsync` (connect-time read-back) syncs each slider only when *that* lever reports
  Active, so an inactive lever keeps its disk-persisted preference. **Add a lever by extending the
  enum, not by copying methods.**
- **"Combined player speed" readout + ceiling raise** (`a1fbe64`, build 2215) — the Player row shows a
  live `Combined player speed: 0.895×  (world 0.298 * pawn 3)` line so *Whole world* also slowing the
  player is never a surprise; the tooltip documents the **Player = 1 ÷ World** compensation. It is a
  pure C# getter over the **two slider values** (reactive via `[NotifyPropertyChangedFor]` on both
  lanes) — an intent/preview readout, *not* live state; the state readouts remain
  `World/PawnTimeCurrentText` fed from `get_time_state`. The **pawn slider ceiling went 3× → 10×
  (1000%)** for `CustomTimeDilation` super-speed; **World deliberately stays 0–3×**. Both are far
  inside the DLL clamp `Grimoire::TIME_DILATION_MIN/MAX = [0, 100]`. Card gained the dim subtitle
  "slow-mo / bullet time / freeze / fast-forward", echoed into the exported CE Lua `[ENABLE]` comment.
- **CE export bakes each record at its own value**
  ([TimeDilationScriptGenerator.cs](../ui/UE5DumpUI/Services/TimeDilationScriptGenerator.cs)) — new
  `BuildBatchRows(worldValue, pawnValue)` (the single-`double` form kept as a convenience overload
  delegating to it). The two records **"Time: World"** and **"Time: Player"** can be ticked at the same
  time — they serialise cleanly through the single-slot `CMD_TIME` mailbox, so enabling one never
  clobbers the other.
- **Persistence keys renamed** ([UiOptionsSettings.cs](../ui/UE5DumpUI/Models/UiOptionsSettings.cs)) —
  `TeleportUiOptions.TimeDilation` + `TimeTargetIsPawn` → `WorldTimeDilation` + `PawnTimeDilation`
  (both default 1.0). ⚠ **No migration**: unknown JSON members are ignored, so a user crossing build
  2207 silently loses their saved value/target once and falls back to 1.0/1.0.
- **Strings** ([en.axaml](../ui/UE5DumpUI/Resources/Strings/en.axaml)) — dropped `TP.TdPawnToggle` +
  `Tip.TP.TdPawnToggle`, `TP.TdRefresh` → `TP.TdRefreshBoth`, added `TdWorldHeader`/`TdPawnHeader`/
  `TdSubtitle`/`Tip.TP.TdEffective`.
- **Docs** ([tips.md](tips.md) "Slowing, freezing, or speeding up game time", `a4554f1` + `dec4303`) —
  the recipe now covers the two rows, the simultaneous CE records and the 1 ÷ World compensation.
- **Tests** — dual-lane command independence, `PawnEffectiveRateText` = world × pawn and its
  reactivity, per-lane presets, the two-arg CE batch rows, renamed-option round-trip. Compiled bindings
  (`x:DataType=vm:TeleportViewModel`) validated every new binding at build time.

**Known rough edges (unchanged by these commits):** the preset buttons still clamp to `[0, 3]` for
**both** lanes (`Math.Clamp(v, 0.0, 3.0)`) — harmless today since the largest preset is 2×, but a
future pawn preset above 3× would be silently clamped. **Not yet verified in-game:** only the *pawn*
lever has ever been live-exercised (The Adventures of Elliot, UE 4.27, build 2151); the world lever and
the bullet-time combination are unit-tested only.

-----

## 2026-07-14 — Invoke: by-value `fstruct` struct params in the CE Lua helper (dev commit `f66e602`; helper v1.3; reworked from PR #433)

**Committed to `dev` (managed build clean + `InvokeScriptTests` 108/108 green; no C++/pipe change).** Closes a real gap
surfaced by external contributor PR #433 (Rixef): a UFunction taking a **by-value `StructProperty` input param** maps to
the helper token `fstruct` (`BakedScriptGenerator.MapInputType`), but `ue5_invoke_helper.lua`'s `writeBakedParams` had no
`fstruct` arm — the token fell through to the `else` and raised *"Unknown param type 'fstruct'"*. Under the `pcall` in
`invokeUFunction` that surfaced as a graceful invoke failure (`showMessage`), so any function taking an undecomposable
struct param could not be fired from a generated baked script.

- **Helper — recursive `writeParams` + `fstruct` arm** ([ue5_invoke_helper.lua](../scripts/ue5_invoke_helper.lua)): split
  the param loop into `writeParams(base, regionSize, params)`; `writeBakedParams` zero-fills the whole buffer once (still
  via `writeByte`, a valid CE Lua single-byte write) then delegates. The `fstruct` arm resolves struct size (explicit
  `p.size` wins → next-member-offset diff → rest-of-region), zeroes the region in one `writeBytes`, and recurses only when
  `value` is a member table (nested fields, offsets relative to the struct base). Guards a hand-edited row missing
  `offset=`, clamps negative sizes, keeps the bilingual comments + the "supported types" error list. Version 1.2 → 1.3.
- **Generator emits `size=`** ([BakedScriptGenerator.cs](../ui/UE5DumpUI/Services/BakedScriptGenerator.cs)): `fstruct` rows
  now carry `size=<bytes>` so the helper zeroes exactly the struct region instead of the fragile next-offset heuristic
  (the param list is declaration-ordered, not sorted by offset). Scalar rows stay minimal (no `size=`).
- **Reworked, not merged:** PR #433 also retyped the zero-fill `writeByte` → `writeBytes({0})` — reverted; `writeByte` is
  valid CE Lua (used across `ue5_freeze_helper.lua` + the other script generators). Landed as a cleaned commit crediting
  the author (`Co-authored-by: Rixef`); the PR is **closed as superseded**, not merged (merging would re-introduce the
  churn + comment/error-list regressions and conflict with `f66e602`).
- **Tests** ([InvokeScriptTests.cs](../ui/UE5DumpUI.Tests/InvokeScriptTests.cs)): the embedded-helper test now asserts the
  `fstruct` arm at v1.3; a new generator test asserts a `StructProperty` input row emits `type='fstruct', size=N`.

## 2026-07-14 — Solide: force-and-hold a discovered field + player stealth-meter zero (build 2168; MERGED main PR #437)

**SHIPPED (full DLL + UI build + 841 native + 2525 C# tests green).** The honest, low-risk subset of the
"enemies can't detect you" evaluation ([project-enemy-undetectable-eval] memory; the locked Non-Goal in
[godmode-spec.md](godmode-spec.md) §3.2/§5.4 — *no universal detection bool; surface per-game via Property
Search*). Two user-facing deliverables over one new DLL module.

- **New DLL module `Solide`** ([Solide.cpp](../dll/src/Solide.cpp)/[.h](../dll/src/Solide.h)) — the
  multi-instance sibling of Hemmung: holds a list of *force-jobs*, each forcing one reflected field to a
  value across **all live instances** of a class via a write-on-drift re-assert worker (`SOLIDE_REASSERT_MS=300`,
  cap `SOLIDE_MAX_INSTANCES=256`, CDO-skipped, re-resolved every tick — no cached pointers). Three kinds:
  **bool** (reuses the public `Solitar::SetActorBool`; new read-only `Solitar::GetActorBool` companion captures
  the restore base), **object-null** (strong `ObjectProperty` only — weak/soft/lazy refused, since 8 zero
  bytes into a weak ptr = valid `GObjects[0]`, the eval's flagged trap), **numeric** (Float/Double/Int/Int64/Byte,
  Hemmung's LWC-width-aware read/write). `RemoveForce`/`ClearAll` best-effort restore the captured base.
  Plus `FindStealthMeter` — resolve the local pawn + owned components (`Aura::GetRelatedObjects`), keyword-score
  numeric fields via the pure header-inline `MatchStealthField` (unit-tested), return ranked candidates.
- **Pipe (Solide, pipe-only MVP)** — 5 commands (`Renge.h` + `Fern.cpp`): `force_field {class_name, field_name,
  kind, value|on}` → `{held, resolved, code}`; `reset_field`; `reset_all_fields`; `get_forced_fields`;
  `find_stealth_meter`. Fern calls `Solide::` directly (no Frieren export); `Solide::StopWorker()` registered
  in `UE5_Shutdown`.
- **UI — Property Search one-click Force** ([PropertySearchViewModel.cs](../ui/UE5DumpUI/ViewModels/PropertySearchViewModel.cs)
  + [PropertySearchPanel.axaml](../ui/UE5DumpUI/Views/PropertySearchPanel.axaml)): a row context submenu
  (Force ON / OFF / → null / value…) forces the field across all live instances of the row's class and holds it,
  with a bottom **"Forced fields (N held)"** honesty strip (per-hold remove + clear-all). Row gates
  `CanForceBool`/`CanForceNull`/`CanForceNumeric` by the reflected type; numeric prompts via the Freeze value
  dialog, object-null via a `ConfirmDialog`. Gated behind the shared `IExperimentalGate`.
- **UI — Teleport "Stealth Meter" card** ([TeleportViewModel.cs](../ui/UE5DumpUI/ViewModels/TeleportViewModel.cs)):
  Detect (auto-find, verifiable readout) → Hold @0 → Reset, tri-state badge. Experimental-gated. Falls back to
  a Property-Search recipe when the auto-find misses.
- **Honest limits (surfaced, not hidden):** `held=0` badge = the class/field matched nothing (no-op signal);
  by-class holds affect *every* instance of that class (N-held shows it); there is no universal detection bool,
  so this only works where the game exposes a reflected stealth/flag/target field. Single-player only.
- **VERIFIED in-game (Elliot / UE Shipping):** `force_field PointLightComponent::InverseExposureBlend → 0`
  resolved 83 instances, worker started (300 ms), re-resolved the pool each tick, `reset_field` stopped it
  clean — no errors, correct lifecycle (DLL `walk` log).
- **Follow-up (UI-only): Teleport quick-jump nav** ([TeleportPanel.axaml.cs](../ui/UE5DumpUI/Views/TeleportPanel.axaml.cs)) —
  right-click anywhere in the Teleport tab → a menu of the currently-visible cards; clicking one scrolls it to
  the top (via `ScrollViewer.Offset`, since `ContentRoot` swallows `RequestBringIntoView`). Built dynamically
  from the visible `Border` cards (label = each card's SemiBold header), so hidden cards are auto-excluded;
  the whole menu is **experimental-gated** (the card list would otherwise leak experimental card names).

## 2026-07-13 — Auto Snapshot (periodic capture) + free-disk-space guard (build 2166; UI-only, no DLL/pipe change)

**SHIPPED (UI-only; full UI build + 2509 C# tests + C++ self-tests green).** Two additions to the
experimental Snapshot tab — an unattended periodic-capture loop and a hard disk-full safety net. No
DLL, pipe-protocol, or thread-priority change.

- **Free-disk-space guard** ([SnapshotDiskGuard.cs](../ui/UE5DumpUI/Services/SnapshotDiskGuard.cs)):
  before ANY capture (manual + auto), the drive holding the snapshot DB must have at least
  `min(percent% of drive, GB)` free, else the write is refused. Defaults **10% / 50 GB** (whichever is
  smaller — 1 TB → 50 GB, 200 GB → 20 GB). A **0** on either term disables that term (percent 0 → GB
  floor alone), both 0 → guard off. Enforced pre-capture (primary) and mid-capture (reuses the
  max-dataset-cap graceful-stop path — KEEPS the partial, since deleting on a near-full disk is unsafe).
  New `IPlatformService.GetFree/GetTotalDiskSpaceBytes` (default sentinels: free `long.MaxValue`, total
  0 → never blocks on a measurement it can't take) via `System.IO.DriveInfo` in `WindowsPlatformService`.
- **Auto Snapshot loop** ([SnapshotViewModel.cs](../ui/UE5DumpUI/ViewModels/SnapshotViewModel.cs) +
  [AutoSnapshotPlanner.cs](../ui/UE5DumpUI/Services/AutoSnapshotPlanner.cs)): a **manual Start/Stop toggle,
  session-only** (never auto-starts on connect / auto-resumes on launch; only the settings persist).
  Gap after each capture = `max(interval − captureDuration, 60 s)` — one formula that auto-extends the
  effective interval past a long capture AND guarantees the ≥60 s idle breather. **The idle gap is the
  only game-impact lever — no thread-priority drop** (that Phase-0 experiment was reverted for starving
  scans ~20× when the game is busy; see multipipe-eval §8.2). Retention: **KeepRecent N** (roll forever,
  new `ISnapshotStore.EnforceCountAsync` count-FIFO) or **FixedCount N** (stop after N). **Auto-adjust
  quota** (default off): off → bound by quota, stop if it can only hold one snapshot; on → bump the
  quota up a preset (…→5 GB→Unlimited) to fit. Cancel during auto stops the whole loop; capture survives
  tab-switch, stopped on window close.
- The capture engine was extracted into `CaptureCoreAsync(bool isAuto, CancellationToken)` returning a
  `CaptureOutcome`, shared by the manual button and the loop; the loop links each capture's CTS to the
  auto token so Cancel/Stop abort it. Intricate pacing/retention/quota-grow rules live in the pure
  `AutoSnapshotPlanner` (unit-tested without timers). New settings persist via `SnapshotUiOptions`.
- Tests: `SnapshotDiskGuardTests`, `AutoSnapshotPlannerTests`, `SnapshotStoreTests.EnforceCount_*`, and
  VM guard-block/allow tests (configurable `DiskStubPlatformService`).

## 2026-07-13 — Timer control Phase E: Linie cadence flag — periodic timer-callback discovery (build 2156; dev, DLL needs re-inject)

**SHIPPED (DLL + UI; full build + 2479 C# tests + C++ self-tests green).** The behavioural complement to the
static `Timing` discovery (memory `project-timer-feature-eval`, layer E): make the Live Funcs profiler flag
*which UFunction* fires at a regular **timer cadence** — the callback that actually drives a cooldown / respawn
/ damage-over-tick, which name-scoring can't find.

- **DLL ([Linie](../dll/src/Linie.cpp)):** `Stat` gains a Welford running mean/variance of the wall-clock
  **inter-arrival gaps** between a function's fires; `RecordCall(ufunc, nowMs)` is fed the timestamp
  [Stark.cpp:143](../dll/src/Stark.cpp) *already* stamps for the responsiveness check — **zero extra clock
  reads on the PE hot path**. `Snapshot` emits `meanPeriodMs` + `cv` (stddev/mean = regularity) + `gapSamples`;
  `pe_profile_get` ships them per row.
- **UI:** `PeProfileEntry` gains `MeanPeriodMs`/`Cv`/`GapSamples` + an `IsPeriodic` classifier (≥3 gaps, CV≤0.25,
  period outside the per-frame Tick band ~5-40 ms and within a plausible ~50 ms-30 s gameplay-timer window) →
  a **"Timer"** Kind badge + a **Period** column; a **"Periodic only"** filter in `LiveFuncsViewModel`. The
  workflow is the *inverse* of the shipped action-diff: record while **IDLE** ~15-20 s, then Periodic-only
  leaves the steady callbacks.
- **Honest limit (stated in the tooltip):** native C++/lambda timers (`SetTimer(this, &UClass::Method)`) call
  the delegate directly and **bypass ProcessEvent** — only UFUNCTION-bound (BP timer event /
  `SetTimerByFunctionName`) timers are visible. +12 tests (classification + VM filter). A cadence diagnostic
  (build 2158) appends the periodic candidates to `pe_profile_get`'s log line so an idle-window recording is
  verifiable from the log, not just the UI. **LIVE-VERIFIED on The Adventures of Elliot (UE4.27):** an
  idle-window recording flagged **3 periodic funcs out of ~90** (the rest per-frame Tick, correctly excluded)
  — `BP_SupportFairy_C::TryAttackEnable` + its `ExecuteUbergraph` at the same **~325 ms** cadence (cv 0.02) =
  a real ~3 Hz BP timer, plus `EQC_PlayerContext_C::ProvideSingleActor ~108 ms` (cv 0.08); stable across two
  windows (~10 s → 8-27 gaps, ~20 s → 27-83 gaps). No false positives, no tuning needed. *Extends
  Linie/LivePEProfiler (build 2109); completes the timer eval's layer E.*

-----

## 2026-07-13 — Time/Timer control L1: Hemmung module + Timing discovery category (build 2148; dev, DLL needs re-inject)

**SHIPPED (DLL + UI; builds + all 2453 C# tests + C++ self-tests green).** First slice of the layered
Time/Timer feature (eval: memory `project-timer-feature-eval`, todo.md "Time / Timer control"). Two halves:

- **Timing discovery category (UI, no DLL/pipe change).** New `PropertyCategory.Timing` in
  [PropertyScoringTable.cs](../ui/UE5DumpUI/Services/PropertyScoringTable.cs) — always-on `TimingKeywords`
  (Cooldown/CoolDown/Countdown/Delay/Interval/Timer/Elapsed/Remaining/LifeSpan/Lifetime/Recharge/TickRate/
  TimeDilation/Time…), a `TimeStructTypes` set (Timespan/DateTime/QualifiedFrameTime/FrameTime/Timecode)
  mirroring the GAS-struct branch (default-to-Timing when the name misses a keyword), timer terms added to
  `SeedQueries[]` (so round-1 `search_properties` actually fetches them), and a teal chip in the Interesting
  Properties tab. The Timing check is appended **last** so an ambiguous name that also hits an earlier bucket
  keeps it (locked `BuffDuration → Combat` test still passes). [ClassLocationScorer.cs](../ui/UE5DumpUI/Services/ClassLocationScorer.cs)
  gained GameplayEffect/GameplayAbility/WorldSettings +2. Function side: [KeywordScoringTable.cs](../ui/UE5DumpUI/Services/KeywordScoringTable.cs)
  `UtilityKeywords` widened with Cooldown/Dilation/Delay/Interval/Elapsed/Recharge (catches cooldown/dilation
  getters that scored 0 before). One sentinel test renamed (`ComponentTickInterval` → `MeshSectionIndex`,
  now that `Interval` is a keyword) + new Timing/time-struct/class-rule tests.
- **Hemmung module (DLL) — global slow-mo / freeze-time / speed-up.** New Frieren module
  [Hemmung.cpp/.h](../dll/src/Hemmung.cpp) (ヘムング "inhibition"): the **absolute-value sibling of Laufen** —
  holds reflected dilation floats at a pinned value via a write-on-drift re-assert worker (same s_mutex/
  s_workerMutex discipline, LWC-width-aware read/write, owner-change re-capture). Two levers: `DIL_GLOBAL`
  (`AWorldSettings::TimeDilation`, whole-world; resolved via GWorld→PersistentLevel→WorldSettings reflected
  chain + `Aura::FindInstancesByClass("WorldSettings")` fallback) and `DIL_PAWN` (local pawn
  `AActor::CustomTimeDilation`, reusing Laufen/Solitar's pawn chain). Exports `UE5_SetTimeDilation(target,
  value)` / `UE5_ResetTimeDilation(target)`; **Mimic `CMD_TIME=15`** mailbox (`TimeOp` SET/RESET) for CE
  Lua/.CT; pipe `set_time_dilation` / `reset_time_dilation` / `get_time_state` (each surfaces
  owner_addr/field_offset for the Locate-in-GWorld handoff). `Hemmung::StopWorker()` joined on shutdown;
  roster flipped 🟢. Value clamped DLL-side to [0.0, 100.0]. Direct write bypasses SetGlobalTimeDilation's
  clamp; a paused world can't be stepped and an active Sequencer track flickers ≤1 tick (documented).

**Part C UI Time card — SHIPPED (build 2149).** A **"Time Dilation" card** in the Teleport panel
([TeleportPanel.axaml](../ui/UE5DumpUI/Views/TeleportPanel.axaml)) beside Move Speed/Gravity: a "Player only"
toggle (whole-world `TimeDilation` vs per-pawn `CustomTimeDilation`), a linear 0–3× slider + % readout,
preset buttons (Freeze / ¼× / ½× / 1× / 2×), Apply/Reset/↻, and an ON/OFF/Unavailable badge + live "Current:
X× (held; natural Y×)" readout. Wired via new `IDumpService`/`DumpService`
`GetTimeStateAsync`/`SetTimeDilationAsync`/`ResetTimeDilationAsync` (models `TimeDilationKnob`/
`TimeDilationSetResult`/`TimeState`) and `TeleportViewModel` commands; en.axaml strings; 6 new VM tests
(2459 C# green). **CE Lua/.CT generation — SHIPPED (build 2150).**
[TimeDilationScriptGenerator.cs](../ui/UE5DumpUI/Services/TimeDilationScriptGenerator.cs) mirrors
`MovementScriptGenerator`: stateful `[ENABLE]`/`[DISABLE]` records poking the `CMD_TIME=15` mailbox — op `SET`
(baked value) on tick, op `RESET` on untick (instanceAddr=op, ufuncAddr=target 0 global/1 pawn, paramsData
double value); `CeMailboxLayout.CmdTime`+`TimeOpSet/Reset` added; `CeLuaHygiene`-compliant (DEBUG-gated `dbg`,
errors surface, auto-close on clean success). Wired into the Teleport panel's "Add to CE" (AOBMaker push, 2
records) + "Save .CT" batch (`BuildBatchRows` → World + Player rows); 6 new generator tests (2465 C# green).
**Persistence — SHIPPED (build 2151).** Two layers: (1) **live read-back** — `TeleportViewModel.SetConnected`
now reflects whatever dilation the DLL is already holding on connect (and on target-switch), syncing the
slider to the engaged value — the same "state lives in the DLL, survives a UI reconnect while the game lives"
model as teleport markers (`RefreshHeldTimeStateAsync`); disconnect resets the badge. (2) **disk preference** —
`TeleportUiOptions.TimeDilation`/`TimeTargetIsPawn` (in the global `ui-options.json`, mapped in
`MainWindowViewModel` Apply/Capture) pre-fill the card's last-used value+target across UI restarts (NOT
auto-applied; the live read-back wins when a dilation is held). +2 VM tests + options round-trip/defaults
coverage (2467 C# green). **L1 COMPLETE + LIVE-VERIFIED on The Adventures of Elliot (UE4.27, build 2151).**
Log (`Elliot-Win64-Shipping`): `set_time_dilation target=pawn value=0.5` → `Time: target 1
('CustomTimeDilation') base=1.0000 -> hold 0.5000 (rc=0)` → re-assert worker started; held at 0.5/1.0/2.0/1.4688×
and reset cleanly (`Time: reset target 1 (any active left=0)`, worker stopped); `get_time_state` polled on
connect (the read-back). `rc=0` throughout — the per-pawn `CustomTimeDilation` lever resolved + held. (Global
`WorldSettings::TimeDilation` lever wired + unit-covered, not exercised in this session's log.) **REMAINING
(deferred by design):** a dedicated opt-in function-side `FunctionCategory.Timing` bucket; L2 (GAS cooldowns) /
L3 (live FTimerManager). **NEEDS in-game verify** (Hemmung has no
unit test — reflection needs a live game): attach, drag the slider / hit ½×, Apply → world runs at half speed
and holds against slow-mo; Reset restores.

-----

## 2026-07-11 — LKG Phase 2: DLL-attributed confirmed-working proxy (build 2142; dev, needs re-inject)

**SHIPPED (DLL + UI; re-inject required).** Upgrades the Proxy Deploy suggestion from "you deployed /
injected this before" to **"a proxy actually LOADED this game and it stayed running"** — the strongest
known-good signal.

- **DLL self-reports the load path** ([Fern.cpp](../dll/src/Fern.cpp) init response): `load_mode` =
  `proxy:version.dll` | `proxy:dinput8.dll` | `proxy:dxgi.dll` | `injected` | `loaded:<name>` | `unknown`,
  computed from **`GetModuleFileNameW(g_hDllModule)`** — THIS module's own file name. This is the ONLY
  correct proxy attribution: with two proxies deployed, `Heiter.cpp`'s mutex makes the loser a passive
  forwarder that returns before init and never reports, so the served `load_mode` is always the WINNER;
  module-list enumeration (the naive approach) mis-attributes because all proxies share the PE ProductName.
- **UI stability gate** ([MainWindowViewModel](../ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs)
  `ScheduleProxyConfirmation`): on connect, if `EngineState.LoadMode` is a proxy, wait a 20 s dwell and —
  **only if still connected** — record it via `RecordConfirmedProxy`. The dwell guards a proxy that loads +
  connects then crashes the game seconds into play (connect alone isn't proof the game keeps running).
- **Key = game .exe name** (`EngineState.ModuleName`), NOT peHash — survives reinstall/patch; unified with
  Phase 1's injection/enrichment exe-name resolution. Stored in `ProxyDeployUiOptions.ConfirmedProxyByExe`.
- **Suggestion priority**: confirmed-working ("dxgi.dll · confirmed working") > deployed ("· last used") >
  injection ("injection · no proxy deployed") > version default. Progressive: deploy shows "last used" →
  launch + survive the dwell → upgrades to "confirmed working".
- Race-safe: enrichment reads snapshots; all map mutations marshal to the UI thread. Backward-compatible
  (older DLLs emit no `load_mode` → gate is inert). 2436 tests pass; DLL + 3 proxies + trimmed UI green.

-----

## 2026-07-11 — Proxy Deploy per-game suggestion (LKG Phase 1): import-table + remembered pick + injection known-good (builds 2134-2140; dev)

**SHIPPED (UI-only, no DLL/pipe change).** First slice of the "Last-Known-Good proxy" idea: the Proxy
Deploy grid gains a per-game **Suggested proxy** column so the user doesn't have to re-guess which of
version/dinput8/dxgi to deploy after an uninstall→reinstall wipes the folder. Advisory only — it never
changes the global proxy radio and never auto-deploys.

**Why this shape (evaluation first).** A multi-agent evaluation of the full peHash-keyed "record a
confirmed proxy load" design surfaced three verified blockers: (1) the confirmed-success moment (pipe
connect, `ApplyEngineState`) has the game peHash but **no proxy-type** — attribution lives only in the
process-module list, and with two proxies deployed the mutex passive-forwarder (`Heiter.cpp` +
identical `version.rc` `ProductName`/`OriginalFilename`) makes module-enumeration record the *wrong*
proxy; (2) **peHash = TimeDateStamp+SizeOfImage changes on every game patch**, so a reinstall (which
pulls the latest build) orphans the entry — the wrong key for a "survives reinstall" feature; (3)
recording success at connect can log a load-then-crash proxy as "good". Phase 1 therefore avoids the
DLL change and history entirely.

**What Phase 1 does instead:**
- **[ProxyImportAnalyzer](../ui/UE5DumpUI/Services/ProxyImportAnalyzer.cs)** — pure, offline PE
  import-table parser (DOS→NT→section→RVA→file-offset; standard + delay-load dirs). Reports which of
  {version,dinput8,dxgi}.dll the .exe imports. **Honesty guard:** import parsing reports *viability*
  (which proxies can even load), NOT the recommendation — `version.dll` loads *dynamically* so its
  absence from the static import table means nothing, and we deliberately never auto-escalate to dxgi
  (that pattern matches nearly every D3D game and would push dxgi into the Octopath-秒退 trap).
- **Suggestion** = the proxy the user **last deployed for that game** (remembered by folder name, so it
  survives reinstall/patch — the honest mini-LKG) ?? **injection known-good** ?? `version` (safe default).
  Import viability is folded into the column text as advisory context ("version · default · alt: dxgi").
- **Injection as a first-class known-good** (build 2140): injection is a UI-initiated action, so — unlike a
  plain-Connect proxy load — it is reliably knowable in Phase 1. `RememberInjection` records the game .exe
  name whenever the user successfully injects (fresh inject, or an already-loaded process the module list
  attributes to injection). A game that was injected but never had a proxy deployed shows **"injection ·
  no proxy deployed"** — its known-good load method (and often the ONLY option for EA/launcher games that
  strip the exe's DLL search dir). Keyed by .exe name (inject flow) vs proxy pick by folder name (deploy
  flow); both resolve against the same `DetectedGame`, so no key unification needed. Persisted in
  `ProxyDeployUiOptions.InjectedGameExes`.
- **Opt-in** `LkgSuggestEnabled` (default ON) in `ProxyDeployUiOptions`; remembered picks persist in
  `LastManualProxyByGame` (game-name → ProxyType) in `ui-options.json` (UI-owned, never the DLL-shared
  peHash json). Enrichment runs once per scan after `RefreshDeployStatusAsync`; the column is a plain
  `DataGridTextColumn` (Binding==SortMemberPath, the only compiled-binding-sortable pattern in this grid).

**Not done (Phase 2, deferred):** the real DLL-attributed success-history LKG — needs the DLL to
self-report `GetModuleFileNameW(g_hDllModule)` (the winning proxy's own filename) at init, a stable
install-identity key (appid/name, not peHash), and a stability gate before recording. Known Phase 1
caveat: import-table can't predict EA-launcher games that strip the exe's DLL search dir (no proxy
loads at all). 15 new tests (synthetic PE incl. non-identity RVA mapping + real-PE smoke + injection
known-good + service glue); full suite 2434 pass / 0 fail; trimmed self-contained UI publish green.

-----

## 2026-07-11 — Live Funcs profiler refinements: baseline diff, hide-widgets, hide-events, call-order (builds 2110-2130; dev)

Iterative in-game hardening of the Live PE profiler ([Linie](../dll/src/Linie.cpp)) driven by a
DQ7R (UE4.27 Shipping) open-shop hunt. Each round cut noise so the action's true entry point surfaces
— all **general / game-agnostic** (no per-game heuristics):

- **Baseline diff** (client-side): Set Baseline on an idle recording, then the action recording shows a
  Δ column + ranks NEW / increased rows first (keyed by `ClassName::FuncName`, stable across GC).
  "New/changed only" hides unchanged Tick noise.
- **Hide UI widgets**: `pe_profile_get` walks each function's owning-class super-chain
  (`Aura::ClassDerivesFromAny{UserWidget,Widget}`) → `is_widget`. Opening a shop CREATES its widget, so
  all the widget's own methods fire at once and flood the diff — hiding them leaves the persistent opener.
- **Hide events/delegates + Type badge**: `pe_profile_get` returns `function_flags`; the UI tags each row
  Event / Deleg / Call / native (`FUNC_Event`/`FUNC_Delegate`/`FUNC_BlueprintCallable`) and can hide the
  On*/callback reactions, leaving imperative callables.
- **Call order (first-fired)**: Linie now records each function's first-fire position in the call stream
  (`first_seq`); the UI adds an "Order" column + "Earliest first" sort. An action's entry point runs
  BEFORE the reactions it triggers, so this is the causal, name-independent signal that floats the true
  opener to the top of the NEW set.

**Finding of record:** on DQ7R the shop opens via a **native C++ call** the ProcessEvent hook can't see —
only its BP event callbacks (On*) are visible. That's the fundamental limit of PE-based discovery, not a
tool gap; the profiler correctly surfaces it (hide-events empties the callable list). For most UE games
(reflected/Blueprint gameplay) the profiler + these filters find the action's function directly. Tests
across the four rounds: +25 (`LiveFuncsViewModelTests`). Suite 2419 pass / 0 fail; all 3 proxies + AOT clean.

-----

## 2026-07-11 — Live ProcessEvent Call Profiler (Linie) — behaviour-based UFunction discovery (build 2109; dev)

**SHIPPED (DLL + UI). New "Live Funcs" tab.** The root-cause answer to "which function does this game
call to open the shop / dash?" — the thing name heuristics (Interesting Functions) fundamentally can't
find. Workflow: **Start → ALT-TAB to the game → perform ONE action (open shop, dash, attack) → Stop →
see which UFunctions fired, ranked by call count.** The action-specific function is near the top with a
low count (a handful of calls); per-frame Tick/Update noise has huge counts.

**New Frieren module `Linie`** (莉涅 — "reads opponent mana"; roster use = *Analysis / profiling*).
`Stark`'s ProcessEvent hook keeps the single hook and calls into `Linie` with one inlined branch:
- **Hot path stays free when off.** `Linie::IsRecording()` is one relaxed `atomic<bool>` load + a
  predicted-not-taken branch ([Stark.cpp:143](../dll/src/Stark.cpp)); the mutex + `unordered_map<UFunction*,
  count>` are touched ONLY inside a Start/Stop window. Mirrors the existing `s_queueDepth` "skip work
  unless armed" gate. Multi-threaded-PE safe (map guarded by a dedicated mutex, never Stark's queue mutex).
- **Hook-install prerequisite.** The PE hook installs lazily on the first invoke; `pe_profile_start`
  calls the new `UE5_EnsureGameThreadHook()` (reuses the audit-#3 `call_once`) so recording works without
  first issuing an invoke. `hook_active:false` ⇒ vtable detection failed → counts stay 0 (UI warns).
- **Query-time resolution.** Raw `UFunction*` are stored during recording; `pe_profile_get` snapshots →
  sorts by count desc → caps → resolves only the capped set via the new `Ubel::ResolveFunctionInfo`
  (factored out of `WalkFunctions`' version-aware flags/params probe), with a `"Function"` meta-class
  guard so a pointer whose slot was recycled by a GC/level-load is dropped, not deref'd. Cooperative
  `Tot::Requested()` abort. `Linie::Reset()` on client disconnect.

3 pipe commands (`pe_profile_start/stop/get`, [Renge.h](../dll/src/Renge.h)). UI: `LiveFuncsViewModel` +
`LiveFuncsPanel` (Start/Stop/Refresh/Clear + ranked DataGrid + space=AND keyword filter with LRU memory
per the CLAUDE.md rule + "Live"/"Name" row handoffs to Live Walker/clipboard). New "Live Funcs" tab
inserted after Dump Explorer (MainTabIndex shifted); leaving the tab auto-stops any live recording.

**Tests:** `LiveFuncsViewModelTests` (14 — Start hook/no-hook, Stop fetch+rank+empty, filter space=AND,
Clear, handoffs, auto-stop-on-leave, model). Full suite 2402 pass / 0 fail; all 3 proxy DLLs + AOT
publish clean. **DLL-side PE counting needs in-game verification** (Fern/DLL has no unit tests) — the
shop/dash acceptance test on a live UE title.

-----

## 2026-07-11 — Interesting Functions: opt-in "Gameplay Actions" keyword pack (build 2103; dev)

**SHIPPED (UI-only, opt-in default OFF).** The Interesting Functions scorer was tuned for cheat-value
targets (Stats/Inventory nouns + Movement/Combat cheat verbs). Character-control + interaction + shop verbs
the user actually wants to *call* — `Dash`/`Dodge`/`Roll`/`Slide`/`Interact`/`Use`/`Open`/`Buy`/`Sell`/
`Shop`/`Vendor`/`Merchant`/`Trade`/`Purchase` — were **absent from every keyword bucket**, so a plain
`OpenShop()` / `Dash()` / `Interact()` scored 0 and stayed below the threshold-5 cutoff (invisible unless
"Show All").

Added a new opt-in `FunctionCategory.GameplayAction` keyword pack (weight 5, same as Stats/Movement):

- **`KeywordScoringTable.Score(entry, includeGameplayActions = false)`** — new default-`false` param. When
  off, scoring is **byte-identical** to before (regression-guarded by a test), so the pack is purely additive.
  Only **NEW** tokens live in the pack — verbs already in Movement (`Jump/Move/Walk/Sprint`) or Combat
  (`Attack/Fire`) are deliberately NOT duplicated (the final score sums every bucket; a dup would double-count).
- **Whole-token match** (`KeywordTokenizer`, per the CLAUDE.md rule) → single tokens: `OpenShop` →
  `["open","shop"]` hits both `Open` and `Shop` (score 10). Noisier common words (`Use`/`Open`/`Store`) are
  included anyway — opt-in trades precision for recall.
- **Tie priority lowest**: a verb that also hits a built-in bucket keeps that label (e.g. `SellItem`: Sell↔Item
  tie → stays Inventory) but still gains the extra points so it surfaces.
- **UI**: a "Gameplay Actions" checkbox next to "Show All" (green `#5FBF7F`). Toggling **re-scores the loaded
  set in place** (`RescoreAsync` on a worker thread — no pipe re-fetch, class-noise histogram untouched) via
  the shared `ScoreEntries` helper. New `GameplayAction` chip in the category dropdown + green "Gameplay" label.
- **"BP/Exec only" filter** (`CallableOnly`, default off, blue `#7FB6E8`) — a pure *display* filter (in
  `ApplyFilter`, no re-score) that keeps only rows flagged `BlueprintCallable` or `Exec`. Gameplay/control/shop
  entry points are almost always one of these two (both survive cooking), so it hides native getter/setter/
  plumbing noise. Pairs with Gameplay Actions + Show All to browse callable action functions.
  `ScoredFunctionRow` forwards `IsBlueprintCallable`/`IsExec`. Added to `ClearFilters` (it's a view filter);
  `GameplayActions` deliberately is NOT (it's a scoring mode with a re-score cost).

Recipe added to [tips.md](tips.md) ("Finding character-control / shop functions"). **No DLL/pipe change** — all
client-side C# (the DLL already returns raw `function_flags`; scoring has always been UI-side).

**Tests:** `KeywordScoringTableTests` (+6: off-path zero-contribution, default==off regression, on-path
categorisation incl. the Sell↔Item tie, bare-OpenShop clears threshold, Jump no-double-count, DisplayName/Color
for the new enum) + `InterestingFunctionsViewModelTests` (+4: pack toggle re-scores & surfaces OpenShop, defaults
off, BP/Exec-only hides native rows / keeps Exec rows, ClearFilters resets it). Full suite 2388 pass / 0 fail;
AOT publish clean.

-----

## 2026-07-11 — Object Tree per-instance drill-downs: Open in Live Walker / Show Related / Locate in GWorld+GameEngine (build 2098; dev)

**SHIPPED (UI-only). Phase 3 (final) of "global instance explorer".** A global instance keyword search is only useful
if you can act on a specific hit. The Object Tree row context menu previously offered only class-oriented actions (Copy
Type/Name/Address, Find Instances by Type / Type+Name) — nothing to drill THIS exact object. Added four per-instance
handoffs mirroring the InstanceFinder row actions:

- **Open in Live Walker** (`NavigateToLiveWalker` → `LiveWalker.NavigateToAddressCommand`) — walk this object's live fields.
- **Show Related Objects** (`NavigateToRelatedObjects` → `RelatedObjects.LoadForAddressAsync`) — its class/outer/
  Controller↔Pawn/components/ASC/AttributeSet graph.
- **Locate in GWorld** / **Locate in GameEngine** (`LocateInGWorld`/`LocateInGameEngine` →
  `LiveWalker.LocateInGWorldAsync`/`LocateInGameEngineAsync`, `stopAtParent: false` so it lands ON the picked object) —
  shortest pointer chain from the world / engine root. Meaningful for instances; a class row is usually not reachable
  (Live Walker reports that) — kept ungated for simplicity since the "Instances only" toggle already narrows to instances.

Wired in `MainWindowViewModel` with the exact try/catch + tab-switch shape as the InstanceFinder handoffs (reusing the
shared `OpenRelatedAsync`). Each command no-ops on a null node / empty address so a bad row never navigates to a dead
target. UI-only — no DLL/pipe change; the handoffs re-resolve the live address downstream.

**Tests:** `ObjectTreeViewModelNavigationTests` (6 — each command raises its event with the row address; null-node and
empty-address no-op). C# suite 2369 green; UI AOT publish + compiled bindings pass. Completes the three-phase global
instance explorer (Phase 1 instances-only toggle, Phase 2 server-side Search, Phase 3 drill-downs).

-----

## 2026-07-11 — Object Tree top Search upgraded: server-side space=AND over name+class + instances_only + honest truncation (build 2096; dev)

**SHIPPED (DLL + pipe + UI). Phase 2 of "global instance explorer".** The Object Tree top Search box was a silent
trap: `search_objects` → `Aura::SearchByName` matched the **object name only** (single substring), early-exited at the
client's 2000 cap, and Fern reported `total = results.size()` — so the user got ≤2000 rows with **no signal that more
existed**. This makes the top Search a true global instance keyword search, consistent with the bottom filter.

**DLL.** `Aura::SearchByName(query, maxResults, instancesOnly)`:
- **space = AND over object name OR class name.** `query` is whitespace-tokenized (new header-inline
  `Aura::SplitLowerKeywords`); every term must hit the object name OR the class name (`Aura::MatchesAllKeywords`,
  term-level AND / field-level OR) — the server-side twin of the client `ObjectTreeFilter`. So "Pawn" now matches by
  class, and "BP_ Enemy" ANDs.
- **`instances_only` gate** via new header-inline `Aura::IsReflectionMetaClass` — mirrors the C#
  `ReflectionMetaClassifier` (full reflection/type layer, class family + Function/ScriptStruct/Enum/Package + UE4
  `…Property` suffix), so the top Search honours the "Instances only" toggle server-side.
- **Honest truncation:** `SearchResultSet::truncated` is set when the cap is hit (same test as `FindInstancesByClass`'s
  cheap path); Fern emits `data["truncated"]`.
- All three helpers are header-inline in `Aura.h` (like `IsEnginePackage`) and unit-tested in `dll_helpers_test`.

**UI.** `SearchObjectsAsync(query, limit, instancesOnly, ct)` + `ObjectListResult.Truncated`; the VM's `SearchAsync`
forwards `InstancesOnly` and the new `Constants.ObjectTreeSearchCap = 5000` (= `ObjectTreeMaxDisplay`, so every
returned row can display — replaces the old 2000 page size), and on a hit cap the status reads
"Found N+ results (capped — narrow the search, or Reload + filter for all)" instead of silently truncating. Tooltips
updated.

**Whole-pool honesty.** The top Search still caps (returning the entire 486K-object pool over the pipe is not viable),
but it is now **explicit, class-aware, space=AND, instances-only-aware, and reports truncation** — and the tooltip
points at the uncapped path (Reload loads the whole pool into `_allNodes`; the bottom filter scans it with no cap on
match-counting).

**Tests:** `dll_helpers_test` +32 (`Test_IsReflectionMetaClass` full-layer parity + `Test_KeywordMatch` tokenize/AND;
829 pass) + 3 VM tests (`SearchAsync_SendsInstancesOnlyAndSearchCap_ToServer`, truncated→"capped" status, plain-count
status). C# suite 2363 green; full DLL+UI build clean. **DLL scan itself not live-verified** (needs a UE process +
re-inject); header-logic + wiring covered by tests.

-----

## 2026-07-11 — Object Tree "Instances only" toggle: global live-instance keyword search by hiding the reflection layer (build 2092; dev)

**SHIPPED (UI-only, no DLL/pipe change).** Turns the existing Object Tree into a **global live-instance keyword
explorer** — the instance analog the user wanted to mirror the offline metadata Dump Explorer. A multi-agent
feasibility eval established the surprising finding that a whole-pool, class-agnostic, `space = AND` keyword browse
over live instances *already existed*: the Object Tree paginates the entire GObjects pool into `_allNodes` and its
bottom filter runs `ObjectTreeFilter.MatchesAllTerms` over the **whole cache**, not the page. The single genuinely
missing capability was **"show live instances only, not reflection metadata"** — a client-side predicate, not a new
subsystem. So Phase 1 is one toggle.

**What shipped.** New `Helpers/ReflectionMetaClassifier` (`IsReflectionMeta` / `IsLiveInstanceRow`) + an "Instances
only (hide reflection metadata)" checkbox on the Object Tree filter row (`InstancesOnly` observable → `ApplyFilter`).
When on, `ApplyFilter` drops rows whose `ClassName` is in the **full reflection/type layer** before the text/class
filter runs.

**The load-bearing correctness detail** (an adversarial review of the design caught this): the noise is NOT just
class-like metas. Excluding only `Class`/`BlueprintGeneratedClass`/… (what `DumpAllService.IsClassLikeMetaName` /
`Aura::IsClassLikeMeta` cover) would leave every `UFunction`, `UScriptStruct`, `UPackage` and `UEnum` in the result —
failing at the toggle's headline job. So the classifier excludes the whole set: class family + `Function`/
`DelegateFunction`/`SparseDelegateFunction` + `ScriptStruct`/`UserDefinedStruct` + `Enum`/`UserDefinedEnum` +
`Package`, plus a `EndsWith("Property")` suffix rule for UE4's `FooProperty` UObject descriptors (a priority target;
UE5 makes FProperty non-UObject so the suffix never fires there). CDOs/archetypes report their game class as
`ClassName`, so they survive by design; null/empty `ClassName` is treated as an instance (never silently hidden).

**Whole-pool guarantee (the explicit user concern).** The filter runs inside the `foreach (var node in _allNodes)`
loop — the `ObjectTreeMaxDisplay = 5000` cap limits only *displayed* rows, while `matchCount` counts every match. Test
`ObjectTreeViewModelFilterTests.InstancesOnly_FiltersWholePool_BeyondDisplayCap` loads 8000 nodes (2000 metas THEN
6000 instances) and asserts the reported match count is the full **6000** — it would fail if the scan were ever
limited to a page or the display cap, locking the guarantee in as a regression guard.

**Tests:** `ReflectionMetaClassifierTests` (36 cases — full type layer excluded, UE4 property family excluded via
suffix, instances kept, CDO/null contract) + `ObjectTreeViewModelFilterTests` (whole-pool span, meta-hiding,
`space = AND` composition with the text filter). Suite 2360 green. **Not live-verified in a game** (needs a UE
process); logic covered by tests + the Avalonia compiled-binding build. Phase 2 (server-side pool-load-free global
keyword command) and Phase 3 (per-hit drill to Live Walker / Locate) remain optional follow-ups.

-----

## 2026-07-10 — Keyword-search UX unification (space=AND + per-keyword memory) + `get_object_list` `full_path` restores GameOnly pre-walk skip + IsEnginePath format fix (build 2088; dev)

**SHIPPED.** Three changes; the last two are one feature + a serious pre-existing bug it exposed.

**(1) Every keyword/filter box now uses `space = AND` (learned from Object Tree).** The Object Tree text filter
already split on whitespace and required EVERY term to hit a field (`ObjectTreeFilter.SplitTerms` /
`MatchesAllTerms`). That helper grew a `params string?[]` multi-field overload (term-level AND, field-level OR,
null-safe), and **every** client-side keyword filter was migrated off single-substring `.Contains`/`IndexOf` onto it:
Console, Detect Stats, Interesting Properties, Interesting Functions, Game Class Filter, Class Struct, Class Pivot
(class-picker + results), Snapshot Diff (global + class/field/object), SPC result (global + class/field/object),
Live Walker (field-highlight search + Functions filter). So `add money` now matches a row containing both terms in
any order, in any field. Already-AND boxes (Instance Finder temp filter, Dump Explorer, Object Tree) were left as-is.
Server-side filters (Value Search, SPC query-time Class/Prop) can't AND client-side and were skipped. Every panel's
combined facets (Category / ShowAll / GameOnly / class-noise exclusion / numeric ranges / direction / threshold /
selection-detach / SequenceEqual no-op guard) were preserved; the Live Walker field search stays a **highlighter**
(sets `IsSearchMatch`, never removes rows); the Class Pivot picker stays a `Text`-only AutoCompleteBox (never
`SelectedItem`) so the historical `SelectedClass` oscillation can't return.

**(2) Every keyword box now REMEMBERS what you typed (learned from Live Walker).** New reusable
`Helpers/KeywordSearchMemory` packages the Live Walker pattern — an LRU `ObservableCollection<string>` of settled
keywords that yielded matches (700 ms debounce, `SearchKeywordHistory.Remember`, longest-valid-wins), bound to an
`AutoCompleteBox.ItemsSource`. 14 plain filter TextBoxes became AutoCompleteBoxes with per-box memory (`FilterHistory`
etc.). The Snapshot/SPC **class/field/object** pickers keep their existing DATA-derived distinct-value suggestions
(not regressed); only their free-text **global** box got typed-keyword memory. Value Search (server-side, async
filter) uses `KeywordSearchMemory.Commit()` from the reload-completion path instead of a timer probe, so the remember
never races the pipe round-trip and reads a stale count. Disposed in the three `IDisposable` VMs; self-limiting
elsewhere.

**(3) `get_object_list` emits `full_path` (gated) → GameOnly pre-walk skip restored — AND the pre-existing
IsEnginePath format bug fixed.** The DLL `get_object_list` (Fern.cpp) now emits `full_path` per object **only when the
request carries `include_path=true`** (kept off the hot Object Tree paginate — a path string per object is ~19 MB over
486K objects; not interned). `DumpService.GetObjectListAsync(..., bool includePath=false)` parses it into
`UObjectNode.FullPath`; `DumpAllService` sets the flag only for GameOnly and drops engine-package classes BEFORE the
`walk_class` round-trip (the post-walk skip stays as the authoritative backstop for the include_path-off / TOCTOU
case). **The bug an adversarial review then caught:** the C# `DumpAllService.IsEnginePath` matched `"/Script/Engine."`
(single slash, trailing dot) but `Ubel::GetFullName` actually emits `"//Script/Engine/Actor"` (DOUBLE leading slash,
`/` separators — dot is only used for sub-objects at depth > 2). So `IsEnginePath` **never matched a real wire path**,
making GameOnly a complete no-op — including build 2044's post-walk "fix", which corrected the skip LOCATION but not
the path FORMAT. The tests passed because they fed the `/Script/Engine.Actor` fiction the DLL never produces.
`IsEnginePath` was rewritten as a faithful port of the DLL's `Aura::IsEnginePackage` (collapse the leading-slash run,
require a `/`/`.`/end terminator, prefix list synced to the DLL's 37 packages), and the test fixtures switched to the
real `//Script/Engine/Actor` form so they now guard the production format. This is the CLAUDE.md debugging rule in
action: verify a fix against the actual data format, not an assumed one.

**Verification.** Clean DLL+UI+Tests build green; **2320 tests pass** (helper `params`-overload coverage + real-format
`IsEnginePath` theory + a new pre-walk-skip test that keys on `obj.FullPath`). The whole thing was built by one
inventory workflow (16 agents auditing each panel), one implementation workflow (13 agents, one per panel, editing
disjoint VM+AXAML), and one adversarial review workflow (3 lenses) that surfaced both fixed bugs. New files:
`Helpers/KeywordSearchMemory.cs`; `ObjectTreeFilter` gained the `params` overload.

-----

## 2026-07-10 — Dump Explorer tab (offline "Dump All" .jsonl browser) + Options depth/Deep editable while disconnected (build 2044; dev)

**SHIPPED (UI only; no DLL / pipe-protocol change).** Two changes.

**(1) Options “Locate in GWorld depth” + “Deep” now editable while disconnected.** Both controls shared one
`StackPanel IsEnabled="{Binding LiveWalker.IsGWorldAvailable}"` gate (MainWindow.axaml:222) that greyed them out
until a live GWorld resolved. They back **persisted preferences** (`GWorldLocateDepth`=7 / `GWorldLocateDeep` in
`LiveWalkerUiOptions`), so the gate is removed — the user can dial them in ahead of a locate, like every other
Options item. Locate-time read via `FindPathFromGWorldAsync` is unchanged.

**(2) New “Dump Explorer” tab** — an **offline** browser over an exported *Dump All* (`.jsonl`, produced by
`DumpAllService`). One keyword box searches **classes + properties + functions at once** (space = AND, category
filter All/Class/Prop/Func), so you no longer pick a category and switch tabs first. Results split into two
groups: **✅ In current game** (owning class resolves live — matched by **class short name**, which is stable
across game restarts even when the dump’s recorded address is stale) with a **🡒 Jump** to the live class in the
Live Walker, and **⚠ Not in current game** (read-only metadata reference). Live match = one GObjects pass →
`className→currentAddr` index (class-like metas only). Name (not full path) because `get_object_list` only
carries the short FName and `find_object` is an O(n) scan; the trade-off is same-named classes across packages
collide (last-wins). Disconnect invalidates the match (stale addresses cleared). Purely client-side; parsing
needs no game, matching needs a connected+scanned one. A **⤓ Last export** button one-click-loads the most recent
in-session *Export ▸ Dump All* (no dialog; NOT auto-loaded — repeated exports / a busy tab mustn't clobber the
view). Rows also offer **🔍 Instances** (→ Instance Finder for the owning class) as the class→instance bridge to
the downstream *Related ▸ Locate-in-GWorld/GameEngine* flow; Locate is deliberately NOT put on rows here because
it's instance-oriented and a class object almost never sits in the GWorld/engine forward graph. New files: `Models/DumpBrowseModels.cs` (DTOs + source-gen JSON context + flat
`DumpEntry`), `Services/DumpJsonlReader.cs`, `ViewModels/DumpExplorerViewModel.cs`, `Views/DumpExplorerPanel.axaml`.
Added `IPlatformService.ShowOpenFileDialogAsync` (default no-op + Windows `OpenFilePickerAsync`). Tab inserted
after **Related** (`MainTabIndex.DumpExplorer=11`, tail renumbered 12–17). 6 new unit tests
(`DumpExplorerTests`). An adversarial multi-agent review of the feature caught + fixed 4 issues before ship: a
**blocker** (path-matching was dead against the real DLL — `get_object_list` sends no path and
`UObjectNode.FullPath` is always `""`; switched to class-short-name matching), two **major** cancellation races
(OCE guard read the shared `_opCts` field instead of the op-local token → superseded op reset shared `IsBusy`;
now op-local guard + `EndOp` + Load gated on `!IsBusy`), and a **major** stale-match-on-disconnect
(`SetConnected(false)` now clears `IsMatched`/`LiveAddr`).

**(3) Fixed `DumpAllService` GameOnly silent no-op (pre-existing bug, found during (2)'s review).** The engine-
package skip keyed on `obj.FullPath` from `GetObjectListAsync`, which is **always `""`** (the DLL `get_object_list`
sends no path) → `IsEnginePath("")` false → engine classes were never skipped and every "game only" Dump All
actually included all engine classes. Moved the skip **post-walk** onto the reliable `classInfo.FullPath` (from
`walk_class`/`walk_class_batch`, which do carry the path), before `WalkFunctions` so an engine class costs no extra
round-trip; `FlushClassChunkAsync` now returns a `skipped` count. The existing GameOnly test was masking the bug
(it set `obj.FullPath`, which production never has) — rewritten to be realistic so it genuinely regresses.

Full suite 2306 green.

-----

## 2026-07-10 — CE export: configurable String Len + Fabricate empty/extended TArray slots on Copy CE Field (builds 2028-2038; dev)

**SHIPPED (UI-only; 2299 tests green).** Two exporter features (entry added retroactively 2026-07-11; details in
memory `project-ce-export-strlen-fill-empty`).

- **String Len (build 2028)** — `EmitStringLeaf` hardcoded `<Length>256` for every CE String leaf (4 call sites).
  Now a toolbar **exponent slider** (2^4..2^12 = 16..4096, default 256) mirroring the `DropDownLimit` recipe:
  `MainUiOptions.CeStringLengthExponent` → fan-out to LiveWalker/InstanceFinder → `ceStringLength` param on all 3
  `Generate*` entry points → `[ThreadStatic]` → `EmitStringLeaf`. CSX untouched (separate `Bytesize=18`).
- **Fabricate (builds 2032-2038)** — on **Copy CE Field** (Live Walker only), a "Fabricate" exponent slider
  (0=Off default, else 2^N=2..4096; amber "⚠ large" warning past 256) pads a selected **TArray** to
  `max(N, Num)` element rows so the CE table already has slots for items the current save hasn't populated.
  Object arrays replicate the first resolved element's child layout via `EmitDrilledPointer` (synthetic per-slot
  key = the slot's ABSOLUTE address — **globally unique**, fix `ca47a2c`: an offset-based key collided across
  two same-class arrays at the same property offset and wrongly collapsed to "(shared)"); scalar arrays append
  leaves; struct arrays replicate a resolved element's struct layout. **Next-layer-only gate** (fix `77764e0`,
  in-game bug): the first ship-test fabricated every NESTED array of every element recursively (234-item Cargo →
  ~500k-line file truncated at `MaxEmitEntries`) — now `FabricateActive => _fabricateArrayCount>0 &&
  _emitPointerDepth==0` fabricates ONLY a top-level array of the walked object. Fabricate vs Array Limit:
  `rows = max(Fabricate, walked)` — real data is never hidden; slots past `Num` read past-end (harmless, CE
  shows `??` and re-derefs per poll). **Map/Set not fabricated** (sparse gaps never on the wire). Deferred:
  bare `TArray<FString>` string-container emit path (Phase 3).

-----

## 2026-07-09 — Auto-Detect Player Stats: prop-flags wire + structural scorer + experimental "Detect Stats" panel (builds 2014-2026; dev; IN-GAME VERIFIED TQ2/ES2/SEED)

**SHIPPED (DLL + UI; entry added retroactively 2026-07-11 — full history in memory `project-autodetect-stats`).**
Auto-suggest likely HP/MP/Gold/Mana/XP/Level fields from dumped metadata, fusing name + structural signals.
P0 (labeled corpus) + P3 (learned weights) were **de-scoped** by user call — rules only, no ML.

- **P1 (b2014)** — `prop_flags` (uint64 CPF_* hex) + `array_dim` + `inner_struct_type` on wire→UI→JSONL
  (`Fern.cpp` EncodeClassInfoToJson, `FieldInfoModel`, `DumpAllService`). Omitted at defaults.
- **P2a (b2016)** — structural rules in `PropertyScoringTable`: GAS `StructType=="GameplayAttributeData"` → +4
  (un-categorised→Stats); value-category keyword on container/pointer/delegate → −1; **Current/Max pairing**
  (`ComputeStatPairBonuses`, `NormaliseStatStem` strips max/current/base/… qualifiers; b2019 added
  `maximum`/`minimum` full-word tokens for TQ2's `MaximumHealth`) → +2 per family member.
- **P2b (b2017)** — propflags gating: `CPF_SaveGame`(0x01000000)→+2, `CPF_BlueprintVisible`(0x04)→+1,
  `CPF_EditorOnly`→−4. Deliberately NO Transient/Net penalty (current HP is often transient+replicated).
- **P4 (b2022-2026)** — experimental-gated **"Detect Stats"** tab: one button runs the scorer → top-K grouped by
  class → per-class one `find_instances` + one `walk_instance` → confirm on live-instance-exists +
  value-plausible/GAS + Max-sibling → confidence rank. Opt-in **snapshot signal** boosts names that decreased
  across 2 usable snapshots (+ Δ column "320.23 → 300.33"). Prominent "reference only, low accuracy" disclaimer.
  Row handoffs 🌍/inst/copy reuse the InterestingProperties pattern.
- **In-game verified**: TQ2 (80 GAS `GameplayAttributeData` fields / 12 AttributeSet classes — GAS rule fires;
  live run 80 candidates / 41 confirmed incl. Health pair + MaxWalkSpeed), ES2 + SEED (0 GAS — rule correctly
  silent, keyword+type+pair+flags path). Side fixes: SnapshotStore sync-SQLite freeze → `Task.Run`;
  Locate-in-GWorld `not_reachable` banner wrongly said "raising depth won't help" (BFS is depth-bounded —
  log-proven depth 5 fails / depth 8 finds 7 hops) → message + a once-per-session pre-search confirm when
  `GWorldLocateDepth < 7` (b2026).

-----

## 2026-07-08 — See-through occluders (Schlacht) + configurable pierce depth (builds 2006-2011; dev; VERIFIED Tower of Mask + DQ7R)

**SHIPPED (DLL + UI; experimental-gated; entry added retroactively 2026-07-11 — details in memory
`project-seethrough-occluders-schlacht`).** New Frieren module **`Schlacht`** (「全知者」): a ~10 Hz worker
traces **camera→VIEW-forward** (`UKismetSystemLibrary::LineTraceSingle` on the game thread via Stark), hides the
nearest **N** non-Pawn blocking actors via `SetActorHiddenInGame(true)` so world geometry stops covering the
view; hidden actors are restored as the view moves and un-hidden on disable/disconnect/shutdown.

- **Camera→VIEW-forward, not camera→pawn** (the live-verify pivot): in first-person the camera sits AT the
  pawn's eyes → degenerate zero-length ray. Forward = `(cosP·cosY, cosP·sinY, sinP)` × 100000uu works first-
  AND third-person (Tower of Mask ✓ first-person, DQ7R ✓).
- **MOBs stay visible**: `Aura::ClassDerivesFromAny(hit, {"Pawn","Character"})` — enemies/NPCs/player are kept
  + pierced-through and don't consume N.
- **Pierce depth N (build 2009)**: hide the nearest N occluders (UI NumericUpDown 1-10, live-push while active)
  — iterative single-trace advancing past each impact (+2uu step), NO `LineTraceMulti` (which would leak the
  engine-allocated `TArray<FHitResult>` every tick). Example: N=1 hides a painting, N=2 hides painting+wall.
- **The fragile core**: `ExtractHitActor` pulls the actor from FHitResult — UE4 `Actor` TWeakObjectPtr →
  leading int32 ObjectIndex → `Aura::GetByIndex`; UE5 `HitObjectHandle` best-effort. Per-game fragile;
  one-shot failure warning (diagnostics quieted to lifecycle-only in build 2011).
- **CE toggle**: Mimic `CMD_SEETHROUGH=14` mailbox + `SeeThroughScriptGenerator` (editable `pierceCount`);
  "Add to CE" is **AOBMaker-only** (grayed out without the plugin — no clipboard fallback, by request).
- **Known limitation**: NO-OP on collision/render-split games — FF7R's trace hits invisible
  `CollisionAssetActor` proxies, so hiding them changes nothing (harmless). Light/shadow passthrough was
  evaluated separately (2026-07-09, todo.md): dynamic lighting already follows the hide; baked/static shadows
  are Lightmass-textured into the receiver and **can't** be removed at runtime (WON'T-DO).

-----

## 2026-07-08 — Teleport experimental gating: Keep-Foreground / Fly / Standalone-trainer cards opt-in (build 1995; dev)

**SHIPPED (UI-only; entry added retroactively 2026-07-11).** The Teleport tab's riskier cards — **Keep
Foreground**, **Fly / Noclip**, **Standalone CE Lua trainer** — plus the "Experimental Hotkeys" card are now
hidden unless the experimental opt-in is enabled (`ExperimentalEnabled` / `ShowExperimentalHotkeys`,
Avalonia `IsVisible` = collapse). Disabling the opt-in force-releases any active experimental state first
(foreground lock off, fly off) so a hidden card can never keep a hook alive. Later experimental features
(See-through 2006, Detect Stats tab 2022) ship behind the same gate. Policy per the wiki: experimental features
are documented + marked experimental, but the enable path is never shown.

-----

## 2026-07-08 — Inject picker "already loaded" detection + Keep-Foreground cursor-lock fix + emergency hotkey + GodMode/KeepFg "Add to CE" delivery (build 1986; dev)

**SHIPPED (DLL + UI; Keep-Foreground cursor-lock fix LIVE-VERIFIED P3R).** Four related fixes/features.

**(4) Raw-AA-to-clipboard sweep — everything now delivers paste-able CE XML.** The God Mode + Keep Foreground
"Copy CE Script" buttons emitted **raw AA code** to the clipboard — same bug the Global-Pointer buttons had: a
bare AA body can't be pasted into a CE memory record. Now they use the identical delivery as Get
GWorld/GameEngine: push straight into CE via AOBMaker (`CreateAAScript`, group `UE5CEDumper (DLL)`) when
connected, else copy paste-able CE memory-record XML (`CheatTableBuilder.WrapAaScriptXml`). Shared helper
`PushOrCopyToggleScriptAsync(desc, script)`; commands one-liners; labels `Copy CE Script`→`Add to CE`; tooltips
updated. Regression tests lock the no-AOBMaker path to wrapped `<CheatTable>` XML (not a bare `[ENABLE]` body).
**Same milder bug fixed in the Invoke / exec / Debug-Camera paths** (`LiveWalkerViewModel` GenerateInvokeScript
×2, `MainWindowViewModel` InterestingFunctions + Console baked-invoke + Debug-Camera): those already
push-first, but their clipboard FALLBACK copied raw AA — now wrapped via `WrapAaScriptXml` too (status text
updated; the stale "embed ue5_invoke_helper.lua" note dropped). The CE-XML pointer-chain exports
(LiveWalker `CopyToClipboardAsync(xml)`) were left untouched — already full `<CheatTable>` XML. Tests 2242→2244.

**(1) Q1 — Inject picker shows already-loaded dumper + version.** The "Inject into running game" picker now
detects whether OUR dumper DLL is ALREADY active in each UE process (via a proxy, a prior inject, or a CE `.CT`)
and shows the running DLL version, so the user isn't tricked into a redundant double-load that fights over the
pipe. New pure classifier `DumperModuleDetector.Classify` (Services, unit-tested) takes the target's loaded
modules (file name + PE ProductName + FileVersion) and reports `(loaded, mode, version)` — `mode` =
`proxy: <name>` (version/dinput8/dxgi carrying ProductName `UE5CEDumper`) / `injected` (`UE5Dumper.dll`) /
`loaded: <name>`; identity is `ProductName == UE5CEDumper` (same check as `IsOurProxyDll`), so the real system
version.dll/dxgi.dll never counts. `WindowsPlatformService.DetectDumper(Process)` walks `Process.Modules`,
version-probes only the ≤4 name-matching modules (reading a version resource touches the file), and only for
`IsUe` rows; access-denied/32-bit/exited → "unknown". `GameProcessInfo` gained `DumperLoaded/DumperLoadMode/
DumperVersion` + a `DumperStatusLine`; `ProcessPickerWindow` renders an amber "⬤ … already active; Inject will
just Connect" line; `ProxyDeployViewModel.InjectIntoRunningGame` skips injection and connects when
`DumperLoaded`.

**(2) Q2 — Keep Foreground no longer traps the mouse.** Root cause: `Grausam` makes the game believe it never
lost focus AND keeps its game thread ticking, so the game re-applies `ClipCursor()`/`SetCursorPos()` every frame
to confine the OS cursor to its viewport — Windows auto-releases the clip on real deactivation but the
still-running game re-clips within a frame, locking the mouse across the whole desktop (LIVE-REPRODUCED P3R,
3840×2160). Fix: `Grausam` now also MinHooks `user32!ClipCursor` + `SetCursorPos`. While the lock is on but the
game is NOT genuinely foreground (checked via the un-hooked `g_origGetForegroundWindow` trampoline so the check
isn't fooled by our own illusion), `ClipCursor(rect)` is turned into `ClipCursor(nullptr)` and `SetCursorPos` is
swallowed; when the game truly is foreground both pass through so in-game mouse-look is untouched. Disable also
force-releases any masked clip. Cursor hooks are best-effort (per-target enable, NOT `MH_ALL_HOOKS`, so
Stark/Solitar/Laufen hooks aren't touched); the primary GetForegroundWindow hook still gates the whole feature.

**(3) Q2 — Emergency "Keep Foreground OFF" global hotkey (safety net).** Because a trapped cursor can't reach the
UI's OFF button, added `foreground_on` / `foreground_off` rows to the Teleport hotkey table (generic over
`ActionId` — capture/persist/register for free; routed in `OnMarkerHotkeyPressed` to the existing
`ForceForegroundLockOn/OffCommand`). Bind `Keep Foreground OFF` to a global key to release the lock from any app.

Tests: +`DumperModuleDetectorTests` (8 cases); hotkey-count lock 23→25. 2242 tests green.

-----

## 2026-07-08 — Teleport tab: Global Pointers (GWorld / GameEngine) CE Lua + card reorder (build 1978; dev)

**SHIPPED (DLL + UI; live verify pending).** Two new one-click **"DLL-invoke" CE Lua** exports on the Teleport
tab that ask the injected `UE5Dumper.dll` for a live global-pointer address and show it in a message box:
**Get GWorld** and **Get GameEngine instance address**.

**DLL** — new mailbox command `CMD_QUERY_PTR=13` (`Mimic.h`/`Mimic.cpp`, `HandleQueryPtr`) with two ops
(`QueryPtrOp`): `QUERY_OP_GWORLD=0` writes `paramsData[0..7]`=`&GWorld` (the cached `g_cachedGWorld` slot) +
`[8..15]`=`UWorld*` (slot deref via `Macht::ReadSafe`); `QUERY_OP_GAME_ENGINE=1` calls `Genau::FindGameEngine`
and writes `[0..7]`=`UEngine*` instance, `[8..15]`=`UClass*`, `[16..143]`=class name. `result`=0 found / −1 not
resolved. Read-only + thread-agnostic → runs on the mailbox polling thread even when the game thread is idle. No
new C ABI export / `.def` change — reuses the already-exported `g_invokeMailbox`. **Why the mailbox and not a
plain `UE5_GetGWorld` export + `executeCodeEx`:** CE Lua's `executeCodeEx` can't reliably read an export's
return value on protected games (returns nil) — the repo's standing lesson (godmode-spec §10 / lessons-learned).

**UI** — new `PointerQueryScriptGenerator`: a **stateful toggle** record that publishes a resolved global pointer
as a **registered CE symbol** the user references directly (`[UE_GWorld]+offset` / `[UE_GameEngine]+offset`).
Both resolve via one mailbox round-trip (address at paramsData[0..7] = mb+0x328), then differ by backing:
**GWorld** registers the symbol DIRECTLY to the returned **&GWorld slot** — `[UE_GWorld]` derefs the slot to the
CURRENT UWorld, so it **auto-follows level transitions** (no buffer, disable just unregisters). **GameEngine**
has no static slot (found by walking GObjects), so the `UEngine*` is copied into an `allocateMemory(8)` buffer,
the symbol registered to it (SNAPSHOT), and `[DISABLE]` frees the buffer. Hygiene-compliant: errors
`showMessage`+untick, success is `dbg()`-gated + close-on-clean.
New Teleport **"Global Pointers → Cheat Engine symbols"** card with **Get GWorld** / **Get GameEngine** buttons
(`GetGWorldAddressCommand` / `GetGameEngineAddressCommand`). **Delivery: push via AOBMaker (`CreateAAScript`) so
it lands as a real CE memory record; fallback (AOBMaker not connected / refused) copies the paste-able CE
memory-record XML** (`CheatTableBuilder.WrapAaScriptXml` → `<CheatTable><CheatEntries><CheatEntry>…
<VariableType>Auto Assembler Script</VariableType><AssemblerScript>…`) to the clipboard for right-click → Paste.
(A bare `[ENABLE]`/`[DISABLE]` AA body can't be pasted into a memory record — the v1 raw clipboard copy didn't
work.) `CeMailboxLayout.CmdQueryPtr=13`.

**Layout** — the **"Standalone Trainer (no DLL)"** card moved from the TOP of the tab to the BOTTOM (grouped
with the other export cards, below CE Export); the new Global Pointers card sits just above CE Export.

2232 tests green (+11 `PointerQueryScriptGeneratorTests`, incl. symbol register/unregister + the paste-able-XML
wrapper). Live in-game verify pending.

-----

## 2026-07-07 — Copy CE XML / Copy CE Field / Export CSX made cancellable (builds 1974-1977; dev; PRs #418/#419)

**SHIPPED (UI-only; entry added retroactively 2026-07-11).** The three heavy CE exporters — **Copy CE XML**,
**Copy CE Field** (both `fd2e796`, build 1974) and **Export CSX** (`13121da`, build 1977) — now abort cleanly
mid-flight: a `CancellationToken` is threaded through `ResolveDrilldownAsync` and the emit loops, so a deep
drill-down / huge-array export can be abandoned without wedging the pipe or leaving the DLL walking. GOTCHA
locked in memory `feedback-pipeclient-bare-oce-cancel-guard`: the user-cancel catch MUST be
`catch (OperationCanceledException) when (cts.IsCancellationRequested)` — `PipeClient` throws a **bare OCE on
disconnect** (token=None), and an unguarded catch would swallow a disconnect as if the user cancelled. Also
fixed a flaky debounce test in the same push.

-----

## 2026-07-06 — In-UI DLL injection + inject-ue.ps1 CLI (build ~1958-1962; dev)

**SHIPPED (UI + CLI; no DLL change).** A third way to load `UE5Dumper.dll` besides the CE `.CT` LoadLibrary
and the version/dxgi/dinput8 **proxy**: inject it into an **already-running** UE game. No Cheat Engine, no
pre-deployed proxy, no game restart.

**UI** — Proxy Deploy tab gains an **"Inject into running game…"** button → a modal **process picker**
(`ProcessPickerWindow`, code-behind/AOT-safe like ConfirmDialog; UE games listed first, "Show all" toggle) →
`CreateRemoteThread` + `LoadLibraryW` → **auto-connect** the pipe. Ported from the sibling `discrete`
project's `WindowsDllInjector` but with **classic `[DllImport]`** (this project bans `LibraryImport`/
`AllowUnsafeBlocks`); all inject P/Invokes are blittable. Behind `IPlatformService` (`GetRunningProcesses` +
`InjectDll`), x64-only (rejects Wow64). UE-process detection reuses the drive-scan signals on the process exe
path (`UeProcessDetector`: `*-Shipping.exe`, or under `\Binaries\Win64\` with `Engine`/`Content\Paks`
up-tree). New `GameProcessInfo`/`InjectResult` models; `IProxyDeployService.ListGameProcessesAsync/
InjectDllAsync` façades; `ProxyDeployViewModel.InjectIntoRunningGame` + `PickProcessAsync`/
`RequestConnectAsync` delegates (`MainWindowViewModel` wires the latter → `ConnectCommand`). DLL path =
`<exeDir>\UE5Dumper.dll`.

**CLI** — `scripts/inject-ue.ps1`: one combined command-line injector (list + inject + auto). `inject-ue.ps1`
auto-injects the single running UE game (0 → abort; 2+ → list + abort); `-List` / `-ProcessId N` / `-Dll`.
Same technique via `Add-Type` classic `[DllImport]`. Verified `-List`/auto-abort/DLL-resolution on the dev box.

AOT UI + 2211 tests green (+10 `UeProcessDetector`). **Single-player / offline only** — `CreateRemoteThread`
injection is commonly flagged by AV and blocked/banned by kernel anti-cheat (same caveat as the CE inject +
proxy already carry). Live in-game inject verify pending (technique proven via the CLI on the dev box).

## 2026-07-06 — Keep-Foreground lock (Grausam) — stop background game-thread pause (build ~1950-1955; dev; LIVE-VERIFIED P3R)

**SHIPPED (DLL + UI; NEEDS RE-INJECT).** Some games idle/pause their **game thread whenever they are not the
foreground window** (Persona 3 Reload — verified via its dump log: ProcessEvent hook-fire validator saw 0
fires, POV fell back to raw cached read). Root cause (confirmed in vendored UE source): `FApp::HasFocus()`
→ `FWindowsPlatformApplicationMisc::IsThisApplicationForeground()` = *"does `GetForegroundWindow()` belong to
my PID?"*. Both UE's `t.IdleWhenNotForeground` idle **and** any game-side focus-loss pause gate on this, so
clicking our UI (or CE) makes the game think it lost focus → thread sleeps → invokes/POV time out. Editing the
game's `Engine.ini` `[ConsoleVariables] t.IdleWhenNotForeground=0` did NOT help (set in code/pak, and/or the
game has its own focus-pause).

**Fix — new module `Grausam` (illusion magic):** MinHook `user32!GetForegroundWindow` (resolved via
`GetProcAddress`, so the real function body is patched for all callers) to always return the game's own
top-level window (largest visible un-owned window of our PID, via `EnumWindows`) when the real foreground
belongs to another process. `IsThisApplicationForeground()` then always reports foreground → no idle, no
focus-pause, at the *root* signal (version- and game-agnostic; also stops audio ducking). No AOB / no
IConsoleManager needed. Soft-disable leaves the hook installed and passes through (no unhook race). Coexists
with Stark's MinHook (`MH_Initialize` guards `ALREADY_INITIALIZED`).

**v2 (build 1955) — the fix that actually worked.** The GetForegroundWindow hook alone did NOT stop P3R
(log: lock ENABLED but game still paused). P3R's pause is **`WM_ACTIVATEAPP`-message-driven** (UE's
`FWindowsApplication` → `OnApplicationActivationChanged(false)`), not GetForegroundWindow polling. So Grausam
now ALSO subclasses every top-level game window's WndProc (`SetWindowLongPtrW` + `SetProp`, gated on enabled)
and rewrites the deactivation messages to "active": `WM_ACTIVATEAPP→TRUE`, `WM_NCACTIVATE→TRUE`,
`WM_ACTIVATE→WA_ACTIVE`, swallow `WM_KILLFOCUS`. Two focus channels now covered (polling + messages).
**LIVE-VERIFIED on P3R** (build 1955): log shows it subclassed the real 3840×2160 game window and
invoke/POV ops work while backgrounded. Build gotcha: `-Target DLL` does NOT build the proxy DLLs (the
injected file) — use `-Target All`.

**Wiring:** pipe `set_foreground_lock` / `get_foreground_lock` (Renge) → `Grausam::SetForegroundLock/IsEnabled`
(Fern); `IDumpService.Set/GetForegroundLockAsync` (DumpService); Teleport tab **Keep Foreground** card
(ON/OFF/Unknown badge + Force ON/OFF/↻), off by default. DLL + AOT UI + 2194 tests green.

**CE mailbox path (build 1956).** Added `Mimic CMD_FOREGROUND=12` (op `FG_OP_SET`/`FG_OP_GET`, `ForegroundOp`)
so pure-CE users can toggle it without the pipe/UI. Thread-agnostic — Grausam is a pure Win32 hook/subclass,
so `HandleForeground` runs entirely on the mailbox **polling thread** (not the game thread), and therefore
works even while the game thread is idle. New `ForegroundScriptGenerator` (mirrors `ProtectionScriptGenerator`,
quiet-by-default per the CE-Lua hygiene rule) + a **Copy CE Script** button on the Keep Foreground card
(`CopyForegroundLockScript`); `CeMailboxLayout.CmdForeground=12`. The bundled `UE5CEDumper.CT` is unchanged
(GodMode/Fly/etc. aren't baked into it either — the CE path for all of them is the Copy-CE-Script generator).
2201 UI tests green (+7 generator tests).

## 2026-07-06 — Generic (non-Steam) drive scan in Proxy Deploy (build ~1944; dev)

**SHIPPED (UI-only, no re-inject).** The Proxy Deploy tab could only find Steam-installed games. Added a
**Source toggle** (Steam / Scan Drives) at the top of the same panel — Scan Drives searches user-chosen
drives for UE games installed *anywhere* (Epic / GOG / manual), then feeds them into the identical
Deploy / Undeploy / Update / Refresh machinery (all source-agnostic — they only need `BinariesDir`).

**Requirements → mechanism.** (1) drive checkbox-list; (2) **partitions of one physical disk scan
sequentially, different disks in parallel** — `IOCTL_STORAGE_GET_DEVICE_NUMBER` groups drives, groups run
`Task.WhenAll` while drives within a group run a sequential `foreach await` (bounded by
`SemaphoreSlim(Clamp(ProcessorCount,1,4))`); (3) inaccessible folders skipped and walk continues
(`EnumerationOptions.IgnoreInaccessible` + materialize-in-try around every enumeration); (4) UE detection
by folder structure (`LooksLikeUeGameRoot`: sibling `Engine\Binaries\Win64` / `Content\Paks\*.pak|utoc|ucas`
/ `*-Win64-Shipping.exe`) with **prune-on-match** (never descend a matched root's multi-GB Content tree)
+ depth-6 cap + reparse-point cycle guard + hard-skip set; (5) Steam libraries excluded (resolved roots
via `GetSteamLibraryFoldersAsync` + `steamapps` hard-skip fallback); (6) results drop into the existing
`Games` grid.

**Impl / AOT.** Drive→physical-disk mapping is **classic `[DllImport]`** `CreateFileW(\\.\C:, access=0)`
+ `DeviceIoControl` + blittable 12-byte `STORAGE_DEVICE_NUMBER` — no WMI/`System.Management` (AOT-banned),
no `LibraryImport`. `dwDesiredAccess=0` = query-only = **no admin** needed. Lives behind
`IPlatformService.GetLogicalDrives()` (Core stays platform-free); `ProxyDeployService` gains an
`IPlatformService` ctor param (wired in `App.axaml.cs`). Unknown disk (spanned/network/virtual /
`DeviceNumber==0xFFFFFFFF`) → its own scan group. New models `DriveDescriptor` / `DriveScanProgress`;
new service methods `GetScannableDrivesAsync` / `FindUeGamesOnDrivesAsync`; VM `ScanDrivesMode` +
`LoadDrives`/`ScanDrives` commands; per-game-dir detection (`ScanGameFolder`) reused verbatim.
`IsKnownStubExe` also filters `UnrealEditor/UE4Editor/UnrealFrontend.exe`. 2194 UI tests green (+14 pure-
helper + Steam-exclusion end-to-end).

**Follow-ups (same build).** Added a **Cancel** button (`[RelayCommand(IncludeCancelCommand=true)]` →
`ScanDrivesCancelCommand`, shown only while `IsScanning`) and **mode persistence** (`ScanDrivesMode` in
`ProxyDeployUiOptions` + the `ProxyDeployPersist`/Apply/Capture blocks; restoring drive-mode lazily
re-enumerates drives). **Real-machine verified** (headless P/Invoke replica, non-elevated): 7 drives →
6 physical disks, `STORAGE_DEVICE_NUMBER`=12 bytes; two volumes sharing physical disk 0 correctly grouped
into one sequential lane while the other five run parallel — requirement 2 confirmed on real hardware.

## 2026-07-04 — CE generators skip OUT-string params (crash fix) (build ~1913; pushed dev)

**SHIPPED (UI-only, no re-inject).** Parity fix bringing the two CE-Lua generators in line with the
PIPE path (build ~1911): an OUT `FString&` param (the callee fills it) must stay a zeroed/empty FString,
because building one would make the callee's reassignment `FMemory::Free` our CE-allocated (non-FMemory)
`Data` buffer and crash. INPUT strings are unaffected — the common case.

**Impl.** `InvokeScriptGenerator` (INV): the FIRE loop skips `IsStringType && IsOut` params (emits a
`-- out FString left empty` comment; the zero-fill already left a valid empty FString), and the inline
`writeFStr` builder is now only emitted when an INPUT (non-out) string param exists.
`InvokeParamDialog.CollectBakedValues` (AA(Baked)): skips out-string params so the helper never builds
one. Out-string params are rare, so this was a latent edge case, not a common crash. 2162 UI tests
green (+2).

## 2026-07-04 — PIPE FIRE string INPUT params (DLL builds the FString) (build ~1911; pushed dev; needs re-inject)

**SHIPPED (DLL + UI + protocol; NEEDS RE-INJECT).** The in-app **PIPE** FIRE path can now pass string
input params — the last invoke path that couldn't (INV / AA(Baked) got FStrings in build ~1908). The
hex-buffer model can't carry an FString because its `Data` pointer must be a valid **game-process**
address the UI can't allocate. Fix: the UI sends a `str_params` descriptor list and the **DLL builds
each FString in-process**.

**Why the DLL can.** UE5Dumper.dll is injected, so a buffer it `malloc`s lives in the game's address
space (the same reason `paramBuf.data()` already works as the ProcessEvent params pointer). For each
`{ off, wide, text }`: allocate a char buffer (wide → UTF-8→UTF-16LE via `MultiByteToWideChar`, full
Unicode — better than the CE-side ASCII-only path; narrow → raw UTF-8/ANSI bytes), patch the by-value
`{ Data, Num, Max }` at `off`, run ProcessEvent.

**Lifetime.** UE's convention makes the CALLER own the params and a UFUNCTION takes its FString by
value/const-ref, so freeing after ProcessEvent is correct — EXCEPT on a game-thread dispatch **timeout
(-5)**, where the request stays queued with a copy whose `Data` aliases our buffers; a late drain would
deref them, so we deliberately **leak on -5** (matches the CE-side policy). Every other path frees now.

**OUT strings.** The UI only sends `str_params` for **input** (`!IsOut`) string params. An OUT
`FString&` must stay a zeroed/empty struct — otherwise the callee's reassignment would `FMemory::Free`
our non-FMemory buffer and crash. A zeroed slot is a valid empty FString the callee fills safely.

**Impl.** `Fern.cpp` (`invoke_function`): parse `str_params`, build/patch/free (leak-on-`-5`), bounds-
checked. `IDumpService` / `DumpService` gained an `IReadOnlyList<InvokeStringParam>? stringParams` arg
(new `InvokeStringParam` record) serialised to `str_params`. `InvokeParamDialog.OnFireClicked` collects
input string fields instead of writing them as scalars. `ParamBufferBuilder.IsStringType/IsWideString`
made public. `docs/pipe-protocol.md` updated. DLL + UI build clean, 2160 UI tests green (+3). **Re-inject
required** (DLL change). Fern has no unit tests → verify the actual call in-game.

## 2026-07-04 — FString INPUT param support in invoke generators + helper (build ~1908; pushed dev)

**SHIPPED (UI + helper `.lua`, no re-inject).** String **input** params (`StrProperty` /
`Utf8StrProperty` / `AnsiStrProperty`) are now built for you by the two CE-Lua invoke generators.
Before, `ParamBufferBuilder` / `InvokeScriptGenerator` wrote a bare int32 into the FString slot and
`ue5_invoke_helper.lua` raised `Unknown param type 'fstring'` — string params were effectively
unusable from the generated scripts.

**Model.** A UE string param is passed **by value**: the params buffer holds the whole 16-byte
`{ CharT* Data; int32 ArrayNum; int32 ArrayMax }` struct inline (NOT a pointer to it). So the scripts
now `allocateMemory` a char buffer in the target process, write the chars + null terminator, and stamp
the three struct fields. Wide (`StrProperty` → UTF-16LE) vs narrow (`FUtf8String`/`FAnsiString` → raw
bytes) is preserved. ASCII / basic-Latin only for the wide case (no UTF-8 transcode).

**Lifetime.** The Data buffer is intentionally **leaked** by default — freeing is unsafe if the callee
retained the pointer, and it's CE-allocated (not UE `FMemory`), so the game must never free it. The
helper tracks allocations in `_ue5_invoke_str_bufs` and exposes an opt-in `freeInvokeStringBuffers()`
for when you know the call merely read the string. A few small leaks per one-shot cheat is the safe
default.

**Impl.** Helper `ue5_invoke_helper.lua` (v1.1→1.2): new `writeFStringInline` + `writeBakedParams`
`fstring`/`fstringn` branches + public `freeInvokeStringBuffers`. `BakedScriptGenerator`: new
`MapInputType` (keeps wide/narrow — the return path still uses `MapToHelperType`, so its complex-type
classification is untouched) + `RenderLiteral` emits a quoted Lua string for string types.
`InvokeScriptGenerator` (self-contained, no helper): inlines a small `writeFStr(addr, s, wide)` builder
when any string param is present + text field defaults to empty. `ParamBufferBuilder.GetDefaultValue`
strings → empty (nicer dialog field). Generated scripts stay **ASCII** (bilingual EN/中文 comments live
only in the C# source + the static `.lua`, per the user's request — never in emitted text).

**Still out of scope.** The **PIPE** (in-app) FIRE path can't build FStrings (it has no target-process
allocation in the hex-buffer model — that needs a DLL-side change); string params there still pass
empty. UI + helper build clean, 2157 UI tests green (+13).

## 2026-07-04 — Invoke scripts print the return value under UE5_DEBUG (build ~1905; pushed dev)

**SHIPPED (UI-only, no re-inject).** Both CE-Lua UFunction invoke generators now decode + `print()`
the return value when `DEBUG ~= 0`. Before this, only the in-app **PIPE** dialog decoded returns (via
`StructReturnDecoder`); the generated **INV** (`InvokeScriptGenerator`) never read the return at all,
and **AA(Baked)** (`BakedScriptGenerator`) printed it only in the opt-in "Verify return value" mode —
so a user who turned DEBUG on still saw nothing (the exact complaint that prompted this).

**Where the return lives.** After a successful invoke the value sits in the mailbox params buffer at
`g_invokeMailbox.paramsData + returnOffset` (`mb + 0x328 + off`) — UE lays the return param inside the
same params blob as the inputs. Static-native funcs run ProcessEvent in-place ([Mimic.cpp:416](../dll/src/Mimic.cpp));
game-thread funcs copy `ownedParams` back into the caller buffer on success ([Stark.cpp:376](../dll/src/Stark.cpp)) —
the timeout path does NOT copy back, hence the `result==0`/`ok` gate.

**Impl.** New shared emitter `CeInvokeReturn.AppendDecodeAndPrint` (single source of truth so the two
generators can't drift): scalars via the matching CE read; `StrProperty` derefs the `{Data,Num}` header
and wide-reads the string (`readString(_sp, 512, true)`); `Utf8Str/AnsiStr` narrow-read; every other
buffer type (`FText`/`TArray`/`TMap`/`TSet`/`FStruct`/delegate) falls back to a bounded raw-hex dump.
Wired into `InvokeScriptGenerator` (both the direct-invoke and the param-form FIRE paths, base local
`_PDret = mb + 0x328`, gated `if result == 0 and DEBUG ~= 0`) and `BakedScriptGenerator`'s non-verify
branch (resolves the mailbox via `getAddressSafe` + module-prefixed fallback, gated `if ok and DEBUG ~= 0`;
verify mode keeps its own richer Before/After print, so the two never double-fire). Uses `_PDret`
(not `PD`) so it can't collide with the write-path's `PD` local, and the `dbg()` preamble's own
`DEBUG ~= 0` substring means "no return block" tests must assert on `_PDret` / the full gate line.

**Hygiene.** Fully honours the quiet-by-default rule: nothing prints when `DEBUG == 0` (the
success-close still fires); when `DEBUG ~= 0` the value prints and the window stays open. Tooltips for
INV / PIPE / AA(Baked) rewritten to explain the three paths + where the return value goes.

**Known gap (unchanged).** String **input** params are still not built for you — the generators write a
number into the FString slot rather than allocating a `{Data,Num,Max}` buffer + wide char data (and
freeing it after). Documented as a follow-up. UI builds clean, 2144 tests green.

## 2026-07-04 — Game-thread stall detection: POV fast-fail + app-wide "paused" banner (build ~1902; pushed dev, dev→main PR)

**SHIPPED.** Fix for a ~3.5-min object-list load diagnosed from a Brimstone (UE5.6) log: the **game
thread was paused** (user paused / alt-tabbed), so every `Stark` game-thread invoke timed out at
5000ms for 5.5 min straight. Teleport's 500ms auto-refresh polls `teleport_get_pov`, whose
`Wirbel::GetPovImpl` does **two** game-thread invokes (`GetCameraLocation` + `GetCameraRotation`) =
~10s block per poll; the DLL's serial pipe dispatch (head-of-line, see `multipipe-eval.md`) then
starved the pure-memory `get_object_list` paging behind those blocks. `teleport_get_pose` is a raw
read (no invoke) — only POV blocks. DLL + UI (AOT publish) build clean, 2120 tests green. **Needs
re-inject; verify in-game.**

**B — fast-fail (the cure).** `Wirbel::GetPovImpl` now guards the two getters on
`Stark::IsGameThreadResponsive()`; when the thread isn't ticking it skips the invokes and falls
straight through to `ReadPovRaw` (the same trusted cached-POV path already used on cook-stripped
TQ2 / Octopath; response `source="raw"`). While paused the world is frozen, so the cached POV equals
the live value — correct, and no 10s stall. Resumes invoking automatically once the thread ticks.
POV is read-only + Teleport-tab-only — zero impact on dumped/scanned data.

**Stark liveness.** `HookedProcessEvent` now stamps `s_lastHookFireMs` on every fire; `InstallHook`
stamps `s_hookInstalledMs`. `kStallThresholdMs = 500` (PE fires hundreds of times/frame, so only a
genuine stop crosses it). `IsGameThreadResponsive`: hook-not-active → responsive (the first invoke
lazily installs the hook — never block that); fired-before → `now-lastFire ≤ thr`; **active but NEVER
fired → `now-installTime ≤ thr`**. That last branch is essential: in the logged case the hook was
installed but the game was already paused, so it never fired and `s_lastHookFireMs` stayed 0 forever
— last-fire alone would report "responsive" indefinitely. Consequence: the first POV poll after
connecting-while-paused still costs ~10s (installs the hook + baselines); every poll after skips.

**A — app-wide banner.** `Renge::MakeResponse` rides `game_thread_stalled` (= `!IsGameThreadResponsive()`)
on EVERY success envelope — one atomic + steady-clock read, so any command the UI sends updates the
hint (no dedicated heartbeat command/timer). UI: `PipeClient.ReadLoopAsync` raises the new
`IPipeClient.GameThreadStalledChanged` on transition only; `LaneRoutingPipeClient` forwards both lanes;
`MainWindowViewModel.GameThreadStalled` (reset false on disconnect) drives a full-width amber banner
in `MainWindow.axaml` (`str.Banner.GameThreadPaused`) visible from every tab. Two `IPipeClient` test
doubles gained the event.

**Scope.** Only `GetPovImpl` uses the stall guard; other invokes (recall / function-invoke / Laufen /
Solitar) are untouched — the re-assert workers block their own threads, not the pipe, so they don't
starve scans. Single-player.

## 2026-07-03 — Standalone CE-Lua trainer export (no DLL) (build ~1898; pushed dev, dev→main PR)

**SHIPPED.** A "generate a self-contained Cheat Engine trainer for ONE game+version that runs without
the DLL" export on the Teleport tab. Extends the GWorld-anchored AA-walk (PR #333) from a read-only
symbol list into a read+write trainer: **Move Speed / Low Gravity / Super Jump / GodMode / coordinate
TP (Save+Recall)**, driven by CE `createTimer` re-assert loops + raw memory writes over a baked
`[[GWorld]+..]` pointer chain. Delivered into CE via the AOBMaker plugin (`CreateAAScript` per entry),
**gated on AOBMaker availability (gray-out + hint, NO fallback)**. DLL + UI build clean, 2135 UI tests green.

**DLL.** New read-only pipe command `get_trainer_offsets` (`Renge.h`/`Fern.cpp`) →
`Wirbel::GetTrainerOffsets` decomposes `*GWorld→Pawn` into bake-able `(offset, deref)` hops (reusing
`ResolveChain`) + gathers RootComponent/RelativeLocation + FVector width + CharacterMovement +
MaxWalkSpeed/GravityScale/JumpZVelocity offsets; `Solitar::ResolveProtectBits` surfaces every matched
protection bit as a pawn-relative `byteOffset`+`mask`+`protect`. Fern composes one JSON reply.
(Both new fns must live in the public `namespace Wirbel/Solitar`, NOT the file's anon namespace — anon
placement compiled but LNK2019'd.)

**UI.** `Models/TrainerOffsetsModels.cs` (JsonNode-parsed POCO) + `DumpService.GetTrainerOffsetsAsync`
+ `StandaloneTrainerScriptGenerator` (Setup + per-feature `[ENABLE]`/`[DISABLE]` entries; features
whose offset didn't resolve are omitted; Setup AOB-scans GWorld + defines shared globals; re-assert via
`createTimer`; **movement DISABLE restores the captured natural base** so toggling reverts and re-enable
can't re-capture a forced value). Teleport-tab "Standalone Trainer (no DLL)" card (Export gated on
`CanExportTrainer` = connected + AOBMaker) + tab `Tag="Teleport"` + tab-switch AOBMaker re-probe +
`MainWindowViewModel` `SetEngineState`/`CheckAobMakerAsync` wiring (Teleport was missing from both
`SetEngineState` clusters).

**Attribution.** Every generated CE-Lua/AA script now carries
`Generated by UE5CEDumper | https://github.com/bbfox0703/UE5CEDumper` via the shared
`CeLuaHygiene.Attribution` const/helper (all 8 `*ScriptGenerator` + `CeXmlExportService`'s
GWorld-walk/AOB scripts). The two coordinate-TP scripts also carry a "raw write — weaker than the DLL;
the character may not visibly move" note.

**Limitations (by design).** Coordinate TP is a raw `RelativeLocation` write with no `DeepForceWorldPos`
— on cooked-out-setter games (verified: **Elliot UE5.4** — coords change, character doesn't visibly
move); the reliable game-thread `ProcessEvent` teleport stays DLL-only. Cursor-trace TP, `StopMovement`
settle and POV-set are not portable. Per game+version — regenerate after a patch.

## 2026-07-03 — Magic-number centralization: Tier 1 + Tier 2 pointer helper (build ~1888; pushed dev, dev→main PR)

**SHIPPED.** A "pull out the magic numbers, centralize them" sweep across the DLL (C++) and UI (C#),
driven by a multi-agent discovery workflow (17 parallel scanners → per-project synthesis; 63 DLL +
48 UI raw findings, deduped/ranked). Behavior-preserving refactor; full build + all 2120 tests green.

**Tier 1 — duplicated / tunable literals (commit `48315a5`).** DLL: reference the existing
`Grimoire::OFF_UOBJECT_CLASS` (Aura `LooksLikeUObject`) and `Grimoire::PIPE_NAME` (Heiter) instead of
re-hardcoding; new `Stark::kMin/MaxInvokeTimeoutMs` (100/600000) shared by the Stark clamp and Fern's
`set_invoke_timeout` validation; file-local `constexpr` for the Ubel per-request array cap (4096 ×8),
Serie FName chunk/len bounds (256/8192/1024), Aura value-scan defaults (100000/15000) + deep-walk
element cap (50000 ×3), Edel scan budgets (1024/16), Radar session idle-expiry (300s), Lugner_Dxgi
export count (20). UI: 12 new `Constants.cs` entries (GObjects walk page size, scan/query caps +
deadlines + page sizes, Stark invoke timeout, pivot/xref/instance caps, array/dropdown limits) +
reference the existing `DefaultPreviewLimit`/`ObjectTreePageSize`; new `Services/CeMailboxLayout.cs`
shared by all 5 CE Lua generators (canonical mailbox offsets/opcodes/timeout). **Test fix:** the
shared mailbox class normalized `InvokeScriptGenerator`'s zero-padded hex tokens (`0x010`) to the
compact `0x10` used by the other 4 generators (identical numeric address in Lua; 6 InvokeScriptTests
assertions updated) — chosen because it keeps the other four generators' emitted output byte-identical.

**Tier 2 — `IsUserspacePointer` helper (commit `22c2211`).** New `Grimoire::PTR_USERSPACE_MIN/MAX` +
inline `IsUserspacePointer()`. The pervasive x64 "is this a plausible userspace pointer?" guard was
hardcoded ~48× across Genau/Ubel/Aura/Serie/Macht/Wirbel. Converted only the **paired** range checks:
a Python backreference regex (`VAR < 0x10000 || VAR > 0x0*7FFFFFFFFFFF`, same variable both sides,
boundary-guarded) rewrote 43 reject-form sites to `!IsUserspacePointer(x)` (+ folded the two
split-`if` `LooksLikeHeapPtr`/`LooksLikeDataPtr`); 4 accept-form sites became named constants keeping
the strict `>`/`<`. **Critically left untouched** the standalone `0x10000` collisions (min module
size `Macht.cpp:535`, struct/element-size sanity caps, lone low-bound guards) — the pairing
requirement is exactly what makes `0x10000`'s ≥4 meanings safe to disambiguate. Neu::LooksLikePtr (an
equivalent peer helper) also left as-is.

**Deferred (see [todo.md](todo.md)).** Tier 2 remainder — object-count/size ceilings (`0x800000` 8M
object count; `0x100000` 1M, but split between container-element-COUNT and PropertiesSize-BYTES;
`[0x1000..0x400000]` window) and `MAX_CLASS_HIERARCHY_DEPTH=64` — carry genuine per-site multi-meaning
nuance and were left rather than risk conflating unrelated tunables. Tier 3 single-use knobs likewise.

## 2026-06-30 — CE export: flatten leaf records + record colors + collapse single-leaf pointers (build ~1856; MERGED PRs #401/#402, docs #403; in-game VERIFIED on SEED for flatten + colors)

**SHIPPED.** Three opt-in Live Walker → **Options** export features — **Copy CE XML / Copy CE
Field only** (CSX deliberately left nested), **no DLL change, no re-inject**. All **address-
equivalent**: a flattened / collapsed row resolves to the exact same memory as the nested form,
with the offsets folded into the row instead of spread across parent folders. Off by default,
persisted in `LiveWalkerUiOptions`. Origin: the SEED save-data session's three "case" asks plus a
colour follow-up. User recipe added to [tips.md](tips.md) (PR #403).

**Flatten leaf records (names/strings) — PR #401.** Superset of "Flatten primitive-leaf structs":
the flatten gate (new `IsTerminalLeafField` = primitives ∪ `NameProperty` ∪ FString family) now
accepts `FName` + `FString` leaves, so a record struct `{Score, Rank, MsID(FName), PilotName
(FString)}` collapses fully. `EmitFlattenedStruct` emits an FString child as a CE **String** leaf
(`Offsets=[0]`, one FString.Data deref) and a name child as a 4-byte int. `EmitMapProperty`
collapses the per-element `[i]` group when the value is a flattenable record → flat `[i] key ▸
Field` siblings at the combined offset (struct arrays / sets already flattened via `EmitFields`;
only `TMap` wrapped a group). **No field-count cap** — the all-terminal-leaf requirement is the
gate. Subsumes case #2 (`FDateTime` = a single `Ticks`). **In-game VERIFIED on SEED**
`StoryMissionRecord` (`TMap<FName, LifeStoryMissionRecord>`, 222 entries): flat rows, `PilotName`
as a readable CE String.

**Record Colors — PR #401.** Tints flattened container-element rows by element-index parity
(CE `<Color>`, a COLORREF written `BBGGRR` — RGB byte-swapped at emit) so records stay separable
once the `[i]` folder is gone. Even = `struct[0],[2],…`; Odd = `struct[1],[3],…`. `FlattenColorDialog`
(code-behind `ManagedDialogWindow`): neutral preset palette + hex + Reset + live preview; per-section
**Custom…** opens an in-app `ColorPickerDialog` (rainbow hue strip + R/G/B sliders + preview —
deliberately **not** the native Win32 picker, which would need a Core platform-abstraction P/Invoke
and clashes with the dark theme; reflection was never the blocker). Default on, Even = azure, Odd =
unset. Colours land only on flattened rows. **In-game VERIFIED on SEED.**

**Collapse single-leaf pointers — PR #402.** The deferred case #1 ("pointer to a string"): a drilled
pointer whose resolved target holds **exactly one** terminal leaf collapses to one `Pointer ▸ Field`
record via `EmitOneDerefLeaf` (scalar / `FName` → `Address=+ptrOff, Offsets=[childOff]`, 1 deref;
`FString` → `Offsets=[0, childOff]`, 2 derefs) instead of a folder + lone child. Gated in
`EmitDrilledPointer` after the dedup / cycle / depth guards (does not mark dedup or push the cycle
path — a leaf has no subtree); a multi-field pointee keeps its group. Default OFF. **Unit-tested
only** — no in-game pointer-to-string case has surfaced (SEED's `PilotName` is an inline struct
string, already record-flattened); low risk.

**Verification:** 2105 C# tests (+14 across the three features), 797 dll self-tests, Native-AOT
publish clean. All client / C#-only; the export math reuses the CE pointer model proven by the
record flatten. **Lesson:** CE `<Color>` is a Win32 COLORREF (`BBGGRR`), the reverse of RGB —
`0080FF` (azure) writes as `FF8000`.

## 2026-06-28 — Phase 1 REDO: discrete-style two-connection lane split (build ~1845; MERGED PR #396, in-game VERIFIED 1–5)

**SHIPPED.** In-game verified on Elliot (§9.6 items 1–5: connect / interactive-responsive-during-
scan / value-search-mid-snapshot / disconnect-mid-scan / CE invoke under load) and merged to main
(PR #396). Low-priority follow-up: watch-event delivery + single-lane-drop edge. Implements
[multipipe-eval.md](multipipe-eval.md) §9 (Path A),
modeled on the sister repo `D:\Github\discrete`. Fixes the reverted Phase 1's deadlock by giving
**each connection its own handle + its own thread** instead of one worker writing the read
thread's handle — so the synchronous pipe never has two threads on one handle, and **no
overlapped-I/O rewrite is needed**.

**Model.** The UI opens TWO connections (interactive + bulk) to a `maxInstances=3` server; the
DLL serves each on its own detached thread, serial read→dispatch→write on its own handle. The
DLL is **lane-agnostic** (no command classification) — the UI's `LaneRoutingPipeClient` routes
by a `BulkCommands` set (heavy/scan/snapshot → bulk; everything else → interactive). Safe because
the interactive lane never builds the Aura `s_classContainerCache`/`s_classRefCache` and the bulk
lane runs scans one-at-a-time (its own serial connection).

**DLL (`Fern`) — per-connection refactor:** registry of `Connection{pipe, writeMutex, inFlight,
closed}`; thread-per-connection `AcceptLoop`; per-connection `WriteLine`; watches keyed to their
owning connection (old global `PushEvent` removed); monitor iterates connections + trips global
`Tot` on a broken in-flight pipe; per-command cancel reset moved to first-connection-of-session;
`Stop` uses `CancelIoEx` + bounded drain (each thread closes its own handle). **UI:**
`LaneRoutingPipeClient` decorator + `PipeClient` lane tag + `PipeLogEntry.Lane` column.

**Verification so far:** builds (DLL + UI), full suite green (2091 C# / 797 dll / 53 utf8),
Native-AOT publish clean, exe launches + stays alive. **STILL REQUIRED before merge: the §9.6
in-game checklist** (Fern has no unit tests; the last attempt's deadlock only showed in-game).
**Lessons baked in:** separate handle per connection (one thread per handle) is what makes the
synchronous pipe safe; close from the owning thread (`CancelIoEx` to unblock) not cross-thread.

## 2026-06-28 — REVERT Phase 0 + Phase 1 (in-game regressions); keep Pipe Activity log (build ~1841; restores DLL baseline)

**In-game test on Elliot regressed badly** — reverted both phases to the pre-change DLL
baseline (`git checkout 1b7d149 -- Aura.cpp Fern.cpp Fern.h`). Root causes (DLL pipe log +
screenshots; full writeup [multipipe-eval.md](multipipe-eval.md) §8):

1. **Phase 1 deadlocks on the synchronous pipe handle.** The server pipe is created **without
   `FILE_FLAG_OVERLAPPED`** (Fern.cpp:496) → the Windows I/O manager serializes I/O on the
   handle, so the heavy worker's `WriteFile` (response) **cannot complete while the read thread
   is parked in `ReadFile`** waiting for the next command. First connect: `trigger_scan #5`
   handler ran (`RunScan finished`) but its response never reached the UI (nothing else was
   sent to release the read) → UI hung until manual disconnect. Second connect "worked" only
   because continuous user traffic kept flushing the blocked write. **A correct off-thread
   dispatch needs overlapped/async pipe I/O — a real rework, deferred.**
2. **Phase 0 priority drop starves scans ~20×.** With the game saturating cores,
   `THREAD_PRIORITY_BELOW_NORMAL` scan threads barely run → Snapshot crawled to **2% in 57 s
   (~40 min ETA)** vs the normal 1–2 min. The pre-existing `cores − 2` *count* headroom was the
   correct throttle; dropping *priority* on top turned it into "run only when the game is idle".
3. (Also noted) even without the deadlock, Phase 1 wouldn't have fixed Snapshot much: the
   shared `m_writeMutex` re-serializes the multi-MB chunk write, and the **UI-side
   `ReadLoopAsync` parses each multi-MB chunk single-threaded (~2.4 s)**, blocking interleaved
   light responses. Real bottlenecks = scan starvation [now reverted] + UI chunk parse, not DLL
   dispatch.

**Kept:** the System-tab Pipe Activity log (UI-only, unrelated to the deadlock — it's what made
the missing `←` reply obvious). Cap raised 100→**200** per request; newest-first (newest always
visible at top). **Lesson:** never run a `WriteFile` from a second thread while the read thread
is blocked in `ReadFile` on a non-overlapped handle — the OS serializes them. Verify pipe
concurrency changes IN-GAME, not just by unit tests (Fern has none).

## 2026-06-28 — System-tab "Pipe Activity" log (live UI↔DLL traffic tail) (build ~1837; UI-only; 2091 C# green, AOT publish clean + launch-verified)

**Context.** Companion to Phase 1: a small in-UI tail of pipe traffic on the System tab so
the user can *see* light commands interleave with a heavy scan (proof the lane split works)
without opening the `%LOCALAPPDATA%` pipe log. The user noted the file log already exists, so
this is a convenience mirror — its value is immediacy.

**Change.** `IPipeClient` gains an `Activity` event raised for every line: TX (on send, with
command + id), RX (on response, paired back to the TX for command name + round-trip ms via a
new `_txMeta` id→(cmd,tick) map), and push events. `PipeClient` raises it only when a
subscriber is attached (near-zero cost otherwise) and clears `_txMeta` on cancel/disconnect.
The System-tab VM (`PointerPanelViewModel`) subscribes and renders a newest-first ring buffer
(`ObservableCollection<PipeLogEntry>`, cap 100) with **Pause** + **Clear**. Pipe-thread
callbacks enqueue into a `ConcurrentQueue` and **coalesce a single `Dispatcher.UIThread.Post`
per burst at `DispatcherPriority.Background`**, so a snapshot streaming hundreds of chunks
can't flood the UI thread. New `PipeLogEntry` model; a "Pipe Activity" card in `PointerPanel.axaml`
(monospace list + empty placeholder); en.axaml strings. Test mocks (`MockPipeClient`,
`NoopPipeClient`) implement the new event.

**Verification.** Full suite green (2091 C# / 797 dll / 53 utf8); Native-AOT publish clean (no
new ILC/trim warnings) and the published exe launches + stays alive with no `crash.log`.

## 2026-06-28 — Multi-pipe Phase 1: non-blocking DLL dispatch (heavy worker lane) (build ~1836; DLL-only Fern; 2091 C# / 797 dll / 53 utf8 green)

**Context.** Phase 0 (below) protected CE responsiveness but left the actual UI symptom —
Live Walker slow during a Snapshot — untouched, because the DLL command loop was still
strictly serial (a `snapshot_chunk` blocked the read thread so a queued `walk_instance`
waited in the pipe buffer). Phase 1 fixes that. Design + rationale: [multipipe-eval.md](multipipe-eval.md) §5–§6.

**Change (Fern only — no protocol/UI change).** `HandleClient` no longer runs every command
inline. It now routes by lane:
- **Light** (`IsLightCommand` allowlist — get_object/list, walk_class/instance,
  read_array_elements, query_candidates, teleport_*, god/movement/cursor, watch, …): run
  **inline** on the read thread, exactly as before.
- **Heavy** (everything else — value/group scan, snapshot_chunk, find_instances/refs/path,
  find_by_address, search_properties, list_*, invoke_function, version/packed/apply-rescan
  mutators, …): posted to a **single FIFO worker thread** (`HeavyWorkerLoop`). The read loop
  returns immediately to service light commands. The worker runs `DispatchCommand` (which
  bakes the request id into the response) and writes the id-tagged response via the
  `m_writeMutex`-guarded `WriteLine`, so it interleaves safely with the read thread's light
  responses and async `PushEvent`s. The UI already demultiplexes by id, so out-of-order
  completion needs no client change.

**Concurrency-1 by design.** The heavy worker is a single FIFO thread, so two cache-building
scans never run at once → the Aura `s_classContainerCache`/`s_classRefCache` (and GObjects
drift) are never raced across commands. Light commands only read write-once globals + Ubel's
mutex-guarded caches + their own session, so a light command running concurrently with one
heavy job is safe. The allowlist is the *only* light set; anything unlisted defaults to heavy
(safe default — a new/unclassified command can't accidentally race a scan).

**Cancellation simplified.** `Tot::ResetPerCommand` moved from per-command to **per-client**
(at `HandleClient` start) — `g_perCommand` is only ever set on disconnect (Fern monitor), so
per-client reset is equivalent, and it removes the risk of a light command clearing a running
scan's cancel mid-flight. On disconnect the read loop drains the worker: `RequestPerCommand`
(running scan bails at its next Tot poll) + clear the queue + **wait for the worker idle**
before returning, so AcceptLoop's pipe-close + session `DropAll` can't race a live job.
`Fern::Start/Stop` spawn/join the worker (Stop's existing `RequestShutdown` makes an in-flight
job bail fast).

**Status.** Built + full suite green. Fern is integration-level (not unit-tested), so the
threading needs **in-game verification**: confirm Live Walker / teleport stay responsive while
a Snapshot / Value Search runs, and that disconnect mid-scan frees the pipe cleanly.

## 2026-06-28 — Multi-pipe IPC evaluation + Phase 0 scan CPU/priority guard (build ~1834; DLL-only; 2091 C# / 797 dll / 53 utf8 green)

**Context.** User asked whether the UI↔DLL and CE-Lua↔DLL IPC should use **multiple
pipes** — Live Walker feels slow during a Snapshot (suspected pipe queuing), and the CE
`.CT` invoke mailbox should stay responsive while the UI does heavy work. Investigated via
a multi-agent workflow (5 parallel readers over Fern/PipeClient/Mimic+Stark/command
taxonomy/shared-state safety + synthesis + adversarial verify).

**Findings (full writeup: [multipipe-eval.md](multipipe-eval.md)).**
- There are **already two independent channels**: the UI **named pipe** (Fern) and the CE
  **shared-memory mailbox** (Mimic, `g_invokeMailbox`, polled on its own thread → Stark
  game-thread queue). CE never touches the pipe.
- Root cause of the UI lag is **DLL-side head-of-line blocking**: one pipe instance
  (`CreateNamedPipeW maxInstances=1`) + a strictly serial `HandleClient` loop
  (`ReadLine → DispatchCommand[blocks] → WriteLine`). A `snapshot_chunk` (~0.5–2 s) blocks
  the read loop so a queued `walk_instance` waits in the pipe buffer. **Not** the UI
  `_writeLock` (released before the response await — the client already `id`-multiplexes),
  **not** CE.
- **Multi-pipe is the wrong fix.** N pipe instances force two `DispatchCommand` to run
  concurrently, which is **unsafe today** — `s_classContainerCache`/`s_classRefCache` use a
  check-without-lock → build-outside-lock (`Ubel::WalkClassEx`) → insert-under-lock pattern
  that races, and GObjects has no epoch stamp. Recommended instead: **single pipe +
  non-blocking dispatch** (light inline + one heavy worker, concurrency = 1), deferred to
  Phase 1 (needs per-request cancellation — `Tot::ResetPerCommand` is currently global).
- CE responsiveness: pure-memory CE ops (GodMode/Movement/read-only teleport) are already
  immune; only **game-thread-routed** CE invokes are at risk, and only from **CPU
  starvation** during parallel scans — a pipe-count-independent problem.

**Phase 0 shipped (DLL, `Aura.cpp`).** `ScanThreadCount` already capped workers at
`hardware_concurrency() − 2`; added a **thread-priority guard** so a full-pool scan goes
"nice": spawned workers in `ParallelIndexRanges` set `THREAD_PRIORITY_BELOW_NORMAL`, and a
new RAII `ScopedScanPriority` drops the calling thread (which runs chunk 0 inline) for the
duration of a *parallel* scan, restoring on scope exit (incl. the throwing-chunk path).
Serial scans (tiny arrays / anti-tamper "parallel off") are untouched — they leave cores
free anyway. The cancel-watcher stays normal priority so cancellation stays snappy. Net:
Value Search / Group Scan / Snapshot capture no longer competes with the game thread, so CE
`.CT` ProcessEvent invokes keep draining (far less likely to hit the invoke timeout) while
the UI is busy. **The serial UI dispatch is unchanged — Live-Walker-during-Snapshot lag is
Phase 1's job.** *(Built + full test suite green; not yet in-game profiled.)*

## 2026-06-27 — DataGrid horizontal-overflow sweep (all panels) + "Diff is always deep" annotation (build ~1832; UI/XAML-only; no test delta)

**Context.** Two follow-ups after the top-level scalar-array capture fix below. (1) The user
found that dragging a column splitter in the Snapshot result grids could not widen a column
past the window edge — the grid clamped to the UI width with no horizontal scrollbar — and
noted "this was fixed long ago" (the LiveWalker FieldGrid, commit `e1b4d46`) but never went
app-wide. (2) The single-value Snapshot Diff has no "Deep" toggle while Group mode does,
which read as an inconsistency.

**Root cause (DataGrid, adversarially verified).** Any `Width="*"` (star) column makes an
Avalonia DataGrid fit total width to the viewport — the star column absorbs every splitter
drag, so the grid can never overflow and no scrollbar appears. `HorizontalScrollBarVisibility`
**already defaults to `Auto`** (InstanceFinder InstancesGrid scrolls with no explicit attr),
so the missing attribute is NOT the cause — the star column is. The fix is the proven
`e1b4d46` pattern: all columns fixed `Width` + `MinWidth`, no star, plus an explicit
`HorizontalScrollBarVisibility="Auto"` for self-documentation.

**Change.** App-wide sweep — **17 DataGrids across 8 panels** converted (Snapshot ×4, SPC ×5
incl. both snapshot pickers, ClassPivot ×3, RelatedObjects, InstanceFinder ContainerMatches,
LiveWalker References, ValueSearch GroupResultsGrid, ProxyDeploy). Deliberately left alone:
`MainWindow` `<ColumnDefinition Width="*"/>` (tab-host layout, correct) and the two
intentionally non-scroll surfaces (Console ScrollViewer + ClassPivot class-list ListBox, both
`HorizontalScrollBarVisibility="Disabled"`). Every `SortMemberPath` preserved (compiled
bindings need it or nothing sorts). **Tradeoff (same as `e1b4d46`):** fixed columns no longer
auto-stretch to fill a wide window → right-edge dead space; in exchange the grid overflows +
scrolls horizontally on demand.

**Diff "always deep" annotation.** Confirmed (workflow + verify) that the Snapshot **Diff is
already always-deep** — `DiffSnapshotsAsync` applies no `array_field` filter, so nested
array / struct-array element changes (e.g. `SupportActionGauge[3]`, shown as `Array[N]`)
always appear. "Deep" is a QUERY-time toggle that exists **only in Group mode** (gates array
elements in as group-match slots); a shared "global Deep" would be misleading (Diff =
always-deep, Group = opt-in-shallow → opposite defaults). Per the user's choice: no Diff Deep
checkbox; instead an italic **"Diff is always deep"** note (+ tooltip) sits next to **Run
Diff** so users don't hunt for a missing switch.

**Verify.** UI build clean (compiled-binding XAML validates at build); published
`dist\UE5DumpUI.exe` launch-verified (alive, responding, no `crash.log`). XAML-only — no DLL
re-inject, no test delta.

-----

## 2026-06-27 — Snapshot now captures gameplay classes' TOP-LEVEL scalar arrays (e.g. a Pawn's SupportActionGauge[] TArray<float>) — Diff/SPC/Pivot could not see them before (build 1827; DLL; in-game VERIFIED on Elliot)

**Symptom (Elliot, user-reported).** A Live Walker value — element `[3]` of
`BPPlayerCharacter_C.SupportActionGauge` (`TArray<float>`, 7 floats) — dropped ~89→83, but the
Snapshot **Diff** showed nothing. Value Search finds the same value fine.

**Root cause.** `Aura::CaptureStructArrays`'s walker deliberately skipped **top-level
leaf-containers** — a scalar `TArray<int>/<float>` directly on an object (`leafName==""`,
`depth==1`) — via `if (lf.leafName.empty() && lf.depth < 2) return;` (build 1204), because
capturing every object's scalar arrays would balloon the DB. So `SupportActionGauge[]` was
never stored → Diff/SPC/Pivot had nothing to compare. (Nested leaf-containers `Tunes[N]`,
depth≥2, and struct-array element fields were always captured.) This is a CAPTURE-time gap, not
a query-time one — the Diff is already always-deep and the Group "Deep" toggle can't surface
rows that were never written. **Live Value Search is unaffected** — its Phase 2C Array scan
(`f.TypeName=="ArrayProperty"` + matching innerType → `ScanContainer::Array`) walks the TArray
buffer directly.

**Fix (per user choice "only gameplay classes, by default").** New thread-local-memoized
`IsSnapshotGameplayClass(cls)` (reuses `ClassDerivesFromAny(cls, SnapshotGameplayKeepBases())`
= Actor/ActorComponent/Pawn/Character/Controller/PlayerState/GameInstance) gates a new
`captureTopLevelScalarArrays` param on `CaptureStructArrays`. Gameplay classes (the value
carriers) now capture their top-level numeric scalar arrays; engine/system classes still skip
them, so the DB stays bounded. **No schema change** — rows reuse the existing
`array_field`/`elem_index` columns with `inner_prop_name=""` (the same leaf-container path
`Tunes[N]` already proved end-to-end: Fern encode → SnapshotStore INSERT → Diff display
`Array[N]`).

**Verify.** DLL build clean; 797 dll + 2091 C# tests green; **in-game VERIFIED on Elliot** —
re-injected DLL + fresh snapshots, Diff now shows `SupportActionGauge[3]` 89→83. (Pre-1827
snapshots lack these rows — must re-capture.)

-----

## 2026-06-27 — Snapshot consistency guard: detect a GObjects-count drift mid-capture → mark UNUSABLE + auto-delete before next capture (build ~1825; UI/C#-only; tests +8)

**Context.** A user asked whether the slow/huge `48–61 MB`-chunk snapshot they saw was *GC
corruption*. Investigation (DLL `Aura::CaptureSnapshotChunk` + UI capture loop + SQLite
lifecycle) found **No**: the big chunks are legitimately data-heavy objects, and the run's
every chunk reported a stable `total:390017` (no churn). The "~10 min" was a misread — the
3 captures took ~82 s each; the 9-min gap was Class-Pivot work. BUT the probe surfaced a real
latent bug: the paging loop stops on the *per-chunk* `total` with **no drift detection**, so a
capture spanning a level transition / mass spawn-free is stored as "valid" while being a
temporally-inconsistent mix — and snapshots are consumed OFFLINE by SPC Query / Class Pivot.

**Feature (the user-requested safeguard, with the corrected trigger).**
- **Detect** (`Services/SnapshotConsistency.cs`, pure): track the min/max GObjects count each
  chunk reports; `IsDriftSuspect` flags a span beyond `max(2000, 2% of begin)` — generous so
  ordinary gameplay churn never false-flags a good capture (a false positive would auto-delete
  good offline data).
- **Early-abort + mark unusable**: on drift the capture loop stops early and finalizes the
  partial with `is_usable=0`.
- **Schema** (`SnapshotStore`): `is_usable` added **additively** (PRAGMA-guarded `ALTER`, no
  `SchemaVersion` bump) so existing snapshot DBs are preserved, not wiped.
- **Auto-clean**: `DeleteUnusableSnapshotsAsync` runs at the START of each capture (deferred so
  the user sees the ⚠ flag in the list until then).
- **Offline consumers honour it**: SPC + Class Pivot pickers exclude `is_usable=0`; Diff
  auto-select skips them; the saved-snapshots grid + pickers show a ⚠ prefix
  (`SnapshotMeta.LabelDisplay`/`PickerDisplay`/`UsabilityBadge`).

**Tests.** `SnapshotConsistencyTests` (7) + a store round-trip/`DeleteUnusable` test → C#
2091/0; dll 797/0; published UI launch-verified (additive migration safe on startup). No DLL
change (count already in the chunk RX) → no re-inject for this feature.

-----

## 2026-06-27 — Pipe-block fix: stale/recycled Live Walker object with a garbage PropertiesSize no longer wedges the single-threaded pipe (build ~1824; DLL + UI; tests +6 dll)

**Symptom (Elliot, user-reported).** Did Snapshot → Class Pivot → multi-value diff (~10 min),
switched **back to Live Walker**, and the UI pipe appeared **blocked** — no further command
returned until the app was force-closed.

**Root cause (log-proven, three logs cross-checked).** The instance the Live Walker had
selected (`0x376182070`) was freed/GC'd during the long Pivot pass and its memory slot reused.
On return, the panel re-walked the **stale address**:
1. Shallow `walk_instance` (id=375) read a recycled UClass pointer whose name was empty and
   whose `PropertiesSize` decoded as garbage **867763776 (~827 MB)** — yet returned `ok:true`,
   0 fields, `props_size` huge.
2. UI `AutoFillGapsRetryAsync` saw *0 fields + propsSize>0* and **auto-fired `fill_gaps=true`**
   (id=376) — with no upper bound.
3. DLL `Ubel::WalkInstance` computed **one gap `[0, 0x33B90640)`** = the whole 827 MB and called
   `GuessGapTypes` over it. For a gap >64 KB it abandons the bulk read and does a **per-position
   SEH read, advancing 1 byte per fault** over mostly-unmapped memory → ~**8×10⁸ SEH faults** =
   effective hang. The loop had **no `Tot::Requested()` check**, so even the disconnect monitor
   couldn't free it. The single-threaded pipe queued every later command (the user's `walk_world`
   id=377 never ran) → UI frozen.

**Fix (4 layers).**
- **DLL gate** (`Ubel::WalkInstance`): new pure `Ubel::IsSanePropertiesSize` (≤ `kMaxSanePropertiesSize`
  = 1 MB). Implausible `PropertiesSize` → flag `isStale`, zero `propsSize`, **return before the
  gap walk**.
- **DLL gap-fill cap** (`Ubel.cpp` fill-gaps block): explicit `PropertiesSize <= kMaxSanePropertiesSize`
  guard (belt-and-suspenders with the gate).
- **DLL `GuessGapTypes`**: hard `gapLen > kMaxSanePropertiesSize` refusal (protects *every* caller,
  incl. the Native-C scan) **+ a throttled `Tot::Requested()` poll** so a wide gap stays abortable
  on disconnect/shutdown.
- **UI** (`AutoFillGapsRetryAsync`): skip the auto-retry when `IsStale` or `PropertiesSize` exceeds
  1 MB; `UpdateDisplay` surfaces a "object freed/recycled — re-open from 🌍 GWorld/finder" status
  instead of a silently-blank grid. New `stale` field plumbed Fern → `InstanceWalkResult`.

**Tests.** `Test_IsSanePropertiesSize` (6 EXPECTs incl. the 827 MB value) → dll_helpers 797/0;
C# 2083/0; published UI launch-verified (alive 7 s, no crash.log). In-game re-verify on Elliot
pending (repeat the Snapshot/Pivot → Live Walker round-trip).

-----

## 2026-06-27 — Build fix: DLL/proxy PE VERSIONINFO now tracks build_number again on incremental builds (build ~1822; CMake only)

**Symptom.** After the incremental-Ninja build dir became persistent (build 1817), the
DLL's embedded PE version froze at **1.0.0.1817** while the UI kept advancing (1818, 1819,
1820…). Releases shipped a DLL whose FileVersion/ProductVersion/GitCommit didn't match the UI.

**Root cause.** `version.rc` embeds the version via `#include "BuildInfo.h"` (regenerated by
`configure_file` every configure — build number, git hash, timestamp). Ninja scans `.cpp`
for header includes via `/showIncludes` depfiles, but **does NOT scan `.rc` files** (the RC
compile rule emits no depfile). So a changed `BuildInfo.h` recompiled the `.cpp` TUs that
include it (logs stayed current) and relinked the DLL — but reused the STALE `version.res`,
freezing the PE version at the first kept-build. A full `-Clean` masked it (CI always cleans);
only incremental local builds drifted.

**Fix** (`dll/CMakeLists.txt`): `set_source_files_properties(src/version.rc PROPERTIES
OBJECT_DEPENDS "<gen>/BuildInfo.h")` — adds the missing dependency edge so the resource
recompiles whenever `BuildInfo.h` changes. Directory-scoped → applies to the main DLL and all
three proxies. Verified: main DLL, UI, and version/dinput8/dxgi proxies all stamp the same
`1.0.0.<build>` after a build (was DLL frozen at 1817, UI at 1820).

-----

## 2026-06-27 — Copy CE XML/Field: "Flatten primitive-leaf structs" opt-in (generalizes GAS flatten) + Elliot version doc fix to UE5.4 (build ~1820; UI/C#-only — no DLL change; tests +6)

Two independent C#/docs changes, no DLL touch → no re-inject.

### Flatten primitive-leaf structs (CeXmlExportService)
The GAS-attribute flatten (build ~1799) collapsed a `FGameplayAttributeData` struct one
level — `BaseValue`/`CurrentValue` promoted to `HealthPoint ▸ BaseValue` sibling leaves at
the combined offset. Its trigger was the **struct TYPE name** (`GameplayAttributeData`,
F-prefix accepted) plus an all-children-scalar gate — NOT the owning class name and NOT the
field name. New **"Flatten primitive-leaf structs"** opt-in (Live Walker export Options menu,
default off) generalizes that to ANY terminal struct: a struct flattens when its entire
already-flattened subtree is **primitive inline scalars** (`IsPrimitiveLeafField`:
float/double, int8–64, byte/uint16–64, bool, enum + the Guess scalar labels). A pointer/object,
string (`FString`/`FName`/`FText` — pointer-backed), container (Array/Map/Set/Optional), or
unresolved nested struct keeps the struct grouped ("pointers stay unflattened"). It's a
**superset** of the GAS flatten and naturally flattens `FVector`/`FRotator`/`FTransform`
(pure-float structs); a Vector reached through a pointer is never on this path. Both toggles
fire the SAME promotion (`EmitFlattenedStruct`, renamed from `EmitFlattenedGasStruct`).
**Copy CE XML / Copy CE Field only — CSX is deliberately unaffected.** Wired through
LiveWalkerViewModel (4 export call sites) + UiOptionsSettings persist. Tests +6.

### Elliot = UE5.4 (doc reconciliation)
The Adventures of Elliot's PE has its version string stripped → publisher-fallback detects
UE4.27, but runtime markers reconcile it upward to **UE5.4** (tagged FFieldVariant → 5.3 floor,
`CMC::GravityDirection` → 5.4; build ~1808). Updated the user-facing current-state docs to
match: both README version matrices (moved Elliot 4.25–4.27 → 5.3–5.4 with a reconciliation
note), the README `dxgi.dll` proxy notes, and `test-games.md` (version cell + naming
convention). Build-stamped history (dev-log build 1481/1799, lessons-learned, roadmap
blockquote) left as-is — at those builds it genuinely detected as 4.27.

-----

## 2026-06-27 — Build-system perf: shared OBJECT library + unified build dir + incremental Ninja (build 1817; build tooling only; 2077 C# green)

The four DLLs (UE5Dumper.dll + the version/dinput8/dxgi proxies) previously each
got their own clean CMake configure in a separate build dir, so every `build`
wiped the tree and recompiled Zydis + all ~20 sources up to 4× — Aura.cpp et al.
were rebuilt from scratch on every run. Restructured for incremental builds while
keeping correctness and CI behaviour identical.

### What changed
- **Shared `UE5DumperCommon` OBJECT library** (`dll/CMakeLists.txt`): the 17
  proxy-agnostic sources (Aura/Fern/Frieren/Macht/… — none test a `UE5_PROXY_*`
  macro) compile ONCE; all four DLLs link the same objects. This is the
  CMake-correct form of "share the .obj across DLLs". Only Heiter (gated on
  `UE5_PROXY_BUILD`) + the per-proxy Lugner shims compile per target. OBJECT (not
  STATIC) is deliberate — every object is force-linked, so the dllexported C-ABI
  symbols (Frieren) are never dropped.
- **Single unified build dir** (`build.ps1`): one `cmake` configure (all proxy
  options ON) + one Ninja build produces all four DLLs → Zydis/minhook compile
  ONCE (was 4×); a full `Publish -Clean -Target All` C++ build went from ~188
  compile steps across 4 configures to **69 in one**. Legacy `build_proxy*` dirs
  are removed automatically.
- **Incremental by default**: the unconditional `Remove-Item $BUILD_DIR` is gone;
  only `-Clean` wipes. `build.ps1` still re-runs configure every build so
  `BuildInfo.h` (build number / git / timestamp) stays fresh — that was the real
  cause of the old "Ninja used stale data" perception (it was version metadata,
  not code; Ninja tracks header/source changes correctly).
- **`BuildStamp.h/.cpp`** (new leaf util): the volatile build macros now live in
  one tiny TU behind accessors (`BuildStamp::VersionString()` etc.); Fern /
  Frieren / Sein / Heiter call those instead of including `BuildInfo.h`. A
  build-number bump now recompiles only `BuildStamp.cpp` + `version.rc`, not the
  heavy Frieren/Fern. `version.rc` still uses the macros directly (RC needs them).

### Verified (all real builds on this machine)
- Clean main DLL 1:55; **no-change rebuild 8s** (was a full rebuild);
  touch Aura.cpp → 20s (Aura recompiles → source changes are always reflected).
- dxgi proxy MASM thunks isolated + assembled; all `UE5_*` exports intact.
- **CI path `Publish -Clean -Target All` green in 4:30** — all four DLLs in one
  69-step Ninja build + AOT UI + utf8/dll self-tests + 2077 C# tests pass.

### CI / correctness
CI is unchanged: `release.yml` already passes `-Clean` on a fresh runner, so it
still does a full from-scratch build (if a fast incremental ever went wrong, CI
would still catch it). The object-library + unified-dir changes are
correctness-preserving (CMake owns the sharing + dependency tracking), so CI also
builds faster for free. `BuildStamp` kept English (generic leaf utility — same
exception as `Utf8Helpers.h` / `GraphPath.h`).

-----

## 2026-06-27 — UE-version markers extended to 5.6 / 5.7 (build ~1810; DLL only; 2077 green)

Extends the version-reconciliation chain with the two remaining structural markers
already in the codebase (both reliable, distinct from Avowed's custom packing):
- **UE5.7 reordered FUObjectItem** (`Object*`@+0x08, 24-byte item — the stock-UE5.7
  Solarpunk repro): `Aura::GetItemObjOffset()==0x08 && GetItemSize()==24` → version floor
  **507** in the init reconciliation. The `==24` guard excludes Avowed's CUSTOM 20-byte
  packed layout (UE5.3, NOT a version signal); the "unverified" packed-reconstruction path
  is never seen in a real game.
- **UE5.6+ `FNameData` UEnum::Names container** (the struct-of-arrays whose enum bug the
  `Neu` reader fixed; e.g. Titan Quest II): `DynOff::bEnumNamesNewContainer` → **506**,
  added to the lazy `UE5_GetVersion` refine (the flag is set lazily by `DetectUEnumNames`,
  so it surfaces as the user browses, alongside the UE5.5 Utf8Str marker).

Full marker chain now: tagged FFieldVariant (5.3) → GravityDirection (5.4) → Utf8Str/Ansi
(5.5, lazy) → FNameData enum (5.6, lazy) → Object@+0x08 24B item (5.7, init). All raise-only.

-----

## 2026-06-27 — Movement Locate-in-GWorld coverage + Gravity Direction (UE5.4+) + camera nested-struct drill + UE-version upward reconciliation (build ~1808; DLL + UI; 2077 C# green; in-game VERIFIED on Elliot)

Follow-on to the Laufen movement work — Locate-in-GWorld for every pose/POV field,
the gravity-DIRECTION vector, two UX fixes, and honest UE-version detection.

### Locate-in-GWorld — full pose/POV field coverage
- Current Pose: added **Acceleration** (CMC.Acceleration), **Speed** (no own field →
  lands on Velocity), and **Rotation** (Controller.ControlRotation) locators.
- Camera POV: added **Location**, **Rotation**, **FOV** locators
  (APlayerCameraManager.CameraCachePrivate.POV.*, nested offsets resolved by reflection).
- All Locate button labels unified to the compact **"🌍 Locate"** / "⚙ Locate" style
  (Teleport tab + Instance Finder full-text); DataGrid icon-only buttons unchanged.

### Gravity Direction (Laufen, UE5.4+)
- New `Laufen::SetGravityDirection`/`ResetGravityDirection`/`GetGravityDirection` (FVector
  `GravityDirection`, normalized DLL-side, base-capture + re-assert worker). `UE5_SetGravityDirection`
  export; Mimic `CMD_MOVEMENT` **knobId=3** (3 doubles, (0,0,0)=off). Pipe set/reset +
  gravity_direction in get_movement_params.
- UI card: 3 **linear** X/Y/Z sliders (−100%…+100%, normalized), Apply/Reset/Refresh/Locate,
  state badge, global "Gravity Dir toggle" hotkey, CE-Lua/.CT export row. Graceful
  "Unavailable" on pre-5.4. Hint clarifies normalization (single-axis collapses to ±1 =
  direction only, not strength).
- Hotkey Settings: per-row tooltips for all 22 actions; Move Speed/Gravity toggle hotkeys.

### Camera Locate — nested-struct drill fix
The POV fields are nested two struct levels deep, so the locate landed on the
PlayerCameraManager parent. Fix: DLL sends the full drillable path
`CameraCachePrivate.POV.Location`; Live Walker `TryParseContainerPath` gained
`requireIndex:false` so a pure nested-struct path also drives `DrillDisplayPathAsync`
(its index<0 branch already drills struct fields). Now lands on the leaf.

### UE-version upward reconciliation (Genau/Frieren + Ubel)
Heavily-stripped games (Elliot) lose every version string → fall back to 4.27 despite
being UE5. Added a runtime post-process in `UE5_Init` (self-correcting every launch, no
cache delete needed): **tagged FFieldVariant** (structural, UE5.3+) → floor 503;
**CMC::GravityDirection** (property marker) → 504. Plus a **lazy** UE5.5 marker — `Ubel`
sets a flag when any walk sees a reflected `Utf8StrProperty`/`AnsiStrProperty`, and
`UE5_GetVersion` raises 504→505 off it. Elliot now reports UE5.4. (Packed FUObjectItem is
NOT a version marker — Avowed packs it at UE5.3.)

Tests: +5 (TeleportViewModel gravity-dir ×3, MovementScriptGenerator gravity-dir ×2,
DeepContainerChain struct-path ×1) → 2077 C# green.
