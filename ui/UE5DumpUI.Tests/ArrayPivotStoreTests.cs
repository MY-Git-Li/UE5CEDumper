using System.IO;
using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Phase C6 — array-element pivot against a real temp-file SQLite store. Captures a
/// cargo-style struct array (FCargoSlot{ ItemID, Quantity }) on two owners and pivots
/// it by inner-key value (ItemID), proving the reorder-/owner-immune inner join.
/// </summary>
public class ArrayPivotStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public ArrayPivotStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpArrPivot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
        _store.SetActiveGame("ARRPIVOT");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static string IntHex(int v) =>
        string.Concat(BitConverter.GetBytes(v).Select(b => b.ToString("X2")));

    // One owner with a Cargo array of (ItemID inner key, Quantity inner numeric).
    private static SnapshotCapturedObject Cargo(int idx, params (string item, int qty)[] slots)
    {
        var o = new SnapshotCapturedObject
        {
            Index = idx, Addr = $"0x{0x2000 + idx:X}", Name = $"PS_{idx}",
            ClassName = "PlayerState", OuterClassName = "World", Path = $"/G.M:L.PlayerState_{idx}",
        };
        var arr = new SnapshotCapturedArray { Field = "Cargo" };
        int ei = 0;
        foreach (var (item, qty) in slots)
        {
            var el = new SnapshotCapturedArrayElement { Index = ei++, KeyName = "ItemID", KeyValue = item };
            el.Fields.Add(new SnapshotCapturedField { Name = "Quantity", Type = "IntProperty", Hex = IntHex(qty), Offset = 0x8 });
            arr.Elements.Add(el);
        }
        o.Arrays.Add(arr);
        return o;
    }

    private async Task<long> SeedCargoAsync(CancellationToken ct)
    {
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "cargo" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            Cargo(1, ("Fuel", 100), ("Ore", 50)),
            Cargo(2, ("Fuel", 80)),
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 2, 3, ct);
        return id;
    }

    [Fact]
    public async Task ListArrayClasses_OnlyClassesWithCapturedArrays()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedCargoAsync(ct);

        var classes = await _store.ListPivotArrayClassesAsync(id, ct);
        var ps = Assert.Single(classes);
        Assert.Equal("PlayerState", ps.ClassName);
        Assert.Equal(2, ps.InstanceCount);   // two owners
    }

    [Fact]
    public async Task ListArrayFields_ReportsInnerKeyAndElementRows()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedCargoAsync(ct);

        var fields = await _store.ListPivotArrayFieldsAsync(id, "PlayerState", ct);
        var cargo = Assert.Single(fields);
        Assert.Equal("Cargo", cargo.ArrayField);
        Assert.Equal("ItemID", cargo.InnerKeyName);
        Assert.Equal(3, cargo.ElementCount);   // 3 element rows (one Quantity each)
    }

    [Fact]
    public async Task ListArrayProps_ReportsInnerNumericProps()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedCargoAsync(ct);

        var props = await _store.ListPivotArrayPropsAsync(id, "PlayerState", "Cargo", ct);
        var qty = Assert.Single(props);
        Assert.Equal("Quantity", qty.Name);
        Assert.Equal(3, qty.DistinctCount);    // {100, 50, 80}
    }

    [Fact]
    public async Task PivotArray_GroupsByInnerKeyValue_AcrossOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedCargoAsync(ct);

        var res = await _store.PivotArrayAsync(new ArrayPivotQuery
        {
            SnapshotId = id, ClassName = "PlayerState", ArrayField = "Cargo",
            ValueProps = new() { "Quantity" },
        }, ct);

        Assert.Equal(2, res.GroupCount);       // Fuel, Ore
        Assert.Equal(3, res.InstanceCount);    // 3 elements total
        // Fuel spans both owners (qty 100 + 80) → most populous, collision-rendered.
        var fuel = res.Rows[0];
        Assert.Equal("Fuel", fuel.KeyValue);
        Assert.Equal(2, fuel.Count);
        Assert.Equal("Quantity=⟨2: 100,80⟩", fuel.ValuesDisplay);
        // Ore is a single element.
        var ore = res.Rows[1];
        Assert.Equal("Ore", ore.KeyValue);
        Assert.Equal(1, ore.Count);
        Assert.Equal("Quantity=50", ore.ValuesDisplay);
    }

    [Fact]
    public async Task PivotArray_ScalarPivotIgnoresArrayRows()
    {
        // The scalar class pivot must not see array-element rows (array_field IS NULL
        // guard) — PlayerState has only array data, so it lists no scalar fields.
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedCargoAsync(ct);

        var scalarFields = await _store.ListPivotFieldsAsync(id, "PlayerState", ct);
        Assert.Empty(scalarFields);
        var scalarClasses = await _store.ListPivotClassesAsync(id, ct);
        Assert.Empty(scalarClasses);  // no scalar (non-array) rows captured
    }
}
