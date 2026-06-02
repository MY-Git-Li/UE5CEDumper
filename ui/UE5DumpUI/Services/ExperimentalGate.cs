using System;
using System.IO;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Persists the experimental-features opt-in to a tiny JSON file:
/// %LOCALAPPDATA%\UE5CEDumper\experimental.json. Modelled on
/// <see cref="AobUsageService"/>'s persistence pattern (source-generated
/// JSON, atomic temp-then-rename writes, swallow-and-log on failure).
/// </summary>
public sealed class ExperimentalGate : IExperimentalGate
{
    private readonly string _filePath;
    private readonly ILoggingService? _log;
    private bool _enabled;

    // Source-generated JSON context (reflection-based JSON is disabled in trimmed/AOT builds)
    private static readonly ExperimentalSettingsJsonContext s_jsonCtx = ExperimentalSettingsJsonContext.Default;

    public event EventHandler? Changed;

    public ExperimentalGate(IPlatformService platform, ILoggingService? log = null)
    {
        _log = log;
        var dir = Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, Constants.ExperimentalSettingsFile);
        _enabled = Load();
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return false;
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize(json, s_jsonCtx.ExperimentalSettings);
            return settings?.Enabled ?? false;
        }
        catch (Exception ex)
        {
            _log?.Warn(Constants.LogCatInit, $"ExperimentalGate: failed to load, defaulting off: {ex.Message}");
            return false;
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new ExperimentalSettings { Enabled = _enabled }, s_jsonCtx.ExperimentalSettings);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            _log?.Info(Constants.LogCatInit, $"ExperimentalGate: experimental features {(_enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            _log?.Error(Constants.LogCatInit, "ExperimentalGate: failed to save", ex);
        }
    }

    /// <summary>File path for testing/diagnostics.</summary>
    public string FilePath => _filePath;
}
