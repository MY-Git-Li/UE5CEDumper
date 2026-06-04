using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Pure SPC predicate evaluation: directional chain (Unchanged/Changed on hex,
/// Increased/Decreased on numeric) + per-snapshot absolute value predicates.
/// </summary>
public class SpcEngineTests
{
    private static SpcAbsolutePredicate Abs(SpcAbsoluteKind k, double lo = 0, double hi = 0)
        => new() { Kind = k, Low = lo, High = hi };

    [Fact]
    public void DirectionalChain_DecreasedThenIncreased()
    {
        var hex = new[] { "a", "b", "c" };
        var num = new double?[] { 100, 30, 80 };
        var dir = new[] { SpcPredicateKind.Any, SpcPredicateKind.Decreased, SpcPredicateKind.Increased };

        Assert.True(SpcEngine.Matches(hex, num, dir, null));

        // 80 is NOT a decrease from 30 → fails a Decreased at step 2.
        var dir2 = new[] { SpcPredicateKind.Any, SpcPredicateKind.Decreased, SpcPredicateKind.Decreased };
        Assert.False(SpcEngine.Matches(hex, num, dir2, null));
    }

    [Fact]
    public void Unchanged_Changed_CompareHexExactly()
    {
        var num = new double?[] { 1, 1 };
        Assert.True(SpcEngine.Matches(new[] { "AA", "AA" }, num,
            new[] { SpcPredicateKind.Any, SpcPredicateKind.Unchanged }, null));
        Assert.False(SpcEngine.Matches(new[] { "AA", "BB" }, num,
            new[] { SpcPredicateKind.Any, SpcPredicateKind.Unchanged }, null));
        Assert.True(SpcEngine.Matches(new[] { "AA", "BB" }, num,
            new[] { SpcPredicateKind.Any, SpcPredicateKind.Changed }, null));
    }

    [Fact]
    public void AbsolutePredicate_Between_FiltersByValueWindow()
    {
        var hex = new[] { "a", "b" };
        var num = new double?[] { 1920, 0 };   // a UI-width style sequence
        var dir = new[] { SpcPredicateKind.Any, SpcPredicateKind.Decreased };

        // Decreased alone matches (1920 → 0).
        Assert.True(SpcEngine.Matches(hex, num, dir, null));

        // But require the NEW value Between 0–500 AND the OLD between 0–500: the 1920
        // baseline fails the window → excluded (this is how UI-noise gets cut).
        var abs = new[] { Abs(SpcAbsoluteKind.Between, 0, 500), Abs(SpcAbsoluteKind.Between, 0, 500) };
        Assert.False(SpcEngine.Matches(hex, num, dir, abs));

        // A real gameplay sequence in-window passes.
        var num2 = new double?[] { 300, 120 };
        Assert.True(SpcEngine.Matches(hex, num2, dir, abs));
    }

    [Fact]
    public void AbsolutePredicate_Exact_And_AtMost()
    {
        var num = new double?[] { 50, 50 };
        var dir = new[] { SpcPredicateKind.Any, SpcPredicateKind.Unchanged };
        Assert.True(SpcEngine.Matches(new[] { "x", "x" }, num, dir,
            new[] { Abs(SpcAbsoluteKind.Exact, 50), Abs(SpcAbsoluteKind.Exact, 50) }));
        Assert.False(SpcEngine.Matches(new[] { "x", "x" }, num, dir,
            new[] { Abs(SpcAbsoluteKind.None), Abs(SpcAbsoluteKind.AtMost, 0, 40) }));   // 50 > 40
    }

    [Fact]
    public void AbsolutePredicate_OnNonNumeric_Fails()
    {
        // A non-None absolute predicate on a field with no numeric value rejects it.
        var num = new double?[] { null, null };
        Assert.False(SpcEngine.Matches(new[] { "a", "a" }, num,
            new[] { SpcPredicateKind.Any, SpcPredicateKind.Unchanged },
            new[] { Abs(SpcAbsoluteKind.AtLeast, 1), Abs(SpcAbsoluteKind.None) }));
    }
}
