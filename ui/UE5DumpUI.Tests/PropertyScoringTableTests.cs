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
    // Build 678 — keywords added from 15-game cross-game analysis.
    // Lock in category assignments so future bucket reshuffles don't
    // silently regress.
    [InlineData("DamageEffect",  "BP_Ability_C",     PropertyCategory.Combat)]
    [InlineData("AttackTarget",  "BP_Enemy_C",       PropertyCategory.Combat)]
    [InlineData("EffectRadius",  "BP_Spell_C",       PropertyCategory.Combat)]
    [InlineData("AbilityType",   "BP_GameplayAbility_C", PropertyCategory.Combat)]
    [InlineData("DamageModifier","WeaponData",       PropertyCategory.Combat)]
    [InlineData("BuffDuration",  "BP_Effect_C",      PropertyCategory.Combat)]
    [InlineData("ItemCount",     "InventoryComponent", PropertyCategory.Resources)]
    [InlineData("ItemList",      "BP_Container_C",   PropertyCategory.Resources)]
    // === L1 — Timing category (2026-07-13). BuffDuration above is the
    // regression guard that the Timing-last tie-break keeps "Duration" in
    // Combat; these confirm pure-timer names land in Timing. ===
    [InlineData("Cooldown",         "BP_Ability_C", PropertyCategory.Timing)]
    [InlineData("RemainingTime",    "BP_Spell_C",   PropertyCategory.Timing)]
    [InlineData("RespawnDelay",     "MyGameMode",   PropertyCategory.Timing)]
    [InlineData("CastTime",         "BP_Ability_C", PropertyCategory.Timing)]
    [InlineData("CustomTimeDilation","AActor",      PropertyCategory.Timing)]
    [InlineData("TickInterval",     "BP_Spawner_C", PropertyCategory.Timing)]
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
        // Sentinel must contain NO keyword token. (Was "ComponentTickInterval"
        // pre-L1; "Interval" is now a Timing keyword, so a genuinely
        // keyword-free name is used instead.)
        var match = Make("MeshSectionIndex", "FrobnicatorComponent");
        var result = PropertyScoringTable.Score(match);
        Assert.Equal(PropertyCategory.Other, result.Category);
    }

    // ==================================================================
    // Class-location bonus + Unusual flag
    // ==================================================================

    [Theory]
    [InlineData("PlayerCharacter",       false, 5)]  // PlayerController?(no) + Player(+2) + Character(+3) — both stack
    [InlineData("AbilitySystemComponent",false, 3)]
    [InlineData("BP_Inventory_C",        false, 3)]
    [InlineData("MyGameMode",            false, 2)]  // game-level
    [InlineData("AnimMan_Player_C",      false, 2)]  // Player bonus only (no penalty for game-prefix "Anim")
    [InlineData("BP_Player_C",           false, 2)]  // Generic "Player" rule
    [InlineData("LocalPlayer",           true,  6)]  // LocalPlayer(+4 Unusual) + Player(+2)
    [InlineData("UGameViewportClient",   true,  4)]
    [InlineData("BP_HUD_C",              true,  4)]
    [InlineData("FoobarComponent",       false, 0)]  // no match
    // === Build 678 — class rules from 15-game cross-game analysis. ===
    [InlineData("BP_Weapon_C",           false, 2)]  // Weapon class
    [InlineData("WeaponData",            false, 2)]  // Weapon-prefix struct
    [InlineData("BP_Projectile_C",       false, 2)]  // Projectile class
    [InlineData("BP_Battle_Manager_C",   false, 2)]  // Battle class
    // === Build 686 — Phase 2 function-side analyzer surfaced Enemy. ===
    [InlineData("BP_Enemy_C",            false, 2)]  // Enemy class
    [InlineData("EnemyBase",             false, 2)]  // Enemy-prefix
    // === 7-game cross-game analysis (2026-07-09): Health / Damage +2. ===
    [InlineData("BP_HealthComponent_C",  false, 2)]  // Health class
    [InlineData("UDamageDealer",         false, 2)]  // Damage class
    [InlineData("GrimAttributeSetHealth",false, 5)]  // AttributeSet(+3) + Health(+2) stack
    // === L1 timer-home classes (2026-07-13): GameplayEffect / GameplayAbility
    // / WorldSettings +2. Clean tokens — verified none appear in the class
    // names of the locked rows above, so they add no new bonus there. ===
    [InlineData("BP_GameplayEffect_C",   false, 2)]  // GameplayEffect
    [InlineData("MyGameplayAbility",     false, 2)]  // GameplayAbility
    [InlineData("AWorldSettings",        false, 2)]  // WorldSettings — TimeDilation home
    public void PropertyBonus_ClassLocation(
        string className, bool expectedUnusual, int expectedBonus)
    {
        var (bonus, isUnusual) = ClassLocationScorer.PropertyBonus(className);
        Assert.Equal(expectedUnusual, isUnusual);
        Assert.Equal(expectedBonus, bonus);
    }

    [Fact]
    public void PropertyBonus_CheatManager_StacksRules()
    {
        // "UCheatManager" matches both "UCheatManager" (+4 Unusual) AND
        // "CheatManager" (+4 Unusual) — bonuses stack to 8.
        var (bonus, isUnusual) = ClassLocationScorer.PropertyBonus("UCheatManager");
        Assert.True(isUnusual);
        Assert.Equal(8, bonus);
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
        // Stats keyword(5) + LocalPlayer(+4) + Player(+2) = 11, well above threshold(4)
        Assert.True(result.FinalScore >= 10,
            $"Expected score >= 10 (Health=5 + LocalPlayer=4 + Player=2), got {result.FinalScore}");
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
    public void Score_HealthOnAnimManPlayerC_NotPenalised_UserRegressionCase()
    {
        // TowerOfMask regression: the game's player character is named
        // "AnimMan_Player_C" — substring "Anim" used to apply a -2 penalty
        // which made `Health @ AnimMan_Player_C` score Stats(5) - Anim(2)
        // = 3, below the threshold of 4 — so it never surfaced in the
        // Interesting Properties list. With surgical compound-name
        // penalties on the Function side and zero penalty on the Property
        // side, AnimMan_Player_C gets only the Player(+2) bonus.
        var match = Make("Health", "AnimMan_Player_C");
        var result = PropertyScoringTable.Score(match);

        Assert.Equal(PropertyCategory.Stats, result.Category);
        Assert.False(result.IsUnusualLocation);
        // Stats(5) + Player(+2) = 7
        Assert.Equal(7, result.FinalScore);
        Assert.Equal(2, result.ClassBonus);
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
    [InlineData("PlayerCharacter",  5)]  // Player(+2) + Character(+3)
    [InlineData("AnimMontage",     -2)]  // surgical AnimMontage penalty
    [InlineData("AnimMan_Player_C", 2)]  // Player(+2) — no Anim penalty since
                                         // it's not a compound anim-framework name
    [InlineData("GameMode",         2)]
    [InlineData("FoobarComponent",  0)]
    // === Build 686 — Phase 2 function-side data-driven adds. ===
    [InlineData("BP_Enemy_C",       2)]  // Enemy +2
    [InlineData("WeaponBase",       2)]  // Weapon +2 (mirror of property side)
    [InlineData("EnemyWeapon_C",    4)]  // both stack: Enemy(+2) + Weapon(+2)
    // === 7-game cross-game analysis (2026-07-09): Health / Damage +2. ===
    [InlineData("HealthComponent",  2)]  // Health +2
    [InlineData("BP_DamageZone_C",  2)]  // Damage +2
    public void FunctionBonus_PreservesExistingContract(string className, int expected)
    {
        Assert.Equal(expected, ClassLocationScorer.FunctionBonus(className));
    }

    // ==================================================================
    // Structural signals (P2a): GAS attribute + type prior + stat pairing.
    // These use only PropType + StructType (already on PropertySearchMatch),
    // so they run with no new wire data.
    // ==================================================================

    private static PropertySearchMatch MakeEx(string propName, string className,
        string propType, string structType = "", int offset = 0x100, ulong propertyFlags = 0)
        => new()
        {
            PropName          = propName,
            ClassName         = className,
            DefiningClassName = className,
            PropType          = propType,
            StructType        = structType,
            PropOffset        = offset,
            PropSize          = 4,
            PropertyFlags     = propertyFlags,
        };

    // UE EPropertyFlags bits used by the P2b gating tests.
    private const ulong CPF_BlueprintVisible = 0x0000000000000004UL;
    private const ulong CPF_SaveGame         = 0x0000000001000000UL;
    private const ulong CPF_EditorOnly       = 0x0000000800000000UL;

    [Fact]
    public void Score_SaveGameFlag_BoostsPersistentField()
    {
        var save  = PropertyScoringTable.Score(
            MakeEx("Gold", "BP_Player_C", "IntProperty", propertyFlags: CPF_SaveGame));
        var plain = PropertyScoringTable.Score(
            MakeEx("Gold", "BP_Player_C", "IntProperty"));

        Assert.Equal(PropertyScoringTable.SaveGameBonus, save.StructuralBonus);
        Assert.True(save.FinalScore > plain.FinalScore);
    }

    [Fact]
    public void Score_EditorOnlyFlag_IsPenalised()
    {
        var r = PropertyScoringTable.Score(
            MakeEx("Health", "BP_Player_C", "FloatProperty", propertyFlags: CPF_EditorOnly));
        Assert.Equal(PropertyScoringTable.EditorOnlyPenalty, r.StructuralBonus);
    }

    [Fact]
    public void Score_BlueprintVisibleFlag_MildBoost_WhenNotSaveGame()
    {
        var r = PropertyScoringTable.Score(
            MakeEx("Mana", "BP_Player_C", "FloatProperty", propertyFlags: CPF_BlueprintVisible));
        Assert.Equal(PropertyScoringTable.BlueprintVisibleBonus, r.StructuralBonus);
    }

    [Fact]
    public void Score_SaveGameTakesPrecedenceOverBlueprintVisible()
    {
        // Both set → SaveGame wins (else-if) rather than double-counting.
        var r = PropertyScoringTable.Score(
            MakeEx("Level", "PlayerState", "IntProperty",
                   propertyFlags: CPF_SaveGame | CPF_BlueprintVisible));
        Assert.Equal(PropertyScoringTable.SaveGameBonus, r.StructuralBonus);
    }

    [Fact]
    public void Score_GasAttribute_BoostsAndDefaultsToStats()
    {
        // An FGameplayAttributeData whose name is NOT a keyword still surfaces
        // as a Stats attribute purely from its struct type.
        var r = PropertyScoringTable.Score(
            MakeEx("Toughness", "MyAttributeSet", "StructProperty", "GameplayAttributeData"));

        Assert.True(r.IsGasAttribute);
        Assert.Equal(PropertyCategory.Stats, r.Category);
        Assert.Equal(PropertyScoringTable.GasAttributeBonus, r.StructuralBonus);
        Assert.True(r.FinalScore >= PropertyScoringTable.InterestingThreshold);
    }

    [Fact]
    public void Score_TimeStruct_DefaultsToTimingByStructType()
    {
        // An FTimespan whose NAME misses every keyword still surfaces as a
        // Timing field purely from its struct type (mirrors the GAS branch).
        var r = PropertyScoringTable.Score(
            MakeEx("Foobar", "BP_Widget_C", "StructProperty", "Timespan"));

        Assert.Equal(PropertyCategory.Timing, r.Category);
        Assert.Equal(PropertyScoringTable.TimeStructBonus, r.StructuralBonus);
        Assert.True(r.FinalScore >= PropertyScoringTable.InterestingThreshold);
    }

    [Fact]
    public void Score_GasStruct_NotMisclassifiedAsTimeStruct()
    {
        // Regression guard: the GAS branch runs first, so a GameplayAttributeData
        // stays Stats and gets the GAS bonus, never the time-struct path.
        var r = PropertyScoringTable.Score(
            MakeEx("Toughness", "MyAttributeSet", "StructProperty", "GameplayAttributeData"));
        Assert.Equal(PropertyCategory.Stats, r.Category);
        Assert.Equal(PropertyScoringTable.GasAttributeBonus, r.StructuralBonus);
    }

    [Fact]
    public void Score_GasHealth_OutscoresPlainFloatHealth()
    {
        var gas = PropertyScoringTable.Score(
            MakeEx("Health", "MyAttributeSet", "StructProperty", "GameplayAttributeData"));
        var plain = PropertyScoringTable.Score(
            MakeEx("Health", "MyAttributeSet", "FloatProperty"));

        Assert.True(gas.FinalScore > plain.FinalScore);
        Assert.Equal(PropertyCategory.Stats, gas.Category);
    }

    [Fact]
    public void Score_ValueKeywordOnPointerType_GetsGentlePenalty()
    {
        // "Health" on an ObjectProperty (a HealthComponent pointer) is a
        // holder, not the live number → -1 structural nudge.
        var obj   = PropertyScoringTable.Score(Make("Health", "BP_Player_C", propType: "ObjectProperty"));
        var value = PropertyScoringTable.Score(Make("Health", "BP_Player_C", propType: "FloatProperty"));

        Assert.Equal(PropertyScoringTable.NonValueTypePenalty, obj.StructuralBonus);
        Assert.Equal(0, value.StructuralBonus);
        Assert.True(value.FinalScore > obj.FinalScore);
    }

    [Fact]
    public void Score_NumericValueType_StaysNeutral_RegressionGuard()
    {
        // The exact-score contract tests above rely on FloatProperty staying
        // neutral (structuralBonus 0) so P2a doesn't shift historical scores.
        var r = PropertyScoringTable.Score(Make("Health", "AnimMan_Player_C")); // FloatProperty
        Assert.Equal(0, r.StructuralBonus);
        Assert.Equal(7, r.FinalScore); // Stats(5) + Player(2) — unchanged by P2a
    }

    [Theory]
    [InlineData("Health",        false, "health")]
    [InlineData("MaxHealth",     true,  "health")]
    [InlineData("MaximumHealth", true,  "health")]  // TQ2 full-word naming
    [InlineData("MinimumMana",   true,  "mana")]
    [InlineData("CurrentMana",   true,  "mana")]
    [InlineData("HealthMax",     true,  "health")]
    [InlineData("BaseDamage",    true,  "damage")]
    [InlineData("Max",           false, "")]      // all-qualifier → no stem
    [InlineData("Gold",          false, "gold")]
    public void NormaliseStatStem_StripsQualifiers(string name, bool expectVariant, string expectStem)
    {
        var (stem, isVariant) = PropertyScoringTable.NormaliseStatStem(name);
        Assert.Equal(expectStem, stem);
        Assert.Equal(expectVariant, isVariant);
    }

    [Fact]
    public void ComputeStatPairBonuses_PairsBaseAndMax_NotLoneFields()
    {
        var matches = new[]
        {
            Make("Health",    "BP_Player_C", offset: 0x10),
            Make("MaxHealth", "BP_Player_C", offset: 0x14),
            Make("Gold",      "BP_Player_C", offset: 0x20), // lone — no variant sibling
        };
        var bonuses = PropertyScoringTable.ComputeStatPairBonuses(matches);

        Assert.Equal(PropertyScoringTable.StatPairBonus, bonuses[("BP_Player_C", "Health", 0x10)]);
        Assert.Equal(PropertyScoringTable.StatPairBonus, bonuses[("BP_Player_C", "MaxHealth", 0x14)]);
        Assert.False(bonuses.ContainsKey(("BP_Player_C", "Gold", 0x20)));
    }

    [Fact]
    public void ComputeStatPairBonuses_TwoVariantsSameStem_BothBoosted()
    {
        var matches = new[]
        {
            Make("CurrentHP", "PawnState", offset: 0x10),
            Make("MaxHP",     "PawnState", offset: 0x14),
        };
        var bonuses = PropertyScoringTable.ComputeStatPairBonuses(matches);
        Assert.Equal(2, bonuses.Count);
    }

    [Fact]
    public void ComputeStatPairBonuses_DifferentClasses_DoNotPair()
    {
        var matches = new[]
        {
            Make("Health",    "ClassA", offset: 0x10),
            Make("MaxHealth", "ClassB", offset: 0x14),
        };
        Assert.Empty(PropertyScoringTable.ComputeStatPairBonuses(matches));
    }

    [Fact]
    public void ComputeStatPairBonuses_FullWordMaximumCurrent_Pairs()
    {
        // Real TQ2 case: GrimAttributeSetHealth has MaximumHealth + CurrentHealth
        // (full-word qualifiers). Both must pair.
        var matches = new[]
        {
            Make("MaximumHealth", "GrimAttributeSetHealth", offset: 0x10),
            Make("CurrentHealth", "GrimAttributeSetHealth", offset: 0x14),
        };
        Assert.Equal(2, PropertyScoringTable.ComputeStatPairBonuses(matches).Count);
    }
}
