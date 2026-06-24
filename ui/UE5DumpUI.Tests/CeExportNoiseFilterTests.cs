using UE5DumpUI.Helpers;
using Xunit;

namespace UE5DumpUI.Tests;

public class CeExportNoiseFilterTests
{
    [Theory]
    // Engine asset leaves → noise.
    [InlineData("Widget")]
    [InlineData("UserWidget")]
    [InlineData("SoundBase")]
    [InlineData("SoundCue")]
    [InlineData("Texture2D")]
    [InlineData("MaterialInstanceDynamic")]
    [InlineData("ParticleSystem")]
    [InlineData("NiagaraSystem")]
    [InlineData("AnimInstance")]
    public void IsSystemComponent_EngineAssetLeaf_IsNoise(string cls)
        => Assert.True(CeExportNoiseFilter.IsSystemComponent(cls));

    [Theory]
    // Gameplay keep-bases → never noise (guardrail wins).
    [InlineData("Actor")]
    [InlineData("ActorComponent")]
    [InlineData("Pawn")]
    [InlineData("Character")]
    [InlineData("Controller")]
    [InlineData("PlayerState")]
    [InlineData("GameInstance")]
    public void IsSystemComponent_GameplayKeepBase_IsKept(string cls)
        => Assert.False(CeExportNoiseFilter.IsSystemComponent(cls));

    [Theory]
    // Game classes that merely CONTAIN a ban token must NOT be excluded (no substring match).
    [InlineData("WidgetSpawnerComponent")]
    [InlineData("BP_Player_C")]
    [InlineData("LSAbilitySystemComponent")]
    [InlineData("TextureStreamingManager")]   // starts with "Texture" but is not the exact leaf
    [InlineData("NiagaraComponent")]          // an ActorComponent, deliberately not in the ban set
    public void IsSystemComponent_GameClassNamesake_IsKept(string cls)
        => Assert.False(CeExportNoiseFilter.IsSystemComponent(cls));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSystemComponent_EmptyOrNull_IsKept(string? cls)
        => Assert.False(CeExportNoiseFilter.IsSystemComponent(cls));

    [Theory]
    // A package-qualified name is stripped to its leaf before matching.
    [InlineData("/Script/Engine.SoundBase", true)]
    [InlineData("/Script/UMG.UserWidget", true)]
    [InlineData("/Script/Engine.Actor", false)]      // keep-base wins even when qualified
    [InlineData("/Game/Blueprints/BP_Player.BP_Player_C", false)]
    public void IsSystemComponent_PackageQualified_MatchesLeaf(string cls, bool expected)
        => Assert.Equal(expected, CeExportNoiseFilter.IsSystemComponent(cls));
}
