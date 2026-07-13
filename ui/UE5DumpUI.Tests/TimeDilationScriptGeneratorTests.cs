using System;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the time-dilation AA records (Hemmung): stateful toggles that poke the
/// Mimic mailbox CMD_TIME=15 with op SET (baked value) on [ENABLE] and op RESET on
/// [DISABLE]. instanceAddr = op, ufuncAddr = target (0 global / 1 pawn).
/// </summary>
public class TimeDilationScriptGeneratorTests
{
    [Fact]
    public void Generate_is_lf_only()
    {
        var s = TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Global, 0.5);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Global, 0.5);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void Global_enable_uses_op_set_target_0_cmd_15_and_value()
    {
        var s = TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Global, 0.5);
        int enableIdx = s.IndexOf("[ENABLE]", StringComparison.Ordinal);
        int disableIdx = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        string enable = s.Substring(enableIdx, disableIdx - enableIdx);
        Assert.Contains("writeQword(mb + 0x10, 0)", enable);     // op SET
        Assert.Contains("writeQword(mb + 0x18, 0)", enable);     // target Global
        Assert.Contains("writeDouble(mb + 0x328, 0.5)", enable); // baked value
        Assert.Contains("writeInteger(mb + 0x00, 15)", enable);  // CMD_TIME (write LAST)
    }

    [Fact]
    public void Pawn_uses_target_1()
    {
        var s = TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Pawn, 2.0);
        Assert.Contains("writeQword(mb + 0x18, 1)", s);          // target Pawn
    }

    [Fact]
    public void Disable_block_uses_op_reset_and_no_value_write()
    {
        var s = TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Global, 0.25);
        int disableIdx = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        string disable = s.Substring(disableIdx);
        Assert.Contains("writeQword(mb + 0x10, 1)", disable);    // op RESET
        Assert.Contains("writeInteger(mb + 0x00, 15)", disable); // CMD_TIME
        Assert.DoesNotContain("writeDouble(mb + 0x328", disable);// RESET carries no value
    }

    [Fact]
    public void BuildBatchRows_returns_world_and_player_rows()
    {
        var rows = TimeDilationScriptGenerator.BuildBatchRows(0.5);
        Assert.Equal(2, rows.Count);   // World + Player
    }
}
