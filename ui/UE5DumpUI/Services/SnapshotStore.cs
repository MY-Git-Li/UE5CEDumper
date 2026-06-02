using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// SQLite snapshot store (raw ADO.NET — no EF Core, for trim/AOT safety). DB
/// lives at %LOCALAPPDATA%\UE5CEDumper\snapshots.db. The <c>fields</c> table is
/// denormalised (identity columns per row) so the SPC / Pivot engines can join
/// across snapshots with a single indexed query. Array columns are present but
/// NULL until Phase A1b. Schema: docs/experimental-snapshot-spc-pivot.md §6.
/// </summary>
public sealed class SnapshotStore : ISnapshotStore
{
    private readonly string _dir;
    private readonly ILoggingService? _log;
    // Active game's pe_hash (sanitised for use in the filename). Empty until
    // SetActiveGame is called — falls back to a shared "default" db.
    private string _peHash = "";

    // SQLitePCLRaw provider init is idempotent; do it once before first use so
    // the bundled native e_sqlite3 is registered under Native AOT.
    private static readonly object s_initLock = new();
    private static bool s_initialised;

    /// <summary>Per-game DB path: snapshots.&lt;pe_hash&gt;.db (or
    /// snapshots.default.db before a game is set).</summary>
    public string DatabasePath =>
        Path.Combine(_dir, $"{Constants.SnapshotDbPrefix}.{(_peHash.Length > 0 ? _peHash : "default")}.db");

