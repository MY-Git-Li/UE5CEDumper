# Roadmap — Current State

Snapshot of capabilities and per-game configuration. Updated when
behaviour or test coverage changes; pair with [todo.md](todo.md) for
upcoming work and [dev-log.md](dev-log.md) for the historical commit
trail. Build number tags reflect when each row reached its current
state.

> **Last refreshed**: 2026-05-29 (build 797) for the rows below. **dev = main @
> build ~937 (PR #238).** Newer work lives in [dev-log.md](dev-log.md):
> - builds **1531–1544 (2026-06-22)** — **Locate in GWorld** gained a per-row 🌍 on
>   **Interesting Properties** (the last clean gap) and a prominent **⚠ failure
>   banner** in Live Walker — a failed `not_reachable` locate no longer looks like
>   the idle empty state, and exceptions surface too (PR #344). **Locate in
>   GameEngine** (⚙) shipped as a GEngine-rooted companion on all 10 🌍 surfaces —
>   `root_kind=engine` on the existing path handler — reaching engine-layer objects
>   (GameInstance / LocalPlayer / UMG widgets) that no GWorld chain reaches, a
>   deliberate complement (weaker for world actors by design; PR #345).
> - builds **1181–1199 (2026-06-16)** — **Locate in GWorld** (forward-BFS shortest
>   pointer chain GWorld→target, the inverse of Find Refs) on every "open in Live
>   Walker" source, plus **deeply-nested container reach**: `find_by_address` now
>   recursively descends struct-array / map-value / set elements
>   (`Aura::FindInContainersDeep`, fallback-only so the fast path is untouched) to
>   find values in separately-allocated nested containers, and the Instance Finder
>   container 🌍 drills the full multi-level chain to land ON the value.
>   **LIVE-VERIFIED on SEED** incl. the deep `SaveSlotList[1].MsTuneData.MsTunes[0].
>   WeaponTuneList[0].Tunes[2]` repro (28116 objs / 50ms). Build 1199 also fixed the
>   Live Walker per-row `Addr` copy in container-element views (use the resolved
>   `FieldAddress`) and moved "Locate in GWorld depth" to the top Options flyout.
> - builds **1202-1207 (2026-06-16)** — **deeply-nested container values reach end-to-end
>   across ALL four consumers.** A value at ANY (bounded, depth 4) container depth — e.g.
>   `SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]` — is now found by
>   **Instance Finder address search**, **Value Search** (by value), **Snapshot capture**,
>   and so **SPC Query + Snapshot Diff** (Class Pivot already had array support). A shared
>   recursive `Aura::WalkContainerLeaves` drives Value Search + Snapshot capture; Snapshot
>   bakes the full path into `array_field` (no schema change). 1-level cases use the fast
>   static paths; gated per class so the common case pays nothing. 1520 C# / 510 dll green.
> - builds **1027–1112 (2026-06-12…14)** — **Teleport** tab (Wirbel module):
>   BugIt-style marker save/recall (3 slots) + cursor teleport for 2.5D/45° games
>   + BugItGo interop + Debug-Camera force on/off + **read-only camera POV**
>   (`Wirbel::GetPov`; getters with a fully-reflected `CameraCachePrivate.POV` raw
>   fallback). CE integration = mailbox AA records (the old `createHotkey` Lua
>   bundle was removed build 1111). mailbox `CMD_TELEPORT=8`. Full contract in
>   [teleport-spec.md](teleport-spec.md). **LIVE-VERIFIED**: teleport works where
>   the possessed pawn IS the visible character (SEED, DQ III HD-2D); TQ2 = known
>   separate-actor limit; Octopath moves but camera doesn't follow. **POV reads on
>   all four tested titles** (getters on SEED/DQ III; raw fallback on TQ2/Octopath).
> - builds **926-937 (2026-06-06)** — Value Search **lean Candidate** (V3-A/B) +
>   **TSet/TMap scan** (V1a); **app-wide DataGrid sorting fix** (compiled bindings
>   need explicit `SortMemberPath`); **Value Search keyword filter**; **DLL-side
>   cooperative cancellation** (`Cancel.h` + Fern disconnect-monitor + shutdown-abort
>   — long ops stop when the UI closes / DLL shuts down). See the Value Search section
>   below + [todo.md](todo.md) for the V1/V2/V3 plan.
> - builds **805-923** — experimental Snapshot / SPC Query / Class Pivot tabs (N1 noise
>   picker, cancellation, persisted pivot index) in
>   [experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md) +
>   dev-log builds 908-923; Windows-only Native-AOT Avalonia backend in dev-log 918-919.
>
> The build-797 shipments:
>
> - **Multi-numeric Value Search meta types** (build 794-797) — `NumericNoByte` (PR #220) scans every word/dword/qword/float/double field in one pass, each compared by its OWN declared width; `NumericAll` (PR #221) adds Int8/UInt8 plus a result-volume warning. Unlike CE's raw "All", our structured property walk knows each field's declared type → no byte-reinterpret false hits. Both in-game verified OK. See the Value Search section below + [dev-log.md](dev-log.md) build 794-795 / 796-797.
> - **Parallel GObjects-walk scans** (build 792) — `ScanForValue` / `FindInContainers` / `FindReferencesToUObject` now fan their object-array walk across `clamp(cores-2, 1, 16)` worker threads (`ParallelIndexRanges`), with per-thread caches + ascending-tid merge that reproduces the serial result set byte-for-byte. `Ubel`'s class/name/enum/struct-field caches + `CorrectSubclassOffsets` calibration are now mutex-guarded for concurrent access. ~cores× First-Scan speedup on 1M+ object games; **in-game verified OK 2026-05-29** (correct results, no hang/crash, speedup confirmed). See [dev-log.md](dev-log.md) 2026-05-29 + [todo.md](todo.md). The prior 2026-05-27 shipments below are unchanged:
>
> - **Value Search Phase 2** (build 757) — FString / FName / FText with `Contains` / `StartsWith` / `EndsWith` + case-sensitive toggle; FVector / FRotator component-wise compare; TArray\<T\> primitive/string/vector element scan with `Num > 10M` soft circuit-breaker. **FTransform footnote**: wire-stable but zero hits pending per-version Translation offset detection. See [dev-log.md](dev-log.md) 2026-05-27 entry.
> - **Multi-row → One .CT batch generator** (build 760, #3) — Interesting Funcs + Interesting Properties tabs gain Extended-mode multi-select + 📦 Generate CT toolbar button. Output: one CT with root group → per-category sub-groups (alphabetical) → one CheatEntry per row.
> - **`scripts/analysis/diff_dumps.py`** (#4) — pure-Python same-game patch diff at UProperty granularity. Reports moved fields / type changes / function signature changes / added-removed. `--minimal` mode for "things that broke my cheat table". `--self-test` for synthetic-fixture coverage.
> - **Structured-return DataGrid** (build 775, #5) — InvokeParamDialog shows a 4-column property grid (Field / Type / Value / Offset) below the existing single-line decode for FVector / FRotator / FHitResult / user USTRUCT returns. Resolution: KnownStructLayouts → dynamic StructFields → empty.
> - **UCheatManager stripped-body hint** (build 778, #6) — Console panel surfaces an orange footer warning when the selected exec's class or super name contains "CheatManager"; redirects users from `Result=0 + no effect` (cooker `#if !UE_BUILD_SHIPPING` strip) to a game-specific verification target. Memory `feedback_ucheatmanager_stripped.md` + lessons-learned bullet.
> - **NuGet bump + dotnet test → MTP mode** (build 771) — .NET 10 SDK + `Microsoft.Testing.Platform` 2.x compat. New `global.json` `{"test": {"runner": "Microsoft.Testing.Platform"}}`; build.ps1 uses `--project` instead of positional; dropped explicit `Microsoft.Testing.Platform.MSBuild` pins (xunit.v3 owns the transitives).
> - **AOT warning sweep on #5 DataGrid** (build 780) — switched four `DataGridTextColumn` + string-path `Binding` to `DataGridTemplateColumn` + `FuncDataTemplate<StructFieldValue>(lambda)`. Zero IL2026 / IL3050 warnings on `dotnet publish`.
>
> Test totals at this snapshot: **247 DLL helpers + 31 utf8 + 1080 C# = 1358**.

-----

## Capability matrix

| Layer | Drill-down | Find Refs |
|-------|-----------|-----------|
| Object / Class / Interface | ✅ | ✅ |
| Weak / Soft{Class} / Lazy (single + array) | ✅ | ✅ |
| TArray of any pointer-shaped inner | ✅ | ✅ |
| TMap / TSet (Object/Class) | ✅ | ✅ (allocated slots only) |
| Delegate (single FScriptDelegate) | ✅ | ✅ (v3) |
| MulticastInline / MulticastDelegate | ✅ | ✅ (v3) |
| TArray<FScriptDelegate> | ✅ | ✅ (v3) |
| MulticastSparseDelegate (UE 5.0+) | ✅ bindings via SPARSE_ES2_1 AOB (build 561-577) | ✅ v4 sparse pass (build 565) |
| MulticastSparseDelegate (UE 4.23-4.27) | ❌ FObjectKey outer key, separate AOB needed | ❌ |
| OptionalProperty\<pointer / weak\> | ✅ | ✅ |
| OptionalProperty\<scalar Int/Float/Bool/Byte/Enum\> | ✅ trailing-bIsSet | — |
| OptionalProperty\<String / Name / Text\> | ✅ intrusive sentinel + value (build 530) | — |
| OptionalProperty\<Struct\> | ✅ (build 528) | ✅ depth-3 descent through inner struct (build 528) |
| FieldPathProperty | ❌ | ❌ |
| TMap / TSet with weak-like inner sides | — | ❌ (v4 candidate) |

## Per-game configuration

Persisted in HintCache JSON per PE hash, surfaces in the Pointer panel:

| Setting | Range | Default | Pipe cmd | Since |
|---|---|---|---|---|
| UE version override | Auto / 4.18-4.27 / 5.0-5.8 | Auto (detect) | `set_ue_version_override` | build 549 |
| Invoke timeout | 1000-60000 ms | 5000 ms | `set_invoke_timeout` | build 583 |

## UFunction invoke export (build 590-596)

Three buttons per UFunction row in LiveWalker:

| Button | Mode | Output |
|---|---|---|
| **Generate Script** (`INV`) | In-CE form (existing) | AA Script with `createForm` interactive popup |
| **Pipe Invoke** (`PIPE`) | In-app via DLL pipe | Live invoke + decoded result inline |
| **AA(Baked)** (new) | Non-interactive AA Script | Self-contained AA Script with values baked at generation time; depends on `ue5_invoke_helper.lua` embedded in the user's .CT |

Two ways to get `ue5_invoke_helper.lua` into the user's .CT:

- **Tools -> Inject Helper into Current CE Table** (build 610, preferred) —
  one click; sends the embedded helper straight into the open CE table
  via the AOBMaker plugin's new `InjectTableFile` pipe command (`findTableFile`
  delete-if-exists -> `createTableFile` -> `Stream.write` -> verify).
  Requires the AOBMaker CE Plugin to be loaded; falls back gracefully
  with a status-bar hint if unavailable.
- **Tools -> Export CE Helper Lua File...** — manual fallback when
  AOBMaker isn't installed or CE isn't running. Writes `scripts/ue5_invoke_helper.lua`
  to a user-chosen path; user adds it via CE `Table -> Add File...`.

### Invoke param picker (build 711-715, Stage 1+2)

InvokeParamDialog rows for pointer-flavoured params (UObject* / UClass* /
SoftObject / WeakObject / LazyObject / Interface) now surface the expected
target UClass and provide one-click filling:

- **Label** — `[UObject*: AActor, 8B, off=0x10]` instead of bare
  `[UObject*, 8B]`. The DLL extracts `FObjectPropertyBase::PropertyClass` in
  `WalkFunctions` and ships it via the `obj_class` pipe field; C#
  `FunctionParamModel.ObjectClassName` carries it to the UI. Empty when the
  param genuinely has no class constraint or when an older DLL is in use
  (backward-compatible).
- **[Pick…]** — opens `ObjectInstancePickerDialog` pre-filtered to the
  expected UClass via the existing `find_instances` pipe command. Substring
  match catches subclasses (which is what the param actually accepts). Double-
  click row or "Use selected" fills the textbox with the chosen instance's
  address.
- **[null]** — fills `0x0` (WorldContextObject and other optional pointer
  params).
- **[self]** — fills the invoke target's own address (for utility functions
  that re-target themselves).

`ParamBufferBuilder.IsPickablePointerType` is the canonical 7-type list
locked by test theories — adding a new pointer property type requires
mirroring it in both the DLL `WalkFunctions` extraction and the C# helper
or the build catches the drift.

Stage 3 (class validation — DLL `validate_object_class` round-trip before
invoke) deferred until a real class-mismatch crash motivates it; picker
output is almost always class-correct in practice.

## Value Search — by-value scan (build 738 + Phase 2 build 757)

New tab "Value Search" between Interesting Properties and Console. Fills
the search-by-value gap (PropertySearch was by-name; InstanceFinder was
by-address; this is the third axis). Port of discrete's Phase 27b
`ValueScanSession` shape with UE-specific scan engine. Phase 2 expansion
(build 757) extends from numeric primitives to the three deferred type
families.

| Component | What it does |
|---|---|
| `ValueScan::SessionManager` (DLL) | 5-min idle-expiry session container; holds candidate vector + DataType between RPCs. |
| `Aura::ScanForValue` | Walks GObjects (skips UClass meta), per-class field index cached lazily via `Ubel::WalkClassEx` filtered to matching DataType, typed-read + `ComparePredicate` for primitives, `CompareStringPredicate` for strings, `CompareVectorPredicate` for vectors. TArray inner expansion when ArrayProperty's Inner matches. 15s deadline. |
| `Aura::RefineCandidates` | Re-reads each candidate's bytes/string, applies predicate (prev-value scan types compare against stored `Candidate.prevValue`/`prevStr`), prunes failing, updates snapshots on survivors. Re-reads strings via `c.addr` so array-element strings work uniformly with direct string fields. |
| `Fern.cpp` handlers | `begin_value_scan` / `refine_value_scan` / `end_value_scan` / **`query_candidates`** (V3-C). Wire schema (Phase 2): optional `case_sensitive` for string DataTypes, `tolerance` now also applies to FVector/FRotator (per-axis). `IsScanTypeValidFor` gating rejects nonsensical combos (`FString + Bigger`, `Int32 + Contains`) with explicit errors. **V3-C (build 949):** begin/refine return `total` + only the first `page_size` page (scan order); `query_candidates(session, offset, limit, filter, sort_key, sort_desc)` filters + sorts the WHOLE session set server-side (over the DLL's own pools — no game-memory reads) and returns one window. The DLL session is the single dataset owner; the UI is a windowed view. |
| `ValueSearchPanel.axaml` | Hard-locked banner: "Native C++ fields (non-UPROPERTY) cannot be found here — use Cheat Engine's raw memory scan for those." DataType + ScanType selectors (ScanType dropdown filters per DataType via `VisibleScanTypeOptions`), Value/Value2 inputs, Case-sensitive checkbox (string types only), Tolerance NumericUpDown (float/vector types), Candidates DataGrid. |

**Supported DataTypes** (build 757):
- **Numeric** (MVP, build 738): Int8/16/32/64, UInt8/16/32/64, Float, Double, Bool (BoolProperty bitfields normalised to 0/1 via FieldMask).
- **String** (Phase 2A, build 757): FString, FName, FText (best-effort — cooked games strip most display strings; ES2 resolved 1/1551 classes).
- **Vector** (Phase 2B, build 757): FVector, FRotator. **FTransform** wire-stable but currently returns zero hits pending per-version Translation offset detection (tracked as todo `#0c FTransform Translation offset`).
- **TArray\<T\>** (Phase 2C, build 757): walks reflected ArrayProperty buffers for primitive / string / vector inner types. Soft circuit-breaker on `Num > 10M` (skip with `LOG_WARN`), Num/Max/Data validation guards. Matching elements appear as `FieldName[N]` rows.
- **TSet\<T\> / TMap\<K,V\>** (V1a, build 927): walks the FSet/FMapProperty `TSparseArray` (allocated slots only via `IsSparseIndexAllocated`) for any supported leaf DataType. Map key and value are scanned independently (a `TMap<int,int>` with Int32 emits both). Rows render as `Set[idx]` / `Map.Key[idx]` / `Map.Value[idx]`. Reuses the same sparse-walk geometry (`Ubel::GetSetElementStride` / `GetMapPairLayout`) as the container-aware Address Finder. Element addresses are raw, so refine degrades on a container reallocation exactly like TArray (SEH-safe read drops the candidate). _In-game verification pending._
- **TOptional\<T\>** (V1c, build 942): scans an `FOptionalProperty` whose wrapped value matches the requested DataType (numeric / string / vector). The value is read inline at the optional's own offset (field+0, same as a direct leaf — `FOptionalProperty` shares `TArray`'s Inner-at-`FARRAYPROP_INNER` shape). Non-intrusive optionals (`{ T value; bool bIsSet; }`) are **gated on the trailing `bIsSet` byte** (offset = `sizeof(T)`, computed by `ValueScan::OptionalFlagOffset` from the inner element size) so a scan for `0` / stale bytes doesn't false-hit unset slots. Rows render under the optional's own field name; refine re-reads `c.addr` (a stable address, unlike sparse-container slots). Drilling into a `TOptional<FStruct>` for nested leaves remains future. _In-game verification pending._

**Scan bounds** (how many array elements get scanned, and the global caps):
- **Per-array: ALL elements** are scanned (`for idx = 0 .. Num-1`) — Value Search deliberately does **not** inherit the export-side array-size cap, because a hit at a high index is still a legitimate hit (see memory `project_value_search_caveats`). The only per-array limit is the **soft circuit-breaker**: an array whose `Num` is negative or `> 10,000,000` is skipped whole with `LOG_WARN` (corruption / freed-memory / OptionalProperty-misread guard, not a "scan first N" cap). `Max < Num`, null `Data`, and `Num == 0` are also skipped.
- **Global `maxResults` cap** (default 50,000; UI-configurable 100..**1,000,000** since V2/build 954): bounds the total candidate count **across all objects and array elements combined**, not per array. In the parallel walk it's a per-thread local cap, then the ascending-tid merge truncates to `maxResults` (so the kept set is the lowest-index matches the serial walk would have stopped at). Hitting it sets no special flag — the user simply sees fewer than the true total; raise Max or narrow the predicate. **Since V3-C (build 949)** the UI no longer materializes the whole set: begin/refine return `total` + the first page, and the grid pages via `query_candidates` (server-side filter/sort over the full set). The DLL holding a large set is cheap (lean Candidate, V3-A). **V2 (build 954)** then raised the ceiling to 1M and verified the server-side ordered view stays sub-second at that size (~640 ms sort / ~715 ms filter over 1M in the scale bench).
- **15s deadline**: the whole scan bails after 15 seconds and sets `deadline_hit` → the status row shows a "scan truncated" note.

The combined effect: ordinary arrays (equipment/buff lists, etc.) are fully enumerated; the practical ceiling a user hits first is usually the 50k global cap, especially with NumericAll on a small value.
- **NumericNoByte** (multi-numeric meta, build 794-795): one pass over **every** word/dword/qword/float/double field, each compared by its OWN declared width — the "I know the value but not whether it's int or float" starting point. Distinct from CE's raw "All" (no byte-reinterpret false hits — our walk knows each field's declared type). Excludes Int8/UInt8/Bool to prevent small-value result explosion. `BuildNumericTargets` pre-parses the value into one buffer per fitting width (`70000` → no 16-bit; `100.5` → float/double only; hex → integer only); each field resolves its own DataType via `TryDataTypeFromPropertyTypeName` and compares against the matching buffer. Results-grid Type column shows which width matched. Tolerance applies per-member to float/double fields only.
- **NumericAll** (multi-numeric meta with byte, build 796-797): same one-pass scan as NumericNoByte but **including** the 1-byte families (`Int8Property` → Int8, `ByteProperty` → UInt8; Bool still excluded). 10 members. Use when the value really is a byte. Because small values (0/1/255) match a huge number of 1-byte fields, the panel shows an orange **result-volume warning** (`ValueSearchViewModel.DataTypeWarning`) whenever NumericAll is selected. `BuildNumericTargets` range-gates byte widths (`300` → no Int8/UInt8; `-5` → Int8 yes / UInt8 no). _In-game verification pending._

**ScanType matrix**:
| DataType family | Valid scan types |
|---|---|
| Numeric | Exact / Bigger / Smaller / Between + Changed / Unchanged / Increased / Decreased |
| Vector | Same 8 (component-wise per axis; "Increased" triggers when ANY axis moves up) |
| String | Exact / Contains / StartsWith / EndsWith + Changed / Unchanged |

Native C++ fields (non-UPROPERTY) are explicitly excluded — banner directs the user to CE for those. Cross-tab navigation: per-row "Open in Live Walker" opens the owning instance with the field address pre-populated.

**Live verification (ES2, UE 5.5, build 757)**: FString Contains "Engine" → 54 candidates / 323ms; FName Contains "Engine" → 7119 / 415ms; FText Contains "Engine" → 1 / 396ms; FVector Exact tol=0.01 → 49966 / 303ms; FRotator Exact tol=0.01 → 16819 / 290ms. No `LOG_WARN: skipping TArray` fired across ~1.15M scanned objects.

### Multiple Values Group Scan (object-aware "Group Scan"; builds 1276-1319)

A **Single / Group** toggle in the Value Search tab. Group mode finds **objects** that simultaneously hold ALL of N values (2–4) at **distinct** numeric-property offsets, in any order (Str + Def + Dex + Int in one stats object) — multiplicatively more selective than N separate scans. Full design + extension points (the source-agnostic `Orden` SDR matcher = the reuse seam for Snapshot/SPC/Pivot): [group-value-scan-spec.md](group-value-scan-spec.md).

- **P1 + Deep** (builds 1276-1285): per-object numeric leaves (direct + struct descent); an opt-in **Deep** checkbox additionally matches a group *within* one nested numeric container / struct-array element (shared with the single-value deep pass). In-game verified on SEED (UE4.27). **Scalar-valued/keyed maps** (`TMap<Name,int>`) captured too (builds 1561-1562; value block `<map>.Value`, key block `<map>.Key`) — the shared `WalkContainerLeaves` now emits map scalar sides for deep Value Search / Snapshot / Group Scan.
- **P2** (builds 1295-1302, MERGED PR #311): **per-slot scan type** — First Scan `Exact / Bigger / Smaller / Between` (Between = the bounded-unknown entry, e.g. an HP bar in [1,100]); Next Scan also the prev-value four `Changed / Unchanged / Increased / Decreased` (compare each located leaf vs its own previous round). A **locked-offset table** (`🔒 Class — Str@0x20, Def@0x24`) appears once every slot converges. (A "Copy CE / export" was deliberately dropped — export the chain from Live Walker.) prev-value refine in-game verified on SEED.
- **P4 increment 1** (builds 1303-1313, MERGED PR #313): an opt-in **Cross-object (owned components)** checkbox folds the numeric leaves of the sub-objects an actor OWNS (its components + a GAS ASC's `SpawnedAttributes` → `UAttributeSet`, a 2-level owned BFS gated by an Outer-chains-back test) into the actor's block, so a group whose values span {actor, components, attribute sets} matches. Ownership + value driven, not class-name driven. In-game VERIFIED on TQ2 (UE5.07, GAS).
- **P4 increment 2** (builds 1318-1319): each slot carries an `owner_class` so a cross-object slot's **Pivot** handoff lands on the owned sub-object's class (the stats component / `UAttributeSet`) instead of the candidate actor's. The object handoffs (Live Walker / Locate) already targeted the sub-object by address (inc 1); this completes the class side. Closes P4.

## Multi-row → One .CT batch generator (build 760, pick #3)

Interesting Functions + Interesting Properties tabs gain a **📦 Generate CT** toolbar button that wraps the current DataGrid multi-select (`SelectionMode="Extended"`) into a single ready-to-share `.CT` file. Promotes the discover→use workflow from "research toy" to "shareable cheat-table author".

| Component | What it does |
|---|---|
| `Models/CheatTableRow.cs` | Discriminated row type (`CtPropertyRow` wraps `FreezeScriptParams`; `CtFunctionRow` wraps Baked params). Source-panel-agnostic so future call sites (LiveWalker, Live PE Profiler) can feed the same builder. |
| `Services/CheatTableBuilder.cs` | `Build(title, rows)` → CE XML matching CE's File→Save As shape. Root group → per-category sub-groups (alphabetical; "Uncategorised" trails) → one CheatEntry per row with `<VariableType>Auto Assembler Script</VariableType>` body. IDs sequential from `BaseId=1000`. XML-escapes all five canonical entities so `TArray<int>` / `&` / quotes in descriptions can't break a CT load. |
| `MainWindowViewModel.SaveCheatTableAsync` | Platform save-file dialog with `.CT` filter; writes UTF-8 (no BOM, matching the bundled UE5CEDumper.CT). Logs source label for grep. |

Per-row defaults users edit in CE before activating:
- **Properties**: per-UE-type "obvious cheat" freeze literal (Float = `9999.0`, Int = `99999`, Bool = `true`, Byte = `255`). Struct / array / non-scalar rows are filtered out (`FreezeScriptGenerator.IsTypeSupported`); status text reports the skip count.
- **Functions**: BakedValues intentionally empty (helper zero-fills PARAMS); description for parameterised funcs reads `"Class::Func (N (XB)) — edit baked PARAMS in CE"` so users know to populate before activating.

Default filename: `{Source}-batch-yyyyMMdd-HHmmss.CT` (Source = `InterestingProperties` or `InterestingFunctions`).

Not yet wired into LiveWalker (heterogeneous row types — functions + fields + struct sub-fields + array elements — need their own UX pass; tracked in todo.md as `LiveWalker batch generator v2`). AOBMaker direct-inject of the generated CT is also v2.

## UFunction Return Value structured walker (build 775, pick #5)

InvokeParamDialog FIRE result now shows a 4-column DataGrid (Field / Type / Value / Offset) below the existing single-line decode when the return is a StructProperty. Renders FVector / FRotator / FHitResult / user USTRUCT returns as a property grid instead of `"X=1.0, Y=2.0, Z=3.0"` comma joins.

| Component | What it does |
|---|---|
| `Models/StructFieldValue.cs` | Pure record (Name, Type, Value, **absolute** buffer offset = return param offset + sub-field offset). |
| `Services/StructReturnDecoder.cs` | Resolution order: `KnownStructLayouts` (per-version locked) → DLL-discovered dynamic `StructFields` → empty list. Delegates each byte→typed-value cell to `InvokeParamDialog.DecodeParamValue` so the grid and result-label never disagree on a byte mapping. SafeDecode wraps with try/catch — a single bad field doesn't blow the whole grid. |
| `InvokeParamDialog` | Pre-resolves `_returnParam` at construction; clears + hides grid at top of `OnFireClicked` so stale rows don't flash across invocations; header label includes struct name (`"Return value (decoded — Vector):"`). Columns use `DataGridTemplateColumn` + `FuncDataTemplate<StructFieldValue>(lambda)` — AOT-clean (build 780 fix), zero IL2026/IL3050 warnings on publish. |

**v2 follow-ups** (deferred, tracked in todo.md):
- **ObjectProperty return resolution** — needs DLL pipe round-trip via `Ubel::GetName` on the returned address; pointer returns currently render as 8-byte hex in the existing single-line decode only.
- **Recursive struct expansion** — `FHitResult.Location` (FVector) renders as one `Location (StructProperty)` row; nested expansion needs recursive DLL-side discovery in WalkFunctions.

**Verification target (Geri, UE 4.27)**: `PlayerCameraManager::GetCameraLocation` returns FVector — grid should show 3 rows (X / Y / Z floats) at offsets 0x4 / 0x8 / 0xC of the post-call param buffer.

## UCheatManager stripped-body hint (build 778, pick #6)

Console panel surfaces an orange-bordered footer warning when the selected exec's class or super name contains "CheatManager" (case-insensitive substring). Redirects users from the `Result=0 + no in-game effect` failure mode (UE wraps these in `#if !UE_BUILD_SHIPPING` — reflection metadata survives the cook, function bodies don't) to a game-specific verification target.

**Discriminator** (locked in `feedback_ucheatmanager_stripped` memory): `Stark::GetHookFireCount()`:
- `>0 + Result=0 + no effect` = cooker-stripped body (the bug this hint addresses)
- `==0 + Result=0 + no effect` = hook on wrong vtable slot (closed by build-648 pattern scan)

Detection lives in `ConsoleViewModel.IsLikelyUCheatManagerExec(entry)` — public + static, tested across 10 row variants (engine class / game subclass via own name / subclass via super name only / case-insensitive / 4 negatives). Out of scope for v1: full super-chain walk to catch second-degree subclasses (`BP_MyCheatManager_C : MyGameCheatManager : UCheatManager`) — would need a new `walk_super_chain` pipe call; revisit if false-negatives surface.

`docs/lessons-learned.md` gained a corresponding bullet under "UFunction Invoke / ProcessEvent" so the diagnostic flow survives across sessions even when memory rotates.

## Game-patch diff: `scripts/analysis/diff_dumps.py` (pick #4)

Pure-Python sister to `analyze_dumps.py` — consumes the same `Dump All Metadata` JSONL corpus but does N=2 patch-vs-patch diff at UProperty / UFunction granularity. Closes the cheat-table-maintainer pain when a game ships a silent offset shuffle.

Match keys: class `path` (canonical UE id; `addr` is session-local). Path normalisation (`//Script/X` ≡ `/Script/X`). Properties + functions matched by name within class.

Report sections: Added / Removed classes; **Moved fields** (same name, different offset/size); **Type changed** (FloatProperty → DoubleProperty incl. inner_type / struct_type / obj_class / enum); Added/Removed properties; **Function signature changed** (return_type / num_parms / parms_size / flags); Added/Removed functions; per-class `props_size` delta.

CLI flags:
- `--minimal` — breaking changes only (moved + sig-changed), hides added/removed lists.
- `--include-engine` — opens up `/Script/` classes (default skips them since they rarely shift across game patches).
- `--self-test` — 6 built-in synthetic-fixture scenarios; no external dumps needed.

Known limitations (documented in README): no auto-rename detection (renamed field shows as Removed + Added); no function body comparison — only metadata is dumped (logic-only refactors are invisible, covered by todo pick #2 Live PE Profiler in the future).

## Property freeze — horizontal class-wide (build 719)

PropertySearch rows gain a **Freeze** button that ships an AA Script
into CE locking the property at a constant across **every live instance**
of the owning class. Different from the CE-XML export (Route A — kept
in [todo Speculative](todo.md)) which pins a single pointer chain to a
single instance: the freeze script enumerates instances by class+offset
every 5 s, so respawns / new spawns / destroys are handled transparently.

| Component | What it does |
|---|---|
| `Mimic::CMD_LIST_INSTANCES` (mailbox cmd 6) | Paginates live (non-CDO) `UObject*` pointers of a class via `Aura::FindInstancesByClass(exactMatch=true)`. 128 ptrs / page, hard cap 2000 instances. |
| `scripts/ue5_freeze_helper.lua` (embedded) | `freezeProperty(cfg) → handle` API; tick timer (50 ms default) + rescan timer (5 s default); type writers for bool / int8-64 / uint8-64 / float / double; shares `_ue5_invoke_busy` reentrancy flag with invoke helper. |
| `FreezeScriptGenerator` | Renders AA Script with editable `CFG = {...}` block, per-script keyed handle table so multiple Freeze scripts coexist. |
| `FreezeValueDialog` | Single-input modal with type-aware validation (bool accepts true/false/1/0). |
| `Tools -> Inject/Export Freeze Helper Lua` | Sister entries to the invoke-helper Tools menu — one-click AOBMaker inject or manual file export. |

Supported property types (v1): BoolProperty, ByteProperty, Int8/16/32/64Property, UInt16/32/64Property, EnumProperty, FloatProperty, DoubleProperty. **Not supported**: StructProperty / FString / FName / containers (deferred).

Gating: the Freeze button is disabled when AOBMaker plugin isn't reachable; tooltip explains the setup requirement. **No clipboard fallback** — script delivery is AOBMaker-only by design (keeps the surface tight).

## Interesting Functions Finder (build 597-609)

New tab "Interesting Funcs" between Property Search and Game Classes.
Backed by the `list_all_functions` pipe cmd which flattens every
UFunction across every UClass into a single one-shot payload (~4MB
for a 50k-function game). Client-side scoring via
`KeywordScoringTable` + `KeywordTokenizer` (build 609 -- whole-token
match instead of substring, so short acronyms HP/MP/SP/XP/TP only
fire on standalone tokens):

- **Categories**: Stats / Inventory / Movement / Combat / Utility (with
  ExplicitMovementCheats sub-bucket: NoClip/Fly/God/Ghost/Invincible
  weighted +8 to outscore Utility's noisy `Cheat` + `Debug` matches)
- **Class bonuses**: Character/Pawn/PlayerController/PlayerState +3,
  Player +2 (build 673), Enemy / Weapon +2 (build 687 — Phase 2),
  GameMode/GameInstance/SaveGame +2; AnimInstance/AnimMontage/AnimSequence/
  AnimNotify/AnimGraph/AnimBlueprint -2 (build 673 — surgical compound
  names, was bare "Anim" before which broke game classes like AnimMan_*),
  NiagaraSystem/NiagaraEmitter/NiagaraComponent/SoundCue/SoundWave/
  SoundBase/AudioComponent/ParticleSystem/ParticleEmitter -2,
  UserWidget/WidgetComponent -1
- **Flag bonuses**: BlueprintCallable +2, BlueprintEvent +1, Pure-or-
  Const safe getter +1, ParmsSize > 64 -1
- **Threshold = 5**; Show All toggle bypasses

Per-row actions:
- **Live**: open in Live Walker via `find_instance` lookup, falls back
  to ClassStruct tab if class is CDO-only
- **AA(B)**: shortcut into the Copy AA Script (Baked) flow; reuses the
  same dialog as the LiveWalker AA(Baked) button

**AOBMaker availability gating** (build 608, refined build 689) — when
AOBMaker CE Plugin pipe is unreachable, both LiveWalker Functions and
Interesting Funcs panels show a single inline italic status indicator
("AOBMaker plugin not found — AA Script export will fall back to
clipboard"). Was previously a per-row Notes column on every row (pure
noise since the value is VM-level); build 689 collapsed it to one
place. Re-checked on tab activation (5s cooldown so rapid switching
doesn't stack 2s pipe-connect timeouts). Send-time guard distinguishes
"pipe broke during send" (warning) vs "plugin never configured"
(informational).

## Interesting Properties Finder (B' — build 670-687)

Symmetric tab to Interesting Funcs but for properties. Backed by
`search_properties_batch` (build 685) — DLL walks GObjects ONCE and
checks every property against every keyword in one pass, ~30× faster
than the build-670 sequential approach. Uses `PropertyScoringTable.cs`
(separate from KeywordScoringTable since property naming differs from
function naming) + shared `ClassLocationScorer.cs`:

- **Categories**: Stats / Combat / Resources / Movement / Utility
  (no Inventory — uses Resources instead; no ExplicitMovementCheats —
  property names rarely encode cheat-mode verbs)
- **Class bonuses (PropertyRules)**:
  Character/Pawn/PlayerController/PlayerState/AbilitySystem/AttributeSet/
  Inventory/Equipment +3; Player +2; GameMode/GameInstance/SaveGame/
  PlayerProfile +2; **LocalPlayer / GameViewportClient / HUD /
  UCheatManager / CheatManager +4 with ⚠ Unusual Location flag**
  (build 670); Weapon / Projectile / Battle / Enemy +2 (build 678 + 687
  — empirically derived from 15-game cross-game analysis)
- **No visual/audio penalties on Property side** — property names alone
  filter the noise (an "PlaybackSpeed" on UAudioComponent doesn't match
  any keyword, so it scores 0)
- **Threshold = 4** (slightly lower than Function side because per-hit
  weights are lower)

Key concept: **Unusual Location flag** highlights cheat-relevant fields
hosted in non-canonical containers (LocalPlayer / GameViewportClient /
HUD / CheatManager) — the kind of properties developers placed outside
where you'd think to look first.

Per-row actions:
- **Live**: open the property's owning class in Live Walker via
  `find_instance`, fall back to ClassStruct on CDO-only classes.
  Pre-fills the LiveWalker SearchText with the property name so the
  user lands with it highlighted.
- **Name**: copy the bare property name to clipboard.

## Dump-for-analysis pipeline (build 676-687)

`Export → Dump All Metadata (.jsonl)` streams every class + props +
funcs as JSON Lines via the existing pipe endpoints (`get_object_list`
+ `walk_class` + `walk_functions` — no new DLL command). Mirrors the
`IsClassLikeMeta` whitelist so BPGCs are included.

Companion Python script `scripts/analysis/analyze_dumps.py` aggregates
N dumps cross-game and emits a Markdown report with:

- Top OWN property names (with `_resolve_own_props` filter to dedup
  inherited fields counted N times across the inheritance chain)
- Top OWN property TOKENS — candidate keywords, cross-referenced
  against existing category buckets
- Candidate Unusual Location class tokens — class × prop-token
  co-occurrence ranked by cross-game frequency
- Same three sections for the Function side (build 687)
- `--min-games` filter (default 3) drops single-game spikes

15-game corpus (DQ7R / DQI&IIHD2D / ES2 / FSD-DRG / FactoryGameSteam /
Geri / HogwartsLegacy / ManorLords / NMKART / Octopath / Stray / TQ2 /
TowerOfMask / ff7rebirth / ff7remake) drove the build 678 + 687 scoring
table additions. Two subsequent bias rechecks at **17 games** (Star Wars
Jedi: Fallen Order + Ghostwire: Tokyo, 2026-05-12) and **18 games**
(Frontiers — first MMO/ARPG-flavoured entry, 2026-05-20) confirmed
stability with **no further keyword additions** in either pass. Anti-
bias workflow documented in
[scripts/analysis/README.md](../scripts/analysis/README.md) — users
whose preferred genres aren't well-represented dump their own games +
PR with analysis output as evidence.

## Publisher detection

`Genau::DetectPublisherFromPE` reads `LegalCopyright` + `CompanyName`
via `VerQueryValueW` and matches against `kPublishers[]`. A match
forces `bLowConfidence=true` (override the Tier promotion) AND
applies the publisher's `biasFallback` when detection fails. Currently:

| Publisher | Bias fallback | Reason |
|---|---|---|
| `SQUARE_ENIX` | UE 4.27 | UE4 forks shipped without canonical version strings + bundled SDKs leak misleading 5.x numbers |

Adding more entries casually risks wrong bias overriding correct
detection — wait for a real misdetection report before adding.

**Version detection is cached per `peHash` (build 1521).** The slow
`DetectVersionDetailed` memory string scan (5+ s on large games) runs only
**once** per game build; subsequent connects reuse the cached version when it
was stamped by the current `Genau::kVersionDetectLogicRev`, so stripped-version
publisher games (Square Enix) no longer re-detect every connect. The
low-confidence badge stays honest — the cache-reuse path re-applies publisher
low-confidence **live** (`bLowConfidence = cached || publisher!=nullptr`), since
`DetectPublisherFromPE` runs every launch. **Changing a `biasFallback` value
above requires bumping `kVersionDetectLogicRev`** so already-cached games
re-detect under the new bias. Force a fresh detect anytime via the per-game
Delete-cache button; a UE version override still wins over everything.

## Tested games (last verified 2026-06-11)

- **Everspace 2** ✅ (UE 5.4): item template ID via container scan; Find
  Refs v3 returns 9 correct references in 224ms (cache hot, scan
  complete: 1180536/1180536); auto-scroll-to-field after Open works;
  Class Structure for `LocalPlayer` shows correct fields after the
  class-like routing fix; PropertySearch type filter `OptionalProperty`
  finds 9 matches across 5 real classes + 4 test-object fields.
  **`SPARSE_ES2_1` resolves SparseDelegates @ +9AA5F10** (build 575,
  ground truth from PDB).
- **Titan Quest II** ✅ (UE 5.7, bCasePreservingName=**true**, 486k
  objects): cross-version validation — same `SPARSE_ES2_1` AOB hits
  `+D46D170`, exercises FName=16 walker branch (inner stride 0x28).
  Was source of 194 `ValidateArrayElemSize` warnings/session pre-build
  583 → now Debug-only.
- **DQ I&II HD-2D / FF7 Rebirth / FF7 Remake** (UE4 forks, Square Enix
  publisher): Square Enix publisher detected → ⚠ Low Confidence badge +
  Publisher chip; user can set Override = UE 4.27 / 4.18, persists
  across launches. Char Lv / HP / Party Lv in non-reflected memory
  (custom allocator) — out of reflection scope; use CE pointer scan.
  **Build 589 verified**: invoke_timeout=6000 round-trip OK after
  `FillPointerSnapshot` fix; Square Enix purple chip + Low Confidence
  amber badge both surface from `scan_status` payload now.
- **Meltopia** ✅ (UE 5.0.5): full scan OK; was source of ~75
  misalignment + ~58 empty-map false-positives + 4 UFunction timeouts →
  all resolved in build 582-583 (Scharf alignment helper + empty-map
  guard + per-game invoke timeout 6000ms via UI NumericUpDown).
- **Squirrel With A Gun** ✅ (UE 5.0.2): full scan OK; was source of
  `walk_instance` `std::invalid_argument` crash on unsubstituted CE
  placeholder `0x[ply_base]` → resolved by `Renge::TryStrToAddr` in
  build 582.
- **Caravan Sandwitch** ✅ (UE 5.0.4): full scan OK; was source of 49
  empty-TMap false-positives → resolved by count=0+Data=null guard.
- **Retro Rewind Demo** ✅ (UE 5.0.4): full scan OK.
- **The Occupation** ✅ (UE 4.19): UE4 path with `GNAM_CT3`, GWorld OK.
- **TimeSplitters Rewind Early Access V0.3.3** ✅ (UE 4.25): full scan,
  GWorld OK.
- **The Artisan of Glimmith** ✅ (UE 4.27, exe `Geri-Win64-Shipping.exe`,
  24K objects): full scan + GWorld OK. Build 647 cross-version
  reproducer for the wrong-vtable-slot bug (PE was on `vtable+0x220`,
  the old detector picked `0x218` — off by 1 slot) — **fixed and
  fully re-verified on build 648 (2026-05-11)**: pattern scanner
  picks the correct slot, validator confirms 1260 hook fires in
  1500ms, and four real invokes succeed: KismetMath helpers (Add_IntInt
  = 7, Multiply_FloatFloat = 12) via static-native fast path, plus
  instance methods via game-thread dispatch (CharacterMovementComponent
  ::GetMaxJumpHeight = 89.99 float, PlayerCameraManager::
  GetCameraLocation = FVector struct).
- **Squad-Win64-Shipping** ✅ (UE 5.7, 240K objects): build 488 user
  reported 13 `get_object_list` 0xA0 UTF-8 exceptions → root cause was
  Serie wide-path surrogate encoding bug, fixed in build 555. Should
  now work clean post-560.
- **Barn Finders** ✅ (UE 4.25, 137K objects, build 560 user logs):
  full scan OK, UE5-Extended layout (strict). GWorld ✅. No new issues
  surfaced — pre-existing `find_by_address` `stoull` exception on
  malformed `0xrank` input from the Lookup field is already fixed in
  build 561+ (UI side `AddressHelper.TryNormalizeAddress` + DLL side
  `Renge::TryStrToAddr` noexcept). Walker Misaligned-EnumProperty
  warnings (163 in session) cleaned up by `Scharf.h` in build 582.
- **Colossal** ✅ (UE 5.03, 41K objects, build 560 user logs, publisher:
  Atan, exe `Colossal-Win64-Shipping.exe`): full scan OK, UE5-Extended
  layout (strict), TaggedFFieldVariant (UE5.3+). GWorld ✅
  (`GWLD_ES2_6`). Project still ships Epic default copyright/company
  placeholder strings — no publisher thumbprint match expected.
- **Extinction** ✅ (UE 4.15, 230K objects, build 560 user logs,
  publisher: Modus Games, exe `Extinction.exe` under `Blink/Binaries/
  Win64/`): **lowest UE version verified end-to-end** — expands the
  previously-documented 4.18+ floor down to 4.15. Flat (non-chunked)
  `FFixedUObjectArray`, UProperty mode (UE < 4.25), `UField::Next=+0x28`.
  Patterns: GOBJ_RE2 (1.8s, 2 batches) / GNAM_CT3 (4.6s, 4 batches) /
  GWLD_G42_1 (3.3s, 3 batches) — ~10s total scan but all three globals
  resolved on first scan and validated. GWorld ✅.
- **Star Wars Jedi: Fallen Order** ✅ (UE 4.21, 313 887 objects, build
  704 user logs 2026-05-12, EA Origin / Steam launcher): full scan OK —
  GObjects=0x7FF7316F5CD0, GNames=0x12B65A10080,
  **GWorld=0x7FF7317EBAB8** (non-zero, valid). DynOff full UE4 layout
  (`UField::Next=+0x28`, `UStruct::ChildProperties=+0x50`,
  `UProperty::ElemSize=+0x34`). Install path
  `H:\SteamLibrary\steamapps\common\Jedi Fallen Order`, exe layout has
  TWO identical 58.4 MB copies side-by-side in `SwGame\Binaries\Win64\`:
  `SwGame-Win64-Shipping.exe` (canonical UE name) +
  `starwarsjedifallenorder.exe` (EA-launcher target name). CE shows the
  running process as the latter. **Proxy DLL caveat**: neither
  `version.dll` nor `dinput8.dll` proxy gets loaded by the EA launcher —
  must inject via Cheat Engine after the game is running. Scan +
  dump pipeline works identically once the DLL is in-process.
- **MS Gundam SEED Battle Destiny Remastered** ✅ (UE 4.27, 57K objects
  → 72K mid-game, build 1016 user logs 2026-06-11, Steam): full scan OK —
  GObjects=0x7FF758C32550 (GOBJ_ES53_1, UE5-Extended layout strict, Max
  2.16M / 33 chunks), GNames=0x7FF758BF6200 (GNAM_V8, FNamePool hdrOff=0
  stride 2, `UE4Names=no` — a UE4.27 on the UE5-style FNamePool),
  **GWorld=0x7FF758D77040** (GWLD_GH_1). FProperty mode (CPN=no,
  TagFFV=no; `FField::Next=+0x20`, `Name=+0x28`; `FProp::Offset=+0x4C`,
  `StructProp=+0x78`). `version.dll` proxy loads (real version.dll).
  ProcessEvent on **vtable+0x220**, game-thread dispatch validated
  (15 646 hook fires / 1500 ms) and invokes succeed. Install
  `H:\SteamLibrary\steamapps\common\SEED BATTLE DESTINY REMASTERED`, exe
  `Game_SBDR\Binaries\Win64\SEED BATTLE DESTINY REMASTERED.exe`; internal
  UClasses use a `Life` prefix (`LifeGameInstance` 15 fields,
  `BP_LifeSaveData_C`). Bandai Namco (publisher=- — no thumbprint match).
- **Solarpunk** ✅ (stock UE 5.7, ~149K objects, rokaplay, `version.dll` proxy,
  build 1259 user logs 2026-06-17): the real stock-UE5.7 game that exposed +
  live-confirmed the **`Object`@+0x08 within-item layout** (24-byte FUObjectItem:
  `FlagsAndRefCount@0, Object@8, Serial@0x10`). The classic `+0x00` pass is
  bad-dominated (`named=66 / bad=69` — a stride-16 mis-read hits Object only ~1/3
  of the time) so `Aura::DetectItemSize` now falls through to the `+0x08` pass →
  `size=24, offset=+0x08, 200 named / 0 bad` → name resolution ~100% (was 45.9% /
  sanity 4/10 before the **build-1257** fix → now 10/10). GObjects `GOBJ_V13`,
  GNames `GNAM_SAT425_3`, **GWorld ✅ via instance-scan recovery** (raw deref hits
  a decoy `0x1C2D5`). ProcessEvent vtable+0x260, dispatch validated. Closes the
  build-1064 "+0x08 needs a real stock-5.7 game" live-confirm.

GWorld success ratio: **100% of all tested games** — see the
[test-games.md](test-games.md) GWorld Status Summary for the authoritative tally
(**29 / 29** as of 2026-06-17, incl. Solarpunk stock-UE5.7 via the `+0x08` fix);
the list above itemises only a subset and is otherwise last-verified 2026-06-11.
Satisfactory (modular DLL build): scan side OK — `Macht::AOBScanAllModules`
falls through to `FactoryGameSteam-CoreUObject-Win64-Shipping.dll`
under `Engine\Binaries\Win64\` and the 15-game dump corpus includes
its 4,868 BPGCs cleanly. Proxy deploy was previously broken because
the UI skipped the `Engine` subfolder; fixed build 691 (the real
game .exe lives in `Engine\Binaries\Win64\` for this title, not
under `FactoryGame\`).
Star Wars Jedi: Fallen Order: scan side OK as above; proxy deploy
inherently broken because of EA launcher (see lesson in
[lessons-learned.md → Proxy DLL Deploy](lessons-learned.md#proxy-dll-deploy)).
For both EA-launcher and other launcher-wrapped titles, recommend CE
manual injection as the documented workaround.

## Long-running concerns

These are not actionable next-session work — see
[todo.md](todo.md) for that — but they are worth re-checking before
shipping any major Walker / Detection change:

- **`kPublishers[]` table review** — every new publisher we add changes
  detection behaviour for all that publisher's titles. Touch with care;
  prefer per-game user override over a publisher-wide bias unless we
  have ≥3 misdetected titles from the same publisher.
- **AOB pattern decay** — UE engine source rotates roughly every minor
  version. The 128 patterns in `Himmel.h` are time-stamped per
  introducing build; any pattern that hasn't matched in ≥4 minor
  versions is a candidate for removal at the next clean-up.
- **HintCache schema additions** — the `FillPointerSnapshot`
  refactor (build 588) closed *one* instance of a recurring trap. New
  scan-time fields must land in BOTH `CMD_GET_POINTERS` *and*
  `CMD_SCAN_STATUS` payloads. The shared helper enforces this for
  pointer fields; the equivalent guarantee for object-list / walker
  payloads does not yet exist.
