"""Dump the first bytes of named functions, resolved via the PDB, straight out of the EXE.

Point: `#if !UE_BUILD_SHIPPING` removes a function's BODY but not the function - the symbol and
the UFUNCTION thunk both survive cooking, which is why "the symbol is there" proves nothing about
whether an exec actually does anything. The bytes do: a hollowed body is a bare `ret` (or a
few-byte epilogue), a live one is not. This measures it offline, with no game running.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pdb_globals as G                                    # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def main():
    pdb_path, want = sys.argv[1], sys.argv[2]
    exe = os.path.splitext(pdb_path)[0] + ".exe"
    syms = G.Pdb(pdb_path).symbols()
    base = G.image_base(exe)
    hits = sorted(k for k in syms if want in k)
    print(f"# {os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(pdb_path))))} "
          f"-> {len(hits)} match(es)")
    for k in hits[:14]:
        va = base + syms[k]
        b = G.read_at_va(exe, va, 16)
        # A hollowed virtual is typically just `ret` or `xor eax,eax; ret`, optionally after
        # int3 padding. Anything that sets up a frame / calls is a real body.
        verdict = "EMPTY(ret)" if b[:1] == b"\xc3" else (
            "tiny" if b"\xc3" in b[:4] else "has body")
        print(f"  {va:x}  {verdict:<10} {b.hex(' ')}  {k[:64]}")


main()
