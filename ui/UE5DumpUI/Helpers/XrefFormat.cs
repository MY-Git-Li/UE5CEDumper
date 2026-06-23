using System.Collections.Generic;
using System.Linq;
using UE5DumpUI.Models;

namespace UE5DumpUI.Helpers;

/// <summary>
/// Formats batch-xref results into the compact inline-cell summary shared by
/// the Property Search and Interesting Properties "Find Funcs" columns:
/// "N · func1, func2[, …]", or "0" when nothing references the field.
/// </summary>
public static class XrefFormat
{
    public static string FunctionsSummary(IReadOnlyList<PropertyXrefMatch> xrefs)
    {
        if (xrefs.Count == 0) return "0";
        var preview = string.Join(", ", xrefs.Take(2).Select(x => x.FunctionName));
        return xrefs.Count > 2 ? $"{xrefs.Count} · {preview}, …" : $"{xrefs.Count} · {preview}";
    }
}
