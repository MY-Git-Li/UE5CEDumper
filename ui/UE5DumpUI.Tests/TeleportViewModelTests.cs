using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
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
        public TeleportPov NextPov { get; set; } = new() { Code = 0 };
        public int GetPovCalls { get; private set; }

        public int GetPoseCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RecallCalls { get; private set; }
        public bool LastForce { get; private set; }
        public int ClearCalls { get; private set; }
        public int CursorCalls { get; private set; }
        public int GetMarkersCalls { get; private set; }
        public int RecallLastCalls { get; private set; }
        public (double X, double Y, double Z, double? P)? LastExplicit { get; private set; }

        public int NextDebugCameraState { get; set; } = -1;
        public int SetDebugCameraCalls { get; private set; }
        public int GetDebugCameraCalls { get; private set; }
        public bool? LastSetDebugCameraEnable { get; private set; }

        public override Task<int> SetDebugCameraAsync(bool enable, CancellationToken ct = default)
        { SetDebugCameraCalls++; LastSetDebugCameraEnable = enable; return Task.FromResult(NextDebugCameraState); }

        public override Task<int> GetDebugCameraStateAsync(CancellationToken ct = default)
        { GetDebugCameraCalls++; return Task.FromResult(NextDebugCameraState); }

        public int NextGodModeState { get; set; } = -1;
        public int SetGodModeCalls { get; private set; }
        public int GetGodModeCalls { get; private set; }
        public bool? LastSetGodModeEnable { get; private set; }

        public override Task<int> SetGodModeAsync(bool enable, CancellationToken ct = default)
        { SetGodModeCalls++; LastSetGodModeEnable = enable; return Task.FromResult(NextGodModeState); }

        public override Task<int> GetGodModeAsync(CancellationToken ct = default)
        { GetGodModeCalls++; return Task.FromResult(NextGodModeState); }

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

        public override Task<TeleportResult> TeleportRecallLastAsync(CancellationToken ct = default)
        { RecallLastCalls++; return Task.FromResult(NextResult); }

        public override Task<TeleportPov> TeleportGetPovAsync(CancellationToken ct = default)
        { GetPovCalls++; return Task.FromResult(NextPov); }

        public int RelativeCalls { get; private set; }
        public (double Distance, bool Horizontal)? LastRelative { get; private set; }
        public override Task<TeleportPose> TeleportRelativeAsync(double distance, bool horizontal, CancellationToken ct = default)
        { RelativeCalls++; LastRelative = (distance, horizontal); return Task.FromResult(NextPose); }

        public int NextCursorState { get; set; } = -1;
        public int SetCursorCalls { get; private set; }
        public int GetCursorCalls { get; private set; }
        public bool? LastSetCursorShow { get; private set; }
        public override Task<int> SetMouseCursorAsync(bool show, CancellationToken ct = default)
        { SetCursorCalls++; LastSetCursorShow = show; return Task.FromResult(NextCursorState); }
        public override Task<int> GetMouseCursorAsync(CancellationToken ct = default)
        { GetCursorCalls++; return Task.FromResult(NextCursorState); }
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

    /// <summary>Every RegisterSpecific fails (combo held by another app) — models
    /// the "saved hotkey is taken at startup" case.</summary>
    private sealed class FailingHotkeyService : IGlobalHotkeyService
    {
        public IGlobalHotkeyRegistration? RegisterCursorHotkey(Action onPressed) => null;
        public IGlobalHotkeyRegistration? RegisterSpecific(uint modifiers, uint vk, string label, Action onPressed) => null;
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
    public async Task GetPov_populates_camera_display_and_pawn_delta()
    {
        var fake = new FakeDumpService
        {
            // camera at (0,0,100), pawn at (0,0,0) → delta 100; fov 90.
            NextPov = new()
            {
                Code = 0, CamX = 0, CamY = 0, CamZ = 100, Pitch = -30, Yaw = 45, Roll = 0,
                Fov = 90, Source = "raw", HasPawn = true, PawnX = 0, PawnY = 0, PawnZ = 0,
            },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.GetPovCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.GetPovCalls);
        Assert.Equal("100.000", vm.PovZ);
        Assert.Equal("45.00", vm.PovYaw);
        Assert.Equal("90.0°", vm.PovFov);
        Assert.Equal("raw", vm.PovSource);       // cached-POV fallback surfaced
        Assert.Contains("100.0", vm.PovDelta);   // Δ to pawn
    }

    [Fact]
    public async Task GetPov_shows_hint_on_error_code()
    {
        var fake = new FakeDumpService { NextPov = new() { Code = TeleportCodes.Invoke } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.GetPovCommand.ExecuteAsync(null);

        Assert.Equal("—", vm.PovZ);              // cleared
        Assert.Contains("Game thread idle", vm.StatusText);
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
    public async Task CopyAsBugItGo_copies_and_fills_run_field()
    {
        var fake = new FakeDumpService { NextPose = new() { Code = 0, X = 100, Y = 200, Z = 300 } };
        var vm = CreateVm(fake, out var platform);
        vm.IsConnected = true;

        await vm.CopyAsBugItGoCommand.ExecuteAsync(null);

        Assert.NotNull(platform.LastClipboard);
        Assert.StartsWith("BugItGo 100.000 200.000 300.000", platform.LastClipboard);
        // Also pasted into the Run field so BugItGo can fire immediately.
        Assert.StartsWith("BugItGo 100.000 200.000 300.000", vm.BugItGoInput);
    }

    [Fact]
    public async Task RunBugItGo_empty_field_shows_message_without_parsing()
    {
        var fake = new FakeDumpService();
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.BugItGoInput = "   ";   // whitespace only

        await vm.RunBugItGoCommand.ExecuteAsync(null);

        Assert.Null(fake.LastExplicit);
        Assert.Contains("empty", vm.StatusText, StringComparison.OrdinalIgnoreCase);
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
    public void Hotkey_rows_cover_all_teleport_actions()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        // 3 save + 3 recall + recall_last + bugit + bugitgo + debugcam_on/off +
        // pov_get + relative + coords + cursor_on/off.
        Assert.Equal(18, vm.HotkeyRows.Count);
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "save0" && r.DisplayName == "Save marker 1");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "recall2" && r.DisplayName == "Recall marker 3");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "recall_last" && r.DisplayName == "Recall last");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "bugit");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "bugitgo");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "debugcam_on" && r.DisplayName == "Debug cam ON");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "debugcam_off" && r.DisplayName == "Debug cam OFF");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "godmode_on" && r.DisplayName == "God Mode ON");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "godmode_off" && r.DisplayName == "God Mode OFF");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "pov_get" && r.DisplayName == "Get POV");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "relative" && r.DisplayName == "TP facing dir");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "coords" && r.DisplayName == "TP to coords");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "cursor_on" && r.DisplayName == "Cursor ON");
        Assert.Contains(vm.HotkeyRows, r => r.ActionId == "cursor_off" && r.DisplayName == "Cursor OFF");
        Assert.All(vm.HotkeyRows, r => Assert.False(r.HasBinding));
    }

    // ── Debug Camera force on/off ──────────────────────────────────────

    [Fact]
    public async Task ForceDebugCameraOn_calls_dll_and_sets_badge()
    {
        var fake = new FakeDumpService { NextDebugCameraState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceDebugCameraOnCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SetDebugCameraCalls);
        Assert.True(fake.LastSetDebugCameraEnable);
        Assert.Equal("ON", vm.DebugCameraState);
    }

    [Fact]
    public async Task ForceDebugCameraOff_sends_disable_and_sets_badge()
    {
        var fake = new FakeDumpService { NextDebugCameraState = 0 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceDebugCameraOffCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SetDebugCameraCalls);
        Assert.False(fake.LastSetDebugCameraEnable);
        Assert.Equal("OFF", vm.DebugCameraState);
    }

    [Fact]
    public async Task ForceGodModeOn_calls_dll_and_sets_badge()
    {
        var fake = new FakeDumpService { NextGodModeState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceGodModeOnCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SetGodModeCalls);
        Assert.True(fake.LastSetGodModeEnable);
        Assert.Equal("ON", vm.GodModeState);
    }

    [Fact]
    public async Task ForceGodModeOff_sends_disable_and_sets_badge()
    {
        var fake = new FakeDumpService { NextGodModeState = 0 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceGodModeOffCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SetGodModeCalls);
        Assert.False(fake.LastSetGodModeEnable);
        Assert.Equal("OFF", vm.GodModeState);
    }

    [Fact]
    public async Task ForceGodMode_does_nothing_when_disconnected()
    {
        var fake = new FakeDumpService { NextGodModeState = 1 };
        var vm = CreateVm(fake, out _);   // not connected

        await vm.ForceGodModeOnCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.SetGodModeCalls);
        Assert.Equal("Unknown", vm.GodModeState);
    }

    [Fact]
    public async Task ForceDebugCamera_does_nothing_when_disconnected()
    {
        var fake = new FakeDumpService { NextDebugCameraState = 1 };
        var vm = CreateVm(fake, out _);   // not connected

        await vm.ForceDebugCameraOnCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.SetDebugCameraCalls);
        Assert.Equal("Unknown", vm.DebugCameraState);
    }

    [Fact]
    public async Task Disconnect_resets_debug_camera_badge()
    {
        var fake = new FakeDumpService { NextDebugCameraState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);
        await vm.ForceDebugCameraOnCommand.ExecuteAsync(null);
        Assert.Equal("ON", vm.DebugCameraState);

        vm.SetConnected(false);

        Assert.Equal("Unknown", vm.DebugCameraState);
    }

    // ── Directional teleport ───────────────────────────────────────────

    [Fact]
    public async Task TeleportRelative_passes_distance_and_mode_and_applies_pose()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, X = 7, Y = 8, Z = 9, Yaw = 45 },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.RelativeDistance = 250;
        vm.RelativeHorizontal = false;

        await vm.TeleportRelativeCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.RelativeCalls);
        Assert.Equal((250, false), fake.LastRelative);
        Assert.Equal("7.000", vm.PoseX);          // landed pose surfaced
        Assert.Contains("Teleported", vm.StatusText);
    }

    [Fact]
    public async Task TeleportRelative_does_nothing_when_disconnected()
    {
        var fake = new FakeDumpService();
        var vm = CreateVm(fake, out _);   // not connected

        await vm.TeleportRelativeCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.RelativeCalls);
    }

    // ── Teleport to explicit coordinates ───────────────────────────────

    [Fact]
    public async Task TeleportToCoords_without_rotation_passes_xyz_only()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 1 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.CoordX = 11; vm.CoordY = 22; vm.CoordZ = 33;
        vm.CoordSetRotation = false;

        await vm.TeleportToCoordsCommand.ExecuteAsync(null);

        Assert.NotNull(fake.LastExplicit);
        Assert.Equal(11, fake.LastExplicit!.Value.X, 3);
        Assert.Equal(33, fake.LastExplicit.Value.Z, 3);
        Assert.Null(fake.LastExplicit.Value.P);   // rotation omitted
        Assert.Contains("Teleported", vm.StatusText);
    }

    [Fact]
    public async Task TeleportToCoords_with_rotation_passes_pitch()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 1 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        vm.CoordX = 1; vm.CoordY = 2; vm.CoordZ = 3;
        vm.CoordSetRotation = true;
        vm.CoordPitch = -15;

        await vm.TeleportToCoordsCommand.ExecuteAsync(null);

        Assert.NotNull(fake.LastExplicit);
        Assert.Equal(-15, fake.LastExplicit!.Value.P!.Value, 3);
    }

    [Fact]
    public async Task FillCoordsFromCurrent_populates_coord_fields()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, X = 100, Y = 200, Z = 300, Pitch = 5, Yaw = 10, Roll = 0 },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.FillCoordsFromCurrentCommand.ExecuteAsync(null);

        Assert.Equal(100, vm.CoordX);
        Assert.Equal(300, vm.CoordZ);
        Assert.Equal(10, vm.CoordYaw);
    }

    // ── Force mouse cursor ─────────────────────────────────────────────

    [Fact]
    public async Task ForceCursorOn_calls_dll_and_sets_badge()
    {
        var fake = new FakeDumpService { NextCursorState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceCursorOnCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SetCursorCalls);
        Assert.True(fake.LastSetCursorShow);
        Assert.Equal("ON", vm.MouseCursorState);
    }

    [Fact]
    public async Task ForceCursorOff_sends_hide_and_sets_badge()
    {
        var fake = new FakeDumpService { NextCursorState = 0 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.ForceCursorOffCommand.ExecuteAsync(null);

        Assert.False(fake.LastSetCursorShow);
        Assert.Equal("OFF", vm.MouseCursorState);
    }

    [Fact]
    public async Task RefreshCursor_reads_live_state()
    {
        var fake = new FakeDumpService { NextCursorState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        await vm.RefreshCursorCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.GetCursorCalls);
        Assert.Equal("ON", vm.MouseCursorState);
    }

    [Fact]
    public async Task Disconnect_resets_cursor_badge()
    {
        var fake = new FakeDumpService { NextCursorState = 1 };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);
        await vm.ForceCursorOnCommand.ExecuteAsync(null);
        Assert.Equal("ON", vm.MouseCursorState);

        vm.SetConnected(false);

        Assert.Equal("Unknown", vm.MouseCursorState);
    }

    // ── Startup hotkey-conflict surfacing ──────────────────────────────

    [Fact]
    public void Saved_hotkey_taken_at_startup_is_flagged_not_silently_dropped()
    {
        var platform = new FakePlatform();
        // Pre-seed a saved binding so the ctor's LoadAndRegisterHotkeys tries it.
        new TeleportHotkeyStore(platform).Save(new Dictionary<string, TeleportHotkeyBinding>
        {
            ["save0"] = new TeleportHotkeyBinding(HotkeyModifiers.Control, 0x74 /*F5*/),
        });

        var vm = new TeleportViewModel(new FakeDumpService(), new NoopLogger(), platform,
            aobMaker: null, globalHotkeys: new FailingHotkeyService());

        var row = vm.HotkeyRows.First(r => r.ActionId == "save0");
        Assert.True(row.Conflicted);              // flagged, not dropped
        Assert.True(row.HasBinding);              // label still shown
        Assert.Contains("⚠", row.DisplayLabel);
        Assert.True(vm.HasHotkeyWarning);         // top banner is set
    }

    [Fact]
    public void Clearing_a_conflicted_hotkey_clears_the_warning()
    {
        var platform = new FakePlatform();
        new TeleportHotkeyStore(platform).Save(new Dictionary<string, TeleportHotkeyBinding>
        {
            ["save0"] = new TeleportHotkeyBinding(HotkeyModifiers.Control, 0x74),
        });
        var vm = new TeleportViewModel(new FakeDumpService(), new NoopLogger(), platform,
            aobMaker: null, globalHotkeys: new FailingHotkeyService());
        var row = vm.HotkeyRows.First(r => r.ActionId == "save0");
        Assert.True(vm.HasHotkeyWarning);

        vm.ClearHotkeyCommand.Execute(row);

        Assert.False(row.Conflicted);
        Assert.False(row.HasBinding);
        Assert.False(vm.HasHotkeyWarning);
    }

    [Fact]
    public async Task RecallLast_calls_dll_and_reports_success()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = 0, Tier = 1 } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RecallLastCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.RecallLastCalls);
        Assert.Contains("last position", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecallLast_empty_slot_explains_auto_save()
    {
        var fake = new FakeDumpService { NextResult = new() { Code = TeleportCodes.EmptyMarker } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RecallLastCommand.ExecuteAsync(null);

        Assert.Contains("auto-save", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetConnected_routes_last_sentinel_to_last_display()
    {
        var fake = new FakeDumpService
        {
            NextMarkers = new()
            {
                new() { Slot = 0, Valid = false },
                new() { Slot = 1, Valid = false },
                new() { Slot = 2, Valid = false },
                // slot -1 = system "last" sentinel (Fern get_markers).
                new() { Slot = -1, Valid = true, X = 12, Y = 3, Z = 80, Map = "World1" },
            },
        };
        var vm = CreateVm(fake, out _);
        vm.SetConnected(true);

        Assert.True(vm.LastValid);
        Assert.True(vm.CanRecallLast);
        Assert.Contains("World1", vm.LastSummary);
        // The sentinel must not leak into the 3 real marker rows.
        Assert.Equal(3, vm.Markers.Count);
        Assert.All(vm.Markers, m => Assert.False(m.Valid));
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
    public void BeginCapture_toggles_off_when_clicked_again()
    {
        var vm = CreateVm(new FakeDumpService(), out _, new FakeHotkeyService());
        var row = vm.HotkeyRows.First();

        vm.BeginCaptureCommand.Execute(row);   // start
        Assert.True(vm.IsCapturingHotkey);
        Assert.Equal("Cancel", row.CaptureButtonText);

        vm.BeginCaptureCommand.Execute(row);   // click "Cancel" → abort
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Set", row.CaptureButtonText);
        Assert.False(row.HasBinding);
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

    [Fact]
    public async Task Disconnect_clears_pov_display()
    {
        var fake = new FakeDumpService
        {
            NextPov = new() { Code = 0, CamZ = 100, Fov = 90, HasPawn = true },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        await vm.GetPovCommand.ExecuteAsync(null);
        Assert.Equal("100.000", vm.PovZ);   // populated

        vm.SetConnected(false);
        Assert.Equal("—", vm.PovZ);          // cleared on disconnect
        Assert.Equal("—", vm.PovFov);
        Assert.Equal("", vm.PovSource);
        Assert.Equal("", vm.PovDelta);
    }

    // ── Velocity / acceleration readout + Locate in GWorld ─────────────

    [Fact]
    public async Task RefreshPose_populates_velocity_when_movement_present()
    {
        var fake = new FakeDumpService
        {
            NextPose = new()
            {
                Code = 0, X = 1, Y = 2, Z = 3, Map = "W", Source = "raw",
                PawnAddr = "0x1234", HasMovement = true,
                VelX = 100, VelY = 0, VelZ = -50, Speed = 111.8,
                AccX = 10, AccY = 20, AccZ = 0,
            },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RefreshPoseCommand.ExecuteAsync(null);

        Assert.Equal("100.0", vm.VelX);
        Assert.Equal("-50.0", vm.VelZ);
        Assert.Equal("10.0", vm.AccX);
        Assert.Contains("cm/s", vm.Speed);
        Assert.Equal("", vm.MovementNote);            // available → no note
        Assert.Equal("0x1234", vm.PawnAddrDisplay);
    }

    [Fact]
    public async Task RefreshPose_marks_velocity_unavailable_without_movement()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, PawnAddr = "0xABC", HasMovement = false },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;

        await vm.RefreshPoseCommand.ExecuteAsync(null);

        Assert.Equal("—", vm.VelX);
        Assert.Equal("—", vm.Speed);
        Assert.Equal("—", vm.AccZ);
        Assert.Contains("unavailable", vm.MovementNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0xABC", vm.PawnAddrDisplay);    // pawn addr still shown
    }

    [Fact]
    public async Task RefreshPose_error_clears_velocity_and_pawn()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, PawnAddr = "0xAAA", HasMovement = true, VelX = 5, Speed = 5 },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        await vm.RefreshPoseCommand.ExecuteAsync(null);
        Assert.Equal("5.0", vm.VelX);                 // populated
        Assert.Equal("0xAAA", vm.PawnAddrDisplay);

        fake.NextPose = new() { Code = TeleportCodes.NoPawn };
        await vm.RefreshPoseCommand.ExecuteAsync(null);

        Assert.Equal("—", vm.VelX);                   // cleared on error
        Assert.Equal("", vm.PawnAddrDisplay);
        Assert.Equal("", vm.MovementNote);
    }

    [Fact]
    public async Task LocateCurrentPose_fires_event_with_pawn_addr()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, PawnAddr = "0xDEAD", HasMovement = true },
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        string? located = null;
        vm.LocateInGWorld += a => located = a;

        await vm.LocateCurrentPoseInGWorldCommand.ExecuteAsync(null);

        Assert.Equal("0xDEAD", located);
        Assert.Equal(1, fake.GetPoseCalls);           // reads a fresh pose first
        Assert.Equal("0xDEAD", vm.PawnAddrDisplay);   // display updated too
    }

    [Fact]
    public async Task LocateCurrentPose_no_pawn_addr_does_not_fire()
    {
        var fake = new FakeDumpService
        {
            NextPose = new() { Code = 0, PawnAddr = "0x0" },   // unresolved pawn
        };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        await vm.LocateCurrentPoseInGWorldCommand.ExecuteAsync(null);

        Assert.False(fired);
        Assert.Contains("no pawn", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocateCurrentPose_error_code_shows_hint_without_firing()
    {
        var fake = new FakeDumpService { NextPose = new() { Code = TeleportCodes.NoPawn } };
        var vm = CreateVm(fake, out _);
        vm.IsConnected = true;
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        await vm.LocateCurrentPoseInGWorldCommand.ExecuteAsync(null);

        Assert.False(fired);
        Assert.Contains("pawn", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocateCurrentPose_noop_when_disconnected()
    {
        var fake = new FakeDumpService { NextPose = new() { Code = 0, PawnAddr = "0x1" } };
        var vm = CreateVm(fake, out _);   // not connected
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        await vm.LocateCurrentPoseInGWorldCommand.ExecuteAsync(null);

        Assert.False(fired);
        Assert.Equal(0, fake.GetPoseCalls);
    }

    [Fact]
    public void TeleportPose_HasPawnAddr_rejects_null_and_zero()
    {
        Assert.False(new TeleportPose { PawnAddr = "" }.HasPawnAddr);
        Assert.False(new TeleportPose { PawnAddr = "0x0" }.HasPawnAddr);
        Assert.False(new TeleportPose { PawnAddr = "0X0" }.HasPawnAddr);
        Assert.True(new TeleportPose { PawnAddr = "0x7FF00010" }.HasPawnAddr);
    }
}
