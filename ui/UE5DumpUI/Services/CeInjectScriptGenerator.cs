using System.Text;

namespace UE5DumpUI.Services;

/// <summary>
/// Generates the bootstrap AA Script that injects <c>UE5Dumper.dll</c> and waits
/// for it to come up — the same job as the <c>[ENABLE]</c> block of the
/// standalone <c>scripts/UE5CEDumper.CT</c>, but as a memory record that can be
/// pushed into the table the user ALREADY has open (via the AOBMaker plugin's
/// <c>CreateAAScript</c>, or pasted as CE record XML).
///
/// <para><b>Why this exists.</b> Cheat Engine holds one table at a time, so the
/// standalone <c>.CT</c> forces a two-stage load: open ours to inject, then open
/// the game's own table — at which point the injection entry is gone. Pushing the
/// same logic into the user's table removes that entirely. The standalone
/// <c>.CT</c> stays shipped and supported as the developer / no-AOBMaker path.</para>
///
/// <para><b>Readiness, not a fixed sleep.</b> <c>[ENABLE]</c> polls the DLL's
/// <c>initState</c> in the Mimic mailbox (<see cref="CeMailboxLayout.OffInitState"/>)
/// every <see cref="CeReadinessLua.PollIntervalMs"/> ms. That is a pure memory read through the
/// exported <c>g_invokeMailbox</c> symbol — deliberately NOT <c>executeCodeEx</c>,
/// which needs <c>CreateRemoteThread</c> and is exactly what games block during
/// start-up. A timeout is an ERROR (the window stays open), never a silent
/// "probably fine".</para>
///
/// <para><b>Symbol resolution runs inside the poll loop</b>, not once up-front:
/// CE's symbol handler may not have picked up the just-injected module yet, and a
/// single failed <c>getAddress</c> would otherwise abort a perfectly healthy
/// start-up.</para>
///
/// <para><c>[DISABLE]</c> shuts the DLL down via <c>executeCodeEx</c>, which is
/// safe there: by then the game is running normally, so remote threads work.</para>
/// </summary>
public static class CeInjectScriptGenerator
{
    /// <summary>Description used for the CE address-list record.</summary>
    public const string RecordDescription = "UE5CEDumper: Inject DLL + Start Pipe Server";

    /// <summary>Group folder the record is nested under in CE's address list, so a
    /// pushed bootstrap doesn't litter the user's own table root.</summary>
    public const string RecordGroup = "UE5CEDumper (DLL)";

    /// <summary>
    /// Build the [ENABLE]/[DISABLE] memory-record script.
    /// </summary>
    /// <param name="dllPath">Absolute path to <c>UE5Dumper.dll</c>. Baked into the
    /// script, so — unlike the standalone <c>.CT</c> — no directory search is
    /// needed at run time: the UI already knows where its own DLL lives.</param>
    public static string Generate(string dllPath)
    {
        var sb = new StringBuilder(4096);
        EmitEnable(sb, dllPath);
        EmitDisable(sb);
        return sb.ToString();
    }

    private static void EmitEnable(StringBuilder sb, string dllPath)
    {
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        Line(sb, "-- ================================================================");
        Line(sb, $"-- Inject UE5Dumper.dll + start the pipe server -- {CeLuaHygiene.Attribution}");
        Line(sb, "-- Tick to inject; untick to shut the DLL down again.");
        Line(sb, "-- Pushed into YOUR table, so you do not have to open the standalone");
        Line(sb, "-- UE5CEDumper.CT first (Cheat Engine holds one table at a time).");
        Line(sb, "-- After it turns green: launch UE5DumpUI.exe and click Connect.");
        Line(sb, "-- ================================================================");
        Line(sb);

        // ── 0. CE must be attached to a process ──
        Line(sb, "if getOpenedProcessID() == 0 then");
        Line(sb, "  showMessage('[UE5CEDumper] No game process is attached.\\n\\n' ..");
        Line(sb, "    'Attach Cheat Engine to the running game first (File > Open Process).')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);

        // ── 0.5. Already loaded? Re-injecting would double-map and fight over the pipe ──
        Line(sb, "-- Already present (proxy DLL deployed, or a previous inject)? Injecting");
        Line(sb, "-- again would double-map us and fight over the pipe.");
        Line(sb, "local okGet, probe = pcall(getAddress, 'UE5_Init')");
        Line(sb, "if okGet and probe and probe ~= 0 then");
        Line(sb, "  dbg('[UE5CEDumper] already loaded -- skipping inject')");
        Line(sb, "  showMessage('[UE5CEDumper] Already loaded in this process.\\n\\n' ..");
        Line(sb, "    'No injection needed -- just launch UE5DumpUI.exe and click Connect.')");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);

        // ── 1. Inject ──
        Line(sb, $"local DLL_PATH = '{CeLuaHygiene.EscapeLuaString(dllPath)}'");
        Line(sb, "dbg('[UE5CEDumper] injecting ' .. DLL_PATH)");
        Line(sb, "if not injectDLL(DLL_PATH) then");
        Line(sb, "  showMessage('[UE5CEDumper] injectDLL failed.\\n\\n' ..");
        Line(sb, "    'Possible causes:\\n' ..");
        Line(sb, "    '  1. The DLL was moved -- expected at:\\n     ' .. DLL_PATH .. '\\n' ..");
        Line(sb, "    '  2. Anti-cheat is blocking injection\\n' ..");
        Line(sb, "    '  3. Cheat Engine needs to run as administrator')");
        Line(sb, "  if memrec then memrec.Active = false end");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);

