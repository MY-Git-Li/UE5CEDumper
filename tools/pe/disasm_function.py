#!/usr/bin/env python3
"""disasm_function.py - offline x64 disassembler for a function in a PE, with .data ref annotation.

Disassembles each given function VA, and for every RIP-relative operand that resolves into
the module's writable .data, prints the target + whether it is in the zero-init BSS tail
(VirtualSize > SizeOfRawData) - which is where runtime-filled globals like GUObjectArray live.
This is the "read the FUObjectArray cluster out of AllocateUObjectIndex" helper from the
non-standard-UE reversing workflow (see docs/reversing-nonstandard-ue-games.md).

Deps:  py -m pip install capstone pefile
Usage: py disasm_function.py <game.exe> <VA> [VA ...] [--len 0x200]
       py disasm_function.py game.exe 0x147A604E0 0x14814D2F0
"""
import sys, argparse, capstone, pefile

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("vas", nargs="+", help="image-based VAs, e.g. 0x147A604E0")
    ap.add_argument("--len", dest="length", default="0x200", help="bytes to disassemble per function")
    a = ap.parse_args()
    length = int(a.length, 0)

    pe = pefile.PE(a.exe, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    print("ImageBase = 0x%X" % base)

    # section table: (va_start, va_end, name, writable, executable, is_bss_at)
    secs = []
    for s in pe.sections:
        va = base + s.VirtualAddress
        vsize = max(s.Misc_VirtualSize, s.SizeOfRawData)
        secs.append((va, va + vsize, s.Name.rstrip(b"\x00").decode(errors="replace"),
                     bool(s.Characteristics & 0x80000000), bool(s.Characteristics & 0x20000000),
                     base + s.VirtualAddress + s.SizeOfRawData))  # raw data ends here; beyond = BSS

    def classify(va):
        for v0, v1, name, w, e, rawend in secs:
            if v0 <= va < v1:
                tag = name + ("/W" if w else "") + ("/X" if e else "")
                if w and not e and va >= rawend:
                    tag += " [BSS zero-init]"
                return tag
        return "?"

    def read(va, n):
        return pe.get_data(va - base, n)

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    for v in a.vas:
        fva = int(v, 0)
        print("\n=== disasm @ 0x%X (%s) ===" % (fva, classify(fva)))
        code = read(fva, length)
        for ins in md.disasm(code, fva):
            ann = ""
            for op in ins.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    tgt = ins.address + ins.size + op.mem.disp
                    cls = classify(tgt)
                    if "/W" in cls and "/X" not in cls:           # writable, non-exec .data
                        ann = "   -> 0x%X (%s)" % (tgt, cls)
            print("  +0x%03X  %-9s %s%s" % (ins.address - fva, ins.mnemonic, ins.op_str, ann))
            if ins.mnemonic in ("ret", "int3") and ins.address - fva > 0x40:
                break

if __name__ == "__main__":
    main()
