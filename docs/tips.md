# Tips & Recipes

User-facing how-to recipes for common tasks with UE5CEDumper. Each recipe maps
a goal to the panels / buttons that get you there. Add new recipes as separate
`##` sections.

-----

## Walking the engine from the top: "Start from GameEngine"

The Live Walker tab has two root entry points:

- **Start from GWorld** — roots on the live `UWorld` and lists its top-level
  actors. Best for "find an actor / enemy / component in the level".
- **Start from GameEngine** — roots on the live `UEngine`/`UGameEngine` object.
  Best for reaching engine-owned state: `GameInstance` (→ subsystems, save data,
  local players), `GameViewport`, the engine's own `World`, audio/subsystem
  managers, etc.

It's resolved by a reflected member (the engine's `GameViewport` property), **not
by class name**, so it works regardless of UE version or a game-specific
`UGameEngine` subclass. From the engine root, drill down with normal pointer
navigation (`GameInstance → …`).

### What works from a GameEngine root (and what doesn't)

- **Copy CE XML / Copy CE Field — yes.** The exported pointer chain is anchored
  on the engine object's **absolute runtime address** plus the breadcrumb spine.
  It's valid for the current session.
- **The `AOB` checkbox is disabled** from a GameEngine (or any non-GWorld) root.
  The AOB symbol anchors specifically on **GWorld**, so it can only stabilize a
  GWorld-rooted chain across restarts. From other roots the export uses the
  direct address — which is why the option greys out. If you need a
  restart-stable chain, start from GWorld instead, or re-resolve the engine
  address each session.
- No "Locate in GameEngine" / reverse-path features — those are GWorld-only.

-----

## Finding character-control / shop functions (jump / dash / interact / open-shop)

The **Interesting Functions** finder is tuned for *cheat-value* targets — stat/resource
nouns (HP/MP/Gold) and movement/combat cheat verbs. Basic **operation** verbs like
`Dash` / `Dodge` / `Roll` / `Interact` / `Open` / `Buy` / `Sell` / `Shop` / `Vendor` /
`Trade` are **not** in the default keyword tables, so they score 0 and hide below the
threshold. Two ways to surface them:

### The opt-in "Gameplay Actions" pack (recommended)

1. Open **Interesting Functions → Load**.
2. Tick the green **Gameplay Actions** checkbox (next to *Show All*). This folds an extra
   keyword pack (`Dash`/`Dodge`/`Interact`/`Use`/`Open`/`Buy`/`Sell`/`Shop`/`Vendor`/
   `Merchant`/`Trade`/`Purchase`/…) into the score and **re-scores in place** — no reload.
   The matching rows get a green **Gameplay** category chip.
3. Narrow with the filter box (space = AND over func + class name): type `shop`, `buy`,
   `dash`, `interact`. Filter to the **Gameplay** category in the dropdown to see only these.
4. Tick **BP/Exec only** to hide native getter/setter/plumbing noise — gameplay/control/shop
   entry points are almost always `BlueprintCallable` or `Exec` (both survive cooking). Combine
   with *Show All* to browse callable action functions even when they score below threshold.

It's opt-in (default off) because it's noisier than the cheat-value default — turn it back
off to return to the tuned view.

### Best for "open shop UI" and other unguessable functions: the Live Funcs profiler

When the name gives you nothing (a game-specific `OpenShop` / `BeginTrade` / a mangled
Blueprint name), stop guessing names and watch what the game **actually calls**:

A single recording captures a lot (one real case: 70 functions / ~75k calls in 7s), and
per-frame `Tick`/`Update` noise dominates the top while the shop-open function — which fires
only a handful of times — sinks to the bottom. **Use the baseline diff to isolate the action:**

1. Open the **Live Funcs** tab → **Start** → stand still a few seconds → **Stop**. This is
   your idle baseline. Click **⚑ Set Baseline** (turns on Diff mode).
2. **Start** → ALT-TAB to the game → walk up to the merchant and **open the shop** → ALT-TAB
   back → **Stop**.
3. The list now shows a **diff**: functions that did NOT fire while idle are tagged **NEW**
   (green) and ranked to the top; "New/changed only" hides the unchanged Tick noise.
