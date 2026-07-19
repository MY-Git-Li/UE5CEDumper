# Dev Log

Append-only milestone history, newest first. Each entry references a
build number from `build_number.txt` so commits can be cross-referenced.
**Reading tip:** grep `^## ` for the index, then read the top (newest-first).
Entries for **builds ≤1177** are archived: builds 939–1177 in
[archive/dev-log-2026-06-pre-build-1180.md](archive/dev-log-2026-06-pre-build-1180.md),
builds 715–937 in
[archive/dev-log-2026-06-pre-build-940.md](archive/dev-log-2026-06-pre-build-940.md),
builds ≤696 in
[archive/dev-log-2026-05-pre-build-700.md](archive/dev-log-2026-05-pre-build-700.md).

> **Looking for current state?** See [roadmap.md](roadmap.md) for the
> capability matrix / per-game configuration / tested games, and
> [todo.md](todo.md) for the prioritized next-work list. This file
> records *what shipped* — the other two record *what works now* and
> *what's next*.

-----

## 2026-07-19 — MindsEye: GNames solved — obfuscated FNameEntry payloads decoded from the fork's own key table (build 2238; dev, DLL needs re-inject)

**LIVE-VERIFIED on MindsEye game version 7.3.1 ONLY** (PE hash `0863E3B90C993000`; the exe carries no
game-version resource, so pin the build by that hash). **Name sanity 10/10** — GNames had never been
found for this title. Every RVA below is build-specific; re-derivation playbook in
[mindseye-fork-notes.md](mindseye-fork-notes.md). Experimental-gated end to end; a title without the
fork's fingerprint runs byte-identical code.

**The format.** MindsEye keeps the STOCK UE5 `FNameEntryHeader` but inserts a field and obfuscates
the payload:

```
+0x00  u16 header   stock: bIsWide:1 | LowercaseProbeHash:5 | Len:10   (len = header >> 6)
+0x02  u16 tag      NON-STOCK — selects this entry's XOR key
+0x04  chars[len]   single-byte XOR (stock puts chars at +0x02), 2-byte aligned
```

`FNamePool` = RVA `0x0BA306C0` (`FRWLock`=0, `CurrentBlock`, `CurrentByteCursor`, `Blocks[]` at
`+0x10`). Block 0 decodes under `0x09` to the canonical EName list — `None`, `ByteProperty`,
`IntProperty`, … — every length matching `header >> 6`.

**The key is per TAG, not per block** — an early hypothesis that cost a build to disprove. Observed:
tag `0x0001`→`0x09`, `0x0002`/`0x0082`→`0x5B`, `0x0003`→`0x1D`, `0x0016`→`0xA3`, `0x0036`→`0xE3`,
`0x0061`→`0xC9`.

**Where the key comes from.** The fork's de-obfuscator (RVA `0x0178B440` ANSI, `0x0178B540` wide)
does `len = header>>6`, `memcpy(dst, entry+4, len)`, then `KeyDerive(ctx, u16 @ entry+2)`
(RVA `0x0178CF50`) and `xor byte ptr [rax], dl` with an SSE `xorps` fast path. `KeyDerive` is an
open-hash probe under `RtlAcquireSRWLockShared`:

```
ctx +0x10 entries   +0x18 count   +0x44 sentinel (== count => empty)
ctx +0x48 inline buckets   +0x50 bucket array (0 => inline)   +0x58 capacity (pow2)
bucket = tag & (capacity-1)
entry = 24 bytes: +0x00 u16 tag | +0x08 u64 (LOW BYTE = key) | +0x10 i32 next (-1 = end)
```

**We read that table directly and NEVER call the routine.** Calling it was designed, adversarially
reviewed and dropped: `KeyDerive` takes the SRW lock *before* probing, so a fault inside it would
unwind out of game frames with the lock still held and permanently wedge every later
`FName::ToString` — no crash, no log, and `Tot` is a cooperative poll with no poll point inside game
code. Its ctx getter also reads `gs:[0x58]` with a lazy per-thread init branch, so calling it from a
thread the game never used is its own hazard. Reading the table has neither failure mode.

**Locating ctx** — all static, no control transfer: `Sig::AOB_NAMEDECRYPT_ME1` (Himmel, new `ME`
source tag) matches the de-obfuscator's semantically anchored prologue (unique in the 145 MB
`.text`; the 16-byte MSVC prologue alone hits 139x), then `match + AOB_NAMEDECRYPT_ME1_CTX_CALL_OFF`
(`0x2F`) `E8 rel32` -> the getter, and the getter's first `48 8D 05` rip-relative LEA -> ctx
(RVA `0x0BA47700`). Live: `decrypt fn=0x7FF60492B440 getter=0x7FF604931050 keyTable=0x7FF60EBE7700`
— all three exactly the RVAs confirmed in CE.

**Changes.**
- `Flamme::IsExperimentalEnabled()` — reads the SAME `%LOCALAPPDATA%\UE5CEDumper\experimental.json`
  the UI's `ExperimentalGate` writes, so the DLL honours the toggle on every entry path (UI pipe
  scan, CE Lua `UE5_Init`, proxy auto-start) with no protocol change. Missing/malformed => OFF.
- `Genau::TryObfuscatedPool` — appended LAST inside the `ValidateGNames` block-offset loop, after
  both stock layouts are rejected for that candidate. Acceptance is the SAME standard as stock:
  entry 0 must decode to exactly `"None"` — found in ONE compare, not 256, since a single-byte XOR
  onto `"None"` exists only if the three inter-byte deltas already match — then >=6 chained
  identifier entries must corroborate. The AOB scan runs only after all of that passes, so an
  ordinary title never scans it. No key table => the pool is REFUSED (decoding block 0 alone is not
  name resolution).
- `Serie::InitObfuscated` — adopts the geometry Genau proved instead of running the stock detectors
  (they all hunt a literal `"None"` in the payload, impossible before decryption), so no stock
  detection path is modified, only bypassed on this one branch. `s_payloadGap` is 0 for every stock
  title, so `strStart` resolves to the same address as before.

**Two bugs found on the way, both mine:**
1. *Heuristic key recovery was wrong.* The first design brute-forced a key per block behind an
   identifier-charset filter. It rejected 135 of 465 blocks: real pools are full of asset paths
   (`/Game/Storm/Animations/...` fails a `[A-Za-z0-9_]` gate) and wide entries aborted the walk.
   Superseded entirely by the table read; every heuristic deleted.
2. *A memory-ordering race produced wrong keys.* The tag->key cache wrote value then flag as two
   plain stores; nothing stops the compiler publishing the flag first, so another thread saw
   "resolved" with a still-zero key and XORed with 0 — `Object` rendered as `Fkclj}`. The same tag
   resolved to `0x09` on one thread and `0x00` on another **in the same millisecond**. Fixed by
   collapsing value+flag into ONE `std::atomic<uint16_t>` (bit 8 resolved, bit 9 miss, bits 0-7 key)
   — a single word cannot tear — plus a decode retry, since the table is LIVE (the game adds names
   as it runs) and a lock-free read can catch it mid-update.

**What is NOT recoverable, and why that is not a defect.** MindsEye ran a symbol-rename pass over
its own non-engine symbols at build time: property and class names are generated 16-character
all-lowercase identifiers (`wcxugjojsqaqvers`, `eurngjogndgrjhls`, ...). Proven, not inferred —
those strings appear verbatim in the exe's `.rdata`, and the binary holds **21,635** distinct 16-char
all-lowercase tokens. Length comes from the header and is key-independent, so a wrong key could
never produce them. Engine symbols (`LocalPlayers`, `NetTimeSyncComponent`, `AnalyticsComponent`, ...)
are untouched and read normally. The original names exist nowhere — not in memory, not in the
binary — so no tool can recover them.

Live Walker now walks `GWorld -> PersistentLevel -> StormWP -> EVMindsEyeGameInstance ->
LocalPlayers -> LocalPlayer -> BP_PlayerController_C` with correct values, classes and outer chains.

> The `GWorld does not deref to a UWorld — recovering...` line in an earlier session log is a
> scan-time timing artifact, not a defect: `GWorld` is a `UWorld**` static slot and `*GWorld` was
> still null while the game was loading. Later runs log no warning and `Start from GWorld` works.

-----

## 2026-07-19 — MindsEye: GObjects solved via preset-bound item layout; GNames located + name obfuscation reverse-engineered (build 2220; dev, DLL needs re-inject)

**LIVE-VERIFIED on MindsEye (Build A Rocket Boy, UE 5.4.4 licensee fork) — game version 7.3.1 ONLY**
(PE hash `0863E3B90C993000`). The first game in the matrix where `GNames=MISSING`, and the first where
the tool **reported `GObjects=OK` on garbage**. Every RVA below is build-specific; see
[mindseye-fork-notes.md](mindseye-fork-notes.md) to re-derive them after a game update.

**What was actually wrong (two independent bugs).** The AOB *did* find the real `GUObjectArray`
(RVA `0x0BB139B0`) and `ValidateGObjects` **rejected** it — the existing `"MindsEye"` preset is written
relative to `FChunkedFixedUObjectArray`, but the AOB resolves the `FUObjectArray` base, `0x10` earlier,
so `num@+0x14` read `NumChunks=9` and failed the `num < 0x1000` gate. The relaxed Tier 2 fallback then
accepted an unrelated heap blob (an ICU-like locale object containing the ASCII text `"International"`)
because its `numOff` landed on the **high half of an adjacent module pointer** — `Num=32758` is literally
`0x7FF6`. Result: `Count=509`, `named=0`, every lookup empty, init reporting `GObjects=OK`.

**Ground truth by offline disassembly** (capstone + the `.pdata` RUNTIME_FUNCTION table — no Ghidra;
`.text` is 145 MB so headless analysis was not worth the hours). `.rdata` still carries the `__FILE__`
anchors (`J:\work\e18f6e32b612e2cd\Engine\Source\Runtime\CoreUObject\Private\UObject\UObjectArray.cpp`),
so xref → containing function → rip-relative globals recovers everything:
`FUObjectArray::AllocateObjectPool` (RVA `0x019B17B0`) pins the five chunked fields, and the
index→object accessors (e.g. RVA `0x0191AA10`: `shr rcx,0x10 / movzx edx,bx / shl rdx,5 /
add rdx,[r9+rcx*8] / cmp qword [rdx+0x10],0`) pin the item layout.

| | value |
|---|---|
| `GUObjectArray` | RVA `0x0BB139B0` (`ObjObjects` at `+0x10`) |
| chunked fields | MaxElements `+0x10`, NumChunks `+0x14`, MaxChunks `+0x20`, NumElements `+0x24`, Objects `+0x28` |
| `FUObjectItem` | **32 bytes**, `UObject*` at **`+0x10`** (stock: 24 / `+0x00`) |
| elements per chunk | 65536 (stock) |

Matches the vendored Dumper-7 `MindsEye` layout exactly (`vendor/Dumper-7/.../ObjectArray.cpp:60`).

**Changes (all additive; the shared detection paths are byte-for-byte unchanged for other titles).**
- `Genau.cpp` — appended `{ 0x28, 0x10, 0x24, 0x20, 0x14, "MindsEye-Extended" }` as the **last** strict
  preset (the `Default → UE5-Extended` relationship already in that table).
- `Aura.cpp` — same row appended **last** in Tier 2 `s_ue4ExtendedPresets`. Cannot steal an existing
  title: `ValidateChunkedLayout` needs `maxChunks ∈ [6, 0x5FF]`, and on a UE5-Extended array this row
  reads `MaxElements` (~2.1M) as `maxChunks`.
- `Aura.cpp` — new **preset-bound `LayoutPreset::itemHint {stride, objOff}`**, consumed by
  `DetectItemSize` *before* the shared sweep and only when the winning preset carries one.
  **Deliberately not another entry in `candidates[]`:** MindsEye's 32/`+0x10` item aliases perfectly
  with stride 16 (every odd 16-byte slot is a real object pointer → `good=100 / bad=100`), so putting
  it in the shared sweep would let it outscore the true stride on genuine stride-16 titles
  (Titan Quest II, Octopath Traveler). Still evidence-gated (`hGood >= 8 && hBad*4 <= hGood`, which the
  50%-aliased result cannot reach); on rejection it logs and falls through to the unchanged sweep.
- `Genau.cpp` — relaxed Tier 2 rows gained a mirrored `maxOff` feeding an **upper-bound-only** check
  (`kRelaxedMaxCeiling = 0x4000000`, 8× the strict cap; skipped entirely if the read fails).
  Deliberately weaker than Tier 1 (no `max < num`, looser ceiling) so a title that raises
  `gc.MaxObjectsInGame`, or whose max field is elsewhere, still reaches the accept path as before.
  Kills the blob by 3.5× (its `max` reads `0x0DE62600` = 233M).
- `Genau.cpp` — GNames diagnostics: `ValidateGNames` now logs the **candidate pool base** (previously
  never logged, so a `chunk0` could not be attributed) plus **raw header bytes** instead of `name` —
  `name` is memset before the log and only ever filled after a length match, so the empty name in every
  historical log was a **logging artifact, not evidence about the game**. Added a 96-byte dump of the
  block itself on its own budget (the 7×2 per-offset probes used to exhaust the shared 10-line throttle).

Rejected after adversarial regression review (3 hunters × 5 rules): a pointer-high-bits reject rule
(would kill The Artisan of Glimmith at 24K objects — the qword covering `numOff` on a real array is
`Max | Num<<32`, which is itself in userspace range), an Aura quality floor, and a DEGRADED-report
trigger keyed on relaxed-tier acceptance (fires on correct resolutions, e.g. Avowed).

**Result:** `Layout 'MindsEye-Extended' detected (strict)` → `FUObjectItem size=32, object-ptr
offset=+0x10 (preset item hint) — 200 total, 0 bad` → `Count=530638, ItemSize=32`. Was
`Count=509, ItemSize=16, bad=100`.

**GNames — located, still unresolved (names ARE obfuscated).** The new block dump identified the pool
immediately: `FNamePool` = RVA `0x0BA306C0` (`FRWLock=0`, `CurrentBlock=507`,
`CurrentByteCursor=0x197D4`, then 64 KB-aligned `Blocks[]` at `+0x10`). The entry format keeps the
**stock UE5 header** but inserts a field and encrypts the payload:

```
+0x00  u16 header   stock: bIsWide:1 | LowercaseProbeHash:5 | Len:10   (len = header >> 6)
+0x02  u16 tag      NON-STOCK — the lookup key for this block's XOR key
+0x04  chars[len]   XOR-obfuscated (stock puts chars at +0x02), 2-byte aligned
```

Block 0 decodes under XOR `0x09` to the canonical hardcoded EName list — `None`, `ByteProperty`,
`IntProperty`, `BoolProperty`, `FloatProperty`, `ObjectProperty` — every length matching `header >> 6`.
The key is **per block**: block 0/1/2/3/6 = `0x09` / `0xE3` / `0x81` / `0xE7` / `0x33`, decoding
`None` / `GameplayTargetDataFilterHandle` / `RigUnit_DebugTransform` / `GetBoneTrackByName` /
`NetConnPacke…`. Encrypted bytes are identical across sessions at different addresses, so the key is
deterministic, not address-derived.

Decrypt routine found: **RVA `0x0178B440`** (ANSI; `0x0178B540` is the wide twin, `add r8,r8`). It does
`len = header>>6`, `memcpy(dst, entry+4, len)`, then `KeyDerive(ctx, u16 @ entry+2)` (RVA `0x0178CF50`)
and a byte-wise `xor byte ptr [rax], dl` with an SSE `xorps` fast path. `KeyDerive` is **not** closed
form — it takes a lock and does a hash-map probe (`bucket = tag & (capacity-1)`) into a runtime table
at ctx RVA `0x0BA47700`. So the key cannot be computed offline; it must be read from the live table or
obtained by calling the game's own routine. Follow-up (Plan A): AOB the decrypt function and route
`Serie` through it for this title.

**Also corrected:** the pak/IoStore container AES (CUE4Parse `MindsEyeAes.cs`) is real but irrelevant —
that is asset-load-time, not process memory. The binary itself is unpacked, has no Denuvo/EAC/BattlEye,
and stock `GWLD_ES2_6` / `SPARSE_ES2_1` still match uniquely.

-----

## 2026-07-14 — Invoke: by-value `fstruct` struct params in the CE Lua helper (dev commit `f66e602`; helper v1.3; reworked from PR #433)

**Committed to `dev` (managed build clean + `InvokeScriptTests` 108/108 green; no C++/pipe change).** Closes a real gap
surfaced by external contributor PR #433 (Rixef): a UFunction taking a **by-value `StructProperty` input param** maps to
the helper token `fstruct` (`BakedScriptGenerator.MapInputType`), but `ue5_invoke_helper.lua`'s `writeBakedParams` had no
`fstruct` arm — the token fell through to the `else` and raised *"Unknown param type 'fstruct'"*. Under the `pcall` in
`invokeUFunction` that surfaced as a graceful invoke failure (`showMessage`), so any function taking an undecomposable
struct param could not be fired from a generated baked script.

- **Helper — recursive `writeParams` + `fstruct` arm** ([ue5_invoke_helper.lua](../scripts/ue5_invoke_helper.lua)): split
  the param loop into `writeParams(base, regionSize, params)`; `writeBakedParams` zero-fills the whole buffer once (still
  via `writeByte`, a valid CE Lua single-byte write) then delegates. The `fstruct` arm resolves struct size (explicit
  `p.size` wins → next-member-offset diff → rest-of-region), zeroes the region in one `writeBytes`, and recurses only when
  `value` is a member table (nested fields, offsets relative to the struct base). Guards a hand-edited row missing
  `offset=`, clamps negative sizes, keeps the bilingual comments + the "supported types" error list. Version 1.2 → 1.3.
- **Generator emits `size=`** ([BakedScriptGenerator.cs](../ui/UE5DumpUI/Services/BakedScriptGenerator.cs)): `fstruct` rows
  now carry `size=<bytes>` so the helper zeroes exactly the struct region instead of the fragile next-offset heuristic
  (the param list is declaration-ordered, not sorted by offset). Scalar rows stay minimal (no `size=`).
- **Reworked, not merged:** PR #433 also retyped the zero-fill `writeByte` → `writeBytes({0})` — reverted; `writeByte` is
  valid CE Lua (used across `ue5_freeze_helper.lua` + the other script generators). Landed as a cleaned commit crediting
  the author (`Co-authored-by: Rixef`); the PR is **closed as superseded**, not merged (merging would re-introduce the
  churn + comment/error-list regressions and conflict with `f66e602`).
- **Tests** ([InvokeScriptTests.cs](../ui/UE5DumpUI.Tests/InvokeScriptTests.cs)): the embedded-helper test now asserts the
  `fstruct` arm at v1.3; a new generator test asserts a `StructProperty` input row emits `type='fstruct', size=N`.

## 2026-07-14 — Solide: force-and-hold a discovered field + player stealth-meter zero (build 2168; MERGED main PR #437)

**SHIPPED (full DLL + UI build + 841 native + 2525 C# tests green).** The honest, low-risk subset of the
"enemies can't detect you" evaluation ([project-enemy-undetectable-eval] memory; the locked Non-Goal in
[godmode-spec.md](godmode-spec.md) §3.2/§5.4 — *no universal detection bool; surface per-game via Property
Search*). Two user-facing deliverables over one new DLL module.

- **New DLL module `Solide`** ([Solide.cpp](../dll/src/Solide.cpp)/[.h](../dll/src/Solide.h)) — the
  multi-instance sibling of Hemmung: holds a list of *force-jobs*, each forcing one reflected field to a
  value across **all live instances** of a class via a write-on-drift re-assert worker (`SOLIDE_REASSERT_MS=300`,
  cap `SOLIDE_MAX_INSTANCES=256`, CDO-skipped, re-resolved every tick — no cached pointers). Three kinds:
  **bool** (reuses the public `Solitar::SetActorBool`; new read-only `Solitar::GetActorBool` companion captures
  the restore base), **object-null** (strong `ObjectProperty` only — weak/soft/lazy refused, since 8 zero
  bytes into a weak ptr = valid `GObjects[0]`, the eval's flagged trap), **numeric** (Float/Double/Int/Int64/Byte,
  Hemmung's LWC-width-aware read/write). `RemoveForce`/`ClearAll` best-effort restore the captured base.
  Plus `FindStealthMeter` — resolve the local pawn + owned components (`Aura::GetRelatedObjects`), keyword-score
  numeric fields via the pure header-inline `MatchStealthField` (unit-tested), return ranked candidates.
- **Pipe (Solide, pipe-only MVP)** — 5 commands (`Renge.h` + `Fern.cpp`): `force_field {class_name, field_name,
  kind, value|on}` → `{held, resolved, code}`; `reset_field`; `reset_all_fields`; `get_forced_fields`;
  `find_stealth_meter`. Fern calls `Solide::` directly (no Frieren export); `Solide::StopWorker()` registered
  in `UE5_Shutdown`.
- **UI — Property Search one-click Force** ([PropertySearchViewModel.cs](../ui/UE5DumpUI/ViewModels/PropertySearchViewModel.cs)
  + [PropertySearchPanel.axaml](../ui/UE5DumpUI/Views/PropertySearchPanel.axaml)): a row context submenu
  (Force ON / OFF / → null / value…) forces the field across all live instances of the row's class and holds it,
  with a bottom **"Forced fields (N held)"** honesty strip (per-hold remove + clear-all). Row gates
  `CanForceBool`/`CanForceNull`/`CanForceNumeric` by the reflected type; numeric prompts via the Freeze value
  dialog, object-null via a `ConfirmDialog`. Gated behind the shared `IExperimentalGate`.
- **UI — Teleport "Stealth Meter" card** ([TeleportViewModel.cs](../ui/UE5DumpUI/ViewModels/TeleportViewModel.cs)):
  Detect (auto-find, verifiable readout) → Hold @0 → Reset, tri-state badge. Experimental-gated. Falls back to
  a Property-Search recipe when the auto-find misses.
- **Honest limits (surfaced, not hidden):** `held=0` badge = the class/field matched nothing (no-op signal);
  by-class holds affect *every* instance of that class (N-held shows it); there is no universal detection bool,
  so this only works where the game exposes a reflected stealth/flag/target field. Single-player only.
- **VERIFIED in-game (Elliot / UE Shipping):** `force_field PointLightComponent::InverseExposureBlend → 0`
  resolved 83 instances, worker started (300 ms), re-resolved the pool each tick, `reset_field` stopped it
  clean — no errors, correct lifecycle (DLL `walk` log).
- **Follow-up (UI-only): Teleport quick-jump nav** ([TeleportPanel.axaml.cs](../ui/UE5DumpUI/Views/TeleportPanel.axaml.cs)) —
  right-click anywhere in the Teleport tab → a menu of the currently-visible cards; clicking one scrolls it to
  the top (via `ScrollViewer.Offset`, since `ContentRoot` swallows `RequestBringIntoView`). Built dynamically
  from the visible `Border` cards (label = each card's SemiBold header), so hidden cards are auto-excluded;
  the whole menu is **experimental-gated** (the card list would otherwise leak experimental card names).

## 2026-07-13 — Auto Snapshot (periodic capture) + free-disk-space guard (build 2166; UI-only, no DLL/pipe change)

**SHIPPED (UI-only; full UI build + 2509 C# tests + C++ self-tests green).** Two additions to the
experimental Snapshot tab — an unattended periodic-capture loop and a hard disk-full safety net. No
DLL, pipe-protocol, or thread-priority change.

- **Free-disk-space guard** ([SnapshotDiskGuard.cs](../ui/UE5DumpUI/Services/SnapshotDiskGuard.cs)):
  before ANY capture (manual + auto), the drive holding the snapshot DB must have at least
  `min(percent% of drive, GB)` free, else the write is refused. Defaults **10% / 50 GB** (whichever is
  smaller — 1 TB → 50 GB, 200 GB → 20 GB). A **0** on either term disables that term (percent 0 → GB
  floor alone), both 0 → guard off. Enforced pre-capture (primary) and mid-capture (reuses the
  max-dataset-cap graceful-stop path — KEEPS the partial, since deleting on a near-full disk is unsafe).
  New `IPlatformService.GetFree/GetTotalDiskSpaceBytes` (default sentinels: free `long.MaxValue`, total
  0 → never blocks on a measurement it can't take) via `System.IO.DriveInfo` in `WindowsPlatformService`.
- **Auto Snapshot loop** ([SnapshotViewModel.cs](../ui/UE5DumpUI/ViewModels/SnapshotViewModel.cs) +
  [AutoSnapshotPlanner.cs](../ui/UE5DumpUI/Services/AutoSnapshotPlanner.cs)): a **manual Start/Stop toggle,
  session-only** (never auto-starts on connect / auto-resumes on launch; only the settings persist).
  Gap after each capture = `max(interval − captureDuration, 60 s)` — one formula that auto-extends the
  effective interval past a long capture AND guarantees the ≥60 s idle breather. **The idle gap is the
  only game-impact lever — no thread-priority drop** (that Phase-0 experiment was reverted for starving
  scans ~20× when the game is busy; see multipipe-eval §8.2). Retention: **KeepRecent N** (roll forever,
  new `ISnapshotStore.EnforceCountAsync` count-FIFO) or **FixedCount N** (stop after N). **Auto-adjust
  quota** (default off): off → bound by quota, stop if it can only hold one snapshot; on → bump the
  quota up a preset (…→5 GB→Unlimited) to fit. Cancel during auto stops the whole loop; capture survives
  tab-switch, stopped on window close.
- The capture engine was extracted into `CaptureCoreAsync(bool isAuto, CancellationToken)` returning a
  `CaptureOutcome`, shared by the manual button and the loop; the loop links each capture's CTS to the
  auto token so Cancel/Stop abort it. Intricate pacing/retention/quota-grow rules live in the pure
  `AutoSnapshotPlanner` (unit-tested without timers). New settings persist via `SnapshotUiOptions`.
- Tests: `SnapshotDiskGuardTests`, `AutoSnapshotPlannerTests`, `SnapshotStoreTests.EnforceCount_*`, and
  VM guard-block/allow tests (configurable `DiskStubPlatformService`).

## 2026-07-13 — Timer control Phase E: Linie cadence flag — periodic timer-callback discovery (build 2156; dev, DLL needs re-inject)

**SHIPPED (DLL + UI; full build + 2479 C# tests + C++ self-tests green).** The behavioural complement to the
static `Timing` discovery (memory `project-timer-feature-eval`, layer E): make the Live Funcs profiler flag
*which UFunction* fires at a regular **timer cadence** — the callback that actually drives a cooldown / respawn
/ damage-over-tick, which name-scoring can't find.

- **DLL ([Linie](../dll/src/Linie.cpp)):** `Stat` gains a Welford running mean/variance of the wall-clock
  **inter-arrival gaps** between a function's fires; `RecordCall(ufunc, nowMs)` is fed the timestamp
  [Stark.cpp:143](../dll/src/Stark.cpp) *already* stamps for the responsiveness check — **zero extra clock
  reads on the PE hot path**. `Snapshot` emits `meanPeriodMs` + `cv` (stddev/mean = regularity) + `gapSamples`;
  `pe_profile_get` ships them per row.
- **UI:** `PeProfileEntry` gains `MeanPeriodMs`/`Cv`/`GapSamples` + an `IsPeriodic` classifier (≥3 gaps, CV≤0.25,
  period outside the per-frame Tick band ~5-40 ms and within a plausible ~50 ms-30 s gameplay-timer window) →
  a **"Timer"** Kind badge + a **Period** column; a **"Periodic only"** filter in `LiveFuncsViewModel`. The
  workflow is the *inverse* of the shipped action-diff: record while **IDLE** ~15-20 s, then Periodic-only
  leaves the steady callbacks.
- **Honest limit (stated in the tooltip):** native C++/lambda timers (`SetTimer(this, &UClass::Method)`) call
  the delegate directly and **bypass ProcessEvent** — only UFUNCTION-bound (BP timer event /
  `SetTimerByFunctionName`) timers are visible. +12 tests (classification + VM filter). A cadence diagnostic
  (build 2158) appends the periodic candidates to `pe_profile_get`'s log line so an idle-window recording is
  verifiable from the log, not just the UI. **LIVE-VERIFIED on The Adventures of Elliot (UE4.27):** an
  idle-window recording flagged **3 periodic funcs out of ~90** (the rest per-frame Tick, correctly excluded)
  — `BP_SupportFairy_C::TryAttackEnable` + its `ExecuteUbergraph` at the same **~325 ms** cadence (cv 0.02) =
  a real ~3 Hz BP timer, plus `EQC_PlayerContext_C::ProvideSingleActor ~108 ms` (cv 0.08); stable across two
  windows (~10 s → 8-27 gaps, ~20 s → 27-83 gaps). No false positives, no tuning needed. *Extends
  Linie/LivePEProfiler (build 2109); completes the timer eval's layer E.*

-----

## 2026-07-13 — Time/Timer control L1: Hemmung module + Timing discovery category (build 2148; dev, DLL needs re-inject)

**SHIPPED (DLL + UI; builds + all 2453 C# tests + C++ self-tests green).** First slice of the layered
Time/Timer feature (eval: memory `project-timer-feature-eval`, todo.md "Time / Timer control"). Two halves:

- **Timing discovery category (UI, no DLL/pipe change).** New `PropertyCategory.Timing` in
  [PropertyScoringTable.cs](../ui/UE5DumpUI/Services/PropertyScoringTable.cs) — always-on `TimingKeywords`
  (Cooldown/CoolDown/Countdown/Delay/Interval/Timer/Elapsed/Remaining/LifeSpan/Lifetime/Recharge/TickRate/
  TimeDilation/Time…), a `TimeStructTypes` set (Timespan/DateTime/QualifiedFrameTime/FrameTime/Timecode)
  mirroring the GAS-struct branch (default-to-Timing when the name misses a keyword), timer terms added to
  `SeedQueries[]` (so round-1 `search_properties` actually fetches them), and a teal chip in the Interesting
  Properties tab. The Timing check is appended **last** so an ambiguous name that also hits an earlier bucket
  keeps it (locked `BuffDuration → Combat` test still passes). [ClassLocationScorer.cs](../ui/UE5DumpUI/Services/ClassLocationScorer.cs)
  gained GameplayEffect/GameplayAbility/WorldSettings +2. Function side: [KeywordScoringTable.cs](../ui/UE5DumpUI/Services/KeywordScoringTable.cs)
  `UtilityKeywords` widened with Cooldown/Dilation/Delay/Interval/Elapsed/Recharge (catches cooldown/dilation
  getters that scored 0 before). One sentinel test renamed (`ComponentTickInterval` → `MeshSectionIndex`,
  now that `Interval` is a keyword) + new Timing/time-struct/class-rule tests.
- **Hemmung module (DLL) — global slow-mo / freeze-time / speed-up.** New Frieren module
  [Hemmung.cpp/.h](../dll/src/Hemmung.cpp) (ヘムング "inhibition"): the **absolute-value sibling of Laufen** —
  holds reflected dilation floats at a pinned value via a write-on-drift re-assert worker (same s_mutex/
  s_workerMutex discipline, LWC-width-aware read/write, owner-change re-capture). Two levers: `DIL_GLOBAL`
  (`AWorldSettings::TimeDilation`, whole-world; resolved via GWorld→PersistentLevel→WorldSettings reflected
  chain + `Aura::FindInstancesByClass("WorldSettings")` fallback) and `DIL_PAWN` (local pawn
  `AActor::CustomTimeDilation`, reusing Laufen/Solitar's pawn chain). Exports `UE5_SetTimeDilation(target,
  value)` / `UE5_ResetTimeDilation(target)`; **Mimic `CMD_TIME=15`** mailbox (`TimeOp` SET/RESET) for CE
  Lua/.CT; pipe `set_time_dilation` / `reset_time_dilation` / `get_time_state` (each surfaces
  owner_addr/field_offset for the Locate-in-GWorld handoff). `Hemmung::StopWorker()` joined on shutdown;
  roster flipped 🟢. Value clamped DLL-side to [0.0, 100.0]. Direct write bypasses SetGlobalTimeDilation's
  clamp; a paused world can't be stepped and an active Sequencer track flickers ≤1 tick (documented).

**Part C UI Time card — SHIPPED (build 2149).** A **"Time Dilation" card** in the Teleport panel
([TeleportPanel.axaml](../ui/UE5DumpUI/Views/TeleportPanel.axaml)) beside Move Speed/Gravity: a "Player only"
toggle (whole-world `TimeDilation` vs per-pawn `CustomTimeDilation`), a linear 0–3× slider + % readout,
preset buttons (Freeze / ¼× / ½× / 1× / 2×), Apply/Reset/↻, and an ON/OFF/Unavailable badge + live "Current:
X× (held; natural Y×)" readout. Wired via new `IDumpService`/`DumpService`
`GetTimeStateAsync`/`SetTimeDilationAsync`/`ResetTimeDilationAsync` (models `TimeDilationKnob`/
`TimeDilationSetResult`/`TimeState`) and `TeleportViewModel` commands; en.axaml strings; 6 new VM tests
(2459 C# green). **CE Lua/.CT generation — SHIPPED (build 2150).**
[TimeDilationScriptGenerator.cs](../ui/UE5DumpUI/Services/TimeDilationScriptGenerator.cs) mirrors
`MovementScriptGenerator`: stateful `[ENABLE]`/`[DISABLE]` records poking the `CMD_TIME=15` mailbox — op `SET`
(baked value) on tick, op `RESET` on untick (instanceAddr=op, ufuncAddr=target 0 global/1 pawn, paramsData
double value); `CeMailboxLayout.CmdTime`+`TimeOpSet/Reset` added; `CeLuaHygiene`-compliant (DEBUG-gated `dbg`,
errors surface, auto-close on clean success). Wired into the Teleport panel's "Add to CE" (AOBMaker push, 2
records) + "Save .CT" batch (`BuildBatchRows` → World + Player rows); 6 new generator tests (2465 C# green).
**Persistence — SHIPPED (build 2151).** Two layers: (1) **live read-back** — `TeleportViewModel.SetConnected`
now reflects whatever dilation the DLL is already holding on connect (and on target-switch), syncing the
slider to the engaged value — the same "state lives in the DLL, survives a UI reconnect while the game lives"
model as teleport markers (`RefreshHeldTimeStateAsync`); disconnect resets the badge. (2) **disk preference** —
`TeleportUiOptions.TimeDilation`/`TimeTargetIsPawn` (in the global `ui-options.json`, mapped in
`MainWindowViewModel` Apply/Capture) pre-fill the card's last-used value+target across UI restarts (NOT
auto-applied; the live read-back wins when a dilation is held). +2 VM tests + options round-trip/defaults
coverage (2467 C# green). **L1 COMPLETE + LIVE-VERIFIED on The Adventures of Elliot (UE4.27, build 2151).**
Log (`Elliot-Win64-Shipping`): `set_time_dilation target=pawn value=0.5` → `Time: target 1
('CustomTimeDilation') base=1.0000 -> hold 0.5000 (rc=0)` → re-assert worker started; held at 0.5/1.0/2.0/1.4688×
and reset cleanly (`Time: reset target 1 (any active left=0)`, worker stopped); `get_time_state` polled on
connect (the read-back). `rc=0` throughout — the per-pawn `CustomTimeDilation` lever resolved + held. (Global
`WorldSettings::TimeDilation` lever wired + unit-covered, not exercised in this session's log.) **REMAINING
(deferred by design):** a dedicated opt-in function-side `FunctionCategory.Timing` bucket; L2 (GAS cooldowns) /
L3 (live FTimerManager). **NEEDS in-game verify** (Hemmung has no
unit test — reflection needs a live game): attach, drag the slider / hit ½×, Apply → world runs at half speed
and holds against slow-mo; Reset restores.

-----

## 2026-07-11 — LKG Phase 2: DLL-attributed confirmed-working proxy (build 2142; dev, needs re-inject)

**SHIPPED (DLL + UI; re-inject required).** Upgrades the Proxy Deploy suggestion from "you deployed /
injected this before" to **"a proxy actually LOADED this game and it stayed running"** — the strongest
known-good signal.

- **DLL self-reports the load path** ([Fern.cpp](../dll/src/Fern.cpp) init response): `load_mode` =
  `proxy:version.dll` | `proxy:dinput8.dll` | `proxy:dxgi.dll` | `injected` | `loaded:<name>` | `unknown`,
  computed from **`GetModuleFileNameW(g_hDllModule)`** — THIS module's own file name. This is the ONLY
  correct proxy attribution: with two proxies deployed, `Heiter.cpp`'s mutex makes the loser a passive
  forwarder that returns before init and never reports, so the served `load_mode` is always the WINNER;
  module-list enumeration (the naive approach) mis-attributes because all proxies share the PE ProductName.
- **UI stability gate** ([MainWindowViewModel](../ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs)
  `ScheduleProxyConfirmation`): on connect, if `EngineState.LoadMode` is a proxy, wait a 20 s dwell and —
  **only if still connected** — record it via `RecordConfirmedProxy`. The dwell guards a proxy that loads +
  connects then crashes the game seconds into play (connect alone isn't proof the game keeps running).
- **Key = game .exe name** (`EngineState.ModuleName`), NOT peHash — survives reinstall/patch; unified with
  Phase 1's injection/enrichment exe-name resolution. Stored in `ProxyDeployUiOptions.ConfirmedProxyByExe`.
- **Suggestion priority**: confirmed-working ("dxgi.dll · confirmed working") > deployed ("· last used") >
  injection ("injection · no proxy deployed") > version default. Progressive: deploy shows "last used" →
  launch + survive the dwell → upgrades to "confirmed working".
- Race-safe: enrichment reads snapshots; all map mutations marshal to the UI thread. Backward-compatible
  (older DLLs emit no `load_mode` → gate is inert). 2436 tests pass; DLL + 3 proxies + trimmed UI green.

-----

## 2026-07-11 — Proxy Deploy per-game suggestion (LKG Phase 1): import-table + remembered pick + injection known-good (builds 2134-2140; dev)

**SHIPPED (UI-only, no DLL/pipe change).** First slice of the "Last-Known-Good proxy" idea: the Proxy
Deploy grid gains a per-game **Suggested proxy** column so the user doesn't have to re-guess which of
version/dinput8/dxgi to deploy after an uninstall→reinstall wipes the folder. Advisory only — it never
changes the global proxy radio and never auto-deploys.

**Why this shape (evaluation first).** A multi-agent evaluation of the full peHash-keyed "record a
confirmed proxy load" design surfaced three verified blockers: (1) the confirmed-success moment (pipe
connect, `ApplyEngineState`) has the game peHash but **no proxy-type** — attribution lives only in the
process-module list, and with two proxies deployed the mutex passive-forwarder (`Heiter.cpp` +
identical `version.rc` `ProductName`/`OriginalFilename`) makes module-enumeration record the *wrong*
proxy; (2) **peHash = TimeDateStamp+SizeOfImage changes on every game patch**, so a reinstall (which
pulls the latest build) orphans the entry — the wrong key for a "survives reinstall" feature; (3)
recording success at connect can log a load-then-crash proxy as "good". Phase 1 therefore avoids the
DLL change and history entirely.

**What Phase 1 does instead:**
- **[ProxyImportAnalyzer](../ui/UE5DumpUI/Services/ProxyImportAnalyzer.cs)** — pure, offline PE
  import-table parser (DOS→NT→section→RVA→file-offset; standard + delay-load dirs). Reports which of
  {version,dinput8,dxgi}.dll the .exe imports. **Honesty guard:** import parsing reports *viability*
  (which proxies can even load), NOT the recommendation — `version.dll` loads *dynamically* so its
  absence from the static import table means nothing, and we deliberately never auto-escalate to dxgi
  (that pattern matches nearly every D3D game and would push dxgi into the Octopath-秒退 trap).
- **Suggestion** = the proxy the user **last deployed for that game** (remembered by folder name, so it
  survives reinstall/patch — the honest mini-LKG) ?? **injection known-good** ?? `version` (safe default).
  Import viability is folded into the column text as advisory context ("version · default · alt: dxgi").
- **Injection as a first-class known-good** (build 2140): injection is a UI-initiated action, so — unlike a
  plain-Connect proxy load — it is reliably knowable in Phase 1. `RememberInjection` records the game .exe
  name whenever the user successfully injects (fresh inject, or an already-loaded process the module list
  attributes to injection). A game that was injected but never had a proxy deployed shows **"injection ·
  no proxy deployed"** — its known-good load method (and often the ONLY option for EA/launcher games that
  strip the exe's DLL search dir). Keyed by .exe name (inject flow) vs proxy pick by folder name (deploy
  flow); both resolve against the same `DetectedGame`, so no key unification needed. Persisted in
  `ProxyDeployUiOptions.InjectedGameExes`.
- **Opt-in** `LkgSuggestEnabled` (default ON) in `ProxyDeployUiOptions`; remembered picks persist in
  `LastManualProxyByGame` (game-name → ProxyType) in `ui-options.json` (UI-owned, never the DLL-shared
  peHash json). Enrichment runs once per scan after `RefreshDeployStatusAsync`; the column is a plain
  `DataGridTextColumn` (Binding==SortMemberPath, the only compiled-binding-sortable pattern in this grid).

**Not done (Phase 2, deferred):** the real DLL-attributed success-history LKG — needs the DLL to
self-report `GetModuleFileNameW(g_hDllModule)` (the winning proxy's own filename) at init, a stable
install-identity key (appid/name, not peHash), and a stability gate before recording. Known Phase 1
caveat: import-table can't predict EA-launcher games that strip the exe's DLL search dir (no proxy
loads at all). 15 new tests (synthetic PE incl. non-identity RVA mapping + real-PE smoke + injection
known-good + service glue); full suite 2434 pass / 0 fail; trimmed self-contained UI publish green.

-----

## 2026-07-11 — Live Funcs profiler refinements: baseline diff, hide-widgets, hide-events, call-order (builds 2110-2130; dev)

Iterative in-game hardening of the Live PE profiler ([Linie](../dll/src/Linie.cpp)) driven by a
DQ7R (UE4.27 Shipping) open-shop hunt. Each round cut noise so the action's true entry point surfaces
— all **general / game-agnostic** (no per-game heuristics):

- **Baseline diff** (client-side): Set Baseline on an idle recording, then the action recording shows a
  Δ column + ranks NEW / increased rows first (keyed by `ClassName::FuncName`, stable across GC).
  "New/changed only" hides unchanged Tick noise.
- **Hide UI widgets**: `pe_profile_get` walks each function's owning-class super-chain
  (`Aura::ClassDerivesFromAny{UserWidget,Widget}`) → `is_widget`. Opening a shop CREATES its widget, so
  all the widget's own methods fire at once and flood the diff — hiding them leaves the persistent opener.
- **Hide events/delegates + Type badge**: `pe_profile_get` returns `function_flags`; the UI tags each row
  Event / Deleg / Call / native (`FUNC_Event`/`FUNC_Delegate`/`FUNC_BlueprintCallable`) and can hide the
  On*/callback reactions, leaving imperative callables.
- **Call order (first-fired)**: Linie now records each function's first-fire position in the call stream
  (`first_seq`); the UI adds an "Order" column + "Earliest first" sort. An action's entry point runs
  BEFORE the reactions it triggers, so this is the causal, name-independent signal that floats the true
  opener to the top of the NEW set.

**Finding of record:** on DQ7R the shop opens via a **native C++ call** the ProcessEvent hook can't see —
only its BP event callbacks (On*) are visible. That's the fundamental limit of PE-based discovery, not a
tool gap; the profiler correctly surfaces it (hide-events empties the callable list). For most UE games
(reflected/Blueprint gameplay) the profiler + these filters find the action's function directly. Tests
across the four rounds: +25 (`LiveFuncsViewModelTests`). Suite 2419 pass / 0 fail; all 3 proxies + AOT clean.

-----

## 2026-07-11 — Live ProcessEvent Call Profiler (Linie) — behaviour-based UFunction discovery (build 2109; dev)

**SHIPPED (DLL + UI). New "Live Funcs" tab.** The root-cause answer to "which function does this game
call to open the shop / dash?" — the thing name heuristics (Interesting Functions) fundamentally can't
find. Workflow: **Start → ALT-TAB to the game → perform ONE action (open shop, dash, attack) → Stop →
see which UFunctions fired, ranked by call count.** The action-specific function is near the top with a
low count (a handful of calls); per-frame Tick/Update noise has huge counts.

**New Frieren module `Linie`** (莉涅 — "reads opponent mana"; roster use = *Analysis / profiling*).
`Stark`'s ProcessEvent hook keeps the single hook and calls into `Linie` with one inlined branch:
- **Hot path stays free when off.** `Linie::IsRecording()` is one relaxed `atomic<bool>` load + a
  predicted-not-taken branch ([Stark.cpp:143](dll/src/Stark.cpp)); the mutex + `unordered_map<UFunction*,
  count>` are touched ONLY inside a Start/Stop window. Mirrors the existing `s_queueDepth` "skip work
  unless armed" gate. Multi-threaded-PE safe (map guarded by a dedicated mutex, never Stark's queue mutex).
- **Hook-install prerequisite.** The PE hook installs lazily on the first invoke; `pe_profile_start`
  calls the new `UE5_EnsureGameThreadHook()` (reuses the audit-#3 `call_once`) so recording works without
  first issuing an invoke. `hook_active:false` ⇒ vtable detection failed → counts stay 0 (UI warns).
- **Query-time resolution.** Raw `UFunction*` are stored during recording; `pe_profile_get` snapshots →
  sorts by count desc → caps → resolves only the capped set via the new `Ubel::ResolveFunctionInfo`
  (factored out of `WalkFunctions`' version-aware flags/params probe), with a `"Function"` meta-class
  guard so a pointer whose slot was recycled by a GC/level-load is dropped, not deref'd. Cooperative
  `Tot::Requested()` abort. `Linie::Reset()` on client disconnect.

3 pipe commands (`pe_profile_start/stop/get`, [Renge.h](dll/src/Renge.h)). UI: `LiveFuncsViewModel` +
`LiveFuncsPanel` (Start/Stop/Refresh/Clear + ranked DataGrid + space=AND keyword filter with LRU memory
per the CLAUDE.md rule + "Live"/"Name" row handoffs to Live Walker/clipboard). New "Live Funcs" tab
inserted after Dump Explorer (MainTabIndex shifted); leaving the tab auto-stops any live recording.

**Tests:** `LiveFuncsViewModelTests` (14 — Start hook/no-hook, Stop fetch+rank+empty, filter space=AND,
Clear, handoffs, auto-stop-on-leave, model). Full suite 2402 pass / 0 fail; all 3 proxy DLLs + AOT
publish clean. **DLL-side PE counting needs in-game verification** (Fern/DLL has no unit tests) — the
shop/dash acceptance test on a live UE title.

-----

## 2026-07-11 — Interesting Functions: opt-in "Gameplay Actions" keyword pack (build 2103; dev)

**SHIPPED (UI-only, opt-in default OFF).** The Interesting Functions scorer was tuned for cheat-value
targets (Stats/Inventory nouns + Movement/Combat cheat verbs). Character-control + interaction + shop verbs
the user actually wants to *call* — `Dash`/`Dodge`/`Roll`/`Slide`/`Interact`/`Use`/`Open`/`Buy`/`Sell`/
`Shop`/`Vendor`/`Merchant`/`Trade`/`Purchase` — were **absent from every keyword bucket**, so a plain
`OpenShop()` / `Dash()` / `Interact()` scored 0 and stayed below the threshold-5 cutoff (invisible unless
"Show All").

Added a new opt-in `FunctionCategory.GameplayAction` keyword pack (weight 5, same as Stats/Movement):

- **`KeywordScoringTable.Score(entry, includeGameplayActions = false)`** — new default-`false` param. When
  off, scoring is **byte-identical** to before (regression-guarded by a test), so the pack is purely additive.
  Only **NEW** tokens live in the pack — verbs already in Movement (`Jump/Move/Walk/Sprint`) or Combat
  (`Attack/Fire`) are deliberately NOT duplicated (the final score sums every bucket; a dup would double-count).
- **Whole-token match** (`KeywordTokenizer`, per the CLAUDE.md rule) → single tokens: `OpenShop` →
  `["open","shop"]` hits both `Open` and `Shop` (score 10). Noisier common words (`Use`/`Open`/`Store`) are
  included anyway — opt-in trades precision for recall.
- **Tie priority lowest**: a verb that also hits a built-in bucket keeps that label (e.g. `SellItem`: Sell↔Item
  tie → stays Inventory) but still gains the extra points so it surfaces.
- **UI**: a "Gameplay Actions" checkbox next to "Show All" (green `#5FBF7F`). Toggling **re-scores the loaded
  set in place** (`RescoreAsync` on a worker thread — no pipe re-fetch, class-noise histogram untouched) via
  the shared `ScoreEntries` helper. New `GameplayAction` chip in the category dropdown + green "Gameplay" label.
- **"BP/Exec only" filter** (`CallableOnly`, default off, blue `#7FB6E8`) — a pure *display* filter (in
  `ApplyFilter`, no re-score) that keeps only rows flagged `BlueprintCallable` or `Exec`. Gameplay/control/shop
  entry points are almost always one of these two (both survive cooking), so it hides native getter/setter/
  plumbing noise. Pairs with Gameplay Actions + Show All to browse callable action functions.
  `ScoredFunctionRow` forwards `IsBlueprintCallable`/`IsExec`. Added to `ClearFilters` (it's a view filter);
  `GameplayActions` deliberately is NOT (it's a scoring mode with a re-score cost).

Recipe added to [tips.md](tips.md) ("Finding character-control / shop functions"). **No DLL/pipe change** — all
client-side C# (the DLL already returns raw `function_flags`; scoring has always been UI-side).

**Tests:** `KeywordScoringTableTests` (+6: off-path zero-contribution, default==off regression, on-path
categorisation incl. the Sell↔Item tie, bare-OpenShop clears threshold, Jump no-double-count, DisplayName/Color
for the new enum) + `InterestingFunctionsViewModelTests` (+4: pack toggle re-scores & surfaces OpenShop, defaults
off, BP/Exec-only hides native rows / keeps Exec rows, ClearFilters resets it). Full suite 2388 pass / 0 fail;
AOT publish clean.

-----

## 2026-07-11 — Object Tree per-instance drill-downs: Open in Live Walker / Show Related / Locate in GWorld+GameEngine (build 2098; dev)

**SHIPPED (UI-only). Phase 3 (final) of "global instance explorer".** A global instance keyword search is only useful
if you can act on a specific hit. The Object Tree row context menu previously offered only class-oriented actions (Copy
Type/Name/Address, Find Instances by Type / Type+Name) — nothing to drill THIS exact object. Added four per-instance
handoffs mirroring the InstanceFinder row actions:

- **Open in Live Walker** (`NavigateToLiveWalker` → `LiveWalker.NavigateToAddressCommand`) — walk this object's live fields.
- **Show Related Objects** (`NavigateToRelatedObjects` → `RelatedObjects.LoadForAddressAsync`) — its class/outer/
  Controller↔Pawn/components/ASC/AttributeSet graph.
- **Locate in GWorld** / **Locate in GameEngine** (`LocateInGWorld`/`LocateInGameEngine` →
  `LiveWalker.LocateInGWorldAsync`/`LocateInGameEngineAsync`, `stopAtParent: false` so it lands ON the picked object) —
  shortest pointer chain from the world / engine root. Meaningful for instances; a class row is usually not reachable
  (Live Walker reports that) — kept ungated for simplicity since the "Instances only" toggle already narrows to instances.

Wired in `MainWindowViewModel` with the exact try/catch + tab-switch shape as the InstanceFinder handoffs (reusing the
shared `OpenRelatedAsync`). Each command no-ops on a null node / empty address so a bad row never navigates to a dead
target. UI-only — no DLL/pipe change; the handoffs re-resolve the live address downstream.

**Tests:** `ObjectTreeViewModelNavigationTests` (6 — each command raises its event with the row address; null-node and
empty-address no-op). C# suite 2369 green; UI AOT publish + compiled bindings pass. Completes the three-phase global
instance explorer (Phase 1 instances-only toggle, Phase 2 server-side Search, Phase 3 drill-downs).

-----

## 2026-07-11 — Object Tree top Search upgraded: server-side space=AND over name+class + instances_only + honest truncation (build 2096; dev)

**SHIPPED (DLL + pipe + UI). Phase 2 of "global instance explorer".** The Object Tree top Search box was a silent
trap: `search_objects` → `Aura::SearchByName` matched the **object name only** (single substring), early-exited at the
client's 2000 cap, and Fern reported `total = results.size()` — so the user got ≤2000 rows with **no signal that more
existed**. This makes the top Search a true global instance keyword search, consistent with the bottom filter.

**DLL.** `Aura::SearchByName(query, maxResults, instancesOnly)`:
- **space = AND over object name OR class name.** `query` is whitespace-tokenized (new header-inline
  `Aura::SplitLowerKeywords`); every term must hit the object name OR the class name (`Aura::MatchesAllKeywords`,
  term-level AND / field-level OR) — the server-side twin of the client `ObjectTreeFilter`. So "Pawn" now matches by
  class, and "BP_ Enemy" ANDs.
- **`instances_only` gate** via new header-inline `Aura::IsReflectionMetaClass` — mirrors the C#
  `ReflectionMetaClassifier` (full reflection/type layer, class family + Function/ScriptStruct/Enum/Package + UE4
  `…Property` suffix), so the top Search honours the "Instances only" toggle server-side.
- **Honest truncation:** `SearchResultSet::truncated` is set when the cap is hit (same test as `FindInstancesByClass`'s
  cheap path); Fern emits `data["truncated"]`.
- All three helpers are header-inline in `Aura.h` (like `IsEnginePackage`) and unit-tested in `dll_helpers_test`.

**UI.** `SearchObjectsAsync(query, limit, instancesOnly, ct)` + `ObjectListResult.Truncated`; the VM's `SearchAsync`
forwards `InstancesOnly` and the new `Constants.ObjectTreeSearchCap = 5000` (= `ObjectTreeMaxDisplay`, so every
returned row can display — replaces the old 2000 page size), and on a hit cap the status reads
"Found N+ results (capped — narrow the search, or Reload + filter for all)" instead of silently truncating. Tooltips
updated.

**Whole-pool honesty.** The top Search still caps (returning the entire 486K-object pool over the pipe is not viable),
but it is now **explicit, class-aware, space=AND, instances-only-aware, and reports truncation** — and the tooltip
points at the uncapped path (Reload loads the whole pool into `_allNodes`; the bottom filter scans it with no cap on
match-counting).

**Tests:** `dll_helpers_test` +32 (`Test_IsReflectionMetaClass` full-layer parity + `Test_KeywordMatch` tokenize/AND;
829 pass) + 3 VM tests (`SearchAsync_SendsInstancesOnlyAndSearchCap_ToServer`, truncated→"capped" status, plain-count
status). C# suite 2363 green; full DLL+UI build clean. **DLL scan itself not live-verified** (needs a UE process +
re-inject); header-logic + wiring covered by tests.

-----

## 2026-07-11 — Object Tree "Instances only" toggle: global live-instance keyword search by hiding the reflection layer (build 2092; dev)

**SHIPPED (UI-only, no DLL/pipe change).** Turns the existing Object Tree into a **global live-instance keyword
explorer** — the instance analog the user wanted to mirror the offline metadata Dump Explorer. A multi-agent
feasibility eval established the surprising finding that a whole-pool, class-agnostic, `space = AND` keyword browse
over live instances *already existed*: the Object Tree paginates the entire GObjects pool into `_allNodes` and its
bottom filter runs `ObjectTreeFilter.MatchesAllTerms` over the **whole cache**, not the page. The single genuinely
missing capability was **"show live instances only, not reflection metadata"** — a client-side predicate, not a new
subsystem. So Phase 1 is one toggle.

**What shipped.** New `Helpers/ReflectionMetaClassifier` (`IsReflectionMeta` / `IsLiveInstanceRow`) + an "Instances
only (hide reflection metadata)" checkbox on the Object Tree filter row (`InstancesOnly` observable → `ApplyFilter`).
When on, `ApplyFilter` drops rows whose `ClassName` is in the **full reflection/type layer** before the text/class
filter runs.

**The load-bearing correctness detail** (an adversarial review of the design caught this): the noise is NOT just
class-like metas. Excluding only `Class`/`BlueprintGeneratedClass`/… (what `DumpAllService.IsClassLikeMetaName` /
`Aura::IsClassLikeMeta` cover) would leave every `UFunction`, `UScriptStruct`, `UPackage` and `UEnum` in the result —
failing at the toggle's headline job. So the classifier excludes the whole set: class family + `Function`/
`DelegateFunction`/`SparseDelegateFunction` + `ScriptStruct`/`UserDefinedStruct` + `Enum`/`UserDefinedEnum` +
`Package`, plus a `EndsWith("Property")` suffix rule for UE4's `FooProperty` UObject descriptors (a priority target;
UE5 makes FProperty non-UObject so the suffix never fires there). CDOs/archetypes report their game class as
`ClassName`, so they survive by design; null/empty `ClassName` is treated as an instance (never silently hidden).

**Whole-pool guarantee (the explicit user concern).** The filter runs inside the `foreach (var node in _allNodes)`
loop — the `ObjectTreeMaxDisplay = 5000` cap limits only *displayed* rows, while `matchCount` counts every match. Test
`ObjectTreeViewModelFilterTests.InstancesOnly_FiltersWholePool_BeyondDisplayCap` loads 8000 nodes (2000 metas THEN
6000 instances) and asserts the reported match count is the full **6000** — it would fail if the scan were ever
limited to a page or the display cap, locking the guarantee in as a regression guard.

**Tests:** `ReflectionMetaClassifierTests` (36 cases — full type layer excluded, UE4 property family excluded via
suffix, instances kept, CDO/null contract) + `ObjectTreeViewModelFilterTests` (whole-pool span, meta-hiding,
`space = AND` composition with the text filter). Suite 2360 green. **Not live-verified in a game** (needs a UE
process); logic covered by tests + the Avalonia compiled-binding build. Phase 2 (server-side pool-load-free global
keyword command) and Phase 3 (per-hit drill to Live Walker / Locate) remain optional follow-ups.

-----

## 2026-07-10 — Keyword-search UX unification (space=AND + per-keyword memory) + `get_object_list` `full_path` restores GameOnly pre-walk skip + IsEnginePath format fix (build 2088; dev)

**SHIPPED.** Three changes; the last two are one feature + a serious pre-existing bug it exposed.

**(1) Every keyword/filter box now uses `space = AND` (learned from Object Tree).** The Object Tree text filter
already split on whitespace and required EVERY term to hit a field (`ObjectTreeFilter.SplitTerms` /
`MatchesAllTerms`). That helper grew a `params string?[]` multi-field overload (term-level AND, field-level OR,
null-safe), and **every** client-side keyword filter was migrated off single-substring `.Contains`/`IndexOf` onto it:
Console, Detect Stats, Interesting Properties, Interesting Functions, Game Class Filter, Class Struct, Class Pivot
(class-picker + results), Snapshot Diff (global + class/field/object), SPC result (global + class/field/object),
Live Walker (field-highlight search + Functions filter). So `add money` now matches a row containing both terms in
any order, in any field. Already-AND boxes (Instance Finder temp filter, Dump Explorer, Object Tree) were left as-is.
Server-side filters (Value Search, SPC query-time Class/Prop) can't AND client-side and were skipped. Every panel's
combined facets (Category / ShowAll / GameOnly / class-noise exclusion / numeric ranges / direction / threshold /
selection-detach / SequenceEqual no-op guard) were preserved; the Live Walker field search stays a **highlighter**
(sets `IsSearchMatch`, never removes rows); the Class Pivot picker stays a `Text`-only AutoCompleteBox (never
`SelectedItem`) so the historical `SelectedClass` oscillation can't return.

**(2) Every keyword box now REMEMBERS what you typed (learned from Live Walker).** New reusable
`Helpers/KeywordSearchMemory` packages the Live Walker pattern — an LRU `ObservableCollection<string>` of settled
keywords that yielded matches (700 ms debounce, `SearchKeywordHistory.Remember`, longest-valid-wins), bound to an
`AutoCompleteBox.ItemsSource`. 14 plain filter TextBoxes became AutoCompleteBoxes with per-box memory (`FilterHistory`
etc.). The Snapshot/SPC **class/field/object** pickers keep their existing DATA-derived distinct-value suggestions
(not regressed); only their free-text **global** box got typed-keyword memory. Value Search (server-side, async
filter) uses `KeywordSearchMemory.Commit()` from the reload-completion path instead of a timer probe, so the remember
never races the pipe round-trip and reads a stale count. Disposed in the three `IDisposable` VMs; self-limiting
elsewhere.

**(3) `get_object_list` emits `full_path` (gated) → GameOnly pre-walk skip restored — AND the pre-existing
IsEnginePath format bug fixed.** The DLL `get_object_list` (Fern.cpp) now emits `full_path` per object **only when the
request carries `include_path=true`** (kept off the hot Object Tree paginate — a path string per object is ~19 MB over
486K objects; not interned). `DumpService.GetObjectListAsync(..., bool includePath=false)` parses it into
`UObjectNode.FullPath`; `DumpAllService` sets the flag only for GameOnly and drops engine-package classes BEFORE the
`walk_class` round-trip (the post-walk skip stays as the authoritative backstop for the include_path-off / TOCTOU
case). **The bug an adversarial review then caught:** the C# `DumpAllService.IsEnginePath` matched `"/Script/Engine."`
(single slash, trailing dot) but `Ubel::GetFullName` actually emits `"//Script/Engine/Actor"` (DOUBLE leading slash,
`/` separators — dot is only used for sub-objects at depth > 2). So `IsEnginePath` **never matched a real wire path**,
making GameOnly a complete no-op — including build 2044's post-walk "fix", which corrected the skip LOCATION but not
the path FORMAT. The tests passed because they fed the `/Script/Engine.Actor` fiction the DLL never produces.
`IsEnginePath` was rewritten as a faithful port of the DLL's `Aura::IsEnginePackage` (collapse the leading-slash run,
require a `/`/`.`/end terminator, prefix list synced to the DLL's 37 packages), and the test fixtures switched to the
real `//Script/Engine/Actor` form so they now guard the production format. This is the CLAUDE.md debugging rule in
action: verify a fix against the actual data format, not an assumed one.

**Verification.** Clean DLL+UI+Tests build green; **2320 tests pass** (helper `params`-overload coverage + real-format
`IsEnginePath` theory + a new pre-walk-skip test that keys on `obj.FullPath`). The whole thing was built by one
inventory workflow (16 agents auditing each panel), one implementation workflow (13 agents, one per panel, editing
disjoint VM+AXAML), and one adversarial review workflow (3 lenses) that surfaced both fixed bugs. New files:
`Helpers/KeywordSearchMemory.cs`; `ObjectTreeFilter` gained the `params` overload.

-----

## 2026-07-10 — Dump Explorer tab (offline "Dump All" .jsonl browser) + Options depth/Deep editable while disconnected (build 2044; dev)

**SHIPPED (UI only; no DLL / pipe-protocol change).** Two changes.

**(1) Options “Locate in GWorld depth” + “Deep” now editable while disconnected.** Both controls shared one
`StackPanel IsEnabled="{Binding LiveWalker.IsGWorldAvailable}"` gate (MainWindow.axaml:222) that greyed them out
until a live GWorld resolved. They back **persisted preferences** (`GWorldLocateDepth`=7 / `GWorldLocateDeep` in
`LiveWalkerUiOptions`), so the gate is removed — the user can dial them in ahead of a locate, like every other
Options item. Locate-time read via `FindPathFromGWorldAsync` is unchanged.

**(2) New “Dump Explorer” tab** — an **offline** browser over an exported *Dump All* (`.jsonl`, produced by
`DumpAllService`). One keyword box searches **classes + properties + functions at once** (space = AND, category
filter All/Class/Prop/Func), so you no longer pick a category and switch tabs first. Results split into two
groups: **✅ In current game** (owning class resolves live — matched by **class short name**, which is stable
across game restarts even when the dump’s recorded address is stale) with a **🡒 Jump** to the live class in the
Live Walker, and **⚠ Not in current game** (read-only metadata reference). Live match = one GObjects pass →
`className→currentAddr` index (class-like metas only). Name (not full path) because `get_object_list` only
carries the short FName and `find_object` is an O(n) scan; the trade-off is same-named classes across packages
collide (last-wins). Disconnect invalidates the match (stale addresses cleared). Purely client-side; parsing
needs no game, matching needs a connected+scanned one. A **⤓ Last export** button one-click-loads the most recent
in-session *Export ▸ Dump All* (no dialog; NOT auto-loaded — repeated exports / a busy tab mustn't clobber the
view). Rows also offer **🔍 Instances** (→ Instance Finder for the owning class) as the class→instance bridge to
the downstream *Related ▸ Locate-in-GWorld/GameEngine* flow; Locate is deliberately NOT put on rows here because
it's instance-oriented and a class object almost never sits in the GWorld/engine forward graph. New files: `Models/DumpBrowseModels.cs` (DTOs + source-gen JSON context + flat
`DumpEntry`), `Services/DumpJsonlReader.cs`, `ViewModels/DumpExplorerViewModel.cs`, `Views/DumpExplorerPanel.axaml`.
Added `IPlatformService.ShowOpenFileDialogAsync` (default no-op + Windows `OpenFilePickerAsync`). Tab inserted
after **Related** (`MainTabIndex.DumpExplorer=11`, tail renumbered 12–17). 6 new unit tests
(`DumpExplorerTests`). An adversarial multi-agent review of the feature caught + fixed 4 issues before ship: a
**blocker** (path-matching was dead against the real DLL — `get_object_list` sends no path and
`UObjectNode.FullPath` is always `""`; switched to class-short-name matching), two **major** cancellation races
(OCE guard read the shared `_opCts` field instead of the op-local token → superseded op reset shared `IsBusy`;
now op-local guard + `EndOp` + Load gated on `!IsBusy`), and a **major** stale-match-on-disconnect
(`SetConnected(false)` now clears `IsMatched`/`LiveAddr`).

**(3) Fixed `DumpAllService` GameOnly silent no-op (pre-existing bug, found during (2)'s review).** The engine-
package skip keyed on `obj.FullPath` from `GetObjectListAsync`, which is **always `""`** (the DLL `get_object_list`
sends no path) → `IsEnginePath("")` false → engine classes were never skipped and every "game only" Dump All
actually included all engine classes. Moved the skip **post-walk** onto the reliable `classInfo.FullPath` (from
`walk_class`/`walk_class_batch`, which do carry the path), before `WalkFunctions` so an engine class costs no extra
round-trip; `FlushClassChunkAsync` now returns a `skipped` count. The existing GameOnly test was masking the bug
(it set `obj.FullPath`, which production never has) — rewritten to be realistic so it genuinely regresses.

Full suite 2306 green.

-----

## 2026-07-10 — CE export: configurable String Len + Fabricate empty/extended TArray slots on Copy CE Field (builds 2028-2038; dev)

**SHIPPED (UI-only; 2299 tests green).** Two exporter features (entry added retroactively 2026-07-11; details in
memory `project-ce-export-strlen-fill-empty`).

- **String Len (build 2028)** — `EmitStringLeaf` hardcoded `<Length>256` for every CE String leaf (4 call sites).
  Now a toolbar **exponent slider** (2^4..2^12 = 16..4096, default 256) mirroring the `DropDownLimit` recipe:
  `MainUiOptions.CeStringLengthExponent` → fan-out to LiveWalker/InstanceFinder → `ceStringLength` param on all 3
  `Generate*` entry points → `[ThreadStatic]` → `EmitStringLeaf`. CSX untouched (separate `Bytesize=18`).
- **Fabricate (builds 2032-2038)** — on **Copy CE Field** (Live Walker only), a "Fabricate" exponent slider
  (0=Off default, else 2^N=2..4096; amber "⚠ large" warning past 256) pads a selected **TArray** to
  `max(N, Num)` element rows so the CE table already has slots for items the current save hasn't populated.
  Object arrays replicate the first resolved element's child layout via `EmitDrilledPointer` (synthetic per-slot
  key = the slot's ABSOLUTE address — **globally unique**, fix `ca47a2c`: an offset-based key collided across
  two same-class arrays at the same property offset and wrongly collapsed to "(shared)"); scalar arrays append
  leaves; struct arrays replicate a resolved element's struct layout. **Next-layer-only gate** (fix `77764e0`,
  in-game bug): the first ship-test fabricated every NESTED array of every element recursively (234-item Cargo →
  ~500k-line file truncated at `MaxEmitEntries`) — now `FabricateActive => _fabricateArrayCount>0 &&
  _emitPointerDepth==0` fabricates ONLY a top-level array of the walked object. Fabricate vs Array Limit:
  `rows = max(Fabricate, walked)` — real data is never hidden; slots past `Num` read past-end (harmless, CE
  shows `??` and re-derefs per poll). **Map/Set not fabricated** (sparse gaps never on the wire). Deferred:
  bare `TArray<FString>` string-container emit path (Phase 3).

-----

## 2026-07-09 — Auto-Detect Player Stats: prop-flags wire + structural scorer + experimental "Detect Stats" panel (builds 2014-2026; dev; IN-GAME VERIFIED TQ2/ES2/SEED)

**SHIPPED (DLL + UI; entry added retroactively 2026-07-11 — full history in memory `project-autodetect-stats`).**
Auto-suggest likely HP/MP/Gold/Mana/XP/Level fields from dumped metadata, fusing name + structural signals.
P0 (labeled corpus) + P3 (learned weights) were **de-scoped** by user call — rules only, no ML.

- **P1 (b2014)** — `prop_flags` (uint64 CPF_* hex) + `array_dim` + `inner_struct_type` on wire→UI→JSONL
  (`Fern.cpp` EncodeClassInfoToJson, `FieldInfoModel`, `DumpAllService`). Omitted at defaults.
- **P2a (b2016)** — structural rules in `PropertyScoringTable`: GAS `StructType=="GameplayAttributeData"` → +4
  (un-categorised→Stats); value-category keyword on container/pointer/delegate → −1; **Current/Max pairing**
  (`ComputeStatPairBonuses`, `NormaliseStatStem` strips max/current/base/… qualifiers; b2019 added
  `maximum`/`minimum` full-word tokens for TQ2's `MaximumHealth`) → +2 per family member.
- **P2b (b2017)** — propflags gating: `CPF_SaveGame`(0x01000000)→+2, `CPF_BlueprintVisible`(0x04)→+1,
  `CPF_EditorOnly`→−4. Deliberately NO Transient/Net penalty (current HP is often transient+replicated).
- **P4 (b2022-2026)** — experimental-gated **"Detect Stats"** tab: one button runs the scorer → top-K grouped by
  class → per-class one `find_instances` + one `walk_instance` → confirm on live-instance-exists +
  value-plausible/GAS + Max-sibling → confidence rank. Opt-in **snapshot signal** boosts names that decreased
  across 2 usable snapshots (+ Δ column "320.23 → 300.33"). Prominent "reference only, low accuracy" disclaimer.
  Row handoffs 🌍/inst/copy reuse the InterestingProperties pattern.
- **In-game verified**: TQ2 (80 GAS `GameplayAttributeData` fields / 12 AttributeSet classes — GAS rule fires;
  live run 80 candidates / 41 confirmed incl. Health pair + MaxWalkSpeed), ES2 + SEED (0 GAS — rule correctly
  silent, keyword+type+pair+flags path). Side fixes: SnapshotStore sync-SQLite freeze → `Task.Run`;
  Locate-in-GWorld `not_reachable` banner wrongly said "raising depth won't help" (BFS is depth-bounded —
  log-proven depth 5 fails / depth 8 finds 7 hops) → message + a once-per-session pre-search confirm when
  `GWorldLocateDepth < 7` (b2026).

-----

## 2026-07-08 — See-through occluders (Schlacht) + configurable pierce depth (builds 2006-2011; dev; VERIFIED Tower of Mask + DQ7R)

**SHIPPED (DLL + UI; experimental-gated; entry added retroactively 2026-07-11 — details in memory
`project-seethrough-occluders-schlacht`).** New Frieren module **`Schlacht`** (「全知者」): a ~10 Hz worker
traces **camera→VIEW-forward** (`UKismetSystemLibrary::LineTraceSingle` on the game thread via Stark), hides the
nearest **N** non-Pawn blocking actors via `SetActorHiddenInGame(true)` so world geometry stops covering the
view; hidden actors are restored as the view moves and un-hidden on disable/disconnect/shutdown.

- **Camera→VIEW-forward, not camera→pawn** (the live-verify pivot): in first-person the camera sits AT the
  pawn's eyes → degenerate zero-length ray. Forward = `(cosP·cosY, cosP·sinY, sinP)` × 100000uu works first-
  AND third-person (Tower of Mask ✓ first-person, DQ7R ✓).
- **MOBs stay visible**: `Aura::ClassDerivesFromAny(hit, {"Pawn","Character"})` — enemies/NPCs/player are kept
  + pierced-through and don't consume N.
- **Pierce depth N (build 2009)**: hide the nearest N occluders (UI NumericUpDown 1-10, live-push while active)
  — iterative single-trace advancing past each impact (+2uu step), NO `LineTraceMulti` (which would leak the
  engine-allocated `TArray<FHitResult>` every tick). Example: N=1 hides a painting, N=2 hides painting+wall.
- **The fragile core**: `ExtractHitActor` pulls the actor from FHitResult — UE4 `Actor` TWeakObjectPtr →
  leading int32 ObjectIndex → `Aura::GetByIndex`; UE5 `HitObjectHandle` best-effort. Per-game fragile;
  one-shot failure warning (diagnostics quieted to lifecycle-only in build 2011).
- **CE toggle**: Mimic `CMD_SEETHROUGH=14` mailbox + `SeeThroughScriptGenerator` (editable `pierceCount`);
  "Add to CE" is **AOBMaker-only** (grayed out without the plugin — no clipboard fallback, by request).
- **Known limitation**: NO-OP on collision/render-split games — FF7R's trace hits invisible
  `CollisionAssetActor` proxies, so hiding them changes nothing (harmless). Light/shadow passthrough was
  evaluated separately (2026-07-09, todo.md): dynamic lighting already follows the hide; baked/static shadows
  are Lightmass-textured into the receiver and **can't** be removed at runtime (WON'T-DO).

-----

## 2026-07-08 — Teleport experimental gating: Keep-Foreground / Fly / Standalone-trainer cards opt-in (build 1995; dev)

**SHIPPED (UI-only; entry added retroactively 2026-07-11).** The Teleport tab's riskier cards — **Keep
Foreground**, **Fly / Noclip**, **Standalone CE Lua trainer** — plus the "Experimental Hotkeys" card are now
hidden unless the experimental opt-in is enabled (`ExperimentalEnabled` / `ShowExperimentalHotkeys`,
Avalonia `IsVisible` = collapse). Disabling the opt-in force-releases any active experimental state first
(foreground lock off, fly off) so a hidden card can never keep a hook alive. Later experimental features
(See-through 2006, Detect Stats tab 2022) ship behind the same gate. Policy per the wiki: experimental features
are documented + marked experimental, but the enable path is never shown.

-----

## 2026-07-08 — Inject picker "already loaded" detection + Keep-Foreground cursor-lock fix + emergency hotkey + GodMode/KeepFg "Add to CE" delivery (build 1986; dev)

**SHIPPED (DLL + UI; Keep-Foreground cursor-lock fix LIVE-VERIFIED P3R).** Four related fixes/features.

**(4) Raw-AA-to-clipboard sweep — everything now delivers paste-able CE XML.** The God Mode + Keep Foreground
"Copy CE Script" buttons emitted **raw AA code** to the clipboard — same bug the Global-Pointer buttons had: a
bare AA body can't be pasted into a CE memory record. Now they use the identical delivery as Get
GWorld/GameEngine: push straight into CE via AOBMaker (`CreateAAScript`, group `UE5CEDumper (DLL)`) when
connected, else copy paste-able CE memory-record XML (`CheatTableBuilder.WrapAaScriptXml`). Shared helper
`PushOrCopyToggleScriptAsync(desc, script)`; commands one-liners; labels `Copy CE Script`→`Add to CE`; tooltips
updated. Regression tests lock the no-AOBMaker path to wrapped `<CheatTable>` XML (not a bare `[ENABLE]` body).
**Same milder bug fixed in the Invoke / exec / Debug-Camera paths** (`LiveWalkerViewModel` GenerateInvokeScript
×2, `MainWindowViewModel` InterestingFunctions + Console baked-invoke + Debug-Camera): those already
push-first, but their clipboard FALLBACK copied raw AA — now wrapped via `WrapAaScriptXml` too (status text
updated; the stale "embed ue5_invoke_helper.lua" note dropped). The CE-XML pointer-chain exports
(LiveWalker `CopyToClipboardAsync(xml)`) were left untouched — already full `<CheatTable>` XML. Tests 2242→2244.

**(1) Q1 — Inject picker shows already-loaded dumper + version.** The "Inject into running game" picker now
detects whether OUR dumper DLL is ALREADY active in each UE process (via a proxy, a prior inject, or a CE `.CT`)
and shows the running DLL version, so the user isn't tricked into a redundant double-load that fights over the
pipe. New pure classifier `DumperModuleDetector.Classify` (Services, unit-tested) takes the target's loaded
modules (file name + PE ProductName + FileVersion) and reports `(loaded, mode, version)` — `mode` =
`proxy: <name>` (version/dinput8/dxgi carrying ProductName `UE5CEDumper`) / `injected` (`UE5Dumper.dll`) /
`loaded: <name>`; identity is `ProductName == UE5CEDumper` (same check as `IsOurProxyDll`), so the real system
version.dll/dxgi.dll never counts. `WindowsPlatformService.DetectDumper(Process)` walks `Process.Modules`,
version-probes only the ≤4 name-matching modules (reading a version resource touches the file), and only for
`IsUe` rows; access-denied/32-bit/exited → "unknown". `GameProcessInfo` gained `DumperLoaded/DumperLoadMode/
DumperVersion` + a `DumperStatusLine`; `ProcessPickerWindow` renders an amber "⬤ … already active; Inject will
just Connect" line; `ProxyDeployViewModel.InjectIntoRunningGame` skips injection and connects when
`DumperLoaded`.

**(2) Q2 — Keep Foreground no longer traps the mouse.** Root cause: `Grausam` makes the game believe it never
lost focus AND keeps its game thread ticking, so the game re-applies `ClipCursor()`/`SetCursorPos()` every frame
to confine the OS cursor to its viewport — Windows auto-releases the clip on real deactivation but the
still-running game re-clips within a frame, locking the mouse across the whole desktop (LIVE-REPRODUCED P3R,
3840×2160). Fix: `Grausam` now also MinHooks `user32!ClipCursor` + `SetCursorPos`. While the lock is on but the
game is NOT genuinely foreground (checked via the un-hooked `g_origGetForegroundWindow` trampoline so the check
isn't fooled by our own illusion), `ClipCursor(rect)` is turned into `ClipCursor(nullptr)` and `SetCursorPos` is
swallowed; when the game truly is foreground both pass through so in-game mouse-look is untouched. Disable also
force-releases any masked clip. Cursor hooks are best-effort (per-target enable, NOT `MH_ALL_HOOKS`, so
Stark/Solitar/Laufen hooks aren't touched); the primary GetForegroundWindow hook still gates the whole feature.

**(3) Q2 — Emergency "Keep Foreground OFF" global hotkey (safety net).** Because a trapped cursor can't reach the
UI's OFF button, added `foreground_on` / `foreground_off` rows to the Teleport hotkey table (generic over
`ActionId` — capture/persist/register for free; routed in `OnMarkerHotkeyPressed` to the existing
`ForceForegroundLockOn/OffCommand`). Bind `Keep Foreground OFF` to a global key to release the lock from any app.

Tests: +`DumperModuleDetectorTests` (8 cases); hotkey-count lock 23→25. 2242 tests green.

-----

## 2026-07-08 — Teleport tab: Global Pointers (GWorld / GameEngine) CE Lua + card reorder (build 1978; dev)

**SHIPPED (DLL + UI; live verify pending).** Two new one-click **"DLL-invoke" CE Lua** exports on the Teleport
tab that ask the injected `UE5Dumper.dll` for a live global-pointer address and show it in a message box:
**Get GWorld** and **Get GameEngine instance address**.

**DLL** — new mailbox command `CMD_QUERY_PTR=13` (`Mimic.h`/`Mimic.cpp`, `HandleQueryPtr`) with two ops
(`QueryPtrOp`): `QUERY_OP_GWORLD=0` writes `paramsData[0..7]`=`&GWorld` (the cached `g_cachedGWorld` slot) +
`[8..15]`=`UWorld*` (slot deref via `Macht::ReadSafe`); `QUERY_OP_GAME_ENGINE=1` calls `Genau::FindGameEngine`
and writes `[0..7]`=`UEngine*` instance, `[8..15]`=`UClass*`, `[16..143]`=class name. `result`=0 found / −1 not
resolved. Read-only + thread-agnostic → runs on the mailbox polling thread even when the game thread is idle. No
new C ABI export / `.def` change — reuses the already-exported `g_invokeMailbox`. **Why the mailbox and not a
plain `UE5_GetGWorld` export + `executeCodeEx`:** CE Lua's `executeCodeEx` can't reliably read an export's
return value on protected games (returns nil) — the repo's standing lesson (godmode-spec §10 / lessons-learned).

**UI** — new `PointerQueryScriptGenerator`: a **stateful toggle** record that publishes a resolved global pointer
as a **registered CE symbol** the user references directly (`[UE_GWorld]+offset` / `[UE_GameEngine]+offset`).
Both resolve via one mailbox round-trip (address at paramsData[0..7] = mb+0x328), then differ by backing:
**GWorld** registers the symbol DIRECTLY to the returned **&GWorld slot** — `[UE_GWorld]` derefs the slot to the
CURRENT UWorld, so it **auto-follows level transitions** (no buffer, disable just unregisters). **GameEngine**
has no static slot (found by walking GObjects), so the `UEngine*` is copied into an `allocateMemory(8)` buffer,
the symbol registered to it (SNAPSHOT), and `[DISABLE]` frees the buffer. Hygiene-compliant: errors
`showMessage`+untick, success is `dbg()`-gated + close-on-clean.
New Teleport **"Global Pointers → Cheat Engine symbols"** card with **Get GWorld** / **Get GameEngine** buttons
(`GetGWorldAddressCommand` / `GetGameEngineAddressCommand`). **Delivery: push via AOBMaker (`CreateAAScript`) so
it lands as a real CE memory record; fallback (AOBMaker not connected / refused) copies the paste-able CE
memory-record XML** (`CheatTableBuilder.WrapAaScriptXml` → `<CheatTable><CheatEntries><CheatEntry>…
<VariableType>Auto Assembler Script</VariableType><AssemblerScript>…`) to the clipboard for right-click → Paste.
(A bare `[ENABLE]`/`[DISABLE]` AA body can't be pasted into a memory record — the v1 raw clipboard copy didn't
work.) `CeMailboxLayout.CmdQueryPtr=13`.

**Layout** — the **"Standalone Trainer (no DLL)"** card moved from the TOP of the tab to the BOTTOM (grouped
with the other export cards, below CE Export); the new Global Pointers card sits just above CE Export.

2232 tests green (+11 `PointerQueryScriptGeneratorTests`, incl. symbol register/unregister + the paste-able-XML
wrapper). Live in-game verify pending.

-----

## 2026-07-07 — Copy CE XML / Copy CE Field / Export CSX made cancellable (builds 1974-1977; dev; PRs #418/#419)

**SHIPPED (UI-only; entry added retroactively 2026-07-11).** The three heavy CE exporters — **Copy CE XML**,
**Copy CE Field** (both `fd2e796`, build 1974) and **Export CSX** (`13121da`, build 1977) — now abort cleanly
mid-flight: a `CancellationToken` is threaded through `ResolveDrilldownAsync` and the emit loops, so a deep
drill-down / huge-array export can be abandoned without wedging the pipe or leaving the DLL walking. GOTCHA
locked in memory `feedback-pipeclient-bare-oce-cancel-guard`: the user-cancel catch MUST be
`catch (OperationCanceledException) when (cts.IsCancellationRequested)` — `PipeClient` throws a **bare OCE on
disconnect** (token=None), and an unguarded catch would swallow a disconnect as if the user cancelled. Also
fixed a flaky debounce test in the same push.

-----

## 2026-07-06 — In-UI DLL injection + inject-ue.ps1 CLI (build ~1958-1962; dev)

**SHIPPED (UI + CLI; no DLL change).** A third way to load `UE5Dumper.dll` besides the CE `.CT` LoadLibrary
and the version/dxgi/dinput8 **proxy**: inject it into an **already-running** UE game. No Cheat Engine, no
pre-deployed proxy, no game restart.

**UI** — Proxy Deploy tab gains an **"Inject into running game…"** button → a modal **process picker**
(`ProcessPickerWindow`, code-behind/AOT-safe like ConfirmDialog; UE games listed first, "Show all" toggle) →
`CreateRemoteThread` + `LoadLibraryW` → **auto-connect** the pipe. Ported from the sibling `discrete`
project's `WindowsDllInjector` but with **classic `[DllImport]`** (this project bans `LibraryImport`/
`AllowUnsafeBlocks`); all inject P/Invokes are blittable. Behind `IPlatformService` (`GetRunningProcesses` +
`InjectDll`), x64-only (rejects Wow64). UE-process detection reuses the drive-scan signals on the process exe
path (`UeProcessDetector`: `*-Shipping.exe`, or under `\Binaries\Win64\` with `Engine`/`Content\Paks`
up-tree). New `GameProcessInfo`/`InjectResult` models; `IProxyDeployService.ListGameProcessesAsync/
InjectDllAsync` façades; `ProxyDeployViewModel.InjectIntoRunningGame` + `PickProcessAsync`/
`RequestConnectAsync` delegates (`MainWindowViewModel` wires the latter → `ConnectCommand`). DLL path =
`<exeDir>\UE5Dumper.dll`.

**CLI** — `scripts/inject-ue.ps1`: one combined command-line injector (list + inject + auto). `inject-ue.ps1`
auto-injects the single running UE game (0 → abort; 2+ → list + abort); `-List` / `-ProcessId N` / `-Dll`.
Same technique via `Add-Type` classic `[DllImport]`. Verified `-List`/auto-abort/DLL-resolution on the dev box.

AOT UI + 2211 tests green (+10 `UeProcessDetector`). **Single-player / offline only** — `CreateRemoteThread`
injection is commonly flagged by AV and blocked/banned by kernel anti-cheat (same caveat as the CE inject +
proxy already carry). Live in-game inject verify pending (technique proven via the CLI on the dev box).

## 2026-07-06 — Keep-Foreground lock (Grausam) — stop background game-thread pause (build ~1950-1955; dev; LIVE-VERIFIED P3R)

**SHIPPED (DLL + UI; NEEDS RE-INJECT).** Some games idle/pause their **game thread whenever they are not the
foreground window** (Persona 3 Reload — verified via its dump log: ProcessEvent hook-fire validator saw 0
fires, POV fell back to raw cached read). Root cause (confirmed in vendored UE source): `FApp::HasFocus()`
→ `FWindowsPlatformApplicationMisc::IsThisApplicationForeground()` = *"does `GetForegroundWindow()` belong to
my PID?"*. Both UE's `t.IdleWhenNotForeground` idle **and** any game-side focus-loss pause gate on this, so
clicking our UI (or CE) makes the game think it lost focus → thread sleeps → invokes/POV time out. Editing the
game's `Engine.ini` `[ConsoleVariables] t.IdleWhenNotForeground=0` did NOT help (set in code/pak, and/or the
game has its own focus-pause).

**Fix — new module `Grausam` (illusion magic):** MinHook `user32!GetForegroundWindow` (resolved via
`GetProcAddress`, so the real function body is patched for all callers) to always return the game's own
top-level window (largest visible un-owned window of our PID, via `EnumWindows`) when the real foreground
belongs to another process. `IsThisApplicationForeground()` then always reports foreground → no idle, no
focus-pause, at the *root* signal (version- and game-agnostic; also stops audio ducking). No AOB / no
IConsoleManager needed. Soft-disable leaves the hook installed and passes through (no unhook race). Coexists
with Stark's MinHook (`MH_Initialize` guards `ALREADY_INITIALIZED`).

**v2 (build 1955) — the fix that actually worked.** The GetForegroundWindow hook alone did NOT stop P3R
(log: lock ENABLED but game still paused). P3R's pause is **`WM_ACTIVATEAPP`-message-driven** (UE's
`FWindowsApplication` → `OnApplicationActivationChanged(false)`), not GetForegroundWindow polling. So Grausam
now ALSO subclasses every top-level game window's WndProc (`SetWindowLongPtrW` + `SetProp`, gated on enabled)
and rewrites the deactivation messages to "active": `WM_ACTIVATEAPP→TRUE`, `WM_NCACTIVATE→TRUE`,
`WM_ACTIVATE→WA_ACTIVE`, swallow `WM_KILLFOCUS`. Two focus channels now covered (polling + messages).
**LIVE-VERIFIED on P3R** (build 1955): log shows it subclassed the real 3840×2160 game window and
invoke/POV ops work while backgrounded. Build gotcha: `-Target DLL` does NOT build the proxy DLLs (the
injected file) — use `-Target All`.

**Wiring:** pipe `set_foreground_lock` / `get_foreground_lock` (Renge) → `Grausam::SetForegroundLock/IsEnabled`
(Fern); `IDumpService.Set/GetForegroundLockAsync` (DumpService); Teleport tab **Keep Foreground** card
(ON/OFF/Unknown badge + Force ON/OFF/↻), off by default. DLL + AOT UI + 2194 tests green.

**CE mailbox path (build 1956).** Added `Mimic CMD_FOREGROUND=12` (op `FG_OP_SET`/`FG_OP_GET`, `ForegroundOp`)
so pure-CE users can toggle it without the pipe/UI. Thread-agnostic — Grausam is a pure Win32 hook/subclass,
so `HandleForeground` runs entirely on the mailbox **polling thread** (not the game thread), and therefore
works even while the game thread is idle. New `ForegroundScriptGenerator` (mirrors `ProtectionScriptGenerator`,
quiet-by-default per the CE-Lua hygiene rule) + a **Copy CE Script** button on the Keep Foreground card
(`CopyForegroundLockScript`); `CeMailboxLayout.CmdForeground=12`. The bundled `UE5CEDumper.CT` is unchanged
(GodMode/Fly/etc. aren't baked into it either — the CE path for all of them is the Copy-CE-Script generator).
2201 UI tests green (+7 generator tests).

## 2026-07-06 — Generic (non-Steam) drive scan in Proxy Deploy (build ~1944; dev)

**SHIPPED (UI-only, no re-inject).** The Proxy Deploy tab could only find Steam-installed games. Added a
**Source toggle** (Steam / Scan Drives) at the top of the same panel — Scan Drives searches user-chosen
drives for UE games installed *anywhere* (Epic / GOG / manual), then feeds them into the identical
Deploy / Undeploy / Update / Refresh machinery (all source-agnostic — they only need `BinariesDir`).

**Requirements → mechanism.** (1) drive checkbox-list; (2) **partitions of one physical disk scan
sequentially, different disks in parallel** — `IOCTL_STORAGE_GET_DEVICE_NUMBER` groups drives, groups run
`Task.WhenAll` while drives within a group run a sequential `foreach await` (bounded by
`SemaphoreSlim(Clamp(ProcessorCount,1,4))`); (3) inaccessible folders skipped and walk continues
(`EnumerationOptions.IgnoreInaccessible` + materialize-in-try around every enumeration); (4) UE detection
by folder structure (`LooksLikeUeGameRoot`: sibling `Engine\Binaries\Win64` / `Content\Paks\*.pak|utoc|ucas`
/ `*-Win64-Shipping.exe`) with **prune-on-match** (never descend a matched root's multi-GB Content tree)
+ depth-6 cap + reparse-point cycle guard + hard-skip set; (5) Steam libraries excluded (resolved roots
via `GetSteamLibraryFoldersAsync` + `steamapps` hard-skip fallback); (6) results drop into the existing
`Games` grid.

**Impl / AOT.** Drive→physical-disk mapping is **classic `[DllImport]`** `CreateFileW(\\.\C:, access=0)`
+ `DeviceIoControl` + blittable 12-byte `STORAGE_DEVICE_NUMBER` — no WMI/`System.Management` (AOT-banned),
no `LibraryImport`. `dwDesiredAccess=0` = query-only = **no admin** needed. Lives behind
`IPlatformService.GetLogicalDrives()` (Core stays platform-free); `ProxyDeployService` gains an
`IPlatformService` ctor param (wired in `App.axaml.cs`). Unknown disk (spanned/network/virtual /
`DeviceNumber==0xFFFFFFFF`) → its own scan group. New models `DriveDescriptor` / `DriveScanProgress`;
new service methods `GetScannableDrivesAsync` / `FindUeGamesOnDrivesAsync`; VM `ScanDrivesMode` +
`LoadDrives`/`ScanDrives` commands; per-game-dir detection (`ScanGameFolder`) reused verbatim.
`IsKnownStubExe` also filters `UnrealEditor/UE4Editor/UnrealFrontend.exe`. 2194 UI tests green (+14 pure-
helper + Steam-exclusion end-to-end).

**Follow-ups (same build).** Added a **Cancel** button (`[RelayCommand(IncludeCancelCommand=true)]` →
`ScanDrivesCancelCommand`, shown only while `IsScanning`) and **mode persistence** (`ScanDrivesMode` in
`ProxyDeployUiOptions` + the `ProxyDeployPersist`/Apply/Capture blocks; restoring drive-mode lazily
re-enumerates drives). **Real-machine verified** (headless P/Invoke replica, non-elevated): 7 drives →
6 physical disks, `STORAGE_DEVICE_NUMBER`=12 bytes; two volumes sharing physical disk 0 correctly grouped
into one sequential lane while the other five run parallel — requirement 2 confirmed on real hardware.

## 2026-07-04 — CE generators skip OUT-string params (crash fix) (build ~1913; pushed dev)

**SHIPPED (UI-only, no re-inject).** Parity fix bringing the two CE-Lua generators in line with the
PIPE path (build ~1911): an OUT `FString&` param (the callee fills it) must stay a zeroed/empty FString,
because building one would make the callee's reassignment `FMemory::Free` our CE-allocated (non-FMemory)
`Data` buffer and crash. INPUT strings are unaffected — the common case.

**Impl.** `InvokeScriptGenerator` (INV): the FIRE loop skips `IsStringType && IsOut` params (emits a
`-- out FString left empty` comment; the zero-fill already left a valid empty FString), and the inline
`writeFStr` builder is now only emitted when an INPUT (non-out) string param exists.
`InvokeParamDialog.CollectBakedValues` (AA(Baked)): skips out-string params so the helper never builds
one. Out-string params are rare, so this was a latent edge case, not a common crash. 2162 UI tests
green (+2).

## 2026-07-04 — PIPE FIRE string INPUT params (DLL builds the FString) (build ~1911; pushed dev; needs re-inject)

**SHIPPED (DLL + UI + protocol; NEEDS RE-INJECT).** The in-app **PIPE** FIRE path can now pass string
input params — the last invoke path that couldn't (INV / AA(Baked) got FStrings in build ~1908). The
hex-buffer model can't carry an FString because its `Data` pointer must be a valid **game-process**
address the UI can't allocate. Fix: the UI sends a `str_params` descriptor list and the **DLL builds
each FString in-process**.

**Why the DLL can.** UE5Dumper.dll is injected, so a buffer it `malloc`s lives in the game's address
space (the same reason `paramBuf.data()` already works as the ProcessEvent params pointer). For each
`{ off, wide, text }`: allocate a char buffer (wide → UTF-8→UTF-16LE via `MultiByteToWideChar`, full
Unicode — better than the CE-side ASCII-only path; narrow → raw UTF-8/ANSI bytes), patch the by-value
`{ Data, Num, Max }` at `off`, run ProcessEvent.

**Lifetime.** UE's convention makes the CALLER own the params and a UFUNCTION takes its FString by
value/const-ref, so freeing after ProcessEvent is correct — EXCEPT on a game-thread dispatch **timeout
(-5)**, where the request stays queued with a copy whose `Data` aliases our buffers; a late drain would
deref them, so we deliberately **leak on -5** (matches the CE-side policy). Every other path frees now.

**OUT strings.** The UI only sends `str_params` for **input** (`!IsOut`) string params. An OUT
`FString&` must stay a zeroed/empty struct — otherwise the callee's reassignment would `FMemory::Free`
our non-FMemory buffer and crash. A zeroed slot is a valid empty FString the callee fills safely.

**Impl.** `Fern.cpp` (`invoke_function`): parse `str_params`, build/patch/free (leak-on-`-5`), bounds-
checked. `IDumpService` / `DumpService` gained an `IReadOnlyList<InvokeStringParam>? stringParams` arg
(new `InvokeStringParam` record) serialised to `str_params`. `InvokeParamDialog.OnFireClicked` collects
input string fields instead of writing them as scalars. `ParamBufferBuilder.IsStringType/IsWideString`
made public. `docs/pipe-protocol.md` updated. DLL + UI build clean, 2160 UI tests green (+3). **Re-inject
required** (DLL change). Fern has no unit tests → verify the actual call in-game.

## 2026-07-04 — FString INPUT param support in invoke generators + helper (build ~1908; pushed dev)

**SHIPPED (UI + helper `.lua`, no re-inject).** String **input** params (`StrProperty` /
`Utf8StrProperty` / `AnsiStrProperty`) are now built for you by the two CE-Lua invoke generators.
Before, `ParamBufferBuilder` / `InvokeScriptGenerator` wrote a bare int32 into the FString slot and
`ue5_invoke_helper.lua` raised `Unknown param type 'fstring'` — string params were effectively
unusable from the generated scripts.

**Model.** A UE string param is passed **by value**: the params buffer holds the whole 16-byte
`{ CharT* Data; int32 ArrayNum; int32 ArrayMax }` struct inline (NOT a pointer to it). So the scripts
now `allocateMemory` a char buffer in the target process, write the chars + null terminator, and stamp
the three struct fields. Wide (`StrProperty` → UTF-16LE) vs narrow (`FUtf8String`/`FAnsiString` → raw
bytes) is preserved. ASCII / basic-Latin only for the wide case (no UTF-8 transcode).

**Lifetime.** The Data buffer is intentionally **leaked** by default — freeing is unsafe if the callee
retained the pointer, and it's CE-allocated (not UE `FMemory`), so the game must never free it. The
helper tracks allocations in `_ue5_invoke_str_bufs` and exposes an opt-in `freeInvokeStringBuffers()`
for when you know the call merely read the string. A few small leaks per one-shot cheat is the safe
default.

**Impl.** Helper `ue5_invoke_helper.lua` (v1.1→1.2): new `writeFStringInline` + `writeBakedParams`
`fstring`/`fstringn` branches + public `freeInvokeStringBuffers`. `BakedScriptGenerator`: new
`MapInputType` (keeps wide/narrow — the return path still uses `MapToHelperType`, so its complex-type
classification is untouched) + `RenderLiteral` emits a quoted Lua string for string types.
`InvokeScriptGenerator` (self-contained, no helper): inlines a small `writeFStr(addr, s, wide)` builder
when any string param is present + text field defaults to empty. `ParamBufferBuilder.GetDefaultValue`
strings → empty (nicer dialog field). Generated scripts stay **ASCII** (bilingual EN/中文 comments live
only in the C# source + the static `.lua`, per the user's request — never in emitted text).

**Still out of scope.** The **PIPE** (in-app) FIRE path can't build FStrings (it has no target-process
allocation in the hex-buffer model — that needs a DLL-side change); string params there still pass
empty. UI + helper build clean, 2157 UI tests green (+13).

## 2026-07-04 — Invoke scripts print the return value under UE5_DEBUG (build ~1905; pushed dev)

**SHIPPED (UI-only, no re-inject).** Both CE-Lua UFunction invoke generators now decode + `print()`
the return value when `DEBUG ~= 0`. Before this, only the in-app **PIPE** dialog decoded returns (via
`StructReturnDecoder`); the generated **INV** (`InvokeScriptGenerator`) never read the return at all,
and **AA(Baked)** (`BakedScriptGenerator`) printed it only in the opt-in "Verify return value" mode —
so a user who turned DEBUG on still saw nothing (the exact complaint that prompted this).

**Where the return lives.** After a successful invoke the value sits in the mailbox params buffer at
`g_invokeMailbox.paramsData + returnOffset` (`mb + 0x328 + off`) — UE lays the return param inside the
same params blob as the inputs. Static-native funcs run ProcessEvent in-place ([Mimic.cpp:416](../dll/src/Mimic.cpp));
game-thread funcs copy `ownedParams` back into the caller buffer on success ([Stark.cpp:376](../dll/src/Stark.cpp)) —
the timeout path does NOT copy back, hence the `result==0`/`ok` gate.

**Impl.** New shared emitter `CeInvokeReturn.AppendDecodeAndPrint` (single source of truth so the two
generators can't drift): scalars via the matching CE read; `StrProperty` derefs the `{Data,Num}` header
and wide-reads the string (`readString(_sp, 512, true)`); `Utf8Str/AnsiStr` narrow-read; every other
buffer type (`FText`/`TArray`/`TMap`/`TSet`/`FStruct`/delegate) falls back to a bounded raw-hex dump.
Wired into `InvokeScriptGenerator` (both the direct-invoke and the param-form FIRE paths, base local
`_PDret = mb + 0x328`, gated `if result == 0 and DEBUG ~= 0`) and `BakedScriptGenerator`'s non-verify
branch (resolves the mailbox via `getAddressSafe` + module-prefixed fallback, gated `if ok and DEBUG ~= 0`;
verify mode keeps its own richer Before/After print, so the two never double-fire). Uses `_PDret`
(not `PD`) so it can't collide with the write-path's `PD` local, and the `dbg()` preamble's own
`DEBUG ~= 0` substring means "no return block" tests must assert on `_PDret` / the full gate line.

**Hygiene.** Fully honours the quiet-by-default rule: nothing prints when `DEBUG == 0` (the
success-close still fires); when `DEBUG ~= 0` the value prints and the window stays open. Tooltips for
INV / PIPE / AA(Baked) rewritten to explain the three paths + where the return value goes.

**Known gap (unchanged).** String **input** params are still not built for you — the generators write a
number into the FString slot rather than allocating a `{Data,Num,Max}` buffer + wide char data (and
freeing it after). Documented as a follow-up. UI builds clean, 2144 tests green.

## 2026-07-04 — Game-thread stall detection: POV fast-fail + app-wide "paused" banner (build ~1902; pushed dev, dev→main PR)

**SHIPPED.** Fix for a ~3.5-min object-list load diagnosed from a Brimstone (UE5.6) log: the **game
thread was paused** (user paused / alt-tabbed), so every `Stark` game-thread invoke timed out at
5000ms for 5.5 min straight. Teleport's 500ms auto-refresh polls `teleport_get_pov`, whose
`Wirbel::GetPovImpl` does **two** game-thread invokes (`GetCameraLocation` + `GetCameraRotation`) =
~10s block per poll; the DLL's serial pipe dispatch (head-of-line, see `multipipe-eval.md`) then
starved the pure-memory `get_object_list` paging behind those blocks. `teleport_get_pose` is a raw
read (no invoke) — only POV blocks. DLL + UI (AOT publish) build clean, 2120 tests green. **Needs
re-inject; verify in-game.**

**B — fast-fail (the cure).** `Wirbel::GetPovImpl` now guards the two getters on
`Stark::IsGameThreadResponsive()`; when the thread isn't ticking it skips the invokes and falls
straight through to `ReadPovRaw` (the same trusted cached-POV path already used on cook-stripped
TQ2 / Octopath; response `source="raw"`). While paused the world is frozen, so the cached POV equals
the live value — correct, and no 10s stall. Resumes invoking automatically once the thread ticks.
POV is read-only + Teleport-tab-only — zero impact on dumped/scanned data.

**Stark liveness.** `HookedProcessEvent` now stamps `s_lastHookFireMs` on every fire; `InstallHook`
stamps `s_hookInstalledMs`. `kStallThresholdMs = 500` (PE fires hundreds of times/frame, so only a
genuine stop crosses it). `IsGameThreadResponsive`: hook-not-active → responsive (the first invoke
lazily installs the hook — never block that); fired-before → `now-lastFire ≤ thr`; **active but NEVER
fired → `now-installTime ≤ thr`**. That last branch is essential: in the logged case the hook was
installed but the game was already paused, so it never fired and `s_lastHookFireMs` stayed 0 forever
— last-fire alone would report "responsive" indefinitely. Consequence: the first POV poll after
connecting-while-paused still costs ~10s (installs the hook + baselines); every poll after skips.

**A — app-wide banner.** `Renge::MakeResponse` rides `game_thread_stalled` (= `!IsGameThreadResponsive()`)
on EVERY success envelope — one atomic + steady-clock read, so any command the UI sends updates the
hint (no dedicated heartbeat command/timer). UI: `PipeClient.ReadLoopAsync` raises the new
`IPipeClient.GameThreadStalledChanged` on transition only; `LaneRoutingPipeClient` forwards both lanes;
`MainWindowViewModel.GameThreadStalled` (reset false on disconnect) drives a full-width amber banner
in `MainWindow.axaml` (`str.Banner.GameThreadPaused`) visible from every tab. Two `IPipeClient` test
doubles gained the event.

**Scope.** Only `GetPovImpl` uses the stall guard; other invokes (recall / function-invoke / Laufen /
Solitar) are untouched — the re-assert workers block their own threads, not the pipe, so they don't
starve scans. Single-player.

## 2026-07-03 — Standalone CE-Lua trainer export (no DLL) (build ~1898; pushed dev, dev→main PR)

**SHIPPED.** A "generate a self-contained Cheat Engine trainer for ONE game+version that runs without
the DLL" export on the Teleport tab. Extends the GWorld-anchored AA-walk (PR #333) from a read-only
symbol list into a read+write trainer: **Move Speed / Low Gravity / Super Jump / GodMode / coordinate
TP (Save+Recall)**, driven by CE `createTimer` re-assert loops + raw memory writes over a baked
`[[GWorld]+..]` pointer chain. Delivered into CE via the AOBMaker plugin (`CreateAAScript` per entry),
**gated on AOBMaker availability (gray-out + hint, NO fallback)**. DLL + UI build clean, 2135 UI tests green.

**DLL.** New read-only pipe command `get_trainer_offsets` (`Renge.h`/`Fern.cpp`) →
`Wirbel::GetTrainerOffsets` decomposes `*GWorld→Pawn` into bake-able `(offset, deref)` hops (reusing
`ResolveChain`) + gathers RootComponent/RelativeLocation + FVector width + CharacterMovement +
MaxWalkSpeed/GravityScale/JumpZVelocity offsets; `Solitar::ResolveProtectBits` surfaces every matched
protection bit as a pawn-relative `byteOffset`+`mask`+`protect`. Fern composes one JSON reply.
(Both new fns must live in the public `namespace Wirbel/Solitar`, NOT the file's anon namespace — anon
placement compiled but LNK2019'd.)

**UI.** `Models/TrainerOffsetsModels.cs` (JsonNode-parsed POCO) + `DumpService.GetTrainerOffsetsAsync`
+ `StandaloneTrainerScriptGenerator` (Setup + per-feature `[ENABLE]`/`[DISABLE]` entries; features
whose offset didn't resolve are omitted; Setup AOB-scans GWorld + defines shared globals; re-assert via
`createTimer`; **movement DISABLE restores the captured natural base** so toggling reverts and re-enable
can't re-capture a forced value). Teleport-tab "Standalone Trainer (no DLL)" card (Export gated on
`CanExportTrainer` = connected + AOBMaker) + tab `Tag="Teleport"` + tab-switch AOBMaker re-probe +
`MainWindowViewModel` `SetEngineState`/`CheckAobMakerAsync` wiring (Teleport was missing from both
`SetEngineState` clusters).

**Attribution.** Every generated CE-Lua/AA script now carries
`Generated by UE5CEDumper | https://github.com/bbfox0703/UE5CEDumper` via the shared
`CeLuaHygiene.Attribution` const/helper (all 8 `*ScriptGenerator` + `CeXmlExportService`'s
GWorld-walk/AOB scripts). The two coordinate-TP scripts also carry a "raw write — weaker than the DLL;
the character may not visibly move" note.

**Limitations (by design).** Coordinate TP is a raw `RelativeLocation` write with no `DeepForceWorldPos`
— on cooked-out-setter games (verified: **Elliot UE5.4** — coords change, character doesn't visibly
move); the reliable game-thread `ProcessEvent` teleport stays DLL-only. Cursor-trace TP, `StopMovement`
settle and POV-set are not portable. Per game+version — regenerate after a patch.

## 2026-07-03 — Magic-number centralization: Tier 1 + Tier 2 pointer helper (build ~1888; pushed dev, dev→main PR)

**SHIPPED.** A "pull out the magic numbers, centralize them" sweep across the DLL (C++) and UI (C#),
driven by a multi-agent discovery workflow (17 parallel scanners → per-project synthesis; 63 DLL +
48 UI raw findings, deduped/ranked). Behavior-preserving refactor; full build + all 2120 tests green.

**Tier 1 — duplicated / tunable literals (commit `48315a5`).** DLL: reference the existing
`Grimoire::OFF_UOBJECT_CLASS` (Aura `LooksLikeUObject`) and `Grimoire::PIPE_NAME` (Heiter) instead of
re-hardcoding; new `Stark::kMin/MaxInvokeTimeoutMs` (100/600000) shared by the Stark clamp and Fern's
`set_invoke_timeout` validation; file-local `constexpr` for the Ubel per-request array cap (4096 ×8),
Serie FName chunk/len bounds (256/8192/1024), Aura value-scan defaults (100000/15000) + deep-walk
element cap (50000 ×3), Edel scan budgets (1024/16), Radar session idle-expiry (300s), Lugner_Dxgi
export count (20). UI: 12 new `Constants.cs` entries (GObjects walk page size, scan/query caps +
deadlines + page sizes, Stark invoke timeout, pivot/xref/instance caps, array/dropdown limits) +
reference the existing `DefaultPreviewLimit`/`ObjectTreePageSize`; new `Services/CeMailboxLayout.cs`
shared by all 5 CE Lua generators (canonical mailbox offsets/opcodes/timeout). **Test fix:** the
shared mailbox class normalized `InvokeScriptGenerator`'s zero-padded hex tokens (`0x010`) to the
compact `0x10` used by the other 4 generators (identical numeric address in Lua; 6 InvokeScriptTests
assertions updated) — chosen because it keeps the other four generators' emitted output byte-identical.

**Tier 2 — `IsUserspacePointer` helper (commit `22c2211`).** New `Grimoire::PTR_USERSPACE_MIN/MAX` +
inline `IsUserspacePointer()`. The pervasive x64 "is this a plausible userspace pointer?" guard was
hardcoded ~48× across Genau/Ubel/Aura/Serie/Macht/Wirbel. Converted only the **paired** range checks:
a Python backreference regex (`VAR < 0x10000 || VAR > 0x0*7FFFFFFFFFFF`, same variable both sides,
boundary-guarded) rewrote 43 reject-form sites to `!IsUserspacePointer(x)` (+ folded the two
split-`if` `LooksLikeHeapPtr`/`LooksLikeDataPtr`); 4 accept-form sites became named constants keeping
the strict `>`/`<`. **Critically left untouched** the standalone `0x10000` collisions (min module
size `Macht.cpp:535`, struct/element-size sanity caps, lone low-bound guards) — the pairing
requirement is exactly what makes `0x10000`'s ≥4 meanings safe to disambiguate. Neu::LooksLikePtr (an
equivalent peer helper) also left as-is.

**Deferred (see [todo.md](todo.md)).** Tier 2 remainder — object-count/size ceilings (`0x800000` 8M
object count; `0x100000` 1M, but split between container-element-COUNT and PropertiesSize-BYTES;
`[0x1000..0x400000]` window) and `MAX_CLASS_HIERARCHY_DEPTH=64` — carry genuine per-site multi-meaning
nuance and were left rather than risk conflating unrelated tunables. Tier 3 single-use knobs likewise.

## 2026-06-30 — CE export: flatten leaf records + record colors + collapse single-leaf pointers (build ~1856; MERGED PRs #401/#402, docs #403; in-game VERIFIED on SEED for flatten + colors)

**SHIPPED.** Three opt-in Live Walker → **Options** export features — **Copy CE XML / Copy CE
Field only** (CSX deliberately left nested), **no DLL change, no re-inject**. All **address-
equivalent**: a flattened / collapsed row resolves to the exact same memory as the nested form,
with the offsets folded into the row instead of spread across parent folders. Off by default,
persisted in `LiveWalkerUiOptions`. Origin: the SEED save-data session's three "case" asks plus a
colour follow-up. User recipe added to [tips.md](tips.md) (PR #403).

**Flatten leaf records (names/strings) — PR #401.** Superset of "Flatten primitive-leaf structs":
the flatten gate (new `IsTerminalLeafField` = primitives ∪ `NameProperty` ∪ FString family) now
accepts `FName` + `FString` leaves, so a record struct `{Score, Rank, MsID(FName), PilotName
(FString)}` collapses fully. `EmitFlattenedStruct` emits an FString child as a CE **String** leaf
(`Offsets=[0]`, one FString.Data deref) and a name child as a 4-byte int. `EmitMapProperty`
collapses the per-element `[i]` group when the value is a flattenable record → flat `[i] key ▸
Field` siblings at the combined offset (struct arrays / sets already flattened via `EmitFields`;
only `TMap` wrapped a group). **No field-count cap** — the all-terminal-leaf requirement is the
gate. Subsumes case #2 (`FDateTime` = a single `Ticks`). **In-game VERIFIED on SEED**
`StoryMissionRecord` (`TMap<FName, LifeStoryMissionRecord>`, 222 entries): flat rows, `PilotName`
as a readable CE String.

**Record Colors — PR #401.** Tints flattened container-element rows by element-index parity
(CE `<Color>`, a COLORREF written `BBGGRR` — RGB byte-swapped at emit) so records stay separable
once the `[i]` folder is gone. Even = `struct[0],[2],…`; Odd = `struct[1],[3],…`. `FlattenColorDialog`
(code-behind `ManagedDialogWindow`): neutral preset palette + hex + Reset + live preview; per-section
**Custom…** opens an in-app `ColorPickerDialog` (rainbow hue strip + R/G/B sliders + preview —
deliberately **not** the native Win32 picker, which would need a Core platform-abstraction P/Invoke
and clashes with the dark theme; reflection was never the blocker). Default on, Even = azure, Odd =
unset. Colours land only on flattened rows. **In-game VERIFIED on SEED.**

**Collapse single-leaf pointers — PR #402.** The deferred case #1 ("pointer to a string"): a drilled
pointer whose resolved target holds **exactly one** terminal leaf collapses to one `Pointer ▸ Field`
record via `EmitOneDerefLeaf` (scalar / `FName` → `Address=+ptrOff, Offsets=[childOff]`, 1 deref;
`FString` → `Offsets=[0, childOff]`, 2 derefs) instead of a folder + lone child. Gated in
`EmitDrilledPointer` after the dedup / cycle / depth guards (does not mark dedup or push the cycle
path — a leaf has no subtree); a multi-field pointee keeps its group. Default OFF. **Unit-tested
only** — no in-game pointer-to-string case has surfaced (SEED's `PilotName` is an inline struct
string, already record-flattened); low risk.

**Verification:** 2105 C# tests (+14 across the three features), 797 dll self-tests, Native-AOT
publish clean. All client / C#-only; the export math reuses the CE pointer model proven by the
record flatten. **Lesson:** CE `<Color>` is a Win32 COLORREF (`BBGGRR`), the reverse of RGB —
`0080FF` (azure) writes as `FF8000`.

## 2026-06-28 — Phase 1 REDO: discrete-style two-connection lane split (build ~1845; MERGED PR #396, in-game VERIFIED 1–5)

**SHIPPED.** In-game verified on Elliot (§9.6 items 1–5: connect / interactive-responsive-during-
scan / value-search-mid-snapshot / disconnect-mid-scan / CE invoke under load) and merged to main
(PR #396). Low-priority follow-up: watch-event delivery + single-lane-drop edge. Implements
[multipipe-eval.md](multipipe-eval.md) §9 (Path A),
modeled on the sister repo `D:\Github\discrete`. Fixes the reverted Phase 1's deadlock by giving
**each connection its own handle + its own thread** instead of one worker writing the read
thread's handle — so the synchronous pipe never has two threads on one handle, and **no
overlapped-I/O rewrite is needed**.

**Model.** The UI opens TWO connections (interactive + bulk) to a `maxInstances=3` server; the
DLL serves each on its own detached thread, serial read→dispatch→write on its own handle. The
DLL is **lane-agnostic** (no command classification) — the UI's `LaneRoutingPipeClient` routes
by a `BulkCommands` set (heavy/scan/snapshot → bulk; everything else → interactive). Safe because
the interactive lane never builds the Aura `s_classContainerCache`/`s_classRefCache` and the bulk
lane runs scans one-at-a-time (its own serial connection).

**DLL (`Fern`) — per-connection refactor:** registry of `Connection{pipe, writeMutex, inFlight,
closed}`; thread-per-connection `AcceptLoop`; per-connection `WriteLine`; watches keyed to their
owning connection (old global `PushEvent` removed); monitor iterates connections + trips global
`Tot` on a broken in-flight pipe; per-command cancel reset moved to first-connection-of-session;
`Stop` uses `CancelIoEx` + bounded drain (each thread closes its own handle). **UI:**
`LaneRoutingPipeClient` decorator + `PipeClient` lane tag + `PipeLogEntry.Lane` column.

**Verification so far:** builds (DLL + UI), full suite green (2091 C# / 797 dll / 53 utf8),
Native-AOT publish clean, exe launches + stays alive. **STILL REQUIRED before merge: the §9.6
in-game checklist** (Fern has no unit tests; the last attempt's deadlock only showed in-game).
**Lessons baked in:** separate handle per connection (one thread per handle) is what makes the
synchronous pipe safe; close from the owning thread (`CancelIoEx` to unblock) not cross-thread.

## 2026-06-28 — REVERT Phase 0 + Phase 1 (in-game regressions); keep Pipe Activity log (build ~1841; restores DLL baseline)

**In-game test on Elliot regressed badly** — reverted both phases to the pre-change DLL
baseline (`git checkout 1b7d149 -- Aura.cpp Fern.cpp Fern.h`). Root causes (DLL pipe log +
screenshots; full writeup [multipipe-eval.md](multipipe-eval.md) §8):

1. **Phase 1 deadlocks on the synchronous pipe handle.** The server pipe is created **without
   `FILE_FLAG_OVERLAPPED`** (Fern.cpp:496) → the Windows I/O manager serializes I/O on the
   handle, so the heavy worker's `WriteFile` (response) **cannot complete while the read thread
   is parked in `ReadFile`** waiting for the next command. First connect: `trigger_scan #5`
   handler ran (`RunScan finished`) but its response never reached the UI (nothing else was
   sent to release the read) → UI hung until manual disconnect. Second connect "worked" only
   because continuous user traffic kept flushing the blocked write. **A correct off-thread
   dispatch needs overlapped/async pipe I/O — a real rework, deferred.**
2. **Phase 0 priority drop starves scans ~20×.** With the game saturating cores,
   `THREAD_PRIORITY_BELOW_NORMAL` scan threads barely run → Snapshot crawled to **2% in 57 s
   (~40 min ETA)** vs the normal 1–2 min. The pre-existing `cores − 2` *count* headroom was the
   correct throttle; dropping *priority* on top turned it into "run only when the game is idle".
3. (Also noted) even without the deadlock, Phase 1 wouldn't have fixed Snapshot much: the
   shared `m_writeMutex` re-serializes the multi-MB chunk write, and the **UI-side
   `ReadLoopAsync` parses each multi-MB chunk single-threaded (~2.4 s)**, blocking interleaved
   light responses. Real bottlenecks = scan starvation [now reverted] + UI chunk parse, not DLL
   dispatch.

**Kept:** the System-tab Pipe Activity log (UI-only, unrelated to the deadlock — it's what made
the missing `←` reply obvious). Cap raised 100→**200** per request; newest-first (newest always
visible at top). **Lesson:** never run a `WriteFile` from a second thread while the read thread
is blocked in `ReadFile` on a non-overlapped handle — the OS serializes them. Verify pipe
concurrency changes IN-GAME, not just by unit tests (Fern has none).

## 2026-06-28 — System-tab "Pipe Activity" log (live UI↔DLL traffic tail) (build ~1837; UI-only; 2091 C# green, AOT publish clean + launch-verified)

**Context.** Companion to Phase 1: a small in-UI tail of pipe traffic on the System tab so
the user can *see* light commands interleave with a heavy scan (proof the lane split works)
without opening the `%LOCALAPPDATA%` pipe log. The user noted the file log already exists, so
this is a convenience mirror — its value is immediacy.

**Change.** `IPipeClient` gains an `Activity` event raised for every line: TX (on send, with
command + id), RX (on response, paired back to the TX for command name + round-trip ms via a
new `_txMeta` id→(cmd,tick) map), and push events. `PipeClient` raises it only when a
subscriber is attached (near-zero cost otherwise) and clears `_txMeta` on cancel/disconnect.
The System-tab VM (`PointerPanelViewModel`) subscribes and renders a newest-first ring buffer
(`ObservableCollection<PipeLogEntry>`, cap 100) with **Pause** + **Clear**. Pipe-thread
callbacks enqueue into a `ConcurrentQueue` and **coalesce a single `Dispatcher.UIThread.Post`
per burst at `DispatcherPriority.Background`**, so a snapshot streaming hundreds of chunks
can't flood the UI thread. New `PipeLogEntry` model; a "Pipe Activity" card in `PointerPanel.axaml`
(monospace list + empty placeholder); en.axaml strings. Test mocks (`MockPipeClient`,
`NoopPipeClient`) implement the new event.

**Verification.** Full suite green (2091 C# / 797 dll / 53 utf8); Native-AOT publish clean (no
new ILC/trim warnings) and the published exe launches + stays alive with no `crash.log`.

## 2026-06-28 — Multi-pipe Phase 1: non-blocking DLL dispatch (heavy worker lane) (build ~1836; DLL-only Fern; 2091 C# / 797 dll / 53 utf8 green)

**Context.** Phase 0 (below) protected CE responsiveness but left the actual UI symptom —
Live Walker slow during a Snapshot — untouched, because the DLL command loop was still
strictly serial (a `snapshot_chunk` blocked the read thread so a queued `walk_instance`
waited in the pipe buffer). Phase 1 fixes that. Design + rationale: [multipipe-eval.md](multipipe-eval.md) §5–§6.

**Change (Fern only — no protocol/UI change).** `HandleClient` no longer runs every command
inline. It now routes by lane:
- **Light** (`IsLightCommand` allowlist — get_object/list, walk_class/instance,
  read_array_elements, query_candidates, teleport_*, god/movement/cursor, watch, …): run
  **inline** on the read thread, exactly as before.
- **Heavy** (everything else — value/group scan, snapshot_chunk, find_instances/refs/path,
  find_by_address, search_properties, list_*, invoke_function, version/packed/apply-rescan
  mutators, …): posted to a **single FIFO worker thread** (`HeavyWorkerLoop`). The read loop
  returns immediately to service light commands. The worker runs `DispatchCommand` (which
  bakes the request id into the response) and writes the id-tagged response via the
  `m_writeMutex`-guarded `WriteLine`, so it interleaves safely with the read thread's light
  responses and async `PushEvent`s. The UI already demultiplexes by id, so out-of-order
  completion needs no client change.

**Concurrency-1 by design.** The heavy worker is a single FIFO thread, so two cache-building
scans never run at once → the Aura `s_classContainerCache`/`s_classRefCache` (and GObjects
drift) are never raced across commands. Light commands only read write-once globals + Ubel's
mutex-guarded caches + their own session, so a light command running concurrently with one
heavy job is safe. The allowlist is the *only* light set; anything unlisted defaults to heavy
(safe default — a new/unclassified command can't accidentally race a scan).

**Cancellation simplified.** `Tot::ResetPerCommand` moved from per-command to **per-client**
(at `HandleClient` start) — `g_perCommand` is only ever set on disconnect (Fern monitor), so
per-client reset is equivalent, and it removes the risk of a light command clearing a running
scan's cancel mid-flight. On disconnect the read loop drains the worker: `RequestPerCommand`
(running scan bails at its next Tot poll) + clear the queue + **wait for the worker idle**
before returning, so AcceptLoop's pipe-close + session `DropAll` can't race a live job.
`Fern::Start/Stop` spawn/join the worker (Stop's existing `RequestShutdown` makes an in-flight
job bail fast).

**Status.** Built + full suite green. Fern is integration-level (not unit-tested), so the
threading needs **in-game verification**: confirm Live Walker / teleport stay responsive while
a Snapshot / Value Search runs, and that disconnect mid-scan frees the pipe cleanly.

## 2026-06-28 — Multi-pipe IPC evaluation + Phase 0 scan CPU/priority guard (build ~1834; DLL-only; 2091 C# / 797 dll / 53 utf8 green)

**Context.** User asked whether the UI↔DLL and CE-Lua↔DLL IPC should use **multiple
pipes** — Live Walker feels slow during a Snapshot (suspected pipe queuing), and the CE
`.CT` invoke mailbox should stay responsive while the UI does heavy work. Investigated via
a multi-agent workflow (5 parallel readers over Fern/PipeClient/Mimic+Stark/command
taxonomy/shared-state safety + synthesis + adversarial verify).

**Findings (full writeup: [multipipe-eval.md](multipipe-eval.md)).**
- There are **already two independent channels**: the UI **named pipe** (Fern) and the CE
  **shared-memory mailbox** (Mimic, `g_invokeMailbox`, polled on its own thread → Stark
  game-thread queue). CE never touches the pipe.
- Root cause of the UI lag is **DLL-side head-of-line blocking**: one pipe instance
  (`CreateNamedPipeW maxInstances=1`) + a strictly serial `HandleClient` loop
  (`ReadLine → DispatchCommand[blocks] → WriteLine`). A `snapshot_chunk` (~0.5–2 s) blocks
  the read loop so a queued `walk_instance` waits in the pipe buffer. **Not** the UI
  `_writeLock` (released before the response await — the client already `id`-multiplexes),
  **not** CE.
- **Multi-pipe is the wrong fix.** N pipe instances force two `DispatchCommand` to run
  concurrently, which is **unsafe today** — `s_classContainerCache`/`s_classRefCache` use a
  check-without-lock → build-outside-lock (`Ubel::WalkClassEx`) → insert-under-lock pattern
  that races, and GObjects has no epoch stamp. Recommended instead: **single pipe +
  non-blocking dispatch** (light inline + one heavy worker, concurrency = 1), deferred to
  Phase 1 (needs per-request cancellation — `Tot::ResetPerCommand` is currently global).
- CE responsiveness: pure-memory CE ops (GodMode/Movement/read-only teleport) are already
  immune; only **game-thread-routed** CE invokes are at risk, and only from **CPU
  starvation** during parallel scans — a pipe-count-independent problem.

**Phase 0 shipped (DLL, `Aura.cpp`).** `ScanThreadCount` already capped workers at
`hardware_concurrency() − 2`; added a **thread-priority guard** so a full-pool scan goes
"nice": spawned workers in `ParallelIndexRanges` set `THREAD_PRIORITY_BELOW_NORMAL`, and a
new RAII `ScopedScanPriority` drops the calling thread (which runs chunk 0 inline) for the
duration of a *parallel* scan, restoring on scope exit (incl. the throwing-chunk path).
Serial scans (tiny arrays / anti-tamper "parallel off") are untouched — they leave cores
free anyway. The cancel-watcher stays normal priority so cancellation stays snappy. Net:
Value Search / Group Scan / Snapshot capture no longer competes with the game thread, so CE
`.CT` ProcessEvent invokes keep draining (far less likely to hit the invoke timeout) while
the UI is busy. **The serial UI dispatch is unchanged — Live-Walker-during-Snapshot lag is
Phase 1's job.** *(Built + full test suite green; not yet in-game profiled.)*

## 2026-06-27 — DataGrid horizontal-overflow sweep (all panels) + "Diff is always deep" annotation (build ~1832; UI/XAML-only; no test delta)

**Context.** Two follow-ups after the top-level scalar-array capture fix below. (1) The user
found that dragging a column splitter in the Snapshot result grids could not widen a column
past the window edge — the grid clamped to the UI width with no horizontal scrollbar — and
noted "this was fixed long ago" (the LiveWalker FieldGrid, commit `e1b4d46`) but never went
app-wide. (2) The single-value Snapshot Diff has no "Deep" toggle while Group mode does,
which read as an inconsistency.

**Root cause (DataGrid, adversarially verified).** Any `Width="*"` (star) column makes an
Avalonia DataGrid fit total width to the viewport — the star column absorbs every splitter
drag, so the grid can never overflow and no scrollbar appears. `HorizontalScrollBarVisibility`
**already defaults to `Auto`** (InstanceFinder InstancesGrid scrolls with no explicit attr),
so the missing attribute is NOT the cause — the star column is. The fix is the proven
`e1b4d46` pattern: all columns fixed `Width` + `MinWidth`, no star, plus an explicit
`HorizontalScrollBarVisibility="Auto"` for self-documentation.

**Change.** App-wide sweep — **17 DataGrids across 8 panels** converted (Snapshot ×4, SPC ×5
incl. both snapshot pickers, ClassPivot ×3, RelatedObjects, InstanceFinder ContainerMatches,
LiveWalker References, ValueSearch GroupResultsGrid, ProxyDeploy). Deliberately left alone:
`MainWindow` `<ColumnDefinition Width="*"/>` (tab-host layout, correct) and the two
intentionally non-scroll surfaces (Console ScrollViewer + ClassPivot class-list ListBox, both
`HorizontalScrollBarVisibility="Disabled"`). Every `SortMemberPath` preserved (compiled
bindings need it or nothing sorts). **Tradeoff (same as `e1b4d46`):** fixed columns no longer
auto-stretch to fill a wide window → right-edge dead space; in exchange the grid overflows +
scrolls horizontally on demand.

**Diff "always deep" annotation.** Confirmed (workflow + verify) that the Snapshot **Diff is
already always-deep** — `DiffSnapshotsAsync` applies no `array_field` filter, so nested
array / struct-array element changes (e.g. `SupportActionGauge[3]`, shown as `Array[N]`)
always appear. "Deep" is a QUERY-time toggle that exists **only in Group mode** (gates array
elements in as group-match slots); a shared "global Deep" would be misleading (Diff =
always-deep, Group = opt-in-shallow → opposite defaults). Per the user's choice: no Diff Deep
checkbox; instead an italic **"Diff is always deep"** note (+ tooltip) sits next to **Run
Diff** so users don't hunt for a missing switch.

**Verify.** UI build clean (compiled-binding XAML validates at build); published
`dist\UE5DumpUI.exe` launch-verified (alive, responding, no `crash.log`). XAML-only — no DLL
re-inject, no test delta.

-----

## 2026-06-27 — Snapshot now captures gameplay classes' TOP-LEVEL scalar arrays (e.g. a Pawn's SupportActionGauge[] TArray<float>) — Diff/SPC/Pivot could not see them before (build 1827; DLL; in-game VERIFIED on Elliot)

**Symptom (Elliot, user-reported).** A Live Walker value — element `[3]` of
`BPPlayerCharacter_C.SupportActionGauge` (`TArray<float>`, 7 floats) — dropped ~89→83, but the
Snapshot **Diff** showed nothing. Value Search finds the same value fine.

**Root cause.** `Aura::CaptureStructArrays`'s walker deliberately skipped **top-level
leaf-containers** — a scalar `TArray<int>/<float>` directly on an object (`leafName==""`,
`depth==1`) — via `if (lf.leafName.empty() && lf.depth < 2) return;` (build 1204), because
capturing every object's scalar arrays would balloon the DB. So `SupportActionGauge[]` was
never stored → Diff/SPC/Pivot had nothing to compare. (Nested leaf-containers `Tunes[N]`,
depth≥2, and struct-array element fields were always captured.) This is a CAPTURE-time gap, not
a query-time one — the Diff is already always-deep and the Group "Deep" toggle can't surface
rows that were never written. **Live Value Search is unaffected** — its Phase 2C Array scan
(`f.TypeName=="ArrayProperty"` + matching innerType → `ScanContainer::Array`) walks the TArray
buffer directly.

**Fix (per user choice "only gameplay classes, by default").** New thread-local-memoized
`IsSnapshotGameplayClass(cls)` (reuses `ClassDerivesFromAny(cls, SnapshotGameplayKeepBases())`
= Actor/ActorComponent/Pawn/Character/Controller/PlayerState/GameInstance) gates a new
`captureTopLevelScalarArrays` param on `CaptureStructArrays`. Gameplay classes (the value
carriers) now capture their top-level numeric scalar arrays; engine/system classes still skip
them, so the DB stays bounded. **No schema change** — rows reuse the existing
`array_field`/`elem_index` columns with `inner_prop_name=""` (the same leaf-container path
`Tunes[N]` already proved end-to-end: Fern encode → SnapshotStore INSERT → Diff display
`Array[N]`).

**Verify.** DLL build clean; 797 dll + 2091 C# tests green; **in-game VERIFIED on Elliot** —
re-injected DLL + fresh snapshots, Diff now shows `SupportActionGauge[3]` 89→83. (Pre-1827
snapshots lack these rows — must re-capture.)

-----

## 2026-06-27 — Snapshot consistency guard: detect a GObjects-count drift mid-capture → mark UNUSABLE + auto-delete before next capture (build ~1825; UI/C#-only; tests +8)

**Context.** A user asked whether the slow/huge `48–61 MB`-chunk snapshot they saw was *GC
corruption*. Investigation (DLL `Aura::CaptureSnapshotChunk` + UI capture loop + SQLite
lifecycle) found **No**: the big chunks are legitimately data-heavy objects, and the run's
every chunk reported a stable `total:390017` (no churn). The "~10 min" was a misread — the
3 captures took ~82 s each; the 9-min gap was Class-Pivot work. BUT the probe surfaced a real
latent bug: the paging loop stops on the *per-chunk* `total` with **no drift detection**, so a
capture spanning a level transition / mass spawn-free is stored as "valid" while being a
temporally-inconsistent mix — and snapshots are consumed OFFLINE by SPC Query / Class Pivot.

**Feature (the user-requested safeguard, with the corrected trigger).**
- **Detect** (`Services/SnapshotConsistency.cs`, pure): track the min/max GObjects count each
  chunk reports; `IsDriftSuspect` flags a span beyond `max(2000, 2% of begin)` — generous so
  ordinary gameplay churn never false-flags a good capture (a false positive would auto-delete
  good offline data).
- **Early-abort + mark unusable**: on drift the capture loop stops early and finalizes the
  partial with `is_usable=0`.
- **Schema** (`SnapshotStore`): `is_usable` added **additively** (PRAGMA-guarded `ALTER`, no
  `SchemaVersion` bump) so existing snapshot DBs are preserved, not wiped.
- **Auto-clean**: `DeleteUnusableSnapshotsAsync` runs at the START of each capture (deferred so
  the user sees the ⚠ flag in the list until then).
- **Offline consumers honour it**: SPC + Class Pivot pickers exclude `is_usable=0`; Diff
  auto-select skips them; the saved-snapshots grid + pickers show a ⚠ prefix
  (`SnapshotMeta.LabelDisplay`/`PickerDisplay`/`UsabilityBadge`).

**Tests.** `SnapshotConsistencyTests` (7) + a store round-trip/`DeleteUnusable` test → C#
2091/0; dll 797/0; published UI launch-verified (additive migration safe on startup). No DLL
change (count already in the chunk RX) → no re-inject for this feature.

-----

## 2026-06-27 — Pipe-block fix: stale/recycled Live Walker object with a garbage PropertiesSize no longer wedges the single-threaded pipe (build ~1824; DLL + UI; tests +6 dll)

**Symptom (Elliot, user-reported).** Did Snapshot → Class Pivot → multi-value diff (~10 min),
switched **back to Live Walker**, and the UI pipe appeared **blocked** — no further command
returned until the app was force-closed.

**Root cause (log-proven, three logs cross-checked).** The instance the Live Walker had
selected (`0x376182070`) was freed/GC'd during the long Pivot pass and its memory slot reused.
On return, the panel re-walked the **stale address**:
1. Shallow `walk_instance` (id=375) read a recycled UClass pointer whose name was empty and
   whose `PropertiesSize` decoded as garbage **867763776 (~827 MB)** — yet returned `ok:true`,
   0 fields, `props_size` huge.
2. UI `AutoFillGapsRetryAsync` saw *0 fields + propsSize>0* and **auto-fired `fill_gaps=true`**
   (id=376) — with no upper bound.
3. DLL `Ubel::WalkInstance` computed **one gap `[0, 0x33B90640)`** = the whole 827 MB and called
   `GuessGapTypes` over it. For a gap >64 KB it abandons the bulk read and does a **per-position
   SEH read, advancing 1 byte per fault** over mostly-unmapped memory → ~**8×10⁸ SEH faults** =
   effective hang. The loop had **no `Tot::Requested()` check**, so even the disconnect monitor
   couldn't free it. The single-threaded pipe queued every later command (the user's `walk_world`
   id=377 never ran) → UI frozen.

**Fix (4 layers).**
- **DLL gate** (`Ubel::WalkInstance`): new pure `Ubel::IsSanePropertiesSize` (≤ `kMaxSanePropertiesSize`
  = 1 MB). Implausible `PropertiesSize` → flag `isStale`, zero `propsSize`, **return before the
  gap walk**.
- **DLL gap-fill cap** (`Ubel.cpp` fill-gaps block): explicit `PropertiesSize <= kMaxSanePropertiesSize`
  guard (belt-and-suspenders with the gate).
- **DLL `GuessGapTypes`**: hard `gapLen > kMaxSanePropertiesSize` refusal (protects *every* caller,
  incl. the Native-C scan) **+ a throttled `Tot::Requested()` poll** so a wide gap stays abortable
  on disconnect/shutdown.
- **UI** (`AutoFillGapsRetryAsync`): skip the auto-retry when `IsStale` or `PropertiesSize` exceeds
  1 MB; `UpdateDisplay` surfaces a "object freed/recycled — re-open from 🌍 GWorld/finder" status
  instead of a silently-blank grid. New `stale` field plumbed Fern → `InstanceWalkResult`.

**Tests.** `Test_IsSanePropertiesSize` (6 EXPECTs incl. the 827 MB value) → dll_helpers 797/0;
C# 2083/0; published UI launch-verified (alive 7 s, no crash.log). In-game re-verify on Elliot
pending (repeat the Snapshot/Pivot → Live Walker round-trip).

-----

## 2026-06-27 — Build fix: DLL/proxy PE VERSIONINFO now tracks build_number again on incremental builds (build ~1822; CMake only)

**Symptom.** After the incremental-Ninja build dir became persistent (build 1817), the
DLL's embedded PE version froze at **1.0.0.1817** while the UI kept advancing (1818, 1819,
1820…). Releases shipped a DLL whose FileVersion/ProductVersion/GitCommit didn't match the UI.

**Root cause.** `version.rc` embeds the version via `#include "BuildInfo.h"` (regenerated by
`configure_file` every configure — build number, git hash, timestamp). Ninja scans `.cpp`
for header includes via `/showIncludes` depfiles, but **does NOT scan `.rc` files** (the RC
compile rule emits no depfile). So a changed `BuildInfo.h` recompiled the `.cpp` TUs that
include it (logs stayed current) and relinked the DLL — but reused the STALE `version.res`,
freezing the PE version at the first kept-build. A full `-Clean` masked it (CI always cleans);
only incremental local builds drifted.

**Fix** (`dll/CMakeLists.txt`): `set_source_files_properties(src/version.rc PROPERTIES
OBJECT_DEPENDS "<gen>/BuildInfo.h")` — adds the missing dependency edge so the resource
recompiles whenever `BuildInfo.h` changes. Directory-scoped → applies to the main DLL and all
three proxies. Verified: main DLL, UI, and version/dinput8/dxgi proxies all stamp the same
`1.0.0.<build>` after a build (was DLL frozen at 1817, UI at 1820).

-----

## 2026-06-27 — Copy CE XML/Field: "Flatten primitive-leaf structs" opt-in (generalizes GAS flatten) + Elliot version doc fix to UE5.4 (build ~1820; UI/C#-only — no DLL change; tests +6)

Two independent C#/docs changes, no DLL touch → no re-inject.

### Flatten primitive-leaf structs (CeXmlExportService)
The GAS-attribute flatten (build ~1799) collapsed a `FGameplayAttributeData` struct one
level — `BaseValue`/`CurrentValue` promoted to `HealthPoint ▸ BaseValue` sibling leaves at
the combined offset. Its trigger was the **struct TYPE name** (`GameplayAttributeData`,
F-prefix accepted) plus an all-children-scalar gate — NOT the owning class name and NOT the
field name. New **"Flatten primitive-leaf structs"** opt-in (Live Walker export Options menu,
default off) generalizes that to ANY terminal struct: a struct flattens when its entire
already-flattened subtree is **primitive inline scalars** (`IsPrimitiveLeafField`:
float/double, int8–64, byte/uint16–64, bool, enum + the Guess scalar labels). A pointer/object,
string (`FString`/`FName`/`FText` — pointer-backed), container (Array/Map/Set/Optional), or
unresolved nested struct keeps the struct grouped ("pointers stay unflattened"). It's a
**superset** of the GAS flatten and naturally flattens `FVector`/`FRotator`/`FTransform`
(pure-float structs); a Vector reached through a pointer is never on this path. Both toggles
fire the SAME promotion (`EmitFlattenedStruct`, renamed from `EmitFlattenedGasStruct`).
**Copy CE XML / Copy CE Field only — CSX is deliberately unaffected.** Wired through
LiveWalkerViewModel (4 export call sites) + UiOptionsSettings persist. Tests +6.

### Elliot = UE5.4 (doc reconciliation)
The Adventures of Elliot's PE has its version string stripped → publisher-fallback detects
UE4.27, but runtime markers reconcile it upward to **UE5.4** (tagged FFieldVariant → 5.3 floor,
`CMC::GravityDirection` → 5.4; build ~1808). Updated the user-facing current-state docs to
match: both README version matrices (moved Elliot 4.25–4.27 → 5.3–5.4 with a reconciliation
note), the README `dxgi.dll` proxy notes, and `test-games.md` (version cell + naming
convention). Build-stamped history (dev-log build 1481/1799, lessons-learned, roadmap
blockquote) left as-is — at those builds it genuinely detected as 4.27.

-----

## 2026-06-27 — Build-system perf: shared OBJECT library + unified build dir + incremental Ninja (build 1817; build tooling only; 2077 C# green)

The four DLLs (UE5Dumper.dll + the version/dinput8/dxgi proxies) previously each
got their own clean CMake configure in a separate build dir, so every `build`
wiped the tree and recompiled Zydis + all ~20 sources up to 4× — Aura.cpp et al.
were rebuilt from scratch on every run. Restructured for incremental builds while
keeping correctness and CI behaviour identical.

### What changed
- **Shared `UE5DumperCommon` OBJECT library** (`dll/CMakeLists.txt`): the 17
  proxy-agnostic sources (Aura/Fern/Frieren/Macht/… — none test a `UE5_PROXY_*`
  macro) compile ONCE; all four DLLs link the same objects. This is the
  CMake-correct form of "share the .obj across DLLs". Only Heiter (gated on
  `UE5_PROXY_BUILD`) + the per-proxy Lugner shims compile per target. OBJECT (not
  STATIC) is deliberate — every object is force-linked, so the dllexported C-ABI
  symbols (Frieren) are never dropped.
- **Single unified build dir** (`build.ps1`): one `cmake` configure (all proxy
  options ON) + one Ninja build produces all four DLLs → Zydis/minhook compile
  ONCE (was 4×); a full `Publish -Clean -Target All` C++ build went from ~188
  compile steps across 4 configures to **69 in one**. Legacy `build_proxy*` dirs
  are removed automatically.
- **Incremental by default**: the unconditional `Remove-Item $BUILD_DIR` is gone;
  only `-Clean` wipes. `build.ps1` still re-runs configure every build so
  `BuildInfo.h` (build number / git / timestamp) stays fresh — that was the real
  cause of the old "Ninja used stale data" perception (it was version metadata,
  not code; Ninja tracks header/source changes correctly).
- **`BuildStamp.h/.cpp`** (new leaf util): the volatile build macros now live in
  one tiny TU behind accessors (`BuildStamp::VersionString()` etc.); Fern /
  Frieren / Sein / Heiter call those instead of including `BuildInfo.h`. A
  build-number bump now recompiles only `BuildStamp.cpp` + `version.rc`, not the
  heavy Frieren/Fern. `version.rc` still uses the macros directly (RC needs them).

### Verified (all real builds on this machine)
- Clean main DLL 1:55; **no-change rebuild 8s** (was a full rebuild);
  touch Aura.cpp → 20s (Aura recompiles → source changes are always reflected).
- dxgi proxy MASM thunks isolated + assembled; all `UE5_*` exports intact.
- **CI path `Publish -Clean -Target All` green in 4:30** — all four DLLs in one
  69-step Ninja build + AOT UI + utf8/dll self-tests + 2077 C# tests pass.

### CI / correctness
CI is unchanged: `release.yml` already passes `-Clean` on a fresh runner, so it
still does a full from-scratch build (if a fast incremental ever went wrong, CI
would still catch it). The object-library + unified-dir changes are
correctness-preserving (CMake owns the sharing + dependency tracking), so CI also
builds faster for free. `BuildStamp` kept English (generic leaf utility — same
exception as `Utf8Helpers.h` / `GraphPath.h`).

-----

## 2026-06-27 — UE-version markers extended to 5.6 / 5.7 (build ~1810; DLL only; 2077 green)

Extends the version-reconciliation chain with the two remaining structural markers
already in the codebase (both reliable, distinct from Avowed's custom packing):
- **UE5.7 reordered FUObjectItem** (`Object*`@+0x08, 24-byte item — the stock-UE5.7
  Solarpunk repro): `Aura::GetItemObjOffset()==0x08 && GetItemSize()==24` → version floor
  **507** in the init reconciliation. The `==24` guard excludes Avowed's CUSTOM 20-byte
  packed layout (UE5.3, NOT a version signal); the "unverified" packed-reconstruction path
  is never seen in a real game.
- **UE5.6+ `FNameData` UEnum::Names container** (the struct-of-arrays whose enum bug the
  `Neu` reader fixed; e.g. Titan Quest II): `DynOff::bEnumNamesNewContainer` → **506**,
  added to the lazy `UE5_GetVersion` refine (the flag is set lazily by `DetectUEnumNames`,
  so it surfaces as the user browses, alongside the UE5.5 Utf8Str marker).

Full marker chain now: tagged FFieldVariant (5.3) → GravityDirection (5.4) → Utf8Str/Ansi
(5.5, lazy) → FNameData enum (5.6, lazy) → Object@+0x08 24B item (5.7, init). All raise-only.

-----

## 2026-06-27 — Movement Locate-in-GWorld coverage + Gravity Direction (UE5.4+) + camera nested-struct drill + UE-version upward reconciliation (build ~1808; DLL + UI; 2077 C# green; in-game VERIFIED on Elliot)

Follow-on to the Laufen movement work — Locate-in-GWorld for every pose/POV field,
the gravity-DIRECTION vector, two UX fixes, and honest UE-version detection.

### Locate-in-GWorld — full pose/POV field coverage
- Current Pose: added **Acceleration** (CMC.Acceleration), **Speed** (no own field →
  lands on Velocity), and **Rotation** (Controller.ControlRotation) locators.
- Camera POV: added **Location**, **Rotation**, **FOV** locators
  (APlayerCameraManager.CameraCachePrivate.POV.*, nested offsets resolved by reflection).
- All Locate button labels unified to the compact **"🌍 Locate"** / "⚙ Locate" style
  (Teleport tab + Instance Finder full-text); DataGrid icon-only buttons unchanged.

### Gravity Direction (Laufen, UE5.4+)
- New `Laufen::SetGravityDirection`/`ResetGravityDirection`/`GetGravityDirection` (FVector
  `GravityDirection`, normalized DLL-side, base-capture + re-assert worker). `UE5_SetGravityDirection`
  export; Mimic `CMD_MOVEMENT` **knobId=3** (3 doubles, (0,0,0)=off). Pipe set/reset +
  gravity_direction in get_movement_params.
- UI card: 3 **linear** X/Y/Z sliders (−100%…+100%, normalized), Apply/Reset/Refresh/Locate,
  state badge, global "Gravity Dir toggle" hotkey, CE-Lua/.CT export row. Graceful
  "Unavailable" on pre-5.4. Hint clarifies normalization (single-axis collapses to ±1 =
  direction only, not strength).
- Hotkey Settings: per-row tooltips for all 22 actions; Move Speed/Gravity toggle hotkeys.

### Camera Locate — nested-struct drill fix
The POV fields are nested two struct levels deep, so the locate landed on the
PlayerCameraManager parent. Fix: DLL sends the full drillable path
`CameraCachePrivate.POV.Location`; Live Walker `TryParseContainerPath` gained
`requireIndex:false` so a pure nested-struct path also drives `DrillDisplayPathAsync`
(its index<0 branch already drills struct fields). Now lands on the leaf.

### UE-version upward reconciliation (Genau/Frieren + Ubel)
Heavily-stripped games (Elliot) lose every version string → fall back to 4.27 despite
being UE5. Added a runtime post-process in `UE5_Init` (self-correcting every launch, no
cache delete needed): **tagged FFieldVariant** (structural, UE5.3+) → floor 503;
**CMC::GravityDirection** (property marker) → 504. Plus a **lazy** UE5.5 marker — `Ubel`
sets a flag when any walk sees a reflected `Utf8StrProperty`/`AnsiStrProperty`, and
`UE5_GetVersion` raises 504→505 off it. Elliot now reports UE5.4. (Packed FUObjectItem is
NOT a version marker — Avowed packs it at UE5.3.)

Tests: +5 (TeleportViewModel gravity-dir ×3, MovementScriptGenerator gravity-dir ×2,
DeepContainerChain struct-path ×1) → 2077 C# green.

-----

## 2026-06-26 — Movement tuning: Move Speed / Gravity / Super Jump (Laufen module; build ~1799; DLL + pipe + UI + CE-Lua mailbox; tests green, **in-game VERIFIED on Elliot-Win64-Shipping (UE4.27) + Avowed-Win64-Shipping (UE5.3, packed FUObjectItem)**)

New **Laufen** (走る / "to run") DLL module + three Teleport-tab cards that force
per-pawn `UCharacterMovementComponent` float knobs and hold them against per-tick
overwrites — the float analogue of **Solitar** (GodMode), which forces a bool bit.

### Engine (`Laufen.cpp/.h`)
- Self-contained (Path B): copies Solitar's local-pawn chain (GWorld →
  OwningGameInstance → LocalPlayers[0] → PlayerController → Pawn), then one extra
  hop to the reflected `CharacterMovement` ObjectProperty → the CMC.
- Generic **float-knob engine** over 3 knobs resolved by FName (DynOff rule):
  `KNOB_WALK_SPEED`=MaxWalkSpeed, `KNOB_GRAVITY`=GravityScale, `KNOB_JUMP`=JumpZVelocity.
- **Multiplier of a captured base** with the compounding/restore trap solved: base
  is captured once on activation and **re-captured only on pawn change** (respawn),
  never on a mere multiplier change — so re-applying never folds our own write into
  the base, and Reset restores the true original.
- **Re-assert worker** = Solitar's pattern verbatim (write-on-drift, ~250 ms,
  two-mutex split so `join()` never runs under the op mutex). `UE5_Shutdown` joins it.
- Width from reflected `FieldInfo.Size` (4B float / 8B double), never assumed.
- Graceful `MR_ERR_NO_CMC` on vehicle / custom-movement pawns (no CMC).

### Pipe (`Fern.cpp` + `Renge.h`)
- 3 generic commands (the `knob` string selects walk_speed/gravity/jump):
  `get_movement_params` (all knobs + each field's owner+offset for Locate-in-GWorld),
  `set_movement_multiplier {knob,multiplier}`, `reset_movement {knob}`. Pipe-only
  (UI path); CE-Lua/mailbox exposure deferred to P4.

### UI (Teleport tab — three cards, clone of the GodMode card)
- **P1 Move Speed**: 10%–1000% log slider on MaxWalkSpeed + Apply/Reset/↻/Locate.
- **P2 Gravity**: same on GravityScale (<100% floaty, >100% heavy).
- **P3 Super Jump**: persistent **toggle** (Force ON/OFF, like GodMode) + global
  **hotkey** ("Super Jump toggle"). Slider is jump **height** %; since apex height
  ∝ v², the applied JumpZVelocity multiplier is **√(height %)** (e.g. 400% height
  = ×2 velocity). Each card's Locate-in-GWorld reuses the existing value-landing
  `LocateValueInGWorld` handoff to mark the float in Live Walker.
- Log slider = `10^exponent`, exponent ∈ [-1,1], 100% at centre (VM `Math.Pow`,
  AOT-safe, no converter). Tri-state badge ON/OFF/Unavailable.

### Post-test UI polish (same session, build ~1795)
- Move Speed + Gravity got their own global **toggle hotkeys** (`movespeed_toggle`,
  `gravity_toggle` — apply current slider when off, reset when on; needed VM active-state
  tracking `_moveSpeedActive`/`_gravityActive`).
- Super Jump card gained a **Reset** button (off + snap slider to 100%, parity with
  Move Speed / Gravity).
- Hotkey-row label column widened 120→150 (the "Super Jump toggle" label was clipped).
- Renamed the hotkey section "Teleport Hotkeys" → **"Hotkey Settings"** + refreshed its
  hint (now covers God Mode / Super Jump / Move Speed / Gravity toggles, not just teleport).

### P4 — CE-Lua / .CT via mailbox (same session, build ~1799)
- DLL `Laufen::SetKnobPercent(knobId, percent)` — single source of truth for the
  "**100% = OFF**" rule + the jump height→velocity √. `UE5_SetMovementPercent` export.
- Mimic **`CMD_MOVEMENT=10`** mailbox (instanceAddr = knobId, paramsData[0..7] =
  double percent) + `HandleMovement`.
- `MovementScriptGenerator.cs` — stateful toggle AA records (tick = apply baked %,
  untick = 100%/OFF) poking the mailbox directly. Folded into the Teleport tab's
  bottom **"Add action records to CE" + "Save .CT"** (3 movement rows baked at the
  current slider %, alongside the 17 teleport records). Super Jump slider cap raised
  to **3000%** (height; velocity ×√30 ≈ 5.48, under the DLL ×10 cap).

Tests: +7 movement-VM + +7 MovementScriptGenerator (2071 C# green). Roster: `Laufen` 🟢.
**Still deferred:** UE5.4+ arbitrary gravity **direction** vector. Single-player only
(these properties replicate server-side online).

-----

## 2026-06-26 — Teleport per-vector "Locate in GWorld" (position + velocity) + opt-in Deep Locate-in-GWorld/GameEngine (build ~1780; DLL + UI + wire; tests green, adversarially reviewed, in-game UNVERIFIED)

Two requests, one session:

### Part 1 — Locate the position / velocity VECTOR in GWorld (not just the pawn)
The Teleport pose card already had a pawn "🌍 Locate in GWorld". Added two
sibling buttons — next to the Location row and the Velocity row — that land the
GWorld path on the *exact FVector field* rather than the owning pawn:
- **Position** → `RootComponent.RelativeLocation` (owner = RootComponent).
- **Velocity** → `CharacterMovement.Velocity` (owner = CharacterMovement; hidden/
  no-op on pawns with no CMC).

These addresses were already resolved internally by `Wirbel::GetPoseAndMovement`
/ `ReadMovementState`; the change just surfaces owner addr + field offset:
`MovementState` gains `LocOwnerAddr/LocFieldOffset` + `VelOwnerAddr/VelFieldOffset`
([Wirbel.h](../dll/src/Wirbel.h)/[Wirbel.cpp](../dll/src/Wirbel.cpp)), emitted in
`teleport_get_pose` as `loc_owner_addr/loc_field_offset/loc_field_name` (+ `vel_*`
when `has_movement`). UI: `TeleportPose` gains the fields, `TeleportViewModel`
adds a `LocateValueInGWorld(owner, fieldOffset, fieldName)` event +
`LocatePositionInGWorldCommand`/`LocateVelocityInGWorldCommand`, and
`MainWindowViewModel` wires it to the existing value-landing
`LiveWalker.LocateInGWorldAsync(owner, fieldOffset, fieldName)` (the same handoff
Value Search uses). GWorld-only (the owning components are normal world
sub-objects; GameEngine parity wasn't requested).

### Part 2 — "Deep" Locate-in-GWorld / GameEngine (the Value Search Deep analogue)
**Finding (confirmed):** neither Locate supported nested/multi-container, in two
ways. **Gap A** — `find_path_from_gworld`'s target→owner resolution used the
shallow `FindInContainers(addr,1)`; a bare value address in a separately-allocated
nested heap container returned `invalid_target`. **Gap B** — the forward BFS
(`EnumerateOutgoingObjectPtrs` → `GetClassRefMeta`) follows object containers and
inline-struct-nested pointers (depth 3) but NOT object pointers inside
struct-array ELEMENTS (`TArray<FStruct>` holding a `UObject*`), so such objects
were `not_reachable`. Both apply identically to the `root_kind="engine"` variant.

Both closed, opt-in (default off):
- **Gap A**: `find_path_from_gworld` now takes `container_depth`; when the shallow
  scans miss and `container_depth>1`, it attributes the value to its owner via
  `FindInContainersDeep` (mirrors `find_by_address`).
- **Gap B**: `find_path_from_gworld` takes `deep`; `EnumerateOutgoingObjectPtrs`
  gains a `deep` pass that walks `GetClassContainers` struct-element containers
  (array/set element + map value/key) and reads each element struct's object/weak
  pointers via `GetClassRefMeta(elemStruct)`, emitting each as ONE CE-splittable
  hop (`elem_stride` + `elem_value_offset` locate the pointer; `inner_type =
  StructProperty`). `PathStepToBreadcrumbs` extends its container split to
  `ArrayProperty + StructProperty`. **Bound:** one struct-element level — object
  containers nested *inside* the element struct (two levels) aren't a single
  splittable hop and are out of scope. Bounded by a per-container element cap +
  the existing deadline / visited cap.

UI: a **"Deep (nested containers)"** checkbox by the Locate-depth slider
(`LiveWalker.GWorldLocateDeep`, persisted), passed through `FindPathFromGWorldAsync`
(`deep` + `containerDepth=4` when on) at every locate call site.

Tests: +6 (Teleport locate-vector commands + `HasLocAddr`/`HasVelAddr`; deep
struct-array breadcrumb split). All green (2035 C# / 791 dll / 53 utf8). Published
exe launch-verified (no crash). In-game behaviour UNVERIFIED.

-----

## 2026-06-26 — Class Pivot post-capture freeze fixed: runaway connection-open loop + a robust class picker (build 1764; UI/C#+AXAML-only, no DLL/schema/wire change; in-game VERIFIED)

Selecting a class in Class Pivot froze the whole UI ("Not Responding", low CPU, heavy DB I/O)
— `CharacterAttributeSet` has 7 instances and the field query is indexed, so the cost made no
sense. Took several rounds (two wrong diagnoses) before the user's Process Monitor captures
nailed it.

### Root cause (ProcMon-proven, not what it looked like)
The `.db-shm` lock churn at offset 123 was all `Exclusive:False` + `Result=SUCCESS` (granted
every time) with ~135–375 `.db` `CreateFile`/`Close` per second, opens spaced <5 ms apart =
a **runaway loop re-opening a fresh `SqliteConnection`**, not lock contention and not WAL
bloat (the first two hypotheses — both wrong). The driver: the merge-1742 class picker was an
**`AutoCompleteBox` with a two-way `SelectedItem` binding**; its internal Text↔SelectedItem
reconciliation oscillated `SelectedClass` (item↔null) and re-fired `OnSelectedClassChanged →
LoadFieldsAsync` ~135×/sec. Every other `AutoCompleteBox` in the app binds `Text`, never
`SelectedItem` — this was the only one. (The query pages came warm from the pooled connection,
so there were zero data `ReadFile`s — which is what made the ProcMon trace look like pure lock
spinning.)

### The picker, done right (after two dead ends)
- `AutoCompleteBox` `Text`-bound (round 2) — STILL oscillated (object items + `ValueMemberBinding`).
- filter `TextBox` + `ComboBox` (round 3) — no freeze, but the dropdown **Popup dropped the
  click** after a per-keystroke `ItemsSource` rebuild (the exact bug the original merge cited).
- **filter `TextBox` + `ListBox` (shipped)** — a ListBox has no Popup, so a live-filtered list
  commits clicks reliably. Plus a VM-side immunity: `ApplyClassFilter` **skips the rebuild when
  the filtered set is unchanged** (`SequenceEqual`) and **re-selects a pick that survives the
  filter** — so a spurious post-click re-filter can't null `SelectedClass` and wipe the
  selection (the "datagrid flickers then clears" round-4 symptom). Typing the filter sets
  `SelectedClass=null` → no field load → **the DB is never touched while filtering**.

### Bundled hygiene (correct, but not the root-cause fix)
- `OpenAsync`: `PRAGMA busy_timeout=5000` (bounds any future lock wait instead of busy-spinning).
- `EnforceQuotaAsync`: now **always** folds the WAL with `wal_checkpoint(TRUNCATE)` (it used to
  skip it under quota, leaving a bloated `-wal`), off the UI thread.
- `LoadFieldsAsync`/`LoadClassesAsync`: write the per-`(snapshot,class)` cache **before** the
  supersession guard (immutable key → a superseded-but-complete result is still correct), so a
  re-fire collapses to cache hits.
- Per-class field lists load on a deliberate pick and cache permanently; a status readout shows
  `N classes · M field-set(s) cached · ~X MB heap`.

### UX
- The "Pivot target — Source · Snapshot · Class" panel is now a collapsible `Expander`.

UI/C#+AXAML-only. Tests **2029 C#** (added the supersession-cache + filter-preserve/clear
regressions). In-game verified: filter `attribute` → pick `CharacterAttributeSet` → 7 groups
projected, no freeze.

-----

## 2026-06-25 — Class Pivot "Suggest Targets" made usable: bounded N-snapshot discovery + shape ranking + UX (build 1742; UI/C#-only, no DLL/schema/wire change; NOT in-game verified)

The change-driven discovery front-door ("🔍 Suggest Targets") was **unusable** — clicking
Discover climbed UI RAM to ~11 GB and hung (killed via Task Manager). Root-caused and
rebuilt end-to-end, then verified bounded against the user's real 2.85 GB / 10.4M-row Elliot DB.

### Discovery OOM fix (headline)
`DiscoverChangesAsync` → `LoadIntersectedCandidatesAsync` materialised the **entire** older
snapshot's field set into an in-memory `Dictionary<string, Cand>` — and discovery runs with
**no class filter by design**, so on a big game that dict held tens of millions of entries
(~600 B each → 9–18 GB). Replaced (for the Strict path the front-door always uses) with
`DiscoverChangesSqlAsync`: one bounded SQL statement that pivots each identity's per-snapshot
values into columns server-side and returns only the **changed** instances. Process-memory
bound = changed-group count (thousands), not instance count (millions). Verified with the
actual bundled `Microsoft.Data.Sqlite` on the real DB: **229 MB peak / ~43 s** vs the old
11 GB hang. `cache_size=-65536`, **not** `temp_store=MEMORY` (the sort must spill to a temp
FILE). Cancellable via `sqlite3_interrupt` + the per-64k-row check. Loose/InSession fall back
to the old in-memory join (UI never selects them; preserves the public contract).

### N-snapshot (2–4) + shape ranking
- The Before/After 2-box picker is now a **2–4 snapshot checkbox picker** (`DiscoverSnapshotPick`).
- The bounded SQL generalises to N value columns; "changed" = NOT all N equal.
- **Shape ranking** (`PivotDiscoveryEngine`, weight 3.0): with ≥3 snapshots a field that
  changed in exactly ONE interval (a discrete in-game action) is boosted; a steady monotonic
  trend (a draining resource) is neutral; per-frame **jitter** (changed every interval,
  non-monotonic) is demoted. New **Shape** column. This is the "不變、不變、有變" / energy-bar
  §1b case — 2 snapshots can't separate a one-time event from constant noise; 3+ can.
  `class_counts` supplies the per-class Total (no extra scan). **Spawn-sibling consistency:**
  a `ROW_NUMBER` dedup keeps one physical sibling per (snapshot, identity) so the
  representative's hex/value/address can't be stitched from different siblings.

### Two pre-existing crashes surfaced during the user's in-game test (fixed)
- **`ClassPivotViewModel.RefreshAsync` re-entrancy** — `UiCollection.Reset(Snapshots,…)`'s
  detach re-entered `LoadClassesAsync → Classes.Clear()` during the Snapshots rebuild →
  Avalonia *"Source collection was modified during selection update"* (7× in the log); the
  snapshot list silently failed to refresh ("sees old data"). Fixed with a `_refreshing` guard
  (suppress the selection-driven load during the rebuild; trigger it once after).
- **Post-capture memory** — a multi-snapshot capture session sat at several GB (Workstation GC
  keeps the grown heap committed after parsing millions of transient field objects). Added a
  post-capture compacting `GC.Collect(Aggressive)` + heap/working-set telemetry; in-game the
  working set drops ~2.8 GB → ~1.4 GB after each capture (the observed 11 GB was the old build's
  bigger 68K-object captures without reclaim).

### Middle Class picker + result-grid UX
- The Class filter TextBox + ComboBox (whose `ItemsSource` was `Clear()/Add()`-rebuilt per
  keystroke, leaving the ComboBox unable to commit a click — dropdown closed, nothing selected)
  → an **`AutoCompleteBox`** over a stable list + `SelectedItem` (type to filter, pick commits).
- The Fields ↕ Results panes are now **resizable via a `GridSplitter`**, and the results grid
  gained a **filter box** (`ResultFilter`, case-insensitive substring over key + values).
- The result row gained **Locate in GWorld (🌍) / GameEngine (⚙)** buttons wired to
  `LiveWalker.LocateInGWorld/GameEngineAsync` (the SPC/Snapshot/RelatedObjects pattern).

Tests: dll 791/0; C# **2026/0** (new: `ChangeIntervals`, shape one-time-vs-jitter, classTotals,
N=3 stable→changed, N=4 shape rank, duplicate-id guard, spawn-sibling self-consistency, result
filter, Locate gating ×2). Files: `SnapshotStore.cs`, `PivotDiscoveryEngine.cs`,
`DiscoveryModels.cs`, `ClassPivotViewModel.cs`, `SnapshotViewModel.cs`, `MainWindowViewModel.cs`,
`ClassPivotPanel.axaml`, `en.axaml`. Design + both adversarial reviews ran as multi-agent
workflows. **Carryover:** in-game verify on the second device; if the post-capture GC reclaim
proves insufficient on huge games, a streaming `Utf8JsonReader` capture parse would cut the
transient allocation at the source.

-----

## 2026-06-25 — Class Pivot composite multi-field group key (build 1727; UI/C#-only, no DLL/schema/wire change; NOT in-game verified; **MERGED main PR #375** `73783ca`)

The Class Pivot tab can now group a snapshot class's instances by a **TUPLE of key
fields**, not just one — e.g. `(Team · Slot)` instead of a single `Team`. This is the
**aggregation-axis** "multi-value" the user asked for, NOT a group SCAN: it does not
touch the `Orden` SDR seam (that case stays served by **SPC Group** / **Snapshot
Group**, which the [evaluation](group-value-scan-spec.md) found already cover "find N
co-varying values at distinct offsets"). Pivot's unique value is grouping/projection,
so the multi-value extension lives on the group **key**.

**Additive design (single-key path byte-identical, all prior tests pass unchanged):**

- `PivotQuery` gains `List<string> KeyFields` + an `EffectiveKeyFields` accessor that
  falls back to `[KeyField]` when the list is empty — every legacy single-key call
  site is preserved.
- `PivotEngine.Build` computes a composite key via new `RenderCompositeKey`: each key
  field's rendered value joined by `" · "`; a **1-element key renders verbatim (no
  separator)** so it equals the old inline render exactly. A missing segment renders
  `(missing)` for that segment only.
- `SnapshotStore.PivotAsync` fetches **all** `EffectiveKeyFields` (parameterised
  `prop_name IN (...)` with a `!Contains` dedup) so no key segment can wrongly read
  `(missing)` for a non-fetched field.
- UI: the field grid gains a **Key** checkbox column (`PivotFieldPick.IsKey`) beside
  the existing **Value** column; the existing key-field ComboBox stays as the
  **primary** key. `ClassPivotViewModel.CollectKeyFields()` = `[primary] + ticked
  extras (grid order, deduped)`, populated only in Field mode. Status line + the
  `ResultsHint` / tooltip explain the composite key (Field mode only).

Composite key is **inert in Identity / DataTable / Snapshot Array modes** (those
`return` early in `RunPivotAsync` and never construct the Field-mode `PivotQuery`;
`PivotArrayAsync` builds `KeyMode=Identity`, so `Build` never consults the key list).

Adversarial review → **no material issues** (3 benign nits: Field+empty-key divergence
is unreachable behind `CanRunPivot`; Key column visible-but-inert in non-Field modes,
mirroring the always-visible Value column; separator-aliasing impossible — `Render`
only emits decimals/hex, never `·`). Tests **2009 → 2013 C#** (+3 `PivotEngine`
composite/missing/legacy-parity + 1 VM tick→tuple), `dll_helpers_test` 791/0
unchanged, AOT publish green (48.6 MB).

-----

## 2026-06-25 — Object Tree right-click → Instances direct search (build 1722; UI/C#-only, no DLL/schema change; **in-game VERIFIED**; **MERGED main PR #374** `ca2a73f`)

Adds two right-click menu items to the **Object Tree** that drive the **Instances**
tab directly, removing the copy-type → switch-tab → paste → Search round-trip the
user previously had to do by hand:

- **Find Instances (Type)** — pre-fills the Instances class field with the node's
  `ClassName` and auto-runs the search.
- **Find Instances (Type + Name)** — pre-fills BOTH the class field and the
  object-name filter (`ClassName` + `Name`, ANDed server-side) and auto-runs.

Pure reuse of the existing cross-tab handoff pattern (same shape as
`GameClassFilter` / `PropertySearch` → `InstanceFinder`):

- `ObjectTreeViewModel` gains two events (`NavigateToInstanceFinder` /
  `NavigateToInstanceFinderWithName`) + two `[RelayCommand]`s (null-node /
  empty-`ClassName` guarded). "Type" maps to `ClassName`, consistent with the
  existing **Copy Type** menu item.
- `InstanceFinderViewModel.SearchForClassAndNameAsync(className, objectName)` —
  twin of `SearchForClassAsync` but keeps the supplied name instead of clearing
  it (empty name degrades cleanly to a class-only search via the existing
  `SearchAsync` both-empty guard).
- `MainWindowViewModel` wires both events → switch to Instances tab + run, in the
  established `try/catch` + `_log.Error` style.
- `en.axaml` two new strings; `ObjectTreePanel.axaml` a `<Separator/>` + two
  `MenuItem`s in the existing `ContextMenu`.

Verified: UI build green, **2009/0** C# tests + C++ self-tests pass, AOT publish
clean (104.8 MB). Adversarial 3-lens review (correctness / AOT / consistency) →
0 must-fix. **In-game VERIFIED** — both right-click items switch to the Instances
tab and auto-run the expected search.

## 2026-06-25 — Window maximize/restore position fix across all UI windows (build 1718; UI/C#-only, no DLL/schema change; **in-game VERIFIED**; **MERGED main PR #369** `b1f9a50`)

Fixes two window-placement bugs the user reported across **all** snapshot-managed
windows (MainWindow + the 3 `ManagedDialogWindow` dialogs — PropertyXref / FunctionProps /
ObjectInstancePicker). Plain non-managed windows (`InvokeParamDialog`,
`ConfirmDialog`, `FreezeValueDialog`) use native OS restore and were never affected.

- **BUG 1 — "second restore jumps to 0,0":** Normal → Maximize → Restore (correct) →
  Maximize → Restore **again** landed the top-left at `0,0` (size stayed correct). Root
  cause is a **producer/consumer latch**: the *synchronous* re-apply (`Position = A` set
  inside the `Maximized→Normal` `WindowState` change, mid-un-maximize) emitted a
  maximized-origin (`~0,0`) `PositionChanged` transient — the **producer** (mechanism Y) —
  which then latched into the **unguarded** `_normalPosition` promotion (`CommitSnapshot`,
  size had a `>0` guard, position had none) — the **consumer** (mechanism X). The poison
  was written on cycle-1 restore and replayed on cycle-2 restore (the one-cycle-behind
  asymmetry).
- **BUG 2 — born-maximized restore off-screen (cross-restart):** exit while maximized →
  relaunch opens maximized (`AttachWindowState` sets `WindowState=Maximized` before show)
  → first Restore landed at the OS's garbage restore rectangle (e.g. `300,400`),
  partly off-screen. A window born maximized never gave Win32 a valid `rcNormalPosition`,
  and the synchronous re-apply lost the placement race against the OS.

**Fix (no timer, no P/Invoke, no startup flash):**
1. **Deferred re-apply** — replaced the synchronous re-apply with a single
   `Dispatcher.UIThread.Post(…, Background)`. It runs FIFO *after* the Win32 message burst
   (un-maximize reliably fires `WM_SIZE`/state→Normal before `WM_MOVE`), so it **overrides**
   the OS placement instead of fighting it mid-transition, and stops producing the `0,0`
   transient. Fixes BUG 1 *and* BUG 2.
2. **Position acceptability guard** (defense-in-depth) — the missing twin of the size `>0`
   guard. `NotePosition`/`Commit` (pure `WindowRestoreState`) and `OnPositionChanged`/
   `CommitSnapshot` (MainWindow inline) reject a top-left that isn't visible-enough on any
   current monitor via `WindowPlacement.IsVisibleEnough`. Deliberately does **not** reject
   an on-screen `(0,0)` corner (that's a legit park; the `0,0` bug is killed by #1).
3. **`OnRestoreReapplied()`** re-seeds `pending = normal` right after the re-apply so the
   `Position`/`Size` events it triggers aren't mis-read as a fresh user move.
4. `_closed` guards in both posted lambdas so a queued re-apply can't touch a torn-down
   window (no new disposed-object CTD); MainWindow's post is also gated on `!_restorePending`
   so startup validation stays the sole authority.

**Process:** root cause + design verified by a 3-lens design workflow (Avalonia event
ordering / regression skeptic / minimal-design) + synthesis; implementation adversarially
reviewed by a 3-lens review workflow → **0 must-fix defects**. Pure `WindowRestoreState`
keeps the new behaviour unit-testable (+5 tests: off-screen reject, on-screen `(0,0)`
accept, size/pos guard independence, `OnRestoreReapplied` anti-poison, null-screens
accept). Removed dead `_restoreMaximized` field. **C# 2009/0, dll 791/0, Native AOT
48.6 MB clean.** The deferred-reapply *timing* can't be unit-tested; **in-game verified**
by the user (double-max/restore on MainWindow + dialogs no longer jumps to 0,0;
born-maximized restore lands on-screen at the saved rect).

## 2026-06-25 — Live coerced-range preview after a Between / SPC absolute bound (build 1702; UI/C#-only; **MERGED main PR #367** `c0606c6`; NOT in-game-verified)

The Round/Trunc/Ceil rounding switch (build 1672, [PR #364](https://github.com/bbfox0703/UE5CEDumper/pull/364)) only showed a **static** hint of what a Between query *might* become (a hard-coded `10.9–11.1 → 11~11` example). Replaced that guesswork with a **live preview** computed from the values the user actually typed + the active mode, shown right after the bound box. Driving example (user request): Between `11.5~13.2` →

| mode  | preview |
|-------|---------|
| Round | `→ int 12~13 · float 11.5~13.2` |
| Trunc | `→ int 11~13 · float 11.5~13.2` |
| Ceil  | `→ int 12~14 · float 11.5~13.2` |

**Two interpretations** (mirroring `SnapshotNumeric.BetweenMatch`): an INTEGER field coerces each bound via the mode (`CoerceIntTarget`) → the *int range*; a FLOAT field with a fractional bound compares literally → the *float range* (the entered values). When **both bounds are whole** the two coincide and collapse to one range (`→ 11~13`). Type-specific Value Search scans show only their own interpretation (int-only for the integer widths, float-only for Float/Double/vector); the mixed numeric meta types, all group rows, and SPC (type-agnostic corpus) show **both**.

**Surfaces (all 5 — single + multi for every panel the user named):**
- **Value Search** — single (`BetweenPreview`, adapts scope to the selected DataType) + per-row group preview.
- **Snapshot** — per-row group preview (the multi-value Group path; single Diff mode is directional, no Between).
- **SPC** — single value-filter + group matrix, covering all absolute kinds `Exact` / `Between` / `≥` / `≤` (new `Matches` column in both DataGrids).

**Design.** One pure, AOT-safe helper `Models/RoundModePreview.cs` (`Between` / `SpcAbsolute` / `Point` / `Compose` + the canonical `Reduce`/`CoerceIntTarget` math) lives in the **Models** layer so per-row models (`GroupSlotInput`) and the SPC pick VM (`SpcSnapshotPick`) can recompute it **reactively** as the user types, with no Services dependency. It is now the single C# source of truth for the reduce/coerce math — `SnapshotNumeric.Reduce`/`CoerceIntTarget` **delegate** to it (kept behaviour-identical; all prior tests green). The preview is **display-only**: the actual scan always uses the panel-level rounding mode; the per-row/`SpcSnapshotPick` `RoundMode` is pushed in by the VM (on mode-change broadcast + `AddGroupRow` + pick rebuild + game-switch restore) purely to drive the row's own preview. Faithfulness confirmed against the rounding-aware `SpcEngine.AbsMatches` → `SnapshotNumeric` path (NOT the legacy `SpcAbsolutePredicate.Matches`). The original static `RoundingModeHint` line is kept as a general mode explainer; `str.Spc.Col.Preview` ("Matches") added to `en.axaml`.

Tests: new `RoundModePreviewTests` (headline 11.5~13.2 per-mode, scope adaptivity, whole-collapse, reversed/negative bounds, SPC kinds, bad-bound→empty, cross-layer delegation, per-row reactivity) → C# **2004/0**, dll_helpers 791/0; UI AOT publish green (48.6 MB). Adversarial review (4 lenses — math fidelity / reactivity / XAML+AOT+layering / completeness → per-finding verify): **0 confirmed / 0 raised.**

-----

## 2026-06-25 — Flatten GAS attributes in CE export + fix exit-while-connected CTD (build 1698; UI/C#-only; **MERGED main PR #366** `cdc0a32`+`2722d1a`; GAS flatten **in-game VERIFIED (Avowed)**)

Two independent UI/C#-only changes, one commit each — **no DLL change → no re-inject.**

**`feat(ce-export)` `cdc0a32` — Flatten GAS attributes (one level).** New opt-in Live Walker **Options → "Flatten GAS attributes"** (default off, persisted). When on, a GAS `FGameplayAttributeData` StructProperty collapses one level in **Copy CE XML / Copy CE Field**: its scalar `BaseValue`/`CurrentValue` children become **sibling leaves** named `HealthPoint ▸ BaseValue` at the **combined offset** (`+structOff+childOff`) instead of a nested parent group — fewer nodes, easier to read/edit in CE (triggered by Elliot/Avowed attribute sets whose every member is a 2-float GAS struct, producing very tall exports). Scoped strictly to `GameplayAttributeData` structs whose children are **all scalar leaves** (`structChildren.All(c => MapCeField(c) != null)`); any other struct keeps its normal group; **strict no-op when off** (short-circuit on the flag first). Detection (`IsGasAttributeStruct`) accepts both `GameplayAttributeData` (the live spelling — UE reflection drops the leading `F`, confirmed 16× in Elliot logs) and `FGameplayAttributeData`. Name build (per the user's 4 decisions): per-segment `+Offset` follows `DescShowOffset`; the struct type (`DescShowType`) is appended **once at the end** so the merged name reads cleanly → `HealthPoint (30) ▸ BaseValue (8) (GameplayAttributeData)`; separator `▸` (U+25B8); **not** Export CSX; persisted. Core in `CeXmlExportService` (`_flattenGasAttributes` ThreadStatic reset in all 3 `Generate*`; `EmitFlattenedGasStruct` + the `EmitFields` StructProperty branch); wired through both export buttons (AOB + direct) in `LiveWalkerViewModel`; persisted via `UiOptionsSettings.LiveWalkerUiOptions.FlattenGasAttributes` + `MainWindowViewModel` persist/apply/capture. **Correctness invariant:** the flattened leaf sits under the SAME parent group the old struct group did, and an inline struct needs no deref, so `parentResolved + (structOff+childOff)` is identical at any nesting depth. 5 new tests. Adversarial review (8 agents, 5 lenses → per-finding verify): **0 confirmed / 3 dismissed** (includeGuessed bypass — GAS children are never guessed; all-scalar gate flattening a subclass's scalars — still offset-correct; a test-coverage nit). **In-game VERIFIED on Avowed.**

**`fix(pipe)` `2722d1a` — CTD on exit while connected.** `PipeClient`'s `_reader`/`_writer` wrap the **SAME** `_pipe` with no `leaveOpen`, and all three teardown sites disposed reader → writer → pipe. `StreamReader.Dispose()` closes the shared pipe first, so `StreamWriter.Dispose()` then flushes to a **closed** pipe → `ObjectDisposedException` ("Cannot access a closed pipe"). On the **synchronous** shutdown path (window close → `App` ShutdownRequested → `PipeClient.Dispose`) that exception was unhandled → FailFast (`0xc0000409`) → **crash-to-desktop whenever the app closed while still connected to a game**; the async Disconnect/Connect cleanup paths threw the same way but were swallowed as *unobserved* task exceptions, so only the IDisposable path actually crashed. Recurred across builds 1640/1691/1696; the stack was visible only in the **Windows Application event log** (.NET Runtime 1026) + WER dumps, **not** the app logs (hard crash). Fix: a single guarded `CloseStreams()` used by all three sites — dispose the **writer first** (flush while the pipe is still open), then reader, then pipe, each wrapped to swallow `IOException`/`ObjectDisposedException`. Fixes the dispose ordering AND makes teardown non-throwing (a broken pipe from a closed game / dropped connection is expected, never fatal).

Tests: C# **1976/0**, dll_helpers 791/0, utf8 53/0; UI AOT publish green.

-----

## 2026-06-24 — Bookmarks survive a GAME RESTART: breadcrumb spine re-resolved from the live GWorld / GameEngine anchor (build 1690; UI/C#-only; **MERGED main PR #365 `2e54d86`**, **in-game VERIFIED**)

Fixed the reported bug where saved Live-Walker bookmarks all went **stale after a game restart** (UI restart while the game kept running was already fine). Root cause: a bookmark persisted both the navigation SPINE *and* saved memory addresses, and the load path walked the stale leaf address directly (`WalkInstanceAsync(lastBc.Address)`). After a restart, ASLR re-randomizes every pointer → the saved address is dead → the walk lands on garbage → `SavedClassName` mismatch → "stale — re-create". The persisted spine was *already* sufficient (each crumb stores `FieldName` + `FieldOffset` + `IsPointerDeref` + `IsContainerView` + `TargetClassName`); only the addresses were volatile, so **no schema change**.

**Fix** (`LiveWalkerViewModel.cs`): on every bookmark load, `TryReresolveBookmarkSpineAsync` re-walks the saved spine from a live anchor and rebuilds each crumb with a fresh address —
- **Root** — `GWorld` → `WalkWorldAsync().WorldAddr`; `GameEngine` → `ResolveGameEngineAsync().Address`. Other (address-rooted "Custom") roots → bail.
- **Pointer/struct hop** — `WalkForReresolveAsync` (no auto-fill-gaps; the named navigable fields come from reflection) → `MatchSpineField` (name+offset, then a UNIQUE name-only fallback for offset shifts) → follow `PtrAddress` / `StructDataAddr` (+`StructClassAddr`).
- **Container view + `[N]` element** — re-find the container field, then `ResolveContainerElementHop` derefs element N with the SAME address math as the `Populate{Array,Map,Set}ContainerFields` helpers (object ptr → `PtrAddress`; struct → `DataAddr + N*stride`; map `.Key`/`.Value`).
- `BreadcrumbItem` is init-only → `CloneCrumbWithAddress` rebuilds (never mutates `slot.SavedBreadcrumbs`). On success `_cachedWorld` is refreshed to the live world (null for an engine root). **Any** unmatched hop / null ptr / DataTable view / non-anchor root → return null → the caller falls back to the saved addresses (same-process fast path) and the existing class-name staleness guard reports honest staleness. **Safety property kept: never silently shows the wrong object.**

`BuildBreadcrumbSpineFromPath` now threads `rootKind` so **Locate-in-GameEngine** bookmarks persist the correct `"GameEngine"` root marker (previously hardcoded `"GWorld"`, so an engine-rooted spine would re-anchor on the wrong object and always fall back — adversarial-review finding).

**Adversarial review** (4 lenses → per-finding verify, 11 agents): 6 raised → **1 confirmed/fixed** (the `rootKind` threading above); the rest rejected (re-walk address math, container geometry vs the `Populate*` helpers, `_cachedWorld` refresh, breadcrumb immutability, DataTable bailout all check out). Tests: C# **1958 → 1971** (deep-container restart re-resolution, GameEngine-root re-anchor, hop-no-longer-matches fallback→stale, non-GWorld-root fallback, `MatchSpineField` exact/unique-name/ambiguous, `ResolveContainerElementHop` object/struct/null, `IsElementCrumb`, `ParseHexAddr`). Existing GWorld-root + PLV_game-deep-crumb regression tests still pass (re-resolution feeds the stub's default world / bails to fallback). AOT unaffected (UI-only). No DLL change → no re-inject needed.

-----

## 2026-06-24 — Explicit Round/Trunc/Ceil rounding mode replaces implicit float rounding + ± tolerance, across Value Search / Snapshot / SPC (builds 1672-1678; DLL + UI; **MERGED main PR #364 `b5419d3`**, **in-game VERIFIED (Avowed)**, needs re-inject)

Turned the *implicit* "a whole-number target matches any float that rounds to it" behavior (build 1648/1669) — and the rarely-useful free-form ± **tolerance** band — into an explicit, per-panel **rounding-mode switch `{Round (default), Trunc, Ceil}`**. A float/double is reduced to the integer the game **DISPLAYS** (HP `513.36`→`513`; progress `99.6`→`100`) before any numeric compare, so the user searches/compares by what they SEE. User-driven design (two clarifying rounds): per-**panel** independent + persisted switch (not per-mode); decimals on an integer field are **coerced** via the mode with a UI hint (`Between 10.9~11.1` → Round `11~11` / Trunc `10~11` / Ceil `11~12`); **vectors** fold in (per-axis, same logic); and **prev-value** predicates use the mode too ("did the *displayed* value change/inc/dec" — filters sub-display float jitter).

**Unified semantics (DLL `Radar` + C# `SnapshotNumeric` agree):** reduce = Round(half-away)/Trunc(toward 0)/Ceil(toward +inf). Exact/Bigger/Smaller/Between on a FLOAT field with a **whole** target → compare `reduce(value)`; a **fractional** target → exact-literal compare (full precision — positions/precise GAS). Integer fields stay strict; a fractional target/bound is coerced to an integer via the mode. Changed/Unchanged/Increased/Decreased → `reduce(cur)` vs `reduce(prev)` for floats (integers keep byte-exact hex in C# snapshot/SPC — no >2^53 regression; the DLL routes integers to the strict `ApplyOrdered`).

- **DLL** (`Radar.h/.cpp`, `Aura.cpp/.h`, `Orden.h`, `Fern.cpp`): new `enum RoundMode` + `ReduceRounded` + `CompareFloatScalar` (the single float predicate, reused per-axis by `CompareVectorPredicate`); `ComparePredicate`/`SlotSpec`/`ScanForValue`/`RefineCandidates`/`Orden::SlotTarget` carry `roundMode` (replacing `tolerance`); `BuildNumericTargets` + `ParseValueBytes` coerce a fractional value to an integer width via the mode. The 4 Fern handlers parse a `"rounding_mode"` wire field (per-slot for group; top-level for single). The old `tolerance` field is gone from the value/group wire.
- **C#** (`Models/FloatRoundMode.cs` + `FloatRoundModeWire`, `Services/SnapshotNumeric.cs`): `Reduce`/`ExactMatch`/`OrderedMatch`/`BetweenMatch`/`AtLeast`/`AtMost`/`TemporalMatch`/`CoerceIntTarget` — the reduce-aware funnel for `GroupMatch` (Snapshot single+multi) and `SpcEngine` (SPC single+multi, all 4 absolute kinds + the directional chain). `DumpService`/`IDumpService` send `rounding_mode`. 3 VMs gain `SelectedRoundingMode` + a `RoundingModeHint`; the panels swap the Tolerance NumericUpDown for a Round/Trunc/Ceil ComboBox (Value Search shows it for every type except Bool + the 3 string types; Snapshot/SPC always). Persisted per panel via `UiOptionsSettings` (`ValueSearch`/`Snapshot`/`Spc`.RoundingMode) + `MainWindowViewModel` ApplyOptions/BuildOptions/Persist sets.
- **Class Pivot — deferred** (recorded in [todo.md](todo.md)): Pivot does no numeric value-matching (groups by *rendered* strings; `PivotDiscoveryEngine.Direction` is raw-double), so a rounding switch is largely N/A. It reads the same captured corpus, so the build-1648 GAS nested-struct capture *should* surface there automatically — but that is UNVERIFIED.

**Adversarial review** (4 lenses → per-finding verify, 23 agents): 19 raised → **2 genuine, fixed** (DLL `Between` bounds now normalized like C#'s `Min/Max` — reversed `20~10` matched in C# but not the DLL; hardcoded `"Rounding:"` → `en.axaml` resources per the all-strings-in-en rule), rest rejected (the `SnapshotChunkAsync`-needs-`rounding_mode` claim was a false positive — capture stores raw values, rounding is applied at match time in C#; the enum-ComboBox-AOT claim is the same proven pattern the existing DataType/ScanType dropdowns use). Tests: dll_helpers_test 771 → **791** (Round/Trunc/Ceil per predicate + integer coercion + reversed-Between), C# 1932 → **1953** (ExactMatch/Ordered/Between/CoerceIntTarget per mode, integer fractional-bound ranges, wire `rounding_mode`, per-panel persistence, SlotSummary). AOT ~48.5 MB. **DLL change → needs re-inject.**

**In-game VERIFIED (Avowed, build 1678):** all four surfaces correctly find the player `Health` `GameplayAttributeData.BaseValue = 513.3599853516` (Value Search group Exact `513`; Snapshot group Between `510~514`/`512~514` cross-snapshot; SPC group Exact/Between). **Follow-up display fix found during verification:** the group "Matched values" master-row summary (`GroupCandidate.SlotSummary`) showed the QUERY target / Between *lower bound* (`=513` / `=510`) instead of the ACTUAL stored value — misleading now that an integer search matches a fractional float (`513`→`513.36`). Fixed to show `LeafValue` (the real, int/float-distinct value, e.g. `513.36`), matching the already-correct SPC group display. One-line change in `GroupCandidate.SlotSummary`; `LeafValue` was already populated on all three paths (DLL `leaf_value` via `FormatCandidateValue`; Snapshot `fVal[rep]`; SPC `Render(rep)`). Pure display, no DLL change.

-----

## 2026-06-24 — Value Search + Group/Multi Value Search: CE-style rounded float Exact (Exact 513 finds 513.36) — parity with the snapshot GAS fix (build 1669; DLL; on `dev`, NOT in-game verified, needs re-inject)

Follow-up to the snapshot "can't find a GAS attribute value" fix (build 1648). That fix had two parts: **(A)** descend direct `StructProperty` members to reach inner numerics (GAS `FGameplayAttributeData.BaseValue`), and **(B)** rounded float Exact matching (a whole-number target matches any float that rounds to it — `513` finds `513.36`). Question: is the same fix applied to **Value Search** (single + multi/group)?

**Investigation (3-reader workflow over `Aura::ScanForValue` / `ScanForValueGroup` + `Orden` + `Radar`):**
- **Part A — already present.** `Aura::ScanForValue` recurses direct `StructProperty` members for numeric (non-vector) scans (the `acceptedStructNames.empty()` gate, `Aura.cpp` ~6110), and the group path `Aura::CollectGroupLeaves` recurses them unconditionally (`Aura.cpp` ~7479). So both single and group already reach GAS `Health.BaseValue`.
- **Part B — was MISSING in both.** The DLL float Exact compare (`Radar::ApplyOrderedTol`, the `|cur - a| <= tol` band) had no whole-number rounding, so Exact `513` at tol 0 missed a `513.36` BaseValue. (The C# `SnapshotNumeric.ExactMatch` had the rounding; the live DLL scan did not.)

**Fix (DLL, one point):** `Radar::ApplyOrderedTol` Exact case now also returns true when the target is a whole number and `std::round(cur) == a` (half-away-from-zero, matching C# `MidpointRounding.AwayFromZero`). Single (`ScanForValue`) and group/multi (`Orden::LeafSatisfiesSlot` → `Radar::ComparePredicate`) both funnel through this one function, so both inherit it (plus deep-container + native-C scans). Only the absolute **Exact** predicate changed — Bigger/Smaller/Between and the prev-value refine predicates (Changed/Unchanged/Increased/Decreased) are untouched; integers stay strict (they route to `ApplyOrdered`, not `ApplyOrderedTol`); the Vector predicate keeps its own inline compare. A non-whole target keeps strict tol-0 equality.

Tests: dll_helpers_test 765 → **771** (single Float-Exact rounding cases reworked + a new `Test_Orden_RoundedFloatExact` group match/reject). C# 1932/0. AOT 48.5 MB. Adversarial review: clean — `Exact` is a first-scan type that always compares against a user-entered target (never a captured prev-value), so the rounding is always appropriate. **DLL change → needs re-inject.**

-----

## 2026-06-24 — Persist bookmarks + panel options; CE-export system-component filter; NuGet bump (builds 1652-1663; UI-only; MERGED main PR #359 `f63e009`; all in-game VERIFIED)

Three user-requested UI features (evaluated up front via an 11-agent survey→design→adversarial-review workflow, then a 7-agent option-classification pass for #3), each built / tested / in-game-verified / committed separately, then merged together. **All pure-UI — no DLL change, no re-inject.**

- **#2 — CE-export system-component filter** (`085f91f`). Live Walker **Options → "Skip system components"** (default ON): when recursively drilling pointer/struct children for **Copy CE XML / Copy CE Field**, skip system/engine asset fields (Widget, SoundBase, Texture, Material, ParticleSystem, Niagara, AnimInstance). New `Helpers/CeExportNoiseFilter.cs` — a client-side, name-only port of the DLL snapshot noise rule (`DecideSnapshotNoise`): no UClass super-chain at export time, so it strips any `/Script/X.` prefix to the leaf, applies the keep-base guardrail first (Actor/ActorComponent/Pawn/Character/Controller/PlayerState/GameInstance), then exact-matches an asset-leaf ban set (no substring → `WidgetSpawnerComponent` kept; under-exclude bias). The gate lives in `CeXmlExportService.EmitFields` as a **recursion-depth** check (`_emitDepth > 1`) so it only filters resolved CHILDREN, never the top-level fields the user selected — and it tests `PtrClassName` only, so a plain struct member (GAS `FGameplayAttributeData`) is never dropped. "N system fields hidden" status note. **Adversarial review caught the load-bearing trap:** the original "Copy CE Field is a single-field path, pass false" was fiction (both buttons share the same `Generate*` over the multi-selected set, and the `StructTypeName` fallback would delete selected rows) → replaced with the depth gate. In-game verified on a Texture2D drilldown.

- **#3 — Persist panel + Live Walker options across restarts** (`558905f`). New global `UiOptionsStore` (`ui-options.json`, cloned from `ExperimentalGate`) persists ~55 **stable** preferences (export toggles, scan/search behaviour, format dropdowns, limits, teleport offsets, proxy type). Scope was derived by classifying all **435 `[ObservableProperty]`** across the 20 panels (a 7-agent pass) — session-only state (search/filter boxes, selections, tab index, panel-collapse) and values owned elsewhere (snapshot quota + experimental opt-in via `ExperimentalGate`; UE-version override + invoke timeout via DLL per-game state; teleport hotkeys; per-game class denylists) are excluded. `Models/UiOptionsSettings.cs` = nested per-panel sub-objects whose defaults match each VM initializer; **no `DefaultIgnoreCondition`** (else turning OFF a default-true toggle stores the bool type-default and silently reverts to ON). Wiring: `App` → `MainWindowViewModel.InitializeOptionsPersistence` loads once + applies under a `_suppressOptionSave` guard (no save-storm), then `Track`s each VM's `PropertyChanged` filtered by a per-VM `nameof` set → 400 ms-debounced save; `FlushOptions` on shutdown. Persists the MASTER display controls (which fan out to children) not the per-child copies; applies `NativeCScan` before `NewestFirst` (coupled setter). **Adversarial fixes:** quota was already persisted globally (dropped from scope — dual-writer corruption avoided); the `WhenWritingDefault` revert. Snapshot capture options are persisted GLOBALLY this pass (not per-game) — sidesteps a connect-time load race; per-game is a follow-up.

- **#1 — Persist Live Walker bookmarks per game** (`3a55429`). Bookmarks survive a UI restart and auto-load when reconnecting to the same game — `bookmarks.{peHash}.json`, one file per game (new `BookmarkStore`, sync Load/Save/Delete, atomic temp+rename, defaults on corrupt, no-op on empty hash). Persists the navigation SPINE + selected rows + scroll anchor (not the live `ContainerField` / cached world). Slots **4 → 8**; right-click a slot to clear one, a new ✕ button clears all; the bookmark bar now shows when there's a live object OR any saved bookmark, so loaded bookmarks are clickable right after connecting. `MainWindowViewModel.ApplyEngineState` (the convergence point of both connect paths) loads the game's bookmarks; `LoadBookmarksForGame` self-clears in memory first (race-free), save is gated on the active PE hash + suppressed during hydrate. **Staleness:** a saved address stays valid while the SAME game process runs (ASLR is per-process), so a UI restart restores fully; after a game RESTART the load validates the walked class against the saved class and degrades to a "stale — re-create" message (kept on disk, stale selection dropped) rather than risk the wrong object — the GWorld actor-list root re-walks the world (a stable singleton) so it restores even across a restart. **A spine re-walk was deliberately NOT used:** GWorld→actor hops are list selections and actors have no stable identity across a restart, so replaying the path could land on the wrong actor (the exact "confidently wrong" trap the review flagged).

- **NuGet bump** (owner-initiated, in this PR): Avalonia 12.0.4 → **12.0.5** (+ Win32/Skia/HarfBuzz/Themes.Fluent/Fonts.Inter), Avalonia.Controls.DataGrid 12.0.0 → **12.0.1**, SkiaSharp 3.119.4 → **4.148.0** (+ NativeAssets.Win32), HarfBuzzSharp 8.3.1.5 → **14.2.0** (+ NativeAssets.Linux/WebAssembly), test `Microsoft.NET.Test.Sdk` 18.6.0 → **18.7.0** (+ Bcl.AsyncInterfaces, Newtonsoft.Json transitives). Verified: clean restore + AOT single-file publish (libSkiaSharp 11.1→11.6 MB, libHarfBuzzSharp 1.7→2.0 MB, exe ~104.7 MB) + full test run, all green. Runtime visual rendering with the SkiaSharp 4 / HarfBuzz 14 majors is owner-verified.

**Tests: C# 1908 → 1924** (+ `CeExportNoiseFilterTests` + 3 CE-export integration; `UiOptionsStoreTests` round-trip / corrupt / default-off regression / stale-tmp; `BookmarkStoreTests` round-trip / per-game / corrupt / delete; 4→8-slot; `StubDumpService.WalkWorldAsync` implemented). DLL self-test 765/0 (untouched). AOT publish OK.

-----

## 2026-06-24 — Snapshot Group: pre-fill "Compare with" when exactly two snapshots (build 1651; UI-only)

Small UX default: switching the Snapshot tab to Group mode with **exactly two** snapshots now pre-fills the `Compare with` picker (the OTHER snapshot) so Mode B (cross-snapshot temporal) is ready in one switch — previously it was left empty (Mode A). With 1 (no compare) or 3+ (ambiguous which to compare) it stays empty for the user to pick. One guarded line in `SnapshotViewModel.OnIsGroupModeChanged` (`GroupCompareSnapshot ??= the snapshot whose Id ≠ the primary`, only when `Snapshots.Count == 2`); the primary still defaults to the newest. C# **1878 → 1880** (+2 VM tests: two-snapshot pre-fill → Mode B; three-snapshot stays empty). AOT 47.8 MB. No DLL change.

-----

## 2026-06-24 — Snapshot captures GAS attributes (plain nested-struct numerics) + rounded float matching (build 1648; on `dev`, NOT in-game verified)

**Headline:** a user couldn't find a player's Health (`513.3599853516` on `AttributeSetCore.Health`, a GAS `FGameplayAttributeData`) in a snapshot Group scan — even Between 512-514 missed it. Root cause: the snapshot capture has three paths — top-level numeric **scalar** picks, **container** (TArray/TSet/TMap) struct elements, and Native-C **holes** — and a plain `StructProperty` member (not numeric, not a container, not a hole) falls through ALL THREE. So GAS `FGameplayAttributeData.BaseValue/CurrentValue` (THE #1 hack target, a struct member of every `UAttributeSet`) was never captured. The code even documented the gap (`Aura.cpp`: *"the object's own direct fields are captured by each caller's normal direct-field pass"* — but the snapshot's direct pass only captures numeric *scalars*, never struct members' inner scalars).

- **Part A — nested-struct capture (DLL, always-on):** new `Aura::CaptureDirectStructFields` reuses `EmitStructDirectLeaves` (the same struct descent the container path already uses for struct ELEMENTS) to walk each plain `StructProperty` member and emit its inner numeric leaves as scalar fields named `"Health.BaseValue"` at the OBJECT-relative offset, into `so.fields`. Found even without the "Deep" query toggle (they're scalar rows, not array rows). Also captures `FVector`/`FRotator` components (Location/Rotation). Respects scope + family per leaf; bounded by `kMaxStructDepth(4)` + a 512-leaf/object cap. Complementary to the scalar-picks + container passes (no double-capture: a field is either a top-level scalar, a container, or a direct-struct leaf). The capture grows ~30-50% (the user chose **always-on** over opt-in, managed via Type=Floats-only + the max-cap). Benefits SPC / Diff / Pivot too (same `fields` rows).
- **Part B — rounded float matching (C#):** float gameplay values display as integers but store as `513.36`, so searching `513` Exact should match. New `SnapshotNumeric.ExactMatch(v, target, type, tol)` — for float/double, matches exact-equality OR the ± tolerance band OR (whole-number target) any float that **rounds** to it (`Math.Round(v, AwayFromZero) == target`); integer fields stay strict (513 never matches 514); non-whole targets don't round. Wired into `GroupMatch.LeafSatisfiesSlot` (the reported path) + `SpcEngine` (new `AbsMatches`, keeping the Models→Services layering — `SpcAbsolutePredicate` stays in Models, rounding in Services). `SpcEngine.Matches` gained an optional `declaredType` (default "" → strict, so old callers/tests are unaffected); the two `SnapshotStore` SPC callers pass the field's declared type.

**Adversarial review** (DLL-capture / rounded-match lenses) → 5 findings, **3 confirmed all LOW, zero defects:** (1) build-tag off-by-one in comments (aligned); (2) the float-Exact loosening is the INTENDED GAS behavior — correctly gated to floats, integers strict, Diff/Pivot grep-confirmed unaffected; (3) empty `DeclaredType` degrades to strict Exact — fail-safe + non-reachable (capture always writes the type). Tests **DLL 765 / C# 1878** (+ `ExactMatch` matrix, the GAS two-float group-match scenario, the SPC rounding case; 2 pre-existing float-Exact tests updated for the new rounding semantics). AOT 47.8 MB. DLL change → **needs re-inject**.

-----

## 2026-06-24 — Snapshot capacity controls: type-family filter + sampled size estimate + opt-in max-dataset cap (builds 1621-1634; on `dev`, NOT in-game verified)

**Headline:** a user's Avowed snapshot ballooned to **6.7 GB** (unusable). Avowed has **266,366 objects** and the capture grabs every numeric UPROPERTY + every struct-array element (`arrayCap=256`) of every game-scoped object, stored one-row-per-field with fully-repeated `class_fqn`/`norm_path`/`prop_name`/`hex` TEXT in the `fields` table. The post-capture per-game quota can't bound a *single* capture (`EnforceQuotaAsync` only FIFO-evicts **older** snapshots, runs AFTER the whole capture is on disk, and always keeps the newest). Three opt-in controls now cut the bloat at the source and give the user a go/no-go *before* committing — picked by the user over a lossy "skip value==0" (rejected: breaks Diff/SPC/Group Mode-B's 0→non-0 / "stays 0" detection) and over a schema-normalization rework (deferred — bigger, all read queries become JOINs).

- **Type-family filter** (`All numeric` / `Integers only` / `Floats only`) — DLL-level, orthogonal to the numeric Scope. New `Aura::NumericFamily` enum + pure header-inline `NumericDataTypeInFamily` (Float/Double = float family; every Int8..UInt64 = integer family), applied at **all three** numeric emit sites: top-level scalar picks, `CaptureStructArrays` container leaves, `AppendRawHoleFields` Native-C holes. Wire param `numeric_family` (parse defaults to `Any` → back-compat). Floats = HP/MP/coords; integers = counts/flags/IDs → ~halves the row count for type-specific hunts. Lossy by design (re-capture to switch). `CaptureSnapshotChunk` has exactly one caller (the snapshot pipe handler) — Value Search / Group Scan use separate functions, so zero collateral.
- **Sampled size estimate** ("預估大小", pure C#, never writes the DB) — new `SnapshotSizeEstimate` models the actual insert row shape (incl. the `ix_fields(snapshot_id, class_fqn)` index's duplicated `class_fqn` per row). `EstimateSizeAsync` walks **5 windows spread across `[0,total)`** (avoids the low-index engine-cluster bias that `game_only`/noise-skip mostly drop) with the SAME options the real capture uses, extrapolates by scanned objects, shows `~X GB` + a cap-exceeded warning. A full dry walk would be nearly as slow as the capture itself (the walk dominates), so it samples.
- **Max-dataset cap** ("提前止血", opt-in `Off`/512 MB/1 GB/2 GB/4 GB) — the in-capture ceiling the quota lacks. `ICaptureSession.CurrentSizeBytes()` = committed db+WAL file bytes **+ an estimate of rows since the last commit** (the consumer's gauge — without the uncommitted term it lagged by up to one 16-chunk commit batch in the 256 MB page cache → overshoot; the estimate term, same model as the pre-flight, keeps it tight). On crossing the cap the producer stops **gracefully** (a `Volatile` flag, not a CTS cancel) and the partial snapshot is **finalised + KEPT** — distinct from the user-cancel path that deletes it.

**Log finding:** the user's 8.5 MB `UE5DumpUI/pipe-1.log` is full-body `Pipe RX:` logging of `get_object_list` — already fixed by the `LogBody` 1024-char cap (builds 1585+, PR #350); this user is on **build 1404**, so updating resolves it (no new code).

**4-lens adversarial review** (cap-dataloss / family-filter / estimate-accuracy / integration-AOT) → 12 findings, **3 confirmed (all LOW, fixed):** (1) comments wrongly said "no checkpoint runs during capture" — passive autocheckpoints DO run but recycle WAL frames in place without truncating the -wal, so db+wal stays monotonic (comment corrected); (2) the commit-batch overshoot (resolved by the uncommitted-bytes term above, not just documented); (3) stale `EstimateText` lingering into the next capture (cleared at capture start). No correctness / data-loss / AOT bugs. Tests **DLL 765** (+18 family `EXPECT`s) / **C# 1863** (+11: family wire×3, cap stop-and-keep-partial, `CurrentSizeBytes` uncommitted, 6 `SnapshotSizeEstimate`). AOT publish 47.7 MB. DLL change → **needs re-inject**.

**Follow-up (build 1640, in-game test feedback): cancel now reclaims the partial DB's disk.** A cancelled capture already `DeleteSnapshotAsync`'d the partial rows, but the multi-GB **file never shrank** — `DELETE` frees pages internally without truncating, and the prior `VACUUM` ran in **WAL mode where it can't shrink the main `.db`** (the compacted pages land in the -wal; a passive checkpoint never truncates the main file). So the space lingered until a Delete All. Fix: new `SnapshotStore.ReclaimDiskAsync` (checkpoint-TRUNCATE → `journal_mode=DELETE` → `VACUUM` → restore WAL) — VACUUM only truncates the file in a **rollback** journal mode. The cancel path calls `DeleteSnapshotAsync(id, reclaim: true)` off-thread with a "Removing incomplete snapshot…" message + a post-reclaim usage-bar refresh, ending "Capture cancelled — incomplete data removed." The same `ReclaimDiskAsync` replaces the ineffective WAL-mode `VACUUM` in **Delete All** and **quota eviction** (latent same-bug). Test `DeleteSnapshot_Reclaim_ShrinksDbFileOnDisk` asserts the file actually shrinks. C# **1863 → 1864**, AOT 47.8 MB.

**Hotfix (build 1642, in-game regression from the above): Delete All threw `database is locked`.** `ReclaimDiskAsync`'s `journal_mode=DELETE` switch needs EXCLUSIVE access, but Microsoft.Data.Sqlite **connection pooling** left idle handles open from the two just-finished capture sessions + list reads → SQLite refused the switch → the exception bubbled out of `DeleteAllSnapshotsAsync` BEFORE the VM's `Snapshots.Clear()`, so the list stayed stale and the DB wedged (Refresh then also errored). (The unit test missed it — a single pooled connection is exclusive; the app has several.) Fix: `ReclaimDiskAsync` now (a) `SqliteConnection.ClearPool(conn)` evicts idle pooled handles so it's exclusive, and (b) is **best-effort** — the rows are already deleted, so a lock is swallowed (logged, "file not shrunk this time") and **WAL is always restored in `finally`** so the DB never wedges in rollback mode. New test `DeleteAll_WithAnotherConnectionOpen_DoesNotThrow_ClearsData_DbStaysUsable` holds a second connection open to force the lock and asserts no-throw + data cleared + DB still writable. C# **1864 → 1865**, AOT 47.8 MB.

-----

## 2026-06-24 — xref UX overhaul: class-level "Find Func" + batch-inline xref columns + dialog Disassemble/Locate (3 rounds; builds 1602-1620; in-game VERIFIED on TQ2; MERGED main PRs #352 / #353 / #354)

**Headline:** two cross-reference pains, fixed. (1) The per-row `Props` / `Find Funcs` buttons each opened a **modal** → click → Close → next-row dance, and most results are 0 — the user paid a modal-open just to discover an empty answer. (2) Some UFunctions take a **whole class as a parameter** (not an individual property), so the per-field bytecode xref (`find_property_xrefs`) missed them entirely. Both addressed, then polished, across three feedback-driven rounds — each evaluated by a multi-agent workflow (recon → user decisions → phased build+test → adversarial review → fix), all merged after in-game verification on **TQ2 (UE 5.7)**.

**Round 1 (PR #352, builds 1602-1609) — class-level Find Func + Instances buttons + batch-inline.**
- **`Aura::FindFunctionsByClassParam`** + pipe `find_functions_by_class`: a pure-reflection scan of every UFunction's param chain matching each Struct/Object-family param's declared target-type pointer (`FSTRUCTPROP_STRUCT` slot, shared by `FStructProperty::Struct` / `FObjectPropertyBase::PropertyClass`) against the queried class. Because it's reflection (not bytecode), it **also catches native functions** — the exact gap `find_property_xrefs` (bytecode-only) leaves. Reuses the `FindPropertyXrefs` parallel-GObjects + deadline/`Tot`-cancel + gameOnly scaffolding; v1 = direct params/return only.
- `PropertyXrefDialog` gained a **class mode** (`ShowForClassAsync`) reusing the whole grid. Instances rows got **Name / Class copy** (mirroring Live Walker) + **Find Func**; `find_instances` now emits per-instance `class_addr`.
- **Batch-inline columns**: a tab-level batch button fills an inline `Uses` / `Funcs` column per row so the (often 0) result shows at a glance; the modal stays for detail. Interesting Funcs `⤋ Props` (cheap — one function's bytecode) / Property Search + Interesting Props `⤋ Funcs` (expensive — full GObjects sweep, warn>25). Cancellable + per-row progress; row models → `ObservableObject` for the observable cell.

**Round 2 (PR #353, builds 1611-1617) — batch skip-done + more surfaces + dialog actions.**
- **Batch semantics** finalized: operate on the keyword-filtered view (already the default), persist `XrefInfo` across filter changes (rows are reused), and **skip already-scanned** rows with a "(N cached)" counter. Filter `PlayerChar`→batch→clear→filter `Actor` only scans the not-yet-done; the overlap shows cached.
- **Find Func on more surfaces**: Classes tab (per-row + batch), Instances (batch with a run-scoped **ClassAddress→summary dedup cache** so 500 rows of one class = 1 sweep; warn on DISTINCT class count), Class Struct (header "🔎 Find Class Funcs"). No DLL change — reuses `find_functions_by_class`.
- **Dialog actions**: **⚙ Disassemble in CE** — new `Aura::GetFunctionCodeAddr` reads `UFunction->Func` (the native code entry, via Denken's lazily-detected `DynOff::UFUNCTION_FUNC`) via pipe `get_function_code_addr`, then AOBMaker `NavigateDisassembler` + `CreateMemoryRecord`. **🌍 Locate class** resolves a live (non-CDO) instance of the row's owning class → Live Walker GWorld. Wired via `PropertyXrefDialog` static `SharedAobMaker` + static event (a code-behind dialog can't take per-instance DI; MainWindow sets/subscribes once + unsubscribes in Dispose).

**Round 3 (PR #354, build 1620, UI-only) — tooltips + status styling.** Tooltips on every button of both xref dialogs (had none) via `ToolTip.SetTip`; the Disassemble hint is **conditional** on the AOBMaker bridge (connected vs not, button always-visible-but-disabled when absent). Instances StatusText/ClassFilterNote moved from the search toolbar into a dedicated row above the grid; all panels' status messages unified to one canonical style (`#AAB8D0` / FontSize 13 / monospace).

**Adversarial reviews caught 4 compile-clean-but-wrong bugs (all fixed):** (R1) `class_addr` emitted into the wrong DLL handler (`get_object_list` not `find_instances`) → Instances "Find Func" was dead for every row; cold `find_functions_by_class` read the **uncalibrated** `FSTRUCTPROP_STRUCT` estimate → silent false-negatives on shifted-layout games (UE5.7+) until any Class Struct/Live Walker walk → fixed with a `WalkClassEx(classAddr)` calibration up front. (R2) "Disassemble in CE" silently pushed the **shared interpreter** (`ProcessInternal`) address for Blueprint functions while reporting success (every property-mode row is BP) → fixed by gating `GetFunctionCodeAddr` on **`FUNC_Native` (0x400)** (version-aware FunctionFlags offset 0xC0/0xB0/0x98/0x88) so BP funcs return 0 → the honest "Blueprint-only" UI message; a static-event leak (handler not unsubscribed in Dispose) fixed.

**In-game VERIFIED on TQ2 (UE 5.7):** class-xref found `GetFromActor` (class-as-return-param) + all the `wbp_worldmap_C`/`wbp_fast_travel_*` functions taking `TQ2MapTooltipManager`; `HasMana` field xref; Disassemble in CE pushed a native func to CE's disassembler; Instances batch deduped "87 classes scanned across 231 rows (144 reused/cached)". DLL self-test 746/0, C# 1852/0, AOT clean. DLL-changed rounds (R1/R2) need re-inject; R3 is UI-only. v1 class-xref = param-type reflection; array/map-element + native-code-ref are future extensions.

## 2026-06-23 — Vendor sync: zydis 4.1→5.0 (bump-and-test, one-line break) + UE 5.8 readiness (new property-type decoders) (builds 1598-1600; A in-game VERIFIED on SEED+TQ2; MERGED main PR #351 `d0739db`)

A 12-agent audit of `vendor/` answered "what needs syncing?" and acted on the two real gaps. The reference clones **Dumper-7 / RE-UE4SS / UnrealEngine(release=5.8.0)** were all `behind=0` (no action; they're gitignored ref clones, not submodules — the "6.0 branch" = Epic's `ue6-main`, fetched-not-checked-out, deferred). The only thing actually behind was **zydis: 105 commits** (4.1.0 → 5.0.0).

**A — zydis 4.1→5.0 (bump-and-test).** Of 105 commits only **one** touches our compile surface: `ZydisDecodedOperandMemDisp::has_displacement` was removed (`.size`/`.offset` added). Denken (NativeDisasm / Path-2 `[this+off]` xref) is the sole consumer — migrated the single line `dll/src/Denken.cpp:155` `!op.mem.disp.has_displacement` → `op.mem.disp.size == 0` (width in bits, 0≡absent). No reachable correctness benefit today (APX/EGPR/SIB-32/`CalcAbsoluteAddressEx` are outside our 64-bit MOV/LEA + non-Ex path) — strictly future-proofing + off the unmaintained v4 line. **In-game VERIFIED on SEED + TQ2** via the log telemetry: v5 decodes 17-65 instrs/func, finds `[this+off]` accesses, maps properties, zero decode errors — the "mostly empty" Path-2 results are its nature (native constant-offset getters are rare), not a regression.

**B — UE 5.8 readiness.** The same audit's gap analysis (adversarially verified down to the code) confirmed UE 5.8 is **not architecturally blocked**: UObjectBase / UStruct walk spine / FField / FProperty / FBoolProperty / FNamePool are byte-for-byte == 5.5-5.7 in default config, and we're version-agnostic-by-detection. The one actionable gap = the new property-type family **`FUtf8StrProperty` / `FAnsiStrProperty`** (5.5+) + Verse `FVValue/FVRestValue/FVerseString/FVCellProperty` (5.8 UEFN), which both reference dumpers decode and we didn't — our value dispatch matched the type name with exact `== "StrProperty"`, so these correctly avoided a wide-FString mis-decode but produced empty/hex with no typed interpreter. Added `Ubel::ReadFUtf8String` (1-byte `TArray<char>` reader) wired into all three `== "StrProperty"` sites for `Utf8Str`/`AnsiStr`, and a safe `(Verse)` label for the Verse types (which wrap Verse VM cells / `Verse::FNativeString`, not plain FStrings). B is build+unit-tested + fail-safe but in-game-unverified (SEED/TQ2 don't use these types). DLL self-test 746/0, C# 1852/0. Both need re-inject.

## 2026-06-23 — Snapshot capture perf: the bottleneck was a DEBUG LOG (~50× faster after the fix) — telemetry → split → 1-line cap (Phases 0-2 + split + fix; builds 1585-1597; in-game VERIFIED; MERGED main PR #350 `ffc0f2c`)

**Headline:** the snapshot-capture bottleneck was **not** the object walk, the DB write, or the JSON parse — it was the `PipeClient` **`Pipe RX: {line}` debug log** writing the multi-MB `snapshot_chunk` JSON response body per chunk. In-game it was **98,075 ms of a 1m40s SEED capture (98%)**. Capping the logged body at 1024 chars took SEED from **1m40s → 2s** (verified: `walk 317ms / serialize 120ms / read 1363ms / rxlog 0 / jsonparse 56ms / build 480ms / write 843ms`). The whole point of "measure first" — every prior guess (walk, write, finalize, JSON DOM parse) was wrong; only the telemetry found it.

The path: **Phase 0 telemetry** (`walk/ser/parse/write` per chunk) showed `parse+pipe` was 97-99% of capture while the walk was 1-2s. A **split-telemetry refinement** (`PipeClient.ReadLoopAsync` times the line read / the `Pipe RX` debug log / `JsonNode.Parse`, injected into the response; `DumpService` times the model-build; reported as `[read / rxlog / jsonparse / build / other]`) isolated `rxlog` as the 98s. The **fix** (`PipeClient.LogBody`, build 1596) caps the RX/TX debug-log body — a huge payload logs a 1KB prefix + length, never the whole thing. `jsonparse` was 54ms (the DOM-parse rewrite I'd planned was unnecessary); `other`≈0 (the DLL `.dump()` + pipe are negligible).

A 3-layer perf workflow (verified against the real code) found the snapshot **capture** floor is the DLL per-object walk (already parallel + cursor-based — untouchable from C#), and the **fixable** costs are all downstream + persistence-preserving: a sequential (non-overlapped) fetch→write loop, a per-chunk connection + `EnsureSchema` + `synchronous=NORMAL`, and a finalize that runs a `COUNT(DISTINCT) GROUP BY ×2` (~10s freeze) + a `wal_checkpoint`/`VACUUM`. The **queries already run in-memory**, so a full in-memory DB was rejected (it changes no algorithm and would break the cross-session persistence that is snapshots' whole point). Plan: persistence-preserving wins only, phased + independently committed.

- **Phase 0 — telemetry** (`750b741`, DLL+C#, needs re-inject). `Aura::CaptureSnapshotChunk` times the parallel walk+merge (`walkMs`); the `Fern` snapshot_chunk handler times the JSON DOM build (`serialize_ms`); both ride the response. C# accumulates the full fetch round-trip + write `Stopwatch` per chunk and shows a compact `walk / ser / parse / write` breakdown in the capture status line + a final summary log line (`parse = fetch - walk - serialize`). Optimise on numbers, not guesses.
- **Phase 1 — overlap + single-connection bulk session** (`c2c4b2e`, C#-only). A producer/consumer pipeline (`System.Threading.Channels`, bounded 2) fetches chunk N+1 over the pipe while the consumer writes chunk N, so the DLL walk and the DB write OVERLAP. New `ICaptureSession` / `BeginCaptureSessionAsync`: ONE connection with bulk-load pragmas (`synchronous=OFF; temp_store=MEMORY; cache_size=-256MB`) + a transaction committed every 16 chunks, replacing the per-chunk open + `EnsureSchema` + fresh tx. The row-insert binding is shared (`ChunkInserter`) so the test/seeding `WriteChunkAsync` path stays byte-identical. A linked CTS makes cancel/failure propagate without deadlocking the bounded channel; `Progress` is computed on the UI heartbeat (no off-thread ObservableProperty writes). + a deterministic cancel-mid-stream test.
- **Phase 2 — finalize** (`846e752`, C#-only). **Incremental pivot count:** the session accumulates per-`(class, is_array)` distinct GObjects-index sets while writing, then `CompleteSnapshotAsync` writes `class_counts` directly — killing the ~10s GROUP-BY finalize freeze (the lazy GROUP-BY `EnsurePivotIndexAsync` stays as the fallback for non-session/old snapshots; golden test: incremental counts == GROUP-BY counts). **Skip per-capture checkpoint:** `EnforceQuotaAsync` estimates db+WAL+shm without a checkpoint and returns early when under quota, so a non-evicting capture opens no connection and runs no `wal_checkpoint`/`VACUUM`.

Underneath the headline, the Phases 0-2 wins still stand (persistence-preserving): **P1** producer/consumer overlap + single-connection bulk session (`ICaptureSession`, `synchronous=OFF`, commit-every-16) — the overlap hides the write behind the fetch; **P2** incremental pivot count (kills the ~10s `COUNT(DISTINCT) GROUP BY ×2` finalize freeze; golden-tested vs the GROUP-BY) + skip-checkpoint-under-quota. They're now a small slice of a 2s capture rather than minutes. A full in-memory DB was REJECTED (queries already in-memory; breaks cross-session persistence).

C# 1852/0; Native AOT publish clean (47.5 MB); `Channels` + the session trim-safe. **In-game VERIFIED on SEED:** 1m40s → 2s after the `rxlog` cap (build 1597). **Phase 3 (query row cache) is deferred / likely unneeded for capture** — capture is now at its floor; Phase 3 only matters if SPC/denylist *query* re-runs are slow (a separate question). Design of record: the approved plan + the perf-eval workflow + the split-telemetry finding.

## 2026-06-23 — SPC Query Multiple Values (Single/Group toggle): N-snapshot, object-aware group matching with per-slot predicate chains (builds 1575-1584; C#-only; adversarial-reviewed; in-game VERIFIED on SEED; MERGED main PR #349 `563037e`)

Brings the object-aware **Group Scan** to the **SPC Query** tab — the `Orden` reuse seam group-value-scan-spec §3.1 reserved for SPC (*"run Orden per object after the SPC load"*). Where the just-shipped **Snapshot Group Match Mode B** compares exactly **2** snapshots first-vs-last, **SPC Group** is the full **N-snapshot** generalisation: each of the 2-4 value-slots carries its **own per-snapshot predicate CHAIN** (the SPC directional chain `Any/Unchanged/Changed/Increased/Decreased` + a per-snapshot absolute window), and a match is one OBJECT whose N fields each satisfy their chain at DISTINCT offsets. The motivating case it can express that Mode B can't: *Current HP `·→↓→↑`* while *Max HP `·→=→=`* over `[full, damaged, healed]` — a per-snapshot pattern, not a single step. No DLL change, no DB schema change (pure C#).

**Engine** (`SnapshotStore.SpcGroupQueryAsync`). Reuses the SPC cross-snapshot intersection (`LoadIntersectedCandidatesAsync`, now with an opt-in `computeObjKey` that tags each surviving candidate FIELD with its object-identity key + GObjects index — zero cost for the existing single-value SPC / Discovery callers). Groups the surviving fields back into per-object blocks, then runs the matcher per object: each slot's matching-field list is built via `SpcEngine.Matches` (the same per-slot chain evaluator single-value SPC uses) gated by `GroupMatch.TypeInScope`, and `GroupMatch.HasDistinctAssignment` (the Kuhn SDR) requires a distinct field per slot. **Deep is inherent** — the SPC load already includes `array_field` rows (build 1203), so every struct-array element is its own candidate field grouped under its owner (no separate toggle). Emits the shared `GroupCandidate`/`GroupSlotMatch` so the panel binds the same master-detail template. 8 engine tests.

**UI** (SPC tab Single/Group `ToggleSwitch`, mirrors the Snapshot Diff/Group + Value Search Single/Group). The full N×M predicate matrix is editable without dynamic DataGrid columns: each `SpcSnapshotPick` holds a cell per value-slot, and the picker grid edits **one slot-column at a time** — a "Values" strip (per-slot width scope + add/remove) plus an "Editing: Value N" selector swaps which slot the grid's Compare + Value-filter columns write. Results = the master-detail `GroupCandidate` grid (per-slot chain glyphs `· ↓ ↑`, locked-offset table, Open-in-Live-Walker / Copy / Locate-in-GWorld+GameEngine handoffs reusing the existing same-session-address gates). 6 VM tests.

**Adversarial 4-lens review** (engine / regression / VM / UI; 10 agents, each finding independently verified) → **4 confirmed, all fixed**, 2 dismissed:
- **(MEDIUM, engine — also fixed in the shipped Snapshot Group Match)** the per-slot representative was `perSlot[s][0]`, not the leaf the SDR actually assigned → two slots could render the SAME field; and `Locked`/`MatchedOffsets` deduped by `PropOffset`, which collapses array elements (all at offset 0) into a false "🔒 locked". Fixed with a new `GroupMatch.Assignment` (recovers the slot→leaf SDR mapping) used as each slot's representative, and `Locked = (distinct matching fields == 1)` counting fields not offsets. Mirrored into the live `BuildGroupCandidate` (Snapshot Mode A/B) — the in-game-verified Tunes case is unaffected (each slot matched exactly one element). 2 new tests.
- **(LOW)** a dead `CancelGroupWork()` whose comment misdescribed the cancel path (cancellation already flows through `CancelPendingWork` → `_groupCts?.Cancel()`) — deleted.
- **(LOW)** a baseline row's group directional predicate wasn't scrubbed to Any like single mode (harmless — query-time forces index 0 = Any — but could re-surface after re-selection) — now scrubbed in `OnIsBaselineChanged`.

Plus one self-found edge fix: `RemoveGroupSlot` could leave `ActiveGroupSlot = -1` when the active slot is removed (the bound ComboBox's selected-item-removed path) → move selection off the doomed slot first.

**In-game VERIFIED on SEED (UE4.27).** 3 snapshots of the deep nested `BP_LifeSaveData_C` save data — `…WeaponTuneList[0].Tunes[1]` 15→15→**150** and `…Tunes[2]` 20→**21**→21 — with Value 1 chain `· = ↑`, Value 2 chain `· ↑ =`, Value 3 all-Any: SPC Group isolated the single object, both deep array slots locked onto the right elements (`Tunes[1]=150`, `Tunes[2]=21`), the all-Any slot rode along on `version_no`. Locked into a regression test (`ThreeSnapshotChain_DeepNestedTunes_PerSlotChains_TheInGameSeedCase`) that reproduces the exact shape. **UI follow-up (build 1584):** a prominent green "Now editing: Value N" chip beside the *Values (width scope per value)* header (computed `EditingSlotLabel`) so it's obvious which slot's column the snapshot grid is editing.

C# 1850/0; Native AOT publish clean (47.4 MB), no IL2026/IL3050. See [group-value-scan-spec.md](group-value-scan-spec.md) §3.1.

## 2026-06-23 — Snapshot Group Match S5: Deep mode (nested container / struct-array values) — driven by the SEED in-game test (build 1569; commit `382e759`)

First in-game test on SEED (the canonical `Tunes` case): the user changed `SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]` 20→21 across two snapshots and the group match `{Increased, Unchanged}` **missed it** (matched 3 direct-field noise objects instead). Diagnosis (verified against the snapshot DB): the value IS captured — 103,148 `Tunes` rows; `BP_LifeSaveData_C` has **16 direct fields but 52,257 array fields** — but S1-S4 matched DIRECT fields only (the `array_field` rows were excluded as "deep = SPC's scope"), so the `Increased` slot couldn't reach `Tunes[2]`.

**Fix — a "Deep (nested containers)" checkbox** (opt-in, default off; mirrors the live Group Scan's Deep toggle). When on, both `GroupMatchAsync` (Mode A) and `GroupMatchModeBAsync` (Mode B) include the captured `array_field` rows, folded into the OWNING object's block: the `SpcKey` join already keys on `array_field`+`elem_index` (so an element joins to its own counterpart across snapshots) and `SpcObjectKey` buckets them under the owner. The matched slot shows the full path (`Increased → SaveSlot[0]…Tunes[2]`). Array-element leaves use offset 0 (the heap element address isn't captured — the path is the identifier) + the owner `obj_addr` for handoffs, like the Diff array rows. `SnapshotGroupQuery.Deep` + `GroupFieldDisplay` (mirrors `SpcDisplayProp`) + `GroupDeep` VM property + the en.axaml checkbox. 2 tests (Mode A + Mode B Deep find the nested value; OFF misses) — the latter IS the user's SEED case. **In-game VERIFIED:** Deep ON + `{Increased, Unchanged}` matched `BP_LifeSaveData_C` with `Increased → SaveSlotList[1]…Tunes[2]=21`. *(Object-flat deep = array elements as the owner's leaves; array-AS-BLOCK, i.e. each array its own block like the live deep, remains a possible future refinement.)*

**Follow-up cross-session gate fix (the in-game test used previous-session snapshots):** the per-row **⚙ Locate-in-GameEngine** button was the only handoff NOT gated on session validity — it was decoupled from `IsGWorldAvailable` (correct: engine-locate needs no GWorld) but that wrongly dropped the SESSION gate too, so a stale cross-session obj_addr stayed clickable while Live / Copy / 🌍 correctly greyed out. Added `IsEnabled` = the session flag (`CanUseDiffRowActions` / `CanUseGroupRowActions` / `CanUseResultRowActions`, session-only — NOT GWorld) to all three snapshot-corpus ⚙ buttons: Snapshot Diff, Snapshot Group, SPC Query. Class Pivot has no ⚙ button. The live surfaces' ⚙ (Value Search / Instance Finder / …) operate on current-session addresses by construction, so they stay ungated. + a VM test (cross-session snapshot → all group handoffs disabled). C# 1835/0.

## 2026-06-23 — Snapshot Group Match: Multiple Values over the captured-snapshot corpus (S1–S4; builds 1563-1568; C#-only; adversarial-reviewed; in-game verify pending)

Brings the object-aware **Group Scan** (find objects holding ALL of N values at distinct numeric offsets, in any order) to the **Snapshot** experimental feature — the `Orden` reuse seam group-value-scan-spec §3.1 reserved, run in pure C#. Design of record: [snapshot-group-match-spec.md](snapshot-group-match-spec.md). Four phases, each committed:

- **S1 — C# `Orden` port** (`Services/GroupMatch.cs`, `9b541f0`). A faithful, AOT-safe port of the DLL's `Orden` SDR matcher (Kuhn's augmenting-path) + per-slot predicate eval. A `Leaf` carries the per-snapshot value SEQUENCE — `Hex[]` for exact/changed/unchanged byte compares, decoded `Num[]` for magnitude/direction — mirroring `SpcEngine`'s split. Absolute predicates (Exact/Bigger/Smaller/Between) evaluate the newest value; relative (Changed/Unchanged/Increased/Decreased) compare first-vs-last (need ≥2 snapshots). Width-fit + NumericNoByte/All scope gate eligibility. 17 unit tests.
- **S2 — engine + store, Mode A** (`SnapshotStore.GroupMatchAsync`, `b831f18`). Streams one snapshot's DIRECT numeric fields `ORDER BY (class, gobjects_index)` so each object's leaves arrive contiguously, runs the matcher per object, emits a `GroupCandidate` (reuses the live group's `GroupCandidate`/`GroupSlotMatch` models → the master-detail template binds directly). Struct-array element rows excluded (deep = SPC's scope). Reuses `ValueScanType`/`ValueScanDataType` so the UI binds the same pickers; the store translates to the matcher's slots. 6 store tests.
- **S3 — UI** (Snapshot panel Diff/Group toggle, `d01b586`). A `ToggleSwitch` swaps the lower area between the snapshot diff and a group section: a 2-4 row value grid (reuses `GroupSlotInput`) + master-detail results with the 🔒 locked-offset table + per-slot handoffs (Open in Live Walker / Copy / Locate in GWorld+GameEngine) reusing the Diff/SPC same-session-address events. 3 VM tests.
- **S4 — Mode B (cross-snapshot temporal)** (`8d1b4a2`). The motivating feature. A "Compare with" (older) snapshot picker lights up the relative predicates; `GroupMatchModeBAsync` hash-joins the OLD snapshot's direct fields by `SpcKey` (the SPC/Diff identity join), streams the NEW snapshot bucketing shared fields per object by a new `SpcObjectKey` (the object-identity portion of an `SpcKey`), and builds each leaf with the `[old, new]` sequence so relative predicates compare across time. **The "Current HP decreased + Max HP unchanged" case** — the unit test confirms only the damaged actor matches, not the level-up where both fields changed. Mixed relative+absolute slots allowed; 3+ snapshots rejected (v1 = first-vs-last 2-snapshot).

**Adversarial 4-lens review** (matcher / Mode A store / Mode B store / VM-UI; 13 agents) → 6 confirmed, all fixed in `aad2ed7`: **(HIGH)** the compare snapshot was silently dropped on every RefreshAsync → Mode B reverted to Mode A after a capture (now preserved by id); `CancelPendingWork` didn't cancel the group match → tab-switch/close left it running (now does). **(MEDIUM)** the Mode B session gate validated the primary snapshot but the rows' addresses come from the newer of the two → now validates `NewestGroupSnapshot` (mirrors SPC's `NewestSelectedSessionId`). **(LOW)** `IsGroupCompareMode` not re-raised on primary change; Exact tolerance is now a float band only. **Known limitation (documented, deferred):** the snapshot subsystem compares via `numeric_value` doubles, so integer values > 2^53 lose precision — a snapshot-WIDE trait shared by SPC/Diff/Pivot, not unique to group match; the deep 64-bit-exact refactor is out of scope.

No DLL change (pure C#); no DB schema change (reuses the `fields` table). C# 1832/0; full build green. **In-game verify pending:** capture two snapshots around a stat change, then Group mode + "Compare with" → confirm the HP-pair case (Current HP `Decreased` + Max HP `Unchanged`) isolates the actor.

## 2026-06-22 — Group Scan / Snapshot / deep Value Search: capture scalar-valued + scalar-keyed maps (`TMap<Name,int>`) — closes the P3 walker gap (builds 1561-1562; DLL-only; adversarial review 0-confirmed; in-game verify pending)

`Aura::WalkContainerLeaves` — the recursive container-leaf walker shared by **deep Value Search**, **Snapshot capture** (`CaptureStructArrays`), and **deep Group Scan** (`deepVisitor`) — previously emitted leaves only for a Map's STRUCT sides. A `TMap<K, scalar>` value (and the scalar key of any map) was silently dropped: `cfe.innerType` for a map is the `"K -> V"` arrow label (not a real leaf type) and the value lives at `slotBase + valueOffset`, not `slotBase`, so the old leaf-container branch correctly EXCLUDED maps (the build-1208 guard) rather than emit a malformed leaf.

**Fix — 3 edits, DLL-only, additive (no pipe/schema change, C# untouched).**
- `ContainerCacheEntry` gains `keyLeafType` / `valueLeafType` — the real property type names of a SCALAR map side (`TMap<Name,int>` → `keyLeafType="NameProperty"`, `valueLeafType="IntProperty"`); empty when that side is a struct (then the existing `keyStruct` / `valueStruct` is used) or for Array/Set (whose single type is `innerType`). Populated in `CollectContainersRecursive` from `f.keyType` / `f.valueType`.
- `WalkContainerLeaves`, AFTER the existing struct-side `for s` loop, emits one leaf per SCALAR map side: value at `slotBase + valueOffset` (path `<map>.Value`), key at `slotBase` (path `<map>.Key`), `leafName = ""` (the side IS the value). The `.Value` / `.Key` paths mirror the top-level static map scan (`sf.name = base + ".Value"/".Key"`) and never collide. Each side fires only when scalar (`*Struct == 0`), so a struct side — already handled by the loop — is never double-emitted.

**Byte-identical for everything else.** Struct-value/struct-key maps, `TArray<int>` / `TSet<int>`, and direct fields are unchanged (the new block gates on `*Struct == 0`; the struct loop + its `nSides`/tag path logic is untouched). Per consumer: deep Value Search now finds a value held in a NESTED scalar map value/key (top-level was already covered by the static `ScanContainer::MapKey/MapValue` path); Group Scan deep mode treats a `TMap<Name,int>`'s values as one block (`blockKey = <map>.Value`), keys as a separate `.Key` block, with the non-numeric `Name` key naturally filtered out of numeric scans; Snapshot captures the numeric map values within scope (depth ≥ 2, same top-level skip as `TArray<int>`).

Closes the group-value-scan-spec §3.2 P3 "scalar-valued maps" gap AND the Value Search "Proper scalar-map value/key capture" todo (one shared fix). utf8 53/0, dll 746/0, C# 1801/0; full build SUCCESS. **Adversarial 4-lens review** (lifetime / regression / consumer-correctness / map-layout, each reading the real code → per-finding verify) → **0 confirmed findings**. The 1 raised — snapshot SPC/Diff join uses the unstable TMap sparse-slot index for scalar map elements — was refuted (high confidence): the cross-snapshot SPC/Diff join keys on `(class, gobjects_index, prop, array_field, elem_index)` and NEVER on the inner key (the `inner_key_value` column is Pivot-only, a within-one-snapshot grouping), so struct-sided map elements **already** join on the same positional index. It's a pre-existing limitation of positional container-element joins, equally true for the already-shipped struct maps — not a regression. **DLL change → needs re-inject. In-game verify pending** (best on SEED with a `Map<Name,int>`, or any game with a scalar-valued map). See [group-value-scan-spec.md](group-value-scan-spec.md) §3.2 P3.

## 2026-06-22 — Live Walker button-layout pass: "AA" in the header (+ push-to-CE), Class / Outer / per-field "inst" buttons, address-field hint (builds 1550-1560; UI-only; MERGED main PR #346 `34f6a0c`; in-game VERIFIED)

A user-directed cleanup of the Live Walker toolbar/header buttons, plus one behaviour upgrade. Five changes, all UI-only (no DLL change, no re-inject):

1. **"Copy CE AA Script" → compact "AA" button**, relocated from the export toolbar row into the object-info **header** row (right of HEX / Addr / Name / Class), so the symbol-registration action sits with the object it registers. **New behaviour:** when the AOBMaker CE plugin is connected, the AA button now **pushes the script straight into CE's address list** (`IAobMakerBridge.CreateAAScriptAsync`, `autoActivate:false`) instead of only copying XML; it falls back to the clipboard `<CheatTable>` XML when the plugin isn't reachable, with an honest status line. The in-game-verified AA-script generators (GWorld-walk / register-symbol) were **left untouched** — a new `CeXmlExportService.ExtractAssemblerScript(xml)` pulls the raw `[ENABLE]/[DISABLE]` body out of the generated XML (CreateAAScript wants the raw script, not the wrapper), un-escaping `&lt;`/`&gt;`/`&amp;` (`&amp;` last). `symbolName` is sanitised to `[A-Za-z0-9_]`, so no entity corruption is possible.

2. **Address input got its missing hover hint** (`str.Tip.LiveWalker.AddressInput`). Audited (no code change needed) that `AddressHelper.TryNormalizeAddress` already handles a CE-copied `"module.exe"+offset`: `Trim('"')` strips the quotes, the module token is only used to *detect* the format shape (`Contains('.') || Any(IsLetter)`) so internal **spaces are harmless**, and `LastIndexOf('+')` tolerates a `+` inside the filename. Locked in with 5 new `AddressHelperTests`.

3. **"Class" button** added to the header row → copies the class name to the clipboard.

4. **Outer row** gained **HEX / Name / Class** buttons beside the existing Addr (mirroring the header row; the HEX hex-view nav is gated on the AOBMaker plugin).

5. **Per-field "inst" button** in the field grid's Name column → opens the field's pointed-to object class in the Instance Finder tab and runs the search (new `LiveWalkerViewModel.NavigateToInstanceFinder` event → MainWindow switches tab + `InstanceFinder.SearchForClassAsync`, the same handoff Interesting Funcs/Props/Property Search use). Visible only for object-pointer fields (gated on `LiveFieldValue.PtrClassName`, the pointee's live runtime class).

**Verification.** C# 1801/0 (+13 tests: 5 AddressHelper, 4 ExtractAssemblerScript, plus the relocated-button regression coverage), AOT 47.0 MB clean (no trim warnings). 4-dimension adversarial review (XAML / VM-wiring / AA-push / coverage → verify) returned 0 confirmed / 5 dismissed. Also cleared 6 pre-existing `xUnit1051` warnings in `LocateGWorldBannerTests` (pass `TestContext.Current.CancellationToken`). **In-game VERIFIED** — with the AOBMaker plugin connected, the header **AA** button adds the symbol-registration entry straight into CE's address list (no clipboard round-trip).

## 2026-06-22 — Locate in GameEngine: a GEngine-rooted ⚙ companion to Locate in GWorld on all 10 surfaces (builds 1542-1544; DLL + UI; MERGED main PR #345 `f488592`; in-game VERIFIED)

A companion to **Locate in GWorld** that roots the forward-BFS shortest-path search at the live **UGameEngine** instead of GWorld, so engine-layer objects (GameInstance / LocalPlayer / GameViewport / engine subsystems / UMG widgets) that no GWorld pointer chain reaches now get a real chain (GEngine → … → target). It surfaces as a small **⚙ icon button** sitting next to every 🌍 — ALWAYS icon-style even where the GWorld button is big-text (Instance Finder bottom toolbar, Teleport pose). This closes the long-standing "widget / GameInstance-owned value returns `not_reachable` from GWorld" gap (the Octopath `PartyCharacterPanel_C` case).

**Not a GWorld superset — a deliberate complement.** A GameEngine root sits one hop ABOVE the world, so for arbitrary world actors it is *weaker*: the streaming / World-Partition recovery `RecoverViaWorldLevel` (status `ok_via_level`) is hard-gated on a World root, so an actor that "Locates via GWorld (ok_via_level)" is typically `not_reachable` from GameEngine. ⚙ wins for engine-owned objects; 🌍 still wins for world actors. The user measured Phase-0 reach in-game before authorising the full rollout.

**One handler, a `root_kind` field — no new pipe command.** The existing `find_path_from_gworld` handler (`Fern.cpp`) gained a `root_kind` request field (default `"gworld"`); `root_kind=="engine"` resolves `rootObj = Genau::FindGameEngine().engineAddr` (already exposed by `resolve_game_engine`) and returns status `no_engine` when there's no live UGameEngine. **`FindObjectGraphPath` / `GraphPath.h` were untouched** — the BFS was already root-agnostic and the level-recovery self-gates on a World root, so no skip flag was needed. DLL change ⇒ re-inject.

**UI rollout to all 10 surfaces.** `LiveWalkerViewModel.LocateInGWorldAsync` gained a `rootKind` param + a `rootLabel` ("GWorld" / "GameEngine") threaded through every success / failure / log string, plus a thin `LocateInGameEngineAsync` wrapper (and `LocateContainerInGameEngineAsync` mirroring the container variant). ⚙ now sits beside 🌍 on RelatedObjects, Value Search single + group, Instance Finder selected + container-row, Interesting Functions, Interesting Properties, Snapshot diff, SPC, and Teleport pose. Engine commands are **not** gated on `IsGWorldAvailable` (independent; the DLL reports `no_engine` → banner). The shared failure banner's title now **auto-switches** via a new `LocateFailureTitle` VM property (`"Locate in {rootLabel} failed"`), replacing the fixed `str.LiveWalker.LocateFailedTitle` introduced with the GWorld banner one build earlier.

**Verification.** Phase 0 (DLL `root_kind` + the RelatedObjects ⚙ vertical slice, `c94f52e`) in-game verified; the full 9-surface rollout (`97c7a0e`, build 1544) was adversarially reviewed 0-issues and the Value Search ⚙ was in-game verified. dll 746/0 · utf8 53/0 · C# 1793/0. Open: `deadline_ms` is still hardcoded 20000ms in `FindObjectGraphPath` (the GEngine graph is larger) — optional follow-up.

## 2026-06-22 — Locate in GWorld: Interesting Properties 🌍 + a prominent Live Walker failure banner (builds 1531+; UI-only; MERGED main PR #344 `86fb765`; in-game VERIFIED)

An evaluation of where else **Locate in GWorld** applies, plus a fix for the confusing failure UX. The audit of all ~135 UI files found one clean gap — **Interesting Properties** — and confirmed the rest are correctly excluded (Property Search / Console / Pointer / Class Struct / Game Class Filter expose class metadata or static addresses, not graph-reachable instances; they already reach 🌍 via their Instance Finder handoff). Object Tree / Class Pivot / Live Walker per-field were judged MAYBE and left out per the user's scope decision.

**Interesting Properties 🌍.** The panel is the structural twin of Interesting Functions (which already had 🌍): its rows are class / property *definitions*, not addresses. So it reuses the same handoff — `LocateRowInGWorldCommand` raises the row's class name, MainWindow resolves a live non-CDO instance via `FindInstancesAsync(exact)`, then calls `LocateInGWorldAsync(addr, 0, null, stopAtParent:true)`. Gated on `IsGWorldAvailable` (pushed in both `SetEngineState` fan-outs, mirroring Interesting Functions). Per the user's "small icon, prioritized" request the 🌍 sits in a **leftmost dedicated icon column** — the other surfaces group it in a trailing actions strip, a deliberate user-directed divergence (a leftmost position is arguably more discoverable).

**Failure banner — the empty Live Walker no longer reads as idle.** A failed locate (`not_reachable` / `deadline` / `visited_cap` / …) used to call `ClearDisplayedNode()` (`HasData=false`), which showed the big 60%-opacity idle app-logo — visually identical to "nothing loaded yet" — while the actionable reason sat in an 11px low-contrast status line; worse, exceptions wrote to an `ErrorMessage` that isn't bound in this panel, so they were invisible. The fix adds `LocateFailureMessage` + computed `HasLocateFailure` / `ShowEmptyStateLogo`: a failure raises a prominent **⚠ warning banner** (title + full actionable reason) that takes over the empty area instead of the logo, and exceptions are routed into the same banner. A user-initiated **cancel** keeps the prior view + the mild status line. Banner→clear is a **structural invariant** via `OnHasDataChanged` — the banner is only ever raised while `HasData=false`, so any path that shows real data retires it, independent of the nav paths that set `HasData` directly and bypass `UpdateDisplay`.

**Verification.** UI-only (DLL unchanged, no re-inject). +8 tests (Interesting Properties command gating + banner lifecycle); `StubDumpService.FindPathFromGWorldAsync` was made `virtual` so the banner test can stub a path result. C# 1791/0 · AOT 46.9MB green. 4-reader survey + 3-lens adversarial review (0 confirmed bugs). **In-game VERIFIED by the user.** *(The banner's fixed title was generalized to the dynamic `LocateFailureTitle` by the Locate in GameEngine rollout one build later — see the entry above.)*

## 2026-06-22 — UE version detection cached per peHash — no more re-detecting stripped-version games on every connect (build 1521; DLL + UI; dev)

**Symptom.** Every connect re-ran the full UE-version detection. For publisher-stripped games (SquareEnix — Elliot, etc.) `DetectVersionDetailed()` falls through the cheap PE-VERSIONINFO path into the **slow memory string scan** (Tier 1/2/3 over the whole module image, 5+ s on large games). Re-running it on the *same binary* every launch is pure wasted time — the answer never changes.

**Root cause.** The HintCache already carried `ueVersion`/`versionDetected` and `Genau::FindAll` already had a cache-reuse branch — but it was gated on `publisher == nullptr && versionDetected == true`. SquareEnix games match a publisher thumbprint **and** detect as `versionDetected == false` (inferred), so **both gates failed → full re-detect every time**. The old comment justified this as "keep the badge honest" — but `DetectVersionDetailed` scans the module's **static image**, so it is deterministic per binary; the same peHash always yields the same answer. Re-running buys nothing.

**Fix — gate cache-reuse on a detection-logic revision instead of the publisher flag.** New `Genau::kVersionDetectLogicRev` ([Genau.h](../dll/src/Genau.h)) is stamped into each saved version (`Flamme::SaveResults` writes `versionDetectRev` + `lowConfidence`). The reuse branch now trusts the cache for **any** same-peHash game — publisher-stripped, inferred, low-confidence, all of it — whenever `hints.versionDetectRev == kVersionDetectLogicRev`, restoring `bVersionDetected`/`bLowConfidence` **verbatim** so the UI badge and the "set an override" nudge stay exactly as honest as a fresh detect. A logic change → bump the rev → every cache re-detects **once** and re-stamps. The constant is deliberately **decoupled from the build number** (a build-number gate would re-detect on every dev rebuild and defeat the cache for the very games it targets).

**The DLL↔UI write-ordering trap.** Both the DLL (`Flamme`, during `FindAll`) and the C# UI (`AobUsageService.RecordScanAsync`, after connect) write the shared JSON — and the UI write lands *second*. The UI deserializes into the strongly-typed (AOT source-gen) `AobUsageRecord`, which **drops any JSON field it doesn't model** — so without a matching C# field the DLL's `versionDetectRev` stamp would be erased on every connect, silently forcing a re-detect next launch. So `AobUsageRecord` gains `VersionDetectRev` (DLL-authoritative — **preserved, never assigned** by `RecordScanAsync`, exactly like `UEVersionUserOverride`) and `LowConfidence` (written from `EngineState.IsLowConfidence`). The rev never travels the pipe — it lives only in the cache file. Existing escape hatches unchanged: the per-game **Delete cache** button forces a fresh detect; a **UE version override** still wins over everything.

**Tests.** `AobUsageServiceTests` gains `RecordScan_PreservesVersionDetectRevAcrossRoundTrip` (the stamp survives the UI's read-modify-write) + `RecordScan_WritesLowConfidenceFromState`, mirroring the existing override-preservation test.

## 2026-06-21 — Snapshot: "Auto detect Engine/System noise" — skip engine/system classes at CAPTURE time (builds 1484-1486; DLL + UI; dev; in-game verify pending)

Snapshot already had a **Noise Picker**, but it only filters the *finished* snapshot at diff time (`DenylistScope.Diff`, store-side) — every object still enters the SQLite store, so it neither speeds up capture nor shrinks the DB. This adds a capture-time option that prevents pure engine/system classes from **ever entering** the snapshot, mirroring the auto-detect already used in Value Search + the Instance finder. **Default ON.**

**Approach B — inline, single-pass, no histogram pre-scan.** New `Aura::CaptureSnapshotChunk(... , bool skipNoiseClasses)` checks each class in the existing capture loop right after the `gameOnly`/`IsEnginePackage` skip ([Aura.cpp](../dll/src/Aura.cpp)), *before* the costly per-field work (`WalkClassEx` field enumeration, per-scalar `ReadBytesSafe`, recursive `CaptureStructArrays`, Native-C `AppendRawHoleFields`). A class-level `continue` there cuts the **dominant** per-object cost for noise classes (UI widgets, textures, sounds, Niagara, anim instances, `/Script` engine packages) and proportionally shrinks the DB. The verdict (`IsSnapshotNoiseClass`) is **`thread_local` memoized on the class pointer** so the bounded super-chain walks amortize across a chunk's many same-class instances (the capture loop is parallel). `continue` (not early-return) keeps the pager's `scanned` range contiguous — no index holes.

**The Pawn pitfall — a gameplay guardrail that always wins.** Because a capture-time skip is irreversible (the object never enters the store, unlike the reversible Picker tick), `IsSnapshotNoiseClass` force-keeps any class deriving from `{Actor, ActorComponent, Pawn, Character, Controller, PlayerState, GameInstance}` *before* any noise rule runs — so a player Pawn's X/Y/Z (and HP/MP living in components / GAS AttributeSets, kept via `ActorComponent`) is never dropped. The classifier rules already excluded these structurally (`kNoiseBases` has no Actor/Pawn/Character, ActorComponent is a documented hard-ban), but the guardrail makes the promise explicit and irreversible-safe.

**Single source of truth + testability.** The engine-noise leaf-base set is lifted to a header-inline `Aura::SnapshotEngineNoiseBases()` (shared with `ClassifyNoiseClasses`, behavior-identical), alongside `SnapshotGameplayKeepBases()` and the pure `DecideSnapshotNoise(keep, pkg, noiseBase)` precedence — all in `Aura.h` so the lightweight DLL test exercises them without linking the DLL. New `Test_SnapshotNoise_GuardrailAndSets` locks down the guardrail-wins precedence + the keep/noise set memberships (the *real* DLL-level Pawn-safety test — the prior `ClassFacetFilterTests` "Pawn" case was a C# mock that never touched the DLL classifier).

**Plumbing + UI.** Pipe `snapshot_chunk` gains `auto_skip_noise` (DLL default `false` for flag-unaware callers; the UI always sends it). C# `SnapshotChunkAsync(..., bool autoSkipNoise = true, ...)`, a `SnapshotViewModel.AutoSkipNoise` (default true, locked once per capture alongside `gameOnly`/`includeNative`), a checkbox next to "Game objects only" + tooltip explaining it differs from the post-capture Picker (source-level = irreversible; re-capture to undo). SPC/Pivot consume the same corpus, so they benefit automatically (user-confirmed decision). Adversarial implementation review: 0 correctness bugs (thread_local lifetime, precedence, refactor parity, all `SnapshotChunkAsync` call sites, AOT-safe bool serialization all verified); the one consistency nit (snapshot `gameOnly` to a local too) was applied. dll 746/0 · C# 1757/0 · AOT 46.9MB green.

## 2026-06-21 — Value Search / Group Scan: float/double render as fixed-point, never scientific notation (build 1483; DLL-only; MERGED main PR #339 `44bb943`, dev=main; in-game VERIFIED)

A large / garbage `FloatProperty` showed `5.73356e+17` in the Value column instead of plain digits. The DLL's `Radar::FormatScalarBytes` rendered float/double through the default `ostringstream` precision (`oss << v` = `%g`, 6 significant figures), which flips to the exponent form once the magnitude passes ~6 digits — unlike the Live Walker drilldown (`Ubel::InterpretValue`, `%.10f`/`%.15f`), which is always fixed-point.

**Fix — new `Radar::FormatNoSci(v, sigFigs)`.** It keeps the SAME 6 significant figures for in-range values (so normal hits like `1.39391` / `100` / `81.0702` stay byte-identical to before), but derives the decimal places from the magnitude (`decimals = sigFigs - 1 - floor(log10(|v|))`, clamped `0..17`, `buf[340]` for the worst-case ~309-digit double integer span) so the output is always plain fixed-point, then trims trailing zeros (dropping the dot when the fraction is all-zero; integer-part trailing zeros like `1500` survive because the erase starts at the dot). Non-finite values keep their `inf` / `-inf` / `nan` spelling.

**One change, two surfaces.** Both single Value Search (the `Fern` candidate `value`) and the multi-value Group Scan (`leaf_value`) format through the shared `Radar::FormatCandidateValue` → `FormatScalarBytes`, so the fix lands on both at once; `FormatVectorBytes12` (FVector / FRotator components) gets the same per-component treatment. The **numeric value sort is unaffected** — it compares via `DecodeNumericToDouble` (true numeric), not the display string; only the vector value-sort uses the string (already lexicographic, unchanged in spirit).

**The other panels the user asked to check were already safe.** Snapshot / SPC Query / Class Pivot all render through the C# `SnapshotNumeric.Render`, which uses the `"0.######"` custom format string — fixed-point by construction, so it never emits an exponent. No change was needed there (they carry slightly lower precision than the DLL path, but that was not the reported issue).

**Tests + verification.** +5 dll assertions (in-range exact strings `1.39391` / `100`, plus no-exponent checks for a huge float, a `1e20` double, and a `1e-7` tiny float). dll 727/0. Also removed the now-verified "Value Search 1M cap (V2)" todo (the owner confirmed a ~1M-cap scan runs OK). **In-game VERIFIED by the user.**

## 2026-06-21 — Export CSX: CE 7.7+ Binary (bit-switch) format behind a dropdown (builds 1478-1480; UI-only, no DLL/pipe change; in-game VERIFIED by the user)

CE before 7.7 has no bit-switch type in Structure Dissect, so a bit-field `BoolProperty` was exported as a whole `Vartype="Byte"` with the bit noted in the description (a series of same-address bytes). CE 7.7+ supports `Vartype="Binary"` (`BitStart`/`BitSize`), which Copy CE XML / Copy CE Field already emit. This brings CSX in line — **without** changing the legacy output.

**The Export CSX button is now a `DropDownButton` + `MenuFlyout`** (mirrors the toolbar Export/Tools dropdowns) with two items → `ExportCsxPre77Async` / `ExportCsx77Async`, both delegating to `ExportCsxCoreAsync(CsxFormat)`. New `enum CsxFormat { PreCe77, Ce77Plus }` (default `PreCe77`) threads as an optional last arg through `GenerateCsxAsync → EmitStructPropertyFlattened → EmitElement → BuildLiveChildStructure` (all three bit-field-bool sites funnel through `EmitElement`). `PreCe77` is the unchanged path; an early-return `Ce77Plus` branch emits a real bit switch via new `EmitBinaryBitfieldElement`, byte-identical to a native CE 7.7 export (`<Element Offset="90" BitSize="1" Vartype="Binary" BitStart="2" Bytesize="1" OffsetHex="0000005A" Description="bCanBeDamaged" .../>`).

**Correctness.** Only bit-field bools (`BoolBitIndex >= 0`) become `Binary`; whole-byte bools (mask `0xFF`) stay `Byte` in both formats. Byte address = the EmitElement **`offset` parameter** (already absolute — `field.Offset` is struct-relative for flattened-struct fields) **plus `BoolByteOffset`**, matching the DLL read/write path (`Ubel`/`Solitar`/`Wirbel` all use `base + Offset + ByteOffset`). `BitSize` is always 1 (UE `FBoolProperty` is single-bit). The Pre-7.7 `(bit N, mask)` suffix is dropped in 7.7+ (BitStart carries it).

**Two real bugs caught by the design's adversarial verify + a nested-struct regression test.** (1) The first-draft formula used `field.Offset` instead of the `offset` param → wrong byte for bools inside flattened structs; fixed to `offset + BoolByteOffset`. (2) `CeXmlExportService.ResolveStructRecursiveAsync`'s field reconstruction copied `BoolBitIndex`/`BoolFieldMask` but **dropped `BoolByteOffset`** — a latent gap that the `Offset="263"` nested-struct test exposed; now preserved (harmless to CE XML, required for CSX 7.7+).

**sample.CSX audit:** no new XML tags (only `Structures`/`Structure`/`Elements`/`Element`, all already emitted). Other gaps it shows — `Custom`/`Customtype="UE FName to String"` (FName→text), `ChildStruct` by-name references, `PreviewPriority`, per-field `String`, `RLECount` — are **separate backlog items**, not part of this change. Known pre-existing limitation: a bit-field bool inside a **struct-array** element stays `Byte` (`StructSubFieldValue` carries no bool mask).

**Tests + verification.** +4 `CsxExportServiceTests` (sample-exact Binary, `ByteOffset>0` address, nested-struct absolute-offset regression, non-bitfield stays Byte; existing Pre-7.7 bool tests unchanged + green). Evaluated via a 6-agent workflow (4 readers → design → refute-verify). C# 1756/0, AOT 46.9 MB green. **In-game (CE 7.7+) VERIFIED by the user** — the exported .CSX bit switches load correctly.

## 2026-06-21 — Live Walker "Copy CE AA Script" made restart-stable: GWorld-anchored Lua walk instead of a hardcoded leaf address (builds 1450-1474; UI-only; MERGED main PR #333 `544038a`, dev=main; in-game CE VERIFIED)

"Copy CE AA Script" used to emit only `define(Name, <abs addr>)` + `registersymbol` — dead after a game restart (ASLR). On a GWorld-rooted, forward-walkable path it now emits an Auto Assembler script whose Lua walks GWorld → … → the current object at *enable* time and `registerSymbol`s the result, so the symbol survives a restart.

**Three-case dispatch** (`LiveWalkerViewModel.BuildAaScript`, mirrors Copy CE Field's `useAob` gate). The gate is `spine[0].FieldName=="GWorld" && spine.Skip(1).All(FieldOffset>=0) && AddressesEqual(spine[^1].Address, CurrentAddress)` over `CleanBreadcrumbs(DedupeConsecutiveBreadcrumbs(Breadcrumbs))`.
- **A** — GWorld root + AOB available (AOB checkbox on): reuse `AOBScanModuleUE` to recover the `&GWorld` slot at enable time, register it, then the Lua walk. Restart-stable automatically.
- **B** — GWorld root + AOB off/absent (**respects the checkbox**, same as Copy CE Field): hardcode the `&GWorld` slot via `tonumber('<hex>',16)` — `EngineState.GWorldAddr` = pipe `gworld` = DLL `g_cachedGWorld`, i.e. the *slot*, so `readQword` of it yields `UWorld*` — then the walk; the user updates that value after a restart.
- **C** — non-GWorld root / a `-1` WorldLevel-recovery hop / a spine that doesn't land on the object: legacy hardcoded `define()`+`registersymbol()` (unchanged).

**New internal `CeXmlExportService.GenerateGWorldWalkedSymbolXml`**; extracted `AppendAobScanModuleUEHelper`/`AppendCloseLuaEngineHelper` from `BuildAobAssemblerScript` (byte-identical → Copy CE Field output unchanged). The walk reads `readQword` per `ProjectBreadcrumb` step from index 1 (the base deref replaces the GWorld root); pure C#, **NO DLL/pipe change**. CE Lua APIs verified against the repo's shipped scripts (`readQword`/`tonumber(hex,16)` are 64-bit-safe per `ue5_freeze_helper.lua`); `getAddress` intentionally unused (the walk uses an in-scope variable, not a symbol round-trip).

**Adversarial review** (3-dimension judge panel → refute-verify, 18 agents): **8 confirmed / 7 dismissed, all addressed.** The notable one was a real **HIGH** bug — CE `readQword` returns **nil** (not 0) on an unreadable page, so a mid-walk null (streaming / World-Partition) would run `readQword(nil + off)` and *throw* instead of cleanly warning → every hop now guards `addr and addr ~= 0` (the proven in-repo idiom from `ue5_freeze_helper.lua`). Also fixed: container leaf class names (`Array<int>` / `Map<…>`) corrupting the `<Description>` XML / `registerSymbol` name (new `SanitizeCeSymbol`, which fixes the legacy path too), the generator down-scoped to `internal`, and the status-note wording for the GWorld-but-not-walkable case.

**Tests + verification.** +15 `CeAaScriptGWorldWalkTests` (all 3 cases, the nil-guard, index-1 start, inline-vs-deref, container-view deref, DISABLE symmetry, the `-1` / spine-mismatch fallbacks); `MockPlatformService` gained `LastClipboard`. utf8 41/0, dll 722/0, C# 1752/0, AOT 46.9 MB green. **In-game (Cheat Engine) VERIFIED by the user** — the generated .CT registers the symbol and survives a restart.

## 2026-06-21 — Teleport tab: live velocity/acceleration readout + "Locate in GWorld" for the current-pose pawn (builds 1448-1450; DLL + pipe + UI; MERGED main PR #332 `de2216f`, dev=main; in-game VERIFIED)

Two Teleport-tab additions, evaluated up-front (5-reader workflow) then built per the user's decisions (do both, B-before-A, Path A, live poll, no-CMC = "unavailable").

**A — velocity/acceleration readout.** UE velocity/acceleration are reflected `UPROPERTY` FVectors: `UMovementComponent::Velocity` (authoritative, inherited by CMC) + `UCharacterMovementComponent::Acceleration` (CMC-only). New `Wirbel::ReadMovementState` resolves `c.pawn` → `GetCmc` → `Ubel::FindField("Velocity"/"Acceleration")` → `ReadVec3Mem` — pure reflected reads, **no invoke** (off-thread safe like the existing RelativeLocation read); the reflected `Size` auto-handles UE4 12B / UE5 24B LWC. `teleport_get_pose` now also emits `has_movement` + `vel_/acc_/speed` (cm/s, cm/s²), shown on the Current Pose card and refreshed by the existing 500 ms auto-poll. Pawns with no `CharacterMovement` (vehicles / custom frameworks) show "—" + a note instead of a misleading 0.

**B — Locate current-pose pawn in GWorld.** `teleport_get_pose` += `pawn_addr` (the resolved pawn). A new 🌍 button on the Current Pose card reads a fresh pose for the live pawn address, then fires `Teleport.LocateInGWorld` → MainWindowViewModel switches to Live Walker + `LocateInGWorldAsync` (reuses the existing find_path handoff). Gated on `CanOperate` only — not a C# GWorld flag (the build-1311 silent-no-op lesson).

**Notable fix (self-caught, review-confirmed).** `ApplyPose` is called from 7 sites but only the `teleport_get_pose` paths carry the new fields; folding movement into `ApplyPose` made Save-marker / directional-TP flip the readout to "unavailable" and blank the pawn address on a normal pawn → split into `ApplyPose` (pose only) vs `ApplyPoseAndMovement` (only the 5 get_pose callers).

**Tests + verification.** +8 `TeleportViewModelTests`. Adversarial review 0 confirmed / 9 dismissed. utf8 41/0, dll 722/0, C# 1738/0, AOT 46.9 MB green. **In-game VERIFIED by the user.**

## 2026-06-21 — Instances tab: server-side class-noise exclude-before-cap + full-pool histogram + configurable Max cap + temporary keyword filter (builds 1444-1447; DLL + pipe + UI; dev; in-game VERIFIED)

The Instances class-finder was effectively client-capped at 500: the cap in `Aura::FindInstancesByClass` is a scan-STOP (`results.size() < maxResults`), **not** a post-filter, so a wanted instance past the 500th match was dropped server-side *before* any filter ran. The class-noise `ClassFacetFilter` only hid classes *within* the returned 500 (client-side, histogram built from the capped list), so the real target — buried behind thousands of e.g. `StaticMeshActor` / `ActorBroken` rows — could never be reached by filtering. Fix = **Approach 4**: port the Value-Search P2 server-side class-exclusion contract onto the (stateless) `find_instances` scan, plus a configurable cap and a separate client-side keyword box.

**Evaluation.** Locked the design with a 14-agent workflow (4 parallel readers → 4 independent design approaches → a 4-lens judge panel → synthesis → adversarial verify). The verifier rejected the *draft* one-step plan while endorsing its direction, and surfaced six load-bearing details that were then built in (below). User confirmed all three recommended decisions: Approach 4 (hybrid), client-side keyword box, auto-debounced re-run.

**DLL (`Aura.cpp` / `Aura.h`).** `FindInstancesByClass` gained a trailing `excludeClasses` (EXACT, case-SENSITIVE `unordered_set` — deliberately **not** the case-insensitive substring used for the class *query*; folding case would over-exclude a game class sharing a substring) and a trailing `buildHistogram` flag. **Split loop:** the result cap now gates only `push_back` + the object-name read, while the class-match + a full-pool class tally run to completion — so the `class_histogram` counts *every* matched class even when its instances all sit past the cap (the histogram-vanish fix), and tallies **pre-exclude** + **post name-gate** so an excluded class keeps its true count and stays untickable. `truncated` is now per-path: `matchedNonExcluded > results.size()` for the histogram path, classic `results >= cap` for the cheap path. `buildHistogram` keeps the 6 internal callers (Wirbel/Edel/Solitar/Mimic/Frieren, `maxResults=100`) on the old early-exit so they aren't regressed into a full-array walk. `SearchResultSet` gained `classHistogram` (count desc / name asc) + `classDistinct`.

**Pipe (`Fern.cpp`).** `find_instances` parses `exclude_classes` (reused `ParseExcludeClasses`), clamps `limit` to `[1,50000]`, passes `buildHistogram=true`, and emits `class_histogram` (`HistogramToJson`, Top-40) + `class_distinct` — same wire shape as the value-scan begin/refine responses.

**C# service / model.** `FindInstancesAsync` gained `excludeClasses` (reused `AttachExcludeClasses`) + parses `ClassHistogram` / `ClassDistinct` (reused `ParseClassHistogram`); `FindInstancesResult` carries both.

**C# VM (`InstanceFinderViewModel`).** The `ClassFacetFilter` `onChanged` now re-RUNS the scan with `ExcludedClasses` when a class search is active (instant client re-project first for feedback, then a **debounce-by-cancel** re-run via `_reRunCts`), driven from the **server** histogram (`RebuildFromCounts`, which doesn't fire `onChanged` → no loop; `countsPartial:false` since the full-scan histogram is exact). A monotonic `_searchGen` guard drops a stale response; the manual `SearchAsync` does `ClassFilter.Reset()` first (stale-exclusion guard — a class hidden for "Pawn" must not silently filter "Inventory") and stores `_lastClassQuery/_lastNameQuery` for the re-run to replay; the reverse-address path bumps `_searchGen` + cancels the re-run + clears `_hasActiveClassSearch`. New **temporary keyword box** (`InstanceFilterText`) — a client-side `ObjectTreeFilter` over the returned rows (whitespace = AND across Name/Class/Address), 200 ms debounce Timer → `ApplyInstanceFilter`; the VM is now `IDisposable` (disposed by `MainWindowViewModel`). Configurable **Max** cap (`InstanceSearchCap`, default 5000).

**View / strings.** A `Max` `NumericUpDown` (100–50000) in the search row + a keyword `TextBox` above the instance grid; `str.InstanceFinder.{MaxResults,KeywordHint}` + two tooltips. The keyword box is cap-blind by design (narrows only what's on screen) — the tooltip points users at the ⚑ class filter (which re-runs the scan) to reach past the cap.

**Six adversarial-review fixes, all built in:** (1) histogram over the full array pre-exclude (else a *second* noise class behind the first re-buries the target and can't be unticked); (2) `ClassFilter.Reset()` between two different searches; (3) the re-run concurrency guard (`_reRunCts` + `_searchGen`); (4) `truncated` / capNote / `ClassFilterNote` semantics re-reasoned; (5) case-sensitive exact exclude; (6) the keyword debounce-Timer `Dispose` obligation.

**Tests + verification.** +3 `DumpServiceTests` (exclude sent / omitted-when-empty / histogram+distinct parsed). utf8 41/0, dll 722/0, C# 1730/0, AOT 46.8 MB green @1447. **In-game VERIFIED** (UE5 title): a class-only "actor" search shows the picker histogram with real counts (`ActorBroken` 8,406 / `StaticMeshActor` 8,046 — far past the old 500 cap), `engine package` auto-detect hints, ticking classes re-runs the scan to surface the wanted `BTS_TargetActor_C`, and the keyword box narrows the visible rows live.

## 2026-06-20 — Object Tree / Instances / Live Walker UX pass: multi-term tree filter + collapsible tree + Instances object-name search + Live Walker keyword memory (+ follow-up: collapse hit-test, nav-clear, panel hints, inst buttons) (builds 1419-1423; DLL + pipe + UI; dev; in-game verify PENDING)

Four user-requested UX improvements across the left Object Tree, the Instances tab, and Live Walker. Design forks were locked up-front with the user (multi-term single box; in-panel collapse + re-expand strip; server-side object-name filter).

**1. Object Tree (left panel).**
- *List-all reset* — confirmed the existing behavior already covers it: an empty `Search objects` box + the 🔍 button reloads the full list (`ObjectTreeViewModel.SearchAsync` → `LoadAsync`), as does Reload. Documented in the filter tooltip for discoverability; no logic change.
- *Multi-term filter* — the single text-filter box now ANDs whitespace-separated terms, each matching Name / Class / Address (case-insensitive). Typing `BP_ char` narrows to objects matching both, in any order — the "two-layer" filter without a second box. New testable helper `Helpers/ObjectTreeFilter.cs` (`SplitTerms` + `MatchesAllTerms`), used in `ApplyFilter`; placeholder → `Filter (space = AND, e.g. BP_ char)`.
- *Collapsible tree* — a ◀ button in the panel header collapses the whole left column so the right panels get full width; a slim strip with ▶ (in MainWindow column 0) re-expands. `ObjectTreeViewModel.IsCollapsed` + `ToggleCollapseCommand`; `MainWindow.axaml.cs ApplyObjectTreeCollapse` resizes the column (through the named `ContentGrid` — `x:Name` on a `ColumnDefinition` does NOT generate a code-behind field in Avalonia) and hides the splitter, remembering the user-dragged width for restore. The panel's `IsVisible` binds `!IsCollapsed` (resolves against the reassigned DataContext); the strip binds `ObjectTree.IsCollapsed` (against MainWindowViewModel).

**2. Instances tab — object-name search (server-side).** New optional "Object name" box ANDed with the class query, resolved DLL-side so it's *filter-then-cap* (doesn't miss matches the way a client-side filter on capped class results would). `Aura::FindInstancesByClass` gained a `nameFilter` param: empty class = match-any (name-only search), empty name = no gate, both-empty rejected by Fern. The returned list is still capped at `limit` (UI/transport practicality) — so a new `truncated` flag is threaded DLL→pipe→`FindInstancesResult.Truncated` and surfaced in the status text (`⚠ capped at N, narrow the search`); this also fixes the pre-existing *silent* truncation on the class-only search. Pipe `find_instances` gained `name_filter` + `truncated`.

**3. Live Walker keyword memory.** The field-search box is now an `AutoCompleteBox` fed from a per-session remembered-keyword list (`SearchHistory`). New `Helpers/SearchKeywordHistory.cs` implements the rules: LRU (max 8, most-recent first), **longest-valid wins** (a keyword that extends a remembered one replaces it; a prefix of an existing longer one is ignored — so the m→ma→mag→magi→magic typing run keeps only `magic`), and only keywords that returned matches and are ≥2 chars are kept. A 700 ms debounce on `OnSearchTextChanged` means only the keyword the user *settles* on is remembered.

**4. Live Walker search cleared on navigation** (final behavior — see follow-up). The field-search keyword is KEPT across tab switches and refreshes, and cleared only when the grid navigates to DIFFERENT data (drill-down / Back / Parent / breadcrumb / bookmark / world root / fresh walk). `LiveWalkerViewModel.ClearFieldSearchForNavigation` (flush-to-history then clear) is invoked from `UpdateDisplay(clearFieldSearch:true)` AND from the navigation command entry points that rebuild Fields directly and bypass UpdateDisplay (`NavigateToContainerAsync`, `GoBackAsync`, `GoToParentAsync`, `NavigateToBreadcrumbAsync`, `LoadBookmarkAsync`, `StartFromWorldAsync`); `RefreshAsync` passes `clearFieldSearch:false`.

**Adversarial review** (3-dimension judge panel + refute-verify workflow) caught two real defects, both fixed before this entry: (a) the object-name tooltip/doc-comment claimed "isn't limited by the result cap" while results silently capped at 500 → added the `truncated` flag + honest status/tooltip; (b) a type-then-tab-switch within the 700 ms window dropped the keyword → flush-before-clear + the debounce now schedules nothing for sub-minimum/cleared text.

**Follow-up batch (builds 1420-1423; UI-only)** — a second round of user feedback:
- **Collapse-button hit-test fix.** The collapse ◀ was unclickable when the `DisplayCount` text was long — in the old header `DockPanel` the count overflowed on top of the docked button and stole its clicks. The header is now a `Grid` (`Auto,*,Auto`): the count truncates (`TextTrimming=CharacterEllipsis`) in its own `*` column and the button sits in its own `Auto` cell, fully hit-testable.
- **Keyword-clear moved tab-switch → navigation** (see item 4 above). The first cut cleared on tab switch, but the user clarified the keyword should PERSIST across tabs and clear when the grid shows different data (drill-down / Back / Parent). The second adversarial review caught that the first nav-clear (in `UpdateDisplay` only) **missed the container drill-down, GWorld-root, and synthetic-container paths**, which rebuild Fields directly and bypass `UpdateDisplay` — fixed by also clearing at those command entry points via the shared `ClearFieldSearchForNavigation` helper.
- **Panel hints (hover tooltips)** added: Instances class-name + address fields, Properties name field, Object Tree search field, Value Search single/group switch, and the Clear buttons on Interesting Funcs / Interesting Props / Console / Teleport (hotkey).
- **"Find Instances" → "inst"** (Classes/`GameClassFilter` row button) renamed + given the standard `#7FB6E8` inst color (unified naming).
- **New "inst" buttons** on Interesting Funcs + Interesting Props rows → open the row's class in the Instance Finder and run it (`OpenInInstanceFinderCommand` → `NavigateToInstanceFinder` event → MainWindow wiring). All 6 cross-tab "inst" handoffs now route through a new `InstanceFinderViewModel.SearchForClassAsync` that clears the new `SearchObjectName` filter first, so a stale object-name can't silently AND into a handoff search.
- Stale tooltip fix: `str.Tip.LiveWalker.Search` no longer says "cleared on tab switch".

**All green:** utf8 41/0, dll_helpers 697/0, **C# 1692/0** (+34: `ObjectTreeFilterTests`, `SearchKeywordHistoryTests`, `DumpService` name_filter + truncated tests, 2 InterestingFunctions `OpenInInstanceFinder` tests), NativeAOT UI 46.6 MB, no trim warnings. New: `Helpers/ObjectTreeFilter.cs`, `Helpers/SearchKeywordHistory.cs`. Touched: Aura (.h/.cpp), Fern.cpp, IDumpService/DumpService, `Models/FindInstancesResult.cs`, ObjectTree/InstanceFinder/LiveWalker/InterestingFunctions/InterestingProperties VMs + panels, GameClassFilter/PropertySearch/Console/Teleport/ValueSearch panels, MainWindow (.axaml/.cs), en.axaml. **In-game verify PENDING** (multi-term filter; collapse click + width-restore; object-name search + cap warning on a large game; keyword LRU + clear-on-drill-down; inst-button handoffs).

## 2026-06-20 — Live Walker "Start from GameEngine" root + AOB option gated to GWorld roots + 2 option tweaks (build 1412; DLL + pipe + UI; dev; in-game verify PENDING)

Adds a second walk-root entry point next to "Start from GWorld": **Start from GameEngine** roots the Live Walker on the live `UEngine`/`UGameEngine` object so the user can drill `GameInstance` / `GameViewport` / `World` / subsystems from the engine down. No GWorld-style follow-on features are wired at this point — it's a pure alternate root, conceptually a fixed-target "Open in Live Walker". *(A GEngine-rooted **Locate in GameEngine** ⚙ — the forward-BFS follow-on this entry says isn't wired — was added later; see the 2026-06-22 entry.)*

**Resolution is by reflected member, NOT class name** — the class is `UGameEngine` in base UE but often a game subclass, and the literal name isn't guaranteed across versions. Reuses the exact mechanism already proven by `RecoverGWorldViaEngine`: the sole live non-CDO object whose class exposes a non-null `GameViewport` property (offset resolved via `FindPropertyOffsetByName`, cached per class). Refactored that engine-finding loop out into a shared `static FindLiveGameEngine(outViewport, outGvOff)` (RecoverGWorldViaEngine now calls it) and added public `Genau::FindGameEngine()` → `GameEngineInfo{ engineAddr, classAddr, className, gameViewportOk, gameInstanceOk }`. The `*Ok` flags validate the standard pointer members are present + non-null (the "this is the active engine, not a CDO / mid-boot stub" signal the user asked for). New pipe `resolve_game_engine` (Fern) → C# `IDumpService.ResolveGameEngineAsync` / `GameEngineResult`; `LiveWalkerViewModel.StartFromGameEngineCommand` walks the resolved address via `NavigateToAsync(addr, "GameEngine", 0, "GameEngine", isPointer:true)` — the `"GameEngine"` breadcrumb-root marker (≠ `"GWorld"` / `"Custom"`).

**AOB option now correctly gated to GWorld roots.** The AOB symbol anchors the export chain on GWorld, so it only makes sense from a GWorld root — but the checkbox's `IsEnabled` was previously gated only on global `GWorldAob` availability, leaving it enabled (and silently ignored) from non-GWorld roots. Added `IsRootGWorld` (recomputed from `Breadcrumbs[0].FieldName == "GWorld"` via a single `Breadcrumbs.CollectionChanged` subscription — covers navigate/Back/bookmark at once) + computed `CanUseAobSymbol = IsAobSymbolAvailable && IsRootGWorld`; the checkbox binds `IsEnabled` to it and auto-unchecks when leaving a GWorld root. **CE XML / CE Field export already worked from any root** (they fall back to a direct absolute-address anchor when `root != GWorld`) — the only loss from a GameEngine root is the restart-stable AOB anchor, which is exactly what the disabled checkbox now communicates.

**Two option tweaks:** default Live Walker array size `64 → 128` (`MainWindowViewModel._arrayLimitExponent 6→7`, plus the LiveWalker/InstanceFinder backing fields so startup matches before the slider syncs); Value Search + Group/Multi Value Search **Timeout** default `15s → 25s` and slider max `60s → 90s` (`ValueSearchViewModel._scanTimeoutSeconds`, the clamp, both ValueSearchPanel sliders, the tooltip). The timeout is one shared property across single + group modes.

**All green:** utf8 41/0, dll_helpers 697/0, **C# 1660/0** (+2 `ResolveGameEngineAsync` parse tests + 2 timeout-band tests updated to 25/90), NativeAOT UI 46.6 MB. New: `Models/GameEngineResult.cs`; touched Genau (.h/.cpp), Renge.h, Fern.cpp, IDumpService/DumpService, LiveWalkerViewModel, LiveWalkerPanel.axaml, en.axaml, ValueSearchViewModel/Panel, MainWindow/InstanceFinder VMs. **In-game verify PENDING** (confirm the button lands on the engine + AOB greys out off-GWorld roots).

## 2026-06-20 — find_by_address: stop the backward-scan false positive that surfaced 亂碼 in the Live Walker (DQ3 HD-2D) (build 1407; DLL + UI; dev; in-game verify PENDING)

Closes a garbage-name report on **DRAGON QUEST III HD-2D Remake** (UE4.27). The user value-scanned a stat (`1924178`), used the Instance Finder "find object" on the hit `0x19A97CAD230`, and the Live Walker showed the instance as `Property??IntProperty` with a **435-char CJK mojibake class name**. Root cause (from the game-process logs): the scanned value lives in **raw heap outside any registered UObject** (nearest real object `BackgroundBlur` was `0x2630` away — beyond its `PropertiesSize`), so `Aura::FindByAddress` fell back to its **backward memory scan**, which mis-identified the bytes at `0x19A97CAD210` (just `0x20` before) as a UObject. Walking that junk dereferenced an arbitrary `ClassPrivate`, whose FName header happened to have the **wide bit set + a large length**, so `Serie::GetString` decoded an ASCII asset-path buffer (`/Material/IMP_L_C01_Church_…`) as UTF-16LE → a long run of mojibake (each ASCII byte-pair → one BMP code unit, e.g. `"/M"` → U+4D2F 䴯). FNamePool/version were NOT at fault — sanity 10/10, every real object walked clean; the diagnostic `FName[1]='ne??ByteProperty…'` is a harmless synthetic-index over-read.

Three layered fixes (defense-in-depth — each closes a distinct hole the junk slipped through):

1. **Backward-scan candidate validation** (`Aura.cpp`). The old gate accepted any "printable ASCII" name — but `Serie::GetString` sanitizes junk bytes to `'?'` (0x3F, *itself printable*), so `Property??IntProperty` passed. New file-static `IsCleanFName` rejects any `'?'` / non-ASCII / empty / over-long result, and the scan now validates **both the object name AND the class name** (the junk class decoded to mojibake → `""` after fix #2 → rejected). Added a **containment gate** too: a backward hit must satisfy `addr - probe < PropertiesSize` — otherwise it's merely the nearest header, not the owner. Without it, rejecting the close junk header would let the scan latch onto the real-but-far `BackgroundBlur` (`+0x2630`) and mislabel it `"backward"`; now a genuine miss falls through to the honest low-confidence `"nearest"` path.
2. **`Serie::GetString` wide-name guard** (`Serie.cpp` + new pure `Utf8Helpers::IsImplausibleWideName`). The wide branch now drops an implausible decode (return `""`): `> 64` code units (no real wide FName is that long), or `> 24` units that are `≥75%` non-ASCII (the mojibake density signature). Preserves realistic short localized names (`< 24` chars). Header-only + unit-tested in `utf8_helpers_test.cpp` (+10 assertions: real `/Game/…`-as-UTF16 path rejected, short カタカナ / `HP体力` kept, length & density & NUL-termination edges).
3. **UI low-confidence handling** (`InstanceFinderViewModel`). A `"nearest"` result is no longer auto-selected/auto-walked (presenting a beyond-bounds object's fields implied the value lived there). It's now a clickable hint with a clear status: `⚠ Not inside any UObject — this value is likely raw heap / native data. Nearest is X at +0x… (click the row to inspect it anyway).` `"backward"` (a DLL-validated real subobject) keeps auto-walking.

Net: the DQ3 case now resolves to `"nearest BackgroundBlur"` with the ⚠ banner and no auto-walk — no mojibake reaches the UI. **All green:** utf8 **41/0** (was 31), dll_helpers 697/0, **C# 1658/0**, NativeAOT UI 46.5 MB. In-game re-verify pending on DQ3 HD-2D.

## 2026-06-20 — Locate-in-GWorld: reach streaming / World-Partition actors via the world's level list (ULevel::OwningWorld back-reference) (build 1405; DLL + UI; dev; in-game verify PENDING)

Closes the "Locate in GWorld returns `not_reachable` for streaming / World-Partition enemies" limit for the case where a target EXISTS in the world but isn't forward-reachable. Origin: the user observed on Elliot that weapons map (reachable — held by a reflected field on the reachable player pawn) but enemies don't. Verified the real shape: `ULevel::Actors` (`TArray<AActor*>`) IS already collected into the BFS's `objectArrays` and walked, so `GWorld → PersistentLevel → Actors[i]` is already traversed — the genuine gap is actors whose **owning `ULevel` is itself not forward-reachable** (streaming sub-levels / WP runtime cells held via non-reflected structures).

**Key insight (no forward pointer needed):** an `AActor`'s Outer IS its `ULevel`, and `ULevel::OwningWorld` points back at the world. So `Aura::RecoverViaWorldLevel` reaches the owning level by that **back-reference**: climb the target's Outer chain to the first object whose Outer is a `"Level"` (that object is the owning actor; works for a component / AttributeSet target too), confirm `OwningWorld == rootWorld` (guards multi-world / PIE), find the actor's index in `ULevel::Actors`, and emit `world →(WorldLevel back-ref)→ ULevel → Actors[k] → actor [→ … → target]`. The `world → level` hop is synthetic (`fieldType "WorldLevel"`) because — by construction — if the level were forward-reachable the actor would have been too (`level → Actors → actor` is reflected), so the plain BFS would already have found it. The `level → Actors[k]` hop is a REAL object-array element edge; the optional `actor → … → target` tail is a bounded forward BFS for an owned sub-object (so "locate the streaming enemy's AttributeSet/HP" lands end-to-end). Wired into `FindObjectGraphPath` as a fallback that runs ONLY on a clean `not_reachable` (never overrides deadline/cancel/cap, never re-runs the heavy work on success); new result status **`ok_via_level`**.

**UI:** `LocateInGWorldAsync` already keys off `path.Found`, so the recovered chain builds the breadcrumb spine + lands on the actor with no new plumbing. `PathStepToBreadcrumbs` renders the synthetic `WorldLevel` step as a plain navigation anchor (navigate by Address, `IsPointerDeref=false`) so CE export doesn't fabricate an offset for a hop that has none; a `GWorldViaLevelNote` suffix tells the user the world→level hop is a back-reference, not a static pointer; the `not_reachable` message now notes the level list was also checked. **All green:** utf8 31/0, dll_helpers 697/0, **C# 1658/0** (+1: `PathStepToBreadcrumbs_WorldLevel`), NativeAOT 46.5 MB. Honest limit: a streaming-actor chain is NOT a clean CE static-pointer chain (the world→level hop is a back-reference) — it's for in-tool reachability / Live Walker navigation / drilling to HP, which is the original goal. A truly unreferenced actor not in any world level still correctly returns `not_reachable`.

## 2026-06-20 — Related Objects Phase 2 (Edel): auto-detect the actor the player is currently targeting + Phase-1 review hardening (build 1400; new DLL module + pipe + UI; dev; in-game verify PENDING)

Closes the second half of the original user question — "how do I know which enemy is *currently focused*?" — so the user no longer has to guess a class-name keyword in Instance Finder (the TQ2 test hit exactly this pain: `Actor`→FX noise, `creature`→templates/CDOs). **Phase 1** = given a known actor, expand its related objects (shipped builds 1323-1329). **Phase 2** = auto-detect the target actor and feed it into that Phase-1 panel.

**New DLL module `Edel`** (艾德爾 — Hypnosis, "noble"; roster ⬜→🟢) — `Edel::DetectCurrentTarget(maxCandidates)`. Resolves the player chain (GWorld → OwningGameInstance → LocalPlayers[0] → PlayerController → Pawn — a small self-contained read-only COPY of Wirbel's teleport chain, deliberately duplicated to keep Edel decoupled from Wirbel's `Chain`/`TP_ERR` vocabulary; same engine-stable field names + instance-scan + DebugCamera-hop fallbacks), then enumerates the outgoing object pointers of {PC, Pawn, and their depth-1 owned ActorComponents} and **scores** each by *structural-gate-then-keyword*: a candidate is kept only if it walks like an `AActor` (a bounded super-chain FName walk — `ClassifyBySuperChain`, version-agnostic), the player's own PC/Pawn are excluded (seed the dedup set with `{pc,pawn}` — the #1 correctness guard), and the English keyword table is a SCORING BOOST not a gate (so it degrades to a ranked list on non-English / obfuscated games instead of returning nothing). Formula: +50 positive-keyword, +30 is-Pawn / +15 is-Actor, −40 infra-negative **(FIELD-name only)** (`ViewTarget`/`Owner`/`camera`/…), −60 not-Actor, +10 near-combat (Pawn/component source), +5 real GObjects index. Auto-pick = top candidate when its score > 0 **and it clears the runner-up by a ≥20 margin** (so several equally-plausible actors — e.g. JRPG party members, all bare is-Pawn ~35 — don't yield an arbitrary wrong pick); otherwise the ranked guesses are returned but NOT auto-loaded. **Read-only, fast, bounded** (no full GObjects scan except the PC instance-scan fallback; per-root 1024-edge cap, ≤16 components, `Tot::Requested()` abort).

**New public Aura seam** `Aura::CollectOutgoingObjectPtrs(obj, out, maxEdges)` + `OutgoingPtr` struct — a thin façade over the file-static `EnumerateOutgoingObjectPtrs` template, so Edel scores edges without duplicating the container/weak-ptr traversal (zero new graph code; inherits all the adapter's cross-game fixes).

**Pipe** `get_current_target` (pipe-only) → `Fern` handler emits `resolved`/`world`/`player_controller`/`player_pawn`/`note` + ranked `candidates[]`. **C#** `CurrentTargetResult`/`TargetCandidate` models + `IDumpService.DetectCurrentTargetAsync` + `DumpService` (reflection-free `JsonObject`/`JsonArray` parse). **UI** — a **🎯 Detect target** button on the Related panel + a candidate picker (`ListBox`, click a row to inspect another); the top candidate auto-loads via the existing `LoadForAddressAsync`, so the related grid immediately fills with the target's class/outer/components/**AttributeSet** — Detect → HP in one click. Always-populated chain diagnostics drive a graceful-degradation `note` (no world / no PC / no pawn / 0 candidates / weak-only), so a non-matching game never fails hard, it falls back to the manual flow. **Also fixes the documented Locate-in-GWorld `not_reachable` limit for streaming / World-Partition enemies**: Edel doesn't BFS down from GWorld to an unknown target — it reads the target directly off a field the player already holds, so the target is by construction forward-reachable from the player (and usually from GWorld), and the auto-loaded row's 🌍 handoff now has a real chain.

**Phase-1 review hardening** (an adversarial multi-agent review of the shipped Related Objects code, each finding refute-verified): `GetRelatedObjects` gained a wall-clock deadline + in-loop `Tot`/visited cap (a huge reflected `TArray<UObject*>` on the queried actor could otherwise stall the synchronous pipe worker — rejected elements never advanced the add-caps); the hierarchy/counterpart objects (Class/Outer/Controller/Pawn) are now in the `seen`-set so the owned BFS can't re-emit them as duplicate rows; container-element rows report `fieldOffset = -1` (the element lives in the heap Data buffer, not at `parent+offset` — the `[idx]` stays in `fieldName`); `classify()` no longer mislabels a `*AttributeSetComponent` as an AttributeSet; the `get_related_objects` pipe handler now strict-parses `addr` (`TryStrToAddr`, mirroring `find_path_from_gworld`) so an unsubstituted placeholder is a clear error not an ambiguous empty `ok:true`; and the stale `depth` doc comment (`1..2`→`1..3`) was corrected.

A second adversarial code-review pass (3 reviewers × refute-verify over the diff) caught + fixed: the infra-negative was wrongly matched against the candidate's CLASS name too (a real lock-on enemy `BP_WorldBoss_C` ate a −40 for "world" → could drop below the auto-pick margin) — now FIELD-name only; the `enemy` keyword missed the plural `Enemies` (y→ies) → stemmed to `enem` (and `hostile`); self-exclusion seeded only the resolved pawn → now seeds BOTH `Pawn`+`AcknowledgedPawn` (differ on a networked client) + the pre-hop controller; the per-root component budget was shared (a component-heavy pawn starved the PC's components, where lock-on components often live) → now per-root + PC-first; the candidate picker is disabled while busy (no mid-load double-load); and the public `OutgoingPtr` header contract was corrected to match the raw façade output. (Confirmed-safe by the same review: the `seen` restructure changes no normal owned rows; the deadline/visited bounding doesn't truncate normal actors; the JSON round-trip is field-consistent.)

**All green:** DLL builds clean (utf8 31/0, dll_helpers 697/0), **C# 1657/0** (+4: `DumpService.DetectCurrentTargetAsync` parse/order; `RelatedObjectsViewModel` auto-pick / unresolved / weak-candidate paths), **NativeAOT UI 46.5 MB**. New: `Edel.h`/`Edel.cpp`, `Models/CurrentTarget.cs`; touched Aura/Fern/Renge + the C# service/VM/panel/strings; `Edel` registered in both CMake source lists. **In-game verify PENDING** (best on a lock-on / soft-target action title — confirm Detect lands on the focused enemy and the AttributeSet/HP shows; on the named JP/CN test games with no target UPROPERTY, expect the graceful "ranked guesses / no clear target" fallback).

## 2026-06-20 — CE-Field follow-up (a): Map/Set (and interface-array) element hops in a GWorld-path spine now split into container + element crumbs (builds 1389-1390; DLL + pipe + UI; dev; LIVE-VERIFIED in-game)

Closes follow-up (a) of the Copy-CE-Field object-pointer-array work. A "Locate in GWorld" path can hop through a `TMap`/`TSet` element (or a `TArray<FScriptInterface>` element), but `PathStepToBreadcrumbs` previously split ONLY object/class-pointer arrays — Map/Set/interface hops collapsed to a single crumb, so the CE chain stopped at the container's Data buffer and applied the next field's offset to IT (wrong addresses downstream).

The element offset within a container's Data buffer is `ElementIndex*stride (+ valueOffset)`, but the path step didn't carry `stride`/`valueOffset`. Those values are already computed + cached DLL-side (`ome.pairStride`/`ome.valueOffset` for maps, `ose.elemStride` for sets; FScriptInterface is a fixed 16-byte slot) — the fix just threads them through. The `EnumerateOutgoingObjectPtrs` `emit()` lambda was widened 6→8 args (`+elemStride, +elemValueOffset`); ALL callers updated (the BFS lambda in `GraphPath.h`, `GetRelatedObjects`/`AppendOwnedSubObjectLeaves`, the `dll_helpers_test` `MOCK_NB`); `GraphEdge`/`GraphPathStep` gained the two fields (copied through `BfsShortestObjectPath`'s ParentLink + reconstruction); `Fern.cpp` serializes `elem_stride`/`elem_value_offset` (only when >0); C# `GWorldPathStep` + `DumpService` parse them. `PathStepToBreadcrumbs` now splits Map/Set/interface-array hops (element crumb offset = `ElementIndex*stride + valueOffset`, container crumb strips the `.Key`/`.Value` suffix so it names the real field — which also lets follow-up (b)'s Back-nav re-hydration match it). Object/class arrays keep the hardcoded-8 path unchanged.

A 4-lens adversarial review (+ per-finding refute) raised 8 findings (1 false positive) — two were positive confirmations (Map/Set/interface offsets correct against the DLL read math; the path is reachable + downstream handles the new synthetic crumbs). Acted on: interface arrays now split too (16-byte stride — was the wrong-offset single crumb), object-array emit passes `0` not a redundant `8` (matches the doc; C# hardcodes 8). Accepted nits: struct-nested dotted base name won't re-hydrate/dedup (pre-existing, affects object arrays too; CE pointer math unaffected since it uses the absolute `FieldOffset`) and int32 element-offset arithmetic (theoretical — needs an 89M-slot container; `FieldOffset` is `int` by design). 6 new tests (5 C# `PathStepToBreadcrumbs_{MapValue,MapKey,Set,Interface,MapNoStride}` + 1 C++ `Test_GraphPath_MapSetElementGeometryRoundTrip`). **All green: C++ 31/0 + 697/0, C# 1653/0, NativeAOT UI 46.5 MB.** Both CE-Field follow-ups (a) + (b) now done **and LIVE-VERIFIED in-game** — merged to main.

## 2026-06-20 — CE-Field follow-up (b): Back-nav onto a path-synthetic container crumb re-hydrates the array element view instead of mis-rendering the parent grid (builds 1380-1388; UI-only; dev; LIVE-VERIFIED in-game)

Closes follow-up (b) of the Copy-CE-Field object-pointer-array work (PR #323). `PathStepToBreadcrumbs` splits a Locate-in-GWorld `TArray<UObject*>` hop into a container crumb + an element crumb, but the container crumb's `ContainerField` is left null (a `GWorldPathStep` carries no `ArrayDataAddr`/`ArrayCount`/resolved element list). Because every container re-display site is gated on `IsContainerView && ContainerField != null`, Back-nav onto that synthetic crumb fell through to a plain parent re-walk and rendered the PARENT object's field grid — a silent mis-render (the crumb says `SpawnedAttributes` but you see the owning object). NOTE this is NOT the "cosmetic duplicate" the todo implied: the Back handlers never `Breadcrumbs.Add`, and the duplicate the todo referenced was an export-time artifact already handled by 2c's `DedupeConsecutiveBreadcrumbs`.

**Decision: "give it a `ContainerField`" is infeasible** — the path step lacks the data to rebuild an array view (a synthetic `LiveFieldValue` would render 0 rows); that data only exists live in the process. **Fix = lazy re-hydration:** new `TryRepopulateSyntheticContainerAsync` re-walks the parent object live (the crumb's Address IS the parent), matches the container field by `FieldName`+`FieldOffset` (the same lookup `RefreshAsync` uses), and calls `RepopulateContainerView` from the freshly-resolved field — reusing the existing re-walk-and-match pattern, reading memory only when the user actually navigates back. Wired into ALL 4 container re-display sites: `NavigateToBreadcrumbAsync`, `GoBackAsync` (normal + pre-bookmark restore), and `LoadBookmarkAsync`; `RefreshAsync`'s container gate also broadened from `ContainerField != null` to `IsContainerView` (matching by `ContainerField?.Name ?? crumb.FieldName`) so auto-refresh / Refresh doesn't revert the re-hydrated view.

A 3-lens adversarial review (+ per-finding refute pass) over the diff raised 11 findings — 8 nits (pre-existing parity / intentional defensive mirror) + 3 low; the LoadBookmark 4th-site gap and two test-coverage gaps were fixed, the rest consciously accepted. 7 new tests in `LiveWalkerMultiSelectTests.cs` (NavigateToBreadcrumb / GoBack / LoadBookmark / pre-bookmark-restore re-hydrate; no-live-match + not-populatable fall-through; Refresh keeps the view). **All green: C++ 31/0 + 691/0, C# 1648/0, NativeAOT UI 46.5 MB.** Remaining: follow-up (a) (Map/Set spine split — cross-layer DLL + pipe + C#).

## 2026-06-19/20 — Value Search user-adjustable scan timeout (10–60s slider) + Copy CE Field drills object-pointer arrays (leaf + GWorld-path spine + duplicate-crumb dedup) + Guess? gap diagnostic log + Elliot full-release docs (builds 1364-1379; DLL + UI + docs; MERGED main PR #323 `5d96bfc`, dev=main)

Items from an Elliot (SQUARE ENIX UE4.27, now full release) session. **All build-verified: DLL + 3 proxies clean, NativeAOT UI 46.5 MB, C++ 31/0 + 691/0, C# 1641/0.** A 3-dimension adversarial review (+per-finding refute pass) over the diff caught one real regression (item 2, Weak/Soft). Iterative live testing then surfaced TWO more object-array export bugs the leaf fix didn't cover — the GWorld-path breadcrumb SPINE collapsing an array-element hop (item 2b), then a doubled deref from a duplicate consecutive container crumb (item 2c). **ALL LIVE-VERIFIED + MERGED (PR #323):** item 1 (timeout), item 2 leaf + 2b (spine) + 2c (dedup) verified on Elliot AND the deeply-nested Gundam SEED chain (struct-array → `Name→Struct` map → nested struct-array → scalar-array, both nested + Collapse-chain); item 3 (Guess?) confirmed working-as-designed; item 4 docs. SEED also drove two `DedupeConsecutiveBreadcrumbs`/`CleanBreadcrumbs` deep-distinct-chain regression guards (the dedup must NOT over-collapse a legit deep chain) — `DedupeConsecutiveBreadcrumbs_DeepDistinctChain_Unchanged` + `CleanBreadcrumbs_DeepDistinctChain_PreservesAllLevels`.

**1. Scan timeout is now a slider (was a hard 15s).** Both `Aura::ScanForValue` (`Aura.cpp` ~5052) and `Aura::ScanForValueGroup` (~7029) had a `constexpr auto kDeadline = std::chrono::seconds(15)`; a huge game (Elliot, ~407K objects, group first scan hit 15 023 ms) kept truncating with no recourse. Both gained a trailing `int32_t deadlineMs = 15000` param → `const auto kDeadline = std::chrono::milliseconds(deadlineMs > 0 ? deadlineMs : 15000)` (the several in-function deadline checks all reference the local `kDeadline`, so one change each covers them). `Fern.cpp` parses `deadline_ms` on `begin_value_scan` + `begin_group_scan` (clamped [1000, 300000]ms) and threads it. UI: new `ScanTimeoutSeconds` `[ObservableProperty]` (default 15, clamped 10–60 via `OnScanTimeoutSecondsChanged`) + a **"Timeout" `Slider`** (Min 10 / Max 60 / snap 5) in BOTH the single-mode and group-mode option rows (shared property), threaded as `ScanTimeoutSeconds*1000` to `BeginValueScanAsync`/`BeginGroupScanAsync` → `deadline_ms` (attached only when ≠ 15000, wire-tight + back-compat with old DLLs). Truncation banner now reads `(Ns deadline) — raise the Timeout slider or refine`. The agent investigation first mis-pointed at `FindInContainers`/`FindInContainersDeep` (~1985/2276) — those are by-address container lookups, NOT value scans; a direct grep found the real sites. NOTE refine (`RefineCandidates`/`RefineGroupCandidates`) has no deadline (re-reads only the small existing candidate set; fast) — intentionally unchanged.

**2. Copy CE Field now drills into `Array<ObjectProperty>` elements.** Selecting an object-array element (e.g. `SpawnedAttributes[2]` → `CharacterAttributeSet`) and Copy CE Field emitted the element as a plain 8-byte pointer leaf, ignoring the drilldown depth. **Two gaps (the second one the investigation agent missed):** (a) `CeXmlExportService.BuildContainerValueFields` handled Map/Set object values + Struct arrays but had **no `ArrayProperty`+`ObjectProperty` case** → the resolver never walked the element pointers into `resolvedInstances`; (b) even with that fixed, `EmitArrayProperty` emitted object-array elements via `EmitLeaf` and **never consulted `resolvedInstances`**. Fix = (a) add the object-array case to `BuildContainerValueFields` (one synthetic `LiveFieldValue` per non-null element pointer; `PtrClassAddr` left empty → `WalkInstance` resolves class from the pointer); (b) new `EmitObjectArrayProperty` + a branch in `EmitArrayProperty` (placed AFTER the Phase G soft-object handler, gated on at least one element being in `_resolvedInstancesState`) that drills each resolved element via `EmitDrilledPointer` (array group `Offsets=[0]` derefs `TArray.Data`; the element group's own `Offsets=[0]` derefs the 8-byte element pointer; cycle/depth guards reused), with the flat-leaf fallback preserved for unresolved/null elements and for depth=0. Avoided a double class-name (`EmitDrilledPointer` re-appends `(Class)`, so the synth field carries the bare `[N] Name`). New regression test `GenerateInstanceXml_ObjectArray_WithResolvedElement_DrillsElementGroup`. **Review catch (fixed):** both new paths first gated on `IsObjectPropertyType(ArrayInnerType)`, which ALSO matches `Weak/Soft/Lazy/InterfaceProperty` — but only `ObjectProperty`/`ClassProperty` store a raw 8-byte `UObject*` in the array slot (a Weak element is `{ObjectIndex, SerialNumber}`, Soft/Lazy are larger structs). The DLL still *resolves* those to a live object, so they'd have been wrongly drilled with `Offsets=[0]` → CE dereferences a non-pointer to a garbage address (a regression from the prior correct 8-byte leaf). Fixed by gating on a new `IsRawObjectPtrArrayInner` (= `ObjectProperty`/`ClassProperty`, matching the DLL's `Ubel::IsPointerArrayType`); Weak/Soft/Lazy keep their leaf / Phase-G handling. Guard test `GenerateInstanceXml_WeakObjectArray_ResolvedElement_DoesNotDrill`.

**2b. Copy CE Field via "Locate in GWorld" — the breadcrumb SPINE through an object-array element was missing its deref node (live-found on Elliot).** Separate code path from the leaf fix: `FindObjectGraphPath` returns a path hop through `PlayerArray[0]` as ONE step (FieldType=`ArrayProperty`, FieldOffset=array field 0x2E0, ElementIndex=0), and `BuildBreadcrumbSpineFromPath` emitted it as a single pointer crumb (`+2E0, Offsets=[0]`). But a `TArray<UObject*>` element needs TWO derefs — deref `TArray::Data`, then deref the element pointer at `index*8` — so CE stopped at the Data buffer (7FDC6160) and applied the next field's `+340` to IT, garbaging every address below (`PawnPrivate`=12AD87900 vs correct 13F828040). Manual navigation was already correct (it produces a container crumb + an element crumb: `PlayerArray(C,0x2E0) > [0](P,0x0)`); only the GWorld-path-derived spine collapsed it. **Fix:** extracted `LiveWalkerViewModel.PathStepToBreadcrumbs(GWorldPathStep)` (internal static, testable) that splits an object-pointer-array hop (`FieldType==ArrayProperty && ElementIndex>=0 && InnerType∈{Object,Class}Property`) into TWO crumbs — a container-view crumb (deref Data at FieldOffset; `IsContainerView=true` so `CleanBreadcrumbs` skips it as a cycle endpoint) + an element crumb (deref ptr at `ElementIndex*8`). Struct-array / Map / Set element hops keep the single crumb (element stride unknown / not a plain pointer). The DLL/pipe already carried `field_type`/`inner_type`/`element_index`/`field_offset` per step — C#-only fix. 5 tests incl. an end-to-end `GenerateHierarchicalXml_PathThroughObjectArrayElement_EmitsElementDerefNode` (asserts the `[0]` node exists and PawnPrivate nests under it).

**2c. Duplicate consecutive container crumb → doubled `+offset` deref (live-found after 2b).** With 2b correct (`PlayerArray` split working), the export still failed: the breadcrumb carried TWO identical consecutive `SpawnedAttributes(C,0x10A8)` crumbs, so the export (which drops only the LAST container crumb to turn it into the field) left the other in the spine — emitting `SpawnedAttributes(+10A8) → SpawnedAttributes [3 x ObjectProperty](+10A8)`, a double-deref. Both the nested and Collapse-chain exports failed identically. Origin: 2b's path-synthetic container crumb has no `ContainerField`, so a Back-nav onto it fell back to re-walking the parent (ASC) and the user re-entered the container, stacking a duplicate. **Fix:** new `CeXmlExportService.DedupeConsecutiveBreadcrumbs` collapses adjacent crumbs that share `(FieldOffset, Address, FieldName, IsContainerView)` — keeping the LATER one (it carries the live `ContainerField` a real container view needs; the synthetic one doesn't). Applied in `ExportCeFieldXmlAsync` BEFORE the container split (so the redundant copy is gone before one becomes the field) and folded into `CleanBreadcrumbs` (covers Copy CE XML + the spine; the split crumbs from 2b have distinct name/addr so they're never collapsed). 3 tests. Residual cosmetic: the live breadcrumb BAR can still show the duplicate (dedup is export-only) — minor, follow-up if it bothers.

**3. Guess? gap diagnostic (verify-first, no behavior change yet).** User compared Live Walker `Guess?` against a CE Structure Dissect of an Elliot `LSGameWork`-ish object and saw "missing" data at `0x180–0x1D0` (and `0x170–0x180`). Analysis: top-level object fields get `fv.size = fi.Size` (real `FPROPERTY_ELEMSIZE`) and container properties (TMap/TSet) legitimately occupy their full inline size (~0x50), so that region is **almost certainly the inline allocator bytes of a TMap/TSet property** (most likely the `BottledNums` map the user highlighted — 0x50 == exactly 0x180→0x1D0; the `ptr/num/max/hash` byte pattern matches) — i.e. **working as designed** (the walker shows it as one expandable Map row; CE flattens it to raw ints), not a swallowed gap. Could NOT verify from logs (the walk log has only perf summaries, no field sizes). Per the user's choice (verify-first) + the "verify against actual memory layout" rule, shipped a **diagnostic** instead of a speculative fix: `Ubel.cpp` `WalkInstance` now logs (in the `fillGaps` block, only when `Guess?` is on) one compact `WALK:guess` line dumping every reflected field's `offset=size(type)` + the computed raw gaps. User re-runs `Guess?` on that object → the log pinpoints what covers `0x170–0x1D0`, then we decide (container-internals = no change, or correct an over-large field size if it turns out to be a swallow). See [todo.md].

**4. Elliot is now the FULL RELEASE (was the Prologue Demo).** Removed "Demo" from the two Readme version-matrix rows, the `test-games.md` row + GWorld-summary list + naming-convention entry. Re-verified on build 1363: Steam folder `The Adventures of Elliot_The Millennium Tales`, ~390K objects (389 292 at scan → 406 873 mid-game), GObjects `0x149BFB140` (GOBJ_ES53_1), GNames `0x149B17600` (GNAM_V8), GWorld `0x149D8ADA0` (GWLD_TQ_1), **ProcessEvent vtable+0x260 dispatch validated (6 096 hooks/1500 ms) — invokes now work**, `LS`-prefixed UClasses. Historical dev-log entries that described it as a demo at the time are left intact (append-only history).

## 2026-06-19 — dxgi proxy on Octopath: diagnosed a fundamental EARLY-LOAD / loader-lock fragility (NOT fixed for Octopath — use version.dll); shipped 2 genuine early-load correctness fixes along the way (build 1351; `Sein.cpp` + `Lugner_Dxgi.asm` + `Lugner_Dxgi.cpp` + `Heiter.cpp`)

User report: Octopath Traveler **instant-exits without running** with the dxgi.dll proxy, but the **version.dll proxy works fine** on the same game. No log written. **Outcome: Octopath uses version.dll (it imports version.dll; version.dll proxy is live-verified working on it). The dxgi proxy has a deeper early-load limitation on Octopath that two fixes did NOT fully close — hardening deferred (user chose version.dll).** This entry records the full debugger-backed diagnosis so the next person doesn't re-walk it.

**Three distinct crashes, each cdb-confirmed on the actual `%LOCALAPPDATA%\CrashDumps\*.dmp` (WinDbg `Microsoft.WinDbg` via winget → `amd64\cdb.exe`; MS symbol server for ntdll; our frames symbolised against a layout-identical `/MAP` rebuild):**
1. **OLD eager build → AV EXECUTE at 0x0** — a forwarded dxgi call jumped through a null `mProcs[N]` (`DxgiProxy_Init`'s `LoadLibrary(real dxgi)` had failed → table never populated).
2. **lazy build → AV WRITE null+0x24** in `ntdll!RtlpWaitOnCriticalSection` (`inc [DebugInfo+0x24=ContentionCount]`, DebugInfo NULL = **uninitialised CRT lock** `__acrt_lock_table+0xF0`), called from `Sein::GetTimestamp → localtime_s → __tzset`.
3. **lazy + GetLocalTime build → AV in `ntdll!RtlAllocateHeap`** (null heap), called from `DxgiProxy_EnsureResolved → Sein::Error(LOG_ERROR) → GetTimestamp → std::string`, with **`ntdll!LdrpLoadDll`/`LdrpFindLoadedDllByName` on the stack** = we are inside loader activity.

**Real root cause (all three are the same disease).** **Octopath calls into dxgi EXTREMELY early — under the loader lock, before our DLL's CRT is initialised** (no log = our `DllMain` never completed). At that point our heavy "whole-dumper-as-dxgi.dll" proxy cannot: (a) **load the real same-named `dxgi.dll`** — `LoadLibraryW(System32\dxgi.dll)` while our `dxgi.dll` is loaded + under loader lock returns NULL (the dump shows real dxgi never mapped; `LdrpFindLoadedDllByName` on the stack), nor (b) **log / allocate** — the CRT heap + locks aren't ready (`__tzset` lock, `RtlAllocateHeap` null heap). The **version.dll** proxy dodges all of it because its exports (`GetFileVersionInfo…`) are called at normal runtime, NOT under early loader lock. RE-UE4SS gets away with the same DllMain-LoadLibrary pattern only because its proxy is a thin shim; we fold the whole dumper in.

**Two genuine fixes shipped (kept — they fix real latent early-load bugs, just not enough to make Octopath's dxgi work):**
- **`Sein::GetTimestamp()` → Win32 `GetLocalTime()` + `snprintf`** (was CRT `localtime_s`/`std::put_time` → `__tzset`). Removes the crash-2 class entirely; safe at any load time; shared by main DLL + all 3 proxies. *Lesson: never call CRT locale/timezone fns from DllMain/early-load.*
- **dxgi forwarders → lazy self-resolving asm trampolines** + removed eager `DxgiProxy_Init`/`DxgiProxy_LogStatus` from `DllMain` (`LoadLibrary` in `DllMain` is wrong; lazy is how version/dinput8 already work). Fixed the crash-1 class for the non-early case.

**Resolution / guidance.** **For Octopath (and any game that imports version.dll), use the version.dll proxy.** The dxgi proxy remains the right tool ONLY for games that import neither version.dll nor dinput8 (e.g. Elliot) AND that don't call dxgi under early loader lock. Making dxgi robust for the Octopath case needs a thin-shim split or a CRT-free + renamed-real-dxgi-copy forwarding path — deferred. New reusable triage tools left in `tools/pe/`: `pe_imports_exports.py` (PE import/export) + `minidump_triage.py` (minidump modules/exception/stack). main DLL + ProxyDxgi rebuild green; dxgi.dll imports `GetLocalTime`, 70 exports intact.

## 2026-06-19 — LiveWalker "Guess What": fill the LEADING gap before the first field (clamp `occupied` to the scan window) + tighten the Double normal band + float/double aliasing guard (builds 1330-1333; DLL)

User report: "Guess What" fills the gaps *between* known fields (e.g. first field `0x100`, last `0x300` → the `0x100..0x300` holes get guessed rows), but the **leading region from the end of the UObject header (~`0x28`) up to the first field never gets guessed**. Differential symptom — mid gaps fill, leading gap doesn't.

**Root cause (DLL, `Ubel.cpp` gap block in `WalkInstance`).** The gap cursor starts at `headerEnd = UOBJECT_OUTER + 8` (`0x28` standard / `0x30` CPN; `0` for a raw struct). The merge loop emits a gap only when `iv.start > cursor`, then unconditionally does `cursor = max(cursor, iv.end)`. `occupied` was built from **every** reflected field with **no clamp to `>= headerEnd`**. So a field with a low/garbage `Offset_Internal` (≤ `headerEnd`) but `end > headerEnd` fails the `iv.start > cursor` test *yet still* advances the cursor past `headerEnd` — silently swallowing the entire `[headerEnd, firstField)` leading region. The C# side was cleared (a 4-tracer + adversarial-verifier workflow confirmed `DumpService`/`LiveWalkerViewModel`/`LiveWalkerPanel` render every field the DLL sends verbatim — guessed rows only styled gray + edit/nav disabled, never filtered/deduped/hidden), so the missing rows were simply absent from the pipe JSON.

**Fix.** Clamp each `occupied` interval into `[headerEnd, scanEnd]` *before* sort/merge and drop empties: `s = max(start, headerEnd)`, `e = min(end, scanEnd)`, `if (s >= e) continue`. Now a `{0x10, 0x120}` field becomes `{0x28, 0x120}`, the cursor stays at `0x28`, and the leading region survives as a real gap. Everything below `headerEnd` (vtable/flags/class/name/outer) stays **excluded** from `occupied` *and* from the gap set (cursor starts at `headerEnd`), so the well-known header is never turned into garbage guessed rows. End-clamp also stops a garbage-huge `ElemSize` from eating the trailing gaps. No change to `headerEnd`, the merge loop, the gap loop, or the heuristics. 0-field classes (one full `[headerEnd, scanEnd]` gap) and raw structs (`headerEnd=0`) unaffected.

627 dll + 31 utf8 + 1613 C# green. In-game verify PENDING (walk an instance whose first reflected field sits well above the header, toggle Guess? → confirm guessed rows now appear from ~`0x28` up to the first field).

**Also — Double normal band tightened (`IsLikelyDouble`).** Per a user note that CE-style guessing over-produces doubles, the normal-confidence magnitude band was trimmed from `[0.001, 1e12]` to `[0.001, 1e9]` — still wider than float's `[0.001, 1e6]` (doubles exist to hold values beyond float precision, e.g. UE5 LWC large world coords) but no longer flags the `[1e9, 1e12]` tail of random 8-byte patterns as "Double?".

**And — float/double aliasing guard ("prefer the integer reading first").** A `[00000000][float]` 8-byte slot is byte-identical to a small-magnitude double (CE's `0.875` case), so band-trimming alone can't separate them. The user's framing — *whichever reads as an integer, take it first* — maps onto the aliasing fingerprint: a real double almost never has its low 32 mantissa bits all zero UNLESS it's a deliberately clean whole/.5 value. New guard in the `GuessGapTypes` Double branch: for **normal-confidence** doubles only (`dblConf==1`; high-confidence clean doubles are never touched), if the LOW 4 bytes are exactly zero AND the value is NOT a whole/.5 number, drop the double — `Int32(+0)` then emits the clean integer `0` and the next position surfaces the real `Float(+4)`. Worked cases: `00 00 00 00 00 00 EC 3F` (`0.875`) → Int32 `0` + Float `1.84375` ✅; `…59 40` (`100.0`, whole) → Double kept ✅; pi `18 2D 44 54 FB 21 09 40` (nonzero low bytes) → Double kept ✅. Tradeoff (accepted): genuine binary-dyadic doubles with zero low mantissa and non-.0/.5 fraction (`0.25`, `0.375`…) now lean to int+float — rare as standalone doubles in float-dominated UE data. Priority order, exponent windows, clean-fraction + prefer-int(32/64) rules otherwise unchanged.

## 2026-06-19 — Instance Finder "Newest first" opt-in: scan GObjects from the high (most-recently-allocated) end to catch just-spawned instances + reach the newest past the result cap (build 1325; DLL + pipe + UI)

Follow-up to the Related Objects work, from a user insight: GObjects index = allocation order, so the index lens has **two complementary uses** — **high index = a just-spawned runtime instance** (the enemy that just appeared), **low index = the CDO `Default__BP_X_C` / class-default / template** (finding a Blueprint's defaults). The root problem: `Aura::FindInstancesByClass` walks index `0 → count` ascending and stops at `maxResults` (500), so the **default already serves the low-id/CDO use** but **truncates the newest off the end** for a high-population class — "catch the just-spawned enemy" was impossible.

**Fix — one opt-in checkbox = scan direction.** `FindInstancesByClass` gains `newestFirst` (default false): the loop keeps a visit counter `n` and reads the real index `i = newestFirst ? count-1-n : n` (cancel check moved to `n` so it fires either direction); everything else byte-identical, `sr.index = i` stays the true InternalIndex. Pipe `find_instances` gains `newest_first`; `IDumpService/DumpService.FindInstancesAsync(..., newestFirst = false, ...)`; `InstanceFinderViewModel.NewestFirst` `[ObservableProperty]` → a **"Newest first" checkbox** next to "Exact match" (default OFF — unchanged low-id/CDO behaviour). The Index column is already client-side sortable either way (re-sort the returned window); the checkbox controls which *end* the cap keeps. All other `FindInstancesByClass` callers (Frieren/Mimic/Solitar/Wirbel) + `FindInstancesAsync` callers unaffected (defaulted param); test overrides (`ClassPivotViewModelTests` ×2) + `StubDumpService` signatures updated.

**Native-HP note (why this matters):** the tool's Value Search / Group Scan are **property-aware** (scan reflected numeric leaves; `expandFields` walks the property chain — NOT raw memory), so a truly non-reflected native HP field is **not findable in-tool**. Workflow for native HP = locate the OBJECT (newest-first / Related Objects) → 🌍 stable pointer chain → raw-scan the native offset in CE. GAS AttributeSet HP IS reflected (`UPROPERTY`) so the tool reaches it — check reflection first.

627 dll + utf8 31 + **1613 C#** (+1: `FindInstancesAsync_SendsNewestFirstFlag`) green. In-game verify PENDING (a high-population enemy class — confirm `newest_first` surfaces the just-spawned one).

## 2026-06-19 — Related Objects panel (Phase 1): given an actor, surface its class/outer, Controller↔Pawn counterpart, and OWNED sub-objects (components, GAS ASC → AttributeSets) with GWorld / Live Walker / Find-Class handoffs (builds 1323-1324; DLL + pipe + UI)

Answers the user question "can I find the object I clicked / the enemy I'm focused on, and its related objects (AttributeSet etc.)?". The focused-enemy pointer is almost always an `AActor*` in some `UPROPERTY`; once you have that actor, this panel expands the objects around it. **Phase 1 = the always-works half** (given a known actor → its related objects); Phase 2 (a later increment, new `Edel` module) will auto-detect the current target via GWorld→PlayerController/Pawn and feed it in.

**DLL — `Aura::GetRelatedObjects(target, maxResults)` (Aura extension, no new module).** Returns `RelatedObject{addr,index,name,className,relation,fieldName,fieldOffset,depth,parentAddr}`: Self / Class / Outer, the Controller↔Pawn counterpart (reflected by field name via `Ubel::FindFieldOffset` — `Controller`, then `AcknowledgedPawn`/`Pawn`), then a bounded **owned walk up to depth 3** — depth 1 = direct owned sub-objects (components, the ASC), depth 2-3 = each sub-object's owned objects (the ASC's `UAttributeSet`s), so a GAS AttributeSet is reached even when nested behind a stats/ability layer: pawn → stats component → ASC → AttributeSet (build 1326 deepened 2→3 + 96→128 subs after TQ2 testing showed the live creature `bpai_default_character_C`'s AttributeSet sits 3 refs below the pawn). Discovery REUSES the exact P4 cross-object pieces: `EnumerateOutgoingObjectPtrs` (direct ObjectProperty fields + object-pointer containers — OwnedComponents TSet / SpawnedAttributes TArray) gated by `IsOwnedBy` (Outer chains back within the depth budget). The `AbilitySystem (ASC)` / `AttributeSet` / `Owned Component` labels are a class-name convenience on top of the structural walk, not the discovery filter. Fast + bounded (no full GObjects scan); the reverse "who points AT this" view stays `FindReferencesToUObject`.

**Pipe — `get_related_objects` (pipe-only, the norm for variable-length queries).** `Fern` handler mirrors `find_refs_to_uobject`; `RelatedObject` model + `IDumpService.GetRelatedObjectsAsync` + `DumpService` (reflection-free `JsonObject`/`JsonArray` parse).

**UI — new "Related" tab (`RelatedObjectsPanel`/`RelatedObjectsViewModel`).** Takes an actor address (manual paste, or handed off) and shows the related-object grid; each row has 🌍 Locate-in-GWorld / Live (Live Walker) / finder (Find Class) / Addr (copy). Reuses the existing cross-tab event pattern (`LocateInGWorld` lands ON the object, stopAtParent:false; the 🌍 button is NOT gated on a C# GWorld flag — DLL `find_path` is truth — avoiding the build-1311 silent-no-op). AOT-safe DataGrid sort (`Address` sorts on parsed ulong via `CustomSortComparer`). A **`🔗 Related` handoff** was added to **Instance Finder** (selected instance), **Value Search** (per-candidate, on `InstanceAddr`), and **Live Walker** (current object) → switches to the Related tab and loads. New tab inserted at `MainTabIndex.RelatedObjects = 10` (the fixed experimental/Proxy/System tail shifted +1).

627 dll + utf8 31 + **1612 C#** (+2: `DumpService.GetRelatedObjectsAsync` parse/order; `RelatedObject` Display/FieldDisplay/AddressValue) green. **In-game verify PENDING** (best on a GAS title — TQ2 / DQ7R — to confirm actor → ASC → AttributeSet shows up, and the three handoffs land). Next: Phase 2 = `Edel` current-target auto-detect.

## 2026-06-19 — Group Scan P4 (increment 2): per-slot `owner_class` → the group-scan Pivot handoff targets the owned sub-object's class, not the actor's (builds 1318-1319; DLL + pipe + UI; closes P4)

P4 increment 1 made a cross-object slot's **object** handoffs (Live Walker / Locate-in-GWorld) open the owned sub-object via `owner_addr` / `GroupSlotMatch.HandoffAddr`. Its **Pivot** handoff still used the candidate **actor's** class (`GroupSlotMatch.ClassName`, denormalized from the candidate in `ParseGroupCandidate`), so pivoting on a cross-object slot — e.g. a GAS attribute held on a `UAttributeSet`, or a stat on a `UHealthComponent` — would select the wrong class. The Class Pivot is **class-driven** (`SelectClassAndTickPropAsync` looks the class up in the captured snapshot; `propName` is only an optional pre-tick hint), so the class is the correctness fix.

**Mechanism — `owner_class` mirrors `owner_addr` end to end.**
- DLL: each leaf carries an `ownerClass` (the class name of its `ownerAddr` object). `Aura::CollectGroupLeaves` gains an `ownerClassName` param threaded from its two call sites — the main object block passes the candidate class; `AppendOwnedSubObjectLeaves` passes `Ubel::GetName(childCls)` of the owned sub-object — so own-block + struct-nested leaves get the actor's class and each cross-object leaf gets its sub-object's class. Deep-container leaves use the scanned object's class (`curClassName`). `emitGroupCandidate` copies it onto `Radar::GroupSlotMatch::ownerClass` (empty → candidate class, defensive). `Fern::GroupCandidateToJson` emits `owner_class` next to `owner_addr`.
- C#: `ParseGroupCandidate` reads `owner_class` into `GroupSlotMatch.OwnerClass`; a computed `PivotClassName => string.IsNullOrEmpty(OwnerClass) ? ClassName : OwnerClass` (mirrors the existing `HandoffAddr`) feeds `PivotGroupSlot`. For an own-block leaf `owner_class == class_name`, so the Pivot is unchanged there.

The per-slot `field_name` stays the actor-relative path (`HealthComp.CurrentHealth`); when pivoting on the owner class it simply won't match the pre-tick `FirstOrDefault` and no field is pre-ticked — harmless, the Pivot still lands on the right class. **Refine / Orden / session untouched** — `RefineGroupCandidates` copies surviving `sm` entries, so `ownerClass` rides along. P1 / Deep / cross-object scan logic byte-identical (additive field only).

627 dll + 1610 C# (+2: `PivotClassName` owner-vs-actor model test; `PivotGroupSlotCommand` honours the owner class for a cross-object slot and falls back for an own-block slot) green; AOT publish clean (46.4 MB). This closes P4 (increments 1 + 2). In-game Pivot-class verify (a GAS title — TQ2 / DQ7R) still nice-to-have. See [group-value-scan-spec.md](group-value-scan-spec.md) §3.2 P4.

## 2026-06-18 — Group Scan P4 (increment 1): cross-object actor block — fold owned sub-objects' numerics into the actor's block (builds 1303-1313; DLL + pipe + UI; approach C, opt-in; in-game VERIFIED on TQ2 GAS) + Locate-in-GWorld silent-no-op + nested-struct-field-scroll fixes

P1 already finds a group whose N values **co-locate** in one object (incl. a single `UAttributeSet` — every UObject is its own block). P4 adds the **cross-object-distributed** case: a group whose values are spread across {an actor, the components it owns, its GAS AttributeSets} — e.g. Health on the pawn + Gold on an `InventoryComponent`. Opt-in (a third toggle alongside Deep).

**Approach C — ownership + value driven, NOT class-name driven** (the owner-confirmed design; see [group-value-scan-spec.md](group-value-scan-spec.md) §3.2). The original P4 sketch keyed on the sub-object class name matching `AttributeSet` / `Component`; that's both too narrow (GAS-only — SEED, our main UE4.27 test game, is a bespoke `LifeMSUnit` framework, **not** GAS) and too broad (most `UActorComponent`s are mesh/movement/audio — a leaf-budget blowup). Instead the reach is gated by **ownership** and the **value AND** provides the selectivity.

**Mechanism (`Aura::AppendOwnedSubObjectLeaves`).** For each candidate actor, after its own-object block, a bounded **2-level BFS over OWNED objects** folds each owned sub-object's numeric leaves into the SAME block, then `Orden::MatchGroup` runs over the union:
- **Discovery** reuses `EnumerateOutgoingObjectPtrs` (the outgoing-pointer adapter Locate-in-GWorld already uses) — direct `ObjectProperty` fields **and object-pointer CONTAINERS** (`OwnedComponents` `TSet<UActorComponent*>`, a GAS ASC's `SpawnedAttributes` `TArray<UAttributeSet*>`), neither of which P1/Deep walks (Deep walks *numeric* containers, not object-pointer ones). This is the forward object-pointer descent the old todo wrongly said "doesn't exist".
- **Ownership gate** `IsOwnedBy(child, actor, 2)` — the child's Outer chain must reach the actor within 2 hops, so depth 1 = the actor's components and depth 2 = the GAS AttributeSets (actor → ASC → AttributeSet). Shared / global objects (other actors, the world, GameInstance) fail the test and are never followed.
- A sub-object leaf's path is prefixed (`HealthComp.CurrentHealth`, `AbilitySystem.SpawnedAttributes[0].Health.CurrentValue`) and it carries its own **`ownerAddr`** (the sub-object) so the per-slot handoffs open the object that actually holds the field.

**Refine / Orden / session unchanged** — every leaf already keyed on its absolute `leafAddr`, so a cross-object leaf re-reads + locks exactly like an own / deep leaf; a freed sub-object → SEH-safe read faults → that leaf drops. Bounded: ≤ 64 owned sub-objects/actor, shared `leafCap` (4096), same 15 s deadline. **P1 / Deep / single-value stay byte-identical** (all behind the opt-in `cross_object` flag).

**Wire / UI.** `begin_group_scan` gains a `cross_object` flag; each candidate slot echoes `owner_addr`. C# `BeginGroupScanAsync(crossObject)`, `GroupSlotMatch.OwnerAddr` + `HandoffAddr` (Live Walker / Locate-in-GWorld target the owning sub-object; Copy Addr uses the leaf addr); a "Cross-object (owned components)" checkbox next to Deep in Group mode.

627 dll + 1605 C# (+3 cross-object: cross_object wire on/off, HandoffAddr owner-vs-actor, VM passes the flag; +2 Locate decouple — see below) green; AOT publish clean (46.4 MB). Like `FindReferencesToUObject`, the live-scan core has no unit test.

**In-game VERIFIED on TQ2 (UE5.07, a GAS title, proxy mode).** Group `Decreased` + `Exact 300` with **Cross-object on** surfaced a `bp_tq2_character_stats_component` actor whose `AttributesComponent` (ASC) holds `m_pAttributeSetHealth.CurrentHealth.BaseValue = 300` — the exact GAS actor → ASC → `SpawnedAttributes` → `UAttributeSet` 2-hop reach P4 was built for. The path-prefixed leaf name + the value AND both worked.

**Fix surfaced by the same test — "Locate in GWorld" was a silent no-op.** Clicking the per-slot 🌍 did nothing (the session pipe log shows **no `find_path_from_gworld` was ever sent**, no error). Root cause: the 🌍 button (single-value *and* group) was gated `IsEnabled="{Binding IsGWorldAvailable}"`, and the command also short-circuited on that flag — but the sibling Live / Addr buttons (ungated) worked, isolating it to the GWorld gate. The DLL's `find_path_from_gworld` is the real source of truth for GWorld (it returns `invalid` / `no path` when there's no live UWorld), so the C# flag was a redundant gate that, when it read false, **silently disabled the action with no feedback**. Fix: **decouple the Value Search Locate handoff from `IsGWorldAvailable`** — the button is always clickable, `GWorldLocateBlockReason` only blocks on a missing address (with a visible reason, never a silent no-op), and a `_log.Info` records the target addr before the handoff so any future failure is diagnosable. Pre-existing (not a P4 regression); the same gate still exists on the Instance Finder / Snapshot / SPC 🌍 buttons (left for a follow-up unless reported).

**Follow-up — Locate landed on the owner object but not the field (nested-struct leaf).** TQ2 retest (log-confirmed: `find_path_from_gworld target=0x197F5977180` = the AttributeSet owner, then `walk_instance` it): the click reached the AttributeSet but every slot stranded at the object top, and two slots looked identical. Cause: a GAS attribute leaf is `FGameplayAttributeData.CurrentValue` at `owner+0x120`, which sits INSIDE the `CurrentHealth` StructProperty at `0x118` — there's no top-level field at `0x120`, so the exact-offset scroll (`Fields.FirstOrDefault(f => f.Offset == wantOffset)`) found nothing. Fix: new pure `LiveWalkerViewModel.FindFieldByOffsetOrContaining` — exact offset, else the **containing top-level field** (largest offset ≤ the leaf offset). Now slot 1 lands on `CurrentHealth` (its preview shows `{BaseValue, CurrentValue}`) and slot 2 on `MaximumHealth` — distinct rows, each on the value's struct. Benefits the single-value scan too. +3 helper tests.

**Follow-up #2 — Locate reached the object but not the field (cross-object container-reached leaf).** DQ7R (Dragon Quest VII R, UE5) retest, Deep + Cross-object: a `DOLLGameCharacterManager` holds `GameCharacters` = a `TArray<DOLLFriendGameCharacter*>`, and the group matched `GameCharacters[0].MP / .Level / .HP` (MP at `owner+0x118` on the `DOLLFriendGameCharacter` object). Log-confirmed `find_path target=0x7947C080` (the owner) + `walk_instance` it — but the slot's `field_name` is `GameCharacters[0].MP`, the path **from the candidate (the manager)** to the owner. That `[0]` made `TryParseContainerPath` true → the container-drill tried to drill `GameCharacters[0]` **on the owner object**, which has no `GameCharacters` field (it's on the manager) → drill failed → "drill manually". But the owner IS the find_path target and holds MP as a DIRECT field at `scrollFieldOffset` (0x118). Fix: when the container-drill fails, **fall back to the byte-offset scroll** within the owner (extracted `ScrollToFieldByOffset`, reused by the UpdateDisplay scroll hint). Now Locate lands on MP. `DrillDisplayPathAsync` returns false at segment 1 without changing the display (the missing container field is never navigated), so the fallback runs on the owner's own field list. Increment 2 (per-slot `owner_class` for the Pivot handoff) remains.

## 2026-06-18 — Group Scan P2: per-slot prev-value / ordered / Between predicates + locked-offset table (builds 1295-1302; DLL + pipe + UI; prev-value refine in-game VERIFIED on SEED)

The multi-value Group Scan was exact-match-only per slot (P1). P2 gives **each slot its own predicate** — the real power when you don't know exact values — and surfaces the resolved **locked-offset table**. (The spec's third P2 piece, a "Copy CE Script / export of the matched object", was deliberately **skipped per the owner**: users export the resolved chain from Live Walker, which already does it.)

**Per-slot scan type.** Each of the 2–4 input rows now carries a scan type alongside its value/width:
- **First Scan**: `Exact` / `Bigger` / `Smaller` / `Between` (targeted — compares against the row's value(s)). So you can scan "Str > 10 AND Def > 10", or — the key **bounded-unknown** entry point for things like an HP bar whose exact value you don't know — "HP between 1 and 100 AND Mana between 1 and 50". `Between` carries an upper bound in a second per-slot value (`value2` / `targets2`).
- **Next Scan** additionally allows the prev-value four — `Changed` / `Unchanged` / `Increased` / `Decreased` — which compare each located leaf against **its own value from the previous round** (no value needed; the value box hides). Classic flow: First-Scan bounded (Between/Smaller), change something in-game, then refine "both Increased" to find where the tuple moved together. Only substring predicates are rejected (non-numeric).

**Mechanism — reuses the single-value model exactly.** `Orden::SlotTarget` gained `st` + `tolerance` + `targets2`; `LeafSatisfiesSlot` now routes through `Radar::ComparePredicate` (Exact still reduces to byte-equality; Between needs both bounds to fit the leaf width) and **rejects prev-value types on the first scan** (no baseline — they can never spuriously match in `MatchGroup`). `Aura::RefineGroupCandidates` honours each slot's `st`: prev-value compares the re-read leaf bytes against `GroupSlotMatch::prevValue` (already stored since P1), targeted against the slot's new target (+ upper bound for Between) for that leaf's width; `prevValue` is updated on every survival. `Radar::SlotSpec` gained `tolerance` + `value2`/`targets2`; new `Radar::NameOf(ScanType)` echoes the stored predicate back on the wire (`begin`/`refine`/`query` each slot carries `scan_type`, plus `value2` for Between).

**Locked-offset table (the actionable output).** Once every slot converges to a single offset (`AllLocked`), the master-detail row header shows `🔒 ClassName — Str@0x20, Def@0x24, …` (`GroupCandidate.OffsetTable` / `OffsetTableLabel` / `HasOffsetTable`) — the class plus each value's byte offset, ready to rebuild a struct / pointer chain in Live Walker. Per-slot detail captions render prev-value slots as `Hp  ↑ increased → 0x40  (FloatProperty)` and Between slots as `Hp  1..100 → 0x40 …` instead of a missing value.

**Wire / UI.** `begin_group_scan` + `refine_group_scan` slots take an optional `scan_type` (default `Exact`) and `value2` (Between only); `RefineGroupScanAsync` now takes `IReadOnlyList<GroupSlotInput>` (value **+** scan type **+** value2) instead of bare value strings. `GroupSlotInput` became an `ObservableObject` so the value cell hides for prev-value types and the upper-bound box appears for Between; per-row `ScanType` ComboBox added (`GroupScanTypeOptions`). First-Scan validation rejects a prev-value slot up front with a clear "needs a previous scan" message; Between requires both bounds.

627 dll (+3 Orden test fns: ordered first-scan, Between range/missing-bound, prev-value rejected on first scan) + 1600 C# (+8: input reactivity ×2, first-scan prev-value/Between reject, refine passes per-slot scan types, value2 wire, offset-table formatting, prev-value/Between captions) green; AOT publish clean (46.4 MB). Zero change to the single-value scan path. **In-game on SEED (UE4.27): prev-value group refine VERIFIED — a `begin` (Exact 10/15/25, +deep) followed by `refine` with `Unchanged / Unchanged / Increased` executed cleanly (pipe log, no errors).** ⚠ Between first-scan live-verify still nice-to-have.

## 2026-06-18 — GWorld `engine_recovery` gap-fill: GEngine→GameViewport→&World when no static slot exists + AOB-toggle gating fix + test hook (builds 1288-1291; DLL + Pointers-panel clarity)

When the GWorld AOB lands on a decoy, recovery already tries `ExtraScanGWorld` (find a live UWorld in GObjects, then scan `.data` for a static slot pointing at it). But that returns 0 when **no** static slot in the main module points at the live world — the world pointer lives only behind the engine's runtime objects (or in a separately-loaded engine DLL). New **`Genau::RecoverGWorldViaEngine`** fills exactly that gap.

**The recovered slot — a live engine-updated field, not a static symbol.** It returns `viewport + worldOff` = the address of the `UWorld*` FIELD inside `UGameViewportClient`, which the engine keeps updated across level transitions. A single deref yields the current world, matching how every consumer reads GWorld (`Wirbel::DerefWorld` / `ReadSafe(GWorld, uworld)`), so teleport / live walk / path search work unchanged and survive level loads within a session. **Known limit (by design):** this slot is a HEAP object field, not a static module address — valid for live ops but NOT for cross-session CE symbol export. That's fine: the gap case has no static anchor to export anyway, which is why the AOB toggle is force-disabled (below).

**Finding GEngine without a class name.** Class names vary (`UGameEngine` or a game subclass), so GEngine is identified by a stable reflected MEMBER, not a name: walk GObjects once, and for each distinct class resolve the `GameViewport` property offset via `FindPropertyOffsetByName` (version-independent; cached per class in an `unordered_map` so non-engine classes are walked at most once and cache −1). The first live non-CDO object whose `GameViewport` is non-null is GEngine. The viewport's `World` offset is then taken from reflection (`FindPropertyOffsetByName(vpCls, "World")`), falling back to a bounded memory probe (scan pointer-aligned fields ≤ class `PropertiesSize` for one that derefs to a class-`"World"` object) because `UGameViewportClient::World` is a private, often-unreflected member.

**AOB-toggle gating fix — why the Live Walker "AOB" checkbox now grays out on recovery.** The recovery block cleared `g_cachedGWorldPatternId` but left `g_cachedGWorldAob`/`Pos`/`Len` populated with the decoy match (set at `Frieren.cpp` init from `ptrs.gworldAob`). The UI's `IsAobSymbolAvailable` keys on a non-empty `gworld_aob`, so the GWorld "AOB" symbol toggle wrongly stayed enabled+checked for CE export even though GWorld came from recovery. Now **all** recovery branches null those three fields, so the existing C# does the rest with zero changes: `IsAobSymbolAvailable=false` → `IsEnabled=false` (grayed, `LiveWalkerPanel.axaml:147`) + `OnIsAobSymbolAvailableChanged` sets `UseAobSymbol=false` (unchecked), re-evaluated on each (re)connect via `SetEngineState`. This also fixes the pre-existing `instance_scan_recovery` path, which had the same latent bug.

**Test hook (the path is otherwise unreachable without a game whose AOB genuinely fails).** `UE5DUMP_FORCE_GWORLD_RECOVERY` in the GAME process env: `=1` forces the full recovery chain even when the AOB GWorld is valid; `=engine` additionally skips `ExtraScanGWorld` so it goes straight to the engine path (to exercise the new code on a game where `ExtraScanGWorld` would have succeeded). Non-destructive — if recovery fails, the valid AOB GWorld is left untouched.

Strictly gated (only runs when `*GWorld` doesn't deref to a UWorld AND `Aura::GetCount()>0`), so titles with a correct GWorld are byte-identical — zero regression. DLL builds clean (build 1288); no new unit tests (live-scan path, as with `ExtraScanGWorld`).

**Follow-up (builds 1290-1291) — in-game on SEED (UE4.27) the path RAN correctly but the Pointers panel read like "still AOB".** The env-var hook fired and `engine_recovery` succeeded (`GWorld recovered via engine_recovery -> 0x1AD…`, the live `&viewport.World` heap slot), but the UI still showed a GWorld **"AOB: \<scan addr\>"** line and gave no positive sign of recovery. Two gaps: (1) recovery cleared `gworld_aob`/`pattern_id` but NOT `g_cachedGWorldScanAddr`, so the panel's scan-addr row (`HasGWorldScanAddr`) stayed visible with the decoy's instruction address; (2) GWorld had no method-label display at all — `ShowGWorldWarning` only fires on `not_found`, so a *recovered* GWorld looked like a plain AOB hit (GObjects/GNames already show `⚠ AOB failed — found via \<method\>`). Fixes: DLL now also nulls `g_cachedGWorldScanAddr` on every recovery branch (scan-addr row hides; `Register Symbol`/ASM already gated on `gworld_aob`); C# adds `GWorldMethodLabel` + `ShowGWorldRecovered` (`method != aob && != not_found`) and a GWorld fallback row mirroring GObjects, and `FormatMethodLabel` gains friendly arms (`engine recovery` / `instance scan recovery` / `data scan recovery`). The Live Walker "AOB" checkbox graying was already correct (keyed on the cleared `gworld_aob`). 620 dll + 1592 C# green, AOT publish clean (46.3 MB).

Test it on any working game (no rare no-static-slot title needed): set the game-process env `UE5DUMP_FORCE_GWORLD_RECOVERY=engine`, connect, and check the Pointers panel shows `⚠ AOB failed — found via engine recovery` with no scan-addr row, and the Live Walker "AOB" checkbox is grayed.

## 2026-06-18 — Two fixes: CE Copy Field off-by-8 on map-VALUE struct fields + Proxy Deploy "NotDeployed" when another proxy type is deployed (build 1286; UI-only)

Two reported bugs, both C#-only, 1592 tests green (+7).

**(1) CE XML / Copy CE Field off-by-8 inside `Map<…, Struct>` values (SEED `MsTunes`).** Drilling a map value struct (`[0] MSB_STR00 (LifeMsTuneDataRow)`) and copying a CE Field chain produced a pointer that was 8 bytes short — `WeaponTuneList` dereferenced garbage (`P->C00000009`). Root cause: a Map element's VALUE sits at `+valueOffset` inside each `TPair` (here the `FName` key occupies the front, `ValOff=8` per the DLL walk log: `Data=0x1A85ED399A0 KeySz=8 ValOff=8 Stride=80`), but the drilled crumb's `BreadcrumbItem.FieldOffset` carried only the element-base offset (`index*stride`), so the CE chain resolved `[0] MSB_STR00` to the element base (`0x…99A0`) instead of the value base (`0x…99A8`), dragging every child field 8 bytes short. The Live Walker display was already correct (it navigates by `StructDataAddr` = value base); only the export-facing crumb offset was wrong. Direct struct fields (`OriginalPlayer → SkillIds`) and struct/set-array elements were unaffected because their value == element base (delta 0) — which is exactly why only map-value drills broke. **Fix:** new `LiveWalkerViewModel.MapValueDrillOffset(parent)` returns the map's aligned value offset (falling back to key size) when the parent view is a Map container, 0 otherwise; `NavigateToFieldAsync` adds it to `field.Offset` for BOTH the struct and pointer drill branches, so the crumb resolves to the value base and all children (and deeper container/array fields) chain correctly. CSX was already correct (it works from absolute addresses, and its inline map expansion already adds `valOffset`). +4 tests (helper + SEED-shaped end-to-end CE chain).

**(2) Proxy Deploy status: distinguish a clean folder from "another of our proxy types is deployed".** On the `version.dll` radio, a game that already had OUR `dxgi.dll` deployed read `NotDeployed` — misleading, since the folder IS hooked. New `ProxyDeployStatus.DeployedOtherType` + pure `ProxyDeployService.ClassifyAbsentSelected(deployedProxyNames)`: when the selected type's file is absent but another of our proxy types is present, the status becomes `DeployedOtherType` and the Error column names it (`Deployed as dxgi.dll`); a genuinely empty folder stays `NotDeployed`. `deployedProxyNames` is now computed once up front and shared with the existing 2+-coexist `BuildConflictMessage` (which still lists all when 2+ are deployed, so the single-name message is suppressed to avoid duplication). +3 tests (`ClassifyAbsentSelected_*`), `ProxyDeployStatus_AllValues` 6→7.

## 2026-06-18 — Opt-in "Deep" by-value scan: reach values buried in deeply-nested containers (single + group; builds 1283-1285; in-game VERIFIED on SEED)

SEED in-game test of the new group scan exposed the limit: group `10/20/15` found only shallow `ParticleModuleLocationPrimitiveSphere` floats, not the real target `Tunes` — a `TArray<int>` buried at `SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]`.

**Corrected diagnosis.** First read said "value scan can't reach this by design" — **wrong**. `ScanForValue` ALREADY has a recursive deep-container pass (build 1206, `WalkContainerLeaves` gated by auto `needsDeepWalk`, which *does* fire for `BPLifeSaveData_C`), and its own comment cites that exact `...Tunes[N]` path. So single-value was designed to find it; the user's "not found" was **common-value noise** (10/11 match thousands of shallow fields, burying the deep hit). The genuine gap was the new **group** scan: its P1 leaf enumeration (`CollectGroupLeaves`) did direct + struct descent only, with NO container walk.

**Fix — opt-in `deep` (default off, so the existing paths stay byte-identical).**
- **Group deep**: a new per-object pass reuses the SAME recursive `Aura::WalkContainerLeaves` the snapshot uses, bucketing leaves into **blocks** — each numeric container (a `TArray<int>` like `Tunes`) and each struct-array/map element is its own block — and runs `Orden::MatchGroup` per block. So a group is matched WITHIN one array (the owner's "array as a block" rule): `Tunes` = {10,15,20,…} → 10/20/15 land on distinct indices → hit.
- **Single deep**: `deep` forces the deep pass on every class (not just the auto-`needsDeepWalk` set), reaching containers the heuristic misses.
- `Radar::GroupSlotMatch` gained an absolute `leafAddr` (direct = obj+offset, deep = container element address); refine + SDR feasibility now key on `leafAddr` (stale on container realloc → SEH-safe read faults → drop, same as the single-value container refine). Pipe `deep` flag on `begin_value_scan` / `begin_group_scan`; a shared **"Deep (nested containers)"** checkbox in both Single and Group input areas (default off, with a tooltip warning it's heavier). Known limit carried from the walker: scalar-VALUED maps (`TMap<Name,int>`) values aren't emitted (struct-valued maps are) — doesn't affect Tunes.

Bounded by the same caps as snapshot capture (depth 4 / 256 per container / 50k elements per object / 15s deadline). 620 dll + 1585 C# (+9 group/deep) green; AOT publish clean (46.3 MB). **In-game VERIFIED on SEED (UE4.27): both single Value Search and Group Search pass; Deep mode surfaces the buried `Tunes` block.** This largely delivers the P3 "numeric containers as blocks" item early.

## 2026-06-18 — Multiple Values Group Scan (P1): object-aware CE "Group Scan" in the Value Search tab (builds 1276-1278; new `Orden` module; in-game VERIFIED on SEED)

New Value Search **mode** that finds objects holding ALL of N values (2..4) at **distinct** numeric-property offsets, in any order — the object/schema-aware analogue of Cheat Engine's Group Scan. The selling point is selectivity: matching Str + Def + Dex + Int simultaneously narrows thousands of single-value hits to a handful, because the AND across slots is multiplicative. Unlike CE's raw-byte group scan, the "block" is one UObject instance and only reflected numeric **property leaves** are matched, so there are no byte-alignment false positives and the located offsets are real property offsets (directly usable for CE export / Live Walker).

**Architecture — source-agnostic matcher seam.** New header-only Frieren module **`Orden`** (歐爾登, "order"; `dll/src/Orden.h`) is the pure SDR/assignment core: given a block's numeric leaves (`{position, width, bytes}`) + N slot targets, it decides whether all N can be satisfied by **distinct** leaves (a System of Distinct Representatives, via Kuhn's augmenting-path matching) and returns each slot's converging match list. It never reads game memory or touches GObjects — the caller produces the leaves — so Snapshot / SPC Query / Class Pivot can feed their own leaf sources later without dragging in the scanner (the explicit extension seam the owner requested). Reuses `Radar::NumericTargetSet` so "24" transparently matches int16/int32/int64/float/double leaves. +5 unit tests (distinct / duplicate-value SDR / missing-value reject / multi-width / convergence).

**DLL.** `Radar`: `SlotSpec` / `GroupSlotMatch` / `GroupCandidate` / `GroupSession` + a sibling **`GroupSessionManager`** mirroring `SessionManager` (same 300s expiry, V3-C view cache, `BuildGroupOrderedView`) — kept separate so the battle-tested single-value path is untouched. `Aura::ScanForValueGroup` runs **single-threaded** (mirrors `CaptureSnapshotChunk`; group result sets are small by construction) — per object it enumerates numeric leaves (direct + depth-capped StructProperty descent, matching `ScanForValue`'s reach; containers are P3), runs `Orden::MatchGroup`, and emits an object-level candidate; refine (`RefineGroupCandidates`) re-reads each slot's located offsets, keeps those still equal to the new target, and drops the object when any slot empties or no distinct assignment survives. `Fern`: `begin/refine/query_group_candidates` + `end_group_scan` + `GroupCandidateToJson` (object + nested slots); command strings in `Renge.h`. Single-value path emits identical bytes — **zero regression**.

**UI.** Value Search tab gains a **Single / Group** toggle (`ToggleSwitch`). Group mode shows a 2..4-row editable input grid (per-row width scope + value, ＋/− rows) and a **master-detail** results grid: one row per matched object, expandable to its located per-slot fields (🔒 once the offset locks). Each slot leaf carries its denormalized owner so it drives the SAME four handoffs as a single-value hit (Live Walker / Locate-in-GWorld / Copy / Pivot); the object row hands its class to Instance Finder. Server-side filter/sort/window reuse the V3-C shape. New `Orden::MatchGroup`-fed models are plain POCOs (AOT-safe, manual `JsonObject`).

**Decisions (owner-approved):** repeated-value ambiguity → per-slot convergence lists that narrow + lock (correct SDR, not greedy); per-slot type → `NumericNoByte` default, `NumericAll` override (P1); refine → exact-per-slot (prev-value modes = P2); UI → mode toggle within the tab. GAS attribute-component cross-object reach is a narrow opt-in P4.

620 dll_helpers (+5 Orden) + 31 utf8 + 1582 C# tests (+6 group) green; AOT publish clean. **In-game VERIFIED on SEED** (Value Search + Group both pass). P2 (prev-value per slot + locked-offset table + Copy CE), P3 (numeric containers as blocks — since delivered via Deep, builds 1283-1285), P4 (Attribute Component) tracked in todo.

## 2026-06-18 — Live Walker bookmark fixes: PLV_game routing bug + slot tooltips + selection/scroll restore (build 1275; UI-only, in-game verify pending)

Three bookmark fixes on the Live Walker tab, all UI-side (DLL untouched).

**1. "Saved at PersistentLevel, jumped to PLV_game" — World actor-list view hijacked the restore.** The UI `view-0.log` revealed the real path: the user had drilled `PersistentLevel → OwningWorld`, and `OwningWorld` points back to the UWorld, so that breadcrumb's address equals `_cachedWorld.WorldAddr`. The four "is this the GWorld view?" checks (`NavigateToBreadcrumbAsync`, `GoBackAsync` ×2, `LoadBookmarkAsync`) used **address-equality only** → `PopulateFromWorld` (the synthetic actor list, headed by the world name "PLV_game") replaced the saved object instead of walking the UWorld instance. Only the auto-refresh path was already guarded (its comment documents the same "sub-World shares GWorld address" trap via `Breadcrumbs.Count == 1`). Fix: new `IsGWorldActorListRoot(crumb)` = `crumb.FieldName == "GWorld" && _cachedWorld != null && crumb.Address == _cachedWorld.WorldAddr`, applied at all four sites. **`FieldName == "GWorld"` is the unique discriminator** — no UObject field is named "GWorld", only the synthetic Start-from-GWorld / locate-spine root crumb is. Deeper crumbs (OwningWorld) now fall through to a normal instance walk. Two new tests cover both routes (deep-crumb-walks-instance vs. GWorld-root-shows-actor-list).

**2. Slot tooltips.** `BookmarkSlot.TooltipText` is now a **computed** read-only property (notified via the `IsOccupied` setter): empty slots read *"Bookmark N: empty — no bookmark saved. Click ★ then this slot to save…"*, occupied slots read *"Jump to bookmark N: Class :: Object … Click to restore this view."* The ★ tooltips (`str.Tip.LiveWalker.BookmarkSave` / `…SaveActive`) now spell out the two-step click-★-then-slot flow.

**3. Selection + view-position restore.** Saving a bookmark now also captures the **selected row(s)** (one or many, from `_selectedFieldsSnapshot` with a `SelectedField` fallback) and a **scroll anchor** (the topmost visible row). Loading re-selects those rows and scrolls the anchor back into view via two new VM↔View events (`CaptureViewAnchor` / `RestoreBookmarkView`). Note: the Avalonia `DataGrid` (12.0.0) exposes **no public pixel-offset scroll API** — `_verticalOffset` / `SetVerticalOffset` are internal and reflecting into them would violate the AOT/no-reflection rule (its template uses `PART_VerticalScrollbar` + `PART_RowsPresenter`, no `ScrollViewer`) — so view-position restore is anchor-based (`ScrollIntoView` of the saved top row), bringing the same region back rather than a pixel-exact offset. `DataGrid.SelectedItems` is a get-only but mutable `IList` (guarded with a `NotSupportedException` → `SelectedItem` fallback).

605 dll_helpers + 31 utf8 + 1576 C# tests green (+7 new bookmark tests); UI AOT publish clean (build 1275). In-game verification pending.

## 2026-06-17 — UE5.6+ enum support: FNameData UEnum::Names container + FEnumProperty::Enum offset (build 1268; FNameData live-confirmed on TQ2)

Two enum-layout fixes, both UE5.6+/5.7 changes, surfaced via TQ2 (forked UE5.7) and
the Solarpunk investigation.

**1. UEnum::Names container changed in UE5.6+ (new `Neu` module, 諾伊).** UE5.6 replaced
the interleaved `TArray<TPair<FName,int64>>` at `UEnum::Names` (offset unchanged, 0x40)
with `FNameData` = `{ UPTRINT TaggedNames@+0 (FName*, &~1), UPTRINT TaggedValues@+8
(int64*, &~1), int32 NumValues@+0x10 }` — a struct-of-arrays with tagged pointers
(verified vs UE5.7.4 `Class.h:3390`). The old single-format reader mis-read TaggedValues
(a pointer) as the legacy array count → detection failed → enum names showed as raw ints
(stock-UE5.7 Solarpunk: detection failed entirely). New dependency-free `dll/src/Neu.h`:
pure `BuildLayout`(known format) / `DetectLayout`(try-both) / `ReadEntry` over an injected
read functor — handles tag-bit masking + FName stride (8 / 0x10 CPN), unit-testable with
synthetic buffers (no live process / FNamePool; string resolution stays in Serie).
`Genau::DetectUEnumNames` now tries BOTH formats at each probed offset and validates via
the member-FName substring check (records `DynOff::bEnumNamesNewContainer`);
`Ubel::ResolveEnumValue` reads via `Neu::BuildLayout` for the detected per-game format.
`s_enumCache` / `GetEnumEntries` / pipe `enum_entries` / every C# consumer (Live Walker
display, CE XML/Field DropDownList, CSX, drilldown) are unchanged — the wire shape is
identical, so the fix flows straight through. +8 `Neu` unit tests (synthetic legacy +
FNameData × normal/CPN, sparse values, tag masking, format disambiguation, edge/bad-ptr).
**LIVE-CONFIRMED on TQ2 (UE5.7):** log shows `UEnum::Names detected at UEnum+0x40 (UE5.6+
FNameData ...)`; `Role`→ROLE_Authority, `RemoteRole`→ROLE_SimulatedProxy,
`NetDormancy`→DORM_Awake resolve, and CE export emits the `0:ROLE_None … 4:ROLE_MAX`
DropDownList (not a Description dump). TQ2 also detects the +0x08 FUObjectItem (200/0) — the
build-1257 quality gate fixed its object resolution too; TQ2 was misdetected as +0x00 before.

**2. `FEnumProperty::Enum` is `FByteProperty::Enum + 8`.** Long-standing latent bug: the
offset detection set `FENUMPROP_ENUM == FBYTEPROP_ENUM == FStructProperty::Struct` at all
four sites (Grimoire default + Genau Phase-B + Genau main path + Ubel CorrectSubclassOffsets).
But `FEnumProperty` has `FNumericProperty* UnderlyingProp` BEFORE its `UEnum* Enum`
(UE5.7.4 `EnumProperty.h:143-144`), whereas `FByteProperty::Enum` is the first subclass
field. So EnumProperty read `UnderlyingProp` as the UEnum* → resolution always failed → raw
ints (masked until fix #1 made ByteProperty enums resolve, exposing the contrast on TQ2:
`Role`/`RemoteRole` named but `UpdateOverlapsMethod`/`SpawnCollisionHandling` raw). Fixed to
`FENUMPROP_ENUM = FBYTEPROP_ENUM + 8` everywhere. Produces the RE-UE4SS-template stock values
(Byte 0x70 / Enum 0x78) and TQ2's shifted 0x74 / 0x7C. ByteProperty path untouched (already
correct) → pure improvement, no regression. **LIVE-CONFIRMED on TQ2:** the EnumProperty
fields that previously showed raw ints now resolve —
`UpdateOverlapsMethodDuringLevelStreaming`→`EActorUpdateOverlapsMethod::UseConfigDefault`,
`DefaultUpdateOverlapsMethod...`→`OnlyUpdateMovable`, `SpawnCollisionHandlingMethod`→
`ESpawnActorCollisionHandlingMethod::AlwaysSpawn` — and CE export emits the full
`0:UseConfigDefault … 4:..._MAX` DropDownList. (Offset arithmetic isn't unit-testable;
confirmed in-game.)

605 dll_helpers + 1569 C# tests green; full build clean (build 1268).

## 2026-06-17 — Aura: bad-dominated classic item pass falls through to +0x08 — stock-UE5.7 (Solarpunk) live-confirmed (build 1257, verified on 1259)

`Aura::DetectItemSize` runs two object-ptr-offset passes — classic `+0x00`
first, then UE5.7+ `+0x08` — but pass 0 early-accepted **any** stride that
resolved `>= 2` named items, **ignoring the `bad` count**. On a stock UE5.7
*reordered* item (`int64 FlagsAndRefCount@+0x00, UObjectBase* Object@+0x08`,
24-byte stride) a mis-strided 16-byte scan lands on the real Object field only
~1/3 of the time (`16i mod 24 ∈ {0,16,8}`), producing a deceptive
`named=66 / bad=69 / null=65` (~1/3 each). 66 ≥ 2 cleared the floor → pass 0
was accepted → the `+0x08` pass (which picks stride-24/offset-8 cleanly) **never
ran**. Symptom: ~46% name resolution, init sanity 4/10, enum-detect fails.

**Fix:** add a quality gate to the early-accept — a name-resolving pass is only
trusted when valid items out-number bad reads (`qualityOk = !bestHasNames ||
bestNamed > bestBad`). A correct layout resolves nearly every non-null slot
(`bad ≈ 0`), so it still accepts exactly as before; a bad-dominated classic pass
now falls through to the `+0x08` pass. The function's existing tentative-fallback
(picks the strongest pass across both offsets) makes regression structurally
impossible — a too-strict gate can only re-route a result through the fallback to
the same stride/offset, never to a worse one. The pass-0 "weak" log line now also
prints `bad=` so the reason is visible.

**Live-confirmed on Solarpunk** (rokaplay, stock UE5.7, `version.dll` proxy,
build 1259 DLL = `c381e7d`+fix): the log now shows
`classic (+0x00) item detection weak (named=66, count=66, bad=69) — retrying
with UE5.7+ object-ptr offset +0x08` → `FUObjectItem size=24, object-ptr
offset=+0x08 (UE5.7+ reordered item) — 200 named, 200 total, 0 bad`. Name
resolution jumped to ~100% (`FindInstancesByClass` reports `named == nonNull`;
`BP_MainPlayerController_C` now returns 2 instances, was 0), sanity 10/10,
GWorld recovered via instance-scan, ProcessEvent dispatch validated. This is the
real stock-5.7+ game the build-1064 `+0x08` work had been waiting on to
live-confirm (the build-1064 note explicitly flagged "still needs live-confirm on
a real stock-5.7+ game"). 567 dll_helpers + 1551 C# tests green;
`DetectItemSize` itself needs a live process so it is not unit-tested. Solarpunk
added to [roadmap.md](roadmap.md) / [test-games.md](test-games.md) / READMEs.

## 2026-06-17 — GodMode: generic invincibility-bool scan (T2) + diagnostics (builds 1254-1256)

Live-test on **SEED BATTLE DESTINY REMASTERED** showed GodMode flipping
`bCanBeDamaged` successfully (`walk-0.log`: `set ON -> rc=1`, bit cleared +
confirmed) but with **no in-game effect** — SEED is a custom battle framework
(`LifeMSUnit : LifeUnitBase : UnitFwBaseUnit`) that doesn't gate damage on the
engine's `CanBeDamaged()`.

- **Diagnostics (1254):** `Solitar::ApplyGodNowLocked` logs the resolved pawn
  address / class / offset / mask / before-after byte (once per toggle); the
  re-assert worker logs (rate-limited) when it has to re-apply a reverted flag
  (drift = the game keeps re-setting it → likely value-based health).
- **Generic invincibility-bool scan (1256, T2):** instead of only `bCanBeDamaged`,
  GodMode now reflection-scans the pawn's whole class hierarchy for
  FBoolProperty fields matching a **universal keyword table**
  (`Solitar::MatchProtectionBool` — invincib / invulnerab / immort / godmode /
  unkillable / cannotdie / muteki / damageimmune / cantakedamage / canbedamaged,
  each with a polarity) and applies ALL matches (ON = protect value, OFF = normal),
  re-asserted by the worker, cached per pawn class. Zero per-game config — same
  philosophy as `PropertyScoringTable`; the matched flags are logged. Conservative
  set (excludes ambiguous deal-damage names like bare `candamage`/`nodamage`).
  Auto-covers games exposing a named invincibility bool; purely value-based games
  (HP number) still need Value Search + Freeze (generic auto-HP-freeze "T3"
  considered, deferred by the user). +19 dll_helpers assertions (matcher polarity)
  → 567. ⚠ in-game live-verify pending.

-----

## 2026-06-17 — GodMode (Solitar): force AActor::bCanBeDamaged, with Lua mailbox on/off (build 1251)

UE4/5-wide damage immunity, zero per-game config. Design contract +
implementation plan: [godmode-spec.md](godmode-spec.md) /
[godmode-implementation-plan.md](godmode-implementation-plan.md).

**Mechanism:** GodMode ON ⇒ the local player pawn's `AActor::bCanBeDamaged`
FBoolProperty bit is forced FALSE, so damage routed through the standard engine
pipeline (`UGameplayStatics::ApplyDamage` → `TakeDamage`, gated on
`CanBeDamaged()`) is dropped. It's the **same single-bit read-modify-write**
`Wirbel::ResolveCursorBit` / `SetMouseCursor` already does for
`bShowMouseCursor`, retargeted to the pawn — **pure memory write, no UFunction
invoke, no game thread**, so it works even in menus. A re-assert worker
re-resolves the pawn every ~300 ms and re-writes on drift, so the flag survives
respawns / level changes. No cached instance pointers (re-resolved per op + per
tick — the 2026-06-10 audit's stale-pointer rule).

- **New module `Solitar`** (索莉塔, roster #11; naming-convention 🟡→🟢). Path B —
  self-contained, public `Ubel`/`Aura`/`Macht`/`DynOff` only, zero `Wirbel`
  coupling. `SetGodMode`/`GetGodMode`/`GetState` + a general `SetActorBool`
  primitive (v2 hook for "force any bool" from Property Search). Worker joined in
  `UE5_Shutdown`.
- **Lua mailbox on/off** (the headline ask): `CMD_PROTECT = 9` + `ProtectOp`
  (SET_GODMODE / GET_GODMODE / GET_STATE). `ProtectionScriptGenerator` emits a
  self-contained CE AA toggle record (tick = ON, untick = OFF) driving the
  mailbox — no helper file. Reachable from the Teleport tab's **Copy CE Script**.
- **Exports** `UE5_SetGodMode` / `UE5_GetGodMode` / `UE5_GetProtectState`; **pipe**
  `set_god_mode` / `get_god_mode` / `get_protect_state`.
- **UI:** a "God Mode" section on the **Teleport tab** (Force ON/OFF + tri-state
  badge + ↻ + Copy CE Script), mirroring the Debug Camera toggle that already
  lives there — no new tab, no `MainTabIndex` shift. **God Mode ON/OFF global-
  hotkey rows added** (build 1252; Teleport hotkey list 16→18).
- **"Invisible" was cut** after review: visual `bHidden` hide isn't useful, and
  "enemies can't detect you" has no universal reflected bool (AI perception is
  per-game) — left to Property Search + the general `SetActorBool` primitive.
- **Tests:** +38 dll_helpers assertions (`Solitar::ApplyBoolBit` single-bit RMW
  leaves the other 7 bits intact) → 548; +10 C# (7 `ProtectionScriptGenerator` +
  3 Teleport VM GodMode) → 1551. Full build green. ⚠ in-game live-verify pending.

-----

## 2026-06-16 — Snapshot/SPC stale-session gating: real per-launch token (process creation time) (build 1227)

Closes the 🔴 top-of-todo bug confirmed on SEED 2026-06-16: build 1216 gated the
Snapshot-diff / SPC per-row Live/Addr/🌍 buttons on `GameSessionId = PeHash-ModuleBase`,
but SEED's EXE loads at a **constant base** (no effective ASLR), so `ModuleBase` was
identical across launches and the gate never fired — old-session snapshots stayed
clickable with stale (post-restart-invalid) addresses.

Fix = swap `ModuleBase` for a true **per-launch token: the game process creation time**.

- **DLL** (`Fern.cpp::FillPointerSnapshot`, shared by `get_pointers` + `scan_status`):
  emits `process_creation_time` = `GetProcessTimes(GetCurrentProcess(), …)` FILETIME
  (hi:lo packed → hex). The DLL runs in-process, so this is the game's own creation
  time — unique per launch even with no ASLR.
- **C#**: `EngineState.ProcessCreationTime` (parsed in `DumpService.BuildEngineState`) +
  a shared computed `EngineState.GameSessionId => $"{PeHash}-{ProcessCreationTime}"`.
  The three former `PeHash-ModuleBase` sites — Snapshot gate (`_currentSessionId`),
  Snapshot capture meta, SPC gate — now all use `state.GameSessionId`, so the format
  is defined once and can't drift.
- **Migration**: existing snapshots stored `PeHash-ModuleBase`; the new current id is
  `PeHash-CreationTime`, so old-format ids never match → they correctly read as a
  different (stale) session and gray out. A relaunch now changes the creation time →
  prior-launch snapshots gray. Same-launch snapshots stay enabled.
- **Back-compat**: DLLs older than build 1227 omit the field → `ProcessCreationTime`
  is `""` → `GameSessionId` degrades to `PeHash-` (no per-launch split, as before).

The 1216 button-gating wiring + `CanUse*` props were already correct — only the
session-id computation changed. Tests: +4 `EngineStateTests` (id format, per-launch
discrimination at constant base, old-format mismatch, empty-creation-time degrade),
+2 `DumpServiceTests` (parse + degrade), updated the capture test's expected id.
1541 C# / 510 dll / 31 utf8 green; full DLL + 3 proxies + UI build clean at 1227.
⚠ in-game live-verify pending (restart SEED → old-session snapshot row actions should
now gray).

## 2026-06-16 — Instance Finder "Locate in GWorld" lands ON the target object (build 1224)

User-reported on SEED: Instance Finder → pick a `BP_LifeSaveData_C` instance → **Locate
in GWorld** stopped on the **holder** object (`BP_LifeGameInstance_C`, with `m_savedata`
highlighted) instead of the object the user actually selected.

Root cause: the Instance Finder handler called `LiveWalker.LocateInGWorldAsync(addr, 0,
null, stopAtParent: true)` — `stopAtParent` deliberately drops the final hop and lands on
the parent pointer. Value Search / Snapshot / SPC all use `stopAtParent: false` (land ON
the target). Fix = flip the Instance Finder wiring in `MainWindowViewModel` to
`stopAtParent: false`, matching the others; the full `GWorld→…→target` spine is still in
the breadcrumb, so the holder is one click up via `Parent ↑`. Updated the
`LocateInGWorldAsync` doc comment (the only remaining `stopAtParent: true` consumer is
Interesting Funcs, whose "where do instances of this class live" semantic genuinely wants
the holder). C#-only, no DLL change. 1535 C# / 510 dll / 31 utf8 green. ⚠ in-game
live-verify pending (should now land on `BP_LifeSaveData_C` showing its own fields).

## 2026-06-16 — Property Search: compact `finder` button + opt-in deep struct/container descent (build 1222)

Two user-requested Property Search changes:

1. **`finder` row button** (rename only — behaviour already shipped). The per-row
   `Find Instances` button is now captioned **`finder`** with a fuller tooltip
   ("switch to the Instance Finder tab, fill in this row's class name, and run the
   search"). The fill-class-+-switch-tab-+-auto-run wiring was already in place
   (`PropertySearchViewModel.FindInstances` → `NavigateToInstanceFinder` →
   `MainWindowViewModel` runs `InstanceFinder.SearchCommand`); this is purely the
   `en.axaml` caption/tooltip update, matching the build-1216 compact-caption trend.

2. **Deep (structs/containers) descent** — opt-in checkbox (default **off**) that makes
   a field buried inside a struct or struct-typed container findable by name. Closes
   the todo "Property Search: descend into struct / container-element inner properties"
   (user-flagged on SEED: `GP` lives at `BP_LifeSaveData_C.SaveSlotList[].MsTuneData.GP`
   and was previously unreachable by name — only via Value Search / Live Walker).
   - **DLL** (`Aura.cpp`): new schema-only walker `CollectSchemaLeaves` enumerates every
     scalar leaf reachable through `StructProperty` members + struct-typed
     `TArray/TSet<FStruct>` / `TMap<K,FStruct>` elements, emitting a synthetic dotted
     path (`SaveSlotList[].MsTuneData.GP`). Depth-capped (4), path-cycle guarded against
     self-referential structs, hard-capped at 4000 leaves/class. `SearchProperties` gains
     a `deep` param: after the shallow direct-field loop it matches the keyword against
     each leaf's **last segment** (same "property named X" semantics), deduped by the
     ROOT field's defining class + dotted path (mirrors the shallow inheritance collapse,
     bumping `inheritedByCount`). Nested matches set `isNested=true`, carry the leaf
     `FProperty*` as `fieldAddr` (so Find Funcs xref works), and keep `previewClassAddr=0`
     so Phase-2 preview resolution naturally skips them (no instance read). Shallow search
     is byte-unchanged when `deep=false`. `SearchPropertiesBatch` (Interesting Properties)
     is intentionally NOT deep — smaller blast radius.
   - **Pipe** (`Fern.cpp`): reads `deep` (default false), serialises `is_nested` on
     nested rows only.
   - **UI**: `DumpService` / `IDumpService` gain a `deep` arg; `PropertySearchViewModel`
     gets a `DeepSearch` toggle (status shows `[deep]`). `PropertySearchMatch` gains
     `IsNested` + computed `ShowScalarActions` (`!IsNested`) + `PropNameTooltip`. In the
     grid, the Property column became a template column with the path tooltip, and
     **Copy Offset + Freeze are hidden for nested rows** (a dotted path has no single
     class-absolute offset) — `finder` + Find Funcs stay. New `en.axaml` strings:
     `str.PropertySearch.Deep` + `str.Tip.PropertySearch.Deep`.
   - **Scope decision** (user): findability-first — nested rows expose `finder` (owning
     class) + Find Funcs (leaf `FProperty`); Copy Offset / Freeze are out of scope for
     all nested matches.

Tests: +2 `DumpServiceTests` (deep default-false on the wire; deep=true round-trips +
`is_nested` parses) +3 `PropertySearchMatchTests` (nested hides scalar actions + path
tooltip; direct field unaffected; `IsNested` back-compat default). **1535 C# / 510 dll /
31 utf8 green; full DLL + 3 proxies + UI build clean at 1222.** ⚠ in-game live-verify
pending (deep search on SEED should surface `SaveSlotList[].MsTuneData.GP`; `finder` on a
nested row lists `BP_LifeSaveData_C` instances).

## 2026-06-16 — Compact per-row buttons + stale-session gating on Snapshot/SPC (build 1216)

UI tightening + a correctness gate, on user request (the Interesting Funcs row, with
5 buttons, is the density target):

- **Compact captions** (all per-row, in `en.axaml`): `Copy Address` → **`Addr`**
  (everywhere incl. Value Search), `Open in Live Walker` → **`Live`** (datagrid
  buttons), and the GWorld button → **`🌍` icon only** (dropped the " GWorld" text).
  Tooltips keep the full explanation. Confirmed the 9 caption keys are datagrid-only
  before changing values.
- **Stale-session gating** — the Snapshot-diff and SPC per-row Live / Addr / 🌍
  buttons are now **disabled unless the address-source snapshot is the CURRENT live
  session** (its session-local ObjAddr is meaningless after a restart/reinject):
  - Live session id = `PeHash-ModuleBase` (matches capture-time `GameSessionId`),
    captured in each VM's `SetEngineState`.
  - **Snapshot**: gate on the New pick — `CanUseDiffRowActions =>
    DiffB.GameSessionId == current` (user: "看 New Session"); 🌍 also needs GWorld.
  - **SPC**: gate on the newest selected snapshot (by Id == capture time) —
    `CanUseResultRowActions => NewestSelectedSessionId == current` (user:
    "看時間最近的一筆"); raised on selection change.
  - Wired as `IsEnabled` on the buttons (the user's "buttons disabled" requirement);
    the commands themselves stay guard-free so the existing command unit tests
    remain valid (the button is the gate, and it's the only invoker).
  - **Known limitation — CONFIRMED broken on SEED (restart-verified 2026-06-16)**:
    `GameSessionId = PeHash-ModuleBase` distinguishes launches only when ASLR moves
    the base. The owner viewed snapshots AFTER restarting the game and the buttons
    still did NOT gray → SEED's EXE loads at a **constant base** (no effective ASLR),
    so `ModuleBase` is identical across launches and the gate can't tell an old
    session from the current one. A true per-launch DLL token (process creation
    time) is the proper fix — promoted to the top of todo.md "▶ Next up". (The 1216
    button wiring + `CanUse*` props are correct; only the session-id computation
    needs to change.)

DLL re-stamped to 1216 (no source change). 510 dll + 31 utf8 + 1530 C# green; AOT clean.

## 2026-06-16 — Per-row action buttons across Snapshot/SPC/Instance Finder + not-found clears Live Walker (build 1213)

UI-consistency pass on user request, making the per-row action buttons uniform with
Value Search (Open in Live Walker / Copy Address / 🌍 GWorld, in that order, same
teal/gold/purple styling):

- **SPC Query**: the three top-toolbar buttons (Open / GWorld / Copy) moved into a
  per-row `DataGridTemplateColumn`; toolbar keeps only Run + Refresh. Commands already
  took the row, so this is pure XAML.
- **Snapshot diff**: top Open/Copy moved per-row, **plus a new per-row 🌍 GWorld**.
  `SnapshotViewModel` gained `IsGWorldAvailable` (set in `SetEngineState` from
  `HasGWorld`), a `LocateInGWorld` event, and a `LocateRowInGWorld` command (reach mode
  via the row's deep `PropName` path); wired in `MainWindowViewModel` exactly like SPC.
  In-session diff only (ObjAddr is snapshot-B's session-local address). New strings
  `str.Snapshot.Diff.LocateGWorld` + tooltip.
- **Instance Finder** (address-search container matches): the plain "Open" became teal
  "Open in Live Walker", and a gold "Copy Address" was added (`CopyContainerAddress`
  copies the owner address via the same `AddressHelper.FormatAddress` as the
  per-instance copy); button column widened to Auto.

**Locate-in-GWorld not-found now clears the Live Walker view.** Previously a failed
locate left the *previous* object on screen under the failure message, looking like the
result. New `LiveWalkerViewModel.ClearDisplayedNode()` (inverse of `UpdateDisplay`) empties
fields/breadcrumbs/header; called in both `LocateInGWorldAsync` and
`LocateContainerInGWorldAsync` when `!path.Found`, except on a user `cancelled` (which
preserves the current view). The failure reason stays in `StatusText`.

DLL re-stamped to 1213 (no source change) so the build-match badge stays green.
510 dll_helpers + 31 utf8 + 1530 C# green; AOT publish clean (XAML compiled).

## 2026-06-16 — Snapshot capture heartbeat: live status during slow chunks (build 1212)

Follow-up to the 1211 stall fix, on user feedback: *"if there's no error, letting it
run as-is is fine — but the status needs feedback; sitting on a frozen number, the
user can't tell hung vs. working."* The capture loop only refreshed `StatusText`
**after** each chunk returned, so a slow (deep-container) chunk left the line frozen
for seconds and looked hung.

**Fix (`SnapshotViewModel`, C# only).** A UI-thread `DispatcherTimer` heartbeat (400 ms)
re-renders the status while capturing — the UI thread is free during the chunk's
`await`, so it ticks even mid-chunk. `RenderCaptureStatus` now shows: the latest chunk
counts, a **live-ticking elapsed clock**, animated trailing dots (fixed 3-char slot so
the count doesn't jitter), an ETA that **auto-omits when a slow chunk makes it stale**,
and — when the current batch has run > 3 s — an explicit *"still scanning this batch
(Ns)"* with its own ticking timer. The loop now feeds counts into fields + renders on
each chunk completion; the heartbeat is stopped before every terminal message
(Finalising / cancelled / failed) and in `finally`. Together with 1211, a slow batch
now reads unmistakably as "working", not hung. **1530 C# tests green.**

## 2026-06-16 — Snapshot capture stall fix: deterministic per-object element cap (build 1211)

User-reported on SEED: after the NaN fix, **Snapshot capture stalled near 80%** — at
23 s it showed ~80 % / "~5 s left", but one chunk (objects ~22,600–22,800) then took
~24 s and the user cancelled (perceived hang). The pipe log confirms steady chunks
then a stall on chunk 114; `SendAsync` has no read timeout, so the disconnect was the
user's Cancel, not a timeout.

Root cause: the recursive capture (build 1205) walks every object's containers to
depth 4; a cluster of deeply-nested WIDE-container objects each hit the **2 s
per-object wall-clock deadline** added in 1208, so one 200-object chunk ground for
~24 s. The per-container 256-elem cap bounds flat width but nested wide containers
still blow up combinatorially (256^depth).

**Fix (`WalkContainerLeaves` / `WalkLeafLimits`).** Added a **deterministic per-walk
element-visit budget** (`maxTotalElems`, threaded as a shared `int64_t* visited`
counter through the recursion; counts each *allocated* element processed, bails fast
via a top-of-frame + per-element check). Snapshot sets it to **50,000** (far above any
real object, but caps the blow-up to tens of ms) and lowers the wall-clock abort to a
**750 ms backstop** (for pathological per-element cost only). Deterministic is the key
property: the walk order is stable, so two captures of the same state truncate
*identically* — SPC diff/join stays consistent, which a wall-clock cutoff would break.
Value Search's deep pass gets the same 50k cap (its 15 s global deadline stays the
cross-object backstop). On SEED the stall chunk drops from ~24 s to a few seconds.

Supersedes the build-1208 cancel-only-then-2 s deadline as the primary bound; partially
addresses audit #6 (no per-object budget). Caps are tunable. **510 dll_helpers + 31
utf8 + 1530 C# green.** ⚠ in-game live-verify pending (capture completes without stall;
deep `GP` still captured).

## 2026-06-16 — Snapshot NaN-float capture crash fix (build 1210)

User-reported regression: on SEED, **Snapshot "Capture failed — Cannot store 'NaN'
values"** — the entire capture aborted. Root cause: the recursive capture (build
1205) now reaches deep `FloatProperty`/`DoubleProperty` leaves, and one held a
non-finite bit pattern (NaN / ±Infinity — uninitialised slack, garbage in a deep
struct-array slot, or a genuinely-NaN gameplay float). `SnapshotNumeric.TryFromHex`
faithfully decoded it to `double.NaN`, which was bound to the `numeric_value REAL`
column; `Microsoft.Data.Sqlite` rejects NaN/Infinity, failing the whole chunk
transaction.

**Fix (`SnapshotNumeric.TryFromHex`).** Return `false` for non-finite float/double
results (`double.IsFinite`), so `numeric_value` is stored as `NULL` instead — the
raw bits are still preserved in the `hex` column, and SPC/diff direction (which
can't compare NaN meaningfully anyway) simply treats it as "no numeric value".
One root fix covers both bind sites (scalar + struct-array element) and every
reader. +6 regression tests (float/double qNaN/±Inf). **1530 C# tests green.**

Not a recursion *correctness* bug — the value was captured fine; the crash was
purely the REAL-column bind. Same class of "the deeper walk surfaced data the old
shallow walk never reached" as the build 1208 drill gap.

## 2026-06-16 — Multi-`[N]` GWorld drill + map-leaf guard + snapshot deadline (build 1208)

Closes the deep-container story's last gap, surfaced when the SEED user pressed
"Locate in GWorld" on a deep Value Search hit
(`SaveSlotList[0].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]`) and it landed on
the intermediate `MsTuneData` node instead of the value. A 4-agent audit (drill-site
mapping + adversarial scan/capture verification) confirmed: the recursive **scan/capture
is correct — no regression**; the value is always FOUND. The gap was purely the UI
**land-ON-it drill**, which only parsed the FIRST `[N]`.

**Multi-`[N]` display-path drill (C#).** Replaced the single-`[N]`
`TryParseStructArrayInner` + `DrillToStructArrayInnerAsync` with
`TryParseContainerPath` + `DrillDisplayPathAsync` (`LiveWalkerViewModel`):
- `TryParseContainerPath` splits the display name into ordered `(name, index)`
  segments — `name[N]` → container element, bare `name` → direct struct field — and
  returns true only when ≥1 `[N]` is present (a plain field falls back to the single
  offset scroll). Handles arbitrary depth; rejects malformed `[]`/`[-1]`/empty segments.
- `DrillDisplayPathAsync` walks each segment from the owner view: drill the container
  by name → select `[N]` → (if not last) descend into the struct element; bare names
  navigate a direct sub-struct; the final segment is selected/scrolled-to. Parity with
  the Instance Finder structured-chain `DrillContainerChainAsync`.
- Wired into BOTH the VS/SPC `LocateInGWorldAsync` reach branch AND
  `NavigateToInstanceFieldAsync`. The latter also **fixes the Open-in-Live-Walker
  offset-0 mis-select**: a deep candidate carries `fieldOffset=0`, which previously
  matched the first offset-0 field; container paths now drill explicitly instead.

**Audit-surfaced DLL fixes (`Aura.cpp`).**
- **Map leaf guard (#1).** `WalkContainerLeaves`' leaf-container branch fired for a
  scalar-value `TMap` (`sides[0].structAddr==0`), emitting a malformed leaf at the KEY
  region with the `"K → V"` arrow label as the type — harmless (both consumers reject
  the bogus type) but wrong. Guarded with `cfe.kind != ContainerKind::Map`. Proper
  scalar-map value/key capture filed as a follow-up (todo).
- **Snapshot per-object deadline (#7).** `CaptureStructArrays`' walker had cancel-only
  abort (no time budget, unlike Value Search's 15 s); a pathological deeply-nested
  object could stall a chunk within the 256 × depth-4 caps. Added a 2 s per-object
  `steady_clock` budget; the chunk loop's own cancel poll still handles client-gone.

Audit also confirmed unaffected: Instance Finder address search (structured chain),
Interesting Props/Funcs + Property Search (definition rows, no container path),
Snapshot Diff row-nav (opens owner). Remaining gaps filed in todo.md (proper scalar-map
capture, top-level `TSet/TMap<FStruct>` depth-1 VS leaves, SPC Strict `prop_offset`
migration edge). **510 dll_helpers + 31 utf8 + 1524 C# tests green.** ⚠ in-game
live-verify pending (multi-`[N]` 🌍 lands exactly on the SEED `…Tunes[N]` value).

## 2026-06-16 — Recursive (>1 level) container-leaf capture across all four consumers (builds 1205-1207)

Completes the deep-container story: a value buried at ANY (bounded) depth — e.g.
`SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]` — is now reachable
by **Instance Finder address search** (already recursive since 1198), **Snapshot
capture → SPC Query / Diff**, and **Value Search**.

**Shared recursive walker (`Aura::WalkContainerLeaves`, build 1204/1205).** New file-
internal walker + `EmitStructDirectLeaves` mirror the `FindInContainersDeep` descent
but ENUMERATE leaves via a visitor: every scalar leaf reachable through struct-array /
map / set elements + nested leaf-containers (`TArray<int>`) + direct sub-structs, to
`maxDepth=4` / `maxElems`, reusing the cached `GetClassContainers`. The visitor gets the
full dotted+indexed path + the container-hop depth, so each consumer applies its own
depth boundary. One engine; two consumers.

**Snapshot capture (build 1205).** `CaptureStructArrays` rewritten to drive the walker.
The FULL nested path is baked into `SnapshotArray.field` (e.g.
`SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes`) — **no schema change** —
so SPC Query + Snapshot Diff, which key on `array_field + elem_index`, get deep support
for free. Nested leaf-containers are captured (inner prop `""`); TOP-LEVEL leaf arrays
(`depth<2`) are skipped to avoid DB bloat (matches prior behaviour). Inner-key
(reorder-immune Array-Pivot join) preserved per element. C# diff/SPC render `path[N]`
(no trailing dot) for empty-inner-prop leaf-containers. **Snapshot + SPC live-verified by
the owner before this; deep nesting now flows through.**

**Value Search (build 1206).** Kept the fast static depth-1 paths (`StructArrayInner`
direct leaves + the `TArray<leaf>` branch); added a per-class `needsDeepWalk` gate
(true only when a container's element struct itself has containers — one cheap
look-ahead) + a per-instance `WalkContainerLeaves` pass that emits candidates for leaves
at container depth **>= 2** (so no double-count with the static paths). Deep candidates
get a per-path descriptor (full display name, `elementIndex=-1`); vectors skipped (the
walker yields scalar leaves, not whole vector structs). Reuses `emitCandidate` +
`multiResolve` + the lean pools. Same 15s deadline / cancel.

**Caps / cost.** `maxDepth=4`, `maxElems=256`; gated per class so the common case (no
struct-element nesting) pays nothing; cancel/deadline poll every 64 elements. Deep VS
candidates' 🌍 deep-drill degrades gracefully for multi-`[N]` paths (the by-address
path fully drills; VS finds the value).

Tests: +1 C# (`Diff_DeepNestedPath_AndLeafContainer`) → 1520 C# / 510 dll / 31 utf8
green; full build + 3 proxies clean. **In-game verify pending**: deep Snapshot diff +
deep Value Search hit on SEED.

-----

## 2026-06-16 — Struct-array values reach end-to-end: VS GWorld drill + SPC/Diff inclusion (build 1203)

Two live follow-ups after the build-1202 Value Search struct-array descent shipped.

**Value Search 🌍 now deep-drills to the inner value (Issue A).** Value Search found
`SaveSlotList[1].GP` but "Locate in GWorld" landed on the outer `SaveSlotList` array,
not the inner `GP` — the reach path's single-shot `_pendingScroll` can't chain array →
element → inner field, and `ParseElementIndexSuffix("SaveSlotList[1].GP")` is -1 (doesn't
end in `]`). Fix: `LiveWalkerViewModel.LocateInGWorldAsync` now detects a struct-array-
inner display name via new pure `TryParseStructArrayInner` ("ArrayPath[N].InnerPath" →
array path, element index, inner dotted path) and runs an explicit awaited drill
`DrillToStructArrayInnerAsync` (navigate the array's leading direct-struct segments →
container → element `[N]` → inner direct-struct segments → select the leaf by name).
Mirrors the Instance Finder container deep-drill; degrades to "drill manually" if a hop
can't be matched. Handles nested array paths + nested inner structs (by name, so no
numeric intra-offset needed).

**Snapshot Diff + SPC Query now include struct-array elements (Issue B).** Capturing
`GP-1` then `GP+1` and running Diff produced nothing — the snapshot ALREADY captures
struct-array elements (`Aura::CaptureStructArrays` → `array_field`/`elem_index`/
`inner_prop_name`), and **Class Pivot already consumes them**, but Diff + SPC filtered
them out with `WHERE array_field IS NULL`. Fix (`SnapshotStore`): drop that filter in the
diff A/B streams + the SPC load, and extend the join key with `array_field` + `elem_index`
so distinct elements (`SaveSlotList[0].GP` vs `[1].GP`, identical class/owner/inner-prop)
don't collide — direct fields contribute `""`/`-1`, leaving their keys unchanged. Rows
display the full path `SaveSlotList[1].GP` (`SpcDisplayProp` / inline build). Array rows'
owner-relative `prop_offset` is zeroed (it doesn't address the separate heap element);
`ObjAddr` stays the owner so "Open in Live Walker" still reaches it. **The deep-nested
case (a value inside a TArray/TMap nested *inside* a struct element) is still capture-
limited** — `CaptureStructArrays` does one struct-array level — so SPC/Diff see 1-level
struct-array values (GP), matching Value Search's descent depth.

Tests: +7 C# (`TryParseStructArrayInner` facts/theory) + reworked the
`WriteChunk_WritesArrayRows_*` diff test (was "excluded", now asserts HP +
`Cargo[0].Quantity` both change, `Cargo[1]` unchanged) → **1519 C#**; 510 dll / 31 utf8
green. Full build + AOT clean. **In-game verify pending**: VS 🌍 lands on `GP`; Snapshot
Diff of a GP change lists `SaveSlotList[1].GP`.

-----

## 2026-06-16 — Value Search descends into struct arrays (build 1202)

Live-testing the deep-container work surfaced that **Value Search can't find a value
inside a struct-array element** — scanning for `GP = 46643`
(`BP_LifeSaveData_C.SaveSlotList[1].GP`, an int inside a `TArray<FStruct>` element)
returned 0 candidates, because `ScanForValue` never descended `TArray<FStruct>`. The
user asked to close that gap (the previously-deferred "Value Search descends structs").

**DLL (`ScanForValue`).** New `ScanContainer::StructArrayInner` + a
`collectStructArrayInner` collector: when field collection hits a `TArray<FStruct>`
(non-vector scan), it walks the element struct's LEAF fields — including those nested
in *direct* sub-structs — and emits one `StructArrayInner` ScanField per leaf, carrying
the outer array's object-relative offset + the leaf's element-relative offset
(`structInnerOffset`) + the element stride. The per-instance scan reads the TArray
header and tests each element at `arrayData + idx*stride + structInnerOffset` (reusing
the existing `scanElement` predicate paths + the 10M circuit-breaker + 15s deadline +
cancel poll). **One struct-array level**: containers nested *inside* an element
(`TArray`/`TSet`/`TMap`) are separate heap allocations and are NOT followed — the
by-address deep scan (`FindInContainersDeep`) covers those. Vector scans keep their
whole-struct-match semantics (descent gated off). Display: `FieldDisplayName` now honours
a `[]` placeholder so a struct-array-inner field renders `SaveSlotList[3].GP` (index
after the array name, not appended at the end), via the descriptor name
`SaveSlotList[].GP`. +3 dll_helpers EXPECTs → **510 dll_helpers green**.

**Snapshot / SPC / Pivot (engine analysis, per the user's "same-engine → together,
else todo").** These are *separate* engines from `ScanForValue`:
- **Snapshot capture ALREADY captures struct-array elements** (`Aura::CaptureStructArrays`
  → `array_field`/`elem_index`/`inner_prop_name` columns), so `GP` is already in the DB.
- **Class Pivot ALREADY supports array-element fields** (`SnapshotStore.ListPivotArrayFieldsAsync`
  + the `array_field IS NOT NULL` array-pivot queries).
- **SPC Query + Snapshot Diff still exclude them** (`WHERE array_field IS NULL`,
  SnapshotStore lines 534/562/681) — the one remaining gap. It lives in the C#
  `SnapshotStore`/`SpcEngine` (a different engine from the Value Search DLL change), so
  per the rule it's filed in [todo.md](todo.md) rather than bundled here.

1512 C# / 510 dll / 31 utf8 green. **In-game verify pending**: scan `46643` on SEED →
expect the `SaveSlotList[1].GP` candidate to appear.

-----

## 2026-06-16 — Deeply-nested container values: recursive find_by_address + multi-level GWorld drill (build 1198)

The first live test of Locate in GWorld (build 1193) surfaced two gaps in the
todo: a value buried **>1 container level deep** couldn't be found by
`find_by_address` at all, and the deep-drill (which lands ON a value inside a
struct-array element) was only fed by the Instance Finder. After investigation
the user chose **recursive descent + multi-level drill, all through the Instance
Finder by-address path** (Value Search / SPC wiring intentionally folded in —
see "Why not VS/SPC" below).

**Repro (SEED BATTLE DESTINY REMASTERED).** `find_by_address 0x228F1251BE8`
returned **0 matches** while a sibling 1-level value (`SaveSlotList[1]+0x4D8`,
the inline `GP` field) was found instantly. The deep int lives at
`BP_LifeSaveData_C.SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[N]`
— six container hops, each nested container a **separate heap allocation**.

**Why the shallow scan can't see it.** `Aura::FindInContainers` bounds-checks
`addr` against each container buffer at a fixed offset within the object
(`GetClassContainers` flattens DIRECT-struct nesting). That finds values stored
INLINE in a buffer — a `TArray<int>` element, or a field of a struct stored
inline in a `TArray<FStruct>` (why `SaveSlotList[1]+0x4D8` works). But a
`TArray<int>` whose *header* is inline in a struct element while its *data*
lives elsewhere on the heap is a separate allocation — `addr` falls in no
top-level buffer.

**Recursive deep descent (DLL).** New `Aura::FindInContainersDeep` +
`MatchAddrInStructContainers` recurse into struct elements: at each level, if
`addr` is inside a buffer it's the terminal hit (a leaf, or an inline struct
field); otherwise, for struct-element containers (Array/Set element struct, Map
value/key struct) descend into each element and recurse, building the full hop
chain. Bounded by `maxDepth` (UI sends 5), a 256-element-per-container probe
cap, the existing 15s deadline, and **early-out on the first match** (one match
answers a by-address lookup). It runs **only as a fallback** when the shallow
scan finds nothing AND the caller opted in (`container_depth > 1`), so the
common fast path is untouched (zero regression). `ContainerCacheEntry` now
carries the element/value/key `UScriptStruct*` + map value offset (resolved at
cache-build via new `Ubel::GetContainerInnerStructAddr` + an extended
`GetMapPairLayout` that also returns key/value struct addrs). `GetMapPairLayout`
now uses the value's **real** alignment (`Scharf::RequiredAlignment`) so the
pair stride/value offset match `WalkInstance` exactly — the deep matcher indexes
map slots by the same sparse index the UI shows.

**Multi-level chain (model + pipe).** `ContainerMatch` gains a
`nestedChain` (`ContainerHop[]`); the outermost container stays in the existing
fields, each deeper hop is one container drilled into, and the deepest hop's
intra-offset locates the value. Serialized as `nested_chain` in the
`find_by_address` response; `find_by_address` reads a new `container_depth`
param. `ContainerMatch.DisplayPath` (C#) now spans the full chain
(`…SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[42]`).

**Multi-level GWorld drill (UI).** New `LiveWalkerViewModel.LocateContainerInGWorldAsync(ContainerMatch)`
reaches the owner via the BFS path, then `DrillContainerChainAsync` walks the
chain hop-by-hop: navigate any leading DIRECT-struct name segments (e.g.
`MsTuneData` in `MsTuneData.MsTunes`) → drill the container → select element
`[N]`; nested hops then drill INTO the struct element to continue; the deepest
hop scrolls to the value (a struct field at its intra-offset, or the leaf
element itself). Degrades gracefully — if a hop can't be matched in the live
view it stops and reports the remaining manual path. The Instance Finder
container row's 🌍 now passes the whole `ContainerMatch`
(`LocateContainerInGWorld` event → `Action<ContainerMatch>`); the build-1193
one-level `elementIntraOffset` branch of `LocateInGWorldAsync` was removed (the
1-level case is just a single-hop chain through the new path). Shared spine
builder `BuildBreadcrumbSpineFromPath` extracted.

**Why not VS/SPC (the folded-in todo item).** Investigation found neither
produces a "value inside a struct-array element" candidate today: Value Search's
`ScanForValue` never descends into `TArray<FStruct>` elements, and SPC filters
every array-element row out of its results (`WHERE array_field IS NULL`). So
there was nothing to wire the deep-drill to — and a VS redirect couldn't reach
the SEED value anyway (its first hop `SaveSlotList` is already a struct array).
The by-address path is where the workflow actually lives, so the effort went
there.

**Tests / build.** +7 C# (`DeepContainerChainTests`: chain `DisplayPath` /
`DeepestIntraOffset` / `IsDeeplyNested` + the flattened `BuildContainerDrillPath`
order) → **1512 C#**; **507 dll_helpers / 31 utf8** unchanged green. Full build
clean (DLL + 3 proxies), AOT publish clean (46 MB).

**LIVE-VERIFIED on SEED (build 1199).** `find_by_address 1B06D16B448` →
`BP_LifeSaveData_C.SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]`
(scanned 28116 objects in 50ms), and the container-row 🌍 reached the owner (2
hops) and drilled the full chain to land ON `Tunes[2]` = 20. Two follow-ups from
the test:
- **Live Walker `Addr` copy bug (fixed).** In a container-element view the per-row
  `Addr` button recomputed `CurrentAddress + field.Offset`, but `CurrentAddress` is
  the OWNING struct (the element lives in a separate heap buffer), so it copied the
  owner's field at that offset (e.g. `Tunes[2]`'s `Addr` gave the parent's
  `WeaponId` address). Now `CopyFieldAddressAsync` uses the already-resolved
  `field.FieldAddress` (same value the Address column + Hex/+CE/Edit buttons use),
  falling back to `CurrentAddress + Offset` only when it's absent.
- **"Locate in GWorld depth" moved to the Options flyout.** The depth NumericUpDown
  left the Live Walker toolbar for the top **Options** dropdown (renamed
  `Locate in GWorld depth`, slider 1–32), bound through the `LiveWalker` sub-VM
  (`LiveWalker.GWorldLocateDepth` / `.IsGWorldAvailable` for the dim-when-no-GWorld
  gate).
- **Deep-scan element cap is now configurable (build 1200).** The
  `kMaxElemProbe = 256` constant became a parameter threaded
  `find_by_address`(`container_elem_cap`) → `FindInContainersDeep(maxElemProbe)`, with
  a `Deep container scan cap` exponent-slider in the Options flyout (2^4–2^12,
  default 256) bound via `MainWindowViewModel.DeepScanElemCap(Exponent)` →
  `InstanceFinder.DeepScanElemCap`. Higher reaches values at higher element indices
  in the recursive descent at the cost of speed.

-----

## 2026-06-16 — Locate in GWorld: forward BFS path search (build 1181)

Pressing **Parent** in the Live Walker walks `UObject.OuterPrivate` (the naming
hierarchy) and almost always dead-ends at `/Engine/Transient` — never the
gameplay-meaningful GWorld spine. New feature answers the inverse question:
given a found target, **where is it under GWorld?**

**Forward BFS (the engine).** New pure, header-only shortest-path core
[`GraphPath.h`](../dll/src/GraphPath.h) (`BfsShortestObjectPath`) + a live
adjacency adapter `EnumerateOutgoingObjectPtrs` in [`Aura.cpp`](../dll/src/Aura.cpp)
that REUSES the per-class reference-metadata cache (`GetClassRefMeta`) powering
`FindReferencesToUObject` — the same battle-tested extractor for direct
Object/Class/Interface, Weak/Soft/Lazy, TArray/TMap/TSet-of-objects, and
StructProperty-nested pointers — but enqueues children instead of comparing
against a target. `Aura::FindObjectGraphPath(rootObj, targetObj, maxDepth=5)`
runs the BFS root-agnostically (the pipe handler resolves GWorld → UWorld and
passes it in, keeping Aura decoupled from the GWorld globals). BFS first-hit ==
shortest hops, so "first found == shortest" is free. visited-set dedup bounds it
to O(reachable), 3M-node cap + 20s deadline + `Cancel::Requested()`. The pure
core is unit-tested against a mock graph (shortest-among-two, cycles, depth
bound, root==target, unreachable, abort, visited-cap, reconstruction) — **+10
tests → dll_helpers 507 green**.

**Pipe.** New `find_path_from_gworld` command (`target` + optional `object_addr`
+ `max_depth`) returns the path steps + resolved target + diagnostics; for a
known owning object (Value Search / Instance Finder) it skips the FindByAddress
resolution scan. MulticastSparseDelegate edges are NOT followed (global-TMap walk
per node too expensive). Spec: [pipe-protocol.md](pipe-protocol.md#find_path_from_gworld).

**UI.** `LiveWalkerViewModel.LocateInGWorldAsync` clears + rebuilds the breadcrumb
spine from the path and lands on the target via the existing
`_pendingScrollFieldOffset` machinery. Two behaviours: a property **VALUE**
(Value Search per-row "🌍 GWorld") lands ON the owning object and scrolls to the
value field; an **OBJECT / class instance** (Instance Finder "🌍 Locate in
GWorld") stops at the parent that points to it and highlights the pointer field,
without drilling in. A **GWorld depth** NumericUpDown (default 5, on the Live
Walker tab) sets the search depth. All triggers gray out when GWorld is
unavailable (`EngineState.HasGWorld`, surfaced via each panel's `SetEngineState`).
Not-found surfaces an actionable status ("increase the depth"). 1505 C# green,
AOT publish clean (45.9MB).

**Trigger panels (build 1187).** Wired from all four "open in Live Walker"
sources: **Instance Finder** (both the by-class and by-address searches funnel
through `SelectedInstance` → object/parent mode), **Value Search** (value/reach),
**SPC Query** (per-row 🌍, value/reach — has the live object + changed-field
offset), and **Interesting Functions** (per-row 🌍 — a function isn't a world
object, so MainWindow resolves a live non-CDO instance of its class via
`FindInstancesAsync` first, then parent mode). **Property Search is intentionally
excluded** — its rows are class/property *definitions* (deduped to the defining
class, often abstract → no single live instance); its existing "Find Instances"
button already bridges to Instance Finder, which has the 🌍 button. Gray-out via
`EngineState.HasGWorld` (each panel's `SetEngineState`, or a direct
`IsGWorldAvailable` set for Interesting Functions which has no `SetEngineState`).
In-game live-verification pending.

**Container-match path (build 1188, after first live test).** First in-game test
(SEED BATTLE DESTINY REMASTERED, build 1187) surfaced an accessibility gap, not a
BFS bug: a by-ADDRESS lookup of a value *inside* a container element
(`BP_LifeSaveData_C.SaveSlotList[1].GP`) produces a **container match**, not a
direct instance → `HasFields=false` → the "🌍 Locate in GWorld" toolbar button
was hidden and `SelectedInstance` was null, so the only action was the container
row's pre-existing plain "Open". Logs confirmed `find_path_from_gworld` was never
called. Fix: the Instance Finder **container-match row** now has its own "🌍"
button (`LocateContainerOwnerInGWorld` → `LocateContainerInGWorld` event → reach
mode) that locates the OWNING object via the GWorld path, then auto-drills into
the matched element `[N]` (safe — `TryDrillIntoMatchedContainer` only drills
`IsContainerNavigable` fields), landing the user on the element ready to scroll to
the value.

**Land ON the nested value (build 1193).** First container-match test correctly
produced `GWorld → … → BP_LifeSaveData_C → SaveSlotList → [1]` (BFS verified!) but
stopped on the array element `[1]` — the actual value `GP` is a field *inside* the
struct element at `[1]+0x4D8`. (Not a depth issue — `GWorldLocateDepth` is the BFS
hop count to the owning object, unrelated; 5 vs 8 gave the same correct result.)
`LocateInGWorldAsync` now takes an `elementIntraOffset`: for a `StructProperty`
container element the Instance Finder passes the match's `IntraOffset`, and the
reach path does explicit awaited drills — walk owner → `NavigateToContainerAsync`
(array view) → `NavigateToFieldAsync` (the `[N]` struct element, which carries
`StructDataAddr`/`StructClassAddr`) → scroll to the field at the intra-offset — so
the breadcrumb spine ends `… → SaveSlotList → [1]` and the DataGrid lands ON `GP`.
The single-shot `_pendingScroll*` path can't chain two container levels, hence the
explicit sequence. Value Search / SPC reach paths still land on the owning field
for struct-array-inner hits (same extension, deferred — todo).

-----

## Older entries (builds ≤1177) → archive

Milestones for builds 939–1177 (2026-06-06 → 2026-06-15) are in
[archive/dev-log-2026-06-pre-build-1180.md](archive/dev-log-2026-06-pre-build-1180.md);
builds 715–937 in
[archive/dev-log-2026-06-pre-build-940.md](archive/dev-log-2026-06-pre-build-940.md);
builds ≤696 in
[archive/dev-log-2026-05-pre-build-700.md](archive/dev-log-2026-05-pre-build-700.md).
— grep `^## ` there for the older index.
