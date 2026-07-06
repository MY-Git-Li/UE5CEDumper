using UE5DumpUI.Models;

namespace UE5DumpUI.Core;

/// <summary>
/// Platform-specific operations interface.
/// All OS-dependent calls must go through this interface.
/// </summary>
public interface IPlatformService
{
    /// <summary>Try to acquire single-instance mutex. Returns true if this is the first instance.</summary>
    bool TryAcquireSingleInstance();

    /// <summary>Release the single-instance mutex.</summary>
    void ReleaseSingleInstance();

    /// <summary>Get the application data folder path (%APPDATA%).</summary>
    string GetAppDataPath();

    /// <summary>Get the log directory path.</summary>
    string GetLogDirectoryPath();

    /// <summary>Copy text to clipboard.</summary>
    Task CopyToClipboardAsync(string text);

    /// <summary>Open the OS file browser at <paramref name="path"/>: reveal/select the
    /// file if it exists, otherwise open the containing directory. Never throws.</summary>
    Task RevealInExplorerAsync(string path);

    /// <summary>Get the machine name for per-machine file naming.</summary>
    string GetMachineName();

    /// <summary>
    /// Close the IME for the given top-level window handle so keystrokes pass
    /// through as direct alphanumeric input. Used when focus enters a text
    /// field: every input field in this app takes ASCII content (hex
    /// addresses, AOB patterns, numbers, class / property names, file paths),
    /// so a CJK IME left in composition mode only gets in the way. We do NOT
    /// restore the prior IME state on focus-out — a user who wants CJK input
    /// flips the IME back manually. No-op on non-Windows / null handle.
    /// </summary>
    void CloseImeForWindow(IntPtr windowHandle);

    /// <summary>
    /// Show a platform save-file dialog.
    /// Returns the chosen file path, or null if user cancelled.
    /// </summary>
    Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string filterName, string filterExtension);

    /// <summary>
    /// Opt the running process into the Windows Restart Manager so a
    /// reboot / update relaunches it on next sign-in (Win10 + Win11, gated by
    /// the user's "restart apps" setting). Default no-op so non-Windows
    /// implementations and test doubles need not provide it; the real Windows
    /// service overrides it with <c>RegisterApplicationRestart</c>.
    /// </summary>
    void RegisterForRestart() { }

    /// <summary>
    /// Enumerate ready fixed/removable drives with metadata AND the physical disk
    /// number backing each (via IOCTL_STORAGE_GET_DEVICE_NUMBER). Used by the
    /// generic (non-Steam) UE-game scan to group drives so that partitions on one
    /// physical disk are scanned sequentially while different disks scan in
    /// parallel. Default returns empty so non-Windows implementations and test
    /// doubles need not provide it. Never throws; one unreadable drive is skipped.
    /// </summary>
    IReadOnlyList<DriveDescriptor> GetLogicalDrives() => Array.Empty<DriveDescriptor>();

    /// <summary>
    /// Enumerate running processes as DLL-injection candidates, flagging which look
    /// like UE4/UE5 games (<see cref="GameProcessInfo.IsUe"/>). Default empty for
    /// non-Windows implementations and test doubles. Never throws; inaccessible
    /// processes are skipped.
    /// </summary>
    IReadOnlyList<GameProcessInfo> GetRunningProcesses() => Array.Empty<GameProcessInfo>();

    /// <summary>
    /// Inject a DLL into a running process via CreateRemoteThread + LoadLibraryW.
    /// x64 targets only (rejects Wow64). Default reports unsupported for non-Windows
    /// implementations and test doubles.
    /// </summary>
    InjectResult InjectDll(int pid, string dllPath) =>
        InjectResult.Failure("DLL injection is only supported on Windows.");
}
