using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// VM-level tests for the Teleport panel: connection gating, error-code →
/// message mapping (incl. the -7 force flow), marker-list refresh on connect,
/// and hotkey-scheme selection. All over a fake IDumpService.
/// </summary>
public class TeleportViewModelTests
{
    private sealed class FakeDumpService : StubDumpService
    {
        public TeleportPose NextPose { get; set; } = new() { Code = 0 };
        public TeleportResult NextResult { get; set; } = new() { Code = 0, Tier = 1 };
        public List<TeleportMarker> NextMarkers { get; set; } = new();

        public int GetPoseCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RecallCalls { get; private set; }
        public bool LastForce { get; private set; }
        public int ClearCalls { get; private set; }
        public int CursorCalls { get; private set; }
        public int GetMarkersCalls { get; private set; }
        public (double X, double Y, double Z, double? P)? LastExplicit { get; private set; }

        public override Task<TeleportPose> TeleportGetPoseAsync(CancellationToken ct = default)
        { GetPoseCalls++; return Task.FromResult(NextPose); }

        public override Task<TeleportPose> TeleportSaveMarkerAsync(int slot, CancellationToken ct = default)
        { SaveCalls++; return Task.FromResult(NextPose); }

        public override Task<TeleportResult> TeleportRecallMarkerAsync(int slot, bool force, CancellationToken ct = default)
        { RecallCalls++; LastForce = force; return Task.FromResult(NextResult); }

        public override Task<TeleportResult> TeleportRecallExplicitAsync(double x, double y, double z,
            double? pitch = null, double? yaw = null, double? roll = null, CancellationToken ct = default)
        { LastExplicit = (x, y, z, pitch); return Task.FromResult(NextResult); }

        public override Task<TeleportResult> TeleportToCursorAsync(double zOffset, int channel, bool fallbackCenter, CancellationToken ct = default)
        { CursorCalls++; return Task.FromResult(NextResult); }

        public override Task<List<TeleportMarker>> TeleportGetMarkersAsync(CancellationToken ct = default)
        { GetMarkersCalls++; return Task.FromResult(NextMarkers); }

        public override Task<int> TeleportClearMarkerAsync(int slot, CancellationToken ct = default)
        { ClearCalls++; return Task.FromResult(0); }
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

    private sealed class FakePlatform : IPlatformService
    {
        // Unique per-instance AppData root so the marker-hotkey store file from
        // one test never bleeds into another test's VM load (tests run parallel).
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "ue5cd-vmtest-" + Guid.NewGuid().ToString("N"));
        public string? LastClipboard { get; private set; }
        public bool TryAcquireSingleInstance() => true;
        public void ReleaseSingleInstance() { }
        public string GetAppDataPath() => _dir;
        public string GetLogDirectoryPath() => _dir;
        public Task CopyToClipboardAsync(string text) { LastClipboard = text; return Task.CompletedTask; }
        public Task RevealInExplorerAsync(string path) => Task.CompletedTask;
        public string GetMachineName() => "TEST";
        public void CloseImeForWindow(IntPtr windowHandle) { }
        public Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string filterExtension)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeHotkeyService : IGlobalHotkeyService
    {
        public List<(uint Mods, uint Vk, string Label)> Registered { get; } = new();
        public IGlobalHotkeyRegistration? RegisterCursorHotkey(Action onPressed)
            => new FakeReg("Ctrl+F8");
        public IGlobalHotkeyRegistration? RegisterSpecific(uint modifiers, uint vk, string label, Action onPressed)
        {
            Registered.Add((modifiers, vk, label));
            return new FakeReg(label);
        }
        private sealed class FakeReg : IGlobalHotkeyRegistration
        {
            public FakeReg(string label) => Label = label;
            public string Label { get; }
            public void Dispose() { }
        }
    }

    private static TeleportViewModel CreateVm(FakeDumpService fake, out FakePlatform platform,
        IGlobalHotkeyService? hotkeys = null)
    {
        platform = new FakePlatform();
        return new TeleportViewModel(fake, new NoopLogger(), platform, aobMaker: null, globalHotkeys: hotkeys);
    }

    [Fact]
    public void Starts_disconnected_with_three_markers()
    {
        var vm = CreateVm(new FakeDumpService(), out _);
        Assert.False(vm.IsConnected);
        Assert.False(vm.CanOperate);
        Assert.Equal(3, vm.Markers.Count);
        Assert.All(vm.Markers, m => Assert.Equal("(empty)", m.Summary));
    }

    [Fact]
    public void SetConnected_refreshes_markers()
    {
        var fake = new FakeDumpService
        {
            NextMarkers = new()
            {
                new() { Slot = 0, Valid = true, X = 10, Y = 20, Z = 30, Map = "Act1" },
                new() { Slot = 1, Valid = false },
                new() { Slot = 2, Valid = false },
            },
        };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        Assert.True(vm.IsConnected);
        Assert.True(vm.CanOperate);
        Assert.Equal(1, fake.GetMarkersCalls);
        Assert.True(vm.Markers[0].Valid);
        Assert.Contains("Act1", vm.Markers[0].Summary);
        Assert.False(vm.Markers[1].Valid);
    }

