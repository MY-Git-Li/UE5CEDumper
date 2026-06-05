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
        Assert.Equal(4, await ScalarAsync(conn, "PRAGMA user_version", ct));
        // v4 dropped the heavy composite indexes — only the lean ix_fields remains.
        Assert.Equal(0, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_strict','ix_loose','ix_insession')", ct));
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    [Fact]
    public async Task Diff_InMemoryHashJoin_ChangedAddedRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("DIFF");

        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[]
        {
            MakeObject(1, ("HP",   "IntProperty", "64000000")),   // 100
            MakeObject(2, ("Mana", "IntProperty", "0A000000")),   // 10
            MakeObject(3, ("XP",   "IntProperty", "05000000")),   // 5  (removed in B)
        }, ct);
        await _store.FinalizeSnapshotAsync(a, 3, 3, ct);

        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[]
        {
            MakeObject(1, ("HP",   "IntProperty", "5A000000")),   // 90   (changed, down)
            MakeObject(2, ("Mana", "IntProperty", "0A000000")),   // 10   (unchanged)
            MakeObject(4, ("Gold", "IntProperty", "32000000")),   // 50   (added)
        }, ct);
        await _store.FinalizeSnapshotAsync(b, 3, 3, ct);

        var diff = await _store.DiffSnapshotsAsync(a, b, new SnapshotDiffFilter(), ct);

        var changed = Assert.Single(diff.Changed);
        Assert.Equal("HP", changed.PropName);
        Assert.Equal("100", changed.OldValue);
        Assert.Equal("90", changed.NewValue);
        Assert.Equal(SnapshotDiffDirection.Down, changed.Direction);
        Assert.Equal(1, diff.AddedCount);     // Gold (obj 4) is B-only
        Assert.Equal(1, diff.RemovedCount);   // XP (obj 3) is A-only
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

    // ---------- N1: per-game class denylist (noise picker) ----------

    private static SnapshotCapturedObject MakeObjectAs(string className, int index,
        params (string name, string type, string hex)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = index, Addr = $"0x{index:X}", Name = $"{className}_{index}",
            ClassName = className, OuterClassName = "World",
            Path = $"/Game/Map.Map:PersistentLevel.{className}_{index}",
        };
        foreach (var (name, type, hex) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = name, Type = type, Hex = hex, Offset = 0 });
        return o;
    }

    // Two snapshots covering two classes: BP_Player_C (gameplay-relevant) and
    // W_HUD_C (noise). Both classes have changing values between A and B so the
    // denylist + Top-N picker have something to act on.
    private async Task<(long a, long b)> SeedTwoClassPairAsync(CancellationToken ct)
    {
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[]
        {
            MakeObjectAs("BP_Player_C", 1, ("Health", "IntProperty", "64000000")), // 100
            MakeObjectAs("W_HUD_C",     2, ("Alpha",  "FloatProperty", "00000000")),// 0.0
            MakeObjectAs("W_HUD_C",     3, ("Width",  "FloatProperty", "00007044")),// 960
        }, ct);
        await _store.FinalizeSnapshotAsync(a, 3, 3, ct);

        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[]
        {
            MakeObjectAs("BP_Player_C", 1, ("Health", "IntProperty", "5A000000")), // 90 (down)
            MakeObjectAs("W_HUD_C",     2, ("Alpha",  "FloatProperty", "0000803F")),// 1.0 (up)
            MakeObjectAs("W_HUD_C",     3, ("Width",  "FloatProperty", "0000F043")),// 480 (down)
        }, ct);
        await _store.FinalizeSnapshotAsync(b, 3, 3, ct);
        return (a, b);
    }

    [Fact]
    public async Task DiffSnapshots_ExcludedClasses_FiltersOutNoiseClass()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);

        var deny = new HashSet<string>(StringComparer.Ordinal) { "W_HUD_C" };
        var diff = await _store.DiffSnapshotsAsync(a, b,
            new SnapshotDiffFilter { ExcludedClasses = deny }, ct);

        // Only the gameplay class row survives. The two W_HUD_C rows are gone.
        var row = Assert.Single(diff.Changed);
        Assert.Equal("BP_Player_C", row.ClassName);
        Assert.Equal("Health", row.PropName);
    }

    [Fact]
    public async Task DiffSnapshots_TopContributors_RanksByHitCount_ExcludesDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);

        // No denylist yet: both classes should appear, W_HUD_C first (2 hits vs 1).
        var diff = await _store.DiffSnapshotsAsync(a, b, new SnapshotDiffFilter(), ct);
        Assert.Equal(2, diff.TopContributors.Count);
        Assert.Equal("W_HUD_C",     diff.TopContributors[0].ClassName);
        Assert.Equal(2,             diff.TopContributors[0].HitCount);
        Assert.Equal("BP_Player_C", diff.TopContributors[1].ClassName);
        Assert.Equal(1,             diff.TopContributors[1].HitCount);

        // Denylist suppresses W_HUD_C from BOTH the rows AND the picker (the user
        // can't re-suggest a class they already hid).
        var deny = new HashSet<string>(StringComparer.Ordinal) { "W_HUD_C" };
        var diff2 = await _store.DiffSnapshotsAsync(a, b,
            new SnapshotDiffFilter { ExcludedClasses = deny }, ct);
        var only = Assert.Single(diff2.TopContributors);
        Assert.Equal("BP_Player_C", only.ClassName);
    }

    [Fact]
    public async Task Diff_AlreadyCancelledToken_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.DiffSnapshotsAsync(a, b, new SnapshotDiffFilter(), cts.Token));
    }

    [Fact]
    public async Task SpcQuery_AlreadyCancelledToken_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var query = new SpcQuery
        {
            SnapshotIds = { a, b },
            Predicates  = { SpcPredicateKind.Any, SpcPredicateKind.Changed },
            AbsolutePredicates = { new SpcAbsolutePredicate(), new SpcAbsolutePredicate() },
            JoinMode = SpcJoinMode.InSession,
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.SpcQueryAsync(query, cts.Token));
    }

    [Fact]
    public async Task SpcQuery_ExcludedClasses_FiltersOutNoiseClass()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);

        var deny = new HashSet<string>(StringComparer.Ordinal) { "W_HUD_C" };
        var query = new SpcQuery
        {
            SnapshotIds = { a, b },
            Predicates  = { SpcPredicateKind.Any, SpcPredicateKind.Changed },
            AbsolutePredicates = { new SpcAbsolutePredicate(), new SpcAbsolutePredicate() },
            JoinMode = SpcJoinMode.InSession,
            ExcludedClasses = deny,
        };
        var res = await _store.SpcQueryAsync(query, ct);

        // Only the gameplay class survives the chain — W_HUD_C never enters the
        // candidate dict on the anchor pass.
        var row = Assert.Single(res.Rows);
        Assert.Equal("BP_Player_C", row.ClassName);
        Assert.Equal("Health",      row.PropName);

        // Top-N likewise excludes the denied class.
        var only = Assert.Single(res.TopContributors);
        Assert.Equal("BP_Player_C", only.ClassName);
    }

    [Fact]
    public void ClassDenylist_PersistsAcrossStoreReload_PerGame()
    {
        // Game A: deny two classes (Spc scope).
        _store.SetActiveGame("GAMEAA");
        var picksA = new HashSet<string>(StringComparer.Ordinal) { "W_HUD_C", "BP_Tick_C" };
        _store.SetClassDenylist(DenylistScope.Spc, picksA);

        // Game B: independently deny a different class.
        _store.SetActiveGame("GAMEBB");
        var picksB = new HashSet<string>(StringComparer.Ordinal) { "WBP_Inventory_C" };
        _store.SetClassDenylist(DenylistScope.Spc, picksB);

        // Fresh store instance over the same temp dir — proves the JSON survived.
        var store2 = new SnapshotStore(new MockPlatformService(_tempDir));
        store2.SetActiveGame("GAMEAA");
        Assert.Equal(picksA, store2.GetClassDenylist(DenylistScope.Spc));
        store2.SetActiveGame("GAMEBB");
        Assert.Equal(picksB, store2.GetClassDenylist(DenylistScope.Spc));
        // Unknown game: empty (not the wrong game's list).
        store2.SetActiveGame("UNKNOWN");
        Assert.Empty(store2.GetClassDenylist(DenylistScope.Spc));
    }

    [Fact]
    public void ClassDenylist_ScopesAreIndependent_AndCoexistInOneFile()
    {
        _store.SetActiveGame("SCOPED");
        var diff  = new HashSet<string>(StringComparer.Ordinal) { "DiffOnly_C" };
        var spc   = new HashSet<string>(StringComparer.Ordinal) { "SpcOnly_C", "Shared_C" };
        var pivot = new HashSet<string>(StringComparer.Ordinal) { "PivotOnly_C" };
        // Writing one scope must not clobber the others (read-modify-write).
        _store.SetClassDenylist(DenylistScope.Diff, diff);
        _store.SetClassDenylist(DenylistScope.Spc, spc);
        _store.SetClassDenylist(DenylistScope.Pivot, pivot);

        var store2 = new SnapshotStore(new MockPlatformService(_tempDir));
        store2.SetActiveGame("SCOPED");
        Assert.Equal(diff,  store2.GetClassDenylist(DenylistScope.Diff));
        Assert.Equal(spc,   store2.GetClassDenylist(DenylistScope.Spc));
        Assert.Equal(pivot, store2.GetClassDenylist(DenylistScope.Pivot));
        // A class in Spc is NOT reported under Diff/Pivot (independence).
        Assert.DoesNotContain("Shared_C", store2.GetClassDenylist(DenylistScope.Diff));
        Assert.DoesNotContain("Shared_C", store2.GetClassDenylist(DenylistScope.Pivot));
    }

    [Fact]
    public void ClassDenylist_WithoutActiveGame_DoesNotPersist()
    {
        // No SetActiveGame call — default DB; the store should refuse to save so a
        // noise pick from one game doesn't leak into the default bucket.
        _store.SetClassDenylist(DenylistScope.Spc, new HashSet<string>(StringComparer.Ordinal) { "X" });

        var store2 = new SnapshotStore(new MockPlatformService(_tempDir));
        Assert.Empty(store2.GetClassDenylist(DenylistScope.Spc));
    }

    [Fact]
    public void ClassDenylist_FilenameSanitisedForPeHash()
    {
        _store.SetActiveGame("../../escape");
        _store.SetClassDenylist(DenylistScope.Spc, new HashSet<string>(StringComparer.Ordinal) { "A" });

        // Only ASCII alphanumerics survive in the filename.
        var expected = Path.Combine(_tempDir, "UE5CEDumper", "snapshots.escape.denylist.json");
        Assert.True(File.Exists(expected));
    }

    // ---------- Pivot class-index + Delete All ----------

    [Fact]
    public async Task PivotClassIndex_ComputesInstanceCounts_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "p" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            MakeObjectAs("BP_Player_C", 1, ("Health", "IntProperty", "64000000")),
            MakeObjectAs("BP_Player_C", 2, ("Health", "IntProperty", "32000000")),
            MakeObjectAs("W_HUD_C",     3, ("Alpha",  "FloatProperty", "0000803F")),
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 3, 3, ct);   // precomputes the index

        var classes = await _store.ListPivotClassesAsync(id, ct);
        Assert.Equal(2, Assert.Single(classes, c => c.ClassName == "BP_Player_C").InstanceCount);
        Assert.Equal(1, Assert.Single(classes, c => c.ClassName == "W_HUD_C").InstanceCount);

        // Idempotent: a second call serves the same built index (no rebuild).
        var again = await _store.ListPivotClassesAsync(id, ct);
        Assert.Equal(classes.Count, again.Count);
    }

    [Fact]
    public async Task PivotClassIndex_LazyBuild_WhenNotFinalizedWithIndex()
    {
        // Simulate an old snapshot: write rows but the index is built lazily on
        // first ListPivotClasses (FinalizeSnapshot also builds it, but the lazy
        // path must work for pre-feature snapshots / cancelled finalizes).
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "lazy" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            MakeObjectAs("BP_Item_C", 1, ("Qty", "IntProperty", "05000000")),
            MakeObjectAs("BP_Item_C", 2, ("Qty", "IntProperty", "07000000")),
        }, ct);
        // NOTE: no FinalizeSnapshotAsync — the index doesn't exist yet.
        var classes = await _store.ListPivotClassesAsync(id, ct);   // builds lazily
        Assert.Equal(2, Assert.Single(classes, c => c.ClassName == "BP_Item_C").InstanceCount);
    }

    [Fact]
    public async Task DeleteAll_TruncatesEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await SeedTwoClassPairAsync(ct);
        Assert.Equal(2, (await _store.ListSnapshotsAsync(ct)).Count);

        await _store.DeleteAllSnapshotsAsync(ct);

        Assert.Empty(await _store.ListSnapshotsAsync(ct));
        // The pivot index for the deleted snapshots is gone too (no rows linger).
        Assert.Empty(await _store.ListPivotClassesAsync(a, ct));
        Assert.Empty(await _store.ListPivotClassesAsync(b, ct));
    }
}
