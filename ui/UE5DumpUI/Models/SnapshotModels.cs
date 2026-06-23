namespace UE5DumpUI.Models;

/// <summary>
/// Metadata for one captured snapshot (one row in the <c>snapshots</c> SQLite
/// table). A snapshot is a type-agnostic point-in-time capture of every numeric
/// UPROPERTY of every (scoped) UObject, used by the experimental Snapshot /
/// SPC / Pivot tabs. See docs/experimental-snapshot-spc-pivot.md.
/// </summary>
public sealed class SnapshotMeta
{
    public long   Id            { get; set; }
    public string Label         { get; set; } = "";
    /// <summary>ISO-8601 capture timestamp (UI-stamped).</summary>
    public string CapturedAt    { get; set; } = "";
    /// <summary>Game build hash (from get_pointers) — distinguishes games.</summary>
    public string PeHash        { get; set; } = "";
    /// <summary>pe_hash + launch token — distinguishes game restarts (sessions).</summary>
    public string GameSessionId { get; set; } = "";
    public int    UeVersion     { get; set; }
    /// <summary>Objects that contributed at least one numeric field.</summary>
    public int    ObjectCount   { get; set; }
    /// <summary>Total numeric field rows captured.</summary>
    public int    FieldCount    { get; set; }
    /// <summary>Capture scope tag, e.g. "NumericNoByte".</summary>
    public string Scope         { get; set; } = "NumericNoByte";

    /// <summary>Estimated on-disk size of this snapshot in bytes (field_count
    /// pro-rated against the DB file size). Computed by the store on list, not
    /// persisted — snapshots share one per-game DB file, so exact per-snapshot
    /// bytes aren't recoverable.</summary>
    public long EstBytes { get; set; }

    /// <summary>Human-readable estimated size, e.g. "42.3 MB".</summary>
    public string EstSizeDisplay => SnapshotFormat.Bytes(EstBytes);

    /// <summary>Label + captured local time — used in the diff / pivot snapshot
    /// ComboBoxes so a custom label never hides WHEN the snapshot was taken
    /// (those pickers show only one line, unlike the saved-snapshots grid which
    /// has a separate Captured column).</summary>
    public string PickerDisplay
    {
        get
        {
            if (System.DateTimeOffset.TryParse(CapturedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
                return $"{Label}  ·  {dto.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
            return Label;
        }
    }
}

/// <summary>Per-game snapshot DB disk usage, for the quota/usage UI.</summary>
public sealed class SnapshotUsage
{
    /// <summary>Active game's DB file size in bytes.</summary>
    public long   GameDbBytes   { get; set; }
    /// <summary>Sum of all snapshots.*.db files in bytes (all games).</summary>
    public long   AllGamesBytes { get; set; }
    /// <summary>Snapshots in the active game's DB.</summary>
    public int    SnapshotCount { get; set; }
}

/// <summary>One UObject captured in a snapshot chunk (transient — flattened
/// into <c>fields</c> rows by the store, not persisted as-is).</summary>
public sealed class SnapshotCapturedObject
{
    public int    Index          { get; set; } = -1;  // GObjects index (in-session join key)
    public string Addr           { get; set; } = "";  // session-local; for CE export
    public string Name           { get; set; } = "";
    public string ClassName      { get; set; } = "";
    public string OuterClassName { get; set; } = "";  // loose-join component
    public string Path           { get; set; } = "";  // full object path (cross-session id)
    public List<SnapshotCapturedField> Fields { get; set; } = new();
    /// <summary>Struct-array inner-key captures (Phase A1b).</summary>
    public List<SnapshotCapturedArray> Arrays { get; set; } = new();
}

/// <summary>One element of a captured struct-array, with a reorder-immune inner
/// key (e.g. FCargoSlot.ItemID = "Fuel") and its numeric inner fields.</summary>
public sealed class SnapshotCapturedArrayElement
{
    public int    Index    { get; set; }
    public string KeyName  { get; set; } = "";   // inner-key field (e.g. "ItemID"); "" if none
    public string KeyValue { get; set; } = "";   // rendered inner-key value (e.g. "Fuel")
    public List<SnapshotCapturedField> Fields { get; set; } = new();
}

/// <summary>A captured struct-array (e.g. "Cargo") and its elements.</summary>
public sealed class SnapshotCapturedArray
{
    public string Field { get; set; } = "";
    public List<SnapshotCapturedArrayElement> Elements { get; set; } = new();
}

/// <summary>One numeric scalar field captured for an object.</summary>
public sealed class SnapshotCapturedField
{
    public string Name   { get; set; } = "";
    public int    Offset { get; set; }
    public string Type   { get; set; } = "";  // declared property type (e.g. "FloatProperty")
    public string Hex    { get; set; } = "";  // little-endian raw bytes (exact compare)
}

/// <summary>Result of one <c>snapshot_chunk</c> pipe round-trip.</summary>
public sealed class SnapshotChunkResult
{
    public int Total   { get; set; }   // GObjects count
    public int Scanned { get; set; }   // indices iterated (advance offset by this)
    // Phase-0 telemetry (DLL-side, per chunk): the parallel walk+merge and the JSON
    // DOM-build wall-times. Default 0 keeps older DLLs / test stubs back-compatible.
    public long WalkMs      { get; set; }
    public long SerializeMs { get; set; }
    public List<SnapshotCapturedObject> Objects { get; set; } = new();
}
