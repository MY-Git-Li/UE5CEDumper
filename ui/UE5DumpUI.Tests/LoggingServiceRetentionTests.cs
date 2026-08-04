using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Log retention rules that had no test at all before audit #4 — which is how a
/// count-based sweep came to rank folders by the one signal its own sibling
/// documents as unusable (B37), and how an 8 MB "cap" turned out to be a kill
/// switch rather than a rotation point (B31).
/// </summary>
public class LoggingServiceRetentionTests : IDisposable
{
    private readonly string _dir;

    public LoggingServiceRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ue5cd-logret-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ----- B37: which folders the count cap evicts -----------------------------

    private static (string, DateTime) F(string name, int minutesAgo) =>
        (name, DateTime.UtcNow.AddMinutes(-minutesAgo));

    [Fact]
    public void Keeps_the_newest_and_evicts_the_rest()
    {
        // Newest-first: b(10m), c(20m), a(30m), d(40m). keep 2 ⇒ b and c survive.
        var evicted = LoggingService.SelectFoldersToEvict(
            new[] { F("a", 30), F("b", 10), F("c", 20), F("d", 40) }, "UE5DumpUI", keep: 2);

        Assert.Equal(new[] { "a", "d" }, evicted.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void The_UI_folder_is_never_evicted_even_when_it_is_the_stalest()
    {
        // The UI writes its own folder continuously, but it is also the folder a user
        // is asked to send when something goes wrong. Losing it to a cap is pure loss.
        var evicted = LoggingService.SelectFoldersToEvict(
            new[] { F("UE5DumpUI", 9999), F("GameA", 1), F("GameB", 2), F("GameC", 3) },
            "UE5DumpUI", keep: 2);

        Assert.DoesNotContain("UE5DumpUI", evicted);
    }

    [Fact]
    public void The_UI_folder_does_not_consume_a_kept_slot()
    {
        // Excluded BEFORE the cap, not after: otherwise "keep 2" would really mean
        // "keep the UI folder and one game", quietly halving the retained history.
        var evicted = LoggingService.SelectFoldersToEvict(
            new[] { F("UE5DumpUI", 1), F("GameA", 2), F("GameB", 3), F("GameC", 4) },
            "UE5DumpUI", keep: 2);

        Assert.Equal(new[] { "GameC" }, evicted);   // A and B kept; UI exempt entirely
    }

    [Fact]
    public void Nothing_is_evicted_while_under_the_cap()
        => Assert.Empty(LoggingService.SelectFoldersToEvict(
               new[] { F("a", 1), F("b", 2) }, "UE5DumpUI", keep: 20));

    [Fact]
    public void An_actively_written_folder_outranks_stale_ones()
    {
        // The B37 failure in one assertion. The caller now passes the newest write time
        // of the files INSIDE each folder; a live game's folder is the newest by that
        // measure even though Windows may not have touched the DIRECTORY's own mtime
        // since the files were created.
        var evicted = LoggingService.SelectFoldersToEvict(
            new[] { F("LiveGame", 0), F("Old1", 500), F("Old2", 600), F("Old3", 700) },
            "UE5DumpUI", keep: 1);

        Assert.DoesNotContain("LiveGame", evicted);
        Assert.Equal(3, evicted.Count);
    }

    // ----- B31: the size cap must ROLL, not stop writing -----------------------

    [Fact]
    public void Crossing_the_size_cap_rolls_instead_of_silently_dropping_events()
    {
        // The property that matters, tested the only honest way: write past the cap and
        // then check the later events are actually ON DISK. Serilog's default is
        // rollOnFileSizeLimit:false, under which the sink stops emitting for the rest of
        // the process — no exception, no SelfLog, and the log simply ends mid-session
        // while docs/architecture.md promises it archives.
        //
        // 4 KB per message keeps this to a couple of thousand writes rather than ~84k.
        var payload = new string('x', 4096);
        const string marker = "MARKER-AFTER-THE-CAP";

        using (var log = new LoggingService(_dir))
        {
            long budget = UE5DumpUI.Constants.LogMaxSizeBytes + (2L * 1024 * 1024);
            for (long written = 0; written < budget; written += payload.Length)
                log.Info(UE5DumpUI.Constants.LogCatInit, payload);

            log.Info(UE5DumpUI.Constants.LogCatInit, marker);
        }

        var initLogs = Directory.GetFiles(_dir, "init-*.log", SearchOption.AllDirectories);
        Assert.NotEmpty(initLogs);

        // More than one file => it rolled rather than freezing at the limit.
        Assert.True(initLogs.Length > 1,
            $"expected the sink to roll past {UE5DumpUI.Constants.LogMaxSizeBytes} bytes, " +
            $"but only {initLogs.Length} file(s) exist — rollOnFileSizeLimit is off again");

        // And the decisive one: the event written AFTER the cap survived.
        Assert.Contains(initLogs, f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal));
    }
}
