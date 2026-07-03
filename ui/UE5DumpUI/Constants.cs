namespace UE5DumpUI;

/// <summary>
/// Centralized constants for the UI application.
/// </summary>
public static class Constants
{
    // Named Pipe
    public const string PipeName = "UE5DumpBfx";

    // Application
    public const string AppName = "UE5DumpUI";
    public const string MutexName = "Global\\UE5DumpUI_SingleInstance";
    public const string AppVersion = "1.0.0";

    // Logging — category-routed to separate files
    public const string LogFolderName = "UE5CEDumper";
    public const string LogSubFolder = "Logs";
    public const string LogSubfolderName = "UE5DumpUI";      // UI module subfolder under Logs/
    public const string MirrorLogPrefix = "ui";               // Prefix for mirror files in game folders
    public const int LogRotateMax = 2;                        // 2-file rotation per category
    public const long LogMaxSizeBytes = 8 * 1024 * 1024;     // 8MB per file

    // Per-process mirror logging
    public const int MaxProcessFolders = 20;           // Clean up oldest beyond this
    public const int LogMaxAgeDays = 15;               // Delete log folders older than this

    // Log category names
    public const string LogCatInit = "init";
    public const string LogCatPipe = "pipe";
    public const string LogCatView = "view";

    // Pipe Communication
    public const int PipeConnectTimeoutMs = 5000;
    public const int DefaultPageSize = 200;

    // Hard timeout for Live Walker refresh / walk calls. If the DLL hangs on a
    // destroyed object (e.g. garbage FField chain from recycled memory), this
    // guarantees the UI unblocks instead of spinning IsLoading forever.
    public const int LiveWalkerRefreshTimeoutMs = 10000;

    // Object Tree
    public const int ObjectTreePageSize = 2000;     // Batch size for loading all objects
    public const int ObjectTreeMaxDisplay = 5000;   // Max items shown in FilteredNodes ListBox

    // Objects-per-page when walking the FULL GObjects pool via GetObjectListAsync
    // (SDK / USMAP / symbol / dump-all exports). Distinct from ObjectTreePageSize
    // (2000) — the exporters page in larger 5000-object batches.
    public const int GObjectsWalkPageSize = 5000;

    // Live Walker preview
    public const int DefaultPreviewLimit = 2;       // Struct sub-fields in preview (0-6)

    // Live Walker auto-refresh
    public const int DefaultAutoRefreshIntervalSec = 10;
    public const int MinAutoRefreshIntervalSec = 6;
    public const int AutoRefreshBenchmarkBufferSec = 5; // Extra seconds added to benchmarked duration

    // AOB Usage Tracking
    public const string AobUsageFilePrefix = "UE5CEDumper";

    // Experimental features (Snapshot / SPC Query / Class Pivot — gated via the
    // System-tab credit checkbox). Persisted under %LOCALAPPDATA%\UE5CEDumper.
    public const string ExperimentalSettingsFile = "experimental.json";

    // Global panel/Live-Walker OPTIONS (stable user preferences — export toggles,
    // scan/search behaviour, formats, limits). Persisted under %LOCALAPPDATA%\UE5CEDumper.
    public const string UiOptionsFile = "ui-options.json";

    // Per-game Live-Walker bookmarks — one file per game, bookmarks.<pe_hash>.json
    // under %LOCALAPPDATA%\UE5CEDumper (same per-game convention as the snapshot DB).
    public const string BookmarkFilePrefix = "bookmarks";

    // Number of Live-Walker bookmark slots.
    public const int BookmarkSlotCount = 8;

    // Experimental Snapshot store — per-game SQLite DB under
    // %LOCALAPPDATA%\UE5CEDumper, named snapshots.<pe_hash>.db so each game's
    // snapshots stay isolated (no cross-game mixing / growth / corruption).
    public const string SnapshotDbPrefix = "snapshots";
    // Objects streamed per snapshot_chunk pipe round-trip. 8192 (raised 200 -> 1000
    // -> 8192) for two reasons on huge games (FF7 Rebirth ~433K objects: ~53 chunks):
    //   (a) fewer pipe round-trips + SQLite write transactions;
    //   (b) the DLL parallelizes each chunk's object walk, and ScanThreadCount only
    //       engages worker threads at >= 8192 work items — so a full chunk runs
    //       multi-threaded (the single-threaded walk was the 10+ min bottleneck).
    // Safe: byte-mode pipe (no message cap) + StreamReader.ReadLineAsync accumulates
    // any size. See docs/todo.md snapshot-perf item (in-game re-test pending).
    public const int SnapshotChunkSize = 8192;

