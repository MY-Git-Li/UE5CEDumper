#!/usr/bin/env python3
"""Extract every AOB signature from dll/src/Himmel.h into a TSV the Ghidra
verifier can consume:  id \t target \t resolve \t pattern \t io \t opc \t tot \t adj \t pri \t src
Symbol-export / symbol-call-follow entries are emitted with pattern kind SYMBOL.
"""
import re, sys, os

HIMMEL = sys.argv[1] if len(sys.argv) > 1 else r"D:\Github\UE5CEDumper\dll\src\Himmel.h"
OUT = sys.argv[2] if len(sys.argv) > 2 else "patterns.tsv"

src = open(HIMMEL, encoding="utf-8").read()

# strip // comments (patterns never contain //)
lines = []
for ln in src.splitlines():
    i = ln.find("//")
    if i >= 0:
        ln = ln[:i]
    lines.append(ln)
clean = "\n".join(lines)

# Drop #define directives (including backslash line-continuations). Without this the
# `SIG_RIP(id, pat, tgt, ...)` MACRO DEFINITION itself parses as a signature row, yielding a
# phantom entry with id="id", target="tgt" and pattern "<UNRESOLVED:pat>" — which inflated the
# reported pattern count by one and produced a spurious "SKIP unparsable" in every sweep.
_nodef, _skipping = [], False
for ln in clean.splitlines():
    if _skipping or ln.lstrip().startswith("#define"):
        _skipping = ln.rstrip().endswith("\\")
        _nodef.append("")
        continue
    _nodef.append(ln)
clean = "\n".join(_nodef)

# 1) constants: constexpr const char* AOB_X = "..." ("..." ...);
consts = {}
for m in re.finditer(r'constexpr\s+const\s+char\*\s+(\w+)\s*=\s*((?:"[^"]*"\s*)+);', clean):
    name = m.group(1)
    parts = re.findall(r'"([^"]*)"', m.group(2))
    consts[name] = "".join(parts)

# also EXPORT_* symbol constants
for m in re.finditer(r'constexpr\s+const\s+char\*\s+(EXPORT_\w+)\s*=\s*((?:"[^"]*"\s*)+);', clean):
    parts = re.findall(r'"([^"]*)"', m.group(2))
    consts[m.group(1)] = "".join(parts)

def split_args(s):
    out, depth, cur, instr = [], 0, "", False
    i = 0
    while i < len(s):
        c = s[i]
        if instr:
            cur += c
            if c == '"':
                instr = False
            elif c == '\\':
                cur += s[i+1]; i += 1
        elif c == '"':
            instr = True; cur += c
        elif c in "([{":
            depth += 1; cur += c
        elif c in ")]}":
            depth -= 1; cur += c
        elif c == "," and depth == 0:
            out.append(cur.strip()); cur = ""
        else:
            cur += c
        i += 1
    if cur.strip():
        out.append(cur.strip())
    return out

def unq(x):
    x = x.strip()
    if x.startswith('"'):
        return "".join(re.findall(r'"([^"]*)"', x))
    return x

USED_CONSTS = set()

def patof(tok):
    tok = tok.strip()
    if tok.startswith('"'):
        return unq(tok)
    USED_CONSTS.add(tok)
    return consts.get(tok, "<UNRESOLVED:%s>" % tok)

rows = []

# 2) macro forms
for m in re.finditer(r'\bSIG_RIP(_DIRECT)?\s*\(', clean):
    start = m.end()
    depth = 1; i = start
    while depth and i < len(clean):
        if clean[i] == '(': depth += 1
        elif clean[i] == ')': depth -= 1
        i += 1
    a = split_args(clean[start:i-1])
    if len(a) < 10: continue
    idn, pat, tgt, io, opc, tot, adj, pri, srcname, note = a[:10]
    rows.append(dict(id=unq(idn), target=tgt.split("::")[-1],
                     resolve="RipDirect" if m.group(1) else "RipBoth",
                     pattern=patof(pat), io=io, opc=opc, tot=tot, adj=adj, pri=pri,
                     src=unq(srcname), note=unq(note)))

for m in re.finditer(r'\bSIG_GWORLD_RIP\s*\(', clean):
    start = m.end()
    depth = 1; i = start
    while depth and i < len(clean):
        if clean[i] == '(': depth += 1
        elif clean[i] == ')': depth -= 1
        i += 1
    a = split_args(clean[start:i-1])
    if len(a) < 10: continue
    idn, pat, io, opc, tot, adj, pri, allownull, srcname, note = a[:10]
    rows.append(dict(id=unq(idn), target="GWorld", resolve="RipBoth",
                     pattern=patof(pat), io=io, opc=opc, tot=tot, adj=adj, pri=pri,
                     src=unq(srcname), note=unq(note)))

