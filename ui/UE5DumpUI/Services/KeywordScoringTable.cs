using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Categories surfaced by the Interesting Functions Finder. Picked
/// from a Cheat-Engine-user perspective: which UE function would I
/// hook / call to alter common gameplay state?
///
/// One function gets exactly one category -- the one with the highest
/// total keyword hit score (ties broken by table order: Stats &gt;
/// Inventory &gt; Movement &gt; Combat &gt; Utility &gt; Other). Keeps the
/// UI chip filter unambiguous; multi-tag UI can come later if needed.
/// </summary>
public enum FunctionCategory
{
    Other,      // Default -- no keyword hits, or score below threshold
    Stats,      // HP/MP/SP/XP/Level/Damage/Heal/Score
    Inventory,  // Gold/Money/Item/Loot/Equip
    Movement,   // Teleport/Walk/Speed/NoClip/Fly
    Combat,     // Attack/Fire/Cast/Ability
    Utility,    // Save/Load/Spawn/Timer/Cheat/Console
}

/// <summary>
/// Keyword + class + flag heuristic that ranks UFunctions by
/// "interesting-to-cheat-engine-user" probability. All scoring is
/// hardcoded here (no settings.json hooks yet) so a single static
/// review tells you exactly what shows up in the finder UI.
///
/// Add a keyword:
///   1. Pick the right category bucket below.
///   2. Append to its array (case doesn't matter -- comparison is
///      case-insensitive).
///   3. Add a unit test in KeywordScoringTableTests for the new
///      keyword (ensures coverage doesn't regress when bucket
///      arrays get reshuffled).
///
/// Categories use substring matching across the FUNCTION name and
/// the CLASS name (e.g. "AddMoney" hits Inventory via 'Money'; a
/// "Damage" function on PlayerCharacter hits Stats via 'Damage' AND
/// gets the Character class bonus).
/// </summary>
public static class KeywordScoringTable
{
    /// <summary>Score threshold: rows with FinalScore &lt; this are
    /// dropped from default view (visible only when "Show all" is
    /// toggled in the UI).</summary>
    public const int InterestingThreshold = 5;

    // ------------------------------------------------------------------
    // Keyword tables (per category). Substring matched against
    // funcName + className, case-insensitive.
    // ------------------------------------------------------------------

    /// <summary>Per-hit score for each Stats keyword.</summary>
    public const int StatsKeywordScore = 5;
    public static readonly string[] StatsKeywords =
    {
        // Health / mana / stamina / energy
        "HP", "Health", "Hp", "MP", "Mana", "SP", "Stamina", "Energy",
        // XP / level / score
        "XP", "Exp", "Experience", "Level", "Score",
        // Combat-stat verbs (still primarily affect a stat field)
        "Damage", "Heal", "Hurt", "Kill", "Revive", "Die", "Death",
    };

    public const int InventoryKeywordScore = 5;
    public static readonly string[] InventoryKeywords =
    {
        "Gold", "Money", "Coin", "Currency", "Cash", "Credit",
        "Item", "Inventory", "Pickup", "Loot", "Drop", "Equip",
        "Wallet", "Stack",
    };

    public const int MovementKeywordScore = 5;
    public static readonly string[] MovementKeywords =
    {
        "Teleport", "Warp", "TP",
        "SetLocation", "SetActorLocation", "Move",
        "Speed", "Velocity", "Walk", "Run", "Sprint", "Jump",
        "NoClip", "Fly", "God", "Ghost", "Invincible",
    };

    public const int CombatKeywordScore = 4;
    public static readonly string[] CombatKeywords =
    {
        "Attack", "Fire", "Shoot", "Cast",
        "Ability", "Skill", "Buff", "Debuff",
        "Reload", "Aim", "Block", "Parry",
    };

    public const int UtilityKeywordScore = 3;
    public static readonly string[] UtilityKeywords =
    {
        "Save", "Load", "Checkpoint",
        "Spawn", "Summon", "Create", "Destroy",
        "Timer", "Time", "Clock", "Countdown",
        "Cheat", "Debug", "Console", "Toggle",
    };

    // ------------------------------------------------------------------
    // Class-name boosts (substring matched against owning class name).
    // ------------------------------------------------------------------

    private static readonly (string Substring, int Bonus)[] ClassBonuses =
    {
        // Player-controlled / persistent state -- big boost (these are
        // where game-state-mutating functions usually live)
        ("Character",         3),
        ("Pawn",              3),
        ("PlayerController",  3),
        ("PlayerState",       3),
        // Game-level systems -- medium boost
        ("GameMode",          2),
        ("GameInstance",      2),
        ("SaveGame",          2),
        // Visual / audio / animation -- penalty (these have lots of
        // BC functions but rarely move cheat-relevant state)
        ("Anim",             -2),
        ("Niagara",          -2),
        ("Sound",            -2),
        ("Audio",            -2),
        ("UI",               -1),  // milder -- some HUD/inventory UI is relevant
        ("Widget",           -1),
        ("Particle",         -2),
    };

    // ------------------------------------------------------------------
    // FunctionFlag boosts. Read from AllFunctionEntry projections so
    // the helper stays decoupled from raw flag bits.
    // ------------------------------------------------------------------

    private const int BlueprintCallableBonus = 2;  // user-facing exposed surface
    private const int BlueprintEventBonus    = 1;  // BP-overridable event
    private const int SafeGetterBonus        = 1;  // pure/const + 0-1 params -> easy 1-click test
    private const int LargeParmsSizePenalty  = -1; // ParmsSize > 64 -> annoying to call
    private const int LargeParmsSizeThreshold= 64;

