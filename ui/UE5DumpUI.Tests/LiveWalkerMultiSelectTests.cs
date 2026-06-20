using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the Copy CE Field(s) multi-selection behaviour:
/// - <see cref="LiveWalkerViewModel.FilterContainerToElement"/> retains
///   multiple matching elements (Array / Map / Set / DataTable) and falls
///   back to the whole container when no selection has a parseable sparse
///   index.
/// - End-to-end via <see cref="CeXmlExportService.GenerateHierarchicalXml"/>
///   to confirm multiple scalar selections emit a single CE root with N
///   leaves under the same pointer chain.
/// </summary>
public class LiveWalkerMultiSelectTests
{
    private static BreadcrumbItem MakeBc(string addr, string label,
        string fieldName = "", bool isPointer = false, int offset = 0,
        bool isContainerView = false)
    {
        return new BreadcrumbItem
        {
            Address = addr,
            Label = label,
            FieldName = string.IsNullOrEmpty(fieldName) ? label : fieldName,
            FieldOffset = offset,
            IsPointerDeref = isPointer,
            IsContainerView = isContainerView,
        };
    }

    private static LiveFieldValue MakeSynthetic(int sparseIndex, string suffix = "")
    {
        return new LiveFieldValue
        {
            Name = string.IsNullOrEmpty(suffix) ? $"[{sparseIndex}]" : $"[{sparseIndex}] {suffix}",
        };
    }

    // ------------------------------------------------------------------
    // FilterContainerToElement — Array
    // ------------------------------------------------------------------

