using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Console panel — surface every UFUNCTION(exec)
/// reachable in the target game and provide a one-click invoke flow.
///
/// Why this exists: many games ship debug / cheat entry points marked
/// with the UE <c>exec</c> specifier (FUNC_Exec = 0x00000200). The cooker
/// preserves these in Shipping builds (often inside UCheatManager
/// subclasses), so the developer's own `fly`, `ghost`, `god`,
/// `setspeed` etc. remain reachable at runtime. Discovering and
/// invoking them sidesteps the entire "find UFunction → build
/// ParamBuffer → invoke" workflow — the game author has already
/// curated the cheat surface for us.
///
/// Load uses the existing <c>list_all_functions</c> pipe command (same
/// payload as Interesting Funcs) and filters client-side to entries
/// with <see cref="AllFunctionEntry.IsExec"/> == true. GameOnly defaults
/// to <b>false</b> because UCheatManager and many engine-side exec
/// classes live in /Script/Engine; filtering them out by default would
/// hide most hits.
///
/// Run uses the existing <see cref="IDumpService.InvokeFunctionAsync"/>
/// path with classname-only resolution (DLL finds the first non-CDO
/// instance). Works without a ParamBuffer for no-arg exec commands —
/// the common case. For exec commands with parameters, the Run path
/// raises <see cref="RequestParameterInvoke"/> so MainWindow can open
/// the standard InvokeParamDialog flow.
/// </summary>
public partial class ConsoleViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;

    /// <summary>Full unfiltered list of exec UFunctions returned by the
    /// most recent Load. Filter operates against this and rebuilds
    /// <see cref="Results"/>.</summary>
    private List<AllFunctionEntry> _allExec = new();

    /// <summary>Cap on history entries — keeps the tail readable + the
    /// AOT-trimmed binary's ObservableCollection bounded. Old entries
    /// drop off the front when this is exceeded.</summary>
    private const int MaxHistoryEntries = 20;

    [ObservableProperty] private bool   _gameOnly = false;     // exec funcs often engine-side
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText =
        "Click Load to discover UFUNCTION(exec) commands in this game.";
    [ObservableProperty] private ObservableCollection<AllFunctionEntry> _results = new();
    [ObservableProperty] private AllFunctionEntry? _selectedResult;
    [ObservableProperty] private ObservableCollection<ConsoleHistoryEntry> _history = new();
    [ObservableProperty] private string _commandInput = "";

    /// <summary>
    /// Raised when the user activates an exec command that has parameters
    /// (NumParms &gt; 0). MainWindow handler fetches full FunctionInfoModel
    /// via walk_functions then opens InvokeParamDialog so the user can
    /// supply values. No-arg exec commands run directly via
    /// <see cref="IDumpService.InvokeFunctionAsync"/> and don't fire this
    /// event.
    /// </summary>
    public event Action<string, string>? RequestParameterInvoke;

    /// <summary>Per-row "Live" action — same contract as Interesting
    /// Funcs. MainWindow tries find_instance → LiveWalker → ClassStruct
    /// fallback.</summary>
    public event Action<string, string>? NavigateToFunction;

    /// <summary>Per-row "AA(B)" action — same contract as Interesting
    /// Funcs. MainWindow handler fetches params + opens InvokeParamDialog
    /// in CopyBakedScript mode.</summary>
    public event Action<string, string>? RequestCopyBakedScript;

    public ConsoleViewModel(IDumpService dump, ILoggingService log)
    {
        _dump = dump;
        _log = log;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// When the user selects a row, refresh the
    /// <see cref="SelectedExecHint"/> binding so the cooker-strip
    /// warning footer toggles per-selection. Single notification —
    /// the hint is computed on demand from <see cref="SelectedResult"/>
    /// to keep this side-effect-free.
    /// </summary>
    partial void OnSelectedResultChanged(AllFunctionEntry? value)
        => OnPropertyChanged(nameof(SelectedExecHint));

    /// <summary>
    /// Footer-line hint shown below the status row when the currently-
    /// selected exec is likely to be one of the
    /// <c>#if !UE_BUILD_SHIPPING</c> stripped engine commands
    /// (UCheatManager::Fly/Ghost/God/Walk/Slomo/ChangeSize/Teleport
    /// etc., or a game-defined subclass). Empty otherwise — the panel
    /// binds visibility to non-empty.
    ///
    /// Detection is a cheap class-name / super-name substring match
    /// against "CheatManager"; catches the canonical engine class +
    /// the typical game-defined subclasses
    /// (<c>MyGameCheatManager</c>, <c>BP_CheatManager_C</c>) without
    /// needing a full super-chain walk. See memory
    /// <c>feedback_ucheatmanager_stripped</c> for the diagnostic
    /// rationale.
    /// </summary>
    public string SelectedExecHint
    {
        get
        {
            if (SelectedResult is null) return "";
            return IsLikelyUCheatManagerExec(SelectedResult)
                ? "⚠ UCheatManager subclasses are often body-stripped in cooked Shipping " +
                  "(Result=0 + no in-game effect). Try a game-specific exec or BC " +
                  "function for verification. See memory feedback_ucheatmanager_stripped."
                : "";
        }
    }

    /// <summary>
    /// True when <paramref name="entry"/>'s class or immediate super
    /// name contains the substring "CheatManager" (case-insensitive).
    /// Catches the engine class, game-defined subclasses, and BPGCs.
    /// Public + static for direct test coverage so the heuristic stays
    /// regression-proof without needing a VM lifecycle.
    /// </summary>
    public static bool IsLikelyUCheatManagerExec(AllFunctionEntry entry)
    {
        if (entry is null) return false;
        const string needle = "CheatManager";
        bool ClassMatches(string s) => !string.IsNullOrEmpty(s)
            && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
        return ClassMatches(entry.ClassName) || ClassMatches(entry.SuperName);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            ClearError();
            IsLoading = true;
            StatusText = "Scanning all UFunctions for exec flag (FUNC_Exec=0x200)...";

            var result = await _dump.ListAllFunctionsAsync(gameOnly: GameOnly);

            _allExec = await Task.Run(() =>
            {
                var list = new List<AllFunctionEntry>();
                foreach (var entry in result.Functions)
                {
                    if (entry.IsExec) list.Add(entry);
                }
                // Sort by Class then Func for deterministic UI ordering;
                // exec commands have no scoring concept (the engine flag
                // IS the curation signal).
                list.Sort((a, b) =>
                {
                    int cmp = string.Compare(a.ClassName, b.ClassName,
                                              StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    return string.Compare(a.FuncName, b.FuncName,
                                           StringComparison.Ordinal);
                });
                return list;
            });

            ApplyFilter();

            if (_allExec.Count == 0)
            {
                StatusText = result.Total > 0
                    ? $"No UFUNCTION(exec) commands found in this game " +
                      $"(scanned {result.Total:N0} functions across " +
                      $"{result.ScannedClasses:N0} classes). The cooker " +
                      $"may have stripped them, or the game doesn't use " +
                      $"this pattern."
                    : "Load returned 0 functions — pipe issue?";
            }
            else
            {
                StatusText = $"{_allExec.Count} exec commands discovered " +
                             $"({result.ScannedClasses:N0} classes scanned, " +
                             $"{result.Total:N0} total UFunctions).";
            }

            _log.Info($"Console.Load: exec={_allExec.Count} of total={result.Total} " +
                      $"(gameOnly={GameOnly}, scanned={result.ScannedObjects:N0} objects)");
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Load failed";
            _log.Error("Console.Load failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Rebuild <see cref="Results"/> from <see cref="_allExec"/> applying
    /// the name substring filter. Order is preserved from the pre-sorted
    /// underlying list.
    /// </summary>
    private void ApplyFilter()
    {
        Results.Clear();
        if (_allExec.Count == 0) return;

        var nameFilter = (FilterText ?? "").Trim();
        var hasName    = nameFilter.Length > 0;

        foreach (var entry in _allExec)
        {
            if (hasName)
            {
                if (!entry.FuncName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                    && !entry.ClassName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            Results.Add(entry);
        }
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = "";
    }

    /// <summary>
    /// Run the currently selected exec command. No-arg commands invoke
    /// directly via <see cref="IDumpService.InvokeFunctionAsync"/>;
    /// commands with parameters raise <see cref="RequestParameterInvoke"/>
    /// so MainWindow can open the InvokeParamDialog flow.
    /// </summary>
    [RelayCommand]
    private async Task RunSelectedAsync()
    {
        var row = SelectedResult;
        if (row == null)
        {
            StatusText = "Pick an exec command from the list first.";
            return;
        }
        await RunEntryAsync(row);
    }

    /// <summary>
    /// Run the command typed in the textbox. Resolves against the
    /// discovered exec list by exact funcName match (case-insensitive).
    /// If multiple classes implement the same exec name (rare but
    /// possible), takes the first hit by class-then-func sort order.
    ///
    /// This is the "type the command and press Enter" workflow,
    /// equivalent to a UE in-game console line.
    /// </summary>
    [RelayCommand]
    private async Task RunCommandTextAsync()
    {
        var raw = (CommandInput ?? "").Trim();
        if (raw.Length == 0)
        {
            StatusText = "Type a command name first (e.g. 'fly' or 'god').";
            return;
        }

        // Strip optional leading slash so users typing "/fly" still work.
        if (raw.StartsWith('/')) raw = raw.Substring(1);

        // For v1 we only support no-arg matching. If the user types
        // "setspeed 5" we still resolve "setspeed" but warn that args
        // can't be parsed yet — they need to use the Run-selected flow
        // which opens InvokeParamDialog for typed params.
        var firstSpace = raw.IndexOf(' ');
        var commandName = firstSpace > 0 ? raw.Substring(0, firstSpace) : raw;
        var hasInlineArgs = firstSpace > 0;

        AllFunctionEntry? match = null;
        foreach (var entry in _allExec)
        {
            if (string.Equals(entry.FuncName, commandName,
                              StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                break;
            }
        }

        if (match == null)
        {
            StatusText = $"No exec command named '{commandName}' " +
                         $"(load the list first, or check spelling).";
            return;
        }

        if (hasInlineArgs && match.NumParms > 0)
        {
            StatusText = $"'{commandName}' takes {match.NumParms} param(s); " +
                         $"inline args not yet supported — opening param dialog.";
            RequestParameterInvoke?.Invoke(match.ClassName, match.FuncName);
            return;
        }

        await RunEntryAsync(match);
    }

    /// <summary>
    /// Core run path — chosen entry → either direct invoke (no-arg) or
    /// param dialog (has args). Records the outcome in
    /// <see cref="History"/>.
    /// </summary>
    private async Task RunEntryAsync(AllFunctionEntry entry)
    {
        if (entry.NumParms > 0)
        {
            StatusText = $"{entry.FuncName} takes {entry.NumParms} param(s) — " +
                         $"opening param dialog.";
            RequestParameterInvoke?.Invoke(entry.ClassName, entry.FuncName);
            return;
        }

        try
        {
            ClearError();
            IsRunning = true;
            StatusText = $"Invoking {entry.ClassName}::{entry.FuncName}…";

            var result = await _dump.InvokeFunctionAsync(
                funcName: entry.FuncName,
                instanceAddr: null,
                className: entry.ClassName,
                parmsSize: 0,
                paramsHex: null,
                directCall: false);

            var resultText = result.Success
                ? (string.IsNullOrEmpty(result.Message) ? "OK" : result.Message)
                : (string.IsNullOrEmpty(result.Error)
                    ? $"Result code {result.Result}"
                    : result.Error);

            AppendHistory(entry, result.Success, resultText);

            StatusText = result.Success
                ? $"✓ {entry.FuncName}: {resultText}"
                : $"✗ {entry.FuncName}: {resultText}";

            _log.Info($"Console.Run: {entry.ClassName}::{entry.FuncName} → " +
                      $"success={result.Success}, code={result.Result}");
        }
        catch (Exception ex)
        {
            AppendHistory(entry, success: false, ex.Message);
            SetError(ex);
            StatusText = $"✗ {entry.FuncName} failed: {ex.Message}";
            _log.Error($"Console.Run threw: {entry.ClassName}::{entry.FuncName}", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void AppendHistory(AllFunctionEntry entry, bool success, string resultText)
    {
        History.Insert(0, new ConsoleHistoryEntry
        {
            When       = DateTime.Now,
            ClassName  = entry.ClassName,
            FuncName   = entry.FuncName,
            Success    = success,
            ResultText = resultText,
        });
        while (History.Count > MaxHistoryEntries)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    /// <summary>Per-row "Live" — try find_instance + open in LiveWalker;
    /// MainWindow falls back to ClassStruct on CDO-only classes.</summary>
    [RelayCommand]
    private void OpenInLiveWalker(AllFunctionEntry? row)
    {
        if (row == null) return;
        NavigateToFunction?.Invoke(row.ClassName, row.FuncName);
    }

    /// <summary>Per-row "AA(B)" — shortcut into the Copy AA Script flow,
    /// same contract as Interesting Funcs.</summary>
    [RelayCommand]
    private void CopyAaScript(AllFunctionEntry? row)
    {
        if (row == null) return;
        RequestCopyBakedScript?.Invoke(row.ClassName, row.FuncName);
    }

    /// <summary>Re-run a history entry — looks up the same class+func in
    /// the discovered list and invokes again. If the load list has been
    /// cleared (re-launch), surfaces a hint to reload.</summary>
    [RelayCommand]
    private async Task ReplayHistoryAsync(ConsoleHistoryEntry? entry)
    {
        if (entry == null) return;
        AllFunctionEntry? match = null;
        foreach (var e in _allExec)
        {
            if (e.ClassName == entry.ClassName && e.FuncName == entry.FuncName)
            {
                match = e;
                break;
            }
        }
        if (match == null)
        {
            StatusText = $"Can't replay {entry.FuncName} — reload the list first.";
            return;
        }
        await RunEntryAsync(match);
    }

    /// <summary>Test seam — unit tests build a list of fixture
    /// AllFunctionEntry rows + a stub dump service, then call this to
    /// drive the same code path Load would after a real pipe call.
    /// Keeps tests free of the gameOnly/IsLoading state-machine
    /// concerns.</summary>
    public void SeedForTests(IEnumerable<AllFunctionEntry> entries)
    {
        var list = new List<AllFunctionEntry>();
        foreach (var e in entries)
        {
            if (e.IsExec) list.Add(e);
        }
        list.Sort((a, b) =>
        {
            int cmp = string.Compare(a.ClassName, b.ClassName,
                                      StringComparison.Ordinal);
            if (cmp != 0) return cmp;
            return string.Compare(a.FuncName, b.FuncName,
                                   StringComparison.Ordinal);
        });
        _allExec = list;
        ApplyFilter();
    }
}
