namespace UE5DumpUI.Models;

/// <summary>
/// Data types supported by the Value Search workflow. Wire string
/// matches DLL-side <c>ValueScan::DataType</c> exactly so the pipe
/// round-trip is just <c>nameof(ValueScanDataType.X)</c>.
///
/// Numeric primitives shipped in build 738 (MVP). Phase 2 (build 750)
/// added FString / FName / FText (Phase 2A) and FVector / FRotator
/// (Phase 2B); FTransform is wire-stable but the DLL returns zero
/// candidates pending per-version Translation offset detection. See
/// memory project_value_search_caveats for the TArray&lt;T&gt; gap.
///
/// Native C++ fields (non-UPROPERTY) are unreachable from this scan
/// regardless of data type — the UI Value Search tab MUST surface that
/// limitation in its banner.
/// </summary>
public enum ValueScanDataType
{
    // Numeric primitives (MVP, build 738)
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float,
    Double,
    Bool,
    // String types (Phase 2A, build 750)
    FString,
    FName,
    FText,
    // Vector types (Phase 2B, build 750). FTransform reserved but
    // currently returns zero hits — see VectorStructNames in DLL.
    FVector,
    FRotator,
    FTransform,
    // Multi-numeric meta type (build 794). Scans every word/dword/qword/
    // float/double UPROPERTY in one pass, comparing the value against
    // each field using that field's own declared width — no need to
    // know whether the value is stored as int or float up front. The
    // "no byte" variant excludes 1-byte (Int8/UInt8/Bool) fields to keep
    // the candidate set from exploding on small values. Wire string
    // "NumericNoByte" matches DLL ValueScan::DataType::NumericNoByte.
    NumericNoByte,
    // Multi-numeric meta type WITH 1-byte fields (build 796). Same as
    // NumericNoByte but additionally includes Int8/UInt8 (still excludes
    // Bool). WARNING: small values (0/1/255) match a very large number of
    // 1-byte fields — the VM surfaces a result-volume warning when this is
    // selected. Wire string "NumericAll" matches DLL DataType::NumericAll.
    NumericAll,
}

/// <summary>
/// Scan comparison predicates. Targeted predicates (Exact / Bigger /
/// Smaller / Between / Contains / StartsWith / EndsWith) compare
/// against user-supplied values; prev-value predicates (Changed /
/// Unchanged / Increased / Decreased) compare against each candidate's
/// stored snapshot from the previous round.
///
/// Per-type validity:
///   Numeric: Exact / Bigger / Smaller / Between / Changed / Unchanged
///            / Increased / Decreased
///   String:  Exact / Contains / StartsWith / EndsWith / Changed / Unchanged
///   Vector:  Exact / Bigger / Smaller / Between / Changed / Unchanged
///            / Increased / Decreased  (component-wise per axis)
/// </summary>
public enum ValueScanType
{
    Exact,
    Bigger,
    Smaller,
    Between,
    Changed,
    Unchanged,
    Increased,
    Decreased,
    // String-only substring predicates (Phase 2A, build 750)
    Contains,
    StartsWith,
    EndsWith,
}

/// <summary>
/// One row in the Value Search result DataGrid. Cached metadata from
/// the FIRST scan is reused across all refines so the user can sort /
/// filter on class/field/owner without DLL round-trips.
/// </summary>
public class ValueCandidate
{
    public string Addr              { get; set; } = "";   // "0x..."
    public string InstanceAddr      { get; set; } = "";   // "0x..."
    public int    InstanceIndex     { get; set; }
    public int    FieldOffset       { get; set; }
    public string InstanceName      { get; set; } = "";
    public string ClassName         { get; set; } = "";
    public string DefiningClassName { get; set; } = "";
    public string FieldName         { get; set; } = "";
    public string FieldType         { get; set; } = "";   // "FloatProperty" / ...
    public byte   BoolFieldMask     { get; set; } = 0xFF;
    public string Value             { get; set; } = "";   // formatted current value

    public string OffsetHex => $"0x{FieldOffset:X}";

    /// <summary>Compact location label for the DataGrid: "ClassName.FieldName".
    /// If the defining class differs from the owning class, it's shown
    /// in brackets to surface inheritance ("BP_Player_C.Health (ACharacter)").</summary>
    public string LocationLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(DefiningClassName)
                && DefiningClassName != ClassName)
            {
                return $"{ClassName}.{FieldName}  ({DefiningClassName})";
            }
            return $"{ClassName}.{FieldName}";
        }
    }
}

/// <summary>
/// Response from <c>begin_value_scan</c>. The DLL retains the full
/// candidate set in a session keyed by <see cref="SessionId"/>; the
/// client echoes the same vector here on first scan and on every
/// refine. <see cref="DeadlineHit"/> tells the UI to show a "scan
/// truncated" warning so the user knows to narrow the predicate.
/// </summary>
public class ValueScanBeginResult
{
    public ulong  SessionId      { get; set; }
    public string DataType       { get; set; } = "";
    public int    Total          { get; set; }
    public int    ScannedClasses { get; set; }
    public int    ScannedObjects { get; set; }
    public long   DurationMs     { get; set; }
    public bool   DeadlineHit    { get; set; }
    public List<ValueCandidate> Candidates { get; set; } = new();
}

/// <summary>Response from <c>refine_value_scan</c>. <see cref="Candidates"/>
/// is the surviving set after pruning; the DLL also stored these as the
/// new session candidate vector so the NEXT refine compares prev-value
/// against the bytes seen during THIS refine.</summary>
public class ValueScanRefineResult
{
    public ulong  SessionId  { get; set; }
    public string DataType   { get; set; } = "";
    public string ScanType   { get; set; } = "";
    public int    Total      { get; set; }
    public long   DurationMs { get; set; }
    public List<ValueCandidate> Candidates { get; set; } = new();
}
