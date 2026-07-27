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
///   <item><b>GameEngine</b> — prefers the same treatment: it asks for the
///   <c>&amp;GEngine</c> SLOT first (<c>QUERY_OP_GENGINE_SLOT</c>) and registers the
///   symbol directly to it, so <c>[UE_GameEngine]</c> auto-follows engine recreation.
///   Only when no GEngine AOB validated does it fall back to copying the live
///   <c>UEngine*</c> into an <c>allocateMemory(8)</c> buffer (a SNAPSHOT; re-tick to
///   refresh), which <c>[DISABLE]</c> then frees.</item>
/// </list>
///
/// <para>That choice is made at ENABLE time, not when this script is generated. A CE
/// record gets saved into a <c>.CT</c> and re-enabled in later sessions, where the AOB
/// may resolve even though it did not when the record was created (or vice versa), so
/// baking the decision in at generation time would make the artifact wrong later. The
/// snapshot path registers an extra <c>&lt;symbol&gt;_buf</c> marker purely so
/// <c>[DISABLE]</c> can tell the two apart — <c>deAlloc</c> must never be called on a
/// game address.</para>
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
        // Short ASCII tag for comments / error messages (the script is transmitted
        // through CE / the AOBMaker JSON pipe).
        string tag = target == Target.GWorld ? "GWorld" : "GameEngine";
        string sym = SymbolName(target);
        // Marker symbol registered ONLY on the snapshot path, so [DISABLE] can tell a
        // buffer we allocated from a game address we merely pointed at. deAlloc on a
        // game address would be a very bad day.
        string bufSym = sym + "_buf";
        bool mayFallBack = target == Target.GameEngine;

        var sb = new StringBuilder(4096);

        // ── [ENABLE] ──
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        Line(sb, "-- ================================================================");
        Line(sb, $"-- Get {tag} -> CE symbol '{sym}' | {CeLuaHygiene.AttributionUrl}");
        if (mayFallBack)
        {
            Line(sb, $"-- ENABLE : ask UE5Dumper.dll (CMD_QUERY_PTR=13) for the &GEngine SLOT");
            Line(sb, $"--          (op 2) and registerSymbol('{sym}', slot) -- [{sym}]");
            Line(sb, "--          then derefs to the CURRENT UEngine, so it auto-follows");
            Line(sb, "--          engine recreation across a restart.");
            Line(sb, "--          If no GEngine AOB validated on this game, fall back to");
            Line(sb, "--          op 1 (live UEngine* found by walking GObjects), copy it");
            Line(sb, "--          into an 8-byte buffer and register THAT -- a SNAPSHOT,");
            Line(sb, "--          so re-tick to refresh.");
            Line(sb, $"-- DISABLE: unregisterSymbol('{sym}'), and deAlloc the buffer only if");
            Line(sb, $"--          the '{bufSym}' marker says we allocated one.");
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

        // One mailbox round-trip, factored into a local so the GameEngine path can run it
        // twice (slot first, snapshot second). Returns addr, or nil + a reason string —
        // a failed op is NOT necessarily fatal here, so it must not untick by itself.
        Line(sb, "local function query(op)");
        Line(sb, "  -- Bounded wait for IDLE, not a single sample: the DLL publishes status=DONE");
        Line(sb, "  -- BEFORE clearing cmd, so a second back-to-back query can still see the");
        Line(sb, "  -- previous command for an instant and would spuriously report 'busy'.");
        Line(sb, "  local waited = 0");
        Line(sb, $"  while readInteger(mb + {CeMailboxLayout.OffCmd}) ~= 0 do");
        Line(sb, "    sleep(1); waited = waited + 1");
        Line(sb, $"    if waited >= {CeMailboxLayout.MailboxIdleWaitMs} then return nil, 'mailbox busy -- try again in a moment' end");
        Line(sb, "  end");
        Line(sb, $"  writeQword(mb + {CeMailboxLayout.OffInstanceAddr}, op)");
        Line(sb, $"  writeInteger(mb + {CeMailboxLayout.OffStatus}, 0)             -- clear status");
        Line(sb, $"  writeInteger(mb + {CeMailboxLayout.OffCmd}, {CmdQueryPtr})  -- CMD_QUERY_PTR (write LAST)");
        Line(sb, "  local elapsed = 0");
        Line(sb, $"  while readInteger(mb + {CeMailboxLayout.OffStatus}) ~= 1 do");
        Line(sb, "    sleep(1); elapsed = elapsed + 1");
        Line(sb, $"    if elapsed >= {CeMailboxLayout.MailboxPollTimeoutMs} then return nil, 'mailbox timeout (DLL not responding?)' end");
        Line(sb, "  end");
        Line(sb, $"  local code = readInteger(mb + {CeMailboxLayout.OffResult})");
        Line(sb, "  if code ~= 0 then return nil, 'not resolved (code=' .. code .. ')' end");
        Line(sb, $"  local a = readQword(mb + {CeMailboxLayout.OffParamsData})");
        Line(sb, "  if not a or a == 0 then return nil, 'address is 0 -- not available yet' end");
        Line(sb, "  return a");
        Line(sb, "end");
        Line(sb);

        if (mayFallBack)
        {
            // Clear a buffer left by a PREVIOUS enable before republishing — otherwise an
            // enable that now takes the slot path would leak the old allocation.
            Line(sb, $"local stale = getAddressSafe('{bufSym}')");
            Line(sb, $"if stale and stale ~= 0 then unregisterSymbol('{bufSym}'); deAlloc(stale) end");
        }
        Line(sb, $"if getAddressSafe('{sym}') then unregisterSymbol('{sym}') end");
        Line(sb);

        if (mayFallBack)
        {
            Line(sb, "-- Prefer the SLOT (op 2). Only a game with no validated GEngine AOB");
            Line(sb, "-- falls through to the snapshot (op 1).");
            Line(sb, "local addr, err = query(2)");
            Line(sb, "local usedSlot = addr ~= nil");
            Line(sb, "if not usedSlot then");
            Line(sb, "  dbg('[" + tag + "] &GEngine slot unavailable (' .. tostring(err) .. ') -- using a UEngine* snapshot')");
            Line(sb, "  addr, err = query(1)");
            Line(sb, "end");
            Line(sb, "if not addr then");
            Line(sb, $"  showMessage('[{tag}] ' .. tostring(err) .. ' -- enter gameplay first?')");
            Line(sb, "  if memrec then memrec.Active = false end");
            Line(sb, "  return");
            Line(sb, "end");
            Line(sb);
            Line(sb, "if usedSlot then");
            Line(sb, $"  registerSymbol('{sym}', addr)");
            Line(sb, $"  dbg(string.format('[{tag}] {sym} -> &GEngine slot 0x%X (auto-follows)', addr))");
            Line(sb, "else");
            Line(sb, "  local mem = allocateMemory(8)");
            Line(sb, "  if not mem or mem == 0 then");
            Line(sb, $"    showMessage('[{tag}] allocateMemory failed')");
            Line(sb, "    if memrec then memrec.Active = false end");
            Line(sb, "    return");
            Line(sb, "  end");
            Line(sb, "  writeQword(mem, addr)");
            Line(sb, $"  registerSymbol('{sym}', mem)");
            Line(sb, $"  registerSymbol('{bufSym}', mem)   -- marker: DISABLE must free this");
            Line(sb, $"  dbg(string.format('[{tag}] {sym} -> 0x%X (snapshot, buffer 0x%X)', addr, mem))");
            Line(sb, "end");
        }
        else
        {
            Line(sb, "local addr, err = query(0)");
            Line(sb, "if not addr then");
            Line(sb, $"  showMessage('[{tag}] ' .. tostring(err) .. ' -- enter gameplay first?')");
            Line(sb, "  if memrec then memrec.Active = false end");
            Line(sb, "  return");
            Line(sb, "end");
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
        if (mayFallBack)
        {
            Line(sb, $"-- Free the buffer ONLY when the '{bufSym}' marker says we allocated one;");
            Line(sb, "-- on the slot path the symbol points at a game address that is not ours.");
            Line(sb, $"local mem = getAddressSafe('{bufSym}')");
            Line(sb, $"if mem and mem ~= 0 then unregisterSymbol('{bufSym}'); deAlloc(mem) end");
        }
        else
        {
            Line(sb, $"-- Remove the '{sym}' symbol (the slot is a game address, nothing to free).");
        }
        Line(sb, $"if getAddressSafe('{sym}') then unregisterSymbol('{sym}') end");
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
