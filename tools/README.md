# tools/

Offline reverse-engineering helpers for finding/validating UE globals (`GObjects`,
`GNames`, `GWorld`) and structure layouts when the in-DLL AOB patterns and heuristics don't
match a game. Used to author new `Himmel.h` signatures and the `Genau`/`Aura` recovery paths.

See **[docs/reversing-nonstandard-ue-games.md](../docs/reversing-nonstandard-ue-games.md)** for
the end-to-end workflow these tools fit into (the Avowed playbook).

## `ghidra/` — Ghidra scripts

Run headless via `support/analyzeHeadless.bat` (or in the Script Manager GUI).
**Ghidra 12 dropped bundled Jython**, so the new helpers are **Java** GhidraScripts; the older
symbol/AOB exporters are Python run through the **pyghidra venv** (see their runner headers).

**Start here for anything AOB-related:** [`ghidra/GROUND-TRUTH.md`](ghidra/GROUND-TRUTH.md) holds
the per-project ground-truth addresses, the quirks of each supplied Ghidra project, and the
procedure for adding a new game. The sweep itself is scripted — do not hand-run `analyzeHeadless`
per project:

```sh
bash tools/ghidra/sweep.sh                      # all 57 rows, MEASURED ~4m40s at SWEEP_JOBS=3
bash tools/ghidra/sweep.sh UE4.27 UE5.7         # only tags matching these substrings
py tools/ghidra/aggregate_sweep.py out/sweep    # -> out/sweep/REPORT.md
```

