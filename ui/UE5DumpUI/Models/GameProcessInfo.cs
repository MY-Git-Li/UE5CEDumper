namespace UE5DumpUI.Models;

/// <summary>
/// A running process offered as a DLL-injection target in the process picker.
/// <paramref name="IsUe"/> flags whether the executable looks like a UE4/UE5 game
/// (name / path heuristics — see <c>UeProcessDetector</c>).
/// </summary>
public sealed record GameProcessInfo(int Pid, string Name, string Path, bool IsUe)
{
    /// <summary>One-line label shown in the picker list.</summary>
    public string Display => $"{Name}   (PID {Pid})";
}
