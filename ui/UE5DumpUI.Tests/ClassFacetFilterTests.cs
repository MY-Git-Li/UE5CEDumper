using System.Linq;
using UE5DumpUI.Helpers;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks down the client-side class-noise filter (Instance / Interesting Funcs+
/// Props / Property Search): Top-N histogram build, live exclude toggle, the
/// re-filter callback contract, exclusion persistence across rebuilds, and the
/// Clear / Reset paths.
/// </summary>
public class ClassFacetFilterTests
{
    private static ClassFacetFilter Make(out int[] changeCount)
    {
        var box = new int[1];
        changeCount = box;
        return new ClassFacetFilter(() => box[0]++);
    }

    // ── Rebuild: histogram ───────────────────────────────────────────────────

    [Fact]
    public void Rebuild_CountsPerClass_OrdersByCountDescThenName()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "B", "A", "A", "B", "B", "C" });

        Assert.True(f.HasFacets);
        Assert.Equal(3, f.DistinctCount);
        // B=3, A=2, C=1 -> ordered by count desc.
        Assert.Equal(new[] { "B", "A", "C" }, f.Facets.Select(r => r.ClassName));
        Assert.Equal(new[] { 3, 2, 1 }, f.Facets.Select(r => r.HitCount));
    }

    [Fact]
    public void Rebuild_TieBreaksOnClassNameOrdinal()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "Zeta", "Alpha" });   // both count 1
        Assert.Equal(new[] { "Alpha", "Zeta" }, f.Facets.Select(r => r.ClassName));
    }

    [Fact]
    public void Rebuild_SkipsNullAndEmpty()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A", null, "", "A" });
        Assert.Single(f.Facets);
        Assert.Equal("A", f.Facets[0].ClassName);
        Assert.Equal(2, f.Facets[0].HitCount);
        Assert.Equal(1, f.DistinctCount);
    }

    [Fact]
    public void Rebuild_HonoursTopNLimit()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A", "B", "C", "D", "E" }, topN: 2);
        Assert.Equal(2, f.Facets.Count);
        Assert.Equal(5, f.DistinctCount);   // distinct count reflects the FULL set
    }

    [Fact]
    public void Rebuild_EmptySource_NoFacets()
    {
        var f = Make(out _);
        f.Rebuild(System.Array.Empty<string>());
        Assert.False(f.HasFacets);
        Assert.Empty(f.Facets);
        Assert.Equal("", f.Summary);
    }

    [Fact]
    public void Rebuild_CountsPartialFlag_Propagates()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A" }, countsPartial: true);
        Assert.True(f.CountsPartial);
        f.Rebuild(new[] { "A" }, countsPartial: false);
        Assert.False(f.CountsPartial);
    }

    [Fact]
    public void Rebuild_DoesNotFireOnChanged()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A", "B" });
        Assert.Equal(0, changes[0]);
    }

    [Fact]
    public void Display_FormatsClassAndCount()
    {
        var f = Make(out _);
        f.Rebuild(Enumerable.Repeat("MyClass", 1234));
        Assert.Equal("MyClass  (1,234)", f.Facets[0].Display);
    }

    // ── Toggle: live exclude ─────────────────────────────────────────────────

    [Fact]
    public void Toggle_HidesClass_FiresOnChangeOnce()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A", "B" });

        f.Facets.Single(r => r.ClassName == "A").Picked = true;

        Assert.True(f.IsExcluded("A"));
        Assert.False(f.IsExcluded("B"));
        Assert.Equal(1, f.ExcludedCount);
        Assert.True(f.AnyExcluded);
        Assert.Equal(1, changes[0]);
    }

    [Fact]
    public void Toggle_Off_RestoresClass()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A" });
        var row = f.Facets[0];

        row.Picked = true;
        row.Picked = false;

        Assert.False(f.IsExcluded("A"));
        Assert.Equal(0, f.ExcludedCount);
        Assert.False(f.AnyExcluded);
        Assert.Equal(2, changes[0]);   // one per real change
    }

    [Fact]
    public void IsExcluded_NullOrEmpty_False()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A" });
        f.Facets[0].Picked = true;
        Assert.False(f.IsExcluded(null));
        Assert.False(f.IsExcluded(""));
    }

    // ── Exclusion persistence across rebuilds ────────────────────────────────

    [Fact]
    public void Rebuild_PreservesExclusion_ReticksRebuiltRow()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A", "B" });
        f.Facets.Single(r => r.ClassName == "A").Picked = true;
        int afterToggle = changes[0];

        // Re-search: same class reappears.
        f.Rebuild(new[] { "A", "A", "B", "C" });

        Assert.True(f.IsExcluded("A"));
        Assert.True(f.Facets.Single(r => r.ClassName == "A").Picked);   // re-ticked
        Assert.Equal(1, f.ExcludedCount);
        Assert.Equal(afterToggle, changes[0]);   // Rebuild itself fires nothing
    }

    [Fact]
    public void ExcludedClass_NotInTopN_StillFilters()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "Noise" });
        f.Facets[0].Picked = true;

        // Next result: Noise is now rare and falls outside the Top-N window.
        f.Rebuild(new[] { "A", "A", "A", "B", "B", "Noise" }, topN: 2);

        Assert.DoesNotContain(f.Facets, r => r.ClassName == "Noise");
        Assert.True(f.IsExcluded("Noise"));   // still hidden even though not shown
    }

    // ── Clear / Reset ────────────────────────────────────────────────────────

    [Fact]
    public void Clear_UnticksAll_FiresOnceNotPerRow()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A", "B", "C" });
        f.Facets.Single(r => r.ClassName == "A").Picked = true;
        f.Facets.Single(r => r.ClassName == "B").Picked = true;
        int before = changes[0];   // 2 toggles

        f.ClearCommand.Execute(null);

        Assert.Equal(0, f.ExcludedCount);
        Assert.All(f.Facets, r => Assert.False(r.Picked));
        Assert.False(f.IsExcluded("A"));
        Assert.Equal(before + 1, changes[0]);   // exactly one re-filter, not per row
    }

    [Fact]
    public void Clear_WhenNothingExcluded_NoOp()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A" });
        f.ClearCommand.Execute(null);
        Assert.Equal(0, changes[0]);
    }

    [Fact]
    public void Reset_ClearsHistogramAndExclusions_NoOnChanged()
    {
        var f = Make(out var changes);
        f.Rebuild(new[] { "A", "B" }, countsPartial: true);
        f.Facets[0].Picked = true;
        int before = changes[0];

        f.Reset();

        Assert.False(f.HasFacets);
        Assert.Empty(f.Facets);
        Assert.Equal(0, f.ExcludedCount);
        Assert.Equal(0, f.DistinctCount);
        Assert.False(f.CountsPartial);
        Assert.False(f.IsExcluded("A"));
        Assert.Equal(before, changes[0]);   // Reset never re-filters
    }

    // ── CanOpen: the disabled-button-with-active-exclusion trap (regression) ──

    [Fact]
    public void CanOpen_FalseBeforeAnySearch()
    {
        var f = Make(out _);
        Assert.False(f.HasFacets);
        Assert.False(f.AnyExcluded);
        Assert.False(f.CanOpen);
    }

    [Fact]
    public void CanOpen_TrueWhenHasFacets()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A", "B" });
        Assert.True(f.CanOpen);
    }

    [Fact]
    public void CanOpen_StaysTrueWhenExcludedButEmptyResult()
    {
        // The exact bug: hide a class, then a search returns 0 rows (or every
        // match is in a hidden class). The button must stay openable so the user
        // can reach Clear — otherwise the exclusion is stuck active + invisible.
        var f = Make(out _);
        f.Rebuild(new[] { "A", "B" });
        f.Facets.Single(r => r.ClassName == "A").Picked = true;

        f.Rebuild(System.Array.Empty<string>());   // 0-result search

        Assert.False(f.HasFacets);
        Assert.True(f.AnyExcluded);
        Assert.True(f.CanOpen);   // recoverable: flyout opens -> Clear available
    }

    [Fact]
    public void CanOpen_FalseAgainAfterClearOnEmptyResult()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A" });
        f.Facets[0].Picked = true;
        f.Rebuild(System.Array.Empty<string>());
        Assert.True(f.CanOpen);

        f.ClearCommand.Execute(null);   // user recovers via the still-open flyout

        Assert.False(f.AnyExcluded);
        Assert.False(f.CanOpen);
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_ReflectsDistinctAndHiddenCounts()
    {
        var f = Make(out _);
        f.Rebuild(new[] { "A", "B", "C" });
        Assert.Equal("3 distinct class(es)", f.Summary);

        f.Facets.Single(r => r.ClassName == "A").Picked = true;
        Assert.Equal("1 hidden · 3 distinct class(es)", f.Summary);
    }
}
