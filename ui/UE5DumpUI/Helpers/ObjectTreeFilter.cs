using System;

namespace UE5DumpUI.Helpers;

/// <summary>
/// Multi-term substring matcher for the Object Tree text filter. The filter
/// box accepts whitespace-separated terms; a node matches only when EVERY term
/// is found (case-insensitive) in at least one of its fields. This is the
/// "two-layer" filter the user asked for: typing "BP_ char" keeps objects whose
/// name / class / address contain BOTH "BP_" and "char", in any order — so you
/// can narrow by one term and refine with the next without a second filter box.
/// </summary>
public static class ObjectTreeFilter
{
    /// <summary>
    /// Split a raw filter string into non-empty, trimmed terms on any
    /// whitespace. Returns an empty array for null / whitespace-only input.
    /// </summary>
    public static string[] SplitTerms(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return Array.Empty<string>();
        return filter.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// True when every term (as produced by <see cref="SplitTerms"/>) is a
    /// case-insensitive substring of at least one of the supplied fields.
    /// An empty term list matches everything (AND over zero terms is true).
    /// </summary>
    public static bool MatchesAllTerms(string[] terms, string name, string className, string address)
    {
        for (int i = 0; i < terms.Length; i++)
        {
            var t = terms[i];
            bool hit =
                name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                className.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                address.Contains(t, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }
}
