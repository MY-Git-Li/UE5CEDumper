using System.IO;
using Microsoft.Data.Sqlite;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpSnapTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* WAL files may linger briefly; best effort */ }
    }

    private static SnapshotCapturedObject MakeObject(int index, params (string name, string type, string hex)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = index,
            Addr = $"0x{index:X}",
            Name = $"BP_Player_C_{index}",
            ClassName = "BP_Player_C",
            OuterClassName = "World",
            Path = $"/Game/Map.Map:PersistentLevel.BP_Player_C_{index}",
        };
        foreach (var (name, type, hex) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = name, Type = type, Hex = hex, Offset = 0 });
        return o;
    }

    [Fact]
    public async Task CreateWriteListDelete_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var meta = new SnapshotMeta
        {
            Label = "first", CapturedAt = "2026-06-02T00:00:00Z",
            PeHash = "ABCD", GameSessionId = "ABCD-1", UeVersion = 504, Scope = "NumericNoByte",
        };
        long id = await _store.CreateSnapshotAsync(meta, ct);
        Assert.True(id > 0);

        int rows = await _store.WriteChunkAsync(id, new[]
        {
            MakeObject(1, ("Health", "FloatProperty", "0000803F"), ("Ammo", "IntProperty", "1E000000")),
            MakeObject(2, ("Health", "FloatProperty", "0000003F")),
        }, ct);
        Assert.Equal(3, rows);

        await _store.FinalizeSnapshotAsync(id, objectCount: 2, fieldCount: rows, ct);

        var list = await _store.ListSnapshotsAsync(ct);
        var saved = Assert.Single(list);
        Assert.Equal(id, saved.Id);
        Assert.Equal("first", saved.Label);
        Assert.Equal("ABCD", saved.PeHash);
        Assert.Equal(504, saved.UeVersion);
        Assert.Equal(2, saved.ObjectCount);
        Assert.Equal(3, saved.FieldCount);

        await _store.DeleteSnapshotAsync(id, ct);
        Assert.Empty(await _store.ListSnapshotsAsync(ct));
    }

    [Fact]
    public async Task Schema_DenormalizedFields_StoresIdentityPerRow()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("DENORM");
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "n" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            MakeObject(1, ("A", "IntProperty", "01000000"),
                          ("B", "IntProperty", "02000000"),
                          ("C", "IntProperty", "03000000")),
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 1, 3, ct);

        await using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath }.ToString());
        await conn.OpenAsync(ct);

        // Denormalised: identity columns live on `fields` (the fast single-table
        // covering-index layout). No `objects` table, no `vfields` view.
        Assert.Equal(3, await ScalarAsync(conn, "SELECT COUNT(*) FROM fields", ct));
        Assert.Equal(1, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM pragma_table_info('fields') WHERE name='norm_path'", ct));
        Assert.Equal(0, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE name='objects'", ct));
        Assert.Equal(3, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM fields WHERE class_fqn='BP_Player_C' " +
            "AND norm_path='/Game/Map.Map:PersistentLevel.BP_Player_C'", ct));
        Assert.Equal(3, await ScalarAsync(conn, "PRAGMA user_version", ct));
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    private async Task<long> SeedSnapshotAsync(string label, int idx, CancellationToken ct)
    {
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = label }, ct);
        await _store.WriteChunkAsync(id, new[] { MakeObject(idx, ("X", "IntProperty", "01000000")) }, ct);
        await _store.FinalizeSnapshotAsync(id, 1, 1, ct);
        return id;
    }

    [Fact]
    public async Task EnforceQuota_DropsOldestKeepsNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        await SeedSnapshotAsync("b", 2, ct);
        await SeedSnapshotAsync("c", 3, ct);

        // A 1-byte quota evicts everything but the newest (always kept).
        int dropped = await _store.EnforceQuotaAsync(1, ct);
        Assert.Equal(2, dropped);
        Assert.Equal("c", Assert.Single(await _store.ListSnapshotsAsync(ct)).Label);
    }

    [Fact]
    public async Task EnforceQuota_UnlimitedIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        Assert.Equal(0, await _store.EnforceQuotaAsync(0, ct));   // 0 = unlimited
        Assert.Single(await _store.ListSnapshotsAsync(ct));
    }

    [Fact]
    public async Task GetUsage_ReportsSizeAndCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSnapshotAsync("a", 1, ct);
        var u = await _store.GetUsageAsync(ct);
        Assert.Equal(1, u.SnapshotCount);
        Assert.True(u.GameDbBytes > 0);
        Assert.True(u.AllGamesBytes >= u.GameDbBytes);

        // List populates a positive size estimate for snapshots with fields.
        Assert.True(Assert.Single(await _store.ListSnapshotsAsync(ct)).EstBytes > 0);
    }

    private static SnapshotCapturedArrayElement MakeElem(
        int idx, string keyName, string keyVal, params (string n, string t, string h)[] fields)
    {
        var el = new SnapshotCapturedArrayElement { Index = idx, KeyName = keyName, KeyValue = keyVal };
        foreach (var (n, t, h) in fields)
            el.Fields.Add(new SnapshotCapturedField { Name = n, Type = t, Hex = h });
        return el;
    }

    private static SnapshotCapturedObject ShipWithCargo(string hpHex, string fuelQtyHex)
    {
        var o = new SnapshotCapturedObject
        {
            Index = 1, Addr = "0x1", Name = "Ship_0", ClassName = "BP_Ship_C",
            OuterClassName = "World", Path = "/Game/M.M:L.Ship_0",
        };
        o.Fields.Add(new SnapshotCapturedField { Name = "HP", Type = "IntProperty", Hex = hpHex });
        var cargo = new SnapshotCapturedArray { Field = "Cargo" };
        cargo.Elements.Add(MakeElem(0, "ItemID", "Fuel", ("Quantity", "IntProperty", fuelQtyHex)));
        cargo.Elements.Add(MakeElem(1, "ItemID", "Ore",  ("Quantity", "IntProperty", "0A000000")));
        o.Arrays.Add(cargo);
        return o;
    }

    [Fact]
    public async Task WriteChunk_WritesArrayRows_ExcludedFromScalarDiff()
    {
        var ct = TestContext.Current.CancellationToken;

        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        int rowsA = await _store.WriteChunkAsync(a, new[] { ShipWithCargo("64000000", "64000000") }, ct); // HP100, Fuel100
        Assert.Equal(3, rowsA);  // 1 scalar HP + 2 array-element Quantity rows
        await _store.FinalizeSnapshotAsync(a, 1, rowsA, ct);

        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[] { ShipWithCargo("5A000000", "50000000") }, ct);  // HP90, Fuel80
        await _store.FinalizeSnapshotAsync(b, 1, 3, ct);

        // The scalar diff sees only HP — array-element Quantity changes are
        // excluded (they'd join ambiguously on prop_name). Array diffing is Pivot.
        var diff = await _store.DiffSnapshotsAsync(a, b, new SnapshotDiffFilter(), ct);
        Assert.Equal("HP", Assert.Single(diff.Changed).PropName);
    }

    [Fact]
    public async Task WriteChunk_EmptyIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "e" }, ct);
        Assert.Equal(0, await _store.WriteChunkAsync(id, Array.Empty<SnapshotCapturedObject>(), ct));
    }

    [Fact]
    public async Task PerGameDb_IsolatesSnapshotsByPeHash()
    {
        var ct = TestContext.Current.CancellationToken;

        _store.SetActiveGame("GAMEA");
        var pathA = _store.DatabasePath;
        await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a", PeHash = "GAMEA" }, ct);

        _store.SetActiveGame("GAMEB");
        var pathB = _store.DatabasePath;
        await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b", PeHash = "GAMEB" }, ct);

        Assert.NotEqual(pathA, pathB);
        Assert.Contains("GAMEA", pathA);
        Assert.Contains("GAMEB", pathB);

        // Each game sees only its own snapshot.
        _store.SetActiveGame("GAMEA");
        var listA = await _store.ListSnapshotsAsync(ct);
        Assert.Equal("a", Assert.Single(listA).Label);

        _store.SetActiveGame("GAMEB");
        var listB = await _store.ListSnapshotsAsync(ct);
        Assert.Equal("b", Assert.Single(listB).Label);
    }

    [Fact]
    public void SetActiveGame_SanitizesPeHashIntoFilename()
    {
        _store.SetActiveGame("../../evil\\x00");
        // Only ASCII alphanumerics survive (drops . / \) -> no path traversal.
        // Raw chars kept: e v i l x 0 0  ->  "evilx00".
        Assert.DoesNotContain("..", _store.DatabasePath);
        Assert.EndsWith("snapshots.evilx00.db", _store.DatabasePath);
    }

    // Two snapshots sharing objects 1 & 2; obj 3 only in A, obj 4 only in B.
    // Health 100->90 (down), Mana 5->8 (up), Ammo 30->30 (unchanged).
    private async Task<(long a, long b)> SeedDiffPairAsync(CancellationToken ct)
    {
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[]
        {
            MakeObject(1, ("Health", "FloatProperty", "0000C842")),                                  // 100.0
            MakeObject(2, ("Ammo", "IntProperty", "1E000000"), ("Mana", "IntProperty", "05000000")), // 30, 5
            MakeObject(3, ("Gold", "IntProperty", "E7030000")),                                       // 999 (A only)
        }, ct);
        await _store.FinalizeSnapshotAsync(a, 3, 4, ct);

        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[]
        {
            MakeObject(1, ("Health", "FloatProperty", "0000B442")),                                  // 90.0 (down)
            MakeObject(2, ("Ammo", "IntProperty", "1E000000"), ("Mana", "IntProperty", "08000000")), // 30 same, 8 up
            MakeObject(4, ("Score", "IntProperty", "01000000")),                                     // B only
        }, ct);
        await _store.FinalizeSnapshotAsync(b, 3, 4, ct);
        return (a, b);
    }

    [Fact]
    public async Task DiffSnapshots_FindsChangedValues_CountsChurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedDiffPairAsync(ct);

        var diff = await _store.DiffSnapshotsAsync(a, b, new SnapshotDiffFilter(), ct);

        Assert.Equal(2, diff.Changed.Count);  // Health + Mana (Ammo unchanged)

        var health = Assert.Single(diff.Changed, r => r.PropName == "Health");
        Assert.Equal("100", health.OldValue);
        Assert.Equal("90", health.NewValue);
        Assert.Equal(SnapshotDiffDirection.Down, health.Direction);
        Assert.Equal("BP_Player_C", health.ClassName);

        var mana = Assert.Single(diff.Changed, r => r.PropName == "Mana");
        Assert.Equal("5", mana.OldValue);
        Assert.Equal("8", mana.NewValue);
        Assert.Equal(SnapshotDiffDirection.Up, mana.Direction);

        Assert.Equal(1, diff.RemovedCount);   // Gold (A only)
        Assert.Equal(1, diff.AddedCount);     // Score (B only)
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task DiffSnapshots_FiltersByDirection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedDiffPairAsync(ct);

        var up = await _store.DiffSnapshotsAsync(a, b,
            new SnapshotDiffFilter { Direction = SnapshotDiffDirection.Up }, ct);
        Assert.Equal("Mana", Assert.Single(up.Changed).PropName);

        var down = await _store.DiffSnapshotsAsync(a, b,
            new SnapshotDiffFilter { Direction = SnapshotDiffDirection.Down }, ct);
        Assert.Equal("Health", Assert.Single(down.Changed).PropName);
    }

    [Fact]
    public async Task DiffSnapshots_FiltersByPropName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedDiffPairAsync(ct);

        var diff = await _store.DiffSnapshotsAsync(a, b,
            new SnapshotDiffFilter { PropContains = "heal", IncludeAddedRemoved = false }, ct);
        Assert.Equal("Health", Assert.Single(diff.Changed).PropName);
        Assert.Equal(0, diff.AddedCount);   // skipped
    }

    [Fact]
    public async Task MultipleSnapshots_ListedNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        var list = await _store.ListSnapshotsAsync(ct);
        Assert.Equal(2, list.Count);
        Assert.Equal(b, list[0].Id);  // newest (higher id) first
        Assert.Equal(a, list[1].Id);
    }
}
