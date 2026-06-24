using System;
using System.IO;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Persists Live-Walker bookmarks PER GAME to %LOCALAPPDATA%\UE5CEDumper\bookmarks.{peHash}.json.
/// One file per game (keyed by PE hash) so a game's bookmarks are isolated and a
/// "clear all" is a single file delete — same per-game-file convention as the snapshot
/// DB / denylist. Source-gen JSON (AOT-safe), atomic temp+rename, swallow-and-log on
/// failure, defaults on missing/corrupt. Synchronous (a handful of tiny records).
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
        _dir = Path.Combine(platform.GetAppDataPath(), Constants.LogFolderName);
        Directory.CreateDirectory(_dir);
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
