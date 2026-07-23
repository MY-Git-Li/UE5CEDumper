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
    /// Enumerate running processes as DLL-injection candidates (UE games flagged).
    /// Delegates to the platform layer.
    /// </summary>
    Task<IReadOnlyList<GameProcessInfo>> ListGameProcessesAsync(CancellationToken ct = default);

    /// <summary>
    /// Inject a DLL into a running process (CreateRemoteThread + LoadLibraryW).
    /// Used by the "Inject into running game" flow. Delegates to the platform layer.
    /// </summary>
    Task<InjectResult> InjectDllAsync(int pid, string dllPath, CancellationToken ct = default);

    /// <summary>True when the app is running elevated (so an Access-Denied inject
    /// can't be helped by relaunching as Administrator — it's already admin).</summary>
    bool IsElevated();

    /// <summary>Retry injection elevated (UAC-prompt relaunch does just the inject).
    /// Used when <see cref="InjectDllAsync"/> returns AccessDenied.</summary>
    Task<InjectResult> InjectDllElevatedAsync(int pid, string dllPath, CancellationToken ct = default);

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
    /// Undeploy (delete) our proxy DLLs from a game's Binaries directory.
    ///
    /// <para><b>Type-agnostic on purpose.</b> It sweeps EVERY proxy flavour we ship,
    /// not the one currently selected in the UI: the radio button chooses what to
    /// <i>deploy</i>, whereas undeploy is a clean-up. Scoping it to the selection
    /// left a user who deployed <c>dxgi.dll</c> and later switched the radio to
    /// <c>version.dll</c> unable to remove it at all — while the grid reported
    /// <see cref="ProxyDeployStatus.DeployedOtherType"/> at them.</para>
    ///
    /// <para>Only ever deletes files that are OURS (<c>ProductName</c> check via
    /// <c>IsOurProxyDll</c>); a foreign <c>version.dll</c>/<c>dxgi.dll</c> — a mod
    /// loader, another tool — is always left alone.</para>
    ///
    /// Returns true when nothing of ours is left behind (including the case where
    /// there was nothing to remove).
    /// </summary>
    Task<bool> UndeployAsync(DetectedGame game, CancellationToken ct = default);

    /// <summary>
    /// Compute a per-game proxy suggestion for each detected game and write it to
    /// <c>SuggestedProxyType</c> / <c>SuggestedProxy</c>. Preference order:
    /// (0) a proxy CONFIRMED to have loaded this game (<paramref name="confirmedByExe"/>,
    /// keyed by .exe file name — the DLL self-reported a proxy load that stayed
    /// stable); (1) a proxy the user last deployed for this game
    /// (<paramref name="rememberedByGame"/>, keyed by <c>DetectedGame.Name</c>);
    /// (2) <c>injection · no proxy deployed</c> when the user has successfully
    /// injected into this game (<paramref name="injectedExes"/>, matched by .exe
    /// file name) and never deployed a proxy; (3) the safe <c>version.dll</c>
    /// default, with the .exe import table only annotating which proxies are
    /// importable. When <paramref name="enabled"/> is false the suggestion fields
    /// are cleared. Advisory only — never changes the selected proxy type, never deploys.
    /// </summary>
    Task ApplyProxySuggestionsAsync(
        IReadOnlyList<DetectedGame> games,
        IReadOnlyDictionary<string, ProxyType> confirmedByExe,
        IReadOnlyDictionary<string, ProxyType> rememberedByGame,
        IReadOnlySet<string> injectedExes,
        bool enabled,
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
