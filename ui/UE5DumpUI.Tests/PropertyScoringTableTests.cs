using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks down the keyword + class-location scoring for the Interesting
/// Properties Finder (B'). Add a test here whenever a keyword is added
/// to <see cref="PropertyScoringTable"/> so coverage doesn't silently
/// regress when bucket arrays get reshuffled.
/// </summary>
public class PropertyScoringTableTests
{
    private static PropertySearchMatch Make(string propName, string className,
        string propType = "FloatProperty", int offset = 0x100)
        => new()
        {
            PropName          = propName,
            ClassName         = className,
            DefiningClassName = className,
            PropType          = propType,
            PropOffset        = offset,
            PropSize          = 4,
        };

    // ==================================================================
    // Category assignment per keyword
    // ==================================================================

    [Theory]
    [InlineData("Health",        "PlayerCharacter",  PropertyCategory.Stats)]
    [InlineData("MaxHealth",     "PlayerCharacter",  PropertyCategory.Stats)]
    [InlineData("CurrentMana",   "BP_Player_C",      PropertyCategory.Stats)]
    [InlineData("Stamina",       "PawnState",        PropertyCategory.Stats)]
    [InlineData("Level",         "PlayerState",      PropertyCategory.Stats)]
    [InlineData("Damage",        "WeaponDataAsset",  PropertyCategory.Combat)]
    [InlineData("BaseDamage",    "BP_Enemy_C",       PropertyCategory.Combat)]
    [InlineData("Armor",         "BP_Player_C",      PropertyCategory.Combat)]
    [InlineData("CritRate",      "PlayerStats",      PropertyCategory.Combat)]
    [InlineData("Gold",          "Inventory",        PropertyCategory.Resources)]
    [InlineData("Coins",         "BP_Player_C",      PropertyCategory.Resources)]
    [InlineData("Ammo",          "Weapon",           PropertyCategory.Resources)]
    [InlineData("Speed",         "MovementComponent",PropertyCategory.Movement)]
    [InlineData("JumpHeight",    "BP_Player_C",      PropertyCategory.Movement)]
    [InlineData("Friction",      "MovementSettings", PropertyCategory.Movement)]
    [InlineData("IsImmortal",    "BP_Player_C",      PropertyCategory.Utility)]
    [InlineData("bCanBeDamaged", "AActor",           PropertyCategory.Utility)]
    public void Score_KeywordHits_AssignsExpectedCategory(
        string propName, string className, PropertyCategory expected)
    {
        var match = Make(propName, className);
        var result = PropertyScoringTable.Score(match);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void Score_NoKeywordHits_ReturnsOther()
    {
        var match = Make("ComponentTickInterval", "FrobnicatorComponent");
        var result = PropertyScoringTable.Score(match);
        Assert.Equal(PropertyCategory.Other, result.Category);
    }

    // ==================================================================
    // Class-location bonus + Unusual flag
    // ==================================================================

    [Theory]
    [InlineData("PlayerCharacter",       false, 3)]  // Expected — Character bonus
    [InlineData("AbilitySystemComponent",false, 3)]
    [InlineData("BP_Inventory_C",        false, 3)]
    [InlineData("MyGameMode",            false, 2)]  // game-level
    [InlineData("LocalPlayer",           true,  4)]  // ⚠ Unusual
    [InlineData("UGameViewportClient",   true,  4)]
    [InlineData("BP_HUD_C",              true,  4)]
    [InlineData("UCheatManager",         true,  4)]  // matches both "UCheatManager" and "CheatManager"
                                                       // -> 4 + 4 = 8 (validated separately in stacking test)
    [InlineData("AnimMontage",           false, -2)] // animation noise
    [InlineData("ParticleEmitter",       false, -2)]
    [InlineData("FoobarComponent",       false, 0)]  // no match
    public void PropertyBonus_ClassLocation(
        string className, bool expectedUnusual, int expectedBonus)
    {
        var (bonus, isUnusual) = ClassLocationScorer.PropertyBonus(className);
        Assert.Equal(expectedUnusual, isUnusual);
        // For UCheatManager the substring matches BOTH rules so bonus stacks
        // (4 + 4 = 8). Test that here as a tolerance match, since the
        // contract is "sum stacked bonuses across all matches".
        if (className == "UCheatManager")
            Assert.Equal(8, bonus);
        else
            Assert.Equal(expectedBonus, bonus);
    }

    [Fact]
    public void PropertyBonus_EmptyClassName_ReturnsZero()
    {
        var (bonus, isUnusual) = ClassLocationScorer.PropertyBonus("");
        Assert.Equal(0, bonus);
        Assert.False(isUnusual);
    }

    [Fact]
    public void Score_HealthOnLocalPlayer_FlagsUnusualLocationAtTopScore()
    {
        // The whole point of B': HP/Health fields shouldn't normally live
        // on LocalPlayer. When they DO, the score should be high (Stats
        // keyword + Unusual class bonus) AND the Unusual flag set.
        var match = Make("Health", "ULocalPlayer", propType: "FloatProperty");
        var result = PropertyScoringTable.Score(match);

        Assert.Equal(PropertyCategory.Stats, result.Category);
        Assert.True(result.IsUnusualLocation,
            "LocalPlayer should trigger Unusual Location flag");
        // Stats keyword(5) + LocalPlayer class bonus(4) = 9, well above threshold(4)
        Assert.True(result.FinalScore >= 8,
            $"Expected score >= 8 (Health=5 + LocalPlayer=4), got {result.FinalScore}");
    }

    [Fact]
    public void Score_HealthOnPlayerCharacter_DoesNotFlagUnusual()
    {
        var match = Make("Health", "PlayerCharacter");
        var result = PropertyScoringTable.Score(match);

        Assert.Equal(PropertyCategory.Stats, result.Category);
        Assert.False(result.IsUnusualLocation,
            "PlayerCharacter is the conventional location — should not be flagged Unusual");
    }

    [Fact]
    public void Score_AnimPenaltyAppliesToProperties()
    {
        // Same stacking behaviour as Function side: a property called
        // "Health" on "AnimCharacter" gets Character bonus(+3) + Anim
        // penalty(-2) = +1, accurately reflecting that it's likely an
        // animation-side mirror of a real Health field elsewhere.
        var match = Make("Health", "AnimCharacter");
        var result = PropertyScoringTable.Score(match);

        // Stats keyword(5) + classBonus(3 - 2 = 1) = 6
        Assert.Equal(6, result.FinalScore);
        Assert.Equal(1, result.ClassBonus);
        Assert.False(result.IsUnusualLocation);
    }

    // ==================================================================
    // Tokenisation — protect against acronym collisions
    // ==================================================================

    [Fact]
    public void Score_ComponentName_DoesNotFalseMatchMP()
    {
        // Build 609 lesson: "Component" contains "mp" as substring.
        // Token matching must NOT score this as a Stats hit.
        var match = Make("ComponentTickEnabled", "SomeComponent");
        var result = PropertyScoringTable.Score(match);
        Assert.Equal(PropertyCategory.Other, result.Category);
    }

    [Fact]
    public void Score_LevitateName_DoesNotFalseMatchLv()
    {
        // Same family: short acronym Lv shouldn't match "Levitate".
        var match = Make("IsLevitating", "BP_FlyingEnemy_C");
        var result = PropertyScoringTable.Score(match);
        Assert.NotEqual(PropertyCategory.Stats, result.Category);
    }

    // ==================================================================
    // Sanity on the threshold
    // ==================================================================

    [Fact]
    public void Threshold_IsRoughlyHalfOfStatsScore()
    {
        // Threshold tuning sanity: a single Stats keyword hit (5) should
        // clear the threshold (4). A Utility hit alone (3) should NOT.
        // If this fails after a tuning change, update the test and
        // explicitly document the new selectivity intent.
        Assert.True(PropertyScoringTable.StatsKeywordScore >=
                    PropertyScoringTable.InterestingThreshold,
            "Stats keyword hit alone should clear threshold");
        Assert.True(PropertyScoringTable.UtilityKeywordScore <
                    PropertyScoringTable.InterestingThreshold,
            "Single Utility keyword hit alone should NOT clear threshold");
    }

    // ==================================================================
    // Function-side ClassLocationScorer (sanity that the refactor didn't
    // break the existing Function bonus shape).
    // ==================================================================

    [Theory]
    [InlineData("PlayerCharacter",  3)]
    [InlineData("AnimMontage",     -2)]
    [InlineData("GameMode",         2)]
    [InlineData("AnimCharacter",    1)]   // Character(+3) + Anim(-2)
    [InlineData("FoobarComponent",  0)]
    public void FunctionBonus_PreservesExistingContract(string className, int expected)
    {
        Assert.Equal(expected, ClassLocationScorer.FunctionBonus(className));
    }
}
