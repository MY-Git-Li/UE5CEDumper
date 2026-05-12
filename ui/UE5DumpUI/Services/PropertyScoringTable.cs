using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Categories surfaced by the Interesting Properties Finder (B').
/// One property gets exactly one category — the one with the highest
/// keyword hit score, broken by enum order (Stats &gt; Combat &gt;
/// Resources &gt; Movement &gt; Utility &gt; Other).
///
/// Smaller bucket count than Function-side because property NAMES are
/// usually noun-only (no verb to disambiguate Stats from Combat). When
/// in doubt the table groups by the data-domain rather than the verb.
/// </summary>
public enum PropertyCategory
{
    Other,      // Default — no keyword hits, or score below threshold
    Stats,      // HP / MP / Stamina / XP / Level (numeric character state)
    Combat,     // Damage / Defense / Armor / Crit (numeric combat tuning)
    Resources,  // Gold / Coin / Gem / Ammo / Material (collectibles)
    Movement,   // Speed / Jump / Friction (numeric movement tuning)
    Utility,    // Checkpoint / Save / Quest / Cheat (state flags)
}

/// <summary>
/// Keyword + class-location heuristic for ranking UProperties by
/// "interesting-to-cheat-engine-user" probability.
///
/// Two layers of signal:
/// 1. Keyword match against the property NAME (KeywordTokenizer
///    whole-token comparison — same machinery the Function side uses).
/// 2. Class-location bonus via <see cref="ClassLocationScorer.PropertyBonus"/>.
///    The Unusual flag from PropertyBonus drives the ⚠ Unusual Location
///    badge in the UI — those are properties sitting in non-canonical
///    containers (LocalPlayer / GameViewportClient / HUD / etc.), which
///    are exactly the hits a CE user would miss looking in
///    Character/Pawn/PlayerState.
///
/// Add a keyword:
///   1. Pick the right category bucket below.
///   2. Append (case doesn't matter — comparison is case-insensitive
///      via KeywordTokenizer).
///   3. Add a test in PropertyScoringTableTests.
/// </summary>
public static class PropertyScoringTable
{
    /// <summary>Score threshold: rows with FinalScore &lt; this are
    /// dropped from default view (visible only when "Show all" is
    /// toggled in the UI). Calibrated against the Function-side
    /// threshold of 5 — Property side runs lower per-hit weight so
    /// 4 lands at roughly the same selectivity in practice.</summary>
    public const int InterestingThreshold = 4;

    // ------------------------------------------------------------------
    // Keyword tables (per category). Whole-token match against the
    // property name via KeywordTokenizer — same lesson from build 609
    // applies: substring matching causes acronym collisions ("Component"
    // contains "MP") so token matching is the safe default.
    // ------------------------------------------------------------------

    public const int StatsKeywordScore = 5;
    public static readonly string[] StatsKeywords =
    {
        // Health / mana / stamina / energy
        "HP", "Hp", "Health", "MP", "Mana", "SP", "Stamina", "Energy",
        // Experience / level
        "XP", "Exp", "Experience", "Level", "Lv", "Lvl",
        // General resource bars / regeneration
        "Regen", "Regenerate", "Max",  // "Max" is per-token; only fires on
                                        // "MaxHealth"/"MaxStamina"/etc, not
                                        // engine words like "ChannelMaxIndex"
    };

    public const int CombatKeywordScore = 4;
    public static readonly string[] CombatKeywords =
    {
        // Damage / defense math
        "Damage", "Dmg", "Defense", "Defence", "Def", "Armor", "Armour",
        "Resistance", "Resist",
        // Crit tuning
        "Crit", "Critical", "CritRate", "CritDamage",
        // Attack / multiplier knobs
        "Attack", "Atk", "Multiplier",
    };

    public const int ResourcesKeywordScore = 4;
    public static readonly string[] ResourcesKeywords =
    {
        // Currency — both singular and plural since game property names
        // are commonly plural for scalar currency totals (e.g. "Coins"
        // as the running total). KeywordTokenizer doesn't stem so each
        // form needs an explicit entry.
        "Gold", "Coin", "Coins", "Money", "Currency", "Cash", "Credit", "Credits",
        // Gacha / premium
        "Gem", "Gems", "Diamond", "Diamonds", "Crystal", "Crystals",
        "Token", "Tokens",
        // Crafting / supplies
        "Ammo", "Material", "Materials", "Resource", "Resources", "Supply", "Supplies",
        // Inventory metadata
        "Stack", "Count", "Quantity", "Amount",
    };

    public const int MovementKeywordScore = 4;
    public static readonly string[] MovementKeywords =
    {
        "Speed", "Velocity", "Walk",
        "Sprint", "Run",  // "Run" safe here at token level — "RunCallback"
                          // is a function name pattern not a property name
        "Jump", "JumpHeight", "JumpZ",
        "Friction", "Acceleration", "Gravity",
        "Dash", "Climb", "Swim",
    };

    public const int UtilityKeywordScore = 3;
    public static readonly string[] UtilityKeywords =
    {
        "Save", "Load", "Checkpoint",
        "Quest", "Mission", "Objective",
        "Cheat", "Debug", "GodMode", "NoClip",
        "Score",
        // Boolean state flags common in BP-added overrides
        "IsImmortal", "CanBeDamaged", "Invincible", "Invulnerable",
    };

    /// <summary>Result of scoring one property.</summary>
    public readonly record struct ScoreResult(
        int FinalScore,
        PropertyCategory Category,
        int KeywordHits,
        int ClassBonus,
        bool IsUnusualLocation);

    /// <summary>
    /// Score one property entry. Mirrors <see cref="KeywordScoringTable.Score"/>
    /// in structure so a future audit of either side gets a consistent
    /// breakdown (keywordSum + classBonus = finalScore).
    /// </summary>
    public static ScoreResult Score(PropertySearchMatch match)
    {
        // Tokenise property name. Class name is NOT tokenised into the
        // keyword search domain on this side — that would let a property
        // called "Foo" on class "DamageComponent" pick up the "Damage"
        // keyword via the class side, which is misleading (the *property*
        // isn't about damage; the class is). Function side does fold both
        // because there a function "Get" on "PlayerHealthComponent" still
        // genuinely deals with health.
        var tokens = KeywordTokenizer.TokenizeAsSet(match.PropName);

        int statsHits     = CountTokenHits(tokens, StatsKeywords);
        int combatHits    = CountTokenHits(tokens, CombatKeywords);
        int resourcesHits = CountTokenHits(tokens, ResourcesKeywords);
        int movementHits  = CountTokenHits(tokens, MovementKeywords);
        int utilityHits   = CountTokenHits(tokens, UtilityKeywords);

        int statsScore     = statsHits     * StatsKeywordScore;
        int combatScore    = combatHits    * CombatKeywordScore;
        int resourcesScore = resourcesHits * ResourcesKeywordScore;
        int movementScore  = movementHits  * MovementKeywordScore;
        int utilityScore   = utilityHits   * UtilityKeywordScore;

        var category = PropertyCategory.Other;
        int catScore = 0;
        int totalKeywordHits =
            statsHits + combatHits + resourcesHits + movementHits + utilityHits;

        if (statsScore     > catScore) { catScore = statsScore;     category = PropertyCategory.Stats; }
        if (combatScore    > catScore) { catScore = combatScore;    category = PropertyCategory.Combat; }
        if (resourcesScore > catScore) { catScore = resourcesScore; category = PropertyCategory.Resources; }
        if (movementScore  > catScore) { catScore = movementScore;  category = PropertyCategory.Movement; }
        if (utilityScore   > catScore) { catScore = utilityScore;   category = PropertyCategory.Utility; }

        // Class-location bonus + Unusual flag. The Unusual classes
        // (LocalPlayer / HUD / GameViewportClient / ...) carry a +4 that
        // pushes interesting properties above the threshold even when
        // their keyword hits are lukewarm — surfacing the "wait, why is
        // there a Health field in GameViewportClient?" findings that
        // are the whole point of B'.
        var (classBonus, isUnusual) = ClassLocationScorer.PropertyBonus(match.ClassName);

        int keywordSum = statsScore + combatScore + resourcesScore +
                         movementScore + utilityScore;
        int finalScore = keywordSum + classBonus;

        return new ScoreResult(
            FinalScore:        finalScore,
            Category:          category,
            KeywordHits:       totalKeywordHits,
            ClassBonus:        classBonus,
            IsUnusualLocation: isUnusual);
    }

    /// <summary>Subset-match: keyword counts as a hit when ALL its
    /// tokens appear in <paramref name="tokens"/>. Same semantics as
    /// the Function-side scorer (KeywordScoringTable.CountTokenHits).</summary>
    private static int CountTokenHits(HashSet<string> tokens, string[] keywords)
    {
        int hits = 0;
        foreach (var k in keywords)
        {
            var keyTokens = KeywordTokenizer.Tokenize(k);
            if (keyTokens.Length == 0) continue;
            bool allPresent = true;
            foreach (var kt in keyTokens)
            {
                if (!tokens.Contains(kt)) { allPresent = false; break; }
            }
            if (allPresent) hits++;
        }
        return hits;
    }

    /// <summary>Display label for a property category.</summary>
    public static string DisplayName(PropertyCategory cat) => cat switch
    {
        PropertyCategory.Stats     => "Stats",
        PropertyCategory.Combat    => "Combat",
        PropertyCategory.Resources => "Resources",
        PropertyCategory.Movement  => "Movement",
        PropertyCategory.Utility   => "Utility",
        _                          => "Other",
    };

    /// <summary>Foreground hex colour for the chip in the DataGrid.</summary>
    public static string CategoryColor(PropertyCategory cat) => cat switch
    {
        PropertyCategory.Stats     => "#E07B7B",  // soft red — HP/MP
        PropertyCategory.Combat    => "#E0A050",  // orange — damage/def
        PropertyCategory.Resources => "#DCDCAA",  // gold — money/gem
        PropertyCategory.Movement  => "#7FB6E8",  // sky — speed/jump
        PropertyCategory.Utility   => "#B280D9",  // purple — quest/save
        _                          => "#808080",  // grey — other
    };

    /// <summary>
    /// One canned query per category, used by the Interesting Properties
    /// VM to batch-call <c>search_properties</c> across a wide keyword
    /// surface in round 1 (no new DLL command needed). Each entry is
    /// a representative keyword that the DLL substring-matches against
    /// every property name. The scorer then re-evaluates client-side
    /// using the full keyword tables above — so this list is "what to
    /// fetch", not "what to score on".
    /// </summary>
    public static readonly string[] SeedQueries =
    {
        // Stats
        "HP", "Health", "Mana", "Stamina", "Energy", "Level", "Experience",
        "Max",
        // Combat
        "Damage", "Defense", "Armor", "Crit", "Attack", "Multiplier",
        // Resources
        "Gold", "Coin", "Money", "Gem", "Ammo", "Stack", "Count",
        // Movement
        "Speed", "Jump", "Walk", "Sprint", "Friction", "Gravity",
        // Utility (state flags / quest)
        "Quest", "Cheat", "Immortal", "Damaged", "Invincible",
    };
}
