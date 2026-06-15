using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>One field of the pivot class, pickable as a projected value field
/// and annotated with key/value suitability scores.</summary>
public partial class PivotFieldPick : ObservableObject
{
    public PivotFieldInfo Info { get; }
    public double KeyScore  { get; }
    public int    ValueScore { get; }

    public PivotFieldPick(PivotFieldInfo info, double keyScore, int valueScore)
    {
        Info = info;
        KeyScore = keyScore;
        ValueScore = valueScore;
    }

    [ObservableProperty] private bool _isValue;

    public string Name          => Info.Name;
    public string DeclaredType  => Info.DeclaredType;
    public int    DistinctCount => Info.DistinctCount;
    public int    InstanceCount => Info.InstanceCount;
    public string KeyScoreDisplay => KeyScore.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// ViewModel for the experimental Class Pivot tab. Groups one class's captured
/// instances by intrinsic identity or a chosen key field and projects value
/// fields per group. Pure C# over the SQLite corpus — no DLL/pipe. A key-field
/// suggestion (reusing PropertyScoringTable + a type/name/cardinality scorer)
/// dissolves the "user must guess the business key" problem. See
/// docs/experimental-snapshot-spc-pivot.md §"Phase C".
/// </summary>
public partial class ClassPivotViewModel : ViewModelBase
{
    private readonly ISnapshotStore _store;
    private readonly ILoggingService _log;
    private readonly IPlatformService? _platform;
    private readonly IDumpService? _dump;
    private EngineState? _engineState;
    private readonly List<PivotClassInfo> _allClasses = new();
    /// <summary>Rows of the selected DataTable, cached so Run projects without a
    /// second pipe round-trip (Phase C4).</summary>
    private DataTableWalkResult? _dataTable;
    /// <summary>Rows requested per DataTable walk. A single page keeps C4 simple;
    /// the status row reports when a table exceeds it.</summary>
    private const int DataTableRowLimit = 2000;
    // Monotonic guards so a stale (superseded) async load can't clobber the
    // collections after a newer snapshot/class selection. Rapidly switching the
    // class ComboBox would otherwise interleave two loads at the await boundary
    // and leave Fields holding a mix of two classes.
    private int _classLoadId;
    private int _fieldLoadId;
    private CancellationTokenSource? _pivotCts;   // heavy pivot run
    private CancellationTokenSource? _loadCts;    // class / field list loads (GROUP BY over ~1.7M rows)

    [ObservableProperty] private string _selectedSource = "Snapshot";
    [ObservableProperty] private SnapshotMeta? _selectedSnapshot;
    [ObservableProperty] private string _classFilter = "";
    [ObservableProperty] private PivotClassInfo? _selectedClass;
    [ObservableProperty] private DataTablePick? _selectedDataTable;
    [ObservableProperty] private PivotArrayFieldInfo? _selectedArrayField;
    [ObservableProperty] private string _selectedKeyMode = "Identity (object path)";
    [ObservableProperty] private string? _selectedKeyField;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private PivotResultRow? _selectedResult;

    // --- Change-driven discovery (C3): the automatic front-door. Instead of making
    // the user guess which class/key to pivot when the target is unknown, find the
    // (class, prop) targets whose value MOVED between two snapshots (capture
    // before/after an in-game action), ranked by "looks like game state". The user
    // picks from a short list; "Use →" fills the pivot below and runs it. Pure C#
    // over the SQLite corpus (reuses the SPC intersection load). See
    // docs/experimental-snapshot-spc-pivot.md §"Phase C — C3".
    [ObservableProperty] private SnapshotMeta? _discoverFrom;
    [ObservableProperty] private SnapshotMeta? _discoverTo;
    [ObservableProperty] private bool   _isDiscovering;
    [ObservableProperty] private string _discoverStatus = "";
    [ObservableProperty] private DiscoveryCandidate? _selectedDiscoverResult;
    /// <summary>The newer snapshot of the discovery pair — the one the pivot targets
    /// on "Use →" (its values + addresses are current).</summary>
    private SnapshotMeta? _discoverNewest;
    private CancellationTokenSource? _discoverCts;

    public ObservableCollection<DiscoveryCandidate> DiscoverResults { get; } = new();

    /// <summary>Discovery needs two distinct snapshots (before/after).</summary>
    public bool CanDiscover => !IsDiscovering && !IsBusy
        && DiscoverFrom != null && DiscoverTo != null && DiscoverFrom.Id != DiscoverTo.Id;

    /// <summary>Pivot data source: persisted snapshots (scalar), persisted
    /// snapshot struct-arrays (inner-key pivot — Phase C6), or a live DataTable
    /// (zero-config — RowName is the key). DataTable mode needs a live connection.</summary>
    public IReadOnlyList<string> SourceOptions { get; } =
        new[] { "Snapshot", "Snapshot Array", "DataTable" };

