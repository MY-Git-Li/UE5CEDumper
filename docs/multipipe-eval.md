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

> Note: §8.3 originally said "Phase 1 should only be revisited with overlapped I/O." That is
> superseded by **§9** — the sister repo `discrete` shows a **two-connection** design that gets
> the lane benefit **without** overlapped I/O, by giving each connection its own handle.

-----

## 9. Phase 1 REDO plan — discrete-style two-connection lane split (Path A; NOT YET BUILT)

This is a **plan for review**, not shipped. It supersedes the reverted single-handle worker-pool.
Modeled on the sister repo **`D:\Github\discrete`**, which runs this in production.

### 9.1 Core idea

The UI opens **TWO** named-pipe client connections to the same server name; the DLL accepts
multiple instances and serves **each connection on its own thread with its own handle**:

- **Interactive lane** — light, fast, read-only browse commands (Live Walker, properties,
  teleport, godmode/movement, pagination over cached sessions).
- **Bulk lane** — heavy, long-running commands (snapshot, value/group scan, find_*,
  search_*, list_*, invoke).

Each connection stays **serial** within itself (read → dispatch → write on one thread, one
handle). The two connections run **independently** on two handles/threads.

**Why this avoids the reverted deadlock (§8.1):** the deadlock was a worker thread doing
`WriteFile` while the read thread was parked in `ReadFile` on the **same** synchronous handle.
Here, each handle is touched by exactly **one** thread doing read→write sequentially — there is
never a second thread writing the same handle. So **synchronous I/O is fine; no overlapped/
`FILE_FLAG_OVERLAPPED` rewrite is needed.** (discrete also uses overlapped I/O, but only because
its CE-Lua bridge opens *additional* bursty pipe clients; our CE uses the Mimic mailbox, not the
pipe, so two UI connections is all we need.)

