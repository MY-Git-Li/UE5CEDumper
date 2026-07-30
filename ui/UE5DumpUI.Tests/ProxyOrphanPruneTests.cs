using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// The parts of the leftover-proxy cleanup that only a REAL filesystem can prove. Deliberately
/// narrow: everything expressible as policy lives in <see cref="ProxyOrphanScannerTests"/>, and what
/// remains here are the three measured OS behaviours the whole design rests on.
///
/// <para>Each of these pins a decision that looks like a style choice and is not:</para>
/// <list type="number">
/// <item>the NON-recursive <c>Directory.Delete</c> overload is a kernel-enforced emptiness check, and
/// it is what makes the pre-computed plan safe to act on later;</item>
/// <item>the BARE <c>EnumerateFileSystemEntries</c> overload is required, because the
/// <c>EnumerationOptions</c> one hides Hidden|System entries by default — a folder holding a hidden
/// save file would otherwise read as holding only our DLL;</item>
/// <item>the prune walk stops at the first non-empty ancestor and leaves everything above it.</item>
/// </list>
/// </summary>
public sealed class ProxyOrphanPruneTests : IDisposable
{
    private readonly string _root;

    public ProxyOrphanPruneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ue5dump-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Swallowing on purpose, following the LogRetentionTests idiom: a cleanup failure must not
        // mask the real assertion failure. NOT the bare `finally { Directory.Delete(...) }` some
        // older tests use — for a deletion feature that throws DirectoryNotFoundException and
        // replaces the actual diagnosis.
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeChain(string gameName, params string[] between)
    {
        string common = Path.Combine(_root, "steamapps", "common");
        string cursor = Path.Combine(common, gameName);
        foreach (string seg in between) cursor = Path.Combine(cursor, seg);
        cursor = Path.Combine(cursor, "Binaries", "Win64");
        Directory.CreateDirectory(cursor);
        return cursor;
    }

    /// <summary>The production prune loop, verbatim in shape: deepest-first, non-recursive, stop on
    /// the first throw. Mirrors ProxyDeployService.RemoveOrphanProxyAsync so this test exercises the
    /// real semantics rather than a paraphrase.</summary>
    private static int PruneLikeProduction(IReadOnlyList<string> dirsDeepestFirst)
    {
        int removed = 0;
        foreach (string dir in dirsDeepestFirst)
        {
            try
            {
                Directory.Delete(dir, recursive: false);
                removed++;
            }
            catch
            {
                break;
            }
        }
        return removed;
    }

