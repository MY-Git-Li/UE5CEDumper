namespace UE5DumpUI.Models;

/// <summary>
/// One FProperty referenced by a UFunction's bytecode, with read/write tally.
/// Result of the reverse edge (function -> properties it reads/writes).
/// Blueprint/script functions only — native functions have no bytecode.
/// </summary>
public sealed class FunctionPropRef
{
    public string PropAddress { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";       // "FloatProperty" / "StructProperty" / ...
    public int Occurrences { get; init; }
    public int WriteCount { get; init; }           // reads = Occurrences - WriteCount

    /// <summary>
    /// "instance" (class member — the RE target), "local" (function local/param,
    /// e.g. BP compiler CallFunc_* temporaries), "default", "sparse", "struct",
    /// "frame". The dialog defaults to instance-only.
    /// </summary>
    public string Scope { get; init; } = "";

    /// <summary>True when this is a class member field (vs a local/temporary).</summary>
    public bool IsClassField => Scope == "instance";

    /// <summary>Compact access summary: "3W / 4R", "write", or "read".</summary>
    public string AccessSummary =>
        WriteCount <= 0 ? "read"
        : WriteCount >= Occurrences ? "write"
        : $"{WriteCount}W / {Occurrences - WriteCount}R";
}

/// <summary>Result of a walk_function_props scan.</summary>
public sealed class FunctionPropRefsResult
{
    public string QueryAddress { get; init; } = "";
    public int ScriptBytes { get; init; }          // 0 = native / empty bytecode
    public List<FunctionPropRef> Props { get; init; } = new();
}
