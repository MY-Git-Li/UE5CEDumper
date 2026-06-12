using Avalonia.Input;
using UE5DumpUI.Core;

namespace UE5DumpUI.Services;

/// <summary>
/// Converts an Avalonia (<see cref="Key"/> + <see cref="KeyModifiers"/>) capture
/// into a Win32 (modifier-mask, virtual-key) pair for RegisterHotKey, plus a
/// human label. Used by the Teleport panel's "press keys to set" hotkey capture.
///
/// Only keys that make sense as a teleport hotkey are accepted: F1–F24, the top
/// number row (0–9), the numpad (0–9), and letters A–Z. Modifier-only presses
/// (Ctrl/Alt/Shift/Win alone) are rejected so the capture waits for a real key —
/// matching "hold Ctrl, then press F7 ⇒ Ctrl+F7; press F7 alone ⇒ F7".
/// </summary>
public static class HotkeyKeyMap
{
    /// <summary>Try to convert a captured key. Returns false for modifier-only
    /// or unsupported keys (caller should keep listening).</summary>
    public static bool TryConvert(Key key, KeyModifiers mods,
        out uint winMods, out uint vk, out string label)
    {
        winMods = 0;
        vk = 0;
        label = "";

        if (!TryVk(key, out vk, out string keyName))
            return false;

        if (mods.HasFlag(KeyModifiers.Control)) winMods |= HotkeyModifiers.Control;
        if (mods.HasFlag(KeyModifiers.Alt))     winMods |= HotkeyModifiers.Alt;
        if (mods.HasFlag(KeyModifiers.Shift))   winMods |= HotkeyModifiers.Shift;
        if (mods.HasFlag(KeyModifiers.Meta))    winMods |= HotkeyModifiers.Win;

        label = Label(winMods, keyName);
        return true;
    }

    /// <summary>Build the display label for a stored (winMods, vk) pair.</summary>
    public static string LabelFor(uint winMods, uint vk)
        => Label(winMods, KeyName(vk));

    private static string Label(uint winMods, string keyName)
    {
        var parts = new List<string>(4);
        if ((winMods & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((winMods & HotkeyModifiers.Alt) != 0)     parts.Add("Alt");
        if ((winMods & HotkeyModifiers.Shift) != 0)   parts.Add("Shift");
        if ((winMods & HotkeyModifiers.Win) != 0)     parts.Add("Win");
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    // --- Avalonia Key → Win32 VK (ranges are contiguous in both enums) ---
    private static bool TryVk(Key key, out uint vk, out string name)
    {
        vk = 0;
        name = "";
        if (key >= Key.F1 && key <= Key.F24)
        {
            vk = (uint)(0x70 + (key - Key.F1));   // VK_F1 = 0x70
            name = "F" + (1 + (key - Key.F1));
            return true;
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            vk = (uint)(0x30 + (key - Key.D0));   // top-row digits
            name = ((int)(key - Key.D0)).ToString();
            return true;
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            vk = (uint)(0x60 + (key - Key.NumPad0));  // VK_NUMPAD0 = 0x60
            name = "Num" + (int)(key - Key.NumPad0);
            return true;
        }
        if (key >= Key.A && key <= Key.Z)
        {
            vk = (uint)(0x41 + (key - Key.A));    // VK_A = 0x41
            name = ((char)('A' + (key - Key.A))).ToString();
            return true;
        }
        return false;
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk >= 0x30 && vk <= 0x39) return ((char)('0' + (vk - 0x30))).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
        if (vk >= 0x41 && vk <= 0x5A) return ((char)('A' + (vk - 0x41))).ToString();
        return "0x" + vk.ToString("X2");
    }
}
