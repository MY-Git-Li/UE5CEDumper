using System.Collections.Generic;
using UE5DumpUI.Services;

namespace UE5DumpUI.Models;

/// <summary>
/// One auto-detected candidate player-stat field, produced by the experimental
/// "Detect Player Stats" panel (P4). Wraps a scored <see cref="PropertySearchMatch"/>
/// with the live confirmation signals gathered at detect time (a live instance
/// exists, the value is plausible, has a Max sibling, is a GAS attribute, and —
/// opt-in — decreased across two snapshots) plus a combined confidence.
///
/// LOW-ACCURACY heuristic by design — the whole panel carries a "reference only"
/// disclaimer. These rows are a shortlist to verify, never a guarantee.
/// </summary>
public sealed class DetectedStat
{
    public required PropertySearchMatch Match { get; init; }
    public required PropertyCategory Category { get; init; }
    public required int BaseScore { get; init; }     // scorer FinalScore (+ pair)
    public required int Confidence { get; init; }     // base + confirmation boosts
    public required bool IsConfirmed { get; init; }

    // Confirmation signals
    public bool LiveInstanceExists { get; init; }
    public bool ValuePlausible { get; init; }
    public bool HasMaxSibling { get; init; }
    public bool IsGasAttribute { get; init; }
    public bool SnapshotDecreased { get; init; }

    /// <summary>Live typed value read from a representative instance ("" if none).</summary>
    public string LiveValue { get; init; } = "";

    // Forwarded for the DataGrid + cross-tab handoffs
    public string ClassName => Match.ClassName;
    public string DefiningClassName => Match.DefiningClassName;
    public string PropName => Match.PropName;
    public string PropType => Match.PropType;
    public int PropOffset => Match.PropOffset;
    public string OffsetHex => Match.OffsetHex;

    public string CategoryLabel => PropertyScoringTable.DisplayName(Category);
    public string CategoryColor => PropertyScoringTable.CategoryColor(Category);

    /// <summary>Compact confirmed/guess badge for the first column.</summary>
    public string ConfirmBadge => IsConfirmed ? "✓ confirmed" : "· guess";

    /// <summary>Foreground colour for the badge — green when confirmed, grey when a guess.</summary>
    public string ConfirmColor => IsConfirmed ? "#6A9955" : "#808080";

    /// <summary>Human-readable roll-up of the signals that fired, for the grid + tooltip.</summary>
    public string SignalSummary
    {
        get
        {
            var parts = new List<string>();
            if (LiveInstanceExists) parts.Add("live");
            if (ValuePlausible && LiveValue.Length > 0) parts.Add($"={LiveValue}");
            else if (LiveValue.Length > 0) parts.Add(LiveValue);
            if (HasMaxSibling) parts.Add("has Max");
            if (IsGasAttribute) parts.Add("GAS");
            if (SnapshotDecreased) parts.Add("▼ on event");
            return string.Join("  ·  ", parts);
        }
    }
}