    // Snapshot Diff / SPC / Group / Value snapshot-query row cap. Shared by the
    // model defaults (SpcQuery.MaxRows, SnapshotDiffFilter.MaxRows, group MaxResults,
    // ValueSearchUiOptions.MaxResults) AND the SnapshotStore `x > 0 ? x : N` fallbacks
    // so a single number governs the cap everywhere.
    public const int DefaultMaxQueryRows = 50000;

    // Live DLL value/group scan candidate cap (the DLL session's max returned rows).
    // Intentionally a SEPARATE constant from DefaultMaxQueryRows even though the value
    // matches — different subsystem (in-game scan session vs. snapshot-DB query).
    public const int ScanSessionMaxResults = 50000;

    // Value/group scan begin deadline in ms (Value Search "Timeout" slider default).
    // Also the sentinel the wire-shape code compares against to omit the field when
    // the user hasn't overridden it. Mirrors the DLL's 15000 ms scan default.
    public const int ScanSessionDeadlineMs = 15000;

    // Server-side paging window for scan begin / refine / query. Distinct from
    // DefaultPageSize (200) — the scan session pages in 1000-row windows.
    public const int ScanSessionPageSize = 1000;

    // GameThreadDispatch (Stark) default invoke timeout in ms. Mirrors the DLL's
    // Stark::kDefaultInvokeTimeoutMs; also the sentinel the UI uses to treat a value
    // as "clear override" so the per-game JSON stays clean.
    public const int StarkDefaultInvokeTimeoutMs = 5000;

    // Class / array pivot group cap (PivotQuery.MaxGroups, ArrayPivotQuery.MaxGroups).
    public const int DefaultMaxPivotGroups = 5000;

    // Batch-xref confirm warning threshold: warn before running a batch "Find Funcs"
    // over more than this many classes/rows (each is a full game-wide sweep).
    // NOTE: InterestingFunctionsViewModel intentionally uses its own local 100.
    public const int XrefBatchWarnThreshold = 25;

    // WalkWorldAsync actor limit passed by the Live Walker world-root walks.
    public const int WorldWalkMaxDepth = 500;

    // Default inline array element read limit (2^7). InstanceFinder / LiveWalker
    // seed their per-panel ArrayLimit with this; MainWindow derives its own from an
    // exponent slider.
    public const int DefaultArrayLimit = 128;

    // Default CE DropDownList enum-entry cap (2^9). InstanceFinder / LiveWalker seed
    // their per-panel DropDownLimit with this.
    public const int DefaultDropDownLimit = 512;

    // Default instance-search result cap (InstanceFinderUiOptions.InstanceSearchCap
    // and the InstanceFinder panel's own default).
    public const int DefaultInstanceSearchCap = 5000;

    // UI
    public const int DefaultWindowWidth = 1400;
    public const int DefaultWindowHeight = 900;
    public const int TreePanelWidth = 350;

    // Proxy DLL Deploy
    // Three proxy DLL options — user picks one via the UI RadioButton.
    // ProxyDllName is kept as the legacy "default" name for backward
    // compatibility with existing tests; the canonical lookup goes through
    // ProxyType.GetDllName() (Models/ProxyType.cs).
    public const string ProxyDllName = "version.dll";
    public const string ProxyDllNameDinput8 = "dinput8.dll";
    // dxgi.dll — statically imported by every D3D11/D3D12 UE game; the
    // reliable hijack target for EXEs that import neither version nor dinput8.
    public const string ProxyDllNameDxgi = "dxgi.dll";
    public const string ProxyProductName = "UE5CEDumper";
    public const string SteamRegistryPath = @"SOFTWARE\WOW6432Node\Valve\Steam";
    public const string SteamRegistryKey = "InstallPath";
    public const string SteamDefaultPath = @"C:\Program Files (x86)\Steam";
    public const string SteamLibraryFoldersVdf = @"config\libraryfolders.vdf";
    public const string SteamAppsCommon = @"steamapps\common";
}
