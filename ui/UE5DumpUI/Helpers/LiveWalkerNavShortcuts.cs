using Avalonia.Input;

namespace UE5DumpUI.Helpers;

/// <summary>Which Live Walker navigation a browser-style shortcut is asking for.</summary>
public enum NavShortcut
{
    None,
    Back,
    Forward,
}

/// <summary>
/// Browser-style Back / Forward shortcut policy for the Live Walker.
///
/// This is pure decision code on purpose: the real triggers — a physical mouse's
/// 4th/5th button, a real Alt+Arrow press — cannot be produced in a headless test,
/// so the DECISION is separated from the window plumbing that acts on it and only
/// the plumbing goes unverified.
///
/// The bindings mirror what every browser does, and all three input routes are
/// accepted because they are not interchangeable in practice: gaming-mouse drivers
/// (Logitech, Razer) are frequently configured to emit <c>Alt+Left</c> keystrokes or
/// the dedicated <see cref="Key.BrowserBack"/> key instead of a true XButton, so
/// handling only one route means "the side buttons do nothing" for some users.
///
/// <para>Gating (both must hold, per the feature's contract):</para>
/// <list type="bullet">
/// <item><b>window active</b> — the dumper must be the foreground window. Key events
/// only reach an unfocused window anyway, so this is mostly belt-and-braces; a click
/// with a side button DOES arrive at a background window (it activates it first),
/// which is exactly the case this guard exists for.</item>
/// <item><b>Live Walker is the current tab</b> — these shortcuts drive that panel's
/// spine, so they must not fire from any other tab. The caller decides this by
/// <c>TabItem.Tag</c>, never by index (see MainTabIndex's note on index drift).</item>
/// </list>
/// </summary>
public static class LiveWalkerNavShortcuts
{
    /// <summary>
    /// Keyboard route. <c>Alt+Left</c> / <c>Alt+Right</c> must carry EXACTLY Alt —
    /// requiring an exact match keeps us out of the way of combos other software
    /// claims (Ctrl+Alt+Arrow is screen rotation on Intel/AMD display drivers).
    /// The dedicated <see cref="Key.BrowserBack"/> / <see cref="Key.BrowserForward"/>
    /// keys mean one thing regardless of what else is held, so they ignore modifiers.
    /// </summary>
    public static NavShortcut Resolve(Key key, KeyModifiers modifiers,
                                      bool isLiveWalkerTab, bool windowActive)
    {
        if (!isLiveWalkerTab || !windowActive) return NavShortcut.None;

        if (key == Key.BrowserBack) return NavShortcut.Back;
        if (key == Key.BrowserForward) return NavShortcut.Forward;

        if (modifiers != KeyModifiers.Alt) return NavShortcut.None;
        return key switch
        {
            Key.Left => NavShortcut.Back,
            Key.Right => NavShortcut.Forward,
            _ => NavShortcut.None,
        };
    }

    /// <summary>
    /// Mouse route: the 4th and 5th buttons. Windows reports these as
    /// <c>WM_XBUTTONDOWN</c>, which Avalonia's Win32 backend turns into an ordinary
    /// PointerPressed whose <see cref="PointerUpdateKind"/> is
    /// <see cref="PointerUpdateKind.XButton1Pressed"/> / <c>XButton2Pressed</c>.
    /// Every other kind (including the ordinary left/right/middle presses that flow
    /// through the same handler) resolves to <see cref="NavShortcut.None"/> so normal
    /// clicking is untouched.
    /// </summary>
    public static NavShortcut Resolve(PointerUpdateKind kind,
                                      bool isLiveWalkerTab, bool windowActive)
    {
        if (!isLiveWalkerTab || !windowActive) return NavShortcut.None;
        return kind switch
        {
            PointerUpdateKind.XButton1Pressed => NavShortcut.Back,
            PointerUpdateKind.XButton2Pressed => NavShortcut.Forward,
            _ => NavShortcut.None,
        };
    }
}
