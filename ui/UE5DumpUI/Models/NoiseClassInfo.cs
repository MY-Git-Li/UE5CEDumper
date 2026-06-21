namespace UE5DumpUI.Models;

/// <summary>
/// One class's verdict from the opt-in "auto-detect system classes" classifier
/// (<c>detect_noise_classes</c>). The DLL marks a class <see cref="IsNoise"/>
/// only by safe-by-construction rules — an engine package (/Script/Engine, /UMG,
/// /Slate, …) or a super-chain that reaches a pure-engine leaf base
/// (Widget / SoundBase / Texture / MaterialInterface / ParticleSystem /
/// NiagaraSystem / AnimInstance). It never uses class-name substrings and never
/// flags ActorComponent descendants. <see cref="Reason"/> is the matched rule
/// label (empty when not noise), shown next to the pre-ticked picker row.
/// </summary>
public sealed class NoiseClassInfo
{
    public string ClassName { get; set; } = "";
    public bool   IsNoise   { get; set; }
    public string Reason    { get; set; } = "";
}
