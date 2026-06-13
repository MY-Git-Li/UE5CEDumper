using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the Teleport CE Lua hotkey bundle (docs/teleport-spec.md §9.2A/§9.3):
/// self-contained mailbox round-trip, busy-check, re-registration guard, the
/// three hotkey schemes with the exact VK literal sets, and the header
/// conflict + NumLock guidance.
/// </summary>
public class TeleportLuaBundleGeneratorTests
{
    [Fact]
    public void Generate_is_lf_only_for_ce_compatibility()
    {
        Assert.DoesNotContain("\r", TeleportLuaBundleGenerator.Generate());
    }

    [Fact]
    public void Is_self_contained_no_helper_file_dependency()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("g_invokeMailbox", s);
        Assert.DoesNotContain("findTableFile", s);
        Assert.DoesNotContain("ue5_invoke_helper", s);
    }

    [Fact]
    public void Triggers_cmd_teleport_and_has_busy_check()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("writeInteger(m + 0x00, 8)", s);     // CMD_TELEPORT = 8 (write LAST)
        Assert.Contains("readInteger(m + 0x00) ~= 0", s);    // busy-check before writing
    }

    [Fact]
    public void Emits_all_op_codes()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("tp(1,", s);    // SAVE
        Assert.Contains("tp(2,", s);    // RECALL
        Assert.Contains("tp(4,", s);    // CURSOR
        Assert.Contains("tp(7,", s);    // RECALL_LAST
        Assert.Contains("tp(9,", s);    // BUGIT_SAVE (bugIt)
        Assert.Contains("tp(10,", s);   // BUGIT_GO   (bugItGo)
    }

    [Fact]
    public void RecallLast_binds_ctrl_alt_zero()
    {
        // Numpad → Ctrl+Alt+Num0 (0x11, 0x12, 0x60).
        var numpad = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.Numpad);
        Assert.Contains("function recallLast()", numpad);
        Assert.Contains("createHotkey(function() recallLast() end, 0x11, 0x12, 0x60)", numpad);
        // Top-row → Ctrl+Alt+0 (0x11, 0x12, 0x30).
        var topRow = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.TopRow);
        Assert.Contains("createHotkey(function() recallLast() end, 0x11, 0x12, 0x30)", topRow);
    }

    [Fact]
    public void Has_hotkey_reregistration_guard()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("_ue5tpHotkeys", s);
        Assert.Contains("h.destroy()", s);
    }

    [Fact]
    public void Header_has_conflict_and_numlock_guidance()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("NumLock", s);
        Assert.Contains("clashes", s);
    }

    [Fact]
    public void BugIt_stores_dll_side_and_copies_clipboard()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("function bugIt()", s);
        Assert.Contains("tp(9, 0)", s);                  // BUGIT_SAVE stores DLL-side
        Assert.Contains("writeToClipboard", s);          // and still copies the string
        Assert.Contains("BugItGo %.3f %.3f %.3f", s);
    }

    [Fact]
    public void BugItGo_recalls_stored_pose_and_warns_when_empty()
    {
        var s = TeleportLuaBundleGenerator.Generate();
        Assert.Contains("function bugItGo()", s);
        Assert.Contains("tp(10, 0)", s);                 // BUGIT_GO
        Assert.Contains("no BugIt stored yet", s);       // -6 no-op guidance
    }

    // ── Hotkey schemes (§9.3) ──────────────────────────────────────────

    [Fact]
    public void Numpad_scheme_uses_numpad_vks()
    {
        var s = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.Numpad);
        // Save 1 = Ctrl+Num1 (0x11, 0x61); Recall 1 = Num1 (0x61); Cursor = Num0 (0x60)
        Assert.Contains("createHotkey(function() save(1) end, 0x11, 0x61)", s);
        Assert.Contains("createHotkey(function() recall(1) end, 0x61)", s);
        Assert.Contains("createHotkey(function() cursor() end, 0x60)", s);
        Assert.Contains("createHotkey(function() bugIt() end, 0x11, 0x60)", s);
        Assert.Contains("createHotkey(function() bugItGo() end, 0x11, 0x10, 0x60)", s);
        // No top-row digit VKs in the pure numpad scheme.
        Assert.DoesNotContain("0x31)", s);
    }

    [Fact]
    public void TopRow_scheme_uses_toprow_vks_with_alt_recall()
    {
        var s = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.TopRow);
        // Save 1 = Ctrl+1 (0x11, 0x31); Recall 1 = Alt+1 (0x12, 0x31); Cursor = Alt+0 (0x12, 0x30)
        Assert.Contains("createHotkey(function() save(1) end, 0x11, 0x31)", s);
        Assert.Contains("createHotkey(function() recall(1) end, 0x12, 0x31)", s);
        Assert.Contains("createHotkey(function() cursor() end, 0x12, 0x30)", s);
        Assert.Contains("createHotkey(function() bugIt() end, 0x11, 0x30)", s);
        Assert.Contains("createHotkey(function() bugItGo() end, 0x11, 0x10, 0x30)", s);
        // No numpad VKs in the pure top-row scheme.
        Assert.DoesNotContain("0x61)", s);
    }

    [Fact]
    public void Both_scheme_registers_two_bindings_per_action()
    {
        var s = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.Both);
        // Save 1 gets both Ctrl+Num1 and Ctrl+1.
        Assert.Contains("createHotkey(function() save(1) end, 0x11, 0x61)", s);
        Assert.Contains("createHotkey(function() save(1) end, 0x11, 0x31)", s);
        // 10 actions × 2 bindings = 20 createHotkey calls.
        int count = s.Split("createHotkey(").Length - 1;
        Assert.Equal(20, count);
    }

    [Fact]
    public void Numpad_scheme_has_one_binding_per_action()
    {
        var s = TeleportLuaBundleGenerator.Generate(TeleportHotkeyScheme.Numpad);
        int count = s.Split("createHotkey(").Length - 1;
        Assert.Equal(10, count);   // 6 markers + recall-last + cursor + bugIt + bugItGo
    }

    [Fact]
    public void AaRecord_wraps_bundle_in_enable_disable()
    {
        var s = TeleportLuaBundleGenerator.GenerateAaRecord(TeleportHotkeyScheme.Numpad);
        Assert.DoesNotContain("\r", s);
        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
        Assert.Contains("if syntaxcheck then return end", s);
        // [ENABLE] registers hotkeys, [DISABLE] tears them down.
        Assert.Contains("createHotkey(", s);
        var disable = s.Substring(s.IndexOf("[DISABLE]", System.StringComparison.Ordinal));
        Assert.Contains("h.destroy()", disable);
        Assert.Contains("_ue5tpHotkeys = nil", disable);
    }

    [Fact]
    public void Cursor_params_are_baked_from_arguments()
    {
        var s = TeleportLuaBundleGenerator.Generate(
            TeleportHotkeyScheme.Numpad, zOffset: 250.0, channel: 2, fallbackCenter: false);
        Assert.Contains("writeDouble(m + 0x328, 250.0)", s);
        Assert.Contains("writeBytes(m + 0x330, 2)", s);      // channel
        Assert.Contains("writeBytes(m + 0x331, 0)", s);      // fallback off
    }
}
