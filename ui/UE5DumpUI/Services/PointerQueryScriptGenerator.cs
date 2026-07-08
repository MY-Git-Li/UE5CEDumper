using System.Text;

namespace UE5DumpUI.Services;

/// <summary>
/// Generates a self-contained CE memory-record AA Script that asks the injected
/// UE5Dumper.dll for a global-pointer address and publishes it as a CE
/// <b>registered symbol</b> the user can reference directly — "Get GWorld" and
/// "Get GameEngine instance address".
///
/// <para>A STATEFUL toggle (like the GodMode record — NOT momentary). Both targets
/// resolve the address with one mailbox round-trip (<c>CMD_QUERY_PTR</c>), then
/// publish a symbol so <c>[UE_GWorld]+offset</c> / <c>[UE_GameEngine]+offset</c>
/// chains straight into the object. They differ in HOW the symbol is backed:</para>
/// <list type="bullet">
///   <item><b>GWorld</b> — registered DIRECTLY to the <c>&amp;GWorld</c> pointer
///   slot (a stable static/engine address). <c>[UE_GWorld]</c> dereferences the
///   slot to the CURRENT <c>UWorld*</c>, so it AUTO-FOLLOWS level transitions. No
///   buffer to free.</item>
///   <item><b>GameEngine</b> — no static slot exists (the DLL finds the live
///   <c>UEngine*</c> by walking GObjects), so the address is copied into an
///   <c>allocateMemory(8)</c> buffer and the symbol registered to that buffer
///   (a SNAPSHOT; re-tick to refresh). <c>[DISABLE]</c> frees the buffer.</item>
/// </list>
///
/// <para>SELF-CONTAINED: talks to the mailbox directly (no
/// <c>ue5_invoke_helper.lua</c>). The mailbox is REQUIRED because CE Lua's
/// <c>executeCodeEx</c> can't reliably read an export's return value on protected
/// games (returns nil) — see docs/lessons-learned.md / docs/godmode-spec.md §10.</para>
/// </summary>
public static class PointerQueryScriptGenerator
{
    // Mailbox layout — see dll/src/Mimic.h (MailboxData + QueryPtrOp).
    private const int CmdQueryPtr = CeMailboxLayout.CmdQueryPtr;

    public enum Target
    {
        GWorld,      // QUERY_OP_GWORLD      = 0
        GameEngine,  // QUERY_OP_GAME_ENGINE = 1
    }

    /// <summary>The CE symbol name a given target publishes on enable
    /// (surfaced in the UI hint / record description so the user knows what to type).</summary>
    public static string SymbolName(Target target) =>
        target == Target.GWorld ? "UE_GWorld" : "UE_GameEngine";

