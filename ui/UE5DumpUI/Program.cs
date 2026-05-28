using Avalonia;
using Avalonia.Win32;

namespace UE5DumpUI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // AOT publish gives gutted stack traces (no PDB lookup at runtime,
        // inlining opaque). A startup crash in the compositor thread would
        // otherwise leave the user with "the exe just closed" and no signal.
        // This top-level catch writes the full exception to
        // %LOCALAPPDATA%\UE5CEDumper\crash.log — the only diagnostic surface
        // for AOT startup failures (aot-pitfalls.md §0.17 / §2 / §8.3).
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UE5CEDumper");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(
                    Path.Combine(logDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UE5DumpUI startup crash\n{ex}\n");
            }
            catch { /* best effort — nothing more we can do if even this fails */ }
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // AOT: WinUI Composition via MicroCom COM interop crashes on Native AOT.
            // Force software redirection surface to bypass the compositor COM path.
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .WithInterFont();
}
