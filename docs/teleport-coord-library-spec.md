# Teleport Coordinate Library — design spec

> **Status: EVALUATED, NOT BUILT** (2026-07-22, against build 2252).
> Multi-agent investigated + adversarially verified. Open work is tracked in
> [todo.md](todo.md) under *"Teleport Coordinate Library"*.
> Parent contract: [teleport-spec.md](teleport-spec.md) (Wirbel markers / POV /
> coord TP). This document only covers the **UI-side unlimited coordinate list**
> and its CE-Lua export/import.

-----

## 1. The ask

Today the Teleport tab offers **3 DLL-side marker slots** (`Wirbel`, hotkey-driven,
survive a UI restart) plus a one-shot **"TP to coords"** numeric-entry card. That
is not enough for the real use case:

> A walkthrough author records **every chest coordinate** in a multi-map game.
> Coordinates from different maps can be numerically close. Teleporting *between*
> maps is risky (streaming / graphics cache / level load). The user needs to keep
> thousands of labelled positions, group them, filter them, pick one, confirm, and
> only then teleport.

Requirements, verbatim from the request:

| # | Requirement |
|---|---|
| R1 | Remember effectively unlimited coordinate sets (~4K is past the human limit) |
| R2 | Per-entry **Label** + **Group**; editable in the UI; sorted by group+label in Lua |
| R3 | Pick an entry, then **confirm**, then teleport (never one-click-fires) |
| R4 | UI: *Save current pos* / *Edit pos list*, a **filter** to narrow the edit scope, in a **collapsible card, collapsed by default** |
| R5 | Export to Lua: a **needs-proxy-DLL** flavour and a **no-DLL** flavour |
| R6 | Export the editor's coordinate data as an **AA script via AOBMaker**; the Lua picks an entry and teleports; the Lua has its own filter |
| R7 | **Re-import** a previously generated AA script (copy-paste the whole thing) so the user can round-trip: export → hand-edit → import → edit in UI → export |

Explicitly **out of scope** (user decision, 2026-07-22): whether a saved coordinate
is still valid after the game is patched. The tool stores and replays coordinates;
it makes no claim about their continued validity.

-----

## 2. Verdict

**Feasible. The core needs ZERO DLL / pipe changes.**

The explicit-coordinate teleport already exists on both transports:

* **Pipe** — `teleport_recall_marker` with `x/y/z` (+ optional `pitch/yaw/roll`)
  → [`DumpService.TeleportRecallExplicitAsync`](../ui/UE5DumpUI/Services/DumpService.cs) (`:3107`).
  Note the command name is shared with slot recall; the presence of `x/y/z`
  selects the explicit path.
* **CE mailbox** — `CMD_TELEPORT = 8`, `op 13 = TP_OP_EXPLICIT`
  (`dll/src/Mimic.h:158-160`), already emitted by
  [`TeleportScriptGenerator`](../ui/UE5DumpUI/Services/TeleportScriptGenerator.cs)
  `Action.Explicit`.

So the feature is **a persisted UI-side list plus a new caller of an existing
primitive**. P1 is pure C#/AXAML.

-----

## 3. The five load-bearing decisions

### D1 — Per-game file key: **exe module name, NOT PE hash**

`BookmarkStore` keys per-game files by `PeHash` (TimeDateStamp + SizeOfImage),
which changes on **every game patch**. Bookmarks *should* die on a patch — they
store offsets. A hand-curated 4 000-entry chest list must **not**.

**Decision:** key the file by a sanitised `EngineState.ModuleName`
(`ui/UE5DumpUI/Models/EngineState.cs:33`, e.g. `MyGame-Win64-Shipping.exe`):

```
%LOCALAPPDATA%\UE5CEDumper\teleport-coords.{sanitizedModuleName}.json
```

Per §1 the tool makes no validity claim after a patch — this decision is only
about **not losing the user's data**. Provide an *"Import from file…"* action for
the renamed-exe case.

### D2 — `Map` is a first-class field

`teleport_get_pose` already returns the `UWorld` name
(`TeleportPose.Map`), and the CE mailbox exposes it too: the `GET_POSE`
output block puts a null-terminated map name at `paramsData[48..175]`
(`dll/src/Mimic.h:42-45`) — i.e. `mailbox + 0x358` — so **both** the app and the
generated Lua can know the current map.

