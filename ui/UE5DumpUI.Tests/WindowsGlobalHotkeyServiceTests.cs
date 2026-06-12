using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Smoke tests for the global cursor hotkey. These actually exercise the Win32
/// RegisterHotKey / GetMessageW / PostThreadMessageW P/Invokes on the build
/// machine — entry points resolve at runtime, so a wrong name (the
/// EntryPointNotFoundException CTD bug) only shows when the code really runs.
/// Windows-only; skipped elsewhere.
/// </summary>
public class WindowsGlobalHotkeyServiceTests
{
    private static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Register_then_dispose_does_not_crash_and_reports_a_combo()
    {
        if (!IsWindows) return;   // platform-gated smoke test

        var svc = new WindowsGlobalHotkeyService();
        bool fired = false;
        var reg = svc.RegisterCursorHotkey(() => fired = true);

        // A free combo from the ladder should normally be claimable in a CI /
        // dev session. If something genuinely grabbed all of Ctrl/Alt+F5..F8,
        // a null result is still a valid (non-crashing) outcome — the contract
        // is "never crash", so only assert the disposal path below.
        if (reg != null)
        {
            Assert.False(string.IsNullOrEmpty(reg.Label));
            // Dispose exercises PostThreadMessageW + Join — a wrong entry point
            // there would throw here.
            reg.Dispose();
        }

        Assert.False(fired);   // we never pressed the key
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        if (!IsWindows) return;

        var svc = new WindowsGlobalHotkeyService();
        var reg = svc.RegisterCursorHotkey(() => { });
        if (reg == null) return;

        reg.Dispose();
        reg.Dispose();   // must be idempotent / not throw
    }
}
