# Multi-pipe IPC Evaluation (UI / DLL / CE contention)

> **Status (2026-06-28): Phase 0 + Phase 1 were SHIPPED then REVERTED** after in-game testing
> regressed badly (build 1840). Baseline restored. The analysis below (§1–§6) is still correct;
> only the *implementation* was wrong. **See §8 Postmortem.** A correct Phase 1 needs
> **overlapped (async) pipe I/O** — not attempted yet.
> **TL;DR:** Do **NOT** add more named pipes. The reported lag is **head-of-line blocking on
> the DLL's single serial command loop**. But both attempted fixes regressed: (a) the Phase 0
> scan thread-priority drop **starved scans ~20× when the game is busy**; (b) Phase 1's
> off-thread heavy worker **deadlocks on the synchronous pipe handle** — a `WriteFile` cannot
> proceed while the read thread is parked in `ReadFile` on the same non-overlapped handle. CE
> `.CT` mailbox is already off the pipe and isolated.

This document evaluates the user request: *"should the UI↔DLL and CE-Lua↔DLL IPC use
multiple pipes? classify UI functions; guarantee the CE `.CT` invoke mailbox stays
responsive while the UI does heavy work."*

-----

## 1. Current architecture — two independent channels

### Channel 1 — UI named pipe (`Fern`)

- **One** pipe instance: [`CreateNamedPipeW(..., /*maxInstances=*/1, ...)`](../dll/src/Fern.cpp:496),
  `PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT` (byte-mode, blocking), 64 KB buffers.
- [`HandleClient`](../dll/src/Fern.cpp:610) is a **strictly serial** loop:
  `ReadLine → Tot::ResetPerCommand → DispatchCommand (SYNCHRONOUS, blocks the thread
  for the whole command) → WriteLine`. While one command runs, the pipe thread cannot
  even **read** the next request line.
- A `MonitorLoop` thread peeks the in-flight handle every ~200 ms (only while
  `m_commandInFlight`) to trip `Tot` cooperative-cancel on a mid-command disconnect.
- Background threads (`RunScan`, `RunRescan`, per-address `watch`) run **outside** the
  dispatch loop and report via [`PushEvent`](../dll/src/Fern.cpp:655) — the only async
  write path, serialized with responses through `m_writeMutex`.

### UI client — already a request-id multiplexer (important)

- **One** [`PipeClient`](../ui/UE5DumpUI/App.axaml.cs:53) created once and shared by every
  panel/VM (manual composition root, no DI container — AOT).
- [`SendAsync`](../ui/UE5DumpUI/Services/PipeClient.cs:98) stamps a monotonic `id`, parks a
  `TaskCompletionSource` in a `ConcurrentDictionary` keyed by `id`, and holds the
  `SemaphoreSlim _writeLock` **only around the byte-write**
  ([acquire L127 → release L134](../ui/UE5DumpUI/Services/PipeClient.cs:127)); the response is
  awaited **after** the lock is released ([`return await tcs.Task`, L149](../ui/UE5DumpUI/Services/PipeClient.cs:149)).
  `ReadLoopAsync` demultiplexes responses back to the waiting TCS by `id`.
- **Consequence:** the client can have many requests outstanding at once. The
  `_writeLock` only prevents interleaved JSON bytes; it does **not** serialize the
  request/response round-trip. The client is **not** the bottleneck.

> ⚠️ A common mis-diagnosis (an automated reviewer made it too): "the UI `_writeLock`
> makes a heavy command block light commands." This is **false** — the lock is released
> before the response await. Verified against the code above.

### Channel 2 — CE Lua / `.CT` mailbox (`Mimic`)

- CE **does not touch the named pipe**. It RPM/WPMs the
  [`g_invokeMailbox`](../dll/src/Mimic.h:198) shared-memory struct; a dedicated DLL poll
  thread reads the `volatile cmd` field every ~1 ms (lock-free) and dispatches.
- Stateful ops (`CMD_INVOKE`, some `CMD_TELEPORT`) block **that poll thread** on
  [`Stark::EnqueueInvoke`](../dll/src/Stark.h:50) `future.wait_for` until the **game thread**
  drains the queue. Pure-memory ops (`CMD_PROTECT` GodMode, `CMD_MOVEMENT`, read-only
  teleport/POV) never touch the game thread.
- The mailbox path shares **no mutex** with the Fern pipe path
  (`Wirbel::s_opMutex` / `Solitar::s_mutex` / `Laufen::s_mutex` / `Stark::s_queueMutex`
  are all independent of `m_pipeMutex` / `m_writeMutex`).

-----

## 2. Root cause of "Live Walker is slow during Snapshot"

**DLL-side head-of-line blocking, full stop.**

Snapshot capture is a stream of `snapshot_chunk` commands (~0.5–2 s each). While one
chunk is inside `DispatchCommand`, the pipe thread will not read the next line — so a
`walk_instance` (a sub-30 ms command) the UI fired immediately just sits in the OS pipe
buffer until the chunk finishes. Because chunks stream continuously, Live Walker is
effectively starved for the whole capture.

