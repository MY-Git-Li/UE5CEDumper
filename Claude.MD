# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> For detailed specs, implementation history, and debugging notes, see the **[docs/](docs/)** directory.

-----

## Build & Deploy
- After making code changes, always do a full rebuild and verify the build output is actually updated before testing. Never assume a build succeeded without checking.

-----

## Code Changes
- When asked to refactor or rename modules/files, make actual code changes (move files, update imports, rename classes) — not just documentation updates. Confirm structural changes before proceeding to docs.

-----

## Debugging
- When fixing bugs, verify the fix against the actual memory layout or data structure rather than assuming. If the first fix doesn't work, re-examine fundamental assumptions about the data format before iterating.

-----

## Git Operations
- When creating PRs, check for branch divergence and resolve merge conflicts before attempting `gh pr create`. Run `git status` and `git log --oneline -5` first.

-----

## Cheat Engine
- When working with CE Lua APIs, verify that functions/methods actually exist in the Cheat Engine Lua API before using them. Do not invent API calls.

-----

## Build & Dev Commands

### Unified Build Script (PREFERRED — always use this)

The `build.ps1` script handles VS DevShell setup, CMake, dotnet, and test execution automatically. **Always use this for building** — bare `cmake` / `dotnet build` commands will fail without the VS DevShell environment.

```bash
# Build everything (DLL + UI + Tests) — Release
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1"

# Build DLL + all 3 proxy DLLs (version/dinput8/dxgi — the injected artifacts)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target DLL

# Build UI only
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target UI

# Run tests only
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target Test

# Debug build
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Mode Debug

# Clean + publish (distribution)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Mode Publish -Clean
```

> **Why not bare cmake/dotnet?** The C++ DLL requires MSVC x64 environment (include paths, linker). `build.ps1` loads this via `Enter-VsDevShell` automatically. Running `cmake --build` without it causes `fatal error C1083: No such file or directory` for standard headers.

### Manual Commands (reference only — prefer build.ps1)

```bash
# C++ DLL (requires VS DevShell loaded first)
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release

# C# UI
dotnet build "ui/UE5DumpUI/UE5DumpUI.csproj" -c Release
dotnet test "ui/UE5DumpUI.Tests/UE5DumpUI.Tests.csproj"
```

### Git Submodules

```bash
git submodule update --init --recursive
```

-----

## Rules (MUST follow)

