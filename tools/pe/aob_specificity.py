"""How noisy will this AOB be? Answered offline, with no corpus and no Ghidra.

    py tools/pe/aob_specificity.py "48 8b 05 ?? ?? ?? ?? 48 85 c0 74 ??"
    py tools/pe/aob_specificity.py --tsv out/sweep/patterns.tsv        # score the whole DB

STDLIB ONLY, ON PURPOSE. Building the index needs numpy and the 200 GB corpus; querying it needs
neither, so this CAN run on a bare second machine and in CI. That portability is the entire point —
see docs/aob-block-library-eval.md §6.

⚠ IT IS NOT ACTUALLY WIRED INTO CI, AND NOTHING ELSE READS IT EITHER (audited 2026-08-01).
.github/workflows/ci.yml runs extract_patterns.py --check, blocktest.py and
check_live_verification.py — not this. Repo-wide there are ZERO callers in dll/, ui/, scripts/ or
build.ps1; the only invocation path is a human typing the command above. Step 5 of the eval's own
build order ("gate authoring on it: a candidate clears the pre-filter before it earns a sweep"),
which is what would make it load-bearing, was never built. Read every number below as describing a
tool that is currently advisory only.

WHAT THE NUMBER MEANS. Every occurrence of the whole pattern must contain each of its literal
windows, so the frequency of the RAREST window is an UPPER BOUND on the pattern's hit count *in the
code the index was built from*. Deliberately loose — wildcards constrain far more than literal runs
do — so read it as "at most this many", never as an estimate.

⚠ THE BOUND IS A PROOF ONLY ON THE INDEX'S OWN SOURCES, AND A PRIOR EVERYWHERE ELSE. Measured:

    on the 11 source binaries      0 violations / 1,017 pairs      (proof)
    on 58 binaries never indexed   27 violations / 7,345  (0.37%)  (prior)
      of which CLEAR-verdict        6 violations / 3,055  (0.20%)  median 0, 99th pct 10, MAX 932

The failures are NOT a version problem — a 4.27-only index bounds a 5.4 binary with 0 violations
in 113. They are a CODE-COVERAGE problem: the index is built from content-free stock templates, so
it has seen UE engine code and essentially no game code, while a shipped title adds 100+ MB of
studio code. Licensing means that half can never be indexed, so this limit is STRUCTURAL and
permanent, not a tuning knob. GNAM_UD2 bounds at 15 and takes 932 hits on FF7 Remake.

WHAT IT CANNOT TELL YOU: whether the pattern hits the RIGHT address. `GNAM_XX_1` bounds at a clean
57 and is DECOY-ONLY. This is a PRE-FILTER, not an acceptance gate — `Himmel.h` rule 5 keeps meaning
the sweep. A quiet pattern that resolves to a decoy is still worthless.
"""
import gzip
import json
import os
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MAGIC = b"UEAOBNGX"
GZIP_MAGIC = bytes((0x1F, 0x8B))
_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_INDEX = next((p for p in (os.path.join(_HERE, "aob-ngram-index.bin.gz"),
                                  os.path.join(_HERE, "aob-ngram-index.bin"))
                      if os.path.exists(p)), os.path.join(_HERE, "aob-ngram-index.bin.gz"))


class Index:
    def __init__(self, path):
        # Sniff gzip rather than trusting the extension, so a renamed or
        # uncompressed index still loads.
        with open(path, "rb") as f:
            head = f.read(2)
        opener = gzip.open if head == GZIP_MAGIC else open
        with opener(path, "rb") as f:
            self.buf = f.read()
        if self.buf[:8] != MAGIC:
            raise ValueError(f"{path}: not an AOB n-gram index")
        ver, ntab, metalen = struct.unpack_from("<HHI", self.buf, 8)
        off = 16
        heads = []
        for _ in range(ntab):
            n, _r, thr, cnt = struct.unpack_from("<BBII", self.buf, off)
            heads.append((n, thr, cnt))
            off += 10
        self.meta = json.loads(self.buf[off:off + metalen].decode("utf-8"))
        off += metalen
        self.tables = {}
        for n, thr, cnt in heads:
            self.tables[n] = (off, cnt, thr, n + 1)
            off += cnt * (n + 1)
        self.ns = sorted(self.tables)
        self.threshold = heads[0][1] if heads else 0

    def lookup(self, key):
        """-> upper bound on the count of this exact byte sequence."""
        n = len(key)
        t = self.tables.get(n)
        if t is None:
            return None
        base, cnt, thr, stride = t
        lo, hi = 0, cnt
        while lo < hi:                                    # records are sorted by big-endian key
            mid = (lo + hi) // 2
            p = base + mid * stride
            k = self.buf[p:p + n]
            if k < key:
                lo = mid + 1
            elif k > key:
                hi = mid
            else:
                return 1 << self.buf[p + n]               # bucket decodes to an UPPER bound
        return thr - 1                                    # absent => below threshold


def literal_runs(pat):
    """Runs of fully-literal bytes. A nibble wildcard (`4?`) is NOT literal and breaks the run."""
    runs, cur = [], []
    for tok in pat.replace(",", " ").split():
        t = tok.strip()
        if not t:
            continue
        if "?" in t or "*" in t:
            if cur:
                runs.append(bytes(cur))
                cur = []
            continue
        try:
            cur.append(int(t, 16))
        except ValueError:
            raise SystemExit(f"cannot parse token {t!r} — expected hex byte or wildcard")
    if cur:
        runs.append(bytes(cur))
    return runs


