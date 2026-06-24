namespace UE5DumpUI.Helpers;

/// <summary>
/// Client-side, name-only port of the DLL snapshot noise rule (Aura.h
/// <c>DecideSnapshotNoise</c>), adapted for CE export. The UClass super-chain is
/// NOT available at export time — the exporter only has already-walked field
/// metadata — so this is a leaf-NAME heuristic, not a derivation walk: a pointer
/// field's pointee class is treated as system/engine noise only when its
/// (package-stripped) leaf name is a known engine asset/data class. A gameplay
/// keep-base name always wins (guardrail), and substrings are never used (so a
/// game class like <c>WidgetSpawnerComponent</c> is kept). Deliberately
/// conservative — under-exclude rather than risk dropping a game class we can't
/// positively identify as engine noise.
///
/// Mirrors the DLL's <c>SnapshotEngineNoiseBases</c> / <c>SnapshotGameplayKeepBases</c>
/// intent: drop references to asset/data objects (textures, sounds, materials,
/// widgets, particles), keep anything component- or gameplay-shaped. Only ever
/// applied to RECURSIVELY-expanded children in CE export, never to the top-level
/// fields the user explicitly selected (see CeXmlExportService.EmitFields).
/// </summary>
internal static class CeExportNoiseFilter
{
    // Gameplay bases — never excluded. Checked first so the guardrail wins over the
    // ban set (matches DecideSnapshotNoise's keep-base precedence). Components are
    // kept via ActorComponent so engine components attached to gameplay actors
    // (e.g. a NiagaraComponent on a Character) stay drillable.
    private static readonly HashSet<string> KeepBases = new(StringComparer.Ordinal)
    {
        "Actor", "ActorComponent", "Pawn", "Character",
        "Controller", "PlayerState", "GameInstance",
    };

    // Engine/system ASSET/DATA leaf classes whose contents a CE user rarely watches.
    // Exact leaf-name match only — no substrings, no super-chain. All are UObject
    // asset/data types (NOT ActorComponents), so banning them never contradicts the
    // ActorComponent keep-base above.
    private static readonly HashSet<string> NoiseClasses = new(StringComparer.Ordinal)
    {
        // UMG / Slate widgets
        "Widget", "UserWidget",
        // Audio assets
        "SoundBase", "SoundCue", "SoundWave",
        // Textures
        "Texture", "Texture2D", "TextureCube", "TextureRenderTarget2D",
        // Materials
        "MaterialInterface", "Material", "MaterialInstanceDynamic", "MaterialInstanceConstant",
        // FX assets
        "ParticleSystem", "NiagaraSystem",
        // Animation / fonts
        "AnimInstance", "Font",
    };

    /// <summary>
    /// True when <paramref name="className"/> is a known engine/system asset class
    /// that should be skipped during recursive CE export. Empty / null → false (keep).
    /// </summary>
    internal static bool IsSystemComponent(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        var leaf = LeafName(className);
        if (KeepBases.Contains(leaf)) return false;   // guardrail wins
        return NoiseClasses.Contains(leaf);
    }

    // Strip any package path ("/Script/Engine.SoundBase" → "SoundBase") so the match
    // works whether the exporter hands us a bare leaf name or a qualified one.
    private static string LeafName(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
    }
}
