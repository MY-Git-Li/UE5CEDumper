using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UE5DumpUI.Models;

/// <summary>
/// A logical drive available for the generic (non-Steam) UE-game scan.
/// Extends ObservableObject so the drive-selection checkbox (IsSelected) is
/// reflected in the UI immediately. All fields except IsSelected are stamped
/// once by the platform layer (see IPlatformService.GetLogicalDrives) and are
/// immutable thereafter.
/// </summary>
public sealed partial class DriveDescriptor : ObservableObject
{
    /// <summary>Root directory the walk starts from, e.g. "C:\".</summary>
    public string Root { get; init; } = "";

    /// <summary>Uppercase drive letter, e.g. 'C'.</summary>
    public char Letter { get; init; }

    /// <summary>Volume label, or null/empty when unavailable.</summary>
    public string? Label { get; init; }

    /// <summary>Drive type (Fixed / Removable).</summary>
    public DriveType Type { get; init; }

    /// <summary>
    /// Physical disk number backing this drive (from
    /// IOCTL_STORAGE_GET_DEVICE_NUMBER), or null when it can't be determined
    /// (spanned/striped/network/virtual). Drives sharing a number live on one
    /// physical disk and are scanned sequentially; null → its own scan group.
    /// </summary>
    public int? PhysicalDiskNumber { get; init; }

    /// <summary>Total capacity in bytes (0 when unreadable).</summary>
    public long TotalBytes { get; init; }

    /// <summary>Free space in bytes (0 when unreadable).</summary>
    public long FreeBytes { get; init; }

    /// <summary>Whether the user picked this drive for the scan.</summary>
    [ObservableProperty] private bool _isSelected;

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";

    /// <summary>One-line label shown next to the selection checkbox.</summary>
    public string Display
    {
        get
        {
            string label = string.IsNullOrWhiteSpace(Label) ? "" : $"  {Label}";
            string disk = PhysicalDiskNumber is int n ? $"  [Disk {n}]" : "  [Disk ?]";
            return $"{Letter}:{label}{disk}   {Gb(FreeBytes)} free / {Gb(TotalBytes)}";
        }
    }
}
