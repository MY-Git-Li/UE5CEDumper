using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

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

    // --- Drive enumeration + physical-disk mapping -----------------------
    // Used by the generic (non-Steam) UE-game scan to schedule per-physical-disk
    // sequential / cross-disk parallel walks. Enumeration is pure managed BCL;
    // the letter -> physical disk number lookup uses classic [DllImport]
    // DeviceIoControl (NOT WMI/System.Management, which is banned by the AOT rule).

    public IReadOnlyList<DriveDescriptor> GetLogicalDrives()
    {
        var list = new List<DriveDescriptor>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return list;
        }

        foreach (var d in drives)
        {
            try
            {
                // Only volumes worth scanning for installed games; IsReady guards
                // against empty optical / card readers that throw on metadata access.
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (!d.IsReady)
                    continue;

                char letter = d.Name.Length > 0 ? char.ToUpperInvariant(d.Name[0]) : '?';
                string? label = null;
                long total = 0, free = 0;
                try { label = d.VolumeLabel; } catch { /* label optional */ }
                try { total = d.TotalSize; } catch { /* size optional */ }
                try { free = d.AvailableFreeSpace; } catch { /* size optional */ }

                list.Add(new DriveDescriptor
                {
                    Root = d.Name,
                    Letter = letter,
                    Label = label,
                    Type = d.DriveType,
                    PhysicalDiskNumber = GetPhysicalDiskNumber(letter),
                    TotalBytes = total,
                    FreeBytes = free,
                });
            }
            catch
            {
                // One unreadable drive must not abort enumeration.
            }
        }

        return list;
    }

    /// <summary>
    /// Map a drive letter to the physical disk number backing it via
    /// DeviceIoControl(IOCTL_STORAGE_GET_DEVICE_NUMBER). Returns null when it
    /// can't be determined — spanned/striped/network/virtual volumes, or the MPIO
    /// sentinel — so the caller treats the drive as its own scan group.
    /// dwDesiredAccess = 0 (query-only) means this needs NO administrator rights;
    /// opening \\.\C: with GENERIC_READ WOULD require elevation and must be avoided.
    /// </summary>
    private static int? GetPhysicalDiskNumber(char driveLetter)
    {
        // Volume device path form: \\.\C:  — NO trailing backslash (a trailing
        // backslash opens the filesystem root, not the volume device handle).
        string path = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";

        IntPtr h = CreateFileW(
            path,
            0,                                          // query-only: no admin needed
            FILE_SHARE_READ | FILE_SHARE_WRITE,          // C: is always open for write
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE)
            return null;

        try
        {
            bool ok = DeviceIoControl(
                h, IOCTL_STORAGE_GET_DEVICE_NUMBER,
                IntPtr.Zero, 0,
                out STORAGE_DEVICE_NUMBER sdn, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(),
                out _, IntPtr.Zero);
            if (!ok)
                return null;                            // spanned/striped/virtual → own group
            if (sdn.DeviceNumber == 0xFFFFFFFF)
                return null;                            // MPIO sentinel → own group
            return unchecked((int)sdn.DeviceNumber);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // IOCTL_STORAGE_GET_DEVICE_NUMBER = CTL_CODE(IOCTL_STORAGE_BASE=0x2D, 0x420,
    // METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x2D1080. Available since Windows XP.
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint FILE_SHARE_READ  = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING    = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // 12-byte blittable struct (three 4-byte integers) => classic [DllImport]
    // with an out-struct works without LibraryImport / AllowUnsafeBlocks.
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;      // DEVICE_TYPE (ULONG) — field order matters
        public uint DeviceNumber;    // physical disk index we group by
        public uint PartitionNumber;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        out STORAGE_DEVICE_NUMBER lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        ReleaseSingleInstance();
    }
}
