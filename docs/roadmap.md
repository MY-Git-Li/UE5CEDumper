# Roadmap — Current State

Snapshot of capabilities and per-game configuration. Updated when
behaviour or test coverage changes; pair with [todo.md](todo.md) for
upcoming work and [dev-log.md](dev-log.md) for the historical commit
trail. Build number tags reflect when each row reached its current
state.

> **Last refreshed**: 2026-05-26 (build 738 on `dev`). New: **Value Search tab** — CE-style First Scan / Next Scan over UPROPERTY fields. Walks GObjects + per-class field index, returns enriched candidates (class + field + defining class + value), refines with the 8 standard CE scan types. Session-based; 5-min idle expiry. Three new pipe cmds (`begin_value_scan` / `refine_value_scan` / `end_value_scan`). Native C++ fields explicitly excluded — banner in UI surfaces the limitation. MVP types: Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool. FString / TArray / FVector deferred. See [dev-log.md](dev-log.md) 2026-05-26 entry.

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

Two ways to get `ue5_invoke_helper.lua` into the user's .CT:

- **Tools -> Inject Helper into Current CE Table** (build 610, preferred) —
  one click; sends the embedded helper straight into the open CE table
  via the AOBMaker plugin's new `InjectTableFile` pipe command (`findTableFile`
  delete-if-exists -> `createTableFile` -> `Stream.write` -> verify).
  Requires the AOBMaker CE Plugin to be loaded; falls back gracefully
  with a status-bar hint if unavailable.
- **Tools -> Export CE Helper Lua File...** — manual fallback when
  AOBMaker isn't installed or CE isn't running. Writes `scripts/ue5_invoke_helper.lua`
  to a user-chosen path; user adds it via CE `Table -> Add File...`.

### Invoke param picker (build 711-715, Stage 1+2)

InvokeParamDialog rows for pointer-flavoured params (UObject* / UClass* /
SoftObject / WeakObject / LazyObject / Interface) now surface the expected
target UClass and provide one-click filling:

- **Label** — `[UObject*: AActor, 8B, off=0x10]` instead of bare
  `[UObject*, 8B]`. The DLL extracts `FObjectPropertyBase::PropertyClass` in
  `WalkFunctions` and ships it via the `obj_class` pipe field; C#
  `FunctionParamModel.ObjectClassName` carries it to the UI. Empty when the
  param genuinely has no class constraint or when an older DLL is in use
  (backward-compatible).
- **[Pick…]** — opens `ObjectInstancePickerDialog` pre-filtered to the
  expected UClass via the existing `find_instances` pipe command. Substring
  match catches subclasses (which is what the param actually accepts). Double-
  click row or "Use selected" fills the textbox with the chosen instance's
  address.
- **[null]** — fills `0x0` (WorldContextObject and other optional pointer
  params).
- **[self]** — fills the invoke target's own address (for utility functions
  that re-target themselves).

`ParamBufferBuilder.IsPickablePointerType` is the canonical 7-type list
locked by test theories — adding a new pointer property type requires
mirroring it in both the DLL `WalkFunctions` extraction and the C# helper
or the build catches the drift.

Stage 3 (class validation — DLL `validate_object_class` round-trip before
invoke) deferred until a real class-mismatch crash motivates it; picker
output is almost always class-correct in practice.

## Value Search — by-value scan (build 738)

New tab "Value Search" between Interesting Properties and Console. Fills
the search-by-value gap (PropertySearch was by-name; InstanceFinder was
by-address; this is the third axis). Port of discrete's Phase 27b
`ValueScanSession` shape with UE-specific scan engine.

| Component | What it does |
|---|---|
| `ValueScan::SessionManager` (DLL) | 5-min idle-expiry session container; holds candidate vector + DataType between RPCs. |
| `Aura::ScanForValue` | Walks GObjects (skips UClass meta), per-class field index cached lazily via `Ubel::WalkClassEx` filtered to matching DataType, typed-read + `ComparePredicate`. 15 s deadline. |
| `Aura::RefineCandidates` | Re-reads each candidate's bytes, applies predicate (prev-value scan types compare against stored `Candidate.prevValue`), prunes failing, updates `prevValue` on survivors. |
| `Fern.cpp` handlers | `begin_value_scan` / `refine_value_scan` / `end_value_scan` (3 new cmds; total **39**). |
| `ValueSearchPanel.axaml` | Top banner (locked by test): "Native C++ fields (non-UPROPERTY) cannot be found here — use Cheat Engine's raw memory scan for those." DataType + ScanType selectors, Value/Value2 inputs (visibility bound to ScanType), Candidates DataGrid with Class.Field / Type / Value / Offset / Addr / Instance columns. |

