# AOB corpus preservation — what to keep, what to reinstall, what to drop

> **Read this before deleting anything under `$GHIDRA_PROJS` (the Ghidra corpus root),
> `D:\tmp\Game archive`, `X:\UE_Analyze_Data`, or before uninstalling a corpus Steam title.**
>
> Companion to [tools/ghidra/GROUND-TRUTH.md](../tools/ghidra/GROUND-TRUTH.md) (which patterns
> are proven by which binary) and [tools/ghidra/sweep.sh](../tools/ghidra/sweep.sh) (the corpus
> list itself, and the source of truth for it).

> ### ⚠ THE PATHS IN THIS DOCUMENT ARE ONE MACHINE'S — not a property of this repo
>
> The corpus root does **not** follow a clone. Every tool resolves it from the `GHIDRA_PROJS`
> environment variable — [`sweep.sh:19`](../tools/ghidra/sweep.sh), [`preflight.py:797`](../tools/ghidra/preflight.py),
> [`build_corpus_manifest.py:423`](../tools/ghidra/build_corpus_manifest.py),
> [`run_headless_export.py`](../tools/ghidra/run_headless_export.py),
> [`run_all_aob_export.py`](../tools/ghidra/run_all_aob_export.py) — falling back to
> `D:\Tools\GHIDRA_Projs` only because that is where the machine which wrote these lines kept it.
>
> **Current location:** `D:\Tools\GHIDRA_Projs` (internal NVMe) — 63 `.rep`, 182.3 GB, verified
> 2026-08-01 (all 57 `sweep.sh`-referenced projects present).
>
> Set `GHIDRA_PROJS` before running anything here, and read every literal `D:\Tools\GHIDRA_Projs`
> below as "wherever yours is". A path written on a different machine is the single most common
> reason a documented command fails for the next person — run `py tools/ghidra/preflight.py`
> first, which reports what it actually found rather than what this file assumes.

> ### 🔥 DO NOT HOST THE CORPUS ON REMOVABLE / USB MEDIA — measured 2026-08-01
>
> The corpus lived on an external USB SSD (Plextor EX1) for a while. A sweep run at
> `SWEEP_JOBS=16 SWEEP_XMX=2G` knocked the drive **off the bus mid-write**. RAM was never the
> constraint — 16 × 2 GB = 32 GB on a 61.6 GB machine. The storage TRANSPORT was.
>
> ```
> disk  / Event 51  ×12+   paging-operation error on \Device\Harddisk2\DR2
> Ntfs  / Event 140        cannot flush the transaction log; "corruption may have occurred, VolumeId: E:"
> Ntfs  / Event 50         delayed-write FAILED on \$Mft — "data has been lost"
> Ntfs  / Event 50         delayed-write FAILED on GHIDRA_Projs\<proj>\idata\00\~00000000.db\tmp…
> volume re-enumerated     HarddiskVolume9 → HarddiskVolume12  (surprise removal + re-attach)
> ```
>
> **Lost `$Mft` writes with a Ghidra project mid-write** is precisely the shape that silently
> corrupts a `.rep`. The Ghidra processes then wedged in unkillable kernel I/O waits —
> `Stop-Process` reported "no such process" while WMI still listed them, and enumerating them
> hung. Only a reboot cleared it.
>
> This corpus survived (verified above). It did not have to. Three rules follow:
>
> 1. **Internal storage only.** §0 says a `.rep` is the artifact of record; putting the artifact of
>    record behind a connector that can drop under load is not a trade-off, it is a bug.
> 2. **If it must be on USB, cap `SWEEP_JOBS` at 2–3.** Not for speed — the full sweep is
>    ~4m38s (`GROUND-TRUTH.md`), so parallelism beyond that buys seconds and risks the corpus.
> 3. **After ANY surprise disconnect, verify before trusting.** "The volume reports Healthy" is a
>    statement about the *re-mounted* volume, not about the data. Run `preflight.py`, and check
>    that every `sweep.sh` project still has a `.rep` on disk.

Every number in this document is **measured**, not estimated, and carries the date it was
measured. Free space and archive sizes move; re-measure with `preflight.py --sizes` before acting.

-----

## 0. The one fact that reorders everything

`sweep.sh` runs

```
analyzeHeadless <projdir> <proj> -process <glob> -noanalysis -readOnly -postScript scan_patterns.java
```

(`sweep.sh:237-240`). **It never opens the original game binary.** Ghidra applied the PDB at
import time and the symbols live inside the `.rep`.

Consequences, and they are the spine of this whole document:

* A `.rep` is the **artifact of record**. Losing one loses the capability.
* The game install / archived binary is only needed to **RE-IMPORT** — i.e. to rebuild a `.rep`
  you deleted, or to re-analyse after a pattern change that needs fresh decompilation.
* Therefore "is the game still installed?" is a **recovery** question, never a **can I sweep
  today** question. `preflight.py` reports the two at different severities on purpose.

Verified empirically: in the last sweep, `UE4.18-FF7R` and `UE4.27-DropIn` both produced complete
scan TSVs while their binaries were off-disk.

