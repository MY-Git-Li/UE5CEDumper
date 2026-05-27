using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class InterestingFunctionsPanel : UserControl
{
    public InterestingFunctionsPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Forward the DataGrid's current multi-select to the VM command.
    /// Sister to <c>InterestingPropertiesPanel.OnGenerateCtClick</c>.
    /// Strongly-typed list keeps the CommunityToolkit [RelayCommand]
    /// binding AOT-friendly (no reflection-based dispatch).
    /// </summary>
    private void OnGenerateCtClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InterestingFunctionsViewModel vm) return;
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid?.SelectedItems is null) return;

        var rows = new List<ScoredFunctionRow>(grid.SelectedItems.Count);
        foreach (var item in grid.SelectedItems)
        {
            if (item is ScoredFunctionRow r) rows.Add(r);
        }
        vm.GenerateCheatTableCommand.Execute(rows);
    }
}
