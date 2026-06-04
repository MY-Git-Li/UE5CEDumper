using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnapshotStore _store;

    public SnapshotViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpSnapVm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SnapshotStore(new MockPlatformService(_tempDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // Stub that streams 3 objects (4 fields) across two non-empty chunks, then a
    // terminal empty chunk — mirrors the DLL's stateless cursor pagination.
    private sealed class CaptureStub : StubDumpService
    {
        public string? LastDataType;
        public bool? LastGameOnly;

        public override Task<int> BeginSnapshotAsync(string dataType, CancellationToken ct = default)
        {
            LastDataType = dataType;
            return Task.FromResult(3);
        }

        public override Task<SnapshotChunkResult> SnapshotChunkAsync(
            string dataType, bool gameOnly, int offset, int limit, CancellationToken ct = default)
        {
            LastGameOnly = gameOnly;
            var r = new SnapshotChunkResult { Total = 3 };
            if (offset == 0)
            {
                r.Scanned = 2;
                r.Objects.Add(MakeObj(0, ("A", "IntProperty", "01000000"), ("B", "FloatProperty", "0000803F")));
                r.Objects.Add(MakeObj(1, ("A", "IntProperty", "02000000")));
            }
            else if (offset == 2)
            {
                r.Scanned = 1;
                r.Objects.Add(MakeObj(2, ("A", "IntProperty", "03000000")));
            }
            else
            {
                r.Scanned = 0;  // terminal
            }
            return Task.FromResult(r);
        }

        private static SnapshotCapturedObject MakeObj(int idx, params (string n, string t, string h)[] fields)
        {
            var o = new SnapshotCapturedObject
            {
                Index = idx, Addr = $"0x{idx:X}", Name = $"Obj_{idx}",
                ClassName = "BP_Thing_C", OuterClassName = "World",
                Path = $"/Game/Map.Map:PersistentLevel.Obj_{idx}",
            };
            foreach (var (n, t, h) in fields)
                o.Fields.Add(new SnapshotCapturedField { Name = n, Type = t, Hex = h });
            return o;
        }
    }

    [Fact]
    public async Task Capture_StreamsAllChunks_PersistsWithCorrectCounts()
    {
        var dump = new CaptureStub();
        var vm = new SnapshotViewModel(dump, _store, new MockLoggingService())
        {
            SelectedScope = "NumericNoByte",
            GameOnly = true,
            Label = "run1",
        };
        vm.SetEngineState(new EngineState { PeHash = "PEHASH", UEVersion = 504, ModuleBase = "7FF600000000" });

        Assert.True(vm.CanCapture);
        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.False(vm.IsCapturing);
        Assert.Equal("NumericNoByte", dump.LastDataType);
        Assert.True(dump.LastGameOnly);

        var list = await _store.ListSnapshotsAsync(TestContext.Current.CancellationToken);
        var saved = Assert.Single(list);
        Assert.Equal("run1", saved.Label);
        Assert.Equal("PEHASH", saved.PeHash);
        Assert.Equal("PEHASH-7FF600000000", saved.GameSessionId);
        Assert.Equal(504, saved.UeVersion);
        Assert.Equal(3, saved.ObjectCount);     // 3 objects streamed
        Assert.Equal(4, saved.FieldCount);      // 2 + 1 + 1 fields

        // The list refreshed into the VM, and the label reset for the next run.
        Assert.Single(vm.Snapshots);
        Assert.Equal("", vm.Label);
    }

    [Fact]
    public void CanCapture_RequiresEngineState()
    {
        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        Assert.False(vm.CanCapture);
        vm.SetEngineState(new EngineState { PeHash = "X" });
        Assert.True(vm.CanCapture);
    }

    private static SnapshotCapturedObject Obj(int idx, string field, string type, string hex)
    {
        var o = new SnapshotCapturedObject
        {
            Index = idx, Addr = $"0x{idx:X}", Name = $"O_{idx}",
            ClassName = "C", OuterClassName = "W", Path = $"/Game/M.M:L.O_{idx}",
        };
        o.Fields.Add(new SnapshotCapturedField { Name = field, Type = type, Hex = hex });
        return o;
    }

    [Fact]
    public async Task RunDiff_PopulatesChangedRows_AndDefaultsPickers()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("G");
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[] { Obj(1, "HP", "IntProperty", "64000000") }, ct);  // 100
        await _store.FinalizeSnapshotAsync(a, 1, 1, ct);
        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[] { Obj(1, "HP", "IntProperty", "5A000000") }, ct);  // 90
        await _store.FinalizeSnapshotAsync(b, 1, 1, ct);

        // Store already scoped to "G" above; a single awaited refresh avoids the
        // SetEngineState fire-and-forget refresh racing this one.
        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        await vm.RefreshCommand.ExecuteAsync(null);

        // Default pickers: A = older, B = newer.
        Assert.Equal(a, vm.DiffA!.Id);
        Assert.Equal(b, vm.DiffB!.Id);
        Assert.True(vm.CanRunDiff);

        await vm.RunDiffCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.DiffRows);
        Assert.Equal("HP", row.PropName);
        Assert.Equal("100", row.OldValue);
        Assert.Equal("90", row.NewValue);
        Assert.Equal(SnapshotDiffDirection.Down, row.Direction);
    }

    [Fact]
    public async Task Diff_ClientFilter_NarrowsRowsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("G2");
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[]
        {
            Obj(1, "HP", "IntProperty", "64000000"),    // 100
            Obj(2, "Mana", "IntProperty", "0A000000"),  // 10
        }, ct);
        await _store.FinalizeSnapshotAsync(a, 2, 2, ct);
        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[]
        {
            Obj(1, "HP", "IntProperty", "5A000000"),    // 90  (down)
            Obj(2, "Mana", "IntProperty", "14000000"),  // 20  (up)
        }, ct);
        await _store.FinalizeSnapshotAsync(b, 2, 2, ct);

        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RunDiffCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.DiffRows.Count);

        // Typing a field filter narrows the grid live (no re-query).
        vm.DiffPropFilter = "HP";
        Assert.Equal("HP", Assert.Single(vm.DiffRows).PropName);

        vm.DiffPropFilter = "";   // cleared -> restored
        Assert.Equal(2, vm.DiffRows.Count);

        // Direction filter is also client-side.
        vm.SelectedDiffDirection = "Increased";
        Assert.Equal("Mana", Assert.Single(vm.DiffRows).PropName);
    }

    [Fact]
    public async Task Diff_GlobalFilter_ValueRange_AndPickerOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("G4");
        long a = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "a" }, ct);
        await _store.WriteChunkAsync(a, new[]
        {
            Obj(1, "HP", "IntProperty", "64000000"),    // 100
            Obj(2, "Mana", "IntProperty", "0A000000"),  // 10
        }, ct);
        await _store.FinalizeSnapshotAsync(a, 2, 2, ct);
        long b = await _store.CreateSnapshotAsync(new SnapshotMeta { Label = "b" }, ct);
        await _store.WriteChunkAsync(b, new[]
        {
            Obj(1, "HP", "IntProperty", "5A000000"),    // 90  (down)
            Obj(2, "Mana", "IntProperty", "14000000"),  // 20  (up)
        }, ct);
        await _store.FinalizeSnapshotAsync(b, 2, 2, ct);

        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RunDiffCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.DiffRows.Count);

        // Picker candidates are the distinct field/class values from the result set.
        Assert.Contains("HP", vm.DiffFieldOptions);
        Assert.Contains("Mana", vm.DiffFieldOptions);
        Assert.Contains("C", vm.DiffClassOptions);

        // Global filter matches across columns: "90" appears only on the HP row's New.
        vm.DiffGlobalFilter = "90";
        Assert.Equal("HP", Assert.Single(vm.DiffRows).PropName);
        vm.DiffGlobalFilter = "";
        Assert.Equal(2, vm.DiffRows.Count);

        // New-value range is button-applied; New<=50 keeps only Mana (20), drops HP (90).
        vm.DiffNewMax = "50";
        Assert.Equal(2, vm.DiffRows.Count);   // not applied until the button
        vm.ApplyDiffRangeCommand.Execute(null);
        Assert.Equal("Mana", Assert.Single(vm.DiffRows).PropName);

        vm.ResetDiffRangeCommand.Execute(null);
        Assert.Equal(2, vm.DiffRows.Count);
        Assert.Equal("", vm.DiffNewMax);
    }

    [Fact]
    public async Task OpenInLiveWalker_RaisesNavigateWithObjectAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("G3");
        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());

        string? navAddr = null;
        vm.NavigateToInstance += a => navAddr = a;

        var row = new SnapshotDiffRow { ObjAddr = "0x7FF600001234", ClassName = "C", PropName = "HP" };
        vm.OpenInLiveWalkerCommand.Execute(row);
        Assert.Equal("0x7FF600001234", navAddr);

        // Null / empty address is a no-op.
        navAddr = null;
        vm.OpenInLiveWalkerCommand.Execute(new SnapshotDiffRow { ObjAddr = "" });
        Assert.Null(navAddr);
    }
}
