using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the "Get GWorld" / "Get GameEngine" CE records: a stateful toggle that
/// resolves an address via the mailbox (CMD_QUERY_PTR=13), then publishes a CE
/// symbol. GWorld registers the symbol DIRECTLY to the &amp;GWorld slot (no buffer,
/// auto-follows level changes); GameEngine copies the UEngine* into an
/// allocateMemory buffer (snapshot) and frees it on disable. Mirrors the mailbox
/// contract in dll/src/Mimic.h (QueryPtrOp) + the CE Lua registerSymbol pattern.
/// </summary>
public class PointerQueryScriptGeneratorTests
{
    [Fact]
    public void Generate_is_lf_only()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Both_blocks_present()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
    }

    [Fact]
    public void GWorld_uses_op_0_reads_slot_and_registers_it_directly()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld);
        Assert.Contains("writeQword(mb + 0x10, 0)", s);    // op QUERY_OP_GWORLD
        Assert.Contains("writeInteger(mb + 0x00, 13)", s); // CMD_QUERY_PTR (write LAST)
        // The &GWorld slot is at paramsData[0..7] = mb + 0x328.
        Assert.Contains("local addr = readQword(mb + 0x328)", s);
        // Registered DIRECTLY to the slot address — no buffer, no dealloc.
        Assert.Contains("registerSymbol('UE_GWorld', addr)", s);
        Assert.DoesNotContain("allocateMemory", s);
        Assert.DoesNotContain("deAlloc", s);
    }

    [Fact]
    public void GameEngine_uses_op_1_buffers_the_instance_and_registers_it()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("writeQword(mb + 0x10, 1)", s);    // op QUERY_OP_GAME_ENGINE
        Assert.Contains("writeInteger(mb + 0x00, 13)", s); // CMD_QUERY_PTR
        // The UEngine* instance is at paramsData[0..7] = mb + 0x328.
        Assert.Contains("local addr = readQword(mb + 0x328)", s);
        Assert.Contains("local mem = allocateMemory(8)", s);
        Assert.Contains("writeQword(mem, addr)", s);
        Assert.Contains("registerSymbol('UE_GameEngine', mem)", s);
    }

    [Fact]
    public void GameEngine_disable_unregisters_and_frees_the_buffer()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        var disable = s.Substring(s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("getAddressSafe('UE_GameEngine')", disable);
        Assert.Contains("unregisterSymbol('UE_GameEngine')", disable);
        Assert.Contains("deAlloc(mem)", disable);
    }

    [Fact]
    public void GWorld_disable_unregisters_only_no_dealloc()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld);
        var disable = s.Substring(s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("unregisterSymbol('UE_GWorld')", disable);
        // The slot is a game address — must never be deAlloc'd.
        Assert.DoesNotContain("deAlloc", disable);
    }

    [Fact]
    public void Enable_closes_lua_engine_on_clean_success()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld);
        Assert.Contains("if DEBUG == 0 then", s);
    }

    [Fact]
    public void SymbolName_maps_targets()
    {
        Assert.Equal("UE_GWorld",
            PointerQueryScriptGenerator.SymbolName(PointerQueryScriptGenerator.Target.GWorld));
        Assert.Equal("UE_GameEngine",
            PointerQueryScriptGenerator.SymbolName(PointerQueryScriptGenerator.Target.GameEngine));
    }

    [Fact]
    public void Resolves_mailbox_symbol_with_module_fallback()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("getAddressSafe('g_invokeMailbox')", s);
        Assert.Contains("getAddressSafe('UE5Dumper.g_invokeMailbox')", s);
    }

    // --- Clipboard fallback: the AA body must be wrapped as paste-able CE XML ---
    // (a bare [ENABLE]/[DISABLE] body can't be pasted into a CE memory record).

    [Fact]
    public void WrapAaScriptXml_is_a_pasteable_cheatentry()
    {
        var script = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld);
        var xml = CheatTableBuilder.WrapAaScriptXml("Get GWorld → symbol UE_GWorld", script);

        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<CheatTable>", xml);
        Assert.Contains("<CheatEntries>", xml);
        Assert.Contains("<CheatEntry>", xml);
        Assert.Contains("<VariableType>Auto Assembler Script</VariableType>", xml);
        Assert.Contains("<AssemblerScript>", xml);
        Assert.Contains("</AssemblerScript>", xml);
    }

    [Fact]
    public void WrapAaScriptXml_escapes_the_script_body()
    {
        // The AA body contains XML-hostile chars (e.g. '>' in the "elapsed >= …"
        // timeout guard); they must be entity-escaped so the CE XML parser reads a
        // single well-formed <AssemblerScript> text node.
        var script = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        var xml = CheatTableBuilder.WrapAaScriptXml("Get GameEngine → symbol UE_GameEngine", script);

        Assert.Contains("&gt;=", xml);              // "elapsed >= N" → "elapsed &gt;= N"
        Assert.DoesNotContain("elapsed >= ", xml);  // no raw '>' survives in the body
    }
}
