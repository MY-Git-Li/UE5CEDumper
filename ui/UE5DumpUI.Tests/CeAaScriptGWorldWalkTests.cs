using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the restart-stable "Copy CE AA Script" — the GWorld-anchored Lua
/// walk generator (CeXmlExportService.GenerateGWorldWalkedSymbolXml) and the
/// LiveWalkerViewModel 3-case dispatch (AOB walk / hardcoded-GWorld walk /
/// legacy hardcoded address).
/// </summary>
public class CeAaScriptGWorldWalkTests
{
    private static BreadcrumbItem Bc(string addr, string fieldName, bool deref = false,
        int offset = 0, bool container = false)
        => new()
        {
            Address = addr,
            Label = fieldName,
            FieldName = fieldName,
            FieldOffset = offset,
            IsPointerDeref = deref,
            IsContainerView = container,
        };

    // GWorld root → deref +0x30 → deref +0x98 → deref +0x1A0 (leaf).
    private static List<BreadcrumbItem> SampleSpine() => new()
    {
        Bc("0x1000", "GWorld"),
        Bc("0x2000", "PersistentLevel", deref: true, offset: 0x30),
        Bc("0x3000", "OwningGameInstance", deref: true, offset: 0x98),
        Bc("0x4000", "PlayerController", deref: true, offset: 0x1A0),
    };

    // ── Generator: AOB branch ──────────────────────────────────────────

    [Fact]
    public void Aob_branch_scans_walks_and_registers_leaf()
    {
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "BP_Test", SampleSpine(), useAob: true,
            aob: "48 8B 1D ?? ?? ?? ??", aobPos: 3, aobLen: 7, gworldSlotAddr: "");

