using UE5DumpUI.Core;

namespace UE5DumpUI.Services;

/// <summary>One persisted teleport hotkey binding.</summary>
public readonly record struct TeleportHotkeyBinding(uint WinMods, uint Vk)
{
    public string Label => HotkeyKeyMap.LabelFor(WinMods, Vk);
}

/// <summary>
/// Persists the user's teleport marker hotkeys to a small text file under
/// %LOCALAPPDATA%\UE5CEDumper. Plain `action=mods,vk` lines — no JSON
/// reflection, so it's trivially Native-AOT safe. Unknown / malformed lines are
/// skipped (forward/backward compatible).
/// </summary>
public sealed class TeleportHotkeyStore
{
    private const string FileName = "teleport-hotkeys.txt";
    private readonly string _path;

    public TeleportHotkeyStore(IPlatformService platform)
    {
        var dir = Path.Combine(platform.GetAppDataPath(), "UE5CEDumper");
        _path = Path.Combine(dir, FileName);
    }

    /// <summary>Load all bindings keyed by action id (e.g. "save0", "recall2").</summary>
    public Dictionary<string, TeleportHotkeyBinding> Load()
    {
        var map = new Dictionary<string, TeleportHotkeyBinding>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path)) return map;
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string action = line.Substring(0, eq).Trim();
                var parts = line.Substring(eq + 1).Split(',');
                if (parts.Length < 2) continue;
                if (uint.TryParse(parts[0].Trim(), out uint mods) &&
                    uint.TryParse(parts[1].Trim(), out uint vk))
                {
                    map[action] = new TeleportHotkeyBinding(mods, vk);
                }
            }
        }
        catch { /* corrupt file → start fresh */ }
        return map;
    }

    /// <summary>Persist the full binding set (replaces the file).</summary>
    public void Save(IReadOnlyDictionary<string, TeleportHotkeyBinding> bindings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string> { "# UE5CEDumper teleport hotkeys (action=mods,vk)" };
            foreach (var kv in bindings)
                lines.Add($"{kv.Key}={kv.Value.WinMods},{kv.Value.Vk}");
            File.WriteAllLines(_path, lines);
        }
        catch { /* best-effort persistence */ }
    }
}
