namespace UE5DumpUI.Models;

/// <summary>
/// Primitive data types supported by the Value Search workflow. Wire
/// string matches DLL-side <c>ValueScan::DataType</c> exactly so the
/// pipe round-trip is just <c>nameof(ValueScanDataType.X)</c>.
///
/// MVP locked-in 2026-05-26 (see memory project_value_search_caveats):
/// strings, names, arrays, vectors, and FText are deliberately omitted.
/// Native C++ fields (non-UPROPERTY) are also unreachable from this
/// scan — the UI Value Search tab MUST surface that limitation.
/// </summary>
public enum ValueScanDataType
{
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
}

/// <summary>
/// Scan comparison predicates. First-Scan predicates (Exact / Bigger
/// / Smaller / Between) compare against user-supplied values; Next-Scan
/// predicates (Changed / Unchanged / Increased / Decreased) compare
/// against each candidate's stored prevValue from the previous round.
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
