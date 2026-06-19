#!/usr/bin/env python3
"""Minimal Windows minidump triage: modules + exception + faulting-thread stack walk.

No deps. Enough to answer "what was the loader doing when it AV'd".
Usage: minidump_triage.py <dump.dmp>
"""
import struct, sys

STREAM = {3:"ThreadList",4:"ModuleList",5:"MemoryList",6:"Exception",7:"SystemInfo",
          9:"Memory64List",16:"MemoryInfoList"}

def u32(d,o): return struct.unpack_from("<I",d,o)[0]
def u64(d,o): return struct.unpack_from("<Q",d,o)[0]

def mdstring(d, rva):
    n = u32(d, rva)
    return d[rva+4:rva+4+n].decode("utf-16-le", "replace")

def main(path):
    d = open(path,"rb").read()
    assert d[:4]==b"MDMP", "not a minidump"
    nstreams = u32(d, 8); dirrva = u32(d, 12)
    streams = {}
    for i in range(nstreams):
        b = dirrva + i*12
        st, sz, rva = u32(d,b), u32(d,b+4), u32(d,b+8)
        streams.setdefault(st, (sz, rva))

    # ---- Modules ----
    modules = []  # (base, size, name)
    if 4 in streams:
        sz, rva = streams[4]
        n = u32(d, rva); p = rva+4
        for i in range(n):
            base = u64(d,p); size = u32(d,p+8); nameRva = u32(d,p+20)
            name = mdstring(d, nameRva)
            modules.append((base, size, name))
            p += 108
    modules.sort()
    def mod_of(addr):
        for base,size,name in modules:
            if base <= addr < base+size:
                return f"{name.split(chr(92))[-1]}+0x{addr-base:X}", name
        return f"0x{addr:X} <unknown>", None

    print(f"== {len(modules)} modules loaded ==")
    interesting = ("dxgi","version.dll","winmm","d3d11","d3d9","d3d12","opengl","ntdll",
                   "ue5dumper","kernelbase")
    for base,size,name in modules:
        low = name.lower()
        if any(k in low for k in interesting):
            print(f"  {base:016X} +{size:08X}  {name}")
    print("  -- (full path of any dxgi/version/winmm modules above) --")

    # ---- Exception ----
    fault_tid = None; rip=None; rsp=None; ctx_rva=None
    if 6 in streams:
        sz, rva = streams[6]
        fault_tid = u32(d, rva)
        exc = rva+8
        code = u32(d, exc); flags=u32(d,exc+4)
        excaddr = u64(d, exc+16)
        nparam = u32(d, exc+24)
        params = [u64(d, exc+32+8*i) for i in range(min(nparam,15))]
        # ThreadContext location after MINIDUMP_EXCEPTION (which is 8+4+4+8+8+4+4+15*8=152 bytes)
        tc = exc + 152
        ctx_sz = u32(d, tc); ctx_rva = u32(d, tc+4)
        desc, _ = mod_of(excaddr)
        print(f"\n== EXCEPTION ==")
        print(f"  thread id      : {fault_tid}")
        print(f"  exception code : 0x{code:08X}")
        print(f"  exception addr : 0x{excaddr:016X}  -> {desc}")
        if params:
            # for AV: param[0]=0 read /1 write /8 exec ; param[1]=address
            kind={0:'read',1:'write',8:'execute'}.get(params[0], str(params[0]))
            if len(params)>=2:
                tgt,_=mod_of(params[1])
                print(f"  AV: {kind} at 0x{params[1]:016X} -> {tgt}")
        # CONTEXT: Rsp@0x98, Rip@0xF8
        if ctx_rva:
            rsp = u64(d, ctx_rva+0x98); rip = u64(d, ctx_rva+0xF8)
            rd,_ = mod_of(rip)
            print(f"  context RIP    : 0x{rip:016X}  -> {rd}")
            print(f"  context RSP    : 0x{rsp:016X}")

    # ---- Memory64List: map VA -> file offset ----
    ranges = []  # (startVA, size, fileoff)
    if 9 in streams:
        sz, rva = streams[9]
        nr = u64(d, rva); baseRva = u64(d, rva+8)
        p = rva+16; off = baseRva
        for i in range(nr):
            start=u64(d,p); dsize=u64(d,p+8); p+=16
            ranges.append((start,dsize,off)); off += dsize
    elif 5 in streams:
        sz, rva = streams[5]
        nr = u32(d, rva); p=rva+4
        for i in range(nr):
            start=u64(d,p); locsz=u32(d,p+8); locrva=u32(d,p+12); p+=16
            ranges.append((start,locsz,locrva))

    def read_va(va, length):
        for start,dsize,foff in ranges:
            if start <= va < start+dsize:
                k = va-start
                return d[foff+k: foff+k+min(length, dsize-k)]
        return None

    # ---- Stack walk (heuristic: scan stack for return-addr candidates) ----
    if rsp:
        print(f"\n== FAULTING-THREAD STACK SCAN (return-address candidates) ==")
        stk = read_va(rsp, 0x1800)
        if not stk:
            print("  (stack memory not in dump)")
        else:
            seen=0
            for i in range(0, len(stk)-8, 8):
                val = struct.unpack_from("<Q", stk, i)[0]
                desc, modname = mod_of(val)
                if modname and modname.lower().endswith(("ntdll.dll","dxgi.dll","version.dll",
                        "winmm.dll","d3d11.dll","d3d9.dll","ue5dumper.dll","kernelbase.dll",
                        "kernel32.dll")):
                    print(f"  rsp+0x{i:04X}: 0x{val:016X}  {desc}")
                    seen+=1
                    if seen>40: break

if __name__=="__main__":
    main(sys.argv[1])
