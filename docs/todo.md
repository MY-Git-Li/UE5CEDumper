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

### 6. One-click helper inject into open CE table — ✅ shipped (build 610)

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

Tests: 651 C# xunit (+7 from this change) + 62 dll_helpers + 31 utf8_helpers.
Coverage: AobMakerInjectTableFileTests (3 — wire-model serialization,
relaxed encoder for single quotes, bridge service arg validation),
MainWindowInjectHelperTests (4 — all four end-states via recording bridge).

-----

## In-flight (spawned to separate sessions / under analysis)

### AOBMaker plugin: inject helper lua into CE table — 🚧 spawned 2026-05-10

**Effort**: M | **Risk**: low | **Status**: spawned to dedicated session

End-to-end goal: replace the two-step `Tools -> Export to disk +
Cheat Engine -> Add File...` workflow with a one-click "Inject Helper
into Current CE Table" menu item that pushes
`ue5_invoke_helper.lua` directly into the user's currently-open .CT
via AOBMaker plugin.

Sub-task brief (sent to spawned session):
- AOBMaker repo (`D:\Github\AOBMaker`): new pipe cmd `InjectTableFile`
  with `{Type, FileName, Content}` payload; plugin Lua handler uses
  `findTableFile` + delete-if-exists + `createTableFile` + write
- UE5DumpUI repo: `IAobMakerBridge.InjectTableFileAsync`,
  `MainWindowViewModel.InjectCeHelperLuaCommand`, new Tools menu item
  next to existing `Export CE Helper Lua File...`
- Both repos: docs updated, tests added

Once spawned session reports back, integrate the UI menu item if it
wasn't included; verify the AOBMaker pipe protocol matches.

### Property Origin Resolver (Proposal A) — ✅ shipped (build 610)

**Effort**: M (actual: ~M) | **Risk**: low (no regressions; tests + build clean)

PropertySearch dedupe-by-defining-class shipped. Searching for
`bCanBeDamaged` now returns one row keyed by `AActor` with a
"+4822 inheritors" badge, instead of 4823 rows you couldn't tell apart.

Implementation:
- DLL `Aura::FindDefiningClass(classAddr, fieldOffset)` walks the
  SuperStruct chain upward and returns the highest-up class still
  declaring the property (algorithm: super has it iff
  `fieldOffset < super.PropertiesSize`).
- DLL `Aura::SearchProperties` rewritten to dedupe by
  `(definingClass, propName, offset)` triple. Per-call
  `unordered_map` accumulates the inheritance count and tracks the
  most-derived subclass observed (largest `PropertiesSize`) as the
  preview-source -- defining class is often abstract (AActor, APawn)
  with no direct instances, so Phase 2 needs to sample a concrete
  subclass. classAddr/className/classPath in the wire output are
  swapped to the defining class for the canonical "this is the
  field's true home" view.
- Wire schema: 4 new fields per match
  (`defining_class_name`/`defining_class_addr`/`defining_class_path`/
  `inherited_by_count`).
- C# `PropertySearchMatch` model gains the new fields plus computed
  `InheritanceBadge` ("+N inheritors" / "" for unique) and
  `InheritanceTooltip` (explains the relationship + shows defining
  class path so user can see engine vs game home).
- PropertySearchPanel grew a "Scope" column between Class and Super
  showing the badge + tooltip.
- Tests (+11): `PropertySearchMatchTests` covers the badge + tooltip
  pluralisation + game-specific-uniqueness wording + existing
  TypeDisplay/OffsetHex formatting baseline.

DLL-side dedup correctness needs a live game (4823-class scenario)
for end-to-end verification -- to be confirmed via smoke test on
Everspace 2 / Titan Quest II / FF7 Rebirth. Behaviour-preserving for
unique BP-added fields (count=0, blank scope column).

Files touched:
- `dll/src/Aura.h` + `Aura.cpp` (`FindDefiningClass`,
  `PropertyMatch` new fields, `SearchProperties` dedup)
- `dll/src/Fern.cpp` (search_properties response wire)
- `ui/UE5DumpUI/Models/PropertySearchResult.cs` (new fields +
  computed display props)
- `ui/UE5DumpUI/Services/DumpService.cs` (deserialize new fields)
- `ui/UE5DumpUI/Views/PropertySearchPanel.axaml` (Scope column)
- `ui/UE5DumpUI.Tests/PropertySearchMatchTests.cs` (new, 11 tests)

**Follow-ups still on the table** (not blocking):
- Proposal B: per-row "similar BP-added properties" suggestions
  (fuzzy-match on tokeniser output to surface game-specific bools
  alongside the engine field)
- Proposal C: Class Family Browser (Character / Inventory / Save /
  Component / DataAsset buckets) — bigger work, separate planning

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
