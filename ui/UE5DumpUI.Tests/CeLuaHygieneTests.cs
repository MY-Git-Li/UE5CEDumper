using System;
using System.Text;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the shared CE-Lua "hygiene" convention that every generated AA Script
/// follows:
/// <list type="bullet">
///   <item>a DEBUG preamble (<c>local DEBUG = UE5_DEBUG or 0</c> + <c>dbg</c>);</item>
///   <item>diagnostics go through <c>dbg()</c> (quiet unless the flag is set);</item>
///   <item>on a clean, DEBUG-off finish the Lua Engine window auto-closes;</item>
///   <item>error paths keep <c>showMessage()</c> and do NOT auto-close.</item>
/// </list>
/// If any generator drifts from this, the whole "don't cover Cheat Engine with
/// the Lua Engine window" UX regresses -- these tests are the guardrail.
/// </summary>
public class CeLuaHygieneTests
{
    // ==================================================================
    // The shared emitter
    // ==================================================================

    [Fact]
    public void Preamble_emits_global_master_with_per_script_override()
    {
        var sb = new StringBuilder();
        CeLuaHygiene.AppendDebugPreamble(sb);
        var s = sb.ToString();

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("local function dbg(...) if DEBUG ~= 0 then print(...) end end", s);
        Assert.DoesNotContain("\r", s);   // LF-only for CE
    }

    [Fact]
    public void CloseOnSuccess_without_condition_gates_only_on_debug()
    {
        var sb = new StringBuilder();
        CeLuaHygiene.AppendCloseOnSuccess(sb);
        Assert.Equal(
            "if DEBUG == 0 then synchronize(function() getLuaEngine().Close() end) end\n",
            sb.ToString());
    }

    [Fact]
    public void CloseOnSuccess_with_condition_ands_it_before_debug()
    {
        var sb = new StringBuilder();
        CeLuaHygiene.AppendCloseOnSuccess(sb, "not hadError");
        Assert.Equal(
            "if not hadError and DEBUG == 0 then synchronize(function() getLuaEngine().Close() end) end\n",
            sb.ToString());
    }

    [Fact]
    public void CloseCall_is_the_canonical_close_expression()
    {
        Assert.Equal("synchronize(function() getLuaEngine().Close() end)", CeLuaHygiene.CloseCall);
    }

    /// <summary>
    /// The idle wait had the same defect as the status wait and outlived the fix: it
    /// counted <c>sleep(1)</c> iterations against a millisecond constant, so the "100 ms"
    /// its constant documented was really ~1.55 s. That one erred LONG, so nothing broke
    /// — which is exactly why it survived. Both halves are pinned here: a real clock, and
    /// a constant that now states the duration it actually produces.
    /// </summary>
    [Fact]
    public void IdleWait_measures_a_real_deadline_not_sleep_iterations()
    {
        var sb = new StringBuilder();
        CeLuaHygiene.AppendIdleWait(sb, "mb", "return nil, 'busy'");
        var s = sb.ToString();

        Assert.Contains("getTickCount", s);
        Assert.Contains($"_idleT0 >= {CeMailboxLayout.MailboxIdleWaitMs}", s);
        Assert.Contains($"_idleIters >= {CeMailboxLayout.MailboxIdleWaitIters}", s);
        Assert.DoesNotContain("idleWaited", s);
        Assert.DoesNotContain("\r", s);

        // The fallback count is what the deadline replaces, so the two must agree. At the
        // measured 15.47 ms per sleep(1) they are within one tick of each other; a future
        // edit to one alone would silently re-open the gap this test closed.
        double fallbackMs = CeMailboxLayout.MailboxIdleWaitIters * 15.47;
        Assert.InRange(fallbackMs, CeMailboxLayout.MailboxIdleWaitMs * 0.9,
                                   CeMailboxLayout.MailboxIdleWaitMs * 1.1);
    }

    /// <summary>The idle wait and the status wait land in the SAME Lua scope in three
    /// generators (Teleport, CoordLibrary, PointerQuery), so their locals must not
    /// collide — the second declaration would shadow the first's deadline.</summary>
    [Fact]
    public void IdleWait_and_MailboxWait_locals_do_not_collide()
    {
        var idle = new StringBuilder();
        CeLuaHygiene.AppendIdleWait(idle, "mb", "return nil, 'busy'");
        var wait = new StringBuilder();
        CeLuaHygiene.AppendMailboxWait(wait, "Tag", MailboxTimeout.ReturnReason);

        foreach (var name in new[] { "_tick", "_t0", "_iters", "_st", "_over", "_msg" })
            Assert.DoesNotContain($"local {name}", idle.ToString());
        foreach (var name in new[] { "_idleTick", "_idleT0", "_idleIters", "_idleOver" })
            Assert.DoesNotContain($"local {name}", wait.ToString());
    }

