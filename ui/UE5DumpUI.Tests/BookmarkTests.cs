using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

public class BookmarkTests
{
    [Fact]
    public void BookmarkSlot_DefaultValues()
    {
        var slot = new BookmarkSlot { SlotIndex = 0 };

        Assert.False(slot.IsOccupied);
        Assert.Equal("", slot.Label);
        Assert.Contains("empty", slot.TooltipText);  // computed empty-slot hint
        Assert.Empty(slot.SavedBreadcrumbs);
        Assert.Equal("", slot.SavedAddress);
        Assert.Equal("", slot.SavedObjectName);
        Assert.Equal("", slot.SavedClassName);
        Assert.Equal("", slot.SavedClassAddr);
        Assert.Null(slot.SavedCachedWorld);
        Assert.Empty(slot.SavedSelectedFields);
        Assert.Null(slot.SavedTopRow);
    }

    [Fact]
    public void BookmarkSlot_DisplayNumber_IsOneBased()
    {
        var slot0 = new BookmarkSlot { SlotIndex = 0 };
        var slot3 = new BookmarkSlot { SlotIndex = 3 };

        Assert.Equal(1, slot0.DisplayNumber);
        Assert.Equal(4, slot3.DisplayNumber);
    }

    [Fact]
    public void BookmarkSlot_SetProperties_NotifiesChange()
    {
        var slot = new BookmarkSlot { SlotIndex = 0 };
        var changedProps = new List<string>();
        slot.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        slot.IsOccupied = true;
        slot.Label = "Test";

        Assert.Contains("IsOccupied", changedProps);
        Assert.Contains("Label", changedProps);
        // TooltipText is computed; flipping IsOccupied raises its change too.
        Assert.Contains("TooltipText", changedProps);
    }

    [Fact]
    public void BookmarkSlot_SetSameValue_DoesNotNotify()
    {
        var slot = new BookmarkSlot { SlotIndex = 0 };
        slot.IsOccupied = false; // Same as default

        var changedProps = new List<string>();
        slot.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        slot.IsOccupied = false; // Set same value again
        Assert.DoesNotContain("IsOccupied", changedProps);
    }

