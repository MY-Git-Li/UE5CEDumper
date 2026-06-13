using System.Globalization;
using System.Text;

namespace UE5DumpUI.Services;

/// <summary>Hotkey scheme for the generated Teleport CE scripts.
/// CE distinguishes top-row digit VKs (0x31..) from numpad VKs (0x61..), so a
/// <c>Ctrl+1</c> binding never fires on <c>Ctrl+Numpad1</c>. See
/// docs/teleport-spec.md §9.3 (decision D11).</summary>
public enum TeleportHotkeyScheme
{
    /// <summary>Ctrl+Num1..3 save, Num1..3 recall, Num0 cursor, Ctrl+Num0 copy.
    /// Default — ARPGs already bind Ctrl+1..3 to skills. Needs NumLock ON.</summary>
    Numpad,
    /// <summary>Ctrl+1..3 save, Alt+1..3 recall, Alt+0 cursor, Ctrl+0 copy.
    /// For TKL / laptop keyboards with no numpad (bare 1..3 would collide with
    /// every game's skill bar, so recall uses Alt).</summary>
    TopRow,
    /// <summary>Both bindings registered per action.</summary>
    Both,
}

/// <summary>
/// Generates a single self-contained CE Lua "Teleport bundle" the user pastes
/// into Cheat Engine (Table Lua Script or the Lua Engine window). It talks to
/// <c>UE5Dumper.dll</c>'s Mimic mailbox directly (<c>CMD_TELEPORT = 8</c>) — no
/// <c>ue5_invoke_helper.lua</c> dependency — and binds hotkeys for marker
/// save/recall, cursor teleport, and "copy current pose as a BugItGo string".
///
/// Mirrors <see cref="DebugCameraScriptGenerator"/>'s self-contained mailbox
/// round-trip. The full contract is docs/teleport-spec.md §9.2A / §9.3.
/// </summary>
public static class TeleportLuaBundleGenerator
{
    // Mimic mailbox layout (must match dll/src/Mimic.h MailboxData):
    //   0x00 cmd (write LAST to trigger), 0x04 status (poll == 1), 0x08 result,
    //   0x10 instanceAddr = teleport op, 0x18 ufuncAddr = slot,
    //   0x228 errorMsg, 0x328 paramsData.
    // CMD_TELEPORT = 8. Ops: 0 GET_POSE, 1 SAVE, 2 RECALL, 3 RECALL_FORCE,
    //   4 CURSOR, 5 GET_MARKER, 6 CLEAR, 7 RECALL_LAST, 9 BUGIT_SAVE,
    //   10 BUGIT_GO. (TeleportOp in Mimic.h)
    private const int CmdTeleport = 8;
    private const int OpSave = 1;
    private const int OpRecall = 2;
    private const int OpCursor = 4;
    private const int OpRecallLast = 7;
    private const int OpBugItSave = 9;
    private const int OpBugItGo = 10;

    // VK codes (numeric literals — CE's defines.lua has VK_NUMPAD0..9 but no
    // VK_1-style names for the 0x30..0x39 top row, so we emit literals + a
    // trailing comment for every key, keeping the script self-contained and
    // editable. See docs/teleport-spec.md §9.3 rule 1.)
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkAlt = 0x12;
    private const int VkNumpad0 = 0x60;
    private const int VkTopRow0 = 0x30;

    private sealed record Binding(int[] Keys, string Notation);

    private sealed record ActionHotkeys(string FuncCall, List<Binding> Bindings, string Label);

    /// <summary>Build the full bundle script for the given scheme + cursor
    /// parameters. Line endings are LF only (CE-friendly). This is the raw
    /// form — paste into a CE 'Table Lua Script' or the Lua Engine window.</summary>
    public static string Generate(
        TeleportHotkeyScheme scheme = TeleportHotkeyScheme.Numpad,
        double zOffset = 100.0,
        int channel = 0,
        bool fallbackCenter = true)
    {
        var actions = BuildActions(scheme);

        var sb = new StringBuilder(4096);
        EmitHeader(sb, scheme, actions, zOffset, channel, fallbackCenter);
        EmitMailboxHelpers(sb, zOffset, channel, fallbackCenter);
        EmitHotkeyRegistration(sb, actions);
        return sb.ToString();
    }

