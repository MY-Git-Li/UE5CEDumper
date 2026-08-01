# memory-maps/

Ghidra's own memory model for every program in the sweep — image base, the complete block map
(start / size / exec / init / read / write) and an **MD5 per initialized block**. 74 files, ~62 KB,
emitted by [`dump_blocks.java`](../dump_blocks.java).

**These are the oracle for [`pe_memory.py`](../pe_memory.py)**, which claims to reconstruct all of
that from the game binary alone. `check_pe_memory.py` diffs the two per block:

```sh
py tools/ghidra/check_pe_memory.py --map out/pe-corpus-map.tsv
```

The digest is the load-bearing part. Matching starts and sizes prove only that the LAYOUT agrees;
a wrong section-size rule, a wrong file offset or a missed zero-fill all survive a layout check and
all change a hash. The rule that only a hash could have settled: a section's initialized block is
`max(SizeOfRawData, VirtualSize)`, zero-padded — raw alone fits 69 of 70 programs and breaks on
DQ7R, a packed build whose executable `.debug` section has `vsz` 1024 bytes larger than `rsz`.

They are **committed on purpose**, for 62 KB:

* `check_pe_memory.py` and therefore the Ghidra-free sweep run on a machine that has the game
  archive and no Ghidra — which is the point of the exercise.
* `pe_memory.py` gets a regression oracle. Without these, its section rule is a claim someone
  believed once; with them, breaking it fails a check.

Regenerate after adding a sweep row (a few KB per program, ~5 min for the whole table):

```sh
SWEEP_SCRIPT=dump_blocks.java SWEEP_OUT=$PWD/out/blocks-dump bash tools/ghidra/sweep.sh
cp out/blocks-dump/blocks_*.tsv tools/ghidra/memory-maps/
```

Four files record a **broken import** (image base `0000:0000`, ~1 KB of DOS stub mapped as code) —
Ghidra artifacts sitting in a project alongside the real program, with no PE counterpart.
`check_pe_memory.py` reports them as SKIP-BROKEN, keyed on the segmented image base and **not** on a
size threshold: Satisfactory ships real DLLs with 6.5 KB and 28 KB of `.text`, and a threshold would
quietly drop those while the summary still read as a clean pass.
