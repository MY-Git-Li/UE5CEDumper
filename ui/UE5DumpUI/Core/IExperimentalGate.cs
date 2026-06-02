using System;

namespace UE5DumpUI.Core;

/// <summary>
/// Shared opt-in gate for the experimental analysis tabs (Snapshot /
/// SPC Query / Class Pivot). A single instance is shared between
/// MainWindowViewModel (which gates tab visibility) and
/// PointerPanelViewModel (which owns the System-tab toggle checkbox), so
/// flipping it in one place updates the other via <see cref="Changed"/>.
/// The implementation persists the flag across restarts.
/// </summary>
public interface IExperimentalGate
{
    /// <summary>True when the experimental tabs should be shown.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Raised whenever <see cref="IsEnabled"/> changes.</summary>
    event EventHandler? Changed;
}
