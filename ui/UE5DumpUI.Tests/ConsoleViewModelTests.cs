using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// VM-level tests for the Console panel — UFUNCTION(exec) discovery
/// (filters on FUNC_Exec = 0x00000200) + one-click invoke.
///
/// Coverage targets:
/// - Load filters to IsExec entries only, sorted by Class then Func.
/// - Filter text matches funcName OR className (case-insensitive
///   substring).
/// - RunSelectedAsync invokes InvokeFunctionAsync with the row's
///   className, no instance addr, and zero parmsSize.
/// - History gets one entry per Run, capped at MaxHistoryEntries.
/// - Commands with NumParms &gt; 0 raise RequestParameterInvoke
///   instead of invoking directly.
/// - RunCommandTextAsync resolves typed name + handles leading slash.
/// </summary>
public class ConsoleViewModelTests
{
    private sealed class FakeDumpService : StubDumpService
    {
        public AllFunctionsResult NextListResult { get; set; } = new();
        public InvokeFunctionResult NextInvokeResult { get; set; } =
            new() { Result = 0, Message = "ProcessEvent OK" };

        public int InvokeCallCount { get; private set; }
        public string LastInvokeClass { get; private set; } = "";
        public string LastInvokeFunc { get; private set; } = "";
        public int LastInvokeParmsSize { get; private set; }
        public string? LastInvokeInstanceAddr { get; private set; }

        public override Task<AllFunctionsResult> ListAllFunctionsAsync(
            bool gameOnly = true, int limit = 100000, CancellationToken ct = default)
        {
            return Task.FromResult(NextListResult);
        }

        public override Task<InvokeFunctionResult> InvokeFunctionAsync(
            string funcName, string? instanceAddr = null, string? className = null,
            int parmsSize = 0, string? paramsHex = null, bool directCall = false,
            CancellationToken ct = default)
        {
            InvokeCallCount++;
            LastInvokeFunc = funcName;
            LastInvokeClass = className ?? "";
            LastInvokeParmsSize = parmsSize;
            LastInvokeInstanceAddr = instanceAddr;
            return Task.FromResult(NextInvokeResult);
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

    // ------------------------------------------------------------------
    // Test data: mix of exec + non-exec entries so the filter is exercised.
    // FUNC_Exec = 0x00000200; FUNC_BlueprintCallable = 0x04000000.
    // ------------------------------------------------------------------

    private const uint FUNC_Exec               = 0x0000_0200;
    private const uint FUNC_BlueprintCallable  = 0x0400_0000;
    private const uint FUNC_Native             = 0x0000_0400;

    private static List<AllFunctionEntry> BuildSampleEntries() => new()
    {
        // 4 exec entries — different classes + arities
        new() { ClassName="UCheatManager", FuncName="Fly",
                FunctionFlags=FUNC_Exec | FUNC_Native, NumParms=0, ParmsSize=0 },
        new() { ClassName="UCheatManager", FuncName="God",
                FunctionFlags=FUNC_Exec | FUNC_Native, NumParms=0, ParmsSize=0 },
        new() { ClassName="MyGameCheatMgr", FuncName="GiveItem",
                FunctionFlags=FUNC_Exec, NumParms=1, ParmsSize=4 },
        new() { ClassName="PlayerController", FuncName="DebugTeleport",
                FunctionFlags=FUNC_Exec | FUNC_BlueprintCallable, NumParms=3, ParmsSize=12 },

        // Non-exec — should be excluded from the results
        new() { ClassName="PlayerCharacter", FuncName="AddMoney",
                FunctionFlags=FUNC_BlueprintCallable, NumParms=2, ParmsSize=8 },
        new() { ClassName="GameMode", FuncName="StartPlay",
                FunctionFlags=FUNC_BlueprintCallable, NumParms=0, ParmsSize=0 },
    };

    private static ConsoleViewModel CreateVm(FakeDumpService fake)
    {
        var log = new NoopLogger();
        return new ConsoleViewModel(fake, log);
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Load_filters_to_exec_only_and_sorts_by_class_then_func()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult
            {
                Total = 6,
                ScannedObjects = 100,
                ScannedClasses = 5,
                TotalFunctions = 6,
                Functions = BuildSampleEntries(),
            }
        };
        var vm = CreateVm(fake);

        await vm.LoadCommand.ExecuteAsync(null);

        // Exec entries only: 4 of the 6 inputs
        Assert.Equal(4, vm.Results.Count);
        // Sorted by class, then func
        Assert.Equal("MyGameCheatMgr",     vm.Results[0].ClassName);
        Assert.Equal("PlayerController",   vm.Results[1].ClassName);
        Assert.Equal("UCheatManager",      vm.Results[2].ClassName);
        Assert.Equal("UCheatManager",      vm.Results[3].ClassName);
        Assert.Equal("Fly",                vm.Results[2].FuncName);
        Assert.Equal("God",                vm.Results[3].FuncName);
        // Every surfaced row IS exec
        Assert.All(vm.Results, r => Assert.True(r.IsExec));
    }

