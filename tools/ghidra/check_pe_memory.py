#!/usr/bin/env python3
"""Does a plain PE reader reproduce Ghidra's memory model EXACTLY? Answered per block, by hash.

    py tools/ghidra/check_pe_memory.py
    py tools/ghidra/check_pe_memory.py --blocks out/blocks --roots "D:/UE_Analyze_data" \\
        --map out/pe-corpus-map.tsv

WHY. `scan_patterns.java` uses four Ghidra APIs and zero analysis APIs, so for the sweep a Ghidra
program IS `image base + block map + bytes`. If `pe_memory.py` reproduces all three from the game
binary, then the 181 GB `.rep` corpus is not a sweep input at all — the 59.4 GB archive we already
keep is (`docs/corpus-preservation.md`), and the sweep needs no JVM, no Ghidra install, and none of
the GB-scale transient writes that `-readOnly` does NOT prevent.

WHAT MAKES THIS A REAL TEST AND NOT A RESTATEMENT. `dump_blocks.java` emits Ghidra's own block map
with an MD5 per initialized block. Matching starts and sizes would only prove the LAYOUT agrees;
the digest is what proves the BYTES do. A wrong section-size rule, a wrong file offset or a missed
zero-fill all survive a layout check and all change a hash.

TWO THINGS IT WILL NOT DO.

  * IT WILL NOT ACCEPT A FILE BY NAME. A same-named binary from another build is exactly the
    failure this corpus keeps hitting; the block digests are the identity, and a candidate that
    does not hash-match is reported, never used.
  * IT WILL NOT SILENTLY PASS A BROKEN IMPORT. Several projects hold a stub re-import (image base
    0000:0000, only the 1104-byte DOS header mapped) alongside the real program. Those have no PE
    counterpart by construction and are reported as SKIP-BROKEN, not as a failure and not as a
    pass.

Exit 0 = every non-broken program is reproduced byte-for-byte from its PE.
"""
import argparse
import collections
import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe_memory import PeMemory                                        # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_ROOTS = [r"D:\UE_Analyze_data"]
PE_EXT = (".exe", ".dll")


def parse_blocks_tsv(path):
    """-> (meta dict, [block dicts]) from dump_blocks.java's output."""
    meta, blocks, hdr = {}, [], None
    with io.open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n").rstrip("\r")
            if line.startswith("# "):
                k, _, v = line[2:].partition("\t")
                meta[k] = v
                continue
            cols = line.split("\t")
            if hdr is None:
                hdr = cols
                continue
            blocks.append(dict(zip(hdr, cols)))
    return meta, blocks


def index_names(roots):
    """basename.lower() -> [paths]. A CANDIDATE GENERATOR ONLY — every hit is then verified by
    hash, because a matching filename has never been evidence in this corpus."""
    idx = collections.defaultdict(list)
    for root in roots:
        if not os.path.isdir(root):
            print("  !! root not found, skipped: %s" % root)
            continue
        for dp, _, fn in os.walk(root):
            for f in fn:
                if f.lower().endswith(PE_EXT):
                    idx[f.lower()].append(os.path.join(dp, f))
    return idx


RIP_SLACK = (1 << 31) + 0x10000


def reachable_window(gblocks):
    """The address range a sweep can ever ASK about, from Ghidra's own executable blocks.

    Everything the sweep looks up is `insVA + tot + disp32 (+adj)` with `insVA` inside an
    executable block, so nothing outside one signed 32-bit displacement of the executable range
    can be reached — not by `getBlock`, not by `getLong`. A block outside it is invisible to the
    scan no matter what it holds."""
    ex = [(int(b["start"], 16), int(b["start"], 16) + int(b["size"]))
          for b in gblocks if b["exec"] == "1"]
    if not ex:
        return (0, 0)
    return (min(s for s, _ in ex) - RIP_SLACK, max(e for _, e in ex) + RIP_SLACK)


