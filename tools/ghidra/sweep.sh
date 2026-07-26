#!/usr/bin/env bash
# sweep.sh — run the WHOLE AOB regression sweep over every Ghidra project we have.
#
# This is the executable form of tools/ghidra/GROUND-TRUTH.md. Keeping the truth values in a
# script rather than only in prose matters: re-deriving them costs a headless run per binary and
# a single mistyped VA silently corrupts every verdict downstream (it has happened once — a
# placeholder truth made two good GEngine patterns look like they produced five decoys, and they
# were demoted on that basis).
#
#   bash tools/ghidra/sweep.sh              # everything
#   bash tools/ghidra/sweep.sh UE4.27 UE5.7 # only tags matching these substrings
#
# Env overrides: GHIDRA_HOME, GHIDRA_PROJS, SWEEP_OUT, SWEEP_XMX
#
# Afterwards:  py tools/ghidra/aggregate_sweep.py <SWEEP_OUT>
set -u

GHIDRA_HOME="${GHIDRA_HOME:-D:/Tools/ghidra_12.1.2_PUBLIC}"
GHIDRA_PROJS="${GHIDRA_PROJS:-D:/Tools/GHIDRA_Projs}"
SWEEP_OUT="${SWEEP_OUT:-$PWD/out/sweep}"
SWEEP_XMX="${SWEEP_XMX:-14G}"
HL="$GHIDRA_HOME/support/analyzeHeadless.bat"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

mkdir -p "$SWEEP_OUT"
export _JAVA_OPTIONS="-Xmx$SWEEP_XMX"

echo "== extracting patterns from Himmel.h =="
py "$REPO/tools/ghidra/extract_patterns.py" "$REPO/dll/src/Himmel.h" "$SWEEP_OUT/patterns.tsv" || exit 1

