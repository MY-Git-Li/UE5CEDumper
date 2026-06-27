# Dev Log — Archive: builds 939–1177 (2026-06-06 → 2026-06-15)

Split out of the main [dev-log.md](../dev-log.md) to keep it under ~3000 lines.
Append-only milestone history, newest first. grep `^## ` for the index.

Older entries: builds 715–937 in
[dev-log-2026-06-pre-build-940.md](dev-log-2026-06-pre-build-940.md); builds ≤696 in
[dev-log-2026-05-pre-build-700.md](dev-log-2026-05-pre-build-700.md).

-----

## 2026-06-15 — Main window placement persistence + restartable-apps opt-in (build 1177)

The UI didn't remember where it was: every launch reset position / size / monitor. Now
it restores the last session's placement, and opts into Windows' reboot-relaunch.

**Placement persistence.** [`WindowStateStore`](../ui/UE5DumpUI/Services/WindowStateStore.cs)
saves `x/y/w/h/max` to `%LOCALAPPDATA%\UE5CEDumper\window-state.txt` (plain `key=value`, no
JSON → Native-AOT safe, same pattern as the teleport hotkey store). `App` attaches it before
the window is shown (no visible reposition) and the window saves on close. Reuses
`MainWindow`'s existing normal-vs-maximized snapshot, so a restored-then-un-maximized window
lands on the right monitor.

**Off-screen reset (the headline ask).** Windows gives no automatic per-app window memory —
the app must validate. On `Opened` (when `Screens` is reliable), the restored NORMAL rect is
checked against THIS session's monitors via the pure
[`WindowPlacement.IsVisibleEnough`](../ui/UE5DumpUI/Services/WindowPlacement.cs) (≥120×40 px
overlap with some working area). A window saved on a now-absent second monitor, or pushed
off-screen by a resolution drop, **resets to a default-size window centered on the primary**.