    [Fact]
    public void ViewModel_InitializesWith4EmptyBookmarkSlots()
    {
        var vm = CreateViewModel();

        Assert.Equal(4, vm.BookmarkSlots.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, vm.BookmarkSlots[i].SlotIndex);
            Assert.False(vm.BookmarkSlots[i].IsOccupied);
        }
    }

    [Fact]
    public void ToggleBookmarkSaveMode_WithNoData_DoesNothing()
    {
        var vm = CreateViewModel();
        // No breadcrumbs, no address => should not toggle
        vm.ToggleBookmarkSaveModeCommand.Execute(null);
        Assert.False(vm.IsBookmarkSaveMode);
    }

    [Fact]
    public void ToggleBookmarkSaveMode_WithData_TogglesMode()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);

        vm.ToggleBookmarkSaveModeCommand.Execute(null);
        Assert.True(vm.IsBookmarkSaveMode);

        vm.ToggleBookmarkSaveModeCommand.Execute(null);
        Assert.False(vm.IsBookmarkSaveMode);
    }

    [Fact]
    public void SaveBookmarkToSlot_WithNoData_DoesNotSave()
    {
        var vm = CreateViewModel();
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.False(slot.IsOccupied);
    }

    [Fact]
    public void SaveBookmarkToSlot_WithData_SavesState()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        var slot = vm.BookmarkSlots[1];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.True(slot.IsOccupied);
        Assert.Equal("0x12345678", slot.SavedAddress);
        Assert.Equal("TestObject", slot.SavedObjectName);
        Assert.Equal("Actor", slot.SavedClassName);
        Assert.Contains("Actor", slot.TooltipText);
        Assert.Contains("TestObject", slot.TooltipText);
        Assert.False(vm.IsBookmarkSaveMode); // Save mode cleared after save
    }

    [Fact]
    public void SaveBookmarkToSlot_TruncatesLongLabel()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm, objectName: "VeryLongObjectNameThatExceedsFourteenChars");
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.True(slot.Label.Length <= 16 + 2); // 14 chars + ".."
        Assert.EndsWith("..", slot.Label);
    }

    [Fact]
    public void ClearBookmark_ResetsSlot()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);
        Assert.True(slot.IsOccupied);

        vm.ClearBookmarkCommand.Execute(slot);

        Assert.False(slot.IsOccupied);
        Assert.Equal("", slot.Label);
        Assert.Contains("empty", slot.TooltipText);  // back to the empty-slot hint
        Assert.Empty(slot.SavedBreadcrumbs);
        Assert.Equal("", slot.SavedAddress);
        Assert.Empty(slot.SavedSelectedFields);
    }

    [Fact]
    public void ClearAllBookmarks_ClearsAllSlots()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);

        // Fill all 4 slots
        foreach (var slot in vm.BookmarkSlots)
            vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.All(vm.BookmarkSlots, s => Assert.True(s.IsOccupied));

        vm.ClearAllBookmarks();

        Assert.All(vm.BookmarkSlots, s => Assert.False(s.IsOccupied));
        Assert.False(vm.IsBookmarkSaveMode);
    }

    [Fact]
    public void ClearBookmark_NullSlot_DoesNotThrow()
    {
        var vm = CreateViewModel();
        vm.ClearBookmarkCommand.Execute(null);
        // Should not throw
    }

    [Fact]
    public void SaveBookmarkToSlot_PreservesBreadcrumbs()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm, breadcrumbCount: 3);
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.Equal(3, slot.SavedBreadcrumbs.Count);
        Assert.Equal("GWorld", slot.SavedBreadcrumbs[0].Label);
    }

    [Fact]
    public async Task LoadBookmark_InSaveMode_SavesInstead()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        vm.IsBookmarkSaveMode = true;
        var slot = vm.BookmarkSlots[2];

        // LoadBookmark should redirect to save when in save mode
        await vm.LoadBookmarkCommand.ExecuteAsync(slot);

        Assert.True(slot.IsOccupied);
        Assert.False(vm.IsBookmarkSaveMode); // Save mode cleared
    }

    [Fact]
    public async Task LoadBookmark_EmptySlot_DoesNothing()
    {
        var vm = CreateViewModel();
        var slot = vm.BookmarkSlots[0];

        // Should not throw or change state
        await vm.LoadBookmarkCommand.ExecuteAsync(slot);
    }

    [Fact]
    public void BookmarkSlot_TooltipText_Occupied_ShowsTargetAndJumpHint()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm, objectName: "PlayerPawn", className: "Pawn");
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.Contains("Pawn", slot.TooltipText);
        Assert.Contains("PlayerPawn", slot.TooltipText);
        Assert.Contains("Jump", slot.TooltipText);   // occupied slots invite a jump-back
        Assert.DoesNotContain("empty", slot.TooltipText);
    }

    [Fact]
    public void SaveBookmarkToSlot_CapturesSelectedRows()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        vm.UpdateSelectedFields(new List<LiveFieldValue>
        {
            new() { Name = "Health", Offset = 0x10 },
            new() { Name = "Mana",   Offset = 0x14 },
        });
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.Equal(2, slot.SavedSelectedFields.Count);
        Assert.Contains(slot.SavedSelectedFields, f => f.Name == "Health" && f.Offset == 0x10);
        Assert.Contains(slot.SavedSelectedFields, f => f.Name == "Mana" && f.Offset == 0x14);
    }

    [Fact]
    public void SaveBookmarkToSlot_NoSnapshot_FallsBackToSelectedFieldAnchor()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        // Single-row selection set without a grid SelectionChanged sync.
        vm.SelectedField = new LiveFieldValue { Name = "Stamina", Offset = 0x20 };
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.Single(slot.SavedSelectedFields);
        Assert.Equal("Stamina", slot.SavedSelectedFields[0].Name);
        Assert.Equal(0x20, slot.SavedSelectedFields[0].Offset);
    }

    [Fact]
    public void SaveBookmarkToSlot_CapturesViewAnchorFromView()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        // The View answers the capture request synchronously with the top row.
        vm.CaptureViewAnchor += a => a.TopRow = new BookmarkFieldRef("Stamina", 0x40);
        var slot = vm.BookmarkSlots[0];

        vm.SaveBookmarkToSlotCommand.Execute(slot);

        Assert.NotNull(slot.SavedTopRow);
        Assert.Equal("Stamina", slot.SavedTopRow!.Name);
        Assert.Equal(0x40, slot.SavedTopRow.Offset);
    }

    [Fact]
    public async Task LoadBookmark_RaisesRestoreWithSavedSelectionAndAnchor()
    {
        var vm = CreateViewModel();
        SetupViewModelWithData(vm);
        vm.UpdateSelectedFields(new List<LiveFieldValue> { new() { Name = "Gold", Offset = 0x30 } });
        vm.CaptureViewAnchor += a => a.TopRow = new BookmarkFieldRef("HeaderRow", 0x0);
        var slot = vm.BookmarkSlots[0];
        vm.SaveBookmarkToSlotCommand.Execute(slot);

        IReadOnlyList<BookmarkFieldRef>? gotSel = null;
        BookmarkFieldRef? gotTop = null;
        var raised = false;
        vm.RestoreBookmarkView += (sel, top) => { gotSel = sel; gotTop = top; raised = true; };

        await vm.LoadBookmarkCommand.ExecuteAsync(slot);

        Assert.True(raised);
        Assert.NotNull(gotSel);
        Assert.Single(gotSel!);
        Assert.Equal("Gold", gotSel![0].Name);
        Assert.NotNull(gotTop);
        Assert.Equal("HeaderRow", gotTop!.Name);
    }

    // --- PLV_game routing bug: a deeper crumb sharing the UWorld address ---

    [Fact]
    public async Task LoadBookmark_DeepCrumbAtWorldAddress_WalksInstance_NotActorList()
    {
        // Regression: navigating PersistentLevel -> OwningWorld lands on a crumb
        // whose address equals the UWorld address. Loading that bookmark must walk
        // it as an instance, NOT swap in the GWorld actor-list view ("PLV_game").
        var dump = new StubDumpService();
        dump.RegisterStruct("0xA8B0", new InstanceWalkResult
        {
            Name = "PLV_game", ClassName = "World", Address = "0xA8B0",
            Fields = new List<LiveFieldValue> { new() { Name = "OwningGameInstance", Offset = 0x10 } },
        });
        var vm = new LiveWalkerViewModel(dump, new MockLoggingService(),
            new MockPlatformService(Path.GetTempPath()));

        var slot = vm.BookmarkSlots[0];
        slot.SavedCachedWorld = new WorldWalkResult
        {
            WorldAddr = "0xA8B0", WorldName = "PLV_game",
            LevelAddr = "0x4500", LevelName = "PersistentLevel", LevelOffset = 0x30,
        };
        slot.SavedBreadcrumbs = new List<BreadcrumbItem>
        {
            new() { Address = "0xA8B0", Label = "GWorld", FieldName = "GWorld", IsPointerDeref = true },
            new() { Address = "0x4500", Label = "PersistentLevel", FieldName = "PersistentLevel", IsPointerDeref = true, FieldOffset = 0x30 },
            new() { Address = "0xA8B0", Label = "OwningWorld", FieldName = "OwningWorld", IsPointerDeref = true, FieldOffset = 0xB8 },
        };
        slot.SavedAddress = "0xA8B0";
        slot.IsOccupied = true;

        await vm.LoadBookmarkCommand.ExecuteAsync(slot);

        Assert.Contains(vm.Fields, f => f.Name == "OwningGameInstance");  // instance walk
        Assert.Equal("World", vm.CurrentClassName);
        Assert.DoesNotContain(vm.Fields, f => f.Name == "PersistentLevel"); // actor-list absent
    }

    [Fact]
    public async Task LoadBookmark_GWorldRoot_ShowsActorList()
    {
        // Control: the genuine GWorld root (FieldName=="GWorld", sole crumb) DOES
        // restore the synthetic actor-list view.
        var dump = new StubDumpService();
        var vm = new LiveWalkerViewModel(dump, new MockLoggingService(),
            new MockPlatformService(Path.GetTempPath()));

        var slot = vm.BookmarkSlots[0];
        slot.SavedCachedWorld = new WorldWalkResult
        {
            WorldAddr = "0xA8B0", WorldName = "PLV_game",
            LevelAddr = "0x4500", LevelName = "PersistentLevel", LevelOffset = 0x30,
        };
        slot.SavedBreadcrumbs = new List<BreadcrumbItem>
        {
            new() { Address = "0xA8B0", Label = "GWorld", FieldName = "GWorld", IsPointerDeref = true },
        };
        slot.SavedAddress = "0xA8B0";
        slot.IsOccupied = true;

        await vm.LoadBookmarkCommand.ExecuteAsync(slot);

        Assert.Equal("UWorld", vm.CurrentClassName);
        Assert.Contains(vm.Fields, f => f.Name == "PersistentLevel");  // actor-list signature
    }

    // --- Helpers ---

    private static LiveWalkerViewModel CreateViewModel()
    {
        var dump = new StubDumpService();
        var log = new MockLoggingService();
        var platform = new MockPlatformService(Path.GetTempPath());
        return new LiveWalkerViewModel(dump, log, platform);
    }

    private static void SetupViewModelWithData(LiveWalkerViewModel vm,
        string objectName = "TestObject",
        string className = "Actor",
        string address = "0x12345678",
        int breadcrumbCount = 1)
    {
        // Use reflection-free approach: set observable properties via their public setters
        vm.CurrentObjectName = objectName;
        vm.CurrentClassName = className;
        vm.CurrentAddress = address;
        vm.HasData = true;

        vm.Breadcrumbs.Clear();
        vm.Breadcrumbs.Add(new BreadcrumbItem
        {
            Address = "0xGWorld",
            Label = "GWorld",
            IsPointerDeref = true,
            FieldOffset = 0,
            FieldName = "GWorld",
        });

        for (int i = 1; i < breadcrumbCount; i++)
        {
            vm.Breadcrumbs.Add(new BreadcrumbItem
            {
                Address = $"0x{i:X8}",
                Label = $"Object{i}",
                IsPointerDeref = true,
                FieldOffset = i * 8,
                FieldName = $"Field{i}",
            });
        }
    }
}
