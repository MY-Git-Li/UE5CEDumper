using System;
using System.IO;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Persists the global panel/Live-Walker OPTIONS (stable user preferences) to a
/// single JSON file: %LOCALAPPDATA%\UE5CEDumper\ui-options.json. Modelled on
/// <see cref="ExperimentalGate"/> (source-generated JSON, atomic temp-then-rename,
/// swallow-and-log on failure). Synchronous — the file is a few KB and is read once
/// at startup and written debounced from the UI layer.
///
/// See <see cref="UiOptionsSettings"/> for what is / isn't persisted.
/// </summary>
public sealed class UiOptionsStore
{
    private readonly string _filePath;
    private readonly ILoggingService? _log;
    private readonly object _ioLock = new();

    // Source-generated JSON context (reflection-based JSON is disabled in trimmed/AOT builds)
    private static readonly UiOptionsJsonContext s_jsonCtx = UiOptionsJsonContext.Default;

    public UiOptionsStore(IPlatformService platform, ILoggingService? log = null)
    {
        _log = log;
        var dir = Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, Constants.UiOptionsFile);
    }

    /// <summary>
    /// Load the saved options, or a fresh all-defaults object when the file is
    /// absent / unreadable / corrupt. Also clears any orphaned .tmp left by a
    /// process killed mid-write (atomic-write residue).
    /// </summary>
    public UiOptionsSettings Load()
    {
        lock (_ioLock)
        {
            TryDeleteStaleTemp();
            try
            {
                if (!File.Exists(_filePath)) return new UiOptionsSettings();
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize(json, s_jsonCtx.UiOptionsSettings)
                       ?? new UiOptionsSettings();
            }
            catch (Exception ex)
            {
                _log?.Warn(Constants.LogCatInit, $"UiOptionsStore: failed to load, using defaults: {ex.Message}");
                return new UiOptionsSettings();
            }
        }
    }

    /// <summary>Persist the options (atomic temp + rename; best-effort).</summary>
    public void Save(UiOptionsSettings settings)
    {
        lock (_ioLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, s_jsonCtx.UiOptionsSettings);
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Error(Constants.LogCatInit, "UiOptionsStore: failed to save", ex);
            }
        }
    }

    private void TryDeleteStaleTemp()
    {
        try
        {
            var tempPath = _filePath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>File path for testing/diagnostics.</summary>
    public string FilePath => _filePath;
}