def score(idx, pat):
    """-> (bound, n_used, limiting_window, longest_run_len, literal_byte_count)."""
    runs = literal_runs(pat)
    lit = sum(len(r) for r in runs)
    longest = max((len(r) for r in runs), default=0)
    usable = [n for n in idx.ns if n <= longest]
    if not usable:
        return None, 0, None, longest, lit
    n = max(usable)
    best, win = None, None
    for r in runs:
        for i in range(len(r) - n + 1):
            w = r[i:i + n]
            f = idx.lookup(w)
            if f is not None and (best is None or f < best):
                best, win = f, w
    return best, n, win, longest, lit


def verdict(bound, longest, lit, floor):
    """THE GUARANTEE IS ONE-DIRECTIONAL: this can CERTIFY a pattern quiet, and cannot CONDEMN one.

    Measured over 151 patterns x 11 source binaries (worst hits per pattern):

        CLEAR (bound <= floor)  n=47  median 0   90th  2   MAX   13
        UNPROVEN (bound > floor)      median 1   90th 33   MAX 3302

    So CLEAR is tight and trustworthy. Above the floor the bound is far too loose to mean anything:
    the buckets an earlier version called NOISY and VERY NOISY had MEDIANS of 0 and 2 — quieter than
    the bucket it called OK — because wildcards constrain a pattern enormously more than its literal
    windows do. That 5-level scale would have told you to reject GOBJ_ES53_1 (bound 2048, actually
    47 hits and the single best pattern in the corpus, priority 100, correct on 37 oracles).

    Hence two outcomes plus a structural one, and no invented gradations. Use it to pick which
    candidates to sweep FIRST, never to throw one away."""
    if longest < 4:
        return ("NO-ANCHOR", "no literal run reaches 4 bytes, so nothing can be established from "
                             "bytes alone — the pattern leans entirely on the validator and belongs "
                             "in a last-resort priority band. NOT a rejection: SPARSE_ES2_1 and "
                             "SPARSE_X1/X2 look like this, and the X pair is the ONLY thing "
                             "reaching sparse delegates on some non-Shipping builds.")
    if bound is None:
        return ("UNSCOREABLE", "no window the index can size. NOT the same as 'rare' — unknown.")
    if bound <= floor:
        return ("CLEAR", f"quiet in STOCK ENGINE CODE — under {floor + 1} occurrences of its rarest "
                         "window in every source binary (proven there: 0 violations / 1,017 pairs). "
                         "On a real game it is a STRONG PRIOR, not a proof: 0.20% violation rate "
                         "over 3,055 unseen pairs, median 0 and 99th pct 10 hits, but the tail is "
                         "real — GNAM_UD2 bounds at 15 and takes 932 on FF7 Remake.")
    return ("UNPROVEN", f"cannot certify (rarest window bounds at {bound}). This is NOT evidence "
                        "the pattern is noisy — the bound is very loose, and most patterns landing "
                        "here measure in single digits. Sweep it to find out.")


def report(idx, pat, label=None):
    bound, n, win, longest, lit = score(idx, pat)
    v, why = verdict(bound, longest, lit, idx.threshold - 1)
    print(f"\n  {label or pat}")
    if label:
        print(f"    pattern         {pat}")
    print(f"    literal bytes   {lit}   longest run {longest}")
    if win is not None:
        print(f"    limiting window {win.hex(' ')}   (n={n})")
    print(f"    upper bound     {'—' if bound is None else f'<= {bound}'} matches")
    print(f"    VERDICT         {v} — {why}")
    return v


def main():
    args = [a for a in sys.argv[1:]]
    idxpath = DEFAULT_INDEX
    if "--index" in args:
        i = args.index("--index")
        idxpath = args[i + 1]
        del args[i:i + 2]
    if not os.path.exists(idxpath):
        print(f"index not found: {idxpath}\nBuild it with tools/pe/build_ngram_index.py "
              f"(needs numpy + the corpus).")
        return 2
    idx = Index(idxpath)
    src = idx.meta.get("sources", [])
    print(f"index: {os.path.basename(idxpath)}  threshold {idx.threshold}  n={idx.ns}  "
          f"{len(src)} source binaries")

    if "--tsv" in args:
        tsv = args[args.index("--tsv") + 1]
        rows = [l.rstrip("\n").split("\t") for l in open(tsv, encoding="utf-8")]
        hdr, rows = rows[0], rows[1:]
        ix = {k: i for i, k in enumerate(hdr)}
        tally = {}
        print(f"\n{'pattern':<18} {'tgt':<16} {'bound':>10}  verdict")
        for r in sorted(rows, key=lambda r: r[ix["id"]]):
            b, _n, _w, lg, lt = score(idx, r[ix["pattern"]])
            v, _ = verdict(b, lg, lt, idx.threshold - 1)
            tally[v] = tally.get(v, 0) + 1
            print(f"  {r[ix['id']]:<16} {r[ix['target']]:<16} "
                  f"{('—' if b is None else b):>10}  {v}")
        print("\n  " + "   ".join(f"{k}={n}" for k, n in sorted(tally.items())))
        return 0

    if not args:
        print(__doc__)
        return 1
    for pat in args:
        report(idx, pat)
    return 0


if __name__ == "__main__":
    sys.exit(main())
