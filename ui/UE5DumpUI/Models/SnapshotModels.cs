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
    public List<SnapshotCapturedObject> Objects { get; set; } = new();
}
