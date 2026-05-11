using UE5DumpUI.Services;

namespace UE5DumpUI.Models;

/// <summary>
/// A pipe-supplied <see cref="AllFunctionEntry"/> wrapped with the
/// scorer output for display in the Interesting Functions Finder
/// DataGrid. Kept separate from <see cref="AllFunctionEntry"/> so the
/// raw pipe model stays a pure data carrier; only the UI layer attaches
/// scoring metadata.
/// </summary>
public sealed class ScoredFunctionRow
{
    public required AllFunctionEntry Entry        { get; init; }
    public required int              FinalScore   { get; init; }
    public required FunctionCategory Category     { get; init; }
    public required int              KeywordHits  { get; init; }
    public required int              ClassBonus   { get; init; }
    public required int              FlagBonus    { get; init; }

    // ------------------------------------------------------------------
    // Display helpers (DataGrid bindings)
    // ------------------------------------------------------------------

    public string CategoryLabel => KeywordScoringTable.DisplayName(Category);
    public string CategoryColor => KeywordScoringTable.CategoryColor(Category);

    /// <summary>
    /// Tooltip for the score column showing the breakdown -- helps the
    /// user understand why a row got the score it did, and surfaces
    /// hidden multi-category matches.
    /// </summary>
    public string ScoreTooltip
        => $"FinalScore={FinalScore} = keywords({KeywordHits} hits) " +
           $"+ classBonus={ClassBonus} + flagBonus={FlagBonus}";

    // Forwarded entry properties (so XAML bindings don't have to walk
    // through .Entry.Property -- keeps the AXAML readable).
    public string ClassName    => Entry.ClassName;
    public string ClassAddr    => Entry.ClassAddr;
    public string SuperName    => Entry.SuperName;
    public string ClassPath    => Entry.ClassPath;
    public string FuncName     => Entry.FuncName;
    public string FuncAddr     => Entry.FuncAddr;
    public string ShortFlags   => Entry.ShortFlags;
    public string ParamsLabel  => Entry.ParamsLabel;
    public uint   FunctionFlags=> Entry.FunctionFlags;
    public byte   NumParms     => Entry.NumParms;
    public ushort ParmsSize    => Entry.ParmsSize;
}
