using CommunityToolkit.Mvvm.ComponentModel;

namespace UE5DumpUI.Models;

/// <summary>
/// A single property match from the search_properties command.
///
/// Build 610+: results are deduped by (definingClass, propName, offset)
/// so a field declared on AActor and inherited by 4823 children only
/// emits one row keyed by the defining class. <see cref="ClassName"/>
/// and <see cref="DefiningClassName"/> will be the same value after
/// dedup; both are exposed to keep wire forward-compat in case the
/// dedup story changes (e.g. a future "Show inheritance expanded"
/// toggle could emit one row per inheriting class).
/// </summary>
public partial class PropertySearchMatch : ObservableObject
{
    public string ClassName { get; set; } = "";
    public string ClassAddr { get; set; } = "";
    public string ClassPath { get; set; } = "";

    /// <summary>
    /// FProperty* address (UProperty* on UE4 &lt;4.25) — the key for
    /// find_property_xrefs ("which methods use this field?"). Emitted by
    /// search_properties / search_properties_batch since build 842.
    /// </summary>
    public string FieldAddr { get; set; } = "";
    public string SuperName { get; set; } = "";
    public string PropName { get; set; } = "";
    public string PropType { get; set; } = "";
    public int PropOffset { get; set; }
    public int PropSize { get; set; }
    public string StructType { get; set; } = "";
    public string InnerType { get; set; } = "";
    public string Preview { get; set; } = "";

    // === Inheritance-aware fields (build 610+) ===
    public string DefiningClassName { get; set; } = "";
    public string DefiningClassAddr { get; set; } = "";
    public string DefiningClassPath { get; set; } = "";
    /// <summary>Number of OTHER classes (excludes the defining class
    /// itself) that inherit this field at the same offset. 0 means
    /// the property is unique to this class -- often a strong
    /// signal that it's a game-specific addition rather than an
    /// engine inherited field.</summary>
    public int InheritedByCount { get; set; }

    /// <summary>
    /// True when this row is a synthetic dotted-path leaf found by the
    /// opt-in deep descent (build 1222) into nested struct members + struct-
    /// typed container elements. For these <see cref="PropName"/> is a dotted
    /// path (e.g. "SaveSlotList[].MsTuneData.GP"), <see cref="ClassName"/> is
    /// the OWNING class (so Find Instances works), and <see cref="FieldAddr"/>
    /// is the leaf FProperty* (so Find Funcs works). There is no single
    /// class-absolute address, so Copy Offset / Freeze are hidden for these
    /// rows (see <see cref="ShowScalarActions"/>).
    /// </summary>
    public bool IsNested { get; set; }

    /// <summary>
    /// Gates the row's Copy Offset + Freeze buttons. Nested (deep) matches
    /// have a dotted path rather than a class-absolute offset, so those two
    /// actions don't apply — only finder (locate live instances of the
    /// owning class) + Find Funcs (xref the leaf FProperty) make sense.
    /// </summary>
    public bool ShowScalarActions => !IsNested;

    /// <summary>
    /// Tooltip for the Property column. Empty for a normal direct field;
    /// for a nested (deep) match it explains the dotted path is a drill
    /// route, not a directly-addressable field, and points at how to reach
    /// a live value.
    /// </summary>
    public string? PropNameTooltip => IsNested
        ? $"Nested field reached via {PropName} on {ClassName}.\n" +
          "This path crosses struct/container members, so it has no single " +
          "class-absolute address. Use finder to list instances of the owning " +
          "class, then Value Search (by value) or Live Walker (drill the path) " +
          "to reach a live value."
        : null;  // null => no tooltip popup on plain direct-field rows

    /// <summary>Display-friendly offset as hex.</summary>
    public string OffsetHex => $"0x{PropOffset:X}";

    /// <summary>Combined type display (e.g. "StructProperty (FVector)" or "ArrayProperty [ObjectProperty]").</summary>
    public string TypeDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(StructType))
                return $"{PropType} ({StructType})";
            if (!string.IsNullOrEmpty(InnerType))
                return $"{PropType} [{InnerType}]";
            return PropType;
        }
    }

    /// <summary>
    /// Compact inheritance hint shown next to ClassName in the DataGrid.
    /// "(unique)" when only one class has this field; "+N inherited"
    /// for a field shared with N children. Empty when count == 0 and
    /// we want the column to stay clean.
    /// </summary>
    public string InheritanceBadge => InheritedByCount switch
    {
        0   => "",            // unique to this class -- no badge needed
        1   => "+1 inheritor",
        _   => $"+{InheritedByCount} inheritors",
    };

    /// <summary>
    /// Tooltip explaining the inheritance relationship -- shows the
    /// defining class path so the user can see whether it's an engine
    /// (/Script/Engine.*) or game (/Game/* /Script/MyGame.*) field.
    /// </summary>
    /// <summary>Batch "Find Funcs" result: which UFunctions reference this
    /// property. Format "N · func1, func2[, …]" / "0" / "—" / "" (not run).</summary>
    [ObservableProperty] private string _xrefInfo = "";

    public string InheritanceTooltip => InheritedByCount == 0
        ? $"This property is unique to {ClassName} -- likely a " +
          $"game-specific field rather than an engine inheritance.\n" +
          $"Path: {ClassPath}"
        : $"Defined on {DefiningClassName} (at offset {OffsetHex}); " +
          $"inherited by {InheritedByCount} subclass(es). Writing to " +
          $"this offset on any instance of {DefiningClassName} (or any " +
          $"subclass) has identical effect.\n" +
          $"Path: {DefiningClassPath}";
}

/// <summary>
/// Result set from the search_properties command.
/// </summary>
public class PropertySearchResult
{
    public int Total { get; set; }
    public int ScannedClasses { get; set; }
    public int ScannedObjects { get; set; }
    public List<PropertySearchMatch> Results { get; set; } = new();
}

/// <summary>
/// Per-query envelope inside a <see cref="PropertySearchBatchResult"/>.
/// Mirrors the DLL-side `per_query[i]` shape.
/// </summary>
public class PropertySearchQueryEnvelope
{
    public string Query { get; set; } = "";
    public int MatchCount { get; set; }
    public List<PropertySearchMatch> Results { get; set; } = new();
}

/// <summary>
/// Result set from the search_properties_batch command. Walks GObjects
/// + class fields ONCE for N queries — see DLL-side SearchPropertiesBatch
/// for the speedup rationale (~30x on a 36-query / 4400-class game).
/// Order of <see cref="PerQuery"/> matches the input queries[] order;
/// callers can therefore index by position or by matching the
/// envelope's <see cref="PropertySearchQueryEnvelope.Query"/> field.
/// </summary>
public class PropertySearchBatchResult
{
    public int QueryCount { get; set; }
    public int Total { get; set; }
    public int ScannedClasses { get; set; }
    public int ScannedObjects { get; set; }
    public List<PropertySearchQueryEnvelope> PerQuery { get; set; } = new();
}
