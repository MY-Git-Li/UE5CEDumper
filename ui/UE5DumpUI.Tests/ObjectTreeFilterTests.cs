using UE5DumpUI.Helpers;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks down the Object Tree multi-term ("BP_ char") filter semantics:
/// whitespace-separated terms are ANDed, each term matching Name / Class /
/// Address (case-insensitive).
/// </summary>
public class ObjectTreeFilterTests
{
    // ── SplitTerms ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitTerms_BlankIsEmpty(string? input)
        => Assert.Empty(ObjectTreeFilter.SplitTerms(input));

    [Fact]
    public void SplitTerms_SingleTerm()
        => Assert.Equal(new[] { "char" }, ObjectTreeFilter.SplitTerms("char"));

    [Fact]
    public void SplitTerms_MultipleTerms_CollapsesWhitespace()
        => Assert.Equal(new[] { "BP_", "char" }, ObjectTreeFilter.SplitTerms("  BP_   char  "));

    [Fact]
    public void SplitTerms_TabsAndNewlinesSplit()
        => Assert.Equal(new[] { "a", "b", "c" }, ObjectTreeFilter.SplitTerms("a\tb\nc"));

    // ── MatchesAllTerms ──────────────────────────────────────────────────────

    [Fact]
    public void MatchesAllTerms_EmptyTerms_MatchesEverything()
        => Assert.True(ObjectTreeFilter.MatchesAllTerms(
            System.Array.Empty<string>(), "anything", "AnyClass", "0x1"));

    [Fact]
    public void MatchesAllTerms_AllTermsMustMatch()
    {
        var terms = ObjectTreeFilter.SplitTerms("BP_ char");
        // "BP_" hits the class, "char" hits the name → match.
        Assert.True(ObjectTreeFilter.MatchesAllTerms(
            terms, "MyCharacter", "BP_MyCharacter_C", "0x140000000"));
    }

    [Fact]
    public void MatchesAllTerms_OneTermMissing_NoMatch()
    {
        var terms = ObjectTreeFilter.SplitTerms("BP_ player");
        // Has "BP_" but no "player" anywhere → reject.
        Assert.False(ObjectTreeFilter.MatchesAllTerms(
            terms, "MyCharacter", "BP_MyCharacter_C", "0x140000000"));
    }

    [Fact]
    public void MatchesAllTerms_CaseInsensitive()
    {
        var terms = ObjectTreeFilter.SplitTerms("bp_ CHAR");
        Assert.True(ObjectTreeFilter.MatchesAllTerms(
            terms, "myCharacter", "Bp_MyCharacter_C", ""));
    }

    [Fact]
    public void MatchesAllTerms_TermCanMatchAddress()
    {
        var terms = ObjectTreeFilter.SplitTerms("Pawn 1400");
        Assert.True(ObjectTreeFilter.MatchesAllTerms(
            terms, "Default__Pawn", "Pawn", "0x14000ABCD"));
    }

    [Fact]
    public void MatchesAllTerms_EachTermMayHitDifferentField()
    {
        var terms = ObjectTreeFilter.SplitTerms("widget hud abc");
        // widget → class, hud → name, abc → address: all three from different fields.
        Assert.True(ObjectTreeFilter.MatchesAllTerms(
            terms, "WB_HUD_Stone", "UserWidget", "0xABC0"));
    }
}