    [Fact]
    public async Task RefreshPose_populates_display_on_success()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, X = 1.5, Y = 2.5, Z = 3.5, Yaw = 90, Map = "World1", Source = "raw" },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RefreshPoseCommand.ExecuteAsync(null);

        Assert.Equal("1.500", vm.PoseX);
        Assert.Equal("90.00", vm.PoseYaw);
        Assert.Equal("World1", vm.PoseMap);
        Assert.Equal("raw", vm.PoseSource);
    }

    [Fact]
    public async Task RefreshPose_shows_hint_on_error_code()
    {
        var fake = new FakeDumpService { NextPose = new() { Code = TeleportCodes.NoPawn } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RefreshPoseCommand.ExecuteAsync(null);

        Assert.Equal("—", vm.PoseX);
        Assert.Contains("pawn", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recall_maps_minus7_to_force_hint_and_does_not_force()
    {
        var fake = new FakeDumpService
        {
            NextResult = new() { Code = TeleportCodes.MapMismatch, CurrentMap = "Act2", MarkerMap = "Act1" },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RecallMarkerCommand.ExecuteAsync(0);

        Assert.False(fake.LastForce);
        Assert.Contains("Force", vm.StatusText);
        Assert.Contains("Act1", vm.StatusText);
    }

    [Fact]
    public async Task ForceRecall_passes_force_true()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 1 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.ForceRecallMarkerCommand.ExecuteAsync(1);

        Assert.True(fake.LastForce);
        Assert.Equal(1, fake.RecallCalls);
    }

    [Fact]
    public async Task Recall_tier2_warns_about_snap_back()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 2 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RecallMarkerCommand.ExecuteAsync(0);

        Assert.Contains("snap back", vm.StatusText);
    }

    [Fact]
    public async Task CopyAsBugItGo_copies_formatted_string()
    {
        var fake = new FakeDumpService { NextPose = new() { Code = 0, X = 100, Y = 200, Z = 300 } };
        var vm = CreateVm(fake, out var platform);
        vm.IsConnected = true;

        await vm.CopyAsBugItGoCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.StartsWith("BugItGo 100.000 200.000 300.000", platform.LastClipboard);
    }

    [Fact]
    public async Task RunBugItGo_parses_and_recalls_explicit()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 1 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.BugItGoInput = "BugItGo 5 6 7";

        await vm.RunBugItGoCommand.ExecuteAsync(null);

        Assert.NotNull(fake.LastExplicit);
        Assert.Equal(5, fake.LastExplicit!.Value.X, 3);
        Assert.Equal(7, fake.LastExplicit.Value.Z, 3);
    }

    [Fact]
    public async Task RunBugItGo_rejects_garbage_without_calling_dll()
    {
        var fake = new FakeDumpService();
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.BugItGoInput = "not a coordinate";

        await vm.RunBugItGoCommand.ExecuteAsync(null);

        Assert.Null(fake.LastExplicit);
        Assert.Contains("parse", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Marker_hotkey_rows_are_six_save_and_recall()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        Assert.Equal(6, vm.HotkeyRows.Count);
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "save0" && r.DisplayName == "Save marker 1");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "recall2" && r.DisplayName == "Recall marker 3");
        Assert.All(vm.HotkeyRows, r => Assert.False(r.HasBinding));
    }

    [Fact]
    public void Capture_assigns_combo_and_registers_global_hotkey()
    {
        var hk = new FakeHotkeyService();
        var vm = CreateVm(new FakeDumpService(), out _, hk);
        var row = vm.HotkeyRows.First(r => r.ActionId == "save0");

        vm.BeginCaptureCommand.Execute(row);
        Assert.True(vm.IsCapturingHotkey);

        // Hold Ctrl, press F7 → Ctrl+F7 (Win32 MOD_CONTROL=2, VK_F7=0x76).
        bool handled = vm.ApplyCapturedKey(Avalonia.Input.Key.F7, Avalonia.Input.KeyModifiers.Control);

        Assert.True(handled);
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Ctrl+F7", row.Label);
        Assert.Contains(hk.Registered, x => x.Mods == 2 && x.Vk == 0x76 && x.Label == "Ctrl+F7");
    }

    [Fact]
    public void Capture_modifier_only_key_keeps_listening()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        var row = vm.HotkeyRows.First();
        vm.BeginCaptureCommand.Execute(row);

        // LeftCtrl alone is not a bindable key → not handled, still capturing.
        bool handled = vm.ApplyCapturedKey(Avalonia.Input.Key.LeftCtrl, Avalonia.Input.KeyModifiers.Control);
        Assert.False(handled);
        Assert.True(vm.IsCapturingHotkey);
    }

    [Fact]
    public void Capture_escape_cancels()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        var row = vm.HotkeyRows.First();
        vm.BeginCaptureCommand.Execute(row);

        bool handled = vm.ApplyCapturedKey(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None);
        Assert.True(handled);
        Assert.False(vm.IsCapturingHotkey);
        Assert.False(row.HasBinding);
    }

    [Fact]
    public void Clear_hotkey_removes_binding()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        var row = vm.HotkeyRows.First();
        vm.BeginCaptureCommand.Execute(row);
        vm.ApplyCapturedKey(Avalonia.Input.Key.F5, Avalonia.Input.KeyModifiers.None);
        Assert.True(row.HasBinding);

        vm.ClearHotkeyCommand.Execute(row);
        Assert.False(row.HasBinding);
    }

    [Fact]
    public async Task Operations_noop_when_disconnected()
    {
        var fake = new FakeDumpService();
        var vm = CreateVm(fake, out _);
        // not connected
        await vm.RefreshPoseCommand.ExecuteAsync(null);
        await vm.SaveMarkerCommand.ExecuteAsync(0);
        await vm.TeleportToCursorCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.GetPoseCalls);
        Assert.Equal(0, fake.SaveCalls);
        Assert.Equal(0, fake.CursorCalls);
    }

    [Fact]
    public void Disconnect_turns_off_auto_refresh()
    {
        var vm = CreateVm(new FakeDumpService(), out _);
        vm.SetConnected(true);
        vm.AutoRefresh = true;
        vm.SetConnected(false);
        Assert.False(vm.AutoRefresh);
        Assert.False(vm.IsConnected);
    }
}
