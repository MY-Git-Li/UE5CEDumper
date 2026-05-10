using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
}