The explicit-coordinate teleport path performs **no map check** (only slot recall
returns `-7 MapMismatch`). Therefore:

* Capture `Map` at save time, store it, show it as a column.
* Default the list to **current map only**; entries from other maps are visibly
  flagged and require an explicit **Force** action.
* Be honest in the UI: **the tool cannot send you to another map.** An entry
  becomes usable only once the game itself has loaded that map. Cross-map safety
  is achieved by *filtering*, not by teleporting.

### D3 — Data channel: embed in the AA script (with a Table File escape hatch)

Verified limits on the AOBMaker `CreateAAScript` path:

| Limit | Value | Source |
|---|---|---|
| Max JSON message | **10 MiB** hard reject | `AOBMaker/plugins/CEPlugin/src/pipe_server.cpp:61`; `PipeProtocol.cs:69`; mirrored `AobMakerBridgeService.cs:20` |
| JSON escape expansion | **+2.3 % … +4.6 %** measured over our shipped scripts | `UnsafeRelaxedJsonEscaping` (only `\n` expands) — `AobMakerMessage.cs:114-135` |
| Server read deadline | **5 000 ms total** for prefix + payload, 10 ms sleep per stall | `pipe_server.cpp:30,55,78` |
| Our response timeout | **5 000 ms** (`CreateAAScript`) vs **15 000 ms** (`InjectTableFile`) | `AobMakerBridgeService.cs:19,35` |
| `MemoryRecord.Script` itself | no size validation (only non-empty) | `pipe_server.cpp:784-791,857` |

**4 000 entries ≈ 400 KB — roughly 4 % of the hard cap.** An earlier worry that
JSON escaping "halves" the budget was measured and **refuted**. The binding
constraint is the **5 s timeout**, not the 10 MiB cap, and 400 KB is far from it.

Real-world corroboration: `CrimsonDesert.CT` carries **6 508 entries × 3 lists =
714 KB** of list data inside one .CT and CE handles it.

**Decision:** embed the dataset in the AA script (R7 requires a self-contained,
paste-able blob). Keep two escape hatches:

* **Soft warn above ~2 000 entries** — for CE's AA-script *editor* UX, not for the
  wire. No hard cap.
* If a genuinely huge dataset ever appears, the **CE Table File** channel already
  exists and is already used by us for helper Lua
  (`IAobMakerBridge.InjectTableFileAsync`; plugin handler `pipe_server.cpp:1956`).
  It picks a safe long-bracket level automatically and gets 15 s. Not needed for v1.

### D4 — Wire format: a Lua table constructor, one entry per line, fence-delimited

Not a `--[[ ]]` comment block — a **real Lua table**, so the script needs no
runtime parser and the format is directly hand-editable:

```lua
-- UE5CD-COORDS-BEGIN v1
local COORDS = {
{'Mountain','Fields','Map01',67162.398,-20380.643,35.791,347.71,31.98,0},
{'Chest 1','Chest','Map01',87668.672,-24858.674,341.376,335.01,26.16,0},
{'Boss','','Map02',89828.133,-15534.243,850.328,346.88,0.93,0},
}
-- UE5CD-COORDS-END
```

Field order is fixed: `label, group, map, X, Y, Z, Pitch, Yaw, Roll`.
Group may be empty (real-world data is ragged — the request's own sample has a row
with no group). Numbers are always `CultureInfo.InvariantCulture`.

**Import (R7)** = paste the entire AA script; we locate the fence and apply one
regex per line. **We never evaluate or parse arbitrary Lua.**

Two importer rules that fall out of verified code:

1. **XML entity handling.** `CheatTableBuilder.EscapeXml` escapes five entities
   (`& < > " '`) but `CeXmlExportService.ExtractAssemblerScript` un-escapes only
   three (`&lt; &gt; &amp;`) — a real, currently-latent asymmetry. If the pasted
   text is the **clipboard .CT XML fallback**, the importer must un-escape all
   **five**. Detect the XML form deterministically (starts with `<?xml`, or
   contains `<AssemblerScript>`); apply entity decoding only then, so a label
   legitimately containing the literal text `&lt;` is not mangled in the raw-script
   case.
2. **Version tag is mandatory.** `v1` in the BEGIN fence. Refuse unknown versions
   with a clear message rather than mis-parsing.

### D5 — Two Lua flavours (R5)

