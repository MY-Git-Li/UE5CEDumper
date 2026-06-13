using System.Text;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Generates C++ SDK offset headers from UClass/UStruct definitions.
/// Supports both LiveFieldValue (single-class from LiveWalker) and
/// ClassInfoModel (bulk from walk_class / static schema).
/// </summary>
public static class SdkExportService
{
    /// <summary>
    /// Generate a C++ header for a single class from LiveWalker field values.
    /// </summary>
    public static string GenerateClassHeader(
        string className, string superName, int propsSize,
        IReadOnlyList<LiveFieldValue> fields, string? fullPath = null)
    {
        var sb = new StringBuilder(fields.Count * 80 + 256);
        EmitFileHeader(sb);
        EmitClassHeaderFromLive(sb, className, superName, propsSize, fields, fullPath);
        return sb.ToString();
    }

    /// <summary>
    /// Generate a C++ header for a single class from static schema (ClassInfoModel).
    /// </summary>
    public static string GenerateClassHeaderFromSchema(ClassInfoModel classInfo)
    {
        var sb = new StringBuilder(classInfo.Fields.Count * 80 + 256);
        EmitFileHeader(sb);
        EmitClassHeaderFromSchema(sb, classInfo);
        return sb.ToString();
    }

    /// <summary>
    /// Default chunk size for the batched walk_class fan-out. Exposed
    /// internally so tests can hit the chunk-boundary edge cases at
    /// (chunkSize - 1) / chunkSize / (chunkSize + 1) class counts.
    /// </summary>
    internal const int FullSdkBatchChunkSize = 200;

    /// <summary>
    /// Bulk: generate headers for multiple classes (one big file).
    ///
    /// Walks classes in batches of <see cref="FullSdkBatchChunkSize"/>
    /// via <c>walk_class_batch</c> to collapse N pipe round-trips into
    /// N / chunkSize. The batch DLL command is a trivial loop over the
    /// same <c>Ubel::WalkClassEx</c> function the single-call path
    /// uses, so each batch element is byte-identical to a single
    /// <see cref="IDumpService.WalkClassAsync"/> response — output of
    /// this method is unchanged from the pre-batch implementation for
    /// any given input. If a batch call throws or returns the wrong
    /// number of results, the chunk is retried as N single calls so
    /// per-class error attribution (the <c>// ERROR: ...</c> line) is
    /// preserved.
    /// </summary>
    public static async Task<string> GenerateFullSdkAsync(
        IDumpService dump, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Collect all Class/ScriptStruct objects
        var targets = new List<(string addr, string name, string className)>();
        int offset = 0;
        const int pageSize = 5000;
        int total = 0;

        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await dump.GetObjectListAsync(offset, pageSize, ct);
            total = page.Total;

            foreach (var obj in page.Objects)
            {
                // Mirrors the DLL-side `Aura::IsClassLikeMeta` whitelist
                // (Class + BPGC variants + DynamicClass). A bare
                // `ClassName == "Class"` check silently drops every
                // BlueprintGeneratedClass — which is where 90%+ of
                // game-specific cheat targets live. Same bug fixed in
                // the DLL build 673; this is the C# mirror.
                if (DumpAllService.IsClassLikeMetaName(obj.ClassName) || obj.ClassName == "ScriptStruct")
                    targets.Add((obj.Address, obj.Name, obj.ClassName));
            }

            offset += page.Scanned > 0 ? page.Scanned : page.Objects.Count;
            progress?.Report($"Scanning objects... ({offset}/{total})");
        } while (offset < total);

        progress?.Report($"Walking {targets.Count} classes...");

        // 2. Walk each class to get field definitions, batched
        var sb = new StringBuilder(targets.Count * 512);
        EmitFileHeader(sb);
        sb.AppendLine("#pragma once");
        sb.AppendLine("#include <cstdint>");
        sb.AppendLine();

