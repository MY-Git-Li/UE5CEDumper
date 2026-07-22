using System;
using System.IO;
using System.Text;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Persists the Teleport coordinate library PER GAME to
/// %LOCALAPPDATA%\UE5CEDumper\teleport-coords.{module}.json.
///
/// Structural clone of <see cref="BookmarkStore"/> — sync, lock-guarded, source-gen
/// JSON, atomic temp+rename, swallow-and-log with empty defaults — with three
/// deliberate deviations, all documented in docs/teleport-coord-library-spec.md §8:
///
///  1. Keyed by the EXE MODULE NAME, not the PE hash (D1), so a game patch does not
///     orphan a hand-curated list.
///  2. <see cref="CoordinateLibraryJsonContext"/> does NOT use
///     <c>WhenWritingDefault</c>, so a legitimate 0.0 coordinate is written.
///  3. Keeps a rolling <c>.bak</c> of the previous good file, and
///     <see cref="SavePreImportBackup"/> writes a distinct <c>.preimport.bak</c>
///     before an import commits. This is hand-curated data; a crash-on-write or a
///     botched Replace-import is by far the worst failure mode in this feature, and
///     it is the one thing <see cref="BookmarkStore"/> does not defend against.
/// </summary>
public sealed class CoordinateLibraryStore
{
    private readonly string _dir;
    private readonly ILoggingService? _log;
    private readonly object _ioLock = new();

    private static readonly CoordinateLibraryJsonContext s_jsonCtx =
        CoordinateLibraryJsonContext.Default;

    public CoordinateLibraryStore(IPlatformService platform, ILoggingService? log = null)
    {
        _log = log;
        _dir = Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Turn a raw module name ("MyGame-Win64-Shipping.exe") into a file-name-safe key.
    /// Case-insensitive by lowering, so a differently-cased report of the same module
    /// resolves to the same file. Returns "" for an unusable module name, which makes
    /// every store operation a no-op.
    /// </summary>
    public static string KeyFor(string? moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return "";
        var name = moduleName!.Trim();
        // Drop a trailing .exe/.dll so "MyGame.exe" and "MyGame" agree.
        int dot = name.LastIndexOf('.');
        if (dot > 0 &&
            (name.AsSpan(dot).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
             name.AsSpan(dot).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            name = name[..dot];
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_'
                ? char.ToLowerInvariant(c)
                : '_');
        }
        var key = sb.ToString().Trim('_');
        return key.Length > 96 ? key[..96] : key;
    }

    private string PathFor(string key) =>
        Path.Combine(_dir, $"{Constants.CoordLibraryFilePrefix}.{key}.json");

    /// <summary>File path for a game's library (testing/diagnostics).</summary>
    public string FilePathFor(string key) => PathFor(key);

    /// <summary>
    /// Load a game's library, or a fresh empty file on missing / unreadable / corrupt.
    /// Returns an empty file (no-op) when <paramref name="key"/> is empty.
    ///
    /// On a corrupt main file the <c>.bak</c> is tried before giving up — the whole
    /// point of keeping it.
    /// </summary>
    public CoordinateLibraryFile Load(string key)
    {
        if (string.IsNullOrEmpty(key)) return new CoordinateLibraryFile();
        lock (_ioLock)
        {
            var path = PathFor(key);
            TryDeleteStaleTemp(path);

            var loaded = TryRead(path);
            if (loaded != null) return loaded;

            var fromBak = TryRead(path + ".bak");
            if (fromBak != null)
            {
                _log?.Warn(Constants.LogCatView,
                    $"CoordinateLibraryStore: {key} main file unreadable, recovered from .bak " +
                    $"({fromBak.Entries.Count} entries)");
                return fromBak;
            }

            return new CoordinateLibraryFile { Module = key };
        }
    }

    private CoordinateLibraryFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize(json, s_jsonCtx.CoordinateLibraryFile);
            if (file == null) return null;
            file.Entries ??= new List<CoordEntry>();
            return file;
        }
        catch (Exception ex)
        {
            _log?.Warn(Constants.LogCatView,
                $"CoordinateLibraryStore: failed to read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persist a game's library: atomic temp + rename, rolling the previous good file
    /// to <c>.bak</c> first. No-op on an empty key.
    /// </summary>
    public void Save(string key, CoordinateLibraryFile file)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_ioLock)
        {
            try
            {
                file.Module = key;
                var json = JsonSerializer.Serialize(file, s_jsonCtx.CoordinateLibraryFile);
                var path = PathFor(key);
                var temp = path + ".tmp";

                File.WriteAllText(temp, json);
                // Roll the previous good file aside BEFORE the rename, so a torn write
                // never leaves us with neither.
                TryRollToBackup(path, path + ".bak");
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Error(Constants.LogCatView, "CoordinateLibraryStore: failed to save", ex);
            }
        }
    }

    /// <summary>
    /// Snapshot the current on-disk library to <c>.preimport.bak</c>. Called
    /// immediately before an import commits so a botched Replace is recoverable —
    /// distinct from the rolling <c>.bak</c>, which the very next Save would overwrite.
    /// Returns the backup path, or "" when nothing was backed up.
    /// </summary>
    public string SavePreImportBackup(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        lock (_ioLock)
        {
            var path = PathFor(key);
            var bak = path + ".preimport.bak";
            return TryRollToBackup(path, bak) ? bak : "";
        }
    }

    /// <summary>Delete a game's library file (user "clear all"). Leaves the backups.</summary>
    public void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(key);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log?.Error(Constants.LogCatView, "CoordinateLibraryStore: failed to delete", ex);
            }
        }
    }

    private bool TryRollToBackup(string path, string bak)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Copy(path, bak, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn(Constants.LogCatView,
                $"CoordinateLibraryStore: backup to {Path.GetFileName(bak)} failed: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteStaleTemp(string path)
    {
        try { var t = path + ".tmp"; if (File.Exists(t)) File.Delete(t); }
        catch { /* best-effort */ }
    }
}
