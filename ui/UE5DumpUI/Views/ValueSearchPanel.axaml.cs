using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UE5DumpUI.Views;

public partial class ValueSearchPanel : UserControl
{
    public ValueSearchPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