**Why it's concurrency-safe:** the interactive lane only reads write-once GObjects globals +
Ubel's mutex-guarded caches + global (mutex-guarded) value-scan sessions — it **never** builds
the Aura `s_classContainerCache` / `s_classRefCache` (those live only in scan engines, all on
the bulk lane). The bulk lane runs scans **one at a time** (serial within its connection). So
two cache-builders never run concurrently — the same safety boundary §4 established, now with
NO deadlock. (discrete confirms: handlers are "read-mostly; concurrent invocation worst-case is
a duplicate cache fill, not corruption.")

### 9.2 Reference (discrete — proven template)

- Server: `dll/src/shared/PipeServer.cpp` — `kMaxPipeInstances = 4`, accept-then-detach a
  thread per client, each with its own handle (`FILE_FLAG_OVERLAPPED` there; we keep sync).
- Client: `ui/UnityDumpUI.Core/Services/BackendAdapter.cs` — two `PipeClient`s (`_pipe`
  interactive + `_bulk`), `BulkCommands` set + `PipeFor(cmd)` routing, `ConnectAsync` opens both.
- Per-connection cache guards: `dll/src/shared/ExportAPI.cpp` (mutex-guarded `s_cache`).

### 9.3 DLL changes (`Fern`)

1. **maxInstances**: `CreateNamedPipeW(..., /*maxInstances=*/3, ...)` (2 UI lanes + 1 reconnect
   transient). Keep `PIPE_TYPE_BYTE | PIPE_WAIT`, **no** `FILE_FLAG_OVERLAPPED`.
2. **Thread-per-connection accept loop**: `AcceptLoop` currently creates one instance, calls
   `HandleClient` **inline**, then loops. Change to: create an instance → `ConnectNamedPipe` →
   **spawn a thread** running `HandleClient(conn)` → loop immediately to create the next
   instance. Track the spawned threads for join on `Stop()`.
3. **Per-connection state** (replaces the single `m_pipe` / `m_writeMutex` /
   `m_commandInFlight` / `m_inflightPipe`): introduce a `Connection` struct holding `HANDLE
   pipe`, its **own** `std::mutex writeMutex`, `std::atomic<bool> inFlight`, and
   `std::atomic<bool> inFlightHeavy`. Keep a registry `std::vector<std::shared_ptr<Connection>>`
   under `m_connMutex` (for teardown + the monitor). `WriteLine` takes the connection's own
   `writeMutex` (writes to different handles need no shared lock).
4. **Watch events → owning connection** (the only `PushEvent` path, Fern.cpp:4515): tag each
   `WatchEntry` with its originating `Connection*`. The watch thread writes the event to **that**
   connection (via its `writeMutex`), not a global `m_pipe`. When a connection disconnects, stop
   **only its** watches (key `m_watches` per connection, or filter by owner).
5. **MonitorLoop → per-connection**: iterate the registry; for any connection whose `inFlight`
   handle is broken, trip cancellation. **Cancellation caveat:** `Tot` is global and only the
   bulk lane runs cancellable scans, so trip `Tot::RequestPerCommand()` **only when the broken
   connection had `inFlightHeavy`** — otherwise an interactive-lane disconnect would wrongly
   abort a running bulk scan. (Longer term: per-connection cancel tokens.)
6. **Session cleanup**: `Radar::SessionManager/GroupSessionManager::DropAll()` currently runs on
   the single client's disconnect. Change to drop **only when the last connection disconnects**
   (registry empty) — sessions are bulk-lane-only and the UI is single-instance, so both lanes
   drop together on close. (Per-owner drop is a future refinement if needed.)
7. **`Stop()`**: signal stop, close all registry handles, join all connection threads (plus the
   existing accept/monitor/scan threads), then `DropAll`.

### 9.4 UI changes (`PipeClient` / `DumpService`)

1. Keep `PipeClient` as-is (one connection, id-mux, `_writeLock` around the byte-write). It's
   already correct for a single serial lane.
2. Add a second `PipeClient` and a thin router (mirror discrete's `BackendAdapter`): a
   `BulkCommands` set + `SendAsync` picks the bulk client for those, the interactive client for
   the rest. `ConnectAsync`/`DisconnectAsync` manage both. The **command lane table is §6** (the
   light allowlist → interactive; everything else → bulk).
3. `Pipe Activity` log: tag each entry with its lane (interactive/bulk) so the System-tab tail
   shows the split visibly (one extra column). The `Activity` event already exists per client.
4. AOT: no reflection added — a second `PipeClient` instance + a `HashSet<string>` route are
   trim-safe.

### 9.5 NOT in scope (separate follow-ups)

The §8.3 snapshot bottlenecks are **orthogonal** to the lane split and remain after it:
(a) the bulk-lane multi-MB chunk write still ties up the bulk handle (fine — it's the bulk
lane's job), and (b) the **UI-side single-threaded multi-MB chunk parse (~2.4 s/chunk)**. The
lane split fixes "interactive stays responsive during a scan"; it does **not** speed up the
snapshot itself. If snapshot speed matters, separately switch `SnapshotChunkAsync` to a
streaming `Utf8JsonReader` and/or smaller chunks (already noted in todo).

### 9.6 In-game verification checklist (MANDATORY — Fern has no unit tests)

1. Connect handshake completes with no hang (the §8.1 failure), incl. a fresh `trigger_scan`.
2. Live Walker drill + teleport + godmode stay responsive **while a Snapshot / Value Search
   streams on the bulk lane**.
3. Start a Value Search **mid-Snapshot** — both complete; bulk lane serializes them, interactive
   stays live.
4. Close the UI mid-scan — both connections free cleanly, sessions/watches dropped, game exits.
5. Disconnect only one lane (edge) — the other keeps working; a bulk scan isn't wrongly cancelled
   by an interactive disconnect (the §9.3.5 caveat).
6. CE `.CT` mailbox invoke still works under heavy UI load (it's off-pipe; should be unaffected).
7. Watches (System-tab / address watch) still deliver events to the right (interactive) lane.

### 9.7 Effort / risk / open decisions

- **Effort:** M–L (DLL per-connection refactor is the bulk; UI router is small). **Risk:** med
  — live threading, no Fern unit tests → §9.6 in-game pass is the gate.
- **Open decisions to confirm before building:**
  - maxInstances = 3 vs 2 vs 4 (reconnect transients / future bridge).
  - Session cleanup: "drop on last disconnect" (simple, proposed) vs per-owner tagging.
  - Cancellation: "trip global Tot only for heavy-in-flight connection" (proposed) vs full
    per-connection cancel tokens (cleaner, more work).
  - Whether to also tag Pipe Activity entries by lane now (small, recommended).
