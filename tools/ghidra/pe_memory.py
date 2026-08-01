"""Ghidra's memory model of a PE, reconstructed from the PE file alone — no Ghidra, no JVM.

WHY THIS EXISTS. `scan_patterns.java` touches four Ghidra APIs and zero analysis APIs, so the
sweep sees a program as nothing but `image base + block map + bytes`. If that view is derivable
from the PE, the 181 GB Ghidra corpus is not an input to the sweep at all — the game binaries are,
and those we already keep (`docs/corpus-preservation.md`).

THE MODEL IS A CLAIM, NOT AN ASSUMPTION. `tools/ghidra/dump_blocks.java` emits the same map (plus
an MD5 per block) straight out of Ghidra, and `tools/ghidra/check_pe_memory.py` diffs the two. The
hash is the part that matters: equal starts and sizes prove only that the LAYOUT agrees, while an
equal digest proves the BYTES do.

THE ONE RULE THAT IS NOT THE OBVIOUS ONE. A section's initialized block is
`max(SizeOfRawData, VirtualSize)` long, zero-padded when the virtual size is the larger — NOT
`min(...)`, and not either field alone. It was derived, not assumed:

  * `min(raw, virtual)` — what `replay_patterns.py` uses — is short by 24-450 bytes on nearly
    every section, because raw is virtual rounded up to the file alignment. That is why that tool
    corroborates and is not byte-exact.
  * raw alone reproduces Ghidra's `exec bytes` on 28 of 29 measured programs, which is exactly the
    kind of agreement that looks conclusive and is not. The 29th is DQ7R, a packed build whose
    `.debug` section is executable with `vsz` 1024 bytes LARGER than `rsz`; Ghidra scans the
    virtual size there, and only `max` fits both cases.

There is no separate uninitialized tail block for a short-raw section, so a read inside the padded
region succeeds and yields zero rather than raising — which happens to be the same value the
caller would have recorded either way, but is not the same block map.
"""
import struct

IMAGE_SCN_MEM_EXECUTE = 0x20000000
IMAGE_SCN_MEM_READ = 0x40000000
IMAGE_SCN_MEM_WRITE = 0x80000000


class Block:
    """One Ghidra MemoryBlock.

    `file_off` is None for an uninitialized block. `raw_len` is how much of an initialized block
    the FILE actually supplies — the rest is zero padding, which is a real part of the block and
    not an absence of one (see the module docstring)."""

    __slots__ = ("name", "start", "size", "execute", "initialized", "read", "write", "file_off",
                 "raw_len")

    def __init__(self, name, start, size, execute, initialized, read, write, file_off,
                 raw_len=None):
        self.name = name
        self.start = start
        self.size = size
        self.execute = execute
        self.initialized = initialized
        self.read = read
        self.write = write
        self.file_off = file_off
        self.raw_len = size if raw_len is None else min(raw_len, size)

    @property
    def end(self):
        return self.start + self.size

    def __repr__(self):
        return "Block(%s @%x +%d x=%d i=%d)" % (
            self.name, self.start, self.size, self.execute, self.initialized)


class PeMemory:
    """The block map plus byte access, with Ghidra's semantics for the three lookups the sweep
    performs: `getBlock` (any block), `contains` (any block), `getLong` (initialized only)."""

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        if d[:2] != b"MZ":
            raise ValueError("%s: not a PE (no MZ)" % path)
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        if d[pe:pe + 4] != b"PE\0\0":
            raise ValueError("%s: not a PE (no PE00 at e_lfanew)" % path)
        nsec = struct.unpack_from("<H", d, pe + 6)[0]
        optsz = struct.unpack_from("<H", d, pe + 20)[0]
        magic = struct.unpack_from("<H", d, pe + 24)[0]
        if magic != 0x20B:
            raise ValueError("%s: not PE32+ (magic %04x)" % (path, magic))
        self.image_base = struct.unpack_from("<Q", d, pe + 24 + 24)[0]
        self.size_of_headers = struct.unpack_from("<I", d, pe + 24 + 60)[0]

        blocks = [Block("Headers", self.image_base, self.size_of_headers,
                        False, True, True, False, 0)]
        tbl = pe + 24 + optsz
        for i in range(nsec):
            o = tbl + i * 40
            name = d[o:o + 8].rstrip(b"\0").decode("latin1")
            vsz, rva, rsz, ptr = struct.unpack_from("<IIII", d, o + 8)
            ch = struct.unpack_from("<I", d, o + 36)[0]
            ex, rd, wr = bool(ch & IMAGE_SCN_MEM_EXECUTE), bool(ch & IMAGE_SCN_MEM_READ), \
                bool(ch & IMAGE_SCN_MEM_WRITE)
            va = self.image_base + rva
            if ptr and rsz:
                blocks.append(Block(name, va, max(rsz, vsz), ex, True, rd, wr, ptr, raw_len=rsz))
            elif vsz:
                blocks.append(Block(name, va, vsz, ex, False, rd, wr, None))
        blocks.sort(key=lambda b: b.start)
        self.blocks = blocks

    # -- Ghidra Memory lookups -------------------------------------------------------------
    def get_block(self, va):
        """Memory.getBlock(addr) — the block containing `va`, initialized or not, else None."""
        lo, hi = 0, len(self.blocks) - 1
        while lo <= hi:
            m = (lo + hi) // 2
            b = self.blocks[m]
            if va < b.start:
                hi = m - 1
            elif va >= b.end:
                lo = m + 1
            else:
                return b
        return None

    def contains(self, va):
        return self.get_block(va) is not None

    def get_long(self, va):
        """Memory.getLong(addr) — SIGNED 64-bit LE, or None where Ghidra would raise
        MemoryAccessException (outside memory, or inside an uninitialized block).

        Signedness is load-bearing: the value goes into the candidate list and is compared for
        equality against the truth VAs, so reading it unsigned would silently change verdicts on
        any qword with the top bit set."""
        b = self.get_block(va)
        if b is None or not b.initialized:
            return None
        off = va - b.start
        if off + 8 > b.size:
            # Ghidra CAN read across two adjacent initialized blocks, so this is stricter than
            # Memory.getLong in principle. It is unreachable on a PE: section VAs are
            # section-aligned while raw sizes are file-aligned, so consecutive initialized blocks
            # always have a gap.
            return None
        if off + 8 > b.raw_len:                   # wholly or partly in the zero padding
            chunk = self.data[b.file_off + off:b.file_off + min(off + 8, b.raw_len)]
            return struct.unpack("<q", chunk.ljust(8, b"\0"))[0]
        (v,) = struct.unpack_from("<q", self.data, b.file_off + off)
        return v

    def block_bytes(self, b):
        if not b.initialized:
            return bytes(b.size)
        raw = self.data[b.file_off:b.file_off + b.raw_len]
        return raw if b.raw_len == b.size else raw.ljust(b.size, b"\0")

    def exec_blocks(self):
        """Exactly what scan_patterns.java scans: executable AND initialized, in address order."""
        return [b for b in self.blocks if b.execute and b.initialized]
