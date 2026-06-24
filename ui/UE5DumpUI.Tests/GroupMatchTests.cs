using System.Globalization;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the C# port of the DLL's Orden SDR matcher (Snapshot Group Match, S1).
/// Mirrors the DLL Orden cases (distinct assignment, duplicate values, no-SDR,
/// width-fit, scope) and adds the snapshot-specific Mode B temporal cases — most
/// importantly the motivating "Current HP decreased + Max HP unchanged" group.
/// </summary>
public class GroupMatchTests
{
    // ---- helpers ----
    private static string Hx(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // Single-snapshot leaf (Mode A).
    private static GroupMatch.Leaf L(int offset, string type, double v, int tag = 0) => new()
    {
        Offset = offset,
        DeclaredType = type,
        Hex = new[] { Hx(v) },
        Num = new double?[] { v },
        Tag = tag,
    };

    // Two-snapshot leaf (Mode B): value a (oldest) -> b (newest).
    private static GroupMatch.Leaf L2(int offset, string type, double a, double b, int tag = 0) => new()
    {
        Offset = offset,
        DeclaredType = type,
        Hex = new[] { Hx(a), Hx(b) },
        Num = new double?[] { a, b },
        Tag = tag,
    };

    private static GroupMatch.Slot Abs(GroupMatch.Predicate p, double target, double tol = 0,
        GroupMatch.Scope sc = GroupMatch.Scope.NumericNoByte) =>
        new() { Predicate = p, Target = target, Tolerance = tol, Scope = sc };

    private static GroupMatch.Slot Between(double lo, double hi) =>
        new() { Predicate = GroupMatch.Predicate.Between, Target = lo, Target2 = hi };

    private static GroupMatch.Slot Rel(GroupMatch.Predicate p,
        GroupMatch.Scope sc = GroupMatch.Scope.NumericNoByte) =>
        new() { Predicate = p, Scope = sc };

    // ============================================================
    // Mode A — single-snapshot absolute (the degenerate path)
    // ============================================================

    [Fact]
    public void TwoSlots_DistinctOffsets_Match()
    {
        var leaves = new[] { L(0x20, "IntProperty", 24), L(0x24, "IntProperty", 10) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 24), Abs(GroupMatch.Predicate.Exact, 10) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void TwoFloatLeaves_ExactWholeTarget_RoundMatches()
    {
        // The reported GAS case: AttributeSetCore.Health BaseValue(0x8) + CurrentValue(0xC)
        // both store 513.3599853516; searching Exact 513/513 must match via rounding.
        var leaves = new[]
        {
            L(0x8, "FloatProperty", 513.3599853516),
            L(0xC, "FloatProperty", 513.3599853516),
        };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 513), Abs(GroupMatch.Predicate.Exact, 513) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));

