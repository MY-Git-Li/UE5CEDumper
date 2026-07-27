using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the "Get GWorld" / "Get GameEngine" CE records: a stateful toggle that
/// resolves an address via the mailbox (CMD_QUERY_PTR=13), then publishes a CE
/// symbol. GWorld registers the symbol DIRECTLY to the &amp;GWorld slot (no buffer,
/// auto-follows level changes). GameEngine now prefers the same shape — the
/// &amp;GEngine SLOT via op 2 — and only falls back to an allocateMemory snapshot of
/// the live UEngine* (op 1) when no GEngine AOB validated. Mirrors the mailbox
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
        Assert.Contains("query(0)", s);                    // op QUERY_OP_GWORLD
        Assert.Contains("writeInteger(mb + 0x00, 13)", s); // CMD_QUERY_PTR (write LAST)
        // The &GWorld slot is at paramsData[0..7] = mb + 0x328.
        Assert.Contains("readQword(mb + 0x328)", s);
        // Registered DIRECTLY to the slot address — no buffer, no dealloc.
        Assert.Contains("registerSymbol('UE_GWorld', addr)", s);
        // GWorld can never take a buffer path, so the script must not even MENTION
        // allocation — a reader has to be able to see at a glance that this record
        // cannot free a game address.
        Assert.DoesNotContain("allocateMemory", s);
        Assert.DoesNotContain("deAlloc", s);
    }

    [Fact]
    public void GameEngine_prefers_the_slot_op_2_and_registers_it_directly()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("writeInteger(mb + 0x00, 13)", s); // CMD_QUERY_PTR
        // Slot first (QUERY_OP_GENGINE_SLOT), instance second (QUERY_OP_GAME_ENGINE).
        Assert.Contains("query(2)", s);
        Assert.Contains("query(1)", s);
        Assert.True(s.IndexOf("query(2)", System.StringComparison.Ordinal)
                  < s.IndexOf("query(1)", System.StringComparison.Ordinal),
            "the slot must be attempted BEFORE the snapshot");
        // On the slot path the symbol binds straight to the returned address.
        Assert.Contains("registerSymbol('UE_GameEngine', addr)", s);
    }

    [Fact]
    public void GameEngine_falls_back_to_a_buffered_snapshot()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("local mem = allocateMemory(8)", s);
        Assert.Contains("writeQword(mem, addr)", s);
        Assert.Contains("registerSymbol('UE_GameEngine', mem)", s);
        // The marker is what lets [DISABLE] tell "we allocated this" from "this is a
        // game address" — without it, deAlloc could be called on the slot.
        Assert.Contains("registerSymbol('UE_GameEngine_buf', mem)", s);
    }

    [Fact]
    public void GameEngine_disable_frees_only_via_the_buffer_marker()
    {
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        var disable = s.Substring(s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("unregisterSymbol('UE_GameEngine')", disable);
        Assert.Contains("deAlloc(mem)", disable);
        // The dealloc must be reached through the marker, never through the symbol
        // itself — on the slot path that symbol IS a game address.
        Assert.Contains("getAddressSafe('UE_GameEngine_buf')", disable);
        Assert.DoesNotContain("deAlloc(getAddressSafe('UE_GameEngine'))", disable);
    }

    [Fact]
    public void Query_waits_for_idle_rather_than_sampling_cmd_once()
    {
        // The DLL writes status=DONE before it clears cmd, so a second back-to-back
        // query can observe the previous command for an instant. A single sample would
        // report "busy" and silently abandon the GEngine-slot -> snapshot fallback.
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        Assert.Contains("while readInteger(mb + 0x00) ~= 0 do", s);
        Assert.Contains("waited = waited + 1", s);
        Assert.DoesNotContain("if readInteger(mb + 0x00) ~= 0 then return nil", s);
    }

    [Fact]
    public void GameEngine_enable_clears_a_stale_buffer_before_republishing()
    {
        // An enable that now takes the slot path must not orphan the allocation a
        // previous snapshot-path enable made.
        var s = PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine);
        var enable = s.Substring(0, s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("local stale = getAddressSafe('UE_GameEngine_buf')", enable);
        Assert.Contains("deAlloc(stale)", enable);
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