**Restartable-apps opt-in.** New `IPlatformService.RegisterForRestart` (default no-op so the
5 test doubles + any non-Windows impl need no change) overridden in `WindowsPlatformService`
with `RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG)` — if the app is
open when the user reboots / installs an update, Windows relaunches it on next sign-in (Win10
+ Win11, gated by the user's "restart apps" setting), and the placement restore above puts it
back where it was. Not on crash/hang (avoids relaunch loops); registration only triggers if
alive at shutdown, so a normal close means "don't come back".

+22 placement/visibility unit tests → **1505 C# green**; Native AOT publish clean (45.9 MB).
⚠ in-app LIVE-VERIFY PENDING (drag/maximize/2nd-monitor-removed behavior; reboot relaunch).

-----

## 2026-06-15 — Third proxy DLL: dxgi.dll (for EXEs importing neither version nor dinput8) (build 1172)

**The problem (owner):** "The Adventures of Elliot" (SQUARE ENIX UE4.27 demo) works via
DLL injection but **both** the `version.dll` and `dinput8.dll` proxies are dead — the
proxy file sits in the game folder yet nothing loads. PE import-table analysis of
`Elliot-Win64-Shipping.exe` settled it: the EXE imports **neither** `version.dll` nor
`dinput8.dll` (static *or* delay), so the OS loader never loads those proxies at all. It
*does* statically import `dxgi.dll` (`CreateDXGIFactory`/`CreateDXGIFactory1`) and
`WINMM.dll` — it's a D3D12 title (`D3D12\`/`DML\` folders present). So the code comment's
"version.dll is loaded by almost every process" premise is not reliable; this build is a
counterexample. (Diagnostic method: parse the EXE import directory directly — see also
`d3d11.dll` statically importing `dxgi!CreateDXGIFactory2`, which proves a partial proxy
would break D3D device load.)

**Fix — `dxgi.dll` as a third proxy target.** dxgi is statically imported by every
D3D11/D3D12 UE game on Windows and is not a KnownDLL, so it's the reliable hijack target
for this population. Unlike the version/dinput8 proxies (plain C forwarders — every export
has a known signature), dxgi exports several **undocumented internals** (`DXGID3D10*`,
`Compat*`, `PIX*`) whose prototypes we don't know, so forwarding goes through
signature-agnostic **MASM jmp-thunks** (`jmp qword ptr [mProcs+8*N]`), mirroring the
vendored RE-UE4SS proxy generator. All 20 real-dxgi exports are forwarded at their exact
ordinals so d3d11/d3d12 can still resolve their dxgi deps through us.

- **C++:** `Lugner_Dxgi.asm` (20 thunks, disasm-verified), `ProxyDxgi.def`
  (`name=f<N> @ord` + the UE5_* C ABI), `Lugner_Dxgi.cpp` (`DxgiProxy_Init` resolves the
  real System32 dxgi into `mProcs[]` — same full-path `LoadLibrary` pattern as the working
  version proxy, so no base-name self-recursion). `Heiter.cpp` calls `DxgiProxy_Init` at
  the very top of `DllMain` ATTACH (before the proxy mutex, since a passive forwarder still
  forwards) and logs the result from the (Sein-initialised) auto-start thread.
- **Build:** `BUILD_PROXY_DXGI` CMake target with `enable_language(ASM_MASM)` (dxgi target
  only); `build.ps1 -Target ProxyDxgi` → `dist\proxy\dxgi.dll`. No `/DELAYLOAD` (we don't
  import dxgi). `-Target All` now builds all three proxies.
- **UI:** `ProxyType.Dxgi` + `Constants.ProxyDllNameDxgi`; `RefreshDeployStatusAsync`
  conflict detection generalized from the binary version↔dinput8 pair to "all other proxy
  names"; third RadioButton + strings/tooltip; `IsDxgiSelected` mirror.

Built clean (`dxgi.dll` 2.0 MB, export table matches real dxgi 1:1), **470 dll_helpers +
1479 C# green**. Deployed to the Elliot folder for live verification. ⚠ in-game LIVE-VERIFY
PENDING (proxy load + pipe + scan).

**Two Proxy Deploy panel fixes (same build, found during live test):**
1. **False redundancy warning.** The conflict check fired whenever *any other* proxy DLL
   existed in a folder, regardless of whether the selected one was deployed — so a game
   with only `version.dll` falsely showed "Both dinput8.dll and version.dll are deployed"
   on the dinput8/dxgi tabs. Now `ProxyDeployService.BuildConflictMessage` (pure, tested)
   warns **only when 2+ of our proxies actually coexist** in the folder, listing all
   present ones, independent of the selected radio. A single deployed proxy of any type =
   no warning. N-proxy-safe (iterates the enum; no hardcoded pair).
2. **Update All was selected-type-only.** Now `UpdateAllAsync` resolves the source DLL for
   *every* proxy type (`<exeDir>/proxy/<name>`) and, per game, updates each
   already-deployed proxy of the *same* type to the latest (new dxgi over old dxgi, new
   version over old version) — regardless of the selected radio, never pushing a fresh
   type the user didn't choose. Adding a 4th type needs no change. +4 conflict tests.

-----

## 2026-06-15 — Value Search → Pivot handoff (value-locator, C2-lite) (build 1161)

The cheap complement to change-driven discovery: when the user **can see a value**
(Gold = 9410), Value Search already finds its `(class, field, address)` — but it was
the **only** source panel missing the C5 "Pivot this" handoff that PropertySearch /
InterestingProperties / LiveWalker have. Added it so a value-scan hit reaches a grouped
pivot in one click.

A per-row **"📊 Pivot"** button on the Value Search results grid (gated by
`PivotEnabled`, so it's hidden when experimental features are off) raises
`ValueSearchViewModel.NavigateToPivot(ClassName, FieldName)` — the hit already carries
both — which `MainWindowViewModel.HandlePivotHandoff` routes to the Class Pivot tab via
the existing `PivotForAsync`. Pure VM/XAML reuse of the C5 contract: new event +
`PivotEnabled` flag + `PivotThis` command (mirrors `PropertySearchViewModel`), wired in
the same `if (snapshotStore != null)` block + `UpdatePivotHandoffEnabled`. No DLL/pipe
change. +2 `PivotHandoffCommandTests` → **1478 C# green**, build.ps1 -Target UI publish
clean. Together with build 1160 this closes the loop both ways: **value-known** → Value
Search → Pivot, and **value-unknown** → Discover → Pivot.

-----

## 2026-06-15 — Class Pivot: change-driven discovery — the automatic front-door (build 1160)

**The problem (owner):** Class Pivot is under-used because it assumes you already
know *which class* to pivot. Within a class the key is already auto-suggested
(`PivotKeyScorer.SuggestKey` + value pre-tick + Identity fallback), but when the
**target is unknown** the user is stuck at "pick one of thousands of classes".

**The fix (Phase C — C3, change-driven):** a new **"🔍 Suggest targets"** front-door
on the Class Pivot tab. Capture two snapshots around an in-game action (spend gold /
take damage / level up), pick *Before* + *After*, press **Discover** → the system
ranks the **(class, property) targets that MOVED** and shows a short list; **Use →**
pivots the chosen one (selects the class, forces the discovered property as a
projected value, switches to Identity grouping, runs). No class/key guessing.

**Why the change signal works:** game-relevant fields are the ones that change
between captures; static config never moves. The ranking is a transparent weighted
sum — **interest** (`PropertyScoringTable`, the calibrated HP/Gold/Damage scorer,
dominant) + **change** (monotonic move beats jitter) + **selectivity** (a change
confined to FEW instances is the thing you touched, not global render/anim noise) −
**population** (penalises ubiquitous huge-instance fields). Every sub-score is
exposed on the candidate for a future cross-game calibration pass.

**Implementation (pure C# over the SQLite corpus — zero DLL/pipe change):**
- `PivotDiscoveryEngine` (pure, AOT-safe): rolls (instance, field) value-sequences
  up per (class, prop), gates on "moved", ranks. Same engine/store split as
  `SpcEngine` / `PivotEngine` — unit-testable without a DB.
- `SnapshotStore.DiscoverChangesAsync` reuses the **exact** SPC cross-snapshot
  intersection load — extracted into a shared `LoadIntersectedCandidatesAsync`
  helper that both `SpcQueryAsync` and discovery now consume (no duplicated load,
  no SPC behaviour change — guarded by the existing `SpcStoreTests`).
- Models `DiscoveryQuery / DiscoveryInput / DiscoveryCandidate / DiscoveryResult`.
- `ClassPivotViewModel`: `DiscoverFrom/To` (default = last two captures), `Discover`
  + `UseDiscoverCandidate` commands, results grid; the C5 handoff tail extracted to
  `SelectClassAndTickPropAsync` (shared by right-click "Pivot this" and "Use →").
- UI: a `<Expander>` "🔍 Suggest targets" section at the top of the Class Pivot tab
  (before/after pickers + ranked grid: Class · Property · Changed N/M · Δ · Category
  · sample sequence · score). Join mode = Strict (works cross-session too).

**Tests:** +10 `PivotDiscoveryEngineTests` (rank math: interest beats neutral,
selective beats ubiquitous, unchanged dropped, direction, determinism, cap),
+7 `DiscoverStoreTests` (end-to-end load/join/wiring), +3 `ClassPivotViewModelTests`
(discover → use → pivot). **1476 C# green, build.ps1 -Target UI publish clean (AOT).**
⚠ **In-game live-verify pending** (capture 2 snapshots in a real game, Discover,
confirm the ranked list surfaces the gameplay field). Remaining C3: the heavier
scorer (Jaccard stability / compound key) + C2 find-by-value are still open.

-----

## 2026-06-15 — Dump All Metadata: meta line records FUObjectItem layout (build 1158)

The `.jsonl` dump's `{"kind":"meta"}` line now carries **`item_layout`**
(`classic` / `unpacked57` / `packed57`), **`item_obj_offset`**, and
**`packed_unverified`** — sourced from the existing `EngineState` fields
(`ItemLayoutMode` / `ItemObjOffset` / `ItemPacked`). This lets the offline
analysis corpus flag a dump captured under the UNVERIFIED UE5.7+ packed layout,
whose reconstructed addresses are best-effort. Pure additive field on
`DumpAllService.WriteMetaLineAsync`; **no DLL / pipe change**.

Context (owner question): the Dump All Metadata feature is **not adversely affected**
by the UE5.7+ packed / `+0x08` within-item-offset work — it enumerates objects via
`get_object_list`, which (like every GObjects consumer) reads object pointers solely
through the single layout-aware `Aura::GetByIndex` (documented invariant in
`Aura.h`). On classic-layout games the packed/`+0x08` branches are dormant and the
dump is byte-identical to before; on UE5.7+ the `+0x08` path makes it correct; packed
mode is a dormant last-resort (`DetectItemSize` `gCount==0` only). The new meta field
just makes the layout self-evident in the output. +1 test; 1457 C# / 470 dll / 31
utf8 green.

-----

## 2026-06-15 — CE export: Collapse chain — fold breadcrumb pointer spine into one CE entry (build 1156)

New LiveWalker **Collapse chain** toggle for **Copy CE XML** / **Copy CE Field**
(MERGED main via PR #286, `7d2e133` / merge `fcb8276`; owner live-verified in CE).
When on, the `GWorld → … → target` navigation spine collapses into a SINGLE CE
multi-level-pointer entry — `base` + one folded node + the target field with its
drill-down — instead of one nested group per breadcrumb. So the deep
`OwningGameInstance → m_savedata → SaveSlotList → [1] → OriginalPlayer → Params`
path pastes as `base → (OwningGameInstance ▸ m_savedata ▸ SaveSlotList ▸ [1] ▸
OriginalPlayer) → Params → [0..6]`.

**Implementation** (`CeXmlExportService.cs`; DLL unchanged):
- `FoldBreadcrumbSpine` + a shared `ProjectBreadcrumb` projection
  (`offset, derefAfter = IsPointerDeref || IsContainerView`). `flattenChain` param
  threaded through `GenerateHierarchicalXml` / `GenerateAobWrappedXml`.
- Offsets in CE document order `[F] ++ reverse(D[1..])` as summed hex, where `D[]` =
  each run of offsets up to & incl. a deref and `F` = the trailing inline run after
  the last deref. Two adjacent pointers → `[0, OffsetB]`; pointer+inline →
  `[OffsetB]`; pure-inline spine → `Address=+F` with no `<Offsets>`. Worked example
  `D=[180,2A8,7D0]`, `F = 6F8+18 = 710` → `+180` / `[710,7D0,2A8]` (matches the
  hand-authored reference XML).
- Folds only when ≥ 2 navigation breadcrumbs (fewer = no-op, byte-identical to off).
- **Robustness:** the fold reads only `(offset, derefAfter)` per breadcrumb and never
  touches the leaf subtree (`EmitFields`), so new expandable field types can't break
  the fold and the fold can't change leaf rendering. Both nested and folded emit
  paths share `ProjectBreadcrumb` so they can't diverge. Every breadcrumb the app
  creates is inline or single-deref (DataTable's 2-level deref = two single-deref
  breadcrumbs), so the fold is total over the breadcrumb model.

**UI / wiring:** `LiveWalkerViewModel.CollapseChain` passed at all 4 export call
sites (Copy CE XML / Field × AOB / hierarchical); `Collapse chain` checkbox in
`LiveWalkerPanel` (toolbar order AOB, Collapse chain, Guess?) + `en.axaml` strings.
Docs: `docs/export-formats.md` "Collapse chain Option" section (worked example).
**+8 tests** (`FoldBreadcrumbSpine` direct: user example exact offsets, Rule-1
`[0,B]`, Rule-2 `[B]`, pure-inline, <3-node no-op; + AOB/hierarchical integration +
flatten-off regression). **1456 C# / 470 dll / 31 utf8 green; UI publish trim/AOT clean.**

-----

## 2026-06-15 — Teleport: cursor first-force fix + cursor-hotkey checkbox scroll fix (build 1150)

Two refinements after the owner re-tested build 1147 (DQIII debug-cam OFF now
works ✅):

**1. Force Mouse Cursor: first press failed, OFF-then-ON worked** (`原因不明`).
Logs showed the DLL did the right thing every time (`SetInputMode_GameAndUIEx
invoked`, `bit now 1`), so the miss was game-side: a single `SetInputMode_GameAndUIEx`
from the game's running input state doesn't release the capture / show the cursor
until the input mode actually CHANGES. **Fix:** `ApplyCursorInputMode(show=true)`
now forces a **GameOnly→GameAndUI transition** (calls `SetInputMode_GameOnly` then
`SetInputMode_GameAndUIEx`), reproducing the manual OFF-then-ON workaround in one
press. Refactored the per-call packing into `InvokeWblInputMode`.

**2. "Global cursor hotkey" checkbox ate the first click + scrolled the panel.**
Classic ScrollViewer focus-driven `RequestBringIntoView`: clicking a
partially-visible control made the viewport scroll it into view and consumed the
first click (second click then worked). **Fix:** `TeleportPanel` handles
`RequestBringIntoViewEvent` on the content-root StackPanel (a child of the
ScrollContentPresenter, so it marks the event handled before the presenter's class
handler scrolls) → `e.Handled = true`. Focus no longer auto-scrolls; manual
wheel/scrollbar unaffected.

DLL 470/470, C# 1448/1448, utf8 31/31 green. ⚠ in-game re-verify pending (does the
forced transition make the first cursor force stick).

## 2026-06-15 — Teleport: cursor input-mode + DQIII debug-cam fix + Get-coords Lua record (build 1147)

Owner live-tested build 1144 on TQ2 + DQIII HD-2D. **Directional + coord TP work
on both** ✅. Three follow-ups from the test:

**1. Cursor force had no visible effect (TQ2 + DQIII).** Logs confirmed the
`bShowMouseCursor` write succeeds (`forced ON addr=… mask=0x01`, code 0) — but a
`GameOnly` viewport recaptures/hides the OS cursor regardless. **Fix:**
`Wirbel::SetMouseCursor` now also drives the input mode via `UWidgetBlueprintLibrary`
(`ApplyCursorInputMode`): show → `SetInputMode_GameAndUIEx(pc, null, DoNotLock,
bHideCursorDuringCapture=FALSE)` (the `false` is the lever — it defaults `true`);
hide → `SetInputMode_GameOnly(pc)`. Best-effort (no UMG ⇒ flag-only). Also re-reads
the bit so the reported state reflects reality. Still one-shot (per-tick re-set =
possible Phase 3).

**2. DQIII debug camera could enable but not disable** (pre-existing feature, not
this change). Log: `UE5_SetDebugCamera(1) -> state=0` then every disable
`already OFF — no-op`. Root cause: `DbgCam_ReadState` finds the DCC only via
`CheatManager.DebugCameraControllerRef`, which **DQIII never populates** → state
misread as OFF → disable no-ops (and enable misreports). **Fix:** instance-scan
fallback — when the CheatManager ref is empty, `UE5_FindInstanceOfClass(
"DebugCameraController")` finds the live DCC; its `OriginalControllerRef` is the
authoritative active flag (the same DCC the teleport hop already resolves). Now
enable reports ON and disable fires the toggle / controller-swap. (See
[[console-debugcamera-force]].)

**3. "Get current coords" Lua/mailbox call** (owner request). `TP_OP_GET_POSE=0`
already returns the 6 pose doubles; added a `TeleportScriptGenerator.Action.GetPose`
CE record that fires op 0 and prints `coords loc=(…) rot=(…)` from the mailbox
block. Batch 16→**17 rows**.

DLL 470/470, C# **1448/1448**, utf8 31/31 green. ⚠ in-game re-verify pending for
cursor (does the input-mode call make it visible / stick) + DQIII debug-cam OFF.

## 2026-06-15 — Teleport: directional + explicit-coordinate TP + force mouse cursor (build 1144)

Three user-requested teleport additions, all reusing the existing resolution
chain + tier ladder (so they inherit every per-game robustness path already built
for recall). Full design: [teleport-spec §16](teleport-spec.md).

**1. Directional teleport** (`Wirbel::TeleportRelative`). Step the pawn along its
facing by a signed distance (uu; negative = backward). Facing = invoke
`AActor::GetActorForwardVector`, falling back to `ControlRotation` Yaw/Pitch trig.
Two modes: **horizontal** (drop Z + renormalize — ground-plane "walk toward that
compass bearing", height kept) and **3D** (full forward incl. pitch — fly/noclip).
`dest = curWorld + unitFwd*distance` → `TeleportPawnTo` + `StopMovement`;
`SaveLastImpl()` first so **Recall last undoes it**; returns the re-read landed
pose (the "回算 X/Y/Z/Pitch/Yaw" requirement).

**2. Teleport to explicit coordinates (force).** Reuses the existing
`Wirbel::RecallExplicit` (no new core) — exact world X/Y/Z, no map check, optional
rotation. New surfaces: CE mailbox op + export + a dedicated UI section (X/Y/Z +
optional Pitch/Yaw/Roll + "Fill from current"). Also undoable.

**3. Force mouse cursor** (`Wirbel::SetMouseCursor`/`GetMouseCursor`). Write
`APlayerController.bShowMouseCursor` (a `BlueprintReadWrite` **bitfield**;
`SetShowMouseCursor` is not a UFUNCTION). Resolved via the reflected FBoolProperty
layout (`ResolveCursorBit`: FieldSize/ByteOffset/FieldMask probed ±, since
`FindField` drops ByteOffset). **One-shot toggle** (Phase 1): games that re-set
the flag every tick may revert it, and a captured input mode can still hide the OS
cursor (Phase 2 = keep-forcing + `SetInputMode_GameAndUIEx`). Pairs with Cursor
Teleport.

**Surfaces.** Exports 38→**42** (`UE5_TeleportRelative`,
`UE5_TeleportRecallExplicit`, `UE5_SetMouseCursor`, `UE5_GetMouseCursor`); mailbox
`TP_OP_RELATIVE=12` / `EXPLICIT=13` / `SET_CURSOR=14` / `GET_CURSOR=15`; pipe
`teleport_relative` / `set_mouse_cursor` / `get_mouse_cursor` (explicit reuses
`teleport_recall_marker`); UI 3 new sections + 4 hotkey rows
(`relative`/`coords`/`cursor_on`/`cursor_off`); CE `.CT`/AOBMaker batch 12→**16**
rows (directional/coord records bake the current field values).

**Tests.** +5 ScriptGenerator (ops 12/13/14 + 16-row batch), +9 ViewModel
(directional pass-through + pose apply, coords with/without rotation, fill-from-
current, cursor on/off/refresh/disconnect-reset, 16 hotkey rows). DLL helpers
470/470, C# **1447/1447** green; AOT clean. ⚠ in-game LIVE-VERIFY PENDING (esp.
cursor stickiness per-game + directional facing source on fixed-cam titles).

## 2026-06-14 — Teleport: TQ2 transform-refresh fix + cursor robustness + ViewTarget detector (build 1113-1116)

Chased the long-standing "TQ2 teleport doesn't move the character" via a new
diagnostic, **disproved the old "separate actor" verdict**, and fixed it.

**Root cause (disproves the old TQ2 verdict).** A `ViewTarget` diagnostic
(`Wirbel::DiagVisibleActor`) showed TQ2's `APlayerCameraManager.ViewTarget.Target`
== the possessed pawn (same addr), and the pawn `bp_tq2_player_character_C` is a
normal Character (child `SkeletalMeshComponent` at rel `(0,0,-96)` on the capsule
root + `CharacterMovement`). So **not a separate actor**. The real failure:
`K2_SetActorLocation` **returns success but is a no-op** on TQ2 (CMC reverts), and
because it claimed success the CMC-freeze path skipped the component-level setter
that refreshes the world transform — the raw write moved memory but left the
cached `ComponentToWorld` stale, so the mesh stayed at the old spot.

**Fix (build 1113).** In the CMC-freeze retry, never trust the actor setter's
return — ALWAYS also run `K2_SetWorldLocation` on the root (runs
`UpdateComponentToWorld`, propagates to the child mesh) + `DeepForceWorldPos`.
**TQ2 marker teleport now works** (verified). Gated to the CMC-freeze path, so
games that already work (SEED / DQ III) are untouched. Residual minor: the mesh
snaps over on the next move (CMC network smoothing; cosmetic, deferred).

**Cursor robustness (builds 1114-1116, generalizable).** Added
`GetHitResultAtScreenPosition` (screen-position trace, needs no cursor and no
`KismetSystemLibrary`), an **auto-scan of trace channels** (requested then 0..9,
so click-to-move ARPGs' custom ground channel is found without guessing), a
`(0,0)`-mouse → screen-center fallback, and pinpoint logging. Helps other top-down
games. **TQ2 cursor teleport stays blocked** — that build strips `GetMousePosition`
(returns 0,0 — virtual cursor), `GetViewportSize`, and `KismetSystemLibrary`, so
there's no generic way to read where the cursor points (per-game limitation).

**ViewTarget detector (kept, gated).** `DiagVisibleActor` now logs only when the
camera's view-target is a genuinely different actor than the pawn (a real
separate-actor game — none seen yet) — silent on normal teleports. The one-off
Root/Mesh dump that proved the TQ2 diagnosis was removed.

DLL-only (Wirbel.cpp); no C ABI / pipe / mailbox / UI change. AOT publish clean;
C# tests unaffected. ⚠ Two TQ2 caveats documented (cursor blocked; minor mesh
smoothing lag).

-----

## 2026-06-14 — Teleport POV: live-verify + raw cached-POV fallback + auto-refresh (build 1112)

**In-game live-verify of the read-only camera POV (build 1110):**

| Game | UE | POV (getters) | After fallback | Teleport (move) |
|------|----|---------------|----------------|-----------------|
| SEED Battle Destiny Remastered | 4.27 | ✅ | ✅ | ✅ |
| DQ III HD-2D Remake | UE5 HD-2D | ✅ | ✅ | ✅ |
| Octopath Traveler | UE5 HD-2D | ❌ | ✅ (raw) | ⚠ pawn moves, camera doesn't follow |
| Titan Quest II | UE5 | ❌ | ✅ (raw) | ⚠ then ✅ — setter no-op, fixed build 1113 |

**Root cause (from the new diagnostics, NOT the cooked-out hypothesis I first
guessed).** The local PlayerController resolves on all four; on TQ2 / Octopath
the camera getters **are present in reflection** (`getters found loc=1 rot=1`) —
they're *not* cooked out — but `ProcessEvent` returns no value, so `InvokeRetVec`
yields nothing. Critically the log also reported **`CameraCache` is reflected**
(Octopath off=992, TQ2 off=5472 on `TQ2PlayerCameraManager`), so a direct read of
the cached POV is possible.

**Fix — raw cached-POV fallback (DLL).** `Wirbel::ReadPovRaw` walks, fully by
reflection (no hardcoded struct layout): `APlayerCameraManager.CameraCachePrivate`
(`FCameraCacheEntry`) → `POV` (`FMinimalViewInfo`) → `Location` / `Rotation` /
`FOV`. Inner `UScriptStruct*`s come from `FStructProperty::Struct` via the same
`DynOff::FSTRUCTPROP_STRUCT` probe Ubel's value-walk uses; LWC width from each
field's reflected `Size`. `GetPovImpl` calls it when both invoke getters yield
nothing (and to backfill a rare partial), tagging `source = "raw"` (vs "invoke").
Surfaced as a chip in the UI POV header (+ `Source` on `TeleportPov`, pipe
`source`, mailbox `paramsData[81]`). The earlier diagnostic `LOG_WARN` now fires
only when the invoke AND the raw read both fail.

**Auto-refresh (UI).** When the Teleport tab's **Auto (0.5s)** toggle is on, each
tick now refreshes the camera POV alongside the pawn pose. On any POV failure the
update is **skipped silently** (last good values / "—" stay; no error, no clear),
so the pose display is unaffected on games where POV is unavailable. POV clears on
disconnect. Manual **Get POV** still surfaces the error code.

No C ABI / pipe / mailbox **shape** change (added a `source` value only). +2 tests
(POV source surfaced, disconnect clears POV).

**LIVE-VERIFIED ✅ (2026-06-14).** Both fall-back titles emit `src=raw` with sane
values matching each camera archetype: **Octopath** fixed HD-2D cam
(`pitch -16°, yaw 0, FOV 40`, static), **TQ2** angled ARPG cam
(`pitch -47°, yaw -116.6° fixed, FOV 75`, location/pitch drifting smoothly = a
live follow camera). LWC double-width read correct on both (UE5). So POV now reads
on all four tested titles: SEED + DQ III via the getters, TQ2 + Octopath via the
raw cached-POV fallback. Merged to main via PR #282.

-----

## 2026-06-14 — Teleport: remove the dead createHotkey Lua bundle (build 1111)

Deleted `TeleportLuaBundleGenerator` (the `createHotkey`-based "Teleport Lua
hotkey bundle") and its test. **Why:** it never reliably executed in the user's
Cheat Engine — the CE-side hotkey registration (`createHotkey`, an earlier cut
also leaned on the `executeCodeEx` round-trip that can't return export values)
didn't fire — and the "Copy CE Lua (hotkey bundle)" button that surfaced it was
already removed back in **build 1038** (when marker hotkeys moved to the app's
own OS-level `RegisterHotKey` capture). So the generator had been **dead,
unsurfaced code** ever since; it was confirmed to have **zero live references**
(only its own test + three doc-comments in `TeleportScriptGenerator`).

**The CE integration path is unchanged and intact:** `TeleportScriptGenerator`
emits per-action **mailbox AA memory records** (ship via AOBMaker
`CreateAAScript` or batch into a `.CT`); bind a **CE record-level hotkey** to a
record (reliable), or use the app's OS-level global hotkeys on the Teleport tab.
The `TeleportHotkeyScheme` enum (numpad / top-row / both) lived in the deleted
file and went with it — it was only ever consumed by the bundle.

Removed −2 files (−351 + −170 lines) and the bundle's 14 tests. Build + remaining
tests green; AOT publish clean. No DLL / pipe / mailbox change.

-----

## 2026-06-14 — Teleport: read-only Camera POV display + Get + Lua (build 1110)

Added a **read-only camera-POV readout** to the Teleport tab (Phase 1 of the POV
research). It surfaces the on-screen camera's world location / rotation / FOV —
**distinct from the pawn pose** — so the user can see, per game, whether the
camera follows the possessed pawn or is driven independently (the Octopath /
SE HD-2D / TQ2 class). **Read-only by design**: `APlayerCameraManager`'s POV is
recomputed every tick by `UpdateCamera`, so there is no universal Set POV (a
write is overwritten next frame); the real "move the camera" paths already exist
(Debug Camera on the same tab, or moving the view-target pawn, which teleport
already does). Full rationale + matrix in [teleport-spec.md](teleport-spec.md) §15.

**DLL (Wirbel).** `Wirbel::Pov` struct + `Wirbel::GetPov`: resolves
`PlayerController.PlayerCameraManager` (NO debug-camera hop — POV must reflect the
*active* view, which is the debug controller's when it's on) and invokes the
BlueprintCallable getters `GetCameraLocation` / `GetCameraRotation` / `GetFOVAngle`
(the Geri-verified struct-return invoke path) via new generic `InvokeRetVec` /
`InvokeRetFloat` helpers. Both location+rotation getters failing ⇒ `TP_ERR_INVOKE`;
FOV best-effort. Best-effort pawn world location (root `RelativeLocation`) included
for the camera↔pawn delta.

**Surfaces.** Export `UE5_TeleportGetPov` (11-double out). Mailbox
`TP_OP_GET_POV = 11` (POV block in `paramsData`: cam 6 doubles, FOV, pawn 3
doubles, hasPawn, source). Pipe `teleport_get_pov`. UI: read-only "Camera POV"
section + **Get POV** button + `pov_get` global hotkey row (OS `RegisterHotKey`)
+ `TeleportPov` DTO + the camera↔pawn delta hint. CE: a "Get camera POV"
`.CT` / AA **mailbox record** (`TeleportScriptGenerator`, op 11 — ticking it
fires the round-trip and prints the camera block) → the `.CT`/AA batch is now
12 rows. POV was deliberately NOT added to the `createHotkey` Lua bundle
(`TeleportLuaBundleGenerator`) — that path is unreliable in CE and unsurfaced
since build 1038; the working CE path is the mailbox AA record.

**Tests.** +1 `TeleportScriptGenerator` (op 11 + camera read-back), batch 11→12;
+2 `TeleportViewModel` (POV display + error-code). **LIVE-VERIFIED ✅** — see the
build 1112 entry above (SEED + DQ III read POV via the getters; TQ2 + Octopath via
the raw cached-POV fallback, with the camera-vs-pawn divergence confirmed on the
independent-camera titles).

-----

## 2026-06-14 — UE5.7+ packed FUObjectItem parsing (gated, UNVERIFIED) + peripheral handling (build 1108)

Implemented the third FUObjectItem within-item layout — the UE5.7+ **packed** encoding
(`UE_ENABLE_FUOBJECT_ITEM_PACKING`) where the `UObject*` is bit-split across two fields and
reconstructed — **before** any game exists to validate it (it is not Epic-default even in
`ue5-main`). Built strictly defensively: it can never regress an existing game and is loudly
flagged as unverified everywhere it surfaces.

**Core (DLL).**
- New dependency-free `dll/src/PackedItem.h`: `ItemLayoutMode {Classic,Unpacked57,Packed57}`,
  `PackedConsts {alignBits=3, ptrMaskBits=0x3FFF}`, pure `Reconstruct(flags, ptrLow, consts)`
  (`obj = ((flags>>32)&PtrMask)<<(32+AlignBits) | (ptrLow<<AlignBits)`) + a test-only `Encode`
  inverse so the math is round-trip unit-testable without a live game.
- `Aura.cpp`: `s_layoutMode` + `s_packedConsts` + `s_packedSerialOff`. `GetByIndex` /
  `GetSerialNumber` branch on the mode (a process-lifetime-constant → perfectly-predicted hot-path
  branch). New exports `GetItemObjOffset()` / `IsPacked()` / `SetPackedConsts()`.
- Detection: `DiagnosePackedLayout` promoted to `TryDetectPacked` (reuses a reconstruction-aware
  `ProbeStride` overload so scoring validates the REBUILT pointer, not the raw FlagsAndRefCount).
  Wired into `DetectItemSize` as a **last resort** in the `gCount==0` truly-unrecognized branch only
  — after both direct passes AND the weak tentative fallback — so it never beats even a weak direct
  match. Activates only on ≥2 reconstructed pointers resolving real FNames; logs
  `*** UNVERIFIED UE5.7+ PACKED ... ACTIVATED ***`.

**Peripheral (the only structural break).** `Fern.cpp` `get_ce_pointer_info` was the lone
GObjects-walk CE pointer chain. Fixed a **latent unpacked bug** (added `GetItemObjOffset()` to the
item hop — was hardcoded to item+0, wrong on Unpacked57) and added a **packed degraded path** (a
native CE chain can't do bit reconstruction → emit the absolute object address + `packed_layout` +
`warning`). All other peripherals (CE XML root, CSX `StructDataAddr`, +CE Field `FieldAddress`,
Teleport via GWorld property chains) consume absolute already-resolved addresses → correct once
`GetByIndex` reconstructs, no change needed.

**Surfacing (decision: log + badge + export note).** `FillPointerSnapshot` + `CMD_GET_OFFSETS`
now emit `item_layout_mode` / `item_packed` / `item_obj_offset` / `item_size`; `EngineState` carries
them. Top-bar **"⚠ Unverified UE5.7+ packed layout"** badge (mirrors the stale-DLL badge pattern).
Ambient `PackedLayoutNotice` embeds a best-effort note into CE XML / CSX output + a `[UE5.7+ packed?]`
prefix on +CE Field record names while active.

**Calibration (decision: now).** New `set_packed_consts` pipe command (`align_bits`/`ptr_mask_bits`/
`force`/`serial_off`) tunes the constants and force-enables packed mode at runtime (no rebuild),
echoing reconstructed `GObjects[0..7]` samples + names — the live-verify harness for the first real
packed game. C# `DumpService.SetPackedConstsAsync` + `PackedConstsResult` model.

**Tests.** +6 C++ round-trip/edge tests in `dll_helpers_test` (470 pass) — the only verification
possible today. +5 C# `DumpServiceTests` (packed EngineState parse + classic fallback +
CePointerInfo packed-degraded/direct + set_packed_consts samples). 1446 C# pass, AOT clean.

⚠️ **Still UNVERIFIED in-game** — pending a real `UE_ENABLE_FUOBJECT_ITEM_PACKING` game to calibrate
the two constants + the serial offset and confirm the +0x08 reconstruction.

-----

## 2026-06-13 — CSX export drilldown parity (Phase B) + depth-from-current-view tests + truncation note (Phase C) (build 1098)

Phase B + C of the CE/CSX export drilldown redesign ([ce-export-drilldown-spec.md](ce-export-drilldown-spec.md)).
Phase A shipped CE XML/Field container-value expansion (build 1085) + map fixes
(build 1090); CSX (`CsxExportService`) was still asymmetric — it expanded
`Map<…,Object>` but `Map<Name,Struct>` / `Set<Struct>` stayed a flat raw byte blob.

**Phase B — CSX reuses the one resolver + flattens container struct values.**
- `GenerateCsxAsync` now calls **`CeXmlExportService.ResolveDrilldownAsync`** (the
  unified pass that resolves top-level structs, pointers, AND container element
  *struct/object values* recursively to Drill Depth, populating `resolvedStructs`
  keyed by `StructDataAddr` + `resolvedInstances` keyed by `PtrAddress`). CSX keeps
  its own `ResolvePointerInstancesAsync` / `ResolveContainerPointerInstancesAsync`
  for the object-pointer-in-object-array / DataTable / multicast shapes the unified
  resolver doesn't descend (CE XML emits those as flat leaves; CSX builds real child
  structures) — no regression, the shared `resolvedInstances` dedupes overlap.
- `ConvertMapElementsToFields` stamps the value's absolute `StructDataAddr`
  (`MapDataAddr + index*stride + valOffset`) + `StructClassAddr` for struct values
  (mirrors `BuildContainerValueFields`); new `ConvertSetStructElementsToFields` does
  the same for `Set<Struct>` (`SetDataAddr + index*stride`). The address formula is
  byte-identical to the resolver's, so emit-time lookups hit.
- Emit: `EmitElement` / `BuildLiveChildStructure` thread `resolvedStructs`, and a
  `StructProperty` field (a container element value, or a struct member of a drilled
  target) routes to `EmitStructPropertyFlattened` (now indent-parametrised) —
  flattening its resolved sub-fields inline (`[idx] key / SubField`) and recursing
  into nested containers/pointers via `EmitElement`. Unresolved (depth exhausted /
  walk failed) → graceful raw-byte-block fallback.
- **Map value-offset bug** flagged for Phase B was already handled: CSX consumes the
  DLL's corrected `map_value_offset` (PR #277's `Scharf::RequiredAlignment` fix), so
  FName/WeakObjectPtr-valued maps land correctly with no extra CSX change.

**Phase C — depth semantics locked + truncation note.**
- Service-level tests assert **Drill Depth is measured from the current view, each
  container level costs one, breadcrumbs cost nothing** (spec §4): a
  `Map<Name,Struct>` whose value struct holds a nested `Map<Name,Struct>` walks one
  level at D=1 (inner map stays a pointer) and two at D=2 — verified in both the
  resolver (`ResolveDrilldown_Depth1_ExpandsOneContainerLevelOnly`) and CSX emit
  (`GenerateCsx_MapValueStruct_NestedMap_DepthMeasuredFromCurrentView`); plus a
  breadcrumb-length-independence test for `GenerateHierarchicalXml`.
- **Truncation note**: `Export CSX` now surfaces the same
  `⚠ Container element limit (N): Field (Map: X total, Y loaded)` status note that
  Copy CE XML already shows, so a container clipped by `ArrayLimit` no longer reads
  as a complete export. (Per the locked decision, no hard walk-budget cap — bounded
  by Drill Depth ≤8 + `ArrayLimit` + existing cycle detection.)

No DLL change. **1435 C# (+6: 4 CSX struct-value/depth, 2 CE XML depth) + 457 dll +
31 utf8 green; AOT publish clean (103 MB single-file).** Remaining drilldown gap:
CSX struct-array elements still use the shallow Phase-F `StructFields` preview rather
than the full resolver re-walk (CE XML's `EmitStructArrayProperty` does the re-walk);
nested-container truncation beyond the top level is still unreported (optional).

## 2026-06-13 — Map value-offset alignment fix + CE-export progress indicator + map value DropDownList (build 1090)

Three follow-ups on the CE export work, reported on SEED `PlayerSelectMsUnitList`
(`Map<EnumProperty, NameProperty>`).

**Map value-offset alignment (DLL — the garbage-values bug).** The container view
showed corrupted FName values (string fragments / CJK). Root cause:
`Macht::ComputeMapValueOffset` guessed the value's alignment from its **size** —
for an 8-byte FName (which is `2× uint32`, align **4**) it returned align 8, so
the value offset became 8 instead of 4, and the derived stride 24 instead of 20.
Every map element was then read at the wrong address. Fixed by passing the real
per-type alignment from the existing `Scharf::RequiredAlignment` (NameProperty →
4, WeakObjectProperty → 4, pointers → 8, …) into `ComputeMapValueOffset` at both
`Ubel.cpp` map-read sites; struct/variable values (align 0) keep the size guess.
Fixes the live container view AND the CE export (both consume the DLL's
`mapValueOffset`). +5 dll self-tests.

**CE-export progress indicator (UI).** `ResolveDrilldownAsync` gained an `onWalk`
callback; the Copy CE Field / Copy CE XML commands show a live
`Resolving… N objects` (throttled) during the recursive resolve and a final
`Copied: N objects, M XML lines.` so big maps/struct-arrays report progress.

**Map value DropDownList instead of a baked-in name (UI).** `EmitMapProperty` no
longer writes the resolved value into the description (`Value: ms_stdag` → `Value`)
— the stored int is dynamic. For Name/Enum values it builds a CE `DropDownList`
(rawInt → resolved name, parsed from `ValueHex`) on the map group and links each
value leaf to it, so CE displays the LIVE name. Key leaves are likewise label-only
(`Key`), and enum key/value widths now follow the real byte size (fixes a 1-byte
enum key shown as a 4-byte `1661982464`).

Owner live-verified. **1429 C# + 457 dll + 31 utf8 green; AOT publish clean (45.5 MB).**

## 2026-06-13 — CE XML/Field export drilldown: enum width, collapse, container values (build 1085)

Cross-game CE export fixes + a recursive container drilldown, all in the CE XML
emitter (`CeXmlExportService`); the DLL is unchanged. Spec: [ce-export-drilldown-spec.md](ce-export-drilldown-spec.md).

**Bug fixes (Copy CE Field / Copy CE XML), reported on SEED `LifeSaveDataSlot`:**
- **Enum width** — `EnumProperty` was hardcoded to `4 Bytes`, so a 1-byte enum
  (e.g. `CurrentStoryMissionSeriesId`) was read as 4 bytes and pulled in the next
  field's bytes (CE showed 5376 instead of 0). Now follows the property's real
  byte size (`CeWidthForSize(Size)`), in both the scalar path and struct-array
  sub-fields; the DLL already reports per-sub-field size.
- **Collapse** — `<Options moHideChildren>` was emitted only on pointer/array
  *deref* nodes, so array element folders like `[1]` stayed expanded. Now every
  non-root group folder collapses when "Collapse Pointer Nodes" is on (root has an
  absolute address, so it stays open).
- **Struct-array elements** — non-scalar sub-fields are surfaced as collapsed
  placeholders instead of being silently dropped; **Copy CE Field on a struct
  array element re-walks it fully** (nested structs/maps expand like drilling in),
  not just the shallow `read_array_elements` preview.

**Container value drilldown (Phase A of the spec — the design contract
"UI-drillable ⇒ export-drillable up to Drill Depth"):** `EmitMapProperty` /
`EmitSetProperty` no longer bail to a placeholder when a key/value is non-scalar —
container element **values that are structs/objects** now expand. New unified
`ResolveDrilldownAsync` (replaces the separate struct+pointer resolves for CE XML)
recursively resolves structs (flatten, free) + pointers (cost 1) + **container
element values** (struct → `resolvedStructs`, object → `resolvedInstances`, cost 1),
keyed by the same StructDataAddr/PtrAddress the emit phase looks up; the emitters
delegate each value back through `EmitFields` so struct/object/nested-container
values reuse `EmitResolvedStruct`/`EmitDrilledPointer`/`Emit*Property` uniformly.
So `Map<Name, Struct>` (`MissionInfoList`), `Set<Struct>`, struct arrays, and
nested `struct → Map<…, Struct>` (`MsTuneData → MsTunes`) all expand. **Also fixed
a struct-flatten metadata gap** — `ResolveStructRecursiveAsync` wasn't copying
`MapValueStructAddr` / `SetElemStructAddr` / `ArrayStructClassAddr` / `MapValueOffset`,
which blocked expanding containers nested *inside* a struct.

**Depth semantics** (user requirement): Drill Depth is measured from the **current
view** downward — the GWorld→…→view breadcrumb path costs nothing (verified already
true; locked with a test). Container expansion + pointer deref each cost 1 level;
struct flatten is free (`MaxStructDepth` bound). Per user decision: shared single
Drill Depth slider, **no global walk cap** (bounded by depth + `ArrayLimit`).

Tests +6 (Map→Struct emit + resolver + depth-0 flat + updated non-scalar-key
contract). **1428 C# + 452 dll + 31 utf8 green.** AOT publish clean (45.5 MB).
CSX alignment (Phase B) + extra depth guards (Phase C) deferred. Owner live-verified.

## 2026-06-13 — Teleport "Recall last" + BugIt/BugItGo hotkeys + DLL BugIt slot (build 1073)

Teleport additions on top of the shipped Wirbel feature (PR #272).

**"Recall last" — system-managed pre-teleport undo.** The DLL now captures the
current pose into a dedicated system "last" slot (`Wirbel::s_lastMarker`) right
**before every jump** — RECALL, RECALL_FORCE, the BugItGo explicit-pose path,
*and* cursor teleport (user chose "all jumps incl. cursor"). If a teleport lands
the pawn somewhere bad, `Wirbel::RecallLast` jumps back. It's a **one-way
restore** (user chose this over a toggle): RecallLast deliberately does NOT
re-save before jumping, so the slot stays pinned to the pre-teleport pose and
repeated recalls always return to the same spot — and a failed recall never
loses the original target. The slot is system-only: there is no SAVE path for
it, the map check is skipped (the pose is always from moments ago on the current
map), and the capture is best-effort (an unreadable pose leaves the prior last
intact). New `SaveLastImpl()` (lock-free, callers already hold `s_opMutex`).

Surfaced end-to-end: mailbox ops `TP_OP_RECALL_LAST=7` / `TP_OP_GET_LAST=8`
(Mimic); pipe `teleport_recall_last` + the "last" slot piggy-backed onto
`teleport_get_markers` as a **sentinel entry with `slot == -1`** (one round trip,
no interface churn); C ABI exports `UE5_TeleportRecallLast` / `UE5_TeleportGetLast`
(36 → 38). UI: a read-only "Last Position (auto-saved)" panel section with a
Recall-last button (`CanRecallLast` gating) and live summary; CE Lua AA record
(`Action.RecallLast`, op 7) added to the .CT batch (8 → 9 rows) + AOBMaker
specs; Lua hotkey bundle gains `recallLast()` bound to **Ctrl+Alt+0 /
Ctrl+Alt+Num0**.

**BugIt / BugItGo hotkeys.** The in-app global-hotkey rows (key-capture +
persisted, reusing `TeleportHotkeyStore`) extended from 6 to **9**: added
`recall_last`, `bugit` (Copy as BugItGo) and `bugitgo` (Run BugItGo). The
`OnMarkerHotkeyPressed` dispatcher routes these non-slot ids before the
digit-suffix parse ("recall_last" also starts with "recall"). Hotkey section
renamed "Marker Hotkeys" → "Teleport Hotkeys".

**BugIt / BugItGo refinements (build 1073).** *(UI)* "Copy as BugItGo" now also
pastes the `BugItGo X Y Z` string into the Run field so BugItGo fires
immediately; Run BugItGo guards on an empty field with a clear message before
parsing. *(Lua AA — DLL-backed)* New **BugIt slot** in Wirbel
(`s_bugItMarker` + `BugItSave`/`BugItGo`): a single user-triggered pose the DLL
holds between hotkey presses (UE BugIt/BugItGo semantics) — BugIt stores the
current pose, BugItGo teleports to it (restores rotation, auto-saves "last"
first, **no-op when nothing stored**). Surfaced via mailbox ops
`TP_OP_BUGIT_SAVE=9` / `TP_OP_BUGIT_GO=10` only (CE Lua path — the UI keeps its
textbox flow, so no pipe/export for this slot). Lua hotkey bundle's old
`copyBugItGo()` is replaced by `bugIt()` (stores DLL-side **and** clipboards) +
`bugItGo()` bound to **Ctrl+Shift+0 / Ctrl+Shift+Num0**; per-action AA records +
.CT batch gain BugIt/BugItGo (9 → 11 rows).

Tests: +9 net (`TeleportViewModel` recall-last + sentinel routing + 9-row
hotkeys + BugIt field-fill + empty-field guard; `TeleportScriptGenerator` op-7/9/10
+ 11-row batch; `TeleportLuaBundleGenerator` op-7/9/10 + Ctrl+Alt+0 / Ctrl+Shift+0
bindings + updated 10/20 counts). **1422 C# + 452 dll + 31 utf8 green.** AOT
publish clean (45.5 MB, no trim warnings). Owner live-verify of the in-game undo
+ BugIt/BugItGo paths still pending.

## 2026-06-13 — UE5.7+ FUObjectItem object-ptr offset auto-detect + Stark hot-hook fast-path + enum width (build 1064)

Vendor/engine-update review (Dumper-7 `92e2669..8883094`, RE-UE4SS `b872ad11..c08dbbc3`,
UnrealEngine 5.8/dev-5.8/ue5-main). Net actionable findings, with fixes:

**UE5.7+ reordered FUObjectItem — within-item object-ptr offset now auto-detected (Aura).**
Verified against real EpicGames/UnrealEngine source: from **5.7.0-release** the item leads
with `int64 FlagsAndRefCount` at +0x00, pushing `UObjectBase* Object` to **+0x08** (unpacked;
packed `UE_ENABLE_FUOBJECT_ITEM_PACKING` splits it further). UE ≤5.6 keeps `Object` at +0x00.
`Aura` previously hardcoded the object pointer at item+0x00 (`GetByIndex`/`ProbeStride`/
`GetSerialNumber`), so a stock 5.7/5.8 game would read `FlagsAndRefCount` as a pointer →
`LooksLikeHeapPtr` fail → stride-detect fail → empty dump. Fix mirrors Dumper-7's
`FUObjectItemInitialOffset`: new `s_itemObjOffset`, detected by a **two-pass** `DetectItemSize`
(classic +0x00 FIRST so every existing game keeps its exact prior path/result; UE5.7+ +0x08
only when the classic pass is unconvincing). Applied in `GetByIndex`, `ProbeStride`,
`GetSerialNumber` (serial offset recomputed relative to it), and the `GetItem`→`GetByIndex`
consumer in Frieren. **Note:** TQ2 (PE-stamped 5.7) keeps Object@+0x00 — confirmed via its
walk log (valid `World`/`Level` classes) — so it's a forked/early-CL 5.7; we passed it by
coincidence, not by handling 5.7. ⚠ Still needs live-confirm on a real stock-5.7+ game.

**Stark ProcessEvent hook — lock-free fast path.** ProcessEvent is the hottest engine
function; the hook took `s_queueMutex` on *every* call. Added an atomic `s_queueDepth`
mirror so the hook skips the mutex entirely unless an invoke is actually pending. (RE-UE4SS's
stability PR lesson — keep the hot hook cheap; our hook was already exception-free on the hot
path.)

**SDK export enum width.** `GenerateEnumDefinition` no longer hardcodes `: uint8_t` — new
`InferEnumUnderlyingType` picks the narrowest int that fits all entry values (mirrors Dumper-7's
"uint64 enum truncated to uint8" fix). NOTE: that generator is currently **not wired into the
live export path** (dead-ish), so this has no shipped-behavior change yet — it just makes the
function correct for when/if it's connected. +7 tests → **1414 C#**.

Already-aligned (no change): Dumper-7's UE5.8 `FChunkedFixedUObjectArray` reorder — our
`"UE5.8"` Aura preset `{0x00,0x0C,0x08,0x14,0x10}` already matches ue5-main source exactly.
OffsetFinder `FStructBaseChain` restriction — N/A (we have no such finder).

## 2026-06-12 — Teleport: user-set marker hotkeys, ARPG walk-back fix, UI polish (build 1038)

Live-test round 2 (TQ2 / UE5 + SEED Battle Destiny / UE4.27).

**ARPG walk-back.** TQ2 recall logged `K2_SetActorLocation invoked OK … rc=0`
but the pawn didn't visibly move — the click-to-move controller walked it back.
`Wirbel::StopMovement` now also invokes **`AController::StopMovement()`** (aborts
the active move order / path following) before the existing
`StopMovementImmediately` velocity reset, with logging on each. SEED (3rd-person)
was already fine; this targets click-to-move ARPGs.

**Marker hotkeys → user-set key capture (replaces the CE Lua bundle).** Per
user feedback the generated `createHotkey` Lua bundle didn't fire in their CE,
so the "Add hotkeys to CE" button + bundle path are removed from the panel.
Instead the UI binds **global** Save/Recall hotkeys itself: a new "Marker
Hotkeys" section with 6 rows (Save 1-3, Recall 1-3), each a **press-to-set**
capture — hold Ctrl/Alt/Shift then a key (e.g. Ctrl+F7), or a single key (F7);
Esc cancels. Bindings persist to `%LOCALAPPDATA%\UE5CEDumper\teleport-hotkeys.txt`
(plain text, AOT-safe) and re-register on launch. New
`IGlobalHotkeyService.RegisterSpecific`, `HotkeyKeyMap` (Avalonia Key → Win32
VK/mods), `TeleportHotkeyStore`. Cursor hotkey stays auto-detect. "Add action
records to CE" (AOBMaker) + "Save .CT" remain for CE-side hotkeys.

**UI polish.** Auto-refresh no longer flickers the buttons (the 0.5s poll uses a
quiet pose read that doesn't toggle `IsBusy`); it also re-pulls markers every ~2s
so CE-Lua/hotkey-triggered saves show. CE-export buttons moved to a `WrapPanel`
(no longer overflow the border). A "last fired @ HH:MM:SS" chip confirms a global
hotkey actually triggered. Channel field widened earlier.

Tests +18 → **1405 C#** (capture flow, key-map, store round-trip, hotkey smoke).
⚠ recall/cursor/marker-hotkeys LIVE-VERIFY PENDING with these fixes.

-----

## 2026-06-12 — Teleport fixes: super-chain lookup, AOBMaker injection, cursor hotkey (build 1035)

Live-test follow-up to the teleport feature below (both TQ2 / UE5 and SEED
Battle Destiny / UE4.27 reproduced the same bug).

**(1) Core bug — recall did nothing.** Logs showed `no K2_TeleportTo /
K2_SetActorLocation on pawn class — raw-write fallback` on both games. Root
cause: `Wirbel::FindFunc` used `Ubel::WalkFunctions(classAddr)`, which only
enumerates a class's OWN function chain — but `K2_SetActorLocation` /
`K2_TeleportTo` are declared on **AActor**, several levels above the game's
concrete pawn subclass. So every teleport fell to the tier-2 raw
`RelativeLocation` write, which CharacterMovement overwrites each tick → no
visible move. (Debug Camera worked because `ToggleDebugCamera` is on
`UCheatManager` itself.) **Fix:** `FindFunc` now walks the class → Super →
Super… chain (`DynOff::USTRUCT_SUPER`), so inherited engine UFunctions resolve.
Same fix unblocks `SetControlRotation` (AController), `StopMovementImmediately`
(UMovementComponent), and the cursor functions (APlayerController). Added
invoke-success / param-pack-fail logging.

**(2) CE export now injects via AOBMaker (was clipboard).** "Add hotkeys to CE"
ships a tickable `Teleport hotkeys` AA-script record straight into the open CE
table via `CreateAAScript` (autoActivate — hotkeys bind immediately; untick to
remove), mirroring the Debug Camera "Copy CE Script" path; clipboard is the
fallback. New "Add action records to CE" pushes the 7 momentary records the
same way. **Save .CT** demoted to a backup. New
`TeleportLuaBundleGenerator.GenerateAaRecord` wraps the bundle as
`[ENABLE]/[DISABLE]`.

**(3) Global cursor hotkey.** Cursor teleport is unusable from a UI button
(switching to the UI moves the cursor out of the game). New
`WindowsGlobalHotkeyService` (`IGlobalHotkeyService`) registers an OS-global
hotkey via `RegisterHotKey` on a dedicated message-loop thread, auto-picking
the first free combo from Ctrl+F8→F5 then Alt+F8→F5 (RegisterHotKey failing on
a taken combo IS the "is it free?" probe). A panel checkbox toggles it and
shows the chosen combo; the user keeps the game focused and presses it.
AOT-safe blittable DllImports.

**(4) Channel field** widened (was clipped to "(").

Build + 1385 C# / 452 dll / 31 utf8 tests green. ⚠ recall/cursor still
LIVE-VERIFY PENDING with the super-chain fix in place.

-----

## 2026-06-12 — Teleport (BugIt-style): marker save/recall + cursor teleport (build 1027)

Universal teleport feature, DLL-side following the Debug Camera pattern
(docs/teleport-spec.md is the full contract). Two modes: **generic** — save
the pawn pose into one of 3 marker slots and recall later; **pointer** —
teleport to the world position under the mouse cursor (2.5D / 45° games like
Titan Quest-likes, with a screen-center fallback for cursorless HD-2D titles).

**DLL.** New **`Wirbel`** module (`Wirbel.cpp/.h`). Resolution chain GWorld →
OwningGameInstance → LocalPlayers[0] → PlayerController → Pawn → RootComponent
→ RelativeLocation + ControlRotation — all engine base-class property names,
resolved live by the **new shared `Ubel::FindField` / `Ubel::FindFieldOffset`**
(extracted from the Debug Camera `DbgCam_FieldOffset`, which now wraps it).
LWC float/double detected from the `RelativeLocation` property size, applied to
all FVector/FRotator packing. Recall invokes `K2_SetActorLocation(bTeleport=true)`
(exact), cursor invokes `K2_TeleportTo` (spot-adjust), rotation via
`SetControlRotation`, velocity reset via `StopMovementImmediately` — all through
Stark's game-thread queue; raw property write is the tier-2 fallback (reported
back as `tier`). Cursor trace: `GetMousePosition` → `GetHitResultUnderCursorByChannel`,
else deproject + `KismetSystemLibrary::LineTraceSingle` (game-thread only — reads
the physics scene). Markers store coordinates + map name only (never pointers);
recall refuses on map mismatch unless forced. DebugCameraController-active case
hops through `OriginalControllerRef`. **6 exports** `UE5_Teleport*` (30→36),
**6 pipe cmds** `teleport_*` (~42→48), **mailbox `CMD_TELEPORT=8`**
(op in instanceAddr, slot in ufuncAddr, pose block in paramsData).

**UI.** New **Teleport tab** (appended last so indices don't shift): live pose
display + Refresh/Auto, 3 marker rows (Save/Recall/Force/Clear), cursor teleport
with Z-offset + channel + center-fallback, **BugItGo interop** (Copy-as-BugItGo +
paste-and-run a `BugItGo X Y Z` / `?BugLoc=…?BugRot=…` string), and CE export.
**Why BugIt "returns nothing":** it's void with no out-params — its outputs are
side effects inside the game (log [stripped in Shipping] + screenshot + the
game-process clipboard), so the dumper reads the pose itself instead.

**CE generators.** `TeleportLuaBundleGenerator` (primary "paste into CE" path) —
one self-contained Lua with `createHotkey` bindings + busy-check +
re-registration guard, talking to the mailbox directly. `TeleportScriptGenerator`
+ `CheatTableBuilder` for a 7-row auto-unticking .CT. **3 hotkey schemes**
(Numpad default / Top-row / Both) — CE distinguishes top-row VKs (0x31) from
numpad VKs (0x61); Numpad default because ARPGs bind Ctrl+1-3 to skills, Top-row
recall uses Alt+1-3 to avoid the skill bar. `BugItGoParser` handles the three
string formats. **+40 C# tests** (1384 total).

⚠ **LIVE-VERIFY PENDING** (10-item smoke checklist in teleport-spec.md §12):
float vs double games, menu/no-pawn errors, map mismatch + force, cursor vs
center, hotkey spam busy-skip, Debug Camera interaction, BugItGo round-trip.

-----

## 2026-06-09 — Value Search: per-object batch read (default ON) to speed up First Scan (build 974)

Follow-up to the parallel toggle. The First Scan is a **reflection-driven pointer walk**
(GObjects → class → field index → read each leaf at `obj+offset`), so its cost is
scattered cache-miss latency + per-field SEH-read overhead, NOT arithmetic (SIMD wouldn't
help — data is non-contiguous). One lever that fits the model: read each object's
fixed-width leaf fields in **one body read** instead of one SEH read per field — fewer
`__try` frames + better locality.

**DLL.** Per class, `buildClassIndex` precomputes the **body span** over `container==None`
fixed-width leaves: `[min(offset), max(offset+16)]` (a leaf reads ≤16B; TOptional also its
flag byte). Per object, a gate decides if batching pays off — `bodyFieldCount >=
kMinBatchFields(4)` AND `span <= kMaxBatchSpan(64KB)` AND `span <= fields *
kBatchBytesPerField(512)` (a density cap so a couple of fields spread across a big object
don't trigger a giant over-read) AND not a string scan. If so, ONE `ReadBytesSafe` fills a
**reused per-thread buffer** (`objBodyBuf`); a new `readBody(off, dst, size)` lambda serves
the vector / multi-numeric / single-numeric / optional-flag reads from it, **falling back
to a direct SEH read** when the buffer is null (gate failed or the read faulted — e.g. the
span straddled an unmapped page, which `ReadBytesSafe` zeroes-then-returns-false on) or
doesn't cover the range. Strings (`Ubel::ReadF*At` chase a separate char buffer), TArray
data, and TSet/TMap sparse data live in other heap allocations → always read directly. The
candidate's `addr` stays the real `obj+offset` (only the *read* is redirected). Buffer cost
= (worker threads) × (≤64KB), reused per object — independent of object count, so **no
meaningful memory growth** (dwarfed by the lean candidate pools).

**Toggle.** `ScanForValue(…, bool batchRead = true)`; Fern parses `batch_read` (default
true); pipe attaches it **only when false** (wire byte-identical otherwise). UI:
`ValueSearchViewModel.BatchRead` (default true) → "Batch read" checkbox next to "Parallel
scan" + tooltip. `IDumpService`/`DumpService` gain the param (before `pageSize`).

**Tests.** +`BeginValueScanAsync_AttachesBatchReadFalseWhenDisabled` (wire omit/false) +
`ViewModel_BatchRead_DefaultsTrue_AndPassesThrough` (VM default + pass-through via
`FakeDumpService.LastBatchRead`); both stub signatures updated. **1303 → 1305 C#**; full
`build.ps1` green (31 utf8 + 452 dll_helpers + 1305 C#); DLL recompiled. **Live-verified
2026-06-09 (user):** batch-on vs batch-off produce **identical results, no regression /
crash** (correctness confirmed). Speedup **inconclusive** — the test game's object set was
too small to measure a clear gain; the win should surface on big-object games (FF7R-class,
~400K objects). Constants (4 / 64KB / 512) remain tunable once there's a big-game profile.

## 2026-06-09 — Value Search: "Parallel scan" toggle (default ON) for anti-tamper-sensitive games (build 973)

User report: Value Search First Scan is a bit slow, but its parallel GObjects walk
(many worker threads reading process memory at once) can trip some games'
anti-tamper / anti-cheat. Added a **default-ON "Parallel scan" toggle** so the user
can trade speed for stealth when needed — unchecked forces a single-threaded scan.

**DLL.** `ParallelGObjectsScan` (the shared template behind value scan / find-refs /
xref / containers) gained an optional `int maxThreads = 0` cap (0 = use
`ScanThreadCount`'s pick; >0 = clamp). `ScanForValue` gained `bool parallel = true`
and passes `parallel ? 0 : 1` — so off means **one worker, run inline on the calling
thread, zero std::threads spawned** (the cancel-watcher still runs but only reads an
atomic, never game memory). Scoped to First Scan only: `RefineCandidates` re-reads
just the surviving candidate addresses serially, so it was never the concurrent-read
concern. Fern's `begin_value_scan` parses `parallel` (default true) and threads it in.

**Wire.** New optional `parallel` field on `begin_value_scan`, attached **only when
false** (DLL default is true) so existing call sites stay byte-identical. Documented
in [pipe-protocol.md](pipe-protocol.md).

**UI.** `ValueSearchViewModel.ParallelScan` (default true) → a "Parallel scan" checkbox
next to "Game classes only" with an anti-tamper tooltip → `BeginValueScanAsync(…,
parallel, …)`. `IDumpService` / `DumpService` gained the `parallel` param (inserted
before `pageSize`; the one positional VM call updated).

**Tests.** +`BeginValueScanAsync_AttachesParallelFalseWhenDisabled` (wire: omitted by
default, `false` when off) + `ViewModel_ParallelScan_DefaultsTrue_AndPassesThrough` (VM
default + pass-through via a new `FakeDumpService.LastParallel`) + an omit assertion on
the existing build-request test; both stub signatures updated. **1301 → 1303 C#**; full
`build.ps1` green (31 utf8 + 452 dll_helpers + 1303 C#); DLL recompiled.

## 2026-06-08 — Class Pivot: close the stale-load clobber race on the cache-hit path (build 972)

Verification pass over the experimental Class Pivot tab's snapshot/class selection
flow (the area that took several rounds to stabilise) surfaced one real latent race.
`ClassPivotViewModel.LoadClassesAsync` / `LoadFieldsAsync` use a monotonic guard
(`_classLoadId` / `_fieldLoadId`) so a slower in-flight scan started by a prior
snapshot/class bails instead of clobbering the picker. **But the `++_loadId` bump only
happened on the cache-MISS path** — the cache-hit and empty early-return paths returned
without advancing the counter. So this sequence clobbered:

1. snapshot/class **B** is already cached; select **A** (not cached) → cache miss →
   `id=N`, heavy `Task.Run` scan in flight.
2. quickly switch back to **B** (cache hit) → B's list applied instantly, counter
   **still `N`**.
3. A's scan completes → guard `id(N) != _loadId(N)` is **false** → A's list overwrites
   B's → the picker shows the wrong snapshot/class's classes/fields.

Both chains had the hole. Fix: move `int id = ++_classLoadId;` / `++_fieldLoadId;` to
**method entry**, so cache-hit and early-return also supersede any in-flight stale load
(it bails on the id mismatch). 3-line change, no behavioural change to the happy path;
matches the existing guard design. The pre-existing `_loadCts` cancel stays on the
cache-miss path (it's a perf optimisation for the rapid-miss case; the id guard is the
correctness mechanism).

Existing tests only covered miss-vs-miss (`RapidClassSwitch`) and a no-in-flight cache
hit (`ClassList_IsCachedPerSnapshot`); the **miss-in-flight vs cache-hit** cross was
uncovered. Added `CacheHit_DoesNotLetStaleInflightLoadClobberLatest` (gated store: load+
cache B, start a gated A miss, re-select B from cache, then let A finish — asserts the
fields stay B's). **Verified it fails without the fix** (`Expected "BetaField", Actual
"AlphaField"`) and passes with it. Tests **1300 → 1301 C#**; full `build.ps1` green
(452 dll_helpers + 1301 C#). Pure C# VM change — no DLL/pipe/AOT surface touched.

## 2026-06-07 — DLL-build indicator moved next to version + propagation fix (build ~968)

User reported the stale-DLL alert "never shows" next to `v1.0.0.966`. Investigation: the dist
DLL and UI were both `1.0.0.966`, so for a current deploy **no badge is correct** — the badge
only fires on mismatch, and there was no positive "matched" signal, so "no badge" was
ambiguous with "warning broken". Three fixes:
- **Legibility:** moved the global stale-DLL badge from the left (next to connection status)
  to **right of the version label** where users look for build info, and added a subtle green
  **`DLL <n>`** confirmation when the deployed DLL build matches the UI (`MainWindowViewModel.
  ShowDllBuildOk` / `DllBuildOkText`). Amber mismatch badge + green match indicator now sit
  together by the version.
- **Propagation gap (real bug):** `PointerPanelViewModel.NotifyComputedProperties()` raised
  `BuildVersionMismatch/Unknown` but NOT `ShowGlobalBuildWarning`/`GlobalBuildWarningText` —
  the only props the top-bar mirror listens for. So an `Update()` that didn't change
  `DllBuildNumber`'s value (reconnect/refresh to the same DLL) left the badge stale. Now
  re-raised in `NotifyComputedProperties`. +1 test locking it.
- Confirmed `build_number` is reported on BOTH the init response and every `get_pointers`
  snapshot (`Fern.cpp` 545/825), so the value isn't lost on refresh (the stale DumpService
  comment claiming "init only" is wrong; the code already falls back to `ptrs`).

## 2026-06-07 — Copy CE Field flat direct-push to CE (build ~966)

Follow-up to the +CE work below. Evaluated the deferred "direct-push Copy CE XML / Copy CE
Field into CE" idea: confirmed CE XML and the AOBMaker bulk-node schema are **near-isomorphic**
(same tree of desc/addr/offsets/type/hex/group — breadcrumb levels already emit relative
`+offset` + `offsets=[0]`), but the current `CeXmlExportService` has **no intermediate tree
model** — ~16 `Emit*` methods build XML inline with 6 threaded `[ThreadStatic]` states, so
"swap the output" means either a golden-test-gated refactor or a parallel walk, plus a new
bulk Begin/Chunk/End client. User scoped it down to **flat Copy CE Field only**.

Shipped: a **+CE Fields** toolbar button next to Copy CE Field (visible on multi-selection,
enabled when AOBMaker is up). It's the multi-select batch form of the per-row +CE button —
loops `CreateMemoryRecord` over the selection (same `_selectedFieldsSnapshot`→`SelectedField`
source as `ExportCeFieldXmlAsync`), one flat top-level record per field via
`MapFieldToCeRecordType`, skips addressless rows, early-bails if the pipe drops mid-batch, and
reports `added/failed/skipped`. **No bulk-tree client, no Emit-layer refactor** — Copy CE XML /
Copy CE Field stay clipboard-only for the hierarchical layout. Building blocks already unit-
tested (type mapping + CreateMemoryRecord serialization); the loop is thin control flow, so no
new VM-level test (matches the existing LiveWalker test approach — only static helpers tested).

## 2026-06-07 — Live Walker one-click "Add to CE" + top-toolbar AOBMaker chip (builds ~960-964)

Two "stop copy-pasting addresses into CE by hand" conveniences that lean on the AOBMaker
CE plugin's existing capabilities — **zero AOBMaker code changes**; all work is on the
UE5DumpUI client.

**Per-row +CE (one-click typed memory record).** Live Walker rows already had Addr / Name /
HEX. Added a **+CE** button on the field-address column and on the pointer column that pushes
a single typed CE memory record straight into the address list via the plugin's
`CreateMemoryRecord` pipe command (`addresslist.createMemoryRecord()`), so the user can jump
straight to CE's *Find out what accesses this address* instead of copy-address → build-record
by hand. The record's type/signed/hex is derived from the field via a new
`CeXmlExportService.MapFieldToCeRecordType` that **reuses the same UE→CE mapping that drives
Copy CE XML / Copy CE Field** (single source of truth); the pointer button uses
`PointerRecordType` (8 Bytes / ShowAsHex). Batch adds intentionally stay on the existing
multi-select **Copy CE Field** (clipboard) — Copy CE XML / Copy CE Field are unchanged.
- Bridge: `IAobMakerBridge.CreateMemoryRecordAsync(description, address, valueType, isSigned,
  showAsHex)` + `AobMakerMessage` gains `valueType` (nullable `int?` so a Byte record's `0`
  still serializes) / `isSigned` / `showAsHex`. `showAsHex` requires an **AOBMaker plugin
  built on/after 2026-06-07**; older plugins still create the record, just in decimal.

**Always-visible AOBMaker status chip (top toolbar).** Mirrored the System-tab AOBMaker
indicator into the main toolbar: colored dot + Connected/Offline + a **⟳** manual refresh
(`MainWindowViewModel.IsAobMakerAvailable` + `RefreshAobMakerCommand`, mirrored from the
LiveWalker/Pointers per-tab probes like the stale-DLL badge). Visible from every tab so the
user can confirm CE connectivity before using HEX/+CE actions; the System-tab indicator stays.

Tests: +CreateMemoryRecord wire round-trip (valueType-always-emitted, false-flags-omitted)
and +MapFieldToCeRecordType mapping coverage (1299 C# green). Docs:
[aobmaker-integration.md](aobmaker-integration.md) updated (message type 6 + version note).

## 2026-06-06 — Global stale-DLL badge + Live Walker tooltip-flicker fix (builds 956-958)

Two small UX fixes from a "manual proxy-DLL deploy" discussion.

**Global stale-DLL badge (build 958).** The DLL already reports `build_number` over
the pipe and the UI compares it to its own (`BuildVersionMismatch`), but the
match/mismatch badge lived only in the **Diagnostics** section of the Pointers tab —
easy to miss after hand-deploying an old proxy DLL into a game folder and forgetting,
then scanning with mismatched offsets. Surfaced it in the **always-visible top bar**:
`PointerPanelViewModel` gained `ShowGlobalBuildWarning` + `GlobalBuildWarningText`
("⚠ DLL build 920 ≠ UI 958 — stale, redeploy"); `MainWindowViewModel` mirrors it as
`ShowBuildMismatchBadge` (gated on `IsConnected`, re-raised from `Pointers`'
PropertyChanged + on connect/disconnect) into an amber badge next to the connection
status. Visible from every tab. +3 VM tests.

**Live Walker function-row tooltip flicker (build 956).** The Functions grid sits at
the window bottom, so the default `Pointer` tooltip placement on the INV/PIPE/AA(Baked)
buttons flipped up onto the cursor → hover ends → tooltip dismisses → re-enters →
repeat (a blinking hint on the bottom row). Same root cause + fix the project already
documented in `ProxyDeployPanel`: `ToolTip.Placement="Top"` + `VerticalOffset="-4"` so
the tip sits above the button, away from the cursor.

## 2026-06-06 — Value Search: raise maxResults cap to 1M (V2, build 954)

With V3-C's server-side window in place the cap is no longer bounded by the pipe / UI
walls — only DLL memory (cheap after V3-A's lean Candidate) and sort latency. Raised
the UI ceiling **500k → 1,000,000** (Increment 10k). The **default stays 50k** — a broad
scan shouldn't collect a million by default; the truncation note prompts the user to
raise it. The DLL has no hard clamp (it already honored whatever `max_results` the UI
sends), so this is purely the UI ceiling + a perf check.

Added a scaling benchmark to `dll_helpers` (1M synthetic candidates): a full
`BuildOrderedView` sort-by-value runs **~640 ms** and a keyword filter **~715 ms** —
both sub-second at the absolute ceiling, and proportionally tiny for typical (10k–100k)
scans. The benchmark runs on every filter/sort change (debounced 250 ms in the UI), so
while here the filter was made **allocation-free**: a case-insensitive substring test
(`ContainsCI`) that doesn't lowercase-copy each column, plus skipping the
`FieldDisplayName` copy for direct fields (877 → 715 ms at 1M). Generous `<5 s` asserts
catch an O(n²) regression. The value-column format (ostringstream) is the remaining
filter cost but is left as-is — it's the single source shared with the wire encoder, so
changing it would risk display drift. Tests **447 → 452 dll**. **Live-verify pending:**
set Max near 1M on a broadly-matching value, confirm the session pages / filters / sorts
responsively. (V2 is now closed; the only deeper follow-up — incremental/top-k sort —
isn't needed at this ceiling.)

## 2026-06-06 — Value Search: deferred enrichment + server-side window (V3-C, build 949)

Re-architected Value Search result handling so the DLL session is the **single
owner** of the candidate set and the UI is a **windowed view** — the precondition
for a large `maxResults` cap (V2) without a giant pipe payload or a DataGrid holding
N rows. Design discussion settled a real tension: client-side filter/sort require the
UI to hold the full set, which defeats windowing. The resolution (user-aligned):
**filter + sort move server-side**. They run in the DLL over the DLL's *own* pools
(addresses + interned descriptor/instance strings) — **no game-memory reads, so the
game thread is never touched** — and only a window is serialized out. A
loaded-window-only filter would be untrustworthy ("no match" couldn't tell "not in
the data" from "not loaded"), so this is also the *correct* shape, not just the
scalable one.

**DLL.** New pure, unit-tested helpers in `ValueScan` (the test target links it):
`FormatCandidateValue` (now the single source of truth for the value display string,
shared by the wire encoder — `Fern::CandidateToJson` lost its private formatters),
`DecodeNumericToDouble`, `SortKey` + `TryParseSortKey`, and `BuildOrderedView`
(case-insensitive substring filter across the displayed columns + stable sort by key
→ candidate-index vector). `SessionManager::QueryWith` caches the ordered view on the
session keyed by `(filter, sortKey, sortDesc)` (invalidated on refine) so plain paging
doesn't re-sort. New `query_candidates` pipe command (`session_id`, `offset`, `limit`,
`filter`, `sort_key`, `sort_desc`) → `{total, filtered_total, offset, count,
candidates}` slices the window out of the cached order. `begin_value_scan` /
`refine_value_scan` now return `total` (full count) + only the FIRST PAGE (`page_size`,
scan order) instead of ALL candidates.

**C#.** `IDumpService` / `DumpService` gain `QueryCandidatesAsync` + a `pageSize`
arg on begin/refine; new `ValueScanWindowResult`. `ValueSearchViewModel` now holds the
CURRENT window, `Total` / `FilteredTotal` / `WindowStatus` / `HasMore`, a server-side
keyword filter (debounced 250ms → reload window 0), a **sort picker** (combo +
`Desc` toggle → `query_candidates`; replaces the client-side column-header sort, which
could only reorder the loaded window — Avalonia's `DataGridColumnEventArgs` can't
cancel the built-in sort, so the headers are now non-sortable and a picker drives the
server), and a **Load More** button (appends the next page). When the view is default
(no filter, scan order) the inline first page from begin/refine is shown with no extra
round-trip.

**Folds in most of V2's UI/pipe work** — raising the cap is now just a bigger number.
Tests **412 → 447 dll** (+23 ordered-view: filter/sort/format/parse), **1268 → 1276
C#** (+8: query wire shape + omit-defaults, inline-page, sort/desc/LoadMore/filter
routing, NewScan reset). AOT publish launch-verified. **Live-verified by user
2026-06-06** — a large First Scan shows total + first page + Load More; the
server-side keyword filter and sort picker narrow/reorder the WHOLE set; a refine
re-pages correctly.

## 2026-06-06 — Value Search: TOptional<T> scan (V1c, build 942)

Closes the last "deferred container" gap in Value Search after V1a (TSet/TMap):
a value held in a `TOptional<T>` UPROPERTY is now findable. `FOptionalProperty`
stores the wrapped value **inline at field+0** (the same Inner-at-`FARRAYPROP_INNER`
shape as `FArrayProperty`, already resolved by `WalkClassEx` into `innerType` /
`innerStructType`), with a trailing `bIsSet` byte for non-intrusive optionals.

So unlike the sparse TSet/TMap walk, a TOptional value is just a **leaf read at
field+0 with an unset gate**. `expandFields` gained an `OptionalProperty` branch
(next to the TArray-inner branch) that emits a leaf `ScanField` whose `typeName` is
the inner type — so the per-instance loop reads/compares it identically to a direct
field. The only addition is `ScanField::optionalFlagOffset`: when the optional is
larger than its value (room for the bool), it's set to `sizeof(T)` and the
per-instance leaf path skips slots whose `bIsSet` byte is 0, so a scan for `0` /
stale bytes doesn't false-hit unset optionals. The flag offset is computed by a new
pure helper `ValueScan::OptionalFlagOffset(optionalSize, innerSize)` (returns
`innerSize` when `optionalSize > innerSize`, else −1 for intrusive/pointer optionals
or unknown sizes); inner size comes from reusing `Ubel::GetArrayInnerElemSize` (valid
because FOptionalProperty shares the Inner offset).

Covers numeric / string / vector inner types (the same DataTypes as direct leaves);
drilling into a `TOptional<FStruct>` for nested leaves is left as a further step.
**Refine needs no change** — `c.addr` is field+0, a stable address (better than the
sparse-slot containers), so prev-value refine works directly. **No wire / C# change.**
Tests **412 → 424 dll** (+12 `OptionalFlagOffset` layout cases: int8/16/32/64,
float/double, FVector, FString, intrusive, unknown-size, defensive). **Live-verify
pending:** scan a known value held in a `TOptional<int/float/FString>` UPROPERTY,
confirm the row appears under the optional's field name + a Next Scan prunes; check an
unset optional doesn't surface on a scan for 0.

## 2026-06-06 — Live Walker focus-on-field on Value Search cross-nav (build 939)

Fixes the "found a value in a `TMap`, opened it in Live Walker, but had no idea
where the data was" complaint. Previously "Open in Live Walker" from a Value Search
row navigated to the owning instance and dumped its whole field list with nothing
selected. Now it focuses the exact field that produced the candidate.

Threaded the candidate's `FieldOffset` + display name through the existing cross-nav
seam: `ValueSearchViewModel.NavigateToInstance` grew from `Action<string>` to
`Action<string,int,string>` (addr, offset, name); `MainWindowViewModel` calls a new
`LiveWalkerViewModel.NavigateToInstanceFieldAsync(addr, offset, name)`. The owning
property row is matched by **byte offset, not name** — field names aren't unique
(inherited members, map `.Key`/`.Value`), and the DLL already sends `field_offset =
desc.fieldOffset` (the owning property's offset from the UObject base, matching
`LiveFieldValue.Offset`). For container hits the display name's trailing `[N]`
(`Augments.Value[2]`) is parsed by `ParseElementIndexSuffix` and the existing
Find-Refs auto-drill machinery (`_pendingDrillElementIndex` +
`TryDrillIntoMatchedContainer`, factored out of the by-name path) drills into the
container and selects element `[N]`. Hits inside a nested struct (absolute offset,
not a top-level row) fall back to a plain navigation — same as Find Refs.

Reuses the proven post-walk `_pendingScroll*` → `ScrollToFieldRequested` →
`ScrollIntoView` path; only a parallel **by-offset** pending hint
(`_pendingScrollFieldOffset`) was added. **No DLL / wire change.** Tests
**1254 → 1268 C#** (+13 `ParseElementIndexSuffix` theory cases + 1 event-shape
assert). **Live-verified by user 2026-06-06** — a `TMap` value hit lands on the
container row and drills to the element; a direct numeric hit selects + scrolls to
the exact field. (This was the original "found a TMap value, no idea where it is"
report.)

