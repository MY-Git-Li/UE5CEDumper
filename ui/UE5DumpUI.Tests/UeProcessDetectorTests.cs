using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the UE-process detection heuristics used by the "Inject into running
/// game" picker (name-based first, then \Binaries\Win64\ path signal).
/// </summary>
public class UeProcessDetectorTests
{
    [Theory]
    [InlineData(@"C:\Games\MyGame\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe", true)]
    [InlineData(@"D:\X\Foo-WinGDK-Shipping.exe", true)]
    [InlineData(@"C:\Games\Repack\Game-Shipping.exe", true)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData(@"C:\Games\MyGame\Engine\Binaries\Win64\CrashReportClient.exe", false)]  // skip helper
    [InlineData(@"C:\Epic\UE_5.4\Engine\Binaries\Win64\UnrealEditor.exe", false)]        // skip editor
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUeExecutable_Heuristics(string? path, bool expected)
        => Assert.Equal(expected, UeProcessDetector.IsUeExecutable(path));

    [Fact]
    public void IsUeExecutable_BinariesWin64_PathSignal_IsTrue()
    {
        // Non-shipping-named exe but under \Binaries\Win64\ → still a UE signal.
        Assert.True(UeProcessDetector.IsUeExecutable(
            @"C:\Games\Foo\Foo\Binaries\Win64\Launcher.exe"));
    }

    [Fact]
    public void IsUeExecutable_OurOwnExe_IsFalse()
        => Assert.False(UeProcessDetector.IsUeExecutable(@"C:\dist\UE5DumpUI.exe"));
}
