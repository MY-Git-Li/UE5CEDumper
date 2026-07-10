using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Helpers;
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
    private readonly IPlatformService? _platform;

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
    [ObservableProperty] private bool _isXrefBatchRunning;

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

    /// <summary>Raised by the per-row "inst" button to open the function's class
    /// in the Instance Finder tab. Payload = class name.</summary>
    public event Action<string>? NavigateToInstanceFinder;

    /// <summary>Raised to locate a live instance of the function's class within
    /// the GWorld object graph. Payload = class name (MainWindow resolves a
    /// representative instance via find_instance, then runs the path search).</summary>
    public event Action<string>? LocateInGWorld;

    /// <summary>Engine-rooted counterpart of <see cref="LocateInGWorld"/> (path search
    /// rooted at the live UGameEngine). Payload = class name.</summary>
    public event Action<string>? LocateInGameEngine;

    /// <summary>True when GWorld is available — gates the per-row "Locate in GWorld" button.</summary>
    [ObservableProperty] private bool _isGWorldAvailable;

    /// <summary>Raised when the user clicks "Generate Cheat Table" on
    /// the multi-select toolbar. Subscribers (MainWindow) open the
    /// save dialog and write the payload — IO out of the VM keeps the
    /// command testable.</summary>
    public event Action<string /*defaultFileName*/, string /*ctXml*/>? RequestSaveCheatTable;

    /// <summary>
    /// Raised when a row's "Name" button is clicked. MainWindow handler
    /// hands the string to the platform clipboard service. Keeps this VM
    /// free of an IPlatformService dependency so the test stubs stay tiny.
    /// </summary>
    public event Action<string>? RequestCopyText;

    /// <summary>Client-side class-noise filter: hides ticked classes (UI widgets,
    /// sound, system components) from <see cref="Results"/>. Lives over the full
    /// <see cref="_allRows"/> set; ticking re-runs <see cref="ApplyFilter"/>.</summary>
    public ClassFacetFilter ClassFilter { get; }

    /// <summary>Live "N hidden by class filter" note (empty when nothing hidden) —
    /// lets the user tell an empty grid caused by a stale class exclusion from a
    /// genuine miss. Recomputed by <see cref="ApplyFilter"/>.</summary>
    [ObservableProperty] private string _classFilterNote = "";

    /// <summary>Per-session remembered filter keywords (LRU) surfaced as the
    /// filter box's AutoCompleteBox suggestions — see <see cref="KeywordSearchMemory"/>.</summary>
    private readonly KeywordSearchMemory _filterMemory;
    public ObservableCollection<string> FilterHistory => _filterMemory.History;

    public InterestingFunctionsViewModel(IDumpService dump, ILoggingService log,
                                          IAobMakerBridge? aobMaker = null,
                                          IPlatformService? platform = null)
    {
        _dump = dump;
        _log = log;
        _aobMaker = aobMaker;
        _platform = platform;
        _filterMemory = new KeywordSearchMemory(() => (FilterText, Results.Count > 0));
        ClassFilter = new ClassFacetFilter(ApplyFilter)
        {
            AutoDetectProvider = async names =>
                (await _dump.DetectNoiseClassesAsync(names))
                    .Select(n => (n.ClassName, n.IsNoise, n.Reason)).ToList(),
        };
    }

    /// <summary>
    /// Reverse edge: "Props" row action — list the properties this function
    /// reads/writes (static bytecode scan). Opens a self-contained dialog
    /// (no tab impact).
    /// </summary>
    [RelayCommand]
    private async Task FindFuncPropsAsync(ScoredFunctionRow? row)
    {
        row ??= SelectedResult;
        if (row == null || string.IsNullOrEmpty(row.FuncAddr)) return;
        try
        {
            await Views.FunctionPropsDialog.ShowForFunctionAsync(
                row.FuncName, row.FuncAddr, _dump, _platform);
        }
        catch (Exception ex)
        {
            _log.Error($"FindFuncProps failed for {row.FuncName}", ex);
        }
    }

    // Props direction is cheap (one function's own bytecode per call), so warn
    // only past a high row count.
    private CancellationTokenSource? _xrefBatchCts;
    private const int XrefBatchWarnThreshold = 100;

    /// <summary>
    /// Batch "Props": run walk_function_props for the selected rows (or all
    /// current results if none selected) and write a compact "N · name1, name2"
    /// summary into each row's inline <see cref="ScoredFunctionRow.XrefInfo"/>
    /// cell — so the user sees the (often 0) result inline instead of opening
    /// the modal per row. Cancellable; only class-member fields are counted.
    /// </summary>
    [RelayCommand]
    private async Task BatchFindFuncPropsAsync(IList<ScoredFunctionRow>? selected)
    {
        var targets = (selected != null && selected.Count > 0)
            ? selected.ToList()
            : Results.ToList();
        if (targets.Count == 0) { StatusText = "No rows to scan — Load first."; return; }

        if (targets.Count > XrefBatchWarnThreshold)
        {
            var ok = await Views.ConfirmDialog.ShowAsync(
                "Batch: properties used",
                $"Analyse {targets.Count} functions? Each reads one function's own "
              + "bytecode (fast). Tip: select fewer rows first (Ctrl/Shift+click) to scope it.",
                confirmText: "Run");
            if (!ok) return;
        }

        _xrefBatchCts?.Cancel();
        _xrefBatchCts = new CancellationTokenSource();
        var ct = _xrefBatchCts.Token;
        IsXrefBatchRunning = true;
        int done = 0, withFields = 0, cached = 0;
        try
        {
            foreach (var row in targets)
            {
                ct.ThrowIfCancellationRequested();
                // Skip rows already scanned (XrefInfo persists across filter changes
                // since the row instance is reused) — re-batch after a new filter only
                // does the newly-included, not-yet-done rows.
                if (!string.IsNullOrEmpty(row.XrefInfo)) { cached++; continue; }
                if (string.IsNullOrEmpty(row.FuncAddr)) { row.XrefInfo = "—"; continue; }
                try
                {
                    var res = await _dump.WalkFunctionPropsAsync(row.FuncAddr, ct);
                    var fields = res.Props.Where(p => p.IsClassField).ToList();
                    if (fields.Count > 0)
                    {
                        withFields++;
                        var preview = string.Join(", ", fields.Take(2).Select(p => p.Name));
                        row.XrefInfo = fields.Count > 2 ? $"{fields.Count} · {preview}, …"
                                                        : $"{fields.Count} · {preview}";
                    }
                    else row.XrefInfo = "0";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    row.XrefInfo = "—";
                    _log.Error($"Batch props failed for {row.FuncName}", ex);
                }
                done++;
                if ((done & 0x7) == 0)
                    StatusText = $"Props: {done}/{targets.Count} scanned ({withFields} use class fields)…";
            }
            StatusText = $"Props done: {withFields}/{targets.Count} use class fields"
                       + (cached > 0 ? $" ({cached} cached)." : ".");
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Props batch cancelled at {done}/{targets.Count}.";
        }
        finally { IsXrefBatchRunning = false; }
    }

    /// <summary>Cancel an in-flight batch Props run.</summary>
    [RelayCommand]
    private void CancelXrefBatch() => _xrefBatchCts?.Cancel();

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
    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
        _filterMemory.Schedule(value);
    }
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

            // Build the class histogram over the FULL scored set, then filter.
            ClassFilter.Rebuild(_allRows.Select(r => r.ClassName));
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
        SelectedResult = null;   // detach before rebuilding the selection-bound list
        Results.Clear();
        if (_allRows.Count == 0) return;

        // Space-separated terms are ANDed (each must hit FuncName or ClassName),
        // so "add money" narrows to entries matching both — the shared Object Tree
        // filter semantics. Cheat-table workflow: user usually remembers either
        // "AddMoney" or "PlayerInventory", and either term keeps both entry points.
        var terms      = ObjectTreeFilter.SplitTerms(FilterText);
        var cat        = CategoryFilter;
        var threshold  = ShowAll ? int.MinValue : KeywordScoringTable.InterestingThreshold;

        int hiddenByClass = 0;
        foreach (var row in _allRows)
        {
            if (row.FinalScore < threshold) continue;
            if (cat.HasValue && row.Category != cat.Value) continue;
            if (terms.Length > 0 &&
                !ObjectTreeFilter.MatchesAllTerms(terms, row.FuncName, row.ClassName))
            {
                continue;
            }
            // Class-noise exclusion last, so the count reflects rows that would
            // otherwise be visible.
            if (ClassFilter.IsExcluded(row.ClassName)) { hiddenByClass++; continue; }
            Results.Add(row);
        }
        ClassFilterNote = hiddenByClass > 0 ? $"{hiddenByClass} hidden by class filter" : "";
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

    /// <summary>Per-row "inst" action: open this function's class in the
    /// Instance Finder tab (pre-fill the class name + run the search). Mirrors
    /// the Property Search / Value Search "inst" handoff.</summary>
    [RelayCommand]
    private void OpenInInstanceFinder(ScoredFunctionRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ClassName)) return;
        NavigateToInstanceFinder?.Invoke(row.ClassName);
    }

    /// <summary>Locate a live instance of this function's class within the GWorld
    /// graph (parent mode — stops at the object that points to the instance).</summary>
    [RelayCommand]
    private void LocateRowInGWorld(ScoredFunctionRow? row)
    {
        if (row == null || !IsGWorldAvailable || string.IsNullOrEmpty(row.ClassName)) return;
        LocateInGWorld?.Invoke(row.ClassName);
    }

    /// <summary>Engine-rooted counterpart — not gated on IsGWorldAvailable (engine
    /// availability is independent of GWorld; the DLL reports no_engine via the banner).</summary>
    [RelayCommand]
    private void LocateRowInGameEngine(ScoredFunctionRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ClassName)) return;
        LocateInGameEngine?.Invoke(row.ClassName);
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

    /// <summary>Per-row action: copy the function name (no class prefix)
    /// to the clipboard. MainWindow handler routes this through the
    /// platform service so AOT / clipboard fallback semantics live in
    /// one place. Bare name (not Class::Func) keeps it pasteable into
    /// CE script editors and grep workflows directly.</summary>
    [RelayCommand]
    private void CopyFunctionName(ScoredFunctionRow? row)
    {
        if (row == null) return;
        if (string.IsNullOrEmpty(row.FuncName)) return;
        RequestCopyText?.Invoke(row.FuncName);
        StatusText = $"Copied function name: {row.FuncName}";
    }

    // ------------------------------------------------------------------
    // Multi-row batch: turn the user's current DataGrid selection into a
    // single CT file (one baked-invoke entry per row, grouped by
    // Category). For functions with parameters the BakedValues list is
    // intentionally empty — the generated script's helper zero-fills the
    // PARAMS buffer, and the user edits the baked PARAMS table in CE
    // before activating. The script header comment guides them.
    // ------------------------------------------------------------------

    /// <summary>Build the CT batch from <paramref name="selected"/>.
    /// Public + static for direct test coverage (no need to stand up
    /// the full VM to verify the row mapping).</summary>
    public static List<CheatTableRow> BuildRowsFromSelection(
        IEnumerable<ScoredFunctionRow> selected)
    {
        var rows = new List<CheatTableRow>();
        foreach (var sr in selected)
        {
            if (sr is null) continue;
            if (string.IsNullOrEmpty(sr.ClassName) || string.IsNullOrEmpty(sr.FuncName))
                continue;

            string desc = sr.NumParms == 0
                ? $"{sr.ClassName}::{sr.FuncName}()"
                : $"{sr.ClassName}::{sr.FuncName}  ({sr.ParamsLabel})  -- edit baked PARAMS in CE";

            rows.Add(new CtFunctionRow
            {
                Category    = KeywordScoringTable.DisplayName(sr.Category),
                Description = desc,
                ClassName   = sr.ClassName,
                FuncName    = sr.FuncName,
                ParmsSize   = sr.ParmsSize,
                // No baked values for batch entries -- user fills the
                // PARAMS table in CE per-row before activating. The
                // helper zero-fills the buffer so the script is safe
                // to enable accidentally (worst case = func runs with
                // all-zero args).
                BakedValues = Array.Empty<BakedParamValue>(),
            });
        }
        return rows;
    }

    /// <summary>"Generate Cheat Table from Selection" command. Same
    /// shape as the Interesting Properties sibling: VM stays IO-free,
    /// MainWindow handles the save dialog.</summary>
    [RelayCommand]
    private void GenerateCheatTable(IList<ScoredFunctionRow>? selected)
    {
        if (selected is null || selected.Count == 0)
        {
            StatusText = "Select 2+ rows first (Ctrl/Shift+click).";
            return;
        }

        var rows = BuildRowsFromSelection(selected);
        if (rows.Count == 0)
        {
            StatusText = "Selection produced 0 valid rows — nothing to write.";
            return;
        }

        var now = DateTime.Now;
        string title = $"UE5CEDumper Interesting Functions — " +
                       $"{rows.Count} row{(rows.Count == 1 ? "" : "s")} " +
                       $"@ {now:yyyy-MM-dd HH:mm}";
        string ct = CheatTableBuilder.Build(title, rows);
        string defaultName = CheatTableBuilder.DefaultFileName(
            processName: "InterestingFunctions", now);

        StatusText = $"Generated {rows.Count} invoke entries " +
                     "(edit baked PARAMS in CE for funcs with arguments).";
        RequestSaveCheatTable?.Invoke(defaultName, ct);
    }
}
