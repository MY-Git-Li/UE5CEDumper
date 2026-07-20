# Design Proposal — Dual-Connection Pipe (eliminate head-of-line blocking)

> Status: **proposal / not yet implemented**. Authored 2026-06-01.
> Motivation: mirror `D:\Github\discrete`'s interactive/bulk split so a long-running
> command (value scan, find-refs, list-all) no longer freezes lightweight UI browsing.

-----

## 1. What "discrete" actually does (and what it does NOT)

discrete is **not** "multiple physical pipes". It is:

- **One** named pipe (`\\.\pipe\DiscreteBfx`), `nMaxInstances = 4` on the server.
- **Two** `NamedPipeClientStream` connections opened by the UI: `_pipe` (interactive) + `_bulk`.
- **Command-level routing** via a `BulkCommands` HashSet in `BackendAdapter.cs` —
  heavy commands go to `_bulk`, everything else to `_pipe`.
- Server side: each accepted client gets its **own detached worker thread**; handlers
  are read-mostly and lock-free across connections.

Goal: `snapshotChunk` (100–500 ms response) on `_bulk` cannot block `getClasses` on `_pipe`.

**We adopt the same model. We do NOT introduce a second pipe name.**

-----

## 2. Our current state (the gap)

| Layer | Current | File |
|-------|---------|------|
| DLL accept | `CreateNamedPipeW(..., nMaxInstances=1, ...)` then **inline** `HandleClient(pipe)` blocks the accept loop until disconnect | `dll/src/Fern.cpp:379` `AcceptLoop` |
| DLL serve | One `while` loop: `ReadLine → DispatchCommand → WriteLine`, fully serial | `dll/src/Fern.cpp:487` `HandleClient` |
| DLL events | `PushEvent` writes to the **single** `m_pipe` handle | `dll/src/Fern.cpp:520` |
| UI client | One `NamedPipeClientStream`; async pending-dict multiplexing but DLL is serial → **HoL blocking** | `ui/.../Services/PipeClient.cs:15` |
| UI routing | All ~41 `SendAsync` calls funnel through one `IPipeClient _pipe` in `DumpService` | `ui/.../Services/DumpService.cs:12` |

**Long commands that currently block the UI:** `begin/refine_value_scan` (seconds),
`find_refs_to_uobject` (cold 200–300 ms, 30 s hard deadline), `list_classes` /
`list_all_functions` / `list_enums` (seconds), `find_instances`, `search_properties*`,
`walk_class_batch`, `invoke_function` (5 s game-thread timeout).

> Note: build 792/793 already **parallelised the internals** of the scan walks, so each
> single command is faster — but it still holds the one connection for its whole duration.

-----

## 3. The concurrency audit (the part that gates safety)

This DLL runs **inside the game process**. A data race here is a game crash, not a unit-test
failure. Before any multi-client change, every piece of shared state on the dispatch path
must have a verdict. Good news: **build 792/793 already did most of the read-path work.**

| Shared state | Owner | Multi-client verdict | Action needed |
|---|---|---|---|
| `ValueScan::SessionManager` | `ValueScan.h:292` | ✅ **Safe.** Singleton, `sessionId`-keyed, global `mu_`. Comment literally says "matches discrete's Phase 27b". Sessions survive across connections. | None |
| Ubel name / enum / walk-class / struct-field caches | `Ubel.cpp` (build 792) | ✅ **Safe.** 5 leaf mutexes (`s_nameCacheMutex` …) guard find/insert; expensive walk runs unlocked. | None |
| DynOff calibration (`CorrectSubclassOffsets`) | Ubel (build 792) | ✅ **Safe.** Double-checked locking on atomic `s_checked`. | None |
| GObjects array reads (Aura walks) | `Aura.cpp` | ✅ **Safe.** Read-only structured walk; build 792 made the parallel version safe, which means the read path is concurrency-clean. | None |
| `m_scan` / `m_rescan` (trigger_scan, rescan) | `Fern.h:45,57` | ✅ **Safe-ish.** Global engine ops guarded by `running` atomic + `statusMutex`. Two clients triggering at once → 2nd rejected. These are *global*, not per-client, by nature. | None (confirm reject path) |
| `m_pipe` (single HANDLE) | `Fern.h:29` | ❌ **Must become per-client.** Used by `HandleClient`, `PushEvent`, and `Stop()`. | **Refactor — §4.1** |
| `m_clientConnected` (single bool) | `Fern.h:28` | ❌ **Must become a count / per-client.** Gates `PushEvent`. | **Refactor — §4.1** |
| `m_watches` + `PushEvent` event routing | `Fern.h:41`, `Fern.cpp:2753,520` | ❌ **Event delivery is ambiguous with >1 client.** A watch must write back to *its own* client's handle. | **Refactor — §4.2** |
| `m_writeMutex` | `Fern.h:31` | ⚠️ **Works but coarse.** Serialises writes across all clients. Harmless (writes are fast) but defeats some parallelism. | Optional: per-client write mutex |
| `invoke_function` → Stark / Mimic mailbox | `Stark.cpp`, `Mimic.cpp` | ⚠️ **Must verify.** Game-thread dispatch is serialised by the game thread itself, but two concurrent posts into the mailbox must be checked. | **Mitigate by routing — §4.3** + verify mailbox is MPSC-safe |
| `write_mem` vs `read_mem` ordering | Macht | ⚠️ **Inherent.** Concurrent write+read of game memory is racy by nature — CE has the identical property. | Accept; route both to interactive |

