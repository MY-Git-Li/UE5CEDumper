using UE5DumpUI.Models;

namespace UE5DumpUI.Core;

/// <summary>
/// SQLite-backed persistence for experimental snapshots. A snapshot is created
/// once (returning its id), streamed in chunks (so neither the game process nor
/// the UI holds the whole capture), then finalised with its totals. Raw ADO.NET
/// only — no EF Core (reflection-based query breaks trim/AOT).
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Absolute path of the SQLite database file for the active game.</summary>
    string DatabasePath { get; }

    /// <summary>
    /// Scope all subsequent operations to a per-game database file
    /// (snapshots.&lt;pe_hash&gt;.db). Called when the engine state arrives so
    /// each game's snapshots stay isolated — no cross-game mixing in the list
    /// or in SPC / Pivot joins, isolated growth, and isolated corruption blast
    /// radius. pe_hash is stable across launches of the same build, so all
    /// sessions of a game share one file.
    /// </summary>
    void SetActiveGame(string? peHash);

    /// <summary>Insert a new snapshot row; returns its generated id.</summary>
    Task<long> CreateSnapshotAsync(SnapshotMeta meta, CancellationToken ct = default);

    /// <summary>Append one captured chunk (flattened to field rows) under one
    /// transaction. Returns the number of field rows written.</summary>
    Task<int> WriteChunkAsync(long snapshotId, IReadOnlyList<SnapshotCapturedObject> objects,
                              CancellationToken ct = default);

    /// <summary>Record the final object/field totals on the snapshot row.</summary>
    Task FinalizeSnapshotAsync(long snapshotId, int objectCount, int fieldCount,
                               CancellationToken ct = default);

    /// <summary>All snapshots for the active game, newest first. Each row's
    /// <see cref="SnapshotMeta.EstBytes"/> is set (pro-rated from the DB size).</summary>
    Task<IReadOnlyList<SnapshotMeta>> ListSnapshotsAsync(CancellationToken ct = default);

    /// <summary>Delete a snapshot and all its field rows.</summary>
    Task DeleteSnapshotAsync(long snapshotId, CancellationToken ct = default);

    /// <summary>Active game's DB file size + all-games total + snapshot count.</summary>
    Task<SnapshotUsage> GetUsageAsync(CancellationToken ct = default);

    /// <summary>Diff snapshot <paramref name="idA"/> (old) against
    /// <paramref name="idB"/> (new): fields whose value changed, joined by
    /// (class, GObjects index, property). Both must be in the active game's DB
    /// (in-session identity). Returns changed rows (filtered/capped) plus
    /// Added/Removed churn counts.</summary>
    Task<SnapshotDiffResult> DiffSnapshotsAsync(
        long idA, long idB, SnapshotDiffFilter filter, CancellationToken ct = default);

    /// <summary>Run an SPC query over the active game's DB: intersect the fields
    /// present in all of <see cref="SpcQuery.SnapshotIds"/> under the chosen join
    /// mode, then keep those whose value sequence satisfies the directional
    /// predicate chain. Multi-session by design (snapshots may span game
    /// launches). Pure SQL — no DLL/pipe. See
    /// docs/experimental-snapshot-spc-pivot.md §"Phase B".</summary>
    Task<SpcResult> SpcQueryAsync(SpcQuery query, CancellationToken ct = default);

    /// <summary>Classes present in a snapshot with their live-instance counts,
    /// most-populous first — populates the Class Pivot class picker.</summary>
    Task<IReadOnlyList<PivotClassInfo>> ListPivotClassesAsync(
        long snapshotId, CancellationToken ct = default);

    /// <summary>Numeric fields of one class in a snapshot with cardinality stats
    /// (distinct values / instance count) — drives key ranking + value picks.</summary>
    Task<IReadOnlyList<PivotFieldInfo>> ListPivotFieldsAsync(
        long snapshotId, string className, CancellationToken ct = default);

    /// <summary>Group a class's instances by identity or a key field and project
    /// the requested value fields per group (collisions rendered ⟨N: …⟩). Pure
    /// SQL fetch + <see cref="Services.PivotEngine"/>. See
    /// docs/experimental-snapshot-spc-pivot.md §"Phase C".</summary>
    Task<PivotResult> PivotAsync(PivotQuery query, CancellationToken ct = default);

    // --- Phase C6: array-element pivot (struct-array inner-key join) ---

    /// <summary>Classes in a snapshot that captured struct-array elements
    /// (array_field rows), with owner-instance counts — the array-pivot class
    /// picker.</summary>
    Task<IReadOnlyList<PivotClassInfo>> ListPivotArrayClassesAsync(
        long snapshotId, CancellationToken ct = default);

    /// <summary>The struct-array fields captured for one class (e.g. "Cargo"),
    /// each with its inner-key name + element count.</summary>
    Task<IReadOnlyList<PivotArrayFieldInfo>> ListPivotArrayFieldsAsync(
        long snapshotId, string className, CancellationToken ct = default);

    /// <summary>The inner numeric props of one captured struct-array (e.g.
    /// "Quantity") with cardinality stats — the value-field picker.</summary>
    Task<IReadOnlyList<PivotFieldInfo>> ListPivotArrayPropsAsync(
        long snapshotId, string className, string arrayField, CancellationToken ct = default);

    /// <summary>Group a class's captured struct-array elements by inner-key value
    /// (e.g. Cargo by ItemID), projecting the inner numeric props per group.
    /// Reorder- and session-immune (keyed by value, not array index). Pure SQL
    /// fetch + <see cref="Services.PivotEngine"/> in Identity mode. See
    /// docs/experimental-snapshot-spc-pivot.md §"Phase C — C6".</summary>
    Task<PivotResult> PivotArrayAsync(ArrayPivotQuery query, CancellationToken ct = default);

    /// <summary>Drop oldest snapshots (FIFO) from the active game's DB until it
    /// fits <paramref name="quotaBytes"/>, then VACUUM to reclaim the space. The
    /// newest snapshot is always kept (even if it alone exceeds the quota).
    /// <paramref name="quotaBytes"/> &lt;= 0 means unlimited (no-op). Returns the
    /// number of snapshots dropped.</summary>
    Task<int> EnforceQuotaAsync(long quotaBytes, CancellationToken ct = default);
}
