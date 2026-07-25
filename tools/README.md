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

| Script | Lang | What it does |
|--------|------|--------------|
| `find_gobjects.java` | Java | Anchors on the `"...disregard for GC pool"` string (in `AllocateUObjectIndex`), finds + decompiles the referencing functions, lists their writable-`.data` globals. First arg overrides the anchor string. |
| `decompile_functions.java` | Java | Decompiles each function VA passed as an arg (+ lists writable-`.data` refs). Forces disassembly if Ghidra missed the function. Use it to read a struct layout out of a function (e.g. `FUObjectArray` offsets + item stride). |
| `find_callers.java` | Java | For each function VA arg, lists call sites and the `LEA/MOV RCX` (`this`) set before each call — recovers a static global a method is invoked on (e.g. `LEA RCX,[GUObjectArray]; call`). |
| `dump_global_xref_aob.java` | Java | **PDB-shipping games.** Resolves UE global symbols by name (`GWorld`, `GUObjectArray`, `NamePoolData`, `SparseDelegates`, `GEngine`, …) and, for every code xref, dumps the raw byte window + a disp-masked AOB candidate + read/write kind + containing function. Turns a symbol-rich binary into `Himmel.h` material in one pass. Filters out variable symbols (they lazy-load the whole datatype list → OOM). |
| `scan_patterns.java` | Java | **The one to use for validation.** Mass-scans a **TSV** of signatures against every executable section and resolves each hit exactly like `Genau::TryResolveMatch`, reporting `hits / ok / decoy` plus a verdict: `UNIQUE-OK` (every hit correct), `OK-FIRST` (a correct hit scans before any decoy), `OK-BEHIND` (**a decoy scans first — unsafe for a weakly-validated target**), `DECOY-ONLY`, `MISS`. Understands nibble wildcards. Env: `GS_TSV`, `GS_OUT`, `GS_TRUE="GObjects=<va>[\|<va2>],GNames=<va>,…"`. |
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

```sh
py tools/pe/disasm_function.py "<game>.exe" 0x147A604E0 0x14814D2F0
```

## External (not vendored)

- **[patternsleuth](https://github.com/trumank/patternsleuth)** (`cargo run -p patternsleuth_cli -- scan --path <exe> --resolver <name>`) — confirm whether the standard resolvers match, and get string-anchored candidate functions. Clone on demand; do not vendor (large + rebuilds).
- **[UEPseudo](https://github.com/Re-UE4SS/UEPseudo)** — RE-UE4SS per-version standard struct layouts (`generated_include/FunctionBodies/`) to compare against.
