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
    [InlineData("PlayerCharacter",       5)]  // Player(+2) + Character(+3)
    [InlineData("MyPawn_C",              3)]  // Pawn — no Player substring
    [InlineData("MyPlayerController",    5)]  // PlayerController(+3) + Player(+2)
    [InlineData("DerivedPlayerState",    5)]  // PlayerState(+3) + Player(+2)
    [InlineData("MyGameMode",            2)]
    [InlineData("CustomGameInstance",    2)]
    [InlineData("MySaveGame",            2)]
    [InlineData("AnimMan_Player_C",      2)]  // Player(+2) only — no longer penalised
                                              // for "Anim" prefix (build 671 TowerOfMask regression)
    [InlineData("RandomActor",           0)]
    public void Score_ClassBonus_AppliesPerSubstring(string className, int expectedBonus)
    {
        var entry = MakeEntry("InertFunc", className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(expectedBonus, result.ClassBonus);
    }

    [Theory]
    [InlineData("AnimNotify_Character",  3 - 2)]      // Character(+3) + AnimNotify(-2)
    [InlineData("UAnimInstance",         -2)]         // surgical AnimInstance penalty
    [InlineData("NiagaraSystemActor",    -2)]         // NiagaraSystem(-2)
    [InlineData("MyUserWidget",          -1)]         // UserWidget(-1)
    [InlineData("MyHUDWidgetComponent",  -1)]         // WidgetComponent(-1)
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
        // Bonuses: Player(+2) + Character(+3) + BlueprintCallable(+2) = 7.
        Assert.Equal(7, result.FinalScore);
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

    // ==================================================================
    // Tokenisation regression cases (build 608+)
    //
    // These are the substring-noise traps that motivated the switch to
    // KeywordTokenizer. Each pair documents:
    //   - "Was a false-positive in v1": now correctly Other.
    //   - "Was a false-negative in v1": now correctly categorised.
    // ==================================================================

    [Theory]
    [InlineData("DoNothing",        "FrobnicatorComponent")] // "Component" used to substring-hit Stats via "MP"
    [InlineData("Update",           "MyComponent")]
    [InlineData("Initialize",       "RandomActor")]
    [InlineData("Tick",             "BaseActor")]
    [InlineData("OnStreamReady",    "TPSStream")] // "TPS" used to substring-hit Movement via "TP"
    public void TokenisationRegression_FormerSubstringFalsePositives_NowOther(
        string funcName, string className)
    {
        var entry = MakeEntry(funcName, className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Other, result.Category);
    }

    [Theory]
    // Acronym-ending function names that v1 had to drop (HP/MP/SP/XP/TP).
    [InlineData("GetHP",        "PlayerCharacter", FunctionCategory.Stats)]
    [InlineData("SetMaxMP",     "PlayerCharacter", FunctionCategory.Stats)]
    [InlineData("RestoreSP",    "PlayerCharacter", FunctionCategory.Stats)]
    [InlineData("AddXP",        "PlayerCharacter", FunctionCategory.Stats)]
    // 'TP' as a Movement keyword -- only matches as a standalone token
    // (e.g. "DoTP" / "InstantTP"), not buried in "TPSStream".
    [InlineData("DoTP",         "PlayerCharacter", FunctionCategory.Movement)]
    public void TokenisationRegression_AcronymsRestored_AssignsCategory(
        string funcName, string className, FunctionCategory expected)
    {
        var entry = MakeEntry(funcName, className);
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void TokenisationRegression_RestoredKeywords_DropTimeClock()
    {
        // 'Drop' restored to Inventory (was dropped due to substring noise).
        var drop = KeywordScoringTable.Score(MakeEntry("DropItem", "Inventory"));
        Assert.Equal(FunctionCategory.Inventory, drop.Category);

        // 'Time'/'Clock' restored to Utility (Lifetime no longer false-fires).
        var lifetime = KeywordScoringTable.Score(MakeEntry("GetLifetime", "Actor"));
        Assert.Equal(FunctionCategory.Other, lifetime.Category); // 'Lifetime' is single token, not 'Time'
        var realTime = KeywordScoringTable.Score(MakeEntry("GetTimeRemaining", "Timer"));
        Assert.Equal(FunctionCategory.Utility, realTime.Category);
    }

    [Fact]
    public void TokenisationRegression_MultiTokenKeyword_NoClipMatchesEnableNoClip()
    {
        // Multi-token keyword: "NoClip" -> ["no","clip"]. Function
        // "EnableNoClip" tokens = ["enable","no","clip"] -- subset
        // match must succeed for Movement category.
        var entry = MakeEntry("EnableNoClip", "DebugCheatManager");
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Movement, result.Category);
    }

    [Fact]
    public void TokenisationRegression_MultiTokenKeyword_GodModeDoesNotMatchDecoration()
    {
        // "God" (single token in ExplicitMovementCheats) should match
        // "ToggleGodMode" -> ["toggle","god","mode"] cleanly.
        var entry = MakeEntry("ToggleGodMode", "DebugManager");
        var result = KeywordScoringTable.Score(entry);
        Assert.Equal(FunctionCategory.Movement, result.Category);
        // And also "ToggleGodMode" still wins over Utility's "Toggle"
        // because ExplicitMovementCheats per-hit (8) > Utility per-hit (3).
    }
}
