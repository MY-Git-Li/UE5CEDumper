using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class PropertySearchPanel : UserControl
{
    // AOT-safe sort comparers for the columns whose sort property isn't
    // rooted by a column-level Binding: the three template columns (Scope /
    // Property / Preview) and the Offset text column (sorts by PropOffset
    // while it displays OffsetHex). Without these the header click is a
    // silent no-op under AOT (aot-pitfalls.md §4.5).
    private static readonly IReadOnlyDictionary<string, IComparer> ResultsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["InheritedByCount"] = DataGridSortComparers.Number<PropertySearchMatch>(r => r.InheritedByCount),
            ["PropName"]         = DataGridSortComparers.Ordinal<PropertySearchMatch>(r => r.PropName),
            ["PropOffset"]       = DataGridSortComparers.Number<PropertySearchMatch>(r => r.PropOffset),
            ["Preview"]          = DataGridSortComparers.Ordinal<PropertySearchMatch>(r => r.Preview),
        };

    public PropertySearchPanel()
    {
        InitializeComponent();
        this.FindControl<DataGrid>("ResultsGrid")?.WireSortComparers(ResultsSortComparers);
        Loaded += OnPanelLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not PropertySearchViewModel vm) return;
        // Inject the value-prompt callback: the VM stays View-free so it
        // remains unit-testable, while the dialog mechanics live here.
        vm.FreezeValuePrompt = PromptFreezeValueAsync;
        // Probe AOBMaker once on attach so the Freeze button reflects
        // current state (cooldown inside the VM prevents pipe spam).
        _ = vm.RefreshAobMakerAvailabilityAsync();
    }

    private async System.Threading.Tasks.Task<string?> PromptFreezeValueAsync(PropertySearchMatch match)
    {
        var dialog = new FreezeValueDialog(match);
        // Find the owning Window so the dialog modals correctly.
        Window? owner = null;
        if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();  // fallback — shouldn't happen in real runs
        return dialog.ValueLiteral;
    }

    private void SearchQueryInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is PropertySearchViewModel vm
            && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPanelLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not PropertySearchViewModel vm) return;
        if (vm.SelectedResult == null) return;
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid == null) return;
        // Defer until the DataGrid has its row visuals materialized — without
        // the dispatcher hop ScrollIntoView no-ops because the row isn't in
        // the realized container set yet.
        Dispatcher.UIThread.Post(() =>
        {
            try { grid.ScrollIntoView(vm.SelectedResult, null); }
            catch { /* defensive: missing row, recycled grid, etc. */ }
        }, DispatcherPriority.Background);
    }
}
