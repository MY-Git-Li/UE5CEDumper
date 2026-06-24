using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotSizeEstimateTests
{
    private static SnapshotCapturedObject Obj(string cls, string path, params (string n, string t, string h)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            ClassName = cls, Path = path, OuterClassName = "World", Addr = "0x7FF600001234",
        };
        foreach (var (n, t, h) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = n, Type = t, Hex = h });
        return o;
    }

    [Fact]
    public void ScalarRowBytes_CountsAllTextColumns_PlusOverhead()
    {
        var o = Obj("BP_Thing_C", "/Game/Map.Map:PersistentLevel.Obj_0");
        var f = new SnapshotCapturedField { Name = "Health", Type = "FloatProperty", Hex = "0000803F" };
        long bytes = SnapshotSizeEstimate.ScalarRowBytes(o, f);

        // Lower bound: at least the sum of all text bytes that get stored. The model
        // also adds fixed overhead + the duplicated class_fqn in the ix_fields index.
        long textOnly = o.ClassName.Length + o.Path.Length + o.OuterClassName.Length
                      + f.Name.Length + f.Type.Length + o.Addr.Length + f.Hex.Length;
        Assert.True(bytes > textOnly, $"row {bytes} should exceed raw text {textOnly} (overhead + index copy)");
    }

    [Fact]
    public void EstimateChunkBytes_SumsScalarAndArrayRows()
    {
        // One object with a scalar field + a one-element struct-array with one inner field.
        var o = Obj("BP_Inv_C", "/Game/M.M:L.Inv", ("Gold", "IntProperty", "64000000"));
        var arr = new SnapshotCapturedArray { Field = "Cargo" };
        var el = new SnapshotCapturedArrayElement { Index = 0, KeyName = "ItemID", KeyValue = "Fuel" };
        el.Fields.Add(new SnapshotCapturedField { Name = "Quantity", Type = "IntProperty", Hex = "0A000000" });
        arr.Elements.Add(el);
        o.Arrays.Add(arr);

        long total = SnapshotSizeEstimate.EstimateChunkBytes(new[] { o });
        long scalarOnly = SnapshotSizeEstimate.ScalarRowBytes(o, o.Fields[0]);

        // The array element row adds to the scalar row (array_field + inner_key columns).
        Assert.True(total > scalarOnly, "chunk total must include the array-element row");
    }

    [Fact]
    public void EstimateChunkBytes_ScalesLinearlyWithFieldCount()
    {
        var one = Obj("C", "/Game/M.M:L.A", ("F0", "IntProperty", "01000000"));
        var two = Obj("C", "/Game/M.M:L.A", ("F0", "IntProperty", "01000000"),
                                            ("F1", "IntProperty", "02000000"));
        long b1 = SnapshotSizeEstimate.EstimateChunkBytes(new[] { one });
        long b2 = SnapshotSizeEstimate.EstimateChunkBytes(new[] { two });
        // Two same-shape fields cost more than one, and less than double (shared class/path
        // bytes are per-row, so it IS ~2x here — assert strictly greater + under 2.2x).
        Assert.True(b2 > b1);
        Assert.True(b2 <= b1 * 22 / 10, $"{b2} should be ~2x {b1}, not more");
    }

    [Fact]
    public void Extrapolate_ScalesSampleToTotal()
    {
        // 1000 bytes over 100 sampled objects, total 1000 objects -> ~10,000 bytes.
        Assert.Equal(10_000, SnapshotSizeEstimate.Extrapolate(1000, 100, 1000));
    }

    [Theory]
    [InlineData(0, 100)]   // nothing sampled
    [InlineData(100, 0)]   // no total
    public void Extrapolate_GuardsAgainstZero(long sampledObjects, long totalObjects)
    {
        Assert.Equal(0, SnapshotSizeEstimate.Extrapolate(5000, sampledObjects, totalObjects));
    }
}
