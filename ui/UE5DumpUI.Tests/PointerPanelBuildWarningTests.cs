using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the stale-DLL build-version warning logic that drives both the
/// per-tab Diagnostics badge and the global top-bar badge. In the test host the
/// UI build number (assembly revision) is 0, so any DLL build &gt; 0 reads as a
/// mismatch and a DLL build of 0 reads as "unknown" — both surface the warning,
/// which is exactly the logic we want to lock so a hand-deployed stale proxy DLL
/// is always flagged.
/// </summary>
public class PointerPanelBuildWarningTests
{
    private static PointerPanelViewModel MakeVm() =>
        new(new MockPlatformService(Path.GetTempPath()));

    [Fact]
    public void NoData_NoWarning()
    {
        var vm = MakeVm();
        Assert.False(vm.ShowGlobalBuildWarning);
        Assert.False(vm.BuildVersionMismatch);
        Assert.False(vm.BuildVersionUnknown);
        Assert.Equal("", vm.GlobalBuildWarningText);
    }

    [Fact]
    public void DllBuildDiffersFromUi_ShowsStaleWarning()
    {
        var vm = MakeVm();
        vm.Update(new EngineState { DllBuildNumber = 920 });  // UI build is 0 in the test host
        Assert.True(vm.BuildVersionMismatch);
        Assert.True(vm.ShowGlobalBuildWarning);
        Assert.Contains("920", vm.GlobalBuildWarningText);
        Assert.Contains("stale", vm.GlobalBuildWarningText);
    }

    [Fact]
    public void DllReportsNoBuild_ShowsUnknownWarning()
    {
        var vm = MakeVm();
        vm.Update(new EngineState { DllBuildNumber = 0 });
        Assert.True(vm.BuildVersionUnknown);
        Assert.True(vm.ShowGlobalBuildWarning);
        Assert.False(vm.BuildVersionMismatch);
        Assert.Contains("pre-dates", vm.GlobalBuildWarningText);
    }

    [Fact]
    public void Update_ReRaisesShowGlobalBuildWarning_EvenWhenDllBuildUnchanged()
    {
        // The global top-bar badge mirror only listens for ShowGlobalBuildWarning /
        // GlobalBuildWarningText. A second Update() with the SAME DllBuildNumber doesn't
        // fire OnDllBuildNumberChanged (value unchanged), so NotifyComputedProperties MUST
        // re-raise them or the badge goes stale on reconnect/refresh.
        var vm = MakeVm();
        vm.Update(new EngineState { DllBuildNumber = 920 }); // 0 -> 920

        bool warningRaised = false, textRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PointerPanelViewModel.ShowGlobalBuildWarning)) warningRaised = true;
            if (e.PropertyName == nameof(PointerPanelViewModel.GlobalBuildWarningText)) textRaised = true;
        };

        vm.Update(new EngineState { DllBuildNumber = 920 }); // unchanged

        Assert.True(warningRaised);
        Assert.True(textRaised);
    }
}