def compare(mem, blocks):
    """-> (ok, [difference strings], [exempt strings]).

    Blocks are aligned by ADDRESS, not by index, so one extra block on either side reports as
    itself instead of shifting every later comparison into a false mismatch.

    Ghidra synthesizes blocks a PE does not contain — `tdb` at 0xff00000000 on a PDB-bearing
    import, for one. Those are exempted ONLY when they are non-executable AND lie outside the
    reachable window above, which is a property that can be checked rather than assumed; the
    exemptions are listed, never silently dropped."""
    import hashlib
    diffs, exempt = [], []
    lo, hi = reachable_window(blocks)
    gmap = {(int(b["start"], 16), int(b["size"])): b for b in blocks}
    pmap = {(b.start, b.size): b for b in mem.blocks}

    def irrelevant(start, size, is_exec):
        return (not is_exec) and (start + size <= lo or start >= hi)

    for key, gb in gmap.items():
        gstart, gsize = key
        gexec, ginit = gb["exec"] == "1", gb["init"] == "1"
        pb = pmap.get(key)
        if pb is None:
            msg = "ghidra-only block %s @%x +%d (x=%d)" % (gb["name"], gstart, gsize, int(gexec))
            (exempt if irrelevant(gstart, gsize, gexec) else diffs).append(msg)
            continue
        if pb.name != gb["name"] or pb.execute != gexec or pb.initialized != ginit:
            diffs.append("block @%x +%d: pe(%s x=%d i=%d) vs ghidra(%s x=%d i=%d)"
                         % (gstart, gsize, pb.name, pb.execute, pb.initialized,
                            gb["name"], int(gexec), int(ginit)))
            continue
        if ginit:
            got = hashlib.md5(mem.block_bytes(pb)).hexdigest()
            if got != gb["md5"]:
                diffs.append("block %s @%x +%d: MD5 %s (pe) vs %s (ghidra)"
                             % (pb.name, gstart, gsize, got, gb["md5"]))
    for key, pb in pmap.items():
        if key not in gmap:
            msg = "pe-only block %s @%x +%d (x=%d)" % (pb.name, pb.start, pb.size, pb.execute)
            (exempt if irrelevant(pb.start, pb.size, pb.execute) else diffs).append(msg)
    return (not diffs), diffs, exempt


