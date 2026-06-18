# UE5CEDumper
<img src="./img/UE5CEDumper.jpg" alt="UE5CEDumper"/>  

**The Live Bridge between Unreal Engine Runtime and Cheat Engine.**

UE5CEDumper is a interactive inspector toolchain. It provides a **live window** into the game's memory, allowing you to browse objects, find instances, and export CE-ready structures in real-time.

> It's built for the *active* table maker. It bridges the gap between seeing an offset and actually using it in Cheat Engine.

> *UE5CEDumper is not meant to be a another dumper that extracts large amounts of data for analysis. Instead, it focuses on quickly finding UE structures and integrating with CE for live development. Think of it as a general-purpose UE tool rather than a specialized dumper.*

---
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D4)](https://www.microsoft.com/windows)
[![UE Version](https://img.shields.io/badge/UE-4.18--5.8-orange)](https://www.unrealengine.com/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![C++](https://img.shields.io/badge/C++-23-00599C)](https://isocpp.org/)
[![Avalonia](https://img.shields.io/badge/Avalonia-UI-8B5CF6)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-CC785C?logo=claude)](https://claude.ai/code)

## Sample screenshots
<video src="https://github.com/bbfox0703/UE5CEDumper/raw/main/MainUI.webm" width="100%" controls autoplay loop muted></video>  
<img src="./img/Value_Search.webp" alt="Value Search"/>  

## Highlights

A quick look at what you can actually *do* with it (the [full table-maker feature list](#features-for-table-makers) is further down):

- **Live memory inspection** — browse the game's objects, find every instance of a class, and drill into struct / class layouts with real-time values.
- **Value Search** — Cheat-Engine-style First Scan / Next Scan over every UE property (numbers, strings, vectors, arrays / maps / sets), so you find what to cheat without knowing offsets.
- **Teleport** — save your position to 3 markers and recall later (BugItGo-style), or **teleport to the mouse cursor** in top-down / 2.5D games. Bind your own global hotkeys. Also reads the live **camera POV** (location / rotation / FOV) so you can see whether a game's camera follows the pawn or is driven independently.¹
- **Debug Camera** — force the free-fly debug camera on/off in games that ship it (reliable on/off even on Shipping builds that normally get stuck).
- **Console** — discover and one-click invoke the `fly` / `god` / `ghost` / game-specific cheat commands that many games leave in.
- **One-click Cheat Engine export** — pointer-chain XML, Structure Dissect (CSX), SDK headers, AA scripts, and multi-row `.CT` batches.
- **No Cheat Engine needed to inject** — drop the `version.dll` **proxy DLL** next to the game, launch, and connect.

> ¹ Marker save/recall works broadly (including Titan Quest II, after a transform-refresh fix for engine setters that report success but don't move the actor). A few heavily-stripped Shipping builds (e.g. Titan Quest II) can't do *cursor* teleport because they remove the standard cursor / viewport / line-trace APIs and use a custom virtual cursor — see [docs/teleport-spec.md](docs/teleport-spec.md) for the details.

## Tested Version Matrix

| UE Version | GObjects | GNames | DynOff | Verified Games |
|---|:---:|:---:|:---:|---|
| **4.15 – 4.17** | ✅ | ✅ | ✅ | *Extinction* |
| **4.18 – 4.20** | ✅ | ✅ | ✅ | *FF7 Remake Intergrade, The Occupation* |
| **4.21 – 4.24** | ✅ | ✅ | ✅ | *Star Wars Jedi, DQ XI S, IDOLM@STER STARLIT SEASON, Octopath Traveler, DQ I&II / III HD-2D Remaster* |
| **4.25 – 4.27** | ✅ | ✅ | ✅ | *FF7 Rebirth, Tower of Mask, Hogwarts Legacy, Romancing SaGa 2 RotS, Ghostwire: Tokyo, TimeSplitters Rewind (Early Access V0.3.3), The Artisan of Glimmith, Barn Finders, MOBILE SUIT GUNDAM SEED Battle Destiny Remastered, The Adventures of Elliot Demo* |
| **5.0 – 5.2** | ✅ | ✅ | ✅ | *Squirrel With A Gun, Caravan Sandwitch, Meltopia, Retro Rewind Demo* |
| **5.3 – 5.4** | ✅ | ✅ | ✅ | *Satisfactory (v1.1.3.1), Colossal, Avowed (Obsidian — packed 20-byte FUObjectItem; static-struct GObjects AOB), Echoes of Aincrad Demo (Bandai Namco — dxgi.dll proxy)* |
| **5.5 – 5.7** | ✅ | ✅* | ✅** | *Titan Quest II, EverSpace 2, Lushfoil Photography Sim, Manor Lords, Cat Island Petrichor Demo, Way of the Hunter 2 Demo, COMBAT PILOT: CARRIER QUALIFICATION Demo, Solarpunk (stock UE5.7 — Object@+0x08)* |

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
| **UFunction Invoker** | Call game functions via `ProcessEvent` directly from the UI. Game-thread safe dispatch via MinHook hook. Supports ScriptStruct parameters — 12 known engine structs (FVector, FRotator, FColor, etc.) + dynamic struct layout discovery. **Structured return DataGrid** (build 775) shows FVector / FRotator / FHitResult / user USTRUCT returns as a Field / Type / Value / Offset grid below the existing single-line decode. **Param picker** for pointer params surfaces the expected UClass label (`[UObject*: AActor, 8B]`) + `[Pick…] [null] [self]` quick-fill buttons. |
| **Value Search — by-value scan** *(build 738 + Phase 2 757 + V1a 927)* | CE-style **First Scan / Next Scan** workflow over every UPROPERTY-declared field. **Numeric primitives** (Int8/16/32/64 + UInt + Float / Double / Bool) with Exact / Bigger / Smaller / Between / Changed / Unchanged / Increased / Decreased + CE-style rounded float tolerance. **Multi-numeric meta** — `NumericNoByte` / `NumericAll` scan every numeric width in one pass, comparing by each field's own declared type, so you don't need to know up front whether the value is stored as int or float (no byte-reinterpret false hits). **Strings** (FString / FName / FText) with Contains / StartsWith / EndsWith and optional case-sensitive matching. **Vectors** (FVector / FRotator) with `"X,Y,Z"` CSV input and per-axis tolerance. **Containers** — `TArray<T>`, plus **`TSet<T>` and `TMap<K,V>` key/value**, are scanned element-wise (rows render `Field[N]` / `Set[N]` / `Map.Key[N]` / `Map.Value[N]`) with a 10 M-element soft circuit-breaker. A case-insensitive **keyword filter** narrows the result grid and a **Cancel** button abandons an in-flight scan. Native C++ fields (non-UPROPERTY) are explicitly out of scope — banner directs users to CE's raw memory scan. Sessions auto-expire after 5 min. |
| **Property Freeze (horizontal class-wide)** *(build 719)* | One-click freeze that locks a property at a constant value across **every live instance** of the owning class — handles respawns / new spawns / destroys automatically via a 5 s rescan timer. Ships an AA Script that depends on `ue5_freeze_helper.lua` (auto-injected into the open CE table via the AOBMaker plugin). Supports BoolProperty / ByteProperty / Int8-64Property / UInt16-64Property / EnumProperty / FloatProperty / DoubleProperty. Multiple freeze scripts coexist via per-script keyed handle table. |
| **Interesting Funcs + Properties finders** *(build 597 → 687)* | Two scoring-driven discovery tabs that rank UFunctions and properties by name + class + flag heuristics — Stats / Inventory / Movement / Combat / Utility categories with class bonuses for Character/Pawn/PlayerController etc. CamelCase tokeniser means short acronyms (HP/MP/SP/XP/TP) match safely. **Unusual Location** flag highlights cheat-relevant properties sitting in non-conventional containers (LocalPlayer / GameViewportClient / HUD / CheatManager). Calibrated against a 18-game cross-game dump corpus via the offline analyzer at [`scripts/analysis/`](scripts/analysis/). |
| **Console — UFUNCTION(exec) discovery + invoke** *(build 731 + UCheatManager hint 778)* | Surface every UFUNCTION(exec) reachable in the game and provide one-click invoke. Many cooked Shipping titles ship `fly` / `ghost` / `god` / `setspeed` / game-specific exec entry points untouched — the game author already curated the cheat surface for us. **UCheatManager hint** warns when the selected exec's class inherits from `UCheatManager` — those are commonly body-stripped by Epic via `#if !UE_BUILD_SHIPPING` (invoke returns OK + no in-game effect); the hint redirects users to a game-specific exec for verification. |
| **Teleport (BugIt-style)** *(build 1027 → 1112)* | Save the player position to **3 marker slots** and recall later, or **teleport to the mouse cursor** for top-down / 2.5D games (with a screen-center fallback for cursorless titles). Resolves the local pawn entirely by reflection (no per-game offsets) and moves it by invoking the engine's own `K2_SetActorLocation` / `K2_TeleportTo` / component-level `K2_SetWorldLocation` on the game thread; raw write is a last-resort fallback. **BugItGo interop** — copy the current pose as a `BugItGo X Y Z` string, or paste one (incl. the full `?BugLoc=…?BugRot=…` form) and run it. **Read-only camera POV** *(build 1110-1112)* — reads the on-screen camera's location / rotation / FOV (distinct from the pawn pose) so you can tell whether a game's camera follows the pawn or is independent; uses the engine's `APlayerCameraManager` getters with a fully-reflected `CameraCachePrivate.POV` cache fallback for titles where the getters don't respond (verified on SEED, DQ III HD-2D, Titan Quest II, Octopath). **User-set global hotkeys** for Save/Recall (press-to-set, persisted) plus an auto-bound cursor hotkey, so you can trigger teleports with the game focused. Also exports the actions as momentary CE records / `.CT`. All logic is DLL-side via a shared-memory mailbox, so the generated CE records run with just the DLL injected. |
| **Debug Camera force on/off** *(build 1014)* | Reliable free-fly debug camera toggle, even on Shipping builds that strip `DisableDebugCamera` and otherwise get stuck in free-fly. Reads the live state from the CheatManager and, when the game's own toggle can't disable, switches the local player's controller back to the original PlayerController by hand. Version-agnostic (all offsets reflected). Shares the same DLL mailbox path, so it works from the UI **or** a self-contained CE script. |
| **Multi-row → One .CT batch generator** *(build 760)* | Select N rows in Interesting Funcs or Interesting Properties with Ctrl/Shift+click, click the 📦 **Generate CT** button → save one ready-to-share `.CT` file containing per-row AA Script entries grouped by category. Property rows become Freeze entries; function rows become Baked Invoke entries. XML-escapes all special chars so `TArray<int>` / `&` / quotes in descriptions can't break a CT load. |
| **Game-patch diff (`diff_dumps.py`)** *(2026-05-27)* | Offline pure-Python tool that compares two `Dump All Metadata` JSONL exports from the same game across patches. Surfaces **moved fields** (same name, different offset / size), type changes, function signature changes, and added / removed classes / properties / functions — the silent killers of working cheat tables. `--minimal` for breaking-changes-only output. Lives at [`scripts/analysis/diff_dumps.py`](scripts/analysis/diff_dumps.py). |
| **Instance Finder** | Locate all active objects of a class instantly. One-click to open in Live Walker. |
| **Property Search** | Search across all classes for specific property names. Find where a field is defined without browsing the full object hierarchy. **Freeze** button per row (build 719) ships a CE AA Script that locks the field across every live instance. |
| **Class Structure** | Browse class definitions with full field layouts, types, offsets, and inheritance chains. |
| **Multi-Format CE Export** | **CE XML** (pointer chain address records), **CSX** (CE Structure Dissect), **SDK Header** (.h), **CE Field** (single field), **CE AA Script** (Auto Assembler). Supports scalars, bitfields, structs, arrays, Map/Set, OptionalProperty\<Struct\>, and UEnum DropDownLists. **N-level pointer drill-down** (toolbar Drill Depth slider 0–4) — Copy CE Field on an `ObjectProperty` follows the pointer and emits the target's UPROPERTYs as nested `Offsets=[0]` children, so a single paste in CE shows the whole referenced object's layout. **Soft array per-element** emits FName leaf at FSoftObjectPath sub-offset (UE4/5.0 single FName; UE5.1+ FTopLevelAssetPath PackageName + AssetName) instead of a flat 8B hex. **Collapse chain** (toolbar toggle) optionally folds the entire `GWorld → … → target` pointer spine into one CE multi-level-pointer entry — `base` + a single merged node + the target field with its drill-down — so a deep navigation path pastes as a compact multi-level pointer instead of N nested groups. |
| **Proxy DLL** | Proxy-DLL injection — no Cheat Engine required. Place next to the game exe, launch, and connect the UI. Three hijack targets cover the field: **`version.dll`** (broadest default), **`dinput8.dll`** (gamepad/DirectInput titles), and **`dxgi.dll`** (every D3D11/D3D12 UE game — the reliable target for EXEs that import *neither* version nor dinput8, e.g. some SQUARE ENIX builds). The Proxy Deploy tab scans Steam, deploys the chosen type per game, and warns only when two of our proxies actually coexist. |
| **Modern UI** | External **Avalonia UI** connected via high-speed Named Pipe (~50 JSON-RPC commands). Sortable result grids throughout. Native AOT published with zero IL2026 / IL3050 warnings. **Long operations cancel cleanly** — they stop when you cancel or close the UI, so the injected DLL never spins idle and the game always closes promptly. Keep your game performance smooth. |

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

> **Which proxy DLL?** Start with `version.dll` (`build.ps1 -Target ProxyDLL`). If the game launches but the UI can't connect, the EXE probably doesn't import `version.dll` — try **`dxgi.dll`** (`-Target ProxyDxgi`), which every D3D11/D3D12 UE game imports, or `dinput8.dll` (`-Target ProxyDinput8`). `build.ps1` (default `-Target All`) builds all three into `dist\proxy\`, and the **Proxy Deploy** tab lets you pick the type per game and deploy it for you.

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
* **GWorld Connectivity**: `GWorld` traversal works in **100% of tested games (34 / 34)** as of 2026-06-17, including Satisfactory (modular UE build with a separate `CoreUObject-Win64-Shipping.dll`), *Star Wars Jedi: Fallen Order* (UE 4.21, EA Origin / Steam launcher), and *Avowed* (where the `GWorld` AOB lands on a decoy and is recovered by scanning `.data` for a pointer to the active game world — the one with a non-null `OwningGameInstance`, skipping World-Partition `_Generated_` cells). For unverified games, fall back to **Object Tree** or **Instance Finder** as the primary entry point.
* **Proxy DLL caveat for EA-launcher games**: *Star Wars Jedi: Fallen Order* launches via the EA app; neither `version.dll` nor `dinput8.dll` proxy is loaded by the wrapped process (EA's launcher restricts the DLL search path). The DLL must be CE-injected after the game is running. The scan side itself works fine — GObjects / GNames / GWorld all resolve cleanly once the DLL is inside the process. Same pattern likely applies to other EA-launched titles; report any new ones as an issue.
* **`dxgi.dll` proxy for games that import neither `version.dll` nor `dinput8.dll`**: a few titles (e.g. *The Adventures of Elliot*, a SQUARE ENIX UE4.27 D3D12 demo; *Echoes of Aincrad Demo*, a Bandai Namco UE5.4 D3D12 title) simply don't import `version.dll` or `dinput8.dll` in their EXE, so those proxies are never loaded by Windows — unlike the EA case, this is the game's own import table, not a launcher restriction. Use the **`dxgi.dll`** proxy (`build.ps1 -Target ProxyDxgi`, or pick *dxgi.dll* in the Proxy Deploy tab): every D3D11/D3D12 UE game imports `dxgi`, so it loads reliably. Live-verified end-to-end on Elliot and Echoes of Aincrad Demo (connect + scan + object browse).
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