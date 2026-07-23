using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Proxy DLL Deploy tab.
/// Manages Steam game detection and proxy DLL deployment.
/// Not pipe-dependent — works independently of game connection.
/// </summary>
public partial class ProxyDeployViewModel : ViewModelBase
{
    private readonly IProxyDeployService _deploy;
    private readonly ILoggingService _log;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _sourceDllPath = "";
    [ObservableProperty] private string? _sourceDllVersion;
    [ObservableProperty] private bool _forceOverwrite;
    [ObservableProperty] private string? _lastOperationResult;

    /// <summary>
    /// Opt-in (default ON): show a per-game suggested proxy in the grid, derived
    /// from the .exe import table + the proxy the user last deployed for that game.
    /// Advisory only — never changes the selected proxy radio, never auto-deploys.
    /// </summary>
    [ObservableProperty] private bool _lkgSuggestEnabled = true;

    /// <summary>
    /// The proxy the user last DEPLOYED per game (keyed by DetectedGame.Name, the
    /// stable folder name — survives reinstall/patch, unlike peHash). Feeds the
    /// suggestion as a mini "last known good". Round-tripped via ProxyDeployUiOptions
    /// (MainWindowViewModel ApplyOptions/BuildOptions); persisted on change through
    /// <see cref="RequestOptionSave"/> because a Dictionary mutation is not tracked
    /// by the [ObservableProperty] save mechanism.
    /// </summary>
    public Dictionary<string, ProxyType> LastManualProxyByGame { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Games the user has successfully loaded via UI DLL injection, keyed by the
    /// .exe file name (available in the inject flow; stable across reinstall/patch).
    /// Injection is a UI-initiated action, so — unlike a plain-Connect proxy load —
    /// it is reliably known here. When a game is in this set but NOT in
    /// <see cref="LastManualProxyByGame"/>, the suggestion surfaces "injection ·
    /// no proxy deployed": injection is this game's known-good load method.
    /// </summary>
    public HashSet<string> InjectedGameExes { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Proxy CONFIRMED to have actually loaded a game — the DLL self-reported a
    /// proxy <c>load_mode</c> at connect AND the session stayed connected past the
    /// stability dwell (so it didn't load-then-crash). Keyed by .exe file name.
    /// The strongest known-good signal; wins over a merely-deployed pick. Recorded
    /// via <see cref="RecordConfirmedProxy"/> from MainWindowViewModel's gate.
    /// </summary>
    public Dictionary<string, ProxyType> ConfirmedProxyByExe { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by MainWindowViewModel: schedule a debounced options save after
    /// the remembered-pick map / injected set / confirmed map is mutated (none is an
    /// ObservableProperty, so the change-tracking save doesn't fire).</summary>
    public Action? RequestOptionSave { get; set; }

    /// <summary>
    /// Scan source: false = Steam library (default), true = generic drive scan.
    /// Bound to the Source radio pair at the top of the panel.
    /// </summary>
    [ObservableProperty] private bool _scanDrivesMode;

    // Status text colours — the top Status line + the last-result label turn a
    // prominent red when an operation reports failures (e.g. a deploy write blocked
    // by a file lock because the game is still running), green on full success, and
    // neutral gray otherwise. Bound to the TextBlock Foregrounds in the XAML.
    private const string StatusNeutral = "#888888";
    private const string StatusSuccess = "#4EC9B0";
    private const string StatusError   = "#F14C4C";
    [ObservableProperty] private string _statusColor = StatusNeutral;
    [ObservableProperty] private string _lastOperationColor = StatusNeutral;

    /// <summary>Set an operation's result on both the running Status line and the
    /// persistent last-result label, coloured red when any item failed (e.g. a
    /// file-locked write) or green on full success.</summary>
    private void SetOperationResult(string text, int fail)
    {
        LastOperationResult = text;
        StatusText = text;
        string color = fail > 0 ? StatusError : StatusSuccess;
        StatusColor = color;
        LastOperationColor = color;
        _log.Info("ProxyDeploy", text);
    }

    /// <summary>
    /// Which proxy DLL the user wants to deploy. Bound to the RadioButtons
    /// at the top of the panel. Changing this triggers a status refresh so
    /// the DataGrid reflects the deploy state of the newly-selected DLL.
    /// Defaults to version.dll — called at normal runtime (GetFileVersionInfo /
    /// COM / manifest parsing) rather than under the early loader lock, so it is
    /// the safest activation timing for the broadest set of games. (dxgi.dll is
    /// statically imported very early and some games call it before the CRT is
    /// initialised — e.g. Octopath Traveler instant-exits with the dxgi proxy;
    /// see docs/dev-log.md. Pick dxgi only for EXEs importing neither version
    /// nor dinput8.)
    /// </summary>
    [ObservableProperty] private ProxyType _selectedProxyType = ProxyType.Version;

    // Two-way bindings for radio button state — Avalonia RadioButtons bind
    // to bool, so we expose convenience properties that mirror SelectedProxyType.
    public bool IsVersionSelected
    {
        get => SelectedProxyType == ProxyType.Version;
        set { if (value) SelectedProxyType = ProxyType.Version; }
    }
    public bool IsDinput8Selected
    {
        get => SelectedProxyType == ProxyType.Dinput8;
        set { if (value) SelectedProxyType = ProxyType.Dinput8; }
    }
    public bool IsDxgiSelected
    {
        get => SelectedProxyType == ProxyType.Dxgi;
        set { if (value) SelectedProxyType = ProxyType.Dxgi; }
    }

    /// <summary>
    /// Detected games. Non-replaceable: items are added/removed in place so that
    /// per-row PropertyChanged notifications from the DataGrid keep working.
    /// Replacing the collection (the previous approach) caused stale visuals
    /// because new ItemsSource swaps don't re-bind row containers reliably when
    /// the underlying items are the same instances.
    /// </summary>
    public ObservableCollection<DetectedGame> Games { get; } = new();

    /// <summary>
    /// Drives available for the generic (non-Steam) scan. Populated lazily on
    /// first switch to Scan Drives mode (or via Refresh Drives). Each carries an
    /// IsSelected checkbox and its physical-disk number for grouping.
    /// </summary>
    public ObservableCollection<DriveDescriptor> Drives { get; } = new();

    // Two-way mirror properties for the Steam / Scan-Drives radio pair.
    public bool IsSteamSource
    {
        get => !ScanDrivesMode;
        set { if (value) ScanDrivesMode = false; }
    }
    public bool IsDriveSource
    {
        get => ScanDrivesMode;
        set { if (value) ScanDrivesMode = true; }
    }

    /// <summary>Whether any games are selected for batch operations.</summary>
    public bool HasSelection => Games.Any(g => g.IsSelected);

    public ProxyDeployViewModel(IProxyDeployService deploy, ILoggingService log)
    {
        _deploy = deploy;
        _log = log;

        UpdateSourceDllInfo();
    }

    /// <summary>
    /// Locate the source DLL for the currently-selected proxy type.
    /// The proxy/ subdirectory next to the UI executable is kept separate
    /// so Windows DLL search order doesn't load our version.dll into the
    /// UI process itself.
    /// </summary>
    private void UpdateSourceDllInfo()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var dllName = SelectedProxyType.GetDllName();
            var dllPath = Path.Combine(exeDir, "proxy", dllName);
            SourceDllPath = dllPath;
            SourceDllVersion = File.Exists(dllPath)
                ? _deploy.GetDllVersion(dllPath)
                : null;

            StatusText = File.Exists(dllPath)
                ? $"Source: {dllName} v{SourceDllVersion ?? "?"}"
                : $"Source DLL not found: {dllPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Init error: {ex.Message}";
            _log.Error("ProxyDeploy", $"ViewModel init failed: {ex}");
        }
    }

    /// <summary>
    /// Triggered by the source-generated SelectedProxyType setter
    /// (from [ObservableProperty]). Re-resolves the source DLL path for
    /// the new proxy type and refreshes deploy status of detected games.
    /// </summary>
    partial void OnSelectedProxyTypeChanged(ProxyType value)
    {
        UpdateSourceDllInfo();
        // Notify radio button mirror properties so XAML stays in sync.
        OnPropertyChanged(nameof(IsVersionSelected));
        OnPropertyChanged(nameof(IsDinput8Selected));
        OnPropertyChanged(nameof(IsDxgiSelected));

        // If we already have games, re-evaluate their deploy status against
        // the new proxy type. Fire-and-forget — UI doesn't block on toggle.
        if (Games.Count > 0 && File.Exists(SourceDllPath))
        {
            _ = RefreshAfterTypeChangeAsync();
        }
    }

    private async Task RefreshAfterTypeChangeAsync()
    {
        try
        {
            await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType);
        }
        catch (Exception ex)
        {
            _log.Warn("ProxyDeploy", $"Refresh after type change failed: {ex.Message}");
        }
    }

