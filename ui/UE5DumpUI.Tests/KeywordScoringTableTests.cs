using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks down the keyword + class + flag scoring contract used by the
/// Interesting Functions Finder. Add a test here every time a keyword
/// is added to <see cref="KeywordScoringTable"/> so future bucket
/// reshuffles can't silently drop coverage.
/// </summary>
public class KeywordScoringTableTests
{
    // ------------------------------------------------------------------
    // Helper: minimal AllFunctionEntry builder. ParmsSize defaults to 4
    // so we don't accidentally hit the LargeParmsSizePenalty unless the
    // test explicitly opts in.
    // ------------------------------------------------------------------

    private static AllFunctionEntry MakeEntry(
        string funcName,
        string className,
        uint flags = 0,
        byte numParms = 1,
        ushort parmsSize = 4)
        => new()
        {
            ClassName     = className,
            ClassAddr     = "0x1000",
            SuperName     = "Object",
            ClassPath     = $"/Game/{className}.{className}",
            FuncName      = funcName,
            FuncAddr      = "0x2000",
            FunctionFlags = flags,
            NumParms      = numParms,
            ParmsSize     = parmsSize,
        };

    // Common flag bitmasks for readability.
    private const uint BlueprintCallable = 0x0400_0000;
    private const uint BlueprintEvent    = 0x0800_0000;
    private const uint BlueprintPure     = 0x1000_0000;
    private const uint Const             = 0x4000_0000;

    // ==================================================================
    // Category assignment per keyword
    // ==================================================================

    [Theory]
    [InlineData("AddMoney",          "Wallet",                  FunctionCategory.Inventory)]
    [InlineData("GetGold",           "PlayerInventory",         FunctionCategory.Inventory)]
    [InlineData("PickupLoot",        "BP_Pickup",               FunctionCategory.Inventory)]
    [InlineData("SetMaxHealth",      "PlayerCharacter",         FunctionCategory.Stats)]
    [InlineData("RestoreMana",       "PlayerCharacter",         FunctionCategory.Stats)]
    [InlineData("AddExperience",     "Player",                  FunctionCategory.Stats)]
    [InlineData("DealDamage",        "WeaponHandler",           FunctionCategory.Stats)]
    [InlineData("TeleportToWaypoint","PlayerCharacter",         FunctionCategory.Movement)]
    [InlineData("SetActorLocation",  "BaseActor",               FunctionCategory.Movement)]
    [InlineData("SetSpeed",          "Mover",                   FunctionCategory.Movement)]
    [InlineData("EnableNoClip",      "DebugCheatManager",       FunctionCategory.Movement)]
    [InlineData("CastAbility",       "AbilityHandler",          FunctionCategory.Combat)]
    [InlineData("FireWeapon",        "Weapon",                  FunctionCategory.Combat)]
    [InlineData("SaveGame",          "SaveGameSubsystem",       FunctionCategory.Utility)]
    [InlineData("SpawnActor",        "GameplayHelper",          FunctionCategory.Utility)]
    [InlineData("StartTimer",        "TimerSubsystem",          FunctionCategory.Utility)]
    public void Score_KeywordHits_AssignsExpectedCategory(
        string funcName, string className, FunctionCategory expected)
    {
        var entry = MakeEntry(funcName, className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void Score_NoKeywordHits_ReturnsOtherCategory()
    {
        var entry = MakeEntry("DoNothingPlz", "FrobnicatorComponent");
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Other, result.Category);
    }

    [Fact]
    public void Score_KeywordInClassNameOnly_StillCategorises()
    {
        // Function name is generic, but class name screams Inventory.
        var entry = MakeEntry("Update", "BP_PlayerInventory_C");
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Inventory, result.Category);
    }

    [Fact]
    public void Score_TieBetweenCategories_PicksHigherEnumOrder()
    {
        // "HealthPotion" hits Stats (Health) AND Inventory (Item-like via none... wait
        // actually no inventory match here). Use a clearer dual-hit:
        // "GoldHealth" -> Inventory (Gold) + Stats (Health), both at score 5.
        // Per the docstring: "Stats > Inventory > Movement > Combat > Utility"
        // tie-broken by enum order. Stats has the lower enum value, so Stats wins.
        var entry = MakeEntry("GoldHealth", "Misc");
        var result = KeywordScoringTable.Score(entry);
        // Both buckets matched -- finalScore should reflect both even though
        // only one Category label is shown.
        Assert.Equal(FunctionCategory.Stats, result.Category);
        Assert.True(result.KeywordHits >= 2,
            $"Expected at least 2 keyword hits, got {result.KeywordHits}");
        Assert.True(result.FinalScore >= 10,
            $"Expected score >= 10 (both buckets), got {result.FinalScore}");
    }

    // ==================================================================
    // Class bonuses
    // ==================================================================

