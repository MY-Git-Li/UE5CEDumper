using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;

namespace UE5DumpUI.Views;

public partial class PointerPanel : UserControl
{
    public PointerPanel()
    {
        InitializeComponent();
    }

    // Repo URL in the credit footer opens in the default browser. The author
    // line is now the experimental-features checkbox, so only the repo link
    // remains clickable here. Mirrors CreditFooter's UseShellExecute pattern.
    private void OnRepoUrlPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb || string.IsNullOrWhiteSpace(tb.Text)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = tb.Text,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Swallow: failing to open a credit link must never crash the UI.
        }
    }
}
