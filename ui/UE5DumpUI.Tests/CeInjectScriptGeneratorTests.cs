using System;
using System.Linq;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the shape of the CE bootstrap record (<see cref="CeInjectScriptGenerator"/>) —
/// the [ENABLE]/[DISABLE] script pushed into the user's ALREADY-OPEN CE table so the
/// standalone UE5CEDumper.CT is no longer needed just to inject.
///
/// The invariants that matter: it polls the DLL's mailbox initState instead of
/// sleeping a fixed budget, it never uses executeCodeEx during start-up (games block
/// CreateRemoteThread then), every failure path returns BEFORE the success-close so
/// the Lua Engine window stays readable, and the emitted text is safe to hand to the
/// AOBMaker plugin (which wraps the whole body in [==[ ... ]==]).
/// </summary>
public class CeInjectScriptGeneratorTests
{
    private const string Dll = @"D:\dist\UE5Dumper.dll";

    private static string Enable(string s)
    {
        var i = s.IndexOf("[ENABLE]", StringComparison.Ordinal);
        var j = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        return s.Substring(i, j - i);
    }

    private static string Disable(string s) =>
        s.Substring(s.IndexOf("[DISABLE]", StringComparison.Ordinal));

    [Fact]
    public void Generate_emits_enable_and_disable_blocks()
    {
        var s = CeInjectScriptGenerator.Generate(Dll);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
        Assert.Contains("{$lua}", s);
    }

    [Fact]
    public void Enable_bakes_the_resolved_dll_path()
    {
        var s = CeInjectScriptGenerator.Generate(Dll);
        // Backslashes must survive as Lua escapes, not raw.
        Assert.Contains(@"D:\\dist\\UE5Dumper.dll", Enable(s));
        Assert.Contains("injectDLL(DLL_PATH)", Enable(s));
    }

    [Fact]
    public void Enable_polls_initstate_instead_of_sleeping_a_fixed_budget()
    {
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        Assert.Contains("readInteger, mb + 0x0C", e);          // MailboxData.initState
        Assert.Contains($"sleep({CeInjectScriptGenerator.PollIntervalMs})", e);
        Assert.Contains($"while waited < {CeInjectScriptGenerator.ReadyTimeoutMs} do", e);
        // The old .CT behaviour we are replacing must not reappear.
        Assert.DoesNotContain("sleep(1000)", e);
        Assert.DoesNotContain("sleep(15000)", e);
    }

