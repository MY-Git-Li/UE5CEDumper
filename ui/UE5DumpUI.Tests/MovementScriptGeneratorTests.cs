using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the movement-tuning AA records (Laufen): stateful toggles that poke the
/// Mimic mailbox CMD_MOVEMENT=10 with a baked percent, where [DISABLE] sends 100
/// (the DLL reads 100% as OFF / restore base).
/// </summary>
public class MovementScriptGeneratorTests
{
    [Fact]
    public void Generate_is_lf_only()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.WalkSpeed, 200);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.Gravity, 50);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void WalkSpeed_uses_knobId_0_and_cmd_10_with_percent()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.WalkSpeed, 250);
        Assert.Contains("writeQword(mb + 0x10, 0)", s);        // knobId WALK_SPEED
        Assert.Contains("writeInteger(mb + 0x00, 10)", s);     // CMD_MOVEMENT (write LAST)
        Assert.Contains("writeDouble(mb + 0x328, 250", s);     // baked percent
    }

    [Fact]
    public void Gravity_uses_knobId_1()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.Gravity, 75);
        Assert.Contains("writeQword(mb + 0x10, 1)", s);
    }

    [Fact]
    public void Jump_uses_knobId_2()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.Jump, 400);
        Assert.Contains("writeQword(mb + 0x10, 2)", s);
        Assert.Contains("writeDouble(mb + 0x328, 400", s);     // jump HEIGHT % (DLL applies sqrt)
    }

    [Fact]
    public void Disable_block_sends_100_percent_off()
    {
        var s = MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.WalkSpeed, 300);
        // The [DISABLE] half must send 100 (OFF) regardless of the baked %.
        int disableIdx = s.IndexOf("[DISABLE]", System.StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        string disable = s.Substring(disableIdx);
        Assert.Contains("writeDouble(mb + 0x328, 100", disable);
    }

    [Fact]
    public void BuildBatchRows_returns_three_movement_rows()
    {
        var rows = MovementScriptGenerator.BuildBatchRows(200, 50, 400);
        Assert.Equal(3, rows.Count);
    }
}
