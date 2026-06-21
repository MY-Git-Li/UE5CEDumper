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

    /// <summary>True when more NON-EXCLUDED matches exist than the returned list's
    /// cap. The GObjects scan is exhaustive; the returned list is capped — surface
    /// this so the user excludes noise classes (which frees cap slots server-side)
    /// or narrows, rather than trusting a partial list.</summary>
    public bool Truncated { get; init; }

    /// <summary>Server-side class-noise histogram (Top-40, count desc), tallied over
    /// the FULL matched pool PRE-exclude — so an excluded class (or one whose
    /// instances all sit past the cap) still appears in the picker and stays
    /// untickable. Drives <see cref="Helpers.ClassFacetFilter.RebuildFromCounts"/>.</summary>
    public List<ClassCount> ClassHistogram { get; init; } = new();

    /// <summary>True distinct matched-class count (>= ClassHistogram.Count when the
    /// histogram was capped at Top-40).</summary>
    public int ClassDistinct { get; init; }
}
