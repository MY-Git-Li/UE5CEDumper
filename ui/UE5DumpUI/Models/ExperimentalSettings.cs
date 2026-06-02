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
