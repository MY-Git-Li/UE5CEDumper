# identity/

The fingerprint of every Ghidra program in the sweep corpus — 74 records, ~163 KB, emitted by
[`dump_identity.java`](../dump_identity.java).

Each holds Ghidra's own provenance for that program (executable **MD5/SHA256**, `Created With
Ghidra Version`, PDB GUID/Age), its analysis state, and a **SHA-256 over every `(address, name,
type, scope, source)` in the symbol table**.

**These are committed because they are what outlives a `.rep`.** Delete a project and its identity
is gone with it — and then a future rebuild has nothing to be verified against. With these,
[`reimport_verify.py`](../reimport_verify.py) can still answer "is the rebuild the same project?"
years later:

```sh
py tools/ghidra/reimport_verify.py UE4.10-Game
py tools/ghidra/reimport_verify.py UE4.26-Satisfactory --program CoreUObject --analyze
```

The digest is the load-bearing field. Matching symbol *counts* would pass a rebuild that put the
right number of symbols at the wrong addresses — exactly what a same-named-but-different PDB
produces, which is the failure mode this corpus is most exposed to.

## Two things these records settled

**The discriminator is instructions, not functions — and definitely not `.rep` size.** Applying a
PDB creates functions and defined data without disassembling anything, so `UE4.10-GameDev` records
195,451 functions, 840,853 defined data, `Analyzed=false` and **zero instructions**. Across the
corpus: 42 programs PDB-loaded *and* disassembled, 18 disassembled without a PDB, 13 raw imports
(including the 4 broken stubs).

**`ES2-0517` is the only project created by Ghidra 11.3.2**; the other 72 programs are 12.1.2. That
is why it re-runs a language-version upgrade on every open, and it is the one project whose original
toolchain a rebuild on the installed Ghidra cannot reproduce.

Regenerate after adding a sweep row (~1 h for the whole table — the symbol digest on a fully
analysed project is the slow part):

```sh
SWEEP_SCRIPT=dump_identity.java SWEEP_OUT=$PWD/out/identity bash tools/ghidra/sweep.sh
```

`meta.SourceFileNNN` (hundreds of PDB build-tree paths per program) is omitted by default — 85% of
the bytes, ignored by every comparison, and re-derivable from the archived PDB. `GI_SOURCEFILES=1`
keeps it.
