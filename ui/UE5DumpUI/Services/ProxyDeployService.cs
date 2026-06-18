using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Detects Steam-installed UE games and manages proxy DLL deployment.
/// All file system and registry calls are encapsulated here.
/// </summary>
public sealed class ProxyDeployService : IProxyDeployService
{
    private readonly ILoggingService _log;

    public ProxyDeployService(ILoggingService log)
    {
        _log = log;
    }

    // ────────────────────────────────────────────────────────────────
    // Steam Library Detection
    // ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetSteamLibraryFoldersAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var result = new List<string>();
            try
            {
                string? steamPath = GetSteamInstallPath();
                if (steamPath == null)
                {
                    _log.Warn("ProxyDeploy", "Steam installation not found");
                    return (IReadOnlyList<string>)result;
                }

                string vdfPath = Path.Combine(steamPath, Constants.SteamLibraryFoldersVdf);
                if (!File.Exists(vdfPath))
                {
                    _log.Warn("ProxyDeploy", $"libraryfolders.vdf not found: {vdfPath}");
                    // Fallback: use Steam path itself as the single library
                    result.Add(steamPath);
                    return (IReadOnlyList<string>)result;
                }

                string vdfContent = File.ReadAllText(vdfPath);
                var paths = VdfParser.ParseLibraryFolders(vdfContent);

                if (paths.Count == 0)
                {
                    _log.Warn("ProxyDeploy", "VDF parse returned 0 libraries, using Steam path as fallback");
                    result.Add(steamPath);
                }
                else
                {
                    // Validate paths exist
                    foreach (string p in paths)
                    {
                        if (Directory.Exists(p))
                            result.Add(p);
                        else
                            _log.Warn("ProxyDeploy", $"Steam library path does not exist: {p}");
                    }
                }

                _log.Info("ProxyDeploy", $"Found {result.Count} Steam library folder(s)");
            }
            catch (Exception ex)
            {
                _log.Error("ProxyDeploy", $"GetSteamLibraryFolders failed: {ex.Message}");
            }

