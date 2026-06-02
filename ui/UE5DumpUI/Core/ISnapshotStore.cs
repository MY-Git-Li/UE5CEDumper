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

    /// <summary>Drop oldest snapshots (FIFO) from the active game's DB until it
    /// fits <paramref name="quotaBytes"/>, then VACUUM to reclaim the space. The
    /// newest snapshot is always kept (even if it alone exceeds the quota).
    /// <paramref name="quotaBytes"/> &lt;= 0 means unlimited (no-op). Returns the
    /// number of snapshots dropped.</summary>
    Task<int> EnforceQuotaAsync(long quotaBytes, CancellationToken ct = default);
}
