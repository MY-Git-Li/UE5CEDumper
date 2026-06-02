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
    private EngineState? _engineState;
    private readonly List<PivotClassInfo> _allClasses = new();
    // Monotonic guards so a stale (superseded) async load can't clobber the
    // collections after a newer snapshot/class selection. Rapidly switching the
    // class ComboBox would otherwise interleave two loads at the await boundary
    // and leave Fields holding a mix of two classes.
    private int _classLoadId;
    private int _fieldLoadId;

    [ObservableProperty] private SnapshotMeta? _selectedSnapshot;
    [ObservableProperty] private string _classFilter = "";
    [ObservableProperty] private PivotClassInfo? _selectedClass;
    [ObservableProperty] private string _selectedKeyMode = "Identity (object path)";
    [ObservableProperty] private string? _selectedKeyField;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private PivotResultRow? _selectedResult;

    public IReadOnlyList<string> KeyModeOptions { get; } =
        new[] { "Identity (object path)", "Field" };

    public ObservableCollection<SnapshotMeta> Snapshots { get; } = new();
    public ObservableCollection<PivotClassInfo> Classes { get; } = new();
    public ObservableCollection<PivotFieldPick> Fields { get; } = new();
    public ObservableCollection<string> KeyFieldOptions { get; } = new();
    public ObservableCollection<PivotResultRow> Results { get; } = new();

    /// <summary>Raised to open a pivot group's representative object in Live Walker.</summary>
    public event Action<string>? NavigateToInstance;

    public bool IsFieldKeyMode => SelectedKeyMode == "Field";

    public bool CanRunPivot => SelectedSnapshot != null && SelectedClass != null && !IsBusy
        && (!IsFieldKeyMode || !string.IsNullOrEmpty(SelectedKeyField));

    /// <summary>The most recently started class/field load, started by a
    /// snapshot/class selection change. Exposed so tests can await the
    /// fire-and-forget chain deterministically; the live UI ignores it.</summary>
    public Task? PendingLoad { get; private set; }

    public ClassPivotViewModel(ISnapshotStore store, ILoggingService log,
                               IPlatformService? platform = null)
    {
        _store = store;
        _log = log;
        _platform = platform;
    }

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        _store.SetActiveGame(state.PeHash);
        _ = RefreshAsync();
    }

    partial void OnIsBusyChanged(bool value)            => OnPropertyChanged(nameof(CanRunPivot));
    partial void OnSelectedSnapshotChanged(SnapshotMeta? value) => PendingLoad = LoadClassesAsync();
    partial void OnSelectedKeyFieldChanged(string? value) => OnPropertyChanged(nameof(CanRunPivot));

    partial void OnSelectedKeyModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFieldKeyMode));
        OnPropertyChanged(nameof(CanRunPivot));
    }

    partial void OnSelectedClassChanged(PivotClassInfo? value)
    {
        OnPropertyChanged(nameof(CanRunPivot));
        PendingLoad = LoadFieldsAsync();
    }

    partial void OnClassFilterChanged(string value) => ApplyClassFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var list = await _store.ListSnapshotsAsync();
            Snapshots.Clear();
            foreach (var s in list) Snapshots.Add(s);
            // Default to the newest snapshot (triggers class load).
            SelectedSnapshot = Snapshots.Count > 0 ? Snapshots[0] : null;
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list snapshots failed", ex);
            SetError(ex);
        }
    }

    private async Task LoadClassesAsync()
    {
        OnPropertyChanged(nameof(CanRunPivot));
        if (SelectedSnapshot == null) { _allClasses.Clear(); Classes.Clear(); return; }
        int id = ++_classLoadId;
        long snapId = SelectedSnapshot.Id;
        try
        {
            var list = await _store.ListPivotClassesAsync(snapId);
            if (id != _classLoadId) return;   // a newer snapshot superseded us
            _allClasses.Clear();
            _allClasses.AddRange(list);
            ApplyClassFilter();
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list classes failed", ex);
            SetError(ex);
        }
    }

    private void ApplyClassFilter()
    {
        string f = ClassFilter.Trim();
        Classes.Clear();
        foreach (var c in _allClasses)
            if (f.Length == 0 || c.ClassName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                Classes.Add(c);
    }

    private async Task LoadFieldsAsync()
    {
        if (SelectedSnapshot == null || SelectedClass == null)
        {
            Fields.Clear(); KeyFieldOptions.Clear(); Results.Clear();
            return;
        }
        int id = ++_fieldLoadId;
        long snapId = SelectedSnapshot.Id;
        string cls = SelectedClass.ClassName;
        try
        {
            var fields = await _store.ListPivotFieldsAsync(snapId, cls);
            // A newer class selection superseded us — leave its results intact.
            // (Clear is deferred to here so a stale load can't wipe the latest.)
            if (id != _fieldLoadId) return;
            Fields.Clear();
            KeyFieldOptions.Clear();
            Results.Clear();
            foreach (var f in fields)
            {
                Fields.Add(new PivotFieldPick(f,
                    PivotKeyScorer.KeyScore(f), PivotKeyScorer.ValueScore(cls, f.Name)));
                KeyFieldOptions.Add(f.Name);
            }

            // Suggest a key: pick the best-scoring field. If it scores well,
            // default to Field mode on it; otherwise fall back to Identity.
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

            // Pre-tick the most interesting value fields (excludes the key),
            // capped so a wide class doesn't project dozens of columns.
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
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: list fields failed", ex);
            SetError(ex);
        }
    }

    [RelayCommand]
    private async Task RunPivotAsync()
    {
        if (!CanRunPivot) return;
        ClearError();
        IsBusy = true;
        Results.Clear();
        try
        {
            var query = new PivotQuery
            {
                SnapshotId  = SelectedSnapshot!.Id,
                ClassName   = SelectedClass!.ClassName,
                KeyMode     = IsFieldKeyMode ? PivotKeyMode.Field : PivotKeyMode.Identity,
                KeyField    = SelectedKeyField ?? "",
                ValueFields = Fields.Where(f => f.IsValue).Select(f => f.Name).ToList(),
            };
            var res = await _store.PivotAsync(query);
            foreach (var row in res.Rows) Results.Add(row);

            var trunc = res.Truncated ? $" (capped at {query.MaxGroups:N0})" : "";
            var keyDesc = IsFieldKeyMode ? $"key={query.KeyField}" : "identity";
            StatusText = $"{res.GroupCount:N0} groups{trunc} from {res.InstanceCount:N0} instances · {keyDesc}";
        }
        catch (Exception ex)
        {
            _log.Error(Constants.LogCatView, "Pivot: run failed", ex);
            SetError(ex);
            StatusText = "Pivot failed.";
        }
        finally
        {
            IsBusy = false;
        }
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