    public IReadOnlyList<string> KeyModeOptions { get; } =
        new[] { "Identity (object path)", "Field" };

    public ObservableCollection<SnapshotMeta> Snapshots { get; } = new();
    public ObservableCollection<PivotClassInfo> Classes { get; } = new();
    public ObservableCollection<DataTablePick> DataTables { get; } = new();
    public ObservableCollection<PivotArrayFieldInfo> ArrayFields { get; } = new();
    public ObservableCollection<PivotFieldPick> Fields { get; } = new();
    public ObservableCollection<string> KeyFieldOptions { get; } = new();
    public ObservableCollection<PivotResultRow> Results { get; } = new();

    /// <summary>Raised to open a pivot group's representative object in Live Walker.</summary>
    public event Action<string>? NavigateToInstance;

    public bool IsSnapshotSource  => SelectedSource == "Snapshot";
    public bool IsArraySource     => SelectedSource == "Snapshot Array";
    public bool IsDataTableSource => SelectedSource == "DataTable";
    /// <summary>Both snapshot modes pick from the same snapshot + class pickers.</summary>
    public bool UsesSnapshot      => IsSnapshotSource || IsArraySource;
    public bool IsFieldKeyMode    => SelectedKeyMode == "Field";
    /// <summary>The key-field picker is only meaningful for scalar snapshot Field
    /// mode; Array keys on the inner-key value, DataTable on RowName.</summary>
    public bool ShowKeyField => IsSnapshotSource && IsFieldKeyMode;

    public bool CanRunPivot => !IsBusy && (
        IsDataTableSource ? (SelectedDataTable != null && _dataTable != null) :
        IsArraySource     ? (SelectedSnapshot != null && SelectedClass != null && SelectedArrayField != null) :
        (SelectedSnapshot != null && SelectedClass != null
            && (!IsFieldKeyMode || !string.IsNullOrEmpty(SelectedKeyField))));

    /// <summary>The most recently started class/field load, started by a
    /// snapshot/class selection change. Exposed so tests can await the
    /// fire-and-forget chain deterministically; the live UI ignores it.</summary>
    public Task? PendingLoad { get; private set; }

    public ClassPivotViewModel(ISnapshotStore store, ILoggingService log,
                               IPlatformService? platform = null, IDumpService? dump = null)
    {
        _store = store;
        _log = log;
        _platform = platform;
        _dump = dump;
    }

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        _store.SetActiveGame(state.PeHash);
        LoadDenylistFromStore();
        // A new connection invalidates any DataTable list/rows from a prior game.
        DataTables.Clear();
        _dataTable = null;
        SelectedDataTable = null;
        _ = RefreshAsync();
        if (IsDataTableSource) PendingLoad = RefreshDataTablesAsync();
    }

    // --- N1: Pivot-scope class denylist (right-click "Hide this class") ---
    // Independent from the SPC / Diff lists — hiding a class here only affects
    // the Pivot class picker.
    private HashSet<string> _excludedClasses = new(StringComparer.Ordinal);

    /// <summary>Classes the user hid from the Pivot picker (display + remove).</summary>
    public ObservableCollection<string> ActiveDenylist { get; } = new();

    /// <summary>True when at least one class is hidden — drives the chips bar's
    /// visibility (Avalonia won't bind an int Count to a bool IsVisible).</summary>
    public bool HasHiddenClasses => ActiveDenylist.Count > 0;

    private void LoadDenylistFromStore()
    {
        _excludedClasses = _store.GetClassDenylist(DenylistScope.Pivot);
        ActiveDenylist.Clear();
        foreach (var c in _excludedClasses.OrderBy(s => s, StringComparer.Ordinal))
            ActiveDenylist.Add(c);
        OnPropertyChanged(nameof(HasHiddenClasses));
    }

