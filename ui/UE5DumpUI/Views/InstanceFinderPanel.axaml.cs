using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class InstanceFinderPanel : UserControl
{
    public InstanceFinderPanel()
    {
        InitializeComponent();
    }

    /// <summary>Forward the grid's multi-select (empty = all filtered rows) to the
    /// batch Find Func command, which fills each row's inline "Funcs" column
    /// (deduped by class within the run).</summary>
    private void OnBatchFindFuncsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstanceFinderViewModel vm) return;
        var grid = this.FindControl<DataGrid>("InstancesGrid");
        var rows = new List<InstanceResult>();
        if (grid?.SelectedItems is { } sel)
        {
            foreach (var item in sel)
                if (item is InstanceResult r) rows.Add(r);
        }
        vm.BatchFindFuncsCommand.Execute(rows);
    }

    private void SearchClassNameInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is InstanceFinderViewModel vm
            && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void LookupAddressInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is InstanceFinderViewModel vm
            && vm.LookupAddressCommand.CanExecute(null))
        {
            vm.LookupAddressCommand.Execute(null);
            e.Handled = true;
        }
    }
}