    [Theory]
    [InlineData("PlayerCharacter",       3)]
    [InlineData("MyPawn_C",              3)]
    [InlineData("MyPlayerController",    3)]
    [InlineData("DerivedPlayerState",    3)]
    [InlineData("MyGameMode",            2)]
    [InlineData("CustomGameInstance",    2)]
    [InlineData("MySaveGame",            2)]
    [InlineData("RandomActor",           0)]
    public void Score_ClassBonus_AppliesPerSubstring(string className, int expectedBonus)
    {
        var entry = MakeEntry("InertFunc", className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(expectedBonus, result.ClassBonus);
    }

    [Theory]
    [InlineData("AnimNotify_Character",  3 - 2)] // Character (+3) + Anim (-2) = +1 net
    [InlineData("NiagaraSystemActor",    -2)]   // Niagara (-2)
    [InlineData("HUDWidget",             -1)]   // Widget (-1)
    [InlineData("MyUIPanel",             -1)]   // UI (-1)
    public void Score_ClassBonus_PenaltyAndBonusStack(string className, int expectedBonus)
    {
        var entry = MakeEntry("InertFunc", className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(expectedBonus, result.ClassBonus);
    }

    // ==================================================================
    // Flag bonuses
    // ==================================================================

    [Fact]
    public void Score_BlueprintCallable_GivesBonus()
    {
        var entry = MakeEntry("Foo", "Bar", flags: BlueprintCallable);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(2, result.FlagBonus);
    }

    [Fact]
    public void Score_PureGetterZeroParams_GetsSafeGetterBonus()
    {
        // Pure + numParms <= 1 (the return value) = safe getter -> +1
        var entry = MakeEntry("GetHealth", "Foo", flags: BlueprintPure, numParms: 1);
        var result = KeywordScoringTable.Score(entry);
        // flagBonus = SafeGetter(+1). Pure on its own without BlueprintCallable
        // doesn't grant the BC bonus.
        Assert.Equal(1, result.FlagBonus);
    }

    [Fact]
    public void Score_LargeParmsSize_GetsPenalty()
    {
        var entry = MakeEntry("Foo", "Bar", parmsSize: 80);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(-1, result.FlagBonus);
    }

    [Fact]
    public void Score_BlueprintCallableWithLargeParms_NetsToOne()
    {
        // BC(+2) + LargeParms(-1) = +1
        var entry = MakeEntry("Foo", "Bar",
            flags: BlueprintCallable, parmsSize: 80);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(1, result.FlagBonus);
    }

    // ==================================================================
    // Combined scoring (the 'real' table-tuning scenarios)
    // ==================================================================

    [Fact]
    public void Score_AddMoneyOnPlayer_ScoresAboveThreshold()
    {
        // The canonical happy-path example from the docstring.
        // Stats keyword(0) + Inventory(Money=+5) + Class(Player Character +3) +
        // Flags(BC +2) = 10 -- well above InterestingThreshold(5).
        var entry = MakeEntry("AddMoney", "PlayerCharacter",
            flags: BlueprintCallable, numParms: 2, parmsSize: 5);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Inventory, result.Category);
        Assert.True(result.FinalScore >= KeywordScoringTable.InterestingThreshold,
            $"Expected score above threshold, got {result.FinalScore}");
    }

    [Fact]
    public void Score_NiagaraDoNothing_ScoresBelowThreshold()
    {
        // Niagara penalty (-2), no keyword hits, no flag bonuses
        // -> negative or near-zero score (definitely below threshold).
        var entry = MakeEntry("UpdateInternal", "BP_NiagaraEffect_C");
        var result = KeywordScoringTable.Score(entry);
        Assert.True(result.FinalScore < KeywordScoringTable.InterestingThreshold,
            $"Niagara internal func should be below threshold, got {result.FinalScore}");
    }

    [Fact]
    public void Score_UnusedBucket_DoesNotAffectCategoryWhenScoreZero()
    {
        // A function with no keyword hits should not be assigned Stats
        // just because it has bonus from class.
        var entry = MakeEntry("Tick", "PlayerCharacter",
            flags: BlueprintCallable);
        var result = KeywordScoringTable.Score(entry);
        // No keyword hits -> Other regardless of class bonus.
        Assert.Equal(FunctionCategory.Other, result.Category);
        Assert.Equal(0, result.KeywordHits);
        // But the score still reflects the bonuses (class +3, BC +2 = 5).
        Assert.Equal(5, result.FinalScore);
    }

    // ==================================================================
    // DisplayName + CategoryColor — quick coverage so they can't be
    // accidentally NRE'd from the converter / DataGrid binding.
    // ==================================================================

    [Theory]
    [InlineData(FunctionCategory.Stats,     "Stats")]
    [InlineData(FunctionCategory.Inventory, "Inventory")]
    [InlineData(FunctionCategory.Movement,  "Movement")]
    [InlineData(FunctionCategory.Combat,    "Combat")]
    [InlineData(FunctionCategory.Utility,   "Utility")]
    [InlineData(FunctionCategory.Other,     "Other")]
    public void DisplayName_AllCategories_HasLabel(FunctionCategory cat, string expected)
    {
        Assert.Equal(expected, KeywordScoringTable.DisplayName(cat));
    }

    [Theory]
    [InlineData(FunctionCategory.Stats)]
    [InlineData(FunctionCategory.Inventory)]
    [InlineData(FunctionCategory.Movement)]
    [InlineData(FunctionCategory.Combat)]
    [InlineData(FunctionCategory.Utility)]
    [InlineData(FunctionCategory.Other)]
    public void CategoryColor_AllCategories_HasHexColor(FunctionCategory cat)
    {
        var hex = KeywordScoringTable.CategoryColor(cat);
        Assert.StartsWith("#", hex);
        Assert.Equal(7, hex.Length); // "#RRGGBB"
    }
}
