using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Instance Finder panel.
/// Search for instances by class name, view live values, export CE XML.
/// </summary>
public partial class InstanceFinderViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IPlatformService _platform;

    private EngineState? _engineState;

    // Monotonic guard for the field walk: a fast instance selection must not be
    // clobbered by a slower walk_instance for a previously-selected instance
    // (which would render A's fields under B and desync the CE XML export).
    private int _fieldLoadId;

    // Address format
    [ObservableProperty] private int _selectedAddressFormatIndex;
    private AddressFormat AddrFormat => (AddressFormat)SelectedAddressFormatIndex;

    /// <summary>Whether CE XML export should collapse pointer/array nodes.</summary>
    public bool CollapsePointerNodes { get; set; }

    /// <summary>Max array element count for inline reading (2^N, default 128).</summary>
    private int _arrayLimit = 128;
    public int ArrayLimit
    {
        get => _arrayLimit;
        set
        {
            if (_arrayLimit == value) return;
            _arrayLimit = value;
            // Auto-refresh selected instance with new limit
            if (SelectedInstance != null)
                _ = LoadInstanceFieldsAsync(SelectedInstance);
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
            // Auto-refresh selected instance with new limit
            if (SelectedInstance != null)
                _ = LoadInstanceFieldsAsync(SelectedInstance);
        }
    }

    /// <summary>Max CE DropDownList entries (2^N, default 512). Used during CE XML export.</summary>
    public int DropDownLimit { get; set; } = 512;

    // --- Class name search ---
    [ObservableProperty] private string _searchClassName = "";
    /// <summary>Optional object-name filter (case-insensitive substring), ANDed
    /// with the class query. Resolved DLL-side (filter-then-cap) so it doesn't
    /// miss matches the way a client-side filter on capped class results would —
    /// but the returned list is still capped at <c>limit</c>; truncation is
    /// surfaced in the status text. Either box may be empty: class-only,
    /// name-only, or both.</summary>
    [ObservableProperty] private string _searchObjectName = "";
    [ObservableProperty] private bool _exactMatch;

    /// <summary>Opt-in: scan GObjects from the high (newest-allocated) end so the
    /// most-recently-spawned instances survive the result cap — use to catch a
    /// just-spawned enemy. Off (default) keeps the lowest indices (CDO /
    /// class-default / earliest instances — good for finding a Blueprint's
    /// template/defaults).</summary>
    [ObservableProperty] private bool _newestFirst;
    [ObservableProperty] private ObservableCollection<InstanceResult> _instances = new();
    [ObservableProperty] private InstanceResult? _selectedInstance;
    [ObservableProperty] private ObservableCollection<LiveFieldValue> _fields = new();
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isLoadingFields;
    [ObservableProperty] private bool _hasInstances;
    [ObservableProperty] private bool _hasFields;
    [ObservableProperty] private string _ceXmlOutput = "";
    [ObservableProperty] private bool _showCeXml;
    [ObservableProperty] private string _statusText = "";

    // --- Address-to-Instance reverse lookup ---
    [ObservableProperty] private string _lookupAddress = "";
    [ObservableProperty] private string _lookupStatusText = "";
    [ObservableProperty] private bool _isLookingUp;

    // Container-aware results: addresses that fall inside an ArrayProperty
    // heap buffer rather than within a UObject's PropertiesSize.
    [ObservableProperty] private ObservableCollection<ContainerMatch> _containerMatches = new();
    [ObservableProperty] private bool _hasContainerMatches;

    /// <summary>
    /// Event raised when user wants to navigate to an address in the Live Walker.
    /// </summary>
    public event Action<string>? NavigateToLiveWalker;

    /// <summary>
    /// Event raised when the user wants to locate a found instance within the
    /// GWorld object graph (forward path search). Payload = instance address.
    /// </summary>
    public event Action<string>? LocateInGWorld;

    /// <summary>
    /// Event raised to locate the OWNER of a container match within the GWorld
    /// graph (the looked-up address fell inside a container element). The whole
    /// <see cref="ContainerMatch"/> is passed so the walker can drill the full
    /// (possibly multi-level) container chain to land ON the value — including
    /// deeply-nested values found by the recursive deep scan.
    /// </summary>
    public event Action<ContainerMatch>? LocateContainerInGWorld;

    /// <summary>Event raised to show the selected instance's related objects
    /// (components, GAS ASC → AttributeSets, Controller↔Pawn) in the Related
    /// tab. Payload = instance address.</summary>
    public event Action<string>? NavigateToRelatedObjects;

    /// <summary>True when GWorld is available — gates the "Locate in GWorld" button.</summary>
    [ObservableProperty] private bool _isGWorldAvailable;

    /// <summary>Per-container element probe cap for the recursive deep container
    /// scan (the find_by_address fallback that locates values in deeply-nested,
    /// separately-allocated containers). Set from the top Options flyout.</summary>
    [ObservableProperty] private int _deepScanElemCap = 256;

    public InstanceFinderViewModel(IDumpService dump, ILoggingService log, IPlatformService platform)
    {
        _dump = dump;
        _log = log;
        _platform = platform;
    }

    public void SetEngineState(EngineState state)
    {
        _engineState = state;
        IsGWorldAvailable = state?.HasGWorld ?? false;
    }

    /// <summary>Cross-tab handoff entry point (the per-row "inst" buttons on
    /// Property Search / Value Search / Interesting Funcs+Props / Classes /
    /// Related). Runs a class-name search and clears any stale Object-name
    /// filter so the handoff lists instances of the class itself, not ANDed
    /// with a leftover name the user typed earlier.</summary>
    public async Task SearchForClassAsync(string className)
    {
        SearchObjectName = "";
        SearchClassName = className;
        if (SearchCommand.CanExecute(null))
            await SearchCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var className = SearchClassName?.Trim() ?? "";
        var nameFilter = SearchObjectName?.Trim() ?? "";
        // Need at least one criterion — both empty is a no-op (mirrors the DLL guard).
        if (className.Length == 0 && nameFilter.Length == 0) return;

        try
        {
            ClearError();
            IsSearching = true;
            StatusText = "Searching...";
            ShowCeXml = false;

            var result = await _dump.FindInstancesAsync(className, ExactMatch, newestFirst: NewestFirst, nameFilter: nameFilter);

            // Detach the bound selection before rebuilding (Avalonia's selection
            // model throws if Instances is Clear()'d while SelectedInstance is live).
            UiCollection.Reset(Instances, result.Instances, () => SelectedInstance = null);

            HasInstances = Instances.Count > 0;
            // The GObjects scan is exhaustive, but the returned list is capped —
            // be honest when it was hit so the user narrows instead of trusting a
            // partial list (broad object-name terms like "Component" overflow easily).
            var capNote = result.Truncated ? $" — ⚠ capped at {Instances.Count}, narrow the search" : "";
            if (result.Scanned > 0)
            {
                var pct = result.NonNull > 0 ? 100.0 * result.Named / result.NonNull : 0;
                StatusText = $"Found {Instances.Count} instances (scanned {result.Scanned:N0}, non-null {result.NonNull:N0}, named {result.Named:N0} ({pct:F1}%)){capNote}";
            }
            else
            {
                StatusText = $"Found {Instances.Count} instances{capNote}";
            }
            _log.Info($"FindInstances: class='{className}' name='{nameFilter}' -> {Instances.Count} results (scanned={result.Scanned}, nonNull={result.NonNull}, named={result.Named})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Search failed";
            _log.Error($"FindInstances failed for class='{className}' name='{nameFilter}'", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task LookupAddressAsync()
    {
        if (string.IsNullOrWhiteSpace(LookupAddress)) return;

        try
        {
            ClearError();
            IsLookingUp = true;
            LookupStatusText = "Looking up...";
            ShowCeXml = false;

            // Reject malformed input up-front. Prior behavior surfaced
            // "No UObject found at this address" for "0xajsd;jald" — misleading,
            // because the DLL silently parsed it to 0 (noexcept StrToAddr) and
            // searched for a UObject at 0 instead.
            if (!AddressHelper.TryNormalizeAddress(LookupAddress, _engineState?.ModuleBase, out var addrStr))
            {
                LookupStatusText = "Invalid address — expected hex (e.g. 0x7FF... or module.exe+RVA)";
                SelectedInstance = null;   // detach before clearing the bound collection
                Instances.Clear();
                ContainerMatches.Clear();
                Fields.Clear();
                HasFields = false;
                HasContainerMatches = false;
                return;
            }

            var result = await _dump.FindByAddressAsync(addrStr, DeepScanElemCap);

            SelectedInstance = null;   // detach before clearing the bound collection
            Instances.Clear();
            Fields.Clear();
            HasFields = false;
            ContainerMatches.Clear();

            // Container matches: address falls inside a UObject's TArray heap buffer.
            // The DLL always runs this scan when scan_containers=true, so we can
            // surface them even when the standard UObject containment check
            // already produced a match (the container path is more specific).
            foreach (var cm in result.ContainerMatches)
                ContainerMatches.Add(cm);
            HasContainerMatches = ContainerMatches.Count > 0;

            // Build a "[scanned X/Y in Zms]" suffix so the user can tell a
            // clean miss from a deadline-truncated scan — important when
            // testing on big games (FF7 Rebirth ~430K objects).
            string scanSuffix = "";
            if (result.ContainerScan is { } cs && cs.ObjectsTotal > 0)
            {
                if (cs.DeadlineHit)
                    scanSuffix = $"  [scanned {cs.ObjectsScanned}/{cs.ObjectsTotal} in {cs.DurationMs}ms — DEADLINE HIT, retry to continue]";
                else
                    scanSuffix = $"  [scanned {cs.ObjectsScanned}/{cs.ObjectsTotal} in {cs.DurationMs}ms]";
            }

            if (result.Found)
            {
                var instance = new InstanceResult
                {
                    Address = result.Address,
                    Index = result.Index,
                    Name = result.Name,
                    ClassName = result.ClassName,
                    OuterAddr = result.OuterAddr,
                };
                Instances.Add(instance);
                HasInstances = true;

                // "nearest" means the address is BEYOND the UObject's
                // PropertiesSize — we are NOT inside it (typically raw heap /
                // native allocation, or a container buffer the value lives in).
                // Don't auto-walk it: presenting its fields implies the value
                // lives there, which is misleading. Surface it as a clickable
                // hint and warn instead. ("backward" is a real subobject the DLL
                // validated, so auto-walking it is genuinely useful — keep it.)
                bool lowConfidence = result.MatchKind == "nearest";
                if (!lowConfidence)
                    SelectedInstance = instance;  // Auto-select to trigger field loading

                // Be honest about confidence — "nearest" / "backward" mean addr
                // is BEYOND the UObject's PropertiesSize, often misleading
                // (especially for heap-allocated container data).
                var matchInfo = result.MatchKind switch
                {
                    "exact"    => "Exact UObject match",
                    "contains" => $"Inside {result.Name} (offset +0x{result.OffsetFromBase:X})",
                    "backward" => $"Past {result.Name} (offset +0x{result.OffsetFromBase:X}) — backward scan",
                    "nearest"  => $"⚠ Not inside any UObject — this value is likely raw heap / native data. Nearest is {result.Name} at +0x{result.OffsetFromBase:X} (click the row to inspect it anyway).",
                    _          => $"Match: {result.Name} (offset +0x{result.OffsetFromBase:X})",
                };
                if (HasContainerMatches)
                    matchInfo += $" — found in {ContainerMatches.Count} container(s) below";
                LookupStatusText = matchInfo + scanSuffix;
                _log.Info($"FindByAddress: '{addrStr}' -> {matchInfo}{scanSuffix}");
            }
            else if (HasContainerMatches)
            {
                HasInstances = false;
                LookupStatusText = $"Inside {ContainerMatches.Count} container(s) — see list below" + scanSuffix;
                _log.Info($"FindByAddress: '{addrStr}' -> {ContainerMatches.Count} container matches only{scanSuffix}");
            }
            else
            {
                HasInstances = false;
                LookupStatusText = "No UObject found at this address" + scanSuffix;
                _log.Info($"FindByAddress: '{addrStr}' -> not found{scanSuffix}");
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            LookupStatusText = "Lookup failed";
            _log.Error($"FindByAddress failed for '{LookupAddress}'", ex);
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    [RelayCommand]
    private void OpenContainerOwner(ContainerMatch? match)
    {
        if (match == null || string.IsNullOrEmpty(match.OwnerAddress)) return;
        // Live Walker doesn't yet auto-drill into the array element; opening
        // the owner UObject lets the user click into the field manually.
        // Pass a hint string in status so they know what to look for.
        LookupStatusText = $"Opened {match.DisplayPath} — drill into '{match.FieldName}' in Live Walker";
        NavigateToLiveWalker?.Invoke(match.OwnerAddress);
    }

    partial void OnSelectedInstanceChanged(InstanceResult? value)
    {
        if (value != null)
        {
            _ = LoadInstanceFieldsAsync(value);
        }
        else
        {
            // Supersede any in-flight field walk AND clear the loading flag here:
            // the superseded walk bails in its finally without touching the flag
            // (id != _fieldLoadId), and no successor load runs to reset it.
            _fieldLoadId++;
            IsLoadingFields = false;
            Fields.Clear();
            HasFields = false;
        }
    }

    private async Task LoadInstanceFieldsAsync(InstanceResult instance)
    {
        int id = ++_fieldLoadId;
        try
        {
            ClearError();
            IsLoadingFields = true;
            ShowCeXml = false;

            var result = await _dump.WalkInstanceAsync(instance.Address, arrayLimit: ArrayLimit, previewLimit: PreviewLimit);
            if (id != _fieldLoadId) return;   // a newer selection / limit change superseded us

            // Compute base address for FieldAddress calculation
            ulong baseAddr = 0;
            try
            {
                if (!string.IsNullOrEmpty(result.Address))
                    baseAddr = Convert.ToUInt64(result.Address.Replace("0x", "").Replace("0X", ""), 16);
            }
            catch { /* ignore parse failures */ }

            Fields.Clear();
            foreach (var f in result.Fields)
            {
                if (baseAddr != 0)
                    f.FieldAddress = $"0x{baseAddr + (ulong)f.Offset:X}";
                Fields.Add(f);
            }

            HasFields = Fields.Count > 0;
        }
        catch (Exception ex)
        {
            if (id != _fieldLoadId) return;   // stale failure — don't clobber newer state
            SetError(ex);
            _log.Error($"Failed to walk instance at {instance.Address}", ex);
        }
        finally
        {
            if (id == _fieldLoadId)   // only the latest walk owns the loading flag
                IsLoadingFields = false;
        }
    }

    [RelayCommand]
    private async Task ExportCeXmlAsync()
    {
        if (SelectedInstance == null) return;

        try
        {
            ClearError();
            IsLoadingFields = true;

            // Pre-resolve StructProperty inner fields via DLL
            StatusText = "Resolving struct fields...";
            var resolvedStructs = await CeXmlExportService.ResolveStructFieldsAsync(
                _dump, new List<LiveFieldValue>(Fields), arrayLimit: ArrayLimit);

            // Compute root address in user-selected format
            var rootAddress = AddressHelper.FormatAddress(
                SelectedInstance.Address, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);

            StatusText = "Generating CE XML...";
            var xml = CeXmlExportService.GenerateInstanceXml(
                rootAddress, SelectedInstance.Name, SelectedInstance.ClassName,
                new List<LiveFieldValue>(Fields), resolvedStructs,
                collapsePointerNodes: CollapsePointerNodes,
                maxDropDownEntries: DropDownLimit);

            await _platform.CopyToClipboardAsync(xml);
            StatusText = "";
            _log.Info($"CE XML copied to clipboard for instance {SelectedInstance.Name} ({resolvedStructs.Count} structs resolved)");
        }
        catch (Exception ex)
        {
            StatusText = "";
            SetError(ex);
            _log.Error("Failed to export CE XML", ex);
        }
        finally
        {
            IsLoadingFields = false;
        }
    }

    [RelayCommand]
    private async Task CopyFieldAddressAsync(LiveFieldValue? field)
    {
        if (field == null || SelectedInstance == null) return;
        if (string.IsNullOrEmpty(SelectedInstance.Address)) return;

        try
        {
            var instanceAddr = Convert.ToUInt64(SelectedInstance.Address.Replace("0x", "").Replace("0X", ""), 16);
            var absAddr = instanceAddr + (ulong)field.Offset;
            var hexAddr = $"0x{absAddr:X}";

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
    private async Task CopyInstanceAddressAsync(InstanceResult? instance)
    {
        if (instance == null || string.IsNullOrEmpty(instance.Address)) return;

        try
        {
            var formatted = AddressHelper.FormatAddress(
                instance.Address, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy instance address for {instance.Name}", ex);
        }
    }

    /// <summary>Copy a container-match row's owning UObject address to the
    /// clipboard (mirrors the per-instance copy + Value Search's Copy Address).</summary>
    [RelayCommand]
    private async Task CopyContainerAddressAsync(ContainerMatch? match)
    {
        if (match == null || string.IsNullOrEmpty(match.OwnerAddress)) return;

        try
        {
            var formatted = AddressHelper.FormatAddress(
                match.OwnerAddress, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);
            await _platform.CopyToClipboardAsync(formatted);
            LookupStatusText = $"Copied {formatted}  ({match.OwnerClassName})";
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to copy container owner address for {match.OwnerClassName}", ex);
        }
    }

    [RelayCommand]
    private async Task GenerateCeAAScriptAsync(InstanceResult? instance)
    {
        if (instance == null || string.IsNullOrEmpty(instance.Address)) return;

        try
        {
            var symbolName = instance.ClassName.Replace(" ", "_").Replace("-", "_");

            var formattedAddr = AddressHelper.FormatAddress(
                instance.Address, _engineState?.ModuleName, _engineState?.ModuleBase, AddrFormat);

            var xml = CeXmlExportService.GenerateRegisterSymbolXml(symbolName, formattedAddr);

            await _platform.CopyToClipboardAsync(xml);
            _log.Info($"CE AA script copied to clipboard for {instance.ClassName}");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error("Failed to generate CE AA script", ex);
        }
    }

    [RelayCommand]
    private void OpenInLiveWalker()
    {
        if (SelectedInstance == null) return;
        NavigateToLiveWalker?.Invoke(SelectedInstance.Address);
    }

    [RelayCommand]
    private void LocateSelectedInGWorld()
    {
        if (SelectedInstance == null || !IsGWorldAvailable) return;
        LocateInGWorld?.Invoke(SelectedInstance.Address);
    }

    [RelayCommand]
    private void ShowRelatedObjects()
    {
        if (SelectedInstance == null) return;
        NavigateToRelatedObjects?.Invoke(SelectedInstance.Address);
    }

    /// <summary>Locate a container match's OWNER within the GWorld graph — the
    /// looked-up address is a value inside a container element, so reach the
    /// owning object (via the shortest GWorld path) and auto-drill into the
    /// element so the user lands next to the value.</summary>
    [RelayCommand]
    private void LocateContainerOwnerInGWorld(ContainerMatch? match)
    {
        if (match == null || !IsGWorldAvailable || string.IsNullOrEmpty(match.OwnerAddress)) return;
        // Hand off the whole match — the Live Walker reaches the owner via the
        // GWorld path and drills the full container chain (outermost → element →
        // … → deepest value), which covers both 1-level struct-element values
        // and deeply-nested values from the recursive deep scan.
        LocateContainerInGWorld?.Invoke(match);
    }
}
