using System;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure helpers for judging whether a streamed snapshot capture stayed
/// CONSISTENT — i.e. the game's GObjects population didn't churn underneath it.
///
/// A snapshot pages the live FUObjectArray over several seconds. If the game
/// loads a level or mass-spawns/frees objects mid-capture, early chunks and late
/// chunks describe DIFFERENT worlds: some captured rows reference objects that
/// were freed (and possibly recycled) by the time later chunks ran, so the
/// snapshot is a temporally-inconsistent mix. Such a snapshot is later consumed
/// OFFLINE by SPC Query and Class Pivot, where the inconsistency silently
/// corrupts cross-snapshot joins — so it must be flagged at capture time.
///
/// Signal: the GObjects count (FUObjectArray::NumElements) reported by every
/// chunk RX. A real level transition swings NumElements by THOUSANDS; ordinary
/// frame-to-frame churn (a few projectiles/effects) is tens. The bound is
/// deliberately generous so normal gameplay never false-flags a good capture
/// (a false positive would auto-delete the user's good offline data).
///
/// Pure (no I/O) so the threshold is unit-tested and locked at build time.
/// </summary>
public static class SnapshotConsistency
{
    /// <summary>Absolute floor for the count-span that marks a capture suspect.
    /// Below this, even on a tiny game, the swing is ordinary churn.</summary>
    public const int DriftFloor = 2000;

    /// <summary>Fractional bound (of the begin-time count) above the floor — so a
    /// large game needs a proportionally larger swing, not a fixed 2000.</summary>
    public const double DriftFraction = 0.02;  // 2%

    /// <summary>The count-span (max − min GObjects count observed across the
    /// capture, vs the begin count) beyond which the capture is judged suspect.</summary>
    public static int DriftThreshold(int beginTotal)
        => Math.Max(DriftFloor, (int)(beginTotal * DriftFraction));

    /// <summary>True when the observed GObjects count drifted enough during the
    /// capture to make the snapshot temporally inconsistent. <paramref name="minTotal"/>
    /// / <paramref name="maxTotal"/> are the smallest / largest per-chunk totals seen;
    /// the begin count is folded in so a drift that only shows up after the first
    /// chunk is still caught. Returns false for a non-positive begin count
    /// (nothing to compare) so an empty/early-failed capture is never mis-flagged.</summary>
    public static bool IsDriftSuspect(int beginTotal, int minTotal, int maxTotal)
    {
        if (beginTotal <= 0) return false;
        int lo = Math.Min(minTotal, beginTotal);
        int hi = Math.Max(maxTotal, beginTotal);
        return (hi - lo) > DriftThreshold(beginTotal);
    }
}
