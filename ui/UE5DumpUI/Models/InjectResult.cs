namespace UE5DumpUI.Models;

/// <summary>
/// Result of a DLL-injection attempt (CreateRemoteThread + LoadLibraryW).
/// On success <see cref="HModule"/> holds the low 32 bits of the loaded HMODULE
/// (the remote thread's exit code); on failure <see cref="ErrorMessage"/> is set.
/// <see cref="AccessDenied"/> flags an OpenProcess Access-Denied (Win32 5) — the
/// caller can retry elevated (the game runs at a higher integrity level).
/// </summary>
public sealed record InjectResult(bool Ok, uint HModule, string? ErrorMessage, bool AccessDenied = false)
{
    public static InjectResult Success(uint hmodule) => new(true, hmodule, null);
    public static InjectResult Failure(string message) => new(false, 0, message);
}
