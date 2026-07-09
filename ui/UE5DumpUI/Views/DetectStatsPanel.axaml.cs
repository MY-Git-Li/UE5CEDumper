using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UE5DumpUI.Views;

/// <summary>
/// Experimental "Detect Player Stats" panel (P4). One button shortlists likely
/// HP / MP / Gold fields and confirms them against the live game; results are a
/// low-accuracy reference (see the in-panel disclaimer). Logic lives in
/// <see cref="ViewModels.DetectStatsViewModel"/>.
/// </summary>
public partial class DetectStatsPanel : UserControl
{
    public DetectStatsPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
