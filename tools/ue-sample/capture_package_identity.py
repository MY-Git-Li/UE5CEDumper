#!/usr/bin/env python3
"""Record WHICH package a DumperTest test session was run against.

WHY THIS EXISTS
  The packaged binary is deliberately not in git (583 MB per config against a 180 MB
  .git -- see README.md). That decision is right, and it leaves one hole: a stale
  package silently tests yesterday's property zoo. The failure is nasty because it does
  not look like staleness -- it looks like the dumper reading the wrong value.

  Same shape as tools/ghidra/identity/ for the AOB corpus: the artifact lives outside,
  the small thing that VERIFIES it lives in the repo.

  It also records which names are absent on purpose. RawInt/RawFloat/RawDouble are the
  non-UPROPERTY holes the Native-C scan has to find; if they ever START appearing in the
  binary's name tables, someone has added a UPROPERTY to them and that test is dead.

Usage:
  py tools/ue-sample/capture_package_identity.py "D:\\path\\to\\DumperTest"
  py tools/ue-sample/capture_package_identity.py <root> --check    # compare, don't write
Exit 0 = written / matches, 1 = mismatch under --check.
"""
import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "package-identity.json")

# Reflected names that MUST be present, and raw members that must NOT be.
# Class names are emitted as UTF-16 (TEXT() in the UHT registration), property names as
# narrow strings in FPropertyParams -- so both encodings have to be searched. Checking
# only ASCII makes every class name look missing from a Shipping build.
MUST_EXIST = ["DumperTestActor", "DumperTestSubsystem", "DumperTestPayload",
              "Text_Even2_TwoNull", "Opt_Int_Unset", "FrozenInt"]
MUST_BE_ABSENT = ["RawInt", "RawFloat", "RawDouble"]


def find_exe(root, config):
    base = os.path.join(root, config, "Windows")
    for dirpath, _, files in os.walk(base):
        if os.path.basename(dirpath).lower() == "win64":
            for f in files:
                if f.lower().endswith(".exe"):
                    return os.path.join(dirpath, f)
    return None


def probe(path):
    with open(path, "rb") as fh:
        data = fh.read()
    def count(name):
        return data.count(name.encode("ascii")) + data.count(name.encode("utf-16-le"))
    return {
        "file": os.path.basename(path),
        "size": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "reflected_present": {n: count(n) for n in MUST_EXIST},
        "raw_absent": {n: count(n) for n in MUST_BE_ABSENT},
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root", help="folder holding Development/ and Shipping/")
    ap.add_argument("--check", action="store_true", help="compare against the stored record")
    args = ap.parse_args()

    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    try:
        commit = subprocess.run(["git", "-C", repo, "rev-parse", "--short", "HEAD"],
                                capture_output=True, text=True).stdout.strip()
    except OSError:
        commit = "unknown"

    record = {
        "engine": "UE 5.4",
        "source_commit": commit,
        "captured_utc": datetime.datetime.now(datetime.timezone.utc)
                                 .replace(microsecond=0).isoformat(),
        "build_command": (r'"C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" '
                          r'DumperTestEditor Win64 Development -Project="<project>\DumperTest.uproject" '
                          r'-WaitMutex'),
        "packaged_root": args.root,
        "configs": {},
        "problems": [],
    }

    for cfg in ("Development", "Shipping"):
        exe = find_exe(args.root, cfg)
        if not exe:
            record["problems"].append("%s: no Binaries/Win64 exe found" % cfg)
            continue
        info = probe(exe)
        for n, c in info["reflected_present"].items():
            if c == 0:
                record["problems"].append("%s: reflected name '%s' is MISSING from the binary" % (cfg, n))
        for n, c in info["raw_absent"].items():
            if c:
                record["problems"].append(
                    "%s: '%s' appears %d time(s) -- it is supposed to be a NON-UPROPERTY hole; "
                    "someone reflected it and the Native-C test is dead" % (cfg, n, c))
        record["configs"][cfg] = info

    text = json.dumps(record, indent=2) + "\n"
    if args.check:
        if not os.path.exists(OUT):
            print("no stored record at %s" % OUT)
            return 1
        stored = json.load(open(OUT, encoding="utf-8"))
        drift = [c for c in record["configs"]
                 if stored.get("configs", {}).get(c, {}).get("sha256") != record["configs"][c]["sha256"]]
        if drift:
            print("package DRIFT in %s -- rebuilt since the record was written" % ", ".join(drift))
            return 1
        print("package matches the stored identity")
        return 0

    with open(OUT, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)
    print("wrote %s" % OUT)
    for p in record["problems"]:
        print("  PROBLEM: %s" % p)
    return 1 if record["problems"] else 0


if __name__ == "__main__":
    sys.exit(main())
