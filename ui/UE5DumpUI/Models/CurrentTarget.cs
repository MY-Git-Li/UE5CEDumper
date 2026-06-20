namespace UE5DumpUI.Models;

/// <summary>
/// One scored candidate for "the actor the local player is currently targeting",
/// produced by the Edel current-target detector (<c>get_current_target</c>). The
/// top-scoring candidate is the auto-pick; the full ranked list lets the user
/// override when two actors are equally plausible.
/// </summary>
public sealed class TargetCandidate
{
    public string Address { get; init; } = "";

    /// <summary>InternalIndex in GObjects; -1 when unreadable.</summary>
    public int Index { get; init; } = -1;

    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";

    /// <summary>Heuristic score; higher = more likely the focused target.</summary>
    public int Score { get; init; }

    /// <summary>The PC / Pawn / component the target pointer was read from.</summary>
    public string SourceAddress { get; init; } = "";

    /// <summary>Class name of the source object (UI subtitle).</summary>
    public string SourceClass { get; init; } = "";

    /// <summary>Reflected field/path that pointed at this candidate.</summary>
    public string FieldName { get; init; } = "";

    /// <summary>Offset within the source object; -1 for container/path edges.</summary>
    public int FieldOffset { get; init; } = -1;

    /// <summary>Human-readable why this scored as it did (matched signals).</summary>
    public string Reason { get; init; } = "";

    /// <summary>Display label: "Name : ClassName" (or whichever is non-empty).</summary>
    public string Display =>
        string.IsNullOrEmpty(Name) ? ClassName
        : string.IsNullOrEmpty(ClassName) ? Name
        : $"{Name} : {ClassName}";

    /// <summary>"score N — via Field on SourceClass" subtitle for the picker.</summary>
    public string ScoreDisplay =>
        $"score {Score}  ·  via {(string.IsNullOrEmpty(FieldName) ? "?" : FieldName)}"
        + (string.IsNullOrEmpty(SourceClass) ? "" : $" on {SourceClass}");

    /// <summary>Address as ulong for AOT-safe hex sorting (0 on parse failure).</summary>
    public ulong AddressValue =>
        ulong.TryParse(Address.Replace("0x", "", System.StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0UL;
}

/// <summary>
/// Result of a current-target detection (<c>get_current_target</c>). The chain
/// diagnostics (<see cref="World"/>/<see cref="PlayerController"/>/<see cref="PlayerPawn"/>/<see cref="Note"/>)
/// are always populated so the UI can show exactly where detection stopped.
/// </summary>
public sealed class CurrentTargetResult
{
    /// <summary>True = chain reached a Pawn AND the top candidate has a positive score.</summary>
    public bool Resolved { get; init; }

    public string World { get; init; } = "";
    public string PlayerController { get; init; } = "";
    public string PlayerPawn { get; init; } = "";

    /// <summary>User-facing status / failure reason.</summary>
    public string Note { get; init; } = "";

    /// <summary>Candidates sorted best-first; [0] is the auto-pick when Resolved.</summary>
    public List<TargetCandidate> Candidates { get; init; } = new();
}
