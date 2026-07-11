namespace UE5DumpUI.Models;

/// <summary>
/// One UFunction observed firing through the game's ProcessEvent during a
/// Live PE Profiler recording window (<c>pe_profile_get</c>). Ranked by
/// <see cref="Count"/> — the behaviour-based counterpart to the name-heuristic
/// Interesting Functions finder: the function the game ACTUALLY called when the
/// user performed an in-game action.
///
/// Plain init-only POCO (hand-parsed from the pipe JsonObject like
/// <see cref="AllFunctionEntry"/>): the Start → Stop → Get flow produces an
/// immutable snapshot per fetch, so no ObservableObject is needed.
/// </summary>
public sealed class PeProfileEntry
{
    public string ClassName { get; init; } = "";
    public string FuncName  { get; init; } = "";
    public string FuncAddr  { get; init; } = "";
    public byte   NumParms  { get; init; }
    public ushort ParmsSize { get; init; }
    public long   Count     { get; init; }
    public uint   FunctionFlags { get; init; }

    // UE FunctionFlags (ObjectMacros.h) relevant to "is this a thing I can CALL vs
    // an event the engine fires AT me". Event/delegate signatures are reactions,
    // not entry points — the flood you see when profiling an interaction.
    private const uint FUNC_Event             = 0x0000_0800;
    private const uint FUNC_MulticastDelegate = 0x0001_0000;
    private const uint FUNC_Delegate          = 0x0010_0000;
    private const uint FUNC_BlueprintCallable = 0x0400_0000;
    private const uint FUNC_BlueprintEvent    = 0x0800_0000;
    private const uint FUNC_Native            = 0x0000_0400;

    /// <summary>Event handler / delegate signature — a reaction the engine dispatches
    /// (On*/callbacks), NOT an imperative function you'd invoke to drive the flow.</summary>
    public bool IsEventLike =>
        (FunctionFlags & (FUNC_Event | FUNC_MulticastDelegate | FUNC_Delegate)) != 0;

    /// <summary>Short kind badge: "Deleg" / "Event" / "Call" / "native" / "".</summary>
    public string TypeLabel =>
        (FunctionFlags & (FUNC_MulticastDelegate | FUNC_Delegate)) != 0 ? "Deleg"
        : (FunctionFlags & (FUNC_Event | FUNC_BlueprintEvent)) != 0     ? "Event"
        : (FunctionFlags & FUNC_BlueprintCallable) != 0                 ? "Call"
        : (FunctionFlags & FUNC_Native) != 0                           ? "native"
        : "";

    /// <summary>Owning class derives from UUserWidget/UWidget — the transient UI
    /// created BY the action (e.g. a shop widget), not its opener. The UI can hide
    /// these so the persistent opener (controller / subsystem / component) surfaces.</summary>
    public bool   IsWidget  { get; init; }

    /// <summary>"NumParms (ParmsSize B)" e.g. "2 (5B)"; empty for no-arg funcs.</summary>
    public string ParamsLabel => NumParms == 0 ? "" : $"{NumParms} ({ParmsSize}B)";

    /// <summary>Small kind badge for the grid — "UI" marks a transient widget method.</summary>
    public string Kind => IsWidget ? "UI" : "";

    // --- Baseline-diff display state (set by LiveFuncsViewModel when Diff mode is
    // on; 0 / false otherwise). The diff is client-side — the DLL knows nothing of
    // it. Mutable by design: rows are transient per fetch and never persisted. ---

    /// <summary>Count minus this Class::Func's baseline count (== Count when new).</summary>
    public long Delta { get; set; }
    /// <summary>True when this Class::Func was absent from the baseline entirely —
    /// the strongest "fired because of the action" signal.</summary>
    public bool IsNew { get; set; }

    /// <summary>Δ-column label: "NEW" (absent from baseline) / "+N" (fired more) /
    /// "" (unchanged, or not in diff mode) / "-N" (fired less).</summary>
    public string DeltaLabel =>
        IsNew ? "NEW" : Delta > 0 ? $"+{Delta}" : Delta < 0 ? Delta.ToString() : "";
}

/// <summary>
/// Result of <c>pe_profile_get</c>: the ranked fire-count table plus the
/// aggregate counters (distinct functions + total PE calls) shown in the status
/// line so the user can gauge how much fired during the window.
/// </summary>
public sealed class PeProfileResult
{
    public bool Recording     { get; init; }
    public int  DistinctFuncs { get; init; }
    public long TotalCalls    { get; init; }
    public List<PeProfileEntry> Entries { get; init; } = new();
}
