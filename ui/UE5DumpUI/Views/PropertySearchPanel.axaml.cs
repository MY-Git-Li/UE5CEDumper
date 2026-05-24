using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class PropertySearchPanel : UserControl
{
    public PropertySearchPanel()
    {
        InitializeComponent();
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
