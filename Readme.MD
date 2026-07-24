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

A quick look at what you can actually *do* with it (the full table-maker feature list is in **[Features.md](Features.md)**):

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
| **5.5 – 5.7** | ✅ | ✅* | ✅** | *Titan Quest II, EverSpace 2, Lushfoil Photography Sim, Manor Lords, Cat Island Petrichor Demo, Way of the Hunter 2 Demo, COMBAT PILOT: CARRIER QUALIFICATION Demo, Solarpunk, Pionero Capital Demo, Satisfactory (v1.2.3.1), Star Trek Voyager – Across the Unknown* |

*\*GNames uses .data pointer-scan fallback for 5.5+*
*\**DynOff supports **CasePreservingName (FName = 16 bytes)** layout.*

---

## Features for Table Makers

The full table-maker feature list — one row per feature (AOB scanning, DynOff, Live Walker, Value Search single + group scan, Teleport, movement tuning + God Mode + Time Dilation, the Live function profiler, multi-format CE export, and the rest) — lives in **[Features.md](Features.md)**.

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