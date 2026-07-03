using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Teleport panel — universal BugIt-style teleport: 3 marker
/// slots (save/recall), cursor teleport for 2.5D/45° games, BugItGo interop,
/// and one-click CE Lua / .CT export. All work runs DLL-side (Wirbel module via
/// the teleport_* pipe commands); this VM is a thin async client.
/// Full contract: docs/teleport-spec.md §9.
/// </summary>
public partial class TeleportViewModel : ViewModelBase, IDisposable
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IPlatformService _platform;
    private readonly IAobMakerBridge? _aobMaker;
    private readonly IGlobalHotkeyService? _globalHotkeys;
    private readonly TeleportHotkeyStore? _hotkeyStore;
    private IGlobalHotkeyRegistration? _cursorHotkey;
    // Live marker-hotkey registrations + the persisted combos, both keyed by
    // action id ("save0".."recall2").
    private readonly Dictionary<string, IGlobalHotkeyRegistration> _markerHotkeys = new();
    private readonly Dictionary<string, TeleportHotkeyBinding> _bindings = new(StringComparer.Ordinal);
    private Avalonia.Threading.DispatcherTimer? _autoTimer;
    private int _autoTick;
    // Re-entrancy guard: the 500ms DispatcherTimer keeps firing even while a tick
    // is still awaiting its pipe round-trip. If a long pipe op is hogging the DLL's
    // single-threaded dispatch loop (e.g. a Copy CE XML drilldown emitting 100k+
    // lines), back-to-back ticks would pile up get_pose requests that all drain in
    // a burst. Skip a tick whenever the previous one hasn't finished.
    private bool _autoTickBusy;
    private bool _disposed;
    // Latest engine state (for GWorld AOB / module) — needed to bake a standalone
    // trainer .CT. Pushed by MainWindowViewModel on connect / rescan.
    private Models.EngineState? _engineState;

    public TeleportViewModel(IDumpService dump, ILoggingService log, IPlatformService platform,
        IAobMakerBridge? aobMaker = null, IGlobalHotkeyService? globalHotkeys = null)
    {
        _dump = dump;
        _log = log;
        _platform = platform;
        _aobMaker = aobMaker;
        _globalHotkeys = globalHotkeys;
        for (int i = 0; i < 3; i++)
            Markers.Add(new TeleportMarkerRow { Slot = i });

        // Hotkey rows: Save 1-3, Recall 1-3, then the system Recall-last and the
        // two BugItGo actions (Force stays UI-button only). Adding a row here is
        // all it takes — capture, persistence and registration are generic over
        // ActionId; OnMarkerHotkeyPressed routes the id to the right command.
        for (int i = 0; i < 3; i++)
            HotkeyRows.Add(new TeleportHotkeyRow { ActionId = $"save{i}", DisplayName = $"Save marker {i + 1}",
                Hint = $"Save the current position into marker slot {i + 1}." });
        for (int i = 0; i < 3; i++)
            HotkeyRows.Add(new TeleportHotkeyRow { ActionId = $"recall{i}", DisplayName = $"Recall marker {i + 1}",
                Hint = $"Teleport back to marker slot {i + 1}." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "recall_last",  DisplayName = "Recall last",
            Hint = "Undo the last teleport — return to the position from just before it." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "bugit",        DisplayName = "Copy BugItGo",
            Hint = "Copy the current position as a 'BugItGo X Y Z' string to the clipboard." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "bugitgo",      DisplayName = "Run BugItGo",
            Hint = "Teleport to the position stored by the last BugIt." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "debugcam_on",  DisplayName = "Debug cam ON",
            Hint = "Turn the free-fly Debug Camera ON." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "debugcam_off", DisplayName = "Debug cam OFF",
            Hint = "Turn the Debug Camera OFF (return to the player view)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "godmode_on",   DisplayName = "God Mode ON",
            Hint = "Turn God Mode ON (force damage immunity on the player pawn)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "godmode_off",  DisplayName = "God Mode OFF",
            Hint = "Turn God Mode OFF." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "superjump_toggle", DisplayName = "Super Jump toggle",
            Hint = "Toggle Super Jump on/off (hold a high JumpZVelocity from the slider)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "movespeed_toggle", DisplayName = "Move Speed toggle",
            Hint = "Toggle the Move Speed override on/off (apply the slider %, or restore)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "gravity_toggle",   DisplayName = "Gravity toggle",
            Hint = "Toggle the Gravity (GravityScale) override on/off." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "gravdir_toggle",   DisplayName = "Gravity Dir toggle",
            Hint = "Toggle the Gravity Direction override on/off (UE5.4+)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "pov_get",      DisplayName = "Get POV",
            Hint = "Read the current camera POV (location / rotation / FOV)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "relative",     DisplayName = "TP facing dir",
            Hint = "Teleport along the pawn's facing direction by the set distance." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "coords",       DisplayName = "TP to coords",
            Hint = "Teleport to the entered X / Y / Z coordinates." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "cursor_on",    DisplayName = "Cursor ON",
            Hint = "Force the mouse cursor ON (bShowMouseCursor = true)." });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "cursor_off",   DisplayName = "Cursor OFF",
            Hint = "Force the mouse cursor OFF." });

        if (_globalHotkeys != null)
        {
            _hotkeyStore = new TeleportHotkeyStore(platform);
            LoadAndRegisterHotkeys();
        }
    }

    /// <summary>Raised when the user clicks "Locate in GWorld" on the Current Pose
    /// card — carries the player pawn address. MainWindowViewModel switches to the
    /// Live Walker and runs LocateInGWorldAsync (goto + select that exact pawn).</summary>
    public event Action<string>? LocateInGWorld;

    /// <summary>Engine-rooted counterpart of <see cref="LocateInGWorld"/> (path search
    /// rooted at the live UGameEngine). NOTE: a pawn is a world actor and usually lives
    /// below GWorld, so this is typically unreachable — provided for parity.</summary>
    public event Action<string>? LocateInGameEngine;

    /// <summary>Raised when the user clicks one of the per-vector "Locate in GWorld"
    /// buttons (position / velocity) — carries (ownerAddr, fieldOffset, fieldName) of
    /// the FVector field. MainWindowViewModel switches to the Live Walker and runs
    /// LocateInGWorldAsync so the path lands on the exact vector field (the same
    /// value-landing handoff Value Search uses), not just the owning component.</summary>
    public event Action<string, int, string>? LocateValueInGWorld;

    /// <summary>Whether global teleport hotkeys can be offered (a hotkey service
    /// was supplied — false in headless tests).</summary>
    public bool CanBindCursorHotkey => _globalHotkeys != null;

    /// <summary>Per-action marker hotkey rows shown in the panel.</summary>
    public ObservableCollection<TeleportHotkeyRow> HotkeyRows { get; } = new();

    /// <summary>The row currently capturing a key combo (null when idle). The
    /// panel code-behind feeds KeyDown here while non-null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCapturingHotkey))]
    private TeleportHotkeyRow? _capturingRow;

    public bool IsCapturingHotkey => CapturingRow != null;

    /// <summary>"Save marker 1 fired @ 19:48:50" — last time any global teleport
    /// hotkey triggered, so the user can confirm the hotkey is live.</summary>
    [ObservableProperty] private string _lastHotkeyFired = "";

    // ── Connection gating ──────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanRecallLast))]
    [NotifyPropertyChangedFor(nameof(CanExportTrainer))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanRecallLast))]
    private bool _isBusy;

    /// <summary>Buttons are live only when connected and no op is in flight.</summary>
    public bool CanOperate => IsConnected && !IsBusy;

    [ObservableProperty] private string _statusText = "Not connected";

    // ── Standalone trainer export (no-DLL CE Lua .CT via AOBMaker) ──────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportTrainer))]
    [NotifyPropertyChangedFor(nameof(AobMakerNote))]
    private bool _isAobMakerAvailable;

    /// <summary>Export is offered only when connected AND the AOBMaker plugin is
    /// reachable (it's the delivery channel — no clipboard fallback by design).</summary>
    public bool CanExportTrainer => IsConnected && IsAobMakerAvailable;

    public string AobMakerNote => IsAobMakerAvailable
        ? "AOBMaker connected — the standalone trainer will be pushed straight into CE."
        : "AOBMaker plugin not detected. Start Cheat Engine with the AOBMaker plugin, then press ⟳.";

    /// <summary>Push the latest engine state (GWorld AOB / module) for trainer bake.</summary>
    public void SetEngineState(Models.EngineState state) => _engineState = state;

    /// <summary>Probe AOBMaker availability (the delivery channel for trainer export).</summary>
    public async Task CheckAobMakerAsync()
    {
        if (_aobMaker == null) return;
        try { IsAobMakerAvailable = await _aobMaker.CheckAvailabilityAsync(); }
        catch { IsAobMakerAvailable = false; }
    }

    /// <summary>
    /// Fetch baked offsets from the DLL, emit a no-DLL standalone CE-Lua trainer
    /// (Move Speed / Gravity / Super Jump / GodMode / coordinate TP), and push each
    /// entry into CE via AOBMaker (Setup auto-activates). Gated on AOBMaker — no
    /// clipboard/disk fallback by design (see project-standalone-ce-lua-trainer).
    /// </summary>
    [RelayCommand]
    private async Task ExportTrainerAsync()
    {
        if (_aobMaker == null || !_aobMaker.IsAvailable)
        {
            StatusText = "AOBMaker plugin not detected — cannot push the trainer into CE.";
            return;
        }
        if (_engineState == null || string.IsNullOrWhiteSpace(_engineState.GWorldAob))
        {
            StatusText = "GWorld AOB unavailable — connect and scan first.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Fetching baked offsets…";
            var offs = await _dump.GetTrainerOffsetsAsync();
            offs.Module = _engineState.ModuleName;
            offs.GWorldAob = _engineState.GWorldAob;
            offs.GWorldAobPos = _engineState.GWorldAobPos;
            offs.GWorldAobLen = _engineState.GWorldAobLen;

            if (!offs.IsUsable)
            {
                StatusText = offs.Code != 0
                    ? $"Trainer offsets unavailable (code {offs.Code}). Enter gameplay so a pawn is live, then retry."
                    : "Trainer offsets incomplete (pawn / RootComponent not resolved).";
                return;
            }

            var entries = StandaloneTrainerScriptGenerator.Generate(offs);
            int ok = 0;
            foreach (var e in entries)
            {
                var sent = await _aobMaker.CreateAAScriptAsync(e.Description, e.Script, e.AutoActivate);
                if (sent) { ok++; }
                else { break; }   // pipe dropped mid-push (CE closed?)
            }
            IsAobMakerAvailable = _aobMaker.IsAvailable;
            StatusText = ok == entries.Count
                ? $"Standalone trainer pushed to CE ({ok} entries). Setup ran; toggle the rest. No DLL needed henceforth."
                : $"⚠ Pushed {ok}/{entries.Count} entries — AOBMaker pipe dropped (CE closed?).";
            _log.Info($"Standalone trainer export: {ok}/{entries.Count} entries pushed to CE");
        }
        catch (Exception ex)
        {
            StatusText = $"Trainer export failed: {ex.Message}";
            _log.Error("Standalone trainer export failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Current pose ───────────────────────────────────────────────────
    [ObservableProperty] private string _poseX = "—";
    [ObservableProperty] private string _poseY = "—";
    [ObservableProperty] private string _poseZ = "—";
    [ObservableProperty] private string _posePitch = "—";
    [ObservableProperty] private string _poseYaw = "—";
    [ObservableProperty] private string _poseRoll = "—";
    [ObservableProperty] private string _poseMap = "";
    [ObservableProperty] private string _poseSource = "";

    /// <summary>Resolved pawn address shown on the Current Pose card ("" when
    /// unavailable) — this is the object the "Locate in GWorld" button targets.</summary>
    [ObservableProperty] private string _pawnAddrDisplay = "";

    // ── Live velocity / acceleration (from the pawn's CharacterMovement) ───
    // Polled in the same round-trip as the pose (Feature A). Values are cm/s
    // (velocity) / cm/s² (acceleration). On pawns with no CharacterMovement
    // (vehicle / custom movement framework) the readouts show "—" and
    // MovementNote explains why, rather than a misleading 0.
    [ObservableProperty] private string _velX = "—";
    [ObservableProperty] private string _velY = "—";
    [ObservableProperty] private string _velZ = "—";
    [ObservableProperty] private string _speed = "—";
    [ObservableProperty] private string _accX = "—";
    [ObservableProperty] private string _accY = "—";
    [ObservableProperty] private string _accZ = "—";
    /// <summary>Non-empty when velocity/acceleration are unavailable (no
    /// CharacterMovement) — shown as a small hint under the readout.</summary>
    [ObservableProperty] private string _movementNote = "";

    [ObservableProperty] private bool _autoRefresh;

    // ── Camera POV (read-only) ─────────────────────────────────────────
    // The on-screen camera (APlayerCameraManager), DISTINCT from the pawn pose
    // above. There is no Set POV — the view is recomputed every tick (see
    // TeleportPov / teleport-spec). Manual Get only (not part of the auto-poll,
    // which reads the cheap raw pawn pose; POV needs a game-thread invoke).
    [ObservableProperty] private string _povX = "—";
    [ObservableProperty] private string _povY = "—";
    [ObservableProperty] private string _povZ = "—";
    [ObservableProperty] private string _povPitch = "—";
    [ObservableProperty] private string _povYaw = "—";
    [ObservableProperty] private string _povRoll = "—";
    [ObservableProperty] private string _povFov = "—";
    /// <summary>"invoke" / "raw" — how the POV was read ("raw" = cached-POV
    /// fallback, shown as a chip so the user knows the getters didn't respond).</summary>
    [ObservableProperty] private string _povSource = "";
    /// <summary>"Δ to pawn: 412.3 uu …" — the camera↔pawn distance plus the hint
    /// that an independent camera barely moves it after a teleport.</summary>
    [ObservableProperty] private string _povDelta = "";

    // ── Cursor teleport ────────────────────────────────────────────────
    [ObservableProperty] private double _zOffset = 100.0;
    [ObservableProperty] private int _traceChannel;      // ETraceTypeQuery byte
    [ObservableProperty] private bool _fallbackToCenter = true;

    /// <summary>Global cursor-teleport hotkey toggle. When on, the dumper grabs
    /// the first free combo (Ctrl+F8→F5, then Alt+F8→F5) so the user can keep
    /// the game focused (cursor in the game) and fire a cursor teleport.</summary>
    [ObservableProperty] private bool _cursorHotkeyEnabled;
    [ObservableProperty] private string _cursorHotkeyLabel = "";

    // ── BugItGo interop ────────────────────────────────────────────────
    [ObservableProperty] private string _bugItGoInput = "";

    // ── Debug Camera (Genau/Wirbel via set_debug_camera) ───────────────
    /// <summary>Tri-state badge text: "ON" / "OFF" / "Unknown". The deterministic
    /// enable/disable + the "return the camera back" handling all live DLL-side;
    /// this VM is a thin client over set_debug_camera / get_debug_camera_state,
    /// the same path the Console panel uses.</summary>
    [ObservableProperty] private string _debugCameraState = "Unknown";
    [ObservableProperty] private string _debugCameraBadgeColor = "#888888";

    // ── God Mode (Solitar via set_god_mode) ────────────────────────────
    /// <summary>Tri-state badge: "ON" (immune) / "OFF" / "Unknown". The pawn
    /// resolution, bitfield write, and re-assert worker all live DLL-side; this
    /// VM is a thin client over set_god_mode / get_god_mode.</summary>
    [ObservableProperty] private string _godModeState = "Unknown";
    [ObservableProperty] private string _godModeBadgeColor = "#888888";

    // ── Move Speed multiplier (Laufen via set_movement_multiplier) ─────
    /// <summary>Tri-state badge: "ON" (held) / "OFF" / "Unavailable". The pawn →
    /// CharacterMovement resolution, MaxWalkSpeed write, base-value cache and
    /// re-assert worker all live DLL-side; this VM is a thin client over
    /// get_movement_params / set_movement_multiplier / reset_movement.</summary>
    [ObservableProperty] private string _moveSpeedState = "Unknown";
    [ObservableProperty] private string _moveSpeedBadgeColor = "#888888";

    /// <summary>Log-scale slider position: the multiplier is 10^exponent, so
    /// exponent ∈ [-1, 1] maps to 10%…1000% with 100% (1.0×) at the centre. A
    /// log scale gives equal slider travel to halving and doubling.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveSpeedPercentText))]
    private double _moveSpeedExponent;

    /// <summary>Live "Current: 600 (base 600)" readout for the current pawn.</summary>
    [ObservableProperty] private string _moveSpeedCurrentText = "—";

    /// <summary>Multiplier the slider currently represents (10^exponent).</summary>
    public double MoveSpeedMultiplier => Math.Pow(10.0, MoveSpeedExponent);

    /// <summary>Slider position as a percentage string (e.g. "250%").</summary>
    public string MoveSpeedPercentText => $"{Math.Round(MoveSpeedMultiplier * 100.0)}%";

    // ── Gravity multiplier (Laufen via "gravity" knob = GravityScale) ──
    /// <summary>Tri-state badge: "ON" (held) / "OFF" / "Unavailable".</summary>
    [ObservableProperty] private string _gravityState = "Unknown";
    [ObservableProperty] private string _gravityBadgeColor = "#888888";

    /// <summary>Log-scale slider: multiplier = 10^exponent, exponent ∈ [-1,1] ⇒
    /// 10%…1000% of GravityScale with 100% (1.0×) at centre. &lt;100% = floaty /
    /// moon-jump, &gt;100% = heavy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GravityPercentText))]
    private double _gravityExponent;

    [ObservableProperty] private string _gravityCurrentText = "—";

    public double GravityMultiplier => Math.Pow(10.0, GravityExponent);
    public string GravityPercentText => $"{Math.Round(GravityMultiplier * 100.0)}%";

    // ── Super Jump (Laufen via "jump" knob = JumpZVelocity) ────────────
    /// <summary>Tri-state badge: "ON" (high-jump held) / "OFF" / "Unavailable".
    /// Persistent toggle (Force ON keeps re-asserting), mirroring God Mode.</summary>
    [ObservableProperty] private string _superJumpState = "Unknown";
    [ObservableProperty] private string _superJumpBadgeColor = "#888888";

    /// <summary>Log-scale slider for jump HEIGHT (10%…1000%, 100% = base). Apex
    /// height h ∝ JumpZVelocity², so the applied velocity multiplier is
    /// √(heightMultiplier) — see <see cref="SuperJumpVelocityMultiplier"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuperJumpPercentText))]
    private double _superJumpExponent;

    [ObservableProperty] private string _superJumpCurrentText = "—";

    /// <summary>Tracks whether the high-jump override is currently held (so the
    /// global hotkey can toggle it).</summary>
    private bool _superJumpActive;

    /// <summary>Desired jump-HEIGHT multiplier the slider represents.</summary>
    public double SuperJumpHeightMultiplier => Math.Pow(10.0, SuperJumpExponent);

    /// <summary>The JumpZVelocity multiplier to actually apply: √(heightMult),
    /// since apex height scales with the square of launch velocity.</summary>
    public double SuperJumpVelocityMultiplier => Math.Sqrt(SuperJumpHeightMultiplier);

    /// <summary>Slider position as a jump-height percentage (e.g. "400%").</summary>
    public string SuperJumpPercentText => $"{Math.Round(SuperJumpHeightMultiplier * 100.0)}%";

    // ── Gravity Direction (Laufen, UE5.4+ GravityDirection vector) ─────
    /// <summary>Tri-state badge: "ON" / "OFF" / "Unavailable" (pre-5.4 / no
    /// reflected GravityDirection).</summary>
    [ObservableProperty] private string _gravDirState = "Unknown";
    [ObservableProperty] private string _gravDirBadgeColor = "#888888";

    /// <summary>Direction components, each a LINEAR slider in [-1, +1] (= -100%…
    /// +100%). The DLL normalizes; default (0,0,-1) = straight down.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GravDirXPercentText))]
    private double _gravDirX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GravDirYPercentText))]
    private double _gravDirY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GravDirZPercentText))]
    private double _gravDirZ = -1;

    [ObservableProperty] private string _gravDirCurrentText = "—";

    /// <summary>Tracks whether the gravity-direction override is held (hotkey toggle).</summary>
    private bool _gravDirActive;

    public string GravDirXPercentText => $"{Math.Round(GravDirX * 100.0)}%";
    public string GravDirYPercentText => $"{Math.Round(GravDirY * 100.0)}%";
    public string GravDirZPercentText => $"{Math.Round(GravDirZ * 100.0)}%";

    // ── Directional teleport (move along the pawn's facing) ────────────
    /// <summary>Distance in unreal units to step along the facing direction
    /// (negative = backward).</summary>
    [ObservableProperty] private double _relativeDistance = 100.0;
    /// <summary>True = move on the ground plane (Yaw only, keep Z); false = full
    /// 3D forward (follow pitch — fly / noclip feel).</summary>
    [ObservableProperty] private bool _relativeHorizontal = true;

    // ── Teleport to explicit coordinates (force, no map check) ─────────
    [ObservableProperty] private double _coordX;
    [ObservableProperty] private double _coordY;
    [ObservableProperty] private double _coordZ;
    /// <summary>Also restore Pitch/Yaw/Roll when teleporting to the coordinates.</summary>
    [ObservableProperty] private bool _coordSetRotation;
    [ObservableProperty] private double _coordPitch;
    [ObservableProperty] private double _coordYaw;
    [ObservableProperty] private double _coordRoll;

    // ── Force mouse cursor (writes APlayerController.bShowMouseCursor) ──
    /// <summary>Tri-state badge: "ON" / "OFF" / "Unknown". One-shot write; games
    /// that re-set the flag every tick may revert it.</summary>
    [ObservableProperty] private string _mouseCursorState = "Unknown";
    [ObservableProperty] private string _mouseCursorBadgeColor = "#888888";

    // ── Startup hotkey-conflict warning ────────────────────────────────
    /// <summary>Non-empty when one or more saved hotkeys could not be re-bound at
    /// startup because another app holds the combo. Shown as a top banner; the
    /// conflicting rows are flagged individually. Kept separate from StatusText so
    /// a connect/disconnect message can't overwrite it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyWarning))]
    private string _hotkeyWarning = "";

    public bool HasHotkeyWarning => !string.IsNullOrEmpty(HotkeyWarning);

    // ── System "last" position (auto-saved before every jump) ──────────
    /// <summary>True once the DLL has auto-saved a pre-teleport pose (enables
    /// the Recall-last button). System-managed — the user never saves this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecallLast))]
    private bool _lastValid;

    /// <summary>Human summary of the last auto-saved pose, e.g. "(12.0, 3.0,
    /// 80.0)  World1", or a placeholder before the first teleport.</summary>
    [ObservableProperty] private string _lastSummary = "(saved automatically before each teleport)";

    /// <summary>Recall-last is live only when connected, idle, and a pose has
    /// actually been auto-saved.</summary>
    public bool CanRecallLast => CanOperate && LastValid;

    public ObservableCollection<TeleportMarkerRow> Markers { get; } = new();

    /// <summary>Called by MainWindowViewModel on connect/disconnect. On connect,
    /// the marker list is refreshed (markers live in the DLL — they survive a UI
    /// restart as long as the game process lives).</summary>
    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (connected)
        {
            StatusText = "Connected";
            _ = RefreshMarkersAsync();
        }
        else
        {
            StatusText = "Not connected";
            AutoRefresh = false;
            ApplyDebugCameraState(-1);   // badge back to Unknown
            ApplyGodModeState(-1);       // godmode badge back to Unknown
            ApplyMoveSpeedState(-1);     // move-speed badge back to Unknown
            MoveSpeedCurrentText = "—";
            ApplyGravityState(-1);       // gravity badge back to Unknown
            GravityCurrentText = "—";
            ApplySuperJumpState(-1);     // super-jump badge back to Unknown
            SuperJumpCurrentText = "—";
            _superJumpActive = false;
            ApplyGravDirState(-1);       // gravity-direction badge back to Unknown
            GravDirCurrentText = "—";
            _gravDirActive = false;
            ApplyMouseCursorState(-1);   // cursor badge back to Unknown
            ClearPovDisplay();
        }
    }

    partial void OnCursorHotkeyEnabledChanged(bool value)
    {
        if (value)
        {
            if (_globalHotkeys == null) { CursorHotkeyEnabled = false; return; }
            _cursorHotkey?.Dispose();
            _cursorHotkey = _globalHotkeys.RegisterCursorHotkey(OnCursorHotkeyPressed);
            if (_cursorHotkey == null)
            {
                CursorHotkeyLabel = "";
                CursorHotkeyEnabled = false;
                StatusText = "Could not bind a cursor hotkey — Ctrl/Alt+F5..F8 are all taken.";
            }
            else
            {
                CursorHotkeyLabel = _cursorHotkey.Label;
                StatusText = $"Cursor teleport bound to {_cursorHotkey.Label} — keep the game " +
                             "focused and press it to teleport to the cursor.";
                _log.Info($"Teleport: cursor hotkey bound to {_cursorHotkey.Label}");
            }
        }
        else
        {
            _cursorHotkey?.Dispose();
            _cursorHotkey = null;
            CursorHotkeyLabel = "";
        }
    }

    // Fires on the hotkey thread → marshal to the UI thread, then teleport.
    private void OnCursorHotkeyPressed()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CanOperate && TeleportToCursorCommand.CanExecute(null))
            {
                MarkHotkeyFired("Cursor teleport", ran: true);
                _ = TeleportToCursorCommand.ExecuteAsync(null);
            }
            else
            {
                MarkHotkeyFired("Cursor teleport", ran: false);
            }
        });
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _autoTimer ??= new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _autoTimer.Tick -= AutoTick;
            _autoTimer.Tick += AutoTick;
            _autoTick = 0;
            _autoTimer.Start();
        }
        else
        {
            _autoTimer?.Stop();
        }
    }

    private async void AutoTick(object? sender, EventArgs e)
    {
        if (!IsConnected) return;
        // Drop this tick if the previous one is still in flight — prevents
        // get_pose / get_markers requests from queueing up behind a slow pipe.
        if (_autoTickBusy) return;
        _autoTickBusy = true;
        try
        {
            // Quiet poll: does NOT toggle IsBusy, so the buttons (bound to
            // CanOperate) don't flicker disabled/enabled twice a second.
            await RefreshPoseQuietAsync();
            // Re-pull markers every ~2s so changes made via CE Lua / global hotkeys
            // (which the UI didn't initiate) show up.
            if (++_autoTick % 4 == 0)
                await RefreshMarkersAsync();
        }
        finally
        {
            _autoTickBusy = false;
        }
    }

    private async Task RefreshPoseQuietAsync()
    {
        try
        {
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code == TeleportCodes.Ok) ApplyPoseAndMovement(p);
        }
        catch (Exception ex)
        {
            _log.Error("Teleport auto-refresh failed", ex);
        }

        // Auto-update the camera POV alongside the pose. POV is unavailable on
        // games that cook the camera getters out of reflection (TQ2 / Octopath),
        // so on any failure SKIP the update (leave the last good values / "—")
        // rather than clearing or surfacing an error — the pose display stays
        // useful regardless. Separate try/catch so a POV fault never disturbs the
        // pose refresh above.
        try
        {
            var pov = await _dump.TeleportGetPovAsync();
            if (pov.Code == TeleportCodes.Ok) ApplyPov(pov);
        }
        catch (Exception ex)
        {
            _log.Error("Teleport auto-refresh POV failed", ex);
        }
    }

    // ── Commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshPoseAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code != TeleportCodes.Ok)
            {
                ClearPoseDisplay();
                StatusText = TeleportCodes.Describe(p.Code);
                return;
            }
            ApplyPoseAndMovement(p);
            StatusText = $"Pose read ({p.Source}).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport RefreshPose failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the Current Pose's pawn in the Live Walker — reads a fresh
    /// pose to get the live pawn address, then hands it to the GWorld locator
    /// (goto + select). One-click: works even without auto-refresh on. NOT gated
    /// on a C# GWorld-available flag (a stale flag once caused a silent no-op,
    /// build 1311) — the DLL's find_path is the source of truth and the Live
    /// Walker reports reachability via its own status (incl. ok_via_level for
    /// World-Partition / streaming pawns).</summary>
    [RelayCommand]
    private Task LocateCurrentPoseInGWorldAsync() => LocateCurrentPoseAsync(useEngine: false);

    /// <summary>Engine-rooted counterpart of <see cref="LocateCurrentPoseInGWorldAsync"/>.
    /// A pawn is a world actor, usually below GWorld, so this is typically unreachable —
    /// provided for parity (see the tooltip).</summary>
    [RelayCommand]
    private Task LocateCurrentPoseInGameEngineAsync() => LocateCurrentPoseAsync(useEngine: true);

    /// <summary>Locate the pawn's position FVector (RootComponent.RelativeLocation)
    /// in GWorld — lands the Live Walker on the exact location field rather than the
    /// pawn. Reads a fresh pose for the live owner address. GWorld-only: the owning
    /// RootComponent is a normal world sub-object the GWorld graph reaches.</summary>
    [RelayCommand]
    private Task LocatePositionInGWorldAsync() => LocatePoseFieldAsync(PoseLocateKind.Position);

    /// <summary>Locate the pawn's velocity FVector (CharacterMovement.Velocity) in
    /// GWorld — lands on the exact velocity field. Unavailable on pawns with no
    /// CharacterMovement (vehicle / custom-movement framework).</summary>
    [RelayCommand]
    private Task LocateVelocityInGWorldAsync() => LocatePoseFieldAsync(PoseLocateKind.Velocity);

    /// <summary>Locate the pawn's acceleration FVector (CharacterMovement.Acceleration)
    /// in GWorld.</summary>
    [RelayCommand]
    private Task LocateAccelerationInGWorldAsync() => LocatePoseFieldAsync(PoseLocateKind.Acceleration);

    /// <summary>Locate "Speed" in GWorld — Speed has no own field (it is |Velocity|),
    /// so this lands on the Velocity vector it is computed from.</summary>
    [RelayCommand]
    private Task LocateSpeedInGWorldAsync() => LocatePoseFieldAsync(PoseLocateKind.Speed);

    /// <summary>Locate the pawn's rotation (Controller.ControlRotation FRotator) in
    /// GWorld.</summary>
    [RelayCommand]
    private Task LocateRotationInGWorldAsync() => LocatePoseFieldAsync(PoseLocateKind.Rotation);

    private enum PoseLocateKind { Position, Velocity, Acceleration, Speed, Rotation }

    // Shared body for the per-field locate buttons: read a fresh pose, pick the
    // owner+offset of the requested field, and hand it to the value-landing GWorld
    // locator (same path Value Search uses — lands on the field, not just the
    // owning component). Speed has no own field → lands on Velocity.
    private async Task LocatePoseFieldAsync(PoseLocateKind kind)
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = TeleportCodes.Describe(p.Code);
                return;
            }
            ApplyPoseAndMovement(p);

            string what, owner, field;
            int offset;
            switch (kind)
            {
                case PoseLocateKind.Velocity:
                case PoseLocateKind.Speed:
                    what = kind == PoseLocateKind.Speed ? "speed (Velocity vector)" : "velocity";
                    if (!p.HasMovement || !p.HasVelAddr)
                    {
                        StatusText = "No velocity vector to locate (this pawn has no CharacterMovement).";
                        return;
                    }
                    owner = p.VelOwnerAddr; offset = p.VelFieldOffset; field = p.VelFieldName;
                    break;
                case PoseLocateKind.Acceleration:
                    what = "acceleration";
                    if (!p.HasMovement || !p.HasAccAddr)
                    {
                        StatusText = "No acceleration vector to locate (this pawn has no CharacterMovement).";
                        return;
                    }
                    owner = p.AccOwnerAddr; offset = p.AccFieldOffset; field = p.AccFieldName;
                    break;
                case PoseLocateKind.Rotation:
                    what = "rotation";
                    if (!p.HasRotAddr)
                    {
                        StatusText = "No rotation field to locate (ControlRotation unresolved).";
                        return;
                    }
                    owner = p.RotOwnerAddr; offset = p.RotFieldOffset; field = p.RotFieldName;
                    break;
                default: // Position
                    what = "position";
                    if (!p.HasLocAddr)
                    {
                        StatusText = "No position vector to locate (location field unresolved).";
                        return;
                    }
                    owner = p.LocOwnerAddr; offset = p.LocFieldOffset; field = p.LocFieldName;
                    break;
            }
            StatusText = $"Locating {what} {owner}+0x{offset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(owner, offset, field);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport LocatePoseField ({kind}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the camera's POV Location FVector (CameraCachePrivate.POV.
    /// Location on the PlayerCameraManager) in GWorld.</summary>
    [RelayCommand]
    private Task LocateCameraLocationInGWorldAsync() => LocateCameraFieldAsync(CameraLocateKind.Location);

    /// <summary>Locate the camera's POV Rotation FRotator in GWorld.</summary>
    [RelayCommand]
    private Task LocateCameraRotationInGWorldAsync() => LocateCameraFieldAsync(CameraLocateKind.Rotation);

    /// <summary>Locate the camera's FOV float (CameraCachePrivate.POV.FOV) in GWorld.</summary>
    [RelayCommand]
    private Task LocateCameraFovInGWorldAsync() => LocateCameraFieldAsync(CameraLocateKind.Fov);

    private enum CameraLocateKind { Location, Rotation, Fov }

    private async Task LocateCameraFieldAsync(CameraLocateKind kind)
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var pov = await _dump.TeleportGetPovAsync();
            if (pov.Code != TeleportCodes.Ok)
            {
                StatusText = $"Get POV: {TeleportCodes.Describe(pov.Code)}";
                return;
            }
            ApplyPov(pov);

            string what, owner = pov.CamOwnerAddr, field;
            int offset;
            switch (kind)
            {
                case CameraLocateKind.Rotation:
                    what = "camera rotation";
                    if (!pov.HasCamRotAddr)
                    {
                        StatusText = "No camera rotation field to locate (cached-POV layout not reflected).";
                        return;
                    }
                    offset = pov.CamRotFieldOffset; field = pov.CamRotFieldName;
                    break;
                case CameraLocateKind.Fov:
                    what = "camera FOV";
                    if (!pov.HasCamFovAddr)
                    {
                        StatusText = "No camera FOV field to locate (cached-POV layout not reflected).";
                        return;
                    }
                    offset = pov.CamFovFieldOffset; field = pov.CamFovFieldName;
                    break;
                default: // Location
                    what = "camera location";
                    if (!pov.HasCamLocAddr)
                    {
                        StatusText = "No camera location field to locate (cached-POV layout not reflected).";
                        return;
                    }
                    offset = pov.CamLocFieldOffset; field = pov.CamLocFieldName;
                    break;
            }
            StatusText = $"Locating {what} {owner}+0x{offset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(owner, offset, field);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport LocateCameraField ({kind}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task LocateCurrentPoseAsync(bool useEngine)
    {
        if (!IsConnected) return;
        string root = useEngine ? "GameEngine" : "GWorld";
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = TeleportCodes.Describe(p.Code);
                return;
            }
            ApplyPoseAndMovement(p);
            if (!p.HasPawnAddr)
            {
                StatusText = "No pawn to locate (the current pose has no resolved pawn address).";
                return;
            }
            StatusText = $"Locating pawn {p.PawnAddr} in {root}…";
            if (useEngine) LocateInGameEngine?.Invoke(p.PawnAddr);
            else           LocateInGWorld?.Invoke(p.PawnAddr);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport LocateCurrentPose ({root}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Read the on-screen camera POV (location / rotation / FOV) — this
    /// is the APlayerCameraManager view, distinct from the pawn pose above. On
    /// games that drive the camera independently of the pawn (HD-2D / fixed-view)
    /// the two diverge. Read-only: there is no Set POV (the view is recomputed
    /// every tick). Use the Debug Camera buttons to actually move the camera.</summary>
    [RelayCommand]
    private async Task GetPovAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var pov = await _dump.TeleportGetPovAsync();
            if (pov.Code != TeleportCodes.Ok)
            {
                ClearPovDisplay();
                StatusText = $"Get POV: {TeleportCodes.Describe(pov.Code)}";
                return;
            }
            ApplyPov(pov);
            StatusText = "Camera POV read.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport GetPov failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveMarkerAsync(int slot)
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportSaveMarkerAsync(slot);
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = $"Save marker {slot + 1}: {TeleportCodes.Describe(p.Code)}";
                return;
            }
            ApplyPose(p);
            UpdateMarkerRow(slot, p);
            StatusText = $"Marker {slot + 1} saved.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport SaveMarker {slot} failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task RecallMarkerAsync(int slot) => RecallInternalAsync(slot, force: false);

    [RelayCommand]
    private Task ForceRecallMarkerAsync(int slot) => RecallInternalAsync(slot, force: true);

    private async Task RecallInternalAsync(int slot, bool force)
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.TeleportRecallMarkerAsync(slot, force);
            if (r.Code == TeleportCodes.MapMismatch)
            {
                StatusText = $"Marker {slot + 1} saved on '{r.MarkerMap}', you're on " +
                             $"'{r.CurrentMap}' — press Force to recall anyway.";
                return;
            }
            if (r.Code != TeleportCodes.Ok)
            {
                StatusText = $"Recall {slot + 1}: {TeleportCodes.Describe(r.Code)}";
                return;
            }
            StatusText = r.Tier == 2
                ? $"Recalled to marker {slot + 1} (raw write — game may snap back)."
                : $"Recalled to marker {slot + 1}.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport Recall {slot} failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ClearMarkerAsync(int slot)
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            await _dump.TeleportClearMarkerAsync(slot);
            var row = Markers[slot];
            row.Valid = false;
            row.Summary = "(empty)";
            StatusText = $"Marker {slot + 1} cleared.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Teleport ClearMarker {slot} failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RecallLastAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.TeleportRecallLastAsync();
            if (r.Code == TeleportCodes.EmptyMarker)
            {
                StatusText = "No last position yet — recall/force/BugItGo/cursor " +
                             "auto-saves it before each jump.";
                return;
            }
            if (r.Code != TeleportCodes.Ok)
            {
                StatusText = $"Recall last: {TeleportCodes.Describe(r.Code)}";
                return;
            }
            StatusText = r.Tier == 2
                ? "Recalled to last position (raw write — game may snap back)."
                : "Recalled to last position.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport RecallLast failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TeleportToCursorAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.TeleportToCursorAsync(ZOffset, TraceChannel, FallbackToCenter);
            if (r.Code != TeleportCodes.Ok)
            {
                StatusText = $"Cursor teleport: {TeleportCodes.Describe(r.Code)}";
                return;
            }
            string where = r.UsedCenter ? "screen center" : "cursor";
            StatusText = r.Tier == 2
                ? $"Teleported to {where} (raw write — game may snap back)."
                : $"Teleported to {where}.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport ToCursor failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CopyAsBugItGoAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = TeleportCodes.Describe(p.Code);
                return;
            }
            ApplyPoseAndMovement(p);
            string s = string.Format(CultureInfo.InvariantCulture,
                "BugItGo {0:0.000} {1:0.000} {2:0.000}", p.X, p.Y, p.Z);
            // Paste straight into the Run field so the user can fire BugItGo
            // immediately, and keep the clipboard copy for pasting elsewhere.
            BugItGoInput = s;
            await _platform.CopyToClipboardAsync(s);
            StatusText = $"BugIt: {s} (copied + filled the Run field).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport CopyAsBugItGo failed", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunBugItGoAsync()
    {
        if (!IsConnected) return;
        if (string.IsNullOrWhiteSpace(BugItGoInput))
        {
            StatusText = "BugItGo field is empty — press 'Copy as BugItGo' to capture " +
                         "the current pose, or paste coordinates first.";
            return;
        }
        if (!BugItGoParser.TryParse(BugItGoInput, out var t) || t is null)
        {
            StatusText = "Could not parse — expected 'BugItGo X Y Z', 'X Y Z', or a " +
                         "?BugLoc=(...)?BugRot=(...) string.";
            return;
        }
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.TeleportRecallExplicitAsync(
                t.X, t.Y, t.Z, t.Pitch, t.Yaw, t.Roll);
            if (r.Code != TeleportCodes.Ok)
            {
                StatusText = $"BugItGo: {TeleportCodes.Describe(r.Code)}";
                return;
            }
            StatusText = r.Tier == 2
                ? $"Teleported to ({t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}) (raw write)."
                : $"Teleported to ({t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport RunBugItGo failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Debug Camera (deterministic force on/off, shared with Console) ──

    /// <summary>Map the DLL tri-state (1=on, 0=off, -1=unknown) onto the badge.</summary>
    private void ApplyDebugCameraState(int state)
        => (DebugCameraState, DebugCameraBadgeColor) = state switch
        {
            1  => ("ON",      "#4EC9B0"),   // green — active
            0  => ("OFF",     "#999999"),   // grey — inactive
            _  => ("Unknown", "#888888"),
        };

    [RelayCommand]
    private Task ForceDebugCameraOnAsync()  => ForceDebugCameraAsync(wantOn: true);

    [RelayCommand]
    private Task ForceDebugCameraOffAsync() => ForceDebugCameraAsync(wantOn: false);

    /// <summary>↻ — re-read and display the live Debug Camera state.</summary>
    [RelayCommand]
    private async Task RefreshDebugCameraAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.GetDebugCameraStateAsync();
            ApplyDebugCameraState(state);
            StatusText = state switch
            {
                1 => "Debug Camera is ON.",
                0 => "Debug Camera is OFF.",
                _ => "Debug Camera state unknown — enter gameplay so a PlayerController " +
                     "spawns, then ↻.",
            };
        }
        catch (Exception ex)
        {
            ApplyDebugCameraState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshDebugCamera failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Deterministic enable/disable — delegates to the DLL
    /// (set_debug_camera), which reads state, toggles only when needed, and on a
    /// disable the game's stripped ToggleDebugCamera can't honour, switches the
    /// local player's controller back to the original PlayerController. Idempotent.</summary>
    private async Task ForceDebugCameraAsync(bool wantOn)
    {
        if (!IsConnected) return;
        var want = wantOn ? "ON" : "OFF";
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.SetDebugCameraAsync(wantOn);
            ApplyDebugCameraState(state);
            StatusText = state switch
            {
                1 when wantOn  => "✓ Debug Camera forced ON.",
                0 when !wantOn => "✓ Debug Camera forced OFF.",
                -1 => $"Force {want}: no live CheatManager / unreadable state " +
                      "(enter gameplay first).",
                _  => $"⚠ Force {want}: state is now {(state == 1 ? "ON" : "OFF")} " +
                      "— the game may re-drive the camera.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Force {want} failed: {ex.Message}";
            _log.Error($"Teleport ForceDebugCamera({want}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── God Mode (force AActor.bCanBeDamaged, Solitar) ─────────────────

    /// <summary>Map the DLL tri-state (1=immune, 0=can be damaged, &lt;0=unknown)
    /// onto the badge.</summary>
    private void ApplyGodModeState(int state)
        => (GodModeState, GodModeBadgeColor) = state switch
        {
            1 => ("ON",      "#4EC9B0"),   // green — immune
            0 => ("OFF",     "#999999"),   // grey — can be damaged
            _ => ("Unknown", "#888888"),
        };

    [RelayCommand]
    private Task ForceGodModeOnAsync()  => ForceGodModeAsync(wantOn: true);

    [RelayCommand]
    private Task ForceGodModeOffAsync() => ForceGodModeAsync(wantOn: false);

    /// <summary>↻ — re-read and display the live God Mode state.</summary>
    [RelayCommand]
    private async Task RefreshGodModeAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.GetGodModeAsync();
            ApplyGodModeState(state);
            StatusText = state switch
            {
                1 => "God Mode is ON (bCanBeDamaged = false).",
                0 => "God Mode is OFF.",
                _ => "God Mode state unknown — enter gameplay so a pawn spawns, then ↻.",
            };
        }
        catch (Exception ex)
        {
            ApplyGodModeState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshGodMode failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Force God Mode on/off — delegates to the DLL (set_god_mode), which
    /// resolves the pawn, writes bCanBeDamaged, and runs a re-assert worker so the
    /// flag survives respawns. Idempotent.</summary>
    private async Task ForceGodModeAsync(bool wantOn)
    {
        if (!IsConnected) return;
        var want = wantOn ? "ON" : "OFF";
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.SetGodModeAsync(wantOn);
            ApplyGodModeState(state);
            StatusText = state switch
            {
                1 when wantOn  => "✓ God Mode forced ON (bCanBeDamaged = false).",
                0 when !wantOn => "✓ God Mode forced OFF.",
                < 0 => $"Force {want}: no pawn / unreadable (enter gameplay first) — " +
                       "the flag will apply once a pawn exists.",
                _  => $"⚠ Force {want}: state is now {(state == 1 ? "ON" : "OFF")}.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"God Mode force {want} failed: {ex.Message}";
            _log.Error($"Teleport ForceGodMode({want}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Copy a self-contained CE AA script (tick = ON, untick = OFF) that
    /// drives God Mode through the DLL mailbox (CMD_PROTECT). Paste into a CE
    /// memory record. Works offline — it's static text.</summary>
    [RelayCommand]
    private async Task CopyGodModeScriptAsync()
    {
        try
        {
            await _platform.CopyToClipboardAsync(
                UE5DumpUI.Services.ProtectionScriptGenerator.Generate());
            StatusText = "Copied GodMode CE script (paste into a CE memory record; " +
                         "tick = ON, untick = OFF).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport CopyGodModeScript failed", ex);
        }
    }

    // ── Move Speed multiplier (force MaxWalkSpeed, Laufen) ─────────────

    /// <summary>Tracks whether the move-speed override is held (so the global
    /// hotkey can toggle it).</summary>
    private bool _moveSpeedActive;

    /// <summary>Map the override state (1 = held, 0 = off, &lt;0 = no pawn / no
    /// CMC) onto the badge.</summary>
    private void ApplyMoveSpeedState(int state)
    {
        _moveSpeedActive = state == 1;
        (MoveSpeedState, MoveSpeedBadgeColor) = state switch
        {
            1 => ("ON",          "#4EC9B0"),   // green — override held
            0 => ("OFF",         "#999999"),   // grey — base restored
            _ => ("Unavailable", "#C9A04E"),   // amber — no pawn / no CharacterMovement
        };
    }

    /// <summary>Format the live "Current: X (base Y)" readout from a knob.</summary>
    private void ApplyMoveSpeedReadout(MovementParams mp)
    {
        var k = mp.WalkSpeed;
        if (!mp.HasCmc || !k.Resolved)
        {
            MoveSpeedCurrentText = "Current: unavailable (no CharacterMovement on this pawn)";
            ApplyMoveSpeedState(-1);
            return;
        }
        MoveSpeedCurrentText = k.Active
            ? string.Format(CultureInfo.InvariantCulture,
                "Current: {0:0.#} cm/s  (base {1:0.#} × {2:0.##})", k.Current, k.Base, k.Multiplier)
            : string.Format(CultureInfo.InvariantCulture, "Current: {0:0.#} cm/s", k.Current);
        ApplyMoveSpeedState(k.Active ? 1 : 0);
    }

    /// <summary>↻ — re-read the live walk-speed value + override state.</summary>
    [RelayCommand]
    private async Task RefreshMoveSpeedAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyMoveSpeedReadout(mp);
            StatusText = mp.HasCmc
                ? "Read movement params."
                : "No CharacterMovement on the current pawn (enter gameplay, or this pawn is a vehicle / custom-movement type).";
        }
        catch (Exception ex)
        {
            ApplyMoveSpeedState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshMoveSpeed failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Apply the slider multiplier to MaxWalkSpeed and hold it via the
    /// DLL re-assert worker (base is captured DLL-side on first apply, so the
    /// multiplier never compounds and Reset restores the true original).</summary>
    [RelayCommand]
    private async Task ApplyMoveSpeedAsync()
    {
        if (!IsConnected) return;
        double mult = MoveSpeedMultiplier;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.SetMovementMultiplierAsync("walk_speed", mult);
            // Refresh the live readout so the user sees the new held value.
            var mp = await _dump.GetMovementParamsAsync();
            ApplyMoveSpeedReadout(mp);
            StatusText = r.State switch
            {
                1   => string.Format(CultureInfo.InvariantCulture,
                         "✓ Walk speed held at {0:0}% ({1:0.#} cm/s). Drag + Apply to change; Reset to restore.",
                         mult * 100.0, mp.WalkSpeed.Current),
                < 0 => "No pawn / no CharacterMovement — enter gameplay first; the override applies once a pawn exists.",
                _   => "Walk speed override is off.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Apply walk speed failed: {ex.Message}";
            _log.Error("Teleport ApplyMoveSpeed failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Restore MaxWalkSpeed to its captured base and stop holding it.</summary>
    [RelayCommand]
    private async Task ResetMoveSpeedAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            await _dump.ResetMovementAsync("walk_speed");
            MoveSpeedExponent = 0.0;   // slider back to 100%
            var mp = await _dump.GetMovementParamsAsync();
            ApplyMoveSpeedReadout(mp);
            StatusText = "Walk speed restored to base.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport ResetMoveSpeed failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the MaxWalkSpeed field in GWorld — lands on the exact float
    /// on the CharacterMovement so the user can freeze / inspect it in the Live
    /// Walker (same handoff the per-vector pose locators use).</summary>
    [RelayCommand]
    private async Task LocateMoveSpeedInGWorldAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyMoveSpeedReadout(mp);
            var k = mp.WalkSpeed;
            if (!k.HasAddr)
            {
                StatusText = "No MaxWalkSpeed field to locate (this pawn has no CharacterMovement).";
                return;
            }
            StatusText = $"Locating {k.FieldName} {k.OwnerAddr}+0x{k.FieldOffset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(k.OwnerAddr, k.FieldOffset, k.FieldName);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport LocateMoveSpeed failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Gravity multiplier (force GravityScale, Laufen) ────────────────

    /// <summary>Tracks whether the gravity override is held (hotkey toggle).</summary>
    private bool _gravityActive;

    private void ApplyGravityState(int state)
    {
        _gravityActive = state == 1;
        (GravityState, GravityBadgeColor) = state switch
        {
            1 => ("ON",          "#4EC9B0"),
            0 => ("OFF",         "#999999"),
            _ => ("Unavailable", "#C9A04E"),
        };
    }

    private void ApplyGravityReadout(MovementParams mp)
    {
        var k = mp.Gravity;
        if (!mp.HasCmc || !k.Resolved)
        {
            GravityCurrentText = "Current: unavailable (no CharacterMovement on this pawn)";
            ApplyGravityState(-1);
            return;
        }
        GravityCurrentText = k.Active
            ? string.Format(CultureInfo.InvariantCulture,
                "Current: {0:0.###}×  (base {1:0.###} × {2:0.##})", k.Current, k.Base, k.Multiplier)
            : string.Format(CultureInfo.InvariantCulture, "Current: {0:0.###}× GravityScale", k.Current);
        ApplyGravityState(k.Active ? 1 : 0);
    }

    /// <summary>↻ — re-read the live GravityScale + override state.</summary>
    [RelayCommand]
    private async Task RefreshGravityAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravityReadout(mp);
            StatusText = mp.HasCmc ? "Read movement params."
                : "No CharacterMovement on the current pawn.";
        }
        catch (Exception ex)
        {
            ApplyGravityState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshGravity failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Apply the slider multiplier to GravityScale and hold it.</summary>
    [RelayCommand]
    private async Task ApplyGravityAsync()
    {
        if (!IsConnected) return;
        double mult = GravityMultiplier;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.SetMovementMultiplierAsync("gravity", mult);
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravityReadout(mp);
            StatusText = r.State switch
            {
                1   => string.Format(CultureInfo.InvariantCulture,
                         "✓ Gravity held at {0:0}% ({1:0.###}× GravityScale).", mult * 100.0, mp.Gravity.Current),
                < 0 => "No pawn / no CharacterMovement — enter gameplay first.",
                _   => "Gravity override is off.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Apply gravity failed: {ex.Message}";
            _log.Error("Teleport ApplyGravity failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Restore GravityScale to its captured base.</summary>
    [RelayCommand]
    private async Task ResetGravityAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            await _dump.ResetMovementAsync("gravity");
            GravityExponent = 0.0;
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravityReadout(mp);
            StatusText = "Gravity restored to base.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport ResetGravity failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the GravityScale field in GWorld.</summary>
    [RelayCommand]
    private async Task LocateGravityInGWorldAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravityReadout(mp);
            var k = mp.Gravity;
            if (!k.HasAddr)
            {
                StatusText = "No GravityScale field to locate (this pawn has no CharacterMovement).";
                return;
            }
            StatusText = $"Locating {k.FieldName} {k.OwnerAddr}+0x{k.FieldOffset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(k.OwnerAddr, k.FieldOffset, k.FieldName);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport LocateGravity failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Super Jump (force JumpZVelocity, Laufen) ───────────────────────

    private void ApplySuperJumpState(int state)
    {
        _superJumpActive = state == 1;
        (SuperJumpState, SuperJumpBadgeColor) = state switch
        {
            1 => ("ON",          "#4EC9B0"),
            0 => ("OFF",         "#999999"),
            _ => ("Unavailable", "#C9A04E"),
        };
    }

    private void ApplySuperJumpReadout(MovementParams mp)
    {
        var k = mp.Jump;
        if (!mp.HasCmc || !k.Resolved)
        {
            SuperJumpCurrentText = "Current: unavailable (no CharacterMovement on this pawn)";
            ApplySuperJumpState(-1);
            return;
        }
        SuperJumpCurrentText = k.Active
            ? string.Format(CultureInfo.InvariantCulture,
                "Current: {0:0.#} cm/s  (base {1:0.#} × {2:0.##} → ~{3:0}% height)",
                k.Current, k.Base, k.Multiplier, k.Multiplier * k.Multiplier * 100.0)
            : string.Format(CultureInfo.InvariantCulture, "Current: {0:0.#} cm/s JumpZVelocity", k.Current);
        ApplySuperJumpState(k.Active ? 1 : 0);
    }

    [RelayCommand]
    private Task ForceSuperJumpOnAsync()  => SetSuperJumpAsync(wantOn: true);

    [RelayCommand]
    private Task ForceSuperJumpOffAsync() => SetSuperJumpAsync(wantOn: false);

    /// <summary>Reset Super Jump: turn the override off AND snap the height slider
    /// back to 100% (parity with the Move Speed / Gravity Reset buttons).</summary>
    [RelayCommand]
    private async Task ResetSuperJumpAsync()
    {
        SuperJumpExponent = 0.0;
        await ResetSuperJumpInternalAsync();
    }

    /// <summary>↻ — re-read the live JumpZVelocity + override state.</summary>
    [RelayCommand]
    private async Task RefreshSuperJumpAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplySuperJumpReadout(mp);
            StatusText = mp.HasCmc ? "Read movement params."
                : "No CharacterMovement on the current pawn.";
        }
        catch (Exception ex)
        {
            ApplySuperJumpState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshSuperJump failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Toggle the high-jump override (for the global hotkey): ON applies
    /// the slider's jump-height multiplier and holds it; OFF restores the base.</summary>
    private Task SetSuperJumpAsync(bool wantOn)
        => wantOn ? ApplySuperJumpInternalAsync() : ResetSuperJumpInternalAsync();

    private async Task ApplySuperJumpInternalAsync()
    {
        if (!IsConnected) return;
        // Apply the VELOCITY multiplier = √(height multiplier); apex height ∝ v².
        double velMult = SuperJumpVelocityMultiplier;
        double heightPct = SuperJumpHeightMultiplier * 100.0;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.SetMovementMultiplierAsync("jump", velMult);
            var mp = await _dump.GetMovementParamsAsync();
            ApplySuperJumpReadout(mp);
            StatusText = r.State switch
            {
                1   => string.Format(CultureInfo.InvariantCulture,
                         "✓ Super Jump ON — ~{0:0}% jump height (JumpZVelocity ×{1:0.##}).", heightPct, velMult),
                < 0 => "No pawn / no CharacterMovement — enter gameplay first.",
                _   => "Super Jump is off.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Super Jump ON failed: {ex.Message}";
            _log.Error("Teleport ApplySuperJump failed", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task ResetSuperJumpInternalAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            await _dump.ResetMovementAsync("jump");
            var mp = await _dump.GetMovementParamsAsync();
            ApplySuperJumpReadout(mp);
            StatusText = "Super Jump OFF — jump velocity restored to base.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport ResetSuperJump failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the JumpZVelocity field in GWorld.</summary>
    [RelayCommand]
    private async Task LocateSuperJumpInGWorldAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplySuperJumpReadout(mp);
            var k = mp.Jump;
            if (!k.HasAddr)
            {
                StatusText = "No JumpZVelocity field to locate (this pawn has no CharacterMovement).";
                return;
            }
            StatusText = $"Locating {k.FieldName} {k.OwnerAddr}+0x{k.FieldOffset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(k.OwnerAddr, k.FieldOffset, k.FieldName);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport LocateSuperJump failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Gravity Direction (force GravityDirection vector, Laufen — UE5.4+) ──

    private void ApplyGravDirState(int state)
    {
        _gravDirActive = state == 1;
        (GravDirState, GravDirBadgeColor) = state switch
        {
            1 => ("ON",          "#4EC9B0"),
            0 => ("OFF",         "#999999"),
            _ => ("Unavailable", "#C9A04E"),   // pre-5.4 / no reflected GravityDirection
        };
    }

    private void ApplyGravDirReadout(MovementParams mp)
    {
        var g = mp.GravityDirection;
        if (!mp.HasCmc || !g.Resolved)
        {
            GravDirCurrentText = "Current: unavailable (needs UE5.4+ with a reflected GravityDirection).";
            ApplyGravDirState(-1);
            return;
        }
        GravDirCurrentText = string.Format(CultureInfo.InvariantCulture,
            "Current: ({0:0.00}, {1:0.00}, {2:0.00})", g.X, g.Y, g.Z);
        ApplyGravDirState(g.Active ? 1 : 0);
    }

    /// <summary>↻ — re-read the live gravity direction + override state.</summary>
    [RelayCommand]
    private async Task RefreshGravDirAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravDirReadout(mp);
            StatusText = (mp.HasCmc && mp.GravityDirection.Resolved)
                ? "Read gravity direction."
                : "Gravity direction unavailable (needs UE5.4+ with a reflected GravityDirection).";
        }
        catch (Exception ex)
        {
            ApplyGravDirState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshGravDir failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Apply the slider direction (X/Y/Z in [-1,1]) and hold it. The DLL
    /// normalizes; (0,0,0) is treated as OFF.</summary>
    [RelayCommand]
    private async Task ApplyGravDirAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var r = await _dump.SetGravityDirectionAsync(GravDirX, GravDirY, GravDirZ);
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravDirReadout(mp);
            var g = mp.GravityDirection;
            StatusText = r.State switch
            {
                1 => string.Format(CultureInfo.InvariantCulture,
                       "✓ Gravity direction held at ({0:0.00}, {1:0.00}, {2:0.00}).", g.X, g.Y, g.Z),
                0 => "Gravity direction off (a zero vector = off).",
                _ when !r.Resolved => "Gravity direction unavailable — needs UE5.4+ (no reflected GravityDirection).",
                _ => "Gravity direction: no pawn / no CharacterMovement (enter gameplay first).",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Apply gravity direction failed: {ex.Message}";
            _log.Error("Teleport ApplyGravDir failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Restore gravity direction to the captured default + reset sliders
    /// to (0,0,-1) = straight down.</summary>
    [RelayCommand]
    private async Task ResetGravDirAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            await _dump.ResetGravityDirectionAsync();
            GravDirX = 0; GravDirY = 0; GravDirZ = -1;   // sliders back to "down"
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravDirReadout(mp);
            StatusText = "Gravity direction restored to the game default.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport ResetGravDir failed", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Locate the GravityDirection FVector in GWorld.</summary>
    [RelayCommand]
    private async Task LocateGravDirInGWorldAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var mp = await _dump.GetMovementParamsAsync();
            ApplyGravDirReadout(mp);
            var g = mp.GravityDirection;
            if (!g.HasAddr)
            {
                StatusText = "No GravityDirection field to locate (needs UE5.4+).";
                return;
            }
            StatusText = $"Locating {g.FieldName} {g.OwnerAddr}+0x{g.FieldOffset:X} in GWorld…";
            LocateValueInGWorld?.Invoke(g.OwnerAddr, g.FieldOffset, g.FieldName);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport LocateGravDir failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Directional + explicit-coordinate teleport ─────────────────────

    /// <summary>Teleport along the pawn's facing by RelativeDistance uu (negative
    /// = backward). Horizontal keeps Z; otherwise follows pitch. Undoable via
    /// Recall last.</summary>
    [RelayCommand]
    private async Task TeleportRelativeAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportRelativeAsync(RelativeDistance, RelativeHorizontal);
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = $"TP facing: {TeleportCodes.Describe(p.Code)}";
                return;
            }
            ApplyPose(p);
            StatusText = string.Format(CultureInfo.InvariantCulture,
                "Teleported {0:0.#} uu {1} → ({2:0.0}, {3:0.0}, {4:0.0}).",
                RelativeDistance, RelativeHorizontal ? "horizontally" : "in 3D",
                p.X, p.Y, p.Z);
        }
        catch (Exception ex) { SetError(ex); _log.Error("Teleport Relative failed", ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Force-teleport to the explicit X/Y/Z (and optional rotation) — no
    /// map check. Undoable via Recall last.</summary>
    [RelayCommand]
    private async Task TeleportToCoordsAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var r = CoordSetRotation
                ? await _dump.TeleportRecallExplicitAsync(CoordX, CoordY, CoordZ,
                    CoordPitch, CoordYaw, CoordRoll)
                : await _dump.TeleportRecallExplicitAsync(CoordX, CoordY, CoordZ);
            if (r.Code != TeleportCodes.Ok)
            {
                StatusText = $"TP to coords: {TeleportCodes.Describe(r.Code)}";
                return;
            }
            StatusText = string.Format(CultureInfo.InvariantCulture,
                r.Tier == 2
                    ? "Teleported to ({0:0.0}, {1:0.0}, {2:0.0}) (raw write — game may snap back)."
                    : "Teleported to ({0:0.0}, {1:0.0}, {2:0.0}).",
                CoordX, CoordY, CoordZ);
        }
        catch (Exception ex) { SetError(ex); _log.Error("Teleport ToCoords failed", ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Populate the X/Y/Z (and rotation) fields from the current pose.</summary>
    [RelayCommand]
    private async Task FillCoordsFromCurrentAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code != TeleportCodes.Ok)
            {
                StatusText = TeleportCodes.Describe(p.Code);
                return;
            }
            ApplyPoseAndMovement(p);
            CoordX = p.X; CoordY = p.Y; CoordZ = p.Z;
            CoordPitch = p.Pitch; CoordYaw = p.Yaw; CoordRoll = p.Roll;
            StatusText = "Filled coordinates from the current pose.";
        }
        catch (Exception ex) { SetError(ex); _log.Error("Teleport FillCoords failed", ex); }
        finally { IsBusy = false; }
    }

    // ── Force mouse cursor on/off (shared with Lua via set_mouse_cursor) ─

    /// <summary>Map the DLL tri-state (1=on, 0=off, -1=unknown) onto the badge.</summary>
    private void ApplyMouseCursorState(int state)
        => (MouseCursorState, MouseCursorBadgeColor) = state switch
        {
            1 => ("ON",      "#4EC9B0"),
            0 => ("OFF",     "#999999"),
            _ => ("Unknown", "#888888"),
        };

    [RelayCommand]
    private Task ForceCursorOnAsync()  => ForceCursorAsync(show: true);

    [RelayCommand]
    private Task ForceCursorOffAsync() => ForceCursorAsync(show: false);

    /// <summary>↻ — re-read and display the live bShowMouseCursor state.</summary>
    [RelayCommand]
    private async Task RefreshCursorAsync()
    {
        if (!IsConnected) return;
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.GetMouseCursorAsync();
            ApplyMouseCursorState(state);
            StatusText = state switch
            {
                1 => "Mouse cursor is ON.",
                0 => "Mouse cursor is OFF.",
                _ => "Cursor state unknown — enter gameplay so a PlayerController spawns.",
            };
        }
        catch (Exception ex)
        {
            ApplyMouseCursorState(-1);
            SetError(ex);
            _log.Error("Teleport RefreshCursor failed", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task ForceCursorAsync(bool show)
    {
        if (!IsConnected) return;
        var want = show ? "ON" : "OFF";
        try
        {
            IsBusy = true;
            ClearError();
            var state = await _dump.SetMouseCursorAsync(show);
            ApplyMouseCursorState(state);
            StatusText = state switch
            {
                1 when show  => "✓ Mouse cursor forced ON.",
                0 when !show => "✓ Mouse cursor forced OFF.",
                -1 => $"Force cursor {want}: no live PlayerController / unresolved " +
                      "(enter gameplay first).",
                _  => $"⚠ Force cursor {want}: state is now {(state == 1 ? "ON" : "OFF")} " +
                      "— the game may re-set it each frame.",
            };
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Force cursor {want} failed: {ex.Message}";
            _log.Error($"Teleport ForceCursor({want}) failed", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Marker global hotkeys (user-set via key capture) ────────────────

    private void LoadAndRegisterHotkeys()
    {
        if (_hotkeyStore == null || _globalHotkeys == null) return;
        var saved = _hotkeyStore.Load();
        var failed = new List<string>();
        foreach (var row in HotkeyRows)
        {
            if (!saved.TryGetValue(row.ActionId, out var b)) continue;
            // Always show the saved combo so the user knows what was bound, even
            // when registration fails — otherwise a hotkey silently goes dead and
            // the row looks unbound.
            row.Label = b.Label;
            if (RegisterMarkerHotkey(row.ActionId, b))
            {
                _bindings[row.ActionId] = b;
                row.Conflicted = false;
            }
            else
            {
                // Combo taken by another app this session. Keep it persisted (it
                // may be free next launch) and flag the row instead of dropping it.
                row.Conflicted = true;
                failed.Add($"{row.DisplayName} ({b.Label})");
            }
        }
        UpdateHotkeyWarning(failed);
    }

    /// <summary>Build (or clear) the top conflict banner from the list of rows
    /// whose saved combo could not be registered.</summary>
    private void UpdateHotkeyWarning(List<string> failed)
    {
        HotkeyWarning = failed.Count == 0
            ? ""
            : $"⚠ {failed.Count} hotkey(s) couldn't be bound — held by another app: " +
              $"{string.Join(", ", failed)}. Free the combo and reconnect, or re-bind below.";
    }

    /// <summary>Recompute the banner from the current per-row Conflicted flags
    /// (called after a successful re-bind or a clear).</summary>
    private void RecomputeHotkeyWarning()
        => UpdateHotkeyWarning(HotkeyRows
            .Where(r => r.Conflicted && r.HasBinding)
            .Select(r => $"{r.DisplayName} ({r.Label})")
            .ToList());

    private bool RegisterMarkerHotkey(string actionId, TeleportHotkeyBinding b)
    {
        if (_globalHotkeys == null) return false;
        if (_markerHotkeys.TryGetValue(actionId, out var old))
        {
            old.Dispose();
            _markerHotkeys.Remove(actionId);
        }
        var reg = _globalHotkeys.RegisterSpecific(b.WinMods, b.Vk, b.Label,
            () => OnMarkerHotkeyPressed(actionId));
        if (reg == null) return false;
        _markerHotkeys[actionId] = reg;
        return true;
    }

    private void OnMarkerHotkeyPressed(string actionId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var row = HotkeyRows.FirstOrDefault(r => r.ActionId == actionId);
            string what = row?.DisplayName ?? actionId;
            if (!CanOperate)
            {
                // Honest feedback: the hotkey fired but nothing was sent.
                MarkHotkeyFired(what, ran: false);
                return;
            }
            MarkHotkeyFired(what, ran: true);
            // Non-slot actions first (must precede the digit-suffix parse below;
            // "recall_last" also starts with "recall" but has no slot).
            switch (actionId)
            {
                case "recall_last":  _ = RecallLastCommand.ExecuteAsync(null); return;
                case "bugit":        _ = CopyAsBugItGoCommand.ExecuteAsync(null); return;
                case "bugitgo":      _ = RunBugItGoCommand.ExecuteAsync(null); return;
                case "debugcam_on":  _ = ForceDebugCameraOnCommand.ExecuteAsync(null); return;
                case "debugcam_off": _ = ForceDebugCameraOffCommand.ExecuteAsync(null); return;
                case "godmode_on":   _ = ForceGodModeOnCommand.ExecuteAsync(null); return;
                case "godmode_off":  _ = ForceGodModeOffCommand.ExecuteAsync(null); return;
                case "superjump_toggle":
                    _ = (_superJumpActive ? ForceSuperJumpOffCommand : ForceSuperJumpOnCommand)
                        .ExecuteAsync(null);
                    return;
                case "movespeed_toggle":
                    _ = (_moveSpeedActive ? ResetMoveSpeedCommand : ApplyMoveSpeedCommand)
                        .ExecuteAsync(null);
                    return;
                case "gravity_toggle":
                    _ = (_gravityActive ? ResetGravityCommand : ApplyGravityCommand)
                        .ExecuteAsync(null);
                    return;
                case "gravdir_toggle":
                    _ = (_gravDirActive ? ResetGravDirCommand : ApplyGravDirCommand)
                        .ExecuteAsync(null);
                    return;
                case "pov_get":      _ = GetPovCommand.ExecuteAsync(null); return;
                case "relative":     _ = TeleportRelativeCommand.ExecuteAsync(null); return;
                case "coords":       _ = TeleportToCoordsCommand.ExecuteAsync(null); return;
                case "cursor_on":    _ = ForceCursorOnCommand.ExecuteAsync(null); return;
                case "cursor_off":   _ = ForceCursorOffCommand.ExecuteAsync(null); return;
            }
            int slot = actionId[^1] - '0';
            if (actionId.StartsWith("save", StringComparison.Ordinal))
                _ = SaveMarkerCommand.ExecuteAsync(slot);
            else if (actionId.StartsWith("recall", StringComparison.Ordinal))
                _ = RecallMarkerCommand.ExecuteAsync(slot);
        });
    }

    private void MarkHotkeyFired(string what, bool ran)
        => LastHotkeyFired = ran
            ? $"{what} fired @ {DateTime.Now:HH:mm:ss}"
            : $"{what} pressed @ {DateTime.Now:HH:mm:ss} — ignored (not connected)";

    /// <summary>Begin (or cancel) capturing a key combo for a marker hotkey row.
    /// Clicking Set starts capture; clicking again (now labelled Cancel) aborts
    /// and keeps the existing binding. The panel code-behind forwards KeyDown to
    /// <see cref="ApplyCapturedKey"/> while capturing.</summary>
    [RelayCommand]
    private void BeginCapture(TeleportHotkeyRow? row)
    {
        if (row == null || _globalHotkeys == null) return;
        if (CapturingRow == row)        // toggle: clicking "Cancel" aborts
        {
            row.IsCapturing = false;
            CapturingRow = null;
            StatusText = "Hotkey capture cancelled.";
            return;
        }
        if (CapturingRow != null) CapturingRow.IsCapturing = false;
        CapturingRow = row;
        row.IsCapturing = true;
        StatusText = $"Press a key combo for '{row.DisplayName}' (hold Ctrl/Alt/Shift then a key; Esc or Cancel to abort)…";
    }

    /// <summary>Called by the code-behind on KeyDown while a row is capturing.
    /// Returns true when the capture consumed the key (handled).</summary>
    public bool ApplyCapturedKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods)
    {
        var row = CapturingRow;
        if (row == null) return false;

        if (key == Avalonia.Input.Key.Escape)
        {
            row.IsCapturing = false;
            CapturingRow = null;
            StatusText = "Hotkey capture cancelled.";
            return true;
        }
        if (!HotkeyKeyMap.TryConvert(key, mods, out uint winMods, out uint vk, out string label))
            return false;   // modifier-only / unsupported → keep listening

        row.IsCapturing = false;
        CapturingRow = null;

        var binding = new TeleportHotkeyBinding(winMods, vk);
        if (!RegisterMarkerHotkey(row.ActionId, binding))
        {
            StatusText = $"'{label}' is already in use by another app — pick a different combo.";
            return true;
        }
        _bindings[row.ActionId] = binding;
        row.Label = label;
        row.Conflicted = false;
        RecomputeHotkeyWarning();
        _hotkeyStore?.Save(_bindings);
        StatusText = $"'{row.DisplayName}' bound to {label}. Keep the game focused and press it.";
        _log.Info($"Teleport: {row.ActionId} hotkey bound to {label}");
        return true;
    }

    [RelayCommand]
    private void ClearHotkey(TeleportHotkeyRow? row)
    {
        if (row == null) return;
        if (_markerHotkeys.TryGetValue(row.ActionId, out var reg))
        {
            reg.Dispose();
            _markerHotkeys.Remove(row.ActionId);
        }
        _bindings.Remove(row.ActionId);
        _hotkeyStore?.Save(_bindings);
        row.Label = "";
        row.Conflicted = false;
        row.IsCapturing = false;
        if (CapturingRow == row) CapturingRow = null;
        RecomputeHotkeyWarning();
        StatusText = $"'{row.DisplayName}' hotkey cleared.";
    }

    [RelayCommand]
    private async Task AddActionsToCeAsync()
    {
        try
        {
            ClearError();
            bool available = _aobMaker != null && await _aobMaker.CheckAvailabilityAsync();
            if (!available)
            {
                StatusText = "AOBMaker not connected — use 'Save .CT' instead " +
                             "(open Cheat Engine with the AOBMaker plugin loaded).";
                return;
            }
            int ok = 0;
            // 17 momentary auto-unticking records: Save 1-3, Recall 1-3, Recall
            // last, BugIt, BugItGo, Cursor, Get POV, Get coords, TP facing dir,
            // TP to coords, Cursor ON, Cursor OFF, Clear all. The directional/
            // coords records bake the current UI field values (distance/horiz/XYZ).
            var specs = new (string Desc, TeleportScriptGenerator.Action Act, int Slot)[]
            {
                ("Teleport: Save marker 1",   TeleportScriptGenerator.Action.Save,     0),
                ("Teleport: Save marker 2",   TeleportScriptGenerator.Action.Save,     1),
                ("Teleport: Save marker 3",   TeleportScriptGenerator.Action.Save,     2),
                ("Teleport: Recall marker 1", TeleportScriptGenerator.Action.Recall,   0),
                ("Teleport: Recall marker 2", TeleportScriptGenerator.Action.Recall,   1),
                ("Teleport: Recall marker 3", TeleportScriptGenerator.Action.Recall,   2),
                ("Teleport: Recall last",     TeleportScriptGenerator.Action.RecallLast, 0),
                ("Teleport: BugIt (store pose)", TeleportScriptGenerator.Action.BugIt,   0),
                ("Teleport: BugItGo (go to stored)", TeleportScriptGenerator.Action.BugItGo, 0),
                ("Teleport: To cursor",       TeleportScriptGenerator.Action.Cursor,   0),
                ("Teleport: Get camera POV",  TeleportScriptGenerator.Action.GetPov,   0),
                ("Teleport: Get current coords", TeleportScriptGenerator.Action.GetPose, 0),
                ("Teleport: TP facing direction", TeleportScriptGenerator.Action.Relative, 0),
                ("Teleport: TP to coordinates", TeleportScriptGenerator.Action.Explicit, 0),
                ("Teleport: Cursor ON",       TeleportScriptGenerator.Action.CursorOn,  0),
                ("Teleport: Cursor OFF",      TeleportScriptGenerator.Action.CursorOff, 0),
                ("Teleport: Clear all markers", TeleportScriptGenerator.Action.ClearAll, 0),
            };
            foreach (var s in specs)
            {
                string script = TeleportScriptGenerator.Generate(
                    s.Act, s.Slot, ZOffset, TraceChannel, FallbackToCenter,
                    RelativeDistance, RelativeHorizontal,
                    CoordX, CoordY, CoordZ, CoordSetRotation,
                    CoordPitch, CoordYaw, CoordRoll);
                if (await _aobMaker!.CreateAAScriptAsync(s.Desc, script, autoActivate: false))
                    ok++;
            }
            // Movement-tuning toggles (Laufen) baked at the current slider % —
            // stateful records (tick = apply %, untick = OFF). 100% = OFF.
            var moveSpecs = new (string Desc, MovementScriptGenerator.Knob Knob, double Percent)[]
            {
                ($"Movement: Move Speed ({MoveSpeedMultiplier * 100:0}%)",
                    MovementScriptGenerator.Knob.WalkSpeed, MoveSpeedMultiplier * 100),
                ($"Movement: Gravity ({GravityMultiplier * 100:0}%)",
                    MovementScriptGenerator.Knob.Gravity, GravityMultiplier * 100),
                ($"Movement: Super Jump ({SuperJumpHeightMultiplier * 100:0}% height)",
                    MovementScriptGenerator.Knob.Jump, SuperJumpHeightMultiplier * 100),
            };
            foreach (var s in moveSpecs)
            {
                string script = MovementScriptGenerator.Generate(s.Knob, s.Percent);
                if (await _aobMaker!.CreateAAScriptAsync(s.Desc, script, autoActivate: false))
                    ok++;
            }
            // Gravity Direction vector (UE5.4+) baked at the current sliders.
            string gdDesc = string.Format(CultureInfo.InvariantCulture,
                "Movement: Gravity Direction ({0:0.0#}, {1:0.0#}, {2:0.0#})", GravDirX, GravDirY, GravDirZ);
            string gdScript = MovementScriptGenerator.GenerateGravityDirection(GravDirX, GravDirY, GravDirZ);
            if (await _aobMaker!.CreateAAScriptAsync(gdDesc, gdScript, autoActivate: false))
                ok++;
            int total = specs.Length + moveSpecs.Length + 1;
            StatusText = $"Added {ok}/{total} Teleport + Movement records to CE " +
                         "(teleport = momentary; movement = on/off toggle; bind CE hotkeys as you like).";
            _log.Info($"Teleport + Movement actions -> CE via AOBMaker ({ok}/{total})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport AddActionsToCe failed", ex);
        }
    }

    [RelayCommand]
    private async Task SaveCtAsync()
    {
        try
        {
            var rows = TeleportScriptGenerator.BuildBatchRows(
                ZOffset, TraceChannel, FallbackToCenter,
                RelativeDistance, RelativeHorizontal,
                CoordX, CoordY, CoordZ, CoordSetRotation,
                CoordPitch, CoordYaw, CoordRoll);
            // Movement-tuning toggles (Laufen) baked at the current slider values.
            rows.AddRange(MovementScriptGenerator.BuildBatchRows(
                MoveSpeedMultiplier * 100, GravityMultiplier * 100,
                SuperJumpHeightMultiplier * 100,
                GravDirX, GravDirY, GravDirZ));
            string ct = CheatTableBuilder.Build("Teleport — UE5CEDumper", rows);
            var path = await _platform.ShowSaveFileDialogAsync(
                defaultFileName: CheatTableBuilder.DefaultFileName("Teleport", DateTime.Now),
                filterName:      "Cheat Engine Table (*.CT)",
                filterExtension: ".CT");
            if (string.IsNullOrEmpty(path))
            {
                StatusText = "Save .CT cancelled.";
                return;
            }
            await File.WriteAllTextAsync(path, ct,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = $"Saved: {Path.GetFileName(path)}";
            _log.Info($"Teleport .CT saved: {path}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport SaveCt failed", ex);
        }
    }

    private async Task RefreshMarkersAsync()
    {
        try
        {
            var markers = await _dump.TeleportGetMarkersAsync();
            foreach (var m in markers)
            {
                // slot == -1 is the system "last" sentinel (see Fern get_markers).
                if (m.Slot == -1)
                {
                    LastValid = m.Valid;
                    LastSummary = m.Valid
                        ? string.Format(CultureInfo.InvariantCulture,
                            "({0:0.0}, {1:0.0}, {2:0.0})  {3}", m.X, m.Y, m.Z, m.Map)
                        : "(saved automatically before each teleport)";
                    continue;
                }
                if (m.Slot < 0 || m.Slot >= Markers.Count) continue;
                var row = Markers[m.Slot];
                row.Valid = m.Valid;
                row.Summary = m.Valid
                    ? string.Format(CultureInfo.InvariantCulture,
                        "({0:0.0}, {1:0.0}, {2:0.0})  {3}", m.X, m.Y, m.Z, m.Map)
                    : "(empty)";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Teleport RefreshMarkers failed", ex);
        }
    }

    private void ApplyPose(TeleportPose p)
    {
        PoseX = p.X.ToString("0.000", CultureInfo.InvariantCulture);
        PoseY = p.Y.ToString("0.000", CultureInfo.InvariantCulture);
        PoseZ = p.Z.ToString("0.000", CultureInfo.InvariantCulture);
        PosePitch = p.Pitch.ToString("0.00", CultureInfo.InvariantCulture);
        PoseYaw = p.Yaw.ToString("0.00", CultureInfo.InvariantCulture);
        PoseRoll = p.Roll.ToString("0.00", CultureInfo.InvariantCulture);
        PoseMap = p.Map;
        PoseSource = p.Source;
    }

    // Pose PLUS the movement/pawn readout — only for teleport_get_pose results,
    // which alone carry pawn_addr + velocity/acceleration. The incidental
    // ApplyPose callers (Save marker / directional TP, whose responses lack those
    // fields) must NOT use this, or they would clobber the readout to
    // "unavailable" and clear the pawn address on a perfectly normal pawn.
    private void ApplyPoseAndMovement(TeleportPose p)
    {
        ApplyPose(p);
        PawnAddrDisplay = p.HasPawnAddr ? p.PawnAddr : "";
        ApplyMovement(p);
    }

    // Velocity/acceleration off the CharacterMovement (cm/s, cm/s²). When the
    // pawn has none (HasMovement false), show "—" + an explanatory note rather
    // than a misleading 0 — see TeleportPose.HasMovement.
    private void ApplyMovement(TeleportPose p)
    {
        if (p.HasMovement)
        {
            VelX = p.VelX.ToString("0.0", CultureInfo.InvariantCulture);
            VelY = p.VelY.ToString("0.0", CultureInfo.InvariantCulture);
            VelZ = p.VelZ.ToString("0.0", CultureInfo.InvariantCulture);
            AccX = p.AccX.ToString("0.0", CultureInfo.InvariantCulture);
            AccY = p.AccY.ToString("0.0", CultureInfo.InvariantCulture);
            AccZ = p.AccZ.ToString("0.0", CultureInfo.InvariantCulture);
            Speed = p.Speed.ToString("0.0", CultureInfo.InvariantCulture) + " cm/s";
            MovementNote = "";
        }
        else
        {
            VelX = VelY = VelZ = "—";
            AccX = AccY = AccZ = "—";
            Speed = "—";
            MovementNote = "Velocity/acceleration unavailable — this pawn has no "
                + "CharacterMovement (vehicle / custom movement framework).";
        }
    }

    private void ClearPoseDisplay()
    {
        PoseX = PoseY = PoseZ = "—";
        PosePitch = PoseYaw = PoseRoll = "—";
        PoseMap = PoseSource = "";
        PawnAddrDisplay = "";
        MovementNote = "";
        VelX = VelY = VelZ = "—";
        AccX = AccY = AccZ = "—";
        Speed = "—";
    }

    private void ApplyPov(TeleportPov p)
    {
        PovX = p.CamX.ToString("0.000", CultureInfo.InvariantCulture);
        PovY = p.CamY.ToString("0.000", CultureInfo.InvariantCulture);
        PovZ = p.CamZ.ToString("0.000", CultureInfo.InvariantCulture);
        PovPitch = p.Pitch.ToString("0.00", CultureInfo.InvariantCulture);
        PovYaw = p.Yaw.ToString("0.00", CultureInfo.InvariantCulture);
        PovRoll = p.Roll.ToString("0.00", CultureInfo.InvariantCulture);
        PovFov = p.Fov > 0
            ? p.Fov.ToString("0.0", CultureInfo.InvariantCulture) + "°"
            : "—";
        PovSource = p.Source;
        PovDelta = p.HasPawn
            ? string.Format(CultureInfo.InvariantCulture,
                "Δ to pawn: {0:0.0} uu — Get POV again after a teleport; if this barely " +
                "changes, the camera is independent of the pawn.", p.PawnDistance)
            : "Pawn position unavailable (no possessed pawn?).";
    }

    private void ClearPovDisplay()
    {
        PovX = PovY = PovZ = "—";
        PovPitch = PovYaw = PovRoll = "—";
        PovFov = "—";
        PovSource = "";
        PovDelta = "";
    }

    private void UpdateMarkerRow(int slot, TeleportPose p)
    {
        if (slot < 0 || slot >= Markers.Count) return;
        var row = Markers[slot];
        row.Valid = true;
        row.Summary = string.Format(CultureInfo.InvariantCulture,
            "({0:0.0}, {1:0.0}, {2:0.0})  {3}", p.X, p.Y, p.Z, p.Map);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_autoTimer != null)
        {
            _autoTimer.Stop();
            _autoTimer.Tick -= AutoTick;
            _autoTimer = null;
        }
        _cursorHotkey?.Dispose();
        _cursorHotkey = null;
        foreach (var reg in _markerHotkeys.Values) reg.Dispose();
        _markerHotkeys.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>One marker slot row in the Teleport panel.</summary>
public partial class TeleportMarkerRow : ObservableObject
{
    [ObservableProperty] private int _slot;
    [ObservableProperty] private bool _valid;
    [ObservableProperty] private string _summary = "(empty)";

    /// <summary>1-based label for the UI ("Marker 1").</summary>
    public string Label => $"Marker {Slot + 1}";
}

/// <summary>One user-settable marker-hotkey row (Save/Recall × slot).</summary>
public partial class TeleportHotkeyRow : ObservableObject
{
    /// <summary>Stable id: "save0".."save2" / "recall0".."recall2".</summary>
    public string ActionId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>One-line description shown as the row's tooltip (what the hotkey does).</summary>
    public string Hint { get; init; } = "";

    /// <summary>Current bound combo label (e.g. "Ctrl+F7"), or "" when unset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBinding))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _label = "";

    /// <summary>True while this row is waiting for the user to press a combo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText))]
    private bool _isCapturing;

    /// <summary>True when the saved combo could not be registered at startup —
    /// another app holds it. The label is still shown (so the user knows which
    /// combo to free or rebind), flagged with a warning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private bool _conflicted;

    public bool HasBinding => !string.IsNullOrEmpty(Label);
    public string DisplayLabel => IsCapturing ? "Press keys…"
        : HasBinding ? (Conflicted ? $"{Label} ⚠" : Label)
        : "—";

    /// <summary>"Set" normally, "Cancel" while capturing (the Set button toggles).</summary>
    public string CaptureButtonText => IsCapturing ? "Cancel" : "Set";
}
