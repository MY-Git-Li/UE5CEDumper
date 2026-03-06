using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UE5DumpUI.Models;

namespace UE5DumpUI.Core;

/// <summary>
/// Converts user-entered string values to raw bytes for memory writes.
/// Performs range validation per UE property type. AOT-safe (no reflection).
/// </summary>
public static class FieldValueConverter
{
    /// <summary>
    /// Check if a property type supports inline editing.
    /// </summary>
    public static bool IsEditableType(string typeName) =>
        typeName is "FloatProperty" or "DoubleProperty"
            or "IntProperty" or "UInt32Property"
            or "Int64Property" or "UInt64Property"
            or "Int16Property" or "UInt16Property"
            or "ByteProperty" or "Int8Property"
            or "BoolProperty" or "EnumProperty";

    /// <summary>
    /// Try to convert a user-entered string to bytes for the given field type.
    /// </summary>
    /// <param name="typeName">UE property type name (e.g., "FloatProperty").</param>
    /// <param name="input">User-entered value string.</param>
    /// <param name="fieldSize">Field size in bytes (from DLL metadata).</param>
    /// <param name="enumEntries">Optional enum entries for EnumProperty validation.</param>
    /// <returns>Tuple of (success, data bytes, error message).</returns>
    public static (bool Success, byte[] Data, string Error) TryConvert(
        string typeName, string input, int fieldSize,
        IReadOnlyList<EnumEntryValue>? enumEntries = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (false, Array.Empty<byte>(), "Value cannot be empty");

        var trimmed = input.Trim();

        return typeName switch
        {
            "FloatProperty" => TryConvertFloat(trimmed),
            "DoubleProperty" => TryConvertDouble(trimmed),
            "IntProperty" => TryConvertInt32(trimmed),
            "UInt32Property" => TryConvertUInt32(trimmed),
            "Int64Property" => TryConvertInt64(trimmed),
            "UInt64Property" => TryConvertUInt64(trimmed),
            "Int16Property" => TryConvertInt16(trimmed),
            "UInt16Property" => TryConvertUInt16(trimmed),
            "ByteProperty" => TryConvertByte(trimmed, enumEntries, fieldSize),
            "Int8Property" => TryConvertSByte(trimmed),
            "EnumProperty" => TryConvertEnum(trimmed, enumEntries, fieldSize),
            _ => (false, Array.Empty<byte>(), $"Type '{typeName}' is not editable"),
        };
    }

    /// <summary>
    /// Apply a boolean value to a byte using a bitmask (read-modify-write pattern).
    /// </summary>
    public static byte ApplyBoolMask(byte currentByte, int fieldMask, bool newValue)
    {
        var mask = (byte)(fieldMask & 0xFF);
        return newValue
            ? (byte)(currentByte | mask)
            : (byte)(currentByte & ~mask);
    }

    /// <summary>
    /// Parse a boolean string value ("true", "false", "1", "0").
    /// </summary>
    public static bool TryParseBool(string input, out bool value)
    {
        var lower = input.Trim().ToLowerInvariant();
        if (lower is "true" or "1") { value = true; return true; }
        if (lower is "false" or "0") { value = false; return true; }
        value = false;
        return false;
    }

    // --- Private conversion methods ---

    private static (bool, byte[], string) TryConvertFloat(string input)
    {
        if (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid float: {input}");
        if (float.IsNaN(v))
            return (false, Array.Empty<byte>(), "NaN is not allowed");
        if (float.IsInfinity(v))
            return (false, Array.Empty<byte>(), "Infinity is not allowed");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertDouble(string input)
    {
        if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid double: {input}");
        if (double.IsNaN(v))
            return (false, Array.Empty<byte>(), "NaN is not allowed");
        if (double.IsInfinity(v))
            return (false, Array.Empty<byte>(), "Infinity is not allowed");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertInt32(string input)
    {
        if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid int32 (range: {int.MinValue} to {int.MaxValue})");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertUInt32(string input)
    {
        if (!uint.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid uint32 (range: 0 to {uint.MaxValue})");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertInt64(string input)
    {
        if (!long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), "Invalid int64 (value out of range)");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertUInt64(string input)
    {
        if (!ulong.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), "Invalid uint64 (value out of range)");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertInt16(string input)
    {
        if (!short.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid int16 (range: {short.MinValue} to {short.MaxValue})");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertUInt16(string input)
    {
        if (!ushort.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), $"Invalid uint16 (range: 0 to {ushort.MaxValue})");
        return (true, BitConverter.GetBytes(v), "");
    }

    private static (bool, byte[], string) TryConvertByte(string input,
        IReadOnlyList<EnumEntryValue>? enumEntries, int fieldSize)
    {
        // ByteProperty with enum entries: try enum name first
        if (enumEntries is { Count: > 0 })
            return TryConvertEnum(input, enumEntries, fieldSize > 0 ? fieldSize : 1);

        if (!byte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), "Invalid byte (range: 0 to 255)");
        return (true, new[] { v }, "");
    }

    private static (bool, byte[], string) TryConvertSByte(string input)
    {
        if (!sbyte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Array.Empty<byte>(), "Invalid int8 (range: -128 to 127)");
        return (true, new[] { (byte)v }, "");
    }

    private static (bool, byte[], string) TryConvertEnum(string input,
        IReadOnlyList<EnumEntryValue>? enumEntries, int fieldSize)
    {
        long rawValue;

        // Try matching by enum entry name first (case-insensitive)
        if (enumEntries is { Count: > 0 })
        {
            var match = enumEntries.FirstOrDefault(e =>
                string.Equals(e.Name, input, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                rawValue = match.Value;
            }
            else if (!long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out rawValue))
            {
                var validNames = string.Join(", ", enumEntries.Take(8).Select(e => e.Name));
                return (false, Array.Empty<byte>(),
                    $"Unknown enum value. Valid: {validNames}{(enumEntries.Count > 8 ? ", ..." : "")}");
            }
        }
        else
        {
            // No enum entries — raw int only
            if (!long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out rawValue))
                return (false, Array.Empty<byte>(), "Invalid enum value (expected integer)");
        }

        // Convert to bytes based on field size
        var size = fieldSize > 0 ? fieldSize : 1;
        byte[] data = size switch
        {
            1 => new[] { (byte)(rawValue & 0xFF) },
            2 => BitConverter.GetBytes((short)(rawValue & 0xFFFF)),
            4 => BitConverter.GetBytes((int)(rawValue & 0xFFFFFFFF)),
            8 => BitConverter.GetBytes(rawValue),
            _ => new[] { (byte)(rawValue & 0xFF) },
        };

        return (true, data, "");
    }
}
