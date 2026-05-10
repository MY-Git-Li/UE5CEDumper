# Roadmap — Current State

Snapshot of capabilities and per-game configuration. Updated when
behaviour or test coverage changes; pair with [todo.md](todo.md) for
upcoming work and [dev-log.md](dev-log.md) for the historical commit
trail. Build number tags reflect when each row reached its current
state.

> **Last refreshed**: 2026-05-10 (build 609, post-tokeniser + AOBMaker gating polish on `dev`).

-----

## Capability matrix

| Layer | Drill-down | Find Refs |
|-------|-----------|-----------|
| Object / Class / Interface | ✅ | ✅ |
| Weak / Soft{Class} / Lazy (single + array) | ✅ | ✅ |
| TArray of any pointer-shaped inner | ✅ | ✅ |
| TMap / TSet (Object/Class) | ✅ | ✅ (allocated slots only) |
| Delegate (single FScriptDelegate) | ✅ | ✅ (v3) |
| MulticastInline / MulticastDelegate | ✅ | ✅ (v3) |
| TArray<FScriptDelegate> | ✅ | ✅ (v3) |
| MulticastSparseDelegate (UE 5.0+) | ✅ bindings via SPARSE_ES2_1 AOB (build 561-577) | ✅ v4 sparse pass (build 565) |
| MulticastSparseDelegate (UE 4.23-4.27) | ❌ FObjectKey outer key, separate AOB needed | ❌ |
| OptionalProperty\<pointer / weak\> | ✅ | ✅ |
| OptionalProperty\<scalar Int/Float/Bool/Byte/Enum\> | ✅ trailing-bIsSet | — |
| OptionalProperty\<String / Name / Text\> | ✅ intrusive sentinel + value (build 530) | — |
| OptionalProperty\<Struct\> | ✅ (build 528) | ✅ depth-3 descent through inner struct (build 528) |
| FieldPathProperty | ❌ | ❌ |
| TMap / TSet with weak-like inner sides | — | ❌ (v4 candidate) |

## Per-game configuration

Persisted in HintCache JSON per PE hash, surfaces in the Pointer panel:

| Setting | Range | Default | Pipe cmd | Since |
|---|---|---|---|---|
| UE version override | Auto / 4.18-4.27 / 5.0-5.8 | Auto (detect) | `set_ue_version_override` | build 549 |
| Invoke timeout | 1000-60000 ms | 5000 ms | `set_invoke_timeout` | build 583 |

## UFunction invoke export (build 590-596)

Three buttons per UFunction row in LiveWalker:

| Button | Mode | Output |
|---|---|---|
| **Generate Script** (`INV`) | In-CE form (existing) | AA Script with `createForm` interactive popup |
| **Pipe Invoke** (`PIPE`) | In-app via DLL pipe | Live invoke + decoded result inline |
| **AA(Baked)** (new) | Non-interactive AA Script | Self-contained AA Script with values baked at generation time; depends on `ue5_invoke_helper.lua` embedded in the user's .CT |

Tools menu **Export CE Helper Lua File...** writes
`scripts/ue5_invoke_helper.lua` to a user-chosen path so they can drop
it next to their .CT and add via Cheat Engine `Table -> Add File...`.

## Interesting Functions Finder (build 597-609)

New tab "Interesting Funcs" between Property Search and Game Classes.
Backed by the `list_all_functions` pipe cmd which flattens every
UFunction across every UClass into a single one-shot payload (~4MB
for a 50k-function game). Client-side scoring via
`KeywordScoringTable` + `KeywordTokenizer` (build 609 -- whole-token
match instead of substring, so short acronyms HP/MP/SP/XP/TP only
fire on standalone tokens):

