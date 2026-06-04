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

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(PredicateEnabled));
    partial void OnIsBaselineChanged(bool value)
    {
        OnPropertyChanged(nameof(PredicateEnabled));
        if (value) SelectedPredicate = "Any";   // the baseline is always "Any"
    }

    public IReadOnlyList<string> PredicateOptions { get; }

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

    /// <summary>Raised to open a result row's object in the Live Walker tab.</summary>
    public event Action<string>? NavigateToInstance;

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
        _store.SetActiveGame(state.PeHash);
        _ = RefreshAsync();
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
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanRunQuery));
            UpdateWarning();
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

        ClearError();
        IsQuerying = true;
        SelectedResult = null;   // detach before clearing the bound results grid
        Results.Clear();
        try
        {
            var query = new SpcQuery
            {
                JoinMode      = ParseJoinMode(SelectedJoinMode),
                ClassContains = ClassFilter.Trim(),
                PropContains  = PropFilter.Trim(),
            };
            for (int i = 0; i < selected.Count; i++)
            {
                query.SnapshotIds.Add(selected[i].Id);
                // Index 0 is the baseline; its predicate is forced to Any.
                query.Predicates.Add(i == 0 ? SpcPredicateKind.Any
                                            : ParsePredicate(selected[i].SelectedPredicate));
            }

            // Off the UI thread — the N-way self-join over ~1.8M rows would
            // otherwise freeze the window for seconds.
            var res = await Task.Run(() => _store.SpcQueryAsync(query));
            foreach (var row in res.Rows) Results.Add(row);

            var trunc = res.Truncated ? $" (capped at {query.MaxRows:N0})" : "";
            var spanSessions = selected.Select(p => p.Meta.GameSessionId).Distinct().Count();
            var sess = spanSessions > 1 ? $", {spanSessions} sessions" : "";
            StatusText = Results.Count == 0
                ? $"No fields match the chain across {selected.Count} snapshots{sess}."
                : $"{Results.Count:N0} match{trunc} across {selected.Count} snapshots{sess} ({SelectedJoinMode}).";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "SPC: query failed", ex);
            SetError(ex);
            StatusText = "Query failed.";
        }
        finally
        {
            IsQuerying = false;
        }
    }

    /// <summary>Open the selected result's object in the Live Walker tab.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(SpcResultRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        NavigateToInstance?.Invoke(row.ObjAddr);
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
}
