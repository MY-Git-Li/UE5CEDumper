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

### 3. UFunction metadata exposure ([4]) — **next**

**Effort**: M | **Risk**: med

Blueprint-derived UFunctions carry a metadata map (`UFunction::*Meta*`
calls in UE source) with `DisplayName` / `ToolTip` / `Category` /
`Keywords`. Currently we only expose the cooked function name. Surfacing
metadata gives:

- Better display strings ("Add Player Currency" beats `AddMoney`)
- A **second corpus** for the keyword scorer in step 2 (matches against
  Category `Player|Stats|Combat` etc. are higher-signal than matches
  against function names alone, which can collide with engine-internal
  helpers)
- Tooltip text in the UI invoke dialog

**Risk**: metadata layout differs across UE versions and isn't always
present (especially for cooked / shipping builds where editor-only
metadata may be stripped). Need a feature-detect path that gracefully
degrades to "no metadata" rather than reading garbage.

**Touch points**:
- `dll/src/Ubel.cpp` UFunction reader — probe `MetaDataMap` (TMap<FName,
  FString>) at the version-specific offset
- `Aura::WalkClass` augment per-function row with `metadata: { displayName,
  toolTip, category, keywords }`
- Pipe schema bump
- UI: new columns in the function lists, tooltip wired

**Pre-req for step 4**: read the relevant UnrealEngine source to find
the `UMetaData` / `UFunction::FindMetaData` chain. Build per-version
offset table (UE 4.27, 5.0, 5.4, 5.7 minimum).

### 4. Update interesting-functions + existing function lists with metadata ([3c rev2])

**Effort**: S | **Risk**: low (assuming step 3 lands cleanly)

Once step 3 ships:

- Keyword scorer in step 2 also matches against `DisplayName`,
  `Category`, `Keywords` metadata fields (each weighted higher than
  function-name match because they are author-curated, not cooked)
- Existing function lists (Class Structure, PropertySearch results)
  show `DisplayName` as primary label with cooked name as small
  secondary text

### 5. UI invoke dialog overflow fix ([3b])

**Effort**: S | **Risk**: low

`InvokeDialog.axaml` parameter list overflows the screen when a
function has >~10 params. Wrap the param list in a `ScrollViewer` with
`MaxHeight` (~60% of window height). Add "Reset to defaults" button.

Cleanup item — keep at the end of the chain.

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
- **Satisfactory** (UE 5.3, modular DLL build): GWorld scan fails on
  the main exe. Pattern likely needs to live in
  `CoreUObject-Win64-Shipping.dll`. Adapt `Genau::FindAll` to scan
  multiple modules when the primary scan fails.

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