    /// <summary>Build the [ENABLE]/[DISABLE] AA Script body for one query.</summary>
    public static string Generate(Target target)
    {
        int op = target switch
        {
            Target.GWorld     => 0,
            Target.GameEngine => 1,
            _                 => 0,
        };
        // Short ASCII tag for comments / error messages (the script is transmitted
        // through CE / the AOBMaker JSON pipe).
        string tag = target == Target.GWorld ? "GWorld" : "GameEngine";
        string sym = SymbolName(target);
        // GWorld  -> register the symbol DIRECTLY to the returned &GWorld slot.
        // GameEngine -> copy the returned UEngine* into an allocated buffer.
        bool usesBuffer = target == Target.GameEngine;
        // The address to publish is at mailbox paramsData[0..7] = mb + 0x328 for
        // BOTH targets (GWorld: the &GWorld slot; GameEngine: the UEngine* instance).
        string addrOff = CeMailboxLayout.OffParamsData;

        var sb = new StringBuilder(3072);

        // ── [ENABLE] ──
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        Line(sb, "-- ================================================================");
        Line(sb, $"-- Get {tag} -> CE symbol '{sym}' | {CeLuaHygiene.AttributionUrl}");
        if (usesBuffer)
        {
            Line(sb, $"-- ENABLE : query UE5Dumper.dll (CMD_QUERY_PTR=13), allocate an");
            Line(sb, $"--          8-byte buffer, write the live UEngine* into it, then");
            Line(sb, $"--          registerSymbol('{sym}', buffer).  [{sym}]+off -> engine.");
            Line(sb, $"-- DISABLE: unregisterSymbol('{sym}') + deAlloc the buffer.");
            Line(sb, "-- The engine address is a SNAPSHOT -- re-tick to refresh.");
        }
        else
        {
            Line(sb, $"-- ENABLE : query UE5Dumper.dll (CMD_QUERY_PTR=13) for the &GWorld");
            Line(sb, $"--          pointer slot, then registerSymbol('{sym}', slot).");
            Line(sb, $"--          [{sym}] derefs the slot to the CURRENT UWorld, so");
            Line(sb, $"--          [{sym}]+off auto-follows level transitions.");
            Line(sb, $"-- DISABLE: unregisterSymbol('{sym}') (nothing to free -- the slot");
            Line(sb, "--          is a game address, not ours).");
        }
        Line(sb, "-- Requires the DLL injected (version.dll proxy or CE inject).");
        Line(sb, "-- ================================================================");
        Line(sb);

        Line(sb, "local mb = getAddressSafe('g_invokeMailbox')");
        Line(sb, "if not mb or mb == 0 then mb = getAddressSafe('UE5Dumper.g_invokeMailbox') end");
        Line(sb, "if not mb or mb == 0 then");
        Line(sb, $"  showMessage('[{tag}] g_invokeMailbox not found -- is UE5Dumper.dll injected?')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);

        // Mailbox round-trip. On ANY failure: message + untick + return (no register).
        Line(sb, $"if readInteger(mb + {CeMailboxLayout.OffCmd}) ~= 0 then");
        Line(sb, $"  showMessage('[{tag}] mailbox busy -- try again in a moment')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb, $"writeQword(mb + {CeMailboxLayout.OffInstanceAddr}, {op})             -- op ({tag})");
        Line(sb, $"writeInteger(mb + {CeMailboxLayout.OffStatus}, 0)             -- clear status");
        Line(sb, $"writeInteger(mb + {CeMailboxLayout.OffCmd}, {CmdQueryPtr})  -- CMD_QUERY_PTR (write LAST)");
        Line(sb, "local elapsed = 0");
        Line(sb, $"while readInteger(mb + {CeMailboxLayout.OffStatus}) ~= 1 do");
        Line(sb, "  sleep(1); elapsed = elapsed + 1");
        Line(sb, $"  if elapsed >= {CeMailboxLayout.MailboxPollTimeoutMs} then");
        Line(sb, $"    showMessage('[{tag}] mailbox timeout (DLL not responding?)')");
        Line(sb, "    if memrec then memrec.Active = false end");
        Line(sb, "    return");
        Line(sb, "  end");
        Line(sb, "end");
        Line(sb, $"local code = readInteger(mb + {CeMailboxLayout.OffResult})");
        Line(sb, $"if code ~= 0 then");
        Line(sb, $"  showMessage('[{tag}] not resolved (code=' .. code .. ') -- enter gameplay first?')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb, $"local addr = readQword(mb + {addrOff})");
        Line(sb, "if not addr or addr == 0 then");
        Line(sb, $"  showMessage('[{tag}] address is 0 -- not available yet')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);

        if (usesBuffer)
        {
            // GameEngine: buffer-backed symbol (snapshot of the UEngine*).
            Line(sb, $"-- Drop any stale '{sym}' buffer from a previous enable, then republish.");
            Line(sb, $"local old = getAddressSafe('{sym}')");
            Line(sb, $"if old and old ~= 0 then unregisterSymbol('{sym}'); deAlloc(old) end");
            Line(sb, "local mem = allocateMemory(8)");
            Line(sb, "if not mem or mem == 0 then");
            Line(sb, $"  showMessage('[{tag}] allocateMemory failed')");
            Line(sb, "  if memrec then memrec.Active = false end");
            Line(sb, "  return");
            Line(sb, "end");
            Line(sb, "writeQword(mem, addr)");
            Line(sb, $"registerSymbol('{sym}', mem)");
            Line(sb, $"dbg(string.format('[{tag}] {sym} -> 0x%X (buffer 0x%X)', addr, mem))");
        }
        else
        {
            // GWorld: register the symbol DIRECTLY to the &GWorld slot (no buffer).
            Line(sb, $"-- Clear any stale registration, then point '{sym}' at the &GWorld slot.");
            Line(sb, $"if getAddressSafe('{sym}') then unregisterSymbol('{sym}') end");
            Line(sb, $"registerSymbol('{sym}', addr)");
            Line(sb, $"dbg(string.format('[{tag}] {sym} -> &GWorld slot 0x%X', addr))");
        }
        Line(sb, $"if DEBUG == 0 then {CeLuaHygiene.CloseCall} end");
        Line(sb, "{$asm}");

        // ── [DISABLE] ──
        Line(sb, "[DISABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        if (usesBuffer)
        {
            Line(sb, $"-- Remove the '{sym}' symbol and free its buffer.");
            Line(sb, $"local mem = getAddressSafe('{sym}')");
            Line(sb, $"if mem and mem ~= 0 then unregisterSymbol('{sym}'); deAlloc(mem) end");
        }
        else
        {
            Line(sb, $"-- Remove the '{sym}' symbol (the slot is a game address, nothing to free).");
            Line(sb, $"if getAddressSafe('{sym}') then unregisterSymbol('{sym}') end");
        }
        Line(sb, $"dbg('[{tag}] {sym} unregistered')");
        Line(sb, $"if DEBUG == 0 then {CeLuaHygiene.CloseCall} end");
        Line(sb, "{$asm}");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string text = "")
    {
        sb.Append(text);
        sb.Append('\n');
    }
}
