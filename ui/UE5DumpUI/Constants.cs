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

    // Experimental Snapshot store — per-game SQLite DB under
    // %LOCALAPPDATA%\UE5CEDumper, named snapshots.<pe_hash>.db so each game's
    // snapshots stay isolated (no cross-game mixing / growth / corruption).
    public const string SnapshotDbPrefix = "snapshots";
    // Objects streamed per snapshot_chunk pipe round-trip. Raised 200 -> 1000 to
    // cut round-trip + SQLite-transaction overhead on huge games (FF7 Rebirth
    // ~433K objects: 2166 chunks -> ~433). Safe: the pipe is byte-mode (no message
    // cap) + StreamReader.ReadLineAsync accumulates any size, and the DLL's 15s
    // per-chunk deadline re-chunks a slow chunk (returns partial, pager advances by
    // scanned). See docs/todo.md snapshot-perf item (in-game re-test pending).
    public const int SnapshotChunkSize = 1000;

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