**Audit conclusion:** the heavy read commands we most want to offload (value scan, find-refs,
list-all, search) are **already concurrency-safe**. The new work is almost entirely in
**Fern's own per-client plumbing** (§4), not in the engine walkers.

-----

## 4. DLL changes (Fern)

### 4.1 Accept loop → multi-instance + per-client thread

Mirror discrete's Phase 22f.

```cpp
// Fern.h
static constexpr DWORD kMaxPipeInstances = 4;   // UI(2) + headroom for CE-Lua / CLI
std::atomic<int> m_activeClients{0};
// remove the single m_pipe / m_clientConnected single-client assumptions
```

```cpp
// Fern::AcceptLoop  (revised shape)
while (m_running.load()) {
    HANDLE pipe = CreateNamedPipeW(
        Grimoire::PIPE_NAME, PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        kMaxPipeInstances,                       // <-- was 1
        Grimoire::PIPE_BUF_SIZE, Grimoire::PIPE_BUF_SIZE, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) { /* backoff */ continue; }

    BOOL ok = ConnectNamedPipe(pipe, nullptr);
    if (!ok && GetLastError() != ERROR_PIPE_CONNECTED) { CloseHandle(pipe); continue; }
    if (!m_running.load()) { CloseHandle(pipe); break; }

    m_activeClients.fetch_add(1, std::memory_order_relaxed);
    std::thread([this, pipe]() {
        try { HandleClient(pipe); } catch (...) { /* never std::terminate the game */ }
        StopWatchesForClient(pipe);          // §4.2
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        m_activeClients.fetch_sub(1, std::memory_order_relaxed);
    }).detach();
    // immediately loop and create the next instance — DO NOT block on HandleClient
}
```

`Stop()` becomes: set `m_running=false`, signal a stop event so blocked `ReadFile`s wake,
join the accept thread, then bounded-wait for `m_activeClients == 0` (discrete waits ≤2 s).

