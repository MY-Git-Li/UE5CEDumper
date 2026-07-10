using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;

namespace UE5DumpUI.Views;

public partial class DumpExplorerPanel : UserControl
{
    // AOT-safe sort comparers — under trimming the DataGrid can't discover a
    // sortable member by reflection, so header clicks are a silent no-op without
    // these (see aot-pitfalls.md §4.5). The Offset column displays a hex string
    // but sorts on the numeric value.
    private static readonly IReadOnlyDictionary<string, IComparer> DumpSortComparers =
        new Dictionary<string, IComparer>
        {
            ["KindLabel"]  = DataGridSortComparers.Ordinal<DumpEntry>(e => e.KindLabel),
            ["Name"]       = DataGridSortComparers.Ordinal<DumpEntry>(e => e.Name),
            ["OwnerClass"] = DataGridSortComparers.Ordinal<DumpEntry>(e => e.OwnerClass),
            ["TypeInfo"]   = DataGridSortComparers.Ordinal<DumpEntry>(e => e.TypeInfo),
            ["Offset"]     = DataGridSortComparers.Number<DumpEntry>(e => e.Offset),
            ["Path"]       = DataGridSortComparers.Ordinal<DumpEntry>(e => e.Path),
        };

    public DumpExplorerPanel()
    {
        InitializeComponent();
        this.FindControl<DataGrid>("MatchedGrid")?.WireSortComparers(DumpSortComparers);
        this.FindControl<DataGrid>("UnmatchedGrid")?.WireSortComparers(DumpSortComparers);
    }
}
