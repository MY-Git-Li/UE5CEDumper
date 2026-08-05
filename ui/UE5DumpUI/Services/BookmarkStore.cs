using System;
using System.IO;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Persists Live-Walker bookmarks PER GAME to
/// %LOCALAPPDATA%\UE5CEDumper\Bookmarks\bookmarks.{peHash}.json.
/// One file per game (keyed by PE hash) so a game's bookmarks are isolated and a
/// "clear all" is a single file delete — same per-game-file convention as the snapshot
/// DB / denylist. Source-gen JSON (AOT-safe), atomic temp+rename, swallow-and-log on
/// failure, defaults on missing/corrupt. Synchronous (a handful of tiny records).
///
/// <para>The <c>Bookmarks\</c> subfolder and the one-time move of files still at the old
/// flat root are <see cref="AppDataFolderMaintenance"/>'s, running from this constructor
/// so no caller can read the folder before it has been migrated.</para>
///
/// <para><b>These files are NEVER aged out.</b> The snapshot DBs next door are swept at
/// <see cref="Constants.DataMaxAgeDays"/> because they are the thing that grows to
/// gigabytes; a bookmark file is a few KB of HAND-PLACED navigation the user cannot
/// regenerate by replaying anything. The disk argument that justifies the snapshot sweep
/// simply does not apply, and the cost of being wrong is not symmetric. Clearing them
/// stays an explicit action (<see cref="Delete"/>, the Live Walker's "clear all").</para>
///
/// See <see cref="BookmarkFile"/> for what is / isn't persisted.
/// </summary>
public sealed class BookmarkStore
{
    private readonly string _dir;
    private readonly ILoggingService? _log;
    private readonly object _ioLock = new();

    private static readonly BookmarkJsonContext s_jsonCtx = BookmarkJsonContext.Default;

    public BookmarkStore(IPlatformService platform, ILoggingService? log = null)
    {
        _log = log;
        _dir = AppDataFolderMaintenance.Prepare(
            Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName),
            Constants.BookmarkSubFolder,
            Constants.BookmarkFilePrefix,
            // NO age sweep, deliberately — see the class doc. Migration only.
            maxAgeDays: 0,
            log);
    }

    private string PathFor(string peHash) =>
        Path.Combine(_dir, $"{Constants.BookmarkFilePrefix}.{peHash}.json");

    /// <summary>
    /// Load a game's bookmarks, or a fresh empty file on missing / unreadable / corrupt.
    /// Returns an empty file (no-op) when <paramref name="peHash"/> is empty.
    /// </summary>
    public BookmarkFile Load(string peHash)
    {
        if (string.IsNullOrEmpty(peHash)) return new BookmarkFile();
        lock (_ioLock)
        {
            var path = PathFor(peHash);
            TryDeleteStaleTemp(path);
            try
            {
                if (!File.Exists(path)) return new BookmarkFile { PeHash = peHash };
                var json = File.ReadAllText(path);
                // No TouchUsed here: nothing sweeps this folder, so a last-used stamp
                // would be a metadata write on every load that no reader consumes.
                return JsonSerializer.Deserialize(json, s_jsonCtx.BookmarkFile)
                       ?? new BookmarkFile { PeHash = peHash };
            }
            catch (Exception ex)
            {
                _log?.Warn(Constants.LogCatView, $"BookmarkStore: failed to load {peHash}, using empty: {ex.Message}");
                return new BookmarkFile { PeHash = peHash };
            }
        }
    }

    /// <summary>Persist a game's bookmarks (atomic temp + rename). No-op on empty PE hash.</summary>
    public void Save(string peHash, BookmarkFile file)
    {
        if (string.IsNullOrEmpty(peHash)) return;
        lock (_ioLock)
        {
            try
            {
                file.PeHash = peHash;
                var json = JsonSerializer.Serialize(file, s_jsonCtx.BookmarkFile);
                var path = PathFor(peHash);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Error(Constants.LogCatView, "BookmarkStore: failed to save", ex);
            }
        }
    }

    /// <summary>Delete a game's bookmark file (user "clear all"). No-op on empty PE hash.</summary>
    public void Delete(string peHash)
    {
        if (string.IsNullOrEmpty(peHash)) return;
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(peHash);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log?.Error(Constants.LogCatView, "BookmarkStore: failed to delete", ex);
            }
        }
    }

    private static void TryDeleteStaleTemp(string path)
    {
        try { var t = path + ".tmp"; if (File.Exists(t)) File.Delete(t); }
        catch { /* best-effort */ }
    }

    /// <summary>File path for a game's bookmarks (testing/diagnostics).</summary>
    public string FilePathFor(string peHash) => PathFor(peHash);
}
