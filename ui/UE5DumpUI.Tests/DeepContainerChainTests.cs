using System.Collections.Generic;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the deeply-nested container reach (build 1196): the
/// <see cref="ContainerMatch"/> nested-chain model + display path, and the
/// flattened drill path the Live Walker walks to land on a value buried in
/// separately-allocated nested containers.
/// </summary>
public class DeepContainerChainTests
{
    // --- ContainerMatch.DisplayPath / DeepestIntraOffset / IsDeeplyNested ---

    [Fact]
    public void DisplayPath_ShallowMatch_UnchangedShape()
    {
        // A plain 1-level struct-element value (e.g. SaveSlotList[1].GP at +0x4D8).
        var m = new ContainerMatch
        {
            OwnerName    = "BP_LifeSaveData_C",
            FieldName    = "SaveSlotList",
            ElementIndex = 1,
            IntraOffset  = 0x4D8,
            InnerType    = "StructProperty",
        };

        Assert.False(m.IsDeeplyNested);
        Assert.Equal(0x4D8, m.DeepestIntraOffset);
        Assert.Equal("BP_LifeSaveData_C.SaveSlotList[1]+0x4D8", m.DisplayPath);
    }

    [Fact]
    public void DisplayPath_ShallowLeafElement_NoIntraSuffix()
    {
        // A scalar leaf element (the element IS the value) → no +0xK suffix.
        var m = new ContainerMatch
        {
            OwnerName    = "BP_Inventory_C",
            FieldName    = "Gold",
            ElementIndex = 3,
            IntraOffset  = 0,
        };

        Assert.Equal("BP_Inventory_C.Gold[3]", m.DisplayPath);
    }

    [Fact]
    public void DisplayPath_DeeplyNested_SpansFullChain()
    {
        // The SEED repro: SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[42].
        var m = new ContainerMatch
        {
            OwnerName    = "BP_LifeSaveData_C",
            FieldName    = "SaveSlotList",
            ElementIndex = 1,
            IntraOffset  = 0,        // outermost element is a struct we descended into
            InnerType    = "StructProperty",
            NestedChain  = new List<ContainerHop>
            {
                new() { FieldName = "MsTuneData.MsTunes", ElementIndex = 0, IntraOffset = 0, MapValueSide = true },
                new() { FieldName = "WeaponTuneList",      ElementIndex = 0, IntraOffset = 0 },
                new() { FieldName = "Tunes",               ElementIndex = 42, IntraOffset = 0 },
            },
        };

        Assert.True(m.IsDeeplyNested);
        Assert.Equal(0, m.DeepestIntraOffset);
        Assert.Equal(
            "BP_LifeSaveData_C.SaveSlotList[1].MsTuneData.MsTunes[0].WeaponTuneList[0].Tunes[42]",
            m.DisplayPath);
    }

    [Fact]
    public void DisplayPath_DeeplyNested_UsesDeepestIntraOffset()
    {
        // Deepest hop lands inside a struct element at +0x10 (not a leaf element).
        var m = new ContainerMatch
        {
            OwnerName    = "Save",
            FieldName    = "Slots",
            ElementIndex = 2,
            IntraOffset  = 0,
            NestedChain  = new List<ContainerHop>
            {
                new() { FieldName = "Items", ElementIndex = 5, IntraOffset = 0x10 },
            },
        };

        Assert.Equal(0x10, m.DeepestIntraOffset);
        Assert.Equal("Save.Slots[2].Items[5]+0x10", m.DisplayPath);
    }

    [Fact]
    public void DisplayPath_Deepest_NoteFromDeepestHop()
    {
        var m = new ContainerMatch
        {
            OwnerName    = "Save",
            FieldName    = "Slots",
            ElementIndex = 0,
            Note         = "",   // outermost is solid
            NestedChain  = new List<ContainerHop>
            {
                new() { FieldName = "Items", ElementIndex = 9, IntraOffset = 0, Note = "freed" },
            },
        };

        Assert.Equal("Save.Slots[0].Items[9] (freed)", m.DisplayPath);
    }

    // --- LiveWalkerViewModel.BuildContainerDrillPath ---

    [Fact]
    public void BuildDrillPath_ShallowMatch_SingleHop()
    {
        var m = new ContainerMatch
        {
            FieldName    = "SaveSlotList",
            ElementIndex = 1,
            IntraOffset  = 0x4D8,
        };

        var hops = LiveWalkerViewModel.BuildContainerDrillPath(m);

        Assert.Single(hops);
        Assert.Equal(("SaveSlotList", 1, 0x4D8), hops[0]);
    }

    [Fact]
    public void BuildDrillPath_DeeplyNested_OutermostFirstThenChain()
    {
        var m = new ContainerMatch
        {
            FieldName    = "SaveSlotList",
            ElementIndex = 1,
            IntraOffset  = 0,
            NestedChain  = new List<ContainerHop>
            {
                new() { FieldName = "MsTuneData.MsTunes", ElementIndex = 0,  IntraOffset = 0 },
                new() { FieldName = "WeaponTuneList",      ElementIndex = 0,  IntraOffset = 0 },
                new() { FieldName = "Tunes",               ElementIndex = 42, IntraOffset = 0 },
            },
        };

        var hops = LiveWalkerViewModel.BuildContainerDrillPath(m);

        Assert.Equal(4, hops.Count);
        Assert.Equal(("SaveSlotList", 1, 0), hops[0]);
        Assert.Equal(("MsTuneData.MsTunes", 0, 0), hops[1]);
        Assert.Equal(("WeaponTuneList", 0, 0), hops[2]);
        Assert.Equal(("Tunes", 42, 0), hops[3]);   // deepest = the value's element
    }

    // --- LiveWalkerViewModel.TryParseStructArrayInner (Value Search 🌍 deep-drill) ---

    [Fact]
    public void TryParseStructArrayInner_DirectInnerField()
    {
        Assert.True(LiveWalkerViewModel.TryParseStructArrayInner(
            "SaveSlotList[1].GP", out var arr, out var idx, out var inner));
        Assert.Equal("SaveSlotList", arr);
        Assert.Equal(1, idx);
        Assert.Equal(new[] { "GP" }, inner);
    }

    [Fact]
    public void TryParseStructArrayInner_NestedArrayAndInnerStruct()
    {
        Assert.True(LiveWalkerViewModel.TryParseStructArrayInner(
            "Save.SaveSlotList[2].MsTuneData.GP2", out var arr, out var idx, out var inner));
        Assert.Equal("Save.SaveSlotList", arr);   // array path may itself be dotted
        Assert.Equal(2, idx);
        Assert.Equal(new[] { "MsTuneData", "GP2" }, inner);
    }

    [Theory]
    [InlineData("Items[3]")]      // leaf-array element (ends in "]"), not struct-array-inner
    [InlineData("Health")]        // plain field
    [InlineData("")]              // empty
    [InlineData("[0].GP")]        // no array name before "["
    [InlineData("Cargo[].GP")]    // empty index
    public void TryParseStructArrayInner_RejectsNonStructArrayInner(string name)
    {
        Assert.False(LiveWalkerViewModel.TryParseStructArrayInner(name, out _, out _, out _));
    }
}
