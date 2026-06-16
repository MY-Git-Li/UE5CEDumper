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
/// One selectable snapshot row in the SPC picker: a snapshot plus whether it is
/// part of the query sequence and which directional predicate applies to it.
/// </summary>
public partial class SpcSnapshotPick : ObservableObject
{
    public SnapshotMeta Meta { get; }

    public SpcSnapshotPick(SnapshotMeta meta, IReadOnlyList<string> predicateOptions)
    {
        Meta = meta;
        PredicateOptions = predicateOptions;
    }

    [ObservableProperty] private bool   _isSelected;
    /// <summary>True for the oldest CHECKED snapshot — the baseline. Its predicate
    /// is forced to "Any" and its picker is disabled (there's nothing before it to
    /// compare against). Recomputed by the VM whenever the selection changes.</summary>
    [ObservableProperty] private bool   _isBaseline;
    /// <summary>How this snapshot's value compares to the previous selected one
    /// (display string from <see cref="PredicateOptions"/>). Ignored for the
    /// oldest selected snapshot — that one is always the baseline.</summary>
    [ObservableProperty] private string _selectedPredicate = "Any";

    /// <summary>The predicate picker is editable only for a selected, non-baseline
    /// snapshot.</summary>
    public bool PredicateEnabled => IsSelected && !IsBaseline;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(PredicateEnabled));
        OnPropertyChanged(nameof(AbsEnabled));
    }
    partial void OnIsBaselineChanged(bool value)
    {
        OnPropertyChanged(nameof(PredicateEnabled));
        if (value) SelectedPredicate = "Any";   // the baseline is always "Any"
    }

    public IReadOnlyList<string> PredicateOptions { get; }

    // --- Optional absolute (value-window) predicate, applied IN ADDITION to the
    //     directional one, before the cap. Cuts directional-but-irrelevant noise. ---
    public IReadOnlyList<string> AbsKindOptions { get; } =
        new[] { "(any value)", "Exact", "Between", "≥", "≤" };
    [ObservableProperty] private string _absKind = "(any value)";
    [ObservableProperty] private string _absLow  = "";
    [ObservableProperty] private string _absHigh = "";

    /// <summary>Value boxes are editable only for a selected snapshot.</summary>
    public bool AbsEnabled => IsSelected;
    public bool ShowAbsLow  => AbsKind is "Exact" or "Between" or "≥";
    public bool ShowAbsHigh => AbsKind is "Between" or "≤";

    partial void OnAbsKindChanged(string value)
    {
        OnPropertyChanged(nameof(ShowAbsLow));
        OnPropertyChanged(nameof(ShowAbsHigh));
    }

    /// <summary>Compile this row's UI choice into an absolute predicate.</summary>
    public SpcAbsolutePredicate ToAbsolutePredicate()
    {
        double Lo() => double.TryParse(AbsLow.Trim(),  System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        double Hi() => double.TryParse(AbsHigh.Trim(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        return AbsKind switch
        {
            "Exact"        => new SpcAbsolutePredicate { Kind = SpcAbsoluteKind.Exact,   Low = Lo() },
            "Between"      => new SpcAbsolutePredicate { Kind = SpcAbsoluteKind.Between, Low = Lo(), High = Hi() },
            "≥"       => new SpcAbsolutePredicate { Kind = SpcAbsoluteKind.AtLeast, Low = Lo() },
            "≤"       => new SpcAbsolutePredicate { Kind = SpcAbsoluteKind.AtMost,  High = Hi() },
            _              => new SpcAbsolutePredicate { Kind = SpcAbsoluteKind.None },
        };
    }

    public long   Id         => Meta.Id;
    public string Label      => Meta.Label;
    public string CapturedAt => Meta.CapturedAt;
    /// <summary>Short tail of the game-session id so the user can see at a glance
    /// which snapshots come from different game launches (the cross-session case).</summary>
    public string SessionShort
    {
        get
        {
            var s = Meta.GameSessionId;
            if (string.IsNullOrEmpty(s)) return "";
            int dash = s.LastIndexOf('-');
            return dash >= 0 && dash + 1 < s.Length ? s[(dash + 1)..] : s;
        }
    }
}

/// <summary>
/// ViewModel for the experimental SPC Query tab. Pure C# over the SQLite
/// snapshot corpus — no DLL/pipe. The user selects two or more snapshots (which
/// may span game sessions), assigns a directional predicate to each, picks a
/// join mode, and runs the chain. Generalises CE's "Unknown Initial Value +
/// Increased/Decreased" to N persisted, cross-session snapshots. See
/// docs/experimental-snapshot-spc-pivot.md §"Phase B".
/// </summary>
public partial class SpcQueryViewModel : ViewModelBase
{
    private readonly ISnapshotStore _store;
    private readonly ILoggingService _log;
    private readonly IPlatformService? _platform;
    private EngineState? _engineState;
    private CancellationTokenSource? _queryCts;   // heavy in-memory SPC op

    [ObservableProperty] private string _selectedJoinMode = "Strict";
    [ObservableProperty] private string _classFilter = "";
    [ObservableProperty] private string _propFilter = "";
    [ObservableProperty] private bool   _isQuerying;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private SpcResultRow? _selectedResult;

    /// <summary>Directional predicate options (display strings). v1 is
    /// type-agnostic: directions only, no type/value entry.</summary>
    public IReadOnlyList<string> PredicateOptions { get; } =
        new[] { "Any", "Unchanged", "Changed", "Increased", "Decreased" };

    public IReadOnlyList<string> JoinModeOptions { get; } =
        new[] { "Strict", "Loose", "In-session" };

    /// <summary>Available snapshots (newest first), each pickable into the chain.</summary>
    public ObservableCollection<SpcSnapshotPick> SnapshotPicks { get; } = new();

    public ObservableCollection<SpcResultRow> Results { get; } = new();

    // --- N1: per-game class denylist (noise picker) ---

    /// <summary>Top-N classes ranked by hit count over the last result set, each
    /// row toggleable. Apply pushes the selection into the per-game denylist and
    /// re-runs the query (so the user sees the cleaned result immediately).</summary>
    public ObservableCollection<NoiseRowVm> NoiseRows { get; } = new();

    /// <summary>The currently-active per-game denylist (loaded from the store).
    /// Empty when no game is wired. Display-only — mutations happen via
    /// ApplyNoisePicksAsync.</summary>
    public ObservableCollection<string> ActiveDenylist { get; } = new();
    [ObservableProperty] private bool _noisePanelOpen;

    private HashSet<string> _excludedClasses = new(StringComparer.Ordinal);

    // --- Client-side result filtering (over the last query's full result set) ---
    [ObservableProperty] private string _resultClassFilter  = "";
    [ObservableProperty] private string _resultFieldFilter  = "";
    [ObservableProperty] private string _resultObjectFilter = "";
    [ObservableProperty] private string _resultGlobalFilter = "";
    // Value-sequence range: bounds on the first and last value of the sequence,
    // applied on demand (Apply button) and cleared by Reset.
    [ObservableProperty] private string _seqFirstMin = "";
    [ObservableProperty] private string _seqFirstMax = "";
    [ObservableProperty] private string _seqLastMin = "";
    [ObservableProperty] private string _seqLastMax = "";

    partial void OnResultClassFilterChanged(string value)  => ApplyResultFilter();
    partial void OnResultFieldFilterChanged(string value)  => ApplyResultFilter();
    partial void OnResultObjectFilterChanged(string value) => ApplyResultFilter();
    partial void OnResultGlobalFilterChanged(string value) => ApplyResultFilter();

    /// <summary>Distinct Class/Field/Object candidates from the last result set,
    /// feeding the AutoCompleteBox pickers (partial match).</summary>
    public ObservableCollection<string> ResultClassOptions  { get; } = new();
    public ObservableCollection<string> ResultFieldOptions  { get; } = new();
    public ObservableCollection<string> ResultObjectOptions { get; } = new();

    private readonly List<SpcResultRow> _allResults = new();
    private string _resultSummary = "";

    /// <summary>Raised to open a result row's object in the Live Walker tab.</summary>
    public event Action<string>? NavigateToInstance;

    /// <summary>Raised to locate a result row's owning object within the GWorld
    /// object graph. Payload = (owning object address, changed field byte offset,
    /// field name).</summary>
    public event Action<string, int, string>? LocateInGWorld;

    /// <summary>True when GWorld is available — gates the per-row "Locate in GWorld" button.</summary>
    [ObservableProperty] private bool _isGWorldAvailable;

    public int SelectedCount => SnapshotPicks.Count(p => p.IsSelected);

    /// <summary>At least two snapshots picked and not mid-query.</summary>
    public bool CanRunQuery => SelectedCount >= 2 && !IsQuerying;

    public SpcQueryViewModel(ISnapshotStore store, ILoggingService log,
                             IPlatformService? platform = null)
    {
        _store = store;
        _log = log;
        _platform = platform;
        // Don't list yet — the per-game DB isn't known until a game connects.
    }

    partial void OnIsQueryingChanged(bool value) => OnPropertyChanged(nameof(CanRunQuery));

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        IsGWorldAvailable = state.HasGWorld;
        _store.SetActiveGame(state.PeHash);
        LoadDenylistFromStore();
        _ = RefreshAsync();
    }

    /// <summary>Reload the per-game denylist after SetActiveGame switched the
    /// active DB. Resets the local cache + the bound display collection.</summary>
    private void LoadDenylistFromStore()
    {
        _excludedClasses = _store.GetClassDenylist(DenylistScope.Spc);
        ActiveDenylist.Clear();
        foreach (var c in _excludedClasses.OrderBy(s => s, StringComparer.Ordinal))
            ActiveDenylist.Add(c);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // Off the UI thread (SQLite "*Async" is synchronous) — this also posts
            // the rebuild as a fresh dispatcher work item, avoiding the "cannot
            // change ObservableCollection during a CollectionChanged event" crash.
            var list = await Task.Run(() => _store.ListSnapshotsAsync());
            // Display oldest-first: the baseline (oldest selected) then sits at the
            // top and the predicate chain reads top-to-bottom in time order.
            var ordered = list.OrderBy(m => m.Id).ToList();
            // Preserve current selections/predicates across a refresh so a capture
            // on the Snapshot tab doesn't wipe a half-built query.
            var prev = SnapshotPicks.ToDictionary(p => p.Id, p => (p.IsSelected, p.SelectedPredicate));
            foreach (var p in SnapshotPicks) p.PropertyChanged -= OnPickChanged;

            // Build the fresh picks detached, then swap in one shot.
            var fresh = new List<SpcSnapshotPick>(ordered.Count);
            foreach (var m in ordered)
            {
                var pick = new SpcSnapshotPick(m, PredicateOptions);
                if (prev.TryGetValue(m.Id, out var s))
                {
                    pick.IsSelected = s.IsSelected;
                    pick.SelectedPredicate = s.SelectedPredicate;
                }
                pick.PropertyChanged += OnPickChanged;
                fresh.Add(pick);
            }
            // Convenience: first visit pre-selects the two newest (now at the tail)
            // so a directional query is one predicate edit away.
            if (prev.Count == 0 && fresh.Count >= 2)
            {
                fresh[^1].IsSelected = true;
                fresh[^2].IsSelected = true;
            }
            UiCollection.Reset(SnapshotPicks, fresh, () => { /* no SelectedItem binding */ });
            RecomputeBaselines();
            AutoSelectJoinMode();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanRunQuery));
            UpdateWarning();
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "SPC: list failed", ex);
            SetError(ex);
        }
    }

    private void OnPickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpcSnapshotPick.IsSelected))
        {
            RecomputeBaselines();
            AutoSelectJoinMode();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanRunQuery));
            UpdateWarning();
        }
    }

    // --- Auto join-mode selection ---
    // Same-session snapshots are best joined by gobjects_index (In-session): it
    // tracks each physical object exactly, including transient objects whose
    // normalised path collides (e.g. every //Engine/Transient/Item folds to the
    // same Strict key, so Strict collapses 4 distinct items into 1 mismatched
    // candidate and the predicate fails — the "materials don't show up" bug).
    // Cross-session snapshots have no stable index, so they fall back to Strict.
    // The user can still override the combo manually.
    private bool _joinModeUserOverride;
    private bool _settingJoinModeProgrammatically;

    partial void OnSelectedJoinModeChanged(string value)
    {
        if (!_settingJoinModeProgrammatically) _joinModeUserOverride = true;
    }

    private void AutoSelectJoinMode()
    {
        if (_joinModeUserOverride) return;
        var ticked = SnapshotPicks.Where(p => p.IsSelected).ToList();
        if (ticked.Count < 2) return;
        bool sameSession = ticked.Select(p => p.Meta.GameSessionId).Distinct().Count() == 1;
        var target = sameSession ? "In-session" : "Strict";
        if (SelectedJoinMode != target)
        {
            _settingJoinModeProgrammatically = true;
            SelectedJoinMode = target;
            _settingJoinModeProgrammatically = false;
        }
    }

    /// <summary>Mark the oldest CHECKED snapshot as the baseline (predicate forced
    /// to "Any", picker disabled); everything else is non-baseline.</summary>
    private void RecomputeBaselines()
    {
        var baseline = SnapshotPicks.Where(p => p.IsSelected).OrderBy(p => p.Id).FirstOrDefault();
        foreach (var p in SnapshotPicks) p.IsBaseline = ReferenceEquals(p, baseline);
    }

    /// <summary>A 2-snapshot directional query matches very broadly (a single
    /// up/down step is true for a huge fraction of fields → often the row cap). Warn
    /// the user to add a third snapshot to make the sequence discriminating.</summary>
    private void UpdateWarning()
    {
        int sel = SelectedCount;
        WarningText = sel == 2
            ? "Only 2 snapshots selected: a single direction matches very broadly and often hits the result cap. Add a 3rd snapshot to make the sequence discriminating."
            : "";
    }

    /// <summary>Cancel an in-flight SPC query — called when the user navigates
    /// away from the SPC tab. The in-memory intersection over ~1.8M rows is the
    /// heaviest experimental op; left running while another tab loads, it pegs a
    /// core + allocates GBs (the tab-switch hang the user reported).</summary>
    public void CancelPendingWork() => _queryCts?.Cancel();

    [RelayCommand]
    private async Task RunQueryAsync()
    {
        var selected = SnapshotPicks.Where(p => p.IsSelected)
                                    .OrderBy(p => p.Id)   // oldest -> newest
                                    .ToList();
        if (selected.Count < 2)
        {
            StatusText = "Select at least two snapshots.";
            return;
        }

        // Cancel a prior in-flight query before starting a new one.
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        var cts = _queryCts = new CancellationTokenSource();
        var ct = cts.Token;

        ClearError();
        IsQuerying = true;
        StatusText = "Running SPC query… (intersecting fields across snapshots)";
        SelectedResult = null;   // detach before clearing the bound results grid
        Results.Clear();
        try
        {
            var query = new SpcQuery
            {
                JoinMode      = ParseJoinMode(SelectedJoinMode),
                ClassContains = ClassFilter.Trim(),
                PropContains  = PropFilter.Trim(),
                // N1: hand the current denylist to the store so denylisted classes
                // never enter the candidate dict (cuts memory + match cost both).
                ExcludedClasses = _excludedClasses.Count > 0 ? _excludedClasses : null,
            };
            for (int i = 0; i < selected.Count; i++)
            {
                query.SnapshotIds.Add(selected[i].Id);
                // Index 0 is the baseline; its predicate is forced to Any.
                query.Predicates.Add(i == 0 ? SpcPredicateKind.Any
                                            : ParsePredicate(selected[i].SelectedPredicate));
                // Optional per-snapshot absolute value window (applied before the cap).
                query.AbsolutePredicates.Add(selected[i].ToAbsolutePredicate());
            }

            // Off the UI thread — the N-way self-join over ~1.8M rows would
            // otherwise freeze the window for seconds.
            var res = await Task.Run(() => _store.SpcQueryAsync(query, ct), ct);
            _allResults.Clear();
            _allResults.AddRange(res.Rows);
            RebuildResultOptions();
            RebuildNoiseRows(res.TopContributors);

            var trunc = res.Truncated ? $" (capped at {query.MaxRows:N0})" : "";
            var spanSessions = selected.Select(p => p.Meta.GameSessionId).Distinct().Count();
            var sess = spanSessions > 1 ? $", {spanSessions} sessions" : "";
            _resultSummary = res.Rows.Count == 0
                ? $"No fields match the chain across {selected.Count} snapshots{sess}."
                : $"{res.Rows.Count:N0} match{trunc} across {selected.Count} snapshots{sess} ({SelectedJoinMode}).";
            ApplyResultFilter();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Query cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "SPC: query failed", ex);
            SetError(ex);
            StatusText = "Query failed.";
        }
        finally
        {
            // Clear busy only if THIS run is still current (a newer run may have
            // superseded us after cancellation).
            if (ReferenceEquals(_queryCts, cts))
            {
                IsQuerying = false;
                _queryCts.Dispose();
                _queryCts = null;
            }
        }
    }

    private void ApplyResultFilter()
    {
        if (_allResults.Count == 0 && Results.Count == 0) { StatusText = _resultSummary; return; }
        string cls = ResultClassFilter.Trim();
        string fld = ResultFieldFilter.Trim();
        string obj = ResultObjectFilter.Trim();
        string glob = ResultGlobalFilter.Trim();
        double? fMin = ParseBound(SeqFirstMin), fMax = ParseBound(SeqFirstMax);
        double? lMin = ParseBound(SeqLastMin),  lMax = ParseBound(SeqLastMax);

        SelectedResult = null;   // detach before clearing the bound results grid
        Results.Clear();
        foreach (var r in _allResults)
        {
            if (cls.Length  > 0 && r.ClassName.IndexOf(cls, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (fld.Length  > 0 && r.PropName.IndexOf(fld, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (obj.Length  > 0 && r.NormPath.IndexOf(obj, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (glob.Length > 0 && !MatchesGlobal(r, glob)) continue;
            if (!WithinRange(SeqFirst(r), fMin, fMax)) continue;
            if (!WithinRange(SeqLast(r),  lMin, lMax)) continue;
            Results.Add(r);
        }
        StatusText = _resultSummary +
            (Results.Count != _allResults.Count ? $"  ·  showing {Results.Count:N0}" : "");
    }

    private static string SeqFirst(SpcResultRow r) => r.Values.Count > 0 ? r.Values[0] : "";
    private static string SeqLast(SpcResultRow r)  => r.Values.Count > 0 ? r.Values[^1] : "";

    private static bool MatchesGlobal(SpcResultRow r, string q) =>
        r.ClassName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.PropName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.NormPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.DeclaredType.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.SequenceDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static double? ParseBound(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool WithinRange(string rendered, double? min, double? max)
    {
        if (min is null && max is null) return true;
        if (!double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return false;
        if (min is not null && v < min.Value) return false;
        if (max is not null && v > max.Value) return false;
        return true;
    }

    /// <summary>Apply the value-sequence range (first/last) to the loaded results.</summary>
    [RelayCommand]
    private void ApplyResultRange() => ApplyResultFilter();

    /// <summary>Clear the value-sequence range back to unbounded, then re-filter.</summary>
    [RelayCommand]
    private void ResetResultRange()
    {
        SeqFirstMin = SeqFirstMax = SeqLastMin = SeqLastMax = "";
        ApplyResultFilter();
    }

    private void RebuildResultOptions()
    {
        FillDistinct(ResultClassOptions,  _allResults.Select(r => r.ClassName));
        FillDistinct(ResultFieldOptions,  _allResults.Select(r => r.PropName));
        FillDistinct(ResultObjectOptions, _allResults.Select(r => r.NormPath));
    }

    private static void FillDistinct(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var v in values.Where(s => !string.IsNullOrEmpty(s))
                                 .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            target.Add(v);
    }

    /// <summary>Open the selected result's object in the Live Walker tab.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(SpcResultRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        NavigateToInstance?.Invoke(row.ObjAddr);
    }

    /// <summary>Locate this row's owning object within the GWorld graph (reach mode —
    /// lands on the object and scrolls to the changed field).</summary>
    [RelayCommand]
    private void LocateRowInGWorld(SpcResultRow? row)
    {
        if (row == null || !IsGWorldAvailable || string.IsNullOrEmpty(row.ObjAddr)) return;
        LocateInGWorld?.Invoke(row.ObjAddr, row.PropOffset, row.PropName);
    }

    /// <summary>Copy the matched field's live address (newest snapshot's obj_addr
    /// + offset) to the clipboard — a quick handoff into CE.</summary>
    [RelayCommand]
    private async Task CopyAddressAsync(SpcResultRow? row)
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
                StatusText = $"Copied {addr:X}  ({row.ClassName}::{row.PropName})";
            }
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "SPC: copy address failed", ex);
        }
    }

    private static SpcPredicateKind ParsePredicate(string s) => s switch
    {
        "Unchanged" => SpcPredicateKind.Unchanged,
        "Changed"   => SpcPredicateKind.Changed,
        "Increased" => SpcPredicateKind.Increased,
        "Decreased" => SpcPredicateKind.Decreased,
        _           => SpcPredicateKind.Any,
    };

    private static SpcJoinMode ParseJoinMode(string s) => s switch
    {
        "Loose"      => SpcJoinMode.Loose,
        "In-session" => SpcJoinMode.InSession,
        _            => SpcJoinMode.Strict,
    };

    // --- N1: noise picker helpers ---

    /// <summary>Rebuild the noise rows from a fresh result's Top-N contributors.
    /// All rows start unchecked — the user opts in by ticking + Apply.</summary>
    private void RebuildNoiseRows(IReadOnlyList<ClassNoiseRow> top)
    {
        NoiseRows.Clear();
        foreach (var c in top)
        {
            NoiseRows.Add(new NoiseRowVm
            {
                ClassName        = c.ClassName,
                HitCount         = c.HitCount,
                SamplePropsDisplay = c.SamplePropsDisplay,
            });
        }
    }

    /// <summary>Commit the ticked noise rows into the per-game denylist, persist,
    /// and re-run the SPC query so the user sees the cleaned result immediately.
    /// Untick all + Apply is the "clear" path.</summary>
    [RelayCommand]
    private async Task ApplyNoisePicksAsync()
    {
        // Build a FRESH set rather than mutating _excludedClasses in place: an
        // in-flight RunQueryAsync (background thread) may still be iterating the
        // captured reference via deny.Contains(), and HashSet is not safe for
        // concurrent read+write. Persist + reload then swaps _excludedClasses to a
        // new instance, leaving the running query's copy untouched. (Additive —
        // Apply never removes a previously-denied class; that's RemoveFromDenylist.)
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal);
        bool changed = false;
        foreach (var row in NoiseRows)
            if (row.Picked && updated.Add(row.ClassName)) changed = true;
        if (!changed)
        {
            StatusText = "No noise classes picked — tick one or more rows first.";
            return;
        }
        _store.SetClassDenylist(DenylistScope.Spc, updated);
        LoadDenylistFromStore();   // reassigns _excludedClasses to a fresh instance
        await RunQueryAsync();
    }

    /// <summary>Untick all noise-picker rows (without touching the persisted
    /// denylist). Distinct from Clear all, which empties the denylist.</summary>
    [RelayCommand]
    private void ResetNoisePicks()
    {
        foreach (var row in NoiseRows) row.Picked = false;
    }

    /// <summary>Remove one class from the per-game denylist (used by the active-
    /// denylist chips). Re-runs the query so the user sees the un-cleaned result
    /// immediately.</summary>
    [RelayCommand]
    private async Task RemoveFromDenylistAsync(string? className)
    {
        if (string.IsNullOrEmpty(className) || !_excludedClasses.Contains(className)) return;
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal);
        updated.Remove(className);
        _store.SetClassDenylist(DenylistScope.Spc, updated);
        LoadDenylistFromStore();
        await RunQueryAsync();
    }

    /// <summary>Drop every class from the denylist + re-run.</summary>
    [RelayCommand]
    private async Task ClearDenylistAsync()
    {
        if (_excludedClasses.Count == 0) return;
        _store.SetClassDenylist(DenylistScope.Spc, new HashSet<string>(StringComparer.Ordinal));
        LoadDenylistFromStore();
        await RunQueryAsync();
    }
}

/// <summary>One Top-N noise picker row in the SPC / Diff side panel. The
/// <see cref="Picked"/> flag is the user's tick; Apply collects them into the
/// per-game denylist.</summary>
public partial class NoiseRowVm : ObservableObject
{
    [ObservableProperty] private bool _picked;
    public string ClassName          { get; set; } = "";
    public int    HitCount           { get; set; }
    public string SamplePropsDisplay { get; set; } = "";
}
