using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Interesting Functions Finder panel.
///
/// Workflow:
/// 1. User clicks Load -> DLL <c>list_all_functions</c> returns every
///    UFunction across every UClass (~50k for a big game).
/// 2. <see cref="KeywordScoringTable"/> scores + categorises each row
///    client-side. Cheap (~50k rows process in &lt;1s).
/// 3. UI displays rows sorted by FinalScore descending, filtered by
///    name substring, category chip, and (optional) "Show All" toggle
///    that bypasses the InterestingThreshold cutoff.
/// 4. Per-row actions: Open in LiveWalker (with find_instance fallback
///    to ClassStruct when no live instance exists) and Copy AA Script
///    (Baked) -- shortcut into the same dialog that LiveWalker's
///    AA(Baked) button opens, fetching params on demand.
///
/// Cross-tab navigation events are fired up to MainWindowViewModel
/// which knows how to switch tabs + invoke the right commands on
/// LiveWalkerViewModel / ClassStructViewModel. Mirrors the
/// GameClassFilterViewModel pattern.
/// </summary>
public partial class InterestingFunctionsViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IAobMakerBridge? _aobMaker;

    /// <summary>Cooldown between AOBMaker availability re-checks. The
    /// pipe connect attempt has a 2s timeout when CE isn't running, so
    /// without throttling rapid tab switches would stack 2s-blocking
    /// checks. Mirrors <see cref="LiveWalkerViewModel"/>'s pattern.</summary>
    private static readonly TimeSpan AobMakerCheckCooldown = TimeSpan.FromSeconds(5);
    private DateTime _lastAobMakerCheck = DateTime.MinValue;

    /// <summary>Full unfiltered list of scored rows. Filter operates
    /// against this and rebuilds <see cref="Results"/>.</summary>
    private List<ScoredFunctionRow> _allRows = new();

    [ObservableProperty] private bool   _gameOnly = true;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private FunctionCategory? _categoryFilter; // null = All
    [ObservableProperty] private bool   _showAll;     // false = hide rows below threshold
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText = "Click Load to scan all UFunctions";
    [ObservableProperty] private ObservableCollection<ScoredFunctionRow> _results = new();
    [ObservableProperty] private ScoredFunctionRow? _selectedResult;

    /// <summary>True when the AOBMaker CE Plugin pipe responded to the
    /// last availability check. Defaults true so the UI doesn't gray
    /// out before we've had a chance to probe; first
    /// <see cref="CheckAobMakerAsync"/> on Load updates it.</summary>
    [ObservableProperty] private bool _isAobMakerAvailable = true;

    // Static category list used by the chip dropdown (in chip-display order).
    public IReadOnlyList<FunctionCategory?> CategoryOptions { get; } = new FunctionCategory?[]
    {
        null,                          // "All"
        FunctionCategory.Stats,
        FunctionCategory.Inventory,
        FunctionCategory.Movement,
        FunctionCategory.Combat,
        FunctionCategory.Utility,
        FunctionCategory.Other,
    };

    // ------------------------------------------------------------------
    // Cross-tab navigation events. MainWindowViewModel subscribes and
    // routes to the right destination panel. Two events:
    //
    // - NavigateToFunction(className, funcName): try to find a live
    //   non-CDO instance of `className`; on hit, switch to LiveWalker
    //   tab + walk to instance + auto-scroll to `funcName` row. On
    //   miss, switch to ClassStruct tab + walk class metadata.
    // - RequestCopyBakedScript(className, funcName): fetch full
    //   FunctionInfoModel via walk_functions, then open the Copy AA
    //   Script dialog (or fast-path no-arg generation).
    // ------------------------------------------------------------------

    public event Action<string, string>? NavigateToFunction;
    public event Action<string, string>? RequestCopyBakedScript;

    public InterestingFunctionsViewModel(IDumpService dump, ILoggingService log,
                                          IAobMakerBridge? aobMaker = null)
    {
        _dump = dump;
        _log = log;
        _aobMaker = aobMaker;
    }

    /// <summary>
    /// Per-row hint shown in the Notes column. Same value across every
    /// row in the current grid -- the column is per-row in the AXAML
    /// layout but the data is VM-level. When AOBMaker is unavailable
    /// the AA(B) shortcut still works (clipboard fallback) but the
    /// CE-side workflow is degraded; this surfaces that to the user
    /// without burying it in a tooltip.
    /// </summary>
    public string AobMakerNote => IsAobMakerAvailable
        ? ""
        : "AOBMaker plugin not found — AA Script export will fall back to clipboard";

    // Filter inputs all funnel into a single ApplyFilter pass. Each
    // partial method fires once per ObservableProperty change.
    partial void OnFilterTextChanged(string value)         => ApplyFilter();
    partial void OnCategoryFilterChanged(FunctionCategory? value) => ApplyFilter();
    partial void OnShowAllChanged(bool value)              => ApplyFilter();
    partial void OnIsAobMakerAvailableChanged(bool value)
        => OnPropertyChanged(nameof(AobMakerNote));

    /// <summary>
    /// Probe AOBMaker pipe and update <see cref="IsAobMakerAvailable"/>.
    /// Runs the bridge's CheckAvailabilityAsync (2s pipe-connect timeout
    /// when CE not running) so cheap to call on tab activation.
    /// </summary>
    public async Task CheckAobMakerAsync()
    {
        if (_aobMaker == null) { IsAobMakerAvailable = false; return; }
        _lastAobMakerCheck = DateTime.UtcNow;
        try
        {
            IsAobMakerAvailable = await _aobMaker.CheckAvailabilityAsync();
        }
        catch
        {
            IsAobMakerAvailable = false;
        }
    }

    /// <summary>
    /// Fire-and-forget AOBMaker availability check with cooldown so
    /// rapid tab switches don't stack 2s-blocking pipe-connect attempts.
    /// Called from MainWindow's tab-switch handler.
    /// </summary>
    public void TryCheckAobMaker()
    {
        if (_aobMaker == null) return;
        if (DateTime.UtcNow - _lastAobMakerCheck < AobMakerCheckCooldown) return;
        _ = CheckAobMakerAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            ClearError();
            IsLoading = true;
            StatusText = "Loading all UFunctions (this can take 2-10s on large games)...";

            var result = await _dump.ListAllFunctionsAsync(gameOnly: GameOnly);

            // Score on the worker thread to keep the UI responsive when
            // the result set is in the tens-of-thousands range.
            _allRows = await Task.Run(() =>
            {
                var rows = new List<ScoredFunctionRow>(result.Functions.Count);
                foreach (var entry in result.Functions)
                {
                    var s = KeywordScoringTable.Score(entry);
                    rows.Add(new ScoredFunctionRow
                    {
                        Entry       = entry,
                        FinalScore  = s.FinalScore,
                        Category    = s.Category,
                        KeywordHits = s.KeywordHits,
                        ClassBonus  = s.ClassBonus,
                        FlagBonus   = s.FlagBonus,
                    });
                }
                // Sort once after scoring; ApplyFilter preserves order
                // without re-sorting since it walks the pre-sorted list.
                rows.Sort((a, b) =>
                {
                    int cmp = b.FinalScore.CompareTo(a.FinalScore);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(a.ClassName, b.ClassName, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    return string.Compare(a.FuncName, b.FuncName, StringComparison.Ordinal);
                });
                return rows;
            });

            ApplyFilter();

            int interesting = 0;
            foreach (var r in _allRows)
                if (r.FinalScore >= KeywordScoringTable.InterestingThreshold) interesting++;

            StatusText = $"{result.Total} functions across {result.ScannedClasses} classes  " +
                         $"({interesting} above threshold {KeywordScoringTable.InterestingThreshold}, " +
                         $"scanned {result.ScannedObjects:N0} objects)";
            _log.Info($"ListAllFunctions: total={result.Total} classes={result.ScannedClasses} " +
                      $"interesting={interesting} (gameOnly={GameOnly})");

            // Refresh AOBMaker state at end of load so the Notes column
            // reflects current connectivity. Doesn't block the load --
            // status text above already updated.
            await CheckAobMakerAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Load failed";
            _log.Error("ListAllFunctions failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Rebuild <see cref="Results"/> from <see cref="_allRows"/> applying
    /// name + category + threshold filters. Order is preserved from the
    /// pre-sorted full list (Score desc, then Class, then Func) so the
    /// DataGrid doesn't need to re-sort.
    /// </summary>
    private void ApplyFilter()
    {
        Results.Clear();
        if (_allRows.Count == 0) return;

        var nameFilter = (FilterText ?? "").Trim();
        var hasName    = nameFilter.Length > 0;
        var cat        = CategoryFilter;
        var threshold  = ShowAll ? int.MinValue : KeywordScoringTable.InterestingThreshold;

        foreach (var row in _allRows)
        {
            if (row.FinalScore < threshold) continue;
            if (cat.HasValue && row.Category != cat.Value) continue;
            if (hasName)
            {
                // Match against function name OR class name (cheat-table
                // workflow: user usually remembers either "AddMoney" or
                // "PlayerInventory" — substring against both keeps both
                // entry points working).
                if (!row.FuncName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                    && !row.ClassName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            Results.Add(row);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterText      = "";
        CategoryFilter  = null;
        ShowAll         = false;
    }

    /// <summary>Per-row action: fire navigate event so MainWindow can
    /// try LiveWalker (with find_instance) or fall back to ClassStruct.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(ScoredFunctionRow? row)
    {
        if (row == null) return;
        NavigateToFunction?.Invoke(row.ClassName, row.FuncName);
    }

    /// <summary>Per-row action: shortcut into the Copy AA Script flow
    /// without going through LiveWalker first. MainWindow handler
    /// fetches the full FunctionInfoModel via walk_functions then
    /// opens InvokeParamDialog in CopyBakedScript mode.</summary>
    [RelayCommand]
    private void CopyAaScript(ScoredFunctionRow? row)
    {
        if (row == null) return;
        RequestCopyBakedScript?.Invoke(row.ClassName, row.FuncName);
    }
}
