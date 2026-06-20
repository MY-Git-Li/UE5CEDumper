namespace UE5DumpUI.Models;

/// <summary>
/// Result container for FindInstances, including diagnostic counters.
/// </summary>
public sealed class FindInstancesResult
{
    public List<InstanceResult> Instances { get; init; } = new();

    /// <summary>Total GObject indices scanned (= NumElements at call time).</summary>
    public int Scanned { get; init; }

    /// <summary>Non-null objects encountered during scan.</summary>
    public int NonNull { get; init; }

    /// <summary>Objects whose class name resolved successfully.</summary>
    public int Named { get; init; }

    /// <summary>True when the scan hit the result cap (more matches likely exist).
    /// The GObjects scan is exhaustive, but the returned list is capped — surface
    /// this so the user knows to narrow the query rather than trust a partial list.</summary>
    public bool Truncated { get; init; }
}
