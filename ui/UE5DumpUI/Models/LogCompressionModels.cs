using System;
using System.Collections.Generic;

namespace UE5DumpUI.Models;

/// <summary>What the policy decided about one log file. Every value except
/// <see cref="Compress"/> is a REASON, not a failure — the UI reports the counts so a
/// sweep that compresses nothing can say why instead of looking broken.</summary>
public enum LogCompressionDecision
{
    /// <summary>Eligible — hand this path to the compressor.</summary>
    Compress,
    /// <summary>A <c>-0.log</c> that is still plausibly being written: either one of THIS
    /// process's own files, or a game's newest log that has not yet gone quiet.</summary>
    SkipLive,
    /// <summary>Already smaller on disk than its length — compressed on an earlier pass.</summary>
    SkipAlreadyCompressed,
    /// <summary>At or below the size floor; compressing it would cost more than it saves.</summary>
    SkipTooSmall,
    /// <summary>Written too recently — something may still be appending to it.</summary>
    SkipTooFresh,
}

/// <summary>
/// One candidate as the policy sees it. <paramref name="OnDiskBytes"/> is
/// <c>GetCompressedFileSizeW</c>, which is the ONLY reliable "is it already compressed"
/// signal for LZX — see <see cref="Services.LogCompressionPolicy"/>.
/// </summary>
/// <param name="OwnedByThisProcess">The file lives in the UI's own log folder, so a
/// Serilog sink holds it open for the whole session. Known for a fact, unlike a game
/// folder's files, where liveness can only be inferred from age.</param>
public readonly record struct LogFileEntry(
    string Path, string Name, long Length, long OnDiskBytes, DateTime LastWriteUtc,
    bool OwnedByThisProcess = false);

/// <summary>The compressor's work list plus the reason every other file was left out.</summary>
public sealed class LogCompressionPlan
{
    public List<string> ToCompress { get; } = new();
    public int SkippedLive { get; set; }
    public int SkippedAlreadyCompressed { get; set; }
    public int SkippedTooSmall { get; set; }
    public int SkippedTooFresh { get; set; }
    /// <summary>Logical bytes of the files in <see cref="ToCompress"/> (what the ratio is against).</summary>
    public long CandidateBytes { get; set; }
}

/// <summary>
/// Outcome of a sweep. <paramref name="BytesBefore"/> / <paramref name="BytesAfter"/> are
/// ON-DISK sizes of the attempted files, measured before and after — never parsed out of
/// <c>compact.exe</c>'s stdout, which reports success for a batch in which individual
/// files failed (see docs/log-compression-eval.md §4).
/// </summary>
public readonly record struct LogCompressionResult(
    bool Supported,
    int Compressed,
    int Failed,
    int SkippedLive,
    int SkippedAlreadyCompressed,
    int SkippedTooSmall,
    int SkippedTooFresh,
    long BytesBefore,
    long BytesAfter)
{
    public long BytesSaved => BytesBefore - BytesAfter;
    /// <summary>True when the sweep had nothing at all to do (not an error).</summary>
    public bool NothingToDo => Compressed == 0 && Failed == 0;
}
