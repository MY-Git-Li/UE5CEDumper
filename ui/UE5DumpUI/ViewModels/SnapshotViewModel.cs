using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the experimental Snapshot tab. Captures a type-agnostic
/// point-in-time snapshot of every numeric UPROPERTY of every (scoped) UObject
/// by streaming snapshot_chunk pipe calls into the SQLite store, then lists the
/// saved snapshots. Foundation for SPC Query / Class Pivot. See
/// docs/experimental-snapshot-spc-pivot.md Phase A.
/// </summary>
public partial class SnapshotViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ISnapshotStore _store;
    private readonly ILoggingService _log;
    private readonly IExperimentalGate? _gate;
    private readonly IPlatformService? _platform;
    private EngineState? _engineState;
    // Live game session id (PeHash-CreationTime; the process creation time is
    // unique per launch even on no-ASLR games). Diff rows' ObjAddr is the New
    // snapshot's session-local address, so the per-row Live/Addr/GWorld actions
    // are only valid when the New (DiffB) snapshot belongs to the current live
    // session. See EngineState.GameSessionId.
    private string _currentSessionId = "";
    private CancellationTokenSource? _cts;        // capture (streaming) op
    private CancellationTokenSource? _diffCts;    // diff (heavy in-memory) op

    // Capture heartbeat: the chunk loop only refreshes status AFTER each chunk
    // returns, so a slow (deep-container) chunk freezes the display for many
    // seconds and looks hung. A UI-thread timer ticks the elapsed clock + an
    // animated indicator while capturing — the UI thread is free during the
    // chunk's await, so the user always sees life even when the object count is
    // frozen between chunk completions. (build 1212)
    private DispatcherTimer? _captureHeartbeat;
    private DateTime _captureStart;
    private DateTime _lastChunkAt;
    private int _capOffset, _capTotal, _capObjects, _capFields;
    private int _heartbeatPhase;
    // Phase-0 telemetry totals (ms): DLL walk, DLL JSON build, full fetch round-trip
    // (walk+serialize+pipe+parse), and SQLite write. parse = fetch - walk - serialize.
    private long _capWalkMs, _capSerMs, _capFetchMs, _capWriteMs;
    // Split of the old "parse+pipe" bucket (C#-side): pipe read, the "Pipe RX" debug log,
    // the JsonNode DOM parse, and the model-build loop. The remainder (parse+pipe minus
    // these four) ≈ the DLL's .dump() + the pipe transfer.
    private long _capReadMs, _capRxLogMs, _capParseMs, _capBuildMs;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool   _gameOnly = true;
    /// <summary>Auto detect Engine/System noise (default ON). When on, the DLL skips
    /// pure engine/system classes (UI widgets, textures, sounds, Niagara, anim
    /// instances, /Script engine packages) BEFORE they enter the snapshot — saving
    /// capture time + DB size at the source, vs the post-capture Noise Picker that
    /// only filters the finished snapshot. A gameplay guardrail force-keeps
    /// Actor/Pawn/Character/component-derived classes, so a player Pawn's X/Y/Z is
    /// never dropped. Irreversible (re-capture to bring a skipped class back).</summary>
    [ObservableProperty] private bool   _autoSkipNoise = true;
    /// <summary>Native-C (P3, opt-in, default OFF). When on, each captured object also
    /// gets its unmanaged holes (non-UPROPERTY raw bytes) guessed + normalized into
    /// synthetic "&lt;raw@0xNN&gt;" fields, so SPC diff / Class Pivot can track native
    /// (HP/MP) values. Slower + larger capture; Pointer/Padding guesses are dropped.</summary>
    [ObservableProperty] private bool   _includeNativeFields;
    [ObservableProperty] private string _selectedScope = "NumericNoByte";
    [ObservableProperty] private bool   _isCapturing;
    [ObservableProperty] private bool   _isDeleting;
    [ObservableProperty] private bool   _isGWorldAvailable;   // gates the per-row 🌍 button
    [ObservableProperty] private double _progress;          // 0..1
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private SnapshotMeta? _selectedSnapshot;
    [ObservableProperty] private string _selectedQuotaLabel = "1 GB";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private string _allGamesText = "";
    [ObservableProperty] private double _usageRatio;        // 0..1 for the bar
    [ObservableProperty] private bool   _showUsageBar = true;

    // Collapsible sections (E): the capture + compare regions fold away to give
    // the diff grid more room. Capture is force-opened while capturing.
    [ObservableProperty] private bool _captureSectionOpen = true;
    [ObservableProperty] private bool _compareSectionOpen = true;

    // --- Diff (compare two snapshots) ---
    [ObservableProperty] private SnapshotMeta? _diffA;      // old
    [ObservableProperty] private SnapshotMeta? _diffB;      // new
    [ObservableProperty] private string _diffClassFilter = "";
    [ObservableProperty] private string _diffPropFilter = "";
    [ObservableProperty] private string _diffObjectFilter = "";
    [ObservableProperty] private string _selectedDiffDirection = "Any";
    [ObservableProperty] private bool   _isDiffing;
    [ObservableProperty] private string _diffStatusText = "";
    [ObservableProperty] private SnapshotDiffRow? _selectedDiffRow;

    // Global filter (matches across every displayed column) + Old/New numeric
    // range. The range is applied on demand (Apply button) and cleared by Reset;
    // the text/global filters are live.
    [ObservableProperty] private string _diffGlobalFilter = "";
    [ObservableProperty] private string _diffOldMin = "";
    [ObservableProperty] private string _diffOldMax = "";
    [ObservableProperty] private string _diffNewMin = "";
    [ObservableProperty] private string _diffNewMax = "";

    partial void OnDiffGlobalFilterChanged(string value) => ApplyDiffFilter();

    // Distinct candidate values from the last diff, feeding the Class/Field/Object
    // AutoCompleteBox pickers (partial match).
    public ObservableCollection<string> DiffClassOptions  { get; } = new();
    public ObservableCollection<string> DiffFieldOptions  { get; } = new();
    public ObservableCollection<string> DiffObjectOptions { get; } = new();

    // Full unfiltered changed set from the last Run Diff; the grid (DiffRows)
    // is a live client-side filter of this so typing in the filter boxes is
    // instant (no SQL re-query).
    private readonly List<SnapshotDiffRow> _allDiff = new();
    private string _diffSummary = "";

    /// <summary>Raised to open a diff row's object in the Live Walker tab.</summary>
    public event Action<string>? NavigateToInstance;

    /// <summary>Raised to locate a diff row's owning object in the GWorld graph
    /// (reach mode — land on the object, scroll to the changed field). Args:
    /// (objAddr, fieldOffset, fieldName). Same shape as SPC / Value Search.</summary>
    public event Action<string, int, string>? LocateInGWorld;

    /// <summary>Engine-rooted counterpart of <see cref="LocateInGWorld"/> (path search
    /// rooted at the live UGameEngine). Same payload.</summary>
    public event Action<string, int, string>? LocateInGameEngine;

    public IReadOnlyList<string> DiffDirectionOptions { get; } =
        new[] { "Any", "Increased", "Decreased" };

    public ObservableCollection<SnapshotDiffRow> DiffRows { get; } = new();

    // --- N1: per-game class denylist (noise picker) ---
    public ObservableCollection<NoiseRowVm> NoiseRows { get; } = new();
    public ObservableCollection<string> ActiveDenylist { get; } = new();
    [ObservableProperty] private bool _noisePanelOpen;
    private HashSet<string> _excludedClasses = new(StringComparer.Ordinal);

    /// <summary>Two distinct snapshots picked and not mid-diff.</summary>
    public bool CanRunDiff => DiffA != null && DiffB != null && DiffA != DiffB && !IsDiffing && !IsCapturing;

    /// <summary>True only when the New (DiffB) snapshot — whose session-local
    /// ObjAddr the diff rows carry — belongs to the CURRENT live session, so its
    /// addresses are still valid in the running game. Gates the per-row Live / Addr
    /// actions (a cross-session address is stale).</summary>
    public bool CanUseDiffRowActions =>
        !string.IsNullOrEmpty(_currentSessionId) && DiffB?.GameSessionId == _currentSessionId;
    /// <summary>As above, additionally requiring GWorld for the 🌍 button.</summary>
    public bool CanLocateDiffRowInGWorld => CanUseDiffRowActions && IsGWorldAvailable;

    partial void OnDiffAChanged(SnapshotMeta? value)   => OnPropertyChanged(nameof(CanRunDiff));
    partial void OnDiffBChanged(SnapshotMeta? value)
    {
        OnPropertyChanged(nameof(CanRunDiff));
        RaiseDiffRowActionGates();
    }
    partial void OnIsGWorldAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLocateDiffRowInGWorld));
        OnPropertyChanged(nameof(CanLocateGroupRowInGWorld));
    }

    private void RaiseDiffRowActionGates()
    {
        OnPropertyChanged(nameof(CanUseDiffRowActions));
        OnPropertyChanged(nameof(CanLocateDiffRowInGWorld));
        OnPropertyChanged(nameof(CanUseGroupRowActions));
        OnPropertyChanged(nameof(CanLocateGroupRowInGWorld));
    }
    partial void OnIsDiffingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunDiff));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanEditSettings));
    }

    /// <summary>Capture scope / quota / pickers are locked while a capture or diff
    /// is running, so the user can't change settings mid-operation.</summary>
    public bool CanEditSettings => !IsCapturing && !IsDiffing;

    // Filter boxes narrow the loaded result live (client-side).
    partial void OnDiffClassFilterChanged(string value)      => ApplyDiffFilter();
    partial void OnDiffPropFilterChanged(string value)       => ApplyDiffFilter();
    partial void OnDiffObjectFilterChanged(string value)     => ApplyDiffFilter();
    partial void OnSelectedDiffDirectionChanged(string value) => ApplyDiffFilter();

    private void ApplyDiffFilter()
    {
        if (_allDiff.Count == 0 && DiffRows.Count == 0) return;
        string cls = DiffClassFilter.Trim();
        string prop = DiffPropFilter.Trim();
        string obj = DiffObjectFilter.Trim();
        string glob = DiffGlobalFilter.Trim();
        SnapshotDiffDirection? dir = SelectedDiffDirection switch
        {
            "Increased" => SnapshotDiffDirection.Up,
            "Decreased" => SnapshotDiffDirection.Down,
            _           => null,
        };
        double? oldMin = ParseBound(DiffOldMin), oldMax = ParseBound(DiffOldMax);
        double? newMin = ParseBound(DiffNewMin), newMax = ParseBound(DiffNewMax);

        SelectedDiffRow = null;   // detach before clearing the bound results grid
        DiffRows.Clear();
        foreach (var r in _allDiff)
        {
            if (cls.Length  > 0 && r.ClassName.IndexOf(cls, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (prop.Length > 0 && r.PropName.IndexOf(prop, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (obj.Length  > 0 && r.NormPath.IndexOf(obj, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (dir.HasValue && r.Direction != dir.Value) continue;
            if (glob.Length > 0 && !MatchesGlobal(r, glob)) continue;
            if (!WithinRange(r.OldValue, oldMin, oldMax)) continue;
            if (!WithinRange(r.NewValue, newMin, newMax)) continue;
            DiffRows.Add(r);
        }
        DiffStatusText = _diffSummary +
            (DiffRows.Count != _allDiff.Count ? $"  ·  showing {DiffRows.Count:N0}" : "");
    }

    // Global filter: case-insensitive substring across every displayed column.
    private static bool MatchesGlobal(SnapshotDiffRow r, string q) =>
        r.ClassName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.PropName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.NormPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.OldValue.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.NewValue.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static double? ParseBound(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    // A value passes when no bound is set, or it parses numerically and falls
    // within the inclusive bounds. A set bound on a non-numeric value rejects.
    private static bool WithinRange(string rendered, double? min, double? max)
    {
        if (min is null && max is null) return true;
        if (!double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return false;
        if (min is not null && v < min.Value) return false;
        if (max is not null && v > max.Value) return false;
        return true;
    }

    /// <summary>Apply the Old/New numeric range to the loaded diff (button-driven).</summary>
    [RelayCommand]
    private void ApplyDiffRange() => ApplyDiffFilter();

    /// <summary>Clear the Old/New range back to unbounded, then re-filter.</summary>
    [RelayCommand]
    private void ResetDiffRange()
    {
        DiffOldMin = DiffOldMax = DiffNewMin = DiffNewMax = "";
        ApplyDiffFilter();
    }

    // Rebuild the distinct Class/Field/Object picker candidates from the loaded set.
    private void RebuildDiffOptions()
    {
        FillDistinct(DiffClassOptions,  _allDiff.Select(r => r.ClassName));
        FillDistinct(DiffFieldOptions,  _allDiff.Select(r => r.PropName));
        FillDistinct(DiffObjectOptions, _allDiff.Select(r => r.NormPath));
    }

    private static void FillDistinct(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var v in values.Where(s => !string.IsNullOrEmpty(s))
                                 .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            target.Add(v);
    }

    /// <summary>Capture scope: NumericNoByte (default, excludes 1-byte) or
    /// NumericAll (includes Int8/UInt8 — floods on small values).</summary>
    public IReadOnlyList<string> ScopeOptions { get; } = new[] { "NumericNoByte", "NumericAll" };

    /// <summary>Per-game DB quota presets. "Unlimited" disables eviction.</summary>
    public IReadOnlyList<string> QuotaOptions { get; } =
        new[] { "512 MB", "1 GB", "2 GB", "5 GB", "Unlimited" };

    public ObservableCollection<SnapshotMeta> Snapshots { get; } = new();

    /// <summary>True once connected (engine state available) and not mid-capture
    /// or mid-diff.</summary>
    public bool CanCapture => _engineState != null && !IsCapturing && !IsDiffing;

    private int QuotaMb => _gate?.SnapshotQuotaMb ?? LabelToMb(SelectedQuotaLabel);
    private long QuotaBytes => QuotaMb <= 0 ? 0 : (long)QuotaMb * 1024 * 1024;

    public SnapshotViewModel(IDumpService dump, ISnapshotStore store, ILoggingService log,
                             IExperimentalGate? gate = null, IPlatformService? platform = null)
    {
        _dump = dump;
        _store = store;
        _log = log;
        _gate = gate;
        _platform = platform;
        if (_gate != null) _selectedQuotaLabel = MbToLabel(_gate.SnapshotQuotaMb);
        // Don't list yet — the per-game DB isn't known until a game connects.
    }

    partial void OnSelectedQuotaLabelChanged(string value)
    {
        if (_gate != null) _gate.SnapshotQuotaMb = LabelToMb(value);
        ShowUsageBar = QuotaMb > 0;
        _ = UpdateUsageAsync();
    }

    private static int LabelToMb(string label) => label switch
    {
        "512 MB"    => 512,
        "1 GB"      => 1024,
        "2 GB"      => 2048,
        "5 GB"      => 5120,
        "Unlimited" => 0,
        _           => 1024,
    };

    private static string MbToLabel(int mb) => mb switch
    {
        512  => "512 MB",
        1024 => "1 GB",
        2048 => "2 GB",
        5120 => "5 GB",
        0    => "Unlimited",
        _    => "1 GB",
    };

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        IsGWorldAvailable = state.HasGWorld;   // enable the per-row 🌍 button
        _currentSessionId = state.GameSessionId;   // PeHash-CreationTime; matches capture-time GameSessionId
        RaiseDiffRowActionGates();
        // Scope the store to this game's DB, then load its saved snapshots.
        _store.SetActiveGame(state.PeHash);
        LoadDenylistFromStore();
        OnPropertyChanged(nameof(CanCapture));
        _ = RefreshAsync();
    }

    private void LoadDenylistFromStore()
    {
        _excludedClasses = _store.GetClassDenylist(DenylistScope.Diff);
        ActiveDenylist.Clear();
        foreach (var c in _excludedClasses.OrderBy(s => s, StringComparer.Ordinal))
            ActiveDenylist.Add(c);
    }

    partial void OnIsCapturingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanEditSettings));  // lock Scope/GameOnly/Quota/Label during capture
        OnPropertyChanged(nameof(CanRunDiff));        // and the Run Diff button
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var list = await Task.Run(() => _store.ListSnapshotsAsync());
            // Preserve the diff picks across a refresh (a capture finishes -> this
            // runs) by id, since Reset detaches every selection bound to Snapshots.
            long? keepA = DiffA?.Id, keepB = DiffB?.Id, keepG = GroupSnapshot?.Id, keepGC = GroupCompareSnapshot?.Id;
            UiCollection.Reset(Snapshots, list,
                () => { SelectedSnapshot = null; DiffA = null; DiffB = null; GroupSnapshot = null; GroupCompareSnapshot = null; });
            if (keepA.HasValue) DiffA = Snapshots.FirstOrDefault(s => s.Id == keepA.Value);
            if (keepB.HasValue) DiffB = Snapshots.FirstOrDefault(s => s.Id == keepB.Value);
            // Group mode: preserve the primary pick (default to newest) AND the optional
            // compare pick (Mode B) — else a capture-triggered refresh silently drops the
            // compare snapshot and reverts Mode B to Mode A. Don't default the compare:
            // null = Mode A is the intended default.
            if (keepG.HasValue) GroupSnapshot = Snapshots.FirstOrDefault(s => s.Id == keepG.Value);
            GroupSnapshot ??= Snapshots.FirstOrDefault();
            if (keepGC.HasValue) GroupCompareSnapshot = Snapshots.FirstOrDefault(s => s.Id == keepGC.Value);
            await UpdateUsageAsync();
            // Convenience: default the diff pickers to the two newest snapshots
            // (A = older, B = newer) so "Run Diff" is one click after capturing.
            if (Snapshots.Count >= 2 && DiffA == null && DiffB == null)
            {
                DiffB = Snapshots[0];   // newest
                DiffA = Snapshots[1];   // second-newest
            }
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: list failed", ex);
            SetError(ex);
        }
    }

    private async Task UpdateUsageAsync()
    {
        try
        {
            var u = await Task.Run(() => _store.GetUsageAsync());
            ShowUsageBar = QuotaMb > 0;
            if (QuotaMb > 0)
            {
                UsageText  = $"{SnapshotFormat.Bytes(u.GameDbBytes)} / {MbToLabel(QuotaMb)}";
                UsageRatio = QuotaBytes > 0 ? Math.Min(1.0, u.GameDbBytes / (double)QuotaBytes) : 0;
            }
            else
            {
                UsageText  = $"{SnapshotFormat.Bytes(u.GameDbBytes)} (no limit)";
                UsageRatio = 0;
            }
            AllGamesText = $"All games on disk: {SnapshotFormat.Bytes(u.AllGamesBytes)}";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: usage query failed", ex);
        }
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (!CanCapture) return;
        ClearError();

        var engine = _engineState!;
        var dataType = SelectedScope;
        // Lock the capture options ONCE: every snapshot_chunk in the paging loop
        // below must carry the same values, so a mid-capture toggle can't make the
        // chunks inconsistent. (The checkboxes are also disabled via CanEditSettings
        // while capturing — this is belt-and-suspenders.)
        bool gameOnly       = GameOnly;
        bool autoSkipNoise  = AutoSkipNoise;
        bool includeNative  = IncludeNativeFields;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsCapturing = true;
        CaptureSectionOpen = true;   // force the capture region visible while capturing
        Progress = 0;
        _captureStart = DateTime.UtcNow;
        _lastChunkAt  = _captureStart;
        _capOffset = _capTotal = _capObjects = _capFields = 0;
        _capWalkMs = _capSerMs = _capFetchMs = _capWriteMs = 0;
        _capReadMs = _capRxLogMs = _capParseMs = _capBuildMs = 0;
        _heartbeatPhase = 0;

        long snapshotId = 0;
        try
        {
            int total = await _dump.BeginSnapshotAsync(dataType, ct);
            _capTotal = total;
            StartCaptureHeartbeat();

            var meta = new SnapshotMeta
            {
                Label         = string.IsNullOrWhiteSpace(Label) ? DefaultLabel() : Label.Trim(),
                CapturedAt    = DateTime.UtcNow.ToString("o"),
                PeHash        = engine.PeHash,
                // PeHash-CreationTime: the process creation time differs per
                // launch even on no-ASLR games (where ModuleBase is constant),
                // so it distinguishes game restarts (sessions) of the same build
                // for cross-session SPC + the stale-session row-action gate.
                GameSessionId = engine.GameSessionId,
                UeVersion     = engine.UEVersion,
                Scope         = dataType,
            };
            snapshotId = await _store.CreateSnapshotAsync(meta, ct);

            // Producer/consumer: fetch chunk N+1 over the pipe while chunk N is written
            // to SQLite, so the DLL walk and the DB write OVERLAP instead of running
            // back-to-back. The consumer owns one bulk-capture session (single connection,
            // bulk pragmas, commit-every-N). A linked CTS lets a consumer (write) failure
            // stop the producer without deadlocking on the bounded channel, and vice-versa.
            int objectCount = 0, fieldCount = 0;
            await using (var session = await _store.BeginCaptureSessionAsync(ct))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var lct = linked.Token;
                var channel = Channel.CreateBounded<SnapshotChunkResult>(
                    new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });

                var producer = Task.Run(async () =>
                {
                    try
                    {
                        int offset = 0;
                        while (!lct.IsCancellationRequested)
                        {
                            var fetchSw = System.Diagnostics.Stopwatch.StartNew();
                            var chunk = await _dump.SnapshotChunkAsync(
                                dataType, gameOnly, offset, Constants.SnapshotChunkSize,
                                includeNative, autoSkipNoise, lct);
                            fetchSw.Stop();
                            // Phase-0 telemetry: full fetch round-trip + DLL-reported splits
                            // + the C#-side parse+pipe breakdown (read / RX-log / parse / build).
                            _capFetchMs += fetchSw.ElapsedMilliseconds;
                            _capWalkMs  += chunk.WalkMs;
                            _capSerMs   += chunk.SerializeMs;
                            _capReadMs  += chunk.ReadMs;
                            _capRxLogMs += chunk.RxLogMs;
                            _capParseMs += chunk.ParseMs;
                            _capBuildMs += chunk.BuildMs;
                            offset += chunk.Scanned;
                            _capOffset = offset;     // display-only; heartbeat reads on UI thread
                            _lastChunkAt = DateTime.UtcNow;
                            await channel.Writer.WriteAsync(chunk, lct);
                            if (chunk.Scanned == 0 || offset >= chunk.Total) break;
                        }
                    }
                    // Swallow cancellation (real ct OR consumer-triggered) — the consumer
                    // surfaces the real fault; non-cancel exceptions propagate out + fault
                    // the producer task, which WhenAll surfaces.
                    catch (OperationCanceledException) { }
                    finally { channel.Writer.TryComplete(); }
                }, lct);

                var consumer = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var chunk in channel.Reader.ReadAllAsync(lct))
                        {
                            if (chunk.Objects.Count == 0) continue;
                            var writeSw = System.Diagnostics.Stopwatch.StartNew();
                            int n = session.WriteChunk(snapshotId, chunk.Objects, lct);
                            writeSw.Stop();
                            _capWriteMs += writeSw.ElapsedMilliseconds;
                            objectCount += chunk.Objects.Count;
                            fieldCount  += n;
                            _capObjects = objectCount;   // display-only
                            _capFields  = fieldCount;
                        }
                    }
                    catch { linked.Cancel(); throw; }   // unblock the producer on a write failure
                }, lct);

                await Task.WhenAll(producer, consumer);

                StopCaptureHeartbeat();   // stop ticking before the terminal messages
                StatusText = "Finalising…";
                // Incremental pivot counts on the session connection — replaces the ~10s
                // COUNT(DISTINCT) GROUP BY ×2 the lazy build runs (the documented finalize
                // freeze). Dispose then restores pragmas + closes.
                await session.CompleteSnapshotAsync(snapshotId, objectCount, fieldCount, ct);
            }

            // FIFO eviction: drop oldest snapshots of this game until the DB
            // fits the quota (the just-captured one is always kept). EnforceQuotaAsync now
            // skips the checkpoint + VACUUM entirely when the capture stays under quota.
            int dropped = await Task.Run(() => _store.EnforceQuotaAsync(QuotaBytes, ct), ct);
            var evicted = dropped > 0 ? $" — dropped {dropped} oldest (quota)" : "";
            // Phase-0 telemetry: one summary line with the full cost breakdown for
            // before/after comparison. parse+pipe is now split into pipe-read / RX-log /
            // json-parse / model-build; the leftover (other) ≈ the DLL .dump() + pipe transfer.
            long parseMs = Math.Max(0, _capFetchMs - _capWalkMs - _capSerMs);
            long otherMs = Math.Max(0, parseMs - _capReadMs - _capRxLogMs - _capParseMs - _capBuildMs);
            _log.Info(Constants.LogCatView,
                $"Capture done: {objectCount:N0} objects, {fieldCount:N0} fields in " +
                $"{FmtSpan(DateTime.UtcNow - _captureStart)} — walk {_capWalkMs}ms, serialize {_capSerMs}ms, " +
                $"parse+pipe {parseMs}ms [read {_capReadMs} / rxlog {_capRxLogMs} / jsonparse {_capParseMs} / " +
                $"build {_capBuildMs} / other {otherMs}], write {_capWriteMs}ms");
            StatusText = $"Captured {objectCount:N0} objects, {fieldCount:N0} fields{evicted}";
            Label = "";
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StopCaptureHeartbeat();
            StatusText = "Capture cancelled.";
            if (snapshotId > 0)
            {
                // DELETE over the partial snapshot — off the UI thread (sync sqlite).
                try { await Task.Run(() => _store.DeleteSnapshotAsync(snapshotId)); } catch { /* best effort */ }
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            StopCaptureHeartbeat();
            _log.Error(Constants.LogCatView, "Snapshot: capture failed", ex);
            SetError(ex);
            StatusText = "Capture failed.";
        }
        finally
        {
            StopCaptureHeartbeat();   // idempotent — belt-and-suspenders cleanup
            IsCapturing = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Start the capture heartbeat: a UI-thread timer that re-renders the status
    /// line every 400 ms so the elapsed clock + animated dots keep moving even
    /// while a single slow chunk (deep-container objects) is in flight — the
    /// difference between "still working" and "looks hung". The UI thread is free
    /// during the chunk's await, so the tick fires.
    /// </summary>
    private void StartCaptureHeartbeat()
    {
        StopCaptureHeartbeat();
        RenderCaptureStatus();   // paint once immediately
        _captureHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _captureHeartbeat.Tick += OnCaptureHeartbeat;
        _captureHeartbeat.Start();
    }

    private void StopCaptureHeartbeat()
    {
        if (_captureHeartbeat == null) return;
        _captureHeartbeat.Stop();
        _captureHeartbeat.Tick -= OnCaptureHeartbeat;
        _captureHeartbeat = null;
    }

    private void OnCaptureHeartbeat(object? sender, EventArgs e)
    {
        _heartbeatPhase++;
        RenderCaptureStatus();
    }

    /// <summary>
    /// Compose the live "Capturing…" status from the latest chunk counts + a
    /// live elapsed clock. Animated trailing dots move every tick so the line is
    /// visibly alive even when the object count is frozen mid-chunk; if the
    /// current batch has been running a few seconds, say so explicitly — the
    /// "is it hung or working?" reassurance for deep-container objects.
    /// </summary>
    private void RenderCaptureStatus()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _captureStart;
        // Progress is computed HERE (UI thread, via the heartbeat) from the producer's
        // _capOffset/_capTotal, so the background producer never mutates the bound
        // ObservableProperty off-thread.
        Progress = _capTotal > 0 ? Math.Min(1.0, _capOffset / (double)_capTotal) : 0;
        string dots = new string('.', _heartbeatPhase % 4);   // animate 0..3 dots
        string timing = $" · {FmtSpan(elapsed)} elapsed";
        if (Progress >= 0.02)
        {
            // ETA from fraction-done; auto-omits while a slow chunk makes the
            // estimate go stale (remain ≤ 0), leaving just the ticking clock.
            var remain = TimeSpan.FromSeconds(elapsed.TotalSeconds / Progress) - elapsed;
            if (remain > TimeSpan.Zero) timing += $", ~{FmtSpan(remain)} left";
        }
        var batchAge = now - _lastChunkAt;
        string batch = batchAge > TimeSpan.FromSeconds(3)
            ? $" · still scanning this batch ({FmtSpan(batchAge)})"
            : "";
        // {dots,-3}: fixed 3-char slot so the count column doesn't jitter as the
        // dots animate.
        StatusText = $"Capturing{dots,-3} {_capOffset:N0}/{_capTotal:N0} ({Progress:P0}) — " +
                     $"{_capObjects:N0} objects, {_capFields:N0} fields{timing}{CapturePerfSuffix()}{batch}";
    }

    // Phase-0 telemetry: a compact walk/serialize/parse/write breakdown (seconds),
    // shown once the totals are meaningful so we optimise capture on real numbers.
    private string CapturePerfSuffix()
    {
        long parse = _capFetchMs - _capWalkMs - _capSerMs;
        if (parse < 0) parse = 0;
        if (_capFetchMs + _capWriteMs < 500) return "";   // too early to be useful
        return $" · walk {_capWalkMs / 1000.0:0.0}s / ser {_capSerMs / 1000.0:0.0}s / " +
               $"parse {parse / 1000.0:0.0}s / write {_capWriteMs / 1000.0:0.0}s";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Reveal the active game's snapshot DB in the OS file browser.</summary>
    [RelayCommand]
    private async Task OpenDbFolderAsync()
    {
        if (_platform == null) return;
        try { await _platform.RevealInExplorerAsync(_store.DatabasePath); }
        catch (Exception ex) { _log.Error(Constants.LogCatView, "Snapshot: open DB folder failed", ex); }
    }

    /// <summary>Cancel any in-flight heavy diff query — called when the user
    /// switches away from the Snapshot tab so a big in-memory diff doesn't keep
    /// burning CPU while another tab competes (the root cause of the tab-switch
    /// UI hang). The streaming capture op is deliberately NOT cancelled here —
    /// it yields between chunks and the user shouldn't silently lose a capture.</summary>
    public void CancelPendingWork()
    {
        _diffCts?.Cancel();
        // A group match over a big snapshot is the same heavy in-memory op (the whole
        // snapshot, off the UI thread) — abort it on tab-switch / window-close too.
        _groupCts?.Cancel();
    }

    [RelayCommand]
    private async Task RunDiffAsync()
    {
        if (!CanRunDiff) return;
        // Cancel a prior in-flight diff before starting a new one.
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        var cts = _diffCts = new CancellationTokenSource();
        var ct = cts.Token;

        ClearError();
        IsDiffing = true;
        DiffStatusText = "Running diff… (large snapshots can take a while)";
        try
        {
            // Load the full changed set (capped); the filter boxes narrow it
            // client-side afterward so typing is instant. N1: hand the per-game
            // denylist to the store so denied classes are filtered before the cap.
            var filter = new SnapshotDiffFilter
            {
                ExcludedClasses = _excludedClasses.Count > 0 ? _excludedClasses : null,
            };
            // Auto-swap if the user picked Old newer than New: snapshot Id is
            // monotonic (= capture order), so the lower Id is always the older one.
            // Diffing with A older than B keeps the Increased/Decreased directions
            // meaningful instead of silently inverted.
            long idA = DiffA!.Id, idB = DiffB!.Id;
            bool swapped = false;
            if (idA > idB) { (idA, idB) = (idB, idA); swapped = true; }
            var diff = await Task.Run(() => _store.DiffSnapshotsAsync(idA, idB, filter, ct), ct);
            _allDiff.Clear();
            _allDiff.AddRange(diff.Changed);
            RebuildDiffOptions();
            RebuildNoiseRows(diff.TopContributors);
            var trunc = diff.Truncated ? $" (capped at {filter.MaxRows:N0})" : "";
            var swapNote = swapped ? "  ·  (auto-swapped Old/New to capture order)" : "";
            _diffSummary =
                $"{diff.Changed.Count:N0} changed{trunc}  ·  +{diff.AddedCount:N0} added  ·  −{diff.RemovedCount:N0} removed{swapNote}";
            ApplyDiffFilter();
        }
        catch (OperationCanceledException)
        {
            DiffStatusText = "Diff cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: diff failed", ex);
            SetError(ex);
        }
        finally
        {
            // Only clear the busy flag if THIS run is still the current one (a
            // newer run may have superseded us after cancellation).
            if (ReferenceEquals(_diffCts, cts))
            {
                IsDiffing = false;
                _diffCts.Dispose();
                _diffCts = null;
            }
        }
    }

    /// <summary>Open the selected diff row's object in the Live Walker tab.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(SnapshotDiffRow? row)
    {
        // Stale-session gating is enforced on the button (IsEnabled =
        // CanUseDiffRowActions); the command itself stays guard-free for testability.
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        NavigateToInstance?.Invoke(row.ObjAddr);
    }

    /// <summary>Locate this diff row's owning object in the GWorld graph (reach
    /// mode — land on it, scroll to the changed field via PropName, which may be a
    /// deep container path). Only valid for an in-session diff (ObjAddr is the
    /// session-local address from snapshot B); a cross-session address fails the
    /// path search gracefully.</summary>
    [RelayCommand]
    private void LocateRowInGWorld(SnapshotDiffRow? row)
    {
        if (row == null || !IsGWorldAvailable || string.IsNullOrEmpty(row.ObjAddr)) return;
        LocateInGWorld?.Invoke(row.ObjAddr, row.PropOffset, row.PropName);
    }

    /// <summary>Engine-rooted counterpart — not gated on IsGWorldAvailable (engine
    /// availability is independent of GWorld; the DLL reports no_engine via the banner).</summary>
    [RelayCommand]
    private void LocateRowInGameEngine(SnapshotDiffRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        LocateInGameEngine?.Invoke(row.ObjAddr, row.PropOffset, row.PropName);
    }

    /// <summary>Copy the changed field's live address (obj_addr + offset) to the
    /// clipboard — a quick handoff into CE. Valid for an in-session diff.</summary>
    [RelayCommand]
    private async Task CopyDiffAddressAsync(SnapshotDiffRow? row)
    {
        if (row == null || _platform == null) return;
        try
        {
            var hex = row.ObjAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? row.ObjAddr.Substring(2) : row.ObjAddr;
            if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var baseAddr))
            {
                ulong addr = baseAddr + (ulong)row.PropOffset;
                await _platform.CopyToClipboardAsync($"{addr:X}");
                DiffStatusText = $"Copied {addr:X}  ({row.ClassName}::{row.PropName})";
            }
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: copy address failed", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SnapshotMeta? meta)
    {
        if (meta == null || IsCapturing || IsDeleting || IsDiffing) return;
        IsDeleting = true;
        StatusText = "Deleting snapshot…";
        try
        {
            // DELETE over ~1.7M field rows + ExecuteNonQueryAsync runs synchronously
            // under Microsoft.Data.Sqlite, so run it off the UI thread to keep the
            // window responsive.
            await Task.Run(() => _store.DeleteSnapshotAsync(meta.Id));
            // Detach any selection pointing at the row before removing it.
            if (ReferenceEquals(SelectedSnapshot, meta)) SelectedSnapshot = null;
            if (ReferenceEquals(DiffA, meta)) DiffA = null;
            if (ReferenceEquals(DiffB, meta)) DiffB = null;
            Snapshots.Remove(meta);
            await UpdateUsageAsync();
            StatusText = "Snapshot deleted.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: delete failed", ex);
            SetError(ex);
        }
        finally
        {
            IsDeleting = false;
        }
    }

    /// <summary>Delete EVERY snapshot for the active game (truncate + VACUUM).
    /// Off the UI thread; guarded against running during capture/delete.</summary>
    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (IsCapturing || IsDeleting || IsDiffing || Snapshots.Count == 0) return;
        IsDeleting = true;
        StatusText = "Deleting all snapshots…";
        try
        {
            await Task.Run(() => _store.DeleteAllSnapshotsAsync());
            SelectedSnapshot = null; DiffA = null; DiffB = null;
            Snapshots.Clear();
            await UpdateUsageAsync();
            StatusText = "All snapshots deleted.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: delete-all failed", ex);
            SetError(ex);
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private static string DefaultLabel()
        => $"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

    /// <summary>Compact human duration: "8s" / "1m23s" / "1h02m05s".</summary>
    private static string FmtSpan(TimeSpan t) =>
        t.TotalHours >= 1   ? $"{(int)t.TotalHours}h{t.Minutes:D2}m{t.Seconds:D2}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:D2}s"
        : $"{t.Seconds}s";

    // --- N1: noise picker helpers (shared shape with SpcQueryViewModel) ---

    private void RebuildNoiseRows(IReadOnlyList<ClassNoiseRow> top)
    {
        NoiseRows.Clear();
        foreach (var c in top)
        {
            NoiseRows.Add(new NoiseRowVm
            {
                ClassName          = c.ClassName,
                HitCount           = c.HitCount,
                SamplePropsDisplay = c.SamplePropsDisplay,
            });
        }
    }

    [RelayCommand]
    private async Task ApplyNoisePicksAsync()
    {
        // Fresh set, not in-place mutation — an in-flight RunDiffAsync (background
        // thread) may still be reading the captured reference via deny.Contains();
        // HashSet isn't safe for concurrent read+write. See SpcQueryViewModel for
        // the full rationale.
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal);
        bool changed = false;
        foreach (var row in NoiseRows)
            if (row.Picked && updated.Add(row.ClassName)) changed = true;
        if (!changed)
        {
            DiffStatusText = "No noise classes picked — tick one or more rows first.";
            return;
        }
        _store.SetClassDenylist(DenylistScope.Diff, updated);
        LoadDenylistFromStore();
        await RunDiffAsync();
    }

    /// <summary>Untick all noise-picker rows (without touching the persisted
    /// denylist). Distinct from Clear all, which empties the denylist.</summary>
    [RelayCommand]
    private void ResetNoisePicks()
    {
        foreach (var row in NoiseRows) row.Picked = false;
    }

    [RelayCommand]
    private async Task RemoveFromDenylistAsync(string? className)
    {
        if (string.IsNullOrEmpty(className) || !_excludedClasses.Contains(className)) return;
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal);
        updated.Remove(className);
        _store.SetClassDenylist(DenylistScope.Diff, updated);
        LoadDenylistFromStore();
        await RunDiffAsync();
    }

    [RelayCommand]
    private async Task ClearDenylistAsync()
    {
        if (_excludedClasses.Count == 0) return;
        _store.SetClassDenylist(DenylistScope.Diff, new HashSet<string>(StringComparer.Ordinal));
        LoadDenylistFromStore();
        await RunDiffAsync();
    }
}
