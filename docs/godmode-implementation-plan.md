# GodMode — Implementation Plan (v1)

> Companion to **[godmode-spec.md](godmode-spec.md)** (the design contract).
> This is the step-by-step build plan: ordered phases, exact files/symbols, and
> a build/verify gate after each phase. **Scope = GodMode only** (`bCanBeDamaged`),
> **re-assert loop in v1**, **Path B (self-contained `Solitar`)** — per the
> locked decisions in spec §2.

> **Status: NOT STARTED.** No code written yet (another session is editing
> `Wirbel.cpp` / `Frieren.cpp` / `Genau.cpp` / `Aura.cpp`). See
> **[§Merge safety](#merge-safety)** before starting — the *new* `Solitar.*`
> files are conflict-free; the *shared-file* edits (Frieren/Fern/Mimic/CMake) are
> the conflict surface and should land after the concurrent work merges.

-----

## 0. Build & verify commands (used at every gate)

```bash
# DLL only
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target DLL
# DLL native tests (utf8_helpers + dll_helpers)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target Test
# UI only
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1" -Target UI
# C# tests
dotnet test "ui/UE5DumpUI.Tests/UE5DumpUI.Tests.csproj"
# Everything (final gate)
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Github\UE5CEDumper\build.ps1"
```

> CLAUDE.md: always use `build.ps1` (loads the VS DevShell); bare `cmake`/`dotnet
> build` fail without it. After building, **verify the build output actually
> updated** before testing. `dotnet test --filter FullyQualifiedName~` runs ZERO
> tests (xUnit v3 / MTP) — run the whole project.

-----

## Phase 1 — DLL core: `Solitar` module (self-contained)

**New files (conflict-free):** `dll/src/Solitar.h`, `dll/src/Solitar.cpp`.

### 1a. `Solitar.h`

- Namespace `Solitar`, header comment per [naming-convention.md](naming-convention.md).
- `enum ProtectResult` (`PR_OK`/`PR_ERR_NOT_INIT`/`PR_ERR_NO_PAWN`/`PR_ERR_REFLECT`/`PR_ERR_WRITE`) — spec §5.1.
- Public API: `int32_t SetGodMode(bool)`, `int32_t GetGodMode()`,
  `struct State { int8_t godmode; bool resolvable; }`, `int32_t GetState(State&)`,
  `int32_t SetActorBool(uintptr_t obj, uintptr_t classAddr, const char* propName, bool on)`,
  `void StopWorker()`.
- **Pure inline helper for unit testing** (so `dll_helpers_test` can include it
  WITHOUT linking `Solitar.cpp` — the project's leaf-test pattern):
  ```cpp
  // Apply a desired single-bit value to a byte, leaving the other 7 bits intact.
  inline uint8_t ApplyBoolBit(uint8_t cur, uint8_t mask, bool value) {
      return value ? static_cast<uint8_t>(cur | mask)
                   : static_cast<uint8_t>(cur & ~mask);
  }
  ```

### 1b. `Solitar.cpp` — self-contained (Path B; copy logic, not links)

Includes: `Sein.h` (`#define LOG_CAT "WALK"`), `Solitar.h`, `Grimoire.h`,
`Macht.h`, `Aura.h`, `Ubel.h`. Globals reused (declare `extern`):
`extern uintptr_t g_cachedGWorld;` (defined in Frieren.cpp; same as Wirbel).

Anonymous-namespace helpers — **copy the logic** from the cited Wirbel functions
(spec §4 table), rewritten to use only public `Ubel`/`Aura`/`Macht`/`DynOff`:

| Helper | Copy from | Notes |
|---|---|---|
| `DerefWorld()` | Wirbel.cpp:77 | deref `g_cachedGWorld` via `Macht::ReadSafe` |
| `ReadPtrAt(obj,off)` | Wirbel.cpp:84 | trivial |
| `ResolveLocalPC(world)` | Wirbel.cpp:290 | chain + `Aura::FindInstancesByClass` fallback |
| `HopThroughDebugCamera(pc)` | Wirbel.cpp:341 | DebugCameraController → `OriginalControllerRef` |
| `ResolvePawn(pc)` | Wirbel.cpp:357 | `Pawn`, fallback `AcknowledgedPawn` |
| `ResolveActorBoolBit(obj, cls, name, &byteAddr, &mask)` | **Wirbel.cpp:1091 `ResolveCursorBit`** | the exact FBoolProperty layout read (probe `{base,±4,±8}`, validate FieldSize==1 + single-bit masks + ByteOffset≤7) |

Public functions (spec §5.2–5.4):

- `SetActorBool(obj, cls, name, on)`: `ResolveActorBoolBit` → `Macht::ReadSafe`
  byte → `ApplyBoolBit(b, mask, on)` → `Macht::WriteBytes` → re-read → return
  observed property bit (1/0) or `PR_ERR_*`.
- `ApplyGodNow()` (internal): resolve pawn; `SetActorBool(pawn, pawnClass,
  "bCanBeDamaged", /*on=*/ !s_wantGod)` — **polarity: GodMode ON ⇒ property
  FALSE**. Fallback name: `FindField(..., "CanBeDamaged" contains, "BoolProperty")`.
- `SetGodMode(on)`: lock `s_mutex`; `s_wantGod = on`; if on → `StartWorker()`
  + `ApplyGodNow()`; if off → `ApplyGodNow()` (restores can-be-damaged) +
  `StopWorker()`. Return observed state.
- `GetGodMode()` / `GetState()`: resolve pawn, read the bit; map "cannot be
  damaged" → godmode=1.

Re-assert worker (spec §5.3):

- `static std::thread s_worker; static std::atomic<bool> s_workerStop;`
- `StartWorker()`: idempotent; spawn thread running:
  `while (s_wantGod && !s_workerStop) { sleep(kProtectReassertMs); lock; if (resolvable) read bit; if drifted from desired → re-write; unlock; }`
- `StopWorker()`: set `s_workerStop`, `join()` if joinable, clear flag.
  **Dedicated stop flag + join — do NOT overload `Cancel::Requested()`** (that
  flag is for pipe long-ops). *(Refines spec §5.3, which mentioned `Cancel`.)*
- Worker does **only memory reads/writes** — never the game thread, so it can't
  stall.

> **Gate 1:** add to CMake (Phase 6) first if you want it to compile, or stub
> the wiring; otherwise this phase compiles once Phase 6 registers the file.

-----

## Phase 2 — Frieren C ABI exports  ⚠ shared file

**Edit:** `dll/src/Frieren.h`, `dll/src/Frieren.cpp` (Frieren.cpp is in the
concurrent session's modified set — land after it merges; see Merge safety).

- `#include "Solitar.h"` in Frieren.cpp.
- Implement (mirror `UE5_SetDebugCamera` style, Frieren.cpp:724):
  ```c
  int32_t UE5_SetGodMode(int32_t enable)  { return Solitar::SetGodMode(enable != 0); }
  int32_t UE5_GetGodMode(void)            { return Solitar::GetGodMode(); }
  int32_t UE5_GetProtectState(int32_t* outGod, int32_t* outResolvable);
  ```
- Declare them in `Frieren.h` (`__declspec(dllexport)`), next to
  `UE5_SetDebugCamera`.
- In `UE5_Shutdown()` (Frieren.cpp), call `Solitar::StopWorker();` before the
  rest of teardown (so the worker thread joins before unload — avoids the
  "game won't close" class).

**Gate 2:** `build.ps1 -Target DLL` — exports present (check the DLL export
table / map, or a quick CE `getAddress` later).

-----

## Phase 3 — Mimic mailbox: `CMD_PROTECT = 9`  ⚠ shared file

**Edit:** `dll/src/Mimic.h`, `dll/src/Mimic.cpp`.

- `Mimic.h`: add `CMD_PROTECT = 9` to `enum Cmd` (with the doc-comment block in
  the style of `CMD_TELEPORT`); add `enum ProtectOp { PROTECT_OP_SET_GODMODE=0,
  PROTECT_OP_GET_GODMODE=1, PROTECT_OP_GET_STATE=2 };` (reserve 3 =
  `SET_ACTOR_BOOL` for v2, documented only). Field map per spec §6.2.
- `Mimic.cpp`: `HandleProtect()` thin switch (style of `HandleSetDebugCamera`,
  Mimic.cpp:633) delegating to `UE5_SetGodMode`/`UE5_GetGodMode`/
  `UE5_GetProtectState`; write `result` + `paramsData[0..1]`; add the dispatch
  `case CMD_PROTECT:` in the poll loop.
- `extern "C"` decls for the three exports at the top of Mimic.cpp (as Mimic
  already does for `UE5_SetDebugCamera`).

**Gate 3:** `build.ps1 -Target DLL`.

-----

## Phase 4 — Fern pipe + Renge constants  ⚠ shared file

**Edit:** `dll/src/Renge.h`, `dll/src/Fern.cpp`.

- `Renge.h`: `constexpr const char* CMD_SET_GOD_MODE = "set_god_mode";`,
  `CMD_GET_GOD_MODE = "get_god_mode";`, `CMD_GET_PROTECT_STATE = "get_protect_state";`
  (next to `CMD_SET_DEBUG_CAMERA`).
- `Fern.cpp`: 3 handlers (mirror `set_debug_camera` at Fern.cpp:3201 /
  `set_mouse_cursor` at :3407): parse `enable`, call the export, build the JSON
  response (`state`/`godmode`/`resolvable`/`code`). Add the `extern "C"` decls
  for the new exports near Fern.cpp:40.

**Gate 4:** `build.ps1 -Target DLL`. Optional: manual pipe smoke
(`{"cmd":"get_protect_state"}`).

-----

## Phase 5 — Grimoire constants  ⚠ shared file

**Edit:** `dll/src/Grimoire.h` — in `namespace Grimoire`:

```cpp
// --- GodMode (Solitar) — docs/godmode-spec.md ---
constexpr int kProtectReassertMs = 300;   // re-assert worker tick
// Property name + fallback live in Solitar.cpp (string literals) or here if reused.
```

(Result codes live in `Solitar.h`; mailbox ops in `Mimic.h`; command strings in
`Renge.h` — keep each magic-word class in its existing home, CLAUDE.md.)

-----

## Phase 6 — Build registration  ⚠ shared file

**Edit:** `dll/CMakeLists.txt` — add `src/Solitar.cpp` to **both** source lists:

1. `add_library(UE5Dumper SHARED …)` (around line 144, after `src/Wirbel.cpp`).
2. `set(PROXY_SOURCES …)` (around line 233, after `src/Wirbel.cpp`) — so the
   version/dinput8/dxgi proxy DLLs also get GodMode.

**Gate 6:** `build.ps1 -Target DLL` builds the main DLL **and** (if a proxy
configure runs) the proxies, with `Solitar.cpp` compiled in.

-----

## Phase 7 — DLL native tests

**Edit:** `dll/tests/dll_helpers_test.cpp`.

- `#include "Solitar.h"` and test `Solitar::ApplyBoolBit`:
  - clear bit leaves other 7 bits intact (`ApplyBoolBit(0xFF, 0x04, false) == 0xFB`),
  - set bit (`ApplyBoolBit(0x00, 0x04, true) == 0x04`),
  - idempotence, every single-bit mask `0x01..0x80`.
- (No need to add `Solitar.cpp` to the test target — the helper is header-inline,
  matching how the leaf test stays Win32-free.)

**Gate 7:** `build.ps1 -Target Test` — utf8 + dll_helpers green.

-----

## Phase 8 — UI: service, DTOs, ViewModel, Panel

Mirror the Teleport feature's file set (confirmed present): `Views/TeleportPanel.axaml(.cs)`,
`ViewModels/TeleportViewModel.cs`, `Services/TeleportScriptGenerator.cs`,
`Models/TeleportModels.cs`, `Services/TeleportHotkeyStore.cs`.

**New files (conflict-free):**

- `ui/UE5DumpUI/Models/ProtectionModels.cs` — DTOs (`GodModeStateDto { int State; int Code; }`,
  `ProtectStateDto { int Godmode; bool Resolvable; int Code; }`) + a
  `ProtectionState` VM-facing model. **Register them in the source-gen JSON
  context** (the `[JsonSerializable]` partial context — mirror how
  `TeleportModels` DTOs are registered; AOT rule).
- `ui/UE5DumpUI/ViewModels/ProtectionViewModel.cs` — `[ObservableProperty]`
  `GodModeOn`, `GodModeBadge` (`ON`/`OFF`/`unavailable`), `IsBusy`; commands
  `ToggleGodModeCommand`, `RefreshStateCommand`, `CopyCeLuaCommand`,
  `SaveCtCommand`; gate on `IsConnected`; query `GetProtectStateAsync` on connect.
- `ui/UE5DumpUI/Views/ProtectionPanel.axaml` (+ `.axaml.cs`) — layout per spec
  §7 (toggle + badge + re-assert indicator + hotkey row + CE export + warning +
  discovery cross-link). All strings via `StaticResource`.
- `ui/UE5DumpUI/Services/ProtectionScriptGenerator.cs` — pure static, LF-only;
  tickbox toggle AA record (ENABLE→op SET_GODMODE value 1, DISABLE→value 0);
  embeds the op/offset table as comments; CE APIs verified vs `celua.txt`.
- (Hotkey: reuse `TeleportHotkeyStore`'s pattern, or generalise it; one persisted
  global hotkey for "toggle GodMode" via `IGlobalHotkeyService`.)

**Edit (shared UI files — low conflict risk; the concurrent work is DLL-side):**

- `ui/UE5DumpUI/Core/IDumpService.cs` — add `Task<int> SetGodModeAsync(bool)`,
  `Task<int> GetGodModeAsync()`, `Task<ProtectStateDto> GetProtectStateAsync()`;
  implement in `StubDumpService` (tests) too.
- `ui/UE5DumpUI/Services/DumpService.cs` — implement via the pipe client
  (mirror the teleport/debug-camera service methods).
- `ui/UE5DumpUI/Views/MainWindow.axaml` + `ViewModels/MainWindowViewModel.cs` —
  add the Protection tab (append last, like Teleport, so existing tab indices
  don't shift) + instantiate `ProtectionViewModel`.
- `ui/UE5DumpUI/Resources/Strings/en.axaml` — all new UI strings.
- C# constants mirror (the `Constants`/Grimoire-equivalent class) — mailbox op
  codes + command strings (single source, mirrored from the DLL table).

**Gate 8:** `build.ps1 -Target UI` (AOT-clean: no reflection-based APIs).

-----

## Phase 9 — UI tests

**New files:**

- `ui/UE5DumpUI.Tests/ProtectionScriptGeneratorTests.cs` — op codes/offsets
  match the constants table; LF-only; busy-check; ENABLE value=1 / DISABLE
  value=0; no helper-file references.
- `ui/UE5DumpUI.Tests/ProtectionViewModelTests.cs` (with `StubDumpService`) —
  connect/disconnect gating; toggle → badge mapping; error-code→message mapping;
  hotkey persist/restore; state query on connect.

**Gate 9:** `dotnet test "ui/UE5DumpUI.Tests/UE5DumpUI.Tests.csproj"` green.

-----

## Phase 10 — Docs & bookkeeping (same PR)

Per spec §10: `dll-spec.md` (exports + Solitar row), `pipe-protocol.md` (+3
cmds), `Mimic.h`/dll-spec mailbox section (`CMD_PROTECT=9`),
`naming-convention.md` (Solitar row + strike Solitär from "Available"),
`CLAUDE.md` architecture diagram + counts + `ProtectionPanel`, `architecture.md`
file counts (+1 .cpp/.h), `roadmap.md` capability row, `tips.md` recipe,
`dev-log.md` milestone. Bump `build_number.txt` via `build.ps1` (don't hand-edit).

**Final gate:** full `build.ps1` (DLL + UI + tests) green; AOT publish clean.

-----

## Live verification (after build, in-game)

Run the spec §9 smoke checklist. Minimum before calling it done:

1. A UE4 game **and** a UE5 game (LWC-irrelevant here, but covers `UBoolProperty`
   vs `FBoolProperty` layout): GodMode ON → standard-pipeline damage stops.
2. Log the resolved `bCanBeDamaged` offset + `FieldMask` per game (confirms the
   reflection path + the `±4/±8` probe, exactly like the cursor-bit probe).
3. Respawn with GodMode ON → still immune (re-assert worked).
4. Close game / disconnect with GodMode ON → clean exit (worker joined).
5. A known custom-health game → confirm the UI cross-link guidance is the right
   fallback (Value Search/Freeze).

-----

## <a name="merge-safety"></a>Merge safety (concurrent session)

Current `git status` shows another session editing
`dll/src/{Aura,Flamme,Frieren,Genau}.{cpp,h}`. Conflict surface:

| File | Risk | Mitigation |
|---|---|---|
| `Solitar.h` / `Solitar.cpp` (new) | **none** | brand-new files |
| UI files (new + edits) | **low** | concurrent work is DLL-side |
| `Frieren.cpp` / `Frieren.h` (Phase 2) | **high** — in their modified set | land Phase 2 **after** the concurrent work merges; the export wrappers are tiny + append-only |
| `Mimic.cpp/.h`, `Fern.cpp`, `Renge.h`, `Grimoire.h`, `dll/CMakeLists.txt` (Phases 3–6) | **medium** — not in their current set, but commonly touched | keep edits append-only (new enum members, new constants at end of list); rebase if needed |

**Recommended order:** Phase 1 (Solitar.*) + Phase 8/9 (UI, against a stubbed
service) can start immediately on a branch. Hold Phases 2–6 (shared DLL files)
until the concurrent DLL work merges to `dev`, then rebase and apply the
append-only wiring edits. This keeps the high-risk edits to a minimal,
late-applied diff.

-----

## Checklist

- [ ] P1 `Solitar.h` + `Solitar.cpp` (self-contained chain + bit write + worker)
- [ ] P2 Frieren exports (`UE5_SetGodMode`/`GetGodMode`/`GetProtectState`) + `StopWorker` in `UE5_Shutdown` ⚠
- [ ] P3 Mimic `CMD_PROTECT=9` + `ProtectOp` + `HandleProtect` ⚠
- [ ] P4 Fern pipe (`set_god_mode`/`get_god_mode`/`get_protect_state`) + Renge constants ⚠
- [ ] P5 Grimoire `kProtectReassertMs` ⚠
- [ ] P6 CMake: `Solitar.cpp` in `UE5Dumper` + `PROXY_SOURCES` ⚠
- [ ] P7 dll_helpers test for `ApplyBoolBit`
- [ ] P8 UI: DTOs (+JSON ctx) / IDumpService(+Stub) / DumpService / ViewModel / Panel / ScriptGenerator / tab / strings / constants
- [ ] P9 UI tests (generator + viewmodel)
- [ ] P10 docs + bookkeeping + build number
- [ ] Live verify (UE4 + UE5, respawn, clean exit, custom-health fallback)
