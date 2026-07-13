using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotDiskGuardTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Required_1TbDrive_TakesTheSmallerAbsoluteFloor()
    {
        // 1 TB at 10% ≈ 102 GB vs a 50 GB floor → the 50 GB floor wins (以最低值為準).
        long total = 1024L * Gb;
        Assert.Equal(50 * Gb, SnapshotDiskGuard.RequiredFreeBytes(total, 10, 50));
    }

    [Fact]
    public void Required_SmallDrive_TakesTheSmallerPercentage()
    {
        // 200 GB at 10% = 20 GB vs a 50 GB floor → the 20 GB percentage wins.
        long total = 200L * Gb;
        Assert.Equal(20 * Gb, SnapshotDiskGuard.RequiredFreeBytes(total, 10, 50));
    }

    [Fact]
    public void Required_PercentZero_UsesGbFloorAlone()
    {
        // Percent 0 disables the percentage term → the GB floor is the requirement.
        Assert.Equal(50 * Gb, SnapshotDiskGuard.RequiredFreeBytes(1024L * Gb, 0, 50));
    }

    [Fact]
    public void Required_GbZero_UsesPercentageAlone()
    {
        // GB 0 disables the absolute floor → the percentage is the requirement.
        Assert.Equal(20 * Gb, SnapshotDiskGuard.RequiredFreeBytes(200L * Gb, 10, 0));
    }

    [Fact]
    public void Required_BothZero_DisablesTheGuard()
    {
        Assert.Equal(0, SnapshotDiskGuard.RequiredFreeBytes(1024L * Gb, 0, 0));
    }

    [Fact]
    public void Required_UnknownTotal_IgnoresPercentUsesGbFloor()
    {
        // total 0 (a test double / non-Windows platform) can't compute a percentage,
        // so the GB floor stands alone.
        Assert.Equal(50 * Gb, SnapshotDiskGuard.RequiredFreeBytes(0, 10, 50));
    }

    [Theory]
    [InlineData(60, true)]    // 60 GB free ≥ 50 GB required
    [InlineData(50, true)]    // exactly the threshold passes (>=)
    [InlineData(49, false)]   // just under the threshold blocks
    public void HasEnoughFree_BoundaryOnA1TbDrive(int freeGb, bool expected)
    {
        long total = 1024L * Gb;
        Assert.Equal(expected, SnapshotDiskGuard.HasEnoughFree(freeGb * Gb, total, 10, 50));
    }

    [Fact]
    public void HasEnoughFree_MaxValueFreeSentinelNeverBlocks()
    {
        // long.MaxValue is the "unknown free space" sentinel from the interface default;
        // it must always pass, even with a GB floor set and an unknown total.
        Assert.True(SnapshotDiskGuard.HasEnoughFree(long.MaxValue, 0, 10, 50));
        Assert.True(SnapshotDiskGuard.HasEnoughFree(long.MaxValue, 1024L * Gb, 10, 50));
    }

    [Fact]
    public void HasEnoughFree_GenuinelyLowFreeBlocks()
    {
        // A real drive with 10 GB free on a 1 TB disk (needs 50 GB) blocks.
        Assert.False(SnapshotDiskGuard.HasEnoughFree(10 * Gb, 1024L * Gb, 10, 50));
    }
}
