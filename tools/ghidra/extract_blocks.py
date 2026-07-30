"""Extract AOB shape blocks — small byte windows around the sites patterns actually land on.

WHY BLOCKS AS WELL AS THE N-GRAM INDEX. They answer different questions and neither substitutes:
    index  -> "how NOISY is this candidate?"     (density; docs/aob-block-library-eval.md §6)
    blocks -> "does it still match the TRUE site, and still avoid the known decoys?"  (shape)
The repo has no pattern regression test between extract_patterns.py's dead-constant check and the
full sweep. Blocks fill that: `blocktest.py` runs in milliseconds with no Ghidra and no corpus.

SELF-BUILT SOURCES ONLY — a hard rule, not a preference. §1 of the eval could not clear whether
excerpts of third-party game binaries are redistributable, and §2 showed we do not need them. This
script REFUSES any source outside the self-built tree for that reason. A third-party site can still
be recorded by shape, with `bytes` omitted and only a sha256 kept, so anyone who owns that game can
regenerate it locally — that path is deliberately not automated here.

WHAT A BLOCK PROVES. Each records the window, its base VA, and the VA the match RESOLVES to. So
blocktest checks the strong property — "this pattern still resolves to the right global" — not the
weak one, "some bytes still match".

    py tools/ghidra/extract_blocks.py                     # -> tools/ghidra/blocks/*.json
    py tools/ghidra/extract_blocks.py --limit-oracles 4
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import replay_patterns as RP                               # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SWEEP = os.path.join(REPO, "tools", "ghidra", "sweep.sh")
MANIFEST = os.path.join(REPO, "tools", "ghidra", "corpus-manifest.json")
OUTDIR = os.path.join(REPO, "tools", "ghidra", "blocks")
# Window = MARGIN bytes before the match + the match itself + MARGIN after. Sized to the pattern,
# not a fixed 80 bytes: a block only has to let `blocktest.py` re-find the match and read its
# displacement, and every byte beyond that is engine code shipped for nothing. Fixed 24/56 cost
# 26.6 KB; pattern-relative costs 10.6 KB for an identical test. The small leading margin is
# deliberate — it keeps the match at a non-zero offset, so the test still exercises the SEARCH
# rather than matching at position 0 by construction.
MARGIN = 4


def sweep_rows():
    rows = []
    for ln in open(SWEEP, encoding="utf-8"):
        m = re.match(r'^"([^|]+)\|([^|]+)\|([^|]*)\|(.*)"$', ln.strip())
        if m:
            rows.append((m.group(1), m.group(2), m.group(4)))
    return rows


def parse_true(spec):
    """'GObjects=1478245d0|1478245e0,GNames=...' -> {target: {va, ...}}. Drops module prefixes."""
    out = {}
    for part in spec.split(","):
        part = part.strip()
        if not part or "=" not in part:
            continue
        key, vals = part.split("=", 1)
        tgt = key.split(":")[-1]
        out.setdefault(tgt, set()).update(int(v, 16) for v in vals.split("|") if v)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=OUTDIR)
    ap.add_argument("--tsv", default=os.path.join(REPO, "out", "sweep", "patterns.tsv"))
    ap.add_argument("--limit-oracles", type=int, default=0)
    ap.add_argument("--per-site", type=int, default=2,
                    help="max TRUE blocks and max DECOY blocks per (oracle, target)")
    a = ap.parse_args()

    man = json.load(open(MANIFEST, encoding="utf-8"))["entries"]
    rows = [r for r in RP_rows(a.tsv) if r["resolve"] in ("RipBoth", "RipDirect", "RipDeref")]
    print(f"{len(rows)} byte-scannable patterns")

    todo = []
    for tag, proj, spec in sweep_rows():
        ent = man.get(tag)
        if not ent or ent.get("source") != "self-built":
            continue                                        # LICENSING: self-built only
        path = ent.get("binary_last_seen")
        if not path or not os.path.isfile(path) or not spec.strip():
            continue
        truth = parse_true(spec)
        if truth:
            todo.append((tag, path, truth, ent))
    if a.limit_oracles:
        todo = todo[:a.limit_oracles]
    print(f"{len(todo)} self-built oracles with ground truth\n")

    os.makedirs(a.out, exist_ok=True)
    blocks, seen = [], set()
    for tag, path, truth, ent in todo:
        data = open(path, "rb").read()
        base, secs = RP.sections(data)
        execs = [s for s in secs if s[5] & 0x20000000]
        lo = min(s[1] for s in secs) + base
        hi = max(s[1] + max(s[2], s[4]) for s in secs) + base
        kept = {}
        for r in rows:
            tgt = r["target"]
            if tgt not in truth:
                continue
            # Skip the scan entirely once BOTH caps for this target are full. Without this the
            # extractor re-scans a 100-280 MB .text for every one of ~150 patterns on every oracle
            # — thousands of full passes, and it does not finish in a reasonable time.
            if (len(kept.get((tgt, "true"), [])) >= a.per_site
                    and len(kept.get((tgt, "decoy"), [])) >= a.per_site):
                continue
            cpat = RP.compile_pat(r["pattern"])
            io_, opc, tot, adj = int(r["io"]), int(r["opc"]), int(r["tot"]), int(r["adj"])
            for name, sva, vsz, ptr, rsz, _c in execs:
                if (len(kept.get((tgt, "true"), [])) >= a.per_site
                        and len(kept.get((tgt, "decoy"), [])) >= a.per_site):
                    break
                buf = data[ptr:ptr + min(rsz, vsz)]
                # finditer, NOT RP.scan(): that returns a LIST, so it materialises every match
                # before the caps can stop it. A 4-literal-byte pattern over a 280 MB DebugGame
                # .text yields millions, and the extraction never finishes. Stream and break.
                for _m in cpat.finditer(buf):
                    if (len(kept.get((tgt, "true"), [])) >= a.per_site
                            and len(kept.get((tgt, "decoy"), [])) >= a.per_site):
                        break
                    p = _m.start()
                    fo = ptr + p + io_ + opc
                    if fo + 4 > len(data):
                        continue
                    disp = struct.unpack_from("<i", data, fo)[0]
                    va = base + sva + p + io_ + tot + disp + adj
                    if not (lo <= va < hi):
                        continue
                    cls = "true" if va in truth[tgt] else "decoy"
                    k = (tgt, cls)
                    if len(kept.get(k, [])) >= a.per_site:
                        continue
                    plen = len(r["pattern"].split())
                    start = max(0, p - MARGIN)
                    win = buf[start:p + plen + MARGIN]
                    if len(win) < 16:
                        continue
                    h = hashlib.sha256(win).hexdigest()
                    if h in seen:
                        continue
                    seen.add(h)
                    kept.setdefault(k, []).append({
                        "target": tgt, "class": cls,
                        "window_va": base + sva + start,
                        "match_offset": p - start,
                        "resolves_to": va,
                        "bytes": win.hex(" "),
                        "found_by": r["id"],
                    })
        for (tgt, cls), items in sorted(kept.items()):
            for i, b in enumerate(items, 1):
                bid = f"{tgt}-{cls}-{tag}-{i:02d}"
                blocks.append(dict(
                    id=bid, engine=tag, provenance="self-built",
                    source_binary=os.path.basename(path),
                    window_va=hex(b["window_va"]), match_offset=b["match_offset"],
                    resolves_to=hex(b["resolves_to"]),
                    target=tgt, cls=cls, found_by=b["found_by"], bytes=b["bytes"]))
        print(f"  {tag:<28} {sum(len(v) for v in kept.values()):>3} blocks "
              f"({', '.join(f'{k[0]}/{k[1]}:{len(v)}' for k, v in sorted(kept.items()))})")
        del data

    doc = {
        "note": "AOB shape blocks. SELF-BUILT stock-engine binaries only — see "
                "docs/aob-block-library-eval.md §1/§2. Each block is a small window around a site "
                "a pattern matched, plus the VA that match RESOLVES to, so blocktest.py can assert "
                "resolution and not merely 'bytes still match'.",
        "window": {"margin": MARGIN, "sizing": "match length + margin on each side"},
        "blocks": blocks,
    }
    outp = os.path.join(a.out, "blocks.json")
    with open(outp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1)
        f.write("\n")
    print(f"\nwrote {outp}  {len(blocks)} blocks  {os.path.getsize(outp)/1024:.0f} KB")
    return 0


def RP_rows(tsv):
    rows = [l.rstrip("\n").split("\t") for l in open(tsv, encoding="utf-8")]
    hdr, rest = rows[0], rows[1:]
    return [dict(zip(hdr, r)) for r in rest]


if __name__ == "__main__":
    sys.exit(main())
