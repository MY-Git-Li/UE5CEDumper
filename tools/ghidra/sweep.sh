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
# Env overrides: GHIDRA_HOME, GHIDRA_PROJS, SWEEP_OUT, SWEEP_XMX, SWEEP_SCRIPT
#
# SWEEP_SCRIPT runs a DIFFERENT postScript over the same ROWS table — the point being that the
# table (which project carries which truth VAs, and which `-process` glob each row needs) exists
# once and cannot drift between tools:
#   SWEEP_SCRIPT=dump_blocks.java SWEEP_OUT=$PWD/out/blocks bash tools/ghidra/sweep.sh
#
# Afterwards:  py tools/ghidra/aggregate_sweep.py <SWEEP_OUT>
set -u

GHIDRA_HOME="${GHIDRA_HOME:-D:/Tools/ghidra_12.1.2_PUBLIC}"
GHIDRA_PROJS="${GHIDRA_PROJS:-D:/Tools/GHIDRA_Projs}"
SWEEP_OUT="${SWEEP_OUT:-$PWD/out/sweep}"
SWEEP_XMX="${SWEEP_XMX:-14G}"
SWEEP_SCRIPT="${SWEEP_SCRIPT:-scan_patterns.java}"
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
# ── UE 4.10.4 UE4Game, Shipping + Development ─────────────────────────────────────────────────
# THE OLDEST BINARY IN THE CORPUS, older than Nekopara 4.11, and the only symbolised oracle below
# 4.15. Epic stock: CL 2872498, `++depot+UE4-Releases+4.10`, IsLicenseeVersion=0.
#
# NOTHING WAS COMPILED. 4.10 needs VS2015, which is not installed — but the launcher engine already
# SHIPS prebuilt MONOLITHIC game targets with full PDBs in Engine/Binaries/Win64:
# `UE4Game-Win64-Shipping.exe` (38.7 MB) and `UE4Game.exe` (83 MB, Development). Same escape hatch
# the 5.3 rows used, and it generalises: for any launcher engine, look for the prebuilt UE4Game/
# UnrealGame targets BEFORE concluding a version needs a toolchain. There is no prebuilt DebugGame.
#
# ⚠ GObjects IS EXPECTED TO SCORE 0 ON BOTH ROWS. That is the finding, not a defect — LEAVE IT ❌.
# At 4.10 the array is a FUNCTION-LOCAL STATIC behind a magic-static guard inside
# `GetUObjectArray()`, so every consumer reaches it with a CALL and never materialises the address
# inline; all 52 GObjects patterns are `lea reg,[rip+GUObjectArray]`-shaped and cannot match. 4.11
# promoted it to a plain `GUObjectArray` global, which is exactly why Nekopara (4.11) resolves.
# This MEASURES the pre-4.11 support floor the repo already asserts, instead of assuming it.
# Measured: 74 GObjects candidates on Shipping / 105 on Development, and the true VA (and its
# +0x10 alias) is in NEITHER list at any rank — not merely outside the top N.
#
# Truth recovery, both rows double-derived and in agreement:
#   GObjects — by disassembly, since there is no S_PUB32 for it. `GetUObjectArray` @14023c2e0
#     (Shipping) / @14067d730 (Dev): the guarded init does `lea rbx,[rip+X]`, passes rbx as `this`
#     to `??0FUObjectArray@@QEAA@XZ`, and returns rbx — so X is the array. Independently confirmed
#     by `GetObjectArrayForDebugVisualizers`, which is literally `GetUObjectArray(); add rax,0x10`.
#     That also MEASURES ObjObjects@+0x10 here rather than inheriting it, so the `|base+0x10` alias
#     on these two rows is verified.
#   GNames/GWorld/GEngine — `pdb_globals.py` + a full 151-pattern byte replay, which agree exactly.
# GNames is the pre-4.23 `TNameEntryArray*` (FName::GetNames load at +4, no -0x10). GWorld is typed
# `UWorldProxy` at 4.10 (`?GWorld@@3VUWorldProxy@@A`) — a wrapper, same storage. SparseDelegates
# ABSENT BY DESIGN (4.23+ feature).
#
# Coverage, and it is what makes the Development row worth its scan: Shipping is comfortable
# (GNames n=3, GWorld n=2, GEngine n=3) but Development is THREADBARE — GNames and GWorld are each
# held by a SINGLE voter. Both still resolve, and the reason is PRIORITY, not consensus:
#   GWorld  — the top consensus row is a DECOY (144bdc140, n=2, GWLD_SAT52_1+GWLD_V3), but
#             `GWLD_FD_1` is pri=102 against that decoy's 365, so the walk lands correctly FIRST.
#             Read the priority walk, not the n= ranking; they disagree here.
#   GNames  — `GNAM_XX_1` (pri=717) is the first hitting pattern and its first hit is the truth,
#             while every n>=2 consensus row is a decoy.
# Another rule-5 payout: two single-voter patterns are the only thing holding the oldest engine in
# the corpus up, and on consensus alone both would look wrong.
# Both imported with `-import ... -noanalysis`; do NOT "fix" them by analysing them.
"UE4.10-Game|UE410_Game_Shipping|-|GObjects=1423422b0|1423422c0,GNames=14232f530,GWorld=14234edb8,GEngine=14234a450"
"UE4.10-GameDev|UE410_Game_Development|-|GObjects=144bdb090|144bdb0a0,GNames=144bc0d50,GWorld=144be85f8,GEngine=144be35c8"
# UE 4.15.3 — the "Flying" template, Shipping. The oldest oracle between 4.10 and 4.20, and the
# oldest one whose GObjects actually RESOLVES (the 4.10 rows above cannot — magic-static): it turns
# the 4.13-4.19 FLAT FFixedUObjectArray / 24-byte FUObjectItem band from source interpolation into
# measurement (Objects@base+0x10, Max@+0x18, Num@+0x1C).
# GNames is NOT a symbol — pre-4.23 has no FNamePool; it is the static FName::GetNames @0x1401F54B0
# tests-and-stores (load at +4), corroborated by 9 RIP xrefs across GetNames/StaticInit/
# InitInternal_FindOrAddNameEntry<char,wchar_t>. SparseDelegates correctly ABSENT (4.23+ feature):
# 0 ASCII and 0 UTF-16LE occurrences in the 47 MB image.
# DECOYS, all three carrying PLAIN GDATA records that find_syms3.java will surface:
#   GCoreObjectArrayForDebugVisualizers @142CC37F8 -> runtime value IS the ObjObjects VA
#   GObjectArrayForDebugVisualizers     @142B0F0A0 -> holds the above (TWO indirections off)
#   GFNameTableForDebuggerVisualizers_MT@142B915C8 -> runtime value is *GNames (one level deeper)
# It also gave DIRECT SYMBOL CONFIRMATION of the previously-inferred 4.11 TSharedPtr GWorld decoy:
# GWLD_SAT52_1's decoys here are PDB-named FGCObject::GGCObjectReferencer and
# FSlateApplication::CurrentApplication.
# NOTE the project name misspells "Flying" as "Flyinh" — use it verbatim.
"UE4.15-Flying|UE415_Flyinh-Win64-Shipping|-|GObjects=142ccc200|142ccc210,GNames=142c92508,GWorld=142ce7770,GEngine=142ce6898"
# 4.15.3 Development + DebugGame — the same project's non-Shipping twins, and the OLDEST config
# group in the corpus. They anchor the far end of the non-Shipping GNames question: healthy at
# 4.15 / 4.23 / 4.27 / 5.3, collapsed to GNAM_V1-only at 5.7.4 / 5.8.x.
# SparseDelegates is ABSENT BY DESIGN on all three 4.15 rows (4.23+ feature), not missing.
# GNames here is the pre-4.23 `TNameEntryArray*`, taken from `FName::GetNames`'s load at +4 with
# NO -0x10 (that adjustment is an FNamePool/Blocks artifact). `pdb_globals.py` does this
# automatically now, and the recipe was validated by reproducing the Shipping row's recorded
# GNames=142c92508 above before being used on these two.
"UE4.15-FlyingDev|UE415_Flyinh_Development|-|GObjects=1456b9b40|1456b9b50,GNames=1456501c0,GWorld=1456d8b40,GEngine=1456d7838"
"UE4.15-FlyingDbgGame|UE415_Flyinh_DebugGame|-|GObjects=1456bcbc0|1456bcbd0,GNames=14564e5a0,GWorld=1456dbbc0,GEngine=1456da8b8"
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
# 4.23.1 DebugGame — the same project's non-Shipping twin, and the LOWER BRACKET on the
# GNames-on-non-Shipping-5.8 gap: everything resolves here (GNames n=16), so that gap is NOT a
# property of non-Shipping builds in general. Double-derived like the rest (pdb_globals.py +
# 151-pattern replay); all five agree, zero contradicting candidates.
"UE4.23-FlyingDbgGame|UE423_Flying-Win64-DebugGame|-|GObjects=1462508f0|146250900,GNames=146231c00,GWorld=14636ffd8,SparseDelegates=145fc79c0,GEngine=14636d3e0"
"UE4.24-DropIn|DropIn_UE424|-|GObjects=1471db720|1471db730,GNames=1471bca00,GWorld=1472ea620,SparseDelegates=146da38d0,GEngine=1472e74a0"
"UE4.25-Everspace2|ES2-UE425|-|GObjects=1444b0520|1444b0510,GNames=144497d00,GWorld=1445f1160,SparseDelegates=1440070c0,GEngine=1445edad8"
"UE4.26-Satisfactory|Satisfactory_UE426|-|-CoreUObject-:GObjects=1803f9210|1803f9220,-CoreUObject-:SparseDelegates=1803f37d0,-Core-Win64:GNames=180659380,-Engine-:GWorld=18182a0b8,-Engine-:GEngine=181826658"
"UE4.27-DropIn|DropIn|-|GObjects=14a3aa670|14a3aa660,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910,GEngine=14a528890"
# Two more symbolised 4.27s. Both report `++UE4+Release-4.27-CL-0`, i.e. built from an engine
# SOURCE tree rather than a launcher binary — which is consistent with them shipping PDBs at all.
"UE4.27-Breeders|Breeders_of_the_Nephelym|-|GObjects=1445f32d0|1445f32e0,GNames=1445b7000,GWorld=14473a7f8,SparseDelegates=1441a88b0,GEngine=144736ee8"
"UE4.27-Maelstrom|Maelstrom|-|GObjects=145b90c10|145b90c20,GNames=145b54940,GWorld=145cd4ee8,SparseDelegates=145839d00,GEngine=145cd15c8"
# ── UE 4.27.2 "Flying" template, built by the maintainer in ALL THREE CONFIGS ──────────────────
# One project, one engine, one day; the BUILD CONFIG is the only variable. That is what makes this
# a control group rather than three more games, and it settles a question the corpus could not
# answer before: whether the GOBJ_DI427_* patterns encode "UE 4.27" or "a Development build".
# They encode the config — see the three-config table in Himmel.h's DI427 block. UE4.27-DropIn,
# their previous sole oracle, is itself a Development build, which is why it looked version-shaped.
#
# All five values on all three rows are DOUBLE-derived and the two agree exactly:
#   (1) `py tools/pe/pdb_globals.py <pdb>` — an MSF/S_PUB32 decode, validated by reproducing the
#       UE4.23-Flying and UE5.8-StackOBot rows below/above byte-for-byte before being trusted here;
#   (2) a full 151-pattern byte replay of Himmel.h against .text, which converges on the same VA
#       for every target with ZERO contradicting candidates (GObjects n=6, GNames n=16, GWorld
#       n=17, Sparse n=4, GEngine n=5 on Development; same on the other two).
# Ghidra was never opened. Not live-verified — these are template projects, not games.
#
# GNames: no `NamePoolData` and no `?GetNames@FName@@` symbol here either (the standing rule holds
#   at 4.27), so it comes from FNameDebugVisualizer::GetBlocks, minus 0x10 — Development
#   @0x141f29e70 = `48 8d 05 d9 da 7c 07 c3` -> 0x1496f7950.
# GObjects: the `|base+0x10` alias is CORRECT here (pre-5.8 layout) and is confirmed rather than
#   assumed — the DI427 trio resolves to base+0x10 while ES53_1/G42_2/GH_4/PS2/PS3/SAT426_1
#   resolve to the base. The 32-byte FUObjectItem does NOT move ObjObjects.
# DECOYS, all present with PLAIN aliases that find_syms3.java WILL hand you (Development VAs):
#   GCoreObjectArrayForDebugVisualizers @1496c6890 (a pointer; its runtime VALUE is the ObjObjects
#   VA), GObjectArrayForDebugVisualizers @14925f358 (a reference to that — two indirections off),
#   GNameBlocksDebug @14961a1e0 (holds NamePoolData + 0x10).
#
# THE DEVELOPMENT ROW IS THE ONE THAT EARNS ITS SCAN. It takes over DropIn's sole-oracle role for
# GOBJ_DI427_1/2/3, converting a dependency on an external store into a locally rebuildable asset.
"UE4.27-FlyingDev|UE427_Flying_Development|-|GObjects=14973e660|14973e670,GNames=1496f7940,GWorld=1498c0e18,SparseDelegates=149272bd0,GEngine=1498bc7d0"
# DebugGame is NEAR-REDUNDANT for engine globals and it is honest to say so: UE builds DebugGame's
# ENGINE modules optimized like Development, so the codegen these patterns see is the same — the
# DI427 hit counts are IDENTICAL (832/1415/246) at different addresses. Keep it as the control that
# proves that claim, or comment it out to save a headless run; it is not coverage.
"UE4.27-FlyingDbgGame|UE427_Flying-Win64-DebugGame|-|GObjects=1497416a0|1497416b0,GNames=1496fa980,GWorld=1498c3e58,SparseDelegates=149275bb0,GEngine=1498bf810"
# Shipping is the NEGATIVE control, and it is the most informative of the three: all three
# GOBJ_DI427_* score ZERO here on the same source, while GNames/GWorld/Sparse/GEngine all still
# resolve. It is also the corpus's first 4.27 Shipping oracle from an engine we know is Epic-stock
# (Breeders/Maelstrom/DQ7R are third-party builds).
"UE4.27-FlyingShipping|UE427_Flying-Win64-Shipping|-|GObjects=1448a9500|1448a9510,GNames=14486d1c0,GWorld=1449f18b0,SparseDelegates=144457b60,GEngine=1449edf98"
"UE5.2-Satisfactory|SF521_pdb|-|@SF521@"
# ── UE 5.3 ThirdPerson, built by the maintainer in all three configs ───────────────────────────
# 5.3's FIRST SYMBOLISED ORACLE, and it closes the corpus's worst version hole: until now the only
# 5.3 binary was Avowed — no PDB, and truth for **1 of 5 targets** (SparseDelegates alone), so
# GObjects/GNames/GWorld/GEngine had ZERO ground truth at 5.3 anywhere. Every other UE5 version had
# at least one 5/5 row.
#
# Two things it is positioned to settle:
#   * Avowed's 20-byte packed FUObjectItem is ATTRIBUTED to the Obsidian fork but has never been
#     measured against a stock 5.3. Same evidentiary gap DropIn's 32-byte item had until 2026-07-29,
#     and the same fix: a stock build of the same version. It also gives GOBJ_AV1/AV2 — currently
#     0-correct outside Avowed — their first real decoy check.
#   * It BISECTS the non-Shipping GNames collapse (fine at 4.27, GNAM_V1-only at 5.7.4, and
#     5.0-5.6 untested). Whichever way the Development/DebugGame rows land, the interval halves.
#
# NO C++ TOOLCHAIN WAS NEEDED, and that is worth recording because the obvious route fails: a C++
# project on 5.3 dies in UBT with "must be compiled with VS2022 17.4 (MSVC 14.34.x) or later …
# detected 14.29.30159". Cause (from UBT's own log) is the FamilyRank pick — UE 5.3 predates
# 14.44/14.50/14.51 so it ranks them all 4 ("unknown"), while the one family it recognises, 14.29
# from VS2026's v142 component, ranks 3 and wins — then fails the >=14.34 gate. The launcher engine
# ships UnrealGame{,-Win64-DebugGame,-Win64-Shipping}.exe WITH PDBs, so a Blueprint-only project
# packages all three configs with nothing compiled.
"UE5.3-ThirdPerson|ThirdPerson53_Shipping|-|GObjects=146859320|146859330,GNames=1467b2f80,GWorld=1469c6208,SparseDelegates=1465f1650,GEngine=1469c3418"
"UE5.3-ThirdPersonDev|ThirdPerson53_Development|-|GObjects=14e7b1b90|14e7b1ba0,GNames=14e6d9640,GWorld=14e99c0a8,SparseDelegates=14e527700,GEngine=14e997078"
"UE5.3-ThirdPersonDbgGame|ThirdPerson53_DebugGame|-|GObjects=14e7c4b90|14e7c4ba0,GNames=14e6ec640,GWorld=14e9af0a8,SparseDelegates=14e53a700,GEngine=14e9aa078"
# ── UE 5.4.4 ThirdPerson, built by the maintainer in all three configs ────────────────────────
# Epic stock: CL 35576357, `++UE5+Release-5.4`, IsLicenseeVersion=0, IsPromotedBuild=1.
# It closes the LAST UE5 version with no symbolised oracle. Elliot was the corpus's only 5.4 and
# its truth is disassembly-derived with no PDB, so every 5.4 claim rested on that single unproven
# reading; now Elliot can be checked against a stock build of its own version.
#
# ⚠ THE PATCH VERSION IS THE POINT: 5.4.4 is EXACTLY MindsEye's engine version. docs/
# mindseye-fork-notes.md is a full re-derivation playbook for a UE 5.4.4 LICENSEE FORK (reordered
# FChunkedFixedUObjectArray, 32-byte FUObjectItem with UObject*@+0x10, XOR-obfuscated FNameEntry)
# whose "this is what the fork CHANGED" claims were all measured against inference about stock 5.4.
# These rows are that missing control — same version, same patch, Epic-stock. Anything MindsEye
# does differently is now a measurable delta rather than an attribution.
#
# Truth is double-derived and the two agree on all five, all three configs: `pdb_globals.py` and a
# full 151-pattern byte replay. Shipping consensus is strong across the board (GObjects n=5 on the
# +0x10 alias, GNames n=13, GWorld n=15, Sparse n=3, GEngine n=5) — no thin single-voter cells
# like the 4.10 rows have.
# NOTE this is a PACKAGED ThirdPerson project, not the engine's prebuilt UnrealGame target. That
# was deliberate: the prebuilt target is free but content-free, so it cannot serve the
# gameplay-feature matrix (no ACharacter to possess). These binaries serve both jobs.
"UE5.4-ThirdPerson|ThirdPerson54_Shipping|-|GObjects=1478245d0|1478245e0,GNames=14776d880,GWorld=1479a46f0,SparseDelegates=1475a52a0,GEngine=1479a1318"
# ⚠ THE DEV/DEBUGGAME PAIR CLOSED THE non-Shipping GNames BISECTION, and the answer is 5.4.
# Measured 2026-07-29 — the transition is between 5.3 and 5.4, one version, not the 5.4-5.6 band
# this pair was expected only to narrow:
#     5.3 Dev + DbgG : GNames 15/15 patterns correct, lands GNAM_ES53_1, 0 wasted
#     5.4 Dev + DbgG : GNames  1/6  patterns correct, lands GNAM_V1, 2240 wasted   <-- the edge
#     5.7.4 DbgG     : GNames  1/8  patterns correct, lands GNAM_V1, 2199 wasted
# Both 5.4 configs are byte-for-byte identical in behaviour (same 2240), consistent with UE
# building DebugGame's ENGINE modules optimized like Development. So 5.5/5.6 are NO LONGER NEEDED
# for this question — anything they add is coverage, not bisection. If a fix pattern is ever mined,
# THIS is the pair to mine it against: 5.3-vs-5.4 is the smallest interval containing the change.
# Bonus corroboration: the Shipping row's profile tracks UE5.4-Elliot (GObjects 8/15 vs 9/15,
# GNames 13/16 vs 13/17, GWorld 15/16 vs 13/14), which independently supports Elliot's
# hand-derived, PDB-less truth — the first check that row has ever had.
"UE5.4-ThirdPersonDev|ThirdPerson54_Development|-|GObjects=14fce7520|14fce7530,GNames=14fbfa7c0,GWorld=14feec7a0,SparseDelegates=14f9d21d0,GEngine=14fee7570"
"UE5.4-ThirdPersonDbgGame|ThirdPerson54_DebugGame|-|GObjects=14fcf7520|14fcf7530,GNames=14fc0a7c0,GWorld=14fefc7a0,SparseDelegates=14f9e21d0,GEngine=14fef7570"
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
# STACK O BOT — Epic's own sample project, built by the maintainer in Shipping against unmodified
# engine source. The 5.7.4/5.8 PAIR is a controlled A/B: same game, same config, adjacent engine
# versions, so any behavioural difference is attributable to the engine alone. That pair is what
# root-caused the UE 5.8 reflection break.
# NamePoolData from FNameDebugVisualizer::GetBlocks @0x141313030 minus 0x10; all five rebase onto
# the live run with ZERO residual (live base 0x7FF7B6880000).
"UE5.7.4-StackOBot|StackOBot_Shipping_UE574|-|GObjects=148fc8dd0|148fc8de0,GNames=148efad40,GWorld=149153b90,SparseDelegates=148d21ff0,GEngine=149156ae0"
# UE 5.8 — THE ONLY 5.8 IN THE CORPUS, and the one row where GObjects takes a SINGLE value:
# 5.8 moved FUObjectArray::ObjObjects +0x10 -> +0x00, so the base IS ObjObjects and `base + 0x10`
# is ObjObjects.NumChunks, an int32. Adding the usual `|base+0x10` alias would score a hit on a
# chunk counter as "correct". Do not add it back.
# ALSO THE ORACLE FOR THE 5.8 REFLECTION BREAK: `virtual ~FFieldClass()` (Field.h:101
# @5.8.0-release) puts a vfptr at FFieldClass+0x00, so FFieldClass::Name is +0x08 here and +0x00
# on the 5.7.4 row above. One-second PDB test: `??1FFieldClass@@UEAA@XZ` (U = virtual) exists here
# and NOT in the 5.7.4 PDB, which exports `??1FFieldClass@@QEAA@XZ` (Q = non-virtual).
# NamePoolData from GetBlocks @0x1415ABEB0 minus 0x10. All five rebase onto the live run with zero
# residual, confirmed twice at two different ASLR bases.
"UE5.8-StackOBot|StackOBot_Shipping_UE58|-|GObjects=149f88940,GNames=149eba940,GWorld=14a00b530,SparseDelegates=149c92700,GEngine=14a00e248"
# ── UE 5.8.1 StackOBot, Shipping + Development ────────────────────────────────────────────────
# ⚠ BOTH PROJECTS WERE STILL BEING ANALYSED IN GHIDRA when these rows were written (2026-07-29) —
# their .rep carried a live .lock. Confirm the lock is gone before sweeping, and note the rows do
# NOT depend on that analysis: every value came from the PDB + a raw byte replay, not from Ghidra.
# NO `|base+0x10` alias on either — 5.8 rule, same as the 5.8.0 row above.
#
# Derivation is the same double-derived flow as the 4.27 Flying rows: `pdb_globals.py` (validated
# against the 4.23 and 5.8.0 rows) + a 151-pattern byte replay. The Shipping row agrees on all
# five. The DEVELOPMENT row agrees on four — GNames is PDB-only, and that is a REAL FINDING, not a
# derivation failure; see the GNames-on-non-Shipping-5.8 note in GROUND-TRUTH.md. Expect the
# regression matrix to show ❌ for UE5.8.1-StackOBotDev / GNames, and leave it showing that.
"UE5.8.1-StackOBot|StackOBot_Shipping_UE581|-|GObjects=1499c8940,GNames=1498fa940,GWorld=149a4b530,SparseDelegates=1496d7700,GEngine=149a4e248"
# The Development row is the corpus's FIRST non-Shipping UE5 oracle, and it is worth its scan for
# exactly that reason: it is the only row that can regress-test the two config-only gaps below.
"UE5.8.1-StackOBotDev|StackOBot_Development_UE581|-|GObjects=153108e40,GNames=153000540,GWorld=1531e9cb8,SparseDelegates=152d67c30,GEngine=1531ee050"
# ── Three non-Shipping oracles, imported 2026-07-29 with `-import ... -noanalysis` ─────────────
# ALL THREE PROJECTS ARE RAW IMPORTS — no Auto Analyze, deliberately. The sweep reads raw bytes
# (scan_patterns.java touches only getMemory/getBytes/getImageBase), analysis is ~88% of a .rep,
# and a 300 MB non-Shipping EXE takes 3-4 h to analyze for zero benefit here. Do NOT "fix" these
# by analysing them. Each was verified post-import by running scan_patterns.java against the
# truth below; all five targets behave exactly as the offline derivation predicted.
#
# WHAT THEY ARE FOR: they are the only rows that exercise a NON-SHIPPING config, where two
# targets nearly collapse. Their GNames cells are SUPPOSED to be ugly — see the
# GNames-on-non-Shipping-UE5 entry in GROUND-TRUTH.md. Measured on the real imports:
#   5.7.4 DbgG : GNames -> GNAM_V1 ok=2/192 (pri 870)   Sparse -> SPARSE_MEL55_1 ALONE
#   5.8.0 DbgG : GNames -> GNAM_V1 ok=1/198 (pri 870)   Sparse -> SPARSE_X1/X2 only
#   5.8.0 Titan: GNames -> GNAM_V1 ok=1/199 (pri 870)   Sparse -> SPARSE_X1/X2 only
# In every case the priority walk LANDS on GNAM_CT3 (700) with a decoy, so GNames here depends
# entirely on ValidateGNames rejecting ~2,300 decoys before reaching V1 in the last-resort band.
"UE5.7.4-StackOBotDbgGame|StackOBot_DebugGame_UE574|-|GObjects=151c85900|151c85910,GNames=151b80980,GWorld=151eba9f0,SparseDelegates=151905f70,GEngine=151ebf250"
# 5.8.0 DebugGame, same project as the UE5.8-StackOBot Shipping row above -> a config-only A/B.
"UE5.8-StackOBotDbgGame|StackOBot_DebugGame_UE58|-|GObjects=153863480,GNames=15375a800,GWorld=153944338,SparseDelegates=1534bcc30,GEngine=1539486e0"
# 5.8.0 DebugGame from a DIFFERENT project ("Titan"), which is what RULES OUT the gap being
# project-specific: its coverage matches the StackOBot DebugGame row down to the pattern IDs.
"UE5.8-TitanDbgGame|Titan_DebugGame_UE58|-|GObjects=154422a80,GNames=154319e00,GWorld=154503938,SparseDelegates=154056c30,GEngine=154507ce0"
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
# HOGWARTS LEGACY (UE 4.27, no PDB). NO GS_TRUE on purpose — without symbols there is no truth to
# supply, and a GUESSED truth is worse than none (it mislabels every hit as a decoy).
#
# ⚠ MEASURED CAVEAT — this binary is DENUVO-PROTECTED and has NO `.text` SECTION AT ALL. Ghidra
# reports 379.69 MB of "executable" bytes across `.udata` (105 MB) and `.xpdata` (274 MB); a normal
# monolithic probe (TQ2) has 123 MB of real `.text`. So its 9,499 hits are matches against
# ENCRYPTED DATA, not code.
#
# Consequence for §6 of the report: hits/MB is computed over executable bytes, so this row adds
# ~380 MB of non-code to the DENOMINATOR and pushes every pattern's density DOWN. That statistic
# exists precisely to predict real-game collision cost, so folding a packed binary into it makes it
# read better than reality. Treat the §6 numbers as a lower bound while this row is in.
#
# It is kept anyway because it answers a different question well: 86 MISS / 0 spurious-correct
# shows the tables do not explode on a protected binary. If §6 is ever load-bearing for a decision,
# either give sweep.sh a per-row `noise=0` flag that aggregate_sweep.py honours, or drop this row
# for that run — do not silently reason from a diluted density.
"UE4.27-Hogwarts|Hogwarts_Legacy|-|"
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
  # GB_* are dump_blocks.java's env; harmless to scan_patterns.java and vice versa, so both
  # scripts can be driven from this one table without a per-script branch here.
  GS_OUT="$SWEEP_OUT" GS_TSV="$SWEEP_OUT/patterns.tsv" GS_TRUE="$TRUE" GS_TAG="$TAG" \
  GB_OUT="$SWEEP_OUT" GB_TAG="$TAG" \
     "$HL" "$GHIDRA_PROJS" "$PROJ" "${PROC[@]}" -noanalysis -readOnly \
     -scriptPath "$REPO/tools/ghidra" -postScript "$SWEEP_SCRIPT" > "$LOG" 2>&1
  local rc=$?
  echo "   done $TAG (exit=$rc)  log: $LOG"
  # Record failures to a FILE, not a variable: each call runs in its own background
  # subshell, so a counter incremented here would never reach the parent. The file also
  # carries the TAG, which `wait -n` alone cannot tell you.
  [ "$rc" -ne 0 ] && printf '%s\t%s\t%s\n' "$TAG" "$rc" "$LOG" >> "$SWEEP_OUT/_failures.tsv"
  # Was `echo` — so the function returned ECHO's status and every job "succeeded", no
  # matter what Ghidra did. The printed `(exit=N)` text was always right (`$?` is expanded
  # before echo runs); only the function's own status was wrong.
  return "$rc"
}