for m in re.finditer(r'\bSIG_(EXPORT|SYM_CALL)\s*\(', clean):
    start = m.end()
    depth = 1; i = start
    while depth and i < len(clean):
        if clean[i] == '(': depth += 1
        elif clean[i] == ')': depth -= 1
        i += 1
    a = split_args(clean[start:i-1])
    if len(a) < 5: continue
    idn, sym, tgt, pri, note = a[:5]
    rows.append(dict(id=unq(idn), target=tgt.split("::")[-1],
                     resolve="SymbolExport" if m.group(1) == "EXPORT" else "SymbolCallFollow",
                     pattern=patof(sym), io=0, opc=0, tot=0, adj=0, pri=pri,
                     src="EXP", note=unq(note)))

# 3) raw brace initialisers: { "ID", AOB_X, AobTarget::Y, AobResolve::Z, io, opc, tot, adj, pri, callOff, bool, "src", "note" }
for m in re.finditer(r'\{\s*("(?:[^"]*)")\s*,\s*(\w+)\s*,\s*AobTarget::(\w+)\s*,\s*AobResolve::(\w+)\s*,', clean):
    start = m.start()
    depth = 0; i = start
    while i < len(clean):
        if clean[i] == '{': depth += 1
        elif clean[i] == '}':
            depth -= 1
            if depth == 0: break
        i += 1
    a = split_args(clean[start+1:i])
    if len(a) < 12: continue
    rows.append(dict(id=unq(a[0]), target=a[2].split("::")[-1], resolve=a[3].split("::")[-1],
                     pattern=patof(a[1]), io=a[4], opc=a[5], tot=a[6], adj=a[7], pri=a[8],
                     src=unq(a[11]) if len(a) > 11 else "?",
                     note=unq(a[12]) if len(a) > 12 else ""))

seen = set()
uniq = []
for r in rows:
    if r["id"] in seen:
        continue
    seen.add(r["id"])
    uniq.append(r)

def ev(x):
    if isinstance(x, int):
        return x
    x = str(x).strip()
    try:
        return int(x, 0)
    except Exception:
        return 0

with open(OUT, "w", encoding="utf-8") as f:
    f.write("id\ttarget\tresolve\tio\topc\ttot\tadj\tpri\tsrc\tpattern\tnote\n")
    for r in sorted(uniq, key=lambda r: (r["target"], ev(r["pri"]))):
        f.write("\t".join([r["id"], r["target"], r["resolve"], str(ev(r["io"])), str(ev(r["opc"])),
                           str(ev(r["tot"])), str(ev(r["adj"])), str(ev(r["pri"])), r["src"],
                           r["pattern"], r["note"]]) + "\n")

from collections import Counter
c = Counter((r["target"], r["resolve"]) for r in uniq)
print("total:", len(uniq))
for k, v in sorted(c.items()):
    print("  ", k, v)
bad = [r["id"] for r in uniq if "<UNRESOLVED" in r["pattern"]]
if bad:
    print("UNRESOLVED:", bad)

# DEAD CONSTANTS — declared but never referenced by any PATTERNS[] array, so never scanned for.
# This has bitten twice: AOB_GNAMES_UD1 (a UEDumper example pinning `cmp [rbp-0x18],0`, a stack
# slot) and AOB_GOBJECTS_CT2 (a bare MSVC prologue with no RIP operand at all, so it could not
# have resolved anything even if wired up). Both sat in the file for a long time looking like
# active signatures. Deliberate exceptions are whitelisted below.
NOT_IN_TABLES = {
    # Consumed directly by Genau::ResolveNameKeyTable — it de-obfuscates FNameEntry payloads
    # rather than resolving a global pointer, so it is not an AobSignature at all.
    "AOB_NAMEDECRYPT_ME1",
}
declared = {n for n in consts if n.startswith("AOB_")}
dead = sorted(declared - USED_CONSTS - NOT_IN_TABLES)
if dead:
    print("DEAD (declared but in no PATTERNS[] array — remove or wire up):")
    for d in dead:
        print("   ", d)
print("->", os.path.abspath(OUT))