| | **Needs proxy DLL** (mailbox `CMD_TELEPORT` op 13) | **No DLL** (`StandaloneTrainerScriptGenerator` extension) |
|---|---|---|
| Teleport mechanism | engine invoke (clean, settles) | raw write to `RootComponent.RelativeLocation` |
| Rotation | ✅ `hasRot` flag, mailbox `+0x358` | needs the already-baked `ctrlRotOff` |
| Current map name | ✅ `GET_POSE` → `mailbox + 0x358` | ❌ unavailable → the Map RadioGroup is omitted and the map guard degrades to a label |
| Survives a game patch | ✅ | ❌ baked offsets go stale |
| Known caveat | — | `AppendTpWeakNote` (`StandaloneTrainerScriptGenerator.cs:442-449`): on games that don't refresh the cached world transform the coords change but the character may not visibly move |

**Recommend the DLL flavour as the default**, with the no-DLL flavour clearly
labelled as degraded in its own script header.

-----

## 4. Special characters — what to block in the UI

The request asked whether the UI should block characters up front. **Yes, and the
list is short**, because D4 chose single-quoted Lua literals (escapable) over a raw
long-bracket block.

### Hard block (reject at input; strip on import)

| Input | Why | Evidence |
|---|---|---|
| `NUL` (U+0000) | The CE plugin compiles the script with `luaL_dostring`, which takes **no length** → `strlen` silently truncates the chunk at the NUL | `pipe_server.cpp:881`; `lauxlib.h:124-125` |
| `CR` / `LF` | The wire format is strictly one entry per line | D4 |
| All other C0 control chars | Illegal in XML 1.0 with no entity form; `EscapeXml` passes them through unchanged | `CheatTableBuilder.cs:230-250` |
| The literal substring `]==]` | `CreateAAScript` wraps the **whole** script in `[==[ … ]==]` at a **hardcoded level 2**, so this sequence terminates the wrapper early and breaks the push. (`InjectTableFile` is safe — it calls `PickLongBracketLevel`; `CreateAAScript` does not.) | `pipe_server.cpp:857` vs `:1946-1954` |

Verified 2026-07-22: **nothing under `ui/` or `scripts/` currently emits `]==]`**,
so this is a new-input hazard only. (A pre-existing latent instance of the same
class lives in `BakedScriptGenerator.EscapeLuaComment`, which neutralises `]]` but
not `]==]` — tracked separately, out of scope here.)

### Allowed, handled by escaping — do NOT block

| Input | Handling |
|---|---|
| `'` and `\` | Lua-escaped by an `EscapeLua`-class helper on emit; un-escaped on import |
| `"` `<` `>` `&` | XML-entity-escaped on the .CT / clipboard path; five-entity decode on import (see D4) |
| **CJK / non-ASCII** | **Fine.** The AOBMaker JSON encoder is `UnsafeRelaxedJsonEscaping` precisely because the CE plugin's Lua JSON parser cannot decode `\uXXXX` (`docs/aobmaker-integration.md:42,412`), so non-ASCII crosses the wire as literal UTF-8. `CrimsonDesert.CT`'s 6 508-entry `ItemID-zh_tw.` list proves CE renders CJK in a ListView. **The repo's ASCII-only rule applies to generated *comments*, not to user data.** |

### Additional UI constraints

* `Label` ≤ 64 chars, `Group` ≤ 32 chars — keeps the ListView readable and the
  script compact. Trim leading/trailing whitespace.
* `Map` is engine-derived (`UWorld` name), never user-typed; apply the same
  control-char strip defensively.
* The **new generator must not use `--[[ ]]` long-bracket comments at all** — use
  `--` line comments, so `]==]` can only ever enter via user text.

-----

## 5. The generated Lua picker (R6)

### Verified CE Lua API surface

The project rule is **never invent a CE Lua API**. Two sources of truth:

*Already emitted by us* (`InvokeScriptGenerator.cs:235-305`): `createForm`,
`createLabel`, `createEdit`, `createButton`, `createTimer`.
**`createListBox` and `createComboBox` appear nowhere in this repo and are NOT verified.**

*Verified working in `CrimsonDesert.CT` "open item ID query GUI"* (CheatEntry 357,
`:9306-9650`) — the reference the request points at:

| Control | Verified members |
|---|---|
| `createForm` | `.Caption .Width .Height .Position('poScreenCenter') .ClientWidth .ActiveControl .Destroyed .show() .close() .BringToFront() .OnClose → caFree` |
| `createPanel` | `.Align('alTop'/'alBottom') .Height .BevelOuter('bvNone')` |
| `createLabel` | `.Caption .Left .Top .Align('alClient')` |
| `createEdit` | `.Left .Top .Width .Text .OnChange` |
| `createRadioGroup` | `.Caption .Top .Left .Width .Height .Columns .Items.add(str) .ItemIndex .OnClick` |
| `createButton` | `.Caption .Left .Top .Width .Height .OnClick` |
| `createListView` | `.Align .ViewStyle('vsReport') .RowSelect .ReadOnly .MultiSelect .SelCount .Selected` · `.Columns.add().Caption`, `.Columns[i].Width` (0-based) · `.Items.beginUpdate()/.clear()/.add()/.endUpdate()/.Count/[i]` · row `.Caption`, `.SubItems.add(str)`, `.Selected` · `.OnDblClick`, `.OnKeyDown(sender,key,shift)` |
| globals | `synchronize(fn)`, `showMessage`, `writeToClipboard`, `caFree`, `syntaxcheck` |

**Design accordingly: `createListView` + `createRadioGroup` only. No ListBox, no
ComboBox, no CheckBox.**

### Layout rules stolen from the reference

* **Panel creation order is load-bearing**: `alTop` panels stack in creation
  order and the `alClient` control **must be created last**
  (`CrimsonDesert.CT:9428,9519`). Order: `pnlTop` → `pnlStatus` → `pnlBottom` → `lv`.
* Re-open guard: `if frm ~= nil and not frm.Destroyed then frm.show(); frm.BringToFront(); return end`.
* A status label doubles as the live match counter.
* Display cap **2 000 rows** with a *"Matched N (showing first 2000) — type more to
  narrow"* message (`:9552`). Note this is the reference's *hardcoded guess*; no
  measurement of where a CE ListView actually stutters exists.

### Two facets, two RadioGroups

The reference's `sources` (EN/TW/JA) act like the request's **Group**, hard-capped
at 1–3 (`:9354`). We have two facets, and both map onto the verified control:

* **RadioGroup A — Group.** Items = `All` + the **top N groups by entry count** in
  the exported set. **N = 8** (2 rows × 4 columns) is the recommended cap.
  Overflow groups are *not lost*: every entry is still reachable via `All`, and
  the group name is part of the search key, so typing it filters. Warn at export
  time when groups were folded.
* **RadioGroup B — Map scope.** `Current map` / `All maps`. DLL flavour only
  (needs `GET_POSE`); omitted in the no-DLL flavour.

### Filter

The reference uses a plain case-insensitive substring over
`lower(id .. ' ' .. desc)` with `string.find(..., 1, true)` and no debounce.
**We mirror the app's MUST-rule instead: whitespace-separated terms combined with
AND**, matched against `lower(label .. ' ' .. group .. ' ' .. map)`. It is a few
lines of Lua and keeps app and script semantics identical.

Rows are **pre-sorted at generation time** (Group asc, then Label natural-sort so
`Chest 2` precedes `Chest 10`), so the Lua never sorts.

### Confirm-before-teleport (R3)

No CE yes/no dialog API is verified, so use two buttons instead — which also
matches the existing marker Force semantics:

* **`Teleport`** — refuses with `showMessage` when the selected entry's map ≠ the
  current map (DLL flavour), otherwise fires `CMD_TELEPORT` op 13.
* **`Force teleport`** — ignores the map guard.
* `OnDblClick` = same as `Teleport` (guarded), never Force.

### Hygiene

This is an **interactive** form, so it must not use the momentary auto-close path.
Follow `InvokeScriptGenerator`'s form shape: the untick + `CeLuaHygiene.CloseCall`
go in `frm.OnClose`, and **every error path must leave the window open**
(project MUST-rule, `CLAUDE.md` → *CE Lua output hygiene*). Emit the
`CeLuaHygiene.AppendDebugPreamble` in every `{$lua}` block that uses `DEBUG`/`dbg`.

-----

## 6. App-side UI (R2 / R4)

### Placement

A new card immediately **below the existing "TP to coords" card**
(`TeleportPanel.axaml:466-507`), which gains a **`＋ Add to library`** button.