-----

## 1. The manifest — `tools/ghidra/corpus-manifest.json`

Schema `ue5cedumper.corpus-manifest/2`. Regenerate with:

```
py tools/ghidra/build_corpus_manifest.py            # writes .json and .tsv
py tools/ghidra/build_corpus_manifest.py --no-hash  # skip the ~15 GB of hashing
```

Nothing in it is typed by hand. Five measured sources, in order of authority:

| # | Source | Gives |
|---|--------|-------|
| 1 | `tools/ghidra/sweep.sh` `ROWS=()` | TAG, project, glob, GS_TRUE → the tag list and `ROLE` |
| 2 | `<proj>.rep/idata/**/*.gbf` | program names, Ghidra's `Executable Location` + `Executable MD5` — **read straight off disk, no Ghidra, no project lock, ~3 s for all 43 projects** |
| 3 | the recorded path | does the binary still exist, is a `.pdb` beside it, what does it hash to today |
| 4 | `libraryfolders.vdf` + `appmanifest_*.acf` | appid / installdir / SizeOnDisk / buildid for what is installed **now** |
| 5 | `appcache/appinfo.vdf` (entry framing) | appid for titles that are **not** installed |

Anything none of the five can answer is `null` = UNKNOWN. **A guessed app id is worse than a gap** —
it sends you to install the wrong game.

### Grain and counts

`38 tags / 51 selected programs / 55 TSV rows`, all three asserted by the drift check.

* 38 = `ROWS=()` entries in `sweep.sh`. **Not 51.**
* 55 = one row per (tag, Ghidra program); modular projects hold several programs.
* 51 = rows the `-process` glob actually selects (the 4 unselected ones are the
  wrong-version programs inside the mis-imported `Satisfactory_UE521` project — the glob there is
  load-bearing **correctness**, not scoping convenience).

`corpus-manifest.tsv` is the same data at program grain, for diffing.

### Fields that exist because they were needed

* **`binary_md5`** — Ghidra's recorded import MD5. Unlike `steam_buildid` / `binary_size_bytes` /
  `binary_sha256` (which the generator deliberately nulls on a drifted row, so it never asserts
  the replacement build is the corpus build) this is **never nulled**: it describes the corpus
  build, not today's file. It is the only identity a GONE or DRIFTED row still has, and it is what
  lets `preflight --verify-hash` say *"this file is NOT the corpus build"* instead of shrugging.
* **`duplicate_copies`** — other paths whose bytes are byte-identical, MD5-confirmed. Searched only
  for rows no store can re-serve (self-built / archive). Same-name **same-size is not proof**:
  measured, a repackage of UE423_Flying emits a fresh PE at identical size
  (`43f6b130…` vs the imported `f61ec1be…`).
* **`pdb_relpath` / `pdb_size_bytes`** — the PDB checklist has to be machine-readable, or it drifts.

### Drift check

`preflight.py` parses `sweep.sh` itself and compares three ways: sweep-only tags, manifest-only
tags, project-name mismatches. **Any drift → exit 3.** Verified in both directions (adding a tag
and renaming one). A changed `GLOB` or `GS_TRUE` is *not* yet compared — see Open questions.

-----

## 2. Preflight — `tools/ghidra/preflight.py`

Pure-stdlib Python, no third-party deps, **never invokes Ghidra** (project state comes from
`.rep/idata/**/*.prp` XML on disk, so it takes no project lock and honours the read-only rule by
construction).

```
py tools/ghidra/preflight.py                  # the normal pre-sweep check, seconds
py tools/ghidra/preflight.py --sizes          # + per-project .rep sizes (for triage)
py tools/ghidra/preflight.py --verify-hash    # + MD5 every located binary (minutes, reads ~15 GB)
py tools/ghidra/preflight.py UE5.7            # filter to matching tags
py tools/ghidra/preflight.py --json           # machine-readable
```

**Two independent notions of "present", because the actions differ:**

| Level | Condition | Severity |
|-------|-----------|----------|
| A — Ghidra project | missing / locked / no `.gpr` / empty / glob matches nothing | **the sweep FAILS.** Gates (exit 2). |
| B — binary / PDB | not installed / wrong build / no symbols | sweep is fine; **re-import** is impossible. Advisory; gates only under `--strict` (exit 4). |

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | GO |
| 1 | tool error (**including argparse usage errors**, remapped from 2 so a typo can never read as "corpus incomplete") |
| 2 | NO-GO — a blocking Ghidra project problem (`--allow-partial` downgrades) |
| 3 | DRIFT — manifest vs `sweep.sh` disagree, either direction (`--allow-drift` downgrades) |
| 4 | GAPS — only under `--strict` |

Highest precedence wins; **all sections always print**, so a gate never hides information.

### What it cannot determine, and why

* **Whether a re-download reproduces the corpus build** without `--verify-hash`. Steam serves only
  the current build; only the MD5 comparison answers it.
* **`needs_pdb` for a title that is not installed.** It is derived by `stat()`-ing beside a located
  binary, so an uninstalled row reports `UNKNOWN_UNCHECKABLE` — *unmeasured, not "no"*.