| Script | Lang | What it does |
|--------|------|--------------|
| `sweep.sh` | bash | **The whole regression sweep.** Extracts `Himmel.h` → TSV, then replays it against every project in its table with that project's `GS_TRUE`, N projects at a time (a Ghidra project takes an *exclusive* lock, so parallelism is safe across projects and never within one). The truth table lives here in executable form so it cannot drift from the docs. |
| `aggregate_sweep.py` | py3 | Turns the per-binary TSVs into the decisions: the **regression matrix** (per oracle × target, which pattern the runtime lands on and how many validations were wasted getting there), hotspot ranking by cost, a band audit (specificity vs priority), dead weight, load-bearing patterns, and noise normalised per MB of monolithic `.text`. Writes `REPORT.md`. |
| `find_gobjects.java` | Java | Anchors on the `"...disregard for GC pool"` string (in `AllocateUObjectIndex`), finds + decompiles the referencing functions, lists their writable-`.data` globals. First arg overrides the anchor string. |
| `decompile_functions.java` | Java | Decompiles each function VA passed as an arg (+ lists writable-`.data` refs). Forces disassembly if Ghidra missed the function. Use it to read a struct layout out of a function (e.g. `FUObjectArray` offsets + item stride). |
| `find_callers.java` | Java | For each function VA arg, lists call sites and the `LEA/MOV RCX` (`this`) set before each call — recovers a static global a method is invoked on (e.g. `LEA RCX,[GUObjectArray]; call`). |
| `dump_global_xref_aob.java` | Java | **PDB-shipping games.** Resolves UE global symbols by name (`GWorld`, `GUObjectArray`, `NamePoolData`, `SparseDelegates`, `GEngine`, …) and, for every code xref, dumps the raw byte window + a disp-masked AOB candidate + read/write kind + containing function. Turns a symbol-rich binary into `Himmel.h` material in one pass. Filters out variable symbols (they lazy-load the whole datatype list → OOM). |
| `scan_patterns.java` | Java | **The engine `sweep.sh` drives.** Mass-scans a **TSV** of signatures against every executable section and resolves each hit exactly like `Genau::TryResolveMatch`, reporting `hits / ok / decoy` plus a verdict: `UNIQUE-OK`, `OK-FIRST`, `OK-BEHIND` (**a decoy scans first**), `DECOY-ONLY`, `MISS`, `NO-TRUTH`. Also emits a per-pattern hotspot TSV and a **consensus** file — which VA the most *independent* patterns agree on, the only correctness signal available on a symbol-less binary — plus a *priority walk* showing what the runtime would land on there. Understands nibble wildcards. Env: `GS_TSV`, `GS_OUT`, `GS_TAG`, `GS_TRUE` (entries may carry a `programNameSubstring:` prefix, which is **required** for modular builds — their DLLs share image base `0x180000000` so their ranges overlap). Outputs are keyed by tag + program + image base; program name alone is not unique across projects. |
| `replay_patterns.py` | py3 | **Replays the whole signature DB against a PE with NO Ghidra and no project** — reads `patterns.tsv`, resolves every RIP-relative match itself, and ranks the candidate VAs each target converges on. This is the corroboration half of the "derive truth without Ghidra" flow: `pdb_globals.py` says what the answer *is*, this says which patterns *find* it. `ONLY_TARGET=GNames TOPN=12` narrows it. ⚠️ It shows the top-N by voter count — **"not in the top N" is NOT "absent"**, a mistake that produced three wrong write-ups before the sweep corrected them. It also reports what *hits*, never what the validator walk *lands on*; only the sweep answers that. |
| `capture_provenance.py` | py3 | Regenerates `corpus-provenance.tsv`, the **build-identity snapshot**. Exists because `build_corpus_manifest.py` deliberately NULLs `steam_buildid`/size/sha256 on a drifted row, destroying the pointer to the build a `.rep` was made from the first time a game patches. Merges four sources: Steam `sku.sis` backups, `console_log.txt` depot downloads (which **rotate** — snapshot early), the manifest, and hand-resolved SteamDB entries. Idempotent. **Re-run BEFORE a game updates, not after.** |
| `extract_patterns.py` | py3 | Parses **all** of `dll/src/Himmel.h` (macros + raw brace initialisers + string-concatenated constants) into the TSV `scan_patterns.java` eats. Lets you replay the entire signature database against a new binary in one pass. No deps. |
| `gen_cands.py` | py3 | Turns a `dump_xrefs2.java` dump into hundreds of mechanically-enumerated candidate patterns (every xref × several window lengths, trailing wildcards trimmed, anchor-byte rule enforced). Feed the result to `scan_patterns.java` so selection is evidence-driven instead of eyeballed. |
| `dump_xrefs2.java` | Java | Successor to `dump_global_xref_aob.java`: accepts `Label@hexVA` as well as symbol names, and per xref emits the disassembly context plus disp-masked **and** raw windows at four back-off depths (`AOB0/2/4/6`, each with its `io=`), so you can pick how much leading context a pattern needs. |
| `dump_types.java` | Java | Dumps PDB struct layouts — `sizeof` + every field offset — for ~120 UE types (`FUObjectItem`, `FProperty`, `FNamePool`, `UStruct`, `UWorld`, …). The ground truth for `docs/technical-notes.md`. |
| `dump_vtables.java` | Java | Dumps every slot of the UE spine classes' vftables with the resolved function name per slot. How the UE 4.27 `ProcessEvent` slot (`+0x220`) was confirmed. |
| `pe_probe.java` | Java | Byte-exact simulation of the DLL's `DetectProcessEventVTableOffsetByPattern` against real vtables — answers "would the shipped detector find PE on this build, and where?" without running the game. |
| `dump_dataat.java` | Java | Demangled type + recursive layout + raw bytes of a global. How `FSparseDelegateStorage::SparseDelegates` was shown to be keyed by a raw `UObjectBase*` on UE 4.27. |
| `dump_func.java` | Java | Disassembly + decompilation of a function, annotating every referenced global with its symbol. |
| `find_syms3.java` | Java | Regex symbol search with a code/data filter. |
| `scan_strings.java` | Java | Raw ASCII **and UTF-16LE** substring scan over initialised sections. Found that DropIn keeps `++UE4+Release-4.27` only as a wide literal. |
| `probe.java` | Java | One-shot sanity check that a project opened, PDB symbols are present, and the key UE globals resolve. Run this first on any new binary. |
| `verify_aob.java` | Java | **Superseded by `scan_patterns.java`** — kept only because older notes point at it. Hand-edited `CAND[]` table, no nibble support, no decoy-ordering verdict. |
| `ExportUESymbols.ghidra.py` | pyghidra | Exports UE symbols (FName/UObject/GObjects/GNames/FNamePool) to JSON. |
| `ExtractAOBContext.ghidra.py` | pyghidra | Extracts byte patterns around all code refs to GObjects/GNames/GWorld → JSON per game (AOB pattern mining). |
| `run_headless_export.py` | pyghidra | Headless runner: opens one project, runs a script on one binary. |
| `run_all_aob_export.py` | pyghidra | Batch runner: `ExtractAOBContext` over all Ghidra projects. |

