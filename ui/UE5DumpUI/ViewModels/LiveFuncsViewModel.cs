using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Live Funcs panel — the Live ProcessEvent Call Profiler.
///
/// Behaviour-based UFunction discovery (the root-cause answer to "which function
/// does this game call to open the shop / dash?", which name heuristics can't):
/// 1. Start → DLL forces the game-thread ProcessEvent hook up and begins counting
///    every UFunction the game dispatches.
/// 2. The user ALT-TABs to the game and performs the action (open shop, dash).
/// 3. Stop → freezes the table; the VM immediately fetches + ranks it by fire count.
///
/// The ranked rows hand off to Live Walker (open the function on a live instance)
/// exactly like the Interesting Functions finder, so the discovered function can be
/// invoked right away.
/// </summary>
public partial class LiveFuncsViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;

    /// <summary>How many top rows to fetch from the DLL (ranked by fire count).</summary>
    private const int FetchLimit = 300;

    /// <summary>Full unfiltered result set — the filter rebuilds <see cref="Results"/> from this.</summary>
    private List<PeProfileEntry> _allEntries = new();

    [ObservableProperty] private bool   _isRecording;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _statusText = "Click Start, do an in-game action (open shop / dash), then Stop.";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private ObservableCollection<PeProfileEntry> _results = new();
    [ObservableProperty] private PeProfileEntry? _selectedResult;

    /// <summary>Per-session remembered filter keywords (LRU) surfaced as the filter
    /// box's AutoCompleteBox suggestions — see <see cref="KeywordSearchMemory"/>.
    /// The match count is client-side and synchronous, so Schedule (not Commit) is
    /// the correct hook.</summary>
    private readonly KeywordSearchMemory _filterMemory;
    public ObservableCollection<string> FilterHistory => _filterMemory.History;

    /// <summary>Raised by the per-row "Live" action so MainWindow can open the
    /// function in Live Walker (find a live non-CDO instance, else Class Struct).
    /// Payload = (className, funcName). Mirrors InterestingFunctionsViewModel.</summary>
    public event Action<string, string>? NavigateToFunction;

    /// <summary>Raised by the per-row "Name" action; MainWindow routes it through
    /// the platform clipboard so this VM stays free of IPlatformService.</summary>
    public event Action<string>? RequestCopyText;

    public LiveFuncsViewModel(IDumpService dump, ILoggingService log, IPlatformService? platform = null)
    {
        _dump = dump;
        _log = log;
        _filterMemory = new KeywordSearchMemory(() => (FilterText, Results.Count > 0));
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
        _filterMemory.Schedule(value);
    }

    /// <summary>Start recording. Forces the game-thread PE hook up first; if it
    /// couldn't install (vtable detection failed on this game), warns that counts
    /// will stay 0.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRecording) return;
        try
        {
            ClearError();
            IsBusy = true;
            bool hookActive = await _dump.PeProfileStartAsync();
            IsRecording = true;
            StatusText = hookActive
                ? "Recording… ALT-TAB to the game, perform the action (open shop / dash), then click Stop."
                : "Recording, but no PE hook installed on this game — counts will stay 0. "
                  + "Try issuing any invoke first (e.g. Teleport → Get POV), then Start again.";
            _log.Info($"LivePEProfiler: start (hook_active={hookActive})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Start failed";
            _log.Error("LivePEProfiler start failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Stop recording, then immediately fetch + rank the fire-count table.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRecording) return;
        try
        {
            ClearError();
            IsBusy = true;
            await _dump.PeProfileStopAsync();
            IsRecording = false;
            await FetchAndPopulateAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Stop failed";
            _log.Error("LivePEProfiler stop failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Re-fetch the current table without stopping — a live peek while
    /// recording, or a re-pull after Stop.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            ClearError();
            IsBusy = true;
            await FetchAndPopulateAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Refresh failed";
            _log.Error("LivePEProfiler refresh failed", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task FetchAndPopulateAsync()
    {
        var result = await _dump.PeProfileGetAsync(FetchLimit);
        _allEntries = result.Entries;
        ApplyFilter();
        StatusText = result.DistinctFuncs == 0
            ? "No UFunctions recorded. Was the game running (unpaused) during the window? "
              + "If it stayed 0, the PE hook may not be installed on this game."
            : $"{result.DistinctFuncs:N0} distinct functions, {result.TotalCalls:N0} total calls"
              + (result.Recording ? " (still recording)" : "") + ".";
    }

    /// <summary>Clear the fetched results + filter (does not touch a live recording).</summary>
    [RelayCommand]
    private void Clear()
    {
        _allEntries = new();
        FilterText = "";
        Results.Clear();
        SelectedResult = null;
        StatusText = "Cleared.";
    }

    /// <summary>Rebuild <see cref="Results"/> from <see cref="_allEntries"/> applying
    /// the name filter (space = AND over func + class name, the shared filter
    /// semantics). Order is preserved (the DLL already ranked by count desc).</summary>
    private void ApplyFilter()
    {
        SelectedResult = null;
        Results.Clear();
        if (_allEntries.Count == 0) return;

        var terms = ObjectTreeFilter.SplitTerms(FilterText);
        foreach (var e in _allEntries)
        {
            if (terms.Length > 0 &&
                !ObjectTreeFilter.MatchesAllTerms(terms, e.FuncName, e.ClassName))
            {
                continue;
            }
            Results.Add(e);
        }
    }

    /// <summary>Per-row "Live" action: open this function on a live instance of its
    /// class in Live Walker (MainWindow does the find_instance → walk handoff).</summary>
    [RelayCommand]
    private void OpenInLiveWalker(PeProfileEntry? row)
    {
        if (row == null || string.IsNullOrEmpty(row.ClassName)) return;
        NavigateToFunction?.Invoke(row.ClassName, row.FuncName);
    }

    /// <summary>Per-row "Name" action: copy the function name to the clipboard.</summary>
    [RelayCommand]
    private void CopyFuncName(PeProfileEntry? row)
    {
        if (row == null || string.IsNullOrEmpty(row.FuncName)) return;
        RequestCopyText?.Invoke(row.FuncName);
        StatusText = $"Copied function name: {row.FuncName}";
    }

    /// <summary>Called when the user navigates away from the Live Funcs tab. Flushes
    /// the keyword memory and auto-stops any live recording so a forgotten session
    /// doesn't keep the game thread taking the profile mutex on every PE call.</summary>
    public void OnLeavingTab()
    {
        _filterMemory.Flush();
        if (IsRecording) _ = AutoStopOnLeaveAsync();
    }

    private async Task AutoStopOnLeaveAsync()
    {
        try { await _dump.PeProfileStopAsync(); }
        catch (Exception ex) { _log.Error("LivePEProfiler auto-stop failed", ex); }
        IsRecording = false;
        StatusText = "Recording auto-stopped (left the tab). Re-open and Refresh to see counts.";
    }
}
