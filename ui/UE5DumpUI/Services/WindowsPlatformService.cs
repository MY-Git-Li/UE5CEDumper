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

    public async Task<string?> ShowOpenFileDialogAsync(string filterName, string filterExtension)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = desktop.MainWindow;
            if (topLevel?.StorageProvider is { } sp)
            {
                var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(filterName)
                        {
                            Patterns = new[] { $"*{filterExtension}" }
                        }
                    }
                });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
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

    // --- Process enumeration + DLL injection ------------------------------
    // Classic CreateRemoteThread + LoadLibraryW injection so the UI can load
    // UE5Dumper.dll into a running game without Cheat Engine or a pre-deployed
    // proxy. Classic [DllImport] on blittable signatures — AOT-safe, no
    // LibraryImport. x64 targets only (the DLL is 64-bit).

    public IReadOnlyList<GameProcessInfo> GetRunningProcesses()
    {
        var list = new List<GameProcessInfo>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return list; }

        foreach (var p in procs)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch { path = null; }   // protected / cross-bitness / exited → skip
                if (string.IsNullOrEmpty(path))
                    continue;

                bool isUe = UeProcessDetector.IsUeExecutable(path);
                // Probe loaded modules only for UE games (the inject targets): a
                // module snapshot per process isn't free, and non-UE rows (only
                // shown under "Show all") are never expected to host our DLL.
                (bool Loaded, string? Mode, string? Version) dump = (false, null, null);
                if (isUe)
                    dump = DetectDumper(p);

                list.Add(new GameProcessInfo(
                    p.Id, Path.GetFileName(path), path, isUe,
                    dump.Loaded, dump.Mode, dump.Version));
            }
            catch
            {
                // One bad process must not abort enumeration.
            }
            finally
            {
                p.Dispose();
            }
        }
        return list;
    }

    /// <summary>
    /// Is OUR dumper DLL already loaded in <paramref name="p"/>, and how? Walks the
    /// target's loaded modules, version-probes only the handful whose file name
    /// could be ours (the three proxy names + UE5Dumper.dll — reading a version
    /// resource touches the file, so the hundreds of unrelated DLLs are skipped),
    /// then delegates the identity/mode decision to the pure
    /// <see cref="DumperModuleDetector"/>. Returns "unknown" (false/null/null) on
    /// any failure — an elevated/protected game, a 32-bit target, or a process that
    /// exited mid-enumeration must never abort the whole process list.
    /// </summary>
    private static (bool Loaded, string? Mode, string? Version) DetectDumper(Process p)
    {
        try
        {
            var mods = new List<DumperModuleDetector.ModuleInfo>();
            foreach (ProcessModule m in p.Modules)
            {
                string name = m.ModuleName ?? "";
                string lower = name.ToLowerInvariant();
                if (lower is not ("version.dll" or "dinput8.dll" or "dxgi.dll" or "ue5dumper.dll"))
                    continue;
                string? product = null, version = null;
                try
                {
                    var fv = m.FileVersionInfo;
                    product = fv.ProductName;
                    version = fv.FileVersion;
                }
                catch { /* unreadable version resource → stays null (won't match ours) */ }
                mods.Add(new DumperModuleDetector.ModuleInfo(name, product, version));
            }
            return DumperModuleDetector.Classify(mods);
        }
        catch
        {
            return (false, null, null);
        }
    }

    public InjectResult InjectDll(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            return InjectResult.Failure($"DLL not found: {dllPath}");

        IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            bool denied = err == 5;   // ERROR_ACCESS_DENIED — game likely elevated
            return new InjectResult(false, 0,
                $"OpenProcess failed (Win32 {err})."
                    + (denied ? " The game may be running as Administrator." : ""),
                AccessDenied: denied);
        }
        try
        {
            if (IsWow64Process(hProc, out bool wow64) && wow64)
                return InjectResult.Failure(
                    $"PID {pid} is a 32-bit process; UE5Dumper.dll is x64-only.");

            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
            var size = (UIntPtr)pathBytes.Length;
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, size, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                return InjectResult.Failure($"VirtualAllocEx failed (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!WriteProcessMemory(hProc, remote, pathBytes, size, out _))
                    return InjectResult.Failure($"WriteProcessMemory failed (Win32 {Marshal.GetLastWin32Error()}).");

                IntPtr k32 = GetModuleHandleW("kernel32.dll");
                IntPtr loadLib = k32 == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(k32, "LoadLibraryW");
                if (loadLib == IntPtr.Zero)
                    return InjectResult.Failure("Could not resolve LoadLibraryW.");

                IntPtr hThread = CreateRemoteThread(hProc, IntPtr.Zero, UIntPtr.Zero, loadLib, remote, 0, IntPtr.Zero);
                if (hThread == IntPtr.Zero)
                    return InjectResult.Failure($"CreateRemoteThread failed (Win32 {Marshal.GetLastWin32Error()}).");
                try
                {
                    uint wait = WaitForSingleObject(hThread, InjectTimeoutMs);
                    if (wait != WAIT_OBJECT_0)
                        return InjectResult.Failure($"Remote thread did not finish in {InjectTimeoutMs / 1000}s (wait 0x{wait:X8}).");
                    if (!GetExitCodeThread(hThread, out uint exitCode))
                        return InjectResult.Failure($"GetExitCodeThread failed (Win32 {Marshal.GetLastWin32Error()}).");
                    if (exitCode == 0)
                        return InjectResult.Failure(
                            "LoadLibraryW returned NULL — the DLL failed to load. " +
                            "Check %LOCALAPPDATA%\\UE5CEDumper\\Logs.");
                    return InjectResult.Success(exitCode);
                }
                finally { CloseHandle(hThread); }
            }
            finally { VirtualFreeEx(hProc, remote, UIntPtr.Zero, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    public bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retry injection with elevation: relaunch THIS exe headless with
    /// <c>--inject-elevated &lt;pid&gt; &lt;dll&gt; &lt;resultFile&gt;</c> via ShellExecute "runas"
    /// (one UAC prompt). The short-lived elevated child does the inject and writes
    /// its result to <paramref name="resultFile"/>; the main UI keeps running.
    /// </summary>
    public InjectResult InjectDllElevated(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            return InjectResult.Failure($"DLL not found: {dllPath}");

        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe))
            return InjectResult.Failure("Cannot resolve the app path for elevation.");

        string resultFile = Path.Combine(Path.GetTempPath(), $"ue5inject_{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,   // required for the "runas" verb
                Verb = "runas",
            };
            psi.ArgumentList.Add("--inject-elevated");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add(dllPath);
            psi.ArgumentList.Add(resultFile);

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception w) when (w.NativeErrorCode == 1223)
            {
                return InjectResult.Failure("Elevation cancelled (UAC declined).");
            }
            if (proc == null)
                return InjectResult.Failure("Could not start the elevated helper.");

            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { /* best effort */ }
                return InjectResult.Failure("Elevated inject timed out.");
            }

            string content = "";
            try { if (File.Exists(resultFile)) content = File.ReadAllText(resultFile).Trim(); }
            catch { /* fall back to exit code below */ }

            if (content.StartsWith("OK ", StringComparison.Ordinal))
            {
                uint.TryParse(content.AsSpan(3), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint hmod);
                return InjectResult.Success(hmod);
            }
            return InjectResult.Failure(content.Length > 0
                ? content
                : $"Elevated inject failed (exit {proc.ExitCode}).");
        }
        catch (Exception ex)
        {
            return InjectResult.Failure($"Elevated inject error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
        }
    }

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT_RESERVE = 0x3000;   // MEM_COMMIT | MEM_RESERVE
    private const uint MEM_RELEASE        = 0x8000;
    private const uint PAGE_READWRITE     = 0x04;
    private const uint WAIT_OBJECT_0      = 0x0;
    private const uint InjectTimeoutMs    = 10000;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
        UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
        UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter,
        uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    public void Dispose()
    {
        ReleaseSingleInstance();
    }
}
