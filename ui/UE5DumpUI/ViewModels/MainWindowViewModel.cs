using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// Main window ViewModel — orchestrates connection and child ViewModels.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private bool _disposed;
    private readonly IPipeClient _pipeClient;
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IPlatformService _platform;
    private readonly AobUsageService? _aobUsage;
    private readonly IAobMakerBridge? _aobMaker;  // captured so InterestingFunctions handlers can ship AA Scripts
    private EngineState? _engineState;

    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private string _windowTitle = "UE5 Dump UI";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _needsScan;       // True when connected but scan not yet done (proxy DLL mode)
    [ObservableProperty] private bool _isScanning;      // True while trigger_scan is in progress
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _selectedAddressFormatIndex;
    [ObservableProperty] private bool _collapsePointerNodes;
    [ObservableProperty] private int _arrayLimitExponent = 6; // 2^6 = 64
    [ObservableProperty] private int _dropDownLimitExponent = 9; // 2^9 = 512
    [ObservableProperty] private int _csxDrilldownDepth; // 0 = flat (dummy), 1+ = real child structures
    [ObservableProperty] private int _previewLimit = 2; // Struct preview sub-field count (0-6)

    /// <summary>Computed array element limit: 2^ArrayLimitExponent (2..16384).</summary>
    public int ArrayLimit => 1 << ArrayLimitExponent;

    /// <summary>Computed CE DropDownList max entries: 2^DropDownLimitExponent (64..8192).</summary>
    public int DropDownLimit => 1 << DropDownLimitExponent;

    /// <summary>Show warning when array limit &gt;= 256 (high memory usage).</summary>
    public bool ShowArrayLimitWarning => ArrayLimitExponent >= 8;

    /// <summary>Address format options for toolbar ComboBox.</summary>
    public string[] AddressFormatOptions { get; } =
    [
        "Hex (no prefix)",
        "Hex (0x prefix)",
        "Module+Offset",
    ];

    /// <summary>
    /// Application version string read from assembly metadata (e.g. "v1.0.0.37").
    /// </summary>
    public string AppVersion { get; } = GetAppVersion();

    private static string GetAppVersion()
    {
        var ver = Assembly.GetEntryAssembly()?.GetName().Version;
        return ver != null ? $"v{ver}" : "";
    }

    // Child ViewModels
    public ObjectTreeViewModel ObjectTree { get; }
    public ClassStructViewModel ClassStruct { get; }
    public PointerPanelViewModel Pointers { get; }
    public LiveWalkerViewModel LiveWalker { get; }
    public InstanceFinderViewModel InstanceFinder { get; }
    public PropertySearchViewModel PropertySearch { get; }
    public GameClassFilterViewModel GameClassFilter { get; }
    public InterestingFunctionsViewModel InterestingFunctions { get; }
    public ProxyDeployViewModel? ProxyDeploy { get; }

    partial void OnSelectedAddressFormatIndexChanged(int value)
    {
        ObjectTree.SelectedAddressFormatIndex = value;
        LiveWalker.SelectedAddressFormatIndex = value;
        InstanceFinder.SelectedAddressFormatIndex = value;
    }

    partial void OnCollapsePointerNodesChanged(bool value)
    {
        LiveWalker.CollapsePointerNodes = value;
        InstanceFinder.CollapsePointerNodes = value;
    }

    partial void OnArrayLimitExponentChanged(int value)
    {
        OnPropertyChanged(nameof(ArrayLimit));
        OnPropertyChanged(nameof(ShowArrayLimitWarning));
        LiveWalker.ArrayLimit = ArrayLimit;
        InstanceFinder.ArrayLimit = ArrayLimit;
    }

    partial void OnDropDownLimitExponentChanged(int value)
    {
        OnPropertyChanged(nameof(DropDownLimit));
        LiveWalker.DropDownLimit = DropDownLimit;
        InstanceFinder.DropDownLimit = DropDownLimit;
    }

    partial void OnCsxDrilldownDepthChanged(int value)
    {
        LiveWalker.CsxDrilldownDepth = value;
        OnPropertyChanged(nameof(CsxDrilldownDepthBrush));
    }

    /// <summary>Toolbar slider colour — default 0-4, amber 5, red 6 to flag exponential output growth.</summary>
    public Avalonia.Media.IBrush CsxDrilldownDepthBrush => CsxDrilldownDepth switch
    {
        >= 6 => Avalonia.Media.SolidColorBrush.Parse("#E05252"),
        5    => Avalonia.Media.SolidColorBrush.Parse("#E6A817"),
        _    => Avalonia.Media.SolidColorBrush.Parse("#D4D4D4"),
    };

    partial void OnPreviewLimitChanged(int value)
    {
        LiveWalker.PreviewLimit = value;
        InstanceFinder.PreviewLimit = value;
    }

    /// <summary>
    /// Re-check AOBMaker CE Plugin availability on tab switch.
    /// The user may open CE after connecting, so periodic re-check ensures
    /// AOBMaker-dependent buttons become enabled when the plugin appears.
    /// </summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (!IsConnected) return;

        switch (value)
        {
            case 0: // Live Walker
                _ = LiveWalker.CheckAobMakerAsync();
                break;
            case 5: // Pointers
                _ = Pointers.CheckAobMakerAsync();
                break;
        }
    }

    public MainWindowViewModel(
        IPipeClient pipeClient,
        IDumpService dump,
        ILoggingService log,
        IPlatformService platform,
        AobUsageService? aobUsage = null,
        IAobMakerBridge? aobMaker = null,
        IProxyDeployService? proxyDeploy = null)
    {
        _pipeClient = pipeClient;
        _dump = dump;
        _log = log;
        _platform = platform;
        _aobUsage = aobUsage;
        _aobMaker = aobMaker;

        ObjectTree = new ObjectTreeViewModel(dump, log, platform);
        ClassStruct = new ClassStructViewModel(dump, log);
        Pointers = new PointerPanelViewModel(platform, dump, log, aobMaker, aobUsage);
        LiveWalker = new LiveWalkerViewModel(dump, log, platform, aobMaker);
        InstanceFinder = new InstanceFinderViewModel(dump, log, platform);
        PropertySearch = new PropertySearchViewModel(dump, log);
        GameClassFilter = new GameClassFilterViewModel(dump, log);
        InterestingFunctions = new InterestingFunctionsViewModel(dump, log, aobMaker);

        if (proxyDeploy != null)
            ProxyDeploy = new ProxyDeployViewModel(proxyDeploy, log);

        // Wire Pointers Extra Scan -> refresh all panels after rescan results applied
        Pointers.RescanApplied += async () =>
        {
            try
            {
                var state = await _dump.GetPointersAsync();
                _engineState = state;

                Pointers.Update(state);

                ObjectTree.SetEngineState(state);
                LiveWalker.SetEngineState(state);
                InstanceFinder.SetEngineState(state);

                _ = LiveWalker.CheckAobMakerAsync();

                StatusText = $"Connected — UE{state.UEVersion} ({state.ObjectCount} objects)";

                // Re-load objects if tree was empty
                if (ObjectTree.ObjectCount == 0 && state.ObjectCount > 0)
                    _ = ObjectTree.LoadCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                _log.Error("RescanApplied refresh error", ex);
            }
        };

        // Wire cross-VM communication
        // Wrap async lambdas in try/catch to prevent async void from crashing the app
        ObjectTree.SelectionChanged += async (node) =>
        {
            try
            {
                await ClassStruct.OnObjectSelected(node);
            }
            catch (Exception ex)
            {
                _log.Error("SelectionChanged handler error", ex);
            }
        };

        // Wire InstanceFinder -> LiveWalker navigation + tab switch
        InstanceFinder.NavigateToLiveWalker += async (addr) =>
        {
            try
            {
                SelectedTabIndex = 0; // Switch to Live Walker tab
                await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error("NavigateToLiveWalker handler error", ex);
            }
        };

        // Wire PropertySearch -> InstanceFinder (pre-fill class name +
        // switch tab + auto-run the search). Pre-fill alone left the user
        // having to click Search again, which they correctly flagged as
        // friction — the whole point of "Find Instances" is to see live
        // instances of that class, so trigger the query immediately.
        PropertySearch.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = 1; // Switch to Instance Finder tab
                InstanceFinder.SearchClassName = className;
                if (InstanceFinder.SearchCommand.CanExecute(null))
                    await InstanceFinder.SearchCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                _log.Error("NavigateToInstanceFinder handler error", ex);
            }
        };

        // Wire PropertySearch -> LiveWalker navigation + tab switch
        PropertySearch.NavigateToLiveWalker += async (addr) =>
        {
            try
            {
                SelectedTabIndex = 0; // Switch to Live Walker tab
                await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error("PropertySearch NavigateToLiveWalker handler error", ex);
            }
        };

        // Wire GameClassFilter -> InstanceFinder (pre-fill class name +
        // switch tab + auto-run the search). Same rationale as the
        // PropertySearch wiring above — clicking "Find Instances" should
        // produce instances on screen without an extra Search click.
        GameClassFilter.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = 1; // Switch to Instance Finder tab
                InstanceFinder.SearchClassName = className;
                if (InstanceFinder.SearchCommand.CanExecute(null))
                    await InstanceFinder.SearchCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                _log.Error("GameClassFilter NavigateToInstanceFinder handler error", ex);
            }
        };

        // Wire GameClassFilter -> LiveWalker navigation + tab switch
        GameClassFilter.NavigateToLiveWalker += async (addr) =>
        {
            try
            {
                SelectedTabIndex = 0; // Switch to Live Walker tab
                await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error("GameClassFilter NavigateToLiveWalker handler error", ex);
            }
        };

        // Wire GameClassFilter -> ClassStruct (walk class schema + switch tab)
        GameClassFilter.NavigateToClassStruct += async (classAddr) =>
        {
            try
            {
                SelectedTabIndex = 5; // Switch to ClassStruct tab (was 4 pre-InterestingFunctions tab insertion)
                await ClassStruct.LoadClassCommand.ExecuteAsync(classAddr);
            }
            catch (Exception ex)
            {
                _log.Error("GameClassFilter NavigateToClassStruct handler error", ex);
            }
        };

        // Wire InterestingFunctions -> Live Walker (with find_instance fallback to ClassStruct).
        // The Finder gives us (className, funcName), but Live Walker is instance-based:
        //   1. Try FindInstancesAsync(className, exactMatch=true) -- pick the first non-CDO live instance.
        //   2. On hit: switch to Live Walker tab + navigate to that instance + auto-scroll to funcName.
        //   3. On miss (CDO-only class, or class not yet instantiated): switch to ClassStruct tab so
        //      the user at least sees the function in the class metadata, with a status hint.
        InterestingFunctions.NavigateToFunction += async (className, funcName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;

                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                // Skip CDO entries (their name typically starts with "Default__"); pick the first
                // real instance so Live Walker has something to walk.
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }

                if (!string.IsNullOrEmpty(liveAddr))
                {
                    SelectedTabIndex = 0; // Live Walker
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(liveAddr);
                    // Function Goto: TrySelectFunctionByNameAsync awaits any
                    // in-flight LoadFunctionsAsync (NavigateToAddress fires
                    // it forget-style so fields render fast). Without that
                    // await the first click after a class change finds an
                    // empty function list and reports "function not selected".
                    var picked = await LiveWalker.TrySelectFunctionByNameAsync(funcName);
                    StatusText = picked
                        ? $"Navigated to {className}::{funcName} (live instance {liveAddr})"
                        : $"Navigated to {className} @ {liveAddr}; function '{funcName}' not in this class";
                    _log.Info($"InterestingFunctions -> LiveWalker: {className}::{funcName} @ {liveAddr}" +
                              (picked ? "" : " (function not selected)"));
                }
                else
                {
                    SelectedTabIndex = 5; // ClassStruct fallback
                    // Look up the class address via ListClasses since Find Instances came back empty.
                    var classes = await _dump.ListClassesAsync(gameOnly: false);
                    var match = classes.Classes.FirstOrDefault(
                        c => c.ClassName.Equals(className, StringComparison.Ordinal));
                    if (match != null && !string.IsNullOrEmpty(match.ClassAddr))
                    {
                        await ClassStruct.LoadClassCommand.ExecuteAsync(match.ClassAddr);
                        StatusText = $"No live instance of {className}; showing class metadata";
                        _log.Info($"InterestingFunctions -> ClassStruct fallback: {className}::{funcName}");
                    }
                    else
                    {
                        StatusText = $"Class {className} not resolvable (Find Instances + ListClasses both empty)";
                        _log.Warn($"InterestingFunctions navigate: {className} not found");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions NavigateToFunction handler error: {className}::{funcName}", ex);
            }
        };

        // Wire InterestingFunctions -> clipboard. The VM avoids holding
        // IPlatformService directly so its test stubs stay minimal; the
        // MainWindow knows the platform service and can do the actual
        // copy here. Status text already set by the VM.
        InterestingFunctions.RequestCopyText += async (text) =>
        {
            if (string.IsNullOrEmpty(text)) return;
            try { await _platform.CopyToClipboardAsync(text); }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions clipboard copy failed: {ex.Message}", ex);
            }
        };

        // Wire InterestingFunctions -> Copy AA Script (Baked).
        // Walks the class to fetch the chosen UFunction's full param metadata, then either
        // generates a no-arg script directly (fast path) or opens InvokeParamDialog in
        // CopyBakedScript mode for the user to fill values.
        InterestingFunctions.RequestCopyBakedScript += async (className, funcName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(funcName)) return;

                // Need a class address to walk_functions. Reuse ListClasses (cached game class list).
                var classes = await _dump.ListClassesAsync(gameOnly: false);
                var classMatch = classes.Classes.FirstOrDefault(
                    c => c.ClassName.Equals(className, StringComparison.Ordinal));
                if (classMatch == null || string.IsNullOrEmpty(classMatch.ClassAddr))
                {
                    StatusText = $"Class {className} not found";
                    return;
                }

                var functions = await _dump.WalkFunctionsAsync(classMatch.ClassAddr);
                var funcMatch = functions.FirstOrDefault(
                    f => f.Name.Equals(funcName, StringComparison.Ordinal));
                if (funcMatch == null)
                {
                    StatusText = $"{className}::{funcName} not in walk_functions output";
                    return;
                }

                // Find a live instance so the script's invokeUFunction has a target. The
                // helper uses CMD_INVOKE_BY_NAME which finds an instance itself, but
                // running it now lets us surface a clear error if the class is CDO-only.
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 1);
                string instanceAddr = "";
                foreach (var inst in instances.Instances)
                {
                    if (!inst.Name.StartsWith("Default__", StringComparison.Ordinal))
                    {
                        instanceAddr = inst.Address;
                        break;
                    }
                }

                // Fast path: zero-arg function -> generate + ship directly without opening
                // the dialog (matches the LiveWalker AA(Baked) button's 0-arg fast path).
                var inputParams = funcMatch.Params.Where(p => !p.IsReturn).ToList();
                if (inputParams.Count == 0)
                {
                    var script = Services.BakedScriptGenerator.Generate(
                        className, funcName, funcMatch.ParmsSize,
                        Array.Empty<Models.BakedParamValue>());
                    var description = $"Invoke (baked, no args): {className}::{funcName}";
                    // Sample availability before send so 'pipe broke mid-send'
                    // surfaces distinctly from 'CE not running'.
                    bool wasAvailable = _aobMaker?.IsAvailable ?? false;
                    bool sentToCe = false;
                    if (_aobMaker != null && wasAvailable)
                        sentToCe = await _aobMaker.CreateAAScriptAsync(description, script, autoActivate: false);
                    if (!sentToCe)
                        await _platform.CopyToClipboardAsync(script);
                    // Sync VM-level state so InterestingFunctions tab's Notes
                    // column reflects post-send reality.
                    if (_aobMaker != null)
                        InterestingFunctions.IsAobMakerAvailable = _aobMaker.IsAvailable;
                    StatusText = sentToCe
                        ? $"AA Script created in CE: {funcName}"
                        : wasAvailable
                            ? $"⚠ AOBMaker pipe broke (CE closed?) — script copied to clipboard"
                            : $"AOBMaker not connected — script copied to clipboard ({funcName})";
                    _log.Info($"InterestingFunctions baked AA Script (no args) " +
                              $"{(sentToCe ? "sent to CE" : "to clipboard")}: " +
                              $"{className}::{funcName} (wasAvailable={wasAvailable})");
                    return;
                }

                // Otherwise open the dialog in CopyBakedScript mode.
                if (Avalonia.Application.Current?.ApplicationLifetime is not
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not { } owner)
                    return;

                var dialog = new Views.InvokeParamDialog(
                    className, funcName, inputParams, funcMatch.Params, funcMatch.ParmsSize,
                    instanceAddr, _dump, _engineState?.UEVersion ?? 0,
                    aobMaker: _aobMaker, platform: _platform,
                    mode: Views.InvokeDialogMode.CopyBakedScript);
                var result = await dialog.ShowDialog<string?>(owner);
                StatusText = result == "ok"
                    ? $"AA Script ready: {className}::{funcName}"
                    : $"AA Script export cancelled: {funcName}";
                _log.Info($"InterestingFunctions CopyBakedScript dialog " +
                          $"{(result == "ok" ? "completed" : "cancelled")}: {className}::{funcName}");
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions RequestCopyBakedScript handler error: {className}::{funcName}", ex);
                StatusText = $"AA Script export failed: {ex.Message}";
            }
        };

        _pipeClient.ConnectionStateChanged += (connected) =>
        {
            if (!connected) _log.StopProcessMirror();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = connected;
                StatusText = connected ? "Connected" : "Disconnected";
                if (!connected) WindowTitle = "UE5 Dump UI";
            });
        };
    }

    /// <summary>
    /// Audit fixes #16/#17: dispose owned child VMs that hold timers /
    /// CancellationTokenSources. Called from MainWindow.Closed so timer
    /// callbacks don't fire after the window is gone.
    /// Other child VMs (PointerPanel, ClassStruct, InstanceFinder, etc.)
    /// don't currently own disposable resources; they're skipped here.
    /// If they grow IDisposable in the future, add them to this list.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ObjectTree.Dispose();
        LiveWalker.Dispose();

        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            ClearError();
            StatusText = "Connecting...";
            LiveWalker.ClearAllBookmarks();

            await _pipeClient.ConnectAsync();

            var state = await _dump.InitAsync();
            _engineState = state;
            IsConnected = true;

            // Detect proxy DLL mode: connected but scan not yet done
            // (UE version 0 and all pointers at 0x0 / empty)
            bool notScanned = state.UEVersion == 0
                && state.ObjectCount == 0
                && (string.IsNullOrEmpty(state.GObjectsAddr) || state.GObjectsAddr == "0x0");

            if (notScanned)
            {
                NeedsScan = true;
                StatusText = "Connected — waiting for scan (load a save first, then click Start Scan)";

                if (!string.IsNullOrEmpty(state.ModuleName))
                {
                    WindowTitle = $"UE5 Dump UI — {state.ModuleName}";
                    _log.StartProcessMirror(state.ModuleName);
                }

                _log.Info(Constants.LogCatInit, "Connected (proxy mode — scan not yet triggered)");
            }
            else
            {
                NeedsScan = false;
                ApplyEngineState(state);
            }
        }
        catch (Exception ex)
        {
            StatusText = "Connection Error";
            SetError(ex);
            _log.Error(Constants.LogCatInit, "Connection failed", ex);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            ClearError();
            ObjectTree.CancelLoadCommand.Execute(null);
            _log.StopProcessMirror();
            await _pipeClient.DisconnectAsync();
            StatusText = "Disconnected";
            WindowTitle = "UE5 Dump UI";
            IsConnected = false;
            NeedsScan = false;
            IsScanning = false;
        }
        catch (OperationCanceledException)
        {
            // Expected during disconnect
            StatusText = "Disconnected";
            IsConnected = false;
        }
        catch (Exception ex)
        {
            // Suppress pipe-related errors during disconnect
            if (ex is IOException or ObjectDisposedException)
            {
                StatusText = "Disconnected";
                IsConnected = false;
            }
            else
            {
                SetError(ex);
            }
        }
        finally
        {
            LiveWalker.ClearAllBookmarks();
        }
    }

    /// <summary>
    /// Apply a fully-scanned engine state to all child ViewModels.
    /// Shared between ConnectAsync (normal mode) and TriggerScanAsync (proxy mode).
    /// </summary>
    private void ApplyEngineState(EngineState state)
    {
        Pointers.Update(state);

        ObjectTree.SetEngineState(state);
        LiveWalker.SetEngineState(state);
        InstanceFinder.SetEngineState(state);

        // Fire-and-forget: check AOBMaker availability for Live Walker
        _ = LiveWalker.CheckAobMakerAsync();

        // Fire-and-forget: persist AOB usage data (failure must not block UI)
        if (_aobUsage != null)
            _ = _aobUsage.RecordScanAsync(state);

        StatusText = $"Connected — UE{state.UEVersion} ({state.ObjectCount} objects)";

        if (!string.IsNullOrEmpty(state.ModuleName))
        {
            WindowTitle = $"UE5 Dump UI — {state.ModuleName}";
            _log.StartProcessMirror(state.ModuleName);
        }

        _log.Info(Constants.LogCatInit, $"Connected: UE{state.UEVersion}, {state.ObjectCount} objects, module={state.ModuleName}");

        // Auto-load objects
        _ = ObjectTree.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Trigger AOB scan from the UI. Used in proxy DLL mode where the DLL starts
    /// the pipe server without scanning. The user clicks "Start Scan" after the
    /// game has loaded a save / reached the main world.
    /// </summary>
    [RelayCommand]
    private async Task TriggerScanAsync()
    {
        try
        {
            ClearError();
            IsScanning = true;
            StatusText = "Starting scan...";

            // trigger_scan now returns immediately — scan runs in background
            await _dump.TriggerScanAsync();

            // Poll scan_status every 500ms until complete
            while (true)
            {
                await Task.Delay(500);

                var status = await _dump.GetScanStatusAsync();
                StatusText = $"Scanning... {status.StatusText}";

                if (!status.Running && status.Phase >= 7 && status.EngineState != null)
                {
                    _engineState = status.EngineState;
                    NeedsScan = false;
                    IsScanning = false;
                    ApplyEngineState(status.EngineState);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            IsScanning = false;
            StatusText = "Scan failed";
            SetError(ex);
            _log.Error(Constants.LogCatInit, "TriggerScan failed", ex);
        }
    }

    // --- Export Commands ---

    [RelayCommand]
    private async Task ExportSymbolsX64dbgAsync()
    {
        await ExportSymbolsAsync("x64dbg Database (*.dd64)", ".dd64",
            (symbols, moduleName) => SymbolExportService.GenerateX64dbgDatabase(symbols, moduleName));
    }

    [RelayCommand]
    private async Task ExportSymbolsGhidraAsync()
    {
        await ExportSymbolsAsync("Ghidra Symbols (*.txt)", ".txt",
            (symbols, _) => SymbolExportService.GenerateGhidraSymbols(symbols));
    }

    [RelayCommand]
    private async Task ExportSymbolsIdaAsync()
    {
        await ExportSymbolsAsync("IDA Script (*.idc)", ".idc",
            (symbols, _) => SymbolExportService.GenerateIdaScript(symbols));
    }

    /// <summary>
    /// Tools menu: stream the embedded <c>ue5_invoke_helper.lua</c> to a
    /// user-chosen file. The helper is required at runtime by every
    /// "Copy AA Script (Baked)" output -- once per .CT the user picks
    /// Tools -> Export CE Helper Lua File... here, then drags the file
    /// into their table via Cheat Engine's Table -> Add File...
    /// menu. Doesn't need an active DLL connection.
    /// </summary>
    [RelayCommand]
    private async Task ExportCeHelperLuaAsync()
    {
        try
        {
            var savePath = await _platform.ShowSaveFileDialogAsync(
                defaultFileName:  HelperLuaResource.DefaultFileName,
                filterName:       "CE Lua Helper (*.lua)",
                filterExtension:  ".lua");
            if (string.IsNullOrEmpty(savePath))
            {
                _log.Info("Export CE Helper Lua: user cancelled");
                return;
            }

            var content = HelperLuaResource.Read();
            await File.WriteAllTextAsync(savePath, content);

            _log.Info($"Exported CE helper lua: {savePath} " +
                      $"({content.Length:N0} chars)");
            StatusText = $"CE helper exported: {Path.GetFileName(savePath)}";
        }
        catch (Exception ex)
        {
            _log.Error("Export CE Helper Lua failed", ex);
            StatusText = $"Export CE helper failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Tools menu: ship the embedded <c>ue5_invoke_helper.lua</c> straight
    /// into the currently open CE table via the AOBMaker plugin pipe
    /// (<c>InjectTableFile</c>). Replaces the manual save-to-disk +
    /// <c>Table -&gt; Add File...</c> dance.
    /// Probes <see cref="IAobMakerBridge.IsAvailable"/> first via
    /// <see cref="IAobMakerBridge.CheckAvailabilityAsync"/> so a stale
    /// availability flag (CE closed since the last check) doesn't fire
    /// off a guaranteed-to-fail pipe round-trip.
    /// </summary>
    [RelayCommand]
    private async Task InjectCeHelperLuaAsync()
    {
        if (_aobMaker == null)
        {
            StatusText = "AOBMaker plugin not configured";
            return;
        }

        // Show an in-flight status so successive clicks can be told apart
        // even when both end in the same outcome — without this the user
        // sees the previous run's text frozen on screen until the new
        // run finishes, which reads as "the click did nothing".
        StatusText = $"Injecting {HelperLuaResource.DefaultFileName} into CE table...";

        try
        {
            await _aobMaker.CheckAvailabilityAsync();
            if (!_aobMaker.IsAvailable)
            {
                StatusText = "Inject helper: AOBMaker not connected — open Cheat Engine with the AOBMaker plugin loaded";
                return;
            }

            var content = HelperLuaResource.Read();
            var (ok, error) = await _aobMaker.InjectTableFileAsync(
                HelperLuaResource.DefaultFileName, content);

            if (ok)
            {
                _log.Info($"Injected {HelperLuaResource.DefaultFileName} into CE table " +
                          $"({content.Length:N0} chars)");
                StatusText = $"Inject helper OK: {HelperLuaResource.DefaultFileName} embedded ({content.Length:N0} bytes)";
            }
            else if (!string.IsNullOrEmpty(error))
            {
                StatusText = $"Inject helper failed: {error} — use Export to disk + Add File... fallback";
            }
            else
            {
                StatusText = "Inject helper failed (no plugin response — CE closed?) — use Export to disk + Add File... fallback";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Inject CE Helper Lua failed", ex);
            StatusText = $"Inject helper failed: {ex.Message}";
        }
    }

    private async Task ExportSymbolsAsync(
        string filterName, string filterExtension,
        Func<IReadOnlyList<SymbolEntry>, string, string> generator)
    {
        if (_engineState == null) return;

        try
        {
            ClearError();
            var moduleName = _engineState.ModuleName;
            if (string.IsNullOrEmpty(moduleName)) moduleName = "game.exe";
            var safeModule = Path.GetFileNameWithoutExtension(moduleName);

            var filePath = await _platform.ShowSaveFileDialogAsync(
                $"{safeModule}_symbols", filterName, filterExtension);
            if (string.IsNullOrEmpty(filePath)) return;

            StatusText = "Collecting symbols...";

            var progress = new Progress<string>(msg =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = msg));

            var symbols = await SymbolExportService.CollectSymbolsAsync(
                _dump, moduleName, _engineState.ModuleBase, progress);

            StatusText = "Writing file...";
            var content = generator(symbols, moduleName);
            await File.WriteAllTextAsync(filePath, content);

            StatusText = $"Exported {symbols.Count} symbols";
            _log.Info($"Symbols exported to {filePath} ({symbols.Count} entries)");
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            SetError(ex);
            _log.Error("Symbol export failed", ex);
        }
    }

    [RelayCommand]
    private async Task ExportFullSdkAsync()
    {
        if (_engineState == null) return;

        try
        {
            ClearError();
            var moduleName = _engineState.ModuleName;
            if (string.IsNullOrEmpty(moduleName)) moduleName = "game";
            var safeModule = Path.GetFileNameWithoutExtension(moduleName);

            var filePath = await _platform.ShowSaveFileDialogAsync(
                $"{safeModule}_SDK", "C++ Header (*.h)", ".h");
            if (string.IsNullOrEmpty(filePath)) return;

            StatusText = "Generating SDK...";
            var progress = new Progress<string>(msg =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = msg));

            var content = await SdkExportService.GenerateFullSdkAsync(_dump, progress);
            await File.WriteAllTextAsync(filePath, content);

            StatusText = "SDK exported";
            _log.Info($"Full SDK exported to {filePath}");
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            SetError(ex);
            _log.Error("Full SDK export failed", ex);
        }
    }

    [RelayCommand]
    private async Task ExportUsmapAsync()
    {
        if (_engineState == null) return;

        try
        {
            ClearError();
            var moduleName = _engineState.ModuleName;
            if (string.IsNullOrEmpty(moduleName)) moduleName = "game";
            var safeModule = Path.GetFileNameWithoutExtension(moduleName);

            var filePath = await _platform.ShowSaveFileDialogAsync(
                $"{safeModule}", "USMAP (*.usmap)", ".usmap");
            if (string.IsNullOrEmpty(filePath)) return;

            StatusText = "Generating USMAP...";
            var progress = new Progress<string>(msg =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = msg));

            var bytes = await UsmapExportService.GenerateUsmapAsync(_dump, progress);
            await File.WriteAllBytesAsync(filePath, bytes);

            StatusText = "USMAP exported";
            _log.Info($"USMAP exported to {filePath} ({bytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            SetError(ex);
            _log.Error("USMAP export failed", ex);
        }
    }
}
