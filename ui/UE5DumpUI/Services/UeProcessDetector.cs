using System.IO;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure heuristics for deciding whether a running process's executable is a
/// UE4/UE5 game — the process-side analogue of the drive-scan's
/// <c>LooksLikeUeGameRoot</c>. Name-based first (cheap, pure), then structural
/// (guarded <c>Directory.Exists</c>). Unit-testable.
/// </summary>
internal static class UeProcessDetector
{
    // Non-game UE helper / editor / our-own executables that must never be
    // offered as an injection target.
    private static readonly HashSet<string> SkipExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "CrashReportClient.exe", "UnrealEditor.exe", "UE4Editor.exe",
        "UnrealFrontend.exe", "UnrealCEFSubProcess.exe", "EpicWebHelper.exe",
        "UE5DumpUI.exe",
    };

    /// <summary>True when the executable at <paramref name="exePath"/> looks like a
    /// UE4/UE5 shipping game.</summary>
    public static bool IsUeExecutable(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return false;

        string name = Path.GetFileName(exePath);
        if (SkipExe.Contains(name))
            return false;

        // High-confidence: cooked shipping executable naming
        // (…-Win64-Shipping.exe, …-WinGDK-Shipping.exe, …-Shipping.exe).
        if (name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        // Structural: exe under \Binaries\Win64\ with an Engine folder / Content\Paks up-tree.
        if (exePath.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? bin  = Path.GetDirectoryName(exePath);                    // ...\<Proj>\Binaries\Win64
                string? proj = Path.GetDirectoryName(Path.GetDirectoryName(bin)); // ...\<Proj>
                string? root = Path.GetDirectoryName(proj);                       // ...\<GameRoot>
                if (proj != null && Directory.Exists(Path.Combine(proj, "Content", "Paks")))
                    return true;
                if (root != null && Directory.Exists(Path.Combine(root, "Engine")))
                    return true;
            }
            catch
            {
                // fall through to the path-signal default
            }
            return true; // under \Binaries\Win64\ is itself a strong UE signal
        }
        return false;
    }
}
