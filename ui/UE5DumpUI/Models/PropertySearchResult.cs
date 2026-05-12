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
public class PropertySearchMatch
{
    public string ClassName { get; set; } = "";
    public string ClassAddr { get; set; } = "";
    public string ClassPath { get; set; } = "";
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
