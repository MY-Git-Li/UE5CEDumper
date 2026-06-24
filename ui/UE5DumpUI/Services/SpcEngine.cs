using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure, AOT-safe SPC predicate evaluator. Given one candidate field's value
/// sequence across the selected snapshots (oldest → newest), decides whether it
/// satisfies the directional chain + the per-snapshot absolute predicates. Kept
/// separate from <see cref="SnapshotStore"/> (which does the in-memory intersection
/// load) so the predicate logic is unit-testable without a database. The in-memory
/// approach (port of the `discrete` sister project) replaced the old SQL N-way
/// self-join, which only stayed fast with perfect composite covering indexes.
/// </summary>
public static class SpcEngine
{
    /// <summary>
    /// Evaluate the chain over a value sequence. <paramref name="hex"/>/<paramref
    /// name="num"/> are the raw bytes / numeric value per snapshot (same length,
    /// oldest → newest). <paramref name="dir"/>[i] is snapshot i's directional
    /// predicate (index 0 = baseline, ignored). <paramref name="abs"/>[i] is the
    /// optional absolute predicate for snapshot i (null/short list = none). Unchanged
    /// / Changed compare raw hex (type-exact); Increased / Decreased compare numeric
    /// (per declared width). Absolute predicates compare numeric. All must hold.
    /// </summary>
    public static bool Matches(
        IReadOnlyList<string> hex,
        IReadOnlyList<double?> num,
        IReadOnlyList<SpcPredicateKind> dir,
        IReadOnlyList<SpcAbsolutePredicate>? abs,
        string declaredType = "")
    {
        int n = hex.Count;

        // Absolute (value-window) predicates: each snapshot independently.
        if (abs != null)
            for (int i = 0; i < n && i < abs.Count; i++)
                if (!AbsMatches(abs[i], num[i], declaredType)) return false;

        // Directional chain: snapshot i vs i-1. Index 0 is the baseline (Any).
        for (int i = 1; i < n; i++)
        {
            switch (dir[i])
            {
                case SpcPredicateKind.Unchanged:
                    if (!string.Equals(hex[i], hex[i - 1], StringComparison.Ordinal)) return false;
                    break;
                case SpcPredicateKind.Changed:
                    if (string.Equals(hex[i], hex[i - 1], StringComparison.Ordinal)) return false;
                    break;
                case SpcPredicateKind.Increased:
                    if (num[i] is not double hi || num[i - 1] is not double loPrev || !(hi > loPrev)) return false;
                    break;
                case SpcPredicateKind.Decreased:
                    if (num[i] is not double lo || num[i - 1] is not double hiPrev || !(lo < hiPrev)) return false;
                    break;
                case SpcPredicateKind.Any:
                default:
                    break;
            }
        }
        return true;
    }

    // Evaluate one absolute predicate with FLOAT-AWARE Exact: a whole-number target
    // matches any float that rounds to it (513 finds a 513.36 GAS BaseValue). The
    // other kinds (Between/AtLeast/AtMost/None) delegate to the model's own logic.
    // Lives here (Services) so the rounding reuses SnapshotNumeric without the Models
    // layer depending on Services. (build 1648)
    private static bool AbsMatches(SpcAbsolutePredicate p, double? value, string declaredType)
    {
        if (p.Kind == SpcAbsoluteKind.Exact)
            return value is double v && SnapshotNumeric.ExactMatch(v, p.Low, declaredType);
        return p.Matches(value);
    }
}