The Teleport tab's right-click quick-jump menu is built **dynamically** from the
visual tree, so no code-behind change is needed — but it imposes three hard
structural requirements on the card:

1. It must be a **direct child** of the `ContentRoot` StackPanel.
2. It must be a **`Border`** (other element types are skipped).
3. Its label comes from the **first `FontWeight="SemiBold"` TextBlock descendant**.

Card chrome must match verbatim:
`Background="#252526" BorderBrush="#3E3E42" BorderThickness="1" CornerRadius="4" Padding="10"`,
inserted before `TeleportPanel.axaml:1287` (the status strip).

⚠ **Verify at implementation time:** the card is a `Border` wrapping an
`Expander`. Confirm that the SemiBold header TextBlock placed in the Expander's
`Header` is still reached by the jump menu's first-SemiBold-descendant walk —
otherwise the menu shows a wrong label.

### Collapsible, collapsed by default

Use the VM-bound `IsExpanded` dialect (SnapshotPanel / SpcPanel / LiveWalkerPanel),
`[ObservableProperty] bool` defaulted to **false**. The VM may force it open after
a *Save current pos* (existing precedent). No panel in the repo persists
`IsExpanded` across sessions today; if that is wanted it needs a new
`UiOptionsSettings` field.

### Contents

* **Toolbar** — `Save current pos` · `Add` · `Edit` · `Duplicate` · `Delete` ·
  **`Teleport selected`** · `Export ▾` · `Import` · `Clear all`
* **Filter row** — keyword `AutoCompleteBox` + Group combo + `Current map only`
* **Grid** — Label / Group / Map / X / Y / Z / Pitch / Yaw / Roll / Distance
* **Selection preview** — `Chest 2 — Map01 — 1,204 uu away`. Distance is free: the
  panel already polls `get_pose` every ~2 s.
* **Commit** — single click only selects; `Teleport selected` fires; a map
  mismatch raises a confirm. Reuse `TeleportToCoordsAsync`'s existing shape
  (`IsConnected` guard, `TeleportCodes.Describe` on failure, `Tier == 2` raw-write
  warning). `Save current pos` reuses `FillCoordsFromCurrentAsync`'s
  `TeleportGetPoseAsync` + `ApplyPoseAndMovement` path.

### ⚠ DataGrid virtualization

`TeleportPanel` has **no DataGrid today**, and its `ContentRoot` sits in a
vertically-unbounded `ScrollViewer` — **a DataGrid there will not virtualize**.
With 4 000 rows that is a hard perf problem. The grid **must** carry an explicit
`MaxHeight` (`LiveWalkerPanel.axaml:805` uses `MaxHeight="280"` as precedent).
Also note `ContentRoot` swallows `RequestBringIntoView`, so `BringIntoView()` will
not work — scroll via `Scroller.Offset`.

### Non-negotiable project rules this card must follow

* **Keyword box** — space = AND via `ObjectTreeFilter.SplitTerms` +
  `MatchesAllTerms(terms, params string?[])` (never one `.Contains` over a
  concatenated string) **and** per-keyword memory via `KeywordSearchMemory`
  (field + `XxxHistory` property + ctor probe `() => (FilterText, Results.Count > 0)`
  + `Schedule(value)` in the generated `OnFilterTextChanged`). This is a purely
  client-side list, so `Schedule` — not `Commit` — is correct. `AutoCompleteBox`
  with `PlaceholderText` (not `Watermark`) and `Text="{Binding …, Mode=TwoWay}"`.
* **UI strings** — English only, new `str.TP.*` keys in `Resources/Strings/en.axaml`.
* **Grid sorting** — per-column `SortMemberPath` pointing at the underlying model
  property (universal in this repo), so a formatted string still sorts numerically.
* **AOT** — source-gen JSON only.

-----

## 7. Store

Structural clone of `BookmarkStore` (`ui/UE5DumpUI/Services/BookmarkStore.cs`):
sync, `_ioLock`-guarded, source-gen JSON, atomic temp+rename, swallow-and-log with
empty defaults on missing/corrupt, `Delete()` as the clear-all primitive.

Deviations, each deliberate:

| Aspect | Decision |
|---|---|
| File key | **exe module name**, not PE hash (D1) |
| `DefaultIgnoreCondition` | **MUST NOT** be `WhenWritingDefault`. Follow the `BookmarkFile` / `UiOptionsSettings` dialect. A legitimately-saved coordinate of exactly `0.0` (or `Pitch/Yaw/Roll = 0`) would otherwise be dropped from the JSON and silently reload as 0 *by accident rather than by record*. |
| Backup | Keep one `.bak` alongside the atomic rename. This is hand-curated user data — a crash-on-write losing a 4 000-entry list is by far the worst failure mode in this feature, and it is the one thing `BookmarkStore` does not defend against. |
| Model | `public sealed class`, plain get/set POCOs, explicit `Version` int (v1), `List<CoordEntry>` |
| Context | `internal partial class CoordinateLibraryJsonContext : JsonSerializerContext` in `ui/UE5DumpUI/Models/` (never in `Services/`), matching the 7 existing contexts |
| Wiring | Construct in `App.axaml.cs` next to the other stores → trailing optional ctor param on `MainWindowViewModel` → trailing optional param on `TeleportViewModel` (which takes **no** store today, so `null` in headless tests) |

**Load hook.** `TeleportViewModel.SetEngineState` (`:226`) is a one-line stash that
does **not** capture identity today. Mirror `LiveWalkerViewModel`: capture the key
inside `SetEngineState`, but perform the disk load from a **separate public
`LoadCoordLibraryForGame(...)`** called explicitly by `MainWindowViewModel` — so
the load is not a hidden side effect.

⚠ `MainWindowViewModel` has **two** engine-state fan-out sites and they are not
symmetric: `:2502-2521` (`ApplyEngineState`) performs the bookmark load, `:613-623`
(connect path) does not. Determine which are reachable and wire deliberately.

-----

## 8. Phasing

| Phase | Content | Effort | Risk |
|---|---|---|---|
| **P1** | Model + store + collapsible card + CRUD + `Save current pos` + `Teleport selected` with map guard + filter | **M** | low — UI only, no DLL change |
| **P2** | Lua export, DLL flavour: generic picker form + embedded dataset + AOBMaker push, clipboard `.CT` XML fallback | **S** | low–med — CE API surface is verified but untested by us |
| **P3** | Re-import (R7): fence locator + per-line regex + five-entity XML decode + merge/replace dialog | **S–M** | low |
| **P4** | No-DLL flavour via `StandaloneTrainerScriptGenerator` (degraded map guard) | **S** | med — raw-write TP is inherently weaker |
| **P5** (opt) | TSV/CSV import-export; `TP to selected` / `next` / `prev` global hotkeys | **S** | low |

P1 alone delivers R1–R4 — the bulk of the value. P2–P4 are the CE integration.

-----

## 9. Open questions for implementation time

1. **Quick-jump label** — does the menu's first-SemiBold-descendant walk reach a
   TextBlock inside an `Expander.Header`? (§6)
2. **ListView throughput** — the reference's 2 000-row display cap is an unverified
   guess. Measure where `lv.Items.add()` actually stutters before treating 2 000
   as meaningful.
3. **Which `MainWindowViewModel` fan-out sites are reachable** for the store load. (§7)
4. **Experimental gating** — five Teleport cards are gated on
   `ExperimentalEnabled`. A coordinate bookmark list is not combat-affecting, so
   it likely should **not** be gated; confirm.
5. **CE Lua `readString`** — the DLL-flavour picker needs to read the map name from
   `mailbox + 0x358`. Confirm `readString` against CE's own API reference before use.
6. **Client-side pre-flight size check** — `AobMakerBridgeService.WriteMessageAsync`
   (`:495-506`) has **no** send-side size cap, and the plugin's oversize path
   (`pipe_server.cpp:61`) returns without writing a response, so an oversized push
   surfaces as a confusing *timeout* rather than a size error. Worth adding
   regardless of this feature.

-----

## 10. Cross-references

* [teleport-spec.md](teleport-spec.md) — Wirbel markers, POV, coord TP, the `CMD_TELEPORT` op table
* [aobmaker-integration.md](aobmaker-integration.md) — the CE-plugin pipe bridge
* [export-formats.md](export-formats.md) — CE XML / CSX export rules
* [ui-spec.md](ui-spec.md) — Avalonia stack + AOT constraints
* `CLAUDE.md` → *CE Lua output hygiene* and *Keyword search boxes* — both are
  MUST-rules this feature is bound by