4. Tick **Hide UI widgets** (the created UI, tagged **UI**, not the entry point) and **Hide
   events/delegates** (the `On*`/callback *reactions*, tagged **Event**/**Deleg** — not functions
   you call). What's left are imperative **Call** functions on persistent objects.
5. Tick **Earliest first**. An action's entry point fires *before* the reactions it triggers, so
   the causal opener sorts to the top of the NEW set (see the **Order** column) — name-independent.
6. Click **Live** on the top candidate to open it in Live Walker and invoke it.

> **If "Hide events/delegates" empties the list**, the action opened via a **native C++ call** the
> ProcessEvent hook can't see (or the panel hadn't fully appeared — record through until it's on
> screen). That's the limit of behaviour-based discovery. Fallback: while the UI is open, use
> Instance Finder on the vendor/inventory class and invoke its own `BlueprintCallable` functions
> (`AddGold`/`SellItem`/…) directly — you often don't need to "open" anything to change the state.

(Without a baseline it still works — just Start → action → Stop and sort/scan by count — but
the diff is what makes a busy game tractable. Leaving the tab auto-stops recording.)

> **Remember the earlier caveat:** a shop *widget* class like `DOLLShopStoreLayout` is the
> transient thing the action *creates* (GC'd when the shop closes — only its `Default__` CDO
> remains), not the opener. The NEW row you want is the function on a **persistent** object
> (PlayerController / a UI-manager subsystem / the vendor's interaction component) that opened it.

### Then: find the right instance and call it

A control/shop function is a **stateful instance method** — it needs a live, non-CDO `this`:

- Click **Live** on the row to open it in Live Walker (falls back to Class Struct if there's
  no live instance). Or use the row's **inst** button → Instance Finder.
- For the player: **Related Objects → 🎯 Detect target**, or Live Walker's *Start from GWorld*
  → PlayerController → Pawn.
- **Pipe Invoke** the function (Live Walker row) → fill params → **FIRE**. No-arg verbs
  (`Jump`, `StopJumping`, `Dash`) fire directly.

### Open-shop caveats (honest limits)

"Open shop UI" is **game-specific** and often needs a param (a vendor object pointer, or a
vendor/shop ID). Practical path:

1. Find the vendor/shop object: **Instance Finder** → `Shop`/`Store`/`Vendor`/`Merchant`, or
   spot the interaction actor in Live Walker's GWorld list while standing at the merchant.
2. See its functions **with params**: open its class in Class Struct / Live Walker
   (`walk_functions` shows each param's name/type/`obj_class`). Or use **Find Func** to list
   functions that *take that class as a parameter* — often lands `OpenShopFor(AVendor*)`.
3. In the invoke dialog, enter a vendor **ID** (`IntProperty`, decimal or `0x…`) or use
   **[Pick…]** to select the vendor **object pointer** (pre-filtered to the param's class).

Some games open the shop purely via a **UMG widget event** with no callable UFunction entry
point — those can't be reproduced by invoking a single function.

-----

## Forcing camera rotation in a fixed-view (2.5D / 45°) game

UE4 and UE5 share the camera pipeline, so the same handful of entry points work
across versions. A "locked" 45° / top-down camera usually just means the game
**never wired input to the camera** — the underlying UE camera chain is still
complete and writable.

### The shared camera chain (bottom → top)

```
APlayerController.ControlRotation
        ↓ (bUsePawnControlRotation / bInheritYaw ...)
USpringArmComponent.RelativeRotation   ← 2.5D games most often hard-code the angle here
        ↓
UCameraComponent
        ↓
APlayerCameraManager.CameraCachePrivate.POV.Rotation  ← final output, recomputed every tick
```

Typical fixed-view setup: the SpringArm's `RelativeRotation.Pitch` is set to
-45/-60 with `bUsePawnControlRotation=false` and `bInheritYaw=false`, so mouse
input never reaches the camera.

### Approaches (easy → hard)

**1. Debug Camera (easiest, UE4/5 both).**
`UCheatManager::ToggleDebugCamera` spawns an `ADebugCameraController` — a free-fly
camera (WASD + mouse), zero memory edits. In UE5CEDumper: **Console → load exec
commands → the Debug Camera control row** (visible when `ToggleDebugCamera` is
found). Use **Force On / Force Off** (robust — handles Shipping builds whose
`DisableDebugCamera` is stripped by switching the player's controller back), or
**Copy CE Script** for a self-contained CE checkbox (tick = on, untick = off).
Caveat: this is a *separate* camera; game logic keeps running underneath.

**2. Rotate the SpringArm / SceneComponent (most common real fix).**
Instances → find `SpringArmComponent` → open it in **Live Walker** → look at
`RelativeRotation` (FRotator = Pitch/Yaw/Roll; note UE5 LWC makes these `double`,
UE4 `float`). Edit **Yaw** to turn the view. If the value snaps back, a Blueprint
is re-setting it every tick — either **Freeze** the field, or invoke the function
instead of a raw write:

- `USceneComponent::K2_SetRelativeRotation` — BlueprintCallable, exists in UE4 &
  UE5, callable through the invoke helper via ProcessEvent (cleaner than a raw
  write — it runs `UpdateComponentToWorld`).
- While you're there, check the `bInheritYaw` / `bUsePawnControlRotation`
  bitfields — flipping `bUsePawnControlRotation` to `true` often restores mouse
  control of the view outright.

**3. PlayerController.ControlRotation.**
`APlayerController::SetControlRotation` is a cross-version native function (the K2
`SetControlRotation` is BlueprintCallable). In fixed-view games it's usually cut
off by the SpringArm's inherit flags, so combine it with the bitfield flips from
approach 2 for it to have any effect.

**4. CameraManager POV (last resort).**
`APlayerCameraManager.CameraCachePrivate.POV.Rotation` is the final value, but
`UpdateCamera` overwrites it every tick — a raw write does nothing. You'd have to
hook (UE5CEDumper already has the Stark/MinHook ProcessEvent base) or NOP the
write. Rarely worth it; the first three paths usually suffice.

> **Diagnose first — Teleport tab → "Camera POV" → Get POV.** This reads the
> live camera location / rotation / FOV (via `GetCameraLocation` /
> `GetCameraRotation` / `GetFOVAngle`) and shows the camera↔pawn distance. It's
> read-only (there's no Set POV — `UpdateCamera` would overwrite it), but it
> tells you *which* approach you need: if the camera location barely changes
> when you teleport the pawn, the camera is independent (use approach 1's Debug
> Camera or approach 2/SpringArm) rather than pawn-following.

### Caveats

- Some games don't use a SpringArm — they `SetViewTargetWithBlend` to a fixed
  `CameraActor` placed in the level. Then rotate **that actor's** RootComponent
  instead (`K2_SetActorRotation` / `K2_SetWorldRotation`).
- 2.5D side effects: after rotating, billboard sprites, occlusion culling, and
  "front-only" art can break — that's a limitation of the game's art, not a tech
  problem.
- FOV: `APlayerCameraManager.DefaultFOV` (or the `fov` console command) is also
  cross-version; you'll often want to widen it when pulling the view back.

### Fastest workflow in UE5CEDumper

Instance Finder → search `SpringArmComponent` → Live Walker → inspect
`RelativeRotation` → edit **Yaw** and watch the screen turn → if it gets
overwritten, flip `bUsePawnControlRotation` / `bInheritYaw`, or invoke
`K2_SetRelativeRotation` instead of writing raw.

-----

## Save & recall positions / teleport to the cursor

Use the **Teleport** tab (works after Connect + scan). It resolves the local
player's pawn for you — no per-game class names needed — and teleports by
invoking the engine's own functions, so it's clean across UE4 and UE5.

**Marker save / recall (BugItGo-style).** Stand where you want, click **Save**
on Marker 1/2/3. Later click **Recall** to jump back (rotation restored, fall
velocity zeroed). Markers live in the DLL, so they survive a UI restart as long
as the game keeps running. Recall is refused with a hint if you've changed maps
— click **Force** to override (you may land in an unloaded area). Markers store
coordinates only, so dying / respawning never breaks them.

**Teleport to the cursor (2.5D / 45° games).** For top-down ARPGs (Titan
Quest-likes) click **Teleport to cursor** — the pawn jumps to whatever's under
the mouse. For games with no visible cursor (SE HD-2D-style), leave **Fall back
to screen center** ticked and it traces from the middle of the view instead. If
hits land on the wrong surface, bump the **Channel** number (games remap trace
channels); **Z offset** lifts you off the ground so you don't spawn inside it.

**BugItGo interop.** **Copy as BugItGo** puts a `BugItGo X Y Z` string on the
clipboard (handy for sharing a spot). Paste any `BugItGo X Y Z`, bare `X Y Z`,
or a full `?BugLoc=(…)?BugRot=(…)` string into the box and click **Run** to
teleport there — no CheatManager needed. (Note: invoking the game's own `BugIt`
from the Console tab returns nothing on purpose — it writes to the game's log /
screenshot / clipboard, not back to the dumper. Use Copy-as-BugItGo instead.)

**Hotkeys in Cheat Engine.** Pick a **Hotkey scheme** (Numpad / Top-row / Both),
click **Copy CE Lua (hotkey bundle)**, and paste into a CE *Table Lua Script*.
That binds save/recall/cursor/copy to keys (default: Ctrl+Num1-3 save, Num1-3
recall, Num0 cursor — NumLock must be on). Use **Top-row** on laptops or when
the game already uses Ctrl+1-3. Requires `UE5Dumper.dll` injected; the script is
self-contained (no helper Lua). **Save .CT…** exports the same actions as 7
momentary records instead.

> Single-player only — online games will rubber-band or may flag teleports.

-----

## Flattening save-data records into a clean Cheat Engine table

A struct-heavy object — a save slot, a stats block, a `TMap` of mission records —
exports to CE as a tree of nested folders: one group per struct, one per array /
map element, each wrapping the actual values. CE *can* watch that, but you click
through a lot of folders to reach a number. The Live Walker **Options** dropdown
has a family of toggles that flatten that tree at export time. They all apply to
**Copy CE XML** and **Copy CE Field** only (the CSX Structure-Dissect export is
deliberately left nested), and **none of them change the watched address** — a
flattened row resolves to the exact same memory the nested one did, just with the
offsets folded into the row instead of spread across parent folders.

### The toggles (Live Walker → Options)

- **Flatten primitive-leaf structs** — collapses any struct whose whole contents
  are plain numbers (`int` / `float` / `bool` / `enum`), e.g. an `FVector`
  (X/Y/Z), an `FRotator`, an `FDateTime` (its single `Ticks`), or a `{Min,Max}`
  pair. Children become sibling leaves named `Struct ▸ Field` at the combined
  offset. A struct that contains a pointer / string / container keeps its group.

- **Flatten leaf records (names/strings)** — a superset of the above that *also*
  accepts `FName` and `FString` fields as leaves, so a save-data "record" struct
  like `{Score, Rank, MsID (FName), PilotName (FString)}` flattens **completely**.
  An `FName` renders as its 4-byte index; an `FString` renders as a CE **String**
  (it carries the one pointer-dereference CE needs to read the text). There is no
  field-count limit — the "every field is a leaf" requirement is the safety gate.
  **This also reaches into containers:** a `TMap` / `TArray` *of* such records
  drops its per-element `[i]` folders too, so 200 mission records become 200 flat
  rows (`[0] mc1om_001 ▸ Score`, …) instead of 200 folders. (Struct arrays / sets
  already flattened; only `TMap` kept a wrapper before.)

- **Collapse single-leaf pointers** — for the "pointer to a string" case: when a
  pointer's target object holds exactly **one** watchable value (a scalar, an
  `FName`, or an `FString`), it collapses the folder-plus-lone-child into one
  `Pointer ▸ Field` record at the pointer offset, following the pointer in the
  row's offset chain. A pointer whose target has two or more fields keeps its
  group (its object identity is worth a boundary).

All three are **off by default** and persist across sessions. The `▸` segment
names honour the **Append +Offset** option; the struct / class type (if you turn
on **Append +Type**) is shown once at the end of the row.

### Telling the records apart: Record Colors

Once a map of records is flattened, the per-element folders that used to separate
them are gone — 200 rows in a row. **Options → Record Colors…** opens an editor
that tints each element's rows by index parity (CE colours the row text), so
even-indexed records (`struct[0]`, `[2]`, …) read in one colour and odd-indexed
(`[1]`, `[3]`, …) in another — adjacent records stay visually distinct.

- Each of **Even** / **Odd** takes a colour from a neutral preset palette, a hex
  value, **Custom…** (an in-app picker: rainbow hue strip + R/G/B sliders + live
  preview), or **Reset** (no colour → CE uses its theme). Default: enabled, Even =
  azure, Odd = unset.
- Colours only land on **flattened container rows** — ordinary fields are never
  tinted, and nothing is coloured unless one of the flatten toggles is also on.

### Worked example

A save slot exposes `StoryMissionRecord` = `TMap<FName, LifeStoryMissionRecord>`
with 222 entries, each `{Score, Time, ClearCount, Rank, MsID, PilotID, PilotName}`.
Drill to it in Live Walker, tick **Flatten leaf records**, open **Record Colors…**
(leave the azure default), then **Copy CE XML** and paste into CE. You get a flat
list — `[0] mc1om_001 ▸ Score`, `… ▸ PilotName` (a readable String), `[1]
mc1om_002 ▸ Score`, … — with even and odd missions in alternating colours, no
folders to expand, every `Score` directly editable.

### Caveats

- **CE XML / Field only.** CSX export stays nested by design — if you export both
  from the same view they'll look different.
- `FText` is **not** flattened (its internal chain has no clean CE encoding); a
  struct / pointer holding one keeps its group.
- Flattening removes the object / element **boundary**, not the data. If you
  prefer the folders for orientation, just leave the toggles off.
