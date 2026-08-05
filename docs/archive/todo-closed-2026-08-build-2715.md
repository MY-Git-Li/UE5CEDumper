# todo.md — closed sections archived 2026-08-05 (build 2715)

Moved out of [`../todo.md`](../todo.md) when it passed 250 KB. These sections are **closed**:
every item shipped, was refuted, or was explicitly downgraded. **Nothing was edited, only moved.**
Open work stays in the live `todo.md`.

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
  `Capture_DisconnectMidStream_DoesNotSaveUsablePartial`; 2526 green.
  > **✅ LIVE-VERIFIED 2026-07-23 (Elliot, the real kill).** Snapshot #1 created 22:43:48, 12 chunks
  > in, chunk `offset 90112 / total 356407` (25%) sent and never answered because the GAME WAS CLOSED
  > → `Pipe: ReadLine returned null (disconnected)` → **`SnapshotStore: deleted snapshot #1 (reclaimed
  > disk)`** at 22:44:03. No usable partial survived, the UI did not wedge, and it shut down cleanly
  > afterwards. This is exactly the H1 scenario, executed for real rather than simulated.
  *Delete this row after the audit batch is merged to main.* *Parent: audit-2026-07-14-findings §H1.*

- **[✅ ALL MEDIUMs DONE — 1 HIGH + 10 MED shipped on `dev`]** — the entire
  audit-#3 HIGH+MEDIUM set is fixed. Remaining audit work = the **13 LOW** batch (below) + optional/cosmetic
  items. **In-game verification status is NOT tracked here** — it lives per-item under
  [§ Pending live-game verification](#pending-live-game-verification-verify-only--no-code); this block
  is about what shipped, that one is about what has been proven. Done-notes for the DLL cluster:
  > **✅ DONE — M5 + M2 enable-recovery leak** (SHIPPED commit `61e1f7f`, build 2189, **needs in-game verify**).
  > `Tot::RequestShutdown()` at the TOP of `UE5_Shutdown` + every module's `StartWorker*` gated on
  > `Tot::ShutdownRequested()` (single spawn chokepoint) → no worker revives in the shutdown window; cleared
  > by `Fern::Start`. **Adversarially verified** (5 lenses: no deadlock / lock-order / M3↔M5 / M4↔M5
  > regression; `EnqueueInvoke` gates on Stark's hook flag not `g_shutdown` so the M3 un-hide survives). The
  > same pass caught + fixed a leak in the M1/M2 enable-recovery (un-responsive re-enable orphaned the
  > leftover). **⚠️ EXERCISED at last (Solarpunk, 2026-07-25) — and it revealed a DIFFERENT crash, now
  > FIXED (build 2389).** Leaving See-through ON and closing the game fail-fasted the DLL (`0xc0000409`).
  > A WER minidump showed the fault was on OUR worker thread (pure `version.dll` stack): the per-tick
  > invoke sizes `std::vector(fi.parmsSize)` with no upper bound, so a garbage UFunction ParmsSize read
  > during the game's shutdown throws `bad_alloc`, which escapes the unguarded `WorkerLoop` →
  > `std::terminate`. Fixed by capping ParmsSize in `FindFuncByName` + a `try/catch` around the worker
  > tick, in **Schlacht AND Dunste** (the Fly twin). **Note also confirmed:** `UE5_Shutdown` is NOT
  > called on a game-close (`DllMain(DETACH)` is a no-op, no proxy-graceful-exit hook), so `PipeServer:
  > Stopped` never logs and the shutdown-window worker-revive gate itself is still unproven — but the
  > actual risk it was meant to cover (a worker misbehaving at close) is now handled by the crash fix.
  > **✅ crash fix LIVE-VERIFIED (build 2389, DEBUG build, 2026-07-25):** re-ran the repro (See-through +
  > a Time re-assert worker both live → close game) → no crash / no dump / no event-log error; the 2384
  > run produced all three. *Delete the shutdown-gate half after a real game-close is shown to leave no
  > worker running; the crash half is done + verified.*
  > **✅ DONE + LIVE-VERIFIED — M1 / M2 / M3** (SHIPPED commit `0f6f6e0`, build 2188). All Schlacht:
  > disable joins the worker *before* snapshot/restore (M1); an unresponsive game thread keeps the hidden
  > record + recovers it on the next enable instead of discarding it (M2); `SetEnabled(false)` is called from
  > Fern last-client cleanup + `UE5_Shutdown` with a cheap no-op early-out (M3).
  > **M1 verified (Elliot 2026-07-23):** `SeeThrough: worker stopped` is logged BEFORE
  > `SeeThrough: disabled (1 restored)` — join, then restore, and the hidden actor really came back.
  > **M3 verified (Elliot 2026-07-24):** the session sent **one** `seethrough_set` (the enable) and never a
  > disable, yet `SeeThrough: worker stopped` fired at 09:51:30.235 — the same instant as the second
  > `Client disconnected`. That disable came from the last-client cleanup, which is exactly M3.
  > **M2 half-verified (same run):** the unresponsive branch fired for real —
  > `disabled but 1 actor(s) remain hidden (game thread unresponsive)` — so the record is KEPT rather than
  > discarded. The other half is no longer a user-visible risk: build 2364's deferred restore (see the
  > leftover row below) un-hides automatically once the game thread resumes, so it no longer waits on a
  > later enable. *Delete after the batch merges to main.*
  > **✅ DONE — M4** (SHIPPED commit `7edea28`, build 2187, **needs in-game verify**). `Tot::MarkBackgroundWorker()`
  > thread-local marks each re-assert worker (Solide/Hemmung/Laufen/Solitar/Dunste/Schlacht) so `Tot::Requested()`
  > returns `g_shutdown`-only on those threads → workers no longer freeze on the per-command cancel latch while
  > a pipe command still honours it. Did NOT reset `g_perCommand` on disconnect (would regress the
  > orphaned-scan abort). **Exercised but not conclusively verified (Elliot 2026-07-24):** the UI
  > disconnected with a Hemmung dual-lane hold (world 0.5 + pawn 2.0) and a Laufen jump multiplier live, so
  > the per-command cancel was tripped with re-assert workers running — and nothing errored. But the DLL log
  > ends at the disconnect, so "the workers kept re-asserting afterwards" is not directly shown. To close it:
  > disconnect with a hold on, then look at the GAME (does the hold still apply?) or reconnect and read
  > `get_time_state`. *Delete after batch merged + in-game verified.*
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

- **Audit #3 DLL batch — what the Elliot 2026-07-23 22:19-22:28 session DID exercise** —
  The user drove the checklist. Commands seen: `seethrough_set` on→off, `set_time_dilation` x4
  (global 0.5 + pawn 2.0, twice), `set_foreground_lock` on, `pe_profile_start/stop/get` + a second
  start, `get_current_target` x2, `get_related_objects` x3, `find_path_from_gworld`. Results:
  > **✅ M1 verified with evidence.** `SeeThrough: worker stopped` is logged BEFORE
  > `SeeThrough: disabled (1 restored)` — the disable joins the worker first, then restores, and the
  > one hidden actor was genuinely un-hidden. Enable→disable window 29.3 s at the 100 ms tick.
  > **✅ Disconnect with holds active.** Both sessions ended with a clean two-lane
  > `Client disconnected`, with a Hemmung dual-lane hold, the Grausam foreground lock, and a running
  > Linie recording all still active — the M4 worker-cancel-latch and the deliberate
  > "holds persist across disconnect" behaviour, with **zero errors** in any category.
  > **⛔ Still NOT exercised: M5** — the game was never closed (no `PipeServer: Stopped` /
  > `UE5_Shutdown` in this session), so the shutdown-window worker-revive gate is still unproven.
  > Also untouched: Solide force-field (L2/L3/L4), Laufen, Solitar, Dunste.
  > **The session surfaced two NEW defects** (both fixed below, both need an in-game re-check).
  *Parent: audit-2026-07-14-findings; log review 2026-07-23.*

- **NEW (Elliot 2026-07-24) — "See-through OFF" could leave an actor invisible with no hint why; UI now
  says so. The DEEPER fix is still open.** Effort: **M** · Risk: med.
  Switching See-through off while the game thread is paused cannot un-hide anything: the DLL keeps the
  record (M2) and warns, and the actors stay invisible until a later enable restores them. **This is not an
  edge case — it is the default path.** `Stark::IsGameThreadResponsive()` is driven by ProcessEvent
  fire times, and UE throttles a backgrounded window, so *clicking in the UI to switch the feature off (or
  to disconnect) is itself what pauses the game thread.* Live: the 2026-07-24 disconnect left exactly one
  actor hidden.
  > **Half-fixed (build 2362):** the leftover count was already on the wire (`hidden_count`), the UI just
  > ignored it and said "See-through OFF." It now reports *"OFF — but N actor(s) are still hidden because
  > the game thread is paused… focus the game, then toggle See-through on and off once"* in both the status
  > line and the card, with a test. The user is no longer left guessing.

  > **✅ FULLY FIXED (build 2364) — (c) deferred restore + (d) cross-link.**
  > **(c)** A disable that cannot un-hide now hands off to a short-lived `PendingRestoreLoop` that polls
  > `Stark::IsGameThreadResponsive()` every 250 ms and restores the moment the thread comes back — i.e.
  > the instant the user clicks into the game, which is exactly when they would have noticed. It is a
  > proper member of the worker family: `Tot::MarkBackgroundWorker()` (M4), spawn gated on
  > `Tot::ShutdownRequested()` (M5), joined by `StopWorker()`, and superseded by either direction of
  > `SetEnabled` so only one path ever owns `hiddenActors`. Bounded at 5 minutes (a thread that outlives
  > everything is what audit #3 was about; past that the game is realistically closed, which makes the
  > leftover moot) with an explicit give-up WARN.
  > **NOTE — the connect-time drain first proposed as (c) would NOT have worked:** reconnecting means
  > clicking in the UI, which backgrounds the game again, so the drain would fail for the same reason
  > the original disable did. Waiting on the game thread is the only trigger that actually fires.
  > **(d)** The card hint and both messages now say recovery is automatic and point at **Keep Foreground**,
  > which prevents the pause entirely.
  > **This also closes M2's recovery half** — the leftover no longer depends on a user remembering to
  > re-enable.
  *Parent: audit-2026-07-14-findings §M2; log review 2026-07-24.*

- **✅ FIXED (build 2354) — See-Through re-scanned the whole GObjects pool ~5x/second** —
  Effort: **S** · Risk: low. **Found by reading the Elliot log, not by a test.**
  `Schlacht::CollectOccluders` called `UE5_FindInstanceOfClass("KismetSystemLibrary")` **per trace**,
  and that is a full `Aura::FindInstancesByClass` scan which only exits early on a NON-CDO hit — a
  function library has none, so every call walked all **326,363** objects, plus a `FindFuncByName`
  class walk. Measured from the log: **145 of those scans in the 29 s** the feature was on (~5/s).
  Fix: resolve the CDO + `LineTraceSingle` signature ONCE per enable, keyed off a generation counter
  bumped in `SetEnabled(true)` (so a re-enable re-resolves); the failure case is cached too, so a game
  that cooked the function out no longer rescans 10x/s. **Re-check in-game:** enable See-Through on a
  big game and confirm the `only CDO` WARN now appears once per enable instead of continuously — and
  that occluders still hide/restore.
  > **✅ VERIFIED TWICE.** Build 2356 (19 s window): 145 full-pool scans → 1. Build 2361 (2026-07-24, a
  > **105 s** window — 5x longer, so the old code would have logged ~500): still exactly **1**, and
  > See-Through still behaves (`enabled (pierce=1)` → worker → `worker stopped` → `disabled
  > (1 restored)`). The single scan is visible as one
  > `FindInstancesByClass class='KismetSystemLibrary': 1 found, scanned=352851`.
  *Parent: Schlacht build 1991; log review 2026-07-23.*

- **✅ FIXED (build 2354) — the "concurrent first-invoke" WARN fired on EVERY invoke** —
  Effort: **S** · Risk: low. `Frieren::EnsureProcessEventReady` tested `arrivals > 1`, where
  `arrivals` is the all-time invoke ordinal — so every invoke after the very first logged
  `ProcessEvent: concurrent first-invoke #N serialized behind the one-time init (audit #3 race window
  observed but guarded)`. The Elliot session logged **437** of them in four minutes (one per
  See-Through tick) on a run whose own INFO line said **"1 caller(s) arrived before init began"**,
  i.e. there was no contention at all. Beyond the log spam, the diagnostic was worthless because it
  could never be false. Fix: publish the arrival high-water mark inside the `call_once` lambda and
  warn only for `arrivals <= thatMark` — genuine in-flight contention only.
  > **✅ VERIFIED TWICE: 436 WARNs → 0** (build 2356, 552 invokes), and **0 again** on the build-2361
  > run. *Parent: audit #3 ProcessEvent double-install guard; log review 2026-07-23.*

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

-----

## 4th proxy DLL — winmm.dll — ✅ SHIPPED build 2317 (as a SLOT, not for coverage)

**Built on the slot-contention trigger, not the coverage one.** The n=24 census below stands: winmm
and dxgi both cover 100% and winmm reaches exactly zero games dxgi misses, so there was never a
coverage case. What justifies it is the other half of that finding — **a proxy only works if its
filename is free.** `dxgi.dll` is the name ReShade and many mod loaders take; `version.dll` is
likewise a common ASI/mod-loader name (e.g. Ultimate ASI Loader). With both taken the only remaining
choice was dinput8 at 2/24. winmm is the spare universally-viable slot.

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
**ASI / mod loaders** (e.g. Ultimate ASI Loader) commonly install as `version.dll`. A user with
ReShade on dxgi *and* an ASI loader on version has only dinput8 left, which is 2/24. **Build winmm if, and only if, that
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
