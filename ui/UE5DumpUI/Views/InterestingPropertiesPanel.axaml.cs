using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class InterestingPropertiesPanel : UserControl
{
    public InterestingPropertiesPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Forward the DataGrid's current multi-select to the VM command.
    /// AOT-safe: we cast each SelectedItem explicitly to
    /// <see cref="ScoredPropertyRow"/> rather than going through any
    /// reflection-based path, and the command parameter is a strongly-
    /// typed <see cref="System.Collections.Generic.IList{T}"/> so
    /// CommunityToolkit's [RelayCommand] doesn't need dynamic dispatch
    /// to bind it.
    /// </summary>
    private void OnGenerateCtClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InterestingPropertiesViewModel vm) return;
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid?.SelectedItems is null) return;

        var rows = new List<ScoredPropertyRow>(grid.SelectedItems.Count);
        foreach (var item in grid.SelectedItems)
        {
            if (item is ScoredPropertyRow r) rows.Add(r);
        }
        vm.GenerateCheatTableCommand.Execute(rows);
    }
}
