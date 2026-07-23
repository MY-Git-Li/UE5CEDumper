using System.Collections.ObjectModel;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Cancellation semantics for the Live Walker "Copy CE XML" / "Copy CE Field" exports.
/// The drilldown pipe-walk is abortable via a per-export CancellationTokenSource; the
/// key subtlety is that the VM's <c>catch (OperationCanceledException)</c> is guarded
/// with <c>when (cts.IsCancellationRequested)</c> so that a BARE OperationCanceledException
/// NOT raised by our token — e.g. PipeClient's "Pipe disconnected during send" — is
/// reported as a real error, not silently mislabeled as a user cancellation.
/// </summary>
public class LiveWalkerExportCancelTests
{
    // Stub whose WalkInstanceAsync runs an optional pre-throw hook then throws a supplied
    // exception. Lets a test simulate either a user cancel (hook cancels the export, then
    // the in-flight OCE surfaces with the token cancelled) or a raw pipe disconnect (bare
    // OCE, token NOT cancelled).
    private sealed class WalkThrowStub : StubDumpService
    {
        public Action? BeforeThrow;
        public Func<Exception> Make = () => new OperationCanceledException();

        public override Task<InstanceWalkResult> WalkInstanceAsync(string addr, string? classAddr = null,
            int arrayLimit = 64, int previewLimit = 2, bool fillGaps = false, bool lean = false,
            CancellationToken ct = default)
        {
            BeforeThrow?.Invoke();
            throw Make();
        }
    }

    private static LiveWalkerViewModel MakeVm(WalkThrowStub dump)
    {
        var vm = new LiveWalkerViewModel(dump, new MockLoggingService(),
            new MockPlatformService(Path.GetTempPath()))
        {
            CurrentAddress = "0x1000",
            Breadcrumbs = new ObservableCollection<BreadcrumbItem>
            {
                new() { Address = "0x1000", Label = "Root", FieldName = "Root" },
            },
            // A StructProperty field forces a WalkInstance during ResolveDrilldown (structs
            // resolve regardless of drill depth), so the export always reaches the pipe call.
            Fields = new ObservableCollection<LiveFieldValue>
            {
                new() { Name = "S", TypeName = "StructProperty", Offset = 0,
                        StructClassAddr = "0xCLS", StructDataAddr = "0xDATA" },
            },
        };
        return vm;
    }

    [Fact]
    public async Task ExportCeXml_UserCancel_ReportsCancelled()
    {
        var dump = new WalkThrowStub();
        var vm = MakeVm(dump);
        // Simulate the user hitting Cancel mid-walk: cancel the export, then let the
        // in-flight OCE surface — now cts.IsCancellationRequested is true.
        dump.BeforeThrow = () => vm.CancelExportCommand.Execute(null);

        await vm.ExportCeXmlCommand.ExecuteAsync(null);

        Assert.Equal("Export cancelled.", vm.StatusText);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsExporting);
    }

    [Fact]
    public async Task ExportCeXml_PipeDisconnect_NotMislabeledAsCancel()
    {
        // Regression (review 2026-07-07): a mid-export pipe disconnect surfaces as a BARE
        // OperationCanceledException ("Pipe disconnected during send") that is NOT from the
        // export token. It must be reported as an error, never as a user cancellation.
        var dump = new WalkThrowStub { Make = () => new OperationCanceledException("Pipe disconnected during send") };
        var vm = MakeVm(dump);

        await vm.ExportCeXmlCommand.ExecuteAsync(null);

        Assert.NotEqual("Export cancelled.", vm.StatusText);
        Assert.Equal("Pipe disconnected during send", vm.ErrorMessage);
        Assert.False(vm.IsExporting);
    }

    [Fact]
    public async Task ExportCeField_PipeDisconnect_NotMislabeledAsCancel()
    {
        // Same guard on the Copy CE Field command (shares the guarded catch).
        var dump = new WalkThrowStub { Make = () => new OperationCanceledException("Pipe disconnected during send") };
        var vm = MakeVm(dump);
        vm.UpdateSelectedFields(new List<LiveFieldValue>(vm.Fields));

        await vm.ExportCeFieldXmlCommand.ExecuteAsync(null);

        Assert.NotEqual("Export cancelled.", vm.StatusText);
        Assert.Equal("Pipe disconnected during send", vm.ErrorMessage);
        Assert.False(vm.IsExporting);
    }
}
