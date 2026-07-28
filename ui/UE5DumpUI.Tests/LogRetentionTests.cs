using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Age-based log retention (replaces the old fixed generation shuffle).
///
/// The generation scheme could not express "keep 15 days" at all: rotation ran on
/// every startup, so a handful of restarts evicted everything before them no matter
/// how recent. These cover the replacement and, specifically, the two collisions it
/// created with code that was already there.
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir;

    public LogRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ue5dump-logret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Touch(string name, DateTime writeTime)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        File.SetLastWriteTime(p, writeTime);
        return p;
    }

    private string[] Names() =>
        Directory.GetFiles(_dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToArray();

    // ── archive ──────────────────────────────────────────────────────────

    [Fact]
    public void Archive_StampsFromTheFilesOwnMtime_NotNow()
    {
        // This is the property the whole policy rests on. If archiving stamped
        // "now", a log written a month ago would get today's date and survive
        // another full retention window — retention would silently never expire.
        var written = new DateTime(2026, 3, 4, 5, 6, 7);
        Touch("init-0.log", written);

        LoggingService.ArchivePreviousLog(_dir, "init");

        Assert.Equal(new[] { "init-20260304-050607.log" }, Names());
    }

    [Fact]
    public void Archive_SameSecondCollision_DisambiguatesInsteadOfOverwriting()
    {
        var t = new DateTime(2026, 3, 4, 5, 6, 7);
        Touch("init-0.log", t);
        LoggingService.ArchivePreviousLog(_dir, "init");
        Touch("init-0.log", t);
        LoggingService.ArchivePreviousLog(_dir, "init");

        Assert.Equal(
            new[] { "init-20260304-050607-1.log", "init-20260304-050607.log" },
            Names());
    }

    [Fact]
    public void Archive_MigratesLegacyNumberedGenerations()
    {
        // Left by builds before age-based retention. Nothing rotates them any more,
        // so without migration they orphan in the folder forever.
        Touch("init-0.log", new DateTime(2026, 3, 4, 5, 6, 7));
        Touch("init-1.log", new DateTime(2026, 3, 3, 1, 2, 3));
        Touch("init-2.log", new DateTime(2026, 3, 2, 1, 2, 3));

        LoggingService.ArchivePreviousLog(_dir, "init");

        Assert.Equal(
            new[]
            {
                "init-20260302-010203.log",
                "init-20260303-010203.log",
                "init-20260304-050607.log",
            },
            Names());
    }

    [Fact]
    public void Archive_DoesNotTouchOtherCategories()
    {
        Touch("init-0.log", new DateTime(2026, 3, 4, 5, 6, 7));
        Touch("pipe-0.log", new DateTime(2026, 3, 4, 5, 6, 7));

        LoggingService.ArchivePreviousLog(_dir, "init");

        Assert.Contains("pipe-0.log", Names());
    }

    // ── prune ────────────────────────────────────────────────────────────

    [Fact]
    public void Prune_DeletesOnlyArchivesPastTheWindow()
    {
        Touch("init-20260101-000000.log", DateTime.Now.AddDays(-20));
        Touch("init-20260110-000000.log", DateTime.Now.AddDays(-14));

        LoggingService.PruneAgedLogs(_dir, "init", 15);

        Assert.Equal(new[] { "init-20260110-000000.log" }, Names());
    }

    [Fact]
    public void Prune_NeverDeletesTheLiveFile()
    {
        // -0.log is the session currently being written. Even with an ancient mtime
        // (clock skew, a restored backup) it must survive — deleting it would pull
        // the file out from under an open handle.
        Touch("init-0.log", DateTime.Now.AddDays(-400));

        LoggingService.PruneAgedLogs(_dir, "init", 15);

        Assert.Equal(new[] { "init-0.log" }, Names());
    }

    [Fact]
    public void Prune_IsScopedToItsOwnCategory()
    {
        Touch("init-20260101-000000.log", DateTime.Now.AddDays(-20));
        Touch("pipe-20260101-000000.log", DateTime.Now.AddDays(-20));

        LoggingService.PruneAgedLogs(_dir, "init", 15);

        Assert.Equal(new[] { "pipe-20260101-000000.log" }, Names());
    }

    // ── the archive-suffix guard ─────────────────────────────────────────
    //
    // CleanupOldDailyLogs deletes any "{prefix}-*.log" whose suffix is not a bare
    // integer, to clear the pre-category daily format. Without an explicit guard it
    // would delete every archive written above, on the very next startup.

    [Theory]
    [InlineData("20260304-050607", true)]    // an archive
    [InlineData("20260304-050607-1", true)]  // same-second collision
    [InlineData("20260304", false)]          // the legacy daily format — must still go
    [InlineData("0", false)]                 // live file, handled by the integer branch
    [InlineData("2026030-050607", false)]    // 7-digit date
    [InlineData("20260304-05060", false)]    // 5-digit time
    [InlineData("20260304-05060x", false)]
    [InlineData("abcdefgh-ijklmn", false)]
    [InlineData("20260304-050607-1-2", false)]
    [InlineData("", false)]
    public void IsArchiveSuffix_SeparatesArchivesFromTheLegacyDailyFormat(string suffix, bool expected)
        => Assert.Equal(expected, LoggingService.IsArchiveSuffix(suffix));

    [Fact]
    public void CleanupOldDailyLogs_KeepsArchives_RemovesLegacyDailyFiles()
    {
        // The end-to-end version of the guard above — this is the regression that
        // would silently empty the archive folder.
        Touch("UE5DumpUI-20260304-050607.log", DateTime.Now);  // archive: keep
        Touch("UE5DumpUI-0.log", DateTime.Now);                // live:    keep
        Touch("UE5DumpUI-20260304.log", DateTime.Now);         // legacy:  delete

        LoggingService.CleanupOldDailyLogs(_dir, "UE5DumpUI");

        Assert.Equal(
            new[] { "UE5DumpUI-0.log", "UE5DumpUI-20260304-050607.log" },
            Names());
    }

    // ── round trip ───────────────────────────────────────────────────────

    [Fact]
    public void ArchiveThenPrune_KeepsAWindowRegardlessOfLaunchCount()
    {
        // The actual point of the change. Simulate 30 launches, one per day, with
        // the two steps a real startup performs. The old 2-generation scheme would
        // leave 2 files; this must leave a 15-day window.
        for (int daysAgo = 29; daysAgo >= 0; daysAgo--)
        {
            Touch("init-0.log", DateTime.Now.AddDays(-daysAgo));
            LoggingService.ArchivePreviousLog(_dir, "init");
            LoggingService.PruneAgedLogs(_dir, "init", 15);
        }

        var kept = Names();
        Assert.All(kept, n => Assert.StartsWith("init-", n));
        // 15-day window, inclusive of both ends depending on sub-second timing.
        Assert.InRange(kept.Length, 15, 16);
    }
}