        // ── 2. Poll for readiness (shared emitter — see CeReadinessLua) ──
        CeReadinessLua.AppendPollLoop(sb);
        Line(sb);

        // ── 3. Report. Every failure path returns BEFORE the success-close. ──
        Line(sb, "if mb == nil then");
        Line(sb, $"  showMessage({CeReadinessLua.SymbolNeverAppearedMessage})");
        Line(sb, "  return");
        Line(sb, "elseif state == INIT_FAILED then");
        Line(sb, $"  showMessage({CeReadinessLua.PipeFailedMessage})");
        Line(sb, "  return");
        Line(sb, "elseif state ~= INIT_READY and state ~= INIT_SKIPPED then");
        Line(sb, $"  showMessage({CeReadinessLua.TimedOutMessage})");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);
        Line(sb, "if state == INIT_SKIPPED then");
        // SKIPPED is not an error: a pipe server IS up, owned by another instance.
        Line(sb, "  dbg('[UE5CEDumper] another instance already owns the pipe -- proceeding')");
        Line(sb, "else");
        Line(sb, "  dbg(string.format('[UE5CEDumper] ready in %.1f sec', waited / 1000))");
        Line(sb, "end");
        Line(sb, "dbg('[UE5CEDumper] pipe: \\\\\\\\.\\\\pipe\\\\UE5DumpBfx -- launch UE5DumpUI.exe and click Connect')");
        CeLuaHygiene.AppendCloseOnSuccess(sb);
        Line(sb, "{$asm}");
    }

    private static void EmitDisable(StringBuilder sb)
    {
        Line(sb, "[DISABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        CeLuaHygiene.AppendDebugPreamble(sb);
        Line(sb, "-- Stop the pipe server and tear the DLL down. executeCodeEx is fine");
        Line(sb, "-- here (unlike during start-up): by now the game is running normally,");
        Line(sb, "-- so CreateRemoteThread works.");
        Line(sb);
        // [ENABLE]'s early bail-outs set memrec.Active = false, which makes CE run
        // THIS block against a DLL that was never loaded. That is a no-op, not a
        // failure — don't shout about it.
        Line(sb, "-- [ENABLE]'s early bail-outs untick the record, which makes CE run this");
        Line(sb, "-- block even though nothing was ever injected. Detect that and stay quiet:");
        Line(sb, "-- 'nothing to shut down' is not a failure.");
        Line(sb, "local okProbe, probe = pcall(getAddress, 'UE5_StopPipeServer')");
        Line(sb, "if not (okProbe and probe and probe ~= 0) then");
        Line(sb, "  dbg('[UE5CEDumper] nothing loaded -- nothing to shut down')");
        CeLuaHygiene.AppendCloseOnSuccess(sb, indent: "  ");
        Line(sb, "  return");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local function callDLL(name)");
        Line(sb, "  local okGet, fn = pcall(getAddress, name)");
        Line(sb, "  if not (okGet and fn and fn ~= 0) then");
        Line(sb, "    dbg('[UE5CEDumper] export not found: ' .. name)");
        Line(sb, "    return false");
        Line(sb, "  end");
        Line(sb, "  return (pcall(executeCodeEx, 0, fn))");
        Line(sb, "end");
        Line(sb);
        Line(sb, "local a = callDLL('UE5_StopPipeServer')");
        Line(sb, "local b = callDLL('UE5_Shutdown')");
        Line(sb, "dbg('[UE5CEDumper] shutdown: stopPipe=' .. tostring(a) .. ' shutdown=' .. tostring(b))");
        // The DLL stays mapped: FreeLibrary on an injected DLL mid-game isn't worth
        // the risk. Re-ticking hits [ENABLE]'s already-loaded guard, which is correct
        // — UE5_AutoStart is idempotent and resets initState on the way through.
        Line(sb, "if not (a and b) then");
        Line(sb, "  print('[UE5CEDumper] shutdown did not complete cleanly -- check the DLL log.')");
        Line(sb, "else");
        CeLuaHygiene.AppendCloseOnSuccess(sb, indent: "  ");
        Line(sb, "end");
        Line(sb, "{$asm}");
    }

    /// <summary>Append a line with LF-only ending (no CR) for CE compatibility.</summary>
    private static void Line(StringBuilder sb, string text = "")
    {
        sb.Append(text);
        sb.Append('\n');
    }
}