# ---------------------------------------------------------------------------------------------
# The sweep table:  TAG | PROJECT | PROCESS-GLOB ('-' = every program) | GS_TRUE
#
# GS_TRUE entries may carry a `programNameSubstring:` prefix. That is REQUIRED for modular
# builds: their DLLs all share image base 0x180000000, so their address ranges OVERLAP and an
# unscoped union would let a hit inside Core be scored as a correct GObjects (which lives in
# CoreUObject). Use substrings that cannot alias — `-Core-Win64` does not match
# `-CoreUObject-Win64`.
#
# A row with an EMPTY GS_TRUE is a NOISE PROBE: it can only ever answer "did anything hit that
# should not have?", never "did it hit the right thing". Every verdict comes back NO-TRUTH by
# design — see the NO-TRUTH note in scan_patterns.java. Their real job is the consensus table,
# and they matter because they are MONOLITHIC: a modular Satisfactory DLL is a 4-30 MB .text
# while a shipped game EXE is 100-200 MB, so per-pattern collision counts measured only on
# Satisfactory badly understate the noise a real game presents.
# ---------------------------------------------------------------------------------------------
ROWS=(
# ---- symbolised oracles (full PDB) ----
"UE4.20-Everspace|ES1-420|-|GObjects=142e797f0|142e79800,GNames=1431dead8,GWorld=1432e1ac0,GEngine=1432df470"
"UE4.22-Satisfactory|Satisfactory_UE422|-|GObjects=144006f80|144006f90,GNames=144002a78,GWorld=1441073b8,GEngine=144104e58"
"UE4.24-DropIn|DropIn_UE424|-|GObjects=1471db720|1471db730,GNames=1471bca00,GWorld=1472ea620,SparseDelegates=146da38d0,GEngine=1472e74a0"
"UE4.25-Everspace2|ES2-UE425|-|GObjects=1444b0520|1444b0510,GNames=144497d00,GWorld=1445f1160,SparseDelegates=1440070c0,GEngine=1445edad8"
"UE4.26-Satisfactory|Satisfactory_UE426|-|-CoreUObject-:GObjects=1803f9210|1803f9220,-CoreUObject-:SparseDelegates=1803f37d0,-Core-Win64:GNames=180659380,-Engine-:GWorld=18182a0b8,-Engine-:GEngine=181826658"
"UE4.27-DropIn|DropIn|-|GObjects=14a3aa670|14a3aa660,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910,GEngine=14a528890"
"UE5.2-Satisfactory|SF521_pdb|-|@SF521@"
"UE5.5-Everspace2|ES2-0517|-|GObjects=149aa7ef0|149aa7ee0,GNames=149c009c0,GWorld=149b37d18,SparseDelegates=149aa7e90,GEngine=149da5810"
# Second UE 5.5 Everspace 2, two manifests newer (2025-06-17 vs the 05-17 snapshot). Same engine,
# same studio, different compile — the ONLY same-game cross-build pair in the corpus, and so the
# only thing that can answer "does a pattern survive a game update?" rather than "a version bump".
"UE5.5-Everspace2b|ES2_UE55|-|GObjects=149aa5f60|149aa5f70,GNames=149bfe940,GWorld=149b35dd8,SparseDelegates=149aa5f10,GEngine=149da37b0"
# Meltopia, now WITH symbols — the retry succeeded using the MSDIA PDB loader (PDB-Universal
# fails on this file). A second symbolised MONOLITHIC UE 5.5 oracle, which the modular-heavy
# corpus is short of. Supersedes the symbol-less `Meltopia.rep`.
"UE5.5-Meltopia|Meltopia_V2|-|GObjects=149d87420|149d87430,GNames=149ca3c80,GWorld=149f03d10,SparseDelegates=149a9a070,GEngine=149f002f8"
"UE5.6-Satisfactory|Satisfactory_v1.2.3.1|-|-CoreUObject-:GObjects=1805a3620|1805a3630,-CoreUObject-:SparseDelegates=1805661a0,-Core-Win64:GNames=18082e8c0,-Engine-:GWorld=18216db68,-Engine-:GEngine=182170748,CrashReportClient:GObjects=141a9d4b0|141a9d4c0,CrashReportClient:GNames=1419c7e80,CrashReportClient:SparseDelegates=1419307f0"
"UE5.7-Solarpunk|Solarpunk|-|GObjects=1476ca920|1476ca910,GNames=1478e50c0,GWorld=1478cce58,SparseDelegates=1476ca4b0,GEngine=147a2fc20"
# ---- partially-symbolised: truth DERIVED BY DISASSEMBLY, not from a PDB ----
# FF7 Remake has no PDB. These two VAs were recovered by hand and are as solid as a symbol:
# FUN_140FD1490 is `GEngine->GetWorldFromContextObject(Obj)` — 0x145879EE8 is loaded into RCX as
# `this` for a call returning a UWorld, and the caller then runs that UWorld's InternalIndex
# (+0x0C) through `cmp [0x1453BD48C]` / `mov rax,[0x1453BD480]` / `lea rcx,[rax+idx*24]` /
# `mov eax,[rcx+8]` — textbook GUObjectArray.IndexToObject on a 24-byte FUObjectItem. So
# 0x1453BD480 is ObjObjects.Objects and GUObjectArray is 0x10 below it. Both are independently
# corroborated: GOBJ_RE2 + GOBJ_V12 agree on 0x1453BD470, and GENGC_A agrees on 0x145879EE8.
# GNames/GWorld are deliberately left unset — the consensus is suggestive but unproven, and a
# guessed truth value is worse than none (it silently mislabels every hit as a decoy).
"UE4.18-FF7R|FF7R|-|GObjects=1453bd470|1453bd480,GEngine=145879ee8"
# ---- MONOLITHIC noise probes (no symbols; consensus only) ----
# FF7 REBIRTH (distinct from FF7 Remake above): the only binary that can exercise GOBJ_RE1 and
# the GNAM_V7 CallFollow, both of which were contributed FOR it and hit nothing anywhere else.
"UE4.x-FF7Rebirth|FF7Re|-|"
"UE4.x-Octopath|Octopath|-|"
"UE4.27-Artisan|The_Artisan_of_Glimmith|-|"
"UE5.2-SatGameDLL|Satisfactory_UE521|FactoryGame-FactoryGame-Win64-Shipping.dll|"
# PALWORLD — the corpus had NO UE 5.1 sample at all, and it is what GOBJ_V13 / GNAM_V8 /
# GWLD_V7 were contributed for (GWLD_V7 has never hit anything in the corpus).
"UE5.1-Palworld|Palworld|-|"
"UE5.3-Avowed|Avowed|-|"
"UE5.5-ManorLords|Manor Lords|-|"
"UE5.6-TQ2|TQ2|-|"
"UEx-DQ12HD2D|DQ_I_II_HD2D|-|"
)

