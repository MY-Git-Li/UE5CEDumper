using System.Text;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Services;

/// <summary>
/// Generates Cheat Engine XML address records using hierarchical nested format.
///
/// CE XML address resolution rules (hierarchical tree model):
/// - Root node: absolute address "Module.exe"+RVA
/// - Each child's Address is relative to its parent's RESOLVED address
/// - Pointer field: &lt;Address&gt;+{offset}&lt;/Address&gt; with &lt;Offsets&gt;&lt;Offset&gt;0&lt;/Offset&gt;&lt;/Offsets&gt;
///   → CE resolves to *(parentAddr + offset), children offset from the dereferenced value
/// - Inline field (scalar/struct): &lt;Address&gt;+{offset}&lt;/Address&gt; (no Offsets, no dereference)
/// - GroupHeader=1 makes an entry a collapsible folder with children
///
/// CE type mapping:
/// - Signed integers (IntProperty, Int8/16/64Property): ShowAsSigned=1
/// - Unsigned integers (UInt32/16/64Property, ByteProperty): ShowAsSigned=0
/// - BoolProperty with bit mask: VariableType=Binary, BitStart/BitLength from UE FieldMask
/// - Pointer fields (ObjectProperty navigable): ShowAsHex=1, GroupHeader placeholder
/// - Struct fields (StructProperty): real field names via DLL resolution, flattened nested structs
///
/// Struct expansion:
/// - StructProperty fields are resolved via WalkInstanceAsync to get real field names/types
/// - Nested StructProperty are recursively flattened (all inner fields at the same level)
/// - Pointer fields inside structs emit as 8 Bytes ShowAsHex placeholder
/// - Max recursion depth: 5 levels
/// </summary>
public static class CeXmlExportService
{
    // NOTE: _nextId is reset at the start of each Generate* method call,
    // so concurrent calls are safe as long as each completes atomically.
    // Using ThreadStatic to eliminate any cross-thread risk.
    [ThreadStatic]
    private static int _nextId;

    /// <summary>Max depth for recursive struct resolution.</summary>
    private const int MaxStructDepth = 5;

    /// <summary>Max entries for a CE DropDownList. Lists exceeding this are omitted.</summary>
    [ThreadStatic]
    private static int _maxDropDownEntries;

    /// <summary>
    /// Tracks emitted DropDownList owners by UEnum address → parent group's Description.
    /// Reset per Generate* call. Enables DropDownListLink sharing for same-enum arrays.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _dropDownOwners;

    /// <summary>
    /// Tracks emitted DropDownList parent descriptions to ensure uniqueness.
    /// CE uses Description text as DropDownListLink key, so duplicates cause ambiguity.
    /// If a duplicate is found, ".001", ".002" etc. suffix is appended.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _dropDownDescriptions;

    /// <summary>
    /// When true, every non-root GroupHeader folder (pointer/array/map/set deref
    /// nodes, struct groups, AND array/map/set element folders such as
    /// <c>[1]</c>) emits &lt;Options moHideChildren="1"
    /// moDeactivateChildrenAsWell="1"/&gt; to collapse it by default in Cheat
    /// Engine. The root node is excluded (its address is absolute, not "+...",
    /// so it stays expanded).
    /// </summary>
    [ThreadStatic]
    private static bool _collapsePointerNodes;

    /// <summary>
    /// Path-based cycle detection for drilled pointer emit. Holds the PtrAddresses
    /// currently on the emit stack — we push on entry into EmitDrilledPointer and
    /// pop on exit. If a target appears in this set, the pointer is a back-edge
    /// (e.g. UWorld -&gt; PersistentLevel -&gt; OwningWorld) and must NOT be re-emitted
    /// as a group, otherwise the StringBuilder explodes (observed: 2GB capacity
    /// hit on DQ I&amp;II HD-2D with Drill Depth = 2).
    ///
    /// ResolvePointerInstancesAsync's visited set protects the *resolve* phase;
    /// the *emit* phase is independent and needs its own protection.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _emitPath;

    /// <summary>
    /// Hard ceiling on EmitDrilledPointer recursion depth — covers the rare case
    /// where ResolvePointerInstancesAsync produced a long but acyclic chain that
    /// would still trigger an XML blow-up. Set generously so legitimate trees
    /// (depth 4 + cascade) fit comfortably; the cycle protection above is the
    /// primary line of defence.
    /// </summary>
    private const int MaxEmitPointerDepth = 16;

    [ThreadStatic]
    private static int _emitPointerDepth;

    /// <summary>
    /// Per-call resolved-field dictionaries, mirrored into thread-static state so
    /// the container emitters (EmitMapProperty / EmitSetProperty / struct-array)
    /// can expand element VALUES that are structs/objects by delegating to
    /// EmitFields — without threading the dicts through every emit signature.
    /// Set at the top of each Generate* entry point; keyed by StructDataAddr /
    /// PtrAddress respectively (same keys ResolveDrilldownAsync populates).
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, List<LiveFieldValue>>? _resolvedStructsState;
    [ThreadStatic]
    private static Dictionary<string, List<LiveFieldValue>>? _resolvedInstancesState;

    /// <summary>CE field metadata for XML generation.</summary>
    private record CeFieldInfo(
        string VariableType,
        bool IsSigned = false,
        bool ShowAsHex = false,
        int BitStart = -1,
        int BitLength = 0);

    // ========================================
    // Struct field resolution (async, requires DLL pipe)
    // ========================================

    /// <summary>
    /// Pre-resolve all StructProperty fields by walking their inner structure via the DLL.
    /// Returns a dictionary keyed by field offset, containing flattened inner fields
    /// with relative offsets from the struct start and dot-prefixed names for nested structs.
    ///
    /// Example: StructA at offset 0x100 with inner StructB at +0x10 containing X at +0x0
    ///   -> resolvedStructs[0x100] = [
    ///        LiveFieldValue { Name="IntField", Offset=0x0 },
    ///        LiveFieldValue { Name="StructB.X", Offset=0x10 },
    ///        LiveFieldValue { Name="StructB.Y", Offset=0x14 },
    ///      ]
    /// </summary>
    /// <summary>
    /// Pre-resolve ObjectProperty / ClassProperty / WeakObjectProperty / Soft* / Lazy* /
    /// Interface* targets so the CE XML emitter can drop GroupHeader+Offsets=[0] children
    /// onto the pointer leaf, mirroring the same drilldown the CSX exporter ships
    /// (CsxExportService.ResolvePointerInstancesAsync). The result is keyed by PtrAddress
    /// so the emit-time lookup is O(1) per field.
    ///
    /// Cascades StructProperty resolution into <paramref name="resolvedStructs"/> for
    /// every drilled target's fields too — without that, drilled children with
    /// StructProperty (e.g. <c>PrimaryComponentTick (ActorComponentTickFunction)</c>
    /// inside a UComponent) would render as empty GroupHeader placeholders even
    /// though the user asked for full drill-down.
    ///
    /// Recurses into resolved targets up to <paramref name="depth"/>; uses a
    /// shared visited set for cycle detection. Returns empty when depth &lt;= 0.
    /// </summary>
    public static async Task<Dictionary<string, List<LiveFieldValue>>> ResolvePointerInstancesAsync(
        IDumpService dump,
        IReadOnlyList<LiveFieldValue> fields,
        int depth,
        int arrayLimit = 64,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs = null)
    {
        var resolved = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
        if (depth <= 0) return resolved;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        await ResolvePointerInstancesRecursiveAsync(
            dump, fields, resolved, depth, arrayLimit, visited, resolvedStructs);
        return resolved;
    }

    private static async Task ResolvePointerInstancesRecursiveAsync(
        IDumpService dump,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>> resolved,
        int remainingDepth,
        int arrayLimit,
        HashSet<string> visited,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs)
    {
        if (remainingDepth <= 0) return;

        foreach (var field in fields)
        {
            if (!IsObjectPropertyType(field.TypeName)) continue;
            if (string.IsNullOrEmpty(field.PtrAddress) || field.PtrAddress == "0x0") continue;
            if (resolved.ContainsKey(field.PtrAddress)) continue;
            if (!visited.Add(field.PtrAddress)) continue; // cycle protection

            try
            {
                var result = await dump.WalkInstanceAsync(field.PtrAddress, field.PtrClassAddr, arrayLimit);
                if (result.Fields.Count > 0)
                {
                    resolved[field.PtrAddress] = result.Fields;

                    // Cascade struct resolution so the drilled target's
                    // StructProperty children expand to real sub-fields,
                    // not empty GroupHeader placeholders.
                    if (resolvedStructs != null)
                    {
                        await ResolveStructFieldsIntoAsync(
                            dump, result.Fields, resolvedStructs, arrayLimit);
                    }

                    // Recurse one level deeper for nested pointers in the resolved target
                    await ResolvePointerInstancesRecursiveAsync(
                        dump, result.Fields, resolved, remainingDepth - 1,
                        arrayLimit, visited, resolvedStructs);
                }
            }
            catch
            {
                // Pipe error / target reclaimed — skip this branch quietly,
                // pointer falls back to a flat 8 Bytes hex leaf in the emit step.
            }
        }
    }

    /// <summary>
    /// Object/Class pointer family — same set CsxExportService treats as drilldown-eligible.
    /// </summary>
    private static bool IsObjectPropertyType(string typeName) => typeName is
        "ObjectProperty" or "ClassProperty" or "WeakObjectProperty" or
        "SoftObjectProperty" or "SoftClassProperty" or "LazyObjectProperty" or
        "InterfaceProperty";

    // Array inner types whose element slot holds a RAW 8-byte UObject* — so a
    // drilled element can dereference it with Offsets=[0]. This is a STRICT subset
    // of IsObjectPropertyType and matches the DLL's Ubel::IsPointerArrayType:
    // Weak (FWeakObjectPtr {ObjectIndex, SerialNumber}), Soft (FSoftObjectPath) and
    // Lazy (FGuid) elements are NOT raw pointers even though the DLL resolves them
    // to a live UObject* — dereferencing their slot would land CE at a garbage
    // address, so they must keep their existing leaf / Phase-G handling.
    private static bool IsRawObjectPtrArrayInner(string innerType) =>
        innerType is "ObjectProperty" or "ClassProperty";

    // ========================================
    // Unified drilldown resolver (docs/ce-export-drilldown-spec.md Phase A)
    // ========================================

