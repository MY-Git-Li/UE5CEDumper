using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class TeleportPanel : UserControl
{
    public TeleportPanel()
    {
        InitializeComponent();
        // Tunnel KeyDown so we intercept the capture key before any focused
        // child (e.g. the BugItGo TextBox) consumes it.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // Suppress focus-driven auto-scroll: clicking a partially-visible control
        // (e.g. the "Global cursor hotkey" checkbox) otherwise makes the
        // ScrollViewer bring it into view, scrolling the panel and eating the
        // first click (the second click then works). Handling RequestBringIntoView
        // on the content root — a child of the ScrollContentPresenter — marks it
        // handled before the presenter's class handler scrolls. Manual wheel /
        // scrollbar are unaffected (they don't raise this event).
        ContentRoot.AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView);
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
        => e.Handled = true;

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TeleportViewModel vm || !vm.IsCapturingHotkey)
            return;
        if (vm.ApplyCapturedKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }
}
