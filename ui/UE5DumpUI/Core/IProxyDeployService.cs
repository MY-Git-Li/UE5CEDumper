using UE5DumpUI.Models;

namespace UE5DumpUI.Core;

/// <summary>
/// Service for detecting Steam games and managing proxy DLL deployment.
/// All OS-dependent calls (registry, file system, PE version info) are
/// encapsulated here for testability and platform abstraction.
/// </summary>
public interface IProxyDeployService
{
    /// <summary>
    /// Get all Steam library folder paths by parsing libraryfolders.vdf.
    /// Returns empty list if Steam is not installed or VDF parse fails.
    /// </summary>
    Task<IReadOnlyList<string>> GetSteamLibraryFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Scan Steam library folders for UE game executables.
    /// Detects standard (xxx-Win64-Shipping.exe) and custom UE game layouts.
    /// </summary>
    Task<IReadOnlyList<DetectedGame>> FindUeGamesAsync(
        IReadOnlyList<string> libraryPaths, CancellationToken ct = default);

    /// <summary>
    /// Enumerate the ready fixed/removable drives (with physical-disk numbers)
    /// available for the generic (non-Steam) scan. Delegates to the platform
    /// layer so the caller stays platform-agnostic.
    /// </summary>
    Task<IReadOnlyList<DriveDescriptor>> GetScannableDrivesAsync(CancellationToken ct = default);

    /// <summary>
    /// Generic (non-Steam) scan: walk the selected drives for UE games by folder
    /// structure. Drives on the same physical disk are walked sequentially while
    /// different disks run in parallel; Steam library folders and system/junk
    /// trees are excluded; inaccessible folders are skipped and the walk
    /// continues. Reuses the same per-game-dir detection as the Steam path.
    /// </summary>
    Task<IReadOnlyList<DetectedGame>> FindUeGamesOnDrivesAsync(
        IReadOnlyList<DriveDescriptor> selectedDrives,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Check deployment status for each game (for the given proxy type) and
    /// update Status / InstalledVersion / ErrorMessage. Independently of the
    /// selected type, flags a redundancy warning via ErrorMessage only when
    /// 2+ of our proxy DLLs actually coexist in the same folder (only one
    /// activates at runtime).
    /// </summary>
    Task RefreshDeployStatusAsync(
        IList<DetectedGame> games, string sourceDllPath, ProxyType proxyType,
        CancellationToken ct = default);

    /// <summary>
    /// Deploy the proxy DLL of the given type to a game's Binaries directory.
    /// Returns true on success, false on failure (sets game.ErrorMessage).
    /// </summary>
    Task<bool> DeployAsync(string sourceDllPath, DetectedGame game, ProxyType proxyType,
        bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Undeploy (delete) the proxy DLL of the given type from a game's
    /// Binaries directory. Only removes our DLL (checks ProductName).
    /// Returns true on success.
    /// </summary>
    Task<bool> UndeployAsync(DetectedGame game, ProxyType proxyType,
        CancellationToken ct = default);

    /// <summary>
    /// Check if a DLL at the given path is ours (ProductName == "UE5CEDumper").
    /// </summary>
    bool IsOurProxyDll(string dllPath);

    /// <summary>
    /// Get the file version string from a DLL's PE version info.
    /// Returns null if version info is unavailable.
    /// </summary>
    string? GetDllVersion(string dllPath);
}