        // A float that rounds to 514 is NOT matched by an Exact-513 slot.
        var miss = new[] { L(0x8, "FloatProperty", 513.7), L(0xC, "FloatProperty", 513.7) };
        Assert.False(GroupMatch.Run(miss, slots, out _));
    }

    [Fact]
    public void DuplicateValue_NeedsTwoDistinctLeaves()
    {
        // Two slots both want 24; two distinct leaves hold 24 -> SDR exists.
        var leaves = new[] { L(0x20, "IntProperty", 24), L(0x24, "IntProperty", 24) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 24), Abs(GroupMatch.Predicate.Exact, 24) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void DuplicateValue_OnlyOneLeaf_NoSdr()
    {
        // Two slots want 24 but only ONE leaf holds it -> no distinct assignment.
        var leaves = new[] { L(0x20, "IntProperty", 24), L(0x24, "IntProperty", 99) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 24), Abs(GroupMatch.Predicate.Exact, 24) };
        Assert.False(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void OrderIndependent_Match()
    {
        // Values appear in the opposite offset order vs the slots.
        var leaves = new[] { L(0x20, "IntProperty", 10), L(0x24, "IntProperty", 24) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 24), Abs(GroupMatch.Predicate.Exact, 10) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void WidthFit_ValueTooBigForInt16_GatedToWiderLeaf()
    {
        // 70000 cannot fit an Int16; it must bind the Int32 leaf, and 24 binds Int16.
        var leaves = new[] { L(0x0, "Int16Property", 24), L(0x4, "IntProperty", 70000) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 70000), Abs(GroupMatch.Predicate.Exact, 24) };
        Assert.True(GroupMatch.Run(leaves, slots, out var per));
        // The 70000 slot matched only the Int32 leaf (index 1).
        Assert.Equal(new[] { 1 }, per[0]);
    }

    [Fact]
    public void WidthFit_NonIntegralTargetRejectsIntLeaf()
    {
        // 3.5 cannot be an integer field; only the Float leaf is eligible.
        var leaves = new[] { L(0x0, "IntProperty", 3), L(0x4, "FloatProperty", 3.5) };
        var slots = new[] { Abs(GroupMatch.Predicate.Exact, 3.5), Abs(GroupMatch.Predicate.Exact, 3) };
        Assert.True(GroupMatch.Run(leaves, slots, out var per));
        Assert.Equal(new[] { 1 }, per[0]); // 3.5 -> Float leaf only
    }

    [Fact]
    public void Scope_OneByteExcludedUnderNoByte_ButIncludedUnderAll()
    {
        // Two slots both want 5; a Byte leaf and an Int32 leaf hold it.
        var leaves = new[] { L(0x0, "ByteProperty", 5), L(0x4, "IntProperty", 5) };

        // NoByte: the Byte leaf is ineligible -> only one eligible leaf -> no SDR.
        var noByte = new[]
        {
            Abs(GroupMatch.Predicate.Exact, 5, sc: GroupMatch.Scope.NumericNoByte),
            Abs(GroupMatch.Predicate.Exact, 5, sc: GroupMatch.Scope.NumericNoByte),
        };
        Assert.False(GroupMatch.Run(leaves, noByte, out _));

        // All: both leaves eligible -> SDR exists.
        var all = new[]
        {
            Abs(GroupMatch.Predicate.Exact, 5, sc: GroupMatch.Scope.NumericAll),
            Abs(GroupMatch.Predicate.Exact, 5, sc: GroupMatch.Scope.NumericAll),
        };
        Assert.True(GroupMatch.Run(leaves, all, out _));
    }

    [Fact]
    public void Between_Inclusive()
    {
        var leaves = new[] { L(0x0, "IntProperty", 50), L(0x4, "IntProperty", 200) };
        Assert.True(GroupMatch.Run(new[] { leaves[0], leaves[1] },
            new[] { Between(1, 100), Between(100, 300) }, out _));
        // 50 is NOT in [60,100] -> first slot empty -> no match.
        Assert.False(GroupMatch.Run(leaves, new[] { Between(60, 100), Between(100, 300) }, out _));
    }

    [Fact]
    public void Exact_FloatTolerance()
    {
        // Non-whole target (1.5) so the rounding path doesn't apply — this isolates the
        // ± tolerance band. (A whole target would now also round-match; see ExactMatch.)
        var leaf = new[] { L(0x0, "FloatProperty", 1.5001), L(0x4, "IntProperty", 7) };
        Assert.True(GroupMatch.Run(leaf,
            new[] { Abs(GroupMatch.Predicate.Exact, 1.5, 0.001), Abs(GroupMatch.Predicate.Exact, 7) }, out _));
        Assert.False(GroupMatch.Run(leaf,
            new[] { Abs(GroupMatch.Predicate.Exact, 1.5, 0.0), Abs(GroupMatch.Predicate.Exact, 7) }, out _));
    }

    [Fact]
    public void LessThanTwoSlots_ReturnsFalse()
    {
        var leaves = new[] { L(0x0, "IntProperty", 24) };
        Assert.False(GroupMatch.Run(leaves, new[] { Abs(GroupMatch.Predicate.Exact, 24) }, out _));
    }

    // ============================================================
    // Mode B — cross-snapshot temporal (the motivating feature)
    // ============================================================

    [Fact]
    public void ModeB_Decreased_Unchanged_HpPair_Match()
    {
        // The motivating case: Current HP took damage (100->80), Max HP unchanged (100->100).
        var leaves = new[]
        {
            L2(0x10, "IntProperty", 100, 80),   // Current HP -> Decreased
            L2(0x14, "IntProperty", 100, 100),  // Max HP     -> Unchanged
        };
        var slots = new[] { Rel(GroupMatch.Predicate.Decreased), Rel(GroupMatch.Predicate.Unchanged) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void ModeB_Unchanged_RejectsObjectWhereMaxAlsoChanged()
    {
        // Level-up: BOTH HP fields changed -> the Unchanged slot has no match -> not a group.
        var leaves = new[]
        {
            L2(0x10, "IntProperty", 100, 120),  // Current HP -> Decreased? no, increased
            L2(0x14, "IntProperty", 100, 120),  // Max HP     -> also changed
        };
        var slots = new[] { Rel(GroupMatch.Predicate.Decreased), Rel(GroupMatch.Predicate.Unchanged) };
        Assert.False(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void ModeB_Increased()
    {
        var leaves = new[] { L2(0x0, "IntProperty", 80, 100), L2(0x4, "IntProperty", 5, 5) };
        var slots = new[] { Rel(GroupMatch.Predicate.Increased), Rel(GroupMatch.Predicate.Unchanged) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void ModeB_Changed_DetectsAnyMovement()
    {
        var leaves = new[] { L2(0x0, "FloatProperty", 1.0, 2.0), L2(0x4, "IntProperty", 7, 7) };
        var slots = new[] { Rel(GroupMatch.Predicate.Changed), Rel(GroupMatch.Predicate.Unchanged) };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void Relative_RequiresTwoSnapshots()
    {
        // A single-snapshot leaf has no baseline -> a relative predicate never matches.
        var leaf = L(0x0, "IntProperty", 80); // one snapshot only
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf, Rel(GroupMatch.Predicate.Decreased)));
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf, Rel(GroupMatch.Predicate.Unchanged)));
    }

    [Fact]
    public void ModeB_MixedAbsoluteAndRelativeSlots()
    {
        // Open decision #1 (lean: yes) — absolute and relative slots in one query.
        // Current HP decreased, Max HP unchanged, Level exactly 30 (in the newest snapshot).
        var leaves = new[]
        {
            L2(0x10, "IntProperty", 100, 80),   // Current HP -> Decreased
            L2(0x14, "IntProperty", 100, 100),  // Max HP     -> Unchanged
            L2(0x18, "IntProperty", 30, 30),    // Level      -> Exact 30 (newest)
        };
        var slots = new[]
        {
            Rel(GroupMatch.Predicate.Decreased),
            Rel(GroupMatch.Predicate.Unchanged),
            Abs(GroupMatch.Predicate.Exact, 30),
        };
        Assert.True(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void Absolute_UsesNewestSnapshotValue()
    {
        // Exact 80 must match on the NEWEST value (100 -> 80), not the oldest (100).
        var leaves = new[] { L2(0x0, "IntProperty", 100, 80), L2(0x4, "IntProperty", 5, 5) };
        Assert.True(GroupMatch.Run(leaves,
            new[] { Abs(GroupMatch.Predicate.Exact, 80), Abs(GroupMatch.Predicate.Exact, 5) }, out _));
        Assert.False(GroupMatch.Run(leaves,
            new[] { Abs(GroupMatch.Predicate.Exact, 100), Abs(GroupMatch.Predicate.Exact, 5) }, out _));
    }
}