It is **not** the UI `_writeLock` (released immediately), **not** GObjects drift, **not**
the CE channel.

-----

## 3. CE mailbox responsiveness — what actually threatens it

Multiple pipes do **nothing** for the CE concern, because CE is not on the pipe.

| CE operation class | Exposure to UI heavy work |
|---|---|
| Pure-memory (GodMode/Solitar, Movement/Laufen, read-only pose/POV/marker) | **Immune.** No pipe, no game thread, independent mutex. |
| Game-thread-routed (`CMD_INVOKE` ProcessEvent; cursor/recall teleports) | Shares the **single `Stark` game-thread queue** with the UI's own game-thread ops (`invoke_function`, game-thread teleports) **and** is sensitive to **CPU starvation**: a parallel full-pool scan pinning every core delays the game tick, so a queued invoke can hit its timeout. |

**The fix is to throttle scan CPU, not to add pipes** (Phase 0), plus the existing
`set_invoke_timeout` safety valve.

-----

## 4. Options considered

| # | Option | Needs concurrent `DispatchCommand`? | Shared-state safety | Effort / Risk | Verdict |
|---|---|---|---|---|---|
| 1 | **Single pipe + non-blocking dispatch** (worker pool, heavy concurrency = 1) | No (1 heavy worker + light inline) | ✅ Safe — light commands read only write-once globals/own session; do not build class caches | L / med | **Recommended** |
| 2 | **N pipe instances** (fast/bulk lanes) | **Yes** | ❌ Two `DispatchCommand` race on `s_classContainerCache`/`s_classRefCache` (double-checked-lock) + `Ubel::WalkClassEx` | L / high | Same benefit, more danger |
| 3 | Per-purpose dedicated pipes | Yes (most) | ❌ Least safe | XL / high | Rejected |
| 4 | Only loosen UI `_writeLock` | No | n/a | S / low | **Non-fix** (bottleneck isn't here) |
| 5 | Move CE further off / confirm isolation | No | n/a | S / low | Already isolated; needs CPU guard only |

**Why Option 1 beats Option 2:** identical concurrency-safety boundary (one heavy op at
a time; light reads run concurrently), but the worker-pool reuses the existing UI `id`
multiplexer and one connection — Option 2 adds a second connection lifecycle, doubles
disconnect/cleanup (`StopAllWatches`/`DropAll` assume a single client), and a single
global `Tot` flag would cross-trip between lanes.

### The concurrency-safety crux (gates Option 2 / Phase 2)

The DLL is **not** safe to run two scan-family commands concurrently today:

- [`s_classContainerCache`](../dll/src/Aura.cpp:1784) and
  [`s_classRefCache`](../dll/src/Aura.cpp:2644) use a check-without-lock → build-outside-lock
  (`Ubel::WalkClassEx`) → insert-under-lock pattern → a data race when two threads miss
  simultaneously.
- GObjects/GNames reads **are** reentrant (write-once globals set at `Init`), but there is
  **no epoch/version stamp**, so two concurrent walks can straddle a live GObjects drift
  inconsistently.
- `Radar` `SessionManager`/`GroupSessionManager` are mutex-protected **per session**, but
  two distinct commands don't share that lock — they still race the Aura class caches.

Therefore any true-parallel design (Option 2/3, or raising the heavy-worker cap above 1)
**must first** make those caches thread-safe + add a GObjects epoch. Defer.

-----

## 5. Recommendation — phased

- **Phase 0 (S, low risk) — protect CE during heavy UI work.** SHIPPED.
  Throttle scan CPU so the game thread always wins scheduling; document CE isolation.
- **Phase 1 (M–L, med risk) — fix the UI contention.** Split DLL dispatch: light/control
  commands run inline on the read thread; heavyweight commands go to **one** worker thread
  and return their `id`-tagged response via the existing write-mutex path, so the read loop
  is free immediately. Requires **per-request cancellation** (today
  [`Tot::ResetPerCommand`](../dll/src/Fern.cpp:616) is global — a light command would clear a
  running heavy scan's cancel state) and generalizing the monitor's in-flight detection to
  "any worker active". The UI needs ~no protocol change (already `id`-muxed).
- **Phase 2 (L, optional — do not pursue speculatively) — true parallel heavy ops.** Only
  if users want two heavy operations at once: first make `s_classContainerCache` /
  `s_classRefCache` / `Ubel` caches thread-safe and add a GObjects epoch, **then** raise the
  heavy-worker cap.

-----

## 6. UI function classification (for the Phase 1 dispatch split)

The dividing rule that keeps the light lane safe to run concurrently with a heavy worker:
a command is **light/control** only if it (a) reads write-once globals or its own session,
and (b) does **not** call `Ubel::WalkClassEx` / build the class-metadata caches.

| Lane | Representative commands | Handling |
|---|---|---|
| **CONTROL / LIGHT** (~47) | `get_object_list`, `get_object`, `walk_instance`, `walk_class`, `read_array_elements`, `query_candidates`, `query_group_candidates`, all `teleport_*`, `set/get_god_mode`, `set/reset_movement_multiplier`, `set/get_mouse_cursor`, `get_current_target`, `get_pointers`, `get_offsets` | run **inline** on the read thread |
| **HEAVY** (~15) | `begin_value_scan`/`refine_value_scan`, `begin_group_scan`/`refine_group_scan`, `snapshot_chunk`, `find_instances`, `find_refs_to_uobject`, `find_path_from_gworld`, `search_properties`/`search_properties_batch`, `list_classes`/`list_all_functions`, `walk_world` (GWorld-null fallback) | dispatch to **1 worker**; `Tot`-cancellable; concurrency = 1 |
| **ASYNC (already)** | `rescan`, `trigger_scan` | begin + `*_status` poll; unchanged |
| **Special** | `invoke_function` (heavy **and** game-thread-routed, up to 30 s) | worker lane; shares the `Stark` queue with CE invoke (second game-thread contention axis) |

-----

## 7. What was shipped (then reverted)

- **Phase 0** (`Aura.cpp`): scan worker threads + the chunk-0 calling thread were dropped to
  `THREAD_PRIORITY_BELOW_NORMAL` during a parallel scan (RAII `ScopedScanPriority`), on top of
  the pre-existing `hardware_concurrency() − 2` cap.
- **Phase 1** (`Fern.cpp`/`.h`): `HandleClient` routed light commands inline and heavy commands
  to a single FIFO worker thread (`HeavyWorkerLoop`), with per-client cancel + disconnect drain.

Both were reverted to the pre-change baseline (the Pipe Activity log, §8, was kept).

-----

## 8. Postmortem — why Phase 0 + Phase 1 were reverted (build 1840, Elliot)

In-game testing surfaced three regressions; root-caused from the DLL pipe log + screenshots.

### 8.1 Phase 1 deadlocks on the synchronous pipe handle (the connect hang)

The server pipe is created **without `FILE_FLAG_OVERLAPPED`** (synchronous handle,
[Fern.cpp:496](../dll/src/Fern.cpp)). On a synchronous handle the Windows I/O manager
**serializes operations**: a pending `ReadFile` holds the file-object lock, so a concurrent
`WriteFile` on the same handle **blocks until the read completes**. Phase 1's whole premise —
the heavy worker writing a response while the read thread is parked in `ReadFile` waiting for
the next command — is therefore impossible: the worker's `WriteFile` hangs until *some new
command* arrives to release the read.

Proof from the log: on the first connect, `trigger_scan #5` was received (11:52:29.515), its
handler ran (`RunScan finished` 30.036), but **the response never reached the UI** — nothing
else was sent, so the read thread stayed parked, the worker's write stayed blocked, and the UI
hung until a manual disconnect. The second connect "worked" only because continuous user
traffic (object-tree loads, a `walk_world` click) kept releasing the read and flushing the
previous write. Classic intermittently-masked deadlock.

(The old serial loop never hit this: the read thread did its own writing, so read and write
were never outstanding at the same time. Watch-thread `PushEvent` writes technically could, but
were rare enough to slip by.)

**A correct Phase 1 requires overlapped/async pipe I/O** (or separate read/write handles), so
the read can be pending while the worker writes independently. That is a real rework of
`ReadLine`/`WriteLine`/`AcceptLoop` and must be tested in-game before shipping.

### 8.2 Phase 0 priority drop starves scans ~20× (snapshot 1–2 min → ~1 hour)

With a game (Elliot) running and saturating cores, `THREAD_PRIORITY_BELOW_NORMAL` scan threads
are preempted by the game's normal-priority threads and barely run. The snapshot crawled to **2%
in 57 s (ETA ~40 min)**. The `cores − 2` *count* headroom was already the right (and sufficient)
throttle; lowering *priority* on top of it was the mistake — it converts "leave some CPU for the
game" into "run only when the game is fully idle", which a live game never is.

### 8.3 Net

Even with the deadlock worked around, Phase 1 wouldn't have helped the snapshot case much: the
shared `m_writeMutex` re-serializes a multi-MB chunk write, and the **UI-side `ReadLoopAsync`
parses each multi-MB chunk single-threaded (~2.4 s each)**, blocking delivery of any interleaved
light response. The dominant snapshot bottlenecks are (a) scan-thread starvation [fixed by the
revert] and (b) UI-side chunk parse — *not* DLL dispatch serialization. Any future work should
target those, and Phase 1 should only be revisited with overlapped I/O.

### 8.4 Kept: Pipe Activity log (System tab)

Unrelated to the deadlock; UI-only. `IPipeClient.Activity` event → `PointerPanelViewModel`
newest-first ring buffer (cap **200**, Pause/Clear), coalesced one `Dispatcher.UIThread.Post`
per burst. Ironically it was the tool that made the deadlock obvious (the missing `←` reply).