Supported MVP DataTypes: Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool (BoolProperty bitfields normalized to 0/1 via `FieldMask`). **Not supported**: FString, FName, FText, TArray\<T\>, FVector / FRotator / FTransform — deferred to v2. Native C++ fields (non-UPROPERTY) are explicitly excluded — banner directs the user to CE for those.

Scan flow: `Exact / Bigger / Smaller / Between` for First Scan; `Changed / Unchanged / Increased / Decreased` additionally available for Next Scan. Cross-tab navigation: per-row "Open in Live Walker" opens the owning instance with the field address pre-populated.

## Property freeze — horizontal class-wide (build 719)

PropertySearch rows gain a **Freeze** button that ships an AA Script
into CE locking the property at a constant across **every live instance**
of the owning class. Different from the CE-XML export (Route A — kept
in [todo Speculative](todo.md)) which pins a single pointer chain to a
single instance: the freeze script enumerates instances by class+offset
every 5 s, so respawns / new spawns / destroys are handled transparently.

| Component | What it does |
|---|---|
| `Mimic::CMD_LIST_INSTANCES` (mailbox cmd 6) | Paginates live (non-CDO) `UObject*` pointers of a class via `Aura::FindInstancesByClass(exactMatch=true)`. 128 ptrs / page, hard cap 2000 instances. |
| `scripts/ue5_freeze_helper.lua` (embedded) | `freezeProperty(cfg) → handle` API; tick timer (50 ms default) + rescan timer (5 s default); type writers for bool / int8-64 / uint8-64 / float / double; shares `_ue5_invoke_busy` reentrancy flag with invoke helper. |
| `FreezeScriptGenerator` | Renders AA Script with editable `CFG = {...}` block, per-script keyed handle table so multiple Freeze scripts coexist. |
| `FreezeValueDialog` | Single-input modal with type-aware validation (bool accepts true/false/1/0). |
| `Tools -> Inject/Export Freeze Helper Lua` | Sister entries to the invoke-helper Tools menu — one-click AOBMaker inject or manual file export. |

Supported property types (v1): BoolProperty, ByteProperty, Int8/16/32/64Property, UInt16/32/64Property, EnumProperty, FloatProperty, DoubleProperty. **Not supported**: StructProperty / FString / FName / containers (deferred).

Gating: the Freeze button is disabled when AOBMaker plugin isn't reachable; tooltip explains the setup requirement. **No clipboard fallback** — script delivery is AOBMaker-only by design (keeps the surface tight).

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
  Player +2 (build 673), Enemy / Weapon +2 (build 687 — Phase 2),
  GameMode/GameInstance/SaveGame +2; AnimInstance/AnimMontage/AnimSequence/
  AnimNotify/AnimGraph/AnimBlueprint -2 (build 673 — surgical compound
  names, was bare "Anim" before which broke game classes like AnimMan_*),
  NiagaraSystem/NiagaraEmitter/NiagaraComponent/SoundCue/SoundWave/
  SoundBase/AudioComponent/ParticleSystem/ParticleEmitter -2,
  UserWidget/WidgetComponent -1
- **Flag bonuses**: BlueprintCallable +2, BlueprintEvent +1, Pure-or-
  Const safe getter +1, ParmsSize > 64 -1
- **Threshold = 5**; Show All toggle bypasses

Per-row actions:
- **Live**: open in Live Walker via `find_instance` lookup, falls back
  to ClassStruct tab if class is CDO-only
- **AA(B)**: shortcut into the Copy AA Script (Baked) flow; reuses the
  same dialog as the LiveWalker AA(Baked) button