    /// <summary>Build the bundle wrapped as a CE memory-record AA Script, for
    /// shipping straight into the CE table via AOBMaker (CreateAAScript).
    /// Ticking the record registers the hotkeys ([ENABLE]); unticking destroys
    /// them ([DISABLE]). Same mailbox / hotkey body as <see cref="Generate"/>.</summary>
    public static string GenerateAaRecord(
        TeleportHotkeyScheme scheme = TeleportHotkeyScheme.Numpad,
        double zOffset = 100.0,
        int channel = 0,
        bool fallbackCenter = true)
    {
        var actions = BuildActions(scheme);

        var sb = new StringBuilder(4096);
        Line(sb, "[ENABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if syntaxcheck then return end");
        EmitHeader(sb, scheme, actions, zOffset, channel, fallbackCenter);
        EmitMailboxHelpers(sb, zOffset, channel, fallbackCenter);
        EmitHotkeyRegistration(sb, actions);
        Line(sb, "{$asm}");
        Line(sb, "[DISABLE]");
        Line(sb, "{$lua}");
        Line(sb, "if _ue5tpHotkeys then");
        Line(sb, "  for _, h in ipairs(_ue5tpHotkeys) do pcall(function() h.destroy() end) end");
        Line(sb, "  _ue5tpHotkeys = nil");
        Line(sb, "end");
        Line(sb, "print('[Teleport] hotkeys removed.')");
        Line(sb, "{$asm}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Hotkey scheme table (docs/teleport-spec.md §9.3)
    // ------------------------------------------------------------------

    private static List<ActionHotkeys> BuildActions(TeleportHotkeyScheme scheme)
    {
        var list = new List<ActionHotkeys>();

        for (int i = 1; i <= 3; i++)
        {
            list.Add(new ActionHotkeys(
                $"function() save({i}) end",
                SaveBindings(scheme, i),
                $"Save marker {i}"));
        }
        for (int i = 1; i <= 3; i++)
        {
            list.Add(new ActionHotkeys(
                $"function() recall({i}) end",
                RecallBindings(scheme, i),
                $"Recall marker {i}"));
        }
        list.Add(new ActionHotkeys(
            "function() recallLast() end",
            RecallLastBindings(scheme),
            "Recall last"));
        list.Add(new ActionHotkeys(
            "function() cursor() end",
            CursorBindings(scheme),
            "Teleport to cursor"));
        list.Add(new ActionHotkeys(
            "function() bugIt() end",
            BugItBindings(scheme),
            "BugIt (store + copy)"));
        list.Add(new ActionHotkeys(
            "function() bugItGo() end",
            BugItGoBindings(scheme),
            "BugItGo (go to stored)"));
        return list;
    }

    private static List<Binding> SaveBindings(TeleportHotkeyScheme s, int i)
    {
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkNumpad0 + i }, $"Ctrl+Num{i}"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkTopRow0 + i }, $"Ctrl+{i}"));
        return b;
    }

    private static List<Binding> RecallBindings(TeleportHotkeyScheme s, int i)
    {
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkNumpad0 + i }, $"Num{i}"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkAlt, VkTopRow0 + i }, $"Alt+{i}"));
        return b;
    }

    private static List<Binding> RecallLastBindings(TeleportHotkeyScheme s)
    {
        // Ctrl+Alt+0 keeps it clear of the per-marker recalls and the copy combo.
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkAlt, VkNumpad0 }, "Ctrl+Alt+Num0"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkAlt, VkTopRow0 }, "Ctrl+Alt+0"));
        return b;
    }

    private static List<Binding> CursorBindings(TeleportHotkeyScheme s)
    {
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkNumpad0 }, "Num0"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkAlt, VkTopRow0 }, "Alt+0"));
        return b;
    }

    private static List<Binding> BugItBindings(TeleportHotkeyScheme s)
    {
        // "BugIt" = capture/copy — keeps the old copy-pose combo.
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkNumpad0 }, "Ctrl+Num0"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkTopRow0 }, "Ctrl+0"));
        return b;
    }

    private static List<Binding> BugItGoBindings(TeleportHotkeyScheme s)
    {
        // "BugItGo" = return to the stored BugIt spot. Ctrl+Shift+0 sits clear
        // of cursor (Num0/Alt+0), BugIt (Ctrl+0) and recall-last (Ctrl+Alt+0).
        var b = new List<Binding>();
        if (s is TeleportHotkeyScheme.Numpad or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkShift, VkNumpad0 }, "Ctrl+Shift+Num0"));
        if (s is TeleportHotkeyScheme.TopRow or TeleportHotkeyScheme.Both)
            b.Add(new Binding(new[] { VkControl, VkShift, VkTopRow0 }, "Ctrl+Shift+0"));
        return b;
    }

    // ------------------------------------------------------------------
    // Emit
    // ------------------------------------------------------------------

    private static void EmitHeader(StringBuilder sb, TeleportHotkeyScheme scheme,
        List<ActionHotkeys> actions, double zOffset, int channel, bool fallbackCenter)
    {
        Line(sb, "-- ================================================================");
        Line(sb, "-- UE5CEDumper Teleport bundle -- generated, self-contained (mailbox only)");
        Line(sb, "-- Requires UE5Dumper.dll injected (version.dll proxy or CE inject).");
        Line(sb, "-- Paste into a CE 'Table Lua Script' or the Lua Engine window.");
        Line(sb, $"-- Hotkey scheme: {scheme}");
        Line(sb, "-- Bindings:");
        foreach (var a in actions)
        {
            string notation = string.Join(" / ", a.Bindings.ConvertAll(x => x.Notation));
            Line(sb, $"--   {a.Label,-22} = {notation}");
        }
        Line(sb, "-- NumLock must be ON for numpad bindings; with NumLock off the numpad");
        Line(sb, "-- keys send navigation keys and the hotkeys appear dead (use the");
        Line(sb, "-- Top-row or Both scheme instead).");
        Line(sb, "-- If a hotkey clashes with your game, edit the createHotkey lines below");
        Line(sb, "-- (VK codes are commented) or rebind in CE; regenerating with another");
        Line(sb, "-- scheme also works.");
        Line(sb, "-- ================================================================");
        Line(sb);
    }

    private static void EmitMailboxHelpers(StringBuilder sb, double zOffset, int channel, bool fallbackCenter)
    {
        Line(sb, "local function tpMailbox()");
        Line(sb, "  local a = getAddressSafe('g_invokeMailbox')");
        Line(sb, "  if not a or a == 0 then a = getAddressSafe('UE5Dumper.g_invokeMailbox') end");
        Line(sb, "  return a");
        Line(sb, "end");
        Line(sb);

        // Core round-trip. op + slot in the pointer fields; cursor params in
        // paramsData. Returns (code, mailboxAddr) or (nil) on no-DLL/busy.
        Line(sb, "-- op: 0 get-pose, 1 save, 2 recall, 4 cursor (see Mimic.h TeleportOp)");
        Line(sb, "local function tp(op, slot)");
        Line(sb, "  local m = tpMailbox()");
        Line(sb, "  if not m then print('[Teleport] g_invokeMailbox not found -- DLL injected?') return nil end");
        Line(sb, "  if readInteger(m + 0x00) ~= 0 then print('[Teleport] mailbox busy, skipped') return nil end");
        Line(sb, $"  if op == {OpCursor} then");
        Line(sb, $"    writeDouble(m + 0x328, {Lua(zOffset)})   -- zOffset");
        Line(sb, $"    writeBytes(m + 0x330, {channel & 0xFF})            -- trace channel (ETraceTypeQuery byte)");
        Line(sb, $"    writeBytes(m + 0x331, {(fallbackCenter ? 1 : 0)})            -- fall back to screen center");
        Line(sb, "  end");
        Line(sb, "  writeQword(m + 0x18, slot or 0)         -- ufuncAddr = slot");
        Line(sb, "  writeQword(m + 0x10, op)                -- instanceAddr = op");
        Line(sb, "  writeInteger(m + 0x04, 0)               -- clear status");
        Line(sb, $"  writeInteger(m + 0x00, {CmdTeleport})  -- CMD_TELEPORT (write LAST)");
        Line(sb, "  local elapsed = 0");
        Line(sb, "  while readInteger(m + 0x04) ~= 1 do");
        Line(sb, "    sleep(1); elapsed = elapsed + 1");
        Line(sb, "    if elapsed >= 10000 then print('[Teleport] mailbox timeout (DLL not responding?)') return nil end");
        Line(sb, "  end");
        Line(sb, "  local code = readInteger(m + 0x08)");
        Line(sb, "  if code ~= 0 then");
        Line(sb, "    print('[Teleport] op=' .. op .. ' code=' .. code .. ' ' .. readString(m + 0x228, 256))");
        Line(sb, "  end");
        Line(sb, "  return code, m");
        Line(sb, "end");
        Line(sb);

        Line(sb, $"function save(i)   tp({OpSave},   i - 1) end");
        Line(sb, "function recall(i)");
        Line(sb, $"  local code = tp({OpRecall}, i - 1)");
        Line(sb, "  if code == -7 then");
        Line(sb, "    print('[Teleport] marker ' .. i .. ' was saved on another map -- recall refused. " +
                 "Use the UI Force button or re-save here.')");
        Line(sb, "  end");
        Line(sb, "end");
        // recallLast: undo the most recent jump. The DLL auto-saves the pre-jump
        // pose before every recall/cursor/BugItGo, so this restores it (op 7).
        Line(sb, "function recallLast()");
        Line(sb, $"  local code = tp({OpRecallLast}, 0)");
        Line(sb, "  if code == -6 then print('[Teleport] no last position saved yet') end");
        Line(sb, "end");
        Line(sb, $"function cursor()  tp({OpCursor}, 0) end");
        Line(sb);

        // bugIt: BUGIT_SAVE stores the pose DLL-side AND returns the pose block,
        // so we also clipboard a 'BugItGo X Y Z' string for convenience.
        Line(sb, "function bugIt()");
        Line(sb, $"  local code, m = tp({OpBugItSave}, 0)");
        Line(sb, "  if code ~= 0 or not m then return end");
        Line(sb, "  local x = readDouble(m + 0x328)");
        Line(sb, "  local y = readDouble(m + 0x330)");
        Line(sb, "  local z = readDouble(m + 0x338)");
        Line(sb, "  local s = string.format('BugItGo %.3f %.3f %.3f', x, y, z)");
        Line(sb, "  writeToClipboard(s)");
        Line(sb, "  print('[Teleport] BugIt stored + copied: ' .. s)");
        Line(sb, "end");
        // bugItGo: teleport to the DLL-stored BugIt pose (no-op when none yet).
        Line(sb, "function bugItGo()");
        Line(sb, $"  local code = tp({OpBugItGo}, 0)");
        Line(sb, "  if code == -6 then print('[Teleport] no BugIt stored yet -- press BugIt first') end");
        Line(sb, "end");
        Line(sb);
    }

    private static void EmitHotkeyRegistration(StringBuilder sb, List<ActionHotkeys> actions)
    {
        Line(sb, "-- Re-running this script destroys the previously created hotkeys first");
        Line(sb, "-- so keys are never double-registered (CE keeps them until restart).");
        Line(sb, "if _ue5tpHotkeys then");
        Line(sb, "  for _, h in ipairs(_ue5tpHotkeys) do pcall(function() h.destroy() end) end");
        Line(sb, "end");
        Line(sb, "_ue5tpHotkeys = {}");
        Line(sb);
        foreach (var a in actions)
        {
            foreach (var bind in a.Bindings)
            {
                string keys = string.Join(", ", Array.ConvertAll(bind.Keys,
                    k => "0x" + k.ToString("X2", CultureInfo.InvariantCulture)));
                Line(sb, $"table.insert(_ue5tpHotkeys, createHotkey({a.FuncCall}, {keys}))   -- {bind.Notation} ({a.Label})");
            }
        }
        Line(sb);
        Line(sb, "print('[Teleport] hotkeys registered. UE5Dumper.dll must be injected.')");
    }

    private static string Lua(double v) =>
        v.ToString("0.0###", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, string text = "")
    {
        sb.Append(text);
        sb.Append('\n');
    }
}
