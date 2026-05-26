using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Value Search panel (build 733+, port from
/// discrete Phase 27b shape).
///
/// Workflow:
///   1. User picks DataType + ScanType + Value(s), clicks First Scan.
///   2. DLL walks GObjects + UProperty fields matching DataType,
///      applies predicate, returns enriched candidates + a sessionId.
///   3. User narrows with Next Scan: changes ScanType / value(s) and
///      hits Refine. Prev-value scan types (Changed / Unchanged /
///      Increased / Decreased) compare against candidate snapshots
///      from the previous round.
///   4. New Scan ends the session and resets to step 1.
///
/// Native C++ fields (non-UPROPERTY) are NOT scanned -- this is a
/// hard contract surfaced in the panel's banner. See memory
/// project_value_search_caveats for rationale.
/// </summary>
public partial class ValueSearchViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;

    // ------------------------------------------------------------------
    // Inputs
    // ------------------------------------------------------------------

    [ObservableProperty] private ValueScanDataType _selectedDataType = ValueScanDataType.Int32;
    [ObservableProperty] private ValueScanType     _selectedScanType = ValueScanType.Exact;
    [ObservableProperty] private string _value  = "";
    [ObservableProperty] private string _value2 = "";
    [ObservableProperty] private bool   _gameOnly = true;
    [ObservableProperty] private int    _maxResults = 50000;

    public IReadOnlyList<ValueScanDataType> DataTypeOptions { get; } = new[]
    {
        ValueScanDataType.Int32,
        ValueScanDataType.Int64,
        ValueScanDataType.Int16,
        ValueScanDataType.Int8,
        ValueScanDataType.UInt32,
        ValueScanDataType.UInt64,
        ValueScanDataType.UInt16,
        ValueScanDataType.UInt8,
        ValueScanDataType.Float,
        ValueScanDataType.Double,
        ValueScanDataType.Bool,
    };

    /// <summary>Scan types valid in any context (used by both First and
    /// Next scan). Prev-value scans only become reachable AFTER a
    /// session exists; that constraint is enforced by the
    /// <see cref="HasSession"/> binding on the Next-Scan button.</summary>
    public IReadOnlyList<ValueScanType> ScanTypeOptions { get; } = new[]
    {
        ValueScanType.Exact,
        ValueScanType.Bigger,
        ValueScanType.Smaller,
        ValueScanType.Between,
        ValueScanType.Changed,
        ValueScanType.Unchanged,
        ValueScanType.Increased,
        ValueScanType.Decreased,
    };

    // ------------------------------------------------------------------
    // Output state
    // ------------------------------------------------------------------

    [ObservableProperty] private ulong _sessionId;
    [ObservableProperty] private string _statusText = "Click First Scan to scan for a value across all UPROPERTY fields.";
    [ObservableProperty] private bool   _isScanning;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ObservableCollection<ValueCandidate> _candidates = new();
    [ObservableProperty] private ValueCandidate? _selectedCandidate;

    /// <summary>True when a scan session is active (between First Scan
    /// and New Scan / End). Drives the enablement of the Next Scan
    /// button and the visibility of the New Scan reset button.</summary>
    public bool HasSession => SessionId != 0;
    partial void OnSessionIdChanged(ulong value) => OnPropertyChanged(nameof(HasSession));

    /// <summary>True when the selected ScanType compares against a
    /// previously-observed value (Changed / Unchanged / Increased /
    /// Decreased). The Value / Value2 input boxes are hidden when this
    /// is set so the user doesn't think a value is required.</summary>
    public bool RequiresValueInput => !IsPrevValueScanType(SelectedScanType);

    /// <summary>True when ScanType is Between -- the second value box
    /// becomes visible.</summary>
    public bool RequiresValue2Input => SelectedScanType == ValueScanType.Between;

    partial void OnSelectedScanTypeChanged(ValueScanType value)
    {
        OnPropertyChanged(nameof(RequiresValueInput));
        OnPropertyChanged(nameof(RequiresValue2Input));
    }

    public static bool IsPrevValueScanType(ValueScanType st) => st switch
    {
        ValueScanType.Changed
            or ValueScanType.Unchanged
            or ValueScanType.Increased
            or ValueScanType.Decreased => true,
        _ => false,
    };

    public static bool IsFirstScanType(ValueScanType st) => st switch
    {
        ValueScanType.Exact
            or ValueScanType.Bigger
            or ValueScanType.Smaller
            or ValueScanType.Between => true,
        _ => false,
    };

    // ------------------------------------------------------------------
    // Cross-tab navigation (Open in Live Walker via address)
    // ------------------------------------------------------------------

    public event Action<string>? NavigateToInstance;
    public event Action<string>? RequestCopyText;

    public ValueSearchViewModel(IDumpService dump, ILoggingService log)
    {
        _dump = dump;
        _log  = log;
    }

    [RelayCommand]
    private async Task FirstScanAsync()
    {
        if (IsScanning) return;

        if (!IsFirstScanType(SelectedScanType))
        {
            ErrorMessage = "First Scan only supports Exact / Bigger / Smaller / Between. " +
                           "Use Next Scan for Changed / Unchanged / Increased / Decreased.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            ErrorMessage = "Value is required.";
            return;
        }
        if (SelectedScanType == ValueScanType.Between && string.IsNullOrWhiteSpace(Value2))
        {
            ErrorMessage = "Between requires Value and Value2.";
            return;
        }

        try
        {
            IsScanning  = true;
            ErrorMessage = "";
            StatusText  = $"Scanning {SelectedDataType} fields...";

            // If a session is already open (user changed mind without
            // clicking New Scan), retire it first so the DLL doesn't
            // accumulate orphan sessions.
            await EndSessionIfAnyAsync();

            var result = await _dump.BeginValueScanAsync(
                SelectedDataType, SelectedScanType, Value,
                SelectedScanType == ValueScanType.Between ? Value2 : null,
                GameOnly, MaxResults);

            SessionId = result.SessionId;
            Candidates = new ObservableCollection<ValueCandidate>(result.Candidates);

            var summary = $"First Scan: {result.Total} candidates in {result.DurationMs} ms " +
                          $"(scanned {result.ScannedObjects} objects, " +
                          $"{result.ScannedClasses} classes with matching fields)";
            if (result.DeadlineHit)
                summary += "  ⚠ scan truncated (15s deadline) — narrow predicate to see complete set";
            StatusText = summary;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"First Scan failed: {ex.Message}";
            _log.Error($"ValueSearch First Scan failed", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task NextScanAsync()
    {
        if (IsScanning || !HasSession) return;

        bool needsValue = !IsPrevValueScanType(SelectedScanType);
        if (needsValue && string.IsNullOrWhiteSpace(Value))
        {
            ErrorMessage = "Value is required for this Scan Type.";
            return;
        }
        if (SelectedScanType == ValueScanType.Between && string.IsNullOrWhiteSpace(Value2))
        {
            ErrorMessage = "Between requires Value and Value2.";
            return;
        }

        try
        {
            IsScanning  = true;
            ErrorMessage = "";
            StatusText  = $"Refining ({SelectedScanType})...";

            var result = await _dump.RefineValueScanAsync(
                SessionId, SelectedScanType,
                needsValue ? Value : null,
                SelectedScanType == ValueScanType.Between ? Value2 : null);

            Candidates = new ObservableCollection<ValueCandidate>(result.Candidates);

            StatusText = $"Next Scan ({SelectedScanType}): {result.Total} surviving candidates " +
                         $"in {result.DurationMs} ms";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Next Scan failed: {ex.Message}";
            _log.Error($"ValueSearch Refine failed", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task NewScanAsync()
    {
        await EndSessionIfAnyAsync();
        Candidates = new ObservableCollection<ValueCandidate>();
        StatusText = "Session ended. Configure a new scan and click First Scan.";
        ErrorMessage = "";
    }

    [RelayCommand]
    private void OpenInLiveWalker(ValueCandidate? candidate)
    {
        if (candidate == null) return;
        if (string.IsNullOrEmpty(candidate.InstanceAddr)) return;
        NavigateToInstance?.Invoke(candidate.InstanceAddr);
    }

    [RelayCommand]
    private void CopyAddress(ValueCandidate? candidate)
    {
        if (candidate == null) return;
        if (string.IsNullOrEmpty(candidate.Addr)) return;
        RequestCopyText?.Invoke(candidate.Addr);
    }

    private async Task EndSessionIfAnyAsync()
    {
        if (SessionId == 0) return;
        try
        {
            await _dump.EndValueScanAsync(SessionId);
        }
        catch (Exception ex)
        {
            // Idempotent on the DLL side, but log defensively -- a
            // stale session that fails to end here will time out via
            // the DLL's 5-min idle expiry.
            _log.Warn($"ValueSearch End session {SessionId} failed: {ex.Message}");
        }
        finally
        {
            SessionId = 0;
        }
    }
}