Java examples (read-only, won't modify the project):

```sh
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript decompile_functions.java 0x147A604E0 0x14814D2F0
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript find_callers.java 0x14814D2F0

# PDB-shipping game (symbol-rich): mine + verify AOBs. A game with a ~GB PDB needs a
# big heap or the datatype manager OOMs — export _JAVA_OPTIONS=-Xmx16G first.
export _JAVA_OPTIONS="-Xmx16G"
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript dump_global_xref_aob.java
```

### The full PDB→AOB loop (the flow that produced the `DI427` signatures)

```sh
export _JAVA_OPTIONS="-Xmx24G"
export GS_OUT="$PWD/out"

# 0. does the project even have symbols?
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript probe.java

# 1. what does the CURRENT database do on this binary? (extract -> replay -> verdicts)
py tools/ghidra/extract_patterns.py dll/src/Himmel.h out/patterns.tsv
GS_TSV=$PWD/out/patterns.tsv \
GS_TRUE="GObjects=<objObjectsVA>|<fuObjectArrayVA>,GNames=<poolVA>,GWorld=<va>,SparseDelegates=<va>,GEngine=<va>" \
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript scan_patterns.java

# 2. mine candidates for whatever came back MISS / DECOY-ONLY
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript dump_xrefs2.java "GWorld@<va>" "ObjObjects@<va>"
py tools/ghidra/gen_cands.py out/xrefs_GWorld.txt GWorld 3 7 0 out/cands.tsv 20 28

# 3. verify — and RUN IT ON OTHER GAMES TOO. The bar is UNIQUE-OK on the owning binary
#    AND zero hits (or correct) everywhere else. A shorter pattern that looks clean on one
#    binary routinely produces decoys on another engine version; that is the whole point
#    of the multi-binary gauntlet.
GS_TSV=$PWD/out/cands.tsv GS_TRUE="GWorld=<va>" analyzeHeadless ... -postScript scan_patterns.java
```

## `pe/` — PE (capstone) helpers

| Script | What it does |
|--------|--------------|
| `disasm_function.py` | Offline x64 disassembler for function VA(s) in a PE; annotates RIP-relative writable-`.data` targets and flags the zero-init **BSS** ones (where runtime-filled globals like `GUObjectArray` live). `py -m pip install capstone pefile` first. |
| `ue_version.py` | Read a game's UE version out of its `++UE5+Release-X.Y` build tag, to decide whether it is worth a Ghidra import at all. Stdlib only. **~half of shipped games have the tag stripped**, so `UNKNOWN` means unknown, not uninteresting — it is a filter, not a gate. |
| `func_bytes.py` | **Answer "is this function hollow?" offline** — resolves any symbol via the PDB, then reads the EXE bytes at that VA. A `#if !UE_BUILD_SHIPPING` body compiles to a bare `ret`; a live one does not. This is what disproved the long-standing "UCheatManager is body-stripped in Shipping" claim (it is not — the gate is that no *instance* exists). No game running, no Ghidra. |
| `pdb_globals.py` | **Sweep ground truth out of a PDB, without opening Ghidra.** Prints GObjects / GNames / GWorld / SparseDelegates / GEngine and a paste-ready `GS_TRUE=` line for `tools/ghidra/sweep.sh`. Stdlib only — it reads the MSF publics stream directly. Replaces step 2 of GROUND-TRUTH.md's "Deriving truth for a new game" (a ~10-min headless run) with ~2 seconds, for any binary that ships symbols. When GObjects has no public symbol it prints the **pre-4.11 magic-static route** (`GetUObjectArray` → the `lea` feeding the ctor) instead of a bare "NOT FOUND". |
| `pdb_match.py` | **"Can I trust this .pdb for this .exe?"** — the check to run on any PDB whose provenance you are not certain of. A matching FILENAME proves nothing: a PDB from a *different build of the same game* loads without complaint and yields addresses wrong by an unpredictable amount, which is the worst failure mode for ground truth because every value looks plausible. Compares the PE's CodeView **GUID + Age** against the PDB's own info stream (the linker mints a fresh GUID per link, so a rebuild cannot fake it), then decodes the publics stream to confirm it carries real content and not just a stripped shell. `--scan <dir>` walks a whole backup tree. Exit 0 = all usable. Also reports whether the PE was linked **`/Brepro`** (`IMAGE_DEBUG_TYPE_REPRO`), which is the ONLY sound way to know whether its `TimeDateStamp` is a build date or a content hash — plausibility is misleading (Hogwarts reads `2025-11-12` and Elliot `2026-07-15`; both are hashes). |

```sh
py tools/pe/disasm_function.py "<game>.exe" 0x147A604E0 0x14814D2F0

# Triage a whole Steam library before importing anything
py tools/pe/ue_version.py "D:/SteamLibrary/steamapps/common"/*/*/Binaries/Win64/*-Shipping.exe

# Ground truth for a new oracle. The GObjects base|base+0x10 alias is decided AUTOMATICALLY from
# the PDB (5.8 made ~FFieldClass virtual, so `??1FFieldClass@@UEAA@XZ` = 5.8+ = no alias); the
# tool prints which way it went and why. Override with --gobjects-alias / --no-gobjects-alias.
py tools/pe/pdb_globals.py "<game>-Win64-Shipping.pdb"
py tools/pe/pdb_globals.py "<game>.pdb" --grep FSparseDelegateStorage   # hunt decoys / prove absence

# Vet a PDB BEFORE deriving truth from it — pairing (GUID+Age) and content in one pass.
py tools/pe/pdb_match.py "<game>-Win64-Shipping.exe"          # partner inferred by name
py tools/pe/pdb_match.py --scan "D:/UE_Analyze_Data/Game Binary backup"
```

**The strongest PDB check is not this tool — it is reproducing a row you already have.** Pairing
proves *which binary* the PDB describes; it does not prove your decode of it is right. Run
`pdb_globals.py` on a PDB whose oracle is already in `sweep.sh` and diff the `GS_TRUE=` line
against the recorded one. Measured 2026-07-29 over the 9 pairs in `Game Binary backup`: all 9 pair
correctly, and **6 of 7 corpus oracles reproduce byte-for-byte**. Two things that look like
failures and are not:

* **`GObjects=A|B` is a SET, not an ordered pair.** `scan_patterns.java` reads `true=[a,b]` and
  accepts either. Most rows are recorded ascending, a few descending (Everspace 2, Solarpunk) —
  cosmetic only.
* **A missing GNames can be correct for that title.** Solarpunk has neither `FName::GetNames` nor
  `FNameDebugVisualizer::GetBlocks`, so `pdb_globals` legitimately reports NOT FOUND; its truth
  came from the PDB→AOB Ghidra loop below. Absence is a routing problem, not a bad PDB.

> **Validate it before trusting a new row**: re-run it on `UE423_Flying-Win64-Shipping.pdb` and
> `StackOBot-Win64-Shipping.pdb` (5.8) and confirm it still reproduces those two `sweep.sh` rows
> byte-for-byte. Then corroborate the new values independently — pattern-replay consensus, per
> GROUND-TRUTH.md rule 4. A silently-drifting decoder is precisely the failure mode that file
> exists to prevent (a single wrong VA has already corrupted a whole sweep once).

> The authoritative version source stays the DLL's runtime detection (the
> `UE5_Init: Complete (UEnnn, …)` line in the game's `init-0.log`), which reconciles the PE data
> against memory — The Adventures of Elliot's PE claims 4.27 and it is really 5.4.
> `ue_version.py` still earned its place: it recovered Octopath Traveler as **4.18**, which the
> sweep corpus had carried as "4.x, version stripped".

## External (not vendored)

- **[patternsleuth](https://github.com/trumank/patternsleuth)** (`cargo run -p patternsleuth_cli -- scan --path <exe> --resolver <name>`) — confirm whether the standard resolvers match, and get string-anchored candidate functions. Clone on demand; do not vendor (large + rebuilds).
- **[UEPseudo](https://github.com/Re-UE4SS/UEPseudo)** — RE-UE4SS per-version standard struct layouts (`generated_include/FunctionBodies/`) to compare against.
