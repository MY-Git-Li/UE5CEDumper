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
        // Ranked by call count desc (non-diff mode): Tick (900) tops OpenShop (3).
        // (The action-specific low-count OpenShop is what Diff mode surfaces instead.)
        Assert.Equal("Tick", vm.Results[0].FuncName);
        Assert.Contains(vm.Results, r => r.FuncName == "OpenShop");
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
    // Baseline diff (isolate the action's function from Tick noise)
    // ==================================================================

    [Fact]
    public async Task Diff_SetBaselineThenAction_SurfacesNewFunctionOnTop()
    {
        var (vm, dump) = MakeVm();
        // Idle baseline: per-frame noise, no shop function.
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "APawn", FuncName = "Tick",   Count = 900 },
            new PeProfileEntry { ClassName = "AHUD",  FuncName = "Update", Count = 300 });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        vm.SetBaselineCommand.Execute(null);
        Assert.True(vm.DiffMode);

        // Action: Tick keeps firing (higher), Update unchanged, PLUS a NEW OpenShop.
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "APawn",       FuncName = "Tick",     Count = 1000 },
            new PeProfileEntry { ClassName = "AHUD",        FuncName = "Update",   Count = 300 },
            new PeProfileEntry { ClassName = "AShopVendor", FuncName = "OpenShop", Count = 2 });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        // NewChangedOnly defaults on → the unchanged Update (Δ0) is hidden.
        Assert.DoesNotContain(vm.Results, r => r.FuncName == "Update");
        // NEW function ranks first, ahead of the +100 Tick.
        Assert.Equal("OpenShop", vm.Results[0].FuncName);
        Assert.True(vm.Results[0].IsNew);
        Assert.Equal("NEW", vm.Results[0].DeltaLabel);
        var tick = vm.Results.First(r => r.FuncName == "Tick");
        Assert.Equal(100, tick.Delta);
        Assert.Equal("+100", tick.DeltaLabel);
        Assert.Contains("NEW", vm.StatusText);
    }

    [Fact]
    public async Task Diff_NewChangedOnlyOff_ShowsUnchangedRowsToo()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(new PeProfileEntry { ClassName = "AHUD", FuncName = "Update", Count = 300 });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        vm.SetBaselineCommand.Execute(null);

        dump.NextGet = ResultOf(new PeProfileEntry { ClassName = "AHUD", FuncName = "Update", Count = 300 });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Results, r => r.FuncName == "Update"); // Δ0 hidden by default
        vm.NewChangedOnly = false;
        Assert.Contains(vm.Results, r => r.FuncName == "Update");       // now visible
    }

    [Fact]
    public void SetBaseline_WithNoData_DoesNotEnableDiff()
    {
        var (vm, _) = MakeVm();
        vm.SetBaselineCommand.Execute(null);
        Assert.False(vm.DiffMode);
    }

    [Fact]
    public void ClearBaseline_ResetsDiffMode()
    {
        var (vm, _) = MakeVm();
        vm.DiffMode = true;
        vm.ClearBaselineCommand.Execute(null);
        Assert.False(vm.DiffMode);
    }

    [Theory]
    [InlineData(false, 0L, "")]
    [InlineData(false, 5L, "+5")]
    [InlineData(false, -3L, "-3")]
    [InlineData(true, 7L, "NEW")]
    public void PeProfileEntry_DeltaLabel_Formats(bool isNew, long delta, string expected)
    {
        var e = new PeProfileEntry { IsNew = isNew, Delta = delta };
        Assert.Equal(expected, e.DeltaLabel);
    }

    [Fact]
    public async Task HideWidgets_RemovesTransientWidgetMethods()
    {
        // A shop-open recording: the widget's Construct fires (is_widget) alongside
        // the persistent controller's opener. Hiding widgets leaves the opener.
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "DOLLShopStoreLayout", FuncName = "Construct",
                                 Count = 1, IsWidget = true },
            new PeProfileEntry { ClassName = "AShopController", FuncName = "OpenShop",
                                 Count = 2, IsWidget = false });
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Results.Count);

        vm.HideWidgets = true;
        Assert.DoesNotContain(vm.Results, r => r.ClassName == "DOLLShopStoreLayout");
        Assert.Contains(vm.Results, r => r.FuncName == "OpenShop");
    }

    [Theory]
    [InlineData(true, "UI")]
    [InlineData(false, "")]
    public void PeProfileEntry_Kind_ReflectsIsWidget(bool isWidget, string expected)
    {
        var e = new PeProfileEntry { IsWidget = isWidget };
        Assert.Equal(expected, e.Kind);
    }

    // FUNC_Event=0x800, FUNC_MulticastDelegate=0x10000, FUNC_BlueprintCallable=0x04000000, FUNC_Native=0x400.
    [Theory]
    [InlineData(0x0000_0800u, true,  "Event")]   // BP event (On*)
    [InlineData(0x0001_0000u, true,  "Deleg")]   // multicast delegate (OnHit etc.)
    [InlineData(0x0400_0000u, false, "Call")]    // imperative BlueprintCallable — the kind we want
    [InlineData(0x0000_0400u, false, "native")]
    public void PeProfileEntry_TypeAndEventLike_FromFlags(uint flags, bool eventLike, string label)
    {
        var e = new PeProfileEntry { FunctionFlags = flags };
        Assert.Equal(eventLike, e.IsEventLike);
        Assert.Equal(label, e.TypeLabel);
    }

    [Fact]
    public async Task HideEvents_RemovesEventAndDelegateRows()
    {
        var (vm, dump) = MakeVm();
        dump.NextGet = ResultOf(
            new PeProfileEntry { ClassName = "AVolume",  FuncName = "OnStartSkit", Count = 7,
                                 FunctionFlags = 0x0000_0800 },   // Event
            new PeProfileEntry { ClassName = "AShopMgr", FuncName = "OpenShop",    Count = 1,
                                 FunctionFlags = 0x0400_0000 });  // BlueprintCallable
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Results.Count);

        vm.HideEvents = true;
        Assert.DoesNotContain(vm.Results, r => r.FuncName == "OnStartSkit");
        Assert.Contains(vm.Results, r => r.FuncName == "OpenShop");
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
