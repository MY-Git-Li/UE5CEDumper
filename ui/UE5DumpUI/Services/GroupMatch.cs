using System;
using System.Collections.Generic;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure C# port of the DLL's <c>Orden</c> SDR / assignment matcher (header
/// <c>dll/src/Orden.h</c>), for **Snapshot Group Match** — finding objects in the
/// captured-snapshot corpus that hold ALL of N values at DISTINCT numeric offsets,
/// in any order (a System of Distinct Representatives). The source-agnostic seam the
/// group-value-scan spec reserved (§3.1): the matcher is fed <see cref="Leaf"/>s and
/// knows nothing about live memory, GObjects, or the SQLite store.
///
/// Snapshot semantics (vs the live DLL matcher): a leaf carries the per-snapshot
/// value SEQUENCE — the decoded numeric (<see cref="Leaf.Num"/>) for magnitude /
/// direction predicates and the raw hex (<see cref="Leaf.Hex"/>) for exact /
/// changed / unchanged byte compares — mirroring how <see cref="SpcEngine"/> splits
/// numeric vs hex. Mode A (single snapshot) → sequences of length 1, absolute
/// predicates only. Mode B (≥2 snapshots) → relative predicates compare first-vs-last.
///
/// Pure + AOT-safe: static methods, no reflection, no dynamic dispatch.
/// See docs/snapshot-group-match-spec.md.
/// </summary>
public static class GroupMatch
{
    /// <summary>Width scope for a slot — which leaf widths are eligible. Mirrors the
    /// live group's <c>NumericNoByte</c> / <c>NumericAll</c> meta types.</summary>
    public enum Scope
    {
        /// <summary>Numeric fields excluding 1-byte (Int8 / Byte) — the default
        /// (small values match a huge number of 1-byte fields).</summary>
        NumericNoByte,
        /// <summary>All numeric fields including 1-byte.</summary>
        NumericAll,
    }

    /// <summary>Per-slot predicate. Absolute predicates compare the leaf's value in
    /// the NEWEST snapshot against a target; relative predicates compare the leaf's
    /// value across the snapshot sequence (first-vs-last).</summary>
    public enum Predicate
    {
        // Absolute (need a target; evaluated on the newest snapshot).
        Exact,
        Bigger,
        Smaller,
        Between,
        // Relative (no target; compare first-vs-last across the sequence — needs ≥2).
        Changed,
        Unchanged,
        Increased,
        Decreased,
    }

    /// <summary>One numeric leaf of a captured object: its offset + declared property
    /// type + the per-snapshot value sequence. <see cref="Hex"/> and <see cref="Num"/>
    /// are index-aligned across the selected snapshots (oldest→newest). <see cref="Tag"/>
    /// is an opaque caller handle (echoed back via the matched leaf index) so the
    /// engine can rebuild display metadata without a second lookup; the matcher never
    /// interprets it.</summary>
    public sealed class Leaf
    {
        public int Offset;
        public string DeclaredType = "";
        public IReadOnlyList<string> Hex = Array.Empty<string>();
        public IReadOnlyList<double?> Num = Array.Empty<double?>();
        public int Tag;
    }

    /// <summary>One slot's predicate + (for absolute predicates) target value(s).</summary>
    public sealed class Slot
    {
        public Scope Scope = Scope.NumericNoByte;
        public Predicate Predicate = Predicate.Exact;
        public double? Target;     // absolute predicates
        public double? Target2;    // Between upper bound
        public double Tolerance;   // float +/- band (0 = exact)
    }

    // ---- width / type helpers (mirror SnapshotNumeric's declared-type set) ----

    private static int WidthBytes(string t) => t switch
    {
        "Int8Property" or "ByteProperty" => 1,
        "Int16Property" or "UInt16Property" => 2,
        "IntProperty" or "UInt32Property" or "FloatProperty" => 4,
        "Int64Property" or "UInt64Property" or "DoubleProperty" => 8,
        _ => 0, // non-numeric — never a group leaf
    };

    private static bool IsOneByte(string t) => t is "Int8Property" or "ByteProperty";
    private static bool IsFloat(string t) => t is "FloatProperty" or "DoubleProperty";
    private static bool IsUnsigned(string t) =>
        t is "ByteProperty" or "UInt16Property" or "UInt32Property" or "UInt64Property";

    /// <summary>Is <paramref name="t"/> a numeric field eligible under <paramref name="scope"/>?</summary>
    private static bool TypeInScope(string t, Scope scope)
    {
        if (WidthBytes(t) == 0) return false;
        if (scope == Scope.NumericNoByte && IsOneByte(t)) return false;
        return true;
    }

    /// <summary>Does an absolute target value fit the leaf's declared width? Mirrors the
    /// live matcher's <c>NumericTargetSet.Find(width) != null</c> gate — an integer leaf
    /// rejects a non-integral or out-of-range target; a float leaf accepts any finite
    /// double. This is what stops "70000" from matching an Int16 field.</summary>
    private static bool TargetFitsWidth(double target, string t)
    {
        if (!double.IsFinite(target)) return false;
        if (IsFloat(t)) return true; // Float/Double hold any finite value (lenient on precision)

        // Integer types: target must be integral and within range.
        if (Math.Floor(target) != target) return false;
        int w = WidthBytes(t);
        bool uns = IsUnsigned(t);
        // Range per width/signedness.
        double min, max;
        switch (w)
        {
            case 1: (min, max) = uns ? (0, 255) : (-128, 127); break;
            case 2: (min, max) = uns ? (0, 65535) : (-32768, 32767); break;
            case 4: (min, max) = uns ? (0, 4294967295d) : (-2147483648d, 2147483647d); break;
            // 8-byte: clamp to the exactly-representable double range; values beyond
            // 2^53 lose precision (same caveat SPC's numeric_value carries). Accept the
            // full 64-bit range conceptually — direction/magnitude stay correct.
            case 8: (min, max) = uns ? (0, 18446744073709551615d) : (-9223372036854775808d, 9223372036854775807d); break;
            default: return false;
        }
        return target >= min && target <= max;
    }