# UE 5.2 — Satisfactory v0.8.x engine DLLs from D:\tmp\Game archive\Satisfactory\UE5.2.1, each
# imported WITH its PDB into the SF521_pdb project (the user-supplied Satisfactory_UE521.rep
# holds only the 5.2 *game* DLL; its Core/CoreUObject/Engine are duplicates of the 4.26 ones).
# Cross-checked against the DLL export table: GEngine RVA 0x1CD1140, GWorld 0x1CD4828,
# GUObjectArray 0x4194D0 — all consistent with these VAs at image base 0x180000000.
SF521_TRUE="-CoreUObject-:GObjects=1804194d0|1804194e0,-CoreUObject-:SparseDelegates=1803edcb0,-Core-Win64:GNames=18073d0c0,-Engine-:GWorld=181cd4828,-Engine-:GEngine=181cd1140"

FILTERS=("$@")
match() {
  [ ${#FILTERS[@]} -eq 0 ] && return 0
  for f in "${FILTERS[@]}"; do case "$1" in *"$f"*) return 0;; esac; done
  return 1
}

one_project() {
  local TAG="$1" PROJ="$2" GLOB="$3" TRUE="$4"
  local LOG="$SWEEP_OUT/log_$(echo "$TAG" | tr -c 'A-Za-z0-9._-' '_').txt"
  local PROC=(-process)
  [ "$GLOB" != "-" ] && PROC=(-process "$GLOB")
  GS_OUT="$SWEEP_OUT" GS_TSV="$SWEEP_OUT/patterns.tsv" GS_TRUE="$TRUE" GS_TAG="$TAG" \
     "$HL" "$GHIDRA_PROJS" "$PROJ" "${PROC[@]}" -noanalysis -readOnly \
     -scriptPath "$REPO/tools/ghidra" -postScript scan_patterns.java > "$LOG" 2>&1
  echo "   done $TAG (exit=$?)  log: $LOG"
}

# Ghidra takes an EXCLUSIVE lock per project, so parallelism is safe ACROSS projects but never
# within one. SWEEP_JOBS controls how many projects run at once.
JOBS="${SWEEP_JOBS:-3}"
running=0
for row in "${ROWS[@]}"; do
  TAG="${row%%|*}";  rest="${row#*|}"
  PROJ="${rest%%|*}"; rest="${rest#*|}"
  GLOB="${rest%%|*}"; TRUE="${rest#*|}"
  match "$TAG" || continue
  [ "$TRUE" = "@SF521@" ] && TRUE="$SF521_TRUE"
  case "$TRUE" in *@GOBJ52@*) echo "!! SKIP $TAG — UE5.2 truth placeholders not filled in"; continue;; esac

  echo "== launching $TAG   project=$PROJ   glob=$GLOB"
  one_project "$TAG" "$PROJ" "$GLOB" "$TRUE" &
  running=$((running + 1))
  if [ "$running" -ge "$JOBS" ]; then wait -n 2>/dev/null || wait; running=$((running - 1)); fi
done
wait

echo
echo "== sweep complete. Aggregate with:"
echo "   py tools/ghidra/aggregate_sweep.py \"$SWEEP_OUT\""
