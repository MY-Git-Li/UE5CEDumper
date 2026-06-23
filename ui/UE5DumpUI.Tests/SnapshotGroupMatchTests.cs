using System;
using System.IO;
using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// S2 — Snapshot Group Match over a real temp SQLite store (Mode A, single-snapshot
/// absolute). Verifies the store loads one snapshot's objects, runs the C# Orden
/// matcher per object, and emits GroupCandidates for objects holding ALL of N values
/// at distinct offsets. See docs/snapshot-group-match-spec.md.
/// </summary>
public class SnapshotGroupMatchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public SnapshotGroupMatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpGroupTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
        _store.SetActiveGame("GROUP");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* WAL files may linger briefly; best effort */ }
    }

    private static string IntHex(int v) => Convert.ToHexString(BitConverter.GetBytes(v)); // LE, 8 hex chars

    private static SnapshotCapturedObject Obj(int index, string cls,
        params (string name, string type, int offset, string hex)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = index,
            Addr = $"0x{index * 0x1000:X}",
            Name = $"{cls}_{index}",
            ClassName = cls,
            OuterClassName = "World",
            Path = $"/Game/Map.Map:PersistentLevel.{cls}_{index}",
        };
        foreach (var (name, type, offset, hex) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = name, Type = type, Offset = offset, Hex = hex });
        return o;
    }

    private static SnapshotGroupSlotInput Slot(ValueScanType st, string value, string value2 = "",
        ValueScanDataType dt = ValueScanDataType.NumericNoByte) =>
        new() { ScanType = st, Value = value, Value2 = value2, DataType = dt };

    private async System.Threading.Tasks.Task<long> SeedAsync(
        System.Threading.CancellationToken ct, params SnapshotCapturedObject[] objects)
    {
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "g", Scope = "NumericNoByte" }, ct);
        int rows = await _store.WriteChunkAsync(id, objects, ct);
        await _store.FinalizeSnapshotAsync(id, objects.Length, rows, ct);
        return id;
    }

    [Fact]
    public async Task ModeA_FindsObjectsHoldingBothValuesAtDistinctOffsets()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedAsync(ct,
            // matches {24, 10}: Str@0x20=24, Def@0x24=10
            Obj(1, "BP_Player_C",
                ("Str", "IntProperty", 0x20, IntHex(24)),
                ("Def", "IntProperty", 0x24, IntHex(10)),
                ("Dex", "IntProperty", 0x28, IntHex(14))),
            // does NOT match (no 10)
            Obj(2, "BP_Player_C",
                ("Str", "IntProperty", 0x20, IntHex(24)),
                ("Def", "IntProperty", 0x24, IntHex(99))),
            // matches {24, 10} on a different class/offsets
            Obj(3, "BP_Enemy_C",
                ("HP", "IntProperty", 0x10, IntHex(24)),
                ("MP", "IntProperty", 0x14, IntHex(10))));

        var q = new SnapshotGroupQuery
        {
            SnapshotIds = { id },
            Slots = { Slot(ValueScanType.Exact, "24"), Slot(ValueScanType.Exact, "10") },
        };
        var res = await _store.GroupMatchAsync(q, ct);

        Assert.Null(res.Error);
        Assert.Equal(2, res.Total);
        Assert.Equal(2, res.Candidates.Count);
        Assert.Contains(res.Candidates, c => c.ClassName == "BP_Player_C" && c.InstanceIndex == 1);
        Assert.Contains(res.Candidates, c => c.ClassName == "BP_Enemy_C" && c.InstanceIndex == 3);
        Assert.DoesNotContain(res.Candidates, c => c.InstanceIndex == 2);

        // The Player candidate's slots locked onto Str@0x20 and Def@0x24.
        var player = res.Candidates.First(c => c.InstanceIndex == 1);
        Assert.All(player.Slots, s => Assert.True(s.Locked));
        Assert.Contains(player.Slots, s => s.FieldName == "Str" && s.FieldOffset == 0x20);
        Assert.Contains(player.Slots, s => s.FieldName == "Def" && s.FieldOffset == 0x24);
    }

    [Fact]
    public async Task ModeA_DuplicateValue_NeedsTwoDistinctLeaves()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two slots both want 24; object A has two fields =24 -> match; object B has one -> no.
        long id = await SeedAsync(ct,
            Obj(1, "BP_A_C", ("X", "IntProperty", 0x10, IntHex(24)), ("Y", "IntProperty", 0x14, IntHex(24))),
            Obj(2, "BP_A_C", ("X", "IntProperty", 0x10, IntHex(24)), ("Y", "IntProperty", 0x14, IntHex(7))));

        var q = new SnapshotGroupQuery
        {
            SnapshotIds = { id },
            Slots = { Slot(ValueScanType.Exact, "24"), Slot(ValueScanType.Exact, "24") },
        };
        var res = await _store.GroupMatchAsync(q, ct);

        Assert.Null(res.Error);
        Assert.Single(res.Candidates);
        Assert.Equal(1, res.Candidates[0].InstanceIndex);
    }

    [Fact]
    public async Task ModeA_Between_And_ExcludedClasses()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedAsync(ct,
            Obj(1, "BP_Player_C", ("HP", "IntProperty", 0x10, IntHex(50)), ("Lv", "IntProperty", 0x14, IntHex(7))),
            Obj(2, "WidgetNoise_C", ("HP", "IntProperty", 0x10, IntHex(50)), ("Lv", "IntProperty", 0x14, IntHex(7))));

        var q = new SnapshotGroupQuery
        {
            SnapshotIds = { id },
            Slots = { Slot(ValueScanType.Between, "1", "100"), Slot(ValueScanType.Exact, "7") },
            ExcludedClasses = new() { "WidgetNoise_C" },
        };
        var res = await _store.GroupMatchAsync(q, ct);

        Assert.Null(res.Error);
        Assert.Single(res.Candidates);          // the widget is denied
        Assert.Equal("BP_Player_C", res.Candidates[0].ClassName);
    }

    [Fact]
    public async Task Validation_TooFewSlots_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedAsync(ct, Obj(1, "C", ("X", "IntProperty", 0, IntHex(1))));
        var res = await _store.GroupMatchAsync(new SnapshotGroupQuery
        {
            SnapshotIds = { id },
            Slots = { Slot(ValueScanType.Exact, "1") },
        }, ct);
        Assert.NotNull(res.Error);
        Assert.Empty(res.Candidates);
    }

    [Fact]
    public async Task Validation_ModeB_NotYetAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedAsync(ct, Obj(1, "C", ("X", "IntProperty", 0, IntHex(1)), ("Y", "IntProperty", 4, IntHex(2))));
        var res = await _store.GroupMatchAsync(new SnapshotGroupQuery
        {
            SnapshotIds = { id, id + 1 },   // two snapshots = Mode B
            Slots = { Slot(ValueScanType.Exact, "1"), Slot(ValueScanType.Exact, "2") },
        }, ct);
        Assert.NotNull(res.Error);
        Assert.Contains("Mode B", res.Error);
    }

    [Fact]
    public async Task Validation_RelativePredicateInModeA_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await SeedAsync(ct, Obj(1, "C", ("X", "IntProperty", 0, IntHex(1)), ("Y", "IntProperty", 4, IntHex(2))));
        var res = await _store.GroupMatchAsync(new SnapshotGroupQuery
        {
            SnapshotIds = { id },
            Slots = { Slot(ValueScanType.Decreased, ""), Slot(ValueScanType.Exact, "2") },
        }, ct);
        Assert.NotNull(res.Error);
    }
}
