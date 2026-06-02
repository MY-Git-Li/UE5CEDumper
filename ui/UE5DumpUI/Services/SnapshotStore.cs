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

        // Second command for struct-array element rows (carries the array
        // columns; SPC/Pivot inner-join on array_field + inner_key + inner_prop).
        await using var arrCmd = conn.CreateCommand();
        arrCmd.Transaction = tx;
        arrCmd.CommandText = """
            INSERT INTO fields(snapshot_id, class_fqn, norm_path, outer_chain, prop_name, prop_offset,
                               declared_type, gobjects_index, obj_addr, numeric_value, hex,
                               array_field, elem_index, inner_key_name, inner_key_value, inner_prop_name)
            VALUES ($snap, $cls, $np, $oc, $pn, $off, $dt, $idx, $addr, $num, $hex,
                    $af, $ei, $ikn, $ikv, $ipn);
            """;
        var aSnap = arrCmd.Parameters.Add("$snap", SqliteType.Integer);
        var aCls  = arrCmd.Parameters.Add("$cls",  SqliteType.Text);
        var aNp   = arrCmd.Parameters.Add("$np",   SqliteType.Text);
        var aOc   = arrCmd.Parameters.Add("$oc",   SqliteType.Text);
        var aPn   = arrCmd.Parameters.Add("$pn",   SqliteType.Text);
        var aOff  = arrCmd.Parameters.Add("$off",  SqliteType.Integer);
        var aDt   = arrCmd.Parameters.Add("$dt",   SqliteType.Text);
        var aIdx  = arrCmd.Parameters.Add("$idx",  SqliteType.Integer);
        var aAddr = arrCmd.Parameters.Add("$addr", SqliteType.Text);
        var aNum  = arrCmd.Parameters.Add("$num",  SqliteType.Real);
        var aHex  = arrCmd.Parameters.Add("$hex",  SqliteType.Text);
        var aAf   = arrCmd.Parameters.Add("$af",   SqliteType.Text);
        var aEi   = arrCmd.Parameters.Add("$ei",   SqliteType.Integer);
        var aIkn  = arrCmd.Parameters.Add("$ikn",  SqliteType.Text);
        var aIkv  = arrCmd.Parameters.Add("$ikv",  SqliteType.Text);
        var aIpn  = arrCmd.Parameters.Add("$ipn",  SqliteType.Text);

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

            // Struct-array element rows (one per inner numeric field).
            foreach (var arr in obj.Arrays)
            {
                foreach (var el in arr.Elements)
                {
                    object keyName  = string.IsNullOrEmpty(el.KeyName) ? DBNull.Value : el.KeyName;
                    object keyValue = string.IsNullOrEmpty(el.KeyName) ? DBNull.Value : el.KeyValue;
                    foreach (var f in el.Fields)
                    {
                        aSnap.Value = snapshotId;
                        aCls.Value  = obj.ClassName;
                        aNp.Value   = normPath;
                        aOc.Value   = obj.OuterClassName;
                        aPn.Value   = f.Name;
                        aOff.Value  = f.Offset;
                        aDt.Value   = f.Type;
                        aIdx.Value  = obj.Index;
                        aAddr.Value = obj.Addr;
                        aNum.Value  = SnapshotNumeric.TryFromHex(f.Type, f.Hex, out var anum)
                                        ? anum : (object)DBNull.Value;
                        aHex.Value  = f.Hex;
                        aAf.Value   = arr.Field;
                        aEi.Value   = el.Index;
                        aIkn.Value  = keyName;
                        aIkv.Value  = keyValue;
                        aIpn.Value  = f.Name;
                        await arrCmd.ExecuteNonQueryAsync(ct);
                        rows++;
                    }
                }
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

        // Estimate each snapshot's on-disk size by pro-rating the DB file size
        // across all field rows (snapshots share one per-game DB file).
        long fileBytes = FileSizeOf(DatabasePath);
        long totalFields = 0;
        foreach (var m in list) totalFields += m.FieldCount;
        if (totalFields > 0 && fileBytes > 0)
        {
            double bytesPerField = (double)fileBytes / totalFields;
            foreach (var m in list) m.EstBytes = (long)(m.FieldCount * bytesPerField);
        }
        return list;
    }

    public async Task<SnapshotUsage> GetUsageAsync(CancellationToken ct = default)
    {
        var usage = new SnapshotUsage();
        await using (var conn = await OpenAsync(ct))
        {
            // Fold the WAL back into the .db so the file size reflects all data.
            await ExecAsync(conn, "PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
            usage.SnapshotCount = (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }
        usage.GameDbBytes   = FileSizeOf(DatabasePath);
        usage.AllGamesBytes = AllGamesBytes();
        return usage;
    }

    public async Task<int> EnforceQuotaAsync(long quotaBytes, CancellationToken ct = default)
    {
        if (quotaBytes <= 0) return 0;  // unlimited

        await using var conn = await OpenAsync(ct);
        await ExecAsync(conn, "PRAGMA wal_checkpoint(TRUNCATE);", ct);

        long fileBytes = FileSizeOf(DatabasePath);
        if (fileBytes <= quotaBytes) return 0;

        // Read snapshots newest-first with their field counts.
        var rows = new List<(long id, int fields)>();
        long totalFields = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, field_count FROM snapshots ORDER BY id DESC;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int fc = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                rows.Add((r.GetInt64(0), fc));
                totalFields += fc;
            }
        }
        if (rows.Count <= 1) return 0;  // always keep at least the newest

        double bytesPerField = totalFields > 0 ? (double)fileBytes / totalFields : 0;
        long kept = 0;
        var dropIds = new List<long>();
        bool keeping = true;
        for (int i = 0; i < rows.Count; i++)
        {
            long est = (long)(rows[i].fields * bytesPerField);
            if (keeping && (i == 0 || kept + est <= quotaBytes))
                kept += est;
            else
            {
                keeping = false;       // once over, every OLDER snapshot drops too
                dropIds.Add(rows[i].id);
            }
        }
        if (dropIds.Count == 0) return 0;

        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM fields WHERE snapshot_id=$id; DELETE FROM snapshots WHERE id=$id;";
            var p = del.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in dropIds) { p.Value = id; await del.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
        }
        // Reclaim disk now that rows are gone (DELETE alone doesn't shrink).
        await ExecAsync(conn, "VACUUM;", ct);
        await ExecAsync(conn, "PRAGMA wal_checkpoint(TRUNCATE);", ct);

        _log?.Info(Constants.LogCatView,
            $"SnapshotStore: quota eviction dropped {dropIds.Count} oldest snapshot(s)");
        return dropIds.Count;
    }

    private static long FileSizeOf(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    private long AllGamesBytes()
    {
        try
        {
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(_dir, $"{Constants.SnapshotDbPrefix}.*.db"))
                sum += FileSizeOf(f);
            return sum;
        }
        catch { return 0; }
    }

    public async Task<SnapshotDiffResult> DiffSnapshotsAsync(
        long idA, long idB, SnapshotDiffFilter filter, CancellationToken ct = default)
    {
        var result = new SnapshotDiffResult();
        int max = filter.MaxRows > 0 ? filter.MaxRows : 50000;

        await using var conn = await OpenAsync(ct);

        // --- Changed: same (class, index, prop), different bytes ---
        await using (var cmd = conn.CreateCommand())
        {
            var sql = new StringBuilder(@"
SELECT a.class_fqn, b.norm_path, a.gobjects_index, a.prop_name, a.prop_offset, a.declared_type,
       b.obj_addr, a.hex, b.hex, a.numeric_value, b.numeric_value
FROM fields a JOIN fields b
  ON a.snapshot_id=$A AND b.snapshot_id=$B
  AND a.class_fqn=b.class_fqn AND a.gobjects_index=b.gobjects_index AND a.prop_name=b.prop_name
WHERE a.hex <> b.hex AND a.array_field IS NULL AND b.array_field IS NULL");
            cmd.Parameters.AddWithValue("$A", idA);
            cmd.Parameters.AddWithValue("$B", idB);
            if (!string.IsNullOrEmpty(filter.ClassContains))
            {
                sql.Append(" AND a.class_fqn LIKE $cls");
                cmd.Parameters.AddWithValue("$cls", $"%{filter.ClassContains}%");
            }
            if (!string.IsNullOrEmpty(filter.PropContains))
            {
                sql.Append(" AND a.prop_name LIKE $prop");
                cmd.Parameters.AddWithValue("$prop", $"%{filter.PropContains}%");
            }
            if (filter.Direction == SnapshotDiffDirection.Up)
                sql.Append(" AND b.numeric_value > a.numeric_value");
            else if (filter.Direction == SnapshotDiffDirection.Down)
                sql.Append(" AND b.numeric_value < a.numeric_value");
            sql.Append(" LIMIT $lim;");
            cmd.Parameters.AddWithValue("$lim", max + 1);  // +1 to detect truncation
            cmd.CommandText = sql.ToString();

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (result.Changed.Count >= max) { result.Truncated = true; break; }
                string type   = r.IsDBNull(5) ? "" : r.GetString(5);
                string oldHex = r.IsDBNull(7) ? "" : r.GetString(7);
                string newHex = r.IsDBNull(8) ? "" : r.GetString(8);
                double? oldNum = r.IsDBNull(9)  ? null : r.GetDouble(9);
                double? newNum = r.IsDBNull(10) ? null : r.GetDouble(10);
                var dir = (oldNum.HasValue && newNum.HasValue)
                    ? (newNum > oldNum ? SnapshotDiffDirection.Up
                       : newNum < oldNum ? SnapshotDiffDirection.Down
                       : SnapshotDiffDirection.None)
                    : SnapshotDiffDirection.None;
                result.Changed.Add(new SnapshotDiffRow
                {
                    ClassName    = r.IsDBNull(0) ? "" : r.GetString(0),
                    NormPath     = r.IsDBNull(1) ? "" : r.GetString(1),
                    ObjectIndex  = r.IsDBNull(2) ? -1 : r.GetInt32(2),
                    PropName     = r.IsDBNull(3) ? "" : r.GetString(3),
                    PropOffset   = r.IsDBNull(4) ? 0  : r.GetInt32(4),
                    DeclaredType = type,
                    ObjAddr      = r.IsDBNull(6) ? "" : r.GetString(6),
                    OldValue     = SnapshotNumeric.Render(type, oldHex),
                    NewValue     = SnapshotNumeric.Render(type, newHex),
                    Direction    = dir,
                });
            }
        }

        // --- Added / Removed field churn (counts only) ---
        if (filter.IncludeAddedRemoved)
        {
            result.RemovedCount = await CountChurnAsync(conn, idA, idB, ct);  // in A, not B
            result.AddedCount   = await CountChurnAsync(conn, idB, idA, ct);  // in B, not A
        }
        return result;
    }

    // Count fields present in `inSnap` whose (class, index, prop) key has no
    // match in `notInSnap` (uses the ix_insession index for the anti-join).
    private static async Task<int> CountChurnAsync(
        SqliteConnection conn, long inSnap, long notInSnap, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(*) FROM fields a WHERE a.snapshot_id=$in AND a.array_field IS NULL AND NOT EXISTS (
  SELECT 1 FROM fields b WHERE b.snapshot_id=$notin AND b.array_field IS NULL
    AND b.class_fqn=a.class_fqn AND b.gobjects_index=a.gobjects_index AND b.prop_name=a.prop_name);";
        cmd.Parameters.AddWithValue("$in", inSnap);
        cmd.Parameters.AddWithValue("$notin", notInSnap);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<SpcResult> SpcQueryAsync(SpcQuery query, CancellationToken ct = default)
    {
        var result = new SpcResult { SnapshotCount = query.SnapshotIds.Count };
        // Compile throws ArgumentException for < 2 snapshots or a mismatched
        // predicate count — let it propagate so the VM surfaces the error.
        var compiled = SpcQueryBuilder.Compile(query);
        int n = query.SnapshotIds.Count;
        int max = query.MaxRows > 0 ? query.MaxRows : 50000;

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        if (compiled.ClassLike != null) cmd.Parameters.AddWithValue("$cls", compiled.ClassLike);
        if (compiled.PropLike  != null) cmd.Parameters.AddWithValue("$prop", compiled.PropLike);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        // Column layout (see SpcQueryBuilder): 0 class, 1 norm_path, 2 prop_name,
        // 3 prop_offset, 4 declared_type, 5 obj_addr, then n hex columns.
        const int hexBase = 6;
        while (await r.ReadAsync(ct))
        {
            if (result.Rows.Count >= max) { result.Truncated = true; break; }
            string type = r.IsDBNull(4) ? "" : r.GetString(4);
            var row = new SpcResultRow
            {
                ClassName    = r.IsDBNull(0) ? "" : r.GetString(0),
                NormPath     = r.IsDBNull(1) ? "" : r.GetString(1),
                PropName     = r.IsDBNull(2) ? "" : r.GetString(2),
                PropOffset   = r.IsDBNull(3) ? 0  : r.GetInt32(3),
                DeclaredType = type,
                ObjAddr      = r.IsDBNull(5) ? "" : r.GetString(5),
            };
            for (int i = 0; i < n; i++)
            {
                string hex = r.IsDBNull(hexBase + i) ? "" : r.GetString(hexBase + i);
                row.Values.Add(SnapshotNumeric.Render(type, hex));
            }
            result.Rows.Add(row);
        }
        return result;
    }

    public async Task<IReadOnlyList<PivotClassInfo>> ListPivotClassesAsync(
        long snapshotId, CancellationToken ct = default)
    {
        var list = new List<PivotClassInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT class_fqn, COUNT(DISTINCT gobjects_index) AS instances
            FROM fields WHERE snapshot_id=$s AND array_field IS NULL
            GROUP BY class_fqn ORDER BY instances DESC, class_fqn;
            """;
        cmd.Parameters.AddWithValue("$s", snapshotId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PivotClassInfo
            {
                ClassName     = r.IsDBNull(0) ? "" : r.GetString(0),
                InstanceCount = r.IsDBNull(1) ? 0  : r.GetInt32(1),
            });
        return list;
    }

    public async Task<IReadOnlyList<PivotFieldInfo>> ListPivotFieldsAsync(
        long snapshotId, string className, CancellationToken ct = default)
    {
        var list = new List<PivotFieldInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT prop_name, declared_type,
                   COUNT(DISTINCT hex) AS distinctVals, COUNT(*) AS instances
            FROM fields WHERE snapshot_id=$s AND class_fqn=$c AND array_field IS NULL
            GROUP BY prop_name, declared_type ORDER BY prop_name;
            """;
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$c", className);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PivotFieldInfo
            {
                Name          = r.IsDBNull(0) ? "" : r.GetString(0),
                DeclaredType  = r.IsDBNull(1) ? "" : r.GetString(1),
                DistinctCount = r.IsDBNull(2) ? 0  : r.GetInt32(2),
                InstanceCount = r.IsDBNull(3) ? 0  : r.GetInt32(3),
            });
        return list;
    }

    public async Task<PivotResult> PivotAsync(PivotQuery query, CancellationToken ct = default)
    {
        // Fetch only the rows the engine needs: the key field (Field mode) + the
        // value fields, for this class. Prop names come from our own field list
        // (DB-sourced identifiers) but are still parameterised defensively.
        var props = new List<string>();
        if (query.KeyMode == PivotKeyMode.Field && !string.IsNullOrEmpty(query.KeyField))
            props.Add(query.KeyField);
        foreach (var v in query.ValueFields)
            if (!props.Contains(v)) props.Add(v);

        var rows = new List<PivotInputRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder("""
            SELECT gobjects_index, norm_path, obj_addr, prop_name, prop_offset, declared_type, hex
            FROM fields WHERE snapshot_id=$s AND class_fqn=$c AND array_field IS NULL
            """);
        cmd.Parameters.AddWithValue("$s", query.SnapshotId);
        cmd.Parameters.AddWithValue("$c", query.ClassName);
        if (props.Count > 0)
        {
            sql.Append(" AND prop_name IN (");
            for (int i = 0; i < props.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var name = "$p" + i;
                sql.Append(name);
                cmd.Parameters.AddWithValue(name, props[i]);
            }
            sql.Append(')');
        }
        sql.Append(" ORDER BY gobjects_index;");
        cmd.CommandText = sql.ToString();

        // Defensive input cap: a pathologically large class shouldn't pull an
        // unbounded row set into memory. Far above any realistic class fan-out;
        // if it ever fires we log it and flag the result truncated (no silent
        // caps — see the repo's lessons-learned).
        const int fetchRowCap = 2_000_000;
        bool capped = false;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (rows.Count >= fetchRowCap) { capped = true; break; }
                rows.Add(new PivotInputRow
                {
                    ObjectIndex  = r.IsDBNull(0) ? -1 : r.GetInt64(0),
                    NormPath     = r.IsDBNull(1) ? "" : r.GetString(1),
                    ObjAddr      = r.IsDBNull(2) ? "" : r.GetString(2),
                    PropName     = r.IsDBNull(3) ? "" : r.GetString(3),
                    PropOffset   = r.IsDBNull(4) ? 0  : r.GetInt32(4),
                    DeclaredType = r.IsDBNull(5) ? "" : r.GetString(5),
                    Hex          = r.IsDBNull(6) ? "" : r.GetString(6),
                });
            }
        }
        if (capped)
            _log?.Warn(Constants.LogCatView,
                $"Pivot: row fetch hit the {fetchRowCap:N0} cap for class {query.ClassName} — results truncated");

        var result = PivotEngine.Build(rows, query);
        if (capped) result.Truncated = true;
        return result;
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
