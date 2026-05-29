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

    /// <summary>CE-style rounded-scan slack for Float/Double and vector
    /// comparisons. Default 0.5 covers the common case: game UI
    /// displays "338" for a real float of 337.5, so scanning for "338"
    /// with tolerance 0.5 matches any value in [337.5, 338.5]. Integer
    /// + string types ignore it.</summary>
    [ObservableProperty] private double _tolerance = 0.5;

    /// <summary>Opt-in case sensitivity for string scans. Default
    /// false matches CE's case-insensitive convention. Has no effect
    /// for non-string DataTypes (the wire serializer strips it).</summary>
    [ObservableProperty] private bool _caseSensitive = false;

    public IReadOnlyList<ValueScanDataType> DataTypeOptions { get; } = new[]
    {
        // Multi-numeric meta scan: one pass over every word/dword/qword/
        // float/double field, comparing per declared width. Listed first
        // because it's the natural "I don't know the stored type yet"
        // starting point. "No byte" excludes 1-byte + bool fields so a
        // small value doesn't explode the candidate set.
        ValueScanDataType.NumericNoByte,
        // Same one-pass scan but INCLUDING 1-byte fields. Listed right
        // after the no-byte variant; the VM shows a result-volume warning
        // when it's selected (small values flood 1-byte fields).
        ValueScanDataType.NumericAll,
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
        // Phase 2A — string types. FText is best-effort (cooked games
        // strip a lot of metadata so display strings may be unresolvable;
        // we keep it as a third option since the UE NameProperty /
        // StrProperty / TextProperty fields are common enough that
        // exposing all three is friendlier than guessing).
        ValueScanDataType.FString,
        ValueScanDataType.FName,
        ValueScanDataType.FText,
        // Phase 2B — vector types. FTransform is wire-stable but
        // currently returns zero hits pending per-version Translation
        // offset detection; deliberately not exposed in the dropdown
        // until that lands.
        ValueScanDataType.FVector,
        ValueScanDataType.FRotator,
    };

    /// <summary>Full set of scan-type values; the panel uses
    /// <see cref="VisibleScanTypeOptions"/> instead so the dropdown
    /// shows only entries valid for the current DataType.</summary>
    private static readonly ValueScanType[] s_allScanTypes = new[]
    {
        ValueScanType.Exact,
        ValueScanType.Bigger,
        ValueScanType.Smaller,
        ValueScanType.Between,
        ValueScanType.Changed,
        ValueScanType.Unchanged,
        ValueScanType.Increased,
        ValueScanType.Decreased,
        ValueScanType.Contains,
        ValueScanType.StartsWith,
        ValueScanType.EndsWith,
    };

    /// <summary>Scan types valid for the current DataType. Numeric +
    /// Vector use ordering predicates; string types use substring
    /// predicates. Mirror of the DLL's <c>IsScanTypeValidFor</c>.</summary>
    public IReadOnlyList<ValueScanType> VisibleScanTypeOptions =>
        FilterScanTypes(SelectedDataType);

    /// <summary>Static helper: filter the global scan-type list by a
    /// given DataType. Public so unit tests can lock the contract
    /// without standing up a ViewModel instance.</summary>
    public static IReadOnlyList<ValueScanType> FilterScanTypes(ValueScanDataType dt)
    {
        return s_allScanTypes.Where(st => IsScanTypeValidFor(dt, st)).ToList();
    }

    /// <summary>True when the (dataType, scanType) pair is a legal
    /// combination. Mirror of DLL <c>ValueScan::IsScanTypeValidFor</c>:
    ///   String : Exact / Contains / StartsWith / EndsWith / Changed / Unchanged
    ///   Vector / Numeric: Exact / Bigger / Smaller / Between / Changed /
    ///                     Unchanged / Increased / Decreased
    /// </summary>
    public static bool IsScanTypeValidFor(ValueScanDataType dt, ValueScanType st)
    {
        bool isString = IsStringDataType(dt);
        bool isSubstring = st == ValueScanType.Contains
                        || st == ValueScanType.StartsWith
                        || st == ValueScanType.EndsWith;
        if (isString)
        {
            return st == ValueScanType.Exact
                || st == ValueScanType.Contains
                || st == ValueScanType.StartsWith
                || st == ValueScanType.EndsWith
                || st == ValueScanType.Changed
                || st == ValueScanType.Unchanged;
        }
        // Numeric + Vector: substring predicates reject; everything
        // else is valid.
        return !isSubstring;
    }

    public static bool IsStringDataType(ValueScanDataType dt) =>
        dt == ValueScanDataType.FString
        || dt == ValueScanDataType.FName
        || dt == ValueScanDataType.FText;

    public static bool IsVectorDataType(ValueScanDataType dt) =>
        dt == ValueScanDataType.FVector
        || dt == ValueScanDataType.FRotator
        || dt == ValueScanDataType.FTransform;

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

    /// <summary>True when the selected DataType uses tolerance --
    /// Float/Double for scalar tolerance, Vector/Rotator for axis-
    /// wise tolerance. Integer + string types hide the UI knob because
    /// the DLL ignores it for those comparisons.</summary>
    public bool SupportsTolerance =>
        SelectedDataType == ValueScanDataType.Float
        || SelectedDataType == ValueScanDataType.Double
        || IsMultiNumericDataType(SelectedDataType)
        || IsVectorDataType(SelectedDataType);

    /// <summary>True for the multi-numeric meta types (NumericNoByte /
    /// NumericAll) that fan out over a fixed member set instead of a
    /// single fixed-width compare. Mirror of DLL
    /// <c>ValueScan::IsMultiNumericDataType</c>.</summary>
    public static bool IsMultiNumericDataType(ValueScanDataType dt) =>
        dt == ValueScanDataType.NumericNoByte
        || dt == ValueScanDataType.NumericAll;

    /// <summary>Non-empty when the selected DataType warrants a caution
    /// to the user. Currently only NumericAll: including 1-byte fields
    /// means small values (0/1/255) match a very large number of fields,
    /// so the candidate set can explode. Empty for every other type
    /// (drives an orange hint TextBlock via IsNotNullOrEmpty).</summary>
    public string DataTypeWarning =>
        SelectedDataType == ValueScanDataType.NumericAll
            ? "NumericAll includes 1-byte fields — small values (e.g. 0 / 1 / 255) " +
              "will match a very large number of fields and can flood the results. " +
              "Use a larger / more specific value, or pick NumericNoByte to skip 1-byte fields."
            : "";

    /// <summary>True when the selected DataType is a string type
    /// (FString / FName / FText) — drives the Case-sensitive checkbox
    /// visibility. Non-string types hide it because the wire layer
    /// strips the flag for them anyway.</summary>
    public bool SupportsCaseSensitive => IsStringDataType(SelectedDataType);

    partial void OnSelectedDataTypeChanged(ValueScanDataType value)
    {
        OnPropertyChanged(nameof(SupportsTolerance));
        OnPropertyChanged(nameof(SupportsCaseSensitive));
        OnPropertyChanged(nameof(DataTypeWarning));
        OnPropertyChanged(nameof(VisibleScanTypeOptions));
        // If the currently-selected ScanType is no longer valid for
        // the new DataType (e.g. user switched from Int32+Bigger to
        // FString+Bigger), snap it to a sensible default that exists
        // in every (DataType, ScanType) matrix cell -- Exact.
        if (!IsScanTypeValidFor(value, SelectedScanType))
        {
            SelectedScanType = ValueScanType.Exact;
        }
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
            or ValueScanType.Between
            or ValueScanType.Contains
            or ValueScanType.StartsWith
            or ValueScanType.EndsWith => true,
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
            ErrorMessage = "First Scan supports targeted predicates only (Exact / " +
                           "Bigger / Smaller / Between / Contains / StartsWith / EndsWith). " +
                           "Use Next Scan for Changed / Unchanged / Increased / Decreased.";
            return;
        }
        if (!IsScanTypeValidFor(SelectedDataType, SelectedScanType))
        {
            ErrorMessage = $"Scan type '{SelectedScanType}' is not valid for {SelectedDataType}.";
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

            // Tolerance only passes through for Float/Double + vector
            // types; integer + string scans get exact-match semantics
            // regardless of the UI value.
            double effTol = SupportsTolerance ? Tolerance : 0.0;
            // Case sensitivity only passes through for string types;
            // wire serializer enforces the same restriction.
            bool effCase = SupportsCaseSensitive && CaseSensitive;
            var result = await _dump.BeginValueScanAsync(
                SelectedDataType, SelectedScanType, Value,
                SelectedScanType == ValueScanType.Between ? Value2 : null,
                GameOnly, MaxResults, effTol, effCase);

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

            double effTol = SupportsTolerance ? Tolerance : 0.0;
            bool effCase = SupportsCaseSensitive && CaseSensitive;
            var result = await _dump.RefineValueScanAsync(
                SessionId, SelectedScanType,
                needsValue ? Value : null,
                SelectedScanType == ValueScanType.Between ? Value2 : null,
                effTol, effCase);

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
