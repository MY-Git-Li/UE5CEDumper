using System.IO;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Covers the Dump Explorer's offline pieces: the JSONL reader's flatten +
/// type-composition, and the ViewModel's live-match split (matched by object
/// PATH, restart-safe) + keyword/category filter + jump handoff.
/// </summary>
public class DumpExplorerTests
{
    private const string PlayerPath = "/Game/BP_Player.BP_Player_C";
    private const string GhostPath  = "/Game/BP_Ghost.BP_Ghost_C";

    // A small, well-formed "Dump All" corpus: meta + 2 classes (one with props
    // and a func) + a summary line. The BP_Player addr differs from the live one
    // to prove matching is by path, not the dump-time address.
    private const string SampleJsonl =
        "{\"kind\":\"meta\",\"ue_version\":505,\"module\":\"Game.exe\",\"object_count\":1000,\"dumped_at\":\"2026-07-10T00:00:00Z\",\"dumper_build\":2034}\n" +
        "{\"kind\":\"class\",\"name\":\"BP_Player_C\",\"addr\":\"0x1000\",\"path\":\"" + PlayerPath + "\",\"meta\":\"BlueprintGeneratedClass\",\"super\":\"Character\",\"props\":[" +
            "{\"name\":\"Health\",\"type\":\"FloatProperty\",\"offset\":128,\"size\":4}," +
            "{\"name\":\"Inventory\",\"type\":\"ArrayProperty\",\"offset\":132,\"size\":16,\"inner_type\":\"ObjectProperty\",\"obj_class\":\"BP_Item_C\"}]," +
            "\"funcs\":[{\"name\":\"TakeDamage\",\"addr\":\"0x2000\",\"return_type\":\"void\",\"num_parms\":1}]}\n" +
        "{\"kind\":\"class\",\"name\":\"BP_Ghost_C\",\"addr\":\"0x3000\",\"path\":\"" + GhostPath + "\",\"meta\":\"BlueprintGeneratedClass\",\"super\":\"Actor\",\"props\":[" +
            "{\"name\":\"Mana\",\"type\":\"FloatProperty\",\"offset\":64,\"size\":4}]}\n" +
        "{\"kind\":\"summary\",\"classes_emitted\":2}\n";

    private static async Task<string> WriteTempAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dumpexpl-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    // ---- Reader ----

    [Fact]
    public async Task Reader_FlattensClassesPropsFuncs_AndMeta()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var model = await DumpJsonlReader.ReadAsync(path, ct: TestContext.Current.CancellationToken);

            Assert.Equal(2, model.ClassCount);
            Assert.Equal(3, model.PropertyCount);
            Assert.Equal(1, model.FunctionCount);
            Assert.Equal(6, model.Entries.Count);   // 2 class + 3 prop + 1 func

            Assert.NotNull(model.Meta);
            Assert.Equal(505, model.Meta!.UeVersion);
            Assert.Equal("Game.exe", model.Meta.Module);
            Assert.Equal(1000, model.Meta.ObjectCount);

            var cls = model.Entries.Single(e => e.Kind == DumpEntryKind.Class && e.Name == "BP_Player_C");
            Assert.Equal(": Character", cls.TypeInfo);
            Assert.Equal(PlayerPath, cls.Path);
            Assert.Equal("0x1000", cls.ClassAddr);

            var health = model.Entries.Single(e => e.Kind == DumpEntryKind.Property && e.Name == "Health");
            Assert.Equal("BP_Player_C", health.OwnerClass);
            Assert.Equal("0x80", health.OffsetDisplay);   // 128 -> 0x80

            var inv = model.Entries.Single(e => e.Name == "Inventory");
            Assert.Equal("ArrayProperty<ObjectProperty>:BP_Item_C", inv.TypeInfo);

