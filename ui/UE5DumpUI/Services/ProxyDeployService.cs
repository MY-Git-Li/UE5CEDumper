using System.Diagnostics;
using System.IO;
using System.Linq;
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
        //   4. Wrapped (NEKOPALIVE) — an extra folder between the game root and
        //      the project, and the exe is not named *-Win64-Shipping:
        //         <Game>\Package\<Sub>\Binaries\Win64\Nekopara.exe   ← real
        //         <Game>\Package\Engine\Binaries\Win64\CrashReportClient.exe
        //
        // Walking Engine\ unconditionally produces phantom rows for layouts 1+2
        // (the user sees both rows for the same game). Skipping Engine\ kills
        // layout 3 (Satisfactory). Solution: try primary roots first; only fall
        // back to Engine\Binaries\Win64\ when primary contributed no rows for
        // this gameDir.
        //
        // Layout 4 is why this searches to a bounded DEPTH rather than exactly one
        // level down. A single level finds <Game>\<Sub>\Binaries\Win64 and misses
        // <Game>\Package\<Sub>\Binaries\Win64 entirely — that is a whole game the
        // scan never sees, and the Engine fallback misses it too because the
        // Engine folder is not a direct child either.
        var primary = new List<(string Dir, int Depth)>();
        var engineRoots = new List<(string Dir, int Depth)>();
        CollectBinariesRoots(gameDir, gameDir, 0, primary, engineRoots);

        // SHALLOWEST DEPTH WINS. Depth is scanned ascending and the first depth that yields any
        // row stops the search — the same "primary first, fallback only if empty" shape as the
        // Engine tier below, applied to nesting.
        //
        // Without this, the depth-3 search turns bonus content into phantom rows: P3R ships
        // <Game>\P3R\Binaries\Win64 (depth 1, the real game) alongside
        // <Game>\Artbook\P3R_Artbook\Binaries\Win64 and <Game>\Soundtrack\P3R_Soundtrack\... —
        // and those two are genuinely UE apps with their own Engine folder, so no content-based
        // filter separates them. Depth does: the real game is shallower. A wrapped layout like
        // NEKOPALIVE has nothing at depth 1 at all, so it still reaches its depth-2 project.
        int gamesBefore = games.Count;
        foreach (var group in primary.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            foreach (var (root, _) in group)
                ScanBinariesDir(gameName, gameDir, root, games, seenBinDirs);
            if (games.Count != gamesBefore)
                break;
        }

        // Engine fallback: only walked when primary yielded zero rows for this
        // gameDir. Catches pure-modular layouts like Satisfactory where the
        // real launcher .exe lives in <Game>\Engine\Binaries\Win64\.
        if (games.Count == gamesBefore)
            foreach (var group in engineRoots.GroupBy(r => r.Depth).OrderBy(g => g.Key))
            {
                foreach (var (root, _) in group)
                    ScanBinariesDir(gameName, gameDir, root, games, seenBinDirs);
                if (games.Count != gamesBefore)
                    break;
            }
    }

    /// <summary>Deepest wrapper level below a game root that is still searched for a
    /// <c>Binaries\Win64</c>. 2 covers the observed <c>&lt;Game&gt;\Package\&lt;Sub&gt;\</c>
    /// wrapping with one level spare; going deeper buys nothing and costs directory walks
    /// across every game in the library.</summary>
    private const int MaxBinariesSearchDepth = 3;

    /// <summary>Directory names never worth descending into while looking for a
    /// <c>Binaries\Win64</c>. <c>Content</c> is the one that matters — a shipped game's
    /// content tree can hold thousands of folders, and none of them is a binaries root.</summary>
    private static readonly string[] BinariesSearchSkipDirs =
        { "Binaries", "Content", "Saved", "Intermediate", "Config", "DerivedDataCache", "Plugins" };

    /// <summary>
    /// Walk down from <paramref name="dir"/> collecting every directory that owns a
    /// <c>Binaries\Win64</c>, split into Engine-side and everything else so the caller can keep
    /// the two-tier "primary first, Engine only as fallback" rule that stops modular games
    /// producing two rows.
    /// </summary>
    private static void CollectBinariesRoots(
        string dir, string gameDir, int depth,
        List<(string Dir, int Depth)> primary, List<(string Dir, int Depth)> engineRoots)
    {
        // A root is worth recording whether or not it has a Binaries child — ScanBinariesDir
        // re-checks — but only descend while there is depth left. Depth travels with the root so
        // the caller can prefer shallower ones.
        bool underEngine = IsUnderEngineFolder(dir, gameDir);
        (underEngine ? engineRoots : primary).Add((dir, depth));

        if (depth >= MaxBinariesSearchDepth)
            return;

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (BinariesSearchSkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                CollectBinariesRoots(sub, gameDir, depth + 1, primary, engineRoots);
            }
        }
        catch
        {
            // Permission error / reparse point — this branch just contributes nothing.
        }
    }

    /// <summary>True when any path component between the game root and <paramref name="dir"/>
    /// (inclusive) is named <c>Engine</c>. Checking the whole relative path rather than the
    /// immediate parent is what makes the Engine tier work under a wrapper folder.</summary>
    private static bool IsUnderEngineFolder(string dir, string gameDir)
    {
        string rel = Path.GetRelativePath(gameDir, dir);
        if (rel == ".") return false;
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                  .Any(part => string.Equals(part, "Engine", StringComparison.OrdinalIgnoreCase));
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

    // THREADING CONTRACT for everything below that touches a DetectedGame.
    //
    // DetectedGame is an ObservableObject and its Status / InstalledVersion /
    // ErrorMessage / SuggestedProxy are bound to the Proxy Deploy DataGrid. Writing them
    // from inside a Task.Run raises PropertyChanged on a thread-pool thread, which lets
    // Avalonia mutate the visual tree while the render thread is composing it — that is an
    // access violation inside libSkiaSharp, not an exception, so it takes the whole app
    // down with no managed stack. It did: "Scan Steam" then "Update all" over 29 games
    // reliably crashed with 0xc0000005 in libSkiaSharp.
    //
    // So: do the file I/O on the worker, COLLECT the results, and APPLY them after the
    // await — which resumes on the caller's context. Every call site is a UI-thread
    // [RelayCommand] in ProxyDeployViewModel with no ConfigureAwait(false), so that
    // context is the UI thread. Keep it that way; do not add ConfigureAwait(false) to
    // these awaits, and do not reintroduce writes inside the Task.Run bodies.
    //
    // (This service deliberately does NOT reference Avalonia.Threading — in this codebase
    // Dispatcher.UIThread appears only in ViewModels and Views.)

    /// <summary>One game's post-operation state, computed off-thread and applied on the
    /// caller's thread. The two Set* flags exist because some paths deliberately leave a
    /// field untouched, and <c>null</c> is itself a meaningful value for the other two.</summary>
    private readonly record struct GameStatusUpdate(
        DetectedGame Game,
        ProxyDeployStatus Status,
        string? InstalledVersion = null,
        string? ErrorMessage = null,
        bool SetInstalledVersion = true,
        bool SetErrorMessage = true);

    private static void ApplyStatus(in GameStatusUpdate u)
    {
        u.Game.Status = u.Status;
        if (u.SetInstalledVersion) u.Game.InstalledVersion = u.InstalledVersion;
        if (u.SetErrorMessage)     u.Game.ErrorMessage     = u.ErrorMessage;
    }

    public async Task RefreshDeployStatusAsync(
        IList<DetectedGame> games, string sourceDllPath, ProxyType proxyType,
        CancellationToken ct = default)
    {
        // Snapshot so the worker never enumerates a collection the UI thread can mutate.
        var targets = games.ToList();

        var updates = await Task.Run(() =>
        {
            var results = new List<GameStatusUpdate>(targets.Count);
            string? sourceVersion = GetDllVersion(sourceDllPath);

            string selectedDllName = proxyType.GetDllName();
            string[] allProxyNames = AllProxyDllNames();

            foreach (var game in targets)
            {
                ct.ThrowIfCancellationRequested();

                string targetDll = Path.Combine(game.BinariesDir, selectedDllName);

                ProxyDeployStatus status;
                string? installedVersion = null;
                string? errorMessage = null;

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
                    var (absentStatus, message) = ClassifyAbsentSelected(deployedProxyNames);
                    status = absentStatus;
                    errorMessage = message;
                }
                else if (!IsOurProxyDll(targetDll))
                {
                    status = ProxyDeployStatus.OtherProxy;
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(targetDll);
                        errorMessage = $"Other proxy: {info.ProductName ?? info.FileDescription ?? "unknown"}";
                    }
                    catch
                    {
                        errorMessage = "Other proxy DLL detected";
                    }
                }
                else
                {
                    installedVersion = GetDllVersion(targetDll);
                    status = (sourceVersion != null && installedVersion == sourceVersion)
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
                    errorMessage = string.IsNullOrEmpty(errorMessage)
                                   ? conflictMsg
                                   : $"{errorMessage}; {conflictMsg}";
                }

                results.Add(new GameStatusUpdate(game, status, installedVersion, errorMessage));
            }

            return results;
        }, ct);

        // Back on the caller's thread — see the threading contract above.
        foreach (var u in updates) ApplyStatus(u);
    }

    // ────────────────────────────────────────────────────────────────
    // Deploy / Undeploy
    // ────────────────────────────────────────────────────────────────

    public async Task<bool> DeployAsync(string sourceDllPath, DetectedGame game, ProxyType proxyType,
        bool force = false, CancellationToken ct = default)
    {
        var (ok, update) = await Task.Run<(bool, GameStatusUpdate)>(() =>
        {
            try
            {
                string targetDll = Path.Combine(game.BinariesDir, proxyType.GetDllName());

                // Refuse to overwrite another program's proxy DLL
                if (File.Exists(targetDll) && !IsOurProxyDll(targetDll) && !force)
                {
                    return (false, new GameStatusUpdate(game, ProxyDeployStatus.OtherProxy,
                        ErrorMessage: "Refused: another program's proxy DLL",
                        SetInstalledVersion: false));
                }

                // Skip if same version (unless force)
                if (!force && File.Exists(targetDll) && IsOurProxyDll(targetDll))
                {
                    string? srcVer = GetDllVersion(sourceDllPath);
                    string? tgtVer = GetDllVersion(targetDll);
                    if (srcVer != null && srcVer == tgtVer)
                    {
                        // Already up to date — status only, version/error left as they were.
                        return (true, new GameStatusUpdate(game, ProxyDeployStatus.DeployedCurrent,
                            SetInstalledVersion: false, SetErrorMessage: false));
                    }
                }

                File.Copy(sourceDllPath, targetDll, overwrite: true);
                _log.Info("ProxyDeploy", $"Deployed {proxyType.GetDisplayName()} to {game.Name}: {targetDll}");
                return (true, new GameStatusUpdate(game, ProxyDeployStatus.DeployedCurrent,
                    InstalledVersion: GetDllVersion(targetDll)));
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) /* SHARING_VIOLATION */
                                      || ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("ProxyDeploy", $"Deploy to {game.Name} failed: file locked");
                return (false, new GameStatusUpdate(game, ProxyDeployStatus.ErrorLocked,
                    ErrorMessage: "File locked (game running?)", SetInstalledVersion: false));
            }
            catch (Exception ex)
            {
                _log.Error("ProxyDeploy", $"Deploy to {game.Name} failed: {ex.Message}");
                return (false, new GameStatusUpdate(game, ProxyDeployStatus.ErrorOther,
                    ErrorMessage: ex.Message, SetInstalledVersion: false));
            }
        }, ct);

        ApplyStatus(update);   // caller's thread — see the threading contract above
        return ok;
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

    public async Task<bool> UndeployAsync(DetectedGame game, CancellationToken ct = default)
    {
        var (ok, update) = await Task.Run<(bool, GameStatusUpdate)>(() =>
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
                // InstalledVersion is only cleared when something was actually removed.
                return (success, new GameStatusUpdate(game, status,
                    ErrorMessage: message, SetInstalledVersion: removed > 0));
            }
            catch (Exception ex)
            {
                _log.Error("ProxyDeploy", $"Undeploy from {game.Name} failed: {ex.Message}");
                return (false, new GameStatusUpdate(game, ProxyDeployStatus.ErrorOther,
                    ErrorMessage: ex.Message, SetInstalledVersion: false));
            }
        }, ct);

        ApplyStatus(update);   // caller's thread — see the threading contract above
        return ok;
    }

    // ────────────────────────────────────────────────────────────────
    // Proxy Suggestion (import-table + remembered pick)
    // ────────────────────────────────────────────────────────────────

    public async Task ApplyProxySuggestionsAsync(
        IReadOnlyList<DetectedGame> games,
        IReadOnlyDictionary<string, ProxyType> confirmedByExe,
        IReadOnlyDictionary<string, ProxyType> rememberedByGame,
        IReadOnlySet<string> injectedExes,
        bool enabled,
        CancellationToken ct = default)
    {
        var targets = games.ToList();

        var suggestions = await Task.Run(() =>
        {
            var results = new List<(DetectedGame Game, ProxyType? Type, string? Display)>(targets.Count);

            foreach (var game in targets)
            {
                ct.ThrowIfCancellationRequested();

                if (!enabled)
                {
                    results.Add((game, null, null));
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

                results.Add((game, suggestion.Type, suggestion.Display));
            }

            return results;
        }, ct);

        // Back on the caller's thread — see the threading contract above.
        // SuggestedProxy feeds a DataGrid column, so this pass is a visual-tree mutation
        // exactly like the status one, and reading 29 PE import tables is slow enough that
        // it used to land mid-render.
        foreach (var (game, type, display) in suggestions)
        {
            game.SuggestedProxyType = type;
            game.SuggestedProxy = display;
        }
    }

    /// <summary>Open a game .exe and parse only its PE headers/import directory
    /// (no full-file read) to learn which proxy DLLs it imports. Returns null when
    /// the file is missing/locked/malformed — the caller then shows no viability
    /// hint. The parsing itself lives in the pure, testable ProxyImportAnalyzer.</summary>
    private ProxyImportAnalyzer.ProxyImportInfo? ReadProxyImports(string exePath)
    {
        var info = AnalyzeOnePe(exePath);
        if (info is not { ImportsNone: true }) return info;

        // Importing NONE of the three is the signature of a MODULAR UE build: the
        // .exe is a thin bootstrap (Satisfactory's is ~264 KB) and the engine lives
        // in sibling *-Win64-Shipping.dll modules. Reading the stub alone made the
        // Suggested column claim "no dxgi/dinput8" for a game where a dxgi proxy
        // loads perfectly well (D3D12RHI imports it). A proxy activates if ANY
        // module in the process imports that name, so fold the siblings in.
        return info.Value.Merge(ReadModuleImports(Path.GetDirectoryName(exePath)));
    }

    /// <summary>Parse one PE's import directories. Returns null when the file is
    /// missing/locked/malformed — the caller then shows no viability hint.</summary>
    private ProxyImportAnalyzer.ProxyImportInfo? AnalyzeOnePe(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ProxyImportAnalyzer.Analyze(fs);
        }
        catch (Exception ex)
        {
            _log.Debug("ProxyDeploy", $"Import parse skipped for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Upper bound on modular-build modules parsed per game — a runaway
    /// guard, not a budget. Satisfactory ships 182 next to its stub, and the
    /// all-three short-circuit does NOT fire there (nothing imports dinput8), so
    /// the whole set really is walked; 512 keeps a comfortable margin rather than
    /// silently truncating the answer. Cheap because Analyze reads PE headers
    /// only, and this path is reached solely for a stub exe.</summary>
    private const int MaxModularModulesScanned = 512;

    /// <summary>OR the proxy-import flags of the <c>*-Win64-Shipping.dll</c> modules
    /// sitting next to a modular build's bootstrap .exe. Top-level only (no recursion)
    /// — that is where UE puts them, and it keeps this off the hot path of a library
    /// scan. Short-circuits once all three names are accounted for.</summary>
    private ProxyImportAnalyzer.ProxyImportInfo ReadModuleImports(string? dir)
    {
        var acc = new ProxyImportAnalyzer.ProxyImportInfo(false, false, false);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return acc;
        try
        {
            int seen = 0;
            foreach (string dll in Directory.EnumerateFiles(dir, "*-Win64-Shipping.dll"))
            {
                if (++seen > MaxModularModulesScanned) break;
                if (AnalyzeOnePe(dll) is { } m) acc = acc.Merge(m);
                if (acc is { ImportsVersion: true, ImportsDinput8: true, ImportsDxgi: true })
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("ProxyDeploy", $"Modular import scan skipped for {dir}: {ex.Message}");
        }
        return acc;
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