* **PDB coverage of a modular project.** `pdb_relpath` tracks the anchor program only; the other
  8 programs of `UE5.6-Satisfactory` are not accounted for.
* **Ghidra database health.** It reads project *metadata*, never opens a program database. A
  silently corrupted `.rep` (the realistic failure mode when moving 120 GB onto a spinning HDD)
  looks perfectly healthy here. Only an actual sweep would notice.
* **App ids it was never given.** Where the manifest has `null` it prints
  *"app id UNKNOWN in manifest — cannot name what to install"* rather than guessing.

### Section [2] is informational, not a defect

`Maelstrom` and `Satisfactory_v1.2.3.1` contain `<name>.0` programs. **Measured**: these are
MS-DOS/MZ-loader imports that map only the 1104-byte DOS stub (`scanning … CODE_0 size=1104` at
image base `0000:0000`) and score `hits=0` on all 151 patterns. `aggregate_sweep.py` already
excludes them (`exec_mb <= 0` → "broken import"), which is why `REPORT.md` reads
**"programs scanned: 51"** and not 55. They do **not** double-score truth and do **not** inflate
any statistic. Deleting them in Ghidra is optional disk hygiene (~0.2 GB).

They do expose a **real bug**: `scan_patterns.java:206` builds the output filename from
`currentProgram.getImageBase()` and `sanitize()` does not strip `:`. On NTFS `…@0000:0000.txt`
truncates at the colon, so the content lands in **alternate data streams** of a 0-byte file:

```
Get-Item 'out/sweep/scan_UE4.27-Maelstrom__MaelstromV2-Win64-Shipping.exe@0000' -Stream *
    :$DATA 0   0000.tsv 16769   0000.txt 14167
```

Harmless for a stub; **silent data loss** for any real program Ghidra loads with a segmented
address space.

-----

## 3. PDB checklist — which titles must be installed to keep symbols

Measured 2026-07-29. **21 sweep rows resolve to a PDB, but only 20 distinct PDB files exist**
(`UE5.5-Everspace2` and `UE5.5-Everspace2b` point at the same path — see §5). Total 8.11 GB.

### 3a. Steam-resident — FRAGILE, a game patch overwrites these in place

10 distinct files, 6.25 GB. **There is no way to get an old PDB back.**

| Tag | Title (Steam) | App id | PDB MB |
|-----|---------------|--------|--------|
| UE5.5-Everspace2b | EVERSPACE™ 2 | 1128920 | 1801.0 |
| UE5.7-Solarpunk | Solarpunk | 1805110 | 1553.2 |
| UE4.27-Maelstrom | Maelstrom | 764050 | 954.2 |
| UE4.27-Breeders | Breeders of the Nephelym | 1161770 | 762.6 |
| UE4.20-Everspace | EVERSPACE | 396750 | 382.9 |
| UE5.5-Meltopia | Meltopia | 3601800 | 347.4 |
| UE4.27-DropIn | Drop In - VR F2P | 1144800 | 273.2 |
| UE5.1-Grimhook | Grimhook | 2667430 | 159.4 |
| UE4.20-HeliumRain | HeliumRain | 681330 | 109.6 |
| UE5.6-Satisfactory | Satisfactory | 526870 | 53.4 (anchor only) |

> **Action:** copying these 10 files into maintainer-held storage is 6.25 GB and removes the
> single largest fragility in the corpus. `UE5.6-Satisfactory` needs the whole
> `Engine/Binaries/Win64` PDB set, not just the anchor.

### 3b. Archive-held — safe, already yours

6 files, 1.39 GB, under `D:\tmp\Game archive` (mirrored to `X:\UE_Analyze_Data\Game archive`):
`UE4.22-Satisfactory` 700.1, `UE4.24-DropIn` 272.6, `UE5.2-SatGameDLL` 200.5,
`UE4.25-Everspace2` 165.8, `UE5.2-Satisfactory` 44.6, `UE4.26-Satisfactory` 36.3 MB.
These are superseded depot versions — **Steam cannot re-serve them.**

### 3c. Self-built — safe, and the cheapest backup on the machine

4 files, 484.4 MB. `UE5.8-StackOBot` 180.0, `UE5.7.4-StackOBot` 169.2, `UE4.23-Flying` 79.1,
`UE4.15-Flying` 56.1 MB.

