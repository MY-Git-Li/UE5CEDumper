using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UE5DumpUI.Views;

public partial class ConsolePanel : UserControl
{
    public ConsolePanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
