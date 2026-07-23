using System;
using System.Collections.Generic;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the shape of the automatic PERF log line written around every heavy
/// operation (Copy CE XML / Copy CE Field / Value Scan / Snapshot capture).
///
/// The point of recording automatically rather than in a measurement session is
/// that the evidence accumulates from real use. That only works if the line is
/// trustworthy, so the arithmetic is pinned here: deltas not absolutes, the
/// probe's own calls excluded, and no negative figures when the DLL's counters
/// are reset mid-operation.
/// </summary>
public class DiagnosticsProbeTests
{
    private static DiagnosticsResult R(long dispatches, long busyMs,
                                       params (string Cmd, long Count, long TotalMs, long MaxMs)[] cmds)
        => new()
        {
            TotalDispatches = dispatches,
            TotalBusyMs = busyMs,
            Commands = new List<DiagnosticsCommandEntry>(Array.ConvertAll(cmds,
                c => new DiagnosticsCommandEntry
                {
                    Cmd = c.Cmd, Count = c.Count, TotalMs = c.TotalMs, MaxMs = c.MaxMs,
                })),
        };

    // ── Deltas, not absolutes ────────────────────────────────────────

    [Fact]
    public void Deltas_subtract_the_opening_snapshot()
    {
        var before = R(100, 1000, ("value_scan_begin", 2, 800, 500));
        var after  = R(105, 5000, ("value_scan_begin", 3, 4800, 4000));

        var top = DiagnosticsProbe.TopDeltas(before, after, 3);
        Assert.Single(top);
        Assert.Equal("value_scan_begin", top[0].Cmd);
        Assert.Equal(1, top[0].Count);          // 3 - 2
        Assert.Equal(4000, top[0].TotalMs);     // 4800 - 800
    }

    [Fact]
    public void A_command_first_seen_during_the_operation_counts_in_full()
    {
        var before = R(10, 100);
        var after  = R(12, 900, ("search_properties", 2, 800, 600));

        var top = DiagnosticsProbe.TopDeltas(before, after, 3);
        Assert.Single(top);
        Assert.Equal(2, top[0].Count);
        Assert.Equal(800, top[0].TotalMs);
    }

    [Fact]
    public void Untouched_commands_are_omitted()
    {
        // Only what THIS operation did is interesting.
        var before = R(10, 100, ("init", 1, 5, 5), ("get_pointers", 1, 0, 0));
        var after  = R(11, 900, ("init", 1, 5, 5), ("get_pointers", 1, 0, 0),
                                ("value_scan_begin", 1, 800, 800));

        var top = DiagnosticsProbe.TopDeltas(before, after, 5);
        Assert.Single(top);
        Assert.Equal("value_scan_begin", top[0].Cmd);
    }

    [Fact]
    public void The_probes_own_calls_are_excluded()
    {
        // The opening snapshot is itself a dispatch that lands in the closing one.
        // Reporting it would make every measurement list the measurement.
        var before = R(10, 100);
        var after  = R(12, 400, ("get_diagnostics", 1, 125, 125),
                                ("value_scan_begin", 1, 175, 175));

        var top = DiagnosticsProbe.TopDeltas(before, after, 5);
        Assert.Single(top);
        Assert.Equal("value_scan_begin", top[0].Cmd);
    }

    [Fact]
    public void Deltas_are_ranked_by_total_time()
    {
        var before = R(0, 0);
        var after  = R(6, 600, ("a", 1, 100, 100), ("b", 1, 300, 300), ("c", 1, 200, 200));

        var top = DiagnosticsProbe.TopDeltas(before, after, 3);
        Assert.Equal(new[] { "b", "c", "a" }, Array.ConvertAll(top.ToArray(), t => t.Cmd));
    }

    [Fact]
    public void Limit_truncates_the_ranking()
    {
        var before = R(0, 0);
        var after  = R(6, 600, ("a", 1, 100, 100), ("b", 1, 300, 300), ("c", 1, 200, 200));
        Assert.Equal(2, DiagnosticsProbe.TopDeltas(before, after, 2).Count);
        Assert.Equal(3, DiagnosticsProbe.TopDeltas(before, after, 0).Count);   // 0 = no limit
    }

    // ── Resilience: a mid-operation reset must not produce nonsense ──

    [Fact]
    public void A_counter_reset_mid_operation_never_yields_negatives()
    {
        // reset_diagnostics between the two samples makes 'after' SMALLER.
        var before = R(500, 9000, ("value_scan_begin", 9, 8000, 4000));
        var after  = R(2, 30, ("value_scan_begin", 1, 20, 20));

        var line = DiagnosticsProbe.Format("Value Scan", TimeSpan.FromSeconds(2), before, after);
        Assert.DoesNotContain("-", line.Replace("·", "").Replace("—", ""), StringComparison.Ordinal);

        var top = DiagnosticsProbe.TopDeltas(before, after, 3);
        Assert.All(top, t => Assert.True(t.TotalMs >= 0 && t.Count >= 0));
    }

    // ── The log line ────────────────────────────────────────────────

    [Fact]
    public void Format_leads_with_the_label_and_the_busy_share()
    {
        var before = R(0, 0);
        var after  = R(3, 500, ("value_scan_begin", 1, 500, 500));

        var line = DiagnosticsProbe.Format("Value Scan (First)", TimeSpan.FromSeconds(2), before, after);
        Assert.StartsWith("PERF Value Scan (First):", line, StringComparison.Ordinal);
        Assert.Contains("wall 2,000.0 ms", line, StringComparison.Ordinal);
        Assert.Contains("dispatcher busy 500 ms (25.0%)", line, StringComparison.Ordinal);
        Assert.Contains("3 dispatches", line, StringComparison.Ordinal);
        Assert.Contains("value_scan_begin 500ms/1x", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_survives_a_zero_length_operation()
    {
        // Guards the wall-clock divisor.
        var line = DiagnosticsProbe.Format("X", TimeSpan.Zero, R(0, 0), R(0, 0));
        Assert.Contains("(0.0%)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_reports_gobjects_growth_only_when_it_moved()
    {
        var before = new DiagnosticsResult { GObjectsCount = 1000 };
        var same   = new DiagnosticsResult { GObjectsCount = 1000 };
        var grown  = new DiagnosticsResult { GObjectsCount = 1500 };

        Assert.DoesNotContain("GObjects", DiagnosticsProbe.Format("X", TimeSpan.FromSeconds(1), before, same),
                              StringComparison.Ordinal);
        Assert.Contains("GObjects +500", DiagnosticsProbe.Format("X", TimeSpan.FromSeconds(1), before, grown),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Format_reports_game_memory_only_on_a_meaningful_move()
    {
        // Sub-MiB jitter is noise, and the figure is the GAME's memory, not ours.
        var before = new DiagnosticsResult { Process = new DiagnosticsProcess { WorkingSetBytes = 1_000_000_000 } };
        var jitter = new DiagnosticsResult { Process = new DiagnosticsProcess { WorkingSetBytes = 1_000_100_000 } };
        var real   = new DiagnosticsResult { Process = new DiagnosticsProcess { WorkingSetBytes = 1_500_000_000 } };

        Assert.DoesNotContain("game WS", DiagnosticsProbe.Format("X", TimeSpan.FromSeconds(1), before, jitter),
                              StringComparison.Ordinal);
        Assert.Contains("game WS +", DiagnosticsProbe.Format("X", TimeSpan.FromSeconds(1), before, real),
                        StringComparison.Ordinal);
    }
}
