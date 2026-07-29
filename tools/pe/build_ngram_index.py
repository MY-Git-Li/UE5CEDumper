"""Build the AOB specificity index: a byte n-gram FREQUENCY TABLE, shipping no code.

WHAT THIS IS. Slide a fixed-width window over the executable sections and count how often each
distinct byte sequence occurs. The result is a flat {sequence: count} table — a word-frequency list
for machine code. There is no tree, no hierarchy, no relation between entries: `48 8B 05 A1` and
`8B 05 A1 B2` are unrelated keys even though they overlap in the source.

WHY IT CAN BE COMMITTED. Two properties, and the SECOND is the load-bearing one:
  1. A bag of n-grams has no positions.
  2. **Only sequences at or above `--threshold` are kept.** This is NOT merely a size optimisation.
     A COMPLETE overlapping n-gram table IS assemblable — de Bruijn / Eulerian path, exactly how DNA
     sequencing reconstructs a genome. Thresholding destroys that: the rare, distinctive sequences
     that carry the identity of the code are dropped BY CONSTRUCTION, and what survives is the
     generic MSVC codegen every UE binary shares. Never lower the threshold to 1 "for accuracy" —
     that is the property, not a knob.

WHAT IT ANSWERS. Given a candidate AOB, the frequency of its rarest literal window is a hard UPPER
BOUND on how many times the whole pattern can match: every occurrence of the pattern must contain
that window. Validated on all 151 Himmel.h patterns with ZERO upper-bound violations.

WHAT IT CANNOT ANSWER. Whether a pattern hits the RIGHT address. `GNAM_XX_1` bounds at a clean 57
and is DECOY-ONLY. This is a pre-filter; `Himmel.h` rule 5 keeps meaning the sweep.

SOURCES. Self-built stock-engine binaries only (docs/reference-builds.md) — never a shipped game.
Defaults to SHIPPING configs, because the question is "will this be noisy on a shipped game" and
Development/DebugGame carry different, much larger codegen. Adding more sources only ever LOOSENS
the bound (the union takes the max), never breaks it.

    py tools/pe/build_ngram_index.py -o tools/pe/aob-ngram-index.bin
    py tools/pe/build_ngram_index.py --include-all --threshold 8      # wider, bigger

Needs numpy, and runs only on the corpus machine. The QUERY tool (aob_specificity.py) is stdlib-only
by design so it works anywhere.
"""
import argparse
import glob
import gzip
import json
import os
import struct
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MAGIC = b"UEAOBNGX"
FORMAT_VERSION = 1
DEFAULT_NS = (4, 5, 6)
DEFAULT_ROOT = r"D:\UE_Analyze_Data\Varies Version builds"
MIN_GAME_EXE = 5_000_000          # below this it is the launcher stub, not the game


