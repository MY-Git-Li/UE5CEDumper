using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// SQLite snapshot store (raw ADO.NET — no EF Core, for trim/AOT safety). One DB
/// per game at %LOCALAPPDATA%\UE5CEDumper\snapshots.&lt;pe_hash&gt;.db. The
/// <c>fields</c> table is denormalised (identity columns per row) so the SPC / Pivot
/// / Diff self-joins hit a single-table covering index (ix_strict/loose/insession) —
/// the fast path. Schema: docs/experimental-snapshot-spc-pivot.md §6.
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
        try
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", ct);
            await EnsureSchemaAsync(conn, ct);
            return conn;
        }
        catch
        {
            // OpenAsync succeeded but schema init threw/cancelled (the tab-switch
            // token can land mid-EnsureSchema): dispose so the native handle is
            // released and the pooled connection is returned, not leaked.
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Current on-disk schema version. The <c>fields</c> table is
    /// denormalised (identity columns per row). v4 DROPPED the three heavy composite
    /// covering indexes (ix_strict/loose/insession — ~450 MB on a ~1.8M-row capture):
    /// Diff and SPC now run as in-memory hash-joins (see <see cref="DiffSnapshotsAsync"/>
    /// / <see cref="SpcQueryAsync"/>), which need only a fast <c>WHERE snapshot_id</c>
    /// scan, and Pivot filters by (snapshot_id, class_fqn). So a single lean
    /// <c>ix_fields(snapshot_id, class_fqn)</c> serves every query — roughly halving
    /// the DB. Bump this on any incompatible change: an older DB is dropped +
    /// recreated on open (experimental captures recapture in ~2 min; no migration).</summary>
    private const long SchemaVersion = 4;

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        long ver;
        await using (var vcmd = conn.CreateCommand())
        {
            vcmd.CommandText = "PRAGMA user_version;";
            ver = (long)(await vcmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        // Old (or experimental v2) schema detected -> just drop and recreate. The
        // DROPs are no-ops on a brand-new (empty) DB. Covers v1 (user_version 0) and
        // the reverted v2 normalised layout alike.
        if (ver < SchemaVersion)
        {
            await ExecAsync(conn, """
                DROP VIEW  IF EXISTS vfields;
                DROP TABLE IF EXISTS fields;
                DROP TABLE IF EXISTS objects;
                DROP TABLE IF EXISTS snapshots;
                """, ct);
        }

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
            CREATE INDEX IF NOT EXISTS ix_fields ON fields(snapshot_id, class_fqn);
            """, ct);

        // Pivot class-index (additive — no SchemaVersion bump, so it doesn't drop
        // existing snapshots). Precomputes the per-class instance count once per
        // snapshot so the Class Pivot picker reads a tiny table instead of running
        // a COUNT(DISTINCT) GROUP BY over ~1.7M rows on every open (the 10s+ scan).
        // pivot_index_built marks (snapshot, scalar/array) as computed so an empty
        // result (a snapshot with no array classes) isn't mistaken for "not built".
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS class_counts(
                snapshot_id    INTEGER NOT NULL,
                class_fqn      TEXT,
                is_array       INTEGER NOT NULL,
                instance_count INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_class_counts ON class_counts(snapshot_id, is_array);
            CREATE TABLE IF NOT EXISTS pivot_index_built(
                snapshot_id INTEGER NOT NULL,
                is_array    INTEGER NOT NULL,
                PRIMARY KEY(snapshot_id, is_array)
            );
            """, ct);

        if (ver < SchemaVersion)
            await ExecAsync(conn, $"PRAGMA user_version={SchemaVersion};", ct);
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

        // Precompute the pivot class-index now, while we're still in the capture
        // flow (the user already expects a wait) — so the Class Pivot picker opens
        // instantly later instead of running a 10s+ GROUP BY on first selection.
        // Best-effort: if it throws/cancels, the list methods lazily build it.
        try
        {
            // forceRebuild: a lazy build triggered by browsing this snapshot in the
            // Pivot tab WHILE it was still capturing would have persisted counts +
            // the built-marker over partial rows, and the marker check would then
            // make this finalize call a no-op — permanently wrong counts. Forcing a
            // rebuild here (snapshot now complete) makes the final state authoritative.
            await EnsurePivotIndexAsync(conn, snapshotId, 0, ct, forceRebuild: true);
            await EnsurePivotIndexAsync(conn, snapshotId, 1, ct, forceRebuild: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn(Constants.LogCatView,
                $"SnapshotStore: pivot index precompute failed for #{snapshotId} (will build lazily): {ex.Message}");
        }

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
            del.CommandText =
                "DELETE FROM fields WHERE snapshot_id=$id; " +
                "DELETE FROM class_counts WHERE snapshot_id=$id; " +
                "DELETE FROM pivot_index_built WHERE snapshot_id=$id; " +
                "DELETE FROM snapshots WHERE id=$id;";
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
        ct.ThrowIfCancellationRequested();   // bail before opening a connection
        var result = new SnapshotDiffResult();
        int max = filter.MaxRows > 0 ? filter.MaxRows : 50000;

        string classContains = filter.ClassContains ?? "";
        string propContains  = filter.PropContains ?? "";
        // N1: per-game class denylist. Filter both A-load and B-stream so the
        // Added/Removed churn counts also reflect only non-denylisted classes.
        var deny = filter.ExcludedClasses is { Count: > 0 } ? filter.ExcludedClasses : null;

        await using var conn = await OpenAsync(ct);

        // In-memory hash join (the technique the `discrete` sister project uses):
        // stream both snapshots' scalar fields into a dictionary keyed by the
        // in-session identity, then diff in two O(n) passes with O(1) hash lookups.
        // This is independent of index/schema shape — far faster than a SQL
        // self-join over ~1.8M rows, which only stays quick with a perfect
        // single-table composite covering index. Key = (class_fqn, gobjects_index,
        // prop_name); unique within one snapshot.

        // Intern the high-repetition class/prop strings to cut allocations.
        var intern = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string s) { if (intern.TryGetValue(s, out var v)) return v; intern[s] = s; return s; }

        // Snapshot A → { key : (hex, numeric) }. Only the old value + direction
        // input is kept (display columns come from B, the newer snapshot).
        var aMap = new Dictionary<(string cls, long idx, string prop), (string hex, double? num)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT class_fqn, gobjects_index, prop_name, hex, numeric_value " +
                              "FROM fields WHERE snapshot_id=$id AND array_field IS NULL;";
            cmd.Parameters.AddWithValue("$id", idA);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int rowCount = 0;
            while (await r.ReadAsync(ct))
            {
                // ReadAsync ignores ct under Microsoft.Data.Sqlite — explicit check.
                if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                string aCls = r.IsDBNull(0) ? "" : r.GetString(0);
                if (deny != null && deny.Contains(aCls)) continue;
                var key = (Intern(aCls),
                           r.IsDBNull(1) ? -1L : r.GetInt64(1),
                           Intern(r.IsDBNull(2) ? "" : r.GetString(2)));
                aMap[key] = (r.IsDBNull(3) ? "" : r.GetString(3),
                             r.IsDBNull(4) ? (double?)null : r.GetDouble(4));
            }
        }

        // Snapshot B: stream rows, hash-look-up A. matched = common keys (changed +
        // unchanged); bTotal = all B rows — together they give the Added/Removed churn.
        int matched = 0, bTotal = 0;
        // N1: Top-N noise picker — accumulate per-class hit count + sample props
        // over the changed-row set (counts only what the user actually sees).
        var noise = new NoiseAccumulator();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT class_fqn, gobjects_index, prop_name, hex, numeric_value, " +
                              "norm_path, obj_addr, prop_offset, declared_type " +
                              "FROM fields WHERE snapshot_id=$id AND array_field IS NULL;";
            cmd.Parameters.AddWithValue("$id", idB);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int rowCount = 0;
            while (await r.ReadAsync(ct))
            {
                if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                string cls  = r.IsDBNull(0) ? "" : r.GetString(0);
                if (deny != null && deny.Contains(cls)) continue;  // also skip from bTotal so churn is correct
                bTotal++;
                long   idx  = r.IsDBNull(1) ? -1L : r.GetInt64(1);
                string prop = r.IsDBNull(2) ? "" : r.GetString(2);
                if (!aMap.TryGetValue((Intern(cls), idx, Intern(prop)), out var a)) continue;  // B-only (added)
                matched++;

                string bHex = r.IsDBNull(3) ? "" : r.GetString(3);
                if (string.Equals(a.hex, bHex, StringComparison.Ordinal)) continue;  // unchanged

                // Optional store-side filters (the VM passes an empty filter and
                // filters client-side, but honour these for API completeness).
                if (classContains.Length > 0 && cls.IndexOf(classContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (propContains.Length  > 0 && prop.IndexOf(propContains,  StringComparison.OrdinalIgnoreCase) < 0) continue;

                double? bNum = r.IsDBNull(4) ? (double?)null : r.GetDouble(4);
                var dir = (a.num.HasValue && bNum.HasValue)
                    ? (bNum > a.num ? SnapshotDiffDirection.Up
                       : bNum < a.num ? SnapshotDiffDirection.Down
                       : SnapshotDiffDirection.None)
                    : SnapshotDiffDirection.None;
                if (filter.Direction == SnapshotDiffDirection.Up   && dir != SnapshotDiffDirection.Up)   continue;
                if (filter.Direction == SnapshotDiffDirection.Down && dir != SnapshotDiffDirection.Down) continue;

                // Count noise even past the row cap so the picker isn't biased
                // toward whichever class happened to land in the first 50k rows.
                noise.Bump(cls, prop);
                if (result.Changed.Count >= max) { result.Truncated = true; continue; }  // keep counting churn
                string type = r.IsDBNull(8) ? "" : r.GetString(8);
                result.Changed.Add(new SnapshotDiffRow
                {
                    ClassName    = cls,
                    NormPath     = r.IsDBNull(5) ? "" : r.GetString(5),
                    ObjectIndex  = (int)idx,
                    PropName     = prop,
                    PropOffset   = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    DeclaredType = type,
                    ObjAddr      = r.IsDBNull(6) ? "" : r.GetString(6),
                    OldValue     = SnapshotNumeric.Render(type, a.hex),
                    NewValue     = SnapshotNumeric.Render(type, bHex),
                    Direction    = dir,
                });
            }
        }

        // Added = B keys with no A match; Removed = A keys with no B match.
        if (filter.IncludeAddedRemoved)
        {
            result.AddedCount   = bTotal - matched;
            result.RemovedCount = aMap.Count - matched;
        }
        noise.WriteTo(result.TopContributors);
        return result;
    }

    public async Task<SpcResult> SpcQueryAsync(SpcQuery query, CancellationToken ct = default)
    {
        var result = new SpcResult { SnapshotCount = query.SnapshotIds.Count };
        int n = query.SnapshotIds.Count;
        if (n < 2)
            throw new ArgumentException("SPC needs at least two snapshots.", nameof(query));
        if (query.Predicates.Count != n)
            throw new ArgumentException("Predicate count must equal snapshot count.", nameof(query));
        ct.ThrowIfCancellationRequested();   // bail before opening a connection
        int max = query.MaxRows > 0 ? query.MaxRows : 50000;

        string classContains = query.ClassContains?.Trim() ?? "";
        string propContains  = query.PropContains?.Trim() ?? "";
        var mode = query.JoinMode;
        var abs  = query.AbsolutePredicates is { Count: > 0 } ? query.AbsolutePredicates : null;
        // N1: per-game class denylist. Null/empty = no filtering. The set is
        // captured once here so a concurrent UI-side mutation can't change the
        // predicate mid-query.
        var deny = query.ExcludedClasses is { Count: > 0 } ? query.ExcludedClasses : null;

        await using var conn = await OpenAsync(ct);

        // In-memory intersection (port of `discrete`): load the anchor (oldest)
        // snapshot's fields into a candidate dict keyed by the join identity, then
        // stream each later snapshot and keep only candidates that recur, appending
        // their value. One shrinking dict — no SQL self-join, no covering-index
        // dependency. Class/prop filters narrow at anchor load. Duplicate keys within
        // a snapshot (e.g. spawn siblings sharing a normalised path under Strict) keep
        // the first — collapses cross-product noise.
        var intern = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string s) { if (intern.TryGetValue(s, out var v)) return v; intern[s] = s; return s; }

        var cands = new Dictionary<string, Cand>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SpcRowSql;
            cmd.Parameters.AddWithValue("$id", query.SnapshotIds[0]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int rowCount = 0;
            while (await r.ReadAsync(ct))
            {
                // Microsoft.Data.Sqlite's ReadAsync runs synchronously and ignores
                // the token, so the ONLY way to abort a multi-million-row scan is an
                // explicit periodic check. ~64k-row cadence keeps the overhead nil.
                if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                string cls  = r.IsDBNull(0) ? "" : r.GetString(0);
                if (deny != null && deny.Contains(cls)) continue;
                string prop = r.IsDBNull(4) ? "" : r.GetString(4);
                if (classContains.Length > 0 && cls.IndexOf(classContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (propContains.Length  > 0 && prop.IndexOf(propContains,  StringComparison.OrdinalIgnoreCase) < 0) continue;
                string key = SpcKey(mode, r, cls, prop);
                if (cands.ContainsKey(key)) continue;
                var c = new Cand { ClassName = Intern(cls), PropName = Intern(prop) };
                c.Hex.Add(r.IsDBNull(7) ? "" : r.GetString(7));
                c.Num.Add(r.IsDBNull(8) ? (double?)null : r.GetDouble(8));
                cands[key] = c;
            }
        }

        for (int i = 1; i < n && cands.Count > 0; i++)
        {
            foreach (var c in cands.Values) c.Seen = false;
            bool newest = i == n - 1;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SpcRowSql;
                cmd.Parameters.AddWithValue("$id", query.SnapshotIds[i]);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                int rowCount = 0;
                while (await r.ReadAsync(ct))
                {
                    if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    string cls  = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (deny != null && deny.Contains(cls)) continue;
                    string prop = r.IsDBNull(4) ? "" : r.GetString(4);
                    string key = SpcKey(mode, r, cls, prop);
                    if (!cands.TryGetValue(key, out var c) || c.Seen) continue;
                    c.Seen = true;
                    c.Hex.Add(r.IsDBNull(7) ? "" : r.GetString(7));
                    c.Num.Add(r.IsDBNull(8) ? (double?)null : r.GetDouble(8));
                    if (newest)
                    {
                        c.NormPath     = r.IsDBNull(1) ? "" : r.GetString(1);
                        c.ObjAddr      = r.IsDBNull(6) ? "" : r.GetString(6);
                        c.PropOffset   = r.IsDBNull(5) ? 0  : r.GetInt32(5);
                        c.DeclaredType = r.IsDBNull(3) ? "" : r.GetString(3);
                    }
                }
            }
            // Intersection: drop candidates absent from this snapshot.
            ct.ThrowIfCancellationRequested();
            List<string>? drop = null;
            foreach (var kv in cands)
                if (!kv.Value.Seen) (drop ??= new()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) cands.Remove(k);
        }

        // Evaluate the directional chain + absolute predicates; build result rows.
        // Accumulate per-class hit count + per-(class, prop) sub-count to drive the
        // post-query Top-N noise picker (N1).
        var noise = new NoiseAccumulator();
        int evalCount = 0;
        foreach (var c in cands.Values)
        {
            if ((++evalCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (c.Hex.Count != n) continue;
            if (!SpcEngine.Matches(c.Hex, c.Num, query.Predicates, abs)) continue;
            noise.Bump(c.ClassName, c.PropName);  // counted even when row cap is hit
            if (result.Rows.Count >= max) { result.Truncated = true; continue; }
            var row = new SpcResultRow
            {
                ClassName = c.ClassName, NormPath = c.NormPath, PropName = c.PropName,
                PropOffset = c.PropOffset, DeclaredType = c.DeclaredType, ObjAddr = c.ObjAddr,
            };
            for (int i = 0; i < n; i++) row.Values.Add(SnapshotNumeric.Render(c.DeclaredType, c.Hex[i]));
            result.Rows.Add(row);
        }
        noise.WriteTo(result.TopContributors);
        return result;
    }

    // 0 class,1 norm,2 outer,3 type,4 prop,5 offset,6 addr,7 hex,8 num,9 gobjects_index
    private const string SpcRowSql =
        "SELECT class_fqn, norm_path, outer_chain, declared_type, prop_name, prop_offset, " +
        "obj_addr, hex, numeric_value, gobjects_index " +
        "FROM fields WHERE snapshot_id=$id AND array_field IS NULL;";

    private static string SpcKey(SpcJoinMode mode, SqliteDataReader r, string cls, string prop) => mode switch
    {
        SpcJoinMode.Loose     => cls + "\u0001" + (r.IsDBNull(2) ? "" : r.GetString(2)) + "\u0001" + prop,
        SpcJoinMode.InSession => cls + "\u0001" + (r.IsDBNull(9) ? -1L : r.GetInt64(9)) + "\u0001" + prop,
        _ /* Strict */        => cls + "\u0001" + (r.IsDBNull(1) ? "" : r.GetString(1)) + "\u0001" + prop +
                                 "\u0001" + (r.IsDBNull(5) ? 0 : r.GetInt32(5)),
    };

    // One SPC candidate field: identity + its value sequence (+ display from newest).
    private sealed class Cand
    {
        public string ClassName = "", PropName = "", NormPath = "", ObjAddr = "", DeclaredType = "";
        public int  PropOffset;
        public bool Seen;
        public readonly List<string>  Hex = new();
        public readonly List<double?> Num = new();
    }

    // Compute the per-class instance counts for one snapshot ONCE and persist them
    // into class_counts (the expensive COUNT(DISTINCT gobjects_index) GROUP BY runs
    // server-side via INSERT...SELECT — no row marshalling). Idempotent: guarded by
    // the pivot_index_built marker so it never recomputes. isArray selects scalar
    // (0) vs struct-array (1) classes. Called eagerly at FinalizeSnapshot and lazily
    // from the list methods (covers snapshots captured before this feature).
    internal static async Task EnsurePivotIndexAsync(
        SqliteConnection conn, long snapshotId, int isArray, CancellationToken ct,
        bool forceRebuild = false)
    {
        if (!forceRebuild)
        {
            await using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT 1 FROM pivot_index_built WHERE snapshot_id=$s AND is_array=$a LIMIT 1;";
            chk.Parameters.AddWithValue("$s", snapshotId);
            chk.Parameters.AddWithValue("$a", isArray);
            if (await chk.ExecuteScalarAsync(ct) != null) return;   // already built
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        // Clear any prior counts for this snapshot+kind first: makes the rebuild
        // authoritative (forceRebuild path overwrites a partial mid-capture build)
        // AND idempotent against the dup-marker race where two concurrent lazy
        // callers both pass the check above and would otherwise double-INSERT.
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM class_counts WHERE snapshot_id=$s AND is_array=$a;";
            del.Parameters.AddWithValue("$s", snapshotId);
            del.Parameters.AddWithValue("$a", isArray);
            await del.ExecuteNonQueryAsync(ct);
        }
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = isArray == 0
                ? """
                  INSERT INTO class_counts(snapshot_id, class_fqn, is_array, instance_count)
                  SELECT snapshot_id, class_fqn, 0, COUNT(DISTINCT gobjects_index)
                  FROM fields WHERE snapshot_id=$s AND array_field IS NULL
                  GROUP BY class_fqn;
                  """
                : """
                  INSERT INTO class_counts(snapshot_id, class_fqn, is_array, instance_count)
                  SELECT snapshot_id, class_fqn, 1, COUNT(DISTINCT gobjects_index)
                  FROM fields WHERE snapshot_id=$s AND array_field IS NOT NULL
                  GROUP BY class_fqn;
                  """;
            ins.Parameters.AddWithValue("$s", snapshotId);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT OR IGNORE INTO pivot_index_built(snapshot_id, is_array) VALUES($s,$a);";
            mark.Parameters.AddWithValue("$s", snapshotId);
            mark.Parameters.AddWithValue("$a", isArray);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PivotClassInfo>> ListPivotClassesAsync(
        long snapshotId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<PivotClassInfo>();
        await using var conn = await OpenAsync(ct);
        await EnsurePivotIndexAsync(conn, snapshotId, 0, ct);   // builds once, then no-op
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT class_fqn, instance_count FROM class_counts
            WHERE snapshot_id=$s AND is_array=0
            ORDER BY instance_count DESC, class_fqn;
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
        ct.ThrowIfCancellationRequested();
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
        ct.ThrowIfCancellationRequested();   // bail before opening a connection
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
            int rowCount = 0;
            while (await r.ReadAsync(ct))
            {
                if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
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

    // --- Phase C6: array-element pivot ---------------------------------------

    public async Task<IReadOnlyList<PivotClassInfo>> ListPivotArrayClassesAsync(
        long snapshotId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<PivotClassInfo>();
        await using var conn = await OpenAsync(ct);
        await EnsurePivotIndexAsync(conn, snapshotId, 1, ct);   // array classes, built once
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT class_fqn, instance_count FROM class_counts
            WHERE snapshot_id=$s AND is_array=1
            ORDER BY instance_count DESC, class_fqn;
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

    public async Task<IReadOnlyList<PivotArrayFieldInfo>> ListPivotArrayFieldsAsync(
        long snapshotId, string className, CancellationToken ct = default)
    {
        var list = new List<PivotArrayFieldInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT array_field, COALESCE(inner_key_name, ''), COUNT(*) AS elems
            FROM fields WHERE snapshot_id=$s AND class_fqn=$c AND array_field IS NOT NULL
            GROUP BY array_field, inner_key_name ORDER BY array_field;
            """;
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$c", className);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PivotArrayFieldInfo
            {
                ArrayField   = r.IsDBNull(0) ? "" : r.GetString(0),
                InnerKeyName = r.IsDBNull(1) ? "" : r.GetString(1),
                ElementCount = r.IsDBNull(2) ? 0  : r.GetInt32(2),
            });
        return list;
    }

    public async Task<IReadOnlyList<PivotFieldInfo>> ListPivotArrayPropsAsync(
        long snapshotId, string className, string arrayField, CancellationToken ct = default)
    {
        var list = new List<PivotFieldInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT inner_prop_name, declared_type,
                   COUNT(DISTINCT hex) AS distinctVals, COUNT(*) AS elems
            FROM fields WHERE snapshot_id=$s AND class_fqn=$c AND array_field=$af AND array_field IS NOT NULL
            GROUP BY inner_prop_name, declared_type ORDER BY inner_prop_name;
            """;
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$c", className);
        cmd.Parameters.AddWithValue("$af", arrayField);
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

    public async Task<PivotResult> PivotArrayAsync(ArrayPivotQuery query, CancellationToken ct = default)
    {
        var props = new List<string>();
        foreach (var v in query.ValueProps)
            if (!props.Contains(v)) props.Add(v);

        var rows = new List<PivotInputRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder("""
            SELECT gobjects_index, elem_index, obj_addr, inner_key_value, inner_prop_name, declared_type, hex
            FROM fields WHERE snapshot_id=$s AND class_fqn=$c AND array_field=$af AND array_field IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("$s",  query.SnapshotId);
        cmd.Parameters.AddWithValue("$c",  query.ClassName);
        cmd.Parameters.AddWithValue("$af", query.ArrayField);
        if (props.Count > 0)
        {
            sql.Append(" AND inner_prop_name IN (");
            for (int i = 0; i < props.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var name = "$p" + i;
                sql.Append(name);
                cmd.Parameters.AddWithValue(name, props[i]);
            }
            sql.Append(')');
        }
        sql.Append(" ORDER BY gobjects_index, elem_index;");
        cmd.CommandText = sql.ToString();

        // Each (owner instance, element index) pair becomes one synthetic
        // "instance" so PivotEngine folds an element's inner props together; the
        // inner-key value becomes the Identity group key (reorder-/session-immune).
        var idMap = new Dictionary<(long, int), long>();
        long nextId = 0;
        const int fetchRowCap = 2_000_000;
        bool capped = false;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            int rowCount = 0;
            while (await r.ReadAsync(ct))
            {
                if ((++rowCount & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (rows.Count >= fetchRowCap) { capped = true; break; }
                long objIdx = r.IsDBNull(0) ? -1 : r.GetInt64(0);
                int  elem   = r.IsDBNull(1) ? -1 : r.GetInt32(1);
                var key = (objIdx, elem);
                if (!idMap.TryGetValue(key, out var sid)) { sid = nextId++; idMap[key] = sid; }
                rows.Add(new PivotInputRow
                {
                    ObjectIndex  = sid,
                    NormPath     = r.IsDBNull(3) ? "(no key)" : r.GetString(3),  // inner-key value = group key
                    ObjAddr      = r.IsDBNull(2) ? "" : r.GetString(2),
                    PropName     = r.IsDBNull(4) ? "" : r.GetString(4),
                    DeclaredType = r.IsDBNull(5) ? "" : r.GetString(5),
                    Hex          = r.IsDBNull(6) ? "" : r.GetString(6),
                });
            }
        }
        if (capped)
            _log?.Warn(Constants.LogCatView,
                $"Array pivot: row fetch hit the {fetchRowCap:N0} cap for {query.ClassName}.{query.ArrayField} — results truncated");

        var pq = new PivotQuery
        {
            KeyMode     = PivotKeyMode.Identity,   // group key = inner-key value (NormPath)
            ValueFields = props,
            MaxGroups   = query.MaxGroups,
        };
        var result = PivotEngine.Build(rows, pq);
        if (capped) result.Truncated = true;
        return result;
    }

    public async Task DeleteSnapshotAsync(long snapshotId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM fields WHERE snapshot_id=$id; " +
            "DELETE FROM class_counts WHERE snapshot_id=$id; " +
            "DELETE FROM pivot_index_built WHERE snapshot_id=$id; " +
            "DELETE FROM snapshots WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        await cmd.ExecuteNonQueryAsync(ct);
        _log?.Info(Constants.LogCatView, $"SnapshotStore: deleted snapshot #{snapshotId}");
    }

    public async Task DeleteAllSnapshotsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Truncate every table for the active game's DB, then VACUUM to reclaim
        // the (potentially multi-GB) file on disk.
        cmd.CommandText =
            "DELETE FROM fields; DELETE FROM class_counts; " +
            "DELETE FROM pivot_index_built; DELETE FROM snapshots;";
        await cmd.ExecuteNonQueryAsync(ct);
        await ExecAsync(conn, "VACUUM;", ct);
        _log?.Info(Constants.LogCatView, "SnapshotStore: deleted ALL snapshots for the active game");
    }

    // --- N1: Top-N noise contributor accumulator (used by SPC + Diff) ---
    //
    // Walks each emitted result row, keeps per-class hit counts + per-(class, prop)
    // sub-counts, then writes a Top-50 ranking with up to 3 sample props per class.
    // Pure in-memory; the rows that drive it are the same rows the user is about to
    // see, so the picker reflects the predicate they just ran rather than the raw
    // capture distribution.
    private sealed class NoiseAccumulator
    {
        private const int kTopN          = 50;
        private const int kSamplePropMax = 3;
        private readonly Dictionary<string, ClassBucket> _buckets =
            new(StringComparer.Ordinal);

        private sealed class ClassBucket
        {
            public int HitCount;
            public readonly Dictionary<string, int> PropCounts =
                new(StringComparer.Ordinal);
        }

        public void Bump(string className, string propName)
        {
            // A malformed (NULL→"") class_fqn shouldn't surface as a blank,
            // un-tickable picker row.
            if (string.IsNullOrEmpty(className)) return;
            if (!_buckets.TryGetValue(className, out var b))
            {
                b = new ClassBucket();
                _buckets[className] = b;
            }
            b.HitCount++;
            b.PropCounts.TryGetValue(propName, out var c);
            b.PropCounts[propName] = c + 1;
        }

        public void WriteTo(List<ClassNoiseRow> dest)
        {
            // Sort classes by hit count desc, tie-break by name for stability.
            var ordered = _buckets
                .Select(kv => (Cls: kv.Key, Count: kv.Value.HitCount, Props: kv.Value.PropCounts))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Cls, StringComparer.Ordinal)
                .Take(kTopN);

            foreach (var x in ordered)
            {
                var sampleProps = x.Props
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Take(kSamplePropMax)
                    .Select(p => p.Key)
                    .ToList();
                dest.Add(new ClassNoiseRow
                {
                    ClassName   = x.Cls,
                    HitCount    = x.Count,
                    SampleProps = sampleProps,
                });
            }
        }
    }

    // --- N1: per-game, PER-TAB class denylists (noise picker) ---
    //
    // Stored next to the per-game DB as snapshots.<pe_hash>.denylist.json so the
    // lists auto-follow the game and survive quota-driven snapshot eviction. The
    // file holds three INDEPENDENT lists (Diff / Spc / Pivot) — a class hidden in
    // one tab never affects the others. Source-gen JSON (AOT-safe), atomic write,
    // swallow-and-log on failure — mirrors ExperimentalGate's persistence pattern.

    private static readonly ClassDenylistJsonContext s_denylistJsonCtx =
        ClassDenylistJsonContext.Default;

    private string DenylistPath =>
        Path.Combine(_dir, $"{Constants.SnapshotDbPrefix}.{(_peHash.Length > 0 ? _peHash : "default")}.denylist.json");

    // Load the whole settings object (all three lists); defensive on missing /
    // corrupt file. Returns a fresh empty object on any failure.
    private ClassDenylistSettings LoadDenylistSettings()
    {
        try
        {
            var path = DenylistPath;
            if (!File.Exists(path)) return new ClassDenylistSettings();
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize(json, s_denylistJsonCtx.ClassDenylistSettings)
                   ?? new ClassDenylistSettings();
        }
        catch (Exception ex)
        {
            _log?.Warn(Constants.LogCatView,
                $"SnapshotStore: failed to load denylist, using empty: {ex.Message}");
            return new ClassDenylistSettings();
        }
    }

    private static List<string> ScopeList(ClassDenylistSettings s, DenylistScope scope) => scope switch
    {
        DenylistScope.Diff  => s.Diff,
        DenylistScope.Spc   => s.Spc,
        DenylistScope.Pivot => s.Pivot,
        _                   => s.Diff,
    };

    public HashSet<string> GetClassDenylist(DenylistScope scope)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ScopeList(LoadDenylistSettings(), scope))
            if (!string.IsNullOrEmpty(c)) set.Add(c);
        return set;
    }

    public void SetClassDenylist(DenylistScope scope, HashSet<string> classes)
    {
        // No-op without an active game — the default DB shouldn't accumulate
        // game-specific noise picks. UI gates the save behind SetActiveGame
        // already, so this is defence in depth.
        if (_peHash.Length == 0) return;
        try
        {
            // Read-modify-write: preserve the other two tabs' lists.
            var settings = LoadDenylistSettings();
            var sorted = new List<string>(classes);
            sorted.Sort(StringComparer.Ordinal);  // stable on-disk order for diffability
            switch (scope)
            {
                case DenylistScope.Diff:  settings.Diff  = sorted; break;
                case DenylistScope.Spc:   settings.Spc   = sorted; break;
                case DenylistScope.Pivot: settings.Pivot = sorted; break;
            }
            var json = System.Text.Json.JsonSerializer.Serialize(
                settings, s_denylistJsonCtx.ClassDenylistSettings);
            var path = DenylistPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            _log?.Info(Constants.LogCatView,
                $"SnapshotStore: saved {scope} denylist ({sorted.Count} classes) -> {path}");
        }
        catch (Exception ex)
        {
            _log?.Error(Constants.LogCatView, "SnapshotStore: failed to save denylist", ex);
        }
    }
}
