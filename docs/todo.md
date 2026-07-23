# Todo

Open work only. **Read this when deciding what to do next.**

> **2026-06-06 cleanup.** This file was slimmed to open items only. The full
> pre-cleanup history (every shipped build's effort/risk retrospective, files
> touched, test counts, decision rationale) is frozen in
> [archive/todo-completed-build-937.md](archive/todo-completed-build-937.md).
> The running milestone log is [dev-log.md](dev-log.md).
>
> **Conventions:** each item is **flat and self-describing** — the title decodes
> its session shorthand (e.g. "V3-C", "#5 v2"), and a trailing *parent* line gives
> the one-line context (which already-shipped work it follows + the dev-log build).
> **Effort** S/M/L/XL (S=hours · M=1 session · L=multi-session · XL=weeks).
> **Risk** low/med/high (chance of breaking existing behaviour / perf regression).
> When an item ships: write it up in [dev-log.md](dev-log.md), update
> [roadmap.md](roadmap.md) if capability changed, then **delete it here** (don't
> strike-through — the archive holds the history).

-----

## 🔎 Audit #3 fixes (build 2168 — 2026-07-14; full detail in [audit-2026-07-14-findings.md](audit-2026-07-14-findings.md))

Third bug/leak audit of the post-b1872 code (Solide/Hemmung/Linie/Schlacht/Grausam + Auto-Snapshot/
Dump-Explorer/Live-Funcs/Teleport-Stealth). 32 findings raised, verified against current code, then
put through an **adversarial double-confirm** (skeptics mandated to refute; HIGH got 3 diverse lenses).
**Net after double-confirm: 23 scheduled** (1 HIGH + 9 MED + 13 LOW) — **M6 and L6 refuted/dropped**,
7 LOWs downgraded to optional cleanup. 0 regressions from audit #2. **Common root cause of 8 of the 10
HIGH/MED: disconnect/shutdown lifecycle** — a *bare* `OperationCanceledException` from `PipeClient`
(DisconnectAsync/Dispose `TrySetCanceled()` with **no token**, so only ambient `ct.IsCancellationRequested`
distinguishes it from a real cancel), or a DLL worker whose state isn't reset/restored when the last client
leaves. **Prefer fixing the shared root** (an `IsUserCancel(ct)` helper + a single `OnLastClientGone()`
reset registry) over each site individually. Each ID below maps to a section in the findings doc; delete
the row here when it ships.