    /// <summary>
    /// R2 — there is ONE Lua escape table. Three copies existed, one of them (Invoke's)
    /// silently weaker: it handled backslash / quote / newline only, so CR, TAB and — the
    /// one that matters — a closing long bracket passed through verbatim. AOBMaker wraps
    /// the WHOLE script in <c>[==[ … ]==]</c>, so that byte sequence must not appear
    /// anywhere in the emitted text. The public names remain as forwarders; this pins
    /// that they really do forward.
    /// </summary>
    [Theory]
    [InlineData(@"plain")]
    [InlineData(@"it's")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\there")]
    [InlineData(@"back\slash")]
    [InlineData("close]]bracket")]
    [InlineData("close]==]bracket")]
    [InlineData("中文 with 標點，")]
    public void All_lua_escapers_agree_with_the_shared_one(string input)
    {
        var canonical = CeLuaHygiene.EscapeLuaString(input);
        Assert.Equal(canonical, BakedScriptGenerator.EscapeLua(input));
        Assert.Equal(canonical, FreezeScriptGenerator.EscapeLua(input));
    }

    /// <summary>The property that made the divergence dangerous: no escaped output may
    /// contain a closing long bracket of any level.</summary>
    [Theory]
    [InlineData("]]")]
    [InlineData("]=]")]
    [InlineData("]==]")]
    [InlineData("a]]b]==]c")]
    public void Escaped_output_never_contains_a_closing_long_bracket(string input)
    {
        var s = CeLuaHygiene.EscapeLuaString(input);
        Assert.DoesNotContain("]]", s, StringComparison.Ordinal);
        Assert.DoesNotContain("]=]", s, StringComparison.Ordinal);
        Assert.DoesNotContain("]==]", s, StringComparison.Ordinal);
    }

    // ==================================================================
    // Every self-contained generator carries the convention
    // ==================================================================

    [Fact]
    public void GodMode_gates_state_print_and_closes_on_success()
    {
        var s = ProtectionScriptGenerator.Generate();

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[GodMode]", s);                    // diagnostic gated
        Assert.DoesNotContain("print('[GodMode]", s);           // no bare diagnostic print
        Assert.Contains(CeLuaHygiene.CloseCall, s);             // closes on success
        Assert.Contains("elseif DEBUG == 0 then", s);           // ...only when not an error
        Assert.Contains("showMessage('[GodMode]", s);           // errors still surface
    }

    [Fact]
    public void DebugCamera_gates_state_print_and_closes_on_success()
    {
        var s = DebugCameraScriptGenerator.Generate();

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[DebugCamera]", s);
        Assert.DoesNotContain("print('[DebugCamera]", s);
        Assert.Contains(CeLuaHygiene.CloseCall, s);
        Assert.Contains("elseif DEBUG == 0 then", s);
        Assert.Contains("showMessage('[DebugCamera]", s);
    }

    [Theory]
    [InlineData(MovementScriptGenerator.Knob.WalkSpeed)]
    [InlineData(MovementScriptGenerator.Knob.Gravity)]
    [InlineData(MovementScriptGenerator.Knob.Jump)]
    public void Movement_knob_gates_state_print_and_closes_on_success(
        MovementScriptGenerator.Knob knob)
    {
        var s = MovementScriptGenerator.Generate(knob, 150.0);

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[Movement]", s);
        Assert.DoesNotContain("print('[Movement]", s);
        Assert.Contains(CeLuaHygiene.CloseCall, s);
        // [DISABLE] also closes on a clean untick.
        Assert.Contains("if DEBUG == 0 then synchronize(function() getLuaEngine().Close() end) end", s);
        // ...but a [DISABLE] mailbox timeout must RETURN (error path), never fall
        // through 'break' into the success-close. Both blocks use 'return' now.
        Assert.DoesNotContain("then break end", s);
        Assert.DoesNotContain("\r", s);
    }

    /// <summary>
    /// B15 — the "a timeout is an error" rule, checked over EVERY generator instead of
    /// Movement alone. Movement-only is how Teleport kept two bare
    /// <c>if elapsed >= … then break end</c> sites: one fell straight into the
    /// auto-close, so the Lua Engine window shut on the single outcome the user needed
    /// to read, and the other generator had no <c>hadError</c> at all.
    ///
    /// <para>The assertion is deliberately about the EMITTED TEXT, not about intent: a
    /// bare <c>break</c> out of a mailbox wait is the exact shape CLAUDE.md forbids, and
    /// it is greppable. Note a bare <c>return</c> is not the fix everywhere — in a
    /// momentary [ENABLE] it would strand the record ticked — so the rule is "do not
    /// break silently", satisfied by setting <c>hadError</c> first.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryGeneratedScript))]
    public void No_generator_breaks_a_mailbox_wait_silently(string name, string script)
    {
        Assert.False(script.Contains("then break end", StringComparison.Ordinal),
            $"{name}: a mailbox wait ends with a bare 'then break end'. A timeout is an " +
            "error — set hadError (and say so) before breaking, or the auto-close hides it.");

        // Anything that CAN close on success must gate that close on hadError, since the
        // only reason to track hadError is to make the close unreachable on failure.
        if (script.Contains("hadError", StringComparison.Ordinal))
        {
            Assert.True(
                script.Contains("not hadError", StringComparison.Ordinal),
                $"{name}: declares hadError but never gates anything on it.");
        }
    }

    public static TheoryData<string, string> EveryGeneratedScript()
    {
        var data = new TheoryData<string, string>();
        foreach (TeleportScriptGenerator.Action a in Enum.GetValues<TeleportScriptGenerator.Action>())
            data.Add($"Teleport.{a}", TeleportScriptGenerator.Generate(a));
        foreach (var k in Enum.GetValues<MovementScriptGenerator.Knob>())
            data.Add($"Movement.{k}", MovementScriptGenerator.Generate(k, 150.0));
        data.Add("Movement.GravityDirection", MovementScriptGenerator.GenerateGravityDirection(0, 0, -1));
        foreach (var t in Enum.GetValues<TimeDilationScriptGenerator.Target>())
            data.Add($"TimeDilation.{t}", TimeDilationScriptGenerator.Generate(t, 0.5));
        foreach (var f in Enum.GetValues<FlyScriptGenerator.FlyToggle>())
            data.Add($"Fly.{f}", FlyScriptGenerator.Generate(f));
        data.Add("Protection", ProtectionScriptGenerator.Generate());
        data.Add("SeeThrough", SeeThroughScriptGenerator.Generate());
        data.Add("Foreground", ForegroundScriptGenerator.Generate());
        data.Add("DebugCamera", DebugCameraScriptGenerator.Generate());
        return data;
    }

    [Fact]
    public void Movement_gravity_direction_gates_state_print_and_closes()
    {
        var s = MovementScriptGenerator.GenerateGravityDirection(0, 0, -1);

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[Movement] Gravity Direction", s);
        Assert.DoesNotContain("print('[Movement]", s);
        Assert.Contains(CeLuaHygiene.CloseCall, s);
    }

    [Fact]
    public void Teleport_momentary_gates_code_print_and_closes_when_no_error()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 0);

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[Teleport]", s);
        Assert.DoesNotContain("print('[Teleport]", s);
        // Momentary: closes inside the deferred self-untick, gated on no error.
        Assert.Contains("local hadError = false", s);
        Assert.Contains("if DEBUG == 0 and not hadError then " + CeLuaHygiene.CloseCall + " end", s);
        // The marker-on-another-map failure sets hadError (keeps window open).
        Assert.Contains("hadError = true", s);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Teleport_clear_all_gates_print_and_closes()
    {
        var s = TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.ClearAll);

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg('[Teleport] all markers cleared')", s);
        // B15: the close was UNCONDITIONAL here — this generator had no hadError at all,
        // so a mailbox timeout while clearing markers shut the window on its own error
        // message. The success line is gated too: "all markers cleared" must not be
        // printed by a run where one of the three slots timed out.
        Assert.Contains("if DEBUG == 0 and not hadError then " + CeLuaHygiene.CloseCall + " end", s);
        Assert.Contains("if not hadError then dbg('[Teleport] all markers cleared') end", s);
        Assert.DoesNotContain("then break end", s);
    }

    [Fact]
    public void Every_generator_carries_the_project_url()
    {
        const string url = "https://github.com/bbfox0703/UE5CEDumper";
        Assert.Equal(url, CeLuaHygiene.AttributionUrl);
        Assert.Contains(url, ProtectionScriptGenerator.Generate());
        Assert.Contains(url, DebugCameraScriptGenerator.Generate());
        Assert.Contains(url, MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.WalkSpeed, 150.0));
        Assert.Contains(url, TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 0));
    }

    [Fact]
    public void Baked_carries_preamble_and_debug_gated_close()
    {
        var s = BakedScriptGenerator.Generate(
            "PlayerCharacter", "AddMoney", 5,
            new[] { new BakedParamValue("Amount", "IntProperty", 4, 0, "1000") });

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        // Silent-on-success close is now gated on DEBUG too.
        Assert.Contains("if ok and DEBUG == 0 then", s);
        Assert.Contains(CeLuaHygiene.CloseCall, s);
    }

    [Fact]
    public void Freeze_gates_started_stopped_prints_and_closes_both_blocks()
    {
        var p = new FreezeScriptParams
        {
            ClassName      = "BP_Teammate_C",
            PropertyName   = "CurrentHealth",
            PropertyOffset = 0x4F8,
            UeTypeName     = "FloatProperty",
            ValueLiteral   = "9999.0",
        };
        var s = FreezeScriptGenerator.Generate(p);

        Assert.Contains("local DEBUG = UE5_DEBUG or 0", s);
        Assert.Contains("dbg(string.format('[Freeze] Started:", s);
        Assert.Contains("dbg('[Freeze] Stopped:", s);
        Assert.DoesNotContain("print(string.format('[Freeze] Started:", s);
        Assert.DoesNotContain("print('[Freeze] Stopped:", s);
        // Both ENABLE (start) and DISABLE (stop) close on a clean, DEBUG-off run.
        var closes = s.Split(CeLuaHygiene.CloseCall).Length - 1;
        Assert.Equal(2, closes);
        // Real errors still surface via showMessage (and skip the close).
        Assert.Contains("showMessage('[Freeze]", s);
    }
}