        Assert.Contains("[ENABLE]", xml);
        Assert.Contains("[DISABLE]", xml);
        Assert.Contains("AOBScanModuleUE", xml);
        Assert.Contains("aob='48 8B 1D ?? ?? ?? ??'", xml);
        // Base deref then one readQword per spine step (index 1..3).
        Assert.Contains("local addr = gworld_base and readQword(gworld_base)", xml);
        Assert.Contains("readQword(addr + 0x30)", xml);
        Assert.Contains("readQword(addr + 0x98)", xml);
        Assert.Contains("readQword(addr + 0x1A0)", xml);
        Assert.Contains("registerSymbol('BP_Test', addr)", xml);
        // DISABLE unregisters BOTH the leaf and the GWorld base symbol.
        Assert.Contains("unregisterSymbol('BP_Test')", xml);
        Assert.Contains("unregisterSymbol('gworld_base_", xml);
        // Not the legacy hardcoded form.
        Assert.DoesNotContain("define(", xml);
    }

    [Fact]
    public void Aob_branch_does_not_hardcode_a_base_address()
    {
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", SampleSpine(), useAob: true,
            aob: "48 8B 1D", aobPos: 3, aobLen: 7, gworldSlotAddr: "0xDEADBEEF");
        Assert.DoesNotContain("tonumber('", xml);   // base comes from the scan, not a literal
    }

    // ── Generator: hardcoded-GWorld branch ─────────────────────────────

    [Fact]
    public void Hardcode_branch_uses_tonumber_and_no_aob_scan()
    {
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "BP_Test", SampleSpine(), useAob: false,
            aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x7FF61234ABCD");

        Assert.DoesNotContain("AOBScanModuleUE", xml);
        Assert.Contains("tonumber('7FF61234ABCD', 16)", xml);   // 0x stripped, 64-bit safe
        Assert.Contains("registerSymbol('gworld_base_", xml);   // GWorld registered too
        Assert.Contains("readQword(addr + 0x30)", xml);
        Assert.Contains("registerSymbol('BP_Test', addr)", xml);
        Assert.Contains("unregisterSymbol('BP_Test')", xml);
        Assert.Contains("unregisterSymbol('gworld_base_", xml);  // DISABLE symmetry
    }

    // ── Generator: walk semantics + edge cases ─────────────────────────

    [Fact]
    public void Walk_starts_at_index_1_skipping_the_gworld_root_offset()
    {
        var spine = new List<BreadcrumbItem>
        {
            Bc("0x1000", "GWorld", offset: 0x9999),   // root offset must be ignored
            Bc("0x2000", "Step", deref: true, offset: 0x30),
        };
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", spine, useAob: false, aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x10");

        Assert.Contains("readQword(addr + 0x30)", xml);
        Assert.DoesNotContain("0x9999", xml);   // the root's offset never appears as a step
    }

    [Fact]
    public void Inline_struct_crumb_adds_without_dereferencing()
    {
        var spine = new List<BreadcrumbItem>
        {
            Bc("0x1000", "GWorld"),
            Bc("0x1010", "InlineStruct", deref: false, offset: 0x10),   // inline: add, no deref
            Bc("0x2000", "Ptr", deref: true, offset: 0x20),             // deref
        };
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", spine, useAob: false, aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x10");

        Assert.Contains("addr = addr + 0x10", xml);     // inline add
        Assert.Contains("(inline)", xml);
        Assert.Contains("readQword(addr + 0x20)", xml); // pointer deref
    }

    [Fact]
    public void Container_view_crumb_dereferences()
    {
        var spine = new List<BreadcrumbItem>
        {
            Bc("0x1000", "GWorld"),
            Bc("0x2000", "Arr", deref: false, offset: 0x40, container: true),  // container ⇒ deref
        };
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", spine, useAob: false, aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x10");

        Assert.Contains("readQword(addr + 0x40)", xml);
    }

    [Fact]
    public void Every_walk_hop_guards_against_nil_and_zero()
    {
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", SampleSpine(), useAob: false, aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x10");

        // CE readQword returns NIL (not 0) on an unreadable page, so every hop must
        // guard `addr and addr ~= 0` — a bare `if addr ~= 0` would let a nil through
        // and throw on `readQword(nil + off)`. No bare zero-only guard may remain.
        Assert.DoesNotContain("if addr ~= 0 then", xml);
        foreach (var line in xml.Split('\n')
                     .Where(l => l.Contains("addr = readQword(addr +") || l.Contains("addr = addr + 0x")))
            Assert.Contains("if addr and addr ~= 0 then", line);
    }

    [Fact]
    public void Leaf_only_registers_when_walk_is_non_null()
    {
        var xml = CeXmlExportService.GenerateGWorldWalkedSymbolXml(
            "S", SampleSpine(), useAob: false, aob: "", aobPos: 0, aobLen: 0, gworldSlotAddr: "0x10");
        Assert.Contains("if addr and addr ~= 0 then", xml);
        Assert.Contains("WARNING: null pointer mid-walk", xml);
    }

    // ── ViewModel dispatch: which case is chosen ───────────────────────

    private static LiveWalkerViewModel MakeVm(out MockPlatformService platform)
    {
        platform = new MockPlatformService(System.IO.Path.GetTempPath());
        return new LiveWalkerViewModel(new StubDumpService(), new MockLoggingService(), platform);
    }

    private static EngineState EngineWith(string gworldAob, string gworldAddr) => new()
    {
        GWorldAob = gworldAob,
        GWorldAobPos = 3,
        GWorldAobLen = 7,
        GWorldAddr = gworldAddr,
        ModuleName = "Game.exe",
        ModuleBase = "0x140000000",
    };

    private static void LoadGWorldSpine(LiveWalkerViewModel vm)
    {
        vm.Breadcrumbs.Clear();
        vm.Breadcrumbs.Add(Bc("0x1000", "GWorld"));
        vm.Breadcrumbs.Add(Bc("0x2000", "PersistentLevel", deref: true, offset: 0x30));
        vm.Breadcrumbs.Add(Bc("0x4000", "PlayerController", deref: true, offset: 0x1A0));
        vm.CurrentAddress = "0x4000";       // == last crumb ⇒ walkable
        vm.CurrentClassName = "BP_Player";
    }

    [Fact]
    public async Task Dispatch_gworld_root_with_aob_emits_aob_walk()
    {
        var vm = MakeVm(out var platform);
        LoadGWorldSpine(vm);
        vm.SetEngineState(EngineWith(gworldAob: "48 8B 1D ?? ?? ?? ??", gworldAddr: "0x150000000"));
        vm.UseAobSymbol = true;

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.Contains("AOBScanModuleUE", platform.LastClipboard);
        Assert.Contains("registerSymbol('BP_Player', addr)", platform.LastClipboard);
        Assert.DoesNotContain("define(", platform.LastClipboard);
    }

    [Fact]
    public async Task Dispatch_gworld_root_aob_unchecked_uses_hardcoded_base()
    {
        var vm = MakeVm(out var platform);
        LoadGWorldSpine(vm);
        // AOB is available, but the user left the checkbox OFF → respect it (Case B).
        vm.SetEngineState(EngineWith(gworldAob: "48 8B 1D ?? ?? ?? ??", gworldAddr: "0x150000000"));
        vm.UseAobSymbol = false;

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.DoesNotContain("AOBScanModuleUE", platform.LastClipboard);
        Assert.Contains("tonumber('150000000', 16)", platform.LastClipboard);
        Assert.Contains("registerSymbol('BP_Player', addr)", platform.LastClipboard);
    }

    [Fact]
    public async Task Dispatch_non_gworld_root_emits_legacy_hardcoded_address()
    {
        var vm = MakeVm(out var platform);
        vm.Breadcrumbs.Clear();
        vm.Breadcrumbs.Add(Bc("0x9000", "SomeActor"));   // root is NOT GWorld
        vm.CurrentAddress = "0x9000";
        vm.CurrentClassName = "BP_Other";
        vm.SetEngineState(EngineWith(gworldAob: "48 8B 1D", gworldAddr: "0x150000000"));

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.Contains("define(BP_Other,", platform.LastClipboard);
        Assert.Contains("registersymbol(BP_Other)", platform.LastClipboard);
        Assert.DoesNotContain("{$lua}", platform.LastClipboard);   // not a walk script
    }

    [Fact]
    public async Task Dispatch_negative_offset_hop_falls_back_to_hardcoded()
    {
        var vm = MakeVm(out var platform);
        vm.Breadcrumbs.Clear();
        vm.Breadcrumbs.Add(Bc("0x1000", "GWorld"));
        // A WorldLevel back-reference recovery hop carries FieldOffset -1 — not
        // forward-walkable, so the dispatch must fall back to the hardcoded address.
        vm.Breadcrumbs.Add(Bc("0x2000", "OwningWorld", deref: true, offset: -1));
        vm.CurrentAddress = "0x2000";
        vm.CurrentClassName = "BP_WP";
        vm.SetEngineState(EngineWith(gworldAob: "48 8B 1D", gworldAddr: "0x150000000"));
        vm.UseAobSymbol = true;

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.Contains("define(BP_WP,", platform.LastClipboard);
        Assert.DoesNotContain("AOBScanModuleUE", platform.LastClipboard);
    }

    [Fact]
    public async Task Dispatch_spine_not_landing_on_current_falls_back()
    {
        var vm = MakeVm(out var platform);
        LoadGWorldSpine(vm);
        vm.CurrentAddress = "0xDEAD";   // != last crumb (0x4000) ⇒ not a faithful spine
        vm.CurrentClassName = "BP_Mismatch";
        vm.SetEngineState(EngineWith(gworldAob: "48 8B 1D", gworldAddr: "0x150000000"));
        vm.UseAobSymbol = true;

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.Contains("define(BP_Mismatch,", platform.LastClipboard);
    }

    [Fact]
    public async Task Dispatch_container_leaf_class_name_is_sanitized_to_valid_symbol()
    {
        var vm = MakeVm(out var platform);
        vm.Breadcrumbs.Clear();
        vm.Breadcrumbs.Add(Bc("0x9000", "SomeActor"));   // Case C path
        vm.CurrentAddress = "0x9000";
        vm.CurrentClassName = "Map<FName, int>";          // container view class name
        vm.SetEngineState(EngineWith(gworldAob: "", gworldAddr: ""));

        await vm.GenerateCeAAScriptCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        // < > , spaces all become '_' so the XML <Description> + define() stay valid.
        Assert.DoesNotContain("Map<FName, int>", platform.LastClipboard);
        Assert.DoesNotContain("<FName", platform.LastClipboard);
        Assert.Contains("Map_FName", platform.LastClipboard);
    }
}
