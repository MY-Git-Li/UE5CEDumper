namespace UE5DumpUI.Models;

/// <summary>
/// Retention policy for the auto-snapshot loop (see
/// <see cref="Services.AutoSnapshotPlanner"/> and the Snapshot tab's Auto section).
/// Persisted as the wire string "KeepRecent" / "FixedCount" in
/// <see cref="UiOptionsSettings"/>; default <see cref="KeepRecent"/>. Pure value
/// enum (AOT-safe).
/// </summary>
public enum AutoSnapshotRetentionMode
{
    /// <summary>Keep capturing forever, retaining only the newest N snapshots
    /// (count-based FIFO eviction via <see cref="Core.ISnapshotStore.EnforceCountAsync"/>).</summary>
    KeepRecent = 0,

    /// <summary>Capture exactly N times this session, then stop (no count eviction —
    /// all N are kept, still subject to the byte quota).</summary>
    FixedCount,
}

/// <summary>Wire-string ↔ <see cref="AutoSnapshotRetentionMode"/> mapping, mirroring
/// <see cref="FloatRoundModeWire"/>. Kept in Models so the VM + persisted options
/// agree on the spelling.</summary>
public static class AutoSnapshotRetentionModeWire
{
    public const string KeepRecent = "KeepRecent";
    public const string FixedCount = "FixedCount";

    public static string ToWire(AutoSnapshotRetentionMode mode) => mode switch
    {
        AutoSnapshotRetentionMode.FixedCount => FixedCount,
        _                                    => KeepRecent,
    };

    /// <summary>Parse a wire string; unknown / null falls back to
    /// <see cref="AutoSnapshotRetentionMode.KeepRecent"/> (backward compatible).</summary>
    public static AutoSnapshotRetentionMode Parse(string? s) => s switch
    {
        FixedCount => AutoSnapshotRetentionMode.FixedCount,
        _          => AutoSnapshotRetentionMode.KeepRecent,
    };
}

/// <summary>Why the auto-snapshot loop stopped itself after a capture cycle —
/// decided by <see cref="Services.AutoSnapshotPlanner.EvaluatePostCapture"/>.</summary>
public enum AutoStopReason
{
    /// <summary>Keep looping — no stop condition met.</summary>
    None = 0,

    /// <summary>FixedCount mode reached its target capture count.</summary>
    ReachedCount,

    /// <summary>The byte quota can only hold a single snapshot while the user wants
    /// to retain more, and auto-adjust-quota is off — the loop can make no progress.</summary>
    QuotaHoldsOne,
}

/// <summary>Outcome of one capture attempt (<see cref="ViewModels.SnapshotViewModel"/>
/// CaptureCoreAsync) — lets the auto-snapshot loop react without inspecting UI state.</summary>
public enum CaptureOutcome
{
    /// <summary>Capture finished with a complete snapshot.</summary>
    Success = 0,

    /// <summary>Capture stopped early but KEPT a usable partial snapshot
    /// (max-dataset cap or mid-capture low-disk stop).</summary>
    Partial,

    /// <summary>User cancelled — the partial was discarded + disk reclaimed.</summary>
    Cancelled,

    /// <summary>Refused before writing: the drive was below the free-space guard.</summary>
    DiskLow,

    /// <summary>Refused before writing: not connected / not capturable.</summary>
    NotReady,

    /// <summary>An error (e.g. pipe/DLL failure) aborted the capture.</summary>
    Failed,
}
