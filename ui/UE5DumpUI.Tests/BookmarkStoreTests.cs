using System.IO;
using UE5DumpUI;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the BookmarkStore persistence contract: per-game file keyed by PE hash,
/// round-trips the full spine (breadcrumbs + selection + top row), defaults on
/// missing/corrupt, no-ops on an empty PE hash, and deletes on "clear all". Reuses
/// <see cref="MockPlatformService"/> from AobUsageServiceTests.
/// </summary>
public class BookmarkStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MockPlatformService _platform;
    private const string Pe = "1A2B3C4D5E6F7080";

    public BookmarkStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UE5DumpBmTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _platform = new MockPlatformService(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static BookmarkFile SampleFile() => new()
    {
        PeHash = Pe,
        Slots =
        {
            new PersistedBookmark
            {
                SlotIndex = 2,
                Label = "BP_Player",
                SavedObjectName = "BP_Player_C_0",
                SavedClassName = "BP_Player_C",
                SavedAddress = "0x12340000",
                SavedClassAddr = "0x9000",
                Breadcrumbs =
                {
                    new PersistedCrumb { Address = "0xA8B0", Label = "GWorld", FieldName = "GWorld", IsPointerDeref = true },
                    new PersistedCrumb { Address = "0x12340000", Label = "BP_Player", FieldName = "PlayerPawn", FieldOffset = 0x2C8, TargetClassName = "BP_Player_C", IsPointerDeref = true },
                },
                SelectedFields =
                {
                    new PersistedFieldRef { Name = "Health", Offset = 0x40 },
                    new PersistedFieldRef { Name = "Stamina", Offset = 0x44 },
                },
                TopRow = new PersistedFieldRef { Name = "Health", Offset = 0x40 },
            },
        },
    };

    [Fact]
    public void FilePath_IsPerGameUnderAppData()
    {
        var store = new BookmarkStore(_platform);
        Assert.Equal(
            Path.Combine(_tempDir, Constants.LogFolderName, $"{Constants.BookmarkFilePrefix}.{Pe}.json"),
            store.FilePathFor(Pe));
    }

    [Fact]
    public void Load_NoFile_ReturnsEmpty_AndDoesNotCreateFile()
    {
        var store = new BookmarkStore(_platform);
        var file = store.Load(Pe);
        Assert.Empty(file.Slots);
        Assert.False(File.Exists(store.FilePathFor(Pe)));
    }

    [Fact]
    public void RoundTrip_PreservesSpineSelectionAndTopRow()
    {
        var store = new BookmarkStore(_platform);
        store.Save(Pe, SampleFile());

        var r = new BookmarkStore(_platform).Load(Pe);   // simulate restart
        var slot = Assert.Single(r.Slots);
        Assert.Equal(2, slot.SlotIndex);
        Assert.Equal("BP_Player", slot.Label);
        Assert.Equal("BP_Player_C", slot.SavedClassName);
        Assert.Equal("0x12340000", slot.SavedAddress);

        Assert.Equal(2, slot.Breadcrumbs.Count);
        Assert.Equal("GWorld", slot.Breadcrumbs[0].FieldName);
        Assert.Equal("PlayerPawn", slot.Breadcrumbs[1].FieldName);
        Assert.Equal(0x2C8, slot.Breadcrumbs[1].FieldOffset);

        Assert.Equal(2, slot.SelectedFields.Count);
        Assert.Equal("Stamina", slot.SelectedFields[1].Name);
        Assert.Equal(0x44, slot.SelectedFields[1].Offset);

        Assert.NotNull(slot.TopRow);
        Assert.Equal("Health", slot.TopRow!.Name);
        Assert.Equal(0x40, slot.TopRow.Offset);
    }

    [Fact]
    public void Save_IsPerGame_DoesNotBleedAcrossPeHashes()
    {
        var store = new BookmarkStore(_platform);
        store.Save(Pe, SampleFile());

        Assert.Empty(store.Load("OTHERHASH00000000").Slots);   // different game → empty
        Assert.Single(store.Load(Pe).Slots);
    }

    [Fact]
    public void EmptyPeHash_IsNoOp()
    {
        var store = new BookmarkStore(_platform);
        store.Save("", SampleFile());                 // must not throw / write
        Assert.Empty(store.Load("").Slots);           // must not throw
        Assert.False(File.Exists(store.FilePathFor("")));
    }

    [Fact]
    public void Delete_RemovesTheGameFile()
    {
        var store = new BookmarkStore(_platform);
        store.Save(Pe, SampleFile());
        Assert.True(File.Exists(store.FilePathFor(Pe)));

        store.Delete(Pe);
        Assert.False(File.Exists(store.FilePathFor(Pe)));
        Assert.Empty(store.Load(Pe).Slots);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var store = new BookmarkStore(_platform);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePathFor(Pe))!);
        File.WriteAllText(store.FilePathFor(Pe), "}{ not json");

        Assert.Empty(store.Load(Pe).Slots);
    }

    [Fact]
    public void Load_DeletesStaleTempFile()
    {
        var store = new BookmarkStore(_platform);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePathFor(Pe))!);
        var temp = store.FilePathFor(Pe) + ".tmp";
        File.WriteAllText(temp, "orphaned");

        store.Load(Pe);
        Assert.False(File.Exists(temp));
    }
}
