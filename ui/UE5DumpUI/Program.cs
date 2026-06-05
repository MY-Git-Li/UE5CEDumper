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
            // Windows-only tool (injects into Windows games), so wire the Win32 +
            // Skia backends EXPLICITLY instead of UsePlatformDetect(). This drops
            // the Avalonia.Desktop meta-package (which dragged in the X11 / macOS
            // Native / FreeDesktop backends + Tmds.DBus), eliminating their
            // "will always throw" ILC AOT warnings — those code paths can never
            // run on Windows. UsePlatformDetect() itself lives in Avalonia.Desktop,
            // so it's gone too.
            .UseWin32()
            .UseSkia()
            // Text shaping — UsePlatformDetect() wired this for us; the explicit
            // backend must call it or AppBuilder.Setup() throws "No text shaping
            // system configured".
            .UseHarfBuzz()
            // AOT: WinUI Composition via MicroCom COM interop crashes on Native AOT.
            // Force software redirection surface to bypass the compositor COM path.
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .WithInterFont();
}