- **✅ DONE — H1 — Snapshot silently truncated but saved as usable/Success on user Disconnect** —
  SHIPPED commit `452d3ff` (build 2182). Producer catch now filters on `lct.IsCancellationRequested`, so a
  bare disconnect-OCE faults the producer → `Task.WhenAll` rethrows → `CompleteSnapshotAsync` is skipped →
  the existing outer OCE catch deletes the partial. **Verified `is_usable` defaults to 1** (so an
  un-finalised row is *usable* — the fix relies on deletion, not on it being auto-cleaned); the outer catch
  was deliberately **not** filtered (that would reroute to the non-deleting generic handler and re-leave a
  usable partial — M7's separate concern). Regression test
  `Capture_DisconnectMidStream_DoesNotSaveUsablePartial`; 2526 green. *Delete this row after the audit batch
  is merged to main.* *Parent: audit-2026-07-14-findings §H1.*

- **[✅ ALL MEDIUMs DONE — 1 HIGH + 10 MED shipped on `dev`; DLL M1–M5 await in-game verify]** — the entire
  audit-#3 HIGH+MEDIUM set is fixed. Remaining audit work = the **13 LOW** batch (below) + optional/cosmetic
  items. Done-notes for the DLL cluster:
  > **✅ DONE — M5 + M2 enable-recovery leak** (SHIPPED commit `61e1f7f`, build 2189, **needs in-game verify**).
  > `Tot::RequestShutdown()` at the TOP of `UE5_Shutdown` + every module's `StartWorker*` gated on
  > `Tot::ShutdownRequested()` (single spawn chokepoint) → no worker revives in the shutdown window; cleared
  > by `Fern::Start`. **Adversarially verified** (5 lenses: no deadlock / lock-order / M3↔M5 / M4↔M5
  > regression; `EnqueueInvoke` gates on Stark's hook flag not `g_shutdown` so the M3 un-hide survives). The
  > same pass caught + fixed a leak in the M1/M2 enable-recovery (un-responsive re-enable orphaned the
  > leftover). *Delete after in-game verify.*
  > **✅ DONE — M1/M2/M3** (SHIPPED commit `0f6f6e0`, build 2188, **needs in-game verify**). All Schlacht:
  > disable now joins the worker *before* snapshot/restore (M1); an unresponsive game thread keeps the hidden
  > record + recovers it on the next enable instead of discarding it (M2); `SetEnabled(false)` is called from
  > Fern last-client cleanup + `UE5_Shutdown` with a cheap no-op early-out (M3). *Delete after in-game verify.*
  > **✅ DONE — M4** (SHIPPED commit `7edea28`, build 2187, **needs in-game verify**). `Tot::MarkBackgroundWorker()`
  > thread-local marks each re-assert worker (Solide/Hemmung/Laufen/Solitar/Dunste/Schlacht) so `Tot::Requested()`
  > returns `g_shutdown`-only on those threads → workers no longer freeze on the per-command cancel latch while
  > a pipe command still honours it. Did NOT reset `g_perCommand` on disconnect (would regress the
  > orphaned-scan abort). *Delete after batch merged + in-game verified.*
  > **~~M6~~ dropped by double-confirm** — "Solide hold unstoppable after UI crash" is working-as-designed:
  > hold persistence across disconnect is deliberate and family-uniform (Solitar/Laufen/Hemmung/Wirbel also
  > persist), and an off-switch exists (reconnect → `reset_all_fields`, or game restart). The real disconnect
  > defect here is **M4**, which stays scheduled.

- **[✅ ALL UI MEDIUMs DONE — M7/M8/M9/M10]** — the four UI-side audit-#3 MEDIUMs are shipped on `dev`
  (H1 too). Remaining MEDIUMs are the **M1–M5 DLL disconnect/shutdown cluster** (above). Done-notes:
  > **✅ DONE — M10** (SHIPPED commit `8108ff2`, build 2186). PropertySearch ResultFilter now uses
  > `ObjectTreeFilter.SplitTerms` + `MatchesAllTerms(terms, Class, Prop, Type, Super, Preview)` (space=AND,
  > field-OR) + `KeywordSearchMemory` (field + `ResultFilterHistory` + probe + `Schedule` + `Dispose`); axaml
  > `TextBox`→`AutoCompleteBox`. Tests `PropertySearchFilterTests`; made `StubDumpService.SearchPropertiesAsync`
  > virtual. *Delete after batch merged to main.*
  > **✅ DONE — M9** (SHIPPED commit `1f46994`, build 2185). Gate-off teardown releases an active Solide
  > stealth hold (`if (StealthState == StealthHoldingState) ResetStealthCommand`), matching the
  > Foreground/Fly/SeeThrough force-off pattern; `"Holding @0"` factored into a shared const. Tests
  > `ExperimentalGateOff_releases_active_stealth_hold` (+ no-op case). *Delete after batch merged to main.*
  > **✅ DONE — M8** (SHIPPED commit `ad9a7e7`, build 2184). `_sessionEpoch` bumped on disconnect; the dwell
  > records only via the pure gate `ShouldConfirmProxy(IsConnected, scheduledEpoch, currentEpoch)`; timer
  > disposed before early returns, on disconnect, and in `Dispose()`. Tests `MainWindowProxyConfirmTests`.
  > *Delete after the audit batch is merged to main.*
  > **✅ DONE — M7** (SHIPPED commit `1b108a9`, build 2183). Disconnect now reports `Failed` (not
  > `Cancelled`), so the auto-loop stops via `case Failed` instead of wedging; `case Cancelled` also stops
  > defensively; the partial delete+reclaim (`RemovePartialAsync`) now also runs on the generic
  > `catch (Exception)`, closing the non-OCE (IOException/InvalidOperationException) H1 sibling hole.
  > Regression test `AutoSnapshot_DisconnectMidCapture_StopsLoopWithoutWedge`; 2527 green. *Delete after the
  > audit batch is merged to main.*

- **[✅ ALL 13 LOWs DONE] Audit #3 low-severity batch** — SHIPPED on `dev` in four commits:
  UI L13–L17 (`8bd33f8`, +2 tests), Solide L2/L3/L4 (`408fd2d`), DLL L1/L5/L8/L10/L12 (`7f3898f`), and the
  adversarial-verify followups (`3362636`: L4 prune-guard for >256-instance churn + L10 GFW-hook re-subclass
  race). The DLL LOWs (L1–L12) **await in-game verify**. With the HIGH + all 10 MED, the entire scheduled
  audit-#3 set is now fixed — only the optional/cosmetic downgrades below remain. *Delete after batch merged +
  DLL in-game verified. Parent: audit-2026-07-14-findings §L1–L17.*

- **[optional / cosmetic] Audit #3 downgraded items (do only if touching the file)** —
  Effort: **S** each · Risk: low. Double-confirm judged these real-but-not-worth-a-dedicated-fix (no
  incorrect/harmful outcome): **~~L6~~** DROPPED (Welford `m2` provably ≥ 0 → NaN unreachable + tolerant
  downstream); **L7** Linie post-Reset phantom row (wiped by next `StartRecording`); **L9** Grausam per-frame
  global `ClipCursor(nullptr)` (deliberate release tradeoff; niche third-party case); **L11** Schlacht raw
  `AActor*` across GC (SEH-guarded, self-corrects next tick, no crash); **L18** DetectStats missing detach has
  zero functional effect (no `OnSelectedResultChanged`) — only the no-`ct` usability point stands; **L19**
  LiveFuncs `Clear()` inverted detach order (cosmetic); **L20** Dump All export no `CancellationToken`
  (usability gap, output correct); **L21** (INFO) DumpExplorer reimplements space=AND (semantically
  identical — style only). *Parent: audit-2026-07-14-findings §L6–L21 (see per-item ⬇/⛔ banners).*

-----

## ▶ Next up (genuinely actionable now)

- **Multi-pipe Phase 1 — residual verification (low priority; lane split SHIPPED PR #396)** —
  Effort: **S** · Risk: low. The two-connection lane split shipped + in-game verified for §9.6 items
  1–5 (dev-log 2026-06-28). Two checklist items weren't explicitly exercised: (6) **watch-event
  delivery** to the interactive lane (System-tab / address watch still pushes correctly), and the
  **single-lane-independent-drop edge** (§9.7 — one lane errors while the other lives; the UI router
  should tear down both for a clean reconnect, and a stray interactive disconnect shouldn't cancel a
  running bulk scan). Verify opportunistically. *Parent: multipipe-eval §9 (PR #396).*
  The build-1836 single-handle worker-pool was REVERTED (deadlocked on the synchronous pipe, §8.1).
  The sister repo `D:\Github\discrete` runs a proven alternative: the UI opens **two** client
  connections (interactive + bulk) to a `maxInstances≥2` server that serves **each connection on
  its own thread + own handle**. Each connection stays serial read→write on one handle (one thread)
  → **no same-handle deadlock, and NO overlapped-I/O rewrite needed** (each handle is touched by
  exactly one thread). Safe because the interactive lane never builds the Aura class caches and the
  bulk lane runs scans one-at-a-time (§9.1). DLL work = per-connection refactor of Fern (thread-per-
  connection accept, per-connection write-mutex / in-flight / watch-event routing, monitor + session
  cleanup keyed per-connection — §9.3); UI work = a 2nd `PipeClient` + a `BulkCommands` router like
  discrete's `BackendAdapter` (§9.4). Lane table = §6. **MUST pass the §9.6 in-game checklist before
  shipping.** Open decisions in §9.7 (maxInstances count; session-drop policy; cancellation scope).
  Snapshot SPEED is a SEPARATE issue (§9.5): UI-side single-threaded multi-MB chunk parse (~2.4s/chunk)
  → streaming `Utf8JsonReader`/smaller chunks. *Parent: reverted Phase 1 build 1836 (dev-log 2026-06-28).*

- **Magic-number centralization — Tier 2 remainder + Tier 3 (deferred; low priority)** —
  Effort: **M** · Risk: med. Tier 1 (dup/tunable literals) + the Tier 2 `IsUserspacePointer` paired-
  bounds helper SHIPPED (dev-log 2026-07-03). LEFT because each carries genuine per-site multi-meaning
  nuance: object-count/size ceilings — `0x800000` (8M UObject count), `0x100000` (1M, but needs
  SPLITTING into container-element-COUNT vs PropertiesSize-BYTES — one const would conflate them),
  `[0x1000..0x400000]` NumElements window; and `MAX_CLASS_HIERARCHY_DEPTH=64` (`64` is high-collision —
  buffers / bit-counts — needs per-site verification). Tier 3 = single-use knobs (Heiter startup
  delays, Fern watch/monitor poll, Mimic caps, Solitar caps, Wirbel eps/channel, movement/debug-cam
  protocol-id enums). Same careful methodology: pair/meaning-gated, never blind-sweep a literal.
  *Parent: magic-number centralization (dev-log 2026-07-03).*

- **CE responsiveness under heavy scans — pick a NON-priority approach if it ever bites** —
  Effort: **S-M** · Risk: med. Phase 0 (drop scan threads to `BELOW_NORMAL`) was **REVERTED** —
  with the game saturating cores it starved scans ~20× (Snapshot 1–2 min → ~1 h). The
  pre-existing `cores − 2` count cap (`ScanThreadCount`, Aura.cpp:105) is the right throttle and
  is restored. CE `.CT` invoke is already off-pipe (Mimic mailbox); if a real starvation case
  appears, prefer: yield points inside the scan loop, a smaller worker cap while a CE invoke is
  pending, or `Stark`-queue priority for mailbox invokes — **not** a blanket thread-priority drop.
  *Parent: reverted Phase 0 build 1834 (dev-log 2026-06-28).*

- **Class Pivot discovery (build 1742) — deeper capture-memory fix** —
  Effort: **M** (memory) · Risk: low. The bounded N-snapshot discovery + shape ranking +
  Locate-in-GWorld/GameEngine + resizable/filterable results shipped build 1742; the class
  picker freeze was fixed + in-game verified build 1764 (filter TextBox + ListBox — see
  dev-log). The **change-driven discovery ("Suggest Targets") path itself is still NOT in-game
  verified** end-to-end. Separately: the post-capture compacting `GC.Collect` (build 1742) is a
  **mitigation** for the multi-snapshot working-set bloat — the *deeper* fix is to stop the
  transient allocation at the source by replacing the capture's JSON-DOM parse with a streaming
  `Utf8JsonReader` (`SnapshotChunkAsync` / the chunk parse). Only pursue the streaming rewrite if
  the GC reclaim proves insufficient on huge (Avowed-scale, 266K-object) games. *Parent: dev-log build 1742.*

- **Class Pivot — rounding-mode + "can't-find-data"/GAS-capture follow-ups (deferred from build 1672)** —
  Effort: **S-M** · Risk: low. The per-panel **RoundingMode {Round/Trunc/Ceil}** (build 1672) was rolled out to Value Search, Snapshot, and SPC but **NOT Class Pivot**. Two distinct gaps:
  (1) **Rounding mode** — Pivot does **no numeric value MATCHING** today: it groups by the *rendered* key string (`PivotEngine` uses `SnapshotNumeric.Render`) and `PivotDiscoveryEngine.Direction()` compares raw `double`s with no reduce. So a rounding-mode switch is largely **N/A** — but if Pivot ever grows a value-target filter, it should reuse `SnapshotNumeric.ExactMatch/OrderedMatch/BetweenMatch(...,FloatRoundMode)` like the other panels. Lower priority: optionally apply the reduce to the grouping KEY so float GAS values bucket by displayed integer (e.g. 513.36/513.4 group as "513").
  (2) **"Can't find data" / GAS-capture** — the recent snapshot fixes (nested-`StructProperty` GAS capture `Aura::CaptureDirectStructFields`, build 1648; rounded-float matching) flowed into Snapshot/SPC/Group. Pivot reads the **same captured corpus**, so the GAS `Health.BaseValue`-style fields *should* now appear in Pivot automatically — **but this is UNVERIFIED**. Verify in-game that a GAS attribute captured post-1648 actually shows up as a pivotable field/key in Class Pivot; if Pivot has its own field-selection or numeric-only filter that drops nested-struct leaves, fix it. *Parent: rounding-mode switch build 1672; snapshot GAS-capture build 1648 (project-snapshot-nested-struct-gas).*

- **Flatten GAS attributes — optional extensions (deferred by user, build 1698)** —
  Effort: **S** · Risk: low. The "Flatten GAS attributes" Options toggle (build 1698) collapses a
  `GameplayAttributeData` StructProperty one level in **Copy CE XML / Copy CE Field** only. Two
  follow-ups the user explicitly scoped out of that change:
  (1) **Export CSX** — apply the same flatten to the CE Structure Dissect (`.csx`) export
  (`CsxExportService.EmitElement`). The `IsGasAttributeStruct` detection + combined-offset math port
  directly, but CSX is a separate emitter so it was intentionally left out.
  (2) **Other single-field / wrapper structs** — keep flatten GAS-only "for now"; a general
  "flatten any single-/two-scalar-field struct one level" option would need a careful detection rule
  to avoid surprising collapses ("various cases"). *Parent: Flatten GAS attributes build 1698
  (project-gas-attr-flatten-ce-export).*

- **dxgi proxy early-load fragility — harden (thin-shim + renamed real-dxgi copy), or leave dxgi as "late-load games only"** —
  Effort: **M-L** · Risk: med (loader-time code + deploy flow). **Deferred by owner (2026-06-19); Octopath uses version.dll for now, and the UI default is back to version.dll.** The dxgi proxy instant-exits on games that call dxgi **extremely early — under the loader lock, before our CRT is initialised** (Octopath Traveler: debugger-confirmed across 3 distinct crash dumps — execute-0 / `__tzset` uninit CRT lock / `RtlAllocateHeap` null heap; see dev-log 2026-06-19). Two genuine early-load fixes shipped + kept (`Sein::GetTimestamp`→Win32 `GetLocalTime`; dxgi lazy self-resolving thunks), but they do NOT make Octopath's dxgi work — the **root blocker** is that `LoadLibraryW(real same-named System32\dxgi.dll)` returns NULL under the early loader lock. **version.dll dodges it all by being called at normal runtime, not under early loader lock.** Robust fix = **thin-shim split (like RE-UE4SS):** `dxgi.dll` becomes a tiny CRT-free forwarder that (a) loads the real dxgi via a **renamed copy** (`dxgi_orig.dll`) to dodge the same-base-name-under-lock failure, and (b) `LoadLibrary("UE5Dumper.dll")` to run the heavy dumper as a **separate, normally-named, late-loaded** DLL. Deploy becomes **2 files** (`dxgi.dll` + `UE5Dumper.dll`) → the Proxy Deploy panel's deploy/undeploy/redundancy/Update-All must copy/remove both. NOTE: `/MD` (dynamic VCRuntime/UCRT) alone is only a **partial** fix — it removes the CRT-init crashes (Octopath already loads the shared UCRT early) but NOT the loader-lock same-name `LoadLibrary` blocker (that resurfaces as execute-0). version.dll/dinput8.dll don't need any of this (they load late). *Parent: dxgi proxy build 1172; early-load diagnosis + 2 fixes build 1351 (dev-log 2026-06-19).*

- **UE5.7+ packed FUObjectItem — live-verify + calibrate when a packed game appears** —
  Effort: **S** (mostly verify) · Risk: low (gated, last-resort only). Packed parsing shipped
  build 1108 but is **UNVERIFIED** (no `UE_ENABLE_FUOBJECT_ITEM_PACKING` game exists yet). When
  one does: attach, watch for the `*** UNVERIFIED ... PACKED ... ACTIVATED ***` WARN (or force via
  `set_packed_consts {force:true}`), then tune `align_bits`/`ptr_mask_bits`/`serial_off` against the
  echoed `GObjects[0..7]` samples until names resolve; confirm the object walk + a CE XML/CSX export
  are correct; then promote out of UNVERIFIED (drop the badge gate, pin the constants).
  Open sub-question: the packed **SerialNumber** offset (currently best-effort `0x0C`) is unpinned.
  *Parent: PackedItem.h + Aura packed mode + set_packed_consts shipped build 1108 (dev-log 2026-06-14).*

- **DLL cancellation — live-game verification** — Effort: **0** (verify only) · Risk:
  low. Confirm in-game that (a) disabling the script / closing the game while a long
  scan runs no longer hangs, (b) closing the UI mid-scan stops the DLL and a reopened
  UI reconnects promptly.
  *Parent: cooperative cancel + shutdown-abort + disconnect monitor shipped build
  936-937, PR #238 (dev-log 2026-06-06).*

- **Guess? "missing" mid-object data — RESOLVED (working as designed; diagnostic kept).** The
  `WALK:guess` diagnostic (build 1364+, `Ubel.cpp` `WalkInstance` fillGaps block, one line per
  Guess? walk, opt-in-gated) confirmed it **live on Elliot `LSGameWork`**: `0x170=16(ArrayProperty)`
  covers `0x170–0x180` and `0x180=80(MapProperty)` covers `0x180–0x1D0` exactly — the region the user
  saw "missing" is the inline allocator bytes of a TArray + TMap, fully owned by reflected container
  properties. The `GAPS:` list has nothing in `0x170–0x1D0` (only small padding/bitfield holes like
  `[0x3,0x8)`, `[0xA04,0xA3C)`). So `Guess?` correctly emits no raw rows there — CE dissect just
  flattens the container internals; our walker shows them as expandable Array/Map rows. **No code
  change.** The diagnostic line is kept as the standing answer to future "why doesn't Guess? show
  region X" questions (gated on `Guess?` being on). Optional future nicety: a `docs/tips.md` note that
  container internals aren't decomposed into guessed rows. *Parent: Guess-What leading-gap fix (builds
  1330-1333) + diagnostic (build 1364, this session); confirmed live 2026-06-19.*

- **Native-C Value Scan — P0–P3 ALL SHIPPED on dev; only in-game verify of P3 remains** —
  Effort: **0** (verify only) · Risk: low. Full design + status in
  [native-c-value-scan-spec.md](native-c-value-scan-spec.md). Opt-in raw/unmanaged
  (non-`UPROPERTY`) scan for native HP/MP via "Guess What" (`Ubel::GuessGapTypes`), across
  Value Search + Group Scan + Snapshot→SPC→Pivot. **DONE + in-game VERIFIED:** P1 single
  (Octopath), P2 group (FF7 Rebirth). **P3 DONE (build+tests+AOT green):**
  `CaptureSnapshotChunk(captureNativeC)` → `AppendRawHoleFields` (GuessGapTypes →
  `NormalizeGuessedTypeToProperty` → drop Pointer/Padding → `<raw@0xNN>` fields,
  numericScope-filtered, ≤256/obj); pipe `native_c` on `snapshot_chunk`; C#
  `SnapshotViewModel.IncludeNativeFields` toggle + intro string. SPC Query + Class Pivot
  consume raw rows with ZERO code changes (key on prop_name=offset + canonical declared_type;
  existing `fields` schema, no migration). **REMAINING: in-game verify P3** — BLOCKED on the
  snapshot-perf item below (FF7 Rebirth capture with Native-C didn't finish — 16+ min, >50%
  uncaptured). Verify on a smaller / faster game, or after the perf work: capture a native
  snapshot pair around a stat change, confirm SPC diff tracks a `<raw@0x..>` value + Class
  Pivot decodes it (not hex).
  *Parent: P0–P3 shipped on dev (this session); builds on value_search_caveats, the `Orden`
  seam (group-value-scan-spec §3.1), and the "Guess What" build (commit 75ea723).*

- **Snapshot capture too slow on huge games (FF7 Rebirth ~433K objects) — esp. with Native-C** —
  Effort: **M-L** · Risk: med (touches the hot capture path). FF7 Rebirth snapshot with
  Native-C ON ran **16+ min and left >50% of objects uncaptured**, so P3 couldn't be verified
  there. Likely causes, in order: (1) **Native-C `AppendRawHoleFields` calls `Ubel::GuessGapTypes`,
  which reads memory BYTE-BY-BYTE** (the zero-run probe does one `Macht::ReadSafe<uint8_t>` per
  byte) — over every hole of every object that's very slow. **FIX SHIPPED (this session):** `Ubel::GuessGapTypes` now reads the whole gap
  ONCE into a reused `thread_local` buffer and guesses in-buffer — the per-position AND
  per-byte (zero-run probe) SEH reads are eliminated; output is byte-identical; an SEH
  fallback is kept for a faulting / over-large gap. Also speeds up LiveWalker "Guess What".
  (still 10+ min after this fix, so the round-trip overhead below dominates too.) (2)
  ~~one pipe round-trip per 200 objects~~ **CHUNK RAISED 200 → 1000 (this session, 待測 /
  in-game re-test pending):** `Constants.SnapshotChunkSize` — ~2166 chunks → ~433 for 433K
  objects, also cutting the per-chunk SQLite write-transaction count. Safe (byte-mode pipe +
  `StreamReader.ReadLineAsync` accumulate any size; DLL 15s per-chunk deadline re-chunks slow
  chunks). **NEEDS in-game re-test on FF7 Rebirth — if still too slow, the bottleneck is the
  single-threaded DLL walk → do (3).** Could raise further / batch SQLite inserts if needed.
  (3) ~~DLL `CaptureSnapshotChunk` is single-threaded~~ **PARALLELIZED (this session, 待測 /
  in-game re-test pending):** the per-object capture loop now runs across worker threads via
  `ParallelGObjectsScan` (each worker fills its own `SnapshotObject` vector, merged after; whole
  chunk processed → `scanned` stays contiguous for the pager; Tot-cancel only, no wall-clock
  early return). Chunk size raised to **8192** so each full chunk clears `ScanThreadCount`'s
  >=8192 worker-thread threshold + amortizes the per-chunk cancel watcher. Verified by an
  adversarial 3-lens race/correctness audit (capture-helper statics, GuessGapTypes/WalkClassEx
  copy-out, pager contiguity) — no crash/corruption/mis-paging; the worker stride-check was
  fixed to range-relative for prompt cancel. **NEEDS in-game re-test on FF7 Rebirth** (combined
  with the GuessGapTypes + chunk wins, this is the big one). (4) **Source-level noise skip
  SHIPPED (builds 1484-1486, dev, in-game verify pending):** the "Auto detect Engine/System
  noise" option (default ON) `continue`s past pure engine/system classes (UI widgets, textures,
  sounds, Niagara, anim instances, `/Script` engine packages) in the capture loop BEFORE the
  per-field walk — cutting the dominant per-object cost for the many noise objects a big game
  carries + shrinking the DB, with a gameplay guardrail that force-keeps Actor/Pawn/Character/
  component-derived classes. Complements (1)-(3); especially helps games heavy in engine UI/FX
  objects. Still open if needed: class-scoped capture (only a chosen class's instances) + a
  clearer "X% captured" progress.
  *Parent: Native-C P3 in-game test (FF7 Rebirth), this session.*

- **DynOff calibrated offsets are non-atomic — tighten the second writer (low-risk hardening)** —
  Effort: **S** · Risk: low. The race audit of the parallel snapshot flagged a PRE-EXISTING
  technical data race the parallel readers widen: `DynOff::FSTRUCTPROP_STRUCT` (and the sibling
  calibrated `DynOff::` ints) are non-atomic. `Ubel::CorrectSubclassOffsets` serializes its writes
  (`s_calibrationMutex` + acquire/release `s_checked`), but there's a SECOND unguarded writer at
  `Ubel.cpp:~4288` (`DynOff::FSTRUCTPROP_STRUCT = tryOffset;` inside `WalkInstance`), and the
  snapshot/WalkClassEx-enrichment readers aren't gated by `s_checked`. Benign in practice
  (idempotent convergent writes + aligned-int load/store atomicity on x64), so NOT a crash risk,
  but technically UB. Lowest-risk fix: drop the redundant `~4288` write (verify `CorrectSubclassOffsets`
  already covers that calibration first — don't regress StructProperty struct-name resolution), or
  make the calibrated offsets `std::atomic<int>` with relaxed loads. *Parent: parallel-snapshot race audit, this session.*

-----

## Bookmarks + Options persistence + CE-export filter — follow-ups (shipped PR #359, builds 1652-1663)

The three persistence features (CE-export system-component filter, global panel-options persistence, per-game bookmark persistence) shipped + in-game verified (dev-log 2026-06-24). Deferred refinements, none blocking:

- **#3 — snapshot capture options: GLOBAL → per-game (peHash)** — Effort: **M** · Risk: med.
  The snapshot capture block (`GameOnly` / `AutoSkipNoise` / `IncludeNativeFields` / `SelectedScope` / `SelectedFamily` / `SelectedMaxDataset`) is persisted as a single GLOBAL default in `ui-options.json` to avoid a connect-time load race. Making it per-game (a `snapshots.{peHash}.options.json` sibling of the denylist, or a section in the bookmark/per-game store) lets `SelectedMaxDataset` track each game's size (the Avowed-6.7 GB driver). **Load it inside `SnapshotViewModel.SetEngineState` AFTER `_store.SetActiveGame(peHash)` and BEFORE `RefreshAsync`, under its own suppression flag** — NOT in `ApplyEngineState` (the original adversarial-review C2 finding: wrong-game bleed + save-storm if loaded at the wrong point). *Parent: #3 Options persistence, PR #359 (dev-log 2026-06-24).*

- **#3 — opt-in "resume where I left off" (view-state persistence)** — Effort: **S** · Risk: low.
  `SelectedTabIndex` + panel-collapse toggles (`CaptureSectionOpen` / `CompareSectionOpen` / `NoisePanelOpen` / `IsFunctionsExpanded` / Object Tree `IsCollapsed`) were deliberately EXCLUDED as transient view state. Some users want them restored. Add as an OPT-IN (a "remember tab + panels" preference) so the default stays clean. Lives in the existing `UiOptionsStore` (a `View` sub-object). *Parent: #3 Options persistence, PR #359.*

- **#3 — "Reset options to defaults" button** — Effort: **S** · Risk: low.
  Delete `ui-options.json` + re-apply model defaults to every VM (reuse `ApplyOptions(new UiOptionsSettings())`). One en.axaml string + a menu item (System tab or a small ⚙ on the toolbar). *Parent: #3 Options persistence, PR #359.*

- **#2 — CE-export filter: also skip system-component CONTAINER elements** — Effort: **S** · Risk: low.
  The "Skip system components" filter currently only covers pointer/struct fields (`PtrClassName`); array/map/set ELEMENTS whose element class is an engine asset (`KeyPtrClassName` / `ValuePtrClassName`, `LiveFieldValue.cs:60/66`) slip through because the container emitters don't route through the `EmitFields` depth gate. Add the per-element check in `EmitMapProperty` / `EmitSetProperty` / `EmitArrayProperty` (depth>1 only). The tooltip already states elements are not filtered, so this is additive, not a bug. *Parent: #2 CE-export noise filter, PR #359.*

- **~~#1 — bookmarks across a game RESTART (deeper-than-GWorld-root paths)~~ — DONE (build 1690, MERGED main PR #365 `2e54d86`, in-game VERIFIED).**
  Bookmarks now SPINE-re-walk on every load: `LiveWalkerViewModel.TryReresolveBookmarkSpineAsync` re-resolves the saved breadcrumb chain (stable field name+offset+deref/container kind) from a live anchor — GWorld (`WalkWorldAsync`) or GameEngine (`ResolveGameEngineAsync`) — rebuilding each crumb with a fresh address (`BreadcrumbItem` is init-only → `CloneCrumbWithAddress`). Container element hops (`[N]`) re-resolve via the same element-address math as the `Populate*ContainerFields` helpers. Any unmatched hop / null ptr / DataTable view / non-anchor root → return null → fall back to saved addresses (same-process fast path) + the existing `SavedClassName` staleness guard. **v1 safety property kept: never silently shows the wrong object** (name+offset match + class guard). `BuildBreadcrumbSpineFromPath` now threads `rootKind` so Locate-in-GameEngine bookmarks persist the right `"GameEngine"` anchor marker (adversarial-review finding). Remaining game-PATCH case (offsets shifted across builds): the **"import bookmarks from a previous build"** affordance (pick an older `bookmarks.{oldHash}.json`, re-resolve against the new build) is still open if users ask. *Parent: #1 bookmark persistence, PR #359.*

- **#1 — orphaned per-game bookmark files accumulate** — Effort: **S** · Risk: low.
  Every game patch = new PE hash = a fresh `bookmarks.{hash}.json`; old ones are never swept (same latent issue the snapshot per-game files have, but those have quota eviction). Add a startup sweep (delete `bookmarks.*.json` older than N days, or cap file count) — or a "clear bookmarks for all games" action. Low disk impact; do only if it bothers someone. *Parent: #1 bookmark persistence, PR #359.*

-----

## Related Objects panel — Phase 2 + follow-ups (Phase 1 shipped builds 1323-1327)

Phase 1 (the "Related" tab: given an actor, list Self/Class/Outer + Controller↔Pawn + owned components/ASC/AttributeSet via a depth-3 owned walk; 🌍 GWorld / Live Walker / finder / copy per row; 🔗 Related handoff from Instance Finder / Value Search / Live Walker) + the Instance Finder **"Newest first"** opt-in shipped builds 1323-1326. **In-game VERIFIED on TQ2:** `bp_ai_default_character_C` → Related lists 58 objects incl. `TQ2AIController`, `GrimAbilitySystemComponent` (ASC), `bp_tq2_character_stats_component_C` (AttributesComponent) → `AttributeSetHealth.CurrentHealth` = live HP (73.57). **Phase 2 (`Edel` current-target auto-detect) SHIPPED build 1400** (dev-log 2026-06-20) — `🎯 Detect target` button resolves GWorld→PC→Pawn, scores the player's outgoing object-ptr fields (structural is-Actor gate + keyword boost), auto-loads the top candidate; the `Edel` roster name is now 🟢. Remaining follow-ups, in order:

- **Phase 2 Edel — in-game verification + tuning** — Effort: **0** (verify only) · Risk: low. Built + unit-tested + AOT-green but unproven live. Verify on a **lock-on / soft-target action title** (the target lives in a `UPROPERTY` object field): click 🎯 Detect target → confirm the top candidate IS the focused enemy and its AttributeSet/HP shows in the grid; confirm the 🌍 Locate-in-GWorld now resolves for that target (it should — the player references it). On the named JP/CN test games (TQ2/SEED/DQ7R, mostly no target `UPROPERTY`) confirm the **graceful fallback** fires (note = "no clear target / weak guesses", nothing auto-loaded) rather than feeding a wrong actor. Tune the score constants / keyword tables only if a real game motivates it. *Parent: Edel shipped build 1400, dev-log 2026-06-20.*

- **Locate in GWorld — streaming / World-Partition actors — ADDRESSED via the world's level list (build 1405); in-game verify pending** — Effort: **0** (verify only) · Risk: low. `Aura::RecoverViaWorldLevel` now recovers a `not_reachable` actor through its owning `ULevel` (reached by the `ULevel::OwningWorld` back-reference, since an actor's Outer IS its level), emitting `world →(WorldLevel)→ ULevel → Actors[k] → actor [→ target]` with status `ok_via_level`. This makes ANY actor that belongs to the current world locatable + navigable in Live Walker (and a bounded tail BFS reaches an owned AttributeSet/HP), regardless of how its level was streamed in — closing the Elliot "weapons map, enemies don't" case. **Verify in-game:** on a streaming/WP title, 🌍 on a just-spawned enemy now lands (status note: "via the world's level list"); confirm the breadcrumb spine reaches the enemy and you can drill to its HP. Two honest residual limits (acceptable, not bugs): (1) the chain is NOT a clean CE static-pointer chain (the world→level hop is a back-reference) — it's for in-tool navigation; (2) a truly unreferenced actor not in ANY world level still returns `not_reachable` (correct). Edel (build 1400) remains the complementary path when the player references the target. *Parent: Related Objects Phase 1 in-game test (dev-log 2026-06-19); recovery dev-log 2026-06-20.*

- **Locate from GEngine — alternate root for UI-widget / GameInstance-owned objects** — ✅ **DONE (builds 1542-1544, MERGED main PR #345 `f488592`; in-game VERIFIED)** — shipped as **Locate in GameEngine** (⚙ icon on all 10 🌍 surfaces). The existing `find_path_from_gworld` handler gained a `root_kind` field (`engine` → `rootObj = Genau::FindGameEngine().engineAddr`, `no_engine` when absent); `FindObjectGraphPath` was already root-agnostic so it was untouched. Reaches engine-layer objects (GameInstance / LocalPlayer / GameViewport / UMG widgets — the Octopath `PartyCharacterPanel_C` case) that no GWorld chain reaches; a deliberate complement to 🌍 (weaker for world actors, since `RecoverViaWorldLevel`/`ok_via_level` is World-root-gated). See dev-log 2026-06-22. *Residual: `deadline_ms` still hardcoded 20000ms in `FindObjectGraphPath` — optional follow-up.*

-----

## Multiple Values Group Scan — remaining phases (P1 shipped build 1276)

P1 (object-aware group scan, direct numeric leaves + one-level struct descent, exact-per-slot, mode toggle + master-detail UI) shipped builds 1276-1278 — new `Orden` SDR matcher. Follow-ups, in order:

- ~~**P1 in-game verification**~~ **DONE** — verified on SEED (UE4.27): single Value Search + Group Search both pass; Deep mode surfaces the buried `Tunes` block. *(dev-log 2026-06-18.)*

- ~~**P2 — prev-value per slot + offset-table**~~ **DONE (builds 1295-1302)** — per-slot scan type now on `Orden::SlotTarget` (`st`+`tolerance`+`targets2`, routed through `ComparePredicate`) + `RefineGroupCandidates`: First Scan takes Exact/Bigger/Smaller/**Between** per slot (Between = bounded-unknown entry, e.g. HP in [1,100]), Next Scan also takes the prev-value four (Changed/Unchanged/Increased/Decreased — compare each leaf vs its own previous round). Locked-offset table (`🔒 Class — Str@0x20, Def@0x24`) shows once all slots lock. **"Copy CE Script" / export is a deliberate WON'T-DO (not pending work)** — the owner exports the resolved chain from Live Walker (which already does it); do not re-add a group-side CE/export button. **prev-value group refine in-game VERIFIED on SEED** (Unchanged/Unchanged/Increased ran clean); Between first-scan live-verify still nice-to-have. *(dev-log 2026-06-18.)*

- **P3 — numeric containers as blocks — DONE (opt-in Deep, builds 1283-1285; scalar maps builds 1561-1562)**. The "Deep" toggle treats each numeric `TArray/TSet` + each struct-array/map element as its own block via the recursive `WalkContainerLeaves`, matching the group WITHIN one array (finds the SEED `Tunes[N]` case); single-value Deep forces the existing deep pass on all classes. The scalar-map follow-up added **scalar-valued + scalar-keyed maps** (`TMap<Name,int>` → value block `<map>.Value`, key block `<map>.Key`) by extending `ContainerCacheEntry` with `keyLeafType`/`valueLeafType` — closes the walker TODO **and** the Value Search "Proper scalar-map value/key capture" item (one shared fix); struct sides byte-identical, adversarial 4-lens review 0-confirmed. *Remaining (verify only):* in-game verify of the Deep path + a scalar-map (`Map<Name,int>`) game on SEED. *Parent: dev-log 2026-06-18 deep entry + 2026-06-22 scalar-map entry.*

- **Snapshot Group Match follow-ups — feature S1-S5 SHIPPED + MERGED main PR #348, in-game VERIFIED on SEED** (spec: [snapshot-group-match-spec.md](snapshot-group-match-spec.md); dev-log 2026-06-23). Remaining optional work, same `Orden` matcher: ~~**(1)** SPC Query "N-field intersection"~~ **DONE — SPC Group** (Single/Group toggle in the SPC tab; the N-snapshot, per-slot predicate-CHAIN generalisation of Snapshot Group Mode B; builds 1575-1584, in-game VERIFIED on SEED, dev-log 2026-06-23). Still open: **(2)** Class Pivot "co-varying tuples" (spec §3.1 row 3); **(3)** array-AS-BLOCK deep (each nested array its own block, like the live deep — both snapshot reuses shipped object-flat: array elements as the owner's leaves); **(4)** the snapshot-wide >2^53 double-precision limitation (carry exact target bytes per width like the DLL's `NumericTargetSet` — affects SPC/Diff/Pivot too, not just group match). All low priority — pick up if a real case motivates it.

-----

## Locate-in-GWorld — `IsGWorldAvailable` gate decouple (Value Search done, others pending)

- **Decouple the other panels' 🌍 from `IsGWorldAvailable`** — Effort: **S** · Risk: low. Value Search's per-row/per-slot "Locate in GWorld" was gated `IsEnabled="{Binding IsGWorldAvailable}"` (and the command short-circuited on it); on TQ2 (proxy mode) the flag read false even though GWorld was resolved, so the button was silently disabled (no `find_path` sent, no feedback). Fixed for Value Search (build 1311) by decoupling — the button is always clickable and the DLL's `find_path_from_gworld` (which returns `invalid`/`no path` with no live UWorld) is the source of truth. **The same gate still exists on Instance Finder / Snapshot / SPC 🌍 buttons** — apply the same decouple if a user hits it there. Open question worth a quick check: *why* did `IsGWorldAvailable` (an `[ObservableProperty]` fed from `state.HasGWorld`, which was true) evaluate false in the button binding on TQ2 — a binding-resolution quirk in the group RowDetails template vs a real state-propagation timing bug. *(dev-log 2026-06-18.)*

-----

## CE export drilldown — remaining gaps (Phase A/B/C shipped)

Phase A (CE XML/Field container-value expansion, build 1085), Phase B (CSX parity,
build 1098), Phase C (depth-from-current-view tests + CSX truncation note, build
1098) all shipped. Spec: [ce-export-drilldown-spec.md](ce-export-drilldown-spec.md).
Open follow-ups (low priority):

- **CSX struct-array element full re-walk** — Effort: **S** · Risk: low. CSX struct
  arrays still flatten the shallow Phase-F `StructFields` preview, so nested
  structs/maps *inside* an array element stay shallow. CE XML's
  `EmitStructArrayProperty` already re-walks each element via `resolvedStructs`
  (build 1076); mirror that in `ConvertArrayStructElementsToFields` (stamp
  `StructDataAddr` per element + route to `EmitStructPropertyFlattened`). The unified
  resolver already populates `resolvedStructs` for array struct elements — only the
  CSX emit path ignores it.
- **Nested-container truncation note** — Effort: **S** · Risk: low. The
  `⚠ Container element limit` note (CE XML + CSX) only scans top-level fields; a
  container clipped by `ArrayLimit` *inside* a drilled struct/pointer is unreported.
  Cheap: scan `resolvedStructs`/`resolvedInstances` values too. (Marked optional in
  the spec.)
- **FName → live readable string in CE via a "UE FName to String" custom type** —
  Effort: **M-L** · Risk: med (CE-Lua + per-game GNames config). Highest-value of the
  remaining sample.CSX gaps. Today FName is shown statically: **CSX** emits a raw
  8-byte qword (`MapCsxType` `NameProperty`→`8 Bytes` — no name at all); **Copy CE XML /
  Field** emit the 4-byte `ComparisonIndex` + a static `DropDownList` snapshot (index→string
  captured at export time, arrays only; single scalar FNames mostly show the raw index).
  sample.CSX instead uses `Vartype="Custom" Customtype="UE FName to String"` — a CE custom
  type (Lua, registered via `registerCustomTypeLua`) that resolves the FName index against
  GNames **live, inside CE, at runtime**, so any FName value updates to its current string.
  The exporter change is the easy 10% (emit `Custom`/`Customtype` for `NameProperty`, opt-in,
  keep DropDownList as fallback); the real work is **shipping + auto-configuring a GNames-aware
  FName custom-type Lua**: parse the pool block layout (UE4 `TNameEntry` vs UE5 `FNamePool`,
  stride/casing — knowledge already in the DLL's `Serie` module) and feed it the live GNames
  address. GNames must be **ASLR/restart-stable** → reuse the AOB / GWorld-anchor recovery from
  the Copy CE AA Script work (dev-log 2026-06-21). Benefits all three exporters (CSX gains names
  at all; CE XML/Field gain live resolution vs the frozen snapshot + single-scalar coverage).
  Decide: keep DropDownList as a no-setup fallback when the custom type isn't installed.
  *Parent: CSX 7.7+ Binary format + sample.CSX audit (dev-log 2026-06-21, PR #335); FNamePool =
  `Serie` module; GNames anchor = `project-aa-script-gworld-walk`.*

-----

## Teleport Coordinate Library — P1-P5 SHIPPED (builds 2257-2267), needs in-game verification

Design contract: **[teleport-coord-library-spec.md](teleport-coord-library-spec.md)**.
Write-up: [dev-log.md](dev-log.md) 2026-07-23. All five phases are on `dev`, 2777 tests green,
**zero DLL/pipe change**. What remains is verification that unit tests structurally cannot do.

- **VERIFY IN-GAME — the teleport itself** — Effort: **S** · Risk: low.
  Save current pos → move → Teleport selected → land back. Then the map guard: save on map A, load
  map B, confirm plain Teleport refuses and Force is the only way through. Watch for `Tier == 2`
  (raw-write fallback) in the status line — the game may snap the pawn back, which is expected and
  already surfaced. *Parent: P1.*

- **VERIFY IN CE — the emitted picker (both flavours)** — Effort: **M** · Risk: med.
  **Nothing has executed a single line of the emitted Lua.** Push to CE → enable the record → the
  form should open with the ListView filled, the filter narrowing on space-AND, both RadioGroups
  live, Teleport working and Force bypassing the map guard. Highest-risk items, in order:
  (a) the CE control/property set — verified from `CrimsonDesert.CT` CheatEntry 357, not from CE's
  own docs, so `lv.ItemIndex`, `readString(mb + params + 48, 127, false)` and
  `rgGroup.Items.add` are the ones most likely to be wrong;
  (b) panel creation order actually producing the intended layout;
  (c) the no-DLL flavour's raw write, which additionally needs "UE5 Trainer: Setup" enabled first
  and has the known may-not-visibly-move caveat. *Parent: P3 + P5.*

- **VERIFY — CSV against a real spreadsheet** — Effort: **S** · Risk: low.
  Export → open in Excel (check CJK renders, i.e. the BOM did its job) → edit a label → save →
  re-import and confirm the two-stage preview shows the change *before* committing. Deliberately
  try a group named `1-2` and a label starting `=` — the first should show up in the diff as an
  Excel date mangling, the second should survive the armouring round trip. *Parent: P2.*

- **VERIFY — the quick-jump menu label** — Effort: **S** · Risk: low.
  The tab's right-click menu takes a card's label from the first `SemiBold` TextBlock descendant of
  a direct-child `Border`. The new card puts that TextBlock inside an `Expander.Header`; confirm the
  walk still resolves "Coordinate Library" rather than a wrong label. Spec §7 flags this. *Parent: P1.*

- **VERIFY — DataGrid behaviour at scale** — Effort: **S** · Risk: med.
  The grid carries `MaxHeight="260"` precisely because `ContentRoot` is a vertically unbounded
  ScrollViewer and an unconstrained DataGrid would not virtualize. Load ~4 000 entries (import a
  generated CSV) and confirm scrolling and filtering stay responsive. Also measure where CE's
  ListView actually stutters — the picker's 2 000-row display cap is inherited from the reference
  table as an unverified guess. *Parent: P1 + P3.*

- **VERIFY — experimental gating** (DECIDED + implemented, build 2269) — Effort: **S** · Risk: low.
  The card is now gated on `ExperimentalEnabled` like the other five. Confirm the whole card
  appears/disappears with the System-tab checkbox, that it is absent from the tab's right-click
  quick-jump menu while hidden (the code-behind skips a card that is not `IsEffectivelyVisible`),
  and that toggling the gate off mid-preview clears a pending CSV/Lua import. *Parent: user call
  2026-07-23; spec §10.4.*

- **Unrelated finding, worth doing anyway** — Effort: **S** · Risk: low.
  `AobMakerBridgeService.WriteMessageAsync` (`:495-506`) has **no send-side size check**, and the
  plugin's oversize path (`pipe_server.cpp:61`) returns *without writing a response*, so an oversized
  push surfaces as a confusing "no response"/timeout instead of a size error. Add a client-side
  pre-flight check against the 10 MiB cap. A 4 000-entry library is ~480 KB so this is not urgent for
  the coordinate library, but it is the failure mode a user would hit first. *Parent: spec §10.6.*

-----

## Teleport — follow-ups (deferred / future research)

Teleport shipped (Wirbel, build 1027-1043). Works where the possessed pawn is
the visible character (SEED) and, via the deep-force, even on hard-cooked HD-2D
titles (Octopath — character moves). Open items, all per-game / research-grade:

- **Camera/POV doesn't follow on hard-cooked games (Octopath / SE HD-2D)** —
  Effort: **M-L** · Risk: med. **Read-only camera-POV display DONE + LIVE-VERIFIED
  builds 1110-1112** (Teleport tab → "Camera POV" → Get POV; `Wirbel::GetPov` +
  `teleport_get_pov` + a "Get camera POV" mailbox AA record). POV now **reads on
  all four tested titles** — getters on SEED / DQ III, and a fully-reflected
  `CameraCachePrivate.POV` raw fallback on TQ2 / Octopath (getters present but
  `ProcessEvent` returns nothing) — so you can *measure* the camera↔pawn delta.
  **That's the READ; the actual camera-FOLLOW-after-teleport fix below is still
  open** (POV read confirms the divergence but doesn't move the camera). Phase 2 ideas: FOV set/reset (`SetFOV`/`LockedFOV` is the only
  persistently-settable POV component); a "re-anchor camera" nudge after teleport
  (`SetViewTargetWithBlend(pawn,0)` + `SetGameCameraCutThisFrame()`). There is no
  universal Set POV — `UpdateCamera` overwrites it every tick. See
  [teleport-spec.md](teleport-spec.md) §15.
  The deep-force moves the pawn's root
  `ComponentToWorld`, but the camera tracks a separate child component
  (SpringArm / CameraComponent) or follow-camera actor whose world transform we
  never refresh — so the view stays put and can get **stuck unrecoverably**
  (no in-game event re-syncs the view-target chain; a save reload / area
  transition fixes it). Options, in order of cheapness: (1) invoke
  `APlayerController::SetViewTargetWithBlend(pawn, 0)` to re-anchor (likely also
  cooked out on these titles); (2) `APlayerCameraManager::SetGameCameraCutThisFrame()`
  for an instant cut; (3) deep-force the follow-camera component's
  `ComponentToWorld` too (need to *find* it — game-specific); (4) recompute
  child world transforms (manual `UpdateChildTransforms` — native, no
  reflection, hard). **Deferred**: no universal solution; a failed camera nudge
  risks making the stuck-camera worse. In-app disclaimer covers it.
- **TQ2 teleport — FIXED (build 1113); two minor caveats remain.** The old
  "separate visible actor" theory was **disproven** (build 1113 ViewTarget
  diagnostic): TQ2's pawn IS the camera view-target and owns the mesh + CMC. The
  failure was `K2_SetActorLocation` reporting success but not moving + a stale
  cached transform; fixed by always running `K2_SetWorldLocation` + deep-force in
  the CMC-freeze path. Marker teleport now works. Remaining: **(a) cursor teleport
  blocked** — TQ2 strips `GetMousePosition` (returns 0,0 / virtual cursor),
  `GetViewportSize`, and `KismetSystemLibrary`, so there's no generic way to read
  the cursor target (per-game RE only — low value, deferred); **(b) minor visual
  lag** — the mesh snaps over on the next move after a marker teleport (CMC network
  smoothing; a CMC smoothing-offset reset would fix it, Effort S, deferred). See
  [lessons-learned.md](lessons-learned.md) "TQ2 verdict".
- **Gamepad / mouse-extra-button hotkeys** — Effort: **M** · Risk: low.
  Marker hotkey capture is keyboard-only (`RegisterHotKey`). Mouse extra buttons
  + gamepad need low-level hooks / XInput polling ("record on all-released" per
  the user's spec). Deferred until requested.

-----

## Value Search — coverage + memory (build 923 plan)

Dependency order was **V3-A → V3-B → V1a → V3-C → V2**; **all shipped** (V3-C build 949:
DLL owns the set, UI is a server-side-filtered/sorted window; V2 build 954: ceiling
raised to 1M, sort/filter verified sub-second). Remaining open: **V1b** (container
prev-value refine) and **V1c live-verify**.

- **Deep Value-Search candidate → multi-level 🌍 drill** — ✅ **DONE build 1208.**
  Generalised `TryParseStructArrayInner`/`DrillToStructArrayInnerAsync` into
  `TryParseContainerPath` + `DrillDisplayPathAsync` (parse the full multi-`[N]`
  display path into ordered `(name,index)` segments; drill each as a container
  hop or direct-struct field; land on the final leaf). Wired into BOTH the VS/SPC
  `LocateInGWorldAsync` reach branch AND `NavigateToInstanceFieldAsync`
  (Open-in-Live-Walker — also fixes the offset-0 mis-select for deep candidates).
  Verified by a 4-agent audit (drill-sites + scan/capture correctness); the value
  was always FOUND — this was the land-ON-it polish. ⚠ in-game live-verify pending
  (multi-`[N]` 🌍 should land exactly on the SEED `...Tunes[N]` value).

- **Top-level `TSet<FStruct>` / `TMap<K,FStruct>` depth-1 inner leaves (Value Search)** —
  Effort: **S/M** · Risk: low. The static depth-1 collector (`collectStructArrayInner`)
  only covers `TArray<FStruct>`; the recursive `deepEmit` skips `depth<2` to avoid
  double-counting it. So the DIRECT fields of a struct element in a *top-level*
  Set/Map are scanned by neither path. (Nested ones — the SEED `MsTunes` case — are
  depth≥2 and ARE caught.) Fix: add a Set/Map analogue to `collectStructArrayInner`,
  or relax `deepEmit` to `depth>=1` for the Set/Map element side only. Audit #2.

- **SPC Strict-join `prop_offset` migration edge** — Effort: **S** · Risk: low.
  1-level struct-array element rows now store `prop_offset=0` (build 1205, was
  `nf.Offset`); a Strict-mode SPC query that mixes a pre-1205 and a post-1205
  snapshot keys the same logical field differently. Either zero `prop_offset` for
  array-element rows in the Strict key, or bump the schema to force recapture.
  Audit #4. *Cosmetic unless mixing snapshots across the 1205 boundary.*

- **Interesting Props: optional "Locate in GWorld" 🌍** — ✅ **DONE (builds 1531+, MERGED main PR #344 `86fb765`; in-game VERIFIED)** — added a leftmost per-row 🌍 icon column that resolves a live non-CDO instance of the row's class then calls `LocateInGWorldAsync(addr, 0, null, stopAtParent:true)` (the Interesting Functions handoff, gated on `IsGWorldAvailable`). Same PR added the prominent Live Walker failure banner. The panel also gained the ⚙ Locate in GameEngine button via PR #345. See dev-log 2026-06-22.

- **V1b — container prev-value refine (stable key)** — Effort: **M** · Risk: **high**.
  `Candidate.addr` stores a raw element address; TArray realloc already makes it stale,
  and TSparseArray is worse — freed slots get reused, so `c.addr` on refine may point at
  a different logical entry → Changed/Unchanged semantics silently lie. Store
  `container addr + slot index` as a stable key and re-walk the sparse array on refine
  (same idea as snapshot's `SelectArrayInnerKey`). **Do only if refine-on-container is
  actually requested.**
  *Parent: V1a TSet/TMap key|value scan shipped First-Scan-only, build 927 (dev-log
  2026-06-06).*

-----

## Experimental: Snapshot / SPC / Class Pivot

Gated behind the System-tab opt-in (`IExperimentalGate`). Design of record:
[experimental-snapshot-spc-pivot.md](experimental-snapshot-spc-pivot.md). Phases
0/A/B/C (C1+C3-lite+C4+C5+C6) + N1 noise picker all shipped; the engine rework
(in-memory hash-joins), heavy-query cancellation, persisted pivot index, and
Windows-only AOT backend all shipped (dev-log builds 805–923).

- **C2 — find-by-value locator + pivot handoff** — Effort: **M** · Risk: **med**.
  Closes the loop: locate which class/field holds a known value, then hand off into
  Class Pivot.
  *Parent: Pivot Phase C (C1/C3-lite/C4/C5/C6) shipped builds 830-877 (dev-log).*

- **A3c — CE .CT freeze-export from a diff/SPC/pivot hit** — Effort: **M** · Risk: low.
  Copy Address already covers the manual path; this is the full automated freeze export.
  *Parent: A3 diff engine + Copy Address shipped build 817.*

- **Heavier C3 scorer** — Effort: **M** · Risk: med. Jaccard stability + greedy compound
  key + class shortlist / volatility ranking (the "29i-3" scorer).
  *Parent: C3-lite key scorer shipped build 830.*

- **N1 v2 — per-`(class, prop)` deny granularity** — Effort: **M** · Risk: low. v1 is
  by-class; some classes (`ACharacter`, `APawn`) carry both gameplay fields (`Health`)
  and noise (`Velocity`, `LastRenderTime`). v2 would chevron-expand each Top-N row to its
  Top-K noisiest props. **Defer until v1 proves the bulk case is solved.**
  *Parent: N1 per-tab class denylist shipped builds 908-910 (dev-log 2026-06-05).*

- *(optional)* **`discrete`-style gzip blob storage** — Effort: M · Risk: low. Only if
  snapshot DB size becomes a real concern.

-----

## Bytecode cross-reference: property ↔ function — deferred follow-ups

Path 1 (BP Kismet bytecode) core + v2a, and Path 2 (native via Zydis/Denken) forward
direction, both shipped (dev-log builds 838-872).

- **Path 1 v2b — CFG-precise attribution** — Effort: **L** · Risk: **med-high**. v2a's
  "nearest entry offset" mis-attributes a sub-graph reached from multiple events via
  jumps; the `EX_Let*` write detector misses wrapped LHS (`Other.Field` / `Struct.Member`
  / `Arr[i] = x`). A real variable-length decoder (follow `EX_Jump` / `EX_JumpIfNot` /
  `EX_ComputedJump`, parse the LHS expression tree) fixes both. Reference:
  `vendor/RE-UE4SS/.../KismetDebugger.cpp` (`render_expr`) + `EExprToken`. **Only when a
  real mis-attribution motivates the cost.**
  *Parent: Path 1 + v2a shipped builds 838-861.*

- **Path 2 follow-ups** — (a) **reverse direction** (property → native funcs; needs
  disassembling every native function per query — expensive); (b) **SIB-indexed**
  `[reg+idx*scale+disp]` accesses (currently skipped); (c) **CFG-aware branch following**
  (only fall-through + direct call/tail-jmp followed today); (d) live tuning of the
  `this`-tracking + Func-offset detector across more games.
  *Parent: Path 2 native UFunction analysis shipped builds 862-872.*

-----

## Call-UE-function / invoke

- **#2 Live ProcessEvent Call Profiler** — ✅ **SHIPPED build 2109** (new `Linie` module +
  "Live Funcs" tab). Ranks UFunctions by **observed behaviour** (Start → perform action →
  Stop → see what fired), the root-cause answer for game-specific functions (OpenShop/Dash)
  name heuristics can't find. Hot-path gate is one relaxed `atomic<bool>` load when off (the
  map + mutex are only touched while recording — the recording-window mutex was accepted over
  a lockless counter per the plan; escalate to a sharded table only if an in-game benchmark
  shows contention). `pe_profile_start` forces the PE hook via `UE5_EnsureGameThreadHook`.
  **Remaining:** in-game acceptance test (shop/dash on a live UE title) + confirm nil overhead
  with recording off. See [dev-log.md](dev-log.md) build 2109.

- **#7 View Snap Hotkey (Property → snap-to-step)** — Effort: **S-M** · Risk: **low**.
  Bind a CE hotkey that snaps a Float/Double property to the next N° step (rotation
  snap) — generalises to MoveSpeed multipliers, zoom levels, time-dilation cycling.
  New row action + `SnapHotkeyDialog` + `scripts/ue5_snap_helper.lua` +
  `SnapHotkeyScriptGenerator.cs`. **95% mirrors the build-719 freeze Route B.**

- **Add-on: AA(Baked) "Auto-tick every N ms"** — Effort: **S** · Risk: low. Wrap the
  generated `invokeUFunction` call in a `createTimer(N, callback)` block ([DISABLE] tears
  it down); same per-script keyed handle table as FreezeScript. Lands as a 1-day add-on
  after #7 (both touch the script generator + dialog).

- **#5 v2 — ObjectProperty return resolution + recursive struct expansion** —
  Effort: **S + S**. (a) Resolve ObjectProperty/ClassProperty returns to "Name (Class)"
  via a DLL pipe round-trip (`resolve_object_name(addr)` or extend the invoke response).
  (b) Recursively expand nested structs (`FHitResult.Location` → its own FVector rows).
  *Parent: #5 structured-return DataGrid shipped build 775, PR #211.*

- **#0c FTransform Translation offset** — Effort: **S** · Risk: low. `VectorStructNames
  (FTransform)` returns empty → zero hits. Needs per-version Translation offset detection
  (UE4 / UE5-non-LWC at +16, UE5 LWC at +32).
  *Parent: Value Search Phase 2 vectors shipped build 757, PR #208.*

- **FString / FText / TArray input in baked AA Script** — Effort: **M** · Risk: **med**.
  Functions like `KismetSystemLibrary::PrintString` are observable side-effect verify
  targets but unreachable — the helper's `writeBakedParams` only handles scalar inputs.
  Needs CE-side buffer alloc + FString header write (ptr/count/max) + keep-alive + free
  in the cleanup timer. Same pattern for FText + TArray-of-scalars. (Open since the build
  643-644 ES2 live test.)

- **LiveWalker batch generator (v2 of the CT batch)** — Effort: **S-M**. Heterogeneous
  rows (functions + fields + struct sub-fields + array elements) + drilldown state — needs
  its own UX pass.
  *Parent: #3 multi-row → one .CT batch (Interesting Funcs/Props) shipped build 760.*

- **Dual-connection pipe (eliminate head-of-line blocking)** — Effort: **M** · Risk:
  **high** (in-process DLL concurrency). **POSTPONED 2026-06-01.** Full design:
  [multi-connection-pipe-proposal.md](archive/multi-connection-pipe-proposal.md) *(archived — superseded by [multipipe-eval.md](multipipe-eval.md))*. Engine-side
  concurrency is already safe (builds 792/793 + SessionManager); residual risk is Fern's
  accept/shutdown rewrite. Benefit is moderate (parallel scans already shrank the blocking
  window) — revisit only if "UI freezes during a big scan" becomes a real pain.

- **KismetMathLibrary stub-pattern UX hint** — Effort: **S** · Risk: low. On UE 5.5+
  cooked Shipping, `KismetMathLibrary::Add_IntInt` etc. consistently return 0 (cooker
  strips the `execXxx` thunk). Add a "Recommended verification targets" footer hint when
  the selected class is `KismetMathLibrary` / `KismetSystemLibrary`, and update
  lessons-learned / test-games. (Not a feature to enable calling them — a UX redirect.)

- **Mimic: zero the ReturnValue slot before invoke** — Effort: **S** · Risk: low. ES2
  showed Before/After dumps identical (stale `0x49`) so we can't tell "wrote 73" from
  "didn't touch ReturnValue". Overwrite the slot with a sentinel / zero before calling PE
  so the After dump is unambiguous. ~2-line patch in `Mimic.cpp` (both fast path + game-
  thread dispatch).

- **CE Lua AA Script activation hang — UX hardening** — Effort: **M** (mitigation) ·
  Risk: low. AA Script sometimes never reaches the mailbox (CE Lua froze or hid an error).
  Mitigations: re-arm helper-injected check on UI Connect; mailbox heartbeat `print()`
  before the write; early-exit `if not g_invokeMailbox then showMessage(...) end` in the
  helper. (UX hardening, not a correctness bug — we can't distinguish AA-error from CE
  freeze from the DLL side.)

-----

## Time / Timer control (Hemmung) — feasibility evaluated (2026-07-13), not yet built

Eval memo: memory `project-timer-feature-eval`. Multi-agent + adversarially verified.
User ask = auto/manual-assisted discovery of game **time/timer components**, list the
**methods** handling them, real-time **lock/reset/adjust + multi-select**, and a
**cross-session persistence** path (Copy-CE-Field-like). Confirmed the DLL has ZERO
`TimeDilation`/`WorldSettings`/`TimerManager`/`CustomTimeDilation` refs today — new
capability, but every building block already ships. **Verdict: build in layers; the L2/L3
native-RE parts are exactly the reflection-invisible ones — cut them from v1.** Order below.

- **L0 — timer discovery docs recipe (ships today, ~0 code)** — Effort: **S** · Risk: low.
  BP-authored `Cooldown`/`RemainingTime`/`RespawnTime`/`Duration` floats are reflected
  UPROPERTYs → already found by Property Search (by name) + Value Search (by value; a
  ticking countdown survives repeated **Decreased** refines, count-up via Increased, paused
  via Unchanged). Lock via class-wide **Freeze** (`FreezeScriptGenerator`+`ue5_freeze_helper.lua`,
  restart+respawn-safe by class+offset re-enum); multi-select via **CheatTableBuilder** (a
  `List<CtPropertyRow>`, no builder change). Deliverable = a `docs/tips.md` recipe + a Group
  Scan example ({Elapsed↑, Remaining↓} in one object). *Parent: this eval.*

- **L1 — global game-speed control + Timing discovery category — DISCOVERY + DLL SHIPPED (build 2148); UI Time card (Part C) REMAINING** —
  Effort remaining: **M** · Risk: low. **DONE (build 2148, dev):** the `Hemmung` DLL module +
  `PropertyCategory.Timing` discovery category shipped and green (all 2453 C# tests + C++ self-tests).
  `Hemmung.cpp/.h` (roster 🟢) = absolute-value `Laufen` sibling: DIL_GLOBAL `AWorldSettings::TimeDilation`
  (GWorld→PersistentLevel→WorldSettings reflected chain + `Aura::FindInstancesByClass("WorldSettings")`
  fallback) + DIL_PAWN pawn `AActor::CustomTimeDilation`, write-on-drift re-assert worker, clamp
  [0.0,100.0]; exports `UE5_Set/ResetTimeDilation`, Mimic `CMD_TIME=15` (`TimeOp` SET/RESET, ufuncAddr =
  target), pipe `set/reset_time_dilation` + `get_time_state`. Discovery: `PropertyScoringTable.Timing`
  (append LAST → BuffDuration stays Combat) + `TimeStructTypes` (Timespan/DateTime/QualifiedFrameTime/
  FrameTime/Timecode) + `SeedQueries` timer terms + `ClassLocationScorer` GameplayEffect/GameplayAbility/
  WorldSettings +2 + function-side `UtilityKeywords` widen (Cooldown/Dilation/Delay/Interval/Elapsed/
  Recharge). Dev-log 2026-07-13; memory `project-timer-feature-eval`.
  **Part C UI Time card — DONE (build 2149; UI SUPERSEDED at build 2207 by the dual-row World+Player
  card — see dev-log 2026-07-15).** A "Time Dilation" card in the Teleport panel beside
  Move-Speed/Gravity: *as first shipped,* a "Player only" toggle (global `TimeDilation` vs pawn
  `CustomTimeDilation`), 0–3×
  slider + % + presets (Freeze/¼×/½×/1×/2×) + Apply/Reset/↻ + badge/readout; new `IDumpService`
  `Get/Set/ResetTimeDilation` (+ `TimeDilationKnob`/`TimeDilationSetResult`/`TimeState` models) + VM
  commands + en.axaml strings + 6 VM tests (2459 C# green).
  **CE Lua/.CT generation — DONE (build 2150).** `TimeDilationScriptGenerator` (mirrors
  `MovementScriptGenerator`): stateful `[ENABLE]`/`[DISABLE]` records poking `CMD_TIME=15` (op SET on tick, op
  RESET on untick); `CeLuaHygiene`-compliant; wired into the Teleport panel's "Add to CE" (2 records: World +
  Player) + "Save .CT" batch; 6 generator tests. So the dilation lock now works from a standalone CE table
  without the UI.
  **Persistence — DONE (build 2151).** (1) live read-back: `SetConnected` reflects the DLL's held dilation on
  connect + on target-switch (syncs the slider to the engaged value; `RefreshHeldTimeStateAsync`), disconnect
  resets the badge — the "state lives in the DLL, survives a UI reconnect" markers model. (2) disk preference:
  `TeleportUiOptions.TimeDilation`/`TimeTargetIsPawn` in `ui-options.json` pre-fill the last value+target
  across UI restarts (NOT auto-applied; live read-back wins). +2 VM tests + options round-trip.
  *(Those two keys were renamed `WorldTimeDilation`/`PawnTimeDilation` at build 2207 with no migration.)*
  **L1 COMPLETE + LIVE-VERIFIED on Elliot (UE4.27, build 2151)** — log confirms `set_time_dilation target=pawn
  value=0.5` → `hold 0.5000 (rc=0)`, held 0.5/1.0/2.0/1.4688×, reset clean, `get_time_state` polled on connect.
  Per-pawn `CustomTimeDilation` exercised; global `WorldSettings::TimeDilation` wired+unit-covered but not yet
  live-exercised (verify opportunistically). Also NOT built (deferred):
  `SetGlobalTimeDilation`/`GetTimeSeconds` invoke wrappers, and a dedicated opt-in function-side
  `FunctionCategory.Timing` bucket (timer methods currently land in Utility at weight 3 — below threshold
  without a class bonus / Show-All).
  **DON'T over-promise (adversarial corrections):** (a) locating the ACTIVE WorldSettings is
  NOT one line — `Aura::FindInstancesByClass` matches immediate class FName only (no `IsA`,
  `Aura.cpp:1365`), misses BP subclasses + can't disambiguate streaming/PIE sub-worlds →
  prefer INVOKING `SetGlobalTimeDilation` (calls `GetWorldSettings()` internally, price = its
  `[Min,Max]` clamp; a direct write bypasses the clamp but needs the right instance);
  (b) `Ubel` only READS today — a generic "write reflected float by name on object" surface is
  new; (c) paused worlds can't be stepped via dilation + active Sequencer flickers within the
  250ms drift window; (d) cross-game parity is CONDITIONAL — L1 inherits the tool's GObjects
  baseline burden on hard-cooked/SE-fork/encrypted titles, and bespoke non-UE time multipliers
  won't respond at all. Persistence (Copy-CE-Field-like), best→worst: class-wide **Freeze** >
  **GWorld-anchored AA Script** (`CeXmlExportService.GenerateGWorldWalkedSymbolXml`, `useAob`;
  registers a SYMBOL only → add a CE freeze or route TimeDilation through FreezeScriptGenerator
  with className=WorldSettings) > **StandaloneTrainer**; NONE survive a game PATCH; multi-select
  → CheatTableBuilder verbatim. *Parent: this eval; reuses movement-tuning-laufen + godmode-spec
  + autodetect-stats + standalone-trainer + aa-script-gworld-walk.*

- **L2 — GAS effect cooldowns (DEFER; feasible-with-caveats)** — Effort: **L** · Risk: high.
  Deep `UAbilitySystemComponent::ActiveGameplayEffects` walk: partly non-UPROPERTY
  (FastArraySerializer), remaining time is COMPUTED (`Duration-(Now-StartWorldTime)`, needs
  world time), version-fragile. Current ASC folding (P4 cross-object) reaches
  SpawnedAttributes/AttributeSets VALUES, NOT ActiveGameplayEffects. Only if a GAS title
  motivates it. *Parent: this eval.*

- **L3 — live `FTimerManager` timer enumeration (DEFER; research-grade native RE)** —
  Effort: **XL** · Risk: high. `FTimerManager` is NOT a UObject (FNoncopyable via native
  `UWorld::GetTimerManager()`); its `FTimerData` timers live in a TSparseArray+heap with
  handle indirection, none reflected. Per-version native layout (ExpireTime widened float→double
  in UE5; internals refactored across 4.x) + often-unresolvable C++/lambda callbacks =
  Avowed-packed-FUObjectItem-tier. A reflected BP `FTimerHandle` var is opaque (just an index)
  → does NOT yield remaining time. Recommend NOT in v1. *Parent: this eval.*

- **E — Linie cadence flag — DONE + LIVE-VERIFIED on Elliot (UE4.27, build 2158).** `Linie::Stat` Welford
  inter-arrival mean/variance (fed the timestamp `Stark.cpp:143` already reads, zero hot-path cost);
  `pe_profile_get` emits `mean_period_ms`/`cv`/`gap_samples` + logs the periodic candidates; UI
  `PeProfileEntry.IsPeriodic` (≥3 gaps, CV≤0.25, period out of the ~40 ms frame band, ≤30 s) → "Timer" badge +
  Period column + "Periodic only" filter (idle-window workflow). +12 tests. **Verified:** an idle recording
  flagged 3 periodic funcs out of ~90 (`BP_SupportFairy_C::TryAttackEnable`+`ExecuteUbergraph` @ ~325 ms
  cv 0.02 = a real ~3 Hz BP timer; `ProvideSingleActor` ~108 ms), Tick correctly excluded, stable across two
  windows. Native lambda/member-ptr timers bypass ProcessEvent (documented). *Parent: this eval; extended
  Linie/LivePEProfiler build 2109.*

-----

## MindsEye licensee fork — follow-ups (GObjects + GNames SHIPPED builds 2220/2238)

Both halves are live-verified end-to-end, but **on game version 7.3.1 only** (PE hash
`0863E3B90C993000`). Everything below was identified while shipping and deliberately not blocked on;
full context + the re-derivation playbook is in [mindseye-fork-notes.md](mindseye-fork-notes.md).
**Before touching any of these, check the PE hash** — if it moved, the constants come first.

- **Wide `FNameEntry` payloads are not de-obfuscated** — Effort: **S-M** · Risk: low.
  `Serie::GetString` applies the XOR only in the **ANSI** branch; the wide branch reads and
  `EncodeUtf16`s the raw ciphertext, which `IsImplausibleWideName` then rejects → empty name.
  `Genau::ObfChainOk` likewise bails on the first wide entry ("stop, do not judge"). The fork ships a
  wide twin de-obfuscator (RVA `0x0178B540` on the solved build, `add r8,r8` — i.e. the same key,
  applied over 2-byte units), so wide names **are** obfuscated and the key lookup already works for
  them. Low practical impact (most FNames are ANSI) but it is a real hole: any wide name silently
  resolves empty rather than wrong. Fix = mirror the ANSI XOR into the wide branch (per-byte over the
  UTF-16 payload) + let `ObfChainOk` corroborate on wide entries instead of aborting.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

- **Process-lifetime caches that no rescan clears** — Effort: **S** · Risk: low.
  Two function-local statics survive a full re-scan: (1) `Genau::TryObfuscatedPool`'s
  `s_ctxTried`/`s_ctxCache` — `FindGNames`' reset block clears `g_nameChunksOffset` /
  `g_namePayloadGap` / `g_nameKeyTableCtx` but **not** these, so **one failed AOB scan is sticky for
  the whole process** and a rescan never retries the key-table resolve; (2)
  `Flamme::IsExperimentalEnabled()` caches its answer, so toggling the UI experimental switch after
  the DLL is loaded has **no effect until re-inject** (arguably correct — but it is undocumented in
  the UI and reads as a broken toggle). Fix (1) by clearing the statics from the same reset block;
  for (2) either re-read on each query or surface "re-inject required" next to the toggle.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

- **No test coverage for any of the fork-specific paths** — Effort: **M** · Risk: low.
  `dll/tests/` holds only `dll_helpers_test.cpp` + `utf8_helpers_test.cpp`; nothing exercises the
  preset-bound `LayoutPreset::itemHint` gate, `TryObfuscatedPool`'s acceptance rule, or
  `Serie::LookupTagKey`'s open-hash probe. All three are **pure enough to unit-test without a game**
  (fabricate a synthetic chunk/pool/key-table in a buffer), and two of them encode load-bearing
  invariants a refactor could silently break: the item hint must stay **evidence-gated**
  (`hGood>=8 && hBad*4<=hGood`) so it can never win on a 50%-aliased stride-16 read, and the pool
  must be **REFUSED when the key table is unresolvable** even though block 0 decodes. Also worth a
  regression test: the tag→key cache publishes value+flag in **one** `std::atomic<uint16_t>` — the
  two-plain-stores version produced wrong keys across threads.
  *Parent: MindsEye, dev-log 2026-07-19 (builds 2220 + 2238).*

- **`find_anchors.py` from the re-derivation playbook is not committed** — Effort: **S** · Risk: low.
  [mindseye-fork-notes.md](mindseye-fork-notes.md) step 0 tells the reader to run
  `python find_anchors.py <exe>`, but `tools/pe/` only has `disasm_function.py` (which takes explicit
  VAs — it neither parses `.pdata` nor searches for `__FILE__` anchors), `minidump_triage.py` and
  `pe_imports_exports.py`. So the playbook's first two steps have to be re-scripted ad hoc, which is
  exactly the friction the playbook exists to remove. Either commit the script under `tools/pe/` (with
  a line in [tools/README.md](../tools/README.md)) or rewrite steps 0–2 against what is actually
  committed. *Parent: MindsEye docs, commit `8ef4a9f`.*

- **`GAP = 2` is hardcoded, so a second fork needs a code change** — Effort: **S** · Risk: low.
  The obfuscated-payload gap is a `constexpr int GAP = 2` inside `TryObfuscatedPool` ("the only forked
  geometry seen so far"). That is the honest call today — inventing a search over gaps would weaken
  acceptance for zero known benefit — but note it as the first thing to generalise if a second
  licensee fork with a different `FNameEntry` shape appears. Do **not** pre-emptively loosen it.
  *Parent: MindsEye GNames, dev-log 2026-07-19 (build 2238).*

-----

## Property scoring / discovery

- **Class Family Browser (Proposal C)** — Effort: **L** · Risk: **med**. New "Class
  Family" tab bucketing game classes by inferred role (Character / Pawn / Inventory /
  Stats / Save / Components / DataAssets / DataTables / GameMode) — the "I have no idea
  where to start in a new game" entry point. **NOT a jump-in-and-code task** — the
  classification heuristic + UI design needs its own planning round (cluster the dump
  corpus's BPGCs by property-name similarity first).

- **Proposal B — per-row "similar BP-added properties"** — Effort: **M** · Risk: low.
  **DEFERRED indefinitely.** When the user lands on `bCanBeDamaged @ AActor`, surface a
  side-panel of fuzzy-matched game-specific bools (`bIsImmortal @ BP_PlayerCharacter_C`).
  B' (the broad-sweep Interesting Properties) already covers the workflow — revisit only
  if a real user reports the specific gap B fills.

- **Runtime `keywords.json` override** — Effort: **M** · Risk: **med**. Let users tune
  the scoring tables without recompiling (source-gen JSON for AOT; hardcoded fallback;
  additive vs replace mode; "Export current tables to JSON" seed button). **Only if a
  user actually asks.**

- **More-genre dump coverage (calibration)** — Effort: **S** (mostly user-side). The
  corpus is heavy on JRPG/sim/ARPG/FPS/racing/sandbox; missing MMO/fighting/horror/RTS/
  sports-sim. Dump 3-5 games per genre → re-run `scripts/analysis/analyze_dumps.py` → PR
  keyword adds with evidence attached.

-----

## Carryover capability gaps

Pick up when the active plan finishes or when blocked.

- **MulticastSparseDelegateProperty UE 4.23-4.27** — Effort: **L** · Risk: **med**.
  Closes the last delegate-flavour gap (UE5 landed in PR #194). UE4 needs a separate AOB
  + walker branch: outer key is `FObjectKey {FWeakObjectPtr; int32 SerialNumber}` (16B)
  not raw `UObjectBase*`, outer stride ~0x60 → ~0x68, key match reconstructs `FObjectKey`
  from `(owner, GetSerialNumber(InternalIndex))`. Walker currently returns
  `supported=false` for UE < 5.0.

- **Find Refs v4 — TMap / TSet weak-like inner sides** — Effort: **M** · Risk: **low**.
  Currently Object/Class only; weak/soft pointer collections (`TMap<UObject*,
  FWeakObjectPtr>` etc.) silently miss target hits. Reuse the v3 weak-resolve helper
  inside the existing TMap/TSet walkers in `Aura::FindReferencesToUObject`.

- **FieldPathProperty drill-down + Find Refs** — Effort: **M** · Risk: **low**. Last
  remaining no-handler property type. Rare in shipping games (only Editor-derived
  classes) — genuinely low priority.

- **GWorld coverage** — Effort: **S each** · Risk: low. Two remaining titles:
  - **Star Wars Jedi: Survivor** (UE 4.27?) — untested; needs an AOB sweep + result triage.
  - **Satisfactory** (UE 5.3, modular DLL build) — (1) proxy DLL injection fails (loader
    bypasses normal proxy hooking; workaround = CE manual injection); (2) GWorld pattern
    likely lives in `CoreUObject-Win64-Shipping.dll` — adapt `Genau::FindAll` to scan
    multiple modules when the primary scan fails.

- **`kPublishers[]` table additions** — Effort: **S each** · Risk: **high** (if added
  casually — wrong publisher bias overrides correct detection). Only add a publisher with
  ≥3 misdetected titles AND a clear pattern. Wait for real misdetection reports.

- **UE 6.0 readiness — version-string map entry + remote-object watch (do only with a real UE6 binary)** —
  Effort: **S** · Risk: low. UE 6.0 is **layout-identical to UE 5.8** across every structure the dumper
  reads (verified `origin/5.8..origin/ue6-main`, 2026-06-30 — see [technical-notes.md](technical-notes.md));
  the core walk + AOBs are already UE6-ready, nothing to implement now. Two small, deferred items:
  (1) **Version-string map** — `Genau.cpp:2159` tops out at `{"5.8.",508}`; no `6.0.`→600 entry, so UE6
  games fall to the bias fallback (dynamic detection still works, so this is detection-clarity only).
  Adding `{"6.0.",600}` needs a `kVersionDetectLogicRev` bump (forces a one-time re-detect of all cached
  games) **and** care vs game-version strings like "6.0" (mirror the "15.6.0" guard at `Genau.cpp:2221`).
  (2) **UE6 AOBs** — add UE6-specific AOBs only against a real binary; our AOBs wildcard displacements and
  resolve the pointer, so the 5.8/6.0 reordered fields are handled post-resolve by the existing "UE5.8"
  preset. `StaticAllocateObject` gained a `UObject*` param (body changed) but the GObjects AOBs target the
  `mov reg,[rip+GUObjectArray]` sites, not that prologue. **Watch-item (far future, not shipping-default):**
  `UE_WITH_REMOTE_OBJECT_HANDLE` (experimental multi-server / UEFN remote objects, OFF in normal shipping)
  inserts `FRemoteObjectId` into `UObjectBase` (between `InternalIndex` and `ClassPrivate`) and `FUObjectItem`;
  if a UE6 game ships it ON, the hardcoded `OFF_UOBJECT_*` offsets shift by `sizeof(FRemoteObjectId)` and
  FUObjectItem packing is forced off — a real handler branch would then be needed.
  *Parent: UE6-vs-5.8 parity audit (2026-06-30); per-structure detail in technical-notes.md.*

-----

## Pending live-game verification (verify only — no code)

- **Flaky: `SnapshotViewModelTests.GroupMatch_MissingValue_ShowsErrorNoCandidates`** — failed ONCE
  in a full parallel run on 2026-07-23 (build 2318), then passed 25/25 three times in isolation and
  green on an immediate full re-run. Unrelated to the winmm/proxy work that was in flight. This test
  class has prior form for snapshot-DB concurrency flakes (see `feedback-ci-only-test-flakes`, and
  PR #451's concurrent-first-open fix), so the likeliest cause is another store-level race under
  parallel load rather than the assertion itself. **Not chased** — one observation is not a
  reproduction. If it recurs, capture whether `GroupCandidates` was non-empty or `GroupStatusText`
  empty, since those point at different halves. Effort **S** once reproducible.


Shipped + unit-tests-pass but unproven on real games:

- **Copy CE Field drills object-pointer arrays — leaf + GWorld-path spine + dup-crumb dedup — DONE +
  MERGED (PR #323, builds 1364-1379).** LEAF (`SpawnedAttributes[2]` → `CharacterAttributeSet` →
  `HealthPoint`), SPINE 2b (`PathStepToBreadcrumbs` splits a Locate-in-GWorld `PlayerArray[0]` hop into
  container + element), and DEDUP 2c (`DedupeConsecutiveBreadcrumbs` collapses a redundant consecutive
  container crumb in `ExportCeFieldXmlAsync` + `CleanBreadcrumbs`) all **LIVE-VERIFIED on Elliot AND the
  deeply-nested Gundam SEED chain** (nested + Collapse-chain). Unit-tested
  (`...ObjectArray_WithResolvedElement_DrillsElementGroup`, `...PathThroughObjectArrayElement_EmitsElementDerefNode`,
  `DedupeConsecutiveBreadcrumbs_*`, `..._DeepDistinctChain_Unchanged`). **(b) DONE + LIVE-VERIFIED
  (builds 1380-1388) — Back-nav onto a path-synthetic container crumb now re-hydrates the array element view.** The
  crumb's `ContainerField` is null (the `GWorldPathStep` carries no `ArrayDataAddr`/`ArrayCount`/element
  list), so Back-nav fell through to a parent re-walk and rendered the PARENT object grid (a silent
  mis-render — NOT a literal duplicate; the 2c dedup already covers the export-time crumb). "Give it a
  `ContainerField`" is infeasible (path step lacks the data) → `TryRepopulateSyntheticContainerAsync`
  LAZILY re-walks the parent + matches the field by name+offset + `RepopulateContainerView`, wired into
  all 4 re-display sites (NavigateToBreadcrumb, GoBack normal + pre-bookmark restore, LoadBookmark) +
  `RefreshAsync`'s container gate broadened. 7 new tests; C# 1648/0, AOT 46.5 MB. **(a) DONE +
  LIVE-VERIFIED (builds 1389-1390) — Map/Set (and interface-array) element hops in a GWorld-path spine
  now split into container + element crumbs.** The DLL `emit()` lambda was widened 6→8 args to thread `elemStride`
  (Map `pairStride` / Set `elemStride` / interface-array 16) + `elemValueOffset` (Map value's within-pair
  offset; 0 for set/key/interface) through `GraphEdge`/`GraphPathStep` → Fern `elem_stride`/`elem_value_offset`
  → C# `GWorldPathStep` → `PathStepToBreadcrumbs` (element crumb offset = `ElementIndex*stride + valueOffset`;
  container crumb strips the `.Key`/`.Value` suffix so Back-nav re-hydration matches). All emit callers
  updated (`GetRelatedObjects`/`AppendOwnedSubObjectLeaves`/test mock); object/class arrays keep the
  hardcoded-8 path. 6 new tests (5 C# + 1 dll round-trip); C++ 697/0, C# 1653/0, AOT 46.5 MB. Adversarial
  review confirmed Map/Set/Set offsets correct + reachable; accepted nits: struct-nested dotted base name
  doesn't re-hydrate (pre-existing, affects arrays too, CE math still correct) + int32 element-offset
  arithmetic (theoretical, `FieldOffset` is int by design).
- **Value Search `TOptional<T>` scan (V1c)** (build 942). Scan a known value held in a
  `TOptional<int/float/FString>` UPROPERTY → confirm the row appears under the optional's
  field name and a Next Scan prunes; confirm an **unset** optional doesn't surface on a
  scan for `0` (the `bIsSet` gate). Layout helper is unit-tested; the field walk needs a
  live game with optional UPROPERTYs.
- **Property freeze (Route B)** on a respawning-NPC game (build 719). Watch: tick FPS
  impact (50ms × N instances), rescan cadence at respawn, vtable-liveness guard on level
  transition, AOBMaker gating UX, multi-script coexistence. First candidate: Geri (UE
  4.27).
- **Build-648 ProcessEvent fix** re-verify on ES2 (UE 5.5) + Geri (UE 4.27): look for
  `GameThreadDispatch: validation OK — hook fired N times`; previously-`-5`-timing-out
  instance invokes should now succeed. Lower-priority extras: a UE 4.18-4.24 game (smaller
  vtable / lower slot) + a heavily-modified publisher fork.
- **Static-native PE fast path** (build 636) latency vs game-thread dispatch on an active
  session; confirm stateful UFunctions still route through dispatch (don't fall into the
  fast path by accident).
- **FPROPERTY_FLAGS offset fix** (build 642): sweep the 12+ tested games' Class Structure
  Return columns + confirm baked PARAMS no longer include ReturnValue as an input.
- **Verify Return Value diagnostic** (build 637/644): pointer-return shows `0x` prefix;
  FString-return shows the "see After: dump above" hint.
- **`walk_functions_batch` follow-up** — Effort: **S**. Sister to `walk_class_batch`;
  DumpAll still does `WalkFunctions` single-call per class. Same byte-equivalence safety
  net. **Skip unless profiling shows it as the new bottleneck.**

-----

## See-through (Schlacht) — "pass light/shadow through too?" — EVALUATED (mostly WON'T-DO)

**Question:** can See-through also let the occluder's **light/shadow** effects pass through, not just
its mesh? **Verdict: split by lighting type — dynamic is already handled; baked is infeasible from an
injected DLL.** (36-agent adversarial verify against UE engine source, 2026-07-09.)

- **Dynamic / real-time light (movable lights, Lumen GI+reflections, DF shadows/DFAO, HW ray tracing)
  — ALREADY passes through, no code change.** `AActor::SetActorHiddenInGame(true)` sets `bHidden` →
  `UPrimitiveComponent::ShouldRender()` false → `ShouldComponentAddToScene()` false (default flags) →
  the primitive is dropped from the render scene entirely, so it is absent from the **shadow-depth
  pass** too — the dynamic shadow vanishes with the mesh (community's `bCastHiddenShadow=true` recipe
  exists only because default hiding drops the shadow). UE5 does the same for the mesh distance-field /
  Lumen scene: `PrimitiveNeedsDistanceFieldSceneData()` has `IsDrawnInGame()` as a required OR-term,
  and `FScene::UpdatePrimitivesIsDrawn_RenderThread()` calls `DistanceFieldSceneData.RemovePrimitive()`
  + `LumenRemovePrimitive()` on the hide branch by default; the HW-RT gather also skips `!bDrawInGame`
  primitives. So on a Lumen/movable-light game, hiding the wall already removes its shadow, GI
  occlusion, and RT contribution.

- **Exception (fixable): a game that sets `bCastHiddenShadow=true` (or `bAffectIndirectLightingWhileHidden`)
  on world meshes** keeps the shadow/GI after hide (that flag's whole purpose is cast-while-hidden).
  **Only actionable enhancement:** alongside `SetActorHiddenInGame`, also invoke
  `UPrimitiveComponent::SetCastShadow(false)` / `SetCastHiddenShadow(false)` (and
  `SetAffectDistanceFieldLighting(false)` for Lumen/DF) on each of the hit actor's primitive components,
  restoring on un-hide. All are `BlueprintCallable` UFUNCTIONs reachable via the existing
  `UE5_CallProcessEventEx` ProcessEvent path; component enumeration already exists (`GetRelatedObjects`).
  Effort: **S** · Risk: low. **Do only if a real game shows a lingering shadow after See-through hides
  the mesh** (LIVE-VERIFY first, per the module's ethos). Won't help baked lighting.

- **Baked / static light (Static or Stationary mobility — the common case for UE4 & perf-sensitive UE5
  world geometry: locked-60fps, mobile, VR) — INFEASIBLE, WON'T-DO.** The wall's shadow is baked by
  Lightmass into the **receiving** surface's (floor / neighbouring wall) lightmap texture (and per-object
  distance-field shadow maps for Stationary), stored per-mesh in the `MapBuildDataRegistry` — it lives
  on the receiver, not the caster. `SetActorHiddenInGame` only toggles the caster's own primitive
  visibility; it cannot touch another mesh's lightmap, so a **"ghost shadow"** stays exactly where the
  wall was. Removing a baked shadow needs an editor-time **Build Lighting** (Lightmass is editor-only,
  stripped from shipping/cooked builds); no runtime API recomputes lightmaps. The only external "fix"
  is forcing the whole level to unlit/dynamic (`r.AllowStaticLighting 0` + restart — global, breaks all
  level lighting), which isn't worth it. This is why many games show a residual shadow after See-through
  hides the mesh — nothing we can do about it from a DLL.

*Parent: Schlacht Stage 1 (dev-log 2026-07-08 build ~1989; project-seethrough-occluders-schlacht).*

-----

## Output-monitor pin — "the game has no monitor-select UI" — EVALUATED (2026-07-23), NOT BUILT

**Question:** on a dual-monitor setup, when a game exposes no output-display setting, can we fix it
with **UE functionality**? **Verdict: the UE reflection layer has no concept of an output monitor —
the monitor-selecting step is Win32/DXGI. UE reflection only contributes the windowed↔fullscreen
toggle and the persistence.** And the hard part is not the initial move, it is that the game
**drifts back** — so the deliverable is a *pin*, not a one-shot move.

**What UE reflection does and does not give us**

- Stock UE has **no** monitor-index `UPROPERTY`, no BlueprintCallable monitor selector, and no cvar.
  (The `-monitor=N` recipe circulating since Froyok's 2018 post is an *engine source modification*,
  not stock behaviour.) `r.setres WxH[w|f|wf]` changes mode/resolution, never the screen.
- **Invokable today** (BlueprintCallable ⇒ in the reflection function table ⇒ reachable via
  `invoke_function`): `UGameUserSettings::SetFullscreenMode(int32)` (`EWindowMode` 0=Fullscreen /
  1=WindowedFullscreen / 2=Windowed), `SetScreenResolution`, `ApplyResolutionSettings(bool)`,
  `ApplySettings(bool)`, `SaveSettings()`.
- **NOT invokable:** `SetWindowPosition()` / `GetWindowPosition()` are **not** BlueprintCallable, so
  they are absent from the reflection function table. The backing `WindowPosX` / `WindowPosY` *are*
  config properties (default `-1` = centre) ⇒ writable via Property Search / Live Walker / Solide
  Force. That yields a no-code path (**write WindowPosX/Y → invoke `SaveSettings()` → restart**) but
  it needs a restart and collides with the documented UE 4.16+ "re-centres itself after the startup
  map loads" override.
- Why the move-then-fullscreen sequence works at all: UE `WindowedFullscreen` resolves via
  `MonitorFromWindow`, and DXGI exclusive fullscreen picks "the output containing most of the client
  area" when `pTarget` is NULL — **both follow the window**. So `SetFullscreenMode(2) → move the
  HWND → SetFullscreenMode(1)` lands on the target screen.

**Drift is event-driven, not continuous** — regain focus / alt-tab / `WM_DISPLAYCHANGE` /
swapchain reset. Unity's issue tracker documents exactly this symptom ("exclusive fullscreen always
opens on monitor 1 after regaining focus even when monitor 2 is set as primary"). So a pin does
**not** need a high-frequency poll.

**Three pin mechanisms, lightest first**

- **(a) Rewrite `WM_WINDOWPOSCHANGING` — the good one.** `Grausam.cpp` `SubclassProc` (~line 144)
  already subclasses the game WndProc and `Grausam.cpp` `FindGameWindow()` (~line 61) already resolves the HWND
  (`EnumWindows` + same PID + largest visible). Patching `WINDOWPOS.x/y` **before the move happens**
  is flicker-free and the game never notices. Any "detect it moved, move it back" scheme flickers and
  fights the game's own repositioning — which is the user-visible "it just snaps back" symptom.
- **(b) Low-frequency watchdog — the backstop.** ~4-5 Hz worker; if
  `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target`, `SetWindowPos`. Structurally
  **identical to the Solide / Hemmung / Laufen write-on-drift re-assert workers** — copy the shape.
  Covers paths (a) can't see (game switches mode via the swapchain, not via `SetWindowPos`).
- **(c) Hook `IDXGISwapChain::SetFullscreenState` — the real fix for exclusive fullscreen.** MSDN is
  explicit: `pTarget` **is** the output selector; NULL means "DXGI guesses from window placement",
  and on alt-enter **NULL is the only option DXGI has**. So for a true-exclusive-fullscreen game
  (a)+(b) are palliative — the game's next `SetFullscreenState(TRUE, NULL)` re-guesses. Substituting
  the user's chosen `IDXGIOutput*` is the cure. MinHook is already vendored (Stark/Grausam), but
  `Lugner_Dxgi.cpp` is a **pure export forwarder (asm thunks), not a
  swapchain vtable hook** — this is entirely new work, and per-API (D3D11 / D3D12 / Vulkan separately).

**This feature is not UE-bound (scope decision needed).** `Heiter.cpp` (`ProxyStart`, ~lines 57-86)
shows **proxy mode starts the pipe server immediately with no AOB scan**, and all three mechanisms
above are pure Win32/DXGI with zero UE reflection ⇒ injected via the `dxgi.dll` proxy this would work
in a **Unity** game too. Blocker: every UI panel currently assumes UE init succeeded, so a non-UE
process shows a wall of errors. Either accept "only this one card works, everything else is red" or
build a minimal non-UE mode — decide before advertising it as a capability.

**Try the no-code per-engine fixes first** (this class of game is rare; don't pre-build):

- **Unity:** `HKCU\Software\<Company>\<Product>` → **`UnitySelectMonitor`** (0-based), and the
  documented **`-adapter N`** launch arg. ⚠ *Engine-specific*: in **UE**, `-adapter` selects the **GPU
  adapter** and does nothing for monitor choice — the two engines are not interchangeable here, and
  `-adapter` is widely mis-recommended for UE.
- **UE:** `-windowed -WinX= -WinY= -ResX= -ResY=` (Steam launch options) or the same keys in
  `%LOCALAPPDATA%\<Game>\Saved\Config\Windows\GameUserSettings.ini` — subject to the 4.16+ recentre bug.
- **Engine-agnostic:** disable the unwanted display before launch (MultiMonitorTool / `DisplaySwitch`),
  re-enable after. 100% effective against "always picks output 0" games.
- **Why "set it as primary" fails:** enumeration order comes from the adapter's output connectors
  (`EnumDisplayMonitors` / DXGI output order); Windows exposes **no** way to reorder it, and the
  primary flag doesn't change it. That is why physically re-ordering the DP cables is the only clean
  non-tool fix.

**Prior art — check before building.** Special K already does this (Window Management X/Y offset,
retained across launches). For one or two games it is the faster answer. Our differentiators: the
zero-flicker (a) that Special K lacks, integration with the existing UI, and the (c) DXGI path —
Special K's own multi-display borderless-fullscreen limitation is still open (SpecialKO/SpecialK#87).

| Phase | Scope | Effort | Risk |
|---|---|---|---|
| **P1** | (a) `WM_WINDOWPOSCHANGING` + (b) watchdog + `EnumDisplayMonitors` listing + 2 pipe cmds (`list_monitors` / `set_game_monitor`) + one Teleport card. Borderless/windowed only | **M** | low |
| **P2** | (c) `SetFullscreenState` hook — covers exclusive fullscreen | **M-L** | med — swapchain vtable hooks read as overlay behaviour to some anti-cheat; per-graphics-API work |
| **P3** | Minimal non-UE-mode UI boundary (unlocks Unity/other engines) | **M** | med |

**Naming:** take **Böse** (barrier/guard) from the [naming-convention.md](naming-convention.md) roster —
the module's job is *holding the window in place*, a barrier, not a transfer (so not `Zart`, and
teleport semantics stay with Wirbel).

**Recommendation:** spend ten minutes on `UnitySelectMonitor` / `-adapter N` against the actual
offending game first. If that sticks, park this entirely — P1+P2 is M-L of work for a handful of
games. If it doesn't stick *and* more than one such game is on hand, P1 alone is cheap: it reuses
Grausam's subclass and Solide's re-assert shape, leaving only monitor enumeration and the
`WM_WINDOWPOSCHANGING` branch as genuinely new code.

*Parent: Grausam foreground-lock infrastructure (dev-log builds ~1950-1984;
project-foreground-lock-grausam). Sibling evaluation of the Schlacht see-through and Hemmung
time-control evals above.*

-----

## 4th proxy DLL — winmm.dll — ✅ SHIPPED build 2317 (as a SLOT, not for coverage)

**Built on the slot-contention trigger, not the coverage one.** The n=24 census below stands: winmm
and dxgi both cover 100% and winmm reaches exactly zero games dxgi misses, so there was never a
coverage case. What justifies it is the other half of that finding — **a proxy only works if its
filename is free.** `dxgi.dll` is the name ReShade and many mod loaders take; `version.dll` is
likewise often occupied (P3R ships one). With both taken the only remaining choice was dinput8 at
2/24. winmm is the spare universally-viable slot.

**Generated, not hand-written** (`scripts/gen_proxy_forwarders.py winmm`): 180 exports across
`Lugner_Winmm.cpp` / `.asm` / `ProxyWinmm.def`. Re-run with `--check` to verify they are current.
**Verified against the real DLL:** 180/180 forwarding exports present, **every ordinal matching
System32 winmm exactly**, zero missing, plus our 60-symbol UE5 ABI — and the proxy does **not**
import winmm itself, which the build-2301 prerequisite is what makes possible.

*Kept below: the census that says don't build it for coverage, and the trap that had to be fixed
first. Both still govern any FIFTH flavour.*

**⚠ The earlier n=7 recommendation was wrong, and it is worth knowing why.** That sample silently
included **non-UE games** — Nioh3 (Team Ninja), Crimson Desert (BlackSpace), Atelier Yumia (KT) —
because it globbed a Steam library rather than gating on UE markers. Nioh3 was the single row that
made winmm look uniquely valuable ("imports neither dxgi nor version"), and it is not a UE game at
all, so it never bore on this decision.

**Measured — every installed UE game, n=24 (`scripts/analysis/scan_proxy_imports.py`):**

| Module | Coverage | Note |
|---|---|---|
| **dxgi.dll** | **24/24 (100%)** | every UE game, static or delay import |
| **winmm.dll** | **24/24 (100%)** | identical set — **adds nothing over dxgi** |
| d3d11 / d3d12 | 24/24 | (not hijack candidates; sanity check that the scan saw real games) |
| dsound.dll | 23/24 | |
| xinput1_3 | 17/24 | |
| version.dll | 7/24 | **but see below — this number does NOT measure version's viability** |
| dinput8.dll | 2/24 | genuinely weak |

**Games importing none of {version, dinput8, dxgi}: 0.** The current three already reach everything.

- **The version.dll 7/24 is not a coverage figure.** Per `ProxyImportAnalyzer`'s own class remarks,
  version.dll is loaded *dynamically* by almost every Windows process, so its absence from an import
  table says nothing about whether the proxy works. It remains the safe universal default; the 29%
  is only how often it happens to be a *static* import.
- **KnownDLLs verified on this host** (Win11 26200): `winmm` / `dsound` / `dxgi` / `version` /
  `dinput8` are **all absent** ⇒ all app-dir-hijackable. `IMM32` / `MSVCRT` / `gdiplus` / `SHCORE` /
  `PSAPI` / `SHLWAPI` **are** listed ⇒ permanently non-viable, exclude from any future selection.
- **Export counts (measured):** version 17 · dinput8 6 · dxgi 20 · **winmm 181 (180 named)** ·
  dsound 12. If winmm is ever built: do **not** hand-write it — **generate** the `.def` + trampoline
  `.asm` from the real DLL's export table (and re-generate dxgi's from the same script to kill
  hand-maintenance drift).

**The one surviving trigger: slot contention, not coverage.** A proxy only helps if its filename is
*free*. Two real cases: **ReShade** commonly installs itself as `dxgi.dll` (or `d3d11.dll`), and
**P3R already ships its own `version.dll`** in `Binaries\Win64\`. A user with ReShade on dxgi *and*
something on version has only dinput8 left, which is 2/24. **Build winmm if, and only if, that
combination shows up in practice** — it would then be a genuinely free 100%-coverage slot. Until
then it is M-effort for a case nobody has reported.

**✅ DONE (build 2308) — `ProxyImportAnalyzer` misread modular UE builds.** `ReadProxyImports`
is handed `game.ExePath` only. In a **modular** build (Satisfactory) that exe is a ~264 KB bootstrap
stub and the engine lives in `*-Win64-Shipping.dll` modules, so the analyzer sees no dxgi/dinput8 and
the Suggested-proxy column claims `version · default · no dxgi/dinput8` — when a dxgi proxy would in
fact load fine (`D3D12RHI` imports it). **Severity LOW**: the analyzer's design deliberately treats
imports as advisory context that never overrides the version default, so the harm is a misleading
hint string, not a wrong deployment. Fix shape: when the main exe imports none of
`{dxgi, d3d11, d3d12}`, union in the imports of the sibling `*-Win64-Shipping.dll` modules — the same
fallback `scan_proxy_imports.py` uses. **Shipped:** `ImportsNone` + a pure `Merge` OR on
`ProxyImportInfo`, with the file-walking half in `ProxyDeployService` so the analyzer stays OS-free.
Measured at **30 ms** for Satisfactory's 182 modules; monolithic games unaffected (0 ms, fallback
never triggers). +5 tests.

**✅ CLEARED (build 2301) — the BLOCKER: we called winmm ourselves, from the shared object library.**
Kept here because it is the single most important thing to understand before touching this idea, and
because the same trap applies to any future proxy whose API we also *consume*.

`Mimic.cpp` raises the timer resolution for the CE-mailbox poll thread
(`timeBeginPeriod(kPollIntervalMs)` / `timeEndPeriod`), and `Mimic.cpp` is in
**`UE5_COMMON_SOURCES`** — the object library linked into the main DLL *and every proxy*. `Winmm` was
in both `UE5Dumper`'s link list and `PROXY_LINK_LIBS`. Had our proxy *been* `winmm.dll`:

- our static import of `winmm.dll!timeBeginPeriod` would resolve against the module named
  `winmm.dll` in the process — **ourselves** → `Proxy_timeBeginPeriod` → the forwarding pointer.
  Before the real System32 winmm is resolved that pointer is the fallback stub, which **returns 0 —
  and `0` is `TIMERR_NOERROR`**. So it would not crash: it would **silently succeed while doing
  nothing**, `Sleep(1)` degrading to the 15.6 ms tick and CE-mailbox latency getting ~15× worse with
  no error anywhere.
- **Delay-load would not have saved us** (unlike the version proxy, which delay-loads `version.dll`
  purely to break the link-time circularity and never calls into it): a delay-load
  `LoadLibrary("winmm.dll")` from the game directory finds **us** again.
- **No test would have caught it** — `dll_helpers_test` linked `Winmm` directly into the test exe, so
  its `timeBeginPeriod(1)` + `Sleep(1)` latency assert passed regardless of proxy behaviour.

**How it was fixed (build 2301, dev-log 2026-07-23):** `Mimic.cpp` now resolves
`timeBeginPeriod` / `timeEndPeriod` from the **System32** copy by explicit path
(`GetSystemDirectoryW` → `LoadLibraryW(<sys>\winmm.dll)` → `GetProcAddress`), and `Winmm` is gone
from `UE5Dumper`'s link list, `PROXY_LINK_LIBS`, **and** `dll_helpers_test` — whose latency check now
resolves the same way, so it covers the real mechanism. Windows keys loaded modules by full path, so
this always yields the genuine OS winmm even with a same-named proxy of ours mapped. The helper is
proxy-agnostic (no `UE5_PROXY_*` test), satisfying the `UE5_COMMON_SOURCES` invariant — an `#ifdef`
would have violated it. **Verified objectively:** `winmm.dll` no longer appears in the import table
of `UE5Dumper.dll`, any of the three proxies, or the test exe; and the reworked latency check
measures 1.95 ms/sleep through the resolved pointers (vs the ~15.6 ms a silent no-op would give).
**UI side: verified clean** — no `winmm` / `timeBeginPeriod` usage anywhere in `ui/`.

**Prior art — `D:\Github\ZoltDump` already ships a winmm proxy.** Shape to copy: `Eisen.cpp/.h` +
a **generated** `EisenWinmmPtrs.h` (one `extern "C" FARPROC g_pfn_<name>` per export, every one
initialised to `Proxy_Fallback` = `xor rax,rax; ret` so exports missing on older Windows return 0
instead of jumping through null) + `ProxyWinmmTrampoline.asm` (MASM stubs jumping via the pointers) +
`ProxyWinmm.def` (`name = Proxy_name`), with the real DLL loaded by explicit `GetSystemDirectoryW`
path. Its `build.ps1` parameterises the flavour as `-ProxyTarget winmm` rather than our hardcoded
target triple. **Two caveats when copying:** (1) ZoltDump has **no** `timeBeginPeriod` caller of its
own, so its design does not address the self-call trap above — don't assume copying it is enough
(ours is now handled on our side, in Mimic); (2) that same
returns-0 fallback is precisely what makes our self-call fail *silently*. Keep our forwarder named
`Lugner_Winmm.cpp` for internal consistency (`Lugner` owns proxy forwarding here); ZoltDump used
`Eisen` only because it has no `Lügner` equivalent.

**Build-script deltas (measured, so the "compile once" sharing is preserved):**
`dll/CMakeLists.txt` — add `src/Lugner_Winmm.cpp` to `PROXY_SPECIFIC_SOURCES` (gated by
`UE5_PROXY_WINMM_BUILD` like the other two) + one `option(BUILD_PROXY_WINMM)` / `add_library` block
copied from the dxgi one. **`UE5DumperCommon` must not change** — that is what keeps the shared
sources compiling once instead of 5×. `build.ps1` — `-Target` `ValidateSet`, the `$cppTargets` map
(~line 355), the `-DBUILD_PROXY_*=ON` configure string (appears **twice**: ~363 and ~588 — both must
be updated or the test configure silently drops the target), and the dist-copy table (~413-415).
`build.cmd` needs nothing unless we want a `build proxywinmm` alias (it only forwards mode/target).
UI — `Models/ProxyType.cs` (enum + `GetDllName` + `GetDisplayName` + `FromDllName`), `Constants.cs`,
`Services/ProxyImportAnalyzer.cs`, `Services/DumperModuleDetector.cs`, `Services/ProxyDeployService.cs`,
`ViewModels/ProxyDeployViewModel.cs`, `Resources/Strings/en.axaml`, tests.

**Two invariants to hold:** (1) `ProxyImportAnalyzer`'s class remarks deliberately refuse to
auto-escalate away from the version default — **winmm must be an advisory alt, not the new default**,
until a wider sample proves otherwise. (2) A static import only proves the DLL *gets loaded*, not
that the proxy *survives* (anti-tamper, signature checks, an occupied slot); and its absence doesn't
prove the reverse either, since a dynamic `LoadLibrary("dxgi.dll")` also searches the app dir first.

**Rejected alternatives:** `dsound` — cheap (12 exports) but 4/7 and lands in audio init.
`xinput1_4` — 109 functions but only **8 named, the rest ordinal-only**, so the `.def` needs
`NONAME` + ordinal mapping, for only 3/7 coverage.

**Note for whoever adds winmm:** the double-inject guards are now driven off a shared named list
(`Methode.cpp` `kProxyDllNames` / `UE5CEDumper.CT` `UE5_PROXY_DLL_NAMES`) rather than the old
hardcoded `version` + `winmm` pair — add the 4th flavour to **both** or the guard silently stops
covering it (shipped build 2291; the `.CT` half is in-game verified, the `Methode.cpp` CE-plugin half
is only reachable via CE's *Inject && Connect* menu item and has not been exercised).

Effort **M** · Risk low (the prerequisite is done) — **but do not spend it without the slot-contention
trigger above.** The census that would have justified it has now been run (n=24) and says no; re-run
`scripts/analysis/scan_proxy_imports.py` if the installed set changes substantially before
revisiting.

*Parent: the 3-proxy set (version/dinput8/dxgi; project-dll-loading-and-proxies).*

-----

## UE performance counters in the UI — EVALUATED (2026-07-23), tiered

**Verdict: the literal ask — surfacing UE's own `stat` counters — is impossible from an injected
DLL. But the two cheapest tiers are worth more than the literal ask, because they measure the thing
[multipipe-eval.md](multipipe-eval.md) already blames for UI lag and currently has zero telemetry for.**

- **Tier 0 — WON'T DO: UE's `stat` system.** Shipping builds compile with `STATS=0` (even the *Test*
  configuration defines `STATS 0` by default), and the console is **removed from the binary** in
  Shipping, not hidden. Re-enabling needs `FORCE_USE_STATS` and an engine recompile. Unreachable from
  an injected DLL — record as WON'T-DO so it isn't re-litigated.

- **✅ Tier 1 — DONE (build 2308).** New `Sense` module + `get_diagnostics` pipe command + a
  System-tab card. Records per-command dispatch cost (count / total / max / last) at Fern's existing
  `inFlight` chokepoint — which is exactly the head-of-line window — and reports `busy_percent`, the
  fraction of wall-clock a dispatcher was occupied. **That is the number Phase 1 was missing.**
  Also carries game-thread health from Stark and the GObjects count. *Original note kept below for
  the rationale.*

- **Tier 1 — our own health. Zero new machinery, highest value.**
  [multipipe-eval.md](multipipe-eval.md) already names DLL-side **serial-dispatch head-of-line
  blocking** as the root cause of UI lag and game-thread CPU starvation as the CE-mailbox risk — yet
  neither is measured, so Phase 1 would be decided blind. Free to collect: per-command Fern handling
  time + queue depth; Stark invoke queue depth / timeout count (`invoke_timeout_ms` is already
  reported over the pipe); per-worker tick count + write-on-drift hit rate for Solide / Hemmung /
  Laufen / Solitar / Schlacht; Aura `NumElements` over time (GC/leak indicator). **Linie already
  computes frame-cadence statistics** (per-UFunction fire counts + Welford mean/cv) — it just isn't
  presented as performance. Effort **S-M** · Risk none.

- **✅ Tier 2 — DONE (build 2308).** Working set / private bytes / CPU% / thread + handle counts,
  in the same `get_diagnostics` payload. On demand only (thread count walks a system-wide snapshot).
  CPU% is `-1` until a second sample exists to difference against, and the UI renders that as an em
  dash — "0%" would read as *idle*, which is a different and wrong claim.

- **Tier 3 — real FPS / frame time: hook `IDXGISwapChain::Present`.** The only engine-version-
  independent, accurate source (true frametime, 1% low, pacing, present mode). **Shares its entire
  hook infrastructure with P2 of the output-monitor-pin evaluation above** — these two must be
  decided together and funded once, not twice. Effort **M-L** (joint) · Risk med (overlay-shaped
  behaviour; per-graphics-API work).

- **Tier 3.5 — `GAverageFPS` / `GAverageMS` via AOB.** These are plain engine globals
  (`GAverageFPS = 1000/GAverageMS`), **not** gated by `STATS`, so they survive Shipping, and Himmel's
  128-pattern infrastructure could carry a signature. But it is a per-version/per-compiler signature
  to maintain and yields the engine's *smoothed average*, strictly worse than the Present hook. Keep
  only as the fallback if we decide never to hook DXGI.

- **Tier 4 — reflected time values.** `AWorldSettings::TimeDilation` (Hemmung already reads it) and
  `UWorld::TimeSeconds` / `RealTimeSeconds` / `DeltaTimeSeconds` (not `UPROPERTY` — needs DynOff
  probing). Caveat: `DeltaTimeSeconds` is the **game-thread** delta only (no render/GPU) and is
  polluted by time dilation — usable as context, **not** as an FPS readout.

**Status: Tier 1 + Tier 2 SHIPPED (build 2308). Tier 3 still deferred to the monitor-pin P2
DXGI-hook decision; Tier 0 remains WON'T-DO.**

**Follow-on deliberately NOT built: per-worker tick counters** for Solide / Hemmung / Laufen /
Solitar / Schlacht (tick count + write-on-drift hit rate). That is five modules touched for a number
that does not bear on the dispatch question — and the dispatch question is the one that blocked a
decision. Worth doing if a re-assert worker is ever suspected of burning game-thread time. Effort
**S-M** · Risk low.

**✅ Automatic PERF records — DONE (build 2320).** `Services/DiagnosticsProbe.cs` brackets **Copy CE
XML / Copy CE Field / Value Scan (First & Next) / Snapshot capture** with two `get_diagnostics`
snapshots and logs the delta as a `PERF` line in the `view` log. Better than the manual measurement
session it replaces: a deliberate test only covers the scenario somebody thought of, and only if they
remembered to reset first — this accumulates evidence from real use.

**✅ ANSWERED (2026-07-23, build 2324) — and the answer is "don't build Phase 1".** Measured on
Elliot (UE 5.4) + SEED (UE 4.27), 24,178 dispatches across 5 real Copy CE XML / Copy CE Field runs.
Full table and reasoning in [multipipe-eval.md](multipipe-eval.md) §10.

- **Dispatcher busy 29.8%** — idle ~70% of wall-clock, and the ratio holds (22-31%) across
  operations from 2.6 ms to 5.4 s. Non-blocking dispatch can only recover a slice of the busy 30%,
  and only if something were queued behind it — in a single-user export nothing is.
- **Worst SINGLE dispatch: 14.3 ms** out of 24,178. Phase 1's premise is a long-blocking command
  holding the read loop; no such command exists here.
- Phase 1 was already **shipped and reverted once** (build 1840) and a correct version needs
  overlapped/async pipe I/O. Not a trade worth making for this.

**The real lever is CALL COUNT.** `walk_instance` is 100% of dispatcher cost in every row, and one
Copy CE XML issued **20,357** of them: **0.088 ms in the DLL vs 0.208 ms of round-trip overhead —
2.4x the work is overhead.** Batching it at the established ~200/call chunk (as
`search_properties_batch` / `walk_class_batch` already do) would collapse 24,178 round-trips to
~121. **✅ SHIPPED build 2329 — `walk_instance_batch`.** The measurement said dll 27-30% / **ipc 59-73%** /
ui 0-10%, i.e. per-call round-trip overhead roughly 2x the actual walk, so the calls were collapsed
(chunk ~200). Built to the `walk_class_batch` precedent with all three safety layers: a DLL handler
that is a trivial loop over the single-call path, a shared serialiser/deserialiser pair, and an
equivalence test comparing both paths field-for-field. The CE export now walks breadth-first per
level. A failed batch — or a short/long reply, which would otherwise mis-pair results with addresses
— replays the chunk as single calls.

**✅ DONE + MEASURED (build 2335): 1.71x faster.** Copy CE XML on SEED went **5,893 -> 3,437 ms**,
dispatches **22,522 -> 1,355**, IPC **3,532 -> 1,278 ms**. `top:` names `walk_instance_batch`.
(Build 2329 had batched the wrong loop - the calls come from the STRUCT tree, not the
object-pointer drilldown; fixed with a breadth-first `PrefetchStructTreeAsync` feeding the
unchanged depth-first emit, since that emit's order IS the exported field order.)

**The 2.4-3.5x projection was wrong, and usefully so - IPC is not purely per-round-trip.** At the
old 0.157 ms/call, 1,355 calls should have cost ~212 ms of IPC; they cost **1,278 ms**. So of the
original 3,532 ms, ~2,253 ms was fixed per-round-trip cost (removed) and **~1,066 ms is
payload-proportional** (untouchable by batching - the same bytes still cross). `ui` rose 610 -> 653
ms for the same reason. Full table in [multipipe-eval.md](multipipe-eval.md) section 10.5.

**Next lever, if anyone wants more: BYTES, not messages.** Remaining 3,437 ms = dll 1,506 (real
work) + ipc 1,278 (mostly payload) + ui 653 (parse). Trimming fields the CE export never reads would
hit the payload-proportional IPC *and* the parse cost together. **Unquantified** - nobody has
measured what fraction of a `walk_instance` payload the export actually consumes; measure that
before committing. Note also that raising the batch chunk would achieve nothing: average batch size
is ~16.6 (fan-out-limited), not near the 200 cap.

*Parent: multipipe-eval.md Phase 1 (non-blocking dispatch) needs Tier 1 to be decidable; Linie
(dev-log build 2156) already holds the cadence half.*

-----

## Speculative — pick if the active plan finishes ahead of schedule

Not yet committed to:

- **Invoke history / favorites panel** — auto-record (target, args, result) per
  invocation; one-click re-fire.
- **Dry-run-first invoke** — for never-called functions, invoke with zero/sentinel params
  first to detect a crash before committing real args.
- **CE table builder** — bundle selected pointer entries + AA scripts into a single `.ct`,
  auto-grouped by category (broader than the build-760 Interesting Funcs/Props batch).
- **Global hotkey binding** for shortlisted functions ("give 1000 gold" on Ctrl+G).
- **Property freeze — Route A (docs only)** — reuse CE XML/CSX export to land a pointer
  chain, user manually ticks Freeze in CE. Works today, no code; tradeoff is the chain
  binds to one resolved instance (breaks on respawn). Keep for one-shot static-singleton
  freezes so users don't have to wait for Route B.
