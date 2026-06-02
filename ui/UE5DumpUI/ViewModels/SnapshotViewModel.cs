using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

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
    private EngineState? _engineState;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool   _gameOnly = true;
    [ObservableProperty] private string _selectedScope = "NumericNoByte";
    [ObservableProperty] private bool   _isCapturing;
    [ObservableProperty] private double _progress;          // 0..1
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private SnapshotMeta? _selectedSnapshot;
    [ObservableProperty] private string _selectedQuotaLabel = "1 GB";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private string _allGamesText = "";
    [ObservableProperty] private double _usageRatio;        // 0..1 for the bar
    [ObservableProperty] private bool   _showUsageBar = true;

    /// <summary>Capture scope: NumericNoByte (default, excludes 1-byte) or
    /// NumericAll (includes Int8/UInt8 — floods on small values).</summary>
    public IReadOnlyList<string> ScopeOptions { get; } = new[] { "NumericNoByte", "NumericAll" };

    /// <summary>Per-game DB quota presets. "Unlimited" disables eviction.</summary>
    public IReadOnlyList<string> QuotaOptions { get; } =
        new[] { "512 MB", "1 GB", "2 GB", "5 GB", "Unlimited" };

    public ObservableCollection<SnapshotMeta> Snapshots { get; } = new();

    /// <summary>True once connected (engine state available) and not mid-capture.</summary>
    public bool CanCapture => _engineState != null && !IsCapturing;

    private int QuotaMb => _gate?.SnapshotQuotaMb ?? LabelToMb(SelectedQuotaLabel);
    private long QuotaBytes => QuotaMb <= 0 ? 0 : (long)QuotaMb * 1024 * 1024;

    public SnapshotViewModel(IDumpService dump, ISnapshotStore store, ILoggingService log,
                             IExperimentalGate? gate = null)
    {
        _dump = dump;
        _store = store;
        _log = log;
        _gate = gate;
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
        OnPropertyChanged(nameof(CanCapture));
        _ = RefreshAsync();
    }

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(CanCapture));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var list = await _store.ListSnapshotsAsync();
            Snapshots.Clear();
            foreach (var s in list) Snapshots.Add(s);
            await UpdateUsageAsync();
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
            var u = await _store.GetUsageAsync();
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

    [RelayCommand]
    private async Task DeleteAsync(SnapshotMeta? meta)
    {
        if (meta == null || IsCapturing) return;
        try
        {
            await _store.DeleteSnapshotAsync(meta.Id);
            Snapshots.Remove(meta);
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Snapshot: delete failed", ex);
            SetError(ex);
        }
    }

    private static string DefaultLabel()
        => $"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
}