# Ghidra takes an EXCLUSIVE lock per project, so parallelism is safe ACROSS projects but never
# within one. SWEEP_JOBS controls how many projects run at once.
JOBS="${SWEEP_JOBS:-3}"
running=0
rm -f "$SWEEP_OUT/_failures.tsv"

# Does this shell have `wait -n` (bash 4.3+)? Probe the VERSION, never the exit status.
# The old code was `wait -n 2>/dev/null || wait`, which was safe only while one_project
# always returned 0. Now that it propagates Ghidra's status, that idiom would read a
# FAILED JOB as "wait -n is unsupported" and fall through to a bare `wait` — draining every
# remaining job at once while `running` dropped by only 1, collapsing concurrency to 1 for
# the rest of the sweep. A version probe cannot be fooled by a job's exit code.
have_wait_n=0
if [ "${BASH_VERSINFO[0]:-0}" -gt 4 ] ||
   { [ "${BASH_VERSINFO[0]:-0}" -eq 4 ] && [ "${BASH_VERSINFO[1]:-0}" -ge 3 ]; }; then
  have_wait_n=1
fi
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
  if [ "$running" -ge "$JOBS" ]; then
    # `|| true` because a failing job now returns non-zero and we account for it via
    # _failures.tsv, not here — reaping must not abort or change control flow.
    if [ "$have_wait_n" -eq 1 ]; then
      wait -n || true
      running=$((running - 1))
    else
      wait || true          # no `wait -n`: this drains everything, so reset the counter
      running=0
    fi
  fi
done
wait

FAILED=0
if [ -s "$SWEEP_OUT/_failures.tsv" ]; then
  FAILED=$(wc -l < "$SWEEP_OUT/_failures.tsv" | tr -d ' ')
  echo
  echo "!! $FAILED project(s) FAILED — the sweep's results are INCOMPLETE:"
  while IFS=$'\t' read -r ftag frc flog; do
    echo "   $ftag  (exit=$frc)  $flog"
  done < "$SWEEP_OUT/_failures.tsv"
  echo "   A missing scan TSV silently becomes a missing ROW in REPORT.md, which reads as"
  echo "   'that pattern was never tested' rather than 'that project did not run'."
fi

echo
echo "== sweep complete. Aggregate with:"
echo "   py tools/ghidra/aggregate_sweep.py \"$SWEEP_OUT\""
[ "$FAILED" -eq 0 ] || exit 1
