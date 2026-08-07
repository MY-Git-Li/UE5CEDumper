using System;
using System.Collections.Generic;
using System.Linq;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// One rule, asserted once for every generator that talks to the Mimic mailbox —
/// deliberately NOT six near-identical per-generator tests, because six copies of the
/// rule is exactly how the defect got in.
///
/// <para><b>The rule.</b> An <c>[ENABLE]</c> block that returns without applying anything
/// MUST untick the record. Cheat Engine leaves the row ticked otherwise, so the user is
/// told a cheat is active when nothing was set. Every generator got this right on the
/// "mailbox not found" path and every one got it wrong on the "timeout" path and on the
/// "the DLL answered with an error" path.</para>
///
/// <para>Two more properties are pinned here because they were wrong in all seven
/// hand-rolled copies of the wait loop:</para>
/// <list type="bullet">
/// <item>the timeout must be a REAL deadline. The loop used to count <c>sleep(1)</c>
/// iterations and bail at 10000, which is 10 s only if <c>sleep(1)</c> sleeps 1 ms —
/// measured in CE's Lua Engine on 2026-08-06 it sleeps <b>15.47 ms</b>, so the real
/// timeout was ~155 s of frozen Lua Engine.</item>
/// <item>the message must READ the status rather than guess. "(DLL not responding?)" was
/// a guess; a timeout with <c>status == 0</c> means the DLL never saw the command at all
/// (measured on ES2 with the DLL's poller demonstrably alive at 1 ms).</item>
/// </list>
/// </summary>
public class CeMailboxBailoutTests
{
    /// <summary>Every mailbox-driven ENABLE/DISABLE script, by name so a failure says which.</summary>
    public static IEnumerable<object[]> MailboxScripts() => new List<object[]>
    {
        new object[] { "Movement.WalkSpeed",  MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.WalkSpeed, 150) },
        new object[] { "Movement.Gravity",    MovementScriptGenerator.Generate(MovementScriptGenerator.Knob.Gravity, 50) },
        new object[] { "Movement.GravityDir", MovementScriptGenerator.GenerateGravityDirection(0, 0, -1) },
        new object[] { "Protection",          ProtectionScriptGenerator.Generate() },
        new object[] { "SeeThrough",          SeeThroughScriptGenerator.Generate() },
        new object[] { "Foreground",          ForegroundScriptGenerator.Generate() },
        new object[] { "DebugCamera",         DebugCameraScriptGenerator.Generate() },
        new object[] { "Fly.Enabled",         FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Enabled, 0) },
        new object[] { "Fly.Noclip",          FlyScriptGenerator.Generate(FlyScriptGenerator.FlyToggle.Noclip) },
        new object[] { "TimeDilation.Global", TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Global, 0.5) },
        new object[] { "TimeDilation.Pawn",   TimeDilationScriptGenerator.Generate(TimeDilationScriptGenerator.Target.Pawn, 2.0) },
    };

    /// <summary>The [ENABLE] half only — [DISABLE] has no cheat to leave falsely ticked.</summary>
    private static string EnableBlock(string script)
    {
        int a = script.IndexOf("[ENABLE]", StringComparison.Ordinal);
        Assert.True(a >= 0, "no [ENABLE] block");
        int b = script.IndexOf("[DISABLE]", a, StringComparison.Ordinal);
        return b < 0 ? script[a..] : script[a..b];
    }

    // ── The rule ────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void EveryEnableBailout_UnticksTheRecord(string name, string script)
    {
        var lines = EnableBlock(script).Split('\n');

        // Counting messages does NOT work — the shared timeout branch has three
        // alternative messages (idle / processing / unexpected) sharing one untick, so a
        // count says "3 bail-outs, 1 untick" and is wrong. Scan structurally instead.
        bool sawBailout = false;

        for (int i = 0; i < lines.Length; i++)
        {
            // (a) Every error message must reach an untick before control leaves its
            // branch. The window covers an if/elseif/else trio plus its `end`.
            if (lines[i].Contains("showMessage(", StringComparison.Ordinal))
            {
                sawBailout = true;
                Assert.True(Within(lines, i + 1, 8, "memrec.Active = false"),
                    $"{name}: line {i + 1} reports a failure but no untick follows it — " +
                    $"the CE row stays ticked with nothing applied:\n  {lines[i].Trim()}");
            }

            // (b) Every early return must already have unticked. The syntaxcheck guard is
            // not a bail-out — it runs before anything is attempted.
            string t = lines[i].Trim();
            if (t == "return" && !lines[i].Contains("syntaxcheck", StringComparison.Ordinal))
            {
                Assert.True(Back(lines, i - 1, 4, "memrec.Active = false"),
                    $"{name}: the return on line {i + 1} leaves the block without unticking");
            }
        }

        Assert.True(sawBailout,
            $"{name}: no ENABLE bail-out at all — the harness is not reaching the code it claims to check");
    }

    private static bool Within(string[] lines, int from, int span, string needle)
    {
        for (int i = from; i < Math.Min(lines.Length, from + span); i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool Back(string[] lines, int from, int span, string needle)
    {
        for (int i = from; i >= Math.Max(0, from - span); i--)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return true;
        return false;
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheOldGuessingTimeoutMessage_IsGone(string name, string script)
    {
        // It blamed the DLL for the far more common "the DLL never saw it" case and sent
        // at least one real user to inspect a healthy DLL.
        Assert.False(script.Contains("(DLL not responding?)", StringComparison.Ordinal),
            $"{name}: still emits the guess that blamed a healthy DLL");
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheTimeoutReadsTheStatus_InsteadOfGuessing(string name, string script)
    {
        string enable = EnableBlock(script);
        // status 0 = never picked up; 0xFF = picked up and wedged. Both must be named,
        // because they send the user to completely different places.
        Assert.True(enable.Contains("never saw this command", StringComparison.Ordinal),
            $"{name}: does not name the status-0 case (the DLL never picked it up)");
        Assert.True(enable.Contains("never finished it", StringComparison.Ordinal),
            $"{name}: does not name the status-PROCESSING case (the DLL wedged)");
        Assert.Contains($"_st == {CeMailboxLayout.StatusProcessing}", enable, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheTimeoutIsARealDeadline_NotAnIterationCount(string name, string script)
    {
        // getTickCount() was probed in CE's Lua Engine on 2026-08-06 and returns ms.
        Assert.True(script.Contains("getTickCount", StringComparison.Ordinal),
            $"{name}: timeout is not measured against a real clock");
        Assert.Contains($"_t0 >= {CeMailboxLayout.MailboxPollTimeoutMs}", script, StringComparison.Ordinal);

        // The pre-fix shape must not survive anywhere: `elapsed` counted sleep(1) calls
        // and compared them to a millisecond constant.
        Assert.False(script.Contains("elapsed >= ", StringComparison.Ordinal),
            $"{name}: the pre-fix iteration counter survived");
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheAutoCloseStaysUnreachableOnEveryBailout(string name, string script)
    {
        // "A timeout is an error, so return (not break)" — the close must never be
        // reachable from a bail-out. Proxy: the wait loop bails with `return`.
        string enable = EnableBlock(script);
        int loop = enable.IndexOf("while _st ~=", StringComparison.Ordinal);
        Assert.True(loop >= 0, $"{name}: no shared wait loop — did this generator stop using CeLuaHygiene?");
        int close = enable.IndexOf(CeLuaHygiene.CloseCall, StringComparison.Ordinal);
        if (close >= 0)
            Assert.True(close > loop, $"{name}: the success-close is emitted before the wait loop");
        Assert.DoesNotContain("then break end", enable, StringComparison.Ordinal);
    }

    // ── The contract check ──────────────────────────────────────────────────
    //
    // Versioned on the CONTRACT (mailbox offsets / Cmd / per-command ops / status
    // meanings), never on the build number — a .CT saved months ago has to keep working
    // against a newer DLL that changed nothing it depends on. tools/check_mailbox_contract.py
    // is what stops the version going stale; these pin the SCRIPT half.

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void EveryScriptChecksTheContractBeforeWritingAnything(string name, string script)
    {
        string enable = EnableBlock(script);

        int check = enable.IndexOf(CeMailboxLayout.ContractSymbol, StringComparison.Ordinal);
        Assert.True(check >= 0, $"{name}: never reads the contract symbol");

        // Ordering is the whole point. If the layout moved, a write lands on whatever now
        // occupies those offsets — so the check has to come before the FIRST write, not
        // merely somewhere in the block.
        //
        // Matched on the actual write CALLS, not on the substring "write": these scripts
        // carry header comments that use the word (e.g. "cmd (write LAST to trigger)"),
        // and the first version of this test tripped on those instead of on real writes.
        int w = new[] { "writeQword(", "writeInteger(", "writeDouble(", "writeBytes(", "writeByte(" }
            .Select(fn => enable.IndexOf(fn, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        Assert.True(w < 0 || check < w,
            $"{name}: writes to the mailbox at offset {w} before checking the contract at {check}");
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheContractCheckNamesBothFailureDirections(string name, string script)
    {
        string enable = EnableBlock(script);
        // The two directions need OPPOSITE advice, and today the second one is silent
        // corruption: a new .CT against an old DLL just writes nonsense.
        Assert.True(enable.Contains("too old for the DLL", StringComparison.Ordinal),
            $"{name}: does not tell the user to regenerate a too-old script");
        Assert.True(enable.Contains("DLL is older than this script", StringComparison.Ordinal),
            $"{name}: does not tell the user to update a too-old DLL");
        // And the stale-symbol case, which is a measured failure mode rather than a
        // theoretical one (2026-08-06: CE held a mailbox address the DLL no longer owned).
        Assert.Contains("stale address", enable, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void AFailedContractCheckUnticksTheRecord(string name, string script)
    {
        // Even for momentary scripts, whose deferred untick timer does not exist yet at
        // this point in the block — a bare return there would strand the row ticked.
        string enable = EnableBlock(script);
        int check = enable.IndexOf(CeMailboxLayout.ContractSymbol, StringComparison.Ordinal);
        int untick = enable.IndexOf("memrec.Active = false", check, StringComparison.Ordinal);
        Assert.True(untick > check, $"{name}: a failed contract check leaves the record ticked");
    }

    [Fact]
    public void TheBakedContractVersionMatchesTheDll()
    {
        // The C# constant is what actually gets emitted, so it — not the C++ one — is what
        // a script claims. tools/check_mailbox_contract.py enforces the other half of this
        // (that the DLL agrees); here we only pin that the emitted number IS the constant.
        string script = ProtectionScriptGenerator.Generate();
        Assert.Contains($"local _want = {CeMailboxLayout.ContractVersion}", script, StringComparison.Ordinal);
    }

    // ── The OTHER script shape: momentary actions ───────────────────────────
    //
    // Teleport's rows fire once and self-untick from a deferred timer that also
    // suppresses the success-close. Applying the toggles' fix here would BREAK it: an
    // early `return` would skip that timer and strand the record ticked — which is
    // exactly what B15's comment in the generator warns about. So the shared emitter has
    // a second mode, and these tests pin the difference rather than assuming it.

    public static IEnumerable<object[]> MomentaryScripts() => new List<object[]>
    {
        new object[] { "Teleport.Save",   TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Save, 0) },
        new object[] { "Teleport.Recall", TeleportScriptGenerator.Generate(TeleportScriptGenerator.Action.Recall, 1) },
    };

    [Theory]
    [MemberData(nameof(MomentaryScripts))]
    public void AMomentaryTimeout_FlagsAndBreaks_SoTheDeferredUntickStillRuns(string name, string script)
    {
        string enable = EnableBlock(script);

        int loop = enable.IndexOf("while _st ~=", StringComparison.Ordinal);
        Assert.True(loop >= 0, $"{name}: not using the shared wait loop");

        // Flag + break, NOT untick + return.
        int bail = enable.IndexOf("hadError = true", loop, StringComparison.Ordinal);
        Assert.True(bail > loop, $"{name}: the timeout does not set hadError");
        Assert.True(enable.IndexOf("break", bail, StringComparison.Ordinal) > bail,
            $"{name}: the timeout does not break out of the wait");

        // The deferred timer is what unticks; it must still be reached, and it must
        // suppress the auto-close when hadError.
        Assert.Contains("if memrec then memrec.Active = false end", enable, StringComparison.Ordinal);
        Assert.Contains($"if DEBUG == 0 and not hadError then {CeLuaHygiene.CloseCall} end",
                        enable, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MomentaryScripts))]
    public void MomentaryScriptsGotTheSameTimeoutFixes(string name, string script)
    {
        Assert.False(script.Contains("elapsed >= ", StringComparison.Ordinal),
            $"{name}: still counts sleep(1) iterations against a millisecond constant");
        Assert.True(script.Contains("getTickCount", StringComparison.Ordinal),
            $"{name}: timeout is not measured against a real clock");
        Assert.Contains("never saw this command", script, StringComparison.Ordinal);
    }

    // ── The emitter is shared, so the text cannot drift again ────────────────

    [Fact]
    public void AllGeneratorsEmitByteIdenticalWaitLoops_ApartFromTheirTag()
    {
        // The whole point of moving this into CeLuaHygiene. Strip the bracketed tag and
        // every generator's loop must be the same text.
        var loops = MailboxScripts()
            .Select(row => (Name: (string)row[0], Loop: ExtractLoop((string)row[1])))
            .ToList();

        var normalised = loops
            .Select(x => (x.Name, Text: System.Text.RegularExpressions.Regex.Replace(x.Loop, @"\[[^\]]+\]", "[TAG]")))
            .ToList();

        var first = normalised[0];
        foreach (var other in normalised.Skip(1))
            Assert.True(first.Text == other.Text,
                $"{other.Name} drifted from {first.Name}:\n---{first.Name}---\n{first.Text}\n---{other.Name}---\n{other.Text}");
    }

    private static string ExtractLoop(string script)
    {
        string enable = EnableBlock(script);
        int a = enable.IndexOf("local _tick", StringComparison.Ordinal);
        Assert.True(a >= 0, "no shared wait loop found");
        // Up to and including the loop's closing `end`, which is the last line the
        // emitter writes.
        int b = enable.IndexOf("\nend\n", a, StringComparison.Ordinal);
        Assert.True(b >= 0, "wait loop is not terminated");
        return enable[a..(b + 5)];
    }

    // ── The THIRD script shape: a helper function that answers a caller ──────
    //
    // PointerQuery's `query`, CoordLibrary's `call` and Invoke's `waitDone` put the wait
    // inside a `local function`, so neither of the shapes above applies: a `return`
    // leaves the HELPER rather than the block, and the helper must not untick (the
    // GameEngine record calls query() speculatively and is allowed to fail over to a
    // snapshot). These three were the only generators still hand-rolling the loop after
    // build 2743 converted the other seven — and so they were the only ones still
    // counting sleep(1) iterations against a millisecond constant and, in
    // PointerQuery's case, still shipping the "(DLL not responding?)" guess.

    public static IEnumerable<object[]> HelperShapedScripts() => new List<object[]>
    {
        new object[] { "PointerQuery.GWorld",     PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GWorld) },
        new object[] { "PointerQuery.GameEngine", PointerQueryScriptGenerator.Generate(PointerQueryScriptGenerator.Target.GameEngine) },
        new object[] { "CoordLibrary",            CoordLibraryScriptGenerator.Generate(OneCoord(), out _) },
        new object[] { "Invoke.NoParams",         InvokeScriptGenerator.Generate("PlayerCharacter", "Respawn", VoidFunc()) },
        new object[] { "Invoke.WithParams",       InvokeScriptGenerator.Generate("PlayerCharacter", "AddMoney", IntParamFunc()) },
    };

    private static UE5DumpUI.Models.CoordEntry[] OneCoord() => new[]
    {
        new UE5DumpUI.Models.CoordEntry { Uid = "u1", Label = "Shrine", Group = "Act 1", Map = "Level_01" },
    };

    private static UE5DumpUI.Models.FunctionInfoModel VoidFunc() =>
        new() { Name = "Respawn", ParmsSize = 0 };

    /// <summary>A function WITH an input param, so the generator emits the picker-form
    /// path — where <c>waitDone</c> is called from inside <c>btnFire.OnClick</c> rather
    /// than from the chunk. That second frame is the whole reason this generator raises
    /// instead of returning.</summary>
    private static UE5DumpUI.Models.FunctionInfoModel IntParamFunc() => new()
    {
        Name = "AddMoney",
        ParmsSize = 4,
        Params = new List<UE5DumpUI.Models.FunctionParamModel>
        {
            new() { Name = "Amount", TypeName = "IntProperty", Size = 4, Offset = 0 },
        },
    };

    [Theory]
    [MemberData(nameof(HelperShapedScripts))]
    public void HelperShapedWaits_UseTheSharedLoop(string name, string script)
    {
        Assert.True(script.Contains("while _st ~= " + CeMailboxLayout.StatusDone, StringComparison.Ordinal),
            $"{name}: not using CeLuaHygiene.AppendMailboxWait — this is the file that hand-rolled it");
    }

    [Theory]
    [MemberData(nameof(HelperShapedScripts))]
    public void HelperShapedWaits_MeasureARealDeadline(string name, string script)
    {
        Assert.True(script.Contains("getTickCount", StringComparison.Ordinal),
            $"{name}: timeout is not measured against a real clock");
        Assert.Contains($"_t0 >= {CeMailboxLayout.MailboxPollTimeoutMs}", script, StringComparison.Ordinal);

        // The pre-fix shape, in each of the three spellings these files used. sleep(1)
        // costs 15.47 ms, so every one of them was a ~155 s timeout wearing a 10 s label.
        Assert.False(script.Contains("elapsed >= ", StringComparison.Ordinal),
            $"{name}: InvokeScriptGenerator's iteration counter survived");
        Assert.False(script.Contains("waited >= ", StringComparison.Ordinal),
            $"{name}: CoordLibrary/PointerQuery's iteration counter survived");
        Assert.False(script.Contains("idleWaited >= ", StringComparison.Ordinal),
            $"{name}: the idle wait's iteration counter survived");
    }

    [Theory]
    [MemberData(nameof(HelperShapedScripts))]
    public void HelperShapedWaits_ReadTheStatus_InsteadOfGuessing(string name, string script)
    {
        Assert.True(script.Contains("never saw this command", StringComparison.Ordinal),
            $"{name}: does not name the status-0 case (the DLL never picked it up)");
        Assert.True(script.Contains("never finished it", StringComparison.Ordinal),
            $"{name}: does not name the status-PROCESSING case (the DLL wedged)");
        Assert.False(script.Contains("(DLL not responding?)", StringComparison.Ordinal),
            $"{name}: still emits the guess that blamed a healthy DLL");
        // Invoke's own flavour of the same mistake: on a TIMEOUT the DLL never wrote
        // errorMsg, so that string is empty or stale from the previous command.
        Assert.False(script.Contains("Mailbox timeout: ' .. err", StringComparison.Ordinal),
            $"{name}: still reports a timeout from the mailbox's stale errorMsg string");
    }

    [Theory]
    [MemberData(nameof(HelperShapedScripts))]
    public void HelperShapedWaits_DoNotUntickFromInsideTheHelper(string name, string script)
    {
        // The defining property of this shape. PointerQuery's GameEngine record calls
        // query() for the &GEngine slot and falls back to a snapshot when that fails —
        // an untick inside the helper would kill the record on an attempt that is
        // ALLOWED to fail, and CoordLibrary's would kill it while the picker form is
        // still open. The callers own the untick, and they already do it.
        string region = string.Join('\n', ExtractBailStatements(script));
        Assert.False(region.Contains("memrec.Active = false", StringComparison.Ordinal),
            $"{name}: the helper unticks the record itself — its caller owns that decision:\n{region}");
    }

    /// <summary>
    /// The statements the timeout runs after building <c>_msg</c>, up to and including
    /// the one that leaves the wait. Walked line by line rather than sliced by a
    /// hard-coded <c>"\n  end\n"</c>: generators emit this block at three different
    /// indents, and an indent-sensitive slice silently ran past the end of Invoke's
    /// helper and into the NEXT bail-out's untick — reporting a violation in code the
    /// timeout never reaches.
    /// </summary>
    private static List<string> ExtractBailStatements(string script)
    {
        var lines = script.Split('\n');
        int i = Array.FindIndex(lines,
            l => l.Contains("_msg = 'timed out and the DLL never saw", StringComparison.Ordinal));
        Assert.True(i > 0, "no shared timeout reason found");

        // Walk to the last arm of the reason table, then past its closing `end`.
        while (i < lines.Length && !lines[i].Contains("tostring(_st)", StringComparison.Ordinal)) i++;
        Assert.True(i + 2 < lines.Length, "timeout-reason block is not terminated");
        i += 2;

        var bail = new List<string>();
        for (int j = i; j < lines.Length && bail.Count < 4; j++)
        {
            bail.Add(lines[j]);
            string t = lines[j].Trim();
            if (t is "return" or "break" || t.StartsWith("return nil", StringComparison.Ordinal)
                || t.StartsWith("error(", StringComparison.Ordinal)) break;
        }
        return bail;
    }

    /// <summary>
    /// Negative control for the test above. If <see cref="ExtractBailStatements"/> ever
    /// stops finding the real bail — mis-anchored, or fooled by an indent change — the
    /// "does not untick" assertion would pass over an empty or wrong region and prove
    /// nothing. The toggles DO untick in exactly that region, so this is the sample that
    /// makes a green "no untick" mean something.
    /// </summary>
    [Theory]
    [MemberData(nameof(MailboxScripts))]
    public void TheBailExtractorFindsTheUntickWhenThereIsOne(string name, string script)
    {
        string region = string.Join('\n', ExtractBailStatements(EnableBlock(script)));
        Assert.True(region.Contains("memrec.Active = false", StringComparison.Ordinal),
            $"{name}: the extractor missed a bail-out that DOES untick — every 'does not " +
            $"untick' assertion built on it is therefore vacuous:\n{region}");
    }

    [Theory]
    [MemberData(nameof(HelperShapedScripts))]
    public void HelperShapedWaits_CarryTheDiagnosisOutToTheCaller(string name, string script)
    {
        // Two carriers, and they are not interchangeable — see the MailboxTimeout docs.
        bool byValue = script.Contains("return nil, _msg", StringComparison.Ordinal);
        bool byRaise = script.Contains("error('[Invoke] ' .. _msg)", StringComparison.Ordinal);
        Assert.True(byValue || byRaise,
            $"{name}: the timeout diagnosis never reaches the caller — it is computed and dropped");
    }

    /// <summary>
    /// The reason table is ONE emitter for every shape. It used to be three inline
    /// <c>showMessage</c> literals, which meant a by-value or by-raise mode would have
    /// needed its own copy of "status 0 means X, 0xFF means Y" — and a second copy of
    /// that rule is precisely how the original defect reached seven files at once.
    /// </summary>
    [Fact]
    public void TheTimeoutReasonIsByteIdenticalAcrossEveryShape()
    {
        var all = MailboxScripts().Concat(MomentaryScripts()).Concat(HelperShapedScripts())
            .Select(row => (Name: (string)row[0], Reason: ExtractReason((string)row[1])))
            .ToList();

        var first = all[0];
        foreach (var other in all.Skip(1))
            Assert.True(first.Reason == other.Reason,
                $"{other.Name} drifted from {first.Name}:\n---{first.Name}---\n{first.Reason}\n---{other.Name}---\n{other.Reason}");
    }

    /// <summary>The <c>local _msg … end</c> block, de-indented so generators that emit it
    /// at different nesting depths still compare equal.</summary>
    private static string ExtractReason(string script)
    {
        int a = script.IndexOf("local _msg\n", StringComparison.Ordinal);
        Assert.True(a >= 0, "no shared timeout-reason block found");
        int b = script.IndexOf("tostring(_st)\n", a, StringComparison.Ordinal);
        Assert.True(b >= 0, "timeout-reason block is not terminated");
        return string.Join('\n', script[a..(b + 14)].Split('\n').Select(l => l.TrimStart()));
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