    /// <summary>Drop Lua line comments so a check can target real code only —
    /// the emitted comments deliberately mention APIs the code must not use.</summary>
    private static string CodeOnly(string lua) =>
        string.Join('\n', lua.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void Enable_never_uses_executeCodeEx_in_code()
    {
        // Start-up is exactly when games block CreateRemoteThread — the readiness
        // check has to be a pure memory read. Checking CODE ONLY matters twice
        // over: the block's comments name executeCodeEx deliberately (to say why
        // it is avoided), and a real use could be `pcall(executeCodeEx, ...)`
        // rather than a direct `executeCodeEx(` call.
        var code = CodeOnly(Enable(CeInjectScriptGenerator.Generate(Dll)));
        Assert.DoesNotContain("executeCodeEx", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_resolves_the_symbol_inside_the_poll_loop()
    {
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        var loopStart = e.IndexOf("while waited <", StringComparison.Ordinal);
        var loopBody = e.Substring(loopStart);
        // CE's symbol handler may not see the just-injected module on the first try.
        Assert.Contains("pcall(getAddress, 'g_invokeMailbox')", loopBody);
        Assert.Contains($"elseif waited >= {CeInjectScriptGenerator.SymbolGraceMs} then", loopBody);
    }

    [Fact]
    public void Enable_guards_against_double_inject()
    {
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        var probeIdx = e.IndexOf("pcall(getAddress, 'UE5_Init')", StringComparison.Ordinal);
        var injectIdx = e.IndexOf("injectDLL(DLL_PATH)", StringComparison.Ordinal);
        Assert.True(probeIdx > 0, "already-loaded probe missing");
        Assert.True(probeIdx < injectIdx, "the already-loaded probe must run BEFORE injectDLL");
    }

    [Fact]
    public void Enable_requires_an_attached_process_before_injecting()
    {
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        var checkIdx = e.IndexOf("getOpenedProcessID() == 0", StringComparison.Ordinal);
        var injectIdx = e.IndexOf("injectDLL(DLL_PATH)", StringComparison.Ordinal);
        Assert.True(checkIdx > 0 && checkIdx < injectIdx);
    }

    [Fact]
    public void Every_enable_failure_path_returns_before_the_success_close()
    {
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        var closeIdx = e.IndexOf(CeLuaHygiene.CloseCall, StringComparison.Ordinal);
        Assert.True(closeIdx > 0, "success-close missing");
        // Each showMessage (an error report) must be followed by a `return` that
        // lands before the close, so a failure never auto-closes the window.
        int from = 0;
        int seen = 0;
        while (true)
        {
            var msg = e.IndexOf("showMessage(", from, StringComparison.Ordinal);
            if (msg < 0 || msg > closeIdx) break;
            seen++;
            var ret = e.IndexOf("return", msg, StringComparison.Ordinal);
            Assert.True(ret > msg && ret < closeIdx,
                $"showMessage at {msg} has no `return` before the success-close");
            from = msg + 1;
        }
        Assert.True(seen >= 4, $"expected at least 4 guarded error paths, saw {seen}");
    }

    [Fact]
    public void Success_close_is_debug_gated()
    {
        var s = CeInjectScriptGenerator.Generate(Dll);
        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains($"if DEBUG == 0 then {CeLuaHygiene.CloseCall} end", s);
    }

    [Fact]
    public void Skipped_state_is_treated_as_success_not_failure()
    {
        // Another instance owning the pipe means a pipe server IS up — proceeding
        // is correct, erroring is not.
        var e = Enable(CeInjectScriptGenerator.Generate(Dll));
        Assert.Contains("state ~= INIT_READY and state ~= INIT_SKIPPED", e);
    }

    [Fact]
    public void Disable_shuts_the_dll_down()
    {
        var d = Disable(CeInjectScriptGenerator.Generate(Dll));
        Assert.Contains("UE5_StopPipeServer", d, StringComparison.Ordinal);
        Assert.Contains("UE5_Shutdown", d, StringComparison.Ordinal);
        // executeCodeEx IS fine here: the game is running normally by now. It goes
        // through pcall, so match that form rather than a direct call.
        Assert.Contains("pcall(executeCodeEx,", CodeOnly(d), StringComparison.Ordinal);
    }

    [Fact]
    public void Disable_is_a_quiet_noop_when_nothing_was_ever_loaded()
    {
        // [ENABLE]'s early bail-outs untick the record, so CE runs [DISABLE]
        // against a DLL that never loaded. That must not report a failure.
        var d = Disable(CeInjectScriptGenerator.Generate(Dll));
        var probeIdx = d.IndexOf("pcall(getAddress, 'UE5_StopPipeServer')", StringComparison.Ordinal);
        var shoutIdx = d.IndexOf("shutdown did not complete cleanly", StringComparison.Ordinal);
        Assert.True(probeIdx > 0, "not-loaded probe missing");
        Assert.True(probeIdx < shoutIdx, "the not-loaded probe must short-circuit before the failure print");
        Assert.Contains("nothing to shut down", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Emitted_text_is_safe_for_the_aobmaker_long_bracket_wrapper()
    {
        // The CE plugin wraps the whole submitted script in [==[ ... ]==] without
        // escaping the body, so that byte sequence must not appear anywhere.
        var s = CeInjectScriptGenerator.Generate(Dll);
        Assert.DoesNotContain("]==]", s, StringComparison.Ordinal);
        // NUL check uses the CHAR overload on purpose: the string overload of
        // Contains/IndexOf is culture-sensitive, and under ICU a NUL has zero
        // collation weight, so `s.Contains("\0")` reports a match at position 0
        // of ANY string. The char overload is always ordinal.
        Assert.False(s.Contains('\0'), "a NUL cannot be escaped for luaL_dostring");
    }

    [Fact]
    public void Line_endings_are_lf_only()
    {
        Assert.DoesNotContain("\r", CeInjectScriptGenerator.Generate(Dll));
    }

    [Fact]
    public void Record_is_grouped_so_it_does_not_litter_the_user_table_root()
    {
        Assert.False(string.IsNullOrWhiteSpace(CeInjectScriptGenerator.RecordGroup));
        Assert.False(string.IsNullOrWhiteSpace(CeInjectScriptGenerator.RecordDescription));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Game's Folder\UE5Dumper.dll")]
    [InlineData(@"D:\a\b\UE5Dumper.dll")]
    public void Dll_path_is_escaped_for_a_lua_literal(string path)
    {
        var e = Enable(CeInjectScriptGenerator.Generate(path));
        // The raw path (with unescaped backslashes / quotes) must never appear.
        Assert.DoesNotContain($"'{path}'", e);
        Assert.Contains("local DLL_PATH = '", e);
    }
}
