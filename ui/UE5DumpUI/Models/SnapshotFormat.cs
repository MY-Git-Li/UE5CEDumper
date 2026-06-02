namespace UE5DumpUI.Models;

/// <summary>Byte-size formatting for the snapshot usage / quota UI. Pure.</summary>
public static class SnapshotFormat
{
    private static readonly string[] s_units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Human-readable size, e.g. 0 → "0 B", 1536 → "1.5 KB",
    /// 44040192 → "42.0 MB". Whole bytes have no decimal; larger units show one.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < s_units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {s_units[u]}" : $"{v:0.0} {s_units[u]}";
    }
}
