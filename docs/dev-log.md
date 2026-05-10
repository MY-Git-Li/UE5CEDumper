# Dev Log

Append-only milestone history, newest first. Each entry references a
build number from `build_number.txt` so commits can be cross-referenced.

> **Looking for current state?** See [roadmap.md](roadmap.md) for the
> capability matrix / per-game configuration / tested games, and
> [todo.md](todo.md) for the prioritized next-work list. This file
> records *what shipped* — the other two record *what works now* and
> *what's next*.

-----

## 2026-05-10 (latest, dev branch, build 610) — PropertySearch dedupe-by-defining-class

`feat(dll,ui): PropertySearch results deduped by defining class
+ inheritance count badge`. First piece of the "Property Origin
Resolver" set proposed in chat (proposal A) -- closes the
"PropertySearch returns 4823 indistinguishable rows for
`bCanBeDamaged`" UX trap that user flagged as the biggest practical
problem with the existing tooling.

### The problem (one sentence)

UE doesn't shadow inherited properties (no C# `new` keyword
equivalent), so a field declared on `AActor` lives at the same offset
on every `APawn`/`ACharacter`/`BP_*_C` subclass. PropertySearch
walked each class's full inherited chain and emitted one row per
class -- 4823 rows for one field, with no signal to the user about
which one was "real" (they're all the same memory).

### Algorithm

`Aura::FindDefiningClass(classAddr, fieldOffset)` walks the
SuperStruct chain upward. A class C declares the property at
`fieldOffset` iff:

```
fieldOffset >= C.SuperStruct.PropertiesSize    (super doesn't have it)
fieldOffset <  C.PropertiesSize                (C does have it)
```

Translated to a loop: starting at the iterated class, walk to super
while `super.PropertiesSize > fieldOffset` (super has it too); when
super doesn't have it, the current class is the defining class.
32-step depth cap matches Ubel's existing inherited-walk limit.

### SearchProperties dedup

Per-call `unordered_map<DedupKey, size_t>` where key is
`(definingClassAddr, propName, propOffset)`. First encounter:
allocate a representative `PropertyMatch` keyed by the defining
class. Subsequent encounters: bump `inheritedByCount`, no new row.

The representative match's `className` / `classAddr` / `classPath`
are the **defining class**, not the iterated class -- so the user
sees `AActor` as the "true home" of `bCanBeDamaged` regardless of
which subclass GObjects[i] hit first.

### Phase 2 (preview) gotcha + fix

After dedup, `match.classAddr` is the defining class -- often
abstract (AActor / APawn) with zero direct instances. The existing
Phase 2 code looked up instances by exact class match, which after
dedup would find nothing for almost every match.

Fix: track an internal-only `previewClassAddr` per match -- the
most-derived subclass observed during the search loop (largest
`PropertiesSize`, since deeper-in-chain classes are more likely to
be concrete and have live instances). Phase 2 swaps `classAddr <->
previewClassAddr` around the `ResolvePropertyPreviews` call so the
existing instance-lookup helper sees the concrete subclass while
the wire output keeps the canonical defining-class addressing.

### Wire schema additions (4 new fields)

```json
{
  "class_name":           "AActor",                  // defining class (post-dedup)
  "class_addr":           "0x7FF...",                // ditto
  "defining_class_name":  "AActor",                  // explicit duplicate for forward compat
  "defining_class_addr":  "0x7FF...",
  "defining_class_path":  "/Script/Engine.Actor",
  "inherited_by_count":   4822,
  ...
}
```

`defining_class_*` exposed as a separate copy of `class_*` so a
future "Show inheritance expanded" mode could emit one row per
inheriting class with `class_*` reflecting the inheritor and
`defining_class_*` still pointing at the canonical home. Back-compat
preserved: older DLLs that don't emit these default to "" / 0
client-side, which the model's computed properties handle gracefully.

### UI

PropertySearchPanel grew a new "Scope" column between Class and
Super:
- Empty when `InheritedByCount == 0` (a strong "this is a unique,
  game-specific field" hint -- usually the kind a cheat-table maker
  actually wants)
- "+1 inheritor" / "+N inheritors" otherwise

Tooltip on the Scope cell explains the relationship + shows the
defining class path so the user can tell engine fields
(`/Script/Engine.*`) from game fields (`/Game/*` / `/Script/MyGame.*`)
at a glance.

### Tests (+11)

`PropertySearchMatchTests` (new):
- `InheritanceBadge` empty / singular / plural cases
- `InheritanceTooltip` highlights uniqueness when count=0; shows
  defining class path + "identical effect" wording when count>0
- `OffsetHex` / `TypeDisplay` baseline preserved (no regression
  in existing display behaviour)

DLL-side dedup correctness needs a live game (4823-class scenario)
to verify end-to-end -- no unit-test surface for the GObjects walk.
Smoke test pending on Everspace 2 / Titan Quest II / FF7 Rebirth.

### Follow-ups (not blocking)

- **Proposal B**: per-row "similar BP-added properties" suggestions
  using the tokeniser to surface game-specific bools alongside the
  engine field (so user sees `bCanBeDamaged @ AActor` AND nearby
  `bIsImmortal @ BP_PlayerCharacter_C` in one view)
- **Proposal C**: Class Family Browser tab -- bucketed view of
  Character / Pawn / Inventory / Save / Component / DataAsset / etc
  classes loaded in the game. Bigger work, separate planning.

**Build #610, 755 tests passing (662 C# + 62 dll_helpers + 31
utf8_helpers).** 9 commits ahead of `origin/main`.

-----

## 2026-05-10 (build 608-609) — AOBMaker gating + CamelCase tokeniser + dialog overflow fix

Three independent fixes shipped under the "polish + de-risk" theme
after Interesting Functions Finder went live in 597-607.

### AOBMaker availability gating + Notes column + pipe-broken guard (build 608, [`25b6594`](../ui/UE5DumpUI/ViewModels/InterestingFunctionsViewModel.cs))

Closes the UX gap where AA Script export silently fell back to
clipboard when AOBMaker was unavailable -- user had no clear signal
whether the script reached CE or not.

- `InterestingFunctionsViewModel` got `IAobMakerBridge?` ctor injection
  + `IsAobMakerAvailable` ObservableProperty + `AobMakerNote` computed
  string + `CheckAobMakerAsync` / `TryCheckAobMaker` methods. Mirrors
  the LiveWalker pattern; same 5s cooldown so rapid tab switches don't
  stack 2s pipe-connect timeouts.
- `MainWindow.MainTabs_SelectionChanged` now also fire-and-forget
  re-checks AOBMaker on LiveWalker (tab 0) and Interesting Funcs
  (tab 3) activation. Detects CE start/stop without blocking the UI
  thread.
- `LiveWalker.TryCheckAobMaker` made public so the tab handler can
  call it from the same code path.
- LiveWalker + Interesting Funcs DataGrids gained a "Notes" column
  bound via `RelativeSource` ancestor to the parent VM's `AobMakerNote`.
  Italic amber styling (#E0A050). Per-row column with VM-level value
  -- every row shows the same hint, leaves the slot open for future
  per-row notes (e.g. dry-run failures).
- Send-time guards: `LiveWalker.GenerateInvokeScriptAsync`,
  `LiveWalker.CopyBakedScriptAsync` no-args fast path,
  `InvokeParamDialog.OnCopyBakedScriptClicked`, MainWindowVM
  `RequestCopyBakedScript` -- all sample `_aobMaker.IsAvailable` BEFORE
  the send so the failure message can distinguish:
  * `sentToCe=true` -> "AA Script created in CE"
  * `sentToCe=false && wasAvailable=true` -> `⚠ AOBMaker pipe broke
    (CE closed?) -- copied to clipboard`
  * `sentToCe=false && wasAvailable=false` -> "AOBMaker not connected
    -- copied to clipboard"
  After each send, the bridge's post-send `IsAvailable` is synced back
  to the VM so the Notes column reflects reality on the next repaint.

### Step 3 (UFunction metadata exposure) skipped after research

Original plan: read `UField::MetaDataMap` to surface Blueprint
`DisplayName` / `ToolTip` / `Category` / `Keywords`.

Research (vendored UE source `Field.cpp:666-693` +
`CoreMiscDefines.h:22-28`) confirmed:
- `WITH_METADATA = WITH_EDITORONLY_DATA`
- On Windows/Mac/Linux Shipping builds the macro is `1` so the
  `MetaDataMap` POINTER exists in the struct
- BUT the cooker strips the actual content during cook -- runtime
  `GetMetaData()` returns empty string in every cooked Shipping game

Implication: implementing this would only pay off on DebugGame /
Development-config builds. Cheat-engine users almost never encounter
those. Estimated 250 LoC + per-version offset table for ~zero
real-world value.

**Pivoted** to the tokeniser work (B below) which closes the same
substring-noise gap by a different route.

### CamelCase keyword tokeniser (build 609, [`f146a22`](../ui/UE5DumpUI/Services/KeywordTokenizer.cs))

Replaces v1's substring-matching scorer with a tokenisation pass so
keywords match whole tokens instead of letter substrings. Closes the
trade-off where short acronyms HP/MP/SP/XP/TP had to be dropped to
avoid false-positiving every word containing those letter pairs
(`Component`, `Spawn`, `GetTPSStream`, etc).

New `KeywordTokenizer` (`Services/KeywordTokenizer.cs`) -- splits on
underscore/hyphen, lower-to-upper transition (`AddMoney` ->
{add, money}), and run-of-uppers followed by lower (`HUDWidget` ->
{hud, widget}, `BPCharacter` -> {bp, character}). Returns lowercased
tokens for case-blind comparison. Digit-to-letter / letter-to-digit
transitions are NOT split (UE BP names typically already
underscore-separate digit groups; documented + tested cost is
"Player100Health" tokenises to one token).

`KeywordScoringTable.Score` now tokenises function + class name into
a unioned HashSet, then `CountTokenHits` does a subset-match: ALL
keyword tokens must be present in the function/class token set.
Multi-token keywords (`NoClip` -> {no, clip}, `SetActorLocation` ->
{set, actor, location}) work via the subset rule.

Keyword tables restored:
- StatsKeywords: HP/Hp/MP/SP/XP all back -- only fire on standalone
  tokens now (`GetHP`, `SetMaxMP`, `RestoreSP`, `AddXP`)
- InventoryKeywords: 'Drop' restored -- `DropItem` hits Inventory
  cleanly without false-positiving every disposal helper
- MovementKeywords: 'TP' restored. Multi-token forms collapsed to
  single tokens like 'Location' (covers `SetLocation` /
  `SetActorLocation` / etc with less keyword bookkeeping)
- UtilityKeywords: 'Time' / 'Clock' restored -- `Lifetime` no longer
  false-fires (tokenises to one token "lifetime", not "time")
- Create/Destroy still dropped -- engine plumbing spam even tokenised

ClassBonuses (substring-based, separate table) untouched -- `"Anim"`
should still hit `AnimNotify` / `AnimationInstance` / etc, which
benefits from substring rather than whole-token matching.

### Invoke dialog overflow fix (build 609)

The actual issue wasn't a missing ScrollViewer (that was already
there since the dialog was first written) -- it was the hard
`MaxHeight=700` window cap that prevented users on big monitors from
resizing larger to see all params.

`InvokeParamDialog.cs` changes:
- Window `MaxHeight` 700 -> 1100; `Height=480` default; `MinHeight=240`
- `SizeToContent = SizeToContent.Height` so the dialog grows to fit
  the form then caps at MaxHeight
- ScrollViewer wrapping the param panel gained `MinHeight=200` -- when
  the FIRE result label expands after a successful invoke, DockPanel
  would otherwise let the bottom panel squish the scroll area to a
  sliver. 200px floor keeps ~6 param rows visible regardless.

### Tests (+51, total 651 C# / 744 across project)

- `KeywordTokenizerTests` (new, 17 cases): all 4 split rules,
  acronym-preserved-as-single-token cases, the substring-noise
  regression cases (`Component` / `MyComponent` / `Spawn` /
  `SpawnActor` / `GetTPSStream` MUST tokenise without producing
  `mp`/`sp`/`tp` tokens), null/empty/single-char/all-lower/all-upper/
  digit-attachment edge cases.
- `KeywordScoringTableTests` (+9): 5 substring-noise regressions
  now correctly Other; 5 acronym-restored cases (`GetHP`, `SetMaxMP`,
  `RestoreSP`, `AddXP`, `DoTP`) correctly categorise; restored-keyword
  fact (`DropItem` -> Inventory, `GetTimeRemaining` -> Utility,
  `GetLifetime` -> Other); multi-token `EnableNoClip` /
  `ToggleGodMode` via subset-match.
- `InterestingFunctionsViewModelTests` (+4): AOBMaker availability
  flip-to-false note presence, recovery-clears-note, cooldown
  honoured, no-bridge no-throw.

### What's still pending

The plan's full 5 items now reconcile:
1. ✅ AA-Script export from UI (build 590-596)
2. ✅ Interesting Functions Finder (build 597-607)
3. ❌ UFunction metadata exposure -- skipped (cooker strips it; see above)
4. ❌ Finder rev2 metadata fold-in -- skipped (depends on 3)
5. ✅ Invoke dialog overflow fix (build 609)

