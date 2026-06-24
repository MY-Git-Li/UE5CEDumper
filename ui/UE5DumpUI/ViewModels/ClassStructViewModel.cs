using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Class Structure panel.
/// </summary>
public partial class ClassStructViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;
    private readonly IPlatformService _platform;

    [ObservableProperty] private string _className = "";
    [ObservableProperty] private string _classPath = "";
    [ObservableProperty] private string _superName = "";
    [ObservableProperty] private int _propertiesSize;
    [ObservableProperty] private ObservableCollection<FieldInfoModel> _fields = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasClass;
    /// <summary>UClass* of the currently loaded class — for the per-class Find Func.</summary>
    [ObservableProperty] private string _loadedClassAddr = "";

    /// <summary>Field row the user right-clicked (drives the xref context menu).</summary>
    [ObservableProperty] private FieldInfoModel? _selectedField;

    /// <summary>Client-side filter text (substring over field name + type).</summary>
    [ObservableProperty] private string _fieldFilter = "";

    /// <summary>Full unfiltered field set; <see cref="Fields"/> is the
    /// filtered view rebuilt by <see cref="ApplyFieldFilter"/>.</summary>
    private readonly List<FieldInfoModel> _allFields = new();

    /// <summary>
    /// True when a class is loaded but has zero instance fields. The
    /// canonical example is <c>BlueprintFunctionLibrary</c> subclasses
    /// (e.g. <c>GameplayLib</c>) -- pure utility classes whose only
    /// content is static methods. Without this hint the user sees an
    /// empty DataGrid after a cross-tab fallback from Interesting Funcs
    /// and can't tell "broken load" from "this class genuinely has no
    /// fields". UI binds this to a help banner.
    /// </summary>
    public bool HasNoFields => HasClass && !IsLoading && _allFields.Count == 0;

    partial void OnHasClassChanged(bool value)   => OnPropertyChanged(nameof(HasNoFields));
    partial void OnIsLoadingChanged(bool value)  => OnPropertyChanged(nameof(HasNoFields));
    partial void OnFieldFilterChanged(string value) => ApplyFieldFilter();

    /// <summary>Rebuild <see cref="Fields"/> from <see cref="_allFields"/>,
    /// applying <see cref="FieldFilter"/> as a case-insensitive substring over
    /// field name + type. Field lists are small (hundreds), so no debounce.</summary>
    private void ApplyFieldFilter()
    {
        var filter = (FieldFilter ?? "").Trim();
        Fields.Clear();
        foreach (var f in _allFields)
        {
            if (filter.Length == 0
                || f.Name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)
                || f.TypeName.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
            {
                Fields.Add(f);
            }
        }
    }

    /// <summary>
    /// Address of the UObject whose class is currently displayed. Used to
    /// dedupe the "selection bounces twice" Avalonia ListBox behaviour and
    /// to ignore a stale null-fire that would otherwise blank the panel.
    /// </summary>
    private string? _lastLoadedNodeAddress;

    public ClassStructViewModel(IDumpService dump, ILoggingService log, IPlatformService platform)
    {
        _dump = dump;
        _log = log;
        _platform = platform;
    }

    /// <summary>
    /// "Find functions using this field" — static Kismet-bytecode cross-reference
    /// for the field's FProperty*. Opens a self-contained dialog (no tab impact).
    /// </summary>
    [RelayCommand]
    private async Task FindFieldXrefsAsync(FieldInfoModel? field)
    {
        field ??= SelectedField;
        if (field == null || string.IsNullOrEmpty(field.Address) || field.Address == "0x0")
            return;

        try
        {
            await Views.PropertyXrefDialog.ShowForFieldAsync(
                field.Name, field.TypeName, field.Address, _dump, _platform);
            _log.Info($"FindFieldXrefs dialog closed for {ClassName}.{field.Name} ({field.Address})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"FindFieldXrefs failed for {field.Name}", ex);
        }
    }

    /// <summary>"Find Class Funcs": which UFunctions take this whole class as a
    /// parameter or return value (find_functions_by_class — reflection, native
    /// functions included). Distinct from the per-FIELD "Find Funcs" column.</summary>
    [RelayCommand]
    private async Task FindClassFuncAsync()
    {
        if (string.IsNullOrEmpty(LoadedClassAddr)) return;
        await Views.PropertyXrefDialog.ShowForClassAsync(
            ClassName, LoadedClassAddr, _dump, _platform);
    }

    [RelayCommand]
    private async Task LoadClassAsync(string? classAddr)
    {
        if (string.IsNullOrEmpty(classAddr) || classAddr == "0x0") return;

        try
        {
            ClearError();
            IsLoading = true;

            var ci = await _dump.WalkClassAsync(classAddr);

            ClassName = ci.Name;
            ClassPath = ci.FullPath;
            SuperName = ci.SuperName;
            PropertiesSize = ci.PropertiesSize;
            LoadedClassAddr = classAddr;
            HasClass = true;

            _allFields.Clear();
            _allFields.AddRange(ci.Fields);
            ApplyFieldFilter();
            // Fields.Count change doesn't fire HasNoFields; nudge it.
            OnPropertyChanged(nameof(HasNoFields));

            _log.Info($"Loaded class: {ci.Name} ({ci.Fields.Count} fields)");
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to load class at {classAddr}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Called when a UObject is selected in the ObjectTree — loads its class.
    ///
    /// Disposition:
    ///   - If the clicked node IS a class-like UObject (UClass /
    ///     UScriptStruct / UEnum / UFunction or any subclass thereof),
    ///     walk its address DIRECTLY. Going through GetObjectAsync would
    ///     return its metaclass (UClass-of-Class, UClass-of-ScriptStruct,
    ///     etc.) whose FProperty chain is empty in UE — that produced the
    ///     "shows /Script/CoreUObject/Class with 0 fields" bug the user
    ///     reported on LocalPlayer.
    ///   - Otherwise it's an instance — fetch its UClass via get_object.
    ///
    /// A null `node` does NOT blank the panel: Avalonia's ListBox raises
    /// SelectionChanged with null whenever its ItemsSource collection
    /// changes (filter typing, a fresh load, suggestion auto-selection).
    /// We keep the last successfully-loaded class visible until another
    /// real selection arrives.
    ///
    /// We also dedupe consecutive selections of the same node — the
    /// listbox occasionally fires a second SelectionChanged for the same
    /// item right after a click, and re-walking the class is wasteful.
    /// </summary>
    public async Task OnObjectSelected(UObjectNode? node)
    {
        if (node == null) return;
        if (_lastLoadedNodeAddress == node.Address && HasClass) return;

        try
        {
            ClearError();

            string classAddr;
            if (IsClassLikeNode(node.ClassName))
            {
                // The clicked object is itself a UClass / UScriptStruct /
                // UEnum / UFunction — walk it directly.
                classAddr = node.Address;
            }
            else
            {
                // Instance: walk its UClass. Fall back to the object
                // address only if the metaclass lookup fails.
                var detail = await _dump.GetObjectAsync(node.Address);
                classAddr = detail.ClassAddr;
                if (string.IsNullOrEmpty(classAddr) || classAddr == "0x0")
                    classAddr = node.Address;
            }

            _lastLoadedNodeAddress = node.Address;
            await LoadClassCommand.ExecuteAsync(classAddr);
        }
        catch (Exception ex)
        {
            SetError(ex);
            _log.Error($"Failed to load class for object at {node.Address}", ex);
        }
    }

    /// <summary>
    /// True when the node's class name indicates the node IS itself a
    /// walkable type definition (UClass family, UScriptStruct family,
    /// UEnum family, UFunction family) rather than a runtime instance.
    /// </summary>
    private static bool IsClassLikeNode(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        // Any UClass-derived: Class, BlueprintGeneratedClass,
        // WidgetBlueprintGeneratedClass, AnimBlueprintGeneratedClass, etc.
        if (className.EndsWith("Class", StringComparison.Ordinal)) return true;
        return className switch
        {
            "ScriptStruct" or "UserDefinedStruct" => true,
            "Enum" or "UserDefinedEnum" => true,
            "Function" or "DelegateFunction" => true,
            _ => false,
        };
    }
}
