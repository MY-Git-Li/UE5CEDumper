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

THE SOURCE HASH, and why the first version of this file was not good enough.
  v1 recorded `source_commit` from `git rev-parse HEAD`. That is a claim about MY working
  tree at capture time, not about what the binary was built from -- and it read as if it
  were the latter. On 2026-08-05 the very first packaged build was tested with a STALE
  DumperTestActor.cpp (the live project still held 退 where the repo had 走), the record
  said `8b812a5`, and nothing noticed. The report and the reality computed by different
  code paths, in the tool built to prevent exactly that.
  So the record now hashes the SOURCE FILES, and --project compares the tree the package
  was actually built from against this repo's copy. Pass it; a commit id cannot.

Usage:
  py tools/ue-sample/capture_package_identity.py "D:\\path\\to\\packaged"
  py tools/ue-sample/capture_package_identity.py <root> --project "D:\\Unreal Projects\\DumperTest"
  py tools/ue-sample/capture_package_identity.py <root> --check    # compare, don't write
Exit 0 = written / matches, 1 = mismatch, drift, or a problem in the record.
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


SOURCES = ["DumperTestActor.h", "DumperTestActor.cpp", "DumperTestTypes.h",
           "DumperTestSubsystem.h", "DumperTestSubsystem.cpp"]


def hash_sources(src_dir):
    """One digest over the five sample sources, name-and-content, in fixed order.

    Name included so a rename cannot collide, order fixed so the digest is stable.
    """
    h = hashlib.sha256()
    missing = []
    for name in SOURCES:
        p = os.path.join(src_dir, name)
        if not os.path.exists(p):
            missing.append(name)
            continue
        h.update(name.encode("utf-8"))
        with open(p, "rb") as fh:
            h.update(fh.read())
    return h.hexdigest(), missing


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root", help="folder holding Development/ and Shipping/")
    ap.add_argument("--project", default=None,
                    help="the UE project the package was built from; its sources are "
                         "compared against this repo's copy")
    ap.add_argument("--check", action="store_true", help="compare against the stored record")
    args = ap.parse_args()

    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    try:
        commit = subprocess.run(["git", "-C", repo, "rev-parse", "--short", "HEAD"],
                                capture_output=True, text=True).stdout.strip()
    except OSError:
        commit = "unknown"

    repo_src = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "DumperTest", "Source", "DumperTest")
    repo_hash, repo_missing = hash_sources(repo_src)

    record = {
        "engine": "UE 5.4",
        # A claim about the working tree at capture time. Informative only -- the
        # source_sha256 below is what actually binds the record to a source state.
        "source_commit": commit,
        "source_sha256": repo_hash,
        "captured_utc": datetime.datetime.now(datetime.timezone.utc)
                                 .replace(microsecond=0).isoformat(),
        "build_command": (r'"C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" '
                          r'DumperTestEditor Win64 Development -Project="<project>\DumperTest.uproject" '
                          r'-WaitMutex'),
        "packaged_root": args.root,
        "configs": {},
        "problems": [],
    }

    for name in repo_missing:
        record["problems"].append("repo source '%s' is missing -- the record cannot bind "
                                  "the package to a source state" % name)

    if args.project:
        proj_src = os.path.join(args.project, "Source", "DumperTest")
        proj_hash, proj_missing = hash_sources(proj_src)
        record["built_from"] = {"path": proj_src, "source_sha256": proj_hash}
        for name in proj_missing:
            record["problems"].append("project source '%s' is missing from %s" % (name, proj_src))
        if not proj_missing and proj_hash != repo_hash:
            record["problems"].append(
                "STALE PACKAGE: the project this was built from does not match the repo "
                "sources (project %s vs repo %s). Copy tools/ue-sample/DumperTest/Source/"
                "DumperTest/* over and re-package, or the values you are about to verify "
                "are not the ones documented." % (proj_hash[:12], repo_hash[:12]))

    for cfg in ("Development", "Shipping"):
        exe = find_exe(args.root, cfg)
        if not exe:
            record["problems"].append("%s: no Binaries/Win64 exe found" % cfg)
            continue
        info = probe(exe)
        # Was this exe produced AFTER the sources it claims to embody?
        #
        # Note for anyone who remembers R8: that finding was rejected for using mtime as a
        # proxy for CONTENT freshness, which it is not. Here the question is literally
        # temporal -- "was the binary produced after this edit?" -- and mtime is the direct
        # answer, not a stand-in for one. Different question, so the same tool is right.
        exe_mtime = os.path.getmtime(exe)
        newest_src, newest_name = 0.0, None
        for name in SOURCES:
            p = os.path.join(repo_src, name)
            if os.path.exists(p) and os.path.getmtime(p) > newest_src:
                newest_src, newest_name = os.path.getmtime(p), name
        info["exe_mtime"] = datetime.datetime.fromtimestamp(
            exe_mtime, datetime.timezone.utc).replace(microsecond=0).isoformat()
        if newest_name and newest_src > exe_mtime:
            record["problems"].append(
                "STALE BINARY: %s was built before '%s' was last edited -- re-package before "
                "trusting any value from it" % (cfg, newest_name))
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