    /// <summary>Right-click "Hide this class" → add to the Pivot denylist and
    /// drop it from the picker. Operates on the right-clicked row (or the
    /// selected class as a fallback).</summary>
    [RelayCommand]
    private async Task HideClassAsync(PivotClassInfo? cls)
    {
        var name = cls?.ClassName ?? SelectedClass?.ClassName;
        if (string.IsNullOrEmpty(name) || _excludedClasses.Contains(name)) return;
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal) { name };
        _store.SetClassDenylist(DenylistScope.Pivot, updated);
        LoadDenylistFromStore();
        if (ReferenceEquals(SelectedClass, cls)) SelectedClass = null;
        await LoadClassesAsync();   // refresh the dropdown without the hidden class
    }

    /// <summary>Remove one class from the Pivot denylist (chip click) → it
    /// reappears in the picker.</summary>
    [RelayCommand]
    private async Task RemoveFromDenylistAsync(string? className)
    {
        if (string.IsNullOrEmpty(className) || !_excludedClasses.Contains(className)) return;
        var updated = new HashSet<string>(_excludedClasses, StringComparer.Ordinal);
        updated.Remove(className);
        _store.SetClassDenylist(DenylistScope.Pivot, updated);
        LoadDenylistFromStore();
        await LoadClassesAsync();
    }

    /// <summary>Drop every class from the Pivot denylist.</summary>
    [RelayCommand]
    private async Task ClearDenylistAsync()
    {
        if (_excludedClasses.Count == 0) return;
        _store.SetClassDenylist(DenylistScope.Pivot, new HashSet<string>(StringComparer.Ordinal));
        LoadDenylistFromStore();
        await LoadClassesAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunPivot));
        OnPropertyChanged(nameof(CanDiscover));
    }
    partial void OnDiscoverFromChanged(SnapshotMeta? value) => OnPropertyChanged(nameof(CanDiscover));
    partial void OnDiscoverToChanged(SnapshotMeta? value)   => OnPropertyChanged(nameof(CanDiscover));
    partial void OnIsDiscoveringChanged(bool value)         => OnPropertyChanged(nameof(CanDiscover));
    partial void OnSelectedSnapshotChanged(SnapshotMeta? value) => PendingLoad = LoadClassesAsync();
    partial void OnSelectedKeyFieldChanged(string? value) => OnPropertyChanged(nameof(CanRunPivot));
    partial void OnSelectedDataTableChanged(DataTablePick? value) => PendingLoad = LoadDataTableFieldsAsync();
    partial void OnSelectedArrayFieldChanged(PivotArrayFieldInfo? value)
    {
        OnPropertyChanged(nameof(CanRunPivot));
        PendingLoad = LoadArrayPropsAsync();
    }

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsSnapshotSource));
        OnPropertyChanged(nameof(IsArraySource));
        OnPropertyChanged(nameof(IsDataTableSource));
        OnPropertyChanged(nameof(UsesSnapshot));
        OnPropertyChanged(nameof(ShowKeyField));
        OnPropertyChanged(nameof(CanRunPivot));
        // Switching source clears the cross-source picker/result state so a stale
        // field list or row can't be Run against the wrong source. Detach bound
        // selections before clearing (Avalonia selection-model safety).
        SelectedKeyField = null;
        SelectedArrayField = null;
        SelectedResult = null;
        Fields.Clear();
        KeyFieldOptions.Clear();
        ArrayFields.Clear();
        Results.Clear();
        StatusText = "";
        if (IsDataTableSource)
        {
            if (DataTables.Count == 0) PendingLoad = RefreshDataTablesAsync();
        }
        else
        {
            // Snapshot vs Snapshot-Array have different class lists (array classes
            // are a subset) — reload for the new source.
            PendingLoad = LoadClassesAsync();
        }
    }

    partial void OnSelectedKeyModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFieldKeyMode));
        OnPropertyChanged(nameof(ShowKeyField));
        OnPropertyChanged(nameof(CanRunPivot));
    }

    partial void OnSelectedClassChanged(PivotClassInfo? value)
    {
        OnPropertyChanged(nameof(CanRunPivot));
        PendingLoad = IsArraySource ? LoadArrayFieldsAsync() : LoadFieldsAsync();
    }

    partial void OnClassFilterChanged(string value) => ApplyClassFilter();

    /// <summary>C5 right-click handoff target: switch to scalar Snapshot source,
    /// select <paramref name="className"/> in the newest snapshot, and surface
    /// <paramref name="propName"/> (ticked as a value field unless it became the
    /// auto-suggested key). No-op with a hint if no snapshot or class match. The
    /// caller (MainWindowViewModel) has already switched to the Class Pivot tab.</summary>
    public async Task PivotForAsync(string className, string? propName)
    {
        try
        {
            ClearError();
            SelectedSource = "Snapshot";
            if (Snapshots.Count == 0)
                await RefreshAsync();             // loads snapshots -> newest -> classes
            var pending = PendingLoad;            // class load kicked off by the above
            if (pending != null) { try { await pending; } catch { /* surfaced via SetError */ } }

            if (SelectedSnapshot == null)
            {
                StatusText = "No snapshot captured yet — capture one on the Snapshot tab first.";
                return;
            }

            if (await SelectClassAndTickPropAsync(className, propName) && !string.IsNullOrEmpty(propName))
                StatusText = $"Ready: {className} · {propName} — press Run Pivot.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, $"Pivot: handoff for {className}.{propName} failed", ex);
            SetError(ex);
        }
    }

    /// <summary>Select <paramref name="className"/> in the CURRENTLY selected
    /// snapshot and tick <paramref name="propName"/> as a value field. Assumes the
    /// snapshot + class list are already loaded; awaits the field load it triggers.
    /// Returns false (with a status hint) when the class isn't in the snapshot.
    /// Shared by the C5 right-click handoff (<see cref="PivotForAsync"/>) and the
    /// change-driven discovery "Use →" (<see cref="UseDiscoverCandidateAsync"/>).</summary>
    private async Task<bool> SelectClassAndTickPropAsync(string className, string? propName)
    {
        ClassFilter = "";                     // ensure the class isn't filtered out
        var match = Classes.FirstOrDefault(c => c.ClassName == className);
        if (match == null)
        {
            StatusText = $"'{className}' is not in the selected snapshot — capture it, then retry.";
            return false;
        }

        if (ReferenceEquals(SelectedClass, match))
            PendingLoad = LoadFieldsAsync();  // already selected → no change event; reload explicitly
        else
            SelectedClass = match;            // triggers the field load
        var pending = PendingLoad;
        if (pending != null) { try { await pending; } catch { /* surfaced via SetError */ } }

        if (!string.IsNullOrEmpty(propName) && SelectedKeyField != propName)
        {
            var pick = Fields.FirstOrDefault(f => f.Name == propName);
            if (pick != null) pick.IsValue = true;
        }
        return true;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // Off the UI thread: Microsoft.Data.Sqlite "*Async" runs synchronously
            // on the caller, so awaiting it on the UI thread would block + run the
            // collection rebuild inline inside a binding event (the crash).
            var list = await Task.Run(() => _store.ListSnapshotsAsync());
            UiCollection.Reset(Snapshots, list, () => SelectedSnapshot = null);
            // Drop cache entries for snapshots that no longer exist (deleted) so a
            // recaptured Id can't ever read a stale class/field list. (Ids are
            // monotonic so reuse is unlikely, but this keeps the cache honest +
            // bounded.)
            var liveIds = new HashSet<long>(list.Select(s => s.Id));
            foreach (var k in _classCache.Keys.Where(k => !liveIds.Contains(k.Item1)).ToList())
                _classCache.Remove(k);
            foreach (var k in _fieldCache.Keys.Where(k => !liveIds.Contains(k.Item1)).ToList())
                _fieldCache.Remove(k);
            // Default to the newest snapshot (triggers class load).
            SelectedSnapshot = Snapshots.Count > 0 ? Snapshots[0] : null;
            // Discovery defaults: compare the last two captures (To = newest,
            // From = second-newest) — the common "capture before/after an action" flow.
            DiscoverTo   = Snapshots.Count > 0 ? Snapshots[0] : null;
            DiscoverFrom = Snapshots.Count > 1 ? Snapshots[1] : DiscoverTo;
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list snapshots failed", ex);
            SetError(ex);
        }
    }

    // Per-snapshot class-list cache. A snapshot is write-once / immutable, so its
    // class list never changes — compute it ONCE and reuse for the rest of the
    // session instead of re-running the ~1.7M-row GROUP BY on every dropdown
    // change. Keyed by (snapshotId, arrayMode); pruned in RefreshAsync when a
    // snapshot is deleted. The denylist filter is applied on top of the cached
    // list (so hiding a class never needs a re-scan).
    private readonly Dictionary<(long, bool), IReadOnlyList<PivotClassInfo>> _classCache = new();

    private async Task LoadClassesAsync()
    {
        OnPropertyChanged(nameof(CanRunPivot));
        // Supersede any in-flight (stale) class load at ENTRY — the cache-hit and
        // empty early-return paths below must bump this too, so a slower scan
        // started by a prior snapshot bails (id mismatch) instead of clobbering the
        // picker when it finally completes. Bumping only on the cache-MISS path left
        // a hole: a miss in flight, then a switch to an ALREADY-cached snapshot, let
        // the miss complete and overwrite the cached list with the wrong snapshot's.
        int id = ++_classLoadId;
        if (SelectedSnapshot == null) { _allClasses.Clear(); Classes.Clear(); return; }
        long snapId = SelectedSnapshot.Id;
        bool arrayMode = IsArraySource;
        var key = (snapId, arrayMode);

        // Cache hit → apply instantly, no scan, no thread.
        if (_classCache.TryGetValue(key, out var cachedList))
        {
            ApplyClassList(cachedList);
            StatusText = $"{_allClasses.Count:N0} classes — pick one to pivot.";
            return;
        }

        // Cache miss → scan once. Cancel a prior in-flight load so rapidly
        // changing the snapshot doesn't stack several heavy scans on the pool.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        StatusText = "Loading classes… (first time for this snapshot)";
        try
        {
            // Array mode lists only classes that captured struct-array elements.
            var list = await Task.Run(() => arrayMode
                ? _store.ListPivotArrayClassesAsync(snapId, ct)
                : _store.ListPivotClassesAsync(snapId, ct), ct);
            if (id != _classLoadId) return;   // a newer snapshot/source superseded us
            _classCache[key] = list;          // cache for the rest of the session
            ApplyClassList(list);
            StatusText = $"{_allClasses.Count:N0} classes — pick one to pivot.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection — leave the status to the new load.
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list classes failed", ex);
            SetError(ex);
        }
    }

    // Apply a (cached or fresh) class list to the picker, filtered by the
    // Pivot-scope denylist. N1: hidden classes never appear (independent of the
    // SPC / Diff denylists — per-tab isolation).
    private void ApplyClassList(IReadOnlyList<PivotClassInfo> list)
    {
        _allClasses.Clear();
        foreach (var c in list)
            if (!_excludedClasses.Contains(c.ClassName)) _allClasses.Add(c);
        ApplyClassFilter();
    }

    private void ApplyClassFilter()
    {
        string f = ClassFilter.Trim();
        var filtered = _allClasses.Where(c =>
            f.Length == 0 || c.ClassName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        // Detach the selection before rebuilding (Avalonia selection-model safety).
        UiCollection.Reset(Classes, filtered, () => SelectedClass = null);
    }

    // Per-(snapshot, class) field-list cache — same immutability rationale as the
    // class cache: a snapshot's fields for a class never change, so scan once.
    private readonly Dictionary<(long, string), IReadOnlyList<PivotFieldInfo>> _fieldCache = new();

    private async Task LoadFieldsAsync()
    {
        // Supersede any in-flight (stale) field load at ENTRY so a slower scan from
        // a prior class can't clobber the picker — including when the newer class is
        // served from the cache-hit path below (which returns without a scan). Same
        // rationale as LoadClassesAsync: bumping only on cache-MISS left the hole.
        int id = ++_fieldLoadId;
        if (SelectedSnapshot == null || SelectedClass == null)
        {
            Fields.Clear(); KeyFieldOptions.Clear(); Results.Clear();
            return;
        }
        long snapId = SelectedSnapshot.Id;
        string cls = SelectedClass.ClassName;
        var key = (snapId, cls);

        // Cache hit → rebuild the picker instantly (no scan).
        if (_fieldCache.TryGetValue(key, out var cachedFields))
        {
            ApplyFieldList(cachedFields, cls);
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        StatusText = "Loading fields…";
        try
        {
            var fields = await Task.Run(() => _store.ListPivotFieldsAsync(snapId, cls, ct), ct);
            // A newer class selection superseded us — leave its results intact.
            // (Clear is deferred to here so a stale load can't wipe the latest.)
            if (id != _fieldLoadId) return;
            _fieldCache[key] = fields;
            ApplyFieldList(fields, cls);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer class/snapshot selection.
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list fields failed", ex);
            SetError(ex);
        }
    }

    // Rebuild the field picker (+ key suggestion + value pre-ticks) from a cached
    // or freshly-loaded field list. Pure UI work — no DB.
    private void ApplyFieldList(IReadOnlyList<PivotFieldInfo> fields, string cls)
    {
        SelectedKeyField = null;   // detach selections before clearing bound lists
        SelectedResult = null;
        Fields.Clear();
        KeyFieldOptions.Clear();
        Results.Clear();
        foreach (var f in fields)
        {
            Fields.Add(new PivotFieldPick(f,
                PivotKeyScorer.KeyScore(f), PivotKeyScorer.ValueScore(cls, f.Name)));
            KeyFieldOptions.Add(f.Name);
        }

        // Suggest a key: pick the best-scoring field. If it scores well, default
        // to Field mode on it; otherwise fall back to Identity.
        var suggested = PivotKeyScorer.SuggestKey(fields);
        if (suggested != null && PivotKeyScorer.KeyScore(suggested) >= 0.5)
        {
            SelectedKeyField = suggested.Name;
            SelectedKeyMode  = "Field";
        }
        else
        {
            SelectedKeyField = KeyFieldOptions.FirstOrDefault();
            SelectedKeyMode  = "Identity (object path)";
        }

        // Pre-tick the most interesting value fields (excludes the key), capped so
        // a wide class doesn't project dozens of columns.
        int ticked = 0;
        foreach (var p in Fields.OrderByDescending(p => p.ValueScore))
        {
            if (ticked >= 3) break;
            if (p.Name == SelectedKeyField) continue;
            if (p.ValueScore >= PropertyScoringTable.InterestingThreshold)
            {
                p.IsValue = true;
                ticked++;
            }
        }
        StatusText = $"{Fields.Count} fields · suggested key: {SelectedKeyField ?? "(none)"}";
    }

    /// <summary>List live DataTable instances (and subclasses) for the C4 picker.</summary>
    [RelayCommand]
    private async Task RefreshDataTablesAsync()
    {
        if (_dump == null) return;
        try
        {
            var res = await _dump.FindInstancesAsync("DataTable", exactMatch: false, limit: 1000);
            var picks = res.Instances
                .Where(i => i.ClassName.IndexOf("DataTable", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new DataTablePick(i.Name, i.Address, i.ClassName));
            UiCollection.Reset(DataTables, picks, () => SelectedDataTable = null);
            StatusText = $"{DataTables.Count} DataTable(s) found";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list DataTables failed", ex);
            SetError(ex);
        }
    }

    /// <summary>Walk the selected DataTable and surface its row struct fields as the
    /// value-field picker. RowName is the implicit key — no key discovery needed.</summary>
    private async Task LoadDataTableFieldsAsync()
    {
        // Bump the guard at entry (not after the null-check) so a null/empty
        // selection also supersedes any in-flight load — otherwise a slow load
        // for the previous DataTable could complete and repopulate Fields after
        // the selection was cleared (same race fixed in LoadClasses/Fields).
        int id = ++_fieldLoadId;
        _dataTable = null;
        SelectedKeyField = null;
        SelectedResult = null;
        Fields.Clear();
        KeyFieldOptions.Clear();
        Results.Clear();
        OnPropertyChanged(nameof(CanRunPivot));
        if (_dump == null || SelectedDataTable == null) return;

        try
        {
            var dt = await _dump.WalkDataTableRowsAsync(SelectedDataTable.Address, 0, DataTableRowLimit);
            if (id != _fieldLoadId) return;   // a newer DataTable selection superseded us
            _dataTable = dt;

            var fields = DataTablePivotEngine.Fields(dt);
            string structName = dt.RowStructName;
            foreach (var f in fields)
            {
                Fields.Add(new PivotFieldPick(f,
                    PivotKeyScorer.KeyScore(f), PivotKeyScorer.ValueScore(structName, f.Name)));
                KeyFieldOptions.Add(f.Name);
            }

            // Pre-tick the most interesting value fields (there is no key to exclude).
            int ticked = 0;
            foreach (var p in Fields.OrderByDescending(p => p.ValueScore))
            {
                if (ticked >= 3) break;
                if (p.ValueScore >= PropertyScoringTable.InterestingThreshold) { p.IsValue = true; ticked++; }
            }
            // If nothing scored as interesting, tick the first few so Run shows data.
            if (ticked == 0)
                foreach (var p in Fields.Take(3)) p.IsValue = true;

            OnPropertyChanged(nameof(CanRunPivot));
            var trunc = dt.RowCount > dt.Rows.Count ? $" (showing {dt.Rows.Count:N0} of {dt.RowCount:N0})" : "";
            StatusText = $"{(string.IsNullOrEmpty(structName) ? "DataTable" : structName)}: " +
                         $"{dt.Rows.Count:N0} rows{trunc} · key = RowName";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: walk DataTable failed", ex);
            SetError(ex);
        }
    }

    /// <summary>List the captured struct-array fields of the selected class (C6).</summary>
    private async Task LoadArrayFieldsAsync()
    {
        // Bump at entry so a null selection also supersedes in-flight loads.
        int id = ++_classLoadId;
        SelectedArrayField = null;   // detach selections before clearing bound lists
        SelectedKeyField = null;
        SelectedResult = null;
        ArrayFields.Clear();
        Fields.Clear();
        KeyFieldOptions.Clear();
        Results.Clear();
        OnPropertyChanged(nameof(CanRunPivot));
        if (SelectedSnapshot == null || SelectedClass == null) return;
        long snapId = SelectedSnapshot.Id;
        string cls = SelectedClass.ClassName;
        try
        {
            var list = await Task.Run(() => _store.ListPivotArrayFieldsAsync(snapId, cls));
            if (id != _classLoadId) return;   // a newer class/snapshot superseded us
            foreach (var af in list) ArrayFields.Add(af);
            // Auto-select the first array field (triggers prop load).
            SelectedArrayField = ArrayFields.FirstOrDefault();
            StatusText = $"{ArrayFields.Count} array field(s) in {cls}";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list array fields failed", ex);
            SetError(ex);
        }
    }

    /// <summary>List the inner numeric props of the selected struct-array as the
    /// value-field picker. The inner-key value is the implicit group key (C6).</summary>
    private async Task LoadArrayPropsAsync()
    {
        // Bump at entry so a null selection also supersedes in-flight loads.
        int id = ++_fieldLoadId;
        SelectedKeyField = null;
        SelectedResult = null;
        Fields.Clear();
        KeyFieldOptions.Clear();
        Results.Clear();
        OnPropertyChanged(nameof(CanRunPivot));
        if (SelectedSnapshot == null || SelectedClass == null || SelectedArrayField == null) return;
        long snapId = SelectedSnapshot.Id;
        string cls = SelectedClass.ClassName;
        string af = SelectedArrayField.ArrayField;
        try
        {
            var props = await Task.Run(() => _store.ListPivotArrayPropsAsync(snapId, cls, af));
            if (id != _fieldLoadId) return;
            foreach (var f in props)
            {
                Fields.Add(new PivotFieldPick(f,
                    PivotKeyScorer.KeyScore(f), PivotKeyScorer.ValueScore(cls, f.Name)));
                KeyFieldOptions.Add(f.Name);
            }
            // Pre-tick interesting inner props; fall back to the first few.
            int ticked = 0;
            foreach (var p in Fields.OrderByDescending(p => p.ValueScore))
            {
                if (ticked >= 3) break;
                if (p.ValueScore >= PropertyScoringTable.InterestingThreshold) { p.IsValue = true; ticked++; }
            }
            if (ticked == 0)
                foreach (var p in Fields.Take(3)) p.IsValue = true;

            OnPropertyChanged(nameof(CanRunPivot));
            string keyName = string.IsNullOrEmpty(SelectedArrayField.InnerKeyName)
                ? "(elem index)" : SelectedArrayField.InnerKeyName;
            StatusText = $"{af}: {Fields.Count} inner prop(s) · key = {keyName}";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list array props failed", ex);
            SetError(ex);
        }
    }

    [RelayCommand]
    private async Task RunPivotAsync()
    {
        if (!CanRunPivot) return;
        // Cancel a prior in-flight pivot before starting a new one.
        _pivotCts?.Cancel();
        _pivotCts?.Dispose();
        var cts = _pivotCts = new CancellationTokenSource();
        var ct = cts.Token;

        ClearError();
        IsBusy = true;
        StatusText = "Running pivot…";
        SelectedResult = null;   // detach before clearing the bound results grid
        Results.Clear();
        try
        {
            if (IsDataTableSource)
            {
                // Zero-config: RowName is the key, every row its own group.
                var valueFields = Fields.Where(f => f.IsValue).Select(f => f.Name).ToList();
                var res = DataTablePivotEngine.Build(_dataTable!, valueFields);
                foreach (var row in res.Rows) Results.Add(row);
                StatusText = $"{res.GroupCount:N0} rows · key = RowName · {_dataTable!.RowStructName}";
                return;
            }

            if (IsArraySource)
            {
                // Group struct-array elements by inner-key value (reorder-immune).
                var aq = new ArrayPivotQuery
                {
                    SnapshotId = SelectedSnapshot!.Id,
                    ClassName  = SelectedClass!.ClassName,
                    ArrayField = SelectedArrayField!.ArrayField,
                    ValueProps = Fields.Where(f => f.IsValue).Select(f => f.Name).ToList(),
                };
                var arrRes = await Task.Run(() => _store.PivotArrayAsync(aq, ct), ct);
                foreach (var row in arrRes.Rows) Results.Add(row);
                var arrTrunc = arrRes.Truncated ? $" (capped at {aq.MaxGroups:N0})" : "";
                string keyName = string.IsNullOrEmpty(SelectedArrayField.InnerKeyName)
                    ? "elem index" : SelectedArrayField.InnerKeyName;
                StatusText = $"{arrRes.GroupCount:N0} {keyName} group(s){arrTrunc} from {arrRes.InstanceCount:N0} elements · {aq.ArrayField}";
                return;
            }

            var query = new PivotQuery
            {
                SnapshotId  = SelectedSnapshot!.Id,
                ClassName   = SelectedClass!.ClassName,
                KeyMode     = IsFieldKeyMode ? PivotKeyMode.Field : PivotKeyMode.Identity,
                KeyField    = SelectedKeyField ?? "",
                ValueFields = Fields.Where(f => f.IsValue).Select(f => f.Name).ToList(),
            };
            var snapRes = await Task.Run(() => _store.PivotAsync(query, ct), ct);
            foreach (var row in snapRes.Rows) Results.Add(row);

            var snapTrunc = snapRes.Truncated ? $" (capped at {query.MaxGroups:N0})" : "";
            var keyDesc = IsFieldKeyMode ? $"key={query.KeyField}" : "identity";
            StatusText = $"{snapRes.GroupCount:N0} groups{snapTrunc} from {snapRes.InstanceCount:N0} instances · {keyDesc}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Pivot cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: run failed", ex);
            SetError(ex);
            StatusText = "Pivot failed.";
        }
        finally
        {
            if (ReferenceEquals(_pivotCts, cts))
            {
                IsBusy = false;
                _pivotCts.Dispose();
                _pivotCts = null;
            }
        }
    }

    /// <summary>Change-driven discovery: rank the (class, prop) targets that moved
    /// between the two chosen snapshots. The result feeds the pivot via "Use →".</summary>
    [RelayCommand]
    private async Task RunDiscoverAsync()
    {
        if (!CanDiscover) return;
        _discoverCts?.Cancel();
        _discoverCts?.Dispose();
        var cts = _discoverCts = new CancellationTokenSource();
        var ct = cts.Token;

        ClearError();
        IsDiscovering = true;
        DiscoverStatus = "Scanning for changes…";
        SelectedDiscoverResult = null;   // detach before clearing the bound grid
        DiscoverResults.Clear();
        try
        {
            // Order the pair chronologically (lower id = older) so the direction
            // (↑/↓) and the value sequence read oldest → newest, regardless of which
            // box the user put each snapshot in. The newer one is the pivot target.
            long olderId = Math.Min(DiscoverFrom!.Id, DiscoverTo!.Id);
            long newerId = Math.Max(DiscoverFrom!.Id, DiscoverTo!.Id);
            _discoverNewest = Snapshots.FirstOrDefault(s => s.Id == newerId);

            var query = new DiscoveryQuery
            {
                JoinMode        = SpcJoinMode.Strict,
                ExcludedClasses = _excludedClasses.Count > 0 ? _excludedClasses : null,
                MaxResults      = 200,
            };
            query.SnapshotIds.Add(olderId);
            query.SnapshotIds.Add(newerId);

            var res = await Task.Run(() => _store.DiscoverChangesAsync(query, ct), ct);
            foreach (var row in res.Rows) DiscoverResults.Add(row);

            var trunc = res.Truncated ? $" (top {res.Rows.Count:N0} of {res.ChangedGroups:N0})" : "";
            DiscoverStatus = res.Rows.Count == 0
                ? "No fields changed between these two snapshots."
                : $"{res.ChangedGroups:N0} changed target(s){trunc} — pick one, then Use →.";
        }
        catch (OperationCanceledException)
        {
            DiscoverStatus = "Discovery cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: discovery failed", ex);
            SetError(ex);
            DiscoverStatus = "Discovery failed.";
        }
        finally
        {
            if (ReferenceEquals(_discoverCts, cts))
            {
                IsDiscovering = false;
                _discoverCts.Dispose();
                _discoverCts = null;
            }
        }
    }

    /// <summary>"Use →": take a discovered candidate and pivot it — switch to the
    /// newer snapshot of the discovery pair, select the candidate's class, tick its
    /// property, and run the pivot so the grouped result appears immediately. This
    /// is the payoff: the user never had to know the class or key up front.</summary>
    [RelayCommand]
    public async Task UseDiscoverCandidateAsync(DiscoveryCandidate? cand)
    {
        if (cand == null) return;
        try
        {
            ClearError();
            SelectedSource = "Snapshot";
            var target = _discoverNewest ?? DiscoverTo ?? SelectedSnapshot;
            if (target != null && !ReferenceEquals(SelectedSnapshot, target))
                SelectedSnapshot = target;    // triggers the class load
            var pending = PendingLoad;
            if (pending != null) { try { await pending; } catch { /* surfaced via SetError */ } }

            if (SelectedSnapshot == null)
            {
                StatusText = "No snapshot selected — capture one first.";
                return;
            }
            if (await SelectClassAndTickPropAsync(cand.ClassName, cand.PropName))
            {
                // The discovered property IS the thing the user wants to watch, so
                // always project it — and group by Identity so its value shows per
                // instance. (Don't let the key auto-suggester consume the discovered
                // property as the group key, which would hide its value in Identity
                // mode / collapse it in Field mode.)
                SelectedKeyMode = "Identity (object path)";
                var pick = Fields.FirstOrDefault(f => f.Name == cand.PropName);
                if (pick != null) pick.IsValue = true;
                await RunPivotAsync();        // show the grouped pivot of the target now
            }
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, $"Pivot: use discovery candidate {cand.ClassName}.{cand.PropName} failed", ex);
            SetError(ex);
        }
    }

    /// <summary>Cancel an in-flight pivot run, discovery, AND any class/field list
    /// load — called when the user navigates away from the Class Pivot tab so a
    /// heavy GROUP BY / scan doesn't keep burning CPU.</summary>
    public void CancelPendingWork()
    {
        _pivotCts?.Cancel();
        _loadCts?.Cancel();
        _discoverCts?.Cancel();
    }

    /// <summary>Open the selected group's representative object in Live Walker.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(PivotResultRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        NavigateToInstance?.Invoke(row.ObjAddr);
    }

    /// <summary>Copy the representative instance's base address to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyAddressAsync(PivotResultRow? row)
    {
        if (row == null || _platform == null || string.IsNullOrEmpty(row.ObjAddr)) return;
        try
        {
            var hex = row.ObjAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? row.ObjAddr.Substring(2) : row.ObjAddr;
            await _platform.CopyToClipboardAsync(hex);
            StatusText = $"Copied {hex}  ({row.KeyValue})";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: copy address failed", ex);
        }
    }
}
