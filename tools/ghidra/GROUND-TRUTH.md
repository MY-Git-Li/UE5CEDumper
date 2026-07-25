# Ground truth for the AOB regression sweep

Every address below was resolved from a real PDB symbol (or, where noted, from a
2-instruction accessor), so a signature that resolves to one of these is *provably*
correct rather than plausibly correct. Feed them to `scan_patterns.java` via `GS_TRUE`.

**Why this file exists:** re-deriving these takes a headless run per binary, and getting one
wrong silently corrupts every verdict in the sweep. It has already happened once — a
placeholder truth value made two good GEngine patterns look like they produced five decoys,
and they were demoted on that basis. `scan_patterns.java` now prints `NO-TRUTH` instead of
`DECOY-ONLY` when it has no plausible truth, but the real fix is to keep the values written down.

All addresses are **image-based VAs** as Ghidra shows them (preferred base, not runtime).

## The sweep

| Project (`D:\Tools\GHIDRA_Projs\*.rep`) | UE | Symbols | Notes |
|---|---|---|---|
| `ES1-420` | 4.20 | ✅ full PDB | oldest sample; supersedes the symbol-less `ES1.rep` |
| `DropIn` | 4.27.2 | ✅ full PDB | Development build (32-byte `FUObjectItem`) |
| `Avowed` | 5.3 | ❌ none | negative control only — can say "no hits", never "wrong hit" |
| `ES2-0517` | 5.5 | ✅ full PDB | `0517` is a DATE, not a version |
| `Satisfactory_v1.2.3.1` | 5.6.1 | ✅ full PDB | **modular** — truth is split across 3 DLLs |
| `Solarpunk` | 5.7 | ✅ full PDB | |
| `Satfi426` | 4.26 | ⚠ partial | **unusable**: CoreUObject/Engine were never imported |

## `GS_TRUE` strings (copy-paste)

`GObjects` accepts two values because `ValidateGObjects` matches both the `FUObjectArray`
base and its `ObjObjects` sub-struct (base + 0x10).

```sh
# Everspace — UE 4.20.  No FNamePool (TNameEntryArray era) and no sparse delegates (4.23+).
# GNames here is `Names`, a TNameEntryArray* lazily new'd inside FName::GetNames @0x1406B19D0.
GS_TRUE="GObjects=142e797f0|142e79800,GNames=1431dead8,GWorld=1432e1ac0,GEngine=1432df470"

# DropIn — UE 4.27.2.  NamePoolData has no symbol; recovered from
# FNameDebugVisualizer::GetBlocks @0x1426F59C0 = `lea rax,[0x14A363950]; ret`, minus 0x10.
GS_TRUE="GObjects=14a3aa670|14a3aa660,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910,GEngine=14a528890"

# Everspace 2 — UE 5.5.
GS_TRUE="GObjects=149aa7ef0|149aa7ee0,GNames=149c009c0,GWorld=149b37d18,SparseDelegates=149aa7e90,GEngine=149da5810"

# Solarpunk — UE 5.7.
GS_TRUE="GObjects=1476ca920|1476ca910,GNames=1478e50c0,GWorld=1478cce58,SparseDelegates=1476ca4b0,GEngine=147a2fc20"

# Satisfactory v1.2.3.1 — UE 5.6.1, MODULAR: run three times, once per DLL.
#   NOTE the name pool moved from CoreUObject to Core by 5.6.
#   NamePoolData from FNameDebugVisualizer::GetBlocks @0x18035CE20 = `lea rax,[0x18082E8D0]; ret`.
-process "FactoryGameSteam-CoreUObject-Win64-Shipping.dll"  GS_TRUE="GObjects=1805a3630|1805a3620,SparseDelegates=1805661a0"
-process "FactoryGameSteam-Core-Win64-Shipping.dll"         GS_TRUE="GNames=18082e8c0"
-process "FactoryGameSteam-Engine-Win64-Shipping.dll"       GS_TRUE="GWorld=18216db68,GEngine=182170748"

# Avowed — UE 5.3, NO symbols.  Pass NO GS_TRUE at all; every target reports NO-TRUTH and the
# only signal you get (the one you want) is "did anything hit that should not have?".
```

## Running the sweep

```sh
export _JAVA_OPTIONS="-Xmx24G"
py tools/ghidra/extract_patterns.py dll/src/Himmel.h out/patterns.tsv

GS_OUT="$PWD/out/<name>" GS_TSV="$PWD/out/patterns.tsv" GS_TRUE="<from above>" \
analyzeHeadless D:/Tools/GHIDRA_Projs <Project> -process -noanalysis -readOnly \
    -scriptPath tools/ghidra -postScript scan_patterns.java
```

Then read the **`>>> SELECTED`** lines — one per target. They answer the only question that
matters when signatures are added: *walking priority order, which pattern hits first, and does
it reach truth?* That mirrors `Genau::ScanForTarget`, which validates each match and takes the
first that passes.

| Verdict | Meaning |
|---|---|
| `CORRECT (all hits)` | ideal — every match resolves to the true VA |
| `CORRECT first (N decoy(s) scan later, never reached)` | fine — `.text` is swept low→high and the real site comes first |
| `AT RISK: N decoy(s) scan BEFORE the first correct match` | the runtime validator must reject all N. Acceptable for GObjects/GNames (strong validators), **dangerous for SparseDelegates** |
| `*** WOULD RESOLVE WRONG unless the validator rejects all N hits ***` | this pattern alone would pick wrong; a later pattern must save it |
| `NO-TRUTH` | no usable truth supplied — verdict withheld on purpose, **not** evidence of anything |

## Adding a new PDB game

1. `probe.java` first — confirms the project opened and whether symbols exist at all.
2. If symbols: read the globals off it. If `NamePoolData` has no symbol (common), disassemble
   `FNameDebugVisualizer::GetBlocks` — it is always `lea rax,[&Pool.Entries.Blocks]; ret`, so
   subtract `0x10`. Check **Core** as well as CoreUObject on 5.6+.
3. If no symbols: run the sweep with no `GS_TRUE`, then take addresses that **≥3 independent
   patterns** agree on. This was validated on Everspace — the pre-PDB consensus for
   GWorld/GObjects/GNames matched the symbols exactly once the PDB arrived.
4. Add the row + `GS_TRUE` line here, then re-run the whole sweep, not just the new game.