def exec_sections(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    out = []
    for i in range(nsec):
        o = pe + 24 + optsz + i * 40
        vsz, _va, rsz, ptr = struct.unpack_from("<IIII", d, o + 8)
        if struct.unpack_from("<I", d, o + 36)[0] & 0x20000000:
            out.append(d[ptr:ptr + min(rsz, vsz)])
    return out


def bucket_of(counts, np):
    """count -> 1-byte bucket, decoded later as (1 << bucket).

    MUST decode to an UPPER bound or the whole guarantee breaks: bit_length() gives the smallest b
    with count <= 2**b, so 2**b >= count always. At most 2x loose, never wrong. Reporting the
    bucket's LOWER edge would under-report and make the bound unsound."""
    b = np.zeros(counts.shape, dtype=np.uint8)
    v = counts.astype(np.uint64)
    cur = np.ones(counts.shape, dtype=np.uint64)
    for i in range(1, 64):
        cur = cur << np.uint64(1)
        b[(v > (cur >> np.uint64(1))) & (v <= cur)] = i
    b[v > cur] = 63
    return b


def count_ngrams(bufs, n, threshold, np):
    """-> (keys uint64 sorted, buckets uint8) for sequences occurring >= threshold."""
    keys_all = []
    for buf in bufs:
        if len(buf) <= n:
            continue
        a = np.frombuffer(buf, dtype=np.uint8)
        L = len(a) - n + 1
        v = np.zeros(L, dtype=np.uint64)
        for i in range(n):
            v = (v << np.uint64(8)) | a[i:i + L].astype(np.uint64)
        keys_all.append(v)
    if not keys_all:
        return np.empty(0, dtype=np.uint64), np.empty(0, dtype=np.uint8)
    v = np.concatenate(keys_all) if len(keys_all) > 1 else keys_all[0]
    del keys_all
    uniq, cnt = np.unique(v, return_counts=True)
    del v
    keep = cnt >= threshold
    return uniq[keep], bucket_of(cnt[keep], np)


def merge_max(a_keys, a_buck, b_keys, b_buck, np):
    """Union taking the MAX bucket per key — sound for 'any engine version in the union'."""
    if a_keys.size == 0:
        return b_keys, b_buck
    if b_keys.size == 0:
        return a_keys, a_buck
    k = np.concatenate([a_keys, b_keys])
    v = np.concatenate([a_buck, b_buck])
    order = np.argsort(k, kind="stable")
    k, v = k[order], v[order]
    # np.maximum.reduceat over runs of equal keys
    first = np.empty(k.shape, dtype=bool)
    first[0] = True
    np.not_equal(k[1:], k[:-1], out=first[1:])
    starts = np.flatnonzero(first)
    return k[starts], np.maximum.reduceat(v, starts)


def pick_sources(root, include_all):
    out = []
    for ver in sorted(os.listdir(root)):
        if not os.path.isdir(os.path.join(root, ver)):
            continue
        for g in sorted(glob.glob(os.path.join(root, ver, "**", "*.exe"), recursive=True)):
            if os.path.getsize(g) < MIN_GAME_EXE or f"{os.sep}Engine{os.sep}" in g:
                continue
            cfg = ("DebugGame" if "DebugGame" in g else
                   "Shipping" if "Shipping" in g else "Development")
            if not include_all and cfg != "Shipping":
                continue
            out.append((ver, cfg, g))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-o", "--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                        "aob-ngram-index.bin.gz"))
    ap.add_argument("--root", default=DEFAULT_ROOT)
    ap.add_argument("--threshold", type=int, default=16)
    ap.add_argument("--ns", default=",".join(str(x) for x in DEFAULT_NS))
    ap.add_argument("--include-all", action="store_true",
                    help="also index Development/DebugGame (looser bounds, bigger file)")
    ap.add_argument("--limit", type=int, default=0, help="first N sources only (for testing)")
    a = ap.parse_args()

    try:
        import numpy as np
    except ImportError:
        print("numpy required to BUILD the index (the query tool needs nothing).")
        return 1
    if a.threshold < 2:
        print("refusing threshold < 2 — thresholding is what makes the artifact "
              "non-reconstructive, not a tuning knob. See this file's header.")
        return 1

    ns = tuple(int(x) for x in a.ns.split(","))
    if any(n < 3 or n > 8 for n in ns):
        print("n must be 3..8 (packed into a uint64)")
        return 1

    srcs = pick_sources(a.root, a.include_all)
    if a.limit:
        srcs = srcs[:a.limit]
    if not srcs:
        print(f"no sources under {a.root}")
        return 1
    print(f"sources: {len(srcs)}  (threshold {a.threshold}, n={ns}, "
          f"{'ALL configs' if a.include_all else 'Shipping only'})")

    tables = {n: (np.empty(0, dtype=np.uint64), np.empty(0, dtype=np.uint8)) for n in ns}
    used = []
    t0 = time.time()
    for i, (ver, cfg, path) in enumerate(srcs, 1):
        bufs = exec_sections(path)
        mb = sum(len(b) for b in bufs) / 1e6
        print(f"  [{i}/{len(srcs)}] {ver:<7} {cfg:<12} {mb:7.1f} MB  {os.path.basename(path)}")
        for n in ns:
            k, b = count_ngrams(bufs, n, a.threshold, np)
            tables[n] = merge_max(*tables[n], k, b, np)
            print(f"        n={n}: +{k.size:>9,}  union {tables[n][0].size:>10,}")
        used.append({"engine": ver, "config": cfg, "exec_mb": round(mb, 1),
                     "binary": os.path.basename(path)})
        del bufs

    meta = {
        "format": "UE5CEDumper AOB n-gram specificity index",
        "format_version": FORMAT_VERSION,
        "threshold": a.threshold,
        "ns": list(ns),
        "sources": used,
        "contains": "byte-sequence FREQUENCIES ONLY — no code, no addresses, no symbols",
        "non_reconstructive_because":
            "only sequences occurring >= threshold are kept, so the rare distinctive sequences a "
            "de Bruijn assembly would need are absent by construction",
        "bound_semantics":
            "stored bucket b decodes to (1<<b), an UPPER bound on the true count; a key absent from "
            "a table means its count is < threshold, so the bound is (threshold-1)",
    }
    blob = json.dumps(meta, ensure_ascii=False, indent=1).encode("utf-8")

    # gzip halves it (20.8 -> 10.3 MB) and `gzip` is stdlib, so the query tool stays
    # dependency-free. Keys are high-entropy, so this is most of what is winnable.
    opener = gzip.open if a.out.endswith(".gz") else open
    with opener(a.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<HHI", FORMAT_VERSION, len(ns), len(blob)))
        for n in ns:
            f.write(struct.pack("<BBII", n, 0, a.threshold, int(tables[n][0].size)))
        f.write(blob)
        for n in ns:
            k, b = tables[n]
            # big-endian key bytes so lexicographic byte order == numeric order, which lets the
            # query tool binary-search the raw records with no unpacking.
            kb = k.astype(">u8").tobytes()
            recs = bytearray(k.size * (n + 1))
            for j in range(n):
                recs[j::n + 1] = kb[8 - n + j::8]
            recs[n::n + 1] = b.tobytes()
            f.write(bytes(recs))

    sz = os.path.getsize(a.out)
    print(f"\nwrote {a.out}  {sz/2**20:.1f} MB   in {time.time()-t0:.0f}s")
    for n in ns:
        print(f"   n={n}: {tables[n][0].size:,} records")
    return 0


if __name__ == "__main__":
    sys.exit(main())
