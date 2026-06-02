using System.IO;
using System.Linq;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

public class ClassPivotViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public ClassPivotViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpPivotVm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
        _store.SetActiveGame("G");
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

    private async Task SeedInventoryAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "inv" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            Obj(1, "BP_Item_C", "/G.M:L.Item_0", ("ItemID", 1), ("Quantity", 10)),
            Obj(2, "BP_Item_C", "/G.M:L.Item_1", ("ItemID", 1), ("Quantity", 20)),
            Obj(3, "BP_Item_C", "/G.M:L.Item_2", ("ItemID", 2), ("Quantity", 5)),
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 3, 6, ct);
    }

    private ClassPivotViewModel NewVm()
        => new ClassPivotViewModel(_store, new MockLoggingService());

    [Fact]
    public async Task Refresh_LoadsSnapshotsAndClasses()
    {
        await SeedInventoryAsync();
        var vm = NewVm();

        await vm.RefreshAsync();
        await vm.PendingLoad!;   // class load started by the snapshot selection

        Assert.NotNull(vm.SelectedSnapshot);
        Assert.Contains(vm.Classes, c => c.ClassName == "BP_Item_C");
    }

    [Fact]
    public async Task SelectClass_SuggestsKey_AndPreTicksValues()
    {
        await SeedInventoryAsync();
        var vm = NewVm();
        await vm.RefreshAsync();
        await vm.PendingLoad!;

        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "BP_Item_C");
        await vm.PendingLoad!;   // field load + key suggestion

        // ItemID is the best key (int, id-named, partitions 2<3) -> Field mode.
        Assert.Equal("Field", vm.SelectedKeyMode);
        Assert.Equal("ItemID", vm.SelectedKeyField);
        // Quantity is an interesting value field -> pre-ticked; the key is not.
        Assert.True(vm.Fields.First(f => f.Name == "Quantity").IsValue);
        Assert.False(vm.Fields.First(f => f.Name == "ItemID").IsValue);
    }

    [Fact]
    public async Task RunPivot_FieldMode_PopulatesGroups()
    {
        await SeedInventoryAsync();
        var vm = NewVm();
        await vm.RefreshAsync();
        await vm.PendingLoad!;
        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "BP_Item_C");
        await vm.PendingLoad!;

        Assert.True(vm.CanRunPivot);
        await vm.RunPivotCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Results.Count);
        var top = vm.Results[0];
        Assert.Equal("1", top.KeyValue);
        Assert.Equal(2, top.Count);
        Assert.Contains("Quantity=", top.ValuesDisplay);
    }

    [Fact]
    public void OpenInLiveWalker_RaisesNavigate()
    {
        var vm = NewVm();
        string? nav = null;
        vm.NavigateToInstance += a => nav = a;

        vm.OpenInLiveWalkerCommand.Execute(new PivotResultRow { ObjAddr = "0x7FF600001234" });
        Assert.Equal("0x7FF600001234", nav);

        nav = null;
        vm.OpenInLiveWalkerCommand.Execute(new PivotResultRow { ObjAddr = "" });
        Assert.Null(nav);
    }

    // ---- Concurrency guard: a stale field-load must not clobber the latest ----

    // ISnapshotStore stub whose ListPivotFieldsAsync is gated per class so a test
    // can complete an earlier (superseded) load AFTER a later one.
    private sealed class GatedStore : ISnapshotStore
    {
        public readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<PivotFieldInfo>>> Gates = new();

        public string DatabasePath => "";
        public void SetActiveGame(string? peHash) { }
        public Task<IReadOnlyList<SnapshotMeta>> ListSnapshotsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SnapshotMeta>>(new[] { new SnapshotMeta { Id = 1, Label = "s" } });
        public Task<IReadOnlyList<PivotClassInfo>> ListPivotClassesAsync(long snapshotId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PivotClassInfo>>(new[]
            {
                new PivotClassInfo { ClassName = "A", InstanceCount = 2 },
                new PivotClassInfo { ClassName = "B", InstanceCount = 2 },
            });
        public Task<IReadOnlyList<PivotFieldInfo>> ListPivotFieldsAsync(long snapshotId, string className, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<PivotFieldInfo>>();
            Gates[className] = tcs;
            return tcs.Task;
        }

        public Task<long> CreateSnapshotAsync(SnapshotMeta meta, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> WriteChunkAsync(long id, IReadOnlyList<SnapshotCapturedObject> o, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FinalizeSnapshotAsync(long id, int oc, int fc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteSnapshotAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SnapshotUsage> GetUsageAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SnapshotDiffResult> DiffSnapshotsAsync(long a, long b, SnapshotDiffFilter f, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SpcResult> SpcQueryAsync(SpcQuery q, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PivotResult> PivotAsync(PivotQuery q, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> EnforceQuotaAsync(long bytes, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static PivotFieldInfo Field(string name)
        => new() { Name = name, DeclaredType = "IntProperty", DistinctCount = 1, InstanceCount = 2 };

    [Fact]
    public async Task RapidClassSwitch_StaleLoadDoesNotClobberLatest()
    {
        var store = new GatedStore();
        var vm = new ClassPivotViewModel(store, new MockLoggingService());
        await vm.RefreshAsync();        // loads snapshot + classes (classes are immediate)
        await vm.PendingLoad!;

        // Select A (load A starts, gated), then B (load B starts, gated).
        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "A");
        var loadA = vm.PendingLoad!;
        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "B");
        var loadB = vm.PendingLoad!;

        // Complete the NEWER load first, then the stale one.
        store.Gates["B"].SetResult(new[] { Field("BetaField") });
        await loadB;
        store.Gates["A"].SetResult(new[] { Field("AlphaField") });
        await loadA;

        // The stale A load must have bailed — Fields shows only B's field.
        Assert.Equal("BetaField", Assert.Single(vm.Fields).Name);
    }
}