            var func = model.Entries.Single(e => e.Kind == DumpEntryKind.Function);
            Assert.Equal("TakeDamage", func.Name);
            Assert.Equal("void (1)", func.TypeInfo);
            Assert.Equal("", func.OffsetDisplay);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_ParsesRealWorldDumpFixture()
    {
        // A sanitized 23-class slice of a real UE4.27 "Dump All"
        // (TestData/sample-dump.jsonl — addresses/pe_hash/module scrubbed, props
        // capped). Guards the reader against real-world property shapes that the
        // hand-crafted samples above don't cover (nested array-of-struct, enum,
        // soft/weak/interface/delegate types, real prop_flags, etc.).
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-dump.jsonl");
        Assert.True(File.Exists(path), $"fixture not copied to output: {path}");

        var model = await DumpJsonlReader.ReadAsync(path, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(model.Meta);
        Assert.Equal(427, model.Meta!.UeVersion);
        Assert.Equal("SampleGame-Win64-Shipping.exe", model.Meta.Module);

        // Locked counts (regenerating the fixture would flag this).
        Assert.Equal(23, model.ClassCount);
        Assert.Equal(188, model.PropertyCount);
        Assert.Equal(38, model.FunctionCount);
        Assert.Equal(model.ClassCount + model.PropertyCount + model.FunctionCount, model.Entries.Count);
        Assert.Equal(model.ClassCount, model.Entries.Count(e => e.Kind == DumpEntryKind.Class));
        Assert.All(model.Entries, e => Assert.False(string.IsNullOrEmpty(e.Name)));

        // Real-world type composition: nested array-of-struct / struct / enum / object.
        Assert.Equal("ArrayProperty<StructProperty:TemplateSectionPropertyScale>",
            model.Entries.Single(e => e.Name == "PropertyScales" && e.OwnerClass == "TemplateSequenceSection").TypeInfo);
        Assert.Equal("StructProperty<Guid>",
            model.Entries.Single(e => e.Name == "Signature" && e.OwnerClass == "MovieSceneSignedObject").TypeInfo);
        Assert.Equal("EnumProperty(EMovieSceneCompletionMode)",
            model.Entries.Single(e => e.Name == "DefaultCompletionMode" && e.OwnerClass == "MovieSceneSequence").TypeInfo);

        var owner = model.Entries.Single(e => e.Name == "OwnerWidget" && e.OwnerClass == "LifeUIListHelperObject");
        Assert.Equal("ObjectProperty:UserWidget", owner.TypeInfo);
        Assert.Equal("0x28", owner.OffsetDisplay);                 // offset 40
        Assert.Equal("LifeUIListHelperObject", owner.OwningClassName);
    }

    [Fact]
    public async Task Reader_SkipsMalformedLines()
    {
        var path = await WriteTempAsync(
            SampleJsonl + "{ this is not json }\n" +
            "{\"kind\":\"class\",\"name\":\"BP_Extra_C\",\"path\":\"/Game/X.X_C\",\"meta\":\"Class\"}\n");
        try
        {
            var model = await DumpJsonlReader.ReadAsync(path, ct: TestContext.Current.CancellationToken);
            // The corrupt line is skipped; the valid trailing class is kept.
            Assert.Equal(3, model.ClassCount);
            Assert.Contains(model.Entries, e => e.Name == "BP_Extra_C");
        }
        finally { File.Delete(path); }
    }

    // ---- ViewModel ----

    private sealed class FakeDumpService : StubDumpService
    {
        public List<UObjectNode> Objects { get; } = new();

        public override Task<ObjectListResult> GetObjectListAsync(int offset, int limit, CancellationToken ct = default)
        {
            if (offset > 0)
                return Task.FromResult(new ObjectListResult { Total = Objects.Count, Scanned = 0, Objects = new() });
            return Task.FromResult(new ObjectListResult { Total = Objects.Count, Scanned = Objects.Count, Objects = Objects });
        }
    }

    private sealed class NoopLogger : ILoggingService
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
        public void Error(string m, Exception ex) { }
        public void Debug(string m) { }
        public void Info(string c, string m) { }
        public void Warn(string c, string m) { }
        public void Error(string c, string m) { }
        public void Error(string c, string m, Exception ex) { }
        public void Debug(string c, string m) { }
        public void StartProcessMirror(string p) { }
        public void StopProcessMirror() { }
    }

    private static DumpExplorerViewModel CreateVm(FakeDumpService dump, MockPlatformService platform)
        => new(dump, new NoopLogger(), platform);

    private static FakeDumpService LiveGameWithPlayer()
    {
        var dump = new FakeDumpService();
        // Only BP_Player is live — matched by SHORT NAME (get_object_list has no
        // path), at a DIFFERENT address than the dump recorded (proves restart-safe).
        dump.Objects.Add(new UObjectNode
        {
            Address = "0xLIVE111", Name = "BP_Player_C", ClassName = "BlueprintGeneratedClass",
        });
        // A non-class object sharing the name must be ignored (class-like filter).
        dump.Objects.Add(new UObjectNode
        {
            Address = "0xDEAD", Name = "BP_Player_C", ClassName = "Actor",
        });
        return dump;
    }

