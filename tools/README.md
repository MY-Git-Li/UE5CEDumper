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
| `verify_aob.java` | Java | **Validate a candidate before shipping it.** Scans `.text` for each candidate AOB and resolves every hit exactly like `Genau::ScanForTarget` (`match+io`/`+opc`/`+tot`), reporting hits vs decoys vs true-target. Edit the `CAND[]` table. A good pattern is `correct>=1, decoys=0`. |
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
analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript verify_aob.java
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
