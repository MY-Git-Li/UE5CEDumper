using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the Keep-Foreground (Grausam) memory-record AA Script shape: a STATEFUL
/// toggle driving the Mimic mailbox CMD_FOREGROUND(12) op FG_OP_SET(0) — tick
/// writes value 1 (game always foreground), untick writes value 0. The op rides
/// in instanceAddr (0x10), the value in ufuncAddr (0x18), and the command is
/// written LAST to 0x00. Self-contained (no helper-file dependency).
/// </summary>
public class ForegroundScriptGeneratorTests
{
    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = ForegroundScriptGenerator.Generate();
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void Generate_enables_on_tick_and_disables_on_untick()
    {
        var s = ForegroundScriptGenerator.Generate();
        Assert.Contains("writeQword(mb + 0x18, 1)", s);   // [ENABLE] value = ON
        Assert.Contains("writeQword(mb + 0x18, 0)", s);   // [DISABLE] value = OFF
    }

    [Fact]
    public void Both_blocks_select_the_set_op()
    {
        var s = ForegroundScriptGenerator.Generate();
        var ops = s.Split("writeQword(mb + 0x10, 0)").Length - 1;
        Assert.Equal(2, ops);   // FG_OP_SET=0, one per block
    }

    [Fact]
    public void Both_blocks_trigger_the_foreground_mailbox_command()
    {
        var s = ForegroundScriptGenerator.Generate();
        var triggers = s.Split("writeInteger(mb + 0x00, 12)").Length - 1;
        Assert.Equal(2, triggers);   // CMD_FOREGROUND=12, one per block
    }

    [Fact]
    public void Disable_block_is_not_a_nop()
    {
        var s = ForegroundScriptGenerator.Generate();
        var disableIdx = s.IndexOf("[DISABLE]", System.StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        var disableBlock = s.Substring(disableIdx);
        Assert.Contains("writeQword(mb + 0x18, 0)", disableBlock);
        Assert.DoesNotContain("-- nop", disableBlock);
    }

    [Fact]
    public void Is_self_contained_no_helper_file_dependency()
    {
        var s = ForegroundScriptGenerator.Generate();
        Assert.DoesNotContain("findTableFile", s);
        Assert.Contains("g_invokeMailbox", s);
    }

    [Fact]
    public void Generate_is_lf_only_for_ce_compatibility()
    {
        Assert.DoesNotContain("\r", ForegroundScriptGenerator.Generate());
    }
}
