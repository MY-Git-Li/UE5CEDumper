using System.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the Fly (Dunste) DLL-mailbox AA Script shape: STATEFUL toggles driving
/// CMD_FLY(11) — FLY_OP_SET_ENABLED(0) for Fly, FLY_OP_SET_NOCLIP(4) for Noclip.
/// The op rides in instanceAddr (0x10), the on/off value in ufuncAddr (0x18), and
/// the command is written LAST to 0x00. Self-contained (no helper-file dependency).
/// </summary>
public class FlyScriptGeneratorTests
{
    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void Generate_enables_on_tick_and_disables_on_untick()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        Assert.Contains("writeQword(mb + 0x18, 1)", s);   // [ENABLE] value = ON
        Assert.Contains("writeQword(mb + 0x18, 0)", s);   // [DISABLE] value = OFF
    }

    [Fact]
    public void Fly_selects_set_enabled_op_zero()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        var ops = s.Split("writeQword(mb + 0x10, 0)").Length - 1;
        Assert.Equal(2, ops);   // FLY_OP_SET_ENABLED=0, one per block
    }

    [Fact]
    public void Noclip_selects_set_noclip_op_four()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Noclip);
        var ops = s.Split("writeQword(mb + 0x10, 4)").Length - 1;
        Assert.Equal(2, ops);   // FLY_OP_SET_NOCLIP=4, one per block
    }

    [Fact]
    public void Both_blocks_trigger_the_fly_mailbox_command()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        var triggers = s.Split("writeInteger(mb + 0x00, 11)").Length - 1;
        Assert.Equal(2, triggers);   // CMD_FLY=11, one per block
    }

    [Fact]
    public void Is_self_contained_no_helper_file_dependency()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        Assert.DoesNotContain("findTableFile", s);
        Assert.Contains("g_invokeMailbox", s);
    }

    [Fact]
    public void Emits_debug_preamble_for_hygiene()
    {
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled);
        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg", s);
    }

    [Fact]
    public void Generate_is_lf_only_for_ce_compatibility()
    {
        Assert.DoesNotContain("\r", FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled));
        Assert.DoesNotContain("\r", FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Noclip));
    }

    [Fact]
    public void Enable_with_preset_sets_preset_before_enabling()
    {
        // Without a preset, no SET_PRESET (op 2) call.
        Assert.DoesNotContain("writeQword(mb + 0x10, 2)",
            FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled));
        // With a preset, the ENABLE first fires FLY_OP_SET_PRESET (op 2).
        var s = FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled, 1);   // Numpad
        Assert.Contains("writeQword(mb + 0x10, 2)", s);   // FLY_OP_SET_PRESET
        Assert.Contains("writeQword(mb + 0x10, 0)", s);   // then FLY_OP_SET_ENABLED
    }

    [Fact]
    public void BuildBatchRows_returns_one_row_per_preset_plus_noclip()
    {
        var rows = FlyScriptGenerator.BuildBatchRows();
        Assert.Equal(FlyScriptGenerator.PresetNames.Length + 1, rows.Count);   // 3 presets + noclip
        Assert.All(rows, r => Assert.Equal("Fly", r.Category));
        Assert.All(rows, r => Assert.IsType<CtScriptRow>(r));
        Assert.Contains(rows, r => r.Description.Contains("WASD"));
        Assert.Contains(rows, r => r.Description.Contains("Numpad"));
        Assert.Contains(rows, r => r.Description.Contains("Arrows"));
        Assert.Contains(rows, r => r.Description.Contains("Noclip"));
    }
}
