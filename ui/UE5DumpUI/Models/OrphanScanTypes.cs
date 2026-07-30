namespace UE5DumpUI.Models;

/// <summary>
/// Why a candidate leftover-proxy folder may or may not be cleaned up.
///
/// <para>Only <see cref="Deletable"/> authorises removing the folder CHAIN.
/// <see cref="FileOnly"/> authorises recycling the DLL but not touching any directory — that is the
/// correct, expected outcome for a non-Steam install and must be shown to the user, not hidden.
/// Everything else is a refusal that carries a blocker message.</para>
/// </summary>
public enum OrphanVerdict
{
    /// <summary>Files are ours, the shape is right, the game is gone: recycle files + prune chain.</summary>
    Deletable,

    /// <summary>Files are ours and removable, but no directory may be removed (outside a Steam
    /// library, or the chain is too shallow to prune safely).</summary>
    FileOnly,

    /// <summary>Not a <c>...\Binaries\Win64</c> folder.</summary>
    NotUeShape,

    /// <summary>The folder holds no files at all, so there is nothing of ours to remove.</summary>
    NoFilesAtAll,

    /// <summary>At least one file in the folder is not ours. Universal quantifier — one foreign
    /// file blocks the whole folder.</summary>
    ForeignFilePresent,

    /// <summary>The folder has a subdirectory (even an empty one).</summary>
    HasSubdirectories,

    /// <summary>A file's version info could not be read, so ownership is unknown. Fails closed.</summary>
    UnreadableDll,

    /// <summary>The directory listing failed. NEVER treated as "empty".</summary>
    UnreadableDirectory,

    /// <summary>A junction, symlink or other reparse point sits somewhere in the chain. Refused
    /// outright with an EMPTY plan — measured to destroy the junction target otherwise.</summary>
    ReparsePointInChain,

    /// <summary>The candidate is the Steam library root itself.</summary>
    AtCeilingRoot,

    /// <summary>Too few folders between the library root and the leaf to prune safely.</summary>
    BelowMinimumDepth,

    /// <summary>The chain left the known library root before reaching the game folder.</summary>
    OutsideKnownCeiling,

    /// <summary>A Steam <c>appmanifest</c> still names this install directory — the game is
    /// installed (or mid-update). Refused, full stop.</summary>
    SteamManifestPresent,

    /// <summary>Live cooked game content (a shipping exe, or <c>Content\Paks</c>) exists under the
    /// game root, so the game is not gone.</summary>
    LiveContentPresent,

    /// <summary>The folder is the binaries directory of a game the panel already lists as installed.</summary>
    LiveGameFolder,

    /// <summary>Not on a fixed drive, so the Recycle Bin is unavailable and a delete would be
    /// permanent. Refused rather than silently hard-deleting.</summary>
    NotOnFixedDrive,
}

/// <summary>Where a candidate directory came from. Flags so a row can credit several sources.</summary>
[Flags]
public enum OrphanScanSources
{
    None = 0,

    /// <summary>Bounded shape scan of the Steam libraries. The authoritative source — it is the
    /// only one that sees a leftover nobody ever logged.</summary>
    SteamShapeScan = 1,

    /// <summary>The UI's own "Deployed … : &lt;path&gt;" log lines. Covers deployed-but-never-launched
    /// and non-Steam targets, with correct Unicode.</summary>
    DeployLog = 2,

    /// <summary>The DLL's own load banner. Covers non-Steam locations the Steam scan cannot see,
    /// but only for folders a game actually RAN from.</summary>
    DllLoadLog = 4,

    /// <summary>The persisted deployed-path ledger in ui-options.json. No retention window.</summary>
    Ledger = 8,

    All = SteamShapeScan | DeployLog | DllLoadLog | Ledger,
}

/// <summary>Whether a file in a candidate folder belongs to us.</summary>
public enum FileOwnership
{
    /// <summary>Positively identified as one of our binaries.</summary>
    Ours,

    /// <summary>Readable and definitely not ours.</summary>
    Foreign,

    /// <summary>Could not be determined. Always treated as a refusal (fails closed).</summary>
    Unreadable,
}

/// <summary>
/// A directory's contents as DATA, so the policy can be tested without a filesystem.
/// <paramref name="FileNames"/> and <paramref name="SubDirNames"/> are leaf names, not full paths.
/// </summary>
public sealed record DirSnapshot(
    string Path,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<string> SubDirNames,
    bool IsReparsePoint);

/// <summary>Reads a directory. Returns null for "missing or unreadable", which always refuses.</summary>
public delegate DirSnapshot? DirProbe(string path);

/// <summary>Decides whether one file is ours.</summary>
public delegate FileOwnership OwnedFileProbe(string fullPath);

/// <summary>
/// Answers "is this game actually gone?" for a Steam game folder. Two independent signals are
/// checked by the real implementation (no appmanifest naming the install dir, and no executable
/// anywhere under the game root); either one present vetoes the cleanup.
/// </summary>
public delegate LivenessResult LivenessProbe(string commonRoot, string gameFolderName, string gameRoot);

/// <summary>Outcome of a liveness check. <see cref="OrphanVerdict.Deletable"/> means "really gone".</summary>
public readonly record struct LivenessResult(OrphanVerdict Verdict, string Reason, string Evidence);

/// <summary>
/// The complete removal plan for one candidate. <paramref name="DirsToRemove"/> is DEEPEST-FIRST and
/// never includes the ceiling. A dry run and the real run share this object, so what the
/// confirmation window lists cannot diverge from what is acted on.
/// </summary>
public readonly record struct PrunePlan(
    OrphanVerdict Verdict,
    IReadOnlyList<string> FilesToRecycle,
    IReadOnlyList<string> DirsToRemove,
    string? CeilingRoot,
    IReadOnlyList<string> Blockers,
    string EvidenceText);

/// <summary>Progress from the orphan scan, for the panel's status line.</summary>
public readonly record struct OrphanScanProgress(string CurrentPath, int Examined, int Found);

/// <summary>What actually happened when a row was removed. Immutable so it can cross a thread.</summary>
public readonly record struct OrphanRemovalResult(
    bool Success,
    string Message,
    int FilesRecycled,
    int DirsRemoved);