Carryover gaps (in [todo.md](todo.md)) untouched: UE 4.23-4.27 sparse
delegate, Find Refs v4 weak-side, FieldPathProperty, GWorld for
Star Wars Jedi / Satisfactory.

**Build #609, 744 tests passing (651 C# + 62 dll_helpers + 31
utf8_helpers).** 8 commits ahead of `origin/main`.

-----

## 2026-05-10 (build 597-607) — Interesting Functions Finder

`feat(dll,ui): list_all_functions + Interesting Functions Finder
panel` — second item of the "Call-UE-function strengthening" plan
([docs/todo.md](todo.md), step 3c). Three commits on `dev`:
[`d4ef507`](../dll/src/Aura.cpp) DLL + service wire,
[`e3a24fb`](../ui/UE5DumpUI/ViewModels/InterestingFunctionsViewModel.cs)
panel + scoring, and the cross-tab nav + tests + docs in this commit.

### The problem

After the Copy AA Script (Baked) feature shipped (build 590-596), the
remaining friction was *discovery*: a user wanting to find AddMoney /
Teleport / SetHealth on a game with 50k UFunctions across 1k UClasses
had to manually walk the class tree or guess at PropertySearch
keywords. PropertySearch is property-focused (FloatProperty fields
across classes); functions had no equivalent surface. The
GameClassFilter panel lists classes by RE score but doesn't surface
the actual cheat-relevant *verbs* on each class.

### Architecture

Three layers, sliced UI ↔ DLL at the cheapest seam:

**Layer 1 — DLL: `Aura::EnumerateAllFunctions`** (`Aura.cpp` + `Aura.h`)

Mirrors the SearchProperties / ListClasses GObjects-walk pattern: scan
every UObject, identify UClasses by metaclass-name == "Class", dedupe
via visited set, flatten Ubel::WalkFunctions per class. Returns a flat
vector of light-weight `AllFunctionEntry { className, classAddr,
superName, classPath, funcName, funcAddr, functionFlags, numParms,
parmsSize }` rows -- ~80 bytes per row, so 50k functions = ~4MB JSON,
acceptable as a one-shot pipe payload. Per-class WalkClassEx is
technically wasted work (we only need SuperName from it) but adding
a SuperName-only walker just to save a few ms isn't worth the parallel-
reader maintenance burden.

New pipe cmd `Renge::CMD_LIST_ALL_FUNCTIONS = "list_all_functions"`
with `{game_only, limit}` request and `{total, scanned_objects,
scanned_classes, total_functions, functions[]}` response. Cost is
O(GObjects + sum(WalkFunctions)); typical 1M-object game completes in
2-10s. UI runs the call on a worker task with a progress indicator.

