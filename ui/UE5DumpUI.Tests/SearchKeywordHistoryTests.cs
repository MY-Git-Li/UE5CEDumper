using System.Collections.Generic;
using UE5DumpUI.Helpers;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks down the Live Walker remembered-keyword rules: LRU ordering, the
/// 8-entry cap, the "longest valid wins" prefix collapse, and the minimum
/// length backstop.
/// </summary>
public class SearchKeywordHistoryTests
{
    private static List<string> New() => new();

    // ── Basic add / validity ─────────────────────────────────────────────────

    [Fact]
    public void Remember_AddsValidKeyword()
    {
        var h = New();
        Assert.True(SearchKeywordHistory.Remember(h, "magic"));
        Assert.Equal(new[] { "magic" }, h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("m")]   // below MinLength (2)
    public void Remember_RejectsTooShortOrBlank(string? keyword)
    {
        var h = New();
        Assert.False(SearchKeywordHistory.Remember(h, keyword));
        Assert.Empty(h);
    }

    [Fact]
    public void Remember_TrimsWhitespace()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "  magic  ");
        Assert.Equal(new[] { "magic" }, h);
    }

    // ── LRU ordering ─────────────────────────────────────────────────────────

    [Fact]
    public void Remember_MostRecentFirst()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "health");
        SearchKeywordHistory.Remember(h, "mana");
        SearchKeywordHistory.Remember(h, "stamina");
        Assert.Equal(new[] { "stamina", "mana", "health" }, h);
    }

    [Fact]
    public void Remember_ReusingKeyword_MovesToFront()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "health");
        SearchKeywordHistory.Remember(h, "mana");
        SearchKeywordHistory.Remember(h, "health");   // touch
        Assert.Equal(new[] { "health", "mana" }, h);
    }

    [Fact]
    public void Remember_ExactDuplicate_CaseInsensitive_NoGrowth()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "Magic");
        SearchKeywordHistory.Remember(h, "magic");
        Assert.Single(h);
        Assert.Equal("magic", h[0]);   // latest casing wins, moved to front
    }

    // ── Longest valid wins ───────────────────────────────────────────────────

    [Fact]
    public void Remember_LongerKeyword_ReplacesPrefix()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "magi");
        SearchKeywordHistory.Remember(h, "magic");   // extends "magi"
        Assert.Equal(new[] { "magic" }, h);
    }

    [Fact]
    public void Remember_PrefixOfExisting_Ignored()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "magic");
        Assert.False(SearchKeywordHistory.Remember(h, "magi"));   // shorter prefix
        Assert.Equal(new[] { "magic" }, h);
    }

    [Fact]
    public void Remember_TypingProgression_KeepsOnlyLongest()
    {
        var h = New();
        // Simulate the debounce firing at several typing pauses on the way to "magic".
        SearchKeywordHistory.Remember(h, "ma");
        SearchKeywordHistory.Remember(h, "mag");
        SearchKeywordHistory.Remember(h, "magi");
        SearchKeywordHistory.Remember(h, "magic");
        Assert.Equal(new[] { "magic" }, h);
    }

    [Fact]
    public void Remember_PrefixCollapse_DoesNotTouchUnrelated()
    {
        var h = New();
        SearchKeywordHistory.Remember(h, "health");
        SearchKeywordHistory.Remember(h, "mag");
        SearchKeywordHistory.Remember(h, "magic");   // collapses "mag", keeps "health"
        Assert.Equal(new[] { "magic", "health" }, h);
    }

    // ── LRU cap ──────────────────────────────────────────────────────────────

    [Fact]
    public void Remember_EvictsBeyondMax()
    {
        var h = New();
        for (int i = 0; i < SearchKeywordHistory.MaxEntries + 3; i++)
            SearchKeywordHistory.Remember(h, $"kw{i:D2}");

        Assert.Equal(SearchKeywordHistory.MaxEntries, h.Count);
        // Newest kept, oldest evicted.
        Assert.Equal($"kw{SearchKeywordHistory.MaxEntries + 2:D2}", h[0]);
        Assert.DoesNotContain("kw00", h);
    }
}
