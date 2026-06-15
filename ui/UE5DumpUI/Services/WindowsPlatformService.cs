using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using UE5DumpUI.Core;

namespace UE5DumpUI.Services;

/// <summary>
/// Windows-specific platform operations.
/// </summary>
public sealed class WindowsPlatformService : IPlatformService, IDisposable
{
    private Mutex? _singleInstanceMutex;

    public bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, Constants.MutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        return true;
    }

    public void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    public string GetAppDataPath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public string GetLogDirectoryPath()
    {
        return Path.Combine(GetAppDataPath(), Constants.LogFolderName, Constants.LogSubFolder);
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = desktop.MainWindow;
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
            }
        }
    }

    public async Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string filterExtension)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = desktop.MainWindow;
            if (topLevel?.StorageProvider is { } sp)
            {
                var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save File",
                    SuggestedFileName = defaultFileName,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType(filterName)
                        {
                            Patterns = new[] { $"*{filterExtension}" }
                        }
                    }
                });
                return file?.Path.LocalPath;
            }
        }
        return null;
    }

    public string GetMachineName()
    {
        return Environment.MachineName;
    }

    public void CloseImeForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        // Grab the input context for this window. A window with no IME loaded
        // (e.g. user is on a pure-ASCII keyboard layout) returns NULL — nothing
        // to do.
        IntPtr himc = ImmGetContext(windowHandle);
        if (himc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Close the IME open status: composition is dismissed and keystrokes
            // flow through as direct alphanumeric input. This is intentionally
            // one-directional — we never re-open it on focus-out.
            ImmSetOpenStatus(himc, false);
        }
        catch
        {
            // IME control must never crash the UI over a focus change.
        }
        finally
        {
            ImmReleaseContext(windowHandle, himc);
        }
    }

    // --- imm32 IME control (P/Invoke) ------------------------------------
    // Classic DllImport on blittable-only signatures (IntPtr + Win32 BOOL):
    // Native-AOT compatible without requiring AllowUnsafeBlocks (which the
    // LibraryImport source generator would force project-wide).

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetOpenStatus(IntPtr hIMC, [MarshalAs(UnmanagedType.Bool)] bool fOpen);

    [DllImport("imm32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    // --- Restartable-apps registration (P/Invoke) ------------------------
    // Opt into the Windows Restart Manager so that if this app is running when
    // the user reboots / installs an update, Windows relaunches it on next
    // sign-in (gated by the user's "Automatically save my restartable apps and
    // restart them when I sign back in" setting, Win10 + Win11). Registered on
    // startup and never unregistered: the trigger only fires if the process is
    // alive at shutdown, so closing the app normally already means "don't bring
    // me back". RegisterApplicationRestart is Unicode-only (no A/W variants).
    private const uint RESTART_NO_CRASH = 1;  // don't relaunch after a crash
    private const uint RESTART_NO_HANG  = 2;  // ...or a hang (avoids relaunch loops)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags);

    public void RegisterForRestart()
    {
        try
        {
            // null command line → relaunch the exe with no extra args (this app
            // is single-instance and takes none). Restart only on reboot/update,
            // never on crash/hang.
            RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG);
        }
        catch
        {
            // Best-effort: API absent (pre-Vista) or policy-denied — ignore.
        }
    }

    public Task RevealInExplorerAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // Open Explorer with the file selected.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    {
                        UseShellExecute = true,
                    });
            }
        }
        catch
        {
            // Revealing a folder must never crash the UI.
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        ReleaseSingleInstance();
    }
}
