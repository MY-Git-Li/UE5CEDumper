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
    private readonly IPlatformService _platform;

    public ProxyDeployService(ILoggingService log, IPlatformService platform)
    {
        _log = log;
        _platform = platform;
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
        // CrashReportClient ships next to the real game exe on modular builds.
        // UnrealEditor / UE4Editor / UnrealFrontend only matter for the generic
        // drive walk (a Steam library never contains an editor install) — filter
        // them so an engine/editor tree is never surfaced as a "game".
        return string.Equals(exeName, "CrashReportClient.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exeName, "UnrealEditor.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exeName, "UE4Editor.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exeName, "UnrealFrontend.exe", StringComparison.OrdinalIgnoreCase);
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
    // Generic (non-Steam) Drive Scan
    // ────────────────────────────────────────────────────────────────

    /// <summary>Directory names hard-skipped during the generic drive walk —
    /// system/junk trees plus "steamapps" (Steam is scanned by the dedicated
    /// path; the resolved Steam roots are also excluded explicitly). Matched by
    /// directory name, case-insensitive.</summary>
    private static readonly HashSet<string> HardSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Windows", "WinSxS",
        "$SysReset", "Recovery", "node_modules", ".git", "WindowsApps", "steamapps",
    };

    // UE game roots are shallow (<Drive>\...\<Game>\<Project>\Binaries\Win64); 6
    // levels covers manual launcher nesting while hard-stopping runaway descent
    // into asset trees. Prune-on-match usually fires long before this.
    private const int MaxWalkDepth = 6;

    public Task<IReadOnlyList<DriveDescriptor>> GetScannableDrivesAsync(CancellationToken ct = default)
    {
        return Task.Run(() => _platform.GetLogicalDrives(), ct);
    }

    public Task<IReadOnlyList<GameProcessInfo>> ListGameProcessesAsync(CancellationToken ct = default)
    {
        return Task.Run(() => _platform.GetRunningProcesses(), ct);
    }

    public Task<InjectResult> InjectDllAsync(int pid, string dllPath, CancellationToken ct = default)
    {
        return Task.Run(() => _platform.InjectDll(pid, dllPath), ct);
    }

    public bool IsElevated() => _platform.IsElevated();

    public Task<InjectResult> InjectDllElevatedAsync(int pid, string dllPath, CancellationToken ct = default)
    {
        return Task.Run(() => _platform.InjectDllElevated(pid, dllPath), ct);
    }

    public Task<IReadOnlyList<DetectedGame>> FindUeGamesOnDrivesAsync(
        IReadOnlyList<DriveDescriptor> selectedDrives,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            // Requirement 5: resolve Steam library roots ONCE for exclusion.
            var steamRoots = new List<string>();
            try
            {
                var libs = await GetSteamLibraryFoldersAsync(ct);
                foreach (string lib in libs)
                {
                    string n = NormalizeDir(lib);
                    if (n.Length > 0)
                        steamRoots.Add(n);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn("ProxyDeploy", $"Steam-root resolve for exclusion failed: {ex.Message}");
            }

            // Requirement 2: partitions of one physical disk scan SEQUENTIALLY,
            // different disks scan in PARALLEL. Bound overall parallelism so a
            // many-disk box doesn't thrash a shared bus.
            var groups = GroupDrivesByPhysicalDisk(selectedDrives);
            using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, 4));

            var tasks = new List<Task<List<DetectedGame>>>(groups.Count);
            foreach (var group in groups)
            {
                tasks.Add(Task.Run(async () =>
                {
                    // Each group owns its list + dedupe set — no shared mutable
                    // state across parallel groups.
                    var local = new List<DetectedGame>();
                    var localSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await gate.WaitAsync(ct);
                    try
                    {
                        foreach (var drive in group)
                        {
                            ct.ThrowIfCancellationRequested();
                            string label = $"{drive.Letter}:";
                            progress?.Report(new DriveScanProgress(local.Count, label, "scanning"));
                            WalkDrive(drive.Root, 0, local, localSeen, steamRoots, progress, label, ct);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                    return local;
                }, ct));
            }

            var results = await Task.WhenAll(tasks);

            // Merge + global dedupe by BinariesDir (collapse a game reached via
            // two drives / a junction).
            var merged = new List<DetectedGame>();
            var globalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in results)
                foreach (var g in list)
                    if (globalSeen.Add(g.BinariesDir))
                        merged.Add(g);

            _log.Info("ProxyDeploy",
                $"Generic scan found {merged.Count} UE game(s) across {selectedDrives.Count} drive(s)");
            return (IReadOnlyList<DetectedGame>)merged;
        }, ct);
    }

    /// <summary>
    /// Bounded, prune-on-match recursive walk. When a directory looks like a UE
    /// game root it is handed to the EXISTING ScanGameFolder (all layout logic
    /// stays there) and NOT descended into (its Content\Paks is the multi-GB
    /// payload). Inaccessible folders are skipped and the walk continues
    /// (requirement 3). Writes only to the caller's local list/set — safe to run
    /// on a worker thread.
    /// </summary>
    private void WalkDrive(
        string dir, int depth,
        List<DetectedGame> games, HashSet<string> seenBinDirs,
        IReadOnlyList<string> steamRoots,
        IProgress<DriveScanProgress>? progress, string driveLabel,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxWalkDepth)
            return;

        // Skip reparse points (junctions/symlinks) to avoid cycles + drive re-entry.
        try
        {
            if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                return;
        }
        catch
        {
            return;
        }

        if (IsExcludedBySteam(dir, steamRoots))
            return;

        // Prune-on-match: reuse the Steam-path per-game-dir detector, then stop.
        if (LooksLikeUeGameRoot(dir))
        {
            int before = games.Count;
            ScanGameFolder(dir, games, seenBinDirs);
            if (games.Count > before)
                progress?.Report(new DriveScanProgress(games.Count, driveLabel, Path.GetFileName(dir)));
            return;
        }

        // Materialize the child list INSIDE the try: EnumerateDirectories is lazy,
        // so an UnauthorizedAccessException for opening `dir` (e.g. System Volume
        // Information) surfaces during iteration, not at the call. IgnoreInaccessible
        // additionally skips individual locked siblings without aborting the batch
        // (requirement 3).
        List<string> children;
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            children = new List<string>(Directory.EnumerateDirectories(dir, "*", opts));
        }
        catch
        {
            return; // access denied / gone — skip subtree (requirement 3)
        }

        foreach (string child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (HardSkipDirs.Contains(Path.GetFileName(child)))
                continue;
            WalkDrive(child, depth + 1, games, seenBinDirs, steamRoots, progress, driveLabel, ct);
        }
    }

    // ---- Pure helpers (unit-testable, no shared state / IO side effects) ----

    /// <summary>Canonicalize a directory path for prefix comparison: full path,
    /// no trailing separator. Returns empty on failure (caller treats as
    /// non-match / skip).</summary>
    internal static string NormalizeDir(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch { return string.Empty; }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Requirement 5: is <paramref name="path"/> at-or-under a resolved
    /// Steam library root? (The "steamapps" name is additionally hard-skipped
    /// during the walk as a fallback when root resolution fails.)</summary>
    internal static bool IsExcludedBySteam(string path, IReadOnlyList<string> steamRoots)
    {
        string norm = NormalizeDir(path);
        if (norm.Length == 0)
            return false;

        foreach (string root in steamRoots)
        {
            if (root.Length == 0)
                continue;
            if (string.Equals(norm, root, StringComparison.OrdinalIgnoreCase))
                return true;
            // Trailing separator guard so 'D:\SteamLib' does not match 'D:\SteamLibBackup'.
            string prefix = root + Path.DirectorySeparatorChar;
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Whether a directory name is hard-skipped by the generic walk.</summary>
    internal static bool IsHardSkipDir(string dirName) => HardSkipDirs.Contains(dirName);

    /// <summary>Requirement 4: cheap, stat-only structural test for a UE shipping
    /// game root. Tiers, most→least reliable: (1) a sibling Engine\Binaries\Win64
    /// next to a project dir with Binaries\Win64; (2) a project Content\Paks with
    /// *.pak/*.utoc/*.ucas; (3) a project Binaries\Win64 with a *-Win64-Shipping
    /// exe; (4) a flattened top-level shipping exe. Never enumerates the (huge)
    /// Content tree.</summary>
    internal static bool LooksLikeUeGameRoot(string dir)
    {
        try
        {
            // Tier 1 — canonical cooked tree.
            if (Directory.Exists(Path.Combine(dir, "Engine", "Binaries", "Win64")))
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (string.Equals(Path.GetFileName(sub), "Engine", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Directory.Exists(Path.Combine(sub, "Binaries", "Win64")))
                        return true;
                }
            }
            // Tier 2 — <Project>\Content\Paks\*.pak|*.utoc|*.ucas
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (string.Equals(Path.GetFileName(sub), "Engine", StringComparison.OrdinalIgnoreCase))
                    continue;
                string paks = Path.Combine(sub, "Content", "Paks");
                if (Directory.Exists(paks) && HasAnyFile(paks, "*.pak", "*.utoc", "*.ucas"))
                    return true;
            }
            // Tier 3 — <Project>\Binaries\Win64\*-Win64-Shipping.exe
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string bin = Path.Combine(sub, "Binaries", "Win64");
                if (Directory.Exists(bin) && HasAnyFile(bin, "*-Win64-Shipping.exe"))
                    return true;
            }
            // Tier 4 — flattened top-level shipping exe (some repacks).
            if (HasAnyFile(dir, "*-Win64-Shipping.exe"))
                return true;
        }
        catch
        {
            // Inaccessible — skip (requirement 3).
        }
        return false;
    }

    private static bool HasAnyFile(string dir, params string[] patterns)
    {
        foreach (string pat in patterns)
        {
            try
            {
                foreach (string _ in Directory.EnumerateFiles(dir, pat))
                    return true;
            }
            catch
            {
                // Inaccessible pattern enumeration — try the next.
            }
        }
        return false;
    }

    /// <summary>Requirement 2: partition drives into scan groups by physical disk.
    /// Drives sharing a physical disk number are grouped (scanned sequentially);
    /// a drive whose disk is unknown (null) gets its OWN singleton group (scanned
    /// in parallel, never serialized against an unrelated drive). Pure.</summary>
    internal static IReadOnlyList<IReadOnlyList<DriveDescriptor>> GroupDrivesByPhysicalDisk(
        IReadOnlyList<DriveDescriptor> drives)
    {
        var byDisk = new Dictionary<int, List<DriveDescriptor>>();
        var groups = new List<IReadOnlyList<DriveDescriptor>>();

        foreach (var d in drives)
        {
            if (d.PhysicalDiskNumber is int disk)
            {
                if (!byDisk.TryGetValue(disk, out var bucket))
                {
                    bucket = new List<DriveDescriptor>();
                    byDisk[disk] = bucket;
                    groups.Add(bucket); // preserve first-seen order, one entry per disk
                }
                bucket.Add(d);
            }
            else
            {
                groups.Add(new List<DriveDescriptor> { d }); // unknown → own group
            }
        }

        return groups;
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
            string[] allProxyNames = AllProxyDllNames();

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

    /// <summary>All distinct proxy DLL file names we ship. <c>Distinct</c> guards
    /// against a future enum value whose switch arm falls back to the default.</summary>
    public static string[] AllProxyDllNames() =>
        Enum.GetValues<ProxyType>()
            .Select(t => t.GetDllName())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>What an undeploy sweep decided to do, per file.</summary>
    /// <param name="ToDelete">Ours — safe to remove.</param>
    /// <param name="ForeignSkipped">Present but NOT ours (a mod loader, another
    /// tool, or the genuine Windows DLL). Never touched.</param>
    public readonly record struct UndeployPlan(
        IReadOnlyList<string> ToDelete,
        IReadOnlyList<string> ForeignSkipped);

    /// <summary>
    /// Decide which proxy DLLs an undeploy should remove. Pure so the policy can be
    /// tested without fabricating PE files with version resources
    /// (<see cref="IsOurProxyDll"/> reads <c>FileVersionInfo.ProductName</c>).
    /// </summary>
    public static UndeployPlan PlanUndeploy(
        IEnumerable<(string Name, bool Exists, bool IsOurs)> candidates)
    {
        var toDelete = new List<string>();
        var foreign = new List<string>();
        foreach (var (name, exists, isOurs) in candidates)
        {
            if (!exists) continue;
            if (isOurs) toDelete.Add(name);
            else foreign.Add(name);
        }
        return new UndeployPlan(toDelete, foreign);
    }

    /// <summary>
    /// Turn the outcome of an undeploy sweep into a status / message / success
    /// triple. Pure, and the single place the precedence rules live:
    /// a locked file outranks everything (it is the actionable failure); refusing
    /// to touch a foreign DLL is only a FAILURE when we removed nothing of ours,
    /// otherwise it is a note on an otherwise successful clean-up.
    /// </summary>
    public static (ProxyDeployStatus Status, string? Message, bool Success) ResolveUndeployOutcome(
        int removed, IReadOnlyList<string> foreignSkipped, IReadOnlyList<string> locked)
    {
        if (locked.Count > 0)
            return (ProxyDeployStatus.ErrorLocked,
                    $"File locked (game running?): {string.Join(", ", locked)}", false);

        if (removed == 0 && foreignSkipped.Count > 0)
            return (ProxyDeployStatus.OtherProxy,
                    $"Refused: not our proxy DLL ({string.Join(", ", foreignSkipped)})", false);

        if (foreignSkipped.Count > 0)
            return (ProxyDeployStatus.NotDeployed,
                    $"Left another program's {string.Join(", ", foreignSkipped)}", true);

        // removed >= 0 with nothing foreign: a clean folder is just as much a
        // success as one we emptied.
        return (ProxyDeployStatus.NotDeployed, null, true);
    }

    public Task<bool> UndeployAsync(DetectedGame game, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                // Sweep EVERY proxy flavour, not just the one selected in the UI.
                // The radio button picks what to DEPLOY; undeploy is a clean-up, and
                // a user who deployed dxgi.dll and later switched the radio to
                // version.dll would otherwise be unable to remove it at all — while
                // the grid happily reported DeployedOtherType.
                var plan = PlanUndeploy(AllProxyDllNames().Select(name =>
                {
                    string p = Path.Combine(game.BinariesDir, name);
                    bool exists = File.Exists(p);
                    return (name, exists, exists && IsOurProxyDll(p));
                }));

                var locked = new List<string>();
                int removed = 0;
                foreach (var name in plan.ToDelete)
                {
                    // Per-file try/catch: one locked DLL must not abandon the rest.
                    // Removing what we can is what the user asked for; the locked
                    // one is reported by name.
                    try
                    {
                        File.Delete(Path.Combine(game.BinariesDir, name));
                        removed++;
                        _log.Info("ProxyDeploy", $"Undeployed {name} from {game.Name}");
                    }
                    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) /* SHARING_VIOLATION */
                                              || ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
                    {
                        locked.Add(name);
                        _log.Warn("ProxyDeploy", $"Undeploy {name} from {game.Name} failed: file locked");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        locked.Add(name);
                        _log.Warn("ProxyDeploy", $"Undeploy {name} from {game.Name} denied: {ex.Message}");
                    }
                }

                var (status, message, success) =
                    ResolveUndeployOutcome(removed, plan.ForeignSkipped, locked);
                game.Status = status;
                game.ErrorMessage = message;
                if (removed > 0) game.InstalledVersion = null;
                return success;
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
    // Proxy Suggestion (import-table + remembered pick)
    // ────────────────────────────────────────────────────────────────

    public Task ApplyProxySuggestionsAsync(
        IReadOnlyList<DetectedGame> games,
        IReadOnlyDictionary<string, ProxyType> confirmedByExe,
        IReadOnlyDictionary<string, ProxyType> rememberedByGame,
        IReadOnlySet<string> injectedExes,
        bool enabled,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            foreach (var game in games)
            {
                ct.ThrowIfCancellationRequested();

                if (!enabled)
                {
                    game.SuggestedProxyType = null;
                    game.SuggestedProxy = null;
                    continue;
                }

                string exeName = Path.GetFileName(game.ExePath);
                ProxyType? confirmed =
                    confirmedByExe.TryGetValue(exeName, out var c) ? c : null;
                ProxyType? remembered =
                    rememberedByGame.TryGetValue(game.Name, out var p) ? p : null;
                bool injected = injectedExes.Contains(exeName);

                var imports = ReadProxyImports(game.ExePath);
                var suggestion = ProxyImportAnalyzer.Recommend(imports, confirmed, remembered, injected);

                game.SuggestedProxyType = suggestion.Type;
                game.SuggestedProxy = suggestion.Display;
            }
        }, ct);
    }

    /// <summary>Open a game .exe and parse only its PE headers/import directory
    /// (no full-file read) to learn which proxy DLLs it imports. Returns null when
    /// the file is missing/locked/malformed — the caller then shows no viability
    /// hint. The parsing itself lives in the pure, testable ProxyImportAnalyzer.</summary>
    private ProxyImportAnalyzer.ProxyImportInfo? ReadProxyImports(string exePath)
    {
        try
        {
            using var fs = new FileStream(
                exePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ProxyImportAnalyzer.Analyze(fs);
        }
        catch (Exception ex)
        {
            _log.Debug("ProxyDeploy", $"Import parse skipped for {exePath}: {ex.Message}");
            return null;
        }
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
