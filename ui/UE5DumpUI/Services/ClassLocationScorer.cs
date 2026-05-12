namespace UE5DumpUI.Services;

/// <summary>
/// Shared class-location heuristic used by both the Interesting Functions
/// scorer and the Interesting Properties scorer.
///
/// Different "lens" depending on what's being scored:
///   - <see cref="FunctionBonus"/> — relevance of a UFunction's owning
///     class (Character / Pawn / ... = good; Anim / Niagara = noise).
///   - <see cref="PropertyBonus"/> — relevance of a UProperty's owning
///     class, with the NEW concept of "unusual location": HP / Damage /
///     Stamina fields that sit in <c>LocalPlayer</c> /
///     <c>GameViewportClient</c> / <c>HUD</c> instead of where Unreal
///     convention would put them. Those hits get a HIGHER bonus AND a
///     boolean flag so the UI can stamp a ⚠ "Unusual Location" badge.
///
/// Substring match (case-insensitive) against the owning class name —
/// same semantics the function-side scorer has had since build 597 (so
/// "AnimCharacter" picks up both the Anim penalty AND the Character
/// bonus, accurately reflecting that it's an animation event on a
/// character).
/// </summary>
public static class ClassLocationScorer
{
    // ------------------------------------------------------------------
    // Function-side bonuses (verbatim from KeywordScoringTable build 607
    // when ClassBonuses lived inside that file -- moved here so Property
    // side can share the bag-of-substrings pattern + tests cover both
    // call sites).
    // ------------------------------------------------------------------

    private static readonly (string Substring, int Bonus)[] FunctionBonuses =
    {
        // Player-controlled / persistent state -- big boost
        ("Character",         3),
        ("Pawn",              3),
        ("PlayerController",  3),
        ("PlayerState",       3),
        // Generic "Player" — catches game-named classes like BP_Player_C,
        // AnimMan_Player_C, MyPlayerStats, etc. that contain "Player" but
        // not the more specific tokens above. Mild bonus (+2) since some
        // false-positives (PlayerCameraManager) are still tolerable.
        ("Player",            2),
        // Game-level systems
        ("GameMode",          2),
        ("GameInstance",      2),
        ("SaveGame",          2),
        // Visual / audio / animation -- penalty. SURGICAL compound names
        // only: a bare "Anim" substring penalty false-positives on
        // legitimate game-class prefixes like "AnimMan_Player_C" (a
        // game's player character in TowerOfMask). The anim framework
        // itself uses very specific compound class names; we list those
        // explicitly so game classes that happen to begin with "Anim"
        // are not punished.
        ("AnimInstance",     -2),
        ("AnimMontage",      -2),
        ("AnimSequence",     -2),
        ("AnimNotify",       -2),
        ("AnimGraph",        -2),
        ("AnimBlueprint",    -2),
        ("AnimDataController", -2),
        ("NiagaraSystem",    -2),
        ("NiagaraEmitter",   -2),
        ("NiagaraComponent", -2),
        ("SoundCue",         -2),
        ("SoundWave",        -2),
        ("SoundBase",        -2),
        ("AudioComponent",   -2),
        ("ParticleSystem",   -2),
        ("ParticleEmitter",  -2),
        ("UserWidget",       -1),
        ("WidgetComponent",  -1),
    };

    /// <summary>
    /// Sum bonuses for every substring that appears in the class name.
    /// Multiple hits stack (AnimCharacter = +Character +Anim = +1 net).
    /// </summary>
    public static int FunctionBonus(string className)
    {
        if (string.IsNullOrEmpty(className)) return 0;
        var lower = className.ToLowerInvariant();
        int total = 0;
        foreach (var (sub, bonus) in FunctionBonuses)
        {
            if (lower.Contains(sub.ToLowerInvariant()))
                total += bonus;
        }
        return total;
    }

    // ------------------------------------------------------------------
    // Property-side bonuses (NEW for B' / Interesting Properties).
    //
    // Key conceptual addition: an "Unusual" tier separated from the
    // regular bonus tier. Classes in the Unusual tier ARE valid places
    // to find gameplay state -- just not the FIRST place a cheat-engine
    // user would think to look. Surfacing them with a ⚠ badge is the
    // whole point of B'.
    // ------------------------------------------------------------------

    /// <summary>One row of the property-side class-location table.
    /// <see cref="Bonus"/> is added to the property's keyword score.
    /// <see cref="IsUnusual"/> drives the ⚠ Unusual Location badge.</summary>
    public readonly record struct PropertyClassRule(string Substring, int Bonus, bool IsUnusual);

    private static readonly PropertyClassRule[] PropertyRules =
    {
        // === Expected locations (gameplay state lives here normally) ===
        new("Character",        3, false),
        new("Pawn",             3, false),
        new("PlayerController", 3, false),
        new("PlayerState",      3, false),
        new("AbilitySystem",    3, false),
        new("AttributeSet",     3, false),
        new("Inventory",        3, false),
        new("Equipment",        3, false),
        // Generic "Player" catches game-named classes like BP_Player_C,
        // AnimMan_Player_C, MyPlayerStats, etc. that contain "Player" but
        // not the more specific tokens above. Mild bonus (+2).
        new("Player",           2, false),
        // Game-level
        new("GameMode",         2, false),
        new("GameInstance",     2, false),
        new("SaveGame",         2, false),
        new("PlayerProfile",    2, false),

        // === UNUSUAL locations (highest-value hits — devs broke convention) ===
        // LocalPlayer / GameViewportClient / HUD often store gameplay state
        // they "shouldn't" because Unreal's recommended pattern would put it
        // in PlayerState or a component. When a cheat-relevant property
        // lands here it's almost always game-specific and overlooked by
        // people grepping conventional containers.
        new("LocalPlayer",          4, true),
        new("GameViewportClient",   4, true),
        new("HUD",                  4, true),
        new("UCheatManager",        4, true),
        // Bare "Cheat" without UCheatManager prefix catches game-specific
        // CheatManager subclasses (e.g. MyGameCheatMgr).
        new("CheatManager",         4, true),

        // No anim/audio/FX penalties on the Property side. Rationale:
        // property NAMES alone already filter most noise (a property
        // called "PlaybackSpeed" on UAudioComponent doesn't match any
        // Stats / Combat / Resources keyword, so it naturally scores 0).
        // The substring "Anim" / "Sound" / "Particle" penalty bit us hard
        // in TowerOfMask: the game's player character class is named
        // "AnimMan_Player_C" — a perfectly cheat-relevant target whose
        // Health field was being filtered out because of the Anim penalty.
        // The Function side keeps refined COMPOUND-name penalties for the
        // function-flavoured noise (which would otherwise dominate);
        // Property side doesn't have that problem and ships penalty-free.
    };

    /// <summary>
    /// Returns (bonus, isUnusual). Sum all matching substring bonuses
    /// (same stacking semantics as <see cref="FunctionBonus"/>);
    /// <c>isUnusual = true</c> if ANY matched rule is flagged Unusual.
    /// Last-one-wins for the bool — but in practice the table is
    /// curated so no class hits more than one Unusual rule.
    /// </summary>
    public static (int Bonus, bool IsUnusual) PropertyBonus(string className)
    {
        if (string.IsNullOrEmpty(className)) return (0, false);
        var lower = className.ToLowerInvariant();
        int total = 0;
        bool unusual = false;
        foreach (var rule in PropertyRules)
        {
            if (lower.Contains(rule.Substring.ToLowerInvariant()))
            {
                total += rule.Bonus;
                if (rule.IsUnusual) unusual = true;
            }
        }
        return (total, unusual);
    }
}
