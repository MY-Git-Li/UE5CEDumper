# DumperTest — the UE sample UE5CEDumper is verified against

**What this is.** The C++ source for a stock **UE 5.4 Third Person** project carrying one actor
whose properties exist solely to be found by this dumper. It is not a game and not an AOB oracle —
[reference-builds.md](../../docs/reference-builds.md) covers those. This answers a third question:
*"does the dumper read this property type correctly?"*, with the answer written down in advance.

**Why it exists.** A large share of [todo.md § Pending live-game verification](../../docs/todo.md)
is blocked not on effort but on **finding a game that happens to contain the right UPROPERTY**:

| item | ⬜ since | the blocker, verbatim |
|---|---|---|
| Value Search `TSet`/`TMap` (V1a) | build **927** | *"needs a live game with such UPROPERTYs"* |
| Value Search `TOptional` (V1c) | build **942** | *"the field walk needs a live game with optional UPROPERTYs"* |
| Value Search `NumericAll` bytes | build **796** | *"a value that genuinely lives in an Int8Property"* |
| B28 CJK FText mojibake | build **2599** | *"any game with Chinese/Japanese UI text"* |
| B8 Fly deferred restore | build **2596** | *"needs a game that actually goes quiet when backgrounded"* |

Every one of those is free here, on demand, with a known expected value. A commercial title can
never promise "an even-length FText containing U+4E00 is on screen right now".

-----

## Build it (one pass, ~20 minutes)

> **The project MUST be named `DumperTest`.** The sources use the `DUMPERTEST_API` export macro,
> which UHT derives from the module name. If you want another name, rename the macro in all four
> files to `<YOURNAME>_API` first.

1. **UE 5.4 Editor → New Project → Games → Third Person → C++** (not Blueprint) → name it
   `DumperTest` → Create. Let it compile and open once, then close the editor.
2. Copy `DumperTest/Source/DumperTest/*.h` and `*.cpp` from this folder into the generated
   `Source/DumperTest/`.
3. Merge `DumperTest/Config/DefaultEngine.ini.add` into the generated `Config/DefaultEngine.ini`.
   **Merge, do not replace** — the template's own file holds the default map and the project ID.
