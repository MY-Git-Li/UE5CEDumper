#pragma once

// ============================================================
// Fern — 費倫 (芙莉蓮的弟子 — Frieren's Apprentice)
// PipeServer: Named Pipe JSON IPC server (~30 commands)
// ============================================================

#include <Windows.h>
#include <string>
#include <thread>
#include <atomic>
#include <unordered_map>
#include <mutex>
#include <deque>
#include <condition_variable>

class Fern {
public:
    ~Fern() { Stop(); }
    bool Start();
    void Stop();
    bool IsClientConnected() const { return m_clientConnected.load(); }

    // Push an event to the connected client
    void PushEvent(const std::string& jsonLine);

private:
    std::thread        m_acceptThread;
    std::atomic<bool>  m_running{false};
    std::atomic<bool>  m_clientConnected{false};
    HANDLE             m_pipe{INVALID_HANDLE_VALUE};
    std::mutex         m_pipeMutex;     // Protects m_pipe access across threads
    std::mutex         m_writeMutex;

    // Disconnect monitor (cooperative cancellation, build 936). While a
    // command is in-flight the handler blocks the pipe thread, so it can't
    // notice the client vanishing. The monitor thread peeks the in-flight
    // pipe every ~200ms and requests per-command cancellation on a broken
    // pipe, so an orphaned scan bails and the pipe frees for a reconnect.
    // It only peeks WHILE m_commandInFlight (the handler is then CPU-bound
    // in DispatchCommand, not in ReadFile/WriteFile), so there is no
    // concurrent read/write on the handle.
    std::thread          m_monitorThread;
    std::atomic<bool>    m_commandInFlight{false};
    std::atomic<HANDLE>  m_inflightPipe{INVALID_HANDLE_VALUE};
    void MonitorLoop();

    // ── Heavy-command worker (Phase 1 — docs/multipipe-eval.md §5/§6) ─────────
    // Long commands (full-pool scans, snapshot streaming, value/group scan,
    // ref/path/container finds, invoke) run on this SINGLE worker thread instead
    // of the read loop, so light commands (Live Walker, properties, teleport)
    // are serviced immediately and never queue behind a multi-second command.
    // The worker is concurrency-1 (FIFO): two cache-building scans never run at
    // once, so the Aura class-metadata caches (and GObjects drift) are never
    // raced across commands. The UI already demultiplexes responses by request
    // id, so out-of-order completion is fine. DispatchCommand bakes the id into
    // the response and WriteLine is m_writeMutex-guarded, so worker responses
    // interleave safely with the read thread's light responses + async events.
    struct HeavyJob { std::string line; HANDLE pipe; };
    std::thread             m_heavyThread;
    std::deque<HeavyJob>    m_heavyQueue;
    std::mutex              m_heavyMutex;   // guards m_heavyQueue + m_heavyBusy + m_heavyStop
    std::condition_variable m_heavyCv;      // signals: new job / job done / stop
    bool                    m_heavyBusy = false;
    bool                    m_heavyStop = false;
    void HeavyWorkerLoop();

    // True for fast, pure-memory / cached-session commands that are safe to run
    // inline on the read thread (concurrently with one heavy job). Everything
    // NOT listed is treated as heavy and deferred to the worker — the safe
    // default, so an unclassified/new command can never race a scan.
    static bool IsLightCommand(const std::string& cmd);

    // Watch entries
    struct WatchEntry {
        uintptr_t           addr;
        uint32_t            size;
        uint32_t            interval_ms;
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
    void HandleClient(HANDLE pipe);
    std::string DispatchCommand(const std::string& jsonLine);
    void StartWatch(uintptr_t addr, uint32_t size, uint32_t interval_ms);
    void StopWatch(uintptr_t addr);
    void StopAllWatches();
    bool WriteLine(HANDLE pipe, const std::string& line);
    std::string ReadLine(HANDLE pipe);
};
