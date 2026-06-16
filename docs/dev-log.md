# Dev Log

Append-only milestone history, newest first. Each entry references a
build number from `build_number.txt` so commits can be cross-referenced.
**Reading tip:** grep `^## ` for the index, then read the top (newest-first).
Entries for **builds ≤696** (2026-05-09 → 2026-05-12) are archived in
[archive/dev-log-2026-05-pre-build-700.md](archive/dev-log-2026-05-pre-build-700.md).

> **Looking for current state?** See [roadmap.md](roadmap.md) for the
> capability matrix / per-game configuration / tested games, and
> [todo.md](todo.md) for the prioritized next-work list. This file
> records *what shipped* — the other two record *what works now* and
> *what's next*.

-----

## 2026-06-16 — Deeply-nested container values: recursive find_by_address + multi-level GWorld drill (build 1198)

The first live test of Locate in GWorld (build 1193) surfaced two gaps in the
todo: a value buried **>1 container level deep** couldn't be found by
`find_by_address` at all, and the deep-drill (which lands ON a value inside a
struct-array element) was only fed by the Instance Finder. After investigation
the user chose **recursive descent + multi-level drill, all through the Instance
Finder by-address path** (Value Search / SPC wiring intentionally folded in —
see "Why not VS/SPC" below).

**Repro (SEED BATTLE DESTINY REMASTERED).** `find_by_address 0x228F1251BE8`
returned **0 matches** while a sibling 1-level value (`SaveSlotList[1]+0x4D8`,
the inline `GP` field) was found instantly. The deep int lives at
`BP_LifeSaveData_C.SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]`
— six container hops, each nested container a **separate heap allocation**.

**Why the shallow scan can't see it.** `Aura::FindInContainers` bounds-checks
`addr` against each container buffer at a fixed offset within the object
(`GetClassContainers` flattens DIRECT-struct nesting). That finds values stored
INLINE in a buffer — a `TArray<int>` element, or a field of a struct stored
inline in a `TArray<FStruct>` (why `SaveSlotList[1]+0x4D8` works). But a
`TArray<int>` whose *header* is inline in a struct element while its *data*
lives elsewhere on the heap is a separate allocation — `addr` falls in no
top-level buffer.

**Recursive deep descent (DLL).** New `Aura::FindInContainersDeep` +
`MatchAddrInStructContainers` recurse into struct elements: at each level, if
`addr` is inside a buffer it's the terminal hit (a leaf, or an inline struct
field); otherwise, for struct-element containers (Array/Set element struct, Map
value/key struct) descend into each element and recurse, building the full hop
chain. Bounded by `maxDepth` (UI sends 5), a 256-element-per-container probe
cap, the existing 15s deadline, and **early-out on the first match** (one match
answers a by-address lookup). It runs **only as a fallback** when the shallow
scan finds nothing AND the caller opted in (`container_depth > 1`), so the
common fast path is untouched (zero regression). `ContainerCacheEntry` now
carries the element/value/key `UScriptStruct*` + map value offset (resolved at
cache-build via new `Ubel::GetContainerInnerStructAddr` + an extended
`GetMapPairLayout` that also returns key/value struct addrs). `GetMapPairLayout`
now uses the value's **real** alignment (`Scharf::RequiredAlignment`) so the
pair stride/value offset match `WalkInstance` exactly — the deep matcher indexes
map slots by the same sparse index the UI shows.

**Multi-level chain (model + pipe).** `ContainerMatch` gains a
`nestedChain` (`ContainerHop[]`); the outermost container stays in the existing
fields, each deeper hop is one container drilled into, and the deepest hop's
intra-offset locates the value. Serialized as `nested_chain` in the
`find_by_address` response; `find_by_address` reads a new `container_depth`
param. `ContainerMatch.DisplayPath` (C#) now spans the full chain
(`…SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[42]`).

**Multi-level GWorld drill (UI).** New `LiveWalkerViewModel.LocateContainerInGWorldAsync(ContainerMatch)`
reaches the owner via the BFS path, then `DrillContainerChainAsync` walks the
chain hop-by-hop: navigate any leading DIRECT-struct name segments (e.g.
`MsTuneData` in `MsTuneData.MsTunes`) → drill the container → select element
`[N]`; nested hops then drill INTO the struct element to continue; the deepest
hop scrolls to the value (a struct field at its intra-offset, or the leaf
element itself). Degrades gracefully — if a hop can't be matched in the live
view it stops and reports the remaining manual path. The Instance Finder
container row's 🌍 now passes the whole `ContainerMatch`
(`LocateContainerInGWorld` event → `Action<ContainerMatch>`); the build-1193
one-level `elementIntraOffset` branch of `LocateInGWorldAsync` was removed (the
1-level case is just a single-hop chain through the new path). Shared spine
builder `BuildBreadcrumbSpineFromPath` extracted.

**Why not VS/SPC (the folded-in todo item).** Investigation found neither
produces a "value inside a struct-array element" candidate today: Value Search's
`ScanForValue` never descends into `TArray<FStruct>` elements, and SPC filters
every array-element row out of its results (`WHERE array_field IS NULL`). So
there was nothing to wire the deep-drill to — and a VS redirect couldn't reach
the SEED value anyway (its first hop `SaveSlotList` is already a struct array).
The by-address path is where the workflow actually lives, so the effort went
there.

**Tests / build.** +7 C# (`DeepContainerChainTests`: chain `DisplayPath` /
`DeepestIntraOffset` / `IsDeeplyNested` + the flattened `BuildContainerDrillPath`
order) → **1512 C#**; **507 dll_helpers / 31 utf8** unchanged green. Full build
clean (DLL + 3 proxies), AOT publish clean (46 MB).

**LIVE-VERIFIED on SEED (build 1199).** `find_by_address 1B06D16B448` →
`BP_LifeSaveData_C.SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]`
(scanned 28116 objects in 50ms), and the container-row 🌍 reached the owner (2
hops) and drilled the full chain to land ON `Tunes[2]` = 20. Two follow-ups from
the test:
- **Live Walker `Addr` copy bug (fixed).** In a container-element view the per-row
  `Addr` button recomputed `CurrentAddress + field.Offset`, but `CurrentAddress` is
  the OWNING struct (the element lives in a separate heap buffer), so it copied the
  owner's field at that offset (e.g. `Tunes[2]`'s `Addr` gave the parent's
  `WeaponId` address). Now `CopyFieldAddressAsync` uses the already-resolved
  `field.FieldAddress` (same value the Address column + Hex/+CE/Edit buttons use),
  falling back to `CurrentAddress + Offset` only when it's absent.
- **"Locate in GWorld depth" moved to the Options flyout.** The depth NumericUpDown
  left the Live Walker toolbar for the top **Options** dropdown (renamed
  `Locate in GWorld depth`, slider 1–32), bound through the `LiveWalker` sub-VM
  (`LiveWalker.GWorldLocateDepth` / `.IsGWorldAvailable` for the dim-when-no-GWorld
  gate).
- **Deep-scan element cap is now configurable (build 1200).** The
  `kMaxElemProbe = 256` constant became a parameter threaded
  `find_by_address`(`container_elem_cap`) → `FindInContainersDeep(maxElemProbe)`, with
  a `Deep container scan cap` exponent-slider in the Options flyout (2^4–2^12,
  default 256) bound via `MainWindowViewModel.DeepScanElemCap(Exponent)` →
  `InstanceFinder.DeepScanElemCap`. Higher reaches values at higher element indices
  in the recursive descent at the cost of speed.

-----

## 2026-06-16 — Locate in GWorld: forward BFS path search (build 1181)

Pressing **Parent** in the Live Walker walks `UObject.OuterPrivate` (the naming
hierarchy) and almost always dead-ends at `/Engine/Transient` — never the
gameplay-meaningful GWorld spine. New feature answers the inverse question:
given a found target, **where is it under GWorld?**

**Forward BFS (the engine).** New pure, header-only shortest-path core
[`GraphPath.h`](../dll/src/GraphPath.h) (`BfsShortestObjectPath`) + a live
adjacency adapter `EnumerateOutgoingObjectPtrs` in [`Aura.cpp`](../dll/src/Aura.cpp)
that REUSES the per-class reference-metadata cache (`GetClassRefMeta`) powering
`FindReferencesToUObject` — the same battle-tested extractor for direct
Object/Class/Interface, Weak/Soft/Lazy, TArray/TMap/TSet-of-objects, and
StructProperty-nested pointers — but enqueues children instead of comparing
against a target. `Aura::FindObjectGraphPath(rootObj, targetObj, maxDepth=5)`
runs the BFS root-agnostically (the pipe handler resolves GWorld → UWorld and
passes it in, keeping Aura decoupled from the GWorld globals). BFS first-hit ==
shortest hops, so "first found == shortest" is free. visited-set dedup bounds it
to O(reachable), 3M-node cap + 20s deadline + `Cancel::Requested()`. The pure
core is unit-tested against a mock graph (shortest-among-two, cycles, depth
bound, root==target, unreachable, abort, visited-cap, reconstruction) — **+10
tests → dll_helpers 507 green**.

