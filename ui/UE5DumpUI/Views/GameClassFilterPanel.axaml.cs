using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;

namespace UE5DumpUI.Views;

public partial class GameClassFilterPanel : UserControl
{
    // AOT-safe sort comparers for the Score template column (no column-level
    // Binding) and the Size text column (sorts by PropertiesSize while it
    // displays SizeHex). Without these the header click is a silent no-op
    // under AOT (aot-pitfalls.md §4.5). Other text columns sort out-of-box.
    private static readonly IReadOnlyDictionary<string, IComparer> ResultsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["Score"]          = DataGridSortComparers.Number<GameClassEntry>(r => r.Score),
            ["PropertiesSize"] = DataGridSortComparers.Number<GameClassEntry>(r => r.PropertiesSize),
        };

    public GameClassFilterPanel()
    {
        InitializeComponent();
        this.FindControl<DataGrid>("ResultsGrid")?.WireSortComparers(ResultsSortComparers);
    }
}