> **Shutdown subtlety:** today `Stop()` closes `m_pipe` to unblock the single `ReadFile`.
> With N detached client threads we need a wake mechanism. Two options: (a) overlapped I/O
> + a stop `HANDLE` event (discrete's approach, cleanest), or (b) `CancelSynchronousIo` on
> each client thread. **Recommend (a)** — it also future-proofs non-blocking reads.

### 4.2 Per-client watch / event routing

`WatchEntry` must remember **which client** registered it, and the watch thread must write
to that client's handle — not a global `m_pipe`.

```cpp
struct WatchEntry {
    HANDLE              clientPipe;   // <-- NEW: the originating connection
    uintptr_t           addr;
    uint32_t            size, interval_ms;
    std::thread         watchThread;
    std::atomic<bool>   active{true};
};
```

- `StartWatch(HANDLE clientPipe, addr, size, interval)` stores `clientPipe`; the watch
  thread calls `WriteLine(clientPipe, evt)` directly. **`PushEvent` / the global `m_pipe`
  field are deleted.**
- Watches keyed by `addr` today → key by `(clientPipe, addr)` (or a per-client watch map)
  so two connections can watch independently and disconnect cleanly.
- On client disconnect: `StopWatchesForClient(clientPipe)` joins only that client's watches.

Since watch is an **interactive** feature (live monitoring while browsing) and the UI only
ever opens watches on the interactive connection (§5), in practice `clientPipe` is always the
interactive handle — but storing it makes the server correct regardless.

### 4.3 invoke_function stays effectively serial

No DLL change required if the UI routes `invoke_function` to the **interactive** connection
(§5): a single connection is serial, so there is never a concurrent invoke. Still:

- **Verify** `Mimic`'s mailbox tolerates a post from one thread while another thread is in a
  read walk (it should — different subsystems — but confirm no shared non-atomic state).
- Keep `write_mem` on the interactive connection too, so writes don't interleave with each
  other across connections.

-----

## 5. UI changes

### 5.1 Two clients, command-level routing

`PipeClient` is already per-instance and reusable. Open two against the **same** pipe name.

Lowest-churn approach: introduce a thin **router** so `DumpService`'s ~41 call sites stay
untouched (they keep calling one `SendAsync`).

```csharp
public sealed class PipeRouter : IPipeClient   // wraps two PipeClients
{
    private readonly IPipeClient _interactive;
    private readonly IPipeClient _bulk;

    private static readonly HashSet<string> BulkCommands = new()
    {
        "begin_value_scan", "refine_value_scan", "end_value_scan",
        "find_refs_to_uobject",
        "list_classes", "list_all_functions", "list_enums",
        "find_instances",
        "search_objects", "search_properties", "search_properties_batch",
        "walk_class_batch",
        // dump/export round-trips that batch many walk_class_batch under the hood
    };

    public Task<JsonObject> SendAsync(JsonObject req, CancellationToken ct = default)
        => (req["cmd"]?.GetValue<string>() is { } c && BulkCommands.Contains(c)
            ? _bulk : _interactive).SendAsync(req, ct);
    // ConnectAsync connects both; IsConnected requires both; events surface from interactive
}
```

Stays on the **interactive** connection (latency-sensitive or must-be-serial):
`init`, `get_pointers`, `get_object*`, `find_object`, `find_by_address`, `walk_class`,
`walk_instance`, `walk_functions`, `read_mem`, `write_mem`, `watch`/`unwatch`,
`get_ce_pointer_info`, `invoke_function`, `read_array_elements`, `walk_datatable_rows`,
all `*_status` polls.

### 5.2 Lifecycle / robustness (mirror discrete)

- `ConnectAsync` connects interactive first, then bulk. **Half-open detection:** if either
  drops, treat the whole adapter as disconnected and tear down both (discrete Phase 14a).
- DI: register `PipeRouter` as `IPipeClient` in `App.axaml.cs`; `DumpService` is unchanged.
- Events (`watch`) only ever arrive on interactive — bulk's read loop still parses generically
  but the UI never subscribes watches on bulk.

-----

## 6. Backward / forward compatibility

Version skew between DLL and UI is real (proxy DLL shipped separately from UI):

| DLL | UI | Behaviour |
|-----|----|-----------|
| multi-instance (new) | dual-conn (new) | full benefit |
| multi-instance (new) | single-conn (old) | works — one client on a 4-instance pipe is identical to today |
| single-instance (old) | dual-conn (new) | **2nd `ConnectAsync` blocks/times out** — UI must detect and fall back to single-conn |

**Mitigation:** UI tries the bulk connect with a short timeout; on failure it logs and routes
**all** commands to interactive (degraded mode = today's behaviour). No hard version handshake
needed, but add a `multi_instance: true` field to the `init` response so the UI can decide
up front instead of probing.

-----

## 7. Recommended phasing (de-risked)

1. **Phase 1 — DLL only.** Multi-instance accept + per-client threads + per-client watch
   routing + overlapped-I/O shutdown. Ship with the **existing single-connection UI**. One
   client on a 4-instance pipe must behave byte-identically to today. This isolates the
   scary in-process concurrency change and lets it bake before any UI benefit rides on it.
2. **Phase 2 — UI dual connection.** Add `PipeRouter` + `BulkCommands` + half-open teardown +
   degraded-mode fallback. Verify the payoff: start a value scan / find-refs on bulk, confirm
   object-tree browsing on interactive stays responsive.
3. **Phase 3 — polish.** `init.multi_instance` flag, per-client write mutex (drop the global
   one) if profiling shows write contention, optional 3rd connection reserved for CE-Lua.

-----

## 8. Testing

- **DLL self-test (`dll_helpers_test`):** the live accept loop isn't unit-testable, but add a
  watch-routing test that two `WatchEntry`s with different `clientPipe` handles target the
  right handle. Reuse the SessionManager tests as the cross-connection-safety proof (already
  green).
- **UI tests:** `PipeRouter` routing table — theory over every command asserting bulk vs
  interactive selection (mirrors discrete's `BulkCommands` test + our existing
  `PickablePointerTypes` contract-test pattern). Degraded-mode fallback test (bulk connect
  fails → all commands route to interactive).
- **Manual verification (the actual goal):** inject into a 1M-object game, kick off
  `begin_value_scan`, and confirm the object tree still expands/scrolls during the scan.
  Without this change it stalls; with it, it shouldn't.

-----

## 9. Effort / risk summary

| Item | Effort | Risk |
|------|--------|------|
| §4.1 multi-instance accept + per-client threads | M | **High** (in-process concurrency; shutdown ordering) |
| §4.2 per-client watch routing | S–M | Med (event delivery correctness) |
| §4.3 invoke/mailbox verify | S | Low (mitigated by routing) |
| §5 UI router + lifecycle | S | Low (localised to one wrapper; DumpService untouched) |
| §6 compat fallback | S | Low |

**Net assessment:** the engine-side concurrency (the genuinely dangerous part) is *already*
handled by build 792/793. The residual risk is concentrated in Fern's accept/shutdown
rewrite (§4.1). Phase 1 isolating that behind an unchanged UI is what makes this safe to ship.
The benefit — non-blocking browse-during-scan — is real but moderate, because the parallelised
scans already shrank the blocking window. Worth doing *if* "UI freezes during a big scan" is a
pain you're hitting in practice; otherwise it's a quality-of-life upgrade, not a blocker.
```