    /// <summary>Toggling the suggestion opt-in re-evaluates the grid (populate or
    /// clear the Suggested column). Fire-and-forget — the UI doesn't block.</summary>
    partial void OnLkgSuggestEnabledChanged(bool value)
    {
        if (Games.Count > 0)
            _ = ApplyProxySuggestionsAsync();
    }

    /// <summary>Compute the per-game proxy suggestion (import table + remembered
    /// pick) for every scanned game. Gated on <see cref="LkgSuggestEnabled"/> —
    /// when off, the service clears the suggestion fields. Advisory only.</summary>
    private async Task ApplyProxySuggestionsAsync(CancellationToken ct = default)
    {
        try
        {
            // Snapshot the known-good maps/set (taken on the calling — UI — thread)
            // so the background enrichment reads immutable copies; this avoids a race
            // with RecordConfirmedProxy / RememberInjection / deploy mutating them.
            var confirmed = new Dictionary<string, ProxyType>(ConfirmedProxyByExe, StringComparer.OrdinalIgnoreCase);
            var remembered = new Dictionary<string, ProxyType>(LastManualProxyByGame, StringComparer.OrdinalIgnoreCase);
            var injected = new HashSet<string>(InjectedGameExes, StringComparer.OrdinalIgnoreCase);

            await _deploy.ApplyProxySuggestionsAsync(
                Games, confirmed, remembered, injected, LkgSuggestEnabled, ct);
        }
        catch (OperationCanceledException) { /* scan cancelled */ }
        catch (Exception ex)
        {
            _log.Warn("ProxyDeploy", $"Proxy suggestion pass failed: {ex.Message}");
        }
    }

