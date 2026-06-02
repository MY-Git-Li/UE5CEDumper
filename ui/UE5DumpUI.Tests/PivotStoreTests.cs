using System.IO;
using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// End-to-end Class Pivot against a real temp-file SQLite store: class/field
/// listing with cardinality, plus field- and identity-mode pivots.
/// </summary>
public class PivotStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public PivotStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpPivotTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
        _store.SetActiveGame("PIVOT");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static string IntHex(int v) =>
        string.Concat(BitConverter.GetBytes(v).Select(b => b.ToString("X2")));

    private static SnapshotCapturedObject Obj(int idx, string cls, string path,
        params (string name, int val)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = idx, Addr = $"0x{0x1000 + idx:X}", Name = $"O_{idx}",
            ClassName = cls, OuterClassName = "World", Path = path,
        };
        foreach (var (name, val) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = name, Type = "IntProperty", Hex = IntHex(val), Offset = 0x10 });
        return o;
    }

    private async Task<long> SeedInventoryAsync(CancellationToken ct)
    {
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "inv" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            Obj(1, "BP_Item_C", "/G.M:L.Item_0", ("ItemID", 1), ("Quantity", 10)),
            Obj(2, "BP_Item_C", "/G.M:L.Item_1", ("ItemID", 1), ("Quantity", 20)),
            Obj(3, "BP_Item_C", "/G.M:L.Item_2", ("ItemID", 2), ("Quantity", 5)),
            Obj(4, "PlayerState", "/G.M:L.PlayerState_0", ("HP", 100)),
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 4, 7, ct);
        return id;
    }

    [Fact]
    public async Task ListClasses_CountsInstances_MostPopulousFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedInventoryAsync(ct);

        var classes = await _store.ListPivotClassesAsync(id, ct);
        Assert.Equal(2, classes.Count);
        Assert.Equal("BP_Item_C", classes[0].ClassName);
        Assert.Equal(3, classes[0].InstanceCount);
        Assert.Equal("PlayerState", classes[1].ClassName);
        Assert.Equal(1, classes[1].InstanceCount);
    }

    [Fact]
    public async Task ListFields_ReportsCardinality()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedInventoryAsync(ct);

        var fields = await _store.ListPivotFieldsAsync(id, "BP_Item_C", ct);
        var itemId = fields.Single(f => f.Name == "ItemID");
        Assert.Equal(2, itemId.DistinctCount);   // values {1,2}
        Assert.Equal(3, itemId.InstanceCount);
        var qty = fields.Single(f => f.Name == "Quantity");
        Assert.Equal(3, qty.DistinctCount);       // values {10,20,5}
    }

    [Fact]
    public async Task Pivot_FieldMode_GroupsByItemId()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedInventoryAsync(ct);

        var res = await _store.PivotAsync(new PivotQuery
        {
            SnapshotId = id, ClassName = "BP_Item_C",
            KeyMode = PivotKeyMode.Field, KeyField = "ItemID",
            ValueFields = new() { "Quantity" },
        }, ct);

        Assert.Equal(2, res.GroupCount);
        Assert.Equal(3, res.InstanceCount);
        var g1 = res.Rows[0];          // most populous
        Assert.Equal("1", g1.KeyValue);
        Assert.Equal(2, g1.Count);
        Assert.Equal("Quantity=⟨2: 10,20⟩", g1.ValuesDisplay);
    }

    [Fact]
    public async Task Pivot_IdentityMode_CollapsesSpawnSiblings()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedInventoryAsync(ct);

        var res = await _store.PivotAsync(new PivotQuery
        {
            SnapshotId = id, ClassName = "BP_Item_C",
            KeyMode = PivotKeyMode.Identity,
            ValueFields = new() { "Quantity" },
        }, ct);

        // The store normalises Item_0/1/2 -> "/G.M:L.Item", so identity mode
        // collapses the three spawn siblings into one group with a value spread.
        var g = Assert.Single(res.Rows);
        Assert.Equal("/G.M:L.Item", g.KeyValue);
        Assert.Equal(3, g.Count);
        Assert.Equal("Quantity=⟨3: 10,20,5⟩", g.ValuesDisplay);
    }
}