- **Language**: Code comments and UI strings in English
- **Single Instance**: UI app uses Mutex to ensure only one instance runs
- **Async everywhere**: All I/O, pipe operations, and alert actions must be async
- **Platform Abstraction**: Any system/OS-dependent call (P/Invoke, Registry, OS commands) MUST go through an interface in `Core` project. `Core` must NEVER contain direct platform-specific code
- **Log output**: All logs go to `%LOCALAPPDATA%\UE5CEDumper\Logs`, split by category into per-process subfolders. DLL: 5 files (init, scan, offsets, pipe, walk). UI: 3 files (init, pipe, view). 4-file rotation, 8MB max per file. Root `Logs/` has no loose files — only subfolders
- **Magic words management**: All magic strings kept in one file per project with proper comments
- **UI Strings**: English only. All UI strings in `Resources/Strings/en.axaml`, referenced via `StaticResource` bindings
- **UE offsets**: All UObject/UStruct offsets must be dynamically verified via OffsetFinder, never hardcoded
- **AOT compatible**: All C# code must be Native AOT / trimming compatible. No reflection-based APIs — use source generators instead (e.g. `[JsonSerializable]` context for `System.Text.Json`, `[ObservableProperty]` for MVVM). The UI is published as a self-contained trimmed binary
- **Module naming (Frieren convention)**: Every new C++ DLL module (a file with its own namespace) MUST take an unused character name from the **Frieren roster** in [docs/naming-convention.md](docs/naming-convention.md) — never a plain-English name (the historical plain-English modules have all been migrated: `Cancel`→`Tot`, `PackedItem`→`Lineal`, `ValueScan`→`Radar`). Add the required header comment (`// {Name} — {中文名} ({meaning})` + a one-line function description) and flip that name's status to 🟢 in use in the roster. Kept English by design (the only exceptions): generic leaf utilities (`Utf8Helpers.h`) and algorithm helpers that live inside an existing namespace (`GraphPath.h` under `Aura::`)
- **CE Lua output hygiene**: Every CE Lua script we emit — the C# script generators (`*ScriptGenerator.cs`, `CeXmlExportService`), the standalone `scripts/*.lua`, and `scripts/UE5CEDumper.CT` — MUST be quiet by default so the CE Lua Engine window never covers Cheat Engine.
  - **DEBUG flag**: gate every diagnostic/progress `print()` behind a flag. Global master switch with per-script override: `local DEBUG = UE5_DEBUG or 0` (unset = off; set `UE5_DEBUG=1` in CE's Lua console to make everything verbose). Print via a `dbg()` wrapper: `local function dbg(...) if DEBUG ~= 0 then print(...) end end`. Standalone `.lua`/`.CT` gate inline on `(UE5_DEBUG or 0) ~= 0`.
  - **Errors always surface**: real failures keep bare `print()` / `showMessage()` (ungated), and warnings that flag a genuine problem (e.g. dissect `warn`, `ue5_logError`) stay unconditional.
  - **Auto-close on clean success**: when `DEBUG == 0` and the block finished with no error, close the window via `synchronize(function() getLuaEngine().Close() end)`. On ANY error path (mailbox-not-found, timeout, `state<0` / `code==-7` / `result~=0`, null mid-walk, AOB-scan-fail) the close MUST be unreachable — `return` before it or guard it so the window stays open. A timeout is an error, so `return` (not `break`) out of a mailbox-wait loop that precedes a close.
  - **Reuse the shared emitter**: generators call `CeLuaHygiene.AppendDebugPreamble` / `AppendCloseOnSuccess` (and `CeLuaHygiene.CloseCall`) so the preamble/close text is byte-identical everywhere and can't drift. Each `{$lua}` block is its own Lua chunk — locals don't cross `[ENABLE]`/`[DISABLE]`, so emit the preamble in every block that uses `DEBUG`/`dbg`.

-----

## Project Goal

Develop a **C++ DLL + Cheat Engine Lua Bridge** Unreal Engine Dumper.
Target: Support UE4 (4.18+) and UE5 (5.0 ~ 5.8+), find global pointers (GObjects / GNames / GWorld), build complete object/struct hierarchy, integrate with CE. Despite the "UE5" name, UE4 games are a priority target — many popular games use UE4, and RE-UE4SS demonstrates broad UE4 support is achievable.

-----

## Architecture Overview

```
+------------------------------------------------------+
|  Game Process                                        |
|                                                      |
|  CE Lua                                              |
|    UE5CEDumper.CT     <- DLL inject + pipe start     |
|    ue5_dissect.lua    <- CE Structure Dissect builder |
|    ue5_invoke_helper.lua <- AA Script invoke shim (embedded in CT via UI) |
|      |  loadLibrary / callFunction (28 C ABI exports)|
|      v                                               |
|  UE5Dumper.dll  [injected]                           |
|      +-- Frieren  (ExportAPI)     <- C ABI for CE Lua (13 teleport incl. Relative/RecallExplicit/Set+GetMouseCursor, build 1144) |
|      +-- Fern     (PipeServer)    <- Named Pipe IPC (~64 cmds incl. value scan + group scan + teleport + teleport_relative + set/get_mouse_cursor + get_related_objects + get_current_target + get/set_movement_multiplier/reset_movement (Laufen) + set/get_foreground_lock (Grausam); find_path_from_gworld deep/container_depth + teleport_get_pose loc/vel field addrs, 1792) |
|      +-- Himmel   (Signatures)    <- 128 AOB + symbol exports |
|      +-- Genau    (OffsetFinder)  <- GObjects/GNames/GWorld + FindGameEngine (live UEngine); CE Lua get GWorld (&slot+deref) / GameEngine addr via Mimic CMD_QUERY_PTR=13 mailbox, 1978 |
|      +-- Aura     (ObjectArray)   <- Chunked + Flat arrays + Find Refs/Containers (+ recursive deep descent FindInContainersDeep, 1198) + ScanForValue (lean Candidate V3-A 926; TSet/TMap scan V1a 927) + ScanForValueGroup (multi-value group scan via Orden, 1276) + FindObjectGraphPath (GWorld fwd BFS / Locate in GWorld, 1181; + RecoverViaWorldLevel streaming/WP actor recovery via ULevel::OwningWorld back-ref, status ok_via_level, 1405; + opt-in deep param — follow obj ptrs in one struct-element container level TArray/TSet/TMap<FStruct>, 1780) + GetRelatedObjects (forward owned graph: Self/Class/Outer/Controller↔Pawn/components/ASC/AttributeSet, 1323) + CollectOutgoingObjectPtrs (public outgoing-edge façade for Edel, 1400) + FindInstancesByClass server-side exclude_classes-before-cap + full-pool class_histogram via split loop gated by buildHistogram, 1444) |
|      +-- Serie    (FNamePool)     <- UE5 Pool / UE4 TNameEntry|
|      +-- Ubel     (UStructWalker) <- FField + UProperty + Phases B-K + Optional + ReadFStringAt/ReadFNameAt/ReadFTextStringAt (public, build 757) |
|      +-- Macht    (Memory)        <- AOBScan + SEH read/write |
|      +-- Sein     (Logger)        <- 5-category per-process   |
|      +-- Stark    (GameThreadDispatch) <- MinHook ProcessEvent hook |
|      +-- Radar    (ValueScan)     <- SessionManager + DataType / ScanType / CompareStringPredicate / CompareVectorPredicate (build 738 + Phase 2 757) + GroupSessionManager (multi-value group scan, 1276) |
|      +-- Orden    (GroupMatch)    <- source-agnostic SDR/assignment matcher for multi-value group scan (header-only, build 1276) |
|      +-- Denken   (NativeDisasm)  <- Zydis x64 decoder: native UFunction -> [this+off] property xref (Path 2, build 862) |
|      +-- Tot      (Cancellation)  <- Tot::Requested() flag; Fern disconnect-monitor + shutdown-abort stop long ops (build 936) |
|      +-- Wirbel   (Teleport)      <- marker save/recall + cursor + camera POV + directional/explicit-coord TP + force mouse cursor (bShowMouseCursor) (build 1144); get_pose surfaces loc/vel FVector owner+offset for per-vector Locate-in-GWorld (1780); CMD_TELEPORT=8 mailbox (ops 0-15) |
|      +-- Solitar  (GodMode)       <- force AActor::bCanBeDamaged (FBoolProperty bit) + re-assert worker; CMD_PROTECT=9 mailbox on/off (build 1251) |
|      +-- Laufen   (MovementTuning)<- force per-pawn CMC float knobs MaxWalkSpeed/GravityScale/JumpZVelocity × multiplier of captured base + re-assert worker (write-on-drift, respawn re-capture); pipe get/set_movement_multiplier/reset_movement + SetKnobPercent (100%=off, jump √height) via UE5_SetMovementPercent export + Mimic CMD_MOVEMENT=10 mailbox (knobId 0-2 float; knobId 3 = GravityDirection FVector UE5.4+, normalized, (0,0,0)=off) for CE Lua/.CT (build 1808) |
|      +-- Edel     (CurrentTarget) <- auto-detect the player's current target: GWorld→PC→Pawn, score outgoing object-ptr fields (structural is-Actor gate + keyword boost); get_current_target pipe-only (build 1400) |
|      +-- Grausam  (ForegroundLock) <- MinHook user32!GetForegroundWindow → always report the game's own window as foreground + subclass game WndProc rewriting WM_ACTIVATEAPP/WM_ACTIVATE/WM_NCACTIVATE→active (two focus channels); defeats UE t.IdleWhenNotForeground idle + focus-loss pause so game-thread invokes/POV keep working when backgrounded; + MinHook ClipCursor/SetCursorPos → release cursor confinement when backgrounded-but-faking-foreground so the mouse isn't trapped in the game window (passthrough when genuinely foreground, 1984); pipe set/get_foreground_lock + Mimic CMD_FOREGROUND=12 mailbox (thread-agnostic, runs on polling thread) for CE Lua/.CT; LIVE-VERIFIED P3R (build ~1950-1956) |
|      +-- Schlacht (SeeThrough)     <- see-through occluders: worker (~10 Hz) traces camera→VIEW-forward (LineTraceSingle, game thread via Stark; works first- AND third-person), hides the NEAREST non-Pawn/Character hit (SetActorHiddenInGame) so world geometry stops covering the view; Pawn/Character kept visible via Aura::ClassDerivesFromAny; hit-actor pulled from FHitResult.Actor (UE4 weak-ptr→Aura::GetByIndex) / HitObjectHandle (UE5); restored as the view moves + un-hidden on disable; pipe seethrough_set/get_state + Mimic CMD_SEETHROUGH=14 mailbox (AOBMaker-only CE toggle); experimental-gated. Stage 1 nearest occluder — VERIFIED Tower of Mask + DQ7R; NO-OP on collision/render-split games (FF7R CollisionAssetActor = invisible collision proxy). Build 1989 |
+----------------------+-------------------------------+
                       | Named Pipe: \\.\pipe\UE5DumpBfx
                       | JSON over pipe (newline-delimited)
+----------------------v-------------------------------+
|  UE5DumpUI  (Avalonia + ReactiveUI, standalone exe)  |
|      +-- PipeClient                <- async pipe mgmt     |
|      +-- ObjectTreePanel           <- UObject hierarchy    |
|      +-- ClassStructPanel          <- Property Grid        |
|      +-- PointerPanel              <- GObjects/GNames addr |
|      +-- LiveWalkerPanel           <- Instance drill-down  |
|      +-- InstanceFinderPanel       <- Class instance search (+ "Newest first" opt-in: scan GObjects high-index end for just-spawned instances, 1325; + server-side class-noise exclude-before-cap re-run + full-pool class_histogram + configurable Max cap + client keyword filter, 1444) |
|      +-- PropertySearchPanel       <- Search by name (build 610+ dedupe + Freeze button; finder btn + opt-in Deep struct/container descent 1222) |
|      +-- InterestingFunctionsPanel <- Scored UFunctions; 📦 batch CT (build 760) |
|      +-- InterestingPropertiesPanel<- Scored properties (B'); 📦 batch CT (build 760) |
|      +-- ConsolePanel              <- UFUNCTION(exec) discovery + UCheatManager hint (build 778) |
|      +-- TeleportPanel             <- marker save/recall + cursor + BugItGo + POV + facing-dir TP + coord TP + force cursor (input-mode, build 1147) + GodMode toggle (Solitar, build 1251) + Move Speed / Gravity / Super Jump cards (Laufen: 10%-1000% log slider × MaxWalkSpeed/GravityScale/JumpZVelocity + Super Jump toggle & global hotkey, build 1792) + CE Lua/.CT (17-row) + Global Pointers card: Get GWorld / GameEngine — DLL-invoke via CMD_QUERY_PTR, publish as a registered CE symbol (UE_GWorld = register directly to stable &GWorld slot, auto-follows; UE_GameEngine = allocateMemory buffer of UEngine* snapshot; disable unregisters + deAllocs the buffer); AOBMaker push, else paste-able CE-XML clipboard fallback (WrapAaScriptXml); Standalone Trainer card moved to bottom, 1978 |
|      +-- ValueSearchPanel          <- CE-style by-value scan (build 738 + Phase 2 757) + Single/Group mode toggle (multi-value group scan, master-detail, build 1276) |
|      +-- RelatedObjectsPanel        <- Related Objects: an actor's class/outer + Controller↔Pawn + owned components/ASC/AttributeSet; 🌍 GWorld / Live Walker / finder handoffs; fed from InstanceFinder/ValueSearch/LiveWalker (build 1323) + 🎯 Detect target auto-detect (Edel, ranked candidate picker, build 1400) |
|      +-- ProxyDeployPanel          <- Proxy DLL management |
|      +-- CeXmlExport               <- CE XML pointer chains |
|      +-- CsxExport                 <- CE Structure Dissect  |
|      +-- CheatTableBuilder         <- Multi-row .CT batch generator (build 760) |
|      +-- StructReturnDecoder       <- Invoke result property grid (build 775) |
+------------------------------------------------------+
```

-----

## Documentation Index

| Document | Contents |
|----------|----------|
| [docs/roadmap.md](docs/roadmap.md) | **Current state** — capability matrix, per-game configuration, tested games, long-running concerns. **Read first** for "what works now". |
| [docs/tips.md](docs/tips.md) | **User-facing how-to recipes** — goal → which panel/button. First recipe: forcing camera rotation in fixed-view (2.5D/45°) games. |
| [docs/todo.md](docs/todo.md) | **What's next** — prioritized open work with effort/risk tags. Active plan + carryover gaps + speculative ideas. |
| [docs/dev-log.md](docs/dev-log.md) | **What shipped** — append-only milestone history per build number. Read when investigating "when/why was X added". |
| [docs/architecture.md](docs/architecture.md) | Directory structure (23 .cpp + 28 .h DLL files; ~135 UI .cs/.axaml files; 33 test files), git submodules, build environment. **C++ build is incremental** (Ninja, single build dir, shared `UE5DumperCommon` object lib compiles the proxy-agnostic sources once); `build clean` forces a full rebuild (CI always passes `-Clean`). |
| [docs/dll-spec.md](docs/dll-spec.md) | C++ DLL interface: 30 C ABI exports, offset tables, CE Lua bridge (Frieren-themed file/namespace names) |
| [docs/pipe-protocol.md](docs/pipe-protocol.md) | Named Pipe JSON IPC protocol (~46 commands incl. Value Search + Group Scan begin/refine/query/end, Request/Response/Event) |
| [docs/multipipe-eval.md](docs/multipipe-eval.md) | **Multi-pipe IPC evaluation** — why NOT to add pipes. Root cause of UI lag = DLL-side serial-dispatch head-of-line blocking (not the pipe count, not the UI `_writeLock`, not CE). CE `.CT` mailbox is already off-pipe; its only risk is game-thread CPU starvation. **Phase 0 SHIPPED** (scan thread-priority guard, build 1834); Phase 1 = non-blocking dispatch; Phase 2 = concurrency-safety prereqs. Read before any pipe/IPC concurrency change. |
| [docs/group-value-scan-spec.md](docs/group-value-scan-spec.md) | **Multiple Values Group Scan** — architecture (`Orden` source-agnostic SDR matcher, block model, deep containers) + the **extension points** for Snapshot/SPC/Pivot reuse + P2/P3/P4 roadmap. Read for "how a future feature plugs into group matching". |
| [docs/snapshot-group-match-spec.md](docs/snapshot-group-match-spec.md) | **Snapshot Group Match (S1–S5 SHIPPED + MERGED main PR #348, in-game VERIFIED)** — N-value group matching over the captured-snapshot corpus via a pure-C# `Orden` port. Mode A single-snapshot absolute + **Mode B cross-snapshot temporal** (per-slot `Changed/Unchanged/Increased/Decreased` — **groups need `Unchanged`**, e.g. Current HP↓ + Max HP unchanged) + **Deep** (nested array/`Tunes[2]` values). Snapshot panel Diff/Group toggle + Compare-with + Deep. Read before touching snapshot multi-value. |
| [docs/native-c-value-scan-spec.md](docs/native-c-value-scan-spec.md) | **Native-C Value Scan (DESIGN ONLY, not built)** — opt-in raw/unmanaged (non-`UPROPERTY`) in-object scan for native HP/MP, in Value Search + Group Scan + Snapshot→SPC→Pivot. Reuses "Guess What" (`Ubel::GuessGapTypes`) + the `Orden` seam; holes-only within `PropertiesSize`. Read before implementing native-value scanning. |
| [docs/ui-spec.md](docs/ui-spec.md) | Avalonia UI tech stack (Avalonia 12.0.2 / .NET 10), AOT compatibility, component specs |
| [docs/export-formats.md](docs/export-formats.md) | CE XML, CSX, SDK Header, USMAP export rules, pointer chain model, type mappings |
| [docs/technical-notes.md](docs/technical-notes.md) | UE version differences, FField vs UProperty, FNamePool, DynOff, Property Type Layouts (Phases B-K), Address Finder layered lookup |
| [docs/lessons-learned.md](docs/lessons-learned.md) | Hard-won lessons from cross-game debugging (20+ games) |
| [docs/test-games.md](docs/test-games.md) | 20 test games with UE versions, GWorld status, stride info |
| [docs/naming-convention.md](docs/naming-convention.md) | Frieren-themed C++ file / namespace mapping (Macht/Genau/Aura/Serie/Ubel/Frieren/Fern/...) |
| [docs/aobmaker-integration.md](docs/aobmaker-integration.md) | AOBMaker CE Plugin pipe bridge (HEX / ASM / SYM / CreateAAScript) |
| [docs/reversing-nonstandard-ue-games.md](docs/reversing-nonstandard-ue-games.md) | **Playbook** for games where AOB + heuristics fail (forked/repacked engines): patternsleuth CLI → capstone .data-cluster → Ghidra headless decompile → caller `LEA` → encode the fix. The Avowed (Obsidian, packed 20-byte FUObjectItem) case. **+ Encrypted-games evaluation** — why NOT to vendor Dumper-7/RE-UE4SS for decryption (both are bring-your-own-key hooks; we already have parity via `UE5_SetObjectDecryption`). |
| [tools/README.md](tools/README.md) | Offline RE helpers — Ghidra scripts (`find_gobjects`/`decompile_functions`/`find_callers` Java + the pyghidra symbol/AOB exporters) and a capstone PE disassembler (`pe/disasm_function.py`). |
| [scripts/analysis/README.md](scripts/analysis/README.md) | Offline analysis tooling — `analyze_dumps.py` (cross-game keyword calibration) + `diff_dumps.py` (same-game patch diff, build 780). |
| [docs/simd-scanning-notes.md](docs/simd-scanning-notes.md) | AOBMaker SIMD scanning research: AVX2 anchor technique (reference) |
| [docs/CE-Bugs-Minesweeper.md](docs/CE-Bugs-Minesweeper.md) | CE-specific quirks and undocumented API behaviors |
| [docs/archive/](docs/archive/) | Outdated design docs preserved for history (UX spec, UFunction roadmap) |
