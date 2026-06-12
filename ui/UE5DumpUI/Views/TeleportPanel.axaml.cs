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
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TeleportViewModel vm || !vm.IsCapturingHotkey)
            return;
        if (vm.ApplyCapturedKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }
}
