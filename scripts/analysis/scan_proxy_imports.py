"""Scan every installed UE game for the proxy-hijack target modules it imports.

Answers "would a <name>.dll proxy actually get loaded by this game?" across a
whole installed library at once. Mirrors what
ui/UE5DumpUI/Services/ProxyImportAnalyzer.cs reads (the standard import
directory AND the delay-load import directory), but over every game rather than
one exe. Written to settle the "4th proxy DLL (winmm)" evaluation in
docs/todo.md; re-run it before adding any new proxy flavour.

Three things this handles that a naive per-exe scan gets wrong — each one
changed the answer at least once:

  * NON-UE GAMES MUST BE EXCLUDED. A Steam library is full of them, and their
    imports say nothing about a UE proxy. `is_ue_tree` gates on real UE markers.
  * MODULAR UE BUILDS ship a thin bootstrap exe with the engine split across
    *-Win64-Shipping.dll modules (Satisfactory). Reading the exe alone reports
    "imports nothing at all"; the union over its modules is the real answer,
    because the loader searches the exe's directory whichever module asks.
  * LAUNCHER / REAL-EXE PAIRS. ff7rebirth.exe is a stub; ff7rebirth_.exe is the
    game. Helper exes (crash handlers, redists) are skipped by name.

Usage:  python scan_proxy_imports.py
Edit ROOTS below to match the machine (Steam library roots come from
steamapps/libraryfolders.vdf).
"""
import os
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TARGETS = ["version.dll", "dinput8.dll", "dxgi.dll", "winmm.dll",
           "dsound.dll", "xinput1_3.dll", "xinput1_4.dll", "d3d11.dll", "d3d12.dll"]

ROOTS = [
    r"C:\Program Files (x86)\Steam\steamapps\common",
    r"D:\SteamLibrary\steamapps\common",
    r"E:\SteamLibrary\steamapps\common",
    r"H:\SteamLibrary\steamapps\common",
    r"C:\XboxGames",
]

# Helper executables that ship alongside the game but are not the game.
SKIP_EXE = (
    "crashreportclient", "crashpad_handler", "crashreport", "unrealcefsubprocess",
    "epicwebhelper", "easyanticheat", "battleye", "be_service", "installermessage",
    "vc_redist", "dxsetup", "dotnet", "uninstall", "launcher_installer",
    "unrealpak", "shadercompileworker", "benchmark_launcher",
)
SKIP_DIRS = {"content", "paks", "_commonredist", "engine_", "redist", "directx",
             "movies", "shaders", "derivedatacache", "saved", "intermediate"}


def read_pe_imports(path):
    """Return the lowercase names of every imported module (standard + delay)."""
    with open(path, "rb") as fh:
        d = fh.read()
    if len(d) < 0x40 or d[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    if pe + 0x18 > len(d) or d[pe:pe + 4] != b"PE\0\0":
        return None
    magic = struct.unpack_from("<H", d, pe + 24)[0]
    dd = pe + 24 + (112 if magic == 0x20B else 96)
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    so = pe + 24 + struct.unpack_from("<H", d, pe + 20)[0]
    secs = []
    for i in range(nsec):
        o = so + i * 40
        if o + 40 > len(d):
            return None
        secs.append((struct.unpack_from("<I", d, o + 12)[0],
                     max(struct.unpack_from("<I", d, o + 8)[0], 1),
                     struct.unpack_from("<I", d, o + 20)[0]))

    def rva2off(r):
        for va, vs, pr in secs:
            if va <= r < va + vs:
                return pr + (r - va)
        return None

    found = set()
    # (data-directory index, descriptor stride, offset of the name RVA)
    for idx, stride, noff in ((1, 20, 12), (13, 32, 4)):
        if dd + idx * 8 + 4 > len(d):
            continue
        rva = struct.unpack_from("<I", d, dd + idx * 8)[0]
        if not rva:
            continue
        off = rva2off(rva)
        if off is None:
            continue
        for k in range(2000):
            e = off + k * stride
            if e + stride > len(d) or d[e:e + stride] == b"\0" * stride:
                break
            nr = struct.unpack_from("<I", d, e + noff)[0]
            if not nr:
                continue
            no = rva2off(nr)
            if no is None or no >= len(d):
                continue
            try:
                found.add(d[no:d.index(b"\0", no)].decode("ascii", "replace").lower())
            except ValueError:
                pass
    return found


def is_ue_tree(root):
    """UE markers: an Engine\\Binaries folder, a *-Shipping.exe, or */Content/Paks."""
    for dirpath, dirnames, filenames in walk(root, maxdepth=4):
        base = os.path.basename(dirpath).lower()
        if base == "binaries" and os.path.basename(os.path.dirname(dirpath)).lower() == "engine":
            return True
        if base == "paks" and "content" in dirpath.lower():
            return True
        for f in filenames:
            if f.lower().endswith("-shipping.exe"):
                return True
    return False


def walk(root, maxdepth=5):
    root_depth = root.rstrip("\\/").count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root):
        if dirpath.count(os.sep) - root_depth >= maxdepth:
            dirnames[:] = []
        dirnames[:] = [x for x in dirnames if x.lower() not in SKIP_DIRS]
        yield dirpath, dirnames, filenames


