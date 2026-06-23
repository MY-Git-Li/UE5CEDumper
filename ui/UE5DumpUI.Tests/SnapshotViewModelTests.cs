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
        public bool? LastNativeC;
        public bool? LastAutoSkipNoise;

        public override Task<int> BeginSnapshotAsync(string dataType, CancellationToken ct = default)
        {
            LastDataType = dataType;
            return Task.FromResult(3);
        }

        public override Task<SnapshotChunkResult> SnapshotChunkAsync(
            string dataType, bool gameOnly, int offset, int limit,
            bool nativeC = false, bool autoSkipNoise = true, CancellationToken ct = default)
        {
            LastGameOnly = gameOnly;
            LastNativeC = nativeC;
            LastAutoSkipNoise = autoSkipNoise;
            var r = new SnapshotChunkResult { Total = 3, WalkMs = 5, SerializeMs = 2 };
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
        vm.SetEngineState(new EngineState { PeHash = "PEHASH", UEVersion = 504, ModuleBase = "7FF600000000", ProcessCreationTime = "01D9ABCDEF012345" });

        Assert.True(vm.CanCapture);
        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.False(vm.IsCapturing);
        Assert.Equal("NumericNoByte", dump.LastDataType);
        Assert.True(dump.LastGameOnly);
        Assert.False(dump.LastNativeC);   // Native-C default OFF (P3)
        Assert.True(dump.LastAutoSkipNoise);   // Auto-skip noise default ON (source-level skip)

        var list = await _store.ListSnapshotsAsync(TestContext.Current.CancellationToken);
        var saved = Assert.Single(list);
        Assert.Equal("run1", saved.Label);
        Assert.Equal("PEHASH", saved.PeHash);
        // GameSessionId = PeHash-ProcessCreationTime (build 1227+); the per-launch
        // process creation time replaces ModuleBase, which is constant on no-ASLR games.
        Assert.Equal("PEHASH-01D9ABCDEF012345", saved.GameSessionId);
        Assert.Equal(504, saved.UeVersion);
        Assert.Equal(3, saved.ObjectCount);     // 3 objects streamed
        Assert.Equal(4, saved.FieldCount);      // 2 + 1 + 1 fields

        // The list refreshed into the VM, and the label reset for the next run.
        Assert.Single(vm.Snapshots);
        Assert.Equal("", vm.Label);
    }

    [Fact]
    public async Task Capture_PassesIncludeNativeFields_ToSnapshotChunk()
    {
        var dump = new CaptureStub();
        var vm = new SnapshotViewModel(dump, _store, new MockLoggingService())
        {
            SelectedScope = "NumericNoByte",
            GameOnly = true,
            IncludeNativeFields = true,   // P3 opt-in
            Label = "native-run",
        };
        vm.SetEngineState(new EngineState { PeHash = "PEHASH", UEVersion = 504, ModuleBase = "7FF600000000", ProcessCreationTime = "01D9ABCDEF012345" });

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.True(dump.LastNativeC);
    }

    [Fact]
    public async Task Capture_PassesAutoSkipNoise_ToSnapshotChunk()
    {
        var dump = new CaptureStub();
        var vm = new SnapshotViewModel(dump, _store, new MockLoggingService())
        {
            SelectedScope = "NumericNoByte",
            GameOnly = true,
            AutoSkipNoise = false,   // user opted OUT of the source-level skip
            Label = "no-skip-run",
        };
        vm.SetEngineState(new EngineState { PeHash = "PEHASH", UEVersion = 504, ModuleBase = "7FF600000000", ProcessCreationTime = "01D9ABCDEF012345" });

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.False(dump.LastAutoSkipNoise);
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

    // ---- Snapshot Group Match (S3 UI) ----

    private static SnapshotCapturedObject GMakeObj(int idx, string cls,
        params (string n, string t, int off, string h)[] fields)
    {
        var o = new SnapshotCapturedObject
        {
            Index = idx, Addr = $"0x{idx * 0x1000:X}", Name = $"{cls}_{idx}",
            ClassName = cls, OuterClassName = "World", Path = $"/Game/Map:{cls}_{idx}",
        };
        foreach (var (n, t, off, h) in fields)
            o.Fields.Add(new SnapshotCapturedField { Name = n, Type = t, Offset = off, Hex = h });
        return o;
    }

    private async Task<SnapshotViewModel> NewVmWithSnapshotAsync(CancellationToken ct)
    {
        _store.SetActiveGame("GVM");
        long id = await _store.CreateSnapshotAsync(
            new SnapshotMeta { Label = "s", Scope = "NumericNoByte", PeHash = "GVM", GameSessionId = "GVM-T" }, ct);
        await _store.WriteChunkAsync(id, new[]
        {
            GMakeObj(1, "BP_Player_C",
                ("Str", "IntProperty", 0x20, "18000000"),   // 24
                ("Def", "IntProperty", 0x24, "0A000000"),   // 10
                ("Dex", "IntProperty", 0x28, "0E000000")),  // 14
            GMakeObj(2, "BP_Player_C",
                ("Str", "IntProperty", 0x20, "18000000"),   // 24
                ("Def", "IntProperty", 0x24, "63000000")),  // 99 (no 10 -> won't match)
        }, ct);
        await _store.FinalizeSnapshotAsync(id, 2, 5, ct);

        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        vm.SetEngineState(new EngineState { PeHash = "GVM", UEVersion = 504, ModuleBase = "7FF600000000", ProcessCreationTime = "T" });
        await vm.RefreshCommand.ExecuteAsync(null);   // deterministic load (don't rely on the fire-and-forget refresh)
        return vm;
    }

    [Fact]
    public async Task GroupMatch_PopulatesCandidates_FromSeededSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var vm = await NewVmWithSnapshotAsync(ct);

        vm.IsGroupMode = true;
        Assert.NotNull(vm.GroupSnapshot);                  // defaulted to the newest snapshot
        vm.GroupInputs[0].ScanType = ValueScanType.Exact; vm.GroupInputs[0].Value = "24";
        vm.GroupInputs[1].ScanType = ValueScanType.Exact; vm.GroupInputs[1].Value = "10";
        Assert.True(vm.CanRunGroupMatch);

        await vm.RunGroupMatchCommand.ExecuteAsync(null);

        var c = Assert.Single(vm.GroupCandidates);
        Assert.Equal(1, c.InstanceIndex);                  // only object 1 holds 24 AND 10
        Assert.All(c.Slots, s => Assert.True(s.Locked));
    }

    [Fact]
    public async Task GroupRows_AddRemove_BoundedTwoToFour()
    {
        var ct = TestContext.Current.CancellationToken;
        var vm = await NewVmWithSnapshotAsync(ct);

        Assert.Equal(2, vm.GroupInputs.Count);
        Assert.False(vm.CanRemoveGroupRow);                // at the min
        vm.AddGroupRowCommand.Execute(null);
        vm.AddGroupRowCommand.Execute(null);
        Assert.Equal(4, vm.GroupInputs.Count);
        Assert.False(vm.CanAddGroupRow);                   // at the max
        vm.AddGroupRowCommand.Execute(null);               // no-op past 4
        Assert.Equal(4, vm.GroupInputs.Count);
        vm.RemoveGroupRowCommand.Execute(null);
        Assert.Equal(3, vm.GroupInputs.Count);
    }

    [Fact]
    public async Task GroupMatch_MissingValue_ShowsErrorNoCandidates()
    {
        var ct = TestContext.Current.CancellationToken;
        var vm = await NewVmWithSnapshotAsync(ct);

        vm.GroupInputs[0].Value = "24";                    // slot 2 left blank -> store validation error
        await vm.RunGroupMatchCommand.ExecuteAsync(null);

        Assert.Empty(vm.GroupCandidates);
        Assert.False(string.IsNullOrEmpty(vm.GroupStatusText));
    }

    private async Task<SnapshotViewModel> NewVmWith2SnapshotsAsync(CancellationToken ct)
    {
        var vm = await NewVmWithSnapshotAsync(ct);
        long id2 = await _store.CreateSnapshotAsync(
            new SnapshotMeta { Label = "s2", Scope = "NumericNoByte", PeHash = "GVM", GameSessionId = "GVM-T" }, ct);
        await _store.WriteChunkAsync(id2, new[] { GMakeObj(9, "BP_Player_C", ("Str", "IntProperty", 0x20, "18000000")) }, ct);
        await _store.FinalizeSnapshotAsync(id2, 1, 1, ct);
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task GroupCompareSnapshot_PreservedAcrossRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var vm = await NewVmWith2SnapshotsAsync(ct);
        Assert.True(vm.Snapshots.Count >= 2);

        vm.GroupSnapshot = vm.Snapshots[0];
        vm.GroupCompareSnapshot = vm.Snapshots[1];   // Mode B
        Assert.True(vm.IsGroupCompareMode);
        long compareId = vm.GroupCompareSnapshot!.Id;

        await vm.RefreshCommand.ExecuteAsync(null);  // e.g. after a capture completes

        Assert.NotNull(vm.GroupCompareSnapshot);     // preserved (was: silently dropped -> reverts to Mode A)
        Assert.Equal(compareId, vm.GroupCompareSnapshot!.Id);
        Assert.True(vm.IsGroupCompareMode);
    }

    [Fact]
    public async Task GroupRowActions_DisabledForCrossSessionSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetActiveGame("GVM");
        long id = await _store.CreateSnapshotAsync(
            new SnapshotMeta { Label = "old-session", Scope = "NumericNoByte", PeHash = "GVM", GameSessionId = "GVM-OLD" }, ct);
        await _store.WriteChunkAsync(id, new[] { GMakeObj(1, "BP_Player_C", ("Str", "IntProperty", 0x20, "18000000")) }, ct);
        await _store.FinalizeSnapshotAsync(id, 1, 1, ct);

        var vm = new SnapshotViewModel(new CaptureStub(), _store, new MockLoggingService());
        // Current live session is GVM-NEW; the snapshot is from GVM-OLD (a previous run).
        vm.SetEngineState(new EngineState { PeHash = "GVM", UEVersion = 504, ModuleBase = "7FF600000000", ProcessCreationTime = "NEW" });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.GroupSnapshot = vm.Snapshots[0];

        // Cross-session: every per-slot handoff (Live / Copy / Locate-GWorld / Locate-GameEngine)
        // must be disabled — its captured obj_addr is stale in the running game.
        Assert.False(vm.CanUseGroupRowActions);
        Assert.False(vm.CanLocateGroupRowInGWorld);
    }

    [Fact]
    public async Task IsGroupCompareMode_RaisedWhenPrimaryChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var vm = await NewVmWith2SnapshotsAsync(ct);
        vm.GroupSnapshot = vm.Snapshots[0];
        vm.GroupCompareSnapshot = vm.Snapshots[1];
        Assert.True(vm.IsGroupCompareMode);

        bool raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsGroupCompareMode)) raised = true; };
        vm.GroupSnapshot = vm.Snapshots[1];          // primary now equals compare -> collapses to Mode A

        Assert.True(raised);                          // OnGroupSnapshotChanged re-raises it
        Assert.False(vm.IsGroupCompareMode);
    }
}
