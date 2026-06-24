using System.Text.Json.Serialization;

namespace UE5DumpUI.Models;

/// <summary>
/// Persisted panel/Live-Walker OPTIONS (stable user preferences only) — the
/// toggles, dropdowns, formats and limits the user sets and expects to survive a
/// UI restart. Stored globally (machine-wide) as a single JSON file under
/// %LOCALAPPDATA%\UE5CEDumper\ui-options.json. See <see cref="Services.UiOptionsStore"/>.
///
/// Deliberately EXCLUDED (session-only by nature): search/filter/query text boxes,
/// live-data selections + results, addresses, one-shot modes, transient view state
/// (tab index, panel-collapse), busy/status flags. Also excluded because already
/// persisted elsewhere: snapshot quota + the experimental opt-in (ExperimentalGate /
/// experimental.json), UE-version override + invoke timeout (DLL per-game state),
/// teleport hotkeys (TeleportHotkeyStore), per-game class denylists (SnapshotStore).
///
/// Each sub-object's property defaults MUST match the corresponding ViewModel's
/// [ObservableProperty] initializer — a value missing from an older file hydrates
/// to the model default, which must equal what the VM would otherwise show.
///
/// NOTE: the JSON context intentionally does NOT use DefaultIgnoreCondition. Several
/// options default to true (DedupSharedObjects, GameOnly, ParallelScan …); with
/// WhenWritingDefault a user turning one OFF (= the bool type-default false) would be
/// omitted and silently revert to ON on the next launch. Every field is written.
/// </summary>
public sealed class UiOptionsSettings
{
    /// <summary>Schema version for future migrations (v1 = initial).</summary>
    public int SchemaVersion { get; set; } = 1;

    public MainUiOptions Main { get; set; } = new();
    public LiveWalkerUiOptions LiveWalker { get; set; } = new();
    public ValueSearchUiOptions ValueSearch { get; set; } = new();
    public SnapshotUiOptions Snapshot { get; set; } = new();
    public InstanceFinderUiOptions InstanceFinder { get; set; } = new();
    public PropertySearchUiOptions PropertySearch { get; set; } = new();
    public TeleportUiOptions Teleport { get; set; } = new();
    public SpcUiOptions Spc { get; set; } = new();
    public PivotUiOptions Pivot { get; set; } = new();
    public InterestingFuncsUiOptions InterestingFuncs { get; set; } = new();
    public InterestingPropsUiOptions InterestingProps { get; set; } = new();
    public ConsoleUiOptions Console { get; set; } = new();
    public GameClassFilterUiOptions GameClassFilter { get; set; } = new();
    public ProxyDeployUiOptions ProxyDeploy { get; set; } = new();
}

/// <summary>Top-bar display controls (master — fan out to child VMs on change).</summary>
public sealed class MainUiOptions
{
    public int SelectedAddressFormatIndex { get; set; }
    public bool CollapsePointerNodes { get; set; }
    public int ArrayLimitExponent { get; set; } = 7;
    public int DropDownLimitExponent { get; set; } = 9;
    public int CsxDrilldownDepth { get; set; }
    public int PreviewLimit { get; set; } = 2;
    public int DeepScanElemCapExponent { get; set; } = 8;
}

public sealed class LiveWalkerUiOptions
{
    public bool CollapseChain { get; set; }
    public bool DescShowOffset { get; set; }
    public bool DescShowType { get; set; }
    public bool DedupSharedObjects { get; set; } = true;
    public bool ExcludeSystemComponents { get; set; } = true;
    public int GWorldLocateDepth { get; set; } = 5;
    public int AutoRefreshIntervalSec { get; set; } = 10;   // Constants.DefaultAutoRefreshIntervalSec
}

public sealed class ValueSearchUiOptions
{
    public ValueScanDataType SelectedDataType { get; set; } = ValueScanDataType.Int32;
    public ValueScanType SelectedScanType { get; set; } = ValueScanType.Exact;
    public bool GameOnly { get; set; } = true;
    public int MaxResults { get; set; } = 50000;
    public int ScanTimeoutSeconds { get; set; } = 25;
    public bool ParallelScan { get; set; } = true;
    public bool BatchRead { get; set; } = true;
    public bool DeepScan { get; set; }
    public bool CrossObjectScan { get; set; }
    public bool NativeCScan { get; set; }
    public bool NewestFirst { get; set; }
    public bool PreFilterNoise { get; set; }
    public FloatRoundMode RoundingMode { get; set; } = FloatRoundMode.Round;
    public bool CaseSensitive { get; set; }
}

public sealed class SnapshotUiOptions
{
    public bool GameOnly { get; set; } = true;
    public bool AutoSkipNoise { get; set; } = true;
    public bool IncludeNativeFields { get; set; }
    public string SelectedScope { get; set; } = "NumericNoByte";
    public string SelectedFamily { get; set; } = "All numeric";
    public string SelectedMaxDataset { get; set; } = "Off";
    public bool ShowUsageBar { get; set; } = true;
    public bool GroupDeep { get; set; }
    public FloatRoundMode RoundingMode { get; set; } = FloatRoundMode.Round;
}

public sealed class InstanceFinderUiOptions
{
    public bool ExactMatch { get; set; }
    public bool NewestFirst { get; set; }
    public int InstanceSearchCap { get; set; } = 5000;
    public int DeepScanElemCap { get; set; } = 256;
}

public sealed class PropertySearchUiOptions
{
    public bool GameClassesOnly { get; set; } = true;
    public bool DeepSearch { get; set; }
}

public sealed class TeleportUiOptions
{
    public double ZOffset { get; set; } = 100.0;
    public int TraceChannel { get; set; }
    public bool FallbackToCenter { get; set; } = true;
    public bool CursorHotkeyEnabled { get; set; }
    public double RelativeDistance { get; set; } = 100.0;
    public bool RelativeHorizontal { get; set; } = true;
    public bool CoordSetRotation { get; set; }
    public bool AutoRefresh { get; set; }
}

public sealed class SpcUiOptions
{
    public string SelectedJoinMode { get; set; } = "Strict";
    public FloatRoundMode RoundingMode { get; set; } = FloatRoundMode.Round;
}

public sealed class PivotUiOptions
{
    public string SelectedSource { get; set; } = "Snapshot";
    public string SelectedKeyMode { get; set; } = "Identity (object path)";
}

public sealed class InterestingFuncsUiOptions
{
    public bool GameOnly { get; set; } = true;
    public bool ShowAll { get; set; }
}

public sealed class InterestingPropsUiOptions
{
    public bool GameOnly { get; set; } = true;
    public bool UnusualOnly { get; set; }
    public bool ShowAll { get; set; }
}

public sealed class ConsoleUiOptions
{
    public bool GameOnly { get; set; }
}

public sealed class GameClassFilterUiOptions
{
    public bool GameClassesOnly { get; set; } = true;
}

public sealed class ProxyDeployUiOptions
{
    public ProxyType SelectedProxyType { get; set; } = ProxyType.Version;
    public bool ForceOverwrite { get; set; }
}

/// <summary>
/// Source-generated JSON context (AOT/trimming — reflection JSON is disabled).
/// All nested sub-objects are reachable from the root type, so only the root needs
/// registering. WriteIndented for human-readability; NO DefaultIgnoreCondition
/// (every field is written — see the UiOptionsSettings remarks).
/// </summary>
[JsonSerializable(typeof(UiOptionsSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class UiOptionsJsonContext : JsonSerializerContext
{
}
