using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// Tab positions in the <c>MainWindow.axaml</c> TabControl, used by the
/// panel-to-panel navigation handlers that set
/// <see cref="MainWindowViewModel.SelectedTabIndex"/>. This is the single
/// source of truth for those indices — MUST stay in the same order as the
/// &lt;TabItem&gt; elements in MainWindow.axaml. (These indices silently
/// drifted before: the "ClassStruct" navigations hard-coded 7 but GameClassFilter
/// took index 7, pushing ClassStruct to 8.) The tab-switch *read* path
/// (AOBMaker re-check) instead matches on <c>TabItem.Tag</c> in
/// MainWindow.axaml.cs, which is reorder-proof and needs no entry here.
/// </summary>
internal enum MainTabIndex
{
    LiveWalker = 0,
    InstanceFinder = 1,
    PropertySearch = 2,
    InterestingFunctions = 3,
    InterestingProperties = 4,
    ValueSearch = 5,
    Console = 6,
    Teleport = 7,
    GameClassFilter = 8,
    ClassStruct = 9,
    RelatedObjects = 10,
    // Fixed tail order: the 3 experimental tabs (hidden unless opted in), then
    // Proxy Deploy (always 2nd-to-last), then System/Pointers (always last) —
    // regardless of any future tab additions. When experimental is off the 3
    // tabs collapse, so the visible last two are Proxy Deploy + System.
    Snapshot = 11,
    SpcQuery = 12,
    ClassPivot = 13,
    ProxyDeploy = 14,
    Pointers = 15,   // the "System" tab (str.Tab.Pointers = "System")
}

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
    // Held so Dispose can detach it from the static PropertyXrefDialog event
    // (the lambda captures `this`; without unsubscribe each VM would leak —
    // matters for the test suite, which builds the VM repeatedly).
    private readonly Action<string> _xrefLocateHandler;

    /// <summary>
    /// Platform service, exposed so the window code-behind can route the
    /// global focus-in IME-close through the same abstraction (the
    /// Platform Abstraction rule forbids direct P/Invoke outside it).
    /// </summary>
    public IPlatformService Platform => _platform;
    private readonly AobUsageService? _aobUsage;
    private readonly IAobMakerBridge? _aobMaker;  // captured so InterestingFunctions handlers can ship AA Scripts
    private readonly IExperimentalGate? _experimentalGate;
    private EngineState? _engineState;

    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private string _windowTitle = "UE5 Dump UI";
    [ObservableProperty] private bool _isConnected;

    /// <summary>Global stale-DLL badge shown in the always-visible top bar:
    /// only while connected AND the DLL build differs from / pre-dates the UI's.
    /// Mirrors the per-tab Diagnostics badge (PointerPanelViewModel) but is
    /// visible from every tab, so a hand-deployed old proxy DLL is noticed
    /// before scanning with mismatched offsets. (Re-raised when Pointers'
    /// warning state changes — see ctor — and when IsConnected flips.)</summary>
    public bool ShowBuildMismatchBadge => IsConnected && Pointers.ShowGlobalBuildWarning;
    public string BuildMismatchBadgeText => Pointers.GlobalBuildWarningText;

    /// <summary>Positive counterpart to <see cref="ShowBuildMismatchBadge"/>: true while
    /// connected AND the DLL build matches the UI's. Shown as a subtle "DLL &lt;n&gt;" next to
    /// the version so a current deploy is visibly confirmed — "no badge" alone is ambiguous
    /// with "the warning is broken".</summary>
    public bool ShowDllBuildOk => IsConnected && Pointers.BuildVersionsMatch;
    public string DllBuildOkText => $"DLL {Pointers.DllBuildNumber}";

    /// <summary>Global "unverified UE5.7+ packed layout" badge in the always-visible top bar:
    /// only while connected AND the game runs the *** UNVERIFIED *** packed FUObjectItem layout.
    /// Tells the user that reconstructed addresses and every export are best-effort.</summary>
    public bool ShowPackedLayoutBadge => IsConnected && Pointers.ShowPackedLayoutBadge;
    public string PackedLayoutBadgeText => Pointers.PackedLayoutBadgeText;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBuildMismatchBadge));
        OnPropertyChanged(nameof(ShowDllBuildOk));
        OnPropertyChanged(nameof(ShowPackedLayoutBadge));
    }
    [ObservableProperty] private bool _needsScan;       // True when connected but scan not yet done (proxy DLL mode)
    [ObservableProperty] private bool _isScanning;      // True while trigger_scan is in progress
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _selectedAddressFormatIndex;
    [ObservableProperty] private bool _collapsePointerNodes;
    [ObservableProperty] private int _arrayLimitExponent = 7; // 2^7 = 128
    [ObservableProperty] private int _dropDownLimitExponent = 9; // 2^9 = 512
    [ObservableProperty] private int _csxDrilldownDepth; // 0 = flat (dummy), 1+ = real child structures
    [ObservableProperty] private int _previewLimit = Constants.DefaultPreviewLimit; // Struct preview sub-field count (0-6)
    [ObservableProperty] private int _deepScanElemCapExponent = 8; // 2^8 = 256 (find_by_address deep scan per-container cap)

    // Always-visible top-toolbar AOBMaker status (mirrors the per-tab indicators).
    [ObservableProperty] private bool _isAobMakerAvailable;

    /// <summary>True when an AOBMaker bridge was supplied — gates the toolbar status chip.</summary>
    public bool IsAobMakerConfigured => _aobMaker != null;

    /// <summary>Computed array element limit: 2^ArrayLimitExponent (2..16384).</summary>
    public int ArrayLimit => 1 << ArrayLimitExponent;

    /// <summary>Computed CE DropDownList max entries: 2^DropDownLimitExponent (64..8192).</summary>
    public int DropDownLimit => 1 << DropDownLimitExponent;

    /// <summary>Show warning when array limit &gt;= 256 (high memory usage).</summary>
    public bool ShowArrayLimitWarning => ArrayLimitExponent >= 8;

    /// <summary>Computed per-container element cap for the find_by_address deep
    /// container scan: 2^DeepScanElemCapExponent (16..4096).</summary>
    public int DeepScanElemCap => 1 << DeepScanElemCapExponent;

    /// <summary>
    /// Experimental analysis tabs (Snapshot / SPC Query / Class Pivot) stay
    /// hidden unless the user opts in via the System-tab credit checkbox.
    /// Backed by the shared <see cref="IExperimentalGate"/> so the toggle
    /// (owned by <see cref="PointerPanelViewModel"/>) and this tab-visibility
    /// flag stay in sync. See docs/experimental-snapshot-spc-pivot.md Phase 0.
    /// </summary>
    public bool ExperimentalEnabled
    {
        get => _experimentalGate?.IsEnabled ?? false;
        set
        {
            if (_experimentalGate == null || _experimentalGate.IsEnabled == value) return;
            _experimentalGate.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Lock the experimental opt-in for the rest of this session. Called the
    /// first time the user opens one of the experimental tabs (Snapshot /
    /// SPC Query / Class Pivot) while enabled — from that point the System-tab
    /// opt-in checkbox can no longer be unticked. Session-only (a restart clears
    /// the lock). Idempotent and a no-op when the gate isn't enabled.
    /// </summary>
    public void LockExperimental()
    {
        if (_experimentalGate is { IsEnabled: true, IsLocked: false })
            _experimentalGate.Lock();
    }

    /// <summary>C5: switch to the Class Pivot tab and hand off the chosen
    /// class/property. Guarded so it's inert when the pivot tab isn't available.</summary>
    private async void HandlePivotHandoff(string className, string? propName)
    {
        if (Pivot == null) return;
        try
        {
            SelectedTabIndex = (int)MainTabIndex.ClassPivot;
            await Pivot.PivotForAsync(className, propName);
        }
        catch (Exception ex)
        {
            _log.Error($"Pivot handoff error: {className}.{propName}", ex);
        }
    }

    /// <summary>Enable the "Pivot this property" context-menu items only when the
    /// experimental Class Pivot tab is both present and opted in — so the handoff
    /// stays invisible while experimental features are off.</summary>
    private void UpdatePivotHandoffEnabled()
    {
        bool on = ExperimentalEnabled && Pivot != null;
        if (PropertySearch != null)        PropertySearch.PivotEnabled = on;
        if (InterestingProperties != null) InterestingProperties.PivotEnabled = on;
        if (LiveWalker != null)            LiveWalker.PivotEnabled = on;
        ValueSearch.PivotEnabled = on;
    }

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
    public InterestingPropertiesViewModel InterestingProperties { get; }
    public ValueSearchViewModel ValueSearch { get; }
    public RelatedObjectsViewModel RelatedObjects { get; }
    public ConsoleViewModel Console { get; }
    public TeleportViewModel Teleport { get; }
    public ProxyDeployViewModel? ProxyDeploy { get; }
    /// <summary>Experimental Snapshot tab — null when no snapshot store was
    /// injected (e.g. in unit tests). Gated behind <see cref="ExperimentalEnabled"/>.</summary>
    public SnapshotViewModel? Snapshot { get; }
    /// <summary>Experimental SPC Query tab — shares the snapshot store with
    /// <see cref="Snapshot"/>. Null when no store was injected.</summary>
    public SpcQueryViewModel? Spc { get; }
    /// <summary>Experimental Class Pivot tab — shares the snapshot store.
    /// Null when no store was injected.</summary>
    public ClassPivotViewModel? Pivot { get; }

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

    partial void OnDeepScanElemCapExponentChanged(int value)
    {
        OnPropertyChanged(nameof(DeepScanElemCap));
        InstanceFinder.DeepScanElemCap = DeepScanElemCap;
    }

    /// <summary>Toolbar slider colour — default 0-3, then yellow (4) → orange →
    /// deep red (8) to flag exponential output growth. Max is 8.</summary>
    public Avalonia.Media.IBrush CsxDrilldownDepthBrush => CsxDrilldownDepth switch
    {
        >= 8 => Avalonia.Media.SolidColorBrush.Parse("#E02828"),
        7    => Avalonia.Media.SolidColorBrush.Parse("#E04A2C"),
        6    => Avalonia.Media.SolidColorBrush.Parse("#E0702C"),
        5    => Avalonia.Media.SolidColorBrush.Parse("#E69A17"),
        4    => Avalonia.Media.SolidColorBrush.Parse("#E6C217"),
        _    => Avalonia.Media.SolidColorBrush.Parse("#D4D4D4"),
    };

    partial void OnPreviewLimitChanged(int value)
    {
        LiveWalker.PreviewLimit = value;
        InstanceFinder.PreviewLimit = value;
    }

    // NOTE: the on-tab-switch AOBMaker re-check used to live here as an
    // OnSelectedTabIndexChanged switch keyed on magic tab indices, which
    // silently drifted when tabs were inserted (Pointers ended up checking
    // the wrong tab). It now lives in MainWindow.axaml.cs's
    // MainTabs_SelectionChanged, routed by TabItem.Tag so it can't drift.

    public MainWindowViewModel(
        IPipeClient pipeClient,
        IDumpService dump,
        ILoggingService log,
        IPlatformService platform,
        AobUsageService? aobUsage = null,
        IAobMakerBridge? aobMaker = null,
        IProxyDeployService? proxyDeploy = null,
        IExperimentalGate? experimentalGate = null,
        ISnapshotStore? snapshotStore = null,
        IGlobalHotkeyService? globalHotkeys = null,
        BookmarkStore? bookmarks = null)
    {
        _pipeClient = pipeClient;
        _dump = dump;
        _log = log;
        _platform = platform;
        _aobUsage = aobUsage;
        _aobMaker = aobMaker;
        _experimentalGate = experimentalGate;

        // Keep tab visibility in sync when the toggle is flipped elsewhere
        // (the checkbox lives on the System tab / PointerPanelViewModel). Also
        // re-gate the "Pivot this property" context-menu handoff (C5).
        if (experimentalGate != null)
            experimentalGate.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(ExperimentalEnabled));
                UpdatePivotHandoffEnabled();
            };

        ObjectTree = new ObjectTreeViewModel(dump, log, platform);
        ClassStruct = new ClassStructViewModel(dump, log, platform);
        Pointers = new PointerPanelViewModel(platform, dump, log, aobMaker, aobUsage, experimentalGate, snapshotStore, pipeClient);
        LiveWalker = new LiveWalkerViewModel(dump, log, platform, aobMaker, bookmarks);
        InstanceFinder = new InstanceFinderViewModel(dump, log, platform);
        PropertySearch = new PropertySearchViewModel(dump, log, aobMaker, platform);
        GameClassFilter = new GameClassFilterViewModel(dump, log, platform);
        InterestingFunctions = new InterestingFunctionsViewModel(dump, log, aobMaker, platform);
        InterestingProperties = new InterestingPropertiesViewModel(dump, log, platform);
        ValueSearch = new ValueSearchViewModel(dump, log);
        RelatedObjects = new RelatedObjectsViewModel(dump, log, platform);
        Console = new ConsoleViewModel(dump, log);
        Teleport = new TeleportViewModel(dump, log, platform, aobMaker, globalHotkeys);
        if (snapshotStore != null)
        {
            Snapshot = new SnapshotViewModel(dump, snapshotStore, log, experimentalGate, platform);
            // Diff row -> open its object in Live Walker (same shape as ValueSearch).
            Snapshot.NavigateToInstance += async (addr) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
                }
                catch (Exception ex)
                {
                    _log.Error($"Snapshot NavigateToInstance handler error: {addr}", ex);
                }
            };
            // Diff row -> Locate in GWorld (value/reach: land on the owning object,
            // scroll to the changed field — same shape as SPC / Value Search).
            Snapshot.LocateInGWorld += async (addr, fieldOffset, fieldName) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGWorldAsync(addr, fieldOffset, fieldName, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"Snapshot LocateInGWorld handler error: {addr}", ex);
                }
            };
            Snapshot.LocateInGameEngine += async (addr, fieldOffset, fieldName) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGameEngineAsync(addr, fieldOffset, fieldName, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"Snapshot LocateInGameEngine handler error: {addr}", ex);
                }
            };

            Spc = new SpcQueryViewModel(snapshotStore, log, platform);
            // SPC hit -> open its object in Live Walker (newest snapshot's addr).
            Spc.NavigateToInstance += async (addr) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
                }
                catch (Exception ex)
                {
                    _log.Error($"SPC NavigateToInstance handler error: {addr}", ex);
                }
            };
            // SPC hit -> Locate in GWorld (value/reach: land on the owning object,
            // scroll to the changed field).
            Spc.LocateInGWorld += async (addr, fieldOffset, fieldName) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGWorldAsync(addr, fieldOffset, fieldName, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"SPC LocateInGWorld handler error: {addr}", ex);
                }
            };
            Spc.LocateInGameEngine += async (addr, fieldOffset, fieldName) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGameEngineAsync(addr, fieldOffset, fieldName, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"SPC LocateInGameEngine handler error: {addr}", ex);
                }
            };

            Pivot = new ClassPivotViewModel(snapshotStore, log, platform, dump);
            // Pivot group -> open its representative object in Live Walker.
            Pivot.NavigateToInstance += async (addr) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
                }
                catch (Exception ex)
                {
                    _log.Error($"Pivot NavigateToInstance handler error: {addr}", ex);
                }
            };
            Pivot.LocateInGWorld += async (addr) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGWorldAsync(addr, 0, null, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"Pivot LocateInGWorld handler error: {addr}", ex);
                }
            };
            Pivot.LocateInGameEngine += async (addr) =>
            {
                try
                {
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                    await LiveWalker.LocateInGameEngineAsync(addr, 0, null, stopAtParent: false);
                }
                catch (Exception ex)
                {
                    _log.Error($"Pivot LocateInGameEngine handler error: {addr}", ex);
                }
            };

            // C5: right-click "Pivot this property" from the three source panels ->
            // switch to the Class Pivot tab and pre-select the class/property.
            PropertySearch.NavigateToPivot        += (cls, prop) => HandlePivotHandoff(cls, prop);
            InterestingProperties.NavigateToPivot += (cls, prop) => HandlePivotHandoff(cls, prop);
            LiveWalker.NavigateToPivot            += (cls, prop) => HandlePivotHandoff(cls, prop);
            // Value-locator -> pivot: a value-scan hit already carries class + field.
            ValueSearch.NavigateToPivot           += (cls, prop) => HandlePivotHandoff(cls, prop);

            // "Remove all snapshot data" (System tab) deletes every snapshot DB file —
            // the experimental tabs' cached lists are now stale, so refresh them to the
            // empty state.
            Pointers.SnapshotDataRemoved += () =>
            {
                _ = Snapshot?.RefreshCommand.ExecuteAsync(null);
                _ = Spc?.RefreshCommand.ExecuteAsync(null);
                _ = Pivot?.RefreshCommand.ExecuteAsync(null);
            };
        }
        // Gate the handoff menu items to the experimental flag (and pivot existence).
        UpdatePivotHandoffEnabled();

        if (proxyDeploy != null)
            ProxyDeploy = new ProxyDeployViewModel(proxyDeploy, log);

        // Mirror the per-tab stale-DLL warning into the always-visible top-bar
        // badge so a version mismatch is noticed from any tab.
        Pointers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PointerPanelViewModel.ShowGlobalBuildWarning)
                               or nameof(PointerPanelViewModel.GlobalBuildWarningText)
                               or nameof(PointerPanelViewModel.BuildVersionsMatch)
                               or nameof(PointerPanelViewModel.DllBuildNumber))
            {
                OnPropertyChanged(nameof(ShowBuildMismatchBadge));
                OnPropertyChanged(nameof(BuildMismatchBadgeText));
                OnPropertyChanged(nameof(ShowDllBuildOk));
                OnPropertyChanged(nameof(DllBuildOkText));
            }
            if (e.PropertyName is nameof(PointerPanelViewModel.ShowPackedLayoutBadge)
                               or nameof(PointerPanelViewModel.PackedLayoutBadgeText))
            {
                OnPropertyChanged(nameof(ShowPackedLayoutBadge));
                OnPropertyChanged(nameof(PackedLayoutBadgeText));
            }
            if (e.PropertyName == nameof(PointerPanelViewModel.IsAobMakerAvailable))
                IsAobMakerAvailable = Pointers.IsAobMakerAvailable;
        };

        // Mirror the per-tab AOBMaker availability (LiveWalker + Pointers each
        // probe on their own tab activation) into the always-visible top-toolbar
        // chip so its state stays correct from any tab without a manual refresh.
        IsAobMakerAvailable = _aobMaker?.IsAvailable ?? false;
        LiveWalker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveWalkerViewModel.IsAobMakerAvailable))
                IsAobMakerAvailable = LiveWalker.IsAobMakerAvailable;
        };

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
                ValueSearch.SetEngineState(state);
                InterestingFunctions.IsGWorldAvailable = state.HasGWorld;
                InterestingProperties.IsGWorldAvailable = state.HasGWorld;
                Snapshot?.SetEngineState(state);
                Spc?.SetEngineState(state);
                Pivot?.SetEngineState(state);

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

        // Wire ObjectTree right-click -> InstanceFinder (find instances of the
        // selected node's class, optionally ANDed with its object name) + tab
        // switch + auto-run. Saves the copy-type / paste-into-Instances / Search
        // round-trip. Mirrors the GameClassFilter / PropertySearch handoffs.
        ObjectTree.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error($"ObjectTree NavigateToInstanceFinder handler error: {className}", ex);
            }
        };
        ObjectTree.NavigateToInstanceFinderWithName += async (className, name) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAndNameAsync(className, name);
            }
            catch (Exception ex)
            {
                _log.Error($"ObjectTree NavigateToInstanceFinderWithName handler error: {className}", ex);
            }
        };

        // Wire InstanceFinder -> LiveWalker navigation + tab switch
        InstanceFinder.NavigateToLiveWalker += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Switch to Live Walker tab
                await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error("NavigateToLiveWalker handler error", ex);
            }
        };

        // Wire InstanceFinder -> "Locate in GWorld". The selected row IS the
        // target object, so land ON it (stopAtParent: false) — same as Value
        // Search / Snapshot / SPC. Parent-stop left the user on the holder
        // object (e.g. BP_LifeGameInstance_C.m_savedata) instead of the object
        // they picked; the full GWorld→…→target spine is still in the
        // breadcrumb, so the holder is one click up via Parent ↑.
        InstanceFinder.LocateInGWorld += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGWorldAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"InstanceFinder LocateInGWorld handler error: {addr}", ex);
            }
        };
        InstanceFinder.LocateInGameEngine += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGameEngineAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"InstanceFinder LocateInGameEngine handler error: {addr}", ex);
            }
        };

        // Wire InstanceFinder container match -> "Locate in GWorld" (the address is
        // a value inside a container element → reach the owning object + drill the
        // full container chain, including deeply-nested values).
        InstanceFinder.LocateContainerInGWorld += async (match) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateContainerInGWorldAsync(match);
            }
            catch (Exception ex)
            {
                _log.Error($"InstanceFinder LocateContainerInGWorld handler error: {match.OwnerAddress}", ex);
            }
        };
        InstanceFinder.LocateContainerInGameEngine += async (match) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateContainerInGameEngineAsync(match);
            }
            catch (Exception ex)
            {
                _log.Error($"InstanceFinder LocateContainerInGameEngine handler error: {match.OwnerAddress}", ex);
            }
        };

        // Wire RelatedObjects -> LiveWalker / "Locate in GWorld" / InstanceFinder.
        // Each related-object row lands ON the picked object (stopAtParent: false),
        // same as Instance Finder / Value Search.
        RelatedObjects.NavigateToLiveWalker += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.NavigateToAddressCommand.ExecuteAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error($"RelatedObjects NavigateToLiveWalker handler error: {addr}", ex);
            }
        };
        RelatedObjects.LocateInGWorld += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGWorldAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"RelatedObjects LocateInGWorld handler error: {addr}", ex);
            }
        };
        RelatedObjects.LocateInGameEngine += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGameEngineAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"RelatedObjects LocateInGameEngine handler error: {addr}", ex);
            }
        };
        RelatedObjects.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error($"RelatedObjects NavigateToInstanceFinder handler error: {className}", ex);
            }
        };

        // Wire Teleport "Locate in GWorld" -> land ON the player pawn (the object
        // whose Current Pose is shown), same shape as Instance Finder / Related.
        Teleport.LocateInGWorld += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGWorldAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"Teleport LocateInGWorld handler error: {addr}", ex);
            }
        };
        Teleport.LocateInGameEngine += async (addr) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGameEngineAsync(addr, 0, null, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"Teleport LocateInGameEngine handler error: {addr}", ex);
            }
        };
        // Per-vector locate (position / velocity): land ON the FVector field inside
        // its owning component (RootComponent / CharacterMovement) — same value-
        // landing handoff Value Search uses (owner addr + field offset + name).
        Teleport.LocateValueInGWorld += async (owner, fieldOffset, fieldName) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGWorldAsync(owner, fieldOffset, fieldName, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"Teleport LocateValueInGWorld handler error: {owner}+0x{fieldOffset:X}", ex);
            }
        };

        // Wire Instance Finder / Value Search / Live Walker -> Related Objects:
        // hand the chosen object's address to the Related tab and load its graph.
        async Task OpenRelatedAsync(string addr)
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.RelatedObjects;
                await RelatedObjects.LoadForAddressAsync(addr);
            }
            catch (Exception ex)
            {
                _log.Error($"NavigateToRelatedObjects handler error: {addr}", ex);
            }
        }
        InstanceFinder.NavigateToRelatedObjects += async (addr) => await OpenRelatedAsync(addr);
        ValueSearch.NavigateToRelatedObjects += async (addr) => await OpenRelatedAsync(addr);
        LiveWalker.NavigateToRelatedObjects += async (addr) => await OpenRelatedAsync(addr);

        // Wire LiveWalker -> InstanceFinder (per-field "inst" button: open the
        // field's pointed-to object class + switch tab + auto-run, mirroring the
        // Property Search / Interesting Funcs+Props "inst" handoff).
        LiveWalker.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error($"LiveWalker NavigateToInstanceFinder handler error: {className}", ex);
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
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder; // Switch to Instance Finder tab
                await InstanceFinder.SearchForClassAsync(className);
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
                SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Switch to Live Walker tab
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
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder; // Switch to Instance Finder tab
                await InstanceFinder.SearchForClassAsync(className);
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
                SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Switch to Live Walker tab
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
                SelectedTabIndex = (int)MainTabIndex.ClassStruct; // Switch to ClassStruct tab
                await ClassStruct.LoadClassCommand.ExecuteAsync(classAddr);
            }
            catch (Exception ex)
            {
                _log.Error("GameClassFilter NavigateToClassStruct handler error", ex);
            }
        };

        // Wire InterestingFunctions / InterestingProperties -> InstanceFinder
        // (per-row "inst" button: pre-fill class name + switch tab + auto-run,
        // mirroring the Property Search / Value Search "inst" handoff).
        InterestingFunctions.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions NavigateToInstanceFinder handler error: {className}", ex);
            }
        };
        InterestingProperties.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder;
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingProperties NavigateToInstanceFinder handler error: {className}", ex);
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
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Live Walker
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
                    SelectedTabIndex = (int)MainTabIndex.ClassStruct; // ClassStruct fallback
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

        // Wire InterestingFunctions -> "Locate in GWorld": resolve a live (non-CDO)
        // instance of the function's class (same find_instance path as
        // NavigateToFunction above), then run the GWorld path search in parent mode
        // (stop before drilling into the instance). A function isn't itself a world
        // object, so the meaningful target is "where do instances of this class live".
        InterestingFunctions.LocateInGWorld += async (className) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                if (string.IsNullOrEmpty(liveAddr))
                {
                    LiveWalker.StatusText = $"No live (non-CDO) instance of {className} to locate in GWorld.";
                    return;
                }
                await LiveWalker.LocateInGWorldAsync(liveAddr, 0, null, stopAtParent: true);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions LocateInGWorld handler error: {className}", ex);
            }
        };

        // Xref dialog (code-behind, no per-instance DI): give it the app's AOBMaker
        // bridge for "Disassemble in CE", and handle its "Locate class" request by
        // resolving a live (non-CDO) instance + navigating to Live Walker — same
        // class-name path as the Interesting Functions locate just above.
        Views.PropertyXrefDialog.SharedAobMaker = aobMaker;
        _xrefLocateHandler = async (className) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                if (string.IsNullOrEmpty(liveAddr))
                {
                    LiveWalker.StatusText = $"No live (non-CDO) instance of {className} to locate in GWorld.";
                    return;
                }
                await LiveWalker.LocateInGWorldAsync(liveAddr, 0, null, stopAtParent: true);
            }
            catch (Exception ex)
            {
                _log.Error($"Xref dialog LocateClass handler error: {className}", ex);
            }
        };
        Views.PropertyXrefDialog.LocateClassInGWorldRequested += _xrefLocateHandler;

        InterestingFunctions.LocateInGameEngine += async (className) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                if (string.IsNullOrEmpty(liveAddr))
                {
                    LiveWalker.StatusText = $"No live (non-CDO) instance of {className} to locate in GameEngine.";
                    return;
                }
                await LiveWalker.LocateInGameEngineAsync(liveAddr, 0, null, stopAtParent: true);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingFunctions LocateInGameEngine handler error: {className}", ex);
            }
        };

        // Wire InterestingProperties -> Live Walker. Same pattern as
        // InterestingFunctions: try find_instance for a non-CDO live address,
        // fall back to ClassStruct when none. We don't scroll to the
        // specific property row in round 1 (LiveWalker has no public
        // ScrollToField yet) — the property name is left in the status
        // text so the user knows what to look for.
        InterestingProperties.NavigateToProperty += async (className, propName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;

                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
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
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Live Walker
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(liveAddr);
                    // Pre-fill the search box so the user lands with the
                    // property highlighted instead of having to scroll.
                    LiveWalker.SearchText = propName;
                    StatusText = $"Navigated to {className} (live instance {liveAddr}); searching {propName}";
                    _log.Info($"InterestingProperties -> LiveWalker: {className}.{propName} @ {liveAddr}");
                }
                else
                {
                    SelectedTabIndex = (int)MainTabIndex.ClassStruct; // ClassStruct fallback
                    var classes = await _dump.ListClassesAsync(gameOnly: false);
                    var match = classes.Classes.FirstOrDefault(
                        c => c.ClassName.Equals(className, StringComparison.Ordinal));
                    if (match != null && !string.IsNullOrEmpty(match.ClassAddr))
                    {
                        await ClassStruct.LoadClassCommand.ExecuteAsync(match.ClassAddr);
                        StatusText = $"No live instance of {className}; showing class metadata (look for {propName})";
                        _log.Info($"InterestingProperties -> ClassStruct fallback: {className}.{propName}");
                    }
                    else
                    {
                        StatusText = $"Class {className} not resolvable";
                        _log.Warn($"InterestingProperties navigate: {className} not found");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingProperties NavigateToProperty handler error: {className}.{propName}", ex);
            }
        };

        // Wire InterestingProperties -> "Locate in GWorld": same className→live-instance
        // resolution as InterestingFunctions (a property is a class-level definition,
        // not a world object, so we locate where instances of its class live).
        InterestingProperties.LocateInGWorld += async (className) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                if (string.IsNullOrEmpty(liveAddr))
                {
                    LiveWalker.StatusText = $"No live (non-CDO) instance of {className} to locate in GWorld.";
                    return;
                }
                await LiveWalker.LocateInGWorldAsync(liveAddr, 0, null, stopAtParent: true);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingProperties LocateInGWorld handler error: {className}", ex);
            }
        };
        InterestingProperties.LocateInGameEngine += async (className) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;
                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
                string? liveAddr = null;
                foreach (var inst in instances.Instances)
                {
                    if (string.IsNullOrEmpty(inst.Address)) continue;
                    if (inst.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;
                    liveAddr = inst.Address;
                    break;
                }
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                if (string.IsNullOrEmpty(liveAddr))
                {
                    LiveWalker.StatusText = $"No live (non-CDO) instance of {className} to locate in GameEngine.";
                    return;
                }
                await LiveWalker.LocateInGameEngineAsync(liveAddr, 0, null, stopAtParent: true);
            }
            catch (Exception ex)
            {
                _log.Error($"InterestingProperties LocateInGameEngine handler error: {className}", ex);
            }
        };

        InterestingProperties.RequestCopyText += async (text) =>
        {
            if (string.IsNullOrEmpty(text)) return;
            try { await _platform.CopyToClipboardAsync(text); }
            catch (Exception ex)
            {
                _log.Error($"InterestingProperties clipboard copy failed: {ex.Message}", ex);
            }
        };

        // Wire InterestingProperties -> CT save dialog. The VM builds
        // the .CT payload and emits (defaultFileName, ctXml); we own
        // the platform-specific save dialog + write here so the VM
        // stays IO-free and unit-testable. The CT filter matches what
        // CE associates with the .CT extension on Windows.
        InterestingProperties.RequestSaveCheatTable += async (defaultName, ctXml) =>
        {
            await SaveCheatTableAsync(defaultName, ctXml, "InterestingProperties");
        };

        // Wire ValueSearch -> LiveWalker (open candidate's owning instance)
        // + clipboard. Same shape as InstanceFinder.NavigateToLiveWalker
        // since ValueSearch already has the instance address resolved.
        ValueSearch.NavigateToInstance += async (addr, fieldOffset, fieldName) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;  // Live Walker
                // Focus the candidate's owning field (matched by offset) so the
                // user lands ON the matched value — not just the instance's
                // field list — and drills to the element for container hits.
                await LiveWalker.NavigateToInstanceFieldAsync(addr, fieldOffset, fieldName);
            }
            catch (Exception ex)
            {
                _log.Error($"ValueSearch NavigateToInstance handler error: {addr}", ex);
            }
        };

        // Wire ValueSearch -> "Locate in GWorld" (property value → reach the
        // owning object + scroll to the value field).
        ValueSearch.LocateInGWorld += async (addr, fieldOffset, fieldName) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGWorldAsync(addr, fieldOffset, fieldName, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"ValueSearch LocateInGWorld handler error: {addr}", ex);
            }
        };
        ValueSearch.LocateInGameEngine += async (addr, fieldOffset, fieldName) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.LiveWalker;
                await LiveWalker.LocateInGameEngineAsync(addr, fieldOffset, fieldName, stopAtParent: false);
            }
            catch (Exception ex)
            {
                _log.Error($"ValueSearch LocateInGameEngine handler error: {addr}", ex);
            }
        };
        ValueSearch.RequestCopyText += async (text) =>
        {
            if (string.IsNullOrEmpty(text)) return;
            try { await _platform.CopyToClipboardAsync(text); }
            catch (Exception ex)
            {
                _log.Error($"ValueSearch clipboard copy failed: {ex.Message}", ex);
            }
        };

        // Wire ValueSearch -> InstanceFinder (the per-row "inst" button:
        // pre-fill the hit's owning class + switch tab + auto-run the search,
        // mirroring the Property Search / Game Class "finder" handoff above).
        ValueSearch.NavigateToInstanceFinder += async (className) =>
        {
            try
            {
                SelectedTabIndex = (int)MainTabIndex.InstanceFinder; // Switch to Instance Finder tab
                await InstanceFinder.SearchForClassAsync(className);
            }
            catch (Exception ex)
            {
                _log.Error("ValueSearch NavigateToInstanceFinder handler error", ex);
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

        // Wire InterestingFunctions -> CT save dialog (sister to the
        // Properties hookup above).
        InterestingFunctions.RequestSaveCheatTable += async (defaultName, ctXml) =>
        {
            await SaveCheatTableAsync(defaultName, ctXml, "InterestingFunctions");
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

                // Fast path: TRULY trivial functions only (no inputs AND no return).
                // Functions like KismetSystemLibrary::GetGameName have no inputs
                // but DO return a value -- they need the dialog so the Verify
                // Return Value toggle is reachable. Mirrors LiveWalker's path.
                var inputParams = funcMatch.Params.Where(p => !p.IsReturn).ToList();
                var hasReturn = funcMatch.Params.Any(p => p.IsReturn);
                if (inputParams.Count == 0 && !hasReturn)
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

        // ─────────────────────────────────────────────────────────────
        // Console panel wiring (Console = UFUNCTION(exec) discovery+invoke)
        //
        // Mirrors the InterestingFunctions handler bodies above. Duplicated
        // intentionally for v1 — a future shared-helper extraction would
        // touch GameClassFilter / InterestingFunctions / InterestingProperties
        // / Console (4 callers), worth its own refactor pass.
        // ─────────────────────────────────────────────────────────────

        Console.NavigateToFunction += async (className, funcName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className)) return;

                var instances = await _dump.FindInstancesAsync(className, exactMatch: true, limit: 5);
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
                    SelectedTabIndex = (int)MainTabIndex.LiveWalker; // Live Walker
                    await LiveWalker.NavigateToAddressCommand.ExecuteAsync(liveAddr);
                    var picked = await LiveWalker.TrySelectFunctionByNameAsync(funcName);
                    StatusText = picked
                        ? $"Navigated to {className}::{funcName} (live instance {liveAddr})"
                        : $"Navigated to {className} @ {liveAddr}; exec '{funcName}' not in this class";
                    _log.Info($"Console -> LiveWalker: {className}::{funcName} @ {liveAddr}" +
                              (picked ? "" : " (function not selected)"));
                }
                else
                {
                    SelectedTabIndex = (int)MainTabIndex.ClassStruct; // ClassStruct fallback
                    var classes = await _dump.ListClassesAsync(gameOnly: false);
                    var match = classes.Classes.FirstOrDefault(
                        c => c.ClassName.Equals(className, StringComparison.Ordinal));
                    if (match != null && !string.IsNullOrEmpty(match.ClassAddr))
                    {
                        await ClassStruct.LoadClassCommand.ExecuteAsync(match.ClassAddr);
                        StatusText = $"No live instance of {className}; showing class metadata " +
                                     $"(exec '{funcName}' — UCheatManager subclasses often need an active PlayerController)";
                        _log.Info($"Console -> ClassStruct fallback: {className}::{funcName}");
                    }
                    else
                    {
                        StatusText = $"Class {className} not resolvable (Find Instances + ListClasses both empty)";
                        _log.Warn($"Console navigate: {className} not found");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Console NavigateToFunction handler error: {className}::{funcName}", ex);
            }
        };

        // Console -> RequestParameterInvoke fires when a multi-param exec
        // command is selected. Opens the standard InvokeParamDialog in
        // PipeInvoke mode so the user fills values + presses FIRE to run.
        Console.RequestParameterInvoke += async (className, funcName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(funcName)) return;

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

                if (Avalonia.Application.Current?.ApplicationLifetime is not
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not { } owner)
                    return;

                var inputParams = funcMatch.Params.Where(p => !p.IsReturn).ToList();
                var dialog = new Views.InvokeParamDialog(
                    className, funcName, inputParams, funcMatch.Params, funcMatch.ParmsSize,
                    instanceAddr, _dump, _engineState?.UEVersion ?? 0,
                    aobMaker: _aobMaker, platform: _platform,
                    mode: Views.InvokeDialogMode.PipeInvoke);
                var result = await dialog.ShowDialog<string?>(owner);
                StatusText = result == "ok"
                    ? $"exec {className}::{funcName} dialog closed"
                    : $"exec {funcName} cancelled";
                _log.Info($"Console PipeInvoke dialog " +
                          $"{(result == "ok" ? "completed" : "cancelled")}: {className}::{funcName}");
            }
            catch (Exception ex)
            {
                _log.Error($"Console RequestParameterInvoke handler error: {className}::{funcName}", ex);
                StatusText = $"exec dialog failed: {ex.Message}";
            }
        };

        // Console -> RequestCopyBakedScript reuses the InterestingFunctions
        // logic body. Same shape as above (no-arg fast path + dialog path).
        Console.RequestCopyBakedScript += async (className, funcName) =>
        {
            try
            {
                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(funcName)) return;

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

                var inputParams = funcMatch.Params.Where(p => !p.IsReturn).ToList();
                var hasReturn = funcMatch.Params.Any(p => p.IsReturn);
                if (inputParams.Count == 0 && !hasReturn)
                {
                    var script = Services.BakedScriptGenerator.Generate(
                        className, funcName, funcMatch.ParmsSize,
                        Array.Empty<Models.BakedParamValue>());
                    var description = $"exec (baked, no args): {className}::{funcName}";
                    bool wasAvailable = _aobMaker?.IsAvailable ?? false;
                    bool sentToCe = false;
                    if (_aobMaker != null && wasAvailable)
                        sentToCe = await _aobMaker.CreateAAScriptAsync(description, script, autoActivate: false);
                    if (!sentToCe)
                        await _platform.CopyToClipboardAsync(script);
                    StatusText = sentToCe
                        ? $"AA Script created in CE: {funcName}"
                        : wasAvailable
                            ? $"⚠ AOBMaker pipe broke (CE closed?) — script copied to clipboard"
                            : $"AOBMaker not connected — script copied to clipboard ({funcName})";
                    _log.Info($"Console baked AA Script (no args) " +
                              $"{(sentToCe ? "sent to CE" : "to clipboard")}: " +
                              $"{className}::{funcName} (wasAvailable={wasAvailable})");
                    return;
                }

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
                _log.Info($"Console CopyBakedScript dialog " +
                          $"{(result == "ok" ? "completed" : "cancelled")}: {className}::{funcName}");
            }
            catch (Exception ex)
            {
                _log.Error($"Console RequestCopyBakedScript handler error: {className}::{funcName}", ex);
                StatusText = $"AA Script export failed: {ex.Message}";
            }
        };

        // Console -> RequestDebugCameraCeScript builds the stateful
        // setDebugCamera [ENABLE]/[DISABLE] memory-record script and ships it
        // to AOBMaker (a CE AA-script record) or the clipboard. Self-contained:
        // no class/func resolution needed — the helper resolves the export.
        Console.RequestDebugCameraCeScript += async () =>
        {
            try
            {
                var script = Services.DebugCameraScriptGenerator.Generate();
                const string description = "Debug Camera (force on/off): setDebugCamera";
                bool wasAvailable = _aobMaker?.IsAvailable ?? false;
                bool sentToCe = false;
                if (_aobMaker != null && wasAvailable)
                    sentToCe = await _aobMaker.CreateAAScriptAsync(description, script, autoActivate: false);
                if (!sentToCe)
                    await _platform.CopyToClipboardAsync(script);
                StatusText = sentToCe
                    ? "Debug Camera AA Script created in CE (tick = ON, untick = OFF)."
                    : wasAvailable
                        ? "⚠ AOBMaker pipe broke (CE closed?) — Debug Camera script copied to clipboard."
                        : "AOBMaker not connected — Debug Camera script copied to clipboard " +
                          "(embed ue5_invoke_helper.lua in your .CT).";
                _log.Info($"Console Debug Camera CE script " +
                          $"{(sentToCe ? "sent to CE" : "to clipboard")} (wasAvailable={wasAvailable})");
            }
            catch (Exception ex)
            {
                _log.Error("Console RequestDebugCameraCeScript handler error", ex);
                StatusText = $"Debug Camera script export failed: {ex.Message}";
            }
        };

        _pipeClient.ConnectionStateChanged += (connected) =>
        {
            if (!connected) _log.StopProcessMirror();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = connected;
                StatusText = connected ? "Connected" : "Disconnected";
                Teleport.SetConnected(connected);
                if (!connected) WindowTitle = "UE5 Dump UI";
            });
        };
    }

    /// <summary>
    /// Audit fixes #16/#17: dispose owned child VMs that hold timers /
    /// CancellationTokenSources. Called from MainWindow.Closed so timer
    /// callbacks don't fire after the window is gone. Any child VM that is
    /// IDisposable (owns a timer / CTS / SQLite handle) MUST be disposed here —
    /// the VM's own Dispose() has no other caller.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Detach from the static xref-dialog hooks so this VM doesn't leak.
        Views.PropertyXrefDialog.LocateClassInGWorldRequested -= _xrefLocateHandler;
        Views.PropertyXrefDialog.SharedAobMaker = null;

        ObjectTree.Dispose();
        LiveWalker.Dispose();
        // InstanceFinder owns a keyword-filter debounce Timer + a class-noise re-run
        // CTS (the Timer/CTS lambdas root the VM); dispose so they can't fire after teardown.
        InstanceFinder.Dispose();
        // PropertySearch is IDisposable (owns a debounce System.Threading.Timer);
        // its Dispose had no caller before, leaking the timer until process exit.
        PropertySearch.Dispose();
        // Teleport owns a DispatcherTimer (auto-refresh) — dispose so it can't
        // tick after the window closes.
        Teleport.Dispose();

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

    // ──────────────────────────────────────────────────────────────────────
    // Global panel/Live-Walker OPTIONS persistence (stable preferences only).
    //
    // Wiring lives here (not in the giant ctor) and is kicked off by App after
    // construction via InitializeOptionsPersistence. LOAD applies saved values
    // under _suppressOptionSave so the resulting PropertyChanged storm doesn't
    // re-save. SAVE is a single debounced write triggered when any *persistable*
    // property of a tracked VM changes — filtered by the per-VM name sets so the
    // constant live-data churn (Fields/StatusText/results/selections) never saves.
    // ──────────────────────────────────────────────────────────────────────

    private UiOptionsStore? _uiOptions;
    private bool _suppressOptionSave;
    private Timer? _optionSaveDebounce;
    private const int OptionSaveDebounceMs = 400;

    /// <summary>
    /// Called by App right after construction: load the saved options, apply them
    /// (suppressed), then start tracking changes for debounced save-on-change.
    /// </summary>
    public void InitializeOptionsPersistence(UiOptionsStore store)
    {
        _uiOptions = store;
        var o = store.Load();

        _suppressOptionSave = true;
        try { ApplyOptions(o); }
        catch (Exception ex) { _log.Error("UiOptions apply failed", ex); }
        finally { _suppressOptionSave = false; }

        WireOptionSaveTracking();
    }

    /// <summary>Cancel the debounce and write the current options immediately
    /// (called on app shutdown so a change made &lt;debounce before exit lands).</summary>
    public void FlushOptions()
    {
        _optionSaveDebounce?.Change(Timeout.Infinite, Timeout.Infinite);
        SaveOptionsNow();
    }

    private void ScheduleOptionSave()
    {
        if (_uiOptions == null) return;
        _optionSaveDebounce ??= new Timer(_ => SaveOptionsNow());
        _optionSaveDebounce.Change(OptionSaveDebounceMs, Timeout.Infinite);
    }

    // Reads simple value-type VM properties only (bool/int/double/enum/string) —
    // safe to run on the debounce threadpool thread (no collections, no UI objects).
    private void SaveOptionsNow()
    {
        try { _uiOptions?.Save(BuildOptions()); }
        catch (Exception ex) { _log.Error("UiOptions save failed", ex); }
    }

    private void Track(ObservableObject vm, HashSet<string> persistable)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (_suppressOptionSave) return;
            if (e.PropertyName != null && persistable.Contains(e.PropertyName))
                ScheduleOptionSave();
        };
    }

    private void WireOptionSaveTracking()
    {
        Track(this, MainPersist);
        Track(LiveWalker, LiveWalkerPersist);
        Track(ValueSearch, ValueSearchPersist);
        Track(InstanceFinder, InstanceFinderPersist);
        Track(PropertySearch, PropertySearchPersist);
        Track(Teleport, TeleportPersist);
        Track(InterestingFunctions, InterestingFuncsPersist);
        Track(InterestingProperties, InterestingPropsPersist);
        Track(Console, ConsolePersist);
        Track(GameClassFilter, GameClassFilterPersist);
        if (Snapshot != null) Track(Snapshot, SnapshotPersist);
        if (Spc != null) Track(Spc, SpcPersist);
        if (Pivot != null) Track(Pivot, PivotPersist);
        if (ProxyDeploy != null) Track(ProxyDeploy, ProxyDeployPersist);
    }

    // Persistable property-name sets — used both to filter PropertyChanged and as
    // the single source of truth for what each VM persists. nameof keeps them
    // compile-safe against renames.
    private static readonly HashSet<string> MainPersist = new()
    {
        nameof(SelectedAddressFormatIndex), nameof(CollapsePointerNodes),
        nameof(ArrayLimitExponent), nameof(DropDownLimitExponent),
        nameof(CsxDrilldownDepth), nameof(PreviewLimit), nameof(DeepScanElemCapExponent),
    };
    private static readonly HashSet<string> LiveWalkerPersist = new()
    {
        nameof(LiveWalkerViewModel.CollapseChain), nameof(LiveWalkerViewModel.DescShowOffset),
        nameof(LiveWalkerViewModel.DescShowType), nameof(LiveWalkerViewModel.FlattenGasAttributes),
        nameof(LiveWalkerViewModel.FlattenLeafStructs), nameof(LiveWalkerViewModel.FlattenLeafRecords),
        nameof(LiveWalkerViewModel.CollapseLeafPointers),
        nameof(LiveWalkerViewModel.FlattenColorEnabled), nameof(LiveWalkerViewModel.FlattenColorEven),
        nameof(LiveWalkerViewModel.FlattenColorOdd),
        nameof(LiveWalkerViewModel.DedupSharedObjects),
        nameof(LiveWalkerViewModel.ExcludeSystemComponents), nameof(LiveWalkerViewModel.GWorldLocateDepth),
        nameof(LiveWalkerViewModel.GWorldLocateDeep),
        nameof(LiveWalkerViewModel.AutoRefreshIntervalSec),
    };
    private static readonly HashSet<string> ValueSearchPersist = new()
    {
        nameof(ValueSearchViewModel.SelectedDataType), nameof(ValueSearchViewModel.SelectedScanType),
        nameof(ValueSearchViewModel.GameOnly), nameof(ValueSearchViewModel.MaxResults),
        nameof(ValueSearchViewModel.ScanTimeoutSeconds), nameof(ValueSearchViewModel.ParallelScan),
        nameof(ValueSearchViewModel.BatchRead), nameof(ValueSearchViewModel.DeepScan),
        nameof(ValueSearchViewModel.CrossObjectScan), nameof(ValueSearchViewModel.NativeCScan),
        nameof(ValueSearchViewModel.NewestFirst), nameof(ValueSearchViewModel.PreFilterNoise),
        nameof(ValueSearchViewModel.SelectedRoundingMode), nameof(ValueSearchViewModel.CaseSensitive),
    };
    private static readonly HashSet<string> SnapshotPersist = new()
    {
        nameof(SnapshotViewModel.GameOnly), nameof(SnapshotViewModel.AutoSkipNoise),
        nameof(SnapshotViewModel.IncludeNativeFields), nameof(SnapshotViewModel.SelectedScope),
        nameof(SnapshotViewModel.SelectedFamily), nameof(SnapshotViewModel.SelectedMaxDataset),
        nameof(SnapshotViewModel.ShowUsageBar), nameof(SnapshotViewModel.GroupDeep),
        nameof(SnapshotViewModel.SelectedRoundingMode),
    };
    private static readonly HashSet<string> InstanceFinderPersist = new()
    {
        nameof(InstanceFinderViewModel.ExactMatch), nameof(InstanceFinderViewModel.NewestFirst),
        nameof(InstanceFinderViewModel.InstanceSearchCap), nameof(InstanceFinderViewModel.DeepScanElemCap),
    };
    private static readonly HashSet<string> PropertySearchPersist = new()
    {
        nameof(PropertySearchViewModel.GameClassesOnly), nameof(PropertySearchViewModel.DeepSearch),
    };
    private static readonly HashSet<string> TeleportPersist = new()
    {
        nameof(TeleportViewModel.ZOffset), nameof(TeleportViewModel.TraceChannel),
        nameof(TeleportViewModel.FallbackToCenter), nameof(TeleportViewModel.CursorHotkeyEnabled),
        nameof(TeleportViewModel.RelativeDistance), nameof(TeleportViewModel.RelativeHorizontal),
        nameof(TeleportViewModel.CoordSetRotation), nameof(TeleportViewModel.AutoRefresh),
    };
    private static readonly HashSet<string> SpcPersist = new()
    {
        nameof(SpcQueryViewModel.SelectedJoinMode),
        nameof(SpcQueryViewModel.SelectedRoundingMode),
    };
    private static readonly HashSet<string> PivotPersist = new()
    {
        nameof(ClassPivotViewModel.SelectedSource), nameof(ClassPivotViewModel.SelectedKeyMode),
    };
    private static readonly HashSet<string> InterestingFuncsPersist = new()
    {
        nameof(InterestingFunctionsViewModel.GameOnly), nameof(InterestingFunctionsViewModel.ShowAll),
    };
    private static readonly HashSet<string> InterestingPropsPersist = new()
    {
        nameof(InterestingPropertiesViewModel.GameOnly), nameof(InterestingPropertiesViewModel.UnusualOnly),
        nameof(InterestingPropertiesViewModel.ShowAll),
    };
    private static readonly HashSet<string> ConsolePersist = new() { nameof(ConsoleViewModel.GameOnly) };
    private static readonly HashSet<string> GameClassFilterPersist = new() { nameof(GameClassFilterViewModel.GameClassesOnly) };
    private static readonly HashSet<string> ProxyDeployPersist = new()
    {
        nameof(ProxyDeployViewModel.SelectedProxyType), nameof(ProxyDeployViewModel.ForceOverwrite),
    };

    /// <summary>Apply saved options to every VM. Runs under _suppressOptionSave.
    /// Setters with side effects (address-format fan-out, scan-timeout clamp,
    /// NativeCScan→NewestFirst) still fire — only the SAVE is suppressed.</summary>
    private void ApplyOptions(UiOptionsSettings o)
    {
        // Main display controls first — their OnChanged fans out to child VMs.
        SelectedAddressFormatIndex = o.Main.SelectedAddressFormatIndex;
        CollapsePointerNodes = o.Main.CollapsePointerNodes;
        ArrayLimitExponent = o.Main.ArrayLimitExponent;
        DropDownLimitExponent = o.Main.DropDownLimitExponent;
        CsxDrilldownDepth = o.Main.CsxDrilldownDepth;
        PreviewLimit = o.Main.PreviewLimit;
        DeepScanElemCapExponent = o.Main.DeepScanElemCapExponent;

        var lw = o.LiveWalker;
        LiveWalker.CollapseChain = lw.CollapseChain;
        LiveWalker.DescShowOffset = lw.DescShowOffset;
        LiveWalker.DescShowType = lw.DescShowType;
        LiveWalker.FlattenGasAttributes = lw.FlattenGasAttributes;
        LiveWalker.FlattenLeafStructs = lw.FlattenLeafStructs;
        LiveWalker.FlattenLeafRecords = lw.FlattenLeafRecords;
        LiveWalker.CollapseLeafPointers = lw.CollapseLeafPointers;
        LiveWalker.FlattenColorEnabled = lw.FlattenColorEnabled;
        LiveWalker.FlattenColorEven = lw.FlattenColorEven;
        LiveWalker.FlattenColorOdd = lw.FlattenColorOdd;
        LiveWalker.DedupSharedObjects = lw.DedupSharedObjects;
        LiveWalker.ExcludeSystemComponents = lw.ExcludeSystemComponents;
        LiveWalker.GWorldLocateDepth = lw.GWorldLocateDepth;
        LiveWalker.GWorldLocateDeep = lw.GWorldLocateDeep;
        LiveWalker.AutoRefreshIntervalSec = lw.AutoRefreshIntervalSec;

        var vs = o.ValueSearch;
        vs_Apply(vs);

        var inf = o.InstanceFinder;
        InstanceFinder.ExactMatch = inf.ExactMatch;
        InstanceFinder.NewestFirst = inf.NewestFirst;
        InstanceFinder.InstanceSearchCap = inf.InstanceSearchCap;
        InstanceFinder.DeepScanElemCap = inf.DeepScanElemCap;

        PropertySearch.GameClassesOnly = o.PropertySearch.GameClassesOnly;
        PropertySearch.DeepSearch = o.PropertySearch.DeepSearch;

        var tp = o.Teleport;
        Teleport.ZOffset = tp.ZOffset;
        Teleport.TraceChannel = tp.TraceChannel;
        Teleport.FallbackToCenter = tp.FallbackToCenter;
        Teleport.CursorHotkeyEnabled = tp.CursorHotkeyEnabled;
        Teleport.RelativeDistance = tp.RelativeDistance;
        Teleport.RelativeHorizontal = tp.RelativeHorizontal;
        Teleport.CoordSetRotation = tp.CoordSetRotation;
        Teleport.AutoRefresh = tp.AutoRefresh;

        InterestingFunctions.GameOnly = o.InterestingFuncs.GameOnly;
        InterestingFunctions.ShowAll = o.InterestingFuncs.ShowAll;

        InterestingProperties.GameOnly = o.InterestingProps.GameOnly;
        InterestingProperties.UnusualOnly = o.InterestingProps.UnusualOnly;
        InterestingProperties.ShowAll = o.InterestingProps.ShowAll;

        Console.GameOnly = o.Console.GameOnly;
        GameClassFilter.GameClassesOnly = o.GameClassFilter.GameClassesOnly;

        if (Snapshot != null)
        {
            var sn = o.Snapshot;
            Snapshot.GameOnly = sn.GameOnly;
            Snapshot.AutoSkipNoise = sn.AutoSkipNoise;
            Snapshot.IncludeNativeFields = sn.IncludeNativeFields;
            Snapshot.SelectedScope = sn.SelectedScope;
            Snapshot.SelectedFamily = sn.SelectedFamily;
            Snapshot.SelectedMaxDataset = sn.SelectedMaxDataset;
            Snapshot.ShowUsageBar = sn.ShowUsageBar;
            Snapshot.GroupDeep = sn.GroupDeep;
            Snapshot.SelectedRoundingMode = sn.RoundingMode;
        }
        if (Spc != null)
        {
            Spc.SelectedJoinMode = o.Spc.SelectedJoinMode;
            Spc.SelectedRoundingMode = o.Spc.RoundingMode;
        }
        if (Pivot != null)
        {
            Pivot.SelectedSource = o.Pivot.SelectedSource;
            Pivot.SelectedKeyMode = o.Pivot.SelectedKeyMode;
        }
        if (ProxyDeploy != null)
        {
            ProxyDeploy.SelectedProxyType = o.ProxyDeploy.SelectedProxyType;
            ProxyDeploy.ForceOverwrite = o.ProxyDeploy.ForceOverwrite;
        }
    }

    // NativeCScan's setter forces NewestFirst on/off, so apply it BEFORE NewestFirst
    // — otherwise the side effect would clobber the saved NewestFirst value.
    private void vs_Apply(ValueSearchUiOptions vs)
    {
        ValueSearch.SelectedDataType = vs.SelectedDataType;
        ValueSearch.SelectedScanType = vs.SelectedScanType;
        ValueSearch.GameOnly = vs.GameOnly;
        ValueSearch.MaxResults = vs.MaxResults;
        ValueSearch.ScanTimeoutSeconds = vs.ScanTimeoutSeconds;
        ValueSearch.ParallelScan = vs.ParallelScan;
        ValueSearch.BatchRead = vs.BatchRead;
        ValueSearch.DeepScan = vs.DeepScan;
        ValueSearch.CrossObjectScan = vs.CrossObjectScan;
        ValueSearch.NativeCScan = vs.NativeCScan;     // may flip NewestFirst (side effect)
        ValueSearch.NewestFirst = vs.NewestFirst;     // saved value wins (applied last)
        ValueSearch.PreFilterNoise = vs.PreFilterNoise;
        ValueSearch.SelectedRoundingMode = vs.RoundingMode;
        ValueSearch.CaseSensitive = vs.CaseSensitive;
    }

    /// <summary>Snapshot the current option values from every VM into a settings object.</summary>
    private UiOptionsSettings BuildOptions()
    {
        var o = new UiOptionsSettings();

        o.Main.SelectedAddressFormatIndex = SelectedAddressFormatIndex;
        o.Main.CollapsePointerNodes = CollapsePointerNodes;
        o.Main.ArrayLimitExponent = ArrayLimitExponent;
        o.Main.DropDownLimitExponent = DropDownLimitExponent;
        o.Main.CsxDrilldownDepth = CsxDrilldownDepth;
        o.Main.PreviewLimit = PreviewLimit;
        o.Main.DeepScanElemCapExponent = DeepScanElemCapExponent;

        o.LiveWalker.CollapseChain = LiveWalker.CollapseChain;
        o.LiveWalker.DescShowOffset = LiveWalker.DescShowOffset;
        o.LiveWalker.DescShowType = LiveWalker.DescShowType;
        o.LiveWalker.FlattenGasAttributes = LiveWalker.FlattenGasAttributes;
        o.LiveWalker.FlattenLeafStructs = LiveWalker.FlattenLeafStructs;
        o.LiveWalker.FlattenLeafRecords = LiveWalker.FlattenLeafRecords;
        o.LiveWalker.CollapseLeafPointers = LiveWalker.CollapseLeafPointers;
        o.LiveWalker.FlattenColorEnabled = LiveWalker.FlattenColorEnabled;
        o.LiveWalker.FlattenColorEven = LiveWalker.FlattenColorEven;
        o.LiveWalker.FlattenColorOdd = LiveWalker.FlattenColorOdd;
        o.LiveWalker.DedupSharedObjects = LiveWalker.DedupSharedObjects;
        o.LiveWalker.ExcludeSystemComponents = LiveWalker.ExcludeSystemComponents;
        o.LiveWalker.GWorldLocateDepth = LiveWalker.GWorldLocateDepth;
        o.LiveWalker.GWorldLocateDeep = LiveWalker.GWorldLocateDeep;
        o.LiveWalker.AutoRefreshIntervalSec = LiveWalker.AutoRefreshIntervalSec;

        o.ValueSearch.SelectedDataType = ValueSearch.SelectedDataType;
        o.ValueSearch.SelectedScanType = ValueSearch.SelectedScanType;
        o.ValueSearch.GameOnly = ValueSearch.GameOnly;
        o.ValueSearch.MaxResults = ValueSearch.MaxResults;
        o.ValueSearch.ScanTimeoutSeconds = ValueSearch.ScanTimeoutSeconds;
        o.ValueSearch.ParallelScan = ValueSearch.ParallelScan;
        o.ValueSearch.BatchRead = ValueSearch.BatchRead;
        o.ValueSearch.DeepScan = ValueSearch.DeepScan;
        o.ValueSearch.CrossObjectScan = ValueSearch.CrossObjectScan;
        o.ValueSearch.NativeCScan = ValueSearch.NativeCScan;
        o.ValueSearch.NewestFirst = ValueSearch.NewestFirst;
        o.ValueSearch.PreFilterNoise = ValueSearch.PreFilterNoise;
        o.ValueSearch.RoundingMode = ValueSearch.SelectedRoundingMode;
        o.ValueSearch.CaseSensitive = ValueSearch.CaseSensitive;

        o.InstanceFinder.ExactMatch = InstanceFinder.ExactMatch;
        o.InstanceFinder.NewestFirst = InstanceFinder.NewestFirst;
        o.InstanceFinder.InstanceSearchCap = InstanceFinder.InstanceSearchCap;
        o.InstanceFinder.DeepScanElemCap = InstanceFinder.DeepScanElemCap;

        o.PropertySearch.GameClassesOnly = PropertySearch.GameClassesOnly;
        o.PropertySearch.DeepSearch = PropertySearch.DeepSearch;

        o.Teleport.ZOffset = Teleport.ZOffset;
        o.Teleport.TraceChannel = Teleport.TraceChannel;
        o.Teleport.FallbackToCenter = Teleport.FallbackToCenter;
        o.Teleport.CursorHotkeyEnabled = Teleport.CursorHotkeyEnabled;
        o.Teleport.RelativeDistance = Teleport.RelativeDistance;
        o.Teleport.RelativeHorizontal = Teleport.RelativeHorizontal;
        o.Teleport.CoordSetRotation = Teleport.CoordSetRotation;
        o.Teleport.AutoRefresh = Teleport.AutoRefresh;

        o.InterestingFuncs.GameOnly = InterestingFunctions.GameOnly;
        o.InterestingFuncs.ShowAll = InterestingFunctions.ShowAll;

        o.InterestingProps.GameOnly = InterestingProperties.GameOnly;
        o.InterestingProps.UnusualOnly = InterestingProperties.UnusualOnly;
        o.InterestingProps.ShowAll = InterestingProperties.ShowAll;

        o.Console.GameOnly = Console.GameOnly;
        o.GameClassFilter.GameClassesOnly = GameClassFilter.GameClassesOnly;

        if (Snapshot != null)
        {
            o.Snapshot.GameOnly = Snapshot.GameOnly;
            o.Snapshot.AutoSkipNoise = Snapshot.AutoSkipNoise;
            o.Snapshot.IncludeNativeFields = Snapshot.IncludeNativeFields;
            o.Snapshot.SelectedScope = Snapshot.SelectedScope;
            o.Snapshot.SelectedFamily = Snapshot.SelectedFamily;
            o.Snapshot.SelectedMaxDataset = Snapshot.SelectedMaxDataset;
            o.Snapshot.ShowUsageBar = Snapshot.ShowUsageBar;
            o.Snapshot.GroupDeep = Snapshot.GroupDeep;
            o.Snapshot.RoundingMode = Snapshot.SelectedRoundingMode;
        }
        if (Spc != null)
        {
            o.Spc.SelectedJoinMode = Spc.SelectedJoinMode;
            o.Spc.RoundingMode = Spc.SelectedRoundingMode;
        }
        if (Pivot != null)
        {
            o.Pivot.SelectedSource = Pivot.SelectedSource;
            o.Pivot.SelectedKeyMode = Pivot.SelectedKeyMode;
        }
        if (ProxyDeploy != null)
        {
            o.ProxyDeploy.SelectedProxyType = ProxyDeploy.SelectedProxyType;
            o.ProxyDeploy.ForceOverwrite = ProxyDeploy.ForceOverwrite;
        }

        return o;
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
        // Load this game's persisted bookmarks (SetEngineState above captured the PE
        // hash). Self-clears in-memory first, so it's safe on both connect and a
        // game-change re-scan. Synchronous (tiny file).
        LiveWalker.LoadBookmarksForGame(state.PeHash);
        InstanceFinder.SetEngineState(state);
        ValueSearch.SetEngineState(state);
        InterestingFunctions.IsGWorldAvailable = state.HasGWorld;
        InterestingProperties.IsGWorldAvailable = state.HasGWorld;
        Teleport.SetConnected(true);   // refresh markers once the DLL is scanned
        Snapshot?.SetEngineState(state);
        Spc?.SetEngineState(state);
        Pivot?.SetEngineState(state);

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

                if (!status.Running)
                {
                    if (status.Phase >= 7 && status.EngineState != null)
                    {
                        _engineState = status.EngineState;
                        NeedsScan = false;
                        IsScanning = false;
                        ApplyEngineState(status.EngineState);
                        return;
                    }
                    // The scan stopped without reaching a complete engine state —
                    // surface a failure instead of polling scan_status forever.
                    IsScanning = false;
                    StatusText = "Scan did not complete";
                    SetError(new InvalidOperationException(
                        $"Scan ended at phase {status.Phase} without an engine state"));
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
    /// Shared CT save handler — opens the platform save-file dialog with
    /// the VM-supplied default filename + .CT filter, writes the XML
    /// payload (UTF-8, no BOM — CE's loader handles either but the
    /// existing UE5CEDumper.CT ships without BOM so we stay consistent),
    /// and surfaces success/error in the top status bar.
    ///
    /// <paramref name="source"/> is a short label for the log entry
    /// (e.g. "InterestingProperties" / "InterestingFunctions") so a
    /// later grep through the user's logs can identify which tab
    /// generated which file.
    /// </summary>
    private async Task SaveCheatTableAsync(
        string defaultFileName, string ctXml, string source)
    {
        try
        {
            var savePath = await _platform.ShowSaveFileDialogAsync(
                defaultFileName: defaultFileName,
                filterName:      "Cheat Engine Table (*.CT)",
                filterExtension: ".CT");
            if (string.IsNullOrEmpty(savePath))
            {
                _log.Info($"Save Cheat Table ({source}): user cancelled");
                return;
            }
            // Avalonia's open-file dialog returns the chosen filter's
            // extension as a hint; some platforms append it twice if
            // the user typed an extension. Strip any duplicate.
            await File.WriteAllTextAsync(savePath, ctXml,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _log.Info($"Saved Cheat Table ({source}): {savePath} " +
                      $"({ctXml.Length:N0} chars)");
            StatusText = $"Saved: {Path.GetFileName(savePath)}";
        }
        catch (Exception ex)
        {
            _log.Error($"Save Cheat Table ({source}) failed", ex);
            StatusText = $"Save Cheat Table failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Top-toolbar "&#x27F3;" button: re-probe whether Cheat Engine is running with
    /// the AOBMaker plugin loaded and update the always-visible status chip. Navigation
    /// and Add-to-CE actions already re-probe on use, so this is purely for at-a-glance
    /// feedback. Propagates the result to the Live Walker and Pointers panels so all
    /// three indicators (and their button enablement) agree.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAobMakerAsync()
    {
        if (_aobMaker == null)
        {
            IsAobMakerAvailable = false;
            return;
        }

        try
        {
            var ok = await _aobMaker.CheckAvailabilityAsync();
            IsAobMakerAvailable = ok;
            LiveWalker.IsAobMakerAvailable = ok;
            Pointers.IsAobMakerAvailable = ok;
            StatusText = ok
                ? "AOBMaker plugin connected"
                : "AOBMaker plugin not detected — open Cheat Engine with the AOBMaker plugin loaded";
        }
        catch (Exception ex)
        {
            IsAobMakerAvailable = false;
            _log.Error("Refresh AOBMaker status failed", ex);
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

    /// <summary>
    /// Tools menu: stream the embedded <c>ue5_freeze_helper.lua</c> to a
    /// user-chosen file. Manual-fallback companion to
    /// <see cref="InjectFreezeHelperLuaAsync"/> for cases where AOBMaker
    /// isn't installed.
    /// </summary>
    [RelayCommand]
    private async Task ExportFreezeHelperLuaAsync()
    {
        try
        {
            var savePath = await _platform.ShowSaveFileDialogAsync(
                defaultFileName:  FreezeHelperLuaResource.DefaultFileName,
                filterName:       "CE Lua Freeze Helper (*.lua)",
                filterExtension:  ".lua");
            if (string.IsNullOrEmpty(savePath))
            {
                _log.Info("Export Freeze Helper Lua: user cancelled");
                return;
            }

            var content = FreezeHelperLuaResource.Read();
            await File.WriteAllTextAsync(savePath, content);

            _log.Info($"Exported freeze helper lua: {savePath} " +
                      $"({content.Length:N0} chars)");
            StatusText = $"Freeze helper exported: {Path.GetFileName(savePath)}";
        }
        catch (Exception ex)
        {
            _log.Error("Export Freeze Helper Lua failed", ex);
            StatusText = $"Export freeze helper failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Tools menu: ship the embedded <c>ue5_freeze_helper.lua</c> straight
    /// into the currently open CE table via the AOBMaker plugin
    /// (<c>InjectTableFile</c>). Sister to <see cref="InjectCeHelperLuaAsync"/>;
    /// the two helpers coexist in one .CT.
    /// </summary>
    [RelayCommand]
    private async Task InjectFreezeHelperLuaAsync()
    {
        if (_aobMaker == null)
        {
            StatusText = "AOBMaker plugin not configured";
            return;
        }

        StatusText = $"Injecting {FreezeHelperLuaResource.DefaultFileName} into CE table...";

        try
        {
            await _aobMaker.CheckAvailabilityAsync();
            if (!_aobMaker.IsAvailable)
            {
                StatusText = "Inject freeze helper: AOBMaker not connected — open Cheat Engine with the AOBMaker plugin loaded";
                return;
            }

            var content = FreezeHelperLuaResource.Read();
            var (ok, error) = await _aobMaker.InjectTableFileAsync(
                FreezeHelperLuaResource.DefaultFileName, content);

            if (ok)
            {
                _log.Info($"Injected {FreezeHelperLuaResource.DefaultFileName} into CE table " +
                          $"({content.Length:N0} chars)");
                StatusText = $"Inject freeze helper OK: {FreezeHelperLuaResource.DefaultFileName} embedded ({content.Length:N0} bytes)";
            }
            else if (!string.IsNullOrEmpty(error))
            {
                StatusText = $"Inject freeze helper failed: {error} — use Export to disk + Add File... fallback";
            }
            else
            {
                StatusText = "Inject freeze helper failed (no plugin response — CE closed?) — use Export to disk + Add File... fallback";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Inject Freeze Helper Lua failed", ex);
            StatusText = $"Inject freeze helper failed: {ex.Message}";
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

    /// <summary>
    /// Stream a full classes-and-properties dump to a JSON-Lines file
    /// for offline analysis. Used to feed the
    /// <c>scripts/analysis/analyze_dumps.py</c> aggregator that derives
    /// keyword tables / class bonuses from real-game data instead of
    /// hand-curated guesses.
    /// </summary>
    [RelayCommand]
    private async Task ExportDumpAllAsync()
    {
        if (_engineState == null) return;

        try
        {
            ClearError();
            var moduleName = _engineState.ModuleName;
            if (string.IsNullOrEmpty(moduleName)) moduleName = "game";
            var safeModule = Path.GetFileNameWithoutExtension(moduleName);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var filePath = await _platform.ShowSaveFileDialogAsync(
                $"{safeModule}-dump-{stamp}", "Dump JSON Lines (*.jsonl)", ".jsonl");
            if (string.IsNullOrEmpty(filePath)) return;

            StatusText = "Dumping classes...";
            var progress = new Progress<DumpProgress>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Total > 0
                        ? $"{p.Phase} ({p.Done}/{p.Total})"
                        : $"{p.Phase} ({p.Done})";
                }));

            var options = new DumpOptions(
                GameOnly: false,                           // Capture engine too; analysis can filter
                IncludeFunctions: true,
                IncludeInstanceCounts: true,
                DumperBuildNumber: GetBuildNumber(),
                DumperCommit: null);                        // Not yet plumbed through

            await using var fs = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
            await DumpAllService.GenerateAsync(_dump, _engineState, fs, options, progress);

            var fileInfo = new FileInfo(filePath);
            StatusText = $"Dumped {fileInfo.Length / 1024 / 1024:F1} MB to {Path.GetFileName(filePath)}";
            _log.Info($"DumpAll exported to {filePath} ({fileInfo.Length} bytes)");
        }
        catch (Exception ex)
        {
            StatusText = "Dump failed";
            SetError(ex);
            _log.Error("DumpAll export failed", ex);
        }
    }

    /// <summary>
    /// Same EntryAssembly + Version.Revision trick the System tab uses
    /// (see PointerPanelViewModel.ReadUiBuildNumber for the two-trap
    /// rationale).
    /// </summary>
    private static int GetBuildNumber()
    {
        var rev = Assembly.GetEntryAssembly()?.GetName().Version?.Revision ?? 0;
        return rev > 0 ? rev : 0;
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
