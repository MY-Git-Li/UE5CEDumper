#pragma once

// ============================================================
// Fern — 費倫 (芙莉蓮的弟子 — Frieren's Apprentice)
// PipeServer: Named Pipe JSON IPC server (~30 commands)
//
// Multi-connection model (Path A — docs/multipipe-eval.md §9): the server
// accepts up to kMaxPipeInstances clients and serves EACH on its own thread
// with its own handle, fully serial within itself (read→dispatch→write). Two
// threads therefore never touch the same handle, so the synchronous pipe never
// hits the WriteFile-while-ReadFile-parked deadlock that sank the single-handle
// worker-pool attempt. The UI opens TWO connections (interactive + bulk lanes)
// and routes commands; the DLL itself is lane-agnostic — it only supports
// concurrent connections. CE Lua stays on the Mimic mailbox (off-pipe).
// ============================================================

#include <Windows.h>
#include <string>
#include <thread>
#include <atomic>
#include <unordered_map>
#include <mutex>
#include <condition_variable>
#include <memory>
#include <vector>

class Fern {
public:
    ~Fern() { Stop(); }
    bool Start();
    void Stop();
    bool IsClientConnected() const { return m_clientConnected.load(); }

private:
    // Max concurrent client connections. 2 UI lanes (interactive + bulk) + 1
    // slack for reconnect transients. CE uses the Mimic mailbox, not the pipe.
    static constexpr DWORD kMaxPipeInstances = 3;

    // Per-connection state. Each accepted client gets one of these, served by
    // its own HandleConnection thread on its own handle. writeMutex serializes
    // the (single) thread's response writes against this connection's own watch
    // event writes — writes to DIFFERENT connections need no shared lock.
    struct Connection {
        HANDLE             pipe{INVALID_HANDLE_VALUE};
        std::mutex         writeMutex;
        std::atomic<bool>  inFlight{false};   // true only while inside DispatchCommand
        std::atomic<bool>  closed{false};     // CloseHandle done exactly once
    };

    std::thread        m_acceptThread;
    std::atomic<bool>  m_running{false};
    // True for the duration of Stop(). m_running goes false on Stop()'s first
    // line, so it cannot tell Start() "a teardown is still unwinding" — and
    // starting during that window move-assigns onto a joinable thread, which is
    // std::terminate.
    std::atomic<bool>  m_stopping{false};
    std::atomic<bool>  m_clientConnected{false};   // mirror of (!m_conns.empty())

    // Registry of live connections + the instance currently parked in
    // ConnectNamedPipe. Stop() reads it only to decide whether to send a
    // wake-connect; it must NOT close that handle (a synchronous listen instance
    // blocks the close until somebody connects — see Fern::Stop).
    std::vector<std::shared_ptr<Connection>> m_conns;
    HANDLE                  m_listenPipe{INVALID_HANDLE_VALUE};
    std::mutex              m_connMutex;     // guards m_conns + m_listenPipe + m_clientConnected
    std::condition_variable m_connCv;        // notified when a connection thread exits

    // Disconnect monitor (cooperative cancellation). A bulk-lane scan blocks its
    // connection thread inside DispatchCommand, so that thread can't notice the
    // client vanishing. The monitor peeks each in-flight connection every ~200ms
    // and trips per-command cancellation on a broken pipe so the orphaned scan
    // bails. It peeks ONLY while a connection's inFlight flag is set (the thread
    // is then CPU-bound in DispatchCommand, not in ReadFile/WriteFile), so there
    // is no concurrent read/write on the handle.
    std::thread          m_monitorThread;
    void MonitorLoop();

    // Watch entries — per-connection. A watch's event is written to the
    // connection that registered it (owner); on that connection's disconnect,
    // only its watches are stopped (and joined before the Connection is freed,
    // so `owner` stays valid for the watch thread's lifetime).
    struct WatchEntry {
        uintptr_t           addr;
        uint32_t            size;
        uint32_t            interval_ms;
        Connection*         owner{nullptr};
        std::thread         watchThread;
        std::atomic<bool>   active{true};
    };
    std::unordered_map<uintptr_t, std::unique_ptr<WatchEntry>> m_watches;
    std::mutex m_watchMutex;

    // Initial Scan (async trigger_scan for proxy DLL mode)
    struct ScanState {
        std::atomic<bool> running{false};
        std::atomic<int>  phase{0};       // 0=idle, 1..6=scanning, 7=complete
        std::string       statusText;
        std::mutex        statusMutex;
        std::thread       scanThread;
        bool              completed = false;
    };
    ScanState m_scan;
    void RunScan();

    // Extra Scan (user-triggered background rescan for missing pointers)
    struct RescanState {
        std::atomic<bool> running{false};
        std::atomic<int>  phase{0};       // 0=idle, 1=GObjects, 2=GWorld, 3=complete
        std::string       statusText;
        std::mutex        statusMutex;
        uintptr_t         foundGObjects = 0;
        uintptr_t         foundGWorld   = 0;
        const char*       gobjectsMethod = "not_found";
        const char*       gworldMethod   = "not_found";
        std::thread       scanThread;
    };
    RescanState m_rescan;
    void RunRescan(bool scanGObjects, bool scanGWorld);

    void AcceptLoop();
    void HandleConnection(std::shared_ptr<Connection> conn);
    std::string DispatchCommand(const std::shared_ptr<Connection>& conn, const std::string& jsonLine);
    void StartWatch(const std::shared_ptr<Connection>& conn, uintptr_t addr, uint32_t size, uint32_t interval_ms);
    void StopWatch(uintptr_t addr);
    void StopWatchesForConnection(Connection* owner);
    void StopAllWatches();
    void CloseConnOnce(Connection& conn);
    bool WriteLine(Connection& conn, const std::string& line);
    std::string ReadLine(HANDLE pipe);
};