    public SnapshotStore(IPlatformService platform, ILoggingService? log = null)
    {
        _log = log;
        _dir = Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName);
        Directory.CreateDirectory(_dir);
        EnsureProviderInitialised();
    }

    public void SetActiveGame(string? peHash)
    {
        _peHash = SanitizePeHash(peHash);
        _log?.Info(Constants.LogCatView, $"SnapshotStore: active DB -> {DatabasePath}");
    }

    // pe_hash is hex, but sanitise defensively so it can never escape the
    // filename (path traversal / invalid chars).
    private static string SanitizePeHash(string? peHash)
    {
        if (string.IsNullOrEmpty(peHash)) return "";
        var sb = new StringBuilder(peHash.Length);
        foreach (var c in peHash)
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();

    private static void EnsureProviderInitialised()
    {
        if (s_initialised) return;
        lock (s_initLock)
        {
            if (s_initialised) return;
            SQLitePCL.Batteries_V2.Init();
            s_initialised = true;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS snapshots(
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                label           TEXT,
                captured_at     TEXT,
                pe_hash         TEXT,
                game_session_id TEXT,
                ue_version      INTEGER,
                object_count    INTEGER,
                field_count     INTEGER,
                scope           TEXT
            );
            CREATE TABLE IF NOT EXISTS fields(
                snapshot_id     INTEGER NOT NULL,
                class_fqn       TEXT,
                norm_path       TEXT,
                outer_chain     TEXT,
                prop_name       TEXT,
                prop_offset     INTEGER,
                declared_type   TEXT,
                gobjects_index  INTEGER,
                obj_addr        TEXT,
                numeric_value   REAL,
                hex             TEXT,
                array_field     TEXT,
                elem_index      INTEGER,
                inner_key_name  TEXT,
                inner_key_value TEXT,
                inner_prop_name TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_fields_snap ON fields(snapshot_id);
            CREATE INDEX IF NOT EXISTS ix_strict      ON fields(snapshot_id, class_fqn, norm_path,      prop_name);
            CREATE INDEX IF NOT EXISTS ix_loose       ON fields(snapshot_id, class_fqn, outer_chain,    prop_name);
            CREATE INDEX IF NOT EXISTS ix_insession   ON fields(snapshot_id, class_fqn, gobjects_index, prop_name);
            """, ct);
    }

    public async Task<long> CreateSnapshotAsync(SnapshotMeta meta, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots(label, captured_at, pe_hash, game_session_id, ue_version, object_count, field_count, scope)
            VALUES ($label, $at, $pe, $sess, $ue, 0, 0, $scope);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$label", meta.Label);
        cmd.Parameters.AddWithValue("$at",    meta.CapturedAt);
        cmd.Parameters.AddWithValue("$pe",    meta.PeHash);
        cmd.Parameters.AddWithValue("$sess",  meta.GameSessionId);
        cmd.Parameters.AddWithValue("$ue",    meta.UeVersion);
        cmd.Parameters.AddWithValue("$scope", meta.Scope);
        var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        meta.Id = id;
        _log?.Info(Constants.LogCatView, $"SnapshotStore: created snapshot #{id} ({meta.Label})");
        return id;
    }

    public async Task<int> WriteChunkAsync(long snapshotId, IReadOnlyList<SnapshotCapturedObject> objects,
                                           CancellationToken ct = default)
    {
        if (objects.Count == 0) return 0;

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fields(snapshot_id, class_fqn, norm_path, outer_chain, prop_name, prop_offset,
                               declared_type, gobjects_index, obj_addr, numeric_value, hex)
            VALUES ($snap, $cls, $np, $oc, $pn, $off, $dt, $idx, $addr, $num, $hex);
            """;
        var pSnap = cmd.Parameters.Add("$snap", SqliteType.Integer);
        var pCls  = cmd.Parameters.Add("$cls",  SqliteType.Text);
        var pNp   = cmd.Parameters.Add("$np",   SqliteType.Text);
        var pOc   = cmd.Parameters.Add("$oc",   SqliteType.Text);
        var pPn   = cmd.Parameters.Add("$pn",   SqliteType.Text);
        var pOff  = cmd.Parameters.Add("$off",  SqliteType.Integer);
        var pDt   = cmd.Parameters.Add("$dt",   SqliteType.Text);
        var pIdx  = cmd.Parameters.Add("$idx",  SqliteType.Integer);
        var pAddr = cmd.Parameters.Add("$addr", SqliteType.Text);
        var pNum  = cmd.Parameters.Add("$num",  SqliteType.Real);
        var pHex  = cmd.Parameters.Add("$hex",  SqliteType.Text);

        int rows = 0;
        foreach (var obj in objects)
        {
            string normPath = SnapshotIdentity.NormalizePath(obj.Path);
            foreach (var f in obj.Fields)
            {
                pSnap.Value = snapshotId;
                pCls.Value  = obj.ClassName;
                pNp.Value   = normPath;
                pOc.Value   = obj.OuterClassName;
                pPn.Value   = f.Name;
                pOff.Value  = f.Offset;
                pDt.Value   = f.Type;
                pIdx.Value  = obj.Index;
                pAddr.Value = obj.Addr;
                pNum.Value  = SnapshotNumeric.TryFromHex(f.Type, f.Hex, out var num)
                                ? num : (object)DBNull.Value;
                pHex.Value  = f.Hex;
                await cmd.ExecuteNonQueryAsync(ct);
                rows++;
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task FinalizeSnapshotAsync(long snapshotId, int objectCount, int fieldCount,
                                            CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE snapshots SET object_count=$oc, field_count=$fc WHERE id=$id;";
        cmd.Parameters.AddWithValue("$oc", objectCount);
        cmd.Parameters.AddWithValue("$fc", fieldCount);
        cmd.Parameters.AddWithValue("$id", snapshotId);
        await cmd.ExecuteNonQueryAsync(ct);
        _log?.Info(Constants.LogCatView,
            $"SnapshotStore: finalised snapshot #{snapshotId} ({objectCount} objects, {fieldCount} fields)");
    }

    public async Task<IReadOnlyList<SnapshotMeta>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, captured_at, pe_hash, game_session_id, ue_version, object_count, field_count, scope
            FROM snapshots ORDER BY id DESC;
            """;
        var list = new List<SnapshotMeta>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SnapshotMeta
            {
                Id            = reader.GetInt64(0),
                Label         = reader.IsDBNull(1) ? "" : reader.GetString(1),
                CapturedAt    = reader.IsDBNull(2) ? "" : reader.GetString(2),
                PeHash        = reader.IsDBNull(3) ? "" : reader.GetString(3),
                GameSessionId = reader.IsDBNull(4) ? "" : reader.GetString(4),
                UeVersion     = reader.IsDBNull(5) ? 0  : reader.GetInt32(5),
                ObjectCount   = reader.IsDBNull(6) ? 0  : reader.GetInt32(6),
                FieldCount    = reader.IsDBNull(7) ? 0  : reader.GetInt32(7),
                Scope         = reader.IsDBNull(8) ? "" : reader.GetString(8),
            });
        }
        return list;
    }

    public async Task DeleteSnapshotAsync(long snapshotId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fields WHERE snapshot_id=$id; DELETE FROM snapshots WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        await cmd.ExecuteNonQueryAsync(ct);
        _log?.Info(Constants.LogCatView, $"SnapshotStore: deleted snapshot #{snapshotId}");
    }
}
