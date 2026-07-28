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
# HELIUM RAIN 4.20.3 — the SECOND symbolised 4.20, so the pre-4.23 GNames derivation no longer
# rests on Everspace alone. Pre-4.23: no FNamePool (GNames is the TNameEntryArray* that
# FName::GetNames lazily new's — taken from the load at +4 in that function) and NO sparse
# delegates, so SparseDelegates is absent BY DESIGN here, not missing.
"UE4.20-HeliumRain|HeliumRain|-|GObjects=14321fb58|14321fb68,GNames=143216ec8,GWorld=14331de50,GEngine=14331b7d8"
"UE4.22-Satisfactory|Satisfactory_UE422|-|GObjects=144006f80|144006f90,GNames=144002a78,GWorld=1441073b8,GEngine=144104e58"
# UE 4.23 — the "Flying" template, SHIPPING, built by the maintainer against Epic's INSTALLED
# 4.23.1 Launcher engine (CL 9631420, IsPromotedBuild=1, IsLicenseeVersion=0), so the engine
# objects are Epic stock and a Shared build environment forbids overriding the check/logging
# macros. Closes the 4.23 hole this file carried as "DELIBERATELY NOT CHASED" — its own escape
# clause ("unless a 4.23 binary falls into your lap") fired.
# It matters TWICE: 4.23 is the version FNamePool was introduced in AND the version sparse
# delegates were introduced in, so it is the EARLIEST binary either target can be checked against.
# GNames: NO NamePoolData symbol AND no ?GetNames@FName@@ at 4.23 (0 occurrences of both in the
#   PDB), so BOTH recipes documented in GROUND-TRUTH.md fail here. Taken from
#   FNameDebugVisualizer::GetBlocks @0x14062c010 (`48 8d 05 f9 83 82 02 c3` -> 0x142e54410) minus
#   0x10. All five values are triple-derived: PDB S_PUB32 decode, a 150-pattern byte replay
#   against .text, and the live run — which rebases all five off ONE ASLR base with zero residual.
# DECOYS: GCoreObjectArrayForDebugVisualizers has a PLAIN name (find_syms3.java WILL surface it)
#   and its RUNTIME value equals the ObjObjects VA; GNameBlocksDebug holds NamePoolData+0x10.
# NOTE the project name uses an UNDERSCORE before "Shipping", unlike every other row.
"UE4.23-Flying|UE423_Flying-Win64_Shipping|-|GObjects=142e6b968|142e6b978,GNames=142e54400,GWorld=142f6cf10,SparseDelegates=142c4d060,GEngine=142f6a8a0"
"UE4.24-DropIn|DropIn_UE424|-|GObjects=1471db720|1471db730,GNames=1471bca00,GWorld=1472ea620,SparseDelegates=146da38d0,GEngine=1472e74a0"
"UE4.25-Everspace2|ES2-UE425|-|GObjects=1444b0520|1444b0510,GNames=144497d00,GWorld=1445f1160,SparseDelegates=1440070c0,GEngine=1445edad8"
"UE4.26-Satisfactory|Satisfactory_UE426|-|-CoreUObject-:GObjects=1803f9210|1803f9220,-CoreUObject-:SparseDelegates=1803f37d0,-Core-Win64:GNames=180659380,-Engine-:GWorld=18182a0b8,-Engine-:GEngine=181826658"
"UE4.27-DropIn|DropIn|-|GObjects=14a3aa670|14a3aa660,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910,GEngine=14a528890"
# Two more symbolised 4.27s. Both report `++UE4+Release-4.27-CL-0`, i.e. built from an engine
# SOURCE tree rather than a launcher binary — which is consistent with them shipping PDBs at all.
"UE4.27-Breeders|Breeders_of_the_Nephelym|-|GObjects=1445f32d0|1445f32e0,GNames=1445b7000,GWorld=14473a7f8,SparseDelegates=1441a88b0,GEngine=144736ee8"
"UE4.27-Maelstrom|Maelstrom|-|GObjects=145b90c10|145b90c20,GNames=145b54940,GWorld=145cd4ee8,SparseDelegates=145839d00,GEngine=145cd15c8"
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
# GRIMHOOK — the corpus's FIRST symbolised UE 5.1. Palworld is 5.1 but has no PDB, so every 5.1
# claim rested on unproven consensus until this landed. GNames is NOT a bare -0x10 assumption:
# 0x14639E140 has 58 code xrefs and they are FName::ToString / GetEntry / AppendString /
# FLazyName::Resolve, all loading it as the pool `this`.
# Do NOT be tempted by the symbolised `GNameBlocksDebug` (0x14632A4D0) on a future PDB game —
# it is a SEPARATE pointer variable, not NamePoolData+0x10, and it is all-zero in the file.
"UE5.1-Grimhook|Grimhook|-|GObjects=14643d800|14643d810,GNames=14639e140,GWorld=1465aa438,SparseDelegates=1461417d0,GEngine=1465a6630"
# THE TWO OLDEST BINARIES IN THE CORPUS. No PDB, no RTTI (/GR-), truth by disassembly.
# Both are FLAT FFixedUObjectArray presented at the FUObjectArray BASE (Objects@+0x10,
# Max@+0x18, Num@+0x1C) — which is what the "Flat-Base" preset was added for. Item stride
# differs: 4.11 is 16 bytes (Object/Flags/SerialNumber, NO ClusterRootIndex), 4.13 is the
# familiar 24. Stride is auto-detected, so nothing depends on that difference.
# Nekopara is 4.11.0-PREVIEW7 (build path `Engine\4_11_0_preview7\`), so do not read its 16-byte
# item as "UE 4.11 = stride 16" — 4.11 final may already carry ClusterIndex.
# Pre-4.23 on both: GNames is a TNameEntryArray*, and SparseDelegates is correctly ABSENT.
"UE4.11-Nekopara|NEKOPALIVE_UE411|-|GObjects=1423c1510|1423c1520,GNames=1423b8f58,GWorld=1424a4370,GEngine=1424a2de0"
"UE4.13-Fantasynth|Fantasynth_UE413|-|GObjects=142779240|142779250,GNames=14273ffd0,GWorld=1427851f0,GEngine=14277d350"
# FREUD GATE 4.21 and LIGHT MAZE 5.0.3 — no PDB, but both projects are fully analyzed and every
# global was pinned by disassembly AND corroborated by independent pattern consensus. They close
# the last two version holes: 4.21 had no sample at all, and 4.27 previously jumped straight to
# 5.1 with nothing at 5.0. Freud Gate is pre-4.23, so its GNames is a TNameEntryArray* and it has
# no sparse delegates — both correct, not omissions.
"UE4.21-FreudGate|Freud_Gate_UE421|-|GObjects=142f97c90|142f97ca0,GNames=142f93958,GWorld=1430897c8,GEngine=143086f60"
"UE5.0-LightMaze|Light_Maze|-|GObjects=144a59a60|144a59a70,GNames=144bff5c0,GWorld=144dedcc0,SparseDelegates=144a59b80,GEngine=144dea558"
# ---- symbol-less, truth DERIVED BY DISASSEMBLY (2026-07-27) ----
# All three were live-run first (our own validators accepted GObjects/GNames/GWorld/Sparse),
# then corroborated in Ghidra. Both Auto Analyze runs were saved PARTWAY through; raw data
# inspection is unaffected, so a missing function here never means a missing global.
#
# DQ7R GEngine is 145ff4b28, NOT the 145d76d78 the live consensus reported. That value is a
# GAME-SIDE singleton reached only by GENG_X4 (50 of its 55 hits) — the live ranking was by raw
# hit count, which let one generic pattern outvote four specific ones. Proven three ways at
# 145ff4b28: UWorld::GetGameViewport, UWorld::GetRealTimeSeconds, and a GetWorld fallback that
# loads GEngine and GWorld in the same function. See the GENG_X4 note in Himmel.h.
"UE4.27-DQ7R|DQ7R|-|GObjects=145ea7660|145ea7670,GNames=145e6b300,GWorld=145ff8470,SparseDelegates=145b26520,GEngine=145ff4b28"
# ELLIOT — the corpus's ONLY UE 5.4 sample. All five globals corroborated, no contradictions.
"UE5.4-Elliot|Elliot|-|GObjects=149bfc140|149bfc150,GNames=149b18600,GWorld=149d8bda0,SparseDelegates=14990e1c0,GEngine=149d8e290"
# DQ XI S — UE 4.18, second pre-4.23 sample. ASLR-relocated at runtime; the image base was
# recovered from FFixedUObjectArray's shape (Objects/+8 Max/+0xC Num) and both supplied deltas
# reproduce exactly. GNames deliberately ABSENT: 4.18 predates FNamePool, GNames is a lazily
# allocated TNameEntryArray*, every GNames pattern here is FNamePool-shaped, and the consensus
# is noise — per the rule above, leave it out rather than guess. Sparse correctly absent (4.23+).
"UE4.18-DQXIS|DQ_XI_S|-|GObjects=145d83bf8|145d83c08,GWorld=145e70c98,GEngine=145e6eeb0"
# ---- MONOLITHIC noise probes (no symbols; consensus only) ----
# FF7 REBIRTH (distinct from FF7 Remake above): the only binary that can exercise GOBJ_RE1 and
# the GNAM_V7 CallFollow, both of which were contributed FOR it and hit nothing anywhere else.
"UE4.x-FF7Rebirth|FF7Re|-|"
# OCTOPATH is 4.18, not "4.x": its .rdata carries `++UE4+Release-4.18`, and its pattern
# fingerprint is IDENTICAL to DQ XI S (a known 4.18) -- GNames 7/28 hit, SPARSE 0/10.
"UE4.18-Octopath|Octopath|-|"
"UE4.27-Artisan|The_Artisan_of_Glimmith|-|"
"UE5.2-SatGameDLL|Satisfactory_UE521|FactoryGame-FactoryGame-Win64-Shipping.dll|"
# PALWORLD — the corpus had NO UE 5.1 sample at all, and it is what GOBJ_V13 / GNAM_V8 /
# GWLD_V7 were contributed for (GWLD_V7 has never hit anything in the corpus).
"UE5.1-Palworld|Palworld|-|"
# AVOWED — SparseDelegates truth DERIVED BY DISASSEMBLY 2026-07-27 (see Himmel.h AOB_SPARSE_AV53_1).
# GObjects/GNames/GWorld stay unset on purpose: this build is the packed-20-byte-FUObjectItem
# negative control with a GWorld decoy (docs/avowed-gobjects-fix.md), and a guessed truth is worse
# than none. Sparse alone is proven, so only sparse is claimed.
"UE5.3-Avowed|Avowed|-|SparseDelegates=14b5bd9a8"
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
