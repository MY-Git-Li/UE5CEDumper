namespace UE5DumpUI.Models;

/// <summary>
/// Statistical-Process-Control predicate kinds for the experimental SPC Query
/// tab. v1 is the type-agnostic DIRECTIONAL set only — the user picks how a
/// field's value moved between consecutive snapshots, never a type or a literal
/// value. This generalizes CE's "Unknown Initial Value + Increased/Decreased"
/// workflow across N persisted snapshots (and across game restarts). See
/// docs/experimental-snapshot-spc-pivot.md §"Phase B".
/// </summary>
public enum SpcPredicateKind
{
    /// <summary>No constraint. The oldest selected snapshot is always the
    /// baseline (Any) since it has no predecessor to compare against.</summary>
    Any,
    /// <summary>Raw bytes equal to the previous snapshot (type-exact).</summary>
    Unchanged,
    /// <summary>Raw bytes differ from the previous snapshot (type-exact).</summary>
    Changed,
    /// <summary>Numeric value (per the field's declared width) &gt; previous.</summary>
    Increased,
    /// <summary>Numeric value (per the field's declared width) &lt; previous.</summary>
    Decreased,
}

/// <summary>
/// Cross-snapshot join mode for SPC. Strict has the lowest false-positive rate
/// but misses runtime-spawned objects across sessions; Loose recovers some of
/// them at the cost of multiple candidates per logical field (the predicate
/// chain filters them down); InSession is exact within one game launch.
/// Identity keys: docs/experimental-snapshot-spc-pivot.md §5.
/// </summary>
public enum SpcJoinMode
{
    /// <summary>(class_fqn, norm_path, prop_name, prop_offset).</summary>
    Strict,
    /// <summary>(class_fqn, outer_chain, prop_name).</summary>
    Loose,
    /// <summary>(class_fqn, gobjects_index, prop_name) — one launch only.</summary>
    InSession,
}

/// <summary>
/// One SPC query: an ordered snapshot sequence (oldest → newest) plus a
/// per-snapshot predicate chain, a join mode and optional name filters. The
/// engine intersects the fields present in ALL listed snapshots under the join
/// key, then keeps those whose value sequence satisfies the predicate chain.
/// </summary>
public sealed class SpcQuery
{
    /// <summary>Snapshot ids, ordered oldest → newest. Must be ≥ 2.</summary>
    public List<long> SnapshotIds { get; set; } = new();

    /// <summary>One predicate per snapshot, same order/length as
    /// <see cref="SnapshotIds"/>. Index 0 is the baseline and is treated as
    /// <see cref="SpcPredicateKind.Any"/> regardless of its value. Predicate
    /// <c>i</c> (i ≥ 1) compares snapshot <c>i</c> against snapshot <c>i-1</c>.</summary>
    public List<SpcPredicateKind> Predicates { get; set; } = new();

    public SpcJoinMode JoinMode { get; set; } = SpcJoinMode.Strict;

    /// <summary>Case-insensitive substring on the class name (empty = all).</summary>
    public string ClassContains { get; set; } = "";
    /// <summary>Case-insensitive substring on the property name (empty = all).</summary>
    public string PropContains { get; set; } = "";

    /// <summary>Max matching candidates returned (default 50,000). One extra is
    /// probed to set <see cref="SpcResult.Truncated"/>.</summary>
    public int MaxRows { get; set; } = 50000;
}

/// <summary>One field whose value sequence across the selected snapshots matched
/// the predicate chain.</summary>
public sealed class SpcResultRow
{
    public string ClassName    { get; set; } = "";
    public string NormPath     { get; set; } = "";   // object identity (display)
    public string PropName     { get; set; } = "";
    public int    PropOffset   { get; set; }
    public string DeclaredType { get; set; } = "";
    /// <summary>Newest snapshot's live address — the CE-export handoff target.</summary>
    public string ObjAddr      { get; set; } = "";
    /// <summary>Rendered value per snapshot, oldest → newest.</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>The value sequence as one display string ("100  →  80  →  60").
    /// A single rendered column keeps the results grid AOT-safe (no per-snapshot
    /// dynamic <c>Binding("Values[i]")</c>, which trips IL2026/IL3050).</summary>
    public string SequenceDisplay => string.Join("  →  ", Values);
}

/// <summary>Result of an SPC query: matching rows + truncation flag.</summary>
public sealed class SpcResult
{
    public List<SpcResultRow> Rows { get; } = new();
    /// <summary>True when the row cap was hit.</summary>
    public bool Truncated   { get; set; }
    /// <summary>Number of snapshots in the evaluated sequence.</summary>
    public int  SnapshotCount { get; set; }
}