    private static List<string> ChainUpTo(string leaf, string stopAfter)
    {
        var chain = new List<string>();
        string? cursor = leaf;
        while (cursor != null)
        {
            chain.Add(cursor);
            if (string.Equals(cursor, stopAfter, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor);
        }
        return chain;
    }

    // ── 1. the kernel-enforced emptiness check ───────────────────────────────

    [Fact]
    public void NonRecursiveDelete_ThrowsOnNonEmptyDirectory()
    {
        // This is the safety net the whole design leans on: it means anything created between the
        // user confirming and the delete running simply stops the walk, with no extra check needed.
        string dir = Path.Combine(_root, "notempty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "someone-elses-file.txt"), "x");

        Assert.ThrowsAny<IOException>(() => Directory.Delete(dir, recursive: false));
        Assert.True(Directory.Exists(dir));
    }

    // ── 2. the enumeration overload choice ───────────────────────────────────

    [Fact]
    public void BareEnumeration_SeesHiddenAndSystemFilesThatOptionsOverloadHides()
    {
        // MEASURED behaviour, and the reason the production probe must use the bare overload:
        // new EnumerationOptions() defaults to AttributesToSkip = Hidden | System, so a folder
        // holding a hidden save file enumerates as holding only our DLL.
        string dir = Path.Combine(_root, "hidden");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version.dll"), "ours");
        string secret = Path.Combine(dir, "desktop.ini");
        File.WriteAllText(secret, "x");
        File.SetAttributes(secret, FileAttributes.Hidden | FileAttributes.System);

        int bare = Directory.EnumerateFileSystemEntries(dir).Count();
        int withOptions = Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions()).Count();

        Assert.Equal(2, bare);              // the guard sees the hidden file and will refuse
        Assert.Equal(1, withOptions);       // the trap: reads as "only our DLL"
        Assert.NotEqual(bare, withOptions);
    }

    // ── 3. the walk stops at the first non-empty ancestor ────────────────────

    [Fact]
    public void Prune_RemovesTheWholeChainAndLeavesCommonIntact()
    {
        string leaf = MakeChain("FakeGame", "Game");
        File.WriteAllText(Path.Combine(leaf, "version.dll"), "ours");
        string common = Path.Combine(_root, "steamapps", "common");
        string gameRoot = Path.Combine(common, "FakeGame");

        File.Delete(Path.Combine(leaf, "version.dll"));    // stands in for the recycle step
        int removed = PruneLikeProduction(ChainUpTo(leaf, gameRoot));

        Assert.Equal(4, removed);                       // Win64, Binaries, Game, FakeGame
        Assert.False(Directory.Exists(gameRoot));
        Assert.True(Directory.Exists(common));          // the ceiling survives
    }

    [Fact]
    public void Prune_StopsAtAnAncestorHoldingASiblingDirectory()
    {
        // The measured Deep Rock Galactic / Romancing SaGa shape: the project folder also holds
        // Mods\ and Saved\. The DLL still goes, and the walk stops there rather than refusing —
        // which is exactly why the plan does not pre-assert ancestor emptiness.
        string leaf = MakeChain("DRG", "FSD");
        string proj = Path.GetDirectoryName(Path.GetDirectoryName(leaf))!;
        Directory.CreateDirectory(Path.Combine(proj, "Saved"));
        string gameRoot = Path.Combine(_root, "steamapps", "common", "DRG");

        int removed = PruneLikeProduction(ChainUpTo(leaf, gameRoot));

        Assert.Equal(2, removed);                                   // Win64 + Binaries only
        Assert.True(Directory.Exists(proj));                        // stopped here
        Assert.True(Directory.Exists(Path.Combine(proj, "Saved"))); // the user's saves are intact
        Assert.True(Directory.Exists(gameRoot));
    }

    [Fact]
    public void Prune_StopsWhenSomethingAppearsAfterThePlanWasBuilt()
    {
        // TOCTOU: the plan is computed, the user reads the dialog, and meanwhile something lands in
        // an ancestor. Nothing above that point may be removed.
        string leaf = MakeChain("RaceGame", "Proj");
        string gameRoot = Path.Combine(_root, "steamapps", "common", "RaceGame");
        var plan = ChainUpTo(leaf, gameRoot);            // plan built BEFORE the interference

        File.WriteAllText(Path.Combine(gameRoot, "appeared-later.txt"), "x");
        int removed = PruneLikeProduction(plan);

        Assert.Equal(3, removed);                        // Win64, Binaries, Proj
        Assert.True(Directory.Exists(gameRoot));        // refused: it is no longer empty
        Assert.True(File.Exists(Path.Combine(gameRoot, "appeared-later.txt")));
    }

    // ── the Recycle Bin P/Invoke: the only thing between "undoable" and "gone" ──

    [Fact]
    public void MoveToRecycleBin_ActuallyRemovesTheFileAndReportsSuccess()
    {
        // THE single highest-consequence untested thing in this feature. Everything the cleanup
        // removes is a file, and the entire safety argument is "it goes to the Recycle Bin, so a
        // wrong call is recoverable". If the SHFILEOPSTRUCTW marshalling is wrong — a field widened,
        // a Pack added, the double-null pFrom lost — FOF_ALLOWUNDO never reaches the shell, the call
        // can still return 0, and the file is HARD deleted while we report success. Only a real call
        // can catch that, so this test makes one.
        string root = Path.GetPathRoot(Path.GetFullPath(_root)) ?? "";
        Assert.SkipWhen(root.Length == 0 || new DriveInfo(root).DriveType != DriveType.Fixed,
                        "temp is not on a fixed drive, so there is no Recycle Bin to use");

        string file = Path.Combine(_root, "recycle-me.tmp");
        File.WriteAllText(file, "leftover proxy stand-in");
        Assert.True(File.Exists(file));

        var platform = new Services.WindowsPlatformService();
        bool ok = platform.MoveToRecycleBin(file);

        Assert.True(ok);                    // a marshalling error gives rc=0x57 and false
        Assert.False(File.Exists(file));    // and it really left the folder
    }

    [Fact]
    public void MoveToRecycleBin_MissingFile_ReportsFailureRatherThanSuccess()
    {
        // Fails closed: the caller gates the folder prune on this returning true, so a false success
        // here would prune a chain whose file is still present.
        var platform = new Services.WindowsPlatformService();
        Assert.False(platform.MoveToRecycleBin(Path.Combine(_root, "does-not-exist.tmp")));
    }

    // ── the probe's own contract on a real filesystem ────────────────────────

    [Fact]
    public void ClassifyLeaf_OverARealFolderWithAHiddenFile_KeepsTheFolderNotTheFile()
    {
        // End-to-end over real IO: a real hidden file must reach the policy and stop the FOLDER from
        // being prunable, while our DLL is still collected for recycling. Pins that the production
        // probe and the policy agree, not just that each is individually right.
        string dir = Path.Combine(_root, "realleaf");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version.dll"), "ours");
        string secret = Path.Combine(dir, "Thumbs.db");
        File.WriteAllText(secret, "x");
        File.SetAttributes(secret, FileAttributes.Hidden);

        var files = new List<string>();
        var subs = new List<string>();
        foreach (string e in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(e)) subs.Add(Path.GetFileName(e));
            else files.Add(Path.GetFileName(e));
        }
        var snap = new DirSnapshot(dir, files, subs, false);

        var verdict = ProxyOrphanScanner.ClassifyLeaf(
            snap,
            full => Path.GetFileName(full).Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                ? FileOwnership.Ours : FileOwnership.Foreign,
            out var ours, out var blockers, out bool prunable);

        Assert.Equal(OrphanVerdict.Deletable, verdict);
        Assert.Equal(new[] { "version.dll" }, ours);   // our litter still goes
        Assert.False(prunable);                         // the folder does not
        Assert.Contains("Thumbs.db", string.Join(" ", blockers), StringComparison.Ordinal);
    }
}
