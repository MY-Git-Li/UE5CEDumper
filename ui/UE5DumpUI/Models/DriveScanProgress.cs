namespace UE5DumpUI.Models;

/// <summary>
/// Immutable progress snapshot reported by the generic drive scan. A fresh
/// instance is reported per update (records are value-equatable and AOT-safe);
/// only scalar fields are carried so there is no shared mutable state between
/// the reporting worker thread and the UI callback.
/// </summary>
/// <param name="GamesFound">Running count of UE games found so far.</param>
/// <param name="CurrentDrive">Drive currently being walked, e.g. "C:".</param>
/// <param name="Phase">Short human-readable phase label.</param>
public sealed record DriveScanProgress(int GamesFound, string CurrentDrive, string Phase);
