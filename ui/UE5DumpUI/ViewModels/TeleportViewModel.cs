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
    private bool _disposed;

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
            HotkeyRows.Add(new TeleportHotkeyRow { ActionId = $"save{i}", DisplayName = $"Save marker {i + 1}" });
        for (int i = 0; i < 3; i++)
            HotkeyRows.Add(new TeleportHotkeyRow { ActionId = $"recall{i}", DisplayName = $"Recall marker {i + 1}" });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "recall_last", DisplayName = "Recall last" });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "bugit",       DisplayName = "Copy BugItGo" });
        HotkeyRows.Add(new TeleportHotkeyRow { ActionId = "bugitgo",     DisplayName = "Run BugItGo" });

        if (_globalHotkeys != null)
        {
            _hotkeyStore = new TeleportHotkeyStore(platform);
            LoadAndRegisterHotkeys();
        }
    }

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
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanRecallLast))]
    private bool _isBusy;

    /// <summary>Buttons are live only when connected and no op is in flight.</summary>
    public bool CanOperate => IsConnected && !IsBusy;

    [ObservableProperty] private string _statusText = "Not connected";

    // ── Current pose ───────────────────────────────────────────────────
    [ObservableProperty] private string _poseX = "—";
    [ObservableProperty] private string _poseY = "—";
    [ObservableProperty] private string _poseZ = "—";
    [ObservableProperty] private string _posePitch = "—";
    [ObservableProperty] private string _poseYaw = "—";
    [ObservableProperty] private string _poseRoll = "—";
    [ObservableProperty] private string _poseMap = "";
    [ObservableProperty] private string _poseSource = "";

    [ObservableProperty] private bool _autoRefresh;

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
        // Quiet poll: does NOT toggle IsBusy, so the buttons (bound to
        // CanOperate) don't flicker disabled/enabled twice a second.
        await RefreshPoseQuietAsync();
        // Re-pull markers every ~2s so changes made via CE Lua / global hotkeys
        // (which the UI didn't initiate) show up.
        if (++_autoTick % 4 == 0)
            await RefreshMarkersAsync();
    }

    private async Task RefreshPoseQuietAsync()
    {
        try
        {
            var p = await _dump.TeleportGetPoseAsync();
            if (p.Code == TeleportCodes.Ok) ApplyPose(p);
        }
        catch (Exception ex)
        {
            _log.Error("Teleport auto-refresh failed", ex);
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
            ApplyPose(p);
            StatusText = $"Pose read ({p.Source}).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport RefreshPose failed", ex);
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
            ApplyPose(p);
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

    // ── Marker global hotkeys (user-set via key capture) ────────────────

    private void LoadAndRegisterHotkeys()
    {
        if (_hotkeyStore == null || _globalHotkeys == null) return;
        var saved = _hotkeyStore.Load();
        foreach (var row in HotkeyRows)
        {
            if (!saved.TryGetValue(row.ActionId, out var b)) continue;
            if (RegisterMarkerHotkey(row.ActionId, b))
            {
                _bindings[row.ActionId] = b;
                row.Label = b.Label;
            }
        }
    }

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
                case "recall_last": _ = RecallLastCommand.ExecuteAsync(null); return;
                case "bugit":       _ = CopyAsBugItGoCommand.ExecuteAsync(null); return;
                case "bugitgo":     _ = RunBugItGoCommand.ExecuteAsync(null); return;
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
        row.IsCapturing = false;
        if (CapturingRow == row) CapturingRow = null;
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
            // 11 momentary auto-unticking records: Save 1-3, Recall 1-3, Recall
            // last, BugIt, BugItGo, Cursor, Clear all.
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
                ("Teleport: Clear all markers", TeleportScriptGenerator.Action.ClearAll, 0),
            };
            foreach (var s in specs)
            {
                string script = TeleportScriptGenerator.Generate(
                    s.Act, s.Slot, ZOffset, TraceChannel, FallbackToCenter);
                if (await _aobMaker!.CreateAAScriptAsync(s.Desc, script, autoActivate: false))
                    ok++;
            }
            StatusText = $"Added {ok}/{specs.Length} Teleport action records to CE " +
                         "(tick a record to fire it once; bind CE hotkeys as you like).";
            _log.Info($"Teleport actions -> CE via AOBMaker ({ok}/{specs.Length})");
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
                ZOffset, TraceChannel, FallbackToCenter);
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

    private void ClearPoseDisplay()
    {
        PoseX = PoseY = PoseZ = "—";
        PosePitch = PoseYaw = PoseRoll = "—";
        PoseMap = PoseSource = "";
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

    public bool HasBinding => !string.IsNullOrEmpty(Label);
    public string DisplayLabel => IsCapturing ? "Press keys…" : (HasBinding ? Label : "—");

    /// <summary>"Set" normally, "Cancel" while capturing (the Set button toggles).</summary>
    public string CaptureButtonText => IsCapturing ? "Cancel" : "Set";
}