    /// <summary>True when <paramref name="leaf"/> satisfies <paramref name="slot"/>.
    /// Absolute predicates evaluate the NEWEST snapshot value (last index); relative
    /// predicates compare first-vs-last and require ≥2 snapshots (a single snapshot has
    /// no baseline, mirroring the live first-scan rejecting prev-value predicates).</summary>
    public static bool LeafSatisfiesSlot(Leaf leaf, Slot slot)
    {
        if (leaf == null || slot == null) return false;
        if (!TypeInScope(leaf.DeclaredType, slot.Scope)) return false;

        switch (slot.Predicate)
        {
            case Predicate.Exact:
            case Predicate.Bigger:
            case Predicate.Smaller:
            case Predicate.Between:
            {
                if (slot.Target is not double target) return false;
                if (!TargetFitsWidth(target, leaf.DeclaredType)) return false;
                if (leaf.Num.Count == 0) return false;
                if (leaf.Num[leaf.Num.Count - 1] is not double v) return false; // NaN/null in newest
                return slot.Predicate switch
                {
                    Predicate.Exact => Math.Abs(v - target) <= slot.Tolerance,
                    Predicate.Bigger => v > target,
                    Predicate.Smaller => v < target,
                    Predicate.Between => slot.Target2 is double hi
                        && TargetFitsWidth(hi, leaf.DeclaredType)
                        && v >= Math.Min(target, hi) && v <= Math.Max(target, hi),
                    _ => false,
                };
            }

            case Predicate.Changed:
            case Predicate.Unchanged:
            {
                if (leaf.Hex.Count < 2) return false; // no baseline (Mode A)
                string a = leaf.Hex[0] ?? "";
                string b = leaf.Hex[leaf.Hex.Count - 1] ?? "";
                bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                return slot.Predicate == Predicate.Unchanged ? same : !same;
            }

            case Predicate.Increased:
            case Predicate.Decreased:
            {
                if (leaf.Num.Count < 2) return false;
                if (leaf.Num[0] is not double first) return false;
                if (leaf.Num[leaf.Num.Count - 1] is not double last) return false;
                return slot.Predicate == Predicate.Increased ? last > first : last < first;
            }

            default:
                return false;
        }
    }

    // ---- SDR / Kuhn's augmenting-path matching (port of Orden::HasDistinctAssignment) ----

    private static bool TryAssign(int slot, List<int>[] m, int[] leafToSlot, bool[] seen)
    {
        foreach (int leaf in m[slot])
        {
            if (seen[leaf]) continue;
            seen[leaf] = true;
            if (leafToSlot[leaf] == -1 || TryAssign(leafToSlot[leaf], m, leafToSlot, seen))
            {
                leafToSlot[leaf] = slot;
                return true;
            }
        }
        return false;
    }

    /// <summary>True when every slot can be assigned a DISTINCT leaf (an SDR exists).
    /// Handles the duplicate-value case (two slots both wanting "24" need two different
    /// leaves) naturally via the matching. O(slots × edges) — trivial for 2..4 slots.</summary>
    public static bool HasDistinctAssignment(List<int>[] perSlot, int nLeaves)
    {
        if (nLeaves < 0) return false;
        var leafToSlot = new int[nLeaves];
        for (int i = 0; i < nLeaves; i++) leafToSlot[i] = -1;
        for (int s = 0; s < perSlot.Length; s++)
        {
            if (perSlot[s].Count == 0) return false;
            var seen = new bool[nLeaves];
            if (!TryAssign(s, perSlot, leafToSlot, seen)) return false;
        }
        return true;
    }

    /// <summary>Build each slot's matching-leaf list (capped at <paramref name="perSlotCap"/>)
    /// and report whether a distinct simultaneous assignment exists. <paramref name="perSlot"/>
    /// is filled even on a false return (the convergence lists the caller persists), but a
    /// false return means the object is NOT a group candidate. Returns false for &lt; 2 slots
    /// (a group needs ≥ 2 values; the caller enforces the 2..4 bound).</summary>
    public static bool Run(IReadOnlyList<Leaf> leaves, IReadOnlyList<Slot> slots,
                           out List<int>[] perSlot, int perSlotCap = 8)
    {
        perSlot = new List<int>[slots.Count];
        for (int s = 0; s < slots.Count; s++) perSlot[s] = new List<int>();
        if (slots.Count < 2) return false;

        for (int s = 0; s < slots.Count; s++)
        {
            for (int li = 0; li < leaves.Count; li++)
            {
                if (LeafSatisfiesSlot(leaves[li], slots[s]))
                {
                    if (perSlot[s].Count < perSlotCap)
                        perSlot[s].Add(li);
                }
            }
            if (perSlot[s].Count == 0) return false; // early reject
        }
        return HasDistinctAssignment(perSlot, leaves.Count);
    }
}
