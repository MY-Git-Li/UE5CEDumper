#!/usr/bin/env python3
"""Mechanically generate AOB candidates from a dump_xrefs2 output file.

For every XREF, for every AOBk window, emit truncated candidates of several lengths.
Truncating from the END keeps `io` valid. Candidates are de-duplicated by pattern text.
The generated TSV is fed straight to scan_patterns.java, which reports
hits / ok / decoy / verdict for each — so selection is evidence-driven, not eyeballed.

usage: gen_cands.py <xrefs_file> <target> <opc> <tot> <adj> <out.tsv> [lengths...]
"""
import re, sys, os
from collections import OrderedDict

src, target, opc, tot, adj, out = sys.argv[1:7]
opc, tot, adj = int(opc), int(tot), int(adj)
lengths = [int(x) for x in sys.argv[7:]] or [16, 20, 24, 28, 32]

cands = OrderedDict()
cur_fn = "?"
cur_va = "?"
for line in open(src, encoding="utf-8", errors="replace"):
    line = line.rstrip("\n")
    if line.startswith("XREF\t"):
        f = line.split("\t")
        cur_va = f[2] if len(f) > 2 else "?"
        cur_fn = f[-1].replace("IN ", "") if len(f) > 4 else "?"
        continue
    m = re.match(r"^AOB(\d+)\tio=(\d+)\t(.*)$", line)
    if not m:
        continue
    io = int(m.group(2))
    toks = m.group(3).split()
    # the masked disp32 must be fully inside the kept prefix
    need = io + opc + 4
    for L in lengths:
        if L < need or L > len(toks):
            continue
        pref = toks[:L]
        # drop trailing wildcards — they add length but no specificity
        while pref and pref[-1] == "??":
            pref.pop()
        if len(pref) < need:
            continue
        # anchor rule: first token must be a full literal
        if "?" in pref[0]:
            continue
        # reject windows that are mostly padding
        if sum(1 for t in pref if t == "CC") > 6:
            continue
        pat = " ".join(pref)
        key = (pat, io)
        if key in cands:
            continue
        cands[key] = (cur_va, cur_fn)

with open(out, "w", encoding="utf-8") as f:
    f.write("id\ttarget\tresolve\tio\topc\ttot\tadj\tpri\tsrc\tpattern\tnote\n")
    for i, ((pat, io), (va, fn)) in enumerate(cands.items()):
        f.write("\t".join([
            "C%04d" % i, target, "RipDirect", str(io), str(opc), str(tot), str(adj),
            str(i), "GEN", pat, "%s @%s" % (fn, va)]) + "\n")

print("generated", len(cands), "candidates ->", os.path.abspath(out))
