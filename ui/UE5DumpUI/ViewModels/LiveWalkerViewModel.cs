using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Live Data Walker panel.
/// Browse GWorld hierarchy and navigate into any UObject by clicking pointers.
/// </summary>
public partial class LiveWalkerViewModel : ViewModelBase, IDisposable
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IPlatformService _platform;
    private readonly IAobMakerBridge? _aobMaker;
    private bool _disposed;

    // Cached GWorld walk result for back-navigation
    private WorldWalkResult? _cachedWorld;

    // Engine state for CE address formatting
    private EngineState? _engineState;

    // Navigation breadcrumb stack
    [ObservableProperty] private ObservableCollection<BreadcrumbItem> _breadcrumbs = new();
    [ObservableProperty] private ObservableCollection<LiveFieldValue> _fields = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _currentObjectName = "";
    [ObservableProperty] private string _currentClassName = "";
    [ObservableProperty] private string _currentAddress = "";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private LiveFieldValue? _selectedField;

    // Multi-selection snapshot. Updated by LiveWalkerPanel's SelectionChanged
    // handler whenever the DataGrid's SelectedItems changes. Drives Copy CE
    // Field(s) export — everything else (drill-down, copy buttons, edit) acts
    // on the row whose own button was clicked, so multi-select doesn't affect
    // those flows. SelectedField is still the focus anchor for search /
    // bookmark / scroll-to logic.
    private readonly List<LiveFieldValue> _selectedFieldsSnapshot = new();
    [ObservableProperty] private int _selectedFieldsCount;

    public bool HasSelectedFields => SelectedFieldsCount > 0;

    public string ExportCeFieldButtonLabel => SelectedFieldsCount > 1
        ? $"Copy CE Fields ({SelectedFieldsCount})"
        : "Copy CE Field";

    partial void OnSelectedFieldsCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedFields));
        OnPropertyChanged(nameof(ExportCeFieldButtonLabel));
    }

    /// <summary>
    /// Sync the multi-selection snapshot from the DataGrid's SelectedItems
    /// collection. Called by LiveWalkerPanel.FieldGrid_SelectionChanged.
    /// Filters out non-LiveFieldValue entries defensively (Avalonia's
    /// SelectedItems is typed as IList).
    /// </summary>
    public void UpdateSelectedFields(System.Collections.IEnumerable? selectedItems)
    {
        _selectedFieldsSnapshot.Clear();
        if (selectedItems != null)
        {
            foreach (var item in selectedItems)
            {
                if (item is LiveFieldValue f) _selectedFieldsSnapshot.Add(f);
            }
        }
        SelectedFieldsCount = _selectedFieldsSnapshot.Count;
    }

    [ObservableProperty] private string _currentOuterAddr = "";
    [ObservableProperty] private string _currentOuterName = "";
    [ObservableProperty] private string _currentOuterClassName = "";
    [ObservableProperty] private bool _hasParent;
    // UFunction display. _allFunctions holds the unfiltered set received
    // from the DLL; Functions is the user-visible filtered subset rebuilt
    // by ApplyFunctionFilter() whenever the filter text changes. Function
    // counts on UE-derived classes can climb past 200 entries (Character /
    // PlayerController inheritance chains), so the filter is a usability
    // floor — not a perf optimization.
    private readonly List<FunctionInfoModel> _allFunctions = new();
    private Task? _pendingFunctionsLoad;
    [ObservableProperty] private ObservableCollection<FunctionInfoModel> _functions = new();
    [ObservableProperty] private bool _hasFunctions;
    [ObservableProperty] private FunctionInfoModel? _selectedFunction;
    [ObservableProperty] private string _functionFilter = "";

    /// <summary>
    /// Two-way binding for the Functions Expander. Defaults to collapsed
    /// because most navigation in LiveWalker is field-focused; cross-tab
    /// jumps from Interesting Funcs flip this to true via
    /// <see cref="TrySelectFunctionByName"/> so the user lands with the
    /// target function already visible.
    /// </summary>
    [ObservableProperty] private bool _isFunctionsExpanded;

    partial void OnFunctionFilterChanged(string value) => ApplyFunctionFilter();

    [RelayCommand]
    private void ClearFunctionFilter() => FunctionFilter = "";
    private string _currentClassAddr = "";
    private bool _isDefinitionView;  // True when displaying a class/struct definition (no live data)
    private DataTableWalkResult? _cachedDataTableRows;  // Cached DataTable row data

    // CE XML output (kept for possible future use but no longer shown in panel)
    [ObservableProperty] private string _ceXmlOutput = "";
    [ObservableProperty] private bool _showCeXml;

    // Address format
    [ObservableProperty] private int _selectedAddressFormatIndex;
    private AddressFormat AddrFormat => (AddressFormat)SelectedAddressFormatIndex;

    /// <summary>Whether CE XML export should collapse pointer/array nodes.</summary>
    public bool CollapsePointerNodes { get; set; }

    /// <summary>
    /// Whether Copy CE XML / Copy CE Field should collapse the GWorld-&gt;...-&gt;target
    /// pointer spine into a single CE multi-level-pointer entry (base + one folded
    /// node + the target field with its drill-down). LiveWalker-local toggle —
    /// affects only the two clipboard exports, not CSX / .h / AA Script.
    /// </summary>
    [ObservableProperty] private bool _collapseChain;

    /// <summary>Max array element count for inline reading (2^N, default 64).</summary>
    private int _arrayLimit = 64;
    public int ArrayLimit
    {
        get => _arrayLimit;
        set
        {
            if (_arrayLimit == value) return;
            _arrayLimit = value;
            // Auto-refresh current view with new limit
            if (!string.IsNullOrEmpty(CurrentAddress))
                RefreshCommand.Execute(null);
        }
    }

    /// <summary>Max struct sub-fields to show in preview (0 = none, default 2, max 6).</summary>
    private int _previewLimit = 2;
    public int PreviewLimit
    {
        get => _previewLimit;
        set
        {
            if (_previewLimit == value) return;
            _previewLimit = value;
            // Auto-refresh current view with new limit
            if (!string.IsNullOrEmpty(CurrentAddress))
                RefreshCommand.Execute(null);
        }
    }

    /// <summary>Max CE DropDownList entries (2^N, default 512). Used during CE XML export.</summary>
    public int DropDownLimit { get; set; } = 512;

    /// <summary>CSX drilldown depth (0 = flat/dummy, 1-4 normal, 5-6 deep / warning band).
    /// Each extra level can multiply CE XML / CSX output exponentially because every
    /// ObjectProperty hit fans out to its own field tree. 4 was the historic ceiling
    /// after the cycle-elision fix in build 552 hit a 2GB StringBuilder OOM at depth 2
    /// on UWorld back-edges; raising to 6 is safe with the current cycle guard, but
    /// the slider colour shifts to amber/red at 5-6 to flag the size impact.</summary>
    [ObservableProperty] private int _csxDrilldownDepth;

    // === Locate in GWorld (forward BFS path search) ===
    // User-set search depth (how many pointer hops down from GWorld to look),
    // and live GWorld availability (drives gray-out of the feature).
    [ObservableProperty] private int _gWorldLocateDepth = 5;
    [ObservableProperty] private bool _isGWorldAvailable;

    /// <summary>Foreground brush for the depth display — default at 0-3, then
    /// warms from yellow (4) through orange to deep red (8) as the export
    /// cost grows. Max is 8.</summary>
    public Avalonia.Media.IBrush CsxDrilldownDepthBrush => CsxDrilldownDepth switch
    {
        >= 8 => Avalonia.Media.SolidColorBrush.Parse("#E02828"),  // deep red — very large output
        7    => Avalonia.Media.SolidColorBrush.Parse("#E04A2C"),  // red-orange
        6    => Avalonia.Media.SolidColorBrush.Parse("#E0702C"),  // orange
        5    => Avalonia.Media.SolidColorBrush.Parse("#E69A17"),  // amber
        4    => Avalonia.Media.SolidColorBrush.Parse("#E6C217"),  // yellow — first warning band
        _    => Avalonia.Media.SolidColorBrush.Parse("#D4D4D4"),  // default 0-3
    };

    partial void OnCsxDrilldownDepthChanged(int value)
        => OnPropertyChanged(nameof(CsxDrilldownDepthBrush));

    // Search
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _searchMatchCount;
    [ObservableProperty] private bool _hasSearchResults;

    // AOBMaker CE Plugin integration
    [ObservableProperty] private bool _isAobMakerAvailable;

    /// <summary>
    /// Per-row hint shown in the Functions DataGrid Notes column. Same
    /// value across every row in the current grid; the column is per-row
    /// in AXAML but the data is VM-level. When AOBMaker is unavailable
    /// the AA(B) shortcut still works (clipboard fallback) but the
    /// in-CE workflow is degraded; this surfaces that to the user without
    /// requiring them to hover for a tooltip.
    /// </summary>
    public string AobMakerNote => IsAobMakerAvailable
        ? ""
        : "AOBMaker plugin not found — AA Script export will fall back to clipboard";

    partial void OnIsAobMakerAvailableChanged(bool value)
        => OnPropertyChanged(nameof(AobMakerNote));

    // AOB Symbol toggle for CE XML export
    [ObservableProperty] private bool _useAobSymbol;
    [ObservableProperty] private bool _isAobSymbolAvailable;

    // Guess What toggle: fill gaps between known fields with heuristic guesses
    [ObservableProperty] private bool _fillGaps;

    // AOBMaker CE Plugin detection cooldown (avoids spamming pipe connect on rapid navigation)
    private DateTime _lastAobMakerCheck = DateTime.MinValue;
    private static readonly TimeSpan AobMakerCheckCooldown = TimeSpan.FromSeconds(5);

    // Auto-refresh
    [ObservableProperty] private bool _isAutoRefreshing;
    [ObservableProperty] private int _autoRefreshIntervalSec = Constants.DefaultAutoRefreshIntervalSec;
    [ObservableProperty] private int _autoRefreshMinSec = Constants.MinAutoRefreshIntervalSec;
    [ObservableProperty] private string _autoRefreshStatusText = "sec";
    private DispatcherTimer? _autoRefreshTimer;
    private DispatcherTimer? _countdownTimer;
    private int _countdownRemaining;
    private bool _isAutoRefreshBenchmarked;
    private bool _isAutoRefreshing_InProgress; // Guard against overlapping refreshes
    private bool _isEditing; // True while a cell is being edited (suppresses auto-refresh)

    // Bookmark slots (4 fixed slots)
    [ObservableProperty] private ObservableCollection<BookmarkSlot> _bookmarkSlots = new();
    [ObservableProperty] private bool _isBookmarkSaveMode;  // True while waiting for user to pick a slot

    // Pre-bookmark navigation state (for Back-after-bookmark)
    private List<BreadcrumbItem>? _preBookmarkBreadcrumbs;
    private string _preBookmarkAddress = "";
    private WorldWalkResult? _preBookmarkCachedWorld;

    /// <summary>
    /// Raised when the View should scroll the DataGrid to a specific field name.
    /// The View subscribes to this and calls ScrollIntoView on the DataGrid.
    /// </summary>
    public event Action<string>? ScrollToFieldRequested;

    /// <summary>
    /// Raised when the View should scroll the DataGrid to the first search match.
    /// </summary>
    public event Action? ScrollToFirstSearchMatch;

    /// <summary>
    /// Raised when the View should scroll to (and the selection already points
    /// at) a specific field row. Carries the exact object so match navigation
    /// lands on the right row even when field names repeat (container elements).
    /// </summary>
    public event Action<LiveFieldValue>? ScrollFieldIntoView;

    /// <summary>
    /// Raised when the View should scroll the FunctionGrid to a specific
    /// UFunction by name. Used by cross-tab navigation from Interesting
    /// Funcs so the user lands on the correct row even when the function
    /// list scrolls past the visible area.
    /// </summary>
    public event Action<string>? ScrollToFunctionRequested;
    private string _lastScrolledSearchText = "";

    /// <summary>Raised to pivot the selected field's owning class in the
    /// experimental Class Pivot tab (className, fieldName). C5 right-click handoff.</summary>
    public event Action<string, string>? NavigateToPivot;

    /// <summary>
    /// Raised synchronously while saving a bookmark so the View can report the
    /// DataGrid's topmost visible row (written into the carrier) for scroll restore.
    /// </summary>
    public event Action<ViewAnchorRef>? CaptureViewAnchor;

    /// <summary>
    /// Raised after a bookmark finishes loading: the View re-selects the saved
    /// field rows (matched by name + offset) and scrolls the saved anchor row back
    /// into view, so the bookmark returns to what the user was looking at.
    /// </summary>
    public event Action<IReadOnlyList<BookmarkFieldRef>, BookmarkFieldRef?>? RestoreBookmarkView;

    /// <summary>Raised to show the currently-walked object's related objects
    /// (components, GAS ASC → AttributeSets, Controller↔Pawn) in the Related
    /// tab. Payload = current object address.</summary>
    public event Action<string>? NavigateToRelatedObjects;

    /// <summary>Gates the "Pivot this property" context-menu item — true only when
    /// the experimental Class Pivot tab is available (mirrors the gate).</summary>
    [ObservableProperty] private bool _pivotEnabled;

    /// <summary>Per-field action: pivot the current class on the selected field in
    /// the Class Pivot tab. Inert for synthetic container views (Array/Map/Set/
    /// DataTable labels) — the handoff just reports the class isn't in a snapshot.</summary>
    [RelayCommand]
    private void PivotThis(LiveFieldValue? field)
    {
        field ??= SelectedField;
        if (field == null || string.IsNullOrEmpty(CurrentClassName) || string.IsNullOrEmpty(field.Name))
            return;
        NavigateToPivot?.Invoke(CurrentClassName, field.Name);
    }

    public LiveWalkerViewModel(IDumpService dump, ILoggingService log, IPlatformService platform,
                               IAobMakerBridge? aobMaker = null)
    {
        _dump = dump;
        _log = log;
        _platform = platform;
        _aobMaker = aobMaker;

        // Initialize 4 empty bookmark slots
        for (int i = 0; i < 4; i++)
            BookmarkSlots.Add(new BookmarkSlot { SlotIndex = i });
    }

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        IsAobSymbolAvailable = !string.IsNullOrEmpty(state?.GWorldAob);
        IsGWorldAvailable = state?.HasGWorld ?? false;
    }

    partial void OnIsAobSymbolAvailableChanged(bool value)
    {
        if (!value)
            UseAobSymbol = false;
    }

    partial void OnFillGapsChanged(bool value)
    {
        // Toggle triggers refresh to rebuild field list with/without guessed fields
        if (!string.IsNullOrEmpty(CurrentAddress))
            RefreshCommand.Execute(null);
    }

    /// <summary>Clear both error message and status text (e.g., container limit warnings).</summary>
    private void ClearStatus()
    {
        ClearError();
        StatusText = "";
    }

    [RelayCommand]
    private async Task StartFromWorldAsync()
    {
        try
        {
            ClearStatus();
            IsLoading = true;
            StopAutoRefreshTimer();
            _preBookmarkBreadcrumbs = null;
            IsBookmarkSaveMode = false;

            var world = await _dump.WalkWorldAsync(500, arrayLimit: ArrayLimit);
            _cachedWorld = world;

            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Address = world.WorldAddr,
                Label = "GWorld",
                IsPointerDeref = true,
                FieldOffset = 0,
                FieldName = "GWorld",
            });

            PopulateFromWorld(world);

            // Show DLL-side error if world walk was partial (e.g. PersistentLevel not found)
            if (!string.IsNullOrEmpty(world.Error))
            {
                SetError(new InvalidOperationException(world.Error));
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Failed to load GWorld", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// True when <paramref name="crumb"/> is the synthetic GWorld actor-list root
    /// (the "Start from GWorld" view). Only this crumb may be re-displayed via
    /// <see cref="PopulateFromWorld"/>. A deeper breadcrumb such as OwningWorld can
    /// resolve to the very same UWorld address, but it was reached through a normal
    /// pointer field and must be walked as an instance — otherwise navigating or
    /// restoring a bookmark to it swaps the saved object for the GWorld actor list
    /// (headed by the world name, e.g. "PLV_game"). The FieldName=="GWorld" marker
    /// is unique to the synthetic root (no UObject field is named "GWorld"); this
    /// mirrors the Breadcrumbs.Count==1 guard the auto-refresh path already uses.
    /// </summary>
    private bool IsGWorldActorListRoot(BreadcrumbItem? crumb) =>
        crumb != null
        && crumb.FieldName == "GWorld"
        && _cachedWorld != null
        && crumb.Address == _cachedWorld.WorldAddr;

    private void PopulateFromWorld(WorldWalkResult world)
    {
        CurrentObjectName = world.WorldName;
        CurrentClassName = "UWorld";
        CurrentAddress = world.WorldAddr;
        HasData = true;
        ShowCeXml = false;
        HasParent = false;
        _isDefinitionView = false;
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";

        Fields.Clear();

        // Compute base address for FieldAddress display
        ulong worldBase = 0;
        try
        {
            if (!string.IsNullOrEmpty(world.WorldAddr))
                worldBase = Convert.ToUInt64(world.WorldAddr.Replace("0x", "").Replace("0X", ""), 16);
        }
        catch { /* ignore parse failures */ }

        // PersistentLevel as first navigable entry (offset from DLL walk_world response)
        if (!string.IsNullOrEmpty(world.LevelAddr) && world.LevelAddr != "0x0")
        {
            var pLevel = new LiveFieldValue
            {
                Name = world.LevelName ?? "PersistentLevel",
                TypeName = "ObjectProperty",
                Offset = world.LevelOffset,
                Size = 8,
                PtrAddress = world.LevelAddr,
                PtrName = world.LevelName ?? "PersistentLevel",
                PtrClassName = "ULevel",
            };
            if (worldBase != 0)
                pLevel.FieldAddress = $"0x{worldBase + (ulong)world.LevelOffset:X}";
            Fields.Add(pLevel);
        }

        // Each actor as a navigable entry
        foreach (var actor in world.Actors)
        {
            Fields.Add(new LiveFieldValue
            {
                Name = actor.Name,
                TypeName = "ObjectProperty",
                Offset = 0,
                Size = 8,
                PtrAddress = actor.Address,
                PtrName = actor.Name,
                PtrClassName = actor.ClassName,
            });

            // Components as indented sub-entries
            foreach (var comp in actor.Components)
            {
                Fields.Add(new LiveFieldValue
                {
                    Name = $"  {actor.Name}.{comp.Name}",
                    TypeName = "ObjectProperty",
                    Offset = 0,
                    Size = 8,
                    PtrAddress = comp.Address,
                    PtrName = comp.Name,
                    PtrClassName = comp.ClassName,
                });
            }
        }
    }

    /// <summary>
    /// When <paramref name="parent"/> is a Map container view, the per-element
    /// value offset (aligned value offset, falling back to key size) — exactly
    /// the offset <see cref="PopulateMapContainerFields"/> uses to place each
    /// element's value inside the TPair. A drilled Map value's raw field offset
    /// is only the element-base offset (index*stride); adding this lands the
    /// CE/CSX pointer chain on the value rather than valueOffset bytes short.
    /// Returns 0 for any non-map parent (direct struct fields and struct/set
    /// array elements have value == element base), so it is a safe additive
    /// correction to a drilled breadcrumb's FieldOffset.
    /// </summary>
    internal static int MapValueDrillOffset(BreadcrumbItem? parent)
    {
        if (parent is { IsContainerView: true, ContainerField: { } cf }
            && cf.MapCount > 0 && !string.IsNullOrEmpty(cf.MapKeyType))
        {
            return cf.MapValueOffset > 0 ? cf.MapValueOffset : cf.MapKeySize;
        }
        return 0;
    }

    [RelayCommand]
    private async Task NavigateToFieldAsync(LiveFieldValue? field)
    {
        if (field == null || !field.IsNavigable) return;

        // Re-check AOBMaker CE Plugin availability (detects CE start/close, cooldown-throttled)
        TryCheckAobMaker();

        try
        {
            ClearStatus();
            IsLoading = true;

            // Save the clicked field name on the current breadcrumb for scroll restoration on Back
            if (Breadcrumbs.Count > 0)
                Breadcrumbs[^1].ScrollHintFieldName = field.Name;

            // CE/CSX chain offsets are relative to the parent's RESOLVED address.
            // When drilling a Map element's VALUE, the parent (the Map container
            // view) resolves to the element-storage base, but the value sits at
            // +valueOffset inside each element (the key is at the front of the
            // TPair). field.Offset is only the element-base offset (index*stride),
            // so the breadcrumb must carry the FULL offset to the value or every
            // child lands valueOffset bytes short (the off-by-8 on FName-keyed
            // maps). Zero for non-map parents, so other navigation is unchanged.
            int navOffset = field.Offset + MapValueDrillOffset(
                Breadcrumbs.Count > 0 ? Breadcrumbs[^1] : null);

            if (!string.IsNullOrEmpty(field.PtrAddress) && field.PtrAddress != "0x0")
            {
                // ObjectProperty navigation (pointer dereference)
                await NavigateToAsync(field.PtrAddress, field.Name, navOffset, field.Name, isPointer: true);
            }
            else if (!string.IsNullOrEmpty(field.StructDataAddr) && field.StructDataAddr != "0x0")
            {
                // StructProperty navigation: walk struct data using its class
                var result = await _dump.WalkInstanceAsync(field.StructDataAddr, field.StructClassAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
                result = await AutoFillGapsRetryAsync(result, field.StructDataAddr, field.StructClassAddr);
                var displayName = !string.IsNullOrEmpty(field.StructTypeName)
                    ? $"{field.Name} ({field.StructTypeName})"
                    : field.Name;

                // DataTable row navigation: the uint8* is a pointer that needs dereference,
                // not an inline struct. Set IsPointerDeref=true for correct CE XML pointer chain.
                var isDataTableRow = Breadcrumbs.Count > 0 && Breadcrumbs[^1].IsDataTableView;

                Breadcrumbs.Add(new BreadcrumbItem
                {
                    Address = field.StructDataAddr,
                    Label = displayName,
                    ClassAddr = field.StructClassAddr,
                    FieldOffset = navOffset,
                    FieldName = field.Name,
                    IsPointerDeref = isDataTableRow,
                });
                _log.Info($"NAV→Struct {field.Name} addr={field.StructDataAddr} off=0x{navOffset:X} dtRow={isDataTableRow} | BC={FormatBreadcrumbTrace()}");
                UpdateDisplay(result);
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to navigate to {field.Name}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NavigateToContainerAsync(LiveFieldValue? field)
    {
        if (field == null || !field.IsContainerNavigable) return;

        try
        {
            ClearStatus();
            IsLoading = true;

            // Save scroll hint on current breadcrumb
            if (Breadcrumbs.Count > 0)
                Breadcrumbs[^1].ScrollHintFieldName = field.Name;

            if (field.DataTableRowCount > 0 && _cachedDataTableRows != null)
            {
                NavigateToDataTableContainer(field, _cachedDataTableRows);
            }
            else if (field.ArrayCount > 0 && !string.IsNullOrEmpty(field.ArrayInnerType))
            {
                await NavigateToArrayContainerAsync(field);
            }
            else if (field.MapCount > 0 && !string.IsNullOrEmpty(field.MapKeyType))
            {
                NavigateToMapContainer(field);
            }
            else if (field.SetCount > 0 && !string.IsNullOrEmpty(field.SetElemType))
            {
                NavigateToSetContainer(field);
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to navigate to container {field.Name}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task NavigateToArrayContainerAsync(LiveFieldValue field)
    {
        var typeLabel = !string.IsNullOrEmpty(field.ArrayStructType)
            ? field.ArrayStructType : field.ArrayInnerType;
        var label = $"{field.Name} [{field.ArrayCount} x {typeLabel}]";

        // Fetch elements BEFORE adding breadcrumb — if the DLL call fails,
        // we must not leave a stale breadcrumb that causes repeated entries.
        var parentAddr = CurrentAddress;
        List<ArrayElementValue> elements;
        if (field.ArrayElements != null && field.ArrayElements.Count >= field.ArrayCount)
        {
            // All elements already inline (complete set)
            elements = field.ArrayElements;
        }
        else if (field.ArrayElements is { Count: > 0 } && IsPointerOrStructArrayType(field.ArrayInnerType))
        {
            // Pointer/struct arrays: use inline elements (Phase D/E/F resolved names).
            // read_array_elements is scalar-only and cannot resolve pointer names.
            elements = field.ArrayElements;
        }
        else if (!string.IsNullOrEmpty(field.ArrayInnerAddr) && !string.IsNullOrEmpty(parentAddr))
        {
            // Scalar arrays: fetch full element list from DLL (Phase B)
            var result = await _dump.ReadArrayElementsAsync(
                parentAddr, field.Offset, field.ArrayInnerAddr,
                field.ArrayInnerType, field.ArrayElemSize, 0, field.ArrayCount);
            elements = result.Elements;
        }
        else
        {
            elements = field.ArrayElements ?? new();
        }

        // Only add breadcrumb after successful element retrieval
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = parentAddr,
            Label = label,
            FieldOffset = field.Offset,
            FieldName = field.Name,
            IsPointerDeref = false,
            IsContainerView = true,
            ContainerField = field,
        });
        _log.Info($"NAV→Container {field.Name} addr={parentAddr} off=0x{field.Offset:X} | BC={FormatBreadcrumbTrace()}");

        PopulateArrayContainerFields(elements, field);
    }

    private void NavigateToMapContainer(LiveFieldValue field)
    {
        var keyLabel = !string.IsNullOrEmpty(field.MapKeyType) ? field.MapKeyType : "?";
        var valLabel = !string.IsNullOrEmpty(field.MapValueType) ? field.MapValueType : "?";
        var label = $"{field.Name} {{Map: {field.MapCount}, {keyLabel} \u2192 {valLabel}}}";

        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = CurrentAddress,
            Label = label,
            FieldOffset = field.Offset,
            FieldName = field.Name,
            IsPointerDeref = false,
            IsContainerView = true,
            ContainerField = field,
        });
        _log.Info($"NAV→MapContainer {field.Name} addr={CurrentAddress} off=0x{field.Offset:X} | BC={FormatBreadcrumbTrace()}");

        PopulateMapContainerFields(field.MapElements ?? new(), field);
    }

    private void NavigateToSetContainer(LiveFieldValue field)
    {
        var elemLabel = !string.IsNullOrEmpty(field.SetElemType) ? field.SetElemType : "?";
        var label = $"{field.Name} {{Set: {field.SetCount}, {elemLabel}}}";

        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = CurrentAddress,
            Label = label,
            FieldOffset = field.Offset,
            FieldName = field.Name,
            IsPointerDeref = false,
            IsContainerView = true,
            ContainerField = field,
        });
        _log.Info($"NAV→SetContainer {field.Name} addr={CurrentAddress} off=0x{field.Offset:X} | BC={FormatBreadcrumbTrace()}");

        PopulateSetContainerFields(field.SetElements ?? new(), field);
    }

    private void NavigateToDataTableContainer(LiveFieldValue field, DataTableWalkResult dtResult)
    {
        var label = $"RowMap [{dtResult.RowCount} x {dtResult.RowStructName}]";

        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = CurrentAddress,
            Label = label,
            FieldOffset = dtResult.RowMapOffset,
            FieldName = field.Name,
            IsPointerDeref = false,
            IsContainerView = true,
            IsDataTableView = true,
            ContainerField = field,
            DataTableData = dtResult,
        });
        _log.Info($"NAV\u2192DataTable {field.Name} addr={CurrentAddress} rows={dtResult.RowCount} struct={dtResult.RowStructName} | BC={FormatBreadcrumbTrace()}");

        PopulateDataTableRowFields(dtResult);
    }

    private void PopulateDataTableRowFields(DataTableWalkResult dtResult)
    {
        CurrentObjectName = "RowMap";
        CurrentClassName = $"DataTable<{dtResult.RowStructName}>";
        HasData = true;
        ShowCeXml = false;
        HasParent = false;
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";

        Fields.Clear();
        foreach (var row in dtResult.Rows)
        {
            // Build preview from first 2 scalar fields
            var preview = "";
            var previewParts = new List<string>();
            foreach (var fv in row.Fields)
            {
                if (previewParts.Count >= 2) break;
                if (!string.IsNullOrEmpty(fv.TypedValue) && fv.TypedValue != "0" && fv.TypedValue != "0.0"
                    && fv.TypeName != "ObjectProperty" && fv.TypeName != "ClassProperty")
                {
                    previewParts.Add($"{fv.Name}={fv.TypedValue}");
                }
                else if (!string.IsNullOrEmpty(fv.StrValue))
                {
                    var s = fv.StrValue.Length > 30 ? fv.StrValue[..30] + "..." : fv.StrValue;
                    previewParts.Add($"{fv.Name}=\"{s}\"");
                }
                else if (!string.IsNullOrEmpty(fv.PtrName))
                {
                    previewParts.Add($"{fv.Name}={fv.PtrName}");
                }
            }
            if (previewParts.Count > 0)
                preview = " | " + string.Join(", ", previewParts);

            // Actual byte offset of the uint8* pointer within TSparseArray data
            int rowPtrOffset = row.SparseIndex * dtResult.Stride + dtResult.FNameSize;
            var f = new LiveFieldValue
            {
                Name = $"[{row.SparseIndex}] {row.RowName}",
                TypeName = "StructProperty",
                Offset = rowPtrOffset,
                Size = 0,
                TypedValue = $"{{{dtResult.RowStructName}}}{preview}",
                // Enable struct navigation to drill into the row data
                StructDataAddr = row.DataAddr,
                StructClassAddr = dtResult.RowStructAddr,
                StructTypeName = dtResult.RowStructName,
            };
            if (!string.IsNullOrEmpty(row.DataAddr))
                f.FieldAddress = row.DataAddr;
            Fields.Add(f);
        }
    }

    private void PopulateArrayContainerFields(List<ArrayElementValue> elements, LiveFieldValue sourceField)
    {
        var typeLabel = !string.IsNullOrEmpty(sourceField.ArrayStructType)
            ? sourceField.ArrayStructType : sourceField.ArrayInnerType;
        CurrentObjectName = sourceField.Name;
        CurrentClassName = $"Array<{typeLabel}>";
        HasData = true;
        ShowCeXml = false;
        // Disable Parent button for container views (not a UObject)
        HasParent = false;
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";

        // Parse TArray::Data base address for computing element addresses
        ulong dataBase = 0;
        if (!string.IsNullOrEmpty(sourceField.ArrayDataAddr))
            ulong.TryParse(sourceField.ArrayDataAddr.Replace("0x", "").Replace("0X", ""),
                System.Globalization.NumberStyles.HexNumber, null, out dataBase);

        // Check if this is a struct array with navigation metadata
        bool isStructArray = sourceField.ArrayInnerType == "StructProperty"
            && !string.IsNullOrEmpty(sourceField.ArrayStructClassAddr);

        Fields.Clear();
        foreach (var elem in elements)
        {
            // Compute element address for struct navigation
            var elemAddr = (isStructArray && dataBase != 0 && sourceField.ArrayElemSize > 0)
                ? $"0x{dataBase + (ulong)(elem.Index * sourceField.ArrayElemSize):X}" : "";

            var f = new LiveFieldValue
            {
                Name = $"[{elem.Index}]",
                TypeName = sourceField.ArrayInnerType,
                Offset = elem.Index * sourceField.ArrayElemSize,
                Size = sourceField.ArrayElemSize,
                HexValue = elem.Hex,
                TypedValue = !string.IsNullOrEmpty(elem.PtrName)
                    ? (!string.IsNullOrEmpty(elem.PtrClassName)
                        ? $"{elem.PtrName} ({elem.PtrClassName})"
                        : elem.PtrName)
                    : (!string.IsNullOrEmpty(elem.EnumName) ? elem.EnumName : elem.Value),
                PtrAddress = elem.PtrAddress,
                PtrName = elem.PtrName,
                PtrClassName = elem.PtrClassName,
                EnumName = elem.EnumName,
                // Struct navigation for StructProperty elements
                StructDataAddr = elemAddr,
                StructClassAddr = isStructArray ? sourceField.ArrayStructClassAddr : "",
                StructTypeName = isStructArray ? sourceField.ArrayStructType : "",
            };
            if (dataBase != 0 && sourceField.ArrayElemSize > 0)
                f.FieldAddress = $"0x{dataBase + (ulong)(elem.Index * sourceField.ArrayElemSize):X}";
            Fields.Add(f);
        }
        ApplyPendingElementScroll();
    }

    private void PopulateMapContainerFields(List<ContainerElementValue> elements, LiveFieldValue sourceField)
    {
        var keyLabel = !string.IsNullOrEmpty(sourceField.MapKeyType) ? sourceField.MapKeyType : "?";
        var valLabel = !string.IsNullOrEmpty(sourceField.MapValueType) ? sourceField.MapValueType : "?";
        CurrentObjectName = sourceField.Name;
        CurrentClassName = $"Map<{keyLabel}, {valLabel}>";
        HasData = true;
        ShowCeXml = false;
        HasParent = false;
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";

        // Parse TSparseArray::Data base address for computing element addresses
        ulong dataBase = 0;
        if (!string.IsNullOrEmpty(sourceField.MapDataAddr))
            ulong.TryParse(sourceField.MapDataAddr.Replace("0x", "").Replace("0X", ""),
                System.Globalization.NumberStyles.HexNumber, null, out dataBase);
        // Use aligned value offset if available (DLL computes alignment); fall back to key size
        int valOffset = sourceField.MapValueOffset > 0 ? sourceField.MapValueOffset : sourceField.MapKeySize;
        int pairSize = valOffset + sourceField.MapValueSize;
        int stride = ComputeSetElementStride(pairSize);

        // Check if value type is StructProperty with navigation metadata
        bool isStructValue = sourceField.MapValueType == "StructProperty"
            && !string.IsNullOrEmpty(sourceField.MapValueStructAddr);

        Fields.Clear();
        if (elements.Count == 0)
        {
            // Show metadata summary when element data couldn't be read
            StatusText = $"Map has {sourceField.MapCount} entries but element data could not be read (key={keyLabel} sz={sourceField.MapKeySize}, val={valLabel} sz={sourceField.MapValueSize})";
        }
        foreach (var elem in elements)
        {
            var keyDisplay = !string.IsNullOrEmpty(elem.KeyPtrName) ? elem.KeyPtrName : elem.Key;
            var valDisplay = !string.IsNullOrEmpty(elem.ValuePtrName) ? elem.ValuePtrName : elem.Value;

            // Compute value struct address: entry start + aligned value offset
            var valStructAddr = (isStructValue && dataBase != 0 && stride > 0)
                ? $"0x{dataBase + (ulong)(elem.Index * stride) + (ulong)valOffset:X}" : "";

            var f = new LiveFieldValue
            {
                Name = $"[{elem.Index}] {keyDisplay}",
                TypeName = sourceField.MapValueType,
                Offset = elem.Index * stride,
                Size = sourceField.MapKeySize + sourceField.MapValueSize,
                HexValue = !string.IsNullOrEmpty(elem.ValueHex) ? $"{elem.KeyHex} | {elem.ValueHex}" : elem.KeyHex,
                TypedValue = $"{keyDisplay} \u2192 {valDisplay}",
                // Enable → navigation for ObjectProperty values
                PtrAddress = elem.ValuePtrAddress,
                PtrName = elem.ValuePtrName,
                PtrClassName = elem.ValuePtrClassName,
                // Struct navigation for StructProperty values
                StructDataAddr = valStructAddr,
                StructClassAddr = isStructValue ? sourceField.MapValueStructAddr : "",
                StructTypeName = isStructValue ? sourceField.MapValueStructType : "",
            };
            if (dataBase != 0 && stride > 0)
                f.FieldAddress = $"0x{dataBase + (ulong)(elem.Index * stride):X}";
            Fields.Add(f);
        }
        ApplyPendingElementScroll();
    }

    /// <summary>
    /// Re-populate the container view from a (potentially refreshed) container field.
    /// Dispatches to the appropriate populate helper based on container type.
    /// </summary>
    private void RepopulateContainerView(LiveFieldValue containerField, BreadcrumbItem? bc = null)
    {
        // DataTable rows: use cached DataTableWalkResult from breadcrumb
        if (bc is { IsDataTableView: true, DataTableData: not null })
        {
            PopulateDataTableRowFields(bc.DataTableData);
        }
        else if (containerField.ArrayCount > 0 && !string.IsNullOrEmpty(containerField.ArrayInnerType))
        {
            PopulateArrayContainerFields(containerField.ArrayElements ?? new(), containerField);
        }
        else if (containerField.MapCount > 0 && !string.IsNullOrEmpty(containerField.MapKeyType))
        {
            PopulateMapContainerFields(containerField.MapElements ?? new(), containerField);
        }
        else if (containerField.SetCount > 0 && !string.IsNullOrEmpty(containerField.SetElemType))
        {
            PopulateSetContainerFields(containerField.SetElements ?? new(), containerField);
        }
    }

    /// <summary>
    /// Re-populate a path-synthetic container crumb on Back-navigation.
    ///
    /// <see cref="PathStepToBreadcrumbs"/> emits the array-field level of a
    /// Locate-in-GWorld object-pointer-array hop as a container crumb whose
    /// <see cref="BreadcrumbItem.ContainerField"/> is deliberately left null — the
    /// GWorld path step carries no TArray::Data base / element count / resolved
    /// element list, so the view cannot be rebuilt from the crumb alone. The normal
    /// container re-populate branch is gated on ContainerField != null, so without
    /// this such a crumb would fall through to a plain parent re-walk and render the
    /// PARENT object's field grid instead of the array element view (a silent
    /// mis-render — the crumb label says e.g. "SpawnedAttributes" but the grid shows
    /// the owning object). Here we lazily hydrate it: re-walk the parent object live
    /// (the crumb's Address is the parent), match the container field by name +
    /// offset (the same lookup <c>RefreshCurrentView</c> uses), and re-populate the
    /// container element view from the freshly-resolved field. Returns true if it
    /// handled the crumb; false (no live match / not a container) lets the caller
    /// fall through to the existing re-walk.
    /// </summary>
    private async Task<bool> TryRepopulateSyntheticContainerAsync(BreadcrumbItem item)
    {
        if (!item.IsContainerView || item.ContainerField != null) return false;

        var classAddr = string.IsNullOrEmpty(item.ClassAddr) ? null : item.ClassAddr;
        var result = await _dump.WalkInstanceAsync(item.Address, classAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
        result = await AutoFillGapsRetryAsync(result, item.Address, classAddr);

        var field = result.Fields.FirstOrDefault(f => f.Name == item.FieldName && f.Offset == item.FieldOffset);
        if (field == null) return false;

        // Only handle when the matched field actually resolves to a populatable
        // container (mirrors RepopulateContainerView's non-DataTable branches);
        // otherwise let the caller fall through to a normal re-walk.
        bool willRepopulate =
            (field.ArrayCount > 0 && !string.IsNullOrEmpty(field.ArrayInnerType)) ||
            (field.MapCount > 0 && !string.IsNullOrEmpty(field.MapKeyType)) ||
            (field.SetCount > 0 && !string.IsNullOrEmpty(field.SetElemType));
        if (!willRepopulate) return false;

        RepopulateContainerView(field, item);
        _log.Info($"NAV⇒SyntheticContainer rehydrated {item.FieldName} @ {item.Address} off=0x{item.FieldOffset:X}");
        return true;
    }

    private void PopulateSetContainerFields(List<ContainerElementValue> elements, LiveFieldValue sourceField)
    {
        var elemLabel = !string.IsNullOrEmpty(sourceField.SetElemType) ? sourceField.SetElemType : "?";
        CurrentObjectName = sourceField.Name;
        CurrentClassName = $"Set<{elemLabel}>";
        HasData = true;
        ShowCeXml = false;
        HasParent = false;
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";

        // Parse TSparseArray::Data base address for computing element addresses
        ulong dataBase = 0;
        if (!string.IsNullOrEmpty(sourceField.SetDataAddr))
            ulong.TryParse(sourceField.SetDataAddr.Replace("0x", "").Replace("0X", ""),
                System.Globalization.NumberStyles.HexNumber, null, out dataBase);
        int stride = ComputeSetElementStride(sourceField.SetElemSize);

        // Check if element type is StructProperty with navigation metadata
        bool isStructElem = sourceField.SetElemType == "StructProperty"
            && !string.IsNullOrEmpty(sourceField.SetElemStructAddr);

        Fields.Clear();
        foreach (var elem in elements)
        {
            var display = !string.IsNullOrEmpty(elem.KeyPtrName) ? elem.KeyPtrName : elem.Key;

            // Compute struct element address
            var structAddr = (isStructElem && dataBase != 0 && stride > 0)
                ? $"0x{dataBase + (ulong)(elem.Index * stride):X}" : "";

            var f = new LiveFieldValue
            {
                Name = $"[{elem.Index}]",
                TypeName = sourceField.SetElemType,
                Offset = elem.Index * stride,
                Size = sourceField.SetElemSize,
                HexValue = elem.KeyHex,
                TypedValue = display,
                // Enable → navigation for ObjectProperty elements
                PtrAddress = elem.KeyPtrAddress,
                PtrName = elem.KeyPtrName,
                PtrClassName = elem.KeyPtrClassName,
                // Struct navigation for StructProperty elements
                StructDataAddr = structAddr,
                StructClassAddr = isStructElem ? sourceField.SetElemStructAddr : "",
                StructTypeName = isStructElem ? sourceField.SetElemStructType : "",
            };
            if (dataBase != 0 && stride > 0)
                f.FieldAddress = $"0x{dataBase + (ulong)(elem.Index * stride):X}";
            Fields.Add(f);
        }
        ApplyPendingElementScroll();
    }

    /// <summary>
    /// Apply a pending "[N]" scroll hint left over after Open-from-Find-Refs's
    /// auto-drill chain. The first scroll hint (container field name) was
    /// consumed by UpdateDisplay; UpdateDisplay re-armed
    /// _pendingScrollFieldName with "[N]" before triggering NavigateToContainer
    /// so the freshly-built container Fields list scrolls to the matching
    /// element entry. Map elements use the "[N] keyDisplay" naming pattern, so
    /// we accept either an exact match or a "[N] " prefix.
    /// </summary>
    private void ApplyPendingElementScroll()
    {
        if (string.IsNullOrEmpty(_pendingScrollFieldName)) return;
        var hint = _pendingScrollFieldName;
        // Only intercept "[N]" element hints here — non-bracket hints belong
        // to the UpdateDisplay scroll path (object-instance fields).
        if (hint.Length < 3 || hint[0] != '[' || !hint.EndsWith("]")) return;

        _pendingScrollFieldName = null;
        var hit = Fields.FirstOrDefault(f =>
            f.Name == hint || f.Name.StartsWith(hint + " ", StringComparison.Ordinal));
        if (hit != null)
        {
            SelectedField = hit;
            ScrollToFieldRequested?.Invoke(hit.Name);
            _log.Info($"PopulateContainer: auto-scrolled to '{hit.Name}' (element hint '{hint}')");
        }
        else
        {
            _log.Info($"PopulateContainer: element hint '{hint}' not found");
        }
    }

    /// <summary>
    /// Post-match container drill shared by the by-name (Find Refs) and
    /// by-offset (Value Search) scroll paths in UpdateDisplay. When an
    /// element index is pending and the matched field is a navigable
    /// container, drill in and leave a "[N]" hint so the freshly-built
    /// element view scrolls to the matched entry. No-op for direct fields
    /// (pending index &lt; 0) or non-container matches.
    /// </summary>
    private void TryDrillIntoMatchedContainer(LiveFieldValue hit)
    {
        if (_pendingDrillElementIndex < 0) return;
        var elemIndex = _pendingDrillElementIndex;
        _pendingDrillElementIndex = -1;
        if (hit.IsContainerNavigable)
        {
            // Stage the element scroll hint so PopulateContainerFields
            // (called by NavigateToContainerAsync) picks it up.
            _pendingScrollFieldName = $"[{elemIndex}]";
            _log.Info($"UpdateDisplay: auto-drill into container '{hit.Name}' element [{elemIndex}]");
            _ = NavigateToContainerAsync(hit);
        }
        else
        {
            _log.Info($"UpdateDisplay: skipped auto-drill — '{hit.Name}' is not container-navigable");
        }
    }

    /// <summary>
    /// Create a container field copy retaining only the elements matching the
    /// selected synthetic fields (one or more). Extracts sparse indices from
    /// each selected field's "[N]" or "[N] description" name pattern.
    /// Used by Copy CE Field(s) export to emit one container with N filtered
    /// elements instead of N separate top-level entries — preserves CE's
    /// hierarchical structure (container header + nested elements under same
    /// pointer chain).
    /// If no selected field has a parseable sparse index, returns the whole
    /// container (preserving the original single-select fallback).
    /// </summary>
    internal static LiveFieldValue FilterContainerToElement(
        LiveFieldValue containerField, IReadOnlyList<LiveFieldValue> selectedFields)
    {
        var indices = new HashSet<int>();
        foreach (var f in selectedFields)
        {
            var idx = ParseSparseIndex(f.Name);
            if (idx.HasValue) indices.Add(idx.Value);
        }
        if (indices.Count == 0) return containerField;

        if (containerField.DataTableRowCount > 0 && containerField.DataTableRowData != null)
        {
            return new LiveFieldValue
            {
                Name = containerField.Name,
                TypeName = containerField.TypeName,
                Offset = containerField.Offset,
                Size = containerField.Size,
                DataTableRowCount = containerField.DataTableRowCount,
                DataTableStructName = containerField.DataTableStructName,
                DataTableFNameSize = containerField.DataTableFNameSize,
                DataTableStride = containerField.DataTableStride,
                DataTableRowStructAddr = containerField.DataTableRowStructAddr,
                DataTableRowData = containerField.DataTableRowData
                    .Where(r => indices.Contains(r.SparseIndex)).ToList(),
            };
        }

        if (containerField.MapCount > 0 && containerField.MapElements != null)
        {
            return new LiveFieldValue
            {
                Name = containerField.Name,
                TypeName = containerField.TypeName,
                Offset = containerField.Offset,
                Size = containerField.Size,
                MapCount = containerField.MapCount,
                MapKeyType = containerField.MapKeyType,
                MapValueType = containerField.MapValueType,
                MapKeySize = containerField.MapKeySize,
                MapValueSize = containerField.MapValueSize,
                MapDataAddr = containerField.MapDataAddr,
                MapKeyStructAddr = containerField.MapKeyStructAddr,
                MapKeyStructType = containerField.MapKeyStructType,
                MapValueStructAddr = containerField.MapValueStructAddr,
                MapValueStructType = containerField.MapValueStructType,
                MapElements = containerField.MapElements.Where(e => indices.Contains(e.Index)).ToList(),
            };
        }

        if (containerField.SetCount > 0 && containerField.SetElements != null)
        {
            return new LiveFieldValue
            {
                Name = containerField.Name,
                TypeName = containerField.TypeName,
                Offset = containerField.Offset,
                Size = containerField.Size,
                SetCount = containerField.SetCount,
                SetElemType = containerField.SetElemType,
                SetElemSize = containerField.SetElemSize,
                SetDataAddr = containerField.SetDataAddr,
                SetElemStructAddr = containerField.SetElemStructAddr,
                SetElemStructType = containerField.SetElemStructType,
                SetElements = containerField.SetElements.Where(e => indices.Contains(e.Index)).ToList(),
            };
        }

        if (containerField.ArrayCount > 0 && containerField.ArrayElements != null)
        {
            return new LiveFieldValue
            {
                Name = containerField.Name,
                TypeName = containerField.TypeName,
                Offset = containerField.Offset,
                Size = containerField.Size,
                ArrayCount = containerField.ArrayCount,
                ArrayInnerType = containerField.ArrayInnerType,
                ArrayStructType = containerField.ArrayStructType,
                ArrayElemSize = containerField.ArrayElemSize,
                ArrayInnerAddr = containerField.ArrayInnerAddr,
                ArrayDataAddr = containerField.ArrayDataAddr,
                ArrayStructClassAddr = containerField.ArrayStructClassAddr,
                SoftArrayFNameSize = containerField.SoftArrayFNameSize,
                SoftArrayIsTopLevelAssetPath = containerField.SoftArrayIsTopLevelAssetPath,
                ArrayElements = containerField.ArrayElements.Where(e => indices.Contains(e.Index)).ToList(),
                ArrayEnumAddr = containerField.ArrayEnumAddr,
                ArrayEnumEntries = containerField.ArrayEnumEntries,
            };
        }

        return containerField; // fallback: emit whole container
    }

    /// <summary>Parse sparse index from "[N]" or "[N] name" patterns.</summary>
    private static int? ParseSparseIndex(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '[') return null;
        var endBracket = name.IndexOf(']');
        if (endBracket <= 1) return null;
        if (int.TryParse(name.Substring(1, endBracket - 1), out var index))
            return index;
        return null;
    }

    /// <summary>
    /// Check if an array inner type requires Phase D/E/F resolution (pointer names, struct fields).
    /// read_array_elements (Phase B) only handles scalars; pointer/struct arrays must use
    /// the inline elements from walk_instance which have full resolution.
    /// </summary>
    private static bool IsPointerOrStructArrayType(string innerType)
        => innerType is "ObjectProperty" or "ClassProperty"
            or "WeakObjectProperty"
            or "SoftObjectProperty" or "SoftClassProperty"
            or "LazyObjectProperty"
            or "InterfaceProperty"
            or "DelegateProperty"
            or "MulticastDelegateProperty" or "MulticastInlineDelegateProperty"
            or "StructProperty";

    /// <summary>
    /// Compute TSparseArray element stride: AlignUp(elemSize, 4) + 8.
    /// Mirrors Mem::ComputeSetElementStride in the DLL and CeXmlExportService.
    /// </summary>
    private static int ComputeSetElementStride(int elemSize)
    {
        int hashStart = (elemSize + 3) & ~3;
        return hashStart + 8;
    }

    /// <summary>
    /// Detect fields whose container element count exceeds the loaded element count.
    /// Returns a warning string listing the truncated fields, or null if none.
    /// </summary>
    private static string? BuildContainerLimitWarning(IEnumerable<LiveFieldValue> fields, int arrayLimit)
    {
        var truncated = new List<string>();
        foreach (var f in fields)
        {
            if (f.ArrayCount > arrayLimit)
            {
                int loaded = f.ArrayElements?.Count ?? 0;
                truncated.Add($"{f.Name} (Array: {f.ArrayCount} total, {loaded} loaded)");
            }
            if (f.MapCount > arrayLimit)
            {
                int loaded = f.MapElements?.Count ?? 0;
                truncated.Add($"{f.Name} (Map: {f.MapCount} total, {loaded} loaded)");
            }
            if (f.SetCount > arrayLimit)
            {
                int loaded = f.SetElements?.Count ?? 0;
                truncated.Add($"{f.Name} (Set: {f.SetCount} total, {loaded} loaded)");
            }
        }
        if (truncated.Count == 0) return null;
        return $"⚠ Container element limit ({arrayLimit}): {string.Join(", ", truncated)}";
    }

    [RelayCommand]
    private async Task NavigateToBreadcrumbAsync(BreadcrumbItem? item)
    {
        if (item == null) return;

        try
        {
            ClearStatus();
            IsLoading = true;

            // Remove all breadcrumbs after this one
            var idx = Breadcrumbs.IndexOf(item);
            if (idx < 0) return;

            var removedCount = Breadcrumbs.Count - idx - 1;
            while (Breadcrumbs.Count > idx + 1)
                Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);

            _log.Info($"NAV⇒BC[{idx}] {item.FieldName ?? item.Label} removed={removedCount} | BC={FormatBreadcrumbTrace()}");
            var scrollHint = item.ScrollHintFieldName;

            // If navigating back to a container view, re-populate from saved field
            if (item.IsContainerView && item.ContainerField != null)
            {
                RepopulateContainerView(item.ContainerField, item);
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            // Path-synthetic container crumb (IsContainerView but no live ContainerField,
            // from PathStepToBreadcrumbs): lazily re-hydrate the container view from a live
            // parent walk instead of falling through to a parent-grid re-walk.
            if (item.IsContainerView && item.ContainerField == null
                && await TryRepopulateSyntheticContainerAsync(item))
            {
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            // If navigating back to the GWorld actor-list root, re-display the
            // actor list. A deeper crumb sharing the world address (OwningWorld)
            // is NOT the root — it falls through to a normal instance walk.
            if (IsGWorldActorListRoot(item))
            {
                PopulateFromWorld(_cachedWorld!);
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            // Re-walk this object (pass ClassAddr for StructProperty navigation)
            var classAddr = string.IsNullOrEmpty(item.ClassAddr) ? null : item.ClassAddr;
            var result = await _dump.WalkInstanceAsync(item.Address, classAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
            result = await AutoFillGapsRetryAsync(result, item.Address, classAddr);
            UpdateDisplay(result);

            if (!string.IsNullOrEmpty(scrollHint))
                ScrollToFieldRequested?.Invoke(scrollHint);
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        // Cancel bookmark save mode on any navigation
        IsBookmarkSaveMode = false;

        // If at root breadcrumb and we have pre-bookmark state, restore it
        if (Breadcrumbs.Count < 2 && _preBookmarkBreadcrumbs != null)
        {
            try
            {
                ClearStatus();
                IsLoading = true;

                var savedBreadcrumbs = _preBookmarkBreadcrumbs;
                var savedCachedWorld = _preBookmarkCachedWorld;
                _preBookmarkBreadcrumbs = null;
                _preBookmarkAddress = "";
                _preBookmarkCachedWorld = null;

                Breadcrumbs.Clear();
                foreach (var bc in savedBreadcrumbs)
                    Breadcrumbs.Add(bc);
                _cachedWorld = savedCachedWorld;

                var lastBc = Breadcrumbs.LastOrDefault();
                if (lastBc != null)
                {
                    if (lastBc.IsContainerView && lastBc.ContainerField != null)
                    {
                        RepopulateContainerView(lastBc.ContainerField, lastBc);
                        return;
                    }
                    if (lastBc.IsContainerView && lastBc.ContainerField == null
                        && await TryRepopulateSyntheticContainerAsync(lastBc))
                    {
                        return;
                    }
                    if (IsGWorldActorListRoot(lastBc))
                    {
                        PopulateFromWorld(_cachedWorld!);
                        return;
                    }
                    var classAddr = string.IsNullOrEmpty(lastBc.ClassAddr) ? null : lastBc.ClassAddr;
                    var result = await _dump.WalkInstanceAsync(
                        lastBc.Address, classAddr,
                        arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
                    result = await AutoFillGapsRetryAsync(result, lastBc.Address, classAddr);
                    UpdateDisplay(result);
                }

                StatusText = "Returned to pre-bookmark view";
                _log.Info("NAV←Back restored pre-bookmark state");
            }
            catch (Exception ex)
            {
                SetError(ex);
            }
            finally
            {
                IsLoading = false;
            }
            return;
        }

        if (Breadcrumbs.Count < 2) return;

        // Re-check AOBMaker CE Plugin availability (detects CE start/close, cooldown-throttled)
        TryCheckAobMaker();

        var removed = Breadcrumbs[^1];
        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        var prev = Breadcrumbs[^1];
        var scrollHint = prev.ScrollHintFieldName;
        _log.Info($"NAV←Back removed={removed.FieldName ?? removed.Label} | BC={FormatBreadcrumbTrace()}");

        try
        {
            ClearStatus();
            IsLoading = true;

            // If going back to a container view, re-populate from saved field
            if (prev.IsContainerView && prev.ContainerField != null)
            {
                RepopulateContainerView(prev.ContainerField, prev);
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            // Path-synthetic container crumb: re-hydrate from a live parent walk
            // (see TryRepopulateSyntheticContainerAsync) rather than re-walking to the parent grid.
            if (prev.IsContainerView && prev.ContainerField == null
                && await TryRepopulateSyntheticContainerAsync(prev))
            {
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            // If going back to the GWorld actor-list root, re-display the actor
            // list. A deeper crumb sharing the world address (OwningWorld) is not
            // the root — it falls through to a normal instance walk below.
            if (IsGWorldActorListRoot(prev))
            {
                PopulateFromWorld(_cachedWorld!);
                if (!string.IsNullOrEmpty(scrollHint))
                    ScrollToFieldRequested?.Invoke(scrollHint);
                return;
            }

            var classAddr = string.IsNullOrEmpty(prev.ClassAddr) ? null : prev.ClassAddr;
            var result = await _dump.WalkInstanceAsync(prev.Address, classAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
            result = await AutoFillGapsRetryAsync(result, prev.Address, classAddr);
            UpdateDisplay(result);

            if (!string.IsNullOrEmpty(scrollHint))
                ScrollToFieldRequested?.Invoke(scrollHint);
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task GoToParentAsync()
    {
        if (string.IsNullOrEmpty(CurrentOuterAddr) || CurrentOuterAddr == "0x0") return;

        try
        {
            ClearStatus();
            IsLoading = true;

            // Navigate to the parent (OuterPrivate) object
            var parentAddr = CurrentOuterAddr;

            // Add current object as a breadcrumb before navigating up
            // so user can go back down via breadcrumbs
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Address = parentAddr,
                Label = !string.IsNullOrEmpty(CurrentOuterName) ? CurrentOuterName : "Parent",
                IsPointerDeref = true,
                FieldOffset = 0,
                FieldName = "Outer",
            });

            var result = await _dump.WalkInstanceAsync(parentAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
            result = await AutoFillGapsRetryAsync(result, parentAddr);
            UpdateDisplay(result);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to navigate to parent {CurrentOuterAddr}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // === Find References (reverse pointer scan) ===
    //
    // OuterPrivate / Parent gives the naming-hierarchy parent (often
    // /Engine/Transient for runtime-spawned objects), not the logical
    // gameplay owner. Find References reverse-scans every UObject for
    // pointers to the current one, surfacing answers like "this Item is
    // PlayerInventory.Items[3]". Results render in a panel above the
    // field grid; user clicks Open to navigate to that owner.

    [ObservableProperty] private ObservableCollection<ReferenceMatch> _references = new();
    [ObservableProperty] private bool _hasReferences;
    [ObservableProperty] private string _referencesHeader = "";

    // Optional scroll-to hint applied once the next WalkInstance result
    // populates the Fields collection. Used by Open-from-references so the
    // user lands directly on the field that holds the pointer.
    private string? _pendingScrollFieldName;

    // Value Search cross-nav focus: the owning property's byte offset to
    // scroll to once the next walk result populates Fields. Field NAMES
    // aren't unique (inherited members, map .Key/.Value), so Value Search
    // matches the row by OFFSET; the by-name hint above stays for Find Refs.
    private int? _pendingScrollFieldOffset;

    // Optional auto-drill index applied alongside _pendingScrollFieldName.
    // When >= 0 and the resolved field is container-navigable, the post-
    // load handler navigates into the container view AND sets a follow-up
    // scroll hint for the element entry "[N]" so Open-from-Find-Refs lands
    // directly on the matched element instead of stopping at the container.
    private int _pendingDrillElementIndex = -1;

    [RelayCommand]
    private async Task FindReferencesAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress) || CurrentAddress == "0x0") return;

        try
        {
            ClearStatus();
            IsLoading = true;
            StatusText = "Searching for references…";

            var result = await _dump.FindReferencesToUObjectAsync(CurrentAddress);

            References.Clear();
            foreach (var r in result.References)
                References.Add(r);
            HasReferences = References.Count > 0;

            string scanSuffix = "";
            if (result.Scan is { } cs && cs.ObjectsTotal > 0)
            {
                scanSuffix = cs.DeadlineHit
                    ? $"  [scanned {cs.ObjectsScanned}/{cs.ObjectsTotal} in {cs.DurationMs}ms — DEADLINE HIT, retry to continue]"
                    : $"  [scanned {cs.ObjectsScanned}/{cs.ObjectsTotal} in {cs.DurationMs}ms]";
            }

            if (HasReferences)
            {
                ReferencesHeader = $"References to {CurrentObjectName} ({References.Count})" + scanSuffix;
                StatusText = $"Found {References.Count} reference(s)" + scanSuffix;
                _log.Info($"FindReferences: {CurrentAddress} -> {References.Count} matches{scanSuffix}");
            }
            else
            {
                ReferencesHeader = $"References to {CurrentObjectName} (none found)" + scanSuffix;
                HasReferences = true;  // Show empty panel so user sees scan completed
                StatusText = "No references found — likely held by a non-reflected pointer (TUniquePtr / raw pointer / non-UObject struct)" + scanSuffix;
                _log.Info($"FindReferences: {CurrentAddress} -> 0 matches{scanSuffix}");
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"FindReferences failed for {CurrentAddress}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ClearReferences()
    {
        References.Clear();
        HasReferences = false;
        ReferencesHeader = "";
    }

    [RelayCommand]
    private void ShowRelatedObjects()
    {
        if (string.IsNullOrEmpty(CurrentAddress) || CurrentAddress == "0x0") return;
        NavigateToRelatedObjects?.Invoke(CurrentAddress);
    }

    [RelayCommand]
    private async Task OpenReferenceOwnerAsync(ReferenceMatch? match)
    {
        if (match == null || string.IsNullOrEmpty(match.OwnerAddress)) return;

        // Pre-arm the scroll hint so when the new owner's Fields list
        // populates we auto-select the field that held the pointer.
        var firstSegment = (match.FieldName ?? "").Split('.')[0];
        _pendingScrollFieldName = string.IsNullOrEmpty(firstSegment) ? null : firstSegment;

        // Container hits (Array/Map/Set element) — pre-arm the drill index
        // so the post-load handler also navigates into the container view
        // and lands on element "[N]". Only auto-drill when the FieldName
        // refers DIRECTLY to the container (or "<Container>.Key" /
        // "<Container>.Value" for map-side hits) — nested struct paths
        // like "Stats.Equipment" require a manual struct drill the user
        // has to do themselves, so we don't auto-drill those.
        var fieldName = match.FieldName ?? "";
        var canAutoDrill = match.ElementIndex >= 0
            && !string.IsNullOrEmpty(firstSegment)
            && (fieldName == firstSegment
                || fieldName == firstSegment + ".Key"
                || fieldName == firstSegment + ".Value");
        _pendingDrillElementIndex = canAutoDrill ? match.ElementIndex : -1;

        // Append a status hint so the user knows where to look on the new
        // page (the field that's holding the pointer).
        StatusText = $"Opened {match.OwnerName} — held the previous object in '{match.FieldName}'"
            + (match.ElementIndex >= 0 ? $"[{match.ElementIndex}]" : "");
        await NavigateToAddressAsync(match.OwnerAddress);
    }

    [RelayCommand]
    private async Task NavigateToAddressAsync(string? addr)
    {
        if (string.IsNullOrEmpty(addr)) return;

        // Drop stale Find Refs auto-drill state if a different navigation
        // path takes over before the chained drill kicks in. Guarded:
        // OpenReferenceOwnerAsync sets _pendingDrillElementIndex *before*
        // calling NavigateToAddressAsync, so we mustn't clobber it on the
        // call this command receives from that method. Detection: the
        // pending hint set by OpenReferenceOwnerAsync is non-empty.
        // NavigateToInstanceFieldAsync (Value Search) pre-arms the same
        // drill index alongside _pendingScrollFieldOffset, so preserve it
        // for that path too.
        if (string.IsNullOrEmpty(_pendingScrollFieldName) && !_pendingScrollFieldOffset.HasValue)
            _pendingDrillElementIndex = -1;

        try
        {
            ClearStatus();
            IsLoading = true;
            StopAutoRefreshTimer();
            _preBookmarkBreadcrumbs = null;
            IsBookmarkSaveMode = false;
            Breadcrumbs.Clear();
            // Stale references panel from a previous lookup target shouldn't
            // hang around when we navigate elsewhere — references are
            // about the now-current UObject, not the new one.
            References.Clear();
            HasReferences = false;

            // Normalize address: supports CE formats like "module.exe"+offset,
            // quoted module names ("module.exe"+offset), and plain hex.
            // Strict validation — garbage like "0xlkaskdlaj" surfaces as a clean
            // status message instead of silently navigating to address 0.
            if (!AddressHelper.TryNormalizeAddress(addr, _engineState?.ModuleBase, out var normalizedAddr))
            {
                StatusText = "Invalid address — expected hex (e.g. 0x7FF... or module.exe+RVA)";
                return;
            }

            await NavigateToAsync(normalizedAddr, "Custom", 0, "Custom", isPointer: true);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to navigate to {addr}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Navigate to a UObject instance and focus the field that produced a
    /// Value Search candidate. The owning property row is matched by byte
    /// OFFSET (field names aren't unique — inherited members + map
    /// .Key/.Value collide); if the candidate is a container element (display
    /// name ends in "[N]") the matched container is drilled and the element
    /// row [N] is selected. Falls back to a plain navigation when the field
    /// can't be located as a top-level row (e.g. a hit inside a nested
    /// struct, which the user must drill manually — same as Find Refs).
    /// </summary>
    public async Task NavigateToInstanceFieldAsync(string? addr, int fieldOffset, string? fieldName)
    {
        if (string.IsNullOrEmpty(addr)) return;
        // A container path (e.g. "Cargo[3].ItemId" or the deep
        // "SaveSlotList[0]...Tunes[2]") can't be reached by the single
        // offset/element pending-scroll — deep candidates carry fieldOffset=0,
        // which would otherwise mis-select the first offset-0 field. Walk the
        // owner then drill the full path explicitly (shared with LocateInGWorld).
        if (TryParseContainerPath(fieldName, out var pathSegs))
        {
            _pendingScrollFieldOffset = null;
            _pendingScrollFieldName = null;
            _pendingDrillElementIndex = -1;
            await NavigateToAddressAsync(addr);
            await DrillDisplayPathAsync(pathSegs);
            return;
        }
        // Pre-arm focus state BEFORE navigating; the post-walk UpdateDisplay
        // handler consumes it once Fields is populated. (Mirrors how
        // OpenReferenceOwnerAsync pre-arms the by-name hint for Find Refs.)
        _pendingScrollFieldOffset = fieldOffset;
        _pendingDrillElementIndex = ParseElementIndexSuffix(fieldName ?? "");
        await NavigateToAddressAsync(addr);
    }

    /// <summary>
    /// "Locate in GWorld": compute the shortest pointer chain from the live
    /// UWorld down to <paramref name="objectAddr"/> (the owning UObject), then
    /// REPLACE the breadcrumb spine with that path and land on the target.
    ///
    /// <paramref name="stopAtParent"/> distinguishes the two behaviours:
    ///   • false (land ON the target — Value Search / Snapshot / SPC AND the
    ///     Instance Finder selected object): build the full GWorld→…→target
    ///     spine, open the target node, and (for a container VALUE) scroll to /
    ///     drill the value field (<paramref name="scrollFieldOffset"/> /
    ///     <paramref name="scrollFieldName"/> "[N]").
    ///   • true  (stop at the PARENT — Interesting Funcs "where do instances of
    ///     this class live"): drop the final node, land on the holder, and
    ///     highlight the pointer field that leads to the target.
    ///
    /// On failure the reason is surfaced via StatusText (e.g. "increase depth").
    /// </summary>
    public async Task LocateInGWorldAsync(string? objectAddr, int scrollFieldOffset,
                                          string? scrollFieldName, bool stopAtParent,
                                          CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(objectAddr)) return;
        try
        {
            ClearStatus();
            IsLoading = true;
            StopAutoRefreshTimer();
            _preBookmarkBreadcrumbs = null;
            IsBookmarkSaveMode = false;

            var path = await _dump.FindPathFromGWorldAsync(objectAddr, objectAddr, GWorldLocateDepth, ct);

            if (!path.Found)
            {
                // Don't leave the previous object on screen as if it were the
                // result — clear it but keep the failure reason. (A user-initiated
                // cancel preserves the current view.)
                if (path.Status != "cancelled") ClearDisplayedNode();
                StatusText = GWorldPathFailureStatus(path);
                return;
            }

            BuildBreadcrumbSpineFromPath(path, objectAddr, stopAtParent);

            // Value inside a container path (Value Search / SPC "Array[N]...[M]",
            // e.g. "SaveSlotList[1].GP" or the deep
            // "SaveSlotList[0].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]"):
            // the single-shot pending-scroll can't chain container → element →
            // inner field across multiple "[N]" levels, so drill the full path
            // explicitly after reaching the owner — parity with the Instance
            // Finder structured-chain deep-drill.
            if (!stopAtParent && TryParseContainerPath(scrollFieldName, out var pathSegs))
            {
                _pendingScrollFieldOffset = null;
                _pendingScrollFieldName = null;
                _pendingDrillElementIndex = -1;

                var ownerAddr = Breadcrumbs[^1].Address;
                var ownerResult = await _dump.WalkInstanceAsync(ownerAddr, arrayLimit: ArrayLimit,
                                                                previewLimit: PreviewLimit, fillGaps: FillGaps, ct: ct);
                ownerResult = await AutoFillGapsRetryAsync(ownerResult, ownerAddr);
                UpdateDisplay(ownerResult);

                bool landed = await DrillDisplayPathAsync(pathSegs);
                // Fall back to the byte-offset scroll ONLY when the drill failed
                // WITHOUT navigating away — i.e. the first path segment isn't a field
                // on THIS object, so we're still on the owner. That's the cross-object
                // case (P4): the field name is the path FROM THE CANDIDATE to the owner
                // (e.g. "GameCharacters[0].MP" on a manager — the owner IS the find_path
                // target and holds MP as a DIRECT field at scrollFieldOffset). If the
                // drill instead navigated partway in before failing (e.g. a single-value
                // deep path whose container reallocated), we're no longer on the owner,
                // so skip the fallback rather than land on an unrelated row.
                if (!landed && Breadcrumbs.Count > 0 && Breadcrumbs[^1].Address == ownerAddr)
                    landed = ScrollToFieldByOffset(scrollFieldOffset);
                _log.Info($"LocateInGWorld: reach+container-path-drill, {path.Depth} hop(s), landed={landed} | BC={FormatBreadcrumbTrace()}");
                StatusText = landed
                    ? $"Located via GWorld — {path.Depth} hop(s) to {path.TargetName}; landed on {scrollFieldName}."
                    : $"Located via GWorld — {path.Depth} hop(s) to {path.TargetName} (drill into {scrollFieldName} manually).";
                return;
            }

            // Decide the field to scroll/highlight once the display node is walked.
            if (stopAtParent)
            {
                // Highlight the pointer on the parent that leads to the target,
                // but do NOT auto-drill into it (stop before the class).
                if (path.Steps.Count > 0)
                {
                    _pendingScrollFieldOffset = path.Steps[^1].FieldOffset;
                    _pendingDrillElementIndex = -1;
                }
            }
            else
            {
                // Land on the value field inside the owning object (auto-drill
                // into a container element when the field name carried a "[N]").
                _pendingScrollFieldOffset = scrollFieldOffset;
                _pendingDrillElementIndex = ParseElementIndexSuffix(scrollFieldName ?? "");
            }

            var displayAddr = Breadcrumbs[^1].Address;
            var result = await _dump.WalkInstanceAsync(displayAddr, arrayLimit: ArrayLimit,
                                                       previewLimit: PreviewLimit, fillGaps: FillGaps, ct: ct);
            result = await AutoFillGapsRetryAsync(result, displayAddr);
            UpdateDisplay(result);

            _log.Info($"LocateInGWorld: {(stopAtParent ? "parent" : "reach")} mode, {path.Depth} hop(s), " +
                      $"visited {path.Visited}, {path.DurationMs}ms | BC={FormatBreadcrumbTrace()}");

            StatusText = stopAtParent
                ? $"Located via GWorld — {path.Depth} hop(s); parent of {path.TargetName} ({path.TargetClass})."
                : $"Located via GWorld — {path.Depth} hop(s) to {path.TargetName} ({path.TargetClass}).";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"LocateInGWorld failed for {objectAddr}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Map a failed GWorld path search to an actionable status message.</summary>
    private string GWorldPathFailureStatus(GWorldPathResult path) => path.Status switch
    {
        // not_reachable means the BFS exhausted everything reachable from GWorld
        // (within the depth) WITHOUT finding the target — the object isn't
        // referenced by any forward pointer chain from GWorld. Raising the depth
        // does NOT help (the reachable set is already exhausted). This is common
        // for just-spawned or streaming / World-Partition actors that nothing
        // references yet.
        "not_reachable"  => $"Not reachable — nothing in the GWorld graph references this object (searched {path.Visited:N0} objects). Common for just-spawned or streaming/World-Partition actors; raising depth won't help. Try once it's aggro'd/selected in-game, or use Find Refs to find a holder.",
        "deadline"       => $"GWorld path search timed out at depth {GWorldLocateDepth} (visited {path.Visited:N0}). Try a smaller depth.",
        "visited_cap"    => $"GWorld path search space too large at depth {GWorldLocateDepth} (visited {path.Visited:N0}). Try a smaller depth.",
        "cancelled"      => "GWorld path search cancelled.",
        "no_gworld"      => "GWorld is not available (AOB scan found no UWorld).",
        "invalid_target" => "Could not resolve the target object in GObjects.",
        _                => $"No GWorld path found ({path.Status}).",
    };

    /// <summary>
    /// Replace the breadcrumb spine with the GWorld→target path: a GWorld root
    /// node + one node per hop. <paramref name="stopAtParent"/> drops the final
    /// (target) node so the view lands on the parent pointer.
    /// </summary>
    private void BuildBreadcrumbSpineFromPath(GWorldPathResult path, string objectAddr, bool stopAtParent)
    {
        int stepCount = path.Steps.Count;
        int includedSteps = stopAtParent ? Math.Max(0, stepCount - 1) : stepCount;

        Breadcrumbs.Clear();
        References.Clear();
        HasReferences = false;

        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = !string.IsNullOrEmpty(path.RootAddr) ? path.RootAddr : objectAddr,
            Label = !string.IsNullOrEmpty(path.RootName) ? path.RootName : "GWorld",
            IsPointerDeref = true,
            FieldOffset = 0,
            FieldName = "GWorld",
        });

        for (int i = 0; i < includedSteps; i++)
            foreach (var bc in PathStepToBreadcrumbs(path.Steps[i]))
                Breadcrumbs.Add(bc);
    }

    /// <summary>
    /// Convert one GWorld-path hop into the breadcrumb level(s) it represents.
    ///
    /// A <c>TArray&lt;ObjectProperty&gt;</c> element hop crosses TWO dereferences —
    /// deref <c>TArray::Data</c> (the array field at <c>FieldOffset</c>), then deref
    /// the element pointer at <c>index*8</c> within that buffer — but the DLL path
    /// collapses both into ONE hop (FieldOffset = array field, ElementIndex = the
    /// element). Emitting it as a single breadcrumb makes the CE chain stop at the
    /// TArray::Data buffer and apply the next field's offset to IT instead of to the
    /// element's target object (wrong addresses for everything below). So such a hop
    /// is split into TWO crumbs here — a container crumb (deref Data) + an element
    /// crumb (deref the pointer at index*8) — matching what manual navigation
    /// produces. Only raw object-pointer arrays split (the element slot IS an 8-byte
    /// <c>UObject*</c>); struct-array / Map / Set element hops keep the single crumb
    /// (their element isn't a plain pointer at a known stride).
    /// </summary>
    internal static IReadOnlyList<BreadcrumbItem> PathStepToBreadcrumbs(GWorldPathStep s)
    {
        var label = !string.IsNullOrEmpty(s.ToName) ? s.ToName
                  : (!string.IsNullOrEmpty(s.FieldName) ? s.FieldName : "(node)");

        bool isObjPtrArrayElem = s.ElementIndex >= 0
            && s.FieldType == "ArrayProperty"
            && (s.InnerType == "ObjectProperty" || s.InnerType == "ClassProperty");

        if (isObjPtrArrayElem)
        {
            return new[]
            {
                // Level 1 — the array field: deref TArray::Data. Flagged as a
                // container view so CleanBreadcrumbs skips it as a cycle endpoint
                // (it shares the parent object's resolved region). ContainerField
                // stays null (path-derived, not re-populatable); Back-nav handles
                // that with a plain re-walk.
                new BreadcrumbItem
                {
                    Address = !string.IsNullOrEmpty(s.From) ? s.From : s.To,
                    Label = !string.IsNullOrEmpty(s.FieldName) ? s.FieldName : "(array)",
                    FieldOffset = s.FieldOffset,
                    FieldName = s.FieldName,
                    IsContainerView = true,
                },
                // Level 2 — the element pointer at index*8: deref to the child object.
                new BreadcrumbItem
                {
                    Address = s.To,
                    Label = $"[{s.ElementIndex}]",
                    FieldOffset = s.ElementIndex * 8,  // ObjectProperty stride = 8 (pointer)
                    FieldName = $"[{s.ElementIndex}]",
                    IsPointerDeref = true,
                },
            };
        }

        if (s.ElementIndex >= 0) label += $"[{s.ElementIndex}]";
        return new[]
        {
            new BreadcrumbItem
            {
                Address = s.To,
                Label = label,
                FieldOffset = s.FieldOffset,
                FieldName = s.FieldName,
                IsPointerDeref = true,  // every edge we followed is a pointer deref
            },
        };
    }

    /// <summary>
    /// "Locate in GWorld" for a container-match value (Instance Finder by-address).
    /// Reaches the owning object via the shortest GWorld path, then drills the
    /// full container chain — outermost container → element [N] → (nested
    /// container → element → …) — to land ON the value, even when it lives in a
    /// deeply-nested, separately-allocated container (the deep-scan case). The
    /// single-shot <c>_pendingScroll*</c> path can't chain multiple container
    /// levels, so the drill is an explicit awaited sequence.
    /// </summary>
    public async Task LocateContainerInGWorldAsync(ContainerMatch match, CancellationToken ct = default)
    {
        if (match == null || string.IsNullOrEmpty(match.OwnerAddress)) return;
        try
        {
            ClearStatus();
            IsLoading = true;
            StopAutoRefreshTimer();
            _preBookmarkBreadcrumbs = null;
            IsBookmarkSaveMode = false;

            var path = await _dump.FindPathFromGWorldAsync(match.OwnerAddress, match.OwnerAddress,
                                                           GWorldLocateDepth, ct);
            if (!path.Found)
            {
                if (path.Status != "cancelled") ClearDisplayedNode();
                StatusText = GWorldPathFailureStatus(path);
                return;
            }

            BuildBreadcrumbSpineFromPath(path, match.OwnerAddress, stopAtParent: false);

            // Explicit awaited drill — clear the single-shot pending state.
            _pendingScrollFieldOffset = null;
            _pendingScrollFieldName = null;
            _pendingDrillElementIndex = -1;

            var ownerAddr = Breadcrumbs[^1].Address;
            var ownerResult = await _dump.WalkInstanceAsync(ownerAddr, arrayLimit: ArrayLimit,
                                                            previewLimit: PreviewLimit, fillGaps: FillGaps, ct: ct);
            ownerResult = await AutoFillGapsRetryAsync(ownerResult, ownerAddr);
            UpdateDisplay(ownerResult);   // land on owner

            int totalHops = 1 + match.NestedChain.Count;
            int drilled = await DrillContainerChainAsync(match);

            _log.Info($"LocateContainerInGWorld: {path.Depth} hop(s) to {match.OwnerName}, " +
                      $"drilled {drilled}/{totalHops} container level(s), {path.DurationMs}ms | BC={FormatBreadcrumbTrace()}");
            StatusText = drilled >= totalHops
                ? $"Located via GWorld — {path.Depth} hop(s); landed on {match.DisplayPath}."
                : $"Located via GWorld — {path.Depth} hop(s) to {match.OwnerName}; drilled {drilled}/{totalHops} level(s). " +
                  $"Continue manually: {match.DisplayPath}.";
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"LocateContainerInGWorld failed for {match.OwnerAddress}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Drill the container chain of <paramref name="match"/> hop-by-hop from the
    /// currently-displayed owning object, landing on the value. Each hop:
    /// navigate any intermediate DIRECT struct segments (dotted name) → drill the
    /// container → select element [N]; nested hops then drill INTO the struct
    /// element to continue. The deepest hop scrolls to the value (a field of the
    /// struct element at its intra-offset, or the leaf element itself). Returns
    /// the number of hops drilled; stops early (and reports) if a hop can't be
    /// matched in the live view.
    /// </summary>
    /// <summary>
    /// Flatten a container match into the ordered drill path, outermost-first:
    /// the match's own (outermost) container hop followed by each nested-chain
    /// hop. Each entry is (container field dotted-name, element index, intra
    /// offset). The last entry is the deepest hop whose intra-offset locates the
    /// value. Pure — unit-tested.
    /// </summary>
    internal static List<(string fieldName, int elementIndex, int intraOffset)> BuildContainerDrillPath(ContainerMatch match)
    {
        var hops = new List<(string fieldName, int elementIndex, int intraOffset)>
        {
            (match.FieldName, match.ElementIndex, match.IntraOffset),
        };
        foreach (var h in match.NestedChain)
            hops.Add((h.FieldName, h.ElementIndex, h.IntraOffset));
        return hops;
    }

    private async Task<int> DrillContainerChainAsync(ContainerMatch match)
    {
        var hops = BuildContainerDrillPath(match);

        int drilled = 0;
        for (int hi = 0; hi < hops.Count; hi++)
        {
            var (fieldName, elementIndex, intraOffset) = hops[hi];
            bool isLast = hi == hops.Count - 1;

            // Navigate leading DIRECT-struct segments (e.g. "MsTuneData" in
            // "MsTuneData.MsTunes"); the last segment is the container itself.
            var segments = fieldName.Split('.');
            for (int s = 0; s < segments.Length - 1; s++)
            {
                var structField = Fields.FirstOrDefault(f => f.Name == segments[s] && f.IsStructNavigation);
                if (structField == null) return drilled;   // can't continue
                await NavigateToFieldAsync(structField);
            }

            var containerName = segments[^1];
            var containerField = Fields.FirstOrDefault(f => f.Name == containerName && f.IsContainerNavigable);
            if (containerField == null) return drilled;
            await NavigateToContainerAsync(containerField);   // → element view

            var elemRow = Fields.FirstOrDefault(f =>
                f.Name == $"[{elementIndex}]" ||
                f.Name.StartsWith($"[{elementIndex}] ", StringComparison.Ordinal));
            if (elemRow == null) return drilled;

            if (!isLast)
            {
                // Must descend into the struct element to reach the next hop.
                if (!elemRow.IsStructNavigation) return drilled;
                await NavigateToFieldAsync(elemRow);
                drilled++;
                continue;
            }

            // Deepest hop — land on the value.
            if (elemRow.IsStructNavigation)
            {
                // Value is a field INSIDE the struct element (at intraOffset).
                await NavigateToFieldAsync(elemRow);
                var leaf = Fields.FirstOrDefault(f => f.Offset == intraOffset);
                if (leaf != null)
                {
                    SelectedField = leaf;
                    ScrollToFieldRequested?.Invoke(leaf.Name);
                }
            }
            else
            {
                // The element itself is the value (leaf element, e.g. TArray<int>).
                SelectedField = elemRow;
                ScrollToFieldRequested?.Invoke(elemRow.Name);
            }
            drilled++;
        }
        return drilled;
    }

    /// <summary>
    /// Extract a trailing "[N]" container-element index from a Value Search
    /// field display name (e.g. "Cargo[3]" or "Augments.Value[2]"). Returns
    /// -1 for a direct field with no element suffix, an empty/negative
    /// bracket, or a non-leaf path like "Cargo[3].ItemId" (not a drillable
    /// element row).
    /// </summary>
    internal static int ParseElementIndexSuffix(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || fieldName[^1] != ']') return -1;
        int open = fieldName.LastIndexOf('[');
        if (open < 0 || open >= fieldName.Length - 2) return -1;  // no '[' or "[]"
        var inner = fieldName.Substring(open + 1, fieldName.Length - open - 2);
        return int.TryParse(inner, out var idx) && idx >= 0 ? idx : -1;
    }

    /// <summary>
    /// Parse a Value Search / SPC candidate display name into an ordered drill
    /// path of segments, each either a DIRECT struct field ("Name", index -1) or
    /// a CONTAINER element ("Name", index N from "Name[N]"). Returns true only
    /// when the path contains at least one "[N]" element (so it needs container
    /// drilling); a plain field ("Health"), an empty/malformed name, or a bracket
    /// not at a segment's end returns false so the caller falls back to the
    /// single-offset scroll. Handles arbitrary depth, e.g.
    /// "SaveSlotList[0].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[2]". Pure —
    /// unit-tested. Generalises the former single-"[N]" struct-array parser.
    /// </summary>
    /// <summary>
    /// Resolve the field row a byte-offset scroll hint should land on: the field
    /// at <paramref name="wantOffset"/> exactly, or — when none exists because the
    /// leaf lives inside a nested struct (a GAS <c>FGameplayAttributeData.CurrentValue</c>
    /// at owner+0x120 sits inside the <c>CurrentHealth</c> StructProperty at 0x118) —
    /// the containing top-level field, i.e. the one with the largest offset ≤ the
    /// leaf offset. Returns null only when no field is at or before the offset.
    /// Pure / static so the contract is unit-testable without a populated grid.
    /// </summary>
    internal static LiveFieldValue? FindFieldByOffsetOrContaining(
        IReadOnlyList<LiveFieldValue> fields, int wantOffset)
    {
        LiveFieldValue? exact = null, containing = null;
        foreach (var f in fields)
        {
            if (f.Offset == wantOffset) { exact = f; break; }
            if (f.Offset <= wantOffset && (containing == null || f.Offset > containing.Offset))
                containing = f;
        }
        return exact ?? containing;
    }

    /// <summary>
    /// Select + scroll the currently-displayed field list to the row at
    /// <paramref name="wantOffset"/> (exact, else the containing top-level field
    /// via <see cref="FindFieldByOffsetOrContaining"/>). Returns false when no row
    /// is at or before the offset. Shared by the UpdateDisplay scroll hint and the
    /// Locate-in-GWorld container-drill fallback.
    /// </summary>
    private bool ScrollToFieldByOffset(int wantOffset)
    {
        var hit = FindFieldByOffsetOrContaining(Fields, wantOffset);
        if (hit == null)
        {
            _log.Info($"ScrollToFieldByOffset: offset 0x{wantOffset:X} not found among top-level fields");
            _pendingDrillElementIndex = -1;
            return false;
        }
        SelectedField = hit;
        ScrollToFieldRequested?.Invoke(hit.Name);
        _log.Info($"ScrollToFieldByOffset: offset 0x{wantOffset:X} -> field '{hit.Name}' @0x{hit.Offset:X}");
        TryDrillIntoMatchedContainer(hit);
        return true;
    }

    internal static bool TryParseContainerPath(string? fieldName,
                                               out List<(string name, int index)> segments)
    {
        segments = new List<(string name, int index)>();
        if (string.IsNullOrEmpty(fieldName)) return false;

        bool hasIndex = false;
        foreach (var raw in fieldName.Split('.'))
        {
            if (raw.Length == 0) return false;                 // empty segment — malformed
            if (raw[^1] == ']')
            {
                int open = raw.LastIndexOf('[');
                if (open <= 0) return false;                   // "]" with no name before "["
                var idxStr = raw.Substring(open + 1, raw.Length - open - 2);
                if (!int.TryParse(idxStr, out var idx) || idx < 0) return false;  // "[]"/"[-1]"/non-numeric
                segments.Add((raw.Substring(0, open), idx));
                hasIndex = true;
            }
            else if (raw.IndexOf('[') >= 0)
            {
                return false;                                   // bracket not at the end — unexpected
            }
            else
            {
                segments.Add((raw, -1));
            }
        }
        return hasIndex;
    }

    /// <summary>
    /// From the currently-displayed owning object, drill an arbitrary-depth
    /// display path (parsed by <see cref="TryParseContainerPath"/>) to land ON the
    /// value. Each segment is either a direct struct field (descend by name) or a
    /// container element "Name[N]" (drill the container by name → select element
    /// [N]; if not the last segment, descend into the struct element to continue).
    /// The final segment is selected/scrolled to. Returns true on a full landing.
    /// Generalises the single-level struct-array drill to multi-"[N]" paths so
    /// deep Value Search / SPC candidates reach exactly — parity with the Instance
    /// Finder structured-chain drill (<see cref="DrillContainerChainAsync"/>).
    /// </summary>
    private async Task<bool> DrillDisplayPathAsync(List<(string name, int index)> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            bool isLast = i == segments.Count - 1;

            if (index >= 0)
            {
                // Container element: drill the container (by name) then select [N].
                var containerField = Fields.FirstOrDefault(f => f.Name == name && f.IsContainerNavigable);
                if (containerField == null) return false;
                await NavigateToContainerAsync(containerField);

                var elemRow = Fields.FirstOrDefault(f =>
                    f.Name == $"[{index}]" ||
                    f.Name.StartsWith($"[{index}] ", StringComparison.Ordinal));
                if (elemRow == null) return false;

                if (isLast)
                {
                    // The element itself is the value (leaf element, e.g. TArray<int>).
                    SelectedField = elemRow;
                    ScrollToFieldRequested?.Invoke(elemRow.Name);
                    return true;
                }
                // Descend into the struct element to reach the next segment.
                if (!elemRow.IsStructNavigation) return false;
                await NavigateToFieldAsync(elemRow);
            }
            else
            {
                // Direct struct field.
                if (isLast)
                {
                    var leaf = Fields.FirstOrDefault(f => f.Name == name);
                    if (leaf == null) return false;
                    SelectedField = leaf;
                    ScrollToFieldRequested?.Invoke(leaf.Name);
                    return true;
                }
                var structField = Fields.FirstOrDefault(f => f.Name == name && f.IsStructNavigation);
                if (structField == null) return false;
                await NavigateToFieldAsync(structField);
            }
        }
        return true;   // unreachable for a non-empty path (last segment always returns)
    }

    [RelayCommand]
    private void SaveBookmarkToSlot(BookmarkSlot? slot)
    {
        IsBookmarkSaveMode = false;
        if (slot == null || Breadcrumbs.Count == 0 || string.IsNullOrEmpty(CurrentAddress)) return;

        slot.SavedBreadcrumbs = Breadcrumbs.ToList();
        slot.SavedAddress = CurrentAddress;
        slot.SavedObjectName = CurrentObjectName;
        slot.SavedClassName = CurrentClassName;
        slot.SavedClassAddr = _currentClassAddr;
        slot.SavedCachedWorld = _cachedWorld;

        // Capture the selected rows (one or many) so loading re-selects them.
        // Prefer the multi-select snapshot; fall back to the single SelectedField
        // anchor (set when a row is clicked without a grid SelectionChanged sync).
        slot.SavedSelectedFields = _selectedFieldsSnapshot
            .Select(f => new BookmarkFieldRef(f.Name, f.Offset))
            .ToList();
        if (slot.SavedSelectedFields.Count == 0 && SelectedField != null)
            slot.SavedSelectedFields.Add(new BookmarkFieldRef(SelectedField.Name, SelectedField.Offset));

        // Capture the scroll anchor (topmost visible row; View fills this in synchronously).
        var anchor = new ViewAnchorRef();
        CaptureViewAnchor?.Invoke(anchor);
        slot.SavedTopRow = anchor.TopRow;

        // Truncate label for button display
        var label = !string.IsNullOrEmpty(CurrentObjectName) ? CurrentObjectName : CurrentClassName;
        if (label.Length > 14) label = label[..14] + "..";
        slot.Label = label;
        slot.IsOccupied = true;  // also refreshes the computed TooltipText

        StatusText = $"Bookmark {slot.DisplayNumber} saved";
        var topName = slot.SavedTopRow?.Name ?? "-";
        _log.Info($"Bookmark saved slot={slot.SlotIndex} addr={CurrentAddress} name={CurrentObjectName} sel={slot.SavedSelectedFields.Count} top={topName}");
    }

    [RelayCommand]
    private void ToggleBookmarkSaveMode()
    {
        if (Breadcrumbs.Count == 0 || string.IsNullOrEmpty(CurrentAddress))
            return;
        IsBookmarkSaveMode = !IsBookmarkSaveMode;
    }

    [RelayCommand]
    private void CancelBookmarkSave()
    {
        IsBookmarkSaveMode = false;
    }

    [RelayCommand]
    private async Task LoadBookmarkAsync(BookmarkSlot? slot)
    {
        if (slot == null) return;

        // If in save mode, redirect to save instead of loading
        if (IsBookmarkSaveMode)
        {
            SaveBookmarkToSlot(slot);
            return;
        }

        if (!slot.IsOccupied) return;

        try
        {
            ClearStatus();
            IsLoading = true;
            StopAutoRefreshTimer();

            // Save current state for Back-after-bookmark
            if (Breadcrumbs.Count > 0)
            {
                _preBookmarkBreadcrumbs = Breadcrumbs.ToList();
                _preBookmarkAddress = CurrentAddress;
                _preBookmarkCachedWorld = _cachedWorld;
            }

            // Restore breadcrumbs
            Breadcrumbs.Clear();
            foreach (var bc in slot.SavedBreadcrumbs)
                Breadcrumbs.Add(bc);

            _cachedWorld = slot.SavedCachedWorld;

            // Re-display the saved view. Branches are mutually exclusive and all
            // rebuild Fields, so selection + scroll restore happens once at the end.
            var lastBc = Breadcrumbs.LastOrDefault();
            if (lastBc != null)
            {
                if (lastBc.IsContainerView && lastBc.ContainerField != null)
                {
                    RepopulateContainerView(lastBc.ContainerField, lastBc);
                }
                else if (lastBc.IsContainerView && lastBc.ContainerField == null
                         && await TryRepopulateSyntheticContainerAsync(lastBc))
                {
                    // Path-synthetic container crumb (no live ContainerField): re-hydrated
                    // from a live parent walk inside the condition — mirrors the 3 Back-nav
                    // dispatch sites so a bookmark saved on such a view restores the array
                    // element view, not the parent object grid. Falls through to the walk
                    // below when no live match (graceful degradation).
                }
                else if (IsGWorldActorListRoot(lastBc))
                {
                    // Only the genuine GWorld root re-shows the actor list — a deeper
                    // OwningWorld crumb at the same address is walked as an instance.
                    PopulateFromWorld(_cachedWorld!);
                }
                else
                {
                    var classAddr = string.IsNullOrEmpty(lastBc.ClassAddr) ? null : lastBc.ClassAddr;
                    var result = await _dump.WalkInstanceAsync(
                        lastBc.Address, classAddr,
                        arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
                    result = await AutoFillGapsRetryAsync(result, lastBc.Address, classAddr);
                    UpdateDisplay(result);
                }
            }

            // Re-select the rows the user had selected + restore the scroll position.
            RestoreBookmarkView?.Invoke(slot.SavedSelectedFields, slot.SavedTopRow);

            StatusText = $"Bookmark {slot.DisplayNumber} loaded";
            var topName = slot.SavedTopRow?.Name ?? "-";
            _log.Info($"Bookmark loaded slot={slot.SlotIndex} addr={slot.SavedAddress} sel={slot.SavedSelectedFields.Count} top={topName}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = $"Bookmark {slot.DisplayNumber} invalid — address may no longer be valid";
            _log.Error($"Bookmark load failed slot={slot.SlotIndex}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ClearBookmark(BookmarkSlot? slot)
    {
        if (slot == null) return;
        slot.IsOccupied = false;  // also refreshes the computed TooltipText (empty hint)
        slot.Label = "";
        slot.SavedBreadcrumbs.Clear();
        slot.SavedAddress = "";
        slot.SavedObjectName = "";
        slot.SavedClassName = "";
        slot.SavedClassAddr = "";
        slot.SavedCachedWorld = null;
        slot.SavedSelectedFields.Clear();
        slot.SavedTopRow = null;
    }

    /// <summary>Clear all bookmark slots (called on disconnect).</summary>
    public void ClearAllBookmarks()
    {
        foreach (var slot in BookmarkSlots)
            ClearBookmark(slot);
        _preBookmarkBreadcrumbs = null;
        _preBookmarkAddress = "";
        _preBookmarkCachedWorld = null;
        IsBookmarkSaveMode = false;
    }

    [RelayCommand]
    private async Task ExportCeXmlAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress) || Breadcrumbs.Count == 0) return;

        try
        {
            ClearStatus();
            IsLoading = true;

            // Container view: strip container breadcrumb, use original ContainerField.
            // Container breadcrumbs share the parent's Address, which causes CleanBreadcrumbs
            // to falsely detect a cycle and remove them. Using parent breadcrumbs + ContainerField
            // lets EmitFields dispatch to EmitMapProperty/EmitArrayProperty/EmitSetProperty correctly.
            var lastBc = Breadcrumbs[^1];
            var isContainerView = lastBc.IsContainerView && lastBc.ContainerField != null;
            var breadcrumbsForXml = isContainerView
                ? (IReadOnlyList<BreadcrumbItem>)Breadcrumbs.Take(Breadcrumbs.Count - 1).ToList()
                : Breadcrumbs;
            var fieldsForXml = isContainerView
                ? new List<LiveFieldValue> { lastBc.ContainerField! }
                : new List<LiveFieldValue>(Fields);

            _log.Info($"CEXML export: containerView={isContainerView} bcCount={breadcrumbsForXml.Count} | BC={FormatBreadcrumbTrace()}");

            // Pre-check CleanBreadcrumbs to log any cycle removals
            var cleaned = CeXmlExportService.CleanBreadcrumbs(breadcrumbsForXml);
            if (cleaned.Count != breadcrumbsForXml.Count)
            {
                _log.Info($"CEXML CleanBC: {breadcrumbsForXml.Count}→{cleaned.Count} removed={breadcrumbsForXml.Count - cleaned.Count}");
                for (int i = 0; i < cleaned.Count; i++)
                {
                    var bc = cleaned[i];
                    var flags = bc.IsContainerView ? "C" : bc.IsPointerDeref ? "P" : "S";
                    _log.Info($"  [{i}] {bc.FieldName ?? bc.Label} ({flags}) off=0x{bc.FieldOffset:X} addr={bc.Address}");
                }
            }

            // Unified drilldown resolve (docs/ce-export-drilldown-spec.md Phase A):
            // structs (flatten) + pointers + CONTAINER ELEMENT VALUES (Map/Set/struct-
            // array values that are themselves structs/objects), recursively to
            // CsxDrilldownDepth — so a Map<Name, Struct> / Set<Struct> / nested
            // Map-of-Struct expands in the export, matching what the UI can drill.
            StatusText = CsxDrilldownDepth > 0
                ? "Resolving struct + pointer + container fields..."
                : "Resolving struct fields...";
            var resolvedStructs = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
            var resolvedInstances = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
            int lastShown = 0;
            await CeXmlExportService.ResolveDrilldownAsync(
                _dump, fieldsForXml, resolvedStructs, resolvedInstances,
                depth: CsxDrilldownDepth, arrayLimit: ArrayLimit,
                onWalk: () =>
                {
                    // Live indicator: objects (structs + pointer targets) resolved so far,
                    // throttled so a deep/wide map doesn't spam the bound StatusText.
                    int n = resolvedStructs.Count + resolvedInstances.Count;
                    if (n - lastShown >= 16) { lastShown = n; StatusText = $"Resolving… {n} objects"; }
                });

            var rootBc = breadcrumbsForXml[0];

            // AOB mode requires a GWorld-rooted breadcrumb chain. When the object was
            // opened via Instance Finder / Address Lookup there is no GWorld→object path,
            // so we fall back to direct-address mode to avoid generating a wrong base.
            var isGWorldRoot = rootBc.FieldName == "GWorld";
            var useAob = UseAobSymbol && isGWorldRoot && !string.IsNullOrEmpty(_engineState?.GWorldAob);
            if (UseAobSymbol && !isGWorldRoot)
                _log.Info("CEXML: AOB requested but root is not GWorld — falling back to direct address");

            StatusText = "Generating CE XML...";
            string xml;
            if (useAob)
            {
                xml = CeXmlExportService.GenerateAobWrappedXml(
                    rootBc.Label, breadcrumbsForXml, fieldsForXml,
                    _engineState!.GWorldAob, _engineState.GWorldAobPos, _engineState.GWorldAobLen,
                    _engineState.ModuleName,
                    resolvedStructs,
                    collapsePointerNodes: CollapsePointerNodes,
                    maxDropDownEntries: DropDownLimit,
                    resolvedInstances: resolvedInstances,
                    flattenChain: CollapseChain);
            }
            else
            {
                var rootAddress = AddressHelper.FormatAddress(
                    rootBc.Address, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
                xml = CeXmlExportService.GenerateHierarchicalXml(
                    rootAddress, rootBc.Label, breadcrumbsForXml, fieldsForXml, resolvedStructs,
                    collapsePointerNodes: CollapsePointerNodes,
                    maxDropDownEntries: DropDownLimit,
                    resolvedInstances: resolvedInstances,
                    flattenChain: CollapseChain);
            }

            await _platform.CopyToClipboardAsync(xml);
            var limitWarn = BuildContainerLimitWarning(fieldsForXml, ArrayLimit);
            var aobFallbackWarn = (UseAobSymbol && !isGWorldRoot) ? "AOB skipped (no GWorld path)" : null;
            // Final indicator: objects (structs + pointer targets) walked + XML line count.
            int objCount = resolvedStructs.Count + resolvedInstances.Count;
            int lineCount = xml.Count(c => c == '\n') + 1;
            var statusExtra = aobFallbackWarn != null ? " " + aobFallbackWarn
                : (limitWarn != null ? " " + limitWarn : "");
            StatusText = $"Copied: {objCount} objects, {lineCount} XML lines.{statusExtra}";
            _log.Info($"CE XML copied to clipboard for {CurrentClassName} (AOB={useAob}, " +
                $"{resolvedStructs.Count} structs / {resolvedInstances.Count} pointers resolved, depth={CsxDrilldownDepth})");
        }
        catch (Exception ex)
        {
            StatusText = "";
            SetError(ex);
            _log.Error("Failed to export CE XML", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsxAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress) || !HasData) return;

        try
        {
            ClearStatus();

            // Build struct name: "ClassName_ObjectName" or "ClassName"
            var structName = !string.IsNullOrEmpty(CurrentObjectName)
                ? $"{CurrentClassName}_{CurrentObjectName}".Replace(" ", "_")
                : CurrentClassName.Replace(" ", "_");
            // Sanitize for file name and XML attribute
            structName = structName.Replace("<", "").Replace(">", "").Replace("\"", "");
            // Sanitize for file system: remove invalid chars
            var safeFileName = string.Join("_",
                structName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

            // Show save-file dialog; user picks folder + file name
            var filePath = await _platform.ShowSaveFileDialogAsync(
                safeFileName, "CE Structure Dissect (*.CSX)", ".CSX");
            if (string.IsNullOrEmpty(filePath)) return; // user cancelled

            IsLoading = true;
            StatusText = CsxDrilldownDepth > 0 ? "Resolving struct + pointer fields..." : "Resolving struct fields...";
            var csx = await CsxExportService.GenerateCsxAsync(
                _dump, structName, Fields, arrayLimit: ArrayLimit, drilldownDepth: CsxDrilldownDepth);

            // Write to file (overwrite if exists — user already confirmed via dialog)
            await File.WriteAllTextAsync(filePath, csx);

            // Surface a truncation note so a partial export (a container clipped by
            // ArrayLimit) doesn't silently read as complete — same note Copy CE XML shows.
            var limitWarn = BuildContainerLimitWarning(Fields, ArrayLimit);
            StatusText = limitWarn ?? "";
            _log.Info($"CSX exported to {filePath} for {CurrentClassName}"
                + (limitWarn != null ? $" ({limitWarn})" : ""));
        }
        catch (UnauthorizedAccessException)
        {
            StatusText = "";
            SetError("Cannot write to the selected location — access denied.");
            _log.Error("CSX export failed: access denied");
        }
        catch (Exception ex)
        {
            StatusText = "";
            SetError(ex);
            _log.Error("Failed to export CSX", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportCeFieldXmlAsync()
    {
        // Use the multi-selection snapshot; fall back to SelectedField for
        // robustness if SelectionChanged hasn't synced yet (e.g. when the
        // command fires programmatically right after a single-row selection).
        var selectedSnapshot = _selectedFieldsSnapshot.Count > 0
            ? new List<LiveFieldValue>(_selectedFieldsSnapshot)
            : (SelectedField != null ? new List<LiveFieldValue> { SelectedField } : new List<LiveFieldValue>());

        if (selectedSnapshot.Count == 0 || string.IsNullOrEmpty(CurrentAddress) || Breadcrumbs.Count == 0) return;

        try
        {
            ClearStatus();
            IsLoading = true;

            // Collapse consecutive duplicate crumbs FIRST (before the container
            // split below), so a redundant trailing container crumb — e.g. a
            // Locate-in-GWorld path-synthetic SpawnedAttributes(C) followed by the
            // user re-entering that same container — doesn't leave one copy in the
            // spine while the other becomes the field (which double-derefs the array
            // field offset). The later duplicate is kept (it carries the live
            // ContainerField needed by FilterContainerToElement).
            var dedupedBc = CeXmlExportService.DedupeConsecutiveBreadcrumbs(Breadcrumbs);

            // Container view: strip container breadcrumb, build ONE filtered
            // ContainerField containing all selected elements (preserves CE's
            // hierarchical structure — header + nested elements under same
            // pointer chain — instead of N detached top-level entries).
            var lastBc = dedupedBc[^1];
            var isContainerView = lastBc.IsContainerView && lastBc.ContainerField != null;

            IReadOnlyList<BreadcrumbItem> breadcrumbsForXml;
            List<LiveFieldValue> fieldsForXml;

            // Struct-array elements: in the array container view each element row is a
            // StructProperty navigation (StructDataAddr = element address, StructClassAddr =
            // element UScriptStruct). The shallow read_array_elements preview only carries
            // scalar/pointer sub-fields, so the FilterContainerToElement path below would drop
            // nested struct/map fields. Instead keep the array breadcrumb (its Offsets=[0]
            // derefs TArray::Data) and export the selected element rows AS struct fields, so
            // ResolveStructFieldsAsync re-walks each element in full — nested structs/maps
            // expand exactly like drilling into the element.
            bool isStructElementSelection = isContainerView
                && lastBc.ContainerField!.ArrayInnerType == "StructProperty"
                && !string.IsNullOrEmpty(lastBc.ContainerField.ArrayStructClassAddr)
                && selectedSnapshot.Count > 0
                && selectedSnapshot.All(f => f.TypeName == "StructProperty"
                                             && !string.IsNullOrEmpty(f.StructDataAddr));

            if (isStructElementSelection)
            {
                breadcrumbsForXml = dedupedBc;
                fieldsForXml = selectedSnapshot;
            }
            else if (isContainerView)
            {
                breadcrumbsForXml = dedupedBc.Take(dedupedBc.Count - 1).ToList();
                fieldsForXml = new List<LiveFieldValue>
                    { FilterContainerToElement(lastBc.ContainerField!, selectedSnapshot) };
            }
            else
            {
                breadcrumbsForXml = dedupedBc;
                fieldsForXml = selectedSnapshot;
            }

            var fieldSummary = selectedSnapshot.Count == 1
                ? $"field={selectedSnapshot[0].Name}"
                : $"fields={selectedSnapshot.Count}({string.Join(",", selectedSnapshot.Take(5).Select(f => f.Name))}{(selectedSnapshot.Count > 5 ? "…" : "")})";
            _log.Info($"CEFieldXML export: {fieldSummary} containerView={isContainerView} bcCount={breadcrumbsForXml.Count} | BC={FormatBreadcrumbTrace()}");

            // Pre-check CleanBreadcrumbs to log any cycle removals
            var cleaned = CeXmlExportService.CleanBreadcrumbs(breadcrumbsForXml);
            if (cleaned.Count != breadcrumbsForXml.Count)
            {
                _log.Info($"CEFieldXML CleanBC: {breadcrumbsForXml.Count}→{cleaned.Count} removed={breadcrumbsForXml.Count - cleaned.Count}");
                for (int i = 0; i < cleaned.Count; i++)
                {
                    var bc = cleaned[i];
                    var flags = bc.IsContainerView ? "C" : bc.IsPointerDeref ? "P" : "S";
                    _log.Info($"  [{i}] {bc.FieldName ?? bc.Label} ({flags}) off=0x{bc.FieldOffset:X} addr={bc.Address}");
                }
            }

            // Unified drilldown resolve (docs/ce-export-drilldown-spec.md Phase A) —
            // structs + pointers + container element values (Map/Set/struct-array
            // struct/object values), recursively to CsxDrilldownDepth.
            StatusText = CsxDrilldownDepth > 0
                ? "Resolving struct + pointer + container fields..."
                : "Resolving struct fields...";
            var resolvedStructs = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
            var resolvedInstances = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
            int lastShown = 0;
            await CeXmlExportService.ResolveDrilldownAsync(
                _dump, fieldsForXml, resolvedStructs, resolvedInstances,
                depth: CsxDrilldownDepth, arrayLimit: ArrayLimit,
                onWalk: () =>
                {
                    // Live indicator: objects (structs + pointer targets) resolved so far,
                    // throttled so a deep/wide map doesn't spam the bound StatusText.
                    int n = resolvedStructs.Count + resolvedInstances.Count;
                    if (n - lastShown >= 16) { lastShown = n; StatusText = $"Resolving… {n} objects"; }
                });

            var rootBc = breadcrumbsForXml[0];

            // Same GWorld-root guard as ExportCeXmlAsync
            var isGWorldRoot = rootBc.FieldName == "GWorld";
            var useAob = UseAobSymbol && isGWorldRoot && !string.IsNullOrEmpty(_engineState?.GWorldAob);
            if (UseAobSymbol && !isGWorldRoot)
                _log.Info("CEFieldXML: AOB requested but root is not GWorld — falling back to direct address");

            StatusText = "Generating CE Field XML...";
            string xml;
            if (useAob)
            {
                xml = CeXmlExportService.GenerateAobWrappedXml(
                    rootBc.Label, breadcrumbsForXml, fieldsForXml,
                    _engineState!.GWorldAob, _engineState.GWorldAobPos, _engineState.GWorldAobLen,
                    _engineState.ModuleName,
                    resolvedStructs,
                    collapsePointerNodes: CollapsePointerNodes,
                    maxDropDownEntries: DropDownLimit,
                    resolvedInstances: resolvedInstances,
                    flattenChain: CollapseChain);
            }
            else
            {
                var rootAddress = AddressHelper.FormatAddress(
                    rootBc.Address, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
                xml = CeXmlExportService.GenerateHierarchicalXml(
                    rootAddress, rootBc.Label, breadcrumbsForXml, fieldsForXml, resolvedStructs,
                    collapsePointerNodes: CollapsePointerNodes,
                    maxDropDownEntries: DropDownLimit,
                    resolvedInstances: resolvedInstances,
                    flattenChain: CollapseChain);
            }

            await _platform.CopyToClipboardAsync(xml);
            var limitWarn = BuildContainerLimitWarning(fieldsForXml, ArrayLimit);
            var aobFallbackWarn = (UseAobSymbol && !isGWorldRoot) ? "AOB skipped (no GWorld path)" : null;
            // Final indicator: objects (structs + pointer targets) walked + XML line count.
            int objCount = resolvedStructs.Count + resolvedInstances.Count;
            int lineCount = xml.Count(c => c == '\n') + 1;
            var statusExtra = aobFallbackWarn != null ? " " + aobFallbackWarn
                : (limitWarn != null ? " " + limitWarn : "");
            StatusText = $"Copied: {objCount} objects, {lineCount} XML lines.{statusExtra}";
            _log.Info($"CE Field XML copied: {selectedSnapshot.Count} field(s) (AOB={useAob}, " +
                $"{resolvedInstances.Count} pointer targets resolved at depth={CsxDrilldownDepth})");
        }
        catch (Exception ex)
        {
            StatusText = "";
            SetError(ex);
            _log.Error("Failed to export CE Field XML", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Direct-push variant of Copy CE Field: instead of generating CE XML to the clipboard,
    /// push each selected field straight into CE's address list as a typed memory record via
    /// the AOBMaker plugin (the multi-select batch form of the per-row +CE button). This is a
    /// FLAT push — one top-level record per selected field, typed via
    /// <see cref="CeXmlExportService.MapFieldToCeRecordType"/>. It intentionally does NOT
    /// reproduce the hierarchical pointer-chain / container-element structure that
    /// <see cref="ExportCeFieldXmlAsync"/> builds for the clipboard; use Copy CE Field for that.
    /// </summary>
    [RelayCommand]
    private async Task PushCeFieldToCeAsync()
    {
        if (_aobMaker == null) return;

        // Same selection source as ExportCeFieldXmlAsync (snapshot, falling back to the
        // single selected row if SelectionChanged hasn't synced yet).
        var selected = _selectedFieldsSnapshot.Count > 0
            ? new List<LiveFieldValue>(_selectedFieldsSnapshot)
            : (SelectedField != null ? new List<LiveFieldValue> { SelectedField } : new List<LiveFieldValue>());
        if (selected.Count == 0)
        {
            StatusText = "No fields selected";
            return;
        }

        try
        {
            ClearStatus();
            int ok = 0, fail = 0, skipped = 0;
            foreach (var field in selected)
            {
                // Fields without a resolved address (e.g. container/struct headers) can't
                // become a flat record — skip rather than push a bogus address.
                if (string.IsNullOrEmpty(field.FieldAddress)) { skipped++; continue; }

                var t = CeXmlExportService.MapFieldToCeRecordType(field);
                var added = await _aobMaker.CreateMemoryRecordAsync(
                    Services.PackedLayoutNotice.RecordNamePrefix + field.Name,
                    StripHexPrefix(field.FieldAddress), t.ValueType, t.IsSigned, t.ShowAsHex);
                if (added)
                {
                    ok++;
                }
                else
                {
                    fail++;
                    // If the bridge lost the pipe (CE closed mid-batch) stop now rather than
                    // eating one 2 s connect timeout per remaining field.
                    if (!_aobMaker.IsAvailable) break;
                }
            }

            IsAobMakerAvailable = _aobMaker.IsAvailable;
            if (!_aobMaker.IsAvailable && ok == 0)
            {
                StatusText = "AOBMaker not connected — open CE with the plugin loaded";
            }
            else
            {
                var extra = (fail > 0 ? $", {fail} failed" : "") + (skipped > 0 ? $", {skipped} skipped" : "");
                StatusText = $"Added to CE: {ok} record(s){extra}";
            }
            _log.Info($"CE Field push: {ok} added, {fail} failed, {skipped} skipped (of {selected.Count} selected)");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Failed to push CE Field records to CE", ex);
        }
    }

    /// <summary>
    /// Compute CE-compatible "Module.exe"+RVA string from an absolute address.
    /// </summary>
    private string ComputeModuleRva(string hexAddr)
    {
        var addr = Convert.ToUInt64(hexAddr.Replace("0x", "").Replace("0X", ""), 16);
        var moduleBase = Convert.ToUInt64(_engineState!.ModuleBase.Replace("0x", "").Replace("0X", ""), 16);
        var rva = addr - moduleBase;
        return $"\"{_engineState.ModuleName}\"+{rva:X}";
    }

    [RelayCommand]
    private async Task GenerateCeAAScriptAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress)) return;

        try
        {
            ClearStatus();
            var symbolName = !string.IsNullOrEmpty(CurrentClassName)
                ? CurrentClassName.Replace(" ", "_").Replace("-", "_")
                : "UE5_Symbol";

            var formattedAddr = AddressHelper.FormatAddress(
                CurrentAddress, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);

            var xml = CeXmlExportService.GenerateRegisterSymbolXml(symbolName, formattedAddr);

            await _platform.CopyToClipboardAsync(xml);
            _log.Info($"CE AA script copied to clipboard for {CurrentClassName}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Failed to generate CE AA script", ex);
        }
    }

    [RelayCommand]
    private async Task ExportSdkHeaderAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress) || !HasData) return;

        try
        {
            ClearStatus();

            var structName = !string.IsNullOrEmpty(CurrentObjectName)
                ? $"{CurrentClassName}_{CurrentObjectName}".Replace(" ", "_")
                : CurrentClassName.Replace(" ", "_");
            structName = structName.Replace("<", "").Replace(">", "").Replace("\"", "");
            var safeFileName = string.Join("_",
                structName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

            var filePath = await _platform.ShowSaveFileDialogAsync(
                safeFileName, "C++ Header (*.h)", ".h");
            if (string.IsNullOrEmpty(filePath)) return;

            IsLoading = true;
            StatusText = "Generating SDK header...";

            // Get the superclass name from the first breadcrumb's class info if available
            var superName = "";
            if (Breadcrumbs.Count > 0)
            {
                var bc = Breadcrumbs[^1];
                if (!string.IsNullOrEmpty(bc.ClassAddr))
                {
                    try
                    {
                        var classInfo = await _dump.WalkClassAsync(bc.ClassAddr);
                        superName = classInfo.SuperName;
                    }
                    catch
                    {
                        // Non-critical — just emit without super
                    }
                }
            }

            // Estimate properties size from the last field end or use a safe heuristic
            var propsSize = 0;
            if (Fields.Count > 0)
            {
                var lastField = Fields.OrderByDescending(f => f.Offset + f.Size).First();
                propsSize = lastField.Offset + lastField.Size;
            }

            var header = SdkExportService.GenerateClassHeader(
                CurrentClassName, superName, propsSize, Fields.ToList());

            await File.WriteAllTextAsync(filePath, header);

            StatusText = "";
            _log.Info($"SDK header exported to {filePath} for {CurrentClassName}");
        }
        catch (UnauthorizedAccessException)
        {
            StatusText = "";
            SetError("Cannot write to the selected location — access denied.");
            _log.Error("SDK header export failed: access denied");
        }
        catch (Exception ex)
        {
            StatusText = "";
            SetError(ex);
            _log.Error("Failed to export SDK header", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress)) return;

        // Re-check AOBMaker CE Plugin availability (detects CE start/close, cooldown-throttled)
        TryCheckAobMaker();

        // Snapshot address before async call — if user navigates while we're awaiting,
        // CurrentAddress will differ and we discard the stale result.
        var addressAtStart = CurrentAddress;
        var breadcrumbCountAtStart = Breadcrumbs.Count;

        // Remember the selected row so a refresh (manual or auto) lands back on
        // it instead of resetting to the top. UpdateDisplay either replaces the
        // field objects in-place (drops the selection binding) or fully rebuilds
        // (drops scroll too); restoring by name+offset covers both. Empty when
        // nothing is selected, so we never yank an un-selected list around.
        var keepFieldName   = SelectedField?.Name;
        var keepFieldOffset = SelectedField?.Offset ?? int.MinValue;

        // Hard deadline: if the DLL hangs walking a recycled/destroyed object,
        // cancel the pipe request instead of leaving IsLoading stuck forever.
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(Constants.LiveWalkerRefreshTimeoutMs));
        var ct = timeoutCts.Token;

        try
        {
            ClearStatus();
            IsLoading = true;

            // If refreshing a container view, re-walk the parent instance and re-extract container data.
            // Path-synthetic container crumbs (from PathStepToBreadcrumbs) carry no live ContainerField,
            // so match by the crumb's own field name+offset in that case — otherwise refresh would skip
            // this branch and re-walk a stale address, reverting the re-hydrated container to a grid
            // (mirrors TryRepopulateSyntheticContainerAsync on the Back-nav side).
            if (Breadcrumbs.Count > 0 && Breadcrumbs[^1].IsContainerView)
            {
                var containerBc = Breadcrumbs[^1];

                // DataTable container: re-fetch rows directly
                if (containerBc.IsDataTableView)
                {
                    var dtResult = await _dump.WalkDataTableRowsAsync(containerBc.Address, ct: ct);
                    if (CurrentAddress != addressAtStart || Breadcrumbs.Count != breadcrumbCountAtStart) return;
                    containerBc.DataTableData = dtResult;
                    PopulateDataTableRowFields(dtResult);
                    return;
                }

                // Re-walk the parent instance to get fresh container data
                string? parentClassAddr = null;
                if (Breadcrumbs.Count >= 2)
                {
                    var parentBc = Breadcrumbs[^2];
                    if (!string.IsNullOrEmpty(parentBc.ClassAddr))
                        parentClassAddr = parentBc.ClassAddr;
                }

                var parentResult = await _dump.WalkInstanceAsync(containerBc.Address, parentClassAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps, ct: ct);
                if (CurrentAddress != addressAtStart || Breadcrumbs.Count != breadcrumbCountAtStart) return;

                // Find the container field by name and offset in the refreshed result. Use the live
                // ContainerField identity when present, else the crumb's own field name+offset.
                var matchName   = containerBc.ContainerField?.Name   ?? containerBc.FieldName;
                var matchOffset = containerBc.ContainerField?.Offset ?? containerBc.FieldOffset;
                var updatedField = parentResult.Fields
                    .FirstOrDefault(f => f.Name == matchName && f.Offset == matchOffset);

                if (updatedField != null)
                {
                    RepopulateContainerView(updatedField);
                    RestoreSelectedField(keepFieldName, keepFieldOffset);
                }
                return;
            }

            // If refreshing GWorld view (first breadcrumb only), re-fetch the world.
            // Must check Breadcrumbs.Count == 1 because a sub-World (e.g. S01L04) can share
            // the same address as GWorld — without this guard, auto-refresh at deeper levels
            // would incorrectly show the GWorld actor list instead of instance fields.
            if (_cachedWorld != null && CurrentAddress == _cachedWorld.WorldAddr
                && Breadcrumbs.Count == 1)
            {
                var world = await _dump.WalkWorldAsync(500, arrayLimit: ArrayLimit, ct: ct);
                if (CurrentAddress != addressAtStart || Breadcrumbs.Count != breadcrumbCountAtStart) return;
                _cachedWorld = world;
                PopulateFromWorld(world);
                RestoreSelectedField(keepFieldName, keepFieldOffset);
                return;
            }

            // Pass ClassAddr from current breadcrumb (needed for StructProperty context;
            // without it the DLL interprets struct memory as UObject → garbage → empty grid)
            string? classAddr = null;
            if (Breadcrumbs.Count > 0)
            {
                var current = Breadcrumbs[^1];
                if (!string.IsNullOrEmpty(current.ClassAddr))
                    classAddr = current.ClassAddr;
            }

            var result = await _dump.WalkInstanceAsync(CurrentAddress, classAddr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps, ct: ct);
            result = await AutoFillGapsRetryAsync(result, CurrentAddress, classAddr);
            if (CurrentAddress != addressAtStart || Breadcrumbs.Count != breadcrumbCountAtStart) return;
            UpdateDisplay(result);
            RestoreSelectedField(keepFieldName, keepFieldOffset);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            SetError(new TimeoutException(
                $"Refresh timed out after {Constants.LiveWalkerRefreshTimeoutMs / 1000}s — " +
                "the target object may have been destroyed in-game."));
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// After a refresh rebuilds the field grid, re-select the row that was
    /// selected before (matched by name, preferring the same byte offset) and
    /// scroll it back into view — so Refresh / auto-refresh doesn't reset to the
    /// top. No-op when nothing was selected or the field is gone.
    /// </summary>
    private void RestoreSelectedField(string? name, int offset)
    {
        if (string.IsNullOrEmpty(name)) return;

        LiveFieldValue? exact = null, byName = null;
        foreach (var f in Fields)
        {
            if (f.Name != name) continue;
            byName ??= f;
            if (f.Offset == offset) { exact = f; break; }
        }

        var hit = exact ?? byName;
        if (hit == null) return;

        SelectedField = hit;
        ScrollToFieldRequested?.Invoke(hit.Name);
    }

    [RelayCommand]
    private async Task CopyFieldAddressAsync(LiveFieldValue? field)
    {
        if (field == null) return;

        try
        {
            // Prefer the field's already-resolved absolute address (the same value
            // shown in the Address column and used by the Hex / +CE / Edit buttons).
            // Only fall back to CurrentAddress + Offset when it's missing:
            // CurrentAddress is the OWNING struct, which is WRONG for a container-
            // element view — the element lives in a separate heap buffer
            // (TArray::Data / TSparseArray::Data), not at owner+Offset. Recomputing
            // there landed on the owning struct's field at the same offset instead.
            string hexAddr;
            if (!string.IsNullOrEmpty(field.FieldAddress) && field.FieldAddress != "0x0")
            {
                hexAddr = field.FieldAddress;
            }
            else if (!string.IsNullOrEmpty(CurrentAddress))
            {
                var instanceAddr = Convert.ToUInt64(CurrentAddress.Replace("0x", "").Replace("0X", ""), 16);
                hexAddr = $"0x{instanceAddr + (ulong)field.Offset:X}";
            }
            else return;

            var formatted = AddressHelper.FormatAddress(
                hexAddr, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy address for {field.Name}", ex);
        }
    }

    [RelayCommand]
    private async Task CopyFieldNameAsync(LiveFieldValue? field)
    {
        if (field == null || string.IsNullOrEmpty(field.Name)) return;

        try
        {
            await _platform.CopyToClipboardAsync(field.Name);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy name for {field.Name}", ex);
        }
    }

    [RelayCommand]
    private async Task CopyPtrAddressAsync(LiveFieldValue? field)
    {
        if (field == null || string.IsNullOrEmpty(field.PtrAddress)) return;

        try
        {
            var formatted = AddressHelper.FormatAddress(
                field.PtrAddress, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy ptr address for {field.Name}", ex);
        }
    }

    // --- AOBMaker CE Plugin: hex view navigation ---

    /// <summary>Check AOBMaker availability (called after data load).</summary>
    public async Task CheckAobMakerAsync()
    {
        if (_aobMaker == null) return;
        _lastAobMakerCheck = DateTime.UtcNow;
        try
        {
            IsAobMakerAvailable = await _aobMaker.CheckAvailabilityAsync();
        }
        catch { IsAobMakerAvailable = false; }
    }

    /// <summary>
    /// Fire-and-forget AOBMaker availability check with cooldown.
    /// Detects both CE starting (buttons enable) and CE closing (buttons disable).
    /// Skips if last check was within <see cref="AobMakerCheckCooldown"/> to avoid
    /// spamming pipe connects on rapid navigation (2s timeout when CE not running).
    /// Public so MainWindow's tab-switch handler can also re-check on tab activation.
    /// </summary>
    public void TryCheckAobMaker()
    {
        if (_aobMaker == null) return;
        if (DateTime.UtcNow - _lastAobMakerCheck < AobMakerCheckCooldown) return;
        _ = CheckAobMakerAsync();
    }

    /// <summary>Strip leading "0x" prefix for AOBMaker hex navigation.</summary>
    private static string StripHexPrefix(string addr)
        => addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addr[2..] : addr;

    // --- Live Edit Mode ---

    /// <summary>Whether a field value is currently being edited. Suppresses auto-refresh.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Commit an inline field edit: validate, convert to bytes, write to game memory.
    /// </summary>
    public async Task CommitFieldEditAsync(LiveFieldValue field, string newValue)
    {
        if (field == null || string.IsNullOrEmpty(field.FieldAddress)) return;

        try
        {
            ClearStatus();

            // BoolProperty: read-modify-write with bitmask
            if (field.TypeName == "BoolProperty")
            {
                if (!FieldValueConverter.TryParseBool(newValue, out var boolVal))
                {
                    StatusText = $"Invalid bool value: {newValue} (expected true/false/1/0)";
                    return;
                }

                // Write address = field address + boolByteOffset
                var baseAddr = Convert.ToUInt64(
                    field.FieldAddress.Replace("0x", "").Replace("0X", ""), 16);
                var writeAddr = $"0x{baseAddr + (ulong)field.BoolByteOffset:X}";

                // Read current byte, apply mask, write back
                var currentBytes = await _dump.ReadMemAsync(writeAddr, 1);
                var modified = FieldValueConverter.ApplyBoolMask(
                    currentBytes[0], field.BoolFieldMask, boolVal);

                await _dump.WriteMemAsync(writeAddr, new[] { modified });
            }
            else
            {
                // Standard scalar / enum conversion
                var (success, data, error) = FieldValueConverter.TryConvert(
                    field.TypeName, newValue, field.Size, field.EnumEntries);

                if (!success)
                {
                    StatusText = $"Invalid value for {field.Name}: {error}";
                    return;
                }

                await _dump.WriteMemAsync(field.FieldAddress, data);
            }

            // Refresh to show updated value, then restore selection to the edited row
            var editedName = field.Name;
            var editedOffset = field.Offset;
            await RefreshAsync();

            var restored = Fields?.FirstOrDefault(f => f.Name == editedName && f.Offset == editedOffset);
            if (restored != null)
                SelectedField = restored;

            StatusText = $"Written: {field.Name} = {newValue}";
            _log.Info($"EDIT {field.Name} ({field.TypeName}) @ {field.FieldAddress} = {newValue}");
        }
        catch (Exception ex)
        {
            StatusText = $"Write failed for {field.Name}: {ex.Message}";
            _log.Error($"Failed to write {field.Name} @ {field.FieldAddress}", ex);
        }
    }

    [RelayCommand]
    private async Task HexFieldAddressAsync(LiveFieldValue? field)
    {
        if (_aobMaker == null || field == null || string.IsNullOrEmpty(field.FieldAddress)) return;
        try
        {
            await _aobMaker.NavigateHexViewAsync(StripHexPrefix(field.FieldAddress));
        }
        catch (Exception ex)
        {
            _log.Error($"AOBMaker HEX field failed for {field.Name}", ex);
        }
    }

    [RelayCommand]
    private async Task HexPtrAddressAsync(LiveFieldValue? field)
    {
        if (_aobMaker == null || field == null || string.IsNullOrEmpty(field.PtrAddress)) return;
        try
        {
            await _aobMaker.NavigateHexViewAsync(StripHexPrefix(field.PtrAddress));
        }
        catch (Exception ex)
        {
            _log.Error($"AOBMaker HEX ptr failed for {field.Name}", ex);
        }
    }

    [RelayCommand]
    private async Task HexObjectAddressAsync()
    {
        if (_aobMaker == null || string.IsNullOrEmpty(CurrentAddress)) return;
        try
        {
            await _aobMaker.NavigateHexViewAsync(StripHexPrefix(CurrentAddress));
        }
        catch (Exception ex)
        {
            _log.Error("AOBMaker HEX object address failed", ex);
        }
    }

    // --- AOBMaker CE Plugin: one-click "Add to CE" memory record ---

    /// <summary>
    /// Add a single typed CE memory record at this field's own address (instance base +
    /// offset), labelled with the field name and typed to match the field. One-click
    /// alternative to copy-address-then-build-the-record-by-hand, so the user can jump
    /// straight to CE's "Find out what accesses this address". Batch adds go through
    /// the existing multi-select Copy CE Field (clipboard).
    /// </summary>
    [RelayCommand]
    private async Task AddFieldToCeAsync(LiveFieldValue? field)
    {
        if (_aobMaker == null || field == null || string.IsNullOrEmpty(field.FieldAddress)) return;
        var t = CeXmlExportService.MapFieldToCeRecordType(field);
        await AddRecordToCeAsync(field.Name, field.FieldAddress, t, "field");
    }

    /// <summary>
    /// Add a single CE memory record at this field's pointer target (the dereferenced
    /// object/struct base), typed as an 8-byte hex pointer. Only meaningful for navigable
    /// pointer fields (PtrAddress populated).
    /// </summary>
    [RelayCommand]
    private async Task AddPtrToCeAsync(LiveFieldValue? field)
    {
        if (_aobMaker == null || field == null || string.IsNullOrEmpty(field.PtrAddress)) return;
        await AddRecordToCeAsync(field.Name, field.PtrAddress, CeXmlExportService.PointerRecordType, "ptr");
    }

    /// <summary>
    /// Shared back-end for the per-row Add-to-CE buttons: push one typed memory record to
    /// CE via the AOBMaker plugin and reflect the outcome in the status line. Keeps the
    /// always-visible toolbar chip honest by syncing <see cref="IsAobMakerAvailable"/> to
    /// the bridge's post-call state.
    /// </summary>
    private async Task AddRecordToCeAsync(string name, string address,
        CeXmlExportService.CeRecordType t, string kind)
    {
        try
        {
            var ok = await _aobMaker!.CreateMemoryRecordAsync(
                Services.PackedLayoutNotice.RecordNamePrefix + name,
                StripHexPrefix(address), t.ValueType, t.IsSigned, t.ShowAsHex);
            IsAobMakerAvailable = _aobMaker.IsAvailable;
            StatusText = ok
                ? $"Added to CE: {name}"
                : (_aobMaker.IsAvailable
                    ? $"CE rejected record for {name}"
                    : "AOBMaker not connected — open CE with the plugin loaded");
        }
        catch (Exception ex)
        {
            _log.Error($"AOBMaker Add-to-CE ({kind}) failed for {name}", ex);
            StatusText = $"Add to CE failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyCurrentAddressAsync()
    {
        if (string.IsNullOrEmpty(CurrentAddress)) return;

        try
        {
            var formatted = AddressHelper.FormatAddress(
                CurrentAddress, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy current address", ex);
        }
    }

    [RelayCommand]
    private async Task CopyCurrentNameAsync()
    {
        if (string.IsNullOrEmpty(CurrentObjectName)) return;

        try
        {
            await _platform.CopyToClipboardAsync(CurrentObjectName);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy current name", ex);
        }
    }

    [RelayCommand]
    private async Task CopyOuterAddressAsync()
    {
        if (string.IsNullOrEmpty(CurrentOuterAddr) || CurrentOuterAddr == "0x0") return;

        try
        {
            var formatted = AddressHelper.FormatAddress(
                CurrentOuterAddr, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy outer address", ex);
        }
    }

    // ========================================
    // Auto-refresh
    // ========================================

    /// <summary>
    /// Reacts to IsAutoRefreshing changes (driven by ToggleButton.IsChecked binding).
    /// Starts or stops the auto-refresh timer accordingly.
    /// </summary>
    partial void OnIsAutoRefreshingChanged(bool value)
    {
        if (value)
            StartAutoRefreshTimer();
        else
            StopAutoRefreshTimer();
    }

    partial void OnAutoRefreshIntervalSecChanged(int value)
    {
        // Enforce minimum interval (dynamic minimum from benchmark)
        if (value < AutoRefreshMinSec)
        {
            AutoRefreshIntervalSec = AutoRefreshMinSec;
            return;
        }

        // Update timer interval if already running
        if (_autoRefreshTimer != null && _autoRefreshTimer.IsEnabled)
        {
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(value);
            _countdownRemaining = value; // Reset countdown to new interval
        }
    }

    private void StartAutoRefreshTimer()
    {
        // Stop existing timer, but don't reset IsAutoRefreshing
        if (_autoRefreshTimer != null)
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
            _autoRefreshTimer = null;
        }

        // Reset benchmark state — first tick will measure refresh duration
        _isAutoRefreshBenchmarked = false;

        var interval = Math.Max(AutoRefreshIntervalSec, AutoRefreshMinSec);
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(interval)
        };
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        _autoRefreshTimer.Start();

        // Start 1-second countdown timer for status display
        StopCountdownTimer();
        _countdownRemaining = interval;
        AutoRefreshStatusText = $"sec · {_countdownRemaining}s";
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
    }

    private void StopCountdownTimer()
    {
        if (_countdownTimer != null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= OnCountdownTick;
            _countdownTimer = null;
        }
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_isAutoRefreshing_InProgress)
        {
            AutoRefreshStatusText = "sec · refreshing...";
            return;
        }

        _countdownRemaining--;
        if (_countdownRemaining < 0) _countdownRemaining = 0;
        AutoRefreshStatusText = $"sec · {_countdownRemaining}s";
    }

    public void StopAutoRefreshTimer()
    {
        if (_autoRefreshTimer != null)
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
            _autoRefreshTimer = null;
        }

        StopCountdownTimer();
        IsAutoRefreshing = false;

        // Reset dynamic minimum and benchmark state on stop (tab switch, navigation, etc.)
        AutoRefreshMinSec = Constants.MinAutoRefreshIntervalSec;
        _isAutoRefreshBenchmarked = false;
        AutoRefreshStatusText = "sec";
    }

    /// <summary>
    /// Audit fix #17: stop both DispatcherTimers when the VM is destroyed.
    /// Without this, a still-registered Tick handler keeps the VM rooted by
    /// the Avalonia dispatcher, so the timer fires post-disposal — at best
    /// wasting work, at worst crashing on stale state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // StopAutoRefreshTimer already handles both _autoRefreshTimer and
        // _countdownTimer (via StopCountdownTimer) — single call covers it.
        StopAutoRefreshTimer();

        GC.SuppressFinalize(this);
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        // Anti-flooding: skip if a refresh is already in progress or no data to refresh.
        // Uses a dedicated flag (_isAutoRefreshing_InProgress) to prevent re-entrant calls
        // from the DispatcherTimer firing while a previous refresh is still awaiting.
        if (_isAutoRefreshing_InProgress || _isEditing || !HasData || string.IsNullOrEmpty(CurrentAddress)) return;

        _isAutoRefreshing_InProgress = true;
        try
        {
            var sw = Stopwatch.StartNew();
            await RefreshAsync();
            sw.Stop();

            var durationSec = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);

            // Benchmark: on first successful auto-refresh, check if the interval is too short.
            // If refresh took longer than the user's interval, auto-clamp the minimum.
            if (!_isAutoRefreshBenchmarked)
            {
                _isAutoRefreshBenchmarked = true;

                if (durationSec >= AutoRefreshIntervalSec)
                {
                    var newMin = durationSec + Constants.AutoRefreshBenchmarkBufferSec;
                    AutoRefreshMinSec = newMin;
                    AutoRefreshIntervalSec = newMin;

                    // Restart timer with the new interval
                    if (_autoRefreshTimer != null)
                    {
                        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(newMin);
                    }

                    _log.Info($"Auto-refresh: benchmark {durationSec}s, clamped interval to {newMin}s");
                }
            }

            // Reset countdown after refresh completes
            _countdownRemaining = Math.Max(AutoRefreshIntervalSec, AutoRefreshMinSec);
        }
        catch
        {
            // Silently ignore auto-refresh errors to avoid flooding the UI with error dialogs
        }
        finally
        {
            _isAutoRefreshing_InProgress = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch(value);
    }

    private void ApplySearch(string query)
    {
        // Require at least 2 characters — single char matches too broadly
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            foreach (var f in Fields) f.IsSearchMatch = false;
            SearchMatchCount = 0;
            HasSearchResults = false;
            _lastScrolledSearchText = "";
        }
        else
        {
            int count = 0;
            foreach (var f in Fields)
            {
                bool match =
                    f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    f.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    f.DisplayValue.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    f.PtrClassName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(f.StructTypeName) && f.StructTypeName.Contains(query, StringComparison.OrdinalIgnoreCase));

                f.IsSearchMatch = match;
                if (match) count++;
            }

            SearchMatchCount = count;
            HasSearchResults = count > 0;

            // Scroll to first match when search text changes and has results
            if (count > 0 && query != _lastScrolledSearchText)
            {
                _lastScrolledSearchText = query;
                ScrollToFirstSearchMatch?.Invoke();
            }
        }

        // Force DataGrid to re-evaluate row styles by resetting the collection
        var items = new ObservableCollection<LiveFieldValue>(Fields);
        Fields = items;
    }

    /// <summary>Move the selection to the next highlighted search match
    /// (down arrow). Wraps from the last match back to the first.</summary>
    [RelayCommand]
    private void NextSearchMatch() => NavigateSearchMatch(+1);

    /// <summary>Move the selection to the previous highlighted search match
    /// (up arrow). Wraps from the first match back to the last.</summary>
    [RelayCommand]
    private void PrevSearchMatch() => NavigateSearchMatch(-1);

    /// <summary>Step the selection through the highlighted search matches.
    /// Search only re-colours matching rows; this lets the user actually jump
    /// between them (setting SelectedField anchors the grid selection). When
    /// the current selection isn't a match, forward starts at the first match
    /// and backward at the last. Stepping wraps around both ends.</summary>
    private void NavigateSearchMatch(int direction)
    {
        if (!HasSearchResults) return;
        var matches = Fields.Where(f => f.IsSearchMatch).ToList();
        if (matches.Count == 0) return;

        int cur = SelectedField != null ? matches.IndexOf(SelectedField) : -1;
        int next = cur < 0
            ? (direction > 0 ? 0 : matches.Count - 1)
            : (cur + direction + matches.Count) % matches.Count;

        var target = matches[next];
        SelectedField = target;
        ScrollFieldIntoView?.Invoke(target);
    }

    private async Task NavigateToAsync(string addr, string label, int fieldOffset, string fieldName, bool isPointer)
    {
        var result = await _dump.WalkInstanceAsync(addr, arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: FillGaps);
        result = await AutoFillGapsRetryAsync(result, addr);

        var displayName = !string.IsNullOrEmpty(result.Name) ? result.Name : label;
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = addr,
            Label = displayName,
            FieldOffset = fieldOffset,
            FieldName = fieldName,
            IsPointerDeref = isPointer,
        });

        _log.Info($"NAV→ {fieldName} addr={addr} off=0x{fieldOffset:X} ptr={isPointer} | BC={FormatBreadcrumbTrace()}");
        UpdateDisplay(result);
    }

    /// <summary>
    /// Auto-retry with fill_gaps when a walk returns 0 fields but PropertiesSize indicates data exists.
    /// This gives users raw byte analysis instead of an empty panel for classes with no UPROPERTY fields.
    /// Only triggers when FillGaps toggle is off (avoids double-fill_gaps).
    /// </summary>
    private async Task<InstanceWalkResult> AutoFillGapsRetryAsync(
        InstanceWalkResult result, string addr, string? classAddr = null)
    {
        if (result.Fields.Count == 0 && result.PropertiesSize > 0x30 && !FillGaps)
        {
            _log.Info($"Auto fill_gaps: 0 fields but propsSize={result.PropertiesSize}, retrying with fill_gaps for {addr}");
            result = await _dump.WalkInstanceAsync(addr, classAddr,
                arrayLimit: ArrayLimit, previewLimit: PreviewLimit, fillGaps: true);
        }
        return result;
    }

    /// <summary>Format breadcrumb trail for debug logging.</summary>
    private string FormatBreadcrumbTrace()
    {
        if (Breadcrumbs.Count == 0) return "(empty)";
        var parts = new List<string>(Breadcrumbs.Count);
        foreach (var bc in Breadcrumbs)
        {
            var flags = bc.IsContainerView ? "C" : bc.IsPointerDeref ? "P" : "S";
            parts.Add($"{bc.FieldName ?? bc.Label}({flags},0x{bc.FieldOffset:X},{bc.Address?[^4..]})");
        }
        return string.Join(" > ", parts);
    }

    [RelayCommand]
    private async Task GenerateInvokeScriptAsync(FunctionInfoModel? func)
    {
        if (func == null || string.IsNullOrEmpty(CurrentClassName)) return;

        try
        {
            ClearStatus();
            var script = InvokeScriptGenerator.Generate(CurrentClassName, func.Name, func);
            var description = $"Invoke: {CurrentClassName}::{func.Name}";

            // Sample availability before send so we can distinguish 'pipe
            // broke mid-send' (was available, now isn't) from 'never
            // configured / CE not running' (was already false). Note:
            // this command is also IsEnabled-bound to IsAobMakerAvailable
            // in the AXAML, so wasAvailable=false here would only happen
            // if the user clicked between availability flips -- the
            // clipboard fallback below still produces a usable script.
            bool wasAvailable = _aobMaker?.IsAvailable ?? false;
            if (_aobMaker != null && wasAvailable)
            {
                var sent = await _aobMaker.CreateAAScriptAsync(description, script, autoActivate: false);
                if (sent)
                {
                    _log.Info($"Invoke script sent to CE: {description}");
                    StatusText = $"Invoke script created in CE: {func.Name}";
                    if (_aobMaker != null) IsAobMakerAvailable = _aobMaker.IsAvailable;
                    return;
                }
            }

            // Fallback: copy script to clipboard. If we thought CE was
            // present (button shouldn't have been clickable in that case),
            // surface a pipe-broken warning so the user knows the AA Script
            // didn't land in CE.
            await _platform.CopyToClipboardAsync(script);
            if (_aobMaker != null) IsAobMakerAvailable = _aobMaker.IsAvailable;
            StatusText = wasAvailable
                ? $"⚠ AOBMaker pipe broke (CE closed?) — invoke script copied to clipboard"
                : $"Invoke script copied to clipboard: {func.Name}";
            _log.Info($"Invoke script copied to clipboard: {description} (wasAvailable={wasAvailable})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to generate invoke script for {func.Name}", ex);
        }
    }

    [RelayCommand]
    private async Task InvokeViaPipeAsync(FunctionInfoModel? func)
    {
        if (func == null || string.IsNullOrEmpty(CurrentAddress)) return;

        try
        {
            ClearStatus();

            if (Avalonia.Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
                return;

            var inputParams = func.Params.Where(p => !p.IsReturn).ToList();

            // Dialog owns the entire invoke lifecycle:
            // - Shows input fields (or "no params" message)
            // - FIRE button calls InvokeFunctionAsync internally
            // - Copy AA Script button bakes current values via BakedScriptGenerator
            //   and pushes to AOBMaker / clipboard
            // - Decoded results shown inline (return values, out params)
            // - Returns "ok" on Close, null on Cancel
            var dialog = new Views.InvokeParamDialog(
                CurrentClassName, func.Name, inputParams, func.Params, func.ParmsSize,
                CurrentAddress, _dump, _engineState?.UEVersion ?? 0,
                aobMaker: _aobMaker, platform: _platform,
                mode: Views.InvokeDialogMode.PipeInvoke);

            var dialogResult = await dialog.ShowDialog<string?>(owner);

            StatusText = dialogResult == "ok"
                ? $"Invoke dialog closed: {CurrentClassName}::{func.Name}"
                : $"Invoke cancelled: {func.Name}";

            _log.Info($"Pipe invoke dialog {(dialogResult == "ok" ? "completed" : "cancelled")}: " +
                      $"{CurrentClassName}::{func.Name} inst={CurrentAddress}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to invoke {func?.Name} via pipe", ex);
        }
    }

    /// <summary>
    /// Third UFunction-row button: opens the InvokeParamDialog in
    /// CopyBakedScript mode (FIRE hidden) so the user can fill the form
    /// and ship a non-interactive AA Script for inclusion in their .CT.
    /// For zero-param functions the dialog is skipped -- the script is
    /// generated immediately from an empty BakedParamValue list.
    /// </summary>
    [RelayCommand]
    private async Task CopyBakedScriptAsync(FunctionInfoModel? func)
    {
        if (func == null || string.IsNullOrEmpty(CurrentClassName)) return;

        try
        {
            ClearStatus();

            var inputParams = func.Params.Where(p => !p.IsReturn).ToList();
            var hasReturn = func.Params.Any(p => p.IsReturn);

            // Fast-path: TRULY trivial functions only (no inputs AND no return).
            // For functions that return a value but take no inputs (e.g.
            // KismetSystemLibrary::GetGameName, KismetMathLibrary::GetPI),
            // we MUST show the dialog so the Verify Return Value toggle is
            // reachable -- otherwise the user has no way to print/inspect
            // what the function actually returned.
            if (inputParams.Count == 0 && !hasReturn)
            {
                var script = Services.BakedScriptGenerator.Generate(
                    CurrentClassName, func.Name, func.ParmsSize,
                    Array.Empty<Models.BakedParamValue>());
                var description = $"Invoke (baked, no args): {CurrentClassName}::{func.Name}";
                // Sample availability BEFORE the send so we can distinguish
                // 'pipe broke mid-send' (was available, now isn't) from
                // 'not configured' (was already false).
                bool wasAvailable = _aobMaker?.IsAvailable ?? false;
                bool sentToCe = false;
                if (_aobMaker != null && wasAvailable)
                    sentToCe = await _aobMaker.CreateAAScriptAsync(description, script, autoActivate: false);
                if (!sentToCe)
                    await _platform.CopyToClipboardAsync(script);
                // Sync the VM-level flag from whatever the bridge ended up at,
                // so the Notes column reflects post-send reality on the next
                // repaint.
                if (_aobMaker != null) IsAobMakerAvailable = _aobMaker.IsAvailable;

                StatusText = sentToCe
                    ? $"AA Script created in CE: {func.Name}"
                    : wasAvailable
                        ? $"⚠ AOBMaker pipe broke (CE closed?) — script copied to clipboard"
                        : $"AOBMaker not connected — script copied to clipboard ({func.Name})";
                _log.Info($"Baked AA Script (no args) {(sentToCe ? "sent to CE" : "to clipboard")}: " +
                          $"{CurrentClassName}::{func.Name} (wasAvailable={wasAvailable})");
                return;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
                return;

            var dialog = new Views.InvokeParamDialog(
                CurrentClassName, func.Name, inputParams, func.Params, func.ParmsSize,
                CurrentAddress, _dump, _engineState?.UEVersion ?? 0,
                aobMaker: _aobMaker, platform: _platform,
                mode: Views.InvokeDialogMode.CopyBakedScript);

            var dialogResult = await dialog.ShowDialog<string?>(owner);

            StatusText = dialogResult == "ok"
                ? $"AA Script ready: {CurrentClassName}::{func.Name}"
                : $"AA Script export cancelled: {func.Name}";

            _log.Info($"CopyBakedScript dialog {(dialogResult == "ok" ? "completed" : "cancelled")}: " +
                      $"{CurrentClassName}::{func.Name}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to generate baked script for {func?.Name}", ex);
        }
    }

    /// <summary>
    /// Empty the displayed node — fields, breadcrumb spine, header + parent info —
    /// so a FAILED Locate-in-GWorld doesn't leave the previous object on screen
    /// looking like the result. The caller sets StatusText with the reason after.
    /// (Inverse of <see cref="UpdateDisplay"/>.)
    /// </summary>
    private void ClearDisplayedNode()
    {
        Fields.Clear();
        Breadcrumbs.Clear();
        SelectedField = null;
        HasData = false;
        ShowCeXml = false;
        CurrentObjectName = "";
        CurrentClassName = "";
        CurrentAddress = "";
        CurrentOuterAddr = "";
        CurrentOuterName = "";
        CurrentOuterClassName = "";
        HasParent = false;
    }

    private void UpdateDisplay(InstanceWalkResult result)
    {
        CurrentObjectName = result.Name;
        CurrentClassName = result.ClassName;
        CurrentAddress = result.Address;
        HasData = true;
        ShowCeXml = false;

        // Update parent (Outer) info
        CurrentOuterAddr = result.OuterAddr;
        CurrentOuterName = result.OuterName;
        CurrentOuterClassName = result.OuterClassName;
        HasParent = !string.IsNullOrEmpty(result.OuterAddr) && result.OuterAddr != "0x0";

        // Inline structs are not UObjects — they don't have OuterPrivate or FName at
        // the UObject::Name offset. The DLL reads garbage when walking a struct address
        // as if it were a UObject, producing corrupted name strings (亂碼).
        // Override CurrentObjectName with the breadcrumb label (set from field metadata
        // during navigation) and disable the Parent button / clear Outer info.
        if (Breadcrumbs.Count > 0 && !Breadcrumbs[^1].IsPointerDeref
            && !string.IsNullOrEmpty(Breadcrumbs[^1].ClassAddr))
        {
            CurrentObjectName = Breadcrumbs[^1].Label;
            HasParent = false;
            CurrentOuterAddr = "";
            CurrentOuterName = "";
            CurrentOuterClassName = "";
        }

        // Track whether this is a definition view (schema-only, no live values)
        _isDefinitionView = result.IsDefinition;

        // Compute absolute field addresses
        ulong baseAddr = 0;
        try
        {
            if (!string.IsNullOrEmpty(result.Address))
                baseAddr = Convert.ToUInt64(result.Address.Replace("0x", "").Replace("0X", ""), 16);
        }
        catch { /* ignore parse failures */ }

        // Update fields. When refreshing the same object (same field count and layout),
        // replace items in-place to preserve DataGrid scroll position.
        // When navigating to a different object, do a full clear+rebuild.
        var newFields = result.Fields;
        foreach (var f in newFields)
        {
            if (baseAddr != 0)
                f.FieldAddress = $"0x{baseAddr + (ulong)f.Offset:X}";
        }

        if (Fields.Count == newFields.Count && Fields.Count > 0
            && Fields[0].Name == newFields[0].Name)
        {
            // Same layout — replace in-place (preserves scroll position)
            for (int i = 0; i < newFields.Count; i++)
                Fields[i] = newFields[i];
        }
        else
        {
            // Different layout — full rebuild
            Fields.Clear();
            foreach (var f in newFields)
                Fields.Add(f);
        }

        // Apply pending scroll-to-field hint (e.g. set by OpenReferenceOwner).
        // Setting SelectedField alone does NOT scroll the DataGrid — Avalonia's
        // DataGrid only auto-scrolls on user-driven selection. Raise the
        // ScrollToFieldRequested event so the View calls ScrollIntoView, the
        // same path used by edit-commit and inline drill navigation.
        if (!string.IsNullOrEmpty(_pendingScrollFieldName))
        {
            var hint = _pendingScrollFieldName;
            _pendingScrollFieldName = null;
            var hit = Fields.FirstOrDefault(f => f.Name == hint);
            if (hit != null)
            {
                SelectedField = hit;
                ScrollToFieldRequested?.Invoke(hint);
                _log.Info($"UpdateDisplay: auto-scrolled to '{hint}' (pending scroll hint)");
                TryDrillIntoMatchedContainer(hit);
            }
            else
            {
                _log.Info($"UpdateDisplay: pending scroll hint '{hint}' not found in field list");
                // Drop the drill hint too — without the container field we
                // have nothing to drill into.
                _pendingDrillElementIndex = -1;
            }
        }
        else if (_pendingScrollFieldOffset is int wantOffset)
        {
            // Value Search cross-nav: match the owning property row by byte
            // offset (names aren't unique). Lands on the container row for a
            // map/array/set hit; TryDrillIntoMatchedContainer then drills to
            // the matched element when the display name carried a "[N]".
            _pendingScrollFieldOffset = null;
            ScrollToFieldByOffset(wantOffset);
        }

        // Store class address and load functions asynchronously. Track
        // the in-flight task so cross-tab navigators (e.g. Interesting
        // Funcs -> Live) can await it before calling
        // TrySelectFunctionByName -- otherwise the call races with the
        // function-list population and the row never gets selected.
        _currentClassAddr = result.ClassAddr;
        _pendingFunctionsLoad = LoadFunctionsAsync(result.ClassAddr);

        // DataTable detection: if this is a DataTable, fetch rows and inject synthetic RowMap field
        _cachedDataTableRows = null;
        if (result.ClassName == "DataTable" && !string.IsNullOrEmpty(result.Address))
            _ = TryLoadDataTableRowsAsync(result.Address);
    }

    /// <summary>
    /// Detect DataTable and inject a synthetic RowMap field for container navigation.
    /// Called fire-and-forget from UpdateDisplay to avoid blocking the UI.
    /// </summary>
    private async Task TryLoadDataTableRowsAsync(string dataTableAddr)
    {
        try
        {
            var dtResult = await _dump.WalkDataTableRowsAsync(dataTableAddr);
            _cachedDataTableRows = dtResult;

            // Inject a synthetic "RowMap" field at the end of the field list
            var syntheticField = new LiveFieldValue
            {
                Name = "RowMap",
                TypeName = "DataTableRows",
                Offset = dtResult.RowMapOffset,
                Size = 0,
                TypedValue = $"{{DataTable: {dtResult.RowCount} rows, {dtResult.RowStructName}}}",
                DataTableRowCount = dtResult.RowCount,
                DataTableStructName = dtResult.RowStructName,
                DataTableFNameSize = dtResult.FNameSize,
                DataTableStride = dtResult.Stride,
                DataTableRowStructAddr = dtResult.RowStructAddr,
                DataTableRowData = dtResult.Rows,
            };

            // Add on UI thread
            await Dispatcher.UIThread.InvokeAsync(() => Fields.Add(syntheticField));
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load DataTable rows for {dataTableAddr}", ex);
        }
    }

    private async Task LoadFunctionsAsync(string classAddr)
    {
        if (string.IsNullOrEmpty(classAddr) || classAddr == "0x0")
        {
            _allFunctions.Clear();
            Functions.Clear();
            HasFunctions = false;
            return;
        }

        try
        {
            var funcs = await _dump.WalkFunctionsAsync(classAddr);
            _allFunctions.Clear();
            _allFunctions.AddRange(funcs);
            HasFunctions = funcs.Count > 0;
            ApplyFunctionFilter();
        }
        catch
        {
            _allFunctions.Clear();
            Functions.Clear();
            HasFunctions = false;
        }
    }

    /// <summary>
    /// Rebuild the visible <see cref="Functions"/> collection from
    /// <see cref="_allFunctions"/> using <see cref="FunctionFilter"/>.
    /// Substring match on function name (case-insensitive). Empty filter
    /// shows everything. Mirrors the InterestingFunctions filter pattern
    /// so the UX is consistent across the two UFunction views.
    /// </summary>
    private void ApplyFunctionFilter()
    {
        Functions.Clear();
        if (_allFunctions.Count == 0) return;

        var filter = (FunctionFilter ?? "").Trim();
        if (filter.Length == 0)
        {
            foreach (var f in _allFunctions) Functions.Add(f);
            return;
        }

        foreach (var f in _allFunctions)
        {
            if (f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Functions.Add(f);
        }
    }

    /// <summary>
    /// Public entry point for cross-tab navigation: scroll to and select
    /// the named function in the Functions DataGrid. Returns true when the
    /// function was found and made current; false when the class has no
    /// such function (caller should still expand the section so the user
    /// can see the full list).
    /// </summary>
    public async Task<bool> TrySelectFunctionByNameAsync(string functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return false;

        // Wait for any in-flight LoadFunctionsAsync triggered by the
        // preceding NavigateToAddress to finish. UpdateDisplay() kicks the
        // function load fire-and-forget so fields render immediately;
        // without this await the cross-tab navigator races the loader and
        // sees an empty _allFunctions on the first click after a class
        // change. The second click then succeeds because the previous
        // load already completed -- exactly the "(function not selected)"
        // pattern observed in the live-test logs.
        if (_pendingFunctionsLoad is { IsCompleted: false } pending)
        {
            try { await pending; }
            catch { /* loader logs its own error path; treat as miss */ }
        }

        if (_allFunctions.Count == 0) return false;

        // Clear filter first — a previously typed filter could hide the
        // target row even though it's in the underlying list.
        if (!string.IsNullOrEmpty(FunctionFilter)) FunctionFilter = "";

        // Auto-expand the section so the user can actually see the target
        // row without an extra click after a cross-tab navigation.
        IsFunctionsExpanded = true;

        foreach (var f in Functions)
        {
            if (string.Equals(f.Name, functionName, StringComparison.Ordinal))
            {
                SelectedFunction = f;
                ScrollToFunctionRequested?.Invoke(functionName);
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A breadcrumb navigation item, recording navigation history for CE XML export.
/// </summary>
public sealed class BreadcrumbItem
{
    public string Address { get; init; } = "";
    public string Label { get; init; } = "";
    public string ClassAddr { get; init; } = "";

    /// <summary>Offset of the field that was clicked to reach this level (hex).</summary>
    public int FieldOffset { get; init; }

    /// <summary>Field name (e.g., "m_pAttributeSetHealth").</summary>
    public string FieldName { get; init; } = "";

    /// <summary>True if navigation was through a pointer dereference (ObjectProperty), false for inline struct.</summary>
    public bool IsPointerDeref { get; init; }

    /// <summary>Field name the user was looking at before drilling in. Used to restore scroll position on Back.</summary>
    public string? ScrollHintFieldName { get; set; }

    /// <summary>True if this breadcrumb represents a container element view (Array/Map/Set/DataTable).</summary>
    public bool IsContainerView { get; init; }

    /// <summary>The source container field (for refreshing container views).</summary>
    public LiveFieldValue? ContainerField { get; init; }

    /// <summary>True if this breadcrumb represents a DataTable row container view.</summary>
    public bool IsDataTableView { get; init; }

    /// <summary>Cached DataTable walk result (for refreshing DataTable row views).</summary>
    public DataTableWalkResult? DataTableData { get; set; }
}

/// <summary>
/// A saved bookmark slot capturing LiveWalker navigation state.
/// </summary>
public sealed class BookmarkSlot : ObservableObject
{
    public int SlotIndex { get; init; }

    /// <summary>1-based display number for UI binding.</summary>
    public int DisplayNumber => SlotIndex + 1;

    private bool _isOccupied;
    public bool IsOccupied
    {
        get => _isOccupied;
        // TooltipText is computed from IsOccupied + the saved metadata (which is
        // always assigned before IsOccupied flips true), so refresh the hint here.
        set { if (SetProperty(ref _isOccupied, value)) OnPropertyChanged(nameof(TooltipText)); }
    }

    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    /// <summary>
    /// Hover hint for the slot button. Always non-empty so the user can tell an
    /// empty slot from a filled one before clicking: empty slots explain how to
    /// save, occupied slots show the target and invite a jump-back.
    /// </summary>
    public string TooltipText => IsOccupied
        ? $"Jump to bookmark {DisplayNumber}: {SavedClassName} :: {SavedObjectName}\n{SavedAddress}\nClick to restore this view (object, selected rows, scroll)."
        : $"Bookmark {DisplayNumber}: empty - no bookmark saved.\nClick ★ then this slot to save the current view here.";

    // Saved navigation state
    public List<BreadcrumbItem> SavedBreadcrumbs { get; set; } = new();
    public string SavedAddress { get; set; } = "";
    public string SavedObjectName { get; set; } = "";
    public string SavedClassName { get; set; } = "";
    public string SavedClassAddr { get; set; } = "";
    public WorldWalkResult? SavedCachedWorld { get; set; }

    /// <summary>Field rows (name + byte offset) the user had selected at save time.</summary>
    public List<BookmarkFieldRef> SavedSelectedFields { get; set; } = new();

    /// <summary>
    /// Topmost visible field row at save time — the anchor used to restore the
    /// scroll position. Null when no row was visible (e.g. empty grid). The
    /// Avalonia DataGrid exposes no public pixel-offset scroll API, so the view
    /// position is restored by scrolling this row back into view.
    /// </summary>
    public BookmarkFieldRef? SavedTopRow { get; set; }
}

/// <summary>Identifies a field row for bookmark re-selection (name + byte offset).</summary>
public sealed record BookmarkFieldRef(string Name, int Offset);

/// <summary>
/// Mutable carrier letting a synchronous event handler hand a value back to the
/// raiser. Used to pull the DataGrid's topmost-visible row from the View into the
/// ViewModel when a bookmark is saved (for scroll-position restore).
/// </summary>
public sealed class ViewAnchorRef { public BookmarkFieldRef? TopRow; }