def pick_main_exe(root):
    """Prefer a *-Shipping.exe; else the largest non-helper exe."""
    shipping, others = [], []
    for dirpath, _dirnames, filenames in walk(root):
        for f in filenames:
            if not f.lower().endswith(".exe"):
                continue
            low = f.lower()
            if any(s in low for s in SKIP_EXE):
                continue
            p = os.path.join(dirpath, f)
            (shipping if low.endswith("-shipping.exe") else others).append(p)
    pool = shipping or others
    if not pool:
        return None
    try:
        return max(pool, key=os.path.getsize)
    except OSError:
        return pool[0]


def modular_union(game_root):
    """Union the imports of every *-Win64-Shipping.dll in a MODULAR UE build.

    A modular build (Satisfactory) ships a thin bootstrap exe with the engine
    split across DLLs, so reading the exe alone reports no graphics imports at
    all. What matters for proxy hijacking is whether ANY module in the process
    imports the name — the loader searches the exe's directory either way.
    """
    union = set()
    for dirpath, _dn, filenames in walk(game_root, maxdepth=6):
        for f in filenames:
            low = f.lower()
            if low.endswith("-win64-shipping.dll"):
                try:
                    union |= (read_pe_imports(os.path.join(dirpath, f)) or set())
                except OSError:
                    pass
    return union


def main():
    rows = []
    for root in ROOTS:
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            game = os.path.join(root, name)
            if not os.path.isdir(game):
                continue
            try:
                if not is_ue_tree(game):
                    continue
                exe = pick_main_exe(game)
                if not exe:
                    continue
                imps = read_pe_imports(exe)
                if not imps:
                    continue
                label = os.path.basename(exe)
                # No graphics import at all => the exe is a bootstrap stub for a
                # modular build; fall back to the union over its Shipping DLLs.
                if not ({"dxgi.dll", "d3d11.dll", "d3d12.dll"} & imps):
                    union = modular_union(game)
                    if union:
                        imps = imps | union
                        label += "  (+modular DLLs)"
            except (OSError, PermissionError):
                continue
            rows.append((name, label, [t for t in TARGETS if t in imps]))

    print("%-34s %-34s %s" % ("game", "exe", "imports"))
    print("-" * 120)
    for g, e, t in sorted(rows, key=lambda r: r[0].lower()):
        safe_g = g.encode("ascii", "replace").decode()
        print("%-34s %-34s %s" % (safe_g[:33], e[:33], ", ".join(t) or "(none)"))

    total = len(rows)
    print("\nUE games scanned: %d" % total)
    if total:
        print("\n%-14s %-8s %s" % ("module", "count", "coverage"))
        for t in TARGETS:
            n = sum(1 for _, _, l in rows if t in l)
            bar = "#" * round(n * 30 / total)
            print("%-14s %-8s %-30s %.0f%%" % (t, "%d/%d" % (n, total), bar, n * 100.0 / total))

        # Which games would the CURRENT three proxies fail to reach?
        cur = ("version.dll", "dinput8.dll", "dxgi.dll")
        gaps = [g for g, _, l in rows if not any(c in l for c in cur)]
        print("\nGames importing NONE of {version,dinput8,dxgi}: %d" % len(gaps))
        for g in gaps:
            print("   - %s" % g.encode("ascii", "replace").decode())
        gaps_w = [g for g, _, l in rows if not any(c in l for c in cur) and "winmm.dll" in l]
        print("...of which winmm WOULD reach: %d" % len(gaps_w))

        only_dxgi_missing = [g for g, _, l in rows if "dxgi.dll" not in l]
        print("\nGames without dxgi: %d" % len(only_dxgi_missing))
        for g in only_dxgi_missing:
            print("   - %s" % g.encode("ascii", "replace").decode())


if __name__ == "__main__":
    main()
