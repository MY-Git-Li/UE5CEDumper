namespace UE5DumpUI.Models;

/// <summary>
/// How a Class Pivot groups a class's instances. UE dissolves the
/// "user must guess a business key" pain that <c>discrete</c> hits with anonymous
/// Unity instances — so identity is a first-class mode, offered before any key
/// field. See docs/experimental-snapshot-spc-pivot.md §"Phase C".
/// </summary>
public enum PivotKeyMode
{
    /// <summary>Group by intrinsic object identity (normalised path). Spawn-
    /// counter-normalised siblings (BP_Enemy_C_0/_1/…) collapse into one group,
    /// so the value cells show the spread across all of them. No key field needed.</summary>
    Identity,
    /// <summary>Group by the value of a chosen key field (e.g. ItemID → group the
    /// inventory by item).</summary>
    Field,
}

/// <summary>One class present in a snapshot, with its live-instance count.</summary>
public sealed class PivotClassInfo
{
    public string ClassName    { get; set; } = "";
    public int    InstanceCount { get; set; }
    /// <summary>"BP_Item_C  (1,240)" for the class picker.</summary>
    public string Display => $"{ClassName}  ({InstanceCount:N0})";
}

/// <summary>One numeric field of a pivot class, with cardinality stats used to
/// rank key candidates and suggest value fields.</summary>
public sealed class PivotFieldInfo
{
    public string Name          { get; set; } = "";
    public string DeclaredType  { get; set; } = "";
    /// <summary>Distinct values of this field across the class's instances.</summary>
    public int    DistinctCount { get; set; }
    /// <summary>Instances that carry this field (≈ class instance count).</summary>
    public int    InstanceCount { get; set; }
}

/// <summary>One captured (instance, field) row the store hands to the pure
/// <see cref="Services.PivotEngine"/>. Rows are grouped by instance, then by key.</summary>
public sealed class PivotInputRow
{
    public long   ObjectIndex { get; set; }   // gobjects_index (in-session instance id)
    public string NormPath    { get; set; } = "";
    public string ObjAddr     { get; set; } = "";
    public string PropName    { get; set; } = "";
    public int    PropOffset  { get; set; }
    public string DeclaredType { get; set; } = "";
    public string Hex         { get; set; } = "";
}

/// <summary>A Class Pivot request: one snapshot, one class, a key mode, and the
/// value fields to project per group.</summary>
public sealed class PivotQuery
{
    public long   SnapshotId { get; set; }
    public string ClassName  { get; set; } = "";
    public PivotKeyMode KeyMode { get; set; } = PivotKeyMode.Identity;
    /// <summary>Key field name (used only when <see cref="KeyMode"/> is Field).</summary>
    public string KeyField   { get; set; } = "";
    /// <summary>Fields whose values are projected per group.</summary>
    public List<string> ValueFields { get; set; } = new();
    /// <summary>Max groups returned (default 5,000). One extra is probed to set
    /// <see cref="PivotResult.Truncated"/>.</summary>
    public int MaxGroups { get; set; } = 5000;
}

/// <summary>One group in a pivot: a distinct key value and the projected values
/// of the instances that share it.</summary>
public sealed class PivotResultRow
{
    public string KeyValue { get; set; } = "";
    /// <summary>Instances in this group.</summary>
    public int    Count    { get; set; }
    /// <summary>A representative instance's live address (first in the group) —
    /// the Open-in-Live-Walker handoff target.</summary>
    public string ObjAddr  { get; set; } = "";
    /// <summary>Projected values rendered as "field=cell", joined. A cell for a
    /// group with differing values renders as a collision ⟨N: v1,v2,+M⟩.</summary>
    public string ValuesDisplay { get; set; } = "";
}

/// <summary>Result of a Class Pivot: grouped rows + stats.</summary>
public sealed class PivotResult
{
    public List<PivotResultRow> Rows { get; } = new();
    public int  GroupCount    { get; set; }
    public int  InstanceCount { get; set; }
    public bool Truncated     { get; set; }
}