        int walked = 0;
        for (int chunkStart = 0; chunkStart < targets.Count; chunkStart += FullSdkBatchChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int chunkEnd = Math.Min(chunkStart + FullSdkBatchChunkSize, targets.Count);
            int chunkLen = chunkEnd - chunkStart;
            var chunkAddrs = new string[chunkLen];
            for (int i = 0; i < chunkLen; i++)
                chunkAddrs[i] = targets[chunkStart + i].addr;

            // Try the batched path first. We treat any failure (pipe
            // exception, unexpected element count) as a hint that the
            // chunk must be retried single-call to preserve per-class
            // error attribution. Cancellations always propagate.
            List<ClassInfoModel>? batchResult = null;
            try
            {
                var fetched = await dump.WalkClassesBatchAsync(chunkAddrs, ct);
                if (fetched.Count == chunkLen)
                    batchResult = fetched;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // batchResult stays null → fallback below.
            }

            if (batchResult != null)
            {
                for (int i = 0; i < chunkLen; i++)
                {
                    EmitClassHeaderFromSchema(sb, batchResult[i]);
                    sb.AppendLine();
                    walked++;
                    if (walked % 50 == 0)
                        progress?.Report($"Walking classes... ({walked}/{targets.Count})");
                }
            }
            else
            {
                // Per-class fallback — same shape as the pre-batch path.
                for (int i = 0; i < chunkLen; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (addr, name, _) = targets[chunkStart + i];
                    try
                    {
                        var classInfo = await dump.WalkClassAsync(addr, ct);
                        EmitClassHeaderFromSchema(sb, classInfo);
                        sb.AppendLine();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        sb.AppendLine($"// ERROR: Failed to walk {name} at {addr}");
                        sb.AppendLine();
                    }
                    walked++;
                    if (walked % 50 == 0)
                        progress?.Report($"Walking classes... ({walked}/{targets.Count})");
                }
            }
        }

