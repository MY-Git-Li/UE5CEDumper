using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Pure Class Pivot engine: instance folding, identity vs field grouping,
/// per-group value projection, and the collision render ⟨N: v1,v2,+M⟩. No DB.
/// </summary>
public class PivotEngineTests
{
    private static string IntHex(int v) =>
        string.Concat(BitConverter.GetBytes(v).Select(b => b.ToString("X2")));

    private static PivotInputRow Row(long idx, string path, string addr,
        string prop, int val, string type = "IntProperty") => new()
    {
        ObjectIndex = idx, NormPath = path, ObjAddr = addr,
        PropName = prop, PropOffset = 0x10, DeclaredType = type, Hex = IntHex(val),
    };

    [Fact]
    public void FieldMode_GroupsByKey_AndCollidesValues()
    {
        // Three inventory items: two are ItemID=1 (Qty 10 + 20), one is ItemID=2.
        var rows = new[]
        {
            Row(1, "/G.M:L.Item_0", "0x1", "ItemID", 1), Row(1, "/G.M:L.Item_0", "0x1", "Quantity", 10),
            Row(2, "/G.M:L.Item_1", "0x2", "ItemID", 1), Row(2, "/G.M:L.Item_1", "0x2", "Quantity", 20),
            Row(3, "/G.M:L.Item_2", "0x3", "ItemID", 2), Row(3, "/G.M:L.Item_2", "0x3", "Quantity", 5),
        };
        var q = new PivotQuery
        {
            ClassName = "BP_Item_C", KeyMode = PivotKeyMode.Field, KeyField = "ItemID",
            ValueFields = new() { "Quantity" },
        };
        var res = PivotEngine.Build(rows, q);

        Assert.Equal(2, res.GroupCount);
        Assert.Equal(3, res.InstanceCount);
        // Most-populous group first: ItemID=1 with two members.
        var g1 = res.Rows[0];
        Assert.Equal("1", g1.KeyValue);
        Assert.Equal(2, g1.Count);
        Assert.Equal("Quantity=⟨2: 10,20⟩", g1.ValuesDisplay);
        Assert.Equal("0x1", g1.ObjAddr);   // representative = first instance

        var g2 = res.Rows[1];
        Assert.Equal("2", g2.KeyValue);
        Assert.Equal(1, g2.Count);
        Assert.Equal("Quantity=5", g2.ValuesDisplay);
    }

    [Fact]
    public void IdentityMode_CollapsesNormalisedSiblings()
    {
        // Three enemies share a normalised path (spawn counter stripped). Identity
        // mode groups them; HP spread renders as a collision.
        const string p = "/G.M:L.BP_Enemy_C";
        var rows = new[]
        {
            Row(1, p, "0x1", "HP", 100), Row(2, p, "0x2", "HP", 80), Row(3, p, "0x3", "HP", 100),
        };
        var q = new PivotQuery
        {
            ClassName = "BP_Enemy_C", KeyMode = PivotKeyMode.Identity,
            ValueFields = new() { "HP" },
        };
        var res = PivotEngine.Build(rows, q);

        var g = Assert.Single(res.Rows);
        Assert.Equal(p, g.KeyValue);
        Assert.Equal(3, g.Count);
        Assert.Equal("HP=⟨3: 100,80⟩", g.ValuesDisplay);   // distinct {100,80}, N=3
    }

    [Fact]
    public void FieldMode_MissingKey_BucketsAsMissing()
    {
        var rows = new[]
        {
            Row(1, "/G.M:L.A", "0x1", "Quantity", 7),   // no ItemID on this instance
        };
        var q = new PivotQuery
        {
            ClassName = "C", KeyMode = PivotKeyMode.Field, KeyField = "ItemID",
            ValueFields = new() { "Quantity" },
        };
        var res = PivotEngine.Build(rows, q);
        Assert.Equal("(missing)", Assert.Single(res.Rows).KeyValue);
    }

    [Fact]
    public void MaxGroups_Truncates()
    {
        var rows = new System.Collections.Generic.List<PivotInputRow>();
        for (int i = 0; i < 10; i++)
            rows.Add(Row(i, $"/G.M:L.O_{i}", $"0x{i:X}", "Id", i));
        var q = new PivotQuery
        {
            ClassName = "C", KeyMode = PivotKeyMode.Field, KeyField = "Id",
            ValueFields = new(), MaxGroups = 4,
        };
        var res = PivotEngine.Build(rows, q);
        Assert.True(res.Truncated);
        Assert.Equal(4, res.Rows.Count);
        Assert.Equal(10, res.GroupCount);   // full count reported even when capped
    }

    [Theory]
    [InlineData(new[] { "5" }, "5")]
    [InlineData(new[] { "5", "5" }, "5")]                    // all agree -> just the value
    [InlineData(new[] { "10", "20" }, "⟨2: 10,20⟩")]
    [InlineData(new[] { "1", "2", "3", "4", "5" }, "⟨5: 1,2,3,+2⟩")]
    public void RenderCell_Formats(string[] values, string expected)
    {
        Assert.Equal(expected, PivotEngine.RenderCell(values));
    }

    [Fact]
    public void RenderCell_Empty_IsDash()
    {
        Assert.Equal("—", PivotEngine.RenderCell(System.Array.Empty<string>()));
    }
}