def main():
    ap = argparse.ArgumentParser()
    # Defaults to the COMMITTED maps, not a scratch dir: they are ~60 KB, and committing them is
    # what lets this run on a machine with the archive but no Ghidra — and what turns
    # `pe_memory.py` into something with a regression oracle instead of a rule someone believed.
    ap.add_argument("--blocks", default=os.path.join(REPO, "tools", "ghidra", "memory-maps"),
                    help="dir of blocks_*.tsv from dump_blocks.java")
    ap.add_argument("--roots", nargs="*", default=None)
    ap.add_argument("--map", default=None, help="write the verified tag/prog -> PE path map here")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()
    roots = args.roots or DEFAULT_ROOTS

    files = sorted(f for f in os.listdir(args.blocks)
                   if f.startswith("blocks_") and f.endswith(".tsv"))
    if not files:
        print("no blocks_*.tsv in %s — regenerate with\n"
              "   SWEEP_SCRIPT=dump_blocks.java SWEEP_OUT=$PWD/out/blocks-dump "
              "bash tools/ghidra/sweep.sh" % args.blocks)
        return 1
    print("blocks dir : %s (%d programs)" % (args.blocks, len(files)))
    print("roots      : %s" % ", ".join(roots))
    print("indexing PE names ...")
    names = index_names(roots)
    print("   %d distinct PE names\n" % len(names))

    tally, rows, failures, exemptions = collections.Counter(), [], [], []
    for fn in files:
        meta, blocks = parse_blocks_tsv(os.path.join(args.blocks, fn))
        tag, prog = meta.get("tag", ""), meta.get("program", "")
        ib_str = meta.get("image_base_str", "")
        # A file still being written by a running dump has no meta and no rows. Report it as
        # INCOMPLETE rather than as a program that could not be matched — the two look identical
        # in a summary and mean completely different things.
        if not prog or not blocks:
            tally["INCOMPLETE"] += 1
            failures.append((fn, "", "no program/blocks — dump still running or file truncated"))
            print("  !! %-28s %-46s INCOMPLETE" % (fn, ""))
            continue
        exec_init = sum(int(b["size"]) for b in blocks
                        if b["exec"] == "1" and b["init"] == "1")
        # The stub re-imports: image base 0000:0000, ~1 KB of DOS header mapped as code. They are
        # Ghidra artifacts with no PE counterpart, and the sweep's own results for them are
        # meaningless (scan_patterns.java skips a program only at exec_bytes == 0).
        #
        # THE SEGMENTED BASE IS THE TEST, NOT THE SIZE. An "under N bytes of code" threshold looks
        # equivalent and is not: Satisfactory ships real DLLs with 6.5 KB and 28 KB of .text
        # (`-InputDevice-`, `-AudioMixerCore-`), and skipping those would quietly shrink the
        # corpus while the summary still read as a clean pass. A broken import that somehow got a
        # normal base will fail below instead — which is the right outcome, not a skip.
        if ":" in ib_str:
            tally["SKIP-BROKEN"] += 1
            print("  -- %-28s %-46s SKIP-BROKEN (base %s, %d exec bytes)"
                  % (tag, prog, ib_str, exec_init))
            continue
        cands = names.get(prog.lower(), [])
        if not cands:
            tally["NO-CANDIDATE"] += 1
            failures.append((tag, prog, "no file named %r under the roots" % prog))
            print("  !! %-28s %-46s NO-CANDIDATE" % (tag, prog))
            continue
        best_diffs, hit, hit_exempt = None, None, []
        for c in cands:
            try:
                mem = PeMemory(c)
            except Exception as e:                        # not a PE32+, truncated, unreadable
                if best_diffs is None:
                    best_diffs = ["%s: %s" % (c, e)]
                continue
            ok, diffs, ex = compare(mem, blocks)
            if ok:
                hit, hit_exempt = c, ex
                break
            if best_diffs is None or len(diffs) < len(best_diffs):
                best_diffs = ["%s:" % c] + diffs
        if hit:
            tally["EXACT"] += 1
            rows.append((tag, prog, ib_str, hit))
            for e in hit_exempt:
                exemptions.append((tag, prog, e))
            note = "  (+%d unreachable block(s))" % len(hit_exempt) if hit_exempt else ""
            print("  OK %-28s %-46s %s%s" % (tag, prog, hit, note))
        else:
            tally["MISMATCH"] += 1
            failures.append((tag, prog, "; ".join(best_diffs[:6]) if best_diffs else "?"))
            print("  !! %-28s %-46s MISMATCH over %d candidate(s)" % (tag, prog, len(cands)))
            if args.verbose and best_diffs:
                for d in best_diffs[:10]:
                    print("        %s" % d)

    print("\n=== summary ===")
    for k, v in tally.most_common():
        print("   %-14s %d" % (k, v))
    if exemptions:
        # Listed, not hidden. Each of these was accepted because it is non-executable AND outside
        # a signed-32-bit displacement of every executable block, so no scan can reach it.
        print("\n=== accepted as unreachable (non-exec, outside the RIP window) ===")
        for tag, prog, e in exemptions:
            print("   %-28s %-40s %s" % (tag, prog, e))
    if failures:
        print("\n=== not reproduced ===")
        for tag, prog, why in failures:
            print("   %s / %s\n      %s" % (tag, prog, why))
    if args.map and rows:
        os.makedirs(os.path.dirname(os.path.abspath(args.map)), exist_ok=True)
        with io.open(args.map, "w", encoding="utf-8", newline="\n") as f:
            f.write("tag\tprogram\timage_base\tpe_path\n")
            for r in rows:
                f.write("\t".join(r) + "\n")
        print("\n   -> %s (%d verified programs)" % (args.map, len(rows)))
    if failures:
        print("\n   NOT equivalent: the rows above are not reproducible from a PE alone.")
        return 1
    print("\n   EQUIVALENT: every non-broken program's image base, block map and block BYTES are")
    print("   reproduced from its PE. The sweep does not need Ghidra to read these programs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
