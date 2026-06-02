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

    /// <summary>Per-game snapshot DB size cap in MB (0 = unlimited). Persisted
    /// alongside <see cref="IsEnabled"/>; setting it does not raise
    /// <see cref="Changed"/> (the Snapshot tab owns its own quota UI).</summary>
    int SnapshotQuotaMb { get; set; }

    /// <summary>True once the user has actually opened an experimental tab while
    /// enabled. While locked the opt-in checkbox can no longer be unticked, and
    /// <see cref="IsEnabled"/> can no longer be set back to false. Persisted, so
    /// the commitment survives restarts.</summary>
    bool IsLocked { get; }

    /// <summary>Commit to the experimental features irreversibly (idempotent).
    /// Persists the lock and raises <see cref="Changed"/> on the first call.</summary>
    void Lock();

    /// <summary>Raised whenever <see cref="IsEnabled"/> or <see cref="IsLocked"/>
    /// changes.</summary>
    event EventHandler? Changed;
}