4. **Build from the command line. Do not build the solution in Visual Studio** — see the trap below.

   ```bash
   "C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" DumperTestEditor Win64 Development -Project="D:\Unreal Projects\DumperTest\DumperTest.uproject" -WaitMutex
   ```

   *Verified 2026-08-05: 27 actions, **30 s**, zero errors.* Then double-click the `.uproject`.
   (Double-clicking it first also works — the editor offers to rebuild out-of-date modules and takes
   the same path.)

   > ### ⚠ `Build.bat … exit 6` and a wall of "找不到 …csproj 的專案資訊"
   >
   > **Symptom:** building the UE-generated `.sln` fails on `DotNetPerforceLib` /
   > `EventLoopUnitTests` with *"目標 Framework 'net6.0' 已不受支援"* and project-info-not-found for
   > every `EpicGames.*` shared library.
   >
   > **Cause:** those are the **engine's own C# Programs**, which the generated solution includes and
   > which target **net6.0**. A machine whose only SDK is .NET 8/9/10 has no net6.0 targeting pack, so
   > NuGet cannot restore them. Visual Studio **2026** makes it worse by running a one-way upgrade on
   > the solution first (`UpgradeLog.htm` + `Backup\` appear next to the `.uproject`).
   >
   > **None of it is needed to build a game module.** UBT ships **precompiled**
   > (`Engine/Binaries/DotNET/UnrealBuildTool/UnrealBuildTool.dll`) and UE bundles its own .NET at
   > `Engine/Binaries/ThirdParty/DotNet/6.0.302/`. The command above touches neither the solution nor
   > the system SDK.
   >
   > **Do NOT install the .NET 6 SDK for this.** It is end-of-life, and it would only satisfy engine
   > programs you will never run.
   >
   > **The C++ toolchain was never the problem.** UBT reported *"Using Visual Studio 2022 14.38.33145
   > toolchain (…\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.38.33130)"* — it found a
   > VS2022-era MSVC inside the VS 2026 install and was happy. UE 5.4's UBT knows compilers only up to
   > `VisualStudio2022` (`UEBuildWindows.cs:138`), so if you ever *do* need the IDE, open the solution
   > with **VS 2022** and build **only the `DumperTest` project** (right-click → Build), never
   > *Build Solution*. The `.sln` is disposable — regenerate it from the `.uproject` context menu.
5. Press Play. The output log must show
   `[DumperTest] ADumperTestActor ready at 0x…` — if it does not, nothing below will work and the
   problem is the subsystem, not the dumper.
6. **Package twice**: Platforms → Windows → Build Configuration → **Shipping**, Package Project;
   then again with **Development**.

> **Do NOT package into `D:\UE_Analyze_Data\Varies Version builds\`.** `inventory_builds.py`
> and `preflight.py` treat that tree as the AOB corpus and CI asserts its row counts; a new folder
> there would drift them. Use a sibling such as `D:\UE_Analyze_Data\DumperTest\5.4\Shipping\`.

**Launch it windowed** so alt-tab is one keystroke — several checks depend on backgrounding it:

```bash
DumperTest.exe -windowed -ResX=1280 -ResY=720
```

**Why two configs.** Shipping vs Development on the *same source* is the config-only A/B that
[todo.md's self-built-samples section](../../docs/todo.md) calls the highest-value first cell: it
*measures* which reflected UFunctions the cooker hollows out (`UCheatManager::Fly/God/Slomo` invoke
successfully and do nothing in Shipping) instead of rediscovering it per game.

-----

## Where the packaged binary lives — NOT in git, and not in CI

**Decided 2026-08-05. Only the source is committed; the package is not.** Both halves have a number
behind them.

**Not in git.** A packaged 5.4 ThirdPerson set measures **583 MB** (the three configs of the existing
5.4 corpus row); one Shipping package alone is 100–200 MB. This repo's entire `.git` is **180 MB**, so
committing even one config would roughly double it — permanently, because history cannot be pruned
without a rewrite. And it would be committing a **build artifact whose source is already here**.

The repo already has the right pattern and it is worth copying exactly: the AOB corpus binaries live
**outside** the repo, and what is committed is the small thing that *verifies* them —
`tools/ghidra/identity/` + `memory-maps/`, **668 KB** total. See
[corpus-preservation.md](../../docs/corpus-preservation.md). So:

* **binary** → outside, e.g. `D:\UE_Analyze_Data\DumperTest\5.4\{Shipping,Development}\`
  (**not** in `Varies Version builds\` — `inventory_builds.py`/`preflight.py` treat that tree as the
  AOB corpus and CI asserts its row counts).
* **repo** → this source, plus a note of the engine version, the exact build command and the packaged
  exe's hash, so *"is the package I am testing built from this source?"* has an answer. Without that,
  a stale package silently tests yesterday's property zoo and looks like a dumper regression.

**Not in CI, and size is the lesser reason.** CI has no UE 5.4 install (tens of GB), no GPU and no
display — but the real blocker is that **what this sample tests is a live process being injected into
and walked**: a running game, a ticking game thread, the UI or CE attached. That is the same class of
thing [todo.md § Pending live-game verification](../../docs/todo.md) exists for, and the existing
`check_live_verification.py` gate only checks that the register is *well-formed* — it does not run,
and cannot run, the verification itself. Nothing about packaging this sample changes that.

> **What CI could cheaply do, if it is ever wanted (not built):** a source-level drift gate asserting
> the expected-value table in this README still matches the literals in `DumperTestActor.cpp`. That is
> audit #4's 4a root cause — *the report and the reality are computed by different code paths* —
> applied here: change `I32 = 1234567` in the .cpp without changing the table and the next tester
> scans for a number that is not there. ~40 lines of Python, and it would be the seventh gate.

-----

## Reflection is confirmed complete (2026-08-05, build verified)

Read out of the generated `DumperTestActor.gen.cpp` — this is the evidence that the zoo actually
reaches UE reflection rather than merely compiling:

| emitted param type | count | covers |
|---|---|---|
| `FTextPropertyParams` | **9** | the 8 actor FTexts + `UDumperTestPayload::PayloadText` |
| `FStrPropertyParams` | 6 | 4 `Str_*` + `PayloadString` + `Opt_Str_Set`'s inner |
| **`FGenericPropertyParams`** | **4** | **the four `TOptional`s** — `FOptionalProperty` has no dedicated params type; each also emits an `_Inner` (`Opt_Int_Set_Inner` = `EPropertyGenFlags::Int`) |
| `FSet` / `FMap` / `FArray` | 1 / 2 / 2 | `Set_Int`, both maps, `Arr_Int` + `Arr_Struct` |
| `FInt8` / `FInt16` / `FUInt16` / `FInt64` | 1 each | the byte/width families |
| `FBoolPropertyParams` | 4 | 3 bitfields + `bPlainBool` |
| `FByte` / `FEnum` / `FFloat` / `FDouble` / `FStruct` / `FObject` / `FName` | 3 / 1 / 3 / 1 / 2 / 1 / 2 | |

> A grep for these must include digits — `F[A-Za-z]*PropertyParams` silently misses
> `FInt8`/`FInt16`/`FInt64` and makes the byte families look absent.

-----

## What to expect — these are the acceptance criteria

Find the actor with **Instances → `DumperTestActor`**, then open it in Live Walker.

### B28 — FText (the reason this project exists)

Trigger, restated: an FText whose character count is **EVEN** and which contains a character whose
**low byte is 0x00** (一 U+4E00, 最 U+6700, 言 U+8A00, 退 U+9000). In UTF-16LE such a character is
stored `00 4E` — a NUL at an even byte offset.

| field | value | why it is here |
|---|---|---|
| `Text_Even2_OneNull` | 統一 | 2 chars, one U+xx00 — **primary trigger** |
| `Text_Even2_TwoNull` | 一言 | 2 chars, bytes `00 4E 00 8A` — **strongest trigger** |
| `Text_Even4_TwoNull` | 統一言語 | 4 chars, two U+xx00 |
| `Text_Odd3_OneNull` | 走一步 | **CONTROL** — odd length. If only this renders, parity is still being used as an encoding signal |
| `Text_Even6_NoNull` | 日本語テスト | **CONTROL** — even, but no low byte is 00. Even length alone must trigger nothing |
| `Text_Ascii` | `DumperTest FText ASCII` | **CONTROL for the other direction** — a fix that swings to always-UTF-16 breaks this |
| `Text_Localized` | 統一言語 | Same glyphs, different `FTextHistory` (LOCTEXT). Disagreement with `Text_Even4_TwoNull` means the fault is history traversal, not decoding |
| `Text_Empty` | *(empty)* | the empty display-string path |
| `Name_Cjk` | 統一 | FName holding CJK — the FNamePool path (Serie), which is neither reader above |
| `Str_*` | same four strings | **CONTROL GROUP** — FString never had B28. If an `Str_` is wrong too, the fault is not B28 and the FText result means nothing |

**PASS** = every `Text_*` reads as CJK. **FAIL** = short ASCII punctuation soup (`,{1`, `-N?e`).

> **What this does NOT cover:** Star Trek Voyager stores its FText as **UTF-8**, which is a licensee
> deviation no stock UE build produces. That counter-check still needs STVoyager.

### Value Search / containers

| field | value | check |
|---|---|---|
| `Set_Int` | `{1337, 4242, 8888}` | scan **4242** → row renders as `Set[idx]` |
| `Map_NameToInt` | Alpha:111 Beta:222 Gamma:333 | scan **222** → `Map.Value[idx]` |
| `Map_IntToFloat` | 1:1.5 2:2.5 3:3.5 | non-FName key shape |
| `Arr_Int` | `{10,20,30,40,50}` | |
| `Arr_Struct` | 2 × `FDumperTestStat` — `StatName` Attack/Defence, `Value` 7777/6666, `Label` an FText | struct-element container — the deep-descent level, with an FText inside it |
| `Opt_Int_Set` | **24680** | V1c — appears under the optional's field name; Next Scan prunes |
| `Opt_Float_Set` | 99.5 | |
| `Opt_Str_Set` | `OptionalPresent` | |
| `Opt_Int_Unset` | *(unset)* | **NEGATIVE criterion** — a scan for **0** must NOT surface it (the `bIsSet` gate) |

### Numerics, flags, layout

| field | value | check |
|---|---|---|
| `I8_Neg` | **-5** | the unit-tested boundary: Int8 yes / UInt8 no |
| `U8_Small` / `U8_Max` | 1 / **255** | NumericAll byte family; also the "is the result volume usable or does it drown the panel" UX judgement no test can make |
| `I16` / `U16` | -12345 / 54321 | |
| `I32` / `I64` | 1234567 / 8899001122334455 | |
| `F32` | **513.36** | the Round/Trunc/Ceil worked example |
| `F64` | 2718.281828 | |
| `bFlagA` / `bFlagB` / `bFlagC` | 1 / 0 / 1 | three bitfields in one byte — bool masks |
| `Grade` | `Elite` (=2) | enum with a **hole** at 3..6 (`Legend`=7), so index≠value cannot pass by accident |
| `FixedArr` (8 elements) | 100..800 | `ArrayDim > 1` — a different property shape from TArray |
| `Health` — `BaseValue` / `CurrentValue` | Base 100, Current ticking | nested StructProperty in GAS-attribute shape → also the "Flatten GAS attributes" CE-export toggle |
| `Payload` → `PayloadText` / `PayloadString` / `PayloadValue` | 統一言語, same as FString, 909090 | Related Objects edge · Locate-in-GWorld through a pointer · Solide force-to-null (strong ptr, so allowed) |
| `RawInt` / `RawFloat` / `RawDouble` | 0x5A5A5A5A / 777.75 / 31415.926535 | **not** UPROPERTY — the interior holes "Guess What" and the Native-C scan must find |

### Group Scan / Snapshot Mode B (temporal)

A 1 Hz timer drives exactly the documented hard case — *groups need `Unchanged`*:

| field | behaviour |
|---|---|
| `Health.CurrentValue` | falls 1/sec, wraps 1 → 100 (so both **Decreased** and **Increased** occur) |
| `Health.BaseValue` | **never moves** — the `Unchanged` slot a group match needs |
| `TickCount` | rises monotonically |
| `FrozenInt` | 424242, never written again |

### B8 / Grausam — the backgrounding pair

`t.IdleWhenNotForeground=1` is set in the ini, so alt-tabbing away **guarantees** the game thread
stops ticking (`ShouldUseIdleMode()` needs only `IsGame() && SupportsWindowedMode() && cvar &&
!HasFocus()`, and the first two are automatic).

* **B8** — Teleport → Fly ON + Noclip → fly through a wall → alt-tab to the UI, wait >500 ms →
  Disable. **PASS** = `Fly: DISABLED but the pawn's collision is still OFF (game thread
  unresponsive)`, then on refocus `Fly: game thread resumed after N ms — pawn collision restored`.
* **Grausam** — with the foreground lock ON, idle mode must **never** engage while backgrounded.
  The lock's whole job is keeping `FApp::HasFocus()` true through the `WM_ACTIVATEAPP` rewrite, so
  this is a positive test rather than "it seemed to keep working".

### Free, no code needed

* **B29 manual half** — this is a UE game folder. Drop a third-party `dxgi.dll` (ReShade) in and
  click *Inject && Connect*. No commercial game required.
* **B5 active half** — launch through the deployed `version.dll` proxy, connect the UI, click Scan,
  and fire a CE mailbox command while the scan runs.
* **Audit #3 M1/M2/M3** — See-Through needs visible geometry and a pawn; the Third Person template
  has both, and unlike a game the wall is always in the same place.

-----

## What this sample can NOT settle

Stating these so nobody spends a packaging cycle on them:

* **B4** (CE mailbox after the UI dies), **B26**, **B16**, the `.CT` `reg.exe` fallback — nothing
  UE-specific; any process will do.
* **B13/B41** (Recycle Bin) — not UE at all.
* **B2** (symbol-export GWorld) — *possibly*: check whether the **Development** package exports
  `GWorld`. If it does, this replaces the dependency on owning Satisfactory. Unverified.
* **B18** (Extra Scan cancel) — needs GObjects to miss by AOB. Pick a corpus row already known to
  fail rather than trying to engineer it here.
* **Anything about licensee forks** (MindsEye, Avowed) — a stock sample is by definition not a fork.
* **STVoyager's UTF-8 FText** — a licensee deviation; no stock build reproduces it.

-----

## Traps

* **Encoding.** Every CJK literal is a `\uXXXX` escape and the files carry a UTF-8 BOM. Both, on
  purpose: a BOM-less file is re-read through the system code page (on a zh-TW machine CP950, whose
  lead bytes can swallow the following character), which would corrupt exactly the strings B28 is
  about — and the test would then be measuring MSVC, not the dumper. **If you edit a literal, keep
  it escaped.**
* **The actor is spawned by a `UWorldSubsystem`, not placed in the level.** A level is a binary
  asset: not diffable, not reviewable, and "remember to drag the actor in" fails silently in a way
  that looks like a dumper bug. Nothing here requires an asset edit.
* **`Opt_Int_Unset` must stay unset.** It is the only negative criterion in the file; initialising
  it "for tidiness" deletes the test.
* **If UHT rejects a `TOptional`** on a different engine version, comment out those four fields and
  their initialisers — everything else is independent. On 5.4 they are confirmed fine:
  `FOptionalProperty` exists (`PropertyOptional.h`), UHT resolves it (`UhtOptionalProperty.cs`), the
  only inner-type rule is `CanBeContainerValue`, and the engine itself ships `TOptional<FBox>` and
  `TOptional<uint32>` UPROPERTYs.