    [Fact]
    public void FilterContainer_ArrayMultiElement_KeepsAllSelectedIndices()
    {
        var container = new LiveFieldValue
        {
            Name = "Inventory",
            TypeName = "ArrayProperty",
            ArrayCount = 5,
            ArrayInnerType = "IntProperty",
            ArrayElemSize = 4,
            ArrayElements = new List<ArrayElementValue>
            {
                new() { Index = 0, Value = "10" },
                new() { Index = 1, Value = "20" },
                new() { Index = 2, Value = "30" },
                new() { Index = 3, Value = "40" },
                new() { Index = 4, Value = "50" },
            }
        };

        // Select rows [1] and [3]
        var selected = new List<LiveFieldValue> { MakeSynthetic(1), MakeSynthetic(3) };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.ArrayElements);
        Assert.Equal(2, filtered.ArrayElements!.Count);
        Assert.Contains(filtered.ArrayElements, e => e.Index == 1);
        Assert.Contains(filtered.ArrayElements, e => e.Index == 3);
        // Container metadata preserved (count, type, etc.)
        Assert.Equal(5, filtered.ArrayCount);
        Assert.Equal("IntProperty", filtered.ArrayInnerType);
    }

    [Fact]
    public void FilterContainer_ArraySingleSelection_RetainsOnlyThatElement()
    {
        // Backward compat: single-selection still works
        var container = new LiveFieldValue
        {
            Name = "Inventory",
            TypeName = "ArrayProperty",
            ArrayCount = 3,
            ArrayInnerType = "FloatProperty",
            ArrayElemSize = 4,
            ArrayElements = new List<ArrayElementValue>
            {
                new() { Index = 0, Value = "1.0" },
                new() { Index = 1, Value = "2.0" },
                new() { Index = 2, Value = "3.0" },
            }
        };

        var selected = new List<LiveFieldValue> { MakeSynthetic(2) };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.ArrayElements);
        Assert.Single(filtered.ArrayElements!);
        Assert.Equal(2, filtered.ArrayElements![0].Index);
    }

    // ------------------------------------------------------------------
    // FilterContainerToElement — Map / Set / DataTable
    // ------------------------------------------------------------------

    [Fact]
    public void FilterContainer_MapMultiElement_KeepsAllSelectedIndices()
    {
        var container = new LiveFieldValue
        {
            Name = "Stats",
            TypeName = "MapProperty",
            MapCount = 4,
            MapKeyType = "NameProperty",
            MapValueType = "IntProperty",
            MapKeySize = 8,
            MapValueSize = 4,
            MapElements = new List<ContainerElementValue>
            {
                new() { Index = 0, Key = "HP",     Value = "100" },
                new() { Index = 1, Key = "MP",     Value = "50" },
                new() { Index = 2, Key = "Stam",   Value = "75" },
                new() { Index = 3, Key = "Energy", Value = "20" },
            }
        };

        // User selects [0] HP and [2] Stam
        var selected = new List<LiveFieldValue>
        {
            MakeSynthetic(0, "HP"),
            MakeSynthetic(2, "Stam"),
        };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.MapElements);
        Assert.Equal(2, filtered.MapElements!.Count);
        Assert.Contains(filtered.MapElements, e => e.Index == 0 && e.Key == "HP");
        Assert.Contains(filtered.MapElements, e => e.Index == 2 && e.Key == "Stam");
        // Display count remains the full map count so the user sees the
        // header description "Map: 4" instead of "Map: 2".
        Assert.Equal(4, filtered.MapCount);
    }

    [Fact]
    public void FilterContainer_SetMultiElement_KeepsAllSelectedIndices()
    {
        var container = new LiveFieldValue
        {
            Name = "Tags",
            TypeName = "SetProperty",
            SetCount = 3,
            SetElemType = "IntProperty",
            SetElemSize = 4,
            SetElements = new List<ContainerElementValue>
            {
                new() { Index = 0, Key = "7" },
                new() { Index = 1, Key = "13" },
                new() { Index = 2, Key = "42" },
            }
        };

        var selected = new List<LiveFieldValue> { MakeSynthetic(0), MakeSynthetic(2) };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.SetElements);
        Assert.Equal(2, filtered.SetElements!.Count);
        Assert.Contains(filtered.SetElements, e => e.Index == 0);
        Assert.Contains(filtered.SetElements, e => e.Index == 2);
    }

    [Fact]
    public void FilterContainer_DataTableMultiRow_KeepsAllSelectedIndices()
    {
        var container = new LiveFieldValue
        {
            Name = "WeaponTable",
            TypeName = "DataTable",
            DataTableRowCount = 4,
            DataTableStructName = "FWeaponRow",
            DataTableRowData = new List<DataTableRowInfo>
            {
                new() { SparseIndex = 0, RowName = "Sword" },
                new() { SparseIndex = 1, RowName = "Bow" },
                new() { SparseIndex = 2, RowName = "Staff" },
                new() { SparseIndex = 3, RowName = "Dagger" },
            }
        };

        var selected = new List<LiveFieldValue>
        {
            MakeSynthetic(1, "Bow"),
            MakeSynthetic(3, "Dagger"),
        };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.DataTableRowData);
        Assert.Equal(2, filtered.DataTableRowData!.Count);
        Assert.Contains(filtered.DataTableRowData, r => r.SparseIndex == 1);
        Assert.Contains(filtered.DataTableRowData, r => r.SparseIndex == 3);
    }

    // ------------------------------------------------------------------
    // Fallback paths
    // ------------------------------------------------------------------

    [Fact]
    public void FilterContainer_NoParseableIndex_ReturnsWholeContainer()
    {
        // Original single-select behaviour: if the selected field name
        // doesn't follow the "[N]" pattern we can't filter, so emit the
        // whole container.
        var container = new LiveFieldValue
        {
            Name = "Inventory",
            TypeName = "ArrayProperty",
            ArrayCount = 2,
            ArrayInnerType = "IntProperty",
            ArrayElemSize = 4,
            ArrayElements = new List<ArrayElementValue>
            {
                new() { Index = 0, Value = "10" },
                new() { Index = 1, Value = "20" },
            }
        };

        var selected = new List<LiveFieldValue>
        {
            new() { Name = "NotAnElement" },
        };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        // Same object returned (whole container) — both elements still
        // present.
        Assert.Same(container, filtered);
    }

    [Fact]
    public void FilterContainer_EmptySelection_ReturnsWholeContainer()
    {
        var container = new LiveFieldValue
        {
            Name = "Stuff",
            TypeName = "ArrayProperty",
            ArrayCount = 1,
            ArrayElements = new List<ArrayElementValue> { new() { Index = 0, Value = "x" } },
        };

        var filtered = LiveWalkerViewModel.FilterContainerToElement(
            container, new List<LiveFieldValue>());

        Assert.Same(container, filtered);
    }

    [Fact]
    public void FilterContainer_MixedParseableUnparseable_KeepsParseableSubset()
    {
        // Defensive: a stray non-synthetic row in the selection (shouldn't
        // happen in practice but guard anyway). The parseable ones still
        // get filtered, the unparseable one is silently dropped.
        var container = new LiveFieldValue
        {
            Name = "Inventory",
            TypeName = "ArrayProperty",
            ArrayCount = 3,
            ArrayInnerType = "IntProperty",
            ArrayElemSize = 4,
            ArrayElements = new List<ArrayElementValue>
            {
                new() { Index = 0, Value = "10" },
                new() { Index = 1, Value = "20" },
                new() { Index = 2, Value = "30" },
            }
        };

        var selected = new List<LiveFieldValue>
        {
            MakeSynthetic(0),
            new() { Name = "RandomNonSyntheticRow" },
            MakeSynthetic(2),
        };
        var filtered = LiveWalkerViewModel.FilterContainerToElement(container, selected);

        Assert.NotNull(filtered.ArrayElements);
        Assert.Equal(2, filtered.ArrayElements!.Count);
        Assert.Contains(filtered.ArrayElements, e => e.Index == 0);
        Assert.Contains(filtered.ArrayElements, e => e.Index == 2);
    }

    // ------------------------------------------------------------------
    // End-to-end: multi-select non-container view -> single XML root
    // ------------------------------------------------------------------

    [Fact]
    public void GenerateHierarchicalXml_MultipleScalarFields_EmitsSingleRootWithAllLeaves()
    {
        // Sibling fields under one pointer chain — the caller passes a
        // multi-element list and the emitter produces one root + N leaves.
        var breadcrumbs = new[] { MakeBc("0x1000", "Root") };
        var fields = new List<LiveFieldValue>
        {
            new() { Name = "Health", TypeName = "FloatProperty", Offset = 0x10, Size = 4 },
            new() { Name = "Mana",   TypeName = "FloatProperty", Offset = 0x14, Size = 4 },
            new() { Name = "Level",  TypeName = "IntProperty",   Offset = 0x18, Size = 4 },
        };

        var xml = CeXmlExportService.GenerateHierarchicalXml(
            "\"game.exe\"+1000", "Root", breadcrumbs, fields);

        // One XML declaration (single combined output, not three separate ones)
        Assert.Equal(1, CountOccurrences(xml, "<?xml"));
        // One root group + 3 leaves
        Assert.Equal(4, CountOccurrences(xml, "<CheatEntry>"));
        Assert.Contains("\"Health\"", xml);
        Assert.Contains("\"Mana\"",   xml);
        Assert.Contains("\"Level\"",  xml);
        Assert.Contains("<Address>+10</Address>", xml);
        Assert.Contains("<Address>+14</Address>", xml);
        Assert.Contains("<Address>+18</Address>", xml);
    }

    [Fact]
    public void GenerateHierarchicalXml_MultipleArrayElements_EmitsOneArrayGroupWithSelectedLeaves()
    {
        // Container-view multi-select: VM passes one container with 2
        // filtered elements, the emitter wraps them under one array group.
        var breadcrumbs = new[]
        {
            MakeBc("0x1000", "Root"),
            MakeBc("0x2000", "Player", "Player", isPointer: true, offset: 0x50),
        };
        var fields = new List<LiveFieldValue>
        {
            new()
            {
                Name = "Scores", TypeName = "ArrayProperty", Offset = 0x80, Size = 16,
                ArrayCount = 5, ArrayInnerType = "IntProperty", ArrayElemSize = 4,
                // Pre-filtered by the VM to indices 1 and 3
                ArrayElements = new List<ArrayElementValue>
                {
                    new() { Index = 1, Value = "20" },
                    new() { Index = 3, Value = "40" },
                }
            },
        };

        var xml = CeXmlExportService.GenerateHierarchicalXml(
            "\"game.exe\"+1000", "Root", breadcrumbs, fields);

        // Exactly one CE document
        Assert.Equal(1, CountOccurrences(xml, "<?xml"));
        // Array group preserves "5 x ..." in description
        Assert.Contains("Scores [5 x IntProperty (4B)]", xml);
        // Both selected elements appear; the unselected ones do not
        Assert.Contains("\"[1]\"", xml);
        Assert.Contains("\"[3]\"", xml);
        Assert.DoesNotContain("\"[0]\"", xml);
        Assert.DoesNotContain("\"[2]\"", xml);
        Assert.DoesNotContain("\"[4]\"", xml);
    }

    // ------------------------------------------------------------------
    // PathStepToBreadcrumbs — GWorld-path array-element hop splitting
    // ------------------------------------------------------------------

    [Fact]
    public void PathStepToBreadcrumbs_ObjectArrayElement_SplitsIntoContainerPlusElement()
    {
        // A GWorld-path hop through a TArray<ObjectProperty> element must expand
        // into TWO crumbs: the array field (deref TArray::Data) + the element
        // (deref the pointer at index*8). Regression for the Locate-in-GWorld
        // "[0] missing → wrong addresses downstream" Copy CE Field bug.
        var step = new GWorldPathStep
        {
            From = "0x3AB399520", To = "0x3AB399C40",
            FieldOffset = 0x2E0, FieldName = "PlayerArray",
            FieldType = "ArrayProperty", InnerType = "ObjectProperty",
            ElementIndex = 0, ToName = "PlayerState", ToClass = "PlayerState",
        };

        var crumbs = LiveWalkerViewModel.PathStepToBreadcrumbs(step);

        Assert.Equal(2, crumbs.Count);
        // Level 1: array field — container-view deref of TArray::Data at +2E0.
        Assert.Equal(0x2E0, crumbs[0].FieldOffset);
        Assert.True(crumbs[0].IsContainerView);
        Assert.Equal("PlayerArray", crumbs[0].FieldName);
        // Level 2: element pointer at index*8 = 0, pointer deref to PlayerState.
        Assert.Equal(0, crumbs[1].FieldOffset);
        Assert.True(crumbs[1].IsPointerDeref);
        Assert.False(crumbs[1].IsContainerView);
        Assert.Equal("[0]", crumbs[1].FieldName);
        Assert.Equal("0x3AB399C40", crumbs[1].Address);
    }

    [Fact]
    public void PathStepToBreadcrumbs_ObjectArrayElement_ElementOffsetIsIndexTimes8()
    {
        var step = new GWorldPathStep
        {
            From = "0xA", To = "0xB", FieldOffset = 0x10, FieldName = "Arr",
            FieldType = "ArrayProperty", InnerType = "ClassProperty", ElementIndex = 2,
        };
        var crumbs = LiveWalkerViewModel.PathStepToBreadcrumbs(step);
        Assert.Equal(2, crumbs.Count);
        Assert.Equal(0x10, crumbs[1].FieldOffset);  // index 2 * 8-byte ptr stride = 0x10
    }

    [Fact]
    public void PathStepToBreadcrumbs_DirectPointerField_SingleCrumb()
    {
        var step = new GWorldPathStep
        {
            From = "0xA", To = "0xB", FieldOffset = 0x340, FieldName = "PawnPrivate",
            FieldType = "ObjectProperty", ElementIndex = -1, ToName = "BP_PlayerCharacter_C",
        };
        var crumbs = LiveWalkerViewModel.PathStepToBreadcrumbs(step);
        Assert.Single(crumbs);
        Assert.Equal(0x340, crumbs[0].FieldOffset);
        Assert.True(crumbs[0].IsPointerDeref);
        Assert.False(crumbs[0].IsContainerView);
    }

    [Fact]
    public void PathStepToBreadcrumbs_StructArrayElement_DoesNotSplit()
    {
        // Struct-array element stride isn't a plain 8-byte pointer → keep the
        // single crumb (current behavior) rather than emit a wrong index*8 deref.
        var step = new GWorldPathStep
        {
            From = "0xA", To = "0xB", FieldOffset = 0x10, FieldName = "Items",
            FieldType = "ArrayProperty", InnerType = "StructProperty", ElementIndex = 1,
        };
        var crumbs = LiveWalkerViewModel.PathStepToBreadcrumbs(step);
        Assert.Single(crumbs);
    }

    [Fact]
    public void GenerateHierarchicalXml_PathThroughObjectArrayElement_EmitsElementDerefNode()
    {
        // End-to-end: a GWorld path GameState → PlayerArray[0] → PlayerState →
        // PawnPrivate must emit the array field (+2E0, deref Data), THEN the
        // element ([0] +0, deref ptr), THEN PawnPrivate (+340) NESTED under the
        // element — not PawnPrivate directly under the array (which would apply
        // +340 to the Data buffer base).
        var steps = new[]
        {
            new GWorldPathStep { From="0xW", To="0x3AB399520", FieldOffset=0x1B0, FieldName="GameState", FieldType="ObjectProperty", ElementIndex=-1, ToName="GameState" },
            new GWorldPathStep { From="0x3AB399520", To="0x3AB399C40", FieldOffset=0x2E0, FieldName="PlayerArray", FieldType="ArrayProperty", InnerType="ObjectProperty", ElementIndex=0, ToName="PlayerState" },
            new GWorldPathStep { From="0x3AB399C40", To="0x13F828040", FieldOffset=0x340, FieldName="PawnPrivate", FieldType="ObjectProperty", ElementIndex=-1, ToName="BP_PlayerCharacter_C" },
        };
        var breadcrumbs = new List<BreadcrumbItem> { MakeBc("0xW", "GWorld", "GWorld", isPointer: true) };
        foreach (var s in steps)
            breadcrumbs.AddRange(LiveWalkerViewModel.PathStepToBreadcrumbs(s));

        var fields = new List<LiveFieldValue>
            { new() { Name = "Health", TypeName = "FloatProperty", Offset = 0x30, Size = 4 } };

        var xml = CeXmlExportService.GenerateHierarchicalXml(
            "\"game.exe\"+1000", "GWorld", breadcrumbs, fields);

        // The element [0] deref node must exist (was missing → the bug).
        int elemIdx = xml.IndexOf("\"[0]\"", StringComparison.Ordinal);
        Assert.True(elemIdx >= 0, "element [0] deref node missing from the chain");
        Assert.Contains("<Address>+2E0</Address>", xml);  // PlayerArray (deref Data)
        Assert.Contains("<Address>+340</Address>", xml);  // PawnPrivate
        // PawnPrivate must come AFTER [0] in the document (nested under it), so the
        // +340 resolves against PlayerState, not the TArray::Data buffer.
        int pawnIdx = xml.IndexOf("\"PawnPrivate\"", StringComparison.Ordinal);
        Assert.True(pawnIdx > elemIdx, "PawnPrivate must nest under the [0] element");
    }

    // ------------------------------------------------------------------
    // DedupeConsecutiveBreadcrumbs — collapse redundant duplicate crumbs
    // ------------------------------------------------------------------

    [Fact]
    public void DedupeConsecutiveBreadcrumbs_CollapsesIdenticalContainerNeighbors_KeepsLater()
    {
        // Two identical consecutive container crumbs (e.g. a Locate-in-GWorld
        // path-synthetic SpawnedAttributes(C) + the user re-entering it) collapse
        // to one — keeping the LATER crumb (it has the live ContainerField).
        var cf = new LiveFieldValue
        {
            Name = "SpawnedAttributes", TypeName = "ArrayProperty",
            ArrayInnerType = "ObjectProperty", ArrayCount = 3,
        };
        var list = new List<BreadcrumbItem>
        {
            MakeBc("0x5E1BD8A0", "ASC", "AbilitySystemComponent", isPointer: true, offset: 0x7E0),
            new BreadcrumbItem { Address = "0x5E1BD8A0", Label = "SpawnedAttributes", FieldName = "SpawnedAttributes", FieldOffset = 0x10A8, IsContainerView = true },                      // synthetic, no ContainerField
            new BreadcrumbItem { Address = "0x5E1BD8A0", Label = "SpawnedAttributes", FieldName = "SpawnedAttributes", FieldOffset = 0x10A8, IsContainerView = true, ContainerField = cf }, // real
        };

        var result = CeXmlExportService.DedupeConsecutiveBreadcrumbs(list);

        Assert.Equal(2, result.Count);
        Assert.Equal("AbilitySystemComponent", result[0].FieldName);
        Assert.Equal("SpawnedAttributes", result[1].FieldName);
        Assert.NotNull(result[1].ContainerField);  // kept the later (richer) crumb
    }

    [Fact]
    public void DedupeConsecutiveBreadcrumbs_KeepsDistinctSplitCrumbs()
    {
        // The PathStepToBreadcrumbs split (container + element) must NOT be
        // collapsed — they differ by name/address/kind.
        var list = new List<BreadcrumbItem>
        {
            new BreadcrumbItem { Address = "0x3AB399520", FieldName = "PlayerArray", FieldOffset = 0x2E0, IsContainerView = true },
            new BreadcrumbItem { Address = "0x3AB399C40", FieldName = "[0]", FieldOffset = 0x0, IsPointerDeref = true },
        };
        var result = CeXmlExportService.DedupeConsecutiveBreadcrumbs(list);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GenerateHierarchicalXml_DuplicateConsecutiveContainerCrumbs_EmitsSingleDeref()
    {
        // A spine carrying two identical consecutive container crumbs must collapse
        // to ONE CE deref level (CleanBreadcrumbs dedups), not double-deref +10A8.
        var breadcrumbs = new List<BreadcrumbItem>
        {
            MakeBc("0x1000", "Root"),
            new BreadcrumbItem { Address = "0x5000", Label = "ASC", FieldName = "ASC", FieldOffset = 0x7E0, IsPointerDeref = true },
            new BreadcrumbItem { Address = "0x6000", Label = "Spawned", FieldName = "Spawned", FieldOffset = 0x10A8, IsContainerView = true },
            new BreadcrumbItem { Address = "0x6000", Label = "Spawned", FieldName = "Spawned", FieldOffset = 0x10A8, IsContainerView = true },
        };
        var fields = new List<LiveFieldValue>
            { new() { Name = "X", TypeName = "FloatProperty", Offset = 0x10, Size = 4 } };

        var xml = CeXmlExportService.GenerateHierarchicalXml(
            "\"g.exe\"+1000", "Root", breadcrumbs, fields);

        Assert.Equal(1, CountOccurrences(xml, "<Address>+10A8</Address>"));
    }

    // Deep distinct chain (the Gundam SEED repro) must pass through the dedup +
    // clean passes UNTOUCHED — guards DedupeConsecutiveBreadcrumbs / CleanBreadcrumbs
    // against over-collapsing a legitimate deeply-nested export.
    private static List<BreadcrumbItem> SeedDeepChain() => new()
    {
        MakeBc("0x100", "GWorld", "GWorld", isPointer: true),
        new BreadcrumbItem { Address = "0x200", FieldName = "OwningGameInstance",  FieldOffset = 0x180, IsPointerDeref = true },
        new BreadcrumbItem { Address = "0x300", FieldName = "m_savedata",          FieldOffset = 0x2A8, IsPointerDeref = true },
        new BreadcrumbItem { Address = "0x400", FieldName = "SaveSlotList",        FieldOffset = 0x7D0, IsContainerView = true },
        new BreadcrumbItem { Address = "0x500", FieldName = "[1]",                 FieldOffset = 0x6F8, IsPointerDeref = true },
        new BreadcrumbItem { Address = "0x600", FieldName = "MsTuneData.MsTunes",  FieldOffset = 0x0,   IsContainerView = true },
        new BreadcrumbItem { Address = "0x700", FieldName = "[0]",                 FieldOffset = 0x8,   IsPointerDeref = true },
        new BreadcrumbItem { Address = "0x800", FieldName = "WeaponTuneList",      FieldOffset = 0x18,  IsContainerView = true },
    };

    [Fact]
    public void DedupeConsecutiveBreadcrumbs_DeepDistinctChain_Unchanged()
    {
        var chain = SeedDeepChain();
        var result = CeXmlExportService.DedupeConsecutiveBreadcrumbs(chain);
        Assert.Equal(chain.Count, result.Count);
        for (int i = 0; i < chain.Count; i++)
            Assert.Equal(chain[i].FieldName, result[i].FieldName);
    }

    [Fact]
    public void CleanBreadcrumbs_DeepDistinctChain_PreservesAllLevels()
    {
        var chain = SeedDeepChain();
        var result = CeXmlExportService.CleanBreadcrumbs(chain);
        Assert.Equal(chain.Count, result.Count);
    }

    private static int CountOccurrences(string source, string substring)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }
}
