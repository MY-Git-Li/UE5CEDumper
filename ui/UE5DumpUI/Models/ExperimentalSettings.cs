using System.Text.Json.Serialization;

namespace UE5DumpUI.Models;

/// <summary>
/// Persisted opt-in flag for the experimental analysis tabs (Snapshot /
/// SPC Query / Class Pivot). Stored as a tiny JSON file under
/// %LOCALAPPDATA%\UE5CEDumper\experimental.json so the choice survives
/// restarts. See <see cref="Services.ExperimentalGate"/>.
/// </summary>
public sealed class ExperimentalSettings
{
    /// <summary>True once the user ticks the System-tab credit checkbox.</summary>
    public bool Enabled { get; set; }

    /// <summary>True once the user has opened an experimental tab while enabled.
    /// From then on the opt-in can no longer be turned off. See
    /// <see cref="Services.ExperimentalGate.IsLocked"/>.</summary>
    public bool Locked { get; set; }

    /// <summary>Per-game snapshot DB size cap, in MB. When a capture pushes the
    /// active game's DB over this, the oldest snapshots are auto-dropped (FIFO).
    /// 0 = unlimited. Default 1 GB.</summary>
    public int SnapshotQuotaMb { get; set; } = 1024;
}

/// <summary>
/// Source-generated JSON serializer context for AOT/trimming compatibility.
/// System.Text.Json reflection is disabled in this app — all types must be
/// registered here.
/// </summary>
[JsonSerializable(typeof(ExperimentalSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal partial class ExperimentalSettingsJsonContext : JsonSerializerContext
{
}
