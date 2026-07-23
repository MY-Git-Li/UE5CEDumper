# UE5CEDumper
<img src="./img/UE5CEDumper.jpg" alt="UE5CEDumper"/>  

**The Live Bridge between Unreal Engine Runtime and Cheat Engine.**

UE5CEDumper is a interactive inspector toolchain. It provides a **live window** into the game's memory, allowing you to browse objects, find instances, and export CE-ready structures in real-time.

> It's built for the *active* table maker. It bridges the gap between seeing an offset and actually using it in Cheat Engine.

> *UE5CEDumper is not meant to be a another dumper that extracts large amounts of data for analysis. Instead, it focuses on quickly finding UE structures and integrating with CE for live development. Think of it as a general-purpose UE tool rather than a specialized dumper.*

> ### Scope of use
>
> **Windows x64, single-player / offline only.** This is an inspection and debugging tool for games
> you legally own, on your own machine. Do not use it in multiplayer, competitive or online modes —
> besides being unfair, that is where anti-cheat, account bans and legal exposure actually live.
> UE5CEDumper reads the memory of a running process; it does not redistribute any game code, assets
> or keys, and it does not touch pak/IoStore container encryption.

---
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D4)](https://www.microsoft.com/windows)
[![UE Version](https://img.shields.io/badge/UE-4.18--5.8-orange)](https://www.unrealengine.com/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![C++](https://img.shields.io/badge/C++-23-00599C)](https://isocpp.org/)
[![Avalonia](https://img.shields.io/badge/Avalonia-UI-8B5CF6)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-CC785C?logo=claude)](https://claude.ai/code)

## Sample screenshots
<img src="./img/MainUI.gif" alt="Live Walker"/>  
<img src="./img/Value_Search.webp" alt="Value Search"/>  

## Highlights

A quick look at what you can actually *do* with it (the [full table-maker feature list](#features-for-table-makers) is further down):

- **Live memory inspection** — browse the game's objects, find every instance of a class, and drill into struct / class layouts with real-time values.
- **Value Search** — Cheat-Engine-style First Scan / Next Scan over every UE property (numbers, strings, vectors, arrays / maps / sets), so you find what to cheat without knowing offsets. A **Group mode** finds the object that holds *several* values at once (e.g. `Str + Def + Dex + Int` in one stats block) — far more selective than one value — and an opt-in **Deep** mode reaches values buried inside deeply-nested containers.
- **Teleport** — save your position to 3 markers and recall later (BugItGo-style), or **teleport to the mouse cursor** in top-down / 2.5D games. Bind your own global hotkeys. Also reads the live **camera POV** (location / rotation / FOV) so you can see whether a game's camera follows the pawn or is driven independently.¹
- **Player tuning** — toggle **Super Jump**, **Move Speed**, and **Gravity** with 10%–3000% sliders, plus **God Mode** (damage immunity) and **Time Dilation** (global or player-only slow-mo / freeze / fast-forward) — all forced by reflection and held against the game's per-tick overwrites, so they survive respawns. Bind global hotkeys, or export them as Cheat Engine on/off records.
- **Debug Camera** — force the free-fly debug camera on/off in games that ship it (reliable on/off even on Shipping builds that normally get stuck).
- **Console** — discover and one-click invoke the `fly` / `god` / `ghost` / game-specific cheat commands that many games leave in.
- **Live function profiler** — record while you perform *one* in-game action (open a shop, dash) and see exactly which UFunctions the game dispatched, ranked by call count with baseline-diff and noise filters. Finds the function behaviour-first, when name search can't.
- **One-click Cheat Engine export** — pointer-chain XML, Structure Dissect (CSX), SDK headers, AA scripts, and multi-row `.CT` batches.
- **Dump Explorer** — browse an exported "Dump All" `.jsonl` offline with one keyword search across classes + properties + functions, split into ✅ *in the current game* (jump straight to the Live Walker) vs ⚠ *not in this game*.
- **No Cheat Engine needed to inject** — drop the `version.dll` **proxy DLL** next to the game, or inject into an already-running game from the UI (**Inject into running game…**) or the **`inject-ue.ps1`** command-line tool (auto-elevates for games running as admin). The Proxy Deploy tab remembers what worked and **suggests the right proxy per game** (confirmed-working > last deployed > injection known-good).

> ¹ Marker save/recall works broadly (including Titan Quest II, after a transform-refresh fix for engine setters that report success but don't move the actor). A few heavily-stripped Shipping builds (e.g. Titan Quest II) can't do *cursor* teleport because they remove the standard cursor / viewport / line-trace APIs and use a custom virtual cursor — see [docs/teleport-spec.md](docs/teleport-spec.md) for the details.

## Tested Version Matrix

| UE Version | GObjects | GNames | DynOff | Verified Games |
|---|:---:|:---:|:---:|---|
| **4.15 – 4.17** | ✅ | ✅ | ✅ | *Extinction* |
| **4.18 – 4.20** | ✅ | ✅ | ✅ | *FF7 Remake Intergrade, The Occupation* |
| **4.21 – 4.24** | ✅ | ✅ | ✅ | *Star Wars Jedi, DQ XI S, IDOLM@STER STARLIT SEASON, Octopath Traveler, DQ I&II / III HD-2D Remaster* |
| **4.25 – 4.27** | ✅ | ✅ | ✅ | *FF7 Rebirth, Stellar Blade (劍星), Tower of Mask, Hogwarts Legacy, Romancing SaGa 2 RotS, Ghostwire: Tokyo, TimeSplitters Rewind (Early Access V0.3.3), The Artisan of Glimmith, Barn Finders, MOBILE SUIT GUNDAM SEED Battle Destiny Remastered, Persona 3 Reload (女神異聞錄３ Reload — PE stripped → tool defaults 504; UE5-Extended + TaggedFFieldVariant on a 4.27-slot ProcessEvent; game idles its thread when unfocused)* |
| **5.0 – 5.2** | ✅ | ✅ | ✅ | *Squirrel With A Gun, Caravan Sandwitch, Meltopia, Retro Rewind Demo* |
| **5.3 – 5.4** | ✅ | ✅ | ✅ | *Satisfactory (v1.1.3.1), Colossal, Avowed (Obsidian — packed 20-byte FUObjectItem; static-struct GObjects AOB), Echoes of Aincrad Demo (Bandai Namco — dxgi.dll proxy), The Adventures of Elliot: The Millennium Tales (stripped PE → detected 4.27, runtime-reconciled to UE5.4), MindsEye (Build A Rocket Boy — licensee fork: reordered `FChunkedFixedUObjectArray`, 32-byte `FUObjectItem` with `UObject*` at +0x10, and XOR-obfuscated `FNameEntry` payloads decoded from the fork's own key table; **verified on game v7.3.1 only** — every RVA is build-specific, see [mindseye-fork-notes.md](docs/mindseye-fork-notes.md))* |
| **5.5 – 5.7** | ✅ | ✅* | ✅** | *Titan Quest II, EverSpace 2, Lushfoil Photography Sim, Manor Lords, Cat Island Petrichor Demo, Way of the Hunter 2 Demo, COMBAT PILOT: CARRIER QUALIFICATION Demo, Solarpunk (stock UE5.7 — Object@+0x08), Pionero Capital Demo (stock UE5.7 — Object@+0x08, dxgi.dll proxy, GWorld direct), Satisfactory (v1.2.3.1 — UE5.6; modular build, all globals via symbol export, 217 K objects), Star Trek Voyager – Across the Unknown (stock UE5.6 — dxgi.dll proxy, GWorld direct, 171 K objects)* |

*\*GNames uses .data pointer-scan fallback for 5.5+*
*\**DynOff supports **CasePreservingName (FName = 16 bytes)** layout.*

---

## Features for Table Makers

| Feature | Description |
|---|---|
| **AOB Scanning** | 128 AOB patterns + 5 symbol exports from 14+ sources. Supports `FChunkedFixed` and `FFixed` object arrays. Encrypted GObjects + preset layout detection. **Scan Hint Cache** remembers winning patterns per game for 3× faster repeat scans, with UI buttons to clear/reset the cache. |
| **DynOff (Dynamic Offsets)** | Runtime probing of `FField`, `FProperty`, and `UStruct`. No more manual offset updating after game patches. |
| **Live Walker** | Inspect instance memory data with real-time value updates. Navigate into pointers (→), inline structs ({}), and containers ([]). Breadcrumb trail for full navigation history. Auto-refresh with configurable interval. |
| **Definition View** | Drill into UScriptStruct/UClass definitions to see field layouts, types, and live hex values. Works seamlessly with CE XML export for pointer chain navigation. |
| **Container Drill-Down** | Drill into **Array**, **Map**, **Set**, **Optional**, and delegate containers. Resolves Object/Class/Weak/Soft/Lazy/Interface/Delegate/MulticastDelegate inner types with click-through navigation to the target. |
| **Address Finder** | Paste a CE address into Instance Finder → exact / contains / backward / nearest tiered match (with `match_kind` confidence labelling), plus container-aware scan that walks every reflected `TArray` / `TMap` / `TSet` (including nested in `StructProperty`, depth 3) to find the owning UObject. |
| **Find Refs (Reverse Reference)** | "Who holds a pointer to this object?" — walks every UObject's pointer-shaped fields and containers, covering direct `Object/Class/Interface`, `Weak/Soft/Lazy`, `OptionalProperty<pointer/Struct>`, `Delegate` / `MulticastInline/MulticastDelegate` (per-binding `FWeakObjectPtr`), `TArray` of all of those, and `TMap` / `TSet` with UObject sides. Per-class metadata cache makes warm scans ~70 ms even on 1.18 M-object games. **Open** on a container element hit auto-drills into `[N]` so you land directly on the array/map/set entry that held the pointer (no extra click). |
| **UFunction Invoker** | Call game functions via `ProcessEvent` directly from the UI. Game-thread safe dispatch via MinHook hook. Supports ScriptStruct parameters — 12 known engine structs (FVector, FRotator, FColor, etc.) + dynamic struct layout discovery. **Structured return DataGrid** (build 775) shows FVector / FRotator / FHitResult / user USTRUCT returns as a Field / Type / Value / Offset grid below the existing single-line decode. **Param picker** for pointer params surfaces the expected UClass label (`[UObject*: AActor, 8B]`) + `[Pick…] [null] [self]` quick-fill buttons. **FString INPUT params** (build ~1911) — the DLL builds real FStrings for string parameters (PIPE FIRE and the CE Lua generators both support them; OUT-string params are skipped for crash safety), and generated CE invoke scripts print the function's return value when `UE5_DEBUG=1`. |
| **Live Funcs — ProcessEvent call profiler** *(builds 2109-2130)* | Behaviour-based UFunction discovery — the thing name scoring fundamentally can't do. **Start → perform ONE in-game action (open shop, dash, attack) → Stop** → a ranked list of every UFunction the game actually dispatched in the window (the action's function sits near the top with a low count; per-frame Tick noise has huge counts). **Baseline diff** (record idle first, then only NEW / increased rows show), **Hide UI widgets** / **Hide events & delegates** filters, and a first-fired **Order** column ("Earliest first") float the action's true entry point — it runs *before* the reactions it triggers. Hot path is free when not recording (one atomic gate on the ProcessEvent hook). Per-row handoffs to Live Walker / clipboard. **Cadence flag** *(build 2156)* — a Welford inter-arrival stat (fed the timestamp the hook already reads, so zero extra cost) flags functions firing at a regular timer cadence: a **Periodic only** filter + **Timer** badge + **Period** column, so an *idle*-window recording (the inverse of the action diff) surfaces the callback driving a cooldown / respawn / damage-over-time (verified on Elliot: a `~325 ms` Blueprint timer picked out of ~90 functions, per-frame Tick excluded). Limit by design: purely-native C++ calls (incl. native `SetTimer` lambdas) never cross ProcessEvent — reflected / Blueprint gameplay is the target. |
| **Value Search — by-value scan** *(build 738 + Phase 2 757 + V1a 927)* | CE-style **First Scan / Next Scan** workflow over every UPROPERTY-declared field. **Numeric primitives** (Int8/16/32/64 + UInt + Float / Double / Bool) with Exact / Bigger / Smaller / Between / Changed / Unchanged / Increased / Decreased + CE-style rounded float tolerance. **Multi-numeric meta** — `NumericNoByte` / `NumericAll` scan every numeric width in one pass, comparing by each field's own declared type, so you don't need to know up front whether the value is stored as int or float (no byte-reinterpret false hits). **Strings** (FString / FName / FText) with Contains / StartsWith / EndsWith and optional case-sensitive matching. **Vectors** (FVector / FRotator) with `"X,Y,Z"` CSV input and per-axis tolerance. **Containers** — `TArray<T>`, plus **`TSet<T>` and `TMap<K,V>` key/value**, are scanned element-wise (rows render `Field[N]` / `Set[N]` / `Map.Key[N]` / `Map.Value[N]`) with a 10 M-element soft circuit-breaker. A case-insensitive **keyword filter** narrows the result grid and a **Cancel** button abandons an in-flight scan. Native C++ fields (non-UPROPERTY) are explicitly out of scope — banner directs users to CE's raw memory scan. Sessions auto-expire after 5 min. |
| **Value Search — multiple values group scan** *(build 1276; Deep 1283)* | Object-aware analogue of Cheat Engine's **Group Scan**: enter **2–4 values** and it finds the objects that hold *all* of them at distinct numeric-property offsets, in any order (e.g. `Str + Def + Dex + Int` in one stats object). Matching several values at once is multiplicatively more selective than one value, so a handful of hits replace thousands. Results are **master-detail** — one row per object, expand to the located per-slot fields (with a 🔒 once an offset locks across refines) — and each slot reuses the same Open-in-Live-Walker / Locate-in-GWorld / Copy handoffs as a single-value hit. An opt-in **Deep (nested containers)** toggle (on both Single and Group) additionally walks deeply-nested containers — struct arrays, struct-valued maps, nested `TArray`/`TSet` — so a group hidden inside e.g. `SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]` is found, matching *within* one array (each numeric array / struct-array element is its own match block). Built on the source-agnostic `Orden` matcher, so Snapshot / SPC Query / Class Pivot can reuse the same group-matching engine later. |
| **Property Freeze (horizontal class-wide)** *(build 719)* | One-click freeze that locks a property at a constant value across **every live instance** of the owning class — handles respawns / new spawns / destroys automatically via a 5 s rescan timer. Ships an AA Script that depends on `ue5_freeze_helper.lua` (auto-injected into the open CE table via the AOBMaker plugin). Supports BoolProperty / ByteProperty / Int8-64Property / UInt16-64Property / EnumProperty / FloatProperty / DoubleProperty. Multiple freeze scripts coexist via per-script keyed handle table. |
| **Interesting Funcs + Properties finders** *(build 597 → 687)* | Two scoring-driven discovery tabs that rank UFunctions and properties by name + class + flag heuristics — Stats / Inventory / Movement / Combat / Utility categories with class bonuses for Character/Pawn/PlayerController etc. CamelCase tokeniser means short acronyms (HP/MP/SP/XP/TP) match safely. **Unusual Location** flag highlights cheat-relevant properties sitting in non-conventional containers (LocalPlayer / GameViewportClient / HUD / CheatManager). Calibrated against a 18-game cross-game dump corpus via the offline analyzer at [`scripts/analysis/`](scripts/analysis/). Opt-in **Gameplay Actions** keyword pack (build 2103) adds the character-control / interaction / shop verbs (`Dash` / `Dodge` / `Interact` / `Open` / `Buy` / `Sell` / `Shop`…) the cheat-value buckets deliberately ignore, and a **BP/Exec only** display filter keeps just the callable entry points. |
| **Console — UFUNCTION(exec) discovery + invoke** *(build 731 + UCheatManager hint 778)* | Surface every UFUNCTION(exec) reachable in the game and provide one-click invoke. Many cooked Shipping titles ship `fly` / `ghost` / `god` / `setspeed` / game-specific exec entry points untouched — the game author already curated the cheat surface for us. **UCheatManager hint** warns when the selected exec's class inherits from `UCheatManager` — those are commonly body-stripped by Epic via `#if !UE_BUILD_SHIPPING` (invoke returns OK + no in-game effect); the hint redirects users to a game-specific exec for verification. |
| **Teleport (BugIt-style)** *(build 1027 → 1112)* | Save the player position to **3 marker slots** and recall later, or **teleport to the mouse cursor** for top-down / 2.5D games (with a screen-center fallback for cursorless titles). Resolves the local pawn entirely by reflection (no per-game offsets) and moves it by invoking the engine's own `K2_SetActorLocation` / `K2_TeleportTo` / component-level `K2_SetWorldLocation` on the game thread; raw write is a last-resort fallback. **BugItGo interop** — copy the current pose as a `BugItGo X Y Z` string, or paste one (incl. the full `?BugLoc=…?BugRot=…` form) and run it. **Read-only camera POV** *(build 1110-1112)* — reads the on-screen camera's location / rotation / FOV (distinct from the pawn pose) so you can tell whether a game's camera follows the pawn or is independent; uses the engine's `APlayerCameraManager` getters with a fully-reflected `CameraCachePrivate.POV` cache fallback for titles where the getters don't respond (verified on SEED, DQ III HD-2D, Titan Quest II, Octopath). **User-set global hotkeys** for Save/Recall (press-to-set, persisted) plus an auto-bound cursor hotkey, so you can trigger teleports with the game focused. Also exports the actions as momentary CE records / `.CT`. All logic is DLL-side via a shared-memory mailbox, so the generated CE records run with just the DLL injected. **Global Pointers card** *(build 1978)* publishes the live **GWorld / GameEngine** addresses as registered CE symbols — `UE_GWorld` binds the static GWorld slot itself so it auto-follows level changes; `UE_GameEngine` snapshots the UEngine pointer — delivered via AOBMaker push or a paste-able CE-XML clipboard fallback. |
| **Movement tuning + God Mode + Time Dilation** *(Solitar / Laufen / Hemmung, build 1251 → 2158)* | Force the player pawn's `UCharacterMovementComponent` floats to a multiplier of their captured base, held by a re-assert worker so they survive respawns and sprint / ability-system recomputation: **Move Speed** (`MaxWalkSpeed`), **Gravity** (`GravityScale`, <100% floaty / >100% heavy), and **Super Jump** (`JumpZVelocity` — the slider is jump *height* 10%–3000%, applied as ×√height since apex height ∝ v²). Plus **God Mode** (force `AActor::bCanBeDamaged`). All resolved by reflection (no per-game offsets), with per-knob **Locate-in-GWorld**, global toggle hotkeys, and CE Lua / `.CT` export via the `CMD_MOVEMENT` mailbox (single "set %" call, **100% = off**). Only normal walk speed is engine-standard — sprint / dash are game-specific (find them in Property Search). Single-player only (these replicate server-side online). **Time Dilation** *(Hemmung, build 2148 → 2158)* is the absolute-value sibling: it holds Unreal's reflected dilation floats — global `AWorldSettings::TimeDilation` (whole-world slow-mo / freeze / fast-forward) or per-pawn `AActor::CustomTimeDilation` — with a 0–3× slider + presets (Freeze/¼×/½×/1×/2×), a "Player only" toggle, live read-back + disk persistence, and CE Lua / `.CT` export via the `CMD_TIME` mailbox. Verified on Avowed, The Adventures of Elliot, etc. |
| **Debug Camera force on/off** *(build 1014)* | Reliable free-fly debug camera toggle, even on Shipping builds that strip `DisableDebugCamera` and otherwise get stuck in free-fly. Reads the live state from the CheatManager and, when the game's own toggle can't disable, switches the local player's controller back to the original PlayerController by hand. Version-agnostic (all offsets reflected). Shares the same DLL mailbox path, so it works from the UI **or** a self-contained CE script. |
| **Experimental extras** *(opt-in, marked in the UI)* | A set of features gated behind an experimental opt-in, off by default: **Keep Foreground** *(build 1955)* — keep a game running while backgrounded by reporting the game's own window as foreground + rewriting the window-activation messages (defeats `t.IdleWhenNotForeground`-style pauses; also releases the mouse-cursor confinement while backgrounded; live-verified on Persona 3 Reload). **Fly / Noclip** — engine `MOVE_Flying` with VIEW-relative velocity drive (works even on games that zero `Velocity` per tick); Noclip additionally disables collision. **See-through occluders** *(builds 2006-2011)* — hide the **N** nearest non-Pawn world meshes blocking the camera's view (enemies / NPCs / the player stay visible; hidden actors restore as the view moves; verified Tower of Mask + DQ7R; no-op on collision/render-split games like FF7R). **Detect Stats** *(builds 2014-2026)* — one-button HP/MP/Gold candidate suggestion fusing name scoring with structural metadata (GAS `GameplayAttributeData`, `SaveGame`/`BlueprintVisible` property flags, Current/Max pairing) plus live-instance confirmation and an optional snapshot Δ signal (reference-only accuracy, by design; verified TQ2/ES2/SEED). **Standalone CE Lua trainer** — export teleport / god-mode / movement as an all-Lua CE table that runs without the DLL. **Force field + Stealth Meter** *(Solide, build 2168)* — hold a *discovered* reflected field (bool ON/OFF / `ObjectProperty` → null / numeric → absolute) across all live instances of a class via a write-on-drift worker (the honest subset of "enemies can't detect you" — Unreal has no universal detection bool; you discover the field in Property Search); a **Stealth Meter** card auto-finds and zeroes the player's visibility / noise / detection meter. Verified in-game (Elliot). |
| **Multi-row → One .CT batch generator** *(build 760)* | Select N rows in Interesting Funcs or Interesting Properties with Ctrl/Shift+click, click the 📦 **Generate CT** button → save one ready-to-share `.CT` file containing per-row AA Script entries grouped by category. Property rows become Freeze entries; function rows become Baked Invoke entries. XML-escapes all special chars so `TArray<int>` / `&` / quotes in descriptions can't break a CT load. |
| **Game-patch diff (`diff_dumps.py`)** *(2026-05-27)* | Offline pure-Python tool that compares two `Dump All Metadata` JSONL exports from the same game across patches. Surfaces **moved fields** (same name, different offset / size), type changes, function signature changes, and added / removed classes / properties / functions — the silent killers of working cheat tables. `--minimal` for breaking-changes-only output. Lives at [`scripts/analysis/diff_dumps.py`](scripts/analysis/diff_dumps.py). |
| **Dump Explorer (offline)** *(build 2044)* | Browse an exported **Dump All** `.jsonl` with ONE keyword search over classes + properties + functions at once (space = AND, All/Class/Prop/Func category filter) — no more picking a category and switching tabs first. Results split into ✅ **In current game** (the owning class resolves live — matched by class name, so it survives a game restart — with 🡒 **Jump** to the class in the Live Walker and a 🔍 **Instances** bridge into Instance Finder) vs ⚠ **Not in current game** (read-only metadata reference). ⤓ **Last export** one-click-loads the most recent in-session Dump All. Parsing is fully offline; only the live matching needs a connected game. |
| **Object Tree — global instance search** *(builds 2092-2098)* | The whole-pool object browser doubles as a **global live-instance keyword search**. An **Instances only** toggle hides the entire reflection/type layer (classes, functions, script structs, enums, packages — plus UE4's `…Property` descriptor objects) so only real live objects remain; the bottom filter scans the **whole loaded pool** (the display cap limits rows shown, never the match count). The top **Search** runs server-side — space = AND over object name OR class name, honours Instances-only, and reports truncation honestly instead of silently capping. Right-click any hit to act on *that exact object*: **Open in Live Walker**, **Show Related Objects**, **Locate in GWorld / GameEngine**, or Find Instances of its class. |
| **Instance Finder** | Locate all active objects of a class instantly. One-click to open in Live Walker. **Class-noise filter** *(build 1444)* — the picker lists the matched classes by count (with safe engine/system **auto-detect** hints); ticking a class re-runs the scan in the game and **drops it server-side before the result cap**, so an instance that was buried past the cap (e.g. behind thousands of `StaticMeshActor`) surfaces — untick or **Clear** to restore. Configurable **Max** results (100–50000) and a temporary **keyword filter** (Name / Class / Address, space = AND) over the returned rows. |
| **Property Search** | Search across all classes for specific property names. Find where a field is defined without browsing the full object hierarchy. **Freeze** button per row (build 719) ships a CE AA Script that locks the field across every live instance. A right-click **Force field** submenu *(experimental, build 2168)* additionally holds a discovered bool (ON/OFF) / strong `ObjectProperty` (→ null) / numeric field across every live instance via an **in-app DLL hold** (no CE plugin needed, re-asserted against per-tick overwrites), with a **"Forced fields (N held)"** honesty strip. |
| **Class Structure** | Browse class definitions with full field layouts, types, offsets, and inheritance chains. |
| **Multi-Format CE Export** | **CE XML** (pointer chain address records), **CSX** (CE Structure Dissect), **SDK Header** (.h), **CE Field** (single field), **CE AA Script** (Auto Assembler). Supports scalars, bitfields, structs, arrays, Map/Set, OptionalProperty\<Struct\>, and UEnum DropDownLists. **N-level pointer drill-down** (toolbar Drill Depth slider 0–4) — Copy CE Field on an `ObjectProperty` follows the pointer and emits the target's UPROPERTYs as nested `Offsets=[0]` children, so a single paste in CE shows the whole referenced object's layout. **Soft array per-element** emits FName leaf at FSoftObjectPath sub-offset (UE4/5.0 single FName; UE5.1+ FTopLevelAssetPath PackageName + AssetName) instead of a flat 8B hex. **Collapse chain** (toolbar toggle) optionally folds the entire `GWorld → … → target` pointer spine into one CE multi-level-pointer entry — `base` + a single merged node + the target field with its drill-down — so a deep navigation path pastes as a compact multi-level pointer instead of N nested groups. **String Len** toolbar slider *(build 2028)* sets the CE `<Length>` of every exported string leaf (16–4096, default 256), and a **Fabricate** slider *(builds 2032-2038)* pads a selected top-level `TArray` on Copy CE Field with template rows past its current `Num` (off / 2–4096; object, scalar, and struct arrays) — ready-made slots for items the current save hasn't populated yet. Copy CE XML / Copy CE Field / Export CSX are all **cancellable** mid-flight *(builds 1974-1977)*. |
| **Proxy DLL** | Proxy-DLL injection — no Cheat Engine required. Place next to the game exe, launch, and connect the UI. Three hijack targets cover the field: **`version.dll`** (broadest default), **`dinput8.dll`** (gamepad/DirectInput titles), and **`dxgi.dll`** (every D3D11/D3D12 UE game — the reliable target for EXEs that import *neither* version nor dinput8, e.g. some SQUARE ENIX builds). The Proxy Deploy tab scans Steam **or any drive you point it at** (Epic / GOG / manual installs), deploys the chosen type per game, and warns only when two of our proxies actually coexist. A per-game **Suggested proxy** column *(builds 2134-2142)* remembers what actually worked — a proxy the injected DLL itself attributed and that survived a 20 s stability gate (**confirmed working**, the strongest signal) > your last deployed pick > injection known-good > the version default — so an uninstall/reinstall doesn't send you back to guessing. The **Inject into running game…** picker also flags processes that already have the dumper loaded. |
| **Modern UI** | External **Avalonia UI** connected via high-speed Named Pipe (~87 JSON commands). Sortable result grids throughout. Native AOT published with zero IL2026 / IL3050 warnings. **Every keyword/filter box behaves the same** *(build 2088)*: space = AND across terms, and each box remembers the keywords that produced matches (autocomplete history). **Long operations cancel cleanly** — they stop when you cancel or close the UI, so the injected DLL never spins idle and the game always closes promptly. Keep your game performance smooth. |

---

## Architecture & Workflow

### Option A: Cheat Engine DLL injection

1.  **Inject DLL**: Run Cheat Engine, attach game process, load a save. Make sure game data is loaded first. Open `UE5CEDumper.CT`.
2.  **Enable Script**: Enable `init <== enable after process attached`, then `Inject DLL + Start Pipe Server`. The DLL locates global engine pointers and detects the UE version/layout automatically.
3.  **Connect UI**: Wait a few seconds for the scan to finish. Launch **UE5DumpUI.exe** and click **Connect**. Live data streams to the UI via Named Pipes (JSON-RPC).
4.  **Navigate & Analyze**: Browse the `UObject` hierarchy, find a class, drill into containers, or paste an address from CE to reverse-lookup and export.

### Option B: Proxy DLL (Recommended)

1.  **Place DLL**: Copy `version.dll` (from `build.ps1 -Target ProxyDLL`) into the game's root folder (next to the `.exe`).
2.  **Launch Game**: Start the game normally. The proxy DLL loads automatically and starts the pipe server.
3.  **Load a Save**: Reach the main game world so UE objects are populated in memory.
4.  **Connect + Scan**: Launch **UE5DumpUI.exe**, click **Connect**, then click **Start Scan**. The DLL performs the AOB scan and returns engine data to the UI.
5.  **Navigate & Analyze**: Same workflow as Option A — browse objects, find instances, export CE structures.

> **Note**: Do not use both methods simultaneously. If the proxy DLL is in the game folder, do not also inject `UE5Dumper.dll` via CE. The DLL detects duplicate instances and skips auto-start to prevent conflicts.

> **Which proxy DLL?** Start with `version.dll` (`build.ps1 -Target ProxyDLL`). If the game launches but the UI can't connect, the EXE probably doesn't import `version.dll` — try **`dxgi.dll`** (`-Target ProxyDxgi`), which every D3D11/D3D12 UE game imports, or `dinput8.dll` (`-Target ProxyDinput8`). `build.ps1` (default `-Target All`) builds all three into `dist\proxy\`, and the **Proxy Deploy** tab lets you pick the type per game and deploy it for you (Steam games, or any drive you point it at — Epic / GOG / manual). Once a game has worked before, the grid's **Suggested proxy** column tells you which one to pick.

### Option C: Inject into a running game (no CE, no restart)

Inject `UE5Dumper.dll` into an **already-running** game — the quickest path (no Cheat Engine, no pre-deployed proxy, no game restart). Two front-ends, one technique (`CreateRemoteThread` + `LoadLibraryW`):

- **From the UI**: Proxy Deploy tab → **Inject into running game…** → pick the game in the process picker → **Inject**. The UI auto-connects. If the game runs as Administrator you get a UAC prompt to inject elevated — no manual restart.
- **From the command line** — `inject-ue.ps1` (ships in `dist\` next to `UE5Dumper.dll`):

  ```powershell
  .\inject-ue.ps1                 # auto: inject the single running UE game
  .\inject-ue.ps1 -List           # list detected UE games
  .\inject-ue.ps1 -ProcessId 1234 # inject a specific PID
  ```

  Then launch **UE5DumpUI.exe** and **Connect**. On Access-Denied (an elevated game) the script auto-relaunches itself elevated (one UAC prompt).

> **x64 games only.** See the scope note at the top of this README — like all injection, `CreateRemoteThread` may be flagged by anti-virus and is blocked/banned by kernel anti-cheat (EAC / BattlEye).

| **Game Process (Injected)** |
| :---: |
| DLL + CE Lua Bridge (or Proxy DLL) |
| ⬇️ |
| **Named Pipe IPC (JSON-RPC Protocol)** |
| ⬇️ |
| **External GUI (Avalonia UI App)** |

---

### Optional: integration with AOBMaker CE DLL plugin

AOBMaker is a tool used to generate AOB patterns and CE Auto Assembler scripts. It has a CE DLL plugin that can communicate with UE5CEDumper to share detected patterns and offsets. If you have AOBMaker, you can enable the integration with UE5CEDumper UI:
- Direct browse memory region or code in CE by one-click in UI.
- Generate CE AA scripts with dynamic GWorld AOB AA scripts directly.
- Generate CE memory records for UE types and fields.
- Generate dissect data for UE structs to use with CE Structure Dissect.

Note: AOBMaker integration is optional and not required to use UE5CEDumper. The core features of UE5CEDumper work independently of AOBMaker.

Download AOBMaker here (register is required): https://opencheattables.com/viewtopic.php?t=1788

## Requirements

### Build

| Tool | Version |
|---|---|
| Visual Studio / MSVC | 2022 v17+ (MSVC v143) |
| CMake | 3.25+ |
| Ninja | any recent |
| .NET SDK | 10.0 |

> `build.cmd` / `build.ps1` locate MSVC automatically via `vswhere`. No hardcoded paths.

### Runtime

- Windows 10/11 x64
- Cheat Engine 7.6+ (for CE injection method) *or* Proxy DLL (no CE required)
- A running Unreal Engine 4 or 5 game process (x64)

---

## Important Notes

* **Custom Data Structures**: In games like *FF7 Rebirth*, some critical data (e.g., HP) is stored in custom structures outside standard `UObjects`. The Live Walker can help investigate these regions, but direct discovery is not possible.
* **GWorld Connectivity**: `GWorld` traversal works in **100% of tested games (39 / 39)** as of 2026-07-23, including Satisfactory (modular UE build with a separate `CoreUObject-Win64-Shipping.dll`), *Star Wars Jedi: Fallen Order* (UE 4.21, EA Origin / Steam launcher), *Avowed* (where the `GWorld` AOB lands on a decoy and is recovered by scanning `.data` for a pointer to the active game world — the one with a non-null `OwningGameInstance`, skipping World-Partition `_Generated_` cells), *Stellar Blade*, *Persona 3 Reload*, *Pionero Capital Demo*, *MindsEye* (UE 5.4.4 licensee fork, game v7.3.1), and *Star Trek Voyager – Across the Unknown* (stock UE 5.6). For unverified games, fall back to **Object Tree** or **Instance Finder** as the primary entry point.
* **Proxy DLL caveat for EA-launcher games**: *Star Wars Jedi: Fallen Order* launches via the EA app; neither `version.dll` nor `dinput8.dll` proxy is loaded by the wrapped process (EA's launcher restricts the DLL search path). The DLL must be CE-injected after the game is running. The scan side itself works fine — GObjects / GNames / GWorld all resolve cleanly once the DLL is inside the process. Same pattern likely applies to other EA-launched titles; report any new ones as an issue.
* **`dxgi.dll` proxy for games that import neither `version.dll` nor `dinput8.dll`**: a few titles (e.g. *The Adventures of Elliot*, a SQUARE ENIX D3D12 title — its stripped PE detects as UE4.27 but it is runtime-reconciled to UE5.4; *Echoes of Aincrad Demo*, a Bandai Namco UE5.4 D3D12 title) simply don't import `version.dll` or `dinput8.dll` in their EXE, so those proxies are never loaded by Windows — unlike the EA case, this is the game's own import table, not a launcher restriction. Use the **`dxgi.dll`** proxy (`build.ps1 -Target ProxyDxgi`, or pick *dxgi.dll* in the Proxy Deploy tab): every D3D11/D3D12 UE game imports `dxgi`, so it loads reliably. Live-verified end-to-end on Elliot, Echoes of Aincrad Demo, *Pionero Capital Demo* (a stock UE5.7 title — the first stock-5.7 game verified through this proxy), and *Star Trek Voyager – Across the Unknown* (stock UE5.6) — connect + scan + object browse.
* **Games that pause when backgrounded**: some titles (e.g. *Persona 3 Reload*, `t.IdleWhenNotForeground=1`) freeze their game thread whenever they are not the foreground window — game-thread invokes then time out. The tool **detects the stall** (an amber "game thread stalled" banner appears; camera-POV reads fall back to a raw cached read instead of hanging the pipe), and the experimental **Keep Foreground** toggle addresses the root cause so invokes keep working while the game is backgrounded.
* **Container Limits**: Array/Map/Set element reading respects a configurable limit to avoid excessive memory reads. Adjust the **Array Limit** slider in the Live Walker when working with large containers.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on:
- **Reporting detection failures** — what logs and info to include (most helpful!)
- **Submitting AOB patterns** — for reverse engineers who want to contribute directly
- **Code contributions** — PR process and code style

---

## References & Credits

| Project | Use |
|---|---|
| [Encryqed/Dumper-7](https://github.com/Encryqed/Dumper-7) | Dynamic offset detection patterns, FField/FProperty probing strategy |
| [UE4SS-RE/RE-UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) | UE5 runtime reflection, alternative angle |
| [Spuckwaffel/UEDumper](https://github.com/Spuckwaffel/UEDumper) | Live editor UI architecture reference |
| [trumank/patternsleuth](https://github.com/trumank/patternsleuth) | Additional AOB patterns for GObjects/GNames |
| [Do0ks/GSpots](https://github.com/Do0ks/GSpots) | Additional AOB patterns for GObjects/GNames |
| [nlohmann/json](https://github.com/nlohmann/json) | Header-only JSON library used in DLL |
| [cheat-engine/cheat-engine](https://github.com/cheat-engine/cheat-engine) | CE Lua scripting API reference |
| **AOBMaker (private)** | AOB pattern generation tooling, AA script generation and fast CE-Goto (not a must) |
| UE4 Dumper.CT | Cake-san's cheat table — additional UE4 AOB patterns (CT-series in Signatures.h) |

---

## Built with Claude Code

This project is developed with the assistance of [Claude Code](https://claude.ai/code) by Anthropic. The C++ DLL, C# Avalonia UI, build scripts, and documentation are collaboratively authored by the developer and Claude Code.

---

**License**: [MIT](LICENSE) © 2026 bbfox0703