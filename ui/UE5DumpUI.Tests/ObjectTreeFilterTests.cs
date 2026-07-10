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

    // ── MatchesAllTerms — params (multi-field) overload ─────────────────────
    // The shared "space = AND" matcher every panel reuses: term-level AND,
    // field-level OR, over an arbitrary field count (1/2/4/5…).

    [Fact]
    public void MatchesAllTerms_Params_SingleField_AndWithinField()
    {
        var terms = ObjectTreeFilter.SplitTerms("attr set");
        // Both terms must appear in the single field (Snapshot/SPC single-field boxes).
        Assert.True(ObjectTreeFilter.MatchesAllTerms(terms, "CharacterAttributeSet"));
        Assert.False(ObjectTreeFilter.MatchesAllTerms(terms, "CharacterAttribute"));
    }

    [Fact]
    public void MatchesAllTerms_Params_TwoFields_OrAcrossFields()
    {
        var terms = ObjectTreeFilter.SplitTerms("add money");
        // "add" hits FuncName, "money" hits ClassName → match (Console/Funcs panels).
        Assert.True(ObjectTreeFilter.MatchesAllTerms(terms, "AddCurrency", "PlayerMoneyComponent"));
        Assert.False(ObjectTreeFilter.MatchesAllTerms(terms, "AddCurrency", "PlayerInventory"));
    }

    [Fact]
    public void MatchesAllTerms_Params_FiveFields_GlobalFilter()
    {
        var terms = ObjectTreeFilter.SplitTerms("health float");
        // Global filters match across 5 columns (Snapshot DiffGlobal / SPC ResultGlobal).
        Assert.True(ObjectTreeFilter.MatchesAllTerms(
            terms, "BP_Player_C", "MaxHealth", "/Game/Player", "FloatProperty", "100 -> 120"));
    }

    [Fact]
    public void MatchesAllTerms_Params_SkipsNullAndEmptyFields()
    {
        var terms = ObjectTreeFilter.SplitTerms("pawn");
        // Null / empty fields are skipped, not matched — only the real field counts.
        // 4 fields so overload resolution binds the params overload (not the 3-string one).
        Assert.True(ObjectTreeFilter.MatchesAllTerms(terms, null, "", "APawn", null));
        Assert.False(ObjectTreeFilter.MatchesAllTerms(terms, null, "", "AActor", null));
    }

    [Fact]
    public void MatchesAllTerms_Params_EmptyTerms_MatchesEverything()
        => Assert.True(ObjectTreeFilter.MatchesAllTerms(
            System.Array.Empty<string>(), "a", "b"));
}
