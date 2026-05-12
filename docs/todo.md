# Todo

Prioritized open work. **Read this when deciding what to do next.**
Move items to [dev-log.md](dev-log.md) once they ship; update
[roadmap.md](roadmap.md) when capability state changes.

> Format: each item has **Effort** (S/M/L/XL — rough; S=hours, M=1
> session, L=multi-session, XL=spans weeks), **Risk** (low/med/high —
> likelihood of breaking existing behaviour or introducing perf
> regression), and **Why** (the reason it's on the list). Strike-through
> when shipped.

-----

## Active plan: Call-UE-function strengthening

The "Call UE function" capability is currently the weakest link in the
"discover → use" workflow. Five-step plan agreed 2026-05-10:

### 1. AA-Script export from UI ([3a]) — ✅ shipped (build 590-596)

**Effort**: M (actual: ~M as estimated) | **Risk**: low (no regressions)

LiveWalker UFunction rows gained a third button **AA(Baked)** that
opens the existing param dialog in `CopyBakedScript` mode and ships
a non-interactive AA Script via AOBMaker / clipboard. Sister to the
existing `Generate Script` (in-CE form) and `Pipe Invoke` (in-app
test) buttons.

Architecture (revised from the original plan after design review):
- **Helper file in CE table** — instead of inlining the mailbox
  protocol in every AA Script, the generated script depends on
  `ue5_invoke_helper.lua` being embedded in the user's .CT via
  Cheat Engine's `Table -> Add File...` menu. The script uses
  `findTableFile()` + `load()` to resolve the helper at runtime. No
  filesystem fallback — explicit error + setup instructions if the
  file is missing.
- **Tools menu export** — `Tools -> Export CE Helper Lua File...`
  streams the embedded helper to a user-chosen path so they can drop
  it next to their .CT.
- **Re-declaration safe** — helper functions use the
  `if not invokeUFunction then function ... end
  registerLuaFunctionHighlight('invokeUFunction') end` pattern so
  multiple AA scripts loading the same helper don't redefine it.
- **Print discipline** — generated scripts are silent on success
  (auto-close the lua engine via `synchronize(getLuaEngine().Close())`
  per the user's hygiene rule), print + showMessage on error.

Files touched:
- `scripts/ue5_invoke_helper.lua` (new, ~285 lines)
- `ui/UE5DumpUI/Models/BakedParamValue.cs` (new)
- `ui/UE5DumpUI/Services/BakedScriptGenerator.cs` (new, ~250 lines)
- `ui/UE5DumpUI/Services/HelperLuaResource.cs` (new)
- `ui/UE5DumpUI/Views/InvokeParamDialog.cs` (`InvokeDialogMode` enum,
  `Copy AA Script` button, `CollectBakedValues` helper)
- `ui/UE5DumpUI/ViewModels/LiveWalkerViewModel.cs` (`CopyBakedScriptCommand`)
- `ui/UE5DumpUI/Views/LiveWalkerPanel.axaml` (third button column)
- `ui/UE5DumpUI/Views/MainWindow.axaml` (Tools dropdown)
- `ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs` (`ExportCeHelperLuaCommand`)
- `ui/UE5DumpUI/UE5DumpUI.csproj` (EmbeddedResource link to helper)
- `ui/UE5DumpUI/Resources/Strings/en.axaml` (button + tooltip + Tools menu strings)
- `ui/UE5DumpUI.Tests/InvokeScriptTests.cs` (+36 test cases:
  baked-render correctness for each UE type, struct flattening,
  unparseable-input fallback, Lua-quote escaping, helper resource
  reachable from assembly manifest)

Tests: 597 -> 633 (504 -> 540 C# + 62 dll_helpers + 31 utf8_helpers).

### 2. Interesting-functions finder ([3c]) — ✅ shipped (build 597-607)

**Effort**: M (actual: ~M as estimated) | **Risk**: low (no regressions)

New "Interesting Funcs" tab between Property Search and Game Classes.
Per-row Live + AA(B) actions. Architecture decisions actually shipped:

- **Scoring is UI-side** (not DLL) so keyword tables can be tuned
  without DLL rebuild. DLL just enumerates via new `list_all_functions`
  pipe cmd.
- **Scoring file**: `KeywordScoringTable.cs` -- 5 visible categories
  (Stats / Inventory / Movement / Combat / Utility) + an
  ExplicitMovementCheats sub-bucket (NoClip/Fly/God/Ghost/Invincible/
  Invisible at +8 per-hit) so explicit movement cheats outrank a
  Utility-noisy `DebugCheatManager` class name.
- **Substring-noise lesson** (caught by unit tests): short acronyms
  like HP/MP/SP/XP/TP collide with engine-spam words ("Component"
  contains "mp"). Dropped them from the keyword tables; full forms
  only (Health/Mana/Stamina/Experience/Teleport).
- **Tab insertion shifts ClassStruct from index 4 -> 5**; updated
  `GameClassFilter.NavigateToClassStruct` accordingly.
- **Cross-tab nav**: Live button uses FindInstancesAsync -> non-CDO
  pick -> Live Walker; falls back to ClassStruct (with status hint)
  when class is CDO-only. AA(B) button reuses the InvokeParamDialog
  CopyBakedScript mode from step 1.

Files touched:
- `dll/src/Aura.h` + `Aura.cpp` (`AllFunctionEntry` /
  `EnumerateAllFunctions`)
- `dll/src/Renge.h` + `Fern.cpp` (CMD_LIST_ALL_FUNCTIONS handler)
- `ui/UE5DumpUI/Models/AllFunctionsResult.cs` + `ScoredFunctionRow.cs`
  (new)
- `ui/UE5DumpUI/Services/KeywordScoringTable.cs` +
  `CategoryDisplayConverter.cs` (new)
- `ui/UE5DumpUI/Services/DumpService.cs` + `Core/IDumpService.cs`
  (`ListAllFunctionsAsync`)
- `ui/UE5DumpUI/ViewModels/InterestingFunctionsViewModel.cs` (new)
- `ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs` (cross-tab handlers
  + `_aobMaker` field for AA Script delivery)
- `ui/UE5DumpUI/Views/InterestingFunctionsPanel.axaml` + .cs (new)
- `ui/UE5DumpUI/Views/MainWindow.axaml` (new TabItem; ClassStruct
  shifted)
- `ui/UE5DumpUI/Resources/Strings/en.axaml` (~14 new strings)
- `ui/UE5DumpUI.Tests/CsxExportServiceTests.cs` (un-seal
  `StubDumpService`, mark `ListAllFunctionsAsync` `virtual`)
- `ui/UE5DumpUI.Tests/KeywordScoringTableTests.cs` + `InterestingFunctionsViewModelTests.cs`
  (+60 test cases)

Tests: 633 -> 693 (540 -> 600 C# + 62 dll_helpers + 31 utf8_helpers).

### 3. UFunction metadata exposure ([4]) — ❌ skipped (build 608 research)

**Effort**: M (estimated) | **Risk**: med (now confirmed: ~total no-op)

Original plan: read `UField::MetaDataMap` to surface Blueprint
`DisplayName` / `ToolTip` / `Category` / `Keywords`.

Research finding ([dev-log build 608](dev-log.md)): the metadata map
is `#if WITH_METADATA` (= `WITH_EDITORONLY_DATA`). On Windows/Mac/Linux
Shipping builds the macro is `1` so the `MetaDataMap` POINTER exists,
but the cooker strips the actual content during cook -- `GetMetaData()`
returns empty string at runtime in every cooked Shipping game.
Verified against Engine/Source/Runtime/CoreUObject/Private/UObject/Field.cpp
+ Core/Public/Misc/CoreMiscDefines.h.

Implication: implementing this would only pay off on DebugGame /
Development-config builds, which a cheat-engine user almost never
encounters. The work was estimated at ~250 LoC + per-version offset
table -- not worth shipping for ~zero real-world value.

**Pivot**: did B (CamelCase tokeniser) instead -- closes the substring-
noise gap from the v1 finder so short acronyms (HP/MP/SP/XP/TP) can
work safely as keywords, materially improving the existing scorer
without needing metadata at all. Shipped build 608+.

If a user reports the finder missing obvious cheat-relevant functions
in a real game, revisit this -- but expect to need a per-version
`MetaDataMap` offset table only useful for testing dev-config builds.

**Effort**: M | **Risk**: med

Blueprint-derived UFunctions carry a metadata map (`UFunction::*Meta*`
calls in UE source) with `DisplayName` / `ToolTip` / `Category` /
`Keywords`. Currently we only expose the cooked function name. Surfacing
metadata gives:

### 4. Update interesting-functions + existing function lists with metadata ([3c rev2]) — ❌ skipped (depended on 3)

Skipped because step 3 was skipped — see step 3 entry above. Tokeniser
work delivered the same end value (better keyword matching) without
needing metadata.

The "stale step 3" original spec preserved here as historical context:

- Better display strings ("Add Player Currency" beats `AddMoney`)
- A **second corpus** for the keyword scorer in step 2 (matches against
  Category `Player|Stats|Combat` etc. are higher-signal than matches
  against function names alone, which can collide with engine-internal
  helpers)
- Tooltip text in the UI invoke dialog

Implementation notes if a user requests this anyway:
- `dll/src/Ubel.cpp` UFunction reader — probe `MetaDataMap` (TMap<FName,
  FString>) at the version-specific offset
- `Aura::WalkClass` augment per-function row with `metadata: { displayName,
  toolTip, category, keywords }`
- Pipe schema bump
- UI: new columns in the function lists, tooltip wired
- Pre-req: read UnrealEngine source for the `UMetaData` /
  `UFunction::FindMetaData` chain; build per-version offset table
  (UE 4.27, 5.0, 5.4, 5.7 minimum)

Original step 4 incremental rev2 spec follows for completeness:

- Keyword scorer in step 2 also matches against `DisplayName`,
  `Category`, `Keywords` metadata fields (each weighted higher than
  function-name match because they are author-curated, not cooked)
- Existing function lists (Class Structure, PropertySearch results)
  show `DisplayName` as primary label with cooked name as small
  secondary text

### 5. UI invoke dialog overflow fix ([3b]) — ✅ shipped (build 609)

**Effort**: S (actual: ~XS) | **Risk**: low (no regressions)

The actual issue wasn't a missing ScrollViewer (that was already there
since the dialog was first written). It was the hard `MaxHeight=700`
window cap that prevented users on big monitors from resizing the
dialog larger to see all params.

Fix:
- Window `MaxHeight` 700 → 1100; added `Height=480` default + `MinHeight=240`
- `SizeToContent = SizeToContent.Height` so the dialog grows to fit
  the form, then caps at MaxHeight
- ScrollViewer wrapping the param panel gained `MinHeight=200` -- when
  the FIRE result label expands after a successful invoke, DockPanel
  would otherwise let the bottom panel squish the scroll area down to
  a sliver. 200px floor keeps ~6 param rows visible regardless.
- Skipped the "Reset to defaults" button -- v1 use case for this is
  unclear; can add if requested.

File touched: `ui/UE5DumpUI/Views/InvokeParamDialog.cs` (~10 lines).

### 6. One-click helper inject into open CE table — ✅ shipped (build 611)

**Effort**: S (actual: ~S) | **Risk**: low (additive — old export menu kept as fallback)

Removes the manual save-to-disk + `Table -> Add File...` dance for
brand-new users by wiring a new `InjectTableFile` pipe command into the
AOBMaker CE Plugin and a matching `IAobMakerBridge.InjectTableFileAsync`
on the UE5DumpUI side.

Plugin side (`D:\Github\AOBMaker\plugins\CEPlugin\src\pipe_server.cpp`):
`HandleInjectTableFile` runs `findTableFile` (delete-if-exists) +
`createTableFile` + `Stream.write` + `Stream.Size` verify under
`synchronize` so all CE Lua APIs execute on CE's main thread. Long-bracket
level is chosen dynamically so any payload is safe (even one containing
`]==]`). Protocol constants added to `protocol.h`; routed from
`HandleClient`.

UE5DumpUI side: new `Tools -> Inject Helper into Current CE Table` menu
item -> `MainWindowViewModel.InjectCeHelperLuaCommand` -> probe
`CheckAvailabilityAsync` -> `InjectTableFileAsync(fileName, content)` ->
status text covers all four user-visible end-states (no bridge, CE not
running, success, failure). Inject path uses a 15 s response timeout
(vs. 5 s default for navigation calls) to give synchronize round-trip
headroom for ~10 KB payloads.

Tests: integrated via cherry-pick onto current dev. Final total
670 C# xunit (+8 from this change after subclassed-stub bridge
addition) + 62 dll_helpers + 31 utf8_helpers = **763 total**.
Coverage: AobMakerInjectTableFileTests (3 — wire-model serialization,
relaxed encoder for single quotes, bridge service arg validation),
MainWindowInjectHelperTests (4 — all four end-states via recording bridge),
plus FakeAobMakerBridge stub gained the new method (+1 test indirectly).

Spawned session worked in a separate worktree from `aa2ac0d`; cherry-
picked `44a3943` onto current dev as `67fd61b` rather than merging,
keeping the linear history. Doc-only conflicts (dev-log.md / roadmap.md /
todo.md "latest" headers) resolved by keeping both entries with
this one bumped to build 611.

-----

## Pending live-game verification

Features that shipped + unit tests pass but need real game smoke tests
before we can declare them solid on multiple titles.

### ~~ProcessEvent vtable fix — partial confirmation on ES2 (UE 5.5)~~ — ✅ FULL VERIFICATION (build 648, 2026-05-11)

**Effort**: 0 (done) | **Risk**: — | **Why**: Verified end-to-end on
**two UE versions** spanning the original repro range:

| Game | UE | Actual PE slot | Old hardcoded | Validator | Scenarios passed |
|---|---|:---:|:---:|---|---|
| EverSpace 2 | 5.5 | `vtable+0x278` | `0x228` (off 10 slots) | 2360 fires / 1500ms | KismetMath Add_IntInt=7, Multiply_DoubleDouble=12, InventoryLib::GetTotalCargoSpaceOfShip=73 |
| The Artisan of Glimmith (Geri) | 4.27 | `vtable+0x220` | `0x218` (off 1 slot) | 1260 fires / 1500ms | KismetMath Add_IntInt=7, Multiply_FloatFloat=12, **CharacterMovementComponent::GetMaxJumpHeight=89.99 (instance method, game-thread dispatch)**, **PlayerCameraManager::GetCameraLocation=FVector (struct return, game-thread dispatch)** |

**KismetMathLibrary "stub" hypothesis is now falsified on both UE
versions** (see retracted [feedback_kismet_stubs.md]). Both
static-native fast path (KismetMath) AND game-thread dispatch (Geri
scenarios 3 + 4 are instance methods on CharacterMovementComponent /
PlayerCameraManager) work correctly.

Remaining nice-to-have verification (lower priority — fix is already
considered solid):
- A UE 4.18-4.24 game (smaller vtable, lower slot offset) to make
  sure the pattern scanner's `[0x100, 0x300]` window catches lower
  slot positions. Pick from Octopath Traveler / IDOLM@STER STARLIT
  SEASON / DQ XI S.
- A custom publisher fork (Square Enix DQ/FF7 series) to confirm the
  pattern scan finds PE even when the binary has been heavily
  modified post-cook.

### Mimic: zero ReturnValue slot before invoke so verify-mode dumps are unambiguous

**Effort**: S | **Risk**: low | **Why**: ES2 live test 2026-05-11
showed `GetTotalCargoSpaceOfShip(0)` returning Before/After dumps:
```
Before: 00 00 00 00 49 00 00 00
After : 00 00 00 00 49 00 00 00   <- 0x49 = 73 in ReturnValue slot
```
The `0x49` was identical pre- and post-invoke, so we can't tell apart
"function ran and wrote 73" from "function ran but didn't touch
ReturnValue (leaving stale 73 from previous call)". Fix:
`Mimic::HandleInvoke` should overwrite the ReturnValue slot with a
sentinel (e.g. `0xCDCDCDCDCDCDCDCD`) or zero before calling PE. Then
the After dump unambiguously shows what PE wrote — if it's still the
sentinel, PE didn't write the return slot. Affects: all verify-mode
AA Scripts. Trivial 2-line patch in `dll/src/Mimic.cpp`'s static-
native fast path + the game-thread dispatch path.

### CE Lua hang during AA Script activation (ES2 2026-05-11 session 2)

**Effort**: M (mitigation) | **Risk**: low | **Why**: After restarting
the game (new proxy DLL load) + UE5DumpUI, the user tried Scenario 3
again. DLL stayed healthy: `find_instances InventoryLib: 1 found` at
22:27:23, then 72-second silence with no `Mailbox: received cmd=4`,
ending with `Client disconnected` at 22:28:35 + `PipeServer Stopped`
at 22:28:43. The AA Script never reached the mailbox — CE Lua either
froze or showed a hidden error dialog. Mitigations to consider:

1. **Re-arm helper-injected check on every UI Connect**: when UI
   connects to a fresh DLL session, optionally re-prompt the user
   (or auto-inject) the `ue5_invoke_helper.lua` if AOBMaker is
   reachable. Currently the helper persists in the .CT across CE
   restarts but NOT across game restarts in proxy mode (since proxy
   doesn't touch the CT). Easy to forget.
2. **Mailbox heartbeat on the AA Script side**: have the generated
   AA Script `print()` a "starting" line before writing to the
   mailbox, so the CE Lua log shows progress even if the mailbox
   write later hangs.
3. **Timeout/watchdog on the CE Lua side**: less feasible because
   we don't control CE's Lua engine. But we can add an explicit
   `if not g_invokeMailbox then showMessage('Helper not loaded —
   run Tools → Inject Helper first'); return end` early-exit in
   the helper itself.

We can't distinguish AA-Script-error from CE-Lua-freeze from our DLL
side because the mailbox just never receives anything. So this is
primarily a UX hardening task, not a correctness bug.

### Static-native ProcessEvent fast path (build 636)

**Effort**: S | **Risk**: low | **Why**: Verified on ES2 (logs show
`static-native fast path (flags=0x14022403, bypassing GameThreadDispatch)
... INVOKE result=0`). Need to confirm on a game where the user is
actively playing (game thread pumping) so we can compare static-native
fast path latency vs. instance-method GameThreadDispatch latency on the
same session. Also confirm that **stateful** UFunctions (BlueprintEvent
/ RPC / non-static) still correctly route through GameThreadDispatch
and don't fall into the fast path by accident.

Test plan:
- Active game session (player moving), Interesting Funcs -> any static
  BFL function (Stats/Math) -> AA(B) + Verify -> should print result
  in <50ms regardless of game idle/active state
- Same session, instance method (e.g. PlayerController::* setter) ->
  AA(B) + Verify -> uses GameThreadDispatch; should also succeed if
  game thread is active. Idle test (let game sit on title screen) ->
  expect timeout `-5` for the instance method but **not** for the
  static native helper.

### FPROPERTY_FLAGS offset fix (build 642)

**Effort**: S | **Risk**: med | **Why**: The +8 -> +4 offset fix
flipped how the walker reads CPF_ReturnParm / CPF_OutParm / CPF_Parm
on **every** UFunction parameter across **every** UE version. The
verify-mode PARAMS table is now correctly emitting ReturnValue as a
return slot (not as an input), but the same flag-read also feeds
`docs/dll-spec.md` UFunction listings, the Class Structure tab's
function display, USMAP export, etc. Sweep the 12+ tested games
quickly to confirm no regression on any of them.

Test plan:
- For each game in [docs/roadmap.md](roadmap.md#tested-games-last-verified-2026-05-10),
  open Class Structure on a known UClass with mixed input/output params
  (e.g. Character::AddMovementInput, PlayerController::GetMousePosition)
  and check the Functions section's Return column populates.
- Quick sanity: Interesting Funcs -> any function with a return value
  -> AA(B) -> generated PARAMS should NOT include ReturnValue (used
  to before this fix).

### Verify Return Value diagnostic mode (build 637 / refined 644)

**Effort**: S | **Risk**: low | **Why**: Live-tested on ES2 and worked
end-to-end (mailbox resolution + Before/After dump + decoded scalar
print). Need to confirm pointer-return functions show 0x prefix
correctly and FString-return functions show the "see After: dump
above" hint.

Test plan:
- Pick a function returning UObject* (e.g. `GetWorld`, `GetGameInstance`,
  `GetOuter` if BC) -> Verify on -> expect `(pointer@N) = 0xFFFFFFFF...`
- Pick a function returning FString (e.g. `GetGameName`) -> Verify on ->
  expect `(fstring@N, size=16B) -- complex return; see After: dump above`
  and dump shows non-zero ptr+count when the call succeeded.

-----

## ~~CRITICAL: ProcessEvent vtable detection is wrong~~ — ✅ shipped (build 648)

**Effort**: M (actual: ~M) | **Risk**: med (no regressions in 786-test
suite; live-game smoke test pending — see "Pending live-game
verification" section above).

Replaced the version-table vtable detector with a **function-body
pattern scan** modeled on Dumper-7's approach (vendor/Dumper-7/Dumper/
Engine/Private/OffsetFinder/Offsets.cpp:15-74). Iterate vtable slots in
the `[0x100, 0x300]` window, read each candidate function's first 0xF00
bytes, look for two `TEST [reg+disp32], imm32` instructions that
ProcessEvent uniquely contains:

- Pattern 1 (within first 0x400 bytes): `imm32 = 0x00000400` (FUNC_Native test)
- Pattern 2 (within first 0xF00 bytes): `imm32 = 0x00400000` (high-flag test)

`disp32` points at `UFunction::FunctionFlags` (`0x88..0xC0` across UE
versions) — we wildcard those bytes so the scan is FunctionFlags-offset
agnostic. The old UE-version-table heuristic is kept as a `LOG_WARN`
fallback path for unusual compiler output (heavily-optimised LTO,
custom publisher fork).

**Belt-and-braces validation** (the real safety net): `Stark` now
exposes `GetHookFireCount()`, an atomic counter ticked at the top of
`HookedProcessEvent`. After `InstallHook` succeeds, `Frieren::
TryInstallGameThreadHook` spawns a detached 1500ms validator thread —
if the counter is still 0 when it wakes up, log a loud `[ERROR]
GameThreadDispatch: VALIDATION FAILED` with the hooked address. UE's
real ProcessEvent fires many times per second under normal gameplay,
so a zero reading after 1.5s is strong evidence of a wrong-slot hook.
Silent vtable-misdetection is the whole reason this bug slept for
600+ builds; we now refuse to keep that failure mode silent.

Files touched:
- `dll/src/Frieren.cpp` — new `DetectProcessEventVTableOffsetByPattern`
  + legacy `ByVersion` fallback + post-install validator thread
- `dll/src/Stark.h` + `Stark.cpp` — `s_hookFireCount` atomic +
  `GetHookFireCount()` API; counter ticked in `HookedProcessEvent`
- `docs/lessons-learned.md` — new entry "Vtable-index detection is
  unreliable" + "Validate hooks by side-effect, not metadata"
- `docs/dev-log.md` — build 648 entry

Tests: 786 total (693 C# + 62 dll_helpers + 31 utf8_helpers) — no
regressions. Build 648 on dev.

### Pending live-game re-verification (CRITICAL)

Until the hook is observed firing on real games, treat the build 648
fix as "compiles + tests pass" — not "live-verified". Need:

1. Run on **ES2 (UE 5.5)** with a player-controlled session, look for
   `GameThreadDispatch: validation OK — hook fired N times` in
   `init-*.log` after first invoke. The instance-method invokes that
   previously timed out at `-5` should now succeed.
2. Run on **Geri / The Artisan of Glimmith (UE 4.27)**, same check.
3. Re-test KismetMathLibrary helpers ([feedback_kismet_stubs.md](
   ../../../../../../C:/Users/user/.claude/projects/D--Github-UE5CEDumper/memory/feedback_kismet_stubs.md))
   — with a correctly-hooked PE they *might* return real values; if
   they don't, the stub-pattern hypothesis stands. Either way, update
   the memory note based on actual observation.

### Out of scope for build 648

- AOB scan of the PE prologue (the original `Fix plan` step 1). The
  function-body pattern approach is functionally equivalent — both
  rely on PE's distinctive byte sequence — but is more robust because
  we don't have to guess where the prologue lives in `.text`. Can
  revisit if the function-body scan whiffs on a real game.
- Hot re-hook if validation fails. Unhook-while-game-thread-may-be-
  inside-the-trampoline is itself unsafe (see `Stark::RemoveHook`
  comment); the validator just observes and logs. User reaction is to
  collect logs + report so we can extend pattern coverage.

-----

## Call-UE-function feature gaps (discovered build 643-644 live test)

Two real gaps surfaced when test-driving the verify-mode AA Script
flow on Everspace 2 (UE 5.5). Both are caller-experience improvements,
not correctness bugs in our existing code.

### Document KismetMathLibrary stub-pattern in cooked Shipping; suggest better verification targets

**Effort**: S | **Risk**: low | **Why**: A naive user trying to verify
the invoke pipeline reaches for `KismetMathLibrary::Exp(8) -> 2980.957`
or `Add_IntInt(3, 4) -> 7` because they're the simplest possible
sanity tests. On UE 5.5+ cooked Shipping these consistently return 0
even though the function lookup, fast-path, and ProcessEvent
dispatch all succeed -- the cooker leaves the reflection metadata
intact but the `execXxx` thunk has been stripped or replaced with
a no-op stub (likely a side effect of UE's BlueprintFastCall
optimisation, where the Blueprint VM bytecode bypasses ProcessEvent
for these helpers entirely).

Live verification on ES2 (UE 5.5):
- `KismetMathLibrary::exp` (lowercase) -> 0 (Before/After identical)
- `KismetMathLibrary::Multiply_DoubleDouble(3, 4)` -> 0 (A and B
  written, ReturnValue stays 0)
- `KismetMathLibrary::Add_IntInt(3, 4)` -> 0 (same pattern; rules
  out "double precision specifically broken" hypothesis)

What to do:
- Add a "Recommended verification targets" hint in the InvokeParamDialog
  status footer when the selected class is `KismetMathLibrary` /
  `KismetSystemLibrary`: "These BlueprintFunctionLibrary helpers are
  often stub-only in cooked Shipping. Verify with game-specific
  classes instead."
- Update [docs/lessons-learned.md](lessons-learned.md) and
  [docs/test-games.md](test-games.md) so the lesson survives across
  sessions.

This is **NOT** a feature to enable calling KismetMathLibrary
helpers -- there's nothing we can do from outside the cooker. It's
a UX hint to redirect users to verification targets that actually
work (game-specific instance methods on a UObject when the game is
actively playing, so ProcessEvent traffic drains the queue).

### FString / FText / TArray input support in baked AA Script

**Effort**: M | **Risk**: med | **Why**: Functions like
`KismetSystemLibrary::PrintString` are observable side-effect targets
(player sees text in-game) ideal for verifying ProcessEvent works
end-to-end -- but currently unreachable because we can't bake an
FString **input** value. Helper's `writeBakedParams` only handles
scalar inputs (bool/int/float/double/pointer); FString needs:

1. Allocate a wide-char buffer in CE address space
2. Write the FString header at the param offset:
   - ptr (qword) = buffer address
   - count (int32) = char count
   - max (int32) = char count (typically same as count)
3. Keep the buffer alive across the ProcessEvent call (CE's
   `allocateMemory` returns a stable address)
4. Free the buffer after the call (in the cleanup timer)

Same pattern applies to FText (slightly more complex header) and
TArray of scalars (count + capacity + ptr to elements).

Implementation sketch:
- Helper-side: new `writeFString(buffer, header_addr, str)` /
  `freeFString(buffer)` shared utilities
- Generator-side: detect `StrProperty` / `TextProperty` / scalar
  `ArrayProperty` in `BakedParamValue` and emit the alloc + header
  + free dance instead of the simple `writeQword` path
- Dialog-side: TextBox should accept the unquoted user string;
  generator emits Lua-string literal with escaping
- Cleanup-side: extend the cleanup timer to free any allocated
  buffers before disabling the memrec

Out-of-scope for v1: complex-typed return decoding (FString return
already handled via "see After: dump" hint -- input support doesn't
imply output support); StructProperty inputs (the dialog flattens
known structs but generating allocs for nested containers is
significantly more work).

Read-back path: the helper's `readUFunctionReturn` already has
distinct read paths per type -- could grow a `'fstring'` token that
returns the decoded Lua string instead of a number. Optional v2.

-----

## Property Origin Resolver — proposals B + C still on table

> **🎯 NEXT SESSION STARTING POINT (2026-05-12 close-out, build 689)**:
> Big productive session — see dev-log for full list. Headline shipments:
>
> - **B' (Interesting Properties tab with Unusual Location detection)** — shipped build 670, plus calibrated by 15-game cross-game analysis (build 678 + 687)
> - **DLL BPGC filter bug fix** (build 673) — three callsites that filtered out every BlueprintGeneratedClass; turned out to be the biggest cheat-relevance gap in the project
> - **`search_properties_batch`** (build 685) — 36 keywords in one GObjects walk, ~30× speedup on big games (42s → ~1.5s on TQ2)
> - **Dump All Metadata + Python analyzer pipeline** — `scripts/analysis/analyze_dumps.py` now feeds keyword/class-rule decisions with real-game data. 15-game corpus methodology documented.
> - **9 keyword adds + 5 class rules** (Combat: Effect/Target/Radius/Ability/Modifier/Duration; Resources: Item/Items; class rules: Weapon/Projectile/Battle/Enemy) — all empirically validated, every addition has ≥3-game evidence.
>
> Proposal B (per-row "similar BP-added properties" side panel) **explicitly deferred** —
> B' is shipped and proves the broad-sweep approach works; B's anchor-driven panel adds
> work without solving a concrete user gap. **Skip unless a real user request surfaces.**
>
> Suggested next-session starters (pick one):
>
> 1. **More dumps for genre coverage** — 15-game corpus is heavy on JRPG/sim/ARPG.
>    Adding MMO / fighting / horror / RTS would calibrate further. Use the existing
>    pipeline; no code changes needed unless a new class-rule emerges. **S effort**.
> 2. ~~**Fix the same BPGC-filter bug in `SdkExportService`**~~ — **shipped build 690**.
>    Now calls `DumpAllService.IsClassLikeMetaName` directly; regression test
>    `GenerateFullSdkAsync_AcceptsAllClassLikeMetasAndScriptStruct` locks the contract.
> 3. ~~**Multi-module GWorld scan for Satisfactory**~~ — **shipped build 691**.
>    Real bug was the UI proxy-deploy scanner skipping `Engine\Binaries\Win64\`
>    (where modular UE builds put their launcher); scan-side was already working.
> 4. **Class Family Browser (Proposal C)** — bucketed view of game classes by inferred
>    role (Character / Pawn / Inventory / Stats / Save / etc.). Genuine
>    "where do I start exploring a new game?" entry point. **L effort, needs own
>    planning round.**
> 5. **Runtime `keywords.json` override** — let users customise scoring tables
>    without recompiling. Discussed during the anti-bias conversation; not yet built.
>    Source-generated JSON serializer for AOT compat. **M effort**.

### Proposal B — DEFERRED (build 689)

Original B (per-row "similar BP-added properties" suggestions on a row
click) is now deferred indefinitely. B' (the broad-sweep "find HP/MP/etc
in unusual containers" approach) shipped and is calibrated, which gives
us the cheat-discovery workflow without B's added complexity. If a real
user reports the gap B was meant to fill ("the engine field at the wrong
layer — show me the BP-added bool that the game's TakeDamage override
actually checks"), revisit B then; until then, skip.

Proposal A (dedupe-by-defining-class) shipped as build 610.
**Proposal B' shipped as build 670 + calibrated through build 687.**

-----

## New pending items (discovered build 657-689)

### ~~Fix SdkExportService BPGC filter~~ — **shipped build 690**

C# Full SDK export was filtering with bare `ClassName is "Class" or "ScriptStruct"`
which silently dropped every BlueprintGeneratedClass — same bug class as the
build 673 DLL fix, different code path. Now calls
`DumpAllService.IsClassLikeMetaName` directly so the whitelist
(Class + BPGC + AnimBPGC + WidgetBPGC + DynamicClass) stays in lockstep.
Regression test `GenerateFullSdkAsync_AcceptsAllClassLikeMetasAndScriptStruct`
covers every meta variant explicitly so it can't silently regress.

### ~~Multi-module GWorld scan (Satisfactory class)~~ — **shipped build 691**

**Status**: Both halves resolved, neither was the originally-suspected bug.

- **Scan side** (originally framed as "multi-module GWorld scan needs implementing"):
  turns out `Macht::AOBScanAllModules` was ALREADY in place from the build-509 SIMD
  scanner rewrite (commit `589fc35`), and `Genau::ScanForTarget` already invokes it
  with `tryMultiModule=true` for GObjects / GNames / GWorld / SparseDelegates. The
  15-game dump corpus already contained `FactoryGameSteam`'s clean output — the
  scan side was working all along; only the roadmap note was stale (now corrected).
- **Proxy deploy side** (the real bug): user couldn't drop `version.dll` because
  Satisfactory's actual launcher exe lives in `Engine\Binaries\Win64\` (modular UE
  build), NOT `<Game>\Binaries\Win64\`. UI proxy-deploy scanner was explicitly
  skipping `Engine\` subdir + breaking on the first `*.exe` it saw, which for
  modular layouts meant the launcher dir was invisible. Build 691 removes the
  Engine-skip and filters `CrashReportClient.exe` via `IsKnownStubExe`, so the
  scanner walks `Engine\Binaries\Win64\` but never surfaces phantom rows for
  monolithic games (where that folder only contains CrashReportClient).

Files touched: [ProxyDeployService.cs:140](../ui/UE5DumpUI/Services/ProxyDeployService.cs#L140),
[ProxyDeployTests.cs](../ui/UE5DumpUI.Tests/ProxyDeployTests.cs) (3 new tests —
modular layout, monolithic regression, orphan-Engine-dir edge case),
[lessons-learned.md "Proxy DLL Deploy"](lessons-learned.md#proxy-dll-deploy).

User-side verification 2026-05-12: manual `version.dll` drop into
`<Satisfactory>\Engine\Binaries\Win64\` → pipe connects, dump completes.

### More-genre dump coverage (calibration follow-up)

**Effort**: S (mostly user-side dumping; analyzer already does the
heavy lifting) | **Risk**: low | **Why**: The 15-game corpus is
heavy on JRPG / sim / ARPG / FPS / racing / sandbox. Missing genres:
MMO, fighting, horror, RTS, sports-sim. Each genre has its own
vocabulary (e.g. fighters: combo / cancel / parry / juggle; MMOs:
threat / aggro / dispel / cooldown_ms).

Workflow: dump 3-5 games per missing genre, re-run
`scripts/analysis/analyze_dumps.py work/dump/*.jsonl`, look at the new
cross-game tokens, PR additions to PropertyScoringTable /
KeywordScoringTable with the analysis output attached as evidence.

Process documented in [scripts/analysis/README.md](../scripts/analysis/README.md).

### Runtime `keywords.json` override (anti-bias UX)

**Effort**: M | **Risk**: med | **Why**: Discussed during build-679
anti-bias conversation. Users who disagree with the default scoring
tables currently have to fork + recompile. A runtime override file
(`keywords.json` alongside the exe) would let users add their own
genre-specific keywords without touching C# / build env.

Constraints:
- Must be AOT-compat — use source-generated JsonSerializerContext per
  CLAUDE.md rule
- Default tables stay hardcoded as fallback (so behaviour is sane
  even when the JSON is missing / malformed)
- Schema mirrors the C# tables 1:1 (StatsKeywords / CombatKeywords /
  …) plus an extension mode (additive vs replace)
- One-click "Export current tables to JSON" UI button to seed the
  customisation file

Not blocking — only do if a user actually asks for it.

### Class Family Browser (Proposal C) — still on the wishlist

**Effort**: L | **Risk**: med | **Why**: New tab "Class Family" with
a bucketed view of game classes by inferred role (Character / Pawn /
Inventory / Stats / Save / Components / DataAssets / DataTables /
GameMode). Real answer to "I have no idea where to start exploring a
new game". Needs its own planning round before starting — the
classification heuristic + UI design is the hard part, not the
implementation. **NOT a "jump in and code" task.**

Pre-work would benefit from the dump corpus: cluster 15 games' BPGCs
by property-name similarity to derive concrete "Inventory-like" /
"Character-like" / etc. archetype patterns.

### Proposal B: per-row "similar BP-added properties" suggestions

**Effort**: M | **Risk**: low

When a user lands on `bCanBeDamaged @ AActor`, surface a side-panel
with fuzzy-matched game-specific bools that semantically overlap
(e.g. `bIsImmortal @ BP_PlayerCharacter_C`). Reuses the
`KeywordTokenizer` + `KeywordScoringTable` machinery to score
similarity. **Why**: closes the "engine field is at the wrong layer;
show me the BP-added bool that the game's TakeDamage override
actually checks" gap from the analysis.

**UX**: anchor-driven. User already has a property selected (the
engine field); we surface its likely game-specific counterpart in BP
subclasses.

### Proposal B': "Unusual Location" Property Detection — **new insight 2026-05-12**

**Effort**: S–M (small if folded into B's PR) | **Risk**: low

Complementary to B but a different entry point: **find game-state-
suggestive properties (HP/MP/Stamina/XP/Damage/Health/etc.) regardless
of whether an engine equivalent exists, AND flag the cases where
they're sitting in a class you wouldn't expect.**

**Motivation**: developers don't always follow Unreal conventions.
HP/MP fields routinely show up in non-standard containers — observed
patterns include `LocalPlayer`, `GameViewportClient`, `HUDClass`,
`GameInstance` subclasses, even random `UObject`-derived service
classes. From a cheat-development perspective these are the most
valuable hits because they're **not where you'd think to look first**.
Function-side already does this kind of class-location-aware ranking
(`Character / Pawn / PlayerController / PlayerState +3`,
`Anim / Niagara / Sound -2`, etc. in `KeywordScoringTable`); the
Property side needs the same treatment.

**UX**: broad sweep, no anchor needed. Could land as either:
- a new **"Interesting Properties"** tab (analogous to Interesting
  Funcs), OR
- a **scoring-aware mode toggle** in the existing PropertySearch tab

**Scoring sketch** — reuse `KeywordTokenizer` for property-name
matches, layer class-location bonuses/penalties on top:

| Class bucket                                      | Bonus | Interpretation                |
|---------------------------------------------------|------:|-------------------------------|
| Character / Pawn / PlayerState / Inventory        |   +3  | Expected location             |
| GameMode / GameInstance / SaveGame                |   +2  | Expected (game-level state)   |
| AbilitySystemComponent / Stats / Status           |   +2  | Expected (gameplay subsystem) |
| **LocalPlayer / GameViewportClient / HUD**        |  **+4** | **Unusual — high-value hit**  |
| Anim / Niagara / Sound / Audio / Particle / Mesh  |   −2  | Noise (visual/effect classes) |
| UI / Widget                                       |   −1  | Noise (UI display)            |

The Unusual category gets a **positive bonus** because a HP field in
`LocalPlayer` is more interesting than a HP field in `BP_Player_C`
(the latter is the "normal" place; the former is the cheat-finder's
gold). Display this as a **"⚠ Unusual Location"** badge on the row
so the user immediately sees why this hit is unconventional.

**Keyword starter list** (extend in C# scoring table):
- Stats: `HP`, `MP`, `SP`, `Health`, `Mana`, `Stamina`, `Energy`,
  `XP`, `Exp`, `Experience`, `Level`, `Lv`, `Lvl`
- Combat: `Damage`, `Defense`, `Armor`, `CritRate`, `CritDamage`,
  `Attack`, `MoveSpeed`, `JumpHeight`
- Resources: `Gold`, `Coin`, `Money`, `Currency`, `Gem`, `Diamond`

Apply `KeywordTokenizer` whole-token matching so short acronyms
(HP/MP/SP/XP/Lv) don't substring-collide with engine spam
(`Component`, `Levitate`, etc.) — same lesson from build 609.

### Pairing rationale (why B + B' together)

Both proposals lean on the same building blocks:
1. `KeywordTokenizer.cs` — whole-token matching, already proven
2. `KeywordScoringTable.cs` — already has Function-side scoring
   tables; extend with PropertyScoringTable using the same shape
3. `ScoredFunctionRow`-style row model for `ScoredPropertyRow`
4. Class-location bonus/penalty machinery — already mature for
   functions, factor out to a shared `ClassLocationScorer` helper

Doing them together = ~1.3× the work for both, vs ~1× + ~1× sequential.
Estimate **M total** if done in one PR.

### Open design questions (decide before starting)

1. **B as side-panel vs B' as new tab vs B' as PropertySearch mode** —
   pick one of: (a) side-panel for B + new "Interesting Properties"
   tab for B', (b) extend PropertySearch with a "Scored" sort/filter
   mode covering both. Option (b) is fewer moving parts but more
   crowded UI; option (a) keeps the discovery/exploration entry
   points separate.
2. **Anchor-driven B's fuzzy threshold** — too loose = noise, too
   tight = no hits. Need a calibration round on 3-4 games. The
   build-609 KeywordTokenizer threshold-5 lesson applies.
3. **PropertyScoringTable keyword list** — start with the table
   above, calibrate on real games (ES2's `bCanBeDamaged` / `Health`,
   Geri's `MaxJumpHeight` are good anchors).

### Files in scope (pre-implementation guess)

- `ui/UE5DumpUI/Services/PropertyScoringTable.cs` (new, mirror of
  `KeywordScoringTable.cs`)
- `ui/UE5DumpUI/Services/ClassLocationScorer.cs` (new, extracted from
  `KeywordScoringTable`'s class-bonus logic — refactor first so
  Function side benefits too)
- `ui/UE5DumpUI/Models/ScoredPropertyRow.cs` (new)
- `ui/UE5DumpUI/ViewModels/PropertySearchViewModel.cs` (extend with
  scoring + Unusual Location badge) OR new
  `InterestingPropertiesViewModel.cs` (depending on #1 above)
- `ui/UE5DumpUI/Views/PropertySearchPanel.axaml` (Scope column
  already exists for B's "+N inheritors"; add Unusual Location badge)
- `dll/src/Aura.cpp` — possibly extend `EnumerateAllFunctions`-style
  scan for properties if PropertySearch's current pagination can't
  serve the new flow

### Out of scope for this round

- Full Class Family Browser (Proposal C) — separate planning
- Anchor-driven function fuzzy-match (Function v2 equivalent of B) —
  Function v1 closed; defer until concrete user request

### Proposal C: Class Family Browser

New tab "Class Family" — bucketed view of the game's classes by
inferred role (Character / Pawn / Inventory / Stats / Save /
Components / DataAssets / DataTables / GameMode). The "where do
character / item data live?" entry point. **Effort**: L | **Risk**:
med (needs careful family-classification rules per UE version) |
**Why**: real answer to "I have no idea where to start exploring a
new game" -- bigger work, separate planning round before starting.

-----

## Carryover capability gaps

Existing gaps from before the plan above. Pick up when the active plan
finishes or when blocked.

### MulticastSparseDelegateProperty UE 4.23-4.27

**Effort**: L | **Risk**: med | **Why**: Closes the only remaining
delegate-flavour gap. UE5 path landed in PR #194 (build 561-577); UE4
needs a separate AOB + walker branch.

- Outer key is `FObjectKey { FWeakObjectPtr Object; int32
  ObjectSerialNumber; }` (12B + 4 pad = 16B) instead of raw
  `UObjectBase*`
- Outer stride changes ~0x60 → ~0x68
- Key match logic must reconstruct `FObjectKey` from
  `(owner, Aura::GetSerialNumber(InternalIndex))`
- Need separate AOB — UE4 binaries don't share the UE5 `lea` sequence

Walker currently returns `supported=false` for UE < 5.0 to make this
gap explicit.

### Find Refs v4 — TMap / TSet weak-like inner sides

**Effort**: M | **Risk**: low | **Why**: Currently Object/Class only;
weak/soft pointer collections (`TMap<UObject*, FWeakObjectPtr>` etc.)
silently miss target hits.

Reuse the v3 weak-resolve helper inside the existing TMap / TSet
walkers in `Aura::FindReferencesToUObject`.

### FieldPathProperty drill-down + Find Refs

**Effort**: M | **Risk**: low | **Why**: Last remaining no-handler
property type. Rare in shipping games — only seen in Editor-derived
classes — so genuinely low priority.

### GWorld coverage

**Effort**: S each | **Risk**: low | **Why**: Two remaining
unverified / failing titles.

- **Star Wars Jedi: Survivor** (UE 4.27?): untested — needs an AOB
  sweep run + result triage
- **Satisfactory** (UE 5.3, modular DLL build): two related issues,
  both stemming from the same root — game splits CoreUObject into a
  separate DLL rather than baking it into the main exe.
  1. **Proxy DLL injection fails** (verified 2026-05-12, user feedback):
     dropping version.dll / dinput8.dll into the install folder doesn't
     attach. The game's loader or launcher bypasses normal proxy
     hooking. **Workaround**: CE DLL injection (manual). This was good
     enough for the 10-game dump-for-analysis run, but breaks the
     proxy-deploy UX path entirely on Satisfactory.
  2. **GWorld scan fails on the main exe**. Pattern likely lives in
     `CoreUObject-Win64-Shipping.dll`. Adapt `Genau::FindAll` to scan
     multiple modules when the primary scan fails.

  **Effort**: M (multi-module scan in Genau) + investigate why proxy
  DLL doesn't attach. Once attached via CE injection the rest works —
  dump produced 4868 BPGCs cleanly, the biggest game-class count of
  the analysis-corpus dataset.

### `kPublishers[]` table additions

**Effort**: S each | **Risk**: high (if added casually) | **Why**:
Wrong publisher bias overrides correct detection.

Only add a publisher when we have ≥3 misdetected titles from that
publisher AND a clear pattern (e.g. "all UE4-fork builds shipping under
this LegalCopyright string"). Wait for real misdetection reports.

-----

## Speculative — pick if active plan finishes ahead of schedule

Items from the brainstorm that aren't yet committed to:

- **Invoke history / favorites panel** — auto-record (target, args,
  result) per UI invocation; one-click re-fire from history
- **Dry-run-first invoke** — for never-called functions, invoke with
  zero/sentinel params first to detect crash before letting user
  commit to real args
- **CE table builder** — bundle selected pointer entries + AA scripts
  into a single `.ct` file, auto-grouped by category
- **Hotkey binding** — global hotkey assignment for shortlisted
  functions ("give 1000 gold" on Ctrl+G)

-----

## Done (recent — moved to [dev-log.md](dev-log.md))

Recent items that shipped, kept here briefly until the next refresh:

- ✅ **Walker false-positive sweep + Scharf alignment helper** (build
  582-583, PR #195)
- ✅ **Per-game GameThreadDispatch invoke timeout** (build 583-588, PR #195)
- ✅ **`FillPointerSnapshot` refactor** (build 588, PR #195)
- ✅ **UI strict address validation** (build 588, PR #195)
- ✅ **Drill Depth slider 0-6 with warning band** (build 588, PR #195)
- ✅ **dev-log split into roadmap.md + todo.md** (this document, post
  build 589)
- ✅ **Copy AA Script (Baked) UFunction export** (helper file +
  generator + dialog + Tools menu, build 590-596)
- ✅ **Interesting Functions Finder** (list_all_functions pipe +
  KeywordScoringTable + new tab + cross-tab nav, build 597-607)
- ✅ **AOBMaker availability gating + Notes column + pipe-broken
  guard** (build 608)
- ✅ **CamelCase keyword tokeniser** (KeywordTokenizer + tokens
  replace substring matching, restored short acronyms, build 609)
- ✅ **Invoke dialog overflow fix** (window MaxHeight + ScrollViewer
  MinHeight, build 609)
- ❌ **UFunction metadata exposure (steps 3+4) skipped** — research
  confirmed metadata is stripped from cooked Shipping binaries; would
  be ~zero value for real cheat-table targets. Pivoted to tokeniser
  instead.
- ✅ **PropertySearch dedupe-by-defining-class (Property Origin
  Resolver A)** — `bCanBeDamaged` now one row "+4822 inheritors"
  instead of 4823 indistinguishable rows (build 610)
- ✅ **One-click Inject Helper into Current CE Table** (AOBMaker
  plugin's new InjectTableFile pipe cmd + UE5DumpUI Tools menu;
  cherry-picked from spawned session, build 611)
- ✅ **Multi-select Copy CE Field(s)** — LiveWalker DataGrid Extended
  mode; container-view multi-select emits one filtered container
  with N elements (build 660)
- ✅ **System tab "UI build: 0" bug** — `Version.Revision` not `.Build`
  (build 662)
- ✅ **Tab labels shortened** + **status text overflow fix** +
  **⚙ Options popover** (build 666)
- ✅ **Interesting Properties tab (B' round 1)** — Stats / Combat /
  Resources / Movement / Utility categories, Unusual Location flag
  for LocalPlayer / GameViewportClient / HUD / CheatManager
  (build 670)
- ✅ **DLL BPGC filter fix** (`IsClassLikeMeta` whitelist in
  SearchProperties / ListClasses / EnumerateAllFunctions) + **surgical
  Anim penalty** (AnimMan_Player_C no longer punished) + **Player +2 rule**
  (build 673)
- ✅ **Export → Dump All Metadata (.jsonl)** + Python analyzer pipeline
  (`scripts/analysis/analyze_dumps.py` + README anti-bias section)
  (build 676)
- ✅ **15-game data-driven keyword adds**:
  CombatKeywords +6 (Effect/Target/Radius/Ability/Modifier/Duration);
  ResourcesKeywords +2 (Item/Items); PropertyRules +3 (Weapon/Projectile/Battle)
  — all backed by cross-game evidence (build 678)
- ✅ **`search_properties_batch`** — DLL walks GObjects ONCE for N
  queries; ~30× speedup on big games (build 685)
- ✅ **Phase 2 function-side analysis** confirms KeywordScoringTable
  is comprehensive; class-bonus side gets Enemy +2 (both Function +
  Property) and Weapon +2 (Function side mirror) (build 687)
- ✅ **AOBMaker "Notes" column removed** — replaced with single
  inline status-row indicator (build 689)
