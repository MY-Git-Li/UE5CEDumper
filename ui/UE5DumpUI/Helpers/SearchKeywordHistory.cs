using System;
using System.Collections.Generic;

namespace UE5DumpUI.Helpers;

/// <summary>
/// Per-session "remembered keywords" store for the Live Walker field search.
/// Implements the requested rules:
///   - LRU: most-recently-used first, capped at <see cref="MaxEntries"/>.
///   - Longest valid wins: a new keyword that EXTENDS a remembered one replaces
///     it (the shorter prefix is dropped); a new keyword that is a PREFIX of an
///     already-remembered (longer) one is ignored. So while typing
///     m → ma → mag → magi → magic, only the settled "magic" is kept — never the
///     intermediate prefixes.
///   - Only valid keywords are remembered: the caller passes only keywords that
///     yielded matches, and entries shorter than <see cref="MinLength"/> are
///     rejected here as a backstop.
/// All comparisons are case-insensitive; the original casing is preserved.
/// </summary>
public static class SearchKeywordHistory
{
    /// <summary>Maximum remembered keywords (LRU eviction beyond this).</summary>
    public const int MaxEntries = 8;

    /// <summary>Keywords shorter than this are never remembered.</summary>
    public const int MinLength = 2;

    /// <summary>
    /// Fold <paramref name="keyword"/> into <paramref name="history"/> in place,
    /// applying the LRU + longest-valid rules. The list is ordered
    /// most-recently-used first. Returns true when the history changed.
    /// </summary>
    public static bool Remember(IList<string> history, string? keyword, int maxEntries = MaxEntries)
    {
        if (history is null) return false;
        var k = keyword?.Trim() ?? "";
        if (k.Length < MinLength) return false;

        // Longest valid wins: a remembered entry that already EXTENDS k covers it,
        // so k adds nothing — keep the longer one, change nothing.
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].Length > k.Length &&
                history[i].StartsWith(k, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Drop entries that k supersedes: any existing entry that is a prefix of k
        // (shorter run of the same keyword) or an exact case-insensitive duplicate.
        // Iterate backwards for safe in-place removal.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (k.StartsWith(history[i], StringComparison.OrdinalIgnoreCase))
                history.RemoveAt(i);
        }

        // Insert at the front (most-recently used) and enforce the LRU cap.
        history.Insert(0, k);
        while (history.Count > maxEntries)
            history.RemoveAt(history.Count - 1);

        return true;
    }
}
