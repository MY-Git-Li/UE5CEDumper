namespace UE5DumpUI.Models;

/// <summary>
/// Result of a paginated object list query.
/// </summary>
public sealed class ObjectListResult
{
    public int Total { get; init; }
    /// <summary>Number of GObject indices the server actually scanned (for proper pagination advancement).</summary>
    public int Scanned { get; init; }
    /// <summary>True when a server-side search hit its result cap — more matches exist than
    /// were returned. Only set by <c>search_objects</c>; the paginated list load leaves it false.</summary>
    public bool Truncated { get; init; }
    public List<UObjectNode> Objects { get; init; } = new();
}
