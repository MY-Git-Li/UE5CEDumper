using CommunityToolkit.Mvvm.ComponentModel;

namespace UE5DumpUI.Models;

/// <summary>
/// A single instance found by FindInstances.
/// </summary>
public sealed partial class InstanceResult : ObservableObject
{
    public string Address { get; init; } = "";
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";
    /// <summary>UClass* address — the key for find_functions_by_class.</summary>
    public string ClassAddress { get; init; } = "";
    public string OuterAddr { get; init; } = "";

    /// <summary>Batch "Find Func" result: which UFunctions take this instance's
    /// class as a param/return. Format "N · func1, func2[, …]" / "0" / "—" / "".</summary>
    [ObservableProperty] private string _xrefInfo = "";
}