**Layer 2 — UI keyword scorer:
[`KeywordScoringTable`](../ui/UE5DumpUI/Services/KeywordScoringTable.cs)**
(C# static class)

Scoring is client-side so the rules can be tuned without a DLL
rebuild. Six categories (Stats / Inventory / Movement / Combat /
Utility / Other) with per-bucket keyword arrays and a fixed
per-category hit-score weight:

| Category | Per-hit | Sample keywords |
|---|---|---|
| Stats | 5 | Health, Mana, Stamina, Experience, Damage, Heal, Kill |
| Inventory | 5 | Gold, Money, Currency, Item, Inventory, Pickup, Loot |
| Movement | 5 | Teleport, Warp, SetActorLocation, Move, Speed, Walk, Sprint |
| Combat | 4 | Attack, Fire, Cast, Ability, Skill, Buff |
| Utility | 3 | Save, Load, Spawn, Timer, Cheat, Debug, Console |
| **ExplicitMovementCheats** | **8** | NoClip, Fly, God, Ghost, Invincible, Invisible |

The ExplicitMovementCheats sub-bucket carries higher per-hit weight
and folds into Movement so a `NoClip` function on a
`DebugCheatManager` class stays in Movement instead of being pulled
into Utility by the noisy `Cheat` + `Debug` class-name keywords (8 vs
3+3=6).

Class-name bonuses (substring, summed -- so AnimNotify_Character gets
+3 Character + -2 Anim = +1 net):

| Class substring | Bonus |
|---|---|
| Character / Pawn / PlayerController / PlayerState | +3 |
| GameMode / GameInstance / SaveGame | +2 |
| Anim / Niagara / Sound / Audio / Particle | -2 |
| UI / Widget | -1 |

Flag bonuses (from `AllFunctionEntry`'s flag projections):

| Flag condition | Bonus |
|---|---|
| `BlueprintCallable` | +2 |
| `BlueprintEvent` | +1 |
| `(BlueprintPure \|\| Const)` && `numParms <= 1` | +1 (safe getter) |
| `ParmsSize > 64` | -1 (annoying to call) |

Score = sum of all keyword-bucket scores + classBonus + flagBonus.
Category = the highest-scoring bucket; ties broken by enum order
(Stats > Inventory > Movement > Combat > Utility). InterestingThreshold
= 5; rows below are hidden by default (UI "Show All" toggle bypasses).

#### Substring-noise lesson

First draft included short acronyms (HP/MP/SP/XP/TP). Tests caught
the regression immediately:
[`Component`](https://en.cppreference.com/w/cpp/string/byte/strstr)
contains "mp", `Spawn` contains "sp", `GetTPSStream` contains "tp".
A `FrobnicatorComponent.DoNothingPlz` function was scoring as Stats
because of the "mp" substring in "Component". Resolution: drop short
acronyms entirely -- accept the miss on `GetHP()` so we don't
false-positive every `*Component*` function. Game devs almost always
emit full-name BC functions for the surface that's actually exposed
to Blueprint anyway. The keyword tables now use full forms only and
have inline comments explaining what got dropped and why.

**Layer 3 — UI panel:
[`InterestingFunctionsPanel`](../ui/UE5DumpUI/Views/InterestingFunctionsPanel.axaml)**

New tab "Interesting Funcs" between PropertySearch and GameClassFilter
(tab index 3 -- ClassStruct shifts 4 → 5; updated the existing
`GameClassFilter.NavigateToClassStruct` handler accordingly).
Toolbar: Load button, Game Only toggle, name-substring filter, category
chip dropdown (with custom
[`CategoryDisplayConverter`](../ui/UE5DumpUI/Services/CategoryDisplayConverter.cs)
that maps null → "All"), Show All toggle, Clear button, status text.

DataGrid columns: Score (with breakdown tooltip), Cat (coloured per
category), Class, Function, Flags (compact `BC,BE,Const` style),
Params (`NumParms (ParmsSize B)`), action buttons:
- **Live**: opens the function in Live Walker via cross-tab nav.
- **AA(B)**: shortcut into the Copy AA Script (Baked) flow.

Both fire events into MainWindowVM which routes them.

### Cross-tab navigation

Two events on
[`InterestingFunctionsViewModel`](../ui/UE5DumpUI/ViewModels/InterestingFunctionsViewModel.cs):

`NavigateToFunction(className, funcName)` -- Live Walker is
instance-based, but the Finder gives us (className, funcName).
Handler:
1. `FindInstancesAsync(className, exactMatch=true, limit=5)` -- pick
   the first non-CDO live instance (skip `Default__*`).
2. On hit: switch to Live Walker tab + `NavigateToAddressCommand` on
   the instance address. (v1 lands on the instance; user manually
   scrolls to the function row -- a row-scroll event into Live Walker
   could be added if feedback says it's worth it.)
3. On miss (CDO-only class, or class not yet instantiated): switch to
   ClassStruct tab + `LoadClassCommand` on the class address resolved
   via `ListClassesAsync` lookup. Surfaces "no live instance, showing
   class metadata" in the status bar.

`RequestCopyBakedScript(className, funcName)` -- shortcut into the
Copy AA Script (Baked) flow without going through Live Walker first.
Handler:
1. `ListClassesAsync` to resolve className → classAddr.
2. `WalkFunctionsAsync(classAddr)` to fetch full param metadata.
3. Find the matching FunctionInfoModel by name.
4. Try `FindInstancesAsync` for a live instance (best-effort; the
   helper's `CMD_INVOKE_BY_NAME` finds an instance itself, but
   surfacing it now lets us show a clear error if the class is
   CDO-only).
5. Zero-arg fast path: generate AA Script directly + ship to AOBMaker /
   clipboard. Otherwise open `InvokeParamDialog` in
   `CopyBakedScript` mode.

### Tests (+60)

[`KeywordScoringTableTests`](../ui/UE5DumpUI.Tests/KeywordScoringTableTests.cs)
-- 30+ cases:
- Per-keyword category assignment (16 [Theory] inlines covering all
  five real categories + a no-hit Other case)
- Class bonus stacking (AnimNotify_Character +1 net, NiagaraSystem -2,
  HUDWidget -1, MyUIPanel -1)
- Flag bonus combinations (BC, safe getter, large-params penalty,
  net-1-after-penalty case)
- Combined "happy path" (`AddMoney/PlayerCharacter` lands above
  threshold)
- Negative case (`UpdateInternal/BP_NiagaraEffect_C` lands below
  threshold)
- Tie-break (`GoldHealth/Misc` -> Stats wins per enum order)
- DisplayName + CategoryColor full-coverage [Theory]

[`InterestingFunctionsViewModelTests`](../ui/UE5DumpUI.Tests/InterestingFunctionsViewModelTests.cs)
-- 10 cases:
- Load -> score -> sort by FinalScore desc
- Default threshold hides noise (AnimNotify, ParticleSystem)
- GameOnly toggle is plumbed to the service
- FilterText substring match against function OR class name
  (case-insensitive)
- CategoryFilter narrows to one bucket; null restores All
- ShowAll bypasses threshold
- ClearFilters resets all inputs
- OpenInLiveWalker fires NavigateToFunction with right payload
- CopyAaScript fires RequestCopyBakedScript with right payload
- Null-row guard

`StubDumpService` (in `CsxExportServiceTests.cs`) un-sealed and the new
`ListAllFunctionsAsync` made `virtual` so the new tests' `FakeDumpService`
can extend it with one override -- avoids duplicating the full
~25-method IDumpService stub list.

### What's covered now / what's pending

Done: discover + score + filter + categorise UFunctions across the
whole loaded class hierarchy; one-click navigate to LiveWalker (with
graceful ClassStruct fallback) or generate a baked AA Script.

Still pending in the same plan ([docs/todo.md](todo.md)):
3. **UFunction metadata exposure** -- expose Blueprint
   `DisplayName` / `ToolTip` / `Category` so the Finder can match
   richer text + show better column labels
4. Finder rev2 -- fold metadata into the keyword scorer
5. InvokeParamDialog ScrollViewer overflow fix

**Build #607, 693 tests passing (600 C# + 62 dll_helpers + 31
utf8_helpers).** 5 commits ahead of `origin/main`.

-----

## 2026-05-10 (build 590-596) — Copy AA Script (Baked) UFunction export

`feat(ui): Copy AA Script (Baked)` series — first chunk of the
"Call-UE-function strengthening" plan from
[docs/todo.md](todo.md#active-plan-call-ue-function-strengthening),
specifically item 1 (AA-Script export from UI). Three commits on `dev`:
[`93f9fd6`](../scripts/ue5_invoke_helper.lua) helper + embed,
[`c3c27e9`](../ui/UE5DumpUI/Services/BakedScriptGenerator.cs) generator
+ dialog + button, and the Tools menu + tests in this commit.

### The problem

The existing `Generate Script` button on UFunction rows produces a CE
AA Script that builds a `createForm` dialog **inside Cheat Engine** so
the user fills params at runtime. That's fine for in-CE testing but
unsuitable for shipping a static cheat table — every "Add Money" needs
the user to type 1000 in a popup every time the script enables.

### Architecture (helper-in-table pattern)

Initial proposal was "self-contained AA scripts that inline the entire
mailbox protocol". User pushed back with a much better model from his
own CE table experience (Crimson Desert): embed a shared helper file
in the .CT itself and have AA scripts load it via `findTableFile`. New
two-artifact design:

**Artifact 1: [`scripts/ue5_invoke_helper.lua`](../scripts/ue5_invoke_helper.lua)**
(new, ~285 lines)

Public API exposed via re-declaration-safe pattern matching the
celua_*.lua convention:

```lua
if not invokeUFunction then
  function invokeUFunction(className, funcName, parmsSize, params)
    ...
  end
  registerLuaFunctionHighlight('invokeUFunction')
end
```

Two functions: `invokeUFunction` (CMD_INVOKE_BY_NAME via mailbox) and
`readUFunctionReturn` (typed read from params buffer). Internal
`writeBakedParams` accepts a flat param array `{ {name, type, offset,
value}, ... }` and dispatches by the helper-token type:
`bool`/`byte`/`int16`/`int32`/`int64`/`float`/`double`/`pointer`. All
pointer-shaped UE types (object/class/name/soft/weak/lazy/interface)
collapse to `'pointer'` and the helper writes them as a qword.

Strict input validation (non-empty strings, parmsSize range check),
pcall-protected mailbox lookup so missing `g_invokeMailbox` surfaces
as a clean `(false, err)` return rather than a Lua trace, and a
sentinel print matching `[*] ue5_invoke_helper.lua v1.0 loaded`.

**Artifact 2: Generated AA Script** (~50 lines per script vs ~200 in
the form-based generator)

```text
[ENABLE]
{$lua}
if syntaxcheck then return end
-- Setup instructions: Table -> Add File... -> ue5_invoke_helper.lua

local tf = findTableFile('ue5_invoke_helper.lua')
if not tf then
  showMessage('[Invoke] ue5_invoke_helper.lua not found in this table.\n...')
  if memrec then memrec.Active = false end
  return
end
do
  local ss = createStringStream()
  ss.copyFrom(tf.Stream, tf.Stream.Size)
  local fn, err = load(ss.DataString)
  ss.destroy()
  if not fn then ... return end
  fn()
end

-- ====== BAKED PARAMS (edit values here) ==============================
local PARAMS = {
  { name='Amount',     type='int32', offset=0, value=1000 },  -- int32 4B
  { name='bShowToast', type='bool',  offset=4, value=1 },     -- bool 1B
}
-- =====================================================================

local ok, err = invokeUFunction('PlayerCharacter', 'AddMoney', 5, PARAMS)
if not ok then
  print(...) ; showMessage(...)
end

local t = createTimer(nil, false)
t.Interval = 100
t.OnTimer = function(s)
  s.Enabled = false; s.destroy()
  if memrec then memrec.Active = false end
  if ok then
    synchronize(function() getLuaEngine().Close() end)
  end
end
t.Enabled = true
{$asm}
```

Hygiene rules baked in (per the user's deployment guidance):
- **No filesystem fallback** for the helper — explicit error +
  showMessage tells the user how to add the file (no surprise loading
  from random paths)
- **Silent on success** — auto-disables the memrec then closes the lua
  engine via `synchronize(getLuaEngine().Close())` so the user doesn't
  see a stray output window pop on every enable
- **Errors keep the window open** so the user can read the message
- **`-- BAKED PARAMS` block** clearly delimits the editable section so
  cheat-table maintainers can tweak values without understanding the
  mailbox protocol

### UI integration

[`InvokeParamDialog.cs`](../ui/UE5DumpUI/Views/InvokeParamDialog.cs)
gains an `InvokeDialogMode` enum with two values:

| Mode | FIRE | Copy AA Script | Close | Cancel | Opened from |
|---|:-:|:-:|:-:|:-:|---|
| `PipeInvoke` | ✓ | ✓ | ✓ | ✓ | LiveWalker `Pipe Invoke` button |
| `CopyBakedScript` | — | ✓ | — | ✓ | LiveWalker `AA(Baked)` button |

The dialog's existing `_structEdits` map is reused to flatten struct
arguments — `CollectBakedValues()` emits one
[`BakedParamValue`](../ui/UE5DumpUI/Models/BakedParamValue.cs) per
sub-field with absolute offset (`parent.Offset + sub.Offset`) and
display name `Parent.Sub`. The generator never sees nesting.

[`LiveWalkerPanel.axaml`](../ui/UE5DumpUI/Views/LiveWalkerPanel.axaml)
column widened from 100 → 320 to fit the third button. The new
`AA(Baked)` button has a 0-arg fast-path: for void-or-no-params
functions the dialog is skipped and the script is generated +
delivered immediately.

[`MainWindow.axaml`](../ui/UE5DumpUI/Views/MainWindow.axaml) gains a
`Tools` dropdown (always available, doesn't require a DLL connection)
with one entry: **Export CE Helper Lua File...**. Streams the
embedded `ue5_invoke_helper.lua` to a user-chosen path via
`IPlatformService.ShowSaveFileDialogAsync` so the user can drop it
next to their .CT for the Add File step.

### Embed mechanics

[`UE5DumpUI.csproj`](../ui/UE5DumpUI/UE5DumpUI.csproj) gets an
`<EmbeddedResource>` link to `scripts/ue5_invoke_helper.lua` with a
stable LogicalName `UE5DumpUI.Resources.CE.ue5_invoke_helper.lua`.
Single source of truth lives in `scripts/` — the resource link is
just a packaging hint.
[`HelperLuaResource.cs`](../ui/UE5DumpUI/Services/HelperLuaResource.cs)
reads it via `Assembly.GetManifestResourceStream` (AOT-clean, no
reflection-based resource lookup). A diagnostic `ListEmbeddedNames`
helper exists for when the manifest name drifts.

### Tests (+36)

Extended
[`InvokeScriptTests.cs`](../ui/UE5DumpUI.Tests/InvokeScriptTests.cs)
with a `BakedScriptGenerator` section covering:

- Structural shape (ENABLE/DISABLE blocks, helper loader present, no
  filesystem fallback wording, `getLuaEngine().Close()` cleanup,
  `createForm` absent so no accidental interactive UI)
- Per-type literal rendering (int decimal, float with InvariantCulture,
  bool true-variants → 1 / false-variants → 0, object pointer hex,
  zero pointer as plain `0`, negative int sign-preserved, hex input
  preserves hex form)
- Multi-param row generation at correct offsets
- Struct sub-field flattening (3-field FVector style → 3 rows with
  `Location.X`/`Y`/`Z` names at consecutive offsets)
- Edge cases: unparseable input falls through to `--[[unparsed:...]]
  0` literal that *flags* the problem instead of mangling it; Lua
  single-quote escape via `EscapeLua` (`O'Brien` → `O\'Brien`); Lua
  comment-close `]]` in the unparsed payload escaped to `] ]` so it
  can't terminate the comment early
- `[Theory]` of all 17 supported UE TypeName → helper-token mappings
- `HelperLuaResource.Read()` returns non-empty content with the
  expected sentinel + public function names — catches packaging
  regressions where the EmbeddedResource link is silently broken

Tests went from 597 → **633** (504 → 540 C# + 62 dll_helpers + 31
utf8_helpers).

### What's covered now / what's pending

Done (top of the call-UE-function strengthening plan): UFunction →
non-interactive AA Script with baked params, deployable as a static
piece of a CE table. Tools-menu helper export closes the loop on
"how does the user get the helper file in the first place".

Still pending in the same plan (in [docs/todo.md](todo.md)):
2. Interesting-functions finder (keyword scorer surfacing
   HP/MP/Gold/Teleport/etc.)
3. UFunction metadata exposure (Blueprint `DisplayName`/`ToolTip`/
   `Category`)
4. Finder rev2 — fold metadata into the scorer
5. InvokeParamDialog ScrollViewer overflow fix

**Build #596, 633 tests passing (540 C# + 62 dll_helpers + 31
utf8_helpers).**

-----

## 2026-05-10 (build 578-589) — Walker false-positive sweep + per-game invoke timeout + FillPointerSnapshot + drill-depth 0-6 band

Three feature commits on `dev` (`c4f0644`, `95722fd`, `e49a599`) driven by
analysis of cross-game logs (build 449 user submission with 7 games + the
local ff7rebirth_ / DQ I&II / TQ2 / Meltopia / ES2 sessions). Awaits PR
to `main`.

### Phase A — walker false-positive sweep + new Scharf helper (build 582-583, c4f0644)

Cross-game log review surfaced four loud-but-harmless WALK warning classes
hammering scan logs across multiple titles:

| Warning | Source | Worst offender / count |
|---|---|---|
| `Misaligned EnumProperty/NameProperty field` | hardcoded need4/need8 in [`Ubel.cpp`](../dll/src/Ubel.cpp) | ff7rebirth_ 2550, DQ I&II 943, Meltopia ~75/session |
| `Cannot read map elements` on default-initialised TMap | [`Ubel.cpp`](../dll/src/Ubel.cpp) `count==0 && Data==null` | CaravanSandWitch 49 |
| `ValidateArrayElemSize` → recovery succeeded | [`Ubel.cpp`](../dll/src/Ubel.cpp) Phase B | TQ2 UE 5.7 — 194/session |
| `walk_instance` exception on placeholder | `Renge::StrToAddr` throwing `std::invalid_argument` | SquirrelGun (`0x[ply_base]`) |

The misalignment heuristic was the loudest. Old check assumed:

- Every `EnumProperty` is 4-byte aligned — wrong for `uint8` enums
  (1-byte aligned, can sit at any offset)
- Every `NameProperty` is 8-byte aligned — wrong for non-CPN builds
  (FName=8, 4-byte aligned)

New [`Scharf.h`](../dll/src/Scharf.h) (Frieren character #17, "sharp-eyed
examinee" — fits FProperty layout sanity checking):

```cpp
namespace Scharf {
    int RequiredAlignment(string typeName, int elemSize, bool isCPN);
    bool IsAlignmentSuspicious(string typeName, int offset, int elemSize, bool isCPN);
}
```

`RequiredAlignment` returns:

- 0 for variable-layout types (`StructProperty` / `FieldPathProperty` /
  `OptionalProperty` / garbage names — skip validation entirely)
- For `EnumProperty` / `ByteProperty`: consults `FPROPERTY_ELEMSIZE`
  (1, 2, or 4 — uint8 enum is 1-aligned, uint32 enum is 4-aligned)
- For `NameProperty`: 4 if non-CPN, 8 if CPN (matches FName layout)
- Standard alignment for fixed-size scalars

Order-sensitive substring trap handled: `WeakObjectProperty` must match
**before** plain `ObjectProperty` (substring `Object` would otherwise
hijack). Pure header — picked up by both DLL and the new helper test exe
without DLL link dependencies.

`Ubel.cpp:362` calls into `Scharf::IsAlignmentSuspicious(typeName, offset,
elemSize, casePreservingName)`. Empty-map guard at `Ubel.cpp:3561` skips
the count=0 + Data=null branch (normal default-initialised TMap, not a
read failure). `ValidateArrayElemSize` warnings at `Ubel.cpp:943-961`
demoted to `LogDebug` — recovery (override known type / zero +
PropertiesSize fallback) always succeeds; the next-line `Inner found` Info
already shows the resolved size.

### Phase B — Pipe address parsing safety (build 582, c4f0644)

[`Renge.h`](../dll/src/Renge.h) `TryStrToAddr(string, uint64_t& out)`:
noexcept strict hex parser. Rejects:

- Unsubstituted CE placeholders (`"0x[ply_base]"` — root cause of the old
  SquirrelGun `walk_instance` crash with
  `std::invalid_argument` from `std::stoull`)
- Leading sign (`"-1"` was wrapping to `0xFFFF...FFFF` via 2's complement)
- Trailing garbage (`"0x123junk"`)
- Empty / whitespace-only

Legacy `Renge::StrToAddr` is now noexcept too (returns 0 on failure)
so any unconverted call site cannot crash the pipe loop.
[`Fern.cpp`](../dll/src/Fern.cpp) `walk_instance` handler upgraded to
`TryStrToAddr` and returns clean `"Invalid addr"` error.

### Phase C — `dll_helpers_test` second C++ test exe (c4f0644)

[`dll/tests/dll_helpers_test.cpp`](../dll/tests/dll_helpers_test.cpp) —
mirrors `utf8_helpers_test` style (no GoogleTest, EXPECT macros, exit
code = failure count). 62 assertions across 13 test groups:

- `TryStrToAddr`: CE placeholder rejection, trailing garbage, leading
  sign, empty/whitespace, valid 0x / module+RVA forms
- `Scharf::IsAlignmentSuspicious`: Meltopia uint8 enum @ 0x5F (must NOT
  warn), CaravanSandWitch FName @ 0x3C (must NOT warn — non-CPN), CPN
  FName @ 0x3C (MUST warn — needs 8-aligned), scalar primitives,
  WeakObjectProperty substring trap

Wired into `build.ps1 -Target Test` before the C# suite. Pulls in
`vendor/nlohmann` since `Renge.h` includes `json.hpp`.

### Phase D — Per-game GameThreadDispatch invoke timeout (build 583-588, c4f0644 + 95722fd)

Meltopia logs showed 4 separate UFunction-invoke timeouts on Blueprint
widget delegates (`BndEvt__OnClicked`, `SetShowCharacterState_BPI`).
Stark.cpp's old `constexpr 5s` was too tight for delegate chains that
lazy-load assets. Solution: per-game persisted timeout, mirroring the
existing `set_ue_version_override` shape.

**DLL backend** ([`Stark.cpp`](../dll/src/Stark.cpp), [`Stark.h`](../dll/src/Stark.h)):

- `Stark::s_invokeTimeoutMs` is now a runtime-modifiable
  `std::atomic<uint32_t>` (default 5000ms, clamp `[100, 600000]`).
- Public API: `Stark::SetInvokeTimeoutMs(ms)` / `GetInvokeTimeoutMs()`.
- Replaces every constexpr `std::chrono::milliseconds{5000}` reference.

**HintCache persistence** ([`Flamme.cpp`](../dll/src/Flamme.cpp),
[`Flamme.h`](../dll/src/Flamme.h)):

- New `Flamme::SaveInvokeTimeout(peHash, ms, processName)` writes
  `invokeTimeoutMs` + `invokeTimeoutMsAt` to the existing per-PE
  HintCache JSON.
- `LoadHints` reads them; `SaveResults` preserves on round-trip.
- Same shape as `ueVersionUserOverride` handling — code structure copied
  field-for-field.

**Auto-apply** ([`Genau.cpp`](../dll/src/Genau.cpp) `FindAll`):

- Calls `Stark::SetInvokeTimeoutMs(hints.invokeTimeoutMs)` early in
  scan, so any pre-scan UFunction call also uses the right timeout.

**Pipe** ([`Fern.cpp`](../dll/src/Fern.cpp)):

- New `CMD_SET_INVOKE_TIMEOUT = "set_invoke_timeout"` accepts
  `{timeout_ms, persist}` — mirrors `set_ue_version_override`.
  `timeout_ms=0` clears override, falls back to default 5000.

**UI** ([`PointerPanelViewModel.cs`](../ui/UE5DumpUI/ViewModels/PointerPanelViewModel.cs),
[`PointerPanel.axaml`](../ui/UE5DumpUI/Views/PointerPanel.axaml),
[`EngineState.cs`](../ui/UE5DumpUI/Models/EngineState.cs),
[`AobUsageRecord.cs`](../ui/UE5DumpUI/Models/AobUsageRecord.cs),
[`DumpService.cs`](../ui/UE5DumpUI/Services/DumpService.cs)):

- `EngineState.InvokeTimeoutMs` (default 5000 = `Stark::kDefaultInvokeTimeoutMs`).
- `AobUsageRecord.InvokeTimeoutMs` + `InvokeTimeoutMsAt` survive
  `RecordScanAsync` round-trip (record class is serializer-driven).
- `DumpService.SetInvokeTimeoutAsync` mirrors `SetUeVersionOverrideAsync`:
  send pipe cmd, log, re-fetch `get_pointers`, `ApplyState()` propagates.
- `PointerPanelViewModel.InvokeTimeoutMs` `[ObservableProperty]` +
  `OnInvokeTimeoutMsChanged` partial fires `ApplyInvokeTimeoutAsync`.
  `_suppressInvokeTimeoutEvent` gate stops refresh-driven assignments
  from re-firing apply.
- `ShowInvokeTimeoutOverrideBadge` surfaces "⏱ Custom" pill when
  value ≠ 5000.
- `NumericUpDown` (1000-60000, step 1000) sits below the UE Version
  Override row in the Pointer panel. 5000 = "clear override" payload.

### Phase E — `FillPointerSnapshot` refactor (build 588, 95722fd)

User reported FF7 Rebirth panel showed `invoke_timeout_ms=5000` despite
HintCache JSON having 6000, plus missing Square Enix chip + missing Low
Confidence badge. DLL log proved the value applied correctly
(`Genau::FindAll: Applied invoke timeout override: 6000ms`); the failure
was that the `CMD_SCAN_STATUS` completion payload was missing
`invoke_timeout_ms` / `is_user_override` / `is_low_confidence` /
`publisher_thumbprint`. UI consumes `scan_status` post-`trigger_scan`,
not `get_pointers`, so the gap meant new fields silently defaulted.

This is the **exact same trap** that bit `sparse_delegates` in PR #194's
first iteration (caught via pipe-trace log diff). Fix: extract a shared
[`Fern.cpp`](../dll/src/Fern.cpp) `FillPointerSnapshot(json& data)`
helper used by both `CMD_GET_POINTERS` and `CMD_SCAN_STATUS` completion.
One helper, two call sites — bug class permanently closed.

### Phase F — UI strict address validation (95722fd)

[`AddressHelper.cs`](../ui/UE5DumpUI/Core/AddressHelper.cs)
`TryNormalizeAddress(input, moduleBase, out string normalized)`:
bool-returning variant of `NormalizeAddress` that mirrors the DLL's
`Renge::TryStrToAddr` semantics — rejects non-hex bodies, leading sign,
trailing garbage. Legacy `NormalizeAddress` becomes a thin wrapper
returning `"0x0"` on failure (back-compat for any caller missed).

InstanceFinder Lookup + LiveWalker Go button now show `"Invalid address —
expected hex (e.g. 0x7FF... or module.exe+RVA)"` instead of the
misleading `"No UObject found at this address"` (which previously fired
because the noexcept `StrToAddr` silently parsed garbage as 0 and the
DLL searched at addr 0).

### Phase G — Drill Depth 0-6 with warning band (95722fd)

[`MainWindow.axaml`](../ui/UE5DumpUI/Views/MainWindow.axaml) slider
`Maximum 4 → 6`, width `80 → 100` to fit two extra ticks.
TextBlock `Foreground` binds to `CsxDrilldownDepthBrush`:

| Depth | Brush | Reason |
|---|---|---|
| 0-4 | default | safe range, was the old max |
| 5 | amber `#E6A817` | exponential growth becomes noticeable |
| 6 | red `#E05252` | a UWorld drill at depth 6 can produce multi-MB CE XML |

Cycle-elision (build 552 fix) + `MaxEmitPointerDepth=16` keep depth 6
*safe* (no crash); the colour band is advisory only.
`LiveWalkerViewModel.CsxDrilldownDepth` converted from auto-property to
`[ObservableProperty]` so brush re-computes; both VMs (Main +
LiveWalker) carry their own brush since the slider binds to Main and
propagates via `OnCsxDrilldownDepthChanged`.

### Phase H — README tested games matrix (e49a599)

Added 6 games verified via cross-game logs:

- **4.18-4.20 row**: + The Occupation (UE 4.19, GNAM_CT3 path)
- **4.25-4.27 row**: + TimeSplitters Rewind Early Access V0.3.3
- **5.0-5.2 row**: + Squirrel With A Gun, Caravan Sandwitch, Meltopia,
  Retro Rewind Demo (replaces the old "(Confirmed via generic patterns)"
  placeholder)

GWorld success ratio: 19/20 (~95%) → 25/26 (~96%). Restore Your Island
skipped — proxy DLL loaded but UI never connected.

### Tests (+96 over build 577 baseline)

Tests grew from 532 (31 C++ + 501 C#) → **597 total (504 C# + 62
dll_helpers + 31 utf8_helpers)**:

- 3 new `DumpServiceTests`:
  - `SetInvokeTimeoutAsync_SendsCorrectPayloadAndRefetches`
  - `SetInvokeTimeoutAsync_ZeroClearsOverride`
  - `GetPointersAsync_DefaultsInvokeTimeoutWhenAbsent` (back-compat with
    older DLL builds that don't include the field)
- `AobUsageServiceTests` round-trip preservation extended to verify
  `InvokeTimeoutMs` + `InvokeTimeoutMsAt` survive `RecordScanAsync`.
- New `dll_helpers_test` exe with 62 assertions (TryStrToAddr + Scharf).

### Bug class caught + future-proofed

The `FillPointerSnapshot` extraction is the more important
infrastructural change — same shape bug had now bitten **twice**
(`sparse_delegates` in PR #194, then `invoke_timeout_ms`/publisher fields
in this round). The shared helper means any future "added a field to
get_pointers" change automatically propagates to scan_status without
needing a second edit in a separate code path. If a third instance
appears post-589, the answer is to add a regression test that diffs the
two payload schemas, not to write the snapshot field a third time.

### Cross-version validation

| Game | UE | Was loud about | Now |
|---|---|---|---|
| Meltopia | 5.0.5 | ~75 misalignment + 4 invoke timeouts | clean (Scharf + per-game timeout) |
| ff7rebirth_ | 4.27 (override) | 2550 misalignment | clean; 6000ms timeout round-trip works (FillPointerSnapshot fix) |
| DQ I&II HD-2D | 4.27 (override) | 943 misalignment | clean |
| CaravanSandWitch | 5.0.4 | 49 empty-map false-positives + 4 misalignment | clean |
| TQ2 | 5.7 | 194 ValidateArrayElemSize warnings | clean (demoted to Debug) |
| SquirrelGun | 5.0.2 | `walk_instance` crash on `0x[ply_base]` | clean (TryStrToAddr) |

### What's still pending

- **PR `dev` → `main`** — these 3 commits haven't been merged yet
  (this dev-log refresh was the prerequisite).
- **UE 4.23-4.27 sparse delegate**: outer key is `FObjectKey`, walker
  still returns `supported=false` for UE < 5.0. Carryover from build
  561-577.
- **FieldPathProperty**: still the only remaining drill-down type with
  no specialized handler.
- **Other publishers** with unreliable version strings: only
  `SQUARE_ENIX` is in the `kPublishers[]` table. Adding casually risks
  wrong bias overriding correct detection — wait for a real misdetection
  report before adding.

**Build #589, 597 tests passing (504 C# + 62 dll_helpers + 31
utf8_helpers).** 3 commits ahead of `origin/main` on `dev`.

-----

## 2026-05-09 (build 561-577) — MulticastSparseDelegateProperty: walker + Find Refs v4 sparse + Pointer panel

`feat(walker)` + `feat(refs)` + `feat(panel)` series — closes the
`MulticastSparseDelegateProperty` drill-down gap that had been parked
since the multicast-inline path landed. Six feature commits + three
bumps + one cross-version validation note + one build-script fix on
`dev`, awaiting next PR to `main`.

### The problem

`MulticastSparseDelegateProperty` is unique among delegate flavours:
the field on a UObject only stores `FSparseDelegate { uint8 bIsBound; }`
(1 byte). The actual binding list lives in CoreUObject's static
`FSparseDelegateStorage::SparseDelegates` — a nested
`TMap<UObjectBase*, TMap<FName, TSharedPtr<FMulticastScriptDelegate>>>`.
Without locating that static, the walker could only surface the bound
flag, leaving the user staring at "(sparse, bound)" with no way to see
which functions were attached.

### Phase A — walker (build 561-563)

[`Himmel.h`](../dll/src/Himmel.h) gains
`AobTarget::SparseDelegates = 3` and `SPARSE_PATTERNS[]` containing
`SPARSE_ES2_1`, captured from PDB-loaded ES2 Ghidra disasm via
`FSparseDelegateStorage::FObjectListener::NotifyUObjectDeleted` middle:

```
48 8D 0D ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 48 8B ??     ; lea rcx,[crit]; call [EnterCrit]; mov rdx,rXX
48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ??                  ; lea rcx,[SparseDelegates]; call TSet::Remove
8B 05                                                 ; mov eax,[SparseDelegates+8]
```

The trailing `8B 05` is critical — it gives a "twin reference" to the
same static 8 bytes apart, which collapses false-positive count to
near zero. CE confirms exactly one match in ES2's 125 MB `.text`.

[`Genau.cpp`](../dll/src/Genau.cpp) `FindSparseDelegateStorage()`
runs the AOB scan with a TMap-header validator (sanity-checks
`ArrayMax` and `AllocationFlags.MaxBits` at +0x0C and +0x2C). Resolved
address is atomic-cached for the DLL lifetime. Originally lazy-on-first-
drill-down; promoted to eager FindAll phase 5 in Phase C so the panel
can show it.

[`Aura.cpp`](../dll/src/Aura.cpp) `WalkSparseDelegateBindings(owner, fname, max)`
is a 3-phase reader:

1. Linear-scan **outer** TSparseArray slots (stride 0x60) for a slot
   whose key matches `owner`. Allocation bits read from inline (≤128
   bits) or heap-secondary; each slot has TSetElement layout
   `{ K, V, HashNextId, HashIndex }`.
2. Linear-scan **inner** TSparseArray slots (stride 0x20 for FName=8,
   0x28 for case-preserving FName=16) for FName key match.
3. Deref `TSharedPtr<FMulticastScriptDelegate>` (16B: `Object*` +
   `RefCount*`), read `InvocationList: TArray<FScriptDelegate>`, walk
   each `{ FWeakObjectPtr, FName }`, resolve weak ptr to live UObject*
   via `Ubel::ResolveWeakObjectPtr`.

Version-gated to UE 5.0+ (UE 4.23-4.27 used `FObjectKey` outer key,
different stride and key-comparison; walker returns
`supported=false`).

[`Ubel.cpp`](../dll/src/Ubel.cpp) MulticastSparseDelegate handler now
calls the walker on `bIsBound == 1` and exposes results as an implicit
DelegateProperty array (same shape as MulticastInline), so drill-down,
CE XML / CSX export, and Find Refs target navigation all reuse
existing wiring with zero new UI plumbing.

### Phase B — Find Refs v4 sparse coverage (build 565)

Added a global pass to
[`Aura::FindReferencesToUObject`](../dll/src/Aura.cpp) that, after the
per-object loop, walks `FSparseDelegateStorage` once and checks every
binding's `FWeakObjectPtr` against the search target. Hits surface as
`ReferenceMatch` with `fieldType="MulticastSparseDelegateProperty"`
and `elementIndex` = position in InvocationList. Owner metadata read
directly from the owner UObject header (no GObjects linear-scan).

The shared TMap header / bit-array helpers (`ReadTMapHeader`,
`TMapBitSet`, `ResolveTMapBitArrayBase`) and layout doc were lifted
above `FindReferencesToUObject` so both readers share the same
primitives.

### Phase C — Pointer panel surfacing (build 567-574)

To verify cross-game without digging through `scan*.log`, the resolved
sparse address now displays in the Global Pointers tab. New
`FSparseDelegateStorage` row mirrors the GWorld layout (address +
pattern ID + AOB hit address + Copy buttons) with three states:

- **Found** (UE 5.0+ + AOB hit): blue address text with pattern ID
- **Not Found** (UE 5.0+, scan failed): amber warning row
- **Unsupported** (UE < 5.0): grey "walker not supported on this
  version" note

Wiring spans
[`Genau::EnginePointers`](../dll/src/Genau.h) (new fields),
[`Frieren.cpp`](../dll/src/Frieren.cpp) (cached globals),
[`Fern.cpp`](../dll/src/Fern.cpp) (`CMD_GET_POINTERS` and
`CMD_SCAN_STATUS` payloads), and a chain of UI files
([`EngineState.cs`](../ui/UE5DumpUI/Models/EngineState.cs),
[`DumpService.cs`](../ui/UE5DumpUI/Services/DumpService.cs),
[`PointerPanelViewModel.cs`](../ui/UE5DumpUI/ViewModels/PointerPanelViewModel.cs),
[`PointerPanel.axaml`](../ui/UE5DumpUI/Views/PointerPanel.axaml),
[`en.axaml`](../ui/UE5DumpUI/Resources/Strings/en.axaml)).

### Bug caught during integration

First panel iteration only added the new fields to `CMD_GET_POINTERS`
response. But after `trigger_scan`, the UI applies the **completion
payload from `CMD_SCAN_STATUS`** to refresh pointer state — that
payload was a separate code path and didn't include sparse fields,
so the panel always rendered "AOB not found" while the DLL log proved
the scan succeeded. Caught via pipe-trace log diff. Fix: mirror the
full pointer payload in `CMD_SCAN_STATUS` complete branch.

### Build infrastructure fix

`build.ps1 -Target Test` previously rebuilt the C# test project + ran
tests but did NOT republish `UE5DumpUI.exe` to `dist/`. After fast
"tweak UI binding, run tests, relaunch from dist/" cycles, users got
stale UI binaries — exactly what bit the sparse panel testing for ~30
minutes. `Test` is now in the same gate as `All`/`UI`, so the test
cycle always refreshes the dist/ exe.

### Cross-version validation

| Game | UE | bCasePreservingName | SparseDelegates RVA | Pattern hit |
|------|----|----|----|----|
| Everspace 2 | 5.4 | false (FName=8, inner stride 0x20) | `+9AA5F10` | SPARSE_ES2_1 |
| Titan Quest II | 5.7 | **true** (FName=16, inner stride 0x28) | `+D46D170` | SPARSE_ES2_1 |

Same pattern hits both games. The walker's `DynOff::bCasePreservingName`
branch absorbs the inner-TMap stride and FScriptDelegate-size
difference, so a single AOB covers UE 5.x. The TQ2 hit was the
critical test — it's the first game with case-preserving FName that
exercised the alternate stride; previously untested.

### What's covered now

- Drill into any `MulticastSparseDelegateProperty` field → see
  bindings as `Owner::Func` per row, navigable to target via Open
- Find Refs target hits surface multicast-sparse references
  (closes the v3 gap noted as "deliberately NOT covered")
- Pointer panel shows resolved storage address per session

### What's still pending

- **UE 4.23-4.27 sparse**: outer key is `FObjectKey { FWeakObjectPtr,
  int32 SerialNumber }` (16B + pad) instead of raw `UObjectBase*`.
  Stride changes ~0x60 → ~0x68 and key matching needs to reconstruct
  `FObjectKey` from `(owner, GetSerialNumber(InternalIndex))`. Walker
  returns `supported=false` for UE < 5.0 to make this gap explicit.
- **FieldPathProperty** (rare): still the only remaining drill-down
  type that has no specialized handler.

**Build #577, 532 tests passing (31 C++ + 501 C#).** 13 commits ahead
of `origin/main` on `dev`.

-----

## 2026-05-09 (release 560) — Utf8Helpers extraction + 31-case C++ self-test

`test(utf8): extract Utf8Helpers + add 31-case C++ self-test target`
([`Utf8Helpers.h`](../dll/src/Utf8Helpers.h),
[`utf8_helpers_test.cpp`](../dll/tests/utf8_helpers_test.cpp), build 559+)

Two functions historically lived in two files with the same invariant
("produce valid UTF-8 even from corrupt input") and that's exactly how
the wide-path FName surrogate handling missed sanitization for so long
(see the build 555 entry below). After fixing the immediate bug, the
follow-up was to lock the contract behind a unit test so the next
refactor can't re-introduce it.

### What moved

- `SanitizeUtf8` (was anonymous static in `Ubel.cpp`) and the
  surrogate-aware UTF-16 → UTF-8 encoder (was inline inside
  `Serie.cpp` wide path) are now `Utf8Helpers::Sanitize` and
  `Utf8Helpers::EncodeUtf16` in a single header-only `Utf8Helpers.h`.
  No `<Windows.h>`, no nlohmann, no MinHook — the test target can pick
  it up without dragging the rest of the DLL into linkage.
- Both [`Ubel.cpp`](../dll/src/Ubel.cpp) (`ReadFString`) and
  [`Serie.cpp`](../dll/src/Serie.cpp) (`GetString` wide path) call
  through to the helper. Single source of truth.

### Test target

`dll/tests/utf8_helpers_test.cpp` is a stand-alone executable: 31
assert-based cases, no GoogleTest / Catch2 dependency. Coverage:

**Sanitize** — ASCII passthrough + tab/lf/cr preservation; control
byte rejection (0x01..0x1F minus tab/lf/cr); valid multi-byte (NBSP /
CJK / emoji) round-trip; lone continuation bytes (the actual 0xA0 case
from Squad logs); CESU-8 surrogate encodings (the
`0xED 0xA0 0x80` = U+D800 case); overlongs (`0xC1 0x81` = 'A',
`0xC0 0x80` = NUL); truncated 2/3/4-byte sequences; mixed valid+bad;
**idempotency** (`Sanitize(Sanitize(x)) == Sanitize(x)`).

**EncodeUtf16** — ASCII; NUL stops; BMP characters (CJK, Latin-é);
surrogate pair → 4-byte UTF-8 (😀 = U+1F600 = 0xD83D + 0xDE00 →
0xF0 0x9F 0x98 0x80); multiple pairs back-to-back; lone high
surrogate; lone low surrogate; high+ASCII (no valid pair); reversed
order (low then high); mixed realistic; **the round-trip invariant**:
`Sanitize(EncodeUtf16(x)) == EncodeUtf16(x)` for any input — i.e. the
encoder never produces output the sanitizer would reject.

### Algorithm tweak driven by the suite

Sanitize previously emitted one `?` per byte even when the malformed
sequence was structurally well-formed (correct lead + correct number
of continuations) but semantically invalid (CESU-8 surrogate /
overlong). The Squad-style 3-byte CESU-8 input produced `???` instead
of `?`. New behaviour: when the sequence is structurally valid but the
decoded codepoint is bad, advance by `extra+1` and emit a single `?`.
Truncated sequences and lone continuation bytes still go per-byte (we
don't trust a malformed length claim — it might be random garbage that
just happened to start with 0xED). Output is more compact; validity is
unchanged.

### Build wiring

`dll/CMakeLists.txt` adds the `utf8_helpers_test` executable target;
proxy-only configures skip it (proxies use separate build dirs).
`build.ps1 -Target Test` now triggers
`cmake --build … --target utf8_helpers_test` before running the
executable. Ninja is a no-op when sources are unchanged; touched
`Utf8Helpers.h` correctly triggers rebuild. C# xunit suite runs after.

**Build #560, 532 tests passing (31 C++ + 501 C#).** Release shipped
to `main` via PR #193.

-----

## 2026-05-09 — UTF-16 surrogate fix in Serie::GetString wide path

`fix(fname): handle UTF-16 surrogates in Serie::GetString wide path`
([`Serie.cpp`](../dll/src/Serie.cpp), build 555+)

External user log against build 488 (Squad-Win64-Shipping, UE 5.7,
240,341 objects) showed 13 occurrences of:

```
[PIPE:cmd] PipeServer: Exception in command 'get_object_list':
[json.exception.type_error.316] invalid UTF-8 byte at index 1: 0xA0
```

All on `get_object_list`, which builds responses from
`Ubel::GetName` → `ReadFName` → `Serie::GetString`. The ANSI path
already sanitized non-ASCII to `?`, but the wide path naively encoded
*any* codepoint in `[0x800..0xFFFF]` as 3-byte UTF-8 — **including the
surrogate range 0xD800..0xDFFF**. Decoding the reported byte sequence:

```
0xE0 | (0xD800 >> 12)         = 0xED
0x80 | ((0xD800 >> 6) & 0x3F) = 0xA0   ← matches the error
0x80 | (0xD800 & 0x3F)        = 0x80
```

Output is well-formed CESU-8 but ill-formed UTF-8 — `nlohmann::json`
strict-validates and rejects it as `type_error.316`.

### Fix

- Detect well-formed UTF-16 surrogate pairs (high then low) and
  combine them into a single 4-byte UTF-8 sequence representing the
  U+10000+ codepoint. Required for emoji and supplementary-plane
  characters that some games (UE5 + custom Asian fonts, modded
  blueprints, player loadout names) embed in display names.
- Replace any lone surrogate (high without low, low without high)
  with `?`, matching the ANSI-path convention.

The bug had lived since the wide-path FName decoder was added —
build 488 (the user log) and build 552 (current main at the time)
both hit it. Squad happens to have enough objects with corrupt /
unusual names that the bad codepoints reliably trigger on every full
GObjects scan; smaller / cleaner games never noticed.

**Build #555, 501 tests still pass.** Test target was added the
following session (see build 559 entry above).

-----

## 2026-05-09 — ReadFString UTF-8 hardening

`fix(walker): sanitize ReadFString output to avoid nlohmann::json UTF-8 errors`
([`Ubel.cpp`](../dll/src/Ubel.cpp), build 553+)

First-pass mitigation for the same `nlohmann::json` UTF-8 failure
class. Audited string sources:

- `Serie::GetString` (FName) — already sanitizes; safe.
- `Ubel::ReadFString` (live FString values) — uses
  `WideCharToMultiByte(CP_UTF8, 0, …)` which silently produces
  ill-formed UTF-8 (CESU-8-style surrogate encodings) when game
  memory contains lone surrogates from corrupted / freed FStrings.
  *Plausible* source of the 0xA0 report.

(The actual root cause turned out to be in Serie's wide path —
see the build 555 entry — but the ReadFString hardening still
matters for genuinely corrupt FString values from freed memory.)

### Two-layer hardening

1. Switch to `WC_ERR_INVALID_CHARS` flag — function fails fast
   (returns 0) instead of producing CESU-8 when the wchar_t buffer
   has unpaired surrogates. On failure, fall back to lossy
   conversion with the original flag.
2. New `SanitizeUtf8` helper (later promoted to
   `Utf8Helpers::Sanitize` in build 559) — final byte-level walk
   that strictly validates the output: rejects overlongs, surrogate
   codepoints, truncated multi-byte sequences, stray continuation
   bytes — replacing each malformed run with `?`. Cheap O(N), only
   invoked on values that head into JSON.

**Build #553.**

-----

## 2026-05-09 — CE XML emit pointer cycle protection

`fix(export): break pointer cycle in CE XML emit + hard depth cap`
([`CeXmlExportService.cs`](../ui/UE5DumpUI/Services/CeXmlExportService.cs),
build 552+)

Drill Depth=2 on DQ I&II HD-2D crashed the UI with
`ArgumentOutOfRangeException` ("The length cannot be greater than the
capacity") at `System.Text.StringBuilder.AppendWithExpansion` —
output had grown to the ~2 GB ceiling.

Stack trace showed > 50 alternating frames of
`EmitDrilledPointer ↔ EmitFields`, classic infinite-recursion
blow-up. Root cause: `ResolvePointerInstancesAsync`'s `visited`
HashSet protects only the *resolve* phase. Once both A and B in a
cycle (e.g. UWorld → PersistentLevel → OwningWorld → UWorld) are
resolved, the resulting dictionary contains both entries, and the
*emit* phase has no idea — it just looks up by `PtrAddress` and
recurses, oscillating between them indefinitely.

### Fix

Path-based cycle detection in the emit phase (separate concern from
resolve):

- `_emitPath`: thread-static `HashSet<string>`, push/pop on each
  `EmitDrilledPointer` entry. If the target's `PtrAddress` is already
  on the path, drop a flat 8-byte hex leaf labeled
  `(cycle elided)` so the user still has a watchable address in CE —
  better than a blank or a hang.
- `MaxEmitPointerDepth = 16` + `_emitPointerDepth` counter.
  Belt-and-braces guard against any pathological-but-acyclic chain
  (resolve depth cap is 4, cascade can extend it slightly). Hits
  show `(max drill depth reached)` instead.

Both states are reset at every `Generate*` entry point alongside the
existing `_dropDownOwners` / `_dropDownDescriptions` reset, so the
protection holds across multiple exports in the same session.

No new tests — repro requires a live DLL/pipe with a self-referential
UE world, which the unit-test surface can't synthesize. Verified by
re-running Drill Depth=2 against DQ I&II HD-2D on build 552 (no
crash, output ~3 MB instead of OOM).

**Build #552, 501 tests still pass.**

-----

## 2026-05-09 — Per-game UE version override + SquareEnix publisher bias + Tier 3 hardening

`feat(detect): per-game UE version override + SquareEnix publisher bias + Tier 3 hardening`
([`Genau.cpp`](../dll/src/Genau.cpp),
[`Flamme.cpp`](../dll/src/Flamme.cpp),
[`Fern.cpp`](../dll/src/Fern.cpp),
[`PointerPanelViewModel.cs`](../ui/UE5DumpUI/ViewModels/PointerPanelViewModel.cs),
[`PointerPanel.axaml`](../ui/UE5DumpUI/Views/PointerPanel.axaml),
build 549+)

Three SquareEnix titles (FF7 Remake, FF7 Rebirth, DQ I&II HD-2D)
consistently misdetected as UE 5.0+ when they're really UE4 forks.
Shipped binaries strip the canonical `++UE?+Release-X.Y` strings AND
embed unrelated SDK 5.x.y strings that the bare-pattern scan happily
matches. PE VERSIONINFO is no help either — SquareEnix puts publisher
version (1.0.0.4) where UE games normally put engine version. Result:
wrong UEVersion drives wrong FSoftObjectPath layout in CE XML exports
and wrong FProperty offset selection.

### Three layers of fix

**1. Publisher thumbprint**
([`Genau.cpp`](../dll/src/Genau.cpp) `DetectPublisherFromPE`)

Reads PE `LegalCopyright` + `CompanyName` via `VerQueryValueW`,
matches against a small hardcoded table (currently `SQUARE_ENIX`
only — adding more publishers casually risks wrong bias overriding
correct detection). A match flips `bLowConfidence=true` regardless
of which Tier hit, because we have direct evidence the publisher
ships unreliable strings — even Tier 1 results may come from
bundled SDKs (PhysX, etc.).

**2. Tier 3 hardening**
([`Genau.cpp`](../dll/src/Genau.cpp) `DetectVersionDetailed`)

The bare `X.Y.D` pattern now requires an
`Engine` / `Unreal` / `UE4` / `UE5` / `++UE` anchor in a 256-byte
window AND defers the first hit so a real Tier 2 `Release-4.27`
later in the module beats an early stray `5.5.0` SDK string.
Tier 3 hits flagged `bLowConfidence` even when accepted.

**3. User override**
(Flamme + new pipe cmd + UI ComboBox)

New `set_ue_version_override` pipe cmd writes
`ueVersionUserOverride` into the per-PE
[`HintCache`](../dll/src/Flamme.cpp) JSON. `FindAll`'s priority is
now:

```
user override > cached high-confidence > fresh detection >
publisher bias > 504 default
```

Override survives game restarts (HintCache reads it on next
launch) and is the highest priority — beats cached detection so a
wrong cache from older builds is recoverable.

Critical [`AobUsageRecord.cs`](../ui/UE5DumpUI/Models/AobUsageRecord.cs)
schema change: added `UEVersionUserOverride` +
`UEVersionUserOverrideAt` fields so the C# `AobUsageService` doesn't
silently drop these on its read-modify-write cycle. STJ source-gen
with `PropertyNamingPolicy.CamelCase` produces `ueVersionUserOverride`
which matches what Flamme writes (verified against the existing
cache file on disk).

### UI

[`PointerPanel.axaml`](../ui/UE5DumpUI/Views/PointerPanel.axaml)
gains a 3-state badge:

| State | Badge | When |
|---|---|---|
| Detected | ✓ green | `bVersionDetected && !bUserOverride && !bLowConfidence` |
| User Override | 🔧 blue | `bUserOverride` |
| Low Confidence | ⚠ amber | `bLowConfidence && !bUserOverride` |

Plus a Publisher chip ("Square Enix" purple) when any thumbprint
matched, and a `Override:` ComboBox (Auto / UE 4.18-4.27 / UE
5.0-5.8) — selection fires `set_ue_version_override` and refreshes
all panels via the existing `RescanApplied` event.

### Tests (+5)

- `DumpService InitAsync_ParsesUserOverrideAndLowConfidence`
- `DumpService InitAsync_ParsesLowConfidenceFlag`
- `DumpService SetUeVersionOverrideAsync_SendsCorrectPayloadAndRefetches`
- `DumpService SetUeVersionOverrideAsync_ZeroClearsOverride`
- `AobUsageService RecordScan_PreservesUserOverrideAcrossRoundTrip`
  (catches the regression where AobUsageService would clobber the
  field Flamme wrote)

### Verified

DQ I&II HD-2D — initial run had cached `versionDetected=true` from
build 488 era; new logic ignores cache when publisher is matched,
re-runs detection, surfaces ⚠ Low Confidence + Publisher: Square
Enix. User can then pick UE 4.27 in the ComboBox; subsequent
launches auto-apply the override.

**Build #549, 501 tests passing (+5 since build 547).**

-----

## 2026-05-09 (build 547) — Find Refs auto-drill into element [N]

`feat(walker): Open-from-Find-Refs auto-drills into array/map/set element`
([`LiveWalkerViewModel.cs`](../ui/UE5DumpUI/ViewModels/LiveWalkerViewModel.cs),
build 547+)

When Find Refs returned a hit on an array / map / set element (e.g.
`OwnerObj.ActiveAbilities[3]`), clicking **Open** previously navigated
to the owner UObject and auto-scrolled to the *container* row only —
the user then had to click the `[N]` drill button manually to land on
the actual element that held the pointer. With several long Find Refs
sessions in a row that's a noticeable friction point that several
shipped CE tables built around the workflow run into.

The previous code had this comment as an intentional cap:
> *Element index is NOT auto-drilled (would require a second
> navigation step into the container view).*

This change wires that second step.

### Auto-drill chain

`OpenReferenceOwnerAsync` now sets a new `_pendingDrillElementIndex`
state alongside the existing `_pendingScrollFieldName`, but only when
the FieldName refers **directly** to the container — accepted forms
are `Container`, `Container.Key`, `Container.Value`. Nested struct
paths like `Stats.Equipment` are **not** auto-drilled (the user still
has to manually walk into the struct first; same behaviour as before
for that case).

`UpdateDisplay`'s post-load scroll handler:
1. Find the container field by name → scroll into view (existing).
2. NEW: if `_pendingDrillElementIndex >= 0` AND the field
   `IsContainerNavigable`, re-arm `_pendingScrollFieldName = "[N]"`
   and fire-and-forget `NavigateToContainerAsync(field)`.
3. Container loads → `Populate{Array|Map|Set}ContainerFields` calls
   the new `ApplyPendingElementScroll` helper which scrolls to the
   element entry whose name is `[N]` (Array / Set) or starts with
   `"[N] "` (Map's `"[N] keyDisplay"` naming pattern).

### Cleanup hooks

`NavigateToAddressAsync` resets `_pendingDrillElementIndex` to `-1`
on entry **only when no pending scroll hint is set** — the guard
preserves the OpenReferenceOwner-set state across its own call into
NavigateToAddressAsync. Other navigation paths (manual address Go,
breadcrumb back, etc.) drop stale drill state cleanly.

### Result

- Open from `OwnerObj.ActiveAbilities[3]` → lands on `[3]` selected
  + scrolled into view in the container (0 manual clicks)
- Open from `OwnerObj.ItemTable.Value` (map value side, sparseIdx=2)
  → lands on `[2] keyName → valueName` row in the map view
- Open from `OwnerObj.Stats.Equipment[1]` (struct-nested) →
  unchanged: scrolls to `Stats` field, user manually drills in
  (same as before — no regression)
- Open from container field hit with element_index=-1 → unchanged

### Tests

The chain is event-driven async UI navigation that requires a live
DLL/pipe to verify end-to-end (DataGrid scroll calls, real
WalkInstance results). Existing test surface (496 tests) still
covers the underlying export / VM logic; the auto-drill is built on
top of already-tested NavigateToContainerAsync /
PopulateContainerFields paths.

**Build #547, 496 tests passing.**

-----

## 2026-05-09 (mid-latest) — CE XML drill-down: cascade struct resolution + OptionalProperty handler

`fix(export): nested StructProperty inside drilled pointer targets +
OptionalProperty CE XML emit`
([`CeXmlExportService.cs`](../ui/UE5DumpUI/Services/CeXmlExportService.cs),
[`CsxExportService.cs`](../ui/UE5DumpUI/Services/CsxExportService.cs),
build 544+)

Two follow-up issues from build 541:

1. **StructProperty inside drilled pointer targets renders as empty
   `<GroupHeader>` placeholder.** Selecting `ScalabilityModifiers`
   (ObjectProperty) and Copy CE Field with Drill Depth=4 correctly
   drilled into `MapScalabilityModifierComponent`, but the inner
   `PrimaryComponentTick (ActorComponentTickFunction)` (StructProperty)
   came out as a group header with no children — same with
   `ComponentTags` / `AssetUserData` (ArrayProperty placeholders).
   The OLD CE XML output (without drill-down) used to expand
   `PrimaryActorTick` fully because it was top-level and got picked up
   by `ResolveStructFieldsAsync`; the new drill-down code path missed
   the cascade entirely.
2. **OptionalProperty fields silently vanished from CE XML.** Falling
   through `EmitFields` they hit no handler and got dropped. ES2's
   `MapScalabilityModifierComponent.VolumetricFogOverrides` (an
   `OptionalProperty<FBox>`) and the test fixtures all disappeared
   from the export.

### Root cause

`resolvedStructs` was keyed by `int field.Offset` and only built for
the root instance's struct fields. When the drill-down resolver
returned a target's children, those children carried their own struct
fields (with their own offsets within the drilled target) — but the
dict had no entry for them, so `EmitFields` fell through to the
navigable-placeholder path. Worse, the offset key collides across
instances (offset 0x30 in object A vs object B).

### Fix

- **`resolvedStructs` re-keyed `int -> string`**: the new key is
  `LiveFieldValue.StructDataAddr` (absolute address of the struct
  data — unique across instances). One dict can now serve struct
  fields anywhere in the drilled tree without collisions.
- **`ResolveStructFieldsAsync`** now writes into a passed dict via a
  new `ResolveStructFieldsIntoAsync` private helper. The public
  signature returns the new string-keyed dict.
- **`ResolvePointerInstancesAsync`** gained an optional
  `resolvedStructs:` parameter — when provided, every drilled
  target's fields are also walked for `StructProperty` and the
  results merged into the same dict. So drilling into A → finds
  StructProperty B inside A → walks B → adds B's sub-fields keyed by
  B's StructDataAddr → emit-time lookup finds them.
- **OptionalProperty handler** added to `EmitFields`:
  - When `StructDataAddr` is stamped (Optional&lt;Struct&gt; when
    set, walker populates the same `{StructDataAddr, StructClassAddr,
    StructTypeName}` triple as bare StructProperty), goes through
    `EmitResolvedStruct` → struct sub-fields rendered inline.
  - Otherwise falls through to a flat 8-byte hex leaf (at minimum CE
    has a watchable address for the optional slot).
  - The cascade resolver also picks up Optional&lt;Struct&gt; fields
    (treats them as StructProperty for resolution purposes).
- **`CsxExportService.EmitStructPropertyFlattened`** adapted to the
  new string-keyed dict (CSX shares `ResolveStructFieldsAsync`).
- **LiveWalkerViewModel** passes `resolvedStructs:` through to
  `ResolvePointerInstancesAsync` for both `ExportCeXmlAsync` and
  `ExportCeFieldXmlAsync`.

### Tests

7 existing tests re-keyed (`Dictionary<int, ...>` → `Dictionary<string, ...>`
with `"0xABC"` matching `StructDataAddr`). 3 new:
- `DrilledPointer_NestedStructProperty_ExpandsViaResolvedStructsCascade`
  — regression coverage for the headline bug
- `OptionalProperty_NoStructInner_EmitsFlatLeaf`
- `OptionalProperty_StructInner_ExpandsToStructGroup`

**Build #544, 496 tests passing** (was 493).

-----

## 2026-05-09 (mid-late) — CE XML pointer drill-down + Property Search scroll restore

`feat(export): CE XML/CE Field N-level ObjectProperty drill-down (depth slider)`
([`CeXmlExportService.cs`](../ui/UE5DumpUI/Services/CeXmlExportService.cs),
[`LiveWalkerViewModel.cs`](../ui/UE5DumpUI/ViewModels/LiveWalkerViewModel.cs),
[`PropertySearchPanel.axaml.cs`](../ui/UE5DumpUI/Views/PropertySearchPanel.axaml.cs),
build 541+)

Two related issues from the build-534 session:

1. **Copy CE Field on `ScalabilityModifiers` (ObjectProperty) only emits a
   single 8-byte hex leaf.** The build-534 fix made the leaf shape valid
   (`<VariableType>8 Bytes</VariableType>` instead of the broken
   `<GroupHeader>1</GroupHeader>` placeholder), but users with
   already-shipped CE tables expected the export to **follow the
   pointer** and include the target's children — exactly the same way
   the CSX export's `drilldownDepth` slider already worked. Their
   workflow is "copy field → paste into CE → instantly inspect the
   referenced UObject's UPROPERTYs without manually re-poking offsets".

2. **Property Search loses scroll position + visible selection** when
   switching tabs (Property Search → Instance Finder → back). The VM
   keeps `SelectedResult` populated, but Avalonia's TabControl swaps
   the tab content out and back in, and the freshly-attached DataGrid
   doesn't auto-scroll to its `SelectedItem` — the highlighted row is
   offscreen so it visually looks like the selection was cleared.

### CE XML pointer drill-down

Mirrors the CSX implementation almost verbatim:

- New `CeXmlExportService.ResolvePointerInstancesAsync(dump, fields,
  depth, arrayLimit)` ports
  `CsxExportService.ResolvePointerInstancesAsync` — recursive walk
  through `ObjectProperty` / `ClassProperty` /
  `WeakObjectProperty` / `Soft*` / `LazyObjectProperty` /
  `InterfaceProperty` targets, depth-capped, with a shared `visited`
  HashSet for cycle protection. Returns `Dictionary<PtrAddress,
  Fields>` so emit-time lookup is O(1).
- `EmitFields` learned a new branch: when `resolvedInstances` has the
  field's PtrAddress and the type is in the `IsObjectPropertyType` set,
  call `EmitDrilledPointer` instead of the flat leaf path.
- `EmitDrilledPointer` writes the leaf as
  `<GroupHeader>1</GroupHeader> <Address>+{fieldOffset}</Address>
  <Offsets><Offset>0</Offset></Offsets>` followed by a recursive
  `EmitFields` over the target's children at their natural offsets
  within the dereferenced UObject. Description is decorated with the
  resolved `PtrClassName` so `BP_X (UCharacter)` is distinguishable
  from `BP_X (UPawn)` without expanding.
- `GenerateHierarchicalXml` / `GenerateInstanceXml` /
  `GenerateAobWrappedXml` all gained an optional
  `resolvedInstances:` parameter that flows through to `EmitFields`.
- `LiveWalkerViewModel.ExportCeXmlAsync` and `ExportCeFieldXmlAsync`
  pre-resolve via `ResolvePointerInstancesAsync(depth: CsxDrilldownDepth)`
  using the same toolbar slider.

### Slider repurposed

The 0-4 slider previously labelled `CSX Depth` (`str.Toolbar.CsxDepth`)
now drives drill-down for **both** CSX and CE XML / CE Field exports;
renamed to `Drill Depth:` and the tooltip clarifies the broader scope.
Backing property `CsxDrilldownDepth` is unchanged (preserves user
preferences).

### Property Search scroll restore

`PropertySearchPanel.axaml.cs` hooks `Loaded` → looks up
`ResultsGrid` (newly named `x:Name`) → schedules a
`Dispatcher.UIThread.Post(... grid.ScrollIntoView(vm.SelectedResult))`
at `Background` priority so the call runs after the DataGrid has
materialized its row containers (otherwise `ScrollIntoView` no-ops on
unrealized rows). Defensive try/catch covers recycled-grid /
missing-row cases.

### Tests

3 new in `CeXmlExportServiceTests` covering:
- ObjectProperty with resolved instance → GroupHeader + Offsets=[0] +
  children at natural offsets
- ObjectProperty with mismatched resolved-instance dict → falls back to
  flat leaf (no leaked children)
- Drilled children of common scalar types (FloatProperty / IntProperty)
  emit as proper leaves

**Build #541, 493 tests passing** (was 490).

-----

## 2026-05-09 (mid-latest) — CE Field/XML ObjectProperty leaf shape fix

`fix(export): emit ObjectProperty/ClassProperty/WeakObjectProperty as 8-Byte leaf`
([`CeXmlExportService.cs::MapCeField`](../ui/UE5DumpUI/Services/CeXmlExportService.cs),
build 534+)

Reported via Copy CE Field on `LocationInfo.ScalabilityModifiers`
(ObjectProperty): the resulting CE XML entry contained
`<GroupHeader>1</GroupHeader>` and **no `<VariableType>`** —
CE rendered it as an empty folder rather than a readable pointer:

```xml
<CheatEntry>
  <Description>"ScalabilityModifiers"</Description>
  <ShowAsHex>1</ShowAsHex>
  <GroupHeader>1</GroupHeader>     ← wrong: leaf is a folder
  <Address>+2C8</Address>
                                   ← wrong: missing <VariableType>
</CheatEntry>
```

**Root cause:** `MapCeField` had `TextProperty` / `Soft*Property` /
`LazyObjectProperty` / `InterfaceProperty` mapped to `("8 Bytes",
ShowAsHex: true)`, but **`ObjectProperty` / `ClassProperty` /
`WeakObjectProperty` were missing**. With `ceField == null`, `EmitFields`
fell through to `IsNavigable` → `EmitNavigableField` →
`EmitGroupPlaceholder`, which emits the GroupHeader-without-VariableType
shape. That code path was originally meant for *struct* navigation (no
scalar value, just a folder) — using it for raw object pointers
produced the buggy output.

**Fix:** add `ObjectProperty` / `ClassProperty` / `WeakObjectProperty`
to `MapCeField` returning `("8 Bytes", ShowAsHex: true)`. They now
emit through `EmitLeaf` and produce the same shape the soft / weak /
interface pointer types already produced:

```xml
<CheatEntry>
  <Description>"ScalabilityModifiers"</Description>
  <ShowAsHex>1</ShowAsHex>
  <ShowAsSigned>0</ShowAsSigned>
  <VariableType>8 Bytes</VariableType>
  <Address>+2C8</Address>
</CheatEntry>
```

Also covers null-pointer ObjectProperty fields (where `IsNavigable`
returns false because `PtrAddress` is empty) — they used to silently
drop from the export entirely.

**Tests:** 4 regression tests in `CeXmlExportServiceTests` covering
ObjectProperty / ClassProperty / WeakObjectProperty leaf shape and
null-ObjectProperty still-emits-leaf cases. **490 tests passing**
(was 486).

-----

## 2026-05-09 (mid-late) — Soft array CE XML: per-element FName leaf emission

`feat(export): TArray<TSoftObjectPtr> per-element CE XML group with FName leaf`
([`Ubel.cpp`](../dll/src/Ubel.cpp) Phase G, [`Fern.cpp`](../dll/src/Fern.cpp) array
JSON, [`CeXmlExportService.cs`](../ui/UE5DumpUI/Services/CeXmlExportService.cs),
build 532+)

Soft arrays (`TArray<TSoftObjectPtr>` / `TArray<TSoftClassPtr>`) used to
collapse to a single `8 Bytes` hex leaf per element in CE XML — only
`FWeakObjectPtr.ObjectIndex + SerialNumber` was addressable, and the
asset path FName at `+0x10` was invisible. Practical result: users had
to manually re-poke offsets in CE every time they wanted to read the
asset reference.

**Element layout the new emission writes** (DLL-provided `fnameSize` +
`isTopLevelAssetPath` flag let the exporter pick the right offsets per
UE version / CasePreservingName):

```
+0x00  WeakPtr   (8 Bytes hex)        — FWeakObjectPtr {ObjectIndex, Serial}
+0x10  AssetPath (4 Bytes, FName index) — UE4 / UE5.0
       PackageName (4 Bytes)            — UE5.1+ FTopLevelAssetPath
+0x10 + fnameSize  AssetName (4 Bytes)  — UE5.1+ only
```

Wire-up:
- **`Ubel.h`** gains two LiveFieldValue fields: `softArrayFNameSize`
  (8 normal / 16 case-preserving) and `softArrayIsTopLevelAssetPath`
  (true for UE >= 5.1). Stamped by both Phase G call sites (FProperty
  and UProperty fallback) so the metadata is present even when the
  array is empty.
- **`Ubel.cpp` Phase G reader** also writes
  `elem.rawIntValue = AssetPathName.ComparisonIndex` so the CE XML
  exporter can build a shared `<DropDownList>` mapping the FName index
  to the resolved asset path string (CE shows the path text in the
  Value column rather than a bare uint32).
- **`Fern.cpp`** serializes the two new fields as `soft_fname_size` and
  `soft_top_level_asset_path` on each ArrayProperty JSON object.
- **`DumpService.cs` + `LiveFieldValue.cs`** parse them into
  `SoftArrayFNameSize` / `SoftArrayIsTopLevelAssetPath`. The container
  navigation clone in `LiveWalkerViewModel` and the flatten clone in
  `CeXmlExportService.ResolveStructFieldsAsync` both forward the new
  fields so they survive the in-UI copies.
- **`CeXmlExportService.EmitSoftObjectArrayProperty`** is the new
  per-element emission. The outer array group keeps
  `Address=+{fieldOffset}, Offsets=[0]` (deref `TArray.Data`), each
  element becomes a sub-group at `+{N * elemSize}` containing the
  WeakPtr leaf, the AssetPath/PackageName FName leaf (with shared
  DropDownList), and on UE5.1+ also the AssetName leaf at
  `+0x10 + fnameSize`. Element description includes the resolved path
  (`[0] /Game/Items/IT_Potion.IT_Potion`).

Backwards-compat: when `SoftArrayFNameSize == 0` (legacy DLL or
deserialized payload without the new fields), the emission falls
through to the original 8-byte-hex path so older CE XML exports stay
readable.

**Tests:** 4 new + 1 backwards-compat case in
`CeXmlExportServiceTests.cs` covering UE4/UE5.0, UE5.1+ TopLevelAsset,
UE5.5+ CasePreservingName, SoftClassProperty, and the legacy fallback.
**486 tests passing** (was 483).

-----

## 2026-05-09 (mid) — OptionalProperty\<String/Name/Text\> intrusive specialization fix

`fix(walker): OptionalProperty intrusive isSet detection + value surfacing`
([`Ubel.cpp`](../dll/src/Ubel.cpp), build 530+)

Surface-tested OptionalProperty\<Struct\> on ES2 and noticed
`OptionalText` showing `(set)` despite the hex dump being all zeros,
while `OptionalString` neighbour with `Max=-1` correctly showed
`(unset)`. Investigation:

The `bIsSet` trailing-byte read at `field + sizeof(T)` is wrong for
heap-backed types — UE specializes `TOptional<T>` via
`FIntrusiveUnsetOptionalState` (see
[Misc/Optional.h `FOptional::IsSet`](../vendor/UnrealEngine/Engine/Source/Runtime/Core/Public/Misc/Optional.h#L26-L37))
so the "set" flag lives *inside* T's normal fields rather than as a
trailing byte. Reading past T lands on the next UPROPERTY's memory and
produces both false positives and false negatives depending on neighbour
layout. The 4 ES2 `OptionalPropertyTestObject` test fields all aliased
each other:

| Field        | innerSize used | bIsSet read addr   | Lands on...                                |
|--------------|---------------:|--------------------|--------------------------------------------|
| OptionalString @0x28 | 16 | 0x38 (next field start) | OptionalText TextData byte 0 = 00 ✓ unset |
| OptionalText @0x38   | 16 | 0x48 (next field start) | OptionalName ComparisonIndex = 0xFF ❌ false-positive set |
| OptionalName @0x48   | 8  | 0x50 (next field start) | OptionalInt int byte 0 = 00 ✓ unset |
| OptionalInt @0x50    | 4  | 0x54 (4 bytes in)       | trailing pad = 00 ✓ unset |

Fix: dispatch by inner type *before* the trailing-bIsSet fallback, with
sentinel checks lifted directly from each type's
`UEOpEquals(FIntrusiveUnsetOptionalState)`:

| Inner type      | Sentinel check                                | Source ref |
|-----------------|-----------------------------------------------|------------|
| `StrProperty`   | `int32` at field+12 (`FString::Max`) == `-1`  | [UnrealString.h.inl:212](../vendor/UnrealEngine/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h.inl#L212) |
| `NameProperty`  | `uint32` at field+0 (`FName::ComparisonIndex`) == `0xFFFFFFFF` | [NameTypes.h:76](../vendor/UnrealEngine/Engine/Source/Runtime/Core/Public/UObject/NameTypes.h#L76) |
| `TextProperty`  | `uintptr_t` at field+0 (`FText::TextData`) == `nullptr`       | [Internationalization/Text.h:837](../vendor/UnrealEngine/Engine/Source/Runtime/Core/Public/Internationalization/Text.h#L837) |

When set, the resolved contents are surfaced via the existing
`ReadFString` / `ReadFName` helpers and rendered as `"FooBar"` / `Bar`
instead of the placeholder `(set)`. `fv.strValue` is already wired
through `Fern.cpp::str_value` JSON, so the UI picks up the new field
without changes.

Other primitive scalar inners (Int/Float/Bool/Byte/Enum) don't have
intrusive specializations and stay on the trailing-bIsSet path —
that path is correct because those types leave at least one trailing
byte for the flag (e.g. `TOptional<int32>` is 8B).

`fi.Size` for these intrusive Optionals is reported as `sizeof(T)` (no
trailing flag), so the 64B hex cap stays correct without tweaking.

**Build / tests:** clean build #530, 483 tests passing.

-----

## 2026-05-09 (later) — OptionalProperty\<Struct\> drill-down + Find Refs descent

`feat(walker): OptionalProperty<Struct> inner sub-field surfacing`
([`Ubel.cpp`](../dll/src/Ubel.cpp), [`Aura.cpp`](../dll/src/Aura.cpp), build 528+)

`OptionalProperty<StructProperty>` was the highest-impact gap remaining
after the 2026-05-09 morning session — ES2 alone has 5 real game-class
cases (`WorldPartitionRuntimeCellData.CellBounds`,
`FontFace.PlatformRasterization`,
`MapScalabilityModifierComponent.VolumetricFog...`) plus the
`OptionalPropertyTestObject` test fixtures.

**WalkInstance change** ([`Ubel.cpp`](../dll/src/Ubel.cpp)):
The OptionalProperty handler already determined `isSet` via the trailing
`bIsSet` byte for non-pointer inners. A new branch runs after `isSet` is
known and before the display-string switch:

- Probe `FStructProperty::Struct` (UScriptStruct\*) on the inner
  FProperty, mirroring the single-value StructProperty handler's probe
  list (`{0, ±4, ±8, ±0x10}`) so a mis-detected `FSTRUCTPROP_STRUCT`
  self-corrects.
- When set, populate the standard
  `{structClassAddr, structDataAddr, structTypeName}` triple. The UI's
  existing `LiveWalkerViewModel.NavigateToFieldAsync` already routes
  these to `WalkInstanceAsync(structDataAddr, structClassAddr)` — no UI
  change needed for drill-down.
- Generate the inline preview from the cached `WalkClass(struct)` via
  one bulk-read of the struct bytes, formatting up to `previewLimit`
  scalar sub-fields (`{X=10.5, Y=200, ...}`). Same pattern as the bare
  `StructProperty` handler at line ~3861.
- Hex display cap raised from 64B → 256B for struct inners so
  `sizeof(TOptional<FBox>)` etc. fit comfortably.

Layout reminder: `TOptional<T>` for struct `T` is **always
non-intrusive** — `{ T value; uint8 bIsSet; }` — so the value lives at
field+0 (same as the bare struct case). The intrusive layout only
applies to pointer-shaped `T` (Object/Class/Interface, Weak/Soft/Lazy)
where null/zero is the unset sentinel.

Unset slots cleanly take the existing `(unset)` path because
`structDataAddr`/`structClassAddr` are only populated when `isSet`.

**Find Refs / Address Finder descent** ([`Aura.cpp`](../dll/src/Aura.cpp)):
Both `CollectContainersRecursive` (Address Finder) and
`CollectRefMetaRecursive` (Find Refs v3) gained a parallel
`OptionalProperty + StructProperty` branch alongside the existing
`StructProperty` descent. The recursion walks through the inner
UScriptStruct at the same `absOffset` (TOptional value sits at field+0),
so a UObject pointer buried inside an `Optional<Struct>` now surfaces
in the reverse scan with the dotted name `Field.SubField`. Depth cap of
3 still applies.

**Build / tests:** clean build, 483 tests passing.

-----

## 2026-05-09 — Find Refs v2/v3, OptionalProperty, Property Search UX, Class Structure fixes

Session focused on closing reverse-reference coverage gaps and fixing UI
papercuts in the Property Search / Game Class / Class Structure tabs.

### Reverse Reference Search (Find Refs)

`Aura::FindReferencesToUObject` walks every UObject's pointer-shaped
fields (and the containers that hold them) to answer "who logically owns
this object?" — UE's `OuterPrivate` is a naming hierarchy, not a gameplay
hierarchy, so for runtime-spawned objects the reverse scan is the only
way to surface real ownership.

**Find Refs v2** (build 511+, [`394a285`](../dll/src/Aura.cpp)):
extended from "ObjectProperty/ClassProperty + TArray<UObject*>" to:
- Direct `Object/Class/Interface` (8B raw pointer at field+0)
- Direct `Weak/Soft{Class}/Lazy` (resolves embedded `FWeakObjectPtr`)
- `TArray` of any of the above
- `TMap<UObject*, V>` / `TMap<K, UObject*>` (allocated slots only)
- `TSet<UObject*>` (allocated slots only)

**Find Refs v3** (build 519+, [`7efe862`](../dll/src/Aura.cpp)):
added delegate / multicast target scan:
- `DelegateProperty` (single `FScriptDelegate` — `FWeakObjectPtr` target
  at field+0)
- `MulticastInlineDelegateProperty` / `MulticastDelegateProperty`
  (FMulticastScriptDelegate is `TArray<FScriptDelegate>` at field+0;
  walks each binding's `FWeakObjectPtr`)
- `TArray<FScriptDelegate>` via `ArrayProperty<DelegateProperty>`

Stride for `FScriptDelegate` derives from `DynOff::bCasePreservingName`
(16 or 24) at runtime, so case-preserving builds compute the right
per-element step.

`MulticastSparseDelegateProperty` is **not** covered — bindings live in
CoreUObject's global `FSparseDelegateStorage`
(`TMap<FObjectKey, TMap<FName, TSharedPtr<FMulticastScriptDelegate>>>`),
not at the field. The AOB to locate that storage is universal (it's UE
engine code, same as GObjects/GNames/GWorld). The blocker is the
read-side TMap walk, not finding the address.

### OptionalProperty (UE 5.2+)

`feat(walker): OptionalProperty drill-down + Find Refs coverage`
([`8f52f63`](../dll/src/Ubel.cpp))

`TOptional<T>` ships in two layouts depending on `T`:
- **Intrusive** (UE 5.4+ for pointer types `Object/Class/Interface` and
  the `FWeakObjectPtr`-shaped `Weak/Soft/Lazy`): `T` directly at field+0,
  null/zero is the unset sentinel. `sizeof(TOptional<T>) == sizeof(T)`.
- **Non-intrusive** (older + non-pointer T): `{ T value; uint8 bIsSet; }`
  with the trailing flag at `field + sizeof(T)`.

`WalkClassEx` probes `FOptionalProperty::ValueProperty` using the same
`FARRAYPROP_INNER` offset (FOptional + FArray have the same shape:
`FProperty + FProperty*`), populating `innerType` /
`innerStructType` / `innerObjClass`.

`WalkInstance` dispatches by inner type:
- Object/Class/Interface: read pointer at field+0; null = unset
- Weak/Soft/Lazy: `{ idx=0, serial=0 }` = unset
- Scalar: trailing `bIsSet` at field+`ResolveInnerSize(inner)`
- Display: `(unset)` or rendered inner value

Find Refs reuses `directPointers` / `weakLikePointers` because the
intrusive layout puts T at field+0 — the comparison is identical to the
bare T. `fieldType` is reported as `OptionalProperty` so the user sees
the Optional wrapper in the result.

**Verified on Everspace 2** (UE 5.4): `OptionalPropertyTestObject`'s 4
test fields (Str/Text/Name/Int) plus 5 game-class StructProperty inners
(`WorldPartitionRuntimeCellData.CellBounds`, `FontFace.Platform...`,
`MapScalabilityModifierComponent.VolumetricFog...`).

### MulticastSparseDelegateProperty bound-flag surfacing

`feat(walker): MulticastSparseDelegateProperty bound-flag surfacing`
([`600045f`](../dll/src/Ubel.cpp))

Sparse multicast delegates were falling through the generic scalar
handler and rendering as garbage hex. Added a field-level handler that:
- Reads `bIsBound` byte at field+0
- Displays `(sparse, bound — bindings in FSparseDelegateStorage)` or
  `(sparse, unbound)`
- Hex over reported size (defensively capped at 16B)
- Leaves `arrayCount=0` so `IsContainerNavigable` stays false

Binding enumeration (drill-down into individual `FScriptDelegate`s) is
queued for v4 — needs the storage AOB + nested TMap + TSharedPtr walk.

### Property Search panel UX

Three usability fixes pushed in successive commits because they all hit
the same "find every OptionalProperty in this game" workflow:

**Type filter exposed + type-only queries allowed**
([`b461c40`](../dll/src/Fern.cpp))

The DLL backend (`Aura::SearchProperties`) and `DumpService` already
supported a `types` filter, but the UI never surfaced it. Added a Type
filter input. Also relaxed the empty-query check in
`Fern.cpp::CMD_SEARCH_PROPERTIES` — name OR types must be set, not
strictly name. SearchProperties already tolerates an empty substring
(empty `find` returns 0 always).

**Type filter autocomplete + client-side result filter +
ObjectTree suggestions refresh**
([`67eaa62`](../ui/UE5DumpUI/ViewModels/PropertySearchViewModel.cs))

- **AutoCompleteBox** for the Type filter, backed by a curated 32-entry
  `PropertyTypeSuggestions` list. Typing "opt" surfaces
  `OptionalProperty`; "del" surfaces all four delegate variants; "weak"
  surfaces `WeakObjectProperty`. Comma-separated multi-type input still
  parsed in the VM.
- **Client-side result filter**: a new `ResultFilter` TextBox under the
  search bar. `ApplyResultFilter` walks a private `_allResults` cache
  and rebuilds `Results` with case-insensitive substring match across
  Class / Property / Type / Super / Preview. 150 ms debounce on the
  partial-changed hook so per-keystroke rebuild doesn't churn the
  ObservableCollection. VM now `IDisposable`.
- **ObjectTree.SearchSuggestions refresh**: dropped A/U-prefixed
  duplicates (`ACharacter`/`Character`, `APawn`/`Pawn`,
  `UAttributeSet`/`AttributeSet` — UE introspection drops prefixes so
  the A/U variants never matched), added universally-useful entries
  (`GameInstance`, `World`, `Level`, `SaveGame`, `GameplayAbility`,
  `GameplayEffect`, `AnimInstance`, ...). 30 categorized entries:
  GAS / Components / Player & Character / Game Framework /
  World+Level / UMG.

### Class Structure / Game Class fixes

**Find Refs reverse Open auto-scroll**
([`a5634b9`](../ui/UE5DumpUI/ViewModels/LiveWalkerViewModel.cs))

After Open from a Find Refs row the holding field was selected but the
DataGrid stayed at the top. Avalonia's DataGrid only auto-scrolls on
user-driven selection, not on programmatic `SelectedItem` assignment —
raise `ScrollToFieldRequested` so the View calls `ScrollIntoView`,
matching the path edit-commit and inline drill navigation already use.

**Class Structure flash-then-blank** (build 524,
[`449f4e4`](../ui/UE5DumpUI/ViewModels/ClassStructViewModel.cs))

ClassStructPanel briefly showed the clicked object's class data and
then went blank. Avalonia's ListBox raises `SelectionChanged` with null
whenever its `ItemsSource` mutates (filter typing, fresh load,
suggestion auto-selection), and the MainWindow handler dutifully
forwarded that null to `ClassStruct.OnObjectSelected`, which then set
`HasClass=false` and cleared `Fields`. Fix: treat null as
"selection cleared, but keep showing what we last loaded" — the user
already picked a class, the IDE-style transient selection wobble
shouldn't undo that. Plus dedupe consecutive selections of the same
node via a private `_lastLoadedNodeAddress`.

**Class Structure: route class-like nodes to themselves**
(build 525, [`89f637b`](../ui/UE5DumpUI/ViewModels/ClassStructViewModel.cs))

Even after fixing the null-fire blank, clicking `LocalPlayer` (or any
other UClass) in the ObjectTree still showed `//Script/CoreUObject/Class`
with 0 fields — `GetObjectAsync` on a UClass returns its metaclass
(UClass-of-Class), and walking that metaclass yields an empty FProperty
chain because UClass's data lives in native C++ members rather than
UPROPERTY-tagged ones. Fix: detect class-like nodes by ClassName —
anything ending in `Class` (Class, BlueprintGeneratedClass,
WidgetBlueprintGeneratedClass, AnimBlueprintGeneratedClass, ...) plus
`ScriptStruct` / `UserDefinedStruct` / `Enum` / `UserDefinedEnum` /
`Function` / `DelegateFunction` — and walk `node.Address` directly.
Only proper instances go through `GetObjectAsync`.

**Game Class: auto-run Find Instances pre-fill** (`449f4e4`)

`PropertySearch` and `GameClassFilter` both raised
`NavigateToInstanceFinder`, which switched to the Instance Finder tab
and pre-filled `SearchClassName` — but stopped short of running the
query. Trigger `SearchCommand` immediately after pre-fill (with
`CanExecute` guard) so clicking "Find Instances" produces results
without an extra Search click.

**Game Class: add Package column** (`89f637b`)

The Super filter aligned with a Super column, but the Package filter
had no matching column — it was prefix-matching against ClassPath,
which displayed only the full `/Script/Engine.Actor` form. Added a
"Package" column showing the extracted prefix (`/Script/Engine`,
`/Game`, `/Script/ES2`, ...) next to Super and Path. Moved
`ExtractPackagePrefix` from the VM onto `GameClassEntry` so the column
binding and the filter logic share one implementation.

-----

> **Current capability matrix, per-game config, tested games, and
> long-running concerns** moved to [roadmap.md](roadmap.md) (post-589).
> **Next-session pickup candidates** moved to [todo.md](todo.md). This
> file is now a pure append-only milestone history.