        progress?.Report($"Generated SDK with {walked} classes");
        return sb.ToString();
    }

    // --- Type Mapping ---

    /// <summary>
    /// Map a UE property type name to a C++ type string, using FieldInfoModel metadata.
    /// </summary>
    internal static string MapCppType(FieldInfoModel field)
    {
        return MapCppTypeCore(
            field.TypeName, field.StructType, field.ObjClassName,
            field.InnerType, field.InnerStructType, field.InnerObjClass,
            field.KeyType, field.KeyStructType, field.ValueType, field.ValueStructType,
            field.ElemType, field.ElemStructType, field.EnumName,
            field.BoolFieldMask, field.Size);
    }

    /// <summary>
    /// Map a UE property type name to a C++ type string, using LiveFieldValue metadata.
    /// </summary>
    internal static string MapCppType(LiveFieldValue field)
    {
        return MapCppTypeCore(
            field.TypeName, field.StructTypeName, field.PtrClassName,
            field.ArrayInnerType, field.ArrayStructType, "",
            field.MapKeyType, field.MapKeyStructType, field.MapValueType, field.MapValueStructType,
            field.SetElemType, field.SetElemStructType, field.EnumName,
            field.BoolFieldMask, field.Size);
    }

    private static string MapCppTypeCore(
        string typeName, string structType, string objClassName,
        string innerType, string innerStructType, string innerObjClass,
        string keyType, string keyStructType, string valueType, string valueStructType,
        string elemType, string elemStructType, string enumName,
        int boolFieldMask, int size)
    {
        return typeName switch
        {
            "IntProperty" => "int32_t",
            "Int8Property" => "int8_t",
            "Int16Property" => "int16_t",
            "Int64Property" => "int64_t",
            "UInt16Property" => "uint16_t",
            "UInt32Property" => "uint32_t",
            "UInt64Property" => "uint64_t",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "BoolProperty" => "bool",
            "NameProperty" => "FName",
            "StrProperty" => "FString",
            "TextProperty" => "FText",

            "ObjectProperty" => FormatPtrType("class", objClassName, "UObject"),
            "ClassProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TSubclassOf<class {objClassName}>"
                : "UClass*",
            "WeakObjectProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TWeakObjectPtr<class {objClassName}>"
                : "TWeakObjectPtr<UObject>",
            "SoftObjectProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TSoftObjectPtr<class {objClassName}>"
                : "TSoftObjectPtr<UObject>",
            "SoftClassProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TSoftClassPtr<class {objClassName}>"
                : "TSoftClassPtr<UObject>",
            "InterfaceProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TScriptInterface<class {objClassName}>"
                : "TScriptInterface<IInterface>",
            "LazyObjectProperty" => !string.IsNullOrEmpty(objClassName)
                ? $"TLazyObjectPtr<class {objClassName}>"
                : "TLazyObjectPtr<UObject>",

            "StructProperty" => !string.IsNullOrEmpty(structType)
                ? $"struct {structType}"
                : $"uint8_t[0x{size:X}]",

            "ArrayProperty" => $"TArray<{MapInnerCppType(innerType, innerStructType, innerObjClass)}>",
            "MapProperty" => $"TMap<{MapInnerCppType(keyType, keyStructType, "")}, {MapInnerCppType(valueType, valueStructType, "")}>",
            "SetProperty" => $"TSet<{MapInnerCppType(elemType, elemStructType, "")}>",

            "EnumProperty" => !string.IsNullOrEmpty(enumName) ? enumName : "uint8_t",
            "ByteProperty" => !string.IsNullOrEmpty(enumName) ? enumName : "uint8_t",

            "DelegateProperty" => "FScriptDelegate",
            "MulticastDelegateProperty" => "FMulticastScriptDelegate",
            "MulticastInlineDelegateProperty" => "FMulticastInlineDelegate",
            "MulticastSparseDelegateProperty" => "FMulticastSparseDelegate",
            "FieldPathProperty" => "FFieldPath",

            _ => $"uint8_t[0x{size:X}]", // unknown type → raw bytes
        };
    }

    private static string FormatPtrType(string prefix, string className, string fallback)
    {
        var name = !string.IsNullOrEmpty(className) ? className : fallback;
        return $"{prefix} {name}*";
    }

    private static string MapInnerCppType(string innerType, string structType, string objClass)
    {
        if (string.IsNullOrEmpty(innerType)) return "uint8_t";

        return innerType switch
        {
            "StructProperty" => !string.IsNullOrEmpty(structType) ? $"struct {structType}" : "uint8_t",
            "ObjectProperty" => FormatPtrType("class", objClass, "UObject"),
            "ClassProperty" => !string.IsNullOrEmpty(objClass) ? $"TSubclassOf<class {objClass}>" : "UClass*",
            "WeakObjectProperty" => !string.IsNullOrEmpty(objClass) ? $"TWeakObjectPtr<class {objClass}>" : "TWeakObjectPtr<UObject>",
            "SoftObjectProperty" => !string.IsNullOrEmpty(objClass) ? $"TSoftObjectPtr<class {objClass}>" : "TSoftObjectPtr<UObject>",
            "InterfaceProperty" => !string.IsNullOrEmpty(objClass) ? $"TScriptInterface<class {objClass}>" : "TScriptInterface<IInterface>",
            _ => MapScalarInnerType(innerType),
        };
    }

    private static string MapScalarInnerType(string innerType)
    {
        return innerType switch
        {
            "IntProperty" => "int32_t",
            "Int8Property" => "int8_t",
            "Int16Property" => "int16_t",
            "Int64Property" => "int64_t",
            "UInt16Property" => "uint16_t",
            "UInt32Property" => "uint32_t",
            "UInt64Property" => "uint64_t",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "BoolProperty" => "bool",
            "ByteProperty" => "uint8_t",
            "NameProperty" => "FName",
            "StrProperty" => "FString",
            "TextProperty" => "FText",
            "EnumProperty" => "uint8_t",
            _ => "uint8_t",
        };
    }

    // --- Header Emission ---

    private static void EmitFileHeader(StringBuilder sb)
    {
        sb.AppendLine("// Auto-generated by UE5CEDumper");
        sb.AppendLine("// https://github.com/bbfox0703/UE5CEDumper");
        sb.AppendLine();
    }

    private static void EmitClassHeaderFromSchema(StringBuilder sb, ClassInfoModel classInfo)
    {
        var className = classInfo.Name;
        var superName = classInfo.SuperName;
        var propsSize = classInfo.PropertiesSize;
        var fullPath = classInfo.FullPath;

        // Class header comment
        sb.Append("// ");
        sb.Append(!string.IsNullOrEmpty(fullPath) ? fullPath : className);
        sb.AppendLine();

        // Struct declaration
        sb.Append("struct ");
        sb.Append(className);
        if (!string.IsNullOrEmpty(superName))
        {
            sb.Append(" : public ");
            sb.Append(superName);
        }
        sb.AppendLine();
        sb.AppendLine("{");

        // Sort fields by offset
        var sorted = classInfo.Fields.OrderBy(f => f.Offset).ToList();
        int cursor = 0;

        // If we have a super class, skip fields below the superclass boundary.
        // Heuristic: first field offset is the superclass size.
        if (sorted.Count > 0 && !string.IsNullOrEmpty(superName))
        {
            cursor = sorted[0].Offset;
        }

        foreach (var field in sorted)
        {
            // Emit padding if gap
            if (field.Offset > cursor)
            {
                var pad = field.Offset - cursor;
                EmitPadding(sb, cursor, pad);
            }

            // Field declaration
            var cppType = MapCppType(field);
            var comment = BuildFieldComment(field.Offset, field.Size, field.TypeName, field.BoolFieldMask);

            sb.Append("    ");
            sb.Append(cppType);
            sb.Append(' ');
            sb.Append(field.Name);
            sb.Append(';');
            sb.Append(comment);
            sb.AppendLine();

            cursor = field.Offset + field.Size;
        }

        // Tail padding to reach PropertiesSize
        if (propsSize > cursor && propsSize > 0)
        {
            EmitPadding(sb, cursor, propsSize - cursor);
        }

        sb.Append("}; // Size: 0x");
        sb.AppendLine(propsSize.ToString("X4"));
    }

    private static void EmitClassHeaderFromLive(
        StringBuilder sb, string className, string superName, int propsSize,
        IReadOnlyList<LiveFieldValue> fields, string? fullPath)
    {
        sb.Append("// ");
        sb.Append(!string.IsNullOrEmpty(fullPath) ? fullPath : className);
        sb.AppendLine();

        sb.Append("struct ");
        sb.Append(className);
        if (!string.IsNullOrEmpty(superName))
        {
            sb.Append(" : public ");
            sb.Append(superName);
        }
        sb.AppendLine();
        sb.AppendLine("{");

        var sorted = fields.OrderBy(f => f.Offset).ToList();
        int cursor = 0;

        if (sorted.Count > 0 && !string.IsNullOrEmpty(superName))
        {
            cursor = sorted[0].Offset;
        }

        foreach (var field in sorted)
        {
            if (field.Offset > cursor)
            {
                EmitPadding(sb, cursor, field.Offset - cursor);
            }

            var cppType = MapCppType(field);
            var comment = BuildFieldComment(field.Offset, field.Size, field.TypeName, field.BoolFieldMask);

            sb.Append("    ");
            sb.Append(cppType);
            sb.Append(' ');
            sb.Append(field.Name);
            sb.Append(';');
            sb.Append(comment);
            sb.AppendLine();

            cursor = field.Offset + field.Size;
        }

        if (propsSize > cursor && propsSize > 0)
        {
            EmitPadding(sb, cursor, propsSize - cursor);
        }

        sb.Append("}; // Size: 0x");
        sb.AppendLine(propsSize.ToString("X4"));
    }

    private static void EmitPadding(StringBuilder sb, int offset, int size)
    {
        sb.Append("    uint8_t Pad_");
        sb.Append(offset.ToString("X4"));
        sb.Append("[0x");
        sb.Append(size.ToString("X4"));
        sb.Append("];");
        sb.Append(BuildFieldComment(offset, size, "PADDING", 0));
        sb.AppendLine();
    }

    private static string BuildFieldComment(int offset, int size, string typeName, int boolMask)
    {
        var sb = new StringBuilder(60);
        sb.Append(" // 0x");
        sb.Append(offset.ToString("X4"));
        sb.Append(" (0x");
        sb.Append(size.ToString("X4"));
        sb.Append(") ");
        sb.Append(typeName);
        if (boolMask > 0)
        {
            sb.Append(" [Mask: 0x");
            sb.Append(boolMask.ToString("X2"));
            sb.Append(']');
        }
        return sb.ToString();
    }

    // --- Enum and Function Generation (Phase 4) ---

    /// <summary>
    /// Generate a C++ enum class definition from an EnumDefinition.
    /// </summary>
    public static string GenerateEnumDefinition(EnumDefinition enumDef, string? underlyingType = null)
    {
        var sb = new StringBuilder(enumDef.Entries.Count * 40 + 100);
        sb.Append("enum class ");
        sb.Append(enumDef.Name);
        sb.Append(" : ");
        // When the caller does not pin the underlying type, infer the minimal width
        // from the entry values rather than always emitting uint8_t. Mirrors Dumper-7's
        // fix for "uint64 enums being uint8": a value > 255 truncated to uint8_t would
        // not even compile. (Inference can still under-size a wide enum whose values all
        // happen to fit a narrower type, but it never produces a non-compiling header.)
        sb.AppendLine(underlyingType ?? InferEnumUnderlyingType(enumDef.Entries));
        sb.AppendLine("{");

        foreach (var entry in enumDef.Entries)
        {
            sb.Append("    ");
            sb.Append(entry.Name);
            sb.Append(" = ");
            sb.Append(entry.Value);
            sb.AppendLine(",");
        }

        sb.AppendLine("};");
        return sb.ToString();
    }

    /// <summary>
    /// Pick the narrowest fixed-width C++ integer type that can represent every enum
    /// entry value. Negative values select a signed type; otherwise unsigned. Defaults
    /// to uint8_t for an empty set (UE's typical underlying type).
    /// </summary>
    internal static string InferEnumUnderlyingType(List<EnumEntryValue> entries)
    {
        if (entries.Count == 0) return "uint8_t";

        long min = long.MaxValue, max = long.MinValue;
        foreach (var e in entries)
        {
            if (e.Value < min) min = e.Value;
            if (e.Value > max) max = e.Value;
        }

        if (min < 0)
        {
            if (min >= sbyte.MinValue && max <= sbyte.MaxValue) return "int8_t";
            if (min >= short.MinValue && max <= short.MaxValue) return "int16_t";
            if (min >= int.MinValue && max <= int.MaxValue) return "int32_t";
            return "int64_t";
        }

        if (max <= byte.MaxValue) return "uint8_t";
        if (max <= ushort.MaxValue) return "uint16_t";
        if (max <= uint.MaxValue) return "uint32_t";
        return "uint64_t";
    }

    /// <summary>
    /// Generate a C++ function signature comment from a FunctionInfoModel.
    /// </summary>
    public static string GenerateFunctionSignature(FunctionInfoModel func)
    {
        var sb = new StringBuilder(func.Params.Count * 30 + 80);

        // Return type
        var retType = "void";
        if (!string.IsNullOrEmpty(func.ReturnType))
            retType = MapFunctionParamType(func.ReturnType);

        sb.Append("    ");
        sb.Append(retType);
        sb.Append(' ');
        sb.Append(func.Name);
        sb.Append('(');

        // Parameters (exclude return param)
        bool first = true;
        foreach (var p in func.Params)
        {
            if (p.IsReturn) continue;
            if (!first) sb.Append(", ");
            first = false;

            var pType = MapFunctionParamType(p.TypeName);
            if (p.IsOut) sb.Append("/* out */ ");
            sb.Append(pType);
            sb.Append(' ');
            sb.Append(p.Name);
        }

        sb.Append(");");

        // Address comment
        if (!string.IsNullOrEmpty(func.Address))
        {
            sb.Append(" // ");
            sb.Append(func.Address);
        }

        return sb.ToString();
    }

    private static string MapFunctionParamType(string typeName)
    {
        return typeName switch
        {
            "IntProperty" => "int32_t",
            "Int8Property" => "int8_t",
            "Int16Property" => "int16_t",
            "Int64Property" => "int64_t",
            "UInt16Property" => "uint16_t",
            "UInt32Property" => "uint32_t",
            "UInt64Property" => "uint64_t",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "BoolProperty" => "bool",
            "ByteProperty" => "uint8_t",
            "NameProperty" => "FName",
            "StrProperty" => "FString",
            "TextProperty" => "FText",
            "ObjectProperty" => "UObject*",
            "ClassProperty" => "UClass*",
            "StructProperty" => "void*",
            "ArrayProperty" => "TArray<uint8_t>",
            _ => "void*",
        };
    }
}
