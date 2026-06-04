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
        // Concurrent because the VM now invokes the store off the UI thread
        // (Task.Run), so gates are created on a thread-pool thread.
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<PivotFieldInfo>>> Gates = new();

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
        public Task<IReadOnlyList<PivotClassInfo>> ListPivotArrayClassesAsync(long s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PivotArrayFieldInfo>> ListPivotArrayFieldsAsync(long s, string c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PivotFieldInfo>> ListPivotArrayPropsAsync(long s, string c, string af, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PivotResult> PivotArrayAsync(ArrayPivotQuery q, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static PivotFieldInfo Field(string name)
        => new() { Name = name, DeclaredType = "IntProperty", DistinctCount = 1, InstanceCount = 2 };

    // ---- Phase C4: DataTable-native (zero-config) pivot source ----

    /// <summary>Fake dump service serving one DataTable list + a 2-row walk.</summary>
    private sealed class DtDumpService : StubDumpService
    {
        public override Task<FindInstancesResult> FindInstancesAsync(
            string className, bool exactMatch = false, int limit = 500, CancellationToken ct = default)
            => Task.FromResult(new FindInstancesResult
            {
                Instances = new()
                {
                    new InstanceResult { Name = "DT_Items", Address = "0xDA7A", ClassName = "DataTable" },
                    // A non-DataTable instance must be filtered out by the VM.
                    new InstanceResult { Name = "SomeActor", Address = "0xBEEF", ClassName = "Actor" },
                },
            });

        public override Task<DataTableWalkResult> WalkDataTableRowsAsync(
            string addr, int offset = 0, int limit = 64, CancellationToken ct = default)
            => Task.FromResult(new DataTableWalkResult
            {
                RowCount = 2, RowStructName = "FItemRow",
                Rows = new()
                {
                    new DataTableRowInfo { RowName = "Sword", DataAddr = "0x1000",
                        Fields = new() { new LiveFieldValue { Name = "Damage", TypeName = "IntProperty", TypedValue = "50" } } },
                    new DataTableRowInfo { RowName = "Shield", DataAddr = "0x2000",
                        Fields = new() { new LiveFieldValue { Name = "Damage", TypeName = "IntProperty", TypedValue = "0" } } },
                },
            });
    }

    [Fact]
    public async Task DataTableSource_ListsTables_FiltersNonDataTables()
    {
        var vm = new ClassPivotViewModel(_store, new MockLoggingService(), null, new DtDumpService());

        vm.SelectedSource = "DataTable";   // empty list → triggers a DataTable refresh
        await vm.PendingLoad!;

        // The Actor instance is filtered; only the DataTable remains.
        Assert.Equal("DT_Items", Assert.Single(vm.DataTables).Name);
        Assert.True(vm.IsDataTableSource);
        Assert.False(vm.IsSnapshotSource);
    }

    [Fact]
    public async Task DataTableSource_SelectTable_LoadsFields_AndRunProjectsRows()
    {
        var vm = new ClassPivotViewModel(_store, new MockLoggingService(), null, new DtDumpService());
        vm.SelectedSource = "DataTable";
        await vm.PendingLoad!;

        vm.SelectedDataTable = vm.DataTables[0];
        await vm.PendingLoad!;            // walk DataTable → field picker + cached rows

        Assert.Contains(vm.Fields, f => f.Name == "Damage");
        Assert.True(vm.CanRunPivot);

        await vm.RunPivotCommand.ExecuteAsync(null);

        // One row per DataTable row, keyed by RowName, no collisions.
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal(new[] { "Sword", "Shield" }, vm.Results.Select(r => r.KeyValue));
        Assert.All(vm.Results, r => Assert.Equal(1, r.Count));
        Assert.Contains("Damage=50", vm.Results[0].ValuesDisplay);
        Assert.Equal("0x1000", vm.Results[0].ObjAddr);   // CE handoff = row struct addr
    }

    // ---- C5: right-click "Pivot this property" handoff ----

    [Fact]
    public async Task PivotForAsync_SelectsClass_AndTicksProperty()
    {
        await SeedInventoryAsync();
        var vm = NewVm();

        await vm.PivotForAsync("BP_Item_C", "Quantity");

        Assert.Equal("Snapshot", vm.SelectedSource);
        Assert.Equal("BP_Item_C", vm.SelectedClass?.ClassName);
        // ItemID is the auto-suggested key; the handed-off Quantity becomes a value.
        Assert.True(vm.Fields.First(f => f.Name == "Quantity").IsValue);
    }

    [Fact]
    public async Task PivotForAsync_UnknownClass_ReportsAndDoesNotThrow()
    {
        await SeedInventoryAsync();
        var vm = NewVm();

        await vm.PivotForAsync("DoesNotExist", "Whatever");

        Assert.Null(vm.SelectedClass);
        Assert.Contains("not in the selected snapshot", vm.StatusText);
    }

    // ---- C6: Snapshot Array source (inner-key pivot) ----

    private async Task SeedCargoAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        long id = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "cargo" }, ct);
        var ps = new SnapshotCapturedObject
        {
            Index = 9, Addr = "0x9000", Name = "PS_9", ClassName = "PlayerState",
            OuterClassName = "World", Path = "/G.M:L.PlayerState_0",
        };
        var arr = new SnapshotCapturedArray { Field = "Cargo" };
        arr.Elements.Add(MakeSlot(0, "Fuel", 100));
        arr.Elements.Add(MakeSlot(1, "Ore", 50));
        ps.Arrays.Add(arr);
        await _store.WriteChunkAsync(id, new[] { ps }, ct);
        await _store.FinalizeSnapshotAsync(id, 1, 2, ct);
    }

    private static SnapshotCapturedArrayElement MakeSlot(int i, string item, int qty)
    {
        var el = new SnapshotCapturedArrayElement { Index = i, KeyName = "ItemID", KeyValue = item };
        el.Fields.Add(new SnapshotCapturedField { Name = "Quantity", Type = "IntProperty", Hex = IntHex(qty), Offset = 0x8 });
        return el;
    }

    [Fact]
    public async Task ArraySource_LoadsArrayClassFieldsProps_AndPivots()
    {
        await SeedCargoAsync();
        var vm = NewVm();
        await vm.RefreshAsync();
        await vm.PendingLoad!;

        vm.SelectedSource = "Snapshot Array";   // reloads class list to array classes
        await vm.PendingLoad!;
        Assert.True(vm.IsArraySource);
        Assert.Contains(vm.Classes, c => c.ClassName == "PlayerState");

        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "PlayerState");
        await vm.PendingLoad!;   // LoadArrayFieldsAsync (auto-selects the array field)
        await vm.PendingLoad!;   // LoadArrayPropsAsync

        var cargo = Assert.Single(vm.ArrayFields);
        Assert.Equal("Cargo", cargo.ArrayField);
        Assert.Equal("ItemID", cargo.InnerKeyName);
        Assert.NotNull(vm.SelectedArrayField);
        Assert.Contains(vm.Fields, f => f.Name == "Quantity");
        Assert.True(vm.CanRunPivot);

        await vm.RunPivotCommand.ExecuteAsync(null);

        // Grouped by inner-key value (ItemID): Fuel + Ore.
        Assert.Equal(2, vm.Results.Count);
        Assert.Contains(vm.Results, r => r.KeyValue == "Fuel");
        Assert.Contains(vm.Results, r => r.KeyValue == "Ore");
    }

    [Fact]
    public async Task RapidClassSwitch_StaleLoadDoesNotClobberLatest()
    {
        var store = new GatedStore();
        var vm = new ClassPivotViewModel(store, new MockLoggingService());
        await vm.RefreshAsync();        // loads snapshot + classes (classes are immediate)
        await vm.PendingLoad!;

        // Select A (load A starts, gated), then B (load B starts, gated). The store
        // call now runs on a thread-pool thread (Task.Run), so wait for both gates
        // to be created before completing them.
        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "A");
        var loadA = vm.PendingLoad!;
        vm.SelectedClass = vm.Classes.First(c => c.ClassName == "B");
        var loadB = vm.PendingLoad!;
        await WaitForGate(store, "A");
        await WaitForGate(store, "B");

        // Complete the NEWER load first, then the stale one.
        store.Gates["B"].SetResult(new[] { Field("BetaField") });
        await loadB;
        store.Gates["A"].SetResult(new[] { Field("AlphaField") });
        await loadA;

        // The stale A load must have bailed — Fields shows only B's field.
        Assert.Equal("BetaField", Assert.Single(vm.Fields).Name);
    }

    // The store call is deferred to a thread pool (Task.Run), so spin briefly until
    // the gated load has registered its TaskCompletionSource.
    private static async Task WaitForGate(GatedStore store, string cls)
    {
        for (int i = 0; i < 400 && !store.Gates.ContainsKey(cls); i++)
            await Task.Delay(5);
    }
}
