namespace UE5DumpUI.Models;

/// <summary>
/// Result of a <c>set_packed_consts</c> calibration call for the UE5.7+ *** UNVERIFIED ***
/// packed FUObjectItem layout. Echoes the resulting layout state plus a handful of
/// reconstructed object samples so an operator can eyeball-calibrate the constants
/// (tweak alignBits / ptrMaskBits until the sample names look like real UObjects).
/// </summary>
public sealed class PackedConstsResult
{
    public string ItemLayoutMode { get; init; } = "classic";
    public bool ItemPacked { get; init; }
    public int ItemObjOffset { get; init; }
    public int ItemSize { get; init; }

    /// <summary>Reconstructed GObjects[0..7] under the current packed constants.</summary>
    public PackedSample[] Samples { get; init; } = [];
}

/// <summary>One reconstructed object sample echoed by <c>set_packed_consts</c>.</summary>
public sealed class PackedSample
{
    public int Index { get; init; }
    public string Addr { get; init; } = "";
    public string Name { get; init; } = "";
}
