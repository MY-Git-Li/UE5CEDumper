using System.Linq;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// VM-level tests for the Live ProcessEvent Call Profiler (Live Funcs panel).
/// Exercises Start/Stop/Refresh/Clear, the "no PE hook" warning, the client-side
/// keyword filter (space = AND), and the cross-tab handoffs. The actual PE
/// counting is DLL-side and verified in-game (Fern has no unit tests).
/// </summary>
public class LiveFuncsViewModelTests
{
    private sealed class FakeDumpService : StubDumpService
    {
        public bool StartHookActive { get; set; } = true;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int GetCalls { get; private set; }
        public PeProfileResult NextGet { get; set; } = new();

        public override Task<bool> PeProfileStartAsync(CancellationToken ct = default)
        {
            StartCalls++;
            return Task.FromResult(StartHookActive);
        }
        public override Task PeProfileStopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
        public override Task<PeProfileResult> PeProfileGetAsync(int limit = 200, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(NextGet);
        }
    }

    private sealed class NoopLogger : ILoggingService
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
        public void Debug(string message) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message) { }
        public void Error(string category, string message, Exception ex) { }
        public void Debug(string category, string message) { }
        public void StartProcessMirror(string processName) { }
        public void StopProcessMirror() { }
    }

    private static (LiveFuncsViewModel vm, FakeDumpService dump) MakeVm()
    {
        var dump = new FakeDumpService();
        var vm = new LiveFuncsViewModel(dump, new NoopLogger());
        return (vm, dump);
    }

    private static PeProfileResult ResultOf(params PeProfileEntry[] entries)
        => new()
        {
            Recording     = false,
            DistinctFuncs = entries.Length,
            TotalCalls    = entries.Sum(e => e.Count),
            Entries       = entries.ToList(),
        };

    // ==================================================================
    // Start / Stop
    // ==================================================================

    [Fact]
    public async Task Start_HookActive_SetsRecordingAndReadyStatus()
    {
        var (vm, dump) = MakeVm();
        dump.StartHookActive = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRecording);
        Assert.Equal(1, dump.StartCalls);
        Assert.Contains("Recording", vm.StatusText);
    }

    [Fact]
    public async Task Start_NoHook_WarnsCountsStayZero()
    {
        var (vm, dump) = MakeVm();
        dump.StartHookActive = false;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRecording);
        Assert.Contains("no PE hook", vm.StatusText);
    }

    [Fact]
    public async Task Stop_FetchesAndPopulatesRanked()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "AShopVendor", FuncName = "OpenShop", Count = 3 },
            new PeProfileEntry { ClassName = "APawn",       FuncName = "Tick",     Count = 900 });

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Equal(1, dump.StopCalls);
        Assert.Equal(1, dump.GetCalls);
        Assert.Equal(2, vm.Results.Count);
        // DLL already ranks by count desc; the VM preserves that order.
        Assert.Equal("OpenShop", vm.Results[0].FuncName);
        Assert.Contains("2 distinct", vm.StatusText);
    }

    [Fact]
    public async Task Stop_WhenNotRecording_IsNoOp()
    {
        var (vm, dump) = MakeVm();
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(0, dump.StopCalls);
        Assert.Equal(0, dump.GetCalls);
    }

    [Fact]
    public async Task Stop_EmptyRecording_ExplainsZero()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(); // nothing fired

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(vm.Results);
        Assert.Contains("No UFunctions recorded", vm.StatusText);
    }

    // ==================================================================
    // Filter (space = AND over func + class) + Clear
    // ==================================================================

    [Fact]
    public async Task Filter_SpaceIsAnd_OverFuncAndClass()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "AShopVendor", FuncName = "OpenPanel", Count = 5 }, // open + shop(class)
            new PeProfileEntry { ClassName = "AMenu",       FuncName = "OpenPanel", Count = 4 }); // open only

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Results.Count);

        vm.FilterText = "open shop";   // AND: needs both terms across func OR class
        Assert.Single(vm.Results);
        Assert.Equal("AShopVendor", vm.Results[0].ClassName);
    }

    [Fact]
    public async Task Clear_EmptiesResultsAndFilter()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "A", FuncName = "F", Count = 1 });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        vm.FilterText = "F";

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Results);
        Assert.Equal("", vm.FilterText);
    }

    // ==================================================================
    // Cross-tab handoffs + auto-stop
    // ==================================================================

    [Fact]
    public void OpenInLiveWalker_RaisesNavigateWithClassAndFunc()
    {
        var (vm, _) = MakeVm();
        (string cls, string func)? got = null;
        vm.NavigateToFunction += (c, f) => got = (c, f);

        vm.OpenInLiveWalkerCommand.Execute(
            new PeProfileEntry { ClassName = "AShopVendor", FuncName = "OpenShop" });

        Assert.Equal(("AShopVendor", "OpenShop"), got);
    }

    [Fact]
    public void CopyFuncName_RaisesRequestCopyText()
    {
        var (vm, _) = MakeVm();
        string? copied = null;
        vm.RequestCopyText += t => copied = t;

        vm.CopyFuncNameCommand.Execute(
            new PeProfileEntry { ClassName = "A", FuncName = "OpenShop" });

        Assert.Equal("OpenShop", copied);
    }

    [Fact]
    public async Task OnLeavingTab_WhileRecording_AutoStops()
    {
        var (vm, dump) = MakeVm();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRecording);

        vm.OnLeavingTab();
        // Auto-stop is fire-and-forget; give the continuation a beat to run.
        await Task.Delay(50);

        Assert.False(vm.IsRecording);
        Assert.True(dump.StopCalls >= 1);
    }

    [Fact]
    public void OnLeavingTab_WhenNotRecording_DoesNotStop()
    {
        var (vm, dump) = MakeVm();
        vm.OnLeavingTab();
        Assert.Equal(0, dump.StopCalls);
    }

    // ==================================================================
    // Model
    // ==================================================================

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(2, 5, "2 (5B)")]
    [InlineData(1, 8, "1 (8B)")]
    public void PeProfileEntry_ParamsLabel_Formats(byte numParms, ushort parmsSize, string expected)
    {
        var e = new PeProfileEntry { NumParms = numParms, ParmsSize = parmsSize };
        Assert.Equal(expected, e.ParamsLabel);
    }
}
