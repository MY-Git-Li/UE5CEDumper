using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
    private CancellationTokenSource? _cts;        // capture (streaming) op
    private CancellationTokenSource? _diffCts;    // diff (heavy in-memory) op

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool   _gameOnly = true;
    [ObservableProperty] private string _selectedScope = "NumericNoByte";
    [ObservableProperty] private bool   _isCapturing;
    [ObservableProperty] private bool   _isDeleting;
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

    partial void OnDiffAChanged(SnapshotMeta? value)   => OnPropertyChanged(nameof(CanRunDiff));
    partial void OnDiffBChanged(SnapshotMeta? value)   => OnPropertyChanged(nameof(CanRunDiff));
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
            long? keepA = DiffA?.Id, keepB = DiffB?.Id;
            UiCollection.Reset(Snapshots, list,
                () => { SelectedSnapshot = null; DiffA = null; DiffB = null; });
            if (keepA.HasValue) DiffA = Snapshots.FirstOrDefault(s => s.Id == keepA.Value);
            if (keepB.HasValue) DiffB = Snapshots.FirstOrDefault(s => s.Id == keepB.Value);
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
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsCapturing = true;
        CaptureSectionOpen = true;   // force the capture region visible while capturing
        Progress = 0;

        long snapshotId = 0;
        try
        {
            int total = await _dump.BeginSnapshotAsync(dataType, ct);

            var meta = new SnapshotMeta
            {
                Label         = string.IsNullOrWhiteSpace(Label) ? DefaultLabel() : Label.Trim(),
                CapturedAt    = DateTime.UtcNow.ToString("o"),
                PeHash        = engine.PeHash,
                // ModuleBase is ASLR-randomised per launch, so it distinguishes
                // game restarts (sessions) of the same build for cross-session SPC.
                GameSessionId = $"{engine.PeHash}-{engine.ModuleBase}",
                UeVersion     = engine.UEVersion,
                Scope         = dataType,
            };
            snapshotId = await _store.CreateSnapshotAsync(meta, ct);

            int offset = 0, objectCount = 0, fieldCount = 0;
            while (!ct.IsCancellationRequested)
            {
                var chunk = await _dump.SnapshotChunkAsync(
                    dataType, GameOnly, offset, Constants.SnapshotChunkSize, ct);

                if (chunk.Objects.Count > 0)
                {
                    fieldCount += await _store.WriteChunkAsync(snapshotId, chunk.Objects, ct);
                    objectCount += chunk.Objects.Count;
                }

                offset += chunk.Scanned;
                Progress = total > 0 ? Math.Min(1.0, offset / (double)total) : 0;
                StatusText = $"Capturing… {offset:N0}/{total:N0} — {objectCount:N0} objects, {fieldCount:N0} fields";

                if (chunk.Scanned == 0 || offset >= chunk.Total) break;
            }

            await _store.FinalizeSnapshotAsync(snapshotId, objectCount, fieldCount, ct);
            // FIFO eviction: drop oldest snapshots of this game until the DB
            // fits the quota (the just-captured one is always kept).
            int dropped = await _store.EnforceQuotaAsync(QuotaBytes, ct);
            var evicted = dropped > 0 ? $" — dropped {dropped} oldest (quota)" : "";
            StatusText = $"Captured {objectCount:N0} objects, {fieldCount:N0} fields{evicted}";
            Label = "";
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Capture cancelled.";
            if (snapshotId > 0)
            {
                try { await _store.DeleteSnapshotAsync(snapshotId); } catch { /* best effort */ }
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: capture failed", ex);
            SetError(ex);
            StatusText = "Capture failed.";
        }
        finally
        {
            IsCapturing = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
        }
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
    public void CancelPendingWork() => _diffCts?.Cancel();

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
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        NavigateToInstance?.Invoke(row.ObjAddr);
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
        if (meta == null || IsCapturing || IsDeleting) return;
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

    private static string DefaultLabel()
        => $"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

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
