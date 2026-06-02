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
    /// <summary>Absolute path of the SQLite database file.</summary>
    string DatabasePath { get; }

    /// <summary>Insert a new snapshot row; returns its generated id.</summary>
    Task<long> CreateSnapshotAsync(SnapshotMeta meta, CancellationToken ct = default);

    /// <summary>Append one captured chunk (flattened to field rows) under one
    /// transaction. Returns the number of field rows written.</summary>
    Task<int> WriteChunkAsync(long snapshotId, IReadOnlyList<SnapshotCapturedObject> objects,
                              CancellationToken ct = default);

    /// <summary>Record the final object/field totals on the snapshot row.</summary>
    Task FinalizeSnapshotAsync(long snapshotId, int objectCount, int fieldCount,
                               CancellationToken ct = default);

    /// <summary>All snapshots, newest first.</summary>
    Task<IReadOnlyList<SnapshotMeta>> ListSnapshotsAsync(CancellationToken ct = default);

    /// <summary>Delete a snapshot and all its field rows.</summary>
    Task DeleteSnapshotAsync(long snapshotId, CancellationToken ct = default);
}
