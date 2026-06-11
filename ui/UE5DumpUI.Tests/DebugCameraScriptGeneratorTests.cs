using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the Debug Camera memory-record AA Script shape: a STATEFUL toggle
/// (tick = setDebugCamera(1), untick = setDebugCamera(0)), both blocks loading
/// the embedded helper. The defining difference from BakedScriptGenerator is
/// that [DISABLE] is NOT a nop here — it actively turns the camera off.
/// </summary>
public class DebugCameraScriptGeneratorTests
{
    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = DebugCameraScriptGenerator.Generate();
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void Generate_enables_on_tick_and_disables_on_untick()
    {
        var s = DebugCameraScriptGenerator.Generate();
        Assert.Contains("setDebugCamera, 1", s);   // [ENABLE]
        Assert.Contains("setDebugCamera, 0", s);   // [DISABLE]
    }

    [Fact]
    public void Disable_block_is_not_a_nop()
    {
        var s = DebugCameraScriptGenerator.Generate();
        var disableIdx = s.IndexOf("[DISABLE]", System.StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        var disableBlock = s.Substring(disableIdx);
        // The whole point: unticking the record forces the camera OFF.
        Assert.Contains("setDebugCamera, 0", disableBlock);
        Assert.DoesNotContain("-- nop", disableBlock);
    }

    [Fact]
    public void Both_blocks_load_the_embedded_helper()
    {
        var s = DebugCameraScriptGenerator.Generate();
        var helperLoads = s.Split("findTableFile('ue5_invoke_helper.lua')").Length - 1;
        Assert.Equal(2, helperLoads);   // one per block (re-declaration-safe)
    }

    [Fact]
    public void Generate_is_lf_only_for_ce_compatibility()
    {
        Assert.DoesNotContain("\r", DebugCameraScriptGenerator.Generate());
    }
}
