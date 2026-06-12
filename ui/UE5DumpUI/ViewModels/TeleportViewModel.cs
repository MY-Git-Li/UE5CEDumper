using System.Collections.ObjectModel;
using System.Globalization;
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
    private IGlobalHotkeyRegistration? _cursorHotkey;
    private Avalonia.Threading.DispatcherTimer? _autoTimer;
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
    }

    /// <summary>Whether a global cursor-teleport hotkey can be offered
    /// (a hotkey service was supplied — false in headless tests).</summary>
    public bool CanBindCursorHotkey => _globalHotkeys != null;

    // ── Connection gating ──────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
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

    // ── CE export ──────────────────────────────────────────────────────
    /// <summary>0 = Numpad, 1 = Top-row, 2 = Both (matches HotkeySchemeOptions).</summary>
    [ObservableProperty] private int _hotkeySchemeIndex;

    public ObservableCollection<string> HotkeySchemeOptions { get; } =
        new() { "Numpad (default)", "Top-row", "Both" };

    public ObservableCollection<TeleportMarkerRow> Markers { get; } = new();

    private TeleportHotkeyScheme Scheme => HotkeySchemeIndex switch
    {
        1 => TeleportHotkeyScheme.TopRow,
        2 => TeleportHotkeyScheme.Both,
        _ => TeleportHotkeyScheme.Numpad,
    };

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
                _ = TeleportToCursorCommand.ExecuteAsync(null);
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
            _autoTimer.Start();
        }
        else
        {
            _autoTimer?.Stop();
        }
    }

    private async void AutoTick(object? sender, EventArgs e)
    {
        if (!CanOperate) return;
        await RefreshPoseAsync();
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
            await _platform.CopyToClipboardAsync(s);
            StatusText = $"Copied: {s}";
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

    [RelayCommand]
    private async Task AddHotkeysToCeAsync()
    {
        try
        {
            ClearError();
            // Primary path: ship a tickable AA-script record into the CE table
            // via AOBMaker (same mechanism as the Debug Camera "Copy CE Script").
            // Ticking it registers the hotkeys; unticking removes them.
            bool available = _aobMaker != null && await _aobMaker.CheckAvailabilityAsync();
            if (available)
            {
                string record = TeleportLuaBundleGenerator.GenerateAaRecord(
                    Scheme, ZOffset, TraceChannel, FallbackToCenter);
                bool ok = await _aobMaker!.CreateAAScriptAsync(
                    $"Teleport hotkeys ({Scheme}) — UE5CEDumper", record, autoActivate: true);
                StatusText = ok
                    ? $"Teleport hotkeys record added to CE and enabled ({Scheme})."
                    : "AOBMaker rejected the hotkeys record — see logs.";
                _log.Info($"Teleport hotkeys -> CE via AOBMaker (scheme={Scheme}, ok={ok})");
                if (ok) return;
            }
            // Fallback: clipboard (paste into a CE Table Lua Script).
            string lua = TeleportLuaBundleGenerator.Generate(
                Scheme, ZOffset, TraceChannel, FallbackToCenter);
            await _platform.CopyToClipboardAsync(lua);
            StatusText = available
                ? "AOBMaker injection failed — Lua bundle copied to clipboard (paste into a CE Table Lua Script)."
                : "AOBMaker not connected — Lua bundle copied to clipboard (paste into a CE Table Lua Script).";
            _log.Info($"Teleport Lua bundle copied to clipboard (scheme={Scheme})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Teleport AddHotkeysToCe failed", ex);
        }
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
            // 7 momentary auto-unticking records: Save 1-3, Recall 1-3, Cursor.
            var specs = new (string Desc, TeleportScriptGenerator.Action Act, int Slot)[]
            {
                ("Teleport: Save marker 1",   TeleportScriptGenerator.Action.Save,   0),
                ("Teleport: Save marker 2",   TeleportScriptGenerator.Action.Save,   1),
                ("Teleport: Save marker 3",   TeleportScriptGenerator.Action.Save,   2),
                ("Teleport: Recall marker 1", TeleportScriptGenerator.Action.Recall, 0),
                ("Teleport: Recall marker 2", TeleportScriptGenerator.Action.Recall, 1),
                ("Teleport: Recall marker 3", TeleportScriptGenerator.Action.Recall, 2),
                ("Teleport: To cursor",       TeleportScriptGenerator.Action.Cursor, 0),
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
