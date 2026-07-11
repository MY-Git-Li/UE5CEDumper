using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;

namespace UE5DumpUI.Views;

public partial class LiveFuncsPanel : UserControl
{
    // AOT-safe sort comparer for the "Calls" template column (no column-level
    // Binding → its reflection sort is trimmed under AOT, aot-pitfalls.md §4.5).
    // The text columns (Class / Function / Params) sort out-of-box.
    private static readonly IReadOnlyDictionary<string, IComparer> ResultsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["Count"] = DataGridSortComparers.Number<PeProfileEntry>(r => r.Count),
            ["FirstSeq"] = DataGridSortComparers.Number<PeProfileEntry>(r => r.FirstSeq),
            ["Delta"] = DataGridSortComparers.Number<PeProfileEntry>(r => r.Delta),
            ["Kind"]  = DataGridSortComparers.Ordinal<PeProfileEntry>(r => r.Kind),
            ["TypeLabel"] = DataGridSortComparers.Ordinal<PeProfileEntry>(r => r.TypeLabel),
        };

    public LiveFuncsPanel()
    {
        InitializeComponent();
        this.FindControl<DataGrid>("ResultsGrid")?.WireSortComparers(ResultsSortComparers);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
