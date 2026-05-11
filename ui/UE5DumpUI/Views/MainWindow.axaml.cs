using System;
using Avalonia.Controls;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Audit fixes #16 / #17: dispose timer-owning child VMs when the
        // window closes, so background DispatcherTimers and Threading.Timer
        // callbacks don't fire post-close on torn-down state.
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable d) d.Dispose();
    }

    /// <summary>
    /// Per-tab activation work:
    /// 1. Stop Live Walker auto-refresh when the user switches away
    ///    from Live Walker (no point polling while viewing other tabs).
    /// 2. Refresh AOBMaker availability for tabs whose actions depend
    ///    on the CE plugin (LiveWalker, InterestingFunctions). The
    ///    re-check is fire-and-forget with a 5s cooldown so rapid tab
    ///    switches don't stack 2s pipe-connect timeouts -- keeps the
    ///    grayout state honest when the user starts/stops CE without
    ///    blocking the UI thread.
    /// </summary>
    private void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // Tab index 0 = Live Walker (first tab in the TabControl)
        if (tabs.SelectedIndex != 0 && vm.LiveWalker.IsAutoRefreshing)
        {
            vm.LiveWalker.StopAutoRefreshTimer();
        }

        // Refresh AOBMaker state for tabs whose toolbar / per-row buttons
        // depend on it. Order matches the AXAML tab order.
        switch (tabs.SelectedIndex)
        {
            case 0: vm.LiveWalker.TryCheckAobMaker(); break;
            case 3: vm.InterestingFunctions.TryCheckAobMaker(); break;
        }
    }
}
