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
}