    /// <summary>
    /// One recursive pass that resolves everything the emitter needs to expand:
    /// (1) StructProperty fields (flattened, depth-free), (2) ObjectProperty
    /// pointer targets (cost 1 level), and (3) CONTAINER ELEMENT VALUES that are
    /// structs/objects (Map values, Set elements, struct-array elements — cost 1
    /// level), recursing into each so nested containers expand too. Populates
    /// <paramref name="resolvedStructs"/> (keyed by StructDataAddr) and
    /// <paramref name="resolvedInstances"/> (keyed by PtrAddress) — the same keys
    /// the emit phase looks up. Replaces the separate ResolveStructFieldsAsync +
    /// ResolvePointerInstancesAsync calls for CE XML export.
    /// </summary>
    public static async Task ResolveDrilldownAsync(
        IDumpService dump,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>> resolvedStructs,
        Dictionary<string, List<LiveFieldValue>> resolvedInstances,
        int depth,
        int arrayLimit = 64,
        Action? onWalk = null)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await ResolveDrilldownRecAsync(dump, fields, resolvedStructs, resolvedInstances,
            depth, arrayLimit, visited, onWalk);
    }

    private static async Task ResolveDrilldownRecAsync(
        IDumpService dump,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>> resolvedStructs,
        Dictionary<string, List<LiveFieldValue>> resolvedInstances,
        int depth,
        int arrayLimit,
        HashSet<string> visited,
        Action? onWalk)
    {
        // (1) Structs at this level — flatten nested (depth-free, MaxStructDepth-bound),
        //     then descend into each resolved struct's own fields (still depth-free) so
        //     containers/pointers INSIDE the struct are reached.
        await ResolveStructFieldsIntoAsync(dump, fields, resolvedStructs, arrayLimit);
        onWalk?.Invoke();
        foreach (var f in fields)
        {
            if (f.TypeName is not ("StructProperty" or "OptionalProperty")) continue;
            if (string.IsNullOrEmpty(f.StructDataAddr)) continue;
            if (!resolvedStructs.TryGetValue(f.StructDataAddr, out var sub)) continue;
            if (!visited.Add("S:" + f.StructDataAddr)) continue;
            await ResolveDrilldownRecAsync(dump, sub, resolvedStructs, resolvedInstances,
                depth, arrayLimit, visited, onWalk);
        }

        if (depth <= 0) return;

        // (2) Pointers — cost 1 level.
        foreach (var f in fields)
        {
            if (!IsObjectPropertyType(f.TypeName)) continue;
            if (string.IsNullOrEmpty(f.PtrAddress) || f.PtrAddress == "0x0") continue;
            if (resolvedInstances.ContainsKey(f.PtrAddress)) continue;
            if (!visited.Add("P:" + f.PtrAddress)) continue;
            await WalkAndRecurseAsync(dump, f.PtrAddress, f.PtrClassAddr, resolvedStructs,
                resolvedInstances, depth - 1, arrayLimit, visited, onWalk);
        }

        // (3) Container element VALUES (struct + object) — cost 1 level.
        foreach (var f in fields)
        {
            var valueFields = BuildContainerValueFields(f);
            if (valueFields.Count == 0) continue;

            var structVals = valueFields
                .Where(v => v.TypeName is "StructProperty"
                            && !string.IsNullOrEmpty(v.StructDataAddr)
                            && !string.IsNullOrEmpty(v.StructClassAddr))
                .ToList();
            if (structVals.Count > 0)
            {
                await ResolveStructFieldsIntoAsync(dump, structVals, resolvedStructs, arrayLimit);
                onWalk?.Invoke();
                foreach (var sv in structVals)
                {
                    if (!resolvedStructs.TryGetValue(sv.StructDataAddr, out var sub)) continue;
                    if (!visited.Add("S:" + sv.StructDataAddr)) continue;
                    await ResolveDrilldownRecAsync(dump, sub, resolvedStructs, resolvedInstances,
                        depth - 1, arrayLimit, visited, onWalk);
                }
            }

            foreach (var ov in valueFields)
            {
                if (!IsObjectPropertyType(ov.TypeName)) continue;
                if (string.IsNullOrEmpty(ov.PtrAddress) || ov.PtrAddress == "0x0") continue;
                if (resolvedInstances.ContainsKey(ov.PtrAddress)) continue;
                if (!visited.Add("P:" + ov.PtrAddress)) continue;
                await WalkAndRecurseAsync(dump, ov.PtrAddress, ov.PtrClassAddr, resolvedStructs,
                    resolvedInstances, depth - 1, arrayLimit, visited, onWalk);
            }
        }
    }

    private static async Task WalkAndRecurseAsync(
        IDumpService dump, string ptrAddr, string ptrClassAddr,
        Dictionary<string, List<LiveFieldValue>> resolvedStructs,
        Dictionary<string, List<LiveFieldValue>> resolvedInstances,
        int depth, int arrayLimit, HashSet<string> visited, Action? onWalk)
    {
        try
        {
            var r = await dump.WalkInstanceAsync(ptrAddr, ptrClassAddr, arrayLimit);
            if (r.Fields.Count > 0)
            {
                resolvedInstances[ptrAddr] = r.Fields;
                onWalk?.Invoke();
                await ResolveDrilldownRecAsync(dump, r.Fields, resolvedStructs,
                    resolvedInstances, depth, arrayLimit, visited, onWalk);
            }
        }
        catch
        {
            // Pipe error / reclaimed target — leave unresolved; emit falls back to a leaf.
        }
    }

    /// <summary>
    /// Build synthetic value fields for a container's struct/object element VALUES,
    /// each carrying the value's absolute <c>StructDataAddr</c> (struct) or
    /// <c>PtrAddress</c> (object) for the resolver to walk. Scalar values are
    /// skipped (they emit as plain leaves). The absolute address formulas match
    /// the emitters (and PopulateMapContainerFields) exactly, so the resolver's
    /// keys line up with the emit-time lookups.
    /// </summary>
    private static List<LiveFieldValue> BuildContainerValueFields(LiveFieldValue field)
    {
        var list = new List<LiveFieldValue>();
        switch (field.TypeName)
        {
            case "MapProperty" when field.MapElements is { Count: > 0 }:
            {
                bool isStruct = field.MapValueType == "StructProperty"
                                && !string.IsNullOrEmpty(field.MapValueStructAddr);
                bool isObj = IsObjectPropertyType(field.MapValueType);
                if (!isStruct && !isObj) break;
                ulong dataBase = ParseHexAddr(field.MapDataAddr);
                int valOffset = field.MapValueOffset > 0 ? field.MapValueOffset : field.MapKeySize;
                int stride = ComputeSetElementStride(valOffset + field.MapValueSize);
                foreach (var e in field.MapElements)
                {
                    long off = (long)e.Index * stride + valOffset;
                    if (isStruct && dataBase != 0)
                        list.Add(new LiveFieldValue
                        {
                            TypeName = "StructProperty",
                            StructDataAddr = AbsAddr(dataBase, off),
                            StructClassAddr = field.MapValueStructAddr,
                            StructTypeName = field.MapValueStructType,
                        });
                    else if (isObj && !string.IsNullOrEmpty(e.ValuePtrAddress) && e.ValuePtrAddress != "0x0")
                        list.Add(new LiveFieldValue
                        {
                            TypeName = field.MapValueType,
                            PtrAddress = e.ValuePtrAddress,
                            PtrName = e.ValuePtrName,
                            PtrClassName = e.ValuePtrClassName,
                        });
                }
                break;
            }
            case "SetProperty" when field.SetElements is { Count: > 0 }:
            {
                bool isStruct = field.SetElemType == "StructProperty"
                                && !string.IsNullOrEmpty(field.SetElemStructAddr);
                bool isObj = IsObjectPropertyType(field.SetElemType);
                if (!isStruct && !isObj) break;
                ulong dataBase = ParseHexAddr(field.SetDataAddr);
                int stride = ComputeSetElementStride(field.SetElemSize);
                foreach (var e in field.SetElements)
                {
                    long off = (long)e.Index * stride;
                    if (isStruct && dataBase != 0)
                        list.Add(new LiveFieldValue
                        {
                            TypeName = "StructProperty",
                            StructDataAddr = AbsAddr(dataBase, off),
                            StructClassAddr = field.SetElemStructAddr,
                            StructTypeName = field.SetElemStructType,
                        });
                    else if (isObj && !string.IsNullOrEmpty(e.KeyPtrAddress) && e.KeyPtrAddress != "0x0")
                        list.Add(new LiveFieldValue
                        {
                            TypeName = field.SetElemType,
                            PtrAddress = e.KeyPtrAddress,
                            PtrName = e.KeyPtrName,
                            PtrClassName = e.KeyPtrClassName,
                        });
                }
                break;
            }
            case "ArrayProperty" when field.ArrayInnerType == "StructProperty"
                    && !string.IsNullOrEmpty(field.ArrayStructClassAddr)
                    && field.ArrayElements is { Count: > 0 }:
            {
                ulong dataBase = ParseHexAddr(field.ArrayDataAddr);
                if (dataBase == 0) break;
                foreach (var e in field.ArrayElements)
                    list.Add(new LiveFieldValue
                    {
                        TypeName = "StructProperty",
                        StructDataAddr = AbsAddr(dataBase, (long)e.Index * field.ArrayElemSize),
                        StructClassAddr = field.ArrayStructClassAddr,
                        StructTypeName = field.ArrayStructType,
                    });
                break;
            }
            // TArray<ObjectProperty> (object pointers, e.g. SpawnedAttributes): emit
            // each non-null element pointer so the resolver walks the target object
            // and populates resolvedInstances — without this, drilling into a
            // selected object-array element was a no-op (the element emitted as a
            // plain 8-byte pointer regardless of drilldown depth). ArrayElementValue
            // carries no PtrClassAddr; WalkInstance resolves the class from the
            // pointer itself when class_addr is omitted.
            case "ArrayProperty" when IsRawObjectPtrArrayInner(field.ArrayInnerType)
                    && field.ArrayElements is { Count: > 0 }:
            {
                foreach (var e in field.ArrayElements)
                    if (!string.IsNullOrEmpty(e.PtrAddress) && e.PtrAddress != "0x0")
                        list.Add(new LiveFieldValue
                        {
                            TypeName = field.ArrayInnerType,
                            PtrAddress = e.PtrAddress,
                            PtrName = e.PtrName,
                            PtrClassName = e.PtrClassName,
                        });
                break;
            }
        }
        return list;
    }

    private static ulong ParseHexAddr(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = (s.StartsWith("0x") || s.StartsWith("0X")) ? s.Substring(2) : s;
        return ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private static string AbsAddr(ulong dataBase, long offset)
        => dataBase == 0 ? "" : $"0x{dataBase + (ulong)offset:X}";

    /// <summary>
    /// Parse the first <paramref name="numBytes"/> of a byte-sequence hex string
    /// (e.g. ContainerElementValue.ValueHex "A4AD310000000000") as a little-endian
    /// integer — the raw int CE reads at the value address (FName ComparisonIndex /
    /// enum value), used to key the value DropDownList.
    /// </summary>
    private static long ParseHexLeInt(string? hex, int numBytes)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        long v = 0;
        int n = Math.Min(numBytes, hex.Length / 2);
        for (int i = 0; i < n; i++)
        {
            int b = Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            v |= (long)b << (i * 8);
        }
        return v;
    }

    /// <summary>
    /// Pre-resolve all StructProperty fields in <paramref name="fields"/> by walking
    /// each one's inner UScriptStruct via the DLL.
    ///
    /// Result is keyed by <see cref="LiveFieldValue.StructDataAddr"/> (the absolute
    /// memory address of the struct data) — NOT by field.Offset — so the same
    /// dictionary can hold struct fields from multiple drilled-pointer instances
    /// without offset-based key collisions (e.g. two different objects each have
    /// a StructProperty at offset 0x30, but their StructDataAddr differs).
    /// </summary>
    public static async Task<Dictionary<string, List<LiveFieldValue>>> ResolveStructFieldsAsync(
        IDumpService dump, IReadOnlyList<LiveFieldValue> fields, int arrayLimit = 64)
    {
        var result = new Dictionary<string, List<LiveFieldValue>>(StringComparer.Ordinal);
        await ResolveStructFieldsIntoAsync(dump, fields, result, arrayLimit);
        return result;
    }

    /// <summary>
    /// Walk <paramref name="fields"/>'s StructProperty entries and add their
    /// resolved sub-fields into <paramref name="resolved"/> (keyed by
    /// StructDataAddr). Used both for top-level resolution and for cascading
    /// into drilled pointer targets — letting one dict cover the whole tree.
    /// </summary>
    private static async Task ResolveStructFieldsIntoAsync(
        IDumpService dump,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>> resolved,
        int arrayLimit)
    {
        foreach (var field in fields)
        {
            // Both StructProperty and OptionalProperty<Struct> have the same
            // {StructClassAddr, StructDataAddr, StructTypeName} triple stamped
            // by the walker when the value is set, so the resolver treats
            // them uniformly — the emit-time branch decides how to render.
            var isStruct = field.TypeName == "StructProperty"
                        || field.TypeName == "OptionalProperty";
            if (!isStruct
                || string.IsNullOrEmpty(field.StructClassAddr)
                || string.IsNullOrEmpty(field.StructDataAddr)
                || field.StructDataAddr == "0x0")
                continue;
            if (resolved.ContainsKey(field.StructDataAddr)) continue;

            var subResolved = new List<LiveFieldValue>();
            try
            {
                await ResolveStructRecursiveAsync(dump, field.StructDataAddr, field.StructClassAddr,
                    "", 0, subResolved, 0, arrayLimit);
            }
            catch
            {
                // If resolution fails (pipe error, etc.), leave empty — will fall back to placeholder
            }

            if (subResolved.Count > 0)
                resolved[field.StructDataAddr] = subResolved;
        }
    }

    private static async Task ResolveStructRecursiveAsync(
        IDumpService dump, string dataAddr, string classAddr,
        string namePrefix, int baseOffset, List<LiveFieldValue> output, int depth,
        int arrayLimit = 64)
    {
        if (depth >= MaxStructDepth) return;

        var walkResult = await dump.WalkInstanceAsync(dataAddr, classAddr, arrayLimit: arrayLimit);

        foreach (var f in walkResult.Fields)
        {
            var displayName = string.IsNullOrEmpty(namePrefix) ? f.Name : $"{namePrefix}.{f.Name}";
            var absOffset = baseOffset + f.Offset;

            if (f.TypeName == "StructProperty"
                && !string.IsNullOrEmpty(f.StructClassAddr)
                && !string.IsNullOrEmpty(f.StructDataAddr)
                && f.StructDataAddr != "0x0")
            {
                // Nested struct — recurse and flatten into the same list
                await ResolveStructRecursiveAsync(dump, f.StructDataAddr, f.StructClassAddr,
                    displayName, absOffset, output, depth + 1, arrayLimit);
            }
            else if (f.IsPointerNavigation)
            {
                // Pointer inside struct — emit as pointer placeholder
                output.Add(new LiveFieldValue
                {
                    Name = displayName,
                    TypeName = f.TypeName,
                    Offset = absOffset,
                    Size = f.Size,
                    PtrAddress = f.PtrAddress,
                    PtrName = f.PtrName,
                    PtrClassName = f.PtrClassName,
                    PtrClassAddr = f.PtrClassAddr,
                });
            }
            else
            {
                // Scalar or array field — add with accumulated offset and prefixed name
                output.Add(new LiveFieldValue
                {
                    Name = displayName,
                    TypeName = f.TypeName,
                    Offset = absOffset,
                    Size = f.Size,
                    HexValue = f.HexValue,
                    TypedValue = f.TypedValue,
                    BoolBitIndex = f.BoolBitIndex,
                    BoolFieldMask = f.BoolFieldMask,
                    // Preserve the within-field byte index so a flattened bit-field bool keeps
                    // landing on the right byte (base + Offset + ByteOffset). CE XML ignores it,
                    // but CSX 7.7+ Binary export needs it to place the bit switch correctly.
                    BoolByteOffset = f.BoolByteOffset,
                    ArrayCount = f.ArrayCount,
                    ArrayInnerType = f.ArrayInnerType,
                    ArrayElemSize = f.ArrayElemSize,
                    ArrayStructType = f.ArrayStructType,
                    ArrayStructClassAddr = f.ArrayStructClassAddr,
                    ArrayElements = f.ArrayElements,
                    ArrayDataAddr = f.ArrayDataAddr,
                    ArrayEnumAddr = f.ArrayEnumAddr,
                    ArrayEnumEntries = f.ArrayEnumEntries,
                    SoftArrayFNameSize = f.SoftArrayFNameSize,
                    SoftArrayIsTopLevelAssetPath = f.SoftArrayIsTopLevelAssetPath,
                    EnumName = f.EnumName,
                    EnumValue = f.EnumValue,
                    EnumAddr = f.EnumAddr,
                    EnumEntries = f.EnumEntries,
                    StrValue = f.StrValue,
                    MapCount = f.MapCount,
                    MapKeyType = f.MapKeyType,
                    MapValueType = f.MapValueType,
                    MapKeySize = f.MapKeySize,
                    MapValueSize = f.MapValueSize,
                    MapValueOffset = f.MapValueOffset,
                    MapDataAddr = f.MapDataAddr,
                    MapElements = f.MapElements,
                    // Container value/key struct metadata — REQUIRED so a Map/Set/Array
                    // nested INSIDE a struct can resolve+expand its struct values
                    // (e.g. MsTuneData → MsTunes {Map → Struct}).
                    MapKeyStructAddr = f.MapKeyStructAddr,
                    MapKeyStructType = f.MapKeyStructType,
                    MapValueStructAddr = f.MapValueStructAddr,
                    MapValueStructType = f.MapValueStructType,
                    SetCount = f.SetCount,
                    SetElemType = f.SetElemType,
                    SetElemSize = f.SetElemSize,
                    SetDataAddr = f.SetDataAddr,
                    SetElemStructAddr = f.SetElemStructAddr,
                    SetElemStructType = f.SetElemStructType,
                    SetElements = f.SetElements,
                });
            }
        }
    }

    // ========================================
    // XML generation
    // ========================================

    /// <summary>
    /// Generate hierarchical CE XML from the navigation breadcrumb trail and current fields.
    ///
    /// Algorithm:
    /// - Root (breadcrumbs[0]): absolute address, GroupHeader
    /// - Each breadcrumb[i] (i>=1): Address=+{fieldOffset}
    ///   - If the breadcrumb is a pointer (IsPointerDeref): add Offsets=[0] to dereference
    ///   - If inline (struct): no Offsets
    ///   Parent's Offsets=[0] resolves the pointer, so children just add their offset
    /// - Leaf fields: always Address=+{field.Offset}, no Offsets
    ///   (Parent breadcrumb already resolved any pointer dereference via its Offsets=[0])
    /// - StructProperty (inline): Address=+{structOffset}, no Offsets, children at relative offsets
    /// - ArrayProperty (scalar): Address=+{fieldOffset}, Offsets=[0] (deref TArray.Data)
    ///   Element children: Address=+{N*elemSize} (Data pointer already dereferenced by parent)
    /// </summary>
    public static string GenerateHierarchicalXml(
        string rootAddress,
        string rootName,
        IReadOnlyList<BreadcrumbItem> breadcrumbs,
        IReadOnlyList<LiveFieldValue> currentFields,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs = null,
        bool collapsePointerNodes = false,
        int maxDropDownEntries = 512,
        Dictionary<string, List<LiveFieldValue>>? resolvedInstances = null,
        bool flattenChain = false)
    {
        // Clean breadcrumbs: remove navigation cycles (e.g., Child->Parent->Child)
        // before generating XML to avoid deeply nested duplicate pointer chains.
        var cleanedBc = CleanBreadcrumbs(breadcrumbs);

        _nextId = 100;
        _collapsePointerNodes = collapsePointerNodes;
        _maxDropDownEntries = maxDropDownEntries;
        _dropDownOwners = new Dictionary<string, string>();
        _dropDownDescriptions = new HashSet<string>(StringComparer.Ordinal);
        _emitPath = new HashSet<string>(StringComparer.Ordinal);
        _emitPointerDepth = 0;
        _resolvedStructsState = resolvedStructs;
        _resolvedInstancesState = resolvedInstances;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<CheatTable>");
        // Best-effort note when the game uses the UE5.7+ UNVERIFIED packed layout (no-op otherwise).
        sb.Append(PackedLayoutNotice.XmlComment);
        sb.AppendLine("  <CheatEntries>");

        // Build the nested structure recursively via indentation tracking
        var indent = "    ";
        var openTags = 0;

        // Root entry (cycle removal preserves breadcrumbs[0], so rootAddress/rootName are still valid)
        EmitGroupOpen(sb, indent, rootName, rootAddress, null, showAsHex: true, varType: "8 Bytes");
        openTags++;

        // Intermediate breadcrumb levels (navigation path)
        // Each breadcrumb: go to field offset from parent's resolved address.
        // If this field is a pointer, add Offsets=[0] to dereference it.
        // Container views (Array/Map/Set) also need Offsets=[0] to dereference
        // TArray::Data / TSparseArray::Data pointer at the field offset.
        // Parent's own Offsets=[0] (if pointer) already resolved the dereference,
        // so children just add their field offset.
        // Navigation spine. With Collapse chain on, fold every breadcrumb after the
        // root into ONE CE multi-level-pointer entry; otherwise emit the nested chain
        // (one group per breadcrumb). spineLevels = group levels the spine occupies,
        // so the leaf indent / close loop work for both shapes.
        int spineLevels;
        var folded = flattenChain ? FoldBreadcrumbSpine(cleanedBc) : null;
        if (folded != null)
        {
            EmitGroupOpen(sb, indent + "  ", folded.Description,
                folded.Address, folded.Offsets, showAsHex: folded.ShowAsHex);
            openTags++;
            spineLevels = 1;
        }
        else
        {
            for (int i = 1; i < cleanedBc.Count; i++)
            {
                // Both emit paths derive (offset, deref, label) from ProjectBreadcrumb
                // so the nested and folded shapes can never disagree about a
                // breadcrumb's pointer semantics. Containers/pointers deref
                // TArray::Data / TSparseArray::Data via Offsets=[0]; inline structs
                // just add their offset.
                var step = ProjectBreadcrumb(cleanedBc[i]);
                var childIndent = indent + new string(' ', i * 2);
                EmitGroupOpen(sb, childIndent, step.Description,
                    $"+{step.Offset:X}",
                    step.DerefAfter ? new[] { 0 } : null,
                    showAsHex: step.DerefAfter);
                openTags++;
            }
            spineLevels = cleanedBc.Count - 1;
        }

        // Leaf fields at the deepest level. Parent breadcrumb (nested or folded)
        // already resolved any pointer dereference, so leaf fields use Address=+{off}.
        var leafIndent = indent + new string(' ', (spineLevels + 1) * 2);
        EmitFields(sb, leafIndent, currentFields, resolvedStructs, resolvedInstances);

        // Close all nested levels (innermost first)
        for (int i = openTags - 1; i >= 0; i--)
        {
            var closeIndent = indent + new string(' ', i * 2);
            EmitGroupClose(sb, closeIndent);
        }

        sb.AppendLine("  </CheatEntries>");
        sb.AppendLine("</CheatTable>");

        return sb.ToString();
    }

    /// <summary>
    /// Generate CE XML for an instance with no navigation history (Instance Finder).
    /// Root = the instance itself. Fields are direct children with +{offset}.
    /// </summary>
    public static string GenerateInstanceXml(
        string rootAddress,
        string rootName,
        string className,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs = null,
        bool collapsePointerNodes = false,
        int maxDropDownEntries = 512,
        Dictionary<string, List<LiveFieldValue>>? resolvedInstances = null)
    {
        _nextId = 100;
        _collapsePointerNodes = collapsePointerNodes;
        _maxDropDownEntries = maxDropDownEntries;
        _dropDownOwners = new Dictionary<string, string>();
        _dropDownDescriptions = new HashSet<string>(StringComparer.Ordinal);
        _emitPath = new HashSet<string>(StringComparer.Ordinal);
        _emitPointerDepth = 0;
        _resolvedStructsState = resolvedStructs;
        _resolvedInstancesState = resolvedInstances;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<CheatTable>");
        // Best-effort note when the game uses the UE5.7+ UNVERIFIED packed layout (no-op otherwise).
        sb.Append(PackedLayoutNotice.XmlComment);
        sb.AppendLine("  <CheatEntries>");

        var indent = "    ";
        EmitGroupOpen(sb, indent, $"{className}: {rootName}", rootAddress, null,
            showAsHex: true, varType: "8 Bytes");

        var leafIndent = indent + "  ";
        EmitFields(sb, leafIndent, fields, resolvedStructs, resolvedInstances);

        EmitGroupClose(sb, indent);

        sb.AppendLine("  </CheatEntries>");
        sb.AppendLine("</CheatTable>");

        return sb.ToString();
    }

    /// <summary>
    /// Generate a CE-compatible XML with an AutoAssembler script that registers a symbol.
    /// Accepts a pre-formatted address string (e.g., "module.exe"+RVA or plain hex).
    /// </summary>
    public static string GenerateRegisterSymbolXml(string symbolName, string formattedAddress)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<CheatTable>");
        sb.AppendLine("  <CheatEntries>");
        sb.AppendLine($"    <CheatEntry>");
        sb.AppendLine($"      <ID>0</ID>");
        sb.AppendLine($"      <Description>\"{symbolName}\"</Description>");
        sb.AppendLine($"      <VariableType>Auto Assembler Script</VariableType>");
        sb.AppendLine($"      <AssemblerScript>");

        sb.AppendLine("[ENABLE]");
        sb.AppendLine($"define({symbolName},{formattedAddress})");
        sb.AppendLine($"registersymbol({symbolName})");
        sb.AppendLine();

        sb.AppendLine("[DISABLE]");
        sb.AppendLine($"unregistersymbol({symbolName})");

        sb.AppendLine($"      </AssemblerScript>");
        sb.AppendLine($"    </CheatEntry>");
        sb.AppendLine("  </CheatEntries>");
        sb.AppendLine("</CheatTable>");

        return sb.ToString();
    }

    /// <summary>
    /// Generate CE XML with an AOB-scanning AA script root instead of a hardcoded address.
    /// The script scans for the GWorld AOB pattern at runtime, registers a unique CE symbol,
    /// and a "base" pointer entry dereferences it. All breadcrumb/field children nest under base.
    /// This format survives game restarts (re-scans AOB on script activation).
    /// </summary>
    public static string GenerateAobWrappedXml(
        string rootName,
        IReadOnlyList<BreadcrumbItem> breadcrumbs,
        IReadOnlyList<LiveFieldValue> currentFields,
        string aob, int aobPos, int aobLen, string moduleName,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs = null,
        bool collapsePointerNodes = false,
        int maxDropDownEntries = 512,
        Dictionary<string, List<LiveFieldValue>>? resolvedInstances = null,
        bool flattenChain = false)
    {
        var cleanedBc = CleanBreadcrumbs(breadcrumbs);

        _nextId = 100;
        _collapsePointerNodes = collapsePointerNodes;
        _maxDropDownEntries = maxDropDownEntries;
        _dropDownOwners = new Dictionary<string, string>();
        _dropDownDescriptions = new HashSet<string>(StringComparer.Ordinal);
        _emitPath = new HashSet<string>(StringComparer.Ordinal);
        _emitPointerDepth = 0;
        _resolvedStructsState = resolvedStructs;
        _resolvedInstancesState = resolvedInstances;

        // Generate unique symbol name to avoid CE overwrite on repeated copies
        var suffix = Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6");
        var symbolName = $"gworld_addr_{suffix}";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<CheatTable>");
        sb.AppendLine("  <CheatEntries>");

        // ---- Outer: AA Script entry ----
        var indent = "    ";
        sb.AppendLine($"{indent}<CheatEntry>");
        sb.AppendLine($"{indent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{indent}  <Description>\"GWorld \u2192 {symbolName}\"</Description>");
        sb.AppendLine($"{indent}  <Options moHideChildren=\"1\" moDeactivateChildrenAsWell=\"1\"/>");
        sb.AppendLine($"{indent}  <LastState/>");
        sb.AppendLine($"{indent}  <VariableType>Auto Assembler Script</VariableType>");
        sb.AppendLine($"{indent}  <AssemblerScript>");
        BuildAobAssemblerScript(sb, symbolName, aob, aobPos, aobLen);
        sb.AppendLine($"{indent}  </AssemblerScript>");
        sb.AppendLine($"{indent}  <CheatEntries>");

        // ---- "base" pointer entry: dereferences the symbol ----
        var baseIndent = indent + "    ";
        sb.AppendLine($"{baseIndent}<CheatEntry>");
        sb.AppendLine($"{baseIndent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{baseIndent}  <Description>\"base\"</Description>");
        sb.AppendLine($"{baseIndent}  <ShowAsHex>1</ShowAsHex>");
        sb.AppendLine($"{baseIndent}  <ShowAsSigned>0</ShowAsSigned>");
        sb.AppendLine($"{baseIndent}  <VariableType>8 Bytes</VariableType>");
        sb.AppendLine($"{baseIndent}  <Address>{symbolName}</Address>");
        sb.AppendLine($"{baseIndent}  <Offsets>");
        sb.AppendLine($"{baseIndent}    <Offset>0</Offset>");
        sb.AppendLine($"{baseIndent}  </Offsets>");
        sb.AppendLine($"{baseIndent}  <CheatEntries>");

        // ---- Inner breadcrumb chain (skip root at index 0, base replaces it) ----
        // With Collapse chain on, fold the whole spine into ONE entry under base;
        // otherwise emit the nested chain. spineLevels = group levels under base.
        var innerOpenTags = 0;
        int spineLevels;
        var folded = flattenChain ? FoldBreadcrumbSpine(cleanedBc) : null;
        if (folded != null)
        {
            EmitGroupOpen(sb, baseIndent + "    ", folded.Description,
                folded.Address, folded.Offsets, showAsHex: folded.ShowAsHex);
            innerOpenTags++;
            spineLevels = 1;
        }
        else
        {
            for (int i = 1; i < cleanedBc.Count; i++)
            {
                // Shared projection: see GenerateHierarchicalXml for the rationale.
                var step = ProjectBreadcrumb(cleanedBc[i]);
                var childIndent = baseIndent + "    " + new string(' ', (i - 1) * 2);
                EmitGroupOpen(sb, childIndent, step.Description,
                    $"+{step.Offset:X}",
                    step.DerefAfter ? new[] { 0 } : null,
                    showAsHex: step.DerefAfter);
                innerOpenTags++;
            }
            spineLevels = Math.Max(0, cleanedBc.Count - 1);
        }

        // ---- Leaf fields ----
        var leafIndent = baseIndent + "    " + new string(' ', spineLevels * 2);
        EmitFields(sb, leafIndent, currentFields, resolvedStructs, resolvedInstances);

        // ---- Close inner breadcrumb groups ----
        for (int i = innerOpenTags - 1; i >= 0; i--)
        {
            var closeIndent = baseIndent + "    " + new string(' ', i * 2);
            EmitGroupClose(sb, closeIndent);
        }

        // ---- Close "base" ----
        sb.AppendLine($"{baseIndent}  </CheatEntries>");
        sb.AppendLine($"{baseIndent}</CheatEntry>");

        // ---- Close AA Script entry ----
        sb.AppendLine($"{indent}  </CheatEntries>");
        sb.AppendLine($"{indent}</CheatEntry>");

        sb.AppendLine("  </CheatEntries>");
        sb.AppendLine("</CheatTable>");

        return sb.ToString();
    }

    /// <summary>
    /// Build the AA script body that scans for an AOB pattern and registers a CE symbol.
    /// Matches the format produced by the CEPlugin's BuildSymbolScanScript.
    /// </summary>
    private static void BuildAobAssemblerScript(StringBuilder sb, string symbolName,
        string aob, int aobPos, int aobLen)
    {
        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine();

        // Idempotent Lua helpers, shared verbatim with GenerateGWorldWalkedSymbolXml.
        AppendAobScanModuleUEHelper(sb);
        AppendCloseLuaEngineHelper(sb);

        // AOB entries table
        sb.AppendLine("local AOBs = {");
        sb.AppendLine($"  {{name='GWorld \u2192 {symbolName}', aob='{aob}', pos={aobPos}, aoblen={aobLen}, symbol='{symbolName}'}},");
        sb.AppendLine("}");
        sb.AppendLine();

        // Use CE global 'process' for the attached process module name
        sb.AppendLine("local module_name = process");
        sb.AppendLine();

        // Scan and register loop
        sb.AppendLine("for _, entry in ipairs(AOBs) do");
        sb.AppendLine("  local aob_addr_str = AOBScanModuleUE(module_name, entry.aob)");
        sb.AppendLine("  if aob_addr_str then");
        sb.AppendLine("    local aob_addr_val = tonumber(aob_addr_str, 16)");
        sb.AppendLine("    local offset_addr = aob_addr_val + entry.pos");
        sb.AppendLine("    local relative_offset = readInteger(offset_addr, true)");
        sb.AppendLine("    local final_addr = relative_offset + aob_addr_val + entry.aoblen");
        sb.AppendLine("    synchronize(function()");
        sb.AppendLine("      unregisterSymbol(entry.symbol)");
        sb.AppendLine("      registerSymbol(entry.symbol, final_addr)");
        sb.AppendLine("    end)");
        sb.AppendLine("    print(string.format('[SymbolScanner] %s registered at: %X', entry.name, final_addr))");
        sb.AppendLine("  else");
        sb.AppendLine("    print(string.format('[SymbolScanner] WARNING: AOB scan failed for %s', entry.name))");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("closeLuaEngine()");
        sb.AppendLine("{$asm}");
        sb.AppendLine();

        // DISABLE section
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine($"unregisterSymbol('{symbolName}')");
        sb.AppendLine("closeLuaEngine()");
        sb.AppendLine("{$asm}");
    }

    /// <summary>AOBScanModuleUE Lua helper (idempotent — won't redefine if already
    /// loaded). Shared verbatim by BuildAobAssemblerScript and the GWorld-walk script.</summary>
    private static void AppendAobScanModuleUEHelper(StringBuilder sb)
    {
        sb.AppendLine("if not AOBScanModuleUE then");
        sb.AppendLine("  function AOBScanModuleUE(moduleName, signature)");
        sb.AppendLine("    local baseAddr = nil");
        sb.AppendLine("    local maxAddr = 0");
        sb.AppendLine("    local modList");
        sb.AppendLine("    synchronize(function()");
        sb.AppendLine("      modList = enumModules()");
        sb.AppendLine("    end)");
        sb.AppendLine("    for _, mod in ipairs(modList) do");
        sb.AppendLine("      if string.lower(mod.Name) == string.lower(moduleName) then");
        sb.AppendLine("        baseAddr = mod.Address");
        sb.AppendLine("        maxAddr = baseAddr + mod.Size");
        sb.AppendLine("        break");
        sb.AppendLine("      end");
        sb.AppendLine("    end");
        sb.AppendLine("    if not baseAddr then return nil end");
        sb.AppendLine("    local ms = createMemScan()");
        sb.AppendLine("    synchronize(function()");
        sb.AppendLine("      ms.firstScan(soExactValue, vtByteArray, nil, signature,");
        sb.AppendLine("        nil, baseAddr, maxAddr, '+X-C-W', fsmNotAligned, '1', true, true, false, false)");
        sb.AppendLine("    end)");
        sb.AppendLine("    ms.waitTillDone()");
        sb.AppendLine("    local results = createFoundList(ms)");
        sb.AppendLine("    results.initialize()");
        sb.AppendLine("    local addr");
        sb.AppendLine("    synchronize(function()");
        sb.AppendLine("      if results.getCount() &gt; 0 then");
        sb.AppendLine("        addr = results[0]");
        sb.AppendLine("      end");
        sb.AppendLine("    end)");
        sb.AppendLine("    results.destroy()");
        sb.AppendLine("    ms.destroy()");
        sb.AppendLine("    return addr");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine("registerLuaFunctionHighlight('AOBScanModuleUE')");
        sb.AppendLine();
    }

    /// <summary>closeLuaEngine Lua helper (idempotent). Shared verbatim by
    /// BuildAobAssemblerScript and the GWorld-walk script.</summary>
    private static void AppendCloseLuaEngineHelper(StringBuilder sb)
    {
        sb.AppendLine("if not closeLuaEngine then");
        sb.AppendLine("  function closeLuaEngine()");
        sb.AppendLine("    synchronize(function()");
        sb.AppendLine("      getLuaEngine().Close()");
        sb.AppendLine("    end)");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine("registerLuaFunctionHighlight('closeLuaEngine')");
        sb.AppendLine();
    }

    /// <summary>
    /// Generate a RESTART-STABLE Auto Assembler script that registers the current
    /// object as a CE symbol by WALKING from GWorld down the navigation spine at
    /// enable time — instead of the hardcoded absolute address that dies on ASLR.
    ///
    /// The GWorld slot (&amp;GWorld) is recovered either by an AOB scan
    /// (<paramref name="useAob"/>=true — survives restart automatically) or
    /// hardcoded from <paramref name="gworldSlotAddr"/> (useAob=false — the user
    /// updates that value after a restart). The Lua then deref's *GWorld → UWorld*
    /// and applies each breadcrumb step (readQword on a pointer-deref crumb, plain
    /// add on an inline-struct crumb), null-guarding every hop, and finally
    /// registerSymbol's the resulting leaf address.
    ///
    /// The caller MUST pass a GWorld-rooted, forward-walkable spine: breadcrumbs[0]
    /// is the GWorld root (its offset is unused — the base deref replaces it) and
    /// every later crumb has FieldOffset &gt;= 0. breadcrumbs[^1] is the object being
    /// registered. Pointer math uses tonumber(hex,16)/readQword — both proven 64-bit
    /// in this project's shipped Lua (ue5_freeze_helper, BuildAobAssemblerScript).
    /// Internal (not public): the sole caller (LiveWalkerViewModel.BuildAaScript)
    /// enforces the forward-walkable precondition, and the tests reach it via
    /// InternalsVisibleTo — so the contract can't be bypassed by an outside caller.
    /// </summary>
    internal static string GenerateGWorldWalkedSymbolXml(
        string leafSymbol,
        IReadOnlyList<BreadcrumbItem> breadcrumbs,
        bool useAob,
        string aob, int aobPos, int aobLen,
        string gworldSlotAddr)
    {
        var cleanedBc = CleanBreadcrumbs(breadcrumbs);
        // Unique GWorld symbol per script so two enabled tables can't unregister
        // each other's GWorld on [DISABLE] (mirrors GenerateAobWrappedXml's suffix).
        var gworldSymbol = $"gworld_base_{Random.Shared.Next(0x100000, 0xFFFFFF):X6}";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<CheatTable>");
        sb.AppendLine("  <CheatEntries>");
        sb.AppendLine("    <CheatEntry>");
        sb.AppendLine("      <ID>0</ID>");
        sb.AppendLine($"      <Description>\"{leafSymbol}\"</Description>");
        sb.AppendLine("      <VariableType>Auto Assembler Script</VariableType>");
        sb.AppendLine("      <AssemblerScript>");

        // ---- ENABLE ----
        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine();
        if (useAob) AppendAobScanModuleUEHelper(sb);
        AppendCloseLuaEngineHelper(sb);

        // ---- Resolve the GWorld slot (&GWorld) into gworld_base + register it ----
        if (useAob)
        {
            sb.AppendLine($"local entry = {{aob='{aob}', pos={aobPos}, aoblen={aobLen}, symbol='{gworldSymbol}'}}");
            sb.AppendLine("local gworld_base = nil");
            sb.AppendLine("local aob_addr_str = AOBScanModuleUE(process, entry.aob)");
            sb.AppendLine("if aob_addr_str then");
            sb.AppendLine("  local aob_addr_val = tonumber(aob_addr_str, 16)");
            sb.AppendLine("  local relative_offset = readInteger(aob_addr_val + entry.pos, true)");
            sb.AppendLine("  gworld_base = relative_offset + aob_addr_val + entry.aoblen");
            sb.AppendLine("  synchronize(function()");
            sb.AppendLine("    unregisterSymbol(entry.symbol)");
            sb.AppendLine("    registerSymbol(entry.symbol, gworld_base)");
            sb.AppendLine("  end)");
            sb.AppendLine("  print(string.format('[GWorldWalk] %s = %X', entry.symbol, gworld_base))");
            sb.AppendLine("else");
            sb.AppendLine("  print('[GWorldWalk] WARNING: GWorld AOB scan failed')");
            sb.AppendLine("end");
        }
        else
        {
            var baseHex = NormalizeHex(gworldSlotAddr);
            sb.AppendLine($"local gworld_base = tonumber('{baseHex}', 16)   -- GWorld slot pointer; UPDATE THIS after a game restart");
            sb.AppendLine("synchronize(function()");
            sb.AppendLine($"  unregisterSymbol('{gworldSymbol}')");
            sb.AppendLine($"  registerSymbol('{gworldSymbol}', gworld_base)");
            sb.AppendLine("end)");
        }
        sb.AppendLine();

        // ---- Walk the spine: *GWorld -> UWorld* -> ... -> leaf ----
        // Every hop guards `addr and addr ~= 0`: CE readQword returns NIL (not 0)
        // on an unreadable page, so a mid-walk null (e.g. a streaming/World-Partition
        // transition) must short-circuit before `readQword(nil + off)` / `nil + off`
        // throws — mirrors the shipped idiom in ue5_freeze_helper.lua.
        sb.AppendLine("local addr = gworld_base and readQword(gworld_base) or 0   -- *GWorld = UWorld*");
        for (int i = 1; i < cleanedBc.Count; i++)
        {
            var step = ProjectBreadcrumb(cleanedBc[i]);
            var note = SanitizeLuaComment(step.Description);
            if (step.DerefAfter)
                sb.AppendLine($"if addr and addr ~= 0 then addr = readQword(addr + 0x{step.Offset:X}) end   -- {note}");
            else
                sb.AppendLine($"if addr and addr ~= 0 then addr = addr + 0x{step.Offset:X} end   -- {note} (inline)");
        }
        sb.AppendLine();

        // ---- Register the leaf (only when the walk produced a live address) ----
        sb.AppendLine("if addr and addr ~= 0 then");
        sb.AppendLine("  synchronize(function()");
        sb.AppendLine($"    unregisterSymbol('{leafSymbol}')");
        sb.AppendLine($"    registerSymbol('{leafSymbol}', addr)");
        sb.AppendLine("  end)");
        sb.AppendLine($"  print(string.format('[GWorldWalk] {leafSymbol} = %X', addr))");
        sb.AppendLine("else");
        sb.AppendLine($"  print('[GWorldWalk] WARNING: null pointer mid-walk; {leafSymbol} not registered')");
        sb.AppendLine("end");
        sb.AppendLine("closeLuaEngine()");
        sb.AppendLine("{$asm}");
        sb.AppendLine();

        // ---- DISABLE ----
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine($"unregisterSymbol('{leafSymbol}')");
        sb.AppendLine($"unregisterSymbol('{gworldSymbol}')");
        sb.AppendLine("closeLuaEngine()");
        sb.AppendLine("{$asm}");

        sb.AppendLine("      </AssemblerScript>");
        sb.AppendLine("    </CheatEntry>");
        sb.AppendLine("  </CheatEntries>");
        sb.AppendLine("</CheatTable>");
        return sb.ToString();
    }

    /// <summary>Strip a leading 0x/0X and surrounding whitespace, leaving bare hex
    /// digits for a Lua tonumber(.,16). Returns "0" for empty input.</summary>
    private static string NormalizeHex(string? addr)
    {
        var s = (addr ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return string.IsNullOrEmpty(s) ? "0" : s;
    }

    /// <summary>Make a breadcrumb description safe inside a single-line Lua comment
    /// embedded in XML: strip newlines (would end the comment early) and XML-escape
    /// &amp;/&lt;/&gt; (CE un-escapes them back before Lua sees the text).</summary>
    private static string SanitizeLuaComment(string? s)
        => string.IsNullOrEmpty(s) ? ""
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
               .Replace("\r", " ").Replace("\n", " ");

    // ========================================
    // Breadcrumb cleaning
    // ========================================

    /// <summary>
    /// Remove cycles from the breadcrumb navigation path before XML generation.
    ///
    /// A cycle occurs when the user navigates away from an object and later returns to
    /// the same address (e.g., Child -> Parent -> Child again). The intermediate entries
    /// (the detour) are removed, keeping only the shortest path.
    ///
    /// Example: [A, B, C, A, B] -> A appears at 0 and 3 -> remove [1..3] -> [A, B]
    /// This gives the clean CE pointer chain: Root(A) -> field(B) instead of
    /// Root(A) -> field(B) -> Outer(C) -> field(A) -> field(B).
    /// </summary>
    /// <summary>
    /// Collapse runs of CONSECUTIVE breadcrumb crumbs that resolve to the exact
    /// same deref step — same field offset, same resolved address, same name, and
    /// same container/pointer kind. Such a pair is always redundant (you can't move
    /// from object X to X via the same field) and would otherwise emit a duplicate
    /// CE deref level. This happens e.g. when a Locate-in-GWorld path leaves a
    /// synthetic container crumb and the user then re-enters that same container,
    /// stacking two identical <c>Foo(C,+N)</c> crumbs. The LATER crumb is kept (it
    /// carries the live <c>ContainerField</c> for a real container view; an earlier
    /// path-synthetic crumb has none). Unlike the cycle pass below, this also
    /// collapses container-view crumbs (which the cycle pass deliberately skips).
    /// </summary>
    internal static IReadOnlyList<BreadcrumbItem> DedupeConsecutiveBreadcrumbs(
        IReadOnlyList<BreadcrumbItem> breadcrumbs)
    {
        if (breadcrumbs.Count <= 1) return breadcrumbs;
        var result = new List<BreadcrumbItem>(breadcrumbs.Count);
        foreach (var bc in breadcrumbs)
        {
            if (result.Count > 0)
            {
                var prev = result[^1];
                if (prev.FieldOffset == bc.FieldOffset
                    && prev.IsContainerView == bc.IsContainerView
                    && string.Equals(prev.Address, bc.Address, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(prev.FieldName, bc.FieldName, StringComparison.Ordinal))
                {
                    result[^1] = bc;  // keep the later (richer) crumb
                    continue;
                }
            }
            result.Add(bc);
        }
        return result;
    }

    internal static IReadOnlyList<BreadcrumbItem> CleanBreadcrumbs(IReadOnlyList<BreadcrumbItem> breadcrumbs)
    {
        if (breadcrumbs.Count <= 1) return breadcrumbs;

        // First collapse consecutive duplicate crumbs (e.g. a path-synthetic
        // container crumb followed by the user re-entering the same container),
        // then run the cycle-removal pass below.
        var result = new List<BreadcrumbItem>(DedupeConsecutiveBreadcrumbs(breadcrumbs));

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < result.Count && !changed; i++)
            {
                for (int j = i + 1; j < result.Count; j++)
                {
                    // Container view breadcrumbs (Array/Map/Set) share their parent's address
                    // by design — they represent TArray::Data / TSparseArray::Data dereference,
                    // not a navigation cycle. Skip them as cycle endpoints.
                    if (result[j].IsContainerView) continue;

                    if (string.Equals(result[i].Address, result[j].Address, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found cycle from i to j -- remove entries (i+1) through j inclusive.
                        // Keeps the first occurrence at i and continues with j+1 onward.
                        result.RemoveRange(i + 1, j - i);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return result;
    }

    // ========================================
    // Breadcrumb chain flattening (Collapse chain)
    // ========================================

    /// <summary>
    /// One emit step in the navigation spine: the offset added from the parent's
    /// resolved address, whether a pointer dereference follows the add, and the
    /// node's display name. The normal (nested) and the flattened (collapsed) emit
    /// paths BOTH derive their steps from this single projection, so they can never
    /// disagree about a breadcrumb's pointer semantics -- and any breadcrumb type
    /// is handled identically by both as long as it is either inline (no
    /// dereference) or a single dereference.
    /// </summary>
    private readonly record struct BreadcrumbStep(int Offset, bool DerefAfter, string Description);

    private static BreadcrumbStep ProjectBreadcrumb(BreadcrumbItem bc)
        => new(bc.FieldOffset,
               bc.IsPointerDeref || bc.IsContainerView,
               bc.IsContainerView ? bc.Label : bc.FieldName);

    /// <summary>Result of collapsing a breadcrumb spine into one CE entry.</summary>
    internal sealed record FoldedChain(string Address, int[]? Offsets, string Description, bool ShowAsHex);

    /// <summary>
    /// Collapse the navigation spine (every breadcrumb after the root) into a
    /// SINGLE CE multi-level-pointer entry, turning a deep GWorld -> ... -> target
    /// chain into base -> one folded node -> target field instead of N nested
    /// groups. Returns null when there are fewer than 2 navigation breadcrumbs to
    /// merge (folding a single node just reproduces the normal output) -- the
    /// caller then emits the nested chain unchanged.
    ///
    /// Math (verified against CE's pointer resolution; see docs/export-formats.md).
    /// CE resolves an entry with Address=+Xbase and Offsets O[0..m-1] as:
    ///   start = parentResolved + Xbase;  p = deref(start);
    ///   for k = m-1..1: p = deref(p + O[k]);  finalAddr = p + O[0]
    /// i.e. the FIRST listed offset O[0] is the OUTERMOST (added without a final
    /// deref) and the LAST listed offset O[m-1] is the first deref after the base.
    /// Folding a spine of (offset, derefAfter) steps:
    ///   - accumulate each run of offsets up to (and including) a deref step into D[]
    ///   - F = the trailing inline run after the last deref (0 if it ended on a deref)
    ///   - Address = +D[0];  Offsets (document order) = [F] ++ reverse(D[1..])
    /// A pure-inline spine (no deref at all) folds to Address=+F with no Offsets.
    ///
    /// Robustness: this reads ONLY (Offset, DerefAfter) per step and never inspects
    /// the leaf-field subtree, so new expandable field types emitted by EmitFields
    /// are neither seen nor affected. Every breadcrumb the app creates is inline or
    /// single-deref (DataTable's 2-level deref is modelled as TWO single-deref
    /// breadcrumbs), so the fold is total over the current breadcrumb model.
    /// </summary>
    internal static FoldedChain? FoldBreadcrumbSpine(IReadOnlyList<BreadcrumbItem> cleanedBc)
    {
        // cleanedBc[0] is the root/base (kept as-is by the caller). The spine is
        // cleanedBc[1..]; need >= 2 nodes there to actually merge anything.
        if (cleanedBc.Count < 3) return null;

        var d = new List<int>(cleanedBc.Count - 1);   // deref-terminated segment sums
        int seg = 0;
        var descParts = new List<string>(cleanedBc.Count - 1);
        for (int i = 1; i < cleanedBc.Count; i++)
        {
            var step = ProjectBreadcrumb(cleanedBc[i]);
            descParts.Add(step.Description);
            seg += step.Offset;
            if (step.DerefAfter) { d.Add(seg); seg = 0; }
        }
        int f = seg;

        // Joined spine so the user can see exactly what was collapsed (decision #1).
        var description = string.Join(" ▸ ", descParts);

        if (d.Count == 0)
        {
            // Pure-inline spine: a single horizontal offset, no dereference.
            return new FoldedChain($"+{f:X}", null, description, ShowAsHex: false);
        }

        // CE document order: outermost (final, no-deref) offset F first, then the
        // deref offsets in reverse depth order. Summed hex per offset (decision #2).
        var offsets = new int[d.Count];
        offsets[0] = f;
        for (int k = 1; k < d.Count; k++)
            offsets[k] = d[d.Count - k];
        return new FoldedChain($"+{d[0]:X}", offsets, description, ShowAsHex: true);
    }

    // ========================================
    // Private helpers
    // ========================================

    /// <summary>
    /// Emit all leaf fields, handling scalars, resolved structs, and navigable placeholders.
    /// All fields use Address=+{field.Offset} (no Offsets) because parent breadcrumb/group
    /// already resolved any pointer dereference via its own Offsets=[0].
    /// </summary>
    private static void EmitFields(StringBuilder sb, string indent,
        IReadOnlyList<LiveFieldValue> fields,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs,
        Dictionary<string, List<LiveFieldValue>>? resolvedInstances = null)
    {
        foreach (var field in fields)
        {
            // Check if this StructProperty has pre-resolved children. Key is
            // StructDataAddr (absolute address) — unique across instances, so
            // the same dict can serve nested struct fields inside drilled
            // pointer targets without offset-collision.
            if (field.TypeName == "StructProperty"
                && resolvedStructs != null
                && !string.IsNullOrEmpty(field.StructDataAddr)
                && resolvedStructs.TryGetValue(field.StructDataAddr, out var structChildren)
                && structChildren.Count > 0)
            {
                EmitResolvedStruct(sb, indent, field, structChildren);
                continue;
            }

            // Pointer drill-down: ObjectProperty / ClassProperty / Weak/Soft/Lazy/Interface
            // with a pre-resolved target → emit GroupHeader+Offsets=[0] and recurse into
            // the target's fields. CE will dereference *(parent + field.Offset) and lay
            // the children out at their natural offsets within the target.
            //
            // Lookup is by PtrAddress so two fields pointing to the same instance share
            // the same resolved field list (this is also what enables cycle protection
            // from ResolvePointerInstancesAsync).
            if (resolvedInstances != null
                && IsObjectPropertyType(field.TypeName)
                && !string.IsNullOrEmpty(field.PtrAddress)
                && field.PtrAddress != "0x0"
                && resolvedInstances.TryGetValue(field.PtrAddress, out var ptrChildren)
                && ptrChildren.Count > 0)
            {
                EmitDrilledPointer(sb, indent, field, ptrChildren, resolvedStructs, resolvedInstances);
                continue;
            }

            // OptionalProperty: TOptional<T> wraps an inner value at field+0.
            // - TOptional<Struct>: walker stamps StructDataAddr/StructClassAddr
            //   when set, so we can render the struct sub-fields inline (no
            //   pointer dereference; struct lives directly at field+0).
            // - All other inner shapes (scalar / pointer / weak / etc): emit
            //   as a flat 8-byte hex leaf so the user has a watchable address
            //   for the value slot. The trailing bIsSet byte (when present)
            //   isn't surfaced separately — UE intrusively encodes it for
            //   FString / FName / FText / pointer types, and the byte's
            //   location for non-intrusive scalars depends on inner T size
            //   that's not exposed to the C# emitter.
            if (field.TypeName == "OptionalProperty")
            {
                if (resolvedStructs != null
                    && !string.IsNullOrEmpty(field.StructDataAddr)
                    && resolvedStructs.TryGetValue(field.StructDataAddr, out var optStructChildren)
                    && optStructChildren.Count > 0)
                {
                    EmitResolvedStruct(sb, indent, field, optStructChildren);
                }
                else
                {
                    // Flat leaf — at minimum CE shows the first 8 bytes of
                    // the optional slot so the user can poke at the value.
                    EmitLeaf(sb, indent, field.Name,
                        new CeFieldInfo("8 Bytes", ShowAsHex: true),
                        $"+{field.Offset:X}", null);
                }
                continue;
            }

            // ArrayProperty: emit as group with element children (Phase C).
            // Multicast delegates are exposed as implicit DelegateProperty arrays
            // (the field's first 8 bytes are the InvocationList::Data pointer,
            // matching TArray addressing — Offsets=[0] derefs it correctly).
            if (field.ArrayCount >= 0
                && (field.TypeName == "ArrayProperty"
                    || field.TypeName == "MulticastInlineDelegateProperty"
                    || field.TypeName == "MulticastDelegateProperty"))
            {
                EmitArrayProperty(sb, indent, field);
                continue;
            }

            // MapProperty: emit as group with key/value children per element
            if (field.TypeName == "MapProperty" && field.MapCount >= 0)
            {
                EmitMapProperty(sb, indent, field);
                continue;
            }

            // SetProperty: emit as group with element children
            if (field.TypeName == "SetProperty" && field.SetCount >= 0)
            {
                EmitSetProperty(sb, indent, field);
                continue;
            }

            // DataTableRows: emit as 2-level deref group (TSparseArray.Data → uint8* row → fields)
            if (field.TypeName == "DataTableRows" && field.DataTableRowCount > 0)
            {
                EmitDataTableRowsProperty(sb, indent, field);
                continue;
            }

            // StrProperty: emit as Unicode string with pointer dereference to wchar_t* Data
            if (field.TypeName == "StrProperty")
            {
                EmitStringLeaf(sb, indent, field.Name, $"+{field.Offset:X}",
                    offsets: [0], unicode: true);
                continue;
            }

            var ceField = MapCeField(field);
            if (ceField != null)
            {
                // Non-array EnumProperty/ByteProperty: DropDownList support
                var ddLink = TryGetEnumDropDown(field);
                EmitLeaf(sb, indent, ddLink.desc ?? field.Name, ceField,
                    $"+{field.Offset:X}", null,
                    dropDownContent: ddLink.content,
                    dropDownListLink: ddLink.link);
            }
            else if (field.IsNavigable)
            {
                EmitNavigableField(sb, indent, field,
                    $"+{field.Offset:X}", null);
            }
        }
    }

    /// <summary>
    /// Check if a non-array enum field should have a DropDownList.
    /// Returns (content, link, desc): content for first occurrence, link for shared reuse,
    /// desc = unique description to use in the CE entry (ensures DropDownListLink matching).
    /// </summary>
    private static (string? content, string? link, string? desc) TryGetEnumDropDown(LiveFieldValue field)
    {
        if (field.TypeName is not ("EnumProperty" or "ByteProperty")) return (null, null, null);
        if (field.EnumEntries is not { Count: > 0 }) return (null, null, null);

        var maxDd = _maxDropDownEntries > 0 ? _maxDropDownEntries : 512;
        if (field.EnumEntries.Count > maxDd) return (null, null, null);

        _dropDownOwners ??= new Dictionary<string, string>();
        var enumKey = field.EnumAddr;

        if (!string.IsNullOrEmpty(enumKey) && _dropDownOwners.TryGetValue(enumKey, out var existing))
        {
            // Shared: link to first occurrence
            return (null, existing, null);
        }

        // First occurrence: emit DropDownList content; use unique description for link matching
        var content = BuildDropDownContent(field.EnumEntries.Select(e => (e.Value, e.Name)));
        var desc = EnsureUniqueDropDownDesc(field.Name);
        if (!string.IsNullOrEmpty(enumKey))
            _dropDownOwners[enumKey] = desc;
        return (content, null, desc);
    }

    /// <summary>
    /// Emit a StructProperty with pre-resolved inner fields as a CE group.
    /// Struct is inline (not a pointer), so Address=+{structOffset}, no Offsets.
    /// Children are flattened (nested structs already expanded with dot-prefixed names).
    /// Each child's Offset is relative to the struct start.
    /// </summary>
    /// <summary>
    /// Emit an ObjectProperty / Class / Weak / Soft / Lazy / Interface field whose
    /// pointer target was pre-resolved by ResolvePointerInstancesAsync. The leaf
    /// becomes a GroupHeader with Address=+{fieldOffset}, Offsets=[0] (CE
    /// dereferences *(parent + fieldOffset)) and the resolved target's fields as
    /// children at their natural offsets within the target instance.
    ///
    /// Reuses the standard EmitFields recursion so nested struct flattening,
    /// container expansion, enum DropDownLists, and further pointer drill-downs
    /// all work uniformly inside the target — depth was already capped during
    /// the resolve phase, so this loop terminates.
    /// </summary>
    private static void EmitDrilledPointer(StringBuilder sb, string indent,
        LiveFieldValue field,
        List<LiveFieldValue> children,
        Dictionary<string, List<LiveFieldValue>>? resolvedStructs,
        Dictionary<string, List<LiveFieldValue>> resolvedInstances)
    {
        // ---- Cycle / depth guards ----
        // ResolvePointerInstancesAsync caches resolved[X] keyed by PtrAddress, so
        // back-pointers (UWorld -> PersistentLevel -> OwningWorld) are still
        // populated in the dictionary. Without an emit-side path check, drilling
        // would oscillate between A's and B's child lists indefinitely.
        _emitPath ??= new HashSet<string>(StringComparer.Ordinal);
        bool alreadyOnPath = !string.IsNullOrEmpty(field.PtrAddress)
                             && _emitPath.Contains(field.PtrAddress);
        bool depthExceeded = _emitPointerDepth >= MaxEmitPointerDepth;

        if (alreadyOnPath || depthExceeded)
        {
            // Emit a flat 8-byte hex leaf so the user keeps a watchable address
            // for the pointer, instead of nothing or an unbounded group. The
            // description tags the reason so it's not mysterious in CE.
            var reason = alreadyOnPath ? " (cycle elided)" : " (max drill depth reached)";
            var classTag = !string.IsNullOrEmpty(field.PtrClassName)
                ? $" ({field.PtrClassName})" : "";
            EmitLeaf(sb, indent, field.Name + classTag + reason,
                new CeFieldInfo("8 Bytes", ShowAsHex: true),
                $"+{field.Offset:X}", null);
            return;
        }

        // Description: include the resolved class name when known so the user
        // can tell BP_X (UCharacter) from BP_X (UPawn) without expanding.
        var description = !string.IsNullOrEmpty(field.PtrClassName)
            ? $"{field.Name} ({field.PtrClassName})"
            : field.Name;

        // Address=+{fieldOffset}, Offsets=[0] — CE dereferences the pointer
        // and treats children's +{N} as offsets from the resolved target.
        EmitGroupOpen(sb, indent, description, $"+{field.Offset:X}", new[] { 0 },
            showAsHex: true);

        // Push self onto the path before recursing; pop on exit (try/finally
        // is overkill — EmitFields doesn't throw under normal use, and a
        // missing pop just makes the SAME pointer non-drillable next time
        // within this call, which is benign).
        bool pushed = !string.IsNullOrEmpty(field.PtrAddress)
                      && _emitPath.Add(field.PtrAddress);
        _emitPointerDepth++;

        var childIndent = indent + "  ";
        EmitFields(sb, childIndent, children, resolvedStructs, resolvedInstances);

        _emitPointerDepth--;
        if (pushed) _emitPath.Remove(field.PtrAddress);

        EmitGroupClose(sb, indent);
    }

    private static void EmitResolvedStruct(StringBuilder sb, string indent,
        LiveFieldValue structField, List<LiveFieldValue> children)
    {
        // Struct is inline: just offset from parent, no dereference
        var address = $"+{structField.Offset:X}";

        // Struct group header with struct type name in description
        var description = !string.IsNullOrEmpty(structField.StructTypeName)
            ? $"{structField.Name} ({structField.StructTypeName})"
            : structField.Name;

        EmitGroupOpen(sb, indent, description, address, null);
        var childIndent = indent + "  ";

        // Children's offsets are relative to the struct base; the struct group is
        // at +structOffset, so EmitFields lays each child at +childOffset under it.
        // Delegating to EmitFields (instead of a bespoke loop) means struct children
        // that are themselves structs / pointers / Maps / Sets expand richly when
        // they were resolved — the core of the drilldown contract.
        EmitFields(sb, childIndent, children, _resolvedStructsState, _resolvedInstancesState);

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Emit an ArrayProperty as a CE group with per-element children.
    /// Scalar arrays (Float, Int, Bool, Byte, Enum, Name) get individual leaf entries.
    /// Non-scalar arrays (Struct, Object) or empty arrays emit as placeholder only.
    ///
    /// TArray addressing:
    /// - Group header: Address=+{fieldOffset}, Offsets=[0] → dereferences TArray.Data pointer
    /// - Element children: Address=+{N*elemSize} → simple offset from the dereferenced Data pointer
    /// </summary>
    private static void EmitArrayProperty(StringBuilder sb, string indent,
        LiveFieldValue field)
    {
        // Build description: "FieldName [N x Type (SizeB)]"
        var typeLabel = !string.IsNullOrEmpty(field.ArrayStructType)
            ? field.ArrayStructType : field.ArrayInnerType;
        var desc = field.ArrayCount > 0 && !string.IsNullOrEmpty(typeLabel)
            ? $"{field.Name} [{field.ArrayCount} x {typeLabel} ({field.ArrayElemSize}B)]"
            : field.Name;

        // Phase F: struct array with resolved sub-fields → per-element group emission
        if (field.ArrayInnerType == "StructProperty"
            && field.ArrayElements is { Count: > 0 }
            && field.ArrayElements[0].StructFields is { Count: > 0 })
        {
            EmitStructArrayProperty(sb, indent, field, desc);
            return;
        }

        // StructProperty array without resolved sub-fields:
        // Still emit with Offsets=[0] for TArray.Data deref and per-element placeholder groups.
        // CE users can manually add sub-entries for the struct fields within each element.
        if (field.ArrayInnerType == "StructProperty"
            && field.ArrayCount > 0 && field.ArrayElemSize > 0)
        {
            EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
            var elemIndent = indent + "  ";

            if (field.ArrayElements is { Count: > 0 })
            {
                foreach (var elem in field.ArrayElements)
                {
                    int elemByteOffset = elem.Index * field.ArrayElemSize;
                    EmitGroupPlaceholder(sb, elemIndent, $"[{elem.Index}]", $"+{elemByteOffset:X}", null);
                }
            }

            EmitGroupClose(sb, indent);
            return;
        }

        // Phase G: TArray<TSoftObjectPtr/TSoftClassPtr> — emit per-element
        // struct group with WeakPtr leaf at +0 + FName leaf(s) at +0x10 (and
        // +0x10+fnameSize for UE5.1+'s FTopLevelAssetPath layout). Without
        // this, the inner element collapses to a single 8B WeakPtr hex blob
        // and the FSoftObjectPath::AssetPathName / PackageName is invisible.
        // Soft array layout metadata (fnameSize + FTopLevelAssetPath flag)
        // comes from the DLL — see Ubel.cpp Phase G handler.
        if ((field.ArrayInnerType == "SoftObjectProperty"
             || field.ArrayInnerType == "SoftClassProperty")
            && field.SoftArrayFNameSize > 0
            && field.ArrayCount > 0 && field.ArrayElemSize > 0)
        {
            EmitSoftObjectArrayProperty(sb, indent, field, desc);
            return;
        }

        // TArray<ObjectProperty> with pre-resolved element targets → per-element
        // drilled group (one EmitDrilledPointer per pointer slot). The array group
        // derefs TArray.Data (Offsets=[0]); each element pointer is then dereffed by
        // its own Offsets=[0] so the target's fields lay out at their natural
        // offsets. Elements whose target wasn't resolved (depth/cycle/limit, or a
        // null slot) fall back to the flat 8-byte leaf. Only taken when at least
        // one element actually resolved, so depth=0 / unresolved arrays keep the
        // prior generic-leaf behavior below.
        if (IsRawObjectPtrArrayInner(field.ArrayInnerType)
            && _resolvedInstancesState != null
            && field.ArrayElements is { Count: > 0 }
            && field.ArrayElemSize > 0
            && field.ArrayElements.Any(e => !string.IsNullOrEmpty(e.PtrAddress)
                    && e.PtrAddress != "0x0"
                    && _resolvedInstancesState.ContainsKey(e.PtrAddress)))
        {
            EmitObjectArrayProperty(sb, indent, field, desc);
            return;
        }

        // Map inner type to CE type
        var ceElem = MapInnerTypeToCeField(field.ArrayInnerType);

        // Non-scalar, empty, or no inline elements → placeholder only (no deref needed)
        if (ceElem == null || field.ArrayCount <= 0
            || field.ArrayElements == null || field.ArrayElements.Count == 0)
        {
            EmitGroupPlaceholder(sb, indent, desc, $"+{field.Offset:X}", null);
            return;
        }

        // CE DropDownList: determine if this array should have dropdown support.
        // DropDownList goes on the parent GroupHeader; all children use DropDownListLink.
        _dropDownOwners ??= new Dictionary<string, string>();
        string? dropDownContent = null;
        string? dropDownLinkTarget = null;
        var maxDd = _maxDropDownEntries > 0 ? _maxDropDownEntries : 512;
        bool isEnumArray = field.ArrayInnerType is "EnumProperty" or "ByteProperty"
            && field.ArrayEnumEntries is { Count: > 0 } && field.ArrayEnumEntries.Count <= maxDd;
        bool isNameArray = field.ArrayInnerType == "NameProperty"
            && field.ArrayElements is { Count: > 0 } && field.ArrayElements.Count <= maxDd;
        // Fallback: enum/byte array with per-element enum names but no full UEnum entries list.
        // Build DropDownList from element values (like NameProperty), no sharing.
        bool isEnumFallback = !isEnumArray
            && field.ArrayInnerType is "EnumProperty" or "ByteProperty"
            && field.ArrayElements is { Count: > 0 } && field.ArrayElements.Count <= maxDd
            && field.ArrayElements.Any(e => !string.IsNullOrEmpty(e.EnumName));

        if (isEnumArray)
        {
            var enumKey = field.ArrayEnumAddr;
            if (!string.IsNullOrEmpty(enumKey) && _dropDownOwners.TryGetValue(enumKey, out var existing))
            {
                // Shared: this parent and all children link to first occurrence's parent
                dropDownLinkTarget = existing;
            }
            else
            {
                // First occurrence: parent gets DropDownList, children link to this parent.
                // Ensure unique description (CE uses Description text as DropDownListLink key).
                dropDownContent = BuildDropDownContent(
                    field.ArrayEnumEntries!.Select(e => (e.Value, e.Name)));
                desc = EnsureUniqueDropDownDesc(desc);
                dropDownLinkTarget = desc;
                if (!string.IsNullOrEmpty(enumKey))
                    _dropDownOwners[enumKey] = desc;
            }
        }
        else if (isEnumFallback)
        {
            // Build from current element enum values (deduplicated)
            var seen = new HashSet<long>();
            var pairs = new List<(long, string)>();
            foreach (var e in field.ArrayElements!)
            {
                if (seen.Add(e.RawIntValue) && !string.IsNullOrEmpty(e.EnumName))
                    pairs.Add((e.RawIntValue, e.EnumName));
            }
            if (pairs.Count > 0)
            {
                dropDownContent = BuildDropDownContent(pairs);
                desc = EnsureUniqueDropDownDesc(desc);
                dropDownLinkTarget = desc;
            }
        }
        else if (isNameArray)
        {
            // Build from current element values (deduplicated)
            var seen = new HashSet<long>();
            var pairs = new List<(long, string)>();
            foreach (var e in field.ArrayElements!)
            {
                if (seen.Add(e.RawIntValue) && !string.IsNullOrEmpty(e.Value))
                    pairs.Add((e.RawIntValue, e.Value));
            }
            if (pairs.Count > 0)
            {
                dropDownContent = BuildDropDownContent(pairs);
                desc = EnsureUniqueDropDownDesc(desc);
                dropDownLinkTarget = desc;
            }
        }

        // Array group: Address=+{fieldOffset}, Offsets=[0] to dereference TArray.Data pointer.
        // TArray layout: { Data* +0x00, Count +0x08, Max +0x0C }
        // Offsets=[0] reads the pointer at TArray+0x00 (the Data pointer).
        // DropDownList/DropDownListLink is emitted on this parent group node.
        if (dropDownContent != null)
        {
            EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 },
                dropDownContent: dropDownContent);
        }
        else if (dropDownLinkTarget != null)
        {
            // Shared enum: parent links to first occurrence's parent
            EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 },
                dropDownListLink: dropDownLinkTarget);
        }
        else
        {
            EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        }
        var childIndent = indent + "  ";

        foreach (var elem in field.ArrayElements)
        {
            // Element description: simplified [N] when dropdown is active, else full names
            string elemDesc;
            if (dropDownLinkTarget != null)
            {
                // DisplayValueAsItem=1 handles showing the resolved name in CE's Value column
                elemDesc = $"[{elem.Index}]";
            }
            else if (!string.IsNullOrEmpty(elem.PtrName))
            {
                elemDesc = !string.IsNullOrEmpty(elem.PtrClassName)
                    ? $"[{elem.Index}] {elem.PtrName} ({elem.PtrClassName})"
                    : $"[{elem.Index}] {elem.PtrName}";
            }
            else if (!string.IsNullOrEmpty(elem.EnumName))
                elemDesc = $"[{elem.Index}] {elem.EnumName}";
            else
                elemDesc = $"[{elem.Index}]";

            // Element: simple offset from the already-dereferenced Data pointer
            int elemByteOffset = elem.Index * field.ArrayElemSize;

            if (dropDownLinkTarget != null)
            {
                // All children link to the parent (or first occurrence's parent) Description
                EmitLeaf(sb, childIndent, elemDesc, ceElem,
                    $"+{elemByteOffset:X}", null,
                    dropDownListLink: dropDownLinkTarget);
            }
            else
            {
                EmitLeaf(sb, childIndent, elemDesc, ceElem,
                    $"+{elemByteOffset:X}", null);
            }
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Emit a TArray&lt;ObjectProperty&gt; whose element pointer targets were
    /// pre-resolved by the drilldown resolver. The array group derefs TArray.Data
    /// (Offsets=[0]); each resolved element becomes a drilled GroupHeader (its own
    /// Offsets=[0] derefs the 8-byte element pointer, children at their natural
    /// offsets) via EmitDrilledPointer — so nested structs/pointers/containers
    /// expand and the same cycle/depth guards apply. Unresolved or null elements
    /// fall back to a flat 8-byte pointer leaf (the pre-fix behavior).
    /// </summary>
    private static void EmitObjectArrayProperty(StringBuilder sb, string indent,
        LiveFieldValue field, string desc)
    {
        // Array group: Address=+{fieldOffset}, Offsets=[0] derefs TArray.Data.
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        var elemIndent = indent + "  ";

        foreach (var elem in field.ArrayElements!)
        {
            int elemByteOffset = elem.Index * field.ArrayElemSize;
            // Base name without the class suffix — EmitDrilledPointer re-appends
            // "(ClassName)" itself, so the synth field must NOT carry it (else the
            // class shows twice). The leaf fallback adds it explicitly below.
            var baseName = !string.IsNullOrEmpty(elem.PtrName)
                ? $"[{elem.Index}] {elem.PtrName}"
                : $"[{elem.Index}]";

            if (!string.IsNullOrEmpty(elem.PtrAddress) && elem.PtrAddress != "0x0"
                && _resolvedInstancesState != null
                && _resolvedInstancesState.TryGetValue(elem.PtrAddress, out var children)
                && children.Count > 0)
            {
                // Synthetic pointer field so EmitDrilledPointer derefs the element
                // slot (+elemByteOffset, Offsets=[0]) and lays out the target's fields.
                var synth = new LiveFieldValue
                {
                    Name = baseName,
                    TypeName = field.ArrayInnerType,
                    Offset = elemByteOffset,
                    PtrAddress = elem.PtrAddress,
                    PtrName = elem.PtrName,
                    PtrClassName = elem.PtrClassName,
                };
                EmitDrilledPointer(sb, elemIndent, synth, children,
                    _resolvedStructsState, _resolvedInstancesState!);
                continue;
            }

            // Unresolved / null → flat 8-byte pointer leaf (matches the generic
            // object-array leaf shape: "[N] PtrName (PtrClassName)").
            var leafName = !string.IsNullOrEmpty(elem.PtrClassName)
                ? $"{baseName} ({elem.PtrClassName})"
                : baseName;
            EmitLeaf(sb, elemIndent, leafName,
                new CeFieldInfo("8 Bytes", ShowAsHex: true),
                $"+{elemByteOffset:X}", null);
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Phase G: Emit a TArray&lt;TSoftObjectPtr|TSoftClassPtr&gt; with per-element
    /// struct groups so the FName leaf(s) at the FSoftObjectPath sub-offset
    /// are addressable in CE — instead of a single 8B WeakPtr hex blob.
    ///
    /// Element layout (DLL-provided fname size + FTopLevelAssetPath flag):
    ///   +0x00 FWeakObjectPtr (8B: int32 ObjectIndex + int32 SerialNumber)
    ///   +0x08 Tag (4B) + pad (4B)
    ///   +0x10 FName AssetPathName  (UE4 / UE5.0)         — single FName
    ///         OR FName PackageName (UE5.1+ FTopLevelAssetPath)
    ///   +0x10+fnameSize  FName AssetName (UE5.1+ only)
    ///
    /// FName CE rendering: ComparisonIndex (uint32) at field+0 — emitted as
    /// a "4 Bytes" leaf with a deduplicated DropDownList built from the live
    /// elements so users see the resolved asset path text in CE's Value column.
    ///
    /// Array group: Address=+{fieldOffset}, Offsets=[0] (deref TArray.Data)
    /// Element group: Address=+{N*elemSize}, no Offsets (inline within Data)
    /// Leaves: Address=+{subOffset} (relative to element start)
    /// </summary>
    private static void EmitSoftObjectArrayProperty(StringBuilder sb, string indent,
        LiveFieldValue field, string desc)
    {
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        var elemIndent = indent + "  ";

        // Build a shared DropDownList for the AssetPath/PackageName FName from
        // the live element values. Each elem.RawIntValue is the FName
        // ComparisonIndex (set by ReadSoftObjectArrayElements when the path
        // resolves); fall back to no DropDown if values are missing.
        var maxDd = _maxDropDownEntries > 0 ? _maxDropDownEntries : 512;
        string? sharedDropDown = null;
        if (field.ArrayElements is { Count: > 0 } && field.ArrayElements.Count <= maxDd)
        {
            var seen = new HashSet<long>();
            var pairs = new List<(long, string)>();
            foreach (var e in field.ArrayElements)
            {
                if (e.RawIntValue == 0 || string.IsNullOrEmpty(e.Value)) continue;
                if (seen.Add(e.RawIntValue))
                    pairs.Add((e.RawIntValue, e.Value));
            }
            if (pairs.Count > 0)
                sharedDropDown = BuildDropDownContent(pairs);
        }

        var ceWeakPtr  = new CeFieldInfo("8 Bytes", ShowAsHex: true);
        var ceFNameIdx = new CeFieldInfo("4 Bytes");

        foreach (var elem in field.ArrayElements ?? new List<ArrayElementValue>())
        {
            int elemByteOffset = elem.Index * field.ArrayElemSize;
            string elemDesc = !string.IsNullOrEmpty(elem.Value)
                ? $"[{elem.Index}] {elem.Value}"
                : $"[{elem.Index}]";

            EmitGroupOpen(sb, elemIndent, elemDesc, $"+{elemByteOffset:X}", null);
            var fieldIndent = elemIndent + "  ";

            // FWeakObjectPtr at +0 — useful when the asset is currently loaded
            // (8 bytes packing ObjectIndex + SerialNumber).
            EmitLeaf(sb, fieldIndent, "WeakPtr", ceWeakPtr, "+0", null);

            // FName ComparisonIndex (and Number at +4) for the
            // AssetPathName / PackageName at +0x10.
            string firstFNameLabel = field.SoftArrayIsTopLevelAssetPath
                ? "PackageName"
                : "AssetPath";
            if (sharedDropDown != null)
            {
                EmitLeaf(sb, fieldIndent, firstFNameLabel, ceFNameIdx,
                    "+10", null, dropDownContent: sharedDropDown);
            }
            else
            {
                EmitLeaf(sb, fieldIndent, firstFNameLabel, ceFNameIdx,
                    "+10", null);
            }

            // UE5.1+: FTopLevelAssetPath has a second FName (AssetName) right
            // after PackageName. Stride is the same fnameSize used by the
            // backing FName.
            if (field.SoftArrayIsTopLevelAssetPath)
            {
                int assetNameOffset = 0x10 + field.SoftArrayFNameSize;
                EmitLeaf(sb, fieldIndent, "AssetName", ceFNameIdx,
                    $"+{assetNameOffset:X}", null);
            }

            EmitGroupClose(sb, elemIndent);
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Phase F: Emit struct array with per-element groups containing field children.
    /// Array group: Offsets=[0] (deref TArray.Data)
    /// Element group: Address=+{N*elemSize}, no Offsets (inline within Data)
    /// Field leaf: Address=+{fieldOffset} (relative to element start)
    /// </summary>
    private static void EmitStructArrayProperty(StringBuilder sb, string indent,
        LiveFieldValue field, string desc)
    {
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        var elemIndent = indent + "  ";

        ulong arrDataBase = ParseHexAddr(field.ArrayDataAddr);
        bool canResolveElem = arrDataBase != 0
                              && !string.IsNullOrEmpty(field.ArrayStructClassAddr)
                              && _resolvedStructsState != null;

        foreach (var elem in field.ArrayElements!)
        {
            int elemByteOffset = elem.Index * field.ArrayElemSize;
            var elemDesc = $"[{elem.Index}]";

            // Prefer a full re-walk of the element struct (nested structs/maps expand)
            // when the resolver walked it; fall back to the shallow per-element preview.
            string elemStructAddr = canResolveElem
                ? AbsAddr(arrDataBase, (long)elem.Index * field.ArrayElemSize) : "";
            if (canResolveElem
                && _resolvedStructsState!.TryGetValue(elemStructAddr, out var rs) && rs.Count > 0)
            {
                var sv = new LiveFieldValue
                {
                    Name = elemDesc, TypeName = "StructProperty", Offset = elemByteOffset,
                    StructDataAddr = elemStructAddr, StructClassAddr = field.ArrayStructClassAddr,
                    StructTypeName = field.ArrayStructType,
                };
                EmitFields(sb, elemIndent, new[] { sv }, _resolvedStructsState, _resolvedInstancesState);
                continue;
            }

            if (elem.StructFields is { Count: > 0 })
            {
                // Element group: inline offset from Data pointer
                EmitGroupOpen(sb, elemIndent, elemDesc, $"+{elemByteOffset:X}", null);
                var fieldIndent = elemIndent + "  ";

                foreach (var sf in elem.StructFields)
                {
                    // Enum width follows the sub-field's real byte size (a 1-byte
                    // enum must NOT be read as 4 bytes — that pulls in the next
                    // field's bytes). Other scalars/pointers map by type name.
                    var ceField = sf.TypeName == "EnumProperty"
                        ? new CeFieldInfo(CeWidthForSize(sf.Size))
                        : MapInnerTypeToCeField(sf.TypeName);
                    if (ceField != null)
                    {
                        EmitLeaf(sb, fieldIndent, sf.Name, ceField, $"+{sf.Offset:X}", null);
                    }
                    else
                    {
                        // Non-scalar sub-field (nested struct / map / set / array):
                        // the Phase F array read doesn't carry its inner data, so
                        // surface it as a collapsed placeholder folder at its offset
                        // instead of dropping it silently — the user still sees every
                        // field and its address (and can add children in CE).
                        EmitGroupPlaceholder(sb, fieldIndent, sf.Name, $"+{sf.Offset:X}", null);
                    }
                }

                EmitGroupClose(sb, elemIndent);
            }
            else
            {
                EmitGroupPlaceholder(sb, elemIndent, elemDesc, $"+{elemByteOffset:X}", null);
            }
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Emit a MapProperty as a CE group with per-element children.
    /// TMap uses TSparseArray internally. Data pointer is at +0x00 (same as TArray).
    /// Element stride = ComputeSetElementStride(valOffset + valueSize), where valOffset is aligned.
    /// Each allocated element: key at +0, value at +valOffset (aligned) within the element.
    ///
    /// TSparseArray addressing:
    /// - Group header: Address=+{fieldOffset}, Offsets=[0] → dereferences TSparseArray.Data pointer
    /// - Element group: Address=+{allocatedIndex * stride} → element start from Data pointer
    ///   - Key leaf: Address=+0, type from MapKeyType
    ///   - Value leaf: Address=+{keySize}, type from MapValueType
    /// </summary>
    private static void EmitMapProperty(StringBuilder sb, string indent, LiveFieldValue field)
    {
        var keyLabel = !string.IsNullOrEmpty(field.MapKeyType) ? field.MapKeyType : "?";
        var valLabel = !string.IsNullOrEmpty(field.MapValueType) ? field.MapValueType : "?";
        var desc = field.MapCount > 0
            ? $"{field.Name} {{Map: {field.MapCount}, {keyLabel} \u2192 {valLabel}}}"
            : field.Name;

        // Need elements + sizes for addressable CE entries.
        if (field.MapCount <= 0
            || field.MapElements == null || field.MapElements.Count == 0
            || field.MapKeySize <= 0 || field.MapValueSize <= 0)
        {
            EmitGroupPlaceholder(sb, indent, desc, $"+{field.Offset:X}", null);
            return;
        }

        // Scalar key/value → leaf; struct/object values drill via EmitFields. The
        // value column NEVER bakes the resolved name into the description (the stored
        // int can change at runtime) — Name/Enum values instead get a CE DropDownList
        // (rawInt → resolved name) on the map group that the leaves link to, so CE
        // shows the LIVE name. Enum key/value widths follow the real byte size.
        int valOffset = field.MapValueOffset > 0 ? field.MapValueOffset : field.MapKeySize;
        int stride = ComputeSetElementStride(valOffset + field.MapValueSize);
        ulong dataBase = ParseHexAddr(field.MapDataAddr);
        bool valStruct = field.MapValueType == "StructProperty"
                         && !string.IsNullOrEmpty(field.MapValueStructAddr);
        bool valScalar = !valStruct && !IsObjectPropertyType(field.MapValueType);

        var ceKey = field.MapKeyType == "EnumProperty"
            ? new CeFieldInfo(CeWidthForSize(field.MapKeySize))
            : MapInnerTypeToCeField(field.MapKeyType);

        // Shared value DropDownList (rawInt → name) for Name/Enum values.
        string? valueDropDown = null;
        string? valueDropLink = null;
        if (valScalar && (field.MapValueType == "NameProperty" || field.MapValueType == "EnumProperty"))
        {
            int ddBytes = field.MapValueType == "NameProperty" ? 4 : field.MapValueSize;
            var maxDd = _maxDropDownEntries > 0 ? _maxDropDownEntries : 512;
            var seen = new HashSet<long>();
            var pairs = new List<(long, string)>();
            foreach (var e in field.MapElements)
            {
                if (string.IsNullOrEmpty(e.Value)) continue;
                long raw = ParseHexLeInt(e.ValueHex, ddBytes);
                if (seen.Add(raw)) pairs.Add((raw, e.Value));
            }
            if (pairs.Count > 0 && pairs.Count <= maxDd)
            {
                valueDropDown = BuildDropDownContent(pairs);
                desc = EnsureUniqueDropDownDesc(desc);
                valueDropLink = desc;
            }
        }

        // Map group: Address=+{fieldOffset}, Offsets=[0] (deref TSparseArray.Data)
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 }, dropDownContent: valueDropDown);
        var elemIndent = indent + "  ";

        foreach (var elem in field.MapElements)
        {
            int elemByteOffset = elem.Index * stride;
            var elemDesc = !string.IsNullOrEmpty(elem.KeyPtrName)
                ? $"[{elem.Index}] {elem.KeyPtrName}"
                : !string.IsNullOrEmpty(elem.Key)
                    ? $"[{elem.Index}] {elem.Key}"
                    : $"[{elem.Index}]";

            // Element group: inline from Data pointer
            EmitGroupOpen(sb, elemIndent, elemDesc, $"+{elemByteOffset:X}", null);
            var fieldIndent = elemIndent + "  ";

            // Key leaf at +0 — label only, no baked-in dynamic value.
            if (ceKey != null)
                EmitLeaf(sb, fieldIndent, "Key", ceKey, "+0", null);

            // Value at +valOffset.
            if (valStruct || IsObjectPropertyType(field.MapValueType))
            {
                var valueField = BuildElementValue("Value", field.MapValueType, valOffset, field.MapValueSize,
                    valStruct, AbsAddr(dataBase, elemByteOffset + valOffset),
                    field.MapValueStructAddr, field.MapValueStructType,
                    elem.ValuePtrAddress, elem.ValuePtrName, elem.ValuePtrClassName);
                EmitFields(sb, fieldIndent, new[] { valueField }, _resolvedStructsState, _resolvedInstancesState);
            }
            else
            {
                var ceVal = field.MapValueType == "EnumProperty"
                    ? new CeFieldInfo(CeWidthForSize(field.MapValueSize))
                    : MapInnerTypeToCeField(field.MapValueType);
                if (ceVal != null)
                    EmitLeaf(sb, fieldIndent, "Value", ceVal, $"+{valOffset:X}", null,
                        dropDownListLink: valueDropLink);
            }

            EmitGroupClose(sb, elemIndent);
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Build the synthetic VALUE field of a container element for the emit phase:
    /// a struct (StructDataAddr set → drills when resolved), an object pointer
    /// (PtrAddress set → drills when resolved), or a scalar leaf. Offset is relative
    /// to the element group; StructDataAddr is absolute (matches the resolver key).
    /// </summary>
    private static LiveFieldValue BuildElementValue(
        string name, string typeName, int offset, int size,
        bool isStruct, string structDataAddr, string structClassAddr, string structTypeName,
        string? ptrAddr, string? ptrName, string? ptrClassName)
    {
        if (isStruct && !string.IsNullOrEmpty(structDataAddr))
            return new LiveFieldValue
            {
                Name = name, TypeName = "StructProperty", Offset = offset, Size = size,
                StructDataAddr = structDataAddr, StructClassAddr = structClassAddr,
                StructTypeName = structTypeName,
            };
        if (IsObjectPropertyType(typeName) && !string.IsNullOrEmpty(ptrAddr) && ptrAddr != "0x0")
            return new LiveFieldValue
            {
                Name = name, TypeName = typeName, Offset = offset, Size = size,
                PtrAddress = ptrAddr!, PtrName = ptrName ?? "", PtrClassName = ptrClassName ?? "",
            };
        return new LiveFieldValue { Name = name, TypeName = typeName, Offset = offset, Size = size };
    }

    /// <summary>
    /// Emit a SetProperty as a CE group with per-element children.
    /// TSet uses TSparseArray. Data pointer at +0x00.
    /// Element stride = ComputeSetElementStride(elemSize).
    ///
    /// TSparseArray addressing:
    /// - Group header: Address=+{fieldOffset}, Offsets=[0] → dereferences TSparseArray.Data pointer
    /// - Element leaf: Address=+{allocatedIndex * stride}, type from SetElemType
    /// </summary>
    private static void EmitSetProperty(StringBuilder sb, string indent, LiveFieldValue field)
    {
        var elemLabel = !string.IsNullOrEmpty(field.SetElemType) ? field.SetElemType : "?";
        var desc = field.SetCount > 0
            ? $"{field.Name} {{Set: {field.SetCount}, {elemLabel}}}"
            : field.Name;

        // Empty / no elements → placeholder. Struct/object elements (ceElem == null)
        // now expand via EmitFields instead of collapsing the whole set.
        if (field.SetCount <= 0
            || field.SetElements == null || field.SetElements.Count == 0
            || field.SetElemSize <= 0)
        {
            EmitGroupPlaceholder(sb, indent, desc, $"+{field.Offset:X}", null);
            return;
        }

        var ceElem = MapInnerTypeToCeField(field.SetElemType);   // null for struct/object
        int stride = ComputeSetElementStride(field.SetElemSize);
        ulong dataBase = ParseHexAddr(field.SetDataAddr);
        bool elemStruct = field.SetElemType == "StructProperty"
                          && !string.IsNullOrEmpty(field.SetElemStructAddr);

        // Set group: Address=+{fieldOffset}, Offsets=[0] (deref TSparseArray.Data)
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        var childIndent = indent + "  ";

        foreach (var elem in field.SetElements)
        {
            int elemByteOffset = elem.Index * stride;
            var elemDesc = !string.IsNullOrEmpty(elem.KeyPtrName)
                ? $"[{elem.Index}] {elem.KeyPtrName}"
                : !string.IsNullOrEmpty(elem.Key)
                    ? $"[{elem.Index}] {elem.Key}"
                    : $"[{elem.Index}]";

            if (ceElem != null)
            {
                // Scalar element → flat leaf (unchanged).
                EmitLeaf(sb, childIndent, elemDesc, ceElem, $"+{elemByteOffset:X}", null);
            }
            else
            {
                // Struct / object element → expand via the shared EmitFields dispatch.
                var ev = BuildElementValue(elemDesc, field.SetElemType, elemByteOffset, field.SetElemSize,
                    elemStruct, AbsAddr(dataBase, elemByteOffset),
                    field.SetElemStructAddr, field.SetElemStructType,
                    elem.KeyPtrAddress, elem.KeyPtrName, elem.KeyPtrClassName);
                EmitFields(sb, childIndent, new[] { ev }, _resolvedStructsState, _resolvedInstancesState);
            }
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Emit DataTable RowMap as a CE group with 2-level pointer dereference.
    ///
    /// DataTable RowMap addressing (2-level deref):
    /// - Level 1: Address=+{RowMapOffset}, Offsets=[0] → dereferences TSparseArray.Data pointer
    /// - Level 2: Address=+{sparseIndex*stride+fnameSize}, Offsets=[0] → dereferences uint8* row data pointer
    /// - Level 3: Address=+{fieldOffset} → inline field within the row data buffer
    ///
    /// Unlike TMap where values are inline (no second deref), DataTable RowMap stores uint8*
    /// pointers that must be dereferenced to reach the actual row data.
    /// </summary>
    private static void EmitDataTableRowsProperty(StringBuilder sb, string indent,
        LiveFieldValue field)
    {
        var structName = !string.IsNullOrEmpty(field.DataTableStructName)
            ? field.DataTableStructName : "Row";
        var desc = $"{field.Name} [DataTable: {field.DataTableRowCount} x {structName}]";

        // Need row data for addressable CE entries
        if (field.DataTableRowData == null || field.DataTableRowData.Count == 0
            || field.DataTableStride <= 0 || field.DataTableFNameSize <= 0)
        {
            EmitGroupPlaceholder(sb, indent, desc, $"+{field.Offset:X}", null);
            return;
        }

        // Level 1: RowMap group — deref TSparseArray.Data
        EmitGroupOpen(sb, indent, desc, $"+{field.Offset:X}", new[] { 0 });
        var rowIndent = indent + "  ";

        foreach (var row in field.DataTableRowData)
        {
            // Level 2: Row — deref uint8* at sparseIndex*stride+fnameSize
            int rowPtrOffset = row.SparseIndex * field.DataTableStride + field.DataTableFNameSize;
            var rowDesc = $"[{row.SparseIndex}] {row.RowName}";

            if (row.Fields.Count == 0)
            {
                EmitGroupPlaceholder(sb, rowIndent, rowDesc, $"+{rowPtrOffset:X}", new[] { 0 });
                continue;
            }

            EmitGroupOpen(sb, rowIndent, rowDesc, $"+{rowPtrOffset:X}", new[] { 0 });
            var fieldIndent = rowIndent + "  ";

            // Level 3: Fields — inline offset within dereferenced row data
            foreach (var rowField in row.Fields)
            {
                // StrProperty within row: Unicode string with pointer deref
                if (rowField.TypeName == "StrProperty")
                {
                    EmitStringLeaf(sb, fieldIndent, rowField.Name, $"+{rowField.Offset:X}",
                        offsets: [0], unicode: true);
                    continue;
                }

                var ceField = MapCeField(rowField);
                if (ceField != null)
                {
                    var ddLink = TryGetEnumDropDown(rowField);
                    EmitLeaf(sb, fieldIndent, ddLink.desc ?? rowField.Name, ceField,
                        $"+{rowField.Offset:X}", null,
                        dropDownContent: ddLink.content,
                        dropDownListLink: ddLink.link);
                }
                else if (rowField.IsNavigable)
                {
                    EmitNavigableField(sb, fieldIndent, rowField,
                        $"+{rowField.Offset:X}", null);
                }
                else if (rowField.TypeName == "ArrayProperty" && rowField.ArrayCount >= 0)
                {
                    EmitArrayProperty(sb, fieldIndent, rowField);
                }
                else if (rowField.TypeName == "MapProperty" && rowField.MapCount >= 0)
                {
                    EmitMapProperty(sb, fieldIndent, rowField);
                }
                else if (rowField.TypeName == "SetProperty" && rowField.SetCount >= 0)
                {
                    EmitSetProperty(sb, fieldIndent, rowField);
                }
            }

            EmitGroupClose(sb, rowIndent);
        }

        EmitGroupClose(sb, indent);
    }

    /// <summary>
    /// Compute TSetElement stride: AlignUp(elemSize, 4) + 8 (HashNextId + HashIndex).
    /// Mirrors Mem::ComputeSetElementStride in the DLL.
    /// </summary>
    private static int ComputeSetElementStride(int elemSize)
    {
        int hashStart = (elemSize + 3) & ~3;  // align to 4
        return hashStart + 8;  // + HashNextId(4) + HashIndex(4)
    }

    /// <summary>Emit a group header that will contain child entries (opens CheatEntries block).</summary>
    private static void EmitGroupOpen(StringBuilder sb, string indent, string description,
        string address, int[]? offsets, bool showAsHex = false, string? varType = null,
        string? dropDownContent = null, string? dropDownListLink = null)
    {
        sb.AppendLine($"{indent}<CheatEntry>");
        sb.AppendLine($"{indent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{indent}  <Description>\"{description}\"</Description>");
        // CE DropDownList: inline list on this group, or link to another group's list
        if (dropDownContent != null)
            sb.AppendLine($"{indent}  <DropDownList DisplayValueAsItem=\"1\">{dropDownContent}</DropDownList>");
        else if (dropDownListLink != null)
            sb.AppendLine($"{indent}  <DropDownListLink>{EscapeXmlContent(dropDownListLink)}</DropDownListLink>");
        if (showAsHex)
            sb.AppendLine($"{indent}  <ShowAsHex>1</ShowAsHex>");
        sb.AppendLine($"{indent}  <ShowAsSigned>0</ShowAsSigned>");
        sb.AppendLine($"{indent}  <GroupHeader>1</GroupHeader>");
        // Collapse every non-root group folder (pointer/array deref nodes, struct
        // groups, AND element folders like [1]). Root is excluded — its address is
        // absolute, not "+...".
        if (_collapsePointerNodes && address.StartsWith("+"))
            sb.AppendLine($"{indent}  <Options moHideChildren=\"1\" moDeactivateChildrenAsWell=\"1\"/>");
        if (varType != null)
            sb.AppendLine($"{indent}  <VariableType>{varType}</VariableType>");
        sb.AppendLine($"{indent}  <Address>{address}</Address>");
        EmitOffsets(sb, indent, offsets);
        sb.AppendLine($"{indent}  <CheatEntries>");
    }

    /// <summary>Close a group header's CheatEntries block.</summary>
    private static void EmitGroupClose(StringBuilder sb, string indent)
    {
        sb.AppendLine($"{indent}  </CheatEntries>");
        sb.AppendLine($"{indent}</CheatEntry>");
    }

    /// <summary>
    /// Emit a group placeholder -- a GroupHeader with no children.
    /// Used for navigable struct/pointer fields at leaf level when resolution is unavailable.
    /// Pointer fields get ShowAsHex=1.
    /// </summary>
    private static void EmitGroupPlaceholder(StringBuilder sb, string indent, string description,
        string address, int[]? offsets, bool showAsHex = false)
    {
        sb.AppendLine($"{indent}<CheatEntry>");
        sb.AppendLine($"{indent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{indent}  <Description>\"{description}\"</Description>");
        if (showAsHex)
            sb.AppendLine($"{indent}  <ShowAsHex>1</ShowAsHex>");
        sb.AppendLine($"{indent}  <ShowAsSigned>0</ShowAsSigned>");
        sb.AppendLine($"{indent}  <GroupHeader>1</GroupHeader>");
        // Collapse every non-root group folder (see EmitGroupOpen). Root is
        // excluded — its address is absolute, not "+...".
        if (_collapsePointerNodes && address.StartsWith("+"))
            sb.AppendLine($"{indent}  <Options moHideChildren=\"1\" moDeactivateChildrenAsWell=\"1\"/>");
        sb.AppendLine($"{indent}  <Address>{address}</Address>");
        EmitOffsets(sb, indent, offsets);
        sb.AppendLine($"{indent}</CheatEntry>");
    }

    /// <summary>
    /// Emit a scalar leaf entry with proper CE type, signedness, and bit field support.
    /// </summary>
    private static void EmitLeaf(StringBuilder sb, string indent, string description,
        CeFieldInfo ceField, string address, int[]? offsets,
        string? dropDownContent = null, string? dropDownListLink = null)
    {
        sb.AppendLine($"{indent}<CheatEntry>");
        sb.AppendLine($"{indent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{indent}  <Description>\"{description}\"</Description>");
        // CE DropDownList: inline list content (first occurrence of this enum)
        if (dropDownContent != null)
            sb.AppendLine($"{indent}  <DropDownList DisplayValueAsItem=\"1\">{dropDownContent}</DropDownList>");
        // CE DropDownListLink: reference to another entry's DropDownList
        else if (dropDownListLink != null)
            sb.AppendLine($"{indent}  <DropDownListLink>{EscapeXmlContent(dropDownListLink)}</DropDownListLink>");
        if (ceField.ShowAsHex)
            sb.AppendLine($"{indent}  <ShowAsHex>1</ShowAsHex>");
        sb.AppendLine($"{indent}  <ShowAsSigned>{(ceField.IsSigned ? 1 : 0)}</ShowAsSigned>");
        sb.AppendLine($"{indent}  <VariableType>{ceField.VariableType}</VariableType>");
        if (ceField.BitStart >= 0)
        {
            sb.AppendLine($"{indent}  <BitStart>{ceField.BitStart}</BitStart>");
            sb.AppendLine($"{indent}  <BitLength>{ceField.BitLength}</BitLength>");
            sb.AppendLine($"{indent}  <ShowAsBinary>0</ShowAsBinary>");
        }
        sb.AppendLine($"{indent}  <Address>{address}</Address>");
        EmitOffsets(sb, indent, offsets);
        sb.AppendLine($"{indent}</CheatEntry>");
    }

    /// <summary>
    /// Emit a CE String leaf with proper Length/Unicode/CodePage/ZeroTerminate.
    /// Used for StrProperty which stores FString = { wchar_t* Data, int32 Count, int32 Max }.
    /// The Offsets=[0] dereferences the Data pointer to reach the actual character buffer.
    /// </summary>
    private static void EmitStringLeaf(StringBuilder sb, string indent, string description,
        string address, int[]? offsets, bool unicode, int length = 256)
    {
        sb.AppendLine($"{indent}<CheatEntry>");
        sb.AppendLine($"{indent}  <ID>{_nextId++}</ID>");
        sb.AppendLine($"{indent}  <Description>\"{description}\"</Description>");
        sb.AppendLine($"{indent}  <ShowAsSigned>0</ShowAsSigned>");
        sb.AppendLine($"{indent}  <VariableType>String</VariableType>");
        sb.AppendLine($"{indent}  <Length>{length}</Length>");
        sb.AppendLine($"{indent}  <Unicode>{(unicode ? 1 : 0)}</Unicode>");
        sb.AppendLine($"{indent}  <CodePage>0</CodePage>");
        sb.AppendLine($"{indent}  <ZeroTerminate>1</ZeroTerminate>");
        sb.AppendLine($"{indent}  <Address>{address}</Address>");
        EmitOffsets(sb, indent, offsets);
        sb.AppendLine($"{indent}</CheatEntry>");
    }

    /// <summary>Emit Offsets block if offsets are provided.</summary>
    private static void EmitOffsets(StringBuilder sb, string indent, int[]? offsets)
    {
        if (offsets != null && offsets.Length > 0)
        {
            sb.AppendLine($"{indent}  <Offsets>");
            foreach (var o in offsets)
                sb.AppendLine($"{indent}    <Offset>{o:X}</Offset>");
            sb.AppendLine($"{indent}  </Offsets>");
        }
    }

    /// <summary>
    /// Build DropDownList content string from value:name pairs.
    /// Format: newline-separated "value:name" entries (decimal values, no leading zeros).
    /// </summary>
    private static string BuildDropDownContent(IEnumerable<(long value, string name)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine();  // newline after opening tag
        foreach (var (v, n) in entries)
            sb.AppendLine($"{v}:{n}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Escape special characters for XML element text content.</summary>
    private static string EscapeXmlContent(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Ensure a DropDownList parent Description is unique.
    /// CE uses Description text as DropDownListLink key, so duplicates cause ambiguity.
    /// Appends ".001", ".002" etc. suffix if the description was already used.
    /// </summary>
    private static string EnsureUniqueDropDownDesc(string desc)
    {
        _dropDownDescriptions ??= new HashSet<string>(StringComparer.Ordinal);
        if (_dropDownDescriptions.Add(desc))
            return desc;  // first use — unique

        // Collision: append suffix .001, .002, ...
        for (int i = 1; i < 1000; i++)
        {
            var suffixed = $"{desc}.{i:D3}";
            if (_dropDownDescriptions.Add(suffixed))
                return suffixed;
        }
        return desc;  // fallback (should never happen)
    }

    /// <summary>
    /// Emit a navigable field as a group placeholder (no resolved children available).
    /// Pointer fields get ShowAsHex=1.
    /// </summary>
    private static void EmitNavigableField(StringBuilder sb, string indent,
        LiveFieldValue field, string address, int[]? offsets)
    {
        EmitGroupPlaceholder(sb, indent, field.Name, address, offsets,
            showAsHex: field.IsPointerNavigation);
    }

    /// <summary>
    /// Map UE property type + field metadata to CE field info.
    /// Returns null for unsupported/unknown types (struct, array, delegate, etc.).
    ///
    /// Signedness rules:
    /// - Signed: IntProperty (int32), Int8Property, Int16Property, Int64Property
    /// - Unsigned: UInt32Property, UInt16Property, UInt64Property, ByteProperty
    ///
    /// BoolProperty rules:
    /// - If BoolBitIndex >= 0: Binary type with BitStart/BitLength (CE bit field)
    /// - Otherwise: Byte type (fallback for bool without bit info)
    /// </summary>
    /// <summary>
    /// CE integer-width keyword for a property's byte size. UE enums/bytes can be
    /// 1/2/4/8 bytes wide; emitting the wrong width makes CE read neighbouring
    /// fields — e.g. a 1-byte enum read as "4 Bytes" pulls in the next 3 bytes
    /// (the cause of the SaveSlotList enums reporting 5376 instead of 0).
    /// </summary>
    private static string CeWidthForSize(int size) => size switch
    {
        1 => "Byte",
        2 => "2 Bytes",
        4 => "4 Bytes",
        8 => "8 Bytes",
        _ => "4 Bytes",   // unknown / unreported size → legacy default
    };

    private static CeFieldInfo? MapCeField(LiveFieldValue field)
    {
        return field.TypeName switch
        {
            "FloatProperty" => new CeFieldInfo("Float"),
            "DoubleProperty" => new CeFieldInfo("Double"),

            // Signed integers
            "Int8Property" => new CeFieldInfo("Byte", IsSigned: true),
            "Int16Property" => new CeFieldInfo("2 Bytes", IsSigned: true),
            "IntProperty" => new CeFieldInfo("4 Bytes", IsSigned: true),
            "Int64Property" => new CeFieldInfo("8 Bytes", IsSigned: true),

            // Unsigned integers
            "ByteProperty" => new CeFieldInfo("Byte"),
            "UInt16Property" => new CeFieldInfo("2 Bytes"),
            "UInt32Property" => new CeFieldInfo("4 Bytes"),
            "UInt64Property" => new CeFieldInfo("8 Bytes"),

            // Bool with bit field support
            "BoolProperty" when field.BoolBitIndex >= 0 =>
                new CeFieldInfo("Binary", BitStart: field.BoolBitIndex, BitLength: 1),
            "BoolProperty" => new CeFieldInfo("Byte"),

            // FName index
            "NameProperty" => new CeFieldInfo("4 Bytes"),

            // Enum -- width follows the underlying integer size (uint8 default,
            // but can be 1/2/4/8). Reading a 1-byte enum as 4 bytes corrupts it.
            "EnumProperty" => new CeFieldInfo(CeWidthForSize(field.Size)),

            // StrProperty is handled by EmitStringLeaf (not MapCeField)
            // TextProperty: FText internal pointer chain — CE can't resolve, show as hex
            "TextProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Pointer-shaped property types — single field is a raw 8B pointer.
            // Without these, MapCeField returns null and EmitFields falls through
            // to EmitNavigableField -> EmitGroupPlaceholder, which emits a
            // <GroupHeader>1</GroupHeader> entry with NO <VariableType> — CE
            // shows it as an empty folder rather than a readable pointer.
            // Listing them here promotes them to a proper "8 Bytes / ShowAsHex"
            // leaf so Copy CE Field / Copy CE XML for an ObjectProperty selection
            // produces a usable pointer entry CE can actually display.
            "ObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "ClassProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "WeakObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Soft/Lazy object: FName-based — CE can't resolve, show as hex
            "SoftObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "SoftClassProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "LazyObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Interface: first 8 bytes is UObject*, show as pointer
            "InterfaceProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            _ => null // Unknown -- not a scalar (StructProperty, ArrayProperty, etc.)
        };
    }

    /// <summary>
    /// CE memory-record type descriptor for the AOBMaker <c>CreateMemoryRecord</c> pipe
    /// command: a numeric CE <c>TVariableType</c> plus the signed / hex display flags.
    /// </summary>
    public readonly record struct CeRecordType(int ValueType, bool IsSigned, bool ShowAsHex);

    // CE TVariableType numeric codes for AOBMaker CreateMemoryRecord.
    // Source: AOBMaker docs/API-CEPlugin.md (the CE plugin SDK header is WRONG — use these).
    private const int CeVtByte = 0, CeVtWord = 1, CeVtDword = 2, CeVtQword = 3,
                      CeVtSingle = 4, CeVtDouble = 5, CeVtString = 6,
                      CeVtUnicodeString = 7, CeVtByteArray = 8, CeVtBinary = 9;

    /// <summary>
    /// Map a Live Walker field to a CE memory-record type for a one-click "Add to CE"
    /// push (AOBMaker <c>CreateMemoryRecord</c>). Reuses the same UE→CE mapping that drives
    /// Copy CE XML / Copy CE Field so the single-record push stays consistent with the
    /// clipboard exports. Non-scalar fields (struct/array/etc.) fall back to 8 Bytes /
    /// ShowAsHex; bit-field bools — which the single-record command can't fully express —
    /// fall back to the containing Byte.
    /// </summary>
    public static CeRecordType MapFieldToCeRecordType(LiveFieldValue field)
    {
        var info = MapCeField(field);
        if (info == null)
            return PointerRecordType; // non-scalar (struct/array/etc.) -> 8 Bytes hex
        return new CeRecordType(KeywordToValueType(info.VariableType), info.IsSigned, info.ShowAsHex);
    }

    /// <summary>
    /// CE record type for a raw 8-byte pointer target (a dereferenced object/struct base):
    /// 8 Bytes shown as hex. Used by the one-click "Add ptr target to CE" push.
    /// </summary>
    public static CeRecordType PointerRecordType => new(CeVtQword, IsSigned: false, ShowAsHex: true);

    /// <summary>
    /// Convert a CE VariableType keyword (as produced by <see cref="MapCeField"/>) to its
    /// numeric <c>TVariableType</c> code. "Binary" (a bit-field bool) maps to Byte since the
    /// single-record command carries no bit start/length — pushing the containing byte is the
    /// most useful target for a "what accesses this address" breakpoint.
    /// </summary>
    private static int KeywordToValueType(string keyword) => keyword switch
    {
        "Byte" => CeVtByte,
        "2 Bytes" => CeVtWord,
        "4 Bytes" => CeVtDword,
        "8 Bytes" => CeVtQword,
        "Float" => CeVtSingle,
        "Double" => CeVtDouble,
        "String" => CeVtString,
        "Binary" => CeVtByte,
        _ => CeVtQword,
    };

    /// <summary>
    /// Map an array inner type name to CE field info.
    /// Similar to MapCeField but takes a type name string (for array element types).
    /// BoolProperty in arrays = full byte (no bitfield).
    /// Returns null for non-scalar types (StructProperty, ObjectProperty, etc.).
    /// </summary>
    private static CeFieldInfo? MapInnerTypeToCeField(string innerTypeName)
    {
        return innerTypeName switch
        {
            "FloatProperty" => new CeFieldInfo("Float"),
            "DoubleProperty" => new CeFieldInfo("Double"),

            // Signed integers
            "Int8Property" => new CeFieldInfo("Byte", IsSigned: true),
            "Int16Property" => new CeFieldInfo("2 Bytes", IsSigned: true),
            "IntProperty" => new CeFieldInfo("4 Bytes", IsSigned: true),
            "Int64Property" => new CeFieldInfo("8 Bytes", IsSigned: true),

            // Unsigned integers
            "ByteProperty" => new CeFieldInfo("Byte"),
            "UInt16Property" => new CeFieldInfo("2 Bytes"),
            "UInt32Property" => new CeFieldInfo("4 Bytes"),
            "UInt64Property" => new CeFieldInfo("8 Bytes"),

            // Bool in arrays: stored as full bytes (no bitfield)
            "BoolProperty" => new CeFieldInfo("Byte"),

            // FName index
            "NameProperty" => new CeFieldInfo("4 Bytes"),

            // Enum -- underlying value is typically int32
            "EnumProperty" => new CeFieldInfo("4 Bytes"),

            // Phase D: pointer types — 8 bytes, shown as hex
            "ObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "ClassProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase E: weak object pointer — 8 bytes (ObjectIndex + SerialNumber)
            "WeakObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase G: TSoftObjectPtr / TSoftClassPtr — first 8 bytes is FWeakObjectPtr
            // (ObjectIndex + SerialNumber). Element stride uses ArrayElemSize so
            // consecutive elements remain aligned to TPersistentObjectPtr layout.
            "SoftObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "SoftClassProperty"  => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase H: TLazyObjectPtr — first 8 bytes is FWeakObjectPtr
            "LazyObjectProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase I: TScriptInterface — first 8 bytes is UObject*, show as pointer
            "InterfaceProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase J: FScriptDelegate — first 8 bytes is FWeakObjectPtr (target).
            // Element stride uses ArrayElemSize so consecutive elements stay aligned
            // (16 without CasePreservingName, 24 with).
            "DelegateProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            // Phase K: FMulticastScriptDelegate — first 8 bytes is the inner
            // TArray<FScriptDelegate>::Data pointer; element stride is 16.
            "MulticastDelegateProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),
            "MulticastInlineDelegateProperty" => new CeFieldInfo("8 Bytes", ShowAsHex: true),

            _ => null // Non-scalar (StructProperty, etc.)
        };
    }
}
