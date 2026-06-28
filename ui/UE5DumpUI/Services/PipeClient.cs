using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Named Pipe client for communicating with the injected DLL.
/// </summary>
public sealed class PipeClient : IPipeClient
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource _cts = new();
    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ILoggingService _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // A snapshot_chunk response is multi-MB JSON; logging the FULL body cost ~98 s of a
    // ~100 s capture in-game (verified by the parse+pipe split — the "Pipe RX" debug log
    // alone). Cap the logged body so a huge payload is debug-logged as a short prefix +
    // length, never the whole thing.
    private const int MaxLogBodyChars = 1024;
    private static string LogBody(string s) =>
        s.Length <= MaxLogBodyChars ? s : $"{s[..MaxLogBodyChars]}… ({s.Length:N0} chars)";
    private Task? _readLoopTask;

    // Lane label for the Pipe Activity log ("I" interactive / "B" bulk / "" single).
    private readonly string _laneTag;

    public bool IsConnected { get; private set; }
    public event Action<bool>? ConnectionStateChanged;
    public event Action<JsonObject>? EventReceived;
    public event Action<PipeLogEntry>? Activity;

    // id -> (command name, TX tick) so the RX line can show which command it
    // answers and the round-trip ms. Only populated when Activity has a
    // subscriber. Entries are removed on RX / cancel / disconnect.
    private readonly ConcurrentDictionary<int, (string cmd, long tx)> _txMeta = new();

    private static string NowStr() => DateTime.Now.ToString("HH:mm:ss.fff");

    public PipeClient(ILoggingService log, string laneTag = "")
    {
        _log = log;
        _laneTag = laneTag;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        // An unexpected pipe death exits ReadLoop without disposing the stream set
        // (DisconnectAsync early-returns on !IsConnected), so reconnecting here would
        // abandon the old NamedPipeClientStream with its OS handle open until GC
        // finalization. Dispose any leftover set before building a new one.
        CloseStreams();

        // Dispose previous CTS before creating a new one (prevent WaitHandle leak)
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _pipe = new NamedPipeClientStream(".", Constants.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        _log.Info(Constants.LogCatPipe, $"Connecting to pipe: {Constants.PipeName}...");
        await _pipe.ConnectAsync(Constants.PipeConnectTimeoutMs, ct);

        _reader = new StreamReader(_pipe, Encoding.UTF8);
        _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };

        IsConnected = true;
        ConnectionStateChanged?.Invoke(true);
        _log.Info(Constants.LogCatPipe, "Pipe connected");

        _readLoopTask = Task.Run(ReadLoopAsync, _cts.Token);
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected) return;

        _log.Info(Constants.LogCatPipe, "Disconnecting pipe...");
        _cts.Cancel();

        // Complete all pending requests with cancellation
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetCanceled();
        }
        _pending.Clear();
        _txMeta.Clear();

        if (_readLoopTask != null)
        {
            try { await _readLoopTask; } catch { /* expected */ }
        }

        CloseStreams();

        IsConnected = false;
        ConnectionStateChanged?.Invoke(false);
        _log.Info(Constants.LogCatPipe, "Pipe disconnected");
    }

    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct = default)
    {
        // Capture the writer into a local: a concurrent DisconnectAsync nulls the
        // _writer field, and dereferencing the field at write time would throw a
        // NullReferenceException that the IOException/ObjectDisposedException
        // filters below don't catch. A disposed local still throws
        // ObjectDisposedException, which IS filtered.
        var writer = _writer;
        if (!IsConnected || writer == null)
            throw new InvalidOperationException("Not connected to pipe server");

        int id = Interlocked.Increment(ref _nextId);
        request["id"] = id;

        // Activity tail (System tab): record the TX line + remember the command
        // name / start time so the matching RX can show command + round-trip ms.
        if (Activity != null)
        {
            string cmd = request["cmd"]?.GetValue<string>() ?? "?";
            _txMeta[id] = (cmd, Environment.TickCount64);
            Activity.Invoke(new PipeLogEntry { Time = NowStr(), Lane = _laneTag, Dir = "→", Cmd = cmd, Id = id });
        }

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        // Register cancellation
        await using var reg = ct.Register(() => {
            _txMeta.TryRemove(id, out _);
            if (_pending.TryRemove(id, out var removed))
                removed.TrySetCanceled();
        });

        var json = request.ToJsonString();
        _log.Debug(Constants.LogCatPipe, $"Pipe TX: {LogBody(json)}");

        try
        {
            // Serialize writes to prevent interleaved JSON on the pipe
            await _writeLock.WaitAsync(ct);
            try
            {
                await writer.WriteLineAsync(json);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (IOException) when (!IsConnected || _cts.IsCancellationRequested)
        {
            // Pipe closed during write (disconnect in progress) — cancel this request
            _txMeta.TryRemove(id, out _);
            _pending.TryRemove(id, out _);
            throw new OperationCanceledException("Pipe disconnected during send");
        }
        catch (ObjectDisposedException) when (!IsConnected || _cts.IsCancellationRequested)
        {
            _txMeta.TryRemove(id, out _);
            _pending.TryRemove(id, out _);
            throw new OperationCanceledException("Pipe disconnected during send");
        }

        return await tcs.Task;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _reader != null)
            {
                // Telemetry split (snapshot capture profiling): the response line read, the
                // "Pipe RX" debug log of it, and the JSON DOM parse each scale with payload
                // size and were lumped into one "parse+pipe" bucket. Time them separately and
                // inject the ms into the response object so a snapshot_chunk caller can read
                // them back (harmless to every other response).
                var readSw = System.Diagnostics.Stopwatch.StartNew();
                var line = await _reader.ReadLineAsync(_cts.Token);
                readSw.Stop();
                if (line is null)
                {
                    _log.Warn(Constants.LogCatPipe, "Pipe: ReadLine returned null (disconnected)");
                    break;
                }

                var rxLogSw = System.Diagnostics.Stopwatch.StartNew();
                _log.Debug(Constants.LogCatPipe, $"Pipe RX: {LogBody(line)}");
                rxLogSw.Stop();

                try
                {
                    var parseSw = System.Diagnostics.Stopwatch.StartNew();
                    var obj = JsonNode.Parse(line) as JsonObject;
                    parseSw.Stop();
                    if (obj == null) continue;

                    if (obj["event"] != null)
                    {
                        // Push event from DLL
                        Activity?.Invoke(new PipeLogEntry {
                            Time = NowStr(), Lane = _laneTag, Dir = "⚡",
                            Cmd = obj["event"]?.GetValue<string>() ?? "event" });
                        EventReceived?.Invoke(obj);
                    }
                    else
                    {
                        // Response to a request
                        var id = obj["id"]?.GetValue<int>() ?? 0;
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            // Activity tail: pair the RX with its TX (command +
                            // round-trip ms) recorded in _txMeta on send.
                            if (Activity != null)
                            {
                                string cmd = "?";
                                string detail = "";
                                if (_txMeta.TryRemove(id, out var meta))
                                {
                                    cmd = meta.cmd;
                                    long ms = Environment.TickCount64 - meta.tx;
                                    bool ok = obj["ok"]?.GetValue<bool>() ?? true;
                                    detail = ok ? $"{ms} ms" : $"err {ms} ms";
                                }
                                Activity.Invoke(new PipeLogEntry {
                                    Time = NowStr(), Lane = _laneTag, Dir = "←", Cmd = cmd, Id = id, Detail = detail });
                            }
                            obj["_t_read_ms"]  = readSw.ElapsedMilliseconds;
                            obj["_t_rxlog_ms"] = rxLogSw.ElapsedMilliseconds;
                            obj["_t_parse_ms"] = parseSw.ElapsedMilliseconds;
                            tcs.TrySetResult(obj);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _log.Error(Constants.LogCatPipe, $"Pipe: JSON parse error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (IOException) { /* expected when pipe closes during cancellation */ }
        catch (ObjectDisposedException) { /* expected when pipe disposed during read */ }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatPipe, "Pipe: ReadLoop error", ex);
        }
        finally
        {
            // Fail every in-flight request so awaiting callers don't hang forever
            // when the pipe dies unexpectedly (game closed / DLL unloaded). On the
            // deliberate DisconnectAsync path this is a no-op — that method already
            // drains + clears _pending before awaiting this loop. An IOException
            // (vs DisconnectAsync's cancellation) signals a genuine connection loss
            // so callers surface it as an error rather than a silent cancel.
            if (!_pending.IsEmpty)
            {
                foreach (var kvp in _pending)
                    kvp.Value.TrySetException(new IOException("Pipe disconnected"));
                _pending.Clear();
                _txMeta.Clear();
            }

            // If we exit the read loop unexpectedly, mark disconnected
            if (IsConnected)
            {
                IsConnected = false;
                ConnectionStateChanged?.Invoke(false);
            }
        }
    }

    /// <summary>
    /// Tear down the reader/writer/pipe without ever throwing. The reader and writer wrap
    /// the SAME <see cref="_pipe"/>; disposing the reader first closes that shared stream,
    /// so the writer's Dispose() then flushes to a closed pipe and throws
    /// ObjectDisposedException. On the synchronous shutdown path (App ShutdownRequested →
    /// PipeClient.Dispose) that exception was unhandled and FailFast-crashed the process
    /// (observed CTD on exit while connected). Dispose the WRITER first (flush while the pipe
    /// is still open), then the reader, then the pipe — and guard each: a broken pipe at
    /// teardown (game closed / connection dropped) is expected, never fatal.
    /// </summary>
    private void CloseStreams()
    {
        try { _writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _reader?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _pipe?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Cancel all pending requests so callers don't hang
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetCanceled();
        }
        _pending.Clear();
        _txMeta.Clear();

        CloseStreams();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
