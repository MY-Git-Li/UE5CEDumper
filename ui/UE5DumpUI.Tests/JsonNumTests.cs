using System.Text.Json.Nodes;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the "telemetry reads degrade, never throw" rule (<see cref="JsonNum"/>).
///
/// The bug that motivated it: the DLL's "game-thread hook never fired" sentinel is
/// <c>UINT64_MAX</c>, and <c>GetValue&lt;long&gt;()</c> threw on it — blanking the
/// whole Diagnostics card with the message <i>"An element of type 'Number' cannot be
/// converted to a 'System.Int64'"</i>, which System.Text.Json also emits for
/// FRACTIONAL values. That shared message sends you hunting for a decimal point that
/// isn't there, so both causes are pinned here explicitly.
/// </summary>
public class JsonNumTests
{
    private static JsonNode? N(string json) => JsonNode.Parse(json);

    // ── The exact failure from the field ──

    [Fact]
    public void L_survives_the_uint64_max_sentinel_that_broke_diagnostics()
    {
        // 18446744073709551615 == UINT64_MAX, straight out of the real pipe log.
        var n = N("18446744073709551615");
        Assert.Equal(long.MaxValue, JsonNum.L(n));   // saturates instead of throwing
    }

    [Fact]
    public void L_survives_a_fractional_number_the_other_cause_of_that_message()
    {
        Assert.Equal(2700, JsonNum.L(N("2700.9")));   // truncates instead of throwing
    }

    [Fact]
    public void Real_diagnostics_payload_parses_without_throwing()
    {
        // Trimmed verbatim from the pipe log of the failing run.
        var g = N("""
            { "hook_active": false, "hook_fire_count": 0,
              "invoke_timeout_ms": 5000,
              "ms_since_last_fire": 18446744073709551615,
              "responsive": true }
            """)!;
        Assert.False(JsonNum.B(g["hook_active"]));
        Assert.Equal(0, JsonNum.L(g["hook_fire_count"]));
        Assert.Equal(5000, JsonNum.I(g["invoke_timeout_ms"]));
        Assert.Equal(long.MaxValue, JsonNum.L(g["ms_since_last_fire"], -1L));
        Assert.True(JsonNum.B(g["responsive"]));
    }

    // ── Ordinary values still read exactly ──

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-1", -1L)]
    [InlineData("1153", 1153L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void L_reads_in_range_integers_exactly(string json, long expected)
        => Assert.Equal(expected, JsonNum.L(N(json)));

    [Theory]
    [InlineData("8.86923076923077", 8.86923076923077)]
    [InlineData("0.0", 0.0)]
    [InlineData("-1.0", -1.0)]
    [InlineData("125", 125.0)]      // integer JSON read as double
    public void D_reads_numbers_exactly(string json, double expected)
        => Assert.Equal(expected, JsonNum.D(N(json)));

    // ── Degradation, not exceptions ──

    [Fact]
    public void Missing_and_wrong_typed_nodes_return_the_fallback()
    {
        Assert.Equal(0L, JsonNum.L(null));
        Assert.Equal(-1L, JsonNum.L(null, -1L));
        Assert.Equal(7L, JsonNum.L(N("\"not a number\""), 7L));
        Assert.Equal(-1.0, JsonNum.D(null, -1.0));
        Assert.Equal(3, JsonNum.I(N("true"), 3));
        Assert.True(JsonNum.B(N("\"nope\""), true));
    }

    [Fact]
    public void I_saturates_rather_than_overflowing()
    {
        Assert.Equal(int.MaxValue, JsonNum.I(N("99999999999")));
        Assert.Equal(int.MinValue, JsonNum.I(N("-99999999999")));
        Assert.Equal(3667, JsonNum.I(N("3667")));
    }

    [Fact]
    public void D_never_returns_a_non_finite_value()
    {
        // A NaN reaching a format string is how a panel prints "NaN%" at a user.
        Assert.Equal(-1.0, JsonNum.D(N("\"NaN\""), -1.0));
        Assert.True(double.IsFinite(JsonNum.D(N("1e308"))));
    }

    [Fact]
    public void B_reads_booleans_and_defaults_otherwise()
    {
        Assert.True(JsonNum.B(N("true")));
        Assert.False(JsonNum.B(N("false")));
        Assert.False(JsonNum.B(N("1")));        // not a bool → fallback
    }
}
