using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the Live Walker "Locate in GWorld" failure banner. A failed locate
/// used to clear the grid (HasData=false) and bury the reason in the low-contrast
/// top status line — visually identical to the idle app-logo empty state. The fix
/// surfaces the reason in <see cref="LiveWalkerViewModel.LocateFailureMessage"/> as a
/// prominent in-grid banner and suppresses the idle logo while it shows.
/// </summary>
public class LocateGWorldBannerTests
{
    private sealed class PathStub : StubDumpService
    {
        public GWorldPathResult Next = new();
        public override Task<GWorldPathResult> FindPathFromGWorldAsync(
            string target, string? objectAddr = null, int maxDepth = 5, CancellationToken ct = default,
            string rootKind = "gworld")
            => Task.FromResult(Next);
    }

    private static LiveWalkerViewModel MakeVm(PathStub stub)
        => new(stub, new MockLoggingService(), new MockPlatformService(Path.GetTempPath()));

    [Fact]
    public async Task LocateInGWorld_NotReachable_RaisesBanner_AndHidesLogo()
    {
        var stub = new PathStub
        {
            Next = new GWorldPathResult { Found = false, Status = "not_reachable", Visited = 1234 },
        };
        var vm = MakeVm(stub);

        await vm.LocateInGWorldAsync("0x1000", 0, null, stopAtParent: false);

        Assert.True(vm.HasLocateFailure);
        Assert.Contains("Not reachable", vm.LocateFailureMessage);
        Assert.False(vm.HasData);
        Assert.False(vm.ShowEmptyStateLogo);          // banner takes over the empty area
        Assert.True(string.IsNullOrEmpty(vm.StatusText)); // reason is in the banner, not the top line
    }

    [Fact]
    public async Task LocateInGWorld_Cancelled_NoBanner_KeepsStatusLine()
    {
        var stub = new PathStub
        {
            Next = new GWorldPathResult { Found = false, Status = "cancelled" },
        };
        var vm = MakeVm(stub);

        await vm.LocateInGWorldAsync("0x1000", 0, null, stopAtParent: false);

        // A user-initiated cancel must NOT raise the failure banner — it preserves the
        // current view and reports via the mild top status line instead.
        Assert.False(vm.HasLocateFailure);
        Assert.Contains("cancelled", vm.StatusText);
    }

    [Fact]
    public async Task LocateInGWorld_SuccessAfterFailure_ClearsBanner()
    {
        var stub = new PathStub
        {
            Next = new GWorldPathResult { Found = false, Status = "not_reachable", Visited = 1 },
        };
        var vm = MakeVm(stub);

        await vm.LocateInGWorldAsync("0x1000", 0, null, stopAtParent: false);
        Assert.True(vm.HasLocateFailure);

        // A fresh locate attempt clears the prior banner up-front (ClearStatus) even
        // before its own result lands.
        stub.Next = new GWorldPathResult { Found = false, Status = "cancelled" };
        await vm.LocateInGWorldAsync("0x2000", 0, null, stopAtParent: false);

        Assert.False(vm.HasLocateFailure);
    }

    [Fact]
    public async Task LocateInGameEngine_NotReachable_BannerMentionsGameEngine()
    {
        // The engine-rooted variant must surface a GameEngine-specific reason (an
        // engine root reaches engine-layer objects but not most world actors), so
        // the user isn't told to "raise depth" or expect level-list recovery.
        var stub = new PathStub
        {
            Next = new GWorldPathResult { Found = false, Status = "not_reachable", Visited = 42 },
        };
        var vm = MakeVm(stub);

        await vm.LocateInGameEngineAsync("0x1000", 0, null, stopAtParent: false);

        Assert.True(vm.HasLocateFailure);
        Assert.Contains("GameEngine", vm.LocateFailureMessage);
    }

    [Fact]
    public async Task LocateInGameEngine_NoEngine_BannerExplains()
    {
        var stub = new PathStub
        {
            Next = new GWorldPathResult { Found = false, Status = "no_engine" },
        };
        var vm = MakeVm(stub);

        await vm.LocateInGameEngineAsync("0x1000", 0, null, stopAtParent: false);

        Assert.True(vm.HasLocateFailure);
        Assert.Contains("UGameEngine", vm.LocateFailureMessage);
    }

    [Fact]
    public void HasDataBecomingTrue_RetiresBannerStructurally()
    {
        // Some Live Walker nav paths set HasData directly and bypass UpdateDisplay
        // (world-root / container drill / synthetic-container). The banner→clear must
        // be a structural invariant (OnHasDataChanged), not dependent on UpdateDisplay
        // or each caller's ClearStatus discipline.
        var vm = MakeVm(new PathStub());
        vm.LocateFailureMessage = "Not reachable — nothing references this object.";
        Assert.True(vm.HasLocateFailure);

        vm.HasData = true;   // any path that displays real data

        Assert.False(vm.HasLocateFailure);
        Assert.False(vm.ShowEmptyStateLogo);
    }
}
