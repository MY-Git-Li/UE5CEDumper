using System.IO;
using System.Linq;
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
}
