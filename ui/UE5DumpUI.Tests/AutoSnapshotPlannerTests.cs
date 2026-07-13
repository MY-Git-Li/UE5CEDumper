using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class AutoSnapshotPlannerTests
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    // --- NextGapSeconds -------------------------------------------------

    [Fact]
    public void NextGap_ShortCapture_LeavesInterval()
    {
        // 900 s interval, 120 s capture → 780 s until the next start.
        Assert.Equal(780, AutoSnapshotPlanner.NextGapSeconds(900, 120, 60));
    }

    [Fact]
    public void NextGap_LongCapture_FloorsAtMinGap()
    {
        // Capture nearly as long as the interval → the 60 s idle floor applies
        // (effective interval auto-extends past the configured value).
        Assert.Equal(60, AutoSnapshotPlanner.NextGapSeconds(900, 880, 60));
    }

    [Fact]
    public void NextGap_CaptureLongerThanInterval_StillFloorsAtMinGap()
    {
        Assert.Equal(60, AutoSnapshotPlanner.NextGapSeconds(900, 1000, 60));
    }

    // --- EvaluatePostCapture -------------------------------------------

    [Fact]
    public void Evaluate_FixedCount_StopsAtTarget()
    {
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.FixedCount, desiredCount: 2, autoCapturedCount: 2,
            lastSnapshotBytes: 10 * Mb, quotaBytes: 5 * Gb, adjustQuota: false);
        Assert.Equal(AutoStopReason.ReachedCount, r);
    }

    [Fact]
    public void Evaluate_FixedCount_ContinuesBeforeTarget()
    {
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.FixedCount, desiredCount: 3, autoCapturedCount: 1,
            lastSnapshotBytes: 10 * Mb, quotaBytes: 5 * Gb, adjustQuota: false);
        Assert.Equal(AutoStopReason.None, r);
    }

    [Fact]
    public void Evaluate_QuotaHoldsOne_StopsWhenQuotaTooSmallAndAdjustOff()
    {
        // 1 GB quota, snapshot 600 MB (> quota/2) → can't hold 2, wants 3, adjust off.
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.KeepRecent, desiredCount: 3, autoCapturedCount: 1,
            lastSnapshotBytes: 600 * Mb, quotaBytes: 1 * Gb, adjustQuota: false);
        Assert.Equal(AutoStopReason.QuotaHoldsOne, r);
    }

    [Fact]
    public void Evaluate_QuotaHoldsOne_SuppressedWhenAdjustOn()
    {
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.KeepRecent, desiredCount: 3, autoCapturedCount: 1,
            lastSnapshotBytes: 600 * Mb, quotaBytes: 1 * Gb, adjustQuota: true);
        Assert.Equal(AutoStopReason.None, r);
    }

    [Fact]
    public void Evaluate_QuotaHoldsOne_SuppressedWhenKeepingOnlyOne()
    {
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.KeepRecent, desiredCount: 1, autoCapturedCount: 1,
            lastSnapshotBytes: 600 * Mb, quotaBytes: 1 * Gb, adjustQuota: false);
        Assert.Equal(AutoStopReason.None, r);
    }

    [Fact]
    public void Evaluate_UnlimitedQuota_NeverHoldsOne()
    {
        var r = AutoSnapshotPlanner.EvaluatePostCapture(
            AutoSnapshotRetentionMode.KeepRecent, desiredCount: 3, autoCapturedCount: 1,
            lastSnapshotBytes: 10 * Gb, quotaBytes: 0, adjustQuota: false);
        Assert.Equal(AutoStopReason.None, r);
    }

    // --- RaiseQuotaBytes -----------------------------------------------

    [Fact]
    public void RaiseQuota_AlreadyBigEnough_NoChange()
    {
        // 5 GB quota, wants 2 × 1 GB (× 1.2 = 2.4 GB) → already fits.
        Assert.Equal(5 * Gb, AutoSnapshotPlanner.RaiseQuotaBytes(5 * Gb, 2, 1 * Gb));
    }

    [Fact]
    public void RaiseQuota_GrowsToNextFittingPreset()
    {
        // 512 MB quota, wants 3 × 200 MB (× 1.2 = 720 MB) → next preset ≥ 720 MB is 1 GB.
        Assert.Equal(1 * Gb, AutoSnapshotPlanner.RaiseQuotaBytes(512 * Mb, 3, 200 * Mb));
    }

    [Fact]
    public void RaiseQuota_ExceedsAllPresets_ReturnsNullForUnlimited()
    {
        // Wants 10 × 1 GB (12 GB) — larger than the 5 GB top preset → Unlimited.
        Assert.Null(AutoSnapshotPlanner.RaiseQuotaBytes(5 * Gb, 10, 1 * Gb));
    }

    [Fact]
    public void RaiseQuota_AlreadyUnlimited_StaysUnlimited()
    {
        Assert.Equal(0, AutoSnapshotPlanner.RaiseQuotaBytes(0, 5, 1 * Gb));
    }

    [Fact]
    public void RaiseQuota_UnknownSize_NoChange()
    {
        Assert.Equal(1 * Gb, AutoSnapshotPlanner.RaiseQuotaBytes(1 * Gb, 3, 0));
    }
}