    /// <summary>Record that the user successfully loaded a game via UI injection
    /// (keyed by the .exe file name). Persists + re-evaluates the suggestion column
    /// so an injection-only game shows its known-good method. No-op if already known.</summary>
    private void RememberInjection(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        string key = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(key)) return;

        if (InjectedGameExes.Add(key))
        {
            RequestOptionSave?.Invoke();
            if (Games.Count > 0)
                _ = ApplyProxySuggestionsAsync();
        }
    }

    /// <summary>
    /// Record a proxy CONFIRMED to have loaded a game — called from the connection
    /// stability gate (DLL self-reported a proxy load_mode + session stayed alive).
    /// Keyed by the .exe file name (from EngineState.ModuleName). Persists + re-runs
    /// the suggestion so the game upgrades to "confirmed working". Must be called on
    /// the UI thread (mutation + snapshot serialize there). No-op on a non-proxy DLL
    /// name or if the same confirmation is already recorded.
    /// </summary>
    public void RecordConfirmedProxy(string? exeName, string? proxyDllName)
    {
        if (string.IsNullOrEmpty(exeName)) return;
        if (ProxyTypeExtensions.FromDllName(proxyDllName) is not ProxyType type) return;

        if (!ConfirmedProxyByExe.TryGetValue(exeName, out var prev) || prev != type)
        {
            ConfirmedProxyByExe[exeName] = type;
            RequestOptionSave?.Invoke();
            if (Games.Count > 0)
                _ = ApplyProxySuggestionsAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            ClearError();
            IsScanning = true;
            StatusColor = StatusNeutral;
            StatusText = "Detecting Steam libraries...";
            LastOperationResult = null;

            var libraries = await _deploy.GetSteamLibraryFoldersAsync(ct);
            if (libraries.Count == 0)
            {
                StatusText = "No Steam libraries found";
                IsScanning = false;
                return;
            }

            StatusText = $"Scanning {libraries.Count} library folder(s)...";
            var found = await _deploy.FindUeGamesAsync(libraries, ct);

            Games.Clear();
            foreach (var g in found) Games.Add(g);

            if (Games.Count > 0 && File.Exists(SourceDllPath))
            {
                StatusText = "Checking deploy status...";
                await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);
            }

            // Per-game proxy suggestion (import table + remembered pick), once per scan.
            await ApplyProxySuggestionsAsync(ct);

            StatusText = $"Found {Games.Count} UE game(s)";
            OnPropertyChanged(nameof(HasSelection));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Scan failed";
            StatusColor = StatusError;
            SetError(ex);
            _log.Error("ProxyDeploy", $"Scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Triggered by the source-generated ScanDrivesMode setter. Keeps
    /// the radio mirror props in sync and lazily loads drives the first time the
    /// user switches to Scan Drives mode.</summary>
    partial void OnScanDrivesModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSteamSource));
        OnPropertyChanged(nameof(IsDriveSource));
        if (value && Drives.Count == 0 && !IsScanning)
            LoadDrivesCommand.Execute(null);
    }

    [RelayCommand]
    private async Task LoadDrivesAsync(CancellationToken ct)
    {
        try
        {
            var drives = await _deploy.GetScannableDrivesAsync(ct);
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
            StatusText = $"{Drives.Count} drive(s) available — select and scan";
            StatusColor = StatusNeutral;
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch (Exception ex)
        {
            _log.Warn("ProxyDeploy", $"LoadDrives failed: {ex.Message}");
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanDrivesAsync(CancellationToken ct)
    {
        if (IsScanning) { LastOperationResult = "Wait for scan to finish"; return; }

        var selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            LastOperationResult = "No drives selected";
            return;
        }

        try
        {
            ClearError();
            IsScanning = true;
            StatusColor = StatusNeutral;
            StatusText = "Scanning drives for UE games...";
            LastOperationResult = null;

            // Constructed on the UI thread → callback marshals back to the UI thread.
            var progress = new Progress<DriveScanProgress>(p =>
                StatusText = $"{p.CurrentDrive} {p.Phase} — {p.GamesFound} found");

            var found = await _deploy.FindUeGamesOnDrivesAsync(selected, progress, ct);

            Games.Clear();
            foreach (var g in found) Games.Add(g);

            if (Games.Count > 0 && File.Exists(SourceDllPath))
            {
                StatusText = "Checking deploy status...";
                await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);
            }

            // Per-game proxy suggestion (import table + remembered pick), once per scan.
            await ApplyProxySuggestionsAsync(ct);

            StatusText = $"Found {Games.Count} UE game(s)";
            OnPropertyChanged(nameof(HasSelection));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Scan failed";
            StatusColor = StatusError;
            SetError(ex);
            _log.Error("ProxyDeploy", $"Drive scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── Inject into a running game (Proxy Deploy button → process picker) ──

    /// <summary>Set by the panel code-behind: opens the process picker window and
    /// returns the chosen process (or null on cancel). A delegate so the VM never
    /// references a View.</summary>
    public Func<Task<GameProcessInfo?>>? PickProcessAsync { get; set; }

    /// <summary>Set by MainWindowViewModel: connect the pipe after a successful
    /// inject (best-effort auto-connect).</summary>
    public Func<Task>? RequestConnectAsync { get; set; }

    /// <summary>Load injection-candidate processes for the picker. showAll=false
    /// returns only UE games.</summary>
    public async Task<IReadOnlyList<GameProcessInfo>> ListGameProcessesAsync(bool showAll)
    {
        var all = await _deploy.ListGameProcessesAsync();
        return showAll ? all : all.Where(p => p.IsUe).ToList();
    }

    [RelayCommand]
    private async Task InjectIntoRunningGameAsync()
    {
        StatusColor = StatusNeutral;
        LastOperationColor = StatusNeutral;
        ClearError();

        // The injectable DLL sits next to the UI exe (dist\UE5Dumper.dll).
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var dllPath = Path.Combine(exeDir, "UE5Dumper.dll");
        if (!File.Exists(dllPath))
        {
            SetError($"UE5Dumper.dll not found next to the app: {dllPath}");
            return;
        }

        if (PickProcessAsync is null) return;
        GameProcessInfo? target;
        try
        {
            target = await PickProcessAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
            return;
        }
        if (target is null) return;   // cancelled

        // Already loaded (proxy / prior inject / CE .CT): re-injecting would
        // double-load UE5Dumper.dll and fight over the pipe. Skip straight to
        // connecting to the DLL that's already running.
        if (target.DumperLoaded)
        {
            _log.Info("ProxyDeploy",
                $"PID {target.Pid} already has the dumper loaded ({target.DumperLoadMode} "
                + $"{target.DumperVersion}) — connecting instead of re-injecting");
            SetOperationResult(
                $"{target.Name} already has {target.DumperStatusLine} — connecting...", 0);
            // If the module list shows this game was loaded via injection (not a
            // proxy), that's a known-good injection — remember it for the suggestion.
            if (target.DumperLoadMode?.Contains("inject", StringComparison.OrdinalIgnoreCase) == true)
                RememberInjection(target.Path);
            if (RequestConnectAsync is not null)
            {
                try { await RequestConnectAsync(); }
                catch (Exception ex) { _log.Warn("ProxyDeploy", $"Connect to already-loaded PID {target.Pid} failed: {ex.Message}"); }
            }
            return;
        }

        StatusText = $"Injecting UE5Dumper.dll into {target.Name} (PID {target.Pid})...";
        InjectResult result;
        try
        {
            result = await _deploy.InjectDllAsync(target.Pid, dllPath);
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Inject failed";
            StatusColor = StatusError;
            return;
        }

        // Game runs elevated → OpenProcess Access Denied. Auto-retry WITH elevation
        // (a headless UAC-prompt relaunch does just the inject; the UI stays running)
        // unless we're already admin (then elevation can't help).
        if (!result.Ok && result.AccessDenied && !_deploy.IsElevated())
        {
            StatusText = $"Access denied — requesting Administrator for {target.Name}...";
            try
            {
                result = await _deploy.InjectDllElevatedAsync(target.Pid, dllPath);
            }
            catch (Exception ex)
            {
                SetError(ex);
                StatusText = "Inject failed";
                StatusColor = StatusError;
                return;
            }
        }

        if (!result.Ok)
        {
            SetError(result.ErrorMessage ?? "Injection failed");
            StatusText = "Inject failed";
            StatusColor = StatusError;
            _log.Warn("ProxyDeploy", $"Inject into PID {target.Pid} failed: {result.ErrorMessage}");
            return;
        }

        _log.Info("ProxyDeploy", $"Injected UE5Dumper.dll into PID {target.Pid} (HMODULE=0x{result.HModule:X})");
        SetOperationResult($"Injected into {target.Name} (PID {target.Pid}) — connecting...", 0);
        // Injection is a known-good load method for this game — remember it so the
        // Suggested column flags an injection-only game (never had a proxy deployed).
        RememberInjection(target.Path);

        // Auto-connect the pipe (best-effort — the DLL auto-starts its pipe server).
        if (RequestConnectAsync is not null)
        {
            try { await RequestConnectAsync(); }
            catch (Exception ex) { _log.Warn("ProxyDeploy", $"Auto-connect after inject failed: {ex.Message}"); }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (Games.Count == 0) return;

        try
        {
            ClearError();
            StatusColor = StatusNeutral;
            StatusText = "Refreshing status...";
            LastOperationResult = null;

            // Re-read source DLL version (may have changed)
            SourceDllVersion = File.Exists(SourceDllPath)
                ? _deploy.GetDllVersion(SourceDllPath)
                : null;

            await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);
            await ApplyProxySuggestionsAsync(ct);
            StatusText = $"{Games.Count} game(s) — status refreshed";
        }
        catch (Exception ex)
        {
            StatusText = "Refresh failed";
            StatusColor = StatusError;
            SetError(ex);
        }
    }

    [RelayCommand]
    private async Task DeploySelectedAsync(CancellationToken ct)
    {
        StatusColor = StatusNeutral;
        LastOperationColor = StatusNeutral;
        if (IsScanning) { LastOperationResult = "Wait for scan to finish"; return; }

        if (!File.Exists(SourceDllPath))
        {
            SetError($"Source DLL not found: {SourceDllPath}");
            return;
        }

        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            LastOperationResult = "No games selected";
            return;
        }

        ClearError();
        int ok = 0, fail = 0;
        bool pickChanged = false;

        foreach (var game in selected)
        {
            ct.ThrowIfCancellationRequested();
            StatusText = $"Deploying to {game.Name}...";

            bool success = await _deploy.DeployAsync(SourceDllPath, game, SelectedProxyType, ForceOverwrite, ct);
            if (success)
            {
                ok++;
                // Remember what the user deployed for this game (mini "last known
                // good"), keyed by the stable folder name so it survives reinstall.
                if (!string.IsNullOrEmpty(game.Name))
                {
                    if (!LastManualProxyByGame.TryGetValue(game.Name, out var prev) || prev != SelectedProxyType)
                    {
                        LastManualProxyByGame[game.Name] = SelectedProxyType;
                        pickChanged = true;
                    }
                }
            }
            else fail++;
        }

        // Refresh status from disk to ensure DataGrid reflects actual state
        await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);
        // Reflect the just-recorded pick in the Suggested column immediately.
        await ApplyProxySuggestionsAsync(ct);
        if (pickChanged) RequestOptionSave?.Invoke();

        SetOperationResult($"Deployed: {ok} success, {fail} failed", fail);
    }

    [RelayCommand]
    private async Task UndeploySelectedAsync(CancellationToken ct)
    {
        StatusColor = StatusNeutral;
        LastOperationColor = StatusNeutral;
        if (IsScanning) { LastOperationResult = "Wait for scan to finish"; return; }

        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            LastOperationResult = "No games selected";
            return;
        }

        ClearError();
        int ok = 0, fail = 0;

        foreach (var game in selected)
        {
            ct.ThrowIfCancellationRequested();
            StatusText = $"Removing from {game.Name}...";

            // Type-agnostic: removes every proxy flavour of ours in the folder, not
            // just SelectedProxyType (that radio governs deploying).
            bool success = await _deploy.UndeployAsync(game, ct);
            if (success) ok++;
            else fail++;
        }

        // Refresh status from disk to ensure DataGrid reflects actual state
        await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);

        SetOperationResult($"Removed: {ok} success, {fail} failed", fail);
    }

    [RelayCommand]
    private async Task UpdateAllAsync(CancellationToken ct)
    {
        StatusColor = StatusNeutral;
        LastOperationColor = StatusNeutral;
        if (IsScanning)
        {
            LastOperationResult = "Wait for scan to finish";
            return;
        }

        // Resolve the source DLL for EVERY proxy type (all are built side-by-
        // side into <exeDir>/proxy/). Update All updates each game's already-
        // deployed proxy DLL(s) to the latest of the SAME type — independent
        // of the selected radio button. So a new dxgi.dll replaces an old
        // dxgi.dll, a new version.dll replaces an old version.dll, etc. Adding
        // a 4th proxy type needs no change here (iterates the enum).
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var sources = Enum.GetValues<ProxyType>()
            .Select(t => (Type: t, Path: Path.Combine(exeDir, "proxy", t.GetDllName())))
            .Where(s => File.Exists(s.Path))
            .ToList();

        if (sources.Count == 0)
        {
            SetError($"No source proxy DLLs found in {Path.Combine(exeDir, "proxy")}");
            return;
        }

        ClearError();
        int updated = 0, fail = 0, upToDate = 0;

        foreach (var game in Games)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var (type, srcPath) in sources)
            {
                string targetDll = Path.Combine(game.BinariesDir, type.GetDllName());

                // Only update a proxy that is ALREADY deployed (and ours) for
                // this game — never push a fresh type the user didn't choose.
                if (!File.Exists(targetDll) || !_deploy.IsOurProxyDll(targetDll))
                    continue;

                string? srcVer = _deploy.GetDllVersion(srcPath);
                string? tgtVer = _deploy.GetDllVersion(targetDll);
                if (srcVer != null && srcVer == tgtVer)
                {
                    upToDate++;
                    continue;
                }

                StatusText = $"Updating {game.Name} ({type.GetDisplayName()})...";
                bool success = await _deploy.DeployAsync(srcPath, game, type, force: true, ct: ct);
                if (success) updated++;
                else fail++;
            }
        }

        // Refresh status from disk for the currently-selected type's view.
        await _deploy.RefreshDeployStatusAsync(Games, SourceDllPath, SelectedProxyType, ct);

        if (updated == 0 && fail == 0)
        {
            string msg = upToDate > 0
                ? $"All {upToDate} deployed proxy DLL(s) already up-to-date"
                : "No deployed proxy DLLs to update";
            LastOperationResult = msg;
            StatusText = msg;
            StatusColor = StatusNeutral;
            LastOperationColor = StatusNeutral;
            _log.Info("ProxyDeploy", msg);
        }
        else
        {
            SetOperationResult($"Updated: {updated}, up-to-date: {upToDate}, failed: {fail}", fail);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Selection helpers
    // ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var g in Games) g.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void UnselectAll()
    {
        foreach (var g in Games) g.IsSelected = false;
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var g in Games) g.IsSelected = !g.IsSelected;
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Notify that selection changed (called from View).
    /// </summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
    }

}