**Pipe.** New `find_path_from_gworld` command (`target` + optional `object_addr`
+ `max_depth`) returns the path steps + resolved target + diagnostics; for a
known owning object (Value Search / Instance Finder) it skips the FindByAddress
resolution scan. MulticastSparseDelegate edges are NOT followed (global-TMap walk
per node too expensive). Spec: [pipe-protocol.md](pipe-protocol.md#find_path_from_gworld).

**UI.** `LiveWalkerViewModel.LocateInGWorldAsync` clears + rebuilds the breadcrumb
spine from the path and lands on the target via the existing
`_pendingScrollFieldOffset` machinery. Two behaviours: a property **VALUE**
(Value Search per-row "🌍 GWorld") lands ON the owning object and scrolls to the
value field; an **OBJECT / class instance** (Instance Finder "🌍 Locate in
GWorld") stops at the parent that points to it and highlights the pointer field,
without drilling in. A **GWorld depth** NumericUpDown (default 5, on the Live
Walker tab) sets the search depth. All triggers gray out when GWorld is
unavailable (`EngineState.HasGWorld`, surfaced via each panel's `SetEngineState`).
Not-found surfaces an actionable status ("increase the depth"). 1505 C# green,
AOT publish clean (45.9MB).

**Trigger panels (build 1187).** Wired from all four "open in Live Walker"
sources: **Instance Finder** (both the by-class and by-address searches funnel
through `SelectedInstance` → object/parent mode), **Value Search** (value/reach),
**SPC Query** (per-row 🌍, value/reach — has the live object + changed-field
offset), and **Interesting Functions** (per-row 🌍 — a function isn't a world
object, so MainWindow resolves a live non-CDO instance of its class via
`FindInstancesAsync` first, then parent mode). **Property Search is intentionally
excluded** — its rows are class/property *definitions* (deduped to the defining
class, often abstract → no single live instance); its existing "Find Instances"
button already bridges to Instance Finder, which has the 🌍 button. Gray-out via
`EngineState.HasGWorld` (each panel's `SetEngineState`, or a direct
`IsGWorldAvailable` set for Interesting Functions which has no `SetEngineState`).
In-game live-verification pending.

**Container-match path (build 1188, after first live test).** First in-game test
(SEED BATTLE DESTINY REMASTERED, build 1187) surfaced an accessibility gap, not a
BFS bug: a by-ADDRESS lookup of a value *inside* a container element
(`BP_LifeSaveData_C.SaveSlotList[1].GP`) produces a **container match**, not a
direct instance → `HasFields=false` → the "🌍 Locate in GWorld" toolbar button
was hidden and `SelectedInstance` was null, so the only action was the container
row's pre-existing plain "Open". Logs confirmed `find_path_from_gworld` was never
called. Fix: the Instance Finder **container-match row** now has its own "🌍"
button (`LocateContainerOwnerInGWorld` → `LocateContainerInGWorld` event → reach
mode) that locates the OWNING object via the GWorld path, then auto-drills into
the matched element `[N]` (safe — `TryDrillIntoMatchedContainer` only drills
`IsContainerNavigable` fields), landing the user on the element ready to scroll to
the value.

**Land ON the nested value (build 1193).** First container-match test correctly
produced `GWorld → … → BP_LifeSaveData_C → SaveSlotList → [1]` (BFS verified!) but
stopped on the array element `[1]` — the actual value `GP` is a field *inside* the
struct element at `[1]+0x4D8`. (Not a depth issue — `GWorldLocateDepth` is the BFS
hop count to the owning object, unrelated; 5 vs 8 gave the same correct result.)
`LocateInGWorldAsync` now takes an `elementIntraOffset`: for a `StructProperty`
container element the Instance Finder passes the match's `IntraOffset`, and the
reach path does explicit awaited drills — walk owner → `NavigateToContainerAsync`
(array view) → `NavigateToFieldAsync` (the `[N]` struct element, which carries
`StructDataAddr`/`StructClassAddr`) → scroll to the field at the intra-offset — so
the breadcrumb spine ends `… → SaveSlotList → [1]` and the DataGrid lands ON `GP`.
The single-shot `_pendingScroll*` path can't chain two container levels, hence the
explicit sequence. Value Search / SPC reach paths still land on the owning field
for struct-array-inner hits (same extension, deferred — todo).

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

## 2026-06-06 — Value Search engine + app-wide DataGrid sorting + DLL cancellation (builds 926-937, PRs #237/#238)

Five shipments, all live-verified by the user. Tests **1254 C# + 412 dll + 31 utf8**.

### Value Search — lean Candidate (V3-A + V3-B), build 926, PR #237
Interning refactor of the value-scan result record. `ValueScan::Candidate` carried
six `std::string`s copied by value, almost all redundant: class / defining-class /
field name / type / mask / offset are functions of `(class, field)`, and the
instance name is shared per object. Interned the per-(class,field) metadata into a
session `FieldDescriptor` pool and the per-object metadata into an `InstanceRecord`
pool; the lean Candidate keeps only `addr` + value snapshot + `descriptorIdx` +
`instanceIdx` + `elementIndex` (~240 B → ~72 B, and **0 heap strings per numeric
candidate**). Sessions live in the injected DLL, so this is the precondition for any
later maxResults-cap increase. Worker threads intern into thread-local pools
(descriptorIdx cached on the `ScanField`); a custom ascending-tid merge offset-remaps
candidate indices (replaced `ConcatTruncate` for this caller). **Wire JSON shape
unchanged → C#/UI untouched.** Array element names rebuilt via
`ValueScan::FieldDisplayName(desc, elementIndex)`. **V3-B (instance-table dedupe) was
necessarily folded into V3-A** — a lean Candidate can't keep raw instance fields.

### Value Search — TSet / TMap key|value scan (V1a), build 927, PR #237
Closes the biggest "what can't Value Scan reach?" gap after non-UPROPERTY fields.
`ScanField`'s `bool isArray` → `enum ScanContainer { None, Array, Set, MapKey,
MapValue }` + `valueOffset`. `expandFields` emits Set/Map(key|value) ScanFields next
to the ArrayProperty branch (vector inner gated by `ContainerInnerAccepted`); a shared
`scanElement` lambda (factored out of the TArray loop) drives Array + a new sparse
branch that walks the FSet/FMap `TSparseArray` (allocated slots only via
`IsSparseIndexAllocated`, value at `slot + valueOffset`), reusing the Address Finder's
sparse geometry (`GetSetElementStride` / `GetMapPairLayout`). **Refine + Fern needed
ZERO changes** (operate on `c.addr` + descriptor pool; element addr incl. valueOffset
baked in at First-Scan). Rows render `Set[idx]` / `Map.Key[idx]` / `Map.Value[idx]`.
Element addresses are raw, so refine degrades on container reallocation exactly like
TArray. TOptional (V1c) still deferred. Live-verified: a `TMap<NameProperty,IntProperty>`
value (`PlayerData.AttributeAugmentLevels.Value[2]`=481) found via Int32 Exact scan.

### App-wide DataGrid sorting fix + Value Search keyword filter, builds 932-934, PR #237
Column sorting was dead in **every** DataGrid (text and template columns). **Root
cause: compiled bindings** (`AvaloniaUseCompiledBindingsByDefault=true`) — Avalonia
DataGrid does NOT auto-derive a column's sort path from a compiled binding, so without
an explicit `SortMemberPath` nothing sorts. **NOT an AOT/backend-removal regression**
(reproduced on the non-AOT build). Added `SortMemberPath` to every sortable column
across all panels (numeric backing for hex offset/size/score columns so order is
numeric), `CanUserSort="False"` on action columns. Exception kept: SPC `SnapshotPicks`
stays chronological. Plus a **Value Search keyword filter**: case-insensitive
substring across all columns, client-side over the cached set (`FilterText` →
`ApplyFilter` rebuilds the bound **typed** `ObservableCollection` — a non-generic
`DataGridCollectionView` breaks compiled column-binding type inference, AVLN2000).

### DLL-side cooperative cancellation for long operations, builds 936-937, PR #238
Long DLL ops now stop when the UI disconnects or the DLL shuts down, so the DLL no
longer spins after the UI closes and disabling the script / closing the game no longer
hangs while a scan finishes. The pipe is single-connection + synchronous (a scan blocks
the pipe thread and can't be told to stop on the same pipe mid-scan), so this is a
**cooperative-cancel layer**:
- New `Cancel.h`: `Cancel::Requested()` (relaxed atomic) = per-command disconnect flag
  | sticky shutdown flag.
- `Fern::Stop()`/`UE5_Shutdown` call `RequestShutdown()` **before** joining threads →
  in-flight scan bails, accept-thread join completes fast (**fixes "game won't close"**).
- Fern **monitor thread** `PeekNamedPipe`s the in-flight pipe every 200ms (only while
  `m_commandInFlight` — the handler is CPU-bound then, not touching the handle); a broken
  pipe → per-command cancel → orphaned scan bails, pipe frees for the reconnecting UI
  (**fixes the reconnect-within-window stall**).
- Coverage: a watcher in `ParallelGObjectsScan` flips the existing `deadlineHit` (covers
  value scan / find-refs / containers / xrefs / find-by-path with no per-body edits);
  serial loops poll `Cancel::Requested()` every 4096 iters (`ListClasses`,
  `EnumerateAllFunctions`, `SearchByName`, `FindInstancesByClass`, `SearchProperties[Batch]`,
  `WalkClassesBatch`, `CaptureSnapshotChunk`, `Aura::ForEach`, `list_enums`). **No hard
  timeouts** — Full SDK dump etc. must run to completion.
- UI: Value Search gains a per-scan `CancellationTokenSource` + Cancel button (shown
  while scanning). Cancel abandons the UI wait; the DLL self-terminates at its deadline.

Live-verified: value-scan cancel, close-UI-mid-scan stops the DLL, game closes promptly.

-----

## 2026-06-05 — Experimental UX batch 3: pivot index, capture ETA, Delete All, icon (build 923)

Third live-test feedback pass.

- **Persisted pivot class-index (fixes the ~10s+ Class Pivot first-open scan).**
  The picker ran `COUNT(DISTINCT gobjects_index) GROUP BY class_fqn` over ~1.7M
  rows on every snapshot selection. Now precomputed ONCE per snapshot into a small
  `class_counts` table (additive schema, no version bump — existing snapshots kept;
  `pivot_index_built` marker distinguishes "0 array classes" from "not built").
  Built eagerly at `FinalizeSnapshotAsync` (new captures open instantly) with a lazy
  fallback for old snapshots; the picker reads the tiny table. Persists across
  restarts. Cleaned up on delete + quota-eviction. +3 tests.
- **Capture progress: elapsed + ETA + %.** Status now shows
  `Capturing… X/Y (NN%) — objects/fields · 1m23s elapsed, ~45s left` (ETA from the
  elapsed/fraction projection, suppressed under 2%). Finalize shows "building pivot
  index…".
- **Delete All (truncate) button** on the Snapshot tab — `DeleteAllSnapshotsAsync`
  truncates every table for the active game + VACUUM, off the UI thread.
- **Taskbar/window icon was transparent on the AOT exe.** Avalonia's `.ico` decode
  under AOT/Skia is flaky; switched `Window.Icon` to a PNG (extracted from the
  existing `Mainicon.ico`). The exe file icon (`ApplicationIcon`, .ico) is unchanged
  and was already fine. *(The app.manifest already existed + is DPI-aware, so that
  wasn't the cause.)*

Tests **1254 C# + 393 dll + 31 utf8**, all green. AOT publish clean +
launch-verified (window opens, no crash). **LIVE-VERIFY PENDING (user):** Pivot
first-open is now fast; selecting any snapshot responsive; capture shows ETA; Delete
All works; **taskbar icon renders** (PNG fix).

-----

## 2026-06-05 — AOT: Windows-only Avalonia backend (drop X11/macOS/FreeDesktop) (build 918)

The Native-AOT publish emitted a wall of `ILC: ... will always throw because:
Failed to load type 'Tmds.DBus.Protocol.Connection'` warnings from
`Avalonia.X11` / `Avalonia.FreeDesktop` — code paths that can never run in a
Windows-only tool (UE5CEDumper injects into Windows games). Removed them at the
source instead of suppressing:

- **`Avalonia.Desktop` → `Avalonia.Win32` + `Avalonia.Skia`** (the Desktop
  meta-package bundled the X11 / macOS-Native / FreeDesktop backends). Dropped
  the now-orphaned `Tmds.DBus.Protocol` and the Linux/WebAssembly native assets
  (`{HarfBuzzSharp,SkiaSharp}.NativeAssets.{Linux,WebAssembly}`).
- **`Program.cs`: `UsePlatformDetect()` → `.UseWin32().UseSkia()`.**
  `UsePlatformDetect` itself lives in `Avalonia.Desktop`, so it had to go; the
  explicit Win32+Skia wiring is exactly what PlatformDetect resolved to on
  Windows anyway.
- TrimmerRootAssembly: dropped `Avalonia.Desktop`.

Result: AOT publish is now **warning-free** (was a dozen X11 ILC lines). Single-
file exe ~46.8 MB. **LIVE-VERIFY PENDING (user):** launch the published
`dist/UE5DumpUI.exe` and confirm the window opens + renders — backend init is a
runtime concern the build can't prove. (Reference: CrimsonAtomic keeps
Avalonia.Desktop + NoWarn; we went the leaner remove-the-backend route the user
asked for.)

-----

## 2026-06-05 — Experimental UX hardening batch 2 (build 916)

Second live-test feedback pass on the Snapshot / SPC / Pivot tabs.

- **Capture lock-down (real bug).** `OnIsCapturingChanged` only raised `CanCapture`,
  so `CanEditSettings` never re-evaluated → Scope/GameOnly/Quota/Label stayed editable
  during a capture. The capture loop reads `GameOnly` **per chunk**, so toggling it
  mid-capture would corrupt the snapshot. Now raises `CanEditSettings` + `CanRunDiff`,
  and `CanRunDiff` includes `!IsCapturing` (Run Diff disabled during capture).
- **Delete Selected hang.** `DeleteSnapshotAsync` (DELETE over ~1.7M rows,
  `ExecuteNonQueryAsync` runs synchronously) ran on the UI thread → freeze. Wrapped in
  `Task.Run`, added an `IsDeleting` busy flag (disables the Delete button + status),
  refreshes usage after.
- **Class Pivot slow + unresponsive (the 80% CPU / "can't select snapshot" report).**
  `LoadClassesAsync` (GROUP BY `COUNT(DISTINCT gobjects_index)` over ~1.7M rows) +
  `LoadFieldsAsync` were uncancellable, so rapidly changing the snapshot stacked
  several heavy scans on the thread pool. Added a shared `_loadCts` (cancel-prior-on-new)
  threaded into the store list methods (+ early `ThrowIfCancellationRequested`), a
  "Loading classes…" status, and `CancelPendingWork` now also cancels loads. Stacking
  eliminated. **Plus per-snapshot caching (the real fix for "re-scans on every
  dropdown"):** a snapshot is write-once / immutable, so its class + field lists are
  cached in-VM keyed by `(snapshotId, arrayMode)` / `(snapshotId, class)` — computed
  ONCE, instant on every re-select for the session, no dirty-flag needed. Cache entries
  for deleted snapshots are pruned in `RefreshAsync`; the denylist filter is applied on
  top of the cache so hiding a class never triggers a re-scan. +1 test (CountingStore
  proves the class list is scanned once across re-selects).
- **Pivot intro hint** for transient inventory: their object path is identical
  (`//Engine/Transient/Item`), so Identity mode merges them into one group (visible
  collision ⟨N: …⟩) — use a Field key (ItemID) instead. Same transient-path cause as
  the SPC issue, but Pivot degrades gracefully (single-snapshot, no value-pairing).
- **SPC "materials don't show up" (root-caused).** Transient inventory objects all
  normalise to `//Engine/Transient/Item`, so the **Strict** join (norm_path + offset)
  collapsed 4 distinct items into one candidate; with no `ORDER BY` on the row stream
  the cross-snapshot value pairing was arbitrary, so the directional predicate failed.
  **In-session** join (gobjects_index) tracks each object exactly. Fix: auto-select
  In-session when all ticked snapshots share a `GameSessionId`, Strict otherwise
  (cross-session) — overridable; a manual combo change sticks. Cross-session SPC still
  works via Strict/Loose (the user's other ask).
- **Single-click checkboxes everywhere.** `DataGridCheckBoxColumn` needs select-then-click
  (2 clicks). Converted all four (SPC Use + noise Pick, Snapshot noise Pick, Pivot
  Project) to `DataGridTemplateColumn` + centered `CheckBox` (TwoWay) → one click toggles.
- **Snapshot pickers show the timestamp.** A custom label hid *when* a snapshot was taken
  in the one-line diff/pivot ComboBoxes. New `SnapshotMeta.PickerDisplay` = "Label ·
  yyyy-MM-dd HH:mm:ss (local)" used there (the saved-snapshots grid keeps its separate
  Captured column).
- **Diff Old/New auto-swap.** If Old is picked newer than New, the diff swaps them by
  snapshot Id (= capture order) so Increased/Decreased stay correct, noting it in the
  status.
- **Capture layout compaction.** Scope / GameOnly / Quota / Label / Capture / Cancel now
  share one WrapPanel row (was two), denser now that capture is an Expander.

Tests: **1250 C# + 393 dll + 31 utf8 = 1674**, all green; AOT publish clean. **LIVE-VERIFY
PENDING (user):** (1) settings locked during capture; (2) Delete no longer hangs; (3) Pivot
snapshot/class selection responsive (no CPU peg); (4) SPC same-session auto-picks In-session
and the materials now appear; (5) one-click checkboxes; (6) diff/pivot combos show timestamps;
(7) reversed Old/New auto-swaps.

-----

## 2026-06-05 — N1 follow-ups: per-tab denylists, cancellation, grayout, collapsible layout (build 910)

Live-test feedback pass on the N1 noise picker. Six changes:

**Per-tab denylist isolation (was one shared list).** The user wanted each
experimental tab to keep its OWN exclude list — hiding a class in SPC must not
affect Snapshot Diff or Class Pivot. `ClassDenylistSettings` now holds three
independent lists (`Diff` / `Spc` / `Pivot`) in one per-game JSON file; the store
API is `GetClassDenylist(DenylistScope)` / `SetClassDenylist(DenylistScope, set)`
with read-modify-write so writing one scope preserves the other two. SPC VM uses
`Spc`, Snapshot/Diff uses `Diff`, Pivot uses `Pivot`.

**Class Pivot right-click "Hide this class".** Pivot has no result-derived Top-N
picker (it analyses one class), so its denylist is populated by a ComboBox
ContextMenu "Hide selected class from picker" → adds to the Pivot-scope list and
drops the class from the picker. A hidden-class chips bar (with per-chip remove +
Clear all) appears below the results when non-empty (`HasHiddenClasses`).

**Cancellation + the tab-switch hang fix.** The reported symptom — switching to
Class Pivot mid-SPC-query froze the UI (50-80% CPU) and the process lingered after
close, blocking re-launch — was an uncancellable multi-million-row in-memory query
competing with the new tab's load. Each experimental VM now owns a
`CancellationTokenSource`, cancels its prior op on a new one, and exposes
`CancelPendingWork()`. `MainTabs_SelectionChanged` cancels every experimental tab's
heavy op when navigating away from it; `MainWindow.OnClosed` cancels all three so
the process exits promptly (releasing the single-instance mutex). Crucially —
`Microsoft.Data.Sqlite`'s `ReadAsync(ct)` runs synchronously and **ignores the
token**, so explicit `ct.ThrowIfCancellationRequested()` was added inside every
heavy DB-read / in-memory loop (SPC anchor + per-pass + eval, diff A-load + B-stream,
pivot row fetch ×2) at a ~64k-row cadence, plus an early bail before opening the
connection. Capture (streaming, yields between chunks) is deliberately NOT cancelled
on tab-switch.

**Gray out inputs during operations.** Snapshot capture region was already gated
(`CanEditSettings`, build 882); added gating for the diff inputs during `IsDiffing`,
SPC query inputs + picker grid during `IsQuerying`, and the Pivot selection +
key-mode + field grid during `IsBusy`. Progress bars / status / result-action
buttons (Open / Copy) stay live.

**Reset on the noise pickers.** SPC + Diff Top-N pickers gain a "Reset ticks" button
(`ResetNoisePicksCommand`) that unticks all rows without touching the persisted
denylist (distinct from "Clear all", which empties it). Pivot's equivalent is its
"Clear all" hidden-classes button.

**Collapsible Snapshot layout + splitter.** The capture region and the compare
region are now `Expander`s (capture force-expanded while capturing via
`CaptureSectionOpen`). A `GridSplitter` between the saved-snapshot list (2★) and the
compare+diff block (5★) lets the user trade vertical space — the diff grid (which
showed very few rows) can now be enlarged by collapsing the two regions and dragging
the splitter.

Tests +9 (scoped denylist independence + per-scope persistence + already-cancelled-
token throws for Diff & SPC) → **1250 C# + 393 dll + 31 utf8 = 1674**, all green.
Native AOT publish clean. **LIVE-VERIFY PENDING (user):** (1) per-tab isolation —
hide a class in SPC, confirm it still shows in Diff/Pivot; (2) Pivot right-click
"Hide this class" → confirm it leaves the picker + a chip appears + restart persists;
(3) the tab-switch hang is gone (switch SPC→Pivot mid-query → "Query cancelled.",
responsive); (4) collapse capture + compare, drag the splitter → diff grid grows.
Note: the Pivot ContextMenu command binding inherits the ComboBox's VM DataContext
(no `$parent` traversal), but ContextMenu-in-popup bindings are an Avalonia AOT risk
worth confirming at runtime.

-----

## 2026-06-05 — Experimental N1: per-game class denylist + Top-N noise picker (build 908)

SPC over BPGC-heavy games was flooding the 50k cap with directional-but-
irrelevant hits — game-side widgets / anim BPs / tick components (`W_HUD_C`,
`WBP_Inventory_C`, `BP_CooldownComponent_C`, …). Static denylists don't travel
between games (each title's noise classes are named differently), and the
existing `Aura::IsEnginePackage` skip only covers `/Script/*` not game-side
BPGCs. N1 turns "what's noisy?" into a one-look UI question over the result
the user just paid to compute.

**Surface — Top-N picker on SPC + Diff result tabs.** Each run produces a
`TopContributors: List<ClassNoiseRow>` (max 50) ranked by hit count over the
*matched-rows* set (not raw capture), each row carrying up to 3 sample prop
names. A fold-out Expander under the result grid lets the user tick rows and
hit "Apply &amp; re-run" — the picks join the per-game denylist and the SPC/Diff
query re-runs immediately so the cleaned result is visible without leaving the
tab. Below the picker: chips showing the active denylist with one-click remove
(`RemoveFromDenylistCommand`) + a "Clear all" button.

**Persistence — sibling JSON next to the per-game DB**: `snapshots.&lt;pe_hash&gt;.
denylist.json`. Deviation from the original spec (which proposed extending
`experimental.json` with a per-pe_hash dict). Reasons: (a) the denylist
auto-follows the game already keyed by pe_hash; (b) it survives FIFO snapshot
eviction (eviction drops snapshots, not the user's noise picks); (c) no need
to plumb pe_hash through the gate service. Source-gen JSON (`ClassDenylistJsonContext`),
atomic temp-then-rename writes, swallow-and-log on failure — same pattern as
`ExperimentalGate`. Filenames sanitise pe_hash to ASCII alphanumerics
(same defence as the DB filename). Save is gated on an active game so the
default DB never accumulates game-specific picks.

**Filter application — at the anchor-load step (saves memory AND match cost).**
`SpcQuery` and `SnapshotDiffFilter` gain `ExcludedClasses: HashSet&lt;string&gt;?`.
In `SpcQueryAsync` denied classes are skipped on the anchor-load row stream
*and* on every subsequent snapshot pass, so they never enter the candidate
dict — cuts the in-memory hash-join's peak working set on noisy games. In
`DiffSnapshotsAsync` denied classes are filtered out of BOTH the A-load and
the B-stream (and `bTotal` excludes them too, so the Added/Removed churn
numbers reflect only the visible classes). The Top-N accumulator counts
post-filter, so the picker never re-suggests an already-denied class.

**Pivot — picker filtering only, no Top-N UI.** Class Pivot is per-class
(user picks ONE class to pivot), so a "Top contributor" computation from a
single class is meaningless. Instead `ClassPivotViewModel.LoadClassesAsync`
reads `_store.GetClassDenylist()` and skips denied entries before populating
the bound `_allClasses` list. Symmetric UX: the same denylist that hides
classes from SPC/Diff results also hides them from the Pivot picker.

Files:
- New `ui/UE5DumpUI/Models/ClassDenylistSettings.cs` (model + source-gen JSON ctx).
- `ui/UE5DumpUI/Models/SpcModels.cs`: `SpcQuery.ExcludedClasses`,
  `SpcResult.TopContributors`, `ClassNoiseRow`.
- `ui/UE5DumpUI/Models/SnapshotDiffModels.cs`: `SnapshotDiffFilter.ExcludedClasses`,
  `SnapshotDiffResult.TopContributors`.
- `ui/UE5DumpUI/Core/ISnapshotStore.cs`: `GetClassDenylist` / `SetClassDenylist`.
- `ui/UE5DumpUI/Services/SnapshotStore.cs`: denylist persistence (sibling JSON),
  filter at anchor/per-pass row reads (SPC + Diff), `NoiseAccumulator` helper that
  Top-N-ranks contributors with up to 3 sample props each.
- `ui/UE5DumpUI/ViewModels/SpcQueryViewModel.cs` +
  `ui/UE5DumpUI/ViewModels/SnapshotViewModel.cs`: denylist state,
  `RebuildNoiseRows`, `ApplyNoisePicksAsync` / `RemoveFromDenylistAsync` /
  `ClearDenylistAsync` commands, `NoiseRowVm` (shared).
- `ui/UE5DumpUI/ViewModels/ClassPivotViewModel.cs`: denylist filter on
  `LoadClassesAsync`.
- `ui/UE5DumpUI/Views/SpcPanel.axaml` + `SnapshotPanel.axaml`: Expander +
  picker DataGrid + chip ItemsControl (AOT-safe — no string-path Bindings).
- `ui/UE5DumpUI/Resources/Strings/en.axaml`: `str.Noise.*` keys.
- Tests +6 in `SnapshotStoreTests.cs`: `DiffSnapshots_ExcludedClasses_*`,
  `DiffSnapshots_TopContributors_*`, `SpcQuery_ExcludedClasses_*`,
  `ClassDenylist_PersistsAcrossStoreReload_PerGame`,
  `ClassDenylist_WithoutActiveGame_DoesNotPersist`,
  `ClassDenylist_FilenameSanitisedForPeHash`.

Tests: **1247 C# + 393 dll + 31 utf8 = 1671**, all green. Native AOT publish
clean (zero IL2026 / IL3050 — source-gen JSON for the new settings type
registered via `ClassDenylistJsonContext`). LIVE-VERIFY PENDING (user):
(1) capture two SPC-friendly snapshots on a noisy game; (2) tick the top 1-2
W_*/WBP_*/anim_BP rows; (3) Apply &amp; re-run → confirm gameplay rows now
dominate the result; (4) flip to the Pivot tab → confirm denied classes are
absent from the picker; (5) restart the app → denylist still loaded.

-----

## 2026-06-04 — Experimental Snapshot/SPC/Pivot: live-test hardening + in-memory engines (builds 879-884)

A long live-test + iteration pass on the gated Snapshot / SPC Query / Class Pivot tabs.

**879 — crash + hang fixes.** Live test surfaced a crash + a hang (root-caused from the
UI view log). (a) `ObservableCollection.Clear()`+repopulate while bound to a
ComboBox/DataGrid selection trips Avalonia's selection model
(`ArgumentOutOfRangeException` / "Cannot change ObservableCollection during a
CollectionChanged event") → new `UiCollection.Reset()` detaches the selection before
mutating, applied to every selection-bound rebuild. (b) `Microsoft.Data.Sqlite`'s
`*Async` runs synchronously on the caller, so the Pivot/SPC/Diff queries froze the UI
→ all heavy store calls wrapped in `Task.Run`. Also: SPC oldest-first ordering, baseline
predicate forced to "Any" + disabled, 2-snapshot warning; experimental checkbox split
from the author credit (Opacity 0.1); Snapshot "Open DB folder" button
(`IPlatformService.RevealInExplorerAsync`). Merged to main (PR #230).

**880 — diff + SPC result filters.** AutoCompleteBox pickers (Class/Field/Object, distinct
from the result set), a global filter (any column), and value ranges (diff Old/New;
SPC value-sequence first/last) with Apply/Reset.

**881 → 882 — DB normalize, then REVERTED.** Tried normalising identity into an `objects`
table + `vfields` view (halved size) — but it made Run Diff take >1 min on ~1.8M rows
(the self-joins fell off their single-table composite covering indexes). Reverted to the
denormalised schema; kept the version-gated drop-on-old mechanism. Added indeterminate
progress bars + "Running…" status on all three tabs, and locked capture controls during
a capture/diff.

**883 — in-memory hash-join diff (the real fix, ported from `discrete`).** The Unity sister
project never diffs in SQL — it loads both snapshots into dictionaries and diffs in two
O(n) passes. Adopted: `DiffSnapshotsAsync` streams A into a
`Dictionary<(class,gobjects_index,prop),(hex,num)>`, streams B and hash-looks-up A
(changed rows + Added/Removed churn in one pass). O(n), independent of index/schema shape.

**884 — in-memory SPC + drop heavy indexes (~½ DB) + SPC absolute predicates.** Replaced the
N-way SQL self-join (`SpcQueryBuilder`, deleted) with an in-memory intersection + new pure
`SpcEngine` predicate evaluator. With diff & SPC in-memory and Pivot filtering by
`(snapshot_id, class_fqn)`, dropped the three heavy composite indexes
(`ix_strict/loose/insession`, ~450 MB) for a single lean `ix_fields(snapshot_id, class_fqn)`
— roughly halving the DB at zero capture cost (schema v4). Added per-snapshot **absolute
value predicates** (Exact / Between / ≥ / ≤) applied before the 50k cap, fixing the case
where directional-but-irrelevant UI noise (e.g. `SizeBox.WidthOverride` 1920→0) crowded
real gameplay values out of the cap.

Tests 1358 → 1241 net for the experimental suite churn (the totals shifted as
`SpcQueryBuilderTests` were replaced by `SpcEngineTests`); full suite green throughout
(1241 C# / 393 dll / 31 utf8 at build 884). LIVE-VERIFIED by user: diff fast + correct;
SPC results + value-filter pending final confirm. The `discrete` techniques still
unmined: gzip per-class blob storage (further size), lazy field-index + eviction.

-----

## 2026-06-04 — Class Pivot C5 (right-click handoff) + C6 (array-element pivot) (build 877)

Two more Phase C closures in one session.

**C6 — array-element pivot ("Snapshot Array" source).** Phase A1b already captures
struct-array elements with their inner-key (e.g. `Cargo[].ItemID`) + inner numeric
props (`Quantity`) into the `fields` table's array columns. C6 surfaces them: a third
`Source` mode lets the user pick a snapshot → array-class → struct-array field, then
groups the elements **by inner-key value** (reorder- and owner-immune) projecting the
inner props. The neat part: **no new engine.** `SnapshotStore.PivotArrayAsync` fetches
the element rows and maps each `(owner GObjects index, element index)` pair to a
*synthetic* `PivotInputRow.ObjectIndex`, with `NormPath = inner_key_value`. Run through
`PivotEngine.Build` in **Identity mode**, that groups by inner-key value with the exact
same collision rendering as the scalar pivot. New store methods:
`ListPivotArrayClasses/Fields/Props` (array_field IS NOT NULL filters) + `PivotArrayAsync`.

**C5 — right-click "Pivot this property…" handoff.** A context-menu item on
PropertySearch, InterestingProperties, and LiveWalker raises a new
`NavigateToPivot(className, propName)` event. MainWindowViewModel switches to the Class
Pivot tab and calls `ClassPivotViewModel.PivotForAsync`, which forces scalar Snapshot
mode, selects the class in the newest snapshot (clearing any class filter), and ticks
the handed-off property as a value field (unless it became the auto-suggested key).
Graceful no-op with a status hint when no snapshot/class match exists.

**Gating (the "invisible when experimental off" requirement).** C6 lives entirely
inside the already-gated Class Pivot tab. C5's menu items bind `IsVisible` to a new
per-VM `PivotEnabled` flag that MainWindowViewModel sets to
`ExperimentalEnabled && Pivot != null` (refreshed on `gate.Changed`) — so the handoff
disappears the moment experimental features are toggled off, and never appears when no
snapshot store is wired.

Tests +12 (6 `ArrayPivotStoreTests` + 3 `ClassPivotViewModelTests` [array-source +
2× `PivotForAsync`] + 4 `PivotHandoffCommandTests`). All green: 1244 C# / 393 dll /
31 utf8. AOT publish clean. **LIVE-VERIFY PENDING (user):** (1) C6 — capture a snapshot
of a game with a struct-array inventory, pivot it by inner key; (2) C5 — right-click a
property in PropertySearch/InterestingProps/LiveWalker → confirm it lands in Class Pivot
pre-selected. Remaining Phase C: C2 (find-by-value).

-----

## 2026-06-04 — Class Pivot C4: DataTable-native zero-config pivot (build 873)

Closed Phase C4 of the experimental Snapshot/SPC/Pivot work. A DataTable is already
a `RowName → struct` map, so it pivots with **no key discovery**: each row is its own
group keyed by RowName, and the row struct's fields are the projected columns.

**Design — reuses the existing seam end-to-end.** No new pipe command and no new tab:
the DLL's `walk_datatable_rows` (+ `DumpService.WalkDataTableRowsAsync`) already
existed but had no UI consumer. C4 adds a **`Source` toggle (Snapshot / DataTable)**
to the existing Class Pivot tab. DataTable mode swaps the snapshot/class pickers for a
live DataTable picker (`FindInstancesAsync("DataTable")`, subclass-tolerant, filtered
to class names containing "DataTable"), walks the selected table, and feeds the rows
through the new pure `DataTablePivotEngine` into the *same* results grid + CE handoff
(Copy Address / Open in Live Walker) the snapshot pivot already uses.

- **`DataTablePivotEngine`** (pure, AOT-safe, unit-tested): `Build(dt, valueFields)`
  → one `PivotResultRow` per row (Count 1, never a collision, `ObjAddr` = row struct
  address for CE); `Fields(dt)` aggregates struct fields across rows (type / distinct /
  instance counts) to drive the value-field picker.
- **`ClassPivotViewModel`** gains an optional `IDumpService`, the `Source`/DataTable
  state, `RefreshDataTablesAsync` + `LoadDataTableFieldsAsync`, a DataTable branch in
  `RunPivotAsync`, and `ShowKeyField`/`IsSnapshotSource`/`IsDataTableSource` so the
  Group-By/key controls hide in DataTable mode (replaced by a "Key = RowName" hint).
- **Gating:** C4 lives entirely inside the Class Pivot tab, which is already
  `IsVisible="{Binding ExperimentalEnabled}"` — so the whole feature is invisible when
  the experimental flag is off. No new ungated surface was added.

Tests +8 (6 `DataTablePivotEngineTests` + 2 `ClassPivotViewModelTests` DataTable-source
cases); made `StubDumpService.FindInstancesAsync`/`WalkDataTableRowsAsync` `virtual` so
the VM test can subclass. All green: 1232 C# / 393 dll / 31 utf8. AOT publish clean.
**LIVE-VERIFY PENDING (user):** pick a real game's DataTable and confirm rows + CE
handoff. Remaining Phase C: C2 (find-by-value), C5 (right-click handoff), array pivot.

-----

## 2026-06-03 — Native UFunction property xref via x64 disassembly (Path 2, builds 862-872)

The complement to Path 1: **answer "which fields does this *native* (C++) function
read/write?"** for functions that Path 1 can't see — `FUNC_Native` functions have
empty Kismet bytecode. Path 2 disassembles the native function's machine code with
**Zydis** and maps each `[this+offset]` access back to a UPROPERTY on the owning
class. Forward direction only (function → properties); the reverse (property →
native funcs) would mean disassembling every native function per query and is
deferred. Heuristic by nature (vs Path 1's exact byte match), so confidence is
surfaced honestly in the UI.

**Design — reuses the Path 1 seam end-to-end.** No new pipe command, no new UI
panel: `walk_function_props` already returned empty for native functions and
`FunctionPropsDialog` already showed *"native, no bytecode"*. Path 2 plugs into
exactly that gap — when `UStruct::Script` is empty the DLL falls back to
disassembly and returns the same `FunctionPropRef` rows, tagged `method="disasm"`
with a per-row `confidence`.

- **Zydis vendored as `vendor/zydis` submodule** (v4.1.1 + nested `zycore`),
  matching the `vendor/minhook` convention. Built **static, decoder-only**
  (`ZYDIS_FEATURE_ENCODER/FORMATTER OFF`) → ~250KB. Linked into `UE5Dumper`, both
  proxy DLLs, and `dll_helpers_test`. RE-UE4SS's copy was only a FetchContent stub
  (downloads at build time — rejected as against the offline-vendoring rule).
- **`Denken` module** (`dll/src/Denken.{h,cpp}`, naming map #25). Pure Zydis-only
  decoder core: `Analyze(startAddr, MemReader)` where all process-memory access
  goes through a caller-supplied reader callback — so the TU links into the leaf
  test with no Macht/Win32 dependency. `this` is seeded in **RCX** (MS x64 ABI; the
  exec-thunk signature is `Func(UObject* Context, FFrame&, void*)`), tracked across
  register copies; `[reg+disp]` accesses are recorded high-confidence when the base
  is a proven this-alias, low otherwise (stack/RIP bases excluded). Follows up to
  a few direct `CALL`/tail-`JMP` handoffs (the thunk → real C++ impl) when RCX
  still holds this; instruction budget caps runaway. **Zydis v4 gotcha:** the
  displacement-present flag is `op.mem.disp.has_displacement`, not `.disp.size`.
- **`UFunction::Func` offset detection** (`Aura::EnsureUFunctionFuncOffset`,
  `DynOff::UFUNCTION_FUNC`). Detected **lazily** on first Path-2 use (GObjects
  guaranteed ready, zero startup cost): every UFunction's `Func` is an in-module
  code pointer (native → execXxx thunk; script → ProcessInternal), and no other
  pointer member in the `0x80..0x158` window is `MEM_IMAGE` executable, so the
  first offset where all sampled UFunctions hold a `Macht::LooksLikeCodePointer`
  IS Func. `0` = not found → Path 2 silently disabled (method `"none"`), Path 1
  unaffected. New `Macht::LooksLikeCodePointer` validates via `VirtualQuery`
  (`MEM_IMAGE` + `PAGE_EXECUTE*`).
- **Aura wiring** (`WalkFunctionPropertyRefs`). On empty Script → read the exec
  ptr, `Denken::Analyze`, then map accesses to properties via `Ubel::WalkClass`
  (already includes the full inherited super chain with absolute `Offset_Internal`,
  so an offset matches at most one property). Unmapped accesses counted, not shown.
  Rows sorted high-confidence → writers → frequency → name.
- **Pipe + UI.** `walk_function_props` response gains `method` + `unmapped` and
  each row gains `offset` + `confidence`. C# `FunctionPropRef(sResult)` mirrors them
  (defaulting `method` to `"bytecode"` for old DLLs); `FunctionPropsDialog` is now
  method-aware — a **Confidence** column appears for disasm results (low-conf rows
  amber), the status line flags `[native disasm — heuristic, N unmapped]`, and the
  `"none"` case keeps the old "no analysis available" message. Caveat reworded to
  cover both methods.

**Tests.** +5 `Denken_*` cases in `dll_helpers_test` (hand-assembled x64: this
write / non-this read / alias propagation / call-handoff followed / non-this call
NOT followed / terminators+guards) → 393 dll-helper assertions. +4 C#
`WalkFunctionPropsAsync` cases (bytecode / disasm with offset+confidence+unmapped /
none / backward-compat default) → 1224 C#. Suite: **1224 C# + 393 dll + 31 utf8 =
1648**, all green. Native AOT publish clean (no IL2026/IL3050 from the new
DataGrid column — uses the existing `FuncDataTemplate<T>` pattern). **Live verify
pending (user):** Geri (UE4.27) / ES2 (UE5.5) — Interesting Functions → a native
getter/setter → Props → expect disasm rows naming the touched field(s).

-----

## 2026-06-03 — Property ↔ Function bytecode cross-reference (Path 1, builds 838-861)

A new RE capability line: **answer "which methods use this field, read or write?"
and the inverse "which fields does this function touch?"** by statically scanning
Kismet (Blueprint) bytecode. Six commits on `dev`. No full disassembler — every
step anchors on a known **address** (zero false positives) plus a few stable
`EExprToken` opcode values, so version risk stays low. Coverage is BP/script
functions only; native (`FUNC_Native`) functions have empty bytecode and are
invisible (complementary to CE access-breakpoints, which cover native but drown on
shared/inlined code). Headless verifier: `scripts/xref_probe.ps1` (a pipe CLIENT —
the opposite of `test_pipe.ps1`'s mock server).

- **`574031e` — DLL Path 1 (`find_property_xrefs`).** Given a target `FProperty*`,
  byte-scan every UFunction's `UStruct::Script` for the (unaligned) pointer; the
  variable-access opcodes embed the live fixed-up `FProperty*` directly.
  `DynOff::USTRUCT_SCRIPT` is **derived as `USTRUCT_PROPSSIZE + 0x08`** (MinAlignment
  always sits between them) — verified against every RE-UE4SS `MemberVariableLayout`
  template (UE 4.18-5.7) AND every shifted custom-game layout (Atomic Heart / Silent
  Hill F / Outer Worlds), so the +8 invariant is universal and inherits PROPSSIZE's
  calibration. Reuses `ParallelGObjectsScan` + `ConcatTruncate`; 30s deadline; relies
  on Ubel's mutex-guarded caches (build 792).
- **`0548ed3` — UI wiring.** `PropertyXrefDialog` (code-behind, AOT-safe
  `FuncDataTemplate` columns); Class Struct field grid → right-click "Find functions
  using this field". Self-contained dialog, no new tab (experimental-tab layout
  untouched).
- **`a84eca9` — extend + UX consistency.** DLL `search_properties{,_batch}` now emit
  `field_addr`; Property Search + Interesting Properties get the same xref (row
  "Find Funcs" button + context menu); Class Struct gains a client-side field
  Filter box + row button. Shared `PropertyXrefDialog.ShowForFieldAsync` owner
  resolver. VMs take `IPlatformService` (optional where tests construct them).
- **`4d349d6` — v2a ubergraph → event attribution.** A BP event's stub calls
  `ExecuteUbergraph(<int entryOffset>)` via `EX_(Local)FinalFunction(0x46/0x1C)` +
  `EX_IntConst(0x1D)`. `BuildUbergraphEntryTable` anchors on the ubergraph
  function's address, reads each stub's entry offset → `(entryOffset, eventName)`;
  a serial post-pass attributes each reference offset to the event whose entry
  offset is the largest ≤ it. So `ExecuteUbergraph_*` hits resolve to the actual
  event (e.g. `ItemEffects_BeginPlay`). Best-effort: shared sub-graphs reached from
  multiple events can mis-attribute (needs v2b CFG walk).
- **`9286350` — read/write distinction.** `IsWriteContext` detects assignment
  destinations via the `EX_Let*` LHS shapes (`EX_LetBool 0x14`/`MulticastDelegate
  0x43`/`Delegate 0x44`/`Obj 0x5F`/`WeakObjPtr 0x60` = `[LetOp][varOp][ptr]`;
  `EX_Let 0x0F` = `[0x0F][propptr 8B][varOp][ptr]` with a `LooksLikeHeapPtr` check;
  `EX_LetValueOnPersistentFrame 0x64`). Conservative (high precision); wrapped LHS
  (`Other.Field` / `Struct.Member` / `Arr[i] = x`) reads as a read.
- **`248f631` — reverse edge (`walk_function_props`).** Given ONE UFunction, parse
  its bytecode and list every `FProperty` it references with read/write tally +
  **scope** (instance / local / default / sparse / struct / frame).
  `Ubel::ResolvePropertyNameType` validates each candidate (type name must contain
  "Property"). Sorted instance-first so BP compiler temporaries
  (`CallFunc_*_ReturnValue`, scope=local) don't drown the class fields.
  `FunctionPropsDialog` has a **"Class fields only"** filter (default on);
  Interesting Functions panel gains a "Props" row button.

Live-verified throughout on Everspace 2 (UE5): `BP_Ship_Player_C.
OkkarCatalystCargoUnit2HPBuff` → 7 refs, 1 write, event `ItemEffects_BeginPlay`;
`ExecuteUbergraph_BP_Ship_Player` → 6231 props, instance fields surfaced first.
Every commit: DLL + UI Release build OK, **tests 1220 C# + 370 dll + 31 utf8 green**,
Native AOT publish clean. New pipe commands: `find_property_xrefs`,
`walk_function_props` (+ `field_addr` on `search_properties{,_batch}`,
`ustruct_script` in `get_offsets`).

**Deferred (see todo):** v2b (CFG-precise event attribution + wrapped-LHS r/w),
Path 2 (Zydis disasm of native UFunctions).

## 2026-06-02 — Experimental: Class Pivot (Phase C, build 830)

Third tranche of the experimental Snapshot / SPC / Pivot feature. **Phase C
(Class Pivot)** ships the value-keyed grouping core + UI. Pure C# over the
existing SQLite corpus, **zero DLL change**. Design of record:
[experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md)
§"Phase C".

- **C1 — PivotEngine (pure, AOT-safe).** `PivotEngine.Build` folds the captured
  (instance, field) rows into per-instance records, groups them by **intrinsic
  identity** (normalised path — spawn-counter siblings `BP_Enemy_C_0/_1/…`
  collapse into one group so the value cells show the spread) **or by a chosen
  key field's value** (e.g. inventory by `ItemID`), and projects the requested
  value fields per group. Differing values within a group render as a collision
  `⟨N: v1,v2,+M⟩` (ported from the Unity sister project's 29e-2 polish). Sorts
  most-populous-first; caps at `MaxGroups`. `SnapshotStore.PivotAsync` fetches
  only the key + value rows for the class then calls the engine; `Pivot*Async`
  helpers list classes (with instance counts) and fields (with cardinality).

- **C3 lite — key discovery.** `PivotKeyScorer` ranks numeric fields as group
  keys by **type prior** (Byte/enum + int good, float/double poor), **name prior**
  (id/index/type/slot/tag/… tokens), and **cardinality** (a key must actually
  partition: `1 < distinct < instances`). `SuggestKey` auto-selects the best;
  value-field interest reuses the calibrated `PropertyScoringTable.Score`. This
  is the UE answer to `discrete`'s "user must guess the business key" pain.
  (Top-level capture is all-numeric, so v1 keys are int/enum IDs; FName keys live
  on array-element rows — a later array-pivot path.)

- **C1 UI.** `ClassPivotViewModel` + `ClassPivotPanel` replace the Class Pivot
  placeholder tab: snapshot picker, class picker (filter + most-populous-first),
  a key-mode toggle (Identity / Field) with an auto-suggested key field, a field
  grid (tick value fields; shows type / distinct / instances / **key score**),
  and a results grid (Count / Key / Projected values). Selecting a class loads
  fields, suggests the key, and pre-ticks the most interesting value fields.
  Group → **Open in Live Walker** / **Copy Address** hands its representative
  instance to CE. Wired into `MainWindowViewModel.Pivot`; refreshed on tab
  activation. Like SPC, the projected-values column is a single rendered string
  (collision-aware) to stay AOT-safe — no per-field dynamic bindings.

- **Tests +22 → 1219 C# (1620 total: 1219 C# + 370 dll + 31 utf8).**
  `PivotEngineTests` (field/identity grouping, missing-key bucket, truncation,
  the `⟨N: …⟩` collision render), `PivotKeyScorerTests` (int>float, partitioning
  key > unique id, value interest via the scoring table), `PivotStoreTests`
  (class/field listing + field/identity pivots end-to-end), and
  `ClassPivotViewModelTests` (load → key suggestion → run, via a `PendingLoad`
  test seam on the selection-triggered async loads). Full clean Native AOT
  publish is clean. **Remaining experimental: C2 (find-by-value locator),
  C4 (DataTable-native pivot), C5 (right-click handoff from other panels),
  array-element pivot, A3c (CE .CT export).**

-----

## 2026-06-02 — Experimental: SPC Query (Phase B) + opt-in checkbox lock (build 824)

Second tranche of the experimental Snapshot / SPC / Pivot feature. **Phase B (SPC
Query)** ships the multi-session, type-agnostic directional query engine + UI —
the energy-bar driver case. Pure C# over the existing SQLite corpus, **zero DLL
change**. Plus a UX change on the experimental opt-in checkbox. Design of record:
[experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md)
§"Phase B".

- **B1 — engine (pure C#, no pipe).** `SpcQueryBuilder.Compile` turns an
  `SpcQuery` (ordered snapshot ids + per-snapshot predicate chain + join mode +
  filters) into one indexed SQLite statement: an **N-way self-join** over the
  `fields` table where the oldest snapshot `f0` is the anchor and every later
  snapshot `f{i}` inner-joins on the chosen identity key (so only fields present
  in ALL selected snapshots survive — the candidate intersection). Directional
  predicates become `WHERE` clauses comparing `f{i}` vs `f{i-1}`:
  Unchanged/Changed compare raw `hex` (type-exact); Increased/Decreased compare
  `numeric_value` by the field's declared width (no byte-reinterpret false hits).
  Pushing predicates into SQL lets a selective chain collapse a million-row
  intersection to a handful via the `ix_strict`/`ix_loose`/`ix_insession`
  indexes. Snapshot ids + limit are inlined (validated long/int — injection-safe);
  only the two LIKE filters are parameterised. `SnapshotStore.SpcQueryAsync`
  executes it and renders each snapshot's value via `SnapshotNumeric.Render`.
  **Join modes:** Strict `(class, norm_path, prop, offset)` / Loose
  `(class, outer_chain, prop)` / In-session `(class, gobjects_index, prop)`.
  `SpcModels` carries `SpcPredicateKind` (Any/Unchanged/Changed/Increased/
  Decreased — directional v1), `SpcJoinMode`, `SpcQuery`, `SpcResultRow`
  (rendered value sequence as one AOT-safe column), `SpcResult`.

- **B2 — UI.** `SpcQueryViewModel` + `SpcPanel.axaml` replace the SPC placeholder
  tab. A snapshot picker (DataGrid: tick + label + captured + **session tail** so
  cross-session spans are visible + a per-row predicate ComboBox), a Strict/Loose/
  In-session toggle, class/field filters, and a results grid (Class / Object /
  Field / Type / **value sequence**). The oldest ticked snapshot is the baseline
  (its predicate ignored); each later one compares to the previous ticked. Hit →
  **Copy Address** (newest snapshot's `obj_addr` + offset) / **Open in Live
  Walker** (the existing `NavigateToInstance` handoff). Wired into
  `MainWindowViewModel.Spc` (shares the snapshot store), refreshed on tab
  activation so a just-captured snapshot appears. Results column is a single
  rendered "value sequence" string — no per-snapshot `Binding("Values[i]")`, which
  would trip the IL2026/IL3050 AOT warnings (build-780 lesson).

- **Opt-in checkbox UX (user request).** The System-tab experimental-enable
  checkbox now renders at **Opacity 0.25**, and once it is checked **and** the
  user has opened any experimental tab (Snapshot / SPC Query / Class Pivot) it can
  **no longer be unticked**. `IExperimentalGate` grows `IsLocked` + `Lock()`
  (`IsEnabled` setter also refuses to go false while locked — defence in depth).
  The three experimental `TabItem`s gained `Tag`s; `MainTabs_SelectionChanged`
  calls `MainWindowViewModel.LockExperimental` (idempotent, no-op unless enabled)
  on first open. `PointerPanelViewModel` exposes `CanToggleExperimental`
  (`!IsLocked`) bound to the checkbox `IsEnabled`. The lock is **session-only**
  (NOT persisted) — a restart clears it, so the user can untick again until they
  re-open an experimental tab.

- **Tests +29 → 1197 C# (1598 total: 1197 C# + 370 dll + 31 utf8).**
  `SpcQueryBuilderTests` lock the SQL shape (join keys per mode, predicate
  clauses, filters, limit, validation) without a DB; `SpcStoreTests` run the
  engine end-to-end against a temp SQLite (money "decreased twice" + the
  cross-session energy-bar "same/same/down/up" + norm_path spawn-counter merge +
  filters + intersection drop); `SpcQueryViewModelTests` cover refresh/auto-select/
  run/copy-address/navigate; `ExperimentalGateTests` +4 lock the lock contract.
  Full clean Native AOT publish (`build.ps1 -Mode Publish`) is clean (the only ILC
  notes are the pre-existing benign X11/DBus Linux-backend trim messages).
  **Remaining experimental: Phase C (Class Pivot) + A3c (CE .CT export).**

-----

## 2026-06-02 — Experimental: Snapshot capture / diff (Phase 0 + A, builds 805-823)

First tranche of the experimental Snapshot / SPC / Pivot feature ported in
concept from the Unity sister project `discrete`. Gated behind an opt-in flag so
the default UI is unchanged. Design of record:
[experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md).
**The full capture → quota → compare loop now works end-to-end; A3 diff was
live-verified by the user (caught `DOLLFriendGameCharacter.HP 99→120`).**

- **Phase 0 — gating (build 805, `5b8a47d`).** The System-tab `bbfox` credit
  becomes a checkbox; checked → three experimental tabs appear (tooltip "Enable
  advanced experimental features"). `ExperimentalGate`/`IExperimentalGate`
  persists the opt-in to `%LOCALAPPDATA%\UE5CEDumper\experimental.json` (source-
  gen JSON; shared between the checkbox VM and tab-visibility VM via `Changed`).
  Also fixed an unrelated `build.ps1 -Target Test` crash from a stray empty
  `--no-restore` arg passed to the MTP runner (`4292004`).

- **A1a — DLL scalar capture (build 808, `fe8b5c2`).** Stateless cursor-paginated
  `begin_snapshot` / `snapshot_chunk` pipe commands. `Aura::CaptureSnapshotChunk`
  walks GObjects (game-only via `IsEnginePackage`), reuses cached
  `Ubel::WalkClassEx`, emits per-object identity (index/addr/name/class/
  outer_class/path) + every numeric scalar UPROPERTY (via the pure
  `ValueScan::SelectSnapshotNumericFields`, keyed on the existing NumericNoByte/
  NumericAll member sets).

- **A2 — SQLite store + capture UI (builds 809-813, `0747065` + `e832b4c` +
  `0da1248`).** `Microsoft.Data.Sqlite` raw ADO.NET (no EF Core). **Native AOT
  publish is clean and bundles `e_sqlite3.dll`** — the design's headline risk,
  resolved. `SnapshotStore` (denormalised `fields` table, strict/loose/in-session
  join indexes, streaming chunk writes); `SnapshotViewModel` orchestrates capture
  (begin → loop chunks → store → finalise, progress/cancel); `SnapshotPanel`.
  Pure helpers `SnapshotIdentity.NormalizePath` (leaf-only FName-suffix strip) +
  `SnapshotNumeric.TryFromHex`. **Per-game DB** `snapshots.<pe_hash>.db` — no
  cross-game mixing / unbounded growth / shared corruption blast radius.

- **A2c — quota + usage (build 815, `ab874a4`).** Per-game size quota with FIFO
  auto-eviction on capture (`EnforceQuotaAsync` drops oldest until ≤ quota then
  VACUUMs; newest always kept) + `GetUsageAsync` + per-snapshot `EstBytes`. Quota
  persisted in experimental.json; UI = quota dropdown + used/quota bar + Est.Size.

- **A3 — diff (build 817, `aeba44d`) + polish (build 820, `0731d4e`).** Both
  snapshots live in one per-game DB, so the diff is a single indexed SQL join on
  (class, GObjects index, property) WHERE bytes differ → changed rows (rendered
  via `SnapshotNumeric.Render`, direction ▲/▼) + Added/Removed churn counts. UI:
  Old/New pickers (default to the two newest), client-side **live**
  Class/Field/Object/Direction filters, Copy Address (CE handoff), Open in Live
  Walker.

- **A1b — struct-array inner-key capture (build 823, `ba7c370`).** The cargo/
  inventory case: `TArray<FStruct>` element inner numeric fields keyed by a
  reorder-immune inner key (`ValueScan::SelectArrayInnerKey`: keyworded-FName >
  FName > int > none). `Aura::CaptureSnapshotChunk` resolves the inner
  UScriptStruct (`ArrayProperty::Inner → StructProperty::Struct`), walks it, reads
  the `TArray`, emits ≤ `array_cap` elements with rendered inner key + numeric
  inner hex. Pipe `arrays` field; C# array-element rows; scalar diff excludes them
  (array diffing is Pivot). **Gotcha:** `FieldInfo`/`ClassInfo` are GLOBAL scope
  in Ubel.h, not `Ubel::` (C2039).

**Tests across the tranche**: 1485 → **1569** (1168 C# + 370 dll_helpers + 31
utf8). Every phase AOT-publish-clean. **Remaining (next sessions):** Phase B
(SPC multi-session directional — the energy-bar case), Phase C (Class Pivot,
incl. the array inner-key join + object/primitive arrays), A3c (CE .CT export).

-----

## 2026-05-29 — Value Search: with-byte variant `NumericAll` + result-volume warning (build 796-797)

Follow-up to NumericNoByte (build 794-795, todo #0d). Adds
`ValueScanDataType.NumericAll` — the same one-pass structured multi-numeric
scan but **including** the 1-byte families (`Int8Property` → Int8,
`ByteProperty` → UInt8). Bool stays excluded (it has its own single-type
scan). Members: Int8/UInt8 + Int16/UInt16 + Int32/UInt32 + Int64/UInt64 +
Float + Double (10).

Implementation rode almost entirely on the NumericNoByte plumbing — the meta
machinery (`IsMultiNumericDataType` / `ScanForValue` / `RefineCandidates` /
`CandidateToJson`) is keyed on `IsMultiNumericDataType`, so it picked up the
new type for free. The only DLL deltas: `MultiNumericMembers(NumericAll)`
(adds Int8/UInt8), `PropertyTypeNames(NumericAll)` (10-name union),
`TryDataTypeFromPropertyTypeName` now resolves `Int8Property`/`ByteProperty`
(safe for NumericNoByte — its union never feeds those names in),
`BuildNumericTargets` Int8/UInt8 fit-checks, and the SizeOf/NameOf/parse/
Format switch cases. `BuildNumericTargets` range-gates byte widths as
expected (`300` → no Int8/UInt8; `-5` → Int8 yes / UInt8 no; `200` → UInt8
yes / Int8 no).

**Result-volume warning** (user-requested): small values (0/1/255) match a
very large number of 1-byte fields, so the candidate set can explode. New VM
property `ValueSearchViewModel.DataTypeWarning` (non-empty only for
NumericAll, raised on DataType switch) drives an orange italic hint TextBlock
in `ValueSearchPanel.axaml` (binds via `IsNotNullOrEmpty`, same pattern as
`ErrorMessage`). DataType tooltip in `en.axaml` updated. `SupportsTolerance`
generalised to `IsMultiNumericDataType`; `DumpService.ToleranceAppliesTo`
includes NumericAll.

**Tests**: +30 DLL EXPECTs (members=10 incl. byte, byte property-name
resolution flipped from reject→resolve, NumericAll union-consistency lock,
BuildNumericTargets byte range-gates) + 10 C# (dropdown/classification,
tolerance, the warning is NumericAll-only + raises PropertyChanged, scan-type
mirror, wire name). All green: dll_helpers 349, utf8 31, C# 1105 (total 1485),
zero warnings. **In-game verification pending** (the volume-explosion behavior
the warning guards against is exactly what to sanity-check live).

-----

## 2026-05-29 — Value Search: multi-numeric "NumericNoByte" meta scan (build 794-795)

New `ValueScanDataType.NumericNoByte` — a "find this value across **every**
word/dword/qword/float/double field in one pass" mode, the natural starting
point when you know the value (e.g. `100`) but not whether it's stored as
`int32`, `float`, `uint16`, … Unlike CE's raw "All" type (which reinterprets
the *same untyped bytes* as multiple widths and produces overlapping false
hits), our scan is a **structured property walk** — each candidate's DECLARED
type is known — so each field is compared using *its own* declared width. A
`float Health` compares as float, an `int32 Ammo` as int32: **zero
byte-reinterpret false positives**. "No byte" deliberately excludes
`Int8`/`UInt8`/`Bool` (1-byte fields are too numerous; a small value would
flood the candidate set — the same reason CE breaks "Byte" out separately).
The with-byte variant is the planned follow-up.

Members: `Int16/UInt16`, `Int32/UInt32`, `Int64/UInt64`, `Float`, `Double`.

**DLL** (`ValueScan.{h,cpp}`, `Aura.{h,cpp}`, `Fern.cpp`):
- `IsMultiNumericDataType` / `MultiNumericMembers` / `TryDataTypeFromPropertyTypeName`
  (property-type-name → concrete DataType, rejecting Byte/Int8/Bool/non-numeric)
  / `NumericTargetSet` + `BuildNumericTargets`. The target set holds one
  little-endian buffer **per member width the value can represent** —
  `70000` yields no Int16/UInt16; `-5` no unsigned; `100.5` only Float/Double;
  hex `0x10` integer widths only. `PropertyTypeNames(NumericNoByte)` returns the
  8-name union (locked in a test to exactly match what
  `TryDataTypeFromPropertyTypeName` resolves, so a field can't be accepted yet
  fail per-field resolution).
- `ScanForValue` / `RefineCandidates` gained optional `multiTargets` /
  `multiTargets2` params. In multi mode the scalar-field **and** TArray-element
  comparison sites resolve each field's own DataType + matching target and call
  the existing `ComparePredicate`. Refine re-resolves each candidate's width
  from its stored `fieldType`. `CandidateToJson` renders each row's value with
  its own resolved width. Single-type paths are byte-identical (new branches
  are gated on `isMulti`).
- Tolerance flows through (applies per-member to float/double fields only;
  integer members ignore it, exactly as the single-type path already does).

**UI** (`ValueScanModels.cs`, `ValueSearchViewModel.cs`, `DumpService.cs`,
`en.axaml`): enum member + dropdown entry (listed first), `SupportsTolerance`
+ `ToleranceAppliesTo` include it, case-sensitive stays string-only. The
results grid's existing **Type** column shows each candidate's concrete
property type so the user sees which width matched. DataType tooltip updated.

**Tests**: +3 DLL test fns (`MultiNumericMembers` / `DataTypeFromPropertyTypeName`
incl. the union-consistency lock / `BuildNumericTargets` fit-rules) + 7 C# tests
(dropdown presence, family classification, tolerance/case gating, scan-type
validity mirror, wire-name + tolerance attach). All green: dll_helpers 319,
utf8 31, C# 1095 (total 1445). Zero compile warnings.

**Pending**: in-game verification (correctness + result-volume sanity on a
1M-object game), then the with-byte variant.

-----

## 2026-05-29 — Refactor: extract `ParallelGObjectsScan<ResultT>` template (build 793)

Follow-up to build 792 (logged in todo.md): the three parallelized scans each
carried ~identical scaffolding — a `ThreadResult` struct, the `nthreads` /
`perThread`-vector / `std::atomic<bool> deadlineHit` triplet, a `worker` lambda
open, the `ParallelIndexRanges` call, and a result-concat-with-truncate merge
loop. Centralised into two anon-namespace helpers in `Aura.cpp`:

- `ParallelGObjectsScan<PerThreadT>(count, body)` — owns `ScanThreadCount` +
  the `perThread` vector + the shared `std::atomic<bool> deadlineHit` + the
  `ParallelIndexRanges` call. `body(tr, beginIdx, endIdx, deadlineHit)` is the
  per-thread loop (per-object work + local maxResults cap + deadline check).
  Returns `{ perThread (moved), nthreads, deadlineHit.load() }`.
- `ConcatTruncate(perThread, &PerThreadT::member, maxResults)` — concatenates
  each thread's result vector (selected by pointer-to-member) in ascending-tid
  order, truncating to maxResults. **This is the ascending-merge + lowest-index
  truncation invariant, now a single source of truth** instead of triplicated.

Each scan keeps its own `ThreadResult` (the variable part — different element
type + counter set) and folds its per-thread stat counters inline (sum of
scanned/classesPrimed/classesWalked; `ScanForValue` unions its
`classesWithFields` set). `FindReferencesToUObject` carries the parallel phase's
deadline into its serial sparse-delegate pass via a plain `bool` seeded from
`scan.deadlineHit`.

Pure structural change — the per-object loop bodies are untouched, so behaviour
is byte-identical (the build-792 merge semantics are preserved exactly). Build
793 clean (zero warnings); **1358 tests unchanged** (31 utf8 + 247 dll_helpers +
1080 C#).

-----

## 2026-05-29 — Parallelize GObjects-walk scans + thread-safe Ubel caches (build 792)

Applied the **P1b parallelization** from `D:\Github\CE-Handwire-Private\docs\Memory-Scanning-Internals.md` §16 to the three GObjects-array scans. They were single-threaded `for (i = 0 .. count)` walks — the wall-clock floor on 1M+ object / multi-GB-heap games. Each walk is read-only against game memory + init-time constants (FNamePool offsets, `g_cachedUEVersion`, the FUObjectArray layout), so partitioning the index range across worker threads parallelizes cleanly. (Unlike the `discrete` Unity dumper that doc also covers, our scan is a *structured property walk*, not a raw VirtualQuery sweep, so the doc's SIMD / interval-tree advice doesn't apply — only parallelization does. AOBScan already had AVX2 + executable-section filtering, and our reads are already SEH `memcpy` with no per-chunk VirtualQuery.)

**Infra (`Aura.cpp`, anon namespace):**
- `ScanThreadCount(workItems)` = `clamp(hardware_concurrency - 2, 1, 16)`; returns 1 for < 8192 objects (thread-spawn cost dominates). The `-2` leaves headroom for the game's own threads + our pipe/UI thread.
- `ParallelIndexRanges(count, nthreads, body)` — partitions `[0,count)` into contiguous ascending chunks; chunk 0 runs inline on the calling thread, the rest on `std::thread`, all joined before return. Each worker body is wrapped in `try/catch(...)` + `LOG_WARN` so a throwing chunk (e.g. `bad_alloc`) can't `std::terminate` the host game — it degrades to a best-effort partial merge. 64-bit chunk math guards an int32 multiply overflow if a corrupted count comes back huge.

**Three scans rewritten** (`ScanForValue`, `FindInContainers`, `FindReferencesToUObject`): per-thread caches + result buffer, shared `std::atomic<bool> deadlineHit`, per-thread local cap at `maxResults`. Results merge in ascending-tid order then truncate to `maxResults` → **byte-identical result set to the serial walk** (same addresses, ascending order, lowest-index-preserved-on-truncation) for both under-cap and over-cap cases; deadline-hit is best-effort partial. `FindReferencesToUObject`'s MulticastSparseDelegate pass (a single global-TMap walk, *not* a GObjects walk) stays serial and runs once after the merge. `ScanForValue`'s `scannedClasses` stat is deduped via a per-thread set union.

**Ubel cache thread-safety (the real prerequisite):** the workers call `WalkClass(Ex)` / `GetName` / `ResolveEnumValue` / `GetCachedStructFields` concurrently, all of which memoize into file-scope `unordered_map`s — concurrent first-touches would race. Added 5 leaf-level mutexes (`s_nameCacheMutex` / `s_enumCacheMutex` / `s_walkClassCacheMutex` / `s_structFieldCacheMutex` + `s_calibrationMutex`). Pattern: run the expensive walk/read WITHOUT the lock, guard only the map find/insert, and return either a value copy (`ClassInfo` / `std::string`) or a node-stable `unordered_map` reference (insert/rehash never invalidates element references). `CorrectSubclassOffsets`' one-time `DynOff::` writes use double-checked locking (atomic `s_checked` fast-path + calibration mutex); the existing acquire/release on `s_checked` plus the invariant "every `DynOff` calibrated-offset read is preceded on the same thread by a `WalkClassEx → CorrectSubclassOffsets` call that observed `s_checked == true`" make the offset reads race-free without locking every read.

**Code review** (xhigh effort, 9 finder angles + sweep): no correctness bug in the merge/locking core. Fixed 3: (1) deadline check made **chunk-relative** — `((i - beginIdx) & mask) == 0` so it fires from each chunk's first iteration regardless of where `beginIdx` lands (the old `(i & mask) == 0` could delay the deadline + cross-thread `deadlineHit` check by up to a full stride, or never fire for sub-stride chunks); (2) worker `try/catch`; (3) int64 chunk math. Refuted with reasoning: DynOff "race" (synchronized via `s_checked` acquire/release), `WalkClass`/`GetCachedStructFields` reference-return (`unordered_map` node-stability), calibration↔name deadlock (single, consistent lock order — `GetName` holds no lock while calling out), `classesPrimed` "2× over-count" (the serial code already counted per-instance, not per-unique-class, so the per-thread sum matches).

Tests: **1358 unchanged** (31 utf8 + 247 dll_helpers + 1080 C#). The existing suites stub the DLL / test predicates, so they confirm no compile-or-logic regression but don't exercise the live parallel walk. **In-game verified OK by the user (2026-05-29)** — parallel result set matches the serial walk, no hang / crash, and the expected multi-core First-Scan speedup held.

**Follow-up (logged in todo.md):** the `ThreadResult` / `perThread` / merge scaffolding is ~100 lines duplicated across the three functions — a candidate for a `ParallelGObjectsScan<ResultT>` template helper. Deliberately deferred to avoid refactoring just-verified concurrency code in the same change.

-----

## 2026-05-27 (PR #211 merged dev → main) — AOT-warning cleanup on Invoke structured-return DataGrid (build 780)

The pick #5 structured-return DataGrid (build 775) wired each `DataGridTextColumn` with `new Avalonia.Data.Binding("PropertyName")`. That ctor has `RequiresUnreferencedCodeAttribute` + `RequiresDynamicCodeAttribute` because Avalonia's string-path Binding uses reflection to resolve the property — directly violates CLAUDE.md's "Native AOT compatible, no reflection-based APIs" rule. `dotnet publish` emitted 18 warnings (IL2026 + IL3050) across the four column declarations + their forwarded constructor analysis.

Fix: switch the four columns to `DataGridTemplateColumn` + `FuncDataTemplate<StructFieldValue>(lambda)`. Each cell's text comes from a strongly-typed `Func<StructFieldValue, string>` so no reflection or dynamic dispatch fires. Centralised via a new private helper `AddStructuredReturnColumn(header, width, textSelector)` so the four column declarations stay one-line each.

Trade-off documented in the commit: FuncDataTemplate materialises each cell once at row creation and doesn't observe per-property INPC after that. Acceptable for this panel because `UpdateStructuredReturnGrid` replaces `ItemsSource` wholesale on each FIRE — no in-place mutation case.

Tests: 1080 (unchanged — pure UI plumbing fix). Publish build emits zero IL2026/IL3050; UE5DumpUI.exe still trims to 42.4 MB.

-----

## 2026-05-27 — Console panel: UCheatManager stripped-body hint footer (#6, build 778)

First live test of the build-731 Console tab surfaced the canonical gotcha: `UCheatManager::Fly` / `Ghost` / `God` / `Walk` / `Slomo` / `ChangeSize` invokes return `Result=0` (OK) but produce no in-game effect on cooked Shipping builds. Root cause: UE wraps these in `#if !UE_BUILD_SHIPPING`; Epic ships with that defined, so the function bodies compile out but the `UFUNCTION(exec)` reflection metadata (generated pre-cook by UHT) survives. PE call really happens, function returns 0, no-op.

**Distinct failure mode** from the build 647-648 wrong-vtable-slot bug. The discriminator is `Stark::GetHookFireCount()`: `>0 + Result=0 + no effect = cooker strip`; `==0 = hook on wrong slot`.

Surface area:
- `ConsoleViewModel.IsLikelyUCheatManagerExec(entry)` — public + static, case-insensitive substring match on `ClassName` or `SuperName` against "CheatManager". Catches engine class + game-defined subclasses (`MyGameCheatManager` / `BP_CheatManager_C`) + super-chain-via-immediate-super (`AFooCheats : UCheatManager`). Public so tests lock the heuristic without standing up a VM.
- `ConsoleViewModel.SelectedExecHint` — computed property re-evaluated on `SelectedResult` change via `OnSelectedResultChanged` partial; warning text when the row is UCheatManager-derived, empty otherwise.
- `ConsolePanel.axaml` — orange-bordered footer Border below the status row, IsVisible bound to non-empty SelectedExecHint. Same visual treatment as the Value Search "native C++ fields" banner so users recognise the warning pattern.
- `docs/lessons-learned.md` — new bullet under "UFunction Invoke / ProcessEvent" with the diagnostic flow.
- Memory file `feedback_ucheatmanager_stripped.md` — full diagnostic table + per-version UE source pointer + "what we don't try to do" scope (bypassing the strip is out of scope; this tool is discovery + dispatch).

Tests: 1065 → 1080 (+15). 10-row theory for the predicate (5 positive incl. case-insensitive + super-name-only variants, 4 negative), null guard, SelectedExecHint empty/populated/refresh-on-change.

Deferred: full super-chain walk to catch second-degree subclasses (`BP_MyCheatManager_C : MyGameCheatManager : UCheatManager`) — current substring heuristic catches the first two layers.

-----

## 2026-05-27 — Invoke result: structured-return DataGrid for struct returns (#5, build 775)

Existing decoder already produced `"X=1.0, Y=2.0, Z=3.0"`-style comma joins inside `_resultLabel` for FVector / FRotator returns. Pick #5 wires the same decode into a small 4-column DataGrid (Field / Type / Value / Offset) below the text decode so each sub-field becomes its own row with absolute buffer offset.

What landed:
- `Models/StructFieldValue.cs` — pure record (Name, Type, Value, Offset). Offset is **absolute** buffer offset (return param offset + sub-field offset) so users can copy it into Find In Containers / CE memrec setup directly.
- `Services/StructReturnDecoder.cs` — static `Decode` + `CanDecode`. Resolution order: KnownStructLayouts (per-version locked) → DLL-discovered dynamic StructFields → empty list. Delegates each byte→typed-value cell to `InvokeParamDialog.DecodeParamValue` so the grid and result-label never disagree on a byte mapping. SafeDecode wraps with try/catch so a single bad field doesn't blow the whole grid.
- InvokeParamDialog — pre-resolves `_returnParam` at construction; clears + hides grid at top of `OnFireClicked` so stale rows don't flash across invocations; `UpdateStructuredReturnGrid` populates after a successful FIRE; header label includes struct name (e.g. `"Return value (decoded — Vector):"`).

What's NOT done (deferred):
- **ObjectProperty / ClassProperty return resolution** to "Name (Class)". Pointer returns still show as 8-byte hex in the existing decode; resolving to UObject name needs a DLL pipe round-trip (`Ubel::GetName` on the returned address) — separate scope.
- **Recursive struct expansion**. `FHitResult.Location` (FVector) renders as one "Location (StructProperty)" row with the inner FVector showing as raw bytes; WalkFunctions only goes one level deep on `param.structFields` by design. Nested expansion needs recursive DLL-side discovery.

Tests: 1052 → 1065 (+13). New `StructReturnDecoderTests` covers CanDecode contract, FVector + FRotator decode shape, KnownStructLayouts-wins-over-StructFields precedence (locked by giving the same param both inputs with conflicting field lists), dynamic-fields fallback per-type decode, absolute offset surfacing, short-buffer tolerance (out-of-bounds reads degrade to "?" instead of throwing).

Verification target: Geri's `PlayerCameraManager::GetCameraLocation` returns FVector — grid should show 3 rows (X / Y / Z floats) at offsets 0x4 / 0x8 / 0xC of the post-call param buffer.

-----

## 2026-05-27 — NuGet packages bump + dotnet test migration to MTP mode (build 771)

User-driven NuGet bumps surfaced a .NET 10 + `Microsoft.Testing.Platform.MSBuild` 2.x compat break: the legacy VSTest bridge target was dropped on .NET 10 SDK, so the existing `dotnet test <proj>` invocation errored with "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later" (see https://aka.ms/dotnet-test-mtp-error).

Migration per the official upgrade path:

1. **New `global.json`** at repo root with:
   ```json
   { "test": { "runner": "Microsoft.Testing.Platform" } }
   ```
   switches `dotnet test` to MTP mode natively, replacing the VSTest bridge entirely.

2. **`build.ps1` invocation updated**: `--project <proj>` instead of legacy positional form (the latter silently downgrades to VSTest mode). Dropped `--nologo` / `-v minimal` since they aren't in MTP mode's allowed dotnet-test flag list — unknown flags get forwarded to xunit.v3, which prints help + exits 5.

3. **Dropped explicit pins** on `Microsoft.Testing.Platform.MSBuild` / `Microsoft.Testing.Extensions.Telemetry` / `Microsoft.Testing.Extensions.TrxReport.Abstractions`. xunit.v3 bundles its own MTP bridge (`Xunit.MicrosoftTestingPlatform.*`) compiled against a specific MTP API surface; pinning explicit 2.2.3 versions overrode xunit.v3's tested transitives and triggered `MissingMethodException` on `IOutputDevice.DisplayAsync` at test-run time. Let xunit.v3 resolve transitively.

NuGet bumps kept (all transitives surfaced as explicit pins by the user's IDE bump):
- Avalonia.Angle.Windows.Natives 2.1.27548.20260419
- HarfBuzzSharp.NativeAssets.* 8.3.1.5
- SkiaSharp + NativeAssets.* 3.119.4
- Tmds.DBus.Protocol 0.93.0 → 0.94.0
- Microsoft.NET.Test.Sdk 18.5.1 → 18.6.0
- Microsoft.Extensions.* / System.Memory.Data / System.Security.Cryptography.ProtectedData → 10.0.8
- Azure SDK chain transitives (Azure.Core 1.57.0 / OpenTelemetry / Microsoft.Identity.Client 4.84.1 etc.)

Tests: 1052 passing under MTP mode (no behaviour change — pure dependency bump + test runner mode migration).

**Lesson logged**: never explicitly pin packages that come in transitively via a test framework's own bridge. Let the framework own the version dance for its internal compatibility surface.

-----

## 2026-05-27 — Multi-row → One .CT batch generator (#3, build 760)

Polishes the existing single-row AA(Baked) / Freeze export into a multi-row batch on the **Interesting Functions** + **Interesting Properties** tabs. Promotes the discover→use workflow from "research toy" to "shareable cheat-table author":

1. Discover (existing flow: Load → score → filter → scan)
2. Select N relevant rows (Ctrl/Shift+click; DataGrid is now `SelectionMode="Extended"`)
3. Click 📦 Generate CT → save-dialog → one .CT with per-row AA Script entries, grouped by category

### Architecture

- `Models/CheatTableRow.cs` — discriminated row type (`CtPropertyRow` wraps `FreezeScriptParams`, `CtFunctionRow` wraps Baked params). Source-panel-agnostic so future call sites (LiveWalker mixed rows, Live PE Profiler hits) can feed the same builder without churn.
- `Services/CheatTableBuilder.cs` — assembles N rows into a `<CheatTable CheatEngineTableVersion="46">` XML matching CE's File→Save As shape. Root group → per-category sub-groups (alphabetical, Uncategorised trails) → one `<CheatEntry>` per row with `<VariableType>Auto Assembler Script</VariableType>` body. IDs sequential from `BaseId=1000`. XML escapes all five canonical entities so `TArray<int>` / `&` / quotes in descriptions can't break a CT load.
- Property rows reuse `FreezeScriptGenerator`; function rows reuse `BakedScriptGenerator`. No new generator code.
- VM stays IO-free — emits `RequestSaveCheatTable(defaultName, ctXml)` event; MainWindow owns the platform save-file dialog + UTF-8 write via the existing `SaveCheatTableAsync` helper.

### UX details

- **Property rows**: defaults to a per-UE-type "obvious cheat" freeze literal (Float = `9999.0`, Int = `99999`, Bool = `true`, Byte = `255`); user edits CFG.value in CE before activating. Struct / array / non-scalar rows are skipped (status: "Generated N entries (skipped K unsupported)"). Description includes the defining-class hint when it differs from the user-picked class.
- **Function rows**: BakedValues intentionally empty (helper zero-fills PARAMS); description for parameterised funcs reads `"Class::Func (N (XB)) — edit baked PARAMS in CE"` so users know to populate before activating. No-arg funcs read `"Class::Func()"`.
- Default filename: `{Source}-batch-yyyyMMdd-HHmmss.CT` where Source is `InterestingProperties` / `InterestingFunctions`.

Tests: 1028 → 1052 (+24). `CheatTableBuilderTests` covers structural shape (CheatTable root + nesting + per-row VariableType + UserdefinedSymbols), category alphabetical ordering + Uncategorised-trails-last, input order preservation within a category, 3-row / 10-row / mixed property+function selections, ID uniqueness + sequential allocation, empty/null rows throw, XML escaping for `TArray<int>` / `&` / quotes, DefaultFileName format + fallback, SanitizeFileName, VM mapping (BuildRowsFromSelection skip-unsupported counts, defining-class targetClass choice, per-type freeze literal theory).

Out of scope for v1: LiveWalker integration (heterogeneous row types — needs its own UX pass); AOBMaker direct-inject of the generated CT (currently save-to-disk + user opens in CE).

-----

## 2026-05-27 — `scripts/analysis/diff_dumps.py` — same-game patch diff at UProperty granularity (#4)

Pure-Python sister script to `analyze_dumps.py` — consumes the same `Dump All Metadata` JSONL corpus but does N=2 patch-vs-patch diff instead of N=many cross-game aggregation. Closes the cheat-table-maintainer pain: when a game ships a silent UPROPERTY offset shuffle, hand-coded tables break and the binary search for the new offset is hours of grinding.

Surface area:
- Class-level: Added / Removed / props_size delta
- Property-level: Added / Removed / **Moved** (same name, different offset/size) / **Type changed** (FloatProperty → DoubleProperty incl. inner_type / struct_type / obj_class / enum sub-changes)
- Function-level: Added / Removed / **Signature changed** (return_type / num_parms / parms_size / flags differ — bodies aren't in the dump so logic-only changes are invisible; documented limitation)

Match keys: classes by `path` (canonical UE id; `addr` is session-local and ignored). Path normalisation (`//Script/X` ≡ `/Script/X`). Properties + functions by `name` within class.

CLI:
```bash
python diff_dumps.py <old.jsonl> <new.jsonl>           # full report → stdout
python diff_dumps.py <old.jsonl> <new.jsonl> -o diff.md
python diff_dumps.py <old.jsonl> <new.jsonl> --minimal       # Moved + sig-changed only
python diff_dumps.py <old.jsonl> <new.jsonl> --include-engine
python diff_dumps.py --self-test                              # synthetic fixtures
```

`--self-test` runs 6 built-in synthetic-fixture scenarios (Added / Removed / Moved / TypeChanged / SignatureChanged paths, engine-class filter, path normalisation, self-diff identity, Markdown render edge cases) so the diff logic is checkable without external dumps.

Verified:
- Self-test all assertions pass
- Self-diff Geri vs Geri → 87 unchanged, 0 changes (identity property holds)
- Cross-game Geri vs ES2 → module-mismatch warning fires; 1652 added / 86 removed / 0 changed (doesn't crash on massive divergence)

README extended with the same-game-patch workflow + match-key + known limitations sections. No auto-rename detection (renamed field shows as Removed + Added); no function body comparison (only metadata is dumped — logic-only refactors invisible; covered by Live ProcessEvent Profiler in the future).

-----

## 2026-05-27 — Value Search Phase 2: FString / FName / FText + FVector / FRotator + TArray<T> (build 757)

Closes the v2-deferred list from `docs/todo.md` Section 0 — extends the build-738 numeric-primitives Value Search to the three deferred type families:

### Phase 2A — FString / FName / FText

- New ScanTypes `Contains` / `StartsWith` / `EndsWith` with CE-style case-insensitive default (ASCII fold; non-ASCII bytes compare bitwise). Opt-in case sensitivity exposed as a UI checkbox visible only for string DataTypes.
- `Ubel::ReadFStringAt` / `ReadFNameAt` / `ReadFTextStringAt` exposed publicly so Aura's scan path doesn't duplicate the FString-header decode + UTF-16 sanitize logic.
- FText is best-effort (cooked games strip most display strings — ES2 smoke test resolved 1/1551 classes, expected).

### Phase 2B — FVector / FRotator (component-wise tolerance)

- `CompareVectorPredicate` compares X / Y / Z per axis with shared tolerance. Exact / Bigger / Smaller / Between apply to all axes; Changed / Increased / Decreased trigger when ANY axis moves (matches in-game movement patterns).
- StructProperty inner-name match against `VectorStructNames` (`Vector` / `Vector3f` / `Rotator` / `Rotator3f` / NetQuantize variants) so we don't read 12 bytes from every UE struct.
- FTransform DataType reserved on the wire but mapped to no struct names — returns zero hits pending per-version Translation offset detection (UE4/UE5-non-LWC: +16, UE5 LWC: +32). Documented in `VectorStructNames()`.

### Phase 2C — TArray<T> with safety circuit-breaker

- `ScanField` gains `isArray` + `elemStride` + `elemTypeName`. `expandFields` emits `ArrayProperty` entries when Inner matches the wanted DataType (primitive / string / vector inner all supported).
- Per-instance loop branches on `isArray` and walks `TArray.Data`, emitting one candidate per match labelled `FieldName[N]`.
- **Soft circuit-breaker** per memory `project_value_search_caveats`: `Num > 10M` skips with `LOG_WARN`; `Num >= 0`, `Max >= Num`, `Data != null` guards. Macht's SEH-wrapped reads turn stale post-reallocation addresses into safe failures (candidate drops without crash).
- Refine re-reads strings via `c.addr` instead of `(instanceAddr, fieldOffset)` so array-element strings work uniformly with direct string fields.

### Wire-schema additions (all backward-compatible)

Fields are omitted in the common case so pre-Phase-2 traffic is byte-identical:
- `begin_value_scan` / `refine_value_scan` gain optional `case_sensitive` (attached only for string DataTypes when true).
- Existing `tolerance` now applies to FVector/FRotator too (component-wise per axis).
- DataType strings: `FString`, `FName`, `FText`, `FVector`, `FRotator`, `FTransform`.
- ScanType strings: `Contains`, `StartsWith`, `EndsWith`.

Pipe handlers in Fern.cpp gain `IsScanTypeValidFor` gating that rejects nonsensical combinations (`FString + Bigger`, `Int32 + Contains`, etc.) with an explicit error rather than letting the scan run and silently return 0 hits.

### Live verification (EVERSPACE 2, UE 5.5)

| DataType | ScanType | Value | Result |
|---|---|---|---|
| FString | Contains | "Engine" | 54 / 323ms ✓ |
| FName | Contains | "Engine" | 7119 / 415ms ✓ |
| FText | Contains | "Engine" | 1 / 396ms (cooked-build limitation) |
| FVector | Exact (CSV) tol=0.01 | 49966 / 303ms (hit max_results cap) |
| FRotator | Exact tol=0.01 | 16819 / 290ms ✓ |

No `LOG_WARN: skipping TArray with Num=` fired across ~1.15M scanned objects — ES2 has no pathological arrays.

### Tests

1081 → 1306 total (+225).
- DLL helpers: 124 → 247 (+123). ScanType partition for Contains/StartsWith/EndsWith; type-family predicates (`IsStringDataType` / `IsVectorDataType` / `IsSubstringScanType`); `IsScanTypeValidFor` matrix; `CompareStringPredicate` Exact/Substring/Changed/Unchanged; `CompareVectorPredicate` Exact/Ordering/Between/PrevValue/RejectsSubstring; `VectorStructNames` per-family content.
- C# tests: 957 → 1028 (+71 spread across this + tolerance work). `IsScanTypeValidFor` 27-row theory mirroring DLL contract; `VisibleScanTypeOptions` filtering per DataType; `SelectedScanType` auto-reset on incompatible-type switch; `SupportsCaseSensitive` / `SupportsTolerance` gating; wire-shape locks for `case_sensitive` (string-only) and `tolerance` (now also vector types).

-----

## 2026-05-26 — Value Search: Float/Double tolerance (CE-style rounded scan) (build 746)

User-requested follow-up after the build-744 fix landed TQ2 GAS scans. Game UIs commonly display attributes rounded to the nearest integer ("HP: 338") while the underlying float is something like 337.5. Without tolerance, a scan for "338" misses 337.5 and the user has to guess at decimal precision.

New `tolerance` parameter on `ValueScan::ComparePredicate` + `Aura::ScanForValue` + `Aura::RefineCandidates`, only meaningful for `Float`/`Double` (integer types ignore it -- exact comparison stays the default for non-floating types where tolerance semantics don't transfer cleanly). Per-ScanType behavior:

| ScanType | Tolerance semantic |
|---|---|
| Exact     | `\|cur - target\| <= tol`              (matches displayed-rounded values) |
| Bigger    | `cur > target + tol`                  (clearly above tolerance band) |
| Smaller   | `cur < target - tol` |
| Between   | `target1 - tol <= cur <= target2 + tol` (widen the inclusive range) |
| Changed   | `\|cur - prev\| > tol`                  (changed beyond noise) |
| Unchanged | `\|cur - prev\| <= tol` |
| Increased | `cur > prev + tol`                    (strictly above prev beyond noise) |
| Decreased | `cur < prev - tol` |

The `Float Exact tol=0.5` scan on the user's TQ2 repro example: target 338, candidates whose underlying float falls in `[337.5, 338.5]` match. The CurrentValue / BaseValue floats sitting at 337.5 get found whether the user types 337.5, 338, or 338.5 (with tol >= 0.5).

UI: new `± Tol` `NumericUpDown` (default 0.5) appears next to Value/Value2 when DataType is Float or Double. Hidden for integer types. `SupportsTolerance` VM property drives the visibility binding.

Wire shape: `tolerance` JSON field on `begin_value_scan` / `refine_value_scan` requests. **Omitted when the value is 0 or the type is integer** -- preserves byte-identical wire shape for existing exact-scan call sites and integer scans (back-compat with any external pipe consumer that might be sniffing the protocol). DLL defaults to 0 if absent.

Tests:
- DLL: +18 assertions (124 -> 142 dll_helpers) covering Float Exact within / outside band, tol=0 strict equality back-compat, Bigger / Smaller band shift, Unchanged within drift / Changed beyond drift, Increased / Decreased prev-value semantics, Between widening both bounds, Int32 / UInt64 integer types IGNORE non-zero tolerance.
- C#: +11 (957 -> 968) covering tolerance attached on the wire for Float, omitted for 5 integer types (theory), omitted when zero, refine_value_scan attaches tolerance, VM SupportsTolerance gates by DataType (Int -> false, Float/Double -> true, UInt64 -> false), tolerance pass-through Float / dropped Int through the VM.

-----

## 2026-05-26 — Value Search hotfix #2: WalkClassEx must calibrate FSTRUCTPROP_STRUCT before reading (build 744)

Build-740 recursion fix shipped but TQ2 live test STILL returned 0 candidates. Targeted DIAG logging on `class name contains "AttributeSet"` revealed the actual failure mode:

```
ValueScan DIAG: class 'GrimAttributeSetHealth' WalkClassEx fields=8:
  [1] name='MaximumHealth' type='StructProperty' offset=0xC8 size=37
      addr=0x1FB83F53800 structType=''   ← empty!
  ...
ValueScan DIAG: class 'GrimAttributeSetHealth' emitted 0 ScanFields
```

`structType=''` on EVERY StructProperty was the smoking gun. `ReadSubclassTypeName(f.Address + FSTRUCTPROP_STRUCT)` returned empty because the runtime `FSTRUCTPROP_STRUCT` was still at default `0x78` — TQ2 (UE 5.07) actually needs `0x74`. My nested-struct recursion uses the same offset, so it also bailed on every read.

Root cause: `CorrectSubclassOffsets` (Ubel.cpp:2441) is the calibration routine that probes the right offset, but it was **only called from `WalkInstance`** (line 2864). Any caller that hit `WalkClassEx` without a prior `WalkInstance` saw the uncalibrated default — including `Aura::ScanForValue`'s GObjects walk. The reason the bug was hidden in earlier features: `WalkInstance` typically fires from Live Walker before any other operation, so PropertySearch / etc. always inherited a calibrated value. Value Search is the first feature whose hot path doesn't depend on Live Walker firing first.

Fix: added `CorrectSubclassOffsets(info.Fields)` call at the top of `WalkClassEx`, just after `WalkClass`. The function is idempotent (atomic-guarded), so calling it on every WalkClassEx is a no-op after the first successful probe. Forward declaration added near the top of Ubel.cpp because the definition lives at line ~2441.

Same fix strengthens other `WalkClassEx` consumers (SearchProperties / EnumerateAllFunctions / SdkExport) — they'd also have shown empty `structType` / `innerType` if invoked before any WalkInstance, just hadn't triggered the bug because of LiveWalker-first usage patterns.

957 C# + 124 DLL tests still pass. TQ2 live re-verification pending.

-----

## 2026-05-26 — Value Search: recurse StructProperty so GAS / FGameplayAttributeData values are reachable (build 740 hotfix)

Live-game repro on TQ2 (UE 5.07) the same session as the shipping build
738 entry below: user opened CE Structure Dissect, walked `GWorld →
OwningGameInstance → LocalPlayers[0] → PlayerController → Character →
m_pStatsComponent → m_pAttributeSetHealth → MaximumHealth
(GameplayAttributeData) → BaseValue = 337.5`. Opened Value Search,
Type=Float, Scan=Exact, Value=337.5 → **0 candidates in 743 ms
(scanned 380748 objects, 1717 classes with matching fields)**.

Root cause: `Aura::ScanForValue::buildClassIndex` walked only
top-level `ClassInfo.Fields` entries. `BaseValue` / `CurrentValue` live
inside `FGameplayAttributeData` — a USTRUCT used as a StructProperty
member of `UAttributeSetHealth`. The scan saw `MaximumHealth` as a
StructProperty (not a leaf float type) and dropped it. Same dead-end
would apply to FVector / FRotator / FTransform members of any UObject,
not just GAS-style attribute sets.

Fix: replaced the linear field loop with a recursive `expandFields`
lambda. For each StructProperty encountered, reads `FSTRUCTPROP_STRUCT`
to resolve the inner `UScriptStruct*` and recurses with cumulative
offset + dotted name prefix. Emits ScanField entries for every
matching-type leaf at the correct cumulative offset. Cycle guard via
visited-set; hard depth cap at 4 to bound worst-case CPU on
pathological types (self-referencing USTRUCTs declared as linked
nodes).

```
Before fix (TQ2 repro):
  UAttributeSetHealth.MaximumHealth → StructProperty → skipped
  → 0 candidates

After fix:
  UAttributeSetHealth.MaximumHealth → recurse FGameplayAttributeData
    → MaximumHealth.BaseValue    @ +0x48 → leaf, emitted
    → MaximumHealth.CurrentValue @ +0x4C → leaf, emitted
  → 2+ candidates per AttributeSetHealth instance
```

Uses `Ubel::WalkClassEx` at every depth so BoolProperty FieldMask is
populated for nested bitfield bools on the UE5 FProperty path
(WalkClass alone covers UE4 UProperty path only).

Container properties (Array / Map / Set / Optional) are intentionally
NOT recursed — TArray\<T\> remains v2 gated by the crash-risk plan in
`project_value_search_caveats` memory. StructProperty recursion is
strictly safe: no allocation walk, no Num-bounded iteration.

No new test surface — recursion is implementation-detail inside the
scan lambda; visible only through end-to-end scan results on a live
UE process. Existing predicate / session / parser tests unchanged
(957 C# + 124 DLL still pass on build 740). TQ2 live re-verification
is the contract test.

-----

## 2026-05-26 — Value Search tab: CE-style First Scan / Next Scan over UPROPERTY fields (build 738)

New end-to-end capability: given a value (int / float / bool / etc),
walk every UPROPERTY-declared field of every UObject instance and
return the addresses + class + field metadata for each match. Refines
with the standard CE Next-Scan predicates (Exact / Bigger / Smaller /
Between + Changed / Unchanged / Increased / Decreased) inside a
DLL-side session. Fills the long-standing search-by-value gap —
PropertySearch was search-by-name; InstanceFinder was search-by-address;
this is the third axis. Port of discrete's Phase 27b
[ValueScanSession](../../discrete/dll/src/shared/ValueScanSession.h)
shape with UE-specific scan engine.

Cross-repo motivation: discussed at session start whether the Unity-
side feature (D:\Github\discrete `whatIsAt` + `beginValueScan`) was
worth porting to UE5CEDumper. Verdict: better suited here than there
because UE's reflection metadata is more uniform than IL2CPP's mix of
typed instances + raw native arrays, and the FindByAddress / GObjects-
walk infrastructure for enriching candidates was already mature.

### Architecture

```
UI Value Search tab
  ↓ begin_value_scan { data_type, scan_type, value, value2?, game_only, max_results }
DLL Aura::ScanForValue
  ↓ walks GObjects
  ↓ skips UClass meta-objects (IsClassLikeMeta filter)
  ↓ per-class field index cached lazily via Ubel::WalkClassEx, filtered to
     fields whose TypeName matches the requested DataType
  ↓ typed-read instance+offset bytes, apply ComparePredicate
  ↓ on hit: lookup FindDefiningClass (cached), build ValueScan::Candidate
ValueScan::SessionManager::Begin → sessionId
  ↑ candidates echoed back to UI

UI Next Scan
  ↓ refine_value_scan { session_id, scan_type, value?, value2? }
DLL Aura::RefineCandidates
  ↓ re-reads each candidate's bytes
  ↓ predicate compares to user value (Exact/etc) or candidate.prevValue
     (Changed / Unchanged / Increased / Decreased)
  ↓ prunes failing, updates prevValue on survivors
  ↑ surviving candidates returned

UI New Scan
  ↓ end_value_scan { session_id }
ValueScan::SessionManager::End drops the session.
Sessions auto-expire at 5 min idle so abandoned sessions clear lazily.
```

### MVP scope (build 737)

- **Types**: Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool.
  BoolProperty bitfields normalized to 0/1 via FieldMask so refine
  predicates see stable boolean semantics across sibling-bit flips.
- **Scan candidate source**: GObjects → UProperty fields only.
  Deliberately NOT raw memory scan — the UE precedent is better than
  discrete's because UProperty metadata gives lossless typing and the
  raw-memory false-positive problem is sidestepped entirely.
- **Scan deadline**: 15s; `deadline_hit` surfaces in the response so
  the UI can show "scan truncated — narrow predicate" instead of
  silently returning a partial set.
- **Hard-locked UX contract**: Value Search tab MUST surface a banner
  reading "Native C++ fields (non-UPROPERTY) cannot be found here — use
  Cheat Engine's raw memory scan for those." Locked in by a literal-text
  test (`ValueSearchTests.Banner_LiteralText_IsPresentInEnAxaml`,
  `Banner_IsReferencedByValueSearchPanel`). Rationale: scan walks
  UProperty reflection metadata, so non-reflected C++ fields (private
  members not declared UPROPERTY, or fields inside non-UObject native
  structs embedded in a UObject) are invisible. Without the banner the
  user would assume "value not found" means "value isn't there" rather
  than "this tab can't see it" — a silent failure mode of the worst
  kind.

### Deferred (v2)

- **FString / FName / FText / TArray\<T\> / FVector / FQuat / FTransform**
  — UE-specific types where typed read is non-trivial.
- **TArray scan**: see memory `project_value_search_caveats` for the
  open risk. Existing Copy CE XML / CSX / SDK Header exports apply an
  array size cap to keep payloads bounded; the value scan must NOT
  inherit that cap (a hit at index 50000 of a TArray\<int32\> is still a
  legit hit). Concern: removing the cap may risk crash / hang on
  pathological containers (malformed Num, freed slack, OptionalProperty
  mis-decoded as TArray). Mitigation plan: soft circuit-breaker on Num
  (>10M elements skip with telemetry log) rather than a hard cap; verify
  `Aura::FindInContainers`'s 15s deadline is enough back-pressure;
  stress-test on Satisfactory inventory arrays before shipping.
- **Native C++ field scan**: explicitly excluded; banner directs user
  to CE for those (intended behaviour, not a future task).

### DLL — 3 new files + 3 new pipe cmds

- `dll/src/ValueScan.h` / `.cpp` — DataType / ScanType enums, Candidate
  struct, SessionManager (singleton, 5-min idle expiry), ComparePredicate
  (typed-load + ordered predicate for int64/uint64/double). Heap-leaked
  singleton matches discrete's precedent so DLL teardown doesn't
  destructor-storm tens of thousands of candidates.
- `dll/src/Aura.h` / `.cpp` — adds `ScanForValue` (GObjects walk +
  per-class field index + FindDefiningClass cache) and
  `RefineCandidates` (re-read + prune + prevValue update).
- `dll/src/Renge.h` — `CMD_BEGIN_VALUE_SCAN` / `CMD_REFINE_VALUE_SCAN` /
  `CMD_END_VALUE_SCAN` constants; **pipe cmds now 39** (+3).
- `dll/src/Fern.cpp` — 3 new handlers + `ParseValueBytes` (string →
  LE bytes per DataType, with 0x-hex prefix support for unsigned ints)
  + `FormatValueBytes` (inverse) + `CandidateToJson` helpers.

### Tests

- **DLL** (`dll/tests/dll_helpers_test.cpp`): +31 new assertions across
  `Test_ValueScan_DataTypeSizes`, `Test_ValueScan_ParseDataTypeRoundTrip`,
  `Test_ValueScan_ScanTypePartitioning`, `Test_ValueScan_Predicate_Int32`,
  `Test_ValueScan_Predicate_Int8Negative` (signed-extension regression
  guard), `Test_ValueScan_Predicate_Float`, `_Double`, `_Bool`,
  `_UInt64_RangeBoundary` (ensures unsigned path on 0xFFFF...
  values that would be negative as signed), `Test_ValueScan_SessionLifecycle`
  (Begin → ViewWith → RefineWith mutation → End → missing-session
  contract). DLL test suite **93 → 124** (utf8 31 + dll-helpers 93).
- **C#** (`ui/UE5DumpUI.Tests/ValueSearchTests.cs`): +22 tests including
  service-level JSON round-trips, scan-type partition theory (8
  predicates × 2 buckets = 16 assertions), VM workflow contract
  (First Scan rejects prev-value scan types; Between requires Value2;
  Next Scan with prev-value type omits `value` field; New Scan ends
  session + clears candidates; First Scan auto-ends orphan session),
  and the two banner-literal-text tests that lock the UX rule.
  C# total **935 → 957**.

### C# UI — new files

- `Models/ValueScanModels.cs` — `ValueScanDataType`, `ValueScanType`,
  `ValueCandidate`, `ValueScanBeginResult`, `ValueScanRefineResult`.
- `Services/DumpService.cs` + `Core/IDumpService.cs` — adds
  `BeginValueScanAsync`, `RefineValueScanAsync`, `EndValueScanAsync`
  + shared `ParseValueCandidate` JSON helper.
- `ViewModels/ValueSearchViewModel.cs` — DataType / ScanType selectors,
  Value/Value2 inputs (visibility-bound to scan type), First Scan /
  Next Scan / New Scan commands, NavigateToInstance event for
  cross-tab "Open in Live Walker".
- `Views/ValueSearchPanel.axaml` + `.axaml.cs` — top banner (warm-amber
  styled, locked by test), inputs row, status row, DataGrid of
  candidates with Class.Field / Type / Value / Offset / Addr / Instance
  columns + per-row Open / Copy buttons.
- `Views/MainWindow.axaml` — new tab between Interesting Props and
  Console (header `str.Tab.ValueSearch` = "Value Search").
- `ViewModels/MainWindowViewModel.cs` — wires `ValueSearch` child VM +
  navigation + clipboard events.
- `Resources/Strings/en.axaml` — 16 new string keys (banner + labels +
  tooltips).

### Workflow (golden path)

1. Open Value Search tab → see banner explicitly stating native-field
   limitation.
2. Pick DataType (e.g. Int32), ScanType=Exact, type the value you're
   looking for (e.g. current HP = 100), click **First Scan**.
3. DLL walks GObjects + matching-type UProperties, returns N candidates
   in seconds.
4. Take damage in-game (HP drops to 75), switch ScanType=Decreased,
   click **Next Scan** → candidates pruned to fields that dropped.
5. Repeat with Changed / Unchanged / Decreased until candidate count
   drops to a single-digit list.
6. Click **Open in Live Walker** on a candidate → cross-tab navigation
   opens the owning instance with the field highlighted. Or click
   **Copy Address** to send the address straight to CE.

### Open items (next session — see todo.md)

- Live-game verification on Geri (UE 4.27) + ES2 (UE 5.5): scan for
  HP, take damage, refine. End-to-end smoke test before declaring
  the feature stable on the broader 18-game corpus.
- v2 type expansion (FString first as the easiest; TArray gated by
  the crash-risk plan above).
- Optional UX polish: keyboard shortcut for First/Next Scan; "Add to
  Watch List" right-click action.

-----

## 2026-05-24 — Property freeze (Route B): horizontal lock across all class instances (build 719)

New end-to-end capability: given a property surfaced by **PropertySearch**, generate an AA Script that holds the value at a constant across **every live instance** of the owning class, with automatic instance re-enumeration on a timer so respawns / new spawns / destroys are handled transparently. Sister capability to the existing CE-XML pointer-chain export (Route A, kept in [todo.md → Speculative](todo.md)) — fundamental difference: CE XML pins ONE pointer chain to ONE instance; the freeze script tracks a property by **class + offset + type** and writes to every live instance every tick.

### Architecture

```
PropertySearch row
  → [Freeze] button (grayed when AOBMaker plugin not detected)
  → FreezeValueDialog (single input, type-validated)
  → FreezeScriptGenerator.Generate(...)
  → AOBMaker CreateAAScriptAsync — script lands in CE's address list

Generated AA Script [ENABLE]
  → findTableFile('ue5_freeze_helper.lua') → load() the helper
  → freezeProperty(cfg) → handle
  → handle.start() → createTimer(50ms tick) + createTimer(5s rescan)

Tick (every 50ms): for each cached instance addr → writer(addr + offset, value)
Rescan (every 5s): CMD_LIST_INSTANCES → refresh cache

[DISABLE]: handle.stop() → destroys both timers, clears cache
```

### DLL — CMD_LIST_INSTANCES = 6 ([Mimic.h](../dll/src/Mimic.h), [Mimic.cpp](../dll/src/Mimic.cpp))

New mailbox cmd that paginates **live (non-CDO) UObject* pointers** of a class. Match policy: `exactMatch=true` — partial matching would have `"Pawn"` pull every pawn subclass in the world and the property offset only makes sense for the exact class chain PropertySearch identified. Hard cap 2000 instances, 128 ptrs per page (8 bytes each = exactly 1024 bytes paramsData). Output mirrors the LIST_FUNCTIONS shape: `parmsSize=total`, `numParms=this page`, `functionFlags=total pages`.

Reuses Aura's existing `FindInstancesByClass`; CDO filter (`name contains "Default__"`) drops template objects.

### Lua — `scripts/ue5_freeze_helper.lua` (new, ~340 lines incl. 5 commented samples)

- Public API: `freezeProperty(cfg) → handle` with `handle.start()` / `handle.stop()`
- `cfg` fields: `className`, `propOffset`, `valueType`, `value`, `tickIntervalMs` (default 50), `refreshIntervalSec` (default 5), `filter` (optional `fn(addr) → bool`)
- Type writers cover bool + int8/uint8 + int16/uint16 + int32/uint32 + int64/uint64 + float + double (aliases: byte/sbyte/word/dword/qword/int/long/boolean)
- Shares `_ue5_invoke_busy` reentrancy flag with `ue5_invoke_helper.lua` — neither helper touches the mailbox while the other is mid-call
- Tick has a vtable-null liveness guard so a freed instance between rescans doesn't write to recycled memory
- 5 commented samples in the file header: basic teammate HP, god mode bool, filter-out-local-player, multi-property freeze in one script, how to edit CFG after generation

Bundled as an `<EmbeddedResource>` in `UE5DumpUI.csproj` so the UI can ship it to disk or inject it into the CE table via AOBMaker.

### C# — Models / Services / Views

| File | Purpose |
|---|---|
| `Models/FreezeScriptParams.cs` | DTO: ClassName, PropertyName, PropertyOffset, UeTypeName, ValueLiteral |
| `Services/FreezeScriptGenerator.cs` | Renders the AA Script; per-script keyed handle table (`_ue5_freeze_handles[KEY]`) so multiple Freeze scripts coexist without clobbering each other's globals |
| `Services/FreezeHelperLuaResource.cs` | Embedded-resource accessor (mirrors `HelperLuaResource`) |
| `Views/FreezeValueDialog.cs` | Single-input modal with read-only target details + type-aware validation (`ValidateAndConvert`); accepts bool as `true/false/1/0` (case insensitive) |
| `ViewModels/PropertySearchViewModel.cs` | New `CopyFreezeScriptCommand`, `IsAobMakerAvailable` flag + `FreezeUnavailableTooltip`, `RefreshAobMakerAvailabilityAsync` with 5s cooldown |
| `Views/PropertySearchPanel.axaml` + `.axaml.cs` | New **Freeze** button per row; `DataContextChanged` wires `FreezeValuePrompt` callback so the VM stays View-free |
| `ViewModels/MainWindowViewModel.cs` | New `InjectFreezeHelperLuaCommand` + `ExportFreezeHelperLuaCommand` mirroring the existing invoke-helper Tools entries |
| `Views/MainWindow.axaml` + `Resources/Strings/en.axaml` | Tools menu gains two entries with a `<Separator/>` from the invoke helper pair; 3 new strings + 3 new tooltips |

### Gating — no clipboard fallback, AOBMaker required

Decided per user request to keep the surface tight: the Freeze button is **disabled** when the AOBMaker bridge can't reach CE, with a tooltip explaining the setup. No copy-paste fallback path (would duplicate the helper-loader chrome and split the docs). The AOBMaker plugin's existing `CreateAAScriptAsync` (used by the Pipe Invoke / AA(Baked) flow since build 590) delivers the script directly into CE's address list.

### Per-script handle key (subtle correctness fix during dev)

Initial generator used a single global `_freezeHandle` — would have been clobbered by a second active Freeze script. Switched to `_ue5_freeze_handles[KEY]` table keyed by `ClassName::PropName@0xOffset`. Deterministic key so re-enabling the same script reuses the same slot; defensive stop in [ENABLE] catches the rare "AA Script reload while active" case.

### Tests — +47 tests (target file path includes both the helper resource sanity check and the wiring)

- `FreezeScriptGeneratorTests.cs` — type mapping (12 known + 6 unsupported), Lua escaping (6 cases), generated script section structure (5 facts incl. defining-class preference, hex offset render, helper resource read)
- `FreezeValueDialogValidationTests.cs` — bool / float / double / signed-int / unsigned-int / unsupported (20 cases via theory + 4 facts)
- `PropertySearchFreezeTests.cs` — 7 gating + happy-path scenarios + 1 tooltip test (no-bridge / unavailable / unsupported-type / cancel / happy / defining-class preferred / rejected / tooltip flag)

Final total: 920 C# xunit + 64 dll_helpers + 31 utf8_helpers = **1015 tests**, all green.

### What changed in MEMORY.md

Test count bumped 786 → 1015; tested-games / capability-matrix entries remain valid (freeze is additive, no regressions to existing flows).

### Not done in this round (Route A still on the table)

- **Live-game verification**: needs a UE 4.x or 5.x cooked game with a teammate-style property to confirm the rescan cadence + tick writer doesn't disturb gameplay. Smoke-tested unit-level; first live test should be a single-player game with respawning NPCs (e.g. Geri) where a respawn-induced cache refresh is observable.
- **Bitfield bool detection**: the helper writes a full byte for `bool`, which is wrong for packed bitfield bools (`uint8 bFoo : 1`). PropertySearch doesn't currently surface bitfield mask metadata so we can't gate the button accordingly — deferred until a user hits this.
- **FString / FName / struct field freeze**: out of v1 scope per user (numerics + bool first).
- **Route A polish**: existing CE-XML export already handles single-pointer-chain freeze; the [todo Speculative entry](todo.md) documents it as the "static singleton manager" option.

-----

## 2026-05-20 (PR #199 merged dev → main) — Mailbox poll 10ms→1ms + Invoke param picker Stage 1+2

Three-shipment session on top of the build-696 close-out. Total: 4 commits (build 707 → 715), all pushed to dev, then dev merged into main via PR #199 as a fast-forward (30 commits caught up — first dev→main merge since build 590).

### A. Mailbox poll latency cut (build 707-710, [74db6b5](https://github.com/bbfox0703/UE5CEDumper/commit/74db6b5))

CE Lua's `invokeUFunction` blocks on a status-flag flip driven by Mimic's polling thread. The historic `Sleep(10)` between iterations added ~5ms avg of pure idle wait per invoke — so a tight Lua loop of N invokes used to burn ~N×5ms in the polling loop alone. Lowered to `Sleep(kPollIntervalMs=1)` with a `timeBeginPeriod(1)` / `timeEndPeriod(1)` bracket so Sleep(1) reliably delivers ~1-2ms regardless of host timer state (legacy 15.6ms tick would otherwise defeat the win on idle/server SKUs). Win10 2004+ scopes timeBeginPeriod per-process; no global cost.

Added `Test_Mimic_PollLatency_OneMillisecond` to `dll_helpers_test`: brackets the same timeBeginPeriod pair, asserts 100×Sleep(1) lands under 300ms. Observed 188-194ms on the dev machine (~1.9ms/sleep) — 5× under the ~1560ms a legacy-tick regression would produce. `Winmm` linked into main DLL + both proxy DLLs + the test exe.

The Stark queue (game-thread FIFO with per-request promises) and the UE ProcessEvent throughput itself are unaffected — those are fundamental constraints. This change only kills the mailbox-side idle wait, which was the dominant latency layer for sequential CE-Lua-driven invokes.

### B. Invoke param Stage 1 — surface UObject* expected UClass (build 711, [024b6fd](https://github.com/bbfox0703/UE5CEDumper/commit/024b6fd))

Pain point: invoking a UFunction with a UObject*/UClass*/Soft*/Weak*/Lazy*/Interface parameter, the user had no idea what type was actually expected — the DLL had the info (`FObjectPropertyBase::PropertyClass`) but threw it away when walking function params. The InvokeParamDialog label just said `[UObject*, 8B]` with no class hint, leaving the user to guess or grep the SDK header.

This is Stage 1 of a 3-stage plan to make invoking pointer params tractable. Stage 2 (instance picker) and Stage 3 (class validation) build on the metadata exposed here.

- **DLL**: `Ubel.h::FunctionParam` gains `objClassName` field (mirrors `FieldInfo`). `Ubel.cpp::WalkFunctions` extracts `PropertyClass` for the 7 pointer-flavoured types on both UE5/4.25+ (via `ReadSubclassTypeName`) and UE4 <4.25 (via `UPROPERTY_OFFSET+0x2C` — same delta the StructProperty path uses, since both derived types put their first member at the same subclass slot). `Fern.cpp` walk_functions JSON adds optional `"obj_class"` key alongside `"struct_type"`.
- **C#**: `FunctionParamModel.ObjectClassName` (default "" for backward compat). `DumpService` parses `obj_class`. `InvokeParamDialog` + `InvokeScriptGenerator` labels become `[UObject*: AActor, 8B, off=0x10]` when the class is known, fall back cleanly to the original form when empty.
- **Tests**: 2 new (with-class / without-class label format).

### C. Invoke param Stage 2 — instance picker dialog (build 715, [515a344](https://github.com/bbfox0703/UE5CEDumper/commit/515a344))

When InvokeParamDialog renders a pointer-flavoured param, the row now grows three buttons after the textbox:

```
[param-name]  [type, classHint, NB]  [textbox]  [Pick…] [null] [self]
```

- **[Pick…]**: opens new `ObjectInstancePickerDialog` pre-filtered to the param's expected UClass (from Stage 1). Substring-match default catches subclasses (which is what an ObjectProperty actually accepts). Double-click row OR "Use selected" → textbox fills with chosen address. Cancel leaves textbox alone. Greyed when `ObjectClassName` is empty (older DLL or genuinely unconstrained param — user can still type address by hand).
- **[null]**: fills `0x0` for optional pointer params (WorldContextObject, etc.).
- **[self]**: fills invoke target's own address — for utility functions that re-target themselves. Disabled when no target instance (definition-only views).

Zero DLL change: picker reuses the build-547 `find_instances` pipe command (InstanceFinder has used it for nearly 200 builds). Picker dialog mirrors InvokeParamDialog's code-behind style — no XAML, no CompiledBinding, AOT-safe.

`ParamBufferBuilder.IsPickablePointerType` is the canonical list of the 7 pointer types — 7 positive + 14 negative test theories lock the DLL↔UI contract so a future type drift breaks at compile time.

### Files / counts

- New: `ui/UE5DumpUI/Views/ObjectInstancePickerDialog.cs` (260 lines)
- Modified: `dll/src/Ubel.h`, `dll/src/Ubel.cpp`, `dll/src/Fern.cpp`, `dll/src/Mimic.cpp`, `dll/CMakeLists.txt`, `dll/tests/dll_helpers_test.cpp`, `ui/UE5DumpUI/Models/FunctionInfoModel.cs`, `ui/UE5DumpUI/Services/DumpService.cs`, `ui/UE5DumpUI/Services/ParamBufferBuilder.cs`, `ui/UE5DumpUI/Services/InvokeScriptGenerator.cs`, `ui/UE5DumpUI/Views/InvokeParamDialog.cs`, `ui/UE5DumpUI.Tests/InvokeScriptTests.cs`, `ui/UE5DumpUI.Tests/ParamBufferBuilderTests.cs`

Tests: 910 → **935** (DLL self-tests 93 → 95 +mailbox latency; C# 817 → 840 +2 Stage-1 label tests +21 IsPickablePointerType theories). Build 715 / dist still 704 (no Publish rebuild this session).

### What's still open

- **Stage 3 (class validation)** — explicitly deferred to "real crash drives it" per Stage 2 close-out conversation. DLL would gain `validate_object_class(addr, expectedClassName)`; UI would warn (not block) on mismatch before invoking. Picker output is almost always class-correct in practice.
- **`walk_functions_batch`** — sister to `walk_class_batch`, still on the next-session bench.
- **FString / FText / TArray input for baked AA Script** — still open since build 643-644 ES2 verification.

-----

## 2026-05-20 (dev branch, docs only) — 18-game bias recheck (Frontiers added — first MMO/ARPG-flavoured dump)

User added one new dump (`Frontiers-Win64-Shipping.exe`, UE 4.26,
107,872 objects, 1,310 game classes / BPGCs) bringing the corpus to
**18 games**. Tool / workflow unchanged from the 17-game refresh
([4f50ea0](https://github.com/bbfox0703/UE5CEDumper/commit/4f50ea0)):
same `python scripts/analysis/analyze_dumps.py work/dump/*.jsonl
--min-games 3` run, same ad-hoc drill-down snippets reusing the
analyzer's `load_dump` / `tokenize` / `_resolve_own_props` helpers.

### Genre signature: predicted "out-of-genre" target

Characteristic class names (`TL_*` asset prefix, `BossMonster*`,
`BossFightObserver*`, `Pet*`, `Affix*`, `Dungeon*`, `Sharpshooter`,
`Captain`, `Cursed` archetypes) point to a Korean-MMO / ARPG-style
title — exactly the genre family
[docs/todo.md → More dumps for genre coverage](todo.md) flagged as
the only kind that could move the calibration needle further. The
17-game corpus was heavy on JRPG / sim / action-adventure / sandbox /
racing; this is the first MMO/ARPG-flavoured entry.

### Bias verdict — tables still stable

Token-by-token drill-down on every candidate with ≥3-game support:

- **`skill`** (8 / 18 games, 90 classes) looked promising at first but
  per-name inspection showed ~85% UI-widget noise: `SkillList`,
  `SkillIcon`, `Txt_SkillName`, `Img_SkillIcon`, `Pnl_SkillName_Mask`,
  `Hrz_SkillName`. Genuine cheat-tunable hits
  (`CurrentSkillPoints`, `SkillPointsRequired`, `IsSkillPurchased`)
  are buried under ~15% of the surface. Adding `Skill` to
  PropertyScoringTable would over-fire on UI properties. Function-side
  `Skill` keyword in CombatKeywords remains correct since function
  names like `UseSkill` / `LearnSkill` are unambiguously action verbs.
- **`effects`** (7 / 18), **`aura`** (4 / 18), **`gameplay`** (4 / 18),
  **`requirements`** (3 / 18), **`expiration`** (3 / 18), **`tags`**
  (5 / 18) — all >95% TQ2-skewed (TQ2 contributes 477-905 of each
  token's hit count; other games combined are single-to-low-
  double-digit). Single-game spikes, not cross-game signal.
- **Frontiers-unique tokens** (`affix`, `pet`, `dungeon`, `captain`,
  `sharpshooter`, `cursed`) all concentrated in Frontiers alone. Same
  single-game-spike rejection rule as the 17-game pass.
- **Class-side candidates** (Frontiers top class-x-prop pairs) are all
  UI-flavoured (`credits→credit`, `widget→item`, `bar→resource`,
  `dungeon→level`); none generalise across the corpus.

**No keyword additions, no class-rule additions, no scoring weight
changes.** Second consecutive bias recheck confirming the build-678 /
687 calibration generalises to genre-adjacent AND genuinely out-of-
genre unseen titles.

### Why this is stronger evidence than the 17-game pass

The 17-game recheck added two same-family titles (Star Wars Jedi:
Fallen Order — EA action-adventure; Ghostwire: Tokyo — Tango action-
horror). Both reinforced existing patterns, but the prediction was
that they wouldn't move the needle because they sit in already-well-
represented genres.

This 18-game pass added a predicted-to-be-different-genre title
(MMO-flavoured ARPG, never previously represented). The prediction was
that out-of-genre dumps would surface new vocabulary. The data says
the build-678 / 687 calibration **also covers MMO/ARPG vocabulary**
without any new keyword. That's stronger evidence for table robustness
than two more same-genre dumps would have been.

### Genres still completely absent from the corpus

MMO/ARPG is now (partially) represented. Still missing:

- Pure horror (no Resident Evil / Silent Hill style — only action-horror
  hybrids like GWT / Hogwarts dark sequences)
- Fighting (Tekken / Street Fighter / Mortal Kombat)
- RTS (Age of Empires / StarCraft / Company of Heroes)
- Sports-sim (FIFA / NBA 2K / car-tuning sims)

A dump from any of these would test calibration against vocabulary
genuinely outside the current action-adventure / RPG / sim / shooter /
ARPG neighbourhood.

### Files touched

Docs only. No code, no tests, no scoring tables. The Frontiers dump +
regenerated `work/dump/analysis-report-18games.md` live under `work/`
which is gitignored.

-----

## Older entries (builds 547-696) → archive

Pre-build-700 milestones (2026-05-09 → 2026-05-12) are in
[archive/dev-log-2026-05-pre-build-700.md](archive/dev-log-2026-05-pre-build-700.md)
— grep `^## ` there for the older index.
