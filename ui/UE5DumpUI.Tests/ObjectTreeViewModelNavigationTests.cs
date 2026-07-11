using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Phase 3: the Object Tree per-instance drill-down context actions. Each command must
/// raise its navigation event with the selected row's ADDRESS (the per-hit handoff a
/// global instance search needs), and must no-op on a null node or an empty address so
/// a bad row never fires a navigation to a dead target.
/// </summary>
public class ObjectTreeViewModelNavigationTests
{
    private static ObjectTreeViewModel NewVm() =>
        new(new StubDumpService(), new MockLoggingService(), new MockPlatformService(Path.GetTempPath()));

    private static UObjectNode Node(string addr = "0x1400ABCD") =>
        new() { Address = addr, ClassName = "BP_Enemy_C", Name = "Enemy_0" };

    [Fact]
    public void OpenInLiveWalker_RaisesNavigateToLiveWalker_WithAddress()
    {
        var vm = NewVm();
        string? got = null;
        vm.NavigateToLiveWalker += a => got = a;

        vm.OpenInLiveWalkerCommand.Execute(Node("0x1400ABCD"));

        Assert.Equal("0x1400ABCD", got);
    }

    [Fact]
    public void LocateInGWorld_RaisesLocateInGWorld_WithAddress()
    {
        var vm = NewVm();
        string? got = null;
        vm.LocateInGWorld += a => got = a;

        vm.LocateSelectedInGWorldCommand.Execute(Node("0x1400BEEF"));

        Assert.Equal("0x1400BEEF", got);
    }

    [Fact]
    public void LocateInGameEngine_RaisesLocateInGameEngine_WithAddress()
    {
        var vm = NewVm();
        string? got = null;
        vm.LocateInGameEngine += a => got = a;

        vm.LocateSelectedInGameEngineCommand.Execute(Node("0x1400CAFE"));

        Assert.Equal("0x1400CAFE", got);
    }

    [Fact]
    public void ShowRelatedObjects_RaisesNavigateToRelatedObjects_WithAddress()
    {
        var vm = NewVm();
        string? got = null;
        vm.NavigateToRelatedObjects += a => got = a;

        vm.ShowRelatedObjectsCommand.Execute(Node("0x1400F00D"));

        Assert.Equal("0x1400F00D", got);
    }

    [Fact]
    public void DrillCommands_NullNode_DoNotFire()
    {
        var vm = NewVm();
        bool fired = false;
        vm.NavigateToLiveWalker += _ => fired = true;
        vm.LocateInGWorld += _ => fired = true;
        vm.LocateInGameEngine += _ => fired = true;
        vm.NavigateToRelatedObjects += _ => fired = true;

        vm.OpenInLiveWalkerCommand.Execute(null);
        vm.LocateSelectedInGWorldCommand.Execute(null);
        vm.LocateSelectedInGameEngineCommand.Execute(null);
        vm.ShowRelatedObjectsCommand.Execute(null);

        Assert.False(fired);
    }

    [Fact]
    public void DrillCommands_EmptyAddress_DoNotFire()
    {
        var vm = NewVm();
        bool fired = false;
        vm.NavigateToLiveWalker += _ => fired = true;
        vm.NavigateToRelatedObjects += _ => fired = true;

        vm.OpenInLiveWalkerCommand.Execute(Node(""));
        vm.ShowRelatedObjectsCommand.Execute(Node(""));

        Assert.False(fired);
    }
}
