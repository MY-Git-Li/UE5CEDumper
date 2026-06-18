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

/// <summary>Response from <c>refine_value_scan</c>. <see cref="Total"/> is the
/// surviving count (the full pruned set lives in the DLL session);
/// <see cref="Candidates"/> is only the FIRST PAGE in scan order (V3-C). The
/// UI re-pages / filters / sorts via <c>query_candidates</c>.</summary>
public class ValueScanRefineResult
{
    public ulong  SessionId  { get; set; }
    public string DataType   { get; set; } = "";
    public string ScanType   { get; set; } = "";
    public int    Total      { get; set; }
    public long   DurationMs { get; set; }
    public List<ValueCandidate> Candidates { get; set; } = new();
}

/// <summary>Response from <c>query_candidates</c> (V3-C server-side window).
/// The DLL filters + sorts the WHOLE session set and returns only the
/// requested window. <see cref="Total"/> is the full session size,
/// <see cref="FilteredTotal"/> the count after the keyword filter, and
/// <see cref="Candidates"/> the [<see cref="Offset"/>, Offset+limit) slice in
/// the requested sort order.</summary>
public class ValueScanWindowResult
{
    public ulong  SessionId     { get; set; }
    public string DataType      { get; set; } = "";
    public int    Total         { get; set; }
    public int    FilteredTotal { get; set; }
    public int    Offset        { get; set; }
    public List<ValueCandidate> Candidates { get; set; } = new();
}

// ===================== Multiple values group scan (build 1276) =====================

/// <summary>
/// One input value row of a "Group" scan (2..4 rows). The user supplies a value
/// and, optionally, a numeric width scope. P1 fans out over numeric widths
/// (NumericNoByte default / NumericAll) and matches each slot exactly; concrete
/// per-slot widths + prev-value scan types are a P2 extension. Plain settable
/// POCO — the editable DataGrid writes each cell back via the property setter.
/// </summary>
public class GroupSlotInput
{
    public ValueScanDataType DataType { get; set; } = ValueScanDataType.NumericNoByte;
    public ValueScanType     ScanType { get; set; } = ValueScanType.Exact;  // P1: always Exact
    public string            Value    { get; set; } = "";
}

/// <summary>
/// One slot's converging match inside a <see cref="GroupCandidate"/>. While the
/// scan narrows, <see cref="MatchedOffsets"/> may list several offsets; once a
/// single offset remains the field is identified (<see cref="Locked"/>). The
/// representative (first) match carries the resolved field name / offset / type
/// + leaf value + leaf address so a per-slot row can drive the same handoffs
/// (Open in Live Walker / Locate in GWorld / Copy) as a single-value candidate.
/// </summary>
public class GroupSlotMatch
{
    public int       SlotIndex      { get; set; }
    public string    Value          { get; set; } = "";   // the slot's target value
    public string    FieldName      { get; set; } = "";   // representative match
    public int       FieldOffset    { get; set; }
    public string    FieldType      { get; set; } = "";
    public byte      BoolFieldMask  { get; set; } = 0xFF;
    public string    LeafValue      { get; set; } = "";   // current value at the leaf
    public string    Addr           { get; set; } = "";   // leaf address (instance + offset)
    public string    InstanceAddr   { get; set; } = "";   // owning object (denormalized for self-contained handoffs)
    public string    ClassName      { get; set; } = "";   // owning class (for the Pivot handoff)
    public List<int> MatchedOffsets { get; set; } = new();
    public bool      Locked         { get; set; }

    public string OffsetHex => $"0x{FieldOffset:X}";
    // Detail-row caption: "Str  24 → 0x20  (IntProperty)" once locked, else the
    // target value + how many candidate offsets still match.
    public string DisplayLabel =>
        Locked
            ? $"{(string.IsNullOrEmpty(FieldName) ? "?" : FieldName)}  {Value} → {OffsetHex}  ({FieldType})"
            : $"value {Value}: {MatchedOffsets.Count} candidate offset(s)";
    public string LockLabel => Locked ? "🔒" : $"×{MatchedOffsets.Count}";
}

/// <summary>
/// One group hit: an owning UObject plus its per-slot matches. A logical match
/// means the object simultaneously holds every slot's value at a distinct
/// offset. Mirrors <see cref="ValueCandidate"/>'s owner fields so the master row
/// can hand off to Instance Finder / Class Pivot.
/// </summary>
public class GroupCandidate
{
    public string InstanceAddr      { get; set; } = "";
    public int    InstanceIndex     { get; set; }
    public string InstanceName      { get; set; } = "";
    public string ClassName         { get; set; } = "";
    public string DefiningClassName { get; set; } = "";
    public List<GroupSlotMatch> Slots { get; set; } = new();

    public string LocationLabel =>
        (!string.IsNullOrEmpty(DefiningClassName) && DefiningClassName != ClassName)
            ? $"{ClassName}  ({DefiningClassName})" : ClassName;
    // Compact master-row summary: "Str=24, Def=10, Dex=14, Int=8".
    public string SlotSummary =>
        string.Join(", ", Slots.Select(s =>
            $"{(string.IsNullOrEmpty(s.FieldName) ? "?" : s.FieldName)}={s.Value}"));
    public bool AllLocked => Slots.Count > 0 && Slots.All(s => s.Locked);
}

/// <summary>Response from <c>begin_group_scan</c> — object-level candidates.</summary>
public class GroupScanBeginResult
{
    public ulong SessionId      { get; set; }
    public int   Total          { get; set; }
    public int   SlotCount      { get; set; }
    public int   ScannedClasses { get; set; }
    public int   ScannedObjects { get; set; }
    public long  DurationMs     { get; set; }
    public bool  DeadlineHit    { get; set; }
    public List<GroupCandidate> Candidates { get; set; } = new();
}

/// <summary>Response from <c>refine_group_scan</c> — surviving object count + first page.</summary>
public class GroupScanRefineResult
{
    public ulong SessionId  { get; set; }
    public int   Total      { get; set; }
    public long  DurationMs { get; set; }
    public List<GroupCandidate> Candidates { get; set; } = new();
}

/// <summary>Response from <c>query_group_candidates</c> — server-side window.</summary>
public class GroupScanWindowResult
{
    public ulong SessionId     { get; set; }
    public int   Total         { get; set; }
    public int   FilteredTotal { get; set; }
    public int   Offset        { get; set; }
    public List<GroupCandidate> Candidates { get; set; } = new();
}