            return (IReadOnlyList<string>)result;
        }, ct);
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Constants.SteamRegistryPath);
            if (key?.GetValue(Constants.SteamRegistryKey) is string path && Directory.Exists(path))
                return path;
        }
        catch
        {
            // Registry access may fail — fall through to default
        }

        // Fallback to default Steam path
        if (Directory.Exists(Constants.SteamDefaultPath))
            return Constants.SteamDefaultPath;

        return null;
    }

    // ────────────────────────────────────────────────────────────────
    // UE Game Detection
    // ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<DetectedGame>> FindUeGamesAsync(
        IReadOnlyList<string> libraryPaths, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var games = new List<DetectedGame>();
            var seenBinDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string libPath in libraryPaths)
            {
                ct.ThrowIfCancellationRequested();

                string commonDir = Path.Combine(libPath, Constants.SteamAppsCommon);
                if (!Directory.Exists(commonDir))
                    continue;

                try
                {
                    foreach (string gameDir in Directory.EnumerateDirectories(commonDir))
                    {
                        ct.ThrowIfCancellationRequested();
                        ScanGameFolder(gameDir, games, seenBinDirs);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Warn("ProxyDeploy", $"Error scanning {commonDir}: {ex.Message}");
                }
            }

            _log.Info("ProxyDeploy", $"Found {games.Count} UE game(s)");
            return (IReadOnlyList<DetectedGame>)games;
        }, ct);
    }

    private void ScanGameFolder(string gameDir, List<DetectedGame> games, HashSet<string> seenBinDirs)
    {
        string gameName = Path.GetFileName(gameDir);

        // Two-tier search to handle three different UE shipping layouts:
        //   1. Monolithic (DQ7R, Hogwarts, Stray, etc.)
        //         <Game>\<Sub>\Binaries\Win64\<Game>-Win64-Shipping.exe   ← real
        //         <Game>\Engine\Binaries\Win64\CrashReportClient.exe       ← stub only
        //
        //   2. Hybrid (StellarBlade, NMKART, Palworld, Titan Quest II)
        //         <Game>\<Sub>\Binaries\Win64\<Game>-Win64-Shipping.exe   ← real
        //         <Game>\Engine\Binaries\Win64\<Game>-Win64-Shipping.exe  ← stub launcher
        //
        //   3. Pure modular (Satisfactory)
        //         <Game>\<Sub>\Binaries\Win64\  ← no .exe at all (only .modules + DLLs)
        //         <Game>\Engine\Binaries\Win64\<Game>-Win64-Shipping.exe  ← real launcher
        //
        // Walking Engine\ unconditionally produces phantom rows for layouts 1+2
        // (the user sees both rows for the same game). Skipping Engine\ kills
        // layout 3 (Satisfactory). Solution: try primary roots first; only fall
        // back to Engine\Binaries\Win64\ when primary contributed no rows for
        // this gameDir.
        var primary = new List<string> { gameDir };
        string? engineRoot = null;

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(gameDir))
            {
                if (string.Equals(Path.GetFileName(sub), "Engine", StringComparison.OrdinalIgnoreCase))
                    engineRoot = sub;
                else
                    primary.Add(sub);
            }
        }
        catch
        {
            // Permission error etc. — just use the root
        }

        int gamesBefore = games.Count;
        foreach (string root in primary)
            ScanBinariesDir(gameName, gameDir, root, games, seenBinDirs);

        // Engine fallback: only walked when primary yielded zero rows for this
        // gameDir. Catches pure-modular layouts like Satisfactory where the
        // real launcher .exe lives in <Game>\Engine\Binaries\Win64\.
        if (games.Count == gamesBefore && engineRoot != null)
            ScanBinariesDir(gameName, gameDir, engineRoot, games, seenBinDirs);
    }

    private void ScanBinariesDir(
        string gameName, string gameDir, string root,
        List<DetectedGame> games, HashSet<string> seenBinDirs)
    {
        string binDir = Path.Combine(root, "Binaries", "Win64");
        if (!Directory.Exists(binDir))
            return;

        // Dedup by BinariesDir
        if (!seenBinDirs.Add(binDir))
            return;

        try
        {
            // Find executables in Binaries/Win64. Stub .exes (CrashReportClient,
            // launcher helpers) are filtered up front so they never win against
            // a real game exe even when sorted earlier alphabetically.
            bool foundUe = false;
            foreach (string exePath in Directory.EnumerateFiles(binDir, "*.exe"))
            {
                string exeName = Path.GetFileName(exePath);
                if (IsKnownStubExe(exeName))
                    continue;

                // Standard UE: xxx-Win64-Shipping.exe
                bool isStandardUe = exeName.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase);

                // Check for Engine folder nearby (UE indicator)
                bool hasEngineFolder = Directory.Exists(Path.Combine(root, "Engine"))
                                    || Directory.Exists(Path.Combine(gameDir, "Engine"));

                if (isStandardUe || hasEngineFolder)
                {
                    games.Add(new DetectedGame
                    {
                        Name = gameName,
                        ExePath = exePath,
                        BinariesDir = binDir,
                        UeVersion = TryDetectUeVersion(exePath),
                    });
                    foundUe = true;
                    break; // One exe per BinariesDir is enough
                }
            }

            // Fallback: any non-stub exe in Binaries/Win64 is likely a UE
            // game even without standard naming. Same stub filter applies
            // so we never surface CrashReportClient as a "game".
            if (!foundUe)
            {
                foreach (string exePath in Directory.EnumerateFiles(binDir, "*.exe"))
                {
                    string exeName = Path.GetFileName(exePath);
                    if (IsKnownStubExe(exeName))
                        continue;

                    games.Add(new DetectedGame
                    {
                        Name = gameName,
                        ExePath = exePath,
                        BinariesDir = binDir,
                        UeVersion = TryDetectUeVersion(exePath),
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ProxyDeploy", $"Error scanning {binDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Known non-game UE helper executables that ship inside Binaries/Win64
    /// folders. These would otherwise be picked up as the "game .exe" on
    /// modular UE builds where Engine/Binaries/Win64 holds both the real
    /// game launcher and CrashReportClient side-by-side (e.g. Satisfactory).
    /// Case-insensitive match.
    /// </summary>
    internal static bool IsKnownStubExe(string exeName)
    {
        return string.Equals(exeName, "CrashReportClient.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the multi-proxy redundancy warning from the list of OUR proxy
    /// DLLs actually present in a game folder, or null when fewer than two
    /// coexist. Pure (no IO) so the rule is unit-testable.
    ///
    /// Only 2+ simultaneously-deployed proxies are a real conflict ("only one
    /// will activate at runtime"). Exactly one deployed proxy — of ANY type —
    /// is the normal state and must NOT warn, regardless of which proxy type
    /// the UI currently has selected. N-proxy-safe.
    /// </summary>
    internal static string? BuildConflictMessage(IReadOnlyList<string> deployedProxyNames)
    {
        if (deployedProxyNames.Count < 2)
            return null;

        return $"Multiple proxy DLLs deployed ({string.Join(", ", deployedProxyNames)})"
             + " — only one will activate at runtime";
    }

    /// <summary>
    /// Classify a game whose SELECTED proxy type file is ABSENT. If another of
    /// OUR proxy types is already deployed in the folder, that's
    /// <see cref="ProxyDeployStatus.DeployedOtherType"/> (redeploying the
    /// selected type would create a redundant second proxy) — otherwise the
    /// folder is genuinely clean (<see cref="ProxyDeployStatus.NotDeployed"/>).
    /// <paramref name="deployedProxyNames"/> lists OUR proxy DLLs present in the
    /// folder; since the selected type's file is absent, it never appears here,
    /// so a non-empty list always means an OTHER type. The returned message
    /// names the single other-type proxy; when 2+ coexist the per-folder
    /// <see cref="BuildConflictMessage"/> already lists them all, so this
    /// returns no message to avoid duplicating the list. Pure (no IO) so the
    /// rule is unit-testable.
    /// </summary>
    internal static (ProxyDeployStatus status, string? message) ClassifyAbsentSelected(
        IReadOnlyList<string> deployedProxyNames)
    {
        if (deployedProxyNames.Count == 0)
            return (ProxyDeployStatus.NotDeployed, null);

        string? message = deployedProxyNames.Count == 1
            ? $"Deployed as {deployedProxyNames[0]}"
            : null;
        return (ProxyDeployStatus.DeployedOtherType, message);
    }

    /// <summary>
    /// Try to detect UE version from the game executable's PE version info.
    /// Returns null if detection fails.
    /// </summary>
    private static string? TryDetectUeVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            // Some UE games embed "Unreal Engine" or version in FileDescription/Comments
            // For now, just return null — version is detected by the DLL at runtime
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Deploy Status
    // ────────────────────────────────────────────────────────────────

    public Task RefreshDeployStatusAsync(
        IList<DetectedGame> games, string sourceDllPath, ProxyType proxyType,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string? sourceVersion = GetDllVersion(sourceDllPath);

            string selectedDllName = proxyType.GetDllName();
            // All distinct proxy DLL file names (Distinct guards against a
            // future enum value whose switch arm fell back to the default).
            string[] allProxyNames = Enum.GetValues<ProxyType>()
                .Select(t => t.GetDllName())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var game in games)
            {
                ct.ThrowIfCancellationRequested();

                string targetDll = Path.Combine(game.BinariesDir, selectedDllName);
                game.ErrorMessage = null;

                // Which of OUR proxy DLLs are actually present in this folder?
                // Computed up front because it drives BOTH the absent-selected
                // classification (is the folder truly clean, or is another of our
                // proxy types deployed?) and the 2+ redundancy warning below. This
                // is a property of the folder, INDEPENDENT of the selected radio.
                var deployedProxyNames = allProxyNames
                    .Where(name =>
                    {
                        string p = Path.Combine(game.BinariesDir, name);
                        return File.Exists(p) && IsOurProxyDll(p);
                    })
                    .ToList();

                // Status reflects the SELECTED proxy type's state ────────────
                if (!File.Exists(targetDll))
                {
                    // Absent selected type: clean folder → NotDeployed; another of
                    // our types present → DeployedOtherType (don't mislead the user
                    // into redeploying on top of a working proxy of a different type).
                    var (status, message) = ClassifyAbsentSelected(deployedProxyNames);
                    game.Status = status;
                    game.ErrorMessage = message;
                    game.InstalledVersion = null;
                }
                else if (!IsOurProxyDll(targetDll))
                {
                    game.Status = ProxyDeployStatus.OtherProxy;
                    game.InstalledVersion = null;
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(targetDll);
                        game.ErrorMessage = $"Other proxy: {info.ProductName ?? info.FileDescription ?? "unknown"}";
                    }
                    catch
                    {
                        game.ErrorMessage = "Other proxy DLL detected";
                    }
                }
                else
                {
                    string? installedVersion = GetDllVersion(targetDll);
                    game.InstalledVersion = installedVersion;
                    game.Status = (sourceVersion != null && installedVersion == sourceVersion)
                                  ? ProxyDeployStatus.DeployedCurrent
                                  : ProxyDeployStatus.DeployedOutdated;
                }

                // Redundancy detection: warn ONLY when 2+ of OUR proxies coexist
                // (only one activates at runtime — see Heiter.cpp's mutex). A
                // single deployed proxy of any type is the normal state and must
                // not warn (otherwise switching tabs falsely flags every game
                // that has a different single proxy installed). N-proxy-safe: no
                // hardcoded type pair. deployedProxyNames was computed up front.
                string? conflictMsg = BuildConflictMessage(deployedProxyNames);
                if (conflictMsg != null)
                {
                    game.ErrorMessage = string.IsNullOrEmpty(game.ErrorMessage)
                                        ? conflictMsg
                                        : $"{game.ErrorMessage}; {conflictMsg}";
                }
            }
        }, ct);
    }

    // ────────────────────────────────────────────────────────────────
    // Deploy / Undeploy
    // ────────────────────────────────────────────────────────────────

    public Task<bool> DeployAsync(string sourceDllPath, DetectedGame game, ProxyType proxyType,
        bool force = false, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                string targetDll = Path.Combine(game.BinariesDir, proxyType.GetDllName());

                // Refuse to overwrite another program's proxy DLL
                if (File.Exists(targetDll) && !IsOurProxyDll(targetDll) && !force)
                {
                    game.Status = ProxyDeployStatus.OtherProxy;
                    game.ErrorMessage = "Refused: another program's proxy DLL";
                    return false;
                }

                // Skip if same version (unless force)
                if (!force && File.Exists(targetDll) && IsOurProxyDll(targetDll))
                {
                    string? srcVer = GetDllVersion(sourceDllPath);
                    string? tgtVer = GetDllVersion(targetDll);
                    if (srcVer != null && srcVer == tgtVer)
                    {
                        game.Status = ProxyDeployStatus.DeployedCurrent;
                        return true; // Already up to date
                    }
                }

                File.Copy(sourceDllPath, targetDll, overwrite: true);
                game.Status = ProxyDeployStatus.DeployedCurrent;
                game.InstalledVersion = GetDllVersion(targetDll);
                game.ErrorMessage = null;
                _log.Info("ProxyDeploy", $"Deployed {proxyType.GetDisplayName()} to {game.Name}: {targetDll}");
                return true;
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) /* SHARING_VIOLATION */
                                      || ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
            {
                game.Status = ProxyDeployStatus.ErrorLocked;
                game.ErrorMessage = "File locked (game running?)";
                _log.Warn("ProxyDeploy", $"Deploy to {game.Name} failed: file locked");
                return false;
            }
            catch (Exception ex)
            {
                game.Status = ProxyDeployStatus.ErrorOther;
                game.ErrorMessage = ex.Message;
                _log.Error("ProxyDeploy", $"Deploy to {game.Name} failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    public Task<bool> UndeployAsync(DetectedGame game, ProxyType proxyType,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                string targetDll = Path.Combine(game.BinariesDir, proxyType.GetDllName());

                if (!File.Exists(targetDll))
                {
                    game.Status = ProxyDeployStatus.NotDeployed;
                    game.InstalledVersion = null;
                    return true;
                }

                // Refuse to delete another program's proxy DLL
                if (!IsOurProxyDll(targetDll))
                {
                    game.Status = ProxyDeployStatus.OtherProxy;
                    game.ErrorMessage = "Refused: not our proxy DLL";
                    return false;
                }

                File.Delete(targetDll);
                game.Status = ProxyDeployStatus.NotDeployed;
                game.InstalledVersion = null;
                game.ErrorMessage = null;
                _log.Info("ProxyDeploy", $"Undeployed {proxyType.GetDisplayName()} from {game.Name}: {targetDll}");
                return true;
            }
            catch (IOException ex) when (ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
            {
                game.Status = ProxyDeployStatus.ErrorLocked;
                game.ErrorMessage = "File locked (game running?)";
                _log.Warn("ProxyDeploy", $"Undeploy from {game.Name} failed: file locked");
                return false;
            }
            catch (Exception ex)
            {
                game.Status = ProxyDeployStatus.ErrorOther;
                game.ErrorMessage = ex.Message;
                _log.Error("ProxyDeploy", $"Undeploy from {game.Name} failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    // ────────────────────────────────────────────────────────────────
    // DLL Identification
    // ────────────────────────────────────────────────────────────────

    public bool IsOurProxyDll(string dllPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            return string.Equals(info.ProductName, Constants.ProxyProductName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string? GetDllVersion(string dllPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            return info.FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