- **Categories**: Stats / Inventory / Movement / Combat / Utility (with
  ExplicitMovementCheats sub-bucket: NoClip/Fly/God/Ghost/Invincible
  weighted +8 to outscore Utility's noisy `Cheat` + `Debug` matches)
- **Class bonuses**: Character/Pawn/PlayerController/PlayerState +3,
  GameMode/GameInstance/SaveGame +2; Anim/Niagara/Sound/Audio/Particle
  -2; UI/Widget -1
- **Flag bonuses**: BlueprintCallable +2, BlueprintEvent +1, Pure-or-
  Const safe getter +1, ParmsSize > 64 -1
- **Threshold = 5**; Show All toggle bypasses

Per-row actions:
- **Live**: open in Live Walker via `find_instance` lookup, falls back
  to ClassStruct tab if class is CDO-only
- **AA(B)**: shortcut into the Copy AA Script (Baked) flow; reuses the
  same dialog as the LiveWalker AA(Baked) button

**AOBMaker availability gating** (build 608) — both LiveWalker
Functions and Interesting Funcs DataGrids carry a "Notes" column at
the end. When AOBMaker CE Plugin pipe is unreachable the column shows
"AOBMaker plugin not found — AA Script export will fall back to
clipboard". Re-checked on tab activation (5s cooldown so rapid
switching doesn't stack 2s pipe-connect timeouts). Send-time guard
distinguishes "pipe broke during send" (warning) vs "plugin never
configured" (informational).

## Publisher detection

`Genau::DetectPublisherFromPE` reads `LegalCopyright` + `CompanyName`
via `VerQueryValueW` and matches against `kPublishers[]`. A match
forces `bLowConfidence=true` (override the Tier promotion) AND
applies the publisher's `biasFallback` when detection fails. Currently:

| Publisher | Bias fallback | Reason |
|---|---|---|
| `SQUARE_ENIX` | UE 4.27 | UE4 forks shipped without canonical version strings + bundled SDKs leak misleading 5.x numbers |

Adding more entries casually risks wrong bias overriding correct
detection — wait for a real misdetection report before adding.

## Tested games (last verified 2026-05-10)

- **Everspace 2** ✅ (UE 5.4): item template ID via container scan; Find
  Refs v3 returns 9 correct references in 224ms (cache hot, scan
  complete: 1180536/1180536); auto-scroll-to-field after Open works;
  Class Structure for `LocalPlayer` shows correct fields after the
  class-like routing fix; PropertySearch type filter `OptionalProperty`
  finds 9 matches across 5 real classes + 4 test-object fields.
  **`SPARSE_ES2_1` resolves SparseDelegates @ +9AA5F10** (build 575,
  ground truth from PDB).
- **Titan Quest II** ✅ (UE 5.7, bCasePreservingName=**true**, 486k
  objects): cross-version validation — same `SPARSE_ES2_1` AOB hits
  `+D46D170`, exercises FName=16 walker branch (inner stride 0x28).
  Was source of 194 `ValidateArrayElemSize` warnings/session pre-build
  583 → now Debug-only.
- **DQ I&II HD-2D / FF7 Rebirth / FF7 Remake** (UE4 forks, Square Enix
  publisher): Square Enix publisher detected → ⚠ Low Confidence badge +
  Publisher chip; user can set Override = UE 4.27 / 4.18, persists
  across launches. Char Lv / HP / Party Lv in non-reflected memory
  (custom allocator) — out of reflection scope; use CE pointer scan.
  **Build 589 verified**: invoke_timeout=6000 round-trip OK after
  `FillPointerSnapshot` fix; Square Enix purple chip + Low Confidence
  amber badge both surface from `scan_status` payload now.
- **Meltopia** ✅ (UE 5.0.5): full scan OK; was source of ~75
  misalignment + ~58 empty-map false-positives + 4 UFunction timeouts →
  all resolved in build 582-583 (Scharf alignment helper + empty-map
  guard + per-game invoke timeout 6000ms via UI NumericUpDown).
- **Squirrel With A Gun** ✅ (UE 5.0.2): full scan OK; was source of
  `walk_instance` `std::invalid_argument` crash on unsubstituted CE
  placeholder `0x[ply_base]` → resolved by `Renge::TryStrToAddr` in
  build 582.
- **Caravan Sandwitch** ✅ (UE 5.0.4): full scan OK; was source of 49
  empty-TMap false-positives → resolved by count=0+Data=null guard.
- **Retro Rewind Demo** ✅ (UE 5.0.4): full scan OK.
- **The Occupation** ✅ (UE 4.19): UE4 path with `GNAM_CT3`, GWorld OK.
- **TimeSplitters Rewind Early Access V0.3.3** ✅ (UE 4.25): full scan,
  GWorld OK.
- **Squad-Win64-Shipping** ✅ (UE 5.7, 240K objects): build 488 user
  reported 13 `get_object_list` 0xA0 UTF-8 exceptions → root cause was
  Serie wide-path surrogate encoding bug, fixed in build 555. Should
  now work clean post-560.

GWorld success ratio: **25 / 26 (~96%)**. Untested: Star Wars Jedi.
Failing: Satisfactory (modular DLL — pattern likely needs to live in
`CoreUObject-Win64-Shipping.dll` instead of the main exe).

## Long-running concerns

These are not actionable next-session work — see
[todo.md](todo.md) for that — but they are worth re-checking before
shipping any major Walker / Detection change:

- **`kPublishers[]` table review** — every new publisher we add changes
  detection behaviour for all that publisher's titles. Touch with care;
  prefer per-game user override over a publisher-wide bias unless we
  have ≥3 misdetected titles from the same publisher.
- **AOB pattern decay** — UE engine source rotates roughly every minor
  version. The 128 patterns in `Himmel.h` are time-stamped per
  introducing build; any pattern that hasn't matched in ≥4 minor
  versions is a candidate for removal at the next clean-up.
- **HintCache schema additions** — the `FillPointerSnapshot`
  refactor (build 588) closed *one* instance of a recurring trap. New
  scan-time fields must land in BOTH `CMD_GET_POINTERS` *and*
  `CMD_SCAN_STATUS` payloads. The shared helper enforces this for
  pointer fields; the equivalent guarantee for object-list / walker
  payloads does not yet exist.