> **The whole self-built oracle set — 4 exe + 4 pdb — is 897.0 MB.** Backing that up is under 1 GB
> and is the highest value-per-byte action available. All four have a second byte-identical copy
> today (see `duplicate_copies`), but three of those live in **volatile** locations
> (`D:\Unreal Projects\…\Binaries\` / `Saved\StagedBuilds\`) that a rebuild or Clean destroys.

Engines for a rebuild are installed on `C:\Program Files\Epic Games`: **UE_4.15 (22.54 GB),
UE_4.23 (45.33), UE_4.27 (54.19), UE_5.7 (74.13), UE_5.8 (84.70)** = 280.89 GB. Nothing is
registered with the Epic Launcher (`LauncherInstalled.dat` → `"InstallationList": []`), so a
delete is likely not re-downloadable in place — verify before removing.

### 3d. Not installed on Steam — but the BINARY is not lost

**Superseded 2026-07-29.** Six rows this document and the manifest called "binary GONE" —
`UE4.18-FF7R`, `FF7Re`, `Hogwarts_Legacy`, `Manor Lords`, `Octopath`, `DQ_I_II_HD2D` — all have
**md5-identical copies** under `D:\UE_Analyze_Data\Game Binary backup`, verified against the MD5
Ghidra recorded at import. Nothing was ever missing: `build_corpus_manifest.py` did not search
that root, and `find_duplicate_copies` additionally skipped the search for STEAM rows *and* for
GONE rows — exactly the two cases where a surviving copy matters most. Both guards are removed
and `D:\UE_Analyze_Data` is now first in `DUPE_ROOTS`. **36 of 38 rows now carry at least one
verified duplicate.** These titles therefore need a reinstall only to recover a **PDB**, never
the binary.

**`UE4.18-FF7R` — app 1462040, Steam title "FINAL FANTASY VII REMAKE INTERGRADE"** (the manifest's
`FINAL FANTASY VII REMAKE` is the *installdir*, not the store name — search the store for
INTERGRADE). `needs_pdb` is `null` = unmeasured. `sweep.sh` comments say it has no PDB and its
truth was derived by disassembly, but that is prose, not measurement. Install → re-run
`build_corpus_manifest.py` → the field settles itself.

### When a title reinstalls to a different path

**It already happened four times.** Octopath moved D:→E:, Manor Lords D:→H:, DQ I&II HD-2D D:→H:,
and Drop In reinstalled to a *different folder name* than the manifest recorded
(`Drop In - VR Battle Royale`, not `DropIn`).

The rule the tooling implements, and the rule to follow by hand:

1. **Key on Steam app id**, never on a path. `binary_last_seen` is a *hint*.
2. Resolve `appmanifest_<id>.acf` → `installdir` → bounded depth-3 search for `Binaries/Win64`,
   **shallowest depth wins** (otherwise P3R's Artbook and Soundtrack become phantom rows). Same
   logic as `ui/UE5DumpUI/Services/ProxyDeployService.cs` (`MaxBinariesSearchDepth = 3`).
3. `libraryfolders.vdf`'s `apps` map is **not proof of installation** — only
   `appmanifest_<id>.acf` **plus** the installdir folder are.
4. Re-run `build_corpus_manifest.py`; it records the new location and tags the row
   `relocated-library`. Do not hand-edit.
5. Modular builds scatter programs across **both** `Engine/Binaries/Win64` and
   `<Game>/Binaries/Win64` — resolve per program, not per title.

-----

## 4. Footprint (measured 2026-07-29 00:20–00:45 local)

Measured on the `D:`-rooted machine. Sizes are a property of that disk, not of the repo — see the
header note. The corpus has since moved (external USB → internal NVMe, 2026-08-01) and grew to
63 `.rep` / 182.3 GB, so the row below is a 2026-07-29 snapshot, not current. Re-measure with
`preflight.py --sizes` on whichever machine you are on.

| Asset | Size | Notes |
|-------|------|-------|
| `$GHIDRA_PROJS` (43 `.rep`, measured at `D:\Tools\GHIDRA_Projs`) | **120.94 GB** | 38 referenced by `sweep.sh` = 113.51; 5 orphans = 7.43 |
| — of which ORACLE projects (29 tags) | 92.60 GB | `sweep.sh` supplies `GS_TRUE` |
| — of which NOISE-PROBE projects (9 tags) | 20.93 GB | no truth; they only prove a pattern still fires |
| `C:\Program Files\Epic Games\UE_*` (5) | **280.89 GB** | biggest single item on the machine |
| Steam corpus payload (26 installed apps) | **558.47 GB** | 1 not installed (FF7R) |
| `D:\Unreal Projects` | 83.57 GB | only **22.47 GB** is corpus (ProjectTitan 61.11 GB is not) |
| `D:\tmp\Game archive` | **20.95 GB** / 1354 files | already pruned to Binaries trees |
| `X:\UE_Analyze_Data\Game archive` | **20.95 GB** / 1354 files | mirror of the above, same file count |
| `X:\UE_Analyze_Data\Varies Version builds` | 7.28 GB | the self-built oracle packages |
| Self-built oracle exe+pdb only | **897.0 MB** | the backup that matters most |

Free space at 00:18: C 980.3 · D 2362.1 · E 258.2 · F 801.2 · G 931.3 · H 636.2 · X 1670.1 GB.
**There is no disk crunch today.** D: alone can absorb the entire 120.94 GB Ghidra move. E: is the
tightest at 28.6% and holds two corpus titles — it is the drive most likely to force a decision
first, and it is *not* the Ghidra target.

### Recovering a binary you no longer have — `corpus-provenance.tsv`

`tools/ghidra/corpus-provenance.tsv` is a **snapshot** (2026-07-29) of every row's build identity.
It exists because `build_corpus_manifest.py` **nulls `steam_buildid`/`size`/`sha256` the moment a
row drifts** — correctly, since it must never assert the wrong build, but that destroys the pointer
to the build a `.rep` was made from. Palworld drifted that same day. **Re-snapshot before games
update, not after.** Four routes, strongest first:

| route | rows | what it gives you |
|---|---|---|
| `STEAMDB-BUILDID` | 22 | exact: SteamDB app → Builds → buildid → manifest |
| `STEAMLOG-MANIFEST` | **6** | Steam's own `console_log.txt` logs every depot fetch with app, depot, **exact manifest** and a timestamp. An archived file's mtime falls inside its download, so mtime → log line → manifest. A strong **candidate**, confirmed by md5. |
| `STEAM-BACKUP-MANIFEST` | **4** | the **exact depot manifest id** out of a `sku.sis` in `X:\SteamLibrary backup` — `steamcmd +download_depot <app> <depot> <manifest>`. Survives uninstall *and* delisting. |
| `REBUILD` | 4 | self-built; recompile from the installed engine |
| `STEAMDB-MANIFEST` | **2** | manifest id resolved by hand off SteamDB's depot history, tie broken by an independent repo record (see below) |
| `NONE-HASH-ONLY` | **0** | — |

**Nothing in the corpus is now without a recovery route.**

Two things that were wrong on the first pass and are worth not repeating:

* **Use `file_modified`, never `file_created`.** A copy RESETS ctime but PRESERVES mtime, so an
  *install → copy → uninstall* workflow keeps the original Steam write time. Measured: Hogwarts
  ctime `2026-07-29` but mtime `2025-12-04`; Octopath mtime `2025-05-12`; Palworld mtime
  `2026-07-15` = the pre-patch corpus build. Reading ctime made the dates look destroyed by the
  corpus move when they were not.
* **`sku.sis` beats every date.** The Steam backup descriptor records appid, depots and the exact
  manifest id. Harvesting `X:\SteamLibrary backup` moved **FF7 Remake, FF7 Rebirth, Hogwarts
  Legacy and DQ I&II** out of "md5 only" and into "exactly reproducible" — the three biggest
  uninstall candidates in the drop list below.

⚠ **`console_log.txt` ROTATES — it is the most perishable source here.** It was snapshotted on
2026-07-29 holding only ten download records, and those ten happen to cover every archive row.
Re-snapshot after any depot fetch you care about; once rotated, those manifest ids are gone and
the archive rows fall back to md5-only. The full capture, preserved because the log will not keep it:

```
2026-07-25 22:55:40  app=1128920 depot=1128921 manifest=1228092871900976040
2026-07-25 23:04:13  app=1128920 depot=1128921 manifest=1228092871900976040
2026-07-25 23:16:20  app=526870  depot=526871  manifest=5235170890177666837
2026-07-25 23:22:05  app=526870  depot=526871  manifest=4713640358549407449
2026-07-25 23:24:28  app=526870  depot=526871  manifest=3007809920758804289
2026-07-25 23:53:12  app=526870  depot=526871  manifest=5072022484048628830
2026-07-26 00:22:12  app=526870  depot=526871  manifest=4631200912822720421
2026-07-26 00:25:02  app=526870  depot=526871  manifest=5971929977835941106
2026-07-26 13:19:00  app=1144800 depot=1144801 manifest=5221052602514244898
2026-07-26 14:21:59  app=1144800 depot=1144801 manifest=8071507168854653981
```

**`UE4.22-Satisfactory` is the row that matters most** — it is the sole oracle for
`GNAM_SAT422_1` (priority 715, the UE 4.22 GNames lander), so losing it leaves that pattern with
no proof it works. It is **not** unrecoverable, only inconvenient: its best candidate is
`526870:526871:5235170890177666837`, reached two independent ways — the maintainer's own reading of
the Steam log (SteamDB dates it 2020-06-08, the right era for Satisfactory on UE 4.22), and the
mtime correlation above picking the same manifest as the download closest after `2026-07-25 23:13`.
Download it and confirm `md5 == a1df9f191f8978e6c05d7919237be565` before trusting it. It is also
mirrored twice already (67 MB on `D:\tmp\Game archive` + `X:\UE_Analyze_Data\Game archive`).

Two manifests in the log — `4631200912822720421` (2020-09-25) and `5971929977835941106`
(2021-01-11) — correlate to no archive row. They are later Satisfactory builds that may or may not
share a UE version with one already held; treat them as leads, not as identified rows.

**`UE5.5-Everspace2` (ES2-0517) is RESOLVED — `1128920:1128921:4415922863161237626`**, and the way
it was pinned is worth copying. SteamDB's depot history for 1128921 lists manifests published
2024-11-14, 2025-05-12, 2025-05-16, 2025-05-28 and 2025-06-17. A snapshot taken on **2025-05-17**
must have been running the **2025-05-16** one. The 2025-05-12 fallback is then *excluded* by an
independent record already in this repo: `sweep.sh`'s own note calls `Everspace2b` **"two manifests
newer (2025-06-17 vs the 05-17 snapshot)"** — newer than 2025-05-16 there are exactly two
(2025-05-28, 2025-06-17), whereas newer than 2025-05-12 there would be three. `Everspace2b` is
therefore the 2025-06-17 manifest `735055807809773736`, which is also the one currently installed.
Confirm on download with `md5 == 1a1b0ede76b80a173969343683579129`.

**Method worth reusing: date the snapshot, then let an existing repo note break the tie.** Neither
the SteamDB list nor the project name alone was decisive; a sentence written months earlier for an
unrelated reason was.

### Do this BEFORE any of the drop steps: re-import without analysis

**~88% of a `.rep` is Auto Analyze output, and the sweep does not read any of it.** This is the
only lever here that frees space at **zero** capability cost — every step in the table below
trades something away. Measured 2026-07-29, and verified rather than assumed:

| | `UE423_Flying-Win64-Shipping` (49 MB EXE) |
|---|---|
| `-noanalysis` import | **169 MB, 46 seconds** |
| fully analysed | **1,369 MB** |
| | **8.1×  —  analysis is 88% of the `.rep`** |

**Verified equivalent, not merely assumed:** `scan_patterns.java` run against the raw import
returns the *identical* five verdicts to the recorded `UE4.23-Flying` row in `REPORT.md` —
`GOBJ_ES53_1` OK-BEHIND, `GNAM_DI427_2` / `GWLD_TQ_1` / `SPARSE_DI427_1` / `GENG_X1` all
UNIQUE-OK. It works because the scanner touches only `getMemory()` / `getBytes()` /
`getImageBase()`; the imported program keeps the file's bytes, which is all it reads.
Corroborating the ratio at scale: the two mid-analysis `*_UE581` projects sit at ~3× their EXE
size while every finished project sits at 26–30×.

```bash
analyzeHeadless "$GHIDRA_PROJS" <Name> -import "<path>\<file>.exe" -noanalysis
```

**Three caveats, all load-bearing:**
1. **Needs the original binary.** Re-importing is only possible where the file still exists — so
   this does NOT apply to `ES2-0517` and the others in §"Never drop" whose source is gone. For
   those, Ghidra's *File → Export → Original File* should recover the bytes to re-import from,
   but **that path is untested here** — do not bet an irreplaceable `.rep` on it.
2. **Ten scripts genuinely need analysis** — `dump_func`, `decompile_functions`, `find_callers`,
   `dump_xrefs2`, `dump_global_xref_aob`, `find_gobjects`, `dump_vtables`, `dump_types`,
   `pe_probe`, `probe`. Those are for *reading code* when mining a new pattern or cracking a
   symbol-less binary — an occasional, per-investigation need. Analyse into a throwaway project
   then delete it, rather than storing analysis for all 43 permanently.
3. **`find_syms3.java` needs the PDB applied**, which `-noanalysis` skips. For the five sweep
   globals this no longer matters: `tools/pe/pdb_globals.py` reads them straight from the PDB
   with no Ghidra at all.

Applied across the 120.94 GB in `D:\Tools\GHIDRA_Projs` this is far and away the largest
zero-loss saving available; do it before considering a single deletion below.

### Drop order under pressure

Take these strictly in order. Re-run `preflight.py --sizes` between steps.

| Step | What | Frees | Capability lost |
|------|------|-------|-----------------|
| 0 | The orphan `.rep` — `Meltopia` 3.35, `Satfi426` 0.68, `ISDefenseEditor_UE410` 0.16, `ES1` 0.16 | **4.35 GB** | **None.** GROUND-TRUTH.md already calls Meltopia "superseded and can be deleted" and Satfi426 "superseded, do not re-chase". **Both 2026-07-29 caveats on this row are now discharged:** `UE423_Flying-Win64-DebugGame` (3.08 GB) is no longer an orphan at all — it is the live project behind the `UE4.23-FlyingDbgGame` sweep row, so it has been REMOVED from this list; and `ISDefenseEditor_UE410` is no longer the only evidence for the pre-4.11 floor, which is now measured by two full-PDB 4.10.4 oracles (`UE410_Game_Shipping` / `..._Development`). Drop all four without further thought. |
| 1 | 4 zero-contribution noise probes — `UE4.27-Artisan` 1.35, `UE5.5-ManorLords` 2.88, `UEx-DQ12HD2D` 1.60, `UE5.2-SatGameDLL` 2.28 | **8.11 GB** | Nothing measurable. Simulated exclusion: **0** patterns stop firing, **0** lose correctness evidence. Only §6 denominator mass. |
| 2 | 5 exclusive-exercise noise probes — `UE4.27-Hogwarts` 3.65, `UE5.6-TQ2` 3.30, `UE5.1-Palworld` 2.77, `UE4.x-FF7Rebirth` 2.33, `UE4.18-Octopath` 0.77 | **12.82 GB** | **0** correctness. 6 patterns fall into the already-populated "never hits anywhere" bucket (`GOBJ_RE1`, `GOBJ_V9`, `GOBJ_OT_2`, `GWLD_TQ_2`, `GWLD_SF_3`, `GWLD_TQ_3`). Taking steps 1+2 removes 865.5 MB of the 2,877.2 MB monolithic executable mass (30.1%). Hogwarts first: it is Denuvo-packed and contributes 4.0% of §6 **numerator** hits from non-`.text` bytes. |
| 3 | Version-redundant ORACLES, **one at a time**, re-sweeping between each — `UE5.5-Meltopia` 4.54, `UE4.27-DQ7R` 3.89, `UE4.27-Maelstrom` 3.81, `UE4.27-Breeders` 2.84, `UE4.20-HeliumRain` 1.39 | ≤ **16.47 GB** | Individually nothing. **Not jointly redundant**: Breeders + Maelstrom are the two independent symbolised 4.27s that proved DropIn's 32-byte `FUObjectItem` is a config artifact — keep at least one. Meltopia is the second symbolised *monolithic* 5.5 in a modular-heavy corpus. |
| 4 | Steam uninstalls, biggest first — FF7 Rebirth 159.31, Hogwarts 71.19, Avowed 67.60, Palworld 38.35 | **336.45 GB** (60% of the Steam payload) | **Nothing today.** All four are `needs_pdb=false`; their `.rep` (13.83 GB total) already holds everything the sweep reads. Cost is re-download time *if* a re-import is ever wanted. |
| 5 | Epic engines you will not rebuild against | up to **280.89 GB** | Only the ability to rebuild a self-built oracle. Redundant *if* §3c's 897 MB is backed up. Verify Launcher re-download first. |

### Never drop

`.rep` files holding a **sole-landing** pattern (removing one regresses a target on an engine
version outright) — 7 such patterns across 5 tags:

| Tag | .rep GB | Sole-landing patterns |
|-----|---------|----------------------|
| UE5.7-Solarpunk | 5.43 | `GNAM_SAT425_3`, `SPARSE_SP57_1` (two — the most of any entry) |
| UE4.22-Satisfactory | 2.58 | `GNAM_SAT422_1` (+ 5 sole-ok, the highest count in the corpus) |
| UE5.6-Satisfactory | 4.08 | `GWLD_SF_1` |
| UE4.20-Everspace | 1.67 | `GOBJ_G42_4` |
| UE5.3-Avowed | 5.08 | `SPARSE_AV53_1` |

> `UE4.18-FF7R` is the sole *landing* site of `GOBJ_RE2` but its **sole-ok count is 0** — the
> pattern stays proven on `UE4.15-Flying` and `UE4.18-DQXIS`, and FF7R's truth coverage
> `{GEngine, GObjects}` is a strict subset of `UE4.18-DQXIS`'s. It belongs in the redundant tier,
> not the floor. This corrects an earlier "6 tags" framing.

**Version coverage is an override axis, never a tiebreak.** 18 engine versions rest on exactly one
oracle, and 12 of those oracles score `soleLanding = 0` **and** `soleOk = 0` — including the only
4.23, the only 5.4 and the only 5.8. A script ranking on pattern uniqueness alone would delete
them without a single metric objecting.

**The Satisfactory family is a concentration trap.** `{UE4.22, UE4.26, UE5.2-Satisfactory,
UE5.2-SatGameDLL, UE5.6-Satisfactory}` = 12.47 GB across 5 projects, and is the sole source of
truth for **13 patterns**. A single "I'll clear out the Satisfactory stuff" is the most damaging
action available on this disk.

### Unrecoverable

* **`UE5.5-Everspace2` / `ES2-0517.rep` (11.38 GB, the largest project).** The file at its recorded
  path hashes `85daf780…`, which is exactly `UE5.5-Everspace2b`'s recorded MD5 — a game update
  overwrote the 05-17 build in place, **and its PDB with it**. Steam serves only the current build,
  so **no reinstall restores it**. Searched: all four Steam libraries, both archive roots, and
  drives D/E/F/G/H/X — no copy exists. Its `.rep` is the last copy of that analysis, and `sweep.sh`
  calls the ES2 pair the corpus's *only* same-game cross-build control — the only thing that can
  answer *"does a pattern survive a game update?"*. **Never delete it. Back it up first.**
  ⚠ Caveat: in the last sweep both ES2 rows resolved **identically** on all five targets, so its
  present differential signal is a null result. The value is in retaining the control, not in a
  disagreement it currently shows.
* **`UE4.18-FF7R`** — binary gone. Reinstallable (`steam://install/1462040`, ~100 GB), but a
  reinstall yields *today's* build; whether that reproduces MD5 `3ea9092f…` is **UNVERIFIED**.
  There is also a 91.64 GB depot-cache backup under `X:\SteamLibrary backup\FINAL FANTASY VII
  REMAKE INTERGRADE` (depot 1462041, `.csd`/`.csm`) — a local restore path nobody has tested.

### Cheap hedges worth knowing

* `D:\tmp\Game archive\DropIn` already holds an **unimported second 4.24 build**
  (`UE4.24.2`, MD5 `3D245058…`, distinct from `UE4.24`'s `2F640FF0…`). Importing it creates a
  second same-game cross-build pair for ~1.9 h of analysis — the cheapest available hedge against
  ever losing the ES2 pair.
* `X:\$RECYCLE.BIN` holds a complete `depot_1128921` ES2 4.25.2 tree (exe + 165.8 MB pdb).
  **"Empty recycle bin" silently deletes a corpus copy.**
* `X:\Ghidra_Projs_Backup` **is empty**, while `X:\sync_Ghidra_Projs.PS1` and `X:\sync.cmd` exist.
  A `.rep` backup was configured and has produced nothing. Given that the whole ~120 GB lives on
  one machine, this is the single most actionable item in this document.

-----

## 5. Routine

**Before a sweep:** `py tools/ghidra/preflight.py`. Exit 0 → run it. Exit 2 → fix the project first.
Exit 3 → the manifest and `sweep.sh` disagree; regenerate the manifest, do not hand-edit.

**After installing/uninstalling/moving a corpus title, or after any game patch:**
`py tools/ghidra/build_corpus_manifest.py`, then `preflight.py --verify-hash` to catch a build that
drifted under you. The manifest is a **point-in-time snapshot** — it was already stale within an
hour of first generation (a title reinstalled and it still read `BINARY GONE`).

**Before deleting anything:** `preflight.py --sizes`, then this document's §4 in order.

-----

## 6. Open questions

* Does restoring the `X:\SteamLibrary backup` FF7R depot cache reproduce MD5 `3ea9092f…`?
  Untestable without doing it.
* Do the Epic engine trees re-download from the Launcher after deletion, given
  `"InstallationList": []`?
* `UE5.6-Satisfactory` PDB coverage is tracked for the anchor program only; the other 8 programs
  are unaccounted for.
* Drift detection does not yet compare `GLOB` or `GS_TRUE` — a re-scoped truth prefix changes
  sweep results silently.
* No integrity/restore check exists for the `.rep` databases themselves. On a spinning HDD, silent
  corruption is a more likely loss mode than a deletion decision, and only a sweep would notice.
* Re-analysis cost per project is **UNKNOWN**. The `.gpr`-creation → newest-`.gbf` heuristic is not
  reproducible (it returns ≤ 0 h for six projects whose timestamps were reset by a copy, and
  924 h for Avowed), so any "GB reclaimed per re-analysis hour" ranking is unsupported. Time a
  real re-import before relying on one.


-----

## 6. `D:` is the working copy, `X:` is the backup

`D:\UE_Analyze_Data` holds three trees and is the one to keep fast and current:

| tree | size | contents |
|---|---|---|
| `Game Binary backup` | 11 GB | per-title EXE (+PDB where one exists), already consolidated |
| `Game archive` | 21 GB | DropIn / ES2 / Satisfactory full installs |
| `Varies Version builds` | 8.2 GB | the self-built engine samples, per config |

`X:\UE_Analyze_Data` mirrors this onto a spinning disk with 800k+ files. **Do not walk it in a
tool.** `build_corpus_manifest.py` now builds a single lazy basename index over all dupe roots
instead of walking per row — without that, adding `X:` to the search would have walked it 38
times. Full regeneration is 75 s.

Everything a sweep needs is derivable from EXE+PDB, so the preservation target is **12.17 GB**
(37 distinct EXEs 4.06 GB + 36 distinct PDBs 8.11 GB), not the ~123 GB of `.rep`.

## 7. UE 4.27.2 — built, not yet in the corpus

`D:\UE_Analyze_Data\Varies Version builds\4.27.2\{DebugGame,Development,Shipping}\Win64\`, each
with EXE + PDB. **Import the Development one and it replaces `UE4.27-DropIn`'s sole-oracle role.**

Why Development specifically: `GOBJ_DI427_1/2/3` are the only patterns for which DropIn is the
sole oracle, and what they encode is the **32-byte `FUObjectItem`** (`shl r,5`). Those 8 bytes are
`TStatId`, gated at 4.27 by `#if STATS || ENABLE_STATNAMEDEVENTS_UOBJECT` (`UObjectArray.h`
@ `4.27.2-release`). `STATS` is 0 in Shipping, so a **Shipping 4.27 sample adds nothing** —
Breeders and Maelstrom already cover the stock 24-byte item, which is how GROUND-TRUTH.md proved
the 32-byte item is a config artifact rather than a 4.27 trait.

This converts a sole-oracle dependency on an external store — where a patch can silently replace
the build, as happened to ES2-0517 — into a locally rebuildable asset. It also gives a second
three-config control group after 4.23, which can re-test the config-axis conclusions.

## 8. Moving the corpus to a spinning disk: verify with a sweep, not with preflight

`preflight.py` reads `.rep` metadata (`idata/**/*.prp`) and **never opens a program database**.
A silently corrupted `.rep` — the realistic failure mode when moving ~120 GB onto an HDD — looks
perfectly healthy to it. There is no cheap integrity check. **After the move, run one full sweep
and diff `out/sweep/REPORT.md` against the previous one; an unchanged regression matrix is the
only real acceptance test.**
