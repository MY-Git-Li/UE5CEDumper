#!/usr/bin/env python3
"""Are all the UI string keys actually reachable — and is every reference defined?

    py tools/check_axaml_strings.py            # report + exit 1 on a problem
    py tools/check_axaml_strings.py --list     # just print the two sets, exit 0

Two directions, and they fail for different reasons:

  ORPHANS   a key defined in en.axaml that nothing references. Harmless at runtime, but
            the dictionary then overstates what is localizable — a translator (or the
            wiki's translation pass) spends effort on strings no user can ever see. An
            audit found 24, two of which were worse than dead: they were SHADOWED by
            hardcoded C# that computed the same text, so the file claimed a string was
            configurable when editing it changed nothing.

  DANGLING  a `{StaticResource str.…}` or Res.Get("str.…") with no definition. This one
            is a real bug: Avalonia raises at load time for a missing StaticResource, so
            it is a crash on whichever panel first uses it. There were ZERO at audit
            time, which is exactly the state worth keeping.

Deliberately a plain grep-style scan with no XML parser and no Avalonia dependency, for
the same reason as aob_specificity.py: it has to run in CI and on a bare checkout.

Audit #4 R6.
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UI = os.path.join(ROOT, "ui", "UE5DumpUI")
STRINGS = os.path.join(UI, "Resources", "Strings", "en.axaml")

KEY_DEF = re.compile(r'x:Key="(str\.[^"]+)"')
# Any mention of a str.* token in markup or code. Deliberately loose: a key referenced
# through string concatenation still counts as "someone meant to use it", and a false
# NEGATIVE here (calling a live key dead) is far worse than a false positive.
KEY_REF = re.compile(r'(str\.[A-Za-z0-9_.]+)')

SKIP_DIRS = {"obj", "bin", ".vs"}


def scan() -> tuple[set[str], set[str]]:
    with open(STRINGS, encoding="utf-8") as fh:
        defined = set(KEY_DEF.findall(fh.read()))

    referenced: set[str] = set()
    for base, dirs, files in os.walk(UI):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if not name.endswith((".axaml", ".cs")):
                continue
            path = os.path.join(base, name)
            if os.path.abspath(path) == os.path.abspath(STRINGS):
                continue
            with open(path, encoding="utf-8", errors="ignore") as fh:
                referenced.update(KEY_REF.findall(fh.read()))

    return defined, referenced


def main() -> int:
    defined, referenced = scan()
    orphans = sorted(defined - referenced)
    dangling = sorted(referenced - defined)

    print(f"en.axaml: {len(defined)} keys defined, {len(referenced)} referenced")

    if "--list" in sys.argv:
        for k in orphans:
            print("  ORPHAN   ", k)
        for k in dangling:
            print("  DANGLING ", k)
        return 0

    rc = 0
    if dangling:
        rc = 1
        print(f"\nFAIL: {len(dangling)} referenced key(s) are NOT defined in en.axaml.")
        print("A missing StaticResource raises at load time — this crashes a panel.")
        for k in dangling:
            print("   ", k)

    if orphans:
        rc = 1
        print(f"\nFAIL: {len(orphans)} defined key(s) are referenced nowhere.")
        print("Either wire them up or delete them; a dictionary full of dead keys")
        print("misrepresents what is actually localizable.")
        for k in orphans:
            print("   ", k)

    if rc == 0:
        print("OK: no orphans, no dangling references.")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
