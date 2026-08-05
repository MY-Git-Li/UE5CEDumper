using System;
using System.Collections.Generic;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure policy for the log-compression sweep: which files are worth compressing, and how
/// to split them into <c>compact.exe</c> command lines. Zero <c>System.IO</c> and zero
/// process spawning — the same split as <see cref="AppDataRetentionPolicy"/> /
/// <c>ProxyOrphanScanner</c>, so the rules can be tested without a disk.
///
/// <para>All four skip reasons and the batching rule come from measurements on the real
/// 111.9 MB log folder; the numbers and the traps behind them are in
/// <c>docs/log-compression-eval.md</c>. The three that are NOT obvious:</para>
/// <list type="bullet">
///   <item><b>"Already compressed" cannot use <c>FileAttributes.Compressed</c>.</b> LZX
///   (WOF) does not set it — only <c>compact /c</c> (LZNT1) does. Measured: an LZX file at
///   335,872 bytes on disk out of 8,441,784 still reported attributes <c>Archive</c> alone.
///   The signal is <c>GetCompressedFileSizeW &lt; Length</c>. (NTFS sparse files would also
///   satisfy that; log files are never sparse.)</item>
///   <item><b>The size floor is worth more than it looks.</b> 347 of 698 files were ≤ 4 KB
///   and held 0.4 MB between them — half the work for 0.4 % of the bytes.</item>
///   <item><b>Batches are capped by CHARACTERS, not just count.</b> Per-game log folders
///   are named after the game EXE, so paths are long and contain spaces; a batch that
///   overflows the command line would fail as a unit.</item>
/// </list>
/// </summary>
public static class LogCompressionPolicy
{
    /// <summary>The live file of a session — a logger holds it open and will keep
    /// appending. Compressing it is both futile (an append fully decompresses an LZX file:
    /// measured 335,872 → 8,441,799 bytes) and pointless (the handle blocks it anyway).</summary>
    public static bool IsLiveLog(string? name) =>
        name != null && name.EndsWith("-0.log", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decide one file. Order of the checks is the order of the reasons the UI
    /// reports, cheapest and most-specific first.</summary>
    public static LogCompressionDecision Decide(
        in LogFileEntry f, DateTime nowUtc, TimeSpan minIdle, long minSizeBytes)
    {
        if (IsLiveLog(f.Name))                return LogCompressionDecision.SkipLive;
        if (f.OnDiskBytes > 0 &&
            f.OnDiskBytes < f.Length)         return LogCompressionDecision.SkipAlreadyCompressed;
        if (f.Length <= minSizeBytes)         return LogCompressionDecision.SkipTooSmall;
        if (nowUtc - f.LastWriteUtc < minIdle) return LogCompressionDecision.SkipTooFresh;
        return LogCompressionDecision.Compress;
    }

    /// <summary>Build the work list plus the per-reason skip counts.</summary>
    public static LogCompressionPlan Plan(
        IEnumerable<LogFileEntry> files, DateTime nowUtc, TimeSpan minIdle, long minSizeBytes)
    {
        var plan = new LogCompressionPlan();
        if (files == null) return plan;

        foreach (var f in files)
        {
            switch (Decide(f, nowUtc, minIdle, minSizeBytes))
            {
                case LogCompressionDecision.SkipLive:               plan.SkippedLive++; break;
                case LogCompressionDecision.SkipAlreadyCompressed:  plan.SkippedAlreadyCompressed++; break;
                case LogCompressionDecision.SkipTooSmall:           plan.SkippedTooSmall++; break;
                case LogCompressionDecision.SkipTooFresh:           plan.SkippedTooFresh++; break;
                default:
                    plan.ToCompress.Add(f.Path);
                    plan.CandidateBytes += f.Length;
                    break;
            }
        }
        return plan;
    }

    /// <summary>
    /// Split paths into <c>compact.exe</c> batches, bounded by BOTH a file count and a
    /// command-line character budget.
    ///
    /// <para>The character budget is the one that actually matters: Windows caps a command
    /// line at 32,767 characters, and these paths are long (a per-game folder is named
    /// after the game EXE). Overflowing it fails the whole batch, so the budget is set well
    /// under the cap. Each path costs its length plus two quotes and a separating space.</para>
    ///
    /// <para>A single path longer than the whole budget still gets its own batch rather than
    /// being dropped — silently losing a file is worse than one over-long command line that
    /// the OS will reject and we will report as a failure.</para>
    /// </summary>
    public static List<List<string>> Batch(
        IReadOnlyList<string> paths, int maxPerBatch, int maxCommandLineChars)
    {
        var batches = new List<List<string>>();
        if (paths == null || paths.Count == 0) return batches;
        if (maxPerBatch < 1) maxPerBatch = 1;

        var current = new List<string>();
        int chars = 0;
        foreach (var p in paths)
        {
            int cost = p.Length + 3;                 // two quotes + one separating space
            if (current.Count > 0 &&
                (current.Count >= maxPerBatch || chars + cost > maxCommandLineChars))
            {
                batches.Add(current);
                current = new List<string>();
                chars = 0;
            }
            current.Add(p);
            chars += cost;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