    [Fact]
    public async Task Load_with_no_exec_entries_reports_helpful_status()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult
            {
                Total = 2,
                ScannedObjects = 50,
                ScannedClasses = 1,
                TotalFunctions = 2,
                Functions = new List<AllFunctionEntry>
                {
                    new() { ClassName="A", FuncName="X", FunctionFlags=FUNC_BlueprintCallable },
                    new() { ClassName="B", FuncName="Y", FunctionFlags=FUNC_Native },
                }
            }
        };
        var vm = CreateVm(fake);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(vm.Results);
        Assert.Contains("No UFUNCTION(exec)", vm.StatusText);
    }

    [Fact]
    public async Task FilterText_matches_funcName_substring_case_insensitive()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult
            {
                Functions = BuildSampleEntries(),
                Total = 6, ScannedClasses = 5,
            }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.FilterText = "GIVE";
        Assert.Single(vm.Results);
        Assert.Equal("GiveItem", vm.Results[0].FuncName);

        vm.FilterText = "fly";
        Assert.Single(vm.Results);
        Assert.Equal("Fly", vm.Results[0].FuncName);
    }

    [Fact]
    public async Task FilterText_matches_className_substring_too()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.FilterText = "CheatMgr";
        Assert.Single(vm.Results);
        Assert.Equal("MyGameCheatMgr", vm.Results[0].ClassName);
    }

    [Fact]
    public async Task RunSelected_with_no_param_exec_invokes_directly_and_appends_history()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        // Pick UCheatManager::Fly (no params) — index 2 after the sort
        vm.SelectedResult = vm.Results[2];
        Assert.Equal("Fly", vm.SelectedResult.FuncName);

        await vm.RunSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.InvokeCallCount);
        Assert.Equal("UCheatManager", fake.LastInvokeClass);
        Assert.Equal("Fly", fake.LastInvokeFunc);
        Assert.Equal(0, fake.LastInvokeParmsSize);
        Assert.Null(fake.LastInvokeInstanceAddr);
        Assert.Single(vm.History);
        Assert.True(vm.History[0].Success);
        Assert.Equal("UCheatManager", vm.History[0].ClassName);
        Assert.Equal("Fly", vm.History[0].FuncName);
    }

    [Fact]
    public async Task RunSelected_with_params_raises_RequestParameterInvoke_and_skips_pipe()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        string? capturedClass = null;
        string? capturedFunc = null;
        vm.RequestParameterInvoke += (cls, fn) =>
        {
            capturedClass = cls;
            capturedFunc = fn;
        };

        // GiveItem (NumParms=1) is the first row after the sort.
        vm.SelectedResult = vm.Results[0];
        Assert.Equal("GiveItem", vm.SelectedResult.FuncName);

        await vm.RunSelectedCommand.ExecuteAsync(null);

        Assert.Equal("MyGameCheatMgr", capturedClass);
        Assert.Equal("GiveItem", capturedFunc);
        Assert.Equal(0, fake.InvokeCallCount);
        Assert.Empty(vm.History);
    }

    [Fact]
    public async Task RunSelected_with_no_selection_sets_helpful_status()
    {
        var fake = new FakeDumpService { NextListResult = new AllFunctionsResult() };
        var vm = CreateVm(fake);

        Assert.Null(vm.SelectedResult);
        await vm.RunSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Pick an exec command", vm.StatusText);
        Assert.Equal(0, fake.InvokeCallCount);
    }

    [Fact]
    public async Task RunCommandText_resolves_typed_name_and_strips_leading_slash()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.CommandInput = "/fly";
        await vm.RunCommandTextCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.InvokeCallCount);
        Assert.Equal("Fly", fake.LastInvokeFunc);
    }

    [Fact]
    public async Task RunCommandText_unknown_command_reports_friendly_error()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.CommandInput = "nonexistent";
        await vm.RunCommandTextCommand.ExecuteAsync(null);

        Assert.Contains("No exec command named 'nonexistent'", vm.StatusText);
        Assert.Equal(0, fake.InvokeCallCount);
    }

    [Fact]
    public async Task RunCommandText_with_inline_args_routes_to_param_dialog()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        string? capturedFunc = null;
        vm.RequestParameterInvoke += (_, fn) => capturedFunc = fn;

        // "giveitem 5" — GiveItem takes 1 param; we don't parse "5"
        // yet (FString-input gap), so route to dialog.
        vm.CommandInput = "giveitem 5";
        await vm.RunCommandTextCommand.ExecuteAsync(null);

        Assert.Equal("GiveItem", capturedFunc);
        Assert.Equal(0, fake.InvokeCallCount);
    }

    [Fact]
    public async Task History_appends_failed_invocations_with_error_text()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() },
            NextInvokeResult = new InvokeFunctionResult
            {
                Result = -5,
                Error = "ProcessEvent error code -5 (game-thread dispatch timeout)",
            }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedResult = vm.Results[2]; // Fly
        await vm.RunSelectedCommand.ExecuteAsync(null);

        Assert.Single(vm.History);
        Assert.False(vm.History[0].Success);
        Assert.Contains("dispatch timeout", vm.History[0].ResultText);
        Assert.Equal("Err", vm.History[0].Badge);
    }

    [Fact]
    public async Task History_caps_at_max_entries()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        // Fly is no-arg — fire it 25 times. MaxHistoryEntries is 20.
        vm.SelectedResult = vm.Results[2];
        for (int i = 0; i < 25; i++)
            await vm.RunSelectedCommand.ExecuteAsync(null);

        Assert.Equal(20, vm.History.Count);
        // Newest first
        Assert.Equal("Fly", vm.History[0].FuncName);
    }

    [Fact]
    public async Task ReplayHistory_re_runs_the_same_command()
    {
        var fake = new FakeDumpService
        {
            NextListResult = new AllFunctionsResult { Functions = BuildSampleEntries() }
        };
        var vm = CreateVm(fake);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SelectedResult = vm.Results[2]; // Fly
        await vm.RunSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.InvokeCallCount);

        await vm.ReplayHistoryCommand.ExecuteAsync(vm.History[0]);
        Assert.Equal(2, fake.InvokeCallCount);
    }

    [Fact]
    public void SeedForTests_filters_and_sorts_outside_the_pipe()
    {
        var fake = new FakeDumpService();
        var vm = CreateVm(fake);

        vm.SeedForTests(BuildSampleEntries());

        Assert.Equal(4, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.True(r.IsExec));
        // Sort order locked
        Assert.Equal("MyGameCheatMgr",   vm.Results[0].ClassName);
        Assert.Equal("PlayerController", vm.Results[1].ClassName);
    }

    [Fact]
    public void IsExec_decoder_matches_FUNC_Exec_bit()
    {
        // Belt-and-braces guard against the wrong bit literal — historically
        // 0x4 was confused for FUNC_Exec (it's actually FUNC_BlueprintAuthorityOnly).
        // UE5 ObjectMacros.h: FUNC_Exec = 0x00000200.
        var exec = new AllFunctionEntry { FunctionFlags = 0x0000_0200 };
        Assert.True(exec.IsExec);

        var nonExec = new AllFunctionEntry { FunctionFlags = 0x0000_0004 };
        Assert.False(nonExec.IsExec);

        var execWithMore = new AllFunctionEntry { FunctionFlags = 0x0400_0200 }; // exec + BlueprintCallable
        Assert.True(execWithMore.IsExec);

        // ShortFlags should include "Exec" when the bit is set
        Assert.Contains("Exec", exec.ShortFlags);
        Assert.DoesNotContain("Exec", nonExec.ShortFlags);
    }
}
