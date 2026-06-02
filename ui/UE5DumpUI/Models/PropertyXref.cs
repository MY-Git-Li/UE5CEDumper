namespace UE5DumpUI.Models;

/// <summary>
/// One UFunction that references a target FProperty in its Kismet bytecode.
/// Result of the static property cross-reference scan (find_property_xrefs):
/// "which methods use this field?".
///
/// Coverage is Blueprint/script functions only — native (FUNC_Native)
/// functions have empty bytecode and are invisible to this scan.
/// </summary>
public sealed class PropertyXrefMatch
{
    public string FunctionAddress { get; init; } = "";

    /// <summary>Short UFunction name (e.g. "ExecuteUbergraph_BP_Door").</summary>
    public string FunctionName { get; init; } = "";

    /// <summary>Full path (e.g. "/Game/.../BP_Door.BP_Door_C.ReceiveTick").</summary>
    public string FunctionFullName { get; init; } = "";

    /// <summary>Owning UClass short name.</summary>
    public string OwnerClassName { get; init; } = "";

    public string OwnerClassAddress { get; init; } = "";

    /// <summary>How many times the FProperty* appears in this function's bytecode.</summary>
    public int Occurrences { get; init; }

    /// <summary>
    /// Reference kind, derived from the opcode byte preceding the matched
    /// pointer: "instance" (EX_InstanceVariable 0x01 — class member access,
    /// the common case), "local" (EX_LocalVariable 0x00), or "ref"
    /// (matched but preceding byte not a known variable opcode).
    /// </summary>
    public string Kind { get; init; } = "";
}

/// <summary>Diagnostic counters for a property cross-reference scan.</summary>
public sealed class PropertyXrefScanStats
{
    public int FunctionsScanned { get; init; }
    public int FunctionsWithScript { get; init; }
    public int ObjectsTotal { get; init; }
    public long DurationMs { get; init; }
    public bool DeadlineHit { get; init; }
}

/// <summary>Result of a find_property_xrefs scan.</summary>
public sealed class FindPropertyXrefsResult
{
    public string QueryAddress { get; init; } = "";
    public List<PropertyXrefMatch> Xrefs { get; init; } = new();
    public PropertyXrefScanStats? Scan { get; init; }
}
