using Avalonia.Input;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>Avalonia Key + modifiers → Win32 (mods, vk) + label mapping.</summary>
public class HotkeyKeyMapTests
{
    [Fact]
    public void Function_key_with_ctrl()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.F7, KeyModifiers.Control, out var mods, out var vk, out var label));
        Assert.Equal(0x02u, mods);   // MOD_CONTROL
        Assert.Equal(0x76u, vk);     // VK_F7
        Assert.Equal("Ctrl+F7", label);
    }

    [Fact]
    public void Single_function_key_no_modifier()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.F5, KeyModifiers.None, out var mods, out var vk, out var label));
        Assert.Equal(0u, mods);
        Assert.Equal(0x74u, vk);
        Assert.Equal("F5", label);
    }

    [Fact]
    public void Numpad_with_shift()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.NumPad4, KeyModifiers.Shift, out var mods, out var vk, out var label));
        Assert.Equal(0x04u, mods);   // MOD_SHIFT
        Assert.Equal(0x64u, vk);     // VK_NUMPAD4
        Assert.Equal("Shift+Num4", label);
    }

    [Fact]
    public void Top_row_digit_with_alt()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.D3, KeyModifiers.Alt, out var mods, out var vk, out var label));
        Assert.Equal(0x01u, mods);   // MOD_ALT
        Assert.Equal(0x33u, vk);     // '3'
        Assert.Equal("Alt+3", label);
    }

    [Fact]
    public void Letter_with_ctrl_shift_orders_modifiers()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.G, KeyModifiers.Shift | KeyModifiers.Control, out var mods, out var vk, out var label));
        Assert.Equal(0x06u, mods);   // CONTROL|SHIFT
        Assert.Equal(0x47u, vk);     // VK_G
        Assert.Equal("Ctrl+Shift+G", label);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.LWin)]
    [InlineData(Key.Tab)]
    public void Modifier_only_or_unsupported_keys_rejected(Key key)
    {
        Assert.False(HotkeyKeyMap.TryConvert(key, KeyModifiers.None, out _, out _, out _));
    }

    [Fact]
    public void LabelFor_reconstructs_from_stored_pair()
    {
        Assert.Equal("Ctrl+F7", HotkeyKeyMap.LabelFor(0x02, 0x76));
        Assert.Equal("Num4", HotkeyKeyMap.LabelFor(0x00, 0x64));
    }
}