    [Fact]
    public async Task Vm_LiveMatch_SplitsByClassName_AndJumpsToLiveAddr()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var vm = CreateVm(LiveGameWithPlayer(), new MockPlatformService(Path.GetTempPath()));
            vm.SetConnected(true);
            string? jumped = null;
            vm.NavigateToLiveWalker += a => jumped = a;

            await vm.LoadFromPathAsync(path);

            // BP_Player class + 2 props + 1 func = 4 matched, all at the live addr.
            Assert.Equal(4, vm.Matched.Count);
            Assert.All(vm.Matched, e => Assert.Equal("0xLIVE111", e.LiveAddr));
            Assert.All(vm.Matched, e => Assert.True(e.IsMatched));

            // BP_Ghost class + Mana prop = 2 unmatched.
            Assert.Equal(2, vm.Unmatched.Count);
            Assert.All(vm.Unmatched, e => Assert.False(e.IsMatched));

            // Jump a matched row -> hands off the CURRENT live address.
            var row = vm.Matched.First(e => e.Kind == DumpEntryKind.Class);
            vm.OpenInLiveWalkerCommand.Execute(row);
            Assert.Equal("0xLIVE111", jumped);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Vm_Disconnect_InvalidatesLiveMatch()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var vm = CreateVm(LiveGameWithPlayer(), new MockPlatformService(Path.GetTempPath()));
            vm.SetConnected(true);
            await vm.LoadFromPathAsync(path);
            Assert.Equal(4, vm.Matched.Count);   // BP_Player family matched

            // Disconnect: stale LiveAddrs must be cleared so no row offers a jump
            // to a dead address; everything moves to "not in current game".
            vm.SetConnected(false);
            Assert.Empty(vm.Matched);
            Assert.Equal(6, vm.Unmatched.Count);
            Assert.All(vm.Unmatched, e => Assert.False(e.IsMatched));
            Assert.All(vm.Unmatched, e => Assert.Equal("", e.LiveAddr));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Vm_Filter_ByKeywordAndCategory()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var vm = CreateVm(new FakeDumpService(), new MockPlatformService(Path.GetTempPath()));
            // Not connected -> everything is "not in current game".
            await vm.LoadFromPathAsync(path);
            Assert.Empty(vm.Matched);
            Assert.Equal(6, vm.Unmatched.Count);

            // Keyword narrows across all kinds at once.
            vm.SearchText = "mana";
            Assert.Single(vm.Unmatched);
            Assert.Equal("Mana", vm.Unmatched[0].Name);

            // Category filter (2 = Property) with no keyword -> only property rows.
            vm.SearchText = "";
            vm.SelectedCategoryIndex = 2;
            Assert.All(vm.Unmatched, e => Assert.Equal(DumpEntryKind.Property, e.Kind));
            Assert.Equal(3, vm.Unmatched.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Vm_LoadLastExport_EnabledAfterSetPath_AndLoads()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var vm = CreateVm(new FakeDumpService(), new MockPlatformService(Path.GetTempPath()));
            Assert.False(vm.LoadLastExportCommand.CanExecute(null));   // nothing exported yet
            Assert.Equal("", vm.LastExportFileName);

            vm.SetLastExportPath(path);   // as MainWindow does after Export ▸ Dump All
            Assert.True(vm.LoadLastExportCommand.CanExecute(null));
            Assert.Equal(Path.GetFileName(path), vm.LastExportFileName);

            await vm.LoadLastExportCommand.ExecuteAsync(null);
            Assert.True(vm.HasFile);
            Assert.Equal(6, vm.Unmatched.Count);   // not connected -> all unmatched
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Vm_OpenUnmatched_DoesNotJump()
    {
        var path = await WriteTempAsync(SampleJsonl);
        try
        {
            var vm = CreateVm(new FakeDumpService(), new MockPlatformService(Path.GetTempPath()));
            await vm.LoadFromPathAsync(path);
            string? jumped = null;
            vm.NavigateToLiveWalker += a => jumped = a;

            vm.OpenInLiveWalkerCommand.Execute(vm.Unmatched[0]);
            Assert.Null(jumped);   // no live object -> no handoff
        }
        finally { File.Delete(path); }
    }
}