    /// <summary>Result of scoring one entry.</summary>
    public readonly record struct ScoreResult(
        int FinalScore,
        FunctionCategory Category,
        int KeywordHits,
        int ClassBonus,
        int FlagBonus);

    /// <summary>
    /// Score one function entry. Returns a tuple of (finalScore,
    /// category, breakdown) so the UI can show a tooltip explaining
    /// why a row is interesting.
    /// </summary>
    public static ScoreResult Score(AllFunctionEntry entry)
    {
        // Pre-lower the names ONCE -- substring matches are case-
        // insensitive and we run them through ~5-10 keywords per
        // category. Also, name comparison usually dominates per-row
        // scoring cost.
        var funcLower  = entry.FuncName.ToLowerInvariant();
        var classLower = entry.ClassName.ToLowerInvariant();

        // Keyword pass: tally hits per category, pick the winner.
        int statsHits     = CountHits(funcLower, classLower, StatsKeywords);
        int inventoryHits = CountHits(funcLower, classLower, InventoryKeywords);
        int movementHits  = CountHits(funcLower, classLower, MovementKeywords);
        int combatHits    = CountHits(funcLower, classLower, CombatKeywords);
        int utilityHits   = CountHits(funcLower, classLower, UtilityKeywords);

        int statsScore     = statsHits     * StatsKeywordScore;
        int inventoryScore = inventoryHits * InventoryKeywordScore;
        int movementScore  = movementHits  * MovementKeywordScore;
        int combatScore    = combatHits    * CombatKeywordScore;
        int utilityScore   = utilityHits   * UtilityKeywordScore;

        // Pick highest-scoring category; ties broken by enum order
        // (Stats > Inventory > Movement > Combat > Utility).
        var category = FunctionCategory.Other;
        int catScore = 0;
        int totalKeywordHits =
            statsHits + inventoryHits + movementHits + combatHits + utilityHits;

        if (statsScore > catScore)     { catScore = statsScore;     category = FunctionCategory.Stats; }
        if (inventoryScore > catScore) { catScore = inventoryScore; category = FunctionCategory.Inventory; }
        if (movementScore > catScore)  { catScore = movementScore;  category = FunctionCategory.Movement; }
        if (combatScore > catScore)    { catScore = combatScore;    category = FunctionCategory.Combat; }
        if (utilityScore > catScore)   { catScore = utilityScore;   category = FunctionCategory.Utility; }

        // Class bonus: sum all matching substring bonuses (so something
        // like "AnimCharacter" gets Anim penalty + Character bonus
        // = +1 net, accurately reflecting that it's likely an
        // animation event on a character).
        int classBonus = 0;
        foreach (var (sub, bonus) in ClassBonuses)
        {
            if (classLower.Contains(sub.ToLowerInvariant()))
                classBonus += bonus;
        }

        // Flag bonus
        int flagBonus = 0;
        if (entry.IsBlueprintCallable) flagBonus += BlueprintCallableBonus;
        if (entry.IsBlueprintEvent)    flagBonus += BlueprintEventBonus;
        if ((entry.IsBlueprintPure || entry.IsConst) && entry.NumParms <= 1)
            flagBonus += SafeGetterBonus;
        if (entry.ParmsSize > LargeParmsSizeThreshold)
            flagBonus += LargeParmsSizePenalty;

        // Sum across keyword + class + flag layers. All-keyword-hits-
        // sum (vs single-category picked score) means a function that
        // matches BOTH Stats AND Inventory gets credit for both even
        // though only one category label shows.
        int keywordSum = statsScore + inventoryScore + movementScore +
                         combatScore + utilityScore;
        int finalScore = keywordSum + classBonus + flagBonus;

        return new ScoreResult(
            FinalScore:    finalScore,
            Category:      category,
            KeywordHits:   totalKeywordHits,
            ClassBonus:    classBonus,
            FlagBonus:     flagBonus);
    }

    /// <summary>
    /// Count distinct keyword substrings present in the function name
    /// OR class name. Each keyword counts once even if it appears in
    /// both names (avoids double-credit for e.g. "Health" in both
    /// "GetHealth" and "PlayerHealthComponent").
    /// </summary>
    private static int CountHits(string funcLower, string classLower, string[] keywords)
    {
        int hits = 0;
        foreach (var k in keywords)
        {
            var kLower = k.ToLowerInvariant();
            if (funcLower.Contains(kLower) || classLower.Contains(kLower))
                hits++;
        }
        return hits;
    }

    /// <summary>Display label for a category (shown in the chip filter
    /// dropdown + as a column value).</summary>
    public static string DisplayName(FunctionCategory cat) => cat switch
    {
        FunctionCategory.Stats     => "Stats",
        FunctionCategory.Inventory => "Inventory",
        FunctionCategory.Movement  => "Movement",
        FunctionCategory.Combat    => "Combat",
        FunctionCategory.Utility   => "Utility",
        _                          => "Other",
    };

    /// <summary>Foreground hex colour for the category chip in the
    /// DataGrid -- matches the existing UI palette (DCDCAA / 4EC9B0
    /// / 569CD6 / etc.).</summary>
    public static string CategoryColor(FunctionCategory cat) => cat switch
    {
        FunctionCategory.Stats     => "#E07B7B", // soft red -- combat/HP
        FunctionCategory.Inventory => "#DCDCAA", // gold -- money/loot
        FunctionCategory.Movement  => "#7FB6E8", // sky -- teleport/move
        FunctionCategory.Combat    => "#E0A050", // orange -- attack/skill
        FunctionCategory.Utility   => "#B280D9", // purple -- save/spawn
        _                          => "#808080", // grey -- other
    };
}
