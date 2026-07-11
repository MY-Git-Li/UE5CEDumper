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