**AOBMaker availability gating** (build 608, refined build 689) — when
AOBMaker CE Plugin pipe is unreachable, both LiveWalker Functions and
Interesting Funcs panels show a single inline italic status indicator
("AOBMaker plugin not found — AA Script export will fall back to
clipboard"). Was previously a per-row Notes column on every row (pure
noise since the value is VM-level); build 689 collapsed it to one
place. Re-checked on tab activation (5s cooldown so rapid switching
doesn't stack 2s pipe-connect timeouts). Send-time guard distinguishes
"pipe broke during send" (warning) vs "plugin never configured"
(informational).

## Interesting Properties Finder (B' — build 670-687)

Symmetric tab to Interesting Funcs but for properties. Backed by
`search_properties_batch` (build 685) — DLL walks GObjects ONCE and
checks every property against every keyword in one pass, ~30× faster
than the build-670 sequential approach. Uses `PropertyScoringTable.cs`
(separate from KeywordScoringTable since property naming differs from
function naming) + shared `ClassLocationScorer.cs`:

- **Categories**: Stats / Combat / Resources / Movement / Utility
  (no Inventory — uses Resources instead; no ExplicitMovementCheats —
  property names rarely encode cheat-mode verbs)
- **Class bonuses (PropertyRules)**:
  Character/Pawn/PlayerController/PlayerState/AbilitySystem/AttributeSet/
  Inventory/Equipment +3; Player +2; GameMode/GameInstance/SaveGame/
  PlayerProfile +2; **LocalPlayer / GameViewportClient / HUD /
  UCheatManager / CheatManager +4 with ⚠ Unusual Location flag**
  (build 670); Weapon / Projectile / Battle / Enemy +2 (build 678 + 687
  — empirically derived from 15-game cross-game analysis)
- **No visual/audio penalties on Property side** — property names alone
  filter the noise (an "PlaybackSpeed" on UAudioComponent doesn't match
  any keyword, so it scores 0)
- **Threshold = 4** (slightly lower than Function side because per-hit
  weights are lower)

Key concept: **Unusual Location flag** highlights cheat-relevant fields
hosted in non-canonical containers (LocalPlayer / GameViewportClient /
HUD / CheatManager) — the kind of properties developers placed outside
where you'd think to look first.

Per-row actions:
- **Live**: open the property's owning class in Live Walker via
  `find_instance`, fall back to ClassStruct on CDO-only classes.
  Pre-fills the LiveWalker SearchText with the property name so the
  user lands with it highlighted.
- **Name**: copy the bare property name to clipboard.

## Dump-for-analysis pipeline (build 676-687)

`Export → Dump All Metadata (.jsonl)` streams every class + props +
funcs as JSON Lines via the existing pipe endpoints (`get_object_list`
+ `walk_class` + `walk_functions` — no new DLL command). Mirrors the
`IsClassLikeMeta` whitelist so BPGCs are included.

Companion Python script `scripts/analysis/analyze_dumps.py` aggregates
N dumps cross-game and emits a Markdown report with:

- Top OWN property names (with `_resolve_own_props` filter to dedup
  inherited fields counted N times across the inheritance chain)
- Top OWN property TOKENS — candidate keywords, cross-referenced
  against existing category buckets
- Candidate Unusual Location class tokens — class × prop-token
  co-occurrence ranked by cross-game frequency
- Same three sections for the Function side (build 687)
- `--min-games` filter (default 3) drops single-game spikes

15-game corpus (DQ7R / DQI&IIHD2D / ES2 / FSD-DRG / FactoryGameSteam /
Geri / HogwartsLegacy / ManorLords / NMKART / Octopath / Stray / TQ2 /
TowerOfMask / ff7rebirth / ff7remake) drove the build 678 + 687 scoring
table additions. Two subsequent bias rechecks at **17 games** (Star Wars
Jedi: Fallen Order + Ghostwire: Tokyo, 2026-05-12) and **18 games**
(Frontiers — first MMO/ARPG-flavoured entry, 2026-05-20) confirmed
stability with **no further keyword additions** in either pass. Anti-
bias workflow documented in
[scripts/analysis/README.md](../scripts/analysis/README.md) — users
whose preferred genres aren't well-represented dump their own games +
PR with analysis output as evidence.

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
- **The Artisan of Glimmith** ✅ (UE 4.27, exe `Geri-Win64-Shipping.exe`,
  24K objects): full scan + GWorld OK. Build 647 cross-version
  reproducer for the wrong-vtable-slot bug (PE was on `vtable+0x220`,
  the old detector picked `0x218` — off by 1 slot) — **fixed and
  fully re-verified on build 648 (2026-05-11)**: pattern scanner
  picks the correct slot, validator confirms 1260 hook fires in
  1500ms, and four real invokes succeed: KismetMath helpers (Add_IntInt
  = 7, Multiply_FloatFloat = 12) via static-native fast path, plus
  instance methods via game-thread dispatch (CharacterMovementComponent
  ::GetMaxJumpHeight = 89.99 float, PlayerCameraManager::
  GetCameraLocation = FVector struct).
- **Squad-Win64-Shipping** ✅ (UE 5.7, 240K objects): build 488 user
  reported 13 `get_object_list` 0xA0 UTF-8 exceptions → root cause was
  Serie wide-path surrogate encoding bug, fixed in build 555. Should
  now work clean post-560.
- **Barn Finders** ✅ (UE 4.25, 137K objects, build 560 user logs):
  full scan OK, UE5-Extended layout (strict). GWorld ✅. No new issues
  surfaced — pre-existing `find_by_address` `stoull` exception on
  malformed `0xrank` input from the Lookup field is already fixed in
  build 561+ (UI side `AddressHelper.TryNormalizeAddress` + DLL side
  `Renge::TryStrToAddr` noexcept). Walker Misaligned-EnumProperty
  warnings (163 in session) cleaned up by `Scharf.h` in build 582.
- **Colossal** ✅ (UE 5.03, 41K objects, build 560 user logs, publisher:
  Atan, exe `Colossal-Win64-Shipping.exe`): full scan OK, UE5-Extended
  layout (strict), TaggedFFieldVariant (UE5.3+). GWorld ✅
  (`GWLD_ES2_6`). Project still ships Epic default copyright/company
  placeholder strings — no publisher thumbprint match expected.
- **Extinction** ✅ (UE 4.15, 230K objects, build 560 user logs,
  publisher: Modus Games, exe `Extinction.exe` under `Blink/Binaries/
  Win64/`): **lowest UE version verified end-to-end** — expands the
  previously-documented 4.18+ floor down to 4.15. Flat (non-chunked)
  `FFixedUObjectArray`, UProperty mode (UE < 4.25), `UField::Next=+0x28`.
  Patterns: GOBJ_RE2 (1.8s, 2 batches) / GNAM_CT3 (4.6s, 4 batches) /
  GWLD_G42_1 (3.3s, 3 batches) — ~10s total scan but all three globals
  resolved on first scan and validated. GWorld ✅.
- **Star Wars Jedi: Fallen Order** ✅ (UE 4.21, 313 887 objects, build
  704 user logs 2026-05-12, EA Origin / Steam launcher): full scan OK —
  GObjects=0x7FF7316F5CD0, GNames=0x12B65A10080,
  **GWorld=0x7FF7317EBAB8** (non-zero, valid). DynOff full UE4 layout
  (`UField::Next=+0x28`, `UStruct::ChildProperties=+0x50`,
  `UProperty::ElemSize=+0x34`). Install path
  `H:\SteamLibrary\steamapps\common\Jedi Fallen Order`, exe layout has
  TWO identical 58.4 MB copies side-by-side in `SwGame\Binaries\Win64\`:
  `SwGame-Win64-Shipping.exe` (canonical UE name) +
  `starwarsjedifallenorder.exe` (EA-launcher target name). CE shows the
  running process as the latter. **Proxy DLL caveat**: neither
  `version.dll` nor `dinput8.dll` proxy gets loaded by the EA launcher —
  must inject via Cheat Engine after the game is running. Scan +
  dump pipeline works identically once the DLL is in-process.

GWorld success ratio: **29 / 29 (100% of tested games)** as of 2026-05-12.
Satisfactory (modular DLL build): scan side OK — `Macht::AOBScanAllModules`
falls through to `FactoryGameSteam-CoreUObject-Win64-Shipping.dll`
under `Engine\Binaries\Win64\` and the 15-game dump corpus includes
its 4,868 BPGCs cleanly. Proxy deploy was previously broken because
the UI skipped the `Engine` subfolder; fixed build 691 (the real
game .exe lives in `Engine\Binaries\Win64\` for this title, not
under `FactoryGame\`).
Star Wars Jedi: Fallen Order: scan side OK as above; proxy deploy
inherently broken because of EA launcher (see lesson in
[lessons-learned.md → Proxy DLL Deploy](lessons-learned.md#proxy-dll-deploy)).
For both EA-launcher and other launcher-wrapped titles, recommend CE
manual injection as the documented workaround.

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
