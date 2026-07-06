using Avalonia.Controls;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class ProxyDeployPanel : UserControl
{
    public ProxyDeployPanel()
    {
        InitializeComponent();

        // Wire the process-picker delegate once the VM is attached. Kept here (a
        // View) so the VM never references a Window — it just calls PickProcessAsync.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProxyDeployViewModel vm)
                vm.PickProcessAsync = () => ProcessPickerWindow.ShowAsync(vm.ListGameProcessesAsync);
        };
    }
}
